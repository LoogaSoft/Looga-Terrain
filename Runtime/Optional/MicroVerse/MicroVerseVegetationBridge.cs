using LoogaSoft.Instancing;
using System;
using JBooth.MicroVerseCore;
using LoogaSoft.Terrain.Instances;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations
{
    /// <summary>Suspends explicit vegetation providers while MicroVerse changes their source terrain.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class MicroVerseVegetationBridge : MonoBehaviour, IInstanceSourceAdapter
    {
        [SerializeField] private MicroVerse _microVerse;
        [SerializeField] private TerrainVegetationProvider[] _providers = Array.Empty<TerrainVegetationProvider>();
        private bool _waiting;

        /// <summary>Aggregate terrain vegetation ownership across every bridged TerrainData source.</summary>
        public InstanceSourceAdapterStatus SourceStatus
        {
            get
            {
                var result = new InstanceSourceAdapterStatus(0, 0, 0, _waiting, null);
                foreach (TerrainVegetationProvider provider in _providers)
                {
                    if (provider)
                    {
                        result = InstanceSourceAdapterStatus.Combine(result, provider.SourceStatus);
                    }
                }
                return result;
            }
        }

        #region Built-in
        private void OnEnable()
        {
            if (!_microVerse)
            {
                _microVerse = GetComponentInParent<MicroVerse>();
            }
            MicroVerse.OnBeginUpdating.AddListener(BeginUpdate);
            MicroVerse.OnFinishedUpdating.AddListener(FinishUpdate);
            MicroVerse.OnCancelUpdating.AddListener(FinishUpdate);
            if (_microVerse && _microVerse.IsModifyingTerrain)
            {
                BeginUpdate();
            }
        }

        private void OnDisable()
        {
            MicroVerse.OnBeginUpdating.RemoveListener(BeginUpdate);
            MicroVerse.OnFinishedUpdating.RemoveListener(FinishUpdate);
            MicroVerse.OnCancelUpdating.RemoveListener(FinishUpdate);
            SetBlock(null);
        }

        private void LateUpdate()
        {
            ProcessChanges();
        }
        #endregion

        /// <summary>
        /// Resume providers after MicroVerse finishes modifying and spawning. The providers wait for CPU heights
        /// themselves, because Unity reports each unsynchronized heightmap change to them.
        /// </summary>
        /// <remarks>
        /// MicroVerse 1.7.0 synchronizes heights in its first update but does not set IsHeightSyncd.
        /// A wait for that flag kept native vegetation active after each scene load.
        /// </remarks>
        public void ProcessChanges()
        {
            if (!_waiting || !_microVerse || _microVerse.IsModifyingTerrain || SpawnProcessor.IsModifyingTerrain) return;
            _waiting = false;
            SetBlock(null);
        }

        /// <summary>Assign explicit targets. This method does not generate terrain or change synchronization state.</summary>
        public void Configure(MicroVerse source, TerrainVegetationProvider[] providers)
        {
            SetBlock(null);
            _waiting = false;
            _microVerse = source;
            _providers = providers == null ? Array.Empty<TerrainVegetationProvider>() : (TerrainVegetationProvider[])providers.Clone();
            if (_microVerse && _microVerse.IsModifyingTerrain)
            {
                BeginUpdate();
            }
        }

        private void BeginUpdate()
        {
            if (!_microVerse) return;
            _waiting = true;
            SetBlock("MicroVerse is updating vegetation sources. Native rendering remains active until the update finishes.");
        }

        private void FinishUpdate()
        {
            BeginUpdate();
        }

        private void SetBlock(string reason)
        {
            foreach (TerrainVegetationProvider provider in _providers)
            {
                if (provider)
                {
                    provider.SetIntegrationBlock(this, reason);
                }
            }
        }
    }
}
