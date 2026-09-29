using System;
using LoogaSoft.Terrain.Integrations.Editor;
using NUnit.Framework;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class MicroSplatVirtualTextureTests
    {
        [Test]
        public void AcceptsHybridHeightAndLiveNoiseTogether()
        {
            const string code = "#define _MICROSPLAT 1\n#define _MICROTERRAIN 1\n"
                + "#define _HYBRIDHEIGHTBLEND 1\n#define _NOISEHEIGHT 1\n#define _NOISEHEIGHTFBM 1\n"
                + "#define _DETAILNOISE 1\n#define _DISTANCENOISE 1\n#define _NORMALNOISE 1\n"
                + "#define _STOCHASTIC 1\n#define _TRIPLANAR 1\n#pragma instancing_options";
            Assert.DoesNotThrow(() => MicroSplatVirtualTextureAdapter.ValidateFeatures(code));
        }

        [TestCase("_WETNESS")]
        [TestCase("_SNOW")]
        [TestCase("_PERTEXDETAILNOISESTRENGTH")]
        [TestCase("_TESSDISTANCE")]
        public void RejectsModulesWithoutAValidatedCachePath(string feature)
        {
            string code = "#define _MICROSPLAT 1\n#define " + feature + " 1\n#pragma instancing_options";
            Assert.Throws<NotSupportedException>(() => MicroSplatVirtualTextureAdapter.ValidateFeatures(code));
        }


        [Test]
        public void TerrainBlendingKeepsObjectShaderOutsideTerrainExport()
        {
            string code = "#define _MICROSPLAT 1\n#define _MICROTERRAIN 1\n#define _TERRAINBLENDING 1\n#pragma instancing_options";
            Assert.DoesNotThrow(() => MicroSplatVirtualTextureAdapter.ValidateFeatures(code));
            code = code.Replace("#define _TERRAINBLENDING 1", "#define _TERRAINBLENDABLESHADER 1");
            Assert.Throws<NotSupportedException>(() => MicroSplatVirtualTextureAdapter.ValidateFeatures(code));
        }

        [Test]
        public void RedundantUrpMarkersDoNotHideUnknownFeatures()
        {
            string code = "#define _MICROSPLAT 1\n#define _MSRENDERLOOP_UNITYURP6 1\n"
                + "#define _MSRENDERLOOP_UNITYLD 1\n#define _MSRENDERLOOP_UNITYURP6P3 1\n#pragma instancing_options";
            CollectionAssert.AreEquivalent(new[] { "_MICROSPLAT", "_MSRENDERLOOP_UNITYURP6" },
                MicroSplatCompatibilityEditor.ReadFeatures(code));
            Assert.DoesNotThrow(() => MicroSplatVirtualTextureAdapter.ValidateFeatures(code));
            code = code.Replace("#pragma", "#define _MSRENDERLOOP_UNKNOWN 1\n#pragma");
            Assert.Throws<NotSupportedException>(() => MicroSplatVirtualTextureAdapter.ValidateFeatures(code));
        }

        [Test]
        public void RejectsMissingFeatureBlock()
        {
            Assert.Throws<NotSupportedException>(() => MicroSplatVirtualTextureAdapter.ValidateFeatures("Shader {}"));
        }
    }
}
