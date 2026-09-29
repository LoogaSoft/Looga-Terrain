using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LoogaSoft.Terrain.Instances;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>Streams full-quality vegetation around Scene view cameras without changing runtime observers.</summary>
    [InitializeOnLoad]
    public static class TerrainVegetationPreview
    {
        private static readonly List<TerrainVegetationProvider> Providers = new();
        private static readonly Stopwatch Watch = new();
        private static LoogaWorldController[] _controllers = Array.Empty<LoogaWorldController>();
        private static Transform[] _observers = Array.Empty<Transform>();
        private static double[] _priorities = Array.Empty<double>();
        private static readonly Dictionary<TerrainVegetationProvider, double> LastUpdate = new();
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
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
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
                    EditorGUILayout.LabelField("Full authored quality. Terrain selection temporarily restores native authoring.",
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
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || _saving || _building)
            {
                _pauseReason = "Preview paused during editor transition";
                Stop();
                return;
            }
            if (now < _resumeAfter || now < _nextUpdate) return;
            _nextUpdate = now + 0.03;
            if (_controllers.Any(c => !c || !c.isActiveAndEnabled || !c.RenderVegetation || !c.PreviewVegetationInEditor))
            {
                Stop();
                RequestDiscovery();
            }
            bool rediscover = now >= _nextDiscovery;
            if (rediscover)
            {
                _controllers = Object.FindObjectsByType<LoogaWorldController>(FindObjectsSortMode.None)
                    .Where(c => c.isActiveAndEnabled && c.RenderVegetation && c.PreviewVegetationInEditor).ToArray();
                _nextDiscovery = now + 1;
            }
            GameObject selected = Selection.activeGameObject;
            if ((selected && selected.GetComponent<UnityEngine.Terrain>()) || Selection.activeObject is TerrainData)
            {
                _pauseReason = "Native preview while editing terrain";
                Stop();
                return;
            }
            _pauseReason = null;
            var observers = new List<Transform>();
            foreach (SceneView view in SceneView.sceneViews)
            {
                if (view && view.camera && !view.in2DMode)
                {
                    observers.Add(view.camera.transform);
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
            SortByPriority(now);
            bool repaint = false;
            Watch.Restart();
            foreach (TerrainVegetationProvider provider in Providers)
            {
                if (!provider || !provider.isActiveAndEnabled) continue;
                bool ready = provider.IsRendering;
                int count = provider.InstanceCount;
                provider.BeginEditorPreview(_observers);
                provider.ProcessChanges();
                LastUpdate[provider] = now;
                repaint |= ready != provider.IsRendering || count != provider.InstanceCount;
                if (Watch.Elapsed.TotalMilliseconds >= 8) break;
            }
            _error = null;
            if (repaint)
            {
                SceneView.RepaintAll();
            }
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
        }

        /// <summary>Releases all temporary renderer ownership and restores native foliage visibility.</summary>
        public static void Stop()
        {
            foreach (TerrainVegetationProvider provider in Providers)
            {
                if (provider) provider.EndEditorPreview();
            }
            Providers.Clear();
            _observers = Array.Empty<Transform>();
            LastUpdate.Clear();
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

        private static void OnSceneSaving(Scene scene, string path)
        {
            _saving = true;
            Stop();
        }

        private static void OnSceneSaved(Scene scene)
        {
            _saving = false;
            _resumeAfter = EditorApplication.timeSinceStartup + 0.1;
            RequestDiscovery();
        }

        private static void OnSceneClosing(Scene scene, bool removingScene)
        {
            Stop();
            RequestDiscovery();
        }

        private static void OnUndo()
        {
            Stop();
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
