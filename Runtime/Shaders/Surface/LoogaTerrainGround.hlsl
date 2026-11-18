#ifndef LOOGA_TERRAIN_GROUND_INCLUDED
#define LOOGA_TERRAIN_GROUND_INCLUDED

// The terrain ground at a world position, for meshes that blend into the terrain below them: the four strongest splat
// layers of the terrain tile, with the layer arrays and colour settings of its terrain surface. The inputs are in
// LoogaTerrainGroundInput.hlsl. A project can define LOOGA_TERRAIN_GROUND_HOOK(positionWS, albedo, normalTS,
// smoothness) before it includes this file, so the ground takes the same project shading as the terrain.

// Whiteout blend of three tangent-space normals. The projections are X = (z * sign x, y), Y = (x * sign y, z) and
// Z = (-x * sign z, y).
half3 LoogaBlendTriplanarNormals(half3 normal, half3 weights, half3 axisSign, half3 normalX, half3 normalY,
    half3 normalZ)
{
    normalX.x *= axisSign.x;
    normalY.x *= axisSign.y;
    normalZ.x *= -axisSign.z;
    normalX = half3(normalX.xy + normal.zy, abs(normalX.z) * normal.x);
    normalY = half3(normalY.xy + normal.xz, abs(normalY.z) * normal.y);
    normalZ = half3(normalZ.xy + normal.xy, abs(normalZ.z) * normal.z);
    return normalize(normalX.zyx * weights.x + normalY.xzy * weights.y + normalZ.xyz * weights.z);
}

half4 LoogaGroundLayerProperty(int layer, int row)
{
    return LOAD_TEXTURE2D(_GroundLayerProps, int2(layer, row));
}

// One terrain layer with the terrain triplanar UVs, the per-layer UV scale and the per-layer colour settings.
// The gradients come from outside the ground branch, so the mip levels stay right at the branch edge.
void LoogaGroundSampleTerrainLayer(int layer, float3 position, float3 positionDx, float3 positionDy, half3 weights,
    half3 axisSign, half3 normal, out half3 albedo, out half3 normalWS, out half smoothness)
{
    float2 scale = 0.1 * _GroundTriplanarScale.xy;
    float2 offset = _GroundTriplanarScale.zw;
    float4 layerUv = LoogaGroundLayerProperty(layer, 0);
    float2 uvX = (position.zy * scale + offset) * float2(axisSign.x, 1) * layerUv.xy + layerUv.zw;
    float2 uvY = (position.xz * float2(-1, 1) * scale + offset) * float2(axisSign.y, 1) * layerUv.xy + layerUv.zw;
    float2 uvZ = (position.xy * scale + offset) * float2(axisSign.z, 1) * layerUv.xy + layerUv.zw;
    float2 dxX = positionDx.zy * scale * float2(axisSign.x, 1) * layerUv.xy;
    float2 dyX = positionDy.zy * scale * float2(axisSign.x, 1) * layerUv.xy;
    float2 dxY = positionDx.xz * float2(-1, 1) * scale * float2(axisSign.y, 1) * layerUv.xy;
    float2 dyY = positionDy.xz * float2(-1, 1) * scale * float2(axisSign.y, 1) * layerUv.xy;
    float2 dxZ = positionDx.xy * scale * float2(axisSign.z, 1) * layerUv.xy;
    float2 dyZ = positionDy.xy * scale * float2(axisSign.z, 1) * layerUv.xy;

    albedo = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundAlbedoArray, sampler_GroundAlbedoArray, uvX, layer, dxX, dyX).rgb *
             weights.x +
             SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundAlbedoArray, sampler_GroundAlbedoArray, uvY, layer, dxY, dyY).rgb *
             weights.y +
             SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundAlbedoArray, sampler_GroundAlbedoArray, uvZ, layer, dxZ, dyZ).rgb *
             weights.z;
    half4 rawX = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundNormalArray, sampler_GroundNormalArray, uvX, layer, dxX, dyX);
    half4 rawY = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundNormalArray, sampler_GroundNormalArray, uvY, layer, dxY, dyY);
    half4 rawZ = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundNormalArray, sampler_GroundNormalArray, uvZ, layer, dxZ, dyZ);
    smoothness = rawX.r * weights.x + rawY.r * weights.y + rawZ.r * weights.z;

    // The normal array packs the normal X and Y in A and G.
    half3 normalX = half3(rawX.ag * 2 - 1, 1);
    half3 normalY = half3(rawY.ag * 2 - 1, 1);
    half3 normalZ = half3(rawZ.ag * 2 - 1, 1);
    normalX.xy *= _GroundNormalStrength;
    normalY.xy *= _GroundNormalStrength;
    normalZ.xy *= _GroundNormalStrength;
    // The terrain mirrors the top and front projections against the projections of LoogaBlendTriplanarNormals.
    normalY.x = -normalY.x;
    normalZ.x = -normalZ.x;
    normalWS = LoogaBlendTriplanarNormals(normal, weights, axisSign, normalX, normalY, normalZ);

    half saturation = LoogaGroundLayerProperty(layer, 9).a;
    half3 tint = LoogaGroundLayerProperty(layer, 1).rgb;
    half4 brightnessContrast = LoogaGroundLayerProperty(layer, 3);
    half luminance = dot(albedo, half3(0.2126, 0.7152, 0.0722));
    albedo = lerp(luminance.xxx, albedo, saturation);
    albedo *= tint;
    albedo = saturate((albedo - 0.5) * brightnessContrast.g + 0.5);
    albedo = saturate(albedo + brightnessContrast.r);
}

// Stochastic sampling as on the terrain surface (LoogaTerrainSurface.hlsl), with the settings of the terrain: a
// triangle grid in UV space gives each cell a random UV offset, and three samples blend over each triangle.
float2 LoogaGroundStochasticHash(float2 cell)
{
    return frac(sin(mul(float2x2(127.1, 311.7, 269.5, 183.3), cell)) * 43758.5453);
}

void LoogaGroundStochasticUVs(float2 uv, out float2 uv0, out float2 uv1, out float2 uv2, out half3 weights)
{
    float2 grid = uv * (3.4641016 * _GroundStochastic.y);
    float2 skewed = mul(float2x2(1.0, 0.0, -0.57735027, 1.15470054), grid);
    float2 cell = floor(skewed);
    float3 local = float3(frac(skewed), 0);
    local.z = 1.0 - local.x - local.y;
    float2 vertex0;
    float2 vertex1;
    float2 vertex2;
    if (local.z > 0.0)
    {
        weights = half3(local.z, local.y, local.x);
        vertex0 = cell;
        vertex1 = cell + float2(0, 1);
        vertex2 = cell + float2(1, 0);
    }
    else
    {
        weights = half3(-local.z, 1.0 - local.y, 1.0 - local.x);
        vertex0 = cell + float2(1, 1);
        vertex1 = cell + float2(1, 0);
        vertex2 = cell + float2(0, 1);
    }
    uv0 = uv + LoogaGroundStochasticHash(vertex0);
    uv1 = uv + LoogaGroundStochasticHash(vertex1);
    uv2 = uv + LoogaGroundStochasticHash(vertex2);
    half contrast = (half)_GroundStochastic.z;
    half floorWeight = min(0.0h, contrast - 0.45h);
    weights = saturate(lerp(floorWeight.xxx, 1.0h, weights));
}

// The higher texture features win at the cell edges, as on the terrain.
half3 LoogaGroundStochasticBlend(half3 weights, half3 heights, half contrast)
{
    half3 weighted = weights * (heights + 1.0h / 1024.0h);
    half maximum = max(weighted.x, max(weighted.y, weighted.z));
    half transition = max(contrast * maximum, 1e-4h);
    weighted = saturate((weighted - (maximum - transition)) / transition);
    return weighted / max(weighted.x + weighted.y + weighted.z, 1e-4h);
}

// One layer of a Looga terrain surface, with the projections, layer UV, rotation, colour adjustment and normal
// strength of the terrain surface (LoogaTerrainSurfaceLayers.hlsl). The normals add as surface gradients on the
// terrain projection axes. The occlusion has the occlusion strength of the layer, as on the terrain. A layer with
// stochastic sampling on the terrain gets it here too, so the ground has no visible repetition.
void LoogaGroundSampleSurfaceLayer(int layer, float3 position, float3 positionDx, float3 positionDy, half3 weights,
    half3 axisSign, half3 normal, out half3 albedo, out half3 normalWS, out half smoothness, out half occlusion)
{
    float4 uvRow = LoogaGroundLayerProperty(layer, LOOGA_LAYER_ROW_UV);
    float4 colorRow = LoogaGroundLayerProperty(layer, LOOGA_LAYER_ROW_COLOR);
    float4 adjustRow = LoogaGroundLayerProperty(layer, LOOGA_LAYER_ROW_ADJUST);
    float4 materialRow = LoogaGroundLayerProperty(layer, LOOGA_LAYER_ROW_MATERIAL);
    float2x2 rotation = LoogaLayerRotation(adjustRow.z);
    bool quality = _GroundLayout > 1.5;
    bool stochastic = _GroundStochastic.x > 0.5 && adjustRow.w > 0.5;
    albedo = 0;
    smoothness = 0;
    occlusion = 0;
    float3 gradient = 0;
    [unroll]
    for (int projection = 0; projection < 3; projection++)
    {
        half weight = weights[projection];
        [branch]
        if (weight > 0.001)
        {
            float2 uv;
            float2 dx;
            float2 dy;
            float3 tangent;
            float3 bitangent;
            GetLoogaSurfaceLayerUV(position, positionDx, positionDy, projection, axisSign[projection], _GroundSurfaceUV,
                uvRow, rotation, uv, dx, dy, tangent, bitangent);
            float2 sampleUV[3] = { uv, uv, uv };
            half3 sampleWeights = half3(1, 0, 0);
            half4 albedoSample;
            [branch]
            if (stochastic)
            {
                half3 cellWeights;
                LoogaGroundStochasticUVs(uv, sampleUV[0], sampleUV[1], sampleUV[2], cellWeights);
                half4 sample0 = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundAlbedoArray, sampler_GroundAlbedoArray, sampleUV[0],
                    layer, dx, dy);
                half4 sample1 = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundAlbedoArray, sampler_GroundAlbedoArray, sampleUV[1],
                    layer, dx, dy);
                half4 sample2 = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundAlbedoArray, sampler_GroundAlbedoArray, sampleUV[2],
                    layer, dx, dy);
                sampleWeights = LoogaGroundStochasticBlend(cellWeights, half3(sample0.a, sample1.a, sample2.a),
                    (half)_GroundStochastic.z);
                albedoSample = sample0 * sampleWeights.x + sample1 * sampleWeights.y + sample2 * sampleWeights.z;
            }
            else
            {
                albedoSample = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundAlbedoArray, sampler_GroundAlbedoArray, uv, layer,
                    dx, dy);
            }
            albedo += albedoSample.rgb * weight;
            [unroll]
            for (int index = 0; index < 3; index++)
            {
                half sampleWeight = sampleWeights[index] * weight;
                [branch]
                if (sampleWeight > 0.001)
                {
                    half4 normalSample = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundNormalArray, sampler_GroundNormalArray,
                        sampleUV[index], layer, dx, dy);
                    half4 maskSample = quality
                        ? SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundMaskArray, sampler_GroundNormalArray, sampleUV[index],
                            layer, dx, dy)
                        : 0;
                    half2 normalXY;
                    half layerSmoothness;
                    half layerOcclusion;
                    half metallic;
                    DecodeLoogaSurfaceMaterial(normalSample, maskSample, quality, normalXY, layerSmoothness,
                        layerOcclusion, metallic);
                    occlusion += pow(max(layerOcclusion, 1e-4h), (half)materialRow.z) * sampleWeight;
                    normalXY *= _GroundNormalStrength * (half)materialRow.x;
                    float2 derivative = -normalXY / max(sqrt(saturate(1.0 - dot(normalXY, normalXY))), 1.0 / 128.0);
                    gradient += (derivative.x * tangent + derivative.y * bitangent) * sampleWeight;
                    smoothness += saturate(layerSmoothness + (half)materialRow.y) * sampleWeight;
                }
            }
        }
    }
    gradient -= dot(normal, gradient) * normal;
    normalWS = normalize(normal - gradient);
    albedo = AdjustLoogaSurfaceLayerColor(albedo, colorRow, adjustRow, _GroundLuminance.rgb);
}

// Terrain ground at a world position: the four strongest splat layers, blended by their weights. The layers come
// from the splat maps at the control position (world XZ), and the textures are sampled at the position. A mesh that
// lies on a painted area of the terrain can read the layers beside that area without a stretched texture.
// coverStrength is the world colour (x) and ground cover (y) strength and the mean albedo luminance (z) of the layers,
// 0 for the MicroSplat layout.
// occlusion is the texture occlusion of the layers, as the terrain applies it, 1 for the MicroSplat layout.
// objectDust is the object dust of the layers (0 to 1): how much dust the ground leaves on objects. It is 0 for the
// MicroSplat layout, which has no per-layer value.
void LoogaSampleTerrainGround(float3 position, float2 controlPosition, float3 positionDx, float3 positionDy,
    half3 normal, out half3 albedo, out half3 normalWS, out half smoothness, out half3 coverStrength,
    out half occlusion, out half objectDust)
{
    float2 tileUv = saturate((controlPosition - _GroundTile.xy) * _GroundTile.zw);
    int chosen[4] = { 0, 0, 0, 0 };
    half chosenWeight[4] = { 0, 0, 0, 0 };
    [branch]
    if (_GroundControlPacked.x > 0)
    {
        // Mip 0 of the packed maps, as the control maps below.
        float2 packedSize = max(_GroundControlPacked.zw, 2.0);
        float2 packedUv = (tileUv * (packedSize - 1.0) + 0.5) / packedSize;
        LoogaPackedFootprint footprint = LoadLoogaPackedFootprint(
            TEXTURE2D_ARGS(_GroundControlIndex, sampler_PointClamp),
            TEXTURE2D_ARGS(_GroundControlWeight, sampler_PointClamp), packedUv, packedSize, 0);
        int4 packedIndices;
        float4 packedWeights;
        SelectLoogaPackedLayers(footprint, packedIndices, packedWeights);
        [unroll]
        for (int slot = 0; slot < 4; slot++)
        {
            chosen[slot] = packedIndices[slot];
            chosenWeight[slot] = (half)packedWeights[slot];
        }
    }
    else
    {
        float resolution = max(_GroundControlResolution, 2.0);
        float2 splatUv = (tileUv * (resolution - 1.0) + 0.5) / resolution;
        half4 control[8];
        control[0] = SAMPLE_TEXTURE2D_LOD(_GroundControl0, sampler_LinearClamp, splatUv, 0);
        control[1] = SAMPLE_TEXTURE2D_LOD(_GroundControl1, sampler_LinearClamp, splatUv, 0);
        control[2] = SAMPLE_TEXTURE2D_LOD(_GroundControl2, sampler_LinearClamp, splatUv, 0);
        control[3] = SAMPLE_TEXTURE2D_LOD(_GroundControl3, sampler_LinearClamp, splatUv, 0);
        control[4] = 0;
        control[5] = 0;
        control[6] = 0;
        control[7] = 0;
        // Terrains with more than 16 layers have more control maps. Most have 16 or fewer, so they skip the reads.
        [branch]
        if (_GroundLayerCount > 16.5)
        {
            control[4] = SAMPLE_TEXTURE2D_LOD(_GroundControl4, sampler_LinearClamp, splatUv, 0);
            control[5] = SAMPLE_TEXTURE2D_LOD(_GroundControl5, sampler_LinearClamp, splatUv, 0);
            control[6] = SAMPLE_TEXTURE2D_LOD(_GroundControl6, sampler_LinearClamp, splatUv, 0);
            control[7] = SAMPLE_TEXTURE2D_LOD(_GroundControl7, sampler_LinearClamp, splatUv, 0);
        }

        int layers = (int)min(_GroundLayerCount, 32.0);
        [unroll]
        for (int pick = 0; pick < 4; pick++)
        {
            half best = -1;
            int bestLayer = 0;
            for (int layer = 0; layer < layers; layer++)
            {
                half weight = control[layer >> 2][layer & 3];
                bool taken = false;
                [unroll]
                for (int previous = 0; previous < pick; previous++)
                {
                    taken = taken || chosen[previous] == layer;
                }
                if (!taken && weight > best)
                {
                    best = weight;
                    bestLayer = layer;
                }
            }
            chosen[pick] = bestLayer;
            chosenWeight[pick] = max(best, 0);
        }
    }

    half total = max(chosenWeight[0] + chosenWeight[1] + chosenWeight[2] + chosenWeight[3], 1e-4);

    half3 weights = pow(abs(normal), max(_GroundTriplanarContrast, 1.0));
    weights /= max(weights.x + weights.y + weights.z, 1e-4);
    half3 axisSign = normal < 0 ? -1 : 1;
    albedo = 0;
    normalWS = 0;
    smoothness = 0;
    coverStrength = 0;
    occlusion = 0;
    objectDust = 0;
    [unroll]
    for (int index = 0; index < 4; index++)
    {
        half weight = chosenWeight[index] / total;
        [branch]
        if (weight > 0.001)
        {
            half3 layerAlbedo;
            half3 layerNormal;
            half layerSmoothness;
            half layerOcclusion = 1;
            if (_GroundLayout > 0.5)
            {
                coverStrength += (half3)LoogaGroundLayerProperty(chosen[index], LOOGA_LAYER_ROW_COVER).xyz * weight;
                objectDust += LoogaGroundLayerProperty(chosen[index], LOOGA_LAYER_ROW_OBJECT).x * weight;
                LoogaGroundSampleSurfaceLayer(chosen[index], position, positionDx, positionDy, weights, axisSign,
                    normal, layerAlbedo, layerNormal, layerSmoothness, layerOcclusion);
            }
            else
            {
                LoogaGroundSampleTerrainLayer(chosen[index], position, positionDx, positionDy, weights, axisSign,
                    normal, layerAlbedo, layerNormal, layerSmoothness);
            }
            albedo += layerAlbedo * weight;
            normalWS += layerNormal * weight;
            smoothness += layerSmoothness * weight;
            occlusion += layerOcclusion * weight;
        }
    }
    normalWS = normalize(normalWS + normal * 1e-3);

#ifdef LOOGA_TERRAIN_GROUND_HOOK
    half3 hookNormal = half3(0, 0, 1);
    LOOGA_TERRAIN_GROUND_HOOK(position, albedo, hookNormal, smoothness);
#endif
}

// Terrain ground at a world position, with the splat layers at the control position and no object dust.
void LoogaSampleTerrainGround(float3 position, float2 controlPosition, float3 positionDx, float3 positionDy,
    half3 normal, out half3 albedo, out half3 normalWS, out half smoothness, out half3 coverStrength,
    out half occlusion)
{
    half objectDust;
    LoogaSampleTerrainGround(position, controlPosition, positionDx, positionDy, normal, albedo, normalWS, smoothness,
        coverStrength, occlusion, objectDust);
}

// Terrain ground at a world position, with the splat layers at the control position and no texture occlusion.
void LoogaSampleTerrainGround(float3 position, float2 controlPosition, float3 positionDx, float3 positionDy,
    half3 normal, out half3 albedo, out half3 normalWS, out half smoothness, out half3 coverStrength)
{
    half occlusion;
    LoogaSampleTerrainGround(position, controlPosition, positionDx, positionDy, normal, albedo, normalWS, smoothness,
        coverStrength, occlusion);
}

// Terrain ground at a world position, with the splat layers at the control position.
void LoogaSampleTerrainGround(float3 position, float2 controlPosition, float3 positionDx, float3 positionDy,
    half3 normal, out half3 albedo, out half3 normalWS, out half smoothness)
{
    half3 coverStrength;
    LoogaSampleTerrainGround(position, controlPosition, positionDx, positionDy, normal, albedo, normalWS, smoothness,
        coverStrength);
}

// Terrain ground at a world position, with the splat layers at the same position.
void LoogaSampleTerrainGround(float3 position, float3 positionDx, float3 positionDy, half3 normal,
    out half3 albedo, out half3 normalWS, out half smoothness)
{
    LoogaSampleTerrainGround(position, position.xz, positionDx, positionDy, normal, albedo, normalWS, smoothness);
}

#endif
