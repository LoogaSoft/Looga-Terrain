using System.Collections.Generic;
using LoogaSoft.Terrain.Surface;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// A rock collar around a cave opening. The collar overlaps the cut face, the terrain and the cave wall, so the
    /// edges where they meet are hidden behind it, as with an entrance mesh over a terrain hole. Its outer part lies on
    /// the surface in front of it (cliff walls, rock faces, terrain), so only the rock next to the opening stands out.
    /// The object sits on the opening floor at the face, and its forward axis points out of the cave. The mesh is
    /// built in the scene and is not saved with it.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Cave Mouth")]
    public sealed class LoogaCaveMouth : MonoBehaviour
    {
        // Rows of the collar cross-section, from inside the cave to the buried outer edge.
        private const int SectionRows = 9;
        // Distance between the columns around the opening, in meters.
        private const float ColumnSpacing = 0.3f;
        // The outer rows look for the surface from this far in front of the face plane, in meters.
        private const float SurfaceSearch = 6f;
        private const float SurfaceReach = 14f;
        // Thickness of the outer rows above the surface they lie on, in meters.
        private const float SkinThickness = 0.08f;
        // From this row on, the rows lie on the surface at their place.
        private const int FirstSurfaceRow = 5;
        private const int SmoothPasses = 4;
        // Deepest face behind the face plane that the lip follows, in meters.
        private const float MaximumFaceDepth = 6f;

        private static readonly RaycastHit[] Hits = new RaycastHit[16];

        [Header("Opening")]
        [Tooltip("Width and height of the opening, in meters. Use the size of the passage at its end.")]
        [SerializeField, Min(0.5f)] private float _width = 7f;
        [SerializeField, Min(0.5f)] private float _height = 5f;
        [SerializeField, Min(0f)] private float _cornerRadius = 0.6f;

        [Header("Collar")]
        [Tooltip("Width of the collar on the face around the opening, in meters.")]
        [SerializeField, Min(0.2f)] private float _frameWidth = 1.6f;
        [Tooltip("How far the collar stands out in front of the surface at the opening, in meters.")]
        [SerializeField, Min(0f)] private float _front = 0.35f;
        [Tooltip("How far the collar lines the cave behind the face, in meters. Its inner end is behind the cave wall.")]
        [SerializeField, Min(0.2f)] private float _depth = 2.5f;
        [Tooltip("How far the collar narrows the opening at the face, so the cave wall starts behind it, in meters.")]
        [SerializeField, Min(0f)] private float _lip = 0.35f;
        [Tooltip("How far the outer edge of the collar goes into the surface behind it, in meters.")]
        [SerializeField, Min(0f)] private float _bury = 0.4f;
        [Tooltip("How far the jambs go into the ground below the floor, in meters.")]
        [SerializeField, Min(0f)] private float _sink = 0.6f;
        [Tooltip("Size of the rock bumps, in meters.")]
        [SerializeField, Min(0f)] private float _roughness = 0.15f;
        [SerializeField] private int _seed = 1;

        [Header("Surface")]
        [Tooltip("Use the material of the cave and of the rock around the opening.")]
        [SerializeField] private Material _material;
        [Tooltip("Distance between the rock layers, in meters. Use the layer spacing of the cave.")]
        [SerializeField, Min(0.1f)] private float _layerSpacing = 2.5f;
        [SerializeField] private ShadowCastingMode _shadowCasting = ShadowCastingMode.On;

        private GameObject _root;
        private Mesh _mesh;
        private bool _buildQueued;
        private Matrix4x4 _builtMatrix;

        #region Built-in
        private void OnEnable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += EditorTick;
#endif
            Rebuild();
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
            Release();
        }

        private void OnValidate()
        {
            _buildQueued = true;
        }
        #endregion

        /// <summary>Builds the collar now. Main thread.</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            _buildQueued = false;
            Release();
            if (!isActiveAndEnabled || !_material) return;

            _builtMatrix = transform.localToWorldMatrix;
            _mesh = BuildMesh();
            _root = new GameObject("Mouth " + name) { hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer };
            _root.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var meshRenderer = _root.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = _material;
            meshRenderer.shadowCastingMode = _shadowCasting;
            UnityEngine.Terrain[] terrains = UnityEngine.Terrain.activeTerrains;
            int terrain = LoogaTerrainGroundBlock.FindTerrain(transform.position, terrains);
            if (terrain >= 0) meshRenderer.SetPropertyBlock(LoogaTerrainGroundBlock.Create(terrains[terrain]));
            _root.AddComponent<MeshCollider>().sharedMesh = _mesh;
        }

        #region Mesh
        // The mesh is in world space, so the renderer needs no transform.
        private Mesh BuildMesh()
        {
            List<(Vector2 Point, Vector2 Normal)> path = OpeningPath();
            int columns = path.Count;
            var positions = new Vector3[columns * SectionRows];
            var colors = new Color32[positions.Length];
            var groundOffsets = new Vector2[positions.Length];
            var noiseSeed = new float3(_seed * 17.31f, _seed * 5.13f, _seed * 11.7f);
            Matrix4x4 localToWorld = transform.localToWorldMatrix;
            var frames = new float[columns];
            for (int column = 0; column < columns; column++)
            {
                // The collar is wider in places, so its outer edge is not a regular outline.
                float wobble = noise.snoise(new float2(column * ColumnSpacing * 0.35f, _seed * 3.1f));
                frames[column] = _frameWidth * (0.8f + 0.35f * wobble);
            }
            float[,] surface = SurfaceDepths(path, frames);
            for (int column = 0; column < columns; column++)
            {
                (Vector2 point, Vector2 normal) = path[column];
                float frame = frames[column];
                for (int row = 0; row < SectionRows; row++)
                {
                    Vector2 section = Section(row, frame, surface[column, Mathf.Max(row, FirstSurfaceRow)]);
                    // r goes out from the opening, z goes out of the cave.
                    var local = new Vector3(point.x + normal.x * section.x, point.y + normal.y * section.x, section.y);
                    Vector3 world = localToWorld.MultiplyPoint3x4(local);
                    float3 noisePoint = (float3)world / math.max(_roughness * 4f, 0.1f) + noiseSeed;
                    float bump = noise.snoise(noisePoint) + 0.5f * noise.snoise(noisePoint * 2.3f);
                    // The bumps grow toward the outer edge and stay small at the inner edge of the lip.
                    float bumpWeight = row <= 2 ? 0.4f : row >= SectionRows - 2 ? 0.3f : 1f;
                    Vector3 offset = localToWorld.MultiplyVector(new Vector3(normal.x, normal.y, 0.6f).normalized);
                    world += offset * (bump * _roughness * bumpWeight);
                    int index = column * SectionRows + row;
                    positions[index] = world;
                    colors[index] = RockColor(world, row, local.y);
                }
            }

            var triangles = new List<int>((columns - 1) * (SectionRows - 1) * 6);
            for (int column = 0; column < columns - 1; column++)
            {
                for (int row = 0; row < SectionRows - 1; row++)
                {
                    int a = column * SectionRows + row, b = a + 1, c = a + SectionRows, d = c + 1;
                    triangles.Add(a); triangles.Add(c); triangles.Add(b);
                    triangles.Add(b); triangles.Add(c); triangles.Add(d);
                }
            }
            var mesh = new Mesh
            {
                name = "Cave mouth " + name, hideFlags = HideFlags.HideAndDontSave,
                indexFormat = positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(positions);
            mesh.SetColors(colors);
            mesh.SetUVs(1, groundOffsets);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            // The front of the lip must face out of the cave. The winding depends on the path direction.
            Vector3 lipNormal = mesh.normals[(columns / 2) * SectionRows + 4];
            if (Vector3.Dot(lipNormal, transform.forward) < 0f)
            {
                triangles.Reverse();
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateNormals();
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        // The cross-section of the collar at one column: x = distance out from the opening edge, y = distance out of
        // the cave from the face plane. It starts behind the cave wall and comes out over the lip. From row 5 the rows
        // lie on the surface at their place (depth = surface y), and the outer edge goes into it.
        private Vector2 Section(int row, float frame, float depth)
        {
            // The lip follows the face where it lies in front of or behind the face plane, for example a face
            // that is cut at a slant to the passage.
            float face = Mathf.Clamp(depth, -MaximumFaceDepth, SurfaceSearch * 0.5f);
            switch (row)
            {
                case 0: return new Vector2(0.4f, face - _depth);
                case 1: return new Vector2(0.05f, face - _depth * 0.6f);
                case 2: return new Vector2(-_lip * 0.7f, face - _depth * 0.2f);
                case 3: return new Vector2(-_lip, face + _front * 0.3f);
                case 4: return new Vector2(-_lip * 0.6f, face + _front * 0.9f);
                case 5: return new Vector2(frame * 0.2f, depth + _front);
                case 6: return new Vector2(frame * 0.5f, depth + _front * 0.5f);
                case 7: return new Vector2(frame * 0.8f, depth + SkinThickness);
                default: return new Vector2(frame, depth - _bury);
            }
        }

        // Surface depth of each column and outer row. A ray that finds no surface, for example through the hole of the
        // opening, takes the depth of the nearest columns that found one. The depths are then smoothed around the
        // opening, so neighbouring columns do not tear apart.
        private float[,] SurfaceDepths(List<(Vector2 Point, Vector2 Normal)> path, float[] frames)
        {
            int columns = path.Count;
            var depths = new float[columns, SectionRows];
            var line = new float[columns];
            var smooth = new float[columns];
            for (int row = FirstSurfaceRow; row < SectionRows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    float across = Section(row, frames[column], 0f).x;
                    line[column] = SurfaceDepth(path[column].Point + path[column].Normal * across);
                }
                FillMissing(line);
                for (int pass = 0; pass < SmoothPasses; pass++)
                {
                    for (int column = 0; column < columns; column++)
                    {
                        float before = line[Mathf.Max(column - 1, 0)], after = line[Mathf.Min(column + 1, columns - 1)];
                        smooth[column] = 0.25f * before + 0.5f * line[column] + 0.25f * after;
                    }
                    System.Array.Copy(smooth, line, columns);
                }
                for (int column = 0; column < columns; column++) depths[column, row] = line[column];
            }
            return depths;
        }

        // Linear fill of NaN values between the found values, and the nearest found value at the ends. 0 when none.
        private static void FillMissing(float[] values)
        {
            int previous = -1;
            for (int index = 0; index <= values.Length; index++)
            {
                if (index < values.Length && float.IsNaN(values[index])) continue;
                for (int gap = previous + 1; gap < index; gap++)
                {
                    float from = previous >= 0 ? values[previous] : index < values.Length ? values[index] : 0f;
                    float to = index < values.Length ? values[index] : from;
                    values[gap] = Mathf.Lerp(from, to, (gap - previous) / (float)(index - previous));
                }
                previous = index;
            }
        }

        // Depth of the first surface in front of a point of the face plane, along the forward axis, in local meters.
        // Cliff walls, rock faces, rock formations and the terrain count. NaN without a surface.
        private float SurfaceDepth(Vector2 point)
        {
            Vector3 start = transform.TransformPoint(new Vector3(point.x, point.y, SurfaceSearch));
            int count = Physics.RaycastNonAlloc(start, -transform.forward, Hits, SurfaceReach,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int hit = 0; hit < count; hit++)
            {
                Collider collider = Hits[hit].collider;
                // The terrain and the generated rock (roots named "Rock ...") are the surface. Props in front of the
                // opening, for example timber sets, are not.
                if (!collider || !(collider is TerrainCollider || collider.transform.root.name.StartsWith("Rock ")))
                {
                    continue;
                }
                nearest = Mathf.Min(nearest, Hits[hit].distance);
            }
            return nearest == float.MaxValue ? float.NaN : SurfaceSearch - nearest;
        }

        // Points around the opening in the local XY plane, from the left jamb below the floor, over the top, to the
        // right jamb below the floor, with the normal that points away from the opening.
        private List<(Vector2, Vector2)> OpeningPath()
        {
            float half = _width * 0.5f;
            float radius = Mathf.Min(_cornerRadius, half, _height * 0.5f);
            var path = new List<(Vector2, Vector2)>();
            void Line(Vector2 from, Vector2 to, Vector2 normal, bool last)
            {
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to) / ColumnSpacing));
                for (int step = 0; step < steps + (last ? 1 : 0); step++)
                {
                    path.Add((Vector2.Lerp(from, to, step / (float)steps), normal));
                }
            }
            void Arc(Vector2 centre, float startAngle, float endAngle)
            {
                int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(endAngle - startAngle) * radius / ColumnSpacing));
                for (int step = 0; step < steps; step++)
                {
                    float angle = Mathf.Lerp(startAngle, endAngle, step / (float)steps);
                    var normal = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                    path.Add((centre + normal * radius, normal));
                }
            }
            Line(new Vector2(-half, -_sink), new Vector2(-half, _height - radius), Vector2.left, false);
            Arc(new Vector2(-half + radius, _height - radius), Mathf.PI, Mathf.PI * 0.5f);
            Line(new Vector2(-half + radius, _height), new Vector2(half - radius, _height), Vector2.up, false);
            Arc(new Vector2(half - radius, _height - radius), Mathf.PI * 0.5f, 0f);
            Line(new Vector2(half, _height - radius), new Vector2(half, -_sink), Vector2.right, true);
            return path;
        }

        // Rock face vertex values: R hardness, G cavity, B layer value, A ground weight. The layers follow the height,
        // as on the cave walls. The jamb feet take the terrain ground, so the collar stands in the soil.
        private Color32 RockColor(Vector3 world, int row, float heightAboveFloor)
        {
            var point = (float3)world;
            float warp = noise.snoise(point * 0.03f) * 0.6f;
            float layer = 0.5f + 0.5f * math.sin((point.y / _layerSpacing + warp) * 6.2831853f);
            float hardness = math.saturate(0.5f + 0.5f * noise.snoise(point * 0.08f));
            float cavity = row <= 2 ? 0.6f : row >= SectionRows - 2 ? 0.4f : 0.1f;
            float ground = math.saturate((0.6f - heightAboveFloor) / 0.8f);
            return new Color32((byte)(hardness * 255f), (byte)(cavity * 255f), (byte)(layer * 255f),
                (byte)(ground * 255f));
        }
        #endregion

#if UNITY_EDITOR
        private void EditorTick()
        {
            if (Application.isPlaying) return;
            if (_buildQueued || transform.localToWorldMatrix != _builtMatrix) Rebuild();
        }
#endif

        private void Release()
        {
            if (_root) Destroy(_root);
            if (_mesh) Destroy(_mesh);
            _root = null;
            _mesh = null;
        }

        private static void Destroy(Object target)
        {
            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }
    }
}
