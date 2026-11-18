using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Surface
{
    /// <summary>
    /// Renders the albedo of a terrain surface into a texture in terrain UV space: U follows the terrain X axis and V
    /// the terrain Z axis. Use it where a system needs the visible terrain colour but cannot run the surface shader,
    /// for example a ray tracing scene.
    /// </summary>
    /// <remarks>
    /// The terrain material must have a Meta pass, as Looga/Terrain/Surface does. The draw uses the per-tile data of the
    /// terrain splat property block. The surface is evaluated as seen from far away, so distance blends use their
    /// far values. The texture holds linear colour.
    /// </remarks>
    public sealed class LoogaTerrainAlbedo : IDisposable
    {
        private static readonly int MetaVertexControlID = Shader.PropertyToID("unity_MetaVertexControl");
        private static readonly int MetaFragmentControlID = Shader.PropertyToID("unity_MetaFragmentControl");
        private static readonly int OneOverOutputBoostID = Shader.PropertyToID("unity_OneOverOutputBoost");
        private static readonly int MaxOutputValueID = Shader.PropertyToID("unity_MaxOutputValue");
        private static readonly int LightmapSTID = Shader.PropertyToID("unity_LightmapST");
        private static readonly int CameraPositionID = Shader.PropertyToID("_WorldSpaceCameraPos");

        // Height of the evaluation camera above the terrain centre in meters. Far enough for the far surface blends.
        private const float ViewHeight = 2000;

        private readonly CommandBuffer _commands = new() { name = "Looga Terrain Albedo" };
        private readonly MaterialPropertyBlock _properties = new();
        private Mesh _grid;
        private TerrainData _gridData;
        private int _gridResolution;

        /// <summary>Vertices on each side of the height grid. More vertices follow the terrain shape more closely.</summary>
        public int GridResolution { get; set; } = 257;

        /// <summary>
        /// Render the albedo of the terrain into the target. The target must be a 2D color render texture. Returns
        /// false and leaves the target unchanged when the terrain material has no Meta pass.
        /// </summary>
        public bool Render(UnityEngine.Terrain terrain, RenderTexture target)
        {
            if (terrain == null) throw new ArgumentNullException(nameof(terrain));
            if (target == null) throw new ArgumentNullException(nameof(target));
            Material material = terrain.materialTemplate;
            int pass = material ? material.FindPass("Meta") : -1;
            if (pass < 0) return false;

            BuildGrid(terrain.terrainData);
            terrain.GetSplatMaterialPropertyBlock(_properties);
            _properties.SetVector(LightmapSTID, new Vector4(1, 1, 0, 0));

            Vector3 size = terrain.terrainData.size;
            Vector3 center = terrain.GetPosition() + new Vector3(size.x * 0.5f, 0, size.z * 0.5f);
            _commands.Clear();
            _commands.SetRenderTarget(target);
            _commands.ClearRenderTarget(false, true, Color.black);
            // The Meta vertex stage writes the UV as the world XY position. This projection maps 0 to 1 onto the target.
            _commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0, 1, 0, 1, -1, 1));
            _commands.SetGlobalVector(MetaVertexControlID, new Vector4(1, 0, 0, 0));
            _commands.SetGlobalVector(MetaFragmentControlID, new Vector4(1, 0, 0, 0));
            _commands.SetGlobalFloat(OneOverOutputBoostID, 1);
            _commands.SetGlobalFloat(MaxOutputValueID, 1);
            _commands.SetGlobalVector(CameraPositionID, center + Vector3.up * ViewHeight);
            _commands.DrawMesh(_grid, terrain.transform.localToWorldMatrix, material, 0, pass, _properties);
            Graphics.ExecuteCommandBuffer(_commands);
            return true;
        }

        /// <summary>Build the height grid again on the next render, for example after a heightmap change.</summary>
        public void InvalidateGrid()
        {
            _gridData = null;
        }

        public void Dispose()
        {
            _commands.Release();
            if (_grid != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_grid);
                else UnityEngine.Object.DestroyImmediate(_grid);
            }
            _grid = null;
        }

        // A regular grid over the terrain in terrain space, with the terrain heights, normals and UVs.
        private void BuildGrid(TerrainData data)
        {
            int resolution = Mathf.Clamp(GridResolution, 2, 1025);
            if (_grid != null && _gridData == data && _gridResolution == resolution) return;
            if (_grid == null)
            {
                _grid = new Mesh
                {
                    name = "Looga Terrain Albedo Grid",
                    hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = IndexFormat.UInt32
                };
            }
            _grid.Clear();
            _gridData = data;
            _gridResolution = resolution;

            Vector3 size = data.size;
            var vertices = new Vector3[resolution * resolution];
            var normals = new Vector3[vertices.Length];
            var uvs = new Vector2[vertices.Length];
            float step = 1f / (resolution - 1);
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int index = z * resolution + x;
                    float u = x * step;
                    float v = z * step;
                    vertices[index] = new Vector3(u * size.x, data.GetInterpolatedHeight(u, v), v * size.z);
                    normals[index] = data.GetInterpolatedNormal(u, v);
                    uvs[index] = new Vector2(u, v);
                }
            }
            var triangles = new int[(resolution - 1) * (resolution - 1) * 6];
            int t = 0;
            for (int z = 0; z < resolution - 1; z++)
            {
                for (int x = 0; x < resolution - 1; x++)
                {
                    int i = z * resolution + x;
                    triangles[t++] = i;
                    triangles[t++] = i + resolution;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + resolution;
                    triangles[t++] = i + resolution + 1;
                }
            }
            _grid.vertices = vertices;
            _grid.normals = normals;
            _grid.uv = uvs;
            _grid.triangles = triangles;
            _grid.bounds = new Bounds(size * 0.5f, size);
        }
    }
}
