using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    /// <summary>
    /// Keeps the texture layer maps and the stored placements of a stamp world over an editor start or a scene load.
    /// After a scene save in which the world is settled, the maps and placements go to the Library folder with a record
    /// of the saved files and the source textures. At the next load the world takes them back when the scene file,
    /// the terrain files and the stamp code are unchanged. When the source textures of the waiting modifiers are also
    /// the same as at the save, the load update of those modifiers is replaced by a GPU check: the updater evaluates the
    /// alphamaps of all terrains and updates only the areas where they differ from the saved ones.
    /// </summary>
    /// <remarks>
    /// Without this, each load evaluated the textures and the placements of all terrains again (Kubera World: about 40 s).
    /// The height modifiers that wait at a load already write no heights (LoogaStampWorldUpdater.WaitingAtLoad).
    /// Any difference or a missing file gives the normal load update. The record is deleted at the start of each save,
    /// so an unsettled save leaves no record.
    /// </remarks>
    [InitializeOnLoad]
    internal static class LoogaStampTrustedLoad
    {
        private const string RecordName = "record.json";
        private const string PlacementsName = "placements.bin";
        private const string DataPrefix = "data_";

        [Serializable]
        private sealed class Record
        {
            public string StampVersion;
            public string Scene;
            public string[] Terrains;
            public string Sources;
            // The name and the texture hash of each source in Sources, to name a source that differs at a load.
            public string[] SourceParts;
            public string Data;
            public int[] Tiles;
            public int[] Widths;
            public int[] Heights;
            public string[] Objects;
            // True when only live updates wrote the alphamaps since the scene load. A full texture rebuild since the
            // load sets it false, and the next load then checks the alphamaps: after such saves, the saved alphamaps of
            // some terrains differed from the evaluation (cause not found). An old record reads false.
            public bool LiveUpdatesOnly;
        }

        // A save whose GPU readbacks are open. A later save of the same world replaces it.
        private sealed class PendingSave
        {
            public LoogaStampWorld World;
            public string Folder;
            public string ScenePath;
            public Record Record;
            public int Remaining;
            public bool Failed;
            public readonly List<AsyncGPUReadbackRequest> Requests = new();
        }

        private static readonly Dictionary<LoogaStampWorld, PendingSave> Pending = new();
        // The record of the saved result of each world that took a saved result at its load.
        private static readonly Dictionary<LoogaStampWorld, Record> TrustedSources = new();
        // Why a world did not take the saved result at its load.
        private static readonly Dictionary<LoogaStampWorld, string> Refusals = new();
        private static readonly List<LoogaStampTextureSource> Sources = new();
        private static byte[] _buffer;
        private static Texture2D _upload;

        static LoogaStampTrustedLoad()
        {
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.quitting += CompletePendingSaves;
            AssemblyReloadEvents.beforeAssemblyReload += CompletePendingSaves;
        }

        #region Save
        private static void OnSceneSaved(Scene scene)
        {
            foreach (LoogaStampWorld world in new List<LoogaStampWorld>(LoogaStampWorld.EnabledWorlds))
            {
                if (!world || !world.UpdateInEditor || world.gameObject.scene != scene) continue;
                try
                {
                    Save(world, scene.path);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    Debug.LogWarning("Looga Stamps: the result could not be kept for the next load (" +
                        exception.Message + "). The next load updates all terrains.", world);
                }
            }
        }

        private static void Save(LoogaStampWorld world, string scenePath)
        {
            string folder = FolderOf(world);
            Directory.CreateDirectory(folder);
            // A record of an older save must not stay when this save writes none.
            File.Delete(Path.Combine(folder, RecordName));
            Pending.Remove(world);
            if (!LoogaStampWorldUpdater.IsSettled(world) || AnyModifierWaits(world) || !world.HasAllTextureLayers() ||
                world.StoredPlacements == null) return;
            var parts = new List<string>();
            string sources = SourceHash(world, parts);
            if (sources == null) return;

            string data = DataPrefix + DateTime.UtcNow.Ticks;
            string dataFolder = Path.Combine(folder, data);
            Directory.CreateDirectory(dataFolder);
            var record = new Record
            {
                StampVersion = LoogaStampWorld.StampCodeVersion, Sources = sources, SourceParts = parts.ToArray(),
                Data = data,
                LiveUpdatesOnly = !world.TexturesRebuiltSinceLoad
            };
            if (!WritePlacements(world, Path.Combine(dataFolder, PlacementsName), record)) return;

            var save = new PendingSave { World = world, Folder = folder, ScenePath = scenePath, Record = record };
            var tiles = new List<int>();
            var widths = new List<int>();
            var heights = new List<int>();
            IReadOnlyList<UnityEngine.Terrain> terrains = world.Terrains;
            for (int index = 0; index < terrains.Count; index++)
            {
                UnityEngine.Terrain terrain = terrains[index];
                if (!terrain || !world.TextureLayers.TryGetValue(terrain, out TextureStampEvaluator.TileLayers tile))
                {
                    continue;
                }
                tiles.Add(index);
                widths.Add(tile.Layers.width);
                heights.Add(tile.Layers.height);
                Request(save, tile.Layers, Path.Combine(dataFolder, MapName("layers", index)));
                Request(save, tile.Weights, Path.Combine(dataFolder, MapName("weights", index)));
            }
            record.Tiles = tiles.ToArray();
            record.Widths = widths.ToArray();
            record.Heights = heights.ToArray();
            Pending[world] = save;
        }

        // The stamps are written as GlobalObjectIds, which stay the same over an editor start, and the terrains as their
        // index in the world.
        private static bool WritePlacements(LoogaStampWorld world, string path, Record record)
        {
            var objects = new List<string>();
            var ids = new Dictionary<UnityEngine.Object, int>();
            IReadOnlyList<UnityEngine.Terrain> terrains = world.Terrains;
            bool written;
            using (var writer = new BinaryWriter(new BufferedStream(File.Create(path), 1 << 20)))
            {
                written = world.StoredPlacements.Write(writer, value =>
                {
                    for (int index = 0; index < terrains.Count; index++)
                    {
                        if (terrains[index] == value) return -1 - index;
                    }
                    if (ids.TryGetValue(value, out int id)) return id;
                    id = objects.Count;
                    objects.Add(GlobalObjectId.GetGlobalObjectIdSlow(value).ToString());
                    ids.Add(value, id);
                    return id;
                });
            }
            record.Objects = objects.ToArray();
            return written;
        }

        private static string MapName(string kind, int terrain)
        {
            return kind + "_" + terrain + ".bin";
        }

        private static void Request(PendingSave save, RenderTexture map, string path)
        {
            save.Remaining++;
            save.Requests.Add(AsyncGPUReadback.Request(map, 0, request => OnReadback(save, request, path)));
        }

        // A quit or a script reload soon after a save would drop the open readbacks, and the save would keep no record.
        // The readback callbacks also run inside WaitForCompletion, so the record is written here.
        private static void CompletePendingSaves()
        {
            foreach (PendingSave save in new List<PendingSave>(Pending.Values))
            {
                foreach (AsyncGPUReadbackRequest request in save.Requests)
                {
                    if (!request.done)
                    {
                        request.WaitForCompletion();
                    }
                }
            }
        }

        private static void OnReadback(PendingSave save, AsyncGPUReadbackRequest request, string path)
        {
            save.Remaining--;
            // A later save of the world replaced this one.
            if (!Pending.TryGetValue(save.World, out PendingSave current) || current != save) return;
            try
            {
                if (request.hasError)
                {
                    save.Failed = true;
                }
                else
                {
                    NativeArray<byte> data = request.GetData<byte>();
                    if (_buffer == null || _buffer.Length < data.Length)
                    {
                        _buffer = new byte[data.Length];
                    }
                    NativeArray<byte>.Copy(data, 0, _buffer, 0, data.Length);
                    using FileStream file = File.Create(path);
                    file.Write(_buffer, 0, data.Length);
                }
                if (save.Remaining == 0)
                {
                    Finish(save);
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                save.Failed = true;
                Pending.Remove(save.World);
            }
        }

        // All maps are written. The record names the saved files as they are now, after the scene and asset save.
        private static void Finish(PendingSave save)
        {
            Pending.Remove(save.World);
            if (save.Failed || !save.World) return;

            Record record = save.Record;
            IReadOnlyList<UnityEngine.Terrain> terrains = save.World.Terrains;
            record.Terrains = new string[terrains.Count];
            for (int index = 0; index < terrains.Count; index++)
            {
                TerrainData data = terrains[index] ? terrains[index].terrainData : null;
                // A terrain with unsaved changes is not the file on disk.
                if (data && EditorUtility.IsDirty(data)) return;
                record.Terrains[index] = data ? FileStamp(AssetDatabase.GetAssetPath(data)) : string.Empty;
            }
            record.Scene = FileStamp(save.ScenePath);
            File.WriteAllText(Path.Combine(save.Folder, RecordName), JsonUtility.ToJson(record));
            foreach (string folder in Directory.GetDirectories(save.Folder, DataPrefix + "*"))
            {
                if (Path.GetFileName(folder) == record.Data) continue;
                Directory.Delete(folder, true);
            }
        }
        #endregion

        #region Load
        /// <summary>
        /// Gives the world its saved maps and placements at the first capture after a load, when the scene file, the
        /// terrain files and the stamp code are the same as at the save. A world that kept them over a script reload
        /// keeps its own.
        /// </summary>
        internal static void AdoptAtLoad(LoogaStampWorld world)
        {
            TrustedSources.Remove(world);
            Refusals.Remove(world);
            if (world.HasAllTextureLayers() && world.StoredPlacements != null) return;
            try
            {
                string refusal = Adopt(world);
                if (refusal != null)
                {
                    Refusals[world] = refusal;
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                                              exception is ArgumentException)
            {
                Debug.LogWarning("Looga Stamps: the saved result could not be read (" + exception.Message + "). " +
                    "The world updates all terrains.", world);
                Refusals[world] = "the saved result could not be read";
            }
        }

        /// <summary>
        /// True when the world took a saved result at its load and the source textures of its texture and placement
        /// modifiers are the same as at the save. The saved terrains then hold the result of those modifiers.
        /// </summary>
        /// <param name="check">True when the saved alphamaps must be checked against an evaluation.</param>
        /// <param name="refusal">Why the saved result is not used, for the update note. Null when it is used.</param>
        internal static bool SourcesMatchSave(LoogaStampWorld world, out bool check, out string refusal)
        {
            check = true;
            if (!TrustedSources.Remove(world, out Record record))
            {
                refusal = Refusals.TryGetValue(world, out string reason) ? reason : "no saved result was taken";
                return false;
            }
            if (record.Sources != SourceHash(world))
            {
                refusal = "the source textures differ from the save" + SourceDifference(world, record);
                return false;
            }
            refusal = null;
            check = !record.LiveUpdatesOnly;
            return true;
        }

        // Null when the world took the saved result, else the reason.
        private static string Adopt(LoogaStampWorld world)
        {
            string folder = FolderOf(world);
            string recordPath = Path.Combine(folder, RecordName);
            if (!File.Exists(recordPath)) return "no record (the last save was not settled)";

            var record = JsonUtility.FromJson<Record>(File.ReadAllText(recordPath));
            string mismatch = Mismatch(world, record);
            if (mismatch != null) return mismatch;

            string dataFolder = Path.Combine(folder, record.Data);
            UnityEngine.Object[] objects = FindObjects(record);
            if (objects == null) return "a saved object id is not valid";

            StampPlacements placements;
            using (var reader = new BinaryReader(new BufferedStream(
                       File.OpenRead(Path.Combine(dataFolder, PlacementsName)), 1 << 20)))
            {
                placements = StampPlacements.Read(reader, id => Find(world, objects, id));
            }
            if (placements == null) return "the saved placements could not be read";

            var tiles = new List<TextureStampEvaluator.TileLayers>();
            try
            {
                for (int k = 0; k < record.Tiles.Length; k++)
                {
                    int index = record.Tiles[k];
                    UnityEngine.Terrain terrain = index < world.Terrains.Count ? world.Terrains[index] : null;
                    TerrainData data = terrain ? terrain.terrainData : null;
                    if (!data || data.alphamapWidth != record.Widths[k] || data.alphamapHeight != record.Heights[k])
                    {
                        return "the alphamap size of a terrain changed";
                    }
                    var tile = new TextureStampEvaluator.TileLayers { Terrain = terrain };
                    tiles.Add(tile);
                    tile.Layers = LoadMap(Path.Combine(dataFolder, MapName("layers", index)), record.Widths[k],
                        record.Heights[k], "Looga Stamps Layers");
                    tile.Weights = LoadMap(Path.Combine(dataFolder, MapName("weights", index)), record.Widths[k],
                        record.Heights[k], "Looga Stamps Weights");
                    if (!tile.Layers || !tile.Weights) return "a saved map could not be read";
                }
            }
            finally
            {
                // Incomplete maps go back to the pool. A complete set goes to the world below.
                if (tiles.Count != record.Tiles.Length || tiles.Exists(tile => !tile.Layers || !tile.Weights))
                {
                    foreach (TextureStampEvaluator.TileLayers tile in tiles)
                    {
                        tile.Dispose();
                    }
                    tiles.Clear();
                }
            }
            if (tiles.Count == 0) return "the save has no maps";

            foreach (TextureStampEvaluator.TileLayers tile in tiles)
            {
                world.AdoptTextureLayers(tile);
            }
            world.AdoptStoredPlacements(placements);
            TrustedSources[world] = record;
            return null;
        }

        // Null when the saved files and the stamp code are the same as at the save, and nothing changed them in memory
        // since the load. Else the first difference.
        private static string Mismatch(LoogaStampWorld world, Record record)
        {
            if (record == null || record.Tiles == null || record.Terrains == null || record.Objects == null ||
                record.Widths == null || record.Heights == null || string.IsNullOrEmpty(record.Data))
            {
                return "the record is incomplete";
            }
            if (record.StampVersion != LoogaStampWorld.StampCodeVersion) return "the stamp code changed";
            if (record.Scene != FileStamp(world.gameObject.scene.path)) return "the scene file changed";
            if (world.PlacementSetChanged) return "the placement set changed";

            IReadOnlyList<UnityEngine.Terrain> terrains = world.Terrains;
            if (record.Terrains.Length != terrains.Count)
            {
                return "the world has " + terrains.Count + " terrains, the save " + record.Terrains.Length;
            }
            for (int index = 0; index < terrains.Count; index++)
            {
                TerrainData data = terrains[index] ? terrains[index].terrainData : null;
                if (data && EditorUtility.IsDirty(data)) return "the terrain data of " + data.name + " is dirty";
                string stamp = data ? FileStamp(AssetDatabase.GetAssetPath(data)) : string.Empty;
                if (stamp != record.Terrains[index])
                {
                    return "the terrain data file of " + (data ? data.name : "terrain " + index) + " changed (now '" +
                        stamp + "' at '" + (data ? AssetDatabase.GetAssetPath(data) : "") + "', saved '" +
                        record.Terrains[index] + "')";
                }
            }
            return null;
        }

        // One call for all objects: each single GlobalObjectIdentifierToObjectSlow call took about 63 ms in Kubera World
        // (88 stamps: 5.6 s, batched: 0.07 s).
        private static UnityEngine.Object[] FindObjects(Record record)
        {
            var ids = new GlobalObjectId[record.Objects.Length];
            for (int index = 0; index < ids.Length; index++)
            {
                if (!GlobalObjectId.TryParse(record.Objects[index], out ids[index])) return null;
            }
            var objects = new UnityEngine.Object[ids.Length];
            GlobalObjectId.GlobalObjectIdentifiersToObjectsSlow(ids, objects);
            return objects;
        }

        private static UnityEngine.Object Find(LoogaStampWorld world, UnityEngine.Object[] objects, int id)
        {
            if (id < 0)
            {
                int index = -1 - id;
                return index < world.Terrains.Count ? world.Terrains[index] : null;
            }
            return id < objects.Length ? objects[id] : null;
        }

        private static RenderTexture LoadMap(string path, int width, int height, string name)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length != width * height * 4) return null;

            if (!_upload || _upload.width != width || _upload.height != height)
            {
                if (_upload)
                {
                    UnityEngine.Object.DestroyImmediate(_upload);
                }
                _upload = new Texture2D(width, height, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None)
                {
                    name = "Looga Stamps Map Upload", hideFlags = HideFlags.HideAndDontSave
                };
            }
            _upload.SetPixelData(bytes, 0);
            _upload.Apply(false, false);
            var descriptor = new RenderTextureDescriptor(width, height, GraphicsFormat.R8G8B8A8_UNorm, 0)
            {
                enableRandomWrite = true
            };
            RenderTexture map = TextureStampEvaluator.CreateMap(descriptor, name);
            Graphics.CopyTexture(_upload, map);
            return map;
        }
        #endregion

        #region Checks
        private static string FolderOf(LoogaStampWorld world)
        {
            string id = GlobalObjectId.GetGlobalObjectIdSlow(world).ToString();
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "LoogaStampWorld", "Trusted",
                Hash128.Compute(id).ToString());
        }

        // Size and write time of a file. A save, a checkout or a copy changes them.
        private static string FileStamp(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var file = new FileInfo(Path.GetFullPath(path));
            return file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing";
        }

        // True when an enabled modifier waits for its texture source. The terrains then lack its result.
        private static bool AnyModifierWaits(LoogaStampWorld world)
        {
            world.GetComponentsInChildren(false, Sources);
            foreach (LoogaStampTextureSource source in Sources)
            {
                if (!source.isActiveAndEnabled) continue;
                if (Waits<LoogaTextureModifier>(source) || Waits<LoogaPlacementModifier>(source) ||
                    Waits<LoogaHeightModifier>(source)) return true;
            }
            return false;
        }

        private static bool Waits<T>(LoogaStampTextureSource source) where T : Component
        {
            return source.TryGetComponent(out T stamp) && LoogaStampTextureSource.Waits(stamp);
        }

        // A hash of the textures that the sources give to texture and placement modifiers, in hierarchy order. Null when
        // a texture cannot be read on the CPU. The sources of height modifiers are not in it: their saved heights are
        // trusted at a load in any case.
        // With parts, also gives the name and the texture hash of each source.
        private static string SourceHash(LoogaStampWorld world, List<string> parts = null)
        {
            var hash = new Hash128();
            parts?.Clear();
            world.GetComponentsInChildren(false, Sources);
            for (int index = 0; index < Sources.Count; index++)
            {
                LoogaStampTextureSource source = Sources[index];
                if (!source.isActiveAndEnabled || source.TryGetComponent(out LoogaHeightModifier _)) continue;
                hash.Append(index);
                if (!AppendSource(ref hash, source)) return null;
                if (parts == null) continue;

                var part = new Hash128();
                AppendSource(ref part, source);
                parts.Add(source.name + " [" + TextureNames(source) + "]|" + part);
            }
            return hash.ToString();
        }

        private static string TextureNames(LoogaStampTextureSource source)
        {
            return string.Join(", ", new[] { source.HeightTexture, source.FalloffTexture, source.WeightNoiseTexture,
                source.Weight2NoiseTexture }.Where(texture => texture).Select(texture => texture.name));
        }

        private static bool AppendSource(ref Hash128 hash, LoogaStampTextureSource source)
        {
            return Append(ref hash, source.HeightTexture) && Append(ref hash, source.FalloffTexture) &&
                Append(ref hash, source.WeightNoiseTexture) && Append(ref hash, source.Weight2NoiseTexture);
        }

        // The number of sources that differ from the save and the name of the first one, or empty for an old record.
        private static string SourceDifference(LoogaStampWorld world, Record record)
        {
            var parts = new List<string>();
            if (record.SourceParts == null || SourceHash(world, parts) == null) return string.Empty;
            if (parts.Count != record.SourceParts.Length)
            {
                return " (" + parts.Count + " sources, the save " + record.SourceParts.Length + ")";
            }
            int first = -1, count = 0;
            for (int index = 0; index < parts.Count; index++)
            {
                if (Hash(parts[index]) == Hash(record.SourceParts[index])) continue;
                count++;
                if (first < 0)
                {
                    first = index;
                }
            }
            return first < 0 ? string.Empty : " (" + count + " of " + parts.Count + " sources, first: " +
                parts[first].Substring(0, parts[first].LastIndexOf('|')) + ", saved: " +
                record.SourceParts[first].Substring(0, record.SourceParts[first].LastIndexOf('|')) + ")";
        }

        // The texture hash of a source part.
        private static string Hash(string part)
        {
            return part.Substring(part.LastIndexOf('|') + 1);
        }

        private static bool Append(ref Hash128 hash, Texture texture)
        {
            if (!texture)
            {
                hash.Append(0);
                return true;
            }
            if (texture is not Texture2D readable || !readable.isReadable) return false;

            hash.Append(readable.width);
            hash.Append(readable.height);
            hash.Append((int)readable.graphicsFormat);
            hash.Append(readable.GetRawTextureData<byte>());
            return true;
        }
        #endregion
    }
}
