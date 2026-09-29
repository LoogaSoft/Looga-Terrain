using LoogaSoft.Instancing;
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Instances
{
    /// <summary>Binds native ground textures. Optional height snapshots preserve conformance motion history without a surface cache.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class TerrainGroundBindings : MonoBehaviour
    {
        /// <summary>One terrain and an optional linear world-UV tint map.</summary>
        [Serializable]
        public struct Surface
        {
            /// <summary>Native terrain that provides world bounds and heights.</summary>
            public UnityEngine.Terrain Terrain;
            /// <summary>Optional color texture mapped across the terrain bounds.</summary>
            public Texture ColorMap;
            /// <summary>Multiplier for the optional color map, or the direct ground color.</summary>
            public Color Tint;
            /// <summary>Optional data texture sampled by custom ground receivers.</summary>
            [Tooltip("Optional world-UV data map for custom shaders. Interpret the RGBA channels in the receiver.")]
            public Texture ExtraMap;
            /// <summary>Application-defined values for this terrain surface.</summary>
            [Tooltip("Optional per-surface values for biome, wind or other receiver inputs.")]
            public Vector4 ExtraData;
        }
        [SerializeField] private Surface[] _surfaces = Array.Empty<Surface>();
        [SerializeField, Tooltip("Keep native height history for conformance motion vectors. Disable only when terrain heights remain static.")]
        private bool _trackHeightHistory = true;
        private TerrainGroundHistory _history;
#if UNITY_EDITOR
        private int _editorFrame;
        private void BeginEditorFrame() => _editorFrame++;
#endif
        private int Frame
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) return _editorFrame;
#endif
                return Time.frameCount;
            }
        }
        private static TerrainGroundBindings _owner;
        private readonly Vector4[] _origins = new Vector4[16];
        private readonly Vector4[] _sizes = new Vector4[16];
        private readonly Vector4[] _tints = new Vector4[16];
        private readonly Vector4[] _extraData = new Vector4[16];
        private static readonly int[] _heights = Ids("_LoogaGroundHeight");
        private static readonly int[] _previousHeights = Ids("_LoogaGroundPreviousHeight");
        private static readonly int[] _colors = Ids("_LoogaGroundColor");
        private static readonly int[] _extraMaps = Ids("_LoogaGroundExtra");

        /// <summary>Configuration error, or null when direct bindings are valid.</summary>
        public string Diagnostic { get; private set; }

        private void OnEnable()
        {
            if (_owner && _owner != this)
            {
                Diagnostic = "Only one ground binding set can own shader globals.";
                return;
            }
            _owner = this;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += BeginEditorFrame;
#endif
            RenderPipelineManager.beginCameraRendering += BeforeCamera;
        }
        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= BeginEditorFrame;
#endif
            if (_owner != this) return;
            _owner = null;
            Shader.SetGlobalInt("_LoogaGroundCount", 0);
            for (int i = 0; i < 16; i++)
            {
                Shader.SetGlobalTexture(_heights[i], Texture2D.blackTexture);
                Shader.SetGlobalTexture(_previousHeights[i], Texture2D.blackTexture);
                Shader.SetGlobalTexture(_colors[i], Texture2D.whiteTexture);
                Shader.SetGlobalTexture(_extraMaps[i], Texture2D.whiteTexture);
            }
            _history?.Dispose();
            _history = null;
        }

        /// <summary>Assign at most sixteen explicit terrain surfaces. Use matching tint maps across shared tile edges.</summary>
        public void Configure(Surface[] surfaces)
        {
            if (surfaces == null || surfaces.Length > 16)
            {
                throw new ArgumentException("Assign zero to sixteen direct ground surfaces.");
            }
            _surfaces = (Surface[])surfaces.Clone();
        }

        private void BeforeCamera(ScriptableRenderContext context, Camera camera) => Publish();

        /// <summary>Refresh references after source replacement. Height texture edits are visible without a cache refill.</summary>
        public void Publish()
        {
            if (_owner != this) return;
            if (_surfaces.Length > 16)
            {
                Diagnostic = "Ground bindings exceed the sixteen-surface limit.";
                Shader.SetGlobalInt("_LoogaGroundCount", 0);
                return;
            }
            if (_trackHeightHistory && _history == null)
            {
                _history = new TerrainGroundHistory();
            }
            int count = 0;
            foreach (var surface in _surfaces)
            {
                var terrain = surface.Terrain;
                if (!terrain || !terrain.terrainData || !terrain.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (terrain.transform.rotation != Quaternion.identity || terrain.transform.lossyScale != Vector3.one)
                {
                    Diagnostic = "Native ground surfaces require unrotated, unscaled terrain transforms.";
                    Shader.SetGlobalInt("_LoogaGroundCount", 0);
                    return;
                }
                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                _origins[count] = new Vector4(origin.x, origin.y, origin.z, terrain.terrainData.heightmapResolution);
                _sizes[count] = new Vector4(size.x, size.y, size.z, surface.ColorMap ? 1 : 0);
                _tints[count] = surface.Tint;
                _extraData[count] = surface.ExtraData;
                Shader.SetGlobalTexture(_extraMaps[count], surface.ExtraMap ? surface.ExtraMap : Texture2D.whiteTexture);
                Shader.SetGlobalTexture(_heights[count], terrain.terrainData.heightmapTexture);
                Shader.SetGlobalTexture(_previousHeights[count], _trackHeightHistory ? _history.Previous(terrain.terrainData, Frame) : terrain.terrainData.heightmapTexture);
                Shader.SetGlobalTexture(_colors[count], surface.ColorMap ? surface.ColorMap : Texture2D.whiteTexture);
                count++;
            }
            // Clear unused references before releasing their history textures.
            for (int i = count; i < 16; i++)
            {
                Shader.SetGlobalTexture(_heights[i], Texture2D.blackTexture);
                Shader.SetGlobalTexture(_previousHeights[i], Texture2D.blackTexture);
                Shader.SetGlobalTexture(_colors[i], Texture2D.whiteTexture);
                Shader.SetGlobalTexture(_extraMaps[i], Texture2D.whiteTexture);
            }
            if (!_trackHeightHistory)
            {
                _history?.Dispose();
                _history = null;
            }
            Shader.SetGlobalVectorArray("_LoogaGroundOrigins", _origins);
            Shader.SetGlobalVectorArray("_LoogaGroundSizes", _sizes);
            Shader.SetGlobalVectorArray("_LoogaGroundTints", _tints);
            Shader.SetGlobalVectorArray("_LoogaGroundExtraData", _extraData);
            Shader.SetGlobalInt("_LoogaGroundCount", count);
            _history?.RemoveUnused(Frame);
            Diagnostic = null;
        }

        /// <summary>Sample native height and a seam-aware normal for authoring or gameplay alignment.</summary>
        public bool TrySample(Vector3 world, out float height, out Vector3 normal)
        {
            normal = Vector3.up;
            if (!SampleHeight(world.x, world.z, out height)) return false;
            const float step = 0.25f;
            float left = SampleHeight(world.x - step, world.z, out float value) ? value : height;
            float right = SampleHeight(world.x + step, world.z, out value) ? value : height;
            float back = SampleHeight(world.x, world.z - step, out value) ? value : height;
            float front = SampleHeight(world.x, world.z + step, out value) ? value : height;
            normal = new Vector3((left - right) / (step * 2), 1, (back - front) / (step * 2)).normalized;
            return true;
        }

        private bool SampleHeight(float x, float z, out float height)
        {
            height = 0;
            int count = 0;
            foreach (var surface in _surfaces)
            {
                var terrain = surface.Terrain;
                if (!terrain || !terrain.terrainData || !terrain.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (terrain.transform.rotation != Quaternion.identity || terrain.transform.lossyScale != Vector3.one) return false;
                Vector3 local = new Vector3(x, 0, z) - terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                if (local.x < 0 || local.z < 0 || local.x > size.x || local.z > size.z)
                {
                    continue;
                }
                height += terrain.terrainData.GetInterpolatedHeight(local.x / size.x, local.z / size.z) + terrain.transform.position.y;
                count++;
            }
            if (count == 0) return false;
            height /= count;
            return true;
        }

        private static int[] Ids(string prefix)
        {
            var result = new int[16];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Shader.PropertyToID(prefix + i);
            }
            return result;
        }
    }
}
