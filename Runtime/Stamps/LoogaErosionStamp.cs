using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Erodes the terrain at its place in the stamp order: hydraulic erosion (rain, flow, erosion, sediment transport
    /// and deposition) and thermal erosion (loose ground slides down to the talus angle) on a grid over the stamp area.
    /// Rock hardness changes in 3D over the world, so hard rock stands out and soft rock wears back. The transform sets a
    /// box like a <see cref="LoogaHeightStamp"/>. The falloff and the strength set the weight of the change. Like every
    /// <see cref="LoogaNeighbourhoodStamp"/>, it splits the stamp order.
    /// </summary>
    /// <remarks>
    /// The simulation covers the stamp area plus the margin. Each terrain tile that the area touches runs the whole
    /// simulation from the heights before the stamp, so the tiles agree at their seams. The reach therefore grows to the
    /// whole area when the area crosses a tile edge; keep such an area small. The simulation runs on each evaluation of a
    /// tile.
    /// </remarks>
    [AddComponentMenu("Looga/Stamps/Erosion Stamp")]
    public sealed class LoogaErosionStamp : LoogaNeighbourhoodStamp
    {
        [Header("Grid")]
        [Tooltip("Size of a simulation cell in metres. The cell is a whole number of heightmap samples.")]
        [SerializeField, Range(0.25f, 8f)] private float _cellSize = 1f;
        [Tooltip("Ground around the stamp area that the simulation includes, in metres. Water flows in and out over it.")]
        [SerializeField, Range(0f, 200f)] private float _margin = 40f;
        [Tooltip("Number of steps of the water simulation. 0 = only the valley pass.")]
        [SerializeField, Range(0, 5000)] private int _iterations = 600;
        [Tooltip("Simulated seconds per step. Larger steps are faster but can become unstable.")]
        [SerializeField, Range(0.005f, 0.2f)] private float _timeStep = 0.05f;

        [Header("Valleys")]
        [Tooltip("Steps of the valley pass, before the water simulation. Each step routes the flow over the whole grid " +
                 "and cuts the drainage network. 0 = no valley pass.")]
        [SerializeField, Range(0, 500)] private int _valleySteps = 60;
        [Tooltip("Simulated seconds per valley step.")]
        [SerializeField, Range(0.05f, 10f)] private float _valleyTimeStep = 1f;
        [Tooltip("Routing passes per valley step after the first step. The first step routes until the flow crosses the " +
                 "grid. More passes follow the changed drainage more closely.")]
        [SerializeField, Range(1, 200)] private int _valleyPasses = 24;
        [Tooltip("Rate at which the flow cuts the ground: metres per second per (drainage area ^ area exponent) at a slope " +
                 "of 1.")]
        [SerializeField, Min(0f)] private float _valleyErosion = 0.001f;
        [Tooltip("Sediment that the flow can carry, per (drainage area ^ area exponent) at a slope of 1, in cubic metres " +
                 "per second. Where the slope eases, the load over the capacity settles: fans and valley floors.")]
        [SerializeField, Min(0f)] private float _valleyCapacity = 50f;
        [Tooltip("Share of the load over the capacity that settles per second.")]
        [SerializeField, Range(0f, 1f)] private float _valleyDeposit = 0.5f;
        [Tooltip("Exponent of the drainage area in the cutting rate (0.4 to 0.6 in real landscapes).")]
        [SerializeField, Range(0.1f, 1f)] private float _areaExponent = 0.5f;
        [Tooltip("How the flow spreads to the lower neighbours: low values spread it over all of them, high values send " +
                 "it down the steepest one.")]
        [SerializeField, Range(0.5f, 8f)] private float _flowSpread = 1.5f;

        [Header("Water")]
        [Tooltip("Rain in metres of water per second.")]
        [SerializeField, Min(0f)] private float _rain = 0.012f;
        [Tooltip("Share of the water that evaporates per second.")]
        [SerializeField, Range(0f, 1f)] private float _evaporation = 0.02f;
        [Tooltip("Sediment that the flow can carry per metre per second of speed on a vertical slope.")]
        [SerializeField, Min(0f)] private float _capacity = 1.2f;
        [Tooltip("Rate at which the flow takes ground up to its capacity.")]
        [SerializeField, Min(0f)] private float _dissolve = 0.4f;
        [Tooltip("Rate at which the flow sets down sediment over its capacity.")]
        [SerializeField, Min(0f)] private float _deposit = 0.6f;
        [Tooltip("Water depth in metres at which the flow has its full capacity. Thin sheet flow carries less.")]
        [SerializeField, Min(0.01f)] private float _depthLimit = 0.4f;
        [Tooltip("Smallest slope sine for the capacity, so the flow also carries sediment over flat ground.")]
        [SerializeField, Range(0f, 1f)] private float _minimumTilt = 0.08f;

        [Header("Rock")]
        [Tooltip("Typical size of the rock hardness changes in metres. The hardness changes along the ground and with " +
                 "the height.")]
        [SerializeField, Min(1f)] private float _hardnessScale = 45f;
        [Tooltip("Contrast of the hardness. Higher values give clearer hard and soft rock.")]
        [SerializeField, Range(0f, 4f)] private float _hardnessContrast = 1.8f;
        [Tooltip("Share by which hard rock erodes less. 0 = all rock erodes the same, 1 = the hardest rock does not erode.")]
        [SerializeField, Range(0f, 1f)] private float _hardnessErosion = 0.75f;
        [Tooltip("Talus angle of soft rock, in degrees. Steeper ground slides down.")]
        [SerializeField, Range(10f, 80f)] private float _softTalus = 34f;
        [Tooltip("Talus angle of hard rock, in degrees.")]
        [SerializeField, Range(10f, 89f)] private float _hardTalus = 62f;
        [Tooltip("Rate of the thermal erosion.")]
        [SerializeField, Min(0f)] private float _thermalRate = 0.4f;
        [Tooltip("Soil creep in square metres per second: the ground diffuses down its slope. More creep against the " +
                 "channel cutting gives fewer, wider gullies.")]
        [SerializeField, Min(0f)] private float _creep;
        [SerializeField] private int _seed;

        [Header("Result")]
        [Tooltip("Largest height change in metres.")]
        [SerializeField, Min(0f)] private float _maxChange = 25f;
        [SerializeField, Range(0f, 1f)] private float _strength = 1f;
        [SerializeField] private StampFalloff _falloff = new StampFalloff();

        public float CellSize => _cellSize;
        public float Margin => _margin;
        public int Iterations => _iterations;
        public float TimeStep => _timeStep;
        public int ValleySteps => _valleySteps;
        public float ValleyTimeStep => _valleyTimeStep;
        public int ValleyPasses => _valleyPasses;
        public float ValleyErosion => _valleyErosion;
        public float ValleyCapacity => _valleyCapacity;
        public float ValleyDeposit => _valleyDeposit;
        public float AreaExponent => _areaExponent;
        public float FlowSpread => _flowSpread;
        public float Rain => _rain;
        public float Evaporation => _evaporation;
        public float Capacity => _capacity;
        public float Dissolve => _dissolve;
        public float Deposit => _deposit;
        public float DepthLimit => _depthLimit;
        public float MinimumTilt => _minimumTilt;
        public float HardnessScale => _hardnessScale;
        public float HardnessContrast => _hardnessContrast;
        public float HardnessErosion => _hardnessErosion;
        public float SoftTalus => _softTalus;
        public float HardTalus => _hardTalus;
        public float ThermalRate => _thermalRate;
        public float Creep => _creep;
        public int Seed => _seed;
        public float MaxChange => _maxChange;
        public float Strength => _strength;
        public StampFalloff Falloff => _falloff;

        /// <summary>
        /// Distance in metres that the stamp reads around each sample: the margin when the simulated area lies on one
        /// terrain tile, else the distance from each touched tile to the far edge of the simulated area.
        /// </summary>
        public override float Reach
        {
            get
            {
                Rect area = GetArea();
                Rect simulated = SimulatedArea;
                float reach = _margin;
                LoogaStampWorld world = GetComponentInParent<LoogaStampWorld>();
                if (!world) return Mathf.Max(1f, reach);
                foreach (UnityEngine.Terrain terrain in world.Terrains)
                {
                    if (!terrain || !terrain.terrainData) continue;
                    Vector3 position = terrain.transform.position;
                    Vector3 size = terrain.terrainData.size;
                    var tile = new Rect(position.x, position.z, size.x, size.z);
                    if (!tile.Overlaps(area)) continue;
                    reach = Mathf.Max(reach, tile.xMin - simulated.xMin, simulated.xMax - tile.xMax,
                        tile.yMin - simulated.yMin, simulated.yMax - tile.yMax);
                }
                // One extra cell, so the snapped grid stays inside the apron.
                return Mathf.Max(1f, reach + 2f * _cellSize);
            }
        }

        /// <summary>World XZ rectangle where the stamp can change the heights.</summary>
        internal Rect Area => GetArea();

        /// <summary>World XZ rectangle that the simulation covers: the stamp area plus the margin.</summary>
        public Rect SimulatedArea
        {
            get
            {
                Rect area = GetArea();
                return Rect.MinMaxRect(area.xMin - _margin, area.yMin - _margin, area.xMax + _margin,
                    area.yMax + _margin);
            }
        }

        protected override Rect GetArea()
        {
            return GetCullRect(HeightMapping.WorldAligned);
        }

        protected override void Apply(NeighbourhoodTile tile)
        {
            tile.ApplyErosion(this);
        }

        internal override Rect GetCullRect(HeightMapping mapping)
        {
            Rect rect = StampMapping.CullRect(transform, mapping);
            if (_falloff.TryGetAreaRect(out Rect area))
            {
                rect = StampMapping.Intersect(rect, area);
            }
            return rect;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.55f, 0.75f, 1f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f),
                Vector3.one);
            Vector3 scale = transform.lossyScale;
            Gizmos.DrawWireCube(new Vector3(0f, scale.y * 0.5f, 0f), scale);
        }
    }
}
