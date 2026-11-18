using Unity.Profiling;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Profiler markers of the water body builds. A player builds the water surfaces, or loads the shipped surfaces, when the
    /// scene loads, so a load time measurement reads these markers, for example with a ProfilerRecorder.
    /// </summary>
    public static class LoogaWaterMarkers
    {
        public const string BodyBuild = "Looga Water.Body build";
        public const string FlowBuild = "Looga Water.Body flow build";
        public const string ShoreBuild = "Looga Water.Body shore build";
        /// <summary>A player reads the shipped surface of a body (LoogaWaterBody) instead of a build.</summary>
        public const string BodyLoad = "Looga Water.Body load";

        /// <summary>All marker names.</summary>
        public static readonly string[] Names = { BodyBuild, FlowBuild, ShoreBuild, BodyLoad };

        internal static readonly ProfilerMarker Body = new(BodyBuild);
        internal static readonly ProfilerMarker Flow = new(FlowBuild);
        internal static readonly ProfilerMarker Shore = new(ShoreBuild);
        internal static readonly ProfilerMarker Load = new(BodyLoad);
    }
}
