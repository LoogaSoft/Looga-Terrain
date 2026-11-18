using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace LoogaSoft.Terrain.Geology
{
    // Daylight in a cave. A coarse grid holds the open air of the cave system: 0 = rock, 1 = air in the cave, 2 = air
    // above the terrain (sky). The distance from the sky through the cave air then gives the daylight at each mesh
    // vertex. The rock material darkens its ambient light by this value, because the terrain above the cave does not
    // block the sky light of the global illumination.

    /// <summary>Marks the grid cells above the terrain as sky and all other cells as rock.</summary>
    [BurstCompile]
    internal struct CaveSkyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> Heights;
        [WriteOnly] public NativeArray<byte> Air;
        public float3 Origin;
        public float Cell;
        public int3 Size;
        // Mesh cells in one grid cell, and the width of the mesh height grid (one apron column before the grid).
        public int CellsPerCell;
        public int HeightWidth;

        public void Execute(int index)
        {
            int x = index % Size.x;
            int y = index / Size.x % Size.y;
            int z = index / (Size.x * Size.y);
            float height = Heights[(z * CellsPerCell + 1) * HeightWidth + x * CellsPerCell + 1];
            Air[index] = Origin.y + y * Cell > height ? (byte)2 : (byte)0;
        }
    }

    /// <summary>
    /// Copies the cave air of one mesh chunk into the grid. The chunks write separate cells, so the jobs of all chunks
    /// can share the grid. Under a terrain hole the terrain height is not the open surface: the cave air there a short
    /// distance below it is open to the sky, for example the floor of a spring mouth in a bank.
    /// </summary>
    [BurstCompile]
    internal struct CaveAirJob : IJob
    {
        [ReadOnly] public NativeArray<float> Cave;
        [ReadOnly] public NativeArray<float> Ground;
        // Hole flags of the mesh height grid (2 over a terrain hole), with the width of the grid.
        [ReadOnly] public NativeArray<byte> Holes;
        public int HeightWidth;
        // Cave air under a hole is sky down to this depth below the terrain height, in meters.
        public float OpenDepth;
        [NativeDisableContainerSafetyRestriction] public NativeArray<byte> Air;
        public int3 Size;
        // First mesh sample of the chunk (without the apron), samples along one chunk axis with the apron, mesh cells
        // in one chunk and in one grid cell.
        public int3 FirstSample;
        public int Samples;
        public int ChunkCells;
        public int CellsPerCell;

        public void Execute()
        {
            int3 start = FirstSample / CellsPerCell;
            int3 end = math.min((FirstSample + ChunkCells) / CellsPerCell, Size);
            for (int z = start.z; z < end.z; z++)
            {
                for (int y = start.y; y < end.y; y++)
                {
                    for (int x = start.x; x < end.x; x++)
                    {
                        // Sample index in the chunk arrays: one apron sample before the chunk.
                        int3 sample = new int3(x, y, z) * CellsPerCell - FirstSample + 1;
                        int index = (sample.z * Samples + sample.y) * Samples + sample.x;
                        int cell = (z * Size.y + y) * Size.x + x;
                        if (Ground[index] < 0f)
                        {
                            Air[cell] = 2;
                        }
                        else if (Cave[index] < 0f)
                        {
                            bool hole = Holes[(z * CellsPerCell + 1) * HeightWidth + x * CellsPerCell + 1] == 2;
                            Air[cell] = hole && Ground[index] < OpenDepth ? (byte)2 : (byte)1;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Distance from the sky through the cave air, in meters, for each grid cell (Dijkstra over the 26 neighbours).
    /// Rock cells and cells past the maximum distance keep float.MaxValue.
    /// </summary>
    [BurstCompile]
    internal struct CaveLightJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Air;
        public NativeArray<float> Distance;
        public int3 Size;
        public float Cell;
        public float MaximumDistance;

        public void Execute()
        {
            var heap = new NativeList<int>(4096, Allocator.Temp);
            var keys = new NativeList<float>(4096, Allocator.Temp);
            for (int index = 0; index < Distance.Length; index++)
            {
                Distance[index] = Air[index] == 2 ? 0f : float.MaxValue;
            }
            // The search starts at the cave cells next to the sky.
            for (int index = 0; index < Air.Length; index++)
            {
                if (Air[index] != 1) continue;
                float nearest = SkyStep(index);
                if (nearest == float.MaxValue) continue;
                Distance[index] = nearest;
                Push(ref heap, ref keys, index, nearest);
            }
            while (heap.Length > 0)
            {
                Pop(ref heap, ref keys, out int current, out float distance);
                if (distance > Distance[current] || distance > MaximumDistance) continue;
                int x = current % Size.x, y = current / Size.x % Size.y, z = current / (Size.x * Size.y);
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy, nz = z + dz;
                            if (nx < 0 || ny < 0 || nz < 0 || nx >= Size.x || ny >= Size.y || nz >= Size.z) continue;
                            int next = (nz * Size.y + ny) * Size.x + nx;
                            if (Air[next] != 1) continue;
                            float candidate = distance + Cell * math.sqrt(dx * dx + dy * dy + dz * dz);
                            if (candidate >= Distance[next]) continue;
                            Distance[next] = candidate;
                            Push(ref heap, ref keys, next, candidate);
                        }
                    }
                }
            }
            heap.Dispose();
            keys.Dispose();
        }

        // Shortest step from a cell to a sky cell next to it, or float.MaxValue.
        private float SkyStep(int index)
        {
            int x = index % Size.x, y = index / Size.x % Size.y, z = index / (Size.x * Size.y);
            float nearest = float.MaxValue;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy, nz = z + dz;
                        if (nx < 0 || ny < 0 || nz < 0 || nx >= Size.x || ny >= Size.y || nz >= Size.z) continue;
                        if (Air[(nz * Size.y + ny) * Size.x + nx] != 2) continue;
                        nearest = math.min(nearest, Cell * math.sqrt(dx * dx + dy * dy + dz * dz));
                    }
                }
            }
            return nearest;
        }

        // Binary min heap of cell indices by distance.
        private static void Push(ref NativeList<int> heap, ref NativeList<float> keys, int cell, float key)
        {
            heap.Add(cell);
            keys.Add(key);
            int child = heap.Length - 1;
            while (child > 0)
            {
                int parent = (child - 1) / 2;
                if (keys[parent] <= keys[child]) break;
                Swap(ref heap, ref keys, parent, child);
                child = parent;
            }
        }

        private static void Pop(ref NativeList<int> heap, ref NativeList<float> keys, out int cell, out float key)
        {
            cell = heap[0];
            key = keys[0];
            int last = heap.Length - 1;
            heap[0] = heap[last];
            keys[0] = keys[last];
            heap.RemoveAt(last);
            keys.RemoveAt(last);
            int parent = 0;
            while (true)
            {
                int left = parent * 2 + 1, right = left + 1, smallest = parent;
                if (left < heap.Length && keys[left] < keys[smallest]) smallest = left;
                if (right < heap.Length && keys[right] < keys[smallest]) smallest = right;
                if (smallest == parent) break;
                Swap(ref heap, ref keys, parent, smallest);
                parent = smallest;
            }
        }

        private static void Swap(ref NativeList<int> heap, ref NativeList<float> keys, int a, int b)
        {
            (heap[a], heap[b]) = (heap[b], heap[a]);
            (keys[a], keys[b]) = (keys[b], keys[a]);
        }
    }

    /// <summary>
    /// Cave shade of each mesh vertex: 0 in daylight, up to the darkness value deep in the cave. The vertex reads the
    /// sky distance of the air a short distance in front of its surface. The lip values of the vertex go to
    /// <see cref="CaveLipJob"/>.
    /// </summary>
    [BurstCompile]
    internal struct CaveShadeJob : IJobParallelFor
    {
        // Lip distance of a vertex without daylight.
        public const float NoLip = 1e9f;
        // A floor near an opening shows the ground in front of the opening, past the foot of the cut face. Its offset
        // goes this far past the opening, in meters. A vertex whose normal is within about 60 degrees of up is floor.
        private const float FloorGroundReach = 1.5f;
        private const float FloorFacing = 0.5f;

        [ReadOnly] public NativeArray<byte> Air;
        [ReadOnly] public NativeArray<float> Distance;
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float3> Normals;
        [WriteOnly] public NativeArray<float2> Shade;
        // x: sky distance in meters (NoLip without daylight). yz: world XZ offset from the vertex toward the opening,
        // where the daylight comes in.
        [WriteOnly] public NativeArray<float4> Lip;
        public float3 Origin;
        public float Cell;
        public int3 Size;
        // Daylight falls to 1/e over this distance from the sky, in meters.
        public float Reach;
        public float Darkness;

        public void Execute(int index)
        {
            float3 normal = Normals[index];
            float3 point = Positions[index] + normal * Cell * 0.5f;
            float distance = AirDistance(point);
            if (distance == float.MaxValue)
            {
                point = Positions[index] + normal * Cell * 1.25f;
                distance = AirDistance(point);
            }
            if (distance == float.MaxValue) distance = NearestDistance(Positions[index] + normal * Cell * 0.5f);
            float light = distance == float.MaxValue ? 0f : math.exp(-distance / Reach);
            Shade[index] = new float2(Darkness * (1f - light), 0f);
            Lip[index] = distance == float.MaxValue
                ? new float4(NoLip, 0f, 0f, 0f)
                : new float4(distance, OpeningOffset(point, distance, normal.y > FloorFacing), 0f);
        }

        // The sky distance falls toward the opening, so the opening lies against the gradient. The offset stops at
        // the sample distance in front of the surface, because the daylight path starts there. A floor offset goes
        // past the opening, because the cut face starts there and its layers are steep rock.
        private float2 OpeningOffset(float3 point, float distance, bool floor)
        {
            float step = Cell * 0.5f;
            float3 gradient = float3.zero;
            for (int axis = 0; axis < 3; axis++)
            {
                float3 offset = float3.zero;
                offset[axis] = step;
                float high = AirDistance(point + offset);
                float low = AirDistance(point - offset);
                if (high == float.MaxValue || low == float.MaxValue) return float2.zero;
                gradient[axis] = (high - low) / (2f * step);
            }
            float length = math.length(gradient);
            if (length < 1e-4f) return float2.zero;
            float reach = floor ? distance + FloorGroundReach : math.max(distance - step, 0f);
            return -gradient.xz / length * reach;
        }

        // Distance blended over the air corners of the grid cell around a point, or float.MaxValue with no air corner.
        private float AirDistance(float3 point)
        {
            float3 grid = (point - Origin) / Cell;
            int3 low = (int3)math.floor(grid);
            float3 t = grid - low;
            float sum = 0f, weightSum = 0f;
            for (int corner = 0; corner < 8; corner++)
            {
                int3 offset = new int3(corner & 1, (corner >> 1) & 1, corner >> 2);
                int3 cell = low + offset;
                if (math.any(cell < 0) || math.any(cell >= Size)) continue;
                int cellIndex = (cell.z * Size.y + cell.y) * Size.x + cell.x;
                if (Air[cellIndex] == 0) continue;
                float value = Distance[cellIndex];
                if (value == float.MaxValue) continue;
                float3 w = math.select(1f - t, t, offset == 1);
                float weight = w.x * w.y * w.z;
                sum += value * weight;
                weightSum += weight;
            }
            return weightSum > 1e-4f ? sum / weightSum : float.MaxValue;
        }

        // Smallest distance of the air cells within one cell of a point.
        private float NearestDistance(float3 point)
        {
            int3 centre = (int3)math.round((point - Origin) / Cell);
            float nearest = float.MaxValue;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int3 cell = centre + new int3(dx, dy, dz);
                        if (math.any(cell < 0) || math.any(cell >= Size)) continue;
                        int cellIndex = (cell.z * Size.y + cell.y) * Size.x + cell.x;
                        if (Air[cellIndex] != 0) nearest = math.min(nearest, Distance[cellIndex]);
                    }
                }
            }
            return nearest;
        }
    }

    /// <summary>
    /// Adds the cave quads near the daylight of one chunk to the terrain surface quads, so the terrain surface goes on
    /// a short distance into each opening and ends along a ragged edge on the rock. The cut edge between the terrain
    /// and the cave then does not show as a change of material. Each lip vertex shows the terrain where the daylight
    /// comes in, for example the face around a portal, and casts no shadow, because the rock below it casts. The cave
    /// floor under a hole that already draws the terrain surface (CaveMeshJob) takes the same offset, so it shows the
    /// ground in front of the opening and not the steep layers of the cut face above it.
    /// </summary>
    [BurstCompile]
    internal struct CaveLipJob : IJob
    {
        [ReadOnly] public NativeList<int> CaveIndices;
        [ReadOnly] public NativeArray<float4> Lip;
        public NativeList<int> TerrainIndices;
        public NativeList<float4> Covers;
        // Sky distances where the cover starts to fall and where it ends, in meters.
        public float FullDepth;
        public float EndDepth;

        public void Execute()
        {
            var terrainVertex = new NativeArray<bool>(Covers.Length, Allocator.Temp);
            var caveVertex = new NativeArray<bool>(Covers.Length, Allocator.Temp);
            for (int index = 0; index < TerrainIndices.Length; index++)
            {
                terrainVertex[TerrainIndices[index]] = true;
            }
            for (int index = 0; index < CaveIndices.Length; index++)
            {
                caveVertex[CaveIndices[index]] = true;
            }
            for (int vertex = 0; vertex < Covers.Length; vertex++)
            {
                if (!terrainVertex[vertex] || !caveVertex[vertex] || Lip[vertex].x >= CaveShadeJob.NoLip) continue;
                float4 cover = Covers[vertex];
                Covers[vertex] = new float4(Lip[vertex].y, Lip[vertex].z, cover.z, cover.w);
            }
            int first = TerrainIndices.Length;
            for (int index = 0; index < CaveIndices.Length; index += 3)
            {
                int a = CaveIndices[index], b = CaveIndices[index + 1], c = CaveIndices[index + 2];
                if (math.min(Lip[a].x, math.min(Lip[b].x, Lip[c].x)) >= EndDepth) continue;
                TerrainIndices.Add(a);
                TerrainIndices.Add(b);
                TerrainIndices.Add(c);
            }
            for (int index = first; index < TerrainIndices.Length; index++)
            {
                int vertex = TerrainIndices[index];
                if (terrainVertex[vertex]) continue;
                float4 lip = Lip[vertex];
                float cover = 1f - math.smoothstep(FullDepth, EndDepth, lip.x);
                Covers[vertex] = new float4(lip.y, lip.z, cover, 0f);
            }
            terrainVertex.Dispose();
            caveVertex.Dispose();
        }
    }
}
