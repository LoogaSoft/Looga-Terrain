using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Changes material values of the placements of the Looga object stamp on this GameObject, for example a hue, a
    /// glow color or a brightness. One prefab and one material can then show several looks without recolored copies.
    /// </summary>
    /// <remarks>
    /// The shader of a prototype material marks each property that an effector can change with [LoogaEffectorTarget].
    /// Each placement takes one variant by its weight and a stable random value of its position. A value applies only
    /// to the prototypes whose materials mark the property. Other prototypes keep their material values.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(LoogaObjectStamp))]
    [AddComponentMenu("Looga/Terrain/Stamps/Material Effector")]
    public sealed class LoogaMaterialEffector : MonoBehaviour
    {
        /// <summary>How a value varies between placements.</summary>
        public enum ValueMode
        {
            /// <summary>All placements of the variant get the value.</summary>
            Constant,
            /// <summary>Each placement gets a stable random value between the value and the maximum.</summary>
            Random
        }

        /// <summary>One material value of a variant.</summary>
        [Serializable]
        public sealed class Value
        {
            [Tooltip("Shader property that the prototype material marks with [LoogaEffectorTarget].")]
            [SerializeField] private string _property;
            [Tooltip("Set replaces the material value. Multiply and Add change it.")]
            [SerializeField] private InstanceEffectorOperation _operation = InstanceEffectorOperation.Set;
            [SerializeField] private ValueMode _mode;
            [Tooltip("The value. A float property uses X. A color is in the color space of the material inspector.")]
            [SerializeField] private Vector4 _value = Vector4.one;
            [Tooltip("Random mode: each placement blends from the value to this maximum by its own random value.")]
            [SerializeField] private Vector4 _maximum = Vector4.one;

            public string Property => _property;
            public InstanceEffectorOperation Operation => _operation;
            public ValueMode Mode => _mode;
            public Vector4 Minimum => _value;
            public Vector4 Maximum => _maximum;

            public Value()
            {
            }

            public Value(string property, InstanceEffectorOperation operation, Vector4 value)
            {
                _property = property;
                _operation = operation;
                _value = value;
                _maximum = value;
            }

            /// <summary>A random value: each placement blends from the minimum to the maximum.</summary>
            public Value(string property, InstanceEffectorOperation operation, Vector4 minimum, Vector4 maximum)
            {
                _property = property;
                _operation = operation;
                _mode = ValueMode.Random;
                _value = minimum;
                _maximum = maximum;
            }

            internal Vector4 Sample(float random)
            {
                return _mode == ValueMode.Random ? Vector4.Lerp(_value, _maximum, random) : _value;
            }
        }

        /// <summary>One look: a set of values that a share of the placements takes.</summary>
        [Serializable]
        public sealed class Variant
        {
            [SerializeField] private string _name = "Variant";
            [Tooltip("Share of the placements that take this variant, relative to the other variants. 0 = none.")]
            [Min(0f)]
            [SerializeField] private float _weight = 1f;
            [SerializeField] private List<Value> _values = new();

            public string Name => _name;
            public float Weight => _weight;
            public IReadOnlyList<Value> Values => _values;

            public Variant()
            {
            }

            public Variant(string name, float weight, IEnumerable<Value> values)
            {
                _name = name;
                _weight = weight;
                _values = new List<Value>(values);
            }
        }

        [SerializeField] private List<Variant> _variants = new() { new Variant("Variant", 1f, Array.Empty<Value>()) };

        private bool _rewriteQueued;
        // Reused by Build, so a placement write makes no new lists after the first.
        private readonly List<string> _marked = new();
        private readonly List<Variant> _active = new();

        /// <summary>The variants. Each placement takes one variant by weight.</summary>
        public IReadOnlyList<Variant> Variants => _variants;

        /// <summary>Replaces the variants and writes the values of the stamp again.</summary>
        public void SetVariants(IEnumerable<Variant> variants)
        {
            _variants = new List<Variant>(variants);
            RequestRewrite();
        }

        #region Built-in
        private void OnEnable()
        {
            RequestRewrite();
        }

        private void OnDisable()
        {
            RequestRewrite();
        }

        private void OnValidate()
        {
            RequestRewrite();
        }
        #endregion

        #region Values
        /// <summary>
        /// Fills the effector columns of one prefab and its placements: one property name for each column, then one
        /// operation and value for each column and placement, or one row for all placements. Returns false when no
        /// value applies to the prefab.
        /// </summary>
        public bool Build(GameObject prefab, IReadOnlyList<InstanceSet.Placement> placements, List<string> properties,
            List<InstanceEffectorOperation> operations, List<Vector4> values)
        {
            properties.Clear();
            operations.Clear();
            values.Clear();
            if (!prefab || placements == null || placements.Count == 0) return false;

            CollectMarked(prefab);
            float totalWeight = 0f;
            bool random = false;
            _active.Clear();
            foreach (Variant variant in _variants)
            {
                if (variant == null || variant.Weight <= 0f) continue;

                _active.Add(variant);
                totalWeight += variant.Weight;
                foreach (Value value in variant.Values)
                {
                    if (value == null || !_marked.Contains(value.Property)) continue;

                    if (!properties.Contains(value.Property))
                    {
                        properties.Add(value.Property);
                    }
                    random |= value.Mode == ValueMode.Random;
                }
            }
            if (properties.Count == 0) return false;

            if (_active.Count == 1 && !random)
            {
                AddRow(_active[0], 0, properties, operations, values);
                return true;
            }
            for (int index = 0; index < placements.Count; index++)
            {
                uint hash = PlacementHash(placements[index].Position);
                Variant variant = Pick(hash, totalWeight);
                AddRow(variant, hash, properties, operations, values);
            }
            return true;
        }

        // The marked properties of the materials of the prefab.
        private void CollectMarked(GameObject prefab)
        {
            _marked.Clear();
            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (!material) continue;

                    foreach (InstanceEffectorTargets.Target target in InstanceEffectorTargets.Get(material.shader))
                    {
                        if (!_marked.Contains(target.Name))
                        {
                            _marked.Add(target.Name);
                        }
                    }
                }
            }
        }

        private Variant Pick(uint hash, float totalWeight)
        {
            float pick = Unit(hash) * totalWeight;
            foreach (Variant variant in _active)
            {
                pick -= variant.Weight;
                if (pick < 0f) return variant;
            }
            return _active[_active.Count - 1];
        }

        // One operation and value for each column. A column without a value in the variant keeps the material value.
        private static void AddRow(Variant variant, uint hash, List<string> properties,
            List<InstanceEffectorOperation> operations, List<Vector4> values)
        {
            for (int column = 0; column < properties.Count; column++)
            {
                Value found = null;
                foreach (Value value in variant.Values)
                {
                    if (value != null && value.Property == properties[column])
                    {
                        found = value;
                        break;
                    }
                }
                if (found == null)
                {
                    operations.Add(InstanceEffectorOperation.None);
                    values.Add(Vector4.zero);
                    continue;
                }
                // Each column takes its own random value, so two random properties do not change together.
                operations.Add(found.Operation);
                values.Add(found.Sample(Unit(Mix(hash + (uint)column * 0x9e3779b9u))));
            }
        }

        // A stable value for a placement position in millimeters, so a rebuild gives each placement the same variant.
        private static uint PlacementHash(Vector3 position)
        {
            unchecked
            {
                uint hash = (uint)Mathf.RoundToInt(position.x * 1000f) * 73856093u ^
                    (uint)Mathf.RoundToInt(position.y * 1000f) * 19349663u ^
                    (uint)Mathf.RoundToInt(position.z * 1000f) * 83492791u;
                return Mix(hash);
            }
        }

        private static uint Mix(uint hash)
        {
            unchecked
            {
                hash ^= hash >> 16;
                hash *= 0x7feb352du;
                hash ^= hash >> 15;
                hash *= 0x846ca68bu;
                hash ^= hash >> 16;
                return hash;
            }
        }

        private static float Unit(uint hash)
        {
            return (hash & 0xffffffu) / 16777216f;
        }
        #endregion

        #region Rewrite
        // An effector change needs no new placements. The world writes the effector values of the stored placements of
        // the stamp again. In the editor the write waits for the next update, so OnValidate does not change the set.
        private void RequestRewrite()
        {
#if UNITY_EDITOR
            if (_rewriteQueued) return;

            _rewriteQueued = true;
            UnityEditor.EditorApplication.delayCall += Rewrite;
#else
            Rewrite();
#endif
        }

        private void Rewrite()
        {
            _rewriteQueued = false;
            if (!this || !TryGetComponent(out LoogaObjectStamp stamp)) return;

            LoogaStampWorld world = GetComponentInParent<LoogaStampWorld>();
            if (world)
            {
                world.RewriteEffectors(stamp);
            }
        }
        #endregion
    }
}
