using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Finds and closes the openings of solid meshes, such as rock scans with an open back or bottom. A vendor rock is
    /// often a shell that must stand against a cliff or in the ground. Any other placement shows the inside. A closed
    /// copy of the mesh has a cap over each opening, so no placement can show a hole.
    /// </summary>
    public static class RockMeshOpenings
    {
        /// <summary>One opening: the vertex indices of its rim in boundary order and the outward area vector.</summary>
        public readonly struct Opening
        {
            public readonly int[] Rim;
            public readonly Vector3 Area;
            public readonly int SubMesh;

            public Opening(int[] rim, Vector3 area, int subMesh)
            {
                Rim = rim;
                Area = area;
                SubMesh = subMesh;
            }
        }

        // Positions closer than this count as one vertex, so UV and normal seams are not openings.
        private const float WeldScale = 10000f;
        // A solid has few open edges. Foliage cards and other thin meshes have open edges everywhere.
        private const float SolidOpenEdgeShare = 0.15f;
        private const float MinimumOpeningArea = 1e-4f;
        // Openings smaller than this share of the surface area, such as branch tips, cannot show a clear hole.
        private const float MainOpeningShare = 0.01f;
        // An opening that points at least this far below the horizon stays in the ground after the tilt.
        private const float SafeDownAngle = 45f;
        // A rock or a log has few openings. Foliage has one opening for each leaf card.
        private const int MaximumSolidOpenings = 4;
        // A ground opening whose rim rises higher than this share of the mesh height shows above a normal sink.
        private const float SafeRimRise = 0.15f;

        private static readonly Dictionary<int, bool> _openSolidCache = new();
        private static readonly Dictionary<int, Vector4[]> _mainOpeningCache = new();

        #region Analysis
        /// <summary>
        /// Finds the openings of a mesh. Rims with fewer than three vertices or almost no area are ignored.
        /// </summary>
        /// <returns>The number of open edges, including the ignored ones.</returns>
        public static int FindOpenings(Mesh mesh, List<Opening> openings, out int edgeCount)
        {
            openings.Clear();
            edgeCount = 0;
            if (!mesh) return 0;

            Vector3[] vertices = mesh.vertices;
            int[] welded = Weld(vertices);
            var useCount = new Dictionary<long, int>();
            var directed = new List<(int from, int to, int subMesh)>();
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                int[] triangles = mesh.GetTriangles(subMesh);
                for (int index = 0; index < triangles.Length; index += 3)
                {
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int from = triangles[index + corner];
                        int to = triangles[index + (corner + 1) % 3];
                        long key = EdgeKey(welded[from], welded[to]);
                        useCount[key] = useCount.TryGetValue(key, out int count) ? count + 1 : 1;
                        directed.Add((from, to, subMesh));
                    }
                }
            }
            edgeCount = useCount.Count;

            // Outgoing boundary edges of each welded rim vertex, in the direction of the triangles that own them.
            var outgoing = new Dictionary<int, List<(int from, int to, int subMesh)>>();
            int openEdges = 0;
            foreach ((int from, int to, int subMesh) edge in directed)
            {
                if (useCount[EdgeKey(welded[edge.from], welded[edge.to])] != 1) continue;
                openEdges++;
                if (!outgoing.TryGetValue(welded[edge.from], out List<(int, int, int)> list))
                {
                    list = new List<(int, int, int)>();
                    outgoing.Add(welded[edge.from], list);
                }
                list.Add(edge);
            }

            var used = new HashSet<(int, int)>();
            var rim = new List<int>();
            foreach (List<(int from, int to, int subMesh)> start in outgoing.Values)
            {
                foreach ((int from, int to, int subMesh) first in start)
                {
                    if (used.Contains((first.from, first.to))) continue;
                    rim.Clear();
                    (int from, int to, int subMesh) edge = first;
                    while (used.Add((edge.from, edge.to)))
                    {
                        rim.Add(edge.from);
                        if (!TryNext(outgoing, welded[edge.to], used, out edge)) break;
                    }
                    if (rim.Count < 3) continue;
                    Vector3 area = RimArea(vertices, rim);
                    if (area.magnitude < MinimumOpeningArea) continue;
                    openings.Add(new Opening(rim.ToArray(), area, first.subMesh));
                }
            }
            return openEdges;
        }

        /// <summary>
        /// True when the mesh is a solid with at least one opening. Thin meshes, such as foliage cards, return false.
        /// The result is cached for each mesh.
        /// </summary>
        public static bool IsOpenSolid(Mesh mesh)
        {
            if (!mesh) return false;
            int id = mesh.GetInstanceID();
            if (_openSolidCache.TryGetValue(id, out bool cached)) return cached;
            var openings = new List<Opening>();
            int openEdges = FindOpenings(mesh, openings, out int edgeCount);
            bool openSolid = openings.Count > 0 && edgeCount > 0 && openEdges < edgeCount * SolidOpenEdgeShare;
            _openSolidCache[id] = openSolid;
            return openSolid;
        }

        /// <summary>
        /// True when an upright copy of the mesh, tilted by up to the given angle, can show a main opening. A ground
        /// opening is safe only when it points down after the tilt and its rim stays near the base of the mesh.
        /// Only solids with at most four openings count, so foliage cards return false. A mushroom with an open base
        /// returns false. A rock shell with an open back returns true. The result is cached for each mesh.
        /// </summary>
        public static bool CanShowOpening(Mesh mesh, float maxTiltDegrees)
        {
            if (!mesh) return false;
            foreach (Vector4 opening in MainOpenings(mesh))
            {
                float belowHorizon = Vector3.Angle(opening, Vector3.up) - 90f;
                if (belowHorizon - maxTiltDegrees < SafeDownAngle || opening.w > SafeRimRise) return true;
            }
            return false;
        }

        /// <summary>Removes a mesh from the analysis cache, for example after its vertices change.</summary>
        public static void Forget(Mesh mesh)
        {
            if (mesh)
            {
                _openSolidCache.Remove(mesh.GetInstanceID());
                _mainOpeningCache.Remove(mesh.GetInstanceID());
            }
        }

        // The openings that cover at least MainOpeningShare of the surface: xyz = outward direction, w = height of the
        // highest rim vertex above the lowest mesh vertex, as a share of the mesh height.
        private static Vector4[] MainOpenings(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (_mainOpeningCache.TryGetValue(id, out Vector4[] cached)) return cached;
            var openings = new List<Opening>();
            FindOpenings(mesh, openings, out _);
            if (openings.Count > MaximumSolidOpenings || !IsOpenSolid(mesh))
            {
                openings.Clear();
            }
            float surface = 0f;
            Vector3[] vertices = openings.Count > 0 ? mesh.vertices : null;
            if (openings.Count > 0)
            {
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                    int[] triangles = mesh.GetTriangles(subMesh);
                    for (int index = 0; index < triangles.Length; index += 3)
                    {
                        Vector3 a = vertices[triangles[index]];
                        surface += Vector3.Cross(vertices[triangles[index + 1]] - a,
                            vertices[triangles[index + 2]] - a).magnitude * 0.5f;
                    }
                }
            }
            var main = new List<Vector4>();
            Bounds bounds = mesh.bounds;
            foreach (Opening opening in openings)
            {
                if (opening.Area.magnitude < surface * MainOpeningShare) continue;
                float rimTop = float.MinValue;
                foreach (int vertex in opening.Rim)
                {
                    rimTop = Mathf.Max(rimTop, vertices[vertex].y);
                }
                float rise = (rimTop - bounds.min.y) / Mathf.Max(bounds.size.y, 1e-4f);
                Vector3 direction = opening.Area.normalized;
                main.Add(new Vector4(direction.x, direction.y, direction.z, rise));
            }
            cached = main.ToArray();
            _mainOpeningCache[id] = cached;
            return cached;
        }
        #endregion

        #region Closing
        /// <summary>
        /// Returns a copy of the mesh with a cap over each opening. The cap has its own vertices with a flat normal,
        /// and it takes the UVs and colors of the rim. Returns null when the mesh has no opening.
        /// </summary>
        public static Mesh CreateClosedMesh(Mesh source)
        {
            var openings = new List<Opening>();
            FindOpenings(source, openings, out _);
            if (openings.Count == 0) return null;

            Mesh mesh = Object.Instantiate(source);
            mesh.name = source.name + " Closed";
            var positions = new List<Vector3>();
            mesh.GetVertices(positions);
            var normals = new List<Vector3>();
            mesh.GetNormals(normals);
            var tangents = new List<Vector4>();
            mesh.GetTangents(tangents);
            var colors = new List<Color>();
            mesh.GetColors(colors);
            var uvs = new List<Vector4>[8];
            for (int channel = 0; channel < uvs.Length; channel++)
            {
                if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + channel)) continue;
                uvs[channel] = new List<Vector4>();
                mesh.GetUVs(channel, uvs[channel]);
            }
            var subMeshTriangles = new List<int>[mesh.subMeshCount];
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                subMeshTriangles[subMesh] = new List<int>(mesh.GetTriangles(subMesh));
            }

            foreach (Opening opening in openings)
            {
                // The cap runs the rim in the reverse direction of the triangles that own the rim edges.
                int count = opening.Rim.Length;
                Vector3 normal = opening.Area.normalized;
                int first = positions.Count;
                for (int index = count - 1; index >= 0; index--)
                {
                    int rimVertex = opening.Rim[index];
                    positions.Add(positions[rimVertex]);
                    if (normals.Count > 0)
                    {
                        normals.Add(normal);
                    }
                    if (tangents.Count > 0)
                    {
                        tangents.Add(CapTangent(tangents[rimVertex], normal));
                    }
                    if (colors.Count > 0)
                    {
                        colors.Add(colors[rimVertex]);
                    }
                    foreach (List<Vector4> channel in uvs)
                    {
                        channel?.Add(channel[rimVertex]);
                    }
                }
                List<int> triangles = subMeshTriangles[opening.SubMesh];
                if (!TriangulateRim(positions, first, count, normal, triangles))
                {
                    AddFan(positions, normals, tangents, colors, uvs, first, count, normal, triangles);
                }
            }

            mesh.indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : mesh.indexFormat;
            mesh.SetVertices(positions);
            if (normals.Count > 0)
            {
                mesh.SetNormals(normals);
            }
            if (tangents.Count > 0)
            {
                mesh.SetTangents(tangents);
            }
            if (colors.Count > 0)
            {
                mesh.SetColors(colors);
            }
            for (int channel = 0; channel < uvs.Length; channel++)
            {
                if (uvs[channel] != null)
                {
                    mesh.SetUVs(channel, uvs[channel]);
                }
            }
            for (int subMesh = 0; subMesh < subMeshTriangles.Length; subMesh++)
            {
                mesh.SetTriangles(subMeshTriangles[subMesh], subMesh, false);
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        // Ear clipping in the plane of the opening. The cap vertices run against the rim edges, so each ear has the
        // winding of the outward normal. False when the polygon has no ear, for example a rim that crosses itself.
        private static bool TriangulateRim(List<Vector3> positions, int first, int count, Vector3 normal,
            List<int> triangles)
        {
            Vector3 axisU = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 axisV = Vector3.Cross(normal, axisU);
            var points = new Vector2[count];
            for (int index = 0; index < count; index++)
            {
                Vector3 position = positions[first + index];
                points[index] = new Vector2(Vector3.Dot(position, axisU), Vector3.Dot(position, axisV));
            }
            float winding = Mathf.Sign(SignedArea(points));
            var remaining = new List<int>(count);
            for (int index = 0; index < count; index++)
            {
                remaining.Add(index);
            }

            var added = new List<int>();
            while (remaining.Count > 3)
            {
                bool clipped = false;
                for (int index = 0; index < remaining.Count; index++)
                {
                    int previous = remaining[(index + remaining.Count - 1) % remaining.Count];
                    int current = remaining[index];
                    int next = remaining[(index + 1) % remaining.Count];
                    if (!IsEar(points, remaining, previous, current, next, winding)) continue;
                    AddTriangle(added, first, previous, current, next, positions, normal);
                    remaining.RemoveAt(index);
                    clipped = true;
                    break;
                }
                if (!clipped) return false;
            }
            AddTriangle(added, first, remaining[0], remaining[1], remaining[2], positions, normal);
            triangles.AddRange(added);
            return true;
        }

        // Fan from a center vertex. It closes any rim, also one that crosses itself in the plane of the opening.
        private static void AddFan(List<Vector3> positions, List<Vector3> normals, List<Vector4> tangents,
            List<Color> colors, List<Vector4>[] uvs, int first, int count, Vector3 normal, List<int> triangles)
        {
            int center = positions.Count;
            Vector3 sum = Vector3.zero;
            for (int index = 0; index < count; index++)
            {
                sum += positions[first + index];
            }
            positions.Add(sum / count);
            if (normals.Count > 0)
            {
                normals.Add(normal);
            }
            if (tangents.Count > 0)
            {
                tangents.Add(tangents[first]);
            }
            if (colors.Count > 0)
            {
                colors.Add(colors[first]);
            }
            foreach (List<Vector4> channel in uvs)
            {
                if (channel == null) continue;
                Vector4 uv = Vector4.zero;
                for (int index = 0; index < count; index++)
                {
                    uv += channel[first + index];
                }
                channel.Add(uv / count);
            }
            for (int index = 0; index < count; index++)
            {
                AddTriangle(triangles, 0, center, first + index, first + (index + 1) % count, positions, normal);
            }
        }

        // Adds one triangle with the winding of the outward normal.
        private static void AddTriangle(List<int> triangles, int first, int a, int b, int c, List<Vector3> positions,
            Vector3 normal)
        {
            int ia = first + a, ib = first + b, ic = first + c;
            Vector3 face = Vector3.Cross(positions[ib] - positions[ia], positions[ic] - positions[ia]);
            if (Vector3.Dot(face, normal) < 0f)
            {
                (ib, ic) = (ic, ib);
            }
            triangles.Add(ia);
            triangles.Add(ib);
            triangles.Add(ic);
        }
        #endregion

        #region Helpers
        private static int[] Weld(Vector3[] vertices)
        {
            var ids = new Dictionary<Vector3Int, int>();
            var welded = new int[vertices.Length];
            for (int index = 0; index < vertices.Length; index++)
            {
                Vector3Int key = Vector3Int.RoundToInt(vertices[index] * WeldScale);
                if (!ids.TryGetValue(key, out int id))
                {
                    id = ids.Count;
                    ids.Add(key, id);
                }
                welded[index] = id;
            }
            return welded;
        }

        private static long EdgeKey(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        private static bool TryNext(Dictionary<int, List<(int from, int to, int subMesh)>> outgoing, int vertex,
            HashSet<(int, int)> used, out (int from, int to, int subMesh) next)
        {
            next = default;
            if (!outgoing.TryGetValue(vertex, out List<(int from, int to, int subMesh)> list)) return false;
            foreach ((int from, int to, int subMesh) edge in list)
            {
                if (used.Contains((edge.from, edge.to))) continue;
                next = edge;
                return true;
            }
            return false;
        }

        // Outward area vector of the missing cap. The rim runs with the surface triangles, so the cap normal is the
        // reverse of the rim's own area vector.
        private static Vector3 RimArea(Vector3[] vertices, List<int> rim)
        {
            Vector3 area = Vector3.zero;
            Vector3 origin = vertices[rim[0]];
            for (int index = 1; index < rim.Count - 1; index++)
            {
                area += Vector3.Cross(vertices[rim[index]] - origin, vertices[rim[index + 1]] - origin);
            }
            return -0.5f * area;
        }

        private static Vector4 CapTangent(Vector4 tangent, Vector3 normal)
        {
            var direction = new Vector3(tangent.x, tangent.y, tangent.z);
            direction -= normal * Vector3.Dot(direction, normal);
            if (direction.sqrMagnitude < 1e-8f)
            {
                direction = Vector3.Cross(normal, Vector3.up);
            }
            if (direction.sqrMagnitude < 1e-8f)
            {
                direction = Vector3.right;
            }
            direction.Normalize();
            return new Vector4(direction.x, direction.y, direction.z, tangent.w < 0f ? -1f : 1f);
        }

        private static float SignedArea(Vector2[] points)
        {
            float area = 0f;
            for (int index = 0; index < points.Length; index++)
            {
                Vector2 a = points[index];
                Vector2 b = points[(index + 1) % points.Length];
                area += a.x * b.y - b.x * a.y;
            }
            return area * 0.5f;
        }

        private static bool IsEar(Vector2[] points, List<int> remaining, int previous, int current, int next,
            float winding)
        {
            Vector2 a = points[previous], b = points[current], c = points[next];
            float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            if (cross * winding <= 0f) return false;
            foreach (int index in remaining)
            {
                if (index == previous || index == current || index == next) continue;
                if (InTriangle(points[index], a, b, c, winding)) return false;
            }
            return true;
        }

        private static bool InTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c, float winding)
        {
            float ab = ((b.x - a.x) * (point.y - a.y) - (b.y - a.y) * (point.x - a.x)) * winding;
            float bc = ((c.x - b.x) * (point.y - b.y) - (c.y - b.y) * (point.x - b.x)) * winding;
            float ca = ((a.x - c.x) * (point.y - c.y) - (a.y - c.y) * (point.x - c.x)) * winding;
            return ab >= 0f && bc >= 0f && ca >= 0f;
        }
        #endregion
    }
}
