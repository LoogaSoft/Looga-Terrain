using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Keeps a copy of the terrain heights at its place in the stamp order. A <see cref="LoogaSnapshotLimit"/> later in
    /// the order reads the copy. The evaluator copies the heights only for tiles that a limit uses.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Height Snapshot")]
    public sealed class LoogaHeightSnapshot : LoogaHeightModifier
    {
        private readonly List<LoogaSnapshotLimit> _limits = new();

        /// <summary>Gets the limits of the parent stamp world that read this snapshot, in apply order.</summary>
        public void GetLimits(List<LoogaSnapshotLimit> limits)
        {
            limits.Clear();
            var world = GetComponentInParent<LoogaStampWorld>();
            if (!world) return;
            world.GetComponentsInChildren(true, limits);
            limits.RemoveAll(limit => limit.Snapshot != this);
        }

        /// <summary>
        /// A snapshot changes heights only through its limits, so its rectangle is the union of their rectangles.
        /// Without limits it is the snapshot position.
        /// </summary>
        internal override Rect GetCullRect(HeightMapping mapping)
        {
            GetLimits(_limits);
            Rect? all = null;
            foreach (LoogaSnapshotLimit limit in _limits)
            {
                Rect rect = limit.GetCullRect(mapping);
                all = all.HasValue ? Rect.MinMaxRect(Mathf.Min(all.Value.xMin, rect.xMin),
                    Mathf.Min(all.Value.yMin, rect.yMin), Mathf.Max(all.Value.xMax, rect.xMax),
                    Mathf.Max(all.Value.yMax, rect.yMax)) : rect;
            }
            Vector3 position = transform.position;
            return all ?? new Rect(position.x, position.z, 0f, 0f);
        }
    }
}
