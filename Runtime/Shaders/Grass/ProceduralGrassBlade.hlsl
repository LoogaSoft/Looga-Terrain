#ifndef LOOGA_PROCEDURAL_GRASS_BLADE_INCLUDED
#define LOOGA_PROCEDURAL_GRASS_BLADE_INCLUDED

// The blade vertices of LoogaProceduralGrass.shader. The G-buffer pass and the motion vector pass build the same
// vertices, so the motion vector pass covers exactly the visible blades. Include it after Core.hlsl,
// VegetationWindField.hlsl and ProceduralGrassCommon.hlsl.

StructuredBuffer<GrassBlade> _GrassBlades;
StructuredBuffer<GrassType> _GrassTypes;
StructuredBuffer<uint> _GrassCounters;
float4 _GrassDrawParameters; // World size of one pixel per meter, minimum pixels, view widening, far offset.
float4 _GrassDrawParameters2; // Level of detail distance, thin blade smoothing, sway coherence, minimum screen width.
int _GrassLevel;

// One vertex of a blade strip.
struct GrassBladeVertex
{
    float3 position;
    float3 normal;      // Rounded normal toward the strip edges.
    float3 bladeNormal; // Normal of the flat strip.
    float3 tangent;
    float t;            // Position along the blade from root (0) to tip (1).
    float sideSign;     // -1 or 1 across the strip.
    float detail;       // Share of the detail across the blade width that the pixels can show.
    float cameraDistance;
};

float3 GrassBezier(float3 p0, float3 p1, float3 p2, float3 p3, float t)
{
    float s = 1 - t;
    return s * s * s * p0 + 3 * s * s * t * p1 + 3 * s * t * t * p2 + t * t * t * p3;
}

// Returns the signed wind strength toward flow, from the shared Looga wind field: the steady lean and the idle sway.
// The grass does not use the gusts of the field: their moving edges showed as hard lines over the grass at low views
// (removed 2026-11-16). Previous reads the field of the previous frame.
float GrassWind(float3 root, float hash, float time, float frequency, bool previous, out float2 flow)
{
    // Sway coherence 0 gives each blade a random phase, 1 moves neighbouring blades together in waves. Random phases
    // move adjacent blades in opposite directions every frame, which shows as jitter on thin blades.
    float spread = 1.0 - saturate(_GrassDrawParameters2.z);
    float phase = dot(root.xz, float2(0.173, 0.317)) + time * frequency + hash * 6.283185 * spread;
    float wave = sin(phase) * 0.7 + sin(phase * 1.73 + 1.1) * 0.3;
    float4 field = previous ? _LoogaFieldPreviousDirection : _LoogaFieldDirection;
    if (field.w == 0)
    {
        flow = float2(0.7071, 0.7071);
        return 0.3 * wave;
    }
    // The x component of the gust vector is the field strength.
    float strength = (previous ? _LoogaFieldPreviousGust : _LoogaFieldGust).x;
    float lean = saturate((previous ? _LoogaFieldPreviousWave : _LoogaFieldWave).y);
    float3 direction = VegetationFieldWorldDirection(field.xyz, root, previous);
    flow = direction.xz / max(length(direction.xz), 0.001);
    return strength * (lean + (1 - lean) * wave);
}

// Finds the blade of a vertex. Returns false for the vertices after the last blade of the final instance.
bool GrassFindBlade(uint vertexID, uint instanceID, out GrassBlade blade, out uint vertex)
{
    uint rows = _GrassLevel == 0 ? 7u : 3u;
    uint vertices = 2u * rows + 1u;
    uint bladeInInstance = vertexID / vertices;
    vertex = vertexID - bladeInInstance * vertices;
    uint index = instanceID * 16u + bladeInInstance;
    blade = (GrassBlade)0;
    if (index >= _GrassCounters[2 + _GrassLevel]) return false;
    blade = _GrassBlades[(_GrassLevel == 0 ? 0u : (uint)_GrassDrawParameters.w) + index];
    return true;
}

// Builds one vertex of a blade at a wind time. Previous reads the wind field of the previous frame.
GrassBladeVertex GrassBuildVertex(GrassBlade blade, GrassType type, uint vertex, float time, bool previous)
{
    uint rows = _GrassLevel == 0 ? 7u : 3u;
    uint vertices = 2u * rows + 1u;
    float3 root = blade.root.xyz;
    float bladeLength = blade.root.w;
    float3 up = float3(0, 1, 0);

    // The facing turns toward the wind, so the field leans together. The wind adds lean along its flow.
    float2 flow;
    float wind = GrassWind(root, blade.data.y, time, type.wind.y, previous, flow);
    float2 facing = lerp(blade.shape.xy, flow, type.normal.z);
    facing /= max(length(facing), 0.0001);
    float2 leanAngles = facing * blade.shape.w + flow * (wind * type.wind.x);
    float lean = min(length(leanAngles), 2.0);
    float2 leanDirection = lean > 0.0001 ? leanAngles / length(leanAngles) : facing;
    float3 horizontal = float3(leanDirection.x, 0, leanDirection.y);

    // The blade leaves the ground upright, reaches the tip at the lean angle, and ends turned by the droop.
    float3 tip = root + (horizontal * sin(lean) + up * cos(lean)) * bladeLength;
    float tipAngle = min(lean + blade.data.x, 2.6);
    float3 tipTangent = horizontal * sin(tipAngle) + up * cos(tipAngle);
    float3 control1 = root + up * (bladeLength * 0.35);
    float3 control2 = tip - tipTangent * (bladeLength * 0.35);
    float3 side = float3(leanDirection.y, 0, -leanDirection.x);

    bool tipVertex = vertex == vertices - 1u;
    float t = tipVertex ? 1.0 : pow((vertex / 2u) / (float)rows, 0.85);
    float s = 1 - t;
    float3 position = GrassBezier(root, control1, control2, tip, t);
    float cameraDistance = length(_WorldSpaceCameraPos - root);
    // Near blades take the shape of the 7 vertex blade before they change to it, so the change does not pop:
    // each vertex moves onto the coarse strip between its two coarse rows (rows at (j / 3)^0.85 and the tip).
    if (_GrassLevel == 0 && !tipVertex)
    {
        float lodDistance = _GrassDrawParameters2.x;
        float morph = saturate((cameraDistance - lodDistance * 0.6) / max(lodDistance * 0.4, 0.001));
        if (morph > 0)
        {
            float row = min(floor(pow(t, 1.0 / 0.85) * 3.0), 2.0);
            float rowStart = pow(row / 3.0, 0.85);
            float rowEnd = row < 2.0 ? pow((row + 1.0) / 3.0, 0.85) : 1.0;
            float share = saturate((t - rowStart) / max(rowEnd - rowStart, 0.0001));
            float3 coarse = lerp(GrassBezier(root, control1, control2, tip, rowStart),
                GrassBezier(root, control1, control2, tip, rowEnd), share);
            position = lerp(position, coarse, morph);
        }
    }
    float3 derivative = 3 * s * s * (control1 - root) + 6 * s * t * (control2 - control1) +
        3 * t * t * (tip - control2);
    float3 tangent = normalize(derivative);
    // Each blade twists around its own axis from the root to the tip, so the blades of a field face many
    // directions and some of them reflect the sun toward the camera from every view.
    float twist = (frac(blade.data.y * 7.13) * 2.0 - 1.0) * type.detail.x * t;
    side = side * cos(twist) + cross(tangent, side) * sin(twist);
    float3 normal = normalize(cross(side, tangent));

    // Edge-on blades get wider, and no blade gets thinner than the minimum pixel width.
    float sideSign = (vertex & 1u) ? 1.0 : -1.0;
    float3 view = normalize(_WorldSpaceCameraPos - position);
    float width = blade.shape.z * (1 + _GrassDrawParameters.z * (1 - abs(dot(normal, view))));
    width = max(width, _GrassDrawParameters.y * _GrassDrawParameters.x * cameraDistance);
    float taper = tipVertex ? 0.0 : 1 - t * t;
    position += side * (sideSign * 0.5 * width * taper);
    // The width that a row shows on screen is its width across the view: at right angles to the view ray and to the
    // blade axis. A row narrower than the minimum screen width gets wider in that direction. The blade does not turn,
    // so edge-on blades keep their facing and their shading. The tip stays one vertex.
    float minimumScreenWidth = _GrassDrawParameters2.w;
    if (minimumScreenWidth > 0.0 && !tipVertex)
    {
        float3 screenAcross = cross(view, tangent);
        float screenAcrossLength = length(screenAcross);
        if (screenAcrossLength > 1e-3)
        {
            screenAcross /= screenAcrossLength;
            float minimumHalfWidth = 0.5 * minimumScreenWidth * _GrassDrawParameters.x *
                length(_WorldSpaceCameraPos - position);
            float shownHalfWidth = dot(side, screenAcross) * (sideSign * 0.5 * width * taper);
            float direction = abs(shownHalfWidth) > 1e-6 ? sign(shownHalfWidth) : sideSign;
            float targetHalfWidth = direction * max(abs(shownHalfWidth), minimumHalfWidth);
            position += screenAcross * (targetHalfWidth - shownHalfWidth);
        }
    }
    // Blades only a few pixels wide cannot show their rounding, vein and gloss stripe. Those details then
    // change from pixel to pixel as the blade moves, so they fade out like the mip levels of a texture.
    float bladePixels = blade.shape.z / max(_GrassDrawParameters.x * cameraDistance, 1e-5);
    float detail = saturate((bladePixels - 1.0) / 3.0);

    GrassBladeVertex output;
    output.position = position;
    // Normals turn outward to the blade edges, so the flat strip shades like a rounded blade.
    float roundness = type.normal.y * detail;
    output.normal = tipVertex ? normal : normalize(normal + side * (sideSign * roundness));
    output.bladeNormal = normal;
    output.tangent = tangent;
    output.t = t;
    output.sideSign = sideSign;
    output.detail = detail;
    output.cameraDistance = cameraDistance;
    return output;
}

// Clip position of a blade vertex. A blade on the ground of a mesh moves toward the camera as far as the terrain
// surface mesh of that ground, which moves out along its normal and moves its depth toward the camera (see
// GetLoogaTerrainVertex and GetLoogaTerrainMeshClipPosition). Else the mesh covered the lower part of each blade, and
// the grass on a cliff wall top looked thin from above.
float4 GrassClipPosition(float3 positionWS, GrassBlade blade)
{
    if ((asuint(blade.data.z) & GRASS_ON_MESH_GROUND) != 0u)
    {
        float3 toCamera = GetCameraPositionWS() - positionWS;
        float cameraDistance = length(toCamera);
        float pull = min(0.15 + 0.004 * cameraDistance, 0.5 * cameraDistance) + max(0.02, 0.0005 * cameraDistance);
        positionWS += toCamera * (pull / max(cameraDistance, 1e-4));
    }
    return TransformWorldToHClip(positionWS);
}
#endif
