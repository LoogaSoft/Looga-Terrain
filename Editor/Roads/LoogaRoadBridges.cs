using System.Collections.Generic;
using System.Linq;
using System.Text;
using LoogaSoft.Terrain.Geology;
using LoogaSoft.Terrain.Stamps;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Roads.Editor
{
    /// <summary>
    /// Builds the parts of the bridges of a <see cref="LoogaRoadNetwork"/>, keeps the ground under the decks and cuts
    /// openings for the decks into the rock faces. <see cref="LoogaRoadLive"/> keeps the bridges up to date with the
    /// roads and the ground.
    /// </summary>
    public static class LoogaRoadBridges
    {
        private const string PartsName = "Parts";
        private const string ClearanceName = "Bridge clearance";
        // A road end or a bridge marker this close to a bridge marker joins the bridge, in meters.
        private const float JoinDistance = 3f;
        // The deck is this much wider than the road on each side, in meters.
        internal const float DeckMargin = 1f;
        private const float DeckThickness = 1.2f;
        private const float PierSpacing = 20f;
        // A pier stands where the ground is more than this below the deck bottom, in meters.
        private const float PierHeight = 3f;
        // The ground under a deck stays at least DeckGroundGap below the deck top and blends back over
        // ClearanceSmoothness beside the deck, in meters. The road slab top meets the deck top (LoogaRoadSlabs).
        private const float DeckGroundGap = 0.12f;
        private const float ClearanceSmoothness = 2f;
        // Rock face openings: the rock stays this far below the deck top under a deck and this far beside and past it.
        private const float OpeningClearance = 1.5f;
        private const float OpeningMargin = 0.5f;
        private const float OpeningEndReach = 8f;
        // Road openings of faces with ClearRoads: the rock stays this far below the road surface and this far beside the
        // road. Each road is split into straight openings of about this length, in meters. Beside the margin the rock
        // may rise steeply, so canyon walls next to a road keep their height (a 1 m per meter notch cut them in half).
        private const float RoadOpeningClearance = 0.3f;
        private const float RoadOpeningMargin = 1.5f;
        private const float RoadOpeningStep = 12f;
        private const float RoadOpeningSideSlope = 10f;

        private sealed class BridgePlan
        {
            public Transform Bridge;
            public List<BridgePart> Parts;
            public string Report;
            public Vector3 Start;
            public Vector3 Finish;
            public float HalfWidth;
            // The deck of another structure: no parts, clearance or openings.
            public bool Structure;
        }

        private struct BridgePart
        {
            public string Name;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public Material Material;
        }

        #region Public
        /// <summary>
        /// Builds the parts of each bridge between its two end markers again. Move the end markers of a bridge to its
        /// road ends, then run this.
        /// </summary>
        public static string Build(LoogaRoadNetwork network)
        {
            List<BridgePlan> plans = Plan(network, true, out Transform root);
            if (!root) return "No bridges.";
            var report = new StringBuilder();
            foreach (BridgePlan plan in plans)
            {
                Transform parts = plan.Bridge.Find(PartsName);
                if (parts)
                {
                    Undo.DestroyObjectImmediate(parts.gameObject);
                }
                if (plan.Structure)
                {
                    report.AppendLine(plan.Report);
                    continue;
                }
                parts = new GameObject(PartsName).transform;
                Undo.RegisterCreatedObjectUndo(parts.gameObject, "Build bridge parts");
                parts.SetParent(plan.Bridge, false);
                foreach (BridgePart part in plan.Parts)
                {
                    CreatePart(parts, part);
                }
                report.AppendLine(plan.Report);
            }
            report.AppendLine(UpdateClearance(network, plans, true));
            report.AppendLine("Rock faces with new bridge openings: " + UpdateOpenings(network, plans, true) + ".");
            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            return report.ToString();
        }

        /// <summary>
        /// Brings the parts of each bridge up to date with its markers, the road ends and the ground, without undo
        /// records. Parts move in place while the part list keeps its names. Otherwise the parts are built again. The
        /// parts are derived objects, so an undo of a marker move builds them again. Returns the number of changed
        /// bridges.
        /// </summary>
        internal static int UpdateLive(LoogaRoadNetwork network)
        {
            List<BridgePlan> plans = Plan(network, true, out Transform root);
            int changed = 0;
            foreach (BridgePlan plan in plans)
            {
                Transform parts = plan.Bridge.Find(PartsName);
                if (plan.Structure)
                {
                    if (!parts) continue;
                    Object.DestroyImmediate(parts.gameObject);
                    changed++;
                    continue;
                }
                if (parts && parts.Cast<Transform>().Select(part => part.name)
                        .SequenceEqual(plan.Parts.Select(part => part.Name)))
                {
                    bool moved = false;
                    for (int index = 0; index < plan.Parts.Count; index++)
                    {
                        moved |= ApplyPart(parts.GetChild(index), plan.Parts[index]);
                    }
                    if (moved)
                    {
                        changed++;
                    }
                    continue;
                }
                if (parts)
                {
                    Object.DestroyImmediate(parts.gameObject);
                }
                parts = new GameObject(PartsName).transform;
                parts.SetParent(plan.Bridge, false);
                foreach (BridgePart part in plan.Parts)
                {
                    CreatePart(parts, part);
                }
                changed++;
            }
            if (UpdateClearance(network, plans, false).Length > 0) changed++;
            // The rock faces build again later (one per editor update), so a moved bridge passes the rock. The openings
            // are derived like the parts: an undo record here came after the edit, so Ctrl+Z reverted the openings
            // first and cleared the redo of an undone edit.
            UpdateOpenings(network, plans, false);
            if (changed > 0)
            {
                EditorSceneManager.MarkSceneDirty(network.gameObject.scene);
            }
            return changed;
        }

        /// <summary>Compares the bridge parts in the scene with a full bridge build. Writes nothing.</summary>
        public static string Compare(LoogaRoadNetwork network)
        {
            List<BridgePlan> plans = Plan(network, true, out Transform root);
            if (!root) return "No bridges.";
            var differ = new List<string>();
            int parts = 0;
            foreach (BridgePlan plan in plans)
            {
                if (plan.Structure) continue;
                parts += plan.Parts.Count;
                Transform holder = plan.Bridge.Find(PartsName);
                if (!holder || holder.childCount != plan.Parts.Count)
                {
                    differ.Add(plan.Bridge.name + " (part count)");
                    continue;
                }
                for (int index = 0; index < plan.Parts.Count; index++)
                {
                    Transform part = holder.GetChild(index);
                    BridgePart expected = plan.Parts[index];
                    if (part.name == expected.Name && part.position == expected.Position &&
                        part.rotation == expected.Rotation && part.localScale == expected.Scale &&
                        part.GetComponent<Renderer>().sharedMaterial == expected.Material) continue;
                    differ.Add(plan.Bridge.name + "/" + part.name + " (position off " +
                        (part.position - expected.Position).magnitude.ToString("F4") + " m)");
                    break;
                }
            }
            return plans.Count + " bridges, " + parts + " parts; " + differ.Count + " differ" +
                (differ.Count > 0 ? ": " + string.Join("; ", differ) : "") + ".";
        }

        /// <summary>The deck of each bridge: its end marker positions (deck top) and its half width.</summary>
        public static List<(string name, Vector3 start, Vector3 finish, float halfWidth)> Decks(
            LoogaRoadNetwork network)
        {
            // The decks need only the ends and the width, so the parts are not planned.
            return Plan(network, false, out Transform _)
                .Select(plan => (plan.Bridge.name, plan.Start, plan.Finish, plan.HalfWidth)).ToList();
        }

        /// <summary>
        /// The bridge decks that pass the rock of a face, as rock face openings. A face with ClearRoads also gets an
        /// opening along each road near it.
        /// </summary>
        public static List<RockFaceOpening> Openings(LoogaRoadNetwork network, LoogaRockFace face)
        {
            return Openings(network, face, Plan(network, false, out Transform _));
        }
        #endregion

        #region Plans
        // The parts of each bridge that has both end markers. Without parts a plan has no parts and no report. The
        // deck ends and the width are the same.
        private static List<BridgePlan> Plan(LoogaRoadNetwork network, bool parts, out Transform root)
        {
            root = network.BridgeRoot;
            var plans = new List<BridgePlan>();
            if (!root) return plans;
            List<(LoogaRoadClass roadClass, List<Vector3> ends)> classEnds = RoadEnds(network);
            Material deck = network.DeckMaterial;
            Material railing = network.RailingMaterial;
            if (parts && (!deck || !railing))
            {
                throw new MissingReferenceException("Set the deck and railing materials of " + network.name + ".");
            }
            foreach (Transform child in root)
            {
                if (!child.TryGetComponent(out LoogaRoadBridge bridge) || !bridge.HasMarkers) continue;
                Vector3 start = bridge.StartMarker.position;
                Vector3 finish = bridge.FinishMarker.position;
                LoogaRoadClass roadClass = BridgeClass(network, child, start, finish, root, classEnds);
                if (roadClass == null) continue;
                float halfWidth = roadClass.HalfWidth + DeckMargin;
                if (bridge.StructureDeck)
                {
                    plans.Add(new BridgePlan
                    {
                        Bridge = child, Parts = new List<BridgePart>(), Start = start, Finish = finish,
                        HalfWidth = halfWidth, Structure = true,
                        Report = child.name + ": structure deck, " + Flat(finish - start).magnitude.ToString("F0") +
                            " m, no parts."
                    });
                    continue;
                }
                if (!parts)
                {
                    plans.Add(new BridgePlan { Bridge = child, Start = start, Finish = finish, HalfWidth = halfWidth });
                    continue;
                }
                List<BridgePart> bridgeParts = Parts(network, start, finish, halfWidth, deck, railing, out int piers);
                Vector3 span = finish - start;
                float grade = Mathf.Atan(Mathf.Abs(span.y) / Mathf.Max(Flat(span).magnitude, 0.01f)) * Mathf.Rad2Deg;
                plans.Add(new BridgePlan
                {
                    Bridge = child, Parts = bridgeParts, Start = start, Finish = finish, HalfWidth = halfWidth,
                    Report = child.name + ": " + Flat(span).magnitude.ToString("F0") + " m, " + roadClass.Name +
                        " width, deck grade " + grade.ToString("F1") + " degrees, " + piers + " piers."
                });
            }
            return plans;
        }

        // The first and the last knot of each road spline, for each road class from the widest to the narrowest.
        // Loops, not LINQ: three plans run for each live road update.
        private static List<(LoogaRoadClass roadClass, List<Vector3> ends)> RoadEnds(LoogaRoadNetwork network)
        {
            var classEnds = new List<(LoogaRoadClass roadClass, List<Vector3> ends)>();
            foreach (LoogaRoadClass roadClass in network.RoadClasses.OrderByDescending(item => item.HalfWidth))
            {
                classEnds.Add((roadClass, new List<Vector3>()));
            }
            foreach (LoogaRoad road in network.GetRoads())
            {
                LoogaRoadClass roadClass = road.RoadClass;
                int classIndex = classEnds.FindIndex(item => item.roadClass == roadClass);
                if (classIndex < 0) continue;
                List<Vector3> ends = classEnds[classIndex].ends;
                IReadOnlyList<Spline> splines = road.Container.Splines;
                for (int index = 0; index < splines.Count; index++)
                {
                    Spline spline = splines[index];
                    if (spline.Count == 0) continue;
                    ends.Add(road.transform.TransformPoint(spline[0].Position));
                    ends.Add(road.transform.TransformPoint(spline[spline.Count - 1].Position));
                }
            }
            return classEnds;
        }

        // The widest road class whose road ends at a marker of the bridge, or at a bridge that such a road reaches.
        // Without such a road, the narrowest class.
        private static LoogaRoadClass BridgeClass(LoogaRoadNetwork network, Transform bridge, Vector3 start,
            Vector3 finish, Transform bridgeRoot, List<(LoogaRoadClass roadClass, List<Vector3> ends)> classEnds)
        {
            foreach ((LoogaRoadClass roadClass, List<Vector3> ends) in classEnds)
            {
                if (LinksRoad(bridge, start, finish, bridgeRoot, ends)) return roadClass;
            }
            return classEnds.Count > 0 ? classEnds[classEnds.Count - 1].roadClass : null;
        }

        // True when a deck end is at a road end, or at a bridge whose end markers touch a road end.
        private static bool LinksRoad(Transform bridge, Vector3 start, Vector3 finish, Transform bridgeRoot,
            List<Vector3> roadEnds)
        {
            foreach (Vector3 end in roadEnds)
            {
                if ((end - start).magnitude < JoinDistance || (end - finish).magnitude < JoinDistance) return true;
            }
            foreach (Transform other in bridgeRoot)
            {
                if (other == bridge || !other.TryGetComponent(out LoogaRoadBridge otherBridge) ||
                    !otherBridge.HasMarkers) continue;
                Vector3 otherStart = otherBridge.StartMarker.position;
                Vector3 otherFinish = otherBridge.FinishMarker.position;
                bool linked = false;
                foreach (Vector3 end in roadEnds)
                {
                    if ((end - otherStart).magnitude < JoinDistance || (end - otherFinish).magnitude < JoinDistance)
                    {
                        linked = true;
                        break;
                    }
                }
                if (!linked) continue;
                foreach (Vector3 marker in new[] { otherStart, otherFinish })
                {
                    if ((marker - start).magnitude < JoinDistance || (marker - finish).magnitude < JoinDistance)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        #endregion

        #region Parts
        // A straight deck with railings, and piers where the ground is far below the deck.
        private static List<BridgePart> Parts(LoogaRoadNetwork network, Vector3 start, Vector3 finish, float halfWidth,
            Material deck, Material railing, out int piers)
        {
            var parts = new List<BridgePart>();
            void Part(string name, Vector3 position, Quaternion rotation, Vector3 scale, Material material)
            {
                parts.Add(new BridgePart
                {
                    Name = name, Position = position, Rotation = rotation, Scale = scale, Material = material
                });
            }
            Quaternion rotation = Quaternion.LookRotation(finish - start);
            float length = (finish - start).magnitude;
            // The markers give the road surface. The road slabs pave the deck (LoogaRoadSlabs), so the deck top lies
            // the slab thickness below them, and the deck ends at the markers, where the road slabs meet the paving.
            float paving = LoogaRoadSlabs.SlabThickness;
            Vector3 middle = (start + finish) * 0.5f + rotation * Vector3.down * paving;
            Part("Deck", middle + rotation * Vector3.down * DeckThickness * 0.5f, rotation,
                new Vector3(halfWidth * 2f, DeckThickness, length), deck);
            foreach (float side in new[] { -1f, 1f })
            {
                Part("Railing", middle + rotation * new Vector3(side * (halfWidth - 0.2f), 0.55f, 0f), rotation,
                    new Vector3(0.3f, 1.1f, length), railing);
            }
            piers = 0;
            int spans = Mathf.Max(1, Mathf.RoundToInt(length / PierSpacing));
            for (int index = 1; index < spans; index++)
            {
                Vector3 point = Vector3.Lerp(start, finish, (float)index / spans);
                float ground = network.Ground(point.x, point.z);
                float height = point.y - paving - DeckThickness - ground;
                if (height < PierHeight) continue;
                Part("Pier " + (++piers), new Vector3(point.x, ground - 1f + (height + 1f) * 0.5f, point.z),
                    Quaternion.Euler(0f, rotation.eulerAngles.y, 0f), new Vector3(halfWidth * 1.4f, height + 1f, 2f),
                    deck);
            }
            foreach (Vector3 end in new[] { start, finish })
            {
                Part("Abutment", end + Vector3.down * (paving + DeckThickness + 1.5f),
                    Quaternion.Euler(0f, rotation.eulerAngles.y, 0f), new Vector3(halfWidth * 2f + 1f, 3f, 3f), deck);
            }
            return parts;
        }

        private static void CreatePart(Transform parent, BridgePart spec)
        {
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = spec.Name;
            part.transform.SetParent(parent, false);
            ApplyPart(part.transform, spec);
            part.isStatic = true;
        }

        // Sets the transform and the material of a part. Returns true when a value changed.
        private static bool ApplyPart(Transform part, BridgePart spec)
        {
            bool changed = false;
            if (part.position != spec.Position || part.rotation != spec.Rotation)
            {
                part.SetPositionAndRotation(spec.Position, spec.Rotation);
                changed = true;
            }
            if (part.localScale != spec.Scale)
            {
                part.localScale = spec.Scale;
                changed = true;
            }
            var renderer = part.GetComponent<Renderer>();
            if (renderer.sharedMaterial != spec.Material)
            {
                renderer.sharedMaterial = spec.Material;
                changed = true;
            }
            return changed;
        }
        #endregion

        #region Ground and rock
        // Keeps the ground under every bridge deck just below the deck top: a Min spline path in the network with one
        // straight spline per bridge from end marker to end marker, DeckGroundGap below the deck top, as wide as the
        // deck. Its ends are square, so the road before a bridge keeps its height. It runs after the road paths, so it
        // also cuts the round road caps and the banks that would show through the deck. Returns a one line report, or
        // an empty string when nothing changed.
        private static string UpdateClearance(LoogaRoadNetwork network, List<BridgePlan> plans, bool undo)
        {
            LoogaSplinePath path = network.BridgeClearance;
            bool created = !path;
            if (created)
            {
                var holder = new GameObject(ClearanceName);
                if (undo) Undo.RegisterCreatedObjectUndo(holder, "Add bridge clearance");
                holder.transform.SetParent(network.transform, false);
                holder.AddComponent<SplineContainer>();
                path = holder.AddComponent<LoogaSplinePath>();
                if (undo) Undo.RecordObject(network, "Add bridge clearance");
                network.SetBridgeClearance(path);
                EditorUtility.SetDirty(network);
            }
            Transform clearance = path.transform;
            // After the road paths, just before the last stamp, which stays last.
            Transform last = network.LastStamp;
            if (last && last.parent == clearance.parent && clearance.GetSiblingIndex() != last.GetSiblingIndex() - 1)
            {
                clearance.SetAsLastSibling();
                last.SetAsLastSibling();
            }
            var container = path.GetComponent<SplineContainer>();
            // Plain points first: every knot added to any spline raises Spline.Changed, which marks the live roads as
            // changed (LoogaRoadLive), so splines are made only when the clearance really changes.
            var ends = new List<Vector3[]>();
            float halfWidth = 0f;
            foreach (BridgePlan plan in plans)
            {
                if (plan.Structure) continue;
                ends.Add(new[] { plan.Start, plan.Finish }
                    .Select(end => clearance.InverseTransformPoint(end + Vector3.down * DeckGroundGap)).ToArray());
                halfWidth = Mathf.Max(halfWidth, plan.HalfWidth);
            }
            bool same = !created && container.Splines.Count == ends.Count && container.Splines.Zip(ends,
                    (spline, points) => spline.Count == points.Length && spline.Zip(points,
                        (knot, point) => Vector3.Distance(knot.Position, point) < 0.001f).All(match => match))
                .All(match => match) && Mathf.Approximately(path.Width, halfWidth) && path.SquareEnds &&
                path.Mode == HeightCombineMode.Min;
            if (same) return "";
            var splines = new List<Spline>();
            foreach (Vector3[] points in ends)
            {
                var spline = new Spline();
                foreach (Vector3 point in points)
                {
                    spline.Add(new BezierKnot(point), TangentMode.Linear);
                }
                splines.Add(spline);
            }
            if (undo) Undo.RecordObjects(new Object[] { container, path }, "Update bridge clearance");
            container.Splines = splines;
            path.SetSpline(container, new List<LoogaSplinePath.SplineWidths>(), FalloffEasing.Smoothstep);
            path.SetHeight(HeightCombineMode.Min, halfWidth, ClearanceSmoothness, 0f, null, FalloffEasing.Smoothstep);
            path.SetSquareEnds(true);
            path.SetDistanceField(2048, 64, halfWidth + ClearanceSmoothness + 3f,
                Mathf.Max(halfWidth, ClearanceSmoothness));
            EditorUtility.SetDirty(container);
            EditorUtility.SetDirty(path);
            return "Bridge clearance: " + splines.Count + " decks, ground at least " + DeckGroundGap.ToString("F2") +
                " m below the deck tops.";
        }

        // Gives the rock faces under the rock face root of the network an opening for each deck that passes their
        // rock. Returns the number of changed faces. A changed face builds again.
        private static int UpdateOpenings(LoogaRoadNetwork network, List<BridgePlan> plans, bool undo)
        {
            Transform root = network.RockFaceRoot;
            if (!root) return 0;
            int changed = 0;
            foreach (LoogaRockFace face in root.GetComponentsInChildren<LoogaRockFace>())
            {
                List<RockFaceOpening> openings = Openings(network, face, plans);
                if (face.Openings.Count == openings.Count && openings.All(opening => face.Openings.Contains(opening)))
                {
                    continue;
                }
                if (undo) Undo.RecordObject(face, "Rock face bridge openings");
                face.SetOpenings(openings);
                EditorUtility.SetDirty(face);
                face.RequestBuild();
                changed++;
            }
            return changed;
        }

        // The bridge decks that pass the rock of a face: the deck line from end marker to end marker, lowered under
        // the deck and extended past both ends under the road.
        private static List<RockFaceOpening> Openings(LoogaRoadNetwork network, LoogaRockFace face,
            List<BridgePlan> plans)
        {
            var openings = new List<RockFaceOpening>();
            if (!face.Spline) return openings;
            Bounds bounds = SplineBounds(face.Spline);
            bounds.Expand(2f * (face.Settings.SearchOutward + face.Settings.SearchInward));
            var faceBox = new Bounds(new Vector3(bounds.center.x, 0f, bounds.center.z),
                new Vector3(bounds.size.x, 0f, bounds.size.z));
            foreach (BridgePlan plan in plans)
            {
                Vector3 along = plan.Finish - plan.Start;
                along.y = 0f;
                if (plan.Structure || along.sqrMagnitude < 1f) continue;
                along.Normalize();
                Vector3 a = plan.Start - along * OpeningEndReach + Vector3.down * OpeningClearance;
                Vector3 b = plan.Finish + along * OpeningEndReach + Vector3.down * OpeningClearance;
                var box = new Bounds(new Vector3(a.x, 0f, a.z), Vector3.zero);
                box.Encapsulate(new Vector3(b.x, 0f, b.z));
                box.Expand(new Vector3(2f * plan.HalfWidth, 0f, 2f * plan.HalfWidth));
                if (!box.Intersects(faceBox)) continue;
                openings.Add(new RockFaceOpening(a, b, plan.HalfWidth + OpeningMargin));
            }
            if (face.Settings.ClearRoads)
            {
                AddRoadOpenings(network, faceBox, openings);
            }
            return openings;
        }

        // Straight openings along each road that passes the face box, just below the terrain on the road center line.
        // The terrain there is the road surface, also on roads that follow the ground.
        private static void AddRoadOpenings(LoogaRoadNetwork network, Bounds faceBox, List<RockFaceOpening> openings)
        {
            LoogaStampWorld world = network.World;
            if (!world) return;
            foreach (LoogaRoad road in network.GetRoads())
            {
                LoogaRoadClass roadClass = road.RoadClass;
                SplineContainer container = road.Container;
                if (roadClass == null || !container) continue;
                float halfWidth = roadClass.HalfWidth + RoadOpeningMargin;
                for (int spline = 0; spline < container.Splines.Count; spline++)
                {
                    int steps = Mathf.Max(1, Mathf.CeilToInt(container.CalculateLength(spline) / RoadOpeningStep));
                    bool hasPrevious = false;
                    Vector3 previous = Vector3.zero;
                    for (int step = 0; step <= steps; step++)
                    {
                        Vector3 point = container.EvaluatePosition(spline, step / (float)steps);
                        if (!TrySurface(world, ref point))
                        {
                            hasPrevious = false;
                            continue;
                        }
                        if (hasPrevious)
                        {
                            var box = new Bounds(new Vector3(previous.x, 0f, previous.z), Vector3.zero);
                            box.Encapsulate(new Vector3(point.x, 0f, point.z));
                            box.Expand(new Vector3(2f * halfWidth, 0f, 2f * halfWidth));
                            if (box.Intersects(faceBox))
                            {
                                openings.Add(new RockFaceOpening(previous, point, halfWidth, RoadOpeningSideSlope));
                            }
                        }
                        previous = point;
                        hasPrevious = true;
                    }
                }
            }
        }

        // Sets the point height to the terrain height minus the road opening clearance. False outside the terrains.
        private static bool TrySurface(LoogaStampWorld world, ref Vector3 point)
        {
            IReadOnlyList<UnityEngine.Terrain> terrains = world.Terrains;
            for (int index = 0; index < terrains.Count; index++)
            {
                UnityEngine.Terrain terrain = terrains[index];
                if (!terrain || !terrain.isActiveAndEnabled) continue;
                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                if (point.x < origin.x || point.x > origin.x + size.x ||
                    point.z < origin.z || point.z > origin.z + size.z)
                {
                    continue;
                }
                point.y = terrain.SampleHeight(point) + origin.y - RoadOpeningClearance;
                return true;
            }
            return false;
        }

        private static Bounds SplineBounds(SplineContainer spline)
        {
            const int samples = 256;
            Bounds bounds = new(spline.EvaluatePosition(0f), Vector3.zero);
            for (int index = 1; index <= samples; index++)
            {
                bounds.Encapsulate((Vector3)spline.EvaluatePosition(index / (float)samples));
            }
            return bounds;
        }

        private static Vector3 Flat(Vector3 vector)
        {
            return new Vector3(vector.x, 0f, vector.z);
        }
        #endregion
    }
}
