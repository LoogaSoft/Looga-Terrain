using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// The weld line of a fall sheet as a texture over world XZ: R = signed distance to the line in metres (positive on
    /// the side where the sheet draws), G = 1 within the span of the line. The body that feeds the fall ends exactly on
    /// the line and calms toward it, so the body and the sheet meet with the same positions and the same look.
    /// </summary>
    /// <remarks>
    /// The distance field is linear across a straight part of the line, so a bilinear sample of it puts the line
    /// exactly where the sheet starts. Beyond the ends of the line the distance goes on to the end points, and G falls
    /// from 0.45 to 0 over the end fade, so the calm and the rushing water fade out beside the end of the fall. G above
    /// 0.5 is within the span (WeldEndSpan in LoogaWaterSurface.hlsl).
    /// </remarks>
    internal sealed class WaterWeldField
    {
        // Texel size of the field, in metres.
        private const float TexelSize = 0.5f;
        private const int MaximumSize = 2048;
        // Rows of texels for each job batch.
        private const int RowBatch = 8;
        // G at the ends of the line, and the distance past an end over which it falls to 0, in metres.
        private const float EndSpan = 0.45f;
        private const float EndFade = 8f;

        private Texture2D _texture;

        public Texture2D Texture => _texture;
        /// <summary>xy = world XZ origin, zw = 1 / size, as the shader reads it. Zero before a build.</summary>
        public Vector4 UVRect { get; private set; }

        /// <summary>
        /// Builds the field for a line of world XZ points with a normal for each point toward the side where the
        /// sheet draws. The field covers the line and a margin around it, in metres.
        /// </summary>
        public void Build(IReadOnlyList<Vector2> line, IReadOnlyList<Vector2> normals, float margin)
        {
            if (line == null || line.Count < 2)
            {
                Release();
                return;
            }

            Vector2 min = line[0];
            Vector2 max = line[0];
            foreach (Vector2 point in line)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            min -= Vector2.one * margin;
            max += Vector2.one * margin;
            int width = Mathf.Clamp(Mathf.CeilToInt((max.x - min.x) / TexelSize), 2, MaximumSize);
            int height = Mathf.Clamp(Mathf.CeilToInt((max.y - min.y) / TexelSize), 2, MaximumSize);

            var points = new NativeArray<float2>(line.Count, Allocator.TempJob);
            var segmentNormals = new NativeArray<float2>(line.Count - 1, Allocator.TempJob);
            var pixels = new NativeArray<float2>(width * height, Allocator.TempJob);
            for (int index = 0; index < line.Count; index++)
            {
                points[index] = line[index];
            }
            // The normal of each segment, turned to the side of the point normals.
            for (int index = 1; index < line.Count; index++)
            {
                Vector2 along = (line[index] - line[index - 1]).normalized;
                var normal = new Vector2(-along.y, along.x);
                if (Vector2.Dot(normal, normals[index - 1] + normals[index]) < 0f) normal = -normal;
                segmentNormals[index - 1] = normal;
            }

            new DistanceJob
            {
                Points = points,
                Normals = segmentNormals,
                Pixels = pixels,
                Width = width,
                Origin = min,
                Texel = new float2((max.x - min.x) / width, (max.y - min.y) / height)
            }.Schedule(height, RowBatch).Complete();

            if (!_texture || _texture.width != width || _texture.height != height)
            {
                Release();
                _texture = new Texture2D(width, height, TextureFormat.RGFloat, false, true)
                {
                    name = "Looga Water Weld",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
            _texture.SetPixelData(pixels, 0);
            _texture.Apply(false, false);
            UVRect = new Vector4(min.x, min.y, 1f / (max.x - min.x), 1f / (max.y - min.y));
            points.Dispose();
            segmentNormals.Dispose();
            pixels.Dispose();
        }

        public void Release()
        {
            if (_texture)
            {
                if (Application.isPlaying)
                {
                    Object.Destroy(_texture);
                }
                else
                {
                    Object.DestroyImmediate(_texture);
                }
            }
            _texture = null;
            UVRect = Vector4.zero;
        }

        // One row of texels: the signed distance to the closest segment and the span flag at each texel centre, so a
        // bilinear sample gives the field at the exact point.
        [BurstCompile]
        private struct DistanceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Points;
            [ReadOnly] public NativeArray<float2> Normals;
            [NativeDisableParallelForRestriction] public NativeArray<float2> Pixels;
            public int Width;
            public float2 Origin;
            public float2 Texel;

            public void Execute(int row)
            {
                int last = Points.Length - 2;
                for (int column = 0; column < Width; column++)
                {
                    float2 point = Origin + new float2(column + 0.5f, row + 0.5f) * Texel;
                    float best = float.MaxValue;
                    float signedDistance = 0f;
                    float span = 0f;
                    for (int index = 0; index <= last; index++)
                    {
                        float2 a = Points[index];
                        float2 ab = Points[index + 1] - a;
                        float t = math.dot(point - a, ab) / math.max(math.lengthsq(ab), 1e-8f);
                        float2 offset = point - (a + ab * math.saturate(t));
                        float distance = math.lengthsq(offset);
                        if (distance >= best) continue;

                        best = distance;
                        float2 normal = Normals[index];
                        // Inside the span the distance is the distance along the segment normal, which is linear across
                        // the line. Past an end it is the distance to the end point, with the side of the end normal.
                        bool beyond = (index == 0 && t < 0f) || (index == last && t > 1f);
                        if (beyond)
                        {
                            signedDistance = math.dot(offset, normal) >= 0f ? math.sqrt(distance) : -math.sqrt(distance);
                            float past = (t < 0f ? -t : t - 1f) * math.length(ab);
                            span = EndSpan * math.saturate(1f - past / EndFade);
                        }
                        else
                        {
                            signedDistance = math.dot(offset, normal);
                            span = 1f;
                        }
                    }
                    Pixels[row * Width + column] = new float2(signedDistance, span);
                }
            }
        }
    }
}
