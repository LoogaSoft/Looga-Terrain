using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Grid of the world positions that are inside a spline area or near it. The stamp world gives collider copies only
    /// to the object placements in this grid. Closed splines include their inside, open splines only the distance to
    /// the line.
    /// </summary>
    internal sealed class ColliderAreaMask
    {
        // Grid cell size in meters. The margin is correct to about one cell.
        private const float CellSize = 4f;
        // Distance between two curve samples, in meters.
        private const float SampleSpacing = 2f;
        // Weight of a diagonal step in the distance transform.
        private const float Diagonal = 1.4142135f;

        private readonly List<WorldCurve> _curves = new();
        private readonly List<WorldCurve> _builtCurves = new();
        private readonly List<WorldCurve> _splineCurves = new();
        private readonly List<bool> _closed = new();
        private readonly List<bool> _builtClosed = new();
        private readonly List<int> _curveCounts = new();
        private readonly List<List<Vector2>> _polygons = new();
        private readonly List<float> _crossings = new();
        private float _builtMargin = -1f;
        private bool[] _cells = Array.Empty<bool>();
        private Vector2 _origin;
        private int _width;
        private int _height;

        /// <summary>True when the mask has no spline. Then all placements get colliders.</summary>
        internal bool IsEmpty => _width == 0;

        /// <summary>True when the position is inside the area or not more than the margin outside it.</summary>
        internal bool Contains(Vector3 position)
        {
            int x = Mathf.FloorToInt((position.x - _origin.x) / CellSize);
            int z = Mathf.FloorToInt((position.z - _origin.y) / CellSize);
            if (x < 0 || z < 0 || x >= _width || z >= _height) return false;
            return _cells[z * _width + x];
        }

        /// <summary>
        /// Builds the grid again when the splines or the margin changed. Returns true when the grid changed.
        /// </summary>
        internal bool Update(LoogaSplineArea area, float margin)
        {
            ReadCurves(area);
            if (margin == _builtMargin && SameCurves()) return false;
            _builtMargin = margin;
            _builtCurves.Clear();
            _builtCurves.AddRange(_curves);
            _builtClosed.Clear();
            _builtClosed.AddRange(_closed);
            Build(margin);
            return true;
        }

        private void ReadCurves(LoogaSplineArea area)
        {
            _curves.Clear();
            _closed.Clear();
            _curveCounts.Clear();
            SplineContainer container = area ? area.Spline : null;
            if (!container) return;
            Matrix4x4 localToWorld = container.transform.localToWorldMatrix;
            foreach (Spline spline in container.Splines)
            {
                if (spline == null || spline.Count < 2) continue;
                SplineGeometry.GetWorldCurves(spline, localToWorld, _splineCurves);
                // An open spline has a last curve from its last knot back to itself. It adds no line.
                int count = spline.Closed ? _splineCurves.Count : _splineCurves.Count - 1;
                for (int i = 0; i < count; i++)
                {
                    _curves.Add(_splineCurves[i]);
                }
                _closed.Add(spline.Closed && !area.ClosedAsPath);
                _curveCounts.Add(count);
            }
        }

        private bool SameCurves()
        {
            if (_curves.Count != _builtCurves.Count || _closed.Count != _builtClosed.Count) return false;
            for (int i = 0; i < _closed.Count; i++)
            {
                if (_closed[i] != _builtClosed[i]) return false;
            }
            for (int i = 0; i < _curves.Count; i++)
            {
                WorldCurve a = _curves[i];
                WorldCurve b = _builtCurves[i];
                if (a.P0 != b.P0 || a.P1 != b.P1 || a.P2 != b.P2 || a.P3 != b.P3) return false;
            }
            return true;
        }

        private void Build(float margin)
        {
            SamplePolygons();
            if (_polygons.Count == 0)
            {
                _cells = Array.Empty<bool>();
                _width = 0;
                _height = 0;
                return;
            }
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            foreach (List<Vector2> polygon in _polygons)
            {
                foreach (Vector2 point in polygon)
                {
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
            }
            float border = margin + CellSize;
            _origin = min - new Vector2(border, border);
            _width = Mathf.CeilToInt((max.x - min.x + 2f * border) / CellSize);
            _height = Mathf.CeilToInt((max.y - min.y + 2f * border) / CellSize);
            var distances = new float[_width * _height];
            Array.Fill(distances, float.MaxValue);
            for (int i = 0; i < _polygons.Count; i++)
            {
                if (_closed[i])
                {
                    FillInside(_polygons[i], distances);
                }
                MarkLine(_polygons[i], _closed[i], distances);
            }
            DistanceTransform(distances);
            _cells = new bool[distances.Length];
            float limit = margin / CellSize;
            for (int i = 0; i < distances.Length; i++)
            {
                _cells[i] = distances[i] <= limit;
            }
        }

        // Points along each spline in world XZ.
        private void SamplePolygons()
        {
            foreach (List<Vector2> polygon in _polygons)
            {
                polygon.Clear();
            }
            while (_polygons.Count < _curveCounts.Count)
            {
                _polygons.Add(new List<Vector2>());
            }
            _polygons.RemoveRange(_curveCounts.Count, _polygons.Count - _curveCounts.Count);
            int first = 0;
            for (int s = 0; s < _curveCounts.Count; s++)
            {
                List<Vector2> polygon = _polygons[s];
                for (int c = first; c < first + _curveCounts[s]; c++)
                {
                    WorldCurve curve = _curves[c];
                    var bezier = new BezierCurve(curve.P0, curve.P1, curve.P2, curve.P3);
                    float length = Vector3.Distance(curve.P0, curve.P1) + Vector3.Distance(curve.P1, curve.P2) +
                        Vector3.Distance(curve.P2, curve.P3);
                    int samples = Mathf.Max(1, Mathf.CeilToInt(length / SampleSpacing));
                    for (int i = 0; i < samples; i++)
                    {
                        Vector3 point = CurveUtility.EvaluatePosition(bezier, i / (float)samples);
                        polygon.Add(new Vector2(point.x, point.z));
                    }
                }
                WorldCurve last = _curves[first + _curveCounts[s] - 1];
                polygon.Add(new Vector2(last.P3.x, last.P3.z));
                first += _curveCounts[s];
            }
        }

        // Sets the cells inside a closed polygon to distance 0, with the even-odd rule along each cell row.
        private void FillInside(List<Vector2> polygon, float[] distances)
        {
            for (int z = 0; z < _height; z++)
            {
                float rowZ = _origin.y + (z + 0.5f) * CellSize;
                _crossings.Clear();
                for (int i = 0; i < polygon.Count; i++)
                {
                    Vector2 a = polygon[i];
                    Vector2 b = polygon[(i + 1) % polygon.Count];
                    if ((a.y <= rowZ) == (b.y <= rowZ)) continue;
                    _crossings.Add(a.x + (rowZ - a.y) / (b.y - a.y) * (b.x - a.x));
                }
                _crossings.Sort();
                for (int i = 0; i + 1 < _crossings.Count; i += 2)
                {
                    int start = Mathf.Max(0, Mathf.CeilToInt((_crossings[i] - _origin.x) / CellSize - 0.5f));
                    int end = Mathf.Min(_width - 1,
                        Mathf.FloorToInt((_crossings[i + 1] - _origin.x) / CellSize - 0.5f));
                    for (int x = start; x <= end; x++)
                    {
                        distances[z * _width + x] = 0f;
                    }
                }
            }
        }

        // Sets the cells under the line of a polygon to distance 0.
        private void MarkLine(List<Vector2> polygon, bool closed, float[] distances)
        {
            int segments = closed ? polygon.Count : polygon.Count - 1;
            for (int i = 0; i < segments; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / (CellSize * 0.5f)));
                for (int step = 0; step <= steps; step++)
                {
                    Vector2 point = Vector2.Lerp(a, b, step / (float)steps);
                    int x = Mathf.FloorToInt((point.x - _origin.x) / CellSize);
                    int z = Mathf.FloorToInt((point.y - _origin.y) / CellSize);
                    if (x < 0 || z < 0 || x >= _width || z >= _height) continue;
                    distances[z * _width + x] = 0f;
                }
            }
        }

        // Two-pass chamfer distance transform, in cells.
        private void DistanceTransform(float[] distances)
        {
            for (int z = 0; z < _height; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int i = z * _width + x;
                    float d = Mathf.Min(distances[i], Neighbor(distances, x - 1, z, 1f));
                    d = Mathf.Min(d, Neighbor(distances, x, z - 1, 1f));
                    d = Mathf.Min(d, Neighbor(distances, x - 1, z - 1, Diagonal));
                    distances[i] = Mathf.Min(d, Neighbor(distances, x + 1, z - 1, Diagonal));
                }
            }
            for (int z = _height - 1; z >= 0; z--)
            {
                for (int x = _width - 1; x >= 0; x--)
                {
                    int i = z * _width + x;
                    float d = Mathf.Min(distances[i], Neighbor(distances, x + 1, z, 1f));
                    d = Mathf.Min(d, Neighbor(distances, x, z + 1, 1f));
                    d = Mathf.Min(d, Neighbor(distances, x + 1, z + 1, Diagonal));
                    distances[i] = Mathf.Min(d, Neighbor(distances, x - 1, z + 1, Diagonal));
                }
            }
        }

        // Distance of a neighbor cell plus the step to it. A cell outside the grid is infinitely far.
        private float Neighbor(float[] distances, int x, int z, float step)
        {
            if (x < 0 || z < 0 || x >= _width || z >= _height) return float.MaxValue;
            return distances[z * _width + x] + step;
        }
    }
}
