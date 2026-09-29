using LoogaSoft.Instancing;
using System;
using System.Collections.Generic;
using JBooth.MicroVerseCore;
using LoogaSoft.Terrain.Instances;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations
{
    /// <summary>Refreshes public object and road outputs after MicroVerse synchronization. It never initiates a bake.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed partial class MicroVersePlacementBridge : MonoBehaviour, IInstanceSourceAdapter
    {
        [SerializeField] private MicroVerse _microVerse;
        [SerializeField, Tooltip("Scene Objects: a SceneInstanceProvider draws the spawned objects. " +
            "Data: one InstanceSet draws copies of the spawned transforms.")]
        private MicroVerseObjectOutputMode _mode = MicroVerseObjectOutputMode.SceneObjects;
        [SerializeField] private Transform[] _objectOutputs = Array.Empty<Transform>();
        [SerializeField] private SceneInstanceProvider _objects;
        [SerializeField] private PlacementExclusion[] _surfaceMasks = Array.Empty<PlacementExclusion>();
        [SerializeField] private MeshScatterAuthoring[] _scatter = Array.Empty<MeshScatterAuthoring>();
        private bool _waiting;
        // MicroVerse starts each session with IsHeightSyncd false and nothing to sync.
        // The bridge requires height synchronization only after it sees an update start.
        private bool _isSyncPending;
        // Data mode waits only for MicroVerse to stop modifying and spawning.
        private bool _dataWaiting;
        private readonly List<GameObject> _roots = new();
        private readonly HashSet<GameObject> _seen = new();

        /// <summary>True while public terrain synchronization prevents output capture.</summary>
        public bool IsWaiting => _waiting;
        /// <summary>How the bridge draws the object outputs.</summary>
        public MicroVerseObjectOutputMode Mode => _mode;
        /// <summary>Last capture failure. Native objects retain their renderers.</summary>
        public string Diagnostic { get; private set; }
        /// <summary>Current ownership of MicroVerse object outputs. Waiting outputs remain native.</summary>
        public InstanceSourceAdapterStatus SourceStatus
        {
            get
            {
                if (_mode == MicroVerseObjectOutputMode.Data) return DataStatus();
                if (_objects && !_waiting)
                {
                    InstanceSourceAdapterStatus status = _objects.SourceStatus;
                    return new InstanceSourceAdapterStatus(status.RequestedSources, status.LoogaOwnedSources,
                        status.NativeFallbackSources, false, Diagnostic ?? status.Diagnostic);
                }
                int requested = CountAvailableOutputs();
                return new InstanceSourceAdapterStatus(requested, 0, requested, _waiting, Diagnostic);
            }
        }

        private void OnEnable()
        {
            MicroVerse.OnBeginUpdating.AddListener(Begin);
            MicroVerse.OnFinishedUpdating.AddListener(Finish);
            MicroVerse.OnCancelUpdating.AddListener(Finish);
            _waiting = true;
            _dataWaiting = true;
        }
        private void OnDisable()
        {
            MicroVerse.OnBeginUpdating.RemoveListener(Begin);
            MicroVerse.OnFinishedUpdating.RemoveListener(Finish);
            MicroVerse.OnCancelUpdating.RemoveListener(Finish);
            CancelDataCapture();
            if (_objects)
            {
                _objects.Configure(null);
            }
        }
        private void LateUpdate() => ProcessChanges();

        /// <summary>
        /// Assign dedicated Looga receivers and explicit public output roots. This selects Scene Objects mode and disables
        /// the Data mode set. Outputs stripped in Data mode stay invisible until MicroVerse respawns them.
        /// </summary>
        public void Configure(MicroVerse source, SceneInstanceProvider objects, Transform[] outputs,
            PlacementExclusion[] masks = null, MeshScatterAuthoring[] scatter = null)
        {
            if (_objects)
            {
                _objects.Configure(null);
            }
            if (source != _microVerse)
            {
                _isSyncPending = false;
            }
            _microVerse = source;
            _objects = objects;
            _objectOutputs = outputs == null ? Array.Empty<Transform>() : (Transform[])outputs.Clone();
            _surfaceMasks = masks == null ? Array.Empty<PlacementExclusion>() : (PlacementExclusion[])masks.Clone();
            _scatter = scatter == null ? Array.Empty<MeshScatterAuthoring>() : (MeshScatterAuthoring[])scatter.Clone();
            _mode = MicroVerseObjectOutputMode.SceneObjects;
            CancelDataCapture();
            SetDataSetEnabled(false);
            _waiting = true;
        }

        /// <summary>Request a refresh after Undo, import, tile replacement or an external object edit.</summary>
        public void RequestRefresh()
        {
            _waiting = true;
            _dataWaiting = true;
        }

        /// <summary>
        /// Capture when MicroVerse is idle. After an observed update, capture only after height synchronization.
        /// </summary>
        public bool ProcessChanges()
        {
            if (!isActiveAndEnabled) return false;
            if (!_microVerse)
            {
                if (_objects && _objects.ActiveSourceCount > 0)
                {
                    _objects.Configure(null);
                }
                _waiting = false;
                Diagnostic = "The MicroVerse source is unavailable. Native object rendering remains active.";
                return false;
            }
            bool isDataMode = _mode == MicroVerseObjectOutputMode.Data;
            if (!_waiting && !(isDataMode && _dataWaiting)) return false;
            if (_microVerse.IsModifyingTerrain)
            {
                // This also detects an update that started before the bridge was enabled.
                _isSyncPending = true;
                return false;
            }
            bool changed = isDataMode && ProcessData();
            // Masks, scatter and scene objects read terrain heights, so they wait for height synchronization.
            if (!_waiting) return changed;
            if (_isSyncPending && !_microVerse.IsHeightSyncd) return changed;
            _isSyncPending = false;
            try
            {
                foreach (var mask in _surfaceMasks)
                {
                    if (mask)
                    {
                        mask.Refresh();
                    }
                }
                foreach (var author in _scatter)
                {
                    if (author)
                    {
                        // Mask events contain the changed region. Mesh edits use InvalidateSurface separately.
                        author.ProcessExclusionChanges();
                    }
                }
                _roots.Clear();
                _seen.Clear();
                foreach (var output in _objectOutputs)
                {
                    if (!output)
                    {
                        continue;
                    }
                    foreach (var mesh in output.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        var group = mesh.GetComponentInParent<LODGroup>();
                        GameObject root = group && group.transform.IsChildOf(output) ? group.gameObject : mesh.gameObject;
                        if (_seen.Add(root))
                        {
                            _roots.Add(root);
                        }
                    }
                }
                if (_objects)
                {
                    _objects.Configure(_roots.ToArray());
                }
                Diagnostic = _objects ? _objects.Diagnostic : null;
                _waiting = false;
                return true;
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                _waiting = false;
                if (_objects)
                {
                    _objects.Configure(null);
                }
                return false;
            }
        }

        private void Begin()
        {
            if (!_microVerse) return;
            _isSyncPending = true;
            _waiting = true;
            _dataWaiting = true;
            CancelDataCapture();
            if (_objects)
            {
                _objects.Configure(null);
            }
        }
        private void Finish()
        {
            _waiting = true;
            _dataWaiting = true;
        }

        private int CountAvailableOutputs()
        {
            _seen.Clear();
            foreach (Transform output in _objectOutputs)
            {
                if (!output) continue;
                foreach (MeshRenderer mesh in output.GetComponentsInChildren<MeshRenderer>(true))
                {
                    LODGroup group = mesh.GetComponentInParent<LODGroup>();
                    GameObject root = group && group.transform.IsChildOf(output) ? group.gameObject : mesh.gameObject;
                    _seen.Add(root);
                }
            }
            return _seen.Count;
        }
    }
}
