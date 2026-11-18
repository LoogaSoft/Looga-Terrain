#ifndef LOOGA_TERRAIN_SURFACE_COVER_INCLUDED
#define LOOGA_TERRAIN_SURFACE_COVER_INCLUDED

// World colour map tint and ground cover of the Looga terrain surface. The terrain surface and the meshes that blend
// into the terrain ground (rock faces) use the same functions, so the ground looks the same on both. The material
// buffer of the shader that includes this file declares _LoogaWorldColor, _LoogaWorldColor2, _LoogaGroundCover,
// _LoogaGroundCoverShade, _LoogaGroundCoverSoil, _LoogaGroundCoverView, _LoogaGroundCoverDense and
// _LoogaGroundCoverDetail (LoogaTerrainSurfaceInput.hlsl). LoogaTerrainSurface writes them.
#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceWorld.hlsl"

static const half3 LoogaLuminanceWeights = half3(0.2126, 0.7152, 0.0722);

struct LoogaWorldDebug
{
    half3 worldColor;     // Tint colour, black where the tint is off
    half3 groundCover;    // r cover blend, g far share, b bare cast
    half2 farShade;       // Luminance shares of the far albedo: x ground between the plants, y plants. 0 = no shade.
};

half3 LoogaOverlay(half3 base, half3 blend)
{
    return base < 0.5h ? 2.0h * base * blend : 1.0h - 2.0h * (1.0h - base) * (1.0h - blend);
}

// Terrain tint from the world colour map.
void ApplyLoogaWorldColor(inout half3 albedo, float3 positionWS, float3 normalWS, float cameraDistance,
    float2 positionDx, float2 positionDy, half layerStrength, inout LoogaWorldDebug debug)
{
    int mode = (int)_LoogaWorldColor2.x;
    UNITY_BRANCH
    if (mode == 0 || _LoogaWorldColorState.x < 0.5 || layerStrength <= 0) return;
    float4 coord = LoogaWorldColorCoord(positionWS.xz);
    UNITY_BRANCH
    if (coord.w < 0.5) return;
    half3 color = LoogaWorldColorGrad(coord, positionDx, positionDy);
    float strength = lerp(_LoogaWorldColor.x, _LoogaWorldColor.y,
        smoothstep(_LoogaWorldColor.z, _LoogaWorldColor.w, cameraDistance));
    // Above the maximum slope the tint fades out. y and z are the cosines of the fade start and end slopes.
    strength *= saturate((normalWS.y - _LoogaWorldColor2.z) / max(_LoogaWorldColor2.y - _LoogaWorldColor2.z, 1e-4));
    half3 blended = mode == 1 ? saturate(albedo * color * 2.0h) : mode == 2 ? LoogaOverlay(albedo, color) : color;
    albedo = lerp(albedo, blended, (half)strength * layerStrength);
    debug.worldColor = color;
}

// Region colour map. The map colour replaces the albedo colour. The layer detail keeps the brightness of the albedo
// relative to the mean brightness of its layers (detail), so the texture grain stays.
void ApplyLoogaRegionColorDetail(inout half3 albedo, float3 positionWS, float cameraDistance, float2 positionDx,
    float2 positionDy, half detail)
{
    UNITY_BRANCH
    if (_LoogaRegionColorState.x < 0.5) return;
    float2 uv = (positionWS.xz - _LoogaRegionColorArea.xy) * _LoogaRegionColorArea.zw;
    UNITY_BRANCH
    if (any(uv < 0) || any(uv > 1)) return;
    float2 dx = positionDx * _LoogaRegionColorArea.zw;
    float2 dy = positionDy * _LoogaRegionColorArea.zw;
    half3 color = SAMPLE_TEXTURE2D_GRAD(_LoogaRegionColorMap, sampler_LoogaRegionColorMap, uv, dx, dy).rgb;
    color = max(lerp(dot(color, LoogaLuminanceWeights).xxx, color, (half)_LoogaRegionColorGrade.y), 0.0h)
        * (half)_LoogaRegionColorGrade.x;
    half weight = _LoogaRegionColorState.z > 0.5
        ? SAMPLE_TEXTURE2D_GRAD(_LoogaRegionColorWeight, sampler_LoogaRegionColorWeight, uv, dx, dy).r
        : 1.0h;
    half strength = (half)lerp(_LoogaRegionColorFade.x, _LoogaRegionColorFade.y,
        smoothstep(_LoogaRegionColorFade.z, _LoogaRegionColorFade.w, cameraDistance)) * weight;
    UNITY_BRANCH
    if (strength <= 0) return;
    half3 target = saturate(color * lerp(1.0h, clamp(detail, 0.25h, 3.0h), (half)_LoogaRegionColorState.y));
    albedo = lerp(albedo, target, strength);
}

// Region colour map with the detail from the albedo. meanLuminance is 0 when it is unknown.
void ApplyLoogaRegionColor(inout half3 albedo, float3 positionWS, float cameraDistance, float2 positionDx,
    float2 positionDy, half meanLuminance)
{
    half detail = meanLuminance > 0 ? dot(albedo, LoogaLuminanceWeights) / meanLuminance : 1.0h;
    ApplyLoogaRegionColorDetail(albedo, positionWS, cameraDistance, positionDx, positionDy, detail);
}

// Bare far cover: ground layers that look like plant cover from far away, where no plants grow. Coverage above this
// value removes the bare cover, so the bare cover and the plant cover meet without a seam.
static const half LoogaBareCoverFade = 0.1h;

// Share of the bare far cover at a point with the given plant coverage.
half LoogaBareCoverShare(half bareCover, half coverage)
{
    return bareCover * saturate(1.0h - coverage / LoogaBareCoverFade);
}

// Main light visibility through the plant canopy (Beer-Lambert, as the procedural grass canopy shadow). Near the camera
// the plants draw and the ground under them is in their shade. At distance the ground that shows between the plants is
// in their shade, and the plants take the mean visibility over their height, as the blades take it (farShade, the
// luminance shares of ground and plants in the far albedo). The main light visibility multiplies the specular light
// too, so the shares must be shaded apart: a single canopy albedo factor cannot dim the specular light. 1 = full main
// light.
// Far main light visibility of a canopy of the given closure: the ground share in the canopy shade, the plant share at
// the mean visibility over the plant height.
half LoogaFarCanopyVisibility(half closure, half2 farShade, out half groundVisibility)
{
    half canopyDepth = (half)(_LoogaGroundCoverSoil.w * closure / max(_MainLightPosition.y, 0.2));
    groundVisibility = exp(-canopyDepth);
    half meanVisibility = canopyDepth > 0.001h ? (1.0h - groundVisibility) / canopyDepth : 1.0h;
    return 1.0h - farShade.x * (1.0h - groundVisibility) - farShade.y * (1.0h - meanVisibility);
}

// bareCover is the bare far cover of the layers (see ApplyLoogaGroundCoverView). Bare cover has no plants near the
// camera, so it shades only the far share.
half LoogaGroundCanopySunVisibility(float3 positionWS, float cameraDistance, half layerStrength, half bareCover,
    half2 farShade)
{
    half visibility = 1.0h;
    UNITY_BRANCH
    if (_LoogaGroundCoverSoil.w <= 0 || _LoogaGroundCover.x <= 0 || _LoogaGroundCoverState.x < 0.5 ||
        (layerStrength <= 0 && bareCover <= 0)) return visibility;
    float2 coverUV = (positionWS.xz - _LoogaGroundCoverArea.xy) * _LoogaGroundCoverArea.zw;
    half far = (half)smoothstep(_LoogaGroundCover.y, _LoogaGroundCover.z, cameraDistance);
    half coverage = 0.0h;
    UNITY_BRANCH
    if (layerStrength > 0 && (far < 1.0h || any(farShade > 0)) && all(coverUV >= 0) && all(coverUV <= 1))
    {
        coverage = SAMPLE_TEXTURE2D_LOD(_LoogaGroundCoverMap, sampler_LoogaGroundCoverMap, coverUV, 0).r;
        half groundVisibility;
        half farVisibility = LoogaFarCanopyVisibility(coverage, farShade, groundVisibility);
        visibility = lerp(1.0h, lerp(groundVisibility, farVisibility, far), layerStrength);
    }
    half bareShare = LoogaBareCoverShare(bareCover, coverage);
    UNITY_BRANCH
    if (bareShare > 0 && far > 0 && any(farShade > 0))
    {
        half unusedGround;
        half bareVisibility = lerp(1.0h, LoogaFarCanopyVisibility(bareShare, farShade, unusedGround), far);
        visibility = min(visibility, bareVisibility);
    }
    return visibility;
}

half LoogaGroundCanopySunVisibility(float3 positionWS, float cameraDistance, half layerStrength, half2 farShade)
{
    return LoogaGroundCanopySunVisibility(positionWS, cameraDistance, layerStrength, 0.0h, farShade);
}

// Light of the far canopy that does not depend on the plant colour, as a flat-ground albedo: the gloss of the blades.
// It is _LoogaGroundCoverDense.w from above. At a low view angle toward the sun it rises by up to
// _LoogaGroundCoverDetail.w. Away from the sun the low-angle part keeps 0.2 of that value.
half LoogaGroundCanopySheen(float3 toCamera)
{
    float2 view = -toCamera.xz;
    float2 sun = _MainLightPosition.xz;
    float alignment = dot(view, sun) * rsqrt(max(dot(view, view) * dot(sun, sun), 1e-8));
    float grazing = 1.0 - saturate(toCamera.y);
    grazing *= grazing;
    return (half)(_LoogaGroundCoverDense.w + _LoogaGroundCoverDetail.w * grazing * grazing *
        lerp(0.2, 1.0, saturate(alignment) * saturate(alignment)));
}

// Ground cover. Near the camera the foliage draws, and the ground under it is shaded and tinted. From the canopy
// start to the canopy end distance, where the foliage thins and fades, the ground changes to the canopy colour by
// the coverage. Bare ground takes a cast of the canopy colour at its own luminance. The soil normal relief and
// smoothness fade under full cover. soilLuminance is the ground albedo luminance before the world colour tint, and
// soilMean the mean albedo luminance of the ground layers (layer cover row z). A soilMean of 0 (not measured) uses the
// soil luminance setting instead. toCamera is the unit direction from the ground to the camera.
// bareCover is the bare far cover of the layers. Where the maps have no plants, the ground changes to the bare canopy
// colour (_LoogaGroundCoverBare) by this share over the canopy distances, as plant cover does. No plants draw there,
// so the ground near the camera does not change.
void ApplyLoogaGroundCoverView(inout half3 albedo, inout float3 surfaceGradient, inout half smoothness,
    float3 positionWS, float3 toCamera, float cameraDistance, float2 positionDx, float2 positionDy, half layerStrength,
    half bareCover, half soilLuminance, half soilMean, inout LoogaWorldDebug debug)
{
    UNITY_BRANCH
    if (_LoogaGroundCover.x <= 0 || _LoogaGroundCoverState.x < 0.5 || (layerStrength <= 0 && bareCover <= 0)) return;
    float2 coverUV = (positionWS.xz - _LoogaGroundCoverArea.xy) * _LoogaGroundCoverArea.zw;
    bool inArea = all(coverUV >= 0) && all(coverUV <= 1);

    // A coarser coverage mip at distance softens the plant edges that the foliage no longer hides.
    float far = smoothstep(_LoogaGroundCover.y, _LoogaGroundCover.z, cameraDistance);
    float coverLod = far * _LoogaGroundCover.w;
    // Canopy colour: the plants with their own colour, and the world colour map for the other plants. Where the
    // world colour map is bound but has no data, the ground keeps its own colour.
    half3 local = 0;
    bool hasColor = false;
    half2 coverage = 0;
    UNITY_BRANCH
    if (inArea && layerStrength > 0)
    {
        half4 canopy = SAMPLE_TEXTURE2D_LOD(_LoogaGroundCoverCanopy, sampler_LoogaGroundCoverCanopy, coverUV,
            coverLod);
        UNITY_BRANCH
        if (_LoogaWorldColorState.x > 0.5)
        {
            float4 coord = LoogaWorldColorCoord(positionWS.xz);
            hasColor = coord.w > 0.5;
            UNITY_BRANCH
            if (hasColor)
            {
                // The plants shade the map colour as their blades do
                // (LoogaTerrainSurfaceGlobals.SetGroundCoverMapShade).
                half3 mapColor = LoogaWorldColorGrad(coord, positionDx, positionDy) *
                    (half3)_LoogaGroundCoverMapShade.rgb;
                mapColor = max(lerp(dot(mapColor, LoogaLuminanceWeights).xxx, mapColor,
                    (half)_LoogaGroundCoverMapShade.a), 0.0h);
                local = mapColor * (1.0h - canopy.a) + canopy.rgb;
            }
        }
        else
        {
            hasColor = canopy.a > 0;
            local = canopy.rgb / max(canopy.a, 0.001h);
        }
        UNITY_BRANCH
        if (hasColor)
        {
            local *= (half)_LoogaGroundCoverState.y;
            coverage = SAMPLE_TEXTURE2D_LOD(_LoogaGroundCoverMap, sampler_LoogaGroundCoverMap, coverUV, coverLod).rg;
        }
    }
    half bareShare = LoogaBareCoverShare(bareCover, coverage.r);
    UNITY_BRANCH
    if (!hasColor && (bareShare <= 0 || far <= 0)) return;
    // The bare far cover takes the world colour map where the map has data, as plants without their own colour do,
    // else the bare canopy colour.
    half3 bareLocal = local;
    UNITY_BRANCH
    if (!hasColor && bareCover > 0 && _LoogaWorldColorState.x > 0.5)
    {
        float4 bareCoord = LoogaWorldColorCoord(positionWS.xz);
        UNITY_BRANCH
        if (bareCoord.w > 0.5)
        {
            half3 mapColor = LoogaWorldColorGrad(bareCoord, positionDx, positionDy) *
                (half3)_LoogaGroundCoverMapShade.rgb;
            bareLocal = max(lerp(dot(mapColor, LoogaLuminanceWeights).xxx, mapColor,
                (half)_LoogaGroundCoverMapShade.a), 0.0h) * (half)_LoogaGroundCoverState.y;
        }
        else
        {
            bareLocal = (half3)_LoogaGroundCoverBare.rgb;
        }
    }
    else if (!hasColor)
    {
        bareLocal = (half3)_LoogaGroundCoverBare.rgb;
    }
    // The coverage map holds the closure seen from above. At a low view angle the plants hide more ground. Sparse
    // plants grow in clumps that do not, so the term starts only over the dense coverage range.
    float elevation = max(toCamera.y, 0.001);
    float grazing = min(sqrt(saturate(1.0 - elevation * elevation)) / elevation, _LoogaGroundCoverView.w);
    float dense = smoothstep(_LoogaGroundCoverDense.x, _LoogaGroundCoverDense.y, coverage.r);
    half closure = (half)(1.0 - pow(saturate(1.0 - coverage.r), 1.0 + _LoogaGroundCoverView.z * grazing * dense));

    // The soil luminance relative to its mean modulates the canopy colour, so the soil pattern stays visible. The mean
    // of the ground layers keeps a bright or dark soil from brightening or darkening the whole canopy.
    half luminance = dot(albedo, LoogaLuminanceWeights);
    half soil = soilMean > 0.001h ? soilLuminance / soilMean : luminance / (half)_LoogaGroundCoverSoil.x;
    half detail = lerp(1.0h, clamp(soil, 0.2h, 3.0h), (half)_LoogaGroundCoverSoil.y);
    half3 cast = local * (luminance / max(dot(local, LoogaLuminanceWeights), 0.001h));
    half bare = coverage.g * (1.0h - closure) * (half)lerp(_LoogaGroundCoverView.x, _LoogaGroundCoverView.y, far)
        * layerStrength;
    albedo = lerp(albedo, cast, bare);
    // The plants shade the ground below them, and light that passes through the plants tints it.
    half3 underCanopy = lerp(albedo * (half)_LoogaGroundCoverShade.x,
        local * (half)_LoogaGroundCoverShade.z * detail, (half)_LoogaGroundCoverShade.y);
    // The far canopy: the canopy colour times the canopy albedo, and the sheen of the blades. The sun shade of the
    // plants comes from LoogaGroundCanopySunVisibility.
    half sheen = LoogaGroundCanopySheen(toCamera);
    half3 canopyColor = local * (half)_LoogaGroundCoverShade.w * detail + sheen;
    half blend = closure * (half)_LoogaGroundCover.x * layerStrength;
    half3 nearAlbedo = lerp(albedo, underCanopy, blend);
    // Between the far plants the ground shows as it shows between the near plants: shaded, and in their sun shade.
    half3 farAlbedo = lerp(nearAlbedo, canopyColor, blend);
    // The bare far cover adds only to the far canopy.
    half bareBlend = bareShare * (half)_LoogaGroundCover.x;
    half3 bareColor = bareLocal * (half)_LoogaGroundCoverShade.w * detail + sheen;
    farAlbedo = lerp(farAlbedo, bareColor, bareBlend);
    half farBlend = blend + bareBlend * (1.0h - blend);
    half farSoil = (1.0h - farBlend) * dot(nearAlbedo, LoogaLuminanceWeights) /
        max(dot(farAlbedo, LoogaLuminanceWeights), 0.001h);
    albedo = lerp(nearAlbedo, farAlbedo, (half)far);
    half cover = lerp(blend, farBlend, (half)far);
    surfaceGradient *= 1.0 - cover * (1.0 - _LoogaGroundCoverSoil.z);
    // Plants are rough. The ground under full cover must not reflect the sky at low view angles.
    smoothness = lerp(smoothness, 0.0h, cover);
    debug.groundCover = half3(cover, (half)far, bare);
    debug.farShade = half2(saturate(farSoil), 1.0h - saturate(farSoil));
}

// ApplyLoogaGroundCoverView without bare far cover.
void ApplyLoogaGroundCoverView(inout half3 albedo, inout float3 surfaceGradient, inout half smoothness,
    float3 positionWS, float3 toCamera, float cameraDistance, float2 positionDx, float2 positionDy, half layerStrength,
    half soilLuminance, half soilMean, inout LoogaWorldDebug debug)
{
    ApplyLoogaGroundCoverView(albedo, surfaceGradient, smoothness, positionWS, toCamera, cameraDistance, positionDx,
        positionDy, layerStrength, 0.0h, soilLuminance, soilMean, debug);
}

// ApplyLoogaGroundCoverView for the view of the current camera.
void ApplyLoogaGroundCover(inout half3 albedo, inout float3 surfaceGradient, inout half smoothness, float3 positionWS,
    float cameraDistance, float2 positionDx, float2 positionDy, half layerStrength, half bareCover,
    half soilLuminance, half soilMean, inout LoogaWorldDebug debug)
{
    ApplyLoogaGroundCoverView(albedo, surfaceGradient, smoothness, positionWS,
        normalize(GetCameraPositionWS() - positionWS), cameraDistance, positionDx, positionDy, layerStrength,
        bareCover, soilLuminance, soilMean, debug);
}

// ApplyLoogaGroundCover without bare far cover.
void ApplyLoogaGroundCover(inout half3 albedo, inout float3 surfaceGradient, inout half smoothness, float3 positionWS,
    float cameraDistance, float2 positionDx, float2 positionDy, half layerStrength, half soilLuminance, half soilMean,
    inout LoogaWorldDebug debug)
{
    ApplyLoogaGroundCover(albedo, surfaceGradient, smoothness, positionWS, cameraDistance, positionDx, positionDy,
        layerStrength, 0.0h, soilLuminance, soilMean, debug);
}

// Sky light occlusion of the far plant canopy, so the ground that shows the canopy colour lights like the distant
// plants. The sun shade of the far canopy comes from LoogaGroundCanopySunVisibility. The far canopy does not use the thin
// leaf lighting: the leaf contact shadows of the lighting pass shadow a distant heightfield falsely. blend and far are
// the ground cover blend and far share of ApplyLoogaGroundCover.
void ApplyLoogaFarCanopyLighting(half blend, half far, inout half occlusion)
{
    occlusion *= lerp(1.0h, (half)_LoogaGroundCoverDense.z, blend * far);
}

#endif
