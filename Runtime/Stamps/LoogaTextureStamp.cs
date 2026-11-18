using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Paints one terrain layer in an area. The transform sets the area like a height stamp: position is the centre,
    /// scale x and z set the size, and the y rotation turns the area. The falloff and the weight set the layer weight.
    /// Each texel keeps the four strongest layers. A later texture stamp takes its weight first.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Texture Stamp")]
    public sealed class LoogaTextureStamp : LoogaTextureModifier
    {
        // A global stamp without a painted area changes every terrain.
        private static readonly Rect Everywhere = Rect.MinMaxRect(-1e9f, -1e9f, 1e9f, 1e9f);

        [Header("Texture")]
        [Tooltip("The terrain layer to paint. The stamp world adds it to the terrains.")]
        [SerializeField] private TerrainLayer _layer;
        [Tooltip("Multiplies the weight of the stamp.")]
        [SerializeField, Min(0f)] private float _weight = 1f;

        [Header("Falloff")]
        [SerializeField] private StampFalloff _falloff = new StampFalloff();
        [Tooltip("Texture falloff only. On: sample the falloff texture with clamped UV and ignore its rotation, " +
                 "scale and offset. Off: repeat the texture and use them.")]
        [SerializeField] private bool _clampFalloffTexture;

        [Header("Filters")]
        [Tooltip("Multiplies the weight by 1 + noise.")]
        [SerializeField] private StampNoiseSettings _weightNoise = new StampNoiseSettings();
        [Tooltip("Slope in degrees, measured like MicroVerse: the value also grows with the terrain height.")]
        [SerializeField] private StampRangeFilter _slopeFilter = new StampRangeFilter();
        [Tooltip("Terrain height in metres above the terrain position.")]
        [SerializeField] private StampRangeFilter _heightFilter = new StampRangeFilter();
        [Tooltip("Weight from the distance to the water edge and the height above the water of a water area.")]
        [SerializeField] private StampWaterFilter _waterFilter = new StampWaterFilter();

        [Header("Terrain Field Filters")]
        [Tooltip("Weight from a shape value of the ground, for example the distance below a cliff face.")]
        [SerializeField] private TerrainFieldFilter _fieldFilter = new TerrainFieldFilter();
        [Tooltip("A second field filter. The weight is the product of both filters.")]
        [SerializeField] private TerrainFieldFilter _fieldFilter2 = new TerrainFieldFilter();

        public override TerrainLayer Layer => _layer;
        public float Weight => _weight;
        public StampFalloff Falloff => _falloff;
        public bool ClampFalloffTexture => _clampFalloffTexture;
        public StampNoiseSettings WeightNoise => _weightNoise;
        public StampRangeFilter SlopeFilter => _slopeFilter;
        public StampRangeFilter HeightFilter => _heightFilter;
        public StampWaterFilter WaterFilter => _waterFilter;
        public TerrainFieldFilter FieldFilter => _fieldFilter;
        public TerrainFieldFilter FieldFilter2 => _fieldFilter2;

        /// <summary>True when a field filter reads the terrain fields.</summary>
        internal bool UsesTerrainFields => TerrainFieldMaps.Uses(_fieldFilter, _fieldFilter2);

        /// <summary>
        /// True when a field filter reads a placement field. The stamp then reads the stored placements, and the texture
        /// layers that placement filters read leave it out.
        /// </summary>
        internal bool ReadsPlacementFields => TerrainFieldMaps.UsesPlacements(_fieldFilter, _fieldFilter2);

        /// <summary>True when the stamp reads the terrain heights or the slope.</summary>
        internal bool NeedsHeights =>
            _slopeFilter.Enabled || _heightFilter.Enabled || _waterFilter.ReadsHeights || UsesTerrainFields;

        /// <summary>Sets the texture settings. Converters and tests use it.</summary>
        public void SetTexture(TerrainLayer layer, float weight, bool clampFalloffTexture)
        {
            _layer = layer;
            _weight = weight;
            _clampFalloffTexture = clampFalloffTexture;
            MarkChanged();
        }

        internal override Rect GetCullRect(HeightMapping mapping)
        {
            // MicroVerse TextureStamp.GetBounds: a spline area limits the stamp to the area, a global stamp limits it
            // only with a clamped painted area, and every other falloff uses the stamp bounds.
            if (_falloff.TryGetAreaRect(out Rect area))
            {
                return _waterFilter.Limit(area);
            }
            if (_falloff.Shape == FalloffShape.Global)
            {
                LoogaPaintedArea painted = _falloff.PaintedArea;
                return _waterFilter.Limit(painted && painted.LimitToBounds
                    ? StampMapping.CullRect(painted.transform, mapping) : Everywhere);
            }
            return _waterFilter.Limit(StampMapping.CullRect(transform, mapping));
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.75f, 0.3f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f),
                Vector3.one);
            Vector3 scale = transform.lossyScale;
            Gizmos.DrawWireCube(new Vector3(0f, scale.y * 0.5f, 0f), scale);
        }
    }
}
