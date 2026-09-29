using System;
using System.Collections.Generic;
using System.Linq;
using LoogaSoft.Instancing;
using LoogaSoft.Inspector.Editor;
using LoogaSoft.Terrain.Instances;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Editor
{
    [CustomEditor(typeof(LoogaWorldController))]
    public sealed class LoogaWorldControllerEditor : UnityEditor.Editor
    {
        private List<VegetationCatalog.Entry> _catalog = new();
        private TerrainVegetationProvider[] _providers = Array.Empty<TerrainVegetationProvider>();
        private readonly Dictionary<UnityEngine.Object, Texture> _tilePreviews = new();
        private readonly HashSet<UnityEngine.Object> _pendingPreviews = new();
        private VegetationCatalog.Entry _selected;
        private string _error;
        private bool _worldOptions;
        private bool _details = true;
        private bool _trees;
        private bool _detailDefaults;
        private bool _treeDefaults;
        private bool _diagnostics;
        private bool _usage;
        private Camera _camera;
        private GUIStyle _tileLabelStyle;
        private double _previewDeadline;
        private bool _inspectorEnabled;
        private bool _providersDirty;
        private const float TileSize = 88f;
        private const float TileGap = 5f;

        private void OnEnable()
        {
            _inspectorEnabled = true;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Refresh();
            Undo.undoRedoPerformed += OnUndo;
        }

        private void OnDisable()
        {
            _inspectorEnabled = false;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            Undo.undoRedoPerformed -= OnUndo;
            StopPreviewLoading();
            _tilePreviews.Clear();
        }

        private void OnUndo()
        {
            if (!(target is LoogaWorldController controller) || !controller) return;
            Try(() => controller.ApplyVegetationSettings());
            if (controller.VegetationSettings) controller.VegetationSettings.NotifyChanged();
            Refresh();
            Repaint();
        }

        private void OnHierarchyChanged()
        {
            _providersDirty = true;
        }

        private void Refresh()
        {
            if (!(target is LoogaWorldController controller) || !controller)
            {
                StopPreviewLoading();
                _providers = Array.Empty<TerrainVegetationProvider>();
                _catalog = new List<VegetationCatalog.Entry>();
                _selected = null;
                _tilePreviews.Clear();
                return;
            }
            var previous = _selected;
            _providers = controller.GetVegetationProviders();
            _providersDirty = false;
            _catalog = VegetationCatalog.Build(_providers);
            _selected = previous == null ? null : _catalog.Find(e => e.Kind == previous.Kind && e.Source == previous.Source);
            _camera = _camera ? _camera : Camera.main;
            _tilePreviews.Clear();
            QueuePreviews();
        }

        public override void OnInspectorGUI()
        {
            var controller = (LoogaWorldController)target;
            if (_providersDirty) Refresh();
            serializedObject.Update();
            _worldOptions = LoogaGUILayout.Foldout("World Setup", _worldOptions, () =>
            {
                DrawPropertiesExcluding(serializedObject, "m_Script", "_vegetationSettings", "_previewVegetationInEditor");
                serializedObject.ApplyModifiedProperties();
                if (!string.IsNullOrEmpty(controller.LastReport)) EditorGUILayout.HelpBox(controller.LastReport, MessageType.Info);
                if (GUILayout.Button("Configure World")) { LoogaWorldSetupUtility.Configure(controller); Refresh(); }
                if (GUILayout.Button("Validate Setup")) LoogaWorldSetupUtility.Validate(controller);
                if (GUILayout.Button("Restore Native Rendering")) LoogaWorldSetupUtility.RestoreNative(controller);
            });
            EditorGUILayout.LabelField("Vegetation", EditorStyles.boldLabel);
            TerrainVegetationPreview.DrawControls(controller);
            var profile = controller.VegetationSettings;
            EditorGUI.BeginChangeCheck();
            var next = (VegetationSettings)EditorGUILayout.ObjectField("Shared settings", profile, typeof(VegetationSettings), false);
            if (EditorGUI.EndChangeCheck())
            {
                Assign(controller, next);
                profile = controller.VegetationSettings;
            }
            if (!profile)
            {
                EditorGUILayout.HelpBox("Each terrain uses its local settings. Create or assign a shared profile to control this world's details and trees.", MessageType.Info);
                if (GUILayout.Button("Create shared vegetation settings"))
                {
                    string path = EditorUtility.SaveFilePanelInProject("Create vegetation settings", "VegetationSettings", "asset", "Choose a shared profile location.");
                    if (!string.IsNullOrEmpty(path))
                    {
                        var created = CreateInstance<VegetationSettings>();
                        var provider = _providers.FirstOrDefault(item => item);
                        if (provider)
                        {
                            created.SetDefaults(VegetationKind.Detail, provider.LocalDetailQuality);
                            created.SetDefaults(VegetationKind.Tree, provider.LocalTreeQuality);
                        }
                        AssetDatabase.CreateAsset(created, path);
                        Assign(controller, created);
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Edits update rendering and streaming policy. Terrain placement maps and source assets remain unchanged. A shared profile affects every world that uses it.", MessageType.Info);
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh sources")) Refresh();
            if (GUILayout.Button("Count source data")) CountSources();
            EditorGUILayout.EndHorizontal();
            _details = LoogaGUILayout.Foldout(new GUIContent("Details (" + CountSources(VegetationKind.Detail) + ")"), _details,
                () => DrawCategory(profile, VegetationKind.Detail));
            _trees = LoogaGUILayout.Foldout(new GUIContent("Trees (" + CountSources(VegetationKind.Tree) + ")"), _trees,
                () => DrawCategory(profile, VegetationKind.Tree));
            _diagnostics = LoogaGUILayout.Foldout("Diagnostics", _diagnostics, () =>
            {
                var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (pipeline) EditorGUILayout.LabelField("URP shadow cap", pipeline.shadowDistance.ToString("0.#") + " m");
                EditorGUILayout.HelpBox("Detail counts are paint units, not generated blades. Loaded counts are resident placements. Visible counts are on-demand camera samples and can stall the GPU. Scene InstanceContainers are separate from terrain prototypes.", MessageType.None);
            });
            foreach (var provider in _providers)
            {
                if (!provider) continue;
                if (!string.IsNullOrEmpty(provider.Diagnostic))
                {
                    EditorGUILayout.HelpBox(provider.name + ": " + provider.Diagnostic, Application.isPlaying ? MessageType.Warning : MessageType.Info);
                    break;
                }
            }
            if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Error);
        }

        internal static void DrawDefaults(VegetationSettings profile, VegetationKind kind, ref string error)
        {
            InstanceQualitySettings quality = kind == VegetationKind.Detail ? profile.DetailDefaults : profile.TreeDefaults;
            var fields = VegetationQualityFields.None;
            if (!VegetationQualityGUI.Draw(ref quality, false, ref fields)) return;
            Undo.RecordObject(profile, "Change vegetation defaults");
            try { profile.SetDefaults(kind, quality); EditorUtility.SetDirty(profile); error = null; }
            catch (Exception exception) { error = exception.Message; }
        }

        private void Assign(LoogaWorldController controller, VegetationSettings settings)
        {
            Try(() =>
            {
                Undo.RecordObject(controller, "Assign vegetation settings");
                var providers = controller.GetVegetationProviders();
                Undo.RecordObjects(providers, "Assign vegetation settings");
                controller.SetVegetationSettings(settings);
                EditorUtility.SetDirty(controller);
                foreach (var provider in providers) EditorUtility.SetDirty(provider);
                EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
                Refresh();
            });
        }

        private void CountSources()
        {
            Try(() =>
            {
                try
                {
                    VegetationCatalog.Recount(_catalog, progress =>
                        EditorUtility.DisplayCancelableProgressBar("Count vegetation", "Reading terrain source data", progress));
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            });
        }

        private void QueuePreviews()
        {
            StopPreviewLoading();
            if (!_inspectorEnabled) return;
            EditorApplication.delayCall += LoadPreviews;
        }

        private void LoadPreviews()
        {
            EditorApplication.delayCall -= LoadPreviews;
            if (!_inspectorEnabled) return;
            foreach (var entry in _catalog)
            {
                if (!entry.Source) continue;
                Texture preview = AssetPreview.GetAssetPreview(entry.Source);
                if (preview)
                {
                    _tilePreviews[entry.Source] = preview;
                }
                else
                {
                    Texture thumbnail = AssetPreview.GetMiniThumbnail(entry.Source);
                    if (thumbnail)
                    {
                        _tilePreviews[entry.Source] = thumbnail;
                    }
                    if (AssetPreview.IsLoadingAssetPreview(entry.Source.GetInstanceID()))
                    {
                        _pendingPreviews.Add(entry.Source);
                    }
                }
            }
            if (_pendingPreviews.Count > 0)
            {
                _previewDeadline = EditorApplication.timeSinceStartup + 10;
                EditorApplication.update += UpdatePreviews;
            }
            Repaint();
        }

        private void UpdatePreviews()
        {
            if (!_inspectorEnabled || EditorApplication.timeSinceStartup >= _previewDeadline)
            {
                StopPreviewLoading();
                return;
            }
            foreach (var source in _pendingPreviews.ToArray())
            {
                if (!source || AssetPreview.IsLoadingAssetPreview(source.GetInstanceID())) continue;
                Texture preview = AssetPreview.GetAssetPreview(source);
                if (preview)
                {
                    _tilePreviews[source] = preview;
                }
                _pendingPreviews.Remove(source);
            }
            if (_pendingPreviews.Count != 0) return;
            StopPreviewLoading();
            Repaint();
        }

        private void StopPreviewLoading()
        {
            EditorApplication.delayCall -= LoadPreviews;
            EditorApplication.update -= UpdatePreviews;
            _pendingPreviews.Clear();
        }

        private int CountSources(VegetationKind kind)
        {
            return _catalog.Count(entry => entry.Kind == kind && entry.Source);
        }

        private void DrawCategory(VegetationSettings profile, VegetationKind kind)
        {
            var entries = _catalog.Where(entry => entry.Kind == kind && entry.Source).ToList();
            DrawCategorySummary(profile, kind, entries);
            EditorGUILayout.LabelField("Terrain sources", EditorStyles.boldLabel);
            DrawTiles(entries);
            if (_selected != null && !_selected.Source) _selected = null;
            if (_selected != null && _selected.Kind == kind)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                DrawSelected(profile);
                EditorGUILayout.EndVertical();
            }
            if (!profile) return;
            if (kind == VegetationKind.Detail)
            {
                _detailDefaults = LoogaGUILayout.Foldout("Defaults", _detailDefaults,
                    () => DrawDefaults(profile, kind, ref _error));
            }
            else
            {
                _treeDefaults = LoogaGUILayout.Foldout("Defaults", _treeDefaults,
                    () => DrawDefaults(profile, kind, ref _error));
            }
        }

        private void DrawCategorySummary(VegetationSettings profile, VegetationKind kind,
            IReadOnlyList<VegetationCatalog.Entry> entries)
        {
            int terrains = entries.SelectMany(entry => entry.Usages.Select(usage => usage.Provider))
                .Where(provider => provider).Distinct().Count();
            bool counted = entries.All(entry => entry.SourceCount.HasValue);
            long sourceCount = counted ? entries.Sum(entry => entry.SourceCount.Value) : 0;
            string countLabel = kind == VegetationKind.Detail ? "Paint units" : "Source placements";
            EditorGUILayout.LabelField(entries.Count + " sources across " + terrains + " terrains  |  " +
                countLabel + ": " + (counted ? sourceCount.ToString("N0") : "uncounted"),
                EditorStyles.wordWrappedMiniLabel);
            if (!profile) return;
            InstanceQualitySettings quality = kind == VegetationKind.Detail ? profile.DetailDefaults : profile.TreeDefaults;
            string view = quality.ViewDistance > 0 ? quality.ViewDistance.ToString("0.#") + " m" : "native terrain";
            string shadow = quality.ShadowMode == InstanceShadowMode.Off ? "Off" :
                quality.ShadowDistance > 0 ? quality.ShadowDistance.ToString("0.#") + " m" : "provider cap";
            EditorGUILayout.LabelField("Default view: " + view + "  |  Shadow: " + shadow,
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(5f);
        }

        private void DrawTiles(IReadOnlyList<VegetationCatalog.Entry> entries)
        {
            if (entries.Count == 0)
            {
                EditorGUILayout.HelpBox("No terrain sources in this category.", MessageType.None);
                return;
            }

            int columns = Mathf.Max(1, Mathf.FloorToInt((EditorGUIUtility.currentViewWidth - 48f) / (TileSize + TileGap)));
            for (int start = 0; start < entries.Count; start += columns)
            {
                EditorGUILayout.BeginHorizontal();
                for (int index = start; index < Mathf.Min(start + columns, entries.Count); index++)
                {
                    DrawTile(entries[index]);
                    if (index + 1 < Mathf.Min(start + columns, entries.Count)) GUILayout.Space(TileGap);
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(TileGap);
            }
        }

        private void DrawTile(VegetationCatalog.Entry entry)
        {
            Rect tile = GUILayoutUtility.GetRect(TileSize, TileSize, GUILayout.Width(TileSize), GUILayout.Height(TileSize));
            bool selected = _selected == entry;
            if (selected)
            {
                EditorGUI.DrawRect(tile, EditorGUIUtility.isProSkin
                    ? new Color(0.35f, 0.7f, 1f) : new Color(0.12f, 0.4f, 0.75f));
            }
            Rect tileContent = selected ? new Rect(tile.x + 2f, tile.y + 2f, tile.width - 4f, tile.height - 4f) : tile;
            GUI.Box(tileContent, GUIContent.none, EditorStyles.helpBox);
            Texture icon = _tilePreviews.TryGetValue(entry.Source, out Texture preview)
                ? preview : AssetDatabase.GetCachedIcon(AssetDatabase.GetAssetPath(entry.Source));
            if (icon)
            {
                GUI.DrawTexture(new Rect(tile.x + 19f, tile.y + 7f, 50f, 50f), icon, ScaleMode.ScaleToFit);
            }
            _tileLabelStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                wordWrap = true,
                clipping = TextClipping.Clip
            };
            GUI.Label(new Rect(tile.x + 4f, tile.y + 59f, tile.width - 8f, 26f), entry.Source.name, _tileLabelStyle);
            if (GUI.Button(tile, new GUIContent("", entry.Source.name), GUIStyle.none))
            {
                _selected = entry;
                _usage = false;
            }
        }

        private void DrawSelected(VegetationSettings profile)
        {
            var entry = _selected;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(entry.Source.name, EditorStyles.boldLabel);
            EditorGUILayout.ObjectField("Source asset", entry.Source, typeof(UnityEngine.Object), false);
            EditorGUILayout.LabelField(entry.Usages.Select(usage => usage.Provider).Where(provider => provider).Distinct().Count() +
                " terrains  |  " + entry.CountLabel + ": " + (entry.SourceCount?.ToString("N0") ?? "uncounted") +
                "  |  Loaded: " + entry.GetResidentCount().ToString("N0") +
                "  |  Visible: " + (entry.VisibleCount?.ToString("N0") ?? "unsampled"), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Ping asset")) EditorGUIUtility.PingObject(entry.Source);
            if (GUILayout.Button("Select asset")) Selection.activeObject = entry.Source;
            EditorGUILayout.EndHorizontal();
            if (entry.Source is GameObject prefab)
            {
                foreach (Material material in prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m).Distinct())
                    EditorGUILayout.ObjectField("Material", material, typeof(Material), false);
            }
            _usage = LoogaGUILayout.Foldout("Terrain usage", _usage, () =>
            {
                foreach (var usage in entry.Usages)
                    if (usage.Provider) EditorGUILayout.ObjectField("Slot " + usage.PrototypeIndex, usage.Provider, typeof(TerrainVegetationProvider), true);
            });
            EditorGUI.BeginChangeCheck();
            _camera = (Camera)EditorGUILayout.ObjectField("Diagnostic camera", _camera, typeof(Camera), true);
            if (EditorGUI.EndChangeCheck())
            {
                foreach (var item in _catalog) item.VisibleCount = null;
            }
            using (new EditorGUI.DisabledScope(!Application.isPlaying || !_camera))
            {
                if (GUILayout.Button("Sample visible placements")) Try(() => entry.ReadVisibleCount(_camera));
            }
            if (!profile) return;
            EditorGUILayout.LabelField("Source overrides", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Check a field to override it. Unchecked fields show the inherited value. Overrides follow this source asset across terrain tiles and prototype reordering.", MessageType.None);
            try
            {
                var saved = profile.Overrides.FirstOrDefault(o => o != null && o.Kind == entry.Kind && o.Source == entry.Source);
                var fields = saved?.Fields ?? VegetationQualityFields.None;
                var quality = profile.Resolve(entry.Kind, entry.Source);
                float minView = float.MaxValue, maxView = 0, minShadow = float.MaxValue, maxShadow = 0;
                var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                int validUsages = 0;
                foreach (var usage in entry.Usages)
                {
                    if (!usage.Provider) continue;
                    var terrain = usage.Provider.GetComponent<UnityEngine.Terrain>();
                    if (!terrain) continue;
                    float view = quality.ViewDistance > 0 ? quality.ViewDistance :
                        entry.Kind == VegetationKind.Detail ? terrain.detailObjectDistance : terrain.treeDistance;
                    float shadow = quality.ShadowDistance > 0 ? Mathf.Min(quality.ShadowDistance, usage.Provider.ShadowDistanceLimit) : usage.Provider.ShadowDistanceLimit;
                    if (pipeline) shadow = Mathf.Min(shadow, pipeline.shadowDistance);
                    minView = Mathf.Min(minView, view); maxView = Mathf.Max(maxView, view);
                    minShadow = Mathf.Min(minShadow, shadow); maxShadow = Mathf.Max(maxShadow, shadow);
                    validUsages++;
                }
                EditorGUILayout.LabelField("Effective view range", validUsages == 0 ? "Unavailable" : minView == maxView ? maxView.ToString("0.#") + " m" : minView.ToString("0.#") + "–" + maxView.ToString("0.#") + " m");
                EditorGUILayout.LabelField("Effective shadow cap", validUsages == 0 ? "Unavailable" : quality.ShadowMode == InstanceShadowMode.Off ? "Off" : minShadow == maxShadow ? maxShadow.ToString("0.#") + " m" : minShadow.ToString("0.#") + "–" + maxShadow.ToString("0.#") + " m");
                if (VegetationQualityGUI.Draw(ref quality, true, ref fields))
                {
                    Undo.RecordObject(profile, "Change vegetation override");
                    if (fields == VegetationQualityFields.None) profile.RemoveOverride(entry.Kind, entry.Source);
                    else profile.SetOverride(entry.Kind, entry.Source, fields, quality);
                    EditorUtility.SetDirty(profile);
                    _error = null;
                }
                if (saved != null && GUILayout.Button("Reset source to defaults"))
                {
                    Undo.RecordObject(profile, "Reset vegetation override");
                    profile.RemoveOverride(entry.Kind, entry.Source);
                    EditorUtility.SetDirty(profile);
                }
            }
            catch (Exception exception) { _error = exception.Message; }
        }

        private void Try(Action action)
        {
            try { action(); _error = null; }
            catch (Exception exception) { _error = exception.Message; }
        }

        [MenuItem("GameObject/Looga/Create and Configure World", false, 10)]
        private static void CreateAndConfigure()
        {
            Scene scene = SceneManager.GetActiveScene();
            LoogaWorldController controller = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                controller = root.GetComponentInChildren<LoogaWorldController>(true);
                if (controller) break;
            }
            if (!controller)
            {
                var owner = new GameObject("Looga World");
                SceneManager.MoveGameObjectToScene(owner, scene);
                Undo.RegisterCreatedObjectUndo(owner, "Create Looga World");
                controller = Undo.AddComponent<LoogaWorldController>(owner);
            }
            Selection.activeObject = controller.gameObject;
            LoogaWorldSetupUtility.Configure(controller);
            EditorSceneManager.MarkSceneDirty(scene);
        }

        [MenuItem("Tools/Looga/Configure Current World")]
        private static void ConfigureCurrent() => CreateAndConfigure();
    }
}
