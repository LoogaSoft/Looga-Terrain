using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Roads.Editor
{
    /// <summary>
    /// Removes the full slab meshes from player builds. A slab with level of detail chunks
    /// (<see cref="LoogaRoadSlabLods"/>) keeps its full mesh only for the editor tools, and its renderer is off.
    /// </summary>
    /// <remarks>
    /// The chunks draw the road, so the player does not need the full mesh. Without this step the build holds the slab
    /// triangles two times. A slab without chunks keeps its mesh.
    /// </remarks>
    internal sealed class LoogaRoadSlabPlayerBuild : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // Play mode in the editor processes scenes without a report.
            if (report == null) return;

            int cleared = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
                {
                    Transform slab = group.transform.parent;
                    if (!group.name.StartsWith(LoogaRoadSlabLods.ChunkPrefix) || !slab) continue;
                    if (!slab.TryGetComponent(out MeshRenderer renderer) || renderer.enabled) continue;
                    if (!slab.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) continue;

                    filter.sharedMesh = null;
                    cleared++;
                }
            }
            if (cleared == 0) return;

            Debug.Log("[Looga Terrain] Road slabs of scene " + scene.name + ": " + cleared +
                " full meshes left out of the build. The chunks draw the roads.");
        }
    }
}
