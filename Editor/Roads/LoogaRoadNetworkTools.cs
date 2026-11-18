using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LoogaSoft.Terrain.Stamps;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Roads.Editor
{
    /// <summary>
    /// Layout tools of a <see cref="LoogaRoadNetwork"/>: junction heights, pad contacts, cross slope, merging,
    /// widening, simplifying, and the edits of the road planner pages. The planner pages save one JSON file for each
    /// edited pad (pads/&lt;id&gt;.json), road (roads/&lt;id&gt;.json) and bridge (bridges/&lt;id&gt;.json). Road ids
    /// are M&lt;index&gt; for the splines of the planned roads of the primary class, F&lt;index&gt; for the splines of
    /// its follow-ground roads and N&lt;n&gt; for new roads.
    /// </summary>
    /// <remarks>
    /// The tools move and turn the pads, level the moved pads, give the edited roads new knots and a new height
    /// profile, remove the deleted roads, add the new roads and link the road ends to the pads again. Knots that did
    /// not move keep their heights. <see cref="LoogaRoadLive"/> updates the slabs and the bridges after the stamp
    /// update.
    /// </remarks>
    public static class LoogaRoadNetworkTools
    {
        // The end markers of a new bridge.
        private const string BridgeStartName = "End A";
        private const string BridgeFinishName = "End B";
        private const string BridgeRootName = "Bridges";
        // A knot this close to an old knot keeps the handles of the old knot. A bridge end takes the height of a road
        // end this close to it.
        private const float KeptHandle = 0.05f;
        // A point within this distance of a road curve lies on it, so it takes its handles from a split of that curve.
        private const float OnCurve = 1f;
        // Two handles within this angle (degrees) of a straight line are aligned: the knot is smooth, not a corner.
        private const float AlignedAngle = 1f;
        // Tangent samples of each curve in the curve check.
        private const int CurveCheckSteps = 32;
        private const float BridgeEndReach = 3f;
        // ApplyNetwork: a road whose end lies within this distance of an edited road, a moved pad or an edited bridge
        // keeps the settle result. Other roads that the edits did not move keep their heights (meters). Points along
        // the edited roads are this far apart.
        private const float KeepReach = 12f;
        private const float ReachStep = 4f;
        /// <summary>Spacing of the exported river and lake outline points, in meters.</summary>
        public const float WaterStep = 2f;
        // Widen: sample step along a road; the cosine above which a road runs parallel to a pad side; how far a road
        // edge may lie from the pad side line and still count as in line; the shortest such stretch; how far beyond the
        // side ends the line still counts; and the length over which the move away from the pad eases out (meters).
        private const float WidenStep = 1f;
        private const float WidenParallel = 0.995f;
        private const float WidenLineTolerance = 0.75f;
        private const float WidenMinRun = 8f;
        private const float WidenSideReach = 40f;
        private const float WidenTaper = 25f;
        // A road end at a bridge end may rise or fall for a junction landing while the bridge deck keeps at most this
        // grade (or its present grade, when steeper). The bridge end then takes the new road end height.
        private const float BridgeDeckGrade = 6f;
        // A bridge deck top lies this far above the road end at its marker: the top of the asphalt slab on the road.
        private const float DeckLift = LoogaRoadSlabs.SlabThickness;
        // Simplify: the default largest sideways and height change of a fitted road, and the reach around junctions,
        // landings and bridge ends in which the height fit is tight. The height limit allows the small bumps of the
        // ground-following profile to smooth out. The tight height limit keeps junction heights and pad levels within
        // LevelTolerance.
        private const float SimplifyAcross = 0.75f;
        private const float SimplifyHeight = 0.75f;
        private const float SimplifyLockReach = 15f;
        private const float TightHeight = 0.15f;
        // Fit: the step of the road samples, and the reparameterization passes for a curve that misses by less than
        // FitIterationError times the limits.
        private const float FitStep = 2f;
        private const float FitIterationError = 4f;
        private const int FitIterations = 4;
        // A knot that moved less than this keeps its height.
        private const float KeptKnot = 0.5f;
        // A pad moved or turned more than this gets a new level.
        private const float LevelMove = 2f;
        private const float LevelTurn = 2f;
        // A road end this close to a pad outline takes the pad level. A road end this close to another road takes its
        // height.
        private const float PadEndReach = 3f;
        private const float JunctionReach = 6f;
        // Sample step along the other roads for the junction search.
        private const float JoinStep = 0.5f;
        // Junction shape. A road end on another road gets a landing at the height of that road. After the landing its
        // grade rises gradually to the road grade, and its mouth is wider, so the corners are round.
        private const float LandingLength = 12f;
        private const float LandingGrade = 2f;
        private const float TransitionLength = 30f;
        private const float CornerWidth = 4f;
        private const float CornerLength = 18f;
        // No new knot is added this close to an existing knot.
        private const float KnotGap = 3f;
        // The junction profile reads the ground this far beside the road, outside the cut or fill of the road itself.
        private const float BesideDistance = 22f;
        // A road that is already steeper than MaxGrade keeps its grade up to this limit. The road grade comes from the
        // part of the road farther than the first shape length from its junctions, so a new shape never raises it.
        private const float SteepestGrade = 16f;
        // On a short road the landing and the grade change shrink, so they use at most this share of the road length.
        private const float ZoneShare = 0.5f;
        // ShapeJunctions changes only the knots this close to a junction end. It tries the next length while a sample
        // is steeper than its limit plus the margin.
        private static readonly float[] ShapeLengths = { 60f, 120f, 240f, float.MaxValue };
        private const float GradeMargin = 0.5f;
        // The junction check accepts an end this close to the height of the road that it joins.
        private const float JoinTolerance = 0.05f;
        // Height profile of the roads that the tools shape: the profile of the primary road class.
        private const float Spacing = 2f;
        private const float Smoothing = 15f;
        private const float MaxGrade = 10f;
        // A road end at a pad widens by this much on each side and returns to its width over the flare length.
        private const float FlareWidth = 4f;
        private const float FlareLength = 14f;
        // Pad contact. Where the road surface meets a pad, the road is at the pad level. Within the embankment reach of
        // a pad, the road stays within the bank grade of the pad level, measured from the road edge (PadLimits). The
        // road embankment then moves a pad edge by at most about 3 m x tan(BankGrade), 0.4 m. Half width and embankment
        // are those of the primary road class.
        private static float RoadHalfWidth => _network.PrimaryClass.HalfWidth;
        private const float EmbankmentReach = 12f;
        private const float BankGrade = 8f;
        // LevelPadContacts changes a road that is more than this outside the pad limits. The spline between the knots
        // leaves the fitted profile by up to about 0.3 m, so a smaller tolerance fits a levelled road again on each
        // run.
        private const float LevelTolerance = 0.35f;
        // Largest distance between the knots along a pad contact, so the spline holds the pad level.
        private const float ContactKnotStep = 15f;
        // Cross slope check: sample step along the road, distance of the two side samples from the centre line, the
        // largest accepted tilt in degrees and the number of listed samples.
        private const float CrossStep = 5f;
        private const float CrossOffset = 4f;
        private const float MaxCrossSlope = 3f;
        private const int CrossReportCount = 20;
        // Two road ends this close to each other, with no other road within JunctionReach, are one road
        // (MergeContinuations). Ends this close to a bridge end stay apart.
        private const float MergeDistance = 3f;
        private const float MergeBridgeClearance = 15f;
        // Ends that meet this close to a pad outline stay apart. Knots this close to the meeting knot go.
        private const float MergePadClearance = 8f;
        private const float MergeKnotGap = 6f;

        // The network of the running tool.
        private static LoogaRoadNetwork _network;

        /// <summary>
        /// Supplies the river and lake outlines of the network export, so the planner page shows the water that roads,
        /// bridges and pads must work around. A project with water bodies sets it. Null exports no water.
        /// </summary>
        public static Func<(List<WaterData> rivers, List<WaterData> lakes)> WaterSource { get; set; }

        [Serializable]
        private sealed class PadEdit
        {
            public float x;
            public float z;
            public float rotY;
            public float[] outline;
        }

        [Serializable]
        private sealed class RoadEdit
        {
            public bool deleted;
            public bool isNew;
            public float[] knots;
        }

        // Road Network Editor data. Knot handles are world vectors from the knot; mode is a TangentMode name.
        [Serializable]
        private sealed class KnotData
        {
            public float x;
            public float y;
            public float z;
            public float inX;
            public float inZ;
            public float outX;
            public float outZ;
            public string mode;
        }

        [Serializable]
        private sealed class NetworkRoadEdit
        {
            public bool deleted;
            public bool isNew;
            public string container;
            public KnotData[] knots;
        }

        [Serializable]
        private sealed class BridgeEdit
        {
            public bool deleted;
            public bool isNew;
            public string name;
            public float[] a;
            public float[] b;
        }

        [Serializable]
        private sealed class NetworkData
        {
            public int version = 2;
            public string exported;
            public float roadHalfWidth;
            public List<PadData> pads;
            public List<RoadData> roads;
            public List<BridgeData> bridges;
            public List<WaterData> rivers;
            public List<WaterData> lakes;
        }

        /// <summary>
        /// The outline of a river (both banks) or a lake: x, z pairs in world meters, at least <see cref="WaterStep"/>
        /// apart.
        /// </summary>
        [Serializable]
        public sealed class WaterData
        {
            public string name;
            public float[] points;
        }

        [Serializable]
        private sealed class PadData
        {
            public string id;
            public string name;
            public string levelGroup;
            public bool paved;
            public float x;
            public float y;
            public float z;
            public float rotY;
            public float[] outline;
        }

        [Serializable]
        private sealed class RoadData
        {
            public string id;
            public string container;
            public int index;
            public float length;
            public KnotData[] knots;
        }

        [Serializable]
        private sealed class BridgeData
        {
            public string id;
            public string name;
            public float[] a;
            public float[] b;
            // The deck of another structure: its ends stay where they are.
            public bool structure;
        }

        // The horizontal handles of a knot in world space and its tangent mode. An auto smooth knot computes its
        // handles.
        private readonly struct KnotHandle
        {
            public readonly Vector2 In;
            public readonly Vector2 Out;
            public readonly TangentMode Mode;

            public KnotHandle(Vector2 tangentIn, Vector2 tangentOut, TangentMode mode)
            {
                In = tangentIn;
                Out = tangentOut;
                Mode = mode;
            }

            public bool Auto => Mode == TangentMode.AutoSmooth;
        }

        #region Public
        /// <summary>
        /// Applies the edits in the folder and writes a backup of every changed pad and road container first.
        /// </summary>
        /// <param name="editsFolder">Folder with the pads and roads subfolders that the page export wrote.</param>
        /// <param name="backupFolder">Folder for the JSON backups of the changed components.</param>
        public static string Apply(LoogaRoadNetwork network, string editsFolder, string backupFolder)
        {
            _network = network;
            Dictionary<char, SplineContainer> containers = PlannerContainers();
            Dictionary<string, Transform> pads = PadTransforms().ToDictionary(pad => PadId(pad.name), pad => pad);
            Dictionary<string, PadEdit> padEdits = ReadEdits<PadEdit>(Path.Combine(editsFolder, "pads"));
            Dictionary<string, RoadEdit> roadEdits = ReadEdits<RoadEdit>(Path.Combine(editsFolder, "roads"));
            var report = new StringBuilder();

            Backup(backupFolder, pads.Values, containers.Values);
            List<(string bridge, string marker, Vector3 end, float before)> bridgeEnds = BridgeEnds(containers.Values);

            // Links first, so no pad drags the old road knots when it moves.
            foreach (Transform pad in pads.Values)
            {
                var poi = pad.GetComponent<LoogaRoadPad>();
                if (!poi) continue;
                var links = new SerializedObject(poi);
                links.FindProperty("_roadLinks").arraySize = 0;
                links.ApplyModifiedProperties();
            }

            foreach ((string id, PadEdit edit) in padEdits)
            {
                if (!pads.TryGetValue(id, out Transform pad))
                {
                    report.AppendLine("Pad " + id + " is not in the scene. Skipped.");
                    continue;
                }
                report.AppendLine(MovePad(pad, edit));
            }

            // Every road with an edit gets its knots now. Deleted roads go last, so the spline indexes stay valid.
            var edited = new List<(SplineContainer container, int index, List<Vector2> knots)>();
            foreach ((string id, RoadEdit edit) in roadEdits)
            {
                if (edit.deleted || edit.knots == null || edit.knots.Length < 4 || id[0] == 'N') continue;
                SplineContainer container = containers[id[0]];
                int index = int.Parse(id.Substring(1));
                edited.Add((container, index, Pairs(edit.knots)));
            }
            var newRoads = roadEdits.Where(entry => entry.Key[0] == 'N' && !entry.Value.deleted &&
                entry.Value.knots != null &&
                entry.Value.knots.Length >= 4).OrderBy(entry => entry.Key).ToList();
            foreach ((string id, RoadEdit edit) in newRoads)
            {
                SplineContainer container = containers['M'];
                Undo.RecordObject(container, "Add road");
                container.AddSpline();
                edited.Add((container, container.Splines.Count - 1, Pairs(edit.knots)));
                report.AppendLine(id + " added as " + container.name + " spline " + (container.Splines.Count - 1) +
                    ".");
            }

            List<(Transform pad, Vector2[] outline)> outlines = pads.Values.Select(pad => (pad, Outline(pad))).ToList();
            // Two passes: the first sets every edited road, the second gives the junction ends the final heights of the
            // roads that they join.
            for (int pass = 0; pass < 2; pass++)
            {
                foreach ((SplineContainer container, int index, List<Vector2> knots) in edited)
                {
                    string line = SetRoad(container, index, knots, outlines, containers.Values, pass == 1);
                    if (pass == 1) report.AppendLine(line);
                }
            }
            SetFlares(containers['M'], edited.Where(road => road.container == containers['M']).Select(road =>
                road.index),
                outlines);

            var deleted = roadEdits.Where(entry => entry.Value.deleted && entry.Key[0] != 'N')
                .Select(entry => (container: containers[entry.Key[0]], index: int.Parse(entry.Key.Substring(1)), id:
                    entry.Key))
                .OrderByDescending(road => road.index).ToList();
            foreach ((SplineContainer container, int index, string id) in deleted)
            {
                RemoveSpline(container, index);
                report.AppendLine(id + " removed. Later spline indexes of " + container.name + " moved down by one.");
            }
            // Roads that meet end to end become one road. Then the pad levels, so the junction ends take the levelled
            // heights.
            report.Append(MergeContinuations(containers.Values.ToArray(), outlines));
            report.Append(LevelPadContacts(containers.Values, outlines));
            report.Append(ShapeJunctions(containers.Values, outlines));

            foreach (Transform pad in pads.Values)
            {
                LoogaRoadPads.LinkRoads(pad);
            }
            report.Append(BridgeReport(bridgeEnds, containers.Values));
            RequestFullUpdate();
            EditorSceneManager.MarkSceneDirty(_network.gameObject.scene);
            CheckCrossSlopeAfterUpdate(_network);
            report.AppendLine("The cross slope check runs after the terrain update and writes its report to the " +
                "console.");
            return report.ToString();
        }

        /// <summary>
        /// Shapes every junction of the road group: each road end that stops on another road gets a landing at the
        /// height of that road, a gradual change to its own grade and a wide mouth with round corners. Only the knots
        /// near the junction change. The report ends with a check of every junction. Apply and the road build run it
        /// too.
        /// </summary>
        /// <param name="backupFolder">Folder for the JSON backups of the road containers, or null for no
        /// backup.</param>
        public static string ShapeJunctions(LoogaRoadNetwork network, string backupFolder)
        {
            _network = network;
            (SplineContainer[] containers, List<(Transform pad, Vector2[] outline)> outlines) = RoadsAndPads();
            if (backupFolder != null) Backup(backupFolder, Enumerable.Empty<Transform>(), containers);
            string report = ShapeJunctions(containers, outlines);
            RequestFullUpdate();
            EditorSceneManager.MarkSceneDirty(_network.gameObject.scene);
            return report;
        }

        /// <summary>
        /// Runs the pad pass (LevelPadContacts) and the junction pass (ShapeJunctions) twice, so the roads keep the pad
        /// limits and the junction landings together. Apply, the road build and SimplifyRoads run it.
        /// </summary>
        /// <param name="backupFolder">Folder for the JSON backups of the road containers, or null for no
        /// backup.</param>
        public static string SettleRoads(LoogaRoadNetwork network, string backupFolder)
        {
            _network = network;
            (SplineContainer[] containers, List<(Transform pad, Vector2[] outline)> outlines) = RoadsAndPads();
            if (backupFolder != null) Backup(backupFolder, Enumerable.Empty<Transform>(), containers);
            string report = Settle(containers, outlines);
            foreach ((Transform pad, Vector2[] _) in outlines)
            {
                LoogaRoadPads.LinkRoads(pad);
            }
            RequestFullUpdate();
            EditorSceneManager.MarkSceneDirty(_network.gameObject.scene);
            return report;
        }

        // The junction pass can move a road between the knots that hold its pad contact, so a second round levels those
        // roads again. The second round changes only the roads that the first round left outside a limit or a landing.
        // The bridge ends then follow road ends that the junction pass moved.
        private static string Settle(SplineContainer[] containers, List<(Transform pad, Vector2[] outline)> outlines)
        {
            string first = LevelPadContacts(containers, outlines) + LevelNodes(containers, outlines) +
                ShapeJunctions(containers, outlines);
            return first + "Second round:\n" + LevelPadContacts(containers, outlines) + LevelNodes(containers,
                outlines) +
                ShapeJunctions(containers, outlines) + MoveBridgeEnds(containers);
        }

        /// <summary>
        /// Levels every road with a height stamp with the pads that it meets. Where the road surface meets a pad, the
        /// road takes the pad level. Within its embankment reach of a pad, the road stays within the bank grade of the
        /// pad level, so neither the road nor the pad edge tilts. Junction ends then take the new heights of the roads
        /// that they join. Only roads outside these limits change, and only near the pads. The pads link the road knots
        /// again. Apply and the road build run it before ShapeJunctions.
        /// </summary>
        /// <param name="backupFolder">Folder for the JSON backups of the road containers, or null for no
        /// backup.</param>
        public static string LevelPadContacts(LoogaRoadNetwork network, string backupFolder)
        {
            _network = network;
            (SplineContainer[] containers, List<(Transform pad, Vector2[] outline)> outlines) = RoadsAndPads();
            if (backupFolder != null) Backup(backupFolder, Enumerable.Empty<Transform>(), containers);
            string report = LevelPadContacts(containers, outlines);
            foreach ((Transform pad, Vector2[] _) in outlines)
            {
                LoogaRoadPads.LinkRoads(pad);
            }
            RequestFullUpdate();
            EditorSceneManager.MarkSceneDirty(_network.gameObject.scene);
            return report;
        }
        #endregion

        #region Pads
        // Moves and turns the pad as one piece. The outline knots are in pad space, so they move with it.
        private static string MovePad(Transform pad, PadEdit edit)
        {
            Vector3 before = pad.position;
            float turn = Mathf.DeltaAngle(pad.eulerAngles.y, edit.rotY);
            Undo.RecordObject(pad, "Move road pad");
            pad.SetPositionAndRotation(new Vector3(edit.x, before.y, edit.z), Quaternion.Euler(0f, edit.rotY, 0f));
            float error = 0f;
            Vector2[] outline = Outline(pad);
            List<Vector2> target = Pairs(edit.outline);
            if (target.Count == outline.Length)
            {
                error = outline.Zip(target, Vector2.Distance).Max();
            }
            float moved = Vector2.Distance(new Vector2(before.x, before.z), new Vector2(edit.x, edit.z));
            string line = pad.name + ": moved " + moved.ToString("F1") + " m, turned " + turn.ToString("F1") +
                " deg, outline error " + error.ToString("F2") + " m";
            if (moved > LevelMove || Mathf.Abs(turn) > LevelTurn)
            {
                line += ". " + LoogaRoadPads.AutoLevel(pad);
            }
            return line;
        }

        private static Vector2[] Outline(Transform pad)
        {
            return LoogaRoadPads.SampleOutline(pad.GetComponent<SplineContainer>());
        }
        #endregion

        #region Roads
        // Gives the spline the knots and a height profile. Knots that did not move keep their heights. Ends at a pad
        // take the pad level, ends at a structure deck take its height and, in the junction pass, ends at another road
        // take its height.
        private static string SetRoad(SplineContainer container, int index, List<Vector2> knots,
            List<(Transform pad, Vector2[] outline)> outlines, IEnumerable<SplineContainer> containers, bool junctions,
            List<KnotHandle> handles = null)
        {
            Spline spline = container.Splines[index];
            List<Vector3> old = spline.Select(knot =>
                (Vector3)container.transform.TransformPoint(knot.Position)).ToList();
            handles ??= HandlesAt(container, spline, knots);
            (Spline flat, float[] ground) = Samples(knots, false, handles);
            float length = flat.GetLength();
            int count = ground.Length;
            int SampleOf(int knot) => LoogaRoadNetworkTools.SampleOf(flat, knot, count);

            var pins = new Dictionary<int, float>();
            for (int knot = 0; knot < knots.Count; knot++)
            {
                Vector3 kept = old.FirstOrDefault(point => Vector2.Distance(Flat(point), knots[knot]) < KeptKnot);
                if (kept != default) pins[SampleOf(knot)] = kept.y;
            }
            int padEnds = 0, deckEnds = 0, junctionEnds = 0;
            foreach (int end in new[] { 0, knots.Count - 1 })
            {
                Vector2 point = knots[end];
                Transform pad = outlines.Where(entry => SignedDistance(entry.outline, point) < PadEndReach)
                    .OrderBy(entry => SignedDistance(entry.outline, point)).Select(entry => entry.pad).FirstOrDefault();
                if (pad)
                {
                    pins[SampleOf(end)] = pad.position.y;
                    padEnds++;
                }
                else if (TryStructureEnd(point, out float deck))
                {
                    pins[SampleOf(end)] = deck;
                    deckEnds++;
                }
                else if (junctions && TryJoin(container, index, point, containers, out float joined))
                {
                    pins[SampleOf(end)] = joined;
                    junctionEnds++;
                }
            }

            return container.name + " " + index + ": " + knots.Count + " knots, " + length.ToString("F0") +
                " m, pad ends " + padEnds + ", deck ends " + deckEnds + ", junction ends " + junctionEnds + ", " +
                WriteRoad(container, index, knots, flat, ground, pins, null, PadLimits(flat, count, outlines), handles);
        }

        #region Junctions
        private static string ShapeJunctions(IEnumerable<SplineContainer> containers,
            List<(Transform pad, Vector2[] outline)> outlines)
        {
            List<SplineContainer> all = containers.ToList();
            // Only roads with an active height stamp get a shape.
            List<SplineContainer> shaped = all.Where(container =>
            {
                var path = container.GetComponent<LoogaSplinePath>();
                return path && path.enabled;
            }).ToList();
            var report = new StringBuilder();
            // The report measures the height change against the roads before both passes.
            Dictionary<SplineContainer, List<Spline>> originals = shaped.ToDictionary(container => container,
                container => container.Splines.Select(spline => new Spline(spline)).ToList());
            // Two passes, so an end that joins a road changed in the first pass takes its final height.
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (SplineContainer container in shaped)
                {
                    for (int index = 0; index < container.Splines.Count; index++)
                    {
                        string line = ShapeRoad(container, index, originals[container][index], outlines, all);
                        if (line != null && pass == 1) report.AppendLine(line);
                    }
                }
            }
            report.Append(CheckJunctions(shaped, outlines, all));
            return report.ToString();
        }

        // Gives each node where two or more road ends meet without a road through it one height that every road there
        // can reach. A road can rise or fall by its landing and grade change limits between the node and its reference
        // point: the point at the landing plus the grade change, or its far end on a short road. A far end at a bridge
        // end may move within the deck grade of the bridge. The node takes the height nearest its present height within
        // all these ranges, or the middle of the nearest limits when they do not overlap. ShapeJunctions then shapes
        // the landings.
        private static string LevelNodes(SplineContainer[] containers, List<(Transform pad,
            Vector2[] outline)> outlines)
        {
            var ends = new List<(SplineContainer container, int index, bool start, Vector3 point)>();
            foreach (SplineContainer container in containers.Where(HasHeightStamp))
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    Spline spline = container.Splines[index];
                    if (spline.Count < 2) continue;
                    foreach (bool start in new[] { true, false })
                    {
                        Vector3 point = container.transform.TransformPoint(spline[start ? 0 :
                            spline.Count - 1].Position);
                        if (outlines.Any(entry => SignedDistance(entry.outline, Flat(point)) < PadEndReach)) continue;
                        ends.Add((container, index, start, point));
                    }
                }
            }
            var group = Enumerable.Range(0, ends.Count).ToArray();
            int Root(int item) => group[item] == item ? item : group[item] = Root(group[item]);
            for (int a = 0; a < ends.Count; a++)
            {
                for (int b = a + 1; b < ends.Count; b++)
                {
                    if (Vector2.Distance(Flat(ends[a].point),
                        Flat(ends[b].point)) < JunctionReach) group[Root(a)] = Root(b);
                }
            }

            var report = new StringBuilder();
            foreach (List<int> node in Enumerable.Range(0, ends.Count).GroupBy(Root).Select(entry => entry.ToList())
                         .Where(entry => entry.Count > 1))
            {
                Vector2 centre = node.Aggregate(Vector2.zero, (sum, item) => sum + Flat(ends[item].point)) / node.Count;
                if (HasThroughRoad(containers, centre, node.Select(item => (ends[item].container,
                    ends[item].index)))) continue;
                float present = node.Average(item => ends[item].point.y);
                float lower = float.NegativeInfinity, upper = float.PositiveInfinity;
                foreach (int item in node)
                {
                    (SplineContainer container, int index, bool start, Vector3 _) = ends[item];
                    (float low, float high) = NodeRange(container, index, start, present, outlines);
                    lower = Mathf.Max(lower, low);
                    upper = Mathf.Min(upper, high);
                }
                float height = lower <= upper ? Mathf.Clamp(present, lower, upper) : 0.5f * (lower + upper);
                if (node.All(item => Mathf.Abs(ends[item].point.y - height) < JoinTolerance)) continue;
                foreach (int item in node)
                {
                    (SplineContainer container, int index, bool start, Vector3 point) = ends[item];
                    Spline spline = container.Splines[index];
                    int knot = start ? 0 : spline.Count - 1;
                    BezierKnot value = spline[knot];
                    value.Position = container.transform.InverseTransformPoint(new Vector3(point.x, height, point.z));
                    Undo.RecordObject(container, "Level road node");
                    spline[knot] = value;
                    EditorUtility.SetDirty(container);
                }
                report.AppendLine("Node at (" + centre.x.ToString("F0") + ", " + centre.y.ToString("F0") + ") with " +
                    node.Count +
                    " road ends: " + present.ToString("F2") + " m to " + height.ToString("F2") + " m" +
                    (lower > upper ? " (the roads cannot all keep their limits; " + (lower - upper).ToString("F2") +
                        " m short)." : "."));
            }
            return report.ToString();
        }

        // True when a road other than the node roads passes the node point away from its own ends.
        private static bool HasThroughRoad(SplineContainer[] containers, Vector2 centre,
            IEnumerable<(SplineContainer container, int index)> nodeRoads)
        {
            var own = new HashSet<(SplineContainer, int)>(nodeRoads);
            foreach (SplineContainer container in containers)
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    if (own.Contains((container, index))) continue;
                    var spline = new Spline(container.Splines[index]);
                    if (spline.Count < 2) continue;
                    float length = spline.GetLength();
                    int steps = Mathf.Max(2, Mathf.CeilToInt(length / JoinStep));
                    for (int step = 0; step <= steps; step++)
                    {
                        float along = step * length / steps;
                        if (along < JunctionReach || along > length - JunctionReach) continue;
                        Vector3 world = container.transform.TransformPoint(spline.EvaluatePosition(step /
                            (float)steps));
                        if (Vector2.Distance(Flat(world), centre) < JunctionReach) return true;
                    }
                }
            }
            return false;
        }

        // The node heights that one road end can reach: its reference height (a range at a bridge end) plus or minus
        // the largest rise of its landing and grade change up to the reference point.
        private static (float lower, float upper) NodeRange(SplineContainer container, int index, bool start,
            float present,
            List<(Transform pad, Vector2[] outline)> outlines)
        {
            var spline = new Spline(container.Splines[index]);
            float length = spline.GetLength();
            JunctionZone zone = ZoneOf(container, spline, new List<(bool start, float height)> { (start, present) });
            float reach = Mathf.Min(length, zone.Landing + zone.Transition);
            float rise = 0f;
            for (float distance = Spacing; distance <= reach; distance += Spacing)
            {
                rise += Mathf.Tan(zone.Grade(distance) * Mathf.Deg2Rad) * Spacing;
            }
            float t = (start ? reach : length - reach) / length;
            float reference = container.transform.TransformPoint(spline.EvaluatePosition(t)).y;
            float low = reference, high = reference;
            if (reach >= length)
            {
                List<Vector3> points = spline.Select(knot =>
                    (Vector3)container.transform.TransformPoint(knot.Position)).ToList();
                var joins = new List<(bool start, float height)> { (start, present) };
                foreach ((bool _, float lower, float upper) in BridgeEndRanges(points, joins, outlines))
                {
                    low = Mathf.Min(low, lower);
                    high = Mathf.Max(high, upper);
                }
            }
            return (low - rise, high + rise);
        }

        // The ends of the road at a bridge end (not at a pad or a junction), with the heights that keep the bridge deck
        // within BridgeDeckGrade, or within its present grade when that is steeper.
        private static List<(bool start, float lower, float upper)> BridgeEndRanges(List<Vector3> points,
            List<(bool start, float height)> joins, List<(Transform pad, Vector2[] outline)> outlines)
        {
            var ranges = new List<(bool, float, float)>();
            List<(Transform marker, Transform other, bool fixedDeck)> markers = BridgeMarkers();
            foreach (bool start in new[] { true, false })
            {
                Vector3 point = points[start ? 0 : points.Count - 1];
                if (joins.Any(join => join.start == start)) continue;
                if (outlines.Any(entry => SignedDistance(entry.outline, Flat(point)) < PadEndReach)) continue;
                foreach ((Transform marker, Transform other, bool fixedDeck) in markers)
                {
                    if (Vector2.Distance(Flat(marker.position), Flat(point)) > BridgeEndReach) continue;
                    // A structure deck does not move, so the road end meets its marker exactly.
                    if (fixedDeck)
                    {
                        float fixedRoad = marker.position.y - DeckLift;
                        ranges.Add((start, fixedRoad, fixedRoad));
                        break;
                    }
                    float span = Vector2.Distance(Flat(marker.position), Flat(other.position));
                    float grade = Mathf.Max(Mathf.Tan(BridgeDeckGrade * Mathf.Deg2Rad),
                        Mathf.Abs(marker.position.y - other.position.y) / Mathf.Max(span, 0.01f));
                    // The road end lies DeckLift below the deck top at its marker.
                    float road = other.position.y - DeckLift;
                    ranges.Add((start, road - grade * span, road + grade * span));
                    break;
                }
            }
            return ranges;
        }

        // The road end height at a structure deck marker within BridgeEndReach: DeckLift below the deck top.
        private static bool TryStructureEnd(Vector2 point, out float height)
        {
            foreach ((Transform marker, Transform _, bool fixedDeck) in BridgeMarkers())
            {
                if (!fixedDeck || Vector2.Distance(Flat(marker.position), point) >= BridgeEndReach) continue;
                height = marker.position.y - DeckLift;
                return true;
            }
            height = 0f;
            return false;
        }

        // Each bridge end marker with the other end of its bridge, and whether the bridge is a structure deck.
        private static List<(Transform marker, Transform other, bool fixedDeck)> BridgeMarkers()
        {
            var markers = new List<(Transform, Transform, bool)>();
            foreach (LoogaRoadBridge bridge in Bridges())
            {
                markers.Add((bridge.StartMarker, bridge.FinishMarker, bridge.StructureDeck));
                markers.Add((bridge.FinishMarker, bridge.StartMarker, bridge.StructureDeck));
            }
            return markers;
        }

        // The bridges with both end markers, in hierarchy order.
        private static List<LoogaRoadBridge> Bridges()
        {
            var bridges = new List<LoogaRoadBridge>();
            Transform root = _network.BridgeRoot;
            if (!root) return bridges;
            foreach (Transform child in root)
            {
                if (child.TryGetComponent(out LoogaRoadBridge bridge) && bridge.HasMarkers) bridges.Add(bridge);
            }
            return bridges;
        }

        // Moves each bridge end marker (the deck top) DeckLift above the road end at it, so the deck top continues the
        // road slab top, and builds the bridges again when one moved. Structure decks stay.
        private static string MoveBridgeEnds(IEnumerable<SplineContainer> containers)
        {
            var report = new StringBuilder();
            foreach ((Transform marker, Transform _, bool fixedDeck) in BridgeMarkers())
            {
                if (fixedDeck) continue;
                Vector3 nearest = default;
                float distance = BridgeEndReach;
                foreach (SplineContainer container in containers)
                {
                    foreach (Spline spline in container.Splines)
                    {
                        if (spline.Count < 2) continue;
                        foreach (int end in new[] { 0, spline.Count - 1 })
                        {
                            Vector3 world = container.transform.TransformPoint(spline[end].Position);
                            float candidate = Vector2.Distance(Flat(world), Flat(marker.position));
                            if (candidate >= distance) continue;
                            distance = candidate;
                            nearest = world;
                        }
                    }
                }
                float deck = nearest.y + DeckLift;
                if (distance >= BridgeEndReach || Mathf.Abs(deck - marker.position.y) < JoinTolerance) continue;
                report.AppendLine(marker.parent.name + " " + marker.name + ": " + marker.position.y.ToString("F2") +
                    " m to " +
                    deck.ToString("F2") + " m with its road end.");
                Undo.RecordObject(marker, "Move bridge end");
                marker.position = new Vector3(marker.position.x, deck, marker.position.z);
            }
            if (report.Length > 0) report.Append(LoogaRoadBridges.Build(_network));
            return report.ToString();
        }

        // The junction ends of the road: ends that stop on another road and not at a pad.
        private static List<(bool start, float height)> JunctionEnds(SplineContainer container, int index,
            Spline spline,
            List<(Transform pad, Vector2[] outline)> outlines, IEnumerable<SplineContainer> containers)
        {
            var joins = new List<(bool, float)>();
            foreach (bool start in new[] { true, false })
            {
                Vector2 point = Flat(container.transform.TransformPoint(spline[start ? 0 : spline.Count - 1].Position));
                if (outlines.Any(entry => SignedDistance(entry.outline, point) < PadEndReach)) continue;
                if (TryJoin(container, index, point, containers, out float height)) joins.Add((start, height));
            }
            return joins;
        }

        // Gives the junction ends of the road their landing, grade change and mouth. Returns null for a road without
        // junction ends.
        private static string ShapeRoad(SplineContainer container, int index, Spline original,
            List<(Transform pad, Vector2[] outline)> outlines, IEnumerable<SplineContainer> containers)
        {
            var source = new Spline(container.Splines[index]);
            if (source.Count < 2) return null;
            List<(bool start, float height)> joins = JunctionEnds(container, index, source, outlines, containers);
            if (joins.Count == 0) return null;

            List<Vector3> current = source.Select(knot =>
                (Vector3)container.transform.TransformPoint(knot.Position)).ToList();
            List<Vector2> currentFlat = current.Select(Flat).ToList();
            Spline before = FlatSpline(currentFlat, HandlesAt(container, source, currentFlat));
            JunctionZone zone = ZoneOf(container, source, joins);
            List<Vector3> points = AddJunctionKnots(current, before, joins, zone);
            List<Vector2> knots = points.Select(Flat).ToList();
            List<KnotHandle> handles = HandlesAt(container, source, knots);
            (Spline flat, float[] ground) = Samples(knots, true, handles);
            float length = flat.GetLength();
            int count = ground.Length;
            // Pad levels win over the junction shape.
            (float[] lower, float[] upper) limits = PadLimits(flat, count, outlines);
            // An end at a bridge end is free within the deck grade of its bridge, so a short road between a bridge and
            // a junction gets its landing.
            var free = new HashSet<int>();
            foreach ((bool start, float lower, float upper) in BridgeEndRanges(points, joins, outlines))
            {
                int end = start ? 0 : count - 1;
                free.Add(start ? 0 : knots.Count - 1);
                limits.lower[end] = Mathf.Max(limits.lower[end], lower);
                limits.upper[end] = Mathf.Min(limits.upper[end], upper);
            }

            var steps = new float[count];
            for (int sample = 0; sample < count; sample++)
            {
                float along = sample * length / (count - 1);
                steps[sample] = Mathf.Tan(zone.Grade(joins.Min(join => join.start ? along :
                    length - along)) * Mathf.Deg2Rad) *
                    Spacing;
            }

            // The changed stretch grows until every sample keeps its grade limit. Both ends always keep a pin.
            var pins = new Dictionary<int, float>();
            float margin = Mathf.Tan(GradeMargin * Mathf.Deg2Rad) * Spacing;
            float[] profile = null;
            foreach (float window in ShapeLengths)
            {
                pins.Clear();
                for (int knot = 0; knot < knots.Count; knot++)
                {
                    float along = SplineUtility.ConvertIndexUnit(flat, knot, PathIndexUnit.Knot,
                        PathIndexUnit.Distance);
                    bool near = joins.Any(join => (join.start ? along : length - along) < window);
                    bool end = knot == 0 || knot == knots.Count - 1;
                    if (!near || (end && !free.Contains(knot))) pins[SampleOf(flat, knot, count)] = points[knot].y;
                }
                foreach ((bool start, float height) in joins) pins[start ? 0 : count - 1] = height;
                profile = Profile(ground, pins, steps, limits);
                // Only the changed stretch counts. A steep part farther away is not part of the junction.
                bool kept = true;
                for (int sample = 1; sample < count && kept; sample++)
                {
                    float along = sample * length / (count - 1);
                    if (joins.All(join => (join.start ? along : length - along) > window)) continue;
                    kept = Mathf.Abs(profile[sample] - profile[sample - 1]) <= Mathf.Min(steps[sample],
                        steps[sample - 1]) + margin;
                }
                if (kept) break;
            }

            // The largest height change, against the original road at the same share of the length.
            float change = 0f, changeAt = 0f;
            for (int sample = 0; sample < count; sample++)
            {
                float t = sample / (float)(count - 1);
                float difference = Mathf.Abs(profile[sample] -
                    container.transform.TransformPoint(original.EvaluatePosition(t)).y);
                if (difference <= change) continue;
                change = difference;
                changeAt = joins.Min(join => join.start ? t * length : length - t * length);
            }

            WriteRoad(container, index, knots, flat, ground, pins, steps, limits, handles);
            SetCorners(container, index, before, flat, joins, zone);
            return container.name + " " + index + ": " + joins.Count + " junction end" + (joins.Count > 1 ? "s" : "") +
                ", road grade " + zone.RoadGrade.ToString("F1") + " deg, landing " + zone.Landing.ToString("F0") +
                    " m, " +
                (knots.Count - current.Count) + " knots added, largest height change " + change.ToString("F2") +
                    " m at " +
                changeAt.ToString("F0") + " m from the junction.";
        }

        // The landing, the grade change and the road grade of one road.
        private readonly struct JunctionZone
        {
            public readonly float Landing;
            public readonly float Transition;
            public readonly float Corner;
            public readonly float RoadGrade;

            public JunctionZone(float scale, float roadGrade)
            {
                Landing = LandingLength * scale;
                Transition = TransitionLength * scale;
                Corner = CornerLength * scale;
                RoadGrade = roadGrade;
            }

            // The grade limit at a distance from the junction end, in degrees.
            public float Grade(float distance) => Mathf.Lerp(LandingGrade, RoadGrade,
                Mathf.SmoothStep(0f, 1f, (distance - Landing) / Mathf.Max(Transition, 1e-3f)));
        }

        // The zone shrinks on a short road. The road grade is the steepest grade away from the junctions, or the mean
        // grade that the road needs between its ends, whichever is larger, within MaxGrade and SteepestGrade.
        private static JunctionZone ZoneOf(SplineContainer container, Spline spline, List<(bool start,
            float height)> joins)
        {
            float length = spline.GetLength();
            float scale = Mathf.Min(1f, ZoneShare * length / (LandingLength + TransitionLength));
            float away = 0f;
            int count = Mathf.Max(2, Mathf.CeilToInt(length / Spacing) + 1);
            Vector3 previous = container.transform.TransformPoint(spline.EvaluatePosition(0f));
            for (int sample = 1; sample < count; sample++)
            {
                float t = sample / (float)(count - 1);
                Vector3 point = container.transform.TransformPoint(spline.EvaluatePosition(t));
                float run = Vector2.Distance(Flat(point), Flat(previous));
                bool far = joins.All(join => (join.start ? t * length : length - t * length) > ShapeLengths[0]);
                if (far && run > 0.5f) away = Mathf.Max(away, Mathf.Atan2(Mathf.Abs(point.y - previous.y),
                    run) * Mathf.Rad2Deg);
                previous = point;
            }
            float first = joins.Any(join => join.start) ? joins.First(join => join.start).height
                : container.transform.TransformPoint(spline[0].Position).y;
            float last = joins.Any(join => !join.start) ? joins.First(join => !join.start).height
                : container.transform.TransformPoint(spline[spline.Count - 1].Position).y;
            float level = joins.Count * (LandingLength + TransitionLength * 0.5f) * scale;
            float needed = Mathf.Atan2(Mathf.Abs(last - first), Mathf.Max(length - level, Spacing)) * Mathf.Rad2Deg;
            return new JunctionZone(scale, Mathf.Clamp(Mathf.Max(away, needed), MaxGrade, SteepestGrade));
        }

        // Adds knots at the landing end, in the middle of the grade change and at its end, so the spline can hold the
        // shape. The new knots lie on the old flat curve. Their height is the old height, which the profile replaces.
        private static List<Vector3> AddJunctionKnots(List<Vector3> current, Spline before,
            List<(bool start, float height)> joins, JunctionZone zone)
        {
            float length = before.GetLength();
            var knots = current.Select((point, knot) =>
                (along: SplineUtility.ConvertIndexUnit(before, knot, PathIndexUnit.Knot, PathIndexUnit.Distance),
                    point)).ToList();
            foreach ((bool start, float _) in joins)
            {
                foreach (float distance in new[] { zone.Landing, zone.Landing + zone.Transition * 0.5f,
                             zone.Landing + zone.Transition })
                {
                    float along = start ? distance : length - distance;
                    if (along < KnotGap || along > length - KnotGap) continue;
                    if (knots.Any(knot => Mathf.Abs(knot.along - along) < KnotGap)) continue;
                    float t = along / length;
                    Vector3 flatPoint = before.EvaluatePosition(t);
                    // The height comes from the neighbouring old knots, in proportion to the distance.
                    int next = knots.FindIndex(knot => knot.along > along);
                    (float along, Vector3 point) a = knots[next - 1], b = knots[next];
                    float height = Mathf.Lerp(a.point.y, b.point.y, (along - a.along) / Mathf.Max(b.along - a.along,
                        1e-3f));
                    knots.Insert(next, (along, new Vector3(flatPoint.x, height, flatPoint.z)));
                }
            }
            return knots.Select(knot => knot.point).ToList();
        }

        // Moves the width points of the road to the new knots and gives each junction end a wide mouth.
        private static void SetCorners(SplineContainer container, int index, Spline before, Spline after,
            List<(bool start, float height)> joins, JunctionZone zone)
        {
            var path = container.GetComponent<LoogaSplinePath>();
            if (!path) return;
            var widths = new List<LoogaSplinePath.SplineWidths>();
            for (int spline = 0; spline < container.Splines.Count; spline++)
            {
                var copy = new LoogaSplinePath.SplineWidths();
                copy.Set(spline < path.Widths.Count && path.Widths[spline] != null
                    ? path.Widths[spline].Points.ToList()
                    : new List<Vector2>());
                widths.Add(copy);
            }
            float length = after.GetLength();
            float KnotAt(float along) => SplineUtility.ConvertIndexUnit(after, Mathf.Clamp(along, 0f, length),
                PathIndexUnit.Distance, PathIndexUnit.Knot);
            var points = new List<Vector2>();
            foreach (Vector2 point in widths[index].Points)
            {
                float along = SplineUtility.ConvertIndexUnit(before, point.x, PathIndexUnit.Knot,
                    PathIndexUnit.Distance);
                // Old mouth points go. The mouth below replaces them.
                if (joins.Any(join => (join.start ? along : length - along) < zone.Corner + KnotGap)) continue;
                points.Add(new Vector2(KnotAt(along), point.y));
            }
            foreach ((bool start, float _) in joins)
            {
                points.Add(new Vector2(KnotAt(start ? 0f : length), CornerWidth));
                points.Add(new Vector2(KnotAt(start ? zone.Corner : length - zone.Corner), 0f));
            }
            widths[index].Set(points);
            Undo.RecordObject(path, "Set junction corners");
            path.SetSpline(container, widths, path.WidthEasing);
            path.MarkChanged();
            EditorUtility.SetDirty(path);
        }

        // Lists every junction end that is not at the height of the road that it joins, or that is steeper than its
        // landing or grade change allow.
        private static string CheckJunctions(List<SplineContainer> shaped, List<(Transform pad,
            Vector2[] outline)> outlines,
            IEnumerable<SplineContainer> containers)
        {
            var problems = new StringBuilder();
            int checkedEnds = 0;
            foreach (SplineContainer container in shaped)
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    var spline = new Spline(container.Splines[index]);
                    if (spline.Count < 2) continue;
                    List<(bool start, float height)> joins = JunctionEnds(container, index, spline, outlines,
                        containers);
                    if (joins.Count == 0) continue;
                    JunctionZone zone = ZoneOf(container, spline, joins);
                    float length = spline.GetLength();
                    foreach ((bool start, float height) in joins)
                    {
                        checkedEnds++;
                        Vector3 end = container.transform.TransformPoint(spline.EvaluatePosition(start ? 0f : 1f));
                        float gap = Mathf.Abs(end.y - height);
                        float landing = 0f;
                        Vector3 previous = end;
                        for (float distance = Spacing; distance <= zone.Landing + zone.Transition && distance < length;
                             distance += Spacing)
                        {
                            Vector3 point = container.transform.TransformPoint(
                                spline.EvaluatePosition((start ? distance : length - distance) / length));
                            float grade = Mathf.Atan2(Mathf.Abs(point.y - previous.y), Spacing) * Mathf.Rad2Deg;
                            landing = Mathf.Max(landing, grade - zone.Grade(distance));
                            previous = point;
                        }
                        if (gap <= JoinTolerance && landing <= GradeMargin + 1f) continue;
                        problems.AppendLine("Junction check: " + container.name + " " + index + (start ? " start" :
                            " end") +
                            " is " + gap.ToString("F2") + " m from the road that it joins and up to " +
                                landing.ToString("F1") +
                            " deg steeper than its landing allows.");
                    }
                }
            }
            return problems.Length > 0 ? problems.ToString()
                : "Junction check: all " + checkedEnds + " junction ends have a level landing and a gradual grade.\n";
        }
        #endregion

        #region Pad contacts
        private static string LevelPadContacts(IEnumerable<SplineContainer> containers,
            List<(Transform pad, Vector2[] outline)> outlines)
        {
            List<SplineContainer> all = containers.ToList();
            List<SplineContainer> levelled = all.Where(HasHeightStamp).ToList();
            var lines = new Dictionary<(SplineContainer, int), string>();
            // Two passes, so a junction end on a road changed in the first pass takes its final height.
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (SplineContainer container in levelled)
                {
                    for (int index = 0; index < container.Splines.Count; index++)
                    {
                        string line = LevelRoad(container, index, outlines, all);
                        if (line == null) continue;
                        lines[(container, index)] = lines.TryGetValue((container, index), out string first)
                            ? first + " Second pass: " + line
                            : line;
                    }
                }
            }
            var report = new StringBuilder();
            foreach (string line in lines.Values) report.AppendLine(line);
            report.Append(CheckPadContacts(levelled, outlines));
            return report.ToString();
        }

        private static bool HasHeightStamp(SplineContainer container)
        {
            var path = container.GetComponent<LoogaSplinePath>();
            return path && path.enabled;
        }

        // Fits the road to the pad limits and to the heights of the roads that its junction ends join. Only the knots
        // near the stretches outside the limits change. Returns null when the road already keeps the limits.
        private static string LevelRoad(SplineContainer container, int index,
            List<(Transform pad, Vector2[] outline)> outlines, IEnumerable<SplineContainer> containers)
        {
            var source = new Spline(container.Splines[index]);
            if (source.Count < 2) return null;
            List<Vector3> current = source.Select(knot =>
                (Vector3)container.transform.TransformPoint(knot.Position)).ToList();
            List<Vector2> currentFlat = current.Select(Flat).ToList();
            Spline before = FlatSpline(currentFlat, HandlesAt(container, source, currentFlat));
            float length = before.GetLength();
            int count = Mathf.Max(2, Mathf.CeilToInt(length / Spacing) + 1);
            float AlongOf(int sample) => sample * length / (count - 1);
            // The road height at a distance along the flat road. The knot index maps the flat road to the spline.
            float HeightAt(float along)
            {
                float knot = SplineUtility.ConvertIndexUnit(before, along, PathIndexUnit.Distance, PathIndexUnit.Knot);
                float t = SplineUtility.ConvertIndexUnit(source, knot, PathIndexUnit.Knot, PathIndexUnit.Normalized);
                return container.transform.TransformPoint(source.EvaluatePosition(t)).y;
            }

            (float[] lower, float[] upper) limits = PadLimits(before, count, outlines);
            List<(bool start, float height)> joins = JunctionEnds(container, index, source, outlines, containers);
            float[] old = Enumerable.Range(0, count).Select(sample => HeightAt(AlongOf(sample))).ToArray();
            bool Outside(int sample) => old[sample] < limits.lower[sample] - LevelTolerance ||
                old[sample] > limits.upper[sample] + LevelTolerance;

            // Zones: each run of samples with a pad limit that has a sample outside the limits, and each junction end
            // that is not at the height of the road that it joins.
            var zones = new List<(float from, float to)>();
            for (int sample = 0; sample < count; sample++)
            {
                if (float.IsNegativeInfinity(limits.lower[sample])) continue;
                int first = sample;
                bool outside = false;
                for (; sample < count &&
                    !float.IsNegativeInfinity(limits.lower[sample]); sample++) outside |= Outside(sample);
                if (outside) zones.Add((AlongOf(first), AlongOf(sample - 1)));
            }
            foreach ((bool start, float height) in joins)
            {
                float end = start ? 0f : length;
                if (Mathf.Abs(HeightAt(end) - height) > JoinTolerance) zones.Add((end, end));
            }
            if (zones.Count == 0) return null;
            float ZoneDistance(float along) => zones.Min(zone =>
                along < zone.from ? zone.from - along : along > zone.to ? along - zone.to : 0f);

            List<Vector3> points = AddContactKnots(current, before, zones.Where(zone => zone.to > zone.from).ToList());
            List<Vector2> knots = points.Select(Flat).ToList();
            List<KnotHandle> handles = HandlesAt(container, source, knots);
            (Spline flat, float[] ground) = Samples(knots, true, handles);
            int fitCount = ground.Length;
            (float[] lower, float[] upper) fitLimits = PadLimits(flat, fitCount, outlines);

            // The road keeps its own grade: the steepest grade away from the zones, within MaxGrade and SteepestGrade.
            float away = 0f;
            for (int sample = 1; sample < count; sample++)
            {
                if (ZoneDistance(AlongOf(sample)) <= ShapeLengths[0]) continue;
                away = Mathf.Max(away, Mathf.Atan2(Mathf.Abs(old[sample] - old[sample - 1]), Spacing) * Mathf.Rad2Deg);
            }
            float roadGrade = Mathf.Clamp(away, MaxGrade, SteepestGrade);
            float step = Mathf.Tan(roadGrade * Mathf.Deg2Rad) * Spacing;
            float[] steps = Enumerable.Repeat(step, fitCount).ToArray();

            // The changed stretch grows until every sample in it keeps the grade limit. Both ends always keep a pin.
            var pins = new Dictionary<int, float>();
            float margin = Mathf.Tan(GradeMargin * Mathf.Deg2Rad) * Spacing;
            float[] profile = null;
            foreach (float window in ShapeLengths)
            {
                pins.Clear();
                for (int knot = 0; knot < knots.Count; knot++)
                {
                    float along = SplineUtility.ConvertIndexUnit(flat, knot, PathIndexUnit.Knot,
                        PathIndexUnit.Distance);
                    if (ZoneDistance(along) > window || knot == 0 || knot == knots.Count - 1)
                    {
                        pins[SampleOf(flat, knot, fitCount)] = points[knot].y;
                    }
                }
                foreach ((bool start, float height) in joins) pins[start ? 0 : fitCount - 1] = height;
                profile = Profile(ground, pins, steps, fitLimits);
                bool kept = true;
                for (int sample = 1; sample < fitCount && kept; sample++)
                {
                    if (ZoneDistance(sample * length / (fitCount - 1)) > window) continue;
                    kept = Mathf.Abs(profile[sample] - profile[sample - 1]) <= step + margin;
                }
                if (kept) break;
            }

            float change = 0f;
            for (int sample = 0; sample < fitCount; sample++)
            {
                change = Mathf.Max(change, Mathf.Abs(profile[sample] - HeightAt(sample * length / (fitCount - 1))));
            }
            string written = WriteRoad(container, index, knots, flat, ground, pins, steps, fitLimits, handles);
            if (knots.Count != current.Count) MoveWidths(container, index, before, flat);
            IEnumerable<string> pads = outlines.Where(entry => zones.Any(zone =>
                    SignedDistance(entry.outline, Flat(before.EvaluatePosition(0.5f * (zone.from +
                        zone.to) / length))) <
                    RoadHalfWidth + EmbankmentReach))
                .Select(entry => entry.pad.name);
            return container.name + " " + index + ": levelled at " + string.Join(", ",
                pads.DefaultIfEmpty("junction ends")) +
                ", grade limit " + roadGrade.ToString("F1") + " deg, " + (knots.Count - current.Count) +
                " knots added, largest height change " + change.ToString("F2") + " m, " + written;
        }

        // The lowest and highest height of each sample. Where the road surface meets a pad, the road is at the pad
        // level. Farther out, the road may leave the pad level by the bank grade times the distance from the road edge
        // to the pad, so its embankment does not tilt the pad edge. Samples without a pad within the embankment reach
        // have no limit.
        private static (float[] lower, float[] upper) PadLimits(Spline flat, int count,
            List<(Transform pad, Vector2[] outline)> outlines)
        {
            var lower = new float[count];
            var upper = new float[count];
            float bank = Mathf.Tan(BankGrade * Mathf.Deg2Rad);
            for (int sample = 0; sample < count; sample++)
            {
                lower[sample] = float.NegativeInfinity;
                upper[sample] = float.PositiveInfinity;
                Vector2 point = Flat(flat.EvaluatePosition(sample / (float)(count - 1)));
                foreach ((Transform pad, Vector2[] outline) in outlines)
                {
                    float gap = SignedDistance(outline, point) - RoadHalfWidth;
                    if (gap > EmbankmentReach) continue;
                    float allowed = Mathf.Max(0f, gap) * bank;
                    lower[sample] = Mathf.Max(lower[sample], pad.position.y - allowed);
                    upper[sample] = Mathf.Min(upper[sample], pad.position.y + allowed);
                }
                // Between two pads at different levels, the road takes the middle of the two limits.
                if (lower[sample] > upper[sample]) lower[sample] = upper[sample] = 0.5f * (lower[sample] +
                    upper[sample]);
            }
            return (lower, upper);
        }

        // Adds knots at both ends of each contact stretch, at every ContactKnotStep inside it and one step outside it,
        // so the spline can hold the pad level. The new knots lie on the old flat curve with the old height.
        private static List<Vector3> AddContactKnots(List<Vector3> current, Spline before, List<(float from,
            float to)> zones)
        {
            float length = before.GetLength();
            var knots = current.Select((point, knot) =>
                (along: SplineUtility.ConvertIndexUnit(before, knot, PathIndexUnit.Knot, PathIndexUnit.Distance),
                    point)).ToList();
            foreach ((float from, float to) in zones)
            {
                var places = new List<float> { from - ContactKnotStep, from, to, to + ContactKnotStep };
                int inside = Mathf.FloorToInt((to - from) / ContactKnotStep);
                for (int step = 1; step <= inside; step++) places.Add(from + step * (to - from) / (inside + 1));
                foreach (float along in places)
                {
                    if (along < KnotGap || along > length - KnotGap) continue;
                    if (knots.Any(knot => Mathf.Abs(knot.along - along) < KnotGap)) continue;
                    Vector3 flatPoint = before.EvaluatePosition(along / length);
                    int next = knots.FindIndex(knot => knot.along > along);
                    (float along, Vector3 point) a = knots[next - 1], b = knots[next];
                    float height = Mathf.Lerp(a.point.y, b.point.y, (along - a.along) / Mathf.Max(b.along - a.along,
                        1e-3f));
                    knots.Insert(next, (along, new Vector3(flatPoint.x, height, flatPoint.z)));
                }
            }
            return knots.Select(knot => knot.point).ToList();
        }

        // The width points are knot positions. They move to the new knots at the same distance along the road.
        private static void MoveWidths(SplineContainer container, int index, Spline before, Spline after)
        {
            var path = container.GetComponent<LoogaSplinePath>();
            if (!path || index >= path.Widths.Count || path.Widths[index] == null ||
                path.Widths[index].Points.Count == 0) return;
            var widths = new List<LoogaSplinePath.SplineWidths>();
            for (int spline = 0; spline < container.Splines.Count; spline++)
            {
                var copy = new LoogaSplinePath.SplineWidths();
                copy.Set(spline < path.Widths.Count && path.Widths[spline] != null
                    ? path.Widths[spline].Points.ToList()
                    : new List<Vector2>());
                widths.Add(copy);
            }
            float length = after.GetLength();
            widths[index].Set(widths[index].Points.Select(point => new Vector2(SplineUtility.ConvertIndexUnit(after,
                Mathf.Clamp(SplineUtility.ConvertIndexUnit(before, point.x, PathIndexUnit.Knot, PathIndexUnit.Distance),
                    0f,
                    length), PathIndexUnit.Distance, PathIndexUnit.Knot), point.y)).ToList());
            Undo.RecordObject(path, "Move road widths");
            path.SetSpline(container, widths, path.WidthEasing);
            path.MarkChanged();
            EditorUtility.SetDirty(path);
        }

        // Lists the roads with samples outside the pad limits.
        private static string CheckPadContacts(List<SplineContainer> levelled, List<(Transform pad,
            Vector2[] outline)> outlines)
        {
            var problems = new StringBuilder();
            int checkedSamples = 0;
            foreach (SplineContainer container in levelled)
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    var spline = new Spline(container.Splines[index]);
                    if (spline.Count < 2) continue;
                    List<Vector2> points = spline.Select(knot =>
                        Flat(container.transform.TransformPoint(knot.Position))).ToList();
                    Spline flat = FlatSpline(points, HandlesAt(container, spline, points));
                    int count = Mathf.Max(2, Mathf.CeilToInt(flat.GetLength() / Spacing) + 1);
                    (float[] lower, float[] upper) = PadLimits(flat, count, outlines);
                    float worst = 0f;
                    Vector3 worstAt = default;
                    for (int sample = 0; sample < count; sample++)
                    {
                        if (float.IsNegativeInfinity(lower[sample])) continue;
                        checkedSamples++;
                        float knot = SplineUtility.ConvertIndexUnit(flat, sample / (float)(count - 1),
                            PathIndexUnit.Normalized,
                            PathIndexUnit.Knot);
                        Vector3 point = container.transform.TransformPoint(spline.EvaluatePosition(
                            SplineUtility.ConvertIndexUnit(spline, knot, PathIndexUnit.Knot,
                                PathIndexUnit.Normalized)));
                        float outside = Mathf.Max(lower[sample] - point.y, point.y - upper[sample]);
                        if (outside <= worst) continue;
                        worst = outside;
                        worstAt = point;
                    }
                    if (worst <= LevelTolerance) continue;
                    problems.AppendLine("Pad contact check: " + container.name + " " + index + " is up to " +
                        worst.ToString("F2") + " m outside the pad limits at (" + worstAt.x.ToString("F0") + ", " +
                        worstAt.z.ToString("F0") + ").");
                }
            }
            return problems.Length > 0 ? problems.ToString()
                : "Pad contact check: all " + checkedSamples + " road samples near pads keep the pad limits.\n";
        }
        #endregion

        #region Cross slope
        /// <summary>
        /// Measures the sideways tilt of the terrain under every road with a height stamp, every CrossStep meters at
        /// CrossOffset meters on each side of the centre line, and lists the samples steeper than MaxCrossSlope. Run it
        /// when the stamp system is idle.
        /// </summary>
        public static string CheckCrossSlope(LoogaRoadNetwork network)
        {
            _network = network;
            LoogaStampWorld world = _network.World;
            if (!IsIdle(world))
            {
                return "Cross slope check: the stamp world is not idle. Updating " + (world && world.IsUpdating) +
                    ", heights synchronized " + (world && world.HeightsSynchronized) + "\n";
            }
            (SplineContainer[] containers, List<(Transform pad, Vector2[] outline)> outlines) = RoadsAndPads();
            var steep = new List<(float angle, string where)>();
            int samples = 0;
            foreach (SplineContainer container in containers.Where(HasHeightStamp))
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    var spline = new Spline(container.Splines[index]);
                    if (spline.Count < 2) continue;
                    float length = spline.CalculateLength(container.transform.localToWorldMatrix);
                    for (float along = 0f; along <= length; along += CrossStep)
                    {
                        float t = along / length;
                        Vector3 point = container.transform.TransformPoint(spline.EvaluatePosition(t));
                        Vector3 tangent = container.transform.TransformDirection(spline.EvaluateTangent(t));
                        Vector3 side = Vector3.Cross(Vector3.up, new Vector3(tangent.x, 0f,
                            tangent.z).normalized) * CrossOffset;
                        float left, right;
                        try
                        {
                            left = _network.Ground(point.x - side.x, point.z - side.z);
                            right = _network.Ground(point.x + side.x, point.z + side.z);
                        }
                        catch (InvalidOperationException)
                        {
                            continue;
                        }
                        samples++;
                        float angle = Mathf.Atan2(Mathf.Abs(left - right), 2f * CrossOffset) * Mathf.Rad2Deg;
                        if (angle <= MaxCrossSlope) continue;
                        (Transform pad, Vector2[] outline) nearest = outlines
                            .OrderBy(entry => SignedDistance(entry.outline, Flat(point))).FirstOrDefault();
                        string padNote = nearest.pad
                            ? ", " + SignedDistance(nearest.outline, Flat(point)).ToString("F0") + " m from " +
                                nearest.pad.name
                            : "";
                        steep.Add((angle, container.name + " " + index + " at " + along.ToString("F0") + " m (" +
                            point.x.ToString("F0") + ", " + point.z.ToString("F0") + ")" + padNote));
                    }
                }
            }
            var report = new StringBuilder("Cross slope check: " + steep.Count + " of " + samples +
                " samples steeper than " +
                MaxCrossSlope.ToString("F0") + " deg.\n");
            foreach ((float angle, string where) in steep.OrderByDescending(entry =>
                entry.angle).Take(CrossReportCount))
            {
                report.AppendLine("  " + angle.ToString("F1") + " deg: " + where);
            }
            return report.ToString();
        }

        /// <summary>
        /// Runs <see cref="CheckCrossSlope"/> once after the next finished terrain update and logs the report.
        /// </summary>
        public static void CheckCrossSlopeAfterUpdate(LoogaRoadNetwork network)
        {
            LoogaStampWorld world = network.World;
            if (!world) return;
            bool finished = false;
            // Placements are the last stage of a terrain update.
            Action<Rect> listener = _ => finished = true;
            EditorApplication.CallbackFunction wait = null;
            wait = () =>
            {
                if (!world)
                {
                    EditorApplication.update -= wait;
                    return;
                }
                if (!finished || !IsIdle(world)) return;
                world.PlacementsUpdated -= listener;
                EditorApplication.update -= wait;
                string report = CheckCrossSlope(network);
                if (report.StartsWith("Cross slope check: 0 ")) Debug.Log("[LoogaRoadNetworkTools] " + report);
                else Debug.LogWarning("[LoogaRoadNetworkTools] " + report);
            };
            world.PlacementsUpdated += listener;
            EditorApplication.update += wait;
        }
        #endregion

        // The flat spline through the knots and its ground samples at the profile spacing. With beside, the ground is
        // the mean of the two points at BesideDistance on each side: the ground under an existing road is its own cut
        // or fill.
        private static (Spline flat, float[] ground) Samples(List<Vector2> knots, bool beside = false,
            List<KnotHandle> handles = null)
        {
            Spline flat = FlatSpline(knots, handles);
            int count = Mathf.Max(2, Mathf.CeilToInt(flat.GetLength() / Spacing) + 1);
            var ground = new float[count];
            for (int sample = 0; sample < count; sample++)
            {
                float t = sample / (float)(count - 1);
                Vector3 point = flat.EvaluatePosition(t);
                if (!beside)
                {
                    ground[sample] = _network.Ground(point.x, point.z);
                    continue;
                }
                Vector3 side = Vector3.Cross(Vector3.up,
                    ((Vector3)flat.EvaluateTangent(t)).normalized) * BesideDistance;
                ground[sample] = 0.5f * (_network.Ground(point.x + side.x, point.z + side.z) +
                    _network.Ground(point.x - side.x, point.z - side.z));
            }
            return (flat, ground);
        }

        // The flat spline through the knots. A knot with handles keeps them; the others are auto smooth.
        private static Spline FlatSpline(List<Vector2> knots, List<KnotHandle> handles = null)
        {
            var flat = new Spline();
            for (int index = 0; index < knots.Count; index++)
            {
                var position = new float3(knots[index].x, 0f, knots[index].y);
                if (handles == null || handles[index].Auto)
                {
                    flat.Add(new BezierKnot(position), TangentMode.AutoSmooth);
                    continue;
                }
                KnotHandle handle = handles[index];
                flat.Add(new BezierKnot(position, new float3(handle.In.x, 0f, handle.In.y), new float3(handle.Out.x, 0f,
                    handle.Out.y), quaternion.identity), TangentMode.Broken);
            }
            return flat;
        }

        private static int SampleOf(Spline flat, int knot, int count) => Mathf.RoundToInt(
            SplineUtility.ConvertIndexUnit(flat, knot, PathIndexUnit.Knot, PathIndexUnit.Normalized) * (count - 1));

        // Writes the knots with the heights of the profile and returns the grade and earthwork summary. A knot with
        // handles keeps their horizontal parts and rises with the profile grade at the knot. Without handles, a knot
        // keeps the handles of the old knot at its place.
        private static string WriteRoad(SplineContainer container, int index, List<Vector2> knots, Spline flat,
            float[] ground, Dictionary<int, float> pins, float[] steps = null, (float[] lower,
                float[] upper) limits = default,
            List<KnotHandle> handles = null)
        {
            int count = ground.Length;
            Spline spline = container.Splines[index];
            handles ??= HandlesAt(container, spline, knots);
            float[] height = Profile(ground, pins, steps, limits);
            float sampleLength = flat.GetLength() / Mathf.Max(1, count - 1);
            Undo.RecordObject(container, "Set road knots");
            spline.Clear();
            float steepest = 0f;
            Vector3 previous = default;
            for (int knot = 0; knot < knots.Count; knot++)
            {
                int sample = SampleOf(flat, knot, count);
                var world = new Vector3(knots[knot].x, height[sample], knots[knot].y);
                var local = new float3(container.transform.InverseTransformPoint(world));
                KnotHandle handle = handles[knot];
                if (handle.Auto)
                {
                    spline.Add(new BezierKnot(local), TangentMode.AutoSmooth);
                }
                else
                {
                    int low = Mathf.Max(0, sample - 1), high = Mathf.Min(count - 1, sample + 1);
                    float grade = (height[high] - height[low]) / Mathf.Max(sampleLength * (high - low), 1e-3f);
                    // An end at a bridge continues the deck grade, so the road meets the deck without a kink. The
                    // profile grade there can be much steeper: it follows the ground just before the deck.
                    bool end = knot == 0 || knot == knots.Count - 1;
                    if (end && TryDeckGrade(knots[knot], knot == 0, out float deck)) grade = deck;
                    Vector3 tangentIn = container.transform.InverseTransformVector(new Vector3(handle.In.x,
                        -grade * handle.In.magnitude, handle.In.y));
                    Vector3 tangentOut = container.transform.InverseTransformVector(new Vector3(handle.Out.x,
                        grade * handle.Out.magnitude, handle.Out.y));
                    // Aligned handles stay aligned: the profile grade keeps them in one line.
                    spline.Add(new BezierKnot(local, tangentIn, tangentOut, quaternion.identity),
                        Aligned(handle.In, handle.Out) ? TangentMode.Continuous : TangentMode.Broken);
                }
                if (knot > 0)
                {
                    float run = Vector2.Distance(Flat(world), Flat(previous));
                    if (run > 0.5f) steepest = Mathf.Max(steepest, Mathf.Atan2(Mathf.Abs(world.y - previous.y),
                        run) * Mathf.Rad2Deg);
                }
                previous = world;
            }
            EditorUtility.SetDirty(container);
            float earthwork = 0f;
            for (int sample = 0; sample < count; sample++) earthwork = Mathf.Max(earthwork,
                Mathf.Abs(height[sample] - ground[sample]));
            return "steepest " + steepest.ToString("F1") + " deg, largest cut or fill " + earthwork.ToString("F1") +
                " m.";
        }

        // The grade along the spline at a road end within BridgeEndReach of a bridge end marker: the deck grade from
        // that marker toward the other one. The spline runs onto the deck at its last knot and away from it at its
        // first knot.
        private static bool TryDeckGrade(Vector2 point, bool first, out float grade)
        {
            foreach ((Transform marker, Transform other, bool _) in BridgeMarkers())
            {
                if (Vector2.Distance(Flat(marker.position), point) >= BridgeEndReach) continue;
                float span = Mathf.Max(Vector2.Distance(Flat(marker.position), Flat(other.position)), 0.01f);
                float rise = (other.position.y - marker.position.y) / span;
                grade = first ? -rise : rise;
                return true;
            }
            grade = 0f;
            return false;
        }

        /// <summary>
        /// Gives each road end at a bridge end marker the deck grade: the end handles keep their horizontal parts and
        /// rise with the deck, so the road meets the deck without a kink. The knot heights stay. Reports each changed
        /// end with its old grade and the largest height difference of its end curve from a straight line.
        /// </summary>
        public static string AlignBridgeEnds(LoogaRoadNetwork network)
        {
            _network = network;
            var report = new StringBuilder();
            foreach (SplineContainer container in _network.GetRoads().Select(road => road.Container))
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    Spline spline = container.Splines[index];
                    if (spline.Count < 2) continue;
                    foreach (int knot in new[] { 0, spline.Count - 1 })
                    {
                        BezierKnot value = spline[knot];
                        TangentMode mode = spline.GetTangentMode(knot);
                        if (mode == TangentMode.AutoSmooth || mode == TangentMode.Linear) continue;
                        Vector2 point = Flat(container.transform.TransformPoint(value.Position));
                        if (!TryDeckGrade(point, knot == 0, out float grade)) continue;
                        Vector3 tangentIn = container.transform.TransformVector(math.rotate(value.Rotation,
                            value.TangentIn));
                        Vector3 tangentOut = container.transform.TransformVector(math.rotate(value.Rotation,
                            value.TangentOut));
                        Vector3 along = knot == 0 ? tangentOut : -tangentIn;
                        float before = Mathf.Atan2(along.y, Flat(along).magnitude) * Mathf.Rad2Deg;
                        Vector3 newIn = new Vector3(tangentIn.x, -grade * Flat(tangentIn).magnitude, tangentIn.z);
                        Vector3 newOut = new Vector3(tangentOut.x, grade * Flat(tangentOut).magnitude, tangentOut.z);
                        if (Vector3.Distance(newIn, tangentIn) < 0.001f &&
                            Vector3.Distance(newOut, tangentOut) < 0.001f) continue;
                        Undo.RecordObject(container, "Align bridge ends");
                        spline.SetTangentMode(knot, TangentMode.Broken);
                        spline.SetKnot(knot, new BezierKnot(value.Position,
                            container.transform.InverseTransformVector(newIn),
                            container.transform.InverseTransformVector(newOut), quaternion.identity));
                        spline.SetTangentMode(knot, mode);
                        EditorUtility.SetDirty(container);
                        report.AppendLine(container.name + " " + index + (knot == 0 ? " start" : " end") + ": grade " +
                            before.ToString("F1") + " to " + (Mathf.Atan(grade) * Mathf.Rad2Deg).ToString("F1") +
                            " deg along the spline, end curve " + EndCurveBulge(spline, knot == 0).ToString("F2") +
                            " m from a straight line.");
                    }
                }
            }
            return report.Length > 0 ? report.ToString() : "Every road end at a bridge has the deck grade.";
        }

        // The largest height difference of the end curve of a spline from the straight line between its knots, in
        // meters (positive above).
        private static float EndCurveBulge(Spline spline, bool first)
        {
            int curve = first ? 0 : spline.Count - 2;
            float t0 = SplineUtility.CurveToSplineT(spline, curve);
            float t1 = SplineUtility.CurveToSplineT(spline, curve + 1);
            float y0 = spline.EvaluatePosition(t0).y, y1 = spline.EvaluatePosition(t1).y;
            float worst = 0f;
            for (int step = 1; step < 20; step++)
            {
                float u = step / 20f;
                float offset = spline.EvaluatePosition(Mathf.Lerp(t0, t1, u)).y - Mathf.Lerp(y0, y1, u);
                if (Mathf.Abs(offset) > Mathf.Abs(worst)) worst = offset;
            }
            return worst;
        }

        // The road profile of the old road planner: smoothed ground, the pins, a grade limit in both directions and a
        // light rounding that keeps the pins. Steps holds the height change limit of each sample; null gives MaxGrade
        // everywhere. Limits holds the lowest and highest height of each sample (PadLimits). The limits win over the
        // grade limit.
        private static float[] Profile(float[] ground, Dictionary<int, float> pins, float[] steps = null,
            (float[] lower, float[] upper) limits = default)
        {
            int count = ground.Length;
            float[] height = Smooth(ground, Smoothing / Spacing);
            float uniform = Mathf.Tan(MaxGrade * Mathf.Deg2Rad) * Spacing;
            float Step(int a, int b) => steps == null ? uniform : Mathf.Min(steps[a], steps[b]);
            // The limits apply inside each sweep, so the backward sweep carries a pad level up the road at the grade
            // limit.
            float Bound(int index, float value) => limits.lower == null
                ? value
                : Mathf.Clamp(value, limits.lower[index], limits.upper[index]);
            for (int pass = 0; pass < 4; pass++)
            {
                foreach ((int index, float value) in pins) height[index] = Bound(index, value);
                for (int index = 1; index < count; index++)
                {
                    if (pins.ContainsKey(index)) continue;
                    float step = Step(index, index - 1);
                    height[index] = Bound(index,
                        Mathf.Clamp(height[index], height[index - 1] - step, height[index - 1] + step));
                }
                for (int index = count - 2; index >= 0; index--)
                {
                    if (pins.ContainsKey(index)) continue;
                    float step = Step(index, index + 1);
                    height[index] = Bound(index,
                        Mathf.Clamp(height[index], height[index + 1] - step, height[index + 1] + step));
                }
            }
            float[] rounded = Smooth(height, 2f);
            foreach ((int index, float value) in pins) rounded[index] = value;
            Limit(rounded, limits);
            return rounded;
        }

        private static void Limit(float[] height, (float[] lower, float[] upper) limits)
        {
            if (limits.lower == null) return;
            for (int index = 0; index < height.Length; index++)
            {
                height[index] = Mathf.Clamp(height[index], limits.lower[index], limits.upper[index]);
            }
        }

        private static float[] Smooth(float[] values, float sigma)
        {
            int radius = Mathf.CeilToInt(sigma * 2.5f);
            var result = new float[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                float sum = 0f, weight = 0f;
                for (int offset = -radius; offset <= radius; offset++)
                {
                    int sample = Mathf.Clamp(index + offset, 0, values.Length - 1);
                    float w = Mathf.Exp(-0.5f * offset * offset / (sigma * sigma));
                    sum += values[sample] * w;
                    weight += w;
                }
                result[index] = sum / weight;
            }
            return result;
        }

        // The height of the nearest other road within the junction reach. The distance is horizontal: a 3D nearest
        // point from a point at height 0 prefers the low parts of a road and misses junctions on hills.
        private static bool TryJoin(SplineContainer own, int ownIndex, Vector2 point,
            IEnumerable<SplineContainer> containers,
            out float height)
        {
            height = 0f;
            float best = JunctionReach;
            foreach (SplineContainer container in containers)
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    if (container == own && index == ownIndex) continue;
                    // A copy, because the length cache of a scene spline can be stale after an undo.
                    var spline = new Spline(container.Splines[index]);
                    if (spline.Count < 2) continue;
                    int steps = Mathf.Max(2, Mathf.CeilToInt(spline.GetLength() / JoinStep));
                    for (int step = 0; step <= steps; step++)
                    {
                        Vector3 world = container.transform.TransformPoint(spline.EvaluatePosition(step /
                            (float)steps));
                        float distance = Vector2.Distance(Flat(world), point);
                        if (distance >= best) continue;
                        best = distance;
                        height = world.y;
                    }
                }
            }
            return best < JunctionReach;
        }

        // Edited roads lose their old flare widths, because the widths are knot positions. An end at a pad widens
        // again.
        private static void SetFlares(SplineContainer container, IEnumerable<int> indexes,
            List<(Transform pad, Vector2[] outline)> outlines)
        {
            var path = RequireComponent<LoogaSplinePath>(container.transform, container.name);
            var widths = new List<LoogaSplinePath.SplineWidths>();
            for (int index = 0; index < container.Splines.Count; index++)
            {
                var copy = new LoogaSplinePath.SplineWidths();
                copy.Set(index < path.Widths.Count && path.Widths[index] != null
                    ? path.Widths[index].Points.ToList()
                    : new List<Vector2>());
                widths.Add(copy);
            }
            foreach (int index in indexes)
            {
                Spline spline = container.Splines[index];
                float length = spline.GetLength();
                var points = new List<Vector2>();
                int last = spline.Count - 1;
                foreach (int end in new[] { 0, last })
                {
                    Vector2 point = Flat(container.transform.TransformPoint(spline[end].Position));
                    if (!outlines.Any(entry => SignedDistance(entry.outline, point) < PadEndReach)) continue;
                    float distance = Mathf.Min(FlareLength, length * 0.45f);
                    float along = end == 0 ? distance : length - distance;
                    points.Add(new Vector2(end, FlareWidth));
                    points.Add(new Vector2(SplineUtility.ConvertIndexUnit(spline, along, PathIndexUnit.Distance,
                        PathIndexUnit.Knot), 0f));
                }
                widths[index].Set(points);
            }
            Undo.RecordObject(path, "Set road flares");
            path.SetSpline(container, widths, path.WidthEasing);
            path.MarkChanged();
            EditorUtility.SetDirty(path);
        }

        // Removes the spline and its width entry, so the widths stay aligned with the spline indexes.
        private static void RemoveSpline(SplineContainer container, int index)
        {
            var path = container.GetComponent<LoogaSplinePath>();
            if (path)
            {
                var widths = path.Widths.ToList();
                if (index < widths.Count) widths.RemoveAt(index);
                Undo.RecordObject(path, "Remove road");
                path.SetSpline(container, widths, path.WidthEasing);
                path.MarkChanged();
                EditorUtility.SetDirty(path);
            }
            Undo.RecordObject(container, "Remove road");
            container.RemoveSplineAt(index);
            EditorUtility.SetDirty(container);
        }
        #endregion

        #region Merging
        /// <summary>
        /// Joins each two roads that meet end to end, with no other road at the meeting point, into one spline, so the
        /// road continues without a seam. Ends at a bridge end stay apart. Later spline indexes move down, and the pads
        /// link the road knots again. Knot heights do not change.
        /// </summary>
        /// <param name="backupFolder">Folder for the JSON backups of the pads and road containers, or null for no
        /// backup.</param>
        public static string MergeContinuations(LoogaRoadNetwork network, string backupFolder)
        {
            _network = network;
            (SplineContainer[] containers, List<(Transform pad, Vector2[] outline)> outlines) = RoadsAndPads();
            if (backupFolder != null) Backup(backupFolder, outlines.Select(entry => entry.pad), containers);
            string report = MergeContinuations(containers, outlines);
            foreach ((Transform pad, Vector2[] _) in outlines)
            {
                LoogaRoadPads.LinkRoads(pad);
            }
            RequestFullUpdate();
            EditorSceneManager.MarkSceneDirty(_network.gameObject.scene);
            return report;
        }

        private static string MergeContinuations(SplineContainer[] containers,
            List<(Transform pad, Vector2[] outline)> outlines)
        {
            List<Vector2> bridgeEnds = BridgeEnds(containers).Select(end => Flat(end.end)).ToList();
            var report = new StringBuilder();
            foreach (SplineContainer container in containers)
            {
                // A merge moves the spline indexes, so the search starts again after each one.
                while (TryMergeOne(container, containers, outlines, bridgeEnds,
                    out string line)) report.AppendLine(line);
            }
            return report.Length > 0 ? report.ToString() : "No roads meet end to end.\n";
        }

        private static bool TryMergeOne(SplineContainer container, SplineContainer[] containers,
            List<(Transform pad, Vector2[] outline)> outlines, List<Vector2> bridgeEnds, out string line)
        {
            line = null;
            var ends = new List<(int spline, bool start, Vector2 point)>();
            for (int index = 0; index < container.Splines.Count; index++)
            {
                Spline spline = container.Splines[index];
                if (spline.Count < 2) continue;
                ends.Add((index, true, Flat(container.transform.TransformPoint(spline[0].Position))));
                ends.Add((index, false, Flat(container.transform.TransformPoint(spline[spline.Count - 1].Position))));
            }
            foreach ((int spline, bool start, Vector2 point) a in ends)
            {
                var partners = ends.Where(b => b.spline != a.spline && Vector2.Distance(a.point,
                    b.point) < MergeDistance)
                    .ToList();
                if (partners.Count != 1) continue;
                (int spline, bool start, Vector2 point) b = partners[0];
                Vector2 meet = 0.5f * (a.point + b.point);
                if (bridgeEnds.Any(end => Vector2.Distance(end, meet) < MergeBridgeClearance)) continue;
                // Two roads that meet at a pad both run to the pad. The pad mouth joins them.
                if (outlines.Any(entry => SignedDistance(entry.outline, meet) < MergePadClearance)) continue;
                if (OtherRoadNear(meet, containers, container, a.spline, b.spline)) continue;
                line = Merge(container, a.spline, a.start, b.spline, b.start);
                return true;
            }
            return false;
        }

        // True when a road other than the two splines passes within the junction reach of the point.
        private static bool OtherRoadNear(Vector2 point, IEnumerable<SplineContainer> containers, SplineContainer own,
            int first, int second)
        {
            foreach (SplineContainer container in containers)
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    if (container == own && (index == first || index == second)) continue;
                    var spline = new Spline(container.Splines[index]);
                    if (spline.Count < 2) continue;
                    int steps = Mathf.Max(2, Mathf.CeilToInt(spline.GetLength() / JoinStep));
                    for (int step = 0; step <= steps; step++)
                    {
                        Vector3 world = container.transform.TransformPoint(spline.EvaluatePosition(step /
                            (float)steps));
                        if (Vector2.Distance(Flat(world), point) < JunctionReach) return true;
                    }
                }
            }
            return false;
        }

        // Appends spline b to spline a at their meeting ends and removes b. A reversed spline swaps the tangents of its
        // knots. The meeting knot lies between the two ends. Knots closer than MergeKnotGap to it go, because the old
        // ends often turn toward each other, and the meeting knot and its neighbours are auto smooth, so the road runs
        // on without a kink.
        private static string Merge(SplineContainer container, int a, bool aStart, int b, bool bStart)
        {
            Spline first = container.Splines[a], second = container.Splines[b];
            // The head ends at the meeting point and the tail starts there.
            List<(BezierKnot knot, TangentMode mode)> head = OrderedKnots(first, aStart);
            List<(BezierKnot knot, TangentMode mode)> tail = OrderedKnots(second, !bStart);
            float3 headEnd = head[head.Count - 1].knot.Position, tailStart = tail[0].knot.Position;
            float3 meet = 0.5f * (headEnd + tailStart);
            float step = Mathf.Abs(headEnd.y - tailStart.y);
            float gap = math.distance(headEnd.xz, tailStart.xz);
            while (head.Count > 2 && math.distance(head[head.Count - 2].knot.Position.xz, meet.xz) < MergeKnotGap)
            {
                head.RemoveAt(head.Count - 2);
            }
            while (tail.Count > 2 && math.distance(tail[1].knot.Position.xz, meet.xz) < MergeKnotGap) tail.RemoveAt(1);
            head[head.Count - 2] = (head[head.Count - 2].knot, TangentMode.AutoSmooth);
            tail[1] = (tail[1].knot, TangentMode.AutoSmooth);

            var path = container.GetComponent<LoogaSplinePath>();
            if (path)
            {
                var widths = new List<LoogaSplinePath.SplineWidths>();
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    var copy = new LoogaSplinePath.SplineWidths();
                    copy.Set(index < path.Widths.Count && path.Widths[index] != null
                        ? path.Widths[index].Points.ToList()
                        : new List<Vector2>());
                    widths.Add(copy);
                }
                // Width points are knot positions: reversed on a reversed spline, shifted after the head.
                IEnumerable<Vector2> Moved(int index, bool reverse, int count, int shift) =>
                    widths[index].Points.Select(
                    point => new Vector2((reverse ? count - 1 - point.x : point.x) + shift, point.y));
                // Width points of removed knots keep their place along the knots that stay, near enough for flares.
                widths[a].Set(Moved(a, aStart, first.Count, 0).Select(point => new Vector2(Mathf.Min(point.x,
                    head.Count - 1),
                        point.y))
                    .Concat(Moved(b, !bStart, second.Count, 0).Select(point => new Vector2(
                        head.Count - 1 + Mathf.Max(0f, point.x - (second.Count - tail.Count)), point.y)))
                    .ToList());
                Undo.RecordObject(path, "Merge roads");
                path.SetSpline(container, widths, path.WidthEasing);
                path.MarkChanged();
                EditorUtility.SetDirty(path);
            }

            Undo.RecordObject(container, "Merge roads");
            first.Clear();
            for (int index = 0; index < head.Count - 1; index++) first.Add(head[index].knot, head[index].mode);
            first.Add(new BezierKnot(meet), TangentMode.AutoSmooth);
            for (int index = 1; index < tail.Count; index++) first.Add(tail[index].knot, tail[index].mode);
            EditorUtility.SetDirty(container);
            RemoveSpline(container, b);
            Vector3 world = container.transform.TransformPoint(meet);
            return container.name + " " + a + " + " + b + " merged at (" + world.x.ToString("F0") + ", " +
                world.z.ToString("F0") + "): gap " + gap.ToString("F2") + " m, height step " + step.ToString("F2") +
                    " m, " +
                first.Count + " knots. Later " + container.name + " indexes moved down by one.";
        }

        // The knots of the spline with their tangent modes, reversed on request. A reversed knot swaps its tangents.
        private static List<(BezierKnot knot, TangentMode mode)> OrderedKnots(Spline spline, bool reverse)
        {
            var knots = new List<(BezierKnot knot, TangentMode mode)>();
            for (int index = 0; index < spline.Count; index++) knots.Add((spline[index], spline.GetTangentMode(index)));
            if (!reverse) return knots;
            knots.Reverse();
            return knots.Select(entry => (new BezierKnot(entry.knot.Position, entry.knot.TangentOut,
                entry.knot.TangentIn,
                entry.knot.Rotation), entry.mode)).ToList();
        }
        #endregion

        #region Network editor
        /// <summary>
        /// Writes the road network for the Road Network Editor page: every pad with its outline, every road spline with
        /// its knots, Bezier handles and tangent modes, and every bridge with its two end markers. Positions are world
        /// meters.
        /// </summary>
        /// <param name="path">The JSON file to write.</param>
        public static string ExportNetwork(LoogaRoadNetwork network, string path)
        {
            _network = network;
            (SplineContainer[] containers, List<(Transform pad, Vector2[] outline)> outlines) = RoadsAndPads();
            Dictionary<char, SplineContainer> planned = PlannerContainers();
            var data = new NetworkData
            {
                exported = DateTime.Now.ToString("s"), roadHalfWidth = RoadHalfWidth, pads = new List<PadData>(),
                roads = new List<RoadData>(), bridges = new List<BridgeData>()
            };
            foreach ((Transform pad, Vector2[] outline) in outlines)
            {
                var poi = pad.GetComponent<LoogaRoadPad>();
                data.pads.Add(new PadData
                {
                    id = PadId(pad.name), name = BareName(pad.name),
                    levelGroup = poi ? poi.LevelGroup : "",
                    paved = pad.GetComponentsInChildren<LoogaTextureStamp>()
                        .Any(stamp => _network.IsPavedLayer(stamp.Layer)),
                    x = pad.position.x, y = pad.position.y, z = pad.position.z, rotY = pad.eulerAngles.y,
                    outline = outline.SelectMany(point => new[] { point.x, point.y }).ToArray()
                });
            }
            foreach (SplineContainer container in containers)
            {
                string prefix = container == planned['M'] ? "M" : container == planned['F'] ? "F" : null;
                if (prefix == null) continue;
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    Spline spline = container.Splines[index];
                    if (spline.Count < 2) continue;
                    var knots = new KnotData[spline.Count];
                    for (int knot = 0; knot < spline.Count; knot++)
                    {
                        BezierKnot value = spline[knot];
                        Vector3 position = container.transform.TransformPoint(value.Position);
                        Vector3 tangentIn = container.transform.TransformVector(math.rotate(value.Rotation,
                            value.TangentIn));
                        Vector3 tangentOut = container.transform.TransformVector(math.rotate(value.Rotation,
                            value.TangentOut));
                        knots[knot] = new KnotData
                        {
                            x = position.x, y = position.y, z = position.z, inX = tangentIn.x, inZ = tangentIn.z,
                            outX = tangentOut.x, outZ = tangentOut.z, mode = ModeOf(container, spline, knot).ToString()
                        };
                    }
                    data.roads.Add(new RoadData
                    {
                        id = prefix + index, container = container.name, index = index,
                        length = spline.CalculateLength(container.transform.localToWorldMatrix), knots = knots
                    });
                }
            }
            foreach (LoogaRoadBridge bridge in Bridges())
            {
                Vector3 start = bridge.StartMarker.position, finish = bridge.FinishMarker.position;
                data.bridges.Add(new BridgeData
                {
                    id = PadId(bridge.name), name = bridge.name, a = new[] { start.x, start.y, start.z },
                    b = new[] { finish.x, finish.y, finish.z }, structure = bridge.StructureDeck
                });
            }
            // The rivers and lakes of the project, so the page shows the water that roads, bridges and pads must work
            // around.
            (List<WaterData> rivers, List<WaterData> lakes) water = WaterSource != null
                ? WaterSource()
                : (new List<WaterData>(), new List<WaterData>());
            data.rivers = water.rivers;
            data.lakes = water.lakes;
            File.WriteAllText(path, JsonUtility.ToJson(data));
            return "Exported " + data.pads.Count + " pads, " + data.roads.Count + " roads, " + data.bridges.Count +
                " bridges, " + data.rivers.Count + " rivers and " + data.lakes.Count + " lakes to " + path + ".";
        }

        /// <summary>
        /// Applies the edits of the Road Network Editor page: moved and turned pads, edited, new and deleted roads with
        /// their Bezier handles, and moved, new and removed bridges. Writes a backup of every pad, road container and
        /// bridge first. Then roads that meet end to end merge, the roads take the pad levels and the junctions get
        /// their shape. The cross slope check runs after the terrain update and writes its report to the console.
        /// </summary>
        /// <param name="editsFolder">Folder with the pads, roads and bridges subfolders of the exported edits.</param>
        /// <param name="backupFolder">Folder for the JSON backups.</param>
        public static string ApplyNetwork(LoogaRoadNetwork network, string editsFolder, string backupFolder)
        {
            _network = network;
            Dictionary<char, SplineContainer> containers = PlannerContainers();
            Dictionary<string, Transform> pads = PadTransforms().ToDictionary(pad => PadId(pad.name), pad => pad);
            Dictionary<string, PadEdit> padEdits = ReadEdits<PadEdit>(Path.Combine(editsFolder, "pads"));
            Dictionary<string, NetworkRoadEdit> roadEdits = ReadEdits<NetworkRoadEdit>(Path.Combine(editsFolder,
                "roads"));
            Dictionary<string, BridgeEdit> bridgeEdits = ReadEdits<BridgeEdit>(Path.Combine(editsFolder, "bridges"));
            var report = new StringBuilder();

            Backup(backupFolder, pads.Values, containers.Values);
            ExportNetwork(network, Path.Combine(backupFolder, "network-before.json"));
            Dictionary<SplineContainer, List<Spline>> before = containers.Values.ToDictionary(container => container,
                container => container.Splines.Select(spline => new Spline(spline)).ToList());
            // The places that the edits reach: the pads before and after their moves, the edited and removed roads and
            // the edited bridges.
            var reached = new List<Vector2>();
            foreach (string id in padEdits.Keys)
            {
                if (pads.TryGetValue(id, out Transform pad)) reached.AddRange(Outline(pad));
            }
            foreach (BridgeEdit edit in bridgeEdits.Values)
            {
                if (edit.a != null && edit.a.Length >= 2) reached.Add(new Vector2(edit.a[0], edit.a[1]));
                if (edit.b != null && edit.b.Length >= 2) reached.Add(new Vector2(edit.b[0], edit.b[1]));
            }
            List<(string bridge, string marker, Vector3 end, float before)> bridgeEnds = BridgeEnds(containers.Values);
            foreach (Transform pad in pads.Values)
            {
                var poi = pad.GetComponent<LoogaRoadPad>();
                if (!poi) continue;
                var links = new SerializedObject(poi);
                links.FindProperty("_roadLinks").arraySize = 0;
                links.ApplyModifiedProperties();
            }
            foreach ((string id, PadEdit edit) in padEdits)
            {
                report.AppendLine(pads.TryGetValue(id, out Transform pad) ? MovePad(pad, edit) : "Pad " + id +
                    " is not in the scene.");
            }

            var edited = new List<(SplineContainer container, int index, List<Vector2> knots,
                List<KnotHandle> handles)>();
            foreach ((string id, NetworkRoadEdit edit) in roadEdits.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (edit.deleted || edit.knots == null || edit.knots.Length < 2) continue;
                SplineContainer container;
                int index;
                if (edit.isNew)
                {
                    container = containers[edit.container == containers['F'].name ? 'F' : 'M'];
                    Undo.RecordObject(container, "Add road");
                    container.AddSpline();
                    index = container.Splines.Count - 1;
                    report.AppendLine(id + " added as " + container.name + " spline " + index + ".");
                }
                else if (!containers.TryGetValue(id[0], out container) || !int.TryParse(id.Substring(1), out index) ||
                         index >= container.Splines.Count)
                {
                    report.AppendLine("Road " + id + " is not in the scene. Skipped.");
                    continue;
                }
                edited.Add((container, index, edit.knots.Select(knot => new Vector2(knot.x, knot.z)).ToList(),
                    edit.knots.Select(knot => new KnotHandle(new Vector2(knot.inX, knot.inZ), new Vector2(knot.outX,
                        knot.outZ),
                        Enum.TryParse(knot.mode, out TangentMode mode) ? mode : TangentMode.AutoSmooth)).ToList()));
            }
            List<(Transform pad, Vector2[] outline)> outlines = pads.Values.Select(pad => (pad, Outline(pad))).ToList();
            for (int pass = 0; pass < 2; pass++)
            {
                foreach ((SplineContainer container, int index, List<Vector2> knots,
                    List<KnotHandle> handles) in edited)
                {
                    string line = SetRoad(container, index, knots, outlines, containers.Values, pass == 1, handles);
                    if (pass == 1) report.AppendLine(line);
                }
            }
            SetFlares(containers['M'], edited.Where(road => road.container == containers['M']).Select(road =>
                road.index),
                outlines);
            foreach ((SplineContainer container, int index, List<Vector2> _, List<KnotHandle> _) in edited)
            {
                reached.AddRange(Along(container, container.Splines[index]));
            }
            foreach (Transform pad in padEdits.Keys.Where(pads.ContainsKey).Select(id => pads[id]))
            {
                reached.AddRange(Outline(pad));
            }
            var deleted = roadEdits.Where(entry => entry.Value.deleted && !entry.Value.isNew &&
                containers.ContainsKey(entry.Key[0]))
                .Select(entry => (container: containers[entry.Key[0]], index: int.Parse(entry.Key.Substring(1)), id:
                    entry.Key))
                .OrderByDescending(road => road.index).ToList();
            foreach ((SplineContainer container, int index, string id) in deleted)
            {
                if (index < before[container].Count) reached.AddRange(Along(container, before[container][index]));
                RemoveSpline(container, index);
                report.AppendLine(id + " removed.");
            }

            SplineContainer[] all = containers.Values.ToArray();
            report.Append(MergeContinuations(all, outlines));
            report.Append(Settle(all, outlines));
            report.Append(KeepUntouched(before, reached));
            report.Append(ApplyBridgeEdits(bridgeEdits, all));
            foreach (Transform pad in pads.Values)
            {
                LoogaRoadPads.LinkRoads(pad);
            }
            // Edited bridges have new ends, so only the other bridges keep their old road ends.
            report.Append(BridgeReport(bridgeEnds.Where(end => !bridgeEdits.ContainsKey(PadId(end.bridge))).ToList(),
                all));
            RequestFullUpdate();
            EditorSceneManager.MarkSceneDirty(_network.gameObject.scene);
            CheckCrossSlopeAfterUpdate(_network);
            report.AppendLine("Spline indexes changed. Export the network again before the next round of edits.");
            report.AppendLine("The cross slope check runs after the terrain update and writes its report to the " +
                "console.");
            return report.ToString();
        }

        // World points along the spline, ReachStep apart.
        private static List<Vector2> Along(SplineContainer container, Spline spline)
        {
            var points = new List<Vector2>();
            if (spline.Count < 2) return points;
            float length = spline.GetLength();
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / ReachStep));
            for (int step = 0; step <= steps; step++)
            {
                points.Add(Flat(container.transform.TransformPoint(spline.EvaluatePosition(step / (float)steps))));
            }
            return points;
        }

        // The settle passes level every road of the network and may add knots. A road whose ends did not move and lie
        // farther than KeepReach from the reached points gets its knots from before the edits back, so a round of edits
        // changes only the roads near it.
        private static string KeepUntouched(Dictionary<SplineContainer, List<Spline>> before, List<Vector2> reached)
        {
            var kept = new List<string>();
            foreach ((SplineContainer container, List<Spline> old) in before)
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    Spline spline = container.Splines[index];
                    if (spline.Count < 2) continue;
                    Spline match = old.FirstOrDefault(candidate => SameEnds(candidate, spline));
                    if (match == null || SameKnots(match, spline)) continue;
                    Vector2 start = Flat(container.transform.TransformPoint(spline[0].Position));
                    Vector2 end = Flat(container.transform.TransformPoint(spline[spline.Count - 1].Position));
                    if (reached.Any(point => Vector2.Distance(point, start) < KeepReach ||
                            Vector2.Distance(point, end) < KeepReach)) continue;
                    Undo.RecordObject(container, "Keep untouched roads");
                    spline.Clear();
                    for (int knot = 0; knot < match.Count; knot++)
                    {
                        spline.Add(match[knot], match.GetTangentMode(knot));
                    }
                    kept.Add(container.name + " " + index);
                }
            }
            return kept.Count == 0 ? "" : "Untouched roads kept as before the edits: " + string.Join(", ", kept) +
                ".\n";
        }

        // The same horizontal positions of the first and the last knot.
        private static bool SameEnds(Spline a, Spline b) => a.Count >= 2 && b.Count >= 2 &&
            Vector2.Distance(Flat(a[0].Position), Flat(b[0].Position)) < 0.05f &&
            Vector2.Distance(Flat(a[a.Count - 1].Position), Flat(b[b.Count - 1].Position)) < 0.05f;

        private static bool SameKnots(Spline a, Spline b)
        {
            if (a.Count != b.Count) return false;
            for (int knot = 0; knot < a.Count; knot++)
            {
                BezierKnot x = a[knot], y = b[knot];
                if (Vector3.Distance(x.Position, y.Position) > 0.001f ||
                    Vector3.Distance(x.TangentIn, y.TangentIn) > 0.001f ||
                    Vector3.Distance(x.TangentOut, y.TangentOut) > 0.001f) return false;
            }
            return true;
        }

        /// <summary>
        /// Puts the road splines, their road paths and the pads back as an <see cref="ApplyNetwork"/> backup holds
        /// them. The bridges are not in the backup and stay as they are. Run it in the editor session that wrote the
        /// backup: the JSON holds object references by instance.
        /// </summary>
        public static string RestoreBackup(LoogaRoadNetwork network, string backupFolder)
        {
            _network = network;
            int roads = 0;
            foreach (SplineContainer container in PlannerContainers().Values)
            {
                string file = Path.Combine(backupFolder, container.name + " - SplineContainer.json");
                if (!File.Exists(file)) continue;
                Undo.RecordObject(container, "Restore road backup");
                EditorJsonUtility.FromJsonOverwrite(File.ReadAllText(file), container);
                EditorUtility.SetDirty(container);
                var path = container.GetComponent<LoogaSplinePath>();
                string pathFile = Path.Combine(backupFolder, container.name + " - LoogaSplinePath.json");
                if (path && File.Exists(pathFile))
                {
                    Undo.RecordObject(path, "Restore road backup");
                    EditorJsonUtility.FromJsonOverwrite(File.ReadAllText(pathFile), path);
                    EditorUtility.SetDirty(path);
                }
                roads++;
            }
            int pads = 0;
            foreach (Transform pad in PadTransforms())
            {
                string name = Path.Combine(backupFolder, "pad-" + PadId(pad.name));
                if (!File.Exists(name + "-transform.json")) continue;
                // The transform backup holds the level in the outline field.
                var saved = JsonUtility.FromJson<PadEdit>(File.ReadAllText(name + "-transform.json"));
                Undo.RecordObject(pad, "Restore road backup");
                pad.SetPositionAndRotation(new Vector3(saved.x, saved.outline[0], saved.z),
                    Quaternion.Euler(pad.eulerAngles.x, saved.rotY, pad.eulerAngles.z));
                var outline = pad.GetComponent<SplineContainer>();
                Undo.RecordObject(outline, "Restore road backup");
                EditorJsonUtility.FromJsonOverwrite(File.ReadAllText(name + "-spline.json"), outline);
                EditorUtility.SetDirty(outline);
                var poi = pad.GetComponent<LoogaRoadPad>();
                if (poi && File.Exists(name + "-links.json"))
                {
                    Undo.RecordObject(poi, "Restore road backup");
                    EditorJsonUtility.FromJsonOverwrite(File.ReadAllText(name + "-links.json"), poi);
                    EditorUtility.SetDirty(poi);
                }
                pads++;
            }
            RequestFullUpdate();
            EditorSceneManager.MarkSceneDirty(_network.gameObject.scene);
            return "Restored " + roads + " road splines and " + pads + " pads from " + backupFolder +
                ". The bridges stay as they are.";
        }

        // Moves, removes and adds bridges. An end marker takes the height of the nearest road end within
        // BridgeEndReach, otherwise the ground height. The bridge parts are built again.
        private static string ApplyBridgeEdits(Dictionary<string, BridgeEdit> edits, SplineContainer[] containers)
        {
            if (edits.Count == 0) return "";
            Transform root = _network.BridgeRoot;
            if (!root)
            {
                var rootObject = new GameObject(BridgeRootName);
                SceneManager.MoveGameObjectToScene(rootObject, _network.gameObject.scene);
                Undo.RegisterCreatedObjectUndo(rootObject, "Add bridges");
                Undo.RecordObject(_network, "Add bridges");
                _network.SetBridgeRoot(rootObject.transform);
                EditorUtility.SetDirty(_network);
                root = rootObject.transform;
            }
            var report = new StringBuilder();
            foreach ((string id, BridgeEdit edit) in edits)
            {
                Transform bridge = root.Cast<Transform>().FirstOrDefault(candidate => PadId(candidate.name) == id);
                if (bridge && bridge.TryGetComponent(out LoogaRoadBridge existing) && existing.StructureDeck)
                {
                    report.AppendLine("Bridge " + bridge.name +
                        " is a structure deck, placed by its structure. Skipped.");
                    continue;
                }
                if (edit.deleted)
                {
                    if (bridge)
                    {
                        report.AppendLine("Bridge " + bridge.name + " removed.");
                        Undo.DestroyObjectImmediate(bridge.gameObject);
                    }
                    continue;
                }
                if (edit.a == null || edit.a.Length < 2 || edit.b == null || edit.b.Length < 2) continue;
                if (!bridge)
                {
                    bridge = new GameObject(string.IsNullOrEmpty(edit.name) ? "Bridge " + id : edit.name).transform;
                    bridge.SetParent(root, false);
                    var start = new GameObject(BridgeStartName).transform;
                    var finish = new GameObject(BridgeFinishName).transform;
                    start.SetParent(bridge, false);
                    finish.SetParent(bridge, false);
                    bridge.gameObject.AddComponent<LoogaRoadBridge>().SetMarkers(start, finish);
                    Undo.RegisterCreatedObjectUndo(bridge.gameObject, "Add bridge");
                    report.AppendLine("Bridge " + bridge.name + " added.");
                }
                var markers = bridge.GetComponent<LoogaRoadBridge>();
                if (!markers || !markers.HasMarkers)
                {
                    report.AppendLine("Bridge " + bridge.name + " has no end markers. Skipped.");
                    continue;
                }
                PlaceBridgeEnd(markers.StartMarker, new Vector2(edit.a[0], edit.a[1]), containers);
                PlaceBridgeEnd(markers.FinishMarker, new Vector2(edit.b[0], edit.b[1]), containers);
            }
            report.Append(LoogaRoadBridges.Build(_network));
            return report.ToString();
        }

        private static void PlaceBridgeEnd(Transform marker, Vector2 point, IEnumerable<SplineContainer> containers)
        {
            float height = _network.Ground(point.x, point.y);
            float nearest = BridgeEndReach;
            foreach (SplineContainer container in containers)
            {
                foreach (Spline spline in container.Splines)
                {
                    if (spline.Count < 2) continue;
                    foreach (int end in new[] { 0, spline.Count - 1 })
                    {
                        Vector3 world = container.transform.TransformPoint(spline[end].Position);
                        float distance = Vector2.Distance(Flat(world), point);
                        if (distance >= nearest) continue;
                        nearest = distance;
                        height = world.y + DeckLift;
                    }
                }
            }
            Undo.RecordObject(marker, "Move bridge end");
            marker.position = new Vector3(point.x, height, point.y);
        }

        // The handles at the points, in world space. The points are knots of the spline or points on its curves, such
        // as knots that a pass adds. A curve with a knot that has handles keeps its shape: the handles of each piece
        // between two points come from a split of the curve (de Casteljau), so a long handle shortens when a point is
        // added next to it, and an added point gets aligned handles. Other points are auto smooth.
        private static List<KnotHandle> HandlesAt(SplineContainer container, Spline spline, List<Vector2> points)
        {
            Spline source = WorldFlat(container, spline);
            List<(int knot, int curve, float t)> places = points.Select(point => Locate(source, point)).ToList();
            bool Explicit(int knot) => knot >= 0 && knot < spline.Count &&
                spline.GetTangentMode(knot) != TangentMode.AutoSmooth;
            var handles = new List<KnotHandle>();
            for (int index = 0; index < points.Count; index++)
            {
                (int knot, int curve, float _) = places[index];
                bool shaped = knot >= 0 ? Explicit(knot) : curve >= 0 && (Explicit(curve) || Explicit(curve + 1));
                if (!shaped)
                {
                    handles.Add(new KnotHandle(Vector2.zero, Vector2.zero, TangentMode.AutoSmooth));
                    continue;
                }
                Vector2 tangentIn = knot >= 0 ? Flat(source[knot].TangentIn) : Vector2.zero;
                Vector2 tangentOut = knot >= 0 ? Flat(source[knot].TangentOut) : Vector2.zero;
                if (index > 0 && TryPiece(source, places[index - 1], places[index], out BezierCurve before))
                {
                    tangentIn = Flat(before.P2 - before.P3);
                }
                if (index < points.Count - 1 && TryPiece(source, places[index], places[index + 1],
                    out BezierCurve after))
                {
                    tangentOut = Flat(after.P1 - after.P0);
                }
                if (knot < 0 && (tangentIn == Vector2.zero || tangentOut == Vector2.zero))
                {
                    handles.Add(new KnotHandle(Vector2.zero, Vector2.zero, TangentMode.AutoSmooth));
                    continue;
                }
                handles.Add(new KnotHandle(tangentIn, tangentOut,
                    knot >= 0 ? ModeOf(container, spline, knot) : TangentMode.Continuous));
            }
            return handles;
        }

        // True when the two handles of a knot point in opposite directions, so the curve has no corner there.
        private static bool Aligned(Vector2 tangentIn, Vector2 tangentOut)
        {
            if (tangentIn.sqrMagnitude < 1e-6f || tangentOut.sqrMagnitude < 1e-6f) return false;
            return Vector2.Dot(tangentIn.normalized, tangentOut.normalized) < -Mathf.Cos(AlignedAngle * Mathf.Deg2Rad);
        }

        // The tangent mode for the editor and the passes: a broken knot with aligned handles counts as smooth.
        private static TangentMode ModeOf(SplineContainer container, Spline spline, int knot)
        {
            TangentMode mode = spline.GetTangentMode(knot);
            if (mode != TangentMode.Broken) return mode;
            BezierKnot value = spline[knot];
            Vector2 tangentIn = Flat(container.transform.TransformVector(math.rotate(value.Rotation, value.TangentIn)));
            Vector2 tangentOut = Flat(container.transform.TransformVector(math.rotate(value.Rotation,
                value.TangentOut)));
            return Aligned(tangentIn, tangentOut) ? TangentMode.Continuous : mode;
        }

        // The spline in world space, flat (height 0), with the handles that its knots have now.
        private static Spline WorldFlat(SplineContainer container, Spline spline)
        {
            var flat = new Spline();
            foreach (BezierKnot knot in spline)
            {
                Vector3 position = container.transform.TransformPoint(knot.Position);
                Vector3 tangentIn = container.transform.TransformVector(math.rotate(knot.Rotation, knot.TangentIn));
                Vector3 tangentOut = container.transform.TransformVector(math.rotate(knot.Rotation, knot.TangentOut));
                flat.Add(new BezierKnot(new float3(position.x, 0f, position.z), new float3(tangentIn.x, 0f,
                    tangentIn.z),
                    new float3(tangentOut.x, 0f, tangentOut.z), quaternion.identity), TangentMode.Broken);
            }
            return flat;
        }

        // A point as a knot of the flat spline (knot index, curve = knot, t = 0) or as a place on one of its curves
        // (knot -1). A point off the spline has knot and curve -1.
        private static (int knot, int curve, float t) Locate(Spline flat, Vector2 point)
        {
            for (int knot = 0; knot < flat.Count; knot++)
            {
                if (Vector2.Distance(Flat(flat[knot].Position), point) <= KeptHandle) return (knot, knot, 0f);
            }
            if (flat.Count < 2) return (-1, -1, 0f);
            SplineUtility.GetNearestPoint(flat, new float3(point.x, 0f, point.y), out float3 nearest,
                out float splineT);
            if (Vector2.Distance(Flat(nearest), point) > OnCurve) return (-1, -1, 0f);
            int curve = flat.SplineToCurveT(splineT, out float curveT);
            return (-1, curve, curveT);
        }

        // The piece of a flat spline curve between two located points on that curve (the second may be the next knot).
        private static bool TryPiece(Spline flat, (int knot, int curve, float t) from, (int knot, int curve,
            float t) to,
            out BezierCurve piece)
        {
            piece = default;
            int curve = from.curve;
            float start = from.t, end = to.t;
            if (to.knot >= 0 && to.knot == curve + 1) end = 1f;
            else if (to.curve != curve) return false;
            if (curve < 0 || curve >= flat.Count - 1 || end <= start + 1e-4f) return false;
            CurveUtility.Split(flat.GetCurve(curve), end, out BezierCurve head, out BezierCurve _);
            CurveUtility.Split(head, start / end, out BezierCurve _, out piece);
            return true;
        }
        #endregion

        #region Widen
        /// <summary>
        /// Widens the roads to the half width of the primary road class and keeps each road edge that runs in line with
        /// a pad side on that line: the road stretch moves away from the pad by the added half width, so all the extra
        /// width goes to the far side. The move tapers out over WidenTaper beyond the stretch. A moved road end takes
        /// the road ends and bridge ends joined to it along, an end on the body of another road stays on that road, and
        /// a road end on a moved stretch moves with it. Then the road stamps take the new widths, the roads settle (pad
        /// levels, nodes, junctions) and the bridges are built again.
        /// </summary>
        /// <param name="backupFolder">Folder for the JSON backups of the pads and road containers, or null for no
        /// backup.</param>
        public static string WidenRoads(LoogaRoadNetwork network, string backupFolder)
        {
            _network = network;
            (SplineContainer[] containers, List<(Transform pad, Vector2[] outline)> outlines) = RoadsAndPads();
            if (backupFolder != null) Backup(backupFolder, outlines.Select(entry => entry.pad), containers);
            var report = new StringBuilder();
            // Old end positions and their moves, for the joined ends and bridge ends.
            var endMoves = new List<(Vector2 at, Vector2 move)>();
            var bodyMoves = new List<(SplineContainer container, int index, Spline before, List<AlignedRun> runs,
                float delta)>();
            var moved = new HashSet<(SplineContainer, int, bool)>();
            foreach (SplineContainer container in containers)
            {
                var path = container.GetComponent<LoogaSplinePath>();
                if (!path) continue;
                float delta = RoadHalfWidth - path.Width;
                if (delta <= 0f) continue;
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    List<AlignedRun> runs = AlignedRuns(container, container.Splines[index], path.Width, outlines);
                    if (runs.Count == 0) continue;
                    var before = new Spline(container.Splines[index]);
                    Undo.RecordObject(container, "Widen roads");
                    ShiftRuns(container, index, runs, delta);
                    MoveWidths(container, index, before, new Spline(container.Splines[index]));
                    bodyMoves.Add((container, index, before, runs, delta));
                    float length = before.GetLength();
                    foreach (bool start in new[] { true, false })
                    {
                        Vector2 move = RunOffset(runs, start ? 0f : length, delta);
                        if (move.sqrMagnitude < 1e-4f) continue;
                        endMoves.Add((Flat(container.transform.TransformPoint(before[start ? 0 :
                            before.Count - 1].Position)), move));
                        moved.Add((container, index, start));
                    }
                    foreach (AlignedRun run in runs)
                    {
                        report.AppendLine(container.name + " " + index + ": " + (run.To - run.From).ToString("F0") +
                            " m in line with " +
                            run.Pad + " (" + run.From.ToString("F0") + "-" + run.To.ToString("F0") +
                                " m along), moved " +
                            delta.ToString("F3") + " m away from the pad.");
                    }
                }
            }
            report.Append(FollowMoves(containers, endMoves, bodyMoves, moved));
            report.Append(LoogaRoadWidths.Apply(_network));
            report.Append(Settle(containers, outlines));
            report.Append(LoogaRoadBridges.Build(_network));
            foreach ((Transform pad, Vector2[] _) in outlines)
            {
                LoogaRoadPads.LinkRoads(pad);
            }
            RequestFullUpdate();
            EditorSceneManager.MarkSceneDirty(_network.gameObject.scene);
            CheckCrossSlopeAfterUpdate(_network);
            return report.ToString();
        }

        // A stretch of a road whose edge on the pad side runs on the line of a pad side: the distances along the road
        // and the unit vector from the pad side toward the road.
        private sealed class AlignedRun
        {
            public string Pad;
            public float From;
            public float To;
            public Vector2 Away;
        }

        // The stretches, at least WidenMinRun long, where a road edge lies within WidenLineTolerance of the line of a
        // pad side and runs parallel to it, from WidenSideReach before the side to WidenSideReach past it, with the
        // road on the far side of the line from the pad.
        private static List<AlignedRun> AlignedRuns(SplineContainer container, Spline scene, float half,
            List<(Transform pad, Vector2[] outline)> outlines)
        {
            var spline = new Spline(scene);
            float length = spline.CalculateLength(container.transform.localToWorldMatrix);
            int count = Mathf.Max(2, Mathf.CeilToInt(length / WidenStep) + 1);
            var centres = new Vector2[count];
            var forwards = new Vector2[count];
            for (int sample = 0; sample < count; sample++)
            {
                float t = sample / (float)(count - 1);
                centres[sample] = Flat(container.transform.TransformPoint(spline.EvaluatePosition(t)));
                forwards[sample] = Flat(container.transform.TransformDirection(spline.EvaluateTangent(t))).normalized;
            }
            var runs = new List<AlignedRun>();
            foreach ((Transform pad, Vector2[] outline) in outlines)
            {
                Vector2 centre = outline.Aggregate(Vector2.zero, (sum, point) => sum + point) / outline.Length;
                for (int side = 0; side < outline.Length; side++)
                {
                    Vector2 a = outline[side], b = outline[(side + 1) % outline.Length];
                    float sideLength = Vector2.Distance(a, b);
                    if (sideLength < 1f) continue;
                    Vector2 direction = (b - a) / sideLength;
                    var away = new Vector2(-direction.y, direction.x);
                    if (Vector2.Dot(centre - a, away) > 0f) away = -away;
                    int first = -1;
                    for (int sample = 0; sample <= count; sample++)
                    {
                        bool aligned = false;
                        if (sample < count)
                        {
                            Vector2 inner = centres[sample] - away * half;
                            float along = Vector2.Dot(inner - a, direction);
                            aligned = Mathf.Abs(Vector2.Dot(forwards[sample], direction)) > WidenParallel &&
                                Mathf.Abs(Vector2.Dot(inner - a, away)) < WidenLineTolerance &&
                                Vector2.Dot(centres[sample] - a, away) > 0f &&
                                along > -WidenSideReach && along < sideLength + WidenSideReach;
                        }
                        if (aligned && first < 0) first = sample;
                        if (aligned || first < 0) continue;
                        float from = first * length / (count - 1), to = (sample - 1) * length / (count - 1);
                        if (to - from >= WidenMinRun) runs.Add(new AlignedRun { Pad = pad.name, From = from, To = to,
                            Away = away });
                        first = -1;
                    }
                }
            }
            return runs;
        }

        // The move of the road at a distance along it: the added half width away from the pad of the strongest run,
        // full inside the run and easing out over WidenTaper beyond it.
        private static Vector2 RunOffset(List<AlignedRun> runs, float along, float delta)
        {
            float best = 0f;
            Vector2 away = Vector2.zero;
            foreach (AlignedRun run in runs)
            {
                float outside = along < run.From ? run.From - along : along > run.To ? along - run.To : 0f;
                float weight = 1f - Mathf.SmoothStep(0f, 1f, outside / WidenTaper);
                if (weight <= best) continue;
                best = weight;
                away = run.Away;
            }
            return away * (delta * best);
        }

        // Adds knots at the run ends and the taper ends (curve splits, so the shape stays), then moves every knot by
        // the run offset at its place.
        private static void ShiftRuns(SplineContainer container, int index, List<AlignedRun> runs, float delta)
        {
            Spline spline = container.Splines[index];
            foreach (AlignedRun run in runs)
            {
                foreach (float along in new[] { run.From - WidenTaper, run.From, run.To, run.To + WidenTaper })
                {
                    InsertKnot(spline, container.transform.localToWorldMatrix, along);
                }
            }
            var copy = new Spline(spline);
            for (int knot = 0; knot < spline.Count; knot++)
            {
                float along = SplineUtility.ConvertIndexUnit(copy, knot, PathIndexUnit.Knot, PathIndexUnit.Distance);
                Vector2 move = RunOffset(runs, along, delta);
                if (move.sqrMagnitude < 1e-6f) continue;
                BezierKnot value = spline[knot];
                value.Position += (float3)container.transform.InverseTransformVector(new Vector3(move.x, 0f, move.y));
                spline[knot] = value;
            }
            EditorUtility.SetDirty(container);
        }

        // Adds a knot at a distance along the spline by a split of its curve, so the shape stays. The neighbour knots
        // keep their handles, shortened to the split. Nothing changes when a knot is within KnotGap of the place.
        private static void InsertKnot(Spline spline, Matrix4x4 localToWorld, float along)
        {
            float length = spline.CalculateLength(localToWorld);
            if (along < KnotGap || along > length - KnotGap) return;
            var copy = new Spline(spline);
            for (int knot = 0; knot < spline.Count; knot++)
            {
                if (Mathf.Abs(SplineUtility.ConvertIndexUnit(copy, knot, PathIndexUnit.Knot,
                    PathIndexUnit.Distance) - along) <
                    KnotGap) return;
            }
            int curve = copy.SplineToCurveT(along / length, out float curveT);
            CurveUtility.Split(copy.GetCurve(curve), curveT, out BezierCurve head, out BezierCurve tail);
            BezierKnot before = copy[curve], after = copy[curve + 1];
            spline.SetTangentMode(curve, TangentMode.Broken);
            spline.SetTangentMode(curve + 1, TangentMode.Broken);
            spline[curve] = new BezierKnot(before.Position, math.rotate(before.Rotation, before.TangentIn),
                head.P1 - head.P0, quaternion.identity);
            spline[curve + 1] = new BezierKnot(after.Position, tail.P2 - tail.P3,
                math.rotate(after.Rotation, after.TangentOut), quaternion.identity);
            spline.Insert(curve + 1, new BezierKnot(head.P3, head.P2 - head.P3, tail.P1 - tail.P0, quaternion.identity),
                TangentMode.Broken);
        }

        // Moves the road ends and bridge ends that were joined to a moved road end by the same move, puts a moved end
        // that stood on the body of another road back on that road, and moves the road ends on a moved stretch with it.
        private static string FollowMoves(SplineContainer[] containers, List<(Vector2 at, Vector2 move)> endMoves,
            List<(SplineContainer container, int index, Spline before, List<AlignedRun> runs, float delta)> bodyMoves,
            HashSet<(SplineContainer, int, bool)> moved)
        {
            var report = new StringBuilder();
            foreach (SplineContainer container in containers)
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    Spline spline = container.Splines[index];
                    if (spline.Count < 2) continue;
                    foreach (bool start in new[] { true, false })
                    {
                        if (moved.Contains((container, index, start))) continue;
                        int knot = start ? 0 : spline.Count - 1;
                        Vector2 at = Flat(container.transform.TransformPoint(spline[knot].Position));
                        Vector2 move = Vector2.zero;
                        string reason = null;
                        foreach ((Vector2 end, Vector2 endMove) in endMoves)
                        {
                            if (Vector2.Distance(end, at) >= JunctionReach) continue;
                            move = endMove;
                            reason = "joined road end";
                        }
                        if (reason == null)
                        {
                            // An end on the body of a moved stretch.
                            foreach ((SplineContainer host, int hostIndex, Spline before, List<AlignedRun> runs,
                                float delta) in bodyMoves)
                            {
                                if (host == container && hostIndex == index) continue;
                                Spline flat = WorldFlat(host, before);
                                SplineUtility.GetNearestPoint(flat, new float3(at.x, 0f, at.y), out float3 nearest,
                                    out float t);
                                if (Vector2.Distance(Flat(nearest), at) >= JunctionReach) continue;
                                move = RunOffset(runs, t * flat.GetLength(), delta);
                                reason = "end on a moved road";
                            }
                        }
                        if (reason == null || move.sqrMagnitude < 1e-4f) continue;
                        Undo.RecordObject(container, "Widen roads");
                        BezierKnot value = spline[knot];
                        value.Position += (float3)container.transform.InverseTransformVector(new Vector3(move.x, 0f,
                            move.y));
                        spline[knot] = value;
                        EditorUtility.SetDirty(container);
                        moved.Add((container, index, start));
                        report.AppendLine(container.name + " " + index + (start ? " start" : " end") + ": moved " +
                            move.magnitude.ToString("F2") + " m with a " + reason + ".");
                    }
                }
            }
            // A moved end that stood on the body of a road that did not move goes back onto that road.
            foreach ((SplineContainer container, int index, bool start) in moved.ToList())
            {
                Spline spline = container.Splines[index];
                int knot = start ? 0 : spline.Count - 1;
                Vector3 world = container.transform.TransformPoint(spline[knot].Position);
                foreach (SplineContainer host in containers)
                {
                    for (int hostIndex = 0; hostIndex < host.Splines.Count; hostIndex++)
                    {
                        if (host == container && hostIndex == index) continue;
                        Spline flat = WorldFlat(host, host.Splines[hostIndex]);
                        if (flat.Count < 2) continue;
                        SplineUtility.GetNearestPoint(flat, new float3(world.x, 0f, world.z), out float3 nearest,
                            out float t);
                        float length = flat.GetLength();
                        if (Vector2.Distance(Flat(nearest), Flat(world)) >= JunctionReach ||
                            t * length < JunctionReach ||
                            (1f - t) * length < JunctionReach) continue;
                        BezierKnot value = spline[knot];
                        value.Position = container.transform.InverseTransformPoint(new Vector3(nearest.x, world.y,
                            nearest.z));
                        spline[knot] = value;
                        report.AppendLine(container.name + " " + index + (start ? " start" : " end") + ": back on " +
                            host.name + " " +
                            hostIndex + ".");
                    }
                }
            }
            // Bridge ends at moved road ends. Structure decks stay.
            foreach ((Transform marker, Transform _, bool fixedDeck) in BridgeMarkers())
            {
                if (fixedDeck) continue;
                foreach ((Vector2 end, Vector2 move) in endMoves)
                {
                    if (Vector2.Distance(end, Flat(marker.position)) >= BridgeEndReach) continue;
                    Undo.RecordObject(marker, "Widen roads");
                    marker.position += new Vector3(move.x, 0f, move.y);
                    report.AppendLine(marker.parent.name + " " + marker.name + ": moved " +
                        move.magnitude.ToString("F2") + " m.");
                    break;
                }
            }
            return report.ToString();
        }
        #endregion

        #region Simplify
        /// <summary>
        /// Fits every road again with as few knots as its shape needs. Each road becomes a chain of cubic Bezier curves
        /// with free, aligned handles that stays within SimplifyAcross meters of the old road sideways and
        /// SimplifyHeight meters in height (a least squares fit that splits a curve where it misses the road most).
        /// Knots stay at the road ends, where other roads end on the road and along the junction landings, and the
        /// height fit is tight there and near pads. Width points move with the road, the roads take the pad levels, the
        /// junctions get their shape and the pads link the road knots again.
        /// </summary>
        /// <param name="backupFolder">Folder for the JSON backups of the pads and road containers, or null for no
        /// backup.</param>
        /// <param name="across">Largest sideways change in meters.</param>
        /// <param name="height">Largest height change in meters.</param>
        /// <param name="include">Selects the roads to fit by container and spline index, or null for every
        /// road.</param>
        public static string SimplifyRoads(LoogaRoadNetwork network, string backupFolder, float across = SimplifyAcross,
            float height = SimplifyHeight,
            Func<SplineContainer, int, bool> include = null)
        {
            _network = network;
            (SplineContainer[] containers, List<(Transform pad, Vector2[] outline)> outlines) = RoadsAndPads();
            if (backupFolder != null) Backup(backupFolder, outlines.Select(entry => entry.pad), containers);
            int before = 0, after = 0;
            var lines = new StringBuilder();
            foreach (SplineContainer container in containers)
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    int count = container.Splines[index].Count;
                    before += count;
                    if (count > 2 && (include == null || include(container, index)))
                    {
                        int fitted = FitRoad(container, index, LockedPoints(container, index, containers, outlines),
                            outlines,
                            across, height);
                        lines.AppendLine(container.name + " " + index + ": " + count + " -> " + fitted + " knots.");
                    }
                    after += container.Splines[index].Count;
                }
            }
            // A fit can leave a pad contact or a junction end by a little. The same passes as Apply, in the same order,
            // put the roads back at the pad levels and the landings back in shape.
            lines.Append(Settle(containers, outlines));
            lines.Append(CheckPadContacts(containers.Where(HasHeightStamp).ToList(), outlines));
            foreach ((Transform pad, Vector2[] _) in outlines)
            {
                LoogaRoadPads.LinkRoads(pad);
            }
            RequestFullUpdate();
            EditorSceneManager.MarkSceneDirty(_network.gameObject.scene);
            CheckCrossSlopeAfterUpdate(_network);
            return "Road knots: " + before + " -> " + after + ".\n" + lines;
        }

        /// <summary>
        /// Checks every road with a height stamp against the pad limits and every junction end against its landing.
        /// Changes nothing.
        /// </summary>
        public static string CheckNetwork(LoogaRoadNetwork network)
        {
            _network = network;
            (SplineContainer[] containers, List<(Transform pad, Vector2[] outline)> outlines) = RoadsAndPads();
            List<SplineContainer> levelled = containers.Where(HasHeightStamp).ToList();
            return CheckCurves(containers) + CheckPadContacts(levelled, outlines) + CheckJunctions(levelled, outlines,
                containers);
        }

        // Lists the road curves that turn back on themselves: somewhere along the curve the road runs against the
        // direction from its start knot to its end knot. Long handles on a short curve make such a loop.
        private static string CheckCurves(IEnumerable<SplineContainer> containers)
        {
            var problems = new StringBuilder();
            int checkedCurves = 0;
            foreach (SplineContainer container in containers)
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    Spline flat = WorldFlat(container, container.Splines[index]);
                    for (int curve = 0; curve < flat.Count - 1; curve++)
                    {
                        checkedCurves++;
                        BezierCurve bezier = flat.GetCurve(curve);
                        Vector2 chord = Flat(bezier.P3 - bezier.P0);
                        for (int step = 0; step <= CurveCheckSteps; step++)
                        {
                            Vector2 direction = Flat(CurveUtility.EvaluateTangent(bezier,
                                step / (float)CurveCheckSteps));
                            if (Vector2.Dot(direction, chord) >= 0f) continue;
                            Vector2 at = Flat(bezier.P0);
                            problems.AppendLine("Curve check: " + container.name + " " + index + " curve " + curve +
                                " at (" +
                                at.x.ToString("F0") + ", " + at.y.ToString("F0") + ") turns back on itself.");
                            break;
                        }
                    }
                }
            }
            return problems.Length > 0 ? problems.ToString()
                : "Curve check: none of " + checkedCurves + " road curves turns back on itself.\n";
        }

        // The points where knots stay: where other roads and bridges end on this road, and along the junction landings
        // of this road.
        private static List<Vector2> LockedPoints(SplineContainer own, int ownIndex, SplineContainer[] containers,
            List<(Transform pad, Vector2[] outline)> outlines)
        {
            var spline = new Spline(own.Splines[ownIndex]);
            float length = spline.GetLength();
            int steps = Mathf.Max(2, Mathf.CeilToInt(length / JoinStep));
            var road = new List<Vector2>();
            for (int step = 0; step <= steps; step++)
                road.Add(Flat(own.transform.TransformPoint(spline.EvaluatePosition(step / (float)steps))));
            var locked = new List<Vector2>();
            foreach (SplineContainer container in containers)
            {
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    if (container == own && index == ownIndex) continue;
                    Spline other = container.Splines[index];
                    if (other.Count < 2) continue;
                    foreach (int end in new[] { 0, other.Count - 1 })
                    {
                        Vector2 point = Flat(container.transform.TransformPoint(other[end].Position));
                        if (road.Any(sample => Vector2.Distance(sample, point) < JunctionReach)) locked.Add(point);
                    }
                }
            }
            foreach (LoogaRoadBridge bridge in Bridges())
            {
                foreach (Transform endPoint in new[] { bridge.StartMarker, bridge.FinishMarker })
                {
                    Vector2 point = Flat(endPoint.position);
                    if (road.Any(sample => Vector2.Distance(sample, point) < JunctionReach)) locked.Add(point);
                }
            }
            foreach ((bool start, float _) in JunctionEnds(own, ownIndex, spline, outlines, containers))
            {
                foreach (float distance in new[] { LandingLength, LandingLength + TransitionLength })
                {
                    if (distance >= length) continue;
                    locked.Add(Flat(own.transform.TransformPoint(spline.EvaluatePosition((start ? distance :
                        length - distance) /
                        length))));
                }
            }
            return locked;
        }

        // Replaces the knots of the road with the fitted curves and returns the new knot count. The height fit is tight
        // near the locked points and where the road is within the embankment reach of a pad.
        private static int FitRoad(SplineContainer container, int index, List<Vector2> locked,
            List<(Transform pad, Vector2[] outline)> outlines, float across, float height)
        {
            Spline spline = container.Splines[index];
            var original = new Spline(spline);
            Matrix4x4 toWorld = container.transform.localToWorldMatrix;
            int count = Mathf.Max(3, Mathf.CeilToInt(original.CalculateLength(toWorld) / FitStep) + 1);
            var points = new Vector3[count];
            var tolerance = new Vector2[count];
            for (int sample = 0; sample < count; sample++)
            {
                points[sample] = toWorld.MultiplyPoint3x4(original.EvaluatePosition(sample / (float)(count - 1)));
                Vector2 flat = Flat(points[sample]);
                bool tight = locked.Any(point => Vector2.Distance(point, flat) < SimplifyLockReach) ||
                    outlines.Any(entry => SignedDistance(entry.outline, flat) - RoadHalfWidth < EmbankmentReach);
                tolerance[sample] = new Vector2(across, tight ? Mathf.Min(height, TightHeight) : height);
            }
            // Break points: the ends and the samples nearest the locked points on this road.
            var breaks = new SortedSet<int> { 0, count - 1 };
            foreach (Vector2 point in locked)
            {
                int nearest = 0;
                for (int sample = 1; sample < count; sample++)
                {
                    if (Vector2.Distance(Flat(points[sample]), point) < Vector2.Distance(Flat(points[nearest]),
                        point)) nearest = sample;
                }
                if (Vector2.Distance(Flat(points[nearest]), point) < JunctionReach) breaks.Add(nearest);
            }
            Vector3 Direction(int sample)
            {
                if (sample == 0) return toWorld.MultiplyVector(original.EvaluateTangent(0f)).normalized;
                if (sample == count - 1) return toWorld.MultiplyVector(original.EvaluateTangent(1f)).normalized;
                return (points[sample + 1] - points[sample - 1]).normalized;
            }
            var curves = new List<Vector3[]>();
            int[] cuts = breaks.ToArray();
            for (int part = 0; part + 1 < cuts.Length; part++)
            {
                FitCubic(points, tolerance, cuts[part], cuts[part + 1], Direction(cuts[part]), -Direction(cuts[part +
                    1]), curves);
            }

            Undo.RecordObject(container, "Fit road");
            spline.Clear();
            for (int curve = 0; curve <= curves.Count; curve++)
            {
                Vector3 position = curve < curves.Count ? curves[curve][0] : curves[curve - 1][3];
                Vector3 tangentOut = curve < curves.Count ? curves[curve][1] - curves[curve][0] : Vector3.zero;
                Vector3 tangentIn = curve > 0 ? curves[curve - 1][2] - curves[curve - 1][3] : Vector3.zero;
                if (curve == 0) tangentIn = -tangentOut;
                if (curve == curves.Count) tangentOut = -tangentIn;
                spline.Add(new BezierKnot(container.transform.InverseTransformPoint(position),
                    container.transform.InverseTransformVector(tangentIn),
                        container.transform.InverseTransformVector(tangentOut),
                    quaternion.identity), TangentMode.Continuous);
            }
            EditorUtility.SetDirty(container);
            MoveWidths(container, index, original, new Spline(spline));
            return spline.Count;
        }

        // Fits cubic Bezier curves to the points from first to last (Schneider, "An Algorithm for Automatically Fitting
        // Digitized Curves", Graphics Gems 1990). The end handles point along startDirection and endDirection
        // (endDirection points back into the curve). A curve that misses a point by more than its tolerance splits
        // there.
        private static void FitCubic(Vector3[] points, Vector2[] tolerance, int first, int last, Vector3 startDirection,
            Vector3 endDirection, List<Vector3[]> curves)
        {
            if (last - first < 2)
            {
                float third = Vector3.Distance(points[first], points[last]) / 3f;
                curves.Add(new[] { points[first], points[first] + startDirection * third, points[last] +
                    endDirection * third, points[last] });
                return;
            }
            float[] u = ChordParameters(points, first, last);
            Vector3[] curve = LeastSquaresCurve(points, first, last, u, startDirection, endDirection);
            (float error, int split) = FitError(points, tolerance, first, last, curve, u);
            if (error <= 1f)
            {
                curves.Add(curve);
                return;
            }
            if (error < FitIterationError)
            {
                for (int iteration = 0; iteration < FitIterations; iteration++)
                {
                    u = Reparameterize(points, first, last, u, curve);
                    curve = LeastSquaresCurve(points, first, last, u, startDirection, endDirection);
                    (error, split) = FitError(points, tolerance, first, last, curve, u);
                    if (error <= 1f)
                    {
                        curves.Add(curve);
                        return;
                    }
                }
            }
            split = Mathf.Clamp(split, first + 1, last - 1);
            Vector3 center = (points[split - 1] - points[split + 1]).normalized;
            FitCubic(points, tolerance, first, split, startDirection, center, curves);
            FitCubic(points, tolerance, split, last, -center, endDirection, curves);
        }

        private static float[] ChordParameters(Vector3[] points, int first, int last)
        {
            var u = new float[last - first + 1];
            for (int index = 1; index < u.Length; index++)
            {
                u[index] = u[index - 1] + Vector3.Distance(points[first + index], points[first + index - 1]);
            }
            for (int index = 1; index < u.Length; index++) u[index] /= Mathf.Max(u[u.Length - 1], 1e-5f);
            return u;
        }

        private static Vector3[] LeastSquaresCurve(Vector3[] points, int first, int last, float[] u,
            Vector3 startDirection,
            Vector3 endDirection)
        {
            Vector3 p0 = points[first], p3 = points[last];
            double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
            for (int index = 0; index < u.Length; index++)
            {
                float t = u[index], s = 1f - t;
                float b0 = s * s * s, b1 = 3f * t * s * s, b2 = 3f * t * t * s, b3 = t * t * t;
                Vector3 a0 = startDirection * b1, a1 = endDirection * b2;
                c00 += Vector3.Dot(a0, a0);
                c01 += Vector3.Dot(a0, a1);
                c11 += Vector3.Dot(a1, a1);
                Vector3 rest = points[first + index] - (p0 * (b0 + b1) + p3 * (b2 + b3));
                x0 += Vector3.Dot(a0, rest);
                x1 += Vector3.Dot(a1, rest);
            }
            double determinant = c00 * c11 - c01 * c01;
            float alphaStart = determinant == 0 ? 0f : (float)((x0 * c11 - x1 * c01) / determinant);
            float alphaEnd = determinant == 0 ? 0f : (float)((c00 * x1 - c01 * x0) / determinant);
            float span = Vector3.Distance(p0, p3);
            if (alphaStart < 1e-4f * span || alphaEnd < 1e-4f * span) alphaStart = alphaEnd = span / 3f;
            return new[] { p0, p0 + startDirection * alphaStart, p3 + endDirection * alphaEnd, p3 };
        }

        // The largest miss relative to the tolerance (1 = at the limit) and the point where it happens.
        private static (float error, int split) FitError(Vector3[] points, Vector2[] tolerance, int first, int last,
            Vector3[] curve, float[] u)
        {
            float worst = 0f;
            int split = (first + last) / 2;
            for (int index = 1; index < u.Length - 1; index++)
            {
                Vector3 miss = Bezier(curve, u[index]) - points[first + index];
                Vector2 limit = tolerance[first + index];
                float error = Mathf.Max(new Vector2(miss.x, miss.z).magnitude / limit.x, Mathf.Abs(miss.y) / limit.y);
                if (error <= worst) continue;
                worst = error;
                split = first + index;
            }
            return (worst, split);
        }

        // One Newton-Raphson step per point toward the curve parameter nearest the point.
        private static float[] Reparameterize(Vector3[] points, int first, int last, float[] u, Vector3[] curve)
        {
            var next = new float[u.Length];
            for (int index = 0; index < u.Length; index++)
            {
                float t = u[index];
                Vector3 q = Bezier(curve, t) - points[first + index];
                Vector3 d1 = 3f * ((1 - t) * (1 - t) * (curve[1] - curve[0]) + 2 * t * (1 - t) * (curve[2] - curve[1]) +
                    t * t * (curve[3] - curve[2]));
                Vector3 d2 = 6f * ((1 - t) * (curve[2] - 2 * curve[1] + curve[0]) + t * (curve[3] - 2 * curve[2] +
                    curve[1]));
                float denominator = Vector3.Dot(d1, d1) + Vector3.Dot(q, d2);
                next[index] = Mathf.Abs(denominator) < 1e-6f ? t : Mathf.Clamp01(t - Vector3.Dot(q, d1) / denominator);
            }
            return next;
        }

        private static Vector3 Bezier(Vector3[] curve, float t)
        {
            float s = 1f - t;
            return s * s * s * curve[0] + 3f * s * s * t * curve[1] + 3f * s * t * t * curve[2] + t * t * t * curve[3];
        }
        #endregion

        #region Report
        // Each bridge end with the distance to the nearest road end before the edits.
        private static List<(string bridge, string marker, Vector3 end, float before)> BridgeEnds(
            IEnumerable<SplineContainer> containers)
        {
            var result = new List<(string, string, Vector3, float)>();
            foreach (LoogaRoadBridge bridge in Bridges())
            {
                foreach (Transform end in new[] { bridge.StartMarker, bridge.FinishMarker })
                {
                    result.Add((bridge.name, end.name, end.position, NearestRoadEnd(containers, end.position)));
                }
            }
            return result;
        }

        private static string BridgeReport(List<(string bridge, string marker, Vector3 end, float before)> ends,
            IEnumerable<SplineContainer> containers)
        {
            var report = new StringBuilder();
            foreach ((string bridge, string marker, Vector3 end, float before) in ends)
            {
                float after = NearestRoadEnd(containers, end);
                if (after > before + 1f)
                {
                    report.AppendLine("Bridge end " + bridge + " " + marker + ": nearest road end " +
                        before.ToString("F1") + " m before, " +
                        after.ToString("F1") + " m after the edits.");
                }
            }
            return report.Length > 0 ? report.ToString() : "All bridge ends keep their road ends.\n";
        }

        private static float NearestRoadEnd(IEnumerable<SplineContainer> containers, Vector3 point)
        {
            float best = float.MaxValue;
            foreach (SplineContainer container in containers)
            {
                foreach (Spline spline in container.Splines)
                {
                    if (spline.Count < 2) continue;
                    foreach (int end in new[] { 0, spline.Count - 1 })
                    {
                        best = Mathf.Min(best,
                            Vector2.Distance(Flat(container.transform.TransformPoint(spline[end].Position)),
                            Flat(point)));
                    }
                }
            }
            return best;
        }
        #endregion

        #region Helpers
        private static void Backup(string folder, IEnumerable<Transform> pads, IEnumerable<SplineContainer> containers)
        {
            Directory.CreateDirectory(folder);
            foreach (Transform pad in pads)
            {
                string name = PadId(pad.name);
                File.WriteAllText(Path.Combine(folder, "pad-" + name + "-transform.json"),
                    JsonUtility.ToJson(new PadEdit
                {
                    x = pad.position.x, z = pad.position.z, rotY = pad.eulerAngles.y,
                    outline = new[] { pad.position.y }
                }));
                File.WriteAllText(Path.Combine(folder, "pad-" + name + "-spline.json"),
                    EditorJsonUtility.ToJson(pad.GetComponent<SplineContainer>(), true));
                var poi = pad.GetComponent<LoogaRoadPad>();
                if (poi) File.WriteAllText(Path.Combine(folder, "pad-" + name + "-links.json"),
                    EditorJsonUtility.ToJson(poi, true));
            }
            foreach (SplineContainer container in containers)
            {
                File.WriteAllText(Path.Combine(folder, container.name + " - SplineContainer.json"),
                    EditorJsonUtility.ToJson(container, true));
                var path = container.GetComponent<LoogaSplinePath>();
                if (path) File.WriteAllText(Path.Combine(folder, container.name + " - LoogaSplinePath.json"),
                    EditorJsonUtility.ToJson(path, true));
            }
        }

        // The road spline containers of the network and the pads with their outlines.
        private static (SplineContainer[] containers, List<(Transform pad, Vector2[] outline)> outlines) RoadsAndPads()
        {
            SplineContainer[] containers = _network.GetRoads().Select(road => road.Container).ToArray();
            List<(Transform pad, Vector2[] outline)> outlines = PadTransforms().Select(pad => (pad,
                Outline(pad))).ToList();
            return (containers, outlines);
        }

        // The pads of the network: the children of the pad root with an outline spline.
        private static List<Transform> PadTransforms()
        {
            Transform padRoot = _network.PadRoot
                ? _network.PadRoot
                : throw new MissingReferenceException("Pad root of the road network " + _network.name + ".");
            return padRoot.Cast<Transform>().Where(pad => pad.GetComponent<SplineContainer>()).ToList();
        }

        // The planner road containers: M, the first road of the primary class that does not follow the ground, and F,
        // the first road of the primary class that follows it.
        private static Dictionary<char, SplineContainer> PlannerContainers()
        {
            LoogaRoadClass primary = _network.PrimaryClass;
            List<LoogaRoad> roads = _network.GetRoads().Where(road => road.RoadClass == primary).ToList();
            LoogaRoad planned = roads.FirstOrDefault(road => !road.FollowGround) ??
                throw new MissingReferenceException("A road of the primary class that does not follow the ground.");
            LoogaRoad ground = roads.FirstOrDefault(road => road.FollowGround) ??
                throw new MissingReferenceException("A road of the primary class that follows the ground.");
            return new Dictionary<char, SplineContainer> { { 'M', planned.Container }, { 'F', ground.Container } };
        }

        // Requests a full update of the stamp world after a layout change.
        private static void RequestFullUpdate()
        {
            LoogaStampWorld world = _network.World;
            if (world) world.RequestUpdate(null);
        }

        // True when no update runs or waits and the CPU heights match the generated heights.
        private static bool IsIdle(LoogaStampWorld world)
        {
            return world && !world.IsUpdating && !world.HasRequestedUpdate && world.HeightsSynchronized;
        }

        private static Dictionary<string, T> ReadEdits<T>(string folder)
        {
            var edits = new Dictionary<string, T>();
            if (!Directory.Exists(folder)) return edits;
            foreach (string file in Directory.GetFiles(folder, "*.json"))
            {
                edits[Path.GetFileNameWithoutExtension(file)] = JsonUtility.FromJson<T>(File.ReadAllText(file));
            }
            return edits;
        }

        // A pad name without the pad prefix of the network.
        private static string BareName(string name)
        {
            string prefix = _network.PadPrefix;
            return !string.IsNullOrEmpty(prefix) && name.StartsWith(prefix) ? name.Substring(prefix.Length) : name;
        }

        // The page names a pad by its name without the prefix, lower case, with every other character as a dash.
        private static string PadId(string name)
        {
            string bare = BareName(name);
            return new string(bare.Select(character => char.IsLetterOrDigit(character) ?
                char.ToLowerInvariant(character) : '-')
                .ToArray());
        }

        private static List<Vector2> Pairs(float[] values)
        {
            var pairs = new List<Vector2>();
            for (int index = 0; index + 1 < values.Length; index += 2) pairs.Add(new Vector2(values[index],
                values[index + 1]));
            return pairs;
        }

        // Signed distance to a closed outline: negative inside.
        private static float SignedDistance(Vector2[] outline, Vector2 point)
        {
            float best = float.MaxValue;
            bool inside = false;
            for (int index = 0, previous = outline.Length - 1; index < outline.Length; previous = index++)
            {
                Vector2 a = outline[index], b = outline[previous];
                if ((a.y > point.y) != (b.y > point.y) && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                {
                    inside = !inside;
                }
                Vector2 ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                best = Mathf.Min(best, Vector2.Distance(point, a + ab * t));
            }
            return inside ? -best : best;
        }

        private static Vector2 Flat(Vector3 point) => new Vector2(point.x, point.z);

        private static T RequireComponent<T>(Transform target, string name) where T : Component
        {
            if (!target) throw new MissingReferenceException(name);
            // A missing component is a fake null, so the null-coalescing operator cannot test it.
            T component = target.GetComponent<T>();
            if (!component) throw new MissingComponentException(name + " has no " + typeof(T).Name + ".");
            return component;
        }
        #endregion
    }
}
