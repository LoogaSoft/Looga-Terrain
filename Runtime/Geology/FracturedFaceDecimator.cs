using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    // Reduces a triangle mesh by edge collapses in the order of the smallest quadric error (Garland and Heckbert 1997).
    // Each collapse moves one end of an edge onto the other end, so the vertices keep their attributes. Flat areas
    // collapse almost without error, and sharp edges and corners stay. Locked vertices do not move: the mesh boundary
    // and the vertices shared with other chunks, so neighbour chunks meet without cracks.
    internal static class FracturedFaceDecimator
    {
        // A collapse is not made when it turns a triangle normal by more than about 70 degrees.
        private const double FlipLimit = 0.35;
        // Largest number of triangles around a vertex after a collapse. Without it, flat areas collapsed into a few hub
        // vertices with hundreds of thin triangles, and each further collapse there got slower.
        private const int ValenceLimit = 24;

        private struct Candidate
        {
            public double Cost;
            public int From;
            public int To;
            public int FromVersion;
            public int ToVersion;
        }

        /// <summary>
        /// Collapses edges while the error of the cheapest collapse is below the limit (squared meters). Returns the kept
        /// triangles as indices into the input positions.
        /// </summary>
        public static List<int> Reduce(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, bool[] locked,
            double errorLimit)
        {
            int vertexCount = positions.Count;
            int triangleCount = indices.Count / 3;
            var triangles = new int[indices.Count];
            for (int index = 0; index < indices.Count; index++) triangles[index] = indices[index];
            var deadTriangle = new bool[triangleCount];
            var vertexTriangles = new List<int>[vertexCount];
            for (int vertex = 0; vertex < vertexCount; vertex++) vertexTriangles[vertex] = new List<int>(6);
            var quadrics = new double[vertexCount * 10];
            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                int a = triangles[triangle * 3];
                int b = triangles[triangle * 3 + 1];
                int c = triangles[triangle * 3 + 2];
                vertexTriangles[a].Add(triangle);
                vertexTriangles[b].Add(triangle);
                vertexTriangles[c].Add(triangle);
                Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                if (normal.sqrMagnitude < 1e-14f) continue;
                normal.Normalize();
                double distance = -Vector3.Dot(normal, positions[a]);
                AddPlane(quadrics, a, normal, distance);
                AddPlane(quadrics, b, normal, distance);
                AddPlane(quadrics, c, normal, distance);
            }
            var isLocked = (bool[])locked.Clone();
            LockOpenEdges(triangles, triangleCount, isLocked);

            var version = new int[vertexCount];
            var deadVertex = new bool[vertexCount];
            var heap = new List<Candidate>(triangleCount * 3);
            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    int a = triangles[triangle * 3 + corner];
                    int b = triangles[triangle * 3 + (corner + 1) % 3];
                    Push(heap, Make(quadrics, positions, version, a, b, isLocked));
                    Push(heap, Make(quadrics, positions, version, b, a, isLocked));
                }
            }

            var neighbours = new HashSet<int>();
            var counted = new HashSet<int>();
            var merged = new List<int>();
            while (heap.Count > 0)
            {
                Candidate candidate = Pop(heap);
                if (candidate.Cost > errorLimit) break;
                int from = candidate.From;
                int to = candidate.To;
                if (deadVertex[from] || deadVertex[to] || version[from] != candidate.FromVersion ||
                    version[to] != candidate.ToVersion)
                {
                    continue;
                }
                if (vertexTriangles[from].Count + vertexTriangles[to].Count - 4 > ValenceLimit ||
                    !CanCollapse(from, to, triangles, deadTriangle, vertexTriangles, positions, neighbours, counted))
                {
                    continue;
                }

                // Move the triangles of the removed vertex to the kept vertex. Triangles with both die. The list of
                // the kept vertex keeps only live triangles: with the dead ones, the lists of vertices in large flat
                // areas grew to thousands of entries and each collapse got slower.
                merged.Clear();
                foreach (int triangle in vertexTriangles[from])
                {
                    if (deadTriangle[triangle]) continue;
                    int start = triangle * 3;
                    if (triangles[start] == to || triangles[start + 1] == to || triangles[start + 2] == to)
                    {
                        deadTriangle[triangle] = true;
                        continue;
                    }
                    for (int corner = 0; corner < 3; corner++)
                    {
                        if (triangles[start + corner] == from) triangles[start + corner] = to;
                    }
                    merged.Add(triangle);
                }
                foreach (int triangle in vertexTriangles[to])
                {
                    if (!deadTriangle[triangle]) merged.Add(triangle);
                }
                vertexTriangles[to].Clear();
                vertexTriangles[to].AddRange(merged);
                vertexTriangles[from].Clear();
                deadVertex[from] = true;
                for (int term = 0; term < 10; term++) quadrics[to * 10 + term] += quadrics[from * 10 + term];
                version[to]++;
                // Each neighbour of the kept vertex once.
                counted.Clear();
                foreach (int triangle in vertexTriangles[to])
                {
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int other = triangles[triangle * 3 + corner];
                        if (other == to || !counted.Add(other)) continue;
                        Push(heap, Make(quadrics, positions, version, to, other, isLocked));
                        Push(heap, Make(quadrics, positions, version, other, to, isLocked));
                    }
                }
            }

            var result = new List<int>(indices.Count / 2);
            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                if (deadTriangle[triangle]) continue;
                result.Add(triangles[triangle * 3]);
                result.Add(triangles[triangle * 3 + 1]);
                result.Add(triangles[triangle * 3 + 2]);
            }
            return result;
        }

        // Vertices on an edge with one triangle (a hole or the mesh outline) stay, so the outline keeps its shape.
        private static void LockOpenEdges(int[] triangles, int triangleCount, bool[] locked)
        {
            var edges = new Dictionary<long, int>(triangleCount * 3);
            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    long key = EdgeKey(triangles[triangle * 3 + corner], triangles[triangle * 3 + (corner + 1) % 3]);
                    edges.TryGetValue(key, out int count);
                    edges[key] = count + 1;
                }
            }
            foreach (KeyValuePair<long, int> edge in edges)
            {
                if (edge.Value != 1) continue;
                locked[(int)(edge.Key >> 32)] = true;
                locked[(int)(edge.Key & 0xFFFFFFFF)] = true;
            }
        }

        private static long EdgeKey(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        // The collapse keeps the mesh a manifold (the two ends share only the vertices of their common triangles) and
        // turns no triangle over.
        private static bool CanCollapse(int from, int to, int[] triangles, bool[] deadTriangle,
            List<int>[] vertexTriangles, IReadOnlyList<Vector3> positions, HashSet<int> neighbours,
            HashSet<int> counted)
        {
            neighbours.Clear();
            int shared = 0;
            foreach (int triangle in vertexTriangles[to])
            {
                if (deadTriangle[triangle]) continue;
                for (int corner = 0; corner < 3; corner++) neighbours.Add(triangles[triangle * 3 + corner]);
            }
            var common = 0;
            counted.Clear();
            foreach (int triangle in vertexTriangles[from])
            {
                if (deadTriangle[triangle]) continue;
                int start = triangle * 3;
                bool hasTo = triangles[start] == to || triangles[start + 1] == to || triangles[start + 2] == to;
                if (hasTo) shared++;
                for (int corner = 0; corner < 3; corner++)
                {
                    int other = triangles[start + corner];
                    if (other == from || other == to || !counted.Add(other)) continue;
                    if (neighbours.Contains(other)) common++;
                }
                if (hasTo) continue;
                // The triangle after the move must face the same way.
                Vector3 a = positions[triangles[start]];
                Vector3 b = positions[triangles[start + 1]];
                Vector3 c = positions[triangles[start + 2]];
                Vector3 before = Vector3.Cross(b - a, c - a);
                if (triangles[start] == from) a = positions[to];
                if (triangles[start + 1] == from) b = positions[to];
                if (triangles[start + 2] == from) c = positions[to];
                Vector3 after = Vector3.Cross(b - a, c - a);
                if (after.sqrMagnitude < 1e-12f) return false;
                if (Vector3.Dot(before.normalized, after.normalized) < FlipLimit) return false;
            }
            return shared > 0 && common == shared;
        }

        private static Candidate Make(double[] quadrics, IReadOnlyList<Vector3> positions, int[] version, int from,
            int to, bool[] locked)
        {
            if (locked[from]) return new Candidate { Cost = double.MaxValue };
            Vector3 p = positions[to];
            int a = from * 10;
            int b = to * 10;
            double q0 = quadrics[a] + quadrics[b];
            double q1 = quadrics[a + 1] + quadrics[b + 1];
            double q2 = quadrics[a + 2] + quadrics[b + 2];
            double q3 = quadrics[a + 3] + quadrics[b + 3];
            double q4 = quadrics[a + 4] + quadrics[b + 4];
            double q5 = quadrics[a + 5] + quadrics[b + 5];
            double q6 = quadrics[a + 6] + quadrics[b + 6];
            double q7 = quadrics[a + 7] + quadrics[b + 7];
            double q8 = quadrics[a + 8] + quadrics[b + 8];
            double q9 = quadrics[a + 9] + quadrics[b + 9];
            double x = p.x;
            double y = p.y;
            double z = p.z;
            double cost = q0 * x * x + 2 * q1 * x * y + 2 * q2 * x * z + 2 * q3 * x + q4 * y * y + 2 * q5 * y * z +
                          2 * q6 * y + q7 * z * z + 2 * q8 * z + q9;
            return new Candidate
            {
                Cost = Math.Max(cost, 0.0), From = from, To = to, FromVersion = version[from], ToVersion = version[to]
            };
        }

        // Quadric terms: a², ab, ac, ad, b², bc, bd, c², cd, d² of the plane ax + by + cz + d = 0.
        private static void AddPlane(double[] quadrics, int vertex, Vector3 normal, double d)
        {
            int at = vertex * 10;
            double a = normal.x;
            double b = normal.y;
            double c = normal.z;
            quadrics[at] += a * a;
            quadrics[at + 1] += a * b;
            quadrics[at + 2] += a * c;
            quadrics[at + 3] += a * d;
            quadrics[at + 4] += b * b;
            quadrics[at + 5] += b * c;
            quadrics[at + 6] += b * d;
            quadrics[at + 7] += c * c;
            quadrics[at + 8] += c * d;
            quadrics[at + 9] += d * d;
        }

        private static void Push(List<Candidate> heap, Candidate candidate)
        {
            if (candidate.Cost == double.MaxValue) return;
            heap.Add(candidate);
            int child = heap.Count - 1;
            while (child > 0)
            {
                int parent = (child - 1) >> 1;
                if (heap[parent].Cost <= heap[child].Cost) break;
                (heap[parent], heap[child]) = (heap[child], heap[parent]);
                child = parent;
            }
        }

        private static Candidate Pop(List<Candidate> heap)
        {
            Candidate top = heap[0];
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            int parent = 0;
            while (true)
            {
                int left = parent * 2 + 1;
                if (left >= heap.Count) break;
                int right = left + 1;
                int smallest = right < heap.Count && heap[right].Cost < heap[left].Cost ? right : left;
                if (heap[parent].Cost <= heap[smallest].Cost) break;
                (heap[parent], heap[smallest]) = (heap[smallest], heap[parent]);
                parent = smallest;
            }
            return top;
        }
    }
}
