using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain
{
    /// <summary>Rejects hidden terrain patches using shared prior-frame depth. Source terrain remains unchanged.</summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class TerrainOcclusionCamera : MonoBehaviour
    {
        [SerializeField] private Shader _depthShader;
        [SerializeField] private Shader _shadowBatchShader;
        [SerializeField] private bool _cullShadows = true;
        [SerializeField] private bool _occlusionCulling = true;
        [SerializeField] private bool _adaptiveShadowBatching = true;
        [SerializeField, Range(2, 16)] private int _minimumShadowBatchTiles = 4;
        private readonly Plane[] _shadowPlanes = new Plane[6];
        private readonly Vector4[] _planeValues = new Vector4[6];
        private readonly Vector4[] _lightValues = new Vector4[16];
        private static Light[] _lights;
        private static int _lightFrame = -1;
        [SerializeField, Range(0.00000001f, 0.01f)] private float _depthBias = 0.0000001f;
        private CommandBuffer _commands;
        private IDisposable _consumer;
        private Vector2Int _depthSize;

        /// <summary>Gets the allocated depth storage size.</summary>
        public Vector2Int DepthSize => _depthSize;

        /// <summary>Assigns the terrain-only depth shader for player builds.</summary>
        public void Configure(Shader depthShader, Shader shadowBatchShader = null, bool cullShadows = true,
            bool occlusionCulling = true, bool adaptiveShadowBatching = true, int minimumShadowBatchTiles = 4)
        {
            Release();
            _depthShader = depthShader;
            _shadowBatchShader = shadowBatchShader;
            _cullShadows = cullShadows;
            _occlusionCulling = occlusionCulling;
            _adaptiveShadowBatching = adaptiveShadowBatching;
            _minimumShadowBatchTiles = Mathf.Clamp(minimumShadowBatchTiles, 2, 16);
        }

        private void OnEnable()
        {
            _consumer ??= InstanceVisibility.RegisterConsumer(
                InstanceVisibilityConsumerKind.Terrain);
        }

        private void OnDisable()
        {
            _consumer?.Dispose();
            _consumer = null;
            Release();
        }

        private void OnDestroy()
        {
            _commands?.Dispose();
        }

        private void Release()
        {
            _commands?.Clear();
            TerrainRenderer.ReleaseShadowBatches(GetComponent<Camera>());
            _depthSize = default;
        }

        internal void Cull(Camera camera, List<TerrainRenderer> renderers)
        {
            if (renderers.Count == 0 || camera.stereoEnabled
                || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12
                || !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, GraphicsFormatUsage.LoadStore)) return;
            _commands ??= new CommandBuffer { name = "Looga Terrain Hi-Z" };
            _commands.Clear();
            if (_occlusionCulling)
            {
                CullDepth(camera, renderers);
            }
            if (_cullShadows)
            {
                CullShadows(camera);
            }
            Graphics.ExecuteCommandBuffer(_commands);
            TerrainRenderer.SubmitShadowBatches(camera, _shadowBatchShader,
                _adaptiveShadowBatching ? _minimumShadowBatchTiles : 2);
        }

        private void CullDepth(Camera camera, List<TerrainRenderer> renderers)
        {
            if (!InstanceVisibility.TryGet(camera,
                out InstanceVisibilityContext visibility)) return;
            _depthSize = new Vector2Int(visibility.Width, visibility.Height);
            TerrainRenderer.CullOccluded(camera, _commands,
                visibility.DepthPyramid, visibility.ViewProjection,
                visibility.Width, visibility.Height, visibility.Levels,
                Mathf.Max(_depthBias, visibility.Bias), visibility.Measure,
                visibility.SampleId);
        }

        private void CullShadows(Camera camera)
        {
            if (!Application.isPlaying || _lightFrame != Time.frameCount)
            {
                _lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
                _lightFrame = Time.frameCount;
            }
            Light[] cameraLights = _lights;
            if (camera.scene.IsValid())
            {
                var sceneLights = new List<Light>();
                foreach (GameObject root in camera.scene.GetRootGameObjects())
                {
                    sceneLights.AddRange(root.GetComponentsInChildren<Light>());
                }
                cameraLights = sceneLights.ToArray();
            }
            int count = 0;
            foreach (Light light in cameraLights)
            {
                if (!light || !light.isActiveAndEnabled || light.shadows == LightShadows.None || light.intensity <= 0) continue;
                // Unknown light types and overflow keep the original caster list.
                if (count == _lightValues.Length) return;
                if (light.type == LightType.Directional)
                {
                    Vector3 direction = light.transform.forward;
                    _lightValues[count++] = new Vector4(direction.x, direction.y, direction.z, 0);
                }
                else if (light.type == LightType.Point || light.type == LightType.Spot)
                {
                    Vector3 position = light.transform.position;
                    _lightValues[count++] = new Vector4(position.x, position.y, position.z, light.range);
                }
                else
                {
                    return;
                }
            }
            var pipeline = GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            float reach = pipeline ? pipeline.shadowDistance : camera.farClipPlane;
            int resolution = pipeline ? Mathf.Min(pipeline.mainLightShadowmapResolution, pipeline.additionalLightsShadowmapResolution) : 256;
            float normalBias = 1f;
            foreach (Light light in cameraLights)
            {
                if (light)
                {
                    normalBias = Mathf.Max(normalBias, light.shadowNormalBias + light.shadowBias);
                }
            }
            // Keep casters that can enter the view through shadow bias and filtering.
            float padding = 1f + 8f * Mathf.Max(reach * 2f, camera.orthographic ? camera.orthographicSize * 2f : 0f) / Mathf.Max(256, resolution) * normalBias;
            GeometryUtility.CalculateFrustumPlanes(camera, _shadowPlanes);
            for (int index = 0; index < 6; index++)
            {
                Plane plane = _shadowPlanes[index];
                _planeValues[index] = new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance + padding);
            }
            TerrainRenderer.CullShadows(camera, _commands, _planeValues, _lightValues, count);
        }
    }
}
