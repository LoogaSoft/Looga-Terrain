using System;
using System.Collections.Generic;
using System.Diagnostics;
using LoogaSoft.Terrain.Stamps;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using ArrayState = LoogaSoft.Terrain.Surface.Editor.LoogaTerrainSurfaceArrayBuilder.ArrayState;
using Status = LoogaSoft.Terrain.Surface.Editor.LoogaTerrainSurfaceArrayBuilder.Status;

namespace LoogaSoft.Terrain.Surface.Editor
{
    /// <summary>
    /// Keeps the texture arrays of Looga terrain surfaces up to date in the editor. A change to a surface, a source
    /// texture or a terrain layer queues a check. The check runs after the changes stop for a short time. It assigns
    /// up-to-date arrays, and it starts a background build for out-of-date arrays when the surface has Auto Rebuild on.
    /// A build runs one layer per editor update, shows its progress in the status bar and can be cancelled. It does not
    /// run in play mode, during compilation or a player build, or while a stamp world updates the terrain.
    /// </summary>
    [InitializeOnLoad]
    public static class LoogaTerrainSurfaceAutoBuild
    {
        // Time without changes before a check runs, in seconds.
        private const double CheckDelay = 1.0;
        // Time that one editor update can use for cached layers, in milliseconds. A packed layer can take longer.
        private const double StepBudgetMilliseconds = 30.0;
        // Time that the inspector keeps a status before it asks again, in seconds.
        private const double StatusLifetime = 2.0;

        private static readonly HashSet<LoogaTerrainSurface> Pending = new();
        private static readonly Dictionary<LoogaTerrainSurface, (double Time, Status Value)> Statuses = new();
        // Surfaces with a failed build. They do not build again automatically until they or an asset change.
        private static readonly HashSet<LoogaTerrainSurface> Failed = new();
        // Surfaces with a Rebuild request that waits for another build. They build also when Auto Rebuild is off.
        private static readonly HashSet<LoogaTerrainSurface> Forced = new();
        private static bool _checkAll = true;
        private static double _checkTime;
        private static LoogaTerrainSurfaceArrayBuilder.BuildRun _run;
        private static int _progressId = -1;
        private static bool _cancel;

        static LoogaTerrainSurfaceAutoBuild()
        {
            _checkTime = EditorApplication.timeSinceStartup + CheckDelay;
            LoogaTerrainSurface.AnyChanged += Queue;
            EditorApplication.update += Update;
            // A domain reload or play mode would lose the unsaved arrays. The cache keeps the finished layers.
            AssemblyReloadEvents.beforeAssemblyReload += () => StopRun("The editor reloaded the scripts.");
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) StopRun("The editor entered play mode.");
                if (state == PlayModeStateChange.EnteredEditMode) QueueAll();
            };
            EditorApplication.quitting += () => StopRun("The editor closed.");
        }

        /// <summary>Queues a check of one surface.</summary>
        public static void Queue(LoogaTerrainSurface surface)
        {
            if (!surface || !EditorUtility.IsPersistent(surface)) return;
            Pending.Add(surface);
            Statuses.Remove(surface);
            Failed.Remove(surface);
            _checkTime = EditorApplication.timeSinceStartup + CheckDelay;
        }

        /// <summary>Queues a check of every surface in the project.</summary>
        public static void QueueAll()
        {
            _checkAll = true;
            Statuses.Clear();
            _checkTime = EditorApplication.timeSinceStartup + CheckDelay;
        }

        /// <summary>Starts a build of the requested layout now, also when Auto Rebuild is off.</summary>
        public static void Rebuild(LoogaTerrainSurface surface)
        {
            if (_run != null)
            {
                if (_run.Surface == surface) StopRun("A new build started.");
                else
                {
                    Forced.Add(surface);
                    Pending.Add(surface);
                    return;
                }
            }
            Failed.Remove(surface);
            StartRun(surface);
        }

        /// <summary>Cancels the running build. The finished layers stay in the slice cache.</summary>
        public static void Cancel()
        {
            _cancel = true;
        }

        /// <summary>True while a build of the surface runs.</summary>
        public static bool IsBuilding(LoogaTerrainSurface surface, out float progress, out string description)
        {
            bool building = _run != null && _run.Surface == surface;
            progress = building ? _run.Progress : 0f;
            description = building ? _run.Layout + " arrays: " + _run.Description : string.Empty;
            return building;
        }

        /// <summary>Array state of a surface for the inspector. It is kept for a short time.</summary>
        public static bool TryGetStatus(LoogaTerrainSurface surface, out Status status, out string error)
        {
            error = string.Empty;
            double now = EditorApplication.timeSinceStartup;
            if (Statuses.TryGetValue(surface, out (double Time, Status Value) cached) && now - cached.Time < StatusLifetime)
            {
                status = cached.Value;
                return true;
            }
            try
            {
                status = LoogaTerrainSurfaceArrayBuilder.GetStatus(surface);
                Statuses[surface] = (now, status);
                return true;
            }
            catch (Exception exception)
            {
                status = default;
                error = exception.Message;
                return false;
            }
        }

        /// <summary>True when the last build of the surface failed.</summary>
        public static bool HasFailed(LoogaTerrainSurface surface)
        {
            return Failed.Contains(surface);
        }

        private static void Update()
        {
            if (_run != null)
            {
                StepRun();
                return;
            }
            if (!_checkAll && Pending.Count == 0) return;
            if (EditorApplication.timeSinceStartup < _checkTime || !CanBuild()) return;

            var surfaces = new List<LoogaTerrainSurface>(Pending);
            Pending.Clear();
            if (_checkAll)
            {
                _checkAll = false;
                foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(LoogaTerrainSurface)))
                {
                    var surface = AssetDatabase.LoadAssetAtPath<LoogaTerrainSurface>(AssetDatabase.GUIDToAssetPath(guid));
                    if (surface && !surfaces.Contains(surface)) surfaces.Add(surface);
                }
            }
            foreach (LoogaTerrainSurface surface in surfaces)
            {
                if (surface) Check(surface);
            }
        }

        private static void Check(LoogaTerrainSurface surface)
        {
            Statuses.Remove(surface);
            if (!TryGetStatus(surface, out Status status, out string error))
            {
                Debug.LogWarning("Looga terrain surface '" + surface.name + "': cannot check the arrays. " + error, surface);
                return;
            }
            switch (status.State)
            {
                case ArrayState.UpToDate:
                    SyncSamplerSettings(surface);
                    MeasureMissingAlbedoMeans(surface);
                    break;
                case ArrayState.Unassigned when LoogaTerrainSurfaceArrayBuilder.Assign(surface, status.Layout):
                    Statuses.Remove(surface);
                    break;
                // An array that cannot be assigned is missing from its asset. Build it again.
                case ArrayState.Unassigned:
                case ArrayState.OutOfDate:
                case ArrayState.Missing:
                    bool forced = Forced.Contains(surface);
                    if (!forced && (!surface.AutoRebuild || Failed.Contains(surface))) break;
                    if (_run != null)
                    {
                        Pending.Add(surface);
                        break;
                    }
                    Forced.Remove(surface);
                    StartRun(surface);
                    break;
            }
        }

        private static void StartRun(LoogaTerrainSurface surface)
        {
            try
            {
                _run = new LoogaTerrainSurfaceArrayBuilder.BuildRun(surface, surface.Packing);
            }
            catch (Exception exception)
            {
                Fail(surface, exception);
                return;
            }
            _cancel = false;
            _progressId = Progress.Start("Looga Terrain Surface", "Building the arrays of '" + surface.name + "'");
            Progress.RegisterCancelCallback(_progressId, () =>
            {
                _cancel = true;
                return true;
            });
        }

        private static void StepRun()
        {
            if (_cancel)
            {
                StopRun("The build was cancelled.");
                return;
            }
            if (!_run.Surface)
            {
                StopRun("The surface was deleted.");
                return;
            }
            // Wait while the editor compiles or the terrain updates. The run keeps its state.
            if (!CanBuild()) return;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                while (!_run.IsDone && stopwatch.Elapsed.TotalMilliseconds < StepBudgetMilliseconds)
                {
                    _run.Step();
                }
                Progress.Report(_progressId, _run.Progress, _run.Description);
                if (!_run.IsDone) return;

                LoogaTerrainSurface surface = _run.Surface;
                LoogaTerrainSurfaceArrayBuilder.Report report = _run.Finish();
                Debug.Log("Looga terrain surface '" + surface.name + "': " + report, surface);
                EndRun(Progress.Status.Succeeded);
                // An input can change during the build. The next check builds only the changed layers.
                Queue(surface);
            }
            catch (Exception exception)
            {
                LoogaTerrainSurface surface = _run.Surface;
                EndRun(Progress.Status.Failed);
                Fail(surface, exception);
            }
        }

        private static void StopRun(string reason)
        {
            if (_run == null) return;
            LoogaTerrainSurface surface = _run.Surface;
            EndRun(Progress.Status.Canceled);
            Debug.Log("Looga terrain surface '" + (surface ? surface.name : "deleted") + "': build stopped. " + reason
                + " The finished layers stay in the cache.");
        }

        private static void EndRun(Progress.Status status)
        {
            _run?.Dispose();
            _run = null;
            _cancel = false;
            if (_progressId >= 0 && Progress.Exists(_progressId))
            {
                Progress.Finish(_progressId, status);
            }
            _progressId = -1;
        }

        private static void Fail(LoogaTerrainSurface surface, Exception exception)
        {
            if (surface) Failed.Add(surface);
            Debug.LogError("Looga terrain surface '" + (surface ? surface.name : "deleted")
                + "': the array build failed. Fix the problem, then change the surface or use Rebuild.\n" + exception, surface);
        }

        // Builds change assets and use the GPU. They wait for a quiet editor.
        private static bool CanBuild()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
            {
                return false;
            }
            foreach (LoogaStampWorld world in Object.FindObjectsByType<LoogaStampWorld>(FindObjectsSortMode.None))
            {
                if (world.IsUpdating || world.HasRequestedUpdate) return false;
            }
            return true;
        }

        // Arrays built before the surface kept the albedo means have none. Measure them once.
        private static void MeasureMissingAlbedoMeans(LoogaTerrainSurface surface)
        {
            if (!surface.AlbedoArray || surface.AlbedoMeans.Count == surface.AlbedoArray.depth) return;
            LoogaTerrainSurfaceArrayBuilder.MeasureAlbedoMeans(surface);
            EditorUtility.SetDirty(surface);
            AssetDatabase.SaveAssetIfDirty(surface);
        }

        // Filter settings do not need a build. Copy them to the saved arrays.
        private static void SyncSamplerSettings(LoogaTerrainSurface surface)
        {
            foreach (Texture2DArray array in new[] { surface.AlbedoArray, surface.NormalArray, surface.MaskArray })
            {
                if (!array || array.anisoLevel == surface.ArrayAnisoLevel) continue;
                array.anisoLevel = surface.ArrayAnisoLevel;
                EditorUtility.SetDirty(array);
                AssetDatabase.SaveAssetIfDirty(array);
            }
        }

        internal static void ClearFailures()
        {
            Failed.Clear();
        }
    }

    /// <summary>
    /// Queues a surface check when an asset that the surface reads (a texture or a terrain layer) is imported, moved or
    /// deleted. A changed surface asset or pack shader queues every surface. It only reads asset paths and types. It
    /// does not change texture imports.
    /// </summary>
    internal sealed class LoogaTerrainSurfaceAssetWatcher : AssetPostprocessor
    {
        private const string PackShaderFile = "LoogaTerrainSurfacePack.shader";

        // The asset paths that each surface read at the last change, so a deleted path still finds its surfaces.
        private static readonly Dictionary<string, HashSet<string>> Dependencies = new();

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            bool anyDeletedOrMoved = deletedAssets.Length > 0 || movedAssets.Length > 0;
            if (!anyDeletedOrMoved && !Array.Exists(importedAssets, IsSurfaceInput)) return;

            var changed = new HashSet<string>(importedAssets);
            changed.UnionWith(deletedAssets);
            changed.UnionWith(movedAssets);
            changed.UnionWith(movedFromAssetPaths);
            // Without earlier paths, a deletion cannot find the surfaces that used the deleted asset.
            bool checkAll = Dependencies.Count == 0 && anyDeletedOrMoved;
            foreach (string path in changed)
            {
                checkAll |= path.EndsWith(PackShaderFile, StringComparison.Ordinal) || Dependencies.ContainsKey(path) ||
                    AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(LoogaTerrainSurface);
            }

            var surfaces = new Dictionary<string, HashSet<string>>();
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(LoogaTerrainSurface)))
            {
                string surfacePath = AssetDatabase.GUIDToAssetPath(guid);
                surfaces[surfacePath] = new HashSet<string>(AssetDatabase.GetDependencies(surfacePath, true));
            }
            foreach (KeyValuePair<string, HashSet<string>> surface in surfaces)
            {
                bool reads = Dependencies.TryGetValue(surface.Key, out HashSet<string> earlier) &&
                    earlier.Overlaps(changed) || surface.Value.Overlaps(changed);
                if (!checkAll && !reads) continue;

                var asset = AssetDatabase.LoadAssetAtPath<LoogaTerrainSurface>(surface.Key);
                LoogaTerrainSurfaceAutoBuild.Queue(asset);
            }
            Dependencies.Clear();
            foreach (KeyValuePair<string, HashSet<string>> surface in surfaces)
            {
                Dependencies.Add(surface.Key, surface.Value);
            }
        }

        private static bool IsSurfaceInput(string path)
        {
            Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return type == typeof(Texture2D) || type == typeof(TerrainLayer) || type == typeof(LoogaTerrainSurface) ||
                type == typeof(Shader) && path.EndsWith(PackShaderFile, StringComparison.Ordinal);
        }
    }
}
