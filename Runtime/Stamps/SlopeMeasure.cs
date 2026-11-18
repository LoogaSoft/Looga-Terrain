namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>How the slope filter of a placement or clear stamp measures the slope.</summary>
    public enum SlopeMeasure
    {
        /// <summary>Like MicroVerse: the value also grows with the terrain height. Existing content uses it.</summary>
        Parity,

        /// <summary>The true angle of the ground in degrees, from the height differences in world units.</summary>
        Steepness
    }
}
