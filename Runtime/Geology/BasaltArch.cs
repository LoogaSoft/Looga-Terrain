using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Geology
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SplineContainer), typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Looga Terrain/Basalt Arch")]
    public sealed class BasaltArch : MonoBehaviour
    {
        [Header("Spline")]
        [SerializeField] private SplineContainer _spline;
        [SerializeField] private bool _autoRebuild = true;

        [Header("Massive Column Shape")]
        [SerializeField, Range(5, 12)] private int _crossSectionSides = 8;
        [SerializeField, Min(0.5f)] private float _halfWidth = 90f;
        [SerializeField, Min(0.5f)] private float _halfDepth = 65f;
        [SerializeField, Range(0f, 0.3f)] private float _cornerWear = 0.12f;
        [SerializeField, Range(0f, 0.25f)] private float _surfaceRoughness = 0.055f;
        [SerializeField, Range(0f, 0.3f)] private float _longitudinalVariation = 0.07f;
        [SerializeField] private int _seed = 47011;

        [Header("Tessellation")]
        [SerializeField, Min(2f)] private float _sampleSpacing = 24f;
        [SerializeField, Range(8, 512)] private int _maximumSegments = 256;

        [Header("Surface")]
        [SerializeField] private Material _material;
        [SerializeField] private bool _castShadows = true;
        [SerializeField] private bool _receiveShadows = true;
        [SerializeField] private bool _generateCollider;

        private Mesh _generatedMesh;
        private Material _generatedMaterial;
        private bool _rebuildRequested;

        public SplineContainer Spline => _spline;
        public Mesh GeneratedMesh => _generatedMesh;
        public string Diagnostic { get; private set; }

        private void Reset()
        {
            _spline = GetComponent<SplineContainer>();
            _rebuildRequested = true;
        }

        private void OnEnable()
        {
            if (!_spline) _spline = GetComponent<SplineContainer>();
            UnityEngine.Splines.Spline.Changed += OnSplineChanged;
            _rebuildRequested = true;
            RebuildIfNeeded(true);
        }

        private void OnDisable()
        {
            UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
            ReleaseGeneratedResources();
        }

        private void OnDestroy()
        {
            UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
            ReleaseGeneratedResources();
        }

        private void OnValidate()
        {
            _crossSectionSides = Mathf.Clamp(_crossSectionSides, 5, 12);
            _halfWidth = Mathf.Max(0.5f, _halfWidth);
            _halfDepth = Mathf.Max(0.5f, _halfDepth);
            _sampleSpacing = Mathf.Max(2f, _sampleSpacing);
            _maximumSegments = Mathf.Clamp(_maximumSegments, 8, 512);
            // The custom inspector requests a rebuild after serialized settings change.
            // Do not request one here: assigning the generated mesh can validate sibling
            // components and form an edit-mode feedback loop.
        }

        private void Update()
        {
            // Spline.Changed and explicit inspector actions set this flag. Keeping Update
            // as a one-shot dispatcher coalesces bursts of knot edits without polling or
            // rebuilding every editor frame.
            if (!_rebuildRequested) return;
            RebuildIfNeeded(false);
        }

        private void OnSplineChanged(Spline changedSpline, int knotIndex,
            SplineModification modification)
        {
            if (!_autoRebuild || !_spline || _spline.Spline == null) return;
            if (ReferenceEquals(_spline.Spline, changedSpline))
                _rebuildRequested = true;
        }

        public void Configure(SplineContainer spline, Material material, float halfWidth,
            float halfDepth, int seed)
        {
            _spline = spline;
            _material = material;
            _halfWidth = Mathf.Max(0.5f, halfWidth);
            _halfDepth = Mathf.Max(0.5f, halfDepth);
            _seed = seed;
            _rebuildRequested = true;
        }

        public void RebuildNow()
        {
            _rebuildRequested = true;
            RebuildIfNeeded(true);
        }

        public void ClearGeneratedMesh()
        {
            ReleaseGeneratedResources();
            Diagnostic = null;
        }

        private void RebuildIfNeeded(bool force)
        {
            if (!isActiveAndEnabled) return;
            if (!force && !_rebuildRequested) return;

            _rebuildRequested = false;
            Diagnostic = null;
            if (!_spline || _spline.Spline == null || _spline.Spline.Count < 2)
            {
                Diagnostic = "Assign a spline with at least two knots.";
                ReleaseGeneratedResources();
                return;
            }

            try
            {
                Mesh replacement = BasaltArchMeshFactory.Create(_spline, transform,
                    _sampleSpacing, _maximumSegments, _crossSectionSides, _halfWidth,
                    _halfDepth, _cornerWear, _surfaceRoughness,
                    _longitudinalVariation, _seed);
                ReleaseGeneratedMeshOnly();
                _generatedMesh = replacement;

                MeshFilter filter = GetComponent<MeshFilter>();
                MeshRenderer renderer = GetComponent<MeshRenderer>();
                filter.sharedMesh = _generatedMesh;
                renderer.sharedMaterial = ResolveMaterial();
                renderer.shadowCastingMode = _castShadows
                    ? ShadowCastingMode.On
                    : ShadowCastingMode.Off;
                renderer.receiveShadows = _receiveShadows;

                MeshCollider collider = GetComponent<MeshCollider>();
                if (_generateCollider)
                {
                    if (!collider) collider = gameObject.AddComponent<MeshCollider>();
                    collider.sharedMesh = _generatedMesh;
                }
                else if (collider && collider.sharedMesh == _generatedMesh)
                {
                    collider.sharedMesh = null;
                }
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                ReleaseGeneratedMeshOnly();
            }
        }

        private Material ResolveMaterial()
        {
            if (_material) return _material;
            if (_generatedMaterial) return _generatedMaterial;
            Shader shader = Shader.Find("Looga/Basalt Column");
            if (!shader)
                throw new InvalidOperationException("The Looga/Basalt Column shader is missing.");
            _generatedMaterial = new Material(shader)
            {
                name = "Generated Looga Basalt Arch Material",
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = true
            };
            return _generatedMaterial;
        }

        private void ReleaseGeneratedMeshOnly()
        {
            MeshFilter filter = GetComponent<MeshFilter>();
            if (filter && filter.sharedMesh == _generatedMesh) filter.sharedMesh = null;
            MeshCollider collider = GetComponent<MeshCollider>();
            if (collider && collider.sharedMesh == _generatedMesh) collider.sharedMesh = null;
            DestroyGeneratedObject(_generatedMesh);
            _generatedMesh = null;
        }

        private void ReleaseGeneratedResources()
        {
            ReleaseGeneratedMeshOnly();
            DestroyGeneratedObject(_generatedMaterial);
            _generatedMaterial = null;
        }

        private static void DestroyGeneratedObject(UnityEngine.Object value)
        {
            if (!value) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }

    public static class BasaltArchMeshFactory
    {
        public static Mesh Create(SplineContainer splineContainer, Transform owner,
            float sampleSpacing, int maximumSegments, int sideCount, float halfWidth,
            float halfDepth, float cornerWear, float surfaceRoughness,
            float longitudinalVariation, int seed)
        {
            if (!splineContainer || splineContainer.Spline == null ||
                splineContainer.Spline.Count < 2)
                throw new ArgumentException("A spline with at least two knots is required.");
            if (!owner) throw new ArgumentNullException(nameof(owner));

            sideCount = Mathf.Clamp(sideCount, 5, 12);
            sampleSpacing = Mathf.Max(2f, sampleSpacing);
            maximumSegments = Mathf.Clamp(maximumSegments, 8, 512);
            halfWidth = Mathf.Max(0.5f, halfWidth);
            halfDepth = Mathf.Max(0.5f, halfDepth);
            cornerWear = Mathf.Clamp(cornerWear, 0f, 0.3f);
            surfaceRoughness = Mathf.Clamp(surfaceRoughness, 0f, 0.25f);
            longitudinalVariation = Mathf.Clamp(longitudinalVariation, 0f, 0.3f);

            float length = splineContainer.CalculateLength();
            if (!float.IsFinite(length) || length <= 0.1f)
                throw new InvalidOperationException("The arch spline has no measurable length.");

            int segmentCount = Mathf.Clamp(Mathf.CeilToInt(length / sampleSpacing),
                8, maximumSegments);
            int ringCount = segmentCount + 1;
            Vector3[] centers = new Vector3[ringCount];
            Vector3[] tangents = new Vector3[ringCount];
            float[] distances = new float[ringCount];

            using (NativeSpline worldSpline = new(splineContainer.Spline,
                       (float4x4)splineContainer.transform.localToWorldMatrix,
                       Allocator.Temp))
            {
                float t = 0f;
                Vector3 previousWorld = (Vector3)worldSpline.EvaluatePosition(0f);
                for (int ring = 0; ring < ringCount; ring++)
                {
                    Vector3 worldPosition;
                    if (ring == 0)
                    {
                        worldPosition = previousWorld;
                    }
                    else if (ring == segmentCount)
                    {
                        t = 1f;
                        worldPosition = (Vector3)worldSpline.EvaluatePosition(1f);
                    }
                    else
                    {
                        worldPosition = (Vector3)worldSpline.GetPointAtLinearDistance(t,
                            length / segmentCount, out t);
                    }

                    if (ring > 0)
                        distances[ring] = distances[ring - 1] +
                            Vector3.Distance(previousWorld, worldPosition);
                    centers[ring] = owner.InverseTransformPoint(worldPosition);
                    Vector3 tangentWorld = (Vector3)worldSpline.EvaluateTangent(t);
                    tangents[ring] = owner.InverseTransformDirection(tangentWorld).normalized;
                    previousWorld = worldPosition;
                }
            }

            Vector3[] sides = new Vector3[ringCount];
            Vector3[] ups = new Vector3[ringCount];
            Vector3 span = centers[ringCount - 1] - centers[0];
            Vector3 initialSide = Vector3.Cross(Vector3.up, span.normalized);
            initialSide -= tangents[0] * Vector3.Dot(initialSide, tangents[0]);
            if (initialSide.sqrMagnitude < 0.001f)
            {
                initialSide = Vector3.Cross(Vector3.forward, tangents[0]);
                if (initialSide.sqrMagnitude < 0.001f)
                    initialSide = Vector3.Cross(Vector3.right, tangents[0]);
            }
            sides[0] = initialSide.normalized;
            ups[0] = Vector3.Cross(tangents[0], sides[0]).normalized;
            for (int ring = 1; ring < ringCount; ring++)
            {
                Quaternion transport = Quaternion.FromToRotation(tangents[ring - 1],
                    tangents[ring]);
                Vector3 side = transport * sides[ring - 1];
                side -= tangents[ring] * Vector3.Dot(side, tangents[ring]);
                if (side.sqrMagnitude < 0.001f) side = sides[ring - 1];
                sides[ring] = side.normalized;
                ups[ring] = Vector3.Cross(tangents[ring], sides[ring]).normalized;
            }

            int pointsPerRing = sideCount * 2;
            Vector3[][] rings = new Vector3[ringCount][];
            for (int ring = 0; ring < ringCount; ring++)
            {
                float normalized = ring / (float)segmentCount;
                float coherent = (Mathf.PerlinNoise(seed * 0.00173f + normalized * 4.7f,
                    seed * 0.0031f) - 0.5f) * 2f;
                float radiusScale = 1f + coherent * longitudinalVariation;
                float endDistance = Mathf.Min(normalized, 1f - normalized);
                float endWear = Mathf.Lerp(0.94f, 1f, Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01(endDistance * 18f)));
                rings[ring] = BuildRing(pointsPerRing, sideCount, centers[ring],
                    sides[ring], ups[ring], halfWidth, halfDepth, cornerWear,
                    surfaceRoughness, radiusScale * endWear, normalized, seed);
            }

            List<Vector3> vertices = new();
            List<Vector3> normals = new();
            List<Vector2> uv = new();
            List<Vector2> masks = new();
            List<Vector4> pillarData = new();
            List<Vector4> contactData = new();
            List<Color> colors = new();
            List<int> triangles = new();
            float shaderSeed = Hash01(seed, 7919, 17);

            for (int face = 0; face < pointsPerRing; face++)
            {
                int next = (face + 1) % pointsPerRing;
                bool chamfer = (face & 1) == 0;
                for (int ring = 0; ring < ringCount; ring++)
                {
                    Vector3 first = rings[ring][face];
                    Vector3 second = rings[ring][next];
                    // BuildRing advances counter-clockwise when viewed along the
                    // spline tangent. The outward side normal is therefore edge x
                    // tangent. The previous tangent x edge order pointed inward.
                    Vector3 faceNormal = Vector3.Cross(second - first,
                        tangents[ring]).normalized;
                    float normalized = ring / (float)segmentCount;
                    float u0 = face / (float)pointsPerRing;
                    float u1 = (face + 1f) / pointsPerRing;
                    float maskStart = chamfer ? 0f : 2f;
                    float maskEnd = chamfer ? 1f : 3f;
                    float endMask = 1f - Mathf.Clamp01(
                        Mathf.Min(normalized, 1f - normalized) * 16f);
                    float wear = Mathf.Max(chamfer ? cornerWear : 0f, endMask);
                    Color color = new(wear, 0f, Hash01(seed, face, ring), 1f);

                    AddVertex(first, faceNormal, new Vector2(u0, distances[ring] * 0.02f),
                        new Vector2(maskStart, 0.5f), shaderSeed, color, vertices,
                        normals, uv, masks, pillarData, contactData, colors);
                    AddVertex(second, faceNormal, new Vector2(u1, distances[ring] * 0.02f),
                        new Vector2(maskEnd, 0.5f), shaderSeed, color, vertices,
                        normals, uv, masks, pillarData, contactData, colors);
                }

                int faceBase = face * ringCount * 2;
                for (int ring = 0; ring < segmentCount; ring++)
                {
                    int a = faceBase + ring * 2;
                    int b = a + 1;
                    int c = a + 2;
                    int d = a + 3;
                    triangles.Add(a); triangles.Add(b); triangles.Add(c);
                    triangles.Add(b); triangles.Add(d); triangles.Add(c);
                }
            }

            AddCap(false, rings[0], centers[0], -tangents[0], shaderSeed, seed,
                vertices, normals, uv, masks, pillarData, contactData, colors, triangles);
            AddCap(true, rings[ringCount - 1], centers[ringCount - 1],
                tangents[ringCount - 1], shaderSeed, seed, vertices, normals, uv,
                masks, pillarData, contactData, colors, triangles);

            Mesh mesh = new()
            {
                name = "Looga Procedural Basalt Arch",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertices.Count > ushort.MaxValue
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16
            };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uv);
            mesh.SetUVs(1, masks);
            mesh.SetUVs(2, pillarData);
            mesh.SetUVs(3, contactData);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Vector3[] BuildRing(int pointCount, int sideCount, Vector3 center,
            Vector3 sideAxis, Vector3 upAxis, float halfWidth, float halfDepth,
            float cornerWear, float roughness, float radiusScale, float normalized,
            int seed)
        {
            Vector2[] corners = new Vector2[sideCount];
            for (int side = 0; side < sideCount; side++)
            {
                float angle = Mathf.PI * 2f * side / sideCount;
                float fixedVariation = (Hash01(seed + 3571, side, 23) - 0.5f) *
                    roughness * 0.75f;
                corners[side] = new Vector2(Mathf.Cos(angle) * halfWidth,
                    Mathf.Sin(angle) * halfDepth) * radiusScale * (1f + fixedVariation);
            }

            Vector3[] points = new Vector3[pointCount];
            for (int side = 0; side < sideCount; side++)
            {
                int previous = (side - 1 + sideCount) % sideCount;
                int next = (side + 1) % sideCount;
                float wearNoise = Hash01(seed + 1907, side, 59);
                float chamfer = Mathf.Lerp(0.018f, 0.14f, cornerWear) *
                    Mathf.Lerp(0.72f, 1.28f, wearNoise);
                Vector2 before = Vector2.Lerp(corners[side], corners[previous], chamfer);
                Vector2 after = Vector2.Lerp(corners[side], corners[next], chamfer);
                float localNoise = (Mathf.PerlinNoise(normalized * 9.1f + side * 0.73f,
                    seed * 0.0029f) - 0.5f) * 2f * roughness;
                before *= 1f + localNoise;
                after *= 1f + localNoise;
                points[side * 2] = center + sideAxis * before.x + upAxis * before.y;
                points[side * 2 + 1] = center + sideAxis * after.x + upAxis * after.y;
            }
            return points;
        }

        private static void AddCap(bool end, IReadOnlyList<Vector3> ring,
            Vector3 center, Vector3 normal, float shaderSeed, int seed,
            List<Vector3> vertices, List<Vector3> normals, List<Vector2> uv,
            List<Vector2> masks, List<Vector4> pillarData,
            List<Vector4> contactData, List<Color> colors, List<int> triangles)
        {
            int centerIndex = vertices.Count;
            AddVertex(center, normal, new Vector2(0.5f, 0.5f),
                new Vector2(end ? -1f : -2f, 0f), shaderSeed,
                new Color(0f, 0f, Hash01(seed, end ? 101 : 97, 0), 1f),
                vertices, normals, uv, masks, pillarData, contactData, colors);
            for (int point = 0; point < ring.Count; point++)
            {
                AddVertex(ring[point], normal, Vector2.zero,
                    new Vector2(end ? -1f : -2f, 1f), shaderSeed,
                    new Color(1f, 0f, Hash01(seed, point, end ? 101 : 97), 1f),
                    vertices, normals, uv, masks, pillarData, contactData, colors);
            }
            for (int point = 0; point < ring.Count; point++)
            {
                int first = centerIndex + 1 + point;
                int second = centerIndex + 1 + (point + 1) % ring.Count;
                triangles.Add(centerIndex);
                triangles.Add(end ? first : second);
                triangles.Add(end ? second : first);
            }
        }

        private static void AddVertex(Vector3 position, Vector3 normal, Vector2 uv0,
            Vector2 mask, float shaderSeed, Color color, List<Vector3> vertices,
            List<Vector3> normals, List<Vector2> uv, List<Vector2> masks,
            List<Vector4> pillarData, List<Vector4> contactData, List<Color> colors)
        {
            vertices.Add(position);
            normals.Add(normal);
            uv.Add(uv0);
            masks.Add(mask);
            pillarData.Add(new Vector4(position.x, position.y, position.z, shaderSeed));
            contactData.Add(-Vector4.one);
            colors.Add(color);
        }

        private static float Hash01(int seed, int a, int b)
        {
            unchecked
            {
                uint hash = (uint)seed;
                hash ^= (uint)a * 0x9E3779B9u;
                hash ^= (uint)b * 0x85EBCA6Bu;
                hash ^= hash >> 16;
                hash *= 0x7FEB352Du;
                hash ^= hash >> 15;
                return (hash & 0x00FFFFFFu) / 16777216f;
            }
        }
    }
}
