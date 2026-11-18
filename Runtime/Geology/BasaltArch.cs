using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Geology
{
    public enum BasaltArchConstruction
    {
        /// <summary>One continuous column along the spline.</summary>
        SolidColumn,
        /// <summary>Bent columns that start at both feet. Outer columns can end before the crown.</summary>
        ColumnBundle,
        /// <summary>Rock blocks: block columns along the arch, cut into curved blocks by staggered joints.</summary>
        Blocks
    }

    /// <summary>
    /// Settings of a basalt arch that is a bundle of bent columns. Half Width and Half Depth of the arch set the
    /// bundle size at the feet. Columns near the bundle center reach the crown and join the columns from the other
    /// foot. Farther out, fewer columns reach the crown, so the arch becomes thinner toward the crown.
    /// </summary>
    [Serializable]
    public sealed class BasaltArchBundleSettings
    {
        [Min(0.5f), Tooltip("Column radius, in meters, from the center to a corner.")]
        public float ColumnRadius = 11f;
        [Range(0f, 0.4f), Tooltip("Random radius growth of each column, as a fraction of its radius. " +
                                  "Larger columns overlap their neighbours.")]
        public float RadiusJitter = 0.12f;
        [Range(-0.15f, 0.3f), Tooltip("Gap between neighbour columns as a fraction of their size. " +
                                      "Negative values make neighbours overlap slightly.")]
        public float JointGap = -0.04f;
        [Range(0f, 1f), Tooltip("Part of the bundle, measured from its center, in which every column reaches the crown. " +
                                "The border of the core changes slowly around the bundle.")]
        public float CoreRadius = 0.3f;
        [Range(0.2f, 4f), Tooltip("How fast the column reach drops outside the core. " +
                                  "Larger values let fewer outer columns reach far toward the crown.")]
        public float CrownReachFalloff = 1.5f;
        [Range(0.02f, 0.5f), Tooltip("Share of the arch length, from the foot, at which the outermost columns end.")]
        public float MinimumReach = 0.1f;
        [Range(0f, 0.2f), Tooltip("Random change of each column end, as a share of the arch length. " +
                                  "It staggers the column ends.")]
        public float ReachJitter = 0.04f;
        [Range(0f, 1f), Tooltip("Extra spread of the columns at the feet, as a fraction of the bundle size.")]
        public float FootSpread = 0.2f;
        [Range(0f, 0.8f), Tooltip("Slow sideways and in-plane bends of the bundle, as a fraction of Half Width. " +
                                  "The feet do not move.")]
        public float CenterWander = 0.15f;
        [Range(0f, 0.5f), Tooltip("Irregularity of the bundle outline.")]
        public float OutlineNoise = 0.2f;

        [Header("Weathering")]
        [Tooltip("Builds each column as stacked drums with worn joints, broken corners, scooped faces and broken " +
                 "tops. Off = smooth columns. The weathered mesh has no tangents.")]
        public bool Weathered;
        [Tooltip("Shortest and longest drum, as multiples of the column diameter.")]
        public Vector2 DrumLength = new(1.8f, 4.2f);
        [Range(0f, 0.4f), Tooltip("Depth of the worn groove at each joint, as a fraction of the column radius.")]
        public float JointWear = 0.1f;
        [Range(0f, 0.2f), Tooltip("Random sideways shift of each drum, as a fraction of the column radius.")]
        public float DrumOffset = 0.05f;
        [Range(0f, 15f), Tooltip("Random twist of each drum around the column axis, in degrees.")]
        public float DrumTwist = 5f;
        [Range(0f, 0.2f), Tooltip("Random radius change of each drum, as a fraction of the column radius.")]
        public float DrumTaper = 0.06f;
        [Range(0f, 1f), Tooltip("Share of drum corners that are broken off along the drum.")]
        public float BrokenCorners = 0.35f;
        [Range(0f, 1f), Tooltip("Share of drums with one face scooped out by spalling.")]
        public float Spalls = 0.3f;
        [Range(0f, 1f), Tooltip("Tilt and roughness of the free column ends.")]
        public float BrokenTops = 0.6f;

        public void Clamp()
        {
            ColumnRadius = Mathf.Max(0.5f, ColumnRadius);
            RadiusJitter = Mathf.Clamp(RadiusJitter, 0f, 0.4f);
            JointGap = Mathf.Clamp(JointGap, -0.15f, 0.3f);
            CoreRadius = Mathf.Clamp01(CoreRadius);
            CrownReachFalloff = Mathf.Clamp(CrownReachFalloff, 0.2f, 4f);
            MinimumReach = Mathf.Clamp(MinimumReach, 0.02f, 0.5f);
            ReachJitter = Mathf.Clamp(ReachJitter, 0f, 0.2f);
            FootSpread = Mathf.Clamp01(FootSpread);
            CenterWander = Mathf.Clamp(CenterWander, 0f, 0.8f);
            OutlineNoise = Mathf.Clamp(OutlineNoise, 0f, 0.5f);
            DrumLength.x = Mathf.Max(0.5f, DrumLength.x);
            DrumLength.y = Mathf.Max(DrumLength.x, DrumLength.y);
            JointWear = Mathf.Clamp(JointWear, 0f, 0.4f);
            DrumOffset = Mathf.Clamp(DrumOffset, 0f, 0.2f);
            DrumTwist = Mathf.Clamp(DrumTwist, 0f, 15f);
            DrumTaper = Mathf.Clamp(DrumTaper, 0f, 0.2f);
            BrokenCorners = Mathf.Clamp01(BrokenCorners);
            Spalls = Mathf.Clamp01(Spalls);
            BrokenTops = Mathf.Clamp01(BrokenTops);
        }
    }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    [SelectionBase]
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

        [Header("Construction")]
        [Tooltip("Solid Column extrudes one column along the spline. Column Bundle builds bent columns from both " +
                 "feet. Half Width and Half Depth then set the bundle size at the feet.")]
        [SerializeField] private BasaltArchConstruction _construction = BasaltArchConstruction.SolidColumn;
        [SerializeField] private BasaltArchBundleSettings _bundle = new();
        [Tooltip("Block settings for the Blocks construction. Half Width and Half Depth set the cross section.")]
        [SerializeField] private RockArchBlocks _blocks = new();
        [Tooltip("Material of the Blocks construction, for example the rock face material.")]
        [SerializeField] private Material _blockMaterial;

        [Header("Tessellation")]
        [SerializeField, Min(2f)] private float _sampleSpacing = 24f;
        [SerializeField, Range(8, 512)] private int _maximumSegments = 256;

        [Header("Surface")]
        [SerializeField] private Material _material;
        [SerializeField] private bool _castShadows = true;
        [SerializeField] private bool _receiveShadows = true;
        [SerializeField, Tooltip("Adds mesh colliders. A block arch gives each chunk a collider from its level 1 mesh. " +
                                 "Leave it off for rock that nothing can reach.")]
        private bool _generateCollider;

        [Header("Rendering")]
        [SerializeField, Min(0f), Tooltip("Size of the render chunks, in meters. Each chunk is a child renderer with " +
                                          "the settings of this renderer, so the camera and shadow views draw only the " +
                                          "chunks in view. 0 = one renderer for the whole arch.")]
        private float _chunkSize = 400f;

        private const string ChunkPrefix = "Basalt Arch Chunk ";
        private Mesh _generatedMesh;
        private Material _generatedMaterial;
        private bool _rebuildRequested;
        // Chunk objects, used or not, and the meshes of the used ones. A chunk object is not destroyed while the arch
        // deactivates or is destroyed (an error in Unity), so the next build reuses it.
        private readonly List<GameObject> _chunks = new();
        private readonly List<Mesh> _chunkMeshes = new();
        private RockFaceOutput _blockOutput;

        public SplineContainer Spline => _spline;
        /// <summary>The whole arch mesh. Null while the arch draws in chunks without a collider.</summary>
        public Mesh GeneratedMesh => _generatedMesh;
        /// <summary>The number of render chunks. 0 when the arch draws as one mesh.</summary>
        public int ChunkCount => _blockOutput?.ChunkCount ?? _chunkMeshes.Count;
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
            AdoptChunks();
            if (AdoptAfterReload()) return;
            _rebuildRequested = true;
            RebuildIfNeeded(true);
        }

        private void OnDisable()
        {
            UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
            // A script reload keeps the meshes. The next domain takes them back if the code did not change.
            if (GeneratedReloadHandover.Unloading && StoreForReload()) return;
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
            _bundle ??= new BasaltArchBundleSettings();
            _bundle.Clamp();
            _sampleSpacing = Mathf.Max(2f, _sampleSpacing);
            _maximumSegments = Mathf.Clamp(_maximumSegments, 8, 512);
            _chunkSize = Mathf.Max(0f, _chunkSize);
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
            using ProfilerMarker.AutoScope build = LoogaGeologyMarkers.BasaltArchBuild.Auto();
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

            if (_construction == BasaltArchConstruction.Blocks)
            {
                BuildBlocks();
                return;
            }
            ReleaseBlocks();
            try
            {
                Mesh replacement = _construction == BasaltArchConstruction.ColumnBundle
                    ? BasaltArchMeshFactory.CreateBundle(_spline, transform, _sampleSpacing,
                        _maximumSegments, _halfWidth, _halfDepth, _cornerWear, _surfaceRoughness,
                        _seed, _bundle ?? new BasaltArchBundleSettings())
                    : BasaltArchMeshFactory.Create(_spline, transform,
                        _sampleSpacing, _maximumSegments, _crossSectionSides, _halfWidth,
                        _halfDepth, _cornerWear, _surfaceRoughness,
                        _longitudinalVariation, _seed);
                ReleaseGeneratedMeshOnly();
                _generatedMesh = replacement;

                MeshFilter filter = GetComponent<MeshFilter>();
                MeshRenderer renderer = GetComponent<MeshRenderer>();
                renderer.sharedMaterial = ResolveMaterial();
                renderer.shadowCastingMode = _castShadows
                    ? ShadowCastingMode.On
                    : ShadowCastingMode.Off;
                renderer.receiveShadows = _receiveShadows;
                if (_chunkSize > 0f)
                {
                    // One renderer covers the whole arch (several kilometers), so the views could not cull any of it.
                    BuildChunks(renderer);
                    filter.sharedMesh = null;
                }
                else
                {
                    filter.sharedMesh = _generatedMesh;
                }

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
                if (_chunkMeshes.Count > 0 && !_generateCollider)
                {
                    DestroyGeneratedObject(_generatedMesh);
                    _generatedMesh = null;
                }
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                ReleaseGeneratedMeshOnly();
            }
        }

        // The block arch draws through its own chunks with levels of detail. Each chunk collides when Generate Collider
        // is on.
        private void BuildBlocks()
        {
            ReleaseGeneratedMeshOnly();
            ReleaseBlocks();
            RockFaceBuilder.Workspace workspace = RockFaceBuilder.RentWorkspace();
            try
            {
                RockArchBlocks blocks = (_blocks ?? new RockArchBlocks()).Copy();
                List<RockFaceStrip> strips = RockArchBuilder.Build(_spline, _halfWidth, _halfDepth, _seed, blocks,
                    workspace);
                _blockOutput = RockFaceOutput.CreateGrouped(strips, name, gameObject.layer,
                    _blockMaterial ? _blockMaterial : ResolveMaterial(),
                    _castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off, blocks.ChunkLength,
                    blocks.LodTransitions, _generateCollider);
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                ReleaseBlocks();
            }
            finally
            {
                RockFaceBuilder.ReturnWorkspace(workspace);
            }
        }

        private void ReleaseBlocks()
        {
            _blockOutput?.Release();
            _blockOutput = null;
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

        // After a script reload, takes the chunk objects of the previous domain.
        private void AdoptChunks()
        {
            if (_chunks.Count > 0) return;
            foreach (Transform child in transform)
            {
                if ((child.gameObject.hideFlags & HideFlags.DontSave) != 0 && child.name.StartsWith(ChunkPrefix) &&
                    child.TryGetComponent(out MeshRenderer _))
                {
                    _chunks.Add(child.gameObject);
                }
            }
        }

        // Child renderers with the settings of the arch renderer, one for each chunk of the mesh. They are visible in
        // the hierarchy, because the Scene view does not pick hidden objects; a click selects the arch.
        private void BuildChunks(MeshRenderer template)
        {
            List<Mesh> meshes = BasaltArchMeshFactory.Split(_generatedMesh, _chunkSize);
            for (int index = 0; index < meshes.Count; index++)
            {
                if (index == _chunks.Count || !_chunks[index])
                {
                    var created = new GameObject(ChunkPrefix + index)
                    {
                        hideFlags = HideFlags.DontSave | HideFlags.NotEditable
                    };
                    created.transform.SetParent(transform, false);
                    created.AddComponent<MeshFilter>();
                    created.AddComponent<MeshRenderer>();
                    if (index == _chunks.Count) _chunks.Add(created);
                    else _chunks[index] = created;
                }
                GameObject chunk = _chunks[index];
                Mesh mesh = meshes[index];
                chunk.layer = gameObject.layer;
                chunk.GetComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
                renderer.enabled = true;
                renderer.sharedMaterial = template.sharedMaterial;
                renderer.shadowCastingMode = template.shadowCastingMode;
                renderer.receiveShadows = template.receiveShadows;
                renderer.staticShadowCaster = template.staticShadowCaster;
                renderer.renderingLayerMask = template.renderingLayerMask;
                renderer.lightProbeUsage = template.lightProbeUsage;
                renderer.reflectionProbeUsage = template.reflectionProbeUsage;
                renderer.motionVectorGenerationMode = template.motionVectorGenerationMode;
                renderer.allowOcclusionWhenDynamic = template.allowOcclusionWhenDynamic;
                _chunkMeshes.Add(mesh);
            }
        }

        private void ReleaseGeneratedMeshOnly()
        {
            // The chunk objects stay without a mesh. See _chunks.
            foreach (GameObject chunk in _chunks)
            {
                if (!chunk) continue;
                chunk.GetComponent<MeshFilter>().sharedMesh = null;
                chunk.GetComponent<MeshRenderer>().enabled = false;
            }
            foreach (Mesh mesh in _chunkMeshes)
            {
                DestroyGeneratedObject(mesh);
            }
            _chunkMeshes.Clear();
            MeshFilter filter = GetComponent<MeshFilter>();
            if (filter && filter.sharedMesh == _generatedMesh) filter.sharedMesh = null;
            MeshCollider collider = GetComponent<MeshCollider>();
            if (collider && collider.sharedMesh == _generatedMesh) collider.sharedMesh = null;
            DestroyGeneratedObject(_generatedMesh);
            _generatedMesh = null;
        }

        private void ReleaseGeneratedResources()
        {
            ReleaseBlocks();
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

        #region Script Reload
        // The generated objects of the last domain, stored for the next domain. Instance IDs stay the same over a
        // script reload. The chunk objects are children of the arch, so AdoptChunks finds them.
        [Serializable]
        private struct ReloadState
        {
            public int GeneratedMesh;
            public int GeneratedMaterial;
            public string Diagnostic;
        }

        private string ReloadKey => "LoogaSoft.Terrain.BasaltArch." + GetInstanceID();

        // Returns false when the meshes are old and must be released.
        private bool StoreForReload()
        {
#if UNITY_EDITOR
            // A requested rebuild did not run yet, so the meshes of this domain are old. A block arch builds again.
            if (_rebuildRequested || _construction == BasaltArchConstruction.Blocks) return false;
            GeneratedReloadHandover.Store(ReloadKey, new ReloadState
            {
                GeneratedMesh = _generatedMesh ? _generatedMesh.GetInstanceID() : 0,
                GeneratedMaterial = _generatedMaterial ? _generatedMaterial.GetInstanceID() : 0,
                Diagnostic = Diagnostic
            });
            return true;
#else
            return false;
#endif
        }

        // Takes the meshes of the last domain after a script reload. Returns false when the arch must build. The
        // meshes are taken also then, so the build destroys them.
        private bool AdoptAfterReload()
        {
#if UNITY_EDITOR
            if (!GeneratedReloadHandover.TryTake(ReloadKey, out ReloadState state)) return false;
            _generatedMesh = GeneratedReloadHandover.Find<Mesh>(state.GeneratedMesh);
            _generatedMaterial = GeneratedReloadHandover.Find<Material>(state.GeneratedMaterial);
            _chunkMeshes.Clear();
            foreach (GameObject chunk in _chunks)
            {
                if (!chunk || !chunk.GetComponent<MeshRenderer>().enabled) continue;
                Mesh mesh = chunk.GetComponent<MeshFilter>().sharedMesh;
                if (mesh) _chunkMeshes.Add(mesh);
            }
            if (!GeneratedReloadHandover.CanAdopt) return false;
            Diagnostic = state.Diagnostic;
            _rebuildRequested = false;
            return true;
#else
            return false;
#endif
        }
        #endregion
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

            ArchFrames frames = SampleFrames(splineContainer, owner, sampleSpacing, maximumSegments);
            int segmentCount = frames.SegmentCount;
            int ringCount = segmentCount + 1;
            Vector3[] centers = frames.Centers;
            Vector3[] tangents = frames.Tangents;
            Vector3[] sides = frames.Sides;
            Vector3[] ups = frames.Ups;
            float[] distances = frames.Distances;

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

        /// <summary>
        /// Builds the arch as a bundle of bent hexagonal columns that start at both feet.
        /// Half width and half depth set the bundle size at the feet.
        /// </summary>
        public static Mesh CreateBundle(SplineContainer splineContainer, Transform owner,
            float sampleSpacing, int maximumSegments, float halfWidth, float halfDepth,
            float cornerWear, float surfaceRoughness, int seed, BasaltArchBundleSettings bundle)
        {
            if (!splineContainer || splineContainer.Spline == null ||
                splineContainer.Spline.Count < 2)
                throw new ArgumentException("A spline with at least two knots is required.");
            if (!owner) throw new ArgumentNullException(nameof(owner));
            if (bundle == null) throw new ArgumentNullException(nameof(bundle));
            sampleSpacing = Mathf.Max(2f, sampleSpacing);
            maximumSegments = Mathf.Clamp(maximumSegments, 8, 512);
            halfWidth = Mathf.Max(0.5f, halfWidth);
            halfDepth = Mathf.Max(0.5f, halfDepth);
            cornerWear = Mathf.Clamp(cornerWear, 0f, 0.3f);
            surfaceRoughness = Mathf.Clamp(surfaceRoughness, 0f, 0.25f);

            ArchFrames frames = SampleFrames(splineContainer, owner, sampleSpacing, maximumSegments);
            int segmentCount = frames.SegmentCount;
            // The bundle axis bends slowly between the feet. The bends fade out toward the feet.
            Vector3[] axis = new Vector3[segmentCount + 1];
            for (int ring = 0; ring <= segmentCount; ring++)
            {
                float normalized = ring / (float)segmentCount;
                float envelope = Mathf.Sin(normalized * Mathf.PI) * bundle.CenterWander * halfWidth;
                float sideBend = (Mathf.PerlinNoise(seed * 0.0041f + normalized * 2.3f, 0.37f) - 0.5f) * 2f;
                float upBend = (Mathf.PerlinNoise(seed * 0.0023f + normalized * 1.7f, 0.71f) - 0.5f) * 2f;
                axis[ring] = frames.Centers[ring] +
                    (frames.Sides[ring] * sideBend + frames.Ups[ring] * upBend * 0.5f) * envelope;
            }

            var buffers = new MeshBuffers();
            List<BundleLane> lanes = BuildLanes(halfWidth, halfDepth, bundle, seed);
            for (int index = 0; index < lanes.Count; index++)
            {
                BundleLane lane = lanes[index];
                float reachA = LaneReach(lane, bundle, seed, index, 0);
                float reachB = LaneReach(lane, bundle, seed, index, 1);
                if (reachA >= 0.5f && reachB >= 0.5f)
                {
                    // Both columns reach the crown, so they form one column from foot to foot.
                    AddBundleColumn(buffers, frames, axis, lane, 0f, 1f, false, false, cornerWear,
                        surfaceRoughness, bundle, seed, index);
                    continue;
                }
                // A column that reaches the crown alone ends a little past it, so the caps do not line up.
                if (reachA >= 0.5f) reachA = 0.5f + Hash01(seed + 4211, index, 3) * 0.05f;
                if (reachB >= 0.5f) reachB = 0.5f + Hash01(seed + 4211, index, 4) * 0.05f;
                AddBundleColumn(buffers, frames, axis, lane, 0f, reachA, false, true, cornerWear,
                    surfaceRoughness, bundle, seed, index);
                AddBundleColumn(buffers, frames, axis, lane, 1f - reachB, 1f, true, false, cornerWear,
                    surfaceRoughness, bundle, seed, index + 7919);
            }

            Mesh mesh = new()
            {
                name = "Looga Procedural Basalt Column Arch",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = IndexFormat.UInt32
            };
            mesh.SetVertices(buffers.Vertices);
            mesh.SetNormals(buffers.Normals);
            mesh.SetUVs(0, buffers.Uv);
            mesh.SetUVs(1, buffers.Masks);
            mesh.SetUVs(2, buffers.PillarData);
            mesh.SetUVs(3, buffers.ContactData);
            mesh.SetColors(buffers.Colors);
            mesh.SetTriangles(buffers.Triangles, 0);
            // The basalt shaders build their own surface frame. The weathered mesh is large, so it skips tangents.
            if (!bundle.Weathered) mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Splits a one-submesh mesh into meshes of the triangles whose centers are in the same cube of the given size.
        /// Vertices on a cube border are copied into each part, so the parts draw the same surface.
        /// </summary>
        public static List<Mesh> Split(Mesh source, float cellSize)
        {
            if (!source) throw new ArgumentNullException(nameof(source));
            if (!(cellSize > 0f)) throw new ArgumentOutOfRangeException(nameof(cellSize));
            Vector3[] vertices = source.vertices;
            Vector3[] normals = source.normals;
            Vector4[] tangents = source.HasVertexAttribute(VertexAttribute.Tangent) ? source.tangents : null;
            Color[] colors = source.HasVertexAttribute(VertexAttribute.Color) ? source.colors : null;
            int[] triangles = source.triangles;
            var uvs2 = new List<Vector2>[4];
            var uvs4 = new List<Vector4>[4];
            for (int channel = 0; channel < 4; channel++)
            {
                var attribute = VertexAttribute.TexCoord0 + channel;
                if (!source.HasVertexAttribute(attribute)) continue;
                if (source.GetVertexAttributeDimension(attribute) == 2)
                {
                    source.GetUVs(channel, uvs2[channel] = new List<Vector2>());
                }
                else
                {
                    source.GetUVs(channel, uvs4[channel] = new List<Vector4>());
                }
            }

            // The triangles of each cube, in source order.
            var cells = new Dictionary<Vector3Int, List<int>>();
            var order = new List<List<int>>();
            for (int triangle = 0; triangle < triangles.Length; triangle += 3)
            {
                Vector3 center = (vertices[triangles[triangle]] + vertices[triangles[triangle + 1]] +
                    vertices[triangles[triangle + 2]]) / (3f * cellSize);
                var cell = new Vector3Int(Mathf.FloorToInt(center.x), Mathf.FloorToInt(center.y),
                    Mathf.FloorToInt(center.z));
                if (!cells.TryGetValue(cell, out List<int> members))
                {
                    cells.Add(cell, members = new List<int>());
                    order.Add(members);
                }
                members.Add(triangle);
            }

            var parts = new List<Mesh>(order.Count);
            // Vertex index in the current part, valid where owner equals the part number.
            var remap = new int[vertices.Length];
            var owner = new int[vertices.Length];
            Array.Fill(owner, -1);
            var used = new List<int>();
            var indices = new List<int>();
            for (int part = 0; part < order.Count; part++)
            {
                used.Clear();
                indices.Clear();
                foreach (int triangle in order[part])
                {
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int vertex = triangles[triangle + corner];
                        if (owner[vertex] != part)
                        {
                            owner[vertex] = part;
                            remap[vertex] = used.Count;
                            used.Add(vertex);
                        }
                        indices.Add(remap[vertex]);
                    }
                }
                var mesh = new Mesh
                {
                    name = source.name + " Chunk " + part,
                    hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = used.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16
                };
                mesh.SetVertices(Gather(vertices, used));
                if (normals.Length == vertices.Length) mesh.SetNormals(Gather(normals, used));
                if (tangents != null) mesh.SetTangents(Gather(tangents, used));
                if (colors != null) mesh.SetColors(Gather(colors, used));
                for (int channel = 0; channel < 4; channel++)
                {
                    if (uvs2[channel] != null) mesh.SetUVs(channel, Gather(uvs2[channel], used));
                    else if (uvs4[channel] != null) mesh.SetUVs(channel, Gather(uvs4[channel], used));
                }
                mesh.SetTriangles(indices, 0);
                mesh.RecalculateBounds();
                parts.Add(mesh);
            }
            return parts;
        }

        private static List<T> Gather<T>(IReadOnlyList<T> values, List<int> used)
        {
            var result = new List<T>(used.Count);
            foreach (int index in used)
            {
                result.Add(values[index]);
            }
            return result;
        }

        private struct ArchFrames
        {
            public int SegmentCount;
            public Vector3[] Centers;
            public Vector3[] Tangents;
            public Vector3[] Sides;
            public Vector3[] Ups;
            public float[] Distances;
        }

        private struct BundleLane
        {
            // Offset from the bundle axis along the side and up axes, in meters.
            public Vector2 Offset;
            // Distance from the axis relative to the bundle outline. 0 = center, 1 = outline.
            public float Radial;
            // Direction of the lane around the axis, in radians.
            public float Angle;
            public float Radius;
        }

        private sealed class MeshBuffers
        {
            public readonly List<Vector3> Vertices = new();
            public readonly List<Vector3> Normals = new();
            public readonly List<Vector2> Uv = new();
            public readonly List<Vector2> Masks = new();
            public readonly List<Vector4> PillarData = new();
            public readonly List<Vector4> ContactData = new();
            public readonly List<Color32> Colors = new();
            public readonly List<int> Triangles = new();
        }

        // One ring of a weathered column: its place along the arch and the drum it belongs to.
        private struct DrumStation
        {
            public float Distance;
            public int Drum;
            // 1 = groove ring at a joint, 0 = full drum section.
            public float Inset;
            // 1 = free end ring, 0 = elsewhere.
            public float FreeEnd;
        }

        // Samples the spline at equal distances and builds a twist-free frame at each sample.
        private static ArchFrames SampleFrames(SplineContainer splineContainer, Transform owner,
            float sampleSpacing, int maximumSegments)
        {
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

            return new ArchFrames
            {
                SegmentCount = segmentCount,
                Centers = centers,
                Tangents = tangents,
                Sides = sides,
                Ups = ups,
                Distances = distances
            };
        }

        // Places the column centers on a hexagonal lattice inside the elliptic bundle outline.
        // The lattice matches the corner angles of AddBundleColumn, so neighbour columns share flat faces.
        private static List<BundleLane> BuildLanes(float halfWidth, float halfDepth,
            BasaltArchBundleSettings bundle, int seed)
        {
            float radius = Mathf.Max(0.5f, bundle.ColumnRadius);
            float spacingX = Mathf.Sqrt(3f) * radius * (1f + bundle.JointGap);
            float spacingY = 1.5f * radius * (1f + bundle.JointGap);
            // The column centers stay one radius inside the outline.
            float innerWidth = Mathf.Max(0.01f, halfWidth - radius);
            float innerDepth = Mathf.Max(0.01f, halfDepth - radius);
            float limitScale = 1f + bundle.OutlineNoise;
            int rows = Mathf.CeilToInt(innerDepth * limitScale / spacingY) + 1;
            int columns = Mathf.CeilToInt(innerWidth * limitScale / spacingX) + 1;
            List<BundleLane> lanes = new();
            for (int row = -rows; row <= rows; row++)
            {
                for (int column = -columns; column <= columns; column++)
                {
                    float x = (column + ((row & 1) != 0 ? 0.5f : 0f)) * spacingX;
                    float y = row * spacingY;
                    float radial = Mathf.Sqrt(x * x / (innerWidth * innerWidth) + y * y / (innerDepth * innerDepth));
                    float angle = Mathf.Atan2(y / innerDepth, x / innerWidth);
                    float outline = 1f + (Mathf.PerlinNoise(seed * 0.0037f + Mathf.Cos(angle) * 1.3f,
                        Mathf.Sin(angle) * 1.3f + 0.53f) - 0.5f) * 2f * bundle.OutlineNoise;
                    if (radial > outline) continue;
                    int key = (row + 1024) * 4096 + column + 2048;
                    // Columns only grow from the lattice size, so a larger column overlaps its neighbours
                    // instead of leaving a gap.
                    lanes.Add(new BundleLane
                    {
                        Offset = new Vector2(x, y),
                        Radial = Mathf.Clamp01(radial / Mathf.Max(0.01f, outline)),
                        Angle = angle,
                        Radius = radius * (1f + Hash01(seed + 811, key, 5) * bundle.RadiusJitter)
                    });
                }
            }
            if (lanes.Count == 0)
                lanes.Add(new BundleLane { Offset = Vector2.zero, Radial = 0f, Angle = 0f, Radius = radius });
            return lanes;
        }

        // Share of the arch length that the column of one lane covers from its foot. 0.5 or more reaches the crown.
        // The reach is a smooth field around the bundle, so the ends step down from the core to the outline and no
        // single outer column reaches the crown on its own.
        private static float LaneReach(BundleLane lane, BasaltArchBundleSettings bundle, int seed, int index, int foot)
        {
            float coreNoise = (Mathf.PerlinNoise(seed * 0.0053f + foot * 3.1f + Mathf.Cos(lane.Angle) * 0.9f,
                Mathf.Sin(lane.Angle) * 0.9f + 0.29f) - 0.5f) * 2f;
            float core = Mathf.Clamp(bundle.CoreRadius * (1f + coreNoise * 0.35f), 0f, 0.98f);
            float jitter = (Hash01(seed + 3301, index, foot) - 0.5f) * 2f * bundle.ReachJitter;
            if (lane.Radial <= core) return 0.5f;
            float outer = Mathf.Clamp01((lane.Radial - core) / (1f - core));
            float reach = Mathf.Lerp(0.5f, bundle.MinimumReach, Mathf.Pow(outer, 1f / bundle.CrownReachFalloff)) + jitter;
            return Mathf.Min(Mathf.Max(bundle.MinimumReach * 0.5f, reach), 0.49f);
        }

        // Adds one bent hexagonal column between two shares of the arch length.
        private static void AddBundleColumn(MeshBuffers buffers, ArchFrames frames, Vector3[] axis,
            BundleLane lane, float start, float end, bool freeStart, bool freeEnd, float cornerWear,
            float roughness, BasaltArchBundleSettings bundle, int seed, int columnKey)
        {
            const int sideCount = 6;
            const int pointsPerRing = sideCount * 2;
            int segmentCount = frames.SegmentCount;
            float startIndex = Mathf.Clamp(start, 0f, 1f) * segmentCount;
            float endIndex = Mathf.Clamp(end, 0f, 1f) * segmentCount;
            if (endIndex - startIndex < 0.05f) return;
            if (bundle.Weathered)
            {
                AddWeatheredColumn(buffers, frames, axis, lane, startIndex, endIndex, freeStart, freeEnd, cornerWear,
                    roughness, bundle, seed, columnKey);
                return;
            }

            // Ring positions: the exact start and end, and every spline sample between them.
            List<float> positions = new() { startIndex };
            for (int ring = Mathf.FloorToInt(startIndex) + 1; ring < endIndex; ring++)
            {
                if (ring - startIndex > 0.05f && endIndex - ring > 0.05f) positions.Add(ring);
            }
            positions.Add(endIndex);

            float pillarSeed = Hash01(seed + 5501, columnKey, 11);
            var rings = new Vector3[positions.Count][];
            var ringCenters = new Vector3[positions.Count];
            var ringTangents = new Vector3[positions.Count];
            var ringDistances = new float[positions.Count];
            for (int ring = 0; ring < positions.Count; ring++)
            {
                float index = positions[ring];
                int lower = Mathf.Min(Mathf.FloorToInt(index), segmentCount - 1);
                float blend = index - lower;
                Vector3 tangent = Vector3.Lerp(frames.Tangents[lower], frames.Tangents[lower + 1], blend).normalized;
                Vector3 side = Vector3.Lerp(frames.Sides[lower], frames.Sides[lower + 1], blend);
                side = (side - tangent * Vector3.Dot(side, tangent)).normalized;
                Vector3 up = Vector3.Cross(tangent, side).normalized;
                Vector3 center = Vector3.Lerp(axis[lower], axis[lower + 1], blend);
                float normalized = index / segmentCount;
                // The columns spread apart and grow at the feet.
                float footFactor = 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01(Mathf.Min(normalized, 1f - normalized) / 0.12f));
                float spread = 1f + bundle.FootSpread * footFactor * footFactor;
                Vector3 laneCenter = center + (side * lane.Offset.x + up * lane.Offset.y) * spread;
                ringCenters[ring] = laneCenter;
                ringTangents[ring] = tangent;
                ringDistances[ring] = Mathf.Lerp(frames.Distances[lower], frames.Distances[lower + 1], blend);
                rings[ring] = BuildColumnRing(laneCenter, side, up, lane.Radius * spread, cornerWear,
                    roughness, seed, columnKey, normalized);
            }

            float startDistance = ringDistances[0];
            float endDistance = ringDistances[positions.Count - 1];
            float edgeLength = Mathf.Max(0.5f, lane.Radius * 1.2f);
            for (int face = 0; face < pointsPerRing; face++)
            {
                int next = (face + 1) % pointsPerRing;
                bool chamfer = (face & 1) == 0;
                int faceBase = buffers.Vertices.Count;
                for (int ring = 0; ring < positions.Count; ring++)
                {
                    Vector3 first = rings[ring][face];
                    Vector3 second = rings[ring][next];
                    Vector3 normal = Vector3.Cross(second - first, ringTangents[ring]).normalized;
                    // Distance to the nearest free end. The shader lights the rim of a free end like a pillar top.
                    float toFreeEnd = float.MaxValue;
                    if (freeStart) toFreeEnd = ringDistances[ring] - startDistance;
                    if (freeEnd) toFreeEnd = Mathf.Min(toFreeEnd, endDistance - ringDistances[ring]);
                    float endMask = 1f - Mathf.Clamp01(toFreeEnd / edgeLength);
                    float height = Mathf.Lerp(0.5f, 1f, endMask);
                    float wear = Mathf.Max(chamfer ? cornerWear : 0f, endMask);
                    Color color = new(wear, 0f, Hash01(seed + columnKey, face, ring), 1f);
                    float v = ringDistances[ring] * 0.02f;
                    AddVertex(first, normal, new Vector2(face / (float)pointsPerRing, v),
                        new Vector2(chamfer ? 0f : 2f, height), pillarSeed, color, buffers);
                    AddVertex(second, normal, new Vector2((face + 1f) / pointsPerRing, v),
                        new Vector2(chamfer ? 1f : 3f, height), pillarSeed, color, buffers);
                }
                for (int ring = 0; ring < positions.Count - 1; ring++)
                {
                    int a = faceBase + ring * 2;
                    int b = a + 1;
                    int c = a + 2;
                    int d = a + 3;
                    buffers.Triangles.Add(a); buffers.Triangles.Add(b); buffers.Triangles.Add(c);
                    buffers.Triangles.Add(b); buffers.Triangles.Add(d); buffers.Triangles.Add(c);
                }
            }

            int last = positions.Count - 1;
            AddColumnCap(buffers, rings[0], ringCenters[0], -ringTangents[0], freeStart, pillarSeed, seed,
                columnKey);
            AddColumnCap(buffers, rings[last], ringCenters[last], ringTangents[last], freeEnd, pillarSeed, seed,
                columnKey + 1);
        }

        // A weathered column: stacked drums, each with a small shift, twist and radius change. Worn grooves mark the
        // joints. Some drum corners are broken off, some faces are scooped out, and the free ends are tilted and rough.
        private static void AddWeatheredColumn(MeshBuffers buffers, ArchFrames frames, Vector3[] axis,
            BundleLane lane, float startIndex, float endIndex, bool freeStart, bool freeEnd, float cornerWear,
            float roughness, BasaltArchBundleSettings bundle, int seed, int columnKey)
        {
            const int sideCount = 6;
            const int pointsPerRing = sideCount * 2;
            float radius = lane.Radius;
            float startDistance = DistanceAtIndex(frames, startIndex);
            float endDistance = DistanceAtIndex(frames, endIndex);
            float sampleSpacing = frames.Distances[frames.SegmentCount] / frames.SegmentCount;
            List<Vector2> drums = SplitIntoDrums(startDistance, endDistance, 2f * radius, bundle, seed, columnKey);
            List<DrumStation> stations = BuildDrumStations(drums, radius, sampleSpacing, freeStart, freeEnd);

            int ringCount = stations.Count;
            var rings = new Vector3[ringCount][];
            var ringCenters = new Vector3[ringCount];
            var ringTangents = new Vector3[ringCount];
            var chippedCorners = new bool[drums.Count * sideCount];
            for (int drum = 0; drum < drums.Count; drum++)
            {
                for (int corner = 0; corner < sideCount; corner++)
                {
                    chippedCorners[drum * sideCount + corner] =
                        Hash01(seed + 6131 + columnKey, drum, corner) < bundle.BrokenCorners;
                }
            }

            for (int ring = 0; ring < ringCount; ring++)
            {
                DrumStation station = stations[ring];
                int drum = station.Drum;
                float index = IndexAtDistance(frames, station.Distance);
                int lower = Mathf.Min(Mathf.FloorToInt(index), frames.SegmentCount - 1);
                float blend = index - lower;
                Vector3 tangent = Vector3.Lerp(frames.Tangents[lower], frames.Tangents[lower + 1], blend).normalized;
                Vector3 side = Vector3.Lerp(frames.Sides[lower], frames.Sides[lower + 1], blend);
                side = (side - tangent * Vector3.Dot(side, tangent)).normalized;
                Vector3 up = Vector3.Cross(tangent, side).normalized;
                float normalized = index / frames.SegmentCount;
                float footFactor = 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01(Mathf.Min(normalized, 1f - normalized) / 0.12f));
                float spread = 1f + bundle.FootSpread * footFactor * footFactor;

                // Each drum keeps one shift, twist and radius over its length.
                Vector2 shift = new Vector2(Hash01(seed + 6203 + columnKey, drum, 1) - 0.5f,
                    Hash01(seed + 6203 + columnKey, drum, 2) - 0.5f) * (2f * bundle.DrumOffset * radius);
                float twist = (Hash01(seed + 6203 + columnKey, drum, 3) - 0.5f) * 2f * bundle.DrumTwist *
                    Mathf.Deg2Rad;
                float drumRadius = radius * spread *
                    (1f + (Hash01(seed + 6203 + columnKey, drum, 4) - 0.5f) * 2f * bundle.DrumTaper);
                Vector3 drumSide = side * Mathf.Cos(twist) + up * Mathf.Sin(twist);
                Vector3 drumUp = Vector3.Cross(tangent, drumSide).normalized;
                Vector3 center = Vector3.Lerp(axis[lower], axis[lower + 1], blend) +
                    (side * lane.Offset.x + up * lane.Offset.y) * spread + side * shift.x + up * shift.y;

                int spalledFace = Hash01(seed + 6311 + columnKey, drum, 5) < bundle.Spalls
                    ? Mathf.FloorToInt(Hash01(seed + 6311 + columnKey, drum, 6) * sideCount) % sideCount
                    : -1;
                float spallDepth = Mathf.Lerp(0.1f, 0.22f, Hash01(seed + 6311 + columnKey, drum, 7));
                bool interior = station.Inset <= 0f && station.FreeEnd <= 0f;

                var corners = new Vector2[sideCount];
                for (int corner = 0; corner < sideCount; corner++)
                {
                    float angle = Mathf.PI / 6f + Mathf.PI / 3f * corner;
                    float fixedVariation = (Hash01(seed + 3571 + columnKey, corner, 23) - 0.5f) * roughness * 0.9f;
                    float slowVariation = (Mathf.PerlinNoise(normalized * 9.1f + corner * 0.73f,
                        (seed + columnKey) * 0.0029f) - 0.5f) * roughness;
                    // The joint groove is uneven around the column.
                    float inset = station.Inset * bundle.JointWear * Mathf.Lerp(0.6f, 1.4f,
                        Hash01(seed + 6421 + columnKey, drum * 8 + corner, ring & 1));
                    corners[corner] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * drumRadius *
                        (1f + fixedVariation + slowVariation - inset);
                }

                var points = new Vector3[pointsPerRing];
                for (int corner = 0; corner < sideCount; corner++)
                {
                    int previous = (corner - 1 + sideCount) % sideCount;
                    int next = (corner + 1) % sideCount;
                    float chamfer = Mathf.Lerp(0.025f, 0.14f, cornerWear) *
                        Mathf.Lerp(0.65f, 1.35f, Hash01(seed + 1907 + columnKey, corner, 59));
                    // A broken corner is cut back along the drum and less at its joints.
                    if (chippedCorners[drum * sideCount + corner])
                    {
                        chamfer += Mathf.Lerp(0.18f, 0.34f, Hash01(seed + 6521 + columnKey, drum, corner)) *
                            (interior ? 1f : 0.55f);
                    }
                    chamfer = Mathf.Min(chamfer, 0.45f);
                    Vector2 before = Vector2.Lerp(corners[corner], corners[previous], chamfer);
                    Vector2 after = Vector2.Lerp(corners[corner], corners[next], chamfer);
                    // A spall scoops the face between this corner and the next one inside the drum.
                    if (interior && spalledFace == corner) after *= 1f - spallDepth;
                    if (interior && spalledFace == previous) before *= 1f - spallDepth;
                    points[corner * 2] = center + drumSide * before.x + drumUp * before.y;
                    points[corner * 2 + 1] = center + drumSide * after.x + drumUp * after.y;
                }

                if (station.FreeEnd > 0f)
                {
                    // The start ring moves forward into the column and the end ring moves back into it, never past
                    // the next ring.
                    int neighbour = ring == 0 ? Mathf.Min(1, ringCount - 1) : ring - 1;
                    float room = 0.8f * Mathf.Abs(stations[neighbour].Distance - station.Distance);
                    BreakFreeEnd(points, center, tangent, ring == 0 ? 1f : -1f,
                        Mathf.Min(0.7f * radius * bundle.BrokenTops, room), seed, columnKey + ring);
                }
                rings[ring] = points;
                ringCenters[ring] = center;
                ringTangents[ring] = tangent;
            }

            float pillarSeed = Hash01(seed + 5501, columnKey, 11);
            float edgeLength = Mathf.Max(0.5f, radius * 1.2f);
            var quadNormals = new Vector3[Mathf.Max(1, ringCount - 1)];
            for (int face = 0; face < pointsPerRing; face++)
            {
                int next = (face + 1) % pointsPerRing;
                bool chamferFace = (face & 1) == 0;
                int corner = face / 2;
                // Normals follow the surface along the column, so the joint grooves and scoops shade smoothly.
                for (int ring = 0; ring < ringCount - 1; ring++)
                {
                    Vector3 around = rings[ring][next] - rings[ring][face] + rings[ring + 1][next] -
                        rings[ring + 1][face];
                    Vector3 along = rings[ring + 1][face] - rings[ring][face] + rings[ring + 1][next] -
                        rings[ring][next];
                    quadNormals[ring] = Vector3.Cross(around, along);
                }

                int faceBase = buffers.Vertices.Count;
                for (int ring = 0; ring < ringCount; ring++)
                {
                    DrumStation station = stations[ring];
                    Vector3 first = rings[ring][face];
                    Vector3 second = rings[ring][next];
                    Vector3 normal = (ring > 0 ? quadNormals[ring - 1] : Vector3.zero) +
                        (ring < ringCount - 1 ? quadNormals[ring] : Vector3.zero);
                    normal = normal.sqrMagnitude > 1e-10f
                        ? normal.normalized
                        : Vector3.Cross(second - first, ringTangents[ring]).normalized;
                    float toFreeEnd = float.MaxValue;
                    if (freeStart) toFreeEnd = station.Distance - startDistance;
                    if (freeEnd) toFreeEnd = Mathf.Min(toFreeEnd, endDistance - station.Distance);
                    float endMask = 1f - Mathf.Clamp01(toFreeEnd / edgeLength);
                    float height = Mathf.Lerp(0.5f, 1f, endMask);
                    bool chipped = chamferFace && chippedCorners[station.Drum * sideCount + corner];
                    float wear = Mathf.Max(Mathf.Max(chamferFace ? cornerWear : 0f, endMask),
                        Mathf.Max(station.Inset > 0f ? 0.5f : 0f, chipped ? 0.6f : 0f));
                    // The shader darkens the green channel as a crack, so the joint grooves read as joints.
                    float crack = station.Inset > 0f ? 0.45f : 0f;
                    Color color = new(wear, crack, Hash01(seed + columnKey, face, station.Drum), 1f);
                    float v = station.Distance * 0.02f;
                    AddVertex(first, normal, new Vector2(face / (float)pointsPerRing, v),
                        new Vector2(chamferFace ? 0f : 2f, height), pillarSeed, color, buffers);
                    AddVertex(second, normal, new Vector2((face + 1f) / pointsPerRing, v),
                        new Vector2(chamferFace ? 1f : 3f, height), pillarSeed, color, buffers);
                }
                for (int ring = 0; ring < ringCount - 1; ring++)
                {
                    int a = faceBase + ring * 2;
                    int b = a + 1;
                    int c = a + 2;
                    int d = a + 3;
                    buffers.Triangles.Add(a); buffers.Triangles.Add(b); buffers.Triangles.Add(c);
                    buffers.Triangles.Add(b); buffers.Triangles.Add(d); buffers.Triangles.Add(c);
                }
            }

            int last = ringCount - 1;
            AddColumnCap(buffers, rings[0], CapCenter(rings[0], ringCenters[0], -ringTangents[0], freeStart, radius,
                bundle.BrokenTops, seed, columnKey), -ringTangents[0], freeStart, pillarSeed, seed, columnKey);
            AddColumnCap(buffers, rings[last], CapCenter(rings[last], ringCenters[last], ringTangents[last], freeEnd,
                radius, bundle.BrokenTops, seed, columnKey + 1), ringTangents[last], freeEnd, pillarSeed, seed,
                columnKey + 1);
        }

        // Drum boundaries along a column, in meters along the arch. The last drum is never very short.
        private static List<Vector2> SplitIntoDrums(float startDistance, float endDistance, float diameter,
            BasaltArchBundleSettings bundle, int seed, int columnKey)
        {
            List<Vector2> drums = new();
            float cursor = startDistance;
            float shortest = bundle.DrumLength.x * diameter;
            while (cursor < endDistance - 0.01f)
            {
                float length = Mathf.Lerp(bundle.DrumLength.x, bundle.DrumLength.y,
                    Hash01(seed + 6007 + columnKey, drums.Count, 1)) * diameter;
                float next = cursor + length;
                if (endDistance - next < shortest * 0.5f) next = endDistance;
                drums.Add(new Vector2(cursor, next));
                cursor = next;
            }
            return drums;
        }

        // Rings of a weathered column: a groove ring on each side of every joint, a full ring a short ramp inside
        // each drum end, and interior rings no farther apart than the arch samples.
        private static List<DrumStation> BuildDrumStations(List<Vector2> drums, float radius, float sampleSpacing,
            bool freeStart, bool freeEnd)
        {
            List<DrumStation> stations = new();
            float gap = 0.04f * radius;
            for (int drum = 0; drum < drums.Count; drum++)
            {
                float a = drums[drum].x;
                float b = drums[drum].y;
                float length = b - a;
                bool columnStart = drum == 0;
                bool columnEnd = drum == drums.Count - 1;
                float ramp = Mathf.Min(0.35f * radius, 0.25f * length);
                float freeRamp = Mathf.Min(0.8f * radius, 0.4f * length);
                float startRamp = columnStart ? (freeStart ? freeRamp : 0f) : ramp;
                float endRamp = columnEnd ? (freeEnd ? freeRamp : 0f) : ramp;

                if (columnStart)
                {
                    stations.Add(new DrumStation { Distance = a, Drum = drum, FreeEnd = freeStart ? 1f : 0f });
                }
                else
                {
                    stations.Add(new DrumStation { Distance = a + gap, Drum = drum, Inset = 1f });
                }
                if (startRamp > 0f) stations.Add(new DrumStation { Distance = a + startRamp, Drum = drum });

                float innerStart = a + startRamp;
                float innerEnd = b - endRamp;
                int interior = Mathf.Max(1, Mathf.CeilToInt((innerEnd - innerStart) / sampleSpacing));
                if (interior == 1)
                {
                    stations.Add(new DrumStation { Distance = 0.5f * (innerStart + innerEnd), Drum = drum });
                }
                for (int step = 1; step < interior; step++)
                {
                    stations.Add(new DrumStation
                    {
                        Distance = Mathf.Lerp(innerStart, innerEnd, step / (float)interior),
                        Drum = drum
                    });
                }

                if (endRamp > 0f) stations.Add(new DrumStation { Distance = b - endRamp, Drum = drum });
                if (columnEnd)
                {
                    stations.Add(new DrumStation { Distance = b, Drum = drum, FreeEnd = freeEnd ? 1f : 0f });
                }
                else
                {
                    stations.Add(new DrumStation { Distance = b - gap, Drum = drum, Inset = 1f });
                }
            }

            // A short drum can place rings on top of each other. Keep the first ring at each place.
            List<DrumStation> ordered = new();
            foreach (DrumStation station in stations)
            {
                if (ordered.Count > 0 && station.Distance <= ordered[ordered.Count - 1].Distance + 0.01f) continue;
                ordered.Add(station);
            }
            return ordered;
        }

        // Tilts and roughens a free column end. Points move back into the column by at most the given depth.
        private static void BreakFreeEnd(Vector3[] points, Vector3 center, Vector3 tangent, float inward,
            float depth, int seed, int key)
        {
            float tiltAngle = Hash01(seed + 6607, key, 1) * Mathf.PI * 2f;
            Vector3 reference = Mathf.Abs(Vector3.Dot(tangent, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right;
            Vector3 first = Vector3.Cross(tangent, reference).normalized;
            Vector3 second = Vector3.Cross(tangent, first);
            Vector3 tiltDirection = first * Mathf.Cos(tiltAngle) + second * Mathf.Sin(tiltAngle);
            for (int point = 0; point < points.Length; point++)
            {
                Vector3 offset = points[point] - center;
                float along = 0.5f + 0.5f * Vector3.Dot(offset.normalized, tiltDirection);
                float jag = Hash01(seed + 6701, key, point);
                float back = depth * Mathf.Clamp01(along * 0.75f + jag * 0.25f);
                points[point] += tangent * (inward * back);
            }
        }

        // Center of an end cap. A broken free end gets a raised or sunken center, so its top is not flat.
        private static Vector3 CapCenter(Vector3[] ring, Vector3 center, Vector3 outward, bool free, float radius,
            float strength, int seed, int key)
        {
            if (!free) return center;
            Vector3 average = Vector3.zero;
            foreach (Vector3 point in ring) average += point;
            average /= ring.Length;
            float bump = (Hash01(seed + 6803, key, 1) - 0.4f) * 0.35f * radius * strength;
            return average + outward.normalized * bump;
        }

        private static float DistanceAtIndex(ArchFrames frames, float index)
        {
            int lower = Mathf.Clamp(Mathf.FloorToInt(index), 0, frames.SegmentCount - 1);
            return Mathf.Lerp(frames.Distances[lower], frames.Distances[lower + 1], index - lower);
        }

        private static float IndexAtDistance(ArchFrames frames, float distance)
        {
            float[] distances = frames.Distances;
            int low = 0;
            int high = frames.SegmentCount;
            if (distance <= distances[0]) return 0f;
            if (distance >= distances[high]) return high;
            while (high - low > 1)
            {
                int middle = (low + high) / 2;
                if (distances[middle] <= distance) low = middle;
                else high = middle;
            }
            float span = Mathf.Max(1e-5f, distances[low + 1] - distances[low]);
            return low + (distance - distances[low]) / span;
        }

        // A hexagonal ring with worn corners. The corners sit at 30 + 60 * n degrees, which matches the lattice.
        private static Vector3[] BuildColumnRing(Vector3 center, Vector3 sideAxis, Vector3 upAxis,
            float radius, float cornerWear, float roughness, int seed, int columnKey, float normalized)
        {
            const int sideCount = 6;
            Vector2[] corners = new Vector2[sideCount];
            for (int corner = 0; corner < sideCount; corner++)
            {
                float angle = Mathf.PI / 6f + Mathf.PI / 3f * corner;
                float fixedVariation = (Hash01(seed + 3571 + columnKey, corner, 23) - 0.5f) * roughness * 0.9f;
                float slowVariation = (Mathf.PerlinNoise(normalized * 9.1f + corner * 0.73f,
                    (seed + columnKey) * 0.0029f) - 0.5f) * roughness;
                corners[corner] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius *
                    (1f + fixedVariation + slowVariation);
            }

            Vector3[] points = new Vector3[sideCount * 2];
            for (int corner = 0; corner < sideCount; corner++)
            {
                int previous = (corner - 1 + sideCount) % sideCount;
                int next = (corner + 1) % sideCount;
                float chamfer = Mathf.Lerp(0.025f, 0.14f, cornerWear) *
                    Mathf.Lerp(0.65f, 1.35f, Hash01(seed + 1907 + columnKey, corner, 59));
                Vector2 before = Vector2.Lerp(corners[corner], corners[previous], chamfer);
                Vector2 after = Vector2.Lerp(corners[corner], corners[next], chamfer);
                points[corner * 2] = center + sideAxis * before.x + upAxis * before.y;
                points[corner * 2 + 1] = center + sideAxis * after.x + upAxis * after.y;
            }
            return points;
        }

        // A flat end face. A free end gets the pillar-top marker, so the shader lights its rim.
        private static void AddColumnCap(MeshBuffers buffers, IReadOnlyList<Vector3> ring, Vector3 center,
            Vector3 normal, bool free, float pillarSeed, int seed, int columnKey)
        {
            float marker = free ? -1f : -2f;
            // A broken end is not flat. Its normal comes from the cap triangles and keeps the side of the given one.
            Vector3 geometric = Vector3.zero;
            for (int point = 0; point < ring.Count; point++)
            {
                geometric += Vector3.Cross(ring[point] - center, ring[(point + 1) % ring.Count] - center);
            }
            if (geometric.sqrMagnitude > 1e-10f)
            {
                normal = Vector3.Dot(geometric, normal) >= 0f ? geometric.normalized : -geometric.normalized;
            }
            int centerIndex = buffers.Vertices.Count;
            AddVertex(center, normal, new Vector2(0.5f, 0.5f), new Vector2(marker, 0f), pillarSeed,
                new Color(0f, 0f, Hash01(seed + columnKey, 97, 0), 1f), buffers);
            for (int point = 0; point < ring.Count; point++)
            {
                AddVertex(ring[point], normal, Vector2.zero, new Vector2(marker, 1f), pillarSeed,
                    new Color(1f, 0f, Hash01(seed + columnKey, point, 97), 1f), buffers);
            }
            // Unity draws the side where Cross(b - a, c - a) points. Keep that side on the normal.
            bool alongNormal = Vector3.Dot(Vector3.Cross(ring[1] - ring[0], ring[2] - ring[0]), normal) > 0f;
            for (int point = 0; point < ring.Count; point++)
            {
                int first = centerIndex + 1 + point;
                int second = centerIndex + 1 + (point + 1) % ring.Count;
                buffers.Triangles.Add(centerIndex);
                buffers.Triangles.Add(alongNormal ? first : second);
                buffers.Triangles.Add(alongNormal ? second : first);
            }
        }

        private static void AddVertex(Vector3 position, Vector3 normal, Vector2 uv0, Vector2 mask,
            float pillarSeed, Color color, MeshBuffers buffers)
        {
            buffers.Vertices.Add(position);
            buffers.Normals.Add(normal);
            buffers.Uv.Add(uv0);
            buffers.Masks.Add(mask);
            buffers.PillarData.Add(new Vector4(position.x, position.y, position.z, pillarSeed));
            buffers.ContactData.Add(-Vector4.one);
            buffers.Colors.Add(color);
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
