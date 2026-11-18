using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Removes placements along the spline path on the same object. Each kind has a full-effect width outside the path
    /// width and a smoothness over which the effect falls to 0. The clear removes object and detail placements of all
    /// stamps that are occluded by paths, independent of the hierarchy order. It uses the distance field of the path,
    /// also when the path component is disabled.
    /// </summary>
    /// <remarks>
    /// Objects use the larger of the tree clear and the object clear, like MicroVerse object stamps. The weight of a
    /// placement is multiplied by (1 - tree clear) and by (1 - object clear) before the spawn test.
    /// </remarks>
    [AddComponentMenu("Looga/Stamps/Path Clear")]
    [RequireComponent(typeof(LoogaSplinePath))]
    public sealed class LoogaPathClear : MonoBehaviour
    {
        /// <summary>Width and smoothness of one clear kind, in metres.</summary>
        [System.Serializable]
        public struct ClearShape
        {
            [Tooltip("Off: this kind is not cleared.")]
            [SerializeField] private bool _enabled;
            [Tooltip("Distance outside the path width with full effect, in metres.")]
            [SerializeField] private float _width;
            [Tooltip("Distance over which the effect falls to 0, in metres.")]
            [SerializeField] private float _smoothness;

            public bool Enabled => _enabled;
            public float Width => _width;
            public float Smoothness => _smoothness;

            public ClearShape(bool enabled, float width, float smoothness)
            {
                _enabled = enabled;
                _width = width;
                _smoothness = smoothness;
            }
        }

        [Tooltip("Clear for object stamps. MicroVerse calls it the object clear.")]
        [SerializeField] private ClearShape _objects = new ClearShape(false, 1f, 3f);
        [Tooltip("Second clear for object stamps. MicroVerse calls it the tree clear.")]
        [SerializeField] private ClearShape _trees = new ClearShape(true, 1f, 3f);
        [Tooltip("Clear for detail stamps.")]
        [SerializeField] private ClearShape _details = new ClearShape(true, 1f, 3f);

        private LoogaSplinePath _path;

        /// <summary>Changes after each settings change. Change tracking compares it.</summary>
        internal int Revision { get; private set; }

        public ClearShape Objects => _objects;
        public ClearShape Trees => _trees;
        public ClearShape Details => _details;

        /// <summary>The spline path whose distance field gives the clear.</summary>
        public LoogaSplinePath Path => _path ? _path : _path = GetComponent<LoogaSplinePath>();

        /// <summary>Sets the clear settings. Converters and tests use it.</summary>
        public void SetClear(ClearShape objects, ClearShape trees, ClearShape details)
        {
            _objects = objects;
            _trees = trees;
            _details = details;
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        /// <summary>World XZ rectangle that decides which tiles get the clear: the cull rectangle of the path.</summary>
        internal Rect GetCullRect(HeightMapping mapping)
        {
            return Path ? Path.GetCullRect(mapping) : Rect.zero;
        }

        private void OnValidate()
        {
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }
    }
}
