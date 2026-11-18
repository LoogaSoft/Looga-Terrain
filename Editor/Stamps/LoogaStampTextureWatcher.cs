using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    /// <summary>
    /// Reports reimported textures to the stamp world updater, so that stamps that sample a rebaked height, falloff or
    /// noise texture update like after a settings change.
    /// </summary>
    internal sealed class LoogaStampTextureWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved,
            string[] movedFrom)
        {
            List<Texture> textures = null;
            foreach (string path in imported)
            {
                // A stamp holds its textures, so a texture that is not loaded has no stamp. A load of every imported
                // texture used much memory on a project-wide import.
                if (!AssetDatabase.IsMainAssetAtPathLoaded(path)) continue;
                if (!typeof(Texture).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(path))) continue;
                var texture = AssetDatabase.LoadAssetAtPath<Texture>(path);
                if (!texture) continue;
                textures ??= new List<Texture>();
                textures.Add(texture);
            }
            if (textures != null)
            {
                LoogaStampWorldUpdater.MarkTexturesChanged(textures);
            }
        }
    }
}
