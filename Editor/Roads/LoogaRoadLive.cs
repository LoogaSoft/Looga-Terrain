using System;
using System.Collections.Generic;
using System.Diagnostics;
using LoogaSoft.Terrain.Stamps;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Roads.Editor
{
    /// <summary>
    /// Keeps the road slabs, their shoulder and clear masks and the bridge parts of the road network live. Road, pad
    /// and bridge marker changes (also by undo) mark the inputs as changed, and height writes of the stamp world mark
    /// the changed ground. When the world has settled (heights written and synchronized), LoogaRoadSlabs.UpdateLive runs
    /// the road topology in full and builds only the slabs whose shape, neighbours or ground changed. The masks change
    /// in memory and reach the shoulder and clear stamps through stamp texture sources. Bridges whose markers, road
    /// ends or pier ground changed are built again. The roads are live when the shoulder and clear stamps have texture
    /// sources.
    /// </summary>
    /// <remarks>
    /// The update runs in <see cref="LoogaStampWorld.BeforeChangeCapture"/>, so the world applies the changed masks in
    /// the same tick. The first update after a load keeps the saved meshes and sets the masks before the first capture
    /// of the world, so a load starts no terrain update. The updater follows the first road network of the open
    /// scenes.
    /// </remarks>
    [InitializeOnLoad]
    public static class LoogaRoadLive
    {
        private static LoogaRoadSlabs.LiveState _state = new LoogaRoadSlabs.LiveState();
        private static LoogaRoadNetwork _network;
        private static LoogaStampWorld _world;
        // True after a hierarchy change, so the next editor update finds the road network again.
        private static bool _findNetwork = true;
        // The number of enabled stamp worlds at the last search. After an editor start the scripts load before the
        // scene, so a search without a network repeats when a world is enabled.
        private static int _searchedWorlds = -1;
        // Height writes since the last update, and whether the world settled after them.
        private static Rect? _heights;
        private static bool _settled;
        // Inputs changed since the last update. A change waits for one change capture of the world, so a terrain
        // update that the change starts holds the slab update until it settles.
        private static bool _changed;
        private static bool _rebuildAll;
        private static int _tick;
        private static int _changeTick;
        // What marked the last change, for the update history.
        private static string _reason;
        private static readonly Queue<string> RecentUpdates = new();

        static LoogaRoadLive()
        {
            Spline.Changed += (_, _, _) => MarkChanged("spline");
            // An undo publishes object change events for the objects that it restores, so an undo that changes no
            // road, pad or bridge marker starts no update (about 0.3 s).
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            EditorSceneManager.sceneOpened += (_, _) => Reset();
            EditorApplication.hierarchyChanged += () => _findNetwork = true;
            EditorApplication.update += Subscribe;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            Subscribe();
        }

        /// <summary>
        /// True skips the live updates, for example during a bulk edit of roads and pads. The changes wait, and the
        /// next update after Suspended goes false includes them.
        /// </summary>
        public static bool Suspended { get; set; }

        /// <summary>The last update: slabs built, masks changed and times.</summary>
        public static string LastUpdate { get; private set; }

        /// <summary>Bridges built again in the last update.</summary>
        public static int LastBridges { get; private set; }

        /// <summary>Time of the last update in seconds, slabs, masks and bridges.</summary>
        public static double LastSeconds { get; private set; }

        /// <summary>Number of updates since the load. Timing checks wait for it to change.</summary>
        public static int Updates { get; private set; }

        /// <summary>The last updates, newest last, one line each.</summary>
        public static string History => string.Join("\n", RecentUpdates);

        #region Commands
        [MenuItem("CONTEXT/LoogaRoadNetwork/Make Roads Live")]
        private static void MakeLiveMenu(MenuCommand command)
        {
            var network = (LoogaRoadNetwork)command.context;
            LoogaRoadSlabs.MakeLive(network);
            UnityEngine.Debug.Log("[LoogaRoadLive] The road slabs, masks and bridges of " + network.name +
                " are live.");
        }

        /// <summary>Builds every slab mesh, both masks and the bridges again with the next change capture.</summary>
        [MenuItem("CONTEXT/LoogaRoadNetwork/Build Live Roads Again")]
        public static void RebuildAll()
        {
            _state.Dispose();
            _state = new LoogaRoadSlabs.LiveState();
            _rebuildAll = true;
        }

        /// <summary>Compares the slabs, the masks and the bridges with a full build in memory. Writes nothing.</summary>
        public static string Compare()
        {
            if (!_network) return "No road network.";
            return LoogaRoadSlabs.CompareWithBuild(_network, _state) + LoogaRoadBridges.Compare(_network);
        }

        [MenuItem("CONTEXT/LoogaRoadNetwork/Compare Live Roads With A Build")]
        private static void CompareMenu()
        {
            UnityEngine.Debug.Log("[LoogaRoadLive] " + Compare());
        }
        #endregion

        #region Changes
        private static void MarkChanged(string reason)
        {
            _changed = true;
            _changeTick = _tick;
            _reason = reason;
        }

        // Changes of a road, a pad or a bridge marker, and every object change of another kind than a property
        // change. Asset changes and scene changes do not count: an update writes the slab meshes (assets) and marks
        // the scene dirty.
        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            for (int index = 0; index < stream.length; index++)
            {
                ObjectChangeKind kind = stream.GetEventType(index);
                if (kind is ObjectChangeKind.CreateAssetObject or ObjectChangeKind.DestroyAssetObject or
                    ObjectChangeKind.ChangeAssetObjectProperties or ObjectChangeKind.ChangeScene) continue;
                if (kind != ObjectChangeKind.ChangeGameObjectOrComponentProperties)
                {
                    MarkChanged(kind.ToString());
                    return;
                }
                stream.GetChangeGameObjectOrComponentPropertiesEvent(index, out var change);
                Object changed = EditorUtility.EntityIdToObject(change.instanceId);
                GameObject owner = changed is Component component ? component.gameObject : changed as GameObject;
                if (owner && IsInput(owner.transform))
                {
                    MarkChanged(owner.name);
                    return;
                }
            }
        }

        // The roads, the pads and the bridges of the network are inputs.
        private static bool IsInput(Transform transform)
        {
            if (!_network) return false;
            Transform roads = _network.transform;
            Transform pads = _network.PadRoot;
            Transform bridges = _network.BridgeRoot;
            for (Transform parent = transform; parent; parent = parent.parent)
            {
                if (parent == roads || parent == pads || parent == bridges) return true;
            }
            return false;
        }
        #endregion

        #region World
        // Follows the stamp world of the road network of the open scenes.
        private static void Subscribe()
        {
            int worlds = LoogaStampWorld.EnabledWorlds.Count;
            if (_findNetwork || (!ReferenceEquals(_network, null) && !_network) ||
                (!_network && worlds != _searchedWorlds))
            {
                _findNetwork = false;
                _searchedWorlds = worlds;
                _network = Object.FindFirstObjectByType<LoogaRoadNetwork>();
            }
            LoogaStampWorld world = _network ? _network.World : null;
            if (ReferenceEquals(world, _world)) return;
            Unsubscribe();
            _world = world;
            if (!world) return;
            world.BeforeChangeCapture += OnBeforeCapture;
            world.HeightsUpdated += OnHeightsUpdated;
            world.UpdateSettled += OnUpdateSettled;
        }

        private static void Unsubscribe()
        {
            if (ReferenceEquals(_world, null)) return;
            _world.BeforeChangeCapture -= OnBeforeCapture;
            _world.HeightsUpdated -= OnHeightsUpdated;
            _world.UpdateSettled -= OnUpdateSettled;
            _world = null;
        }

        // True when no update runs or waits and the CPU heights match the generated heights.
        private static bool IsIdle()
        {
            return _world && !_world.IsUpdating && !_world.HasRequestedUpdate && _world.HeightsSynchronized;
        }

        private static void OnHeightsUpdated(Rect region)
        {
            _heights = _heights.HasValue
                ? Rect.MinMaxRect(Mathf.Min(_heights.Value.xMin, region.xMin),
                    Mathf.Min(_heights.Value.yMin, region.yMin), Mathf.Max(_heights.Value.xMax, region.xMax),
                    Mathf.Max(_heights.Value.yMax, region.yMax))
                : region;
            _settled = false;
        }

        private static void OnUpdateSettled(Rect region)
        {
            _settled = true;
        }
        #endregion

        #region Update
        private static void OnBeforeCapture()
        {
            _tick++;
            if (Suspended || !LoogaRoadSlabs.IsLive(_network)) return;
            // The first update keeps the saved meshes and only sets the masks, so it runs at once.
            bool first = _state.Slabs == null && !_rebuildAll;
            if (!first)
            {
                bool ready = _heights.HasValue ? _settled : _rebuildAll || (_changed && _tick > _changeTick + 1);
                if (!ready || !IsIdle()) return;
            }
            Run();
        }

        private static void Run()
        {
            Rect? heights = _heights;
            bool rebuildAll = _rebuildAll;
            string reason = _changed ? _reason : "none";
            _heights = null;
            _settled = false;
            _changed = false;
            _rebuildAll = false;
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                LoogaRoadSlabs.Trace("live: update (" + reason + ")");
                LoogaRoadSlabs.LiveUpdate update = LoogaRoadSlabs.UpdateLive(_network, _state, heights, rebuildAll);
                // The piers stand on the CPU heights, so the bridges wait for synchronized heights.
                if (IsIdle())
                {
                    LoogaRoadSlabs.Trace("live: bridges");
                    LastBridges = LoogaRoadBridges.UpdateLive(_network);
                }
                else
                {
                    MarkChanged("bridges waiting");
                }
                LoogaRoadSlabs.Trace("live: done");
                LastUpdate = update?.ToString();
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[LoogaRoadLive] " + exception.Message);
            }
            LastSeconds = clock.Elapsed.TotalSeconds;
            Updates++;
            RecentUpdates.Enqueue(Updates + ": " + LastUpdate + " Bridges " + LastBridges + ", " +
                LastSeconds.ToString("F3") + " s, heights " + (heights.HasValue ? heights.Value.ToString() : "none") +
                ", change " + reason + ".");
            while (RecentUpdates.Count > 10)
            {
                RecentUpdates.Dequeue();
            }
        }

        private static void Reset()
        {
            _state.Dispose();
            _state = new LoogaRoadSlabs.LiveState();
            _heights = null;
            _settled = false;
            _changed = false;
            _findNetwork = true;
            Subscribe();
        }

        private static void Release()
        {
            _state.Dispose();
            Unsubscribe();
        }
        #endregion
    }
}
