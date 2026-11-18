# StbImageSharp

Bundled third-party source for the terrain surface array builder (editor only).

- Project: https://github.com/StbSharp/StbImageSharp
- Version: 2.30.16 (commit d0094beef983fc8d0b1d4d3c61ddac75635abd12, 2026-08-08)
- License: Public Domain (the README of this commit). StbImageSharp is a C# port of stb_image by Sean Barrett
  (https://github.com/nothings/stb), which is also public domain.
- Files: the `src` folder of the commit, without changes. The `StbImageSharp.csproj` file is not included.
- The assembly defines `STBSHARP_INTERNAL`, so all StbImageSharp types are internal and cannot conflict with another
  copy of StbImageSharp in a project.

To update: replace the `.cs` files with the `src` folder of a newer commit, update this file, and check the array
builder decode against Unity `ImageConversion.LoadImage` on the project source textures.
