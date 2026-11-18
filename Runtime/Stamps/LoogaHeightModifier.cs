using System;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Base of the components that change terrain height. A <see cref="LoogaStampWorld"/> applies them in hierarchy
    /// order.
    /// </summary>
    public abstract class LoogaHeightModifier : MonoBehaviour
    {
        /// <summary>Changes after each settings change. Change tracking compares it.</summary>
        internal int Revision { get; private set; }

        [System.NonSerialized] private int _serializedHash;

        /// <summary>
        /// Marks the settings as changed, so the editor updater applies them again. Scripts call it after they change
        /// nested settings, such as the falloff, the noise or the filters, through their own setters.
        /// </summary>
        public void MarkChanged()
        {
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        // Unity also calls OnValidate after a script reload and after an undo of another object in the same hierarchy,
        // for example the spline of a generated stamp. Only a change of the serialized settings marks a change.
        protected virtual void OnValidate()
        {
            if (!SerializedState.Changed(this, ref _serializedHash)) return;
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        /// <summary>World XZ rectangle that decides which tiles get the modifier.</summary>
        internal abstract Rect GetCullRect(HeightMapping mapping);
    }

    /// <summary>Detects changes of the serialized settings of a stamp between two OnValidate calls.</summary>
    internal static class SerializedState
    {
        /// <summary>
        /// Updates the hash of the serialized fields of a target. Returns true when the hash changed, and always
        /// outside the editor and on the first call.
        /// </summary>
        public static bool Changed(UnityEngine.Object target, ref int hash)
        {
#if UNITY_EDITOR
            int next = UnityEditor.EditorJsonUtility.ToJson(target).GetHashCode();
            // 0 marks "not known yet", so a real hash of 0 counts as 1.
            next = next == 0 ? 1 : next;
            if (next == hash) return false;
            hash = next;
#endif
            return true;
        }
    }

    /// <summary>Fractal gradient noise settings. Values must agree with StampFalloff.hlsl.</summary>
    [Serializable]
    public sealed class StampNoiseSettings
    {
        [SerializeField] private StampNoiseType _type = StampNoiseType.None;
        [Tooltip("Cycles for each unit of the noise input.")]
        [SerializeField] private float _frequency = 10f;
        [Tooltip("Noise strength in metres.")]
        [SerializeField] private float _amplitude = 1f;
        [SerializeField] private float _offset;
        [SerializeField, Range(-0.5f, 0.5f)] private float _balance;

        public StampNoiseType Type => _type;
        public float Frequency => _frequency;
        public float Amplitude => _amplitude;
        public float Offset => _offset;
        public float Balance => _balance;

        /// <summary>Frequency, amplitude, offset and balance as one vector for the shaders.</summary>
        internal Vector4 Parameters => new Vector4(_frequency, _amplitude, _offset, _balance);

        public void Set(StampNoiseType type, float frequency, float amplitude, float offset, float balance)
        {
            _type = type;
            _frequency = frequency;
            _amplitude = amplitude;
            _offset = offset;
            _balance = balance;
        }
    }
}
