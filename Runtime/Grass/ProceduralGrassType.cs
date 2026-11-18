using System.Runtime.InteropServices;
using UnityEngine;

namespace LoogaSoft.Terrain.Grass
{
    /// <summary>
    /// The look of one procedural grass type: blade shape, clumps, color and wind response.
    /// A ProceduralGrassRenderer maps a terrain detail layer to each type.
    /// </summary>
    [CreateAssetMenu(menuName = "Looga/Terrain/Procedural Grass Type", fileName = "Procedural Grass Type")]
    public sealed class ProceduralGrassType : ScriptableObject
    {
        [Header("Density")]
        [Tooltip("Blades per square meter at full coverage. The renderer lattice limits the highest value.")]
        [SerializeField, Min(0f)] private float _density = 150f;
        [Tooltip("Detail coverage (0-1) that gives the full density and length. Lower coverage gives fewer, " +
                 "shorter blades.")]
        [SerializeField, Range(0.01f, 1f)] private float _fullCoverage = 0.3f;

        [Header("Shape")]
        [Tooltip("Shortest and longest blade length in meters, measured along the blade.")]
        [SerializeField] private Vector2 _lengthRange = new Vector2(0.5f, 0.85f);
        [Tooltip("Blade width at the root in meters.")]
        [SerializeField, Min(0.001f)] private float _width = 0.012f;
        [Tooltip("Lowest and highest angle in degrees between the root-to-tip line and up. 0 = upright blades.")]
        [SerializeField] private Vector2 _tiltRange = new Vector2(25f, 75f);
        [Tooltip("Extra turn of the blade tip in degrees. Larger values make the tips droop.")]
        [SerializeField, Range(0f, 90f)] private float _droop = 40f;

        [Header("Clumps")]
        [Tooltip("Mean distance between clump centres in meters.")]
        [SerializeField, Min(0.05f)] private float _clumpSize = 0.6f;
        [Tooltip("Length difference between clumps. 0 = all clumps have the same length.")]
        [SerializeField, Range(0f, 1f)] private float _clumpHeight = 0.5f;
        [Tooltip("Share of the clump facing in the blade facing. 1 = all blades of a clump face the same way.")]
        [SerializeField, Range(0f, 1f)] private float _clumpFacing = 0.7f;
        [Tooltip("Pull of the blade roots toward the clump centre.")]
        [SerializeField, Range(0f, 1f)] private float _clumpPull = 0.45f;
        [Tooltip("Clumping of grass under the full coverage. Thin grass keeps some clumps and the middle of each kept " +
                 "clump, so it grows in tufts and not as single blades. 1 = fewer clumps at full size. 0 = all clumps, " +
                 "each one smaller.")]
        [SerializeField, Range(0f, 1f)] private float _sparseClumping = 0.5f;
        [Tooltip("Length difference between patches of the field, on top of the clump difference. Tall patches rise by " +
                 "up to 1.5 x this share out of a closed mat (low patches lose 0.35 x), so a large field shows depth. " +
                 "0 = no patches.")]
        [SerializeField, Range(0f, 0.9f)] private float _patchHeight = 0.4f;
        [Tooltip("Mean size of the taller and shorter patches in meters.")]
        [SerializeField, Min(0.5f)] private float _patchSize = 6f;

        [Header("Color")]
        [Tooltip("Mid-blade color without a world color map.")]
        [SerializeField, ColorUsage(false)] private Color _baseColor = new Color(0.3f, 0.36f, 0.12f);
        [Tooltip("Share of the terrain world color map (LoogaTerrainSurfaceGlobals) in the base color, read at the " +
                 "clump centre. 0 = only the base color.")]
        [SerializeField, Range(0f, 1f)] private float _colorMapShare = 0.7f;
        [Tooltip("Multiplier of the world color map color.")]
        [SerializeField, Min(0f)] private float _colorMapBrightness = 1f;
        [Tooltip("Root color as a share of the base color.")]
        [SerializeField, Range(0f, 1f)] private float _rootShade = 0.3f;
        [Tooltip("Tip color as a multiple of the base color.")]
        [SerializeField, Range(0f, 3f)] private float _tipShade = 1.25f;
        [Tooltip("Tint of the second tip color. Each clump takes a random mix of the two tip colors.")]
        [SerializeField, ColorUsage(false, true)] private Color _clumpTipTint = new Color(1.15f, 1.05f, 0.8f);
        [Tooltip("Color saturation. 1 keeps the color, larger values make it more saturated.")]
        [SerializeField, Range(0f, 2f)] private float _saturation = 1.2f;
        [Tooltip("Brightness difference between clumps.")]
        [SerializeField, Range(0f, 0.5f)] private float _clumpShade = 0.2f;
        [Tooltip("Ambient occlusion at the root. 1 = no darkening.")]
        [SerializeField, Range(0f, 1f)] private float _rootOcclusion = 0.25f;
        [SerializeField, Range(0f, 1f)] private float _smoothness = 0.5f;
        [Tooltip("Looga thin leaf transmission. 1 transmits about as much light as the blade reflects.")]
        [SerializeField, Range(0f, 2f)] private float _transmission = 1f;
        [Tooltip("Blend of the blade normal toward up. Higher values light the blades more like the ground.")]
        [SerializeField, Range(0f, 1f)] private float _normalUp = 0.2f;
        [Tooltip("Outward turn of the normals at the blade edges, so a flat blade shades like a rounded one.")]
        [SerializeField, Range(0f, 1f)] private float _roundness = 0.4f;
        [Tooltip("Largest twist in degrees of a blade from the root to the tip. Twisted blades face many directions, so " +
                 "some blades reflect the sun toward the camera from every view.")]
        [SerializeField, Range(0f, 180f)] private float _twist = 70f;
        [Tooltip("Random smoothness difference between blades, as a share of the smoothness.")]
        [SerializeField, Range(0f, 1f)] private float _smoothnessVariation = 0.35f;
        [Tooltip("Density of the canopy for the main light. The grass above a blade point hides the sun from it, so the " +
                 "lower part of dense grass is in shade. 0 = no canopy shadow.")]
        [SerializeField, Range(0f, 6f)] private float _canopyShadow = 1.8f;

        [Header("Wind")]
        [Tooltip("Extra lean in degrees at full wind strength.")]
        [SerializeField, Min(0f)] private float _windResponse = 30f;
        [Tooltip("Sway frequency in radians per second.")]
        [SerializeField, Min(0f)] private float _windFrequency = 1.5f;
        [Tooltip("Turn of the blade facing toward the wind direction. 1 = the whole field leans downwind.")]
        [SerializeField, Range(0f, 1f)] private float _windAlign = 0.5f;

        // Tip share of the mean blade color that thin blades take. Keep it in sync with the thin blade smoothing in
        // LoogaProceduralGrass.shader.
        private const float DistantTipShare = 0.6f;

        public float Density => _density;
        public float FullCoverage => _fullCoverage;
        public float ColorMapShare => _colorMapShare;
        public float ColorMapBrightness => _colorMapBrightness;
        public float Saturation => _saturation;
        public float Transmission => _transmission;
        public float CanopyShadow => _canopyShadow;

        // Occurs after an edit of the type in the editor. The renderers that draw the type upload it again.
        internal static event System.Action<ProceduralGrassType> Edited;

#if UNITY_EDITOR
        private void OnValidate()
        {
            Edited?.Invoke(this);
        }
#endif

        /// <summary>
        /// Share of the renderer lattice points that grow a blade of this type at a detail coverage (0-1), before the
        /// renderer limits the sum of all types at a point to 1. The renderer has maximumDensity lattice points per
        /// square meter. Keep it in sync with the type weight in LoogaProceduralGrass.compute.
        /// </summary>
        public float BladeShare(float coverage, float maximumDensity)
        {
            float typeShare = Mathf.Clamp01(_density / Mathf.Max(maximumDensity, 0.001f));
            return Mathf.Clamp01(coverage / _fullCoverage) * typeShare;
        }

        /// <summary>
        /// Ground area in square meters that one blade hides seen from above, at a detail coverage (0-1): the mean
        /// length, shortened at low coverage as the renderer does, times the root width and the mean sine of the tilt.
        /// The clump and patch length factors average to 1. Ground tools use it for the canopy closure.
        /// </summary>
        public float BladeFootprint(float coverage)
        {
            float fullness = Mathf.Clamp01(coverage / _fullCoverage);
            float length = (_lengthRange.x + Mathf.Max(_lengthRange.x, _lengthRange.y)) * 0.5f *
                Mathf.Lerp(0.5f, 1f, fullness);
            float low = _tiltRange.x * Mathf.Deg2Rad;
            float high = Mathf.Max(_tiltRange.x, _tiltRange.y) * Mathf.Deg2Rad;
            float sine = high - low > 1e-4f ? (Mathf.Cos(low) - Mathf.Cos(high)) / (high - low) : Mathf.Sin(low);
            return length * _width * sine;
        }

        /// <summary>
        /// Per-channel factor from a blade color to its mean albedo on thin, distant blades: the mean of the root and
        /// tip colors, with the mean of the two tip colors. The saturation follows the factor. The blade shading is
        /// linear, so ground tools can apply the factor and the saturation to any blade color, such as the world color
        /// map part.
        /// </summary>
        public Vector3 DistantShade
        {
            get
            {
                Color tip = _tipShade * Color.Lerp(Color.white, _clumpTipTint, 0.5f);
                Color shade = Color.Lerp(new Color(_rootShade, _rootShade, _rootShade), tip, DistantTipShare);
                return new Vector3(shade.r, shade.g, shade.b);
            }
        }

        /// <summary>
        /// Linear albedo of the base color as thin, distant blades show it (<see cref="DistantShade"/> and the
        /// saturation). Ground tools use it to color the far canopy.
        /// </summary>
        public Color DistantBaseAlbedo
        {
            get
            {
                Color baseColor = _baseColor.linear;
                Vector3 shade = DistantShade;
                var albedo = new Color(baseColor.r * shade.x, baseColor.g * shade.y, baseColor.b * shade.z);
                float luminance = 0.2126f * albedo.r + 0.7152f * albedo.g + 0.0722f * albedo.b;
                albedo = Color.LerpUnclamped(new Color(luminance, luminance, luminance), albedo, _saturation);
                return new Color(Mathf.Clamp01(albedo.r), Mathf.Clamp01(albedo.g), Mathf.Clamp01(albedo.b), 1f);
            }
        }

        /// <summary>
        /// Writes the two ground cover values of the type (LoogaGrassGroundCover.compute) at index x 2: the lattice share
        /// at full coverage, the full coverage, the blade footprint at full coverage and the share of the own colour,
        /// then the distant base albedo over the canopy colour scale. Keep it in sync with BladeShare and BladeFootprint.
        /// </summary>
        internal void WriteCoverGpu(Vector4[] target, int index, float maximumDensity, float colorScale)
        {
            float typeShare = Mathf.Clamp01(_density / Mathf.Max(maximumDensity, 0.001f));
            Color albedo = DistantBaseAlbedo / Mathf.Max(colorScale, 1e-4f);
            target[index * 2] = new Vector4(typeShare, _fullCoverage, BladeFootprint(_fullCoverage),
                Mathf.Max(0f, 1f - ColorMapShare));
            target[index * 2 + 1] = new Vector4(albedo.r, albedo.g, albedo.b, 0f);
        }

        /// <summary>GPU layout of a type. Keep it in sync with GrassType in ProceduralGrassCommon.hlsl.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct GpuData
        {
            public Vector4 Shape;
            public Vector4 Bend;
            public Vector4 Clump;
            public Vector4 Base;
            public Vector4 Tint;
            public Vector4 Wind;
            public Vector4 Normal;
            public Vector4 Shade;
            public Vector4 Detail;
            public Vector4 Patch;
        }

        internal const int GpuStride = 10 * 16;

        internal GpuData ToGpu(float maximumDensity)
        {
            Color baseColor = _baseColor.linear;
            return new GpuData
            {
                Shape = new Vector4(_lengthRange.x, Mathf.Max(_lengthRange.x, _lengthRange.y), _width, _fullCoverage),
                Bend = new Vector4(_tiltRange.x * Mathf.Deg2Rad, Mathf.Max(_tiltRange.x, _tiltRange.y) * Mathf.Deg2Rad,
                    _droop * Mathf.Deg2Rad, Mathf.Clamp01(_density / Mathf.Max(maximumDensity, 0.001f))),
                Clump = new Vector4(_clumpSize, _clumpHeight, _clumpFacing, _clumpPull),
                Base = new Vector4(baseColor.r, baseColor.g, baseColor.b, _smoothness),
                Tint = new Vector4(_clumpTipTint.r, _clumpTipTint.g, _clumpTipTint.b, _transmission),
                Wind = new Vector4(_windResponse * Mathf.Deg2Rad, _windFrequency, _clumpShade, _rootOcclusion),
                Normal = new Vector4(_normalUp, _roundness, _windAlign, _colorMapBrightness),
                Shade = new Vector4(_rootShade, _tipShade, _saturation, _colorMapShare),
                Detail = new Vector4(_twist * Mathf.Deg2Rad, _smoothnessVariation, _canopyShadow, _sparseClumping),
                Patch = new Vector4(_patchHeight, 1f / _patchSize, 0f, 0f)
            };
        }
    }
}
