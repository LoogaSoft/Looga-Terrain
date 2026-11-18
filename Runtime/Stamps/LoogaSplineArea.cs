using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Distance source for <see cref="FalloffShape.SplineArea"/>. Closed splines give a signed distance (negative
    /// inside). Open splines give the distance to the line. Several splines in one container combine to their minimum.
    /// A band area has two closed splines: the first is the outer edge, the second is the inner edge. Its field goes
    /// from 0 at the outer edge to -1 at the inner edge, so the fade can change width along the edges. Use a falloff
    /// range of 1 and a boost of 0 with a band.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Spline Area")]
    public sealed class LoogaSplineArea : MonoBehaviour
    {
        [SerializeField] private SplineContainer _spline;
        [Tooltip("Distance field samples across one terrain tile.")]
        [SerializeField] private int _resolution = 1024;
        [Tooltip("Distance in metres that falloffs can use. The field search stops at twice this distance.")]
        [SerializeField, Min(0f)] private float _maxDistance = 128f;
        [Tooltip("Use the distance to the line also for closed splines.")]
        [SerializeField] private bool _closedAsPath;
        [Tooltip("The first spline is the outer edge and the second spline is the inner edge. The falloff fades between " +
                 "them. Use a range of 1 and a boost of 0. The max distance must reach half the widest gap.")]
        [SerializeField] private bool _band;

        public SplineContainer Spline => _spline;
        public int Resolution => _resolution;
        public float MaxDistance => _maxDistance;
        public bool ClosedAsPath => _closedAsPath;
        public bool Band => _band;

        /// <summary>Changes after each settings change. Change tracking compares it.</summary>
        internal int Revision { get; private set; }

        public void Set(SplineContainer spline, int resolution, float maxDistance, bool closedAsPath, bool band = false)
        {
            _spline = spline;
            _resolution = Mathf.Clamp(resolution, 16, 4096);
            _maxDistance = maxDistance;
            _closedAsPath = closedAsPath;
            _band = band;
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        private void OnValidate()
        {
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        /// <summary>World XZ rectangle of the area plus its distance range.</summary>
        internal Rect GetCullRect()
        {
            return SplineGeometry.CullRect(_spline, _maxDistance);
        }
    }
}
