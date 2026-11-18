using System;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Range filter of a texture stamp, for example slope or height. Values inside the range keep the full weight. The
    /// smoothness widens the range on each side with a smooth edge. Weight 0 turns the filter off, 1 applies it fully.
    /// </summary>
    [Serializable]
    public sealed class StampRangeFilter
    {
        [SerializeField] private bool _enabled;
        [Tooltip("Lower and upper limit of the full weight.")]
        [SerializeField] private Vector2 _range;
        [Tooltip("Width of the smooth edge below and above the range.")]
        [SerializeField] private Vector2 _smoothness;
        [SerializeField, Range(0f, 1f)] private float _weight = 1f;
        [Tooltip("Noise added to the filtered value.")]
        [SerializeField] private StampNoiseSettings _noise = new StampNoiseSettings();

        public bool Enabled => _enabled;
        public Vector2 Range => _range;
        public Vector2 Smoothness => _smoothness;
        public float Weight => _weight;
        public StampNoiseSettings Noise => _noise;

        public void Set(bool enabled, Vector2 range, Vector2 smoothness, float weight)
        {
            _enabled = enabled;
            _range = range;
            _smoothness = smoothness;
            _weight = weight;
        }
    }
}
