using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Line segments on the world XZ plane, sorted into square cells for fast distance queries near the segments.
    /// A query finds a segment only within the reach that the grid was built for.
    /// </summary>
    internal sealed class WaterSegmentGrid
    {
        private readonly List<Vector2> _starts = new();
        private readonly List<Vector2> _ends = new();
        private readonly Dictionary<long, List<int>> _cells = new();
        private float _cellSize = 1f;
        private float _reach;

        internal int SegmentCount => _starts.Count;

        internal void Clear(float cellSize, float reach)
        {
            _starts.Clear();
            _ends.Clear();
            _cells.Clear();
            _cellSize = Mathf.Max(cellSize, 0.1f);
            _reach = Mathf.Max(reach, 0f);
        }

        /// <summary>Adds the segments of a polyline. A closed polyline also gets the segment from its end to its start.</summary>
        internal void AddPolyline(IReadOnlyList<Vector2> points, bool closed)
        {
            int count = closed ? points.Count : points.Count - 1;
            for (int index = 0; index < count; index++)
            {
                Add(points[index], points[(index + 1) % points.Count]);
            }
        }

        internal void Add(Vector2 start, Vector2 end)
        {
            int segment = _starts.Count;
            _starts.Add(start);
            _ends.Add(end);
            int xMin = Cell(Mathf.Min(start.x, end.x) - _reach);
            int xMax = Cell(Mathf.Max(start.x, end.x) + _reach);
            int yMin = Cell(Mathf.Min(start.y, end.y) - _reach);
            int yMax = Cell(Mathf.Max(start.y, end.y) + _reach);
            for (int y = yMin; y <= yMax; y++)
            {
                for (int x = xMin; x <= xMax; x++)
                {
                    long key = Key(x, y);
                    if (!_cells.TryGetValue(key, out List<int> list))
                    {
                        list = new List<int>(4);
                        _cells.Add(key, list);
                    }
                    list.Add(segment);
                }
            }
        }

        /// <summary>
        /// Finds the nearest segment within the reach. Returns false when no segment is in reach.
        /// </summary>
        /// <param name="along">Fraction along the nearest segment, from 0 at its start to 1 at its end.</param>
        internal bool TryNearest(Vector2 point, out float distance, out int segment, out float along)
        {
            distance = float.MaxValue;
            segment = -1;
            along = 0f;
            if (!_cells.TryGetValue(Key(Cell(point.x), Cell(point.y)), out List<int> list)) return false;

            float best = _reach * _reach;
            foreach (int index in list)
            {
                Vector2 start = _starts[index];
                Vector2 offset = _ends[index] - start;
                float lengthSquared = offset.sqrMagnitude;
                float t = lengthSquared > 1e-12f ? Mathf.Clamp01(Vector2.Dot(point - start, offset) / lengthSquared) : 0f;
                float squared = (start + offset * t - point).sqrMagnitude;
                if (squared > best) continue;

                best = squared;
                segment = index;
                along = t;
            }
            if (segment < 0) return false;

            distance = Mathf.Sqrt(best);
            return true;
        }

        internal Vector2 Direction(int segment)
        {
            return (_ends[segment] - _starts[segment]).normalized;
        }

        /// <summary>The point at a fraction of a segment, as <see cref="TryNearest"/> gives it.</summary>
        internal Vector2 Point(int segment, float along)
        {
            return Vector2.Lerp(_starts[segment], _ends[segment], along);
        }

        private int Cell(float value)
        {
            return Mathf.FloorToInt(value / _cellSize);
        }

        private static long Key(int x, int y)
        {
            return ((long)x << 32) ^ (uint)y;
        }
    }
}
