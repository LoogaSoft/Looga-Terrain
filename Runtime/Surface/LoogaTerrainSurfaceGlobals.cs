using UnityEngine;

namespace LoogaSoft.Terrain.Surface
{
    /// <summary>
    /// Binds the world data of the terrain surfaces as global shader properties: the world colour map and the ground
    /// cover maps. Terrain, grass and other shaders read the same values. Use one component per scene.
    /// </summary>
    /// <remarks>
    /// The serialized maps are the saved data. A data producer, for example a live editor tool, can replace them in
    /// memory with the Set...Override methods. Overrides are not saved and stay until <see cref="ClearOverrides"/>.
    /// </remarks>
    [ExecuteAlways]
    [AddComponentMenu("Looga/Terrain/Terrain Surface Globals")]
    public sealed class LoogaTerrainSurfaceGlobals : MonoBehaviour
    {
        /// <summary>
        /// A world colour map in tiles: one array slice per square cell, and an index texture (RFloat) with the slice of
        /// each cell, or -1 for a cell without a slice.
        /// </summary>
        public struct ColorTiles
        {
            public Texture2DArray Tiles;
            public Texture2D Index;
            /// <summary>World X and Z of the minimum corner of the cell of index texel (0, 0).</summary>
            public Vector2 Minimum;
            /// <summary>Cell size in meters.</summary>
            public float CellSize;
            /// <summary>Border texels around the cell area of each slice.</summary>
            public int BorderTexels;
            /// <summary>Texels of the cell area of each slice, without the border.</summary>
            public int CellTexels;

            public bool IsValid => Tiles && Index && CellSize > 0f && CellTexels > 0;
        }

        private static readonly int WorldColorStateId = Shader.PropertyToID("_LoogaWorldColorState");
        private static readonly int WorldColorMapId = Shader.PropertyToID("_LoogaWorldColorMap");
        private static readonly int WorldColorAreaId = Shader.PropertyToID("_LoogaWorldColorArea");
        private static readonly int WorldColorTilesId = Shader.PropertyToID("_LoogaWorldColorTiles");
        private static readonly int WorldColorTileIndexId = Shader.PropertyToID("_LoogaWorldColorTileIndex");
        private static readonly int WorldColorTileGridId = Shader.PropertyToID("_LoogaWorldColorTileGrid");
        private static readonly int WorldColorTileSliceId = Shader.PropertyToID("_LoogaWorldColorTileSlice");
        private static readonly int GroundCoverStateId = Shader.PropertyToID("_LoogaGroundCoverState");
        private static readonly int GroundCoverMapId = Shader.PropertyToID("_LoogaGroundCoverMap");
        private static readonly int GroundCoverCanopyId = Shader.PropertyToID("_LoogaGroundCoverCanopy");
        private static readonly int GroundCoverAreaId = Shader.PropertyToID("_LoogaGroundCoverArea");
        private static readonly int GroundCoverMapShadeId = Shader.PropertyToID("_LoogaGroundCoverMapShade");
        private static readonly int GroundCoverBareId = Shader.PropertyToID("_LoogaGroundCoverBare");

        [Header("World Colour Map")]
        [Tooltip("Colour over a world area in linear RGB. Surfaces can tint the terrain with it, and the ground cover " +
                 "uses it as the colour of plants without their own colour.")]
        [SerializeField] private Texture2D _worldColorMap;
        [Tooltip("World area of the colour map: minimum X, minimum Z, size X, size Z, in meters.")]
        [SerializeField] private Vector4 _worldColorArea;
        [Tooltip("Scale of the map colour.")]
        [Min(0f)]
        [SerializeField] private float _worldColorScale = 1f;

        [Header("Ground Cover")]
        [Tooltip("R: plant coverage seen from above, 0 to 1. G: share of the ground that takes a plant colour cast " +
                 "where it is bare.")]
        [SerializeField] private Texture2D _groundCoverMap;
        [Tooltip("Optional. A: share of the plants with their own colour. RGB: their linear colour times that share. " +
                 "The other plants take the world colour map. Without it, all plants take the world colour map.")]
        [SerializeField] private Texture2D _groundCoverCanopy;
        [Tooltip("World area of the ground cover maps: minimum X, minimum Z, size X, size Z, in meters.")]
        [SerializeField] private Vector4 _groundCoverArea;
        [Tooltip("Scale of the canopy colour.")]
        [Min(0f)]
        [SerializeField] private float _groundCoverColorScale = 1f;
        [Tooltip("Canopy colour, in linear RGB, of the layers with bare far cover where the maps have no plants. Match " +
                 "the mean far canopy colour of the plant areas.")]
        [ColorUsage(false)]
        [SerializeField] private Color _bareCanopyColor = new(0.05f, 0.08f, 0.03f, 1f);

        // In-memory data from a producer. Not saved.
        private bool _worldColorOverridden;
        private Texture2D _overrideColorMap;
        private Vector4 _overrideColorArea;
        private ColorTiles _overrideColorTiles;
        private float _overrideColorScale = 1f;
        private bool _groundCoverOverridden;
        private Texture _overrideCoverMap;
        private Texture _overrideCanopy;
        private Vector4 _overrideCoverArea;
        private float _overrideCoverColorScale = 1f;
        // Shading of the world colour map part of the canopy colour: RGB factor, A saturation. One = the map colour.
        private Vector4 _coverMapShade = Vector4.one;
        private ILoogaGroundCoverComposer _composer;

        /// <summary>True when a world colour map is bound.</summary>
        public bool HasWorldColor { get; private set; }

        /// <summary>True when ground cover maps are bound.</summary>
        public bool HasGroundCover { get; private set; }

        /// <summary>
        /// False binds the ground cover maps as given, without the composer, for example saved maps that already hold
        /// the plants of the composer. True by default.
        /// </summary>
        public bool ComposeGroundCover { get; set; } = true;

        /// <summary>The bound ground cover coverage map (composed when a composer runs), or null.</summary>
        public Texture BoundCoverage { get; private set; }

        /// <summary>The bound ground cover canopy map (composed when a composer runs), or null.</summary>
        public Texture BoundCanopy { get; private set; }

        #region Built-in
        private void OnEnable()
        {
            Bind();
        }

        private void OnValidate()
        {
            if (isActiveAndEnabled)
            {
                Bind();
            }
        }

        // Another component can set the same globals, and a scene load clears them. Binding each frame keeps them.
        private void LateUpdate()
        {
            Bind();
        }

        private void OnDisable()
        {
            Shader.SetGlobalVector(WorldColorStateId, Vector4.zero);
            Shader.SetGlobalVector(GroundCoverStateId, Vector4.zero);
            HasWorldColor = false;
            HasGroundCover = false;
        }
        #endregion

        #region Overrides
        /// <summary>Uses an in-memory world colour map over an area in place of the saved map. Null clears the map.</summary>
        /// <param name="area">Minimum X, minimum Z, size X and size Z, in meters.</param>
        public void SetWorldColorOverride(Texture2D map, Vector4 area, float scale)
        {
            _worldColorOverridden = true;
            _overrideColorMap = map;
            _overrideColorArea = area;
            _overrideColorTiles = default;
            _overrideColorScale = scale;
            Bind();
        }

        /// <summary>Uses an in-memory tiled world colour map in place of the saved map.</summary>
        public void SetWorldColorOverride(ColorTiles tiles, float scale)
        {
            _worldColorOverridden = true;
            _overrideColorMap = null;
            _overrideColorTiles = tiles;
            _overrideColorScale = scale;
            Bind();
        }

        /// <summary>
        /// Uses in-memory ground cover maps in place of the saved maps. Null coverage clears the ground cover.
        /// </summary>
        /// <param name="coverage">R coverage, G cast share.</param>
        /// <param name="canopy">Optional. A own-colour share, RGB own colour times the share.</param>
        /// <param name="area">Minimum X, minimum Z, size X and size Z, in meters.</param>
        public void SetGroundCoverOverride(Texture coverage, Texture canopy, Vector4 area, float colorScale)
        {
            _groundCoverOverridden = true;
            _overrideCoverMap = coverage;
            _overrideCanopy = canopy;
            _overrideCoverArea = area;
            _overrideCoverColorScale = colorScale;
            Bind();
        }

        /// <summary>
        /// Sets how the plants that take the world colour map shade it, so the canopy colour matches the plants: the
        /// map colour times the factor, then the saturation (1 = no change). A plant renderer that brightens, tints
        /// or saturates the map colour gives its values here. The default is factor 1 and saturation 1.
        /// </summary>
        public void SetGroundCoverMapShade(Vector3 factor, float saturation)
        {
            _coverMapShade = new Vector4(factor.x, factor.y, factor.z, saturation);
            Bind();
        }

        /// <summary>
        /// Sets the composer that adds its plants to the ground cover maps before they are bound. Null removes it. A
        /// composer that is disabled must remove itself.
        /// </summary>
        public void SetGroundCoverComposer(ILoogaGroundCoverComposer composer)
        {
            _composer = composer;
            Bind();
        }

        /// <summary>The composer of the ground cover maps, or null.</summary>
        public ILoogaGroundCoverComposer GroundCoverComposer => _composer;

        /// <summary>
        /// Canopy colour, in linear RGB, of the layers with bare far cover where the maps have no plants.
        /// </summary>
        public Color BareCanopyColor
        {
            get => _bareCanopyColor;
            set
            {
                _bareCanopyColor = value;
                Bind();
            }
        }

        /// <summary>Returns to the saved ground cover maps. The world colour map override stays.</summary>
        public void ClearGroundCoverOverride()
        {
            _groundCoverOverridden = false;
            _overrideCoverMap = null;
            _overrideCanopy = null;
            Bind();
        }

        /// <summary>Returns to the saved maps.</summary>
        public void ClearOverrides()
        {
            _worldColorOverridden = false;
            _overrideColorMap = null;
            _overrideColorTiles = default;
            _groundCoverOverridden = false;
            _overrideCoverMap = null;
            _overrideCanopy = null;
            Bind();
        }
        #endregion

        #region Binding
        private void Bind()
        {
            if (!isActiveAndEnabled) return;
            BindWorldColor();
            BindGroundCover();
        }

        // The bound maps of the last binding. Cache pages read them, so another map invalidates the pages.
        [System.NonSerialized] private Object _boundWorldColor;
        [System.NonSerialized] private Vector4 _boundWorldColorArea;
        [System.NonSerialized] private float _boundWorldColorScale;
        [System.NonSerialized] private Texture _boundCoverage;
        [System.NonSerialized] private Texture _boundCanopy;
        [System.NonSerialized] private Vector4 _boundCoverArea;

        private void BindWorldColor()
        {
            ColorTiles tiles = _worldColorOverridden ? _overrideColorTiles : default;
            Texture2D map = _worldColorOverridden ? _overrideColorMap : _worldColorMap;
            Vector4 area = _worldColorOverridden ? _overrideColorArea : _worldColorArea;
            float scale = _worldColorOverridden ? _overrideColorScale : _worldColorScale;
            Object bound = tiles.IsValid ? tiles.Tiles : map;
            if (bound != _boundWorldColor || area != _boundWorldColorArea || !Mathf.Approximately(scale, _boundWorldColorScale))
            {
                _boundWorldColor = bound;
                _boundWorldColorArea = area;
                _boundWorldColorScale = scale;
                TerrainVirtualTexture.InvalidateWorldArea(null);
            }
            if (tiles.IsValid)
            {
                float sliceSize = tiles.CellTexels + 2f * tiles.BorderTexels;
                Shader.SetGlobalTexture(WorldColorTilesId, tiles.Tiles);
                Shader.SetGlobalTexture(WorldColorTileIndexId, tiles.Index);
                Shader.SetGlobalVector(WorldColorTileGridId, new Vector4(tiles.Minimum.x, tiles.Minimum.y,
                    1f / tiles.CellSize, 0f));
                Shader.SetGlobalVector(WorldColorTileSliceId, new Vector4(tiles.BorderTexels / sliceSize,
                    tiles.CellTexels / sliceSize, tiles.Index.width, tiles.Index.height));
                Shader.SetGlobalVector(WorldColorStateId, new Vector4(2f, scale, 0f, 0f));
                HasWorldColor = true;
                return;
            }
            HasWorldColor = map && area.z > 0f && area.w > 0f;
            if (HasWorldColor)
            {
                Shader.SetGlobalTexture(WorldColorMapId, map);
                Shader.SetGlobalVector(WorldColorAreaId, InverseArea(area));
            }
            Shader.SetGlobalVector(WorldColorStateId, new Vector4(HasWorldColor ? 1f : 0f, scale, 0f, 0f));
        }

        private void BindGroundCover()
        {
            Texture coverage = _groundCoverOverridden ? _overrideCoverMap : _groundCoverMap;
            Texture canopy = _groundCoverOverridden ? _overrideCanopy : _groundCoverCanopy;
            Vector4 area = _groundCoverOverridden ? _overrideCoverArea : _groundCoverArea;
            float scale = _groundCoverOverridden ? _overrideCoverColorScale : _groundCoverColorScale;
            HasGroundCover = coverage && area.z > 0f && area.w > 0f;
            if (HasGroundCover && ComposeGroundCover && _composer != null &&
                _composer.Compose(coverage, canopy, area, scale, out Texture composedCoverage,
                    out Texture composedCanopy))
            {
                coverage = composedCoverage;
                canopy = composedCanopy;
            }
            BoundCoverage = HasGroundCover ? coverage : null;
            BoundCanopy = HasGroundCover ? canopy : null;
            if (BoundCoverage != _boundCoverage || BoundCanopy != _boundCanopy || area != _boundCoverArea)
            {
                _boundCoverage = BoundCoverage;
                _boundCanopy = BoundCanopy;
                _boundCoverArea = area;
                TerrainVirtualTexture.InvalidateWorldArea(null);
            }
            if (HasGroundCover)
            {
                Shader.SetGlobalTexture(GroundCoverMapId, coverage);
                // A black canopy map gives no plant its own colour, so all plants take the world colour map.
                Shader.SetGlobalTexture(GroundCoverCanopyId, canopy ? canopy : Texture2D.blackTexture);
                Shader.SetGlobalVector(GroundCoverAreaId, InverseArea(area));
            }
            Shader.SetGlobalVector(GroundCoverStateId, new Vector4(HasGroundCover ? 1f : 0f, scale, 0f, 0f));
            Shader.SetGlobalVector(GroundCoverMapShadeId, _coverMapShade);
            Shader.SetGlobalVector(GroundCoverBareId, new Vector4(_bareCanopyColor.r, _bareCanopyColor.g,
                _bareCanopyColor.b, 1f));
        }

        private static Vector4 InverseArea(Vector4 area)
        {
            return new Vector4(area.x, area.y, 1f / area.z, 1f / area.w);
        }
        #endregion
    }
}
