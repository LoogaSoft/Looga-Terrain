using System;
using System.Collections.Generic;

namespace LoogaSoft.Terrain.Integrations
{
    /// <summary>Defines the checked MicroSplat surface feature scope.</summary>
    public static class MicroSplatCompatibility
    {
        public const string FeaturesTag = "LoogaMicroSplatFeatures";
        public const string ShaderTag = "LoogaMicroSplatSourceShader";
        private static readonly HashSet<string> Cached = new()
        {
            "_MICROSPLAT", "_MICROTERRAIN", "_HEIGHTBLEND", "_USEGRADMIP", "_BRANCHSAMPLES", "_BRANCHSAMPLESAGR",
            "_TRIPLANAR", "_STOCHASTIC", "_PERTEXTINT", "_PERTEXCONTRAST", "_PERTEXSATURATION",
            "_PERTEXBRIGHTNESS", "_PERTEXUVSCALEOFFSET", "_PERTEXNORMSTR", "_PERTEXSMOOTHSTR",
            "_TERRAINBLENDING",
            "_HYBRIDHEIGHTBLEND", "_NOISEHEIGHT", "_NOISEHEIGHTFBM", "_DETAILNOISE", "_DISTANCENOISE", "_NORMALNOISE"
        };

        // These features use the generated MicroSplat surface on every draw.
        private static readonly HashSet<string> Direct = new()
        {
            "_ALPHAHOLE", "_ALPHAHOLETEXTURE", "_ALPHABELOWHEIGHT",
            "_GLOBALTINT", "_GLOBALTINTCROSSFADE",
            "_SNOW", "_SNOWSIMPLE", "_USEGLOBALSNOWLEVEL", "_SNOWNORMALNOISE",
            "_WETNESS", "_PUDDLES", "_STREAMS", "_DYNAMICFLOWS", "_RAINDROPS",
            "_WINDPARTICULATE", "_GLITTER", "_TEXTURECLUSTER3", "_TEXTURECLUSTERTRIPLANARNOISE",
            "_NORMALNOISE2", "_DISTANCERESAMPLE", "_PERTEXTRIPLANAR", "_TRIPLANARHEIGHTBLEND",
            "_PARALLAX", "_PERTEXPARALLAX", "_TBNOISE", "_TBOBJECTNORMALBLEND"
        };

        /// <summary>Checks the direct surface policy. This does not validate every feature combination.</summary>
        public static bool IsSupported(string feature)
        {
            return IsCacheSupported(feature) || Direct.Contains(feature);
        }

        /// <summary>Checks features whose cached channels preserve the surface behavior.</summary>
        public static bool IsCacheSupported(string feature)
        {
            return Cached.Contains(feature) || feature == "_MSRENDERLOOP_UNITYURP6";
        }

        /// <summary>Lists unsupported features without removing them from the source.</summary>
        public static string GetUnsupported(IEnumerable<string> features)
        {
            var unsupported = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string feature in features)
            {
                if (!IsSupported(feature))
                {
                    unsupported.Add(feature);
                }
            }
            return string.Join(", ", unsupported);
        }
    }
}
