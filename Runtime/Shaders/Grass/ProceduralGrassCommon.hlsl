#ifndef LOOGA_PROCEDURAL_GRASS_COMMON_INCLUDED
#define LOOGA_PROCEDURAL_GRASS_COMMON_INCLUDED

// Data of one grass type. Keep it in sync with ProceduralGrassType.GpuData.
struct GrassType
{
    float4 shape;   // Shortest length, longest length, root width, coverage for the full density.
    float4 bend;    // Lowest tilt, highest tilt, tip droop (radians), density share of the lattice.
    float4 clump;   // Clump size, clump height variation, clump facing share, pull to the clump centre.
    float4 base;    // Linear base color, smoothness.
    float4 tint;    // Tint of the second tip color, transmission strength.
    float4 wind;    // Wind lean (radians), sway frequency, clump shade, root occlusion.
    float4 normal;  // Upward normal blend, edge roundness, wind alignment, world color map brightness.
    float4 shade;   // Root shade, tip shade, saturation, world color map share.
    float4 detail;  // Largest twist (radians), smoothness variation, canopy shadow density, sparse clumping.
    float4 patches; // Patch length difference, 1 / patch size (1 / meters), unused, unused.
};

// One placed blade, 64 bytes. ProceduralGrassRenderer.BladeStride must match.
struct GrassBlade
{
    float4 root;    // World root position, blade length.
    float4 shape;   // Facing direction (XZ), width, tilt (radians).
    float4 data;    // Tip droop (radians), hash, type index and flags (as uint bits), clump value from -1 to 1.
    float4 ground;  // Ground normal X and Z, packed world color (GrassPackColor), fullness of the coverage.
};

// Bits of GrassBlade.data.z: the type index, and a flag for a blade on the ground of a mesh (GrassGround).
#define GRASS_TYPE_MASK 0xFFu
#define GRASS_ON_MESH_GROUND 0x100u

// Packs a linear color from 0 to 1 into 3 x 10 bits with a square root curve. Bit 30 marks a valid color.
uint GrassPackColor(float3 color)
{
    uint3 bits = (uint3)round(sqrt(saturate(color)) * 1023.0);
    return bits.r | (bits.g << 10) | (bits.b << 20) | (1u << 30);
}

// Returns the color in RGB and 1 in A for a valid packed color, else 0.
float4 GrassUnpackColor(uint packed)
{
    float3 color = float3(packed & 1023u, (packed >> 10) & 1023u, (packed >> 20) & 1023u) / 1023.0;
    return float4(color * color, (packed >> 30) & 1u);
}

// PCG hash (Jarzynski and Olano 2020).
uint GrassHash(uint value)
{
    uint state = value * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// Returns a value in [0, 1) and advances the state.
float GrassRandom(inout uint state)
{
    state = GrassHash(state);
    return state * (1.0 / 4294967296.0);
}

#endif
