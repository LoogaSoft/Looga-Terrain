#ifndef LOOGA_STAMP_NOISE_INCLUDED
#define LOOGA_STAMP_NOISE_INCLUDED

// Values must agree with StampNoiseType in StampFalloff.cs.
#define LOOGA_NOISE_NONE 0
#define LOOGA_NOISE_FBM 1
#define LOOGA_NOISE_TEXTURE 2
#define LOOGA_NOISE_WORLEY 3
#define LOOGA_NOISE_WORM_FBM 4
#define LOOGA_NOISE_PRECISE_FBM 5

// Gradient noise with the hash and octave constants of MicroVerse, so noise settings from MicroVerse content match.
// The hash multiplies three cell coordinates as floats. It loses precision beyond about 64 cells from the origin, and
// the noise then shows axis-aligned stripes and blocks (for example frequency 110 at 5 km from the origin).
float2 NoiseGradient(float2 cell)
{
    const float2 k = float2(0.3183099, 0.3678794);
    cell = cell * k + k.yx;
    return -1.0 + 2.0 * frac(16.0 * k * frac(cell.x * cell.y * (cell.x + cell.y)));
}

// Gradient of the precise FBM: an integer hash of the cell, exact at all cells. The length sqrt(2/3) gives the noise
// the variance of the MicroVerse gradients, so the same amplitude and balance give similar weights.
float2 PreciseNoiseGradient(float2 cell)
{
    uint2 q = (uint2)(int2)cell;
    uint h = q.x * 0x8da6b343u ^ q.y * 0xd8163841u;
    h ^= h >> 16;
    h *= 0x7feb352du;
    h ^= h >> 15;
    h *= 0x846ca68bu;
    h ^= h >> 16;
    float angle = (h & 0xffffu) * (6.2831853 / 65536.0);
    return 0.8164966 * float2(cos(angle), sin(angle));
}

float2 CellGradient(float2 cell, bool precise)
{
    return precise ? PreciseNoiseGradient(cell) : NoiseGradient(cell);
}

float GradientNoise(float2 position, bool precise)
{
    float2 cell = floor(position);
    float2 local = frac(position);
    float2 blend = local * local * (3.0 - 2.0 * local);
    float bottom = lerp(dot(CellGradient(cell, precise), local),
        dot(CellGradient(cell + float2(1.0, 0.0), precise), local - float2(1.0, 0.0)), blend.x);
    float top = lerp(dot(CellGradient(cell + float2(0.0, 1.0), precise), local - float2(0.0, 1.0)),
        dot(CellGradient(cell + float2(1.0, 1.0), precise), local - float2(1.0, 1.0)), blend.x);
    return lerp(bottom, top, blend.y);
}

float FractalNoise(float2 position, bool precise)
{
    float value = 0.5 * GradientNoise(position, precise);
    position *= 2.01;
    value += 0.33 * GradientNoise(position, precise);
    position *= 1.96;
    value += 0.17 * GradientNoise(position, precise);
    return value;
}

float FractalNoise(float2 position)
{
    return FractalNoise(position, false);
}

// Feature point of a Worley cell, in cell units. The hash constants are the MicroVerse constants.
float2 WorleyPoint(float2 cell)
{
    return frac(cos(mul(cell, float2x2(-64.2, 71.3, 81.4, -29.8))) * 8321.3);
}

// Square root of the distance to the nearest feature point of the 3 x 3 cells around the position. At most 1.
float WorleyNoise(float2 position)
{
    float2 cell = floor(position);
    float2 local = frac(position);
    float nearest = 1;
    for (int x = -1; x <= 1; x++)
    {
        for (int y = -1; y <= 1; y++)
        {
            float2 offset = float2(x, y);
            nearest = min(nearest, length(WorleyPoint(cell + offset) + offset - local));
        }
    }
    return sqrt(nearest);
}

// Direction of the sine wave of a worm noise cell.
float2 WormDirection(float2 cell)
{
    return sin(cell.x * cell.y + float2(0, 1.571));
}

// Sine waves with one direction for each cell corner, blended across the cell.
float WormNoise(float2 position)
{
    const float frequency = 6.0;
    float2 cell = floor(position) + 77;
    float2 blend = frac(position);
    blend = blend * blend * (3.0 - 2.0 * blend);
    float bottom = lerp(sin(frequency * dot(position, WormDirection(cell + float2(0, 0)))),
        sin(frequency * dot(position, WormDirection(cell + float2(1, 0)))), blend.x);
    float top = lerp(sin(frequency * dot(position, WormDirection(cell + float2(0, 1)))),
        sin(frequency * dot(position, WormDirection(cell + float2(1, 1)))), blend.x);
    return lerp(bottom, top, blend.y);
}

// Three octaves of worm noise. Each octave turns and scales the position.
float WormFractalNoise(float2 position)
{
    const float2x2 turn = float2x2(1.6, 1.2, -1.2, 1.6);
    float value = 0.5 * WormNoise(position);
    position = mul(position, turn);
    value += 0.33 * WormNoise(position);
    position = mul(position, turn);
    value += 0.17 * WormNoise(position);
    return value;
}

// (FBM(position * frequency + offset) - balance) * amplitude. Parameters: x frequency, y amplitude, z offset, w balance.
float NoiseValue(float2 position, float4 parameters)
{
    return (FractalNoise(position * parameters.x + parameters.z) - parameters.w) * parameters.y;
}

// Like NoiseValue, for the FBM, precise FBM, Worley and worm FBM noise types. Other types give 0.
float TypedNoiseValue(int type, float2 position, float4 parameters)
{
    // One return statement. Early returns gave "potentially uninitialized variable" warnings at the call sites.
    float2 input = position * parameters.x + parameters.z;
    float noise = parameters.w;
    if (type == LOOGA_NOISE_FBM)
    {
        noise = FractalNoise(input);
    }
    else if (type == LOOGA_NOISE_PRECISE_FBM)
    {
        noise = FractalNoise(input, true);
    }
    else if (type == LOOGA_NOISE_WORLEY)
    {
        noise = WorleyNoise(input);
    }
    else if (type == LOOGA_NOISE_WORM_FBM)
    {
        noise = WormFractalNoise(input);
    }
    return (noise - parameters.w) * parameters.y;
}

// True for the FBM and the precise FBM. The height, path and water filter noises support only these two types.
bool IsFbmNoise(int type)
{
    return type == LOOGA_NOISE_FBM || type == LOOGA_NOISE_PRECISE_FBM;
}

#endif
