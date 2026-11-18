using System.Runtime.CompilerServices;

// Only the terrain editor uses the decoder. The bundled StbImageSharp types are internal (STBSHARP_INTERNAL), so they
// cannot conflict with another copy of StbImageSharp in the project.
[assembly: InternalsVisibleTo("LoogaSoft.Terrain.Editor")]
