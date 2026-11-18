using System;
using System.Collections.Generic;
using LoogaSoft.Terrain.Geology;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Connects water bodies and channels into one flow network and solves the flow at each channel point. Bodies are
    /// <see cref="LoogaWaterBody"/> areas. Channels are <see cref="LoogaWaterBody"/> rivers, which flow from the first
    /// knot to the last knot. The water heights of the bodies and channels are the authority. The network does not
    /// change the surface shapes. It gives the bodies one flow field across the joints: the solved channel speeds, a
    /// jet where a channel flows into a body, a pull where a body drains into a channel, and the upstream foam.
    /// A channel end that drops freely into lower water is a fall: the network makes its fall sheet and a plunge in
    /// the water below. A <see cref="LoogaWaterFall"/> component can shape one fall. The network solves again when a
    /// body, a fall component or a setting changes.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Water Network")]
    public sealed class LoogaWaterNetwork : MonoBehaviour
    {
        private static readonly List<LoogaWaterNetwork> Networks = new();

        [Header("Elements")]
        [Tooltip("More objects whose enabled child water bodies are in the network. The enabled water bodies under " +
                 "this object are always in it.")]
        [SerializeField] private List<Transform> _extraRoots = new();
        [Tooltip("Objects whose child water bodies are not in the network, for example a group of small still pools.")]
        [SerializeField] private List<Transform> _excludedRoots = new();

        [Header("Solve")]
        [SerializeField] private WaterNetworkSettings _settings = WaterNetworkSettings.Default;

        [Header("Falls")]
        [Tooltip("Looga/Water material with Fall Sheet on for the falls that the network generates. Empty = the " +
                 "network generates no fall sheets. A Looga Water Fall component sets its own material.")]
        [SerializeField] private Material _fallMaterial;
        [Tooltip("Mesh settings of the generated falls, in metres.")]
        [SerializeField] private WaterFallSettings _fallSettings = new();

        private readonly WaterNetworkGraph _graph = new();
        private readonly List<LoogaWaterBody> _bodies = new();
        private readonly List<LoogaWaterBody> _found = new();
        // Fall components of channels in this network, and the falls that the network generated.
        private readonly List<LoogaWaterFall> _fallOverrides = new();
        private readonly List<LoogaWaterFall> _generatedFalls = new();
        private readonly List<LoogaWaterFall> _shownFalls = new();
        // Flow inputs of each body at the last notification, by instance ID.
        private readonly Dictionary<int, Hash128> _bodyFlows = new();
        private Hash128 _solvedInputs;
        private bool _isSolved;
        private bool _solveQueued;
        // Frame of the last solve in play mode.
        private int _solvedFrame = -1;
#if UNITY_EDITOR
        // Editor updates since the script load, and the update of the last solve. See BuildGraph.
        private static int _editorUpdates;
        [NonSerialized] private int _solvedUpdate = -1;
#endif

        public WaterNetworkSettings Settings => _settings;
        public IReadOnlyList<Transform> ExtraRoots => _extraRoots;
        public IReadOnlyList<Transform> ExcludedRoots => _excludedRoots;
        public Material FallMaterial => _fallMaterial;
        public WaterFallSettings FallSettings => _fallSettings;

        /// <summary>The network of the last solve, with no check for changes.</summary>
        internal WaterNetworkGraph SolvedGraph => _graph;

        /// <summary>True when a solve waits for the next editor update or frame.</summary>
        internal bool SolveQueued => _solveQueued;

        /// <summary>Number of solves since the network was loaded. It changes after each solve.</summary>
        internal int SolveCount { get; private set; }

        /// <summary>The solved network. The network solves again first when a body or a setting changed.</summary>
        public WaterNetworkGraph Graph
        {
            get
            {
                SolveIfChanged();
                return _graph;
            }
        }

        /// <summary>
        /// The solved network for a body build. In edit mode a body build solves only when no solve ran in this editor
        /// update and the first solve after OnEnable is not waiting. Otherwise it reads the last solve (empty before
        /// the first solve) and a solve is queued. For example, the bodies of a scene load build before all bodies are
        /// enabled, and the queued solve then runs once with all of them. The solve updates the flow of each body whose
        /// network data changed (NotifyBodies), as in play mode. The queued solve runs only while the editor has focus.
        /// </summary>
        internal WaterNetworkGraph BuildGraph
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying && (_isSolved ? _solvedUpdate == _editorUpdates : _solveQueued))
                {
                    if (!_solveQueued) QueueSolve();
                    return _graph;
                }
#endif
                return Graph;
            }
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void CountEditorUpdates()
        {
            UnityEditor.EditorApplication.update += () => _editorUpdates++;
        }
#endif

        #region Built-in
        private void OnEnable()
        {
            Networks.Add(this);
            LoogaWaterFlow.Changed += OnFlowZoneChanged;
            _bodyFlows.Clear();
            DestroyGeneratedFalls();
            TakeReloadState();
            QueueSolve();
        }

        private void OnDisable()
        {
            Networks.Remove(this);
            LoogaWaterFlow.Changed -= OnFlowZoneChanged;
            // Generated falls are not saved. The network makes them again after a script reload.
            DestroyGeneratedFalls();
            foreach (LoogaWaterFall fall in _fallOverrides)
            {
                if (fall) fall.Hide();
            }
            if (GeneratedReloadHandover.Unloading)
            {
                StoreReloadState();
                return;
            }

            // The bodies go back to their own flow.
            foreach (LoogaWaterBody body in _bodies)
            {
                if (body) body.QueueNetworkFlow();
            }
            _bodyFlows.Clear();
        }

        private void OnValidate()
        {
            _isSolved = false;
            if (isActiveAndEnabled) QueueSolve();
        }

        private void Update()
        {
            if (_solveQueued) SolveIfChanged();
        }
        #endregion

        #region Solve
        /// <summary>Solves the network now.</summary>
        [ContextMenu("Solve")]
        public void Solve()
        {
            _isSolved = false;
            SolveIfChanged();
        }

        /// <summary>Solves the network when a body or a setting changed. Returns true when it solved.</summary>
        public bool SolveIfChanged()
        {
            // In play mode the network solves at most once in a frame. The scene load builds all bodies in one frame,
            // and each body build changes the inputs, so each body would solve the whole network again. The next
            // frame solves once with all changes.
            if (Application.isPlaying && _isSolved && _solvedFrame == Time.frameCount)
            {
                _solveQueued = true;
                return false;
            }
            _solveQueued = false;
            GatherBodies();
            Hash128 inputs = GetInputs();
            if (_isSolved && inputs == _solvedInputs) return false;

            _solvedInputs = inputs;
            _graph.Build(_bodies, _fallOverrides, _settings, _fallSettings);
            _isSolved = true;
            _solvedFrame = Time.frameCount;
#if UNITY_EDITOR
            _solvedUpdate = _editorUpdates;
#endif
            NotifyBodies();
            NotifyFalls();
            SolveCount++;
            return true;
        }

        /// <summary>Queues a solve. The network solves after the current editor update or in the next frame.</summary>
        internal void QueueSolve()
        {
            _solveQueued = true;
#if UNITY_EDITOR
            if (Application.isPlaying) return;

            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this && isActiveAndEnabled && _solveQueued) SolveIfChanged();
            };
#endif
        }

        private void GatherBodies()
        {
            _bodies.Clear();
            AddBodies(transform);
            foreach (Transform root in _extraRoots)
            {
                if (root) AddBodies(root);
            }
            _fallOverrides.Clear();
            foreach (LoogaWaterFall fall in LoogaWaterFall.Active)
            {
                if (!fall || fall.IsGenerated) continue;

                LoogaWaterBody member = fall.Source == LoogaWaterFall.FallSource.BodyCrest ? fall.Body : fall.Channel;
                if (member && _bodies.Contains(member)) _fallOverrides.Add(fall);
            }
        }

        private void AddBodies(Transform root)
        {
            root.GetComponentsInChildren(false, _found);
            foreach (LoogaWaterBody body in _found)
            {
                if (body.isActiveAndEnabled && !IsExcluded(body) && !_bodies.Contains(body)) _bodies.Add(body);
            }
        }

        private bool IsExcluded(LoogaWaterBody body)
        {
            foreach (Transform root in _excludedRoots)
            {
                if (root && body.transform.IsChildOf(root)) return true;
            }
            return false;
        }

        // The body revision changes with each surface build and each inspector change. The knots and the matrices
        // find a spline or level change before the queued surface build runs.
        private Hash128 GetInputs()
        {
            var hash = new Hash128();
            _settings.AppendTo(ref hash);
            _fallSettings.AppendTo(ref hash);
            hash.Append(_fallOverrides.Count);
            foreach (LoogaWaterFall fall in _fallOverrides)
            {
                fall.AppendTo(ref hash);
            }
            hash.Append(_bodies.Count);
            foreach (LoogaWaterBody body in _bodies)
            {
                hash.Append(body.GetInstanceID());
                hash.Append(body.Revision);
                Matrix4x4 bodyMatrix = body.transform.localToWorldMatrix;
                hash.Append(ref bodyMatrix);
                if (!body.Spline) continue;

                Matrix4x4 splineMatrix = body.Spline.transform.localToWorldMatrix;
                hash.Append(ref splineMatrix);
                WaterSplines.AppendKnots(ref hash, body.Spline);
            }
            return hash;
        }
        #endregion

        #region Bodies
        /// <summary>The enabled network that holds the body, or null.</summary>
        internal static LoogaWaterNetwork Find(LoogaWaterBody body)
        {
            foreach (LoogaWaterNetwork network in Networks)
            {
                if (network && network.IsMember(body)) return network;
            }
            return null;
        }

        private bool IsMember(LoogaWaterBody body)
        {
            if (!body || IsExcluded(body)) return false;
            if (body.transform.IsChildOf(transform)) return true;

            foreach (Transform root in _extraRoots)
            {
                if (root && body.transform.IsChildOf(root)) return true;
            }
            return false;
        }

        // Queues a flow update for each body whose flow inputs changed. A solve that changes nothing in the flow of a
        // body, for example after its own surface build, does not update it.
        private void NotifyBodies()
        {
            foreach (LoogaWaterBody body in _bodies)
            {
                Hash128 flow = GetBodyFlow(body);
                int id = body.GetInstanceID();
                // The data of the last flow write from this network is exact: in edit mode a body can write its flow
                // from an older solve (BuildGraph). A body that built before this network was enabled, for example
                // after a script reload, has no network flow, whatever the stored state says. A body that loaded its
                // shipped surface has the flow of the network in the editor at the player build.
                bool known = body.TryGetWrittenFlow(this, out Hash128 previous) ||
                    _bodyFlows.TryGetValue(id, out previous) || body.TryGetShippedFlow(out previous);
                bool changed = !known || previous != flow || !body.HasNetworkFlow;
                _bodyFlows[id] = flow;
                if (changed) body.QueueNetworkFlow();
            }
        }

        /// <summary>The network data that the flow of a body reads: its joints and the solved speeds of its channels.</summary>
        internal Hash128 GetBodyFlow(LoogaWaterBody body)
        {
            var hash = new Hash128();
            foreach (WaterJointFlow joint in _graph.JointFlows)
            {
                if (_graph.Elements[joint.Body].Body != body && _graph.Elements[joint.Channel].Body != body) continue;

                WaterJointFlow value = joint;
                hash.Append(ref value);
            }
            foreach (WaterNetworkElement element in _graph.Elements)
            {
                if (element.Body != body || !element.IsChannel) continue;

                foreach (WaterChannelSample sample in element.Samples)
                {
                    hash.Append(sample.Knot);
                    hash.Append(sample.Speed);
                    hash.Append(sample.Turbulence);
                    hash.Append(sample.Aeration);
                }
            }
            return hash;
        }

        /// <summary>
        /// The network data of the flow of a body at the last solve. False when the body was not in the last solve.
        /// </summary>
        internal bool TryGetBodyFlow(LoogaWaterBody body, out Hash128 flow)
        {
            return _bodyFlows.TryGetValue(body.GetInstanceID(), out flow);
        }

        private string ReloadKey => "LoogaSoft.Terrain.WaterNetwork." + GetInstanceID();

        // The flow inputs that the bodies had at the last notification. After a script reload, the bodies take their
        // meshes back with that flow, so only the bodies whose network data changed build their flow again.
        [Serializable]
        private sealed class ReloadState
        {
            public int[] Ids;
            public string[] Flows;
        }

        private void StoreReloadState()
        {
            var state = new ReloadState { Ids = new int[_bodyFlows.Count], Flows = new string[_bodyFlows.Count] };
            int index = 0;
            foreach (KeyValuePair<int, Hash128> pair in _bodyFlows)
            {
                state.Ids[index] = pair.Key;
                state.Flows[index++] = pair.Value.ToString();
            }
            GeneratedReloadHandover.Store(ReloadKey, state);
        }

        private void TakeReloadState()
        {
            if (!GeneratedReloadHandover.TryTake(ReloadKey, out ReloadState state) || state.Ids == null) return;

            for (int index = 0; index < state.Ids.Length && index < state.Flows.Length; index++)
            {
                _bodyFlows[state.Ids[index]] = Hash128.Parse(state.Flows[index]);
            }
        }

        /// <summary>
        /// Queues a flow update for the channels that flow into or out of the body. They read the body flow where
        /// they overlap it. A body calls it after its flow changed.
        /// </summary>
        internal void QueueJoinedChannels(LoogaWaterBody body)
        {
            foreach (WaterJointFlow joint in _graph.JointFlows)
            {
                LoogaWaterBody channel = _graph.Elements[joint.Channel].Body;
                if (_graph.Elements[joint.Body].Body == body && channel && channel != body) channel.QueueNetworkFlow();
            }
        }

        /// <summary>
        /// The body upstream of a joint of the body that is nearest the focus point, within the reach. The foam
        /// simulation takes its foam where the water comes in.
        /// </summary>
        internal LoogaWaterBody FindUpstream(LoogaWaterBody body, Vector2 focus, float reach)
        {
            // The last solve. A solve on each camera would hash all bodies every frame.
            LoogaWaterBody found = null;
            float best = reach;
            WaterNetworkGraph graph = _graph;
            foreach (WaterNetworkConnection connection in graph.Connections)
            {
                // Falling water does not carry the foam of the river above. The plunge makes its own foam.
                if (connection.Fall) continue;

                LoogaWaterBody upstream = graph.Elements[connection.Upstream].Body;
                if (graph.Elements[connection.Downstream].Body != body || upstream == body) continue;

                float distance = Vector2.Distance(focus, new Vector2(connection.Point.x, connection.Point.z));
                if (distance >= best) continue;

                best = distance;
                found = upstream;
            }
            return found;
        }
        #endregion

        #region Falls
        // Each solved fall shows its sheet: on its fall component, or on a generated fall. Generated falls of joints
        // that are gone are destroyed. Fall components that match no fall joint show no sheet.
        private void NotifyFalls()
        {
            _shownFalls.Clear();
            foreach (WaterNetworkFall fall in _graph.Falls)
            {
                LoogaWaterFall target = fall.Override;
                WaterFallSettings settings = target ? target.Settings : _fallSettings;
                if (!target)
                {
                    if (!_fallMaterial) continue;

                    WaterNetworkElement channel = _graph.Elements[fall.Channel];
                    target = RequireGeneratedFall(channel);
                    target.Configure(channel.Body, channel.SplineIndex, _fallMaterial, _fallSettings);
                }
                _shownFalls.Add(target);
                target.Show(fall, settings);
            }
            foreach (WaterNetworkCrest crest in _graph.Crests)
            {
                if (!crest.Fall) continue;

                _shownFalls.Add(crest.Fall);
                crest.Fall.ShowCrest(crest);
            }
            for (int index = _generatedFalls.Count - 1; index >= 0; index--)
            {
                LoogaWaterFall fall = _generatedFalls[index];
                if (fall && _shownFalls.Contains(fall)) continue;

                if (fall) fall.DestroyGenerated();
                _generatedFalls.RemoveAt(index);
            }
            foreach (LoogaWaterFall fall in _fallOverrides)
            {
                if (fall && !_shownFalls.Contains(fall)) fall.Hide();
            }
        }

        private LoogaWaterFall RequireGeneratedFall(WaterNetworkElement channel)
        {
            foreach (LoogaWaterFall fall in _generatedFalls)
            {
                if (fall && !_shownFalls.Contains(fall) && fall.Channel == channel.Body &&
                    fall.SplineIndex == channel.SplineIndex)
                {
                    return fall;
                }
            }
            LoogaWaterFall created = LoogaWaterFall.CreateGenerated(this);
            _generatedFalls.Add(created);
            return created;
        }

        // Also removes the generated falls of this network that a script reload left without a list entry.
        private void DestroyGeneratedFalls()
        {
            _shownFalls.Clear();
            foreach (LoogaWaterFall fall in LoogaWaterFall.Active)
            {
                if (fall && fall.IsGenerated && (fall.Owner == this || !fall.Owner) && !_generatedFalls.Contains(fall))
                {
                    _generatedFalls.Add(fall);
                }
            }
            foreach (LoogaWaterFall fall in _generatedFalls)
            {
                if (fall) fall.DestroyGenerated();
            }
            _generatedFalls.Clear();
        }

        // A flow zone at a lip changes the speed of the water over it.
        private void OnFlowZoneChanged(Rect area)
        {
            foreach (WaterNetworkFall fall in _graph.Falls)
            {
                if (!area.Contains(new Vector2(fall.Lip.x, fall.Lip.z))) continue;

                _isSolved = false;
                QueueSolve();
                return;
            }
        }
        #endregion
    }
}
