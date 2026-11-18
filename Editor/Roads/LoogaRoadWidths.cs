using System.Collections.Generic;
using System.Linq;
using System.Text;
using LoogaSoft.Terrain.Stamps;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Roads.Editor
{
    /// <summary>
    /// Sets the widths of the road stamps from their road class: the height path, the surface texture, the path clears
    /// and the road clear.
    /// </summary>
    public static class LoogaRoadWidths
    {
        // Edge width of the tree, detail and object clears along a road, in meters.
        private const float ClearSmoothness = 3f;
        // The clear of a road is a child named after the road with this suffix.
        private const string RoadClearSuffix = " - clear";

        /// <summary>
        /// Sets the widths of the road stamps of the network from their road class. The width points (pad flares and
        /// junction corners) stay. Run it after a change of a road class width; then build the bridges again, so they
        /// get the new width.
        /// </summary>
        public static string Apply(LoogaRoadNetwork network)
        {
            var report = new StringBuilder();
            foreach (LoogaRoad road in network.GetRoads())
            {
                LoogaRoadClass roadClass = road.RoadClass;
                LoogaSplinePath path = road.Path;
                if (roadClass == null || !path) continue;
                float before = path.Width;
                SetWidths(road, roadClass);
                report.AppendLine(road.name + ": half width " + before.ToString("F3") + " -> " +
                    roadClass.HalfWidth.ToString("F3") + " m.");
            }
            return report.ToString();
        }

        // The width settings of a road and its clear, from the road class. Every road has a height stamp, so it is
        // level across its width. The profile of a road that follows the ground has no grade limit, so that road
        // still follows the ground along its length. The surface fades out past the clear, so the grass returns over
        // it and no bare ground shows between them.
        private static void SetWidths(LoogaRoad road, LoogaRoadClass roadClass)
        {
            SplineContainer container = road.Container;
            LoogaSplinePath path = road.Path;
            var surface = road.GetComponent<LoogaPathTexture>();
            var pathClear = road.GetComponent<LoogaPathClear>();
            Transform clearObject = road.transform.Find(road.name + RoadClearSuffix);
            var area = clearObject ? clearObject.GetComponent<LoogaSplineArea>() : null;
            var clear = clearObject ? clearObject.GetComponent<LoogaClearStamp>() : null;
            Undo.RecordObjects(new Object[] { path, surface, pathClear }
                .Concat(area ? new Object[] { area, clear } : new Object[0]).ToArray(), "Set road widths");

            path.SetHeight(HeightCombineMode.Override, roadClass.HalfWidth, roadClass.Embankment, 0f, null,
                FalloffEasing.Smoothstep);
            surface.SetTexture(surface.Layer, roadClass.HalfWidth + 0.5f, 4f, 1f);
            pathClear.SetClear(new LoogaPathClear.ClearShape(true, roadClass.HalfWidth + 2f, ClearSmoothness),
                new LoogaPathClear.ClearShape(true, roadClass.HalfWidth + 1f, ClearSmoothness),
                new LoogaPathClear.ClearShape(!roadClass.Paved, roadClass.HalfWidth + 1f, ClearSmoothness));
            SetDistanceField(path, surface, pathClear, path.Widths.ToList());
            EditorUtility.SetDirty(path);
            EditorUtility.SetDirty(surface);
            EditorUtility.SetDirty(pathClear);
            if (!area) return;
            area.Set(container, 2048, roadClass.HalfWidth + roadClass.FlareWidth + 8f, true);
            // The range value is the default. The spline area shape does not use it.
            clear.Falloff.SetShape(FalloffShape.SplineArea, new Vector2(0.8f, 1f), FalloffEasing.Linear);
            clear.Falloff.SetSplineArea(area, 3f, roadClass.HalfWidth + 2f);
            EditorUtility.SetDirty(area);
            EditorUtility.SetDirty(clear);
        }

        // The distance search reaches the widest effect of the path plus the widest flare plus 3 m, and the bounds
        // pad is the largest width or smoothness. The object clear does not widen the search.
        private static void SetDistanceField(LoogaSplinePath path, LoogaPathTexture surface, LoogaPathClear clear,
            List<LoogaSplinePath.SplineWidths> widths)
        {
            float reach = Mathf.Max(surface.Width + surface.Smoothness,
                Mathf.Max(clear.Trees.Width + clear.Trees.Smoothness, clear.Details.Width + clear.Details.Smoothness));
            if (path.enabled) reach = Mathf.Max(reach, path.Width + path.FillShoulder + path.Smoothness);
            float widest = widths.SelectMany(width => width.Points).Select(point => point.y).DefaultIfEmpty(0f).Max();
            float padding = Mathf.Max(Mathf.Max(path.Width, surface.Width), Mathf.Max(path.Smoothness,
                surface.Smoothness));
            path.SetDistanceField(2048, 512, reach + widest + 3f, padding);
        }
    }
}
