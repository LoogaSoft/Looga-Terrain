using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Reads the vertex data of a water body mesh at world points: UV0 (flow, alpha, vertex spacing) and UV1 (regime),
    /// interpolated in the triangle under the point. A fall sheet takes these values on its weld line, so the body and
    /// the sheet have the same vertex data where they meet.
    /// </summary>
    internal sealed class WaterSurfaceSampler
    {
        private readonly List<Vector3> _vertices = new();
        private readonly List<Vector4> _flow = new();
        private readonly List<Vector2> _regime = new();
        private readonly List<int> _triangles = new();
        // Triangles that touch the area of interest, as world XZ corners and vertex indices.
        private readonly List<Vector2> _corners = new();
        private readonly List<int> _indices = new();

        /// <summary>
        /// Prepares the triangles of the body mesh that touch a world XZ area. Returns false without a mesh or without
        /// flow data.
        /// </summary>
        public bool Prepare(MeshFilter meshFilter, Rect area)
        {
            _corners.Clear();
            _indices.Clear();
            Mesh mesh = meshFilter ? meshFilter.sharedMesh : null;
            if (!mesh || mesh.vertexCount == 0) return false;

            mesh.GetVertices(_vertices);
            mesh.GetUVs(0, _flow);
            mesh.GetUVs(1, _regime);
            if (_flow.Count != _vertices.Count) return false;

            Matrix4x4 localToWorld = meshFilter.transform.localToWorldMatrix;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                mesh.GetTriangles(_triangles, subMesh);
                for (int index = 0; index + 2 < _triangles.Count; index += 3)
                {
                    Vector3 a = localToWorld.MultiplyPoint3x4(_vertices[_triangles[index]]);
                    Vector3 b = localToWorld.MultiplyPoint3x4(_vertices[_triangles[index + 1]]);
                    Vector3 c = localToWorld.MultiplyPoint3x4(_vertices[_triangles[index + 2]]);
                    float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
                    float maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
                    float minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z));
                    float maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
                    if (maxX < area.xMin || minX > area.xMax || maxZ < area.yMin || minZ > area.yMax) continue;

                    _corners.Add(new Vector2(a.x, a.z));
                    _corners.Add(new Vector2(b.x, b.z));
                    _corners.Add(new Vector2(c.x, c.z));
                    _indices.Add(_triangles[index]);
                    _indices.Add(_triangles[index + 1]);
                    _indices.Add(_triangles[index + 2]);
                }
            }
            return _indices.Count > 0;
        }

        /// <summary>The vertex data at a world point. False where no prepared triangle lies under the point.</summary>
        public bool TrySample(Vector3 world, out Vector4 flow, out Vector2 regime)
        {
            var point = new Vector2(world.x, world.z);
            // A point on an edge between two triangles belongs to both. A small tolerance keeps points on the outer
            // edge of the mesh.
            const float tolerance = 1e-3f;
            for (int index = 0; index + 2 < _indices.Count; index += 3)
            {
                Vector2 a = _corners[index];
                Vector2 b = _corners[index + 1];
                Vector2 c = _corners[index + 2];
                float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                if (Mathf.Abs(area) < 1e-8f) continue;

                float u = ((b.x - point.x) * (c.y - point.y) - (c.x - point.x) * (b.y - point.y)) / area;
                float v = ((c.x - point.x) * (a.y - point.y) - (a.x - point.x) * (c.y - point.y)) / area;
                float w = 1f - u - v;
                if (u < -tolerance || v < -tolerance || w < -tolerance) continue;

                int ia = _indices[index];
                int ib = _indices[index + 1];
                int ic = _indices[index + 2];
                flow = _flow[ia] * u + _flow[ib] * v + _flow[ic] * w;
                regime = _regime.Count == _vertices.Count
                    ? _regime[ia] * u + _regime[ib] * v + _regime[ic] * w
                    : Vector2.zero;
                return true;
            }
            flow = Vector4.zero;
            regime = Vector2.zero;
            return false;
        }
    }
}
