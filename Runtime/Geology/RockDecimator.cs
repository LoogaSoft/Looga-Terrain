using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

// The road slab levels of detail (LoogaRoadSlabLods) reduce the slab chunks with this decimator.
[assembly: InternalsVisibleTo("LoogaSoft.Terrain.Roads.Editor")]

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Reduces a closed triangle mesh by edge collapses in the order of the smallest quadric error (Garland and
    /// Heckbert 1997). This is the method of <see cref="FracturedFaceDecimator"/> in a Burst job with native containers.
    /// </summary>
    /// <remarks>
    /// Each collapse moves one end of an edge onto the other end, so the kept vertices keep their attributes. The
    /// managed decimator needed 125 s for a rock of 530000 triangles, and most of this time was in its candidate heap.
    /// </remarks>
    internal static class RockDecimator
    {
        /// <summary>
        /// Collapses edges while the error of the cheapest collapse is below the limit (squared meters). The caller
        /// owns the returned triangles, which are indices into the input positions. The details are one value for each
        /// vertex, scaled to meters: a collapse between two different values also costs their squared difference, so
        /// flat faces keep the vertices that carry a mask. Above the triangle budget, collapses continue past the limit:
        /// quadric errors add up on rough rock, so an error limit alone leaves too many triangles.
        /// </summary>
        public static NativeList<int> Reduce(NativeArray<float3> positions, NativeArray<float> details,
            NativeArray<int> source, double errorLimit, int triangleBudget = int.MaxValue)
        {
            int vertexCount = positions.Length;
            var job = new RockDecimateJob
            {
                Positions = positions,
                Details = details,
                Source = source,
                ErrorLimit = errorLimit,
                TriangleBudget = triangleBudget,
                Triangles = new NativeArray<int>(source.Length, Allocator.TempJob),
                DeadTriangles = new NativeArray<byte>(source.Length / 3, Allocator.TempJob),
                FirstCorners = new NativeArray<int>(vertexCount, Allocator.TempJob),
                NextCorners = new NativeArray<int>(source.Length, Allocator.TempJob),
                Valences = new NativeArray<int>(vertexCount, Allocator.TempJob),
                Versions = new NativeArray<int>(vertexCount, Allocator.TempJob),
                DeadVertices = new NativeArray<byte>(vertexCount, Allocator.TempJob),
                Quadrics = new NativeArray<RockQuadric>(vertexCount, Allocator.TempJob),
                NeighbourMarks = new NativeArray<int>(vertexCount, Allocator.TempJob),
                CountedMarks = new NativeArray<int>(vertexCount, Allocator.TempJob),
                Heap = new NativeList<RockCollapse>(source.Length, Allocator.TempJob),
                Result = new NativeList<int>(source.Length, Allocator.Persistent)
            };
            job.Run();
            job.Triangles.Dispose();
            job.DeadTriangles.Dispose();
            job.FirstCorners.Dispose();
            job.NextCorners.Dispose();
            job.Valences.Dispose();
            job.Versions.Dispose();
            job.DeadVertices.Dispose();
            job.Quadrics.Dispose();
            job.NeighbourMarks.Dispose();
            job.CountedMarks.Dispose();
            job.Heap.Dispose();
            return job.Result;
        }
    }

    /// <summary>A collapse of one vertex onto another. The versions show that both vertices are unchanged.</summary>
    internal struct RockCollapse
    {
        public double Cost;
        public int From;
        public int To;
        public int FromVersion;
        public int ToVersion;
    }

    /// <summary>Sum of squared plane distances, as the upper terms of a symmetric 4 x 4 matrix.</summary>
    internal struct RockQuadric
    {
        public double XX, XY, XZ, XW, YY, YZ, YW, ZZ, ZW, WW;

        public static RockQuadric FromPlane(double3 normal, double distance)
        {
            return new RockQuadric
            {
                XX = normal.x * normal.x, XY = normal.x * normal.y, XZ = normal.x * normal.z, XW = normal.x * distance,
                YY = normal.y * normal.y, YZ = normal.y * normal.z, YW = normal.y * distance,
                ZZ = normal.z * normal.z, ZW = normal.z * distance, WW = distance * distance
            };
        }

        public void Add(in RockQuadric other)
        {
            XX += other.XX;
            XY += other.XY;
            XZ += other.XZ;
            XW += other.XW;
            YY += other.YY;
            YZ += other.YZ;
            YW += other.YW;
            ZZ += other.ZZ;
            ZW += other.ZW;
            WW += other.WW;
        }

        public readonly double Error(double3 point)
        {
            return XX * point.x * point.x + 2.0 * XY * point.x * point.y + 2.0 * XZ * point.x * point.z +
                   2.0 * XW * point.x + YY * point.y * point.y + 2.0 * YZ * point.y * point.z + 2.0 * YW * point.y +
                   ZZ * point.z * point.z + 2.0 * ZW * point.z + WW;
        }
    }

    /// <summary>Runs the edge collapses. Each vertex has a linked list of its triangle corners.</summary>
    [BurstCompile]
    internal struct RockDecimateJob : IJob
    {
        // A collapse is not made when it turns a triangle normal by more than about 70 degrees.
        private const float FlipLimit = 0.35f;
        // Largest number of triangles around a vertex after a collapse. Without a limit, flat areas collapse into hubs.
        private const int ValenceLimit = 24;

        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float> Details;
        [ReadOnly] public NativeArray<int> Source;
        public NativeArray<int> Triangles;
        public NativeArray<byte> DeadTriangles;
        public NativeArray<int> FirstCorners;
        // A corner is an index into Triangles. The next corner of the same vertex, or -1.
        public NativeArray<int> NextCorners;
        public NativeArray<int> Valences;
        public NativeArray<int> Versions;
        public NativeArray<byte> DeadVertices;
        public NativeArray<RockQuadric> Quadrics;
        // A vertex carries the current stamp when a search has seen it. This needs no clear between searches.
        public NativeArray<int> NeighbourMarks;
        public NativeArray<int> CountedMarks;
        public NativeList<RockCollapse> Heap;
        public NativeList<int> Result;
        public double ErrorLimit;
        public int TriangleBudget;

        private int _stamp;
        private int _liveTriangles;

        public void Execute()
        {
            Initialize();
            _liveTriangles = Source.Length / 3;
            while (Heap.Length > 0)
            {
                RockCollapse collapse = Pop();
                if (collapse.Cost > ErrorLimit && _liveTriangles <= TriangleBudget) break;
                int from = collapse.From;
                int to = collapse.To;
                if (DeadVertices[from] != 0 || DeadVertices[to] != 0 || Versions[from] != collapse.FromVersion ||
                    Versions[to] != collapse.ToVersion)
                {
                    continue;
                }
                if (Valences[from] + Valences[to] - 4 > ValenceLimit || !CanCollapse(from, to)) continue;
                Collapse(from, to);
                PushNeighbours(to);
            }

            Result.Clear();
            for (int triangle = 0; triangle < DeadTriangles.Length; triangle++)
            {
                if (DeadTriangles[triangle] != 0) continue;
                Result.Add(Triangles[triangle * 3]);
                Result.Add(Triangles[triangle * 3 + 1]);
                Result.Add(Triangles[triangle * 3 + 2]);
            }
        }

        private void Initialize()
        {
            for (int vertex = 0; vertex < FirstCorners.Length; vertex++)
            {
                FirstCorners[vertex] = -1;
            }
            Triangles.CopyFrom(Source);
            for (int corner = 0; corner < Triangles.Length; corner++)
            {
                int vertex = Triangles[corner];
                NextCorners[corner] = FirstCorners[vertex];
                FirstCorners[vertex] = corner;
                Valences[vertex]++;
            }
            for (int start = 0; start < Triangles.Length; start += 3)
            {
                float3 a = Positions[Triangles[start]];
                float3 b = Positions[Triangles[start + 1]];
                float3 c = Positions[Triangles[start + 2]];
                var normal = (double3)math.cross(b - a, c - a);
                double length = math.length(normal);
                if (length < 1e-7) continue;
                normal /= length;
                RockQuadric plane = RockQuadric.FromPlane(normal, -math.dot(normal, a));
                for (int corner = 0; corner < 3; corner++)
                {
                    int vertex = Triangles[start + corner];
                    RockQuadric sum = Quadrics[vertex];
                    sum.Add(plane);
                    Quadrics[vertex] = sum;
                }
            }
            // On a closed mesh, each edge appears once in each direction. One push for each corner covers both.
            for (int start = 0; start < Triangles.Length; start += 3)
            {
                Push(Triangles[start], Triangles[start + 1]);
                Push(Triangles[start + 1], Triangles[start + 2]);
                Push(Triangles[start + 2], Triangles[start]);
            }
        }

        // The collapse keeps the mesh a manifold (the two ends share only the vertices of their common triangles) and
        // turns no triangle over.
        private bool CanCollapse(int from, int to)
        {
            int stamp = ++_stamp;
            for (int corner = FirstCorners[to]; corner >= 0; corner = NextCorners[corner])
            {
                int start = corner / 3 * 3;
                if (DeadTriangles[corner / 3] != 0) continue;
                NeighbourMarks[Triangles[start]] = stamp;
                NeighbourMarks[Triangles[start + 1]] = stamp;
                NeighbourMarks[Triangles[start + 2]] = stamp;
            }

            int shared = 0;
            int common = 0;
            float3 target = Positions[to];
            for (int corner = FirstCorners[from]; corner >= 0; corner = NextCorners[corner])
            {
                if (DeadTriangles[corner / 3] != 0) continue;
                int start = corner / 3 * 3;
                int vertexA = Triangles[start];
                int vertexB = Triangles[start + 1];
                int vertexC = Triangles[start + 2];
                bool hasTo = vertexA == to || vertexB == to || vertexC == to;
                if (hasTo) shared++;
                common += CountCommon(vertexA, from, to, stamp) + CountCommon(vertexB, from, to, stamp) +
                          CountCommon(vertexC, from, to, stamp);
                if (hasTo) continue;

                // The triangle after the move must face the same way.
                float3 a = Positions[vertexA];
                float3 b = Positions[vertexB];
                float3 c = Positions[vertexC];
                float3 before = math.cross(b - a, c - a);
                if (vertexA == from) a = target;
                if (vertexB == from) b = target;
                if (vertexC == from) c = target;
                float3 after = math.cross(b - a, c - a);
                if (math.lengthsq(after) < 1e-12f) return false;
                if (math.dot(math.normalizesafe(before), math.normalizesafe(after)) < FlipLimit) return false;
            }
            return shared > 0 && common == shared;
        }

        // Counts each vertex around the removed vertex once, when it also lies around the kept vertex.
        private int CountCommon(int vertex, int from, int to, int stamp)
        {
            if (vertex == from || vertex == to || CountedMarks[vertex] == stamp) return 0;
            CountedMarks[vertex] = stamp;
            return NeighbourMarks[vertex] == stamp ? 1 : 0;
        }

        // Moves the corners of the removed vertex to the kept vertex. Triangles with both vertices die.
        private void Collapse(int from, int to)
        {
            int moved = -1;
            int corner = FirstCorners[from];
            while (corner >= 0)
            {
                int next = NextCorners[corner];
                int triangle = corner / 3;
                if (DeadTriangles[triangle] == 0)
                {
                    int start = triangle * 3;
                    int vertexA = Triangles[start];
                    int vertexB = Triangles[start + 1];
                    int vertexC = Triangles[start + 2];
                    if (vertexA == to || vertexB == to || vertexC == to)
                    {
                        DeadTriangles[triangle] = 1;
                        _liveTriangles--;
                        int third = vertexA != from && vertexA != to ? vertexA
                            : vertexB != from && vertexB != to ? vertexB : vertexC;
                        Valences[third]--;
                    }
                    else
                    {
                        Triangles[corner] = to;
                        NextCorners[corner] = moved;
                        moved = corner;
                    }
                }
                corner = next;
            }

            // The list of the kept vertex keeps only live triangles, so its length stays near the valence.
            int kept = moved;
            int valence = 0;
            for (int link = moved; link >= 0; link = NextCorners[link])
            {
                valence++;
            }
            corner = FirstCorners[to];
            while (corner >= 0)
            {
                int next = NextCorners[corner];
                if (DeadTriangles[corner / 3] == 0)
                {
                    NextCorners[corner] = kept;
                    kept = corner;
                    valence++;
                }
                corner = next;
            }
            FirstCorners[to] = kept;
            FirstCorners[from] = -1;
            Valences[to] = valence;
            Valences[from] = 0;

            DeadVertices[from] = 1;
            RockQuadric sum = Quadrics[to];
            sum.Add(Quadrics[from]);
            Quadrics[to] = sum;
            Versions[to]++;
        }

        private void PushNeighbours(int vertex)
        {
            int stamp = ++_stamp;
            CountedMarks[vertex] = stamp;
            for (int corner = FirstCorners[vertex]; corner >= 0; corner = NextCorners[corner])
            {
                int start = corner / 3 * 3;
                for (int offset = 0; offset < 3; offset++)
                {
                    int other = Triangles[start + offset];
                    if (CountedMarks[other] == stamp) continue;
                    CountedMarks[other] = stamp;
                    Push(vertex, other);
                    Push(other, vertex);
                }
            }
        }

        // A candidate above the limit never runs, so it stays out of the heap.
        private void Push(int from, int to)
        {
            RockQuadric sum = Quadrics[from];
            sum.Add(Quadrics[to]);
            double detail = Details[from] - Details[to];
            double cost = math.max(sum.Error(Positions[to]), 0.0) + detail * detail;
            if (cost > ErrorLimit && TriangleBudget == int.MaxValue) return;

            var item = new RockCollapse
            {
                Cost = cost, From = from, To = to, FromVersion = Versions[from], ToVersion = Versions[to]
            };
            Heap.Add(item);
            int child = Heap.Length - 1;
            while (child > 0)
            {
                int parent = (child - 1) >> 1;
                if (Heap[parent].Cost <= cost) break;
                Heap[child] = Heap[parent];
                child = parent;
            }
            Heap[child] = item;
        }

        private RockCollapse Pop()
        {
            RockCollapse top = Heap[0];
            int last = Heap.Length - 1;
            RockCollapse item = Heap[last];
            Heap.RemoveAt(last);
            int count = last;
            if (count == 0) return top;
            int parent = 0;
            while (true)
            {
                int child = parent * 2 + 1;
                if (child >= count) break;
                if (child + 1 < count && Heap[child + 1].Cost < Heap[child].Cost) child++;
                if (item.Cost <= Heap[child].Cost) break;
                Heap[parent] = Heap[child];
                parent = child;
            }
            Heap[parent] = item;
            return top;
        }
    }
}
