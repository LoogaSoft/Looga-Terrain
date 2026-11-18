#ifndef LOOGA_DETAIL_PLACEMENT_INCLUDED
#define LOOGA_DETAIL_PLACEMENT_INCLUDED

// Deterministic detail distribution shared by every backend.
// GeneratedVegetationPageSource.Hash and Random must stay equal to these functions.

uint DetailHash(uint value)
{
    value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15;
    value *= 0x846ca68bu; return value ^ (value >> 16);
}

float DetailRandom(uint value)
{
    return (DetailHash(value) & 0x00ffffffu) * (1.0 / 16777216.0);
}

// Terrain UV of one instance of a detail texel. Two random values on each axis give a tent around the texel centre, one
// texel wide on each side, so the instance density is the bilinear filter of the detail map. A uniform spot in the texel
// is a box filter: a sharp patch edge then shows the texel grid. Positions past the terrain edge mirror back.
float2 DetailPosition(float2 texel, uint seed, float detailWidth)
{
    float2 offset = float2(DetailRandom(seed) + DetailRandom(seed + 5), DetailRandom(seed + 1) + DetailRandom(seed + 6));
    float2 uv = (texel + offset - 0.5) / detailWidth;
    return 1.0 - abs(1.0 - abs(uv));
}

#endif
