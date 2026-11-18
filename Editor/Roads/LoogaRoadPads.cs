using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LoogaSoft.Terrain.Stamps;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Roads.Editor
{
    /// <summary>
    /// Pad tools of a road network: road knot links and auto level. A pad is a stamp group with a closed spline and a
    /// <see cref="LoogaRoadPad"/> under the pad root of the network. Move or turn the pad object to move the pad. The
    /// linked road knots follow.
    /// </summary>
    public static class LoogaRoadPads
    {
        private const string GroundSnapshotName = "Ground before pads";
        // Distance between the ground samples of auto level, in meters.
        private const float SampleSpacing = 5f;
        // Largest distance between the outline points of a rounded corner, in meters.
        private const float OutlineChord = 1.5f;
        // Bezier handle length of a quarter circle, as a part of its radius.
        private const float CircleHandle = 0.5523f;

        /// <summary>The road network whose pad root holds the pad, or null.</summary>
        public static LoogaRoadNetwork FindNetwork(Transform pad)
        {
            foreach (LoogaRoadNetwork network in Object.FindObjectsByType<LoogaRoadNetwork>(FindObjectsSortMode.None))
            {
                if (network.PadRoot && pad.IsChildOf(network.PadRoot)) return network;
            }
            return null;
        }

        /// <summary>
        /// Links every run of road knots inside the pad outline plus the pad link margin of the network to the pad.
        /// The linked knots then move with the pad. The call replaces the earlier links of the pad.
        /// </summary>
        public static string LinkRoads(Transform pad)
        {
            return LinkRoads(RequirePad(pad));
        }

        /// <inheritdoc cref="LinkRoads(Transform)"/>
        public static string LinkRoads(LoogaRoadPad pad)
        {
            LoogaRoadNetwork network = RequireNetwork(pad.transform);
            float margin = network.PadLinkMargin;
            SplineContainer[] roads = network.GetRoads().Select(road => road.Container).ToArray();
            Undo.RecordObject(pad, "Link road pad roads");
            pad.LinkNearbyRoadKnots(roads, OutlineSize(pad.GetComponent<SplineContainer>()), margin);
            EditorUtility.SetDirty(pad);
            EditorSceneManager.MarkSceneDirty(pad.gameObject.scene);
            return pad.RoadLinks.Count == 0
                ? "no road knots within " + margin.ToString("F0") + " m."
                : string.Join("; ", pad.RoadLinks.Select(link => link.ToString())) + ".";
        }

        /// <summary>
        /// Sets the pad to the median ground height under its outline. A pad with a level group
        /// (<see cref="LoogaRoadPad.LevelGroup"/>) and every other pad of that group take the median ground height under
        /// all of their outlines. The ground is the stamp world evaluated up to the pad ground snapshot of the network,
        /// so no pad changes it and no update is necessary. Linked road knots move with the pads.
        /// </summary>
        /// <returns>The result message.</returns>
        public static string AutoLevel(LoogaRoadPad pad)
        {
            return AutoLevel(pad.transform);
        }

        /// <inheritdoc cref="AutoLevel(LoogaRoadPad)"/>
        public static string AutoLevel(Transform pad)
        {
            if (!pad.GetComponentInChildren<LoogaHeightStamp>(true)) return pad.name + " does not flatten the ground.";
            LoogaStampWorld world = pad.GetComponentInParent<LoogaStampWorld>();
            if (!world) return pad.name + " is not under a stamp world.";

            LoogaHeightSnapshot snapshot = RequireGroundSnapshot(RequireNetwork(pad));
            string group = pad.TryGetComponent(out LoogaRoadPad roadPad) ? roadPad.LevelGroup : "";
            List<Transform> pads = string.IsNullOrEmpty(group)
                ? new List<Transform> { pad }
                : pad.parent.Cast<Transform>().Where(member =>
                    member.TryGetComponent(out LoogaRoadPad other) && other.LevelGroup == group).ToList();
            var heights = new List<float>();
            foreach (Transform member in pads)
            {
                Vector2 size = OutlineSize(member.GetComponent<SplineContainer>());
                var center = new Vector2(member.position.x, member.position.z);
                float radius = 0.5f * size.magnitude + SampleSpacing;
                using StampHeights ground = world.ReadSnapshot(snapshot,
                    new Rect(center.x - radius, center.y - radius, 2f * radius, 2f * radius));
                heights.AddRange(SampleHeights(center, size, member.eulerAngles.y, (x, z) =>
                    ground.TrySampleHeight(new Vector2(x, z), out float height)
                        ? height
                        : throw new InvalidOperationException("No terrain at " + x + ", " + z + ".")));
            }
            heights.Sort();
            float level = heights[heights.Count / 2];
            float relief = heights[heights.Count - 1] - heights[0];
            var report = new StringBuilder("Auto level" + (pads.Count > 1 ? " of level group " + group : "") + ": ");
            foreach (Transform member in pads)
            {
                Vector3 position = member.position;
                Undo.RecordObject(member, "Auto level road pad");
                member.position = new Vector3(position.x, level, position.z);
                report.Append(member.name + " " + position.y.ToString("F2") + " m, ");
            }
            EditorSceneManager.MarkSceneDirty(pad.gameObject.scene);
            return report + "to " + level.ToString("F2") + " m, ground relief " + relief.ToString("F1") + " m.";
        }

        /// <summary>
        /// The pad outline in world XZ, from the curves of its closed spline: a straight curve gives its start point,
        /// a bent curve (a rounded corner) gives points at most OutlineChord apart. The slabs and the road tools use
        /// it, so they follow the same rounded corners as the pad stamps.
        /// </summary>
        public static Vector2[] SampleOutline(SplineContainer container)
        {
            Spline spline = container.Spline;
            var points = new List<Vector2>();
            for (int index = 0; index < spline.Count; index++)
            {
                BezierCurve curve = spline.GetCurve(index);
                int segments = IsStraight(curve)
                    ? 1
                    : Mathf.Clamp(Mathf.CeilToInt(CurveUtility.ApproximateLength(curve) / OutlineChord), 4, 64);
                for (int segment = 0; segment < segments; segment++)
                {
                    Vector3 point = container.transform.TransformPoint(
                        CurveUtility.EvaluatePosition(curve, segment / (float)segments));
                    points.Add(new Vector2(point.x, point.z));
                }
            }
            return points.ToArray();
        }

        /// <summary>
        /// Rounds the cut corners of a pad: each short side between two long sides becomes a quarter circle (a Bezier
        /// curve) between the same two knots, so the long sides stay where they are. A short side is shorter than half
        /// of each side next to it. The knot positions do not change.
        /// </summary>
        /// <returns>The result message.</returns>
        public static string RoundCorners(Transform pad)
        {
            var container = pad.GetComponent<SplineContainer>();
            if (!container || !container.Spline.Closed || container.Spline.Count < 4)
            {
                return pad.name + ": no closed pad spline with four or more knots.";
            }
            Spline spline = container.Spline;
            int count = spline.Count;
            var positions = new float3[count];
            for (int index = 0; index < count; index++) positions[index] = spline[index].Position;
            float SideLength(int side) => math.distance(positions[side].xz, positions[(side + 1) % count].xz);
            var corner = new bool[count];
            int corners = 0;
            for (int side = 0; side < count; side++)
            {
                float shortest = math.min(SideLength((side + count - 1) % count), SideLength((side + 1) % count));
                corner[side] = SideLength(side) < 0.5f * shortest;
                if (corner[side]) corners++;
            }
            if (corners == 0) return pad.name + ": no cut corners.";

            Undo.RecordObject(container, "Round pad corners");
            for (int knot = 0; knot < count; knot++)
            {
                int previous = (knot + count - 1) % count;
                int next = (knot + 1) % count;
                float3 tangentIn = float3.zero;
                float3 tangentOut = float3.zero;
                // Both handles of a corner side point to the meeting point of the long sides (P1 = knot + tangent out,
                // P2 = knot + tangent in).
                if (corner[knot])
                {
                    tangentOut = CornerTangent(positions[previous], positions[knot], positions[next],
                        positions[(knot + 2) % count]);
                }
                if (corner[previous])
                {
                    tangentIn = CornerTangent(positions[next], positions[knot], positions[previous],
                        positions[(previous + count - 1) % count]);
                }
                spline.SetTangentMode(knot, TangentMode.Broken);
                spline.SetKnot(knot, new BezierKnot(positions[knot], tangentIn, tangentOut, quaternion.identity));
            }
            EditorUtility.SetDirty(container);
            EditorSceneManager.MarkSceneDirty(pad.gameObject.scene);
            return pad.name + ": " + corners + " corners rounded.";
        }

        // The handle at one end (from) of a corner side whose other end is cornerEnd: along the long side that arrives
        // at from (before to from), toward the point where that side meets the long side that leaves cornerEnd
        // (cornerEnd to after), and CircleHandle of the distance to that point long.
        private static float3 CornerTangent(float3 before, float3 from, float3 cornerEnd, float3 after)
        {
            float2 direction = math.normalize(from.xz - before.xz);
            float2 exit = math.normalize(after.xz - cornerEnd.xz);
            // The meeting point of the two lines: from + direction * leg = cornerEnd - exit * other.
            float cross = direction.x * exit.y - direction.y * exit.x;
            float2 gap = cornerEnd.xz - from.xz;
            float leg = math.abs(cross) > 1e-4f
                ? (gap.x * exit.y - gap.y * exit.x) / cross
                : 0.5f * math.length(gap);
            leg = math.max(leg, 0f) * CircleHandle;
            return new float3(direction.x * leg, 0f, direction.y * leg);
        }

        private static bool IsStraight(BezierCurve curve)
        {
            float2 a = curve.P0.xz, b = curve.P3.xz;
            float2 along = b - a;
            float length = math.length(along);
            if (length < 1e-4f) return true;
            float2 normal = new float2(-along.y, along.x) / length;
            return math.abs(math.dot(curve.P1.xz - a, normal)) < 0.001f &&
                math.abs(math.dot(curve.P2.xz - a, normal)) < 0.001f;
        }

        /// <summary>Width (local X) and depth (local Z) of the knot bounds of a pad outline.</summary>
        public static Vector2 OutlineSize(SplineContainer container)
        {
            float3[] knots = container.Spline.Knots.Select(knot => knot.Position).ToArray();
            return new Vector2(knots.Max(knot => knot.x) - knots.Min(knot => knot.x),
                knots.Max(knot => knot.z) - knots.Min(knot => knot.z));
        }

        // A pad without a road pad component gets one, so its road links can follow it.
        private static LoogaRoadPad RequirePad(Transform pad)
        {
            var roadPad = pad.GetComponent<LoogaRoadPad>();
            return roadPad ? roadPad : Undo.AddComponent<LoogaRoadPad>(pad.gameObject);
        }

        private static LoogaRoadNetwork RequireNetwork(Transform pad)
        {
            LoogaRoadNetwork network = FindNetwork(pad);
            return network
                ? network
                : throw new MissingReferenceException(pad.name + " is not under the pad root of a road network.");
        }

        // The snapshot is the first child of the pad root, so every pad comes after it in the stamp order.
        private static LoogaHeightSnapshot RequireGroundSnapshot(LoogaRoadNetwork network)
        {
            Transform group = network.PadRoot;
            LoogaHeightSnapshot snapshot = network.PadGround;
            if (snapshot)
            {
                Transform marker = snapshot.transform;
                if (marker.parent == group && marker.GetSiblingIndex() != 0)
                {
                    Undo.SetSiblingIndex(marker, 0, "Move pad ground snapshot");
                }
                return snapshot;
            }
            var snapshotObject = new GameObject(GroundSnapshotName);
            Undo.RegisterCreatedObjectUndo(snapshotObject, "Add pad ground snapshot");
            Undo.SetTransformParent(snapshotObject.transform, group, "Add pad ground snapshot");
            snapshotObject.transform.SetSiblingIndex(0);
            snapshot = Undo.AddComponent<LoogaHeightSnapshot>(snapshotObject);
            Undo.RecordObject(network, "Add pad ground snapshot");
            network.SetPadGround(snapshot);
            EditorUtility.SetDirty(network);
            return snapshot;
        }

        // The ground heights under a rotated outline box, SampleSpacing apart.
        private static List<float> SampleHeights(Vector2 center, Vector2 size, float yaw,
            Func<float, float, float> ground)
        {
            var heights = new List<float>();
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            for (float x = -size.x * 0.5f; x <= size.x * 0.5f; x += SampleSpacing)
            {
                for (float z = -size.y * 0.5f; z <= size.y * 0.5f; z += SampleSpacing)
                {
                    Vector3 point = rotation * new Vector3(x, 0f, z);
                    heights.Add(ground(center.x + point.x, center.y + point.z));
                }
            }
            return heights;
        }
    }
}
