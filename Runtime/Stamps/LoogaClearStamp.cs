using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Removes the placements of earlier placement stamps in an area. The transform sets the area like a height stamp.
    /// The weights of all clear stamps that cover a point add up. An object is removed only where the sum reaches 1 and
    /// the last covering clear stamp comes after the object stamp. A smaller sum lowers the object weight.
    /// The weight noise and the texture filter shape the clear weight like the weight of a placement stamp. For example,
    /// a partial detail clear with the weight noise of an object stamp thins the grass where that stamp places.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Clear Stamp")]
    public sealed class LoogaClearStamp : LoogaPlacementModifier
    {
        [Header("Clear")]
        [Tooltip("Remove the placements of earlier object stamps.")]
        [SerializeField] private bool _clearObjects = true;
        [Tooltip("Remove the details of earlier detail stamps.")]
        [SerializeField] private bool _clearDetails = true;
        [Tooltip("Kept from MicroVerse content. Looga Stamps places no terrain trees.")]
        [SerializeField] private bool _clearTrees = true;
        [Tooltip("Multiplies the clear weight.")]
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
        [Tooltip("Weight from a shape value of the ground, for example the distance behind a cliff rim.")]
        [SerializeField] private TerrainFieldFilter _fieldFilter = new TerrainFieldFilter();
        [Tooltip("A second field filter. The weight is the product of both filters.")]
        [SerializeField] private TerrainFieldFilter _fieldFilter2 = new TerrainFieldFilter();

        [Header("Texture Filter")]
        [SerializeField] private bool _textureFilterEnabled;
        [Tooltip("Weight of the layers that the list does not contain.")]
        [SerializeField, Range(0f, 1f)] private float _otherTextureWeight = 1f;
        [SerializeField] private List<TextureFilterLayer> _textureFilters = new();

        public bool ClearObjects => _clearObjects;
        public bool ClearDetails => _clearDetails;
        public bool ClearTrees => _clearTrees;
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

        /// <summary>Sets the clear settings. Converters and tests use it.</summary>
        public void SetClear(bool clearObjects, bool clearDetails, bool clearTrees, float weight, bool clampFalloffTexture)
        {
            _clearObjects = clearObjects;
            _clearDetails = clearDetails;
            _clearTrees = clearTrees;
            _weight = weight;
            _clampFalloffTexture = clampFalloffTexture;
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

        /// <summary>Sets the texture filter. Layers that the list does not contain get the other texture weight.</summary>
        public void SetTextureFilter(bool enabled, float otherTextureWeight, IEnumerable<TextureFilterLayer> layers)
        {
            _textureFilterEnabled = enabled;
            _otherTextureWeight = otherTextureWeight;
            _textureFilters = new List<TextureFilterLayer>(layers);
            MarkChanged();
        }

        /// <summary>Texture filter values for the terrain layer list of a tile. See TextureFilterLayer.GetWeights.</summary>
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
            Gizmos.color = new Color(1f, 0.35f, 0.3f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f),
                Vector3.one);
            Vector3 scale = transform.lossyScale;
            Gizmos.DrawWireCube(new Vector3(0f, scale.y * 0.5f, 0f), scale);
        }
    }
}
