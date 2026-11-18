using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Surface;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Root of a Looga stamp set. The heights of the listed terrains come only from the child height modifiers
    /// (height stamps and spline paths), in hierarchy order, starting from height 0.
    /// </summary>
    /// <remarks>
    /// A full rebuild evaluates every terrain. A region update evaluates only the terrains that a world rectangle
    /// touches. Edges against terrains that are not evaluated again take the stored heights of those terrains.
    /// Spline distance fields stay cached between updates. The owner of a changed spline must be invalidated.
    /// Texture modifiers paint the alphamaps the same way. The world keeps the final layer and weight maps of each
    /// terrain for the edges of texture region updates.
    /// Placement modifiers (clear, object and detail stamps) place prefabs into an instance set that the world owns and
    /// paint the terrain detail layers. Path clears on spline paths remove placements near the paths.
    /// Looga terrain renderers and vegetation providers on the owned terrains follow height and alphamap writes through
    /// the Unity terrain callbacks. The world refreshes them after a layer list change and after detail writes.
    /// </remarks>
    [AddComponentMenu("Looga/Stamps/Stamp World")]
    [DisallowMultipleComponent]
    // OnDisable must also run in the editor, so that a domain reload or a closed scene releases the GPU caches.
    [ExecuteAlways]
    public sealed class LoogaStampWorld : MonoBehaviour
    {
        // Enabled worlds, in enable order. The editor updater reads this list on each tick without garbage.
        internal static readonly List<LoogaStampWorld> Enabled = new();

        /// <summary>The enabled stamp worlds, in enable order. Reading the list makes no garbage.</summary>
        public static IReadOnlyList<LoogaStampWorld> EnabledWorlds => Enabled;
        // Grows with each revision of a stamp object (modifiers, areas, water sources) and each changed region of a
        // texture source. The editor updater compares all modifiers with their last state only after a change.
        internal static int RevisionChanges;

        [Header("Terrain")]
        [Tooltip("Terrains that this stamp set owns. Their heightmaps are replaced on a rebuild.")]
        [SerializeField] private UnityEngine.Terrain[] _terrains = Array.Empty<UnityEngine.Terrain>();

        [Header("Evaluation")]
        [Tooltip("MicroVerse Parity reproduces MicroVerse 1.7 heights. World Aligned samples at true positions.")]
        [SerializeField] private HeightMapping _mapping = HeightMapping.MicroVerseParity;
        [Tooltip("Extra samples around each tile, for filters that read neighbor heights. World Aligned only.")]
        [SerializeField, Range(0, 64)] private int _halo = 32;

        [Header("Editor")]
        [Tooltip("Update the terrains in the editor when a stamp, spline or painted area changes.")]
        [SerializeField] private bool _updateInEditor;
        [Tooltip("GPU memory for cached spline distance fields, in MB.")]
        [SerializeField, Min(64)] private int _distanceFieldBudget = 1024;
        [Tooltip("GPU memory for cached water fields, in MB.")]
        [SerializeField, Min(64)] private int _waterFieldBudget = 512;

        [Header("Integrations")]
        [Tooltip("Optional asset that an integration keeps in the terrain layer order of this world, for example a " +
                 "MicroSplat texture array config. The world does not read it.")]
        [SerializeField] private UnityEngine.Object _layerSyncTarget;

        [Header("Placement")]
        [Tooltip("Holds the object stamp placements. The world creates it as a child on the first write. Detail " +
                 "stamps write the terrain detail layers.")]
        [SerializeField] private InstanceSet _placementSet;
        [Tooltip("Optional area for collider copies. Object placements outside the area and its margin get no " +
                 "colliders, for example placements far from the playable area. Empty gives all placements colliders. " +
                 "A change applies at the next placement write.")]
        [SerializeField] private LoogaSplineArea _colliderArea;
        [Tooltip("Distance in metres outside the collider area in which placements still get colliders.")]
        [SerializeField, Min(0f)] private float _colliderAreaMargin = 100f;
        // Detail layers that the detail stamps painted at the last placement write. The list is saved with the scene.
        // A full write after a script reload or a scene load has no stored placements, so the list tells it which
        // layers to clear when their stamps are gone. A layer that no stamp painted, for example a hand-painted
        // layer, is never in the list.
        [SerializeField, HideInInspector] private List<int> _paintedDetailLayers = new();

        private HeightStampEvaluator _evaluator;
        private TextureStampEvaluator _textureEvaluator;
        private PlacementEvaluator _placementEvaluator;
        private TerrainFieldMaps _fieldMaps;
        private PlacementFieldMaps _placementFieldMaps;
        // Tiles whose evaluation could not read prefab mesh vertices, and the time of the last refused read. They
        // update again later (TakeRefusedReadRetry).
        private Rect? _refusedReadArea;
        private double _refusedReadTime;
        private const double RefusedReadDelay = 2.0;
        private SurfaceFieldMaps _surfaceFields;
        // A placement update that places more terrains or waits for GPU readbacks.
        private PlacementUpdate _pendingPlacements;
        // Terrains that an update without waits places in one call. A whole-world update then spreads over several
        // editor ticks, so the GPU keeps up and the editor stays responsive. Most edits touch fewer terrains.
        private const int PlacementTilesPerCall = 4;
        private readonly Dictionary<UnityEngine.Terrain, RenderTexture> _outputs = new();
        private readonly HashSet<UnityEngine.Terrain> _unsynchronized = new();
        // Layer and weight maps of the last texture write, for the edges of region updates.
        private readonly Dictionary<UnityEngine.Terrain, TextureStampEvaluator.TileLayers> _textureLayers = new();
        private readonly HashSet<UnityEngine.Terrain> _texturesUnsynchronized = new();
        // True after a full texture rebuild in this scene load. A script reload keeps the value; the scene file does not.
        private bool _texturesRebuiltSinceLoad;
        // Placements and detail layers of the last placement write, for each terrain. Region updates replace the
        // terrains that they place again.
        private StampPlacements _storedPlacements;
        // Positions that get collider copies, from the collider area. One filter object, so the set sees no change.
        private readonly ColliderAreaMask _colliderMask = new();
        private Func<Vector3, bool> _colliderFilter;
        // Height writes of UpdateRegionWithoutWait whose check of the samples outside the copied area is on the GPU.
        private readonly List<PendingHeights> _pendingHeights = new();
        // Modifiers of IsSnapshotReady, used again for each check. The rock face queue checks it for each build.
        private readonly List<LoogaHeightModifier> _snapshotModifiers = new();
        private long _peakTileLayerBytes;
        // Direction of the next tile sweep. See NextSweep.
        private bool _sweepBackward;
        // An update that a tool requested. All = the whole world.
        private bool _updateRequested;
        private bool _requestedAll;
        private Rect _requestedRegion;

        public IReadOnlyList<UnityEngine.Terrain> Terrains => _terrains;
        public HeightMapping Mapping => _mapping;
        public int Halo => _halo;
        public bool UpdateInEditor => _updateInEditor;
        public UnityEngine.Object LayerSyncTarget => _layerSyncTarget;

        /// <summary>True while changes wait for a region update. The editor updater sets it.</summary>
        /// <remarks>
        /// Not kept over a script reload: the reload kept it while the updater lost the work, and the world stayed
        /// "updating" with nothing scheduled.
        /// </remarks>
        [field: NonSerialized]
        public bool IsUpdating { get; internal set; }

        /// <summary>True while an update that <see cref="RequestUpdate"/> requested has not run.</summary>
        public bool HasRequestedUpdate => _updateRequested;

        /// <summary>True when the CPU heightmaps of all written terrains are synchronized.</summary>
        public bool HeightsSynchronized => _unsynchronized.Count == 0 && _pendingHeights.Count == 0;

        /// <summary>
        /// True while GPU checks of height writes without waits are open. They finish when the GPU has done the height
        /// work, so work that would wait for the GPU can start after them without a long wait.
        /// </summary>
        public bool HeightChecksPending => _pendingHeights.Count > 0;

        // Status displays: placed and total terrains of the pending placement update ((0, 0) without one), and the
        // terrains whose CPU heights wait for synchronization.
        internal Vector2Int PlacementProgress => _pendingPlacements == null ? Vector2Int.zero :
            new Vector2Int(_pendingPlacements.Run?.PlacedTiles ?? _pendingPlacements.TileCount,
                _pendingPlacements.TileCount);
        internal int UnsynchronizedHeightCount => _unsynchronized.Count;

        /// <summary>Raised after new heights are written to the terrains. The rectangle is the updated world XZ area.</summary>
        public event Action<Rect> HeightsUpdated;

        /// <summary>True when the CPU alphamaps of all written terrains are synchronized.</summary>
        public bool TexturesSynchronized => _texturesUnsynchronized.Count == 0;

        /// <summary>Raised after new alphamaps are written. The rectangle is the updated world XZ area.</summary>
        public event Action<Rect> TexturesUpdated;

        /// <summary>
        /// Raised after placements and detail layers are written: the instance set and the CPU detail layers are
        /// current. The rectangle is the updated world XZ area.
        /// </summary>
        public event Action<Rect> PlacementsUpdated;

        /// <summary>
        /// Raised by the editor updater at the start of each tick, before it compares the modifiers with their last
        /// state. A script that moves modifiers because of another edit, for example road knots that follow a pad,
        /// moves them here, so that one update applies both edits.
        /// </summary>
        public event Action BeforeChangeCapture;

        internal void RaiseBeforeChangeCapture()
        {
            BeforeChangeCapture?.Invoke();
        }

        /// <summary>
        /// Raised by the editor updater once after the changes of an edit are complete: the heights are written and
        /// synchronized to the CPU, and the textures and placements are written. The rectangle is the union of the
        /// updated world XZ areas. Generators that read the CPU heights or details, for example meshes on the ground,
        /// update here.
        /// </summary>
        public event Action<Rect> UpdateSettled;

        // Union of the areas updated since the last UpdateSettled.
        private Rect? _settleRegion;

        /// <summary>Adds an updated area to the next <see cref="UpdateSettled"/>. The editor updater calls it.</summary>
        internal void NoteUpdated(Rect region)
        {
            _settleRegion = _settleRegion.HasValue
                ? Rect.MinMaxRect(Mathf.Min(_settleRegion.Value.xMin, region.xMin),
                    Mathf.Min(_settleRegion.Value.yMin, region.yMin), Mathf.Max(_settleRegion.Value.xMax, region.xMax),
                    Mathf.Max(_settleRegion.Value.yMax, region.yMax))
                : region;
        }

        /// <summary>
        /// Raises <see cref="UpdateSettled"/> when an updated area waits and nothing of the update is open. Returns true
        /// when it raised the event.
        /// </summary>
        internal bool SettleIfComplete()
        {
            if (!_settleRegion.HasValue || IsUpdating || HasRequestedUpdate || !HeightsSynchronized ||
                PlacementsPending) return false;
            Rect region = _settleRegion.Value;
            _settleRegion = null;
            // The spline distance build textures are used only while fields build (256 MB at 2048).
            _evaluator?.DistanceFields.ReleaseScratch();
            UpdateSettled?.Invoke(region);
            return true;
        }

        /// <summary>
        /// Largest temporary GPU memory of one placed terrain since the last reset, in bytes: placement textures and the
        /// layer maps of the texture filter. Cached distance fields are not included.
        /// </summary>
        public long PlacementPeakBytes => (_placementEvaluator?.PeakTemporaryBytes ?? 0) + _peakTileLayerBytes;

        /// <summary>GPU memory of the stored layer and weight maps, in bytes.</summary>
        public long StoredTextureLayerBytes
        {
            get
            {
                long bytes = 0;
                foreach (TextureStampEvaluator.TileLayers tile in _textureLayers.Values)
                {
                    if (tile.Layers) bytes += 2L * tile.Layers.width * tile.Layers.height * 4;
                }
                return bytes;
            }
        }

        /// <summary>GPU memory of the cached spline distance fields, in bytes.</summary>
        public long CachedDistanceFieldBytes => _evaluator?.DistanceFields.CachedBytes ?? 0;

        /// <summary>Distance fields built and taken from the cache since the statistics were reset.</summary>
        public Vector2Int DistanceFieldStatistics => _evaluator == null ? Vector2Int.zero :
            new Vector2Int(_evaluator.DistanceFields.BuildCount, _evaluator.DistanceFields.CacheHits);

        /// <summary>GPU memory of the cached water fields, in bytes.</summary>
        public long CachedWaterFieldBytes => _evaluator?.WaterFields.CachedBytes ?? 0;

        /// <summary>Water fields built and taken from the cache since the statistics were reset.</summary>
        public Vector2Int WaterFieldStatistics => _evaluator == null ? Vector2Int.zero :
            new Vector2Int(_evaluator.WaterFields.BuildCount, _evaluator.WaterFields.CacheHits);

        private void OnEnable()
        {
            Enabled.Add(this);
            TerrainCallbacks.heightmapChanged += OnHeightmapChanged;
            // The set makes its collider copies when it is enabled. The filter is not saved, so set it again.
            if (_placementSet)
            {
                ApplyColliderArea(_placementSet);
            }
            TakeTextureLayersAfterReload();
            TakePlacementsAfterReload();
        }

        private void OnDisable()
        {
            Enabled.Remove(this);
            TerrainCallbacks.heightmapChanged -= OnHeightmapChanged;
            if (Geology.GeneratedReloadHandover.Unloading)
            {
                StoreTextureLayersForReload();
                StorePlacementsForReload();
            }
            else
            {
                SynchronizeBeforeClose();
            }
            ReleaseResources();
        }

        #region Reload
        // The layer and weight maps are render textures, and the editor keeps them over a script reload. The next domain
        // takes them back, so the first texture edit after the reload updates only its region. Without the maps, the
        // first texture edit after each reload rebuilt all terrains (Kubera World: frames of 37 s and 24 s).
        [Serializable]
        private sealed class KeptTextureLayers
        {
            public string Version;
            public int[] Terrains;
            public int[] Layers;
            public int[] Weights;
        }

        private string TextureLayersKey => "LoogaSoft.Terrain.Stamps.TextureLayers." + GetInstanceID();

        // The maps go to the next domain. ReleaseResources then does not release them.
        private void StoreTextureLayersForReload()
        {
            var kept = new List<TextureStampEvaluator.TileLayers>();
            foreach (KeyValuePair<UnityEngine.Terrain, TextureStampEvaluator.TileLayers> pair in _textureLayers)
            {
                if (pair.Key && pair.Value.Layers && pair.Value.Weights)
                {
                    kept.Add(pair.Value);
                }
                else
                {
                    pair.Value.Dispose();
                }
            }
            _textureLayers.Clear();
            if (kept.Count == 0) return;

            Geology.GeneratedReloadHandover.Store(TextureLayersKey, new KeptTextureLayers
            {
                Version = Geology.GeneratedReloadHandover.StampCodeVersion,
                Terrains = kept.ConvertAll(tile => tile.Terrain.GetInstanceID()).ToArray(),
                Layers = kept.ConvertAll(tile => tile.Layers.GetInstanceID()).ToArray(),
                Weights = kept.ConvertAll(tile => tile.Weights.GetInstanceID()).ToArray()
            });
        }

        // Takes the maps of the last domain. A change of the stamp code releases them, so the next texture edit
        // rebuilds all terrains with the new code.
        private void TakeTextureLayersAfterReload()
        {
            if (!Geology.GeneratedReloadHandover.TryTake(TextureLayersKey, out KeptTextureLayers kept) ||
                kept.Terrains == null || kept.Layers == null || kept.Weights == null) return;

            bool valid = kept.Version == Geology.GeneratedReloadHandover.StampCodeVersion;
            int count = Mathf.Min(kept.Terrains.Length, Mathf.Min(kept.Layers.Length, kept.Weights.Length));
            for (int i = 0; i < count; i++)
            {
                var terrain = Geology.GeneratedReloadHandover.Find<UnityEngine.Terrain>(kept.Terrains[i]);
                var layers = Geology.GeneratedReloadHandover.Find<RenderTexture>(kept.Layers[i]);
                var weights = Geology.GeneratedReloadHandover.Find<RenderTexture>(kept.Weights[i]);
                if (valid && terrain && layers && weights && layers.IsCreated() && weights.IsCreated() &&
                    Array.IndexOf(_terrains, terrain) >= 0)
                {
                    _textureLayers[terrain] = new TextureStampEvaluator.TileLayers
                    {
                        Terrain = terrain, Layers = layers, Weights = weights
                    };
                    continue;
                }
                if (layers)
                {
                    RenderTexture.ReleaseTemporary(layers);
                }
                if (weights)
                {
                    RenderTexture.ReleaseTemporary(weights);
                }
            }
        }

        // The stored placements are managed data, so a script reload loses them. Without them, the first placement
        // update after each reload placed all terrains again in one frame (Kubera World: 22 s). The result goes to a
        // file in the Library folder (about 80 MB) and the next domain reads it.
        [Serializable]
        private sealed class KeptPlacements
        {
            public string Version;
            public string Path;
        }

        private string PlacementsKey => "LoogaSoft.Terrain.Stamps.Placements." + GetInstanceID();

        private void StorePlacementsForReload()
        {
#if UNITY_EDITOR
            if (_storedPlacements == null || _pendingPlacements != null) return;

            string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "Library",
                "LoogaStampWorld", GetInstanceID() + ".placements");
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                bool written;
                using (var writer = new System.IO.BinaryWriter(new System.IO.BufferedStream(
                           System.IO.File.Create(path), 1 << 20)))
                {
                    written = _storedPlacements.Write(writer, InstanceId);
                }
                if (!written) return;
                Geology.GeneratedReloadHandover.Store(PlacementsKey, new KeptPlacements
                {
                    Version = Geology.GeneratedReloadHandover.StampCodeVersion, Path = path
                });
            }
            catch (System.IO.IOException exception)
            {
                Debug.LogWarning("Looga Stamps: the placements could not be kept over the script reload (" +
                    exception.Message + "). The first placement update places all terrains again.", this);
            }
#endif
        }

        // A script reload keeps the instance IDs of the scene objects.
        private static int InstanceId(UnityEngine.Object value)
        {
            return value.GetInstanceID();
        }

        private void TakePlacementsAfterReload()
        {
#if UNITY_EDITOR
            if (!Geology.GeneratedReloadHandover.TryTake(PlacementsKey, out KeptPlacements kept) ||
                string.IsNullOrEmpty(kept.Path) || !System.IO.File.Exists(kept.Path)) return;
            try
            {
                if (kept.Version == Geology.GeneratedReloadHandover.StampCodeVersion)
                {
                    using var reader = new System.IO.BinaryReader(new System.IO.BufferedStream(
                        System.IO.File.OpenRead(kept.Path), 1 << 20));
                    _storedPlacements = StampPlacements.Read(reader,
                        id => UnityEditor.EditorUtility.EntityIdToObject(id));
                }
            }
            catch (System.IO.IOException)
            {
                // A short or unreadable file: the first placement update places all terrains again.
                _storedPlacements = null;
            }
            finally
            {
                System.IO.File.Delete(kept.Path);
            }
#endif
        }
        #endregion

        // The editor updater steps the maps in edit mode.
        private void Update()
        {
            if (Application.isPlaying)
            {
                StepSurfaceFields();
            }
        }

        // Every height write reports here, also a terrain brush or an undo outside the world.
        private void OnHeightmapChanged(UnityEngine.Terrain terrain, RectInt region, bool synchronized)
        {
            if (_surfaceFields == null || Array.IndexOf(_terrains, terrain) < 0) return;
            Rect changed = SampleRect(new StampTile(terrain), region);
            _fieldMaps?.InvalidateFlow(changed);
            _surfaceFields.MarkChanged(changed);
        }

        /// <summary>Sets the owned terrains. Their order is the tile order of seam averaging.</summary>
        public void SetTerrains(UnityEngine.Terrain[] terrains)
        {
            _terrains = terrains ?? Array.Empty<UnityEngine.Terrain>();
            _storedPlacements = null;
            ReleaseResources();
        }

        public void SetUpdateInEditor(bool update)
        {
            _updateInEditor = update;
        }

        /// <summary>
        /// Requests an update of heights, textures and placements in a world XZ rectangle, or of all terrains when the
        /// region is null. Tools use it after changes that the editor updater does not compare. The editor updater runs
        /// the update in its next tick when Update In Editor is on.
        /// </summary>
        public void RequestUpdate(Rect? region = null)
        {
            if (!region.HasValue || _requestedAll)
            {
                _requestedAll = true;
            }
            else if (_updateRequested)
            {
                Rect a = _requestedRegion, b = region.Value;
                _requestedRegion = Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
                    Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
            }
            else
            {
                _requestedRegion = region.Value;
            }
            _updateRequested = true;
        }

        /// <summary>Takes the requested update. False when none is waiting.</summary>
        internal bool TakeRequestedUpdate(out Rect region)
        {
            region = _requestedAll ? WorldRect() : _requestedRegion;
            bool requested = _updateRequested;
            _updateRequested = false;
            _requestedAll = false;
            return requested;
        }

        public void SetLayerSyncTarget(UnityEngine.Object target)
        {
            _layerSyncTarget = target;
        }

        /// <summary>
        /// Uses an existing instance set for the placements, for example the set of an earlier placement system. The next
        /// placement write replaces the entries of other owners in it.
        /// </summary>
        public void SetPlacementSet(InstanceSet set)
        {
            _placementSet = set;
            _storedPlacements = null;
        }

        /// <summary>Gets the active height modifiers in apply order.</summary>
        public void GetHeightModifiers(List<LoogaHeightModifier> modifiers)
        {
            GetComponentsInChildren(false, modifiers);
            // A stamp that waits for the textures of its texture source has no result yet. Without the skip, a Min
            // stamp with an empty texture would cut its whole area down to its bottom.
            modifiers.RemoveAll(modifier => !modifier.enabled || LoogaStampTextureSource.Waits(modifier) ||
                modifier is LoogaNeighbourhoodStamp { IsReady: false });
        }

        #region Evaluation
        /// <summary>
        /// Evaluates all modifiers into new 16-bit textures, one for each terrain. The terrains do not change.
        /// </summary>
        /// <returns>The heights. The caller must dispose them.</returns>
        public StampHeights EvaluateHeights()
        {
            List<StampTile> tiles = GetTiles(null);
            var outputs = new RenderTexture[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                outputs[i] = HeightStampEvaluator.CreateOutput(tiles[i]);
            }
            Evaluate(tiles, outputs);
            return new StampHeights(tiles.ConvertAll(tile => tile.Terrain).ToArray(), outputs, true);
        }

        /// <summary>
        /// A hash of the enabled modifiers before a snapshot and their revisions. It changes when the set of modifiers
        /// changes or one of them changes its settings or textures. A move does not change it.
        /// </summary>
        internal ulong SnapshotSignature(LoogaHeightSnapshot snapshot)
        {
            var modifiers = new List<LoogaHeightModifier>();
            GetHeightModifiers(modifiers);
            ulong hash = 14695981039346656037UL;
            foreach (LoogaHeightModifier modifier in modifiers)
            {
                if (modifier == snapshot) break;
                hash = (hash ^ (uint)RuntimeHelpers.GetHashCode(modifier)) * 1099511628211UL;
                hash = (hash ^ (uint)modifier.Revision) * 1099511628211UL;
            }
            return hash;
        }

        /// <summary>
        /// False while the heights at a snapshot are incomplete: a stamp before it waits for its textures, or the world
        /// waits for an update. After a script reload, scripts set the stamp textures again in their first updates.
        /// </summary>
        internal bool IsSnapshotReady(LoogaHeightSnapshot snapshot)
        {
            if (IsUpdating || HasRequestedUpdate) return false;
            List<LoogaHeightModifier> modifiers = _snapshotModifiers;
            GetComponentsInChildren(false, modifiers);
            foreach (LoogaHeightModifier modifier in modifiers)
            {
                if (modifier == snapshot) break;
                if (!modifier.enabled) continue;
                if (LoogaStampTextureSource.Waits(modifier) || modifier is LoogaNeighbourhoodStamp { IsReady: false })
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>The terrains whose tiles touch a world XZ region, in tile order.</summary>
        internal void GetTerrains(Rect worldRegion, List<UnityEngine.Terrain> terrains)
        {
            terrains.Clear();
            foreach (StampTile tile in GetTiles(worldRegion))
            {
                terrains.Add(tile.Terrain);
            }
        }

        /// <summary>
        /// Evaluates the terrains that a world XZ rectangle touches up to a snapshot: only the modifiers before the
        /// snapshot apply. The terrains do not change. Authoring tools use it to read the ground before a later stamp,
        /// for example the level of a pad from the ground before the pads, without a change and a wait.
        /// </summary>
        /// <param name="snapshot">An enabled snapshot of this world. It needs no limits.</param>
        /// <returns>The heights at the snapshot. The caller must dispose them.</returns>
        public StampHeights ReadSnapshot(LoogaHeightSnapshot snapshot, Rect worldRegion)
        {
            var modifiers = new List<LoogaHeightModifier>();
            GetHeightModifiers(modifiers);
            int index = modifiers.IndexOf(snapshot);
            if (index < 0)
            {
                throw new ArgumentException("The snapshot is not an enabled modifier of this stamp world.",
                    nameof(snapshot));
            }
            modifiers.RemoveRange(index, modifiers.Count - index);
            List<StampTile> tiles = GetTiles(worldRegion);
            var outputs = new RenderTexture[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                outputs[i] = HeightStampEvaluator.CreateOutput(tiles[i]);
            }
            Evaluate(tiles, outputs, modifiers);
            return new StampHeights(tiles.ConvertAll(tile => tile.Terrain).ToArray(), outputs, true);
        }

        /// <summary>
        /// Evaluates all modifiers, writes the heights into the terrains and synchronizes the CPU heights. The tile
        /// textures are released after the write. Region updates keep the textures of the tiles they write for the next
        /// update.
        /// </summary>
        /// <param name="reuseDistanceFields">False builds every spline distance field and water field again.</param>
        public void RebuildHeights(bool reuseDistanceFields = false)
        {
            if (!reuseDistanceFields)
            {
                ClearDistanceFields();
            }
            Rect all = WorldRect();
            Write(GetTiles(null), all);
            ReleaseOutputs();
            SynchronizeHeights();
        }

        /// <summary>
        /// Evaluates the terrains that a world XZ rectangle touches and writes them. The CPU heights stay
        /// unsynchronized until <see cref="SynchronizeHeights"/>.
        /// </summary>
        /// <returns>The number of terrains written.</returns>
        public int UpdateRegion(Rect worldRegion)
        {
            List<StampTile> tiles = GetTiles(worldRegion);
            if (tiles.Count == 0) return 0;
            Write(tiles, worldRegion);
            return tiles.Count;
        }

        /// <summary>
        /// Like <see cref="UpdateRegion"/>, but without a wait for the GPU. The samples that the region can change are
        /// copied at once. A GPU check then finds changed samples outside them; <see cref="CompletePendingHeights"/>
        /// copies those when the check is done. Until then, <see cref="HeightsSynchronized"/> is false.
        /// </summary>
        /// <remarks>
        /// A wait for the GPU also waits for the frame that the GPU is still rendering, which in the editor can take much
        /// longer than the height work.
        /// </remarks>
        /// <returns>The number of terrains written.</returns>
        public int UpdateRegionWithoutWait(Rect worldRegion)
        {
            List<StampTile> tiles = GetTiles(worldRegion);
            if (tiles.Count == 0) return 0;
            var outputs = new RenderTexture[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                outputs[i] = GetOutput(tiles[i]);
            }
            SupersedePendingHeights(tiles);
            Evaluate(tiles, outputs);
            var copied = new RectInt[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                copied[i] = RegionSamples(tiles[i], worldRegion);
            }
            using (StampSections.Measure(StampSection.HeightCopy))
            {
                CopyChangedHeights(tiles, outputs, copied);
            }
            // The check runs after the copy on the GPU, so it finds only the changed samples outside the copied area.
            _pendingHeights.Add(new PendingHeights
            {
                Check = _evaluator.RequestChangedAreas(tiles, outputs),
                Tiles = tiles,
                Outputs = outputs,
                Superseded = new bool[tiles.Count]
            });
            NotifyHeightsUpdated(worldRegion);
            return tiles.Count;
        }

        /// <summary>
        /// Copies the changed samples that finished checks of <see cref="UpdateRegionWithoutWait"/> found. It does not
        /// wait: unfinished checks stay for a later call.
        /// </summary>
        public void CompletePendingHeights()
        {
            for (int index = 0; index < _pendingHeights.Count;)
            {
                PendingHeights pending = _pendingHeights[index];
                if (!pending.Check.IsDone)
                {
                    index++;
                    continue;
                }
                _pendingHeights.RemoveAt(index);
                RectInt[] areas;
                try
                {
                    areas = pending.Check.Read();
                }
                finally
                {
                    pending.Check.Dispose();
                }
                Rect? changed = null;
                for (int i = 0; i < pending.Tiles.Count; i++)
                {
                    StampTile tile = pending.Tiles[i];
                    if (pending.Superseded[i] || areas[i].width <= 0 || !tile.Terrain || !pending.Outputs[i])
                    {
                        areas[i] = default;
                        continue;
                    }
                    Rect world = SampleRect(tile, areas[i]);
                    changed = changed.HasValue ? Rect.MinMaxRect(Mathf.Min(changed.Value.xMin, world.xMin),
                        Mathf.Min(changed.Value.yMin, world.yMin), Mathf.Max(changed.Value.xMax, world.xMax),
                        Mathf.Max(changed.Value.yMax, world.yMax)) : world;
                }
                if (!changed.HasValue) continue;
                CopyChangedHeights(pending.Tiles, pending.Outputs, areas);
                NotifyHeightsUpdated(changed.Value);
            }
        }

        // Waits for the GPU checks of UpdateRegionWithoutWait and copies their results. Tests and the editor updater
        // (before a script reload) use it.
        internal void WaitForPendingHeights()
        {
            foreach (PendingHeights pending in _pendingHeights)
            {
                pending.Check.Wait();
            }
            CompletePendingHeights();
        }

        private sealed class PendingHeights
        {
            internal HeightStampEvaluator.ChangeCheck Check;
            internal List<StampTile> Tiles;
            internal RenderTexture[] Outputs;
            // A later write of the tile replaces its output and checks it again.
            internal bool[] Superseded;
        }

        private void SupersedePendingHeights(List<StampTile> tiles)
        {
            foreach (PendingHeights pending in _pendingHeights)
            {
                for (int i = 0; i < pending.Tiles.Count; i++)
                {
                    if (tiles.Exists(tile => tile.Terrain == pending.Tiles[i].Terrain))
                    {
                        pending.Superseded[i] = true;
                    }
                }
            }
        }

        private void DropPendingHeights()
        {
            foreach (PendingHeights pending in _pendingHeights)
            {
                pending.Check.Dispose();
            }
            _pendingHeights.Clear();
        }

        // Heightmap samples of a tile that a world rectangle can change, with a margin of two samples.
        private static RectInt RegionSamples(in StampTile tile, Rect region)
        {
            float spacingX = tile.Size.x / (tile.Resolution - 1);
            float spacingZ = tile.Size.z / (tile.Resolution - 1);
            int xMin = Mathf.Clamp(Mathf.FloorToInt((region.xMin - tile.Position.x) / spacingX) - 2, 0, tile.Resolution);
            int zMin = Mathf.Clamp(Mathf.FloorToInt((region.yMin - tile.Position.z) / spacingZ) - 2, 0, tile.Resolution);
            int xMax = Mathf.Clamp(Mathf.CeilToInt((region.xMax - tile.Position.x) / spacingX) + 3, 0, tile.Resolution);
            int zMax = Mathf.Clamp(Mathf.CeilToInt((region.yMax - tile.Position.z) / spacingZ) + 3, 0, tile.Resolution);
            return new RectInt(xMin, zMin, xMax - xMin, zMax - zMin);
        }

        // World XZ rectangle of a sample rectangle of a tile.
        private static Rect SampleRect(in StampTile tile, RectInt samples)
        {
            float spacingX = tile.Size.x / (tile.Resolution - 1);
            float spacingZ = tile.Size.z / (tile.Resolution - 1);
            return Rect.MinMaxRect(tile.Position.x + samples.xMin * spacingX, tile.Position.z + samples.yMin * spacingZ,
                tile.Position.x + (samples.xMax - 1) * spacingX, tile.Position.z + (samples.yMax - 1) * spacingZ);
        }

        /// <summary>Synchronizes the CPU heightmaps (collision, height queries) of all written terrains.</summary>
        public void SynchronizeHeights()
        {
            SynchronizeHeights(int.MaxValue);
        }

        /// <summary>
        /// Synchronizes the CPU heightmaps of at most <paramref name="maxTerrains"/> written terrains, so that an editor
        /// tick stays short. Each terrain reads back only its written area; a full 2049 heightmap takes about 0.2 s.
        /// </summary>
        /// <returns>True when all written terrains are synchronized.</returns>
        public bool SynchronizeHeights(int maxTerrains)
        {
            CompletePendingHeights();
            var done = new List<UnityEngine.Terrain>();
            foreach (UnityEngine.Terrain terrain in _unsynchronized)
            {
                if (done.Count >= maxTerrains) break;
                if (terrain && terrain.terrainData)
                {
                    using (StampSections.Measure(StampSection.HeightSync))
                    {
                        terrain.terrainData.SyncHeightmap();
                    }
                }
                done.Add(terrain);
            }
            foreach (UnityEngine.Terrain terrain in done)
            {
                _unsynchronized.Remove(terrain);
            }
            return _unsynchronized.Count == 0 && _pendingHeights.Count == 0;
        }

        /// <summary>
        /// Removes the cached fields of one spline area, spline path or water area, on all terrains or on the terrains
        /// that a world XZ rectangle touches.
        /// </summary>
        public void InvalidateDistanceFields(UnityEngine.Object owner, Rect? worldRegion = null)
        {
            if (!owner || _evaluator == null) return;
            if (owner is LoogaWaterArea area)
            {
                _evaluator.WaterFields.Invalidate(area, worldRegion);
            }
            else
            {
                _evaluator.DistanceFields.Invalidate(owner, worldRegion);
            }
        }

        /// <summary>Removes all cached spline distance fields and water fields.</summary>
        public void ClearDistanceFields()
        {
            _evaluator?.DistanceFields.ClearCache();
            _evaluator?.WaterFields.ClearCache();
        }

        public void ResetDistanceFieldStatistics()
        {
            _evaluator?.DistanceFields.ResetStatistics();
            _evaluator?.WaterFields.ResetStatistics();
        }

        public void ResetPlacementPeakBytes()
        {
            _placementEvaluator?.ResetPeakTemporaryBytes();
            _peakTileLayerBytes = 0;
        }

        /// <summary>
        /// Releases the GPU resources: evaluators, cached distance fields, smooth stamp rings, tile textures and stored
        /// layer maps. The next texture region update rebuilds all terrains.
        /// </summary>
        public void ReleaseResources()
        {
            ReleaseTextureLayers();
            _pendingPlacements = null;
            _placementEvaluator?.Dispose();
            _placementEvaluator = null;
            _textureEvaluator?.Dispose();
            _textureEvaluator = null;
            _surfaceFields?.Dispose();
            _surfaceFields = null;
            _fieldMaps?.Dispose();
            _fieldMaps = null;
            _placementFieldMaps?.Dispose();
            _placementFieldMaps = null;
            _evaluator?.Dispose();
            _evaluator = null;
            ReleaseOutputs();
        }

        /// <summary>
        /// Releases the GPU memory that the world can build again when it needs it: the cached spline and water distance
        /// fields and the idle placement atlases. Call it when GPU memory is short, for example after a failed readback.
        /// </summary>
        /// <returns>The GPU memory released, in bytes.</returns>
        public long ReleaseCachedFields()
        {
            long bytes = 0;
            if (_evaluator != null)
            {
                bytes += _evaluator.DistanceFields.CachedBytes + _evaluator.WaterFields.CachedBytes;
                _evaluator.DistanceFields.ClearCache();
                _evaluator.WaterFields.ClearCache();
            }
            if (_placementEvaluator != null)
            {
                bytes += _placementEvaluator.ReleaseIdleAtlases();
            }
            return bytes;
        }

        private void ReleaseOutputs()
        {
            DropPendingHeights();
            foreach (RenderTexture output in _outputs.Values)
            {
                if (!output) continue;
                output.Release();
                DestroyImmediate(output);
            }
            _outputs.Clear();
        }

        private void Write(List<StampTile> tiles, Rect region)
        {
            SupersedePendingHeights(tiles);
            var outputs = new RenderTexture[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                outputs[i] = GetOutput(tiles[i]);
            }
            Evaluate(tiles, outputs);
            // Only the changed samples of a tile are copied. The CPU synchronization then reads back only that area
            // (a 400 x 560 area: 18 ms; a full 2049 heightmap: 0.18 s, in the scene up to 0.4 s). A tile with unchanged
            // heights keeps its terrain data and needs no synchronization.
            RectInt[] changed;
            using (StampSections.Measure(StampSection.HeightCompare))
            {
                changed = _evaluator.FindChangedAreas(tiles, outputs);
            }
            using (StampSections.Measure(StampSection.HeightCopy))
            {
                CopyChangedHeights(tiles, outputs, changed);
            }
            NotifyHeightsUpdated(region);
        }

        private void NotifyHeightsUpdated(Rect region)
        {
            _fieldMaps?.InvalidateFlow(region);
            HeightsUpdated?.Invoke(region);
        }

        private void CopyChangedHeights(List<StampTile> tiles, RenderTexture[] outputs, RectInt[] changed)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                for (int i = 0; i < tiles.Count; i++)
                {
                    if (changed[i].width <= 0) continue;
                    TerrainData data = tiles[i].Terrain.terrainData;
                    RenderTexture.active = outputs[i];
                    data.CopyActiveRenderTextureToHeightmap(changed[i], changed[i].position,
                        TerrainHeightmapSyncControl.None);
                    _unsynchronized.Add(tiles[i].Terrain);
                }
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        // Null modifiers: all enabled modifiers of the world.
        private void Evaluate(List<StampTile> tiles, RenderTexture[] outputs, List<LoogaHeightModifier> modifiers = null)
        {
            using StampSections.Scope section = StampSections.Measure(StampSection.HeightEvaluation);
            _evaluator ??= new HeightStampEvaluator();
            _evaluator.DistanceFields.CacheBudget = (long)_distanceFieldBudget * 1024 * 1024;
            _evaluator.WaterFields.CacheBudget = (long)_waterFieldBudget * 1024 * 1024;
            if (modifiers == null)
            {
                modifiers = new List<LoogaHeightModifier>();
                GetHeightModifiers(modifiers);
            }
            try
            {
                if (modifiers.Exists(modifier => modifier is LoogaNeighbourhoodStamp))
                {
                    EvaluateWithBarriers(tiles, outputs, modifiers);
                }
                else
                {
                    foreach (int i in NextSweep(tiles.Count))
                    {
                        _evaluator.EvaluateTile(tiles[i], modifiers, _mapping, _halo, outputs[i]);
                    }
                }
            }
            finally
            {
                // Snapshot copies live only during an evaluation.
                _evaluator.ReleaseSnapshots();
            }
            _evaluator.AverageSeams(tiles, outputs);
        }

        // A neighbourhood stamp reads the heights of the neighbour tiles, so it splits the stack into segments. Every
        // needed tile runs a segment and stores the border ring of its state. The stamp of a tile then reads its own
        // state and the rings of its neighbours. A neighbour that is not written and has a stored ring needs no
        // evaluation. Other neighbours run only the segments that their neighbours need.
        private void EvaluateWithBarriers(List<StampTile> tiles, RenderTexture[] outputs,
            List<LoogaHeightModifier> modifiers)
        {
            var barriers = new List<LoogaNeighbourhoodStamp>();
            var segments = new List<List<LoogaHeightModifier>> { new() };
            foreach (LoogaHeightModifier modifier in modifiers)
            {
                if (modifier is LoogaNeighbourhoodStamp barrier)
                {
                    barriers.Add(barrier);
                    segments.Add(new List<LoogaHeightModifier>());
                }
                else
                {
                    segments[^1].Add(modifier);
                }
            }

            _evaluator.ReleaseDeadNeighbourhoodRings();
            var all = new Dictionary<UnityEngine.Terrain, StampTile>();
            foreach (StampTile tile in GetTiles(null))
            {
                all[tile.Terrain] = tile;
            }
            // Needed tiles of each segment, from the last segment back. Segment k + 1 needs the rings of the
            // neighbours within the reach of barrier k.
            var needed = new HashSet<UnityEngine.Terrain>[segments.Count];
            needed[^1] = new HashSet<UnityEngine.Terrain>(tiles.ConvertAll(tile => tile.Terrain));
            var neighbours = new UnityEngine.Terrain[9];
            for (int k = barriers.Count - 1; k >= 0; k--)
            {
                needed[k] = new HashSet<UnityEngine.Terrain>(needed[k + 1]);
                Rect cull = barriers[k].GetCullRect(_mapping);
                foreach (UnityEngine.Terrain terrain in needed[k + 1])
                {
                    Rect tileRect = all[terrain].WorldRect;
                    if (!StampMapping.Touches(tileRect, cull)) continue;
                    Rect read = Grow(StampMapping.Intersect(tileRect, cull), barriers[k].Reach);
                    GetNeighbours(terrain, all, neighbours);
                    foreach (UnityEngine.Terrain neighbour in neighbours)
                    {
                        if (!neighbour || needed[k].Contains(neighbour)) continue;
                        if (!StampMapping.Touches(all[neighbour].WorldRect, read)) continue;
                        if (_evaluator.HasNeighbourhoodRing(all[neighbour], barriers[k], _mapping, _halo)) continue;
                        needed[k].Add(neighbour);
                    }
                }
            }

            var states = new Dictionary<UnityEngine.Terrain, RenderTexture>();
            var owned = new List<RenderTexture>();
            try
            {
                for (int i = 0; i < tiles.Count; i++)
                {
                    states[tiles[i].Terrain] = _mapping == HeightMapping.MicroVerseParity ? outputs[i] : null;
                }
                foreach (UnityEngine.Terrain terrain in needed[0])
                {
                    if (states.TryGetValue(terrain, out RenderTexture existing) && existing) continue;
                    RenderTexture state = HeightStampEvaluator.CreateState(all[terrain], _mapping, _halo);
                    owned.Add(state);
                    states[terrain] = state;
                }

                // The modifiers after each segment. A snapshot limit there can read a snapshot of an earlier segment.
                var laterModifiers = new List<LoogaHeightModifier>[segments.Count];
                laterModifiers[^1] = new List<LoogaHeightModifier>();
                for (int k = segments.Count - 2; k >= 0; k--)
                {
                    laterModifiers[k] = new List<LoogaHeightModifier>(segments[k + 1]);
                    laterModifiers[k].AddRange(laterModifiers[k + 1]);
                }

                var segmentTiles = new List<UnityEngine.Terrain>();
                for (int k = 0; k < segments.Count; k++)
                {
                    segmentTiles.Clear();
                    foreach (UnityEngine.Terrain terrain in _terrains)
                    {
                        if (terrain && needed[k].Contains(terrain)) segmentTiles.Add(terrain);
                    }
                    foreach (int i in NextSweep(segmentTiles.Count))
                    {
                        UnityEngine.Terrain terrain = segmentTiles[i];
                        _evaluator.EvaluateSegment(all[terrain], segments[k], laterModifiers[k], _mapping, _halo,
                            states[terrain], k == 0);
                    }
                    if (k == barriers.Count) break;

                    LoogaNeighbourhoodStamp barrier = barriers[k];
                    Rect cull = barrier.GetCullRect(_mapping);
                    Rect reach = Grow(cull, barrier.Reach);
                    // All rings of the segment are stored before the stamp changes a state.
                    foreach (UnityEngine.Terrain terrain in needed[k])
                    {
                        if (!StampMapping.Touches(all[terrain].WorldRect, reach)) continue;
                        _evaluator.StoreNeighbourhoodRing(all[terrain], barrier, _mapping, _halo, states[terrain]);
                    }
                    foreach (UnityEngine.Terrain terrain in needed[k + 1])
                    {
                        if (!StampMapping.Touches(all[terrain].WorldRect, cull)) continue;
                        GetNeighbours(terrain, all, neighbours);
                        _evaluator.ApplyNeighbourhood(all[terrain], barrier, _mapping, _halo, states[terrain], neighbours);
                    }
                }

                if (_mapping != HeightMapping.MicroVerseParity)
                {
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        _evaluator.WriteState(tiles[i], _mapping, _halo, states[tiles[i].Terrain], outputs[i]);
                    }
                }
            }
            finally
            {
                foreach (RenderTexture state in owned)
                {
                    state.Release();
                    DestroyImmediate(state);
                }
            }
        }

        // The tile (slot 4) and its eight neighbours in the world: slot = (z offset + 1) * 3 + (x offset + 1).
        // A neighbour outside the world, or with another resolution, is null.
        private static void GetNeighbours(UnityEngine.Terrain terrain, Dictionary<UnityEngine.Terrain, StampTile> all,
            UnityEngine.Terrain[] neighbours)
        {
            UnityEngine.Terrain left = terrain.leftNeighbor, right = terrain.rightNeighbor;
            UnityEngine.Terrain bottom = terrain.bottomNeighbor, top = terrain.topNeighbor;
            neighbours[0] = left ? left.bottomNeighbor : bottom ? bottom.leftNeighbor : null;
            neighbours[1] = bottom;
            neighbours[2] = right ? right.bottomNeighbor : bottom ? bottom.rightNeighbor : null;
            neighbours[3] = left;
            neighbours[4] = terrain;
            neighbours[5] = right;
            neighbours[6] = left ? left.topNeighbor : top ? top.leftNeighbor : null;
            neighbours[7] = top;
            neighbours[8] = right ? right.topNeighbor : top ? top.rightNeighbor : null;
            int resolution = terrain.terrainData.heightmapResolution;
            for (int slot = 0; slot < 9; slot++)
            {
                UnityEngine.Terrain neighbour = neighbours[slot];
                if (neighbour && (!all.ContainsKey(neighbour) ||
                    neighbour.terrainData.heightmapResolution != resolution))
                {
                    neighbours[slot] = null;
                }
            }
        }

        /// <summary>
        /// Grows a height region by the reach of every enabled neighbourhood stamp (for example a smooth stamp) whose
        /// reach it touches, so the tiles whose stamps read the changed heights update too.
        /// </summary>
        public Rect GrowForSmoothing(Rect region)
        {
            var modifiers = new List<LoogaHeightModifier>();
            GetHeightModifiers(modifiers);
            float reach = 0f;
            foreach (LoogaHeightModifier modifier in modifiers)
            {
                if (modifier is not LoogaNeighbourhoodStamp stamp) continue;
                if (StampMapping.Touches(Grow(stamp.GetCullRect(_mapping), stamp.Reach), region))
                {
                    reach = Mathf.Max(reach, stamp.Reach);
                }
            }
            return reach > 0f ? Grow(region, reach) : region;
        }

        private static Rect Grow(Rect rect, float amount)
        {
            return Rect.MinMaxRect(rect.xMin - amount, rect.yMin - amount, rect.xMax + amount, rect.yMax + amount);
        }

        private RenderTexture GetOutput(in StampTile tile)
        {
            if (_outputs.TryGetValue(tile.Terrain, out RenderTexture output) && output &&
                output.width == tile.Resolution)
            {
                return output;
            }
            output = HeightStampEvaluator.CreateOutput(tile);
            _outputs[tile.Terrain] = output;
            return output;
        }

        // Terrains in the world order. A region keeps only the terrains whose rectangle touches it.
        private List<StampTile> GetTiles(Rect? region)
        {
            var tiles = new List<StampTile>(_terrains.Length);
            foreach (UnityEngine.Terrain terrain in _terrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                var tile = new StampTile(terrain);
                if (region.HasValue && !StampMapping.Touches(tile.WorldRect, region.Value)) continue;
                tiles.Add(tile);
            }
            return tiles;
        }

        /// <summary>World XZ rectangle that contains all owned terrains.</summary>
        internal Rect WorldRect()
        {
            Rect? all = null;
            foreach (UnityEngine.Terrain terrain in _terrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                Rect rect = new StampTile(terrain).WorldRect;
                all = all.HasValue ? Rect.MinMaxRect(Mathf.Min(all.Value.xMin, rect.xMin),
                    Mathf.Min(all.Value.yMin, rect.yMin), Mathf.Max(all.Value.xMax, rect.xMax),
                    Mathf.Max(all.Value.yMax, rect.yMax)) : rect;
            }
            return all ?? Rect.zero;
        }
        #endregion

        #region Textures
        /// <summary>Gets the active texture modifiers in hierarchy order.</summary>
        public void GetTextureModifiers(List<LoogaTextureModifier> modifiers)
        {
            GetComponentsInChildren(false, modifiers);
            // A stamp that waits for the falloff texture of its texture source would paint its whole box.
            modifiers.RemoveAll(modifier => !modifier.enabled || LoogaStampTextureSource.Waits(modifier));
        }

        /// <summary>
        /// Gets the terrain layers that the texture modifiers use, also disabled ones, sorted by name. This is the layer
        /// list that MicroVerse assigns to its terrains.
        /// </summary>
        public List<TerrainLayer> GetTerrainLayers()
        {
            var layers = new List<TerrainLayer>();
            foreach (LoogaTextureModifier modifier in GetComponentsInChildren<LoogaTextureModifier>(true))
            {
                TerrainLayer layer = modifier.Layer;
                if (layer && !layers.Contains(layer))
                {
                    layers.Add(layer);
                }
            }
            layers.Sort((a, b) => Comparer<string>.Default.Compare(a.name, b.name));
            return layers;
        }

        /// <summary>
        /// Gives each owned terrain the layer list of <see cref="GetTerrainLayers"/> when its list differs, like
        /// MicroVerse. Rebuild the textures after a change.
        /// </summary>
        /// <returns>The number of terrains that changed.</returns>
        public int SyncTerrainLayers()
        {
            TerrainLayer[] layers = GetTerrainLayers().ToArray();
            int changed = 0;
            foreach (UnityEngine.Terrain terrain in _terrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                TerrainLayer[] current = terrain.terrainData.terrainLayers;
                bool same = current.Length == layers.Length;
                for (int i = 0; same && i < layers.Length; i++)
                {
                    same = current[i] == layers[i];
                }
                if (same) continue;
                terrain.terrainData.terrainLayers = layers;
                // The renderer binds the control textures and layer data again.
                if (terrain.TryGetComponent(out TerrainRenderer renderer))
                {
                    renderer.RefreshSource();
                }
                changed++;
            }
            // Terrains that draw with a Looga terrain surface need the same layer list in the surface.
            if (changed > 0)
            {
                LoogaTerrainSurfaceBinding.SyncSurfaceLayers(_terrains);
            }
            return changed;
        }

        /// <summary>
        /// Evaluates all texture modifiers into new alphamap textures, one set for each terrain. The terrains do not
        /// change. Layer channels follow the current layer list of each terrain.
        /// </summary>
        /// <returns>The alphamaps. The caller must dispose them.</returns>
        public StampAlphamaps EvaluateTextures()
        {
            return EvaluateTextures(null);
        }

        /// <param name="include">Keeps only the modifiers that it accepts. Null keeps all.</param>
        internal StampAlphamaps EvaluateTextures(Predicate<LoogaTextureModifier> include)
        {
            List<TextureStampEvaluator.TileLayers> layers = EvaluateTextureLayers(include);
            var alphamaps = new RenderTexture[layers.Count][];
            try
            {
                for (int i = 0; i < layers.Count; i++)
                {
                    alphamaps[i] = _textureEvaluator.WriteAlphamaps(layers[i]);
                }
            }
            finally
            {
                foreach (TextureStampEvaluator.TileLayers tile in layers)
                {
                    tile.Dispose();
                }
            }
            return new StampAlphamaps(layers.ConvertAll(tile => tile.Terrain).ToArray(), alphamaps);
        }

        /// <summary>
        /// Layer and weight maps of every terrain after all texture passes and the edge copy, in terrain order. The
        /// caller disposes each tile. <see cref="WriteAlphamaps"/> converts one tile.
        /// </summary>
        internal List<TextureStampEvaluator.TileLayers> EvaluateTextureLayers(Predicate<LoogaTextureModifier> include)
        {
            return EvaluateTextureLayers(GetTiles(null), include, null);
        }

        // Evaluates the tiles. With stored maps, the borders against terrains that are not in the list keep their stored
        // texels.
        private List<TextureStampEvaluator.TileLayers> EvaluateTextureLayers(List<StampTile> tiles,
            Predicate<LoogaTextureModifier> include,
            IReadOnlyDictionary<UnityEngine.Terrain, TextureStampEvaluator.TileLayers> stored)
        {
            using StampSections.Scope section = StampSections.Measure(StampSection.TextureEvaluation);
            CreateTextureEvaluator();
            var modifiers = new List<LoogaTextureModifier>();
            GetTextureModifiers(modifiers);
            if (include != null)
            {
                modifiers.RemoveAll(modifier => !include(modifier));
            }
            HeightSampling heights = CreateHeightSampling(out int normalResolution);
            var layers = new List<TextureStampEvaluator.TileLayers>(new TextureStampEvaluator.TileLayers[tiles.Count]);
            PlacementFieldSources fieldSources = null;
            try
            {
                foreach (int i in NextSweep(tiles.Count))
                {
                    layers[i] = _textureEvaluator.EvaluateTile(tiles[i], modifiers, heights.Get, normalResolution,
                        _fieldMaps, StoredPlacementFields(tiles[i], modifiers, ref fieldSources));
                }
                _textureEvaluator.CopyEdges(layers);
                if (stored != null)
                {
                    TextureStampEvaluator.KeepStoredBorders(layers, stored);
                }
            }
            catch
            {
                foreach (TextureStampEvaluator.TileLayers tile in layers)
                {
                    tile?.Dispose();
                }
                throw;
            }
            finally
            {
                _fieldMaps.Release();
                heights.Dispose();
            }
            return layers;
        }

        // The placement field map of a tile from the stored placements, when a texture stamp on the tile reads it. Null
        // otherwise, and before the first placement write: the texture evaluation then skips these stamps until the
        // placement write updates their textures (RefreshPlacementFieldTextures).
        private Texture StoredPlacementFields(in StampTile tile, List<LoogaTextureModifier> modifiers,
            ref PlacementFieldSources sources)
        {
            if (_storedPlacements == null) return null;
            bool needed = false;
            foreach (LoogaTextureModifier modifier in modifiers)
            {
                needed |= modifier is LoogaTextureStamp { ReadsPlacementFields: true } &&
                    StampMapping.Touches(modifier.GetCullRect(HeightMapping.MicroVerseParity), tile.WorldRect);
            }
            if (!needed) return null;
            sources ??= CreateFieldSources(new List<StampTile>());
            _placementFieldMaps ??= new PlacementFieldMaps();
            Texture fields = _placementFieldMaps.Build(tile, sources, _terrains);
            NoteRefusedReads(tile.WorldRect);
            return fields;
        }

        private void CreateTextureEvaluator()
        {
            _evaluator ??= new HeightStampEvaluator();
            _evaluator.DistanceFields.CacheBudget = (long)_distanceFieldBudget * 1024 * 1024;
            _evaluator.WaterFields.CacheBudget = (long)_waterFieldBudget * 1024 * 1024;
            _textureEvaluator ??= new TextureStampEvaluator(_evaluator.DistanceFields, _evaluator.WaterFields);
            _fieldMaps ??= new TerrainFieldMaps();
        }

        // Height sampling for the terrains of the world. MicroVerse makes the normal map of every tile at the heightmap
        // resolution of its first terrain.
        private HeightSampling CreateHeightSampling(out int normalResolution)
        {
            var world = new HashSet<UnityEngine.Terrain>();
            normalResolution = 0;
            foreach (UnityEngine.Terrain terrain in _terrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                if (world.Count == 0)
                {
                    normalResolution = terrain.terrainData.heightmapResolution;
                }
                world.Add(terrain);
            }
            return new HeightSampling(world);
        }

        /// <summary>
        /// Gives the texture filters the terrain heights with anisoLevel 0, like the height textures of MicroVerse, for
        /// the time of one evaluation. The terrain heightmap textures have anisoLevel 1, and forced anisotropic
        /// filtering changes the samples, also with explicit levels and inline samplers. Restores the levels on
        /// dispose.
        /// </summary>
        private sealed class HeightSampling : IDisposable
        {
            private readonly HashSet<UnityEngine.Terrain> _world;
            private readonly Dictionary<RenderTexture, int> _levels = new();

            internal HeightSampling(HashSet<UnityEngine.Terrain> world)
            {
                _world = world;
            }

            /// <summary>The heights of a terrain of the world, or null.</summary>
            internal RenderTexture Get(UnityEngine.Terrain terrain)
            {
                if (!terrain || !_world.Contains(terrain)) return null;
                RenderTexture heights = terrain.terrainData.heightmapTexture;
                if (!_levels.ContainsKey(heights))
                {
                    _levels[heights] = heights.anisoLevel;
                    heights.anisoLevel = 0;
                }
                return heights;
            }

            public void Dispose()
            {
                foreach (KeyValuePair<RenderTexture, int> pair in _levels)
                {
                    if (pair.Key)
                    {
                        pair.Key.anisoLevel = pair.Value;
                    }
                }
                _levels.Clear();
            }
        }

        /// <summary>Alphamaps of one evaluated tile. Release them with RenderTexture.ReleaseTemporary.</summary>
        internal RenderTexture[] WriteAlphamaps(TextureStampEvaluator.TileLayers tile)
        {
            return _textureEvaluator.WriteAlphamaps(tile);
        }

        /// <summary>
        /// Evaluates all texture modifiers and writes the alphamaps into the terrains on the GPU. The CPU copies of the
        /// alphamaps synchronize later (on save or on request), like MicroVerse. The world keeps the layer and weight
        /// maps of each terrain for region updates.
        /// </summary>
        public void RebuildTextures()
        {
            _texturesRebuiltSinceLoad = true;
            // Heights can change outside the stamp world, for example by an undo, so a full rebuild measures the flow
            // again.
            _fieldMaps?.ClearFlow();
            ReleaseTextureLayers();
            SyncTerrainLayers();
            WriteTextures(EvaluateTextureLayers(GetTiles(null), null, null), WorldRect());
        }

        /// <summary>
        /// Evaluates the textures of the terrains near a world XZ rectangle and writes them on the GPU. The rectangle
        /// grows by two alphamap texels, so a change next to a tile edge also updates the neighbor. Edges and corners
        /// against other terrains keep their stored texels. Without stored maps of every terrain, or after a change of
        /// the layer list, all terrains are rebuilt.
        /// </summary>
        /// <returns>The number of terrains written.</returns>
        public int UpdateTextureRegion(Rect worldRegion)
        {
            if (SyncTerrainLayers() > 0 || !HasAllTextureLayers())
            {
                RebuildTextures();
                return _textureLayers.Count;
            }
            float margin = 0f;
            foreach (UnityEngine.Terrain terrain in _terrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                margin = Mathf.Max(margin, 2f * terrain.terrainData.size.x / terrain.terrainData.alphamapWidth);
            }
            Rect region = Rect.MinMaxRect(worldRegion.xMin - margin, worldRegion.yMin - margin,
                worldRegion.xMax + margin, worldRegion.yMax + margin);
            List<StampTile> tiles = GetTiles(region);
            if (tiles.Count == 0) return 0;
            WriteTextures(EvaluateTextureLayers(tiles, null, _textureLayers), region);
            return tiles.Count;
        }

        /// <summary>
        /// True when a texture modifier that reads heights (slope or height filter) covers a terrain that the world XZ
        /// rectangle touches. Height changes there must update the textures too.
        /// </summary>
        public bool TexturesReadHeights(Rect worldRegion)
        {
            var modifiers = new List<LoogaTextureModifier>();
            GetTextureModifiers(modifiers);
            List<StampTile> tiles = GetTiles(worldRegion);
            foreach (LoogaTextureModifier modifier in modifiers)
            {
                if (modifier is not LoogaTextureStamp { NeedsHeights: true }) continue;
                Rect cull = modifier.GetCullRect(HeightMapping.MicroVerseParity);
                foreach (StampTile tile in tiles)
                {
                    if (StampMapping.Touches(cull, tile.WorldRect)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Distance in metres around a height change in which an edit updates the textures and placements of the field
        /// filters. It is 0 when no texture, detail or object stamp uses a field filter. Flow fields can change farther
        /// downstream: those changes show after a full texture or placement update.
        /// </summary>
        public float TerrainFieldReach
        {
            get
            {
                float reach = 0f;
                var textures = new List<LoogaTextureModifier>();
                GetTextureModifiers(textures);
                foreach (LoogaTextureModifier modifier in textures)
                {
                    if (modifier is LoogaTextureStamp stamp)
                    {
                        reach = Mathf.Max(reach, TerrainFieldMaps.ReachOf(stamp.FieldFilter, stamp.FieldFilter2));
                    }
                }
                var placements = new List<LoogaPlacementModifier>();
                GetPlacementModifiers(placements);
                foreach (LoogaPlacementModifier modifier in placements)
                {
                    reach = Mathf.Max(reach, TerrainFieldReachOf(modifier));
                }
                return Mathf.Min(reach, FlowUpdateMargin);
            }
        }

        // Largest growth of an edit update for the field filters, in metres. The full flow reach would update about
        // 2 km wide areas for each edit.
        private const float FlowUpdateMargin = 128f;

        /// <summary>
        /// The flow map of one terrain of the world: R = log10 upstream area (square metres), G = wetness index. The
        /// world keeps the map in its cache. The caller must not destroy it. Null for other terrains.
        /// </summary>
        public Texture2D MeasureTerrainFlow(UnityEngine.Terrain terrain)
        {
            if (!terrain || !terrain.terrainData || Array.IndexOf(_terrains, terrain) < 0) return null;
            CreateTextureEvaluator();
            using HeightSampling heights = CreateHeightSampling(out _);
            Texture2D flow = _fieldMaps.Get(new StampTile(terrain), heights.Get, true)?.Water;
            _fieldMaps.Release();
            return flow;
        }

        /// <summary>
        /// Measures the terrain fields of one terrain of the world from its current heights, for debug views and
        /// tests. See <see cref="TerrainField"/> for the channels. The caller releases both maps with
        /// RenderTexture.ReleaseTemporary.
        /// </summary>
        /// <param name="shape">R steepness, G relative height 4 m, B relative height 20 m, A northness.</param>
        /// <param name="faces">R face above distance, G face above angle, B face below distance, A face below drop.</param>
        /// <returns>False when the terrain is not a terrain of the world.</returns>
        public bool MeasureTerrainFields(UnityEngine.Terrain terrain, out RenderTexture shape, out RenderTexture faces)
        {
            shape = null;
            faces = null;
            if (!terrain || !terrain.terrainData || Array.IndexOf(_terrains, terrain) < 0) return false;
            CreateTextureEvaluator();
            using HeightSampling heights = CreateHeightSampling(out _);
            TerrainFieldMaps.TileFields fields = _fieldMaps.Measure(new StampTile(terrain), heights.Get);
            if (fields == null) return false;
            shape = fields.Shape;
            faces = fields.Faces;
            return true;
        }

        /// <summary>
        /// Builds the placement field map of one terrain of the world from the stored placements, for debug views and
        /// tests: R = canopy, G = object foot distance in metres. The world keeps the map until the next build, so the
        /// caller must not destroy it. Null for other terrains and before the first placement write.
        /// </summary>
        public Texture2D MeasurePlacementFields(UnityEngine.Terrain terrain)
        {
            if (!terrain || !terrain.terrainData || Array.IndexOf(_terrains, terrain) < 0 || _storedPlacements == null)
            {
                return null;
            }
            _placementFieldMaps ??= new PlacementFieldMaps();
            var tile = new StampTile(terrain);
            Texture2D fields = _placementFieldMaps.Build(tile, CreateFieldSources(new List<StampTile>()), _terrains);
            NoteRefusedReads(tile.WorldRect);
            return fields;
        }

        // Takes a refused prefab mesh vertex read of the last evaluation (PlacementFieldMaps.TryReadVertices) and
        // keeps the area for an update after RefusedReadDelay.
        private void NoteRefusedReads(Rect area)
        {
            if (!PlacementFieldMaps.TakeRefusedRead()) return;
            Rect? last = _refusedReadArea;
            _refusedReadArea = last.HasValue ? Rect.MinMaxRect(Mathf.Min(last.Value.xMin, area.xMin),
                Mathf.Min(last.Value.yMin, area.yMin), Mathf.Max(last.Value.xMax, area.xMax),
                Mathf.Max(last.Value.yMax, area.yMax)) : area;
            _refusedReadTime = Time.realtimeSinceStartupAsDouble;
        }

        /// <summary>
        /// The area of evaluations that could not read prefab mesh vertices, when RefusedReadDelay passed since the
        /// last refused read. The editor updater updates it again, so the placements and placement fields use the
        /// measured prefab sizes.
        /// </summary>
        internal bool TakeRefusedReadRetry(out Rect area)
        {
            area = _refusedReadArea ?? default;
            if (!_refusedReadArea.HasValue || Time.realtimeSinceStartupAsDouble - _refusedReadTime < RefusedReadDelay)
            {
                return false;
            }
            _refusedReadArea = null;
            return true;
        }

        /// <summary>Terrains that wait for a surface field map update.</summary>
        public int SurfaceFieldsPending => _surfaceFields?.Pending ?? 0;

        /// <summary>
        /// Updates the surface field map of at most one terrain, when a surface of the world uses the fields and no
        /// height update runs. The map of a terrain takes about one flow measurement.
        /// </summary>
        internal void StepSurfaceFields()
        {
            if (LoogaTerrainSurfaceFields.WantedCount == 0 || IsUpdating || !HeightsSynchronized) return;
            _surfaceFields ??= new SurfaceFieldMaps();
            CreateTextureEvaluator();
            using HeightSampling heights = CreateHeightSampling(out _);
            _surfaceFields.Step(_terrains, _fieldMaps, heights.Get);
            // A player does not use the flow again, and the flow of a tile takes about 1 MB.
            if (Application.isPlaying && _surfaceFields.Pending == 0)
            {
                _fieldMaps.ClearFlow();
            }
        }

        /// <summary>True when the world has at least one texture modifier, enabled or not.</summary>
        public bool HasTextureModifiers => GetComponentInChildren<LoogaTextureModifier>(true);

        // A scene close removes the world and its lists of terrains whose GPU writes are not on the CPU. The terrain
        // assets stay loaded, so a reopen of the scene would see old CPU data that the editor does not show as changed.
        // The copy marks the terrain assets dirty, so a saved result is not trusted against them.
        private void SynchronizeBeforeClose()
        {
            WaitForPendingHeights();
            var written = new List<UnityEngine.Terrain>(_unsynchronized);
            written.AddRange(_texturesUnsynchronized);
            SynchronizeHeights();
            SynchronizeTextures();
#if UNITY_EDITOR
            foreach (UnityEngine.Terrain terrain in written)
            {
                if (!terrain || !terrain.terrainData) continue;
                UnityEditor.EditorUtility.SetDirty(terrain.terrainData);
            }
#endif
        }

        /// <summary>Synchronizes the CPU alphamaps of all written terrains.</summary>
        public void SynchronizeTextures()
        {
            foreach (UnityEngine.Terrain terrain in _texturesUnsynchronized)
            {
                if (terrain && terrain.terrainData)
                {
                    terrain.terrainData.SyncTexture(TerrainData.AlphamapTextureName);
                }
            }
            _texturesUnsynchronized.Clear();
        }

        // Writes the alphamaps of each tile and keeps its maps in place of the old ones.
        private void WriteTextures(List<TextureStampEvaluator.TileLayers> layers, Rect region)
        {
            using (StampSections.Measure(StampSection.AlphamapWrite))
            {
                WriteAlphamaps(layers);
            }
            TexturesUpdated?.Invoke(region);
        }

        private void WriteAlphamaps(List<TextureStampEvaluator.TileLayers> layers)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                foreach (TextureStampEvaluator.TileLayers tile in layers)
                {
                    // One staging texture for all alphamaps of the tile: the GPU copies each one before the next write.
                    TerrainData data = tile.Terrain.terrainData;
                    RenderTexture staging = TextureStampEvaluator.CreateAlphamapTarget(data);
                    try
                    {
                        for (int m = 0; m < TextureStampEvaluator.AlphamapCount(data); m++)
                        {
                            _textureEvaluator.WriteAlphamap(tile, m, staging);
                            RenderTexture.active = staging;
                            data.CopyActiveRenderTextureToTexture(TerrainData.AlphamapTextureName, m,
                                new RectInt(0, 0, staging.width, staging.height), Vector2Int.zero, true);
                        }
                    }
                    finally
                    {
                        RenderTexture.ReleaseTemporary(staging);
                    }
                    _texturesUnsynchronized.Add(tile.Terrain);
                    if (_textureLayers.TryGetValue(tile.Terrain, out TextureStampEvaluator.TileLayers old))
                    {
                        old.Dispose();
                    }
                    _textureLayers[tile.Terrain] = tile;
                }
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        /// <summary>The layer and weight maps of the last texture write, for each terrain.</summary>
        internal IReadOnlyDictionary<UnityEngine.Terrain, TextureStampEvaluator.TileLayers> TextureLayers =>
            _textureLayers;

        /// <summary>
        /// Takes layer and weight maps that describe the current alphamaps of a terrain, for example from a saved result.
        /// The world releases them later.
        /// </summary>
        internal void AdoptTextureLayers(TextureStampEvaluator.TileLayers tile)
        {
            if (_textureLayers.TryGetValue(tile.Terrain, out TextureStampEvaluator.TileLayers old))
            {
                old.Dispose();
            }
            _textureLayers[tile.Terrain] = tile;
        }

        /// <summary>True when the world has layer and weight maps of every terrain, so texture edits update regions.</summary>
        internal bool HasAllTextureLayers()
        {
            foreach (UnityEngine.Terrain terrain in _terrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                if (!_textureLayers.TryGetValue(terrain, out TextureStampEvaluator.TileLayers tile) || !tile.Layers ||
                    tile.Layers.width != terrain.terrainData.alphamapWidth)
                {
                    return false;
                }
            }
            return true;
        }

        private void ReleaseTextureLayers()
        {
            foreach (TextureStampEvaluator.TileLayers tile in _textureLayers.Values)
            {
                tile.Dispose();
            }
            _textureLayers.Clear();
        }
        #endregion

        #region Placement
        /// <summary>Gets the active placement modifiers (clear, object and detail stamps) in apply order.</summary>
        public void GetPlacementModifiers(List<LoogaPlacementModifier> modifiers)
        {
            GetComponentsInChildren(false, modifiers);
            // A stamp that waits for the falloff texture of its texture source would apply over its whole box.
            modifiers.RemoveAll(modifier => !modifier.enabled || LoogaStampTextureSource.Waits(modifier));
        }

        /// <summary>Gets the active path clears. Their order has no effect.</summary>
        public void GetPathClears(List<LoogaPathClear> clears)
        {
            GetComponentsInChildren(false, clears);
            clears.RemoveAll(clear => !clear.enabled);
        }

        /// <summary>The instance set of the last placement write, or null before the first write.</summary>
        public InstanceSet PlacementSet => _placementSet;

        /// <summary>The stored result of the last placement writes, or null before the first rebuild.</summary>
        internal StampPlacements StoredPlacements => _storedPlacements;

        /// <summary>
        /// Takes a placement result that equals the current instance set and detail layers, for example from a saved
        /// result, so the next placement update places only its region.
        /// </summary>
        internal void AdoptStoredPlacements(StampPlacements placements)
        {
            _storedPlacements = placements;
        }

        /// <summary>
        /// Evaluates the textures of some terrains and starts a GPU comparison with their current alphamaps. Nothing is
        /// written. Edges against terrains outside the list keep the stored maps, as in a region update. Null when the
        /// layer list of a terrain changed: then all terrains must update.
        /// </summary>
        /// <param name="terrains">The terrains to check. Null checks all.</param>
        internal TextureStampEvaluator.AlphamapCheck StartTextureCheck(ICollection<UnityEngine.Terrain> terrains = null)
        {
            CreateTextureEvaluator();
            if (SyncTerrainLayers() > 0) return null;

            List<StampTile> tiles = GetTiles(null);
            if (terrains != null)
            {
                tiles.RemoveAll(tile => !terrains.Contains(tile.Terrain));
            }
            Dictionary<UnityEngine.Terrain, TextureStampEvaluator.TileLayers> stored =
                HasAllTextureLayers() ? _textureLayers : null;
            List<TextureStampEvaluator.TileLayers> layers = EvaluateTextureLayers(tiles, null, stored);
            try
            {
                return _textureEvaluator.StartAlphamapCheck(layers);
            }
            finally
            {
                foreach (TextureStampEvaluator.TileLayers tile in layers)
                {
                    tile.Dispose();
                }
            }
        }

        /// <summary>The version of the stamp evaluation code. Saved results are valid only with the same version.</summary>
        internal static string StampCodeVersion => Geology.GeneratedReloadHandover.StampCodeVersion;

        /// <summary>True after a full texture rebuild since the scene load.</summary>
        internal bool TexturesRebuiltSinceLoad => _texturesRebuiltSinceLoad;

        /// <summary>True in the editor when the instance set has changes that the scene file does not hold.</summary>
        internal bool PlacementSetChanged
        {
            get
            {
#if UNITY_EDITOR
                return _placementSet && UnityEditor.EditorUtility.IsDirty(_placementSet);
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Evaluates the placements of all object stamps and the detail layers of all detail stamps on all terrains.
        /// The terrains and the instance set do not change. Each tile reads the current terrain heights and its texture
        /// layers before the edge copy, like MicroVerse.
        /// </summary>
        public StampPlacements EvaluatePlacements()
        {
            WaitForPendingPlacements();
            using PlacementRun run = BeginPlacements(null);
            while (!run.Step())
            {
            }
            return run.Placements;
        }

        /// <summary>
        /// Evaluates the placements and detail layers of all stamps, writes the placements into the instance set and
        /// the detail layers into the terrains. The world keeps the result for region updates.
        /// </summary>
        public void RebuildPlacements()
        {
            StampPlacements placements = EvaluatePlacements();
            FinishPlacements(new PlacementUpdate { Placements = placements, Region = WorldRect() });
        }

        /// <summary>
        /// Places the stamps again on the terrains that a world XZ rectangle touches. Then writes the instance set
        /// entries of the placed object stamps and, when detail stamps were placed, the detail layers of these
        /// terrains. Without a stored result, for example after a domain reload, it rebuilds all terrains.
        /// </summary>
        /// <param name="include">
        /// The changed modifiers, also disabled ones. Null places all modifiers again. Clear stamps and path clears always
        /// apply. When one detail stamp is included, all detail stamps are, because they share the detail layers.
        /// </param>
        /// <returns>The number of terrains placed.</returns>
        public int UpdatePlacementRegion(Rect worldRegion, Predicate<LoogaPlacementModifier> include = null)
        {
            WaitForPendingPlacements();
            PlacementUpdate update = PlaceRegion(worldRegion, include, true);
            if (update == null) return 0;
            FinishPlacements(update);
            return update.TileCount;
        }

        /// <summary>
        /// Like <see cref="UpdatePlacementRegion"/>, but returns before the GPU readbacks of the placements are done. It
        /// places at most a few terrains; each <see cref="CompletePendingPlacements"/> call places the next few, and
        /// writes the result when all are placed and read back. It first waits for an earlier pending update.
        /// </summary>
        /// <remarks>
        /// A wait for the GPU also waits for the frame that the GPU is still rendering, which in the editor can take much
        /// longer than the placement work.
        /// </remarks>
        /// <returns>The number of terrains placed.</returns>
        public int StartPlacementRegion(Rect worldRegion, Predicate<LoogaPlacementModifier> include = null)
        {
            WaitForPendingPlacements();
            PlacementUpdate update = PlaceRegion(worldRegion, include, false);
            if (update == null) return 0;
            _pendingPlacements = update;
            return update.TileCount;
        }

        /// <summary>True while a placement update of <see cref="StartPlacementRegion"/> waits for GPU readbacks.</summary>
        public bool PlacementsPending => _pendingPlacements != null;

        /// <summary>
        /// Places the next terrains of the pending placement update, and writes the update when all terrains are placed
        /// and their GPU readbacks are done. It does not wait.
        /// </summary>
        /// <returns>True when no placement update is pending.</returns>
        public bool CompletePendingPlacements()
        {
            if (_pendingPlacements == null) return true;
            PlacementUpdate update = _pendingPlacements;
            try
            {
                // Finished readbacks first: they free the readback budget for the next terrains.
                _placementEvaluator.CompleteReadbacks();
                if (!StepPlacements(update, PlacementTilesPerCall)) return false;
            }
            catch
            {
                _pendingPlacements = null;
                throw;
            }
            if (_placementEvaluator.IsPending(update.Placements)) return false;
            _pendingPlacements = null;
            FinishPlacements(update);
            return true;
        }

        /// <summary>
        /// Drops the pending placement update. Its GPU readbacks finish without a wait, and their results are not used.
        /// </summary>
        public void CancelPendingPlacements()
        {
            if (_pendingPlacements == null) return;
            _placementEvaluator?.Drop(_pendingPlacements.Placements);
            _pendingPlacements = null;
        }

        // Waits for the pending placement update and writes it. Synchronous updates call it first, so that an older
        // result does not replace a newer one.
        internal void WaitForPendingPlacements()
        {
            if (_pendingPlacements == null) return;
            StepPlacements(_pendingPlacements, int.MaxValue);
            _placementEvaluator.CompleteReadbacks(true);
            CompletePendingPlacements();
        }

        // A placement evaluation and the stored results that it replaces.
        private sealed class PlacementUpdate
        {
            // The evaluation while terrains remain to place, otherwise null.
            internal PlacementRun Run;
            internal StampPlacements Placements;
            internal Rect Region;
            internal int TileCount;
            // Null replaces all stored results.
            internal List<UnityEngine.Terrain> Terrains;
            internal HashSet<LoogaObjectStamp> Stamps;
            internal bool Details;
        }

        // Places the stamps on the terrains that the region touches. Null when it touches no terrain.
        private PlacementUpdate PlaceRegion(Rect worldRegion, Predicate<LoogaPlacementModifier> include, bool wait)
        {
            if (_storedPlacements == null)
            {
                // The rebuild waits for its readbacks: it runs once after a reload, and open readbacks of all terrains
                // across frames need much more GPU memory.
                using PlacementRun rebuild = BeginPlacements(null, null, true);
                while (!rebuild.Step())
                {
                }
                return new PlacementUpdate { Placements = rebuild.Placements, Region = WorldRect(),
                    TileCount = rebuild.TileCount };
            }
            // The stamps whose stored result changes: included stamps, also disabled ones, and destroyed stamps.
            var stamps = new HashSet<LoogaObjectStamp>();
            bool details = false;
            bool sourceChanged = include == null;
            bool readers = false;
            var all = new List<LoogaPlacementModifier>();
            GetComponentsInChildren(true, all);
            foreach (LoogaPlacementModifier modifier in all)
            {
                readers |= modifier.isActiveAndEnabled && ReadsPlacementFields(modifier);
                if (include != null && !include(modifier)) continue;
                if (modifier is LoogaObjectStamp stamp)
                {
                    stamps.Add(stamp);
                    sourceChanged |= stamp.IsFieldSource;
                }
                details |= modifier is LoogaDetailStamp;
            }
            foreach (LoogaObjectStamp stored in _storedPlacements.Stamps)
            {
                if (!stored)
                {
                    // A destroyed stamp can have been a field source.
                    stamps.Add(stored);
                    sourceChanged = true;
                }
            }
            // A changed field source moves the placement fields around its placements. The stamps that read them
            // place again, also on the terrains next to the region.
            if (readers && sourceChanged)
            {
                worldRegion = Grow(worldRegion, PlacementFieldMaps.UpdateMargin);
                foreach (LoogaPlacementModifier modifier in all)
                {
                    if (!ReadsPlacementFields(modifier)) continue;
                    if (modifier is LoogaObjectStamp reader)
                    {
                        stamps.Add(reader);
                    }
                    details |= modifier is LoogaDetailStamp;
                }
            }
            List<StampTile> tiles = GetTiles(worldRegion);
            if (tiles.Count == 0) return null;

            PlacementRun run = BeginPlacements(modifier => modifier is LoogaClearStamp ||
                (modifier is LoogaObjectStamp stamp && stamps.Contains(stamp)) ||
                (modifier is LoogaDetailStamp && details), tiles, wait);
            var update = new PlacementUpdate
            {
                Run = run,
                Placements = run.Placements,
                Region = worldRegion,
                TileCount = tiles.Count,
                Terrains = tiles.ConvertAll(tile => tile.Terrain),
                Stamps = stamps,
                Details = details
            };
            StepPlacements(update, wait ? int.MaxValue : PlacementTilesPerCall);
            return update;
        }

        // Places up to a number of further terrains of an update. True when all terrains are placed.
        private static bool StepPlacements(PlacementUpdate update, int count)
        {
            for (int placed = 0; update.Run != null && placed < count; placed++)
            {
                if (!update.Run.Step()) continue;
                update.Run.Dispose();
                update.Run = null;
            }
            return update.Run == null;
        }

        // Stores the placements of an update and writes them into the instance set and the terrains.
        private void FinishPlacements(PlacementUpdate update)
        {
            if (update.Terrains == null)
            {
                var painted = new List<int>(update.Placements.DetailLayers);
                // A layer that the stamps painted before and do not paint now is written empty, so it is cleared.
                if (_storedPlacements != null)
                {
                    foreach (int layer in _storedPlacements.DetailLayers)
                    {
                        update.Placements.RegisterDetailLayer(layer);
                    }
                }
                foreach (int layer in _paintedDetailLayers)
                {
                    update.Placements.RegisterDetailLayer(layer);
                }
                WritePlacements(update.Placements);
                WriteDetails(update.Placements);
                _storedPlacements = update.Placements;
                _paintedDetailLayers = painted;
            }
            else
            {
                // The detail layers that the terrains hold before the update, so only the changed areas are written.
                DetailState previous = update.Details ? new DetailState(_storedPlacements, update.Terrains) : null;
                _storedPlacements.Replace(update.Placements, update.Terrains, update.Stamps, update.Details);
                WritePlacementEntries(_storedPlacements, update.Stamps);
                if (update.Details)
                {
                    WriteDetails(_storedPlacements, update.Terrains, previous);
                    // A region write adds new layers only. The next full write removes the layers of removed stamps.
                    foreach (int layer in update.Placements.DetailLayers)
                    {
                        if (!_paintedDetailLayers.Contains(layer)) _paintedDetailLayers.Add(layer);
                    }
                }
            }
            RefreshPlacementFieldTextures(update);
            PlacementsUpdated?.Invoke(update.Region);
        }

        // Texture stamps read the placement fields from the stored placements. When an update changed the placements of
        // field sources, the textures of these stamps update again. The placements do not read these stamps, so no
        // further update follows.
        private void RefreshPlacementFieldTextures(PlacementUpdate update)
        {
            bool sources = update.Terrains == null;
            if (!sources)
            {
                foreach (LoogaObjectStamp stamp in update.Stamps)
                {
                    sources |= !stamp || stamp.IsFieldSource;
                }
            }
            if (!sources) return;
            Rect region = Grow(update.Region, PlacementFieldMaps.UpdateMargin);
            var modifiers = new List<LoogaTextureModifier>();
            GetTextureModifiers(modifiers);
            Rect? readers = null;
            foreach (LoogaTextureModifier modifier in modifiers)
            {
                if (modifier is not LoogaTextureStamp { ReadsPlacementFields: true }) continue;
                Rect cull = modifier.GetCullRect(HeightMapping.MicroVerseParity);
                if (!cull.Overlaps(region)) continue;
                Rect part = Rect.MinMaxRect(Mathf.Max(cull.xMin, region.xMin), Mathf.Max(cull.yMin, region.yMin),
                    Mathf.Min(cull.xMax, region.xMax), Mathf.Min(cull.yMax, region.yMax));
                readers = readers.HasValue
                    ? Rect.MinMaxRect(Mathf.Min(readers.Value.xMin, part.xMin), Mathf.Min(readers.Value.yMin, part.yMin),
                        Mathf.Max(readers.Value.xMax, part.xMax), Mathf.Max(readers.Value.yMax, part.yMax))
                    : part;
            }
            if (readers.HasValue)
            {
                UpdateTextureRegion(readers.Value);
            }
        }

        /// <summary>
        /// Starts an evaluation that places one terrain for each <see cref="PlacementRun.Step"/> call, so that an editor
        /// tick reads back only one tile.
        /// </summary>
        /// <param name="include">Keeps only the modifiers that it accepts. Null keeps all. Path clears always apply.</param>
        /// <param name="tiles">The terrains to place. Null places all.</param>
        /// <param name="wait">False does not wait for the GPU readbacks, see <see cref="StartPlacementRegion"/>.</param>
        internal PlacementRun BeginPlacements(Predicate<LoogaPlacementModifier> include, List<StampTile> tiles = null,
            bool wait = true)
        {
            var modifiers = new List<LoogaPlacementModifier>();
            GetPlacementModifiers(modifiers);
            if (include != null)
            {
                modifiers.RemoveAll(modifier => !include(modifier));
            }
            var pathClears = new List<LoogaPathClear>();
            GetPathClears(pathClears);
            var textureModifiers = new List<LoogaTextureModifier>();
            GetTextureModifiers(textureModifiers);
            tiles ??= GetTiles(null);
            var ordered = new List<StampTile>(tiles.Count);
            foreach (int i in NextSweep(tiles.Count))
            {
                ordered.Add(tiles[i]);
            }
            return new PlacementRun(this, ordered, modifiers, pathClears, textureModifiers, CreateFieldSources(ordered),
                wait);
        }

        // The placements that the placement fields read during an evaluation of the tiles. The run sets the evaluated
        // placements; the stored result gives the sources on the other terrains.
        private PlacementFieldSources CreateFieldSources(List<StampTile> tiles)
        {
            var sources = new PlacementFieldSources { Stored = _storedPlacements };
            var modifiers = new List<LoogaPlacementModifier>();
            GetPlacementModifiers(modifiers);
            foreach (LoogaPlacementModifier modifier in modifiers)
            {
                if (modifier is LoogaObjectStamp { IsFieldSource: true } stamp)
                {
                    sources.Stamps.Add(stamp);
                }
            }
            foreach (StampTile tile in tiles)
            {
                sources.CurrentTerrains.Add(tile.Terrain);
            }
            return sources;
        }

        /// <summary>True when a placement modifier reads a placement field. A field source ignores them.</summary>
        internal static bool ReadsPlacementFields(LoogaPlacementModifier modifier)
        {
            return modifier switch
            {
                LoogaObjectStamp stamp => !stamp.IsFieldSource &&
                    TerrainFieldMaps.UsesPlacements(stamp.FieldFilter, stamp.FieldFilter2),
                LoogaDetailStamp stamp => TerrainFieldMaps.UsesPlacements(stamp.FieldFilter, stamp.FieldFilter2),
                LoogaClearStamp stamp => TerrainFieldMaps.UsesPlacements(stamp.FieldFilter, stamp.FieldFilter2),
                _ => false
            };
        }

        /// <summary>
        /// Adds the done placement readbacks to their outputs, and waits for them when wait is true. True when no
        /// readback for the output is open.
        /// </summary>
        internal bool CompletePlacementReadbacks(StampPlacements output, bool wait)
        {
            _placementEvaluator?.CompleteReadbacks(wait);
            return _placementEvaluator == null || !_placementEvaluator.IsPending(output);
        }

        // Tile order of the next sweep over the tiles. The spline and water fields of all tiles do not fit the cache
        // budget, so each sweep goes the other way: the fields of the last tiles of one sweep are still in the cache
        // for the first tiles of the next. Tiles are evaluated independently, so the order has no effect on the result.
        private int[] NextSweep(int count)
        {
            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                order[i] = _sweepBackward ? count - 1 - i : i;
            }
            _sweepBackward = !_sweepBackward;
            return order;
        }

        // Places one tile. The texture layers come from a texture evaluation of the tile without the edge copy. The field
        // sources give the placement fields; null turns the placement field filters off.
        internal void PlaceTile(in StampTile tile, List<LoogaPlacementModifier> modifiers, List<LoogaPathClear> pathClears,
            List<LoogaTextureModifier> textureModifiers, StampPlacements output, PlacementFieldSources fieldSources,
            bool wait = true)
        {
            using StampSections.Scope section = StampSections.Measure(StampSection.PlacementTile);
            CreateTextureEvaluator();
            _placementEvaluator ??= new PlacementEvaluator(_evaluator.DistanceFields, _evaluator.WaterFields);
            bool needsLayers = false;
            bool needsFields = false;
            bool needsFlow = false;
            bool needsPlacementFields = false;
            foreach (LoogaPlacementModifier modifier in modifiers)
            {
                needsLayers |= modifier is LoogaObjectStamp { TextureFilterEnabled: true } or
                    LoogaDetailStamp { TextureFilterEnabled: true } or LoogaClearStamp { TextureFilterEnabled: true };
                needsPlacementFields |= fieldSources != null && ReadsPlacementFields(modifier) &&
                    StampMapping.Touches(modifier.GetCullRect(HeightMapping.MicroVerseParity), tile.WorldRect);
                float reach = TerrainFieldReachOf(modifier);
                if (reach <= 0f || !StampMapping.Touches(modifier.GetCullRect(HeightMapping.MicroVerseParity),
                        tile.WorldRect)) continue;
                needsFields = true;
                needsFlow |= reach >= TerrainFieldMaps.FlowReach;
            }
            using HeightSampling heights = CreateHeightSampling(out int normalResolution);
            TextureStampEvaluator.TileLayers layers = null;
            if (needsLayers)
            {
                using (StampSections.Measure(StampSection.PlacementTextureLayers))
                {
                    layers = _textureEvaluator.EvaluateTile(tile, textureModifiers, heights.Get, normalResolution,
                        _fieldMaps);
                }
            }
            if (layers != null)
            {
                _peakTileLayerBytes = Math.Max(_peakTileLayerBytes,
                    2L * layers.Layers.width * layers.Layers.height * 4);
            }
            try
            {
                TerrainFieldMaps.TileFields fields = needsFields ? _fieldMaps.Get(tile, heights.Get, needsFlow) : null;
                Texture placementFields = null;
                if (needsPlacementFields)
                {
                    _placementFieldMaps ??= new PlacementFieldMaps();
                    placementFields = _placementFieldMaps.Build(tile, fieldSources, _terrains);
                }
                _placementEvaluator.EvaluateTile(tile, modifiers, pathClears, PathMaskSize(),
                    heights.Get(tile.Terrain), layers, fields, placementFields, output, wait);
                NoteRefusedReads(tile.WorldRect);
            }
            finally
            {
                layers?.Dispose();
                _fieldMaps.Release();
            }
        }

        // Reach of the field filters of a placement modifier in metres, 0 when it uses none.
        private static float TerrainFieldReachOf(LoogaPlacementModifier modifier)
        {
            return modifier switch
            {
                LoogaObjectStamp stamp => TerrainFieldMaps.ReachOf(stamp.FieldFilter, stamp.FieldFilter2),
                LoogaDetailStamp stamp => TerrainFieldMaps.ReachOf(stamp.FieldFilter, stamp.FieldFilter2),
                LoogaClearStamp stamp => TerrainFieldMaps.ReachOf(stamp.FieldFilter, stamp.FieldFilter2),
                _ => 0f
            };
        }

        /// <summary>
        /// Object clear map of one terrain: R = index of the last covering clear / 256, G = sum of the clear weights.
        /// Null when no modifier clears objects. The caller releases it with RenderTexture.ReleaseTemporary.
        /// </summary>
        /// <param name="include">Keeps only the modifiers that it accepts. Null keeps all.</param>
        internal RenderTexture EvaluateClearMap(UnityEngine.Terrain terrain, Predicate<LoogaPlacementModifier> include)
        {
            CreateTextureEvaluator();
            _placementEvaluator ??= new PlacementEvaluator(_evaluator.DistanceFields, _evaluator.WaterFields);
            var modifiers = new List<LoogaPlacementModifier>();
            GetPlacementModifiers(modifiers);
            if (include != null)
            {
                modifiers.RemoveAll(modifier => !include(modifier));
            }
            using HeightSampling heights = CreateHeightSampling(out _);
            return _placementEvaluator.EvaluateClearMap(new StampTile(terrain), modifiers, heights.Get(terrain));
        }

        /// <summary>
        /// Writes placements into the instance set of the world: one entry for each stamp and prefab. Prototypes with
        /// the same prefab share an entry. Unchanged entries keep their data. Entries of other owners are removed.
        /// </summary>
        public void WritePlacements(StampPlacements placements)
        {
            WritePlacementEntries(placements, placements.Stamps);
        }

        // Writes the entries of some stamps of the placements. Entries of owners that the placements do not contain are
        // removed.
        private void WritePlacementEntries(StampPlacements placements, IEnumerable<LoogaObjectStamp> stamps)
        {
            using StampSections.Scope section = StampSections.Measure(StampSection.InstanceSetWrite);
            InstanceSet set = AcquirePlacementSet();
            ApplyColliderArea(set);
            Matrix4x4 worldToSet = set.transform.worldToLocalMatrix;
            // The lists are reused for each stamp: SetEntry copies the placements, and the other calls only read them.
            Dictionary<GameObject, List<InstanceSet.Placement>> byPrefab = _placementsByPrefab;
            foreach (LoogaObjectStamp stamp in stamps)
            {
                if (!stamp || !placements.Contains(stamp)) continue;
                byPrefab.Clear();
                int usedLists = 0;
                for (int p = 0; p < stamp.Prototypes.Count; p++)
                {
                    GameObject prefab = stamp.Prototypes[p].Prefab;
                    if (!prefab) continue;
                    if (!byPrefab.TryGetValue(prefab, out List<InstanceSet.Placement> list))
                    {
                        if (usedLists == _placementLists.Count) _placementLists.Add(new List<InstanceSet.Placement>());
                        list = _placementLists[usedLists++];
                        list.Clear();
                        byPrefab.Add(prefab, list);
                    }
                    placements.AddConverted(stamp, p, worldToSet, list);
                }
                foreach (KeyValuePair<GameObject, List<InstanceSet.Placement>> pair in byPrefab)
                {
                    // A fixed order lets an unchanged entry match its saved placements.
                    pair.Value.Sort(PlacementOrder);
                    if (!set.MatchesEntry(stamp, pair.Key, pair.Value, stamp.Colliders, stamp.InstanceGroup))
                    {
                        set.SetEntry(stamp, pair.Key, pair.Value, stamp.Colliders, stamp.InstanceGroup);
                    }
                    WriteEffectors(set, stamp, pair.Key, pair.Value);
                }
                set.RemoveOtherPrototypes(stamp, byPrefab.Keys);
            }
            byPrefab.Clear();
            List<UnityEngine.Object> owners = _placementOwners;
            owners.Clear();
            foreach (LoogaObjectStamp stamp in placements.Stamps)
            {
                if (stamp)
                {
                    owners.Add(stamp);
                }
            }
            set.RemoveOtherOwners(owners);
            owners.Clear();
        }

        /// <summary>
        /// Writes the material effector values of one stamp again from the stored placements. A Material Effector calls
        /// it after a change, so the placements stay and only the effector values change.
        /// </summary>
        public void RewriteEffectors(LoogaObjectStamp stamp)
        {
            if (!stamp || _storedPlacements == null || !_storedPlacements.Contains(stamp)) return;

            _rewriteStamps[0] = stamp;
            WritePlacementEntries(_storedPlacements, _rewriteStamps);
            _rewriteStamps[0] = null;
        }

        // The effector values of one stamp and prefab entry. A stamp without an enabled effector clears them.
        private void WriteEffectors(InstanceSet set, LoogaObjectStamp stamp, GameObject prefab,
            List<InstanceSet.Placement> placements)
        {
            bool built = stamp.TryGetComponent(out LoogaMaterialEffector effector) && effector.isActiveAndEnabled &&
                effector.Build(prefab, placements, _effectorProperties, _effectorOperations, _effectorValues);
            if (!built)
            {
                _effectorProperties.Clear();
                _effectorOperations.Clear();
                _effectorValues.Clear();
            }
            if (set.MatchesEntryEffectors(stamp, prefab, _effectorProperties, _effectorOperations, _effectorValues)) return;

            set.SetEntryEffectors(stamp, prefab, _effectorProperties, _effectorOperations, _effectorValues);
        }

        private readonly LoogaObjectStamp[] _rewriteStamps = new LoogaObjectStamp[1];
        private readonly List<string> _effectorProperties = new();
        private readonly List<InstanceEffectorOperation> _effectorOperations = new();
        private readonly List<Vector4> _effectorValues = new();
        // Reused by WritePlacementEntries, so a placement write makes no new lists after the first.
        private readonly Dictionary<GameObject, List<InstanceSet.Placement>> _placementsByPrefab = new();
        private readonly List<List<InstanceSet.Placement>> _placementLists = new();
        private readonly List<UnityEngine.Object> _placementOwners = new();
        // List.Sort with a Comparison wraps it in a new comparer for each call. Both sorts use Array.Sort with a comparer.
        private static readonly IComparer<InstanceSet.Placement> PlacementOrder =
            Comparer<InstanceSet.Placement>.Create(StampPlacements.Compare);

        /// <summary>
        /// Writes the detail layers into the terrains. Each layer that a detail stamp paints is replaced on every owned
        /// terrain. A terrain that no stamp of the layer touches gets an empty layer. Other layers do not change.
        /// </summary>
        public void WriteDetails(StampPlacements placements)
        {
            WriteDetails(placements, _terrains);
        }

        // The detail layers that terrains hold: the stored layers and the painted layer list before an update.
        private sealed class DetailState
        {
            private readonly Dictionary<UnityEngine.Terrain, Dictionary<int, byte[]>> _layers = new();
            private readonly HashSet<int> _painted;

            internal DetailState(StampPlacements placements, IEnumerable<UnityEngine.Terrain> terrains)
            {
                _painted = new HashSet<int>(placements.DetailLayers);
                foreach (UnityEngine.Terrain terrain in terrains)
                {
                    var layers = new Dictionary<int, byte[]>();
                    foreach (int layer in _painted)
                    {
                        if (placements.TryGetDetailLayer(terrain, layer, out byte[] values))
                        {
                            layers[layer] = values;
                        }
                    }
                    _layers[terrain] = layers;
                }
            }

            // False when the terrain layer is not known: a layer that no detail stamp painted can hold other data.
            internal bool TryGet(UnityEngine.Terrain terrain, int layer, out byte[] values)
            {
                values = null;
                if (!_painted.Contains(layer) || !_layers.TryGetValue(terrain, out Dictionary<int, byte[]> layers))
                {
                    return false;
                }
                layers.TryGetValue(layer, out values);
                return true;
            }
        }

        // With a previous state, only the changed area of each layer is written, and the vegetation provider refreshes
        // only that area. A null state writes every layer in full.
        private static void WriteDetails(StampPlacements placements, IEnumerable<UnityEngine.Terrain> terrains,
            DetailState previous = null)
        {
            foreach (UnityEngine.Terrain terrain in terrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                TerrainData data = terrain.terrainData;
                int layerCount = data.detailPrototypes.Length;
                int resolution = data.detailResolution;
                int[,] values = null;
                RectInt? changed = null;
                foreach (int layer in placements.DetailLayers)
                {
                    if (layer >= layerCount) continue;
                    if (previous != null && previous.TryGet(terrain, layer, out byte[] old))
                    {
                        placements.TryGetDetailLayer(terrain, layer, out byte[] current);
                        RectInt area;
                        using (StampSections.Measure(StampSection.DetailConversion))
                        {
                            area = ChangedDetailArea(Valid(old, resolution), Valid(current, resolution), resolution);
                        }
                        if (area.width <= 0) continue;
                        WriteDetailArea(data, layer, Valid(current, resolution), resolution, area);
                        changed = changed.HasValue ? Union(changed.Value, area) : area;
                        continue;
                    }
                    values ??= new int[resolution, resolution];
                    changed = new RectInt(0, 0, resolution, resolution);
                    using (StampSections.Measure(StampSection.DetailConversion))
                    {
                        if (placements.TryGetDetailLayer(terrain, layer, out byte[] bytes) &&
                            bytes.Length == resolution * resolution)
                        {
                            ExpandDetailLayer(bytes, values, resolution);
                        }
                        else
                        {
                            Array.Clear(values, 0, values.Length);
                        }
                    }
                    using (StampSections.Measure(StampSection.DetailWrite))
                    {
                        data.SetDetailLayer(0, 0, layer, values);
                    }
                }
                // Unity reports no detail changes, so the vegetation provider and the grass read the terrain again.
                if (changed.HasValue && terrain.TryGetComponent(
                        out LoogaSoft.Terrain.Instances.TerrainVegetationProvider provider))
                {
                    provider.InvalidateDetails(changed.Value);
                }
                if (changed.HasValue)
                {
                    LoogaSoft.Terrain.Grass.ProceduralGrassRenderer.InvalidateDetails(terrain, changed.Value);
                }
            }
        }

        // A stored layer without the detail resolution counts as empty, as in the full write.
        private static byte[] Valid(byte[] values, int resolution)
        {
            return values != null && values.Length == resolution * resolution ? values : null;
        }

        private static RectInt Union(RectInt a, RectInt b)
        {
            int xMin = Mathf.Min(a.xMin, b.xMin);
            int yMin = Mathf.Min(a.yMin, b.yMin);
            return new RectInt(xMin, yMin, Mathf.Max(a.xMax, b.xMax) - xMin, Mathf.Max(a.yMax, b.yMax) - yMin);
        }

        // Bounds of the texels that differ between two detail layers. A null layer is empty.
        [BurstCompile(CompileSynchronously = true)]
        private unsafe struct ChangedDetailAreaJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public byte* Old;
            [NativeDisableUnsafePtrRestriction] public byte* New;
            public int Width;
            public NativeReference<RectInt> Area;

            public void Execute()
            {
                int xMin = Width, yMin = Width, xMax = -1, yMax = -1;
                for (int y = 0; y < Width; y++)
                {
                    int row = y * Width;
                    for (int x = 0; x < Width; x++)
                    {
                        int before = Old == null ? 0 : Old[row + x];
                        int after = New == null ? 0 : New[row + x];
                        if (before == after) continue;
                        if (x < xMin) xMin = x;
                        if (x > xMax) xMax = x;
                        if (y < yMin) yMin = y;
                        yMax = y;
                    }
                }
                Area.Value = xMax < 0 ? default : new RectInt(xMin, yMin, xMax - xMin + 1, yMax - yMin + 1);
            }
        }

        private static unsafe RectInt ChangedDetailArea(byte[] old, byte[] current, int resolution)
        {
            if (old == null && current == null) return default;
            using var area = new NativeReference<RectInt>(Allocator.TempJob);
            fixed (byte* before = old)
            fixed (byte* after = current)
            {
                new ChangedDetailAreaJob { Old = before, New = after, Width = resolution, Area = area }.Run();
            }
            return area.Value;
        }

        // Writes one area of a detail layer. A null layer writes zeros.
        private static void WriteDetailArea(TerrainData data, int layer, byte[] bytes, int resolution, RectInt area)
        {
            var values = new int[area.height, area.width];
            using (StampSections.Measure(StampSection.DetailConversion))
            {
                if (bytes != null)
                {
                    for (int z = 0; z < area.height; z++)
                    {
                        int row = (area.y + z) * resolution + area.x;
                        for (int x = 0; x < area.width; x++)
                        {
                            values[z, x] = bytes[row + x];
                        }
                    }
                }
            }
            using (StampSections.Measure(StampSection.DetailWrite))
            {
                data.SetDetailLayer(area.x, area.y, layer, values);
            }
        }

        // Copies detail bytes into the int values of SetDetailLayer, one row for each job index.
        [BurstCompile(CompileSynchronously = true)]
        private unsafe struct ExpandDetailJob : IJobParallelFor
        {
            [NativeDisableUnsafePtrRestriction] public byte* Bytes;
            [NativeDisableUnsafePtrRestriction] public int* Values;
            public int Width;

            public void Execute(int row)
            {
                int start = row * Width;
                for (int x = 0; x < Width; x++)
                {
                    Values[start + x] = Bytes[start + x];
                }
            }
        }

        private static unsafe void ExpandDetailLayer(byte[] bytes, int[,] values, int resolution)
        {
            fixed (byte* source = bytes)
            fixed (int* target = &values[0, 0])
            {
                new ExpandDetailJob { Bytes = source, Values = target, Width = resolution }
                    .Schedule(resolution, 32).Complete();
            }
        }

        // Path occlusion mask resolution, like MicroVerse: the first terrain's heightmap resolution - 1 or its alphamap
        // resolution, whichever is larger, clamped to 512..1024.
        private int PathMaskSize()
        {
            foreach (UnityEngine.Terrain terrain in _terrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                TerrainData data = terrain.terrainData;
                return Mathf.Clamp(Mathf.Max(data.heightmapResolution - 1, data.alphamapResolution), 512, 1024);
            }
            return 512;
        }

        private InstanceSet AcquirePlacementSet()
        {
            if (_placementSet) return _placementSet;
            var holder = new GameObject("Looga placements");
            holder.transform.SetParent(transform, false);
            _placementSet = holder.AddComponent<InstanceSet>();
            return _placementSet;
        }

        // Gives the set the collider filter of the collider area. The set makes its collider copies again only when the
        // area splines, the margin or the filter changed.
        private void ApplyColliderArea(InstanceSet set)
        {
            bool changed = _colliderMask.Update(_colliderArea, _colliderAreaMargin);
            _colliderFilter ??= _colliderMask.Contains;
            Func<Vector3, bool> filter = _colliderMask.IsEmpty ? null : _colliderFilter;
            if (set.ColliderFilter == filter && (!changed || filter == null)) return;
            set.SetColliderFilter(filter);
        }
        #endregion
    }

    /// <summary>An evaluation of the placements that places one terrain for each <see cref="Step"/> call.</summary>
    internal sealed class PlacementRun : IDisposable
    {
        private readonly LoogaStampWorld _world;
        private readonly List<StampTile> _tiles;
        private readonly List<LoogaPlacementModifier> _modifiers;
        private readonly List<LoogaPathClear> _pathClears;
        private readonly List<LoogaTextureModifier> _textureModifiers;
        private readonly bool _wait;
        // The placement fields of the run, or null when no stamp of the run reads them.
        private readonly PlacementFieldSources _fieldSources;
        // The first pass places the field sources on all terrains, with the clears that do not read placement fields.
        // The second pass places the other stamps and reads the placements of the first. Null without a second pass.
        private readonly List<LoogaPlacementModifier> _sources;
        private int _next;
        private int _nextSource;
        private bool _sourcesRead;

        internal PlacementRun(LoogaStampWorld world, List<StampTile> tiles, List<LoogaPlacementModifier> modifiers,
            List<LoogaPathClear> pathClears, List<LoogaTextureModifier> textureModifiers,
            PlacementFieldSources fieldSources, bool wait = true)
        {
            _world = world;
            _wait = wait;
            _tiles = tiles;
            _modifiers = modifiers;
            _pathClears = pathClears;
            _textureModifiers = textureModifiers;
            bool sources = false;
            bool readers = false;
            foreach (LoogaPlacementModifier modifier in modifiers)
            {
                if (modifier is LoogaObjectStamp stamp)
                {
                    Placements.Register(stamp);
                    sources |= stamp.IsFieldSource;
                }
                else if (modifier is LoogaDetailStamp detail)
                {
                    Placements.RegisterDetailLayer(detail.DetailLayer);
                }
                readers |= LoogaStampWorld.ReadsPlacementFields(modifier);
            }
            if (!readers || fieldSources == null) return;
            _fieldSources = fieldSources;
            _fieldSources.Current = Placements;
            if (!sources) return;
            _sources = modifiers.FindAll(modifier => modifier is LoogaObjectStamp { IsFieldSource: true } ||
                (modifier is LoogaClearStamp && !LoogaStampWorld.ReadsPlacementFields(modifier)));
            _modifiers = modifiers.FindAll(modifier => modifier is not LoogaObjectStamp { IsFieldSource: true });
        }

        internal StampPlacements Placements { get; } = new StampPlacements();
        internal int TileCount => _tiles.Count;
        internal int PlacedTiles => _sources == null ? _next : (_nextSource + _next) / 2;

        /// <summary>Places the next terrain.</summary>
        /// <returns>True when all terrains are placed.</returns>
        internal bool Step()
        {
            if (_sources != null)
            {
                if (_nextSource < _tiles.Count)
                {
                    _world.PlaceTile(_tiles[_nextSource], _sources, _pathClears, _textureModifiers, Placements, null,
                        _wait);
                    _nextSource++;
                    return false;
                }
                // The second pass reads the placements of the first, so their readbacks must be done.
                if (!_sourcesRead)
                {
                    if (!_world.CompletePlacementReadbacks(Placements, _wait)) return false;
                    _sourcesRead = true;
                }
            }
            if (_next < _tiles.Count)
            {
                _world.PlaceTile(_tiles[_next], _modifiers, _pathClears, _textureModifiers, Placements, _fieldSources,
                    _wait);
                _next++;
            }
            return _next >= _tiles.Count;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Placements of the object stamps of a stamp world: world transforms for each stamp, terrain and prototype index,
    /// as the GPU passes give them. Also the detail layers of the detail stamps: one byte for each detail texel, row = z.
    /// </summary>
    public sealed class StampPlacements
    {
        /// <summary>One placement in world space. The rotation is not normalized.</summary>
        public struct Placement
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

        // For each stamp and terrain: one list for each prototype index.
        private readonly Dictionary<LoogaObjectStamp, Dictionary<UnityEngine.Terrain, List<Placement>[]>> _stamps = new();
        private readonly Dictionary<UnityEngine.Terrain, Dictionary<int, byte[]>> _details = new();
        private readonly SortedSet<int> _detailLayers = new();

        /// <summary>The evaluated stamps.</summary>
        public IEnumerable<LoogaObjectStamp> Stamps => _stamps.Keys;

        /// <summary>The detail layer indices that detail stamps paint.</summary>
        public IEnumerable<int> DetailLayers => _detailLayers;

        /// <summary>Candidates with a prototype index outside the prototype list.</summary>
        public int RejectedCandidates { get; internal set; }

        public int Count
        {
            get
            {
                int count = 0;
                foreach (Dictionary<UnityEngine.Terrain, List<Placement>[]> terrains in _stamps.Values)
                {
                    foreach (List<Placement>[] lists in terrains.Values)
                    {
                        foreach (List<Placement> list in lists)
                        {
                            count += list.Count;
                        }
                    }
                }
                return count;
            }
        }

        /// <summary>The placements of one stamp and prototype index on all terrains. Empty for an unknown stamp.</summary>
        public IReadOnlyList<Placement> Get(LoogaObjectStamp stamp, int prototype)
        {
            if (!_stamps.TryGetValue(stamp, out Dictionary<UnityEngine.Terrain, List<Placement>[]> terrains) ||
                prototype < 0)
            {
                return Array.Empty<Placement>();
            }
            var placements = new List<Placement>();
            foreach (List<Placement>[] lists in terrains.Values)
            {
                if (prototype < lists.Length)
                {
                    placements.AddRange(lists[prototype]);
                }
            }
            return placements;
        }

        // Converts the placements of one stamp and prototype on all terrains into the list, in the order of Get. Get
        // makes a new list for each call.
        internal void AddConverted(LoogaObjectStamp stamp, int prototype, Matrix4x4 worldToSet,
            List<InstanceSet.Placement> result)
        {
            if (!_stamps.TryGetValue(stamp, out Dictionary<UnityEngine.Terrain, List<Placement>[]> terrains) ||
                prototype < 0) return;
            foreach (List<Placement>[] lists in terrains.Values)
            {
                if (prototype >= lists.Length) continue;
                List<Placement> placements = lists[prototype];
                for (int index = 0; index < placements.Count; index++)
                {
                    if (TryConvert(worldToSet, placements[index], out InstanceSet.Placement converted))
                    {
                        result.Add(converted);
                    }
                }
            }
        }

        /// <summary>The placements of one stamp on one terrain, one list for each prototype index, or null.</summary>
        internal List<Placement>[] GetLists(LoogaObjectStamp stamp, UnityEngine.Terrain terrain)
        {
            return _stamps.TryGetValue(stamp, out Dictionary<UnityEngine.Terrain, List<Placement>[]> terrains) &&
                terrains.TryGetValue(terrain, out List<Placement>[] lists) ? lists : null;
        }

        /// <summary>True when the stamp was evaluated, also when it has no placements.</summary>
        public bool Contains(LoogaObjectStamp stamp)
        {
            return _stamps.ContainsKey(stamp);
        }

        /// <summary>
        /// Gets the evaluated detail layer of one terrain: detail resolution^2 values from 0 to 255, row = z. False when
        /// no detail stamp of the layer touches the terrain.
        /// </summary>
        public bool TryGetDetailLayer(UnityEngine.Terrain terrain, int layer, out byte[] values)
        {
            values = null;
            return _details.TryGetValue(terrain, out Dictionary<int, byte[]> layers) &&
                layers.TryGetValue(layer, out values);
        }

        /// <summary>
        /// Converts a placement into instance set space like the MicroVerse placement bridge. The rotation is
        /// normalized. A sheared result returns false.
        /// </summary>
        public static bool TryConvert(Matrix4x4 worldToSet, in Placement placement, out InstanceSet.Placement result)
        {
            Matrix4x4 matrix = worldToSet *
                Matrix4x4.TRS(placement.Position, Quaternion.Normalize(placement.Rotation), placement.Scale);
            Vector3 position = matrix.GetPosition();
            Quaternion rotation = matrix.rotation;
            Vector3 scale = matrix.lossyScale;
            Matrix4x4 rebuilt = Matrix4x4.TRS(position, rotation, scale);
            float tolerance = 0.0001f * Mathf.Max(1, Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y),
                Mathf.Abs(scale.z)));
            result = default;
            for (int column = 0; column < 3; column++)
            {
                Vector4 difference = matrix.GetColumn(column) - rebuilt.GetColumn(column);
                if (difference.sqrMagnitude > tolerance * tolerance) return false;
            }
            result = new InstanceSet.Placement
            {
                Position = position,
                EulerAngles = rotation.eulerAngles,
                Scale = scale
            };
            return true;
        }

        /// <summary>Order of the placements in an entry: x, z, y, then the y angle.</summary>
        public static int Compare(InstanceSet.Placement a, InstanceSet.Placement b)
        {
            int order = a.Position.x.CompareTo(b.Position.x);
            if (order == 0) order = a.Position.z.CompareTo(b.Position.z);
            if (order == 0) order = a.Position.y.CompareTo(b.Position.y);
            if (order == 0) order = a.EulerAngles.y.CompareTo(b.EulerAngles.y);
            return order;
        }

        /// <summary>Adds a stamp without placements, so that a write clears its entries.</summary>
        internal void Register(LoogaObjectStamp stamp)
        {
            if (_stamps.ContainsKey(stamp)) return;
            _stamps.Add(stamp, new Dictionary<UnityEngine.Terrain, List<Placement>[]>());
        }

        /// <summary>
        /// Replaces the results of some stamps on some terrains with a newer evaluation of these terrains. A stamp that
        /// the evaluation does not contain is removed: it is disabled or destroyed.
        /// </summary>
        /// <param name="details">True replaces the detail layers of the terrains and the painted layer list.</param>
        internal void Replace(StampPlacements evaluated, ICollection<UnityEngine.Terrain> terrains,
            IEnumerable<LoogaObjectStamp> stamps, bool details)
        {
            foreach (LoogaObjectStamp stamp in stamps)
            {
                if (!evaluated._stamps.TryGetValue(stamp,
                        out Dictionary<UnityEngine.Terrain, List<Placement>[]> evaluatedTerrains))
                {
                    _stamps.Remove(stamp);
                    continue;
                }
                if (!_stamps.TryGetValue(stamp, out Dictionary<UnityEngine.Terrain, List<Placement>[]> stored))
                {
                    stored = new Dictionary<UnityEngine.Terrain, List<Placement>[]>();
                    _stamps.Add(stamp, stored);
                }
                foreach (UnityEngine.Terrain terrain in terrains)
                {
                    stored.Remove(terrain);
                }
                foreach (KeyValuePair<UnityEngine.Terrain, List<Placement>[]> pair in evaluatedTerrains)
                {
                    stored[pair.Key] = pair.Value;
                }
            }
            RejectedCandidates += evaluated.RejectedCandidates;
            if (!details) return;
            foreach (UnityEngine.Terrain terrain in terrains)
            {
                _details.Remove(terrain);
                if (evaluated._details.TryGetValue(terrain, out Dictionary<int, byte[]> layers))
                {
                    _details[terrain] = layers;
                }
            }
            // Layers that no stamp paints now stay in the list, so the next write clears their old details.
            _detailLayers.UnionWith(evaluated._detailLayers);
        }

        internal void RegisterDetailLayer(int layer)
        {
            _detailLayers.Add(layer);
        }

        internal void SetDetailLayer(UnityEngine.Terrain terrain, int layer, byte[] values)
        {
            RegisterDetailLayer(layer);
            if (!_details.TryGetValue(terrain, out Dictionary<int, byte[]> layers))
            {
                layers = new Dictionary<int, byte[]>();
                _details.Add(terrain, layers);
            }
            layers[layer] = values;
        }

        internal void Add(LoogaObjectStamp stamp, UnityEngine.Terrain terrain, int prototype, Vector3 position,
            Quaternion rotation, Vector3 scale)
        {
            Register(stamp);
            Dictionary<UnityEngine.Terrain, List<Placement>[]> terrains = _stamps[stamp];
            if (!terrains.TryGetValue(terrain, out List<Placement>[] lists))
            {
                lists = new List<Placement>[stamp.Prototypes.Count];
                for (int i = 0; i < lists.Length; i++)
                {
                    lists[i] = new List<Placement>();
                }
                terrains.Add(terrain, lists);
            }
            lists[prototype].Add(new Placement { Position = position, Rotation = rotation, Scale = scale });
        }

#if UNITY_EDITOR
        /// <summary>
        /// Writes the result to a file, for the next script domain or the next scene load. The stamps and terrains are
        /// written as the IDs that the caller gives them.
        /// </summary>
        /// <returns>False when a stamp or a terrain is destroyed: the result cannot be read back.</returns>
        internal bool Write(System.IO.BinaryWriter writer, Func<UnityEngine.Object, int> idOf)
        {
            writer.Write(RejectedCandidates);
            writer.Write(_stamps.Count);
            foreach (KeyValuePair<LoogaObjectStamp, Dictionary<UnityEngine.Terrain, List<Placement>[]>> stamp in _stamps)
            {
                if (!stamp.Key) return false;
                writer.Write(idOf(stamp.Key));
                writer.Write(stamp.Value.Count);
                foreach (KeyValuePair<UnityEngine.Terrain, List<Placement>[]> terrain in stamp.Value)
                {
                    if (!terrain.Key) return false;
                    writer.Write(idOf(terrain.Key));
                    writer.Write(terrain.Value.Length);
                    foreach (List<Placement> list in terrain.Value)
                    {
                        writer.Write(list.Count);
                        foreach (Placement placement in list)
                        {
                            WriteVector(writer, placement.Position);
                            writer.Write(placement.Rotation.x);
                            writer.Write(placement.Rotation.y);
                            writer.Write(placement.Rotation.z);
                            writer.Write(placement.Rotation.w);
                            WriteVector(writer, placement.Scale);
                        }
                    }
                }
            }
            writer.Write(_details.Count);
            foreach (KeyValuePair<UnityEngine.Terrain, Dictionary<int, byte[]>> terrain in _details)
            {
                if (!terrain.Key) return false;
                writer.Write(idOf(terrain.Key));
                writer.Write(terrain.Value.Count);
                foreach (KeyValuePair<int, byte[]> layer in terrain.Value)
                {
                    writer.Write(layer.Key);
                    writer.Write(layer.Value.Length);
                    writer.Write(layer.Value);
                }
            }
            writer.Write(_detailLayers.Count);
            foreach (int layer in _detailLayers)
            {
                writer.Write(layer);
            }
            return true;
        }

        /// <summary>
        /// Reads a result of <see cref="Write"/>. The caller finds the objects of the IDs. Null when a stamp or a terrain
        /// is missing.
        /// </summary>
        internal static StampPlacements Read(System.IO.BinaryReader reader, Func<int, UnityEngine.Object> find)
        {
            var placements = new StampPlacements { RejectedCandidates = reader.ReadInt32() };
            int stampCount = reader.ReadInt32();
            for (int s = 0; s < stampCount; s++)
            {
                var stamp = find(reader.ReadInt32()) as LoogaObjectStamp;
                if (!stamp) return null;
                var terrains = new Dictionary<UnityEngine.Terrain, List<Placement>[]>();
                placements._stamps.Add(stamp, terrains);
                int terrainCount = reader.ReadInt32();
                for (int t = 0; t < terrainCount; t++)
                {
                    var terrain = find(reader.ReadInt32()) as UnityEngine.Terrain;
                    if (!terrain) return null;
                    var lists = new List<Placement>[reader.ReadInt32()];
                    for (int l = 0; l < lists.Length; l++)
                    {
                        int count = reader.ReadInt32();
                        lists[l] = new List<Placement>(count);
                        for (int p = 0; p < count; p++)
                        {
                            lists[l].Add(new Placement
                            {
                                Position = ReadVector(reader),
                                Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                                    reader.ReadSingle()),
                                Scale = ReadVector(reader)
                            });
                        }
                    }
                    terrains.Add(terrain, lists);
                }
            }
            int detailCount = reader.ReadInt32();
            for (int t = 0; t < detailCount; t++)
            {
                var terrain = find(reader.ReadInt32()) as UnityEngine.Terrain;
                if (!terrain) return null;
                var layers = new Dictionary<int, byte[]>();
                placements._details.Add(terrain, layers);
                int layerCount = reader.ReadInt32();
                for (int l = 0; l < layerCount; l++)
                {
                    int layer = reader.ReadInt32();
                    layers.Add(layer, reader.ReadBytes(reader.ReadInt32()));
                }
            }
            int detailLayerCount = reader.ReadInt32();
            for (int l = 0; l < detailLayerCount; l++)
            {
                placements._detailLayers.Add(reader.ReadInt32());
            }
            return placements;
        }

        private static void WriteVector(System.IO.BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static Vector3 ReadVector(System.IO.BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
#endif
    }

    /// <summary>Evaluated alphamaps of a stamp world: one texture for each four layers of each terrain.</summary>
    public sealed class StampAlphamaps : IDisposable
    {
        private readonly UnityEngine.Terrain[] _terrains;
        private readonly RenderTexture[][] _alphamaps;

        internal StampAlphamaps(UnityEngine.Terrain[] terrains, RenderTexture[][] alphamaps)
        {
            _terrains = terrains;
            _alphamaps = alphamaps;
        }

        public IReadOnlyList<UnityEngine.Terrain> Terrains => _terrains;

        /// <summary>The alphamap textures of one terrain, in the order of its alphamap textures.</summary>
        public IReadOnlyList<RenderTexture> GetAlphamaps(int terrain)
        {
            return _alphamaps[terrain];
        }

        /// <summary>Reads one alphamap from the GPU as RGBA bytes, row 0 at z = 0.</summary>
        public byte[] ReadAlphamap(int terrain, int alphamap)
        {
            RenderTexture texture = _alphamaps[terrain][alphamap];
            var buffer = new NativeArray<byte>(texture.width * texture.height * 4, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            try
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref buffer, texture, 0);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    throw new InvalidOperationException("GPU readback failed for " + _terrains[terrain].name + ".");
                }
                return buffer.ToArray();
            }
            finally
            {
                buffer.Dispose();
            }
        }

        /// <summary>Copies the alphamaps into the terrains on the GPU. The CPU copies synchronize later.</summary>
        public void WriteToTerrains()
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                for (int i = 0; i < _terrains.Length; i++)
                {
                    RenderTexture[] alphamaps = _alphamaps[i];
                    for (int m = 0; m < alphamaps.Length; m++)
                    {
                        RenderTexture.active = alphamaps[m];
                        _terrains[i].terrainData.CopyActiveRenderTextureToTexture(TerrainData.AlphamapTextureName, m,
                            new RectInt(0, 0, alphamaps[m].width, alphamaps[m].height), Vector2Int.zero, true);
                    }
                }
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        public void Dispose()
        {
            foreach (RenderTexture[] alphamaps in _alphamaps)
            {
                if (alphamaps == null) continue;
                foreach (RenderTexture alphamap in alphamaps)
                {
                    RenderTexture.ReleaseTemporary(alphamap);
                }
            }
        }
    }

    /// <summary>Evaluated heights of a stamp world: one 16-bit texture for each terrain.</summary>
    public sealed class StampHeights : IDisposable
    {
        private readonly UnityEngine.Terrain[] _terrains;
        private readonly RenderTexture[] _textures;
        private readonly bool _ownsTextures;
        private readonly ushort[][] _tiles;

        internal StampHeights(UnityEngine.Terrain[] terrains, RenderTexture[] textures, bool ownsTextures)
        {
            _terrains = terrains;
            _textures = textures;
            _ownsTextures = ownsTextures;
            _tiles = new ushort[terrains.Length][];
        }

        /// <summary>
        /// World height at a world XZ position: bilinear between the samples of the first tile that holds the position.
        /// Each tile is read from the GPU on its first use. Returns false outside the tiles.
        /// </summary>
        public bool TrySampleHeight(Vector2 world, out float height)
        {
            for (int index = 0; index < _terrains.Length; index++)
            {
                TerrainData data = _terrains[index].terrainData;
                Vector3 position = _terrains[index].GetPosition();
                Vector3 size = data.size;
                int resolution = _textures[index].width;
                float x = (world.x - position.x) / size.x * (resolution - 1);
                float z = (world.y - position.z) / size.z * (resolution - 1);
                if (x < 0f || z < 0f || x > resolution - 1 || z > resolution - 1) continue;

                _tiles[index] ??= ReadTile(index);
                ushort[] values = _tiles[index];
                int column = Mathf.Min((int)x, resolution - 2);
                int row = Mathf.Min((int)z, resolution - 2);
                float fx = x - column;
                float fz = z - row;
                int sample = row * resolution + column;
                float lower = Mathf.Lerp(values[sample], values[sample + 1], fx);
                float upper = Mathf.Lerp(values[sample + resolution], values[sample + resolution + 1], fx);
                height = position.y + Mathf.Lerp(lower, upper, fz) / 32766f * size.y;
                return true;
            }
            height = 0f;
            return false;
        }

        public IReadOnlyList<UnityEngine.Terrain> Terrains => _terrains;
        public IReadOnlyList<RenderTexture> Textures => _textures;

        /// <summary>
        /// Reads one tile from the GPU. Value 32766 is the full terrain height, like the 16-bit terrain heightmap.
        /// </summary>
        public ushort[] ReadTile(int index)
        {
            var values = new ushort[_textures[index].width * _textures[index].height];
            ReadTile(index, values);
            return values;
        }

        /// <summary>
        /// Reads one tile from the GPU into an array of width x height values, so a caller can reuse its arrays.
        /// </summary>
        public void ReadTile(int index, ushort[] values)
        {
            RenderTexture texture = _textures[index];
            if (values.Length != texture.width * texture.height)
            {
                throw new ArgumentException("The array must hold width x height values.", nameof(values));
            }
            var buffer = new NativeArray<ushort>(values.Length, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            try
            {
                ReadTile(index, buffer);
                buffer.CopyTo(values);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        /// <summary>
        /// Reads one tile from the GPU into a native array of width x height values. The caller owns the array.
        /// </summary>
        public void ReadTile(int index, NativeArray<ushort> values)
        {
            // The readback goes into a buffer of the caller. Unity's own readback buffers come from the frame
            // allocator, and many large reads in one frame can run out of memory.
            RenderTexture texture = _textures[index];
            if (values.Length != texture.width * texture.height)
            {
                throw new ArgumentException("The array must hold width x height values.", nameof(values));
            }
            AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref values, texture, 0);
            request.WaitForCompletion();
            if (request.hasError)
            {
                throw new InvalidOperationException("GPU readback failed for " + _terrains[index].name + ".");
            }
        }

        /// <summary>Copies the heights into the terrains and synchronizes the CPU heightmaps.</summary>
        public void WriteToTerrains()
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                for (int i = 0; i < _terrains.Length; i++)
                {
                    TerrainData data = _terrains[i].terrainData;
                    int resolution = data.heightmapResolution;
                    RenderTexture.active = _textures[i];
                    data.CopyActiveRenderTextureToHeightmap(new RectInt(0, 0, resolution, resolution),
                        Vector2Int.zero, TerrainHeightmapSyncControl.HeightAndLod);
                }
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        public void Dispose()
        {
            if (!_ownsTextures) return;
            foreach (RenderTexture texture in _textures)
            {
                if (!texture) continue;
                texture.Release();
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
