using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Places the pieces of the kit style of a cliff wall. The builder finds the terrain face in front of the spline and
    /// keeps a fine map of it. It then adds large, medium and small pieces until the map is covered. Each piece has
    /// profiles of the views that it can show (see <see cref="CliffWallKitPiece"/>), so the builder buries its cut side in
    /// the terrain and can turn it many ways. The long axis of each piece follows the bedding of the wall, which changes
    /// slowly along the wall, and parts of the wall stand out as buttresses. Foot boulders lie on the ground in front of
    /// the face, with their cut side down.
    /// </summary>
    internal static class CliffWallKitBuilder
    {
        /// <summary>One copy of a kit prefab, in world space.</summary>
        internal struct Placement
        {
            public GameObject Prefab;
            public Vector3 Position;
            public Quaternion Rotation;
            public float Scale;
        }

        // One column of the face frame: a point on the spline, the outward direction and the terrain profile.
        private struct Column
        {
            public Vector3 Origin;
            public Vector3 Tangent;
            public Vector3 Outward;
            public float RimHeight;
            public float FootHeight;
            public int RimStep;
            public int FootStep;
            public float Lean;
            public bool Active;
        }

        // The terrain face by column and by meter of height. Face is the distance of the face in front of the spline.
        private sealed class FaceMap
        {
            public int Columns;
            public int Rows;
            public float Bottom;
            public bool Closed;
            public float[] Face;
            public bool[] Needed;
            public bool[] Covered;
            public int NeededCount;
            public int CoveredCount;

            public int Column(int column)
            {
                if (Closed) return (column % Columns + Columns) % Columns;
                return column >= 0 && column < Columns ? column : -1;
            }

            public int Row(float height) => Mathf.Clamp(Mathf.FloorToInt((height - Bottom) / CellSize), 0, Rows - 1);
        }

        // One tested place of a piece.
        private struct Candidate
        {
            public CliffWallKitPiece Piece;
            public CliffWallKitPiece.View View;
            public float Scale;
            public Vector3 Position;
            public Quaternion Frame;
            public int Column;
            public float Depth;
            public float PivotHeight;
            public float Score;
        }

        private const float ColumnSpacing = 0.25f;
        // Distance on both sides of the spline that decides the drop side, in meters.
        private const float DropSideReach = 6f;
        // Height of a map cell, in meters. The cells are as wide as the columns, so gaps this small still show in the map.
        private const float CellSize = 0.25f;
        // Least distance of the front of a piece in front of the face where it covers the face, in meters.
        private const float CoverMargin = 0.05f;
        // Share of the back of a piece that may stay in front of the face.
        private const float ExposedShare = 0.04f;
        // Share of the front of a piece that may stand past the largest distance from the face.
        private const float ProudShare = 0.1f;
        // Largest share of a piece past the end of the face. Its back shows there.
        private const float OutsideShare = 0.08f;
        // Pieces tested for each open cell. More give a tighter wall and a slower build.
        private const int Attempts = 8;
        // Surface points of a piece that a test reads. The chosen piece marks the map with all its points.
        private const int TestPoints = 260;
        // Share of the face that the large pieces and then the medium pieces cover.
        private const float LargeShare = 0.7f;
        private const float MediumShare = 0.9f;
        // Open cells in a row without a piece that end a fill. The rest of the face is too tight for that size.
        private const int FailureLimit = 200;
        // Tests of the relief layer for each piece that it wants.
        private const int ReliefAttempts = 4;
        // Score of a square meter that is already covered, against 1 for a new square meter.
        private const float OverlapCost = 0.35f;
        // Score of a square meter of cut or stretched surface that stays in front of the face.
        private const float HiddenCost = 2f;
        // Score of a square meter of the back of a piece that stays in front of the face.
        private const float BackCost = 3f;
        // Least new area of a piece, as a share of its front area and in square meters.
        private const float SmallestGain = 0.06f;
        private const float SmallestArea = 0.12f;
        // Lengths of the changes along the wall, in meters.
        private const float BeddingLength = 70f;
        private const float ButtressLength = 34f;
        private const float ReliefLength = 26f;
        private const float ReliefHeight = 15f;

        /// <summary>Adds the placements of the wall pieces and the foot boulders along the spline points.</summary>
        /// <returns>The share of the terrain face that the wall pieces cover, from 0 to 1.</returns>
        internal static float Build(Vector3[] points, bool closed, RockFaceSettings settings, CliffWallKit kit,
            List<CliffWallKitPiece> pieces, List<CliffWallKitPiece> footPieces, TerrainHeightSampler terrain,
            List<Placement> result)
        {
            if (pieces.Count == 0 && footPieces.Count == 0) return 0f;
            int steps = Mathf.CeilToInt((settings.SearchInward + settings.SearchOutward) / settings.ProfileStep) + 1;
            Column[] columns = BuildFrame(points, closed, settings, kit, terrain, steps, out float[] profiles);
            if (columns == null) return 0f;
            var random = new System.Random(kit.Seed);
            FaceMap map = BuildMap(columns, profiles, steps, settings, closed);
            if (pieces.Count > 0) FillFace(map, columns, kit, pieces, random, result);
            if (footPieces.Count > 0 && kit.FootSpacing > 0f)
            {
                PlaceFootBoulders(columns, profiles, steps, settings, kit, footPieces, terrain, random, result);
            }
            return map.NeededCount > 0 ? (float)map.CoveredCount / map.NeededCount : 0f;
        }

        #region Face Frame
        // Columns every half meter along the spline, each with the terrain profile across the cliff.
        private static Column[] BuildFrame(Vector3[] points, bool closed, RockFaceSettings settings, CliffWallKit kit,
            TerrainHeightSampler terrain, int steps, out float[] profiles)
        {
            float pointSpacing = Vector3.Distance(points[0], points[1]);
            int stride = Mathf.Max(1, Mathf.RoundToInt(ColumnSpacing / Mathf.Max(0.01f, pointSpacing)));
            int count = (points.Length - 1) / stride + 1;
            var columns = new Column[count];
            profiles = new float[count * steps];
            for (int index = 0; index < count; index++)
            {
                int point = index * stride;
                int before = closed ? (point - stride + points.Length) % points.Length : Mathf.Max(0, point - stride);
                int after = closed ? (point + stride) % points.Length : Mathf.Min(points.Length - 1, point + stride);
                Vector3 tangent = points[after] - points[before];
                tangent.y = 0f;
                tangent = tangent.sqrMagnitude > 1e-6f ? tangent.normalized : Vector3.forward;
                columns[index] = new Column
                {
                    Origin = new Vector3(points[point].x, 0f, points[point].z),
                    Tangent = tangent,
                    Outward = new Vector3(tangent.z, 0f, -tangent.x)
                };
            }
            ChooseDropSide(columns, settings, terrain);

            float minimumDrop = Mathf.Tan(settings.FaceAngle * Mathf.Deg2Rad) * settings.ProfileStep;
            float shoulderDrop = Mathf.Tan(kit.ShoulderAngle * Mathf.Deg2Rad) * settings.ProfileStep;
            int active = 0;
            for (int index = 0; index < count; index++)
            {
                ref Column column = ref columns[index];
                int offset = index * steps;
                for (int step = 0; step < steps; step++)
                {
                    Vector3 point = column.Origin + column.Outward * Distance(step, settings);
                    profiles[offset + step] = terrain.TrySampleHeight(point.x, point.z, out float height)
                        ? height
                        : float.NaN;
                }
                // The face is the tallest run of samples that drop faster than the face angle.
                int bestStart = -1;
                int bestEnd = -1;
                float bestDrop = 0f;
                int runStart = -1;
                for (int step = 1; step <= steps; step++)
                {
                    bool steep = step < steps && !float.IsNaN(profiles[offset + step]) &&
                                 !float.IsNaN(profiles[offset + step - 1]) &&
                                 profiles[offset + step - 1] - profiles[offset + step] >= minimumDrop;
                    if (steep && runStart < 0)
                    {
                        runStart = step - 1;
                    }
                    if (steep || runStart < 0) continue;
                    float drop = profiles[offset + runStart] - profiles[offset + step - 1];
                    if (drop > bestDrop)
                    {
                        bestDrop = drop;
                        bestStart = runStart;
                        bestEnd = step - 1;
                    }
                    runStart = -1;
                }
                float share = count > 1 ? index / (float)(count - 1) : 0f;
                if (bestDrop < settings.MinimumFaceHeight || RockFaceBuilder.IsExcluded(share, settings)) continue;
                float faceRise = profiles[offset + bestStart] - profiles[offset + bestEnd];
                float faceRun = (bestEnd - bestStart) * settings.ProfileStep;
                column.Lean = Mathf.Atan2(faceRun, Mathf.Max(0.01f, faceRise)) * Mathf.Rad2Deg;
                // The rounded brow above the steep run is part of the face, or it stays as a bare bank over the rocks.
                while (bestStart > 0 && profiles[offset + bestStart - 1] - profiles[offset + bestStart] >= shoulderDrop)
                {
                    bestStart--;
                }
                column.Active = true;
                column.RimStep = bestStart;
                column.FootStep = bestEnd;
                column.RimHeight = profiles[offset + bestStart];
                column.FootHeight = profiles[offset + bestEnd];
                active++;
            }
            return active > 0 ? columns : null;
        }

        // The outward direction of all columns points to the lower side of the spline.
        private static void ChooseDropSide(Column[] columns, RockFaceSettings settings, TerrainHeightSampler terrain)
        {
            float vote = 0f;
            if (settings.DropSide == RockFaceDropSide.Auto)
            {
                foreach (Column column in columns)
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

        // Distance of a profile sample in front of the spline, in meters.
        private static float Distance(int step, RockFaceSettings settings)
        {
            return step * settings.ProfileStep - settings.SearchInward;
        }

        // Distance in front of the spline where the face is at the height. Above the rim it is the rim distance, below
        // the foot the foot distance.
        private static float FaceDistance(Column column, float[] profiles, int offset, float height,
            RockFaceSettings settings)
        {
            if (height >= column.RimHeight) return Distance(column.RimStep, settings);
            for (int step = column.RimStep; step < column.FootStep; step++)
            {
                float upper = profiles[offset + step];
                float lower = profiles[offset + step + 1];
                if (height <= lower) continue;
                float share = (upper - height) / Mathf.Max(1e-4f, upper - lower);
                return Distance(step, settings) + settings.ProfileStep * share;
            }
            return Distance(column.FootStep, settings);
        }

        // The map of the face: the face distance of each column at each meter of height.
        private static FaceMap BuildMap(Column[] columns, float[] profiles, int steps, RockFaceSettings settings, bool closed)
        {
            float bottom = float.MaxValue;
            float top = float.MinValue;
            foreach (Column column in columns)
            {
                if (!column.Active) continue;
                bottom = Mathf.Min(bottom, column.FootHeight);
                top = Mathf.Max(top, column.RimHeight);
            }
            var map = new FaceMap
            {
                Columns = columns.Length,
                Rows = Mathf.CeilToInt((top - bottom) / CellSize) + 1,
                Bottom = bottom,
                Closed = closed
            };
            map.Face = new float[map.Columns * map.Rows];
            map.Needed = new bool[map.Columns * map.Rows];
            map.Covered = new bool[map.Columns * map.Rows];
            for (int index = 0; index < columns.Length; index++)
            {
                Column column = columns[index];
                for (int row = 0; row < map.Rows; row++)
                {
                    int cell = index + row * map.Columns;
                    if (!column.Active)
                    {
                        map.Face[cell] = float.NaN;
                        continue;
                    }
                    float height = bottom + (row + 0.5f) * CellSize;
                    map.Face[cell] = FaceDistance(column, profiles, index * steps, height, settings);
                    if (height > column.RimHeight || height < column.FootHeight) continue;
                    map.Needed[cell] = true;
                    map.NeededCount++;
                }
            }
            return map;
        }
        #endregion

        #region Pieces
        // Builds the wall in two layers. The backing layer lies flat on the face and covers the terrain, large pieces first
        // and small pieces last. The relief layer adds tilted pieces on top, as ledges, and needs no new area.
        private static void FillFace(FaceMap map, Column[] columns, CliffWallKit kit, List<CliffWallKitPiece> pieces,
            System.Random random, List<Placement> result)
        {
            Fill(map, columns, kit, pieces, kit.LargeSize, LargeShare, random, result);
            Fill(map, columns, kit, pieces, kit.MediumSize, MediumShare, random, result);
            Fill(map, columns, kit, pieces, kit.SmallSize, kit.TargetCoverage, random, result);
            AddRelief(map, columns, kit, pieces, random, result);
        }

        // Adds tilted pieces at random places of the face. They show the sides of the meshes that the flat layer cannot.
        private static void AddRelief(FaceMap map, Column[] columns, CliffWallKit kit, List<CliffWallKitPiece> pieces,
            System.Random random, List<Placement> result)
        {
            var cells = new List<int>();
            for (int cell = 0; cell < map.Needed.Length; cell++)
            {
                if (map.Needed[cell]) cells.Add(cell);
            }
            if (cells.Count == 0) return;
            float area = cells.Count * ColumnSpacing * CellSize;
            int goal = Mathf.RoundToInt(area / 100f * kit.ReliefDensity);
            var limits = new List<float>();
            var proud = new List<float>();
            int added = 0;
            for (int attempt = 0; attempt < goal * ReliefAttempts && added < goal; attempt++)
            {
                if (result.Count >= kit.MaximumPieces) return;
                int cell = cells[random.Next(cells.Count)];
                CliffWallKitPiece piece = pieces[random.Next(pieces.Count)];
                float scale = Range(random, kit.MediumSize) / piece.Longest;
                if (scale < kit.ScaleLimits.x || scale > kit.ScaleLimits.y) continue;
                int index = cell % map.Columns;
                if (!columns[index].Active) continue;
                float height = map.Bottom + (cell / map.Columns + 0.5f) * CellSize;
                float top = Mathf.Min(height + Range(random, new Vector2(0.15f, 0.5f)) * piece.Longest * scale,
                    columns[index].RimHeight - kit.RimInset);
                CliffWallKitPiece.View view = piece.Views[random.Next(piece.Views.Length)];
                Candidate candidate = Test(map, columns, kit, piece, view, scale, index, top, true, random, limits, proud);
                if (candidate.Piece == null) continue;
                Mark(map, columns, candidate);
                AddPlacement(candidate, result);
                added++;
            }
        }

        private static void AddPlacement(in Candidate candidate, List<Placement> result)
        {
            result.Add(new Placement
            {
                Prefab = candidate.Piece.Prefab,
                Position = candidate.Position,
                Rotation = candidate.Frame * candidate.View.Rotation,
                Scale = candidate.Scale
            });
        }

        // Takes the open cells in random order, so no rows form. It tests some pieces, views and sizes at each cell and
        // keeps the one that covers the most new area for the least overlap.
        private static void Fill(FaceMap map, Column[] columns, CliffWallKit kit, List<CliffWallKitPiece> pieces,
            Vector2 sizes, float share, System.Random random, List<Placement> result)
        {
            var open = new List<int>();
            for (int cell = 0; cell < map.Needed.Length; cell++)
            {
                if (map.Needed[cell] && !map.Covered[cell]) open.Add(cell);
            }
            for (int index = open.Count - 1; index > 0; index--)
            {
                int other = random.Next(index + 1);
                (open[index], open[other]) = (open[other], open[index]);
            }
            var limits = new List<float>();
            var proud = new List<float>();
            int goal = Mathf.CeilToInt(map.NeededCount * share);
            int failures = 0;
            foreach (int cell in open)
            {
                if (map.CoveredCount >= goal || result.Count >= kit.MaximumPieces || failures >= FailureLimit) return;
                if (map.Covered[cell]) continue;

                int column = cell % map.Columns;
                float height = map.Bottom + (cell / map.Columns + 0.5f) * CellSize;
                var best = new Candidate { Score = float.MinValue };
                for (int attempt = 0; attempt < Attempts; attempt++)
                {
                    CliffWallKitPiece piece = pieces[random.Next(pieces.Count)];
                    float scale = Range(random, sizes) / piece.Longest;
                    if (scale < kit.ScaleLimits.x || scale > kit.ScaleLimits.y) continue;
                    // The first view looks straight at the side opposite the cut, so the piece lies flat on the face.
                    CliffWallKitPiece.View view = piece.Views[0];
                    float wide = piece.Longest * scale;
                    int index = map.Column(column + Mathf.RoundToInt(Range(random, new Vector2(-0.3f, 0.3f)) * wide / ColumnSpacing));
                    if (index < 0 || !columns[index].Active) continue;
                    float top = Mathf.Min(height + Range(random, new Vector2(0.15f, 0.7f)) * wide,
                        columns[index].RimHeight - kit.RimInset);
                    Candidate candidate = Test(map, columns, kit, piece, view, scale, index, top, false, random, limits, proud);
                    if (candidate.Score > best.Score) best = candidate;
                }
                if (best.Piece == null)
                {
                    failures++;
                    continue;
                }
                failures = 0;
                Mark(map, columns, best);
                AddPlacement(best, result);
            }
        }

        // The direction of the long axis of the pieces at a place on the wall, in degrees from the horizontal. Rock has
        // bedding that changes slowly along a cliff, and buttresses where the joints run upright.
        private static float Bedding(int index, CliffWallKit kit)
        {
            float along = index * ColumnSpacing;
            float dip = (Mathf.PerlinNoise(along / BeddingLength + kit.Seed * 0.37f, 3.7f) - 0.5f) * 2f * kit.BeddingDip;
            float buttress = Mathf.PerlinNoise(along / ButtressLength + 11.3f, kit.Seed * 0.19f);
            return buttress > Mathf.Lerp(0.75f, 0.25f, kit.ButtressShare) ? 90f + dip : dip;
        }

        // Largest distance of the front in front of the face at a place. Parts of the wall stand out and parts stand back.
        private static float ProudLimit(int index, float height, CliffWallKit kit)
        {
            float noise = Mathf.PerlinNoise(index * ColumnSpacing / ReliefLength + kit.Seed * 0.61f, height / ReliefHeight);
            return Mathf.Lerp(kit.ProudRange.x, kit.ProudRange.y, Mathf.SmoothStep(0.25f, 0.75f, noise));
        }

        // Sets a view of a piece at the column with its top at the height. Its depth comes from its surfaces: its back
        // goes behind the face, and its front stays near the face.
        private static Candidate Test(FaceMap map, Column[] columns, CliffWallKit kit, CliffWallKitPiece piece,
            CliffWallKitPiece.View view, float scale, int index, float top, bool relief, System.Random random,
            List<float> limits, List<float> proud)
        {
            limits.Clear();
            proud.Clear();
            Column column = columns[index];
            var result = new Candidate { Score = float.MinValue };
            float turn = Range(random, new Vector2(-kit.Turn, kit.Turn));
            float pitch = column.Lean * kit.FollowFace + Range(random, new Vector2(-kit.Tilt, kit.Tilt));
            float roll = Bedding(index, kit) - view.LongAngle + Range(random, new Vector2(-kit.Tilt, kit.Tilt));
            if (random.NextDouble() < kit.FlipChance) roll += 180f;
            // Front (+Z) out of the face. A negative pitch leans the top of the piece back into the face.
            Quaternion frame = Quaternion.LookRotation(Quaternion.Euler(0f, turn, 0f) * column.Outward) *
                               Quaternion.Euler(-pitch, 0f, roll);
            int count = view.Front.Length;
            int stride = Mathf.Max(1, count / TestPoints);
            float high = float.MinValue;
            for (int point = 0; point < count; point += stride)
            {
                high = Mathf.Max(high, (frame * (view.Front[point] * scale)).y);
                high = Mathf.Max(high, (frame * (view.Back[point] * scale)).y);
            }
            float pivotHeight = top - high;

            int outside = 0;
            int tested = 0;
            for (int point = 0; point < count; point += stride)
            {
                tested++;
                Vector3 backPoint = frame * (view.Back[point] * scale);
                if (!Surface(map, columns, column, index, backPoint, pivotHeight + backPoint.y, out float backDistance, out float backFace, out _))
                {
                    outside++;
                    continue;
                }
                if (!float.IsNaN(backFace)) limits.Add(backFace - backDistance);
                Vector3 frontPoint = frame * (view.Front[point] * scale);
                if (Surface(map, columns, column, index, frontPoint, pivotHeight + frontPoint.y, out float frontDistance, out float frontFace, out _) &&
                    !float.IsNaN(frontFace))
                {
                    proud.Add(frontDistance - frontFace);
                }
            }
            if (outside > tested * OutsideShare || limits.Count == 0 || proud.Count == 0) return result;

            limits.Sort();
            proud.Sort();
            float depth = limits[Mathf.FloorToInt(limits.Count * ExposedShare)] - kit.BackDepth;
            float limit = relief ? kit.ProudRange.y : ProudLimit(index, top, kit);
            depth = Mathf.Min(depth, limit - proud[Mathf.FloorToInt(proud.Count * (1f - ProudShare))]);

            int fresh = 0;
            int repeat = 0;
            int hidden = 0;
            int exposed = outside;
            for (int point = 0; point < count; point += stride)
            {
                Vector3 backPoint = frame * (view.Back[point] * scale);
                if (Surface(map, columns, column, index, backPoint, pivotHeight + backPoint.y, out float backDistance, out float backFace, out _) &&
                    !float.IsNaN(backFace) && backDistance + depth > backFace)
                {
                    exposed++;
                }
                Vector3 frontPoint = frame * (view.Front[point] * scale);
                if (!Surface(map, columns, column, index, frontPoint, pivotHeight + frontPoint.y, out float distance, out float face, out int cell)) continue;
                if (float.IsNaN(face) || distance + depth < face + CoverMargin) continue;
                if (view.Hidden[point]) hidden++;
                if (!map.Needed[cell]) continue;
                if (map.Covered[cell]) repeat++;
                else fresh++;
            }
            float pointArea = view.Cell * view.Cell * scale * scale * stride;
            float newArea = fresh * pointArea;
            if (!relief && newArea < Mathf.Max(SmallestArea, view.Area * scale * scale * SmallestGain)) return result;

            result.Piece = piece;
            result.View = view;
            result.Scale = scale;
            result.Frame = frame;
            result.Column = index;
            result.Depth = depth;
            result.PivotHeight = pivotHeight;
            result.Position = column.Origin + column.Outward * depth + Vector3.up * pivotHeight;
            result.Score = newArea - (OverlapCost * repeat + HiddenCost * hidden + BackCost * exposed) * pointArea;
            return result;
        }

        // Marks all cells that the front of the chosen piece covers.
        private static void Mark(FaceMap map, Column[] columns, Candidate candidate)
        {
            CliffWallKitPiece.View view = candidate.View;
            Column column = columns[candidate.Column];
            float spacing = view.Cell * candidate.Scale;
            // Surface points finer than the map need no spread. A larger spread would count gaps as covered.
            int reach = spacing > CellSize ? Mathf.CeilToInt((spacing - CellSize) * 0.5f / CellSize) : 0;
            for (int point = 0; point < view.Front.Length; point++)
            {
                Vector3 frontPoint = candidate.Frame * (view.Front[point] * candidate.Scale);
                float height = candidate.PivotHeight + frontPoint.y;
                if (!Surface(map, columns, column, candidate.Column, frontPoint, height, out float distance, out _, out int cell)) continue;
                int row = cell / map.Columns;
                int columnIndex = cell % map.Columns;
                for (int rowStep = -reach; rowStep <= reach; rowStep++)
                {
                    int nearRow = row + rowStep;
                    if (nearRow < 0 || nearRow >= map.Rows) continue;
                    for (int step = -reach; step <= reach; step++)
                    {
                        int nearColumn = map.Column(columnIndex + step);
                        if (nearColumn < 0) continue;
                        int near = nearColumn + nearRow * map.Columns;
                        if (!map.Needed[near] || map.Covered[near]) continue;
                        if (distance + candidate.Depth < map.Face[near] + CoverMargin) continue;
                        map.Covered[near] = true;
                        map.CoveredCount++;
                    }
                }
            }
        }

        // Reads the map under a point of a piece. Distance is in front of the spline of the column under the point, as
        // if the pivot of the piece was on the spline. False when the point is past the end of the face.
        private static bool Surface(FaceMap map, Column[] columns, Column pivot, int pivotColumn, Vector3 offset, float height,
            out float distance, out float face, out int cell)
        {
            distance = 0f;
            face = 0f;
            cell = 0;
            float along = Vector3.Dot(offset, pivot.Tangent);
            int index = map.Column(pivotColumn + Mathf.RoundToInt(along / ColumnSpacing));
            if (index < 0 || !columns[index].Active) return false;
            Column column = columns[index];
            Vector3 world = pivot.Origin + new Vector3(offset.x, 0f, offset.z);
            distance = Vector3.Dot(world - column.Origin, column.Outward);
            int row = map.Row(height);
            // Below the foot the point is in the ground, so nothing limits it.
            face = height < column.FootHeight ? float.NaN : map.Face[index + row * map.Columns];
            cell = index + row * map.Columns;
            return true;
        }

        // Boulders on the ground in front of the foot of the face.
        private static void PlaceFootBoulders(Column[] columns, float[] profiles, int steps, RockFaceSettings settings,
            CliffWallKit kit, List<CliffWallKitPiece> pieces, TerrainHeightSampler terrain, System.Random random,
            List<Placement> result)
        {
            float length = (columns.Length - 1) * ColumnSpacing;
            float u = Range(random, new Vector2(0f, kit.FootSpacing));
            while (u <= length)
            {
                int index = ColumnAt(columns, u);
                u += kit.FootSpacing * Range(random, new Vector2(0.5f, 1.5f));
                if (index < 0) continue;
                Column column = columns[index];
                CliffWallKitPiece piece = pieces[random.Next(pieces.Count)];
                float scale = Range(random, kit.FootScale);
                float distance = Distance(column.FootStep, settings) + Range(random, kit.FootDistance);
                Vector3 position = column.Origin + column.Outward * distance +
                                   column.Tangent * Range(random, new Vector2(-1f, 1f));
                if (!terrain.TrySampleHeight(position.x, position.z, out float ground)) continue;
                // The cut side goes down into the ground.
                Quaternion upright = piece.CutDirection == Vector3.zero
                    ? Quaternion.identity
                    : Quaternion.FromToRotation(piece.CutDirection, Vector3.down);
                Quaternion rotation = Quaternion.Euler(Range(random, new Vector2(-kit.Tilt, kit.Tilt)),
                    Range(random, new Vector2(0f, 360f)), Range(random, new Vector2(-kit.Tilt, kit.Tilt))) * upright;
                Extents(piece, rotation, scale, column.Outward, out float low, out float high, out _, out _);
                position.y = ground - low - kit.FootSink * (high - low);
                result.Add(new Placement
                {
                    Prefab = piece.Prefab, Position = position, Rotation = rotation, Scale = scale
                });
            }
        }

        // Lowest and highest vertex offset of a turned and scaled piece, and its back and front along the direction.
        private static void Extents(CliffWallKitPiece piece, Quaternion rotation, float scale, Vector3 outward, out float low,
            out float high, out float back, out float front)
        {
            low = float.MaxValue;
            high = float.MinValue;
            back = float.MaxValue;
            front = float.MinValue;
            foreach (Vector3 vertex in piece.Vertices)
            {
                Vector3 offset = rotation * (vertex * scale);
                float along = Vector3.Dot(offset, outward);
                low = Mathf.Min(low, offset.y);
                high = Mathf.Max(high, offset.y);
                back = Mathf.Min(back, along);
                front = Mathf.Max(front, along);
            }
        }

        private static int ColumnAt(Column[] columns, float u)
        {
            int index = Mathf.Clamp(Mathf.RoundToInt(u / ColumnSpacing), 0, columns.Length - 1);
            return columns[index].Active ? index : -1;
        }

        private static float Range(System.Random random, Vector2 range)
        {
            return Mathf.Lerp(range.x, range.y, (float)random.NextDouble());
        }
        #endregion
    }
}
