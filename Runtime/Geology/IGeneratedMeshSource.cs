using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
#if UNITY_EDITOR
    /// <summary>The result of <see cref="IGeneratedMeshSource.BuildForPlayer"/>.</summary>
    internal enum GeneratedMeshBuild
    {
        /// <summary>The generator built now, so its cache file has the current state.</summary>
        Built,
        /// <summary>The generator waits for its inputs, for example a stamp snapshot, and builds later in the editor.</summary>
        Later,
        /// <summary>The generator does not build for a player build, for example a cave, whose build writes holes.</summary>
        Never
    }
#endif

    /// <summary>
    /// A generator whose meshes a player build ships (<see cref="GeneratedMeshCache"/>). The player build copies the cache
    /// file of the current state into the player, named by <see cref="GeneratedMeshCache.PlayerFileName"/>. The player
    /// then loads the file and does not build the meshes at the scene load.
    /// </summary>
    internal interface IGeneratedMeshSource
    {
#if UNITY_EDITOR
        /// <summary>
        /// The cache file and the key of the current state. False when the generator has no cache file, for example a
        /// disabled generator or a kit wall.
        /// </summary>
        bool GetCacheFile(out string path, out Hash128 key);

        /// <summary>Builds now on the main thread for a player build, so the cache file gets the current state.</summary>
        GeneratedMeshBuild BuildForPlayer();

        /// <summary>
        /// False when a player cannot build the meshes at the scene load, because the player build leaves out an input,
        /// for example the stamp textures of a ground source (<see cref="RockFaceGroundSource.ReadableInPlayer"/>). The
        /// player build then stops when the cache file is not current.
        /// </summary>
        bool PlayerCanBuild { get; }
#endif
    }
}
