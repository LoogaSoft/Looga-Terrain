using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Builds the surface of falling water in the frame of a <see cref="WaterFallEdge"/>. Upstream of the lip the sheet
    /// lies at the level of the feeding water and starts on its weld line, where that water ends. Past the lip it lies
    /// one water thickness off the bed: a structure face, or the lower edge of a free jet. It turns into the water below
    /// on a round bend and runs out flat a short way.
    /// </summary>
    /// <remarks>
    /// The mesh holds the fall data of the Looga/Water fall mode in UV2 to UV4 and the body data of Looga/Water in UV0
    /// and UV1.xy, with the values of the feeding water on the weld line, so the sheet starts with the look of that water.
    /// UV1.z is the pattern shift of the grid, so the shader gets the distance along the lip from UV3.x.
    /// </remarks>
    internal static class WaterFallSheet
    {
        // Distance along the bed past the lip over which the surface bends down from the river level.
        private const float BrinkBend = 4f;
        // Free overfall (a lip without a structure profile), after Rouse: the head over the lip is 1.5 critical
        // depths, the depth at the brink is 0.715 critical depths and the water leaves the lip at the mean speed
        // there. The surface draws down 0.785 critical depths over about four critical depths upstream of the brink,
        // so it meets the brink with this slope, and the jet leaves the lip in that direction.
        internal const float CriticalHeadShare = 1.5f;
        private const float BrinkDepthShare = 0.715f;
        private const float BrinkSlope = 0.39f;
        // Distance upstream of the lip over which the progress grows from -0.3 to 0.
        private const float ApproachProgress = 4f;
        // Past the plunge the progress grows by 1 over this distance, as over a plunge pool.
        private const float RunOutProgressLength = 60f;
        // The flow over the run-out turns out to the sides by up to this angle, in degrees.
        private const float RunOutSpread = 25f;
        // Speed under which travel time stops growing faster, in frame units per second.
        private const float MinimumTravelSpeed = 0.5f;
        // Depth below the water below at which a free jet ends, in frame units.
        private const float JetEndDepth = 1f;
        private const int MaximumBedPoints = 4096;
        // The shader moves the vertices off the surface by up to this much, in frame units.
        private const float BoundsPadding = 6f;
        // Largest distance between the points of a straight weld line, in frame units.
        private const float WeldStep = 2f;

        private static readonly List<Vector2> Beds = new();
        private static readonly List<Vector2> Surfaces = new();
        private static readonly List<float> Thicknesses = new();
        private static readonly MeshData Data = new();

        #region Path
        /// <summary>
        /// Solves the sheet centreline from upstream of the lip to the end of the run-out. Returns the index of the
        /// plunge point, where the falling water meets the water below, or -1 when the water never reaches it.
        /// </summary>
        internal static int SolvePath(in WaterFallEdge edge, IReadOnlyList<Vector2> profile, WaterFallSettings settings,
            List<WaterFallPoint> path)
        {
            path.Clear();
            Surfaces.Clear();
            Thicknesses.Clear();
            float spacing = settings.RowSpacing;
            float top = edge.Level;
            for (float u = -settings.SheetStart; u < 0f; u += spacing)
            {
                Surfaces.Add(new Vector2(u, top));
                Thicknesses.Add(Mathf.Max(top - BedHeight(profile, u, edge.Crest), 0f));
            }
            int firstFace = Surfaces.Count;
            bool freeOverfall = profile.Count == 0;
            float brinkSpeed = FreeOverfallSpeed(edge.UnitDischarge, edge.Gravity);
            BuildBed(edge, profile, settings);

            float surfaceLevel = edge.Landing + settings.RunOutLift;
            float crestThickness = edge.UnitDischarge /
                Mathf.Sqrt(2f * edge.Gravity * Mathf.Max(edge.Level - edge.Crest, 0.01f));
            float poolStart = float.MaxValue;
            float along = 0f;
            for (int index = 0; index < Beds.Count; index++)
            {
                Vector2 bed = Beds[index];
                if (index > 0) along += Vector2.Distance(Beds[index - 1], bed);
                Vector2 tangent = (Beds[Mathf.Min(Beds.Count - 1, index + 1)] - Beds[Mathf.Max(0, index - 1)])
                    .normalized;
                var normal = new Vector2(-tangent.y, tangent.x);
                float thickness;
                float brinkExcess = 0f;
                if (freeOverfall)
                {
                    // Continuity: the flow per metre at the speed of the falling water. At the brink this is the
                    // brink depth. The surface drew down before the brink (DrawDownToBrink).
                    float jetSpeed = Mathf.Sqrt(brinkSpeed * brinkSpeed +
                        2f * edge.Gravity * Mathf.Max(edge.Crest - bed.y, 0f));
                    thickness = Mathf.Max(settings.MinimumThickness, edge.UnitDischarge / jetSpeed);
                }
                else
                {
                    float fallSpeed = Mathf.Sqrt(2f * edge.Gravity * Mathf.Max(edge.Level - bed.y, 0.3f));
                    thickness = Mathf.Max(settings.MinimumThickness,
                        Mathf.Min(edge.UnitDischarge / fallSpeed, crestThickness));
                    // At the lip the surface starts at the river level and bends down over the first metres of the
                    // bed. A free jet drops steeply, so the bend follows the bed and not the horizontal distance. The
                    // extra height is vertical, so the surface does not bulge out where the bed turns down.
                    brinkExcess = Mathf.Max(top - bed.y - thickness, 0f) *
                        (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, BrinkBend, along)));
                }
                Vector2 surface = bed + normal * thickness + Vector2.up * brinkExcess;
                thickness += brinkExcess;
                if (surface.y < surfaceLevel)
                {
                    surface.y = surfaceLevel;
                    thickness = surface.y - bed.y;
                    poolStart = Mathf.Min(poolStart, bed.x);
                }
                Surfaces.Add(surface);
                Thicknesses.Add(thickness);
            }
            if (poolStart == float.MaxValue) return -1;

            if (freeOverfall) DrawDownToBrink(firstFace, top);
            BendOverBrink(firstFace, top, settings.BrinkRadius);
            BendIntoWater(firstFace, surfaceLevel, settings.BendRadius);
            BuildPoints(edge, settings, firstFace, poolStart, path);
            for (int index = firstFace; index < path.Count; index++)
            {
                if (path[index].Progress >= 1f) return index;
            }
            return -1;
        }

        // The bed under the falling water from the lip on: the structure face, then a free jet from its end when the
        // face ends above the water below, then flat under the water past the plunge.
        private static void BuildBed(in WaterFallEdge edge, IReadOnlyList<Vector2> profile, WaterFallSettings settings)
        {
            Beds.Clear();
            float spacing = settings.RowSpacing;
            for (int index = 1; index < profile.Count; index++)
            {
                Vector2 a = profile[index - 1];
                Vector2 b = profile[index];
                if (b.x < 0f || b.x <= a.x) continue;

                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / spacing));
                for (int step = Beds.Count == 0 ? 0 : 1; step <= steps; step++)
                {
                    Vector2 point = Vector2.Lerp(a, b, step / (float)steps);
                    if (point.x >= 0f) Beds.Add(point);
                }
            }

            // A free jet leaves the lip, or the face end, and falls under gravity. Over a free overfall it leaves
            // the lip at the brink speed, in the direction of the drawn-down surface.
            Vector2 position;
            Vector2 velocity;
            if (Beds.Count == 0)
            {
                position = new Vector2(0f, edge.Crest);
                float brinkSpeed = Mathf.Max(edge.LipSpeed, FreeOverfallSpeed(edge.UnitDischarge, edge.Gravity));
                velocity = new Vector2(1f, -BrinkSlope).normalized * brinkSpeed;
                Beds.Add(position);
            }
            else
            {
                position = Beds[Beds.Count - 1];
                Vector2 previous = Beds.Count > 1 ? Beds[Beds.Count - 2] : position - Vector2.right;
                float speed = Mathf.Sqrt(edge.LipSpeed * edge.LipSpeed +
                    2f * edge.Gravity * Mathf.Max(edge.Level - position.y, 0f));
                velocity = (position - previous).normalized * speed;
            }
            float jetEnd = edge.Landing - JetEndDepth;
            while (position.y > jetEnd && Beds.Count < MaximumBedPoints)
            {
                float time = spacing / Mathf.Max(velocity.magnitude, 0.1f);
                position += velocity * time + new Vector2(0f, -0.5f * edge.Gravity * time * time);
                velocity.y -= edge.Gravity * time;
                Beds.Add(position);
            }

            // Flat under the water below, far enough for the bend and the run-out.
            Vector2 last = Beds[Beds.Count - 1];
            float end = last.x + settings.RunOut + 2f * settings.BendRadius + 2f * spacing;
            for (float u = last.x + spacing; u <= end && Beds.Count < MaximumBedPoints; u += spacing)
            {
                Beds.Add(new Vector2(u, last.y));
            }
        }

        /// <summary>
        /// Speed of the water that leaves a free overfall, from the flow per metre: the flow at the brink depth of
        /// 0.715 critical depths (Rouse).
        /// </summary>
        internal static float FreeOverfallSpeed(float unitDischarge, float gravity)
        {
            float critical = Mathf.Pow(unitDischarge * unitDischarge / Mathf.Max(gravity, 0.01f), 1f / 3f);
            return unitDischarge / Mathf.Max(BrinkDepthShare * critical, 0.01f);
        }

        // A free overfall draws down toward the brink: upstream of it the surface falls from the level of the feeding
        // water over a parabola, which meets the falling sheet with the brink slope. The points move down, in place.
        private static void DrawDownToBrink(int firstFace, float top)
        {
            if (firstFace <= 0 || firstFace >= Surfaces.Count) return;

            Vector2 brink = Surfaces[firstFace];
            float drop = top - brink.y;
            if (drop <= 0f) return;

            float length = 2f * drop / BrinkSlope;
            for (int index = 0; index < firstFace; index++)
            {
                Vector2 surface = Surfaces[index];
                float behind = brink.x - surface.x;
                if (behind >= length) continue;

                float share = 1f - behind / length;
                float height = top - drop * share * share;
                Thicknesses[index] = Mathf.Max(Thicknesses[index] - (surface.y - height), 0f);
                Surfaces[index] = new Vector2(surface.x, height);
            }
        }

        // The water turns over the brink on a round bend, from the level of the feeding water to the falling sheet,
        // so the top edge of the fall is round and not sharp. The bend ends at the first face point where an arc of
        // the radius fits between the level and the tangent of the face there. The points from the start of the arc
        // to that point move onto the arc and the tangent, in the same order and with the same share of the length.
        private static void BendOverBrink(int firstFace, float top, float radius)
        {
            if (radius <= 0f || firstFace <= 0) return;

            for (int end = firstFace + 1; end < Surfaces.Count - 1; end++)
            {
                Vector2 face = Surfaces[end];
                Vector2 fall = (Surfaces[end + 1] - Surfaces[end - 1]).normalized;
                if (fall.y > -0.05f) continue;

                // The corner of the level and the face tangent, and the tangent length of the arc.
                float back = (top - face.y) / -fall.y;
                Vector2 corner = face - fall * back;
                float turn = Mathf.Acos(Mathf.Clamp(fall.x, -1f, 1f));
                float reach = radius * Mathf.Tan(0.5f * turn);
                if (back < reach) continue;

                Vector2 start = corner - Vector2.right * reach;
                if (start.x <= Surfaces[0].x) return;

                int first = 0;
                while (first < end && Surfaces[first].x <= start.x)
                {
                    first++;
                }
                float original = Vector2.Distance(start, Surfaces[first]);
                for (int index = first + 1; index <= end; index++)
                {
                    original += Vector2.Distance(Surfaces[index - 1], Surfaces[index]);
                }
                float arc = radius * turn;
                float length = arc + (back - reach);
                Vector2 center = start + Vector2.down * radius;
                float travelled = Vector2.Distance(start, Surfaces[first]);
                for (int index = first; index <= end; index++)
                {
                    if (index > first) travelled += Vector2.Distance(Surfaces[index - 1], Surfaces[index]);
                    float along = travelled / Mathf.Max(original, 1e-4f) * length;
                    Surfaces[index] = along <= arc
                        ? center + new Vector2(Mathf.Sin(along / radius), Mathf.Cos(along / radius)) * radius
                        : corner + fall * (reach + along - arc);
                }
                return;
            }
        }

        // The falling sheet turns into the water below on a round bend, so the jet and the water below meet as one
        // smooth surface and not at a sharp corner. The points within the bend move onto the arc, in the same order.
        private static void BendIntoWater(int firstFace, float surfaceLevel, float radius)
        {
            if (radius <= 0f) return;

            int corner = -1;
            for (int index = firstFace; index < Surfaces.Count; index++)
            {
                if (Surfaces[index].y > surfaceLevel + 1e-4f) continue;

                corner = index;
                break;
            }
            if (corner <= firstFace) return;

            Vector2 plunge = Surfaces[corner];
            // The falling direction over the last part of the drop.
            int above = corner;
            while (above > firstFace && Vector2.Distance(Surfaces[above], plunge) < radius)
            {
                above--;
            }
            Vector2 fall = (plunge - Surfaces[above]).normalized;
            float turn = Mathf.Acos(Mathf.Clamp(Vector2.Dot(fall, Vector2.right), -1f, 1f));
            if (turn < 0.05f) return;

            float reach = radius * Mathf.Tan(turn * 0.5f);
            Vector2 start = plunge - fall * reach;
            Vector2 center = plunge + Vector2.right * reach + Vector2.up * radius;
            float startAngle = Mathf.Atan2(start.y - center.y, start.x - center.x);
            float endAngle = -0.5f * Mathf.PI;
            for (int index = firstFace; index < Surfaces.Count; index++)
            {
                Vector2 point = Surfaces[index];
                float along = index < corner ? Vector2.Dot(point - start, fall) : reach + (point.x - plunge.x);
                if (along < 0f || along > 2f * reach) continue;

                float angle = Mathf.Lerp(startAngle, endAngle, along / (2f * reach));
                Surfaces[index] = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            }
        }

        // Speed, progress, travel time and streak tiles along the surface. Past the plunge the water flows out at the
        // plunge speed of the network, which decays with the distance.
        private static void BuildPoints(in WaterFallEdge edge, WaterFallSettings settings, int firstFace,
            float poolStart, List<WaterFallPoint> path)
        {
            float top = edge.Level;
            float drop = Mathf.Max(edge.Level - edge.Landing, 0.01f);
            float scale = Mathf.Max(edge.Scale, 1e-4f);
            for (int index = 0; index < Surfaces.Count; index++)
            {
                Vector2 surface = Surfaces[index];
                float bedX = index >= firstFace ? Beds[index - firstFace].x : surface.x;
                bool onPool = index >= firstFace && bedX >= poolStart;
                Vector2 tangent = (Surfaces[Mathf.Min(Surfaces.Count - 1, index + 1)] -
                    Surfaces[Mathf.Max(0, index - 1)]).normalized;
                float speed;
                float progress;
                if (onPool)
                {
                    float run = bedX - poolStart;
                    speed = edge.PlungeSpeed * Mathf.Exp(-run * scale / Mathf.Max(edge.PlungeDecay, 0.1f)) / scale;
                    progress = 1f + run / RunOutProgressLength;
                }
                else
                {
                    speed = Mathf.Sqrt(edge.LipSpeed * edge.LipSpeed +
                        2f * edge.Gravity * Mathf.Max(0f, top - surface.y));
                    progress = index < firstFace
                        ? Mathf.Lerp(-0.3f, 0f, Mathf.InverseLerp(-ApproachProgress, 0f, surface.x))
                        : Mathf.Clamp01((top - surface.y) / drop);
                }
                var point = new WaterFallPoint
                {
                    Surface = surface,
                    Normal = new Vector2(-tangent.y, tangent.x),
                    Thickness = Thicknesses[index],
                    Progress = progress,
                    Speed = speed
                };
                if (path.Count > 0)
                {
                    WaterFallPoint last = path[path.Count - 1];
                    float step = Vector2.Distance(last.Surface, surface);
                    point.Distance = last.Distance + step;
                    point.Travel = last.Travel + step / Mathf.Max(speed, MinimumTravelSpeed);
                    point.Stretch = last.Stretch + step / StreakTile(settings, 0.5f * (last.Speed + speed));
                }
                path.Add(point);
            }
            // The lip is the first point of the face, where the surface starts to drop.
            float brink = path[Mathf.Min(firstFace, path.Count - 1)].Distance;
            for (int index = 0; index < path.Count; index++)
            {
                WaterFallPoint point = path[index];
                point.Brink = point.Distance - brink;
                path[index] = point;
            }
        }

        // Bed height at a downstream distance: the profile, or the crest without a profile. The profile height is
        // clamped to its first and last points.
        private static float BedHeight(IReadOnlyList<Vector2> profile, float distance, float crest)
        {
            if (profile.Count == 0) return crest;
            if (distance <= profile[0].x) return profile[0].y;

            for (int index = 1; index < profile.Count; index++)
            {
                Vector2 a = profile[index - 1];
                Vector2 b = profile[index];
                if (b.x < distance || a.x > distance || b.x <= a.x) continue;

                return Mathf.Lerp(a.y, b.y, Mathf.InverseLerp(a.x, b.x, distance));
            }
            return profile[profile.Count - 1].y;
        }

        /// <summary>Height of the profile at the lip, or the fallback without a profile.</summary>
        internal static float CrestHeight(IReadOnlyList<Vector2> profile, float fallback)
        {
            return BedHeight(profile, 0f, fallback);
        }

        // The streak tile length grows mildly with the speed. Fast water looks streaked, but not smeared.
        private static float StreakTile(WaterFallSettings settings, float speed)
        {
            return settings.StreakLength * (1f + speed / settings.StreakStretchSpeed);
        }

        // Streak texture tiles per second of travel time, one value for the whole sheet: the rate of the streak tile
        // at the mean speed of the drop. The shader moves the streaks exactly with the water (travel time), so the
        // tile is this long at the mean speed, shorter where the water is slower and longer where it is faster.
        private static float StreakRate(IReadOnlyList<WaterFallPoint> path, WaterFallSettings settings)
        {
            float length = 0f;
            float weighted = 0f;
            for (int index = 1; index < path.Count; index++)
            {
                WaterFallPoint a = path[index - 1];
                WaterFallPoint b = path[index];
                if (b.Progress <= 0f || a.Progress >= 1f) continue;

                float step = b.Distance - a.Distance;
                length += step;
                weighted += step * 0.5f * (a.Speed + b.Speed);
            }
            float speed = length > 0f ? weighted / length : Mathf.Max(path[0].Speed, MinimumTravelSpeed);
            return speed / StreakTile(settings, speed);
        }
        #endregion

        #region Mesh
        /// <summary>
        /// Builds the sheet mesh in the frame: one grid per jet for the sheet, and one per jet for each foam shell. The
        /// sampler gives the vertex data of the feeding water on the weld line (null = values from the edge). The weld
        /// line gets the world XZ points of the sheet start across the whole lip and the normals toward the fall.
        /// </summary>
        internal static void BuildMesh(Mesh mesh, in WaterFallEdge edge, IReadOnlyList<WaterFallPoint> path,
            int plunge, WaterFallSettings settings, WaterSurfaceSampler sampler, List<Vector2> weldLine,
            List<Vector2> weldNormals)
        {
            Data.Clear();
            float plungeDistance = path[plunge].Surface.x;
            int bays = Mathf.Max(1, edge.Bays);
            float pier = Mathf.Max(0f, edge.PierWidth);
            float bayWidth = Mathf.Max(0.1f, (edge.Width - (bays + 1) * pier) / bays);
            for (int bay = 0; bay < bays; bay++)
            {
                float x0 = -0.5f * edge.Width + pier + bay * (bayWidth + pier);
                float x1 = x0 + bayWidth;
                AddGrid(edge, path, settings, sampler, x0, x1, -settings.SheetStart, plungeDistance + settings.RunOut,
                    0f, 0f, 0f);
                // The shells end at the plunge. They fade out above the water, so no loose edge hangs there.
                foreach (WaterFallShell shell in settings.Shells)
                {
                    if (x1 - x0 <= 2f * shell.Inset) continue;

                    AddGrid(edge, path, settings, sampler, x0 + shell.Inset, x1 - shell.Inset, shell.Start,
                        plungeDistance, Mathf.Max(shell.Lift, 1e-3f), shell.Growth, shell.PatternShift);
                }
            }
            Data.ToMesh(mesh);

            // The weld line runs across the whole lip, piers included: the feeding water ends there.
            weldLine.Clear();
            weldNormals.Clear();
            Vector3 forward = edge.FrameToWorld.MultiplyVector(Vector3.forward);
            Vector2 normal = new Vector2(forward.x, forward.z).normalized;
            Vector2 start = path[0].Surface;
            int steps = Mathf.Max(1, Mathf.CeilToInt(edge.Width / WeldStep));
            for (int step = 0; step <= steps; step++)
            {
                float x = Mathf.Lerp(-0.5f * edge.Width, 0.5f * edge.Width, step / (float)steps);
                Vector3 world = edge.FrameToWorld.MultiplyPoint3x4(new Vector3(x, start.y, start.x));
                weldLine.Add(new Vector2(world.x, world.z));
                weldNormals.Add(normal);
            }
        }

        // Adds one grid between x0 and x1 for the path part between two downstream distances. A lift above 0 makes a
        // foam shell.
        private static void AddGrid(in WaterFallEdge edge, IReadOnlyList<WaterFallPoint> path,
            WaterFallSettings settings, WaterSurfaceSampler sampler, float x0, float x1, float fromDistance,
            float toDistance, float lift, float liftGrowth, float patternShift)
        {
            int across = Mathf.Max(1, Mathf.CeilToInt((x1 - x0) / settings.ColumnSpacing));
            bool shell = lift > 0f;
            float streakRate = StreakRate(path, settings);
            // The distance to the end of this part, so a part that ends before the path end fades and calms there.
            float totalDistance = 0f;
            foreach (WaterFallPoint point in path)
            {
                if (point.Surface.x >= fromDistance && point.Surface.x <= toDistance) totalDistance = point.Distance;
            }
            float halfWidth = Mathf.Max(0.5f * edge.Width, 0.01f);
            // The vertex data of the feeding water on the weld line, one value for each column.
            ColumnFlow.Clear();
            ColumnRegime.Clear();
            WaterFallPoint start = path[0];
            for (int column = 0; column <= across; column++)
            {
                float x = Mathf.Lerp(x0, x1, column / (float)across);
                Vector3 weld = edge.FrameToWorld.MultiplyPoint3x4(new Vector3(x, start.Surface.y, start.Surface.x));
                AddWeldValues(edge, sampler, weld, start.Speed);
            }
            int rows = 0;
            int first = Data.Vertices.Count;
            foreach (WaterFallPoint point in path)
            {
                float downstream = point.Surface.x;
                if (downstream < fromDistance || downstream > toDistance) continue;

                float end = totalDistance - point.Distance;
                Vector2 surface = point.Surface + point.Normal * (lift + liftGrowth * Mathf.Clamp01(point.Progress));
                for (int column = 0; column <= across; column++)
                {
                    float x = Mathf.Lerp(x0, x1, column / (float)across);
                    // The water rubs the walls only past the lip.
                    float wall = edge.Walls && downstream >= 0f ? Mathf.Min(x - x0, x1 - x) : 99f;
                    // Past the plunge the flow turns out to the sides, as it enters the water below.
                    float spread = RunOutSpread * Mathf.Deg2Rad * Mathf.Clamp(x / halfWidth, -1f, 1f) *
                        Mathf.Clamp01(point.Progress - 1f);
                    Data.Vertices.Add(new Vector3(x, surface.y, surface.x));
                    Data.Normals.Add(new Vector3(0f, point.Normal.y, point.Normal.x));
                    // The flow runs down the bitangent, toward frame +Z.
                    Data.Tangents.Add(new Vector4(Mathf.Cos(spread), 0f, -Mathf.Sin(spread), -1f));
                    Data.Body.Add(ColumnFlow[column]);
                    Vector2 regime = ColumnRegime[column];
                    Data.Regime.Add(new Vector4(regime.x, regime.y, patternShift, 0f));
                    Data.Flow.Add(new Vector4(streakRate, point.Speed,
                        point.Brink, point.Travel));
                    Data.Sheet.Add(new Vector4(x + patternShift, point.Stretch, point.Progress, point.Distance));
                    Data.Extra.Add(new Vector4(wall, point.Thickness, end, shell ? 1f : 0f));
                }
                if (rows > 0)
                {
                    int a = first + (rows - 1) * (across + 1);
                    int b = a + across + 1;
                    for (int column = 0; column < across; column++)
                    {
                        Data.AddQuad(a + column, a + column + 1, b + column, b + column + 1);
                    }
                }
                rows++;
            }
        }

        // The body look of the sheet takes the vertex data of the feeding water at the weld line: flow, alpha and
        // spacing (UV0) and regime (UV1). Without a sampler, or off the body mesh, the edge gives the flow.
        private static void AddWeldValues(in WaterFallEdge edge, WaterSurfaceSampler sampler, Vector3 weld,
            float speed)
        {
            if (sampler != null && sampler.TrySample(weld, out Vector4 flow, out Vector2 regime))
            {
                ColumnFlow.Add(flow);
                ColumnRegime.Add(regime);
                return;
            }
            Vector2 worldFlow = edge.FlowDirection * (speed * edge.Scale);
            ColumnFlow.Add(new Vector4(worldFlow.x, worldFlow.y, 1f, edge.LipSpacing));
            ColumnRegime.Add(edge.LipRegime);
        }

        /// <summary>
        /// Builds the sheet of a crest: the path swept along a curved lip. Each lip point has its own frame: the
        /// outward normal is downstream and the world up is up. The columns are equal steps along the lip. The mesh
        /// is in the local space of the transform that worldToLocal belongs to. The weld line gets the world XZ points
        /// of the sheet start and the normals toward the fall.
        /// </summary>
        internal static void BuildCrestMesh(Mesh mesh, in WaterFallEdge edge, IReadOnlyList<WaterFallPoint> path,
            int plunge, WaterFallSettings settings, IReadOnlyList<Vector3> lip, IReadOnlyList<Vector2> normals,
            Matrix4x4 worldToLocal, WaterSurfaceSampler sampler, List<Vector2> weldLine, List<Vector2> weldNormals)
        {
            Data.Clear();
            ResampleLip(lip, normals, settings.ColumnSpacing);
            float length = LipArc[LipArc.Count - 1];
            float plungeDistance = path[plunge].Surface.x;
            AddCrestGrid(edge, path, settings, worldToLocal, sampler, 0f, length, -settings.SheetStart,
                plungeDistance + settings.RunOut, 0f, 0f, 0f);
            foreach (WaterFallShell shell in settings.Shells)
            {
                if (length <= 2f * shell.Inset) continue;

                AddCrestGrid(edge, path, settings, worldToLocal, sampler, shell.Inset, length - shell.Inset,
                    shell.Start, plungeDistance, Mathf.Max(shell.Lift, 1e-3f), shell.Growth, shell.PatternShift);
            }
            Data.ToMesh(mesh);

            weldLine.Clear();
            weldNormals.Clear();
            float start = path[0].Surface.x;
            for (int column = 0; column < LipPoints.Count; column++)
            {
                Vector3 point = LipPoints[column];
                Vector2 normal = LipNormals[column];
                weldLine.Add(new Vector2(point.x, point.z) + normal * start);
                weldNormals.Add(normal);
            }
        }

        private static readonly List<Vector3> LipPoints = new();
        private static readonly List<Vector2> LipNormals = new();
        private static readonly List<float> LipArc = new();
        private static readonly List<Vector4> ColumnFlow = new();
        private static readonly List<Vector2> ColumnRegime = new();

        // Equal steps along the lip, at most the column spacing apart, with normalised normals.
        private static void ResampleLip(IReadOnlyList<Vector3> lip, IReadOnlyList<Vector2> normals, float spacing)
        {
            LipPoints.Clear();
            LipNormals.Clear();
            LipArc.Clear();
            var arc = new float[lip.Count];
            for (int index = 1; index < lip.Count; index++)
            {
                arc[index] = arc[index - 1] + Vector3.Distance(lip[index - 1], lip[index]);
            }
            float length = arc[arc.Length - 1];
            int columns = Mathf.Max(1, Mathf.CeilToInt(length / Mathf.Max(spacing, 0.05f)));
            int segment = 1;
            for (int column = 0; column <= columns; column++)
            {
                float target = length * column / columns;
                while (segment < lip.Count - 1 && arc[segment] < target)
                {
                    segment++;
                }
                float t = Mathf.InverseLerp(arc[segment - 1], arc[segment], target);
                LipPoints.Add(Vector3.Lerp(lip[segment - 1], lip[segment], t));
                LipNormals.Add(Vector2.Lerp(normals[segment - 1], normals[segment], t).normalized);
                LipArc.Add(target);
            }
            // The columns must run to the right of the flow, as frame +X does, or the triangles face backward.
            Vector2 middle = LipNormals[LipNormals.Count / 2];
            Vector3 run = LipPoints[LipPoints.Count - 1] - LipPoints[0];
            if (middle.y * run.x - middle.x * run.z >= 0f) return;

            LipPoints.Reverse();
            LipNormals.Reverse();
            for (int index = 0; index < LipArc.Count; index++)
            {
                LipArc[index] = length - LipArc[LipArc.Count - 1 - index];
            }
        }

        // One grid of the crest sheet between two distances along the lip, for the path part between two downstream
        // distances. A lift above 0 makes a foam shell. The data channels are the same as in AddGrid.
        private static void AddCrestGrid(in WaterFallEdge edge, IReadOnlyList<WaterFallPoint> path,
            WaterFallSettings settings, Matrix4x4 worldToLocal, WaterSurfaceSampler sampler, float fromArc,
            float toArc, float fromDistance, float toDistance, float lift, float liftGrowth, float patternShift)
        {
            bool shell = lift > 0f;
            float streakRate = StreakRate(path, settings);
            float totalDistance = 0f;
            foreach (WaterFallPoint point in path)
            {
                if (point.Surface.x >= fromDistance && point.Surface.x <= toDistance) totalDistance = point.Distance;
            }
            int firstColumn = 0;
            while (firstColumn < LipArc.Count - 1 && LipArc[firstColumn] < fromArc)
            {
                firstColumn++;
            }
            int lastColumn = LipArc.Count - 1;
            while (lastColumn > firstColumn && LipArc[lastColumn] > toArc)
            {
                lastColumn--;
            }
            int across = lastColumn - firstColumn;
            if (across < 1) return;

            // The vertex data of the feeding water on the weld line, one value for each column. A crest has no
            // flow direction of its own: the edge flow is the outward normal.
            ColumnFlow.Clear();
            ColumnRegime.Clear();
            WaterFallPoint start = path[0];
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                Vector3 lipPoint = LipPoints[column];
                Vector2 normal = LipNormals[column];
                var weld = new Vector3(lipPoint.x + normal.x * start.Surface.x, start.Surface.y,
                    lipPoint.z + normal.y * start.Surface.x);
                if (sampler != null && sampler.TrySample(weld, out Vector4 sampled, out Vector2 regime))
                {
                    ColumnFlow.Add(sampled);
                    ColumnRegime.Add(regime);
                }
                else
                {
                    Vector2 worldFlow = normal * start.Speed;
                    ColumnFlow.Add(new Vector4(worldFlow.x, worldFlow.y, 1f, edge.LipSpacing));
                    ColumnRegime.Add(edge.LipRegime);
                }
            }

            int rows = 0;
            int first = Data.Vertices.Count;
            foreach (WaterFallPoint point in path)
            {
                float downstream = point.Surface.x;
                if (downstream < fromDistance || downstream > toDistance) continue;

                float end = totalDistance - point.Distance;
                Vector2 surface = point.Surface + point.Normal * (lift + liftGrowth * Mathf.Clamp01(point.Progress));
                for (int column = firstColumn; column <= lastColumn; column++)
                {
                    Vector2 normal = LipNormals[column];
                    var forward = new Vector3(normal.x, 0f, normal.y);
                    // The across direction, as frame +X is to the right of the flow.
                    var right = new Vector3(normal.y, 0f, -normal.x);
                    Vector3 lip = LipPoints[column];
                    var world = new Vector3(lip.x + forward.x * surface.x, surface.y, lip.z + forward.z * surface.x);
                    Vector3 surfaceNormal = forward * point.Normal.x + Vector3.up * point.Normal.y;
                    Data.Vertices.Add(worldToLocal.MultiplyPoint3x4(world));
                    Data.Normals.Add(worldToLocal.MultiplyVector(surfaceNormal).normalized);
                    Vector3 tangent = worldToLocal.MultiplyVector(right).normalized;
                    Data.Tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, -1f));
                    Data.Body.Add(ColumnFlow[column - firstColumn]);
                    Vector2 regime = ColumnRegime[column - firstColumn];
                    Data.Regime.Add(new Vector4(regime.x, regime.y, patternShift, 0f));
                    Data.Flow.Add(new Vector4(streakRate, point.Speed,
                        point.Brink, point.Travel));
                    Data.Sheet.Add(new Vector4(LipArc[column] + patternShift, point.Stretch, point.Progress,
                        point.Distance));
                    Data.Extra.Add(new Vector4(99f, point.Thickness, end, shell ? 1f : 0f));
                }
                if (rows > 0)
                {
                    int a = first + (rows - 1) * (across + 1);
                    int b = a + across + 1;
                    for (int column = 0; column < across; column++)
                    {
                        Data.AddQuad(a + column, a + column + 1, b + column, b + column + 1);
                    }
                }
                rows++;
            }
        }

        private sealed class MeshData
        {
            public readonly List<Vector3> Vertices = new();
            public readonly List<Vector3> Normals = new();
            public readonly List<Vector4> Tangents = new();
            public readonly List<Vector4> Body = new();
            public readonly List<Vector4> Regime = new();
            public readonly List<Vector4> Flow = new();
            public readonly List<Vector4> Sheet = new();
            public readonly List<Vector4> Extra = new();
            private readonly List<int> _triangles = new();

            public void Clear()
            {
                Vertices.Clear();
                Normals.Clear();
                Tangents.Clear();
                Body.Clear();
                Regime.Clear();
                Flow.Clear();
                Sheet.Clear();
                Extra.Clear();
                _triangles.Clear();
            }

            // Row a is upstream of row b. The winding faces up and out of the sheet.
            public void AddQuad(int a0, int a1, int b0, int b1)
            {
                _triangles.Add(a0);
                _triangles.Add(b1);
                _triangles.Add(a1);
                _triangles.Add(a0);
                _triangles.Add(b0);
                _triangles.Add(b1);
            }

            public void ToMesh(Mesh mesh)
            {
                mesh.Clear();
                mesh.indexFormat = Vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.SetVertices(Vertices);
                mesh.SetNormals(Normals);
                mesh.SetTangents(Tangents);
                mesh.SetUVs(0, Body);
                mesh.SetUVs(1, Regime);
                mesh.SetUVs(2, Flow);
                mesh.SetUVs(3, Sheet);
                mesh.SetUVs(4, Extra);
                mesh.subMeshCount = 1;
                mesh.SetTriangles(_triangles, 0);
                mesh.RecalculateBounds();
                Bounds bounds = mesh.bounds;
                bounds.Expand(2f * BoundsPadding);
                mesh.bounds = bounds;
            }
        }
        #endregion
    }
}
