#ifndef LOOGA_TERRAIN_SURFACE_NATIVE_PASSES_INCLUDED
#define LOOGA_TERRAIN_SURFACE_NATIVE_PASSES_INCLUDED

// Pass code of the surface targets. The native Unity terrain target: the terrain engine sets the heightmap, normal
// map, holes and patch instance data, and LoogaTerrainSurfaceBinding sets the control maps through the splat property
// block. The patch target (LOOGA_TERRAIN_PATCHES): the Looga TerrainRenderer draws GPU-selected patches
// (TerrainGeometry.hlsl) and the binding sets the control maps through the renderer's property block. The mesh target
// (LOOGA_TERRAIN_MESH): a mesh over the terrain ground, for example the soil on a cliff top, shows the terrain surface
// of the ground. LoogaTerrainGroundBlock sets the control maps and the tile through the renderer's property block.

#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurface.hlsl"
#include "Packages/com.loogasoft.loogaterrain/Runtime/Water/Shaders/LoogaShoreSwash.hlsl"

#if defined(LOOGA_TERRAIN_PATCHES)

#define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
#include "UnityIndirect.cginc"
#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/TerrainGeometry.hlsl"

#ifdef SCENESELECTIONPASS
    int _ObjectId;
    int _PassValue;
#endif

struct LoogaTerrainAttributes
{
    float3 positionOS : POSITION;   // Patch grid position, 0 to 1 in xz
    uint instance : SV_InstanceID;
};

// The vertex of a selected patch, from the heightmap.
void GetLoogaTerrainVertex(LoogaTerrainAttributes input, out float3 positionWS, out float3 normalWS, out float2 uv)
{
    InitIndirectDrawArgs(0);
    uv = LoogaPatchUV(input.positionOS.xz, GetIndirectInstanceID(input.instance));
    positionWS = LoogaPosition(uv);
    normalWS = LoogaNormal(uv);
}

// Per-pixel terrain normal from the heightmap, so it does not depend on the patch LOD.
float3 GetLoogaTerrainNormalWS(float2 uv, float3 vertexNormalWS)
{
    return LoogaNormal(uv);
}

// Curvature of the terrain heights over the far detail step for the far detail: positive in hollows, negative on
// crests, in 1 / m. A sample past the tile edge mirrors the opposite sample, so the curvature along that axis is 0 there.
float LoogaTerrainCurvature(float2 uv)
{
#if defined(LOOGA_TERRAIN_BATCHED)
    float3 size = _DrawTiles[_LoogaDrawTile].size.xyz;
#else
    float3 size = _LoogaSize.xyz;
#endif
    float stepMeters = max(_LoogaFarCurvature.y, 0.01);
    float2 step = stepMeters / size.xz;
    float center = LoogaHeight(uv);
    float left = LoogaHeight(uv - float2(step.x, 0.0));
    float right = LoogaHeight(uv + float2(step.x, 0.0));
    float down = LoogaHeight(uv - float2(0.0, step.y));
    float up = LoogaHeight(uv + float2(0.0, step.y));
    left = uv.x - step.x < 0.0 ? 2.0 * center - right : left;
    right = uv.x + step.x > 1.0 ? 2.0 * center - left : right;
    down = uv.y - step.y < 0.0 ? 2.0 * center - up : down;
    up = uv.y + step.y > 1.0 ? 2.0 * center - down : up;
    return (left + right + down + up - 4.0 * center) * size.y / (stepMeters * stepMeters);
}

void ClipLoogaTerrainHoles(float2 uv)
{
    LoogaClipHoles(uv);
}

// The TerrainRenderer turns _ALPHATEST_ON on only for terrains with holes. A clip in the shader stops early depth
// writes, so hidden terrain behind hills costs full shading. Terrains without holes use the variant without it.
#if defined(_ALPHATEST_ON)
    #define LOOGA_TERRAIN_HAS_HOLES 1
#endif

#elif defined(LOOGA_TERRAIN_MESH)

#include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaCaveEntranceClip.hlsl"

// Per renderer: the terrain tile under the mesh. x and y are the XZ origin, z and w are 1 / XZ size.
float4 _GroundTile;

struct LoogaTerrainAttributes
{
    float4 positionOS : POSITION;
    float4 normalOS : NORMAL;
    // xy: world XZ offset from the vertex to the terrain point whose layers the vertex shows. z: cover weight. w: 1
    // where the mesh casts a shadow.
    float4 cover : TEXCOORD2;
};

// The mesh vertex in world space. The control UV is the UV of the terrain point that the vertex shows.
void GetLoogaTerrainVertex(LoogaTerrainAttributes input, out float3 positionWS, out float3 normalWS, out float2 uv)
{
    positionWS = TransformObjectToWorld(input.positionOS.xyz);
    normalWS = TransformObjectToWorldNormal(input.normalOS.xyz);
    uv = (positionWS.xz + input.cover.xy - _GroundTile.xy) * _GroundTile.zw;
    // The mesh lies on the surface that it covers, for example rock. It moves out a little, more at a distance, so
    // the depth test keeps it in front.
    positionWS += normalWS * max(0.02, 0.0005 * distance(GetCameraPositionWS(), positionWS));
}

// Clip position of the mesh with its depth moved toward the camera. The screen position and the shading stay the same.
// The terrain draws with coarse levels of detail whose triangles lie up to some decimeters over the true ground next
// to a cliff, and showed through the mesh there as dark slivers.
float4 GetLoogaTerrainMeshClipPosition(float3 positionWS)
{
    float3 toCamera = GetCameraPositionWS() - positionWS;
    float cameraDistance = length(toCamera);
    float pull = min(0.15 + 0.004 * cameraDistance, 0.5 * cameraDistance);
    return TransformWorldToHClip(positionWS + toCamera * (pull / max(cameraDistance, 1e-4)));
}

float3 GetLoogaTerrainNormalWS(float2 uv, float3 vertexNormalWS)
{
    return normalize(vertexNormalWS);
}

float LoogaCoverNoise(float3 positionWS)
{
    float3 cell = floor(positionWS);
    float3 f = positionWS - cell;
    f = f * f * (3.0 - 2.0 * f);
    float values[8];
    [unroll]
    for (int corner = 0; corner < 8; corner++)
    {
        float3 h = frac((cell + float3(corner & 1, (corner >> 1) & 1, corner >> 2)) * 0.1031);
        h += dot(h, h.yzx + 33.33);
        values[corner] = frac((h.x + h.y) * h.z);
    }
    float x0 = lerp(lerp(values[0], values[1], f.x), lerp(values[2], values[3], f.x), f.y);
    float x1 = lerp(lerp(values[4], values[5], f.x), lerp(values[6], values[7], f.x), f.y);
    return lerp(x0, x1, f.z);
}

// Below a cover weight of 1 the mesh ends along a ragged edge. The noise is fixed in the world, so the edge does not
// move with the camera, and all passes cut the same pixels. The cover also ends in cave entrance boxes. The mesh of an
// entrance adds 2 to its cover weights, and it stays.
void ClipLoogaTerrainCover(float3 positionWS, half cover)
{
    if (cover > 1.5h)
    {
        cover -= 2.0h;
    }
    else
    {
        ClipLoogaCaveEntrances(positionWS);
    }
    [branch]
    if (cover < 0.999h)
    {
        float noise = 0.65 * LoogaCoverNoise(positionWS * 1.3) + 0.35 * LoogaCoverNoise(positionWS * 4.1 + 17.0);
        clip(cover - noise);
    }
}

#else

CBUFFER_START(_Terrain)
    #ifdef UNITY_INSTANCING_ENABLED
        float4 _TerrainHeightmapRecipSize;  // 1 / width, 1 / height, 1 / (width - 1), 1 / (height - 1)
        float4 _TerrainHeightmapScale;      // Heightmap scale x, y / max height, z
    #endif
    #ifdef SCENESELECTIONPASS
        int _ObjectId;
        int _PassValue;
    #endif
CBUFFER_END

#ifdef UNITY_INSTANCING_ENABLED
    TEXTURE2D(_TerrainHeightmapTexture);
    TEXTURE2D(_TerrainNormalmapTexture);
    SAMPLER(sampler_TerrainNormalmapTexture);
#endif

UNITY_INSTANCING_BUFFER_START(Terrain)
    UNITY_DEFINE_INSTANCED_PROP(float4, _TerrainPatchInstanceData)  // x base, y base, skip scale
UNITY_INSTANCING_BUFFER_END(Terrain)

#ifdef _ALPHATEST_ON
    TEXTURE2D(_TerrainHolesTexture);
    SAMPLER(sampler_TerrainHolesTexture);

    void ClipLoogaTerrainHoles(float2 uv)
    {
        // Compressed hole maps store 0 as a small value, so test against an epsilon.
        clip(SAMPLE_TEXTURE2D(_TerrainHolesTexture, sampler_TerrainHolesTexture, uv).r < 0.0005 ? -1 : 1);
    }

    #define LOOGA_TERRAIN_HAS_HOLES 1
#endif

struct LoogaTerrainAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 texcoord : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// The terrain vertex in world space. Instanced terrain builds the patch vertex from the terrain heightmap; without
// instancing the mesh already has it.
void GetLoogaTerrainVertex(LoogaTerrainAttributes input, out float3 positionWS, out float3 normalWS, out float2 uv)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float4 positionOS = input.positionOS;
    float3 normalOS = input.normalOS;
    uv = input.texcoord;
    #ifdef UNITY_INSTANCING_ENABLED
        float4 instanceData = UNITY_ACCESS_INSTANCED_PROP(Terrain, _TerrainPatchInstanceData);
        float2 sampleCoords = (positionOS.xy + instanceData.xy) * instanceData.z;
        float height = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(sampleCoords, 0)));
        positionOS.xz = sampleCoords * _TerrainHeightmapScale.xz;
        positionOS.y = height * _TerrainHeightmapScale.y;
        normalOS = _TerrainNormalmapTexture.Load(int3(sampleCoords, 0)).rgb * 2 - 1;
        uv = sampleCoords * _TerrainHeightmapRecipSize.zw;
    #endif
    positionWS = TransformObjectToWorld(positionOS.xyz);
    normalWS = TransformObjectToWorldNormal(normalOS);
}

// Per-pixel terrain normal. Instanced terrain reads it from the normal map, so it does not depend on the patch LOD.
float3 GetLoogaTerrainNormalWS(float2 uv, float3 vertexNormalWS)
{
    #ifdef UNITY_INSTANCING_ENABLED
        float2 coords = (uv / _TerrainHeightmapRecipSize.zw + 0.5) * _TerrainHeightmapRecipSize.xy;
        float3 normalOS = SAMPLE_TEXTURE2D(_TerrainNormalmapTexture, sampler_TerrainNormalmapTexture, coords).xyz * 2 - 1;
        return normalize(TransformObjectToWorldNormal(normalOS));
    #else
        return normalize(vertexNormalWS);
    #endif
}

#endif

// Distance that the surface fades use. In footprint mode it is the distance where the reference camera shows a pixel
// of the same size on the ground. A cached surface page has a texel size but no camera, so it uses the same value.
float GetLoogaFadeDistance(float3 positionWS)
{
    // The derivatives are read before the branch.
    float3 dx = ddx(positionWS);
    float3 dy = ddy(positionWS);
    float footprint = sqrt(min(dot(dx, dx), dot(dy, dy)));
    return _LoogaSurfaceFade.x > 0.5 ? footprint * _LoogaSurfaceFade.y : distance(GetCameraPositionWS(), positionWS);
}

// The surface input without the curvature. All derivatives are read here, outside the branches of the passes.
LoogaSurfaceInput GetLoogaTerrainSurfaceInputBase(float3 positionWS, float3 normalWS, float2 uv)
{
    LoogaSurfaceInput input;
    input.positionWS = positionWS;
    input.normalWS = normalWS;
    #if defined(LOOGA_TERRAIN_MESH)
        // A shown terrain point can lie past the tile edge.
        uv = saturate(uv);
    #endif
    input.controlUV = uv;
    input.cameraDistance = GetLoogaFadeDistance(positionWS);
    input.curvature = 0.0;
    input.positionDx = ddx(positionWS);
    input.positionDy = ddy(positionWS);
    input.controlDx = ddx(uv);
    input.controlDy = ddy(uv);
    return input;
}

// Adds the curvature of the far detail. Only the patch target reads the terrain heights in the pixel shader.
void AddLoogaTerrainSurfaceCurvature(inout LoogaSurfaceInput input)
{
    #if defined(LOOGA_TERRAIN_PATCHES)
        UNITY_BRANCH
        if (_LoogaFarCurvature.x > 0.0 && input.cameraDistance > _LoogaFarDetail.x)
        {
            input.curvature = LoogaTerrainCurvature(input.controlUV);
        }
    #endif
}

LoogaSurfaceInput GetLoogaTerrainSurfaceInput(float3 positionWS, float3 normalWS, float2 uv)
{
    LoogaSurfaceInput input = GetLoogaTerrainSurfaceInputBase(positionWS, normalWS, uv);
    AddLoogaTerrainSurfaceCurvature(input);
    return input;
}

// ---------------------------------------------------------------------------------------------------------------
// Virtual texture cache of the patch target. TerrainVirtualTexture fills pages of the surface with the LoogaVTBake
// pass. A page has no camera: its fades use the page texel size as the screen footprint (FadeDistanceMode
// ScreenFootprint). The G-buffer pass reads the page where a pixel covers at least one virtual texel, and applies the
// view and sun terms of the ground cover per pixel. Close pixels and debug views shade directly, and so do pixels past
// the optional slope and footprint ratio limits of the cache settings.
// ---------------------------------------------------------------------------------------------------------------

#if defined(LOOGA_TERRAIN_PATCHES) && defined(LOOGA_TERRAIN_GBUFFER)

#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/VirtualTexturing/VirtualTextureSampling.hlsl"

// Reads the cached surface of a pixel. Returns false when the pixel must shade directly.
bool TryGetLoogaCachedSurface(LoogaSurfaceInput input, out LoogaSurfaceOutput surface)
{
    surface = (LoogaSurfaceOutput)0;
    // The derivatives are read before the branch.
    float2 uvDx = input.controlDx;
    float2 uvDy = input.controlDy;
    bool cached = _LoogaVTEnabled != 0 && _LoogaVTShadeDirectly < 0.5 && _LoogaSurfaceFade.x > 0.5
        && _LoogaSurfaceDebug.x < 0.5
        && input.normalWS.y >= _LoogaVTSampling.y && LoogaVTCanCacheGrad(uvDx, uvDy);
    // One return at the end: FXC reports an early return after a branch as an uninitialized value.
    UNITY_BRANCH
    if (cached)
    {
        float4 albedo = 0;
        float4 normal = 0;
        float4 cover = 0;
        cached = LoogaVTReadGrad(input.controlUV, uvDx, uvDy, albedo, normal, cover);
        if (cached)
        {
            surface.albedo = (half3)albedo.rgb;
            surface.smoothness = (half)albedo.a;
            surface.normalWS = normal.xyz;
            surface.occlusion = (half)normal.a;
            surface.sunVisibility = 1.0h;
            // The page has no metallic channel. Terrain layers are not metallic.
            surface.metallic = 0.0h;
            ApplyLoogaCachedRegionColor(surface, input, (half4)cover);
            ApplyLoogaCachedGroundCover(surface, input, (half4)cover);
        }
    }
    return cached;
}

#endif

#if defined(LOOGA_TERRAIN_VT_PAGE_PASS)

// The page rectangle in tile UV, with the 4-texel border on each side.
float4 _LoogaVTPageRect;

struct LoogaTerrainPageVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
};

struct LoogaTerrainPageOutput
{
    float4 albedo : SV_Target0;   // Albedo and smoothness before the ground cover
    float4 normal : SV_Target1;   // World normal and occlusion
    float4 cover : SV_Target2;    // LoogaSurfaceOutput.pageCover
};

// One triangle over the page viewport.
LoogaTerrainPageVaryings LoogaTerrainPageVertex(uint vertex : SV_VertexID)
{
    LoogaTerrainPageVaryings output;
    float2 uv = float2((vertex << 1) & 2, vertex & 2);
    output.positionCS = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    #if UNITY_UV_STARTS_AT_TOP
        output.positionCS.y = -output.positionCS.y;
    #endif
    output.uv = _LoogaVTPageRect.xy + uv * _LoogaVTPageRect.zw;
    return output;
}

LoogaTerrainPageOutput LoogaTerrainPageFragment(LoogaTerrainPageVaryings input)
{
    // The border texels of an edge page lie past the tile. The world position continues there, and the heights and
    // control maps clamp to the edge.
    float2 uv = saturate(input.uv);
    float3 positionWS = LoogaPosition(input.uv);
    LoogaSurfaceInput surfaceInput;
    surfaceInput.positionWS = positionWS;
    surfaceInput.normalWS = LoogaNormal(uv);
    surfaceInput.controlUV = uv;
    surfaceInput.positionDx = ddx(positionWS);
    surfaceInput.positionDy = ddy(positionWS);
    surfaceInput.controlDx = ddx(input.uv);
    surfaceInput.controlDy = ddy(input.uv);
    // The texel size is the screen footprint of the page.
    float footprint = sqrt(min(dot(surfaceInput.positionDx, surfaceInput.positionDx),
        dot(surfaceInput.positionDy, surfaceInput.positionDy)));
    surfaceInput.cameraDistance = footprint * _LoogaSurfaceFade.y;
    surfaceInput.curvature = 0.0;
    AddLoogaTerrainSurfaceCurvature(surfaceInput);
    LoogaSurfaceOutput surface = EvaluateLoogaTerrainSurface(surfaceInput);

    LoogaTerrainPageOutput output;
    output.albedo = float4(surface.albedo, surface.smoothness);
    output.normal = float4(surface.normalWS, surface.occlusion);
    output.cover = surface.pageCover;
    return output;
}

#endif

// ---------------------------------------------------------------------------------------------------------------
// Lit passes (GBuffer and forward).
// ---------------------------------------------------------------------------------------------------------------

#if defined(LOOGA_TERRAIN_LIT_PASS)

struct LoogaTerrainVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    float3 normalWS : TEXCOORD2;
    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 3);
    float2 dynamicLightmapUV : TEXCOORD4;
    float4 probeOcclusion : TEXCOORD5;
    #if defined(LOOGA_TERRAIN_FORWARD)
        half fogFactor : TEXCOORD6;
    #endif
    #if defined(LOOGA_TERRAIN_MESH)
        half cover : TEXCOORD7;
    #endif
    UNITY_VERTEX_OUTPUT_STEREO
};

LoogaTerrainVaryings LoogaTerrainLitVertex(LoogaTerrainAttributes input)
{
    LoogaTerrainVaryings output = (LoogaTerrainVaryings)0;
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    float3 positionWS;
    float3 normalWS;
    float2 uv;
    GetLoogaTerrainVertex(input, positionWS, normalWS, uv);

    #if defined(LOOGA_TERRAIN_MESH)
        output.positionCS = GetLoogaTerrainMeshClipPosition(positionWS);
    #else
        output.positionCS = TransformWorldToHClip(positionWS);
    #endif
    output.positionWS = positionWS;
    output.normalWS = normalWS;
    output.uv = uv;
    #if defined(LOOGA_TERRAIN_MESH)
        output.cover = input.cover.z;
    #endif
    OUTPUT_LIGHTMAP_UV(uv, unity_LightmapST, output.staticLightmapUV);
    #if defined(DYNAMICLIGHTMAP_ON)
        output.dynamicLightmapUV = uv * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
    #endif
    OUTPUT_SH4(positionWS, output.normalWS, GetWorldSpaceNormalizeViewDir(positionWS), output.vertexSH,
        output.probeOcclusion);
    #if defined(LOOGA_TERRAIN_FORWARD)
        output.fogFactor = ComputeFogFactor(output.positionCS.z);
    #endif
    return output;
}

// Replaces the shading inputs with an unlit debug colour. The colour goes to emission, so lighting does not change it.
void ApplyLoogaTerrainDebug(LoogaSurfaceOutput surface, inout half3 albedo, inout half metallic, inout half smoothness,
    inout half occlusion, out half3 emission)
{
    emission = 0;
    if (!surface.debug) return;
    emission = surface.debugColor;
    albedo = 0;
    metallic = 0;
    smoothness = 0;
    occlusion = 1;
}

#if defined(LOOGA_TERRAIN_GBUFFER) || defined(LOOGA_TERRAIN_FORWARD)

// Wet ground at the shore of a water body with a swash run-up (LoogaShoreSwash.hlsl): damp and darker below the
// run-up limit, and a wet sheen where the swash has just drained. It comes after the cache, because it changes each
// frame. The film of the sheen fills the small bumps of the ground and lies almost level on a gentle beach, so its
// normal goes most of the way to straight up. Wet bumps that glitter, or the folds of the terrain shape in a mirror, do
// not match the smooth water next to them.
void ApplyLoogaShoreWetness(float3 positionWS, inout LoogaSurfaceOutput surface)
{
    float2 wetness = LoogaShoreWetness(positionWS);
    UNITY_BRANCH
    if (wetness.x + wetness.y <= 0.0) return;

    half darken = (half)max(wetness.x * 0.6, wetness.y);
    surface.albedo *= 1.0h - 0.45h * darken;
    surface.smoothness = lerp(surface.smoothness, max(surface.smoothness, 0.4h), (half)wetness.x);
    surface.smoothness = lerp(surface.smoothness, 0.85h, (half)wetness.y);
    surface.normalWS = normalize(lerp(surface.normalWS, float3(0.0, 1.0, 0.0), saturate(wetness.y * 1.5)));
}

#endif

#if defined(LOOGA_TERRAIN_GBUFFER)

LoogaGBufferOutput LoogaTerrainGBufferFragment(LoogaTerrainVaryings input)
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    #ifdef LOOGA_TERRAIN_HAS_HOLES
        ClipLoogaTerrainHoles(input.uv);
    #endif
    #if defined(LOOGA_TERRAIN_MESH)
        ClipLoogaTerrainCover(input.positionWS, input.cover);
    #endif

    float3 terrainNormalWS = GetLoogaTerrainNormalWS(input.uv, input.normalWS);
    LoogaSurfaceInput surfaceInput = GetLoogaTerrainSurfaceInputBase(input.positionWS, terrainNormalWS, input.uv);
    LoogaSurfaceOutput surface = (LoogaSurfaceOutput)0;
    bool cached = false;
    #if defined(LOOGA_TERRAIN_PATCHES)
        cached = TryGetLoogaCachedSurface(surfaceInput, surface);
    #endif
    UNITY_BRANCH
    if (!cached)
    {
        AddLoogaTerrainSurfaceCurvature(surfaceInput);
        surface = EvaluateLoogaTerrainSurface(surfaceInput);
    }

    ApplyLoogaShoreWetness(input.positionWS, surface);
    half3 albedo = surface.albedo;
    half3 normalWS = surface.normalWS;
    half metallic = surface.metallic;
    half smoothness = surface.smoothness;
    half occlusion = surface.occlusion;
    half3 specularF0 = kDielectricSpec.rgb;
    ApplyLoogaDBuffer(input.positionCS, albedo, normalWS, metallic, specularF0, occlusion, smoothness);
    half3 emission;
    ApplyLoogaTerrainDebug(surface, albedo, metallic, smoothness, occlusion, emission);

    // Looga Lite contract: GBuffer3 holds only emission and the baked diffuse light. Looga Lighting adds the direct
    // light and the indirect specular for pixels with the Looga marker.
    half3 diffuseColor = GetLoogaDiffuseColor(albedo, metallic, specularF0);
    half4 modelParameters = GetDefaultLoogaModelParameters();
    half3 bakedGI;
    half4 shadowMask;
    LOOGA_SAMPLE_BAKED_LIGHTING(input, normalWS, 1.0h - smoothness, modelParameters, bakedGI, shadowMask);
    half3 ambientDiffuse = EvaluateLoogaGBufferAmbient(diffuseColor, bakedGI, occlusion);

    LoogaGBufferOutput output;
    output.GBuffer0 = half4(albedo, PackLoogaMaterialFlags(GetLoogaCommonMaterialFlags()));
    output.GBuffer1 = half4(PackLoogaGBufferSpecular(metallic, specularF0), occlusion);
    // Main light shadow of the plant canopy above the ground (Looga Lighting reads GBuffer1 B).
    output.GBuffer1.b = PackLoogaCanopyShadow(surface.sunVisibility);
    output.GBuffer2 = half4(PackGBufferNormal(normalWS), smoothness);
    output.GBuffer3 = half4(emission + ambientDiffuse, 0.0);
    FillLoogaGBufferExtraOutputs(output, input.positionCS.z, shadowMask);
    return output;
}

#endif

#if defined(LOOGA_TERRAIN_FORWARD)

// Forward fallback for the forward rendering paths. It uses the URP lighting, not Looga Lighting.
half4 LoogaTerrainForwardFragment(LoogaTerrainVaryings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    #ifdef LOOGA_TERRAIN_HAS_HOLES
        ClipLoogaTerrainHoles(input.uv);
    #endif
    #if defined(LOOGA_TERRAIN_MESH)
        ClipLoogaTerrainCover(input.positionWS, input.cover);
    #endif

    float3 terrainNormalWS = GetLoogaTerrainNormalWS(input.uv, input.normalWS);
    LoogaSurfaceOutput surface = EvaluateLoogaTerrainSurface(
        GetLoogaTerrainSurfaceInput(input.positionWS, terrainNormalWS, input.uv));
    ApplyLoogaShoreWetness(input.positionWS, surface);

    InputData inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.positionCS = input.positionCS;
    inputData.normalWS = surface.normalWS;
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
    #if defined(DYNAMICLIGHTMAP_ON)
        inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.dynamicLightmapUV, input.vertexSH, inputData.normalWS);
    #elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
        inputData.bakedGI = SAMPLE_GI(input.vertexSH, GetAbsolutePositionWS(inputData.positionWS), inputData.normalWS,
            inputData.viewDirectionWS, input.positionCS.xy, input.probeOcclusion, inputData.shadowMask);
    #else
        inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
    #endif

    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo = surface.albedo;
    surfaceData.metallic = surface.metallic;
    surfaceData.smoothness = surface.smoothness;
    surfaceData.occlusion = surface.occlusion;
    surfaceData.normalTS = half3(0, 0, 1);
    surfaceData.alpha = 1;
    ApplyLoogaTerrainDebug(surface, surfaceData.albedo, surfaceData.metallic, surfaceData.smoothness,
        surfaceData.occlusion, surfaceData.emission);

    half4 color = UniversalFragmentPBR(inputData, surfaceData);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    return half4(color.rgb, 1.0);
}

#endif

#endif

// ---------------------------------------------------------------------------------------------------------------
// Shadow, depth, depth-normals and scene-selection passes.
// ---------------------------------------------------------------------------------------------------------------

#if defined(LOOGA_TERRAIN_DEPTH_PASS)

float3 _LightDirection;
float3 _LightPosition;

struct LoogaTerrainDepthVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 normalWS : TEXCOORD1;
    #if defined(LOOGA_TERRAIN_MESH)
        float3 positionWS : TEXCOORD2;
        half cover : TEXCOORD3;
        half cast : TEXCOORD4;
    #endif
    UNITY_VERTEX_OUTPUT_STEREO
};

LoogaTerrainDepthVaryings LoogaTerrainDepthVertex(LoogaTerrainAttributes input)
{
    LoogaTerrainDepthVaryings output = (LoogaTerrainDepthVaryings)0;
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    float3 positionWS;
    float3 normalWS;
    float2 uv;
    GetLoogaTerrainVertex(input, positionWS, normalWS, uv);
    #if defined(LOOGA_TERRAIN_MESH)
        output.positionCS = GetLoogaTerrainMeshClipPosition(positionWS);
    #else
        output.positionCS = TransformWorldToHClip(positionWS);
    #endif
    output.uv = uv;
    output.normalWS = normalWS;
    #if defined(LOOGA_TERRAIN_MESH)
        output.positionWS = positionWS;
        output.cover = input.cover.z;
    #endif
    return output;
}

LoogaTerrainDepthVaryings LoogaTerrainShadowVertex(LoogaTerrainAttributes input)
{
    LoogaTerrainDepthVaryings output = (LoogaTerrainDepthVaryings)0;
    float3 positionWS;
    float3 normalWS;
    float2 uv;
    GetLoogaTerrainVertex(input, positionWS, normalWS, uv);
    #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
        float3 lightDirectionWS = normalize(_LightPosition - positionWS);
    #else
        float3 lightDirectionWS = _LightDirection;
    #endif
    #if defined(LOOGA_TERRAIN_MESH)
        // A mesh surface turned away from the light gets no normal inset, as on the rock faces: the inset moved a
        // single sheet into the ground behind it.
        float facing = dot(lightDirectionWS, normalWS);
        float inset = facing > 0.0 ? (1.0 - facing) * _ShadowBias.y : 0.0;
        output.positionCS = TransformWorldToHClip(positionWS + lightDirectionWS * _ShadowBias.x + normalWS * inset);
        output.positionWS = positionWS;
        output.cover = input.cover.z;
        output.cast = input.cover.w;
    #else
        output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
    #endif
    // Clamp casters behind the near plane onto it, so terrain outside the shadow volume still casts.
    #if UNITY_REVERSED_Z
        output.positionCS.z = min(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
    #else
        output.positionCS.z = max(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
    #endif
    output.uv = uv;
    output.normalWS = normalWS;
    return output;
}

half4 LoogaTerrainDepthFragment(LoogaTerrainDepthVaryings input) : SV_Target
{
    #ifdef LOOGA_TERRAIN_HAS_HOLES
        ClipLoogaTerrainHoles(input.uv);
    #endif
    #if defined(LOOGA_TERRAIN_MESH)
        ClipLoogaTerrainCover(input.positionWS, input.cover);
        #if defined(LOOGA_TERRAIN_SHADOW_PASS)
            clip(input.cast - 0.5h);
        #endif
    #endif
    #ifdef SCENESELECTIONPASS
        return half4(_ObjectId, _PassValue, 1.0, 1.0);
    #endif
    return input.positionCS.z;
}

// The depth-normals pass writes the terrain normal without the layer normal maps. The deferred path reads the
// full normal from the GBuffer instead.
void LoogaTerrainDepthNormalsFragment(LoogaTerrainDepthVaryings input, out half4 outNormalWS : SV_Target0
    #ifdef _WRITE_RENDERING_LAYERS
        , out uint outRenderingLayers : SV_Target1
    #endif
    )
{
    #ifdef LOOGA_TERRAIN_HAS_HOLES
        ClipLoogaTerrainHoles(input.uv);
    #endif
    #if defined(LOOGA_TERRAIN_MESH)
        ClipLoogaTerrainCover(input.positionWS, input.cover);
    #endif
    float3 normalWS = GetLoogaTerrainNormalWS(input.uv, input.normalWS);
    #if defined(_GBUFFER_NORMALS_OCT)
        float2 octNormal = PackNormalOctQuadEncode(normalWS);
        outNormalWS = half4(PackFloat2To888(saturate(octNormal * 0.5 + 0.5)), 0.0);
    #else
        outNormalWS = half4(normalWS, 0.0);
    #endif
    #ifdef _WRITE_RENDERING_LAYERS
        outRenderingLayers = EncodeMeshRenderingLayer();
    #endif
}

#endif

// ---------------------------------------------------------------------------------------------------------------
// Meta pass for lightmap baking.
// ---------------------------------------------------------------------------------------------------------------

#if defined(LOOGA_TERRAIN_META_PASS)

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

struct LoogaTerrainMetaVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    float3 normalWS : TEXCOORD2;
};

LoogaTerrainMetaVaryings LoogaTerrainMetaVertex(LoogaTerrainAttributes input)
{
    LoogaTerrainMetaVaryings output = (LoogaTerrainMetaVaryings)0;
    float3 positionWS;
    float3 normalWS;
    float2 uv;
    GetLoogaTerrainVertex(input, positionWS, normalWS, uv);
    // Instanced terrain has no lightmap UV channels. The terrain UV is the lightmap UV.
    output.positionCS = UnityMetaVertexPosition(TransformWorldToObject(positionWS), uv, uv);
    output.uv = uv;
    output.positionWS = positionWS;
    output.normalWS = normalWS;
    return output;
}

half4 LoogaTerrainMetaFragment(LoogaTerrainMetaVaryings input) : SV_Target
{
    #ifdef LOOGA_TERRAIN_HAS_HOLES
        ClipLoogaTerrainHoles(input.uv);
    #endif
    LoogaSurfaceOutput surface = EvaluateLoogaTerrainSurface(
        GetLoogaTerrainSurfaceInput(input.positionWS, normalize(input.normalWS), input.uv));
    MetaInput meta = (MetaInput)0;
    meta.Albedo = surface.albedo;
    meta.Emission = 0;
    return UnityMetaFragment(meta);
}

#endif

#endif
