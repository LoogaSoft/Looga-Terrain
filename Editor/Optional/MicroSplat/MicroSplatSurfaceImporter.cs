using System.Collections.Generic;
using JBooth.MicroSplat;
using LoogaSoft.Terrain.Surface;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations.Editor
{
    /// <summary>
    /// Creates a Looga terrain surface from a MicroSplat terrain, so the new surface starts with the same layers,
    /// source maps and per-layer values. The importer reads only the features that the MicroSplat shader enables.
    /// It does not use the MicroSplat texture arrays: the Looga array builder makes the arrays from the source maps.
    /// </summary>
    public static class MicroSplatSurfaceImporter
    {
        // MicroSplat per-texture property rows (MicroSplatPropData y index).
        private const int UVScaleRow = 0;
        private const int TintRow = 1;
        private const int MaterialRow = 2;
        private const int BrightnessContrastRow = 3;
        private const int SaturationRow = 9;
        private const int RotationRow = 16;

        // MicroSplat MSLuminance in linear color space. The MicroSplat saturation adjustment uses these weights, so the
        // imported layers keep their look. Rec. 709 weights give a brighter result at low saturation.
        private static readonly Vector3 MicroSplatLuminance = new(0.0396819152f, 0.458021790f, 0.00609653955f);

        [MenuItem("CONTEXT/MicroSplatTerrain/Create Looga Terrain Surface")]
        private static void CreateFromContext(MenuCommand command)
        {
            var source = (MicroSplatTerrain)command.context;
            string path = EditorUtility.SaveFilePanelInProject("Create Looga Terrain Surface", source.name + " Surface",
                "asset", "Choose where to save the Looga terrain surface.");
            if (string.IsNullOrEmpty(path)) return;
            LoogaTerrainSurface surface = Import(source, path);
            EditorGUIUtility.PingObject(surface);
        }

        /// <summary>Creates or replaces a Looga terrain surface asset from a MicroSplat terrain.</summary>
        /// <param name="source">MicroSplat terrain with a template material, property data and keywords.</param>
        /// <param name="path">Project path of the surface asset.</param>
        /// <returns>The saved surface asset.</returns>
        public static LoogaTerrainSurface Import(MicroSplatTerrain source, string path)
        {
            Material template = source.templateMaterial;
            MicroSplatKeywords keywords = source.keywordSO;
            MicroSplatPropData properties = source.propData;
            if (!template || !keywords || !properties)
            {
                throw new System.InvalidOperationException(
                    "The MicroSplat terrain needs a template material, a keyword asset and property data.");
            }
            var albedo = template.GetTexture("_Diffuse") as Texture2DArray;
            TextureArrayConfig config = albedo ? TextureArrayConfig.FindConfig(albedo) : null;
            if (!config)
            {
                throw new System.InvalidOperationException("The MicroSplat material has no texture array config.");
            }

            var surface = AssetDatabase.LoadAssetAtPath<LoogaTerrainSurface>(path);
            bool created = !surface;
            if (created)
            {
                surface = ScriptableObject.CreateInstance<LoogaTerrainSurface>();
                // A new surface starts with the layout that matches the MicroSplat packing. An import into an existing
                // surface keeps its layout.
                surface.SetPacking(keywords.IsKeywordEnabled("_PACKINGHQ")
                    ? LoogaTerrainSurface.PackingLayout.Quality : LoogaTerrainSurface.PackingLayout.Compact);
            }
            surface.SetNativeShader(Shader.Find(LoogaTerrainSurface.NativeShaderName));
            surface.SetLayers(ReadLayers(config, properties, keywords));
            ReadMapping(source, template, keywords, surface);
            ReadBlending(template, keywords, surface);
            // MicroSplat makes missing heights and occlusion from the normal map. Keep that, so the built arrays match.
            var settings = TextureArrayConfigEditor.GetSettingsGroup(config, EditorUserBuildSettings.activeBuildTarget);
            surface.SetArrayBuild((int)settings.diffuseSettings.textureSize, surface.ArrayAnisoLevel, true);
            surface.SetStochastic(keywords.IsKeywordEnabled("_STOCHASTIC"), ReadFloat(template, "_StochasticScale", 1f),
                ReadFloat(template, "_StochasticContrast", 0.2f));
            surface.SetSaturationLuminance(keywords.IsKeywordEnabled("_PERTEXSATURATION")
                ? MicroSplatLuminance : new Vector3(0.2126f, 0.7152f, 0.0722f));
            surface.SetAntiTiling(ReadAntiTiling(source, template, keywords));
            surface.SetTriplanar(ReadTriplanar(template, keywords));

            if (created)
            {
                AssetDatabase.CreateAsset(surface, path);
            }
            EditorUtility.SetDirty(surface);
            AssetDatabase.SaveAssetIfDirty(surface);
            return surface;
        }

        private static List<LoogaTerrainSurface.Layer> ReadLayers(TextureArrayConfig config, MicroSplatPropData properties,
            MicroSplatKeywords keywords)
        {
            bool uvScale = keywords.IsKeywordEnabled("_PERTEXUVSCALEOFFSET");
            bool rotation = keywords.IsKeywordEnabled("_PERTEXUVROTATION");
            bool tint = keywords.IsKeywordEnabled("_PERTEXTINT");
            bool interpolation = keywords.IsKeywordEnabled("_PERTEXINTERPCONTRAST");
            bool brightness = keywords.IsKeywordEnabled("_PERTEXBRIGHTNESS");
            bool contrast = keywords.IsKeywordEnabled("_PERTEXCONTRAST");
            bool saturation = keywords.IsKeywordEnabled("_PERTEXSATURATION");
            bool normalStrength = keywords.IsKeywordEnabled("_PERTEXNORMSTR");
            bool smoothness = keywords.IsKeywordEnabled("_PERTEXSMOOTHSTR");
            bool occlusion = keywords.IsKeywordEnabled("_PERTEXAOSTR");
            bool metallic = keywords.IsKeywordEnabled("_PERTEXMETALLIC");
            bool perLayerStochastic = keywords.IsKeywordEnabled("_PERTEXSTOCHASTIC");

            var layers = new List<LoogaTerrainSurface.Layer>();
            for (int index = 0; index < config.sourceTextures.Count; index++)
            {
                var layer = new LoogaTerrainSurface.Layer(config.sourceTextures[index].terrainLayer);
                ReadSources(layer, config.sourceTextures[index], config);
                Color uv = properties.GetValue(index, UVScaleRow);
                Color tintValue = properties.GetValue(index, TintRow);
                Color material = properties.GetValue(index, MaterialRow);
                Color adjust = properties.GetValue(index, BrightnessContrastRow);
                Color saturationValue = properties.GetValue(index, SaturationRow);
                if (uvScale)
                {
                    layer.UVScale = new Vector2(uv.r, uv.g);
                    layer.UVOffset = new Vector2(uv.b, uv.a);
                }
                if (rotation)
                {
                    layer.UVRotation = properties.GetValue(index, RotationRow).r * Mathf.Rad2Deg;
                }
                if (tint)
                {
                    layer.Tint = new Color(tintValue.r, tintValue.g, tintValue.b, 1f);
                }
                if (interpolation)
                {
                    layer.InterpolationContrast = tintValue.a;
                }
                if (brightness)
                {
                    layer.Brightness = adjust.r;
                }
                if (contrast)
                {
                    layer.Contrast = adjust.g;
                }
                if (saturation)
                {
                    layer.Saturation = saturationValue.a;
                }
                if (perLayerStochastic)
                {
                    // MicroSplat skips stochastic sampling when this value is 0.5 or less.
                    layer.Stochastic = saturationValue.b > 0.5f;
                }
                if (normalStrength)
                {
                    layer.NormalStrength = material.r;
                }
                if (smoothness)
                {
                    layer.Smoothness = material.g;
                }
                if (occlusion)
                {
                    layer.OcclusionStrength = material.b;
                }
                if (metallic)
                {
                    layer.Metallic = material.a;
                }
                layers.Add(layer);
            }
            return layers;
        }

        // The source maps of the config entry. The layer uses only these maps: an empty map is generated or gets a
        // default value, as in the MicroSplat build, even when the terrain layer has a map.
        private static void ReadSources(LoogaTerrainSurface.Layer layer, TextureArrayConfig.TextureEntry entry,
            TextureArrayConfig config)
        {
            layer.UseTerrainLayerMaps = false;
            LoogaTerrainSurface.SourceMaps maps = layer.Sources;
            maps.Albedo = entry.diffuse;
            maps.Normal = entry.normal;
            maps.Height = entry.height;
            maps.HeightChannel = Channel(config.allTextureChannelHeight, entry.heightChannel);
            maps.Smoothness = entry.smoothness;
            maps.SmoothnessChannel = Channel(config.allTextureChannelSmoothness, entry.smoothnessChannel);
            maps.SmoothnessIsRoughness = entry.isRoughness;
            maps.Occlusion = entry.ao;
            maps.OcclusionChannel = Channel(config.allTextureChannelAO, entry.aoChannel);
            maps.Metallic = entry.metal;
            maps.MetallicChannel = (LoogaTerrainSurface.TextureChannel)(int)entry.metalChannel;
        }

        // A config-wide channel replaces the channel of every entry unless it is Custom.
        private static LoogaTerrainSurface.TextureChannel Channel(TextureArrayConfig.AllTextureChannel all,
            TextureArrayConfig.TextureChannel entry)
        {
            return all == TextureArrayConfig.AllTextureChannel.Custom
                ? (LoogaTerrainSurface.TextureChannel)(int)entry
                : (LoogaTerrainSurface.TextureChannel)(int)all;
        }

        // MicroSplat triplanar maps the top projection with (-x, z) x 0.1 x the triplanar UV scale. The importer keeps
        // that mapping, including the mirror on x. Without triplanar, MicroSplat uses the 0 to 1 terrain UV.
        private static void ReadMapping(MicroSplatTerrain source, Material template, MicroSplatKeywords keywords,
            LoogaTerrainSurface surface)
        {
            if (keywords.IsKeywordEnabled("_TRIPLANAR"))
            {
                Vector4 triplanar = template.GetVector("_TriplanarUVScale");
                surface.SetMapping(new Vector2(-0.1f * triplanar.x, 0.1f * triplanar.y), new Vector2(triplanar.z, triplanar.w));
                return;
            }
            Vector4 scale = template.GetVector("_UVScale");
            if (keywords.IsKeywordEnabled("_WORLDUV"))
            {
                surface.SetMapping(new Vector2(scale.x, scale.y), new Vector2(scale.z, scale.w));
                return;
            }
            // Terrain UV mode: one UV unit per tile. The per-tile offsets are not kept.
            Vector3 size = source.GetComponent<UnityEngine.Terrain>().terrainData.size;
            surface.SetMapping(new Vector2(scale.x / size.x, scale.y / size.z), new Vector2(scale.z, scale.w));
        }

        // MicroSplat samples the noises with the 0 to 1 terrain UV (world XZ with _WORLDUV). The Looga surface uses world
        // meters, so the importer converts the MicroSplat UV scales with the tile size. Per-texture noise strengths are
        // not read: the layers keep a multiplier of 1.
        private static LoogaTerrainSurface.AntiTilingSettings ReadAntiTiling(MicroSplatTerrain source, Material template,
            MicroSplatKeywords keywords)
        {
            TerrainData data = source.GetComponent<UnityEngine.Terrain>().terrainData;
            float tileSize = data.size.x;
            float metersPerUV = keywords.IsKeywordEnabled("_WORLDUV") ? 1f : tileSize;
            float uvScale = template.GetVector("_UVScale").x;
            var settings = new LoogaTerrainSurface.AntiTilingSettings();

            if (keywords.IsKeywordEnabled("_DETAILNOISE"))
            {
                Vector4 values = template.GetVector("_DetailNoiseScaleStrengthFade");
                LoogaTerrainSurface.DetailNoiseSettings detail = settings.DetailNoise;
                detail.Texture = template.GetTexture("_DetailNoise") as Texture2D;
                detail.Size = metersPerUV / Mathf.Max(uvScale * values.x, 1e-6f);
                detail.Strength = values.y;
                detail.FadeDistance = values.z;
                detail.Enabled = true;
            }
            if (keywords.IsKeywordEnabled("_DISTANCENOISE"))
            {
                Vector4 values = template.GetVector("_DistanceNoiseScaleStrengthFade");
                LoogaTerrainSurface.DistanceNoiseSettings distance = settings.DistanceNoise;
                distance.Texture = template.GetTexture("_DistanceNoise") as Texture2D;
                // MicroSplat applies the distance noise scale twice: to the UV and to the sample scale.
                distance.Size = metersPerUV / Mathf.Max(uvScale * values.x * values.x, 1e-6f);
                distance.Strength = values.y;
                distance.FadeStart = values.z;
                distance.FadeRange = values.w;
                distance.Enabled = true;
            }
            ReadNormalNoise(settings.NormalNoise1, template, keywords, "_NORMALNOISE", "", metersPerUV);
            ReadNormalNoise(settings.NormalNoise2, template, keywords, "_NORMALNOISE2", "2", metersPerUV);
            ReadNormalNoise(settings.NormalNoise3, template, keywords, "_NORMALNOISE3", "3", metersPerUV);
            if (keywords.IsKeywordEnabled("_NOISEHEIGHT"))
            {
                // MicroSplat FBM noise height: one noise cell per 1 / frequency UV units. The texture variant is read as
                // the same procedural noise at the same frequency.
                Vector4 values = template.GetVector("_NoiseHeightData");
                LoogaTerrainSurface.NoiseHeightSettings height = settings.NoiseHeight;
                height.Size = metersPerUV / Mathf.Max(values.x, 1e-6f);
                height.Amplitude = values.y;
                height.Enabled = true;
            }
            if (keywords.IsKeywordEnabled("_CONTROLNOISEUV"))
            {
                // MicroSplat offsets the control UV by (noise - 0.5) x strength texels. The noise texture repeats every
                // 5 / scale texels.
                Vector4 values = template.GetVector("_NoiseUVParams");
                float texel = tileSize / Mathf.Max(data.alphamapResolution, 1);
                LoogaTerrainSurface.ControlNoiseSettings control = settings.ControlNoise;
                control.Size = texel / Mathf.Max(0.2f * values.x, 1e-6f);
                control.Strength = 0.5f * values.y * texel;
                control.Enabled = true;
            }
            if (keywords.IsKeywordEnabled("_DISTANCERESAMPLE"))
            {
                Vector4 values = template.GetVector("_ResampleDistanceParams");
                LoogaTerrainSurface.DistanceResampleSettings resample = settings.DistanceResample;
                resample.UVScale = values.x;
                resample.FadeStart = values.y;
                resample.FadeEnd = values.z;
                bool normals = keywords.IsKeywordEnabled("_DISTANCERESAMPLENORMAL");
                resample.Maps = keywords.IsKeywordEnabled("_DISTANCERESAMPLENOALBEDO")
                    ? LoogaTerrainSurface.ResampleMaps.Normal
                    : normals ? LoogaTerrainSurface.ResampleMaps.Both : LoogaTerrainSurface.ResampleMaps.Albedo;
                if (keywords.IsKeywordEnabled("_DISTANCERESAMPLENOFADE"))
                {
                    resample.Fade = LoogaTerrainSurface.ResampleFade.Constant;
                }
                else if (keywords.IsKeywordEnabled("_DISTANCERESAMPLENOISE"))
                {
                    // MicroSplat noise fade: 1 + FBM x strength at every distance.
                    Vector4 noise = template.GetVector("_DistanceResampleNoiseParams");
                    resample.Fade = LoogaTerrainSurface.ResampleFade.Noise;
                    resample.FadeStart = 0f;
                    resample.FadeEnd = 0.01f;
                    resample.NoiseSize = metersPerUV / Mathf.Max(noise.x, 1e-6f);
                    resample.NoiseStrength = 0.5f * noise.y;
                }
                resample.AlbedoBlend = keywords.IsKeywordEnabled("_DISTANCERESAMPLEALBEDOBLENDOVERLAY")
                    ? LoogaTerrainSurface.ResampleAlbedoBlend.Overlay
                    : keywords.IsKeywordEnabled("_DISTANCERESAMPLEALBEDOBLENDLIGHTERCOLOR")
                        ? LoogaTerrainSurface.ResampleAlbedoBlend.LighterColor : LoogaTerrainSurface.ResampleAlbedoBlend.Linear;
                resample.AlbedoStrength = ReadFloat(template, "_DistanceResampleAlbedoStrength", 1f);
                resample.NormalStrength = ReadFloat(template, "_DistanceResampleNormalStrength", 1f);
                resample.Enabled = true;
            }
            return settings;
        }

        private static void ReadNormalNoise(LoogaTerrainSurface.NormalNoiseSettings noise, Material template,
            MicroSplatKeywords keywords, string keyword, string suffix, float metersPerUV)
        {
            if (!keywords.IsKeywordEnabled(keyword)) return;
            Vector4 values = template.GetVector("_NormalNoiseScaleStrength" + suffix);
            noise.Texture = template.GetTexture("_NormalNoise" + suffix) as Texture2D;
            noise.Size = metersPerUV / Mathf.Max(values.x, 1e-6f);
            noise.Strength = values.y;
            noise.Enabled = true;
        }

        // MicroSplat samples the side projections wherever their weight is above 0.001. The Looga surface keeps the
        // default slope gate: below about 12 degrees the MicroSplat side weights are under 1 % and do not show.
        private static LoogaTerrainSurface.TriplanarSettings ReadTriplanar(Material template, MicroSplatKeywords keywords)
        {
            var triplanar = new LoogaTerrainSurface.TriplanarSettings
            {
                Enabled = keywords.IsKeywordEnabled("_TRIPLANAR")
            };
            if (!triplanar.Enabled) return triplanar;
            triplanar.Contrast = ReadFloat(template, "_TriplanarContrast", 4f);
            if (keywords.IsKeywordEnabled("_TRIPLANARHEIGHTBLEND"))
            {
                triplanar.Mode = LoogaTerrainSurface.TriplanarMode.HeightBlended;
                triplanar.HeightBlendContrast = ReadFloat(template, "_Contrast", 0.2f);
            }
            return triplanar;
        }

        private static float ReadFloat(Material material, string name, float fallback)
        {
            return material.HasProperty(name) ? material.GetFloat(name) : fallback;
        }

        private static void ReadBlending(Material template, MicroSplatKeywords keywords, LoogaTerrainSurface surface)
        {
            int layersPerPixel = keywords.IsKeywordEnabled("_MAX2LAYER") ? 2 : keywords.IsKeywordEnabled("_MAX3LAYER") ? 3 : 4;
            float contrast = template.GetFloat("_Contrast");
            LoogaTerrainSurface.BlendMode mode = LoogaTerrainSurface.BlendMode.Height;
            Vector2 hybrid = surface.HybridDistances;
            if (keywords.IsKeywordEnabled("_DISABLEHEIGHTBLENDING"))
            {
                mode = LoogaTerrainSurface.BlendMode.NormalizedLinear;
            }
            else if (keywords.IsKeywordEnabled("_HYBRIDHEIGHTBLEND"))
            {
                mode = LoogaTerrainSurface.BlendMode.Hybrid;
                hybrid = new Vector2(0f, Mathf.Max(1f, template.GetFloat("_HybridHeightBlendDistance")));
            }
            surface.SetBlending(mode, layersPerPixel, contrast, hybrid);
        }
    }
}
