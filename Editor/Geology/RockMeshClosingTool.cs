using System.Collections.Generic;
using System.IO;
using System.Text;
using LoogaSoft.Terrain.Geology;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>
    /// Replaces the open solid meshes of rock prefabs with closed copies. The copies go into a "Closed Meshes" folder
    /// next to the prefab. Placed instances of the prefab and stamps that use the prefab get the closed meshes too.
    /// </summary>
    public static class RockMeshClosingTool
    {
        private const string FolderName = "Closed Meshes";

        [MenuItem("Assets/Looga/Close Open Rock Meshes")]
        private static void CloseSelectedPrefabs()
        {
            var report = new StringBuilder();
            foreach (Object selected in Selection.objects)
            {
                if (selected is GameObject prefab && PrefabUtility.IsPartOfPrefabAsset(prefab))
                {
                    report.AppendLine(prefab.name + ": " + ClosePrefab(prefab));
                }
            }
            Debug.Log(report.Length > 0 ? report.ToString() : "Select one or more prefab assets.");
        }

        [MenuItem("Assets/Looga/Close Open Rock Meshes", true)]
        private static bool CanCloseSelectedPrefabs()
        {
            foreach (Object selected in Selection.objects)
            {
                if (selected is GameObject prefab && PrefabUtility.IsPartOfPrefabAsset(prefab)) return true;
            }
            return false;
        }

        /// <summary>
        /// Replaces the open solid meshes of a prefab asset with closed copies. Existing copies keep their GUIDs.
        /// </summary>
        /// <returns>A short report of the changed meshes.</returns>
        public static string ClosePrefab(GameObject prefab)
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            string folder = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/') + "/" + FolderName;
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder(Path.GetDirectoryName(folder)?.Replace('\\', '/'), FolderName);
            }

            var closed = new Dictionary<Mesh, Mesh>();
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    filter.sharedMesh = Closed(filter.sharedMesh, folder, closed);
                }
                foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
                {
                    collider.sharedMesh = Closed(collider.sharedMesh, folder, closed);
                }
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            AssetDatabase.SaveAssets();
            return closed.Count == 0 ? "no open solid meshes" : closed.Count + " meshes closed";
        }

        // Returns the closed copy of an open solid mesh, or the mesh itself when it needs no cap.
        private static Mesh Closed(Mesh mesh, string folder, Dictionary<Mesh, Mesh> closed)
        {
            if (!mesh || !RockMeshOpenings.IsOpenSolid(mesh)) return mesh;
            if (closed.TryGetValue(mesh, out Mesh copy)) return copy;

            Mesh capped = RockMeshOpenings.CreateClosedMesh(mesh);
            if (!capped) return mesh;
            string path = folder + "/" + capped.name + ".asset";
            copy = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (copy)
            {
                EditorUtility.CopySerialized(capped, copy);
                Object.DestroyImmediate(capped);
                RockMeshOpenings.Forget(copy);
            }
            else
            {
                AssetDatabase.CreateAsset(capped, path);
                copy = capped;
            }
            closed.Add(mesh, copy);
            return copy;
        }
    }
}
