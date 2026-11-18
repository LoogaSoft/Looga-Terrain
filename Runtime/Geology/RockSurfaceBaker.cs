using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>One layer of rock plates in a surface texture. Periods are cell counts across the tile.</summary>
    [Serializable]
    public struct RockPlateLayer
    {
        [Min(1)] public int Period;
        [Min(1), Tooltip("Cells along V for each cell along U. 3 gives cells three times longer than high on a rock face.")]
        public int Squash;
        [Tooltip("Height of this layer. Later layers subtract smaller plates.")]
        public float Amount;
        [Tooltip("Slope of each plate, in height units for each cell.")]
        public float Tilt;
        [Tooltip("Largest plate height. Lower values give flat tops.")]
        public float Cap;
        [Tooltip("Random height of each plate.")]
        public float DepthJitter;
        [Tooltip("Width of the plate shoulder, in cells.")]
        public float Width;
        [Tooltip("Shape of the shoulder. Small values give round plates.")]
        public float Profile;
        [Tooltip("Wander of the plate borders, in tile widths.")]
        public float Warp;
    }

    /// <summary>One crack network in a surface texture.</summary>
    [Serializable]
    public struct RockCrackLayer
    {
        [Min(1)] public int Period;
        [Min(1), Tooltip("Cells along V for each cell along U. 3 gives cracks that run mostly along the layers.")]
        public int Squash;
        [Tooltip("Depth of the cracks in the height.")]
        public float Depth;
        [Tooltip("Width of the cracks, in cells.")]
        public float Width;
        [Range(0f, 1f), Tooltip("Share of the crack length that is open.")]
        public float Persistence;
        [Tooltip("Wander of the cracks, in tile widths.")]
        public float Warp;
    }

    /// <summary>Settings of a tiling rock surface. See <see cref="RockSurfaceBaker"/>.</summary>
    [Serializable]
    public sealed class RockSurfaceSettings
    {
        public int Seed = 3;
        [Tooltip("Texture width in pixels.")]
        public int Resolution = 2048;
        [Tooltip("Width of the tile on the rock, in meters.")]
        public float TileMeters = 5f;
        [Tooltip("Height range of the surface, in meters. This sets the strength of the normal map.")]
        public float ReliefMeters = 0.09f;
        public List<RockPlateLayer> Plates = new()
        {
            new RockPlateLayer
            {
                Period = 3, Squash = 3, Amount = 0.7f, Tilt = 0.7f, Cap = 0.75f, DepthJitter = 0.6f, Width = 0.03f, Profile = 0.3f,
                Warp = 0.08f
            },
            new RockPlateLayer
            {
                Period = 8, Squash = 3, Amount = 0.14f, Tilt = 0.7f, Cap = 0.75f, DepthJitter = 0.7f, Width = 0.04f, Profile = 0.3f,
                Warp = 0.04f
            },
            new RockPlateLayer
            {
                Period = 29, Squash = 2, Amount = 0.03f, Tilt = 0.7f, Cap = 0.75f, DepthJitter = 0.8f, Width = 0.06f, Profile = 0.35f,
                Warp = 0.015f
            }
        };
        [Tooltip("Reach of the slope blurs that wear the edges, in pixels.")]
        public float WearReach = 12f;
        public List<RockCrackLayer> Cracks = new()
        {
            new RockCrackLayer { Period = 3, Squash = 4, Depth = 0.2f, Width = 0.008f, Persistence = 0.35f, Warp = 0.05f },
            new RockCrackLayer { Period = 11, Squash = 3, Depth = 0.04f, Width = 0.015f, Persistence = 0.2f, Warp = 0.02f }
        };
        [Tooltip("Strength of the rough surface of the plates. The edge wear then breaks along this surface.")]
        public float Roughness = 0.03f;
        [Tooltip("Strength of the fine grain.")]
        public float Grain = 0.08f;
        public Color LowColor = new(0.25f, 0.265f, 0.27f);
        public Color MidColor = new(0.28f, 0.295f, 0.3f);
        public Color HighColor = new(0.31f, 0.325f, 0.33f);
        [Range(0f, 1f), Tooltip("Strength of the mineral grains in the base color.")]
        public float Speckle = 0.05f;
        [Min(1), Tooltip("Mineral grain cells across the tile. 280 on a 5 m tile gives grains of about 1.8 cm, with a second layer of half that size.")]
        public int SpecklePeriod = 280;
        [Min(0f), Tooltip("Brightness of the peaks of the mineral grains. 0 gives an even grain.")]
        public float Flecks = 0f;
        [Range(0f, 1f), Tooltip("Warm pale grains and cool dark grains. 0 keeps one color.")]
        public float GrainWarmth;
        [Range(0f, 1f), Tooltip("Share of the height in the base color. High values turn each bump into a pale spot.")]
        public float HeightColor = 0.03f;
        [Range(0f, 1f), Tooltip("Strength of the pale veins in the base color.")]
        public float VeinLight = 0.35f;
        public Color VeinColor = new(0.2f, 0.175f, 0.155f);
        [Tooltip("The vein network. Each vein has a pale body and a dark crack in its middle.")]
        public RockCrackLayer Veins = new() { Period = 2, Squash = 2, Width = 0.02f, Persistence = 0.55f, Warp = 0.06f };
        [Range(0f, 1f), Tooltip("Share of the base color that comes from large soft patches.")]
        public float ColorVariation = 0.06f;
        [Tooltip("Width and height of the stain map in pixels.")]
        public int StainResolution = 1024;
        [Tooltip("The thin lines that cross the faces. An empty list gives no line map.")]
        public List<RockLineSet> Lines = new();
        [Tooltip("Width and height of the line map in pixels.")]
        public int LineResolution = 2048;
        [Min(1), Tooltip("Reach of the soft halo around the lines, in pixels.")]
        public int LineHalo = 12;
        [Range(0f, 1f), Tooltip("Share of the mineral grain that gathers in patches. 0 = even grain. High values leave smooth " +
                                "sheared faces between the rough patches.")]
        public float GrainClump;
        [Min(1), Tooltip("Grain patches across the tile.")]
        public int ClumpPeriod = 5;
    }

    /// <summary>The textures of a baked rock surface. The caller owns them.</summary>
    public sealed class RockSurfaceMaps
    {
        /// <summary>Base color in sRGB.</summary>
        public Texture2D Albedo;
        /// <summary>Tangent normal in RGB.</summary>
        public Texture2D Normal;
        /// <summary>R mineral grains, G occlusion, B height, A smoothness: the layout of Looga/Rock Face.</summary>
        public Texture2D Mask;
        /// <summary>R rust, G olive, B dark streaks, A mottle. Looga/Rock Face projects it in world space.</summary>
        public Texture2D Stain;
        /// <summary>R pale veins, G dark cracks, B halo. Looga/Rock Face projects it in world space. Null with no lines.</summary>
        public Texture2D Lines;
        /// <summary>Tangent normals of the steps along the cracks of the line map. Null with no lines.</summary>
        public Texture2D LineSteps;
    }

    /// <summary>
    /// Bakes a tiling rock surface on the GPU. Detail smaller than the rock grid comes from this surface: the mesh holds the
    /// blocks and the texture holds the cracks.
    /// </summary>
    public static class RockSurfaceBaker
    {
        private const string ComputeName = "LoogaRockSurface";
        private const int ThreadGroupSize = 32;
        private const int SlopeSamples = 10;
        // Reach of the blurs for occlusion and curvature, as sigma in pixels at a width of 2048 pixels.
        private const float WideSigma = 12f;
        private const float NearSigma = 2.5f;

        private static readonly int FieldId = Shader.PropertyToID("_Field");
        private static readonly int SourceId = Shader.PropertyToID("_Source");
        private static readonly int SecondId = Shader.PropertyToID("_Second");
        private static readonly int WideBlurId = Shader.PropertyToID("_WideBlur");
        private static readonly int NearBlurId = Shader.PropertyToID("_NearBlur");
        private static readonly int VeinsId = Shader.PropertyToID("_Veins");

        /// <summary>Bakes the surface and reads the maps back into textures.</summary>
        public static RockSurfaceMaps Bake(RockSurfaceSettings settings)
        {
            var compute = Resources.Load<ComputeShader>(ComputeName);
            if (compute == null) throw new InvalidOperationException($"Compute shader {ComputeName} is missing.");
            // A compute shader with an error still loads, and its dispatches do nothing.
            if (!compute.IsSupported(0))
            {
                throw new InvalidOperationException($"Compute shader {ComputeName} does not compile on this device.");
            }

            int resolution = settings.Resolution;
            int pixelCount = resolution * resolution;
            int groups = Mathf.CeilToInt(resolution / (float)ThreadGroupSize);
            float pixelScale = resolution / 2048f;
            var buffers = new GraphicsBuffer[7];
            for (int index = 0; index < buffers.Length; index++)
            {
                buffers[index] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, pixelCount, sizeof(float));
            }
            GraphicsBuffer current = buffers[0];
            GraphicsBuffer next = buffers[1];
            GraphicsBuffer second = buffers[2];
            GraphicsBuffer spare = buffers[3];
            GraphicsBuffer wide = buffers[4];
            GraphicsBuffer near = buffers[5];
            GraphicsBuffer veins = buffers[6];

            void Run(string kernelName, GraphicsBuffer source, GraphicsBuffer target, GraphicsBuffer other)
            {
                int kernel = compute.FindKernel(kernelName);
                compute.SetBuffer(kernel, SourceId, source);
                compute.SetBuffer(kernel, FieldId, target);
                if (kernelName is "SlopeBlur" or "Multiply") compute.SetBuffer(kernel, SecondId, other);
                compute.Dispatch(kernel, groups, groups, 1);
            }

            void Step(string kernelName, GraphicsBuffer other = null)
            {
                Run(kernelName, current, next, other);
                (current, next) = (next, current);
            }

            void BlurInto(GraphicsBuffer source, GraphicsBuffer target, float sigma)
            {
                compute.SetFloat("_Sigma", sigma);
                compute.SetInt("_Radius", Mathf.CeilToInt(3f * sigma));
                compute.SetInts("_Axis", 1, 0);
                Run("Blur", source, spare, null);
                compute.SetInts("_Axis", 0, 1);
                Run("Blur", spare, target, null);
            }

            void WriteClouds(GraphicsBuffer target, int period, int octaves)
            {
                compute.SetInt("_Period", period);
                compute.SetInt("_Octaves", octaves);
                Run("Clouds", current, target, null);
            }

            void SlopeBlurStep(GraphicsBuffer slope, float reach, float lowest)
            {
                compute.SetInt("_Samples", SlopeSamples);
                compute.SetFloat("_Reach", reach * pixelScale);
                compute.SetFloat("_Lowest", lowest);
                Step("SlopeBlur", slope);
            }

            try
            {
                current.SetData(new float[pixelCount]);
                compute.SetInt("_Resolution", resolution);
                compute.SetInt("_Seed", settings.Seed);

                // Plates: tilted gradients with capped peaks and a depth for each plate.
                foreach (RockPlateLayer plate in settings.Plates)
                {
                    compute.SetInt("_Period", plate.Period);
                    compute.SetInt("_PeriodV", plate.Period * Mathf.Max(plate.Squash, 1));
                    compute.SetFloat("_Amount", plate.Amount);
                    compute.SetFloat("_Tilt", plate.Tilt);
                    compute.SetFloat("_Cap", plate.Cap);
                    compute.SetFloat("_DepthJitter", plate.DepthJitter);
                    compute.SetFloat("_Width", plate.Width);
                    compute.SetFloat("_Profile", plate.Profile);
                    compute.SetFloat("_Warp", plate.Warp);
                    Step("Plates");
                }

                compute.SetInt("_Period", 14);
                compute.SetInt("_Octaves", 6);
                compute.SetFloat("_Amount", settings.Roughness);
                Step("Grain");

                // Edge wear: a slope blur along clouds, then along a blurred copy of the height.
                WriteClouds(second, 7, 6);
                SlopeBlurStep(second, settings.WearReach, 0.7f);
                BlurInto(current, second, 6f * pixelScale);
                SlopeBlurStep(second, settings.WearReach * 0.5f, 0.4f);

                // Cracks: lines from the cell borders, a slope blur of the lines, then a multiply into the height.
                foreach (RockCrackLayer crack in settings.Cracks)
                {
                    compute.SetInt("_Period", crack.Period);
                    compute.SetInt("_PeriodV", crack.Period * Mathf.Max(crack.Squash, 1));
                    compute.SetFloat("_Width", crack.Width);
                    compute.SetFloat("_Persistence", crack.Persistence);
                    compute.SetFloat("_Warp", crack.Warp);
                    Run("Cracks", current, second, null);
                    WriteClouds(spare, 19, 5);
                    compute.SetInt("_Samples", SlopeSamples);
                    compute.SetFloat("_Reach", 5f * pixelScale);
                    compute.SetFloat("_Lowest", 0.6f);
                    int kernel = compute.FindKernel("SlopeBlur");
                    compute.SetBuffer(kernel, SecondId, spare);
                    compute.SetBuffer(kernel, SourceId, second);
                    compute.SetBuffer(kernel, FieldId, wide);
                    compute.Dispatch(kernel, groups, groups, 1);
                    compute.SetFloat("_Amount", crack.Depth);
                    Step("Multiply", wide);
                }

                // The grain in the height, at the size of the color grains. The blur rounds the grains, so they shade softly.
                compute.SetInt("_Period", settings.SpecklePeriod);
                compute.SetFloat("_Amount", settings.Grain);
                Step("MineralGrain");
                BlurInto(current, next, 0.8f * pixelScale);
                (current, next) = (next, current);

                // Pale veins. A vein with a width of some centimeters stays visible at the distance of a rock.
                compute.SetInt("_Period", settings.Veins.Period);
                compute.SetInt("_PeriodV", settings.Veins.Period * Mathf.Max(settings.Veins.Squash, 1));
                compute.SetFloat("_Width", settings.Veins.Width);
                compute.SetFloat("_Persistence", settings.Veins.Persistence);
                compute.SetFloat("_Warp", settings.Veins.Warp);
                Run("Cracks", current, veins, null);

                BlurInto(current, wide, WideSigma * pixelScale);
                BlurInto(current, near, NearSigma * pixelScale);

                var heights = new float[pixelCount];
                current.GetData(heights);
                float lowest = float.MaxValue;
                float highest = float.MinValue;
                foreach (float height in heights)
                {
                    lowest = Mathf.Min(lowest, height);
                    highest = Mathf.Max(highest, height);
                }

                var linear = new RenderTextureDescriptor(resolution, resolution, RenderTextureFormat.ARGB32, 0)
                    { enableRandomWrite = true, sRGB = false };
                var albedo = new RenderTexture(linear);
                var normal = new RenderTexture(linear);
                var mask = new RenderTexture(linear);
                albedo.Create();
                normal.Create();
                mask.Create();

                float pixelMeters = settings.TileMeters / resolution;
                int mapsKernel = compute.FindKernel("WriteMaps");
                compute.SetFloat("_HeightBias", lowest);
                compute.SetFloat("_HeightScale", 1f / Mathf.Max(highest - lowest, 1e-5f));
                compute.SetFloat("_Slope", settings.ReliefMeters / (2f * pixelMeters));
                compute.SetFloat("_OcclusionScale", 7f);
                compute.SetFloat("_CurvatureScale", 8f);
                compute.SetFloat("_ColorVariation", settings.ColorVariation);
                compute.SetVector("_LowColor", settings.LowColor);
                compute.SetVector("_MidColor", settings.MidColor);
                compute.SetVector("_HighColor", settings.HighColor);
                compute.SetFloat("_SmoothLow", 0.1f);
                compute.SetFloat("_SmoothHigh", 0.45f);
                compute.SetFloat("_Speckle", settings.Speckle);
                compute.SetInt("_SpecklePeriod", settings.SpecklePeriod);
                compute.SetFloat("_VeinLight", settings.VeinLight);
                compute.SetVector("_VeinColor", settings.VeinColor);
                compute.SetFloat("_Flecks", settings.Flecks);
                compute.SetFloat("_GrainWarmth", settings.GrainWarmth);
                compute.SetFloat("_HeightColor", settings.HeightColor);
                compute.SetFloat("_GrainClump", settings.GrainClump);
                compute.SetInt("_ClumpPeriod", settings.ClumpPeriod);
                compute.SetBuffer(mapsKernel, VeinsId, veins);
                compute.SetBuffer(mapsKernel, SourceId, current);
                compute.SetBuffer(mapsKernel, WideBlurId, wide);
                compute.SetBuffer(mapsKernel, NearBlurId, near);
                compute.SetTexture(mapsKernel, "_AlbedoMap", albedo);
                compute.SetTexture(mapsKernel, "_NormalMap", normal);
                compute.SetTexture(mapsKernel, "_MaskMap", mask);
                compute.Dispatch(mapsKernel, groups, groups, 1);

                int stainSize = settings.StainResolution;
                var stain = new RenderTexture(linear) { width = stainSize, height = stainSize };
                stain.Create();
                int stainKernel = compute.FindKernel("WriteStain");
                compute.SetInt("_Resolution", stainSize);
                compute.SetTexture(stainKernel, "_StainMap", stain);
                compute.Dispatch(stainKernel, Mathf.CeilToInt(stainSize / (float)ThreadGroupSize),
                    Mathf.CeilToInt(stainSize / (float)ThreadGroupSize), 1);

                var maps = new RockSurfaceMaps
                {
                    Albedo = ToTexture(albedo), Normal = ToTexture(normal), Mask = ToTexture(mask), Stain = ToTexture(stain)
                };
                if (settings.Lines.Count > 0)
                {
                    maps.Lines = RockLineMap.Draw(settings.Lines, settings.LineResolution, settings.LineHalo, settings.Seed,
                        out maps.LineSteps);
                }
                albedo.Release();
                normal.Release();
                mask.Release();
                stain.Release();
                return maps;
            }
            finally
            {
                foreach (GraphicsBuffer buffer in buffers)
                {
                    buffer.Dispose();
                }
            }
        }

        private static Texture2D ToTexture(RenderTexture source)
        {
            var texture = new Texture2D(source.width, source.height, GraphicsFormat.R8G8B8A8_UNorm,
                TextureCreationFlags.None);
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(source);
            request.WaitForCompletion();
            if (request.hasError) throw new InvalidOperationException("The rock surface readback failed.");
            texture.SetPixelData(request.GetData<byte>(), 0);
            texture.Apply(false);
            return texture;
        }
    }
}
