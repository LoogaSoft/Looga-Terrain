#ifndef LOOGA_WATER_WAVES_INCLUDED
#define LOOGA_WATER_WAVES_INCLUDED

// Gerstner waves (GPU Gems 1, chapter 1). Each wave is float4(direction x, direction z, steepness, wavelength).
// The steepness of all waves together must stay below 1, or the crests fold over. The weight (0 to 1) fades a wave
// that the mesh or the pixels cannot resolve, so it does not alias into a regular pattern.
#define LOOGA_WATER_GRAVITY 9.81

void LoogaGerstnerWave(float4 wave, float2 position, float time, float scale, float weight, inout float3 offset,
    inout float3 tangent, inout float3 binormal, inout float crest)
{
    float steepness = wave.z * scale * weight;
    float k = 6.2831853 / max(wave.w, 0.01);
    // Deep water dispersion: the wave speed follows the wavelength.
    float speed = sqrt(LOOGA_WATER_GRAVITY / k);
    float2 direction = normalize(wave.xy + 1e-5);
    float phase = k * (dot(direction, position) - speed * time);
    float amplitude = steepness / k;
    float s = sin(phase);
    float c = cos(phase);
    offset += float3(direction.x * amplitude * c, amplitude * s, direction.y * amplitude * c);
    tangent += float3(-direction.x * direction.x * steepness * s, direction.x * steepness * c,
        -direction.x * direction.y * steepness * s);
    binormal += float3(-direction.x * direction.y * steepness * s, direction.y * steepness * c,
        -direction.y * direction.y * steepness * s);
    crest += steepness * s;
}

// Weight of a wave of this length on a mesh with this vertex spacing: 1 from three vertices per wave, 0 below two.
float LoogaWaveMeshWeight(float wavelength, float vertexSpacing)
{
    return saturate(wavelength / max(vertexSpacing, 1e-4) - 2);
}

// Weight of a wave of this length on pixels of this world size: 1 from eight pixels per wave, 0 below four.
float LoogaWavePixelWeight(float wavelength, float pixelSize)
{
    return saturate(wavelength / max(4 * pixelSize, 1e-4) - 1);
}

#endif
