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
        [SerializeField] private bool _occlusionCulling = true;
        [SerializeField, Range(0.00000001f, 0.01f)] private float _depthBias = 0.0000001f;
        private CommandBuffer _commands;
        // GPU sampler: gives the GPU time of the Hi-Z occlusion test.
        private static readonly UnityEngine.Profiling.CustomSampler OcclusionGpuSample =
            UnityEngine.Profiling.CustomSampler.Create("Looga Terrain Hi-Z", true);
        private IDisposable _consumer;
        private Vector2Int _depthSize;

        /// <summary>Gets the allocated depth storage size.</summary>
        public Vector2Int DepthSize => _depthSize;

        /// <summary>Assigns the terrain-only depth shader for player builds.</summary>
        public void Configure(Shader depthShader, bool occlusionCulling = true)
        {
            Release();
            _depthShader = depthShader;
            _occlusionCulling = occlusionCulling;
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
            _depthSize = default;
        }

        internal void Cull(Camera camera, List<TerrainRenderer> renderers)
        {
            if (!_occlusionCulling || renderers.Count == 0 || camera.stereoEnabled
                || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12
                || !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, GraphicsFormatUsage.LoadStore)) return;
            _commands ??= new CommandBuffer { name = "Looga Terrain Hi-Z" };
            _commands.Clear();
            _commands.BeginSample(OcclusionGpuSample);
            CullDepth(camera);
            _commands.EndSample(OcclusionGpuSample);
            Graphics.ExecuteCommandBuffer(_commands);
        }

        private void CullDepth(Camera camera)
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
    }
}
