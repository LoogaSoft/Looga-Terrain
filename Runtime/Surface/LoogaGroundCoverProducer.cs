using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace LoogaSoft.Terrain.Surface
{
    /// <summary>
    /// Makes the ground cover maps from the terrain detail layers and binds them to a
    /// <see cref="LoogaTerrainSurfaceGlobals"/>. Coverage: the canopy closure of all detail prototypes. Canopy colour:
    /// the mean colour of the prototypes, weighted by their coverage. The maps are kept in memory and are not saved.
    /// </summary>
    /// <remarks>
    /// The editor updates the changed area after a Looga stamp update. Call <see cref="MarkDirty"/> after other detail
    /// edits, or <see cref="Rebuild"/> for the full area.
    /// </remarks>
    [ExecuteAlways]
    [AddComponentMenu("Looga/Terrain/Ground Cover From Details")]
    public sealed class LoogaGroundCoverProducer : MonoBehaviour
    {
        /// <summary>A set colour of one detail prototype in place of the colour from its texture.</summary>
        [Serializable]
        public struct PrototypeColor
        {
            [Tooltip("Index of the detail prototype on the terrains.")]
            public int Prototype;
            [Tooltip("Colour of the prototype seen from above.")]
            public Color Color;
        }

        private static readonly List<LoogaGroundCoverProducer> Producers = new();

        [Tooltip("Terrains below this object give the details. Empty: all active terrains.")]
        [SerializeField] private Transform _terrainRoot;
        [Tooltip("The globals component that gets the maps. Empty: the first one in the scenes.")]
        [SerializeField] private LoogaTerrainSurfaceGlobals _globals;
        [Tooltip("Size of one map texel in meters.")]
        [Min(0.25f)]
        [SerializeField] private float _texelSize = 2f;
        [Tooltip("Canopy closure: 1 - exp(-scale x covered ground). The covered ground of a detail cell is the coverage " +
                 "(0 to 1) times the prototype density in coverage mode, or the instance count times the mean " +
                 "instance area per cell area in instance count mode.")]
        [Min(0f)]
        [SerializeField] private float _closureScale = 1f;
        [Tooltip("Share of the ground under the details that takes a plant colour cast where it is bare.")]
        [Range(0f, 1f)]
        [SerializeField] private float _castShare = 1f;
        [Tooltip("Set colours of detail prototypes. The other prototypes use the mean colour of their texture times " +
                 "their tint.")]
        [SerializeField] private List<PrototypeColor> _colorOverrides = new();

        private Texture2D _coverage;
        private Texture2D _canopy;
        private Rect _area;
        // The set texel size, or a larger one where the maps would pass the texture size limit.
        private float _texel = 2f;
        private int _width;
        private int _depth;
        private Rect? _dirty;
        private bool _rebuildAll = true;
        private readonly Dictionary<Texture, Color> _textureColors = new();

        /// <summary>Enabled producers. The editor updater reads them.</summary>
        public static IReadOnlyList<LoogaGroundCoverProducer> Active => Producers;

        /// <summary>The coverage map (R closure, G cast share), or null before the first build.</summary>
        public Texture2D Coverage => _coverage;

        /// <summary>The canopy map (RGB colour times the share, A share), or null before the first build.</summary>
        public Texture2D Canopy => _canopy;

        /// <summary>World area of the maps: minimum X and Z, size X and Z.</summary>
        public Rect Area => _area;

        /// <summary>Duration of the last build or update in seconds.</summary>
        public double LastBuildSeconds { get; private set; }

        #region Built-in
        private void OnEnable()
        {
            Producers.Add(this);
            _rebuildAll = true;
        }

        private void OnValidate()
        {
            _textureColors.Clear();
            _rebuildAll = true;
        }

        private void LateUpdate()
        {
            if (_rebuildAll || _dirty.HasValue)
            {
                Process();
            }
        }

        private void OnDisable()
        {
            Producers.Remove(this);
            LoogaTerrainSurfaceGlobals globals = FindGlobals();
            if (globals)
            {
                globals.ClearGroundCoverOverride();
            }
            Release();
        }
        #endregion

        #region Updates
        /// <summary>Builds the full maps again with the next update.</summary>
        public void Rebuild()
        {
            _textureColors.Clear();
            _rebuildAll = true;
        }

        /// <summary>Updates the maps in a world XZ area with the next update.</summary>
        public void MarkDirty(Rect region)
        {
            if (!_dirty.HasValue)
            {
                _dirty = region;
                return;
            }
            Rect dirty = _dirty.Value;
            _dirty = Rect.MinMaxRect(Mathf.Min(dirty.xMin, region.xMin), Mathf.Min(dirty.yMin, region.yMin),
                Mathf.Max(dirty.xMax, region.xMax), Mathf.Max(dirty.yMax, region.yMax));
        }

        /// <summary>Runs a pending build or update now.</summary>
        public void Process()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            UnityEngine.Terrain[] terrains = FindTerrains();
            Rect area = TerrainArea(terrains);
            bool resized = area != _area || !_coverage || !_canopy;
            if (terrains.Length == 0)
            {
                _rebuildAll = false;
                _dirty = null;
                return;
            }
            RectInt texels;
            if (_rebuildAll || resized)
            {
                Allocate(area);
                texels = new RectInt(0, 0, _width, _depth);
            }
            else
            {
                texels = Texels(_dirty.Value);
            }
            _rebuildAll = false;
            _dirty = null;
            if (texels.width > 0 && texels.height > 0)
            {
                Fill(terrains, texels);
                _coverage.Apply(true, false);
                _canopy.Apply(true, false);
            }
            LoogaTerrainSurfaceGlobals globals = FindGlobals();
            if (globals)
            {
                globals.SetGroundCoverOverride(_coverage, _canopy,
                    new Vector4(_area.xMin, _area.yMin, _area.width, _area.height), 1f);
            }
            LastBuildSeconds = clock.Elapsed.TotalSeconds;
        }
        #endregion

        #region Maps
        private void Allocate(Rect area)
        {
            Release();
            _area = area;
            _texel = Mathf.Max(_texelSize, Mathf.Max(area.width, area.height) / SystemInfo.maxTextureSize);
            if (_texel > _texelSize)
            {
                Debug.LogWarning("Ground cover: the terrain area " + area.size + " needs a texel size of " + _texel +
                    " m to stay in the texture size limit.", this);
            }
            _width = Mathf.Max(1, Mathf.CeilToInt(area.width / _texel));
            _depth = Mathf.Max(1, Mathf.CeilToInt(area.height / _texel));
            _coverage = new Texture2D(_width, _depth, TextureFormat.RG16, true, true)
            {
                name = "Looga Ground Cover Coverage", hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear
            };
            _canopy = new Texture2D(_width, _depth, TextureFormat.RGBA32, true, true)
            {
                name = "Looga Ground Cover Canopy", hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear
            };
        }

        private void Release()
        {
            DestroyResource(_coverage);
            DestroyResource(_canopy);
            _coverage = null;
            _canopy = null;
        }

        // The texels that a world area touches, with one texel to spare, clamped to the maps.
        private RectInt Texels(Rect world)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt((world.xMin - _area.xMin) / _texel) - 1);
            int x1 = Mathf.Min(_width - 1, Mathf.FloorToInt((world.xMax - _area.xMin) / _texel) + 1);
            int z0 = Mathf.Max(0, Mathf.FloorToInt((world.yMin - _area.yMin) / _texel) - 1);
            int z1 = Mathf.Min(_depth - 1, Mathf.FloorToInt((world.yMax - _area.yMin) / _texel) + 1);
            return new RectInt(x0, z0, Mathf.Max(0, x1 - x0 + 1), Mathf.Max(0, z1 - z0 + 1));
        }

        // Each texel takes the mean covered ground of the detail cells whose centres lie in it, and the colour of the
        // prototypes weighted by their covered ground.
        private void Fill(UnityEngine.Terrain[] terrains, RectInt texels)
        {
            int count = texels.width * texels.height;
            var covered = new float[count];
            var color = new Vector3[count];
            var samples = new int[count];
            foreach (UnityEngine.Terrain terrain in terrains)
            {
                AddTerrain(terrain, texels, covered, color, samples);
            }
            var coverage = _coverage.GetPixelData<byte>(0);
            var canopy = _canopy.GetPixelData<byte>(0);
            for (int row = 0; row < texels.height; row++)
            {
                for (int column = 0; column < texels.width; column++)
                {
                    int index = row * texels.width + column;
                    int pixel = (texels.yMin + row) * _width + texels.xMin + column;
                    float ground = samples[index] > 0 ? covered[index] / samples[index] : 0f;
                    float closure = 1f - Mathf.Exp(-_closureScale * ground);
                    coverage[pixel * 2] = ToByte(closure);
                    coverage[pixel * 2 + 1] = ToByte(samples[index] > 0 ? _castShare : 0f);
                    Vector3 mean = covered[index] > 0f ? color[index] / covered[index] : Vector3.zero;
                    canopy[pixel * 4] = ToByte(mean.x);
                    canopy[pixel * 4 + 1] = ToByte(mean.y);
                    canopy[pixel * 4 + 2] = ToByte(mean.z);
                    canopy[pixel * 4 + 3] = (byte)(covered[index] > 0f ? 255 : 0);
                }
            }
        }

        private void AddTerrain(UnityEngine.Terrain terrain, RectInt texels, float[] covered, Vector3[] color, int[] samples)
        {
            TerrainData data = terrain.terrainData;
            int resolution = data.detailResolution;
            if (resolution <= 0) return;
            DetailPrototype[] prototypes = data.detailPrototypes;
            Vector3 origin = terrain.transform.position;
            float cellX = data.size.x / resolution;
            float cellZ = data.size.z / resolution;
            // The detail cells whose centres can lie in the texels, with one cell to spare on each side.
            int x0 = Mathf.Clamp(Mathf.FloorToInt((_area.xMin + texels.xMin * _texel - origin.x) / cellX) - 1, 0, resolution);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((_area.xMin + texels.xMax * _texel - origin.x) / cellX) + 1, 0, resolution);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((_area.yMin + texels.yMin * _texel - origin.z) / cellZ) - 1, 0, resolution);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((_area.yMin + texels.yMax * _texel - origin.z) / cellZ) + 1, 0, resolution);
            if (x1 <= x0 || z1 <= z0) return;
            int cellsX = x1 - x0;
            int cellsZ = z1 - z0;
            // Texel of each detail cell row and column, or -1 outside the texels. The texel rows increase with the cell
            // rows, so each texel row has one range of cell rows: first, and one past the last.
            var texelX = new int[cellsX];
            var texelZ = new int[cellsZ];
            var rowCells = new Vector2Int[texels.height];
            for (int x = 0; x < cellsX; x++)
            {
                int pixel = Mathf.FloorToInt((origin.x + (x0 + x + 0.5f) * cellX - _area.xMin) / _texel);
                texelX[x] = pixel >= texels.xMin && pixel < texels.xMax ? pixel - texels.xMin : -1;
            }
            for (int z = 0; z < cellsZ; z++)
            {
                int pixel = Mathf.FloorToInt((origin.z + (z0 + z + 0.5f) * cellZ - _area.yMin) / _texel);
                texelZ[z] = pixel >= texels.yMin && pixel < texels.yMax ? pixel - texels.yMin : -1;
                if (texelZ[z] < 0) continue;
                Vector2Int range = rowCells[texelZ[z]];
                rowCells[texelZ[z]] = range.y > range.x ? new Vector2Int(range.x, z + 1) : new Vector2Int(z, z + 1);
            }
            // Each cell counts once, also when it has no details.
            for (int z = 0; z < cellsZ; z++)
            {
                if (texelZ[z] < 0) continue;
                for (int x = 0; x < cellsX; x++)
                {
                    if (texelX[x] >= 0) samples[texelZ[z] * texels.width + texelX[x]]++;
                }
            }
            bool instanceCount = data.detailScatterMode == DetailScatterMode.InstanceCountMode;
            float cellArea = cellX * cellZ;
            for (int layer = 0; layer < prototypes.Length; layer++)
            {
                DetailPrototype prototype = prototypes[layer];
                float unit;
                if (instanceCount)
                {
                    float width = (prototype.minWidth + prototype.maxWidth) * 0.5f;
                    unit = width * width / cellArea;
                }
                else
                {
                    unit = prototype.density / 255f;
                }
                if (unit <= 0f) continue;
                Color linear = PrototypeColorOf(layer, prototype);
                var layerColor = new Vector3(linear.r, linear.g, linear.b);
                int[,] values = data.GetDetailLayer(x0, z0, cellsX, cellsZ, layer);
                // Rows of texels are independent, so the cell rows that map to one texel row run on one thread.
                Parallel.For(0, texels.height, row =>
                {
                    for (int z = rowCells[row].x; z < rowCells[row].y; z++)
                    {
                        for (int x = 0; x < cellsX; x++)
                        {
                            int value = values[z, x];
                            if (value <= 0 || texelX[x] < 0) continue;
                            int index = row * texels.width + texelX[x];
                            float ground = value * unit;
                            covered[index] += ground;
                            color[index] += layerColor * ground;
                        }
                    }
                });
            }
        }

        private Color PrototypeColorOf(int index, DetailPrototype prototype)
        {
            foreach (PrototypeColor entry in _colorOverrides)
            {
                if (entry.Prototype == index) return entry.Color.linear;
            }
            Texture texture;
            Color tint;
            Renderer renderer = prototype.usePrototypeMesh && prototype.prototype
                ? prototype.prototype.GetComponentInChildren<Renderer>(true) : null;
            if (renderer && renderer.sharedMaterial)
            {
                Material material = renderer.sharedMaterial;
                texture = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : material.mainTexture;
                tint = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor")
                    : material.HasProperty("_Color") ? material.color : Color.white;
            }
            else
            {
                texture = prototype.prototypeTexture;
                tint = Color.Lerp(prototype.healthyColor, prototype.dryColor, 0.5f);
            }
            return MeanColor(texture) * tint.linear;
        }

        // Mean linear colour of the opaque texels of a texture, read on the GPU so the texture need not be readable.
        private Color MeanColor(Texture texture)
        {
            if (!texture) return Color.white;
            if (_textureColors.TryGetValue(texture, out Color cached)) return cached;
            const int size = 128;
            RenderTexture target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            Graphics.Blit(texture, target);
            var readback = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
            // The pool can return the texture that was active before. It must not stay active after the release.
            RenderTexture.active = previous != target ? previous : null;
            RenderTexture.ReleaseTemporary(target);
            Color[] pixels = readback.GetPixels();
            DestroyResource(readback);
            Vector3 sum = Vector3.zero;
            float weight = 0f;
            foreach (Color pixel in pixels)
            {
                sum += new Vector3(pixel.r, pixel.g, pixel.b) * pixel.a;
                weight += pixel.a;
            }
            Vector3 mean = weight > 0f ? sum / weight : Vector3.one;
            var result = new Color(mean.x, mean.y, mean.z, 1f);
            _textureColors[texture] = result;
            return result;
        }

        private static byte ToByte(float value)
        {
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);
        }
        #endregion

        #region Scene
        private UnityEngine.Terrain[] FindTerrains()
        {
            UnityEngine.Terrain[] terrains = _terrainRoot
                ? _terrainRoot.GetComponentsInChildren<UnityEngine.Terrain>()
                : FindObjectsByType<UnityEngine.Terrain>(FindObjectsSortMode.None);
            // Terrains without detail prototypes, such as a backdrop, give no ground cover and do not widen the maps.
            return Array.FindAll(terrains, terrain => terrain.isActiveAndEnabled && terrain.terrainData &&
                terrain.terrainData.detailPrototypes.Length > 0);
        }

        private static Rect TerrainArea(UnityEngine.Terrain[] terrains)
        {
            if (terrains.Length == 0) return default;
            Vector3 min = Vector3.positiveInfinity;
            Vector3 max = Vector3.negativeInfinity;
            foreach (UnityEngine.Terrain terrain in terrains)
            {
                Vector3 position = terrain.transform.position;
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position + terrain.terrainData.size);
            }
            return Rect.MinMaxRect(min.x, min.z, max.x, max.z);
        }

        private LoogaTerrainSurfaceGlobals FindGlobals()
        {
            return _globals ? _globals : FindAnyObjectByType<LoogaTerrainSurfaceGlobals>();
        }

        private static void DestroyResource(UnityEngine.Object resource)
        {
            if (!resource) return;
            if (Application.isPlaying)
            {
                Destroy(resource);
            }
            else
            {
                DestroyImmediate(resource);
            }
        }
        #endregion
    }
}
