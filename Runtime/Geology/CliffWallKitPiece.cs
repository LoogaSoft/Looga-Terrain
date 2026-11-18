using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// What the cliff wall kit knows about one rock prefab: where its mesh is cut, how large it is, and the views that can
    /// show. Rock meshes are often made to show one side. The rest is an open shell, and the closing tool gives that shell
    /// a flat cap with a stretched texture. A view is a direction that the piece can show. It has a map of the front and
    /// the back of the piece from that direction, so the builder can bury the back in the terrain.
    /// </summary>
    internal sealed class CliffWallKitPiece
    {
        /// <summary>One direction that the piece can show. In view space the shown side looks along +Z.</summary>
        internal sealed class View
        {
            public Quaternion Rotation;
            // Points on the front and on the back surface, in pairs, one pair for each XY cell.
            public Vector3[] Front;
            public Vector3[] Back;
            // True where the front must not show: the cut, or a stretched texture.
            public bool[] Hidden;
            public float Cell;
            public float Area;
            // Angle of the long axis of the front, in degrees from view X.
            public float LongAngle;
        }

        // Tilts of the views away from the side opposite the cut, in degrees.
        private static readonly float[] ViewTilts = { 22f, 42f };
        private const int ViewTurns = 8;
        // Cells across the longest side of a surface map.
        private const float MapCells = 44f;
        // Largest share of the front of a view that can be cut or stretched.
        private const float HiddenLimit = 0.05f;
        // A piece with less cut surface than this share can show all sides.
        private const float SmallestCut = 0.02f;
        // A triangle is stretched when its texture density is this far under the median of its mesh.
        private const float StretchLimit = 3f;
        // Level of detail that the maps read. It keeps the shape with far fewer triangles.
        private const int MapDetail = 1;
        // Vertices of a piece that the foot boulder fit reads.
        private const int MaximumVertices = 1500;

        public GameObject Prefab;
        public Vector3[] Vertices;
        public Vector3 Min;
        public Vector3 Max;
        public float Longest;
        // The way that the cut surface faces, in prefab space. Zero when the mesh has no cut.
        public Vector3 CutDirection;
        public float CutShare;
        public View[] Views;

        /// <summary>Reads the prefabs. Prefabs without meshes are left out.</summary>
        internal static List<CliffWallKitPiece> Read(List<GameObject> prefabs)
        {
            var pieces = new List<CliffWallKitPiece>();
            foreach (GameObject prefab in prefabs)
            {
                if (!prefab) continue;
                CliffWallKitPiece piece = ReadPrefab(prefab);
                if (piece != null) pieces.Add(piece);
            }
            return pieces;
        }

        private static CliffWallKitPiece ReadPrefab(GameObject prefab)
        {
            var points = new List<Vector3>();
            var hidden = new List<bool>();
            var surface = new List<Vector3>();
            float totalArea = 0f;
            float cutArea = 0f;
            Vector3 cutDirection = Vector3.zero;
            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
            foreach (MeshFilter filter in Filters(prefab))
            {
                Mesh mesh = filter.sharedMesh;
                if (!mesh) continue;
                Matrix4x4 matrix = toRoot * filter.transform.localToWorldMatrix;
                var vertices = new List<Vector3>();
                mesh.GetVertices(vertices);
                int step = Mathf.Max(1, vertices.Count / MaximumVertices);
                for (int index = 0; index < vertices.Count; index += step)
                {
                    surface.Add(matrix.MultiplyPoint3x4(vertices[index]));
                }
                // The editor can read all mesh data. In a player an unreadable mesh has only its bounds.
                if (!mesh.isReadable && !Application.isEditor)
                {
                    AddBox(mesh.bounds, matrix, points, hidden);
                    continue;
                }
                var normals = new List<Vector3>();
                var uvs = new List<Vector2>();
                var indices = new List<int>();
                mesh.GetNormals(normals);
                mesh.GetUVs(0, uvs);
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    mesh.GetTriangles(indices, subMesh);
                    float limit = MedianDensity(vertices, uvs, indices) / StretchLimit;
                    for (int corner = 0; corner + 2 < indices.Count; corner += 3)
                    {
                        int a = indices[corner];
                        int b = indices[corner + 1];
                        int c = indices[corner + 2];
                        Vector3 pointA = matrix.MultiplyPoint3x4(vertices[a]);
                        Vector3 pointB = matrix.MultiplyPoint3x4(vertices[b]);
                        Vector3 pointC = matrix.MultiplyPoint3x4(vertices[c]);
                        points.Add(pointA);
                        points.Add(pointB);
                        points.Add(pointC);
                        Vector3 cross = Vector3.Cross(pointB - pointA, pointC - pointA);
                        float area = cross.magnitude * 0.5f;
                        totalArea += area;
                        // A closed mesh gives all vertices of a cap the same normal. Sculpted rock has no such triangles.
                        bool cut = normals.Count > 0 && normals[a] == normals[b] && normals[b] == normals[c];
                        if (cut)
                        {
                            cutArea += area;
                            cutDirection += cross * 0.5f;
                        }
                        hidden.Add(cut || uvs.Count > 0 && Density(vertices, uvs, a, b, c) < limit);
                    }
                }
            }
            if (points.Count == 0) return null;

            var piece = new CliffWallKitPiece { Prefab = prefab, Vertices = surface.ToArray() };
            piece.Min = points[0];
            piece.Max = points[0];
            foreach (Vector3 point in points)
            {
                piece.Min = Vector3.Min(piece.Min, point);
                piece.Max = Vector3.Max(piece.Max, point);
            }
            Vector3 size = piece.Max - piece.Min;
            piece.Longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            piece.CutShare = totalArea > 0f ? cutArea / totalArea : 0f;
            piece.CutDirection = piece.CutShare >= SmallestCut ? cutDirection.normalized : Vector3.zero;
            piece.Views = BuildViews(piece, points, hidden);
            return piece;
        }

        // The mesh filters of a level of detail with few triangles, or of all enabled renderers without a LOD group.
        private static List<MeshFilter> Filters(GameObject prefab)
        {
            var filters = new List<MeshFilter>();
            if (prefab.TryGetComponent(out LODGroup group) && group.lodCount > 0)
            {
                foreach (Renderer renderer in group.GetLODs()[Mathf.Min(MapDetail, group.lodCount - 1)].renderers)
                {
                    if (renderer && renderer.TryGetComponent(out MeshFilter filter))
                    {
                        filters.Add(filter);
                    }
                }
                return filters;
            }
            foreach (MeshRenderer renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.enabled && renderer.TryGetComponent(out MeshFilter filter))
                {
                    filters.Add(filter);
                }
            }
            return filters;
        }

        private static void AddBox(Bounds bounds, Matrix4x4 matrix, List<Vector3> points, List<bool> hidden)
        {
            var corners = new Vector3[8];
            for (int corner = 0; corner < 8; corner++)
            {
                corners[corner] = matrix.MultiplyPoint3x4(new Vector3(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z));
            }
            int[] faces =
            {
                0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3
            };
            foreach (int corner in faces)
            {
                points.Add(corners[corner]);
            }
            for (int triangle = 0; triangle < faces.Length / 3; triangle++)
            {
                hidden.Add(false);
            }
        }

        // Texture area for each unit of surface area. A small value means a stretched texture.
        private static float Density(List<Vector3> vertices, List<Vector2> uvs, int a, int b, int c)
        {
            float area = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).magnitude;
            float uvArea = Mathf.Abs((uvs[b].x - uvs[a].x) * (uvs[c].y - uvs[a].y) -
                                     (uvs[c].x - uvs[a].x) * (uvs[b].y - uvs[a].y));
            return area > 1e-8f ? uvArea / area : float.MaxValue;
        }

        private static float MedianDensity(List<Vector3> vertices, List<Vector2> uvs, List<int> indices)
        {
            if (uvs.Count == 0) return 0f;
            var densities = new List<float>(indices.Count / 3);
            for (int corner = 0; corner + 2 < indices.Count; corner += 3)
            {
                densities.Add(Density(vertices, uvs, indices[corner], indices[corner + 1], indices[corner + 2]));
            }
            densities.Sort();
            return densities.Count > 0 ? densities[densities.Count / 2] : 0f;
        }

        // The views around the side opposite the cut. A piece without a cut shows all sides.
        private static View[] BuildViews(CliffWallKitPiece piece, List<Vector3> points, List<bool> hidden)
        {
            var directions = new List<Vector3>();
            if (piece.CutDirection == Vector3.zero)
            {
                for (int x = -1; x <= 1; x++)
                {
                    for (int y = -1; y <= 1; y++)
                    {
                        for (int z = -1; z <= 1; z++)
                        {
                            if (x != 0 || y != 0 || z != 0) directions.Add(new Vector3(x, y, z).normalized);
                        }
                    }
                }
            }
            else
            {
                Vector3 centre = -piece.CutDirection;
                Vector3 side = Vector3.Cross(centre, Mathf.Abs(centre.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                directions.Add(centre);
                foreach (float tilt in ViewTilts)
                {
                    for (int turn = 0; turn < ViewTurns; turn++)
                    {
                        Vector3 axis = Quaternion.AngleAxis(turn * 360f / ViewTurns, centre) * side;
                        directions.Add(Quaternion.AngleAxis(tilt, axis) * centre);
                    }
                }
            }

            var views = new List<View>();
            View best = null;
            float bestShare = float.MaxValue;
            foreach (Vector3 direction in directions)
            {
                View view = BuildView(direction, piece.Longest, points, hidden, out float share);
                if (share <= HiddenLimit) views.Add(view);
                if (share >= bestShare) continue;
                bestShare = share;
                best = view;
            }
            if (views.Count == 0 && best != null) views.Add(best);
            return views.ToArray();
        }

        // Rasterizes the mesh from a direction: the nearest and the farthest surface over each XY cell.
        private static View BuildView(Vector3 direction, float longest, List<Vector3> points, List<bool> hidden,
            out float hiddenShare)
        {
            Quaternion rotation = Quaternion.FromToRotation(direction, Vector3.forward);
            var turned = new Vector3[points.Count];
            Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
            for (int index = 0; index < turned.Length; index++)
            {
                turned[index] = rotation * points[index];
                min = Vector3.Min(min, turned[index]);
                max = Vector3.Max(max, turned[index]);
            }
            float cell = longest / MapCells;
            int width = Mathf.Max(1, Mathf.CeilToInt((max.x - min.x) / cell));
            int height = Mathf.Max(1, Mathf.CeilToInt((max.y - min.y) / cell));
            var front = new float[width * height];
            var back = new float[width * height];
            var marks = new bool[width * height];
            for (int index = 0; index < front.Length; index++)
            {
                front[index] = float.MinValue;
                back[index] = float.MaxValue;
            }
            for (int index = 0; index + 2 < turned.Length; index += 3)
            {
                Rasterize(turned[index], turned[index + 1], turned[index + 2], min, cell, width, height, hidden[index / 3],
                    front, back, marks);
            }

            var fronts = new List<Vector3>();
            var backs = new List<Vector3>();
            var flags = new List<bool>();
            int hiddenCells = 0;
            Vector2 mean = Vector2.zero;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = x + y * width;
                    if (front[index] == float.MinValue) continue;
                    var point = new Vector2(min.x + (x + 0.5f) * cell, min.y + (y + 0.5f) * cell);
                    fronts.Add(new Vector3(point.x, point.y, front[index]));
                    backs.Add(new Vector3(point.x, point.y, back[index]));
                    flags.Add(marks[index]);
                    mean += point;
                    if (marks[index]) hiddenCells++;
                }
            }
            hiddenShare = fronts.Count > 0 ? (float)hiddenCells / fronts.Count : 1f;
            mean /= Mathf.Max(1, fronts.Count);
            return new View
            {
                Rotation = rotation,
                Front = fronts.ToArray(),
                Back = backs.ToArray(),
                Hidden = flags.ToArray(),
                Cell = cell,
                Area = fronts.Count * cell * cell,
                LongAngle = LongAxis(fronts, mean)
            };
        }

        // The main axis of the points in XY, in degrees.
        private static float LongAxis(List<Vector3> points, Vector2 mean)
        {
            float xx = 0f;
            float xy = 0f;
            float yy = 0f;
            foreach (Vector3 point in points)
            {
                float x = point.x - mean.x;
                float y = point.y - mean.y;
                xx += x * x;
                xy += x * y;
                yy += y * y;
            }
            return 0.5f * Mathf.Atan2(2f * xy, xx - yy) * Mathf.Rad2Deg;
        }

        private static void Rasterize(Vector3 a, Vector3 b, Vector3 c, Vector3 min, float size, int width, int height,
            bool hidden, float[] front, float[] back, bool[] marks)
        {
            float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            if (Mathf.Abs(area) < 1e-6f) return;
            int startX = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - min.x) / size), 0, width - 1);
            int endX = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - min.x) / size), 0, width - 1);
            int startY = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - min.y) / size), 0, height - 1);
            int endY = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - min.y) / size), 0, height - 1);
            for (int y = startY; y <= endY; y++)
            {
                float pointY = min.y + (y + 0.5f) * size;
                for (int x = startX; x <= endX; x++)
                {
                    float pointX = min.x + (x + 0.5f) * size;
                    float weightA = ((b.x - pointX) * (c.y - pointY) - (c.x - pointX) * (b.y - pointY)) / area;
                    float weightB = ((c.x - pointX) * (a.y - pointY) - (a.x - pointX) * (c.y - pointY)) / area;
                    float weightC = 1f - weightA - weightB;
                    if (weightA < 0f || weightB < 0f || weightC < 0f) continue;
                    float z = a.z * weightA + b.z * weightB + c.z * weightC;
                    int cell = x + y * width;
                    if (z > front[cell])
                    {
                        front[cell] = z;
                        marks[cell] = hidden;
                    }
                    back[cell] = Mathf.Min(back[cell], z);
                }
            }
        }
    }
}
