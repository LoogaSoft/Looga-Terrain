using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using LoogaSoft.Terrain.Instances;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    /// <summary>
    /// Timing checks for stamp worlds: a full rebuild, and a move or setting change of stamps that the editor updater
    /// applies. A change test does not wait for the GPU: a one-texel readback after each heightmap or alphamap write
    /// gives the time when the GPU has done the write ("visible"), checked once for each editor tick. Placement times
    /// end when the instance set entries and the CPU detail layers are written. The grass time is when the vegetation
    /// streams have loaded all required cells again. The reports also give the CPU time of each update section
    /// (<see cref="StampSections"/>) and each change that the updater captured.
    /// </summary>
    internal static class LoogaStampTiming
    {
        private const double QuietSeconds = 1.5;
        private const double TimeoutSeconds = 30.0;

        private static ChangeTest _moveTest;

        /// <summary>Rebuilds all terrains of a world and returns the phase times and memory.</summary>
        /// <param name="reuseDistanceFields">False builds every spline distance field again.</param>
        internal static string MeasureRebuild(LoogaStampWorld world, bool reuseDistanceFields)
        {
            long memoryBefore = Profiler.GetAllocatedMemoryForGraphicsDriver();
            if (!reuseDistanceFields)
            {
                world.ClearDistanceFields();
            }
            world.ResetDistanceFieldStatistics();
            Rect all = world.WorldRect();
            Stopwatch timer = Stopwatch.StartNew();
            int written = world.UpdateRegion(all);
            double submitted = timer.Elapsed.TotalSeconds;
            WaitForGpu(world);
            double modified = timer.Elapsed.TotalSeconds;
            world.SynchronizeHeights();
            double synchronized = timer.Elapsed.TotalSeconds;
            long memoryAfter = Profiler.GetAllocatedMemoryForGraphicsDriver();
            Vector2Int statistics = world.DistanceFieldStatistics;
            return string.Format("Rebuild ({0}): {1} terrains, submitted {2:F3} s, modified {3:F3} s, synchronized " +
                "{4:F3} s; fields built {5}, cached {6}; field cache {7:F0} MB; graphics memory {8:F2} GB " +
                "(change {9:+0;-0} MB)", reuseDistanceFields ? "warm" : "cold", written, submitted, modified,
                synchronized, statistics.x, statistics.y, world.CachedDistanceFieldBytes / 1048576.0,
                memoryAfter / 1073741824.0, (memoryAfter - memoryBefore) / 1048576.0);
        }

        /// <summary>
        /// Full update of all terrains: heights, textures and placements, as a MicroVerse full update. Returns the
        /// cumulative times and memory. The CPU height synchronization comes after the placements.
        /// </summary>
        /// <param name="reuseDistanceFields">False builds every spline distance field again.</param>
        internal static string MeasureFullUpdate(LoogaStampWorld world, bool reuseDistanceFields)
        {
            long memoryBefore = Profiler.GetAllocatedMemoryForGraphicsDriver();
            if (!reuseDistanceFields)
            {
                world.ClearDistanceFields();
            }
            world.ResetDistanceFieldStatistics();
            world.ResetPlacementPeakBytes();
            StampSections.Reset();
            StampSections.Enabled = true;
            try
            {
                return FullUpdate(world, reuseDistanceFields, memoryBefore) + "\n" + StampSections.Report("  ");
            }
            finally
            {
                StampSections.Enabled = false;
            }
        }

        private static string FullUpdate(LoogaStampWorld world, bool reuseDistanceFields, long memoryBefore)
        {
            Stopwatch timer = Stopwatch.StartNew();
            world.UpdateRegion(world.WorldRect());
            WaitForGpu(world);
            double heights = timer.Elapsed.TotalSeconds;
            world.RebuildTextures();
            WaitForTextures(world);
            double textures = timer.Elapsed.TotalSeconds;
            world.RebuildPlacements();
            double placements = timer.Elapsed.TotalSeconds;
            world.SynchronizeHeights();
            double synchronized = timer.Elapsed.TotalSeconds;
            long memoryAfter = Profiler.GetAllocatedMemoryForGraphicsDriver();
            Vector2Int statistics = world.DistanceFieldStatistics;
            return string.Format("Full update ({0}): heights {1:F3} s, textures {2:F3} s, placements in set and " +
                "details on CPU {3:F3} s, heights synchronized {4:F3} s; fields built {5}, cached {6}; field cache " +
                "{7:F0} MB; placement peak {8:F1} MB; graphics memory {9:F2} GB (change {10:+0;-0} MB)",
                reuseDistanceFields ? "warm" : "cold", heights, textures, placements, synchronized, statistics.x,
                statistics.y, world.CachedDistanceFieldBytes / 1048576.0, world.PlacementPeakBytes / 1048576.0,
                memoryAfter / 1073741824.0, (memoryAfter - memoryBefore) / 1048576.0);
        }

        /// <summary>
        /// Evaluates all placements again and compares them with the stored result of the last writes. Returns a report:
        /// "identical" or the stamps and detail layers that differ.
        /// </summary>
        internal static string CompareStoredPlacements(LoogaStampWorld world)
        {
            StampPlacements stored = world.StoredPlacements;
            if (stored == null) return "No stored placements.";
            StampPlacements full = world.EvaluatePlacements();
            var report = new StringBuilder();
            int stamps = 0, differentStamps = 0, layers = 0, differentLayers = 0;
            var all = new HashSet<LoogaObjectStamp>(full.Stamps);
            all.UnionWith(stored.Stamps);
            foreach (LoogaObjectStamp stamp in all)
            {
                if (!stamp) continue;
                stamps++;
                for (int p = 0; p < stamp.Prototypes.Count; p++)
                {
                    List<string> a = Keys(full.Get(stamp, p));
                    List<string> b = Keys(stored.Get(stamp, p));
                    if (a.Count == b.Count && a.TrueForAll(key => b.BinarySearch(key, StringComparer.Ordinal) >= 0))
                    {
                        continue;
                    }
                    differentStamps++;
                    report.AppendLine("  " + stamp.name + " prototype " + p + ": full " + a.Count + ", stored " +
                        b.Count);
                    break;
                }
            }
            foreach (UnityEngine.Terrain terrain in world.Terrains)
            {
                if (!terrain) continue;
                foreach (int layer in full.DetailLayers)
                {
                    layers++;
                    bool inFull = full.TryGetDetailLayer(terrain, layer, out byte[] a);
                    bool inStored = stored.TryGetDetailLayer(terrain, layer, out byte[] b);
                    if (inFull == inStored && (!inFull || a.AsSpan().SequenceEqual(b))) continue;
                    differentLayers++;
                    report.AppendLine("  detail layer " + layer + " on " + terrain.name + " differs");
                }
            }
            string summary = string.Format("Stored vs full: {0} of {1} stamps differ, {2} of {3} detail layers differ " +
                "({4} vs {5} placements)", differentStamps, stamps, differentLayers, layers, full.Count, stored.Count);
            return differentStamps + differentLayers == 0 ? summary + ": identical" : summary + "\n" + report;
        }

        // Exact placement values as sortable text keys.
        private static List<string> Keys(IReadOnlyList<StampPlacements.Placement> placements)
        {
            var keys = new List<string>(placements.Count);
            foreach (StampPlacements.Placement placement in placements)
            {
                keys.Add(Key(placement.Position) + Key(placement.Scale) + placement.Rotation.x.ToString("R") + "," +
                    placement.Rotation.y.ToString("R") + "," + placement.Rotation.z.ToString("R") + "," +
                    placement.Rotation.w.ToString("R"));
            }
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static string Key(Vector3 value)
        {
            return value.x.ToString("R") + "," + value.y.ToString("R") + "," + value.z.ToString("R") + ";";
        }

        /// <summary>Rebuilds the textures of all terrains and returns the times and memory.</summary>
        /// <param name="reuseDistanceFields">False builds every spline distance field again.</param>
        internal static string MeasureTextureRebuild(LoogaStampWorld world, bool reuseDistanceFields)
        {
            long memoryBefore = Profiler.GetAllocatedMemoryForGraphicsDriver();
            if (!reuseDistanceFields)
            {
                world.ClearDistanceFields();
            }
            world.ResetDistanceFieldStatistics();
            Stopwatch timer = Stopwatch.StartNew();
            world.RebuildTextures();
            double submitted = timer.Elapsed.TotalSeconds;
            WaitForTextures(world);
            double modified = timer.Elapsed.TotalSeconds;
            long memoryAfter = Profiler.GetAllocatedMemoryForGraphicsDriver();
            Vector2Int statistics = world.DistanceFieldStatistics;
            return string.Format("Texture rebuild ({0}): submitted {1:F3} s, modified {2:F3} s; fields built {3}, " +
                "cached {4}; field cache {5:F0} MB; stored layer maps {6:F0} MB; graphics memory {7:F2} GB " +
                "(change {8:+0;-0} MB)", reuseDistanceFields ? "warm" : "cold", submitted, modified, statistics.x,
                statistics.y, world.CachedDistanceFieldBytes / 1048576.0, world.StoredTextureLayerBytes / 1048576.0,
                memoryAfter / 1073741824.0, (memoryAfter - memoryBefore) / 1048576.0);
        }

        /// <summary>
        /// Moves the targets by an offset, waits until the updater has written and synchronized the heights, then
        /// moves them back and waits again. The world must have Update In Editor on. The report goes to a file.
        /// </summary>
        internal static void StartMoveTest(Transform[] targets, Vector3 offset, LoogaStampWorld world, string reportPath)
        {
            StartChangeTest(() => Move(targets, offset), () => Move(targets, -offset),
                "Move test: " + targets.Length + " targets, offset " + offset, world, reportPath, false);
        }

        /// <summary>
        /// Applies a change, waits until the updater has written everything and synchronized the heights, then
        /// reverts the change and waits again. The world must have Update In Editor on. The report goes to a file.
        /// </summary>
        /// <param name="compare">True compares the stored placements with a full evaluation after the change.</param>
        internal static void StartChangeTest(Action change, Action revert, string label, LoogaStampWorld world,
            string reportPath, bool compare)
        {
            if (_moveTest != null)
            {
                EditorApplication.update -= _moveTest.Tick;
            }
            _moveTest = new ChangeTest(change, revert, label, world, reportPath, compare);
            EditorApplication.update += _moveTest.Tick;
        }

        private static void Move(Transform[] targets, Vector3 offset)
        {
            foreach (Transform target in targets)
            {
                target.position += offset;
            }
        }

        internal static bool MoveTestRunning => _moveTest != null;

        /// <summary>
        /// Compares the terrain heightmaps with a full evaluation of all tiles: number of different samples, tiles and
        /// the largest difference in 16-bit steps. After region updates they must be equal. When they differ, the check
        /// runs again with new spline and water fields, to show whether a cached field was out of date.
        /// </summary>
        internal static string CompareWithFullEvaluation(LoogaStampWorld world)
        {
            string cached = ChangedSamples(world);
            if (cached == "identical") return cached;
            world.ClearDistanceFields();
            return cached + "; with new fields: " + ChangedSamples(world);
        }

        private static string ChangedSamples(LoogaStampWorld world)
        {
            using StampHeights heights = world.EvaluateHeights();
            long samples = 0;
            int tiles = 0, largest = 0;
            for (int i = 0; i < heights.Terrains.Count; i++)
            {
                ushort[] evaluated = heights.ReadTile(i);
                RenderTexture stored = heights.Terrains[i].terrainData.heightmapTexture;
                if (stored.width * stored.height != evaluated.Length) continue;
                var buffer = new NativeArray<ushort>(evaluated.Length, Allocator.Persistent);
                try
                {
                    AsyncGPUReadback.RequestIntoNativeArray(ref buffer, stored, 0).WaitForCompletion();
                    int changed = 0;
                    for (int k = 0; k < evaluated.Length; k++)
                    {
                        int difference = Math.Abs(evaluated[k] - buffer[k]);
                        if (difference == 0) continue;
                        changed++;
                        largest = Math.Max(largest, difference);
                    }
                    samples += changed;
                    tiles += changed > 0 ? 1 : 0;
                }
                finally
                {
                    buffer.Dispose();
                }
            }
            return samples == 0 ? "identical" : string.Format("{0} samples on {1} tiles differ, largest {2} steps",
                samples, tiles, largest);
        }

        // A one-texel readback of a heightmap or alphamap. The GPU runs it after the earlier writes, so it is done when
        // they are done. The caller checks it once for each tick and never waits for it.
        private static AsyncGPUReadbackRequest Probe(LoogaStampWorld world, bool heights)
        {
            foreach (UnityEngine.Terrain terrain in world.Terrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                if (heights)
                {
                    return AsyncGPUReadback.Request(terrain.terrainData.heightmapTexture, 0, 0, 1, 0, 1, 0, 1);
                }
                if (terrain.terrainData.alphamapTextureCount == 0) continue;
                return AsyncGPUReadback.Request(terrain.terrainData.alphamapTextures[0], 0, 0, 1, 0, 1, 0, 1);
            }
            throw new InvalidOperationException("The stamp world has no terrain to read back.");
        }

        // Full update measures wait for the GPU, so their phase times include it.
        private static void WaitForGpu(LoogaStampWorld world)
        {
            Probe(world, true).WaitForCompletion();
        }

        private static void WaitForTextures(LoogaStampWorld world)
        {
            Probe(world, false).WaitForCompletion();
        }

        private sealed class ChangeTest
        {
            private readonly Action _change;
            private readonly Action _revert;
            private readonly bool _compare;
            private readonly LoogaStampWorld _world;
            private readonly string _reportPath;
            private readonly StringBuilder _report = new();
            private readonly Stopwatch _timer = new();
            private int _step;
            private double _firstWrite;
            private double _lastWrite;
            private double _synchronized;
            private double _quietStart;
            private int _writes;
            private Rect _region;
            private double _firstTextureWrite;
            private double _lastTextureWrite;
            private int _textureWrites;
            private Rect _textureRegion;
            private double _firstPlacementWrite;
            private double _lastPlacementWrite;
            private int _placementWrites;
            private Rect _placementRegion;
            private double _lastTick;
            private double _longestTickGap;
            private int _ticks;
            // One-texel readbacks after writes: true for a heightmap, false for an alphamap.
            private readonly List<(bool Heights, AsyncGPUReadbackRequest Request)> _probes = new();
            private double _heightsVisible;
            private double _texturesVisible;
            private double _placementsRealtime;
            private double _grassReady;
            private readonly List<string> _changes = new();
            private readonly TerrainVegetationProvider[] _providers;

            internal ChangeTest(Action change, Action revert, string label, LoogaStampWorld world, string reportPath,
                bool compare)
            {
                _change = change;
                _revert = revert;
                _compare = compare;
                _world = world;
                _reportPath = reportPath;
                _world.HeightsUpdated += OnHeightsUpdated;
                _world.TexturesUpdated += OnTexturesUpdated;
                _world.PlacementsUpdated += OnPlacementsUpdated;
                LoogaStampWorldUpdater.ChangeCaptured += OnChangeCaptured;
                _providers = UnityEngine.Object.FindObjectsByType<TerrainVegetationProvider>(FindObjectsSortMode.None);
                _report.AppendLine(label);
                StampSections.Enabled = true;
                _report.AppendLine("Graphics memory at start: " +
                    (Profiler.GetAllocatedMemoryForGraphicsDriver() / 1073741824.0).ToString("F2") + " GB");
            }

            internal void Tick()
            {
                double tick = _timer.Elapsed.TotalSeconds;
                _longestTickGap = Math.Max(_longestTickGap, tick - _lastTick);
                _lastTick = tick;
                _ticks++;
                PollVisibility(tick);
                SceneView.RepaintAll();
                switch (_step)
                {
                    case 0:
                        Apply(_change, "Change");
                        break;
                    case 1:
                    case 3:
                        Wait();
                        break;
                    case 2:
                        if (_timer.Elapsed.TotalSeconds - _quietStart >= QuietSeconds)
                        {
                            ReportVisibility();
                            if (_compare)
                            {
                                _report.AppendLine("  " + CompareStoredPlacements(_world));
                            }
                            Apply(_revert, "Revert");
                        }
                        break;
                    default:
                        if (_timer.Elapsed.TotalSeconds - _quietStart >= QuietSeconds)
                        {
                            ReportVisibility();
                            Finish();
                        }
                        break;
                }
            }

            private void Apply(Action action, string label)
            {
                _report.AppendLine(label + ":");
                _firstWrite = _lastWrite = _synchronized = -1.0;
                _firstTextureWrite = _lastTextureWrite = -1.0;
                _firstPlacementWrite = _lastPlacementWrite = -1.0;
                _writes = 0;
                _textureWrites = 0;
                _placementWrites = 0;
                _lastTick = 0.0;
                _longestTickGap = 0.0;
                _ticks = 0;
                _heightsVisible = _texturesVisible = _grassReady = -1.0;
                _placementsRealtime = double.NaN;
                _changes.Clear();
                _world.ResetDistanceFieldStatistics();
                _world.ResetPlacementPeakBytes();
                StampSections.Reset();
                _timer.Restart();
                action();
                _step++;
            }

            private void Wait()
            {
                double now = _timer.Elapsed.TotalSeconds;
                if (_writes + _textureWrites + _placementWrites > 0 && _synchronized < 0.0 && _world.HeightsSynchronized &&
                    !_world.IsUpdating)
                {
                    _synchronized = now;
                }
                bool done = _synchronized >= 0.0;
                if (!done && now < TimeoutSeconds) return;
                _report.AppendLine(string.Format("  writes {0}, first modified {1:F3} s, last modified {2:F3} s, " +
                    "synchronized {3:F3} s{4}", _writes, _firstWrite, _lastWrite, _synchronized,
                    done ? "" : " (timeout)"));
                _report.AppendLine(string.Format("  last region x {0:F0}..{1:F0}, z {2:F0}..{3:F0}; fields built {4}, " +
                    "cached {5}; field cache {6:F0} MB; graphics memory {7:F2} GB", _region.xMin, _region.xMax,
                    _region.yMin, _region.yMax, _world.DistanceFieldStatistics.x, _world.DistanceFieldStatistics.y,
                    _world.CachedDistanceFieldBytes / 1048576.0,
                    Profiler.GetAllocatedMemoryForGraphicsDriver() / 1073741824.0));
                _report.AppendLine(string.Format("  texture writes {0}, first modified {1:F3} s, " +
                    "last modified {2:F3} s; last region x {3:F0}..{4:F0}, z {5:F0}..{6:F0}; stored layer maps {7:F0} MB", _textureWrites,
                    _firstTextureWrite, _lastTextureWrite, _textureRegion.xMin, _textureRegion.xMax,
                    _textureRegion.yMin, _textureRegion.yMax, _world.StoredTextureLayerBytes / 1048576.0));
                _report.AppendLine(string.Format("  placement writes {0}, first written {1:F3} s, last written {2:F3} s; " +
                    "last region x {3:F0}..{4:F0}, z {5:F0}..{6:F0}; placement peak {7:F1} MB", _placementWrites,
                    _firstPlacementWrite, _lastPlacementWrite, _placementRegion.xMin, _placementRegion.xMax,
                    _placementRegion.yMin, _placementRegion.yMax, _world.PlacementPeakBytes / 1048576.0));
                _report.AppendLine(string.Format("  editor ticks {0}, longest time between ticks {1:F3} s", _ticks,
                    _longestTickGap));
                _report.Append(StampSections.Report("    "));
                _quietStart = now;
                _step++;
            }

            private void OnHeightsUpdated(Rect region)
            {
                _probes.Add((true, Probe(_world, true)));
                double now = _timer.Elapsed.TotalSeconds;
                if (_writes == 0)
                {
                    _firstWrite = now;
                }
                _lastWrite = now;
                _writes++;
                _region = region;
            }

            private void OnTexturesUpdated(Rect region)
            {
                _probes.Add((false, Probe(_world, false)));
                double now = _timer.Elapsed.TotalSeconds;
                if (_textureWrites == 0)
                {
                    _firstTextureWrite = now;
                }
                _lastTextureWrite = now;
                _textureWrites++;
                _textureRegion = region;
            }

            private void OnPlacementsUpdated(Rect region)
            {
                double now = _timer.Elapsed.TotalSeconds;
                if (_placementWrites == 0)
                {
                    _firstPlacementWrite = now;
                }
                _lastPlacementWrite = now;
                _placementWrites++;
                _placementRegion = region;
                _placementsRealtime = Time.realtimeSinceStartupAsDouble;
                _grassReady = -1.0;
            }

            private void OnChangeCaptured(LoogaStampWorld world, string change)
            {
                if (world != _world) return;
                _changes.Add(string.Format("{0:F3} s {1}", _timer.Elapsed.TotalSeconds, change));
            }

            // Checks each open readback once. The GPU has done a write when the readback after it is done.
            private void PollVisibility(double now)
            {
                for (int i = _probes.Count - 1; i >= 0; i--)
                {
                    if (!_probes[i].Request.done) continue;
                    if (_probes[i].Heights) _heightsVisible = Math.Max(_heightsVisible, now);
                    else _texturesVisible = Math.Max(_texturesVisible, now);
                    _probes.RemoveAt(i);
                }
                if (double.IsNaN(_placementsRealtime) || _grassReady >= 0.0) return;
                foreach (TerrainVegetationProvider provider in _providers)
                {
                    if (!provider || !provider.isActiveAndEnabled) continue;
                    if (provider.LastStreamingUpdateTime <= _placementsRealtime || provider.LastMissingRequiredCells > 0)
                    {
                        return;
                    }
                }
                _grassReady = now;
            }

            private void ReportVisibility()
            {
                double visible = Math.Max(Math.Max(_heightsVisible, _texturesVisible), _lastPlacementWrite);
                _report.AppendLine(string.Format("  visible: heights {0:F3} s, alphamaps {1:F3} s, placements {2:F3} s, " +
                    "grass loaded {3:F3} s; visible without grass {4:F3} s, settled {5:F3} s", _heightsVisible,
                    _texturesVisible, _lastPlacementWrite, _grassReady, visible, _synchronized));
                _report.AppendLine("  captured changes " + _changes.Count + ":");
                for (int i = 0; i < Math.Min(_changes.Count, 40); i++)
                {
                    _report.AppendLine("    " + _changes[i]);
                }
            }

            private void Finish()
            {
                _report.AppendLine("Heights after the revert vs a full evaluation: " + CompareWithFullEvaluation(_world));
                EditorApplication.update -= Tick;
                StampSections.Enabled = false;
                _world.HeightsUpdated -= OnHeightsUpdated;
                _world.TexturesUpdated -= OnTexturesUpdated;
                _world.PlacementsUpdated -= OnPlacementsUpdated;
                LoogaStampWorldUpdater.ChangeCaptured -= OnChangeCaptured;
                _moveTest = null;
                File.WriteAllText(_reportPath, _report.ToString());
                UnityEngine.Debug.Log("[LoogaStampTiming] Change test done: " + _reportPath);
            }
        }
    }
}
