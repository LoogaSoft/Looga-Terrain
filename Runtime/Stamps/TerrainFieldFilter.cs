using System;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Values of the ground that a <see cref="TerrainFieldFilter"/> reads. The stamp world measures the shape and flow
    /// fields from the final terrain heights, so each value follows every height change. A face is ground of 58 degrees
    /// or steeper. The placement fields come from the placements of the object stamps that are field sources
    /// (<see cref="LoogaObjectStamp.FieldSource"/>).
    /// </summary>
    public enum TerrainField
    {
        /// <summary>True angle of the ground, in degrees.</summary>
        [InspectorName("Steepness (degrees)")]
        Steepness,

        /// <summary>
        /// Height above the mean ground within 4 m, in metres. Positive on rims and ridges, negative in hollows.
        /// </summary>
        [InspectorName("Relative Height 4 m (metres)")]
        RelativeHeightSmall,

        /// <summary>
        /// Height above the mean ground within 20 m, in metres. Positive on crests, negative at feet and in valleys.
        /// </summary>
        [InspectorName("Relative Height 20 m (metres)")]
        RelativeHeightLarge,

        /// <summary>
        /// North component of the ground normal: 1 = a vertical face that looks north, 0 = flat, -1 = south.
        /// </summary>
        [InspectorName("Northness (-1 to 1)")]
        Northness,

        /// <summary>
        /// Distance to the nearest face that rises over the point, in metres. It is 48 when there is none within 48 m.
        /// </summary>
        [InspectorName("Face Above: Distance (metres)")]
        FaceAboveDistance,

        /// <summary>
        /// Largest elevation angle from the point to a face above it, in degrees. Rock falls from a face reach about 28
        /// degrees and more. It is 0 when there is no face above within 48 m.
        /// </summary>
        [InspectorName("Face Above: Elevation Angle (degrees)")]
        FaceAboveAngle,

        /// <summary>Distance to the nearest face that falls below the point, in metres. It is 48 without one.</summary>
        [InspectorName("Face Below: Distance (metres)")]
        FaceBelowDistance,

        /// <summary>
        /// Largest drop from the point to a face below it within 48 m, in metres. On a rim it is about the cliff height.
        /// </summary>
        [InspectorName("Face Below: Drop (metres)")]
        FaceBelowDrop,

        /// <summary>
        /// Log10 of the ground area that drains through the point, in square metres. 1 = one 2 m cell and its
        /// neighbours, 3 = 1000 m2 (a small gully head), 5 = 10 ha. Water from more than 512 m past the tile is not
        /// counted.
        /// </summary>
        [InspectorName("Flow: Upstream Area (log10 m2)")]
        FlowArea,

        /// <summary>
        /// Topographic wetness index ln(upstream area per metre of contour / slope tangent). Large in wet hollows,
        /// channel floors and at the foot of long slopes, small on ridges and steep ground. Typical range 2 to 15.
        /// Filled pits and flat ground that water must cross, such as pads and lake beds, get 14.
        /// </summary>
        [InspectorName("Flow: Wetness Index")]
        Wetness,

        /// <summary>
        /// Crown cover of the Canopy field sources, from 0 (open sky) to 1 (under a crown). Each crown is full inside half
        /// its radius and ends at 1.2 times its radius. Object stamps that are field sources ignore placement fields.
        /// </summary>
        [InspectorName("Placements: Canopy (0 to 1)")]
        Canopy,

        /// <summary>
        /// Distance from the base of the nearest Footprint field source, in metres. It is 0 under the object and 12
        /// without one within 12 m. Object stamps that are field sources ignore placement fields.
        /// </summary>
        [InspectorName("Placements: Object Foot Distance (metres)")]
        ObjectFootDistance
    }

    /// <summary>
    /// Filter of a texture, detail or object stamp by a <see cref="TerrainField"/>. Values inside the range keep the
    /// full weight. The smoothness widens the range on each side with a smooth edge. Weight 0 turns the filter off.
    /// </summary>
    [Serializable]
    public sealed class TerrainFieldFilter
    {
        [SerializeField] private bool _enabled;
        [SerializeField] private TerrainField _field;
        [Tooltip("Lower and upper limit of the full weight, in the unit of the field.")]
        [SerializeField] private Vector2 _range;
        [Tooltip("Width of the smooth edge below and above the range, in the unit of the field.")]
        [SerializeField] private Vector2 _smoothness;
        [SerializeField, Range(0f, 1f)] private float _weight = 1f;
        [Tooltip("Noise added to the field value, in the unit of the field. It breaks up the edge of the range.")]
        [SerializeField] private StampNoiseSettings _noise = new StampNoiseSettings();

        public bool Enabled => _enabled;
        public TerrainField Field => _field;
        public Vector2 Range => _range;
        public Vector2 Smoothness => _smoothness;
        public float Weight => _weight;
        public StampNoiseSettings Noise => _noise;

        /// <summary>Sets the filter. The stamp must call MarkChanged after it.</summary>
        public void Set(bool enabled, TerrainField field, Vector2 range, Vector2 smoothness, float weight = 1f)
        {
            _enabled = enabled;
            _field = field;
            _range = range;
            _smoothness = smoothness;
            _weight = weight;
        }
    }
}
