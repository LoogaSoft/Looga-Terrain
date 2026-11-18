using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Surface
{
    /// <summary>
    /// Draws a native Unity terrain with a Looga terrain surface. All terrains share the surface material. The
    /// per-tile control maps go through the terrain splat property block. When the terrain has a Looga
    /// <see cref="TerrainRenderer"/>, the renderer draws the terrain with the patch target of the surface and the native
    /// draw is the fallback. Disable the component to restore the terrain material and base map distance.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(UnityEngine.Terrain))]
    [AddComponentMenu("Looga Terrain/Terrain Surface Binding")]
    public sealed class LoogaTerrainSurfaceBinding : MonoBehaviour
    {
        private const int MaxControls = LoogaTerrainSurface.MaxLayers / 4;
        private static readonly int[] ControlIds = CreateControlIds();
        private static readonly int ControlCountId = Shader.PropertyToID("_LoogaControlCount");
        private static readonly int SurfaceFieldsId = Shader.PropertyToID("_LoogaSurfaceFields");
        private static readonly int ControlTexelSizeId = Shader.PropertyToID("_LoogaControlTexelSize");
        private static readonly int TerrainSizeId = Shader.PropertyToID("_LoogaTerrainSize");
        private static readonly int ControlPackedId = Shader.PropertyToID("_LoogaControlPacked");
        private static readonly int ControlIndexId = Shader.PropertyToID("_LoogaControlIndex");
        private static readonly int ControlWeightId = Shader.PropertyToID("_LoogaControlWeight");

        [SerializeField] private LoogaTerrainSurface _surface;
        [Tooltip("Unity draws its own base map beyond this distance. The surface shades every distance itself, " +
                 "so the binding moves the base map out of view. Large terrains, such as a backdrop, need a distance " +
                 "beyond their size.")]
        [SerializeField] private float _basemapDistance = 100000f;
        // Base map distance before the binding changed it. A negative value means that nothing is changed.
        [SerializeField, HideInInspector] private float _restoreBasemapDistance = -1f;
        [Tooltip("Draw through the Looga TerrainRenderer of this terrain when it has one. Off: the native terrain draw.")]
        [SerializeField] private bool _useTerrainRenderer = true;
        [Tooltip("Read packed control maps: the 4 strongest layers of each texel, in a quarter of the memory of the " +
                 "control maps. A player build then ships only the packed maps. Off: the surface reads the control " +
                 "maps.")]
        [SerializeField] private bool _packControls = true;
        // Packed control maps of a player build (LoogaPackedControlsPlayerBuild). The build also gives the terrain
        // data small control maps, so a player reads only these. Null in the editor: it packs the control maps itself.
        [SerializeField, HideInInspector] private Texture2D _shippedIndex;
        [SerializeField, HideInInspector] private Texture2D _shippedWeight;

        // Runtime state. A script reload keeps private fields, but it drops the queued editor call and the event
        // subscription, so this state must start empty after a reload.
        [System.NonSerialized] private UnityEngine.Terrain _terrain;
        [System.NonSerialized] private LoogaTerrainSurface _subscribedSurface;
        [System.NonSerialized] private MaterialPropertyBlock _block;
        [System.NonSerialized] private MaterialPropertyBlock _probe;
        [System.NonSerialized] private Material _appliedMaterial;
        [System.NonSerialized] private readonly Texture2D[] _boundControls = new Texture2D[MaxControls];
        [System.NonSerialized] private bool _applyQueued;
        [System.NonSerialized] private TerrainRenderer _renderer;
        [System.NonSerialized] private TerrainLayer[] _appliedLayers;
        [System.NonSerialized] private LoogaPackedControls _packed;
        // Control map references and update counts of the last pack. A different value packs again.
        [System.NonSerialized] private ulong _packedRevision;

        /// <summary>
        /// Occurs after a binding applies its surface to a terrain, or removes it. Shaders that read the terrain layers,
        /// such as the rock faces, bind the surface data again.
        /// </summary>
        public static event System.Action<UnityEngine.Terrain> SurfaceChanged;

        /// <summary>Gets the reason why the surface is not applied. Empty when it is applied.</summary>
        public string Diagnostic { get; private set; } = string.Empty;
        /// <summary>Gets a value that tells if the terrain draws with the surface material.</summary>
        public bool IsApplied => _appliedMaterial;
        public LoogaTerrainSurface Surface => _surface;
        /// <summary>
        /// Gets the packed control maps that the surface reads, or null when the binding reads the control maps.
        /// </summary>
        public LoogaPackedControls PackedControls =>
            _appliedMaterial && _packed != null && _packed.IsValid ? _packed : null;
        /// <summary>True when a Looga TerrainRenderer draws the terrain with the surface.</summary>
        public bool DrawsWithTerrainRenderer => _renderer && _renderer.IsReady;
        /// <summary>
        /// Reads packed control maps (<see cref="LoogaPackedControls"/>) instead of the control maps. A player build
        /// ships only the packed maps of such a terrain.
        /// </summary>
        public bool PackControls
        {
            get => _packControls;
            set
            {
                _packControls = value;
                if (!isActiveAndEnabled) return;

                Apply();
                // Cached pages hold the surface of the other read.
                if (TryGetComponent(out TerrainVirtualTexture cache))
                {
                    cache.Invalidate();
                }
            }
        }

        /// <summary>Draws through the Looga TerrainRenderer of the terrain when it has one.</summary>
        public bool UseTerrainRenderer
        {
            get => _useTerrainRenderer;
            set
            {
                _useTerrainRenderer = value;
                if (isActiveAndEnabled)
                {
                    Apply();
                }
            }
        }

        #region Built-in
        private void OnEnable()
        {
            _applyQueued = false;
            _terrain = GetComponent<UnityEngine.Terrain>();
            TerrainCallbacks.textureChanged += OnTerrainTextureChanged;
            LoogaTerrainSurfaceFields.MapChanged += OnFieldMapChanged;
            TerrainRenderer.Enabling += OnRendererEnabling;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += CheckControls;
#endif
            Apply();
        }

        private void OnDisable()
        {
            TerrainCallbacks.textureChanged -= OnTerrainTextureChanged;
            LoogaTerrainSurfaceFields.MapChanged -= OnFieldMapChanged;
            TerrainRenderer.Enabling -= OnRendererEnabling;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= CheckControls;
#endif
            CancelQueuedApply();
            Subscribe(null);
            Remove();
        }

        private void OnValidate()
        {
            QueueApply();
        }
        #endregion

        /// <summary>Sets the surface and applies it to the terrain.</summary>
        public void SetSurface(LoogaTerrainSurface surface)
        {
            _surface = surface;
            if (isActiveAndEnabled)
            {
                Apply();
            }
        }

        /// <summary>
        /// Checks the terrain layers against the surface, then assigns the surface material and the control maps.
        /// A failed check restores the terrain and writes the reason to <see cref="Diagnostic"/>.
        /// </summary>
        public void Apply()
        {
            if (!_terrain) return;
            Subscribe(_surface);
            // A binding without a surface is not configured yet. That is not an error, so it does not log a warning.
            if (!_surface)
            {
                Diagnostic = "Assign a Looga terrain surface.";
                Remove();
                return;
            }
            TerrainData data = _terrain.terrainData;
            if (!data)
            {
                Fail("The terrain has no terrain data.");
                return;
            }
            TerrainLayer[] terrainLayers = data.terrainLayers;
            if (!_surface.MatchesLayers(terrainLayers, out string diagnostic))
            {
                Fail(diagnostic);
                return;
            }
            // After a layer change the editor rebuilds the arrays in the background. The terrain draws with the old
            // arrays until then. The shader clamps the slice index, so a new layer shows another layer meanwhile.
            bool waitForArrays = _surface.HasTextureArrays && !_surface.ArraysCoverLayers;
            if (!waitForArrays && !_surface.Matches(terrainLayers, out diagnostic))
            {
                Fail(diagnostic);
                return;
            }
            Material material = _surface.NativeMaterial;
            if (!material)
            {
                Fail("The native surface shader '" + LoogaTerrainSurface.NativeShaderName + "' is missing or not supported.");
                return;
            }
            if (data.alphamapTextureCount > MaxControls)
            {
                Fail("The terrain has more than " + MaxControls + " control maps.");
                return;
            }

            if (_terrain.materialTemplate != material)
            {
                _terrain.materialTemplate = material;
            }
            if (_restoreBasemapDistance < 0f)
            {
                _restoreBasemapDistance = _terrain.basemapDistance;
            }
            if (!Mathf.Approximately(_terrain.basemapDistance, _basemapDistance))
            {
                _terrain.basemapDistance = _basemapDistance;
            }
            _appliedMaterial = material;
            _appliedLayers = terrainLayers;
            LoogaTerrainSurfaceFields.SetWanted(_terrain, _surface.Fields.IsActive);
            BindControls(true);
            UpdateRenderer();
            Diagnostic = waitForArrays ? "Waiting for the texture arrays of the changed layers." : string.Empty;
            SurfaceChanged?.Invoke(_terrain);
        }

        /// <summary>
        /// Gives the surface of each bound terrain the layer list of that terrain, then applies the bindings again. A
        /// stamp world calls this after it changes the terrain layer lists. A surface takes the list of the first
        /// terrain that uses it. A terrain with another list then reports the mismatch in <see cref="Diagnostic"/>.
        /// </summary>
        public static void SyncSurfaceLayers(IEnumerable<UnityEngine.Terrain> terrains)
        {
            var bindings = new List<LoogaTerrainSurfaceBinding>();
            foreach (UnityEngine.Terrain terrain in terrains)
            {
                if (terrain && terrain.terrainData && terrain.TryGetComponent(out LoogaTerrainSurfaceBinding binding)
                    && binding.isActiveAndEnabled && binding._surface)
                {
                    bindings.Add(binding);
                }
            }
            var synced = new HashSet<LoogaTerrainSurface>();
            foreach (LoogaTerrainSurfaceBinding binding in bindings)
            {
                if (synced.Add(binding._surface))
                {
                    binding._surface.SyncLayers(binding._terrain.terrainData.terrainLayers);
                }
            }
            foreach (LoogaTerrainSurfaceBinding binding in bindings)
            {
                binding.Apply();
            }
        }

        // OnValidate runs during serialization. Changing the terrain material there is not safe, so the editor waits
        // one update. The wait uses EditorApplication.update, not delayCall: a delayCall that is dropped (seen when the
        // change came from an editor tool command) left the flag set, so the binding ignored all later changes.
        private void QueueApply()
        {
            if (!isActiveAndEnabled || _applyQueued) return;
#if UNITY_EDITOR
            _applyQueued = true;
            UnityEditor.EditorApplication.update += ApplyQueued;
#else
            Apply();
#endif
        }

        private void ApplyQueued()
        {
            CancelQueuedApply();
            if (this && isActiveAndEnabled)
            {
                Apply();
            }
        }

        private void CancelQueuedApply()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= ApplyQueued;
#endif
            _applyQueued = false;
        }

        private void Fail(string diagnostic)
        {
            if (Diagnostic != diagnostic)
            {
                UnityEngine.Debug.LogWarning("Looga terrain surface is not applied to '" + name + "': " + diagnostic, this);
            }
            Diagnostic = diagnostic;
            Remove();
        }

        // Restores the native terrain state. Another component, such as a MicroSplat terrain, can then set its own material.
        private void Remove()
        {
            if (_terrain)
            {
                LoogaTerrainSurfaceFields.SetWanted(_terrain, false);
                if (_appliedMaterial)
                {
                    _terrain.SetSplatMaterialPropertyBlock(new MaterialPropertyBlock());
                    if (_terrain.materialTemplate == _appliedMaterial)
                    {
                        _terrain.materialTemplate = null;
                    }
                }
                if (_restoreBasemapDistance >= 0f)
                {
                    _terrain.basemapDistance = _restoreBasemapDistance;
                    _restoreBasemapDistance = -1f;
                }
            }
            bool wasApplied = _appliedMaterial;
            _appliedMaterial = null;
            _appliedLayers = null;
            System.Array.Clear(_boundControls, 0, _boundControls.Length);
            _packed?.Release();
            _packed = null;
            _packedRevision = 0;
            ReleaseRenderer();
            if (wasApplied && _terrain)
            {
                SurfaceChanged?.Invoke(_terrain);
            }
        }

        #region Control maps
        // The terrain can replace its alphamap textures, for example after a resolution change. The binding compares
        // the references and sets the property block only when they change. It packs the control maps again when their
        // references or contents change. Returns true when the bound maps changed.
        private bool BindControls(bool force, RectInt? changedRegion = null)
        {
            Texture2D[] controls = _terrain.terrainData.alphamapTextures;
            bool changed = force;
            for (int index = 0; index < MaxControls; index++)
            {
                Texture2D control = index < controls.Length ? controls[index] : null;
                changed |= _boundControls[index] != control;
                _boundControls[index] = control;
            }
            changed |= RefreshPacked(controls, changedRegion);
            if (!changed) return false;

            _block ??= new MaterialPropertyBlock();
            _block.Clear();
            FillTileValues(_block, controls.Length);
            _terrain.SetSplatMaterialPropertyBlock(_block);
            return true;
        }

        // Packs the control maps when they changed, or takes the shipped maps of a player build. A changed region packs
        // only that region. Returns true when the packed map objects changed.
        private bool RefreshPacked(Texture2D[] controls, RectInt? changedRegion)
        {
            if (!_packControls)
            {
                bool hadMaps = _packed != null && _packed.IsValid;
                _packed?.Release();
                _packedRevision = 0;
                return hadMaps;
            }
            _packed ??= new LoogaPackedControls();
            if (_shippedIndex && _shippedWeight)
            {
                return _packed.UseShipped(_shippedIndex, _shippedWeight);
            }
            ulong revision = ControlRevision(controls);
            bool complete = revision != _packedRevision || !_packed.IsValid;
            if (!complete && !changedRegion.HasValue) return false;

            _packedRevision = revision;
            return complete ? _packed.Pack(controls) : _packed.Pack(controls, changedRegion.Value);
        }

        // Changes when a control map is replaced or its CPU data is applied. A GPU write, such as a stamp world write,
        // does not change it: the texture callback reports that write with its region.
        private static ulong ControlRevision(Texture2D[] controls)
        {
            ulong revision = (ulong)controls.Length + 1;
            foreach (Texture2D control in controls)
            {
                revision = unchecked(revision * 397 ^ (control ? (ulong)(uint)control.GetInstanceID() : 0));
                revision = unchecked(revision * 397 ^ (control ? control.updateCount : 0));
            }
            return revision;
        }

        /// <summary>
        /// Writes the per-tile surface values of the terrain to a block: control maps or packed control maps, control
        /// map size, surface fields and terrain size. The block can hold other values, for example a renderer's own, so
        /// it is not cleared. Writes nothing when the surface is not applied.
        /// </summary>
        public void FillTileValues(MaterialPropertyBlock block)
        {
            if (!_appliedMaterial || !_terrain || !_terrain.terrainData) return;

            FillTileValues(block, _terrain.terrainData.alphamapTextureCount);
        }

        // The per-tile surface values. The block can hold other values, for example the renderer's own, so it is not
        // cleared here.
        private void FillTileValues(MaterialPropertyBlock block, int controlCount)
        {
            for (int index = 0; index < MaxControls; index++)
            {
                block.SetTexture(ControlIds[index], _boundControls[index] ? _boundControls[index] : Texture2D.blackTexture);
            }
            block.SetFloat(ControlCountId, controlCount);
            block.SetTexture(SurfaceFieldsId, LoogaTerrainSurfaceFields.Get(_terrain));
            // The packed maps have the size of the control maps. A player has small control maps, so the size comes
            // from the packed maps when the surface reads them.
            LoogaPackedControls packed = _packed != null && _packed.IsValid ? _packed : null;
            Texture first = packed != null ? packed.Index : _boundControls[0];
            block.SetVector(ControlTexelSizeId, first
                ? new Vector4(1f / first.width, 1f / first.height, first.width, first.height)
                : new Vector4(1f, 1f, 1f, 1f));
            block.SetVector(ControlPackedId, packed != null ? new Vector4(1f, packed.MipCount, 0f, 0f) : Vector4.zero);
            block.SetTexture(ControlIndexId, packed != null ? packed.Index : Texture2D.blackTexture);
            block.SetTexture(ControlWeightId, packed != null ? packed.Weight : Texture2D.blackTexture);
            // Control UV noise converts its offset in meters to the 0 to 1 terrain UV.
            Vector3 size = _terrain.terrainData.size;
            block.SetVector(TerrainSizeId, new Vector4(size.x, size.z, 1f / Mathf.Max(size.x, 0.001f),
                1f / Mathf.Max(size.z, 0.001f)));
        }

        // A save of the terrain data asset clears the textures in the terrain splat property block, and no terrain
        // callback reports it. Without this check a natively drawn terrain has no control maps and shows only its first
        // layer.
        private void CheckControls()
        {
            if (!_appliedMaterial || !_terrain || !_terrain.terrainData) return;
            _probe ??= new MaterialPropertyBlock();
            _terrain.GetSplatMaterialPropertyBlock(_probe);
            if (!_probe.GetTexture(ControlIds[0]))
            {
                BindControls(true);
                SurfaceChanged?.Invoke(_terrain);
                return;
            }
            // A replaced control map or a CPU write packs the maps again.
            if (BindControls(false))
            {
                SurfaceChanged?.Invoke(_terrain);
            }
        }

        // A new layer list also writes the alphamaps. The binding then checks the layers again. The check waits one
        // editor update: a stamp world changes the terrain first and gives the surface the new list right after.
        private void OnTerrainTextureChanged(UnityEngine.Terrain terrain, string textureName, RectInt region, bool synched)
        {
            if (terrain != _terrain || !_appliedMaterial || textureName != TerrainData.AlphamapTextureName) return;
            if (!SameLayers(_terrain.terrainData.terrainLayers))
            {
                QueueApply();
                return;
            }
            // The callback comes right after the write, before the next render, so the packed maps are current when
            // the terrain and its cached pages draw again.
            if (BindControls(false, region))
            {
                SurfaceChanged?.Invoke(_terrain);
            }
        }

        // Meshes over the ground make their block again when the surface data changes.
        private void OnFieldMapChanged(UnityEngine.Terrain terrain)
        {
            if (terrain != _terrain || !_appliedMaterial) return;
            BindControls(true);
            SurfaceChanged?.Invoke(_terrain);
        }

        private bool SameLayers(TerrainLayer[] terrainLayers)
        {
            if (_appliedLayers == null || _appliedLayers.Length != terrainLayers.Length) return false;
            for (int index = 0; index < terrainLayers.Length; index++)
            {
                if (_appliedLayers[index] != terrainLayers[index]) return false;
            }
            return true;
        }

        private static int[] CreateControlIds()
        {
            var ids = new int[MaxControls];
            for (int index = 0; index < ids.Length; index++)
            {
                ids[index] = Shader.PropertyToID("_Control" + index);
            }
            return ids;
        }
        #endregion

        #region Terrain renderer
        // The renderer draws GPU-selected patches with the patch target. It takes the surface material as an override
        // and the per-tile values through its own property block, so the MicroSplat bridge on the same terrain stands
        // down. The native material stays assigned: it draws while the renderer is not ready.
        private void UpdateRenderer()
        {
            TerrainRenderer renderer = null;
            Material patches = null;
            if (_useTerrainRenderer && TryGetComponent(out renderer) && renderer.isActiveAndEnabled)
            {
                patches = _surface.PatchesMaterial;
            }
            if (!patches)
            {
                ReleaseRenderer();
                return;
            }
            if (_renderer != renderer)
            {
                ReleaseRenderer();
                _renderer = renderer;
                _renderer.BindMaterialProperties += FillRendererValues;
            }
            _renderer.SetSurfaceOverride(this, patches);
            // The renderer copies the surface values into its own material.
            _renderer.RefreshSurface();
        }

        // A renderer that is enabled after this binding, for example one below it on the object, takes the surface now.
        // Without this, the renderer drew the exported surface asset after each scene load.
        private void OnRendererEnabling(TerrainRenderer renderer)
        {
            if (renderer.gameObject != gameObject || !_appliedMaterial) return;

            UpdateRenderer();
        }

        private void FillRendererValues(MaterialPropertyBlock block)
        {
            if (!_terrain || !_terrain.terrainData) return;
            // The renderer binds after a control-map change, so the references are read again first.
            BindControls(false);
            FillTileValues(block, _terrain.terrainData.alphamapTextureCount);
        }

        private void ReleaseRenderer()
        {
            if (!_renderer) return;
            _renderer.BindMaterialProperties -= FillRendererValues;
            _renderer.SetSurfaceOverride(this, null);
            _renderer = null;
        }
        #endregion

        #region Surface changes
        private void Subscribe(LoogaTerrainSurface surface)
        {
            if (_subscribedSurface == surface) return;
            if (_subscribedSurface)
            {
                _subscribedSurface.Changed -= OnSurfaceChanged;
            }
            _subscribedSurface = surface;
            if (_subscribedSurface)
            {
                _subscribedSurface.Changed += OnSurfaceChanged;
            }
        }

        private void OnSurfaceChanged()
        {
            // The surface can change during its own OnValidate. Apply after it.
            QueueApply();
            // Cached pages hold the surface with its old settings, for example the old blend mode. Without this the far
            // terrain kept the old look until another change invalidated the pages.
            if (TryGetComponent(out TerrainVirtualTexture cache))
            {
                cache.Invalidate();
            }
        }
        #endregion
    }
}
