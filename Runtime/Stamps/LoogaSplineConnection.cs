using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>How two connected splines meet.</summary>
    public enum LoogaSplineConnectionKind
    {
        /// <summary>The From anchor, an end of its spline, continues into the To anchor, the start of its spline.</summary>
        Continues = 0,
        /// <summary>The From anchor, an end of its spline, meets the To spline at the To anchor, as a tributary does.</summary>
        Joins = 1,
        /// <summary>The From anchor, an end of its spline, drops into the To spline at the To anchor, as a waterfall does.</summary>
        FallsInto = 2,
        /// <summary>The From spline forms the banks of the To spline over the To range, as a cliff or a wall does.</summary>
        Banks = 3
    }

    /// <summary>
    /// A position or a range on a spline, in meters along the spline from its start, or from its end. A distance from
    /// the end stays on the end when the spline gets longer or shorter. An end before the start is a single position.
    /// </summary>
    [Serializable]
    public struct LoogaSplineAnchor
    {
        [SerializeField] private SplineContainer _spline;
        [SerializeField, Tooltip("Measures the range from the end of the spline instead of its start.")]
        private bool _fromEnd;
        [SerializeField, Min(0f), Tooltip("Start of the range, in meters along the spline.")]
        private float _start;
        [SerializeField, Min(0f), Tooltip("End of the range, in meters along the spline. A value below the start makes " +
            "the anchor a single position.")]
        private float _end;

        public LoogaSplineAnchor(SplineContainer spline, float start, float end, bool fromEnd)
        {
            _spline = spline;
            _start = Mathf.Max(0f, start);
            _end = Mathf.Max(0f, end);
            _fromEnd = fromEnd;
        }

        public SplineContainer Spline => _spline;
        public bool FromEnd => _fromEnd;
        public float Start => _start;
        public float End => _end;

        /// <summary>The range in meters from the start of a spline of the given length, x &lt;= y.</summary>
        public Vector2 GetRange(float length)
        {
            float first = Mathf.Clamp(_start, 0f, length);
            float last = Mathf.Clamp(Mathf.Max(_start, _end), 0f, length);
            return _fromEnd ? new Vector2(length - last, length - first) : new Vector2(first, last);
        }

        /// <summary>The world position at a distance in meters from the start of the spline.</summary>
        public Vector3 GetPosition(float distance)
        {
            // The evaluation ratio of Unity splines is the share of the spline length.
            float length = _spline.CalculateLength();
            return _spline.EvaluatePosition(length > 0f ? Mathf.Clamp01(distance / length) : 0f);
        }
    }

    /// <summary>
    /// Records how two splines meet, so the tools that generate from each spline treat the meeting the same way: for
    /// example a river that falls into another river, a tributary, or a cliff that forms the banks of a river. Anchors
    /// use distances along the splines, never knot or point indices, so they stay in place when knots are added. The
    /// component only holds the data; generators read it with <see cref="On"/> and redo their work when
    /// <see cref="Changed"/> reports a connection of their spline.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Spline Connection")]
    [ExecuteAlways]
    public sealed class LoogaSplineConnection : MonoBehaviour
    {
        private static readonly List<LoogaSplineConnection> Active = new List<LoogaSplineConnection>();

        [SerializeField] private LoogaSplineConnectionKind _kind;
        [SerializeField] private LoogaSplineAnchor _from;
        [SerializeField] private LoogaSplineAnchor _to;

        /// <summary>Raised when a connection is enabled, disabled or edited.</summary>
        public static event Action<LoogaSplineConnection> Changed;

        /// <summary>The enabled connections of the open scenes.</summary>
        public static IReadOnlyList<LoogaSplineConnection> All => Active;

        public LoogaSplineConnectionKind Kind => _kind;
        public LoogaSplineAnchor From => _from;
        public LoogaSplineAnchor To => _to;

        /// <summary>The enabled connections with an anchor on the spline.</summary>
        public static IEnumerable<LoogaSplineConnection> On(SplineContainer spline)
        {
            if (!spline) yield break;
            foreach (LoogaSplineConnection connection in Active)
            {
                if (connection._from.Spline == spline || connection._to.Spline == spline)
                {
                    yield return connection;
                }
            }
        }

        /// <summary>True when the connection has an anchor on the spline.</summary>
        public bool Involves(SplineContainer spline)
        {
            return spline && (_from.Spline == spline || _to.Spline == spline);
        }

        /// <summary>Sets the kind and the anchors. The caller records Undo on the component.</summary>
        public void Set(LoogaSplineConnectionKind kind, LoogaSplineAnchor from, LoogaSplineAnchor to)
        {
            _kind = kind;
            _from = from;
            _to = to;
            Changed?.Invoke(this);
        }

        private void OnEnable()
        {
            if (!Active.Contains(this)) Active.Add(this);
            Changed?.Invoke(this);
        }

        private void OnDisable()
        {
            Active.Remove(this);
            Changed?.Invoke(this);
        }

        private void OnValidate()
        {
            if (isActiveAndEnabled) Changed?.Invoke(this);
        }

        private void OnDrawGizmosSelected()
        {
            if (!_from.Spline || !_to.Spline) return;
            Vector3 from = Midpoint(_from);
            Vector3 to = Midpoint(_to);
            Gizmos.color = _kind == LoogaSplineConnectionKind.Banks ? new Color(0.9f, 0.6f, 0.2f) : new Color(0.2f, 0.7f, 1f);
            Gizmos.DrawLine(from, to);
            Gizmos.DrawWireSphere(from, 2f);
            Gizmos.DrawWireSphere(to, 2f);
        }

        private static Vector3 Midpoint(LoogaSplineAnchor anchor)
        {
            Vector2 range = anchor.GetRange(anchor.Spline.CalculateLength());
            return anchor.GetPosition(0.5f * (range.x + range.y));
        }
    }
}
