using System;
using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: InternalsVisibleTo("LoogaSoft.Terrain.Water")]
[assembly: InternalsVisibleTo("LoogaSoft.Terrain.Stamps")]

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Lets generated geometry stay over an editor script reload. Hidden generated objects survive the reload, so a
    /// component can take them back after the reload and does not build them again.
    /// </summary>
    /// <remarks>
    /// The objects of the last domain are valid only when the generator code did not change. The generator code can
    /// change in a reload, so <see cref="CanAdopt"/> compares the <see cref="CodeVersion"/> of this domain with the
    /// version that the last domain stored before the reload. The scene does not change during a reload.
    /// </remarks>
    internal static class GeneratedReloadHandover
    {
        private const string VersionKey = "LoogaSoft.Terrain.Geology.CodeVersion";
        // The package folders with the code and shaders that make the generated objects. The other code of the assembly,
        // for example the grass and the terrain renderer, does not change them.
        private static readonly string[] GeneratorFolders = { "Geology", "Surface", "Water" };
        // The package folder with the code and shaders of the stamp evaluation.
        private static readonly string[] StampFolders = { "Stamps" };
        private static bool? _canAdopt;
        private static string _codeVersion;
        private static string _stampCodeVersion;

        /// <summary>
        /// True while the editor unloads the scripts for a reload. The generated objects must stay then.
        /// </summary>
        public static bool Unloading { get; private set; }

        /// <summary>
        /// True after a script reload when the generated objects of the last domain are still valid. False in play
        /// mode builds, at an editor start and after a change of the generator code.
        /// </summary>
        public static bool CanAdopt
        {
            get
            {
#if UNITY_EDITOR
                _canAdopt ??= UnityEditor.SessionState.GetString(VersionKey, string.Empty) == CodeVersion;
                return _canAdopt.Value;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// True from a script domain load until the first editor update. Unity calls OnValidate on the loaded
        /// components then, also when their settings did not change.
        /// </summary>
        public static bool LoadingScripts
        {
            get
            {
#if UNITY_EDITOR
                return !_scriptsLoaded;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// True while the scripts load after a reload that keeps the generated objects. OnValidate of a generator then
        /// queues no build: OnEnable takes the objects back, or builds them.
        /// </summary>
        public static bool KeepsOutputOnLoad => LoadingScripts && CanAdopt;

        private static bool _scriptsLoaded;

        /// <summary>
        /// The version of the generator code: a hash of the files in the generator folders of the package (code,
        /// shaders and compute shaders). A change of other package code, for example the grass, keeps the version.
        /// Without the package folder, the assembly version.
        /// </summary>
        /// <remarks>
        /// The domain reads the files when it loads. A file that changes after the compile and before the domain load
        /// is in the version but not in the code. The editor then compiles again, and the next domain can take objects
        /// that the code without the change made. Tools/Looga/Clear Generated Mesh Cache and a change of the generator
        /// input build them again.
        /// </remarks>
        public static string CodeVersion => _codeVersion ??= ReadVersion(GeneratorFolders);

        /// <summary>
        /// The version of the stamp evaluation code, like <see cref="CodeVersion"/> for the files in Runtime/Stamps.
        /// Results of the stamp evaluation that a stamp world keeps over a reload are valid only with the same version.
        /// </summary>
        public static string StampCodeVersion => _stampCodeVersion ??= ReadVersion(StampFolders);

        private static string AssemblyVersion =>
            typeof(GeneratedReloadHandover).Assembly.ManifestModule.ModuleVersionId.ToString();

        private static string ReadVersion(string[] folders)
        {
#if UNITY_EDITOR
            // This file is in Runtime/Geology. The compiler gives its path.
            string runtime = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(SourcePath()));
            if (string.IsNullOrEmpty(runtime) || !System.IO.Directory.Exists(runtime)) return AssemblyVersion;

            var hash = new Hash128();
            foreach (string folder in folders)
            {
                string path = System.IO.Path.Combine(runtime, folder);
                if (!System.IO.Directory.Exists(path)) return AssemblyVersion;

                string[] files = System.IO.Directory.GetFiles(path, "*", System.IO.SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string file in files)
                {
                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    hash.Append(file.Substring(runtime.Length).Replace('\\', '/'));
                    hash.Append(System.IO.File.ReadAllBytes(file));
                }
            }
            return hash.ToString();
#else
            return AssemblyVersion;
#endif
        }

        private static string SourcePath([CallerFilePath] string path = "")
        {
            return path;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void WatchScriptReload()
        {
            // The versions are read when the domain loads, so they describe the code of this domain at the next reload.
            _codeVersion ??= ReadVersion(GeneratorFolders);
            _stampCodeVersion ??= ReadVersion(StampFolders);
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            UnityEditor.EditorApplication.delayCall += () => _scriptsLoaded = true;
        }

        private static void OnBeforeAssemblyReload()
        {
            Unloading = true;
            UnityEditor.SessionState.SetString(VersionKey, CodeVersion);
        }

        /// <summary>
        /// Stores a value for the next domain. The key must identify the owner, for example its instance ID.
        /// </summary>
        public static void Store<T>(string key, T value)
        {
            UnityEditor.SessionState.SetString(key, JsonUtility.ToJson(value));
        }

        /// <summary>Takes a value that the last domain stored. Returns false when there is none.</summary>
        public static bool TryTake<T>(string key, out T value)
        {
            string json = UnityEditor.SessionState.GetString(key, string.Empty);
            UnityEditor.SessionState.EraseString(key);
            value = default;
            if (json.Length == 0) return false;
            try
            {
                value = JsonUtility.FromJson<T>(json);
                return value != null;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>The object of an instance ID, or null.</summary>
        public static T Find<T>(int instanceId) where T : UnityEngine.Object
        {
            return instanceId == 0 ? null : UnityEditor.EditorUtility.EntityIdToObject(instanceId) as T;
        }
#else
        // A player has no script reload, so there is nothing to hand over. These keep the callers the same in the
        // editor and in a player build.
        public static void Store<T>(string key, T value)
        {
        }

        public static bool TryTake<T>(string key, out T value)
        {
            value = default;
            return false;
        }

        public static T Find<T>(int instanceId) where T : UnityEngine.Object
        {
            return null;
        }
#endif
    }
}
