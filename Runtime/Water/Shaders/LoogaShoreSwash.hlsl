#ifndef LOOGA_SHORE_SWASH_INCLUDED
#define LOOGA_SHORE_SWASH_INCLUDED

// Swash at the water edge, which the water shader and the terrain surface share. Looga/Water lifts the water at the
// shore with it, and the terrain surface wets the ground with it, so the wet sheen comes where the water drains away.

// Wave time of LoogaOceanWaves in seconds.
float _LoogaOceanTime;
// Set by the Looga water body with a swash run-up: x = water level, y = Swash Run-Up Height, z = Shore Wave Period,
// w = 1 when on.
float4 _LoogaSwash;
// Shore texture of that body (R = signed distance to the water edge in meters), its shore wave field (A = fade where
// the waves of two shores meet) and their rectangle (xy = origin, zw = 1 / size).
float4 _LoogaSwashShoreRect;
TEXTURE2D(_LoogaSwashShoreMap);
SAMPLER(sampler_LoogaSwashShoreMap);
TEXTURE2D(_LoogaSwashWaveMap);
// Set by LoogaOceanWaves for each camera: xy = world XZ centre of the near grid, z = radius in meters,
// w = 1 while a near grid draws.
float4 _LoogaOceanGrid;

// Cubic B-spline sample of a texture from four bilinear samples (GPU Gems 2, chapter 20). The shore textures have
// texels of several meters, and the contours of a bilinear sample are straight in each texel, so a water edge or a foam
// band that follows them shows the texel polygons. The cubic sample gives smooth curves.
float4 LoogaSampleCubic(TEXTURE2D_PARAM(textureName, samplerName), float2 uv)
{
    float width;
    float height;
    textureName.GetDimensions(width, height);
    float2 size = float2(width, height);
    float2 position = uv * size - 0.5;
    float2 f = frac(position);
    float2 cell = position - f;
    float2 f2 = f * f;
    float2 f3 = f2 * f;
    float2 w0 = (1.0 - 3.0 * f + 3.0 * f2 - f3) / 6.0;
    float2 w1 = (4.0 - 6.0 * f2 + 3.0 * f3) / 6.0;
    float2 w2 = (1.0 + 3.0 * f + 3.0 * f2 - 3.0 * f3) / 6.0;
    float2 w3 = f3 / 6.0;
    float2 g0 = w0 + w1;
    float2 g1 = w2 + w3;
    float2 uv0 = (cell - 0.5 + w1 / g0) / size;
    float2 uv1 = (cell + 1.5 + w3 / g1) / size;
    return g0.y * (g0.x * SAMPLE_TEXTURE2D_LOD(textureName, samplerName, float2(uv0.x, uv0.y), 0) +
            g1.x * SAMPLE_TEXTURE2D_LOD(textureName, samplerName, float2(uv1.x, uv0.y), 0)) +
        g1.y * (g0.x * SAMPLE_TEXTURE2D_LOD(textureName, samplerName, float2(uv0.x, uv1.y), 0) +
            g1.x * SAMPLE_TEXTURE2D_LOD(textureName, samplerName, float2(uv1.x, uv1.y), 0));
}

// The run-up starts where the near grid of the ocean starts to draw over dry ground, in meters from the water edge.
#define LOOGA_SWASH_DRY_MARGIN 12.0

// Slow change of the shore wave phase along the shore, in radians (about -1.9 to 1.9). The sines are turned against
// the world axes and have unrelated lengths, so the pattern does not repeat in view.
float LoogaShoreWavePhaseShift(float2 grid)
{
    float2 turned = float2(0.267 * grid.x + 0.964 * grid.y, -0.964 * grid.x + 0.267 * grid.y);
    float value = sin(turned.x * 0.0331 + 1.3) * sin(turned.y * 0.0267 + 0.4) +
        0.5 * sin(dot(grid, float2(0.0113, -0.0079)) + 2.0);
    return value * 1.27;
}

// Phase of the shore wave at the water edge, in radians. A crest is at the edge at phase 0. The time includes the
// arrival time of the shore wave field at the edge.
float LoogaSwashEdgePhase(float2 grid, float time, float period)
{
    return 2.0 * PI / max(period, 0.1) * time + LoogaShoreWavePhaseShift(grid);
}

// Height of a wave in its group of five, from 0.45 to 1: a set of large waves, then smaller ones.
float LoogaSwashGroup(float phase)
{
    return lerp(0.45, 1.0, smoothstep(0.15, 0.85, 0.5 + 0.5 * cos(phase / 5.0)));
}

// Share of the full run-up at a time in the wave cycle (0 to 1 after a crest reaches the edge). The water runs up the
// beach over 30 % of the period and drains back by 95 %.
float LoogaSwashRunUp(float age)
{
    return smoothstep(0.0, 0.3, age) * (1.0 - smoothstep(0.3, 0.95, age));
}

// Hash of a 2D cell to 0 to 1 (Dave Hoskins, hash12).
float LoogaSwashHash(float2 cell)
{
    float3 p = frac(float3(cell.xyx) * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

// Smooth value noise from 0 to 1 with a cell of 1 unit.
float LoogaSwashNoise(float2 position)
{
    float2 cell = floor(position);
    float2 f = position - cell;
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = LoogaSwashHash(cell);
    float b = LoogaSwashHash(cell + float2(1.0, 0.0));
    float c = LoogaSwashHash(cell + float2(0.0, 1.0));
    float d = LoogaSwashHash(cell + float2(1.0, 1.0));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// Run-up of one wave along the shore, from 0.55 to 1.25 of the mean. A swash runs up in rounded tongues of different
// lengths, so its front curves even on a straight beach. Each wave (index of the cycle) has its own tongues.
float LoogaSwashTongues(float2 grid, float wave)
{
    float2 offset = float2(wave * 17.31, wave * -11.07);
    float tongues = LoogaSwashNoise(grid / 7.0 + offset) * 0.65 + LoogaSwashNoise(grid / 3.1 - offset * 1.3) * 0.35;
    return lerp(0.55, 1.25, tongues);
}

// Height of one wave at the shore as a share of the run-up height: its group height times its tongues. Both stay the
// same for the whole cycle of the wave (the group height at its highest run-up), so the end of one cycle meets the
// start of the next without a step in the wet ground.
float LoogaSwashWaveScale(float2 grid, float phase)
{
    float wave = floor(phase / (2.0 * PI));
    return LoogaSwashGroup((wave + 0.3) * 2.0 * PI) * LoogaSwashTongues(grid, wave);
}

// Weight of the run-up from the fade of the shore wave field. Where the waves of two shores meet, the run-up keeps
// a part of its height.
float LoogaSwashFieldWeight(float fieldFade)
{
    return lerp(0.4, 1.0, fieldFade);
}

// The run-up is only on the near grid. It fades out before the edge of the grid.
float LoogaSwashGridFade(float2 grid)
{
    if (_LoogaOceanGrid.w <= 0.0) return 0.0;
    return 1.0 - smoothstep(0.7, 0.9, distance(grid, _LoogaOceanGrid.xy) / max(_LoogaOceanGrid.z, 1.0));
}

// Weight of the run-up from the signed distance to the water edge: full on the beach next to the water, none past
// the dry margin.
float LoogaSwashDryZone(float distance)
{
    return smoothstep(-LOOGA_SWASH_DRY_MARGIN, -0.5 * LOOGA_SWASH_DRY_MARGIN, distance);
}

// Wet sheen of ground at a share of the run-up height, a time in the cycle (above 1 for the wave before) after the
// crest. The backwash of the wave leaves the ground when its run-up falls to the share.
float LoogaSwashDrainedSheen(float share, float age)
{
    if (share >= 1.0) return 0.0;
    // Inverse of the backwash smoothstep: the time in the cycle at which the water leaves this height.
    float fall = saturate(1.0 - share);
    float t = 0.5 - sin(asin(1.0 - 2.0 * fall) / 3.0);
    float drained = 0.3 + 0.65 * t;
    if (age < drained) return 0.0;
    // The sheen fades in below the highest point of this run-up. Ground above it gets the steady sheen of
    // LoogaShoreWetness, not a sheen that starts at once along the whole band when the water turns back.
    return exp(-(age - drained) * _LoogaSwash.z / 2.5) * saturate((1.0 - share) / 0.25);
}

// Wetness of the ground at the shore. x = damp ground below the run-up limit and under the water (0 to 1). y = wet sheen
// where the swash has just drained (0 to 1). It fades over a few seconds after the water leaves.
float2 LoogaShoreWetness(float3 positionWS)
{
    if (_LoogaSwash.w <= 0.0) return 0.0;
    float2 uv = (positionWS.xz - _LoogaSwashShoreRect.xy) * _LoogaSwashShoreRect.zw;
    if (any(uv <= 0.0) || any(uv >= 1.0)) return 0.0;
    float distance = SAMPLE_TEXTURE2D_LOD(_LoogaSwashShoreMap, sampler_LoogaSwashShoreMap, uv, 0).r;
    float zone = LoogaSwashDryZone(distance);
    if (zone <= 0.0) return 0.0;

    float height = positionWS.y - _LoogaSwash.x;
    // The same weights as the run-up of the water, so the wet ground meets the water edge.
    // On the dry margin the wave field holds the arrival time of the crests at the water edge (R).
    float4 field = SAMPLE_TEXTURE2D_LOD(_LoogaSwashWaveMap, sampler_LoogaSwashShoreMap, uv, 0);
    float reach = max(_LoogaSwash.y * zone * LoogaSwashFieldWeight(field.a), 1e-3);
    // The ground stays damp up to the highest run-up and dries above it.
    float damp = 1.0 - smoothstep(0.8, 2.0, height / reach);
    // Deeper ground keeps its own colour under the water tint.
    damp *= smoothstep(-0.6, -0.3, height);

    // Height of the current wave as a share of its run-up: the water is at share LoogaSwashRunUp(age) now.
    float phase = LoogaSwashEdgePhase(positionWS.xz, _LoogaOceanTime + field.r, _LoogaSwash.z);
    float age = frac(phase / (2.0 * PI));
    float liveReach = reach * LoogaSwashGridFade(positionWS.xz);
    float share = height / max(liveReach * LoogaSwashWaveScale(positionWS.xz, phase), 1e-3);
    // Ground under the swash is wet. While the water runs up, the sand under the thin front soaks over a wide band, so
    // the front has no dark edge. On the backwash the ground under the water is fully wet up to the water edge, where
    // the drained sheen starts at full strength, so there is no less wet strip along the receding edge.
    float backwash = smoothstep(0.25, 0.35, age);
    float soak = lerp(0.3, 0.005, backwash);
    // On the backwash the full wetness reaches the water edge (the soak band lies above it), where the drained sheen
    // starts at full strength, so no thin less wet line forms between them.
    float covered = saturate((LoogaSwashRunUp(age) - share) / soak + backwash);
    // Sheen where the backwash of this wave, or of the wave before it, has left the ground.
    float drainedSheen = LoogaSwashDrainedSheen(share, age);
    float previousShare = height / max(liveReach * LoogaSwashWaveScale(positionWS.xz, phase - 2.0 * PI), 1e-3);
    drainedSheen = max(drainedSheen, LoogaSwashDrainedSheen(previousShare, age + 1.0));
    // Ground below the still water level is always under water, so it has no sheen.
    // A steady sheen from the waves before reaches past the highest run-up and fades out far above it, so the wet
    // ground does not end at a line where the water stopped. It does not change with the waves.
    float steadyShare = height / max(liveReach, 1e-3);
    float steadySheen = 0.4 * (1.0 - smoothstep(0.5, 1.8, steadyShare));
    float sheen = max(max(covered, drainedSheen), steadySheen) * saturate(share * 10.0);
    return float2(damp, sheen) * zone;
}

#endif
