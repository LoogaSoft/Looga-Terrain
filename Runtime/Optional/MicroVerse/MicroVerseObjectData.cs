#if LOOGA_TERRAIN_MICROVERSE_OBJECTS
using System;
using System.Collections.Generic;
using JBooth.MicroVerseCore;
using LoogaSoft.Instancing;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Integrations
{
    /// <summary>Counts and timings of the last MicroVerse object data capture.</summary>
    public sealed class MicroVerseObjectDataReport
    {
        /// <summary>Spawned roots with native renderers that the capture found.</summary>
        public int NativeRoots { get; internal set; }
        /// <summary>Spawned roots that match no stamp prefab. These roots keep native rendering.</summary>
        public int UnmatchedRoots { get; internal set; }
        /// <summary>Placements written to the set.</summary>
        public int Placements { get; internal set; }
        /// <summary>Owner and prototype entries written by the capture.</summary>
        public int Entries { get; internal set; }
        /// <summary>Time to find and match the spawned roots.</summary>
        public double ScanMilliseconds { get; internal set; }
        /// <summary>Total time of the set entry updates.</summary>
        public double ConfigureMilliseconds { get; internal set; }
        /// <summary>Total time to hide or strip the native render parts.</summary>
        public double HideMilliseconds { get; internal set; }
        /// <summary>Instance data bytes that the set renderer uploaded.</summary>
        public long UploadedBytes { get; internal set; }
    }

    // Finds the spawned roots of MicroVerse object stamps, matches them to stamp prefabs and hides their render parts.
    internal static class MicroVerseObjectData
    {
        internal sealed class Group
        {
            internal ObjectStamp Stamp;
            internal GameObject Prefab;
            internal readonly List<GameObject> Roots = new();
            internal readonly List<InstanceSet.Placement> Placements = new();
        }

        private static readonly List<MeshRenderer> _renderers = new();
        private static readonly List<Component> _components = new();

        /// <summary>
        /// Return one group for each prefab of each stamp that needs new placements.
        /// A stamp needs new placements when MicroVerse respawned it or when its output is empty.
        /// </summary>
        internal static List<Group> Collect(ObjectStamp[] stamps, Transform space, out int nativeRoots, out int unmatchedRoots)
        {
            var groups = new List<Group>();
            nativeRoots = 0;
            unmatchedRoots = 0;
            foreach (ObjectStamp stamp in stamps)
            {
                if (!stamp) continue;
                if (stamp.spawnAsPrefab)
                {
                    // The MicroVerse pool can return a stripped prefab instance at the next update.
                    throw new NotSupportedException(stamp.name + ": data capture does not support Spawn As Prefab.");
                }
                Transform output = stamp.parentObject;
                if (!output)
                {
                    throw new NotSupportedException(stamp.name + ": data capture requires a Parent Object.");
                }
                var stampGroups = new List<Group>();
                var signatures = new List<Part[]>();
                foreach (GameObject prefab in stamp.prototypes)
                {
                    if (!prefab) continue;
                    stampGroups.Add(new Group { Stamp = stamp, Prefab = prefab });
                    signatures.Add(Signature(prefab));
                }
                int matched = 0;
                for (int child = 0; child < output.childCount; child++)
                {
                    GameObject root = output.GetChild(child).gameObject;
                    if (!HasNativeRenderer(root)) continue;
                    nativeRoots++;
                    Group group = Match(root, stampGroups, signatures);
                    if (group == null || !TryGetPlacement(root, space, out InstanceSet.Placement placement))
                    {
                        unmatchedRoots++;
                        continue;
                    }
                    group.Roots.Add(root);
                    group.Placements.Add(placement);
                    matched++;
                }
                // Hidden or stripped roots from an earlier capture mean that MicroVerse did not respawn this stamp.
                if (matched > 0 || output.childCount == 0)
                {
                    groups.AddRange(stampGroups);
                }
            }
            return groups;
        }

        /// <summary>Hide the render parts, or remove them and keep each root with its colliders.</summary>
        internal static void Hide(List<GameObject> roots, bool strip)
        {
            foreach (GameObject root in roots)
            {
                if (!root) continue;
                root.GetComponentsInChildren(true, _renderers);
                if (!strip)
                {
                    foreach (MeshRenderer renderer in _renderers)
                    {
                        renderer.forceRenderingOff = true;
                    }
                    continue;
                }
                // Remove the LOD groups first, because they reference the renderers.
                foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
                {
                    Destroy(group);
                }
                foreach (MeshRenderer renderer in _renderers)
                {
                    if (!renderer) continue;
                    GameObject part = renderer.gameObject;
                    if (part != root && IsRenderOnly(part))
                    {
                        Destroy(part);
                        continue;
                    }
                    MeshFilter filter = part.GetComponent<MeshFilter>();
                    Destroy(renderer);
                    if (filter)
                    {
                        Destroy(filter);
                    }
                }
            }
            _renderers.Clear();
        }

        private static bool HasNativeRenderer(GameObject root)
        {
            root.GetComponentsInChildren(true, _renderers);
            foreach (MeshRenderer renderer in _renderers)
            {
                if (!renderer.forceRenderingOff) return true;
            }
            return false;
        }

        private static Group Match(GameObject root, List<Group> groups, List<Part[]> signatures)
        {
            Part[] signature = Signature(root);
            for (int i = 0; i < groups.Count; i++)
            {
                if (SameSignature(signature, signatures[i])) return groups[i];
            }
            return null;
        }

        // A clone has the renderers of its prefab in the same hierarchy order. Prefab variants can share meshes and
        // differ only in materials or part transforms, so the signature compares all three.
        private struct Part
        {
            internal Mesh Mesh;
            internal Material[] Materials;
            internal Matrix4x4 Local;
        }

        private static Part[] Signature(GameObject root)
        {
            root.GetComponentsInChildren(true, _renderers);
            var parts = new Part[_renderers.Count];
            for (int i = 0; i < parts.Length; i++)
            {
                MeshFilter filter = _renderers[i].GetComponent<MeshFilter>();
                parts[i] = new Part
                {
                    Mesh = filter ? filter.sharedMesh : null,
                    Materials = _renderers[i].sharedMaterials,
                    Local = RelativeMatrix(root.transform, _renderers[i].transform)
                };
            }
            return parts;
        }

        // Build the matrix from local transforms only. World matrices lose precision far from the origin.
        private static Matrix4x4 RelativeMatrix(Transform root, Transform part)
        {
            Matrix4x4 matrix = Matrix4x4.identity;
            for (Transform current = part; current != root; current = current.parent)
            {
                matrix = Matrix4x4.TRS(current.localPosition, current.localRotation, current.localScale) * matrix;
            }
            return matrix;
        }

        private static bool SameSignature(Part[] a, Part[] b)
        {
            if (a.Length == 0 || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].Mesh != b[i].Mesh || a[i].Materials.Length != b[i].Materials.Length) return false;
                for (int m = 0; m < a[i].Materials.Length; m++)
                {
                    if (a[i].Materials[m] != b[i].Materials[m]) return false;
                }
                for (int element = 0; element < 16; element++)
                {
                    if (Mathf.Abs(a[i].Local[element] - b[i].Local[element]) > 0.0001f) return false;
                }
            }
            return true;
        }

        // Placements store translation, rotation and scale. A sheared matrix keeps native rendering.
        private static bool TryGetPlacement(GameObject root, Transform space, out InstanceSet.Placement placement)
        {
            Matrix4x4 matrix = space.worldToLocalMatrix * root.transform.localToWorldMatrix;
            Vector3 position = matrix.GetPosition();
            Quaternion rotation = matrix.rotation;
            Vector3 scale = matrix.lossyScale;
            Matrix4x4 rebuilt = Matrix4x4.TRS(position, rotation, scale);
            float tolerance = 0.0001f * Mathf.Max(1, Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            placement = default;
            for (int column = 0; column < 3; column++)
            {
                Vector4 difference = matrix.GetColumn(column) - rebuilt.GetColumn(column);
                if (difference.sqrMagnitude > tolerance * tolerance) return false;
            }
            placement = new InstanceSet.Placement
            {
                Position = position,
                EulerAngles = rotation.eulerAngles,
                Scale = scale
            };
            return true;
        }

        private static bool IsRenderOnly(GameObject part)
        {
            part.GetComponentsInChildren(true, _components);
            foreach (Component component in _components)
            {
                if (!(component is Transform || component is MeshFilter || component is MeshRenderer || component is LODGroup))
                {
                    _components.Clear();
                    return false;
                }
            }
            _components.Clear();
            return true;
        }

        private static void Destroy(Object target)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(target);
            }
            else
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}
#endif
