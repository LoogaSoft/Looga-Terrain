using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    public enum HeightMapping
    {
        /// <summary>
        /// MicroVerse 1.7 behavior: sample-centre stamp UV for each tile, 16-bit rounding after each stamp,
        /// MicroVerse tile culling. Existing MicroVerse content and captured masks need it.
        /// </summary>
        MicroVerseParity = 0,
        /// <summary>Heightmap samples at their world positions, no rounding between stamps.</summary>
        WorldAligned = 1
    }

    /// <summary>One terrain tile of the stamp world, with the values that the height pass needs.</summary>
    internal readonly struct StampTile
    {
        internal readonly UnityEngine.Terrain Terrain;
        internal readonly int Resolution;
        internal readonly Vector3 Position;
        internal readonly Vector3 Size;
        internal readonly Matrix4x4 WorldToLocal;
        // Heightmap scale times resolution. MicroVerse maps sample-centre UVs over this size.
        internal readonly Vector2 SampleSpan;
        // Height of one heightmap texture unit, in metres (twice the terrain height).
        internal readonly float RealHeight;

        internal StampTile(UnityEngine.Terrain terrain)
        {
            TerrainData data = terrain.terrainData;
            Terrain = terrain;
            Resolution = data.heightmapResolution;
            Position = terrain.transform.position;
            Size = data.size;
            WorldToLocal = terrain.transform.worldToLocalMatrix;
            Vector3 scale = data.heightmapScale;
            SampleSpan = new Vector2(scale.x * Resolution, scale.z * Resolution);
            RealHeight = scale.y * 2f;
        }

        internal Rect WorldRect => new Rect(Position.x, Position.z, Size.x, Size.z);
    }

    /// <summary>Maps heightmap samples to stamp UV and finds the tiles that a stamp changes.</summary>
    internal static class StampMapping
    {
        /// <summary>Matrix from tile UV (sample centre (i + 0.5) / resolution) to stamp UV.</summary>
        internal static Matrix4x4 TileToStamp(in StampTile tile, Transform stamp, HeightMapping mapping)
        {
            Vector3 local = tile.WorldToLocal.MultiplyPoint3x4(stamp.position);
            Vector3 scale = stamp.lossyScale;
            Vector2 stampSize = new Vector2(scale.x, scale.z);
            Matrix4x4 turn = Matrix4x4.Rotate(Quaternion.AngleAxis(stamp.rotation.eulerAngles.y, Vector3.forward));
            Matrix4x4 toStamp = Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0f));
            if (mapping == HeightMapping.MicroVerseParity)
            {
                // MicroVerse divides the position by the sample span and scales by the terrain size.
                // The stamp moves by half a sample and shrinks by (resolution - 1) / resolution.
                Vector2 tileSize = new Vector2(tile.Size.x, tile.Size.z);
                Vector2 centre = new Vector2(local.x / tile.SampleSpan.x, local.z / tile.SampleSpan.y);
                Vector2 uvScale = tileSize / stampSize;
                // Multiply from the right in this order: the float result then equals the MicroVerse matrix.
                Matrix4x4 parity = Matrix4x4.Translate(new Vector3(-centre.x, -centre.y, 0f));
                parity = turn * parity;
                parity = Matrix4x4.Scale(new Vector3(uvScale.x, uvScale.y, 0f)) * parity;
                return toStamp * parity;
            }
            // Sample i is at i * spacing from the tile corner, and tile UV = (i + 0.5) / resolution.
            Vector2 spacing = new Vector2(tile.Size.x / (tile.Resolution - 1), tile.Size.z / (tile.Resolution - 1));
            Matrix4x4 uvToLocal = Matrix4x4.Translate(new Vector3(-0.5f * spacing.x, -0.5f * spacing.y, 0f)) *
                Matrix4x4.Scale(new Vector3(spacing.x * tile.Resolution, spacing.y * tile.Resolution, 1f));
            return toStamp * Matrix4x4.Scale(new Vector3(1f / stampSize.x, 1f / stampSize.y, 0f)) * turn *
                Matrix4x4.Translate(new Vector3(-local.x, -local.z, 0f)) * uvToLocal;
        }

        /// <summary>
        /// Matrix from alphamap tile UV (0 to 1 over the terrain size) to stamp UV, like the MicroVerse splat stamps.
        /// It equals the parity height matrix, but the stamp position is divided by the terrain size.
        /// </summary>
        internal static Matrix4x4 TileToStampSplat(in StampTile tile, Transform stamp)
        {
            Vector3 local = tile.WorldToLocal.MultiplyPoint3x4(stamp.position);
            Vector3 scale = stamp.lossyScale;
            Vector2 tileSize = new Vector2(tile.Size.x, tile.Size.z);
            Vector2 centre = new Vector2(local.x, local.z) / tileSize;
            Matrix4x4 turn = Matrix4x4.Rotate(Quaternion.AngleAxis(stamp.rotation.eulerAngles.y, Vector3.forward));
            // Multiply from the right in the MicroVerse order, so the float result is the same.
            Matrix4x4 matrix = Matrix4x4.Translate(new Vector3(-centre.x, -centre.y, 0f));
            matrix = turn * matrix;
            matrix = Matrix4x4.Scale(new Vector3(tileSize.x / scale.x, tileSize.y / scale.z, 0f)) * matrix;
            return Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0f)) * matrix;
        }

        /// <summary>World XZ rectangle that decides which tiles get the stamp.</summary>
        internal static Rect CullRect(Transform stamp, HeightMapping mapping)
        {
            Vector3 scale = stamp.lossyScale;
            Vector3 position = stamp.position;
            if (mapping == HeightMapping.MicroVerseParity)
            {
                // MicroVerse uses a square of the larger axis. For a turned stamp it adds only two turned corners,
                // so it can miss a tile that a corner of the stamp touches. Parity keeps that behavior.
                float side = Mathf.Max(scale.x, scale.z);
                var bounds = new Bounds(position, new Vector3(side, side, side));
                Matrix4x4 turn = Matrix4x4.TRS(position, stamp.rotation, Vector3.one);
                bounds.Encapsulate(turn.MultiplyPoint(bounds.size / 2f));
                bounds.Encapsulate(turn.MultiplyPoint(-bounds.size / 2f));
                return Rect.MinMaxRect(bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z);
            }
            Quaternion yaw = Quaternion.Euler(0f, stamp.rotation.eulerAngles.y, 0f);
            Vector3 x = yaw * new Vector3(scale.x * 0.5f, 0f, 0f);
            Vector3 z = yaw * new Vector3(0f, 0f, scale.z * 0.5f);
            float halfX = Mathf.Abs(x.x) + Mathf.Abs(z.x);
            float halfZ = Mathf.Abs(x.z) + Mathf.Abs(z.z);
            return Rect.MinMaxRect(position.x - halfX, position.z - halfZ, position.x + halfX, position.z + halfZ);
        }

        /// <summary>True when two rectangles overlap or touch (Bounds.Intersects rule).</summary>
        internal static bool Touches(Rect a, Rect b)
        {
            return a.xMin <= b.xMax && a.xMax >= b.xMin && a.yMin <= b.yMax && a.yMax >= b.yMin;
        }

        /// <summary>
        /// Overlap of two rectangles. Without an overlap it is an empty rectangle at the point of a that is nearest to
        /// the centre of b, so it touches at most one point.
        /// </summary>
        internal static Rect Intersect(Rect a, Rect b)
        {
            float xMin = Mathf.Max(a.xMin, b.xMin), yMin = Mathf.Max(a.yMin, b.yMin);
            float xMax = Mathf.Min(a.xMax, b.xMax), yMax = Mathf.Min(a.yMax, b.yMax);
            if (xMax < xMin || yMax < yMin)
            {
                return new Rect(Mathf.Clamp(b.center.x, a.xMin, a.xMax), Mathf.Clamp(b.center.y, a.yMin, a.yMax), 0f, 0f);
            }
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        /// <summary>
        /// Texel rectangle of the work texture (tile plus halo) that can contain stamp UV 0 to 1.
        /// </summary>
        internal static RectInt StampTexels(Matrix4x4 tileToStamp, int resolution, int halo)
        {
            // Invert the 2D affine part: stamp UV = A * tile UV + t.
            float a = tileToStamp.m00, b = tileToStamp.m01, c = tileToStamp.m10, d = tileToStamp.m11;
            float determinant = a * d - b * c;
            int size = resolution + 2 * halo;
            if (Mathf.Abs(determinant) < 1e-20f) return new RectInt(0, 0, size, size);
            var translation = new Vector2(tileToStamp.m03, tileToStamp.m13);
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int corner = 0; corner < 4; corner++)
            {
                Vector2 uv = new Vector2(corner & 1, corner >> 1) - translation;
                float u = (d * uv.x - b * uv.y) / determinant;
                float v = (a * uv.y - c * uv.x) / determinant;
                // Texel = tile UV * resolution - 0.5 + halo.
                float x = u * resolution - 0.5f + halo;
                float z = v * resolution - 0.5f + halo;
                minX = Mathf.Min(minX, x);
                maxX = Mathf.Max(maxX, x);
                minZ = Mathf.Min(minZ, z);
                maxZ = Mathf.Max(maxZ, z);
            }
            // One texel of margin covers float error at the stamp edge.
            int x0 = Mathf.Clamp(Mathf.FloorToInt(minX) - 1, 0, size);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(minZ) - 1, 0, size);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(maxX) + 2, 0, size);
            int z1 = Mathf.Clamp(Mathf.CeilToInt(maxZ) + 2, 0, size);
            return new RectInt(x0, z0, x1 - x0, z1 - z0);
        }
    }
}
