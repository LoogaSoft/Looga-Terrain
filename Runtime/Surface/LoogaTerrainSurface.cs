using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Surface
{
    /// <summary>
    /// Look of a Looga terrain surface: the layers in control-map order, the per-layer settings, the global
    /// settings and the texture arrays. One asset describes one look, for example the main terrain or a backdrop.
    /// </summary>
    [CreateAssetMenu(menuName = "Looga/Terrain/Terrain Surface", fileName = "Terrain Surface")]
    public sealed class LoogaTerrainSurface : ScriptableObject
    {
        /// <summary>How the shader mixes the layers of a pixel.</summary>
        public enum BlendMode
        {
            /// <summary>Weights from the control maps, scaled so that they add up to one.</summary>
            NormalizedLinear = 0,
            /// <summary>The layer with the higher texture height wins.</summary>
            Height = 1,
            /// <summary>Height blend near the camera that fades to normalized linear with distance.</summary>
            Hybrid = 2,
            /// <summary>
            /// The weight plus the height decides the winner. The height can move an edge only within the height
            /// reach, so a layer does not show far inside another layer.
            /// </summary>
            HeightOffset = 3,
            /// <summary>
            /// Height offset with a world edge noise. The transition has the same width in screen pixels at every
            /// distance, so the edges stay sharp. The global contrast is the smallest transition.
            /// </summary>
            CrispEdge = 4,
            /// <summary>Normalized linear that keeps the texture contrast in the transitions.</summary>
            VariancePreserving = 5
        }

        /// <summary>Texture packing of the arrays.</summary>
        public enum PackingLayout
        {
            /// <summary>Albedo + height (DXT5) and normal + smoothness + occlusion (DXT5). Two arrays.</summary>
            Compact = 0,
            /// <summary>Albedo + height (BC7), normal (BC5) and smoothness + occlusion + metallic (BC7). Three arrays.</summary>
            Quality = 1
        }

        /// <summary>Encoder effort for BC7 arrays. The BC5 and DXT5 arrays do not use it.</summary>
        public enum CompressionQuality
        {
            Fast = 0,
            Normal = 1,
            Best = 2
        }

        /// <summary>Color channel of a source texture.</summary>
        public enum TextureChannel
        {
            R = 0,
            G = 1,
            B = 2,
            A = 3
        }

        /// <summary>One unlit view of a shading input. Keep the values the same as LoogaTerrainSurfaceInput.hlsl.</summary>
        public enum DebugView
        {
            None = 0,
            Albedo = 1,
            Normal = 2,
            SmoothnessOcclusion = 3,
            Height = 4,
            LayerWeights = 5,
            TopLayer = 6,
            TriplanarWeights = 7,
            SampleCount = 8,
            WorldColor = 10,
            GroundCover = 11
        }

        /// <summary>How the triplanar projections blend.</summary>
        public enum TriplanarMode
        {
            /// <summary>Weights from the normal only.</summary>
            Plain = 0,
            /// <summary>The projection with the higher texture height wins. The transitions are sharper.</summary>
            HeightBlended = 1
        }

        /// <summary>
        /// Side projections on steep ground, so the layer textures do not stretch. Gentle slopes use only the top
        /// projection, which needs one set of texture samples.
        /// </summary>
        [Serializable]
        public sealed class TriplanarSettings
        {
            [SerializeField] private bool _enabled = true;
            [SerializeField] private TriplanarMode _mode = TriplanarMode.Plain;
            [Tooltip("Exponent of the projection weights. Higher values give shorter transitions between projections.")]
            [Range(1f, 8f)]
            [SerializeField] private float _contrast = 4f;
            [Tooltip("Height blended mode: width of the transition. Small values give sharp edges.")]
            [Range(0.01f, 1f)]
            [SerializeField] private float _heightBlendContrast = 0.2f;
            [Tooltip("Below this slope in degrees only the top projection is used.")]
            [Range(0f, 60f)]
            [SerializeField] private float _slopeStart = 12f;
            [Tooltip("Slope range in degrees over which the side projections fade in.")]
            [Range(1f, 45f)]
            [SerializeField] private float _slopeRange = 10f;
            [Tooltip("Where the triangle normal differs from the smooth terrain normal by more than this angle in " +
                     "degrees, the projection weights use the triangle normal. This keeps the projection edge on sharp " +
                     "slope changes. 0 = off.")]
            [Range(0f, 45f)]
            [SerializeField] private float _faceNormalAngle;

            public bool Enabled { get => _enabled; set => _enabled = value; }
            public TriplanarMode Mode { get => _mode; set => _mode = value; }
            public float Contrast { get => _contrast; set => _contrast = Mathf.Clamp(value, 1f, 8f); }
            public float HeightBlendContrast { get => _heightBlendContrast; set => _heightBlendContrast = Mathf.Clamp(value, 0.01f, 1f); }
            public float SlopeStart { get => _slopeStart; set => _slopeStart = Mathf.Clamp(value, 0f, 60f); }
            public float SlopeRange { get => _slopeRange; set => _slopeRange = Mathf.Clamp(value, 1f, 45f); }
            public float FaceNormalAngle { get => _faceNormalAngle; set => _faceNormalAngle = Mathf.Clamp(value, 0f, 45f); }
        }

        /// <summary>
        /// Parallax occlusion mapping on the top projection. The view ray steps into the height (albedo alpha) of the
        /// strongest layer, so the ground texture shows depth near the camera. Each step samples the height again: the
        /// cost grows with the step count, and stochastic layers sample up to three times per step.
        /// </summary>
        [Serializable]
        public sealed class ParallaxSettings
        {
            [SerializeField] private bool _enabled;
            [Tooltip("Depth in meters of the lowest texture height below the terrain surface.")]
            [Range(0f, 0.5f)]
            [SerializeField] private float _depth = 0.05f;
            [Tooltip("Camera distance in meters where the parallax starts to fade out.")]
            [Min(0f)]
            [SerializeField] private float _fadeStart = 8f;
            [Tooltip("Camera distance in meters where the parallax is off.")]
            [Min(0f)]
            [SerializeField] private float _fadeEnd = 25f;
            [Tooltip("Height steps at a low view angle. A view from above uses fewer steps.")]
            [Range(4, 32)]
            [SerializeField] private int _maxSteps = 12;

            public bool Enabled { get => _enabled; set => _enabled = value; }
            public float Depth { get => _depth; set => _depth = Mathf.Clamp(value, 0f, 0.5f); }
            public float FadeStart { get => _fadeStart; set => _fadeStart = Mathf.Max(0f, value); }
            public float FadeEnd { get => _fadeEnd; set => _fadeEnd = Mathf.Max(0f, value); }
            public int MaxSteps { get => _maxSteps; set => _maxSteps = Mathf.Clamp(value, 4, 32); }
            public bool IsActive => _enabled && _depth > 0f;
        }

        /// <summary>
        /// Detail of the far ground, where the texture detail is smaller than a pixel: coarse world strata on steep
        /// ground and the curvature of the terrain shape. Both fade in with the camera distance.
        /// </summary>
        [Serializable]
        public sealed class FarDetailSettings
        {
            [SerializeField] private bool _enabled;
            [Tooltip("Camera distance in meters where the far detail starts to fade in.")]
            [Min(0f)]
            [SerializeField] private float _fadeStart = 120f;
            [Tooltip("Distance in meters over which the far detail fades in.")]
            [Min(1f)]
            [SerializeField] private float _fadeRange = 200f;

            [Header("Strata")]
            [Tooltip("Slope in degrees where the strata start. They are full at this slope plus the slope range.")]
            [Range(0f, 90f)]
            [SerializeField] private float _strataSlope = 35f;
            [Tooltip("Slope range in degrees over which the strata fade in.")]
            [Range(1f, 45f)]
            [SerializeField] private float _strataSlopeRange = 15f;
            [Tooltip("Mean thickness of one band in meters.")]
            [Min(0.5f)]
            [SerializeField] private float _strataThickness = 14f;
            [Tooltip("Largest rise and fall of the bands in meters, so the bands follow no exact height.")]
            [Min(0f)]
            [SerializeField] private float _strataWarp = 8f;
            [Tooltip("Lightness change of the bands. 0 = no bands.")]
            [Range(0f, 1f)]
            [SerializeField] private float _strataStrength = 0.25f;
            [Tooltip("Ledge relief at the band edges: the light bands stand out. 0 = flat bands.")]
            [Range(0f, 2f)]
            [SerializeField] private float _strataRelief = 0.3f;

            [Header("Curvature")]
            [Tooltip("Darkening of hollows and lightening of crests of the terrain shape. 0 = off.")]
            [Range(0f, 1f)]
            [SerializeField] private float _curvatureStrength = 0.3f;
            [Tooltip("Distance in meters between the height samples. Larger values show larger shapes.")]
            [Min(0.5f)]
            [SerializeField] private float _curvatureStep = 4f;
            [Tooltip("Curvature (1 / m) that gives the full darkening. Smaller values show gentler shapes.")]
            [Min(0.001f)]
            [SerializeField] private float _curvatureRange = 0.25f;

            public bool Enabled { get => _enabled; set => _enabled = value; }
            public float FadeStart { get => _fadeStart; set => _fadeStart = Mathf.Max(0f, value); }
            public float FadeRange { get => _fadeRange; set => _fadeRange = Mathf.Max(1f, value); }
            public float StrataSlope { get => _strataSlope; set => _strataSlope = Mathf.Clamp(value, 0f, 90f); }
            public float StrataSlopeRange
            {
                get => _strataSlopeRange;
                set => _strataSlopeRange = Mathf.Clamp(value, 1f, 45f);
            }
            public float StrataThickness { get => _strataThickness; set => _strataThickness = Mathf.Max(0.5f, value); }
            public float StrataWarp { get => _strataWarp; set => _strataWarp = Mathf.Max(0f, value); }
            public float StrataStrength { get => _strataStrength; set => _strataStrength = Mathf.Clamp01(value); }
            public float StrataRelief { get => _strataRelief; set => _strataRelief = Mathf.Clamp(value, 0f, 2f); }
            public float CurvatureStrength
            {
                get => _curvatureStrength;
                set => _curvatureStrength = Mathf.Clamp01(value);
            }
            public float CurvatureStep { get => _curvatureStep; set => _curvatureStep = Mathf.Max(0.5f, value); }
            public float CurvatureRange { get => _curvatureRange; set => _curvatureRange = Mathf.Max(0.001f, value); }
        }

        /// <summary>
        /// Erosion variation of the ground from the field map of each tile (<see cref="LoogaTerrainSurfaceFields"/>). A
        /// strength of 0 turns each part off. The surface reads the map only when a part is on.
        /// </summary>
        [Serializable]
        public sealed class SurfaceFieldSettings
        {
            [SerializeField] private bool _enabled;

            [Header("Wetness")]
            [Tooltip("Darkening of wet ground, where water collects on gentle slopes. 0 = none.")]
            [Range(0f, 1f)]
            [SerializeField] private float _wetDarkening;
            [Tooltip("Share of the way from the surface smoothness to 1 on wet ground. 0 = none.")]
            [Range(0f, 1f)]
            [SerializeField] private float _wetSmoothness;
            [Tooltip("Wetness index where the effect starts and where it is full (2 to 15).")]
            [SerializeField] private Vector2 _wetRange = new(8f, 13f);

            [Header("Channels")]
            [Tooltip("Darkening of the channel floors, where much water passes. 0 = none.")]
            [Range(0f, 1f)]
            [SerializeField] private float _channelDarkening;
            [Tooltip("Share of the way from the surface smoothness to 1 in channels. 0 = none.")]
            [Range(0f, 1f)]
            [SerializeField] private float _channelSmoothness;
            [Tooltip("Log10 of the upstream area in square meters where the effect starts and where it is full (0 to 6).")]
            [SerializeField] private Vector2 _channelRange = new(3.5f, 5f);

            [Header("Rim Wear")]
            [Tooltip("Lightening of convex rims. 0 = none.")]
            [Range(0f, 1f)]
            [SerializeField] private float _rimLightening;
            [Tooltip("Loss of the far ground cover on convex rims. 0 = none.")]
            [Range(0f, 1f)]
            [SerializeField] private float _rimCoverLoss;
            [Tooltip("Rise above the surroundings where the effect starts and where it is full, as a share of 2 m over " +
                     "4 m and of 8 m over 20 m. The larger of the two counts.")]
            [SerializeField] private Vector2 _rimRange = new(0.1f, 0.5f);

            [Header("Foot Deposition")]
            [Tooltip("Share of the deposit colour on concave feet. 0 = none.")]
            [Range(0f, 1f)]
            [SerializeField] private float _footStrength;
            [Tooltip("Colour of the fine deposits.")]
            [SerializeField] private Color _footColor = new(0.32f, 0.27f, 0.21f);
            [Tooltip("Depth below the surroundings where the effect starts and where it is full, as a share of 2 m over " +
                     "4 m and of 8 m over 20 m. The larger of the two counts.")]
            [SerializeField] private Vector2 _footRange = new(0.1f, 0.5f);
            [Tooltip("Slope in degrees above which rim wear and foot deposition fade out. A wall shows both signs of " +
                     "the relative height over its height, so they end on steep ground.")]
            [Range(10f, 90f)]
            [SerializeField] private float _maxSlope = 50f;

            public bool Enabled { get => _enabled; set => _enabled = value; }
            public float WetDarkening { get => _wetDarkening; set => _wetDarkening = Mathf.Clamp01(value); }
            public float WetSmoothness { get => _wetSmoothness; set => _wetSmoothness = Mathf.Clamp01(value); }
            public Vector2 WetRange { get => _wetRange; set => _wetRange = value; }
            public float ChannelDarkening { get => _channelDarkening; set => _channelDarkening = Mathf.Clamp01(value); }
            public float ChannelSmoothness { get => _channelSmoothness; set => _channelSmoothness = Mathf.Clamp01(value); }
            public Vector2 ChannelRange { get => _channelRange; set => _channelRange = value; }
            public float RimLightening { get => _rimLightening; set => _rimLightening = Mathf.Clamp01(value); }
            public float RimCoverLoss { get => _rimCoverLoss; set => _rimCoverLoss = Mathf.Clamp01(value); }
            public Vector2 RimRange { get => _rimRange; set => _rimRange = value; }
            public float FootStrength { get => _footStrength; set => _footStrength = Mathf.Clamp01(value); }
            public Color FootColor { get => _footColor; set => _footColor = value; }
            public Vector2 FootRange { get => _footRange; set => _footRange = value; }
            public float MaxSlope { get => _maxSlope; set => _maxSlope = Mathf.Clamp(value, 10f, 90f); }

            public bool IsActive => _enabled && (_wetDarkening > 0f || _wetSmoothness > 0f || _channelDarkening > 0f
                || _channelSmoothness > 0f || _rimLightening > 0f || _rimCoverLoss > 0f || _footStrength > 0f);
        }

        /// <summary>What the distance fades of the surface measure.</summary>
        public enum FadeDistanceMode
        {
            /// <summary>The distance from the camera.</summary>
            CameraDistance = 0,
            /// <summary>
            /// The size of a screen pixel on the ground, as the distance where the reference camera shows the same size.
            /// The fades then follow the screen resolution and field of view. A cached surface needs this mode.
            /// </summary>
            ScreenFootprint = 1
        }

        /// <summary>How a world colour map changes the terrain albedo.</summary>
        public enum WorldColorBlend
        {
            /// <summary>The world colour map does not change the terrain. Ground cover can still read it.</summary>
            Off = 0,
            /// <summary>Albedo x colour x 2. A colour of 0.5 gives no change.</summary>
            Multiply2X = 1,
            /// <summary>Overlay of the colour on the albedo. A colour of 0.5 gives no change.</summary>
            Overlay = 2,
            /// <summary>The colour replaces the albedo.</summary>
            CrossFade = 3
        }

        /// <summary>
        /// Terrain tint from the world colour map that <see cref="LoogaTerrainSurfaceGlobals"/> binds. The map data is
        /// world data. These settings are the look of this surface.
        /// </summary>
        [Serializable]
        public sealed class WorldColorSettings
        {
            [SerializeField] private WorldColorBlend _blend = WorldColorBlend.Off;
            [Tooltip("Blend strength near the camera.")]
            [Range(0f, 1f)]
            [SerializeField] private float _nearStrength = 1f;
            [Tooltip("Blend strength far from the camera.")]
            [Range(0f, 1f)]
            [SerializeField] private float _farStrength = 1f;
            [Tooltip("Camera distances in meters over which the strength changes from near to far.")]
            [SerializeField] private Vector2 _fadeDistances = new(50f, 500f);
            [Tooltip("Above this slope in degrees the tint fades out, so cliffs keep their own colour. 90 = no limit.")]
            [Range(0f, 90f)]
            [SerializeField] private float _maxSlope = 90f;
            [Tooltip("Slope range in degrees over which the tint fades out above the maximum slope.")]
            [Range(1f, 45f)]
            [SerializeField] private float _slopeRange = 10f;

            public WorldColorBlend Blend { get => _blend; set => _blend = value; }
            public float NearStrength { get => _nearStrength; set => _nearStrength = Mathf.Clamp01(value); }
            public float FarStrength { get => _farStrength; set => _farStrength = Mathf.Clamp01(value); }
            public Vector2 FadeDistances { get => _fadeDistances; set => _fadeDistances = value; }
            public float MaxSlope { get => _maxSlope; set => _maxSlope = Mathf.Clamp(value, 0f, 90f); }
            public float SlopeRange { get => _slopeRange; set => _slopeRange = Mathf.Clamp(value, 1f, 45f); }
            public bool IsActive => _blend != WorldColorBlend.Off && (_nearStrength > 0f || _farStrength > 0f);
        }

        /// <summary>
        /// Lighting rules of the ground cover: how the ground under plants is shaded, and how the ground changes to the
        /// plant colour beyond the foliage draw distance. The ground cover maps (coverage and canopy colour) are world
        /// data that <see cref="LoogaTerrainSurfaceGlobals"/> binds. These settings do not decide where plants grow.
        /// </summary>
        [Serializable]
        public sealed class GroundCoverSettings
        {
            [SerializeField] private bool _enabled;
            [Tooltip("Blend toward the ground cover at full coverage. 0 = no ground cover.")]
            [Range(0f, 1f)]
            [SerializeField] private float _strength = 1f;

            [Header("Distance")]
            [Tooltip("Camera distance in meters where the ground starts to change to the canopy colour. Match the start " +
                     "of the foliage thinning.")]
            [SerializeField] private float _canopyStart = 30f;
            [Tooltip("Camera distance in meters where the ground shows only the canopy colour. Keep it before the " +
                     "foliage fade end, so the ground seen through thin foliage already matches.")]
            [SerializeField] private float _canopyEnd = 130f;
            [Tooltip("Coverage mip level at the canopy end distance. A coarser mip softens the plant edges that the " +
                     "foliage no longer hides at distance.")]
            [Range(0f, 6f)]
            [SerializeField] private float _farCoverageMip = 2.5f;

            [Header("Under Canopy")]
            [Tooltip("Ground brightness under the plants near the camera. Lower values give darker ground.")]
            [Range(0f, 1f)]
            [SerializeField] private float _underCanopyShade = 0.55f;
            [Tooltip("Blend of the ground under the plants toward the dark canopy colour. 0 = shaded ground only.")]
            [Range(0f, 1f)]
            [SerializeField] private float _underCanopyTint = 0.3f;
            [Tooltip("Scale from the canopy colour to the dark canopy colour under the plants.")]
            [Min(0f)]
            [SerializeField] private float _underCanopyAlbedo = 0.1f;

            [Header("Canopy")]
            [Tooltip("Scale from the canopy colour to the terrain albedo at distance. Canopy Shadow gives the far " +
                     "canopy its sun shade, so this value is the mean blade brightness only.")]
            [Min(0f)]
            [SerializeField] private float _canopyAlbedo = 1f;
            [Tooltip("Ambient occlusion of the far canopy: the mean occlusion of the plants over their height, for " +
                     "example the root occlusion of the procedural grass. 1 = no occlusion.")]
            [Range(0f, 1f)]
            [SerializeField] private float _canopyOcclusion = 1f;
            [Tooltip("Gloss of the plant blades in the far canopy, added as a grey albedo. It does not change with the " +
                     "plant colour. 0 = no sheen.")]
            [Range(0f, 0.2f)]
            [SerializeField] private float _canopySheen;
            [Tooltip("Sheen added at a low view angle toward the sun, at 2 degrees of elevation. Away from the sun 0.2 " +
                     "of it stays. 0 = no added sheen.")]
            [Range(0f, 0.2f)]
            [SerializeField] private float _canopySunSheen;
            [Tooltip("Size in meters of the light and dark patches of the far canopy, from the detail noise texture of " +
                     "Anti-Tiling. A second sample at 1 / 2.71 of the size adds the clumps.")]
            [Min(0.1f)]
            [SerializeField] private float _canopyDetailSize = 6f;
            [Tooltip("Strength of the patches in the far canopy colour. The canopy maps hold no pattern finer than their " +
                     "texels, so without it the far cover reads as a flat colour. 0 = no patches.")]
            [Range(0f, 1f)]
            [SerializeField] private float _canopyDetailStrength;
            [Tooltip("Relief of the patches in the far canopy normal, as a share of the noise normal.")]
            [Range(0f, 4f)]
            [SerializeField] private float _canopyDetailNormal = 1f;

            [Header("Soil")]
            [Tooltip("Mean linear luminance of the ground albedo under the plants. The ground luminance relative to " +
                     "this value keeps the soil pattern visible in the canopy colour.")]
            [Min(0.001f)]
            [SerializeField] private float _soilLuminance = 0.2f;
            [Tooltip("Soil pattern in the canopy colour. 0 = flat canopy colour, 1 = full soil luminance pattern.")]
            [Range(0f, 1f)]
            [SerializeField] private float _soilDetail = 0.6f;
            [Tooltip("Part of the ground normal relief that stays at full coverage.")]
            [Range(0f, 1f)]
            [SerializeField] private float _soilRelief = 0.75f;

            [Header("Canopy Shadow")]
            [Tooltip("Density of the plant canopy for the main light. The plants hide the sun from the ground below " +
                     "them by their coverage. At distance the plants take the mean sun visibility over their height. " +
                     "Match the canopy shadow of the procedural grass types. 0 = no shadow.")]
            [Range(0f, 6f)]
            [SerializeField] private float _canopyShadow = 1.8f;

            [Header("Bare Cast")]
            [Tooltip("Share of the canopy colour on bare ground that takes a cast, near the camera (x) and at the canopy " +
                     "end distance (y). The ground keeps its luminance, so bare ground reads as soil with a plant cast.")]
            [SerializeField] private Vector2 _bareCast = new(0.35f, 0.6f);

            [Header("View Angle")]
            [Tooltip("Height to width ratio of the plants. At a low view angle the plants hide more ground: the open " +
                     "share falls as (1 - coverage) ^ (1 + ratio x cot(view elevation)).")]
            [Min(0f)]
            [SerializeField] private float _plantAspect = 1f;
            [Tooltip("Upper limit of the view angle term. 25 is about 2 degrees of elevation.")]
            [Min(0f)]
            [SerializeField] private float _maxGrazing = 25f;
            [Tooltip("Coverage range over which the view angle term starts. Sparse plants grow in clumps that do not " +
                     "hide the ground at a low view angle.")]
            [SerializeField] private Vector2 _denseCoverage = new(0.3f, 0.65f);

            public bool Enabled { get => _enabled; set => _enabled = value; }
            public float Strength { get => _strength; set => _strength = Mathf.Clamp01(value); }
            public float CanopyStart { get => _canopyStart; set => _canopyStart = Mathf.Max(0f, value); }
            public float CanopyEnd { get => _canopyEnd; set => _canopyEnd = Mathf.Max(0f, value); }
            public float FarCoverageMip { get => _farCoverageMip; set => _farCoverageMip = Mathf.Clamp(value, 0f, 6f); }
            public float UnderCanopyShade { get => _underCanopyShade; set => _underCanopyShade = Mathf.Clamp01(value); }
            public float UnderCanopyTint { get => _underCanopyTint; set => _underCanopyTint = Mathf.Clamp01(value); }
            public float UnderCanopyAlbedo { get => _underCanopyAlbedo; set => _underCanopyAlbedo = Mathf.Max(0f, value); }
            public float CanopyAlbedo { get => _canopyAlbedo; set => _canopyAlbedo = Mathf.Max(0f, value); }
            public float CanopyOcclusion { get => _canopyOcclusion; set => _canopyOcclusion = Mathf.Clamp01(value); }
            public float CanopySheen { get => _canopySheen; set => _canopySheen = Mathf.Clamp(value, 0f, 0.2f); }
            public float CanopySunSheen { get => _canopySunSheen; set => _canopySunSheen = Mathf.Clamp(value, 0f, 0.2f); }
            public float CanopyDetailSize { get => _canopyDetailSize; set => _canopyDetailSize = Mathf.Max(0.1f, value); }
            public float CanopyDetailStrength
            {
                get => _canopyDetailStrength;
                set => _canopyDetailStrength = Mathf.Clamp01(value);
            }
            public float CanopyDetailNormal
            {
                get => _canopyDetailNormal;
                set => _canopyDetailNormal = Mathf.Clamp(value, 0f, 4f);
            }
            public float SoilLuminance { get => _soilLuminance; set => _soilLuminance = Mathf.Max(0.001f, value); }
            public float SoilDetail { get => _soilDetail; set => _soilDetail = Mathf.Clamp01(value); }
            public float SoilRelief { get => _soilRelief; set => _soilRelief = Mathf.Clamp01(value); }
            public float CanopyShadow { get => _canopyShadow; set => _canopyShadow = Mathf.Clamp(value, 0f, 6f); }
            public Vector2 BareCast { get => _bareCast; set => _bareCast = value; }
            public float PlantAspect { get => _plantAspect; set => _plantAspect = Mathf.Max(0f, value); }
            public float MaxGrazing { get => _maxGrazing; set => _maxGrazing = Mathf.Max(0f, value); }
            public Vector2 DenseCoverage { get => _denseCoverage; set => _denseCoverage = value; }
            public bool IsActive => _enabled && _strength > 0f;
        }

        /// <summary>Which maps the distance resample changes.</summary>
        public enum ResampleMaps
        {
            Albedo = 0,
            Normal = 1,
            Both = 2
        }

        /// <summary>How the far sample of the distance resample fades in.</summary>
        public enum ResampleFade
        {
            /// <summary>From the fade start to the fade end distance.</summary>
            CrossFade = 0,
            /// <summary>Cross-fade in irregular patches.</summary>
            Noise = 1,
            /// <summary>At every distance.</summary>
            Constant = 2
        }

        /// <summary>How the far albedo sample combines with the near sample.</summary>
        public enum ResampleAlbedoBlend
        {
            Linear = 0,
            /// <summary>Keeps the near colour and adds the light and dark variation of the far sample.</summary>
            Overlay = 1,
            /// <summary>The lighter of the two colours.</summary>
            LighterColor = 2
        }

        /// <summary>A small tiling texture close to the camera: luminance and normal detail.</summary>
        [Serializable]
        public sealed class DetailNoiseSettings
        {
            [SerializeField] private bool _enabled;
            [Tooltip("rg normal x and y, b luminance (0.5 = no change). MicroSplat detail noise textures use this packing.")]
            [SerializeField] private Texture2D _texture;
            [Tooltip("World size of one texture repeat in meters.")]
            [Min(0.01f)]
            [SerializeField] private float _size = 4f;
            [Range(0f, 1f)]
            [SerializeField] private float _strength = 0.3f;
            [Tooltip("The noise fades out at this camera distance in meters.")]
            [Min(1f)]
            [SerializeField] private float _fadeDistance = 80f;

            public bool Enabled { get => _enabled; set => _enabled = value; }
            public Texture2D Texture { get => _texture; set => _texture = value; }
            public float Size { get => _size; set => _size = Mathf.Max(0.01f, value); }
            public float Strength { get => _strength; set => _strength = Mathf.Clamp01(value); }
            public float FadeDistance { get => _fadeDistance; set => _fadeDistance = Mathf.Max(1f, value); }
            public bool IsActive => _enabled && _texture && _strength > 0f;
        }

        /// <summary>A large tiling texture far from the camera: luminance and normal variation that hides repetition.</summary>
        [Serializable]
        public sealed class DistanceNoiseSettings
        {
            [SerializeField] private bool _enabled;
            [Tooltip("rg normal x and y, b luminance (0.5 = no change), as the detail noise.")]
            [SerializeField] private Texture2D _texture;
            [Tooltip("World size of one texture repeat in meters.")]
            [Min(0.01f)]
            [SerializeField] private float _size = 200f;
            [Range(0f, 1f)]
            [SerializeField] private float _strength = 0.3f;
            [Tooltip("Camera distance in meters where the noise starts to fade in.")]
            [Min(0f)]
            [SerializeField] private float _fadeStart = 60f;
            [Tooltip("Distance in meters over which the noise fades in.")]
            [Min(1f)]
            [SerializeField] private float _fadeRange = 300f;

            public bool Enabled { get => _enabled; set => _enabled = value; }
            public Texture2D Texture { get => _texture; set => _texture = value; }
            public float Size { get => _size; set => _size = Mathf.Max(0.01f, value); }
            public float Strength { get => _strength; set => _strength = Mathf.Clamp01(value); }
            public float FadeStart { get => _fadeStart; set => _fadeStart = Mathf.Max(0f, value); }
            public float FadeRange { get => _fadeRange; set => _fadeRange = Mathf.Max(1f, value); }
            public bool IsActive => _enabled && _texture && _strength > 0f;
        }

        /// <summary>An extra tiling normal map at its own scale, for example ripples on sand or lumps under moss.</summary>
        [Serializable]
        public sealed class NormalNoiseSettings
        {
            [SerializeField] private bool _enabled;
            [Tooltip("A normal map.")]
            [SerializeField] private Texture2D _texture;
            [Tooltip("World size of one texture repeat in meters.")]
            [Min(0.01f)]
            [SerializeField] private float _size = 32f;
            [Range(0f, 2f)]
            [SerializeField] private float _strength = 0.5f;

            public bool Enabled { get => _enabled; set => _enabled = value; }
            public Texture2D Texture { get => _texture; set => _texture = value; }
            public float Size { get => _size; set => _size = Mathf.Max(0.01f, value); }
            public float Strength { get => _strength; set => _strength = Mathf.Clamp(value, 0f, 2f); }
            public bool IsActive => _enabled && _texture && _strength > 0f;
        }

        /// <summary>Procedural noise added to the layer heights, so height-blend transitions become irregular.</summary>
        [Serializable]
        public sealed class NoiseHeightSettings
        {
            [SerializeField] private bool _enabled;
            [Tooltip("Size of the noise features in meters.")]
            [Min(0.01f)]
            [SerializeField] private float _size = 4f;
            [Tooltip("Height added at the noise peaks (the texture heights are 0 to 1).")]
            [Range(0f, 1f)]
            [SerializeField] private float _amplitude = 0.15f;

            public bool Enabled { get => _enabled; set => _enabled = value; }
            public float Size { get => _size; set => _size = Mathf.Max(0.01f, value); }
            public float Amplitude { get => _amplitude; set => _amplitude = Mathf.Clamp01(value); }
            public bool IsActive => _enabled && _amplitude > 0f;
        }

        /// <summary>Procedural offset of the control-map lookup, so layer edges do not follow the control-map texels.</summary>
        [Serializable]
        public sealed class ControlNoiseSettings
        {
            [SerializeField] private bool _enabled;
            [Tooltip("Size of the noise features in meters.")]
            [Min(0.01f)]
            [SerializeField] private float _size = 3f;
            [Tooltip("Largest offset of the lookup in meters. About one control-map texel gives organic edges.")]
            [Range(0f, 8f)]
            [SerializeField] private float _strength = 1f;

            public bool Enabled { get => _enabled; set => _enabled = value; }
            public float Size { get => _size; set => _size = Mathf.Max(0.01f, value); }
            public float Strength { get => _strength; set => _strength = Mathf.Clamp(value, 0f, 8f); }
            public bool IsActive => _enabled && _strength > 0f;
        }

        /// <summary>A second sample of each layer at a larger UV scale that fades in on far ground.</summary>
        [Serializable]
        public sealed class DistanceResampleSettings
        {
            [SerializeField] private bool _enabled;
            [SerializeField] private ResampleMaps _maps = ResampleMaps.Both;
            [Tooltip("UV scale of the far sample. 0.25 makes the texture four times larger.")]
            [Range(0.01f, 1f)]
            [SerializeField] private float _uvScale = 0.25f;
            [SerializeField] private ResampleFade _fade = ResampleFade.CrossFade;
            [Tooltip("Camera distance in meters where the far sample starts to fade in.")]
            [Min(0f)]
            [SerializeField] private float _fadeStart = 60f;
            [Tooltip("Camera distance in meters where the far sample reaches its full strength.")]
            [Min(0f)]
            [SerializeField] private float _fadeEnd = 250f;
            [SerializeField] private ResampleAlbedoBlend _albedoBlend = ResampleAlbedoBlend.Linear;
            [Range(0f, 1f)]
            [SerializeField] private float _albedoStrength = 1f;
            [Range(0f, 1f)]
            [SerializeField] private float _normalStrength = 1f;
            [Tooltip("Noise fade: size of the patches in meters.")]
            [Min(0.01f)]
            [SerializeField] private float _noiseSize = 50f;
            [Tooltip("Noise fade: how strongly the patches change the fade.")]
            [Range(0f, 2f)]
            [SerializeField] private float _noiseStrength = 0.5f;

            public bool Enabled { get => _enabled; set => _enabled = value; }
            public ResampleMaps Maps { get => _maps; set => _maps = value; }
            public float UVScale { get => _uvScale; set => _uvScale = Mathf.Clamp(value, 0.01f, 1f); }
            public ResampleFade Fade { get => _fade; set => _fade = value; }
            public float FadeStart { get => _fadeStart; set => _fadeStart = Mathf.Max(0f, value); }
            public float FadeEnd { get => _fadeEnd; set => _fadeEnd = Mathf.Max(0f, value); }
            public ResampleAlbedoBlend AlbedoBlend { get => _albedoBlend; set => _albedoBlend = value; }
            public float AlbedoStrength { get => _albedoStrength; set => _albedoStrength = Mathf.Clamp01(value); }
            public float NormalStrength { get => _normalStrength; set => _normalStrength = Mathf.Clamp01(value); }
            public float NoiseSize { get => _noiseSize; set => _noiseSize = Mathf.Max(0.01f, value); }
            public float NoiseStrength { get => _noiseStrength; set => _noiseStrength = Mathf.Clamp(value, 0f, 2f); }
        }

        /// <summary>
        /// Anti-tiling features. Each feature has its own switch. The layers scale the noises with their own strengths.
        /// </summary>
        [Serializable]
        public sealed class AntiTilingSettings
        {
            [SerializeField] private DetailNoiseSettings _detailNoise = new();
            [SerializeField] private DistanceNoiseSettings _distanceNoise = new();
            [SerializeField] private NormalNoiseSettings _normalNoise1 = new();
            [SerializeField] private NormalNoiseSettings _normalNoise2 = new();
            [SerializeField] private NormalNoiseSettings _normalNoise3 = new();
            [SerializeField] private NoiseHeightSettings _noiseHeight = new();
            [SerializeField] private ControlNoiseSettings _controlNoise = new();
            [SerializeField] private DistanceResampleSettings _distanceResample = new();

            public DetailNoiseSettings DetailNoise => _detailNoise ??= new DetailNoiseSettings();
            public DistanceNoiseSettings DistanceNoise => _distanceNoise ??= new DistanceNoiseSettings();
            public NormalNoiseSettings NormalNoise1 => _normalNoise1 ??= new NormalNoiseSettings();
            public NormalNoiseSettings NormalNoise2 => _normalNoise2 ??= new NormalNoiseSettings();
            public NormalNoiseSettings NormalNoise3 => _normalNoise3 ??= new NormalNoiseSettings();
            public NoiseHeightSettings NoiseHeight => _noiseHeight ??= new NoiseHeightSettings();
            public ControlNoiseSettings ControlNoise => _controlNoise ??= new ControlNoiseSettings();
            public DistanceResampleSettings DistanceResample => _distanceResample ??= new DistanceResampleSettings();
        }

        /// <summary>
        /// Source textures of one layer for the array builder. An empty map uses the terrain layer when
        /// <see cref="Layer.UseTerrainLayerMaps"/> is on: diffuse, normal map, and the mask map (R metallic,
        /// G occlusion, B height, A smoothness). A map without a source gets a default value.
        /// </summary>
        [Serializable]
        public sealed class SourceMaps
        {
            [SerializeField] private Texture2D _albedo;
            [SerializeField] private Texture2D _normal;
            [SerializeField] private Texture2D _height;
            [SerializeField] private TextureChannel _heightChannel = TextureChannel.B;
            [SerializeField] private Texture2D _smoothness;
            [SerializeField] private TextureChannel _smoothnessChannel = TextureChannel.A;
            [Tooltip("The smoothness texture stores roughness. The builder inverts it.")]
            [SerializeField] private bool _smoothnessIsRoughness;
            [SerializeField] private Texture2D _occlusion;
            [SerializeField] private TextureChannel _occlusionChannel = TextureChannel.G;
            [SerializeField] private Texture2D _metallic;
            [SerializeField] private TextureChannel _metallicChannel = TextureChannel.R;

            public Texture2D Albedo { get => _albedo; set => _albedo = value; }
            public Texture2D Normal { get => _normal; set => _normal = value; }
            public Texture2D Height { get => _height; set => _height = value; }
            public TextureChannel HeightChannel { get => _heightChannel; set => _heightChannel = value; }
            public Texture2D Smoothness { get => _smoothness; set => _smoothness = value; }
            public TextureChannel SmoothnessChannel { get => _smoothnessChannel; set => _smoothnessChannel = value; }
            public bool SmoothnessIsRoughness { get => _smoothnessIsRoughness; set => _smoothnessIsRoughness = value; }
            public Texture2D Occlusion { get => _occlusion; set => _occlusion = value; }
            public TextureChannel OcclusionChannel { get => _occlusionChannel; set => _occlusionChannel = value; }
            public Texture2D Metallic { get => _metallic; set => _metallic = value; }
            public TextureChannel MetallicChannel { get => _metallicChannel; set => _metallicChannel = value; }
        }

        // Input hash of the arrays that the array builder made for one layout. The editor compares it with the current
        // inputs to find out-of-date arrays.
        [Serializable]
        private struct ArrayBuild
        {
            public PackingLayout Layout;
            public string InputHash;
        }

        /// <summary>Settings of one terrain layer. The list index is the texture array index.</summary>
        [Serializable]
        public sealed class Layer
        {
            [SerializeField] private TerrainLayer _terrainLayer;

            [Header("Source Maps")]
            [Tooltip("Empty source maps use the maps of the terrain layer. Off: empty maps get default values.")]
            [SerializeField] private bool _useTerrainLayerMaps = true;
            [SerializeField] private SourceMaps _sources = new();

            [Header("UV")]
            [Tooltip("Texture repeats per world UV unit. Larger values make the texture smaller.")]
            [SerializeField] private Vector2 _uvScale = Vector2.one;
            [SerializeField] private Vector2 _uvOffset;
            [Tooltip("Rotation of the texture in degrees. Different rotations hide the repetition of similar layers.")]
            [SerializeField] private float _uvRotation;
            [Tooltip("Use stochastic sampling on this layer when the global stochastic switch is on.")]
            [SerializeField] private bool _stochastic = true;

            [Header("Color")]
            [SerializeField] private Color _tint = Color.white;
            [Tooltip("Value added to the color after the contrast. 0 = no change.")]
            [Range(-1f, 1f)]
            [SerializeField] private float _brightness;
            [Tooltip("Contrast around middle grey. 1 = no change.")]
            [Range(0f, 3f)]
            [SerializeField] private float _contrast = 1f;
            [Tooltip("0 = grey, 1 = no change.")]
            [Range(0f, 2f)]
            [SerializeField] private float _saturation = 1f;

            [Header("Material")]
            [Range(0f, 3f)]
            [SerializeField] private float _normalStrength = 1f;
            [Tooltip("Value added to the texture smoothness. 0 = no change.")]
            [Range(-1f, 1f)]
            [SerializeField] private float _smoothness;
            [Tooltip("Exponent of the texture occlusion. 1 = no change, 0 = no occlusion.")]
            [Range(0f, 4f)]
            [SerializeField] private float _occlusionStrength = 1f;
            [Tooltip("Value added to the texture metallic. The compact layout has no metallic map, so it uses only this value.")]
            [Range(0f, 1f)]
            [SerializeField] private float _metallic;

            [Header("Height Blend")]
            [Tooltip("Raises or lowers the texture height. A higher layer wins the height blend.")]
            [Range(-1f, 1f)]
            [SerializeField] private float _heightOffset;
            [Tooltip("Contrast of the texture height. 1 = no change.")]
            [Range(0f, 4f)]
            [SerializeField] private float _heightContrast = 1f;
            [Tooltip("Added to the global height blend contrast for this layer. 0 = no change.")]
            [Range(-1f, 1f)]
            [SerializeField] private float _interpolationContrast;
            [Tooltip("Multiplier of the parallax depth on this layer. 0 keeps the layer flat, for example pavement.")]
            [Range(0f, 2f)]
            [SerializeField] private float _parallaxDepth = 1f;

            [Header("Anti-Tiling")]
            [Tooltip("Multiplier of the detail noise strength on this layer.")]
            [Range(0f, 2f)]
            [SerializeField] private float _detailNoiseStrength = 1f;
            [Tooltip("Multiplier of the distance noise strength on this layer.")]
            [Range(0f, 2f)]
            [SerializeField] private float _distanceNoiseStrength = 1f;
            [Tooltip("Multipliers of the three normal noise strengths on this layer. 0 keeps the layer flat, for example pavement.")]
            [SerializeField] private Vector3 _normalNoiseStrength = Vector3.one;
            [Tooltip("Multiplier of the noise height amplitude on this layer.")]
            [Range(0f, 2f)]
            [SerializeField] private float _noiseHeightAmplitude = 1f;
            [Tooltip("Multiplier of the distance resample strength on this layer.")]
            [Range(0f, 1f)]
            [SerializeField] private float _resampleStrength = 1f;
            [Tooltip("Multiplier of the distance resample UV scale on this layer.")]
            [Min(0.01f)]
            [SerializeField] private float _resampleUVScale = 1f;

            [Header("World Colour And Ground Cover")]
            [Tooltip("Multiplier of the world colour tint on this layer. 0 keeps the layer colour, for example on rock.")]
            [Range(0f, 1f)]
            [SerializeField] private float _worldColorStrength = 1f;
            [Tooltip("Multiplier of the ground cover on this layer. 0 = the layer does not change under plants.")]
            [Range(0f, 1f)]
            [SerializeField] private float _groundCoverStrength = 1f;
            [Tooltip("Plant cover that the layer shows at distance where no plants grow: the ground changes to the " +
                     "bare canopy colour of the Terrain Surface Globals over the canopy distances, as plant cover does. " +
                     "Use it on grass ground outside the plant areas. 0 = off.")]
            [Range(0f, 1f)]
            [SerializeField] private float _bareFarCover;

            [Header("Objects")]
            [Tooltip("Dust that this ground leaves on the base of rocks and other objects that blend into the terrain. " +
                     "Use 1 on sand and dry dirt. 0 = no dust, for example on grass and rock.")]
            [Range(0f, 1f)]
            [SerializeField] private float _objectDust;

            public Layer()
            {
            }

            public Layer(TerrainLayer terrainLayer)
            {
                _terrainLayer = terrainLayer;
            }

            public TerrainLayer TerrainLayer { get => _terrainLayer; set => _terrainLayer = value; }
            public bool UseTerrainLayerMaps { get => _useTerrainLayerMaps; set => _useTerrainLayerMaps = value; }
            public SourceMaps Sources => _sources ??= new SourceMaps();
            public Vector2 UVScale { get => _uvScale; set => _uvScale = value; }
            public Vector2 UVOffset { get => _uvOffset; set => _uvOffset = value; }
            public float UVRotation { get => _uvRotation; set => _uvRotation = value; }
            public bool Stochastic { get => _stochastic; set => _stochastic = value; }
            public Color Tint { get => _tint; set => _tint = value; }
            public float Brightness { get => _brightness; set => _brightness = value; }
            public float Contrast { get => _contrast; set => _contrast = value; }
            public float Saturation { get => _saturation; set => _saturation = value; }
            public float NormalStrength { get => _normalStrength; set => _normalStrength = value; }
            public float Smoothness { get => _smoothness; set => _smoothness = value; }
            public float OcclusionStrength { get => _occlusionStrength; set => _occlusionStrength = value; }
            public float Metallic { get => _metallic; set => _metallic = value; }
            public float HeightOffset { get => _heightOffset; set => _heightOffset = value; }
            public float HeightContrast { get => _heightContrast; set => _heightContrast = value; }
            public float InterpolationContrast { get => _interpolationContrast; set => _interpolationContrast = value; }
            public float ParallaxDepth { get => _parallaxDepth; set => _parallaxDepth = Mathf.Clamp(value, 0f, 2f); }
            public float DetailNoiseStrength { get => _detailNoiseStrength; set => _detailNoiseStrength = value; }
            public float DistanceNoiseStrength { get => _distanceNoiseStrength; set => _distanceNoiseStrength = value; }
            public Vector3 NormalNoiseStrength { get => _normalNoiseStrength; set => _normalNoiseStrength = value; }
            public float NoiseHeightAmplitude { get => _noiseHeightAmplitude; set => _noiseHeightAmplitude = value; }
            public float ResampleStrength { get => _resampleStrength; set => _resampleStrength = value; }
            public float ResampleUVScale { get => _resampleUVScale; set => _resampleUVScale = value; }
            public float WorldColorStrength { get => _worldColorStrength; set => _worldColorStrength = Mathf.Clamp01(value); }
            public float GroundCoverStrength { get => _groundCoverStrength; set => _groundCoverStrength = Mathf.Clamp01(value); }
            public float BareFarCover { get => _bareFarCover; set => _bareFarCover = Mathf.Clamp01(value); }
            public float ObjectDust { get => _objectDust; set => _objectDust = Mathf.Clamp01(value); }
        }

        /// <summary>Maximum number of layers: eight control maps with four layers each.</summary>
        public const int MaxLayers = 32;
        /// <summary>Shader name of the native Unity terrain target.</summary>
        public const string NativeShaderName = "Looga/Terrain/Surface";
        /// <summary>Shader name of the Looga TerrainRenderer target.</summary>
        public const string PatchesShaderName = "Looga/Terrain/Surface (Patches)";
        /// <summary>Shader name of the mesh target: the surface on a mesh over the terrain ground.</summary>
        public const string MeshShaderName = "Looga/Terrain/Surface (Mesh)";
        /// <summary>Shader keyword of the quality packing layout.</summary>
        public const string QualityPackingKeyword = "LOOGA_SURFACE_PACK_QUALITY";
        /// <summary>Shader keyword of the triplanar projections. Without it the shader compiles only the top projection.</summary>
        public const string TriplanarKeyword = "LOOGA_SURFACE_TRIPLANAR";
        /// <summary>
        /// Shader keyword of the height-based blend modes and of height-blended triplanar. Without it the shader
        /// blends the layers linearly and the projections by the normal only.
        /// </summary>
        public const string HeightBlendKeyword = "LOOGA_SURFACE_HEIGHT_BLEND";

        // Rows of the per-layer property texture. Keep these rows the same as LoogaTerrainSurfaceInput.hlsl.
        private enum PropertyRow
        {
            UV = 0,
            Color = 1,
            Adjust = 2,
            Material = 3,
            Height = 4,
            Noise = 5,
            Noise2 = 6,
            Cover = 7,
            Object = 8,
            Count = 9
        }

        private static readonly int AlbedoArrayId = Shader.PropertyToID("_LoogaAlbedoArray");
        private static readonly int NormalArrayId = Shader.PropertyToID("_LoogaNormalArray");
        private static readonly int MaskArrayId = Shader.PropertyToID("_LoogaMaskArray");
        private static readonly int LayerPropsId = Shader.PropertyToID("_LoogaLayerProps");
        private static readonly int UVId = Shader.PropertyToID("_LoogaSurfaceUV");
        private static readonly int BlendId = Shader.PropertyToID("_LoogaSurfaceBlend");
        private static readonly int HybridId = Shader.PropertyToID("_LoogaSurfaceHybrid");
        private static readonly int EdgeId = Shader.PropertyToID("_LoogaSurfaceEdge");
        private static readonly int FadeId = Shader.PropertyToID("_LoogaSurfaceFade");
        private static readonly int StochasticId = Shader.PropertyToID("_LoogaSurfaceStochastic");
        private static readonly int LuminanceId = Shader.PropertyToID("_LoogaSurfaceLuminance");
        private static readonly int DebugId = Shader.PropertyToID("_LoogaSurfaceDebug");
        private static readonly int DetailNoiseId = Shader.PropertyToID("_LoogaDetailNoise");
        private static readonly int DistanceNoiseId = Shader.PropertyToID("_LoogaDistanceNoise");
        private static readonly int NormalNoise1Id = Shader.PropertyToID("_LoogaNormalNoise1");
        private static readonly int NormalNoise2Id = Shader.PropertyToID("_LoogaNormalNoise2");
        private static readonly int NormalNoise3Id = Shader.PropertyToID("_LoogaNormalNoise3");
        private static readonly int NoiseHeightId = Shader.PropertyToID("_LoogaNoiseHeight");
        private static readonly int ControlNoiseId = Shader.PropertyToID("_LoogaControlNoise");
        private static readonly int ResampleId = Shader.PropertyToID("_LoogaResample");
        private static readonly int ResampleBlendId = Shader.PropertyToID("_LoogaResampleBlend");
        private static readonly int ResampleNoiseId = Shader.PropertyToID("_LoogaResampleNoise");
        private static readonly int TriplanarId = Shader.PropertyToID("_LoogaTriplanar");
        private static readonly int Triplanar2Id = Shader.PropertyToID("_LoogaTriplanar2");
        private static readonly int ParallaxId = Shader.PropertyToID("_LoogaParallax");
        private static readonly int FarDetailId = Shader.PropertyToID("_LoogaFarDetail");
        private static readonly int FarStrataId = Shader.PropertyToID("_LoogaFarStrata");
        private static readonly int FarCurvatureId = Shader.PropertyToID("_LoogaFarCurvature");
        private static readonly int DependencyRadiusId = Shader.PropertyToID("_LoogaVTDependencyRadius");
        private static readonly int FieldWetnessId = Shader.PropertyToID("_LoogaFieldWetness");
        private static readonly int FieldChannelId = Shader.PropertyToID("_LoogaFieldChannel");
        private static readonly int FieldRimId = Shader.PropertyToID("_LoogaFieldRim");
        private static readonly int FieldFootId = Shader.PropertyToID("_LoogaFieldFoot");
        private static readonly int FieldFootRangeId = Shader.PropertyToID("_LoogaFieldFootRange");
        private static readonly int WorldColorId = Shader.PropertyToID("_LoogaWorldColor");
        private static readonly int WorldColor2Id = Shader.PropertyToID("_LoogaWorldColor2");
        private static readonly int GroundCoverId = Shader.PropertyToID("_LoogaGroundCover");
        private static readonly int GroundCoverShadeId = Shader.PropertyToID("_LoogaGroundCoverShade");
        private static readonly int GroundCoverSoilId = Shader.PropertyToID("_LoogaGroundCoverSoil");
        private static readonly int GroundCoverViewId = Shader.PropertyToID("_LoogaGroundCoverView");
        private static readonly int GroundCoverDenseId = Shader.PropertyToID("_LoogaGroundCoverDense");
        private static readonly int GroundCoverDetailId = Shader.PropertyToID("_LoogaGroundCoverDetail");
        private static readonly int DetailNoiseTexId = Shader.PropertyToID("_LoogaDetailNoiseTex");
        private static readonly int DistanceNoiseTexId = Shader.PropertyToID("_LoogaDistanceNoiseTex");
        private static readonly int NormalNoiseTex1Id = Shader.PropertyToID("_LoogaNormalNoiseTex1");
        private static readonly int NormalNoiseTex2Id = Shader.PropertyToID("_LoogaNormalNoiseTex2");
        private static readonly int NormalNoiseTex3Id = Shader.PropertyToID("_LoogaNormalNoiseTex3");

        [Header("Layers")]
        [Tooltip("Terrain layers in control-map order. Every bound terrain must use the same order.")]
        [SerializeField] private List<Layer> _layers = new();

        [Header("Texture Arrays")]
        [Tooltip("Packing layout of the arrays that the builder makes. Compact uses less memory. Quality keeps the " +
                 "normal, smoothness and occlusion closer to the source maps and adds a metallic map.")]
        [SerializeField] private PackingLayout _packing = PackingLayout.Compact;
        [Tooltip("Build the arrays again in the editor when an input of the build changes: a source map, a channel, " +
                 "the layer list, the layout or the resolution. Off: use Rebuild in the inspector.")]
        [SerializeField] private bool _autoRebuild = true;
        [Tooltip("Albedo in rgb, height in alpha. One slice per layer.")]
        [SerializeField] private Texture2DArray _albedoArray;
        [Tooltip("Compact layout: normal x in alpha, normal y in green, smoothness in red, occlusion in blue. " +
                 "Quality layout: normal x and y in red and green.")]
        [SerializeField] private Texture2DArray _normalArray;
        [Tooltip("Quality layout only: smoothness in red, occlusion in green, metallic in blue.")]
        [SerializeField] private Texture2DArray _maskArray;
        [HideInInspector]
        [SerializeField] private List<ArrayBuild> _arrayBuilds = new();
        // Mean linear albedo of each albedo array slice (alpha 1 when measured). The editor array builder measures it.
        // The ground cover reads the soil luminance relative to the mean of the ground layers (cover row z).
        [HideInInspector]
        [SerializeField] private Color[] _albedoMeans = Array.Empty<Color>();

        [Header("Array Build")]
        [Tooltip("Width and height of each array slice in pixels.")]
        [SerializeField] private int _arrayResolution = 1024;
        [Tooltip("Anisotropic filtering level of the built arrays. Higher values keep the ground sharp at low view angles.")]
        [Range(1, 16)]
        [SerializeField] private int _arrayAnisoLevel = 4;
        [Tooltip("Encoder effort for the BC7 arrays of the quality layout. Measured on a 1024 slice: Fast 5 ms, " +
                 "Normal 22 ms, Best 44 ms, with a mean error of 0.25, 0.21 and 0.19 of a 255 step. The compact layout " +
                 "and the BC5 normal array do not use this setting.")]
        [SerializeField] private CompressionQuality _compressionQuality = CompressionQuality.Normal;
        [Tooltip("Generate a missing height (overlay of the normal x and y) and a missing occlusion (normal z squared) " +
                 "from the normal map, as MicroSplat does. Off: height 0.5 and occlusion 1.")]
        [SerializeField] private bool _generateMissingFromNormal;

        [Header("Mapping")]
        [Tooltip("UV units per world meter on the top projection. A negative value mirrors the texture on that axis.")]
        [SerializeField] private Vector2 _worldUVScale = Vector2.one;
        [SerializeField] private Vector2 _worldUVOffset;

        [Header("Blending")]
        [SerializeField] private BlendMode _blendMode = BlendMode.NormalizedLinear;
        [Tooltip("Number of the strongest layers blended per pixel. Fewer layers are faster but can show edges.")]
        [Range(2, 4)]
        [SerializeField] private int _layersPerPixel = 4;
        [Tooltip("Width of the height blend transition. Small values give sharp edges.")]
        [Range(0.0001f, 1f)]
        [SerializeField] private float _heightBlendContrast = 0.2f;
        [Tooltip("Hybrid mode: the height blend fades to linear between these camera distances in meters.")]
        [SerializeField] private Vector2 _hybridDistances = new(40f, 120f);
        [Tooltip("Height Offset and Crisp Edge: how far the texture height can move an edge, as a weight " +
                 "difference. 0 = the edges follow the control maps only. 0.5 = the Unreal height blend.")]
        [Range(0f, 1f)]
        [SerializeField] private float _heightReach = 0.5f;
        [Tooltip("Crisp Edge: width of the transition in screen pixels. The Height Blend Contrast is the smallest width.")]
        [Range(0.5f, 8f)]
        [SerializeField] private float _edgeWidth = 1.5f;
        [Tooltip("Crisp Edge: size in meters of the world noise that shapes the edges.")]
        [Min(0.1f)]
        [SerializeField] private float _edgeNoiseSize = 2f;
        [Tooltip("Crisp Edge: strength of the edge noise as a weight difference. 0 = no edge noise.")]
        [Range(0f, 1f)]
        [SerializeField] private float _edgeNoiseStrength = 0.25f;
        [Tooltip("Variance Preserving: largest contrast gain in a transition. 1 = normalized linear. A two-layer " +
                 "transition needs 1.41.")]
        [Range(1f, 2f)]
        [SerializeField] private float _varianceGainLimit = 1.5f;

        [Header("Distance Fades")]
        [Tooltip("What all distance fades of the surface measure. Screen Footprint converts the size of a screen pixel " +
                 "on the ground to the distance where the reference camera shows the same size. A cached surface " +
                 "(virtual texturing) needs Screen Footprint.")]
        [SerializeField] private FadeDistanceMode _fadeDistanceMode = FadeDistanceMode.CameraDistance;
        [Tooltip("Vertical field of view in degrees of the reference camera for Screen Footprint.")]
        [Range(10f, 120f)]
        [SerializeField] private float _fadeReferenceFieldOfView = 60f;
        [Tooltip("Screen height in pixels of the reference camera for Screen Footprint.")]
        [Min(240f)]
        [SerializeField] private float _fadeReferenceHeight = 1440f;

        [Header("Stochastic")]
        [Tooltip("Offsets the texture randomly per triangle cell to hide repetition. It costs up to three samples.")]
        [SerializeField] private bool _stochastic = true;
        [Tooltip("Cell size relative to the texture. Larger values make smaller cells.")]
        [Range(0.25f, 2f)]
        [SerializeField] private float _stochasticCellScale = 1f;
        [Tooltip("Sharpness of the cell transitions. Higher values use fewer samples.")]
        [Range(0.001f, 0.999f)]
        [SerializeField] private float _stochasticContrast = 0.2f;

        [Header("Triplanar")]
        [SerializeField] private TriplanarSettings _triplanar = new();

        [Header("Parallax")]
        [SerializeField] private ParallaxSettings _parallax = new();

        [Header("Anti-Tiling")]
        [SerializeField] private AntiTilingSettings _antiTiling = new();

        [Header("Far Detail")]
        [SerializeField] private FarDetailSettings _farDetail = new();

        [Header("Fields")]
        [SerializeField] private SurfaceFieldSettings _fields = new();

        [Header("World Colour")]
        [SerializeField] private WorldColorSettings _worldColor = new();

        [Header("Ground Cover")]
        [SerializeField] private GroundCoverSettings _groundCover = new();

        [Header("Color")]
        [Tooltip("Luminance weights of the saturation adjustment. Rec. 709 is (0.2126, 0.7152, 0.0722).")]
        [SerializeField] private Vector3 _saturationLuminance = new(0.2126f, 0.7152f, 0.0722f);

        [Header("Debug")]
        [SerializeField] private DebugView _debugView = DebugView.None;

        [Header("Shaders")]
        [SerializeField] private Shader _nativeShader;
        [Tooltip("Shader of the Looga TerrainRenderer target. Bound terrains with a TerrainRenderer draw with it.")]
        [SerializeField] private Shader _patchesShader;
        // Saved materials with the keywords of the surface. A player build keeps only the shader feature variants that
        // saved materials use, and the surface creates its materials at runtime. Without them the terrain rendered
        // black in players. The editor writes them before each player build (LoogaTerrainSurfaceBuildVariants).
        [SerializeField, HideInInspector] private Material _nativeVariants;
        [SerializeField, HideInInspector] private Material _patchesVariants;

        private Material _nativeMaterial;
        private Material _patchesMaterial;
        private Material _meshMaterial;
        private Texture2D _layerProperties;

        /// <summary>Occurs after a setting changes. Bindings use it to check the layers and to apply the surface again.</summary>
        public event Action Changed;

#if UNITY_EDITOR
        /// <summary>Editor only: occurs after a setting of any surface changes. The array builder uses it.</summary>
        public static event Action<LoogaTerrainSurface> AnyChanged;
#endif

        public IReadOnlyList<Layer> Layers => _layers;
        /// <summary>Packing layout that the array builder makes. The assigned arrays can still use the other layout.</summary>
        public PackingLayout Packing => _packing;
        /// <summary>Packing layout of the assigned arrays. Only the quality layout has a mask array.</summary>
        public PackingLayout ArrayLayout => _maskArray ? PackingLayout.Quality : PackingLayout.Compact;
        public bool AutoRebuild => _autoRebuild;
        public Texture2DArray AlbedoArray => _albedoArray;
        public Texture2DArray NormalArray => _normalArray;
        public Texture2DArray MaskArray => _maskArray;
        /// <summary>Mean linear albedo of each albedo array slice, as the editor array builder measured it.</summary>
        public IReadOnlyList<Color> AlbedoMeans => _albedoMeans;
        public int ArrayResolution => _arrayResolution;
        public int ArrayAnisoLevel => _arrayAnisoLevel;
        public CompressionQuality Compression => _compressionQuality;
        public bool GenerateMissingFromNormal => _generateMissingFromNormal;
        public Vector2 WorldUVScale => _worldUVScale;
        public Vector2 WorldUVOffset => _worldUVOffset;
        public BlendMode Blend => _blendMode;
        public int LayersPerPixel => _layersPerPixel;
        public float HeightBlendContrast => _heightBlendContrast;
        public Vector2 HybridDistances => _hybridDistances;
        public float HeightReach => _heightReach;
        public float EdgeWidth => _edgeWidth;
        public float EdgeNoiseSize => _edgeNoiseSize;
        public float EdgeNoiseStrength => _edgeNoiseStrength;
        public float VarianceGainLimit => _varianceGainLimit;
        public FadeDistanceMode FadeDistance { get => _fadeDistanceMode; set => _fadeDistanceMode = value; }

        /// <summary>Meters of fade distance per meter of screen footprint, from the reference camera.</summary>
        public float FadeDistancePerFootprint => Mathf.Max(_fadeReferenceHeight, 240f)
            / (2f * Mathf.Tan(0.5f * Mathf.Clamp(_fadeReferenceFieldOfView, 10f, 120f) * Mathf.Deg2Rad));
        public bool Stochastic => _stochastic;
        public float StochasticCellScale => _stochasticCellScale;
        public float StochasticContrast => _stochasticContrast;
        public Vector3 SaturationLuminance => _saturationLuminance;
        public AntiTilingSettings AntiTiling => _antiTiling ??= new AntiTilingSettings();
        public TriplanarSettings Triplanar => _triplanar ??= new TriplanarSettings();
        public ParallaxSettings Parallax => _parallax ??= new ParallaxSettings();
        public FarDetailSettings FarDetail => _farDetail ??= new FarDetailSettings();
        public SurfaceFieldSettings Fields => _fields ??= new SurfaceFieldSettings();
        public WorldColorSettings WorldColor => _worldColor ??= new WorldColorSettings();
        public GroundCoverSettings GroundCover => _groundCover ??= new GroundCoverSettings();
        public DebugView ActiveDebugView => _debugView;
        /// <summary>
        /// The per-layer property texture (one column per layer, rows in LoogaTerrainSurfaceLayers.hlsl). Other shaders
        /// that read the terrain layers, such as the rock faces, bind it. It is not saved.
        /// </summary>
        public Texture2D LayerProperties => _layerProperties ? _layerProperties : GetLayerProperties();

        /// <summary>
        /// Shared material of the native Unity terrain target. The surface creates it on first use and does not
        /// save it. Returns null when the native shader is missing.
        /// </summary>
        public Material NativeMaterial
        {
            get
            {
                if (_nativeMaterial) return _nativeMaterial;
                if (!_nativeShader || !_nativeShader.isSupported) return null;
                _nativeMaterial = new Material(_nativeShader)
                {
                    name = name + " (Native Terrain)",
                    hideFlags = HideFlags.HideAndDontSave,
                    enableInstancing = true
                };
                ApplyTo(_nativeMaterial);
                return _nativeMaterial;
            }
        }

        /// <summary>
        /// Shared material of the Looga TerrainRenderer target. The surface creates it on first use and does not save
        /// it. Returns null when the patch shader is missing.
        /// </summary>
        public Material PatchesMaterial
        {
            get
            {
                if (_patchesMaterial) return _patchesMaterial;
#if UNITY_EDITOR
                // Surfaces made before the patch target have no shader reference yet.
                if (!_patchesShader)
                {
                    _patchesShader = Shader.Find(PatchesShaderName);
                    if (_patchesShader)
                    {
                        UnityEditor.EditorUtility.SetDirty(this);
                    }
                }
#endif
                if (!_patchesShader || !_patchesShader.isSupported) return null;
                _patchesMaterial = new Material(_patchesShader)
                {
                    name = name + " (Terrain Patches)",
                    hideFlags = HideFlags.HideAndDontSave,
                    enableInstancing = true
                };
                ApplyTo(_patchesMaterial);
                return _patchesMaterial;
            }
        }

        /// <summary>
        /// Shared material of the mesh target, for a mesh that shows the terrain surface over the terrain ground, for
        /// example the soil on top of a cliff wall. The renderer block holds the tile values of the terrain
        /// (<see cref="LoogaTerrainGroundBlock"/>). The surface creates it on first use and does not save it. Returns
        /// null when the mesh shader is missing.
        /// </summary>
        public Material MeshMaterial
        {
            get
            {
                if (_meshMaterial) return _meshMaterial;
                Shader shader = Shader.Find(MeshShaderName);
                if (!shader || !shader.isSupported) return null;
                _meshMaterial = new Material(shader)
                {
                    name = name + " (Mesh)",
                    hideFlags = HideFlags.HideAndDontSave
                };
                ApplyTo(_meshMaterial);
                return _meshMaterial;
            }
        }

        #region Built-in
        private void Reset()
        {
            _nativeShader = Shader.Find(NativeShaderName);
            _patchesShader = Shader.Find(PatchesShaderName);
        }

        private void OnEnable()
        {
            // A reload of the asset (a reimport or a version control update) disables the surface, and OnDisable
            // destroys the shared material. Bound terrains must apply the new material.
            Changed?.Invoke();
        }

        private void OnValidate()
        {
            NotifyChanged();
        }

        private void OnDisable()
        {
            DestroyResource(ref _nativeMaterial);
            DestroyResource(ref _patchesMaterial);
            DestroyResource(ref _meshMaterial);
            DestroyResource(ref _layerProperties);
        }
        #endregion

        #region Settings
        /// <summary>Replaces the layer list. The order must match the terrain layer order of every bound terrain.</summary>
        public void SetLayers(IEnumerable<Layer> layers)
        {
            _layers = new List<Layer>(layers);
            NotifyChanged();
        }

        /// <summary>
        /// Puts the layers in the order of a terrain layer list. Each layer keeps its settings. A new terrain layer gets
        /// default settings. Layers that the list does not use move to the end and keep their settings, so a layer that
        /// returns gets its old settings. The editor then rebuilds the texture arrays.
        /// </summary>
        /// <param name="terrainLayers">Layers of the terrain, in control-map order.</param>
        /// <returns>True when the layer list changed.</returns>
        public bool SyncLayers(IReadOnlyList<TerrainLayer> terrainLayers)
        {
            var ordered = new List<Layer>(Mathf.Max(_layers.Count, terrainLayers.Count));
            foreach (TerrainLayer terrainLayer in terrainLayers)
            {
                Layer layer = _layers.Find(candidate => candidate.TerrainLayer == terrainLayer && !ordered.Contains(candidate));
                ordered.Add(layer ?? new Layer(terrainLayer));
            }
            foreach (Layer layer in _layers)
            {
                // Unused layers stay only while the list has room for them.
                if (ordered.Count < MaxLayers && !ordered.Contains(layer))
                {
                    ordered.Add(layer);
                }
            }

            bool changed = ordered.Count != _layers.Count;
            for (int index = 0; !changed && index < ordered.Count; index++)
            {
                changed = ordered[index] != _layers[index];
            }
            if (!changed) return false;

            _layers = ordered;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
            NotifyChanged();
            return true;
        }

        /// <summary>
        /// Sets the texture arrays. A mask array selects the quality layout, no mask array the compact layout. With
        /// <see cref="AutoRebuild"/> on, the editor replaces arrays that the builder did not make for <see cref="Packing"/>.
        /// </summary>
        public void SetTextureArrays(Texture2DArray albedoArray, Texture2DArray normalArray, Texture2DArray maskArray = null)
        {
            _albedoArray = albedoArray;
            _normalArray = normalArray;
            _maskArray = maskArray;
            NotifyChanged();
        }

        /// <summary>Sets the mean linear albedo of each albedo array slice, as the editor array builder measures it.
        /// </summary>
        public void SetAlbedoMeans(Color[] means)
        {
            _albedoMeans = means ?? Array.Empty<Color>();
            NotifyChanged();
        }

        /// <summary>Sets the packing layout that the array builder makes.</summary>
        public void SetPacking(PackingLayout packing)
        {
            _packing = packing;
            NotifyChanged();
        }

        public void SetAutoRebuild(bool autoRebuild)
        {
            _autoRebuild = autoRebuild;
            NotifyChanged();
        }

        /// <summary>Input hash of the last build of a layout. Empty when the builder did not make that layout.</summary>
        public string GetArrayBuildHash(PackingLayout layout)
        {
            foreach (ArrayBuild build in _arrayBuilds)
            {
                if (build.Layout == layout) return build.InputHash;
            }
            return string.Empty;
        }

        /// <summary>Records the input hash of a build. Only the array builder calls this.</summary>
        public void SetArrayBuildHash(PackingLayout layout, string inputHash)
        {
            _arrayBuilds.RemoveAll(build => build.Layout == layout);
            _arrayBuilds.Add(new ArrayBuild { Layout = layout, InputHash = inputHash });
        }

        public void SetArrayBuild(int resolution, int anisoLevel, bool generateMissingFromNormal)
        {
            _arrayResolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(resolution), 64, 4096);
            _arrayAnisoLevel = Mathf.Clamp(anisoLevel, 1, 16);
            _generateMissingFromNormal = generateMissingFromNormal;
            NotifyChanged();
        }

        public void SetMapping(Vector2 worldUVScale, Vector2 worldUVOffset)
        {
            _worldUVScale = worldUVScale;
            _worldUVOffset = worldUVOffset;
            NotifyChanged();
        }

        public void SetBlending(BlendMode mode, int layersPerPixel, float heightBlendContrast, Vector2 hybridDistances)
        {
            _blendMode = mode;
            _layersPerPixel = Mathf.Clamp(layersPerPixel, 2, 4);
            _heightBlendContrast = Mathf.Clamp(heightBlendContrast, 0.0001f, 1f);
            _hybridDistances = hybridDistances;
            NotifyChanged();
        }

        /// <summary>Sets the values of the Height Offset, Crisp Edge and Variance Preserving blend modes.</summary>
        /// <param name="heightReach">Weight difference that the texture height can overcome (0 to 1).</param>
        /// <param name="edgeWidth">Crisp edge transition width in screen pixels.</param>
        /// <param name="edgeNoiseSize">Crisp edge noise size in meters.</param>
        /// <param name="edgeNoiseStrength">Crisp edge noise strength as a weight difference.</param>
        /// <param name="varianceGainLimit">Largest contrast gain of the variance-preserving blend.</param>
        public void SetBlendShaping(float heightReach, float edgeWidth, float edgeNoiseSize, float edgeNoiseStrength,
            float varianceGainLimit)
        {
            _heightReach = Mathf.Clamp01(heightReach);
            _edgeWidth = Mathf.Clamp(edgeWidth, 0.5f, 8f);
            _edgeNoiseSize = Mathf.Max(0.1f, edgeNoiseSize);
            _edgeNoiseStrength = Mathf.Clamp01(edgeNoiseStrength);
            _varianceGainLimit = Mathf.Clamp(varianceGainLimit, 1f, 2f);
            NotifyChanged();
        }

        public void SetStochastic(bool enabled, float cellScale, float contrast)
        {
            _stochastic = enabled;
            _stochasticCellScale = Mathf.Clamp(cellScale, 0.25f, 2f);
            _stochasticContrast = Mathf.Clamp(contrast, 0.001f, 0.999f);
            NotifyChanged();
        }

        public void SetSaturationLuminance(Vector3 weights)
        {
            _saturationLuminance = weights;
            NotifyChanged();
        }

        /// <summary>Replaces the triplanar settings.</summary>
        public void SetTriplanar(TriplanarSettings settings)
        {
            _triplanar = settings ?? new TriplanarSettings();
            NotifyChanged();
        }

        /// <summary>Replaces the parallax settings.</summary>
        public void SetParallax(ParallaxSettings settings)
        {
            _parallax = settings ?? new ParallaxSettings();
            NotifyChanged();
        }

        /// <summary>Replaces the world colour settings.</summary>
        public void SetWorldColor(WorldColorSettings settings)
        {
            _worldColor = settings ?? new WorldColorSettings();
            NotifyChanged();
        }

        /// <summary>Replaces the ground cover settings.</summary>
        public void SetGroundCover(GroundCoverSettings settings)
        {
            _groundCover = settings ?? new GroundCoverSettings();
            NotifyChanged();
        }

        /// <summary>Replaces the anti-tiling settings.</summary>
        public void SetAntiTiling(AntiTilingSettings settings)
        {
            _antiTiling = settings ?? new AntiTilingSettings();
            NotifyChanged();
        }

        /// <summary>Applies changes that were made through the settings objects, for example <see cref="AntiTiling"/>.</summary>
        public void ApplyChanges()
        {
            NotifyChanged();
        }

        public void SetDebugView(DebugView view)
        {
            _debugView = view;
            NotifyChanged();
        }

        public void SetNativeShader(Shader shader)
        {
            _nativeShader = shader;
            DestroyResource(ref _nativeMaterial);
            NotifyChanged();
        }
        #endregion

        #region Validation
        /// <summary>
        /// Checks that a terrain can use this surface. The terrain layers must match the first surface layers in order.
        /// </summary>
        /// <param name="terrainLayers">Layers of the terrain, in control-map order.</param>
        /// <param name="diagnostic">The reason when the result is false. Otherwise empty.</param>
        public bool Matches(TerrainLayer[] terrainLayers, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (!HasTextureArrays)
            {
                diagnostic = "The surface has no texture arrays. Build them in the surface inspector.";
                return false;
            }
            if (!ArraysCoverLayers)
            {
                diagnostic = "The surface has " + _layers.Count + " layers, but the arrays have "
                    + Mathf.Min(_albedoArray.depth, _normalArray.depth) + " slices.";
                return false;
            }
            return MatchesLayers(terrainLayers, out diagnostic);
        }

        /// <summary>True when the surface has an albedo and a normal array.</summary>
        public bool HasTextureArrays => _albedoArray && _normalArray;

        /// <summary>
        /// True when the arrays have a slice for every layer. After a layer change the editor rebuilds the arrays in the
        /// background, and this is false until the build ends.
        /// </summary>
        public bool ArraysCoverLayers => HasTextureArrays && _layers.Count <= _albedoArray.depth
            && _layers.Count <= _normalArray.depth;

        /// <summary>
        /// Checks only the layer list: the terrain layers must match the first surface layers in order. The texture
        /// arrays are not checked.
        /// </summary>
        /// <param name="terrainLayers">Layers of the terrain, in control-map order.</param>
        /// <param name="diagnostic">The reason when the result is false. Otherwise empty.</param>
        public bool MatchesLayers(TerrainLayer[] terrainLayers, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (_layers.Count > MaxLayers)
            {
                diagnostic = "The surface has " + _layers.Count + " layers. The maximum is " + MaxLayers + ".";
                return false;
            }
            if (terrainLayers.Length > _layers.Count)
            {
                diagnostic = "The terrain has " + terrainLayers.Length + " layers. The surface has " + _layers.Count + ".";
                return false;
            }
            for (int index = 0; index < terrainLayers.Length; index++)
            {
                if (terrainLayers[index] == _layers[index].TerrainLayer)
                {
                    continue;
                }
                diagnostic = "Terrain layer " + index + " is '" + LayerName(terrainLayers[index])
                    + "'. The surface expects '" + LayerName(_layers[index].TerrainLayer) + "'.";
                return false;
            }
            return true;
        }

        private static string LayerName(TerrainLayer layer)
        {
            return layer ? layer.name : "none";
        }
        #endregion

        #region Material
        // The shader feature keywords of the surface settings.
        private void ApplyKeywords(Material material)
        {
            if (!material.shader) return;
            var quality = new UnityEngine.Rendering.LocalKeyword(material.shader, QualityPackingKeyword);
            material.SetKeyword(quality, ArrayLayout == PackingLayout.Quality);
            // Triplanar is a keyword, not only a uniform: the three-projection code needs many more registers, which
            // slows every pixel even where only the top projection has weight.
            material.SetKeyword(new UnityEngine.Rendering.LocalKeyword(material.shader, TriplanarKeyword), Triplanar.Enabled);
            // Without height blending the shader samples each layer in one pass and loops over the projections.
            // The height blend keeps the state of all layers until the blend weights are known, which is slower.
            // The variance-preserving mode is linear and keeps the fast path.
            bool heightBlend = (_blendMode != BlendMode.NormalizedLinear && _blendMode != BlendMode.VariancePreserving)
                || (Triplanar.Enabled && Triplanar.Mode == TriplanarMode.HeightBlended);
            material.SetKeyword(new UnityEngine.Rendering.LocalKeyword(material.shader, HeightBlendKeyword), heightBlend);
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor only. Writes the saved variant materials of the surface as sub-assets with the current keywords, so a
        /// player build keeps the shader variants of the runtime materials. Returns true when a material changed.
        /// </summary>
        public bool UpdateBuildVariants()
        {
            bool changed = UpdateBuildVariant(ref _nativeVariants, _nativeShader, " (Native Variants)");
            changed |= UpdateBuildVariant(ref _patchesVariants, _patchesShader, " (Patches Variants)");
            if (changed) UnityEditor.EditorUtility.SetDirty(this);
            return changed;
        }

        private bool UpdateBuildVariant(ref Material variants, Shader shader, string suffix)
        {
            if (!shader) return false;
            bool changed = false;
            if (!variants)
            {
                variants = new Material(shader) { name = name + suffix, hideFlags = HideFlags.HideInHierarchy };
                UnityEditor.AssetDatabase.AddObjectToAsset(variants, this);
                changed = true;
            }
            else if (variants.shader != shader)
            {
                variants.shader = shader;
                changed = true;
            }
            string keywords = string.Join(" ", variants.shaderKeywords);
            ApplyKeywords(variants);
            changed |= keywords != string.Join(" ", variants.shaderKeywords);
            if (changed) UnityEditor.EditorUtility.SetDirty(variants);
            return changed;
        }
#endif

        /// <summary>Writes the shared surface values to a material of a surface target.</summary>
        public void ApplyTo(Material material)
        {
            if (!material) return;
            material.SetTexture(AlbedoArrayId, _albedoArray);
            material.SetTexture(NormalArrayId, _normalArray);
            if (_maskArray)
            {
                material.SetTexture(MaskArrayId, _maskArray);
            }
            ApplyKeywords(material);
            material.SetTexture(LayerPropsId, GetLayerProperties());
            material.SetVector(UVId, new Vector4(_worldUVScale.x, _worldUVScale.y, _worldUVOffset.x, _worldUVOffset.y));
            material.SetVector(BlendId, new Vector4((float)_blendMode, _layersPerPixel, _heightBlendContrast, _heightReach));
            material.SetVector(HybridId, new Vector4(_hybridDistances.x, _hybridDistances.y, 0f, 0f));
            material.SetVector(EdgeId, new Vector4(_edgeWidth, 1f / Mathf.Max(0.1f, _edgeNoiseSize), _edgeNoiseStrength,
                _varianceGainLimit));
            material.SetVector(FadeId, new Vector4((float)_fadeDistanceMode, FadeDistancePerFootprint, 0f, 0f));
            material.SetVector(StochasticId, new Vector4(_stochastic ? 1f : 0f, _stochasticCellScale, _stochasticContrast, 0f));
            material.SetVector(LuminanceId, _saturationLuminance);
            material.SetVector(DebugId, new Vector4((float)_debugView, 0f, 0f, 0f));
            ApplyAntiTiling(material);
            ApplyTriplanar(material);
            ParallaxSettings parallax = Parallax;
            material.SetVector(ParallaxId, new Vector4(parallax.IsActive ? parallax.Depth : 0f, parallax.FadeStart,
                Mathf.Max(parallax.FadeEnd, parallax.FadeStart + 0.01f), parallax.MaxSteps));
            ApplyFarDetail(material);
            ApplyFields(material);
            foreach ((int id, Vector4 value) in CoverValues())
            {
                material.SetVector(id, value);
            }
            // A cache page texel reads the heights one far curvature step around it, and the control maps up to the
            // largest control noise offset. Changed terrain data invalidates the pages within this radius.
            Vector4 curvature = material.GetVector(FarCurvatureId);
            Vector4 controlNoise = material.GetVector(ControlNoiseId);
            material.SetFloat(DependencyRadiusId, (curvature.x > 0f ? curvature.y : 0f) + 1.4f * controlNoise.y);
        }

        /// <summary>
        /// Writes the world colour and ground cover values of the surface to a renderer block, for a mesh that blends
        /// into the terrain ground (LoogaTerrainSurfaceCover.hlsl).
        /// </summary>
        public void ApplyCoverTo(MaterialPropertyBlock block)
        {
            foreach ((int id, Vector4 value) in CoverValues())
            {
                block.SetVector(id, value);
            }
        }

        // The world colour and ground cover values. A feature that is off gets a strength of 0. The shader skips it with
        // a dynamic branch.
        private (int id, Vector4 value)[] CoverValues()
        {
            WorldColorSettings world = WorldColor;
            bool active = world.IsActive;
            Vector2 fade = world.FadeDistances;
            Vector4 worldColor = new(active ? world.NearStrength : 0f, active ? world.FarStrength : 0f, fade.x,
                Mathf.Max(fade.y, fade.x + 0.01f));
            // The shader compares the cosine of the slope. Above the maximum slope the tint fades out over the range.
            float startCos = Mathf.Cos(world.MaxSlope * Mathf.Deg2Rad);
            float endCos = Mathf.Cos(Mathf.Min(world.MaxSlope + world.SlopeRange, 90f) * Mathf.Deg2Rad);
            if (world.MaxSlope >= 90f)
            {
                startCos = -1f;
                endCos = -2f;
            }
            GroundCoverSettings cover = GroundCover;
            Vector2 dense = cover.DenseCoverage;
            return new[]
            {
                (WorldColorId, worldColor),
                (WorldColor2Id, new Vector4(active ? (float)world.Blend : 0f, startCos, endCos, 0f)),
                (GroundCoverId, new Vector4(cover.IsActive ? cover.Strength : 0f, cover.CanopyStart,
                    Mathf.Max(cover.CanopyEnd, cover.CanopyStart + 1f), cover.FarCoverageMip)),
                (GroundCoverShadeId, new Vector4(cover.UnderCanopyShade, cover.UnderCanopyTint, cover.UnderCanopyAlbedo,
                    cover.CanopyAlbedo)),
                (GroundCoverSoilId, new Vector4(cover.SoilLuminance, cover.SoilDetail, cover.SoilRelief, cover.CanopyShadow)),
                (GroundCoverViewId, new Vector4(cover.BareCast.x, cover.BareCast.y, cover.PlantAspect, cover.MaxGrazing)),
                (GroundCoverDenseId, new Vector4(dense.x, Mathf.Max(dense.y, dense.x + 0.001f), cover.CanopyOcclusion,
                    cover.CanopySheen)),
                (GroundCoverDetailId, new Vector4(1f / cover.CanopyDetailSize, cover.CanopyDetailStrength,
                    cover.CanopyDetailNormal, cover.CanopySunSheen))
            };
        }

        // A far detail that is off gets strengths of 0. The shader skips each part with a dynamic branch.
        private void ApplyFarDetail(Material material)
        {
            FarDetailSettings far = FarDetail;
            bool active = far.Enabled;
            float startCos = Mathf.Cos(far.StrataSlope * Mathf.Deg2Rad);
            float endCos = Mathf.Cos(Mathf.Min(far.StrataSlope + far.StrataSlopeRange, 90f) * Mathf.Deg2Rad);
            material.SetVector(FarDetailId, new Vector4(far.FadeStart, 1f / far.FadeRange, startCos, endCos));
            material.SetVector(FarStrataId, new Vector4(1f / far.StrataThickness, far.StrataWarp,
                active ? far.StrataStrength : 0f, active ? far.StrataRelief : 0f));
            material.SetVector(FarCurvatureId, new Vector4(active ? far.CurvatureStrength : 0f, far.CurvatureStep,
                far.CurvatureRange, 0f));
        }

        // Fields that are off get strengths of 0. The shader skips the map read with a dynamic branch.
        private void ApplyFields(Material material)
        {
            SurfaceFieldSettings fields = Fields;
            bool active = fields.IsActive;
            // The map stores the wetness index 2 to 15 and the log10 flow area 0 to 6 as 0 to 1.
            Vector2 wet = StoredRange((fields.WetRange - new Vector2(2f, 2f)) / 13f);
            Vector2 channel = StoredRange(fields.ChannelRange / 6f);
            Vector2 rim = StoredRange(fields.RimRange);
            Vector2 foot = StoredRange(fields.FootRange);
            Color color = fields.FootColor.linear;
            material.SetVector(FieldWetnessId, new Vector4(active ? fields.WetDarkening : 0f,
                active ? fields.WetSmoothness : 0f, wet.x, wet.y));
            material.SetVector(FieldChannelId, new Vector4(active ? fields.ChannelDarkening : 0f,
                active ? fields.ChannelSmoothness : 0f, channel.x, channel.y));
            material.SetVector(FieldRimId, new Vector4(active ? fields.RimLightening : 0f,
                active ? fields.RimCoverLoss : 0f, rim.x, rim.y));
            material.SetVector(FieldFootId, new Vector4(color.r, color.g, color.b, active ? fields.FootStrength : 0f));
            material.SetVector(FieldFootRangeId, new Vector4(foot.x, foot.y, active ? 1f : 0f,
                Mathf.Cos(fields.MaxSlope * Mathf.Deg2Rad)));
        }

        // A start and 1 / width of a range.
        private static Vector2 StoredRange(Vector2 range)
        {
            return new Vector2(range.x, 1f / Mathf.Max(range.y - range.x, 0.001f));
        }

        private void ApplyTriplanar(Material material)
        {
            TriplanarSettings triplanar = Triplanar;
            // The shader compares the cosine of the slope, so it needs no trigonometry per pixel.
            float startCos = Mathf.Cos(triplanar.SlopeStart * Mathf.Deg2Rad);
            float endCos = Mathf.Cos(Mathf.Min(triplanar.SlopeStart + triplanar.SlopeRange, 89.9f) * Mathf.Deg2Rad);
            material.SetVector(TriplanarId, new Vector4(triplanar.Enabled ? 1f : 0f, triplanar.Contrast, startCos, endCos));
            // Face normal: the replacement starts at the angle and is complete at 1.5 times the angle (1 - cos units).
            float threshold = 1f;
            float range = 0.05f;
            if (triplanar.FaceNormalAngle > 0f)
            {
                threshold = 1f - Mathf.Cos(triplanar.FaceNormalAngle * Mathf.Deg2Rad);
                range = Mathf.Max(1f - Mathf.Cos(triplanar.FaceNormalAngle * 1.5f * Mathf.Deg2Rad) - threshold, 1e-4f);
            }
            material.SetVector(Triplanar2Id, new Vector4((float)triplanar.Mode, triplanar.HeightBlendContrast, threshold, range));
        }

        // A feature that is off gets a strength of 0. The shader skips it with a dynamic branch.
        private void ApplyAntiTiling(Material material)
        {
            AntiTilingSettings settings = AntiTiling;
            DetailNoiseSettings detail = settings.DetailNoise;
            material.SetTexture(DetailNoiseTexId, detail.Texture ? detail.Texture : Texture2D.grayTexture);
            material.SetVector(DetailNoiseId, new Vector4(1f / detail.Size, detail.IsActive ? detail.Strength : 0f,
                detail.FadeDistance, 0f));

            DistanceNoiseSettings distance = settings.DistanceNoise;
            material.SetTexture(DistanceNoiseTexId, distance.Texture ? distance.Texture : Texture2D.grayTexture);
            material.SetVector(DistanceNoiseId, new Vector4(1f / distance.Size, distance.IsActive ? distance.Strength : 0f,
                distance.FadeStart, distance.FadeRange));

            ApplyNormalNoise(material, settings.NormalNoise1, NormalNoiseTex1Id, NormalNoise1Id);
            ApplyNormalNoise(material, settings.NormalNoise2, NormalNoiseTex2Id, NormalNoise2Id);
            ApplyNormalNoise(material, settings.NormalNoise3, NormalNoiseTex3Id, NormalNoise3Id);

            NoiseHeightSettings height = settings.NoiseHeight;
            material.SetVector(NoiseHeightId, new Vector4(1f / height.Size, height.IsActive ? height.Amplitude : 0f, 0f, 0f));

            ControlNoiseSettings control = settings.ControlNoise;
            material.SetVector(ControlNoiseId, new Vector4(1f / control.Size, control.IsActive ? control.Strength : 0f, 0f, 0f));

            DistanceResampleSettings resample = settings.DistanceResample;
            bool albedo = resample.Enabled && resample.Maps != ResampleMaps.Normal;
            bool normal = resample.Enabled && resample.Maps != ResampleMaps.Albedo;
            material.SetVector(ResampleId, new Vector4(resample.UVScale, resample.FadeStart,
                Mathf.Max(resample.FadeEnd, resample.FadeStart + 0.01f), (float)resample.Fade));
            material.SetVector(ResampleBlendId, new Vector4(albedo ? resample.AlbedoStrength : 0f,
                normal ? resample.NormalStrength : 0f, (float)resample.AlbedoBlend, 1f / resample.NoiseSize));
            material.SetVector(ResampleNoiseId, new Vector4(resample.NoiseStrength, 0f, 0f, 0f));
        }

        private static void ApplyNormalNoise(Material material, NormalNoiseSettings noise, int textureId, int valuesId)
        {
            material.SetTexture(textureId, noise.Texture ? noise.Texture : Texture2D.normalTexture);
            material.SetVector(valuesId, new Vector4(1f / noise.Size, noise.IsActive ? noise.Strength : 0f, 0f, 0f));
        }

        // One column per layer, one row per property group. The shader reads exact texels with Load.
        private Texture2D GetLayerProperties()
        {
            if (_layerProperties && _layerProperties.height != (int)PropertyRow.Count)
            {
                DestroyResource(ref _layerProperties);
            }
            if (!_layerProperties)
            {
                _layerProperties = new Texture2D(MaxLayers, (int)PropertyRow.Count, TextureFormat.RGBAFloat, false, true)
                {
                    name = name + " Layer Properties",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
            }
            var pixels = new Color[MaxLayers * (int)PropertyRow.Count];
            for (int index = 0; index < MaxLayers; index++)
            {
                Layer layer = index < _layers.Count ? _layers[index] : null;
                Color albedoMean = _albedoMeans != null && index < _albedoMeans.Length
                    ? _albedoMeans[index]
                    : Color.clear;
                WriteLayer(pixels, index, layer ?? new Layer(null), albedoMean, _saturationLuminance);
            }
            _layerProperties.SetPixels(pixels);
            _layerProperties.Apply(false, false);
            return _layerProperties;
        }

        private static void WriteLayer(Color[] pixels, int column, Layer layer, Color albedoMean, Vector3 luminance)
        {
            pixels[Index(PropertyRow.UV, column)] = new Color(layer.UVScale.x, layer.UVScale.y, layer.UVOffset.x, layer.UVOffset.y);
            Color tint = layer.Tint;
            pixels[Index(PropertyRow.Color, column)] = new Color(tint.r, tint.g, tint.b, layer.Saturation);
            pixels[Index(PropertyRow.Adjust, column)] = new Color(layer.Brightness, layer.Contrast,
                layer.UVRotation * Mathf.Deg2Rad, layer.Stochastic ? 1f : 0f);
            pixels[Index(PropertyRow.Material, column)] = new Color(layer.NormalStrength, layer.Smoothness,
                layer.OcclusionStrength, layer.Metallic);
            pixels[Index(PropertyRow.Height, column)] = new Color(layer.HeightOffset, layer.HeightContrast,
                layer.InterpolationContrast, layer.ParallaxDepth);
            Vector3 normalNoise = layer.NormalNoiseStrength;
            pixels[Index(PropertyRow.Noise, column)] = new Color(layer.DetailNoiseStrength, layer.DistanceNoiseStrength,
                normalNoise.x, normalNoise.y);
            pixels[Index(PropertyRow.Noise2, column)] = new Color(normalNoise.z, layer.NoiseHeightAmplitude,
                layer.ResampleStrength, layer.ResampleUVScale);
            Vector3 adjustedMean = AdjustedMean(layer, albedoMean, luminance);
            pixels[Index(PropertyRow.Cover, column)] = new Color(layer.WorldColorStrength, layer.GroundCoverStrength,
                Vector3.Dot(adjustedMean, luminance), layer.BareFarCover);
            pixels[Index(PropertyRow.Object, column)] = new Color(layer.ObjectDust, adjustedMean.x, adjustedMean.y,
                adjustedMean.z);
        }

        // Mean albedo after the colour adjustments of AdjustLoogaSurfaceLayerColor (LoogaTerrainSurfaceLayers.hlsl,
        // keep them in sync). Zero without a measured mean. The adjustments are linear except for the clamps, so the
        // adjusted mean is close to the mean of the adjusted texels.
        private static Vector3 AdjustedMean(Layer layer, Color mean, Vector3 weights)
        {
            if (mean.a <= 0f) return Vector3.zero;

            var color = new Vector3(mean.r, mean.g, mean.b);
            color = Vector3.LerpUnclamped(Vector3.one * Vector3.Dot(color, weights), color, layer.Saturation);
            Color tint = layer.Tint;
            color = Vector3.Scale(color, new Vector3(tint.r, tint.g, tint.b));
            for (int channel = 0; channel < 3; channel++)
            {
                float contrasted = Mathf.Clamp01((color[channel] - 0.5f) * layer.Contrast + 0.5f);
                color[channel] = Mathf.Clamp01(contrasted + layer.Brightness);
            }
            return color;
        }

        private static int Index(PropertyRow row, int column)
        {
            return (int)row * MaxLayers + column;
        }

        private void NotifyChanged()
        {
            if (_nativeMaterial)
            {
                ApplyTo(_nativeMaterial);
            }
            if (_patchesMaterial)
            {
                ApplyTo(_patchesMaterial);
            }
            if (_meshMaterial)
            {
                ApplyTo(_meshMaterial);
            }
            Changed?.Invoke();
#if UNITY_EDITOR
            AnyChanged?.Invoke(this);
#endif
        }

        private static void DestroyResource<T>(ref T resource) where T : UnityEngine.Object
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
            resource = null;
        }
        #endregion
    }
}
