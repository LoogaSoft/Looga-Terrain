using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain
{
    /// <summary>Encodes color and signed normals into GPU-resident BC3 arrays.</summary>
    public sealed class VirtualTextureCompression : IDisposable
    {
        private readonly ComputeShader _shader;
        private readonly int _kernel;
        private readonly Texture2DArray[] _textures = new Texture2DArray[2];
        private readonly RenderTexture[] _staging = new RenderTexture[3];
        private RenderTexture _blocks;

        /// <summary>Checks the tested GPU copy path and required formats.</summary>
        public static bool IsSupported => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12
            && QualitySettings.activeColorSpace == ColorSpace.Linear
            && SystemInfo.supportsComputeShaders && SystemInfo.supports2DArrayTextures
            && (SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) != 0
            && SystemInfo.IsFormatSupported(GraphicsFormat.RGBA_DXT5_SRGB, GraphicsFormatUsage.Sample)
            && SystemInfo.IsFormatSupported(GraphicsFormat.RGBA_DXT5_UNorm, GraphicsFormatUsage.Sample)
            && SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_UInt, GraphicsFormatUsage.LoadStore);

        /// <summary>Gets resident BC3 surface storage in bytes.</summary>
        public long CacheBytes { get; }
        /// <summary>Gets temporary encoder surface storage in bytes.</summary>
        public long StagingBytes => 136L * 136 * 20 + 34L * 34 * 16;
        /// <summary>Gets a compressed sampled channel.</summary>
        public Texture2DArray Texture(int channel) => _textures[channel];
        /// <summary>Gets a page-sized render target for the producer.</summary>
        public RenderTexture Staging(int channel) => _staging[channel];

        /// <summary>Allocates compressed arrays and one reusable page workspace.</summary>
        public VirtualTextureCompression(ComputeShader shader, int pages)
        {
            if (!shader || !IsSupported)
            {
                throw new NotSupportedException("BC3 terrain caching requires the encoder, Linear color space, and D3D12 format support.");
            }
            _shader = shader;
            _kernel = shader.FindKernel("EncodeBC3");
            CacheBytes = 136L * 136 * pages * 2;
            try
            {
                for (int channel = 0; channel < 2; channel++)
                {
                    _textures[channel] = new Texture2DArray(136, 136, pages, TextureFormat.DXT5, false, channel != 0)
                    {
                        name = "Looga VT BC3 " + channel, hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
                    };
                    _textures[channel].Apply(false, true);
                    _staging[channel] = new RenderTexture(new RenderTextureDescriptor(136, 136)
                    {
                        graphicsFormat = channel == 0 ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R16G16B16A16_SFloat,
                        depthBufferBits = 0, msaaSamples = 1
                    }) { name = "Looga VT compression staging", hideFlags = HideFlags.HideAndDontSave };
                    if (!_staging[channel].Create())
                    {
                        throw new InvalidOperationException("Cannot allocate compression staging.");
                    }
                }
                _staging[2] = new RenderTexture(_staging[1].descriptor)
                {
                    name = "Looga VT HDR staging", hideFlags = HideFlags.HideAndDontSave
                };
                if (!_staging[2].Create())
                {
                    throw new InvalidOperationException("Cannot allocate HDR staging.");
                }
                _blocks = new RenderTexture(new RenderTextureDescriptor(34, 34)
                {
                    graphicsFormat = GraphicsFormat.R32G32B32A32_UInt, enableRandomWrite = true,
                    depthBufferBits = 0, msaaSamples = 1
                }) { name = "Looga VT BC3 blocks", hideFlags = HideFlags.HideAndDontSave };
                if (!_blocks.Create())
                {
                    throw new InvalidOperationException("Cannot allocate BC3 blocks.");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Queues encoding and block copies before the page becomes visible.</summary>
        public void Encode(CommandBuffer commands, int slot)
        {
            for (int channel = 0; channel < 2; channel++)
            {
                commands.SetComputeIntParam(_shader, "_NormalChannel", channel);
                commands.SetComputeTextureParam(_shader, _kernel, "_Source", _staging[channel]);
                commands.SetComputeTextureParam(_shader, _kernel, "_Blocks", _blocks);
                commands.DispatchCompute(_shader, _kernel, 5, 5, 1);
                // Element copies permit the block-sized integer surface to populate BC3 storage.
                commands.CopyTexture(_blocks, 0, 0, _textures[channel], slot, 0);
            }
        }

        /// <summary>Releases the cache and encoder workspace.</summary>
        public void Dispose()
        {
            foreach (Texture texture in _textures)
            {
                Release(texture);
            }
            foreach (Texture texture in _staging)
            {
                Release(texture);
            }
            Release(_blocks);
        }

        private static void Release(Texture texture)
        {
            if (!texture) return;
            if (texture is RenderTexture render)
            {
                render.Release();
            }
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(texture);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
