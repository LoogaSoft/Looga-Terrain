Shader "Looga/Water"
{
    // Water surface for LoogaWaterBody meshes. Gerstner waves move the vertices. The fragment refracts the opaque
    // texture, absorbs light along the path through the water, reflects the environment with Fresnel, and adds the sun
    // highlight, light through the wave crests and foam. With a LoogaWaterReflectionFeature on the renderer, the
    // reflection traces the opaque scene in screen space and uses the reflection probes where the trace misses. Water
    // at the height of a LoogaPlanarReflection shows its mirrored scene instead. Detail textures project from above in
    // world XZ and move with the flow of each vertex (UV0 xy, metres per second). UV0 z is the surface alpha. On
    // flowing water the waves calm down, and the detail, the swell, the wind slicks and the foam all move with the
    // flow, so the whole surface reads as moving water. Fast water uses shorter flow cycles, so its textures move
    // instead of fading in place. With Fall Sheet on, the surface is a sheet of falling water (LoogaWaterFall); see
    // the Fall properties. The surface code is in LoogaWaterSurface.hlsl.
    Properties
    {
        [Header(Waves)]
        _WaveScale("Wave Scale", Range(0, 1)) = 0.35
        // x, z: direction. z: steepness (0 to 1). w: wavelength in meters.
        _WaveA("Wave A (Dir XZ, Steepness, Length)", Vector) = (1, 0.35, 0.18, 24)
        _WaveB("Wave B (Dir XZ, Steepness, Length)", Vector) = (0.8, -0.6, 0.15, 13)
        _WaveC("Wave C (Dir XZ, Steepness, Length)", Vector) = (0.3, 1, 0.12, 7.5)
        _WaveD("Wave D (Dir XZ, Steepness, Length)", Vector) = (-0.5, 0.9, 0.1, 4.2)
        // Waves from the LoogaOceanWaves component of the scene, in place of the four waves above.
        [Toggle(_OCEAN_WAVES)] _OceanWaves("Ocean Waves (FFT)", Float) = 0
        _OceanHeight("Ocean Wave Height", Range(0, 3)) = 1
        // The ocean waves grow from 0 at the water edge to their full height at this water depth, in meters. This is
        // the depth for the first cascade. A cascade with a smaller tile has smaller waves and reaches its full height
        // at a smaller depth, so short waves still move in shallow water where the long waves fade.
        _OceanShoreDepth("Ocean Full Wave Depth (Meters)", Float) = 4
        // The ripples in the light (normals) fade only over this last water depth at the water edge, in meters.
        _OceanRippleDepth("Ocean Full Ripple Depth (Meters)", Float) = 0.4
        _OceanNormalStrength("Ocean Normal Strength", Range(0, 2)) = 1
        // Wave height that gives the full crest light, in meters. The ocean waves get no crest foam from it.
        _OceanCrestHeight("Ocean Crest Height (Meters)", Float) = 0.5
        // Whitecap foam from the foam of LoogaOceanWaves: smoothstep of 0.75 times the summed cascade foam.
        _OceanFoam("Ocean Whitecap Foam", Range(0, 2)) = 1
        // The ocean slopes fade to 1.5 % with the distance at this rate, per meter. The far sea becomes smooth.
        _OceanNormalFalloff("Ocean Normal Distance Falloff", Range(0, 0.1)) = 0.006
        // The whitecap foam fades with the distance at this rate, per meter.
        _OceanFoamFalloff("Ocean Foam Distance Falloff", Range(0, 0.05)) = 0.0075
        // Waves that come to the shore along the arrival field of the water body (LoogaWaterBody Shore Waves). They
        // turn toward the shore, get shorter and higher in shallow water and break where they are higher than 0.78 of
        // the depth. Needs Ocean Waves. Height 0 = off.
        _ShoreWaveHeight("Shore Wave Height (Meters)", Float) = 0
        _ShoreWavePeriod("Shore Wave Period (Seconds)", Float) = 11
        // The shore waves fade out between half this distance and this distance from the water edge, in meters.
        _ShoreWaveReach("Shore Wave Reach (Meters)", Float) = 250
        // Horizontal movement toward the crests. Higher values give sharper crests.
        _ShoreWaveSteepness("Shore Wave Steepness", Range(0, 1)) = 0.35
        // Foam behind the crests of breaking shore waves.
        _SurfFoam("Surf Foam", Range(0, 2)) = 1
        // Time in which the surf foam behind a crest falls to about a third, in seconds.
        _SurfFoamTime("Surf Foam Time (Seconds)", Float) = 1.5
        // Waves that run out from the plunge of a fall (the plunge lines of a water network) over the water body, as
        // curved crests with surf foam behind them. The crests turn around the ends of the plunge and the shore like
        // shore waves. They work with and without Ocean Waves. Height 0 = off.
        _PlungeWaveHeight("Plunge Wave Height (Meters)", Float) = 0.25
        _PlungeWavePeriod("Plunge Wave Period (Seconds)", Float) = 2.5
        // The plunge waves fade out over this distance from the plunge, in meters.
        _PlungeWaveReach("Plunge Wave Reach (Meters)", Float) = 160
        // Foam behind the crests of the plunge waves.
        _PlungeFoam("Plunge Foam", Range(0, 3)) = 1.6
        // Time in which the plunge foam behind a crest falls to about a third, in seconds.
        _PlungeFoamTime("Plunge Foam Time (Seconds)", Float) = 1.5
        // Set by the Looga water body: R = time from the plunge lines, GB = gradient toward them, A = fade.
        [HideInInspector][NoScaleOffset] _PlungeWaveMap("Plunge Wave Map", 2D) = "black" {}
        // Set by the Looga water body: R = distance along the plunge lines where the wave of a point started.
        [HideInInspector][NoScaleOffset] _PlungeAlongMap("Plunge Along Map", 2D) = "black" {}
        // Rise of the water at the shore after each shore wave, in meters. The water runs up the beach as a thin sheet
        // and drains back before the next wave. Only on the near grid of the ocean. 0 = off.
        _SwashHeight("Swash Run-Up Height (Meters)", Float) = 0
        // Set by the Looga water body with Shore Waves. R = arrival time, GB = gradient toward the shore, A = fade.
        [HideInInspector][NoScaleOffset] _ShoreWaveMap("Shore Wave Map", 2D) = "black" {}
        // Set by the Looga water body with Shore Waves: the shore map every half metre in a window around the camera.
        [HideInInspector][NoScaleOffset] _ShoreFineMap("Shore Fine Map", 2D) = "black" {}
        [HideInInspector] _ShoreFineRect("Shore Fine Rect", Vector) = (0, 0, 0, 0)

        [Header(Flow)]
        // The detail textures and the foam move with the vertex flow. Each cycle restarts the texture offset, and two
        // cycles half a cycle apart blend, so the textures do not stretch.
        _FlowCycle("Flow Cycle (Seconds)", Float) = 4
        // At this speed the water gets the full fast-water normals and foam, in meters per second.
        _FastSpeed("Fast Water Speed (m/s)", Float) = 2
        _FastNormal("Fast Water Normal Strength", Range(1, 3)) = 1.6
        _FastFoam("Fast Water Foam", Range(0, 1)) = 0.35
        // Water below this speed has no fast water foam, in meters per second. Above it the foam rises over the fast
        // water speed.
        _FastFoamStart("Fast Foam Start (m/s)", Float) = 0
        // The Gerstner waves fade by this share on fast water, which shows its moving detail instead.
        _FlowWaveDamping("Fast Water Wave Damping", Range(0, 1)) = 0.8
        // The Gerstner waves start to fade at the first speed and have the full damping at the second speed, in meters
        // per second. Set the start above a sea drift, so the waves of the open sea stay. A second speed of 0 uses the
        // fast water speed.
        _WaveCalmStart("Wave Calm Start (m/s)", Float) = 0
        _WaveCalmSpeed("Wave Calm Speed (m/s)", Float) = 0
        // Fast water boils, for example below a waterfall: bumps that rise and fall in place. Height in meters at the
        // full speed, 0 = off. The boils start at the first speed and are full at the second speed, in meters per second.
        _Turbulence("Turbulence Height (m)", Range(0, 1)) = 0
        _TurbulenceStart("Turbulence Start (m/s)", Float) = 2
        _TurbulenceFull("Turbulence Full (m/s)", Float) = 7
        // A water network gives each vertex the turbulence and the aeration of its flow regime (UV1 x and y, 0 to 1).
        // Turbulence acts like fast water: stronger detail normals, calmer waves and boils. Aeration adds white water,
        // which the foam simulation also carries downstream. Bodies outside a network have none.
        _RegimeTurbulence("Regime Turbulence", Range(0, 1)) = 1
        _RegimeBoils("Regime Boils Height (m)", Range(0, 1)) = 0.15
        _RegimeWhiteWater("Regime White Water", Range(0, 1)) = 0.4

        [Header(Detail Normals)]
        [Normal][NoScaleOffset] _NormalMap("Normal Map", 2D) = "bump" {}
        _DetailSizeA("Detail A Size (Meters)", Float) = 6
        _DetailSizeB("Detail B Size (Meters)", Float) = 2.7
        _DetailStrengthA("Detail A Strength", Range(0, 2)) = 0.7
        _DetailStrengthB("Detail B Strength", Range(0, 2)) = 0.5
        // The detail layers drift on their own as well as with the flow, in meters per second.
        _DetailDrift("Detail Drift (A XZ, B XZ)", Vector) = (0.25, 0.1, -0.12, 0.22)
        _MacroSize("Macro Size (Meters)", Float) = 40
        _MacroStrength("Macro Strength", Range(0, 2)) = 0.35
        // A very large normal layer that stays at every distance, so far and high views keep visible swell.
        _SwellSize("Swell Size (Meters)", Float) = 160
        _SwellStrength("Swell Strength", Range(0, 2)) = 0.45
        // Detail normals fade and the surface gets rougher over this distance. This stops far glitter.
        _DetailFadeDistance("Detail Fade Distance (Meters)", Float) = 250
        _FarDetail("Far Detail Strength", Range(0, 1)) = 0.35
        // Far away the small layers give way to a copy of layer A this many times larger, so tiles do not repeat.
        _FarDetailScale("Far Detail Scale", Range(1, 8)) = 4
        // Calm and rough patches on a large scale, like wind slicks on open water. They break up repetition.
        _SlickSize("Wind Slick Size (Meters)", Float) = 260
        _SlickStrength("Wind Slick Strength", Range(0, 1)) = 0.45
        // Shifts each wave by a large, smooth noise, so the waves do not add up to a regular lattice.
        _WaveWarp("Wave Warp (Wavelengths)", Range(0, 1)) = 0.6
        _WaveWarpSize("Wave Warp Size (Meters)", Float) = 420
        // Wave normals fade toward this strength far away.
        _FarWaveNormal("Far Wave Normal Strength", Range(0, 1)) = 0.4

        [Header(Water Body)]
        // Absorption per meter of water for red, green and blue. Red is absorbed first in clear water.
        _Absorption("Absorption Per Meter (RGB)", Vector) = (0.45, 0.075, 0.05, 0)
        _AbsorptionScale("Absorption Scale", Range(0, 4)) = 1
        // The color that the water scatters back from depth. Lit by the sun and the ambient light.
        _DeepColor("Scatter Color", Color) = (0.02, 0.085, 0.105, 1)
        _MaxPath("Max Path Through Water (Meters)", Float) = 60
        _RefractionStrength("Refraction Strength (Meters)", Range(0, 1)) = 0.25

        [Header(Surface Light)]
        _Smoothness("Smoothness", Range(0, 1)) = 0.94
        _FarSmoothness("Far Smoothness", Range(0, 1)) = 0.8
        _ReflectionStrength("Environment Reflection", Range(0, 2)) = 1
        // Share of the screen-space reflection over the probe reflection where a ray finds a surface. The renderer
        // needs a LoogaWaterReflectionFeature for screen-space reflections.
        _ScreenReflection("Screen-Space Reflection", Range(0, 1)) = 1
        _SunStrength("Sun Highlight", Range(0, 4)) = 1
        // Normals for the Fresnel term are this much flatter. This keeps small ripples from reflecting the ground.
        _FresnelNormalFlatten("Fresnel Normal Flatten", Range(0, 1)) = 0.5

        [Header(Light Through Crests)]
        _SSSColor("Crest Light Color", Color) = (0.1, 0.45, 0.4, 1)
        _SSSStrength("Crest Light Strength", Range(0, 4)) = 1
        _SSSPower("Crest Light Focus", Range(1, 16)) = 4
        _SSSDistortion("Crest Light Normal Distortion", Range(0, 1)) = 0.4

        [Header(Foam)]
        // Large-scale noise for the flow phases, the wind slicks and the wave warp.
        [NoScaleOffset] _FoamMap("Foam Mask (R)", 2D) = "black" {}
        // R = clumps, G = bubbles, A = height. Each channel is equalized: the foam coverage is its threshold. The lace
        // channel (B) is not used: its cell walls showed as a regular pattern.
        [NoScaleOffset] _FoamDetailMap("Foam Detail (R Clumps, G Bubbles, A Height)", 2D) = "black" {}
        _FoamDetailSize("Foam Detail Size (Meters)", Float) = 4
        _FoamColor("Foam Color", Color) = (0.8, 0.82, 0.82, 1)
        // Foam coverage falls from the water edge to 0 over this distance. A Looga water body gives the distance.
        _ShoreFoamWidth("Shore Foam Width (Meters)", Float) = 1.5
        _ShoreFoam("Shore Foam Amount", Range(0, 1)) = 0.8
        // The shore band grows and shrinks over time, like small waves on the shore.
        _ShoreFoamPulse("Shore Foam Pulse", Range(0, 1)) = 0.3
        // Without a shore texture (not a Looga water body), the shore band uses the water depth behind the pixel.
        _ShoreFoamDepth("Shore Foam Depth Without Shore Map (Meters)", Float) = 0.8
        // Thin foam line where an object or the ground touches the water, from the depth behind the pixel. Vertical
        // depth finds flat ground, and the depth along the view finds walls, for example rocks in the water.
        _ContactFoamDepth("Contact Foam Depth (Meters)", Float) = 0.15
        _ContactFoam("Contact Foam Amount", Range(0, 1)) = 0.5
        // Thin foam and scum in patches on open water.
        _SurfaceFoam("Open Water Foam", Range(0, 1)) = 0
        _CrestFoamStart("Crest Foam Start", Range(0, 1)) = 0.55
        _CrestFoam("Crest Foam Amount", Range(0, 1)) = 0.5
        _FoamSoftness("Foam Edge Softness", Range(0.01, 1)) = 0.15
        // Sun light through thin foam, seen against the sun.
        _FoamTranslucency("Foam Translucency", Range(0, 1)) = 0.4
        // Darkening of the low parts between the foam clumps and bubbles.
        _FoamOcclusion("Foam Occlusion", Range(0, 1)) = 0.35
        // Darker, softer foam under the surface below the surface foam.
        _SubFoam("Under-Water Foam", Range(0, 1)) = 0.5
        _SubFoamDepth("Under-Water Foam Depth (Meters)", Float) = 0.25
        // The foam detail blends into its average over this distance, so far foam does not glitter.
        _FoamFadeDistance("Foam Detail Fade Distance (Meters)", Float) = 150
        // Set by the Looga water body for each renderer. xy = world XZ origin, zw = 1 / size. Zero = no shore map.
        [HideInInspector][NoScaleOffset] _ShoreMap("Shore Map", 2D) = "black" {}
        [HideInInspector] _ShoreMapRect("Shore Map Rect", Vector) = (0, 0, 0, 0)
        // Set by the Looga water body for an Ocean Waves area: where it has water, for the near grid outside the shore
        // map. xy = world XZ origin, zw = 1 / size. Zero = no mask. Open = 1: the ground outside the rect is open water.
        [HideInInspector][NoScaleOffset] _OceanCoverageMap("Ocean Coverage Map", 2D) = "black" {}
        [HideInInspector] _OceanCoverageRect("Ocean Coverage Rect", Vector) = (0, 0, 0, 0)
        [HideInInspector] _OceanCoverageOpen("Ocean Coverage Open", Float) = 1
        // Set by the Looga water body when it simulates foam. xy = world XZ origin, zw = 1 / size. Zero = no foam.
        [HideInInspector][NoScaleOffset] _FoamSim("Foam Simulation", 2D) = "black" {}
        [HideInInspector] _FoamSimRect("Foam Simulation Rect", Vector) = (0, 0, 0, 0)
        [HideInInspector] _FoamSimFade("Foam Simulation Side Fade", Vector) = (1, 1, 1, 1)
        [HideInInspector] _FoamSimAge("Foam Simulation Step Age", Float) = 0

        [Header(Edges)]
        // The surface fades out where the water is thinner than this. This hides the line on the shore.
        _EdgeFade("Edge Fade (Meters)", Float) = 0.15
        // Negative values draw the surface in front of water at the same depth, for example a river mouth in a lake.
        _DepthOffset("Depth Offset", Float) = 0
        // Set by the renderer of a fall sheet: the depth write and the color mask of the surface pass.
        [HideInInspector] _WaterZWrite("Depth Write", Float) = 1
        [HideInInspector] _WaterColorMask("Color Mask", Float) = 15
        // Set by the Looga water body for each renderer: the weld line where a fall sheet starts.
        [HideInInspector][NoScaleOffset] _WaterWeldMap("Weld Map", 2D) = "black" {}
        [HideInInspector] _WaterWeldRect("Weld Rect", Vector) = (0, 0, 0, 0)
        [HideInInspector] _WaterWeldParams("Weld Params", Vector) = (0, 0, 1, 0)

        // Fall mode: a sheet of falling water from a LoogaWaterFall. The fall sheet draws with a copy of the material
        // of the water that feeds it, with these fall values, so the water and the fall look like one surface. Near
        // the brink the sheet shows the look of that water. Past the brink the fall look comes in: streaks, clumps,
        // white water, foam shells and the plunge pool foam.
        [Header(Fall)]
        [Toggle(_WATER_FALL)] _WaterFall("Fall Sheet", Float) = 0
        _FallStreakWidth("Streak Width (Meters)", Float) = 2.5
        [NoScaleOffset] _FallHeightMap("Fall Height (R)", 2D) = "gray" {}
        [Normal][NoScaleOffset] _FallNormalMap("Fall Normal", 2D) = "bump" {}
        [NoScaleOffset] _FallFoamMask("Aerated Foam Mask (R)", 2D) = "gray" {}
        // A fine foam mask whose small mips give broad noise for the clumps and the pool.
        [NoScaleOffset] _FallNoiseMap("Noise (R)", 2D) = "gray" {}
        // Tileable foam lace (bubble cell walls) for the pool foam.
        [NoScaleOffset] _FallPoolFoamMap("Pool Foam Lace (R)", 2D) = "gray" {}
        _FallNormalStrength("Normal Strength", Range(0, 2)) = 1
        // Vertex movement along the normal from the height texture, in meters.
        _FallBulgeHeight("Drop Bulge Height", Float) = 0.35
        _FallBoilHeight("Plunge Boil Height", Float) = 0.45
        // The end of a sheet that runs out onto water: over this distance from its end, in meters along the mesh, its
        // bulges, clumps and boils fade out, so the end is calm. The part past the plunge draws as white water, and
        // the foam shell fades out over this distance before its end. 0 = off.
        _FallEndPin("End Pin (Meters)", Float) = 0
        // The calm end sinks by this much below its mesh, in meters, so a moving water surface covers its edge.
        _FallEndSink("End Sink (Meters)", Float) = 0
        // The calm end moves with the boils of the water under it (LoogaBoils), by this height in meters.
        _FallSeaBoil("Sea Boil Height (Meters)", Float) = 0
        // The shell lumps move this many times more than the jet surface.
        _FallShellBulge("Shell Bulge Scale", Float) = 1
        _FallShellCoverage("Shell Coverage", Range(0, 1)) = 0.55
        // Width of the soft edge of the shell lumps. Wide edges look like soft, fluffy foam.
        _FallShellSoftness("Shell Softness", Range(0.05, 1)) = 0.4
        // The streak normals of fully white water are this much weaker, so foam looks soft and not glassy.
        _FallFoamNormal("Foam Normal Strength", Range(0, 1)) = 0.45
        // Large lumps of water that fall with the jet: size across the flow in meters, length along the flow in
        // seconds of travel and the largest outward movement at the plunge in meters.
        _FallClumpSize("Clump Size (Meters)", Float) = 5
        _FallClumpPeriod("Clump Length (Seconds)", Float) = 0.35
        _FallClumpHeight("Clump Height (Meters)", Float) = 0.7
        // The shell lumps move this many times more, so the outer layer billows over the jet.
        _FallShellClump("Shell Clump Scale", Float) = 1.8
        // Hollows between the clumps are darker and the clump sides turn the light like rounded cloud forms.
        _FallClumpShadow("Clump Hollow Shadow", Range(0, 1)) = 0.5
        _FallClumpNormal("Clump Normal Strength", Range(0, 3)) = 1
        // Variation along the lip: thick jets with thinner water between them (0 = an even sheet). Jet Size is the
        // width of the largest jets. Thick jets stand out from the curtain by up to Jet Bulge.
        _FallJetVariation("Jet Variation", Range(0, 1)) = 0
        _FallJetSize("Jet Size (Meters)", Float) = 24
        _FallJetBulge("Jet Bulge (Meters)", Float) = 1.5
        // Past the break-up progress the curtain breaks into strands. At the plunge the gaps between them take Gap
        // Share of the width and show only spray (Gap Spray = its opacity). The strand edges wander by up to Strand
        // Fray.
        _FallBreakup("Breakup Progress", Range(0, 1)) = 0.15
        _FallGapShare("Gap Share", Range(0, 1)) = 0.35
        _FallGapSpray("Gap Spray", Range(0, 1)) = 0.3
        _FallStrandFray("Strand Fray (Meters)", Float) = 4
        // Clusters of water that fall down the strands and across the gaps, with a dense head and a tail above it:
        // size across the flow in meters and length along it in seconds of travel.
        _FallCometStrength("Cluster Strength", Range(0, 1)) = 0.6
        _FallCometSize("Cluster Size (Meters)", Float) = 6
        _FallCometLength("Cluster Length (Seconds)", Float) = 0.25
        // Thin water at the silhouette lets light through, so the outline glows softly.
        _FallFoamRim("Edge Glow", Range(0, 2)) = 0.6
        // Air at the brink and the progress where the water is fully white.
        _FallStartAir("Start Air", Range(0, 1)) = 0.6
        // The white water starts this far past the brink (negative: before it) and fades in over the onset distance.
        _FallAirStart("Air Start (Meters)", Float) = 0
        _FallAirOnset("Air Onset (Meters)", Float) = 0.5
        _FallFullAirProgress("Full Air Progress", Range(0.05, 1)) = 0.3
        _FallAirSoftness("Air Edge Softness", Range(0.01, 1)) = 0.3
        // Even fully aerated water keeps some darker streaks.
        _FallMaxAir("Max Air", Range(0, 1)) = 0.9
        // Water that rubs a wall or flows behind a pier turns white.
        _FallWallFoam("Wall Foam", Range(0, 1)) = 0.5
        _FallWallFoamWidth("Wall Foam Width (Meters)", Float) = 2.5
        // The pool foam texture size, in meters.
        _FallPoolFoamSize("Pool Foam Size (Meters)", Float) = 11
        // Width of the soft edge of the pool foam. Wide edges look soft, narrow edges look coarse.
        _FallPoolFoamSoftness("Pool Foam Softness", Range(0.05, 1)) = 0.5
        // How much of the pool the moving foam rafts cover near the plunge. They thin out toward the pool end.
        _FallPoolRaftCover("Pool Raft Cover", Range(0, 2)) = 1
        // Pool length in meters (progress 1 to 2), so the foam fades by distance from the plunge.
        _FallPoolLength("Pool Length (Meters)", Float) = 80
        // A solid white boil this long at each plunge, with a ragged edge.
        _FallPoolBoilLength("Plunge Boil Length (Meters)", Float) = 8
        // Past the boil the foam breaks into streaks: their width across the flow and their length along it, in
        // meters. The foam amount falls by e over the reach, so the streaks thin into lace and trails.
        _FallPoolStreakWidth("Pool Streak Width (Meters)", Float) = 2.5
        _FallPoolStreakLength("Pool Streak Length (Meters)", Float) = 14
        _FallPoolFoamReach("Pool Foam Reach (Meters)", Float) = 16
        // Near the plunge the water is full of bubbles: milky, pale and matte. The aeration falls by e over this
        // distance in meters.
        _FallPoolAeration("Pool Aeration Reach (Meters)", Float) = 12
        _FallAeratedColor("Aerated Water Color", Color) = (0.62, 0.8, 0.78, 1)
        // Between the training walls the basin water churns and stays milky, so the basin floor does not show.
        _FallPoolBasinAeration("Pool Basin Aeration", Range(0, 1)) = 0.75
        // The milky water fades out at this distance from the plunge, with a ragged edge. Past it the basin shows
        // deep, clear water: the sheet covers the floor with this share and at least the min path of water.
        _FallPoolMilkyReach("Pool Milky Reach (Meters)", Float) = 18
        _FallPoolBasinCover("Pool Basin Cover", Range(0, 1)) = 0.85
        _FallPoolMinPath("Pool Basin Min Path (Meters)", Float) = 30
        // The distance from the plunge to the end of the training walls. Past it the milky water and the clear
        // sheet fade out over the open fade distance with a ragged edge.
        _FallPoolOpenStart("Pool Open Start (Meters)", Float) = 40
        _FallPoolOpenFade("Pool Open Fade (Meters)", Float) = 30
        // Large-scale warp of the pool flow, in meters, so the foam does not move in straight lines.
        _FallPoolWarp("Pool Flow Warp (Meters)", Float) = 7
        // The pool foam is the pool foam size long along the flow at this speed.
        _FallPoolTileSpeed("Pool Foam Tile Speed (m/s)", Float) = 5
        // The pool foam spreads this much wider across the flow by the pool end.
        _FallPoolSpread("Pool Foam Spread", Range(0, 2)) = 0.6
        _FallFoamColor("Foam Color", Color) = (0.94, 0.96, 0.97, 1)
        // Troughs between foam lumps are darker.
        _FallFoamShadow("Foam Cavity Shadow", Range(0, 1)) = 0.35
        _FallFoamWrap("Foam Light Wrap", Range(0, 1)) = 0.8
        _FallTranslucency("Backlight Strength", Range(0, 4)) = 1.2
        _FallTranslucencyColor("Backlight Color", Color) = (0.55, 0.85, 0.8, 1)
        // The light path through the clear falling water is at least this long, so thin water over concrete looks
        // as deep as the water that feeds it.
        _FallMinPath("Min Path Through Water (Meters)", Float) = 6
        // The distance past the brink over which the fall look comes in, in meters, while the look of the water
        // that feeds the fall fades out.
        _FallClearHandoff("Clear Handoff (Start, End)", Vector) = (0, 0.5, 0, 0)
        // Refraction of the falling water, as a share of the screen.
        _FallRefraction("Refraction Strength", Range(0, 0.2)) = 0.05
        // The fall look fades in over the start fade and out over the end fade, in meters.
        _FallStartFade("Start Fade (Meters)", Float) = 0.5
        _FallEndFade("End Fade (Meters)", Float) = 4
        // The jet writes depth up to this progress, so effects behind it stay hidden. Effects near the plunge show.
        _FallDepthEnd("Depth Write End Progress", Range(0, 1)) = 1
        // The soft depth lies this far behind the surface, in meters. Negative values pull it toward the camera.
        _FallDepthPushBack("Depth Push Back (Meters)", Float) = 0
        // The pool foam tests its depth this far closer to the camera, in meters, so water waves under it stay hidden.
        _FallPoolDepthBias("Pool Depth Bias (Meters)", Float) = 1
        // Depth only: the sheet writes the depth of its solid parts before its color pass.
        [HideInInspector][Toggle(_WATER_FALL_DEPTH)] _WaterFallDepth("Fall Depth Only", Float) = 0
    }

    SubShader
    {
        // Water renders before the height fog overlay (queue 3001), so the overlay fogs it.
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent-2" "IgnoreProjector"="True" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_WaterZWrite]
            ColorMask [_WaterColorMask]
            Cull Back
            Offset [_DepthOffset], [_DepthOffset]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex WaterVertex
            #pragma fragment WaterFragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #pragma multi_compile_instancing
            #pragma shader_feature_local _OCEAN_WAVES
            #pragma multi_compile_local _ _WATER_FALL
            #pragma multi_compile_local_fragment _ _WATER_FALL_DEPTH

            #include "Packages/com.loogasoft.loogaterrain/Runtime/Water/Shaders/LoogaWaterSurface.hlsl"
            ENDHLSL
        }

        // Writes the depth of the water surface into the camera depth texture of LoogaWaterReflectionFeature (Surface
        // Depth), so transparent objects after the water, such as a height fog overlay, see the water surface and
        // not the ground under it.
        Pass
        {
            Name "WaterDepth"
            Tags { "LightMode"="LoogaWaterDepth" }
            ColorMask 0
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex WaterVertex
            #pragma fragment WaterDepthFragment
            #pragma multi_compile_instancing
            #pragma shader_feature_local _OCEAN_WAVES
            #pragma multi_compile_local _ _WATER_FALL

            #include "Packages/com.loogasoft.loogaterrain/Runtime/Water/Shaders/LoogaWaterSurface.hlsl"
            ENDHLSL
        }

        // Writes the depth of the solid parts of a fall sheet into the soft depth texture of
        // LoogaWaterReflectionFeature (_LoogaSoftDepth), not into the camera depth. Effects such as Looga/Water Mist
        // fade out in front of it, so they have no hard cut at the edges of the jets. The feature draws only the
        // queues after the water, so the water bodies do not draw this pass.
        Pass
        {
            Name "SoftDepth"
            Tags { "LightMode"="LoogaSoftDepth" }
            ColorMask 0
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex WaterVertex
            #pragma fragment FallDepthFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ _WATER_FALL

            #include "Packages/com.loogasoft.loogaterrain/Runtime/Water/Shaders/LoogaWaterSurface.hlsl"
            ENDHLSL
        }
    }
    FallBack Off
}
