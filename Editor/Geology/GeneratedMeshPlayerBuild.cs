using System.Collections.Generic;
using System.IO;
using LoogaSoft.Terrain.Geology;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>
    /// Ships the generated geology meshes and the water surfaces in standalone player builds. A player then loads them and
    /// does not build the rock faces, rock formations, cliff walls, caves and water bodies at the scene load.
    /// </summary>
    /// <remarks>
    /// Before the build, each generator of the open scenes checks its cache file (<see cref="GeneratedMeshCache"/>) and
    /// builds again when the file is old. The open scenes have their stamp snapshots, so the files hold the meshes that
    /// the editor shows. After the build, the step copies the current files into the StreamingAssets of the player, named
    /// by the identity of each generator (<see cref="GeneratedMeshCache.PlayerFileName"/>). A rock formation whose stamp
    /// snapshot waits stops the build: the editor builds it in the next seconds, so the next build can ship it. A
    /// generator without a current file, or in a scene that was not open, builds at the scene load, as before. A
    /// generator that cannot build in a player (<see cref="IGeneratedMeshSource.PlayerCanBuild"/>), for example a rock
    /// formation on a stamp snapshot, stops the build when it has no current file.
    /// </remarks>
    internal sealed class GeneratedMeshPlayerBuild : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        // Shipped file name and the cache file that it copies.
        private static readonly Dictionary<string, string> Files = new();

        // Before other build steps: a step that writes assets, for example terrain maps, can start a stamp update, and a
        // rock formation then waits for its snapshot.
        public int callbackOrder => -100;

        public void OnPreprocessBuild(BuildReport report)
        {
            Files.Clear();
            if (report.summary.platformGroup != BuildTargetGroup.Standalone) return;

            var checks = new List<(IGeneratedMeshSource Source, string Path, bool Built)>();
            var waiting = new List<string>();
            int built = 0;
            bool unsaved = false;
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (!scene.isLoaded) continue;

                unsaved |= scene.isDirty;
                foreach (IGeneratedMeshSource source in Sources(scene))
                {
                    if (!source.GetCacheFile(out string path, out Hash128 key)) continue;

                    bool rebuilt = false;
                    if (!GeneratedMeshCache.HasKey(path, key))
                    {
                        GeneratedMeshBuild build = source.BuildForPlayer();
                        if (build == GeneratedMeshBuild.Later)
                        {
                            waiting.Add(((Component)source).name);
                            continue;
                        }
                        if (build == GeneratedMeshBuild.Built)
                        {
                            built++;
                            rebuilt = true;
                        }
                    }
                    checks.Add((source, path, rebuilt));
                }
            }
            GeneratedMeshCache.WaitForWrites();
            if (waiting.Count > 0)
            {
                throw new BuildFailedException("[Looga Terrain] " + waiting.Count + " rock formations wait for their " +
                    "stamp snapshot (" + List(waiting) + "). The editor builds them in the next seconds. Start the " +
                    "build again.");
            }

            // A later build can change a generator that built before it, for example a water area queues the flow of
            // the rivers that join it. Such a generator builds once more.
            foreach ((IGeneratedMeshSource source, string path, bool rebuilt) in checks)
            {
                if (!rebuilt || source.GetCacheFile(out _, out Hash128 current) &&
                    GeneratedMeshCache.HasKey(path, current)) continue;

                source.BuildForPlayer();
            }
            GeneratedMeshCache.WaitForWrites();

            var old = new List<string>();
            var blocked = new List<string>();
            foreach ((IGeneratedMeshSource source, string path, bool _) in checks)
            {
                var component = (Component)source;
                // A water body gets a new key with each build, and a later build can change it again, for example its
                // flow. The key of the current state decides.
                if (source.GetCacheFile(out _, out Hash128 current) && GeneratedMeshCache.HasKey(path, current))
                {
                    Files[GeneratedMeshCache.PlayerFileName(component)] = path;
                }
                else
                {
                    (source.PlayerCanBuild ? old : blocked).Add(component.name);
                }
            }
            if (blocked.Count > 0)
            {
                Files.Clear();
                throw new BuildFailedException("[Looga Terrain] " + blocked.Count + " generators have no current mesh " +
                    "file and cannot build in a player, because the player build leaves out their inputs, for example " +
                    "the stamp textures (" + List(blocked) + "). Wait until the editor builds them, then start the build " +
                    "again. If this message stays, look for build errors of these generators in the console.");
            }
            if (checks.Count == 0) return;

            Debug.Log("[Looga Terrain] " + Files.Count + " generators of the open scenes have current mesh files (" + built +
                " built again for the player build).");
            if (old.Count > 0)
            {
                Debug.LogWarning("[Looga Terrain] " + old.Count + " generators have no current mesh file and build at " +
                    "the scene load of the player: " + List(old) + ".");
            }
            if (unsaved)
            {
                Debug.LogWarning("[Looga Terrain] An open scene has unsaved changes. The shipped generated meshes follow " +
                    "the open scenes, so save the scenes before a release build.");
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (Files.Count == 0) return;

            string data = DataFolder(report.summary.platform, report.summary.outputPath);
            if (data == null)
            {
                Debug.LogWarning("[Looga Terrain] The generated meshes are not shipped for " + report.summary.platform +
                    ". The player builds them at the scene load.");
                return;
            }
            string folder = Path.Combine(data, "StreamingAssets", GeneratedMeshCache.PlayerFolder);
            // Files of an earlier build into the same folder can belong to generators that no longer exist.
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
            Directory.CreateDirectory(folder);
            long bytes = 0;
            foreach (KeyValuePair<string, string> file in Files)
            {
                string target = Path.Combine(folder, file.Key);
                File.Copy(file.Value, target, true);
                bytes += new FileInfo(target).Length;
            }
            Debug.Log("[Looga Terrain] Shipped " + Files.Count + " generated mesh files (" +
                (bytes / (1024.0 * 1024.0)).ToString("F0") + " MB).");
            Files.Clear();
        }

        private static IEnumerable<IGeneratedMeshSource> Sources(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour is IGeneratedMeshSource source)
                    {
                        yield return source;
                    }
                }
            }
        }

        // The first names of a list, for a console message.
        private static string List(List<string> names)
        {
            const int Shown = 8;
            string text = string.Join(", ", names.GetRange(0, Mathf.Min(names.Count, Shown)));
            return names.Count > Shown ? text + ", ..." : text;
        }

        // The data folder of a standalone player, or null for other targets.
        private static string DataFolder(BuildTarget target, string outputPath)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneLinux64:
                    return Path.Combine(Path.GetDirectoryName(outputPath) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(outputPath) + "_Data");
                case BuildTarget.StandaloneOSX:
                    return Path.Combine(outputPath, "Contents", "Resources", "Data");
                default:
                    return null;
            }
        }
    }
}
