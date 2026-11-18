using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Distance source for <see cref="FalloffShape.WaterArea"/>: the signed distance to the nearest edge of a set of
    /// water sources (negative inside water). Distances are exact up to the max distance. Farther samples are "not
    /// found": far outside or deep inside water.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Water Area")]
    public sealed class LoogaWaterArea : MonoBehaviour
    {
        [Tooltip("Water bodies of this area. A stamp that uses the area reads the nearest edge of any of them.")]
        [SerializeField] private List<LoogaWaterSource> _sources = new();
        [Tooltip("Field samples across one terrain tile.")]
        [SerializeField] private int _resolution = 1024;
        [Tooltip("Distance in metres from the water edge that falloffs can use. " +
                 "Falloff boost and range must stay inside it.")]
        [SerializeField, Min(0f)] private float _maxDistance = 32f;

        public IReadOnlyList<LoogaWaterSource> Sources => _sources;
        public int Resolution => _resolution;
        public float MaxDistance => _maxDistance;

        /// <summary>Changes after each settings change. Change tracking compares it.</summary>
        internal int Revision { get; private set; }

        public void Set(IEnumerable<LoogaWaterSource> sources, int resolution, float maxDistance)
        {
            _sources = new List<LoogaWaterSource>(sources);
            _resolution = Mathf.Clamp(resolution, 16, 4096);
            _maxDistance = Mathf.Max(0f, maxDistance);
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        private void OnValidate()
        {
            _resolution = Mathf.Clamp(_resolution, 16, 4096);
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        /// <summary>World XZ rectangle of the active sources plus the max distance. Empty without sources.</summary>
        internal Rect GetCullRect()
        {
            Rect? all = null;
            foreach (LoogaWaterSource source in _sources)
            {
                if (!source || !source.isActiveAndEnabled || !source.TryGetSurface(out Mesh mesh, out Matrix4x4 matrix))
                {
                    continue;
                }
                Rect rect = WaterDistanceField.SurfaceRect(mesh, matrix);
                all = all.HasValue ? Rect.MinMaxRect(Mathf.Min(all.Value.xMin, rect.xMin),
                    Mathf.Min(all.Value.yMin, rect.yMin), Mathf.Max(all.Value.xMax, rect.xMax),
                    Mathf.Max(all.Value.yMax, rect.yMax)) : rect;
            }
            if (!all.HasValue)
            {
                Vector3 position = transform.position;
                return new Rect(position.x, position.z, 0f, 0f);
            }
            Rect cull = all.Value;
            return Rect.MinMaxRect(cull.xMin - _maxDistance, cull.yMin - _maxDistance, cull.xMax + _maxDistance,
                cull.yMax + _maxDistance);
        }
    }
}
