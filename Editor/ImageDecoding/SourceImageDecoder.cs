using System;
using StbImageSharp;

namespace LoogaSoft.Terrain.ImageDecoding.Editor
{
    /// <summary>
    /// Decodes PNG, JPG and TGA files to 8-bit RGBA pixels without Unity APIs, so the array builder can decode several
    /// source files on worker threads. It uses the bundled StbImageSharp (public domain).
    /// </summary>
    internal static class SourceImageDecoder
    {
        /// <summary>Decodes an image file. Safe to call from several threads at the same time.</summary>
        /// <param name="file">Contents of the image file.</param>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels.</param>
        /// <param name="rgba">RGBA32 pixels in Unity order: the bottom row first.</param>
        /// <returns>False when the file is not a supported image.</returns>
        public static bool TryDecode(byte[] file, out int width, out int height, out byte[] rgba)
        {
            width = 0;
            height = 0;
            rgba = null;
            ImageResult image;
            try
            {
                image = ImageResult.FromMemory(file, ColorComponents.RedGreenBlueAlpha);
            }
            catch (Exception)
            {
                // StbImageSharp throws for unsupported or damaged files. The builder then uses the imported texture.
                return false;
            }
            width = image.Width;
            height = image.Height;
            // StbImageSharp gives the top row first. Unity textures start with the bottom row.
            int rowBytes = width * 4;
            rgba = new byte[image.Data.Length];
            for (int row = 0; row < height; row++)
            {
                Buffer.BlockCopy(image.Data, row * rowBytes, rgba, (height - 1 - row) * rowBytes, rowBytes);
            }
            return true;
        }
    }
}
