using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain
{
    public sealed partial class TerrainRenderer
    {
        private readonly Dictionary<UnityEngine.Object, (string Reason, bool External)> _integrationBlocks = new();
        private string _integrationDiagnostic = string.Empty;
        private Material _materialSource;
        private Action<MaterialPropertyBlock> _bindDynamicMaterialProperties;
        private int _dynamicBindingVersion;

        /// <summary>Gets the exported surface asset. Integrations must not modify this shared asset.</summary>
        public Material SurfaceMaterial => _surfaceMaterial;
        /// <summary>Gets the surface revision used to invalidate cached pages.</summary>
        public int SurfaceRevision => _sourceVersion;
        /// <summary>Gets the completed geometry metadata revision.</summary>
        public int GeometryRevision => _metadataVersion;
        /// <summary>Checks optional integrations before terrain rendering.</summary>
        public event Action ValidateIntegrations;
        /// <summary>Binds live values for each draw. These values must not affect cached base surfaces.</summary>
        public event Action<MaterialPropertyBlock> BindDynamicMaterialProperties
        {
            add
            {
                _bindDynamicMaterialProperties += value;
                _dynamicBindingVersion++;
            }
            remove
            {
                _bindDynamicMaterialProperties -= value;
                _dynamicBindingVersion++;
            }
        }

        /// <summary>Blocks Looga rendering until an integration clears its reason.</summary>
        /// <param name="externalRenderer">True leaves terrain visibility under an external renderer's control.</param>
        public void SetIntegrationBlock(UnityEngine.Object owner, string reason, bool externalRenderer = false)
        {
            if (!owner) throw new ArgumentNullException(nameof(owner));
            if (string.IsNullOrEmpty(reason))
            {
                if (!_integrationBlocks.Remove(owner)) return;
            }
            else
            {
                if (_integrationBlocks.TryGetValue(owner, out var previous)
                    && previous.Reason == reason && previous.External == externalRenderer) return;
                _integrationBlocks[owner] = (reason, externalRenderer);
            }

            bool external = false;
            var reasons = new List<string>();
            foreach (var block in _integrationBlocks.Values)
            {
                reasons.Add(block.Reason);
                external |= block.External;
            }
            _integrationDiagnostic = string.Join("\n", reasons);
            if (reasons.Count > 0)
            {
                Release(!external);
                Diagnostic = _integrationDiagnostic;
            }
        }

        /// <summary>Checks integrations and restores eligible rendering without changing source terrain data.</summary>
        public void RefreshCompatibility()
        {
            ValidateIntegrations?.Invoke();
            if (!string.IsNullOrEmpty(_integrationDiagnostic))
            {
                Diagnostic = _integrationDiagnostic;
                return;
            }
            if (!isActiveAndEnabled) return;
            Initialize();
            if (IsReady)
            {
                // External authoring tools can restore native drawing after a terrain update.
                Source.drawHeightmap = false;
            }
        }

        /// <summary>Uses per-terrain material values without modifying the exported asset or its shader.</summary>
        public void SetMaterialSource(Material source)
        {
            _materialSource = source;
            RefreshSurface();
        }

        /// <summary>Refreshes surface bindings and cached pages without rebuilding geometry metadata.</summary>
        public void RefreshSurface()
        {
            if (_material)
            {
                _material.CopyPropertiesFromMaterial(_materialSource ? _materialSource : _surfaceMaterial);
                _material.enableInstancing = true;
                if (_surfaceMaterial.IsKeywordEnabled("_ALPHATEST_ON"))
                {
                    _material.EnableKeyword("_ALPHATEST_ON");
                }
            }
            MarkSurfaceChanged();
        }
    }
}
