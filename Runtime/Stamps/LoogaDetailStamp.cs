using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Paints one terrain detail layer, such as grass, in an area. Each detail texel gets the filtered stamp weight as
    /// a coverage value from 0 to 255. Detail stamps of the same layer keep the larger value. The terrains must use
    /// the coverage detail scatter mode, and the layer index refers to the terrain detail prototype list.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Detail Stamp")]
    public sealed class LoogaDetailStamp : LoogaPlacementModifier
    {
        [Header("Detail")]
        [Tooltip("Index in the detail prototype list of the terrains.")]
        [SerializeField, Min(0)] private int _detailLayer;
        [Tooltip("Multiplies the stamp weight.")]
        [SerializeField, Min(0f)] private float _weight = 1f;
        [Tooltip("Detail texels with a weight outside this range get 0.")]
        [SerializeField] private Vector2 _weightRange = new Vector2(0f, 999999f);

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
        [Tooltip("Steepness only: a steep slope counts less with its distance, so the weight falls off over the reach " +
                 "from a steep face instead of ending at a hard line. Only ground that rises over the point counts, so " +
                 "it thins grass toward the foot of a cliff and not along its top.")]
        [SerializeField] private bool _slopeReachFade;
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
        [Tooltip("On: path clears (LoogaPathClear) remove details near spline paths. Off: they have no effect.")]
        [SerializeField] private bool _occludedByPaths = true;

        [Header("Displacement")]
        [Tooltip("Lowers the displaced layers where this stamp paints: each value is multiplied by 1 - displacement x " +
                 "this stamp's coverage. Below 1 the other details thin out but do not leave a gap.")]
        [SerializeField, Range(0f, 1f)] private float _displacement;
        [Tooltip("Detail layers that make room for this stamp. Only the values of earlier stamps change.")]
        [SerializeField] private List<int> _displacedLayers = new();

        public int DetailLayer => _detailLayer;
        public float Weight => _weight;
        public Vector2 WeightRange => _weightRange;
        public StampFalloff Falloff => _falloff;
        public bool ClampFalloffTexture => _clampFalloffTexture;
        public StampWeightNoise WeightNoise => _weightNoise;
        public StampWeightNoise Weight2Noise => _weight2Noise;
        public StampRangeFilter SlopeFilter => _slopeFilter;
        public SlopeMeasure SlopeMeasure => _slopeMeasure;
        public float SlopeReach => _slopeReach;
        public bool SlopeReachFade => _slopeReachFade;
        public StampRangeFilter HeightFilter => _heightFilter;
        public StampWaterFilter WaterFilter => _waterFilter;
        public TerrainFieldFilter FieldFilter => _fieldFilter;
        public TerrainFieldFilter FieldFilter2 => _fieldFilter2;
        public bool TextureFilterEnabled => _textureFilterEnabled;
        public float OtherTextureWeight => _otherTextureWeight;
        public IReadOnlyList<TextureFilterLayer> TextureFilters => _textureFilters;
        public bool OccludedByPaths => _occludedByPaths;
        public float Displacement => _displacement;
        public IReadOnlyList<int> DisplacedLayers => _displacedLayers;

        /// <summary>Sets the detail settings. Converters and tests use it.</summary>
        public void SetDetail(int detailLayer, float weight, Vector2 weightRange, bool clampFalloffTexture,
            bool occludedByPaths)
        {
            _detailLayer = Mathf.Max(0, detailLayer);
            _weight = weight;
            _weightRange = weightRange;
            _clampFalloffTexture = clampFalloffTexture;
            _occludedByPaths = occludedByPaths;
            MarkChanged();
        }

        /// <summary>Sets the layers that make room for this stamp and the displacement from 0 to 1.</summary>
        public void SetDisplacement(float displacement, IEnumerable<int> layers)
        {
            _displacement = Mathf.Clamp01(displacement);
            _displacedLayers = new List<int>(layers);
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

        /// <summary>Sets how the slope filter measures the slope.</summary>
        /// <param name="reach">Steepness only: the filter reads the steepest ground within this distance (m).</param>
        /// <param name="fade">Steepness only: a steep slope counts less with its distance.</param>
        public void SetSlopeMeasure(SlopeMeasure measure, float reach = 0f, bool fade = false)
        {
            _slopeMeasure = measure;
            _slopeReach = Mathf.Max(0f, reach);
            _slopeReachFade = fade;
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
            Gizmos.color = new Color(0.75f, 0.9f, 0.3f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f),
                Vector3.one);
            Vector3 scale = transform.lossyScale;
            Gizmos.DrawWireCube(new Vector3(0f, scale.y * 0.5f, 0f), scale);
        }
    }
}
