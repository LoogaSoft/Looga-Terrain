using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Geology
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Basalt Escarpment")]
    public sealed class BasaltEscarpment : MonoBehaviour
    {
        [Header("Spline")]
        [SerializeField] private SplineContainer _spline;
        [SerializeField] private bool _autoPreviewOnEnable = true;
        [SerializeField] private bool _generateWholeSpline;
        [SerializeField, Range(0f, 1f)] private float _previewCenter = 0.5f;
        [SerializeField, Min(10f)] private float _previewLength = 300f;

        [Header("Column Layout")]
        [SerializeField] private BasaltColumnLayoutSettings _layout = new();

        [Header("Column Shape")]
        [SerializeField, Range(2, 16)] private int _verticalSegments = 8;
        [SerializeField, Range(0f, 1f), Tooltip("Controls corner chamfers, rim erosion, and chip frequency.")]
        private float _edgeWear = 0.32f;
        [SerializeField, Range(0f, 0.35f), Tooltip("Controls deterministic variation along each column face.")]
        private float _surfaceRoughness = 0.08f;
        [SerializeField, Range(0f, 0.3f), Tooltip("Controls localized chip and fracture recess depth.")]
        private float _crackDepth = 0.07f;

        [Header("Surface")]
        [SerializeField] private Material _material;
        [SerializeField] private InstanceMaterialProfile _materialProfile;
        [SerializeField, Min(10f)] private float _maximumDistance = 15000f;

        [Header("Grounding")]
        [SerializeField] private bool _clampBasesToTerrain;

        private readonly List<GameObject> _prototypeObjects = new();
        private readonly List<Mesh> _meshes = new();
        private InstanceRenderer _renderer;
        private Material _generatedMaterial;
        private bool _rebuildRequested;
        private bool _forceWholeSpline;

        public SplineContainer Spline => _spline;
        public BasaltColumnLayoutSettings Layout => _layout;
        public int PlacementCount { get; private set; }
        public int PrototypeCount { get; private set; }
        public Bounds GeneratedBounds { get; private set; }
        public string Diagnostic { get; private set; }

        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                Rebuild(false);
                return;
            }

            if (_autoPreviewOnEnable)
                Rebuild(false);
        }

        private void OnDisable() => Release();
        private void OnDestroy() => Release();

        private void OnValidate()
        {
            if (Application.isPlaying) _rebuildRequested = true;
        }

        private void LateUpdate()
        {
            if (!_rebuildRequested) return;
            _rebuildRequested = false;
            Rebuild(_forceWholeSpline);
            _forceWholeSpline = false;
        }

        public void RebuildPreview() => Rebuild(false);
        public void RebuildWholeSpline() => Rebuild(true);

        public void RequestRebuild(bool wholeSpline)
        {
            _forceWholeSpline = wholeSpline;
            _rebuildRequested = true;
        }

        public void ClearPreview()
        {
            Release();
            Diagnostic = null;
        }

        public void ConfigureSpline(SplineContainer spline)
        {
            _spline = spline;
        }

        private void Rebuild(bool forceWholeSpline)
        {
            Release();
            Diagnostic = null;
            if (!isActiveAndEnabled)
            {
                Diagnostic = "Enable the GameObject before building a preview.";
                return;
            }
            if (!_spline || _spline.Spline == null || _spline.Spline.Count < 2)
            {
                Diagnostic = "Assign a spline with at least two knots.";
                return;
            }

            try
            {
                _layout.Validate();
                float splineLength = _spline.CalculateLength();
                if (!float.IsFinite(splineLength) || splineLength <= 0.1f)
                {
                    Diagnostic = "The assigned spline has no measurable length.";
                    return;
                }

                bool whole = forceWholeSpline || _generateWholeSpline || Application.isPlaying;
                GetRange(splineLength, whole, out float start, out float end);
                // The generator interpolates between samples, so samples finer than the smallest pillar suffice.
                float sampleSpacing = Mathf.Max(0.25f, _layout.RadiusMinimum * _layout.EdgeRadiusScale * 0.5f);
                List<BasaltSplineSample> samples = SampleSpline(splineLength, start, end, sampleSpacing);
                List<BasaltColumnPlacement> placements = BasaltColumnLayoutGenerator.Generate(samples,
                    _layout, _clampBasesToTerrain ? SampleTerrainHeight : null);
                BasaltEscarpmentOpening[] openings =
                    GetComponentsInChildren<BasaltEscarpmentOpening>(false);
                BasaltEscarpmentOpening.ApplyAll(placements, openings);
                if (placements.Count == 0)
                {
                    Diagnostic = "The selected spline range produced no columns.";
                    return;
                }

                Material material = ResolveMaterial();
                if (!Application.isPlaying)
                {
                    BuildEditorPreview(placements, material);
                    PlacementCount = placements.Count;
                    return;
                }

                _renderer = new InstanceRenderer(layer: gameObject.layer,
                    worldSourceId: "basalt-columns:" + GetInstanceID());
                _renderer.MaxDistance = _maximumDistance;
                Color instanceTint = material.HasProperty("_BaseColor")
                    ? material.GetColor("_BaseColor")
                    : Color.white;
                Dictionary<(int sides, int variant), int> prototypes = new();
                foreach (BasaltColumnPlacement placement in placements)
                {
                    var key = (placement.SideCount, placement.ShapeVariant);
                    if (!prototypes.TryGetValue(key, out int prototype))
                    {
                        GameObject source = CreatePrototype(key.SideCount, key.ShapeVariant, material);
                        prototype = _renderer.Register(InstancePrototype.FromPrefab(source, _materialProfile));
                        prototypes.Add(key, prototype);
                    }
                    _renderer.Add(prototype, placement.Matrix,
                        new InstanceAppearance(instanceTint, Color.white,
                            placement.ContactOcclusion));
                    IncludeBounds(placement.Matrix);
                }
                _renderer.Flush();
                PlacementCount = placements.Count;
                PrototypeCount = prototypes.Count;
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                Release();
            }
        }

        private void BuildEditorPreview(IReadOnlyList<BasaltColumnPlacement> placements,
            Material material)
        {
            Dictionary<(int sides, int variant), List<BasaltColumnPlacement>> groups = new();
            foreach (BasaltColumnPlacement placement in placements)
            {
                var key = (placement.SideCount, placement.ShapeVariant);
                if (!groups.TryGetValue(key, out List<BasaltColumnPlacement> groupPlacements))
                {
                    groupPlacements = new List<BasaltColumnPlacement>();
                    groups.Add(key, groupPlacements);
                }
                groupPlacements.Add(placement);
                IncludeBounds(placement.Matrix);
            }

            foreach (KeyValuePair<(int sides, int variant), List<BasaltColumnPlacement>> group in groups)
            {
                Mesh source = BasaltColumnMeshFactory.Create(group.Key.sides, _verticalSegments,
                    group.Key.variant, _edgeWear, _surfaceRoughness, _crackDepth, _layout.Seed);
                CombineInstance[] combine = new CombineInstance[group.Value.Count];
                for (int index = 0; index < combine.Length; index++)
                {
                    combine[index] = new CombineInstance
                    {
                        mesh = source,
                        transform = group.Value[index].Matrix
                    };
                }

                Mesh combined = new()
                {
                    name = $"Basalt Preview {group.Key.sides} Sides {group.Key.variant}",
                    hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
                };
                combined.CombineMeshes(combine, true, true, false);
                ApplyCombinedPillarData(combined, source, group.Value);
                GameObject preview = new(combined.name)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    layer = gameObject.layer
                };
                MeshFilter filter = preview.AddComponent<MeshFilter>();
                MeshRenderer renderer = preview.AddComponent<MeshRenderer>();
                filter.sharedMesh = combined;
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                renderer.receiveShadows = true;
                _meshes.Add(source);
                _meshes.Add(combined);
                _prototypeObjects.Add(preview);
            }
            PrototypeCount = groups.Count;
        }

        private static void ApplyCombinedPillarData(Mesh combined, Mesh source,
            IReadOnlyList<BasaltColumnPlacement> placements)
        {
            Vector3[] sourceVertices = source.vertices;
            List<Vector4> pillarData = new(sourceVertices.Length * placements.Count);
            List<Vector4> contactData = new(sourceVertices.Length * placements.Count);
            foreach (BasaltColumnPlacement placement in placements)
            {
                Matrix4x4 matrix = placement.Matrix;
                Vector3 scale = matrix.lossyScale;
                scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                Vector3 origin = matrix.GetColumn(3);
                float seed = PlacementShaderSeed(origin);
                foreach (Vector3 vertex in sourceVertices)
                {
                    Vector3 metricPosition = Vector3.Scale(vertex, scale);
                    pillarData.Add(new Vector4(metricPosition.x, metricPosition.y,
                        metricPosition.z, seed));
                    contactData.Add(placement.ContactOcclusion);
                }
            }

            if (pillarData.Count == combined.vertexCount)
            {
                combined.SetUVs(2, pillarData);
                combined.SetUVs(3, contactData);
            }
        }

        private static float PlacementShaderSeed(Vector3 position)
        {
            unchecked
            {
                int x = Mathf.FloorToInt(position.x * 0.173f);
                int y = Mathf.FloorToInt(position.y * 0.173f);
                int z = Mathf.FloorToInt(position.z * 0.173f);
                uint hash = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^
                    (uint)(z * 83492791) ^ 0x9E3779B9u;
                hash ^= hash >> 16;
                hash *= 0x7FEB352Du;
                hash ^= hash >> 15;
                return (hash & 0x00FFFFFFu) / 16777216f;
            }
        }

        private List<BasaltSplineSample> SampleSpline(float splineLength, float start,
            float end, float spacing)
        {
            float rangeLength = Mathf.Max(0.01f, (end - start) * splineLength);
            int count = Mathf.Max(2, Mathf.CeilToInt(rangeLength / spacing) + 1);
            List<BasaltSplineSample> result = new(count);
            using NativeSpline worldSpline = new(_spline.Spline,
                (float4x4)_spline.transform.localToWorldMatrix, Allocator.Temp);
            float accumulated = 0f;
            float t = start;
            Vector3 previous = (Vector3)worldSpline.EvaluatePosition(t);
            float stepDistance = rangeLength / (count - 1);
            for (int index = 0; index < count; index++)
            {
                float local = index / (float)(count - 1);
                Vector3 position;
                if (index == 0)
                {
                    position = previous;
                }
                else
                {
                    position = (Vector3)worldSpline.GetPointAtLinearDistance(t, stepDistance, out t);
                    accumulated += Vector3.Distance(previous, position);
                }
                Vector3 tangent = (Vector3)worldSpline.EvaluateTangent(t);
                result.Add(new BasaltSplineSample(position, tangent, accumulated, local));
                previous = position;
            }
            return result;
        }

        private void GetRange(float splineLength, bool whole, out float start, out float end)
        {
            if (whole || _previewLength >= splineLength)
            {
                start = 0f;
                end = 1f;
                return;
            }
            float normalizedLength = Mathf.Clamp01(_previewLength / splineLength);
            start = Mathf.Clamp01(_previewCenter - normalizedLength * 0.5f);
            end = Mathf.Clamp01(start + normalizedLength);
            start = Mathf.Max(0f, end - normalizedLength);
        }

        private float? SampleTerrainHeight(Vector3 worldPoint)
        {
            foreach (UnityEngine.Terrain terrain in UnityEngine.Terrain.activeTerrains)
            {
                TerrainData data = terrain.terrainData;
                if (!data) continue;
                Vector3 origin = terrain.transform.position;
                Vector3 size = data.size;
                if (worldPoint.x < origin.x || worldPoint.z < origin.z ||
                    worldPoint.x > origin.x + size.x || worldPoint.z > origin.z + size.z) continue;
                return terrain.SampleHeight(worldPoint) + origin.y;
            }
            return null;
        }

        private Material ResolveMaterial()
        {
            if (_material) return _material;
            Shader shader = Shader.Find("Looga/Basalt Column");
            if (!shader) throw new InvalidOperationException("The Looga/Basalt Column shader is missing.");
            _generatedMaterial = new Material(shader)
            {
                name = "Generated Looga Basalt Material",
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = true
            };
            return _generatedMaterial;
        }

        private GameObject CreatePrototype(int sides, int variant, Material material)
        {
            Mesh mesh = BasaltColumnMeshFactory.Create(sides, _verticalSegments, variant,
                _edgeWear, _surfaceRoughness, _crackDepth, _layout.Seed);
            GameObject prototype = new($"Basalt {sides} Sides {variant}")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = gameObject.layer
            };
            MeshFilter filter = prototype.AddComponent<MeshFilter>();
            MeshRenderer renderer = prototype.AddComponent<MeshRenderer>();
            filter.sharedMesh = mesh;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;
            _meshes.Add(mesh);
            _prototypeObjects.Add(prototype);
            return prototype;
        }

        private void IncludeBounds(Matrix4x4 matrix)
        {
            Vector3 center = matrix.MultiplyPoint3x4(Vector3.zero);
            Vector3 size = new(matrix.GetColumn(0).magnitude, matrix.GetColumn(1).magnitude,
                matrix.GetColumn(2).magnitude);
            Bounds bounds = new(center, size);
            if (GeneratedBounds.size == Vector3.zero)
            {
                GeneratedBounds = bounds;
                return;
            }

            Bounds combined = GeneratedBounds;
            combined.Encapsulate(bounds);
            GeneratedBounds = combined;
        }

        private void Release()
        {
            _renderer?.Dispose();
            _renderer = null;
            foreach (GameObject prototype in _prototypeObjects) DestroyGeneratedObject(prototype);
            _prototypeObjects.Clear();
            foreach (Mesh mesh in _meshes) DestroyGeneratedObject(mesh);
            _meshes.Clear();
            DestroyGeneratedObject(_generatedMaterial);
            _generatedMaterial = null;
            PlacementCount = 0;
            PrototypeCount = 0;
            GeneratedBounds = default;
        }

        private static void DestroyGeneratedObject(UnityEngine.Object value)
        {
            if (!value) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }

        private void OnDrawGizmosSelected()
        {
            if (!_spline || _spline.Spline == null || _spline.Spline.Count < 2) return;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            const int steps = 96;
            Vector3 previous = (Vector3)_spline.EvaluatePosition(0f);
            for (int i = 1; i <= steps; i++)
            {
                Vector3 next = (Vector3)_spline.EvaluatePosition(i / (float)steps);
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
        }
    }
}
