using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// A water body for <see cref="LoogaWaterArea"/>. The source gives a top-down water surface mesh. The mesh
    /// boundary edges are the water edge, and the mesh heights are the water surface.
    /// </summary>
    public abstract class LoogaWaterSource : MonoBehaviour
    {
        /// <summary>Changes after each change of the surface. Change tracking and generators compare it.</summary>
        public int Revision { get; private set; }

        /// <summary>
        /// Marks the surface as changed, so the editor updater builds the water fields again. Call it after the mesh
        /// data changes in place. A new mesh object, a new vertex count or a transform change is found without it.
        /// </summary>
        public void MarkChanged()
        {
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        protected virtual void OnValidate()
        {
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        /// <summary>Gets the surface mesh and its matrix to world space. False when the source has no surface.</summary>
        public abstract bool TryGetSurface(out Mesh mesh, out Matrix4x4 localToWorld);
    }
}
