using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LoogaSoft.Terrain.Surface
{
    /// <summary>
    /// The packed control maps of one terrain: the 4 strongest splat layers of each texel in two RGBA8 maps. The index
    /// map holds the layer indices / 255 and the weight map their weights with a sum of 1. Each mip packs the same mip
    /// of the splat control maps. A terrain with 30 layers has 8 RGBA32 control maps, so the packed maps use a quarter
    /// of their memory. The Looga terrain surface and the terrain ground read the packed maps and filter them in the
    /// shader (LoogaTerrainControlPacked.hlsl).
    /// </summary>
    /// <remarks>
    /// In the editor the maps are render textures that the GPU packs from the control maps of the terrain data. A
    /// player build ships packed textures and terrain data with small control maps (LoogaPackedControlsPlayerBuild).
    /// </remarks>
    public sealed class LoogaPackedControls
    {
        /// <summary>The largest number of control maps that the pack shader reads (32 layers).</summary>
        public const int MaxControls = 8;

        private const string ComputeName = "LoogaControlPack";
        private static readonly int[] ControlIds = CreateControlIds();
        private static readonly int IndexId = Shader.PropertyToID("_PackIndex");
        private static readonly int WeightId = Shader.PropertyToID("_PackWeight");
        private static readonly int SizeId = Shader.PropertyToID("_PackSize");
        private static readonly int RectId = Shader.PropertyToID("_PackRect");
        private static readonly int[] Size = new int[4];
        private static readonly int[] DispatchRect = new int[4];
        private static ComputeShader s_compute;
        private static int s_kernel = -1;

        private RenderTexture _liveIndex;
        private RenderTexture _liveWeight;
        private Texture2D _shippedIndex;
        private Texture2D _shippedWeight;

        /// <summary>Gets the index map: the layer indices / 255 of the 4 strongest layers, sorted by weight.</summary>
        public Texture Index => _shippedIndex ? _shippedIndex : _liveIndex;
        /// <summary>Gets the weight map: the weights of the layers in the index map, with a sum of 1.</summary>
        public Texture Weight => _shippedWeight ? _shippedWeight : _liveWeight;
        /// <summary>True when both maps exist.</summary>
        public bool IsValid => Index && Weight;
        /// <summary>True when the maps come from a player build and not from the GPU.</summary>
        public bool IsShipped => _shippedIndex && _shippedWeight;
        public int MipCount => IsValid ? Index.mipmapCount : 0;

        /// <summary>
        /// Packs the control maps on the GPU into render textures of the same size and mip count. Returns true when the
        /// map objects changed, for example after a size change. The callers must then bind the maps again.
        /// </summary>
        public bool Pack(Texture2D[] controls)
        {
            return Pack(controls, new RectInt(0, 0, int.MaxValue / 2, int.MaxValue / 2));
        }

        /// <summary>
        /// Packs one region of the control maps, in mip 0 texels, and the texels of the lower mips that cover it. New
        /// or resized maps are packed completely. Returns true when the map objects changed.
        /// </summary>
        public bool Pack(Texture2D[] controls, RectInt region)
        {
            if (controls == null || controls.Length == 0 || controls.Length > MaxControls || !controls[0])
            {
                bool hadMaps = IsValid;
                Release();
                return hadMaps;
            }
            if (!LoadCompute())
            {
                bool hadMaps = IsValid;
                Release();
                return hadMaps;
            }

            Texture2D first = controls[0];
            int mips = first.mipmapCount;
            bool changed = IsShipped || !_liveIndex || !_liveWeight || _liveIndex.width != first.width
                || _liveIndex.height != first.height || _liveIndex.mipmapCount != mips;
            if (changed)
            {
                Release();
                _liveIndex = CreateTarget("Looga Packed Control Index", first.width, first.height, mips);
                _liveWeight = CreateTarget("Looga Packed Control Weight", first.width, first.height, mips);
                region = new RectInt(0, 0, first.width, first.height);
            }
            int minX = Mathf.Clamp(region.xMin, 0, first.width);
            int minY = Mathf.Clamp(region.yMin, 0, first.height);
            int maxX = Mathf.Clamp(region.xMax, minX, first.width);
            int maxY = Mathf.Clamp(region.yMax, minY, first.height);
            if (maxX <= minX || maxY <= minY) return changed;

            for (int control = 0; control < MaxControls; control++)
            {
                // Unused slots take the first map: the kernel does not read them, but each slot needs a texture.
                Texture2D map = control < controls.Length && controls[control] ? controls[control] : first;
                s_compute.SetTexture(s_kernel, ControlIds[control], map);
            }
            for (int mip = 0; mip < mips; mip++)
            {
                int width = Mathf.Max(1, first.width >> mip);
                int height = Mathf.Max(1, first.height >> mip);
                // The mip texels that hold any part of the region.
                int startX = minX >> mip;
                int startY = minY >> mip;
                int countX = Mathf.Min(width, ((maxX - 1) >> mip) + 1) - startX;
                int countY = Mathf.Min(height, ((maxY - 1) >> mip) + 1) - startY;
                s_compute.SetTexture(s_kernel, IndexId, _liveIndex, mip);
                s_compute.SetTexture(s_kernel, WeightId, _liveWeight, mip);
                Size[0] = width;
                Size[1] = height;
                Size[2] = controls.Length;
                Size[3] = mip;
                DispatchRect[0] = startX;
                DispatchRect[1] = startY;
                DispatchRect[2] = countX;
                DispatchRect[3] = countY;
                s_compute.SetInts(SizeId, Size);
                s_compute.SetInts(RectId, DispatchRect);
                s_compute.Dispatch(s_kernel, (countX + 7) / 8, (countY + 7) / 8, 1);
            }
            return changed;
        }

        /// <summary>
        /// Uses packed textures of a player build. Returns true when the map objects changed.
        /// </summary>
        public bool UseShipped(Texture2D index, Texture2D weight)
        {
            if (_shippedIndex == index && _shippedWeight == weight && !_liveIndex) return false;

            Release();
            _shippedIndex = index;
            _shippedWeight = weight;
            return true;
        }

        /// <summary>Releases the render textures. Shipped textures belong to the scene, so they stay.</summary>
        public void Release()
        {
            DestroyTarget(ref _liveIndex);
            DestroyTarget(ref _liveWeight);
            _shippedIndex = null;
            _shippedWeight = null;
        }

        private static RenderTexture CreateTarget(string name, int width, int height, int mips)
        {
            var descriptor = new RenderTextureDescriptor(width, height, GraphicsFormat.R8G8B8A8_UNorm, 0)
            {
                enableRandomWrite = true,
                useMipMap = mips > 1,
                autoGenerateMips = false,
                mipCount = mips,
                sRGB = false
            };
            // The shader loads texels, so the filter mode does not matter. Point keeps a stray sample from blending
            // indices.
            var target = new RenderTexture(descriptor)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
            target.Create();
            return target;
        }

        private static void DestroyTarget(ref RenderTexture target)
        {
            if (!target) return;

            target.Release();
            Object.DestroyImmediate(target);
            target = null;
        }

        private static bool LoadCompute()
        {
            if (s_compute && s_kernel >= 0) return true;
            if (!SystemInfo.supportsComputeShaders) return false;

            s_compute = Resources.Load<ComputeShader>(ComputeName);
            s_kernel = s_compute ? s_compute.FindKernel("Pack") : -1;
            return s_kernel >= 0;
        }

        private static int[] CreateControlIds()
        {
            var ids = new int[MaxControls];
            for (int index = 0; index < ids.Length; index++)
            {
                ids[index] = Shader.PropertyToID("_PackControl" + index);
            }
            return ids;
        }
    }
}
