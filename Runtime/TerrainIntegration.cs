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
        // A surface material that a component supplies at run time, for example a Looga terrain surface binding. It
        // replaces the exported asset and is not saved.
        private Material _surfaceOverride;
        private UnityEngine.Object _surfaceOverrideOwner;
        private Action<MaterialPropertyBlock> _bindDynamicMaterialProperties;
        private int _dynamicBindingVersion;

        /// <summary>Gets the exported surface asset. Integrations must not modify this shared asset.</summary>
        public Material SurfaceMaterial => _surfaceMaterial;
        /// <summary>
        /// True while a component supplies the surface material (<see cref="SetSurfaceOverride"/>). Integrations of
        /// other surface systems, such as the MicroSplat bridge, stand down.
        /// </summary>
        public bool HasSurfaceOverride => _surfaceOverride;
        // The material that the renderer draws: the override, or the exported asset.
        internal Material ActiveSurfaceMaterial => _surfaceOverride ? _surfaceOverride : _surfaceMaterial;
        /// <summary>True while an optional integration blocks Looga rendering. The integration owns the reason.</summary>
        public bool HasIntegrationBlock => _integrationBlocks.Count > 0;
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
                bool external = false;
                foreach (var block in _integrationBlocks.Values)
                {
                    external |= block.External;
                }
                if (!external)
                {
                    RestoreCapturedVisibility();
                }
                return;
            }
            // A domain reload calls OnDisable while isActiveAndEnabled stays true. Another component's OnDisable can then
            // call back into this renderer, and new GPU resources would never be released. Active holds enabled renderers.
            if (!isActiveAndEnabled || !Active.Contains(this)) return;
            Initialize();
            if (IsReady)
            {
                // External authoring tools can restore native drawing after a terrain update.
                Source.drawHeightmap = false;
            }
            else
            {
                RestoreCapturedVisibility();
            }
        }

        /// <summary>
        /// Draws with a surface material of a component instead of the exported asset, and builds the GPU resources
        /// again. The material must have the _LoogaTerrainSurface property. A null material removes the override of
        /// this owner. The override is not saved.
        /// </summary>
        public void SetSurfaceOverride(UnityEngine.Object owner, Material surface)
        {
            if (!owner) throw new ArgumentNullException(nameof(owner));
            if (!surface)
            {
                if (_surfaceOverrideOwner != owner) return;
                _surfaceOverride = null;
                _surfaceOverrideOwner = null;
            }
            else
            {
                if (_surfaceOverride == surface && _surfaceOverrideOwner == owner) return;
                _surfaceOverride = surface;
                _surfaceOverrideOwner = owner;
            }
            Release();
            _initializationFailed = false;
            if (isActiveAndEnabled)
            {
                RefreshCompatibility();
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
                // An override owns all its values. Otherwise per-terrain values come from the material source.
                Material source = _surfaceOverride ? _surfaceOverride : _materialSource ? _materialSource : _surfaceMaterial;
                _material.CopyPropertiesFromMaterial(source);
                if (_surfaceOverride)
                {
                    // The override shares the shader of the drawn material, so its feature keywords apply as they are.
                    _material.shaderKeywords = source.shaderKeywords;
                }
                _material.enableInstancing = true;
                // The clip variant stops early depth writes, so hidden terrain costs full shading. Only terrains with
                // holes use it.
                if (_hasHoles || ActiveSurfaceMaterial.IsKeywordEnabled("_ALPHATEST_ON"))
                {
                    _material.EnableKeyword("_ALPHATEST_ON");
                }
                else
                {
                    _material.DisableKeyword("_ALPHATEST_ON");
                }
            }
            MarkSurfaceChanged();
        }
    }
}
