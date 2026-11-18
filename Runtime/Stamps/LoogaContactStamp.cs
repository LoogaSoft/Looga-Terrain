using System;
using System.Collections.Generic;
using LoogaSoft.Terrain.Geology;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Raises the terrain where generated rock stands on it, so the ground builds up against the rock. Next to the rock
    /// the ground rises at the contact angle. It falls back to the terrain at the reach. The stamp renders the rock onto
    /// the height grid to find the ground that the rock stands on, so the ramp follows each block and corner of the rock.
    /// </summary>
    /// <remarks>
    /// Under the rock the ground rises by the full ramp height, so the ramp has no step at the rock edge. After the
    /// heights settle, the stamp builds its rock again, so the ground blend of the rock reads the raised ground.
    /// </remarks>
    [AddComponentMenu("Looga/Stamps/Contact Stamp")]
    public sealed class LoogaContactStamp : LoogaNeighbourhoodStamp
    {
        private const string ComputeName = "LoogaContact";
        private const string FootprintShaderName = "LoogaContactFootprint";

        private static readonly int FootprintRectId = Shader.PropertyToID("_FootprintRect");
        private static readonly int GroundApronId = Shader.PropertyToID("_GroundApron");
        private static readonly int ApronInId = Shader.PropertyToID("_ApronIn");
        private static readonly int FootprintInId = Shader.PropertyToID("_FootprintIn");
        private static readonly int SeedOutId = Shader.PropertyToID("_SeedOut");
        private static readonly int SeedInId = Shader.PropertyToID("_SeedIn");
        private static readonly int TargetId = Shader.PropertyToID("_Target");
        private static readonly int ApronSizeId = Shader.PropertyToID("_ApronSize");
        private static readonly int TargetSizeId = Shader.PropertyToID("_TargetSize");
        private static readonly int ReachId = Shader.PropertyToID("_Reach");
        private static readonly int StepId = Shader.PropertyToID("_Step");
        private static readonly int ApronOriginId = Shader.PropertyToID("_ApronOrigin");
        private static readonly int TexelSizeId = Shader.PropertyToID("_TexelSize");
        private static readonly int TerrainHeightId = Shader.PropertyToID("_TerrainHeight");
        private static readonly int RealHeightId = Shader.PropertyToID("_RealHeight");
        private static readonly int ContactBandId = Shader.PropertyToID("_ContactBand");
        private static readonly int RampReachId = Shader.PropertyToID("_RampReach");
        private static readonly int RampSlopeId = Shader.PropertyToID("_RampSlope");
        private static readonly int ProfilePowerId = Shader.PropertyToID("_ProfilePower");
        private static readonly int ReachVariationId = Shader.PropertyToID("_ReachVariation");
        private static readonly int VariationSizeId = Shader.PropertyToID("_VariationSize");
        private static readonly int StrengthId = Shader.PropertyToID("_Strength");

        [Header("Rock")]
        [Tooltip("Rock formations that stand on the terrain.")]
        [SerializeField] private List<LoogaRockFormation> _formations = new();
        [Tooltip("Height above the ground at which the stamp tests if the rock stands on the ground, in metres. Rock " +
                 "that is lower gets no ramp. Rock that is higher, such as an arch span, also gets no ramp.")]
        [SerializeField, Min(0f)] private float _contactBand = 1f;

        [Header("Ramp")]
        [Tooltip("Slope of the raised ground where it meets the rock, in degrees.")]
        [SerializeField, Range(10f, 70f)] private float _contactAngle = 45f;
        [Tooltip("Distance from the rock where the raised ground meets the terrain again, in metres.")]
        [SerializeField, Range(0.5f, 20f)] private float _reach = 3f;
        [Tooltip("Shape of the ramp. 1 = straight slope. Larger values = steeper at the rock and flatter at the end.")]
        [SerializeField, Range(1f, 4f)] private float _profilePower = 2f;
        [Tooltip("Change of the reach along the rock. 0 = constant, 1 = from 0 to twice the reach. The contact angle " +
                 "stays the same, so a longer ramp is also higher.")]
        [SerializeField, Range(0f, 1f)] private float _reachVariation = 0.3f;
        [Tooltip("Typical length of the reach change along the rock, in metres.")]
        [SerializeField, Min(0.5f)] private float _variationSize = 6f;
        [SerializeField, Range(0f, 1f)] private float _strength = 1f;

        private readonly List<LoogaRockFormation> _subscribed = new();
        private readonly List<MeshRenderer> _renderers = new();
        private LoogaStampWorld _world;
        // Union of the height updates since the last settled update, or null. Texture and placement updates do not
        // change the ground that the rock blends into, so they do not build the rock again.
        [NonSerialized] private Rect? _heightChange;
        private ComputeShader _compute;
        private Material _footprintMaterial;
        private int _seedKernel;
        private int _jumpKernel;
        private int _applyKernel;

        public IReadOnlyList<LoogaRockFormation> Formations => _formations;
        public float ContactAngle => _contactAngle;
        public float RampReach => _reach;
        public float ProfilePower => _profilePower;

        /// <summary>Height of the ramp at the rock for the base reach, in metres.</summary>
        public float RampHeight => _reach * Mathf.Tan(_contactAngle * Mathf.Deg2Rad) / _profilePower;

        /// <summary>The ramp reads the rock within the longest reach.</summary>
        public override float Reach => _reach * (1f + _reachVariation) + 1f;

        /// <summary>False while a formation has no generated rock, for example before its first build.</summary>
        public override bool IsReady
        {
            get
            {
                foreach (LoogaRockFormation formation in _formations)
                {
                    if (formation && formation.isActiveAndEnabled && formation.ChunkCount == 0) return false;
                }
                return true;
            }
        }

        #region Built-in
        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            if (_footprintMaterial) DestroyImmediate(_footprintMaterial);
            _footprintMaterial = null;
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            if (isActiveAndEnabled) Subscribe();
        }
        #endregion

        /// <summary>Sets the rock formations of the stamp.</summary>
        public void SetFormations(IEnumerable<LoogaRockFormation> formations)
        {
            _formations.Clear();
            _formations.AddRange(formations);
            if (isActiveAndEnabled) Subscribe();
            MarkChanged();
        }

        /// <summary>Sets the ramp shape. The height follows from the reach, the angle and the power.</summary>
        public void SetRamp(float contactAngle, float reach, float profilePower, float reachVariation)
        {
            _contactAngle = Mathf.Clamp(contactAngle, 10f, 70f);
            _reach = Mathf.Clamp(reach, 0.5f, 20f);
            _profilePower = Mathf.Clamp(profilePower, 1f, 4f);
            _reachVariation = Mathf.Clamp01(reachVariation);
            MarkChanged();
        }

        protected override Rect GetArea()
        {
            Rect? area = null;
            foreach (LoogaRockFormation formation in _formations)
            {
                if (!formation || formation.ChunkCount == 0) continue;
                Bounds bounds = formation.GeneratedBounds;
                var rect = Rect.MinMaxRect(bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z);
                area = area.HasValue ? Union(area.Value, rect) : rect;
            }
            if (!area.HasValue) return default;
            float margin = Reach;
            Rect value = area.Value;
            return Rect.MinMaxRect(value.xMin - margin, value.yMin - margin, value.xMax + margin, value.yMax + margin);
        }

        protected override void Apply(NeighbourhoodTile tile)
        {
            if (!Load()) return;
            int reach = tile.ApronReach;
            int target = tile.Target.width;
            int apronSize = target + 2 * reach;
            float spacing = tile.TexelSize;
            Vector2 apronOrigin = tile.TexelOrigin - Vector2.one * (reach * spacing);
            RenderTexture footprint = Temporary(apronSize, GraphicsFormat.R32_SFloat);
            RenderTexture seedA = Temporary(apronSize, GraphicsFormat.R32G32_SFloat);
            RenderTexture seedB = Temporary(apronSize, GraphicsFormat.R32G32_SFloat);
            try
            {
                Texture apron = tile.GetApron();
                RenderFootprint(footprint, apron, tile, apronOrigin, spacing, apronSize);

                _compute.SetInts(ApronSizeId, apronSize, apronSize);
                _compute.SetInts(TargetSizeId, target, target);
                _compute.SetInt(ReachId, reach);
                _compute.SetVector(ApronOriginId, apronOrigin);
                _compute.SetFloat(TexelSizeId, spacing);
                _compute.SetFloat(TerrainHeightId, tile.TerrainHeight);
                _compute.SetFloat(RealHeightId, tile.RealHeight);
                _compute.SetFloat(RampReachId, _reach);
                _compute.SetFloat(RampSlopeId, Mathf.Tan(_contactAngle * Mathf.Deg2Rad));
                _compute.SetFloat(ProfilePowerId, _profilePower);
                _compute.SetFloat(ReachVariationId, _reachVariation);
                _compute.SetFloat(VariationSizeId, _variationSize);
                _compute.SetFloat(StrengthId, _strength);

                _compute.SetTexture(_seedKernel, ApronInId, apron);
                _compute.SetTexture(_seedKernel, FootprintInId, footprint);
                _compute.SetTexture(_seedKernel, SeedOutId, seedA);
                Dispatch(_seedKernel, apronSize);

                // Jump flooding: each pass halves the step. The distance is only needed up to the apron reach.
                for (int step = Mathf.NextPowerOfTwo(reach + 1); step >= 1; step /= 2)
                {
                    _compute.SetInt(StepId, step);
                    _compute.SetTexture(_jumpKernel, SeedInId, seedA);
                    _compute.SetTexture(_jumpKernel, SeedOutId, seedB);
                    Dispatch(_jumpKernel, apronSize);
                    (seedA, seedB) = (seedB, seedA);
                }

                _compute.SetTexture(_applyKernel, ApronInId, apron);
                _compute.SetTexture(_applyKernel, FootprintInId, footprint);
                _compute.SetTexture(_applyKernel, SeedInId, seedA);
                _compute.SetTexture(_applyKernel, TargetId, tile.Target);
                Dispatch(_applyKernel, target);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(footprint);
                RenderTexture.ReleaseTemporary(seedA);
                RenderTexture.ReleaseTemporary(seedB);
            }
        }

        #region Footprint
        // Winding number of the ground point at each apron texel: 1 or more where the rock stands on the ground. The
        // ground point is the contact band above the heights before the stamp. See LoogaContactFootprint.shader.
        private void RenderFootprint(RenderTexture footprint, Texture apron, NeighbourhoodTile tile, Vector2 apronOrigin,
            float spacing, int apronSize)
        {
            _renderers.Clear();
            foreach (LoogaRockFormation formation in _formations)
            {
                if (formation && formation.isActiveAndEnabled) formation.GetGeneratedRenderers(0, _renderers);
            }
            float extent = apronSize * spacing;
            // Texel x covers the world from origin + (x - 0.5) * spacing, so texel centres sit on heightmap samples.
            Vector2 start = apronOrigin - Vector2.one * (0.5f * spacing);
            _footprintMaterial.SetVector(FootprintRectId, new Vector4(start.x, start.y, 1f / extent, 1f / extent));
            _footprintMaterial.SetTexture(GroundApronId, apron);
            _footprintMaterial.SetFloat(TerrainHeightId, tile.TerrainHeight);
            _footprintMaterial.SetFloat(RealHeightId, tile.RealHeight);
            _footprintMaterial.SetFloat(ContactBandId, _contactBand);
            var commands = new CommandBuffer { name = "Looga Contact Footprint" };
            try
            {
                commands.SetRenderTarget(footprint);
                commands.ClearRenderTarget(false, true, Color.clear);
                var rect = new Rect(start.x, start.y, extent, extent);
                foreach (MeshRenderer renderer in _renderers)
                {
                    if (!renderer || !renderer.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) continue;
                    Bounds bounds = renderer.bounds;
                    if (bounds.max.x < rect.xMin || bounds.min.x > rect.xMax || bounds.max.z < rect.yMin ||
                        bounds.min.z > rect.yMax)
                    {
                        continue;
                    }
                    Mesh mesh = filter.sharedMesh;
                    for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                    {
                        commands.DrawMesh(mesh, renderer.localToWorldMatrix, _footprintMaterial, subMesh, 0);
                    }
                }
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally
            {
                commands.Release();
            }
        }
        #endregion

        #region Rock Updates
        // The rock builds again after the heights in its area settle, so its ground blend reads the raised ground. That
        // build does not change the shape, so it raises no ShapeChanged and the stamp does not run again. The settled
        // region also holds the texture and placement updates of the edit, so the heights are tracked on their own.
        private void OnUpdateSettled(Rect region)
        {
            Rect? changed = _heightChange;
            _heightChange = null;
            if (Application.isPlaying || !changed.HasValue || !changed.Value.Overlaps(GetArea())) return;
            foreach (LoogaRockFormation formation in _formations)
            {
                if (formation && formation.isActiveAndEnabled) formation.Rebuild();
            }
        }

        private void OnHeightsUpdated(Rect region)
        {
            _heightChange = _heightChange.HasValue ? Union(_heightChange.Value, region) : region;
        }

        private void OnRockShapeChanged()
        {
            MarkChanged();
        }

        private void Subscribe()
        {
            Unsubscribe();
            foreach (LoogaRockFormation formation in _formations)
            {
                if (!formation || _subscribed.Contains(formation)) continue;
                formation.ShapeChanged += OnRockShapeChanged;
                _subscribed.Add(formation);
            }
            _world = GetComponentInParent<LoogaStampWorld>(true);
            if (!_world) return;
            _world.UpdateSettled += OnUpdateSettled;
            _world.HeightsUpdated += OnHeightsUpdated;
        }

        private void Unsubscribe()
        {
            foreach (LoogaRockFormation formation in _subscribed)
            {
                if (formation) formation.ShapeChanged -= OnRockShapeChanged;
            }
            _subscribed.Clear();
            if (_world)
            {
                _world.UpdateSettled -= OnUpdateSettled;
                _world.HeightsUpdated -= OnHeightsUpdated;
            }
            _world = null;
        }
        #endregion

        #region Resources
        private bool Load()
        {
            if (!_compute)
            {
                _compute = Resources.Load<ComputeShader>(ComputeName);
                if (!_compute)
                {
                    throw new NotSupportedException("Looga contact stamps require the " + ComputeName +
                                                    " compute shader.");
                }
                _seedKernel = _compute.FindKernel("Seed");
                _jumpKernel = _compute.FindKernel("JumpFlood");
                _applyKernel = _compute.FindKernel("ApplyContact");
            }
            if (!_footprintMaterial)
            {
                var shader = Resources.Load<Shader>(FootprintShaderName);
                if (!shader || !shader.isSupported) return false;
                _footprintMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            return true;
        }

        private static RenderTexture Temporary(int size, GraphicsFormat format)
        {
            var descriptor = new RenderTextureDescriptor(size, size, format, 0) { enableRandomWrite = true };
            RenderTexture texture = RenderTexture.GetTemporary(descriptor);
            texture.filterMode = FilterMode.Point;
            return texture;
        }

        private void Dispatch(int kernel, int size)
        {
            int groups = Mathf.CeilToInt(size / 8f);
            _compute.Dispatch(kernel, groups, groups, 1);
        }

        private static Rect Union(Rect a, Rect b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax),
                Mathf.Max(a.yMax, b.yMax));
        }
        #endregion
    }
}
