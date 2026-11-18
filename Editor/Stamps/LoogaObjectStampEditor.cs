using System.Collections.Generic;
using LoogaSoft.Inspector.Editor;
using LoogaSoft.Terrain.Geology;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    /// <summary>
    /// Warns when the placement of an object stamp can show the inside of an open mesh. A prototype is a problem when
    /// its prefab has a main opening and the rotation and slope ranges can turn that opening out of the ground.
    /// </summary>
    [CustomEditor(typeof(LoogaObjectStamp))]
    [CanEditMultipleObjects]
    internal sealed class LoogaObjectStampEditor : LoogaEditor
    {
        private readonly List<string> _exposed = new();

        protected override void DrawBeforeProperties()
        {
            var stamp = (LoogaObjectStamp)target;
            if (stamp && stamp.FieldSource != PlacementFieldSource.None &&
                TerrainFieldMaps.UsesPlacements(stamp.FieldFilter, stamp.FieldFilter2))
            {
                EditorGUILayout.HelpBox("This stamp is a placement field source, so its Canopy and Object Foot Distance " +
                    "filters have no effect.", MessageType.Warning);
            }
            ObjectStampOpenings.FindExposedPrefabs(stamp, _exposed);
            if (_exposed.Count == 0) return;
            EditorGUILayout.HelpBox("The rotation of these prefabs can show the inside of an open mesh: " +
                string.Join(", ", _exposed) + ". Select the prefabs and use Assets > Looga > Close Open Rock Meshes, " +
                "or keep the copies upright.", MessageType.Warning);
        }
    }

    /// <summary>Checks the prototypes of an object stamp against the openings of their meshes.</summary>
    public static class ObjectStampOpenings
    {
        // Slope that slope alignment can follow when the stamp has no slope filter, in degrees.
        private const float UnfilteredSlope = 45f;

        /// <summary>Adds the names of the prefabs whose placement can show an opening of their meshes.</summary>
        public static void FindExposedPrefabs(LoogaObjectStamp stamp, List<string> names)
        {
            names.Clear();
            if (!stamp) return;
            StampRangeFilter slope = stamp.SlopeFilter;
            float maxSlope = slope.Enabled ? slope.Range.y + slope.Smoothness.y : UnfilteredSlope;
            foreach (ObjectPrototype prototype in stamp.Prototypes)
            {
                if (prototype == null || !prototype.Enabled || !prototype.Prefab) continue;
                if (names.Contains(prototype.Prefab.name)) continue;
                float tilt = Mathf.Max(MaxAbs(prototype.RotationX), MaxAbs(prototype.RotationZ)) +
                    prototype.SlopeAlignment * maxSlope;
                foreach (MeshFilter filter in prototype.Prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    // Collider proxies and other meshes without an enabled renderer are not visible.
                    if (!filter.TryGetComponent(out MeshRenderer renderer) || !renderer.enabled) continue;
                    if (!RockMeshOpenings.CanShowOpening(filter.sharedMesh, tilt)) continue;
                    names.Add(prototype.Prefab.name);
                    break;
                }
            }
        }

        private static float MaxAbs(Vector2 range)
        {
            return Mathf.Max(Mathf.Abs(range.x), Mathf.Abs(range.y));
        }
    }
}
