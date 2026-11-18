#ifndef LOOGA_CAVE_ENTRANCE_CLIP_INCLUDED
#define LOOGA_CAVE_ENTRANCE_CLIP_INCLUDED

// Cave entrance boxes (LoogaCaveEntrance). An entrance draws one mesh for the ground, the rock and the cave in its box.
// The rock meshes and the terrain covers of other builders end in the box, so they do not draw over that mesh. Global
// values that LoogaCaveEntrance sets: world to box space (box centre at 0, meters), the half size of the box (xyz) with
// the width of the ragged edge band (w), the world centre (xyz) and radius (w) of each box, the world bounds of all
// boxes, and the number of boxes. Most surfaces are outside the bounds and skip the boxes at once.
#define LOOGA_CAVE_ENTRANCE_MAX 16
float4x4 _LoogaCaveEntranceWorldToBox[LOOGA_CAVE_ENTRANCE_MAX];
float4 _LoogaCaveEntranceSize[LOOGA_CAVE_ENTRANCE_MAX];
float4 _LoogaCaveEntranceSphere[LOOGA_CAVE_ENTRANCE_MAX];
float4 _LoogaCaveEntranceMin;
float4 _LoogaCaveEntranceMax;
float _LoogaCaveEntranceCount;

// Smooth value noise in [0, 1].
float LoogaCaveEntranceNoise(float3 p)
{
    float3 cell = floor(p);
    float3 f = p - cell;
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

// Cuts away the pixels of a surface inside a cave entrance box. The surface ends along a ragged edge in the band inside
// the box sides. The noise is fixed in the world, so all passes cut the same pixels and the edge does not move.
void ClipLoogaCaveEntrances(float3 positionWS)
{
    [branch]
    if (any(positionWS < _LoogaCaveEntranceMin.xyz) || any(positionWS > _LoogaCaveEntranceMax.xyz)) return;
    int count = (int)_LoogaCaveEntranceCount;
    [loop]
    for (int index = 0; index < count; index++)
    {
        float3 toCentre = positionWS - _LoogaCaveEntranceSphere[index].xyz;
        if (dot(toCentre, toCentre) > _LoogaCaveEntranceSphere[index].w * _LoogaCaveEntranceSphere[index].w) continue;
        float3 local = mul(_LoogaCaveEntranceWorldToBox[index], float4(positionWS, 1.0)).xyz;
        float4 half = _LoogaCaveEntranceSize[index];
        // Distance into the box from its nearest side, in meters. Negative outside.
        float3 inside3 = half.xyz - abs(local);
        float inside = min(inside3.x, min(inside3.y, inside3.z));
        [branch]
        if (inside > 0.0)
        {
            float noise = 0.65 * LoogaCaveEntranceNoise(positionWS * 1.1) +
                          0.35 * LoogaCaveEntranceNoise(positionWS * 3.7 + 11.3);
            clip(noise * half.w - inside);
        }
    }
}

#endif
