using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>
    /// Writes the tileable foam detail texture of the Looga/Water shader. R = clumps, G = bubbles, B = cellular lace,
    /// A = height. Each of R, G and B is equalized, so the part of the texture above 1 - c is about c. The shader can
    /// then use the foam coverage as the threshold of each channel, and the foam breaks into lace and bubbles where
    /// the coverage is low.
    /// </summary>
    internal static class FoamDetailTextureGenerator
    {
        private const string AssetPath = "Packages/com.loogasoft.loogaterrain/Runtime/Water/Textures/LoogaFoamDetail.png";
        private const int Size = 512;
        private const int Seed = 1931;

        [MenuItem("Tools/Looga/Terrain/Generate Foam Detail Texture")]
        private static void Generate()
        {
            var clumps = new float[Size * Size];
            var bubbles = new float[Size * Size];
            var lace = new float[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float u = (x + 0.5f) / Size;
                    float v = (y + 0.5f) / Size;
                    int index = y * Size + x;
                    // Clumps: soft patches from value noise, broken by large cells.
                    float noise = Fbm(u, v, 4, 5, Seed);
                    Worley(u, v, 7, Seed + 11, out float cellNear, out _);
                    clumps[index] = noise * 0.75f + (1f - Mathf.Clamp01(cellNear * 1.3f)) * 0.25f;
                    // Lace: thin walls between foam cells. The value falls with the distance from the wall, so a lower
                    // threshold makes the walls thicker until the cells close. A warp bends the walls, and the cell
                    // size changes over the texture, so the cells are round and not a regular crackle pattern.
                    float warpU = u + (Fbm(u, v, 6, 3, Seed + 91) - 0.5f) * 0.06f;
                    float warpV = v + (Fbm(u, v, 6, 3, Seed + 97) - 0.5f) * 0.06f;
                    Worley(warpU, warpV, 18, Seed + 23, out float near, out float second);
                    Worley(warpU, warpV, 34, Seed + 37, out float fineNear, out float fineSecond);
                    float wall = 1f - Mathf.Clamp01((second - near) / 0.6f);
                    float fineWall = 1f - Mathf.Clamp01((fineSecond - fineNear) / 0.6f);
                    float scale = Smoothstep(0.35f, 0.65f, Fbm(u, v, 3, 2, Seed + 43));
                    float holes = Fbm(u, v, 8, 3, Seed + 51);
                    lace[index] = Mathf.Lerp(wall, fineWall, scale) * (0.6f + 0.4f * holes);
                }
            }
            AddBubbles(bubbles);
            Equalize(clumps);
            Equalize(bubbles);
            Equalize(lace);

            // Height: dense foam is high and bubbles are domes. A small blur keeps the derivatives smooth.
            var height = new float[Size * Size];
            for (int index = 0; index < height.Length; index++)
            {
                height[index] = 0.5f * clumps[index] + 0.3f * bubbles[index] + 0.2f * lace[index];
            }
            height = Blur(height, 2);
            Normalize(height);

            var pixels = new Color32[Size * Size];
            for (int index = 0; index < pixels.Length; index++)
            {
                pixels[index] = new Color32(ToByte(clumps[index]), ToByte(bubbles[index]), ToByte(lace[index]),
                    ToByte(height[index]));
            }
            WriteTexture(AssetPath, pixels);
            Debug.Log($"Looga water: wrote the foam detail texture to {AssetPath}.");
        }

        // Writes the pixels as a linear PNG with mips that repeats.
        private static void WriteTexture(string assetPath, Color32[] pixels)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);
            texture.SetPixels32(pixels);
            texture.Apply(false);
            string fullPath = Path.GetFullPath(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(assetPath) is TextureImporter importer)
            {
                importer.sRGBTexture = false;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = false;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 4;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }
        }

        // Bubbles of many sizes. Each bubble has its own value, so a higher threshold removes whole bubbles first. The
        // rim of a bubble is brighter than its centre.
        private static void AddBubbles(float[] bubbles)
        {
            var random = new System.Random(Seed + 71);
            for (int count = 0; count < 4500; count++)
            {
                float radius = Mathf.Lerp(1f, 6f, Mathf.Pow((float)random.NextDouble(), 2.2f));
                float cx = (float)random.NextDouble() * Size;
                float cy = (float)random.NextDouble() * Size;
                float value = 0.15f + 0.85f * (float)random.NextDouble();
                int reach = Mathf.CeilToInt(radius + 1f);
                for (int dy = -reach; dy <= reach; dy++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        int px = Mathf.FloorToInt(cx) + dx;
                        int py = Mathf.FloorToInt(cy) + dy;
                        float distance = Mathf.Sqrt(Sq(px + 0.5f - cx) + Sq(py + 0.5f - cy)) / radius;
                        if (distance > 1.1f) continue;
                        float rim = Smoothstep(0.55f, 0.95f, distance) * (1f - Smoothstep(0.95f, 1.1f, distance));
                        float shape = Mathf.Max(rim, distance < 0.95f ? 0.7f : 0f);
                        int index = Wrap(py) * Size + Wrap(px);
                        bubbles[index] = Mathf.Max(bubbles[index], shape * value);
                    }
                }
            }
        }

        // Replaces each value by its rank, so the values spread evenly from 0 to 1. Equal values get the same rank, so
        // the empty area of the bubbles stays 0.
        private static void Equalize(float[] values)
        {
            var order = new int[values.Length];
            for (int index = 0; index < order.Length; index++) order[index] = index;
            var keys = (float[])values.Clone();
            Array.Sort(keys, order);
            int first = 0;
            for (int rank = 0; rank < order.Length; rank++)
            {
                if (keys[rank] != keys[first]) first = rank;
                values[order[rank]] = first / (float)(order.Length - 1);
            }
        }

        private static void Normalize(float[] values)
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            foreach (float value in values)
            {
                min = Mathf.Min(min, value);
                max = Mathf.Max(max, value);
            }
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = (values[index] - min) / Mathf.Max(max - min, 1e-6f);
            }
        }

        // Box blur that wraps at the edges, so the texture stays tileable.
        private static float[] Blur(float[] values, int radius)
        {
            var result = new float[values.Length];
            float scale = 1f / Sq(2 * radius + 1);
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float sum = 0f;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            sum += values[Wrap(y + dy) * Size + Wrap(x + dx)];
                        }
                    }
                    result[y * Size + x] = sum * scale;
                }
            }
            return result;
        }

        // Tileable value noise with octaves. The period is the number of lattice cells across the texture.
        private static float Fbm(float u, float v, int period, int octaves, int seed)
        {
            float sum = 0f;
            float amplitude = 0.5f;
            float total = 0f;
            for (int octave = 0; octave < octaves; octave++)
            {
                sum += ValueNoise(u * period, v * period, period, seed + octave * 101) * amplitude;
                total += amplitude;
                amplitude *= 0.5f;
                period *= 2;
            }
            return sum / total;
        }

        private static float ValueNoise(float x, float y, int period, int seed)
        {
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            float fx = x - x0;
            float fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash(x0, y0, period, seed);
            float b = Hash(x0 + 1, y0, period, seed);
            float c = Hash(x0, y0 + 1, period, seed);
            float d = Hash(x0 + 1, y0 + 1, period, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        // Distances to the nearest and second nearest feature point, in cell units. One jittered point per cell.
        private static void Worley(float u, float v, int period, int seed, out float near, out float second)
        {
            float x = u * period;
            float y = v * period;
            int cellX = Mathf.FloorToInt(x);
            int cellY = Mathf.FloorToInt(y);
            near = float.MaxValue;
            second = float.MaxValue;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = cellX + dx;
                    int cy = cellY + dy;
                    float px = cx + 0.1f + 0.8f * Hash(cx, cy, period, seed);
                    float py = cy + 0.1f + 0.8f * Hash(cx, cy, period, seed + 7);
                    float distance = Mathf.Sqrt(Sq(px - x) + Sq(py - y));
                    if (distance < near)
                    {
                        second = near;
                        near = distance;
                    }
                    else if (distance < second)
                    {
                        second = distance;
                    }
                }
            }
        }

        // Value from 0 to 1 for a lattice point. The point wraps with the period, so the texture tiles.
        private static float Hash(int x, int y, int period, int seed)
        {
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }

        private static int Wrap(int value)
        {
            return ((value % Size) + Size) % Size;
        }

        private static float Smoothstep(float edge0, float edge1, float value)
        {
            float t = Mathf.Clamp01((value - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        private static float Sq(float value)
        {
            return value * value;
        }

        private static byte ToByte(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
        }
    }
}
