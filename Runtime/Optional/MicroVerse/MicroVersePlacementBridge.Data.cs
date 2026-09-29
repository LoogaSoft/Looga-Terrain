using System;
using LoogaSoft.Instancing;
using UnityEngine;
#if LOOGA_TERRAIN_MICROVERSE_OBJECTS
using System.Collections.Generic;
using System.Diagnostics;
using JBooth.MicroVerseCore;
#endif

namespace LoogaSoft.Terrain.Integrations
{
    /// <summary>How a MicroVersePlacementBridge draws MicroVerse object outputs.</summary>
    public enum MicroVerseObjectOutputMode
    {
        /// <summary>A SceneInstanceProvider draws the spawned GameObjects.</summary>
        SceneObjects,
        /// <summary>One InstanceSet draws copies of the spawned transforms. The spawned render parts are hidden or removed.</summary>
        Data
    }

    public sealed partial class MicroVersePlacementBridge
    {
#if LOOGA_TERRAIN_MICROVERSE_OBJECTS
        [SerializeField] private ObjectStamp[] _dataStamps = Array.Empty<ObjectStamp>();
        [SerializeField, Tooltip("On: remove the spawned LOD renderers and keep each root with its colliders. " +
            "Off: only hide the spawned renderers.")]
        private bool _stripNativeParts = true;
        [SerializeField, Tooltip("Draws all Data mode placements. The bridge creates it as a child when it is empty.")]
        private InstanceSet _dataSet;
        private List<MicroVerseObjectData.Group> _dataQueue;
        private int _dataQueueIndex;

        /// <summary>Counts and timings of the last data capture, or null before the first capture.</summary>
        public MicroVerseObjectDataReport LastDataCapture { get; private set; }
        /// <summary>Set that draws the Data mode placements, or null before the first capture.</summary>
        public InstanceSet DataSet => _dataSet;

        /// <summary>
        /// Draw the outputs of these stamps from placement data after each MicroVerse update.
        /// Height synchronization is not necessary, because the capture reads only spawned transforms.
        /// </summary>
        /// <param name="stripNativeParts">True removes the spawned LOD renderers and keeps colliders. False only hides them.</param>
        public void ConfigureData(MicroVerse source, ObjectStamp[] stamps, bool stripNativeParts)
        {
            Configure(source, null, null);
            _mode = MicroVerseObjectOutputMode.Data;
            _dataStamps = stamps == null ? Array.Empty<ObjectStamp>() : (ObjectStamp[])stamps.Clone();
            _stripNativeParts = stripNativeParts;
            SetDataSetEnabled(true);
            _dataWaiting = true;
        }

        #region Data capture
        private bool ProcessData()
        {
            if (!_dataWaiting) return false;
            if (_microVerse.IsModifyingTerrain || SpawnProcessor.IsModifyingTerrain) return false;
            try
            {
                return _dataQueue == null ? BeginDataCapture() : ConfigureNextDataGroup();
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                CancelDataCapture();
                _dataWaiting = false;
                return false;
            }
        }

        // The scan runs once. Each later call writes one set entry, so GPU uploads spread over several updates.
        private bool BeginDataCapture()
        {
            long start = Stopwatch.GetTimestamp();
            List<MicroVerseObjectData.Group> groups = MicroVerseObjectData.Collect(_dataStamps, transform,
                out int nativeRoots, out int unmatchedRoots);
            if (groups.Count == 0)
            {
                // MicroVerse did not respawn a data stamp. The set keeps its saved placements.
                _dataWaiting = false;
                return false;
            }
            LastDataCapture = new MicroVerseObjectDataReport
            {
                NativeRoots = nativeRoots,
                UnmatchedRoots = unmatchedRoots,
                ScanMilliseconds = ElapsedMilliseconds(start)
            };
            Diagnostic = unmatchedRoots > 0
                ? unmatchedRoots + " spawned roots match no stamp prefab. These roots keep native rendering."
                : null;
            _dataQueue = groups;
            _dataQueueIndex = 0;
            return true;
        }

        private bool ConfigureNextDataGroup()
        {
            MicroVerseObjectData.Group group = _dataQueue[_dataQueueIndex++];
            MicroVerseObjectDataReport report = LastDataCapture;
            try
            {
                long start = Stopwatch.GetTimestamp();
                InstanceSet set = AcquireDataSet();
                long uploaded = set.Renderer?.UploadedBytes ?? 0;
                set.SetEntry(group.Stamp, group.Prefab, group.Placements);
                report.ConfigureMilliseconds += ElapsedMilliseconds(start);
                // A rebuild creates a new renderer, so a smaller total means that the count started again.
                long total = set.Renderer?.UploadedBytes ?? 0;
                report.UploadedBytes += total >= uploaded ? total - uploaded : total;
                report.Placements += group.Placements.Count;
                report.Entries++;
                start = Stopwatch.GetTimestamp();
                MicroVerseObjectData.Hide(group.Roots, _stripNativeParts);
                report.HideMilliseconds += ElapsedMilliseconds(start);
            }
            catch (Exception exception)
            {
                // This group keeps native rendering. The other groups continue.
                report.UnmatchedRoots += group.Roots.Count;
                Diagnostic = (Diagnostic == null ? "" : Diagnostic + "\n") + group.Prefab.name + ": " + exception.Message;
            }
            if (_dataQueueIndex < _dataQueue.Count) return true;
            CancelDataCapture();
            _dataWaiting = false;
#if UNITY_EDITOR
            if (!Application.isPlaying && !UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
#endif
            return true;
        }

        private InstanceSet AcquireDataSet()
        {
            if (_dataSet) return _dataSet;
            var holder = new GameObject("MicroVerse object data");
            holder.transform.SetParent(transform, false);
            _dataSet = holder.AddComponent<InstanceSet>();
            return _dataSet;
        }

        private void CancelDataCapture()
        {
            _dataQueue = null;
            _dataQueueIndex = 0;
        }

        private void SetDataSetEnabled(bool enabled)
        {
            if (_dataSet)
            {
                _dataSet.enabled = enabled;
            }
        }

        private InstanceSourceAdapterStatus DataStatus()
        {
            int owned = _dataSet && _dataSet.isActiveAndEnabled ? _dataSet.PlacementCount : 0;
            int native = LastDataCapture?.UnmatchedRoots ?? 0;
            if (_dataQueue != null)
            {
                for (int i = _dataQueueIndex; i < _dataQueue.Count; i++)
                {
                    native += _dataQueue[i].Roots.Count;
                }
            }
            return new InstanceSourceAdapterStatus(owned + native, owned, native, _dataWaiting, Diagnostic);
        }

        private static double ElapsedMilliseconds(long start)
        {
            return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }
        #endregion
#else
        private bool ProcessData()
        {
            _dataWaiting = false;
            Diagnostic = "Data mode requires the MicroVerse objects module.";
            return false;
        }

        private void CancelDataCapture()
        {
        }

        private void SetDataSetEnabled(bool enabled)
        {
        }

        private InstanceSourceAdapterStatus DataStatus()
        {
            return new InstanceSourceAdapterStatus(0, 0, 0, false, Diagnostic);
        }
#endif
    }
}
