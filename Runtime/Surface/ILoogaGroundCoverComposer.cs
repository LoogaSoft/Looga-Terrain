using UnityEngine;

namespace LoogaSoft.Terrain.Surface
{
    /// <summary>
    /// Adds the plants of another system to the ground cover maps before <see cref="LoogaTerrainSurfaceGlobals"/> binds
    /// them, for example the procedural grass from its own detail data on the GPU. The base maps then hold only the
    /// other plants.
    /// </summary>
    public interface ILoogaGroundCoverComposer
    {
        /// <summary>
        /// Returns the composed maps for the base maps, or false to bind the base maps. The composer keeps the returned
        /// textures and updates them when its plants or the base maps change.
        /// </summary>
        /// <param name="coverage">Base coverage: R closure, G cast share.</param>
        /// <param name="canopy">Base canopy: A own-colour share, RGB own colour times the share. Can be null.</param>
        /// <param name="area">Minimum X, minimum Z, size X and size Z of the maps, in meters.</param>
        /// <param name="colorScale">Scale of the canopy colour.</param>
        bool Compose(Texture coverage, Texture canopy, Vector4 area, float colorScale, out Texture composedCoverage,
            out Texture composedCanopy);
    }
}
