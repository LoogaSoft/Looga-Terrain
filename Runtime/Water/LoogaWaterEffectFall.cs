using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// A fall that the water network does not solve, for example a lake that spills over a long crest. It gives the
    /// lip and the landing of the fall to a <see cref="LoogaWaterEffects"/>, which then draws the mist and the spray
    /// of the fall with the network effects. The fall gets no sheet and no plunge flow from this component.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Water Effect Fall")]
    public sealed class LoogaWaterEffectFall : MonoBehaviour
    {
        private static readonly List<LoogaWaterEffectFall> ActiveFalls = new();

        [Tooltip("Network effects that draw the mist and the spray of this fall.")]
        [SerializeField] private LoogaWaterEffects _effects;
        [Tooltip("Points along the lip at the upper water level, in local space, in order across the fall.")]
        [SerializeField] private List<Vector3> _lip = new();
        [Tooltip("Points where the falling water meets the water below, in local space. One point for each lip point.")]
        [SerializeField] private List<Vector3> _landing = new();
        [Tooltip("Flow per metre of lip width, in square metres per second. The particle rate grows with it.")]
        [SerializeField, Min(0.01f)] private float _unitDischarge = 2.8f;
        [Tooltip("Particle rate of this fall. 0 = no effects.")]
        [SerializeField, Min(0f)] private float _rateScale = 1f;

        /// <summary>Enabled effect falls, in no fixed order.</summary>
        internal static IReadOnlyList<LoogaWaterEffectFall> Active => ActiveFalls;

        public LoogaWaterEffects Effects => _effects;
        public float UnitDischarge => Mathf.Max(0.01f, _unitDischarge);
        public float RateScale => _rateScale;
        /// <summary>Number of lip points that have a landing point.</summary>
        public int PointCount => Mathf.Min(_lip.Count, _landing.Count);

        #region Built-in
        private void OnEnable()
        {
            ActiveFalls.Add(this);
            Notify();
        }

        private void OnDisable()
        {
            ActiveFalls.Remove(this);
            Notify();
        }

        private void OnValidate()
        {
            Notify();
        }

        // A moved fall object moves the effects.
        private void Update()
        {
            if (!transform.hasChanged) return;

            transform.hasChanged = false;
            Notify();
        }
        #endregion

        #region Public
        /// <summary>Sets the network effects that draw this fall.</summary>
        public void SetEffects(LoogaWaterEffects effects)
        {
            Notify();
            _effects = effects;
            Notify();
        }

        /// <summary>Sets the lip and the landing points in world space. The lists must have the same count.</summary>
        public void SetPath(IReadOnlyList<Vector3> lip, IReadOnlyList<Vector3> landing)
        {
            _lip.Clear();
            _landing.Clear();
            int count = Mathf.Min(lip.Count, landing.Count);
            for (int index = 0; index < count; index++)
            {
                _lip.Add(transform.InverseTransformPoint(lip[index]));
                _landing.Add(transform.InverseTransformPoint(landing[index]));
            }
            Notify();
        }

        /// <summary>Gets the lip and the landing points in world space.</summary>
        public void GetPath(List<Vector3> lip, List<Vector3> landing)
        {
            lip.Clear();
            landing.Clear();
            for (int index = 0; index < PointCount; index++)
            {
                lip.Add(transform.TransformPoint(_lip[index]));
                landing.Add(transform.TransformPoint(_landing[index]));
            }
        }
        #endregion

        private void Notify()
        {
            if (_effects) _effects.MarkDirty();
        }
    }
}
