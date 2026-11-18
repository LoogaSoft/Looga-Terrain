using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Water source from the <see cref="MeshFilter"/> on the same object. The mesh must be readable, because the
    /// water fields read its boundary edges on the CPU.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Mesh Water Source")]
    public class LoogaMeshWaterSource : LoogaWaterSource
    {
        public override bool TryGetSurface(out Mesh mesh, out Matrix4x4 localToWorld)
        {
            mesh = TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
            localToWorld = transform.localToWorldMatrix;
            return mesh;
        }
    }
}
