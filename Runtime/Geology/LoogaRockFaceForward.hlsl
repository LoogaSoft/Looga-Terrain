#ifndef LOOGA_ROCK_FACE_FORWARD_INCLUDED
#define LOOGA_ROCK_FACE_FORWARD_INCLUDED

// Lit pass of the Looga rock face shaders. One rock texture, triplanar at two scales to hide tiling. The generated mesh
// vertex colors drive the variation: R = layer hardness, G = cavity, B = random value of the strata layer, A = ground
// weight. With _WorldStrata, the layer value and the hardness come from the world position (RockWorldStrata). Where the rock meets the ground, the shader reads the terrain itself: the splat control maps of the tile, the
// terrain layer arrays and the per-layer colour settings.
//
// A project shader can define LOOGA_ROCK_FACE_GROUND_HOOK(positionWS, albedo, normalTS, smoothness) before it includes
// this file. The hook then changes the terrain ground colour the same way as the project terrain shader does.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
// A project hook for the rock face ground is the hook of the shared terrain ground.
#if defined(LOOGA_ROCK_FACE_GROUND_HOOK) && !defined(LOOGA_TERRAIN_GROUND_HOOK)
    #define LOOGA_TERRAIN_GROUND_HOOK(positionWS, albedo, normalTS, smoothness) \
        LOOGA_ROCK_FACE_GROUND_HOOK(positionWS, albedo, normalTS, smoothness)
#endif
#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainGround.hlsl"
#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceCover.hlsl"

struct Attributes
{
    float4 positionOS : POSITION;
    // w is the show value: 1 = drawn, 0 = cut away (RockClipShow).
    float4 normalOS : NORMAL;
    half4 color : COLOR;
    float2 groundOffset : TEXCOORD1;
    // Surface masks of a LoogaRockGenerator mesh: x edge, y ridge, z crevice, w shelter. Read only where _RockMasks is 1:
    // other meshes have no channel 2, and a missing vertex input has no defined value.
    float4 masks : TEXCOORD2;
    // x is the cave shade of a LoogaCaveSystem mesh: 0 in daylight, up to 1 deep in the cave. Read only where
    // _LoogaCaveShade is 1: other meshes have no channel 3, and a missing vertex input has no defined value.
    float2 caveShade : TEXCOORD3;
#if defined(_ROCK_UV)
    // Texture coordinates of a rock asset (asset texture mode).
    float2 uv : TEXCOORD0;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    half3 normalWS : TEXCOORD1;
    half4 color : TEXCOORD2;
    half fogFactor : TEXCOORD3;
    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 4);
#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion : TEXCOORD5;
#endif
    // World XZ offset to the terrain point beside the rock whose layers the ground blend shows.
    float2 groundOffset : TEXCOORD6;
    half show : TEXCOORD7;
    // x is the cave shade. y is the distance to the nearest crack line.
    half2 caveShade : TEXCOORD8;
    half4 masks : TEXCOORD9;
#if defined(_ROCK_UV)
    float2 uv : TEXCOORD10;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

float RockHash31(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

// Slope of the crack walls in the crack line shading, in meters of depth for each meter across.
static const float CrackWallSlope = 1.5;

float RockValueNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(lerp(RockHash31(i), RockHash31(i + float3(1, 0, 0)), f.x),
                     lerp(RockHash31(i + float3(0, 1, 0)), RockHash31(i + float3(1, 1, 0)), f.x), f.y),
                lerp(lerp(RockHash31(i + float3(0, 0, 1)), RockHash31(i + float3(1, 0, 1)), f.x),
                     lerp(RockHash31(i + float3(0, 1, 1)), RockHash31(i + 1.0), f.x), f.y), f.z);
}

// Ridged multifractal of 3D value noise, 0 to 1 (Musgrave): folded octaves give sharp creases, and the finer octaves
// gather along the ridges. p is in fractal space (world position over the scale, height stretched, warped). Octaves
// smaller than about two pixels fade out (octaveFade: per octave, 1 = full), so they do not flicker.
float RockRidgedFractal(float3 p, float4 octaveFade)
{
    float sum = 0.0;
    float weight = 1.0;
    float amplitude = 1.0;
    [unroll]
    for (int octave = 0; octave < 4; octave++)
    {
        float signal = 1.0 - abs(2.0 * RockValueNoise(p + octave * 19.7) - 1.0);
        signal *= signal * weight;
        weight = saturate(signal * 2.0);
        sum += signal * amplitude * octaveFade[octave];
        amplitude *= 0.5;
        p *= 2.07;
    }
    // Total of the full amplitudes, 1 + 0.5 + 0.25 + 0.125.
    return sum / 1.875;
}

// Layer value and hardness of one strata band, with the ranges of RockFaceDetail: a hard band has 0.6 to 1, a soft band
// 0 to 0.25. A hard band comes and goes along the layer, so it does not form a continuous ring.
void RockStrataBand(float band, float2 positionXZ, out half layer, out half hardness)
{
    layer = saturate(RockHash31(float3(band, 83.0, 5.1)) +
                     0.6 * (RockValueNoise(float3(positionXZ / 30.0, band * 1.3 + 60.0)) - 0.5));
    half strength = RockHash31(float3(band, 37.0, 2.7));
    bool hard = RockHash31(float3(band, 31.0, 8.3)) < _StrataHardShare;
    half continuity = smoothstep(0.25, 0.75,
        RockValueNoise(float3(positionXZ / max(_StrataContinuity, 0.1), band * 3.7)));
    hardness = (hard ? lerp(0.6h, 1.0h, strength) : lerp(0.0h, 0.25h, strength)) * continuity;
}

// Strata at a world point. All rock meshes get the same layer value and hardness at the same point, so no color seam
// shows where two meshes meet. A band is 0.2 to 1.8 times _StrataThickness thick, and a world noise lifts and lowers the
// bands by up to _StrataWarp.
void RockWorldStrata(float3 positionWS, out half layer, out half hardness)
{
    float warp = (RockValueNoise(positionWS / 40.0 + 3.1) - 0.5) * 2.0 * _StrataWarp +
                 (RockValueNoise(positionWS / 12.0 + 11.7) - 0.5) * _StrataWarp * 0.5;
    float height = (positionWS.y + warp) / max(_StrataThickness, 0.05);
    // Band k starts at k + jitter(k). The jitter stays below 0.4, so the band starts stay in order.
    float cell = floor(height);
    float startHere = cell + (RockHash31(float3(cell, 41.0, 1.3)) - 0.5) * 0.8;
    float startNext = cell + 1.0 + (RockHash31(float3(cell + 1.0, 41.0, 1.3)) - 0.5) * 0.8;
    float band = height < startHere ? cell - 1.0 : (height >= startNext ? cell + 1.0 : cell);
    float bandStart = band + (RockHash31(float3(band, 41.0, 1.3)) - 0.5) * 0.8;
    float bandEnd = band + 1.0 + (RockHash31(float3(band + 1.0, 41.0, 1.3)) - 0.5) * 0.8;

    half bandLayer;
    half bandHardness;
    RockStrataBand(band, positionWS.xz, bandLayer, bandHardness);
    // A narrow blend into the nearer band keeps the band edge free of aliasing.
    float below = height - bandStart;
    float above = bandEnd - height;
    half neighborLayer;
    half neighborHardness;
    RockStrataBand(below < above ? band - 1.0 : band + 1.0, positionWS.xz, neighborLayer, neighborHardness);
    float edgeWidth = max(0.08 / max(_StrataThickness, 0.05), fwidth(height));
    half neighbor = 0.5h * (1.0h - saturate(min(below, above) / edgeWidth));
    layer = lerp(bandLayer, neighborLayer, neighbor);
    hardness = lerp(bandHardness, neighborHardness, neighbor);
}

float2 RockRotate(float2 value, float2 rotation)
{
    return float2(value.x * rotation.x - value.y * rotation.y, value.x * rotation.y + value.y * rotation.x);
}

half3 RockUnpackRotated(float2 uv, float2 rotation, half strength)
{
    half3 normal = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv), strength);
    // The texture was read in rotated UV space. Rotate its slope back to the projection axes.
    normal.xy = RockRotate(normal.xy, float2(rotation.x, -rotation.y));
    return normal;
}

void RockSampleTriplanar(float3 position, half3 normal, float scale, float2 rotation, float2 offset,
    out half3 albedo, out half3 normalWS, out half4 mask)
{
    half3 weights = pow(abs(normal), _TriplanarSharpness);
    weights /= max(weights.x + weights.y + weights.z, 1e-4);
    half3 axisSign = normal < 0 ? -1 : 1;

    float2 uvX = RockRotate(float2(position.z * axisSign.x, position.y) * scale, rotation) + offset;
    float2 uvY = RockRotate(float2(position.x * axisSign.y, position.z) * scale, rotation) + offset * 1.37;
    float2 uvZ = RockRotate(float2(-position.x * axisSign.z, position.y) * scale, rotation) - offset;

    albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvX).rgb * weights.x +
             SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvY).rgb * weights.y +
             SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvZ).rgb * weights.z;
    mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uvX) * weights.x +
           SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uvY) * weights.y +
           SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uvZ) * weights.z;

    half3 normalX = RockUnpackRotated(uvX, rotation, _NormalStrength);
    half3 normalY = RockUnpackRotated(uvY, rotation, _NormalStrength * _TopNormalStrength);
    half3 normalZ = RockUnpackRotated(uvZ, rotation, _NormalStrength);
    normalWS = LoogaBlendTriplanarNormals(normal, weights, axisSign, normalX, normalY, normalZ);
}

#if defined(_ROCK_UV)
// Linear value of mid grey (0.5 sRGB) is 1 / 4.5948. The tiled rock layer multiplies by this, so a mid grey asset texture
// keeps the color of the tiled rock (the convention of detail albedo maps, unity_ColorSpaceDouble in linear space).
static const half RockDetailNeutral = 4.59479h;

// Asset texture mode: the UV textures of the asset. The tangent frame comes from the screen derivatives of the position and
// the UV, so the asset mesh needs no tangents. The tiled rock layer uses the same frame: tiling scales the UV only.
void RockSampleAsset(float2 uv, half3 normal, float3 positionDx, float3 positionDy, out half3 albedo, out half3 normalWS,
    out half4 mask)
{
    albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb;
    mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uv);
    half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv), _NormalStrength);
    [branch]
    if (max(_RockDetailStrength, _RockDetailNormalStrength) > 0.001h)
    {
        float2 detailUV = uv * _RockDetailUV.xy + _RockDetailUV.zw;
        half area = SAMPLE_TEXTURE2D(_RockDetailAreaMap, sampler_RockDetailMap, uv).a;
        [branch]
        if (_RockDetailStrength > 0.001h)
        {
            half strength = _RockDetailStrength * area;
            half3 detailAlbedo = SAMPLE_TEXTURE2D(_RockDetailMap, sampler_RockDetailMap, detailUV).rgb;
            half4 detailMask = SAMPLE_TEXTURE2D(_RockDetailMaskMap, sampler_RockDetailMap, detailUV);
            albedo = lerp(albedo, albedo * detailAlbedo * RockDetailNeutral * _RockDetailColor.rgb, strength);
            mask.ga *= lerp(1.0h, detailMask.ga, strength);
        }
        half3 detailNormal = UnpackNormalScale(SAMPLE_TEXTURE2D(_RockDetailNormalMap, sampler_RockDetailMap, detailUV),
            _RockDetailNormalStrength * area);
        // Whiteout blend: the slopes of both layers add.
        normalTS = normalize(half3(normalTS.xy + detailNormal.xy, normalTS.z * detailNormal.z));
    }
    float2 uvDx = ddx(uv);
    float2 uvDy = ddy(uv);
    float3 perpendicularY = cross(positionDy, normal);
    float3 perpendicularX = cross(normal, positionDx);
    float3 tangent = perpendicularY * uvDx.x + perpendicularX * uvDy.x;
    float3 bitangent = perpendicularY * uvDx.y + perpendicularX * uvDy.y;
    float scale = rsqrt(max(max(dot(tangent, tangent), dot(bitangent, bitangent)), 1e-12));
    normalWS = normalize(tangent * scale * normalTS.x + bitangent * scale * normalTS.y + normal * normalTS.z);
}
#endif

#if defined(_ROCK_UV)
// Weight of the ground fringe: soil and moss creep over the rock around a ground top. A coarse mip of mask red spreads
// the painted ground past its edge. Ground on up faces spreads 0.25 further down the normal Y. A world noise breaks the
// band, so it does not follow the edge as an outline.
half RockGroundFringe(float2 uv, float3 positionWS, half3 normal)
{
    half spread = SAMPLE_TEXTURE2D_LOD(_MaskMap, sampler_MaskMap, uv, _GroundFringeMip).r;
    half fringe = saturate(spread * 3.0h);
    [branch]
    if (_GroundUpStart > 0.001h)
    {
        fringe = max(fringe, saturate((normal.y - (_GroundUpStart - 0.4h)) / 0.25h));
    }
    half noise = (half)(RockValueNoise(positionWS * 1.3 + 61.7) * 0.6 + RockValueNoise(positionWS * 4.1 + 7.9) * 0.4);
    return saturate(fringe * (0.4h + 1.2h * noise)) * _GroundFringe;
}
#endif

// Mineral stains: iron and other minerals wash out of the joints and color the face in soft patches that run down the
// rock. A warped world noise mixes four colors. The noise is stretched along Y, so the patches form vertical streaks.
// The stains keep the texture detail: they tint the luminance of the rock. They show mostly on steep faces. Stain On
// Flat Faces also tints ledge tops, which weather like the face and were pale without it.
void RockMineralStains(float3 positionWS, half3 geometryNormal, inout half3 albedo)
{
    float3 p = float3(positionWS.x, positionWS.y / max(_StainStretch, 1.0), positionWS.z) / max(_StainScale, 0.1);
    float3 warp = float3(RockValueNoise(p * 0.7 + 5.3), RockValueNoise(p * 0.7 + 17.1),
                         RockValueNoise(p * 0.7 + 29.7)) - 0.5;
    p += warp * 1.6;
    float mixA = RockValueNoise(p) * 0.65 + RockValueNoise(p * 2.3 + 7.7) * 0.35;
    float mixB = RockValueNoise(p * 1.4 + 51.3) * 0.65 + RockValueNoise(p * 3.1 + 13.9) * 0.35;
    half width = 0.5h / max(_StainContrast, 0.01h);
    half warm = smoothstep(0.5h - width, 0.5h + width, (half)mixA);
    half shade = smoothstep(0.5h - width, 0.5h + width, (half)mixB);
    half3 warmColor = lerp(_StainColorB.rgb, _StainColorA.rgb, shade);
    half3 coolColor = lerp(_StainColorD.rgb, _StainColorC.rgb, shade);
    half3 stain = lerp(coolColor, warmColor, warm);
    half luminance = dot(albedo, half3(0.299h, 0.587h, 0.114h));
    half steep = saturate(1.2h - geometryNormal.y * 1.5h);
    albedo = lerp(albedo, luminance * stain * 2.2h, _StainStrength * lerp(steep, 1.0h, _StainFlat));
    // Thin dark wash streaks where water runs down the face.
    float streak = RockValueNoise(float3(positionWS.x * 0.9, positionWS.y * 0.03, positionWS.z * 0.9) + 71.3);
    albedo *= 1.0h - smoothstep(0.55h, 0.85h, (half)streak) * _StreakStrength * 0.35h * steep;
}

// Triplanar sample of the moss texture set around the geometry normal.
void RockSampleMoss(float3 position, half3 normal, out half4 albedo, out half3 normalWS, out half4 mask)
{
    half3 weights = pow(abs(normal), 4.0);
    weights /= max(weights.x + weights.y + weights.z, 1e-4);
    half3 axisSign = normal < 0 ? -1 : 1;
    float scale = 1.0 / max(_MossTiling, 0.05);
    float2 uvX = float2(position.z * axisSign.x, position.y) * scale + 0.31;
    float2 uvY = float2(position.x * axisSign.y, position.z) * scale;
    float2 uvZ = float2(-position.x * axisSign.z, position.y) * scale + 0.57;
    albedo = SAMPLE_TEXTURE2D(_MossMap, sampler_MossMap, uvX) * weights.x +
             SAMPLE_TEXTURE2D(_MossMap, sampler_MossMap, uvY) * weights.y +
             SAMPLE_TEXTURE2D(_MossMap, sampler_MossMap, uvZ) * weights.z;
    mask = SAMPLE_TEXTURE2D(_MossMaskMap, sampler_MossMap, uvX) * weights.x +
           SAMPLE_TEXTURE2D(_MossMaskMap, sampler_MossMap, uvY) * weights.y +
           SAMPLE_TEXTURE2D(_MossMaskMap, sampler_MossMap, uvZ) * weights.z;
    half3 normalX = UnpackNormalScale(SAMPLE_TEXTURE2D(_MossNormalMap, sampler_MossMap, uvX), _MossNormalStrength);
    half3 normalY = UnpackNormalScale(SAMPLE_TEXTURE2D(_MossNormalMap, sampler_MossMap, uvY), _MossNormalStrength);
    half3 normalZ = UnpackNormalScale(SAMPLE_TEXTURE2D(_MossNormalMap, sampler_MossMap, uvZ), _MossNormalStrength);
    normalWS = LoogaBlendTriplanarNormals(normal, weights, axisSign, normalX, normalY, normalZ);
}

// Moss grows on faces that look up, in damp cavities and in drips below ledges. A world noise breaks it into patches.
// Faces toward the sun dry out and keep less. With the texture set, the moss fills the low parts of the rock texture
// first (height blend), so its edge follows the cracks and the rock bumps come through. Its own normal, occlusion and a
// dark rim where the cushion meets the rock give it a body, and a sheen at grazing angles gives the soft look.
void RockMoss(float3 positionWS, half3 geometryNormal, half cavity, half rockHeight, inout half3 albedo,
    inout half3 normalWS, inout half smoothness, inout half occlusion)
{
    float patch = RockValueNoise(positionWS / max(_MossScale, 0.1) + 23.1) * 0.6 +
                  RockValueNoise(positionWS / max(_MossScale * 0.3, 0.05) + 3.3) * 0.4;
    half up = saturate((geometryNormal.y - _MossStart) / 0.3h);
    float drip = RockValueNoise(float3(positionWS.x * 1.3, positionWS.y * 0.08, positionWS.z * 1.3) + 9.7);
    half coverage = saturate(up + cavity * _MossCavity + smoothstep(0.6h, 0.9h, (half)drip) * _MossDrips);
    half sun = saturate(dot(geometryNormal, (half3)_MainLightPosition.xyz));
    coverage *= 1.0h - _MossSunAvoid * sun;
    half amount = saturate(coverage * smoothstep(0.4h, 0.75h, (half)patch) * _MossAmount * 2.0h);
    [branch]
    if (_MossTextured < 0.5h)
    {
        half3 mossColor = lerp(_MossColorA.rgb, _MossColorB.rgb, (half)RockValueNoise(positionWS * 0.8 + 61.7));
        albedo = lerp(albedo, mossColor, amount);
        normalWS = normalize(lerp(normalWS, geometryNormal, amount * 0.5h));
        smoothness *= 1.0h - amount * 0.6h;
        return;
    }
    [branch]
    if (amount < 0.002h) return;

    half4 mossAlbedo;
    half3 mossNormal;
    half4 mossMask;
    RockSampleMoss(positionWS, geometryNormal, mossAlbedo, mossNormal, mossMask);
    // Small clumps break the edge between the texture heights.
    float clump = RockValueNoise(positionWS * 3.1 + 41.3);
    half value = amount + _MossHeightBlend * ((half)mossMask.b - rockHeight) + ((half)clump - 0.5h) * 0.25h;
    // The patch edge fades over a wide range of the amount, so thin moss gathers in the moss height lows first.
    half moss = saturate((value - 0.5h) * _MossEdge + 0.5h) * smoothstep(0.0h, 0.6h, amount);
    [branch]
    if (moss < 0.002h) return;

    // Large patches of the same moss differ a little in color.
    half variation = (half)RockValueNoise(positionWS * 0.17 + 61.7);
    half3 color = mossAlbedo.rgb * _MossTint.rgb * lerp(0.8h, 1.15h, variation);
    // The moss lies over the rock: its own normal, with a little of the rock shape under it.
    half3 mossSurface = normalize(lerp(mossNormal, normalWS, 0.25h));
    half3 view = GetWorldSpaceNormalizeViewDir(positionWS);
    half grazing = 1.0h - saturate(dot(mossSurface, view));
    color *= 1.0h + _MossSheen * grazing * grazing * grazing * 1.5h;
    albedo = lerp(albedo, color, moss);
    normalWS = normalize(lerp(normalWS, mossSurface, moss));
    smoothness = lerp(smoothness, min((half)mossMask.a, 0.35h) * 0.6h, moss);
    // Occlusion of the moss texture, and a dark rim where the cushion meets the rock.
    half rim = saturate(moss * (1.0h - moss) * 4.0h);
    occlusion *= lerp(1.0h, (half)mossMask.g, moss) * (1.0h - _MossCushion * rim * 0.6h);
}

// Large soft colors that do not repeat with the surface tile. The map lies in world space with V along height, so streaks
// run down the rock on every side and each copy of a rock gets its own stains. Rust gathers near cracks.
half3 RockStain(float3 positionWS, half3 normalWS, half nearCrack, half3 albedo)
{
    float2 scale = 1.0 / max(_StainSize, 0.1);
    half3 weights = abs(normalWS);
    weights *= weights;
    weights *= weights;
    weights /= weights.x + weights.y + weights.z;
    half4 stain = SAMPLE_TEXTURE2D(_StainMap, sampler_BaseMap, positionWS.zy * scale) * weights.x +
                  SAMPLE_TEXTURE2D(_StainMap, sampler_BaseMap, positionWS.xz * scale) * weights.y +
                  SAMPLE_TEXTURE2D(_StainMap, sampler_BaseMap, positionWS.xy * scale) * weights.z;
    albedo = lerp(albedo, albedo * _StainRust.rgb, stain.r * (0.45h + 0.55h * nearCrack) * _StainAmount);
    albedo = lerp(albedo, albedo * _StainOlive.rgb, stain.g * _StainAmount);
    albedo = lerp(albedo, albedo * _StainStreak.rgb, stain.b * _StainAmount);
    // The mottle moves the brightness and shifts the hue between warm and cool, as mineral clusters do.
    half mottle = clamp((stain.a - 0.5h) * 4.0h, -1.0h, 1.0h) * _StainAmount;
    return albedo * (1.0h + mottle * _StainMottle * 0.25h) * half3(1.0h + mottle * _StainHue, 1.0h, 1.0h - mottle * _StainHue);
}

// One sample of a map in world space, with V along height.
half4 RockWorldSample(TEXTURE2D_PARAM(map, mapSampler), float3 position, half3 normalWS, float size)
{
    half3 weights = pow(abs(normalWS), _TriplanarSharpness);
    weights /= max(weights.x + weights.y + weights.z, 1e-4);
    float scale = 1.0 / max(size, 0.01);
    return SAMPLE_TEXTURE2D(map, mapSampler, position.zy * scale) * weights.x +
           SAMPLE_TEXTURE2D(map, mapSampler, position.xz * scale) * weights.y +
           SAMPLE_TEXTURE2D(map, mapSampler, position.xy * scale) * weights.z;
}

// Steps along the cracks of the line map. The plates between the cracks are flat, so the normal changes only at a crack, and
// one side of the crack catches the light. U and V of each projection are world axes, as in RockWorldSample.
half3 RockLineSteps(float3 positionWS, half3 geometryNormal, half3 normalWS)
{
    half3 weights = pow(abs(geometryNormal), _TriplanarSharpness);
    weights /= max(weights.x + weights.y + weights.z, 1e-4);
    float scale = 1.0 / max(_LineSize, 0.01);
    half2 stepX = UnpackNormalScale(SAMPLE_TEXTURE2D(_LineStepMap, sampler_NormalMap, positionWS.zy * scale), _LineStep).xy;
    half2 stepY = UnpackNormalScale(SAMPLE_TEXTURE2D(_LineStepMap, sampler_NormalMap, positionWS.xz * scale), _LineStep).xy;
    half2 stepZ = UnpackNormalScale(SAMPLE_TEXTURE2D(_LineStepMap, sampler_NormalMap, positionWS.xy * scale), _LineStep).xy;
    half3 slope = half3(0.0h, stepX.y, stepX.x) * weights.x + half3(stepY.x, 0.0h, stepY.y) * weights.y +
                  half3(stepZ.x, stepZ.y, 0.0h) * weights.z;
    return normalize(normalWS + slope);
}

// Thin lines that cross the faces: pale veins, and dark cracks with a stained halo. The map lies in world space, so the
// lines do not repeat with the surface tile. The mineral grains break the border of the veins.
void RockLines(float3 positionWS, half3 geometryNormal, half textureGrain, inout half3 albedo, inout half3 normalWS,
    inout half smoothness, inout half occlusion)
{
    half3 lines = RockWorldSample(TEXTURE2D_ARGS(_LineMap, sampler_BaseMap), positionWS, geometryNormal, _LineSize).rgb;
    albedo = lerp(albedo, albedo * _LineHaloColor.rgb, lines.b * _LineHalo);
    albedo = lerp(albedo, _LineVeinColor.rgb, saturate(lines.r * (0.55h + 0.9h * textureGrain)) * _LineVein);
    half crack = lines.g * _LineCrack;
    albedo *= 1.0h - crack;
    occlusion *= 1.0h - crack * 0.5h;
    // The inside of a crack is rough and has no sheen.
    smoothness *= 1.0h - crack;
    [branch]
    if (_LineStep > 0.001h)
    {
        normalWS = RockLineSteps(positionWS, geometryNormal, normalWS);
    }
}

// A value from 0 to 1 for each plane of the rock. Planes that differ by a few degrees get different values, so each cleaved
// face reads as one tone. The value comes from the normal, so it needs no block data and has no seams. A slow world noise
// gives two planes with the same normal different values.
half RockPlaneTone(float3 positionWS, half3 normalWS)
{
    float phase = dot(normalWS, float3(1.7, 2.3, -1.3)) * _PlaneFrequency + 6.28318 * RockValueNoise(positionWS / 5.0 + 13.7);
    return (half)(0.5 + 0.5 * sin(phase));
}

// Grains smaller than a pixel average out, so a rock turns flat at a distance. This layer reads the same mineral grains at
// a larger size as the pixels grow on the surface. It is color only: grain in the normals reads as sandpaper. Flecks sets the
// share of pale grains on a dark ground. Low values give a grain that moves both lighter and darker, with no salt on the rock.
// Zones change the strength from plane to plane and never remove the grain, so the mean brightness stays the same.
void RockFarGrain(float3 positionWS, half3 normalWS, float footprint, inout half3 albedo)
{
    half far = (half)saturate((footprint - _FarGrainStart) / max(_FarGrainEnd - _FarGrainStart, 1e-5));
    [branch]
    if (far > 0.001h)
    {
        // Cleavage stretches mineral grain along one direction.
        float3 grainPosition = positionWS * float3(1.0, 1.0 / max(_FarGrainStretch, 1.0), 1.0);
        half grain = RockWorldSample(TEXTURE2D_ARGS(_MaskMap, sampler_MaskMap), grainPosition, normalWS, _FarGrainSize).r;
        half cluster = (half)(RockValueNoise(positionWS / 1.3 + 23.7) * 0.6 + RockValueNoise(positionWS / 0.47 + 5.1) * 0.4);
        half pale = smoothstep(0.62h, 0.74h, grain + (cluster - 0.5h) * 0.2h);
        half both = (grain - 0.5h) * lerp(1.5h, 0.5h, _FarGrainFlecks);
        half3 grained = albedo * (1.0h + (both + (pale - 0.3h) * _FarGrainFlecks) * _FarGrain);
        // A pale grain is its own mineral, so it does not take the color of the ground.
        grained = lerp(grained, dot(grained, half3(0.3h, 0.6h, 0.1h)).xxx, pale * 0.6h * _FarGrainFlecks);
        half zone = lerp(0.6h, 1.25h, RockPlaneTone(positionWS, normalWS));
        albedo = lerp(albedo, grained, far * lerp(1.0h, zone, _FarGrainClump));
    }
}

// Detail of one rock that is thinner than its mesh: the small tiers and the cracks of each block. The side with the largest
// normal axis holds the sample, as in FaceDisplacePass. Ledges that look up or down have no detail.
void RockFaceDetail(float3 positionWS, float3 geometryNormal, inout half3 albedo, inout float3 normalWS, inout half smoothness,
    inout half occlusion)
{
    float3 reach = abs(geometryNormal);
    float weight = saturate((max(reach.x, reach.z) - 0.4) / 0.2);
    if (weight <= 0.0) return;

    float3 share = (positionWS - _FaceDetailCenter.xyz + _FaceDetailHalf.xyz) / (2.0 * _FaceDetailHalf.xyz);
    bool onZ = reach.z >= reach.x;
    float side = onZ ? (geometryNormal.z > 0.0 ? 0.0 : 1.0) : (geometryNormal.x < 0.0 ? 2.0 : 3.0);
    half4 detail = SAMPLE_TEXTURE2D_ARRAY(_FaceDetail, sampler_FaceDetail, onZ ? share.xy : share.zy, side);
    float2 slope = (detail.rg - 128.0 / 255.0) * 2.0 * _FaceDetailSlope * weight;
    float3 across = onZ ? float3(1.0, 0.0, 0.0) : float3(0.0, 0.0, 1.0);
    normalWS = normalize(normalWS - slope.x * across - slope.y * float3(0.0, 1.0, 0.0));
    half crack = detail.b * _FaceDetailCrack * (half)weight;
    albedo *= 1.0h - crack;
    smoothness *= 1.0h - crack;
    occlusion *= 1.0h - crack * 0.5h;
    albedo = lerp(albedo, _FaceDetailVeinColor.rgb, detail.a * _FaceDetailVein * (half)weight);
}

// Surface detail that follows the fractures of the rock. The world layers do not know where the joints are, and the eye reads that
// as a pattern laid over the rock. The lines come from the signed distance, so they are thinner than the volume grid.
void RockFractureSurface(float3 positionWS, float footprint, half textureGrain, inout half3 albedo, inout half smoothness,
    inout half occlusion)
{
    float3 position = TransformWorldToObject(positionWS);
    half4 volume = SAMPLE_TEXTURE3D_LOD(_FractureVolume, sampler_FractureVolume,
        (position - _FractureVolumeMin.xyz) * _FractureVolumeScale.xyz, 0);
    float toPlane = (volume.r - 0.5) * 2.0 * _FractureVolumeRange.x;
    float nearest = volume.g * _FractureVolumeRange.y;

    albedo *= 1.0h + (volume.b - 0.5h) * 2.0h * _BlockTone;

    // Water runs down from each ledge. The stains are thin and stop at random lengths.
    float run = volume.a * _FractureVolumeRange.w;
    half fade = (half)saturate(1.0 - run / max(_LedgeStreakLength, 0.01));
    [branch]
    if (_LedgeStreak > 0.001h && fade > 0.001h)
    {
        half stripes = (half)(RockValueNoise(float3(position.x * 1.7, position.y * 0.15, position.z * 1.7)) * 0.45 +
                              RockValueNoise(float3(position.x * 5.3, position.y * 0.3, position.z * 5.3) + 7.1) * 0.35 +
                              RockValueNoise(float3(position.x * 17.0, position.y * 0.9, position.z * 17.0) + 3.7) * 0.2);
        half streak = smoothstep(0.5h, 0.72h, stripes) * fade * fade;
        albedo *= 1.0h - streak * _LedgeStreak;
        smoothness *= 1.0h - streak * 0.5h;
    }

    // Rust leaves the cracks in patches.
    half rustPatch = (half)(RockValueNoise(position / 0.9 + 3.7) * 0.7 + RockValueNoise(position / 0.25 + 11.3) * 0.3);
    half rust = (half)exp(-nearest / 0.16) * smoothstep(0.4h, 0.65h, rustPatch);
    albedo = lerp(albedo, albedo * _FractureRustColor.rgb, rust * _FractureRust);

    // A plane that meets the surface at a low angle has a small slope along the surface. The distance over the slope is the
    // distance along the surface, so the line keeps its width. A plane nearly along the surface gets no line.
    float slope = length(float2(ddx(toPlane), ddy(toPlane))) / max(footprint, 1e-5);
    half steep = (half)saturate((slope - 0.15) / 0.2);
    float wander = ((RockValueNoise(position / 0.6 + 41.9) - 0.5) + (RockValueNoise(position / 0.13 + 17.1) - 0.5) * 0.4) * _FractureWander;
    float across = (toPlane + wander) / max(slope, 0.25);
    float width = lerp(0.004, 0.02, RockValueNoise(position / 0.45 + 29.3));
    float pixel = max(footprint, 1e-4);
    // A line thinner than a pixel covers only a share of it, so it fades and does not flicker.
    half body = (half)(saturate(1.0 - (abs(across) - width) / pixel) * saturate(2.0 * width / pixel));
    // Halfway between two fractures, the signed distance also crosses 0. The plain distance is large there.
    half onFracture = steep * (half)saturate((0.07 - nearest) / 0.02);
    // Fine lines gather in zones of broken rock. The faces between the zones stay clean.
    half zone = smoothstep(0.42h, 0.6h, (half)(RockValueNoise(position / 1.9 + 91.3) * 0.7 + RockValueNoise(position / 0.6 + 33.1) * 0.3));
    onFracture *= lerp(1.0h, zone, _FractureCluster);
    body *= onFracture;
    // A line changes between pale mineral and open crack along its length.
    half kind = (half)RockValueNoise(position / 1.6 + 5.3);
    // A vein comes and goes along its length, so it does not read as a drawn stroke.
    half breakup = smoothstep(0.38h, 0.62h, (half)RockValueNoise(position / 0.7 + 67.9));
    half veinLevel = 1.0h - _FractureVeinShare;
    half vein = body * smoothstep(veinLevel - 0.04h, veinLevel + 0.04h, kind) * breakup;
    albedo = lerp(albedo, _FractureVeinColor.rgb, saturate(vein * (0.55h + 0.9h * textureGrain)) * _FractureVein);
    // A thin dark edge on one side of each line, as a fine step between two plates.
    half side = (half)saturate(1.0 - abs(across + 1.6 * width) / max(0.6 * width, pixel)) * onFracture;
    half crack = saturate(body - vein + side * 0.6h) * _FractureCrack;
    albedo *= 1.0h - crack;
    smoothness *= 1.0h - crack;
    occlusion *= 1.0h - crack * 0.5h;
}

// One more triplanar normal layer. The layer moves the normal by its own slope, so layers add up. The return value is the
// size of that slope: a steep part of a fracture map is a crack.
half RockDetailLayer(TEXTURE2D_PARAM(map, mapSampler), float3 position, half3 geometryNormal, float size, half strength,
    inout half3 normalWS)
{
    half3 weights = pow(abs(geometryNormal), _TriplanarSharpness);
    weights /= max(weights.x + weights.y + weights.z, 1e-4);
    half3 axisSign = geometryNormal < 0 ? -1 : 1;
    float scale = 1.0 / max(size, 0.01);
    half3 normalX = UnpackNormalScale(SAMPLE_TEXTURE2D(map, mapSampler,
        float2(position.z * axisSign.x, position.y) * scale), strength);
    half3 normalY = UnpackNormalScale(SAMPLE_TEXTURE2D(map, mapSampler,
        float2(position.x * axisSign.y, position.z) * scale), strength);
    half3 normalZ = UnpackNormalScale(SAMPLE_TEXTURE2D(map, mapSampler,
        float2(-position.x * axisSign.z, position.y) * scale), strength);
    half3 slope = LoogaBlendTriplanarNormals(geometryNormal, weights, axisSign, normalX, normalY, normalZ) - geometryNormal;
    normalWS = normalize(normalWS + slope);
    return length(slope);
}

// A small round on convex edges, with no extra geometry. The edge mask acts as a height that falls toward the edge, so the
// normal leans over the edge and the corner catches the light. The slope comes from screen derivatives (surface gradient,
// Mikkelsen 2010). A limit stops a spike on a small triangle.
half3 RockEdgeRound(float3 positionDx, float3 positionDy, half3 geometryNormal, half edge, half3 normalWS)
{
    float edgeDx = ddx((float)edge);
    float edgeDy = ddy((float)edge);
    float3 normal = geometryNormal;
    float3 acrossX = cross(positionDy, normal);
    float3 acrossY = cross(normal, positionDx);
    float area = dot(positionDx, acrossX);
    float3 gradient = (edgeDx * acrossX + edgeDy * acrossY) * sign(area) / max(abs(area), 1e-7);
    gradient *= _EdgeRound;
    float slope = length(gradient);
    if (slope > 1.0) gradient /= slope;
    return (half3)normalize((float3)normalWS + gradient);
}

// Weathering from the surface masks of a generated rock. Edges wear to a paler color. The texture height and a world
// noise set the limit of the wear, so its border is sharp and irregular at the pixel scale although the masks are
// vertex values. Crevices and hollows darken, and the rock under a ledge gets less light. A deposit fills the low parts
// of the texture in sheltered places.
void RockGeneratedWeathering(float3 positionWS, half3 normalWS, half4 masks, half cavity, half textureHeight,
    half textureGrain, inout half3 albedo, inout half smoothness, inout half occlusion)
{
    half edge = masks.x;
    half ridge = masks.y * 2.0h - 1.0h;
    half crevice = masks.z;
    half shelter = masks.w;
    [branch]
    if (_StainAmount > 0.001h)
    {
        albedo = RockStain(positionWS, normalWS, saturate(crevice * 2.0h + cavity), albedo);
    }
    float size = max(_EdgeBreakupSize, 0.01);
    half noise = (half)(RockValueNoise(positionWS / size + 31.7) * 0.55 +
                        RockValueNoise(positionWS / size * 2.7 + 7.3) * 0.3 +
                        RockValueNoise(positionWS / size * 7.3 + 19.1) * 0.15);

    half value = edge * 2.0h * _EdgeWear + _EdgeBreakup * saturate(edge * 4.0h) *
                 ((textureHeight - 0.5h) + (noise - 0.5h) * 1.5h);
    half wear = smoothstep(0.5h - _EdgeSoftness, 0.5h + _EdgeSoftness, value);
    // Stone chips at random. High noise and the mineral grains break the wear into small pieces, so it does not follow the
    // curvature mask exactly.
    [branch]
    if (_EdgeChip > 0.001h)
    {
        float chipSize = max(_EdgeChipSize, 0.01);
        half chips = (half)(RockValueNoise(positionWS / chipSize + 83.1) * 0.5 +
                            RockValueNoise(positionWS / chipSize * 2.3 + 41.3) * 0.3 +
                            RockValueNoise(positionWS / chipSize * 5.1 + 7.7) * 0.2);
        chips = saturate((chips + (textureGrain - 0.5h) * 0.35h - 0.5h) * 7.0h + 0.5h);
        // Wear catches on the raised points of the surface, and the small pits next to them stay dark.
        half peak = saturate((textureHeight - 0.45h) * 3.5h);
        wear *= lerp(1.0h, chips * lerp(0.25h, 1.0h, peak * peak), _EdgeChip);
    }
    albedo *= 1.0h + ridge * _RidgeLight;
    albedo = lerp(albedo, albedo * _EdgeColor.rgb, wear);
    smoothness *= 1.0h - 0.4h * wear;

    // Worn rock shows its pale minerals: where the wear and the broad curvature rise, more of the grains turn pale, and the
    // rock between them keeps its color. The grains average out in the far mips, so a threshold finds none at a distance.
    // There the pixel takes the share of its area that the flecks cover, and the wear keeps its brightness.
    [branch]
    if (_EdgeFlecks > 0.001h)
    {
        half exposed = saturate(wear + saturate(ridge) * 0.6h);
        half level = 0.92h - exposed * _EdgeFlecks * 0.4h;
        half fleck = smoothstep(level - 0.04h, level + 0.04h, textureGrain);
        float footprint = max(length(ddx(positionWS)), length(ddy(positionWS)));
        half far = (half)saturate((footprint - 0.003) / 0.008);
        fleck = lerp(fleck, saturate((0.95h - level) * 0.8h), far);
        albedo = lerp(albedo, _EdgeFleckColor.rgb, fleck * saturate(exposed * 3.0h));
    }

    // The low parts of the texture darken first, so the dark area follows the small cracks.
    half dark = saturate(crevice * 1.6h + cavity * 0.6h - ridge * 0.3h) * saturate(1.4h - textureHeight);
    albedo = lerp(albedo, albedo * _CreviceColor.rgb, dark * _CreviceDarken);
    occlusion *= 1.0h - dark * _CreviceDarken * 0.6h;
    smoothness *= 1.0h - dark * _CreviceRough;
    // The curve squares the light share, so the rock under a ledge falls close to black and open faces stay bright.
    half open = 1.0h - shelter * _ShelterDarken * 0.6h;
    albedo *= lerp(open, open * open, _ShelterCurve);
    occlusion *= 1.0h - shelter * _ShelterDarken * 0.5h;

    [branch]
    if (_DepositAmount > 0.001h)
    {
        float patchSize = max(_DepositSize, 0.01);
        half patches = (half)(RockValueNoise(positionWS / patchSize + 57.3) * 0.65 +
                              RockValueNoise(positionWS / patchSize * 3.1 + 11.9) * 0.35);
        half place = saturate(cavity * 0.8h + crevice * 0.8h + shelter * 0.4h - ridge * 0.2h);
        half film = saturate((place + patches - 1.0h + _DepositAmount * 1.2h - textureHeight * 0.5h) * 5.0h);
        half luminance = dot(albedo, half3(0.299h, 0.587h, 0.114h));
        albedo = lerp(albedo, luminance * _DepositColor.rgb * 2.2h, film * _DepositAmount);
    }
}

Varyings RockVertex(Attributes input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
    output.positionCS = position.positionCS;
    output.positionWS = position.positionWS;
    output.normalWS = TransformObjectToWorldNormal(input.normalOS.xyz);
    output.show = input.normalOS.w;
    output.color = input.color;
    output.groundOffset = input.groundOffset;
#if defined(_ROCK_UV)
    // An asset has no generated rock colors. The ground shows the terrain point beside the asset. The offset to one
    // point changes linearly over the mesh, so the interpolation between the vertices keeps it exact.
    output.uv = input.uv;
    output.color = half4(0.5h, 0.5h, 0.5h, 0.0h);
    output.groundOffset = _RockGroundCenter.w > 0.0 ? _RockGroundCenter.xz - position.positionWS.xz : float2(0, 0);
#endif
    output.masks = _RockMasks > 0.5h ? (half4)input.masks : half4(0.0h, 0.5h, 0.0h, 0.0h);
    output.caveShade = half2(_LoogaCaveShade > 0.5h ? saturate(input.caveShade.x) : 0.0h,
        _CrackLineWidth > 0.0h ? input.caveShade.y : 10.0h);
    output.fogFactor = ComputeFogFactor(position.positionCS.z);
    OUTPUT_SH4(position.positionWS, output.normalWS.xyz, GetWorldSpaceNormalizeViewDir(position.positionWS),
        output.vertexSH, output.probeOcclusion);
    return output;
}

// Rock surface at one pixel. RockFragment lights this surface. A project GBuffer pass can write it to the GBuffer.
void RockSurface(Varyings input, out half3 albedo, out half3 normalWS, out half smoothness, out half occlusion)
{
    float3 positionWS = input.positionWS;
    RockClipShow(positionWS, input.show);
    float3 positionDx = ddx(positionWS);
    float3 positionDy = ddy(positionWS);
    half3 geometryNormal = normalize(input.normalWS);
    // Facet shading: the normal of the triangle replaces part of the smooth vertex normal near the camera, so flat
    // fracture faces read as planes with crisp edges. It fades out with distance, where the triangles are small.
    [branch]
    if (_FacetShading > 0.001h)
    {
        float3 faceNormal = normalize(cross(positionDy, positionDx));
        faceNormal = dot(faceNormal, geometryNormal) < 0.0 ? -faceNormal : faceNormal;
        float cameraDistance = length(positionWS - GetCameraPositionWS());
        half facet = _FacetShading * (1.0h - (half)smoothstep(0.5 * _FacetFade, _FacetFade, cameraDistance));
        geometryNormal = normalize(lerp(geometryNormal, (half3)faceNormal, facet));
    }
    half hardness = input.color.r;
    half cavity = input.color.g;
    half layerValue = input.color.b;
    half groundWeight = input.color.a;
    [branch]
    if (_WorldStrata > 0.5h)
    {
        RockWorldStrata(positionWS, layerValue, hardness);
        // RockFaceDetail adds 0.3 x (1 - hardness) to the vertex cavity: soft layers weather back. Replace that part
        // with the world hardness, so meshes without it, such as the cave, get the same value.
        cavity = saturate(saturate(cavity - 0.3h * (1.0h - input.color.r)) + 0.3h * (1.0h - hardness));
    }

    // Two scales of the same texture. A height-aware noise blend hides the repeat of each scale.
    float baseScale = 1.0 / max(_Tiling, 0.01);
    half3 albedoNear;
    half3 normalNear;
    half4 maskNear;
#if defined(_ROCK_UV)
    // Red of the asset mask marks the ground. The blue channel holds the texture height as in the rock mask.
    RockSampleAsset(input.uv, geometryNormal, positionDx, positionDy, albedoNear, normalNear, maskNear);
    groundWeight = maskNear.r;
    [branch]
    if (_GroundUpStart > 0.001h)
    {
        groundWeight = max(groundWeight, saturate((geometryNormal.y - (_GroundUpStart - 0.15h)) / 0.15h));
    }
    half3 albedoFar = albedoNear;
    half3 normalFar = normalNear;
    half4 maskFar = maskNear;
    half farWeight = 0.0h;
#else
    RockSampleTriplanar(positionWS, geometryNormal, baseScale, float2(1, 0), float2(0, 0),
        albedoNear, normalNear, maskNear);
    half3 albedoFar;
    half3 normalFar;
    half4 maskFar;
    RockSampleTriplanar(positionWS, geometryNormal, baseScale / _MacroScale, float2(0.799, 0.602),
        float2(0.37, 0.71), albedoFar, normalFar, maskFar);
    float blendNoise = RockValueNoise(positionWS / max(_MacroBlendScale, 0.1)) * 0.65 +
                       RockValueNoise(positionWS / max(_MacroBlendScale * 0.37, 0.1) + 17.3) * 0.35;
    half farWeight = saturate((blendNoise - 0.5 + (maskFar.b - maskNear.b) * _HeightBlend) * 3.0 + 0.5) * _MacroAmount;
#endif
    albedo = lerp(albedoNear, albedoFar, farWeight);
    normalWS = normalize(lerp(normalNear, normalFar, farWeight));
    // Fractal detail below the mesh grid: the ridged fractal is a height on the surface. Its slope comes from two more
    // samples a short world distance along the surface, so it is smooth per pixel. A slope from screen derivatives
    // changed only per 2 x 2 pixel block and looked blocky. The valleys darken. It fades out with distance.
    half fractalCavity = 0.0h;
    [branch]
    if (_FractalStrength > 0.001h)
    {
        float fade = 1.0 - smoothstep(0.5 * _FractalFade, _FractalFade, length(positionWS - GetCameraPositionWS()));
        [branch]
        if (fade > 0.001)
        {
            float scale = max(_FractalScale, 0.01);
            // Size of one pixel on the surface, and the share of each octave that is larger than about two pixels.
            float footprint = max(length(positionDx), length(positionDy));
            float4 wavelength = scale / float4(1.0, 2.07, 4.2849, 8.8697) / max(_FractalStretch, 1.0);
            float4 octaveFade = saturate(wavelength / max(footprint, 1e-5) * 0.5 - 1.0);
            // Tangent axes of the surface.
            float3 helper = abs(normalWS.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
            float3 tangent = normalize(cross(helper, normalWS));
            float3 bitangent = cross(normalWS, tangent);
            float step = max(0.01 * scale, 0.5 * footprint);
            // The domain warp is smooth, so the three samples share it.
            float3 stretch = float3(1.0, _FractalStretch, 1.0) / scale;
            float3 p = positionWS * stretch;
            float3 warp = (float3(RockValueNoise(p * 0.5 + 13.1), RockValueNoise(p * 0.5 + 47.3),
                RockValueNoise(p * 0.5 + 71.9)) - 0.5) * 1.2;
            float h0 = RockRidgedFractal(p + warp, octaveFade);
            float h1 = RockRidgedFractal((positionWS + tangent * step) * stretch + warp, octaveFade);
            float h2 = RockRidgedFractal((positionWS + bitangent * step) * stretch + warp, octaveFade);
            float amount = _FractalStrength * fade / step;
            float3 gradient = ((h1 - h0) * tangent + (h2 - h0) * bitangent) * amount;
            normalWS = normalize(normalWS - gradient);
            fractalCavity = (half)((1.0 - h0) * fade * 0.5);
        }
    }
    half4 mask = lerp(maskNear, maskFar, farWeight);

    // Variation from the generated geometry: layer color, hard layers, large patches and cavities.
    half3 layerColor = lerp(_BandColorA.rgb, _BandColorB.rgb, layerValue);
    albedo *= lerp(1.0, layerColor, _BandStrength);
    albedo *= lerp(1.0, _HardBrightness, hardness);
    float patch = RockValueNoise(positionWS / max(_PatchSize, 0.1) + 41.7) * 0.7 +
                  RockValueNoise(positionWS / max(_PatchSize * 0.31, 0.1) + 3.9) * 0.3;
    albedo *= 1.0 + (patch - 0.5) * 2.0 * _PatchStrength;
    albedo *= 1.0 - cavity * _CavityStrength * 0.35;
    albedo *= _BaseColor.rgb;
    [branch]
    if (_StainStrength > 0.001h)
    {
        RockMineralStains(positionWS, geometryNormal, albedo);
    }

    // Dust settles on faces that look up. It is a color of the same rock, not a second rock.
    float dustNoise = RockValueNoise(positionWS * 0.45 + 9.1);
    half dust = saturate((geometryNormal.y - _DustStart) / 0.25) * smoothstep(0.25, 0.65, dustNoise) * _DustAmount;
    albedo = lerp(albedo, _DustColor.rgb * (0.85 + 0.3 * dustNoise), dust);
    normalWS = normalize(lerp(normalWS, geometryNormal, dust * 0.7));
    smoothness = lerp(_SmoothnessMin, _SmoothnessMax, mask.a) * (1.0 - dust);
    occlusion = lerp(1.0, mask.g, _OcclusionStrength) * (1.0 - cavity * _CavityStrength) * (1.0h - fractalCavity);
    [branch]
    if (_FaceDetailCenter.w > 0.5)
    {
        RockFaceDetail(positionWS, geometryNormal, albedo, normalWS, smoothness, occlusion);
    }
    [branch]
    if (_RockMasks > 0.5h)
    {
        // Surface fractures live only in the normals, so they do not change the silhouette.
        [branch]
        if (_FractureStrength > 0.001h)
        {
            half crack = RockDetailLayer(TEXTURE2D_ARGS(_FractureNormalMap, sampler_NormalMap), positionWS, geometryNormal,
                _FractureSize, _FractureStrength, normalWS);
            half dark = saturate(crack * 1.6h - 0.25h) * _FractureDarken;
            albedo *= 1.0h - dark;
            occlusion *= 1.0h - dark * 0.5h;
        }
        [branch]
        if (_GritStrength > 0.001h)
        {
            RockDetailLayer(TEXTURE2D_ARGS(_GritNormalMap, sampler_NormalMap), positionWS, geometryNormal, _GritSize,
                _GritStrength, normalWS);
        }
        albedo *= 1.0h + (RockPlaneTone(positionWS, geometryNormal) - 0.5h) * 2.0h * _PlaneValue;
        // The grain comes first: the veins are a mineral of their own and lie over the grain.
        [branch]
        if (_FarGrain > 0.001h)
        {
            RockFarGrain(positionWS, geometryNormal, max(length(positionDx), length(positionDy)), albedo);
        }
        [branch]
        if (_FractureVolumeScale.w > 0.5)
        {
            RockFractureSurface(positionWS, max(length(positionDx), length(positionDy)), (half)mask.r, albedo, smoothness,
                occlusion);
        }
        [branch]
        if (_LineVein + _LineCrack + _LineHalo > 0.001h)
        {
            RockLines(positionWS, geometryNormal, (half)mask.r, albedo, normalWS, smoothness, occlusion);
        }
        RockGeneratedWeathering(positionWS, geometryNormal, input.masks, cavity, (half)mask.b, (half)mask.r, albedo,
            smoothness, occlusion);
        [branch]
        if (_EdgeRound > 0.0001)
        {
            normalWS = RockEdgeRound(positionDx, positionDy, geometryNormal, input.masks.x, normalWS);
        }
    }
    [branch]
    if (_MossAmount > 0.001h)
    {
        RockMoss(positionWS, geometryNormal, cavity, (half)mask.b, albedo, normalWS, smoothness, occlusion);
    }

#if defined(_GROUND_TERRAIN)
#if defined(_ROCK_UV)
    // Soil and moss tint of the rock around the asset ground. The rock texture stays; the ground covers it below.
    [branch]
    if (_GroundFringe > 0.001h)
    {
        half fringe = RockGroundFringe(input.uv, positionWS, geometryNormal);
        albedo = lerp(albedo, albedo * _GroundFringeColor.rgb * 2.0h, fringe);
    }
#endif
    // Dust from the terrain ground at the base. Only layers with Object Dust, such as sand and dry dirt, leave dust.
    // Grass and rock ground leave none. The dust is a thin film: it takes the ground colour, settles in the low parts
    // of the rock, and keeps the rock normal. A renderer without terrain control maps has no layers and keeps the rock.
    [branch]
    if (groundWeight > 0.001 && _GroundLayerCount > 0.5 && _GroundDustAmount > 0.001h)
    {
        half3 groundAlbedo;
        half3 groundNormal;
        half groundSmoothness;
        // The layers come from the terrain beside the rock. The terrain under the rock has steep-ground layers that
        // the rock hides. The world colour and the ground cover of that point then shade the dust as on the terrain.
        half3 coverStrength;
        half groundOcclusion;
        half objectDust;
        LoogaSampleTerrainGround(positionWS, positionWS.xz + input.groundOffset, positionDx, positionDy,
            geometryNormal, groundAlbedo, groundNormal, groundSmoothness, coverStrength, groundOcclusion, objectDust);
        // Dust Reach keeps the upper part of the blend height clean. 0.5 = the lower half of the blend height.
        half reach = saturate(1.0h - (1.0h - groundWeight) / max(_GroundDustReach, 0.05h));
        half dustWeight = reach * objectDust;
        [branch]
        if (dustWeight > 0.001h)
        {
            float3 coverPosition = float3(positionWS.x + input.groundOffset.x, positionWS.y,
                positionWS.z + input.groundOffset.y);
            float cameraDistance = distance(GetCameraPositionWS(), positionWS);
            float3 coverGradient = 0;
            LoogaWorldDebug coverDebug = (LoogaWorldDebug)0;
            half soilLuminance = dot(groundAlbedo, LoogaLuminanceWeights);
            ApplyLoogaWorldColor(groundAlbedo, coverPosition, float3(0, 1, 0), cameraDistance, positionDx.xz,
                positionDy.xz, coverStrength.x, coverDebug);
            ApplyLoogaGroundCover(groundAlbedo, coverGradient, groundSmoothness, coverPosition, cameraDistance,
                positionDx.xz, positionDy.xz, coverStrength.y, soilLuminance, coverStrength.z, coverDebug);
            // Two scales of noise break the dust into patches and grains. The dust fills the low parts of the rock
            // texture first (mask blue), and bright raised grains stay clean longest.
            half grains = (half)(RockValueNoise(positionWS * 1.7 + 23.1) * 0.6 +
                                 RockValueNoise(positionWS * 6.3 + 5.7) * 0.4) - 0.5h;
            half rockBrightness = dot(albedoNear, half3(0.333h, 0.333h, 0.333h));
            half dust = saturate((dustWeight - 0.5h + grains * 0.6h - (rockBrightness - 0.4h) * 0.3h -
                                  ((half)mask.b - 0.5h) * _GroundHeightBlend) / _GroundContrast + 0.5h);
            dust *= _GroundDustAmount;
            albedo = lerp(albedo, groundAlbedo * _GroundColor.rgb, dust);
            smoothness = lerp(smoothness, groundSmoothness, dust);
            // A dust film fills the fine relief of the rock a little. The rock shape stays.
            normalWS = normalize(lerp(normalWS, geometryNormal, dust * 0.5h));
        }
    }
#endif
    // In a cave the rock above blocks the sky and bounce light. Occlusion scales both.
    occlusion *= 1.0h - input.caveShade.x;
    // Crack lines. The distance comes from the vertices, so the crack stays sharp on any mesh. A crack is a narrow groove: its
    // opening changes along it, its rims are rough at the scale of the grains, its walls turn the normal so they take the
    // light as real walls do, its core is dark, and its worn lips are a little pale. The pixel coverage sets the strength, so a
    // far crack fades out and does not stay one bold pixel wide.
    [branch]
    if (_CrackLineWidth > 0.0h)
    {
        float crack = (float)input.caveShade.y;
        float width = _CrackLineWidth * (0.5 + RockValueNoise(positionWS * 0.9 + 3.1));
        float rough = crack + (RockValueNoise(positionWS * 30.0 + 11.3) - 0.5) * 0.7 * width;
        float pixel = max(fwidth(crack), 1e-5);
        float fade = saturate(2.0 * width / pixel);
        float coverage = saturate((width - abs(rough)) / pixel + 0.5) * fade;
        float across = abs(rough) / width;
        // The surface gradient of the crack distance points across the crack. Each wall leans in toward the middle.
        float3 sideX = cross(positionDy, (float3)geometryNormal);
        float3 sideY = cross((float3)geometryNormal, positionDx);
        float determinant = dot(positionDx, sideX);
        determinant = abs(determinant) > 1e-12 ? determinant : 1e-12;
        float3 gradient = (ddx(crack) * sideX + ddy(crack) * sideY) / determinant;
        float3 outward = gradient / max(length(gradient), 1e-6) * (rough >= 0.0 ? 1.0 : -1.0);
        normalWS = normalize(normalWS - (half3)(outward * CrackWallSlope * coverage));
        half core = (half)((1.0 - smoothstep(0.0, 0.7, across)) * coverage);
        half lip = (half)(smoothstep(1.0, 1.4, across) * (1.0 - smoothstep(1.4, 2.2, across)) * fade);
        albedo *= (1.0h - core * _CrackLineDarken) * (1.0h + lip * 0.15h);
        occlusion *= 1.0h - (half)coverage * _CrackLineDarken * 0.6h;
    }
}

void RockFragment(Varyings input, out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float3 positionWS = input.positionWS;
    half3 albedo;
    half3 normalWS;
    half smoothness;
    half occlusion;
    RockSurface(input, albedo, normalWS, smoothness, occlusion);

    SurfaceData surface = (SurfaceData)0;
    surface.albedo = albedo;
    surface.metallic = 0;
    surface.specular = 0;
    surface.smoothness = smoothness;
    surface.occlusion = occlusion;
    surface.alpha = 1;
    surface.normalTS = half3(0, 0, 1);

    InputData inputData = (InputData)0;
    inputData.positionWS = positionWS;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
    inputData.shadowCoord = TransformWorldToShadowCoord(positionWS);
    inputData.fogCoord = InitializeInputDataFog(float4(positionWS, 1.0), input.fogFactor);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
#if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
    inputData.bakedGI = SAMPLE_GI(input.vertexSH, GetAbsolutePositionWS(positionWS), normalWS,
        inputData.viewDirectionWS, input.positionCS.xy, input.probeOcclusion, inputData.shadowMask);
#else
    inputData.bakedGI = SampleSHPixel(input.vertexSH, normalWS);
    inputData.shadowMask = half4(1, 1, 1, 1);
#endif

    half4 color = UniversalFragmentPBR(inputData, surface);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    outColor = half4(color.rgb, 1);
#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif
