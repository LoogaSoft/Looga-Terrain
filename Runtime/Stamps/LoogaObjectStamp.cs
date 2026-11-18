using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    // Values must agree with the prototype locks in Resources/LoogaPlacementRaster.shader.
    public enum AxisLock
    {
        None = 0,
        XY = 1,
        XZ = 2,
        YZ = 3,
        XYZ = 4
    }

    /// <summary>One prefab of an object stamp, with its pick weight and random ranges.</summary>
    [Serializable]
    public sealed class ObjectPrototype
    {
        [SerializeField] private GameObject _prefab;
        [Tooltip("Off: the stamp never picks this prefab. Its pick weight still counts in the total.")]
        [SerializeField] private bool _enabled = true;
        [Tooltip("Pick weight. Each prefab gets weight + 1.")]
        [SerializeField, Min(0f)] private float _weight;
        [Tooltip("Spawn only where the stamp weight is in this range. y = 0 turns the range off.")]
        [SerializeField] private Vector2 _weightRange;
        [Tooltip("On: the stamp weight must be above 0.5. The weight then maps from 0.5..1 to 0..1.")]
        [SerializeField] private bool _densityByWeight = true;

        [Header("Rotation")]
        [Tooltip("Rotation ranges in degrees.")]
        [SerializeField] private Vector2 _rotationX;
        [SerializeField] private Vector2 _rotationY = new Vector2(-180f, 180f);
        [SerializeField] private Vector2 _rotationZ;
        [SerializeField] private AxisLock _rotationLock;
        [Tooltip("0 = upright, 1 = turned to the terrain slope.")]
        [SerializeField, Range(0f, 1f)] private float _slopeAlignment;

        [Header("Scale")]
        [SerializeField] private Vector2 _scaleX = Vector2.one;
        [SerializeField] private Vector2 _scaleY = Vector2.one;
        [SerializeField] private Vector2 _scaleZ = Vector2.one;
        [SerializeField] private AxisLock _scaleLock = AxisLock.XYZ;
        [Tooltip("Scale where the stamp weight is near 0. The scale reaches 1 at weight 3.")]
        [SerializeField] private float _scaleAtBoundaries = 1f;

        [Header("Height")]
        [Tooltip("Range of the depth in metres that the object sinks into the terrain.")]
        [SerializeField] private Vector2 _sink;
        [Tooltip("Share of the ground drop under the base that the object sinks, before the sink range. 0 = the pivot " +
            "stays at the ground height of its point. 1 = the object sinks to the lowest ground under its base, so a " +
            "wide base does not float on the downhill side of a slope.")]
        [SerializeField, Range(0f, 1f)] private float _groundFit;
        [Tooltip("Base radius in prefab units for the ground fit. 0 = the largest horizontal distance from the pivot " +
            "of the lowest 5 % of the LOD 0 mesh vertices. A player build needs readable meshes for the automatic radius.")]
        [SerializeField, Min(0f)] private float _groundFitRadius;

        public GameObject Prefab => _prefab;
        public bool Enabled => _enabled;
        public float Weight => _weight;
        public Vector2 WeightRange => _weightRange;
        public bool DensityByWeight => _densityByWeight;
        public Vector2 RotationX => _rotationX;
        public Vector2 RotationY => _rotationY;
        public Vector2 RotationZ => _rotationZ;
        public AxisLock RotationLock => _rotationLock;
        public float SlopeAlignment => _slopeAlignment;
        public Vector2 ScaleX => _scaleX;
        public Vector2 ScaleY => _scaleY;
        public Vector2 ScaleZ => _scaleZ;
        public AxisLock ScaleLock => _scaleLock;
        public float ScaleAtBoundaries => _scaleAtBoundaries;
        public Vector2 Sink => _sink;
        public float GroundFit => _groundFit;
        public float GroundFitRadius => _groundFitRadius;

        public ObjectPrototype(GameObject prefab)
        {
            _prefab = prefab;
        }

        public void SetPick(bool enabled, float weight, Vector2 weightRange, bool densityByWeight)
        {
            _enabled = enabled;
            _weight = weight;
            _weightRange = weightRange;
            _densityByWeight = densityByWeight;
        }

        public void SetRotation(Vector2 x, Vector2 y, Vector2 z, AxisLock rotationLock, float slopeAlignment)
        {
            _rotationX = x;
            _rotationY = y;
            _rotationZ = z;
            _rotationLock = rotationLock;
            _slopeAlignment = slopeAlignment;
        }

        public void SetScale(Vector2 x, Vector2 y, Vector2 z, AxisLock scaleLock, float scaleAtBoundaries)
        {
            _scaleX = x;
            _scaleY = y;
            _scaleZ = z;
            _scaleLock = scaleLock;
            _scaleAtBoundaries = scaleAtBoundaries;
        }

        public void SetSink(Vector2 sink)
        {
            _sink = sink;
        }

        /// <summary>Sets the ground fit share from 0 to 1 and the base radius in prefab units (0 = automatic).</summary>
        public void SetGroundFit(float groundFit, float radius)
        {
            _groundFit = Mathf.Clamp01(groundFit);
            _groundFitRadius = Mathf.Max(0f, radius);
        }
    }

    /// <summary>
    /// Weight of one terrain layer in the texture filter. The filter subtracts (1 - weight) x layer weight for each of
    /// the four strongest layers of a texel. Amplitude and balance add balance x amplitude x layer weight.
    /// </summary>
    [Serializable]
    public sealed class TextureFilterLayer
    {
        [SerializeField] private TerrainLayer _layer;
        [SerializeField, Range(-1f, 1f)] private float _weight;
        [SerializeField, Range(0f, 10f)] private float _amplitude = 1f;
        [SerializeField, Range(-1f, 1f)] private float _balance;

        public TerrainLayer Layer => _layer;
        public float Weight => _weight;
        public float Amplitude => _amplitude;
        public float Balance => _balance;

        public TextureFilterLayer(TerrainLayer layer, float weight, float amplitude, float balance)
        {
            _layer = layer;
            _weight = weight;
            _amplitude = amplitude;
            _balance = balance;
        }

        /// <summary>
        /// Texture filter values for the terrain layer list of a tile: x = 1 - weight, y = amplitude, z = balance. Layers
        /// that the filter list does not contain use 1 - otherWeight.
        /// </summary>
        internal static void GetWeights(IReadOnlyList<TextureFilterLayer> filters, float otherWeight,
            TerrainLayer[] layers, Vector4[] weights)
        {
            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] = new Vector4(1f - otherWeight, 1f, 0f, 0f);
            }
            for (int i = 0; i < layers.Length && i < weights.Length; i++)
            {
                // A later list entry for the same layer wins, like MicroVerse.
                foreach (TextureFilterLayer filter in filters)
                {
                    if (ReferenceEquals(filter.Layer, layers[i]))
                    {
                        weights[i] = new Vector4(1f - filter.Weight, filter.Amplitude, filter.Balance, 0f);
                    }
                }
            }
        }
    }

    /// <summary>What the placements of an object stamp give to the placement fields of other stamps.</summary>
    public enum PlacementFieldSource
    {
        /// <summary>The placements give no field.</summary>
        None,

        /// <summary>The crowns give the <see cref="TerrainField.Canopy"/> field.</summary>
        Canopy,

        /// <summary>The bases give the <see cref="TerrainField.ObjectFootDistance"/> field.</summary>
        Footprint,

        /// <summary>The crowns give the Canopy field and the bases the Object Foot Distance field, for example trees.</summary>
        [InspectorName("Canopy and Footprint")]
        CanopyAndFootprint
    }

    /// <summary>
    /// Places prefabs in an area. The stamp makes round(512 x density^2) candidates for each 1 km of terrain, on a grid
    /// that a Poisson disk moves. A candidate spawns where the filtered stamp weight is at least a random value. The
    /// stamp world writes the placements into its instance set.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Object Stamp")]
    public sealed class LoogaObjectStamp : LoogaPlacementModifier
    {
        [Header("Prototypes")]
        [SerializeField] private List<ObjectPrototype> _prototypes = new();

        [Header("Spawning")]
        [Tooltip("Candidates for each 1 km of terrain = round(512 x density^2).")]
        [SerializeField, Min(0.01f)] private float _density = 1f;
        [SerializeField] private uint _seed;
        [Tooltip("Texture that moves the candidates away from the grid. The parity mode uses the MicroVerse disk.")]
        [SerializeField] private Texture2D _poissonDisk;
        [SerializeField, Range(0f, 2f)] private float _poissonDiskStrength = 1f;
        [Tooltip("Multiplies the stamp weight.")]
        [SerializeField, Min(0f)] private float _weight = 1f;

        [Header("Falloff")]
        [SerializeField] private StampFalloff _falloff = new StampFalloff();
        [Tooltip("Texture falloff only. On: sample the falloff texture with clamped UV and ignore its rotation, " +
                 "scale and offset. Off: repeat the texture and use them.")]
        [SerializeField] private bool _clampFalloffTexture;

        [Header("Filters")]
        [Tooltip("The weight is 1 + this noise.")]
        [SerializeField] private StampWeightNoise _weightNoise = new StampWeightNoise();
        [Tooltip("Combines with the first weight noise by its operation.")]
        [SerializeField] private StampWeightNoise _weight2Noise = new StampWeightNoise();
        [Tooltip("Slope in degrees. The slope measure sets how the slope is measured.")]
        [SerializeField] private StampRangeFilter _slopeFilter = new StampRangeFilter();
        [Tooltip("Parity: like MicroVerse, the value also grows with the terrain height. " +
                 "Steepness: the true angle of the ground in degrees.")]
        [SerializeField] private SlopeMeasure _slopeMeasure;
        [Tooltip("Steepness only: distance in metres. The filter reads the steepest ground within it, so it also " +
                 "takes the ground next to a steep face.")]
        [SerializeField, Min(0f)] private float _slopeReach;
        [Tooltip("Terrain height in metres above the terrain position.")]
        [SerializeField] private StampRangeFilter _heightFilter = new StampRangeFilter();
        [Tooltip("Weight from the distance to the water edge and the height above the water of a water area.")]
        [SerializeField] private StampWaterFilter _waterFilter = new StampWaterFilter();

        [Header("Terrain Field Filters")]
        [Tooltip("Weight from a shape value of the ground, for example the distance below a cliff face.")]
        [SerializeField] private TerrainFieldFilter _fieldFilter = new TerrainFieldFilter();
        [Tooltip("A second field filter. The weight is the product of both filters.")]
        [SerializeField] private TerrainFieldFilter _fieldFilter2 = new TerrainFieldFilter();

        [Header("Texture Filter")]
        [SerializeField] private bool _textureFilterEnabled;
        [Tooltip("Weight of the layers that the list does not contain.")]
        [SerializeField, Range(0f, 1f)] private float _otherTextureWeight = 1f;
        [SerializeField] private List<TextureFilterLayer> _textureFilters = new();

        [Header("Occlusion")]
        [Tooltip("On: path clears (LoogaPathClear) remove placements near spline paths. Off: they have no effect.")]
        [SerializeField] private bool _occludedByPaths = true;

        [Header("Placement Field")]
        [Tooltip("None: no field. Canopy: the crowns give the Canopy field. Footprint: the bases give the Object Foot " +
                 "Distance field. Canopy and Footprint: both, for example trees. Field filters of other stamps read " +
                 "these fields. A field source ignores its own placement field filters.")]
        [SerializeField] private PlacementFieldSource _fieldSource;
        [Tooltip("Multiplies the measured radius of each prefab: half the width of its bounds for Canopy, the radius of " +
                 "its lowest quarter (at most 2 m high) for Footprint.")]
        [SerializeField, Range(0.1f, 2f)] private float _fieldRadiusScale = 1f;

        [Header("Output")]
        [Tooltip("Give each placement a collider-only copy of the prefab colliders.")]
        [SerializeField] private bool _colliders = true;
        [Tooltip("Group of the placements in the instance set, for example a grade group. 0 = none.")]
        [SerializeField, Range(0, 255)] private int _instanceGroup;

        public IReadOnlyList<ObjectPrototype> Prototypes => _prototypes;
        public float Density => _density;
        public uint Seed => _seed;
        public Texture2D PoissonDisk => _poissonDisk;
        public float PoissonDiskStrength => _poissonDiskStrength;
        public float Weight => _weight;
        public StampFalloff Falloff => _falloff;
        public bool ClampFalloffTexture => _clampFalloffTexture;
        public StampWeightNoise WeightNoise => _weightNoise;
        public StampWeightNoise Weight2Noise => _weight2Noise;
        public StampRangeFilter SlopeFilter => _slopeFilter;
        public SlopeMeasure SlopeMeasure => _slopeMeasure;
        public float SlopeReach => _slopeReach;
        public StampRangeFilter HeightFilter => _heightFilter;
        public StampWaterFilter WaterFilter => _waterFilter;
        public TerrainFieldFilter FieldFilter => _fieldFilter;
        public TerrainFieldFilter FieldFilter2 => _fieldFilter2;
        public bool TextureFilterEnabled => _textureFilterEnabled;
        public float OtherTextureWeight => _otherTextureWeight;
        public IReadOnlyList<TextureFilterLayer> TextureFilters => _textureFilters;
        public bool OccludedByPaths => _occludedByPaths;
        public PlacementFieldSource FieldSource => _fieldSource;
        public float FieldRadiusScale => _fieldRadiusScale;
        public bool Colliders => _colliders;
        public int InstanceGroup => _instanceGroup;

        /// <summary>Sets the spawn settings. Converters and tests use it.</summary>
        public void SetSpawning(float density, uint seed, Texture2D poissonDisk, float poissonDiskStrength, float weight,
            bool clampFalloffTexture)
        {
            _density = density;
            _seed = seed;
            _poissonDisk = poissonDisk;
            _poissonDiskStrength = poissonDiskStrength;
            _weight = weight;
            _clampFalloffTexture = clampFalloffTexture;
            MarkChanged();
        }

        /// <summary>Replaces the prototypes. Converters and tests use it.</summary>
        public void SetPrototypes(IEnumerable<ObjectPrototype> prototypes)
        {
            _prototypes = new List<ObjectPrototype>(prototypes);
            MarkChanged();
        }

        /// <summary>Sets the texture filter. Converters and tests use it.</summary>
        public void SetTextureFilter(bool enabled, float otherTextureWeight, IEnumerable<TextureFilterLayer> layers)
        {
            _textureFilterEnabled = enabled;
            _otherTextureWeight = otherTextureWeight;
            _textureFilters = new List<TextureFilterLayer>(layers);
            MarkChanged();
        }

        public void SetOccludedByPaths(bool occluded)
        {
            _occludedByPaths = occluded;
            MarkChanged();
        }

        /// <summary>Sets how the slope filter measures the slope.</summary>
        /// <param name="reach">Steepness only: the filter reads the steepest ground within this distance (m).</param>
        public void SetSlopeMeasure(SlopeMeasure measure, float reach = 0f)
        {
            _slopeMeasure = measure;
            _slopeReach = Mathf.Max(0f, reach);
            MarkChanged();
        }

        /// <summary>Sets the placement field that the placements give, and the radius scale of the prefabs.</summary>
        public void SetFieldSource(PlacementFieldSource source, float radiusScale = 1f)
        {
            _fieldSource = source;
            _fieldRadiusScale = Mathf.Clamp(radiusScale, 0.1f, 2f);
            MarkChanged();
        }

        /// <summary>True when the stamp is a field source and other stamps can read its placements.</summary>
        internal bool IsFieldSource => _fieldSource != PlacementFieldSource.None;

        /// <summary>True when the crowns of the placements give the Canopy field.</summary>
        internal bool GivesCanopy =>
            _fieldSource is PlacementFieldSource.Canopy or PlacementFieldSource.CanopyAndFootprint;

        /// <summary>True when the bases of the placements give the Object Foot Distance field.</summary>
        internal bool GivesFootprint =>
            _fieldSource is PlacementFieldSource.Footprint or PlacementFieldSource.CanopyAndFootprint;

        public void SetOutput(bool colliders, int instanceGroup)
        {
            _colliders = colliders;
            _instanceGroup = Mathf.Clamp(instanceGroup, 0, 255);
        }

        /// <summary>
        /// Texture filter values for the terrain layer list of a tile: x = 1 - weight, y = amplitude, z = balance. Layers
        /// that the filter list does not contain use 1 - <see cref="OtherTextureWeight"/>.
        /// </summary>
        internal void GetTextureFilterWeights(TerrainLayer[] layers, Vector4[] weights)
        {
            TextureFilterLayer.GetWeights(_textureFilters, _otherTextureWeight, layers, weights);
        }

        internal override Rect GetCullRect(HeightMapping mapping)
        {
            return _waterFilter.Limit(FalloffCullRect(_falloff, transform, mapping));
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.4f, 0.9f, 0.4f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f),
                Vector3.one);
            Vector3 scale = transform.lossyScale;
            Gizmos.DrawWireCube(new Vector3(0f, scale.y * 0.5f, 0f), scale);
        }
    }
}
