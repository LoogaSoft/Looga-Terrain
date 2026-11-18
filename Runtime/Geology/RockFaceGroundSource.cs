using System;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Supplies the ground heights that a <see cref="LoogaRockFace"/> builds on, in place of the terrain heights.
    /// For example, a source can read the stamp heights before a talus stamp, so the talus later buries the rock base.
    /// </summary>
    public abstract class RockFaceGroundSource : MonoBehaviour
    {
        /// <summary>
        /// Opens the heights of a world XZ region. The rock face disposes the reader after the build.
        /// </summary>
        public abstract IRockFaceGroundReader Open(Rect worldRegion);

        /// <summary>
        /// Starts to read the heights of a world XZ region for a later <see cref="Open"/>. Edit mode builds call it in
        /// each editor update and start when it returns true. Thus Open does not stop the main thread for a GPU read.
        /// </summary>
        /// <returns>True when Open can read the region now. The default source has nothing to read first.</returns>
        public virtual bool Prefetch(Rect worldRegion) => true;

        /// <summary>
        /// False while the heights are incomplete, for example while a stamp waits for its textures after a script
        /// reload. Edit mode builds wait for it, up to a time limit.
        /// </summary>
        public virtual bool IsReady => true;

        /// <summary>
        /// False when a player build leaves out the inputs of the source, for example the stamp textures. A rock face or
        /// rock formation on the source cannot build in a player, so the player build must ship its meshes.
        /// </summary>
        public virtual bool ReadableInPlayer => true;

        /// <summary>
        /// Raised in edit mode when the heights of a world XZ region change. The rock faces of the region build again
        /// when <see cref="IsReady"/> is true.
        /// </summary>
        public event Action<Rect> HeightsChanged;

        /// <summary>Raises <see cref="HeightsChanged"/> after the heights of a region change.</summary>
        protected void RaiseHeightsChanged(Rect worldRegion)
        {
            HeightsChanged?.Invoke(worldRegion);
        }

        /// <summary>
        /// Called when no rock face build waits or runs. A source that keeps heights for the next build of the same
        /// batch releases them here.
        /// </summary>
        public virtual void EndBuilds()
        {
        }
    }

    /// <summary>Ground heights of one region for one rock face build.</summary>
    public interface IRockFaceGroundReader : IDisposable
    {
        /// <summary>
        /// World height at a world XZ position. Returns false outside the region. Rock face builds call it from worker
        /// threads, so it must be safe to call from any thread after Open.
        /// </summary>
        bool TrySampleHeight(float x, float z, out float height);
    }
}
