using Unity.Profiling;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Profiler markers of the generated geology builds. A player builds the geology from its splines when the scene
    /// loads unless the build shipped the meshes, so a load time measurement reads these markers, for example with a
    /// ProfilerRecorder.
    /// </summary>
    public static class LoogaGeologyMarkers
    {
        public const string RockFace = "Looga Geology.Rock face build";
        public const string RockFormation = "Looga Geology.Rock formation build";
        public const string CliffWall = "Looga Geology.Cliff wall build";
        public const string Cave = "Looga Geology.Cave build";
        public const string BasaltEscarpment = "Looga Geology.Basalt escarpment build";
        public const string BasaltArch = "Looga Geology.Basalt arch build";
        public const string ShippedMesh = "Looga Geology.Shipped mesh load";

        /// <summary>All marker names.</summary>
        public static readonly string[] Names =
        {
            RockFace, RockFormation, CliffWall, Cave, BasaltEscarpment, BasaltArch, ShippedMesh
        };

        internal static readonly ProfilerMarker RockFaceBuild = new(RockFace);
        internal static readonly ProfilerMarker RockFormationBuild = new(RockFormation);
        internal static readonly ProfilerMarker CliffWallBuild = new(CliffWall);
        internal static readonly ProfilerMarker CaveBuild = new(Cave);
        internal static readonly ProfilerMarker BasaltEscarpmentBuild = new(BasaltEscarpment);
        internal static readonly ProfilerMarker BasaltArchBuild = new(BasaltArch);
        // The loads of the meshes that a player build shipped (GeneratedMeshCache.PlayerPath).
        internal static readonly ProfilerMarker ShippedMeshLoad = new(ShippedMesh);
    }
}
