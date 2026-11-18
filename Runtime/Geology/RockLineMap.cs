using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>One family of thin lines in a rock line map. The lines of a family share a direction.</summary>
    [Serializable]
    public struct RockLineSet
    {
        [Min(0), Tooltip("Number of lines.")]
        public int Count;
        [Tooltip("Direction in degrees. 0 runs along U, 90 runs along V.")]
        public float Angle;
        [Min(0f), Tooltip("Random change of the direction from line to line, in degrees.")]
        public float Spread;
        [Tooltip("Smallest and largest length, as a share of the map width.")]
        public Vector2 Length;
        [Min(0.5f), Tooltip("Width in pixels at the middle of a line. The line narrows toward its ends.")]
        public float Width;
        [Min(0f), Tooltip("Slow bend of a line, in degrees.")]
        public float Sweep;
        [Min(0f), Tooltip("Small direction changes, in degrees. High values give a jagged line.")]
        public float Jag;
        [Range(0f, 1f), Tooltip("Share of the lines that are dark cracks. The other lines are pale veins.")]
        public float Crack;
        [Range(0f, 1f), Tooltip("Share of the lines that end at the first line they meet, so younger lines stop at older lines.")]
        public float Stop;
        [Range(0f, 1f), Tooltip("Share of the lines that start on an older line and grow one way, so the network branches.")]
        public float Branch;
        [Range(0f, 1f), Tooltip("Share of the lines that grow past their length until they meet an older line.")]
        public float Close;
    }

    /// <summary>
    /// Draws a tiling map of thin lines: R pale veins, G dark cracks, B a soft halo around the lines. Sets draw in list order,
    /// so a later set is younger and can end at an earlier set. Younger cracks that start on older cracks and run to the next
    /// crack give T junctions, as in jointed rock. Drying mud has Y junctions.
    /// A second map holds the steps of the network: the cracks divide the map into plates, and each plate has its own flat height.
    /// </summary>
    public static class RockLineMap
    {
        // A line keeps one small direction change for this many pixels, so a jagged line has straight parts.
        private const int KinkPixels = 7;
        private const int BlurPasses = 6;
        // A closing line can grow to this many times its length.
        private const int CloseGrowth = 4;
        // A crack pixel above this value divides two plates.
        private const float PlateBarrier = 0.25f;
        // Largest distance from a crack pixel to its plate, in pixels.
        private const int PlateReach = 8;
        // Distance of the height test beside a crack, in pixels.
        private const int LowSideReach = 3;
        // Darkness of the high side of a step. The low side has the full crack.
        private const float HighSideCrack = 0.3f;

        /// <summary>Draws the maps. The caller owns the textures.</summary>
        /// <param name="sets">The line families, oldest first.</param>
        /// <param name="size">Width and height of the maps in pixels.</param>
        /// <param name="haloRadius">Reach of the halo in pixels.</param>
        /// <param name="seed">The random seed.</param>
        /// <param name="steps">Tangent normals of the plate steps.</param>
        /// <returns>The line map.</returns>
        public static Texture2D Draw(IReadOnlyList<RockLineSet> sets, int size, int haloRadius, int seed, out Texture2D steps)
        {
            var random = new System.Random(seed);
            var veins = new float[size * size];
            var cracks = new float[size * size];
            var owners = new int[size * size];
            var path = new List<Vector2>();
            int line = 0;
            foreach (RockLineSet set in sets)
            {
                for (int index = 0; index < set.Count; index++)
                {
                    line++;
                    DrawLine(set, line, size, random, random.NextDouble() < set.Crack ? cracks : veins, owners, path);
                }
            }

            float[] heights = PlateHeights(cracks, size, random);
            MarkLowSide(cracks, heights, size);
            float[] halo = Halo(veins, cracks, size, haloRadius);
            var pixels = new Color32[size * size];
            for (int index = 0; index < pixels.Length; index++)
            {
                pixels[index] = new Color32(ToByte(veins[index]), ToByte(cracks[index]), ToByte(halo[index]), 255);
            }

            var map = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            map.SetPixels32(pixels);
            map.Apply(false);
            steps = StepNormals(heights, size);
            return map;
        }

        // A line grows both ways from its start, so both ends can stop at older lines. A branch grows one way from its parent.
        private static void DrawLine(in RockLineSet set, int line, int size, System.Random random, float[] target, int[] owners,
            List<Vector2> path)
        {
            bool branches = path.Count > 0 && Next(random) < set.Branch;
            Vector2 start = branches ? path[random.Next(path.Count)] : new Vector2(Next(random) * size, Next(random) * size);
            float startX = start.x;
            float startY = start.y;
            float angle = set.Angle + (Next(random) * 2f - 1f) * set.Spread;
            float length = Mathf.Lerp(set.Length.x, set.Length.y, Next(random)) * size;
            bool stops = Next(random) < set.Stop;
            bool closes = Next(random) < set.Close;
            // A branch starts inside the stamp of its parent, so the first steps do not count as a meeting.
            int grace = branches ? Mathf.CeilToInt(set.Width) + 2 : 0;
            int branchWay = Next(random) < 0.5f ? 1 : -1;
            float bendPhase = Next(random) * Mathf.PI * 2f;
            float bendRate = Mathf.PI * 2f / Mathf.Max(length, 1f);
            float wavePhase = Next(random) * Mathf.PI * 2f;
            float fadePhase = Next(random) * Mathf.PI * 2f;
            int steps = Mathf.RoundToInt(length * 0.5f);
            int limit = closes ? steps * CloseGrowth : steps;
            for (int way = 1; way >= -1; way -= 2)
            {
                if (branches && way != branchWay) continue;
                float x = startX;
                float y = startY;
                float kink = 0f;
                for (int step = 0; step < limit; step++)
                {
                    if (step % KinkPixels == 0) kink = Mathf.Lerp(kink, (Next(random) * 2f - 1f) * set.Jag, 0.6f);
                    // One long bend with a faster wave on it. A single bend reads as a drawn stroke.
                    float bend = Mathf.Sin(bendPhase + step * way * bendRate) + 0.6f * Mathf.Sin(wavePhase + step * way * bendRate * 3.7f);
                    float heading = (angle + kink + set.Sweep * bend) * Mathf.Deg2Rad;
                    x += Mathf.Cos(heading) * way;
                    y += Mathf.Sin(heading) * way;
                    int owner = owners[Wrap(Mathf.RoundToInt(x), size) + Wrap(Mathf.RoundToInt(y), size) * size];
                    if (stops && step > grace && owner != 0 && owner != line) break;

                    float along = (float)step / limit;
                    float taper = Mathf.Sqrt(1f - along * along * along);
                    // The strength rises and falls along the line, so a long line is not one even stroke.
                    float strength = 0.65f + 0.35f * Mathf.Sin(fadePhase + step * way * bendRate * 5f);
                    float swell = 0.8f + 0.2f * Mathf.Sin(wavePhase + step * way * bendRate * 11f);
                    Stamp(target, owners, line, size, x, y, set.Width * 0.5f * taper * swell, strength);
                    path.Add(new Vector2(Wrap(Mathf.RoundToInt(x), size), Wrap(Mathf.RoundToInt(y), size)));
                }
            }
        }

        // Fills each region between the cracks with one random height. A region with an open crack keeps one height on both
        // sides of the crack, so a crack tip has no step. Steps appear only where the network closes.
        private static float[] PlateHeights(float[] cracks, int size, System.Random random)
        {
            int count = size * size;
            var labels = new int[count];
            var plateHeights = new List<float> { 0f };
            var stack = new Stack<int>();
            for (int first = 0; first < count; first++)
            {
                if (labels[first] != 0 || cracks[first] > PlateBarrier) continue;
                int label = plateHeights.Count;
                plateHeights.Add(Next(random) * 2f - 1f);
                labels[first] = label;
                stack.Push(first);
                while (stack.Count > 0)
                {
                    int index = stack.Pop();
                    int x = index % size;
                    int y = index / size;
                    Claim(Wrap(x + 1, size) + y * size, label, labels, cracks, stack);
                    Claim(Wrap(x - 1, size) + y * size, label, labels, cracks, stack);
                    Claim(x + Wrap(y + 1, size) * size, label, labels, cracks, stack);
                    Claim(x + Wrap(y - 1, size) * size, label, labels, cracks, stack);
                }
            }

            // The pixels of a crack take the label of the plate beside them.
            for (int pass = 0; pass < PlateReach; pass++)
            {
                var grown = (int[])labels.Clone();
                for (int index = 0; index < count; index++)
                {
                    if (labels[index] != 0) continue;
                    int x = index % size;
                    int y = index / size;
                    int found = Mathf.Max(Mathf.Max(labels[Wrap(x + 1, size) + y * size], labels[Wrap(x - 1, size) + y * size]),
                        Mathf.Max(labels[x + Wrap(y + 1, size) * size], labels[x + Wrap(y - 1, size) * size]));
                    grown[index] = found;
                }
                labels = grown;
            }

            var heights = new float[count];
            for (int index = 0; index < count; index++)
            {
                heights[index] = plateHeights[labels[index]];
            }
            return heights;
        }

        private static void Claim(int index, int label, int[] labels, float[] cracks, Stack<int> stack)
        {
            if (labels[index] != 0 || cracks[index] > PlateBarrier) return;
            labels[index] = label;
            stack.Push(index);
        }

        // The shadow of a step lies on its low side. The high side keeps a little of the crack, so a line does not vanish where
        // a step is small.
        private static void MarkLowSide(float[] cracks, float[] heights, int size)
        {
            for (int index = 0; index < cracks.Length; index++)
            {
                if (cracks[index] <= 0f) continue;
                int x = index % size;
                int y = index / size;
                float highest = Mathf.Max(
                    Mathf.Max(heights[Wrap(x + LowSideReach, size) + y * size], heights[Wrap(x - LowSideReach, size) + y * size]),
                    Mathf.Max(heights[x + Wrap(y + LowSideReach, size) * size], heights[x + Wrap(y - LowSideReach, size) * size]));
                float low = Mathf.Clamp01((highest - heights[index]) * 4f);
                cracks[index] *= Mathf.Lerp(HighSideCrack, 1f, low);
            }
        }

        // The height is flat on each plate, so the normal changes only along the steps. A short blur stops the mip chain and the
        // block compression from breaking the step into dots.
        private static Texture2D StepNormals(float[] heights, int size)
        {
            float[] soft = heights;
            for (int pass = 0; pass < 2; pass++)
            {
                var target = new float[size * size];
                BlurRows(soft, target, size, 1);
                soft = target;
            }

            var pixels = new Color32[size * size];
            for (int index = 0; index < pixels.Length; index++)
            {
                int x = index % size;
                int y = index / size;
                var normal = new Vector3(soft[Wrap(x - 1, size) + y * size] - soft[Wrap(x + 1, size) + y * size],
                    soft[x + Wrap(y - 1, size) * size] - soft[x + Wrap(y + 1, size) * size], 1f).normalized;
                pixels[index] = new Color32(ToByte(normal.x * 0.5f + 0.5f), ToByte(normal.y * 0.5f + 0.5f),
                    ToByte(normal.z * 0.5f + 0.5f), 255);
            }

            var map = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            map.SetPixels32(pixels);
            map.Apply(false);
            return map;
        }

        private static void Stamp(float[] target, int[] owners, int line, int size, float x, float y, float radius, float strength)
        {
            int reach = Mathf.CeilToInt(radius + 1f);
            int centerX = Mathf.RoundToInt(x);
            int centerY = Mathf.RoundToInt(y);
            for (int offsetY = -reach; offsetY <= reach; offsetY++)
            {
                for (int offsetX = -reach; offsetX <= reach; offsetX++)
                {
                    float deltaX = centerX + offsetX - x;
                    float deltaY = centerY + offsetY - y;
                    // One pixel of soft border gives a clean line.
                    float cover = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(deltaX * deltaX + deltaY * deltaY));
                    if (cover <= 0f) continue;

                    int index = Wrap(centerX + offsetX, size) + Wrap(centerY + offsetY, size) * size;
                    target[index] = Mathf.Max(target[index], cover * strength);
                    owners[index] = line;
                }
            }
        }

        // The halo follows the cracks, with a weaker part for the veins.
        private static float[] Halo(float[] veins, float[] cracks, int size, int radius)
        {
            var values = new float[size * size];
            var scratch = new float[size * size];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = Mathf.Max(cracks[index], veins[index] * 0.5f);
            }

            for (int pass = 0; pass < BlurPasses; pass++)
            {
                BlurRows(values, scratch, size, radius);
                (values, scratch) = (scratch, values);
            }

            float peak = 1e-5f;
            foreach (float value in values) peak = Mathf.Max(peak, value);
            for (int index = 0; index < values.Length; index++)
            {
                // The root widens the soft outer part, and the peaks at line crossings do not set the whole level.
                values[index] = Mathf.Sqrt(values[index] / peak);
            }

            return values;
        }

        // A box blur along the rows. The result is written turned by 90 degrees, so the next pass blurs the other axis.
        private static void BlurRows(float[] source, float[] target, int size, int radius)
        {
            float scale = 1f / (radius * 2 + 1);
            for (int row = 0; row < size; row++)
            {
                int start = row * size;
                float sum = 0f;
                for (int offset = -radius; offset <= radius; offset++)
                {
                    sum += source[start + Wrap(offset, size)];
                }

                for (int column = 0; column < size; column++)
                {
                    target[column * size + row] = sum * scale;
                    sum += source[start + Wrap(column + radius + 1, size)] - source[start + Wrap(column - radius, size)];
                }
            }
        }

        private static float Next(System.Random random) => (float)random.NextDouble();

        private static int Wrap(int value, int size) => (value % size + size) % size;

        private static byte ToByte(float value) => (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);
    }
}
