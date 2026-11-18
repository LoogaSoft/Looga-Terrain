using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace LoogaSoft.Terrain.Barriers
{
    /// <summary>
    /// Draws the generated pieces of all barrier runs in the scene through Looga Instancing. The pieces keep their
    /// colliders. While a run builds again, all pieces draw natively. When the edits stop, the component registers
    /// the pieces again.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SceneInstanceProvider))]
    public sealed class LoogaBarrierInstancing : MonoBehaviour
    {
        [Tooltip("Largest draw distance of the pieces, in meters.")]
        [Min(10f)]
        [SerializeField] private float _maxDistance = 800f;
        [Tooltip("Seconds without a run build before the pieces register again. Registration of 4000 pieces takes " +
                 "about one second.")]
        [Min(0f)]
        [SerializeField] private float _settleSeconds = 1f;

        private readonly List<GameObject> _sources = new();
        private SceneInstanceProvider _provider;
        private bool _registerRequested;
        private double _lastChange;

        /// <summary>Number of pieces that draw through Looga Instancing.</summary>
        public int InstancedCount => _provider ? _provider.ActiveSourceCount : 0;
        /// <summary>Reasons why pieces stayed on native rendering, or null.</summary>
        public string Diagnostic => _provider ? _provider.Diagnostic : null;

        #region Built-in
        private void OnEnable()
        {
            _provider = GetComponent<SceneInstanceProvider>();
            // The runs report their changes, so the provider does not need to check every piece each frame.
            _provider.TrackSourceChanges = false;
            LoogaBarrierRun.Rebuilding += OnRunChanging;
            LoogaBarrierRun.Rebuilt += OnRunChanged;
            Register(false);
        }

        private void OnDisable()
        {
            LoogaBarrierRun.Rebuilding -= OnRunChanging;
            LoogaBarrierRun.Rebuilt -= OnRunChanged;
            if (_provider)
            {
                _provider.Configure(Array.Empty<GameObject>(), null, _maxDistance);
            }
        }

        private void Update()
        {
            if (!_registerRequested)
                return;

            if (Time.realtimeSinceStartupAsDouble - _lastChange >= _settleSeconds)
            {
                Register(true);
            }
#if UNITY_EDITOR
            else if (!Application.isPlaying)
            {
                // The editor updates only on a change, so the component asks for the next update itself.
                EditorApplication.QueuePlayerLoopUpdate();
            }
#endif
        }
        #endregion

        #region Registration
        /// <summary>Registers the pieces of all runs in the scene again.</summary>
        /// <param name="force">False skips the registration when the provider already holds the same number of pieces.</param>
        public void Register(bool force)
        {
            _registerRequested = false;
            CollectSources();
            if (!force && _provider.ActiveSourceCount == _sources.Count && _sources.Count > 0)
                return;

            _provider.Configure(_sources.ToArray(), null, _maxDistance);
        }

        private void CollectSources()
        {
            _sources.Clear();
            foreach (LoogaBarrierRun run in FindObjectsByType<LoogaBarrierRun>(FindObjectsSortMode.None))
            {
                Transform generated = run.Generated;
                if (!generated)
                {
                    continue;
                }

                foreach (Transform piece in generated)
                {
                    _sources.Add(piece.gameObject);
                }
            }
        }

        // Native rendering takes over before the run removes its pieces, so a removed piece never stays visible.
        private void OnRunChanging(LoogaBarrierRun run)
        {
            if (_provider && _provider.ActiveSourceCount > 0)
            {
                _provider.Configure(Array.Empty<GameObject>(), null, _maxDistance);
            }
            OnRunChanged(run);
        }

        private void OnRunChanged(LoogaBarrierRun run)
        {
            _registerRequested = true;
            _lastChange = Time.realtimeSinceStartupAsDouble;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorApplication.QueuePlayerLoopUpdate();
            }
#endif
        }
        #endregion
    }
}
