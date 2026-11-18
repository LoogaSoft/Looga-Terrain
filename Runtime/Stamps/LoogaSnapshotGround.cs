using System;
using System.Collections.Generic;
using LoogaSoft.Terrain.Geology;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Ground source for a <see cref="LoogaRockFace"/>: the heights at a <see cref="LoogaHeightSnapshot"/>. Put the
    /// snapshot before a <see cref="LoogaTalusStamp"/>. The rock then builds on the ground before the talus, and the
    /// talus buries the rock base. Each height write of the stamp world raises
    /// <see cref="RockFaceGroundSource.HeightsChanged"/>, so the rock faces in the written region build again.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Looga/Stamps/Snapshot Ground Source")]
    public sealed class LoogaSnapshotGround : RockFaceGroundSource
    {
        [Tooltip("Snapshot of the stamp world. Only the modifiers before it apply to the heights.")]
        [SerializeField] private LoogaHeightSnapshot _snapshot;

        // The stamp world of the snapshot, whose height writes this source reports.
        private LoogaStampWorld _watchedWorld;

        public LoogaHeightSnapshot Snapshot => _snapshot;

        public void SetSnapshot(LoogaHeightSnapshot snapshot)
        {
            _snapshot = snapshot;
            if (isActiveAndEnabled) WatchWorld();
        }

        #region Built-in
        private void OnEnable()
        {
            WatchWorld();
        }

        private void OnDisable()
        {
            UnwatchWorld();
        }

        private void OnValidate()
        {
            if (isActiveAndEnabled) WatchWorld();
        }
        #endregion

        #region Height Changes
        private void WatchWorld()
        {
            LoogaStampWorld world = _snapshot ? _snapshot.GetComponentInParent<LoogaStampWorld>() : null;
            if (world == _watchedWorld) return;
            UnwatchWorld();
            _watchedWorld = world;
            if (world) world.HeightsUpdated += OnHeightsUpdated;
        }

        private void UnwatchWorld()
        {
            if (!ReferenceEquals(_watchedWorld, null)) _watchedWorld.HeightsUpdated -= OnHeightsUpdated;
            _watchedWorld = null;
        }

        // The written region covers each changed modifier, also one before the snapshot under a later stamp that hides
        // it. Play mode has no world updates.
        private void OnHeightsUpdated(Rect worldRegion)
        {
            if (!Application.isPlaying) RaiseHeightsChanged(worldRegion);
        }
        #endregion

        public override IRockFaceGroundReader Open(Rect worldRegion)
        {
            if (!_snapshot || !_snapshot.isActiveAndEnabled) return null;
            LoogaStampWorld world = _snapshot.GetComponentInParent<LoogaStampWorld>();
            if (!world) return null;
            return new Reader(KeptTiles.Read(world, _snapshot, worldRegion));
        }

        public override bool Prefetch(Rect worldRegion)
        {
            // Without a snapshot or a world, Open returns null and reads nothing.
            if (!_snapshot || !_snapshot.isActiveAndEnabled) return true;
            LoogaStampWorld world = _snapshot.GetComponentInParent<LoogaStampWorld>();
            return !world || KeptTiles.Prefetch(world, _snapshot, worldRegion);
        }

        public override bool IsReady
        {
            get
            {
                // Without a snapshot or a world, Open returns null and the rock face reports it.
                if (!_snapshot || !_snapshot.isActiveAndEnabled) return true;
                LoogaStampWorld world = _snapshot.GetComponentInParent<LoogaStampWorld>();
                return !world || world.IsSnapshotReady(_snapshot);
            }
        }

        // Player builds leave out the stamp textures (LoogaStampPlayerBuild), so a player cannot evaluate the snapshot.
        public override bool ReadableInPlayer => false;

        /// <summary>Releases the snapshot tiles that the builds read.</summary>
        public override void EndBuilds()
        {
            KeptTiles.Clear();
        }

        private struct Tile
        {
            public Vector3 Position;
            public Vector3 Size;
            public int Resolution;
            public NativeArray<ushort> Values;
        }

        // Snapshot heights of the tiles of rock face regions. The rock face builds after a script reload read the same
        // tiles many times (81 builds: 187 tile evaluations and readbacks, 5.7 s), so the tiles of a region stay for the
        // next builds. The seams of an evaluation are averaged only between its own tiles, so a region reuses only the
        // tiles of a region with the same terrains. The tiles of a snapshot stay only while the modifiers before it are
        // the same, with the same revisions: a script sets the cliff textures after a reload, for example. A moved
        // modifier makes a world update; no tile is kept while it waits, and its height write drops the tiles of its
        // region. The tile values are native memory, so the builds make no managed garbage (8 MB for each tile). A build
        // on a worker thread can still read dropped tiles, so all tiles read since the queue was last empty are
        // released together in EndBuilds. Prefetch reads the tiles from the GPU in the background, so the main thread
        // does not wait for the evaluation and the readback. Main thread only.
        private static class KeptTiles
        {
            // Most tile sets that read from the GPU at the same time. Each tile texture uses 8 MB until its read ends.
            private const int MaximumReads = 4;

            private sealed class Kept
            {
                public ulong Signature;
                public readonly List<TileSet> Sets = new();
            }

            // The tiles of one evaluation. The completion callbacks record the reads, because a done request that is
            // checked in a later frame reports an error.
            private sealed class TileSet
            {
                public UnityEngine.Terrain[] Terrains;
                public List<Tile> Tiles;
                // The evaluated tile textures. They are released after all reads end.
                public StampHeights Heights;
                public AsyncGPUReadbackRequest[] Requests;
                public bool[] Done;
                public int Pending;
                public string Error;

                public bool Reading => Pending > 0;

                public void OnRead(int index, AsyncGPUReadbackRequest request)
                {
                    if (Done[index]) return;
                    Done[index] = true;
                    Pending--;
                    if (request.hasError)
                    {
                        Error ??= "GPU readback failed for " + (Terrains[index] ? Terrains[index].name : "a terrain") +
                            ".";
                    }
                }

                public void WaitForReads()
                {
                    for (int index = 0; index < Requests.Length; index++)
                    {
                        if (Done[index]) continue;
                        Requests[index].WaitForCompletion();
                        // The callback normally runs in WaitForCompletion. The status is still valid in this call.
                        OnRead(index, Requests[index]);
                    }
                }
            }

            private static readonly Dictionary<LoogaHeightSnapshot, Kept> Snapshots = new();
            private static readonly HashSet<LoogaStampWorld> Watched = new();
            private static readonly List<UnityEngine.Terrain> Terrains = new();
            // All tile sets read since the queue was last empty, kept or not.
            private static readonly List<TileSet> ReadSets = new();

            // Starts the read of the tiles of a region. Returns true when Read can return them without a wait.
            public static bool Prefetch(LoogaStampWorld world, LoogaHeightSnapshot snapshot, Rect worldRegion)
            {
                int reads = ReleaseTextures();
                Kept kept = Find(world, snapshot, worldRegion, out TileSet set);
                // While the world updates, no tile is kept. Read then evaluates and waits, as without a prefetch.
                if (kept == null) return true;
                if (set != null) return !set.Reading;
                if (reads >= MaximumReads) return false;
                kept.Sets.Add(Start(world, snapshot, worldRegion));
                return false;
            }

            public static List<Tile> Read(LoogaStampWorld world, LoogaHeightSnapshot snapshot, Rect worldRegion)
            {
                ReleaseTextures();
                Kept kept = Find(world, snapshot, worldRegion, out TileSet set);
                if (set == null)
                {
                    set = Start(world, snapshot, worldRegion);
                    kept?.Sets.Add(set);
                }
                set.WaitForReads();
                ReleaseTextures();
                if (set.Error != null)
                {
                    kept?.Sets.Remove(set);
                    throw new InvalidOperationException(set.Error);
                }
                return set.Tiles;
            }

            public static void Clear()
            {
                foreach (TileSet set in ReadSets)
                {
                    // A read writes into the values until it ends.
                    set.WaitForReads();
                }
                ReleaseTextures();
                foreach (TileSet set in ReadSets)
                {
                    foreach (Tile tile in set.Tiles)
                    {
                        tile.Values.Dispose();
                    }
                }
                ReadSets.Clear();
                Snapshots.Clear();
                Watched.RemoveWhere(world => !world);
            }

            // Finds the kept tile set of the terrains of a region, or null. Returns the kept tiles of the snapshot, or
            // null while the world updates.
            private static Kept Find(LoogaStampWorld world, LoogaHeightSnapshot snapshot, Rect worldRegion,
                out TileSet set)
            {
                set = null;
                if (Watched.Add(world))
                {
                    world.HeightsUpdated += Drop;
                }
                if (world.IsUpdating || world.HasRequestedUpdate)
                {
                    Snapshots.Remove(snapshot);
                    return null;
                }
                ulong signature = world.SnapshotSignature(snapshot);
                if (!Snapshots.TryGetValue(snapshot, out Kept kept) || kept.Signature != signature)
                {
                    Snapshots[snapshot] = kept = new Kept { Signature = signature };
                }
                world.GetTerrains(worldRegion, Terrains);
                foreach (TileSet candidate in kept.Sets)
                {
                    if (!SameTerrains(candidate.Terrains, Terrains)) continue;
                    set = candidate;
                    break;
                }
                return kept;
            }

            // Evaluates the tiles of a region and starts their reads into native memory. The readback buffers of Unity
            // come from the frame allocator, and many large reads in one frame can use all of it.
            private static TileSet Start(LoogaStampWorld world, LoogaHeightSnapshot snapshot, Rect worldRegion)
            {
                StampHeights heights = world.ReadSnapshot(snapshot, worldRegion);
                int count = heights.Terrains.Count;
                var set = new TileSet
                {
                    Terrains = new UnityEngine.Terrain[count],
                    Tiles = new List<Tile>(count),
                    Heights = heights,
                    Requests = new AsyncGPUReadbackRequest[count],
                    Done = new bool[count],
                    // One more until all reads started, so a read that ends early does not end the set.
                    Pending = count + 1
                };
                ReadSets.Add(set);
                for (int index = 0; index < count; index++)
                {
                    RenderTexture texture = heights.Textures[index];
                    set.Terrains[index] = heights.Terrains[index];
                    // The set owns the values before the readback, so a failed readback does not leak them.
                    set.Tiles.Add(new Tile
                    {
                        Position = heights.Terrains[index].GetPosition(),
                        Size = heights.Terrains[index].terrainData.size,
                        Resolution = texture.width,
                        Values = new NativeArray<ushort>(texture.width * texture.height, Allocator.Persistent,
                            NativeArrayOptions.UninitializedMemory)
                    });
                }
                int started = 0;
                try
                {
                    for (; started < count; started++)
                    {
                        NativeArray<ushort> values = set.Tiles[started].Values;
                        int tile = started;
                        set.Requests[started] = AsyncGPUReadback.RequestIntoNativeArray(ref values,
                            heights.Textures[started], 0, request => set.OnRead(tile, request));
                    }
                }
                catch (Exception exception)
                {
                    // The reads that did not start have nothing to wait for.
                    for (int index = started; index < count; index++)
                    {
                        set.Done[index] = true;
                        set.Pending--;
                    }
                    set.Error ??= exception.Message;
                }
                set.Pending--;
                return set;
            }

            // Releases the tile textures of the sets whose reads ended. Returns the number of sets that still read.
            private static int ReleaseTextures()
            {
                int reads = 0;
                foreach (TileSet set in ReadSets)
                {
                    if (set.Heights == null) continue;
                    if (set.Reading)
                    {
                        reads++;
                        continue;
                    }
                    set.Heights.Dispose();
                    set.Heights = null;
                }
                return reads;
            }

            private static bool SameTerrains(UnityEngine.Terrain[] kept, List<UnityEngine.Terrain> terrains)
            {
                if (kept.Length != terrains.Count) return false;
                for (int index = 0; index < kept.Length; index++)
                {
                    if (kept[index] != terrains[index]) return false;
                }
                return true;
            }

            // New heights in a region can change the snapshot of every tile that the region touches.
            private static void Drop(Rect worldRegion)
            {
                foreach (Kept kept in Snapshots.Values)
                {
                    kept.Sets.RemoveAll(set => Array.Exists(set.Terrains, terrain => !terrain || !terrain.terrainData ||
                        StampMapping.Touches(new StampTile(terrain).WorldRect, worldRegion)));
                }
            }
        }

        // Samples the snapshot tiles of a region. The tiles do not change while a build reads them and stay until the
        // queue is empty, so worker threads can sample them.
        private sealed class Reader : IRockFaceGroundReader
        {
            private List<Tile> _tiles;

            public Reader(List<Tile> tiles)
            {
                _tiles = tiles;
            }

            public bool TrySampleHeight(float x, float z, out float height)
            {
                foreach (Tile tile in _tiles)
                {
                    float column = (x - tile.Position.x) / tile.Size.x * (tile.Resolution - 1);
                    float row = (z - tile.Position.z) / tile.Size.z * (tile.Resolution - 1);
                    if (column < 0f || row < 0f || column > tile.Resolution - 1 || row > tile.Resolution - 1) continue;

                    int left = Mathf.Min((int)column, tile.Resolution - 2);
                    int bottom = Mathf.Min((int)row, tile.Resolution - 2);
                    float fx = column - left;
                    float fz = row - bottom;
                    int sample = bottom * tile.Resolution + left;
                    float lower = Mathf.Lerp(tile.Values[sample], tile.Values[sample + 1], fx);
                    float upper = Mathf.Lerp(tile.Values[sample + tile.Resolution],
                        tile.Values[sample + tile.Resolution + 1], fx);
                    // Value 32766 is the full terrain height, like the 16-bit terrain heightmap.
                    height = tile.Position.y + Mathf.Lerp(lower, upper, fz) / 32766f * tile.Size.y;
                    return true;
                }
                height = 0f;
                return false;
            }

            public void Dispose()
            {
                _tiles = null;
            }
        }
    }
}
