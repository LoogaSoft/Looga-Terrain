using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Instances;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.EditorTools;
using UnityEditor.SceneManagement;
using UnityEditor.TerrainTools;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>
    /// Streams full-quality vegetation around Scene view cameras and game cameras with a vegetation interest, without
    /// changing runtime observers.
    /// </summary>
    [InitializeOnLoad]
    public static class TerrainVegetationPreview
    {
        private static readonly List<TerrainVegetationProvider> Providers = new();
        private static readonly HashSet<TerrainVegetationProvider> SelectedProviders = new();
        private static readonly Stopwatch Watch = new();
        // Reused each tick, so that the preview makes no garbage between discoveries.
        private static readonly List<Transform> ObserverList = new();
        private static LoogaWorldController[] _controllers = Array.Empty<LoogaWorldController>();
        private static VegetationInterest[] _interests = Array.Empty<VegetationInterest>();
        private static Transform[] _observers = Array.Empty<Transform>();
        private static double[] _priorities = Array.Empty<double>();
        private static readonly Dictionary<TerrainVegetationProvider, double> LastUpdate = new();
        // Dirty count of each terrain data at the last tick. An undo that changes it reloads the vegetation data.
        private static readonly Dictionary<TerrainData, int> DataVersions = new();
        // Native foliage visibility of the terrains that the previous script domain kept hidden, by terrain instance ID.
        // Their providers take it when the preview starts them again.
        private static readonly Dictionary<int, bool> HiddenFoliage = new();
        private const string HiddenFoliageKey = "LoogaSoft.Terrain.VegetationPreview.HiddenFoliage";
        private const double NearLimit = 1e6;
        private static double _nextDiscovery;
        private static double _nextUpdate;
        private static double _resumeAfter;
        private static bool _saving;
        private static bool _building;
        private static string _pauseReason;
        private static string _error;

        static TerrainVegetationPreview()
        {
            EditorApplication.update += Update;
            EditorApplication.hierarchyChanged += RequestDiscovery;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            LoadHiddenFoliage();
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            Undo.undoRedoPerformed += OnUndo;
        }

        /// <summary>Returns the preview state for one world.</summary>
        public static string GetStatus(LoogaWorldController controller)
        {
            if (!controller || !controller.PreviewVegetationInEditor) return "Native authoring preview";
            if (Application.isPlaying) return "Runtime rendering active";
            if (!string.IsNullOrEmpty(_error)) return _error;
            if (!string.IsNullOrEmpty(_pauseReason)) return _pauseReason;
            int owned = 0;
            int total = 0;
            long resident = 0;
            foreach (TerrainVegetationProvider provider in Providers)
            {
                if (!provider || provider.gameObject.scene != controller.gameObject.scene) continue;
                total++;
                if (provider.IsRendering) owned++;
                resident += provider.InstanceCount;
            }
            if (total == 0) return "Waiting for a Scene view camera";
            return $"{owned}/{total} terrains ready  |  {resident:N0} loaded placements";
        }

        /// <summary>Draws the preview preference and its current ownership state.</summary>
        public static void DrawControls(LoogaWorldController controller)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                bool enabled = EditorGUILayout.Toggle(new GUIContent("Looga Scene view preview",
                    "Uses authored density, mesh LODs, and shadow policy. Runtime camera observers remain unchanged."),
                    controller.PreviewVegetationInEditor);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(controller, "Change vegetation preview");
                    controller.SetEditorVegetationPreview(enabled);
                    EditorUtility.SetDirty(controller);
                    EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
                    RequestDiscovery();
                }
                EditorGUILayout.LabelField(GetStatus(controller), EditorStyles.wordWrappedMiniLabel);
                if (enabled)
                {
                    EditorGUILayout.LabelField("Full authored quality. A terrain paint tool uses native rendering on the selected terrain.",
                        EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private static void RequestDiscovery()
        {
            _nextDiscovery = 0;
        }

        private static void Update()
        {
            // An undo compares the terrain data with these versions. Stamp writes between two undo steps then do not
            // count as a change by the undo.
            foreach (TerrainVegetationProvider provider in Providers)
            {
                if (provider)
                {
                    RecordDataVersion(provider);
                }
            }
            try
            {
                UpdatePreview();
            }
            catch (Exception exception)
            {
                Stop();
                _error = "Preview paused: " + exception.Message;
                _resumeAfter = EditorApplication.timeSinceStartup + 2;
                UnityEngine.Debug.LogException(exception);
            }
        }

        private static void UpdatePreview()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_saving)
            {
                // A scene save runs inside one editor call, so a tick sees this flag only after a failed save.
                _saving = false;
                ReclaimNativeFoliage();
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode || _building)
            {
                _pauseReason = "Preview paused during editor transition";
                Stop();
                return;
            }
            // Scripts compile in the background. The loaded vegetation stays and the streams wait: a stop gave all
            // terrains to native foliage, which made the editor slow during each compilation.
            if (EditorApplication.isCompiling)
            {
                _pauseReason = "Preview paused while scripts compile";
                return;
            }
            if (now < _resumeAfter || now < _nextUpdate) return;
            _nextUpdate = now + 0.03;
            if (AnyControllerStopped())
            {
                Stop();
                RequestDiscovery();
            }
            bool rediscover = now >= _nextDiscovery;
            if (rediscover)
            {
                _controllers = Object.FindObjectsByType<LoogaWorldController>(FindObjectsSortMode.None)
                    .Where(c => c.isActiveAndEnabled && c.RenderVegetation && c.PreviewVegetationInEditor).ToArray();
                _interests = Object.FindObjectsByType<VegetationInterest>(FindObjectsSortMode.None);
                _nextDiscovery = now + 1;
            }
            _pauseReason = null;
            List<Transform> observers = ObserverList;
            observers.Clear();
            // An index loop: a foreach over the ArrayList allocates its enumerator.
            for (int i = 0; i < SceneView.sceneViews.Count; i++)
            {
                var view = (SceneView)SceneView.sceneViews[i];
                if (view && view.camera && !view.in2DMode)
                {
                    observers.Add(view.camera.transform);
                }
            }
            // Game cameras with a vegetation interest also observe. Without them, the Game view shows vegetation only
            // near a Scene view camera, and after a script reload a hidden Scene view keeps its camera at the world
            // origin until that view draws again.
            foreach (VegetationInterest interest in _interests)
            {
                if (interest && interest.isActiveAndEnabled && interest.TryGetComponent(out Camera camera) &&
                    camera.isActiveAndEnabled)
                {
                    observers.Add(interest.transform);
                }
            }
            if (_controllers.Length == 0 || observers.Count == 0)
            {
                Stop();
                return;
            }
            bool camerasChanged = _observers.Length != observers.Count;
            for (int i = 0; !camerasChanged && i < observers.Count; i++)
            {
                camerasChanged = _observers[i] != observers[i];
            }
            if (camerasChanged)
            {
                _observers = observers.ToArray();
            }
            if (camerasChanged || Providers.Count == 0 || rediscover)
            {
                RefreshProviders();
            }
            // Stream while the camera moves. Terrains within detail range of a Scene view camera use the
            // tick budget first, least recently updated first, so no nearby terrain starves.
            bool repaint = ReleaseSelectedTerrains();
            SortByPriority(now);
            Watch.Restart();
            foreach (TerrainVegetationProvider provider in Providers)
            {
                // While play mode ends, a tick can still see the providers of the play mode scene. They keep their
                // runtime observers and go away in the same transition.
                if (!provider || !provider.isActiveAndEnabled || SelectedProviders.Contains(provider) ||
                    Application.IsPlaying(provider)) continue;
                bool ready = provider.IsRendering;
                int count = provider.InstanceCount;
                provider.BeginEditorPreview(_observers);
                // After the start, because a start releases the terrain.
                AdoptHiddenFoliage(provider);
                provider.ProcessChanges();
                LastUpdate[provider] = now;
                RecordDataVersion(provider);
                repaint |= ready != provider.IsRendering || count != provider.InstanceCount;
                if (Watch.Elapsed.TotalMilliseconds >= 8) break;
            }
            _error = null;
            if (repaint)
            {
                SceneView.RepaintAll();
            }
        }

        // Native terrain paint tools need native foliage on the terrain that the user edits.
        // A selection alone keeps the preview. Native foliage on one dense terrain can cost about 0.7 s per frame.
        private static bool ReleaseSelectedTerrains()
        {
            SelectedProviders.Clear();
            Type tool = ToolManager.activeToolType;
            if (tool == null || !typeof(TerrainPaintToolWithOverlaysBase).IsAssignableFrom(tool)) return false;
            bool released = false;
            foreach (TerrainVegetationProvider provider in Providers)
            {
                if (!provider) continue;
                var terrain = provider.GetComponent<UnityEngine.Terrain>();
                bool selected = terrain && (Selection.Contains(terrain.gameObject)
                    || (Selection.activeObject is TerrainData data && terrain.terrainData == data));
                if (!selected) continue;
                SelectedProviders.Add(provider);
                released |= provider.IsRendering;
                provider.EndEditorPreview();
            }
            return released;
        }

        private static bool AnyControllerStopped()
        {
            foreach (LoogaWorldController controller in _controllers)
            {
                if (!controller || !controller.isActiveAndEnabled || !controller.RenderVegetation ||
                    !controller.PreviewVegetationInEditor) return true;
            }
            return false;
        }

        private static void SortByPriority(double now)
        {
            if (_priorities.Length < Providers.Count)
            {
                _priorities = new double[Providers.Count];
            }
            for (int i = 0; i < Providers.Count; i++)
            {
                _priorities[i] = Priority(Providers[i], now);
            }
            // Insertion sort. The list is small and nearly sorted between ticks.
            for (int i = 1; i < Providers.Count; i++)
            {
                TerrainVegetationProvider provider = Providers[i];
                double priority = _priorities[i];
                int j = i - 1;
                while (j >= 0 && _priorities[j] > priority)
                {
                    Providers[j + 1] = Providers[j];
                    _priorities[j + 1] = _priorities[j];
                    j--;
                }
                Providers[j + 1] = provider;
                _priorities[j + 1] = priority;
            }
        }

        // Lower values run first. Near terrains sort by last update time, which is always below NearLimit.
        // Other terrains sort after them by squared distance.
        private static double Priority(TerrainVegetationProvider provider, double now)
        {
            float distance = ObserverDistance(provider, out float range);
            if (distance <= range * range)
            {
                return LastUpdate.TryGetValue(provider, out double last) ? last - now : double.MinValue;
            }
            return NearLimit + distance;
        }

        private static float ObserverDistance(TerrainVegetationProvider provider, out float range)
        {
            range = 0f;
            if (!provider) return float.PositiveInfinity;
            var terrain = provider.GetComponent<UnityEngine.Terrain>();
            if (!terrain || !terrain.terrainData) return float.PositiveInfinity;
            range = terrain.detailObjectDistance;
            Vector3 origin = terrain.GetPosition();
            Vector3 size = terrain.terrainData.size;
            float nearest = float.PositiveInfinity;
            foreach (Transform observer in _observers)
            {
                if (!observer) continue;
                Vector3 position = observer.position;
                float x = Mathf.Max(0f, Mathf.Max(origin.x - position.x, position.x - origin.x - size.x));
                float z = Mathf.Max(0f, Mathf.Max(origin.z - position.z, position.z - origin.z - size.z));
                nearest = Mathf.Min(nearest, x * x + z * z);
            }
            return nearest;
        }

        private static void RefreshProviders()
        {
            var desired = new HashSet<TerrainVegetationProvider>();
            foreach (LoogaWorldController controller in _controllers)
            {
                if (!controller || !controller.isActiveAndEnabled || !controller.PreviewVegetationInEditor) continue;
                foreach (TerrainVegetationProvider provider in controller.GetVegetationProviders())
                {
                    if (provider && provider.isActiveAndEnabled)
                    {
                        desired.Add(provider);
                    }
                }
            }
            for (int i = Providers.Count - 1; i >= 0; i--)
            {
                TerrainVegetationProvider provider = Providers[i];
                if (provider && desired.Remove(provider)) continue;
                if (provider) provider.EndEditorPreview();
                Providers.RemoveAt(i);
            }
            Providers.AddRange(desired);
            RestoreHiddenFoliage(false);
        }

        /// <summary>Releases all temporary renderer ownership and restores native foliage visibility.</summary>
        public static void Stop()
        {
            foreach (TerrainVegetationProvider provider in Providers)
            {
                if (provider) provider.EndEditorPreview();
            }
            Providers.Clear();
            SelectedProviders.Clear();
            _observers = Array.Empty<Transform>();
            LastUpdate.Clear();
            RestoreHiddenFoliage(true);
        }

        // Keeps native foliage hidden on the terrains that Looga draws, and stores their visibility for the next script
        // domain. Native foliage on all terrains made the editor slow for seconds after each script reload.
        private static void OnBeforeAssemblyReload()
        {
            var values = new List<int>();
            foreach (TerrainVegetationProvider provider in Providers)
            {
                if (provider && provider.EndEditorPreviewForReload(out bool nativeState))
                {
                    values.Add(provider.GetComponent<UnityEngine.Terrain>().GetInstanceID());
                    values.Add(nativeState ? 1 : 0);
                }
            }
            // Terrains that this domain did not give back yet stay hidden too.
            foreach (KeyValuePair<int, bool> hidden in HiddenFoliage)
            {
                values.Add(hidden.Key);
                values.Add(hidden.Value ? 1 : 0);
            }
            HiddenFoliage.Clear();
            SessionState.SetIntArray(HiddenFoliageKey, values.ToArray());
            Stop();
        }

        private static void LoadHiddenFoliage()
        {
            int[] values = SessionState.GetIntArray(HiddenFoliageKey, Array.Empty<int>());
            SessionState.EraseIntArray(HiddenFoliageKey);
            for (int i = 0; i + 1 < values.Length; i += 2)
            {
                HiddenFoliage[values[i]] = values[i + 1] != 0;
            }
        }

        private static void AdoptHiddenFoliage(TerrainVegetationProvider provider)
        {
            if (HiddenFoliage.Count == 0) return;
            var terrain = provider.GetComponent<UnityEngine.Terrain>();
            if (terrain && HiddenFoliage.Remove(terrain.GetInstanceID(), out bool nativeState))
            {
                provider.AdoptHiddenNativeFoliage(nativeState);
            }
        }

        // Restores native foliage on hidden terrains that no provider takes: all of them, or those without a preview
        // provider.
        private static void RestoreHiddenFoliage(bool all)
        {
            if (HiddenFoliage.Count == 0) return;
            foreach (UnityEngine.Terrain terrain in Object.FindObjectsByType<UnityEngine.Terrain>(FindObjectsSortMode.None))
            {
                if (!HiddenFoliage.TryGetValue(terrain.GetInstanceID(), out bool nativeState)) continue;
                if (!all && terrain.TryGetComponent(out TerrainVegetationProvider provider) &&
                    Providers.Contains(provider)) continue;
                terrain.drawTreesAndFoliage = nativeState;
                HiddenFoliage.Remove(terrain.GetInstanceID());
            }
            if (all)
            {
                HiddenFoliage.Clear();
            }
        }

        /// <summary>Restores native foliage visibility before a player build serializes open scenes.</summary>
        internal static void PauseForBuild()
        {
            _building = true;
            Stop();
            // The build runs synchronously. The next editor tick occurs after success or failure.
            EditorApplication.delayCall += OnBuildFinished;
        }

        private static void OnBuildFinished()
        {
            _building = false;
            RequestDiscovery();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            Stop();
            RequestDiscovery();
        }

        // The scene file keeps the native foliage visibility. The loaded vegetation stays: a full stop gave the terrains
        // to native foliage until the streams loaded again, which took about 10 s of 0.7-2 s editor frames.
        private static void OnSceneSaving(Scene scene, string path)
        {
            _saving = true;
            foreach (TerrainVegetationProvider provider in Providers)
            {
                if (provider && provider.gameObject.scene == scene)
                {
                    provider.RestoreNativeFoliage();
                }
            }
            // After a script reload, a terrain stays hidden until its provider takes it. A save in that time wrote the
            // hidden state into the scene. On the next load, the provider then did not take the terrain.
            SetHiddenFoliage(scene, true);
        }

        private static void OnSceneSaved(Scene scene)
        {
            _saving = false;
            ReclaimNativeFoliage();
            RequestDiscovery();
        }

        private static void ReclaimNativeFoliage()
        {
            foreach (TerrainVegetationProvider provider in Providers)
            {
                if (provider)
                {
                    provider.ReclaimNativeFoliage();
                }
            }
            SetHiddenFoliage(default, false);
        }

        // Writes the stored visibility to the hidden terrains of a scene that no provider took yet, or hides them again.
        // A default scene selects the terrains of all scenes.
        private static void SetHiddenFoliage(Scene scene, bool restore)
        {
            if (HiddenFoliage.Count == 0) return;
            foreach (UnityEngine.Terrain terrain in Object.FindObjectsByType<UnityEngine.Terrain>(FindObjectsSortMode.None))
            {
                if (scene.IsValid() && terrain.gameObject.scene != scene) continue;
                if (HiddenFoliage.TryGetValue(terrain.GetInstanceID(), out bool nativeState))
                {
                    terrain.drawTreesAndFoliage = restore && nativeState;
                }
            }
        }

        private static void RecordDataVersion(TerrainVegetationProvider provider)
        {
            var terrain = provider.GetComponent<UnityEngine.Terrain>();
            if (terrain && terrain.terrainData)
            {
                DataVersions[terrain.terrainData] = EditorUtility.GetDirtyCount(terrain.terrainData);
            }
        }

        private static void OnSceneClosing(Scene scene, bool removingScene)
        {
            Stop();
            RequestDiscovery();
        }

        // An undo keeps the loaded vegetation. The providers find terrain, density and prototype changes themselves. An
        // undo can also restore an older terrain state with native foliage visible, or older detail and tree data. The
        // versions are from the last editor tick, so only terrain data that the undo itself changed loads again.
        private static void OnUndo()
        {
            foreach (TerrainVegetationProvider provider in Providers)
            {
                if (!provider) continue;
                provider.ReclaimNativeFoliage();
                var terrain = provider.GetComponent<UnityEngine.Terrain>();
                TerrainData data = terrain ? terrain.terrainData : null;
                if (!data || !DataVersions.TryGetValue(data, out int version) ||
                    version == EditorUtility.GetDirtyCount(data)) continue;
                // Detail patches load again while Looga draws the old ones. Trees load again in full.
                provider.InvalidateDetails(new RectInt(0, 0, data.detailWidth, data.detailHeight));
                if (data.treeInstanceCount > 0)
                {
                    provider.InvalidateTrees();
                }
                DataVersions[data] = EditorUtility.GetDirtyCount(data);
            }
            RequestDiscovery();
        }
    }

    /// <summary>Prevents temporary Scene view foliage ownership from entering player data.</summary>
    internal sealed class TerrainVegetationPreviewBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue;

        public void OnPreprocessBuild(BuildReport report)
        {
            TerrainVegetationPreview.PauseForBuild();
        }
    }
}
