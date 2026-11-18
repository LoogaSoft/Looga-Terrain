using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Barriers
{
    /// <summary>
    /// Identifies the leaf hinges of a gate model. A <see cref="LoogaBarrierRun"/> turns the leaves of an open gate.
    /// The gate root uses +Z as the run direction. The meshes below a hinge show on which side of the hinge the leaf is.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LoogaBarrierGate : MonoBehaviour
    {
        [Tooltip("Leaf hinges. Each leaf turns around the up axis of its hinge.")]
        [SerializeField] private Transform[] _hinges = Array.Empty<Transform>();

        public IReadOnlyList<Transform> Hinges => _hinges;

        /// <summary>
        /// Turns each leaf from its current pose. All leaves open to the same side of the gate.
        /// </summary>
        /// <param name="angle">Leaf turn in degrees. Positive = toward the +X side of the gate root.</param>
        public void Open(float angle)
        {
            foreach (Transform hinge in _hinges)
            {
                if (!hinge)
                {
                    continue;
                }

                // A leaf on the -Z side of its hinge turns the other way, so that it opens to the same side.
                hinge.localRotation *= Quaternion.Euler(0f, angle * LeafSide(hinge), 0f);
            }
        }

        // Returns 1 if the leaf is on the +Z side of its hinge in gate space, and -1 if not.
        // A hinge without meshes uses its own +Z axis as the leaf direction.
        private float LeafSide(Transform hinge)
        {
            Vector3 centre = Vector3.zero;
            int count = 0;
            foreach (MeshFilter filter in hinge.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh)
                {
                    centre += filter.transform.TransformPoint(filter.sharedMesh.bounds.center);
                    count++;
                }
            }
            Vector3 leaf = count > 0 ? centre / count - hinge.position : hinge.forward;
            return transform.InverseTransformDirection(leaf).z >= 0f ? 1f : -1f;
        }
    }
}
