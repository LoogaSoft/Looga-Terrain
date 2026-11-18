using System.Collections.Generic;
using LoogaSoft.Terrain.Stamps;
using LoogaSoft.Terrain.Surface;
using UnityEngine;

namespace LoogaSoft.Terrain.Roads
{
    /// <summary>
    /// The road network of a stamp world: the road classes, the pads that the roads join, the bridges, and the asphalt
    /// slabs with their gravel shoulder and plant clear. Put it on the stamp group that holds the road spline paths
    /// (<see cref="LoogaRoad"/>). The editor keeps the slabs, the masks and the bridges up to date with the roads.
    /// </summary>
    /// <remarks>
    /// The road group runs after the pads in the stamp order, so each road stays level across its width and its
    /// embankment blends into the pad edges. Each slab has one submesh for each terrain tile below it. The network
    /// gives each submesh the terrain ground of its tile (control maps and terrain surface layers), so a road shader
    /// can blend its edges into the terrain. Property blocks are not saved, so the network sets them again when it
    /// is enabled and when a terrain surface changes.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga/Roads/Road Network")]
    public sealed class LoogaRoadNetwork : MonoBehaviour
    {
        [Tooltip("Road classes. The first class is the primary class: junction shapes and pad contacts use its width " +
                 "and profile.")]
        [SerializeField] private List<LoogaRoadClass> _roadClasses = new();

        [Header("Pads")]
        [Tooltip("Stamp group of the pads (LoogaRoadPad). The pads run before the roads.")]
        [SerializeField] private Transform _padRoot;
        [Tooltip("Snapshot at the start of the pad group. Auto level reads the ground there, before all pads.")]
        [SerializeField] private LoogaHeightSnapshot _padGround;
        [Tooltip("Name prefix of the pads. Pad ids and names of the planner pages leave it out.")]
        [SerializeField] private string _padPrefix = "";
        [Tooltip("Road knots within this distance past a pad outline move with the pad, in meters.")]
        [Min(0f)]
        [SerializeField] private float _padLinkMargin = 10f;

        [Header("Bridges")]
        [Tooltip("Parent of the bridges (LoogaRoadBridge).")]
        [SerializeField] private Transform _bridgeRoot;
        [SerializeField] private Material _deckMaterial;
        [SerializeField] private Material _railingMaterial;
        [Tooltip("Generated Min spline path that keeps the ground under the bridge decks. Empty creates it.")]
        [SerializeField] private LoogaSplinePath _bridgeClearance;
        [Tooltip("Child of this group that stays last in the stamp order, for example a water guard.")]
        [SerializeField] private Transform _lastStamp;
        [Tooltip("Parent of the rock faces that get bridge openings. Empty skips the openings.")]
        [SerializeField] private Transform _rockFaceRoot;

        [Header("Slabs")]
        [Tooltip("Parent of the generated asphalt slab objects.")]
        [SerializeField] private Transform _slabRoot;
        [SerializeField] private Material _slabMaterial;
        [Tooltip("Texture stamp that paints the gravel shoulder beside the slabs (texture source mask).")]
        [SerializeField] private LoogaTextureStamp _shoulderStamp;
        [Tooltip("Clear stamp that keeps plants off the slabs (texture source mask).")]
        [SerializeField] private LoogaClearStamp _clearStamp;
        [SerializeField] private TerrainLayer _shoulderLayer;
        [Tooltip("Asset folder of the slab meshes (Meshes subfolder) and the saved masks.")]
        [SerializeField] private string _outputFolder = "Assets/Roads";
        [Tooltip("Name prefix of the slab mesh assets.")]
        [SerializeField] private string _meshPrefix = "Looga Road - ";

        public IReadOnlyList<LoogaRoadClass> RoadClasses => _roadClasses;
        /// <summary>The first road class. Junction shapes and pad contacts use its width and profile.</summary>
        public LoogaRoadClass PrimaryClass => _roadClasses.Count > 0 ? _roadClasses[0] : null;
        public Transform PadRoot => _padRoot;
        public LoogaHeightSnapshot PadGround => _padGround;
        public string PadPrefix => _padPrefix;
        public float PadLinkMargin => _padLinkMargin;
        public Transform BridgeRoot => _bridgeRoot;
        public Material DeckMaterial => _deckMaterial;
        public Material RailingMaterial => _railingMaterial;
        public LoogaSplinePath BridgeClearance => _bridgeClearance;
        public Transform LastStamp => _lastStamp;
        public Transform RockFaceRoot => _rockFaceRoot;
        public Transform SlabRoot => _slabRoot;
        public Material SlabMaterial => _slabMaterial;
        public LoogaTextureStamp ShoulderStamp => _shoulderStamp;
        public LoogaClearStamp ClearStamp => _clearStamp;
        public TerrainLayer ShoulderLayer => _shoulderLayer;
        public string OutputFolder => _outputFolder;
        public string MeshPrefix => _meshPrefix;
        public LoogaStampWorld World => GetComponentInParent<LoogaStampWorld>();

        // True until the slabs have their terrain ground. The terrains can enable after the network.
        private bool _groundPending;
        // The terrain ground block of each terrain, shared by the slab submeshes on it.
        private readonly Dictionary<UnityEngine.Terrain, MaterialPropertyBlock> _groundBlocks = new();
        private MaterialPropertyBlock _noGround;

        #region Terrain Ground
        private void OnEnable()
        {
            LoogaTerrainSurfaceBinding.SurfaceChanged += OnTerrainSurfaceChanged;
            _groundPending = true;
        }

        private void OnDisable()
        {
            LoogaTerrainSurfaceBinding.SurfaceChanged -= OnTerrainSurfaceChanged;
        }

        private void Update()
        {
            if (!_groundPending)
            {
                // A terrain can replace its splat maps. The slabs on it then read the new maps.
                foreach (MaterialPropertyBlock block in _groundBlocks.Values)
                {
                    if (LoogaTerrainGroundBlock.HasControlMaps(block)) continue;
                    _groundPending = true;
                    break;
                }
            }
            if (_groundPending) ApplyGround();
        }

        private void OnTerrainSurfaceChanged(UnityEngine.Terrain terrain)
        {
            _groundPending = true;
        }

        /// <summary>Gives every slab submesh the terrain ground of the tile below it.</summary>
        public void ApplyGround()
        {
            if (!_slabRoot) return;
            UnityEngine.Terrain[] terrains = GroundTerrains();
            if (terrains.Length == 0) return;
            _groundPending = false;
            _groundBlocks.Clear();
            foreach (MeshRenderer slab in _slabRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                ApplyGround(slab, terrains);
            }
        }

        /// <summary>Gives each submesh of one slab the terrain ground of the tile below it.</summary>
        public void ApplyGround(MeshRenderer slab)
        {
            UnityEngine.Terrain[] terrains = GroundTerrains();
            if (terrains.Length > 0) ApplyGround(slab, terrains);
        }

        private void ApplyGround(MeshRenderer slab, UnityEngine.Terrain[] terrains)
        {
            if (!slab.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) return;
            Mesh mesh = filter.sharedMesh;
            // In a player, static batching replaces the slab mesh with the combined mesh of its batch. The slab then
            // draws its own range of submeshes, and the combined vertices are in world space. A property block index is
            // a material index of the slab, so the loop stops at the material count.
            bool batched = slab.isPartOfStaticBatch;
            int first = batched ? slab.subMeshStartIndex : 0;
            int count = Mathf.Min(slab.sharedMaterials.Length, mesh.subMeshCount - first);
            for (int index = 0; index < count; index++)
            {
                Bounds bounds = mesh.GetSubMesh(first + index).bounds;
                Vector3 center = batched ? bounds.center : slab.transform.TransformPoint(bounds.center);
                int found = LoogaTerrainGroundBlock.FindTerrain(center, terrains);
                MaterialPropertyBlock block;
                if (found < 0)
                {
                    block = _noGround ??= new MaterialPropertyBlock();
                }
                else if (!_groundBlocks.TryGetValue(terrains[found], out block))
                {
                    block = LoogaTerrainGroundBlock.Create(terrains[found]);
                    _groundBlocks.Add(terrains[found], block);
                }
                slab.SetPropertyBlock(block, index);
            }
        }

        // The active terrains of the stamp world.
        private UnityEngine.Terrain[] GroundTerrains()
        {
            LoogaStampWorld world = World;
            if (!world) return new UnityEngine.Terrain[0];
            var terrains = new List<UnityEngine.Terrain>();
            foreach (UnityEngine.Terrain terrain in world.Terrains)
            {
                if (terrain && terrain.isActiveAndEnabled) terrains.Add(terrain);
            }
            return terrains.ToArray();
        }
        #endregion

        #region Classes
        /// <summary>The road class with the name, or null.</summary>
        public LoogaRoadClass GetClass(string className)
        {
            foreach (LoogaRoadClass roadClass in _roadClasses)
            {
                if (roadClass.Name == className) return roadClass;
            }
            return null;
        }

        /// <summary>True when a paved road class paints the layer. Pads that paint it get slabs.</summary>
        public bool IsPavedLayer(TerrainLayer layer)
        {
            if (!layer) return false;
            foreach (LoogaRoadClass roadClass in _roadClasses)
            {
                if (roadClass.Paved && roadClass.SurfaceLayer == layer) return true;
            }
            return false;
        }

        /// <summary>The active roads of the network, in hierarchy order.</summary>
        public List<LoogaRoad> GetRoads()
        {
            var roads = new List<LoogaRoad>();
            GetComponentsInChildren(false, roads);
            return roads;
        }
        #endregion

        #region Ground
        /// <summary>
        /// Height of the first stamp world terrain at a world XZ position. Throws outside all terrains.
        /// </summary>
        public float Ground(float x, float z)
        {
            LoogaStampWorld world = World;
            if (!world) throw new System.InvalidOperationException(name + " is not under a stamp world.");
            IReadOnlyList<UnityEngine.Terrain> terrains = world.Terrains;
            for (int index = 0; index < terrains.Count; index++)
            {
                UnityEngine.Terrain terrain = terrains[index];
                if (!terrain || !terrain.isActiveAndEnabled) continue;
                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                if (x >= origin.x && x <= origin.x + size.x && z >= origin.z && z <= origin.z + size.z)
                {
                    return terrain.SampleHeight(new Vector3(x, 0f, z)) + origin.y;
                }
            }
            throw new System.InvalidOperationException("No terrain at " + x + ", " + z + ".");
        }
        #endregion

        #region Setup
        /// <summary>
        /// Sets the references of the network. Editor tools call it when they set up or migrate a network.
        /// </summary>
        public void Configure(List<LoogaRoadClass> roadClasses, Transform padRoot, LoogaHeightSnapshot padGround,
            Transform bridgeRoot, Material deckMaterial, Material railingMaterial, Transform lastStamp,
            Transform rockFaceRoot, Transform slabRoot, Material slabMaterial, LoogaTextureStamp shoulderStamp,
            LoogaClearStamp clearStamp, TerrainLayer shoulderLayer, string outputFolder, string meshPrefix,
            string padPrefix)
        {
            _roadClasses = roadClasses;
            _padRoot = padRoot;
            _padPrefix = padPrefix;
            _padGround = padGround;
            _bridgeRoot = bridgeRoot;
            _deckMaterial = deckMaterial;
            _railingMaterial = railingMaterial;
            _lastStamp = lastStamp;
            _rockFaceRoot = rockFaceRoot;
            _slabRoot = slabRoot;
            _slabMaterial = slabMaterial;
            _shoulderStamp = shoulderStamp;
            _clearStamp = clearStamp;
            _shoulderLayer = shoulderLayer;
            _outputFolder = outputFolder;
            _meshPrefix = meshPrefix;
        }

        public void SetBridgeClearance(LoogaSplinePath clearance)
        {
            _bridgeClearance = clearance;
        }

        public void SetBridgeRoot(Transform bridgeRoot)
        {
            _bridgeRoot = bridgeRoot;
        }

        public void SetSlabRoot(Transform slabRoot)
        {
            _slabRoot = slabRoot;
        }

        public void SetPadGround(LoogaHeightSnapshot padGround)
        {
            _padGround = padGround;
        }
        #endregion
    }
}
