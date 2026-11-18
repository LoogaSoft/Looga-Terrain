// Looga Stamps spline distance field for one terrain tile. Each spline is a full-screen blit from the previous field
// into the next one. The texel position comes from the rasterizer UV, as in MicroVerse: tile UV maps to the tile local
// position UV * sample span.
// Output: x = signed distance outside the width (area) or distance outside the width (path), y = distance outside the
// width, z = world height of the nearest polyline vertex, w = spline width. 99999 means "not found".
// A path with an overlap blend uses x and w for the blend sums instead: x = sum of the spline weights, w = sum of the
// weighted spline heights. The sums are valid where y is found.
Shader "Hidden/Looga/SplineDistance"
{
    Properties
    {
        [HideInInspector] _MainTex ("Previous", 2D) = "black" {}
    }
    SubShader
    {
        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"
            #include "../Shaders/StampNoise.hlsl"

            #define NOT_FOUND 99999.0
            // Segments of one chunk. Must agree with ChunkSegments in SplineDistanceField.cs.
            #define CHUNK_SEGMENTS 32
            #define EASING_SMOOTHSTEP 1
            #define EASING_EASE_IN 2
            #define EASING_EASE_OUT 3
            #define EASING_EASE_IN_OUT 4

            struct Curve
            {
                float3 p0;
                float3 p1;
                float3 p2;
                float3 p3;
            };

            Texture2D _MainTex;
            SamplerState sampler_MainTex;
            // Edge pass: the field at the coarse resolution. Interior texels copy it, edge texels are computed again.
            Texture2D _Coarse;
            SamplerState sampler_Coarse;
            int _EdgePass;
            float2 _SampleSpan;
            float4x4 _TileToWorld;

            StructuredBuffer<Curve> _Curves;
            // Index of each curve in its spline, for the knot position. A path gets only the curves near the tile.
            StructuredBuffer<int> _CurveIndices;
            // For each curve and chunk of segments: x min, z min, x max, z max of its points, with a margin.
            StructuredBuffer<float4> _ChunkBoxes;
            int _ChunkCount;
            int _CurveCount;
            int _Segments;
            // Search distance: twice the maximum distance plus the largest width.
            float _SearchLimit;
            // Spline bounds: min x, max x, min z, max z.
            float4 _SplineBounds;
            int _AreaMode;

            // Width points: x = knot position, y = width.
            StructuredBuffer<float2> _Widths;
            int _WidthCount;
            int _WidthEasing;
            int _WidthNoiseType;
            float4 _WidthNoiseParams;

            // Overlap blend of a path: the weight falls by e for each blend distance from the spline centre line, and
            // fades out from the inner to the outer distance outside the width. 0 = the nearest spline sets the height.
            float _OverlapBlend;
            float _BlendInner;
            float _BlendOuter;
            // Square ends of a path: a texel past the first or the last spline point, seen along the end direction,
            // gets nothing from that spline (no round cap). _LastCurve is the index of the last curve of the spline.
            int _SquareEnds;
            int _LastCurve;

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

            bool InsideBox(float2 low, float2 high, float2 position, float margin)
            {
                return position.x >= low.x - margin && position.x <= high.x + margin &&
                    position.y >= low.y - margin && position.y <= high.y + margin;
            }

            float3 CurvePoint(Curve curve, float t)
            {
                float s = 1.0 - t;
                return curve.p0 * s * s * s + curve.p1 * 3.0 * s * s * t + curve.p2 * 3.0 * s * t * t +
                    curve.p3 * t * t * t;
            }

            float SegmentDistanceSquared(float2 position, float2 a, float2 b)
            {
                float2 toPosition = position - a;
                float2 segment = b - a;
                float along = saturate(dot(toPosition, segment) / dot(segment, segment));
                float2 offset = toPosition - segment * along;
                return dot(offset, offset);
            }

            // Parity of the polyline crossings of the horizontal line through the position, left of the position.
            int CrossingParity(Curve curve, float2 position, float2 low, float2 high)
            {
                // The control points contain the curve. Outside their box there is no crossing.
                if (position.y < low.y || position.y > high.y || low.x >= position.x) return 0;
                // Completely left: the crossing count is odd when the end points are on different sides of the line.
                if (high.x < position.x)
                {
                    return (curve.p0.z <= position.y) != (curve.p3.z <= position.y) ? 1 : 0;
                }
                int crossings = 0;
                float2 a = curve.p0.xz;
                for (int i = 1; i < _Segments; i++)
                {
                    float2 b = CurvePoint(curve, float(i) / float(_Segments - 1)).xz;
                    if ((a.y <= position.y) != (b.y <= position.y))
                    {
                        float x = a.x + (position.y - a.y) * (b.x - a.x) / (b.y - a.y);
                        if (x < position.x)
                        {
                            crossings++;
                        }
                    }
                    a = b;
                }
                return crossings & 1;
            }

            float EaseWidth(float width)
            {
                if (_WidthEasing == EASING_SMOOTHSTEP) return smoothstep(0, 1, width);
                if (_WidthEasing == EASING_EASE_IN) return width * width;
                if (_WidthEasing == EASING_EASE_OUT) return 1 - (1 - width) * (1 - width);
                if (_WidthEasing == EASING_EASE_IN_OUT)
                {
                    return width < 0.5 ? 2 * width * width : 1 - pow(-2 * width + 2, 2) / 2;
                }
                return width;
            }

            // Width at a knot position. Between two points the width blends with the easing, scaled by the larger
            // width.
            float WidthAt(float knot)
            {
                if (_WidthCount <= 0) return 0;
                float width = _Widths[0].y;
                if (_WidthCount == 1) return width;
                if (knot >= _Widths[_WidthCount - 1].x) return _Widths[_WidthCount - 1].y;
                for (int i = 1; i < _WidthCount; i++)
                {
                    float2 previous = _Widths[i - 1];
                    float2 next = _Widths[i];
                    if (knot >= previous.x && knot < next.x)
                    {
                        float fraction = frac((knot - next.x) / max(0.001, next.x - previous.x));
                        float peak = max(0.001, max(previous.y, next.y));
                        width = EaseWidth(lerp(previous.y, next.y, fraction) / peak) * peak;
                    }
                }
                return width;
            }

            float4 frag(FragmentInput input) : SV_Target
            {
                float4 last = _MainTex.Sample(sampler_MainTex, input.uv);
                // MicroVerse computes fine texels only near the low x, low z and high z tile edges (UV 0.01).
                if (_EdgePass && input.uv.x > 0.01 && input.uv.y > 0.01 && input.uv.y < 0.99)
                {
                    return _Coarse.Sample(sampler_Coarse, input.uv);
                }
                float2 local = input.uv * _SampleSpan;
                float2 position = mul(_TileToWorld, float4(local.x, 0, local.y, 1)).xz;
                if (!InsideBox(_SplineBounds.xz, _SplineBounds.yw, position, _SearchLimit)) return last;

                // MicroVerse starts the squared distance at 99999, so it finds nothing beyond 316 m. The search margin
                // shrinks to the best squared distance found so far.
                float bestSquared = NOT_FOUND;
                float height = NOT_FOUND;
                float knot = 0;
                float margin = _SearchLimit;
                int parity = 0;
                int bestCurve = -1;
                for (int c = 0; c < _CurveCount; c++)
                {
                    Curve curve = _Curves[c];
                    float2 low = min(min(curve.p0.xz, curve.p1.xz), min(curve.p2.xz, curve.p3.xz));
                    float2 high = max(max(curve.p0.xz, curve.p1.xz), max(curve.p2.xz, curve.p3.xz));
                    if (InsideBox(low, high, position, margin))
                    {
                        float curveSquared = 1e38;
                        float curveHeight = 0;
                        float curveT = 0;
                        int lastPoint = _Segments - 1;
                        int boxStart = c * _ChunkCount;
                        // Upper limit of the curve distance: the nearest chunk end point. A chunk whose box is
                        // farther away, or not nearer than the best segment so far, cannot hold a nearer segment, so
                        // it is skipped. The other chunks run in order, so the result is the full search result.
                        float limit = 1e38;
                        for (int j = 0; j < _ChunkCount; j++)
                        {
                            int endPoint = min(lastPoint, (j + 1) * CHUNK_SEGMENTS);
                            float2 toEnd = position - CurvePoint(curve, float(endPoint) / float(lastPoint)).xz;
                            limit = min(limit, dot(toEnd, toEnd));
                        }
                        for (int k = 0; k < _ChunkCount; k++)
                        {
                            float4 box = _ChunkBoxes[boxStart + k];
                            float2 outsideBox = max(max(box.xy - position, position - box.zw), 0);
                            float boxSquared = dot(outsideBox, outsideBox);
                            if (boxSquared > limit || boxSquared >= curveSquared) continue;
                            int first = k * CHUNK_SEGMENTS + 1;
                            int end = min(lastPoint, first + CHUNK_SEGMENTS - 1);
                            float3 a = first == 1 ? curve.p0 : CurvePoint(curve, float(first - 1) / float(lastPoint));
                            for (int i = first; i <= end; i++)
                            {
                                float t = float(i) / float(_Segments - 1);
                                float3 b = CurvePoint(curve, t);
                                float squared = SegmentDistanceSquared(position, a.xz, b.xz);
                                if (squared < curveSquared)
                                {
                                    curveSquared = squared;
                                    curveHeight = b.y;
                                    curveT = t;
                                }
                                a = b;
                            }
                        }
                        if (curveSquared < bestSquared)
                        {
                            bestSquared = curveSquared;
                            height = curveHeight;
                            knot = _CurveIndices[c] + curveT;
                            margin = min(margin, curveSquared);
                            bestCurve = c;
                        }
                    }
                    // Only an area uses the crossing parity.
                    if (_AreaMode)
                    {
                        parity ^= CrossingParity(curve, position, low, high);
                    }
                }

                if (_SquareEnds && !_AreaMode && bestCurve >= 0)
                {
                    Curve best = _Curves[bestCurve];
                    int index = _CurveIndices[bestCurve];
                    float step = 1.0 / float(_Segments - 1);
                    bool past = false;
                    if (index == 0)
                    {
                        float2 outward = normalize(best.p0.xz - CurvePoint(best, step).xz);
                        past = dot(position - best.p0.xz, outward) > 0;
                    }
                    if (index == _LastCurve)
                    {
                        float2 outward = normalize(best.p3.xz - CurvePoint(best, 1.0 - step).xz);
                        past = past || dot(position - best.p3.xz, outward) > 0;
                    }
                    if (past) bestSquared = NOT_FOUND;
                }

                float width = WidthAt(knot);
                if (_WidthNoiseType != 0)
                {
                    width += abs(NoiseValue(position * 0.01, _WidthNoiseParams));
                }
                float outside = max(0, sqrt(bestSquared) - width);
                float signedDistance = parity ? -outside : outside;
                if (_AreaMode)
                {
                    // Areas combine to the smallest signed distance.
                    if (last.x < outside)
                    {
                        last.xy = min(last.xy, float2(signedDistance, outside));
                        return last;
                    }
                    return float4(signedDistance, outside, height, width);
                }
                if (_OverlapBlend > 0)
                {
                    // The sums start at zero. The cleared field holds "not found" in every channel.
                    bool started = last.y < NOT_FOUND * 0.5;
                    float2 sums = started ? last.xw : float2(0, 0);
                    float centre = sqrt(bestSquared);
                    float weight = (1 - smoothstep(_BlendInner, _BlendOuter, outside)) *
                        exp(-min(centre / _OverlapBlend, 80.0));
                    sums += float2(weight, weight * height);
                    if (last.y < outside) return float4(sums.x, last.y, last.z, sums.y);
                    return float4(sums.x, outside, height, sums.y);
                }
                // Paths keep the spline with the smallest distance. A later spline wins a tie.
                if (last.x < outside) return last;
                return float4(outside, outside, height, width);
            }
            ENDCG
        }

        // Pass 1: copy a path field to the cache format (x = distance outside the width, y = height).
        // The blit reads texel centres, so the copy is exact. With an overlap blend the height is the weighted mean of
        // the spline heights, or the nearest spline height where no spline has weight.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            #define NOT_FOUND 99999.0

            Texture2D _MainTex;
            SamplerState sampler_MainTex;
            float _OverlapBlend;

            struct CopyInput
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct CopyFragment
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CopyFragment vert(CopyInput input)
            {
                CopyFragment output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            float4 frag(CopyFragment input) : SV_Target
            {
                float4 field = _MainTex.Sample(sampler_MainTex, input.uv);
                bool blended = _OverlapBlend > 0 && field.y < NOT_FOUND * 0.5 && field.x > 0;
                return float4(field.y, blended ? field.w / field.x : field.z, 0, 0);
            }
            ENDCG
        }

        // Pass 2: copy an area field to the cache format (x = signed distance).
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            Texture2D _MainTex;
            SamplerState sampler_MainTex;

            struct CopyInput
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct CopyFragment
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CopyFragment vert(CopyInput input)
            {
                CopyFragment output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            float4 frag(CopyFragment input) : SV_Target
            {
                return _MainTex.Sample(sampler_MainTex, input.uv).xxxx;
            }
            ENDCG
        }

        // Pass 3: band area for the cache format. x = 0 at the outer edge and outside it, -1 at the inner edge and
        // inside it. The main texture is the inner edge field and _BandOuter is the outer edge field.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            Texture2D _MainTex;
            SamplerState sampler_MainTex;
            Texture2D _BandOuter;

            struct CopyInput
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct CopyFragment
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CopyFragment vert(CopyInput input)
            {
                CopyFragment output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            float4 frag(CopyFragment input) : SV_Target
            {
                float inner = _MainTex.Sample(sampler_MainTex, input.uv).x;
                float outer = _BandOuter.Sample(sampler_MainTex, input.uv).x;
                if (inner <= 0) return -1;
                if (outer >= 0) return 0;

                return outer / (inner - outer);
            }
            ENDCG
        }
    }
}
