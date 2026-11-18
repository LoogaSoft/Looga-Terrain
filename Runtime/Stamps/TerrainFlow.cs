using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Water flow over a square height grid (Burst). Priority-Flood with a small rise (Barnes et al. 2014) fills pits
    /// and flats, so all ground drains to the grid edge. A multiple flow direction pass (Quinn et al. 1991) then moves
    /// the upstream area of each cell to its lower neighbours, highest cell first. Every filled cell of a pit or a flat
    /// takes the upstream area of the cell where the pit drains and a fixed high wetness, so a filled pit is one even
    /// wet area. The small slope of the fill would otherwise route the water in straight diagonal lines.
    /// </summary>
    internal static class TerrainFlow
    {
        // Rise of a filled cell over the cell that floods it, in metres. Flats get a small slope, so water crosses them.
        private const float FillRise = 0.001f;
        // Smallest slope tangent of the wetness index, so flat ground gets a finite value.
        private const float MinimumTangent = 0.01f;
        // Wetness index of filled pits and flats: ponding ground. A value from the outlet area would change at tile
        // seams, because each tile sees another part of a large catchment.
        private const float FilledWetness = 14f;

        /// <summary>
        /// Computes the upstream area and the wetness index of the inner cells of a grid. The inner cells are the grid
        /// without the apron on each side.
        /// </summary>
        /// <param name="heights">Heights in metres, row by row, size x size.</param>
        /// <param name="cell">Distance between grid cells, in metres.</param>
        /// <param name="output">
        /// Two half floats for each inner cell, row by row: log10 of the upstream area in square metres, and the
        /// wetness index ln(area per metre of contour / slope tangent).
        /// </param>
        internal static void Run(NativeArray<float> heights, int size, int apron, float cell, NativeArray<ushort> output)
        {
            int count = size * size;
            var filled = new NativeArray<float>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var order = new NativeArray<int>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var area = new NativeArray<float>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var queued = new NativeArray<byte>(count, Allocator.TempJob);
            var heap = new NativeArray<ulong>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var pits = new NativeArray<int>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var outlets = new NativeArray<int>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                new FlowJob
                {
                    Heights = heights,
                    Filled = filled,
                    Order = order,
                    Area = area,
                    Queued = queued,
                    Heap = heap,
                    Pits = pits,
                    Outlets = outlets,
                    Output = output,
                    Size = size,
                    Apron = apron,
                    Cell = cell
                }.Run();
            }
            finally
            {
                filled.Dispose();
                order.Dispose();
                area.Dispose();
                queued.Dispose();
                heap.Dispose();
                pits.Dispose();
                outlets.Dispose();
            }
        }

        [BurstCompile(CompileSynchronously = true, DisableSafetyChecks = true)]
        private struct FlowJob : IJob
        {
            [ReadOnly] public NativeArray<float> Heights;
            public NativeArray<float> Filled;
            public NativeArray<int> Order;
            public NativeArray<float> Area;
            public NativeArray<byte> Queued;
            public NativeArray<ulong> Heap;
            // Cells that the fill raised. They leave in arrival order, before the heap (Barnes et al. 2014, Alg. 3).
            public NativeArray<int> Pits;
            // For a filled cell, the unfilled cell where its pit drains. -1 for other cells.
            public NativeArray<int> Outlets;
            public NativeArray<ushort> Output;
            public int Size;
            public int Apron;
            public float Cell;

            private int _heapCount;

            public void Execute()
            {
                Fill();
                Accumulate();
                WriteOutput();
            }

            // Cells leave the queues lowest first, so Order holds the cells in rising filled height. A raised cell can
            // leave less than the fill rise out of order. The flow then skips that tiny drop.
            private void Fill()
            {
                _heapCount = 0;
                int last = Size - 1;
                for (int i = 0; i < Size; i++)
                {
                    Seed(i);
                    Seed(last * Size + i);
                    if (i == 0 || i == last) continue;
                    Seed(i * Size);
                    Seed(i * Size + last);
                }
                int popped = 0;
                int pitHead = 0;
                int pitTail = 0;
                while (_heapCount > 0 || pitHead < pitTail)
                {
                    int i = pitHead < pitTail ? Pits[pitHead++] : (int)(Pop() & 0xFFFFFFFFul);
                    Order[popped++] = i;
                    int y = i / Size;
                    int x = i - y * Size;
                    float height = Filled[i];
                    int y0 = math.max(y - 1, 0);
                    int y1 = math.min(y + 1, last);
                    int x0 = math.max(x - 1, 0);
                    int x1 = math.min(x + 1, last);
                    for (int ny = y0; ny <= y1; ny++)
                    {
                        int row = ny * Size;
                        for (int nx = x0; nx <= x1; nx++)
                        {
                            int n = row + nx;
                            if (Queued[n] != 0) continue;
                            Queued[n] = 1;
                            float own = Heights[n];
                            if (own <= height + FillRise)
                            {
                                Filled[n] = height + FillRise;
                                Outlets[n] = Outlets[i] >= 0 ? Outlets[i] : i;
                                Pits[pitTail++] = n;
                            }
                            else
                            {
                                Filled[n] = own;
                                Outlets[n] = -1;
                                Push(Key(own, n));
                            }
                        }
                    }
                }
            }

            private void Seed(int i)
            {
                if (Queued[i] != 0) return;
                Queued[i] = 1;
                Filled[i] = Heights[i];
                Outlets[i] = -1;
                Push(Key(Heights[i], i));
            }

            // Highest cell first, so each cell has all its upstream area before it passes the area on.
            private void Accumulate()
            {
                int count = Size * Size;
                int last = Size - 1;
                float cellArea = Cell * Cell;
                for (int i = 0; i < count; i++)
                {
                    Area[i] = cellArea;
                }
                // Contour length of the flow to each neighbour (Quinn et al. 1991) over the distance to it.
                float straight = 0.5f / Cell;
                float diagonal = 0.354f / (Cell * math.SQRT2);
                for (int k = count - 1; k >= 0; k--)
                {
                    int i = Order[k];
                    int y = i / Size;
                    int x = i - y * Size;
                    float height = Filled[i];
                    int y0 = math.max(y - 1, 0);
                    int y1 = math.min(y + 1, last);
                    int x0 = math.max(x - 1, 0);
                    int x1 = math.min(x + 1, last);
                    float total = 0f;
                    for (int ny = y0; ny <= y1; ny++)
                    {
                        for (int nx = x0; nx <= x1; nx++)
                        {
                            float drop = height - Filled[ny * Size + nx];
                            if (drop <= 0f) continue;
                            total += drop * (nx == x || ny == y ? straight : diagonal);
                        }
                    }
                    if (total <= 0f) continue;
                    float share = Area[i] / total;
                    for (int ny = y0; ny <= y1; ny++)
                    {
                        for (int nx = x0; nx <= x1; nx++)
                        {
                            int n = ny * Size + nx;
                            float drop = height - Filled[n];
                            if (drop <= 0f) continue;
                            Area[n] += share * drop * (nx == x || ny == y ? straight : diagonal);
                        }
                    }
                }
            }

            private void WriteOutput()
            {
                int inner = Size - 2 * Apron;
                for (int y = 0; y < inner; y++)
                {
                    for (int x = 0; x < inner; x++)
                    {
                        int i = (y + Apron) * Size + x + Apron;
                        int outlet = Outlets[i];
                        float area = Area[outlet >= 0 ? outlet : i];
                        float wetness = FilledWetness;
                        if (outlet < 0)
                        {
                            float dx = (Heights[i + 1] - Heights[i - 1]) / (2f * Cell);
                            float dz = (Heights[i + Size] - Heights[i - Size]) / (2f * Cell);
                            float tangent = math.max(math.length(new float2(dx, dz)), MinimumTangent);
                            wetness = math.log(area / Cell / tangent);
                        }
                        int o = (y * inner + x) * 2;
                        Output[o] = (ushort)math.f32tof16(math.log10(area));
                        Output[o + 1] = (ushort)math.f32tof16(wetness);
                    }
                }
            }

            // Sorts by height, then by cell index, so equal heights leave the queue in a fixed order.
            private static ulong Key(float height, int index)
            {
                uint bits = math.asuint(height);
                bits = (bits & 0x80000000u) != 0 ? ~bits : bits | 0x80000000u;
                return ((ulong)bits << 32) | (uint)index;
            }

            private void Push(ulong key)
            {
                int i = _heapCount++;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (Heap[parent] <= key) break;
                    Heap[i] = Heap[parent];
                    i = parent;
                }
                Heap[i] = key;
            }

            private ulong Pop()
            {
                ulong top = Heap[0];
                ulong moved = Heap[--_heapCount];
                int i = 0;
                while (true)
                {
                    int child = 2 * i + 1;
                    if (child >= _heapCount) break;
                    if (child + 1 < _heapCount && Heap[child + 1] < Heap[child]) child++;
                    if (Heap[child] >= moved) break;
                    Heap[i] = Heap[child];
                    i = child;
                }
                if (_heapCount > 0)
                {
                    Heap[i] = moved;
                }
                return top;
            }
        }
    }
}
