// Looga Stamps placement passes for MicroVerse parity mode. Pass 0 adds one clear stamp to the clear map of a tile.
// Pass 1 writes the object candidates of one object stamp into three half-float targets. The candidate layout, the
// random values and the output precision reproduce MicroVerse 1.7 object stamps (MEASUREMENTS.md 10.4). Pass 2 writes
// the detail weights of one detail stamp. Pass 3 adds one path clear to an occlusion mask. Pass 4 keeps the larger of
// two detail layers. Pass 5 lowers a detail layer where a displacing stamp paints. The statement order follows the
// MicroVerse arithmetic, so the float results are the same.
Shader "Hidden/Looga/PlacementRaster"
{
    SubShader
    {
        ZTest Always
        ZWrite Off
        Cull Off

        CGINCLUDE
        #define LOOGA_STAMP_RASTER
        #include "UnityCG.cginc"
        #include "../Shaders/StampFalloff.hlsl"
        #include "../Shaders/TextureStampCore.hlsl"
        #include "../Shaders/PlacementFilters.hlsl"

        // Tile UV to stamp UV.
        float4x4 _TileToStamp;
        // Noise UV = tile UV * z + xy. z = tile size / 1000 m, xy = tile position / tile size.
        float3 _NoiseUv;
        // Clear map: x = index of the last covering clear / 256, y = sum of the clear weights.
        sampler2D _MainTex;
        float4 _MainTex_TexelSize;
        float _ClearLayer;
        sampler2D _ClearMask;
        // Occlusion masks of path clears. Placement mask: z trees, w details. Object mask: x objects. Black: no
        // occlusion.
        sampler2D _PlacementMask;
        sampler2D _ObjectMask;
        ENDCG

        // Pass 0: clear stamp. The source is the previous clear map.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

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

            float2 frag(FragmentInput input) : SV_Target
            {
                float2 noiseUv = (input.uv * _NoiseUv.z) + _NoiseUv.xy;
                float weight = saturate(FilterWeight(input.uv, input.stampUv, noiseUv)) * TextureMask(input.uv);
                float2 previous = tex2D(_MainTex, input.uv).xy;
                if (weight > 0)
                {
                    previous.x = _ClearLayer / 256;
                    previous.y = previous.y + weight;
                }
                return previous;
            }
            ENDCG
        }

        // Pass 1: object candidates. Each target texel is one candidate cell. The source is the Poisson disk.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            // Values must agree with PlacementEvaluator.PrototypeData.
            #define LOOGA_PROTOTYPE_DENSITY_BY_WEIGHT 1
            #define LOOGA_PROTOTYPE_DISABLED 2

            struct PrototypeData
            {
                float weight;
                float2 weightRange;
                float2 rotationX;
                float2 rotationY;
                float2 rotationZ;
                float2 scaleX;
                float2 scaleY;
                float2 scaleZ;
                int scaleLock;
                int rotationLock;
                float slopeAlignment;
                float2 sink;
                float scaleAtBoundaries;
                float groundFit;
                // Base radius in prefab units. 0 turns the ground fit off.
                float groundRadius;
                int flags;
            };

            StructuredBuffer<PrototypeData> _Prototypes;
            int _PrototypeCount;
            float _TotalWeights;
            float _InstanceCount;
            float _YCount;
            float _Seed;
            float _DiskStrength;
            // 64 x 64 random values, point sampled with repeat.
            sampler2D _RandomTex;

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

            struct CandidateOutput
            {
                float4 positionWeight : SV_Target0;
                float4 rotation : SV_Target1;
                float4 scaleIndex : SV_Target2;
            };

            FragmentInput vert(VertexInput input)
            {
                FragmentInput output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            float4 NextRandom(float cellIndex)
            {
                float2 uv = cellIndex;
                uv /= 64;
                uv.y /= 64;
                return tex2Dlod(_RandomTex, float4(uv, 0, 0));
            }

            float4 EulerToQuaternion(float3 euler)
            {
                float3 c = cos(euler * 0.5);
                float3 s = sin(euler * 0.5);
                return float4(s.x * c.y * c.z - c.x * s.y * s.z,
                    c.x * s.y * c.z + s.x * c.y * s.z,
                    c.x * c.y * s.z - s.x * s.y * c.z,
                    c.x * c.y * c.z + s.x * s.y * s.z);
            }

            float4 MultiplyQuaternions(float4 q1, float4 q2)
            {
                return float4(q2.xyz * q1.w + q1.xyz * q2.w + cross(q1.xyz, q2.xyz), q1.w * q2.w - dot(q1.xyz, q2.xyz));
            }

            CandidateOutput frag(FragmentInput input)
            {
                // Candidate cell on a sqrt(count) grid, moved by the disk. The last row and column can leave the tile.
                float cellCount = sqrt(_InstanceCount);
                float cellIndex = floor(floor(input.uv.y * _YCount) * 512 + input.uv.x * 512);
                float x = floor(cellIndex % cellCount);
                float y = floor(cellIndex / cellCount);
                float2 uv = float2(x, y);

                float diskU = input.uv.x;
                diskU += input.uv.y;
                diskU *= _InstanceCount * 3.1927;
                diskU += _Seed;
                diskU %= _MainTex_TexelSize.z;
                diskU *= _MainTex_TexelSize.x;
                float2 disk = tex2D(_MainTex, float2(diskU, 0.5)).xy * _DiskStrength;
                uv += disk;
                uv /= floor(cellCount);

                float2 stampUv = mul(_TileToStamp, float4(uv, 0, 1)).xy;
                float2 noiseUv = (uv * _NoiseUv.z) + _NoiseUv.xy;
                float mask = 1.0 - tex2D(_PlacementMask, uv).b;
                float mask2 = 1.0 - tex2D(_ObjectMask, uv).r;
                float result = FilterWeight(uv, stampUv, noiseUv);
                float height0;
                float2 gradient = HeightGradient(uv, height0);
                float height = _Heightmap.Sample(looga_linear_clamp_sampler, uv);
                float textureMask = TextureMask(uv);

                float r = NextRandom(cellIndex * 3).x;
                float w = result * textureMask * mask * mask2;
                if (uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1 || w < r || cellIndex > _InstanceCount)
                {
                    w = -1;
                }
                // The clear factor applies after the spawn test. The sum removes the candidate only when it reaches 1.
                float2 clearMask = tex2D(_ClearMask, uv).xy;
                if (round(clearMask.r * 256) > _ClearLayer + 0.5)
                {
                    w *= 1.0 - clearMask.y;
                }
                float3 normal = normalize(float4(gradient.x, 1.0, gradient.y, height0)).xyz;

                CandidateOutput output = (CandidateOutput)0;
                if (w > 0)
                {
                    float4 randomValues = NextRandom(cellIndex * 5);
                    // Weighted pick. Disabled prototypes are skipped, but their weights count in the total.
                    float pick = randomValues.x * _TotalWeights;
                    float sum = 0;
                    int index = 0;
                    for (int i = 0; i < _PrototypeCount; ++i)
                    {
                        if (_Prototypes[i].flags & LOOGA_PROTOTYPE_DISABLED) continue;
                        sum += 1 + _Prototypes[i].weight;
                        index = i;
                        if (sum >= pick) break;
                    }
                    PrototypeData prototype = _Prototypes[index];
                    if (prototype.flags & LOOGA_PROTOTYPE_DISABLED)
                    {
                        w = -1;
                    }
                    else if (prototype.flags & LOOGA_PROTOTYPE_DENSITY_BY_WEIGHT)
                    {
                        w = w > 0.5 ? (w - 0.5) * 2 : -1;
                    }
                    if (prototype.weightRange.y > 0 && (w < prototype.weightRange.x || w > prototype.weightRange.y))
                    {
                        w = -1;
                    }
                    if (w > 0)
                    {
                        float scaleByWeight = lerp(prototype.scaleAtBoundaries, 1, saturate(w / 3));
                        float rotationX = lerp(prototype.rotationX.x, prototype.rotationX.y, randomValues.y);
                        float rotationY = lerp(prototype.rotationY.x, prototype.rotationY.y, randomValues.z);
                        float rotationZ = lerp(prototype.rotationZ.x, prototype.rotationZ.y, randomValues.w);

                        randomValues = NextRandom(cellIndex * 3 + 1927);
                        float scaleX = lerp(prototype.scaleX.x, prototype.scaleX.y, randomValues.y);
                        float scaleY = lerp(prototype.scaleY.x, prototype.scaleY.y, randomValues.z);
                        float scaleZ = lerp(prototype.scaleZ.x, prototype.scaleZ.y, randomValues.w);
                        height -= lerp(prototype.sink.x, prototype.sink.y, randomValues.x) / _RealHeight;

                        // Locks: 1 xy, 2 xz, 3 yz, 4 xyz.
                        if (prototype.rotationLock == 1)
                        {
                            rotationY = rotationX;
                        }
                        else if (prototype.rotationLock == 2)
                        {
                            rotationZ = rotationX;
                        }
                        else if (prototype.rotationLock == 3)
                        {
                            rotationZ = rotationY;
                        }
                        else if (prototype.rotationLock == 4)
                        {
                            rotationY = rotationX;
                            rotationZ = rotationX;
                        }
                        if (prototype.scaleLock == 1)
                        {
                            scaleY = scaleX;
                        }
                        else if (prototype.scaleLock == 2)
                        {
                            scaleZ = scaleX;
                        }
                        else if (prototype.scaleLock == 3)
                        {
                            scaleZ = scaleY;
                        }
                        else if (prototype.scaleLock == 4)
                        {
                            scaleY = scaleX;
                            scaleZ = scaleX;
                        }

                        // Ground fit: the object sinks by a share of the drop to the lowest ground on two rings
                        // under its base, so a wide base meets a slope. Samples past the tile edge clamp to the edge.
                        if (prototype.groundFit > 0 && prototype.groundRadius > 0)
                        {
                            float2 radius = prototype.groundRadius * max(scaleX, scaleZ) * scaleByWeight /
                                _TerrainSizeXZ;
                            float ground = _Heightmap.Sample(looga_linear_clamp_sampler, uv);
                            float lowest = ground;
                            [unroll]
                            for (int k = 0; k < 8; ++k)
                            {
                                float2 direction;
                                sincos(k * 0.7853982, direction.y, direction.x);
                                lowest = min(lowest, _Heightmap.Sample(looga_linear_clamp_sampler,
                                    uv + direction * radius));
                                lowest = min(lowest, _Heightmap.Sample(looga_linear_clamp_sampler,
                                    uv + direction * radius * 0.5));
                            }
                            height -= (ground - lowest) * prototype.groundFit;
                        }

                        float3 slopeAlign = lerp(float3(0, 0, 0), float3(normal.z * 90, 0, normal.x * -90),
                            prototype.slopeAlignment);
                        slopeAlign = radians(slopeAlign);
                        float4 slopeRotation = EulerToQuaternion(slopeAlign);
                        float3 rotation = float3(rotationX, rotationY, rotationZ);
                        rotation = radians(rotation);
                        output.rotation = MultiplyQuaternions(slopeRotation, EulerToQuaternion(rotation));
                        output.positionWeight.xyz = float3(uv.x, height * 2, uv.y);
                        output.positionWeight.w = w;
                        output.scaleIndex.xyz = float3(scaleX, scaleY, scaleZ) * scaleByWeight;
                        output.scaleIndex.w = index;
                    }
                }
                return output;
            }
            ENDCG
        }

        // Pass 2: detail stamp. Each target texel is one detail texel. The R8 target stores the coverage weight.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            // Detail weights outside this range give 0.
            float2 _WeightRange;

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

            // The half return type and the R8 target convert like the MicroVerse detail pass.
            half frag(FragmentInput input) : SV_Target
            {
                float2 noiseUv = (input.uv * _NoiseUv.z) + _NoiseUv.xy;
                float mask = 1.0 - tex2D(_PlacementMask, input.uv).w;
                float result = saturate(FilterWeight(input.uv, input.stampUv, noiseUv));
                float textureMask = TextureMask(input.uv);
                float w = result * mask * textureMask;
                if (w < _WeightRange.x || w > _WeightRange.y)
                {
                    w = 0;
                }
                // Unlike the object pass, the clear sum always lowers the detail weight.
                float2 clearMask = tex2D(_ClearMask, input.uv).xy;
                if (round(clearMask.r * 256) > _ClearLayer)
                {
                    w *= 1.0 - clearMask.g;
                }
                return saturate(w);
            }
            ENDCG
        }

        // Pass 3: path clear. The source is the previous occlusion mask. Each channel keeps the larger of its previous
        // value and 1 - smoothstep(width, width + smoothness, distance outside the path width).
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            // Path distance field: x = distance outside the path width, in metres.
            sampler2D _PathDistance;
            // Width and smoothness for each mask channel, in metres. A negative width keeps the channel.
            float4 _MaskWidth;
            float4 _MaskSmoothness;

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

            float4 frag(FragmentInput input) : SV_Target
            {
                float4 previous = tex2D(_MainTex, input.uv);
                float distance = tex2D(_PathDistance, input.uv).x;
                float4 clear = 1.0 - smoothstep(_MaskWidth, _MaskWidth + _MaskSmoothness, distance);
                clear = max(previous, clear);
                return float4(_MaskWidth.x >= 0 ? clear.x : previous.x, _MaskWidth.y >= 0 ? clear.y : previous.y,
                    _MaskWidth.z >= 0 ? clear.z : previous.z, _MaskWidth.w >= 0 ? clear.w : previous.w);
            }
            ENDCG
        }

        // Pass 4: the larger of two detail layers. The source is the first layer.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            sampler2D _Merge;

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

            half frag(FragmentInput input) : SV_Target
            {
                return max(tex2D(_MainTex, input.uv).r, tex2D(_Merge, input.uv).r);
            }
            ENDCG
        }

        // Pass 5: a displaced detail layer. The source is the layer, _Merge the layer of the displacing stamp.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            sampler2D _Merge;
            float _Displacement;

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

            half frag(FragmentInput input) : SV_Target
            {
                return tex2D(_MainTex, input.uv).r * (1 - _Displacement * tex2D(_Merge, input.uv).r);
            }
            ENDCG
        }
    }
}
