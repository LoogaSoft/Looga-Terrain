using System;
using System.Collections.Generic;
using JBooth.MicroVerseCore;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations
{
    /// <summary>Coordinates Looga rendering with public MicroVerse updates and proxy ownership.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class MicroVerseTerrainBridge : MonoBehaviour
    {
        [SerializeField] private MicroVerse _microVerse;
        [SerializeField] private TerrainRenderer[] _renderers = Array.Empty<TerrainRenderer>();
        [SerializeField] private bool _discoverSourceTerrains = true;
        private readonly Dictionary<TerrainRenderer, Action> _checks = new();
        private readonly HashSet<TerrainRenderer> _next = new();
        private readonly List<TerrainRenderer> _removed = new();
        private bool _wasSynchronized;

        /// <summary>Reports CPU height synchronization separately from generation completion.</summary>
        public bool IsHeightSynchronized => _microVerse && _microVerse.IsHeightSyncd;
        /// <summary>Gets the current proxy compatibility message.</summary>
        public string Diagnostic { get; private set; } = string.Empty;

        #region Built-in
        private void OnEnable()
        {
            if (!_microVerse)
            {
                _microVerse = GetComponentInParent<MicroVerse>();
            }
            MicroVerse.OnBeginUpdating.AddListener(RefreshRenderers);
            MicroVerse.OnFinishedUpdating.AddListener(RefreshRenderers);
            MicroVerse.OnCancelUpdating.AddListener(RefreshRenderers);
            MicroVerse.OnTerrainLayersChanged += OnLayersChanged;
            RefreshRenderers();
        }

        private void OnDisable()
        {
            MicroVerse.OnBeginUpdating.RemoveListener(RefreshRenderers);
            MicroVerse.OnFinishedUpdating.RemoveListener(RefreshRenderers);
            MicroVerse.OnCancelUpdating.RemoveListener(RefreshRenderers);
            MicroVerse.OnTerrainLayersChanged -= OnLayersChanged;
            foreach (var pair in _checks)
            {
                Untrack(pair.Key, pair.Value);
            }
            _checks.Clear();
        }

        private void LateUpdate()
        {
            RefreshMembership();
            foreach (TerrainRenderer renderer in _checks.Keys)
            {
                UpdateGuard(renderer);
            }
            bool synchronized = IsHeightSynchronized;
            if (synchronized && !_wasSynchronized)
            {
                RefreshRenderers();
            }
            _wasSynchronized = synchronized;
        }
        #endregion

        /// <summary>Assigns an authoring source and optional explicit targets without requesting terrain generation.</summary>
        public void Configure(MicroVerse source, TerrainRenderer[] renderers, bool discoverSourceTerrains = true)
        {
            _microVerse = source;
            _renderers = renderers ?? Array.Empty<TerrainRenderer>();
            _discoverSourceTerrains = discoverSourceTerrains;
            RefreshRenderers();
        }

        /// <summary>Refreshes current terrain outputs. This does not request saveback or change synchronization state.</summary>
        public void RefreshRenderers()
        {
            if (!isActiveAndEnabled) return;
            RefreshMembership();
            foreach (TerrainRenderer renderer in _checks.Keys)
            {
                UpdateGuard(renderer);
                renderer.RefreshSource();
            }
        }

        private void OnLayersChanged(TerrainLayer[] layers)
        {
            RefreshRenderers();
        }

        private void RefreshMembership()
        {
            _next.Clear();
            foreach (TerrainRenderer renderer in _renderers)
            {
                if (renderer)
                {
                    _next.Add(renderer);
                }
            }
            if (_discoverSourceTerrains && _microVerse)
            {
                UnityEngine.Terrain[] terrains = _microVerse.terrains;
                if (terrains == null)
                {
                    terrains = _microVerse.GetComponentsInChildren<UnityEngine.Terrain>(true);
                }
                foreach (UnityEngine.Terrain terrain in terrains)
                {
                    if (terrain && terrain.TryGetComponent(out TerrainRenderer renderer))
                    {
                        _next.Add(renderer);
                    }
                }
            }
            _removed.Clear();
            foreach (var pair in _checks)
            {
                if (!_next.Contains(pair.Key))
                {
                    Untrack(pair.Key, pair.Value);
                    _removed.Add(pair.Key);
                }
            }
            foreach (TerrainRenderer renderer in _removed)
            {
                _checks.Remove(renderer);
            }
            foreach (TerrainRenderer renderer in _next)
            {
                if (_checks.ContainsKey(renderer)) continue;
                Action check = () => UpdateGuard(renderer);
                _checks.Add(renderer, check);
                renderer.ValidateIntegrations += check;
            }
        }

        private void UpdateGuard(TerrainRenderer renderer)
        {
            bool external = false;
            Diagnostic = string.Empty;
#if UNITY_EDITOR && __MICROSPLAT__ && __MICROSPLAT_TESSELLATION__
            if (_microVerse && (_microVerse.IsUsingProxyRenderer
                || _microVerse.proxyRenderMode != MicroVerse.ProxyRenderMode.AlwaysUnity))
            {
                external = _microVerse.IsUsingProxyRenderer;
                Diagnostic = "MicroVerse proxy mode owns authoring rendering. Select AlwaysUnity to use Looga.";
            }
#endif
            renderer.SetIntegrationBlock(this, Diagnostic, external);
        }

        private void Untrack(TerrainRenderer renderer, Action check)
        {
            if (!renderer) return;
            renderer.ValidateIntegrations -= check;
            renderer.SetIntegrationBlock(this, null);
        }
    }
}
