using System;
using UnityEngine;

namespace LoogaSoft.Terrain
{
    /// <summary>
    /// Builds the reusable grid for GPU terrain patches.
    /// </summary>
    public static class PatchMesh
    {
        /// <summary>
        /// Creates an upward-facing unit grid with the requested number of quads per edge.
        /// </summary>
        public static Mesh Create(int resolution)
        {
            if (resolution < 2 || resolution > 64 || !Mathf.IsPowerOfTwo(resolution))
            {
                throw new ArgumentOutOfRangeException(nameof(resolution));
            }

            int side = resolution + 1;
            var vertices = new Vector3[side * side];
            var normals = new Vector3[vertices.Length];
            var coordinates = new Vector2[vertices.Length];
            var indices = new int[resolution * resolution * 6];
            int index = 0;
            for (int z = 0; z <= resolution; z++)
            {
                for (int x = 0; x <= resolution; x++)
                {
                    int vertex = z * side + x;
                    coordinates[vertex] = new Vector2((float)x / resolution, (float)z / resolution);
                    vertices[vertex] = new Vector3(coordinates[vertex].x, 0f, coordinates[vertex].y);
                    normals[vertex] = Vector3.up;
                    if (x == resolution || z == resolution)
                    {
                        continue;
                    }

                    indices[index++] = vertex;
                    indices[index++] = vertex + side;
                    indices[index++] = vertex + 1;
                    indices[index++] = vertex + 1;
                    indices[index++] = vertex + side;
                    indices[index++] = vertex + side + 1;
                }
            }

            var mesh = new Mesh { name = "Looga Terrain Patch", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = coordinates;
            mesh.triangles = indices;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            return mesh;
        }
    }
}
