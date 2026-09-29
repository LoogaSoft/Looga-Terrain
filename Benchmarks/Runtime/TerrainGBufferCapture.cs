using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace LoogaSoft.Terrain.Benchmarks
{
    /// <summary>
    /// Captures G-buffer attachments on request, outside benchmark measurements.
    /// </summary>
    public sealed class TerrainGBufferCapture : ScriptableRendererFeature
    {
        private readonly RTHandle[] _copies = new RTHandle[3];
        private CapturePass _pass;
        private Camera _camera;
        private bool _requested;
        private int _recordedFrame = -1;

        /// <summary>
        /// Gets whether the requested frame has completed rendering.
        /// </summary>
        public bool Ready => _recordedFrame >= 0 && Time.frameCount > _recordedFrame;

        #region Lifecycle
        public override void Create()
        {
            _pass = new CapturePass(this) { renderPassEvent = RenderPassEvent.AfterRenderingGbuffer };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_requested && renderingData.cameraData.camera == _camera)
            {
                renderer.EnqueuePass(_pass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseCopies();
        }
        #endregion

        #region Capture
        /// <summary>
        /// Requests attachments from the next render of the specified camera.
        /// </summary>
        public void Request(Camera camera)
        {
            _camera = camera;
            _recordedFrame = -1;
            _requested = true;
        }

        /// <summary>
        /// Saves diagnostic PNG files after rendering. This method blocks for readback.
        /// </summary>
        public bool Save(string directory, string prefix)
        {
            if (!Ready) return false;
            RenderTexture previous = RenderTexture.active;
            try
            {
                for (int index = 0; index < _copies.Length; index++)
                {
                    if (_copies[index] == null) return false;
                    RenderTexture source = _copies[index].rt;
                    RenderTexture.active = source;
                    var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
                    image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                    image.Apply();
                    File.WriteAllBytes(Path.Combine(directory, prefix + "-GBuffer" + index + ".png"), image.EncodeToPNG());
                    Object.Destroy(image);
                }
            }
            finally
            {
                RenderTexture.active = previous;
                ReleaseCopies();
            }
            return true;
        }

        private void ReleaseCopies()
        {
            _requested = false;
            for (int index = 0; index < _copies.Length; index++)
            {
                _copies[index]?.Release();
                _copies[index] = null;
            }
        }
        #endregion

        private sealed class CapturePass : ScriptableRenderPass
        {
            private readonly TerrainGBufferCapture _owner;

            public CapturePass(TerrainGBufferCapture owner)
            {
                _owner = owner;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var camera = frameData.Get<UniversalCameraData>();
                if (camera.camera != _owner._camera) return;
                for (int index = 0; index < _owner._copies.Length; index++)
                {
                    TextureHandle source = resources.gBuffer[index];
                    if (!source.IsValid()) return;
                    var descriptor = camera.cameraTargetDescriptor;
                    descriptor.depthBufferBits = 0;
                    descriptor.msaaSamples = 1;
                    descriptor.graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm;
                    RenderingUtils.ReAllocateHandleIfNeeded(ref _owner._copies[index], descriptor, FilterMode.Point,
                        TextureWrapMode.Clamp, name: "Looga G-buffer diagnostic " + index);
                    TextureHandle destination = graph.ImportTexture(_owner._copies[index]);
                    graph.AddBlitPass(source, destination, Vector2.one, Vector2.zero, passName: "Looga G-buffer capture " + index);
                }
                _owner._recordedFrame = Time.frameCount;
                _owner._requested = false;
            }
        }
    }
}