using UnityEngine;

namespace LoogaSoft.Terrain.Surface
{
    /// <summary>
    /// Asset file that holds the texture arrays of one surface layout as sub-assets. The file is binary also in a
    /// project that uses text serialization: a text file stores each array as hexadecimal text, which doubles its size
    /// and makes saving and loading slow. The array builder creates and updates this asset.
    /// </summary>
    [PreferBinarySerialization]
    public sealed class LoogaTerrainSurfaceArrays : ScriptableObject
    {
    }
}
