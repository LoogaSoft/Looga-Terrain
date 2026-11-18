using System.Collections.Generic;
using LoogaSoft.Terrain.Grass;
using LoogaSoft.Terrain.Surface;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>How <see cref="LoogaMeshGroundCover"/> shows the ground on a rock.</summary>
    public enum MeshGroundCoverMode
    {
        /// <summary>A cover mesh over the faces that point up and over the foot, drawn with the terrain surface.</summary>
        Overlay,

        /// <summary>
        /// The rock material (Looga/Rock Face, Asset UV Textures) shows the terrain ground where red of its mask map is
        /// set, for example the grass top of an asset. The component gives each renderer the terrain values, and the grass
        /// grows where the mask is set.
        /// </summary>
        AssetMask
    }

    /// <summary>
    /// Covers the up-facing parts and the foot of a rock mesh, for example a rock or cliff prefab, with the Looga terrain
    /// surface of the ground there. The top and the foot then match the terrain around the rock, wherever the rock is.
    /// The procedural grass can grow on the covered top. The cover mesh is built in the scene and is not saved with it.
    /// </summary>
    /// <remarks>
    /// The cover reads the finest level of detail of the mesh renderers on this object and its children. It shows the
    /// terrain point straight below each vertex, so a rock that is set into a slope shows the ground of the slope on its
    /// top. The terrain under the rock must have a Looga terrain surface. Move or scale the rock and the cover builds
    /// again. Use Rebuild after a terrain change. The meshes need Read/Write in their import settings.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Mesh Ground Cover")]
    public sealed class LoogaMeshGroundCover : MonoBehaviour, IGrassGround
    {
        [Tooltip("Overlay: a cover mesh over the faces that point up. Asset Mask: the rock material shows the terrain " +
                 "ground where red of its mask map is set (Looga/Rock Face with Asset UV Textures); the mask textures " +
                 "must be readable for the grass.")]
        [SerializeField] private MeshGroundCoverMode _mode = MeshGroundCoverMode.Overlay;

        [Header("Top")]
        [Tooltip("Surface steepness in degrees up to which the cover is full.")]
        [SerializeField, Range(0f, 80f)] private float _fullAngle = 30f;
        [Tooltip("Surface steepness in degrees where the cover ends. Between the two angles the cover ends along a " +
                 "ragged edge.")]
        [SerializeField, Range(1f, 89f)] private float _endAngle = 50f;

        [Header("Foot")]
        [Tooltip("Height above the terrain in meters up to which the foot of the rock shows the ground. 0 = no foot " +
                 "cover.")]
        [SerializeField, Min(0f)] private float _footHeight = 0.6f;

        [Header("Surface")]
        [Tooltip("Distance in meters that the cover lies over the rock surface.")]
        [SerializeField, Min(0f)] private float _offset = 0.03f;
        [Tooltip("The procedural grass grows on the covered top, with the grass of the terrain point below.")]
        [SerializeField] private bool _grassOnTop = true;
        [Tooltip("Asset Mask: steepest masked ground in degrees that grows grass. Grass tops of assets roll over their " +
                 "rims, so the grass follows them past the 45 degrees of terrain-like ground.")]
        [SerializeField, Range(30f, 89f)] private float _grassMaxSlope = 80f;

        // Cover vertices with a smaller weight are not written.
        private const float MinimumWeight = 0.01f;
        // Distance in meters past the asset bounds of the terrain point whose ground the asset shows.
        private const float LandMargin = 3f;
        private static readonly int MaskMapId = Shader.PropertyToID("_MaskMap");
        private static readonly int RockGroundCenterId = Shader.PropertyToID("_RockGroundCenter");
        private static readonly int GroundUpStartId = Shader.PropertyToID("_GroundUpStart");
        private static readonly int GroundAlbedoArrayId = Shader.PropertyToID("_GroundAlbedoArray");
        private static readonly List<Vector2> UVs = new();

        private static readonly List<Vector3> Positions = new();
        private static readonly List<Vector3> Normals = new();
        private static readonly List<int> Triangles = new();
        private static readonly List<Renderer> Sources = new();

        private GameObject _coverObject;
        private Mesh _mesh;
        // Asset mask mode: the mask map of each grass submesh.
        private readonly List<Texture> _grassMasks = new();
        private MaterialPropertyBlock _block;
        private MeshRenderer _coverRenderer;
        private Matrix4x4 _builtMatrix;
        private bool _buildQueued;
        // The terrain under the asset. Null when no build waits for it.
        private UnityEngine.Terrain _waitTerrain;
        // Renderers that hold the terrain values of the asset mask mode.
        private readonly List<Renderer> _blockRenderers = new();

        /// <summary>Number of triangles of the cover mesh.</summary>
        public int CoverTriangleCount { get; private set; }

        Bounds IGrassGround.GrassGroundBounds => _mesh ? _mesh.bounds : default;

        void IGrassGround.GetGrassGroundMeshes(List<GrassGroundMesh> result)
        {
            if (!_mesh) return;
            if (_grassMasks.Count == 0)
            {
                result.Add(new GrassGroundMesh(_mesh, Matrix4x4.identity, 0));
                return;
            }
            for (int subMesh = 0; subMesh < _grassMasks.Count; subMesh++)
            {
                result.Add(new GrassGroundMesh(_mesh, Matrix4x4.identity, subMesh, _grassMasks[subMesh]));
            }
        }

        private void OnEnable()
        {
            Rebuild();
        }

        private void OnDisable()
        {
            Release();
        }

        private void OnValidate()
        {
            _endAngle = Mathf.Max(_endAngle, _fullAngle + 1f);
            _buildQueued = true;
        }

        private void Update()
        {
            if (_buildQueued || transform.localToWorldMatrix != _builtMatrix)
            {
                Rebuild();
                return;
            }
            // After a script reload the terrain surface can apply after this object. Build when it is there.
            if (_waitTerrain && LoogaTerrainGroundBlock.SurfaceMeshMaterial(_waitTerrain))
            {
                Rebuild();
                return;
            }
            // A terrain can replace its splat maps (seen after a scene save), and the surface its layer arrays (after a
            // script reload). The block then shows no ground layers.
            if (_block != null && (!LoogaTerrainGroundBlock.HasControlMaps(_block) || !_block.GetTexture(GroundAlbedoArrayId)))
            {
                Rebuild();
            }
        }

        /// <summary>Builds the cover now, for example after the terrain changed.</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            _buildQueued = false;
            Release();
            _builtMatrix = transform.localToWorldMatrix;
            if (!isActiveAndEnabled) return;

            CollectSources();
            if (Sources.Count == 0) return;
            Bounds bounds = Sources[0].bounds;
            foreach (Renderer source in Sources) bounds.Encapsulate(source.bounds);
            UnityEngine.Terrain[] terrains = UnityEngine.Terrain.activeTerrains;
            int terrainIndex = LoogaTerrainGroundBlock.FindTerrain(bounds.center, terrains);
            if (terrainIndex < 0) return;
            UnityEngine.Terrain terrain = terrains[terrainIndex];
            // Without an applied Looga terrain surface the ground values have no layers. Wait for it.
            if (!LoogaTerrainGroundBlock.SurfaceMeshMaterial(terrain))
            {
                _waitTerrain = terrain;
                return;
            }
            if (_mode == MeshGroundCoverMode.AssetMask)
            {
                BuildAssetMask(terrain, bounds);
                return;
            }
            Material material = LoogaTerrainGroundBlock.SurfaceMeshMaterial(terrain);
            if (!material) return;

            _mesh = BuildMesh(terrain);
            if (!_mesh) return;
            _coverObject = new GameObject("Ground cover " + name)
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = gameObject.layer
            };
            _coverObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _coverRenderer = _coverObject.AddComponent<MeshRenderer>();
            _coverRenderer.sharedMaterial = material;
            // The rock casts the shadow. The cover lies on it.
            _coverRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _block = LoogaTerrainGroundBlock.Create(terrain);
            _coverRenderer.SetPropertyBlock(_block);
            if (_grassOnTop) GrassGround.Register(this);
        }

        // Asset mask mode: the terrain values on every renderer of the asset (all levels of detail) and a grass ground
        // mesh from the finest level with one submesh for each mask map. The grass map reads red of the mask for each
        // pixel, so the grass follows the painted ground and not the coarse vertices. The grass mesh is not drawn.
        private void BuildAssetMask(UnityEngine.Terrain terrain, Bounds bounds)
        {
            _block = LoogaTerrainGroundBlock.Create(terrain);
            Vector2 land = LandPoint(bounds);
            _block.SetVector(RockGroundCenterId, new Vector4(land.x, bounds.center.y, land.y, 1f));
            foreach (Renderer target in GetComponentsInChildren<Renderer>(true))
            {
                if (target is not MeshRenderer) continue;
                target.SetPropertyBlock(_block);
                _blockRenderers.Add(target);
            }
            _mesh = BuildMaskMesh(land);
            if (_mesh && _grassOnTop) GrassGround.Register(this);
        }

        private Mesh BuildMaskMesh(Vector2 land)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var covers = new List<Vector4>();
            var uvs = new List<Vector2>();
            var subMeshIndices = new List<List<int>>();
            _grassMasks.Clear();
            float minimumUp = Mathf.Cos(_grassMaxSlope * Mathf.Deg2Rad);
            foreach (Renderer source in Sources)
            {
                if (!source.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) continue;
                Mesh sourceMesh = filter.sharedMesh;
                if (!sourceMesh.isReadable)
                {
                    Debug.LogWarning("Mesh Ground Cover needs Read/Write on mesh '" + sourceMesh.name +
                                     "' (model import settings). The mesh gets no grass.", this);
                    continue;
                }
                Matrix4x4 toWorld = source.localToWorldMatrix;
                Positions.Clear();
                Normals.Clear();
                UVs.Clear();
                sourceMesh.GetVertices(Positions);
                sourceMesh.GetNormals(Normals);
                sourceMesh.GetUVs(0, UVs);
                int vertexCount = Positions.Count;
                if (vertexCount == 0 || UVs.Count != vertexCount || Normals.Count != vertexCount) continue;
                Matrix4x4 normalToWorld = toWorld.inverse.transpose;
                Material[] materials = source.sharedMaterials;
                for (int subMesh = 0; subMesh < sourceMesh.subMeshCount; subMesh++)
                {
                    if (sourceMesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                    Material material = subMesh < materials.Length ? materials[subMesh] : null;
                    Texture mask = material && material.HasProperty(MaskMapId) ? material.GetTexture(MaskMapId) : null;
                    if (!mask) continue;
                    // Ground on faces that look up, as the shader draws it (Asset Ground On Up Faces).
                    float upStart = material.HasProperty(GroundUpStartId) ? material.GetFloat(GroundUpStartId) : 0f;
                    int maskIndex = _grassMasks.IndexOf(mask);
                    if (maskIndex < 0)
                    {
                        maskIndex = _grassMasks.Count;
                        _grassMasks.Add(mask);
                        subMeshIndices.Add(new List<int>());
                    }
                    List<int> indices = subMeshIndices[maskIndex];
                    Triangles.Clear();
                    sourceMesh.GetTriangles(Triangles, subMesh);
                    for (int index = 0; index + 2 < Triangles.Count; index += 3)
                    {
                        int ia = Triangles[index];
                        int ib = Triangles[index + 1];
                        int ic = Triangles[index + 2];
                        if (ia >= vertexCount || ib >= vertexCount || ic >= vertexCount) continue;
                        Vector3 a = toWorld.MultiplyPoint3x4(Positions[ia]);
                        Vector3 b = toWorld.MultiplyPoint3x4(Positions[ib]);
                        Vector3 c = toWorld.MultiplyPoint3x4(Positions[ic]);
                        Vector3 faceNormal = Vector3.Cross(b - a, c - a);
                        if (faceNormal.sqrMagnitude < 1e-12f) continue;
                        faceNormal.Normalize();
                        // Grass grows on masked ground up to the largest slope, for example over a rounded rim.
                        if (faceNormal.y < minimumUp) continue;
                        // The vertex weight is the up-face ground, from the smooth vertex normal as the shader draws
                        // it; the mask adds its ground for each pixel.
                        int first = positions.Count;
                        AddMaskVertex(a, UVs[ia], UpGround(normalToWorld, Normals[ia], upStart), land, positions,
                            normals, covers, uvs);
                        AddMaskVertex(b, UVs[ib], UpGround(normalToWorld, Normals[ib], upStart), land, positions,
                            normals, covers, uvs);
                        AddMaskVertex(c, UVs[ic], UpGround(normalToWorld, Normals[ic], upStart), land, positions,
                            normals, covers, uvs);
                        indices.Add(first);
                        indices.Add(first + 1);
                        indices.Add(first + 2);
                    }
                }
            }
            CoverTriangleCount = positions.Count / 3;
            if (positions.Count == 0)
            {
                _grassMasks.Clear();
                return null;
            }
            var mesh = new Mesh
            {
                name = "Ground mask " + name,
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(2, covers);
            mesh.subMeshCount = subMeshIndices.Count;
            for (int subMesh = 0; subMesh < subMeshIndices.Count; subMesh++)
            {
                mesh.SetTriangles(subMeshIndices[subMesh], subMesh, false);
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        // The terrain point whose grass and ground the asset shows: past the nearer side of the bounds, because the
        // ground under the asset is hidden and can have other grass. One point for all vertices keeps the offsets
        // linear, so the interpolation between the vertices does not move the point under the asset.
        private static Vector2 LandPoint(Bounds bounds)
        {
            return bounds.extents.x <= bounds.extents.z
                ? new Vector2(bounds.max.x + LandMargin, bounds.center.z)
                : new Vector2(bounds.center.x, bounds.max.z + LandMargin);
        }

        // A grass ground vertex that shows the grass of the land point. The grass map drops ground whose normal is
        // steeper than terrain-like ground (45 degrees). This mesh is not drawn and its slope is already tested against
        // the largest grass slope, so its normal points up.
        // Ground weight of Asset Ground On Up Faces (Looga/Rock Face) for a vertex normal. 0 when the material has none.
        private static float UpGround(Matrix4x4 normalToWorld, Vector3 normal, float upStart)
        {
            if (upStart <= 0.001f) return 0f;
            float up = normalToWorld.MultiplyVector(normal).normalized.y;
            return Mathf.Clamp01((up - (upStart - 0.15f)) / 0.15f);
        }

        private static void AddMaskVertex(Vector3 position, Vector2 uv, float weight, Vector2 land,
            List<Vector3> positions, List<Vector3> normals, List<Vector4> covers, List<Vector2> uvs)
        {
            positions.Add(position);
            normals.Add(Vector3.up);
            covers.Add(new Vector4(land.x - position.x, land.y - position.z, weight, 0f));
            uvs.Add(uv);
        }

        // The renderers of the finest level of detail, or all mesh renderers without a LOD group.
        private void CollectSources()
        {
            Sources.Clear();
            var lodGroup = GetComponentInChildren<LODGroup>();
            if (lodGroup && lodGroup.lodCount > 0)
            {
                foreach (Renderer source in lodGroup.GetLODs()[0].renderers)
                {
                    if (source is MeshRenderer && source.enabled) Sources.Add(source);
                }
                return;
            }
            foreach (MeshRenderer source in GetComponentsInChildren<MeshRenderer>())
            {
                if (source.enabled) Sources.Add(source);
            }
        }

        // Builds the cover in world space. Each vertex holds the cover weight of its triangle (top) or of its height over
        // the terrain (foot). The triangles do not share vertices, so each keeps its own weight and offset direction.
        private Mesh BuildMesh(UnityEngine.Terrain terrain)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var covers = new List<Vector4>();
            var indices = new List<int>();
            float fullCos = Mathf.Cos(_fullAngle * Mathf.Deg2Rad);
            float endCos = Mathf.Cos(_endAngle * Mathf.Deg2Rad);
            Vector3 terrainOrigin = terrain.GetPosition();
            foreach (Renderer source in Sources)
            {
                if (!source.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) continue;
                Mesh sourceMesh = filter.sharedMesh;
                // The editor frees the vertex data of a mesh without Read/Write after it uploads the mesh.
                if (!sourceMesh.isReadable)
                {
                    Debug.LogWarning("Mesh Ground Cover needs Read/Write on mesh '" + sourceMesh.name +
                                     "' (model import settings). The mesh gets no cover.", this);
                    continue;
                }
                Matrix4x4 toWorld = source.localToWorldMatrix;
                Matrix4x4 normalToWorld = toWorld.inverse.transpose;
                Positions.Clear();
                Normals.Clear();
                sourceMesh.GetVertices(Positions);
                sourceMesh.GetNormals(Normals);
                int vertexCount = Positions.Count;
                if (vertexCount == 0) continue;
                bool hasNormals = Normals.Count == vertexCount;
                for (int subMesh = 0; subMesh < sourceMesh.subMeshCount; subMesh++)
                {
                    if (sourceMesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                    Triangles.Clear();
                    sourceMesh.GetTriangles(Triangles, subMesh);
                    for (int index = 0; index + 2 < Triangles.Count; index += 3)
                    {
                        if (Triangles[index] >= vertexCount || Triangles[index + 1] >= vertexCount ||
                            Triangles[index + 2] >= vertexCount)
                        {
                            continue;
                        }
                        Vector3 a = toWorld.MultiplyPoint3x4(Positions[Triangles[index]]);
                        Vector3 b = toWorld.MultiplyPoint3x4(Positions[Triangles[index + 1]]);
                        Vector3 c = toWorld.MultiplyPoint3x4(Positions[Triangles[index + 2]]);
                        Vector3 faceNormal = Vector3.Cross(b - a, c - a);
                        if (faceNormal.sqrMagnitude < 1e-12f) continue;
                        faceNormal.Normalize();
                        float top = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(endCos, fullCos, faceNormal.y));
                        // Faces that point down do not show ground at the foot either.
                        bool foot = _footHeight > 0f && faceNormal.y > -0.3f;
                        float weightA = Weight(top, foot, a, terrain, terrainOrigin);
                        float weightB = Weight(top, foot, b, terrain, terrainOrigin);
                        float weightC = Weight(top, foot, c, terrain, terrainOrigin);
                        if (weightA < MinimumWeight && weightB < MinimumWeight && weightC < MinimumWeight) continue;

                        Vector3 lift = faceNormal * _offset;
                        int first = positions.Count;
                        for (int corner = 0; corner < 3; corner++)
                        {
                            int sourceIndex = Triangles[index + corner];
                            Vector3 position = corner == 0 ? a : corner == 1 ? b : c;
                            float weight = corner == 0 ? weightA : corner == 1 ? weightB : weightC;
                            positions.Add(position + lift);
                            normals.Add(hasNormals
                                ? normalToWorld.MultiplyVector(Normals[sourceIndex]).normalized
                                : faceNormal);
                            covers.Add(new Vector4(0f, 0f, weight, 0f));
                            indices.Add(first + corner);
                        }
                    }
                }
            }
            CoverTriangleCount = indices.Count / 3;
            if (indices.Count == 0) return null;

            var mesh = new Mesh
            {
                name = "Ground cover " + name,
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(2, covers);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Cover weight of one vertex: the larger of the top weight of its triangle and its foot weight.
        private float Weight(float top, bool foot, Vector3 position, UnityEngine.Terrain terrain, Vector3 terrainOrigin)
        {
            if (!foot) return top;
            float ground = terrain.SampleHeight(position) + terrainOrigin.y;
            float footWeight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((position.y - ground) / _footHeight));
            return Mathf.Max(top, footWeight);
        }

        private void Release()
        {
            _waitTerrain = null;
            GrassGround.Unregister(this);
            foreach (Renderer target in _blockRenderers)
            {
                if (target) target.SetPropertyBlock(null);
            }
            _blockRenderers.Clear();
            CoverTriangleCount = 0;
            _coverRenderer = null;
            _block = null;
            if (_coverObject) DestroyObject(_coverObject);
            if (_mesh) DestroyObject(_mesh);
            _coverObject = null;
            _mesh = null;
            _grassMasks.Clear();
        }

        private static void DestroyObject(Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
