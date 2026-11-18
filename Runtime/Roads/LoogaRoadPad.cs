using System;
using System.Collections.Generic;
using LoogaSoft.Terrain.Stamps;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Roads
{
    /// <summary>
    /// A flat pad that roads join, for example a site or a parking area: a stamp group with a closed outline spline.
    /// Keeps the linked road knots on the pad. When the pad moves or turns, the linked knots move with it. The next
    /// knots of each road ease toward the new position.
    /// </summary>
    /// <remarks>
    /// The knots follow before the Looga editor updater compares the modifiers
    /// (<see cref="LoogaStampWorld.BeforeChangeCapture"/>), so one terrain update applies the pad and the road edits.
    /// The updater finds the moved knots itself.
    /// </remarks>
    [ExecuteAlways, DisallowMultipleComponent]
    [AddComponentMenu("Looga/Roads/Road Pad")]
    public sealed class LoogaRoadPad : MonoBehaviour
    {
        /// <summary>A run of consecutive knots of one road spline that keeps its position on the pad.</summary>
        [Serializable]
        public sealed class RoadLink
        {
            // A knot is at the expected position when it is this close to it, in meters.
            private const float MatchTolerance = 0.05f;

            [SerializeField] private SplineContainer _roads;
            [SerializeField] private int _spline;
            [SerializeField] private int _firstKnot;
            // Knot positions in pad space.
            [SerializeField] private Vector3[] _padPositions;
            // Knot positions in world space after the last update. They find the spline again after a spline index
            // changes.
            [SerializeField] private Vector3[] _worldPositions;

            public SplineContainer Roads => _roads;
            /// <summary>
            /// False when the linked knots are gone, for example after a manual knot edit. Link again.
            /// </summary>
            public bool IsFound => _roads && Locate();

            public RoadLink(SplineContainer roads, int spline, int firstKnot, int knotCount, Transform pad)
            {
                _roads = roads;
                _spline = spline;
                _firstKnot = firstKnot;
                _padPositions = new Vector3[knotCount];
                _worldPositions = new Vector3[knotCount];
                for (int index = 0; index < knotCount; index++)
                {
                    Vector3 world = KnotWorld(roads, roads.Splines[spline], firstKnot + index);
                    _worldPositions[index] = world;
                    _padPositions[index] = pad.InverseTransformPoint(world);
                }
            }

            public override string ToString()
            {
                if (!_roads) return "Missing road container.";
                int last = _firstKnot + _padPositions.Length - 1;
                return _roads.name + " spline " + _spline + ", knots " + _firstKnot +
                    (last > _firstKnot ? "-" + last : "");
            }

            /// <summary>
            /// Moves the linked knots to their pad positions and eases the next knots of the road.
            /// </summary>
            /// <param name="easeKnots">Number of knots past each end of the link that move part of the way.</param>
            /// <param name="changed">Grows to include the old and new positions of the moved knots.</param>
            /// <returns>True when a knot moved.</returns>
            public bool Follow(Transform pad, int easeKnots, ref Bounds changed)
            {
                if (!_roads || !Locate()) return false;
                Spline spline = _roads.Splines[_spline];
                int count = _padPositions.Length;
                var targets = new Vector3[count];
                bool moved = false;
                for (int index = 0; index < count; index++)
                {
                    targets[index] = pad.TransformPoint(_padPositions[index]);
                    moved |= (targets[index] - _worldPositions[index]).sqrMagnitude > 1e-6f;
                }
                if (!moved) return false;

                Vector3 firstDelta = targets[0] - _worldPositions[0];
                Vector3 lastDelta = targets[count - 1] - _worldPositions[count - 1];
                for (int index = 0; index < count; index++)
                {
                    Encapsulate(ref changed, _worldPositions[index]);
                    SetKnotWorld(spline, _firstKnot + index, targets[index]);
                    Encapsulate(ref changed, targets[index]);
                }
                for (int step = 1; step <= easeKnots; step++)
                {
                    float weight = 1f - step / (easeKnots + 1f);
                    EaseKnot(spline, _firstKnot - step, firstDelta * weight, ref changed);
                    EaseKnot(spline, _firstKnot + count - 1 + step, lastDelta * weight, ref changed);
                }
                _worldPositions = targets;
                return true;
            }

            // Finds the linked knots after a spline or knot index changed. Returns false when the knots are gone.
            private bool Locate()
            {
                int count = _padPositions?.Length ?? 0;
                if (count == 0) return false;
                IReadOnlyList<Spline> splines = _roads.Splines;
                if (Matches(splines, _spline, _firstKnot, count)) return true;
                for (int spline = 0; spline < splines.Count; spline++)
                {
                    for (int knot = 0; knot + count <= splines[spline].Count; knot++)
                    {
                        if (!Matches(splines, spline, knot, count)) continue;
                        _spline = spline;
                        _firstKnot = knot;
                        return true;
                    }
                }
                return false;
            }

            private bool Matches(IReadOnlyList<Spline> splines, int spline, int firstKnot, int count)
            {
                if (spline < 0 || spline >= splines.Count || firstKnot < 0 || firstKnot + count > splines[spline].Count)
                {
                    return false;
                }
                Vector3 first = KnotWorld(_roads, splines[spline], firstKnot);
                Vector3 last = KnotWorld(_roads, splines[spline], firstKnot + count - 1);
                return (first - _worldPositions[0]).sqrMagnitude < MatchTolerance * MatchTolerance &&
                    (last - _worldPositions[count - 1]).sqrMagnitude < MatchTolerance * MatchTolerance;
            }

            private void EaseKnot(Spline spline, int knot, Vector3 delta, ref Bounds changed)
            {
                if (knot < 0 || knot >= spline.Count) return;
                Vector3 world = KnotWorld(_roads, spline, knot);
                Encapsulate(ref changed, world);
                SetKnotWorld(spline, knot, world + delta);
                Encapsulate(ref changed, world + delta);
            }

            private void SetKnotWorld(Spline spline, int knot, Vector3 world)
            {
                BezierKnot value = spline[knot];
                value.Position = (float3)_roads.transform.InverseTransformPoint(world);
                spline[knot] = value;
            }

            private static Vector3 KnotWorld(SplineContainer roads, Spline spline, int knot)
            {
                return roads.transform.TransformPoint((Vector3)(float3)spline[knot].Position);
            }

            private static void Encapsulate(ref Bounds bounds, Vector3 point)
            {
                if (bounds.size == Vector3.zero && bounds.center == Vector3.zero)
                {
                    bounds = new Bounds(point, Vector3.zero);
                }
                else
                {
                    bounds.Encapsulate(point);
                }
            }
        }

        [Tooltip("Road knots that keep their position on the pad.")]
        [SerializeField] private List<RoadLink> _roadLinks = new();
        [Tooltip("Road knots past each end of a link that move part of the way with the pad. " +
                 "0 moves only the linked knots.")]
        [Range(0, 6)]
        [SerializeField] private int _easeKnots = 2;
        [Tooltip("Pads with the same level group share one level, for example the terraces of one site. Auto level " +
                 "sets every pad of the group to the median ground under all of them. Empty levels the pad alone.")]
        [SerializeField] private string _levelGroup = "";
        [SerializeField, HideInInspector] private Matrix4x4 _lastPadMatrix;

#if UNITY_EDITOR
        private LoogaStampWorld _world;
#endif

        public IReadOnlyList<RoadLink> RoadLinks => _roadLinks;
        /// <summary>Name of the pads that share one level. Empty when the pad has its own level.</summary>
        public string LevelGroup => _levelGroup;

        #region Built-in
        private void OnEnable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += EditorTick;
            _world = GetComponentInParent<LoogaStampWorld>();
            if (_world)
            {
                _world.BeforeChangeCapture += FollowIfMoved;
            }
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
            if (_world)
            {
                _world.BeforeChangeCapture -= FollowIfMoved;
            }
            _world = null;
#endif
        }
        #endregion

        #region Links
        /// <summary>
        /// Replaces the road links with every run of road knots inside the pad outline plus a margin.
        /// </summary>
        /// <param name="roadContainers">Road spline containers to search.</param>
        /// <param name="outlineSize">Width and depth of the pad outline in pad space, in meters.</param>
        /// <param name="margin">Distance past the outline that still links a knot, in meters.</param>
        public void LinkNearbyRoadKnots(IEnumerable<SplineContainer> roadContainers, Vector2 outlineSize, float margin)
        {
            _roadLinks.Clear();
            float halfX = outlineSize.x * 0.5f + margin;
            float halfZ = outlineSize.y * 0.5f + margin;
            foreach (SplineContainer roads in roadContainers)
            {
                for (int spline = 0; spline < roads.Splines.Count; spline++)
                {
                    Spline knots = roads.Splines[spline];
                    int runStart = -1;
                    for (int knot = 0; knot <= knots.Count; knot++)
                    {
                        bool inside = knot < knots.Count && IsInside(roads, knots, knot, halfX, halfZ);
                        if (inside && runStart < 0)
                        {
                            runStart = knot;
                        }
                        else if (!inside && runStart >= 0)
                        {
                            _roadLinks.Add(new RoadLink(roads, spline, runStart, knot - runStart, transform));
                            runStart = -1;
                        }
                    }
                }
            }
            _lastPadMatrix = transform.localToWorldMatrix;
        }

        private bool IsInside(SplineContainer roads, Spline spline, int knot, float halfX, float halfZ)
        {
            Vector3 world = roads.transform.TransformPoint((Vector3)(float3)spline[knot].Position);
            Vector3 local = transform.InverseTransformPoint(world);
            return Mathf.Abs(local.x) <= halfX && Mathf.Abs(local.z) <= halfZ;
        }
        #endregion

        #region Follow
#if UNITY_EDITOR
        // A pad outside a stamp world still follows in its own editor tick.
        private void EditorTick()
        {
            if (Application.isPlaying || !this) return;
            FollowIfMoved();
        }

        private void FollowIfMoved()
        {
            if (transform.localToWorldMatrix != _lastPadMatrix)
            {
                FollowPad();
            }
        }

        // The knot edits are not undo records. The linked knots depend only on the pad transform, and the eased knots
        // move back by the opposite delta. An undo of the pad move therefore moves the road back in the next tick.
        private void FollowPad()
        {
            var changed = new Bounds();
            foreach (RoadLink link in _roadLinks)
            {
                if (link.Follow(transform, _easeKnots, ref changed))
                {
                    UnityEditor.EditorUtility.SetDirty(link.Roads);
                }
            }
            _lastPadMatrix = transform.localToWorldMatrix;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
        #endregion
    }
}
