using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Shape of a cliff wall: the face profile, the buttresses along the wall and the rock facets on the face.
    /// </summary>
    [Serializable]
    public sealed class CliffWallShape
    {
        [Header("Profile")]
        [Range(-30f, 30f), Tooltip("Lean of the face from vertical, in degrees. Positive = the top hangs over the foot. " +
                                   "Negative = the face slopes back from the foot.")]
        public float Lean;
        [Min(0f), Tooltip("Depth of the notch at the foot of the face, in meters. The rock above the notch hangs over " +
                          "it.")]
        public float Undercut;
        [Range(0.05f, 0.8f), Tooltip("Height of the notch, as a share of the face height.")]
        public float UndercutHeight = 0.3f;
        [Tooltip("Outward bulge of the middle of the face, in meters. Negative = a hollow face.")]
        public float Bulge;
        [Min(0.2f), Tooltip("Radius of the rounded edge at the top of the face, in meters. A large radius gives a convex " +
                            "top edge where the ground rolls over into the face.")]
        public float BrowRadius = 2f;
        [Range(0f, 1f), Tooltip("Share of the fall of the ground past the terrain rim that the wall top keeps out to the " +
                               "face. 1 = the top follows the ground down to the rounded edge. Lower values make the " +
                               "buttresses grow in closer to the top. 0 = the top stays level out to the face and the " +
                               "face keeps its full relief up to the top, so the ground meets the face at a sharp corner.")]
        public float TopFall = 1f;
        [Tooltip("The top of the wall shows the ground behind the rim, so the terrain seems to run to the edge of the " +
                 "face. Off = the top shows rock.")]
        public bool GroundOnTop = true;
        [Range(0f, 1f), Tooltip("Share of the rounded edge, from the top, that shows the ground of a ground-covered top.")]
        public float BrowGround = 1f;
        [Min(0.5f), Tooltip("Distance behind the terrain rim where a ground-covered top or a slab top starts, in meters. " +
                            "Set it past the band of rock or steep-ground layers along the terrain rim: the top lies " +
                            "over that band and shows the layers of the land.")]
        public float TopBack = 3f;
        [Min(0f), Tooltip("Depth below the rounded edge to which the ground of a ground-covered top hangs down the face, " +
                          "in meters. The ground then rolls over the edge and the rock shows only below it.")]
        public float SoilDepth = 1.5f;
        [Range(0f, 1f), Tooltip("Change of the soil depth, and of the slab droop, along the wall. 0 = the same depth " +
                               "everywhere.")]
        public float SoilVariation = 0.6f;
        [Min(0.1f), Tooltip("Depth of the face foot below the ground in front of the face, in meters.")]
        public float FootDepth = 1f;

        [Header("Top")]
        [Tooltip("Ground: the top shows the ground of the land behind the rim. Slab: a solid slab like a countertop " +
                 "lies over the top. Its back edge goes down into the ground at the back angle, and its front stands " +
                 "out past the top of the face. With Ground On Top the slab shows the ground of the land, else rock.")]
        public CliffWallTop Top = CliffWallTop.Ground;
        [Min(0f), Tooltip("Slab top: height of the slab over the top line or the ground, in meters.")]
        public float CapRise = 0.15f;
        [Range(10f, 80f), Tooltip("Slab top: slope of the back edge where the ground meets the slab, in degrees.")]
        public float CapBackAngle = 30f;
        [Min(0f), Tooltip("Slab top: distance that the front of the slab stands out past the top of the face, in " +
                          "meters.")]
        public float CapLip = 0.1f;
        [Min(0.2f), Tooltip("Slab top: thickness of the slab at its front, in meters.")]
        public float CapThickness = 0.4f;
        [Min(0.05f), Tooltip("Slab top: radius of the bevel on the front edges, in meters.")]
        public float CapBevel = 0.1f;
        [Min(0f), Tooltip("Slab top: depth to which the front of the slab hangs down below its underside, in meters. The " +
                          "front rolls over in a larger round and the underside goes up from the hanging front to the " +
                          "face, so a ground-covered slab hangs over the face as a thick turf lip. Soil Variation changes " +
                          "it along the wall. 0 = a flat underside.")]
        public float CapDroop;
        [Range(0f, 1f), Tooltip("Slab top: share of the rock relief on the top of the slab. 0 = a flat top.")]
        public float CapRelief = 0.15f;
        [Min(0f), Tooltip("Rock slab top (Ground On Top off): width of the band over which the back of the slab takes " +
                          "the ground of the land, from the line where the ground meets the back edge, in meters. The " +
                          "band edge is ragged.")]
        public float CapBlend = 1.2f;

        [Header("Fractured Face (Prototype)")]
        public CliffWallFracture Fracture = new();

        [Header("Buttresses")]
        [Tooltip("Smallest and largest width of one buttress along the wall, in meters.")]
        public Vector2 ButtressWidth = new(6f, 18f);
        [Min(0f), Tooltip("Largest outward step of a buttress, in meters.")]
        public float ButtressDepth = 3f;
        [Range(0f, 40f), Tooltip("Largest turn of a buttress front away from the wall line, in degrees.")]
        public float ButtressTurn = 15f;
        [Range(0.5f, 4f), Tooltip("Steepness of the buttress sides, as outward meters for each meter along the wall.")]
        public float ButtressSides = 1.5f;
        [Range(0f, 1f), Tooltip("Change of the buttress depth from the foot to the top. 0 = the same depth.")]
        public float ButtressTaper = 0.5f;

        [Header("Facets")]
        [Min(0.5f), Tooltip("Typical size of one rock facet, in meters.")]
        public float FacetSize = 3.5f;
        [Min(0f), Tooltip("Largest outward step of a facet, in meters.")]
        public float FacetDepth = 0.9f;
        [Range(0f, 1f), Tooltip("Tilt of the facets. 0 = all facets are parallel to the wall.")]
        public float FacetTilt = 0.45f;
        [Min(0.01f), Tooltip("Rounding of the edges between facets, in meters.")]
        public float FacetRounding = 0.12f;

        [Header("Form Shade")]
        [Min(0f), Tooltip("Depth of a hollow below the smoothed face at which it gets the full cavity shade, in meters. " +
                          "The shade follows the large forms (recesses under ledges, between buttresses and facets), so " +
                          "it shows at a distance where the fine detail is too small to see. 0 = off.")]
        public float ShadeDepth;
        [Min(0.5f), Tooltip("Size of the smoothed face that the hollows are measured against, in meters. Larger values " +
                            "shade larger forms.")]
        public float ShadeRadius = 3f;

        internal CliffWallShape Copy()
        {
            return (CliffWallShape)MemberwiseClone();
        }
    }

    /// <summary>What covers the top of a cliff wall.</summary>
    public enum CliffWallTop
    {
        /// <summary>The top shows the ground of the land behind the rim (terrain surface mesh).</summary>
        Ground = 0,
        /// <summary>A rock slab lies over the top. The ground meets its back edge at the back angle.</summary>
        Slab = 1
    }

    /// <summary>Profile values of one spline knot. The values blend between knots.</summary>
    [Serializable]
    public struct CliffWallKnot
    {
        [Tooltip("Use the values of this knot. Off = the values of the wall shape.")]
        public bool Override;
        [Range(-30f, 30f), Tooltip("Lean of the face from vertical, in degrees. Positive = overhang.")]
        public float Lean;
        [Min(0f), Tooltip("Depth of the notch at the foot of the face, in meters.")]
        public float Undercut;
        [Tooltip("Outward bulge of the middle of the face, in meters.")]
        public float Bulge;
    }

    // Builds a cliff wall over the steep terrain next to a cliff-top spline. The face follows a designed profile that
    // can lean over its foot, with buttresses, facets and the layered rock rules of a rock face. The whole profile
    // stays in front of the terrain face, so the terrain does not show through the rock. The terrain face must be at
    // least as steep as the rock face, for example a carved face of 85 to 90 degrees under an overhang.
    internal static class CliffWallBuilder
    {
        private const float TangentReach = 1f;
        private const float DropSideReach = 6f;
        private const float BaseStep = 0.1f;
        // The rock stays this far from a cave, turns back into the terrain face over the next distance and ends this
        // far behind the face, in meters.
        private const float CaveClearance = 0.3f;
        private const float CaveTuck = 3f;
        private const float CaveTuckDepth = 0.5f;
        // Height range, above the tuck distance, of the face over an opening where the column above the opening changes
        // from the vertex fit to the column fit (BuildStrip), the distance beside the opening over which the column fit
        // fades out, and the distance in front of the terrain face where the column fit stops, in meters.
        private const float LintelBlend = 2f;
        private const float LintelReach = 5f;
        private const float LintelMargin = 0.5f;
        private const float BuriedMargin = 0.3f;
        private const float BrowStructure = 0.3f;
        // Height of the wall top over the ground behind the rim. Small, so the top meets the terrain without a step.
        private const float CapLift = 0.05f;
        // Largest slab droop, as a share of the face height.
        private const float MaximumDroop = 0.3f;
        // Steepest fall of the wall top past the rim, as height per meter.
        private const float MaximumFall = 1.2f;
        // Steepest terrain slope that the top follows, as height per meter (35 degrees). Where the terrain is steeper,
        // the top goes on in a straight line and rolls into the face.
        private const float FollowSlope = 0.7f;
        // Steepest terrain slope that a level top (TopFall 0) follows (8.5 degrees). The top goes level from where the
        // terrain starts to roll over, so the rounded terrain rim is under the top and the corner stays sharp.
        private const float LevelFollowSlope = 0.15f;
        // Half the distance over which the terrain slope is measured for the roll point, in meters.
        private const float RollReach = 0.25f;
        // Share of the face height below the top over which the buttresses grow to their full depth.
        private const float ButtressGrowth = 0.3f;
        // Height above the floor below which the terrain may cover the rock foot, in meters.
        private const float FootAllowance = 1.5f;
        // Share of the face relief that the soil on the upper face hides, and the height of the soil's lower edge.
        private const float SoilRelief = 0.8f;
        private const float SoilEdge = 0.5f;
        // Height over which the face relief comes back below the soil, in meters.
        private const float SoilReliefEdge = 3f;
        // Height of the top over the terrain surface, in meters. The top lies over the terrain from its start (Top
        // Back) to the rim, so it hides the rock band and the lip along the terrain rim, and the coarse levels of detail
        // of the terrain near the cliff, whose chords cut under the true ground.
        private const float CoverLift = 0.03f;
        // Length over which the top fades in from its start with a ragged edge, in meters. There it has the normal and
        // the layers of the terrain under it, so the top and the terrain look the same through the gaps.
        private const float CoverFade = 1.5f;
        // Length over which the shadow of the top starts at the roll point, in meters.
        private const float CastFade = 0.5f;
        // Normal height range over which a steep top changes from the terrain normal to its own normal (TerrainShade).
        private const float SteepTopNormal = 0.4f;
        private const float SteepTopRange = 0.3f;
        // The top reads the land at most this far behind its start, in meters. On a narrow bench a longer mirror read
        // the steep foot of the next face behind it and showed dark cliff rock on the bench.
        private const float MirrorReach = 2f;
        // Depth of the hidden top behind its start, in meters.
        private const float HiddenDepth = 0.3f;
        // Extra height of the top over the terrain in front of its fade, in meters. With less,
        // a sharp terrain lip, and concave places where the coarse terrain levels of detail lie over the true ground,
        // rose through the top as dark slivers.
        private const float LipMargin = 0.15f;
        // Longest run of a slab back edge into the ground, in meters.
        private const float MaximumBackRun = 20f;
        // Depth of the top of a fractured face below the ground-covered slab top, in meters.
        private const float SlabSeat = 0.03f;
        // Distance along the wall over which the front of a slab follows the relief of the face top, in meters.
        private const float SlabReliefSmoothing = 2f;

        // The wall top: it follows the terrain from its start (Top Back) to the roll point, and from there goes on in a
        // straight line to the rounded edge.
        private struct TopLine
        {
            public float StartX;
            public float RollX;
            public float RollY;
            public float Slope;
            // Slab top only: outward distance of the front, height of the top at the front, and index of the last
            // profile point of the slab (its underside at the face).
            public float FrontX;
            public float FrontTop;
            public int SlabEnd;

            public float Height(float x) => RollY + Slope * (x - RollX);
        }

        // One mesh column: where the wall is, the terrain face that it covers and its own profile values.
        private struct WallColumn
        {
            public float Lean;
            public float Undercut;
            public float Bulge;
            public bool Active;
            // A cave comes near the face of the column, so its rock fits around the opening vertex by vertex.
            public bool NearCave;
            // Smallest distance from the face to a cave, and the height of the highest face sample at that distance.
            public float CaveReach;
            public float CaveTop;
            // Outward distance of the terrain rim and the terrain foot from the column origin.
            public float RimX;
            public float FootX;
            public float RimY;
            public float FloorY;
            // Outward distance of the wall face line (the profile with no lean, notch or bulge).
            public float WallX;
            // Distance behind the rim where the top stops to follow the terrain, and the slope of the top line there,
            // averaged along the wall (FindTopLine).
            public float RollBack;
            public float RollSlope;
            // Slab top only: depth to which the front of the slab hangs down below its underside (BuildStrip).
            public float Droop;
        }

        /// <param name="points">World points along the cliff-top spline, about every 0.25 m.</param>
        /// <param name="shapes">Profile values at each point.</param>
        /// <param name="caveDistance">Optional. Distance in meters from a world point to a cave, negative inside it.
        /// The rock stays out of a cave opening and turns back into the terrain face around it. A column whose whole face
        /// is in a cave gets no rock. Called from worker threads.</param>
        public static List<RockFaceStrip> Build(Vector3[] points, CliffWallKnot[] shapes, bool closed,
            RockFaceSettings settings, CliffWallShape shape, TerrainHeightSampler terrain,
            RockFaceBuilder.Workspace workspace, Func<Vector3, float> caveDistance = null,
            List<Mesh[]> fractureMeshes = null)
        {
            List<RockFaceStrip> strips = workspace.Strips;
            strips.Clear();
            int pointCount = points.Length;
            if (pointCount < 2) return strips;

            var distances = new float[pointCount + 1];
            int segments = closed ? pointCount : pointCount - 1;
            for (int index = 0; index < segments; index++)
            {
                Vector3 a = points[index];
                Vector3 b = points[(index + 1) % pointCount];
                distances[index + 1] = distances[index] + new Vector2(b.x - a.x, b.z - a.z).magnitude;
            }
            float length = distances[segments];
            if (length < 1f) return strips;
            float loopLength = closed ? length : 0f;
            // Wall distance range of the fractured face, empty when it is off.
            Vector2 fractureRange = fractureMeshes != null && shape.Fracture != null && shape.Fracture.Enabled
                ? shape.Fracture.Range * length
                : Vector2.zero;
            int count = Mathf.Max(4, Mathf.RoundToInt(length / settings.ColumnSpacing)) + (closed ? 0 : 1);
            float spacing = length / (closed ? count : count - 1);

            var columns = new RockFaceBuilder.Column[count];
            var walls = new WallColumn[count];
            int cursor = 0;
            for (int index = 0; index < count; index++)
            {
                float u = index * spacing;
                Vector3 origin = PointAt(points, distances, segments, u, ref cursor, out int segment, out float share);
                Vector3 tangent = PointAt(points, distances, segments, Along(u + TangentReach, length, closed)) -
                                  PointAt(points, distances, segments, Along(u - TangentReach, length, closed));
                tangent.y = 0f;
                tangent = tangent.sqrMagnitude > 1e-6f ? tangent.normalized : Vector3.forward;
                columns[index] = new RockFaceBuilder.Column
                {
                    // The profile gives absolute heights, so the origin is at height 0.
                    Origin = new Vector3(origin.x, 0f, origin.z),
                    Outward = new Vector3(tangent.z, 0f, -tangent.x),
                    U = u,
                    Active = true
                };
                CliffWallKnot a = shapes[segment];
                CliffWallKnot b = shapes[(segment + 1) % pointCount];
                walls[index].Lean = Mathf.Lerp(a.Lean, b.Lean, share);
                walls[index].Undercut = Mathf.Lerp(a.Undercut, b.Undercut, share);
                walls[index].Bulge = Mathf.Lerp(a.Bulge, b.Bulge, share);
                walls[index].RimY = origin.y;
            }
            ChooseDropSide(columns, settings, terrain);

            // The terrain face of each column: the tallest steep run of the ground profile near the spline.
            int profileCount = Mathf.CeilToInt((settings.SearchInward + settings.SearchOutward) / settings.ProfileStep) + 1;
            var profiles = new float[count][];
            Parallel.For(0, count, index =>
            {
                // Outside the terrain the profile keeps the spline height.
                profiles[index] = SampleProfile(columns[index], walls[index].RimY, settings, profileCount, terrain);
                FindFace(profiles[index], settings, ref walls[index]);
                // An excluded part of the spline builds no rock, for example where another wall covers the face.
                if (RockFaceBuilder.IsExcluded(columns[index].U / length, settings)) walls[index].Active = false;
                if (caveDistance != null && walls[index].Active) CaveColumn(columns[index], ref walls[index], caveDistance);
            });
            SmoothFace(walls, settings, spacing, closed);
            SmoothTopLine(walls, profiles, settings, shape.TopBack,
                Mathf.Lerp(LevelFollowSlope, FollowSlope, shape.TopFall), spacing, closed);

            // The wall line: far enough out that no point of the profile is behind the terrain face.
            float browRadius = shape.BrowRadius;
            var required = new float[count];
            Parallel.For(0, count, index =>
            {
                if (!walls[index].Active) return;
                required[index] = RequiredWallX(walls[index], profiles[index], settings, shape);
            });
            SpreadWallLine(walls, required, settings, spacing, closed);

            // A closed wall with a gap starts its strips at a column without a face, so a run of rock over the start of
            // the loop is one strip. As two strips, both ends tucked their rock into the ground there and left a gap in
            // the wall. The columns that wrap round keep counting past the loop length, so the rock detail goes on
            // without a step.
            bool allActive = Array.TrueForAll(walls, wall => wall.Active);
            if (closed && !allActive && walls[0].Active && walls[count - 1].Active)
            {
                int shift = Array.FindIndex(walls, wall => !wall.Active);
                Rotate(columns, shift);
                Rotate(walls, shift);
                Rotate(profiles, shift);
                for (int index = count - shift; index < count; index++) columns[index].U += length;
            }

            var buttresses = new ButtressRow(shape, settings.Seed, length, closed);
            float yMinimum = float.MaxValue;
            float yMaximum = float.MinValue;
            foreach (WallColumn wall in walls)
            {
                if (!wall.Active) continue;
                yMinimum = Mathf.Min(yMinimum, wall.FloorY - shape.FootDepth);
                yMaximum = Mathf.Max(yMaximum, wall.RimY + CapLift);
            }
            if (yMinimum > yMaximum) return strips;
            var detail = new RockFaceDetail(settings, 0f, Mathf.Max(length, columns[count - 1].U), yMinimum, yMaximum);

            // One strip for each run of columns with a face. A run shorter than its two end fades gets no rock.
            int start = -1;
            int shortest = Mathf.CeilToInt(Mathf.Max(2f * settings.EndFade, 4f) / spacing);
            for (int index = 0; index <= count; index++)
            {
                bool active = index < count && walls[index].Active;
                if (active && start < 0) start = index;
                if (active || start < 0) continue;
                if (index - start < shortest && !(closed && allActive))
                {
                    start = -1;
                    continue;
                }
                BuildStrip(columns, walls, profiles, start, index - 1, closed && allActive ? loopLength : 0f, settings,
                    shape, detail, buttresses, terrain, workspace, caveDistance, fractureRange, fractureMeshes);
                start = -1;
            }
            return strips;
        }

        #region Columns
        // Tests the column face every meter from the foot to the rim. A column whose face is in a cave at almost every
        // point gets no rock. A column whose face comes near a cave fits its rock around the opening (BuildStrip).
        private static void CaveColumn(in RockFaceBuilder.Column column, ref WallColumn wall,
            Func<Vector3, float> caveDistance)
        {
            int count = Mathf.Max(2, Mathf.CeilToInt(wall.RimY - wall.FloorY) + 1);
            int inCave = 0;
            wall.CaveReach = float.MaxValue;
            wall.CaveTop = float.MaxValue;
            for (int sample = 0; sample < count; sample++)
            {
                float y = Mathf.Lerp(wall.FloorY, wall.RimY, sample / (count - 1f));
                float distance = caveDistance(FacePoint(column, wall, y));
                if (distance < CaveClearance) inCave++;
                if (distance < CaveClearance + Mathf.Max(CaveTuck, LintelReach) + 1f) wall.NearCave = true;
                // All samples in the opening count as the same distance, so the top is the top of the opening.
                float reach = Mathf.Max(distance, CaveClearance);
                if (reach <= wall.CaveReach + 0.25f)
                {
                    wall.CaveReach = Mathf.Min(wall.CaveReach, reach);
                    wall.CaveTop = y;
                }
            }
            if (inCave >= count * 0.95f) wall.Active = false;
        }

        // The terrain face behind a column at a height: a line from the foot to the rim.
        private static Vector3 FacePoint(in RockFaceBuilder.Column column, in WallColumn wall, float y)
        {
            float share = Mathf.Clamp01((y - wall.FloorY) / Mathf.Max(wall.RimY - wall.FloorY, 0.1f));
            return column.Origin + column.Outward * Mathf.Lerp(wall.FootX, wall.RimX, share) + Vector3.up * y;
        }

        // Moves the items so the item at the shift comes first. The items before it go to the end.
        private static void Rotate<T>(T[] items, int shift)
        {
            var copy = (T[])items.Clone();
            for (int index = 0; index < items.Length; index++)
            {
                items[index] = copy[(index + shift) % items.Length];
            }
        }

        private static float Along(float u, float length, bool closed)
        {
            return closed ? Mathf.Repeat(u, length) : Mathf.Clamp(u, 0f, length);
        }

        private static Vector3 PointAt(Vector3[] points, float[] distances, int segments, float distance)
        {
            int cursor = 0;
            return PointAt(points, distances, segments, distance, ref cursor, out _, out _);
        }

        private static Vector3 PointAt(Vector3[] points, float[] distances, int segments, float distance,
            ref int cursor, out int segment, out float share)
        {
            while (cursor < segments - 1 && distances[cursor + 1] < distance) cursor++;
            float span = distances[cursor + 1] - distances[cursor];
            share = span > 1e-6f ? Mathf.Clamp01((distance - distances[cursor]) / span) : 0f;
            segment = cursor;
            return Vector3.Lerp(points[cursor], points[(cursor + 1) % points.Length], share);
        }

        // The outward direction of all columns points to the lower side of the spline.
        private static void ChooseDropSide(RockFaceBuilder.Column[] columns, RockFaceSettings settings,
            TerrainHeightSampler terrain)
        {
            float vote = 0f;
            if (settings.DropSide == RockFaceDropSide.Auto)
            {
                foreach (RockFaceBuilder.Column column in columns)
                {
                    Vector3 right = column.Origin + column.Outward * DropSideReach;
                    Vector3 left = column.Origin - column.Outward * DropSideReach;
                    if (terrain.TrySampleHeight(right.x, right.z, out float rightHeight) &&
                        terrain.TrySampleHeight(left.x, left.z, out float leftHeight))
                    {
                        vote += leftHeight - rightHeight;
                    }
                }
            }
            bool dropLeft = settings.DropSide == RockFaceDropSide.Left ||
                            settings.DropSide == RockFaceDropSide.Auto && vote < 0f;
            if (!dropLeft) return;
            for (int index = 0; index < columns.Length; index++)
            {
                columns[index].Outward = -columns[index].Outward;
            }
        }

        // Ground heights across the cliff, from the search start behind the spline to the search end in front of it.
        private static float[] SampleProfile(RockFaceBuilder.Column column, float fallback, RockFaceSettings settings,
            int count, TerrainHeightSampler terrain)
        {
            var heights = new float[count];
            float last = fallback;
            for (int index = 0; index < count; index++)
            {
                Vector3 point = column.Origin + column.Outward * ProfileX(index, settings);
                if (terrain.TrySampleHeight(point.x, point.z, out float height)) last = height;
                heights[index] = last;
            }
            return heights;
        }

        private static float ProfileX(int index, RockFaceSettings settings)
        {
            return index * settings.ProfileStep - settings.SearchInward;
        }

        // The tallest run of steep ground that starts near the spline and is at least the minimum face height. A flatter
        // bench shorter than the bench tolerance does not end a run. A lower run, such as a small step behind the rim or
        // the toe of a bank on the land behind it, is skipped, so the wall takes the face after it. The first run that
        // was tall enough took a 4 m bank toe behind a 26 m quarry face and left the face bare.
        private static void FindFace(float[] heights, RockFaceSettings settings, ref WallColumn wall)
        {
            float steep = Mathf.Tan(settings.FaceAngle * Mathf.Deg2Rad) * settings.ProfileStep;
            int gapLimit = Mathf.Max(1, Mathf.RoundToInt(settings.BenchTolerance / settings.ProfileStep));
            wall.Active = false;
            int start = 0;
            while (start < heights.Length - 1)
            {
                int first = -1;
                int last = -1;
                int gap = 0;
                for (int index = start; index < heights.Length - 1; index++)
                {
                    bool isSteep = heights[index] - heights[index + 1] > steep;
                    if (first < 0)
                    {
                        if (isSteep) first = last = index;
                        continue;
                    }
                    if (isSteep)
                    {
                        last = index;
                        gap = 0;
                    }
                    else if (++gap > gapLimit)
                    {
                        break;
                    }
                }
                // The face must start near the spline, so a wall does not take the face of a lower bench.
                if (first < 0 || ProfileX(first, settings) > settings.SearchInward) return;
                float rimY = heights[first];
                float floorY = heights[last + 1];
                if (rimY - floorY >= settings.MinimumFaceHeight &&
                    (!wall.Active || rimY - floorY > wall.RimY - wall.FloorY))
                {
                    wall.Active = true;
                    wall.RimX = ProfileX(first, settings);
                    wall.FootX = ProfileX(last + 1, settings);
                    wall.RimY = rimY;
                    wall.FloorY = floorY;
                }
                start = last + 1;
            }
        }

        // Averages the rim and the floor along the wall, so single terrain texels do not show in the rock.
        private static void SmoothFace(WallColumn[] walls, RockFaceSettings settings, float spacing, bool closed)
        {
            int window = Mathf.RoundToInt(settings.ProfileSmoothing / spacing);
            if (window < 1) return;
            int count = walls.Length;
            var rimX = new float[count];
            var rimY = new float[count];
            var floorY = new float[count];
            for (int index = 0; index < count; index++)
            {
                if (!walls[index].Active) continue;
                float sumX = 0f;
                float sumRim = 0f;
                float sumFloor = 0f;
                int used = 0;
                for (int offset = -window; offset <= window; offset++)
                {
                    int other = Neighbour(index, offset, count, closed);
                    if (other < 0 || !walls[other].Active) continue;
                    sumX += walls[other].RimX;
                    sumRim += walls[other].RimY;
                    sumFloor += walls[other].FloorY;
                    used++;
                }
                rimX[index] = sumX / used;
                rimY[index] = sumRim / used;
                floorY[index] = sumFloor / used;
            }
            for (int index = 0; index < count; index++)
            {
                if (!walls[index].Active) continue;
                walls[index].RimX = rimX[index];
                walls[index].RimY = rimY[index];
                walls[index].FloorY = floorY[index];
            }
        }

        private static int Neighbour(int index, int offset, int count, bool closed)
        {
            int other = index + offset;
            if (closed) return (other % count + count) % count;
            return other < 0 || other >= count ? -1 : other;
        }
        #endregion

        #region Profile
        // Outward offset of the designed profile from the wall line at a height: lean, notch and bulge. The rounded edge
        // at the top is not included.
        private static float ProfileOffset(in WallColumn wall, CliffWallShape shape, float y)
        {
            float faceHeight = Mathf.Max(wall.RimY - wall.FloorY, 0.1f);
            float depth = Mathf.Clamp01((wall.RimY - y) / faceHeight);
            float offset = -Mathf.Tan(wall.Lean * Mathf.Deg2Rad) * (wall.RimY - y);
            // A rounded notch above the foot, like a wave-cut notch.
            float notchHeight = Mathf.Max(shape.UndercutHeight, 0.05f);
            float notchCentre = 1f - 0.45f * notchHeight;
            float notch = (depth - notchCentre) / (0.45f * notchHeight);
            offset -= wall.Undercut * Mathf.Exp(-notch * notch);
            offset += wall.Bulge * Mathf.Sin(Mathf.PI * depth);
            return offset;
        }

        // The base profile of one column as world-relative (outward, height) points from the inner end of the cap over
        // the rounded edge down to the buried foot. Structure is 1 on the face and less on the edge and the cap.
        private static void BaseProfile(in WallColumn wall, float[] heights, float buttress, RockFaceSettings settings,
            CliffWallShape shape, List<Vector3> result, out TopLine line)
        {
            result.Clear();
            line = FindTopLine(wall, heights, settings, shape.TopBack, shape.TopFall);
            if (shape.Top == CliffWallTop.Slab)
            {
                SlabProfile(wall, heights, buttress, settings, shape, result, ref line);
                return;
            }
            float top = line.RollY;
            float radius = Mathf.Min(shape.BrowRadius, 0.45f * (wall.RimY - wall.FloorY));
            // The wall top keeps the fall of the ground past the rim, and the rounded edge starts in its direction, so
            // the edge is convex all the way and has no flat step.
            float startAngle = Mathf.Atan(-line.Slope);
            float faceTopX = wall.WallX + ProfileOffset(wall, shape, top - radius) + buttress;
            float capEnd = faceTopX - radius * (1f - Mathf.Sin(startAngle));
            float capEndY = CapHeight(line, heights, settings, capEnd);
            float faceTop = capEndY - radius * Mathf.Cos(startAngle);
            // The cap reaches back past its start.
            float capStart = Mathf.Min(wall.RimX, capEnd) - Mathf.Max(settings.CapLength, shape.TopBack + 1f);

            // The cap lies on the top line over the terrain, and under the ground behind its start. It has no deep tuck
            // at its inner end: a coarse level of detail joined a drawn top vertex to a tucked one and showed a steep
            // wall there.
            int capSteps = Mathf.Max(2, Mathf.CeilToInt((capEnd - capStart) / BaseStep));
            for (int step = 0; step <= capSteps; step++)
            {
                float x = Mathf.Lerp(capStart, capEnd, step / (float)capSteps);
                result.Add(new Vector3(x, CapHeight(line, heights, settings, x), BrowStructure));
            }
            // The rounded edge: an arc from the direction of the cap down to the face.
            float centreX = capEnd - radius * Mathf.Sin(startAngle);
            float centreY = capEndY - radius * Mathf.Cos(startAngle);
            int browSteps = Mathf.Max(3, Mathf.CeilToInt((0.5f * Mathf.PI - startAngle) * radius / BaseStep));
            for (int step = 1; step <= browSteps; step++)
            {
                float share = step / (float)browSteps;
                float angle = Mathf.Lerp(startAngle, 0.5f * Mathf.PI, share);
                float x = centreX + radius * Mathf.Sin(angle);
                float y = centreY + radius * Mathf.Cos(angle);
                // The edge stays over a terrain lip at the rim.
                if (x <= wall.RimX) y = Mathf.Max(y, GroundAt(heights, settings, x) + CoverLift + LipMargin);
                result.Add(new Vector3(x, y, Mathf.Lerp(BrowStructure, 1f, share)));
            }
            // The face down to the buried foot.
            float bottom = wall.FloorY - shape.FootDepth;
            int faceSteps = Mathf.Max(2, Mathf.CeilToInt((faceTop - bottom) / BaseStep));
            for (int step = 1; step <= faceSteps; step++)
            {
                float y = Mathf.Lerp(faceTop, bottom, step / (float)faceSteps);
                float x = wall.WallX + ProfileOffset(wall, shape, y) + buttress;
                result.Add(new Vector3(x, y, 1f));
            }
        }

        // A slab top: a rock slab like a countertop over the top of the wall. Its back edge goes down into the ground at
        // the back angle, so the ground meets the slab at that angle and not at a step. The top lies the rise over the
        // top line, the front stands out past the face top by the lip with bevelled edges, and the flat underside goes
        // back to the face.
        private static void SlabProfile(in WallColumn wall, float[] heights, float buttress, RockFaceSettings settings,
            CliffWallShape shape, List<Vector3> result, ref TopLine line)
        {
            float start = line.StartX;
            float thickness = Mathf.Min(shape.CapThickness, 0.45f * (wall.RimY - wall.FloorY));
            float bevel = Mathf.Min(shape.CapBevel, 0.45f * thickness);
            float droop = Mathf.Min(wall.Droop, MaximumDroop * (wall.RimY - wall.FloorY));
            // A drooping front rolls over in a larger round, down to the bevel above the hanging tip.
            float roll = Mathf.Clamp(bevel + droop, bevel, thickness + droop - bevel);
            float startTop = SlabTop(line, heights, settings, shape, start);
            float faceTopX = wall.WallX + ProfileOffset(wall, shape, startTop - thickness) + buttress;
            float frontX = Mathf.Max(faceTopX + shape.CapLip, start + bevel + roll + BaseStep);

            // The back edge, from its end below the ground up to the top.
            float slope = Mathf.Tan(shape.CapBackAngle * Mathf.Deg2Rad);
            float run = 0f;
            while (run < MaximumBackRun &&
                   startTop - run * slope > GroundAt(heights, settings, start - run) - HiddenDepth)
            {
                run += BaseStep;
            }
            int backSteps = Mathf.Max(1, Mathf.CeilToInt(run / BaseStep));
            for (int step = 0; step < backSteps; step++)
            {
                float back = run * (1f - step / (float)backSteps);
                result.Add(new Vector3(start - back, startTop - back * slope, BrowStructure));
            }
            // The top out to the front round.
            float topEnd = frontX - roll;
            int topSteps = Mathf.Max(2, Mathf.CeilToInt((topEnd - start) / BaseStep));
            for (int step = 0; step <= topSteps; step++)
            {
                float x = Mathf.Lerp(start, topEnd, step / (float)topSteps);
                result.Add(new Vector3(x, SlabTop(line, heights, settings, shape, x), BrowStructure));
            }
            // The upper round turns from the top to the front, and the lower bevel from the front to the underside.
            // The front hangs down by the droop below the underside at the face.
            float frontTop = SlabTop(line, heights, settings, shape, topEnd);
            float bottomY = frontTop - thickness;
            float tipY = bottomY - droop;
            int rollSteps = Mathf.Max(3, Mathf.CeilToInt(0.5f * Mathf.PI * roll / BaseStep));
            for (int step = 1; step <= rollSteps; step++)
            {
                float share = step / (float)rollSteps;
                float angle = 0.5f * Mathf.PI * share;
                result.Add(new Vector3(topEnd + roll * Mathf.Sin(angle), frontTop - roll + roll * Mathf.Cos(angle),
                    Mathf.Lerp(BrowStructure, 1f, share)));
            }
            int frontSteps = Mathf.Max(1, Mathf.CeilToInt((frontTop - roll - tipY - bevel) / BaseStep));
            for (int step = 1; step <= frontSteps; step++)
            {
                result.Add(new Vector3(frontX, Mathf.Lerp(frontTop - roll, tipY + bevel, step / (float)frontSteps),
                    1f));
            }
            int bevelSteps = Mathf.Max(3, Mathf.CeilToInt(0.5f * Mathf.PI * bevel / BaseStep));
            float underStart = frontX - bevel;
            for (int step = 1; step <= bevelSteps; step++)
            {
                float angle = 0.5f * Mathf.PI * (1f + step / (float)bevelSteps);
                result.Add(new Vector3(underStart + bevel * Mathf.Sin(angle), tipY + bevel + bevel * Mathf.Cos(angle),
                    1f));
            }
            // The underside back to the face, up from the hanging tip.
            float faceX = wall.WallX + ProfileOffset(wall, shape, bottomY) + buttress;
            int underSteps = Mathf.CeilToInt(new Vector2(underStart - faceX, droop).magnitude / BaseStep);
            for (int step = 1; step <= underSteps; step++)
            {
                float share = step / (float)underSteps;
                result.Add(new Vector3(Mathf.Lerp(underStart, faceX, share), Mathf.Lerp(tipY, bottomY, share), 1f));
            }
            line.SlabEnd = result.Count - 1;
            // The face down to the buried foot.
            float bottom = wall.FloorY - shape.FootDepth;
            int faceSteps = Mathf.Max(2, Mathf.CeilToInt((bottomY - bottom) / BaseStep));
            for (int step = 1; step <= faceSteps; step++)
            {
                float y = Mathf.Lerp(bottomY, bottom, step / (float)faceSteps);
                result.Add(new Vector3(wall.WallX + ProfileOffset(wall, shape, y) + buttress, y, 1f));
            }
            line.FrontX = frontX;
            line.FrontTop = frontTop;
        }

        // Outward relief of the top face row under the front of a slab, for each column of a strip: the largest relief
        // within the smoothing distance along the wall, then averaged. The front then covers the face top below it
        // (with the buttresses only, the rough face jutted out up to a meter past it), and the relief and facets of
        // single columns do not make spikes on the slab edge.
        private static float[] SlabFrontRelief(RockFaceBuilder.Column[] columns, List<Vector3>[] bases, int first,
            float loopLength, RockFaceSettings settings, RockFaceDetail detail, FacetField facets)
        {
            int count = bases.Length;
            var relief = new float[count];
            for (int local = 0; local < count; local++)
            {
                float faceTop = 0f;
                foreach (Vector3 basePoint in bases[local])
                {
                    if (basePoint.z < 0.99f) continue;
                    faceTop = basePoint.y;
                    break;
                }
                float u = columns[first + local].U;
                relief[local] = RockFaceBuilder.EvaluateDetail(detail, u, faceTop, 1f, loopLength, out _) +
                                facets.Offset(u, faceTop, out _);
            }
            int window = Mathf.Max(1, Mathf.RoundToInt(SlabReliefSmoothing / settings.ColumnSpacing));
            var maxima = new float[count];
            for (int local = 0; local < count; local++)
            {
                float maximum = float.MinValue;
                for (int other = Mathf.Max(0, local - window); other <= Mathf.Min(count - 1, local + window); other++)
                {
                    maximum = Mathf.Max(maximum, relief[other]);
                }
                maxima[local] = maximum;
            }
            for (int local = 0; local < count; local++)
            {
                float sum = 0f;
                int used = 0;
                for (int other = Mathf.Max(0, local - window); other <= Mathf.Min(count - 1, local + window); other++)
                {
                    sum += maxima[other];
                    used++;
                }
                relief[local] = sum / used;
            }
            return relief;
        }

        // Height of a slab top: the rise over the top line, and at least the rise over the ground.
        private static float SlabTop(in TopLine line, float[] heights, RockFaceSettings settings, CliffWallShape shape,
            float x)
        {
            return Mathf.Max(line.Height(x), GroundAt(heights, settings, x) + CoverLift) + shape.CapRise;
        }

        // The top lies just over the terrain from its start (Top Back) to the roll point, where the terrain gets steeper
        // than the follow slope, or the rim. It hides the rock band and a lip along the rim and stays on the ground: a top on
        // a straight line from its start stood meters over a wide rounded rim, and the gap under it showed close up.
        // From the roll point it goes on in a straight line with the slope of the terrain there. The roll point and the
        // slope are averaged along the wall (SmoothTopLine).
        private static TopLine FindTopLine(in WallColumn wall, float[] heights, RockFaceSettings settings, float topBack,
            float topFall)
        {
            float start = wall.RimX - topBack;
            float rollX = Mathf.Max(start, wall.RimX - wall.RollBack);
            return new TopLine
            {
                StartX = start,
                RollX = rollX,
                RollY = GroundAt(heights, settings, rollX) + CoverHeight(start, rollX),
                Slope = wall.RollSlope * topFall
            };
        }

        private static void SmoothTopLine(WallColumn[] walls, float[][] profiles, RockFaceSettings settings,
            float topBack, float followSlope, float spacing, bool closed)
        {
            int count = walls.Length;
            var backs = new float[count];
            for (int index = 0; index < count; index++)
            {
                if (!walls[index].Active) continue;
                float[] heights = profiles[index];
                float rimX = walls[index].RimX;
                float rollX = rimX;
                for (float x = rimX - topBack + RollReach; x <= rimX; x += BaseStep)
                {
                    rollX = x;
                    float slope = (GroundAt(heights, settings, x + RollReach) -
                                   GroundAt(heights, settings, x - RollReach)) / (2f * RollReach);
                    if (slope < -followSlope) break;
                }
                backs[index] = rimX - rollX;
            }
            int window = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(settings.ProfileSmoothing, 1f) / spacing));
            for (int index = 0; index < count; index++)
            {
                if (!walls[index].Active) continue;
                float back = 0f;
                int used = 0;
                for (int offset = -window; offset <= window; offset++)
                {
                    int other = Neighbour(index, offset, count, closed);
                    if (other < 0 || !walls[other].Active) continue;
                    back += backs[other];
                    used++;
                }
                walls[index].RollBack = back / used;
            }
            // The slope of the terrain at the roll point, averaged along the wall: a slope of each column alone made
            // the rounded edges of next columns differ, and the edge showed spikes.
            var slopes = new float[count];
            for (int index = 0; index < count; index++)
            {
                if (!walls[index].Active) continue;
                float rollX = walls[index].RimX - walls[index].RollBack;
                slopes[index] = Mathf.Clamp((GroundAt(profiles[index], settings, rollX + RollReach) -
                                             GroundAt(profiles[index], settings, rollX - RollReach)) / (2f * RollReach),
                    -MaximumFall, MaximumFall);
            }
            for (int index = 0; index < count; index++)
            {
                if (!walls[index].Active) continue;
                float slope = 0f;
                int used = 0;
                for (int offset = -window; offset <= window; offset++)
                {
                    int other = Neighbour(index, offset, count, closed);
                    if (other < 0 || !walls[other].Active) continue;
                    slope += slopes[other];
                    used++;
                }
                walls[index].RollSlope = slope / used;
            }
        }

        // Height of the top over the terrain. The margin grows in after the fade: through the gaps of the ragged edge,
        // a top higher over the terrain showed the dark terrain under it.
        private static float CoverHeight(float startX, float x)
        {
            return CoverLift + LipMargin * RockFaceBuilder.Smooth01((x - startX - CoverFade) / CoverFade);
        }

        // Height of the wall top: over the terrain from its start to the roll point, then on the top line, never under
        // the terrain. Behind its start it lies under the ground, hidden.
        private static float CapHeight(in TopLine line, float[] heights, RockFaceSettings settings, float x)
        {
            float ground = GroundAt(heights, settings, x);
            if (x < line.StartX - 0.5f * BaseStep) return ground - HiddenDepth;
            float cover = ground + CoverHeight(line.StartX, x);
            if (x > line.RollX) return Mathf.Max(line.Height(x), cover);
            // Over a convex shoulder the top rises by about the sag of a straight span between its grid points, so the
            // terrain does not rise between them. Limited: next to the rim drop the bend of the terrain is meters.
            float bend = GroundAt(heights, settings, x + RollReach) + GroundAt(heights, settings, x - RollReach) -
                         2f * ground;
            return cover + Mathf.Clamp(-bend, 0f, LipMargin);
        }

        private static float GroundAt(float[] heights, RockFaceSettings settings, float x)
        {
            float position = (x + settings.SearchInward) / settings.ProfileStep;
            int index = Mathf.Clamp(Mathf.FloorToInt(position), 0, heights.Length - 2);
            return Mathf.Lerp(heights[index], heights[index + 1], Mathf.Clamp01(position - index));
        }

        // Outward distance of the terrain face at a height: the first point outward from the rim where the ground is
        // below the height.
        private static float TerrainFaceX(float[] heights, RockFaceSettings settings, in WallColumn wall, float y)
        {
            int first = Mathf.Max(0, Mathf.FloorToInt((wall.RimX + settings.SearchInward) / settings.ProfileStep) - 4);
            for (int index = first; index < heights.Length - 1; index++)
            {
                if (heights[index + 1] >= y) continue;
                if (heights[index] < y) return ProfileX(index, settings);
                float share = (heights[index] - y) / Mathf.Max(heights[index] - heights[index + 1], 1e-4f);
                return ProfileX(index, settings) + share * settings.ProfileStep;
            }
            return ProfileX(heights.Length - 1, settings);
        }

        // The wall line that keeps the face, the rounded edge and the cap edge in front of the terrain face.
        private static float RequiredWallX(WallColumn wall, float[] heights, RockFaceSettings settings,
            CliffWallShape shape)
        {
            wall.WallX = 0f;
            var profile = new List<Vector3>();
            BaseProfile(wall, heights, 0f, settings, shape, profile, out _);
            float required = float.MinValue;
            foreach (Vector3 point in profile)
            {
                // Only the rounded edge and the face must clear the terrain. The cap lies on the ground or under it
                // behind the rim.
                if (point.z < 0.99f && (point.y > wall.RimY - 0.01f || point.x <= wall.RimX)) continue;
                // The rounded toe of the terrain and small bumps of the floor reach far out at a low height. The rock
                // foot may stand in them: the ground blend covers it, and the wall stays on the terrain face.
                if (point.y < wall.FloorY + FootAllowance) continue;
                required = Mathf.Max(required, TerrainFaceX(heights, settings, wall, point.y) + settings.Clearance -
                                               point.x);
            }
            return required == float.MinValue ? wall.FootX : required;
        }

        // A running maximum and then a running average: the line is smooth and each column is at least as far out as
        // its own requirement.
        private static void SpreadWallLine(WallColumn[] walls, float[] required, RockFaceSettings settings,
            float spacing, bool closed)
        {
            int count = walls.Length;
            int window = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(settings.ProfileSmoothing, 1f) / spacing));
            var maxima = new float[count];
            for (int index = 0; index < count; index++)
            {
                if (!walls[index].Active) continue;
                float maximum = required[index];
                for (int offset = -window; offset <= window; offset++)
                {
                    int other = Neighbour(index, offset, count, closed);
                    if (other >= 0 && walls[other].Active) maximum = Mathf.Max(maximum, required[other]);
                }
                maxima[index] = maximum;
            }
            for (int index = 0; index < count; index++)
            {
                if (!walls[index].Active) continue;
                float sum = 0f;
                int used = 0;
                for (int offset = -window; offset <= window; offset++)
                {
                    int other = Neighbour(index, offset, count, closed);
                    if (other < 0 || !walls[other].Active) continue;
                    sum += maxima[other];
                    used++;
                }
                walls[index].WallX = sum / used;
            }
        }
        #endregion

        #region Mesh
        private static void BuildStrip(RockFaceBuilder.Column[] columns, WallColumn[] walls, float[][] profiles,
            int first, int last, float loopLength, RockFaceSettings settings, CliffWallShape shape,
            RockFaceDetail detail, ButtressRow buttresses, TerrainHeightSampler terrain,
            RockFaceBuilder.Workspace workspace, Func<Vector3, float> caveDistance, Vector2 fractureRange,
            List<Mesh[]> fractureMeshes)
        {
            int columnCount = last - first + 1;
            if (columnCount < 2) return;
            var bases = new List<Vector3>[columnCount];
            var arcs = new float[columnCount][];
            var lines = new TopLine[columnCount];
            float longest = 0f;
            Parallel.For(0, columnCount, local =>
            {
                int index = first + local;
                // The buttresses grow out below the top edge, so the edge stays on the wall line next to the terrain.
                bases[local] = new List<Vector3>();
                WallColumn wall = walls[index];
                if (shape.Top == CliffWallTop.Slab) wall.Droop = SlabDroop(shape, settings.Seed, columns[index].U);
                BaseProfile(wall, profiles[index], 0f, settings, shape, bases[local], out lines[local]);
                arcs[local] = ArcLengths(bases[local]);
            });
            foreach (float[] arc in arcs) longest = Mathf.Max(longest, arc[^1]);

            int rowCount = Mathf.Max(2, Mathf.CeilToInt(longest / settings.RowSpacing));
            RockFaceStrip strip = workspace.TakeStrip(columnCount, rowCount + 1, loopLength > 0f);
            // Rows of the face below a slab top, and their place without the rock relief. In the fractured range the
            // block shell replaces them and follows that place.
            var faceRows = new bool[strip.VertexCount];
            var faceBase = new Vector3[strip.VertexCount];
            strip.CullHidden = true;
            float buriedDepth = Mathf.Max(BuriedMargin, 0.5f * settings.TuckDepth);
            float stripStart = columns[first].U;
            float stripEnd = columns[last].U;
            var facets = new FacetField(shape, settings.Seed);
            float[] slabRelief = shape.Top == CliffWallTop.Slab
                ? SlabFrontRelief(columns, bases, first, loopLength, settings, detail, facets)
                : null;
            // Over a fractured face the blocks follow the face without the rock relief, so the slab front does too.
            // With the relief it stood out over the blocks, and the sky showed below it.
            if (slabRelief != null && fractureRange.y > fractureRange.x)
            {
                for (int local = 0; local < columnCount; local++)
                {
                    float u = columns[first + local].U;
                    if (u <= fractureRange.x || u >= fractureRange.y) continue;
                    slabRelief[local] *= 1f - FracturedFaceBuilder.EndWeight(u, fractureRange, shape.Fracture);
                }
            }
            // Share of the terrain normal in the normal of each vertex (TerrainShade).
            var terrainShade = new float[strip.VertexCount];
            Parallel.For(0, columnCount, local =>
            {
                int index = first + local;
                RockFaceBuilder.Column column = columns[index];
                WallColumn wall = walls[index];
                List<Vector3> profile = bases[local];
                float[] arc = arcs[local];
                float faceHeight = Mathf.Max(wall.RimY - wall.FloorY, 0.1f);
                float endWeight = loopLength > 0f || settings.EndFade <= 0f
                    ? 1f
                    : RockFaceBuilder.Smooth01(Mathf.Min(column.U - stripStart, stripEnd - column.U) / settings.EndFade);
                Vector3 floorGround = column.Origin + column.Outward * (wall.WallX + settings.GroundReach);
                // Height where the rounded edge meets the face.
                float faceTop = wall.FloorY;
                foreach (Vector3 basePoint in profile)
                {
                    if (basePoint.z >= 0.99f)
                    {
                        faceTop = basePoint.y;
                        break;
                    }
                }
                // Over an opening close below the top of the face, the rows above the opening are farther from the
                // cave than the rows next to it, so they turned back less and stood out over the opening as a ledge.
                // There all the rows above the opening go back as far as the row of the column nearest to the cave,
                // but stay in front of the terrain face: behind it, the terrain face showed as a panel with hard sides.
                float lintelPull = 0f;
                if (caveDistance != null && wall.NearCave)
                {
                    float lintelShare = 1f - RockFaceBuilder.Smooth01((faceTop - wall.CaveTop - CaveTuck) / LintelBlend);
                    lintelPull = lintelShare *
                                 (1f - RockFaceBuilder.Smooth01((wall.CaveReach - CaveClearance) / LintelReach));
                }
                // A level top (TopFall 0) meets the face at a sharp corner: the face has its full buttress and relief
                // up to the top, and the rounded edge moves out by the relief of the top face row.
                // A slab top moves out with the top row of the face like a level top.
                bool slab = shape.Top == CliffWallTop.Slab;
                bool levelTop = slab || shape.TopFall <= 0f;
                float topRelief = levelTop
                    ? RockFaceBuilder.EvaluateDetail(detail, column.U, faceTop, 1f, loopLength, out _) +
                      facets.Offset(column.U, faceTop, out _)
                    : 0f;
                for (int row = 0; row <= rowCount; row++)
                {
                    float s = arc[^1] * row / rowCount;
                    Vector3 point = ProfileAt(profile, arc, s);
                    Vector3 ahead = ProfileAt(profile, arc, s + 0.4f);
                    Vector3 behind = ProfileAt(profile, arc, s - 0.4f);
                    var tangent = new Vector2(ahead.x - behind.x, ahead.y - behind.y);
                    // The profile runs outward over the cap and down the face, so the outward normal is on its left.
                    Vector2 normal2 = new Vector2(-tangent.y, tangent.x).normalized;
                    if (normal2.sqrMagnitude < 0.5f) normal2 = Vector2.up;
                    if (normal2.y < 0f && point.z < 0.99f) normal2 = Vector2.up;
                    float structure = point.z;

                    // The buttress of each height moves the face out. It grows in below the rounded edge. A lower
                    // TopFall makes it grow in closer to the top.
                    float depth = Mathf.Clamp01((wall.RimY - point.y) / faceHeight);
                    float buttress = 0f;
                    bool slabRow = slab && s <= arc[lines[local].SlabEnd] + 0.001f;
                    if (slabRow)
                    {
                        // The slab moves out with the buttresses and the smoothed relief of the top face row
                        // (SlabFrontRelief). Over the top the move grows in from the rim to the front, so no quad of
                        // the top stretches over the whole move.
                        float grow = structure > BrowStructure + 0.001f
                            ? 1f
                            : RockFaceBuilder.Smooth01((point.x - wall.RimX) /
                                                       Mathf.Max(lines[local].FrontX - shape.CapBevel - wall.RimX,
                                                           0.5f));
                        buttress = (buttresses.Offset(column.U, depth) + slabRelief[local]) * grow;
                    }
                    else if (structure >= 0.99f)
                    {
                        buttress = levelTop
                            ? buttresses.Offset(column.U, depth)
                            : buttresses.Offset(column.U, depth) *
                              RockFaceBuilder.Smooth01(depth / (ButtressGrowth * shape.TopFall));
                    }
                    else if (levelTop && structure > BrowStructure + 0.001f)
                    {
                        // A level top: the rounded edge moves out with the top row of the face, so the top stays
                        // level out to the face. Else the face rows stepped down and out from the edge.
                        buttress = buttresses.Offset(column.U, depth) + topRelief;
                    }
                    // A ground-covered top is as smooth as the terrain behind it, so it takes the same light.
                    // With the full brow covered, the whole rounded edge is ground and the soil goes on down the face.
                    // A slab with the ground on top shows the ground over the whole slab, down to its underside. It
                    // needs no ragged start: its back edge goes down into the ground.
                    float topGround = slab ? (shape.GroundOnTop && (structure < 0.99f || slabRow) ? 1f : 0f)
                        : !shape.GroundOnTop || structure >= 0.99f ? 0f
                        : shape.BrowGround >= 0.99f ? 1f
                        : 1f - RockFaceBuilder.Smooth01((structure - BrowStructure) /
                                                        ((1f - BrowStructure) * Mathf.Max(shape.BrowGround, 0.05f)));
                    // The soil of the top hangs down over the upper face, with a ragged lower edge. The relief of the
                    // face comes back over a longer distance than the colour, so the soil edge makes no ledge.
                    // A level top keeps the full relief up to the top row: a relief that came back below the soil
                    // moved the upper face out over a meter and made a slanted edge.
                    float soilShape = topGround;
                    // A slab top ends at its bevel: soil hanging down from a thin slab made teeth over the rock edge.
                    if (!slab && shape.GroundOnTop && shape.BrowGround >= 0.99f && structure >= 0.99f)
                    {
                        float below = faceTop - point.y;
                        topGround = Mathf.Max(topGround, SoilWeight(shape, settings.Seed, column.U, below, SoilEdge));
                        if (!levelTop)
                        {
                            soilShape = Mathf.Max(soilShape,
                                SoilRelief * SoilWeight(shape, settings.Seed, column.U, below - SoilReliefEdge * 0.5f,
                                    SoilReliefEdge));
                        }
                    }
                    // Near a cave opening the face is bare rock: the face rock goes back into the terrain face there,
                    // and soil on it stretched and left a stepped edge over the opening.
                    // The rounded edge over the face goes back too where the face is short, else it overhangs the
                    // opening. It keeps its ground cover, because its bare rock showed dark flakes in the terrain
                    // face. The top behind the rim keeps its shape.
                    float caveWeight = 1f;
                    float caveDistanceHere = float.MaxValue;
                    bool overFace = structure >= 0.99f || point.x >= wall.RimX;
                    if (caveDistance != null && wall.NearCave)
                    {
                        caveDistanceHere = caveDistance(FacePoint(column, wall, point.y));
                        caveWeight = RockFaceBuilder.Smooth01((caveDistanceHere - CaveClearance) / CaveTuck);
                        if (structure >= 0.99f)
                        {
                            topGround *= caveWeight;
                            soilShape *= caveWeight;
                        }
                    }
                    float relief = 1f - soilShape;
                    // The slab top takes the rock detail as if the face went on over it, so the detail changes across
                    // the top and not only along the wall. The whole slab has the reduced relief, so its edge stays
                    // clean.
                    bool slabTop = slab && structure <= BrowStructure + 0.001f;
                    float detailY = slabTop ? point.y - (lines[local].FrontX - point.x) : point.y;
                    if (slabRow) relief *= shape.CapRelief;
                    float offset = relief * RockFaceBuilder.EvaluateDetail(detail, column.U, detailY, structure,
                        loopLength, out RockFaceSample sample);
                    float facet = relief * structure * facets.Offset(column.U, detailY, out float edge);
                    // Joints and facet creases darken only where the geometry has them.
                    sample.Cavity = Mathf.Max(sample.Cavity, structure * edge) * relief;

                    Vector3 normal = (column.Outward * normal2.x + Vector3.up * normal2.y).normalized;
                    Vector3 vertex = column.Origin + column.Outward * (point.x + buttress) + Vector3.up * point.y +
                                     normal * (offset + facet);
                    // Near an open end the rock goes back into the terrain behind it.
                    if (endWeight < 1f)
                    {
                        float back = (1f - endWeight) * (point.x + buttress + offset + facet - wall.RimX +
                                                         settings.TuckDepth + 1f);
                        vertex -= column.Outward * Mathf.Max(back, 0f);
                        vertex.y -= (1f - endWeight) * settings.TuckDepth;
                    }
                    // Around a cave opening the face rock goes back into the terrain face, so the opening is cut into
                    // the face and the rock frames it. The top behind the rim keeps its shape. Rock in the
                    // opening is not drawn; it is behind the face there, so its stepped edge does not show.
                    // The face distance says where the face rock tucks back from an opening. A slab row is hidden only
                    // inside the cave: with the face clearance, a slab just over a cave ceiling lost its front rows.
                    bool inCave = slabRow
                        ? wall.NearCave && caveDistance != null && caveDistance(vertex) < 0f
                        : caveDistanceHere < CaveClearance;
                    // A slab stays whole over an opening, as a lintel: pulled back, it left a gap between its ground and
                    // its rock. Where the cave cuts the slab, its rows are in the cave and not drawn.
                    if (!slabRow && caveWeight < 1f && structure >= 0.99f)
                    {
                        Vector3 face = FacePoint(column, wall, vertex.y);
                        float outward = Vector3.Dot(vertex - face, column.Outward);
                        vertex -= column.Outward * ((1f - caveWeight) * Mathf.Max(outward + CaveTuckDepth, 0f));
                    }
                    else if (!slabRow && caveWeight < 1f && overFace)
                    {
                        // The rounded edge over the face shrinks toward the rim, so its rows keep their order. Pulled
                        // back behind the face like the face rock, all its rows met on one line, and its ground folded
                        // and stretched.
                        float overhang = Vector3.Dot(vertex - column.Origin, column.Outward) - wall.RimX;
                        vertex -= column.Outward * ((1f - caveWeight) * Mathf.Max(overhang, 0f));
                    }
                    if (!slabRow && lintelPull > 0f && point.y > wall.CaveTop)
                    {
                        Vector3 face = FacePoint(column, wall, vertex.y);
                        float outward = Vector3.Dot(vertex - face, column.Outward);
                        vertex -= column.Outward * (lintelPull * Mathf.Max(outward - LintelMargin, 0f));
                    }

                    int vertexIndex = strip.Index(local, row);
                    strip.Positions[vertexIndex] = vertex;
                    // Rock under the terrain is not drawn, except near the rim: the terrain turns down there between
                    // its height samples, so a dropped quad would show a hole.
                    // The top behind its start lies under the ground and is not drawn.
                    float start = lines[local].StartX;
                    bool onLand = !slab && structure < 0.99f && point.x < start - 2f * BaseStep;
                    strip.Active[vertexIndex] = !onLand && !inCave &&
                                                (structure < 0.99f ||
                                                 !(terrain.TrySampleHeight(vertex.x, vertex.z, out float buried) &&
                                                   vertex.y < buried - buriedDepth));
                    // The terrain surface mesh draws the ground-covered top and the soil (terrain cover) with the
                    // terrain shading, so the rock blends only into the ground at its foot.
                    float groundWeight = RockFaceBuilder.GroundWeight(vertex, column.Outward, column.U, settings,
                        terrain);
                    if (slabTop && !shape.GroundOnTop && shape.CapBlend > 0f)
                    {
                        // The back of the slab takes the ground of the land from the line where the ground meets its
                        // back edge. The height test above does not reach it: its probe in front of the vertex is past
                        // the rim, over the drop.
                        float meet = start - shape.CapRise / Mathf.Tan(shape.CapBackAngle * Mathf.Deg2Rad);
                        float jitter = 0.5f * shape.CapBlend *
                                       (2f * RockFaceNoise.Perlin(column.U / 3f + 5.3f, 0.7f) - 1f);
                        groundWeight = Mathf.Max(groundWeight,
                            1f - RockFaceBuilder.Smooth01((point.x - meet - jitter) / shape.CapBlend));
                    }
                    strip.Colors[vertexIndex] = new Color(sample.Hardness, sample.Cavity, sample.Layer, groundWeight);
                    // The top reads the land mirrored at its start, so it shows the layers of the land and not the rock
                    // band or the steep-ground layers of the rim under it. At its start the two points are the same, so
                    // the colour has no step there. It fades in from its start with a ragged edge.
                    Vector3 topPoint = column.Origin + column.Outward *
                        (point.x <= start ? point.x : start - Mathf.Min(point.x - start, MirrorReach));
                    Vector3 ground = structure < 0.99f ? topPoint : floorGround;
                    strip.GroundOffsets[vertexIndex] = new Vector2(ground.x - vertex.x, ground.z - vertex.z);
                    // The fade ends at the roll point at the latest, where the terrain starts to fall, and the rounded
                    // edge is fully covered. From there the top lies over the falling terrain and the gap between the
                    // terrain face and the rock, so a partial cover cut holes through to the face (or to a cave under
                    // the rim). A short top (Top Back under the fade length) has a narrower ragged edge.
                    float fadeLength = Mathf.Clamp(Mathf.Min(lines[local].RollX, wall.RimX) - start, BaseStep,
                        CoverFade);
                    float fade = !slab && structure < 0.99f && structure <= BrowStructure + 0.001f
                        ? RockFaceBuilder.Smooth01((point.x - start) / fadeLength)
                        : 1f;
                    // The top casts a shadow from the roll point on, where it leaves the terrain and rolls into the
                    // face. On the land the terrain under it casts.
                    float cast = structure < 0.99f
                        ? RockFaceBuilder.Smooth01((point.x - lines[local].RollX) / CastFade)
                        : 1f;
                    strip.TerrainCover[vertexIndex] = new Vector4(topPoint.x - vertex.x, topPoint.z - vertex.z,
                        topGround * fade, cast);
                    // The rock under a ground-covered top is not drawn, also where the top fades in.
                    strip.Show[vertexIndex] = structure < 0.99f ? 1f - topGround : 1f;
                    faceRows[vertexIndex] = structure >= 0.99f && !slabRow;
                    faceBase[vertexIndex] = column.Origin + column.Outward * (point.x + buttress) + Vector3.up * point.y;
                    // Over a fractured face the block shell also forms the slab front, so the slab keeps only its
                    // ground-covered top. Its front and underside floated over the blocks where the slab stood higher.
                    bool slabFront = slabRow && structure > BrowStructure + 0.001f;
                    if ((faceRows[vertexIndex] || slabFront) && column.U > fractureRange.x && column.U < fractureRange.y)
                    {
                        // The face ends along a ragged edge where the blocks come out of it at the range ends.
                        float weight = FracturedFaceBuilder.EndWeight(column.U, fractureRange, shape.Fracture);
                        strip.Show[vertexIndex] = 1f - RockFaceBuilder.Smooth01((weight - 0.3f) / 0.4f);
                    }
                    terrainShade[vertexIndex] = (!slab || shape.GroundOnTop) && structure < 0.99f
                        ? 1f - RockFaceBuilder.Smooth01((structure - BrowStructure) / (1f - BrowStructure))
                        : 0f;
                }
            });
            RockFaceBuilder.ComputeNormals(strip, columns, first);
            if (shape.ShadeDepth > 0f) FormShade(strip, settings, shape);
            TerrainShade(strip, terrainShade, terrain);
            if (fractureRange.y > fractureRange.x)
            {
                BuildFracture(strip, columns, first, lines, faceRows, faceBase, fractureRange, settings, shape, terrain,
                    fractureMeshes, caveDistance);
            }
        }

        // Cavity shade of the large forms. Each vertex is compared with the face smoothed over Shade Radius (two box
        // averages along each grid direction). A vertex behind the smoothed face is in a hollow and gets more cavity. The
        // shade changes slowly over the face, so the coarse LOD meshes keep it.
        private static void FormShade(RockFaceStrip strip, RockFaceSettings settings, CliffWallShape shape)
        {
            int count = strip.VertexCount;
            var smooth = new Vector3[count];
            var scratch = new Vector3[count];
            Array.Copy(strip.Positions, smooth, count);
            int rowRadius = Mathf.Max(1, Mathf.RoundToInt(0.5f * shape.ShadeRadius / settings.RowSpacing));
            int columnRadius = Mathf.Max(1, Mathf.RoundToInt(0.5f * shape.ShadeRadius / settings.ColumnSpacing));
            for (int pass = 0; pass < 2; pass++)
            {
                for (int column = 0; column < strip.Columns; column++)
                {
                    BoxAverage(smooth, scratch, strip.Index(column, 0), 1, strip.Rows, rowRadius, false);
                }
                for (int row = 0; row < strip.Rows; row++)
                {
                    BoxAverage(scratch, smooth, row, strip.Rows, strip.Columns, columnRadius, strip.Loop);
                }
            }
            for (int index = 0; index < count; index++)
            {
                float depth = Vector3.Dot(smooth[index] - strip.Positions[index], strip.Normals[index]);
                float shade = RockFaceBuilder.Smooth01(depth / shape.ShadeDepth);
                Color32 color = strip.Colors[index];
                color.g = (byte)Mathf.Max(color.g, Mathf.RoundToInt(255f * shade));
                strip.Colors[index] = color;
            }
        }

        // Running box average over one grid line of the given length, from start in steps of stride. The ends repeat
        // the end point, or a loop wraps.
        private static void BoxAverage(Vector3[] source, Vector3[] target, int start, int stride, int length,
            int radius, bool loop)
        {
            int At(int step) => start + stride * (loop ? (step % length + length) % length
                                                       : Mathf.Clamp(step, 0, length - 1));
            Vector3 sum = Vector3.zero;
            for (int step = -radius; step <= radius; step++) sum += source[At(step)];
            float scale = 1f / (2 * radius + 1);
            for (int step = 0; step < length; step++)
            {
                target[start + stride * step] = sum * scale;
                sum += source[At(step + radius + 1)] - source[At(step - radius)];
            }
        }

        // The block shell of the fractured face over the strip columns in the range.
        private static void BuildFracture(RockFaceStrip strip, RockFaceBuilder.Column[] columns, int first,
            TopLine[] lines, bool[] faceRows, Vector3[] faceBase, Vector2 range, RockFaceSettings settings,
            CliffWallShape shape, TerrainHeightSampler terrain, List<Mesh[]> meshes, Func<Vector3, float> caveDistance)
        {
            var faceColumns = new List<FracturedFaceBuilder.FaceColumn>();
            for (int local = 0; local < strip.Columns; local++)
            {
                RockFaceBuilder.Column column = columns[first + local];
                if (column.U < range.x || column.U > range.y) continue;
                var face = new FracturedFaceBuilder.FaceColumn
                {
                    U = column.U,
                    Origin = new Vector3(column.Origin.x, 0f, column.Origin.z),
                    Outward = column.Outward
                };
                for (int row = 0; row < strip.Rows; row++)
                {
                    int index = strip.Index(local, row);
                    if (!faceRows[index]) continue;
                    Vector3 position = faceBase[index];
                    Vector3 offset = position - face.Origin;
                    face.Face.Add(new Vector2(offset.x * column.Outward.x + offset.z * column.Outward.z, position.y));
                }
                if (face.Face.Count < 2) continue;
                // Under a slab the shell reaches just under the ground-covered slab top, so it forms the slab front.
                face.Top = shape.Top == CliffWallTop.Slab ? lines[local].FrontTop - SlabSeat : face.Face[0].y;
                faceColumns.Add(face);
            }
            meshes.AddRange(FracturedFaceBuilder.Build(faceColumns, shape.Fracture, settings, terrain,
                "Fractured face " + meshes.Count, caveDistance, shape.Top == CliffWallTop.Slab));
        }

        // The top takes the terrain normal of the land point whose layers it shows, and the rounded edge turns to its own
        // normal. The top then has the light of the land beside it. With its own normal, a top that turns up over a
        // terrain lip faced a low sun and showed as a bright band. A steep top keeps its own normal, for example where it
        // goes back around a cave opening: with the flat land normal the triplanar mapping used only the top projection
        // there and stretched the ground texture into rings.
        private static void TerrainShade(RockFaceStrip strip, float[] weights, TerrainHeightSampler terrain)
        {
            const float reach = 0.5f;
            for (int index = 0; index < strip.VertexCount; index++)
            {
                float weight = weights[index] *
                               RockFaceBuilder.Smooth01((strip.Normals[index].y - SteepTopNormal) / SteepTopRange);
                if (weight <= 0f) continue;
                Vector4 cover = strip.TerrainCover[index];
                Vector3 position = strip.Positions[index] + new Vector3(cover.x, 0f, cover.y);
                if (!terrain.TrySampleHeight(position.x - reach, position.z, out float left) ||
                    !terrain.TrySampleHeight(position.x + reach, position.z, out float right) ||
                    !terrain.TrySampleHeight(position.x, position.z - reach, out float back) ||
                    !terrain.TrySampleHeight(position.x, position.z + reach, out float front))
                {
                    continue;
                }
                var normal = new Vector3((left - right) / (2f * reach), 1f, (back - front) / (2f * reach));
                strip.Normals[index] = Vector3.Lerp(strip.Normals[index], normal.normalized, weight).normalized;
            }
        }

        // Depth to which the front of a slab hangs down below its underside. It changes along the wall like the soil.
        private static float SlabDroop(CliffWallShape shape, int seed, float u)
        {
            if (shape.CapDroop <= 0f) return 0f;
            float noise = 2f * RockFaceNoise.Perlin(u / 6f + seed * 0.233f, 5.7f) - 1f;
            float fine = 2f * RockFaceNoise.Perlin(u / 1.9f + seed * 0.419f, 2.9f) - 1f;
            return Mathf.Max(0f, shape.CapDroop * (1f + shape.SoilVariation * (0.7f * noise + 0.3f * fine)));
        }

        // Ground weight of the soil that hangs over the upper face. Its depth changes along the wall.
        private static float SoilWeight(CliffWallShape shape, int seed, float u, float below, float edge)
        {
            if (shape.SoilDepth <= 0f) return 0f;
            float noise = 2f * RockFaceNoise.Perlin(u / 7f + seed * 0.173f, 3.1f) - 1f;
            float fine = 2f * RockFaceNoise.Perlin(u / 1.7f + seed * 0.311f, 8.3f) - 1f;
            float depth = shape.SoilDepth * (1f + shape.SoilVariation * (0.7f * noise + 0.3f * fine));
            return 1f - RockFaceBuilder.Smooth01((below - depth) / edge + 0.5f);
        }

        private static float[] ArcLengths(List<Vector3> profile)
        {
            var arc = new float[profile.Count];
            for (int index = 1; index < profile.Count; index++)
            {
                arc[index] = arc[index - 1] + new Vector2(profile[index].x - profile[index - 1].x,
                    profile[index].y - profile[index - 1].y).magnitude;
            }
            return arc;
        }

        private static Vector3 ProfileAt(List<Vector3> profile, float[] arc, float s)
        {
            s = Mathf.Clamp(s, 0f, arc[^1]);
            int index = Array.BinarySearch(arc, s);
            if (index >= 0) return profile[index];
            index = Mathf.Clamp(~index - 1, 0, arc.Length - 2);
            float span = arc[index + 1] - arc[index];
            float share = span > 1e-6f ? (s - arc[index]) / span : 0f;
            return Vector3.Lerp(profile[index], profile[index + 1], share);
        }
        #endregion

        #region Structure
        private static float SmoothMaximum(float a, float b, float rounding)
        {
            float maximum = Mathf.Max(a, b);
            return maximum + rounding * Mathf.Log(Mathf.Exp((a - maximum) / rounding) +
                                                 Mathf.Exp((b - maximum) / rounding));
        }

        // Buttresses along the wall: each has a flat front that can turn away from the wall line, and steep sides. A
        // low buttress between two high ones forms a gully.
        private sealed class ButtressRow
        {
            private const float Rounding = 0.35f;
            private readonly CliffWallShape _shape;
            private readonly float[] _centres;
            private readonly float[] _halfWidths;
            private readonly float[] _depths;
            private readonly float[] _turns;
            private readonly float[] _tapers;
            private readonly float _length;
            private readonly bool _closed;

            public ButtressRow(CliffWallShape shape, int seed, float length, bool closed)
            {
                _shape = shape;
                _length = length;
                _closed = closed;
                var centres = new List<float>();
                var halfWidths = new List<float>();
                float minimum = Mathf.Max(1f, Mathf.Min(shape.ButtressWidth.x, shape.ButtressWidth.y));
                float maximum = Mathf.Max(minimum, shape.ButtressWidth.y);
                float position = closed ? 0f : -maximum;
                int index = 0;
                float end = closed ? length : length + maximum;
                while (position < end)
                {
                    float width = Mathf.Lerp(minimum, maximum, RockFaceNoise.Hash01(seed, index, 0, 211));
                    // A closed wall ends with a buttress that fills the rest of the loop.
                    if (closed && end - position - width < minimum) width = end - position;
                    centres.Add(position + 0.5f * width);
                    halfWidths.Add(0.5f * width);
                    position += width;
                    index++;
                }
                int count = centres.Count;
                _centres = centres.ToArray();
                _halfWidths = halfWidths.ToArray();
                _depths = new float[count];
                _turns = new float[count];
                _tapers = new float[count];
                for (int buttress = 0; buttress < count; buttress++)
                {
                    float depth = RockFaceNoise.Hash01(seed, buttress, 1, 223);
                    _depths[buttress] = shape.ButtressDepth * depth * depth;
                    float turn = (2f * RockFaceNoise.Hash01(seed, buttress, 2, 227) - 1f) * shape.ButtressTurn;
                    _turns[buttress] = Mathf.Tan(turn * Mathf.Deg2Rad);
                    _tapers[buttress] = 2f * RockFaceNoise.Hash01(seed, buttress, 3, 229) - 1f;
                }
            }

            /// <param name="depth">Share of the face height below the top: 0 at the top, 1 at the foot.</param>
            public float Offset(float u, float depth)
            {
                if (_shape.ButtressDepth <= 0f || _centres.Length == 0) return 0f;
                int nearest = Nearest(u);
                float result = float.MinValue;
                for (int offset = -1; offset <= 1; offset++)
                {
                    int buttress = nearest + offset;
                    if (_closed) buttress = (buttress % _centres.Length + _centres.Length) % _centres.Length;
                    else if (buttress < 0 || buttress >= _centres.Length) continue;
                    float along = Delta(u, _centres[buttress]);
                    // Some buttresses grow toward the foot and some toward the top.
                    float height = _tapers[buttress] > 0f ? depth : 1f - depth;
                    float scale = 1f - _shape.ButtressTaper * Mathf.Abs(_tapers[buttress]) * height;
                    float front = _depths[buttress] * scale + _turns[buttress] * along;
                    float side = _shape.ButtressSides * Mathf.Max(0f, Mathf.Abs(along) - _halfWidths[buttress]);
                    float value = front - side;
                    result = result == float.MinValue ? value : SmoothMaximum(result, value, Rounding);
                }
                return Mathf.Clamp(result, 0f, 1.5f * _shape.ButtressDepth);
            }

            private int Nearest(float u)
            {
                if (_closed) u = Mathf.Repeat(u, _length);
                int index = Array.BinarySearch(_centres, u);
                if (index < 0) index = ~index;
                index = Mathf.Clamp(index, 0, _centres.Length - 1);
                if (index > 0 && Mathf.Abs(Delta(u, _centres[index - 1])) < Mathf.Abs(Delta(u, _centres[index])))
                {
                    index--;
                }
                return index;
            }

            private float Delta(float u, float centre)
            {
                float delta = u - centre;
                if (_closed) delta -= _length * Mathf.Round(delta / _length);
                return delta;
            }
        }

        // Angular rock facets on the face: each cell of a jittered grid in (along, height) holds a tilted cone, and the
        // face takes the highest cone at each point. The cones meet in sharp crests, so the face breaks into facets.
        private sealed class FacetField
        {
            private readonly CliffWallShape _shape;
            private readonly int _seed;

            public FacetField(CliffWallShape shape, int seed)
            {
                _shape = shape;
                _seed = seed;
            }

            /// <param name="edge">1 in the creases between facets, 0 on a facet.</param>
            public float Offset(float u, float y, out float edge)
            {
                edge = 0f;
                if (_shape.FacetDepth <= 0f) return 0f;
                float size = _shape.FacetSize;
                int cellU = Mathf.FloorToInt(u / size);
                int cellY = Mathf.FloorToInt(y / size);
                float best = float.MinValue;
                float second = float.MinValue;
                float result = float.MinValue;
                float fall = _shape.FacetDepth / (0.75f * size);
                for (int du = -1; du <= 1; du++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int cu = cellU + du;
                        int cy = cellY + dy;
                        float centreU = (cu + 0.15f + 0.7f * RockFaceNoise.Hash01(_seed, cu, cy, 307)) * size;
                        float centreY = (cy + 0.15f + 0.7f * RockFaceNoise.Hash01(_seed, cu, cy, 311)) * size;
                        float tiltU = (2f * RockFaceNoise.Hash01(_seed, cu, cy, 313) - 1f) * _shape.FacetTilt;
                        float tiltY = (2f * RockFaceNoise.Hash01(_seed, cu, cy, 317) - 1f) * _shape.FacetTilt;
                        float height = _shape.FacetDepth * RockFaceNoise.Hash01(_seed, cu, cy, 331);
                        float alongU = u - centreU;
                        float alongY = y - centreY;
                        float value = height + tiltU * alongU + tiltY * alongY -
                                      fall * Mathf.Sqrt(alongU * alongU + alongY * alongY);
                        if (value > best)
                        {
                            second = best;
                            best = value;
                        }
                        else if (value > second)
                        {
                            second = value;
                        }
                        result = result == float.MinValue
                            ? value
                            : SmoothMaximum(result, value, _shape.FacetRounding);
                    }
                }
                edge = 1f - RockFaceBuilder.Smooth01((best - second) / (2f * _shape.FacetRounding + 0.05f));
                return Mathf.Clamp(0.5f * (result + _shape.FacetDepth), 0f, 1.5f * _shape.FacetDepth);
            }
        }
        #endregion
    }
}
