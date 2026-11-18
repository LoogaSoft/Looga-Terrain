using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain
{
    /// <summary>Encodes color, signed normals and optional 0 to 1 material data into GPU-resident BC3 arrays.</summary>
    public sealed class VirtualTextureCompression : IDisposable
    {
        private readonly ComputeShader _shader;
        private readonly int _kernel;
        private readonly Texture2DArray[] _textures = new Texture2DArray[3];
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
        public long StagingBytes { get; }
        /// <summary>True when the third array is BC3 too. Otherwise the caller copies its staging page.</summary>
        public bool CompressesMaterial => _textures[2];
        /// <summary>Gets a compressed sampled channel.</summary>
        public Texture2DArray Texture(int channel) => _textures[channel];
        /// <summary>Gets the page-sized render target array for the producer: one slice per page of a batch.</summary>
        public RenderTexture Staging(int channel) => _staging[channel];
        /// <summary>Gets the pages that one <see cref="Encode(CommandBuffer, int[], int)"/> call can encode.</summary>
        public int Batch { get; }

        /// <summary>Allocates compressed arrays and a reusable workspace for a batch of pages.</summary>
        /// <param name="materialFormat">Format of the uncompressed third array. Its staging target has the same format.</param>
        /// <param name="compressMaterial">Stores the third array as linear BC3. The material values must be 0 to 1.</param>
        /// <param name="batch">Pages encoded together. Each page has its own staging slice, so the pages of a batch do
        /// not wait for each other on the GPU.</param>
        public VirtualTextureCompression(ComputeShader shader, int pages,
            GraphicsFormat materialFormat = GraphicsFormat.R16G16B16A16_SFloat, bool compressMaterial = false,
            int batch = 1)
        {
            if (!shader || !IsSupported)
            {
                throw new NotSupportedException("BC3 terrain caching requires the encoder, Linear color space, and D3D12 format support.");
            }
            _shader = shader;
            _kernel = shader.FindKernel("EncodeBC3");
            int channels = compressMaterial ? 3 : 2;
            Batch = Mathf.Max(1, batch);
            CacheBytes = 136L * 136 * pages * channels;
            StagingBytes = Batch * (136L * 136 * (4 + 8 + GraphicsFormatUtility.GetBlockSize(materialFormat))
                + 34L * 34 * 16 * channels);
            try
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    _textures[channel] = new Texture2DArray(136, 136, pages, TextureFormat.DXT5, false, channel != 0)
                    {
                        name = "Looga VT BC3 " + channel, hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
                    };
                    _textures[channel].Apply(false, true);
                    if (channel == 2) continue;
                    _staging[channel] = new RenderTexture(new RenderTextureDescriptor(136, 136)
                    {
                        graphicsFormat = channel == 0 ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R16G16B16A16_SFloat,
                        depthBufferBits = 0, msaaSamples = 1, dimension = TextureDimension.Tex2DArray, volumeDepth = Batch
                    }) { name = "Looga VT compression staging", hideFlags = HideFlags.HideAndDontSave };
                    if (!_staging[channel].Create())
                    {
                        throw new InvalidOperationException("Cannot allocate compression staging.");
                    }
                }
                // The page copy into the third array needs the same format.
                _staging[2] = new RenderTexture(new RenderTextureDescriptor(136, 136)
                {
                    graphicsFormat = materialFormat, depthBufferBits = 0, msaaSamples = 1,
                    dimension = TextureDimension.Tex2DArray, volumeDepth = Batch
                }) { name = "Looga VT material staging", hideFlags = HideFlags.HideAndDontSave };
                if (!_staging[2].Create())
                {
                    throw new InvalidOperationException("Cannot allocate material staging.");
                }
                _blocks = new RenderTexture(new RenderTextureDescriptor(34, 34)
                {
                    graphicsFormat = GraphicsFormat.R32G32B32A32_UInt, enableRandomWrite = true,
                    depthBufferBits = 0, msaaSamples = 1, dimension = TextureDimension.Tex2DArray,
                    volumeDepth = Batch * channels
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

        /// <summary>Queues encoding of staging slice 0 and its block copies before the page becomes visible.</summary>
        public void Encode(CommandBuffer commands, int slot)
        {
            _oneSlot[0] = slot;
            Encode(commands, _oneSlot, 1);
        }

        /// <summary>Queues encoding of staging slices 0 to count - 1 into the given cache slots.</summary>
        public void Encode(CommandBuffer commands, int[] slots, int count)
        {
            count = Mathf.Min(count, Batch);
            int channels = CompressesMaterial ? 3 : 2;
            // All encodes first, then all copies: each channel and page writes its own block slice, so no encode
            // waits for a copy.
            for (int channel = 0; channel < channels; channel++)
            {
                commands.SetComputeIntParam(_shader, "_ChannelKind", channel);
                commands.SetComputeIntParam(_shader, "_BlockSliceOffset", channel * Batch);
                commands.SetComputeTextureParam(_shader, _kernel, "_Source", _staging[channel]);
                commands.SetComputeTextureParam(_shader, _kernel, "_Blocks", _blocks);
                commands.DispatchCompute(_shader, _kernel, 5, 5, count);
            }
            for (int channel = 0; channel < channels; channel++)
            {
                for (int page = 0; page < count; page++)
                {
                    // Element copies permit the block-sized integer surface to populate BC3 storage.
                    commands.CopyTexture(_blocks, channel * Batch + page, 0, _textures[channel], slots[page], 0);
                }
            }
        }

        private readonly int[] _oneSlot = new int[1];

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
