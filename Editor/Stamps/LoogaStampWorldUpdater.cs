using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    /// <summary>
    /// Updates stamp worlds that have Update In Editor on. Each editor tick compares every height, texture and placement
    /// modifier and every path clear with its last state. A changed stamp marks its old and new culling rectangles
    /// dirty. A new or removed stamp marks only its own culling rectangle, but a stamp that moves to a different place in
    /// the stamp order updates all terrains. An update that covers most terrains logs its causes. A changed spline marks only the curves next to the changed knots, widened by the reach of the modifier,
    /// and invalidates the distance fields on those tiles only. A changed water source marks only the region near its
    /// changed edges, for the modifiers that read its water areas. The dirty regions are evaluated in the same tick:
    /// heights first, then textures, then placements. A height change also updates the textures there when a texture
    /// filter reads heights. Height and texture changes place all stamps again on their terrains. A changed object
    /// stamp places only itself again, a changed detail stamp all detail stamps, and a changed clear stamp or path clear
    /// all stamps. A reimported texture counts as a change of every modifier that uses it. An update that a tool
    /// requested (LoogaStampWorld.RequestUpdate) runs like a height change in its region. The CPU heights are
    /// synchronized in the ticks without a new change, one terrain for each tick. The CPU alphamaps are synchronized
    /// when the scene is saved, like MicroVerse. Texture and placement modifiers that wait for their texture source
    /// after a load update together, once no modifier of that set waits any more.
    /// </summary>
    [InitializeOnLoad]
    internal static class LoogaStampWorldUpdater
    {
        // Height changes reach the normal map of the texels next to them, also across a tile edge.
        private const float HeightToTextureMargin = 2f;
        // A tick that runs longer than this is logged by the watchdog, and again after each further interval.
        private const double WatchdogSeconds = 20.0;
        private const double WatchdogInterval = 30.0;
        // A failed placement update starts again this many times, each after the delay, before it is dropped.
        private const int PlacementRetries = 3;
        private const double PlacementRetryDelay = 1.0;
        // Placements start after the GPU checks of the last height write, so their readbacks do not wait in one tick for
        // all queued height work. After this time they start anyway, so a check that does not finish cannot block them.
        private const double HeightCheckWaitSeconds = 2.0;
        // The longest time after a load that the updates of modifiers that waited for their texture source are collected.
        // A source that never gets its texture must not hold the others.
        private const double LoadWaitLimit = 240.0;
        // Terrains that the alphamap check after a trusted load evaluates in one tick. One terrain is about 0.3 s, and
        // the readback of each check waits for a later tick, so a larger group makes the whole check shorter.
        private const int CheckTerrainsPerTick = 4;

        private struct ModifierState
        {
            internal Matrix4x4 Transform;
            internal Matrix4x4 Painted;
            internal int Revision;
            internal int FieldRevision;
            internal bool Active;
            internal bool Texture;
            internal bool Placement;
            internal Rect Cull;
        }

        // Placement work of one tick: the dirty region and the changed modifiers. All places every modifier again.
        private sealed class PlacementChange
        {
            internal Rect? Region;
            internal bool All;
            internal readonly HashSet<LoogaPlacementModifier> Modifiers = new();

            internal void Add(Rect? region, MonoBehaviour modifier)
            {
                if (!region.HasValue) return;
                Region = Union(Region, region);
                if (modifier is LoogaObjectStamp or LoogaDetailStamp)
                {
                    Modifiers.Add((LoogaPlacementModifier)modifier);
                }
                else
                {
                    All = true;
                }
            }

            internal void AddAll(Rect? region)
            {
                if (!region.HasValue) return;
                Region = Union(Region, region);
                All = true;
            }

            internal void Clear()
            {
                Region = null;
                All = false;
                Modifiers.Clear();
            }

            internal void Merge(PlacementChange other)
            {
                if (!other.Region.HasValue) return;
                Region = Union(Region, other.Region);
                All |= other.All;
                Modifiers.UnionWith(other.Modifiers);
            }
        }

        private sealed class ContainerState
        {
            internal Matrix4x4 Transform;
            internal readonly List<BezierKnot[]> Knots = new();
            internal readonly List<bool> Closed = new();
        }

        // Last state of a water source that a used water area lists.
        private sealed class SourceState
        {
            internal bool Active;
            internal int Revision;
            internal int Mesh;
            internal int VertexCount;
            internal Matrix4x4 Transform;
            internal Vector2 HeightRange;
            internal Rect Bounds;
            internal readonly HashSet<EdgeKey> Edges = new();
            internal readonly List<LoogaWaterArea> Areas = new();
        }

        private sealed class WorldState
        {
            internal readonly Dictionary<MonoBehaviour, ModifierState> Modifiers = new();
            internal readonly Dictionary<LoogaWaterSource, SourceState> Sources = new();
            internal readonly Dictionary<LoogaWaterArea, int> AreaRevisions = new();
            internal readonly Dictionary<SplineContainer, ContainerState> Containers = new();
            internal readonly List<MonoBehaviour> HeightOrder = new();
            internal readonly List<MonoBehaviour> TextureOrder = new();
            internal readonly List<MonoBehaviour> PlacementOrder = new();
            internal bool SyncPending;
            // True after the first change capture. See TickWorlds.
            internal bool Captured;
            // Height modifiers that waited for their inputs at the first capture after a load, for example for the live
            // cliff textures, which are not saved. The saved heights hold their result, so their first active state
            // writes no heights. Textures and placements are not covered: the saved ones can differ from the stamps.
            internal readonly HashSet<MonoBehaviour> WaitingAtLoad = new();
            // Texture and placement modifiers that waited for their texture source at the first capture after a load.
            // Their sources become ready in groups, and each group updated all terrains again (Kubera World: 9 updates
            // of all terrains at an editor start). Their areas collect here and update once, when no modifier of the
            // set waits any more or after LoadWaitLimit.
            internal readonly HashSet<MonoBehaviour> SourceWaitsAtLoad = new();
            internal Rect? LoadTextures;
            internal readonly PlacementChange LoadPlacements = new();
            internal double LoadTime;
            // After a trusted load: the GPU comparison of evaluated and current alphamaps, one terrain for each tick, so
            // no editor frame evaluates all terrains. Only the areas that differ update.
            internal bool CheckActive;
            internal TextureStampEvaluator.AlphamapCheck TextureCheck;
            internal readonly Queue<UnityEngine.Terrain> CheckQueue = new();
            internal readonly List<UnityEngine.Terrain> CheckTerrain = new();
            internal Rect? CheckChanged;
            internal bool CheckFailed;
            // Placement changes that wait for a tick without height or texture writes.
            internal readonly PlacementChange Placements = new();
            // Placement changes of the update that waits for GPU readbacks.
            internal readonly PlacementChange Started = new();
            // Failed placement updates in a row, and the editor time before which no new one starts.
            internal int PlacementFailures;
            internal double RetryTime;
            // Editor time of the last height or texture write.
            internal double LastWrite;
            // The change signal and revision counts and the editor time of the last capture.
            internal int CapturedSignals;
            internal int CapturedRevisions = -1;
            internal double LastCapture;
        }

        /// <summary>
        /// Diagnostics: one line for each change that a tick captures (the modifier or source, the reason and the dirty
        /// region). The timing reports use it. Null costs nothing.
        /// </summary>
        internal static System.Action<LoogaStampWorld, string> ChangeCaptured;

        // An update that covers more than this part of the terrains logs its causes.
        private const float LargeUpdateShare = 0.5f;
        // The same large update message is not logged again within this time, for example while a stamp is dragged.
        private const double LargeUpdateRepeatSeconds = 10.0;
        private const int LargeUpdateCauses = 6;

        private static readonly Dictionary<LoogaStampWorld, WorldState> _worlds = new();
        // The changes that the tick captured for the current world, for the log of a large update.
        private static readonly List<string> _causes = new();
        private static readonly HashSet<MonoBehaviour> _orderModifiers = new();
        private static string _lastLargeUpdate;
        private static double _lastLargeUpdateTime;
        private static readonly HashSet<Spline> _changedSplines = new();
        // Undo does not always raise Spline.Changed. After an undo or redo, all knots are compared.
        private static bool _compareAllSplines;
        // Textures reimported since the last tick.
        private static readonly HashSet<Texture> _changedTextures = new();
        private static readonly List<LoogaHeightModifier> _heightModifiers = new();
        private static readonly List<LoogaTextureModifier> _textureModifiers = new();
        private static readonly List<LoogaPlacementModifier> _placementModifiers = new();
        private static readonly List<LoogaPathClear> _pathClears = new();
        private static readonly PlacementChange _placementChange = new();
        // Changed region of each container in this tick (unwidened). Null = unchanged.
        private static readonly Dictionary<SplineContainer, Rect?> _containerChanges = new();
        // Passes that read each water area in this tick, and the used areas of each source.
        private static readonly Dictionary<LoogaWaterArea, WaterUse> _waterUses = new();
        private static readonly Dictionary<LoogaWaterSource, List<LoogaWaterArea>> _sourceAreas = new();
        private static readonly List<LoogaWaterSource> _removedSources = new();
        private static readonly List<Vector4> _edges = new();
        // Reused collections. The idle editor tick makes no garbage.
        private static readonly List<LoogaStampWorld> _tickWorlds = new();
        private static readonly List<LoogaStampWorld> _destroyedWorlds = new();
        private static readonly HashSet<MonoBehaviour> _seenModifiers = new();
        private static readonly List<MonoBehaviour> _removedModifiers = new();
        private static readonly List<StampWaterFilter> _waterFilters = new();
        private static readonly Stack<List<LoogaWaterArea>> _areaListPool = new();
        // A full capture compares every modifier with its last state: about 1 ms for the 730 modifiers of Kubera World on
        // each editor tick. A tick captures only after a change signal (a stamp revision, a spline, undo, an object
        // change, a hierarchy change or a texture import), while a world updates, or after FullCaptureInterval. A
        // change without a signal, for example a script that moves a stamp without Undo, updates at the next interval.
        private const double FullCaptureInterval = 0.5;
        // Grows with each change signal. Each world keeps the values of its last capture.
        private static int _changeSignals;
        // Diagnostics: ticks that skipped the comparison.
        internal static int SkippedCaptures;

        // Watchdog state: the start of the running tick (0 = none), its current step and the log file. The watchdog
        // thread reads them while the main thread is busy, so a stalled update names the step that it is stuck in.
        private static long _tickStart;
        private static volatile string _step = "idle";
        private static string _watchdogLog;

        static LoogaStampWorldUpdater()
        {
            _watchdogLog = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "LoogaStampWatchdog.log");
            new Thread(Watch) { IsBackground = true, Name = "Looga Stamps watchdog" }.Start();
            EditorApplication.update += Tick;
            Spline.Changed += OnSplineChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            EditorApplication.hierarchyChanged += MarkChangeSignal;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            AssemblyReloadEvents.beforeAssemblyReload += FinishBeforeReload;
        }

        private static void OnUndoRedo()
        {
            _compareAllSplines = true;
            _changeSignals++;
        }

        private static void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification)
        {
            _changedSplines.Add(spline);
            _changeSignals++;
        }

        private static void MarkChangeSignal()
        {
            _changeSignals++;
        }

        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            for (int index = 0; index < stream.length; index++)
            {
                // Terrain writes mark the scene dirty in most frames, and a dirty scene alone changes no modifier.
                if (stream.GetEventType(index) == ObjectChangeKind.ChangeScene) continue;
                _changeSignals++;
                return;
            }
        }

        /// <summary>Marks reimported textures. The next tick updates the modifiers that use them.</summary>
        internal static void MarkTexturesChanged(IEnumerable<Texture> textures)
        {
            foreach (Texture texture in textures)
            {
                if (texture)
                {
                    _changedTextures.Add(texture);
                    _changeSignals++;
                }
            }
        }

        // Terrain data saves with the scene, so the CPU copies must be current.
        private static void OnSceneSaving(Scene scene, string path)
        {
            foreach (LoogaStampWorld world in Object.FindObjectsByType<LoogaStampWorld>(FindObjectsSortMode.None))
            {
                if (world.gameObject.scene != scene) continue;
                world.SynchronizeHeights();
                world.SynchronizeTextures();
            }
        }

        // A script reload drops the updater state and the lists of terrains whose CPU heights and alphamaps wait for
        // synchronization. After a reload during an update, the world stayed "updating" with nothing scheduled, its CPU
        // heights stayed old, and a later scene save could write old alphamaps. The interrupted work finishes here.
        private static void FinishBeforeReload()
        {
            foreach (KeyValuePair<LoogaStampWorld, WorldState> pair in _worlds)
            {
                LoogaStampWorld world = pair.Key;
                if (!world || !world.isActiveAndEnabled) continue;
                PlacementChange placements = pair.Value.Placements;
                try
                {
                    world.WaitForPendingPlacements();
                    if (placements.Region.HasValue)
                    {
                        world.UpdatePlacementRegion(placements.Region.Value,
                            placements.All ? null : placements.Modifiers.Contains);
                    }
                    world.WaitForPendingHeights();
                    world.SynchronizeHeights();
                    world.SynchronizeTextures();
                }
                catch (System.Exception exception)
                {
                    Debug.LogException(exception, world);
                }
                world.IsUpdating = false;
            }
        }

        private static void Tick()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            Interlocked.Exchange(ref _tickStart, Stopwatch.GetTimestamp());
            try
            {
                TickWorlds();
            }
            finally
            {
                Interlocked.Exchange(ref _tickStart, 0);
                _step = "idle";
            }
        }

        // Logs a tick that runs longer than WatchdogSeconds: its step, the stamp section and the sections entered, so
        // the log shows whether the update is stuck in one place or still working.
        private static void Watch()
        {
            long reported = 0;
            double next = WatchdogSeconds;
            while (true)
            {
                Thread.Sleep(2000);
                long start = Interlocked.Read(ref _tickStart);
                if (start == 0) continue;
                if (start != reported)
                {
                    reported = start;
                    next = WatchdogSeconds;
                }
                double seconds = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
                if (seconds < next) continue;
                next += WatchdogInterval;
                try
                {
                    File.AppendAllText(_watchdogLog, System.DateTime.Now.ToString("HH:mm:ss") + " tick busy " +
                        seconds.ToString("F0") + " s, step: " + _step + ", section: " + StampSections.Current +
                        ", sections entered: " + StampSections.Entries + "\n");
                }
                catch (IOException)
                {
                }
            }
        }

        private static void TickWorlds()
        {
            // A copy, because a tick can enable or disable a world.
            _tickWorlds.Clear();
            _tickWorlds.AddRange(LoogaStampWorld.Enabled);
            double now = EditorApplication.timeSinceStartup;
            foreach (LoogaStampWorld world in _tickWorlds)
            {
                if (!world.UpdateInEditor || !world.isActiveAndEnabled) continue;
                if (!_worlds.TryGetValue(world, out WorldState state))
                {
                    // The first capture waits for the next editor tick. After an editor start the scene loads after
                    // the scripts, and editor hooks of the world (for example the live road masks) subscribe in their
                    // first update. The first capture must see their sources.
                    _worlds[world] = new WorldState { LoadTime = now };
                    continue;
                }
                bool first = !state.Captured;
                state.Captured = true;
                Rect? heights, textures;
                _causes.Clear();
                using (StampSections.Measure(StampSection.ChangeCapture))
                {
                    try
                    {
                        _step = "change capture";
                        world.RaiseBeforeChangeCapture();
                    }
                    catch (System.Exception exception)
                    {
                        Debug.LogException(exception, world);
                    }
                    // The hooks above can change stamps, so the signals are read after them.
                    if (first || state.CapturedSignals != _changeSignals ||
                        state.CapturedRevisions != LoogaStampWorld.RevisionChanges ||
                        now - state.LastCapture >= FullCaptureInterval || world.IsUpdating || world.HasRequestedUpdate ||
                        state.SyncPending)
                    {
                        state.CapturedSignals = _changeSignals;
                        state.CapturedRevisions = LoogaStampWorld.RevisionChanges;
                        state.LastCapture = now;
                        Capture(world, state, first, out heights, out textures, _placementChange);
                    }
                    else
                    {
                        heights = null;
                        textures = null;
                        _placementChange.Clear();
                        SkippedCaptures++;
                    }
                }
                if (!first && !world.IsUpdating && world.TakeRefusedReadRetry(out Rect retry))
                {
                    NoteChange(world, "prefab mesh vertices were not readable at the last evaluation " + retry);
                    world.RequestUpdate(retry);
                }
                if (!first && world.TakeRequestedUpdate(out Rect requested))
                {
                    NoteChange(world, "requested update " + requested);
                    heights = Union(heights, requested);
                    textures = Union(textures, requested);
                }
                if (first)
                {
                    // The first capture after a load: a trusted saved result gives the world its stored maps.
                    LoogaStampTrustedLoad.AdoptAtLoad(world);
                    continue;
                }
                TakeLoadUpdates(world, state, now, ref textures, _placementChange);
                // Copies the samples that GPU checks of earlier height writes found. It does not wait.
                _step = "complete pending heights";
                world.CompletePendingHeights();
                if (heights.HasValue || textures.HasValue || _placementChange.Region.HasValue ||
                    state.Placements.Region.HasValue || world.PlacementsPending)
                {
                    world.IsUpdating = true;
                    // Field filters read the ground within the field reach, so a height change moves their values
                    // that far.
                    Rect? fieldReach = null;
                    if (heights.HasValue)
                    {
                        // A smooth stamp reads the heights around each sample, so its neighbours update too.
                        heights = world.GrowForSmoothing(heights.Value);
                        _step = "height update " + heights.Value;
                        world.UpdateRegionWithoutWait(heights.Value);
                        state.SyncPending = true;
                        float fieldMargin = world.TerrainFieldReach;
                        Rect reach = Grow(heights.Value, HeightToTextureMargin + fieldMargin);
                        if (world.TexturesReadHeights(reach))
                        {
                            textures = Union(textures, reach);
                        }
                        if (fieldMargin > 0f)
                        {
                            fieldReach = reach;
                        }
                    }
                    if (textures.HasValue)
                    {
                        _step = "texture update " + textures.Value;
                        world.UpdateTextureRegion(textures.Value);
                    }
                    // New heights or layers change every placement on their terrains.
                    _placementChange.AddAll(Union(Union(heights, textures), fieldReach));
                    // UpdateSettled reports the union of the areas that the edit updated.
                    Rect? updated = Union(Union(heights, textures), _placementChange.Region);
                    if (updated.HasValue)
                    {
                        world.NoteUpdated(updated.Value);
                        LogLargeUpdate(world, updated.Value);
                    }
                    state.Placements.Merge(_placementChange);
                    // Placements read back from the GPU, and a readback waits for the frame that the GPU renders. They
                    // run in a later tick, so the new heights and textures show first. While an edit continues, they
                    // wait until a tick without height or texture writes. The readbacks complete in later ticks.
                    bool written = heights.HasValue || textures.HasValue;
                    if (written)
                    {
                        state.LastWrite = now;
                    }
                    if (written && world.PlacementsPending)
                    {
                        // New heights or layers make the pending result old. It is placed again with the new change.
                        world.CancelPendingPlacements();
                    }
                    else
                    {
                        bool pending = world.PlacementsPending;
                        try
                        {
                            _step = "placement complete";
                            world.CompletePendingPlacements();
                            if (pending && !world.PlacementsPending)
                            {
                                state.PlacementFailures = 0;
                            }
                        }
                        catch (System.InvalidOperationException exception)
                        {
                            PlacementFailed(world, state, exception);
                        }
                    }
                    if (!world.PlacementsPending)
                    {
                        // A cancelled update places its change again. A written update has no effect here.
                        if (written)
                        {
                            state.Placements.Merge(state.Started);
                        }
                        state.Started.Clear();
                    }
                    bool heightsChecked = !world.HeightChecksPending || now - state.LastWrite >= HeightCheckWaitSeconds;
                    if (!written && !world.PlacementsPending && state.Placements.Region.HasValue &&
                        EditorApplication.timeSinceStartup >= state.RetryTime && heightsChecked)
                    {
                        Rect region = state.Placements.Region.Value;
                        var changed = new HashSet<LoogaPlacementModifier>(state.Placements.Modifiers);
                        bool all = state.Placements.All;
                        bool any = _placementModifiers.Count > 0;
                        if (any)
                        {
                            state.Started.Merge(state.Placements);
                        }
                        state.Placements.Clear();
                        if (any)
                        {
                            try
                            {
                                _step = "placement start " + region + (all ? " (all stamps)" : " (" + changed.Count +
                                    " stamps)");
                                world.StartPlacementRegion(region, all ? null : changed.Contains);
                            }
                            catch (System.InvalidOperationException exception)
                            {
                                // A start completes its readbacks with waits when they reach the readback budget.
                                PlacementFailed(world, state, exception);
                            }
                        }
                    }
                    world.IsUpdating = state.Placements.Region.HasValue || world.PlacementsPending;
                }
                else if (state.SyncPending && SynchronizeHeights(world))
                {
                    state.SyncPending = false;
                }
                if (!state.SyncPending)
                {
                    try
                    {
                        _step = "settle";
                        world.SettleIfComplete();
                    }
                    catch (System.Exception exception)
                    {
                        Debug.LogException(exception, world);
                    }
                }
            }
            _changedSplines.Clear();
            _compareAllSplines = false;
            _changedTextures.Clear();
            RemoveDestroyedWorlds();
            foreach (LoogaStampWorld world in _tickWorlds)
            {
                if (!world || !world.isActiveAndEnabled) continue;
                _step = "surface fields";
                world.StepSurfaceFields();
            }
        }

        // Gives the collected areas of the modifiers that waited for their texture source at the load to this tick, when
        // no modifier of the set waits any more or after LoadWaitLimit. Until then the world is not updating, so the
        // tools that make the source textures, which wait for an idle world, keep running.
        private static void TakeLoadUpdates(LoogaStampWorld world, WorldState state, double now, ref Rect? textures,
            PlacementChange placements)
        {
            if (state.CheckActive)
            {
                StepTextureCheck(world, state, ref textures);
                return;
            }
            if (!state.LoadTextures.HasValue && !state.LoadPlacements.Region.HasValue &&
                state.SourceWaitsAtLoad.Count == 0) return;

            // A destroyed or disabled modifier no longer waits.
            state.SourceWaitsAtLoad.RemoveWhere(modifier => !modifier || !modifier.isActiveAndEnabled);
            if (state.SourceWaitsAtLoad.Count > 0 && now - state.LoadTime < LoadWaitLimit) return;

            bool allReady = state.SourceWaitsAtLoad.Count == 0;
            state.SourceWaitsAtLoad.Clear();
            if (!state.LoadTextures.HasValue && !state.LoadPlacements.Region.HasValue) return;

            // The saved terrains hold the result of these modifiers when their source textures are the same as at the
            // save. After a save of a full texture rebuild, a GPU comparison of the evaluated alphamaps checks it: only
            // areas that differ update.
            string refusal = "a modifier still waited after " + LoadWaitLimit + " s";
            if (allReady && LoogaStampTrustedLoad.SourcesMatchSave(world, out bool check, out refusal))
            {
                state.LoadTextures = null;
                state.LoadPlacements.Clear();
                if (!check)
                {
                    NoteChange(world, "modifiers that waited for their texture sources at the load: the saved result " +
                        "of live updates is used");
                    return;
                }
                state.CheckActive = true;
                state.CheckChanged = null;
                state.CheckFailed = false;
                state.CheckQueue.Clear();
                foreach (UnityEngine.Terrain terrain in world.Terrains)
                {
                    if (!terrain) continue;
                    state.CheckQueue.Enqueue(terrain);
                }
                NoteChange(world, "modifiers that waited for their texture sources at the load: the saved result is " +
                    "used, the alphamaps are checked");
                return;
            }
            NoteChange(world, "modifiers that waited for their texture sources at the load (the saved result is not " +
                "used: " + refusal + ")");
            textures = Union(textures, state.LoadTextures);
            placements.Merge(state.LoadPlacements);
            state.LoadTextures = null;
            state.LoadPlacements.Clear();
        }

        // Checks one terrain for each tick. At the end, updates the areas where the evaluated alphamaps differ from the
        // saved ones. A failed readback or a changed layer list updates all terrains, as a load without a saved result.
        private static void StepTextureCheck(LoogaStampWorld world, WorldState state, ref Rect? textures)
        {
            if (state.TextureCheck != null)
            {
                if (!state.TextureCheck.IsDone) return;

                List<Rect> changed = state.TextureCheck.ReadChangedAreas();
                state.TextureCheck.Dispose();
                state.TextureCheck = null;
                if (changed == null)
                {
                    state.CheckFailed = true;
                }
                else
                {
                    foreach (Rect area in changed)
                    {
                        state.CheckChanged = Union(state.CheckChanged, area);
                    }
                }
            }
            state.CheckTerrain.Clear();
            while (state.CheckQueue.Count > 0 && state.CheckTerrain.Count < CheckTerrainsPerTick)
            {
                UnityEngine.Terrain terrain = state.CheckQueue.Dequeue();
                if (!terrain) continue;
                state.CheckTerrain.Add(terrain);
            }
            if (!state.CheckFailed && state.CheckTerrain.Count > 0)
            {
                state.TextureCheck = world.StartTextureCheck(state.CheckTerrain);
                if (state.TextureCheck != null) return;

                state.CheckFailed = true;
            }

            state.CheckActive = false;
            state.CheckQueue.Clear();
            if (state.CheckFailed)
            {
                NoteChange(world, "the alphamap check after the load failed, all terrains update");
                textures = Union(textures, world.WorldRect());
            }
            else if (state.CheckChanged.HasValue)
            {
                NoteChange(world, "alphamap check after the load: changes in " + state.CheckChanged.Value);
                textures = Union(textures, state.CheckChanged);
            }
            else
            {
                NoteChange(world, "alphamap check after the load: no change");
            }
            state.CheckChanged = null;
            state.CheckFailed = false;
        }

        /// <summary>
        /// True when the world has applied every change that the updater knows: no update, placement, requested update,
        /// load update or height synchronization waits, and the last capture saw the current stamp revisions. The
        /// terrains then hold the result of the stamps.
        /// </summary>
        internal static bool IsSettled(LoogaStampWorld world)
        {
            if (!_worlds.TryGetValue(world, out WorldState state) || !state.Captured) return false;

            return !world.IsUpdating && !world.PlacementsPending && !world.HasRequestedUpdate &&
                world.HeightsSynchronized && !state.SyncPending && !state.Placements.Region.HasValue &&
                !state.Started.Region.HasValue && state.SourceWaitsAtLoad.Count == 0 &&
                !state.LoadTextures.HasValue && !state.LoadPlacements.Region.HasValue && !state.CheckActive &&
                state.CapturedRevisions == LoogaStampWorld.RevisionChanges;
        }

        private static bool SynchronizeHeights(LoogaStampWorld world)
        {
            _step = "height sync";
            return world.SynchronizeHeights(1);
        }

        // A failed GPU readback drops the placement update. It usually means that GPU memory is short, so the cached
        // fields go and the update starts again after a delay, without a wait. A blocking update over a large region
        // stalled the editor for more than 20 minutes when GPU memory was short. After the retries the update is dropped.
        private static void PlacementFailed(LoogaStampWorld world, WorldState state, System.Exception exception)
        {
            long released = world.ReleaseCachedFields();
            state.PlacementFailures++;
            if (state.PlacementFailures <= PlacementRetries)
            {
                Debug.LogWarning("Looga Stamps: placement readback failed (" + exception.Message + "). Released " +
                    (released >> 20) + " MB of cached fields. The placements update again (retry " +
                    state.PlacementFailures + " of " + PlacementRetries + ").", world);
                state.Placements.Merge(state.Started);
                state.RetryTime = EditorApplication.timeSinceStartup + PlacementRetryDelay;
            }
            else
            {
                Debug.LogError("Looga Stamps: placement readback failed " + state.PlacementFailures + " times (" +
                    exception.Message + "). The changed area keeps its old placements. Free GPU memory, then change a " +
                    "stamp there again.", world);
                state.PlacementFailures = 0;
            }
            state.Started.Clear();
        }

        #region Change tracking
        // Compares every modifier with its last state and stores the new state. Gives the dirty world regions.
        private static void Capture(LoogaStampWorld world, WorldState state, bool first, out Rect? heights,
            out Rect? textures, PlacementChange placements)
        {
            world.GetComponentsInChildren(true, _heightModifiers);
            world.GetComponentsInChildren(true, _textureModifiers);
            world.GetComponentsInChildren(true, _placementModifiers);
            world.GetComponentsInChildren(true, _pathClears);
            heights = null;
            textures = null;
            placements.Clear();
            // A moved modifier updates all terrains. A new or removed modifier updates only its own area below.
            if (!first && !SameOrder(state, state.HeightOrder, _heightModifiers))
            {
                heights = world.WorldRect();
                NoteChange(world, "height modifier order");
            }
            if (!first && !SameOrder(state, state.TextureOrder, _textureModifiers))
            {
                textures = world.WorldRect();
                NoteChange(world, "texture modifier order");
            }
            if (!first && !SameOrder(state, state.PlacementOrder, _placementModifiers))
            {
                placements.AddAll(world.WorldRect());
                NoteChange(world, "placement modifier order");
            }
            _containerChanges.Clear();
            _waterUses.Clear();
            HashSet<MonoBehaviour> seen = _seenModifiers;
            seen.Clear();
            foreach (LoogaHeightModifier modifier in _heightModifiers)
            {
                seen.Add(modifier);
                heights = Union(heights, Capture(world, state, first, modifier));
                NoteWaterUse(modifier, WaterUse.Heights);
            }
            foreach (LoogaTextureModifier modifier in _textureModifiers)
            {
                seen.Add(modifier);
                textures = Union(textures, Capture(world, state, first, modifier));
                NoteWaterUse(modifier, WaterUse.Textures);
            }
            foreach (LoogaPlacementModifier modifier in _placementModifiers)
            {
                seen.Add(modifier);
                placements.Add(Capture(world, state, first, modifier), modifier);
                NoteWaterUse(modifier, WaterUse.Placements);
            }
            foreach (LoogaPathClear clear in _pathClears)
            {
                seen.Add(clear);
                placements.Add(Capture(world, state, first, clear), clear);
            }

            List<MonoBehaviour> removed = _removedModifiers;
            removed.Clear();
            foreach (KeyValuePair<MonoBehaviour, ModifierState> pair in state.Modifiers)
            {
                if (seen.Contains(pair.Key)) continue;
                removed.Add(pair.Key);
                if (!pair.Value.Active) continue;
                NoteChange(world, "removed modifier " + pair.Value.Cull);
                if (pair.Value.Placement)
                {
                    placements.AddAll(pair.Value.Cull);
                }
                else if (pair.Value.Texture)
                {
                    textures = Union(textures, pair.Value.Cull);
                }
                else
                {
                    heights = Union(heights, pair.Value.Cull);
                }
            }
            foreach (MonoBehaviour modifier in removed)
            {
                state.Modifiers.Remove(modifier);
            }
            state.HeightOrder.Clear();
            state.HeightOrder.AddRange(_heightModifiers);
            state.TextureOrder.Clear();
            state.TextureOrder.AddRange(_textureModifiers);
            state.PlacementOrder.Clear();
            state.PlacementOrder.AddRange(_placementModifiers);
            Rect? heightsBefore = heights, texturesBefore = textures, placementsBefore = placements.Region;
            CaptureWaterSources(world, state, first, ref heights, ref textures, placements);
            if (heights != heightsBefore || textures != texturesBefore || placements.Region != placementsBefore)
            {
                NoteChange(world, "water sources, heights " + heights + ", textures " + textures + ", placements " +
                    placements.Region);
            }
        }

        // Notes a captured change for the large update log and the diagnostics.
        private static void NoteChange(LoogaStampWorld world, string cause)
        {
            _causes.Add(cause);
            ChangeCaptured?.Invoke(world, cause);
        }

        // Logs the causes of an update that covers most terrains, because such an update takes much longer than a local
        // edit. The usual causes are a moved stamp, a large stamp area, a texture source that reports a large region, or
        // two edits far apart in the same tick (the dirty region is their bounding rectangle).
        private static void LogLargeUpdate(LoogaStampWorld world, Rect region)
        {
            int covered = 0, count = 0;
            foreach (UnityEngine.Terrain terrain in world.Terrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                count++;
                Vector3 position = terrain.GetPosition();
                Vector3 size = terrain.terrainData.size;
                if (region.Overlaps(new Rect(position.x, position.z, size.x, size.z)))
                {
                    covered++;
                }
            }
            if (count == 0 || covered <= count * LargeUpdateShare) return;

            int shown = Mathf.Min(_causes.Count, LargeUpdateCauses);
            string causes = shown == 0 ? "no captured change" : string.Join("; ", _causes.GetRange(0, shown));
            if (_causes.Count > shown)
            {
                causes += "; " + (_causes.Count - shown) + " more";
            }
            string message = "Looga Stamps: the update covers " + covered + " of " + count + " terrains. Causes: " +
                causes;
            double now = EditorApplication.timeSinceStartup;
            if (message == _lastLargeUpdate && now - _lastLargeUpdateTime < LargeUpdateRepeatSeconds) return;
            _lastLargeUpdate = message;
            _lastLargeUpdateTime = now;
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, world, "{0}", message);
        }

        // Dirty region of one modifier, or null.
        private static Rect? Capture(LoogaStampWorld world, WorldState state, bool first, MonoBehaviour modifier)
        {
            ModifierState current = Snapshot(modifier);
            if (!state.Modifiers.TryGetValue(modifier, out ModifierState previous))
            {
                current.Cull = GetCullRect(world, modifier);
                state.Modifiers[modifier] = current;
                TrackContainer(state, ContainerOf(modifier));
                // A new modifier updates its whole area, which includes any region that its source reported.
                SourceChange(modifier);
                if (!first && current.Active)
                {
                    NoteChange(world, Describe(modifier, "new", current.Cull));
                }
                if (first && !current.Active && modifier.isActiveAndEnabled && modifier is LoogaHeightModifier)
                {
                    state.WaitingAtLoad.Add(modifier);
                }
                else if (first && (current.Texture || current.Placement) && modifier.isActiveAndEnabled &&
                         LoogaStampTextureSource.Waits(modifier))
                {
                    state.SourceWaitsAtLoad.Add(modifier);
                }
                return !first && current.Active ? current.Cull : null;
            }

            bool ownChange = !Same(previous, current) || UsesChangedTexture(modifier);
            Rect? splineChange = ContainerChange(state, ContainerOf(modifier));
            Rect? sourceChange = SourceChange(modifier);
            if (!ownChange && !splineChange.HasValue && !sourceChange.HasValue) return null;

            if (!previous.Active && current.Active && state.WaitingAtLoad.Remove(modifier))
            {
                current.Cull = GetCullRect(world, modifier);
                world.InvalidateDistanceFields(FieldOwner(modifier));
                state.Modifiers[modifier] = current;
                return null;
            }
            if (!previous.Active && current.Active && state.SourceWaitsAtLoad.Remove(modifier))
            {
                // The area updates later, together with the other modifiers that waited at the load.
                current.Cull = GetCullRect(world, modifier);
                if (current.FieldRevision != previous.FieldRevision)
                {
                    world.InvalidateDistanceFields(FieldOwner(modifier));
                }
                state.Modifiers[modifier] = current;
                if (current.Texture)
                {
                    state.LoadTextures = Union(state.LoadTextures, current.Cull);
                }
                else
                {
                    state.LoadPlacements.Add(current.Cull, modifier);
                }
                NoteChange(world, Describe(modifier, "became active, waits for the other sources", current.Cull));
                return null;
            }

            Rect? dirty = null;
            current.Cull = GetCullRect(world, modifier);
            bool cullChanged = current.Cull != previous.Cull;
            if (ownChange || cullChanged)
            {
                // A settings, transform or bounds change can affect the whole culling rectangle. The distance field of a
                // texture or placement modifier depends only on its spline area or path.
                if ((!current.Texture && !current.Placement) || current.FieldRevision != previous.FieldRevision)
                {
                    world.InvalidateDistanceFields(FieldOwner(modifier));
                }
                if (previous.Active) dirty = previous.Cull;
                if (current.Active) dirty = Union(dirty, current.Cull);
            }
            else if (splineChange.HasValue)
            {
                Rect reach = Grow(splineChange.Value, Reach(modifier));
                world.InvalidateDistanceFields(FieldOwner(modifier), reach);
                if (current.Active) dirty = reach;
            }
            // A texture source reports only the area where its textures changed.
            if (sourceChange.HasValue && current.Active)
            {
                dirty = Union(dirty, sourceChange.Value);
            }
            state.Modifiers[modifier] = current;
            if (dirty.HasValue)
            {
                // Active also changes when a stamp waits for the result of its texture source or neighbourhood.
                string reason = previous.Active != current.Active ? current.Active ? "became active" : "became inactive" :
                    !Same(previous, current) ? "settings or transform" : UsesChangedTexture(modifier) ? "texture" :
                    cullChanged ? "bounds" : splineChange.HasValue ? "spline" : "texture source region";
                NoteChange(world, Describe(modifier, reason, dirty.Value));
            }
            return dirty;
        }

        // Area where the textures of the modifier's texture source changed since the last tick, or where a
        // neighbourhood stamp reported a change of its result, or null.
        private static Rect? SourceChange(MonoBehaviour modifier)
        {
            Rect? change = modifier.TryGetComponent(out LoogaStampTextureSource source) &&
                source.TakeChangedRegion(out Rect region)
                ? region
                : null;
            if (modifier is LoogaNeighbourhoodStamp neighbourhood && neighbourhood.TakeChangedRegion(out Rect stampRegion))
            {
                change = Union(change, stampRegion);
            }
            return change;
        }

        private static string Describe(MonoBehaviour modifier, string reason, Rect dirty)
        {
            return modifier.GetType().Name + " '" + modifier.name + "' (" + reason + ") " + dirty;
        }

        private static ModifierState Snapshot(MonoBehaviour modifier)
        {
            var state = new ModifierState
            {
                Transform = modifier.transform.localToWorldMatrix,
                // A stamp without a result counts as inactive, as in LoogaStampWorld.GetHeightModifiers.
                Active = modifier.isActiveAndEnabled && !LoogaStampTextureSource.Waits(modifier) &&
                    modifier is not LoogaNeighbourhoodStamp { IsReady: false }
            };
            StampFalloff falloff = null;
            if (modifier is LoogaHeightModifier height)
            {
                state.Revision = height.Revision;
                falloff = FalloffOf(height);
            }
            else if (modifier is LoogaTextureModifier texture)
            {
                state.Texture = true;
                state.Revision = texture.Revision;
                falloff = (texture as LoogaTextureStamp)?.Falloff;
                if (texture is LoogaPathTexture { Path: not null } pathTexture)
                {
                    state.FieldRevision = pathTexture.Path.Revision;
                }
            }
            else if (modifier is LoogaPlacementModifier placement)
            {
                state.Placement = true;
                state.Revision = placement.Revision;
                falloff = FalloffOf(placement);
            }
            else if (modifier is LoogaPathClear clear)
            {
                state.Placement = true;
                state.Revision = clear.Revision;
                if (clear.Path)
                {
                    state.FieldRevision = clear.Path.Revision;
                }
            }
            foreach (StampWaterFilter waterFilter in WaterFiltersOf(modifier))
            {
                if (waterFilter.Area)
                {
                    state.Revision = state.Revision * 29 + waterFilter.Area.Revision;
                }
            }
            if (modifier is LoogaWaterBed { Area: not null } bed)
            {
                // Source edits are compared in CaptureWaterSources. This covers the area settings and source list.
                state.Revision = state.Revision * 29 + bed.Area.Revision;
                if (bed.DropArea)
                {
                    state.Revision = state.Revision * 31 + bed.DropArea.Revision;
                }
            }
            if (falloff != null)
            {
                if (falloff.Shape == FalloffShape.SplineArea && falloff.SplineArea)
                {
                    state.FieldRevision = falloff.SplineArea.Revision;
                    state.Revision = state.Revision * 31 + falloff.SplineArea.Revision;
                }
                else if (falloff.Shape == FalloffShape.WaterArea && falloff.WaterArea)
                {
                    // Source edits are compared in CaptureWaterSources. This covers the area settings and source list.
                    state.FieldRevision = falloff.WaterArea.Revision;
                    state.Revision = state.Revision * 31 + falloff.WaterArea.Revision;
                }
                if (falloff.PaintedArea)
                {
                    state.Painted = falloff.PaintedArea.transform.localToWorldMatrix;
                    state.Revision = state.Revision * 17 + falloff.PaintedArea.Revision;
                }
            }
            return state;
        }

        // True when the modifier samples a texture that was reimported since the last tick.
        internal static bool UsesChangedTexture(MonoBehaviour modifier, ICollection<Texture> changed = null)
        {
            changed ??= _changedTextures;
            if (changed.Count == 0) return false;
            if (modifier is LoogaHeightStamp height && Contains(changed, height.Texture)) return true;
            if (modifier is LoogaObjectStamp objects && (Contains(changed, objects.PoissonDisk) ||
                Contains(changed, objects.WeightNoise.Texture) || Contains(changed, objects.Weight2Noise.Texture)))
            {
                return true;
            }
            if (modifier is LoogaDetailStamp details && (Contains(changed, details.WeightNoise.Texture) ||
                Contains(changed, details.Weight2Noise.Texture)))
            {
                return true;
            }
            if (modifier is LoogaClearStamp clear && (Contains(changed, clear.WeightNoise.Texture) ||
                Contains(changed, clear.Weight2Noise.Texture)))
            {
                return true;
            }
            StampFalloff falloff = FalloffOf(modifier);
            return falloff != null && Contains(changed, falloff.Texture);
        }

        private static bool Contains(ICollection<Texture> textures, Texture texture)
        {
            return texture && textures.Contains(texture);
        }

        private static bool Same(ModifierState a, ModifierState b)
        {
            return a.Active == b.Active && a.Revision == b.Revision && a.FieldRevision == b.FieldRevision &&
                a.Transform == b.Transform && a.Painted == b.Painted;
        }

        // Texture and placement passes cull with the MicroVerse mapping in every mode.
        private static Rect GetCullRect(LoogaStampWorld world, MonoBehaviour modifier)
        {
            return modifier switch
            {
                LoogaTextureModifier texture => texture.GetCullRect(HeightMapping.MicroVerseParity),
                LoogaPlacementModifier placement => placement.GetCullRect(HeightMapping.MicroVerseParity),
                LoogaPathClear clear => clear.GetCullRect(HeightMapping.MicroVerseParity),
                _ => ((LoogaHeightModifier)modifier).GetCullRect(world.Mapping)
            };
        }

        private static StampFalloff FalloffOf(MonoBehaviour modifier)
        {
            return modifier switch
            {
                LoogaHeightStamp height => height.Falloff,
                LoogaSnapshotLimit limit => limit.Falloff,
                LoogaWaterBed bed => bed.Falloff,
                LoogaSmoothStamp smooth => smooth.Falloff,
                LoogaTextureStamp texture => texture.Falloff,
                LoogaObjectStamp objects => objects.Falloff,
                LoogaDetailStamp details => details.Falloff,
                LoogaClearStamp clear => clear.Falloff,
                _ => null
            };
        }

        private static LoogaSplinePath PathOf(MonoBehaviour modifier)
        {
            return modifier switch
            {
                LoogaSplinePath path => path,
                LoogaPathTexture pathTexture => pathTexture.Path,
                LoogaPathClear clear => clear.Path,
                _ => null
            };
        }

        private static SplineContainer ContainerOf(MonoBehaviour modifier)
        {
            LoogaSplinePath path = PathOf(modifier);
            if (path) return path.Spline;
            StampFalloff falloff = FalloffOf(modifier);
            if (falloff != null && falloff.Shape == FalloffShape.SplineArea && falloff.SplineArea)
            {
                return falloff.SplineArea.Spline;
            }
            // A water bed without a spline falloff follows the spline of its drop area.
            return modifier is LoogaWaterBed { DropArea: not null } bed ? bed.DropArea.Spline : null;
        }

        private static Object FieldOwner(MonoBehaviour modifier)
        {
            LoogaSplinePath path = PathOf(modifier);
            if (path) return path;
            StampFalloff falloff = FalloffOf(modifier);
            if (falloff == null) return null;
            if (modifier is LoogaWaterBed { DropArea: not null } bed && falloff.Shape != FalloffShape.SplineArea)
            {
                return bed.DropArea;
            }
            return falloff.Shape == FalloffShape.WaterArea ? falloff.WaterArea : falloff.SplineArea;
        }

        // Distance from a changed curve at which the distance field can change: the field search limit. Heights and
        // weights change only where the field changes, so this is also their reach.
        private static float Reach(MonoBehaviour modifier)
        {
            LoogaSplinePath path = PathOf(modifier);
            if (path)
            {
                float widest = 0f;
                for (int i = 0; i < path.Widths.Count; i++)
                {
                    widest = Mathf.Max(widest, path.GetMaximumWidth(i));
                }
                float noise = path.WidthNoise.Type == StampNoiseType.None ? 0f : Mathf.Abs(path.WidthNoise.Amplitude);
                return 2f * (path.SearchDistance + widest) + noise;
            }
            LoogaSplineArea area = FalloffOf(modifier)?.SplineArea;
            if (!area && modifier is LoogaWaterBed { DropArea: not null } bed) area = bed.DropArea;
            return area ? 2f * area.MaxDistance : 0f;
        }
        #endregion

        #region Water sources
        [System.Flags]
        private enum WaterUse
        {
            None = 0,
            Heights = 1,
            Textures = 2,
            Placements = 4
        }

        /// <summary>Boundary edge with both points rounded to 1 cm, in a fixed point order.</summary>
        internal readonly struct EdgeKey : System.IEquatable<EdgeKey>
        {
            private readonly int _ax, _az, _bx, _bz;

            internal EdgeKey(Vector4 edge)
            {
                int ax = Mathf.RoundToInt(edge.x * 100f), az = Mathf.RoundToInt(edge.y * 100f);
                int bx = Mathf.RoundToInt(edge.z * 100f), bz = Mathf.RoundToInt(edge.w * 100f);
                bool swap = ax > bx || (ax == bx && az > bz);
                _ax = swap ? bx : ax;
                _az = swap ? bz : az;
                _bx = swap ? ax : bx;
                _bz = swap ? az : bz;
            }

            internal Rect Rect => Rect.MinMaxRect(Mathf.Min(_ax, _bx) / 100f, Mathf.Min(_az, _bz) / 100f,
                Mathf.Max(_ax, _bx) / 100f, Mathf.Max(_az, _bz) / 100f);

            public bool Equals(EdgeKey other) => _ax == other._ax && _az == other._az && _bx == other._bx && _bz == other._bz;
            public override bool Equals(object other) => other is EdgeKey key && Equals(key);
            public override int GetHashCode() => ((_ax * 397 ^ _az) * 397 ^ _bx) * 397 ^ _bz;
        }

        /// <summary>
        /// Replaces the stored edges with the current edges. Returns the bounds of the edges that only one of the two
        /// sets has, or null when both sets are equal.
        /// </summary>
        internal static Rect? ReplaceEdges(HashSet<EdgeKey> stored, List<Vector4> current)
        {
            var keys = new HashSet<EdgeKey>();
            Rect? changed = null;
            foreach (Vector4 edge in current)
            {
                var key = new EdgeKey(edge);
                if (keys.Add(key) && !stored.Contains(key))
                {
                    changed = Union(changed, key.Rect);
                }
            }
            foreach (EdgeKey key in stored)
            {
                if (!keys.Contains(key))
                {
                    changed = Union(changed, key.Rect);
                }
            }
            stored.Clear();
            stored.UnionWith(keys);
            return changed;
        }

        // A modifier reads a water area through its falloff, its water filter, or both. A water bed also reads its own
        // area.
        private static void NoteWaterUse(MonoBehaviour modifier, WaterUse use)
        {
            if (!modifier.isActiveAndEnabled) return;
            StampFalloff falloff = FalloffOf(modifier);
            if (falloff != null && falloff.Shape == FalloffShape.WaterArea && falloff.WaterArea)
            {
                NoteWaterUse(falloff.WaterArea, use);
            }
            foreach (StampWaterFilter filter in WaterFiltersOf(modifier))
            {
                if (filter.Enabled)
                {
                    NoteWaterUse(filter.Area, use);
                }
            }
            if (modifier is LoogaWaterBed { Area: not null } bed)
            {
                NoteWaterUse(bed.Area, use);
            }
        }

        private static void NoteWaterUse(LoogaWaterArea area, WaterUse use)
        {
            _waterUses.TryGetValue(area, out WaterUse uses);
            _waterUses[area] = uses | use;
        }

        // Returns the water filters of the modifier in a reused list. The next call overwrites the list.
        private static List<StampWaterFilter> WaterFiltersOf(MonoBehaviour modifier)
        {
            _waterFilters.Clear();
            switch (modifier)
            {
                case LoogaTextureStamp texture:
                    _waterFilters.Add(texture.WaterFilter);
                    break;
                case LoogaObjectStamp objects:
                    _waterFilters.Add(objects.WaterFilter);
                    break;
                case LoogaDetailStamp details:
                    _waterFilters.Add(details.WaterFilter);
                    break;
                case LoogaClearStamp clear:
                    _waterFilters.Add(clear.WaterFilter);
                    break;
                case LoogaSmoothStamp smooth:
                    _waterFilters.Add(smooth.WaterFilter);
                    _waterFilters.Add(smooth.SecondWaterFilter);
                    break;
            }
            return _waterFilters;
        }

        // Compares the sources of the used water areas with their last state. A changed source invalidates the water
        // fields near its changed edges, and the modifiers that read its areas update there. A RAM knot edit then stays
        // local instead of updating the whole river.
        private static void CaptureWaterSources(LoogaStampWorld world, WorldState state, bool first, ref Rect? heights,
            ref Rect? textures, PlacementChange placements)
        {
            // A changed area (settings or source list) changes the fields on all its tiles. The modifiers that read it
            // update their whole culling rectangles through their revisions.
            foreach (LoogaWaterArea area in _waterUses.Keys)
            {
                if (state.AreaRevisions.TryGetValue(area, out int revision) && revision != area.Revision)
                {
                    world.InvalidateDistanceFields(area);
                }
                state.AreaRevisions[area] = area.Revision;
            }
            // The area lists return to a pool, so that a tick without changes makes no garbage.
            foreach (List<LoogaWaterArea> areas in _sourceAreas.Values)
            {
                areas.Clear();
                _areaListPool.Push(areas);
            }
            _sourceAreas.Clear();
            foreach (LoogaWaterArea area in _waterUses.Keys)
            {
                // An index loop: a foreach over IReadOnlyList allocates its enumerator.
                IReadOnlyList<LoogaWaterSource> sources = area.Sources;
                for (int index = 0; index < sources.Count; index++)
                {
                    LoogaWaterSource source = sources[index];
                    if (!source) continue;
                    if (!_sourceAreas.TryGetValue(source, out List<LoogaWaterArea> areas))
                    {
                        areas = _areaListPool.Count > 0 ? _areaListPool.Pop() : new List<LoogaWaterArea>();
                        _sourceAreas[source] = areas;
                    }
                    areas.Add(area);
                }
            }

            _removedSources.Clear();
            foreach (KeyValuePair<LoogaWaterSource, SourceState> pair in state.Sources)
            {
                if (!pair.Key || !_sourceAreas.ContainsKey(pair.Key)) _removedSources.Add(pair.Key);
            }
            foreach (LoogaWaterSource source in _removedSources)
            {
                SourceState removed = state.Sources[source];
                state.Sources.Remove(source);
                if (!first && removed.Active)
                {
                    MarkWaterChange(world, removed.Areas, removed.Bounds, ref heights, ref textures, placements);
                }
            }

            foreach (KeyValuePair<LoogaWaterSource, List<LoogaWaterArea>> pair in _sourceAreas)
            {
                LoogaWaterSource source = pair.Key;
                bool known = state.Sources.TryGetValue(source, out SourceState previous);
                if (!known)
                {
                    previous = new SourceState();
                    state.Sources[source] = previous;
                }
                previous.Areas.Clear();
                previous.Areas.AddRange(pair.Value);
                Rect? changed = UpdateSource(source, previous, known);
                // A new source joins through an area change, which already updates the whole area.
                if (!first && known && changed.HasValue)
                {
                    MarkWaterChange(world, pair.Value, changed.Value, ref heights, ref textures, placements);
                }
            }
        }

        // Stores the current state of a source. Returns the changed region, or null.
        private static Rect? UpdateSource(LoogaWaterSource source, SourceState state, bool known)
        {
            Mesh mesh = null;
            Matrix4x4 matrix = Matrix4x4.identity;
            bool active = source.isActiveAndEnabled && source.TryGetSurface(out mesh, out matrix);
            int meshId = active ? mesh.GetInstanceID() : 0;
            int vertexCount = active ? mesh.vertexCount : 0;
            if (known && state.Active == active && state.Revision == source.Revision && state.Mesh == meshId &&
                state.VertexCount == vertexCount && state.Transform == matrix) return null;

            Rect previousBounds = state.Bounds;
            Vector2 previousHeights = state.HeightRange;
            bool wasActive = state.Active;
            state.Active = active;
            state.Revision = source.Revision;
            state.Mesh = meshId;
            state.VertexCount = vertexCount;
            state.Transform = matrix;
            _edges.Clear();
            if (active)
            {
                WaterDistanceField.GetBoundaryEdges(mesh, matrix, _edges);
                state.Bounds = WaterDistanceField.SurfaceRect(mesh, matrix);
                state.HeightRange = HeightRange(mesh, matrix);
            }
            Rect? changed = ReplaceEdges(state.Edges, _edges);
            // Same edges at a new surface height: the surface channel changes inside the water.
            if (!changed.HasValue && (active != wasActive || state.HeightRange != previousHeights))
            {
                changed = Union(wasActive ? previousBounds : (Rect?)null, active ? state.Bounds : (Rect?)null);
            }
            return changed;
        }

        private static Vector2 HeightRange(Mesh mesh, Matrix4x4 matrix)
        {
            Bounds bounds = mesh.bounds;
            float low = float.MaxValue, high = float.MinValue;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = bounds.min + Vector3.Scale(bounds.size,
                    new Vector3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1));
                float height = matrix.MultiplyPoint3x4(local).y;
                low = Mathf.Min(low, height);
                high = Mathf.Max(high, height);
            }
            return new Vector2(low, high);
        }

        private static void MarkWaterChange(LoogaStampWorld world, List<LoogaWaterArea> areas, Rect changed,
            ref Rect? heights, ref Rect? textures, PlacementChange placements)
        {
            foreach (LoogaWaterArea area in areas)
            {
                if (!area) continue;
                Rect reach = Grow(changed, area.MaxDistance);
                world.InvalidateDistanceFields(area, reach);
                _waterUses.TryGetValue(area, out WaterUse use);
                if ((use & WaterUse.Heights) != 0) heights = Union(heights, reach);
                if ((use & WaterUse.Textures) != 0) textures = Union(textures, reach);
                if ((use & WaterUse.Placements) != 0) placements.AddAll(reach);
            }
        }
        #endregion

        #region Splines
        private static void TrackContainer(WorldState state, SplineContainer container)
        {
            if (!container || state.Containers.ContainsKey(container)) return;
            state.Containers[container] = Capture(container);
        }

        private static ContainerState Capture(SplineContainer container)
        {
            var state = new ContainerState { Transform = container.transform.localToWorldMatrix };
            foreach (Spline spline in container.Splines)
            {
                state.Knots.Add(spline.ToArray());
                state.Closed.Add(spline.Closed);
            }
            return state;
        }

        // World region of the curves that changed since the last capture (old and new positions), or null.
        private static Rect? ContainerChange(WorldState state, SplineContainer container)
        {
            if (!container) return null;
            if (_containerChanges.TryGetValue(container, out Rect? cached)) return cached;
            Rect? region = null;
            if (!state.Containers.TryGetValue(container, out ContainerState previous))
            {
                state.Containers[container] = Capture(container);
                _containerChanges[container] = null;
                return null;
            }
            Matrix4x4 transform = container.transform.localToWorldMatrix;
            IReadOnlyList<Spline> splines = container.Splines;
            bool moved = transform != previous.Transform || splines.Count != previous.Knots.Count;
            bool anyChanged = moved || _compareAllSplines;
            for (int s = 0; s < splines.Count && !anyChanged; s++)
            {
                if (_changedSplines.Contains(splines[s]))
                {
                    anyChanged = true;
                }
            }
            if (!anyChanged)
            {
                _containerChanges[container] = null;
                return null;
            }

            ContainerState current = Capture(container);
            if (moved)
            {
                for (int s = 0; s < previous.Knots.Count; s++)
                {
                    region = Union(region, CurveBounds(previous.Knots[s], previous.Closed[s], previous.Transform, -1));
                }
                for (int s = 0; s < current.Knots.Count; s++)
                {
                    region = Union(region, CurveBounds(current.Knots[s], current.Closed[s], current.Transform, -1));
                }
            }
            else
            {
                for (int s = 0; s < current.Knots.Count; s++)
                {
                    BezierKnot[] before = previous.Knots[s];
                    BezierKnot[] after = current.Knots[s];
                    if (before.Length != after.Length || previous.Closed[s] != current.Closed[s])
                    {
                        region = Union(region, CurveBounds(before, previous.Closed[s], transform, -1));
                        region = Union(region, CurveBounds(after, current.Closed[s], transform, -1));
                        continue;
                    }
                    for (int k = 0; k < after.Length; k++)
                    {
                        if (before[k].Equals(after[k])) continue;
                        region = Union(region, CurveBounds(before, previous.Closed[s], transform, k));
                        region = Union(region, CurveBounds(after, current.Closed[s], transform, k));
                    }
                }
            }
            state.Containers[container] = current;
            _containerChanges[container] = region;
            return region;
        }

        // Bounds of the control points of the curves next to one knot (or of all curves when knot is -1).
        private static Rect? CurveBounds(BezierKnot[] knots, bool closed, Matrix4x4 transform, int knot)
        {
            int count = knots.Length;
            if (count == 0) return null;
            Rect? bounds = null;
            int first = knot < 0 ? 0 : knot - 1;
            int last = knot < 0 ? count - 1 : knot;
            for (int curve = first; curve <= last; curve++)
            {
                int index = closed ? (curve % count + count) % count : curve;
                if (index < 0 || index >= count) continue;
                int next = closed ? (index + 1) % count : Mathf.Min(index + 1, count - 1);
                var bezier = new BezierCurve(knots[index], knots[next]);
                bounds = Union(bounds, Point(transform, bezier.P0));
                bounds = Union(bounds, Point(transform, bezier.P1));
                bounds = Union(bounds, Point(transform, bezier.P2));
                bounds = Union(bounds, Point(transform, bezier.P3));
            }
            return bounds;
        }

        private static Rect Point(Matrix4x4 transform, float3 point)
        {
            Vector3 world = transform.MultiplyPoint3x4(point);
            return new Rect(world.x, world.z, 0f, 0f);
        }
        #endregion

        #region Helpers
        // True when the modifiers in both lists have the same order. New modifiers (not in the world state) and removed
        // modifiers (not in the current list) are skipped, because they update their own areas.
        private static bool SameOrder<T>(WorldState state, List<MonoBehaviour> previous, List<T> current)
            where T : MonoBehaviour
        {
            _orderModifiers.Clear();
            foreach (T modifier in current)
            {
                _orderModifiers.Add(modifier);
            }
            int index = 0;
            foreach (T modifier in current)
            {
                if (!state.Modifiers.ContainsKey(modifier)) continue;
                while (index < previous.Count && !_orderModifiers.Contains(previous[index])) index++;
                if (index == previous.Count || previous[index] != modifier) return false;
                index++;
            }
            while (index < previous.Count && !_orderModifiers.Contains(previous[index])) index++;
            return index == previous.Count;
        }

        private static Rect Grow(Rect rect, float amount)
        {
            return Rect.MinMaxRect(rect.xMin - amount, rect.yMin - amount, rect.xMax + amount, rect.yMax + amount);
        }

        private static Rect? Union(Rect? a, Rect? b)
        {
            if (!a.HasValue) return b;
            if (!b.HasValue) return a;
            Rect x = a.Value, y = b.Value;
            return Rect.MinMaxRect(Mathf.Min(x.xMin, y.xMin), Mathf.Min(x.yMin, y.yMin), Mathf.Max(x.xMax, y.xMax),
                Mathf.Max(x.yMax, y.yMax));
        }

        private static void RemoveDestroyedWorlds()
        {
            List<LoogaStampWorld> destroyed = _destroyedWorlds;
            destroyed.Clear();
            foreach (LoogaStampWorld world in _worlds.Keys)
            {
                if (!world)
                {
                    destroyed.Add(world);
                }
            }
            foreach (LoogaStampWorld world in destroyed)
            {
                _worlds.Remove(world);
            }
        }
        #endregion
    }
}
