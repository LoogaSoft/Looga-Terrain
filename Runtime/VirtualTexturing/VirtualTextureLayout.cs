using System;
using UnityEngine;

namespace LoogaSoft.Terrain
{
    /// <summary>
    /// Maps virtual page coordinates to a compact mip-pyramid table.
    /// </summary>
    public sealed class VirtualTextureLayout
    {
        /// <summary>Gets the number of pages along mip zero.</summary>
        public int PagesPerAxis { get; }
        /// <summary>Gets the coarsest mip index.</summary>
        public int LastMip { get; }
        /// <summary>Gets the number of entries across all mips.</summary>
        public int EntryCount { get; }

        /// <summary>Creates a layout with a power-of-two page count.</summary>
        public VirtualTextureLayout(int pagesPerAxis)
        {
            if (pagesPerAxis < 1 || pagesPerAxis > 256 || !Mathf.IsPowerOfTwo(pagesPerAxis))
            {
                throw new ArgumentOutOfRangeException(nameof(pagesPerAxis));
            }
            PagesPerAxis = pagesPerAxis;
            LastMip = (int)Mathf.Log(pagesPerAxis, 2);
            EntryCount = (pagesPerAxis * pagesPerAxis * 4 - 1) / 3;
        }

        /// <summary>Gets the table index for one page.</summary>
        public int Index(int x, int y, int mip)
        {
            int width = PagesPerAxis >> mip;
            if (mip < 0 || mip > LastMip || x < 0 || y < 0 || x >= width || y >= width)
            {
                throw new ArgumentOutOfRangeException(nameof(mip));
            }
            int offset = (PagesPerAxis * PagesPerAxis * 4 - width * width * 4) / 3;
            return offset + y * width + x;
        }

        /// <summary>Gets coordinates and mip from a table index.</summary>
        public Vector3Int Decode(int index)
        {
            if (index < 0 || index >= EntryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            int width = PagesPerAxis;
            int mip = 0;
            while (index >= width * width)
            {
                index -= width * width;
                width >>= 1;
                mip++;
            }
            return new Vector3Int(index % width, index / width, mip);
        }

        /// <summary>Gets the parent page. The root returns itself.</summary>
        public int Parent(int index)
        {
            Vector3Int page = Decode(index);
            return page.z == LastMip ? index : Index(page.x / 2, page.y / 2, page.z + 1);
        }
    }
}
