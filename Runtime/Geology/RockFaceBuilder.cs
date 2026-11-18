using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Geology
{
    internal struct RockFaceStats
    {
        public int ColumnCount;
        public int ActiveColumnCount;
        public int StripCount;
        public int VertexCount;
        public int TriangleCount;
        public int CorrectionCount;
        public float ProfileMilliseconds;
        public float SurfaceMilliseconds;
    }

    // One continuous grid of the rock surface. Column c, row r is at index c * Rows + r. Rows run from the cap edge,
    // down the face, to the skirt edge. A loop joins its last column to its first.
    // A build uses its strips again for the next build, so the arrays can be longer than VertexCount.
    internal sealed class RockFaceStrip
    {
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public bool Loop { get; private set; }
        public int VertexCount => Columns * Rows;
        public Vector3[] Positions { get; private set; } = new Vector3[0];
        public Vector3[] Normals { get; private set; } = new Vector3[0];
        public Color32[] Colors { get; private set; } = new Color32[0];
        // World XZ offset from the vertex to the terrain point whose layers the ground blend shows.
        public Vector2[] GroundOffsets { get; private set; } = new Vector2[0];
        // 1 = drawn, 0 = cut away. Between, the shader cuts the surface away along a ragged noise edge, so a surface that
        // goes into the terrain ends in an irregular edge, not in a line where the two surfaces cross.
        public float[] Show { get; private set; } = new float[0];
        // Terrain surface drawn over the rock, for example the soil on a cliff top: x and y are the world XZ offset from
        // the vertex to the terrain point whose layers it shows, z is the cover weight. 0 = no cover. Below 1 the cover
        // ends along a ragged edge. w is 1 where the cover casts a shadow, for example on a rounded edge, and 0 where it
        // lies on the terrain, which casts there itself. The chunks draw the covered quads again with the terrain
        // surface mesh material.
        public Vector4[] TerrainCover { get; private set; } = new Vector4[0];

        // True when the grid triangles (column c, row r), (c + 1, r), (c, r + 1) face away from the surface normal.
        public bool FlipWinding { get; set; }
        // True for the rock top inside a closed spline. Its columns run along world X and its rows along world Z.
        public bool IsTop { get; set; }
        // Top only: false for a grid point outside the top. A quad with such a corner gets no triangles.
        // With CullHidden: false for a grid point that nobody can see. A quad gets no triangles when all grid points
        // that it covers are hidden.
        public bool[] Active { get; private set; } = new bool[0];
        // True when the Active values mark hidden grid points, for example rock inside a neighbour block.
        public bool CullHidden { get; set; }

        public int Index(int column, int row) => column * Rows + row;

        // Sets the grid size for a new build. The build writes every vertex, normal and color.
        public void Reset(int columns, int rows, bool loop)
        {
            Columns = columns;
            Rows = rows;
            Loop = loop;
            FlipWinding = false;
            IsTop = false;
            CullHidden = false;
            if (Positions.Length < VertexCount)
            {
                Positions = new Vector3[VertexCount];
                Normals = new Vector3[VertexCount];
                Colors = new Color32[VertexCount];
                GroundOffsets = new Vector2[VertexCount];
                Active = new bool[VertexCount];
                Show = new float[VertexCount];
                TerrainCover = new Vector4[VertexCount];
            }
            // Most builders draw all of their surface and do not write the show or cover values.
            System.Array.Fill(Show, 1f, 0, VertexCount);
            System.Array.Clear(TerrainCover, 0, VertexCount);
        }
    }

    // The spline points and the terrain heights of one build. The main thread reads them, because the spline transform
    // and the terrain data are main-thread objects. The build then runs on any thread. The points are part of the
    // build workspace, so the array can be longer than PointCount.
    internal sealed class RockFaceInput : System.IDisposable
    {
        public Vector3[] Points;
        public int PointCount;
        public bool Closed;
        public TerrainHeightSampler Terrain;

        public void Dispose()
        {
            Terrain?.Dispose();
        }
    }

    // Builds the world-space surface grid over the steep terrain next to a cliff-top spline.
    // Columns run across the cliff. Rows follow the terrain profile from the cap, down the face, to the skirt.
    internal static class RockFaceBuilder
    {
        private const float DenseSampleSpacing = 0.1f;
        private const float TangentReach = 1f;
        private const float NormalReach = 0.75f;
        private const float DropSideReach = 12f;
        private const float DropSideWindow = 10f;
        private const int CorrectionSteps = 8;
        private const float CorrectionStep = 0.25f;
        private const float LoopBlend = 6f;
        private const float SlopeReach = 2f;
        // Smallest depth under the final terrain before a rock face point counts as hidden, in meters.
        private const float BuriedMargin = 0.3f;
        // Distance in front of a rock point where the ground blend also reads the ground, in meters (GroundWeight).
        private const float GroundProbe = 1.5f;

        internal struct Column
        {
            public Vector3 Origin;
            public Vector3 Outward;
            public float U;
            public bool Active;
            public float Rim;
            public float Foot;
            // Part of the workspace heights. No array when a sample is outside the ground.
            public System.ArraySegment<float> Heights;
        }

        /// <summary>
        /// Buffers of one build, used again by later builds, so a build makes no new arrays for its columns and strips.
        /// Each running build takes its own workspace. The strips stay valid until the workspace is returned. A
        /// workspace grows to the largest rock face that used it.
        /// </summary>
        internal sealed class Workspace
        {
            public Vector3[] Points = new Vector3[0];
            public float[] Distances = new float[0];
            public Column[] Columns = new Column[0];
            public float[] Votes = new float[0];
            public float[] Rims = new float[0];
            public float[] Feet = new float[0];
            public float[] Heights = new float[0];
            public readonly List<List<Vector2>> Profiles = new();
            public readonly List<List<float>> Arcs = new();
            // The strips of the current build. The slots keep the strips of earlier builds for use again.
            public readonly List<RockFaceStrip> Strips = new();
            private readonly List<RockFaceStrip> _stripSlots = new();

            public RockFaceStrip TakeStrip(int columns, int rows, bool loop)
            {
                if (_stripSlots.Count == Strips.Count) _stripSlots.Add(new RockFaceStrip());
                RockFaceStrip strip = _stripSlots[Strips.Count];
                strip.Reset(columns, rows, loop);
                Strips.Add(strip);
                return strip;
            }
        }

        private static readonly Stack<Workspace> Workspaces = new();

        // A buffer of at least the length. A longer buffer keeps its old values past the length.
        private static T[] Reserve<T>(ref T[] buffer, int length)
        {
            if (buffer.Length < length) buffer = new T[length];
            return buffer;
        }

        /// <summary>A workspace for one build. Any thread.</summary>
        public static Workspace RentWorkspace()
        {
            lock (Workspaces)
            {
                return Workspaces.Count > 0 ? Workspaces.Pop() : new Workspace();
            }
        }

        /// <summary>Gives back a workspace after its strips are used. Any thread.</summary>
        public static void ReturnWorkspace(Workspace workspace)
        {
            lock (Workspaces)
            {
                Workspaces.Push(workspace);
            }
        }

        /// <param name="ground">
        /// Heights the rock builds on. The terrain heights give the ground blend. The reader must be safe on worker
        /// threads.
        /// </param>
        /// <param name="openings">Passages where the rock stays below a height limit.</param>
        /// <param name="region">World XZ region that the build reads.</param>
        public static List<RockFaceStrip> Build(SplineContainer spline, RockFaceSettings settings,
            IReadOnlyList<RockFaceOpening> openings, IRockFaceGroundReader ground, Rect region, out RockFaceStats stats)
        {
            // The caller keeps the strips, so they get their own workspace.
            Workspace workspace = new();
            using RockFaceInput input = Prepare(spline, region, workspace);
            input.Terrain.WaitForReads();
            return Build(input, settings, openings, ground, workspace, out stats);
        }

        /// <summary>
        /// Reads the spline points into the workspace and starts the GPU reads of the terrain heights of the region for
        /// a build. Main thread. The build must start after <see cref="TerrainHeightSampler.IsReady"/> or
        /// <see cref="TerrainHeightSampler.WaitForReads"/>.
        /// </summary>
        public static RockFaceInput Prepare(SplineContainer spline, Rect region, Workspace workspace)
        {
            float length = spline.CalculateLength();
            int denseCount = Mathf.Max(64, Mathf.CeilToInt(length / DenseSampleSpacing));
            Vector3[] points = Reserve(ref workspace.Points, denseCount + 1);
            for (int index = 0; index <= denseCount; index++)
            {
                points[index] = spline.EvaluatePosition(index / (float)denseCount);
            }
            return new RockFaceInput
            {
                Points = points,
                PointCount = denseCount + 1,
                Closed = spline.Spline.Closed,
                Terrain = new TerrainHeightSampler(region)
            };
        }

        /// <summary>
        /// Builds the strips from prepared input into the workspace. The strips are valid until the workspace is
        /// returned. Any thread.
        /// </summary>
        public static List<RockFaceStrip> Build(RockFaceInput input, RockFaceSettings settings,
            IReadOnlyList<RockFaceOpening> openings, IRockFaceGroundReader ground, Workspace workspace,
            out RockFaceStats stats)
        {
            stats = default;
            Stopwatch timer = Stopwatch.StartNew();
            List<RockFaceStrip> strips = workspace.Strips;
            strips.Clear();
            TerrainHeightSampler terrain = input.Terrain;
            IRockFaceGroundReader sampler = ground ?? terrain;
            int columnCount = CreateColumns(input, settings, sampler, workspace, out float loopLength);
            Column[] columns = workspace.Columns;
            stats.ColumnCount = columnCount;
            if (columnCount < 2) return strips;

            float yMinimum = float.MaxValue;
            float yMaximum = float.MinValue;
            float splineLength = workspace.Distances[input.PointCount - 1];
            for (int index = 0; index < columnCount; index++)
            {
                DetectFace(ref columns[index], settings);
                if (IsExcluded(columns[index].U / splineLength, settings)) columns[index].Active = false;
                if (!columns[index].Active) continue;
                stats.ActiveColumnCount++;
                foreach (float height in columns[index].Heights)
                {
                    yMinimum = Mathf.Min(yMinimum, height);
                    yMaximum = Mathf.Max(yMaximum, height);
                }
            }
            if (stats.ActiveColumnCount < 2) return strips;

            SmoothProfiles(columns, columnCount, settings, loopLength > 0f, workspace);
            stats.ProfileMilliseconds = (float)timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            if (settings.BlockWalls)
            {
                // Separate rock blocks in place of the skin and the top.
                bool fullLoop = loopLength > 0f && stats.ActiveColumnCount == columnCount;
                RockBlockBuilder.BuildBand(columns, columnCount, fullLoop, settings, openings, terrain, workspace);
            }
            else
            {
                BuildSkin(columns, columnCount, loopLength, yMinimum, yMaximum, settings, openings, sampler, terrain,
                    workspace, ref stats);
            }
            stats.StripCount = strips.Count;
            foreach (RockFaceStrip strip in strips)
            {
                stats.VertexCount += strip.VertexCount;
                int quadColumns = strip.Loop ? strip.Columns : strip.Columns - 1;
                stats.TriangleCount += quadColumns * (strip.Rows - 1) * 2;
            }
            stats.SurfaceMilliseconds = (float)timer.Elapsed.TotalMilliseconds;
            return strips;
        }

        // The rock skin over the face columns, and the top when the face fills it.
        private static void BuildSkin(Column[] columns, int columnCount, float loopLength, float yMinimum,
            float yMaximum, RockFaceSettings settings, IReadOnlyList<RockFaceOpening> openings,
            IRockFaceGroundReader sampler, TerrainHeightSampler terrain, Workspace workspace, ref RockFaceStats stats)
        {
            RockFaceDetail detail = new(settings, columns[0].U, columns[columnCount - 1].U, yMinimum, yMaximum);
            // A closed spline with rock all around on one side becomes one strip that joins its last column to its first.
            if (loopLength > 0f && stats.ActiveColumnCount == columnCount && !HasSideChange(columns, columnCount))
            {
                BuildStrip(columns, 0, columnCount - 1, loopLength, settings, detail, openings, sampler, terrain,
                    workspace, ref stats.CorrectionCount);
            }
            else
            {
                int start = -1;
                for (int index = 0; index <= columnCount; index++)
                {
                    bool active = index < columnCount && columns[index].Active;
                    bool sideChange = active && start >= 0 && IsSideChange(columns[index - 1], columns[index]);
                    if (start >= 0 && (!active || sideChange))
                    {
                        if (index - start >= 2)
                        {
                            BuildStrip(columns, start, index - 1, 0f, settings, detail, openings, sampler, terrain,
                                workspace, ref stats.CorrectionCount);
                        }
                        start = -1;
                    }
                    if (active && start < 0) start = index;
                }
            }
            if (settings.FillTop && loopLength > 0f)
            {
                // The same bands as the walls, extended upward over the crags, so the strata run on into the top.
                var topDetail = new RockFaceDetail(settings, columns[0].U, columns[columnCount - 1].U, yMinimum,
                    yMaximum + settings.CragHeight * (1f + settings.CragEdgeRise) + 5f);
                BuildTop(columns, columnCount, settings, topDetail, openings, sampler, terrain, workspace);
            }
        }

        #region Columns
        // True when a share of the spline length is in an excluded range of the settings.
        internal static bool IsExcluded(float share, RockFaceSettings settings)
        {
            if (settings.ExcludedRanges == null) return false;
            foreach (Vector2 range in settings.ExcludedRanges)
            {
                if (share >= Mathf.Min(range.x, range.y) && share <= Mathf.Max(range.x, range.y)) return true;
            }
            return false;
        }

        // Fills the workspace columns. Returns the column count.
        private static int CreateColumns(RockFaceInput input, RockFaceSettings settings,
            IRockFaceGroundReader sampler, Workspace workspace, out float loopLength)
        {
            loopLength = 0f;
            Vector3[] points = input.Points;
            int pointCount = input.PointCount;
            int denseCount = pointCount - 1;
            float[] distances = Reserve(ref workspace.Distances, pointCount);
            distances[0] = 0f;
            for (int index = 1; index <= denseCount; index++)
            {
                Vector3 step = points[index] - points[index - 1];
                step.y = 0f;
                distances[index] = distances[index - 1] + step.magnitude;
            }

            float total = distances[denseCount];
            float start = Mathf.Min(settings.RangeStart, settings.RangeEnd) * total;
            float end = Mathf.Max(settings.RangeStart, settings.RangeEnd) * total;
            bool loop = input.Closed && start <= 0f && end >= total;
            // A loop gets a whole number of columns, so the last column meets the first at the column spacing.
            int count = loop
                ? Mathf.RoundToInt(total / settings.ColumnSpacing)
                : Mathf.FloorToInt((end - start) / settings.ColumnSpacing) + 1;
            if (count < 2) return 0;
            float spacing = loop ? total / count : settings.ColumnSpacing;
            if (loop) loopLength = total;

            Column[] columns = Reserve(ref workspace.Columns, count);
            int cursor = 0;
            for (int index = 0; index < count; index++)
            {
                float u = start + index * spacing;
                Vector3 origin = PointAt(points, distances, pointCount, u, ref cursor);
                int aheadCursor = 0;
                int behindCursor = 0;
                float ahead = loop ? Mathf.Repeat(u + TangentReach, total) : Mathf.Min(u + TangentReach, total);
                float behind = loop ? Mathf.Repeat(u - TangentReach, total) : Mathf.Max(u - TangentReach, 0f);
                Vector3 tangent = PointAt(points, distances, pointCount, ahead, ref aheadCursor) -
                                  PointAt(points, distances, pointCount, behind, ref behindCursor);
                tangent.y = 0f;
                tangent = tangent.sqrMagnitude > 1e-6f ? tangent.normalized : Vector3.forward;
                columns[index] = new Column
                {
                    Origin = new Vector3(origin.x, 0f, origin.z),
                    Outward = new Vector3(tangent.z, 0f, -tangent.x),
                    U = u
                };
            }
            ResolveDropSide(columns, count, settings, sampler, workspace);

            int sampleCount = Mathf.FloorToInt((settings.SearchInward + settings.SearchOutward) /
                                               settings.ProfileStep) + 1;
            if (workspace.Heights.Length < count * sampleCount) workspace.Heights = new float[count * sampleCount];
            float[] heights = workspace.Heights;
            Parallel.For(0, count, index =>
            {
                int offset = index * sampleCount;
                bool valid = true;
                for (int sample = 0; sample < sampleCount && valid; sample++)
                {
                    float distance = -settings.SearchInward + sample * settings.ProfileStep;
                    Vector3 position = columns[index].Origin + columns[index].Outward * distance;
                    valid = sampler.TrySampleHeight(position.x, position.z, out heights[offset + sample]);
                }
                columns[index].Heights = valid ? new System.ArraySegment<float>(heights, offset, sampleCount) : default;
            });
            return count;
        }

        private static Vector3 PointAt(Vector3[] points, float[] distances, int pointCount, float distance,
            ref int cursor)
        {
            while (cursor < pointCount - 2 && distances[cursor + 1] < distance) cursor++;
            float span = distances[cursor + 1] - distances[cursor];
            float t = span > 1e-6f ? Mathf.Clamp01((distance - distances[cursor]) / span) : 0f;
            return Vector3.Lerp(points[cursor], points[cursor + 1], t);
        }

        private static void ResolveDropSide(Column[] columns, int columnCount, RockFaceSettings settings,
            IRockFaceGroundReader sampler, Workspace workspace)
        {
            if (settings.DropSide != RockFaceDropSide.Auto)
            {
                float fixedSign = settings.DropSide == RockFaceDropSide.Right ? 1f : -1f;
                for (int index = 0; index < columnCount; index++)
                {
                    columns[index].Outward *= fixedSign;
                }
                return;
            }

            // The ground is lower on the drop side. A majority over a window keeps the side stable.
            float[] votes = Reserve(ref workspace.Votes, columnCount);
            for (int index = 0; index < columnCount; index++)
            {
                votes[index] = SideHeight(columns[index], -1f, sampler) > SideHeight(columns[index], 1f, sampler)
                    ? 1f : -1f;
            }
            int window = Mathf.Max(1, Mathf.RoundToInt(DropSideWindow / settings.ColumnSpacing));
            for (int index = 0; index < columnCount; index++)
            {
                float sum = 0f;
                for (int other = Mathf.Max(0, index - window);
                     other <= Mathf.Min(columnCount - 1, index + window); other++)
                {
                    sum += votes[other];
                }
                columns[index].Outward *= sum >= 0f ? 1f : -1f;
            }
        }

        // The drop side changes between two neighbour columns. A strip must end there, because a grid that crosses
        // the spline folds over itself and one winding cannot face out on both sides.
        private static bool IsSideChange(Column previous, Column next)
        {
            return Vector3.Dot(previous.Outward, next.Outward) < 0f;
        }

        private static bool HasSideChange(Column[] columns, int columnCount)
        {
            for (int index = 0; index < columnCount; index++)
            {
                if (IsSideChange(columns[index], columns[(index + 1) % columnCount])) return true;
            }
            return false;
        }

        private static float SideHeight(Column column, float sign, IRockFaceGroundReader sampler)
        {
            float total = 0f;
            for (int step = 1; step <= 2; step++)
            {
                Vector3 position = column.Origin + column.Outward * (sign * DropSideReach * step);
                if (sampler.TrySampleHeight(position.x, position.z, out float height)) total += height;
            }
            return total;
        }
        #endregion

        #region Face Detection
        // The face is the steep run with the largest drop. Benches shorter than the bench tolerance do not split a run,
        // so a rough or stepped wall counts as one face. Past the rim, the search ends where the ground rises again by
        // more than the minimum face height, so a face across a gap, for example the far side of an island, does not
        // count.
        private static void DetectFace(ref Column column, RockFaceSettings settings)
        {
            column.Active = false;
            System.ArraySegment<float> heights = column.Heights;
            if (heights.Array == null) return;

            // Drop rate over about one meter, so single terrain texels do not decide the face.
            int reach = Mathf.Max(1, Mathf.RoundToInt(0.5f / settings.ProfileStep));
            float threshold = Mathf.Tan(settings.FaceAngle * Mathf.Deg2Rad);
            int benchSamples = Mathf.Max(1, Mathf.RoundToInt(settings.BenchTolerance / settings.ProfileStep));
            int end = SearchEnd(heights, settings);
            float bestDrop = settings.MinimumFaceHeight;
            int sample = 0;
            while (sample < end)
            {
                if (DropRate(heights, sample, reach, settings.ProfileStep) <= threshold)
                {
                    sample++;
                    continue;
                }

                int rim = sample;
                int lastSteep = sample;
                while (sample < end && sample - lastSteep <= benchSamples)
                {
                    if (DropRate(heights, sample, reach, settings.ProfileStep) > threshold) lastSteep = sample;
                    sample++;
                }
                int foot = Mathf.Min(lastSteep + reach, end - 1);
                float drop = heights[rim] - heights[foot];
                if (drop < bestDrop) continue;

                bestDrop = drop;
                column.Active = true;
                column.Rim = -settings.SearchInward + rim * settings.ProfileStep;
                column.Foot = -settings.SearchInward + foot * settings.ProfileStep;
            }
        }

        // First sample past the rim where the ground is higher than the lowest ground before it by more than the
        // minimum face height (at least 1 m), or the sample count. The rim is the cap length in front of the spline.
        // The search starts from the highest rim ground, so a knob on the rim does not end the search.
        private static int SearchEnd(System.ArraySegment<float> heights, RockFaceSettings settings)
        {
            int start = Mathf.Clamp(Mathf.RoundToInt(settings.SearchInward / settings.ProfileStep), 0, heights.Count - 1);
            int rimEnd = Mathf.Min(heights.Count - 1,
                start + Mathf.CeilToInt(settings.CapLength / settings.ProfileStep));
            float rise = Mathf.Max(settings.MinimumFaceHeight, 1f);
            float lowest = heights[start];
            for (int sample = start + 1; sample <= rimEnd; sample++)
            {
                lowest = Mathf.Max(lowest, heights[sample]);
            }
            for (int sample = rimEnd + 1; sample < heights.Count; sample++)
            {
                if (heights[sample] > lowest + rise) return sample;
                lowest = Mathf.Min(lowest, heights[sample]);
            }
            return heights.Count;
        }

        private static float DropRate(System.ArraySegment<float> heights, int sample, int reach, float step)
        {
            int inner = Mathf.Max(0, sample - reach);
            int outer = Mathf.Min(heights.Count - 1, sample + reach);
            return (heights[inner] - heights[outer]) / Mathf.Max(step, (outer - inner) * step);
        }

        private static void SmoothProfiles(Column[] columns, int columnCount, RockFaceSettings settings, bool loop,
            Workspace workspace)
        {
            int window = Mathf.RoundToInt(settings.ProfileSmoothing / settings.ColumnSpacing);
            if (window < 1) return;

            float[] rims = Reserve(ref workspace.Rims, columnCount);
            float[] feet = Reserve(ref workspace.Feet, columnCount);
            for (int index = 0; index < columnCount; index++)
            {
                if (!columns[index].Active) continue;
                float rimSum = 0f;
                float footSum = 0f;
                int count = 0;
                for (int offset = -window; offset <= window; offset++)
                {
                    int other = index + offset;
                    if (loop) other = (other % columnCount + columnCount) % columnCount;
                    if (other < 0 || other >= columnCount || !columns[other].Active) continue;
                    rimSum += columns[other].Rim;
                    footSum += columns[other].Foot;
                    count++;
                }
                rims[index] = rimSum / count;
                feet[index] = footSum / count;
            }
            for (int index = 0; index < columnCount; index++)
            {
                if (!columns[index].Active) continue;
                columns[index].Rim = rims[index];
                columns[index].Foot = Mathf.Max(feet[index], rims[index] + settings.ProfileStep);
            }
        }
        #endregion

        #region Mesh
        // Adds the strip to the workspace strips.
        private static void BuildStrip(Column[] columns, int first, int last, float loopLength,
            RockFaceSettings settings, RockFaceDetail detail, IReadOnlyList<RockFaceOpening> openings,
            IRockFaceGroundReader sampler, TerrainHeightSampler terrain, Workspace workspace, ref int corrections)
        {
            float maximumLength = 0f;
            List<List<Vector2>> profiles = workspace.Profiles;
            List<List<float>> arcs = workspace.Arcs;
            for (int index = first; index <= last; index++)
            {
                if (profiles.Count <= index - first)
                {
                    profiles.Add(new List<Vector2>());
                    arcs.Add(new List<float>());
                }
                ExtractProfile(columns[index], settings, profiles[index - first]);
                ArcLengths(profiles[index - first], arcs[index - first]);
                maximumLength = Mathf.Max(maximumLength, arcs[index - first][^1]);
            }

            int rowCount = Mathf.Max(2, Mathf.CeilToInt(maximumLength / settings.RowSpacing));
            RockFaceBlocks blocks = settings.Blocks;
            RockFaceStrip strip = workspace.TakeStrip(last - first + 1, rowCount + 1, loopLength > 0f);
            // Rock under the final terrain, for example under a talus or at the tucked edges, is not drawn.
            strip.CullHidden = true;
            float buriedDepth = Mathf.Max(BuriedMargin, 0.5f * settings.TuckDepth);
            float stripStart = columns[first].U;
            float stripEnd = columns[last].U;
            int stripCorrections = 0;
            Parallel.For(first, last + 1, index =>
            {
                Column column = columns[index];
                List<Vector2> profile = profiles[index - first];
                List<float> arc = arcs[index - first];
                float profileStart = profile[0].x;
                float profileEnd = profile[^1].x;
                // The ground blend reads the terrain beside the rock, as the eye sees it: past the cap edge for the
                // upper half of the rows, past the skirt edge for the lower half.
                Vector3 capGround = column.Origin + column.Outward * (profileStart - settings.GroundReach);
                Vector3 skirtGround = column.Origin + column.Outward * (profileEnd + settings.GroundReach);
                float rimHeight = HeightAt(column, settings, column.Rim);
                float endWeight = loopLength > 0f || settings.EndFade <= 0f
                    ? 1f
                    : Smooth01(Mathf.Min(column.U - stripStart, stripEnd - column.U) / settings.EndFade);
                for (int row = 0; row <= rowCount; row++)
                {
                    float s = arc[^1] * row / rowCount;
                    Vector2 point = ProfileAt(profile, arc, s);
                    Vector2 tangent = ProfileAt(profile, arc, s + NormalReach) - ProfileAt(profile, arc, s - NormalReach);
                    Vector2 normal2 = new Vector2(-tangent.y, tangent.x).normalized;
                    Vector3 normal = (column.Outward * normal2.x + Vector3.up * normal2.y).normalized;

                    float capWeight = Smooth01((point.x - profileStart) / Mathf.Max(0.01f, column.Rim - profileStart));
                    float skirtWeight = Smooth01((profileEnd - point.x) / Mathf.Max(0.01f, profileEnd - column.Foot));
                    float weight = Mathf.Min(endWeight, Mathf.Min(capWeight, skirtWeight));
                    // Layers and joints fade on the gentle apron below the face, so the apron gets no stacked rings.
                    Vector2 slope = ProfileAt(profile, arc, s + SlopeReach) - ProfileAt(profile, arc, s - SlopeReach);
                    float verticalShare = Mathf.Abs(slope.y) / Mathf.Max(slope.magnitude, 1e-4f);
                    float structure = point.x <= column.Rim
                        ? 1f
                        : Mathf.Lerp(0.25f, 1f, Smooth01((verticalShare - 0.64f) / 0.27f));
                    float offset = EvaluateDetail(detail, column.U, point.y, structure, loopLength,
                        out RockFaceSample sample);
                    if (blocks.Enabled)
                    {
                        Vector3 position = column.Origin + column.Outward * point.x + Vector3.up * point.y;
                        offset = BlockOffset(blocks, settings.Seed, column.U, position, rimHeight,
                            column.Rim - point.x, normal, verticalShare, offset, loopLength, ref sample);
                    }
                    float displacement = Mathf.Lerp(-settings.TuckDepth, settings.Clearance + offset, weight);

                    Vector3 basePosition = column.Origin + column.Outward * point.x;
                    basePosition.y = point.y;
                    Vector3 vertex = basePosition + normal * displacement;
                    if (weight >= 0.999f && Conform(ref vertex, normal, settings.Clearance, sampler))
                    {
                        Interlocked.Increment(ref stripCorrections);
                    }
                    if (openings != null)
                    {
                        // An index loop: a foreach over IReadOnlyList allocated an enumerator for each vertex.
                        for (int opening = 0; opening < openings.Count; opening++)
                        {
                            vertex.y = Mathf.Min(vertex.y, openings[opening].HeightLimit(vertex.x, vertex.z));
                        }
                    }

                    int vertexIndex = strip.Index(index - first, row);
                    strip.Positions[vertexIndex] = vertex;
                    strip.Active[vertexIndex] = !(terrain.TrySampleHeight(vertex.x, vertex.z, out float buried) &&
                                                  vertex.y < buried - buriedDepth);
                    // A filled formation has rock behind the rim, so its cap gets no ground.
                    float groundWeight = settings.FillTop && point.x < column.Rim
                        ? 0f
                        : GroundWeight(vertex, column.Outward, column.U, settings, terrain);
                    strip.Colors[vertexIndex] = new Color(sample.Hardness, sample.Cavity, sample.Layer, groundWeight);
                    Vector3 ground = 2 * row < rowCount ? capGround : skirtGround;
                    strip.GroundOffsets[vertexIndex] = new Vector2(ground.x - vertex.x, ground.z - vertex.z);
                }
            });
            corrections += stripCorrections;
            ComputeNormals(strip, columns, first);
        }

        // Outward offset with rock blocks: the block surface, a share of the face rules, flutes on steep parts and
        // surface noise. Fissures and grooves get a darker colour.
        private static float BlockOffset(RockFaceBlocks blocks, int seed, float u, Vector3 position, float rimHeight,
            float behindRim, Vector3 normal, float verticalShare, float detailOffset, float loopLength,
            ref RockFaceSample sample)
        {
            float block = RockFaceBlockCells.Offset(blocks, seed, u, position.y, rimHeight - position.y, behindRim,
                loopLength, out float crevice);
            float flutes = RockFaceNoise.Flutes(position, normal, blocks.FluteSpacing);
            float offset = block + blocks.DetailStrength * detailOffset +
                           blocks.FluteDepth * verticalShare * flutes +
                           blocks.NoiseDepth * RockFaceNoise.Surface(position, blocks.NoiseScale);
            sample.Cavity = Mathf.Max(sample.Cavity, crevice);
            return Mathf.Max(0f, offset);
        }

        // Normals from the whole grid, so neighbour chunks and all levels of detail share them at their seams.
        internal static void ComputeNormals(RockFaceStrip strip, Column[] columns, int first)
        {
            float facing = 0f;
            for (int column = 0; column < strip.Columns; column++)
            {
                int previous = strip.Loop ? (column + strip.Columns - 1) % strip.Columns : Mathf.Max(column - 1, 0);
                int next = strip.Loop ? (column + 1) % strip.Columns : Mathf.Min(column + 1, strip.Columns - 1);
                Vector3 outward = columns[first + column].Outward;
                for (int row = 0; row < strip.Rows; row++)
                {
                    Vector3 alongCliff = strip.Positions[strip.Index(next, row)] -
                                         strip.Positions[strip.Index(previous, row)];
                    Vector3 downFace = strip.Positions[strip.Index(column, Mathf.Min(row + 1, strip.Rows - 1))] -
                                       strip.Positions[strip.Index(column, Mathf.Max(row - 1, 0))];
                    Vector3 normal = Vector3.Cross(alongCliff, downFace);
                    facing += Vector3.Dot(normal, outward) + normal.y;
                    strip.Normals[strip.Index(column, row)] = normal;
                }
            }

            // Cross(along, down) is also the face direction of the grid triangles, so one sign fixes both.
            // Each column compares its normals with its own drop side. A curved strip has no common outward direction.
            bool flip = facing < 0f;
            strip.FlipWinding = flip;
            for (int index = 0; index < strip.VertexCount; index++)
            {
                Vector3 normal = strip.Normals[index];
                strip.Normals[index] = normal.sqrMagnitude > 1e-12f
                    ? (flip ? -normal : normal).normalized
                    : Vector3.up;
            }
        }

        #region Top
        // The rock top over the ground inside a closed spline that drops on its outer side. The top edge is the rim that
        // the face search found, so the top meets the cap of the wall. Behind the rim the top rises out of the ground
        // where the wall cap goes into it, so the two rock surfaces cross inside the rock and the world-space texture
        // runs on across them.
        internal static void BuildTop(Column[] columns, int columnCount, RockFaceSettings settings,
            RockFaceDetail detail, IReadOnlyList<RockFaceOpening> openings, IRockFaceGroundReader sampler,
            TerrainHeightSampler terrain, Workspace workspace)
        {
            // The rim line, with about one point per meter. A column without a face uses its spline point.
            int stride = Mathf.Max(1, Mathf.RoundToInt(1f / settings.ColumnSpacing));
            int rimCount = Mathf.Max(3, columnCount / stride);
            var rim = new Vector2[rimCount];
            Vector2 centre = Vector2.zero;
            int outward = 0;
            for (int index = 0; index < rimCount; index++)
            {
                Column column = columns[Mathf.Min(index * stride, columnCount - 1)];
                Vector3 point = column.Active ? column.Origin + column.Outward * column.Rim : column.Origin;
                rim[index] = new Vector2(point.x, point.z);
                centre += rim[index];
            }
            centre /= rimCount;
            for (int index = 0; index < rimCount; index++)
            {
                Column column = columns[Mathf.Min(index * stride, columnCount - 1)];
                if (Vector2.Dot(new Vector2(column.Outward.x, column.Outward.z), rim[index] - centre) > 0f) outward++;
            }
            // A pit drops on its inner side and gets no top.
            if (outward * 2 < rimCount) return;

            Vector2 minimum = rim[0];
            Vector2 maximum = rim[0];
            foreach (Vector2 point in rim)
            {
                minimum = Vector2.Min(minimum, point);
                maximum = Vector2.Max(maximum, point);
            }
            float spacing = settings.TopSpacing;
            int columnsX = Mathf.CeilToInt((maximum.x - minimum.x) / spacing) + 1;
            int rowsZ = Mathf.CeilToInt((maximum.y - minimum.y) / spacing) + 1;
            if (columnsX < 2 || rowsZ < 2) return;
            RockFaceStrip strip = workspace.TakeStrip(columnsX, rowsZ, false);
            strip.IsTop = true;
            // Grid triangles (c, r), (c + 1, r), (c, r + 1) face down. The top faces up.
            strip.FlipWinding = true;

            // The top rises from the ground between these distances behind the rim. The wall cap goes into the ground
            // over the same band.
            float riseStart = settings.CapLength * 0.25f;
            float riseEnd = settings.CapLength * 0.75f;
            // A block pavement starts at the rim, so it meets the raised block tops of the wall without a trough.
            if (settings.Blocks.Enabled)
            {
                riseStart = 0f;
                riseEnd = Mathf.Max(2f * settings.TopSpacing, settings.Blocks.Bevel);
            }
            float lowest = float.MaxValue;
            object lowestLock = new();
            Parallel.For(0, columnsX, column =>
            {
                float columnLowest = float.MaxValue;
                for (int row = 0; row < rowsZ; row++)
                {
                    float x = minimum.x + column * spacing;
                    float z = minimum.y + row * spacing;
                    int index = strip.Index(column, row);
                    float inside = SignedRimDistance(rim, x, z);
                    bool sampled = sampler.TrySampleHeight(x, z, out float ground);
                    // Later stamps can raise the ground of the top above the ground of the walls. The top lies on the
                    // higher of the two, so the terrain does not cover it.
                    if (sampled && terrain != null && terrain.TrySampleHeight(x, z, out float final))
                    {
                        ground = Mathf.Max(ground, final);
                    }
                    bool active = sampled && inside > riseStart - 2f * spacing;
                    strip.Active[index] = active;
                    strip.GroundOffsets[index] = Vector2.zero;
                    if (!active)
                    {
                        strip.Positions[index] = new Vector3(x, float.NaN, z);
                        continue;
                    }
                    float rise = Smooth01((inside - riseStart) / Mathf.Max(riseEnd - riseStart, 0.01f));
                    // Rock blocks give a pavement of tilted block tops with soil in the fissures.
                    float relief;
                    float body;
                    float crevice = 0f;
                    if (settings.Blocks.Enabled)
                    {
                        relief = RockFaceBlockCells.TopHeight(settings.Blocks, settings.Seed, x, z, out crevice);
                        body = 1f - crevice;
                    }
                    else
                    {
                        relief = CragHeight(x, z, inside, ground, settings, out body);
                    }
                    var vertex = new Vector3(x, ground + Mathf.Lerp(-settings.TuckDepth, settings.Clearance + relief, rise), z);
                    if (openings != null)
                    {
                        for (int opening = 0; opening < openings.Count; opening++)
                        {
                            vertex.y = Mathf.Min(vertex.y, openings[opening].HeightLimit(vertex.x, vertex.z));
                        }
                    }
                    strip.Positions[index] = vertex;
                    // The wall rules give the colour: strata layers by height, hard layers and joints.
                    detail.Evaluate(x * 0.83f + z * 0.56f, vertex.y, 1f, out RockFaceSample sample);
                    // Soil fills the low hollows. It reads the terrain ground of the top.
                    float soilHeight = Mathf.Max(settings.TopSoil * settings.CragHeight, 0.01f);
                    float soil = settings.TopSoil > 0f ? (1f - Smooth01(relief / soilHeight)) * rise : 0f;
                    if (settings.Blocks.Enabled)
                    {
                        soil = crevice * rise;
                    }
                    strip.Colors[index] = new Color(sample.Hardness, Mathf.Clamp01(sample.Cavity + 0.3f * (1f - body)),
                        sample.Layer, soil);
                    columnLowest = Mathf.Min(columnLowest, vertex.y);
                }
                lock (lowestLock)
                {
                    lowest = Mathf.Min(lowest, columnLowest);
                }
            });
            if (lowest == float.MaxValue)
            {
                strip.Reset(0, 0, false);
                workspace.Strips.Remove(strip);
                return;
            }
            // Grid points outside the top get no triangles. They stay low inside the grid area, so the mesh bounds
            // and the normals of the edge points stay sane.
            for (int index = 0; index < strip.VertexCount; index++)
            {
                if (strip.Active[index]) continue;
                Vector3 position = strip.Positions[index];
                strip.Positions[index] = new Vector3(position.x, lowest - settings.TuckDepth, position.z);
                strip.Colors[index] = new Color(0f, 0f, 0f, 0f);
            }
            ComputeTopNormals(strip);
        }

        // Distance from the rim line in XZ: positive inside the rim, negative outside.
        internal static float SignedRimDistance(Vector2[] rim, float x, float z)
        {
            var point = new Vector2(x, z);
            float nearest = float.MaxValue;
            bool inside = false;
            for (int index = 0, previous = rim.Length - 1; index < rim.Length; previous = index++)
            {
                Vector2 a = rim[previous];
                Vector2 b = rim[index];
                if ((b.y > z) != (a.y > z) && x < (a.x - b.x) * (z - b.y) / (a.y - b.y) + b.x) inside = !inside;
                Vector2 edge = a - b;
                float t = Mathf.Clamp01(Vector2.Dot(point - b, edge) / Mathf.Max(edge.sqrMagnitude, 1e-8f));
                nearest = Mathf.Min(nearest, (b + edge * t - point).sqrMagnitude);
            }
            float distance = Mathf.Sqrt(nearest);
            return inside ? distance : -distance;
        }

        // Crag height above the ground of the top. A warped noise decides where the crag bodies stand (the coverage). A
        // ridged multifractal gives each body its crests and peaks, and the sharpness narrows them. Small ridged noise
        // breaks the crests. Near the rim the crags are taller and cover more, so the walls rise on into them. Ledges
        // flatten the crags at the strata heights of the walls.
        private static float CragHeight(float x, float z, float inside, float ground, RockFaceSettings settings,
            out float body)
        {
            float scale = 1f / settings.CragSize;
            float u = x * scale;
            float v = z * scale;
            float warpU = u + 1.4f * (RockFaceNoise.Perlin(u * 0.45f + 31.7f, v * 0.45f + 3.1f) - 0.5f);
            float warpV = v + 1.4f * (RockFaceNoise.Perlin(u * 0.45f + 7.9f, v * 0.45f + 57.1f) - 0.5f);

            float edge = settings.CragEdgeRise * (1f - Smooth01(inside / settings.CragSize));
            float coverage = Mathf.Clamp01(settings.CragCoverage + 0.5f * edge);
            body = Smooth01((Fbm(warpU * 0.7f + 13.3f, warpV * 0.7f + 2.9f, 3) - (1f - coverage) * 0.8f - 0.1f) / 0.25f);

            // Two octaves keep the crags large: a few main peaks and their side crests on each top.
            float ridges = Ridged(warpU, warpV, 2);
            float peaks = Mathf.Pow(ridges, settings.CragSharpness);
            // Jagged crests: a second ridged noise at about a third of the crag size breaks the crest lines.
            float jagged = Ridged(warpU * 2.6f + 5.1f, warpV * 2.6f + 9.7f, 1);
            float height = body * (0.2f + 0.8f * peaks) * (1f + settings.CragJaggedness * 0.45f * (jagged - 0.4f));
            height *= settings.CragHeight * (1f + edge);

            // Ledges: flat treads at the strata step, with steep risers between them. The step is the mean strata
            // thickness, in world heights, so the ledges line up with the layers of the walls.
            float step = Mathf.Max(0.5f * (settings.StrataThickness.x + settings.StrataThickness.y), 0.25f);
            float level = (ground + height) / step;
            float floor = Mathf.Floor(level);
            float stepped = (floor + Smooth01((level - floor - 0.65f) / 0.35f)) * step - ground;
            height = Mathf.Lerp(height, Mathf.Max(stepped, 0f), settings.CragLedges * body);

            float roughness = settings.RoughnessDepth * (2f * RockFaceNoise.Perlin(x / settings.RoughnessScale + 7.3f,
                z / settings.RoughnessScale + 1.9f) - 1f);
            return Mathf.Max(0f, height + roughness * Mathf.Lerp(0.4f, 1f, body));
        }

        // Ridged multifractal from 0 to 1: sharp crests where the noise crosses its middle. Each octave grows where the
        // octave below it has a crest, so the crests branch and the valleys stay smooth.
        private static float Ridged(float x, float y, int octaves)
        {
            float sum = 0f;
            float total = 0f;
            float amplitude = 1f;
            float weight = 1f;
            for (int octave = 0; octave < octaves; octave++)
            {
                float signal = 1f - Mathf.Abs(2f * RockFaceNoise.Perlin(x, y) - 1f);
                signal *= signal * weight;
                weight = Mathf.Clamp01(signal * 2f);
                sum += signal * amplitude;
                total += amplitude;
                x = x * 2.07f + 19.3f;
                y = y * 2.07f + 7.1f;
                amplitude *= 0.5f;
            }
            return sum / total;
        }

        private static float Fbm(float x, float y, int octaves)
        {
            float sum = 0f;
            float amplitude = 0.5f;
            float total = 0f;
            for (int octave = 0; octave < octaves; octave++)
            {
                sum += RockFaceNoise.Perlin(x, y) * amplitude;
                total += amplitude;
                x = x * 2.03f + 17.1f;
                y = y * 2.03f + 4.7f;
                amplitude *= 0.5f;
            }
            return sum / total;
        }

        // Upward normals from the grid neighbours.
        private static void ComputeTopNormals(RockFaceStrip strip)
        {
            for (int column = 0; column < strip.Columns; column++)
            {
                int left = Mathf.Max(column - 1, 0);
                int right = Mathf.Min(column + 1, strip.Columns - 1);
                for (int row = 0; row < strip.Rows; row++)
                {
                    int down = Mathf.Max(row - 1, 0);
                    int up = Mathf.Min(row + 1, strip.Rows - 1);
                    Vector3 alongX = strip.Positions[strip.Index(right, row)] - strip.Positions[strip.Index(left, row)];
                    Vector3 alongZ = strip.Positions[strip.Index(column, up)] - strip.Positions[strip.Index(column, down)];
                    Vector3 normal = Vector3.Cross(alongZ, alongX);
                    strip.Normals[strip.Index(column, row)] = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up;
                }
            }
        }
        #endregion

        // Ground texture weight from the height above the final terrain, with noise along the cliff. The final terrain
        // includes later stamps such as a talus, so the blend follows the line where the ground buries the rock.
        // The height is measured to the lower of the ground under the vertex and the ground GroundProbe in front of
        // it. On a steep face the terrain under a vertex near the top is the steep terrain face just behind the rock,
        // almost as high as the rim, so without the front sample the top meters of the face took the ground texture.
        internal static float GroundWeight(Vector3 vertex, Vector3 outward, float u, RockFaceSettings settings,
            TerrainHeightSampler terrain)
        {
            if (settings.GroundBlendHeight <= 0f || !terrain.TrySampleHeight(vertex.x, vertex.z, out float ground)) return 0f;
            Vector3 front = vertex + outward * GroundProbe;
            if (terrain.TrySampleHeight(front.x, front.z, out float frontGround)) ground = Mathf.Min(ground, frontGround);
            float jitter = settings.GroundBlendNoise * settings.GroundBlendHeight *
                           (2f * RockFaceNoise.Perlin(u / 4f + 13.1f, vertex.y / 3f + 7.7f) - 1f);
            return 1f - Smooth01((vertex.y - ground - jitter) / settings.GroundBlendHeight);
        }

        // The rules are not periodic. Near the end of a loop the offset blends into the offset at its start.
        internal static float EvaluateDetail(RockFaceDetail detail, float u, float y, float structure,
            float loopLength, out RockFaceSample sample)
        {
            float offset = detail.Evaluate(u, y, structure, out sample);
            float blendStart = loopLength - LoopBlend;
            if (loopLength <= 0f || u <= blendStart) return offset;

            float wrapped = detail.Evaluate(u - loopLength, y, structure, out RockFaceSample wrappedSample);
            float weight = Smooth01((u - blendStart) / LoopBlend);
            sample.Hardness = Mathf.Lerp(sample.Hardness, wrappedSample.Hardness, weight);
            sample.Cavity = Mathf.Lerp(sample.Cavity, wrappedSample.Cavity, weight);
            if (weight > 0.5f) sample.Layer = wrappedSample.Layer;
            return Mathf.Lerp(offset, wrapped, weight);
        }

        // Pushes a visible vertex out along its normal until it is above the terrain at its own position.
        private static bool Conform(ref Vector3 vertex, Vector3 normal, float clearance, IRockFaceGroundReader sampler)
        {
            if (!sampler.TrySampleHeight(vertex.x, vertex.z, out float ground)) return false;
            float minimum = Mathf.Min(clearance, 0.1f);
            if (vertex.y >= ground + minimum) return false;
            for (int step = 0; step < CorrectionSteps && vertex.y < ground + minimum; step++)
            {
                vertex += normal * CorrectionStep;
                if (!sampler.TrySampleHeight(vertex.x, vertex.z, out ground)) break;
            }
            return true;
        }

        // Profile points are (distance from the spline, height), from the cap edge to the skirt edge.
        private static void ExtractProfile(Column column, RockFaceSettings settings, List<Vector2> profile)
        {
            float start = Mathf.Max(column.Rim - settings.CapLength, -settings.SearchInward);
            float end = Mathf.Min(column.Foot + settings.SkirtLength, settings.SearchOutward);
            profile.Clear();
            profile.Add(new Vector2(start, HeightAt(column, settings, start)));
            int first = Mathf.CeilToInt((start + settings.SearchInward) / settings.ProfileStep + 1e-4f);
            int last = Mathf.FloorToInt((end + settings.SearchInward) / settings.ProfileStep - 1e-4f);
            for (int sample = first; sample <= last; sample++)
            {
                profile.Add(new Vector2(-settings.SearchInward + sample * settings.ProfileStep, column.Heights[sample]));
            }
            profile.Add(new Vector2(end, HeightAt(column, settings, end)));
        }

        private static float HeightAt(Column column, RockFaceSettings settings, float distance)
        {
            float position = (distance + settings.SearchInward) / settings.ProfileStep;
            int sample = Mathf.Clamp(Mathf.FloorToInt(position), 0, column.Heights.Count - 2);
            return Mathf.Lerp(column.Heights[sample], column.Heights[sample + 1], position - sample);
        }

        private static void ArcLengths(List<Vector2> profile, List<float> arc)
        {
            arc.Clear();
            arc.Add(0f);
            for (int index = 1; index < profile.Count; index++)
            {
                arc.Add(arc[index - 1] + Vector2.Distance(profile[index], profile[index - 1]));
            }
        }

        private static Vector2 ProfileAt(List<Vector2> profile, List<float> arc, float s)
        {
            if (s <= 0f) return profile[0] + (profile[1] - profile[0]).normalized * s;
            if (s >= arc[^1]) return profile[^1] + (profile[^1] - profile[^2]).normalized * (s - arc[^1]);
            int low = 0;
            int high = arc.Count - 1;
            while (high - low > 1)
            {
                int middle = (low + high) / 2;
                if (arc[middle] <= s) low = middle;
                else high = middle;
            }
            float span = arc[high] - arc[low];
            return Vector2.Lerp(profile[low], profile[high], span > 1e-6f ? (s - arc[low]) / span : 0f);
        }

        internal static float Smooth01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }
        #endregion
    }

    // Reads world heights from the smallest active terrain that contains a point. The heights of the region are copied
    // once, so sampling is fast and safe on worker threads. A point outside the copied region has no height.
    // Reads the terrain heights of a region from the GPU heightmaps of the terrains. The values are the 16-bit heightmap
    // values, the same as the CPU heightmap after a synchronization, in native memory, so a build makes no managed
    // garbage for them. A terrain that changes only its CPU heightmap (SetHeightsDelayLOD before SyncHeightmap) has old
    // GPU heights until the synchronization.
    internal sealed class TerrainHeightSampler : IRockFaceGroundReader
    {
        private sealed class Tile
        {
            public Rect Rect;
            public float Height;
            public float HeightScale;
            public float CellX;
            public float CellZ;
            public int Width;
            public int Length;
            public NativeArray<ushort> Heights;
            public AsyncGPUReadbackRequest Request;
            public bool Pending;
        }

        // Value 32766 is the full terrain height. This is the conversion of TerrainData.GetHeights, bit for bit. The
        // table stores each float, so the JIT cannot keep a product at a higher precision.
        private static readonly float[] NormalizedHeights = CreateNormalizedHeights();

        private readonly List<Tile> _tiles = new();

        private bool _readFailed;

        // Starts the GPU reads of the heights. Call WaitForReads, or wait for IsReady, before the first sample.
        public TerrainHeightSampler(Rect region)
        {
            try
            {
                AddTiles(region);
            }
            catch
            {
                Dispose();
                throw;
            }
            // The smallest terrain wins, so a large background terrain under the tiles is not used.
            _tiles.Sort((a, b) => (a.CellX * a.CellZ).CompareTo(b.CellX * b.CellZ));
        }

        /// <summary>True when all GPU reads of the heights ended. Main thread.</summary>
        public bool IsReady
        {
            get
            {
                foreach (Tile tile in _tiles)
                {
                    if (tile.Pending) return false;
                }
                return true;
            }
        }

        /// <summary>Waits for the GPU reads of the heights. Throws when a read failed. Main thread.</summary>
        public void WaitForReads()
        {
            foreach (Tile tile in _tiles)
            {
                if (!tile.Pending) continue;
                tile.Request.WaitForCompletion();
                // The callback normally runs in WaitForCompletion. The status is still valid in this call.
                OnRead(tile, tile.Request);
            }
            if (_readFailed)
            {
                throw new System.InvalidOperationException("GPU readback of the terrain heights failed.");
            }
        }

        // The completion callback records the read, because a done request that is checked in a later frame reports an
        // error.
        private void OnRead(Tile tile, AsyncGPUReadbackRequest request)
        {
            if (!tile.Pending) return;
            tile.Pending = false;
            if (request.hasError) _readFailed = true;
        }

        private static float[] CreateNormalizedHeights()
        {
            float scale = (float)(1.0 / 32766.0);
            var heights = new float[ushort.MaxValue + 1];
            for (int value = 0; value < heights.Length; value++)
            {
                heights[value] = value * scale;
            }
            return heights;
        }

        // Starts the readback of each terrain crop in the region. The readbacks run together.
        private void AddTiles(Rect region)
        {
            foreach (UnityEngine.Terrain terrain in UnityEngine.Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null) continue;
                TerrainData data = terrain.terrainData;
                Vector3 position = terrain.transform.position;
                Vector3 size = data.size;
                Rect rect = new(position.x, position.z, size.x, size.z);
                if (!rect.Overlaps(region)) continue;

                int resolution = data.heightmapResolution;
                float cellX = size.x / (resolution - 1);
                float cellZ = size.z / (resolution - 1);
                int firstX = Mathf.Clamp(Mathf.FloorToInt((region.xMin - position.x) / cellX) - 1, 0, resolution - 1);
                int firstZ = Mathf.Clamp(Mathf.FloorToInt((region.yMin - position.z) / cellZ) - 1, 0, resolution - 1);
                int lastX = Mathf.Clamp(Mathf.CeilToInt((region.xMax - position.x) / cellX) + 1, 0, resolution - 1);
                int lastZ = Mathf.Clamp(Mathf.CeilToInt((region.yMax - position.z) / cellZ) + 1, 0, resolution - 1);
                if (lastX <= firstX || lastZ <= firstZ) continue;
                int width = lastX - firstX + 1;
                int length = lastZ - firstZ + 1;
                var tile = new Tile
                {
                    Rect = Rect.MinMaxRect(position.x + firstX * cellX, position.z + firstZ * cellZ,
                        position.x + lastX * cellX, position.z + lastZ * cellZ),
                    Height = position.y,
                    HeightScale = size.y,
                    CellX = cellX,
                    CellZ = cellZ,
                    Width = width,
                    Length = length,
                    Heights = new NativeArray<ushort>(width * length, Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory)
                };
                _tiles.Add(tile);
                RenderTexture texture = data.heightmapTexture;
                if (texture && SystemInfo.supportsAsyncGPUReadback)
                {
                    tile.Pending = true;
                    try
                    {
                        tile.Request = AsyncGPUReadback.RequestIntoNativeArray(ref tile.Heights, texture, 0, firstX,
                            width, firstZ, length, 0, 1, TextureFormat.R16, request => OnRead(tile, request));
                    }
                    catch
                    {
                        // Dispose must not wait for a read that did not start.
                        tile.Pending = false;
                        throw;
                    }
                }
                else
                {
                    // The CPU heights are exact multiples of 1 / 32766, so the rounding gives the heightmap values.
                    float[,] heights = data.GetHeights(firstX, firstZ, width, length);
                    for (int row = 0; row < length; row++)
                    {
                        for (int column = 0; column < width; column++)
                        {
                            tile.Heights[row * width + column] =
                                (ushort)Mathf.RoundToInt(heights[row, column] * 32766f);
                        }
                    }
                }
            }
        }

        public bool TrySampleHeight(float x, float z, out float height)
        {
            foreach (Tile tile in _tiles)
            {
                if (x < tile.Rect.xMin || z < tile.Rect.yMin || x > tile.Rect.xMax || z > tile.Rect.yMax) continue;
                float fx = (x - tile.Rect.xMin) / tile.CellX;
                float fz = (z - tile.Rect.yMin) / tile.CellZ;
                int width = tile.Width;
                int column = Mathf.Clamp((int)fx, 0, width - 2);
                int row = Mathf.Clamp((int)fz, 0, tile.Length - 2);
                float tx = Mathf.Clamp01(fx - column);
                float tz = Mathf.Clamp01(fz - row);
                int sample = row * width + column;
                NativeArray<ushort> heights = tile.Heights;
                float lower = Mathf.Lerp(NormalizedHeights[heights[sample]], NormalizedHeights[heights[sample + 1]],
                    tx);
                float upper = Mathf.Lerp(NormalizedHeights[heights[sample + width]],
                    NormalizedHeights[heights[sample + width + 1]], tx);
                height = tile.Height + Mathf.Lerp(lower, upper, tz) * tile.HeightScale;
                return true;
            }
            height = 0f;
            return false;
        }

        public void Dispose()
        {
            foreach (Tile tile in _tiles)
            {
                // A readback writes into the array until it completes.
                if (tile.Pending) tile.Request.WaitForCompletion();
                tile.Heights.Dispose();
            }
            _tiles.Clear();
        }
    }
}
