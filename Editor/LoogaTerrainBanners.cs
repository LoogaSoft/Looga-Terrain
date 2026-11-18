using LoogaSoft.Inspector.Editor;
using LoogaSoft.Terrain.Instances;
using UnityEditor;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>Registers the LoogaSoft inspector banner for the main Looga Terrain components.</summary>
    [InitializeOnLoad]
    internal static class LoogaTerrainBanners
    {
        static LoogaTerrainBanners()
        {
            LoogaBanner.Register(typeof(LoogaWorldController));
            LoogaBanner.Register(typeof(TerrainRenderer));
            LoogaBanner.Register(typeof(TerrainVegetationProvider));
        }
    }
}
