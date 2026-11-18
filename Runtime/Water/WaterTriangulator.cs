using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Constrained Delaunay triangulation of closed outlines on the XZ plane. The outline segments stay mesh edges, so
    /// the mesh boundary is the outline. Interior points on a world-aligned grid fill the inside. The even-odd rule
    /// decides inside: an outline inside another outline is a hole, for example an island.
    /// </summary>
    /// <remarks>
    /// Points go in one by one: a walk finds the triangle, the triangle splits, and edge flips restore the Delaunay
    /// property. Flips never remove an outline edge. An outline segment that is not an edge yet gets its midpoint
    /// inserted until both halves are edges. The same input always gives the same mesh.
    /// </remarks>
    internal sealed class WaterTriangulator
    {
        // Interior points keep this fraction of the spacing from the outline, so no thin triangles touch it.
        private const float InteriorClearance = 0.6f;
        private const int MaxSplitDepth = 12;
        private const double CoincidentDistance = 1e-4;
        // Detail levels. The mask marks where the fine points stay.
        private const float MaskCell = 32f;
        private const int MaskPointStride = 6;
        private const float LevelScale = 4f;
        private const float MaxLevelScale = 256f;
        private const float FirstLevelMargin = 1500f;

        private readonly List<double> _x = new();
        private readonly List<double> _y = new();
        private readonly List<int> _vertexTriangle = new();
        private readonly List<int> _vertices = new();
        private readonly List<int> _neighbours = new();
        private readonly List<bool> _constrained = new();
        private readonly Stack<int> _legalize = new();
        private readonly WaterSegmentGrid _clearance = new();
        private readonly List<float> _crossings = new();
        private double _originX;
        private double _originY;
        private int _last;
        private uint _random = 1;
        private Rect _detailArea;
        private float _fineSpacing;
        private bool[] _mask;
        private int _maskWidth;
        private int _maskDepth;

        /// <summary>Triangulates the outlines. Vertices are world XZ. Triangles wind clockwise seen from above.</summary>
        /// <param name="interiorSpacing">Distance between interior points, in metres. 0 = outline points only.</param>
        /// <param name="detailArea">
        /// Width above 0: the fine points stay in this world rectangle, within the reach of an outline. Points outside
        /// it get coarser with distance, so that a very large area stays a small mesh.
        /// </param>
        /// <param name="detailReach">Distance from an outline in which points stay fine, in metres.</param>
        internal void Triangulate(IReadOnlyList<List<Vector2>> outlines, float interiorSpacing, Rect detailArea,
            float detailReach, List<Vector2> vertices, List<int> triangles)
        {
            vertices.Clear();
            triangles.Clear();
            Rect bounds = GetBounds(outlines);
            if (bounds.width <= 0f || bounds.height <= 0f) return;

            Begin(bounds);
            var segments = new List<(int Start, int End, int Depth)>();
            foreach (List<Vector2> outline in outlines)
            {
                if (outline.Count < 3) continue;

                int first = -1;
                int previous = -1;
                foreach (Vector2 point in outline)
                {
                    int vertex = Insert(point.x, point.y);
                    if (first < 0) first = vertex;
                    if (previous >= 0 && previous != vertex) segments.Add((previous, vertex, 0));
                    previous = vertex;
                }
                if (previous != first) segments.Add((previous, first, 0));
            }
            RecoverSegments(segments);
            if (interiorSpacing > 0f)
            {
                if (detailArea.width > 0f) InsertLevels(outlines, bounds, interiorSpacing, detailArea, detailReach);
                else InsertInterior(outlines, bounds, interiorSpacing, null);
            }
            Output(vertices, triangles);
        }

        #region Setup
        private void Begin(Rect bounds)
        {
            _x.Clear();
            _y.Clear();
            _vertexTriangle.Clear();
            _vertices.Clear();
            _neighbours.Clear();
            _constrained.Clear();
            _legalize.Clear();
            _random = 1;
            // Coordinates are relative to the bounds centre, so the predicates keep their precision.
            _originX = bounds.center.x;
            _originY = bounds.center.y;
            double size = Math.Max(bounds.width, bounds.height) * 20.0;
            AddVertex(-size, -size);
            AddVertex(size, -size);
            AddVertex(0.0, size);
            AddTriangle(0, 1, 2, -1, -1, -1);
            _last = 0;
        }

        private static Rect GetBounds(IReadOnlyList<List<Vector2>> outlines)
        {
            Vector2 min = new(float.MaxValue, float.MaxValue);
            Vector2 max = new(float.MinValue, float.MinValue);
            foreach (List<Vector2> outline in outlines)
            {
                if (outline.Count < 3) continue;

                foreach (Vector2 point in outline)
                {
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
            }
            return min.x > max.x ? default : Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }
        #endregion

        #region Insertion
        // Inserts a point and returns its vertex. A point on an existing vertex returns that vertex.
        private int Insert(double worldX, double worldY)
        {
            double x = worldX - _originX;
            double y = worldY - _originY;
            int triangle = Locate(x, y);
            int edge = -1;
            for (int index = 0; index < 3; index++)
            {
                int vertex = _vertices[triangle * 3 + index];
                if (Math.Abs(_x[vertex] - x) < CoincidentDistance && Math.Abs(_y[vertex] - y) < CoincidentDistance)
                {
                    return vertex;
                }
            }
            for (int index = 0; index < 3; index++)
            {
                int a = _vertices[triangle * 3 + index];
                int b = _vertices[triangle * 3 + (index + 1) % 3];
                double length = Math.Sqrt(Square(_x[b] - _x[a]) + Square(_y[b] - _y[a]));
                if (Math.Abs(Orient(a, b, x, y)) <= length * 1e-9) edge = index;
            }

            int point = AddVertex(x, y);
            if (edge >= 0 && _neighbours[triangle * 3 + edge] >= 0)
            {
                SplitEdge(triangle, edge, point);
            }
            else
            {
                SplitTriangle(triangle, point);
            }
            while (_legalize.Count > 0)
            {
                int item = _legalize.Pop();
                Legalize(item / 3, item % 3);
            }
            return point;
        }

        // Walks from the last triangle toward the point. A random start edge stops endless loops.
        private int Locate(double x, double y)
        {
            int triangle = _last;
            int limit = _vertices.Count;
            for (int step = 0; step < limit; step++)
            {
                _random = _random * 1664525u + 1013904223u;
                int start = (int)(_random >> 16) % 3;
                bool moved = false;
                for (int offset = 0; offset < 3; offset++)
                {
                    int index = (start + offset) % 3;
                    int a = _vertices[triangle * 3 + index];
                    int b = _vertices[triangle * 3 + (index + 1) % 3];
                    if (Orient(a, b, x, y) >= 0.0) continue;

                    int next = _neighbours[triangle * 3 + index];
                    if (next < 0) continue;

                    triangle = next;
                    moved = true;
                    break;
                }
                if (!moved) break;
            }
            _last = triangle;
            return triangle;
        }

        private void SplitTriangle(int triangle, int point)
        {
            int baseIndex = triangle * 3;
            int a = _vertices[baseIndex];
            int b = _vertices[baseIndex + 1];
            int c = _vertices[baseIndex + 2];
            int nab = _neighbours[baseIndex];
            int nbc = _neighbours[baseIndex + 1];
            int nca = _neighbours[baseIndex + 2];
            bool cab = _constrained[baseIndex];
            bool cbc = _constrained[baseIndex + 1];
            bool cca = _constrained[baseIndex + 2];

            int second = AddTriangle(b, c, point, nbc, -1, triangle);
            int third = AddTriangle(c, a, point, nca, triangle, second);
            SetTriangle(triangle, a, b, point, nab, second, third);
            _neighbours[second * 3 + 1] = third;
            _constrained[baseIndex] = cab;
            _constrained[baseIndex + 1] = false;
            _constrained[baseIndex + 2] = false;
            _constrained[second * 3] = cbc;
            _constrained[third * 3] = cca;
            ReplaceNeighbour(nbc, triangle, second);
            ReplaceNeighbour(nca, triangle, third);
            _legalize.Push(triangle * 3);
            _legalize.Push(second * 3);
            _legalize.Push(third * 3);
        }

        // The point lies on edge (a, b) of the triangle. Both triangles beside the edge split in two.
        private void SplitEdge(int triangle, int edge, int point)
        {
            int a = _vertices[triangle * 3 + edge];
            int b = _vertices[triangle * 3 + (edge + 1) % 3];
            int c = _vertices[triangle * 3 + (edge + 2) % 3];
            int nbc = _neighbours[triangle * 3 + (edge + 1) % 3];
            int nca = _neighbours[triangle * 3 + (edge + 2) % 3];
            bool cbc = _constrained[triangle * 3 + (edge + 1) % 3];
            bool cca = _constrained[triangle * 3 + (edge + 2) % 3];
            bool cab = _constrained[triangle * 3 + edge];

            int other = _neighbours[triangle * 3 + edge];
            int otherEdge = EdgeTo(other, triangle);
            int d = _vertices[other * 3 + (otherEdge + 2) % 3];
            int nad = _neighbours[other * 3 + (otherEdge + 1) % 3];
            int ndb = _neighbours[other * 3 + (otherEdge + 2) % 3];
            bool cad = _constrained[other * 3 + (otherEdge + 1) % 3];
            bool cdb = _constrained[other * 3 + (otherEdge + 2) % 3];

            // Triangle (a, b, c) becomes (a, p, c) and (p, b, c). Other (b, a, d) becomes (b, p, d) and (p, a, d).
            int pbc = AddTriangle(point, b, c, -1, nbc, -1);
            int pad = AddTriangle(point, a, d, -1, nad, -1);
            SetTriangle(triangle, a, point, c, pad, pbc, nca);
            SetTriangle(other, b, point, d, pbc, pad, ndb);
            _neighbours[pbc * 3] = other;
            _neighbours[pbc * 3 + 2] = triangle;
            _neighbours[pad * 3] = triangle;
            _neighbours[pad * 3 + 2] = other;
            ReplaceNeighbour(nbc, triangle, pbc);
            ReplaceNeighbour(nad, other, pad);

            _constrained[triangle * 3] = cab;
            _constrained[triangle * 3 + 1] = false;
            _constrained[triangle * 3 + 2] = cca;
            _constrained[pbc * 3] = cab;
            _constrained[pbc * 3 + 1] = cbc;
            _constrained[other * 3] = cab;
            _constrained[other * 3 + 1] = false;
            _constrained[other * 3 + 2] = cdb;
            _constrained[pad * 3] = cab;
            _constrained[pad * 3 + 1] = cad;

            _legalize.Push(triangle * 3 + 2);
            _legalize.Push(pbc * 3 + 1);
            _legalize.Push(other * 3 + 2);
            _legalize.Push(pad * 3 + 1);
        }

        // Flips the edge when the far vertex lies inside the circumcircle. Outline edges never flip.
        private void Legalize(int triangle, int edge)
        {
            int index = triangle * 3 + edge;
            int other = _neighbours[index];
            if (other < 0 || _constrained[index]) return;

            int a = _vertices[index];
            int b = _vertices[triangle * 3 + (edge + 1) % 3];
            int p = _vertices[triangle * 3 + (edge + 2) % 3];
            int otherEdge = EdgeTo(other, triangle);
            int q = _vertices[other * 3 + (otherEdge + 2) % 3];
            if (InCircle(a, b, p, q) <= 0.0) return;

            int nbp = _neighbours[triangle * 3 + (edge + 1) % 3];
            int npa = _neighbours[triangle * 3 + (edge + 2) % 3];
            bool cbp = _constrained[triangle * 3 + (edge + 1) % 3];
            bool cpa = _constrained[triangle * 3 + (edge + 2) % 3];
            int naq = _neighbours[other * 3 + (otherEdge + 1) % 3];
            int nqb = _neighbours[other * 3 + (otherEdge + 2) % 3];
            bool caq = _constrained[other * 3 + (otherEdge + 1) % 3];
            bool cqb = _constrained[other * 3 + (otherEdge + 2) % 3];

            // The quad (a, q, b, p) gets the diagonal (p, q): triangles (p, a, q) and (p, q, b).
            SetTriangle(triangle, p, a, q, npa, naq, other);
            SetTriangle(other, p, q, b, triangle, nqb, nbp);
            _constrained[triangle * 3] = cpa;
            _constrained[triangle * 3 + 1] = caq;
            _constrained[triangle * 3 + 2] = false;
            _constrained[other * 3] = false;
            _constrained[other * 3 + 1] = cqb;
            _constrained[other * 3 + 2] = cbp;
            ReplaceNeighbour(naq, other, triangle);
            ReplaceNeighbour(nbp, triangle, other);
            _legalize.Push(triangle * 3 + 1);
            _legalize.Push(other * 3 + 1);
        }
        #endregion

        #region Outline
        // Marks each outline segment as a fixed edge. A missing segment gets its midpoint until its halves are edges.
        private void RecoverSegments(List<(int Start, int End, int Depth)> segments)
        {
            var queue = new Queue<(int Start, int End, int Depth)>(segments);
            while (queue.Count > 0)
            {
                (int start, int end, int depth) = queue.Dequeue();
                if (start == end) continue;
                if (TryFindEdge(start, end, out int triangle, out int edge))
                {
                    SetConstrained(triangle, edge);
                    continue;
                }
                if (depth >= MaxSplitDepth) continue;

                double middleX = (_x[start] + _x[end]) * 0.5 + _originX;
                double middleY = (_y[start] + _y[end]) * 0.5 + _originY;
                int middle = Insert(middleX, middleY);
                if (middle == start || middle == end) continue;

                queue.Enqueue((start, middle, depth + 1));
                queue.Enqueue((middle, end, depth + 1));
            }
        }

        // Interior points on a world-aligned grid. Rows find the inside spans with the even-odd rule.
        #region Detail Levels
        // Fine points near the outlines of the detail area. Then each level has four times the spacing and four times
        // the margin around the detail area, up to the bounds.
        private void InsertLevels(IReadOnlyList<List<Vector2>> outlines, Rect bounds, float spacing, Rect area, float reach)
        {
            _detailArea = area;
            _fineSpacing = spacing;
            BuildDetailMask(outlines, area, reach);
            InsertInterior(outlines, Overlap(bounds, area), spacing, IsNearOutline);

            float levelSpacing = spacing;
            float margin = FirstLevelMargin;
            Rect inner = area;
            while (true)
            {
                levelSpacing *= LevelScale;
                Rect outer = Expand(area, margin);
                Rect previous = inner;
                bool first = inner == area;
                bool last = outer.Contains(bounds.min) && outer.Contains(bounds.max);
                InsertInterior(outlines, last ? bounds : Overlap(bounds, outer), levelSpacing,
                    point => !IsNearOutline(point) && (first || !previous.Contains(point)));
                if (last) break;

                inner = outer;
                margin *= LevelScale;
            }
        }

        /// <summary>The distance between the points around a position. Call it after a triangulation with levels.</summary>
        internal float SpacingAt(Vector2 point)
        {
            if (IsNearOutline(point)) return _fineSpacing;

            float spacing = _fineSpacing * LevelScale;
            float margin = FirstLevelMargin;
            while (spacing < _fineSpacing * MaxLevelScale && !Expand(_detailArea, margin).Contains(point))
            {
                spacing *= LevelScale;
                margin *= LevelScale;
            }
            return spacing;
        }

        // Marks the cells of the detail area within the reach of an outline.
        private void BuildDetailMask(IReadOnlyList<List<Vector2>> outlines, Rect area, float reach)
        {
            _maskWidth = Mathf.CeilToInt(area.width / MaskCell);
            _maskDepth = Mathf.CeilToInt(area.height / MaskCell);
            _mask = new bool[_maskWidth * _maskDepth];
            int cells = Mathf.CeilToInt(reach / MaskCell);
            Rect reachArea = Expand(area, reach);
            int stride = 0;
            foreach (List<Vector2> outline in outlines)
            {
                foreach (Vector2 point in outline)
                {
                    if (++stride % MaskPointStride != 0 || !reachArea.Contains(point)) continue;

                    int cellX = Mathf.FloorToInt((point.x - area.xMin) / MaskCell);
                    int cellZ = Mathf.FloorToInt((point.y - area.yMin) / MaskCell);
                    for (int z = Mathf.Max(0, cellZ - cells); z <= Mathf.Min(_maskDepth - 1, cellZ + cells); z++)
                    {
                        for (int x = Mathf.Max(0, cellX - cells); x <= Mathf.Min(_maskWidth - 1, cellX + cells); x++)
                        {
                            int offsetX = x - cellX;
                            int offsetZ = z - cellZ;
                            if (offsetX * offsetX + offsetZ * offsetZ <= cells * cells) _mask[z * _maskWidth + x] = true;
                        }
                    }
                }
            }
        }

        private bool IsNearOutline(Vector2 point)
        {
            int x = Mathf.FloorToInt((point.x - _detailArea.xMin) / MaskCell);
            int z = Mathf.FloorToInt((point.y - _detailArea.yMin) / MaskCell);
            return x >= 0 && z >= 0 && x < _maskWidth && z < _maskDepth && _mask[z * _maskWidth + x];
        }

        private static Rect Expand(Rect rect, float margin)
        {
            return Rect.MinMaxRect(rect.xMin - margin, rect.yMin - margin, rect.xMax + margin, rect.yMax + margin);
        }

        private static Rect Overlap(Rect a, Rect b)
        {
            return Rect.MinMaxRect(Mathf.Max(a.xMin, b.xMin), Mathf.Max(a.yMin, b.yMin), Mathf.Min(a.xMax, b.xMax),
                Mathf.Min(a.yMax, b.yMax));
        }
        #endregion

        private void InsertInterior(IReadOnlyList<List<Vector2>> outlines, Rect bounds, float spacing,
            Func<Vector2, bool> keep)
        {
            float clearance = spacing * InteriorClearance;
            _clearance.Clear(spacing, clearance);
            foreach (List<Vector2> outline in outlines)
            {
                if (outline.Count >= 3) _clearance.AddPolyline(outline, true);
            }

            int rowMin = Mathf.CeilToInt(bounds.yMin / spacing);
            int rowMax = Mathf.FloorToInt(bounds.yMax / spacing);
            for (int row = rowMin; row <= rowMax; row++)
            {
                float z = row * spacing;
                FindCrossings(outlines, z);
                // Rows go back and forth, so each walk starts next to its point.
                bool reverse = (row & 1) == 1;
                for (int pair = 0; pair + 1 < _crossings.Count; pair += 2)
                {
                    int span = reverse ? _crossings.Count - 2 - pair : pair;
                    int columnMin = Mathf.CeilToInt(_crossings[span] / spacing);
                    int columnMax = Mathf.FloorToInt(_crossings[span + 1] / spacing);
                    for (int step = 0; step <= columnMax - columnMin; step++)
                    {
                        int column = reverse ? columnMax - step : columnMin + step;
                        Vector2 point = new(column * spacing, z);
                        if (keep != null && !keep(point)) continue;
                        if (_clearance.TryNearest(point, out _, out _, out _)) continue;

                        Insert(point.x, point.y);
                    }
                }
            }
        }

        private void FindCrossings(IReadOnlyList<List<Vector2>> outlines, float z)
        {
            _crossings.Clear();
            foreach (List<Vector2> outline in outlines)
            {
                if (outline.Count < 3) continue;

                for (int index = 0; index < outline.Count; index++)
                {
                    Vector2 a = outline[index];
                    Vector2 b = outline[(index + 1) % outline.Count];
                    // Half-open rule: a vertex on the row counts once.
                    if ((a.y > z) == (b.y > z)) continue;

                    _crossings.Add(a.x + (z - a.y) / (b.y - a.y) * (b.x - a.x));
                }
            }
            _crossings.Sort();
        }
        #endregion

        #region Output
        // Keeps the triangles inside the outlines. Crossing an outline edge toggles inside and outside.
        private void Output(List<Vector2> vertices, List<int> triangles)
        {
            int triangleCount = _vertices.Count / 3;
            var parity = new int[triangleCount];
            Array.Fill(parity, -1);
            var queue = new Queue<int>();
            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                if (!TouchesSuperVertex(triangle)) continue;

                parity[triangle] = 0;
                queue.Enqueue(triangle);
            }
            while (queue.Count > 0)
            {
                int triangle = queue.Dequeue();
                for (int edge = 0; edge < 3; edge++)
                {
                    int next = _neighbours[triangle * 3 + edge];
                    if (next < 0 || parity[next] >= 0) continue;

                    parity[next] = parity[triangle] ^ (_constrained[triangle * 3 + edge] ? 1 : 0);
                    queue.Enqueue(next);
                }
            }

            var remap = new int[_x.Count];
            Array.Fill(remap, -1);
            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                if (parity[triangle] != 1) continue;

                // Counter-clockwise on the XZ plane is clockwise seen from above, the Unity front face.
                for (int corner = 2; corner >= 0; corner--)
                {
                    int vertex = _vertices[triangle * 3 + corner];
                    if (remap[vertex] < 0)
                    {
                        remap[vertex] = vertices.Count;
                        vertices.Add(new Vector2((float)(_x[vertex] + _originX), (float)(_y[vertex] + _originY)));
                    }
                    triangles.Add(remap[vertex]);
                }
            }
        }

        private bool TouchesSuperVertex(int triangle)
        {
            return _vertices[triangle * 3] < 3 || _vertices[triangle * 3 + 1] < 3 || _vertices[triangle * 3 + 2] < 3;
        }
        #endregion

        #region Topology
        private int AddVertex(double x, double y)
        {
            _x.Add(x);
            _y.Add(y);
            _vertexTriangle.Add(-1);
            return _x.Count - 1;
        }

        private int AddTriangle(int a, int b, int c, int nab, int nbc, int nca)
        {
            int triangle = _vertices.Count / 3;
            _vertices.Add(0);
            _vertices.Add(0);
            _vertices.Add(0);
            _neighbours.Add(-1);
            _neighbours.Add(-1);
            _neighbours.Add(-1);
            _constrained.Add(false);
            _constrained.Add(false);
            _constrained.Add(false);
            SetTriangle(triangle, a, b, c, nab, nbc, nca);
            return triangle;
        }

        private void SetTriangle(int triangle, int a, int b, int c, int nab, int nbc, int nca)
        {
            int baseIndex = triangle * 3;
            _vertices[baseIndex] = a;
            _vertices[baseIndex + 1] = b;
            _vertices[baseIndex + 2] = c;
            _neighbours[baseIndex] = nab;
            _neighbours[baseIndex + 1] = nbc;
            _neighbours[baseIndex + 2] = nca;
            _vertexTriangle[a] = triangle;
            _vertexTriangle[b] = triangle;
            _vertexTriangle[c] = triangle;
        }

        private void ReplaceNeighbour(int triangle, int previous, int next)
        {
            if (triangle < 0) return;

            for (int edge = 0; edge < 3; edge++)
            {
                if (_neighbours[triangle * 3 + edge] != previous) continue;

                _neighbours[triangle * 3 + edge] = next;
                return;
            }
        }

        private int EdgeTo(int triangle, int neighbour)
        {
            for (int edge = 0; edge < 3; edge++)
            {
                if (_neighbours[triangle * 3 + edge] == neighbour) return edge;
            }
            throw new InvalidOperationException("Water triangulation lost a neighbour link.");
        }

        private void SetConstrained(int triangle, int edge)
        {
            _constrained[triangle * 3 + edge] = true;
            int other = _neighbours[triangle * 3 + edge];
            if (other >= 0) _constrained[other * 3 + EdgeTo(other, triangle)] = true;
        }

        // Turns around the start vertex to find the edge (start, end).
        private bool TryFindEdge(int start, int end, out int found, out int foundEdge)
        {
            found = -1;
            foundEdge = -1;
            int first = _vertexTriangle[start];
            int triangle = first;
            for (int guard = 0; guard < 1024 && triangle >= 0; guard++)
            {
                int corner = Corner(triangle, start);
                if (corner < 0) return false;
                if (_vertices[triangle * 3 + (corner + 1) % 3] == end)
                {
                    found = triangle;
                    foundEdge = corner;
                    return true;
                }
                if (_vertices[triangle * 3 + (corner + 2) % 3] == end)
                {
                    found = triangle;
                    foundEdge = (corner + 2) % 3;
                    return true;
                }
                // The edge (c, start) leads to the next triangle around the start vertex.
                triangle = _neighbours[triangle * 3 + (corner + 2) % 3];
                if (triangle == first) return false;
            }
            return false;
        }

        private int Corner(int triangle, int vertex)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                if (_vertices[triangle * 3 + corner] == vertex) return corner;
            }
            return -1;
        }
        #endregion

        #region Predicates
        // Positive when the point is left of the line from a to b.
        private double Orient(int a, int b, double x, double y)
        {
            return (_x[b] - _x[a]) * (y - _y[a]) - (_y[b] - _y[a]) * (x - _x[a]);
        }

        // Positive when d lies inside the circumcircle of the counter-clockwise triangle (a, b, c).
        private double InCircle(int a, int b, int c, int d)
        {
            double adx = _x[a] - _x[d];
            double ady = _y[a] - _y[d];
            double bdx = _x[b] - _x[d];
            double bdy = _y[b] - _y[d];
            double cdx = _x[c] - _x[d];
            double cdy = _y[c] - _y[d];
            double ad = adx * adx + ady * ady;
            double bd = bdx * bdx + bdy * bdy;
            double cd = cdx * cdx + cdy * cdy;
            double determinant = adx * (bdy * cd - bd * cdy) - ady * (bdx * cd - bd * cdx) + ad * (bdx * cdy - bdy * cdx);
            // Points on the circle do not flip. Grids have many of them.
            double scale = (ad + bd + cd) * (ad + bd + cd);
            return determinant > scale * 1e-12 ? determinant : 0.0;
        }

        private static double Square(double value)
        {
            return value * value;
        }
        #endregion
    }
}
