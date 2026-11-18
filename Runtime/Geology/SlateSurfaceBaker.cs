using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>Settings of the slate surface texture.</summary>
    [Serializable]
    public struct SlateSurfaceSettings
    {
        public int Seed;
        [Min(256)] public int Resolution;
        [Min(0f), Tooltip("Strength of the normal map.")]
        public float NormalStrength;
        [Min(0f), Tooltip("Strength of the darkening in low parts of the height.")]
        public float CavityStrength;

        public static SlateSurfaceSettings Default => new() { Seed = 1, Resolution = 2048, NormalStrength = 0.85f, CavityStrength = 9f };
    }

    /// <summary>Tiling maps of a slate surface. The caller owns the textures.</summary>
    public struct SlateSurfaceMaps
    {
        // RGB = color, A = smoothness.
        public Texture2D Albedo;
        // RG = normal, B = height, A = cavity.
        public Texture2D Normal;
        // R = plate lip, G = grit.
        public Texture2D Detail;
    }

    /// <summary>Bakes the slate maps on the GPU. See LoogaSlateSurface.compute.</summary>
    public static class SlateSurfaceBaker
    {
        private const string ComputeName = "LoogaSlateSurface";
        private const int ThreadGroupSize = 8;

        public static SlateSurfaceMaps Bake(in SlateSurfaceSettings settings)
        {
            var compute = Resources.Load<ComputeShader>(ComputeName);
            if (compute == null) throw new InvalidOperationException($"Compute shader {ComputeName} is missing.");

            int size = settings.Resolution;
            RenderTexture height = NewTarget(size, RenderTextureFormat.ARGBFloat);
            RenderTexture tint = NewTarget(size, RenderTextureFormat.ARGBFloat);
            RenderTexture albedo = NewTarget(size, RenderTextureFormat.ARGB32);
            RenderTexture normal = NewTarget(size, RenderTextureFormat.ARGB32);
            RenderTexture detail = NewTarget(size, RenderTextureFormat.ARGB32);

            compute.SetInt("_Resolution", size);
            compute.SetInt("_Seed", settings.Seed);
            compute.SetFloat("_NormalStrength", settings.NormalStrength);
            compute.SetFloat("_CavityStrength", settings.CavityStrength);
            int groups = Mathf.CeilToInt(size / (float)ThreadGroupSize);

            int generate = compute.FindKernel("GenerateSlate");
            compute.SetTexture(generate, "_HeightOut", height);
            compute.SetTexture(generate, "_TintOut", tint);
            compute.Dispatch(generate, groups, groups, 1);

            int build = compute.FindKernel("BuildSlateMaps");
            compute.SetTexture(build, "_HeightIn", height);
            compute.SetTexture(build, "_TintIn", tint);
            compute.SetTexture(build, "_AlbedoOut", albedo);
            compute.SetTexture(build, "_NormalOut", normal);
            compute.SetTexture(build, "_DetailOut", detail);
            compute.Dispatch(build, groups, groups, 1);

            var maps = new SlateSurfaceMaps
            {
                // The palette is in display values, so color reads as sRGB and the rest as data.
                Albedo = ToTexture(albedo, GraphicsFormat.R8G8B8A8_SRGB, "Slate Albedo"),
                Normal = ToTexture(normal, GraphicsFormat.R8G8B8A8_UNorm, "Slate Normal"),
                Detail = ToTexture(detail, GraphicsFormat.R8G8B8A8_UNorm, "Slate Detail")
            };
            height.Release();
            tint.Release();
            albedo.Release();
            normal.Release();
            detail.Release();
            UnityEngine.Object.DestroyImmediate(height);
            UnityEngine.Object.DestroyImmediate(tint);
            UnityEngine.Object.DestroyImmediate(albedo);
            UnityEngine.Object.DestroyImmediate(normal);
            UnityEngine.Object.DestroyImmediate(detail);
            return maps;
        }

        private static RenderTexture NewTarget(int size, RenderTextureFormat format)
        {
            var target = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear) { enableRandomWrite = true };
            target.Create();
            return target;
        }

        // The bytes move unchanged. A read into an sRGB texture would convert the display values a second time.
        private static Texture2D ToTexture(RenderTexture source, GraphicsFormat format, string textureName)
        {
            var read = new Texture2D(source.width, source.height, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            read.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            RenderTexture.active = previous;

            var texture = new Texture2D(source.width, source.height, format, TextureCreationFlags.MipChain)
            {
                name = textureName, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8,
                hideFlags = HideFlags.DontSave
            };
            texture.SetPixelData(read.GetRawTextureData<byte>(), 0);
            texture.Apply(true, false);
            UnityEngine.Object.DestroyImmediate(read);
            return texture;
        }
    }
}
