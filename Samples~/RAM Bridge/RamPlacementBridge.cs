using LoogaSoft.Instancing;
using System;
using LoogaSoft.Terrain.Instances;
using NatureManufacture.RAM;
using UnityEngine;
using UnityEngine.Events;

namespace LoogaSoft.Terrain.Integrations
{
    /// <summary>Connects public RAM generation events to an explicit placement mask.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class RamPlacementBridge : MonoBehaviour
    {
        [SerializeField] private RamSpline _river;
        [SerializeField] private LakePolygon _lake;
        [SerializeField] private PlacementExclusion _mask;
        [SerializeField, Min(0)] private float _belowWater = 3;
        [SerializeField, Min(0)] private float _aboveWater = 1;
        private UnityEvent _finished;
        private bool _dirty = true;
        private bool _hadSource;
        /// <summary>Last geometry failure. RAM water remains unchanged.</summary>
        public string Diagnostic { get; private set; }

        private void OnEnable()
        {
            _dirty = true;
            Subscribe();
        }
        private void OnDisable()
        {
            _finished?.RemoveListener(GenerationFinished);
            _finished = null;
            if (_mask)
            {
                _mask.Clear();
            }
        }
        private void OnValidate() => _dirty = true;
        private void LateUpdate() => ProcessChanges();

        /// <summary>Apply a queued generation event after RAM releases its source mesh.</summary>
        public void ProcessChanges()
        {
            if (!isActiveAndEnabled || (!_dirty && !(_hadSource && !_river && !_lake))) return;
            Subscribe();
            RefreshMask();
        }

        /// <summary>Assign one river or lake and a dedicated Looga mask. This does not generate water or edit terrain.</summary>
        public void Configure(RamSpline river, LakePolygon lake, PlacementExclusion mask, float belowWater = 3, float aboveWater = 1)
        {
            if ((!river && !lake) || (river && lake) || !mask || !float.IsFinite(belowWater) || belowWater < 0 ||
                !float.IsFinite(aboveWater) || aboveWater < 0)
            {
                throw new ArgumentException("Assign exactly one RAM source, a mask and finite nonnegative height distances.");
            }
            if (_mask && _mask != mask)
            {
                _mask.Clear();
            }
            _river = river;
            _lake = lake;
            _mask = mask;
            _belowWater = belowWater;
            _aboveWater = aboveWater;
            Subscribe();
            RefreshMask();
        }

        private void Subscribe()
        {
            _finished?.RemoveListener(GenerationFinished);
            _finished = null;
            if (!isActiveAndEnabled) return;
            // RAM exposes these mutable UnityEvents as its generation contract.
            if (_river)
            {
                _river.OnGenerationEnded ??= new UnityEvent();
                _finished = _river.OnGenerationEnded;
            }
            else if (_lake)
            {
                _lake.OnGenerationEnded ??= new UnityEvent();
                _finished = _lake.OnGenerationEnded;
            }
            _finished?.AddListener(GenerationFinished);
        }

        private void GenerationFinished() => _dirty = true;

        /// <summary>Recapture the generated mesh after RAM finishes. Existing source meshes remain unchanged.</summary>
        public void RefreshMask()
        {
            _dirty = false;
            _hadSource = _river || _lake;
            if (!_mask || !isActiveAndEnabled) return;
            try
            {
                MeshFilter source = _river ? _river.MainMeshFilter : _lake ? _lake.MainMeshFilter : null;
                if (!source)
                {
                    throw new InvalidOperationException("Generate the RAM river or lake before capturing its placement mask.");
                }
                _mask.ConfigureSurface(source, _belowWater, _aboveWater);
                Diagnostic = null;
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                _mask.Clear();
            }
        }
    }
}
