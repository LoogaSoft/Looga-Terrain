using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>Cross-section shape of a cave segment.</summary>
    internal enum CaveShape
    {
        // Rounded rectangle on a flat floor, as in a mined gallery.
        Cut = 0,
        // Ellipse cut by a flat floor, as in a natural passage or chamber.
        Natural = 1,
        // Vertical round shaft from Start (bottom centre) up to End (top centre). The sizes hold the diameters.
        Shaft = 2,
        // Rooms on a flat floor at Start (floor centre). StartSize holds the plan size along the room X axis
        // (StartPlane) and the room Z axis, EndSize.x the height. A mined room is a box with rounded edges, a natural
        // room a dome.
        MinedRoom = 3,
        NaturalRoom = 4
    }

    /// <summary>
    /// One straight piece of a cave passage: the floor centre line from Start to End and the cross-section at each
    /// end. The ends are cut by planes in XZ, so two pieces of one passage meet without a gap or an overlap at a bend.
    /// A shaft piece is vertical instead, and a room piece is one room (see <see cref="CaveShape"/>).
    /// </summary>
    internal struct CaveSegment
    {
        public float3 Start;
        public float3 End;
        // Width and height of the cross-section at Start and at End, in meters.
        public float2 StartSize;
        public float2 EndSize;
        public float CornerRadius;
        public CaveShape Shape;
        // Height of the rock bumps on the walls and the roof, in meters. The bumps fade out over the lowest meters of
        // the walls, so the floor stays walkable. The cave system sets their size.
        public float NoiseAmplitude;
        // Unit XZ normals of the end planes. A point is in the segment between the planes:
        // dot(p - Start, StartPlane) >= 0 and dot(p - End, EndPlane) <= 0.
        public float2 StartPlane;
        public float2 EndPlane;
        // Distance by which the segment reaches past its start and end planes. Inner joints overlap, so a point on
        // the joint plane is inside both pieces, not on the edge of both.
        public float StartOverlap;
        public float EndOverlap;
        // World box of the segment volume with its noise, for culling.
        public float3 BoundsMin;
        public float3 BoundsMax;
    }

    /// <summary>
    /// The cave field: a signed distance to the empty space of the cave, negative inside the cave. Burst jobs and
    /// managed code use the same functions.
    /// </summary>
    internal static class CaveField
    {
        // Height above the floor over which the wall bumps grow to their full height, in meters.
        private const float FloorBand = 2f;
        // Rise of the floor and drop of the roof per meter past an inner joint of a passage. It is larger than the
        // change of slope between two segments.
        private const float JointPinch = 1f;

        /// <summary>
        /// Signed distance to one segment without its noise. <paramref name="floorHeight"/> is the height of the point
        /// over the segment floor.
        /// </summary>
        public static float Segment(in CaveSegment segment, float3 point, out float floorHeight)
        {
            if (segment.Shape == CaveShape.Shaft) return Shaft(segment, point, out floorHeight);
            if (segment.Shape >= CaveShape.MinedRoom) return Room(segment, point, out floorHeight);
            float2 start = segment.Start.xz;
            float2 delta = segment.End.xz - start;
            float length = math.length(delta);
            float2 along = length > 1e-5f ? delta / length : new float2(0f, 1f);
            float t = math.dot(point.xz - start, along);
            float share = length > 1e-5f ? math.saturate(t / length) : 0f;
            float3 centre = math.lerp(segment.Start, segment.End, share);
            // Past an end the floor keeps the slope of the segment. A flat floor there was lower than the floor of the
            // next segment up a slope, so each joint made a step.
            if (length > 1e-5f) centre.y = segment.Start.y + (segment.End.y - segment.Start.y) * (t / length);
            float2 side = new float2(-along.y, along.x);
            float u = math.dot(point.xz - centre.xz, side);
            float v = point.y - centre.y;
            floorHeight = v;
            float2 half = math.lerp(segment.StartSize, segment.EndSize, share) * 0.5f;
            float pastStart = -math.dot(point.xz - start, segment.StartPlane);
            float pastEnd = math.dot(point.xz - segment.End.xz, segment.EndPlane);
            // In the overlap past an inner joint the floor rises and the roof drops, so the next segment gives the
            // floor and the roof there. Otherwise a change of slope at the joint made a step.
            float pinch = JointPinch * math.max(segment.StartOverlap > 0f ? pastStart : 0f,
                segment.EndOverlap > 0f ? pastEnd : 0f);
            if (pinch > 0f)
            {
                v -= pinch;
                half.y = math.max(half.y - pinch, 0.01f);
            }
            float profile = segment.Shape == CaveShape.Natural
                ? NaturalProfile(u, v, half)
                : CutProfile(u, v, half, segment.CornerRadius);
            float axial = math.max(pastStart - segment.StartOverlap, pastEnd - segment.EndOverlap);
            return math.max(profile, axial);
        }

        private static float CutProfile(float u, float v, float2 half, float cornerRadius)
        {
            float radius = math.min(cornerRadius, math.min(half.x, half.y));
            float2 q = math.abs(new float2(u, v - half.y)) - half + radius;
            return math.length(math.max(q, 0f)) + math.min(math.max(q.x, q.y), 0f) - radius;
        }

        // An ellipse whose centre is at a third of the height and whose bottom reaches below the floor, cut by the
        // flat floor. The floor is about as wide as the passage, and the walls bulge out a little above it.
        private static float NaturalProfile(float u, float v, float2 half)
        {
            var centre = new float2(0f, 0.7f * half.y);
            var radii = new float2(1.1f * half.x, 1.3f * half.y);
            float ellipse = (math.length((new float2(u, v) - centre) / radii) - 1f) * math.cmin(radii);
            return math.max(ellipse, -v);
        }

        // A vertical round shaft. The radius blends from the bottom to the top.
        private static float Shaft(in CaveSegment segment, float3 point, out float floorHeight)
        {
            float height = math.max(segment.End.y - segment.Start.y, 0.01f);
            float share = math.saturate((point.y - segment.Start.y) / height);
            float radius = math.lerp(segment.StartSize.x, segment.EndSize.x, share) * 0.5f;
            float radial = math.length(point.xz - segment.Start.xz) - radius;
            float axial = math.max(segment.Start.y - point.y, point.y - segment.End.y);
            // A shaft has no floor, so its walls carry the full noise.
            floorHeight = FloorBand;
            return math.max(radial, axial);
        }

        // A room in its own axes: X along StartPlane, Z to its left, Y up from the floor.
        private static float Room(in CaveSegment segment, float3 point, out float floorHeight)
        {
            float2 local = RoomPlan(segment, point.xz);
            float v = point.y - segment.Start.y;
            floorHeight = v;
            float2 half = segment.StartSize * 0.5f;
            float height = segment.EndSize.x;
            if (segment.Shape == CaveShape.MinedRoom)
            {
                float3 halfBox = new float3(half.x, height * 0.5f, half.y);
                float radius = math.min(segment.CornerRadius, math.cmin(halfBox));
                float3 q = math.abs(new float3(local.x, v - halfBox.y, local.y)) - halfBox + radius;
                return math.length(math.max(q, 0f)) + math.min(math.cmax(q), 0f) - radius;
            }
            // A dome: an ellipsoid whose centre is at a third of the height, cut by the flat floor. The floor is
            // a little smaller than the plan size, and the walls bulge out a little above it.
            var radii = new float3(1.1f * half.x, 0.65f * height, 1.1f * half.y);
            var offset = new float3(local.x, v - 0.35f * height, local.y);
            float ellipsoid = (math.length(offset / radii) - 1f) * math.cmin(radii);
            return math.max(ellipsoid, -v);
        }

        private static float2 RoomPlan(in CaveSegment segment, float2 point)
        {
            float2 delta = point - segment.Start.xz;
            float2 axis = segment.StartPlane;
            return new float2(math.dot(delta, axis), math.dot(delta, new float2(-axis.y, axis.x)));
        }

        /// <summary>
        /// Signed distance in XZ to the plan outline of the segments: the passage widths between the passage ends.
        /// Negative inside the outline, at any height.
        /// </summary>
        public static float Plan(CaveSegment[] segments, float2 point)
        {
            float distance = float.MaxValue;
            foreach (CaveSegment segment in segments)
            {
                if (segment.Shape == CaveShape.Shaft)
                {
                    float radius = math.max(segment.StartSize.x, segment.EndSize.x) * 0.5f;
                    distance = math.min(distance, math.length(point - segment.Start.xz) - radius);
                    continue;
                }
                if (segment.Shape >= CaveShape.MinedRoom)
                {
                    float2 local = math.abs(RoomPlan(segment, point));
                    float2 halfPlan = segment.StartSize * (segment.Shape == CaveShape.NaturalRoom ? 0.55f : 0.5f);
                    distance = math.min(distance, math.cmax(local - halfPlan));
                    continue;
                }
                float2 start = segment.Start.xz;
                float2 delta = segment.End.xz - start;
                float length = math.length(delta);
                float2 along = length > 1e-5f ? delta / length : new float2(0f, 1f);
                float share = length > 1e-5f ? math.saturate(math.dot(point - start, along) / length) : 0f;
                float2 centre = math.lerp(start, segment.End.xz, share);
                float half = math.lerp(segment.StartSize.x, segment.EndSize.x, share) * 0.5f;
                float side = math.abs(math.dot(point - centre, new float2(-along.y, along.x))) - half;
                float axial = math.max(-math.dot(point - start, segment.StartPlane) - segment.StartOverlap,
                    math.dot(point - segment.End.xz, segment.EndPlane) - segment.EndOverlap);
                distance = math.min(distance, math.max(side, axial));
            }
            return distance;
        }

        /// <summary>
        /// Signed distance to the union of the segments, with rock roughness. Each segment adds its own wall bumps
        /// before the union, so a natural passage joins a mined gallery without a step.
        /// </summary>
        public static float Evaluate(NativeArray<CaveSegment> segments, float3 point, float bumpScale,
            float roughness, float roughnessScale)
        {
            float distance = float.MaxValue;
            float bumps = 0f;
            bool sampled = false;
            for (int index = 0; index < segments.Length; index++)
            {
                CaveSegment segment = segments[index];
                if (math.any(point < segment.BoundsMin) || math.any(point > segment.BoundsMax)) continue;
                distance = math.min(distance, WithBumps(segment, point, bumpScale, ref bumps, ref sampled));
            }
            return AddRoughness(distance, point, roughness, roughnessScale);
        }

        /// <summary>
        /// Managed form of <see cref="Evaluate(NativeArray{CaveSegment}, float3, float, float, float)"/>.
        /// </summary>
        public static float Evaluate(CaveSegment[] segments, float3 point, float bumpScale, float roughness,
            float roughnessScale)
        {
            float distance = float.MaxValue;
            float bumps = 0f;
            bool sampled = false;
            foreach (CaveSegment segment in segments)
            {
                if (math.any(point < segment.BoundsMin) || math.any(point > segment.BoundsMax)) continue;
                distance = math.min(distance, WithBumps(segment, point, bumpScale, ref bumps, ref sampled));
            }
            return AddRoughness(distance, point, roughness, roughnessScale);
        }

        // The segment distance with its wall bumps. The bump noise is the same field for all segments, so it is read
        // once per point.
        private static float WithBumps(in CaveSegment segment, float3 point, float bumpScale, ref float bumps,
            ref bool sampled)
        {
            float distance = Segment(segment, point, out float floorHeight);
            if (segment.NoiseAmplitude <= 0f) return distance;
            if (!sampled)
            {
                bumps = Bumps(point / math.max(bumpScale, 0.1f));
                sampled = true;
            }
            return distance + segment.NoiseAmplitude * bumps * math.saturate(floorHeight / FloorBand);
        }

        // Three octaves of simplex noise, about -1 to 1.
        private static float Bumps(float3 point)
        {
            return (noise.snoise(point) + 0.5f * noise.snoise(point * 2.03f + 17.1f) +
                0.25f * noise.snoise(point * 4.07f + 31.7f)) / 1.75f;
        }

        // Far from every segment the distance stays large, so the noise cannot open the rock there.
        private static float AddRoughness(float distance, float3 point, float roughness, float roughnessScale)
        {
            if (distance > 1e6f || roughness <= 0f) return distance;
            return distance + roughness * noise.snoise(point / math.max(roughnessScale, 0.01f));
        }
    }

    /// <summary>
    /// The cave and ground values at each sample of one chunk. Ground is the terrain height above the sample, so the
    /// rock is where both values are positive.
    /// </summary>
    [BurstCompile]
    internal struct CaveSampleJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<CaveSegment> Segments;
        [ReadOnly] public NativeArray<float> Heights;
        [WriteOnly] public NativeArray<float> Cave;
        [WriteOnly] public NativeArray<float> Ground;
        // World position of the first sample (the apron sample before the chunk) and the sample spacing.
        public float3 Origin;
        public float Step;
        // Samples along each axis of the chunk, and the height grid index of the first chunk sample.
        public int Samples;
        public int2 HeightOffset;
        public int HeightWidth;
        public float BumpScale;
        public float Roughness;
        public float RoughnessScale;

        public void Execute(int index)
        {
            int x = index % Samples;
            int y = index / Samples % Samples;
            int z = index / (Samples * Samples);
            float3 point = Origin + new float3(x, y, z) * Step;
            Cave[index] = CaveField.Evaluate(Segments, point, BumpScale, Roughness, RoughnessScale);
            Ground[index] = Heights[(HeightOffset.y + z) * HeightWidth + HeightOffset.x + x] - point.y;
        }
    }

    /// <summary>
    /// Surface nets over the rock of one chunk. Each cell where the rock starts or ends gets one vertex. Each sample
    /// edge with rock at one end gives one quad. The chunk owns the edges whose lower sample is in the chunk, so the
    /// chunks meet without seams. A quad on the terrain surface (the cave field is not the nearer surface) is kept
    /// only over a terrain hole or its skirt, because the terrain draws it elsewhere.
    /// </summary>
    /// <remarks>
    /// The terrain surface material draws a second copy of some quads, so the hole looks like the terrain around it:
    /// the terrain surface quads, and the cave floor near the hole that lies close below the terrain surface, where
    /// the ground of the terrain goes on into the cave. A floor deep below a hole, for example under a shaft, keeps
    /// the rock. The rock draws the same quads below the copy over a hole (<see cref="HoleRockIndices"/>), so the small
    /// outward move of the copy opens no crack. On the skirt the terrain itself is below the copy. Later,
    /// <see cref="CaveLipJob"/> adds the cave quads near the daylight to the copy.
    /// </remarks>
    [BurstCompile]
    internal struct CaveMeshJob : IJob
    {
        [ReadOnly] public NativeArray<float> Cave;
        [ReadOnly] public NativeArray<float> Ground;
        // Per grid column: 2 over a terrain hole, 1 on the skirt around it, else 0. The terrain height of each column.
        [ReadOnly] public NativeArray<byte> Holes;
        [ReadOnly] public NativeArray<float> Heights;
        public NativeArray<int> CellVertices;
        public NativeList<float3> Positions;
        public NativeList<float3> Normals;
        public NativeList<Color32> Colors;
        // Terrain surface values for the mesh target of the terrain surface: XZ offset to the terrain point (0), cover
        // weight and shadow casting (1). The weight is 1 over a hole and less on the skirt, where the mesh ends along
        // a ragged edge over the terrain.
        public NativeList<float4> Covers;
        // Quads of the cave surface (rock material), the terrain surface quads over a hole that the rock also draws,
        // and the quads of the terrain surface (terrain surface material).
        public NativeList<int> Indices;
        public NativeList<int> HoleRockIndices;
        public NativeList<int> TerrainIndices;
        // Positions and terrain flags of all cell vertices, before unused vertices are removed.
        public NativeList<float3> CellPositions;
        public NativeList<byte> CellOnTerrain;
        public float3 Origin;
        public float Step;
        // Cells along each axis of the chunk. The samples have one apron sample before and two after the chunk.
        public int Cells;
        public int2 HoleOffset;
        public int HoleWidth;
        public float StrataSpacing;

        // Cover weight of the skirt vertices. The full skirt draws: the copy looks the same as the terrain below it, so
        // its cell edge does not show, and it hides the texel edge of the hole. A ragged skirt showed the rock below.
        private const float SkirtCover = 1f;
        // A cave quad whose normal is within about 60 degrees of up is floor.
        private const float FloorFacing = 0.5f;
        // A cave floor this close below the terrain surface carries the terrain ground into the cave, in meters.
        private const float FloorCoverDepth = 2f;
        // Near the edge of a hole the cave surface rises to this height above the terrain, from at most the reach below
        // it, in meters. The terrain draws its own texels up to the hole edge, and a cave floor below them showed the
        // texel edge as teeth and spikes.
        private const float EdgeLift = 0.05f;
        private const float EdgeLiftReach = 0.5f;

        private int SampleCount => Cells + 3;
        private int CellCount => Cells + 2;

        public void Execute()
        {
            for (int index = 0; index < CellVertices.Length; index++)
            {
                CellVertices[index] = -1;
            }
            PlaceVertices();
            LiftHoleEdges();
            AddQuads();
            FinishVertices();
        }

        // Sample value: positive in the rock.
        private float Rock(int sample) => math.min(Cave[sample], Ground[sample]);

        // Indices with the apron: cell and sample -1 is index 0.
        private int Sample(int x, int y, int z) => ((z + 1) * SampleCount + y + 1) * SampleCount + x + 1;
        private int Cell(int x, int y, int z) => ((z + 1) * CellCount + y + 1) * CellCount + x + 1;

        private void PlaceVertices()
        {
            for (int z = -1; z <= Cells; z++)
            {
                for (int y = -1; y <= Cells; y++)
                {
                    for (int x = -1; x <= Cells; x++)
                    {
                        PlaceVertex(x, y, z);
                    }
                }
            }
        }

        // The mean of the points where the cell edges cross the rock surface.
        private void PlaceVertex(int x, int y, int z)
        {
            float3 sum = float3.zero;
            float terrainSide = 0f;
            int crossings = 0;
            int inside = 0;
            for (int corner = 0; corner < 8; corner++)
            {
                if (Rock(Corner(x, y, z, corner)) > 0f) inside++;
            }
            if (inside == 0 || inside == 8) return;
            for (int edge = 0; edge < 12; edge++)
            {
                EdgeCorners(edge, out int a, out int b);
                int sampleA = Corner(x, y, z, a);
                int sampleB = Corner(x, y, z, b);
                float valueA = Rock(sampleA);
                float valueB = Rock(sampleB);
                if (valueA > 0f == valueB > 0f) continue;
                float share = valueA / (valueA - valueB);
                sum += math.lerp(CornerOffset(a), CornerOffset(b), share);
                // The surface at the crossing belongs to the terrain when the ground value is the smaller one there.
                float caveValue = math.lerp(Cave[sampleA], Cave[sampleB], share);
                float groundValue = math.lerp(Ground[sampleA], Ground[sampleB], share);
                terrainSide += groundValue < caveValue ? 1f : 0f;
                crossings++;
            }
            float3 local = sum / crossings;
            CellVertices[Cell(x, y, z)] = CellPositions.Length;
            CellPositions.Add(Origin + (new float3(x, y, z) + 1f + local) * Step);
            CellOnTerrain.Add((byte)(terrainSide * 2f > crossings ? 1 : 0));
        }

        private int Corner(int x, int y, int z, int corner)
        {
            return Sample(x + (corner & 1), y + (corner >> 1 & 1), z + (corner >> 2 & 1));
        }

        private static float3 CornerOffset(int corner)
        {
            return new float3(corner & 1, corner >> 1 & 1, corner >> 2 & 1);
        }

        // The 12 cube edges as pairs of corner numbers (bit 0 = x, bit 1 = y, bit 2 = z).
        private static void EdgeCorners(int edge, out int a, out int b)
        {
            int axis = edge >> 2;
            int other = edge & 3;
            int low = axis == 0 ? other << 1 : axis == 1 ? (other & 1) | (other & 2) << 1 : other;
            a = low;
            b = low | 1 << axis;
        }

        private void AddQuads()
        {
            for (int z = 0; z < Cells; z++)
            {
                for (int y = 0; y < Cells; y++)
                {
                    for (int x = 0; x < Cells; x++)
                    {
                        float value = Rock(Sample(x, y, z));
                        // X edge: the quad joins the cells around it in Y and Z.
                        AddQuad(value, Rock(Sample(x + 1, y, z)), Cell(x, y - 1, z - 1), Cell(x, y, z - 1),
                            Cell(x, y, z), Cell(x, y - 1, z));
                        AddQuad(value, Rock(Sample(x, y + 1, z)), Cell(x - 1, y, z - 1), Cell(x - 1, y, z),
                            Cell(x, y, z), Cell(x, y, z - 1));
                        AddQuad(value, Rock(Sample(x, y, z + 1)), Cell(x - 1, y - 1, z), Cell(x, y - 1, z),
                            Cell(x, y, z), Cell(x - 1, y, z));
                    }
                }
            }
        }

        // Two triangles over four cell vertices. The front faces away from the rock.
        private void AddQuad(float valueA, float valueB, int cell0, int cell1, int cell2, int cell3)
        {
            bool rockA = valueA > 0f;
            if (rockA == valueB > 0f) return;
            int v0 = CellVertices[cell0], v1 = CellVertices[cell1], v2 = CellVertices[cell2], v3 = CellVertices[cell3];
            if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0) return;
            bool onTerrain = CellOnTerrain[v0] != 0 && CellOnTerrain[v1] != 0 && CellOnTerrain[v2] != 0 &&
                CellOnTerrain[v3] != 0;
            float3 centre = (CellPositions[v0] + CellPositions[v2]) * 0.5f;
            byte hole = HoleValue(centre);
            if (onTerrain && hole == 0) return;
            bool nearSurface = ColumnHeight(centre) - centre.y < FloorCoverDepth;
            if (rockA)
            {
                AddFace(v0, v1, v2, v3, onTerrain, hole, nearSurface);
            }
            else
            {
                AddFace(v0, v3, v2, v1, onTerrain, hole, nearSurface);
            }
        }

        // Adds a quad in front-face order to the rock and the terrain surface lists. See the remarks of the job.
        private void AddFace(int v0, int v1, int v2, int v3, bool onTerrain, byte hole, bool nearSurface)
        {
            float3 normal = math.cross(CellPositions[v1] - CellPositions[v0], CellPositions[v2] - CellPositions[v0]);
            bool floor = normal.y > FloorFacing * math.length(normal);
            bool rock = !onTerrain || hole == 2;
            bool terrain = onTerrain || floor && hole != 0 && nearSurface;
            if (rock) AddTriangles(onTerrain ? HoleRockIndices : Indices, v0, v1, v2, v3);
            if (terrain) AddTriangles(TerrainIndices, v0, v1, v2, v3);
        }

        private static void AddTriangles(NativeList<int> target, int v0, int v1, int v2, int v3)
        {
            target.Add(v0); target.Add(v1); target.Add(v2);
            target.Add(v0); target.Add(v2); target.Add(v3);
        }

        private bool OverHole(float3 position) => HoleValue(position) != 0;

        // Raises the cell vertices near a hole edge to just above the terrain. Vertices deeper than the reach, for
        // example a floor under a rock lintel, keep their place.
        private void LiftHoleEdges()
        {
            for (int vertex = 0; vertex < CellPositions.Length; vertex++)
            {
                float3 position = CellPositions[vertex];
                if (!NearHoleEdge(position) || !TryTerrainHeight(position, out float height)) continue;
                float target = height + EdgeLift;
                float below = target - position.y;
                if (below > 0f && below < EdgeLiftReach + EdgeLift)
                {
                    CellPositions[vertex] = new float3(position.x, target, position.z);
                }
            }
        }

        // True on the skirt, and on a hole column beside a column that the terrain draws.
        private bool NearHoleEdge(float3 position)
        {
            int x = (int)math.round((position.x - Origin.x) / Step) - 1 + HoleOffset.x;
            int z = (int)math.round((position.z - Origin.z) / Step) - 1 + HoleOffset.y;
            int depth = Holes.Length / HoleWidth;
            if (x < 1 || z < 1 || x >= HoleWidth - 1 || z >= depth - 1) return false;
            byte value = Holes[z * HoleWidth + x];
            if (value != 2) return value == 1;
            for (int nearZ = z - 1; nearZ <= z + 1; nearZ++)
            {
                for (int nearX = x - 1; nearX <= x + 1; nearX++)
                {
                    if (Holes[nearZ * HoleWidth + nearX] != 2) return true;
                }
            }
            return false;
        }

        // Terrain height at a position, bilinear between the grid columns. False outside the terrain.
        private bool TryTerrainHeight(float3 position, out float height)
        {
            float2 column = new float2((position.x - Origin.x) / Step, (position.z - Origin.z) / Step) - 1f +
                HoleOffset;
            int2 low = (int2)math.floor(column);
            float2 t = column - low;
            int depth = Heights.Length / HoleWidth;
            height = 0f;
            if (low.x < 0 || low.y < 0 || low.x + 1 >= HoleWidth || low.y + 1 >= depth) return false;
            float a = Heights[low.y * HoleWidth + low.x];
            float b = Heights[low.y * HoleWidth + low.x + 1];
            float c = Heights[(low.y + 1) * HoleWidth + low.x];
            float d = Heights[(low.y + 1) * HoleWidth + low.x + 1];
            // Columns outside every terrain hold a very low height.
            if (math.min(math.min(a, b), math.min(c, d)) < -1e4f) return false;
            height = math.lerp(math.lerp(a, b, t.x), math.lerp(c, d, t.x), t.y);
            return true;
        }

        // Terrain height of the grid column of a position. The height grid has the same columns as the hole grid.
        private float ColumnHeight(float3 position)
        {
            int x = (int)math.round((position.x - Origin.x) / Step) - 1 + HoleOffset.x;
            int z = (int)math.round((position.z - Origin.z) / Step) - 1 + HoleOffset.y;
            int depth = Heights.Length / HoleWidth;
            if (x < 0 || z < 0 || x >= HoleWidth || z >= depth) return float.MinValue;
            return Heights[z * HoleWidth + x];
        }

        private byte HoleValue(float3 position)
        {
            int x = (int)math.round((position.x - Origin.x) / Step) - 1 + HoleOffset.x;
            int z = (int)math.round((position.z - Origin.z) / Step) - 1 + HoleOffset.y;
            int depth = Holes.Length / HoleWidth;
            if (x < 0 || z < 0 || x >= HoleWidth || z >= depth) return 0;
            return Holes[z * HoleWidth + x];
        }

        // Keeps only the used vertices and adds smooth normals, colors and terrain surface values.
        private void FinishVertices()
        {
            var remap = new NativeArray<int>(CellPositions.Length, Allocator.Temp);
            for (int index = 0; index < remap.Length; index++)
            {
                remap[index] = -1;
            }
            Remap(Indices, remap);
            Remap(HoleRockIndices, remap);
            Remap(TerrainIndices, remap);
            AddNormals(Indices);
            AddNormals(HoleRockIndices);
            AddNormals(TerrainIndices);
            Colors.ResizeUninitialized(Positions.Length);
            Covers.ResizeUninitialized(Positions.Length);
            for (int vertex = 0; vertex < Positions.Length; vertex++)
            {
                Normals[vertex] = math.normalizesafe(Normals[vertex], new float3(0f, 1f, 0f));
                Colors[vertex] = RockColor(Positions[vertex]);
                float cover = HoleValue(Positions[vertex]) == 2 ? 1f : SkirtCover;
                Covers[vertex] = new float4(0f, 0f, cover, 1f);
            }
            remap.Dispose();
        }

        private void Remap(NativeList<int> indices, NativeArray<int> remap)
        {
            for (int index = 0; index < indices.Length; index++)
            {
                int vertex = indices[index];
                if (remap[vertex] < 0)
                {
                    remap[vertex] = Positions.Length;
                    Positions.Add(CellPositions[vertex]);
                    Normals.Add(float3.zero);
                }
                indices[index] = remap[vertex];
            }
        }

        // Area-weighted face normals, added to each corner.
        private void AddNormals(NativeList<int> indices)
        {
            for (int index = 0; index < indices.Length; index += 3)
            {
                int a = indices[index], b = indices[index + 1], c = indices[index + 2];
                float3 normal = math.cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
                Normals[a] += normal;
                Normals[b] += normal;
                Normals[c] += normal;
            }
        }

        // Rock face vertex values: R hardness, G cavity, B layer value, A ground weight. The layers follow the height,
        // so the cave shows the same banded rock as the quarry faces.
        private Color32 RockColor(float3 position)
        {
            float warp = noise.snoise(position * 0.03f) * 0.6f;
            float layer = 0.5f + 0.5f * math.sin((position.y / math.max(StrataSpacing, 0.1f) + warp) * 6.2831853f);
            float hardness = math.saturate(0.5f + 0.5f * noise.snoise(position * 0.08f));
            return new Color32((byte)(hardness * 255f), 0, (byte)(layer * 255f), 0);
        }
    }
}
