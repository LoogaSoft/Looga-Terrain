using LoogaSoft.Instancing;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Instances
{
    /// <summary>Identifies native terrain vegetation without using a prototype index.</summary>
    public enum VegetationKind
    {
        Detail,
        Tree
    }

    /// <summary>Selects fields that a source changes from its kind default.</summary>
    [Flags]
    public enum VegetationQualityFields
    {
        None = 0,
        Decorative = 1 << 0,
        Density = 1 << 1,
        MinimumPixels = 1 << 2,
        ShadowDistance = 1 << 3,
        ShadowFadeDistance = 1 << 4,
        MinimumShadowLod = 1 << 5,
        ShadowSplits = 1 << 6,
        ViewDistance = 1 << 7,
        ViewFadeDistance = 1 << 8,
        LodBias = 1 << 9,
        ShadowMode = 1 << 10,
        DensityFalloffDistance = 1 << 11,
        FarDensity = 1 << 12
    }

    /// <summary>Stores shared tree and detail policies and stable source overrides.</summary>
    [CreateAssetMenu(menuName = "Looga/Terrain/Vegetation Settings")]
    public sealed class VegetationSettings : ScriptableObject
    {
        [Serializable]
        public sealed class OverrideEntry
        {
            [SerializeField] private VegetationKind _kind;
            [SerializeField] private UnityEngine.Object _source;
            [SerializeField] private VegetationQualityFields _fields;
            [SerializeField] private InstanceQualitySettings _quality = InstanceQualitySettings.Default;

            public VegetationKind Kind => _kind;
            public UnityEngine.Object Source => _source;
            public VegetationQualityFields Fields => _fields;
            public InstanceQualitySettings Quality => _quality;

            public OverrideEntry(VegetationKind kind, UnityEngine.Object source,
                VegetationQualityFields fields, InstanceQualitySettings quality)
            {
                _kind = kind;
                _source = source;
                _fields = fields;
                _quality = quality;
            }

            internal InstanceQualitySettings Apply(InstanceQualitySettings result)
            {
                if ((_fields & VegetationQualityFields.Decorative) != 0) result.Decorative = _quality.Decorative;
                if ((_fields & VegetationQualityFields.Density) != 0) result.Density = _quality.Density;
                if ((_fields & VegetationQualityFields.MinimumPixels) != 0) result.MinimumPixels = _quality.MinimumPixels;
                if ((_fields & VegetationQualityFields.DensityFalloffDistance) != 0) result.DensityFalloffDistance = _quality.DensityFalloffDistance;
                if ((_fields & VegetationQualityFields.FarDensity) != 0) result.FarDensity = _quality.FarDensity;
                if ((_fields & VegetationQualityFields.ShadowDistance) != 0) result.ShadowDistance = _quality.ShadowDistance;
                if ((_fields & VegetationQualityFields.ShadowFadeDistance) != 0) result.ShadowFadeDistance = _quality.ShadowFadeDistance;
                if ((_fields & VegetationQualityFields.MinimumShadowLod) != 0) result.MinimumShadowLod = _quality.MinimumShadowLod;
                if ((_fields & VegetationQualityFields.ShadowSplits) != 0) result.ShadowSplits = _quality.ShadowSplits;
                if ((_fields & VegetationQualityFields.ViewDistance) != 0) result.ViewDistance = _quality.ViewDistance;
                if ((_fields & VegetationQualityFields.ViewFadeDistance) != 0) result.ViewFadeDistance = _quality.ViewFadeDistance;
                if ((_fields & VegetationQualityFields.LodBias) != 0) result.LodBias = _quality.LodBias;
                if ((_fields & VegetationQualityFields.ShadowMode) != 0) result.ShadowMode = _quality.ShadowMode;
                return result;
            }
        }

        [SerializeField] private InstanceQualitySettings _detailDefaults = InstanceQualitySettings.Default;
        [SerializeField] private InstanceQualitySettings _treeDefaults = InstanceQualitySettings.Default;
        [SerializeField] private List<OverrideEntry> _overrides = new();
        [Tooltip("Generate mesh detail placements on the GPU. Placement differs from native terrain details. " +
                 "Trees and explicit page sources keep the CPU path.")]
        [SerializeField] private bool _gpuResidentDetails;

        /// <summary>Raised when a caller applies edited settings.</summary>
        public event Action Changed;

        /// <summary>True when streaming providers generate detail placements on the GPU.</summary>
        public bool GpuResidentDetails => _gpuResidentDetails;

        public InstanceQualitySettings DetailDefaults => _detailDefaults;
        public InstanceQualitySettings TreeDefaults => _treeDefaults;
        public IReadOnlyList<OverrideEntry> Overrides => _overrides ??= new List<OverrideEntry>();

        /// <summary>Resolve defaults and enabled fields for a stable prefab or texture reference.</summary>
        public InstanceQualitySettings Resolve(VegetationKind kind, UnityEngine.Object source)
        {
            if (kind != VegetationKind.Detail && kind != VegetationKind.Tree)
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }
            InstanceQualitySettings result = kind == VegetationKind.Tree ? _treeDefaults : _detailDefaults;
            if (source && _overrides != null)
            {
                foreach (OverrideEntry entry in _overrides)
                {
                    if (entry != null && entry.Kind == kind && entry.Source == source)
                    {
                        result = entry.Apply(result);
                    }
                }
            }
            Validate(result);
            return result;
        }

        /// <summary>Set one kind default and notify active providers.</summary>
        public void SetDefaults(VegetationKind kind, InstanceQualitySettings quality)
        {
            Validate(quality);
            InstanceQualitySettings previous;
            if (kind == VegetationKind.Tree)
            {
                previous = _treeDefaults;
                _treeDefaults = quality;
            }
            else if (kind == VegetationKind.Detail)
            {
                previous = _detailDefaults;
                _detailDefaults = quality;
            }
            else throw new ArgumentOutOfRangeException(nameof(kind));
            try
            {
                if (_overrides != null)
                {
                    foreach (OverrideEntry entry in _overrides)
                        if (entry != null && entry.Kind == kind && entry.Source) Resolve(kind, entry.Source);
                }
            }
            catch
            {
                if (kind == VegetationKind.Tree) _treeDefaults = previous;
                else _detailDefaults = previous;
                throw;
            }
            NotifyChanged();
        }

        /// <summary>Replace one source override and notify active providers.</summary>
        public void SetOverride(VegetationKind kind, UnityEngine.Object source,
            VegetationQualityFields fields, InstanceQualitySettings quality)
        {
            ValidateSource(kind, source);
            const VegetationQualityFields allFields = VegetationQualityFields.Decorative |
                VegetationQualityFields.Density | VegetationQualityFields.MinimumPixels |
                VegetationQualityFields.ShadowDistance | VegetationQualityFields.ShadowFadeDistance |
                VegetationQualityFields.MinimumShadowLod | VegetationQualityFields.ShadowSplits |
                VegetationQualityFields.ViewDistance | VegetationQualityFields.ViewFadeDistance |
                VegetationQualityFields.LodBias | VegetationQualityFields.ShadowMode |
                VegetationQualityFields.DensityFalloffDistance | VegetationQualityFields.FarDensity;
            if ((fields & ~allFields) != 0) throw new ArgumentOutOfRangeException(nameof(fields));
            InstanceQualitySettings resolved = kind == VegetationKind.Tree ? _treeDefaults : _detailDefaults;
            resolved = new OverrideEntry(kind, source, fields, quality).Apply(resolved);
            Validate(resolved);
            _overrides ??= new List<OverrideEntry>();
            _overrides.RemoveAll(entry => entry != null && entry.Kind == kind && entry.Source == source);
            _overrides.Add(new OverrideEntry(kind, source, fields, quality));
            NotifyChanged();
        }

        /// <summary>Remove a source override and notify active providers.</summary>
        public void RemoveOverride(VegetationKind kind, UnityEngine.Object source)
        {
            if (_overrides != null && _overrides.RemoveAll(entry => entry != null && entry.Kind == kind && entry.Source == source) > 0)
            {
                NotifyChanged();
            }
        }

        /// <summary>Select GPU or CPU detail placement. Active providers rebuild their streams.</summary>
        public void SetGpuResidentDetails(bool enabled)
        {
            if (_gpuResidentDetails == enabled) return;
            _gpuResidentDetails = enabled;
            NotifyChanged();
        }

        /// <summary>Notify active providers after serialized editor changes.</summary>
        public void NotifyChanged()
        {
            Changed?.Invoke();
        }

        private void OnValidate()
        {
            _overrides ??= new List<OverrideEntry>();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall -= NotifyAfterValidation;
            UnityEditor.EditorApplication.delayCall += NotifyAfterValidation;
#endif
        }

#if UNITY_EDITOR
        private void NotifyAfterValidation()
        {
            UnityEditor.EditorApplication.delayCall -= NotifyAfterValidation;
            if (this) NotifyChanged();
        }
#endif

        private static void ValidateSource(VegetationKind kind, UnityEngine.Object source)
        {
            if (kind != VegetationKind.Detail && kind != VegetationKind.Tree)
                throw new ArgumentOutOfRangeException(nameof(kind));
            if (!source || (kind == VegetationKind.Tree && !(source is GameObject)) ||
                (kind == VegetationKind.Detail && !(source is GameObject) && !(source is Texture2D)))
            {
                throw new ArgumentException("Use a tree prefab, detail prefab, or detail texture.", nameof(source));
            }
        }

        private static void Validate(InstanceQualitySettings quality)
        {
            if (!float.IsFinite(quality.Density) || quality.Density < 0 || quality.Density > 1 ||
                !float.IsFinite(quality.MinimumPixels) || quality.MinimumPixels < 0 ||
                !float.IsFinite(quality.DensityFalloffDistance) || quality.DensityFalloffDistance < 0 ||
                !float.IsFinite(quality.FarDensity) || quality.FarDensity < 0 || quality.FarDensity > 1 ||
                !float.IsFinite(quality.ShadowDistance) || quality.ShadowDistance < 0 ||
                !float.IsFinite(quality.ShadowFadeDistance) || quality.ShadowFadeDistance < 0 ||
                !float.IsFinite(quality.ViewDistance) || quality.ViewDistance < 0 ||
                !float.IsFinite(quality.ViewFadeDistance) || quality.ViewFadeDistance < 0 ||
                !float.IsFinite(quality.LodBias) || quality.LodBias < 0 ||
                quality.MinimumShadowLod < 0 || quality.MinimumShadowLod > 7 ||
                quality.ShadowSplits < 0 || quality.ShadowSplits > 16 ||
                !Enum.IsDefined(typeof(InstanceShadowMode), quality.ShadowMode))
            {
                throw new ArgumentOutOfRangeException(nameof(quality));
            }
            if ((quality.Density < 1 || quality.MinimumPixels > 0 || quality.DensityFalloffDistance > 0) && !quality.Decorative)
            {
                throw new InvalidOperationException("Density and small-object reductions need decorative sources.");
            }
        }
    }
}
