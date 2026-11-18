using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// A coarse mask of where an area body has water, filled from its outlines. The ocean near grid reads it outside the
    /// shore map. Without it, the grid takes all ground outside the shore map for open sea and draws the sea over land
    /// that lies below the sea level, for example a valley inside an island.
    /// </summary>
    internal sealed class WaterCoverageMask
    {
        // Smallest texel size, in metres, and the largest texture side, in texels.
        private const float MinimumTexel = 4f;
        private const int MaximumSize = 2048;
        // Texels of margin around the masked outlines.
        private const int Margin = 2;

        private static readonly List<List<float>> Crossings = new();

        private Texture2D _texture;
        private Rect _rect;
        private bool _openOutside;

        public Texture2D Texture => _texture;
        public bool IsBuilt => _texture;
        public Rect Rect => _rect;
        /// <summary>True when the ground outside the mask rectangle is open water.</summary>
        public bool OpenOutside => _openOutside;
        /// <summary>xy = world XZ origin, zw = 1 / size. Zero when there is no mask.</summary>
        public Vector4 UVRect => IsBuilt
            ? new Vector4(_rect.x, _rect.y, 1f / _rect.width, 1f / _rect.height)
            : Vector4.zero;

        /// <summary>
        /// Builds the mask from the world XZ outlines with the even-odd rule. A body with a far outline masks only the
        /// bounds of its inner outlines (holes), and the ground outside them is open water. Other bodies mask the
        /// bounds of all outlines, and the ground outside them is dry.
        /// </summary>
        public void Build(IReadOnlyList<List<Vector2>> outlines, bool farOutline)
        {
            int first = farOutline ? 1 : 0;
            if (outlines.Count <= first)
            {
                Release();
                return;
            }

            Vector2 min = new(float.MaxValue, float.MaxValue);
            Vector2 max = new(float.MinValue, float.MinValue);
            for (int index = first; index < outlines.Count; index++)
            {
                foreach (Vector2 point in outlines[index])
                {
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
            }
            Vector2 size = max - min;
            float texel = Mathf.Max(MinimumTexel, Mathf.Max(size.x, size.y) / (MaximumSize - 2 * Margin));
            int width = Mathf.Clamp(Mathf.CeilToInt(size.x / texel) + 2 * Margin, 1, MaximumSize);
            int height = Mathf.Clamp(Mathf.CeilToInt(size.y / texel) + 2 * Margin, 1, MaximumSize);
            Vector2 origin = min - Vector2.one * (Margin * texel);
            _rect = new Rect(origin, new Vector2(width * texel, height * texel));
            _openOutside = farOutline;

            byte[] pixels = Fill(outlines, origin, texel, width, height);
            if (!_texture || _texture.width != width || _texture.height != height)
            {
                Release();
                _texture = new Texture2D(width, height, TextureFormat.R8, false, true)
                {
                    name = "Looga Water Coverage",
                    hideFlags = HideFlags.DontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
            }
            _texture.SetPixelData(pixels, 0);
            _texture.Apply(false, false);
        }

        /// <summary>Takes a mask that the last domain built, after a script reload.</summary>
        public void Adopt(Texture2D texture, Rect rect, bool openOutside)
        {
            Release();
            _texture = texture;
            _rect = rect;
            _openOutside = openOutside;
        }

        public void Release()
        {
            if (_texture)
            {
                if (Application.isPlaying)
                {
                    Object.Destroy(_texture);
                }
                else
                {
                    Object.DestroyImmediate(_texture);
                }
            }
            _texture = null;
        }

        // Scanline fill at the texel centres. Each outline edge adds its crossing to the rows that it spans. The
        // texels between each pair of sorted crossings are water.
        private static byte[] Fill(IReadOnlyList<List<Vector2>> outlines, Vector2 origin, float texel, int width,
            int height)
        {
            while (Crossings.Count < height)
            {
                Crossings.Add(new List<float>());
            }
            for (int row = 0; row < height; row++)
            {
                Crossings[row].Clear();
            }
            foreach (List<Vector2> outline in outlines)
            {
                for (int index = 0; index < outline.Count; index++)
                {
                    Vector2 a = outline[index];
                    Vector2 b = outline[(index + 1) % outline.Count];
                    if (Mathf.Approximately(a.y, b.y)) continue;

                    float low = Mathf.Min(a.y, b.y);
                    float high = Mathf.Max(a.y, b.y);
                    // Rows whose centre is in [low, high).
                    int firstRow = Mathf.Max(0, Mathf.CeilToInt((low - origin.y) / texel - 0.5f));
                    int lastRow = Mathf.Min(height - 1, Mathf.CeilToInt((high - origin.y) / texel - 0.5f) - 1);
                    for (int row = firstRow; row <= lastRow; row++)
                    {
                        float y = origin.y + (row + 0.5f) * texel;
                        float t = (y - a.y) / (b.y - a.y);
                        Crossings[row].Add(a.x + t * (b.x - a.x));
                    }
                }
            }

            var pixels = new byte[width * height];
            for (int row = 0; row < height; row++)
            {
                List<float> crossings = Crossings[row];
                crossings.Sort();
                for (int pair = 0; pair + 1 < crossings.Count; pair += 2)
                {
                    int start = Mathf.Max(0, Mathf.CeilToInt((crossings[pair] - origin.x) / texel - 0.5f));
                    int end = Mathf.Min(width - 1, Mathf.CeilToInt((crossings[pair + 1] - origin.x) / texel - 0.5f) - 1);
                    for (int column = start; column <= end; column++)
                    {
                        pixels[row * width + column] = 255;
                    }
                }
            }
            return pixels;
        }
    }
}
