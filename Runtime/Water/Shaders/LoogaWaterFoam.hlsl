#ifndef LOOGA_WATER_FOAM_INCLUDED
#define LOOGA_WATER_FOAM_INCLUDED

// Foam functions that the water shader and the foam simulation share, so the simulated shore band is the same band
// that the shader draws without the simulation.

// UV of the large foam patches in the foam detail texture. Sample the clump channel (R) at mip 3.
float2 LoogaFoamPatchUV(float2 grid, float detailSize)
{
    float s, c;
    sincos(-0.52, s, c);
    return float2(c * grid.x - s * grid.y, s * grid.x + c * grid.y) / (detailSize * 11.0);
}

// Patch value from 0 to 1. A low mip of the clump channel is smooth, and its values spread evenly around 0.5.
float LoogaFoamPatches(float clumps)
{
    return saturate((clumps - 0.5) * 2.2 + 0.5);
}

// Shore band from 0 to 1 at a signed distance from the water edge, in metres. The patches change the width along the
// shore, and the pulse makes the band grow and shrink over time.
float LoogaShoreBand(float shoreDistance, float patches, float time, float width, float pulse)
{
    float wave = 1 + pulse * sin(time * 0.9 + patches * 12.0 - shoreDistance * 1.7);
    float bandWidth = max(width * lerp(0.45, 1.4, patches) * wave, 0.01);
    return smoothstep(0, 1, 1 - shoreDistance / bandWidth);
}

// Two random values from 0 to 1 for a cell (integer hash, pcg2d). The hash uses integers, so it stays exact far from
// the world origin.
float2 LoogaFoamHash(float2 cell)
{
    uint2 v = uint2(int2(cell)) * 1664525u + 1013904223u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v ^= v >> 16u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v ^= v >> 16u;
    return float2(v) * (1.0 / 4294967296.0);
}

// Two smooth value noises from 0 to 1 with a cell of 1 unit. The noise does not repeat, so foam shapes from it make
// no tile pattern.
float2 LoogaFoamNoise(float2 position)
{
    float2 cell = floor(position);
    float2 f = position - cell;
    float2 u = f * f * (3.0 - 2.0 * f);
    float2 a = LoogaFoamHash(cell);
    float2 b = LoogaFoamHash(cell + float2(1.0, 0.0));
    float2 c = LoogaFoamHash(cell + float2(0.0, 1.0));
    float2 d = LoogaFoamHash(cell + float2(1.0, 1.0));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// Value noise from -1 to 1 (x) and its gradient (yz), with a cell of 1 unit.
float3 LoogaBoilNoise(float2 position)
{
    float2 cell = floor(position);
    float2 f = position - cell;
    float2 u = f * f * (3.0 - 2.0 * f);
    float2 du = 6.0 * f * (1.0 - f);
    float a = LoogaFoamHash(cell).x;
    float b = LoogaFoamHash(cell + float2(1.0, 0.0)).x;
    float c = LoogaFoamHash(cell + float2(0.0, 1.0)).x;
    float d = LoogaFoamHash(cell + float2(1.0, 1.0)).x;
    float mixed = a - b - c + d;
    float value = a + (b - a) * u.x + (c - a) * u.y + mixed * u.x * u.y;
    float2 gradient = du * float2(b - a + mixed * u.y, c - a + mixed * u.x);
    return float3(value * 2.0 - 1.0, gradient * 2.0);
}

// One layer of boils: noise of a cell size in meters that changes into new noise over a period in seconds, so the
// bumps rise and fall in place. Returns the height (-1 to 1) and its gradient per meter. The blend keeps the range.
float3 LoogaBoilLayer(float2 grid, float time, float size, float period, float seed)
{
    float cycle = time / period + seed;
    float index = floor(cycle);
    float blend = smoothstep(0.0, 1.0, cycle - index);
    float2 position = grid / size;
    float3 a = LoogaBoilNoise(position + LoogaFoamHash(float2(index, seed * 7.0)) * 61.0);
    float3 b = LoogaBoilNoise(position + LoogaFoamHash(float2(index + 1.0, seed * 7.0)) * 61.0);
    float3 layer = lerp(a, b, blend) * rsqrt(blend * blend + (1.0 - blend) * (1.0 - blend));
    return float3(layer.x, layer.yz / size);
}

// Boils of fast water, such as the pool below a waterfall: x = height from about -1 to 1, yz = its gradient per meter
// in world XZ. Two layers of different sizes and rates. The water bodies and the fall sheets use the same boils, so a
// waterfall sheet that runs out onto the water moves with it.
float3 LoogaBoils(float2 grid, float time)
{
    return LoogaBoilLayer(grid, time, 3.2, 1.7, 0.31) * 0.65 + LoogaBoilLayer(grid, time, 1.3, 0.9, 0.77) * 0.35;
}

#endif
