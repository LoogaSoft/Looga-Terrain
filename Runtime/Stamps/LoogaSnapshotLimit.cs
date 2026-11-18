using System;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Limits the terrain height to the height of an earlier <see cref="LoogaHeightSnapshot"/> plus an offset. The
    /// transform sets a box like a <see cref="LoogaHeightStamp"/>: the limit has no effect outside the box. The falloff
    /// sets the weight inside the box.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Snapshot Limit")]
    public sealed class LoogaSnapshotLimit : LoogaHeightModifier
    {
        [Header("Limit")]
        [Tooltip("Snapshot earlier in the stamp order. Without it, or when it comes later, the limit has no effect.")]
        [SerializeField] private LoogaHeightSnapshot _snapshot;
        [Tooltip("Min keeps the lower of the height and the snapshot height. Max keeps the higher. " +
                 "Override sets the snapshot height. Add is not supported.")]
        [SerializeField] private HeightCombineMode _mode = HeightCombineMode.Min;
        [Tooltip("Metres added to the snapshot height before the combine.")]
        [SerializeField] private float _offset;

        [Header("Falloff")]
        [SerializeField] private StampFalloff _falloff = new StampFalloff();

        public LoogaHeightSnapshot Snapshot => _snapshot;
        public HeightCombineMode Mode => _mode;
        public float Offset => _offset;
        public StampFalloff Falloff => _falloff;

        protected override void OnValidate()
        {
            if (_mode == HeightCombineMode.Add)
            {
                _mode = HeightCombineMode.Min;
            }
            base.OnValidate();
        }

        /// <summary>Sets the snapshot, the combine mode and the offset in metres.</summary>
        public void SetLimit(LoogaHeightSnapshot snapshot, HeightCombineMode mode, float offset)
        {
            if (mode == HeightCombineMode.Add)
            {
                throw new ArgumentException("A snapshot limit does not support Add.", nameof(mode));
            }
            _snapshot = snapshot;
            _mode = mode;
            _offset = offset;
            MarkChanged();
        }

        internal override Rect GetCullRect(HeightMapping mapping)
        {
            Rect box = StampMapping.CullRect(transform, mapping);
            return _falloff.TryGetAreaRect(out Rect area) ? StampMapping.Intersect(box, area) : box;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.6f, 0.2f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f),
                Vector3.one);
            Vector3 scale = transform.lossyScale;
            Gizmos.DrawWireCube(new Vector3(0f, scale.y * 0.5f, 0f), scale);
        }
    }
}
