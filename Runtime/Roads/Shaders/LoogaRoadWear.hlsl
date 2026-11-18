#ifndef LOOGA_ROAD_WEAR_INCLUDED
#define LOOGA_ROAD_WEAR_INCLUDED

// Wear of the Looga road shader, placed in road coordinates. LoogaRoadSlabs writes them into the second UV set of each
// slab: x = distance across from the center line (right positive), y = distance along the spline, z = half width and
// w = a random value of the spline, all in meters except w. On a pad or a junction patch z is zero and x is the
// distance inward from the rim. The vertex color (red) marks a road side that runs along a pad: it gets no edge wear.
//
// A road gets two wheel tracks and an oil stripe in each lane, an edge band where the ground creeps onto the road,
// block cracking on old stretches and fatigue (alligator) cracking in zones along the worn wheel tracks. A pad gets
// the edge band and both kinds of cracking.
//
// Fatigue cracking breaks the asphalt into blocks of 15 to 40 cm. The cracks between them are wide and dark, their
// edges ravel to a lighter, rough rim, each block tilts a little and some blocks sink. The crack grooves and the block
// tilts bend the normal through slopes that come from the crack shapes, not from screen derivatives of a height, so
// they stay still while the camera moves. Every line is antialiased with the size of a pixel on the ground, and a
// crack keeps at least one pixel of width far away (RoadCrack).

// The rim of a road slab lies this far inside the road edge (LoogaRoadSlabs.LipInset). A pad rim is at depth zero.
static const float RoadRimInset = 0.25;

struct RoadWear
{
    // Coverage of open cracks, of their ravelled rims, of tar seals and of repairs, from 0 to 1.
    half crack;
    half rim;
    half seal;
    half repair;
    // Age of the repair at this pixel relative to the road, from 0 (new) to 1.
    half repairAge;
    // Darkening of sunken blocks, from 0 to 1.
    half sunken;
    // Slope of the crack grooves and block tilts in world X and Z (height change per meter).
    float2 slope;
};

float RoadHash(float2 p)
{
    float3 p3 = frac(p.xyx * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// Smooth value noise along a line. The key selects an independent noise.
float RoadNoise(float x, float key)
{
    float cell = floor(x);
    float f = x - cell;
    f = f * f * (3.0 - 2.0 * f);
    return lerp(RoadHash(float2(cell, key)), RoadHash(float2(cell + 1.0, key)), f);
}

// The crack width along a path: it narrows and widens, and now and then fades to a hairline.
float RoadCrackWidth(float x, float key)
{
    return _CrackWidth * lerp(0.25, 1.4, RoadNoise(x / 0.9, key + 4.0));
}

// Antialiased coverage of a band of the given width at a distance from its center line.
half RoadBand(float distance, float width, float pixel)
{
    return half(saturate((0.5 * width - distance) / pixel + 0.5) * saturate(width / pixel));
}

// The size of a pixel on the ground: the larger change of world X or Z across one pixel. Footprint holds the change
// of world XZ across one pixel to the right (xy) and one pixel up (zw).
float RoadPixel(float4 footprint)
{
    return max(max(abs(footprint.x) + abs(footprint.z), abs(footprint.y) + abs(footprint.w)), 1e-4);
}

// The size of a pixel across a crack: the footprint along the crack normal. Screen derivatives of the offset are not
// used: they jump at the kinks of the warped cells and widen thin cracks in whole 2 x 2 pixel blocks.
float RoadCrackPixel(float4 footprint, float2 normal)
{
    return max(abs(dot(footprint.xy, normal)) + abs(dot(footprint.zw, normal)), 1e-4);
}

// The height of a V groove at a signed offset from its center: the depth at the center, zero at the width.
float RoadGrooveHeight(float offset, float width)
{
    return -_CrackDepth * saturate(1.0 - abs(offset) / max(width, 1e-5));
}

// Antialiased coverage of a crack at a signed offset from its center line. Adds the slope of its V groove: the
// height rises from the bottom at the center to the road surface at the full width, in the direction of increasing
// offset (normal, world XZ). The slope is the mean over one pixel (the height change across the pixel divided by its
// size), so the groove has no sharp normal steps that flicker.
// The antialiasing uses the size of a pixel across the crack, so a crack that runs away from the camera stays sharp.
// A crack narrower than a pixel is drawn one pixel wide, and its contrast falls by (width / pixel) ^ exponent; with
// _CrackDistance 0 the exponent is 1 (the true mean), with 1 it is 0.3. Spacing is the distance between parallel
// cracks: the widened crack stays below a fifth of it, so a dense crack network does not become a dark area.
// Pixel is the size of a pixel across the crack (RoadCrackPixel).
half RoadCrackAt(float offset, float width, float pixel, float2 normal, float spacing, inout float2 slope)
{
    float distance = abs(offset);
    float groove = (RoadGrooveHeight(offset + 0.5 * pixel, width) - RoadGrooveHeight(offset - 0.5 * pixel, width)) /
        pixel;
    slope += normal * groove;
    float visible = min(max(width, pixel), max(width, spacing * 0.2));
    float strength = width > 0.0 ? pow(saturate(width / visible), lerp(1.0, 0.3, _CrackDistance)) : 0.0;
    return half(saturate((0.5 * visible - distance) / pixel + 0.5) * saturate(visible / pixel) * strength);
}

// A crack whose offset is a distance in meters along the normal (world XZ).
half RoadCrack(float offset, float width, float4 footprint, float2 normal, float spacing, inout float2 slope)
{
    return RoadCrackAt(offset, width, RoadCrackPixel(footprint, normal), normal, spacing, slope);
}

// The nearest edge of a Voronoi cell: the distance to it and its normal (both in cell units), and the cell that holds
// the position. The normal points away from that cell.
void RoadCellEdge(float2 position, out float distance, out float2 normal, out float2 cellId)
{
    float2 cell = floor(position);
    float2 local = position - cell;
    float2 nearestPoint = 0.0;
    float nearest = 8.0;
    cellId = cell;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float2 toPoint = float2(x, y) + RoadCellOffset(cell + float2(x, y)) - local;
            float d = dot(toPoint, toPoint);
            nearestPoint = d < nearest ? toPoint : nearestPoint;
            cellId = d < nearest ? cell + float2(x, y) : cellId;
            nearest = min(nearest, d);
        }
    }
    distance = 8.0;
    normal = float2(1.0, 0.0);
    [unroll]
    for (int j = -1; j <= 1; j++)
    {
        [unroll]
        for (int i = -1; i <= 1; i++)
        {
            float2 toPoint = float2(i, j) + RoadCellOffset(cell + float2(i, j)) - local;
            float2 between = toPoint - nearestPoint;
            float length2 = dot(between, between);
            float2 direction = between * rsqrt(max(length2, 1e-8));
            float edge = length2 > 1e-6 ? dot(0.5 * (nearestPoint + toPoint), direction) : 8.0;
            normal = edge < distance ? direction : normal;
            distance = min(distance, edge);
        }
    }
}

// The coverage of a crack network with cells of the given size. Where the cells are smaller than a few pixels, the
// lines cannot show, so the coverage moves to the mean of the network: the crack length per area of Voronoi cells
// (2 / size) times the width, made wider by _CrackDistance. The cracked area then keeps its tone far away.
half RoadNetworkCoverage(half lines, float width, float size, float pixel)
{
    float detail = saturate(size / (pixel * 4.0) - 0.5);
    float mean = saturate(2.0 * width / size * lerp(1.0, 1.8, _CrackDistance));
    return half(lerp(mean, lines, detail));
}

// The footprint of a pixel in a warped cell field, in meters: the change of the warped position (position / size
// plus the warp, in cell units) across one pixel, times the size. The warp can change several times faster than the
// position, so a crack in a compressed part of the field needs the larger antialiasing width. Without it the crack
// edges there are sharper than a pixel and flicker while the view moves.
float4 RoadWarpedFootprint(float4 footprint, float4 warpFootprint, float size)
{
    return footprint + warpFootprint * size;
}

// A crack network between warped Voronoi cells of the given size (meters). Returns the crack coverage and adds the
// groove slopes. Warp footprint is the change of the warp (cell units) across one pixel.
half RoadCellCracks(float2 positionXZ, float size, float2 warp, float4 warpFootprint, float width, float4 footprint,
    inout float2 slope)
{
    float cellDistance;
    float2 cellNormal;
    float2 cellId;
    RoadCellEdge(positionXZ / size + warp, cellDistance, cellNormal, cellId);
    half lines = RoadCrack(cellDistance * size, width, RoadWarpedFootprint(footprint, warpFootprint, size),
        -cellNormal, size, slope);
    return RoadNetworkCoverage(lines, width, size, RoadPixel(footprint));
}

// Rectangular repairs aligned with the road: one lane or the full width, 3 to 12 m long. Returns the coverage, the
// distance to the nearest repair edge and the age of the repair (0 to 1 of the road age).
half RoadRepairs(float across, float along, float halfWidth, float twoLanes, float key, float pixel,
    out float seamDistance, out half repairAge)
{
    half cover = 0.0h;
    seamDistance = 1e3;
    repairAge = 0.0h;
    [unroll]
    for (int k = -1; k <= 0; k++)
    {
        float cell = floor(along / _RepairSpacing) + k;
        float present = step(RoadHash(float2(cell, key + 11.0)), _RepairChance);
        float start = (cell + RoadHash(float2(cell, key + 12.0)) * 0.6) * _RepairSpacing;
        float length = lerp(3.0, 12.0, RoadHash(float2(cell, key + 13.0)));
        // A repair on a two-lane road covers one lane, or now and then the full width.
        float lane = twoLanes * step(0.3, RoadHash(float2(cell, key + 14.0)));
        float side = RoadHash(float2(cell, key + 15.0)) < 0.5 ? -1.0 : 1.0;
        float low = lane > 0.5 ? (side > 0.0 ? -0.25 : -halfWidth - 1.0) : -halfWidth - 1.0;
        float high = lane > 0.5 ? (side > 0.0 ? halfWidth + 1.0 : 0.25) : halfWidth + 1.0;
        float inside = min(min(along - start, start + length - along), min(across - low, high - across));
        half covered = half(saturate(inside / pixel + 0.5) * present);
        repairAge = covered > cover ? half(lerp(0.2, 0.75, RoadHash(float2(cell, key + 16.0)))) : repairAge;
        cover = max(cover, covered);
        seamDistance = min(seamDistance, lerp(1e3, abs(inside), present));
    }
    return cover;
}

// The wear of one pixel. The noise is the large wear noise (R drives the fatigue zones, G the block cracking, B varies
// the ground). The fine noise is the same texture at a small size, for ragged borders. Joined marks a road side along
// a pad. Edge normal returns the direction from the slab edge inward (world XZ), or zero on the rounded lip, and edge
// the distance to the slab edge.
RoadWear RoadEvaluateWear(float4 road, half joined, float2 positionXZ, half4 noise, half4 fine, out half tracks,
    out half oil, out half dust, out float edge, out float2 edgeNormal)
{
    RoadWear wear;
    wear.slope = 0.0;
    float isRoad = step(0.01, road.z);
    float halfWidth = max(road.z, 0.01);
    float across = road.x;
    float along = road.y;
    // The key of the noises of this road: a whole number, so the hashes do not change with the interpolation error
    // of the vertex value. A fraction made the crack widths jump from pixel to pixel, which flickered while the view
    // moved.
    float key = round(road.w * 977.0);
    float twoLanes = isRoad * step(3.0, halfWidth);

    // The size of a pixel on the ground, and the road directions in world XZ from the change of the road coordinates
    // across the screen. Both come from smooth values, so they do not flicker.
    float2 dPdx = ddx(positionXZ);
    float2 dPdy = ddy(positionXZ);
    float4 footprint = float4(dPdx, dPdy);
    float pixel = RoadPixel(footprint);
    float dAx = ddx(road.x);
    float dAy = ddy(road.x);
    float det = dPdx.x * dPdy.y - dPdx.y * dPdy.x;
    det = abs(det) > 1e-10 ? det : 1e-10;
    float2 gradient = float2(dAx * dPdy.y - dAy * dPdx.y, dAy * dPdx.x - dAx * dPdy.x) / det;
    // On a road the gradient of x points right, on a pad it points inward from the rim.
    float2 inward = gradient * rsqrt(max(dot(gradient, gradient), 1e-8));
    float2 right = inward;

    // Distance inward from the slab edge. A road side along a pad and a pad rim where a road joins have no edge.
    edge = lerp(road.x, halfWidth - abs(across) + joined * 10.0, isRoad);
    edgeNormal = isRoad > 0.5 ? right * (across < 0.0 ? 1.0 : -1.0) : inward;
    // On the rounded lip the road coordinates keep their rim values, so they give no direction. Zero tells the
    // caller to use the mesh normal.
    edgeNormal = dot(gradient, gradient) > 0.04 ? edgeNormal : 0.0;

    // Wheel tracks and the oil stripe of the lane at this point. Traffic wanders a little along the road.
    float laneCenter = twoLanes * (across < 0.0 ? -0.5 : 0.5) * halfWidth;
    float inLane = across - laneCenter - (RoadNoise(along / 23.0, key + 21.0) - 0.5) * 0.4;
    float track = abs(inLane) - _TrackOffset;
    tracks = half(isRoad * exp(-track * track / (2.0 * _TrackWidth * _TrackWidth)));
    oil = half(isRoad * exp(-inLane * inLane / (2.0 * _OilWidth * _OilWidth)) *
        lerp(0.6, 1.0, RoadNoise(along / 9.0, key + 22.0)));

    // The edge band: ground on the slab edge. Its width changes along the edge with world-space noise at about 8 and
    // 30 m, from a crumbling edge to gravel washed far onto the asphalt. Near the inner border the asphalt breaks
    // into chunks. Each chunk turns to ground at its own distance, so the border follows the chunk outlines, and a
    // narrow gap separates the loose chunks. The surface adds full ground on the outer part of the lip.
    float fromRim = edge - isRoad * RoadRimInset;
    half wander = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, positionXZ / 8.0).b * 0.4h +
        SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, positionXZ.yx / 30.0).b * 0.6h;
    float border = lerp(_EdgeWidthMin, _EdgeWidth, smoothstep(0.3, 0.7, wander)) + (fine.b - 0.5) * 0.25;
    dust = 0.0h;
    half edgeGap = 0.0h;
    // The change of the chunk warp across one pixel. Screen derivatives are not defined inside the branch below.
    float4 chunkWarpFootprint = float4(ddx(fine.gr), ddy(fine.gr)) * 0.9;
    [branch]
    if (fromRim < border + _EdgeChunkSize * 2.0)
    {
        float chunkDistance;
        float2 chunkNormal;
        float2 chunkId;
        // The fine noise bends the chunk outlines, so they are not straight.
        RoadCellEdge(positionXZ / _EdgeChunkSize + (fine.gr - 0.5) * 0.9, chunkDistance, chunkNormal, chunkId);
        // The chunks fade to a plain border where they are smaller than a few pixels, so they do not shimmer far
        // away.
        float chunkDetail = saturate(_EdgeChunkSize / (pixel * 6.0) - 0.5);
        float jitter = (RoadHash(chunkId + 17.3) - 0.5) * 2.0 * _EdgeChunkSize * chunkDetail;
        float inside = border + jitter - fromRim;
        dust = half(smoothstep(-0.06, 0.06, inside));
        float nearBorder = 1.0 - smoothstep(0.0, _EdgeChunkSize * 1.5, abs(border - fromRim));
        edgeGap = RoadCrack(chunkDistance * _EdgeChunkSize, _CrackWidth * 0.6 * nearBorder * chunkDetail,
            RoadWarpedFootprint(footprint, chunkWarpFootprint, _EdgeChunkSize), -chunkNormal, _EdgeChunkSize,
            wear.slope) * (1.0h - dust);
    }

    half open = 0.0h;
    half seal = 0.0h;

    // The cell warp: two sizes of the fine noise bend and roughen the cell edges. The warp can compress the cells, so
    // the crack antialiasing adds the change of the warp across a pixel (RoadWarpedFootprint).
    float2 warp = (fine.rg - 0.5) * 1.2 +
        (SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, positionXZ / 0.6).rg - 0.5) * 0.45;
    float4 warpFootprint = float4(ddx(warp), ddy(warp));
    float2 blockWarp = warp * 0.35;
    float4 blockWarpFootprint = float4(ddx(blockWarp), ddy(blockWarp));

    // Block cracking on old stretches: large blocks between thin cracks, where the green noise is high.
    float blockZone = smoothstep(_BlockStart, _BlockStart + 0.12, noise.g + (fine.g - 0.5) * 0.25);
    float blockWidth = RoadCrackWidth(dot(positionXZ, float2(0.6, 0.8)), key + 61.0) * 0.7 * blockZone;
    open = max(open, RoadCellCracks(positionXZ, _BlockSize, blockWarp, blockWarpFootprint, blockWidth, footprint,
        wear.slope));

    // Fatigue cracking in zones along the worn wheel tracks, and on pads where the wear noise is high. The zone has a
    // ragged border, and the cracks are widest in its middle.
    float fatigue = noise.r + (fine.r - 0.5) * 0.35 + lerp(0.0, tracks * 0.3, isRoad);
    float zone = smoothstep(_AlligatorStart, _AlligatorStart + 0.08, fatigue);
    float heart = smoothstep(_AlligatorStart, _AlligatorStart + 0.3, fatigue);
    float2 fatigueSlope = 0.0;
    float fatigueWidth = RoadCrackWidth(dot(positionXZ, float2(0.71, 0.71)), key + 51.0) * lerp(0.9, 2.0, heart) *
        zone;
    float cellDistance;
    float2 cellNormal;
    float2 cellId;
    RoadCellEdge(positionXZ / _AlligatorSize + warp, cellDistance, cellNormal, cellId);
    float4 fatigueFootprint = RoadWarpedFootprint(footprint, warpFootprint, _AlligatorSize);
    half fatigueCrack = RoadCrack(cellDistance * _AlligatorSize, fatigueWidth, fatigueFootprint, -cellNormal,
        _AlligatorSize, fatigueSlope);
    fatigueCrack = RoadNetworkCoverage(fatigueCrack, fatigueWidth, _AlligatorSize, pixel);
    // The ravelled rim beside the crack: lighter loose aggregate. It fades out before it is two pixels wide, so far
    // away it does not cancel the dark cracks.
    float rimPixel = RoadCrackPixel(fatigueFootprint, -cellNormal);
    half rim = RoadBand(cellDistance * _AlligatorSize, fatigueWidth * 3.0, rimPixel) * (1.0h - fatigueCrack) *
        half(zone) * half(saturate(fatigueWidth * 3.0 / rimPixel - 1.0));
    // Each block tilts a little, and some blocks sink.
    float2 tilt = (float2(RoadHash(cellId + 0.37), RoadHash(cellId + 5.71)) - 0.5) * 2.0 * _AlligatorTilt;
    fatigueSlope += tilt * zone;
    wear.sunken = half(step(RoadHash(cellId + 9.13), 0.18) * zone);
    wear.slope += fatigueSlope;
    open = max(open, max(fatigueCrack, edgeGap));

    // Repairs cover the cracks with new asphalt. A tar seam marks their edge.
    float seamDistance;
    half repairAge;
    half repair = RoadRepairs(across, along, halfWidth, twoLanes, key, pixel, seamDistance, repairAge) *
        half(isRoad);
    seal = max(seal * (1.0h - repair), RoadBand(seamDistance, _SealWidth * isRoad, pixel));
    open *= 1.0h - repair * 0.9h;
    wear.slope *= 1.0 - repair * 0.9;

    wear.crack = open;
    wear.rim = rim * (1.0h - repair);
    wear.seal = seal;
    wear.repair = repair;
    wear.repairAge = repairAge;
    wear.sunken *= 1.0h - repair;
    return wear;
}

#endif
