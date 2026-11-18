using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>How a cell vertex finds its position.</summary>
    public enum RockVertexPlacement
    {
        // Mean of the edge crossings (surface nets). Sharp edges round off. The cube test uses this as its baseline.
        Mean,
        // Dual contouring. A cube test gave 0.25 cells of corner gap, against 0.69 for the mean.
        Feature,
        // Dual contouring with a weak pull to the mean. A vertex can leave its cell by a small share, so corners stay sharp.
        Sharp
    }

    /// <summary>
    /// Dual contouring of a rock field (Ju et al. 2002). Each cell with rock and air corners gets one vertex where the
    /// surface planes at the edge crossings meet. Each grid edge with rock at one end gives one quad of the four cells
    /// around it. Positions are in grid units.
    /// </summary>
    internal static class RockDualContour
    {
        // Pull of the vertex to the mean of the crossings. Flat cells have no unique solution without the pull.
        public const float MeanPull = 0.05f;
        // Largest mean distance of a vertex to its crossing planes, in cells.
        public const float PlaneTolerance = 0.2f;
        // Pull to the mean of Sharp placement.
        public const float SharpPull = 0.015f;
        // Share of the cell size that a vertex of Sharp placement can pass the cell.
        public const float SharpBounds = 0.15f;
        // Share of the solved point in the vertex of Sharp placement. The rest is the mean.
        public const float SharpBlend = 0.96f;

        /// <summary>Builds the mesh. The caller disposes the lists.</summary>
        public static void Build(NativeArray<float> field, int3 size, RockVertexPlacement placement,
            out NativeList<float3> positions, out NativeList<int> indices)
        {
            var cellCount = (size.x - 1) * (size.y - 1) * (size.z - 1);
            var cellVertices = new NativeArray<int>(cellCount, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            var activeCells = new NativeList<int>(cellCount / 32, Allocator.TempJob);
            indices = new NativeList<int>(cellCount / 8, Allocator.Persistent);
            new RockCellScanJob
            {
                Field = field, Size = size, CellVertices = cellVertices, ActiveCells = activeCells
            }.Run();

            positions = new NativeList<float3>(activeCells.Length, Allocator.Persistent);
            positions.ResizeUninitialized(activeCells.Length);
            var sharpness = new NativeArray<float>(activeCells.Length, Allocator.TempJob);
            new RockCellSolveJob
            {
                Field = field, ActiveCells = activeCells.AsArray(), Size = size, Placement = placement,
                Positions = positions.AsArray(), Sharpness = sharpness
            }.Schedule(activeCells.Length, 256).Complete();

            new RockQuadJob
            {
                Field = field, CellVertices = cellVertices, Positions = positions.AsArray(), Sharpness = sharpness,
                Size = size, Indices = indices
            }.Run();

            sharpness.Dispose();
            activeCells.Dispose();
            cellVertices.Dispose();
        }

        /// <summary>
        /// Keeps the largest island of rock and fills air pockets with no way out. Returns the number of pieces removed.
        /// The test runs on the field, not on the mesh: one dual contouring vertex can pinch a chip to the rock, and a mesh
        /// test then finds one piece.
        /// </summary>
        public static int CleanIslands(NativeArray<float> field, int3 size)
        {
            int removed = KeepLargestIsland(field, size);
            // The same search on the turned field: the largest island of air is the air around the rock.
            Negate(field);
            removed += KeepLargestIsland(field, size);
            Negate(field);
            return removed;
        }

        /// <summary>
        /// Removes thick parts that hang on the rock through a neck narrower than the width, and the neck too. Thick means
        /// that a ball of the width fits inside. Thin edges and ridges stay, because no ball fits in them. Returns the
        /// number of parts removed. Run it after <see cref="CleanIslands"/>.
        /// </summary>
        public static int RemoveNeckedParts(NativeArray<float> field, int3 size, float cell, float neckWidth)
        {
            if (neckWidth <= 0f) return 0;
            float radius = 0.5f * neckWidth / cell;
            int reach = (int)math.ceil(radius);
            var ball = new NativeList<int3>(Allocator.TempJob);
            for (int z = -reach; z <= reach; z++)
            {
                for (int y = -reach; y <= reach; y++)
                {
                    for (int x = -reach; x <= reach; x++)
                    {
                        if (x * x + y * y + z * z <= radius * radius) ball.Add(new int3(x, y, z));
                    }
                }
            }
            var removed = new NativeReference<int>(Allocator.TempJob);
            new RockNeckFilterJob
            {
                Field = field, Size = size, Depth = 0.5f * neckWidth, Ball = ball.AsArray(),
                NeckLimit = (int)math.ceil(NeckLength / cell), Removed = removed
            }.Run();
            int parts = removed.Value;
            removed.Dispose();
            ball.Dispose();
            return parts;
        }

        // Longest neck that goes with a removed part, in meters. The limit keeps the removal off the long thin edges of
        // the rock when a neck starts on one.
        private const float NeckLength = 2f;

        private static int KeepLargestIsland(NativeArray<float> field, int3 size)
        {
            var parents = new NativeArray<int>(field.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var counts = new NativeArray<int>(field.Length, Allocator.TempJob);
            var removed = new NativeReference<int>(Allocator.TempJob);
            new RockIslandFilterJob
            {
                Field = field, Size = size, Parents = parents, Counts = counts, Removed = removed
            }.Run();
            int removedIslands = removed.Value;
            removed.Dispose();
            counts.Dispose();
            parents.Dispose();
            return removedIslands;
        }

        private static void Negate(NativeArray<float> field)
        {
            for (int index = 0; index < field.Length; index++)
            {
                field[index] = -field[index];
            }
        }
    }

    /// <summary>Finds the connected islands of rock samples with a union-find. All islands but the largest turn to air.</summary>
    [BurstCompile]
    internal struct RockIslandFilterJob : IJob
    {
        public NativeArray<float> Field;
        public NativeArray<int> Parents;
        public NativeArray<int> Counts;
        public NativeReference<int> Removed;
        public int3 Size;

        public void Execute()
        {
            int planeSize = Size.x * Size.y;
            int sample = 0;
            for (int z = 0; z < Size.z; z++)
            {
                for (int y = 0; y < Size.y; y++)
                {
                    for (int x = 0; x < Size.x; x++, sample++)
                    {
                        Parents[sample] = sample;
                        if (Field[sample] >= 0f) continue;
                        // Samples that share a face are one island. Samples that share only a corner are not.
                        if (x > 0 && Field[sample - 1] < 0f) Union(sample, sample - 1);
                        if (y > 0 && Field[sample - Size.x] < 0f) Union(sample, sample - Size.x);
                        if (z > 0 && Field[sample - planeSize] < 0f) Union(sample, sample - planeSize);
                    }
                }
            }

            for (int index = 0; index < Field.Length; index++)
            {
                if (Field[index] < 0f) Counts[Find(index)]++;
            }

            int largest = 0;
            for (int index = 0; index < Counts.Length; index++)
            {
                largest = math.max(largest, Counts[index]);
            }

            int removedIslands = 0;
            for (int index = 0; index < Counts.Length; index++)
            {
                if (Counts[index] > 0 && Counts[index] < largest) removedIslands++;
            }
            Removed.Value = removedIslands;
            if (removedIslands == 0) return;

            for (int index = 0; index < Field.Length; index++)
            {
                if (Field[index] < 0f && Counts[Find(index)] < largest) Field[index] = -Field[index];
            }
        }

        private int Find(int sample)
        {
            while (Parents[sample] != sample)
            {
                // Each step points the sample at its grandparent, so later searches are short.
                Parents[sample] = Parents[Parents[sample]];
                sample = Parents[sample];
            }
            return sample;
        }

        private void Union(int a, int b)
        {
            int rootA = Find(a);
            int rootB = Find(b);
            if (rootA != rootB) Parents[rootB] = rootA;
        }
    }

    /// <summary>
    /// Finds the thick rock (the union of all balls of the neck radius inside the rock), labels its islands with a
    /// union-find and removes the small islands. The thin rock that joins a removed island to the rest also goes, up to a
    /// limit of steps. An island of a quarter of the largest or more is a main mass and stays.
    /// </summary>
    [BurstCompile]
    internal struct RockNeckFilterJob : IJob
    {
        // An island of this share of the largest island or more is a main mass and stays.
        private const float MassShare = 0.25f;
        private const byte Air = 0, Thin = 1, Thick = 2, Gone = 3;

        public NativeArray<float> Field;
        [ReadOnly] public NativeArray<int3> Ball;
        public NativeReference<int> Removed;
        public int3 Size;
        // Ball centers are at least this deep, in meters.
        public float Depth;
        public int NeckLimit;

        public void Execute()
        {
            int count = Field.Length;
            int planeSize = Size.x * Size.y;
            var kind = new NativeArray<byte>(count, Allocator.Temp);
            for (int index = 0; index < count; index++)
            {
                kind[index] = Field[index] < 0f ? Thin : Air;
            }

            // Each ball center marks the rock in its ball as thick. A center with only centers around it adds nothing new.
            int sample = 0;
            for (int z = 0; z < Size.z; z++)
            {
                for (int y = 0; y < Size.y; y++)
                {
                    for (int x = 0; x < Size.x; x++, sample++)
                    {
                        if (!IsCenter(sample)) continue;
                        if (x > 0 && x < Size.x - 1 && y > 0 && y < Size.y - 1 && z > 0 && z < Size.z - 1 &&
                            IsCenter(sample - 1) && IsCenter(sample + 1) && IsCenter(sample - Size.x) &&
                            IsCenter(sample + Size.x) && IsCenter(sample - planeSize) && IsCenter(sample + planeSize))
                        {
                            kind[sample] = Thick;
                            continue;
                        }
                        foreach (int3 offset in Ball)
                        {
                            int3 point = new int3(x, y, z) + offset;
                            if (math.any(point < 0) || math.any(point >= Size)) continue;
                            int target = point.x + point.y * Size.x + point.z * planeSize;
                            if (kind[target] == Thin) kind[target] = Thick;
                        }
                    }
                }
            }

            var parents = new NativeArray<int>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            sample = 0;
            for (int z = 0; z < Size.z; z++)
            {
                for (int y = 0; y < Size.y; y++)
                {
                    for (int x = 0; x < Size.x; x++, sample++)
                    {
                        parents[sample] = sample;
                        if (kind[sample] != Thick) continue;
                        if (x > 0 && kind[sample - 1] == Thick) Union(parents, sample, sample - 1);
                        if (y > 0 && kind[sample - Size.x] == Thick) Union(parents, sample, sample - Size.x);
                        if (z > 0 && kind[sample - planeSize] == Thick) Union(parents, sample, sample - planeSize);
                    }
                }
            }

            var counts = new NativeArray<int>(count, Allocator.Temp);
            for (int index = 0; index < count; index++)
            {
                if (kind[index] == Thick) counts[Find(parents, index)]++;
            }
            int largest = 0;
            for (int index = 0; index < count; index++)
            {
                largest = math.max(largest, counts[index]);
            }
            int limit = (int)(largest * MassShare);
            int parts = 0;
            for (int index = 0; index < count; index++)
            {
                if (counts[index] > 0 && counts[index] < limit) parts++;
            }
            Removed.Value = parts;
            if (parts == 0) return;

            // Removed thick rock starts a walk over the thin rock, so the neck goes with its part.
            var steps = new NativeArray<int>(count, Allocator.Temp);
            var queue = new NativeList<int>(1024, Allocator.Temp);
            for (int index = 0; index < count; index++)
            {
                if (kind[index] != Thick || counts[Find(parents, index)] >= limit) continue;
                kind[index] = Gone;
                queue.Add(index);
            }
            for (int head = 0; head < queue.Length; head++)
            {
                int current = queue[head];
                if (steps[current] >= NeckLimit) continue;
                int x = current % Size.x;
                int y = current / Size.x % Size.y;
                int z = current / planeSize;
                if (x > 0) Walk(kind, steps, queue, current, current - 1);
                if (x < Size.x - 1) Walk(kind, steps, queue, current, current + 1);
                if (y > 0) Walk(kind, steps, queue, current, current - Size.x);
                if (y < Size.y - 1) Walk(kind, steps, queue, current, current + Size.x);
                if (z > 0) Walk(kind, steps, queue, current, current - planeSize);
                if (z < Size.z - 1) Walk(kind, steps, queue, current, current + planeSize);
            }

            // The sign turns, as in the island filter, so the removed rock is air at the same distance.
            for (int index = 0; index < count; index++)
            {
                if (kind[index] == Gone) Field[index] = -Field[index];
            }
        }

        private bool IsCenter(int sample)
        {
            return Field[sample] < -Depth;
        }

        private static void Walk(NativeArray<byte> kind, NativeArray<int> steps, NativeList<int> queue, int from, int to)
        {
            if (kind[to] != Thin) return;
            kind[to] = Gone;
            steps[to] = steps[from] + 1;
            queue.Add(to);
        }

        private static int Find(NativeArray<int> parents, int sample)
        {
            while (parents[sample] != sample)
            {
                parents[sample] = parents[parents[sample]];
                sample = parents[sample];
            }
            return sample;
        }

        private static void Union(NativeArray<int> parents, int a, int b)
        {
            int rootA = Find(parents, a);
            int rootB = Find(parents, b);
            if (rootA != rootB) parents[rootB] = rootA;
        }
    }

    /// <summary>Numbers the cells that the surface goes through.</summary>
    [BurstCompile]
    internal struct RockCellScanJob : IJob
    {
        [ReadOnly] public NativeArray<float> Field;
        public NativeArray<int> CellVertices;
        public NativeList<int> ActiveCells;
        public int3 Size;

        public void Execute()
        {
            int planeSize = Size.x * Size.y;
            int cell = 0;
            for (int z = 0; z < Size.z - 1; z++)
            {
                for (int y = 0; y < Size.y - 1; y++)
                {
                    int sample = (z * Size.y + y) * Size.x;
                    for (int x = 0; x < Size.x - 1; x++, cell++, sample++)
                    {
                        int rockCorners = (Field[sample] < 0f ? 1 : 0) + (Field[sample + 1] < 0f ? 1 : 0) +
                                          (Field[sample + Size.x] < 0f ? 1 : 0) +
                                          (Field[sample + Size.x + 1] < 0f ? 1 : 0) +
                                          (Field[sample + planeSize] < 0f ? 1 : 0) +
                                          (Field[sample + planeSize + 1] < 0f ? 1 : 0) +
                                          (Field[sample + planeSize + Size.x] < 0f ? 1 : 0) +
                                          (Field[sample + planeSize + Size.x + 1] < 0f ? 1 : 0);
                        if (rockCorners == 0 || rockCorners == 8)
                        {
                            CellVertices[cell] = -1;
                            continue;
                        }
                        CellVertices[cell] = ActiveCells.Length;
                        ActiveCells.Add(cell);
                    }
                }
            }
        }
    }

    /// <summary>Finds the vertex of each active cell.</summary>
    [BurstCompile]
    internal struct RockCellSolveJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Field;
        [ReadOnly] public NativeArray<int> ActiveCells;
        [WriteOnly] public NativeArray<float3> Positions;
        // 0 where the crossings of a cell lie on one plane. Larger values on a crease or a corner.
        [WriteOnly] public NativeArray<float> Sharpness;
        public int3 Size;
        public RockVertexPlacement Placement;

        public void Execute(int index)
        {
            int cell = ActiveCells[index];
            int cellsX = Size.x - 1;
            int cellsY = Size.y - 1;
            int planeCells = cellsX * cellsY;
            var origin = new int3(cell % cellsX, cell / cellsX % cellsY, cell / planeCells);

            // The matrix holds the sum of n * transpose(n). The vector holds the sum of n * dot(n, point). Points are
            // relative to the low cell corner, so the float terms stay small.
            float3 meanSum = float3.zero;
            float3 normalSum = float3.zero;
            float3x3 matrix = default;
            float3 vector = float3.zero;
            float constant = 0f;
            int crossings = 0;
            for (int edge = 0; edge < 12; edge++)
            {
                int axis = edge >> 2;
                int other = edge & 3;
                int3 low = axis == 0 ? new int3(0, other & 1, other >> 1)
                    : axis == 1 ? new int3(other & 1, 0, other >> 1)
                    : new int3(other & 1, other >> 1, 0);
                int3 high = low;
                high[axis] = 1;
                float valueLow = Value(origin + low);
                float valueHigh = Value(origin + high);
                if (valueLow < 0f == valueHigh < 0f) continue;
                float share = valueLow / (valueLow - valueHigh);
                float3 point = math.lerp(low, high, share);
                meanSum += point;
                crossings++;
                if (Placement == RockVertexPlacement.Mean) continue;

                // The two ends of an edge can lie on two faces of a crease. A blend of their gradients moved the planes
                // and increased the corner gap by about 40 %, so the nearer end decides.
                float3 gradient = Gradient(origin + (share < 0.5f ? low : high));
                float length = math.length(gradient);
                if (length < 1e-6f) continue;
                float3 normal = gradient / length;
                normalSum += normal;
                matrix.c0 += normal * normal.x;
                matrix.c1 += normal * normal.y;
                matrix.c2 += normal * normal.z;
                float offset = math.dot(normal, point);
                vector += normal * offset;
                constant += offset * offset;
            }

            float3 mean = meanSum / crossings;
            if (Placement == RockVertexPlacement.Mean)
            {
                Positions[index] = origin + mean;
                Sharpness[index] = 0f;
                return;
            }
            float3x3 planes = matrix;
            float3 targets = vector;
            bool sharp = Placement == RockVertexPlacement.Sharp;
            float pull = sharp ? RockDualContour.SharpPull : RockDualContour.MeanPull;
            matrix.c0.x += pull;
            matrix.c1.y += pull;
            matrix.c2.z += pull;
            vector += mean * pull;
            // The clamp keeps the vertex in its cell. Outside the cell, the vertex folds the quads around it.
            float3 solved = math.mul(math.inverse(matrix), vector);
            if (sharp)
            {
                bool inside = math.all(solved >= -RockDualContour.SharpBounds) && math.all(solved <= 1f + RockDualContour.SharpBounds);
                solved = inside ? math.lerp(mean, solved, RockDualContour.SharpBlend) : mean;
            }
            else
            {
                solved = math.clamp(solved, 0f, 1f);
            }
            // The mean distance from the vertex to the planes of its crossings. A large value means the planes do not meet at
            // one point, for example two surfaces in one cell. Such a vertex makes spikes and dots, so it falls back to the
            // mean of the crossings. The mean lies on the surface. The center of the cell does not.
            float error = math.dot(solved, math.mul(planes, solved)) - 2f * math.dot(solved, targets) + constant;
            if (math.sqrt(math.max(error, 0f) / crossings) > RockDualContour.PlaneTolerance) solved = mean;
            Positions[index] = origin + solved;
            Sharpness[index] = 1f - math.length(normalSum) / crossings;
        }

        private float Value(int3 sample)
        {
            sample = math.clamp(sample, 0, Size - 1);
            return Field[sample.x + Size.x * (sample.y + Size.y * sample.z)];
        }

        private float3 Gradient(int3 sample)
        {
            return new float3(OneSided(sample, new int3(1, 0, 0)), OneSided(sample, new int3(0, 1, 0)),
                OneSided(sample, new int3(0, 0, 1)));
        }

        // Takes the difference of the side with the smaller second difference, as in ENO level set schemes. That side
        // has no crease, so the normal belongs to one face.
        private float OneSided(int3 sample, int3 step)
        {
            float center = Value(sample);
            float below = Value(sample - step);
            float above = Value(sample + step);
            float curveBelow = math.abs(center - 2f * below + Value(sample - 2 * step));
            float curveAbove = math.abs(center - 2f * above + Value(sample + 2 * step));
            return curveBelow < curveAbove ? center - below : above - center;
        }
    }

    /// <summary>Makes two triangles for each grid edge that crosses the surface.</summary>
    [BurstCompile]
    internal struct RockQuadJob : IJob
    {
        [ReadOnly] public NativeArray<float> Field;
        [ReadOnly] public NativeArray<int> CellVertices;
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float> Sharpness;
        public NativeList<int> Indices;
        public int3 Size;

        // Sharpness difference below which the shorter diagonal decides.
        private const float SharpnessTie = 0.02f;

        public void Execute()
        {
            // The first and last samples have no full ring of cells, and the field keeps the surface away from them.
            for (int z = 1; z < Size.z - 1; z++)
            {
                for (int y = 1; y < Size.y - 1; y++)
                {
                    for (int x = 1; x < Size.x - 1; x++)
                    {
                        float value = Value(x, y, z);
                        AddQuad(value, Value(x + 1, y, z), Cell(x, y - 1, z - 1), Cell(x, y, z - 1), Cell(x, y, z),
                            Cell(x, y - 1, z));
                        AddQuad(value, Value(x, y + 1, z), Cell(x - 1, y, z - 1), Cell(x - 1, y, z), Cell(x, y, z),
                            Cell(x, y, z - 1));
                        AddQuad(value, Value(x, y, z + 1), Cell(x - 1, y - 1, z), Cell(x, y - 1, z), Cell(x, y, z),
                            Cell(x - 1, y, z));
                    }
                }
            }
        }

        private float Value(int x, int y, int z) => Field[x + Size.x * (y + Size.y * z)];

        private int Cell(int x, int y, int z) => CellVertices[x + (Size.x - 1) * (y + (Size.y - 1) * z)];

        // The vertices go around the edge. The first triangle winding faces along the edge, from rock to air.
        private void AddQuad(float start, float end, int a, int b, int c, int d)
        {
            if (start < 0f == end < 0f) return;
            if (a < 0 || b < 0 || c < 0 || d < 0) return;
            if (start >= 0f) (b, d) = (d, b);

            // The diagonal runs along a crease when the two vertices on the crease are the sharper pair.
            float sharpnessAC = Sharpness[a] + Sharpness[c];
            float sharpnessBD = Sharpness[b] + Sharpness[d];
            bool splitAC = math.abs(sharpnessAC - sharpnessBD) > SharpnessTie
                ? sharpnessAC > sharpnessBD
                : math.distancesq(Positions[a], Positions[c]) <= math.distancesq(Positions[b], Positions[d]);
            if (splitAC)
            {
                AddTriangle(a, b, c);
                AddTriangle(a, c, d);
            }
            else
            {
                AddTriangle(a, b, d);
                AddTriangle(b, c, d);
            }
        }

        private void AddTriangle(int a, int b, int c)
        {
            Indices.Add(a);
            Indices.Add(b);
            Indices.Add(c);
        }
    }
}
