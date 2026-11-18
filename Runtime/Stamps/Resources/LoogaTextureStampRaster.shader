// Looga Stamps texture passes for MicroVerse parity mode. A texture pass goes from one pair of tile textures into
// another: the layer map holds the four strongest layers (layer index / 32), the weight map their weights. Both are 8
// bits for each channel, so every pass rounds like MicroVerse. The stamp UV comes from the vertices, as in MicroVerse.
Shader "Hidden/Looga/TextureStampRaster"
{
    SubShader
    {
        ZTest Always
        ZWrite Off
        Cull Off

        // Pass 0: texture stamp.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #define LOOGA_STAMP_RASTER
            #include "UnityCG.cginc"
            #include "../Shaders/StampFalloff.hlsl"
            #include "../Shaders/TextureStampCore.hlsl"
            #include "../Shaders/WaterFilter.hlsl"
            #include "../Shaders/TerrainFieldFilter.hlsl"

            // Alphamap tile UV (0 to 1 over the terrain size) to stamp UV.
            float4x4 _TileToStamp;
            Texture2D<float4> _LayerMap;
            Texture2D<float4> _WeightMap;
            float2 _AlphamapSize;
            // Stamp world size x and z.
            float2 _StampScale;
            // Noise UV = tile UV + xy, in terrain sizes. z scales the tile UV (1).
            float3 _NoiseUv;
            float _Layer;
            float _Weight;
            int _ClampFalloffTexture;
            SamplerState looga_linear_repeat_sampler;

            // Weight noise: weight times 1 + noise.
            int _WeightNoiseType;
            float4 _WeightNoiseParams;
            // Height filter. Range and smoothness are in heightmap texture units (metres / real height).
            int _UseHeightFilter;
            float2 _HeightFilterRange;
            float2 _HeightFilterSmoothness;
            float _HeightFilterWeight;
            int _HeightFilterNoiseType;
            float4 _HeightFilterNoiseParams;
            float _RealHeight;
            Texture2D<float> _Heightmap;
            // Slope filter. Range and smoothness are in radians.
            int _UseSlopeFilter;
            float2 _SlopeFilterRange;
            float2 _SlopeFilterSmoothness;
            float _SlopeFilterWeight;
            int _SlopeFilterNoiseType;
            float4 _SlopeFilterNoiseParams;
            Texture2D<float4> _Normalmap;

            struct VertexInput
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FragmentInput
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 stampUv : TEXCOORD1;
            };

            FragmentInput vert(VertexInput input)
            {
                FragmentInput output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.stampUv = mul(_TileToStamp, float4(input.uv, 0, 1)).xy;
                return output;
            }

            // MicroVerse box edge: inner 1 is a hard edge that excludes the border itself.
            float SplatBox(float2 stampUv, float inner)
            {
                if (inner == 1)
                {
                    return stampUv.x <= 0 || stampUv.y <= 0 || stampUv.x >= 1 || stampUv.y >= 1 ? 0 : 1;
                }
                return BoxFalloff(stampUv, inner);
            }

            // Painted areas on splat passes read world = noise UV * terrain size.
            float SplatPaintedWeight(float2 noiseUv)
            {
                float2 world = noiseUv * _TerrainSizeXZ;
                float3 local = mul(_PaintedAreaWorldToLocal, float4(world.x, 0, world.y, 1)).xyz;
                return _PaintedArea.Sample(looga_linear_clamp_sampler, float2(local.x + 0.5, local.z + 0.5));
            }

            float SplatShapeFalloff(float2 tileUv, float2 stampUv, float2 noiseUv, float noise)
            {
                float falloff = 1;
                if (_FalloffShape == LOOGA_FALLOFF_BOX)
                {
                    falloff = SplatBox(stampUv, saturate(_FalloffRange.y - noise));
                }
                else if (_FalloffShape == LOOGA_FALLOFF_RANGE)
                {
                    falloff = RangeFalloff(stampUv, noise);
                }
                else if (_FalloffShape == LOOGA_FALLOFF_TEXTURE)
                {
                    float4 value;
                    if (_ClampFalloffTexture)
                    {
                        value = _FalloffTexture.Sample(looga_linear_clamp_sampler, stampUv);
                    }
                    else
                    {
                        value = _FalloffTexture.Sample(looga_linear_repeat_sampler, FalloffTextureUv(stampUv));
                    }
                    float textureSample = dot(value, _FalloffTextureChannel);
                    falloff *= textureSample;
                    falloff *= _FalloffTextureParams.x;
                    falloff += _FalloffTextureParams.y * textureSample;
                    falloff *= SplatBox(stampUv, saturate(_FalloffRange.y - noise));
                }
                else if (LOOGA_IS_AREA_FALLOFF(_FalloffShape))
                {
                    falloff *= AreaWeight(_AreaDistance.Sample(looga_linear_clamp_sampler, tileUv).r,
                        _AreaRange - noise);
                }
                if (_UsePaintedArea)
                {
                    falloff *= SplatPaintedWeight(noiseUv);
                }
                return EaseFalloff(falloff);
            }

            // Filters, then falloff. Falloff noise is in UV units here, not divided by the terrain height as in the
            // height passes. Global ignores easing and noise.
            float SplatWeight(float2 tileUv, float2 stampUv, float2 noiseUv)
            {
                float result = 1;
                if (_WeightNoiseType == LOOGA_NOISE_FBM)
                {
                    result = 1 + NoiseValue(noiseUv, _WeightNoiseParams);
                }
                if (_UseHeightFilter)
                {
                    float height = _Heightmap.Sample(looga_linear_clamp_sampler, tileUv);
                    if (_HeightFilterNoiseType == LOOGA_NOISE_FBM)
                    {
                        height += NoiseValue(noiseUv, _HeightFilterNoiseParams) / _RealHeight;
                    }
                    result *= lerp(1, RangeWeight(_HeightFilterRange, _HeightFilterSmoothness, height),
                        _HeightFilterWeight);
                }
                if (_UseSlopeFilter)
                {
                    // MicroVerse decodes only x and z, so y keeps the stored value. The slope therefore includes the
                    // height term of the normal map.
                    float3 normal = _Normalmap.Sample(looga_linear_clamp_sampler, tileUv).xyz;
                    normal.xz *= 2;
                    normal.xz -= 1;
                    float slope = (LOOGA_PI / 2) * acos(saturate(dot(normal, float3(0, 1, 0))));
                    if (_SlopeFilterNoiseType == LOOGA_NOISE_FBM)
                    {
                        slope += NoiseValue(noiseUv, _SlopeFilterNoiseParams);
                    }
                    result *= lerp(1, RangeWeight(_SlopeFilterRange, _SlopeFilterSmoothness, slope), _SlopeFilterWeight);
                }
                if (_UseWaterFilter)
                {
                    result *= WaterFilterWeight(tileUv, noiseUv, _Heightmap.Sample(looga_linear_clamp_sampler, tileUv));
                }
                result *= FieldFilterWeight(tileUv, noiseUv);

                float falloff = 1;
                if (_FalloffShape == LOOGA_FALLOFF_GLOBAL)
                {
                    if (_UsePaintedArea)
                    {
                        falloff = SplatPaintedWeight(noiseUv);
                    }
                }
                else
                {
                    falloff = SplatShapeFalloff(tileUv, stampUv, noiseUv, 0);
                    if (_NoiseType == LOOGA_NOISE_FBM)
                    {
                        float2 noisePosition = _NoiseSpace == LOOGA_NOISE_SPACE_STAMP ? stampUv : noiseUv;
                        float noise = NoiseValue(noisePosition, _NoiseParams);
                        noise *= 1 - falloff;
                        falloff = SplatShapeFalloff(tileUv, stampUv, noiseUv, noise);
                    }
                    if (_FalloffShape == LOOGA_FALLOFF_BOX || _FalloffShape == LOOGA_FALLOFF_TEXTURE)
                    {
                        falloff *= SplatBox(stampUv, 1);
                    }
                }
                return result * _Weight * falloff;
            }

            LayerOutput frag(FragmentInput input)
            {
                int3 texel = int3(input.vertex.xy, 0);
                float4 weights = _WeightMap.Load(texel);
                float4 layers = round(_LayerMap.Load(texel) * LAYER_INDEX_SCALE);

                // MicroVerse moves the filter UV back by half an alphamap texel and the stamp UV by half a world unit
                // times the tile UV. The operation order is kept for equal float results.
                float2 uvOffset = 1.0 / _AlphamapSize * 0.5;
                float2 stampOffset = uvOffset * _AlphamapSize / _StampScale * input.uv;
                float2 noiseUv = input.uv;
                noiseUv *= _AlphamapSize;
                noiseUv += lerp(-0.5, 0.5, input.uv);
                noiseUv /= _AlphamapSize;
                noiseUv *= _NoiseUv.z;
                noiseUv += _NoiseUv.xy;
                float weight = saturate(SplatWeight(input.uv - uvOffset, input.stampUv + stampOffset, noiseUv));
                return InsertLayer(weight, _Layer, weights, layers);
            }
            ENDCG
        }

        // Pass 1: normal map for the slope filter, like the MicroVerse normal map. Five height samples, neighbor tiles
        // across the edges. The result is normalize(dx, dz, 1, height) in the order x, 1, z, so the stored y also
        // depends on the height.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            Texture2D _Heightmap;
            SamplerState sampler_Heightmap;
            float4 _Heightmap_TexelSize;
            Texture2D _HeightmapPX;
            SamplerState sampler_HeightmapPX;
            Texture2D _HeightmapPY;
            SamplerState sampler_HeightmapPY;
            Texture2D _HeightmapNX;
            SamplerState sampler_HeightmapNX;
            Texture2D _HeightmapNY;
            SamplerState sampler_HeightmapNY;
            int _HasPX;
            int _HasPY;
            int _HasNX;
            int _HasNY;

            struct VertexInput
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FragmentInput
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            FragmentInput vert(VertexInput input)
            {
                FragmentInput output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            // One return: early returns in branches give an uninitialized-variable warning.
            float SampleHeight(float2 uv)
            {
                float height;
                if (_HasPX && uv.x > 1)
                {
                    uv.x -= 1;
                    uv.x += _Heightmap_TexelSize.x;
                    height = _HeightmapPX.Sample(sampler_HeightmapPX, uv).r;
                }
                else if (_HasPY && uv.y > 1)
                {
                    uv.y -= 1;
                    uv.y += _Heightmap_TexelSize.y;
                    height = _HeightmapPY.Sample(sampler_HeightmapPY, uv).r;
                }
                else if (_HasNX && uv.x < 0)
                {
                    uv.x += 1;
                    uv.x -= _Heightmap_TexelSize.x;
                    height = _HeightmapNX.Sample(sampler_HeightmapNX, uv).r;
                }
                else if (_HasNY && uv.y < 0)
                {
                    uv.y += 1;
                    uv.y -= _Heightmap_TexelSize.y;
                    height = _HeightmapNY.Sample(sampler_HeightmapNY, uv).r;
                }
                else
                {
                    height = _Heightmap.Sample(sampler_Heightmap, uv).r;
                }
                return height;
            }

            float4 frag(FragmentInput input) : SV_Target
            {
                float2 uv = input.uv;
                float2 offset = _Heightmap_TexelSize.xy;
                float height = _Heightmap.Sample(sampler_Heightmap, uv).r;
                float x = SampleHeight(uv + float2(offset.x, 0.0));
                float y = SampleHeight(uv + float2(0.0, offset.y));
                float nx = SampleHeight(uv + float2(-offset.x, 0.0));
                float ny = SampleHeight(uv + float2(0.0, -offset.y));
                // The operation order of MicroVerse is kept for equal float results.
                float2 slope = height - float2(x, y);
                slope += float2(nx, ny) - height;
                slope = slope * 1.0 / offset.xy;
                slope *= 0.5;
                float3 normal = normalize(float4(slope.x, slope.y, 1.0, height)).xzy * 0.5 + 0.5;
                return float4(normal, 1);
            }
            ENDCG
        }

        // Pass 2: path texture. The weight comes from the distance outside the path width.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"
            #include "../Shaders/TextureStampCore.hlsl"

            Texture2D<float4> _LayerMap;
            Texture2D<float4> _WeightMap;
            // x = distance outside the path width in metres, y = height. It covers the tile.
            Texture2D<float4> _PathDistance;
            SamplerState sampler_PathDistance;
            float2 _PathFieldSize;
            float _Layer;
            float _PathTextureWidth;
            float _PathTextureSmoothness;
            float _PathTextureWeight;

            struct VertexInput
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FragmentInput
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            FragmentInput vert(VertexInput input)
            {
                FragmentInput output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            LayerOutput frag(FragmentInput input)
            {
                int3 texel = int3(input.vertex.xy, 0);
                float4 weights = _WeightMap.Load(texel);
                float4 layers = round(_LayerMap.Load(texel) * LAYER_INDEX_SCALE);
                // MicroVerse keeps the field lookup half a field texel inside the tile.
                float2 halfTexel = 1.0 / _PathFieldSize * 0.5;
                float2 fieldUv = lerp(halfTexel, 1 - halfTexel, input.uv);
                float distance = _PathDistance.Sample(sampler_PathDistance, fieldUv).x;
                float edge = _PathTextureWidth + _PathTextureSmoothness;
                float fade = saturate((distance - _PathTextureWidth) / max(0.001, edge - _PathTextureWidth));
                float weight = (1 - fade) * _PathTextureWeight;
                return InsertLayer(weight, _Layer, weights, layers);
            }
            ENDCG
        }
    }
}
