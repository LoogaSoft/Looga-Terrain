using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain
{
    public sealed partial class TerrainRenderer
    {
        private readonly Queue<(int Version, Rect? Region)> _surfaceChanges = new();
        private bool _notifiedHeight;
        private bool _notifiedControls;

        #region Surface changes
        /// <summary>Refreshes texture bindings and cached pages inside normalized terrain coordinates.</summary>
        public void RefreshSurface(Rect normalizedRegion)
        {
            _refreshControlReferences = true;
            _notifiedControls = true;
            MarkSurfaceChanged(normalizedRegion);
        }

        private void MarkSurfaceChanged(Rect? region = null)
        {
            _sourceVersion++;
            _surfaceChanges.Enqueue((_sourceVersion, region));
            while (_surfaceChanges.Count > 64)
            {
                _surfaceChanges.Dequeue();
            }
        }

        internal bool TryGetSurfaceRegion(int sinceVersion, out Rect region)
        {
            region = default;
            if (sinceVersion < 0 || _surfaceChanges.Count == 0
                || sinceVersion < _surfaceChanges.Peek().Version - 1) return false;
            bool found = false;
            foreach (var change in _surfaceChanges)
            {
                if (change.Version <= sinceVersion)
                {
                    continue;
                }
                if (!change.Region.HasValue) return false;
                Rect next = change.Region.Value;
                region = found ? Rect.MinMaxRect(Mathf.Min(region.xMin, next.xMin), Mathf.Min(region.yMin, next.yMin),
                    Mathf.Max(region.xMax, next.xMax), Mathf.Max(region.yMax, next.yMax)) : next;
                found = true;
            }
            return found;
        }

        private static Rect TextureRegion(RectInt region, int resolution)
        {
            float scale = 1f / Mathf.Max(1, resolution);
            // Include source-filter and normal-derivative samples outside the edited texels.
            return Rect.MinMaxRect((region.xMin - 2) * scale, (region.yMin - 2) * scale,
                (region.xMax + 2) * scale, (region.yMax + 2) * scale);
        }
        #endregion
    }
}
