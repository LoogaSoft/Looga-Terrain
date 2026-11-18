using System;
using System.Collections.Generic;
using LoogaSoft.Terrain.Surface;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    // The scene objects of built rock strips: chunks with three levels of detail, the terrain ground values of the
    // terrain below each chunk, and optional colliders. The objects are not saved with the scene.
    internal sealed class RockFaceOutput
    {
        private readonly List<Mesh> _meshes = new();
        private readonly List<(UnityEngine.Terrain Terrain, MeshRenderer Renderer)> _groundRenderers = new();
        // The ground block of each terrain, so a check can find blocks whose splat maps the terrain destroyed.
        private readonly Dictionary<UnityEngine.Terrain, MaterialPropertyBlock> _groundBlocks = new();

#if UNITY_EDITOR
        // The chunks of the build that the cache file will hold.
        private readonly List<(RockFaceChunks.Chunk Chunk, bool Collider, string Terrain)> _chunks = new();
#endif

        public GameObject Root { get; private set; }
        public int ChunkCount { get; private set; }
        public Bounds Bounds { get; private set; }

        // The output as the next domain reads it after a script reload.
        [Serializable]
        private sealed class ReloadState
        {
            public int Root;
            public int ChunkCount;
            public Bounds Bounds;
            public int[] Meshes;
            public int[] Renderers;
            public int[] Terrains;
            public long ShapeHash;
        }

        /// <param name="collides">Strips whose chunks get a collider from their level 1 mesh.</param>
        public static RockFaceOutput Create(List<RockFaceStrip> strips, string name, int layer, Material material,
            ShadowCastingMode shadowCasting, int chunkColumns, Vector2 lodTransitions, Func<RockFaceStrip, bool> collides)
        {
            var output = new RockFaceOutput(name, layer);
            UnityEngine.Terrain[] terrains = UnityEngine.Terrain.activeTerrains;
            var blocks = new Dictionary<int, MaterialPropertyBlock>();
            List<Bounds> entranceAreas = LoogaCaveEntrance.DetailAreas();
            foreach (RockFaceStrip strip in strips)
            {
                bool collider = collides(strip);
                int[] groups = ColumnTerrains(strip, terrains);
                foreach (RockFaceChunks.Chunk chunk in RockFaceChunks.Create(strip, chunkColumns, groups, name,
                             collider ? 1 : -1, entranceAreas))
                {
                    output.AddChunk(chunk, collider, material, shadowCasting, lodTransitions, terrains, blocks);
                }
            }
            return output;
        }

        /// <summary>
        /// Puts whole strips into chunks by their position, for example the many small blocks of a rock formation.
        /// </summary>
        /// <param name="chunkLength">Side of the square world XZ area of one chunk, in meters.</param>
        /// <param name="colliders">False for rock that nothing can reach, for example a distant arch.</param>
        public static RockFaceOutput CreateGrouped(List<RockFaceStrip> strips, string name, int layer,
            Material material, ShadowCastingMode shadowCasting, float chunkLength, Vector2 lodTransitions,
            bool colliders = true)
        {
            var output = new RockFaceOutput(name, layer);
            UnityEngine.Terrain[] terrains = UnityEngine.Terrain.activeTerrains;
            var blocks = new Dictionary<int, MaterialPropertyBlock>();
            var cells = new Dictionary<(int, int, int, int), List<RockFaceStrip>>();
            foreach (RockFaceStrip strip in strips)
            {
                Vector3 lowest = strip.Positions[0];
                Vector3 sum = Vector3.zero;
                for (int index = 0; index < strip.VertexCount; index++)
                {
                    Vector3 position = strip.Positions[index];
                    sum += position;
                    if (position.y < lowest.y)
                    {
                        lowest = position;
                    }
                }
                Vector3 centre = sum / Mathf.Max(strip.VertexCount, 1);
                // A chunk holds strips of one terrain, so it can read the ground of that terrain.
                // Tall rock, for example an arch, also splits by height, so each chunk culls alone.
                var key = (Mathf.FloorToInt(centre.x / chunkLength), Mathf.FloorToInt(centre.y / chunkLength),
                    Mathf.FloorToInt(centre.z / chunkLength), LoogaTerrainGroundBlock.FindTerrain(lowest, terrains));
                if (!cells.TryGetValue(key, out List<RockFaceStrip> cell))
                {
                    cell = new List<RockFaceStrip>();
                    cells.Add(key, cell);
                }
                cell.Add(strip);
            }
            foreach (KeyValuePair<(int, int, int, int), List<RockFaceStrip>> cell in cells)
            {
                RockFaceChunks.Chunk? chunk = RockFaceChunks.CreateCombined(cell.Value, cell.Key.Item4, name,
                    colliders ? 1 : -1);
                if (chunk.HasValue)
                {
                    output.AddChunk(chunk.Value, colliders, material, shadowCasting, lodTransitions, terrains, blocks);
                }
            }
            return output;
        }

        // A chunk can be built before the surface binding of its terrain applies, for example on scene load. Its ground
        // then has no layer textures, so the output reads the ground again when the binding applies.
        private RockFaceOutput(string name, int layer)
        {
            Root = new GameObject("Rock " + name) { hideFlags = HideFlags.HideAndDontSave, layer = layer };
            LoogaTerrainSurfaceBinding.SurfaceChanged += RefreshGround;
        }

        // An output of the last domain. The caller fills it.
        private RockFaceOutput(GameObject root)
        {
            Root = root;
            LoogaTerrainSurfaceBinding.SurfaceChanged += RefreshGround;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Stores the output for the next domain. Do not release it: the objects stay over the script reload.
        /// </summary>
        /// <param name="shapeHash">A value of the owner that the next domain gets back.</param>
        public void StoreForReload(string key, ulong shapeHash = 0)
        {
            var meshes = new int[_meshes.Count];
            for (int index = 0; index < meshes.Length; index++)
            {
                meshes[index] = _meshes[index] ? _meshes[index].GetInstanceID() : 0;
            }
            var renderers = new int[_groundRenderers.Count];
            var terrains = new int[_groundRenderers.Count];
            for (int index = 0; index < renderers.Length; index++)
            {
                (UnityEngine.Terrain terrain, MeshRenderer renderer) = _groundRenderers[index];
                renderers[index] = renderer ? renderer.GetInstanceID() : 0;
                terrains[index] = terrain ? terrain.GetInstanceID() : 0;
            }
            GeneratedReloadHandover.Store(key, new ReloadState
            {
                Root = Root ? Root.GetInstanceID() : 0,
                ChunkCount = ChunkCount,
                Bounds = Bounds,
                Meshes = meshes,
                Renderers = renderers,
                Terrains = terrains,
                ShapeHash = unchecked((long)shapeHash)
            });
        }

        /// <summary>
        /// Takes the output of the last domain. Returns null when there is none, or when this domain cannot use it. The
        /// objects of an output that this domain cannot use are destroyed.
        /// </summary>
        public static RockFaceOutput TakeAfterReload(string key, out ulong shapeHash)
        {
            shapeHash = 0;
            if (!GeneratedReloadHandover.TryTake(key, out ReloadState state)) return null;
            var root = GeneratedReloadHandover.Find<GameObject>(state.Root);
            if (!root || !GeneratedReloadHandover.CanAdopt || state.Meshes == null || state.Renderers == null ||
                state.Terrains == null)
            {
                if (root) Destroy(root);
                foreach (int id in state.Meshes ?? Array.Empty<int>())
                {
                    var mesh = GeneratedReloadHandover.Find<Mesh>(id);
                    if (mesh) Destroy(mesh);
                }
                return null;
            }

            var output = new RockFaceOutput(root) { ChunkCount = state.ChunkCount, Bounds = state.Bounds };
            foreach (int id in state.Meshes)
            {
                var mesh = GeneratedReloadHandover.Find<Mesh>(id);
                if (mesh) output._meshes.Add(mesh);
            }
            for (int index = 0; index < state.Renderers.Length; index++)
            {
                var renderer = GeneratedReloadHandover.Find<MeshRenderer>(state.Renderers[index]);
                var terrain = GeneratedReloadHandover.Find<UnityEngine.Terrain>(state.Terrains[index]);
                if (!renderer || !terrain) continue;
                if (!output._groundBlocks.TryGetValue(terrain, out MaterialPropertyBlock block))
                {
                    block = LoogaTerrainGroundBlock.Create(terrain);
                    output._groundBlocks.Add(terrain, block);
                }
                renderer.SetPropertyBlock(block);
                output._groundRenderers.Add((terrain, renderer));
            }
            shapeHash = unchecked((ulong)state.ShapeHash);
            return output;
        }
#endif

#if UNITY_EDITOR
        /// <summary>Starts to keep the mesh data of this build for the cache file.</summary>
        public static void BeginCacheRecording()
        {
            RockFaceChunks.BeginRecording();
        }

        /// <summary>
        /// Ends the recording and writes the cache file. An output with meshes from other sources stays out of the cache.
        /// </summary>
        public void EndCacheRecording(string path, Hash128 key, string meta)
        {
            Dictionary<Mesh, RockFaceChunks.MeshRecord> records = RockFaceChunks.EndRecording();
            var chunks = new List<GeneratedMeshCache.ChunkEntry>(_chunks.Count);
            foreach ((RockFaceChunks.Chunk chunk, bool collider, string terrain) in _chunks)
            {
                var levels = new RockFaceChunks.MeshRecord[chunk.Levels.Length];
                for (int level = 0; level < levels.Length; level++)
                {
                    if (!records.TryGetValue(chunk.Levels[level], out levels[level])) return;
                }
                chunks.Add(new GeneratedMeshCache.ChunkEntry
                {
                    Terrain = terrain, Collider = collider, Bounds = chunk.Bounds, Levels = levels
                });
            }
            _chunks.Clear();
            GeneratedMeshCache.Write(path, key, meta, chunks);
        }
#endif

        /// <summary>
        /// Builds an output from a cache file. Returns null when there is no file or when it does not fit the key. A
        /// player passes no key: it reads the files that the build shipped.
        /// </summary>
        public static RockFaceOutput LoadCache(string path, Hash128? key, string name, int layer, Material material,
            ShadowCastingMode shadowCasting, Vector2 lodTransitions, out string meta)
        {
            UnityEngine.Terrain[] terrains = GeneratedMeshCache.Terrains();
            if (!GeneratedMeshCache.TryRead(path, key, terrains, LoogaCaveEntrance.DetailAreas(), out meta,
                    out List<(RockFaceChunks.Chunk Chunk, bool Collider)> chunks))
            {
                return null;
            }
            var output = new RockFaceOutput(name, layer);
            var blocks = new Dictionary<int, MaterialPropertyBlock>();
            foreach ((RockFaceChunks.Chunk chunk, bool collider) in chunks)
            {
                output.AddChunk(chunk, collider, material, shadowCasting, lodTransitions, terrains, blocks);
            }
#if UNITY_EDITOR
            output._chunks.Clear();
#endif
            return output;
        }

        private void AddChunk(RockFaceChunks.Chunk chunk, bool collider, Material material,
            ShadowCastingMode shadowCasting, Vector2 lodTransitions, UnityEngine.Terrain[] terrains,
            Dictionary<int, MaterialPropertyBlock> blocks)
        {
#if UNITY_EDITOR
            _chunks.Add((chunk, collider, chunk.Group >= 0 ? terrains[chunk.Group].name : string.Empty));
#endif
            int layer = Root.layer;
            MaterialPropertyBlock block = null;
            Material cover = chunk.Group >= 0 ? LoogaTerrainGroundBlock.SurfaceMeshMaterial(terrains[chunk.Group]) : null;
            if (chunk.Group >= 0 && !blocks.TryGetValue(chunk.Group, out block))
            {
                block = LoogaTerrainGroundBlock.Create(terrains[chunk.Group]);
                blocks.Add(chunk.Group, block);
                _groundBlocks[terrains[chunk.Group]] = block;
            }
            var chunkObject = new GameObject("Chunk " + ChunkCount)
            {
                hideFlags = HideFlags.HideAndDontSave, layer = layer
            };
            chunkObject.transform.SetParent(Root.transform, false);
            var lods = new LOD[RockFaceChunks.LevelCount];
            float[] transitions = { lodTransitions.x, lodTransitions.y, 0f };
            for (int level = 0; level < chunk.Levels.Length; level++)
            {
                Mesh mesh = chunk.Levels[level];
                _meshes.Add(mesh);
                var levelObject = new GameObject("LOD" + level) { hideFlags = HideFlags.HideAndDontSave, layer = layer };
                levelObject.transform.SetParent(chunkObject.transform, false);
                levelObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = levelObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = Materials(mesh, material, cover);
                renderer.shadowCastingMode = shadowCasting;
                renderer.receiveShadows = true;
                if (block != null)
                {
                    renderer.SetPropertyBlock(block);
                    _groundRenderers.Add((terrains[chunk.Group], renderer));
                }
                lods[level] = new LOD(transitions[level], new Renderer[] { renderer });
            }
            if (collider)
            {
                chunkObject.AddComponent<MeshCollider>().sharedMesh = chunk.Levels[1];
            }
            LODGroup group = chunkObject.AddComponent<LODGroup>();
            group.SetLODs(lods);
            group.RecalculateBounds();
            if (ChunkCount == 0)
            {
                Bounds = chunk.Bounds;
            }
            else
            {
                Bounds total = Bounds;
                total.Encapsulate(chunk.Bounds);
                Bounds = total;
            }
            ChunkCount++;
        }

        /// <summary>
        /// Adds one chunk from its level meshes, most detailed first, for example a chunk of the block shell of a
        /// fractured face. The output owns the meshes. The chunk reads the ground of the terrain below its centre. The
        /// collider uses level 1.
        /// </summary>
        public void AddLevels(Mesh[] levels, Material material, ShadowCastingMode shadowCasting, bool collider,
            Vector2 lodTransitions)
        {
            int layer = Root.layer;
            var chunkObject = new GameObject("Mesh " + ChunkCount) { hideFlags = HideFlags.HideAndDontSave, layer = layer };
            chunkObject.transform.SetParent(Root.transform, false);
            UnityEngine.Terrain[] terrains = UnityEngine.Terrain.activeTerrains;
            int group = LoogaTerrainGroundBlock.FindTerrain(levels[0].bounds.center, terrains);
            MaterialPropertyBlock block = null;
            if (group >= 0 && !_groundBlocks.TryGetValue(terrains[group], out block))
            {
                block = LoogaTerrainGroundBlock.Create(terrains[group]);
                _groundBlocks[terrains[group]] = block;
            }
            float[] transitions = { lodTransitions.x, lodTransitions.y, 0f };
            var lods = new LOD[levels.Length];
            for (int level = 0; level < levels.Length; level++)
            {
                Mesh mesh = levels[level];
                _meshes.Add(mesh);
                var levelObject = new GameObject("LOD" + level) { hideFlags = HideFlags.HideAndDontSave, layer = layer };
                levelObject.transform.SetParent(chunkObject.transform, false);
                levelObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = levelObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = shadowCasting;
                renderer.receiveShadows = true;
                if (block != null)
                {
                    renderer.SetPropertyBlock(block);
                    _groundRenderers.Add((terrains[group], renderer));
                }
                lods[level] = new LOD(transitions[Mathf.Min(level, transitions.Length - 1)], new Renderer[] { renderer });
            }
            if (collider) chunkObject.AddComponent<MeshCollider>().sharedMesh = levels[Mathf.Min(1, levels.Length - 1)];
            LODGroup lodGroup = chunkObject.AddComponent<LODGroup>();
            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();
            Bounds bounds = levels[0].bounds;
            if (ChunkCount == 0)
            {
                Bounds = bounds;
            }
            else
            {
                Bounds total = Bounds;
                total.Encapsulate(bounds);
                Bounds = total;
            }
            ChunkCount++;
        }

        // Submesh 1 holds the terrain cover. Without a cover material the cover is not drawn.
        private static Material[] Materials(Mesh mesh, Material material, Material cover)
        {
            return mesh.subMeshCount > 1 && cover ? new[] { material, cover } : new[] { material };
        }

        /// <summary>The terrain replaced its surface or its splat maps. Its chunks read the ground again.</summary>
        public void RefreshGround(UnityEngine.Terrain terrain)
        {
            MaterialPropertyBlock block = null;
            Material cover = null;
            foreach ((UnityEngine.Terrain chunkTerrain, MeshRenderer renderer) in _groundRenderers)
            {
                if (chunkTerrain != terrain || !renderer) continue;
                if (block == null)
                {
                    block = LoogaTerrainGroundBlock.Create(terrain);
                    cover = LoogaTerrainGroundBlock.SurfaceMeshMaterial(terrain);
                }
                renderer.SetPropertyBlock(block);
                if (renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh)
                {
                    renderer.sharedMaterials = Materials(filter.sharedMesh, renderer.sharedMaterial, cover);
                }
            }
            if (block != null) _groundBlocks[terrain] = block;
        }

        /// <summary>
        /// Reads the ground again for each terrain that replaced its splat maps, for example in a scene save. The old
        /// maps are destroyed, so the ground blend showed black. Call it in each update.
        /// </summary>
        public void RefreshDestroyedGround()
        {
            UnityEngine.Terrain stale = null;
            foreach (KeyValuePair<UnityEngine.Terrain, MaterialPropertyBlock> entry in _groundBlocks)
            {
                if (!entry.Key || LoogaTerrainGroundBlock.HasControlMaps(entry.Value)) continue;
                stale = entry.Key;
                break;
            }
            // One terrain per call: RefreshGround changes the dictionary.
            if (stale) RefreshGround(stale);
        }

        /// <summary>Adds the renderers of one LOD level to the list: 0 is the most detailed level.</summary>
        public void GetRenderers(int level, List<MeshRenderer> result)
        {
            if (!Root) return;
            foreach (LODGroup group in Root.GetComponentsInChildren<LODGroup>())
            {
                LOD[] lods = group.GetLODs();
                if (level < 0 || level >= lods.Length) continue;
                foreach (Renderer renderer in lods[level].renderers)
                {
                    if (renderer is MeshRenderer meshRenderer) result.Add(meshRenderer);
                }
            }
        }

        public void SetMaterial(Material material)
        {
            if (!Root) return;
            foreach (MeshRenderer renderer in Root.GetComponentsInChildren<MeshRenderer>())
            {
                // The terrain cover keeps its material.
                Material[] materials = renderer.sharedMaterials;
                materials[0] = material;
                renderer.sharedMaterials = materials;
            }
        }

        public void Release()
        {
            LoogaTerrainSurfaceBinding.SurfaceChanged -= RefreshGround;
            if (Root) Destroy(Root);
            foreach (Mesh mesh in _meshes)
            {
                if (mesh) Destroy(mesh);
            }
            _meshes.Clear();
            _groundRenderers.Clear();
            _groundBlocks.Clear();
            Root = null;
            ChunkCount = 0;
        }

        private static void Destroy(UnityEngine.Object target)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
        }

        // Terrain below each grid column: the smallest terrain that holds the lowest point of the column.
        private static int[] ColumnTerrains(RockFaceStrip strip, UnityEngine.Terrain[] terrains)
        {
            var groups = new int[strip.Columns];
            for (int column = 0; column < strip.Columns; column++)
            {
                Vector3 lowest = strip.Positions[strip.Index(column, 0)];
                for (int row = 1; row < strip.Rows; row++)
                {
                    Vector3 position = strip.Positions[strip.Index(column, row)];
                    if (position.y < lowest.y) lowest = position;
                }
                groups[column] = LoogaTerrainGroundBlock.FindTerrain(lowest, terrains);
            }
            return groups;
        }
    }
}
