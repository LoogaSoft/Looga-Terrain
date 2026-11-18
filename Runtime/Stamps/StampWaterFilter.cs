using System;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Water filter of a texture or placement stamp. It reads the field of a <see cref="LoogaWaterArea"/>: the distance
    /// past the water edge in metres (negative inside water) and the ground height above the water level of the nearest
    /// edge in metres. Weight = the larger of the edge band (distance) and the low ground band (distance and height).
    /// A disabled band gives 0. Without an enabled band the filter is off. Range filter noise moves the edges in metres.
    /// </summary>
    [Serializable]
    public sealed class StampWaterFilter
    {
        // Larger than WaterDistanceField.NotFound, so "beyond the reach" counts as far away.
        private const float Far = 1e6f;

        [SerializeField] private LoogaWaterArea _area;
        [Tooltip("Weight from the distance past the water edge in metres. Negative inside water.")]
        [SerializeField] private StampRangeFilter _edgeBand = new StampRangeFilter();
        [Tooltip("Low ground band: distance past the water edge in metres.")]
        [SerializeField] private StampRangeFilter _lowGroundDistance = new StampRangeFilter();
        [Tooltip("Low ground band: ground height above the water level of the nearest edge in metres.")]
        [SerializeField] private StampRangeFilter _lowGroundHeight = new StampRangeFilter();

        public LoogaWaterArea Area => _area;
        public StampRangeFilter EdgeBand => _edgeBand;
        public StampRangeFilter LowGroundDistance => _lowGroundDistance;
        public StampRangeFilter LowGroundHeight => _lowGroundHeight;

        /// <summary>True when the filter has an area and at least one enabled range.</summary>
        public bool Enabled => _area && (_edgeBand.Enabled || LowGroundEnabled);

        /// <summary>True when the filter reads the terrain heights.</summary>
        internal bool ReadsHeights => Enabled && _lowGroundHeight.Enabled;

        private bool LowGroundEnabled => _lowGroundDistance.Enabled || _lowGroundHeight.Enabled;

        public void SetArea(LoogaWaterArea area)
        {
            _area = area;
        }

        /// <summary>Lower and upper limit for "no limit" on one side of a distance or height range.</summary>
        public static float Unlimited => Far;

        /// <summary>Limits a culling rectangle to the area rectangle when the filter needs the water in reach.</summary>
        internal Rect Limit(Rect cullRect)
        {
            return TryGetLimit(out Rect limit) ? StampMapping.Intersect(cullRect, limit) : cullRect;
        }

        /// <summary>
        /// World XZ rectangle outside which the filter gives weight 0: the area rectangle when both bands need the water
        /// in reach. False when the filter can give weight far from water, for example "distance above 14 m".
        /// </summary>
        internal bool TryGetLimit(out Rect rect)
        {
            rect = default;
            if (!Enabled) return false;
            bool edgeLimited = !_edgeBand.Enabled || UpperLimited(_edgeBand);
            bool lowLimited = !LowGroundEnabled || (_lowGroundDistance.Enabled && UpperLimited(_lowGroundDistance));
            if (!edgeLimited || !lowLimited) return false;
            rect = _area.GetCullRect();
            return true;
        }

        // The weight is 0 beyond the area reach when the range and its noise end below the field reach and the filter
        // applies fully.
        private bool UpperLimited(StampRangeFilter filter)
        {
            float noise = filter.Noise.Type == StampNoiseType.None ? 0f : Mathf.Abs(filter.Noise.Amplitude);
            return filter.Weight >= 1f && filter.Range.y + filter.Smoothness.y + noise < _area.MaxDistance;
        }
    }
}
