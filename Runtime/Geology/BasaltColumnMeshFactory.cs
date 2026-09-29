using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    public static class BasaltColumnMeshFactory
    {
        public static Mesh Create(int sideCount, int verticalSegments, int variant,
            float edgeWear, float surfaceRoughness, float crackDepth, int seed)
        {
            sideCount = Mathf.Clamp(sideCount, 5, 8);
            verticalSegments = Mathf.Clamp(verticalSegments, 2, 16);
            edgeWear = Mathf.Clamp01(edgeWear);
            surfaceRoughness = Mathf.Clamp(surfaceRoughness, 0f, 0.35f);
            crackDepth = Mathf.Clamp(crackDepth, 0f, 0.3f);

            List<Vector3> vertices = new();
            List<Vector3> normals = new();
            List<Vector2> uv = new();
            List<Vector2> uv2 = new();
            List<Color> colors = new();
            List<int> triangles = new();
            // A shared orientation is required for neighbouring regular hexagons to tessellate.
            // Shape variation belongs in the surface profile, not in the cell rotation.
            const float angleOffset = 0f;
            int pointsPerRing = sideCount * 2;
            Vector3[][] rings = new Vector3[verticalSegments + 1][];
            for (int ring = 0; ring <= verticalSegments; ring++)
            {
                rings[ring] = BuildRing(sideCount, ring, verticalSegments, variant, seed,
                    edgeWear, surfaceRoughness, crackDepth, angleOffset);
            }
            Vector3[][] ringNormals = BuildSmoothSideNormals(rings);

            for (int ring = 0; ring < verticalSegments; ring++)
            {
                float v0 = ring / (float)verticalSegments;
                float v1 = (ring + 1) / (float)verticalSegments;
                for (int point = 0; point < pointsPerRing; point++)
                {
                    int nextPoint = (point + 1) % pointsPerRing;
                    Vector3 p00 = rings[ring][point];
                    Vector3 p01 = rings[ring][nextPoint];
                    Vector3 p10 = rings[ring + 1][point];
                    Vector3 p11 = rings[ring + 1][nextPoint];
                    int index = vertices.Count;
                    vertices.Add(p00); vertices.Add(p10); vertices.Add(p11); vertices.Add(p01);
                    normals.Add(ringNormals[ring][point]);
                    normals.Add(ringNormals[ring + 1][point]);
                    normals.Add(ringNormals[ring + 1][nextPoint]);
                    normals.Add(ringNormals[ring][nextPoint]);
                    float u0 = point / (float)pointsPerRing;
                    float u1 = (point + 1) / (float)pointsPerRing;
                    uv.Add(new Vector2(u0, v0)); uv.Add(new Vector2(u0, v1));
                    uv.Add(new Vector2(u1, v1)); uv.Add(new Vector2(u1, v0));
                    bool chamferFace = (point & 1) == 0;
                    float chamferStart = chamferFace ? 0f : 2f;
                    float chamferEnd = chamferFace ? 1f : 3f;
                    // UV2.x identifies the exact chamfer face. UV2.y preserves normalized
                    // pillar height so the shader can place a separate highlight on the top rim.
                    uv2.Add(new Vector2(chamferStart, v0));
                    uv2.Add(new Vector2(chamferStart, v1));
                    uv2.Add(new Vector2(chamferEnd, v1));
                    uv2.Add(new Vector2(chamferEnd, v0));
                    float cornerWear = (point & 1) == 0 ? edgeWear : 0f;
                    Color color0 = new(Mathf.Max(EdgeMask(v0), cornerWear), 0f,
                        Hash01(seed, variant, point), 1f);
                    Color color1 = new(Mathf.Max(EdgeMask(v1), cornerWear), 0f,
                        Hash01(seed, variant, point + 17), 1f);
                    colors.Add(color0); colors.Add(color1); colors.Add(color1); colors.Add(color0);
                    triangles.Add(index); triangles.Add(index + 1); triangles.Add(index + 2);
                    triangles.Add(index); triangles.Add(index + 2); triangles.Add(index + 3);
                }
            }

            AddCap(vertices, normals, uv, uv2, colors, triangles, rings[verticalSegments], true,
                variant, seed);
            AddCap(vertices, normals, uv, uv2, colors, triangles, rings[0], false, variant, seed);

            Mesh mesh = new()
            {
                name = $"Looga Basalt {sideCount} Sides Variant {variant}",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uv);
            mesh.SetUVs(1, uv2);
            List<Vector4> pillarData = new(vertices.Count);
            foreach (Vector3 vertex in vertices)
                pillarData.Add(new Vector4(vertex.x, vertex.y, vertex.z, -1f));
            mesh.SetUVs(2, pillarData);
            List<Vector4> contactData = new(vertices.Count);
            foreach (Vector3 vertex in vertices)
                contactData.Add(-Vector4.one);
            mesh.SetUVs(3, contactData);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Vector3[][] BuildSmoothSideNormals(IReadOnlyList<Vector3[]> rings)
        {
            Vector3[][] result = new Vector3[rings.Count][];
            for (int ring = 0; ring < rings.Count; ring++)
            {
                IReadOnlyList<Vector3> points = rings[ring];
                result[ring] = new Vector3[points.Count];
                for (int point = 0; point < points.Count; point++)
                {
                    Vector3 radial = new(points[point].x, 0f, points[point].z);
                    radial = radial.sqrMagnitude > 0.0001f ? radial.normalized : Vector3.forward;
                    if (ring == 0)
                    {
                        result[ring][point] = Vector3.down;
                        continue;
                    }
                    if (ring == rings.Count - 1)
                    {
                        result[ring][point] = Vector3.up;
                        continue;
                    }
                    if (ring == 1)
                    {
                        result[ring][point] = (radial + Vector3.down).normalized;
                        continue;
                    }
                    if (ring == rings.Count - 2)
                    {
                        result[ring][point] = (radial + Vector3.up).normalized;
                        continue;
                    }
                    // Radial normals make every duplicated side seam agree, including the
                    // broad-face/chamfer boundaries. The polygonal silhouette remains intact.
                    result[ring][point] = radial;
                }
            }
            return result;
        }

        private static float RadiusAt(int side, int ring, float height, int verticalSegments,
            int variant, int seed, float edgeWear, float roughness)
        {
            int edgeDistance = Mathf.Min(ring, verticalSegments - ring);
            float bevelWeight = edgeDistance == 0 ? 1f : edgeDistance == 1 ? 0.36f : 0f;
            float maximumInset = Mathf.Lerp(0.018f, 0.12f, edgeWear);
            float bevel = 1f - maximumInset * bevelWeight;
            // Keep the main shaft continuous from bottom to top. Fine vertical breakup belongs
            // in the detail normal; changing radius independently per ring creates visible bands.
            float rough = (Hash01(seed + 1907, variant * 113, side) * 2f - 1f) *
                roughness * 0.45f;
            return Mathf.Max(0.2f, 0.5f * bevel * (1f + rough));
        }

        private static float EdgeMask(float height) =>
            1f - Mathf.Clamp01(Mathf.Min(height, 1f - height) * 8f);

        private static float Hash01(int seed, int variant, int side) =>
            BasaltColumnLayoutGenerator.Unit(seed + variant * 7919, side, 73);

        private static Vector3[] BuildRing(int sideCount, int ring, int verticalSegments,
            int variant, int seed, float edgeWear, float roughness, float crackDepth,
            float angleOffset)
        {
            float normalizedHeight = RingHeight(ring, verticalSegments, edgeWear);
            Vector2[] corners = new Vector2[sideCount];
            for (int side = 0; side < sideCount; side++)
            {
                float angle = angleOffset + Mathf.PI * 2f * side / sideCount;
                float radius = RadiusAt(side, ring, normalizedHeight, verticalSegments, variant, seed,
                    edgeWear, roughness);
                corners[side] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            }

            Vector3[] points = new Vector3[sideCount * 2];
            for (int side = 0; side < sideCount; side++)
            {
                int previous = (side - 1 + sideCount) % sideCount;
                int next = (side + 1) % sideCount;
                // Keep the chamfer width vertically coherent. Randomizing it per ring makes the
                // interpolation boundaries read as artificial horizontal stripes.
                float wearNoise = Hash01(seed + 3571, variant * 43, side);
                float chamfer = Mathf.Lerp(0.025f, 0.14f, edgeWear) *
                    Mathf.Lerp(0.65f, 1.35f, wearNoise);
                chamfer = Mathf.Clamp(chamfer, 0.01f, 0.22f);
                Vector2 corner = corners[side];
                Vector2 before = Vector2.Lerp(corner, corners[previous], chamfer);
                Vector2 after = Vector2.Lerp(corner, corners[next], chamfer);
                float y = normalizedHeight - 0.5f;
                points[side * 2] = new Vector3(before.x, y, before.y);
                points[side * 2 + 1] = new Vector3(after.x, y, after.y);
            }
            return points;
        }

        private static float RingHeight(int ring, int verticalSegments, float edgeWear)
        {
            if (ring <= 0) return 0f;
            if (ring >= verticalSegments) return 1f;
            if (verticalSegments < 6) return ring / (float)verticalSegments;
            float bevelHeight = Mathf.Lerp(0.003f, 0.012f, edgeWear);
            if (ring == 1) return bevelHeight;
            if (ring == 2) return bevelHeight * 2.4f;
            if (ring == verticalSegments - 1) return 1f - bevelHeight;
            if (ring == verticalSegments - 2) return 1f - bevelHeight * 2.4f;
            float interior = (ring - 2f) / (verticalSegments - 4f);
            return Mathf.Lerp(bevelHeight * 2.4f, 1f - bevelHeight * 2.4f, interior);
        }

        private static void AddCap(List<Vector3> vertices, List<Vector3> normals,
            List<Vector2> uv, List<Vector2> uv2, List<Color> colors, List<int> triangles,
            IReadOnlyList<Vector3> rim, bool top, int variant, int seed)
        {
            Vector3 normal = top ? Vector3.up : Vector3.down;
            float faceHeight = top ? float.NegativeInfinity : float.PositiveInfinity;
            for (int point = 0; point < rim.Count; point++)
                faceHeight = top ? Mathf.Max(faceHeight, rim[point].y) : Mathf.Min(faceHeight, rim[point].y);

            int center = vertices.Count;
            vertices.Add(new Vector3(0f, faceHeight, 0f));
            normals.Add(normal);
            uv.Add(new Vector2(0.5f, 0.5f));
            float capMarker = top ? -1f : -2f;
            uv2.Add(new Vector2(capMarker, 0f));
            colors.Add(new Color(0f, 0f, Hash01(seed, variant, 97), 1f));

            for (int point = 0; point < rim.Count; point++)
            {
                Vector3 position = rim[point];
                position.y = faceHeight;
                vertices.Add(position);
                normals.Add(normal);
                uv.Add(new Vector2(position.x + 0.5f, position.z + 0.5f));
                // The radial mask fades the top-rim highlight toward the cap center.
                uv2.Add(new Vector2(capMarker, 1f));
                colors.Add(new Color(1f, 0f, Hash01(seed, variant, point), 1f));
            }
            for (int point = 0; point < rim.Count; point++)
            {
                int first = center + 1 + point;
                int second = center + 1 + (point + 1) % rim.Count;
                triangles.Add(center);
                triangles.Add(top ? second : first);
                triangles.Add(top ? first : second);
            }
        }
    }
}
