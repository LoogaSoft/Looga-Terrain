using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Integrations.Editor
{
    // MicroVerse 1.7 packs some URP DOTS passes with shader targets below the required 4.5 level.
    internal sealed class MicroVerseDotsShaderRequirements : IPreprocessShaders
    {
        public int callbackOrder => -1000;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> variants)
        {
            string path = AssetDatabase.GetAssetPath(shader);
            if (path != "Packages/com.jbooth.microverse/Examples/Shaders/Lit.microversepack" &&
                path != "Packages/com.jbooth.microverse.roads/Scripts/Shaders/Lit.microversepack" &&
                path != "Packages/com.jbooth.microverse.roads/Scripts/Shaders/RoadLit.microversepack") return;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            if (package == null || package.version != "1.7.0") return;
            var localDots = new ShaderKeyword(shader, "DOTS_INSTANCING_ON");
            var globalDots = new ShaderKeyword("DOTS_INSTANCING_ON");
            for (int i = variants.Count - 1; i >= 0; i--)
            {
                var variant = variants[i];
                if (!variant.shaderKeywordSet.IsEnabled(localDots) && !variant.shaderKeywordSet.IsEnabled(globalDots)) continue;
                // Preserve native road rendering. A low-target DOTS variant cannot compile or serve as a BRG draw.
                variants.RemoveAt(i);
            }
        }
    }
}
