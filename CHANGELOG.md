# Changelog

## 0.1.0-preview.24

- `MicroVersePlacementBridge` captures object outputs after a scene load when MicroVerse is idle. It requires
  `IsHeightSyncd` only after it sees a MicroVerse update start.
- `MicroVerseVegetationBridge` resumes the providers when MicroVerse stops modifying and spawning. It no longer waits
  for `IsHeightSyncd`, which MicroVerse 1.7.0 leaves false after the first update of a session, so native vegetation
  stayed active after each scene load. The providers still wait for unsynchronized heightmap changes.
- Add Data mode to `MicroVersePlacementBridge` (`ConfigureData`). It copies MicroVerse object stamp outputs into one
  Looga Instancing `InstanceSet` and hides or strips the spawned render parts. Colliders stay. It requires the
  MicroVerse objects module and does not wait for `IsHeightSyncd`. Scene Objects mode stays the default.
- The vegetation wind, interaction, ground and lighting includes moved to Looga Instancing `ShaderLibrary`.
  `VegetationInput.hlsl` includes `VegetationDeformation.hlsl` from there. Custom shaders that included the files from
  `Runtime/Instances/Shaders` must use `Packages/com.loogasoft.loogainstancing/ShaderLibrary/`. Rendering does not change.
- Vegetation Lit adds Gust sheen (brightness, gloss, view dependence), off by default.
- Add Outer Row Height to basalt escarpments. It replaces the fixed 0.72 height multiplier of the outer rows, which
  stays the default. Show the Height Distribution curve in the Height section of the inspector.
- Add Fill Gaps to basalt escarpments. It places a fitted pillar in each gap that neighbour pillars enclose. It is off
  by default, so existing groups do not change.
- Basalt escarpments define the pillar size as a Radius range instead of a Thickness range. Saved Thickness data
  migrates to a radius of half the width. `BasaltColumnPlacement.Radius` returns half of `Thickness`.
- Add Edge Radius Scale and Edge Width, which make pillars smaller toward the outer edge and the ends of a group, and
  Radius Jitter and Radius Noise Scale for per-pillar and slow size variation.
- The basalt layout places each row along the spline at a distance set by the local pillar radius, instead of one
  lattice sized by the smallest pillar. Pillars that overlap by more than 45 % of their summed radii are rejected.
- Organize the Basalt Escarpment inspector in Spline, Pillars, Height, Wall, Surface and Advanced sections.
- Add a full-quality Scene view vegetation preview driven by editor cameras.
- Restore native authoring visibility during terrain selection, saving, compilation, and Play transitions.
- Restore native foliage visibility before player builds, so Scene view preview state cannot enter player data.
- Expose required and missing streaming cells, and the time and observer position of the latest streaming update.
- Use static-transform renderers for streamed vegetation. Read terrain size and origin once for each staged cell.
- Stream the Scene view preview while the camera moves. Terrains within detail range of a Scene view camera update first, least recently updated first.
- Limit Scene view preview updates by CPU time only. Record, upload and residency caps no longer apply to the preview.
- Add density falloff fields to vegetation settings, source overrides and the quality inspector.
- Preserve runtime observers and authored density while the preview owns streaming.
- Organize the World Controller with Toolkit foldouts, source preview tiles, and grouped source overrides.
- Avoid repeated Scene view repaints while a vegetation preview provider remains blocked.
- Add an internal GPU detail generator for GPU-resident renderers. It writes world transforms with the
  Reference distribution. It reads heights and holes from the terrain textures, so the GPU sees the current surface.
  The CPU reads painted density and counts candidates for each texel only.
- The GPU detail generator counts the candidates of each page in a Burst job. The counts are identical to the
  managed loop. A 32 by 32 patch block with 5 layers takes 14.6 ms instead of 54.7 ms in the editor.
- Share the detail placement hash in `Shaders/DetailPlacement.hlsl` for the readback and GPU-resident kernels.
- Add `VegetationSettings.GpuResidentDetails`. Streaming providers then generate mesh detail cells on the GPU.
  Trees, texture details and explicit page sources keep the CPU path. The inspector shows the switch.
- GPU-resident streams keep drawing loaded cells after the first complete update. They do not return to native
  details while new cells load. `TerrainVegetationProvider.IsRendering` reports this state.
- Generated detail placement now fills the whole texel. `positionJitter` no longer narrows the spread, because a
  narrower spread left empty bands at texel edges that showed a 2 m grid from above. All backends use this rule.
- Add `TerrainVegetationProvider.GpuResidentDetailsActive` and `DetailStorageSummary` diagnostics.

## [0.1.0-preview.23] - 2026-09-23

- Add shared detail and tree quality profiles with stable source overrides.
- Add a world vegetation catalog with source previews, terrain usage, and separate source, resident, and sampled camera counts.
- Apply runtime quality changes without rebuilding source buffers. Extend streaming residency to the effective source view range.
- Preserve native terrain placement data and local settings when no shared profile is assigned.


## 0.1.0-preview.22 - 2026-09-22

- Add spline-authored procedural basalt arches as continuous closed polygonal columns.
- Rebuild arch geometry automatically when spline knots or shape parameters change.
- Support deterministic cross-section wear, longitudinal scale variation, basalt material data, shadows, and optional mesh collision.
- Add an editor creation command and focused curved-spline and determinism tests.

## 0.1.0-preview.21 - 2026-09-21

- Add editable basalt openings for waterfalls, roads, and other wall crossings.
- Support recessed pillar tops with a smooth shoulder transition and local variation.
- Support a front clearance band so waterfall sheets remain visible over the pillars.
- Allow front columns to move behind the water surface and form a visible basalt substrate.
- Blend side columns toward the recessed substrate across the opening shoulders.
- Recalculate layout-aware contact occlusion after an opening changes the wall.

## 0.1.0-preview.20 - 2026-09-21

- Widen vertical edge highlights with a soft fade anchored to the chamfer geometry.
- Make primary fracture regions common while retaining rare intact patches.
- Add a maximum-height scale and a stronger upper-biased height distribution.
- Compute directional neighbour contact weights for every generated pillar.
- Apply contact occlusion to indirect lighting in editor previews and runtime instances.

## 0.1.0-preview.19 - 2026-09-21

- Seed masked crack regions independently for every pillar.
- Preserve pillar-local crack coordinates and seeds in combined editor previews.
- Increase the primary fracture scale and strengthen fine secondary fractures.
- Align vertical highlights to the actual chamfer faces without broad-face spill.
- Add soft, noise-broken highlights around the upper side bevel and top-cap rim.

## 0.1.0-preview.18 - 2026-09-21

- Replace solid dark chamfer strips with soft noise-broken edge highlights.
- Separate large and fine crack layers with independent procedural region masks.
- Preserve broad crack-free areas while allowing sparse and dense fracture patches.
- Keep masked cracks continuous across pillar faces and compatible with near-field parallax.

## 0.1.0-preview.17 - 2026-09-21

- Vary column diameters across a wider 18 to 32 meter range.
- Derive column heights from the local escarpment height.
- Keep the shortest columns at 60 percent of the wall height.
- Allow the tallest columns to extend 50 meters above the wall.
- Preserve the authored distribution curve so most columns remain near the upper height range.
- Blend sparse multi-scale cracks continuously across pillar faces.
- Break visible detail-normal tiling with warped secondary sampling.
- Add dedicated joint cavity shading for clearer borders between columns.

## 0.1.0-preview.16 - 2026-09-21

- Draw low-cost crack cavities at long range and crossfade to parallax cracks near the camera.
- Extend the default parallax crack range to 190 meters.
- Add crack ambient occlusion and optional URP SSAO and Looga GTAO support.
- Anchor leaned columns at their bottom centers so top directions can vary without moving the shared base plane.

## 0.1.0-preview.15 - 2026-09-20

- Replace the original basalt draft generator with a spline-driven columnar basalt system.
- Generate interlocking six-, seven-, and eight-sided columns through Looga Instancing without per-column GameObjects.
- Add curve-controlled height and thickness distributions, density, noisy wall depth, end taper, boundary dropout, wear, lean, and deterministic crack variation.
- Add safe local spline previews so large walls can be reviewed in sections before a full build.
- Build the bounded local preview automatically when an enabled authoring component becomes active.
- Render edit-mode previews as temporary combined meshes so Scene View authoring is stable and inspectable; keep Looga Instancing for play mode.
- Add a purpose-built URP basalt shader with procedural color, joints, edge wear, and crack accents.
- Add deterministic layout and polygonal mesh tests.
- Pack columns on a shared, tangent-aligned hex lattice with a common spline-relative bottom plane.
- Increase the authored default diameter to 20–25 m and seal cap rims to the exact side-wall vertices.
- Sample curved splines at true world-space distance and keep columns vertical by default so the lattice remains joined around bends.
- Close the polygon wrap seam by sharing the first corner profile with the final side face.
- Add deterministic mid-poly edge wear with irregular chamfer strips, localized corner chips, uneven top rims, and shared watertight ring geometry.
- Keep worn rim bands narrow on tall columns and use eight vertical segments by default for cleaner fracture transitions.

## 0.1.0-preview.14 - 2026-09-20

- Skip procedural basalt columns where no authored Unity terrain exists beneath the wall face.
- Prevent full-height basalt slabs from spanning ocean, missing tiles, or unloaded terrain gaps.

## 0.1.0-preview.13 - 2026-09-20

- Add a spline-like basalt escarpment authoring component with editable control points.
- Generate deterministic five-sided, six-sided, and seven-sided column clusters from reusable meshes.
- Render generated columns through Looga Instancing without creating one GameObject per column.
- Fit column bases to Unity terrains and support terraces, lean, fractures, optional combined collision, and deterministic seeds.
- Add a focused determinism and finite-transform test for procedural column placement.

## 0.1.0-preview.12 - 2026-09-20

- Add approved scene-instance roots to the world controller and configure one shared Looga Instancing provider without scanning unrelated meshes.
- Add optional one-click terrain virtual texturing, including generated shared settings, feedback compaction, compression, and matching MicroSplat page producers.
- Connect explicitly assigned MicroVerse object outputs to synchronized Looga Instancing providers.
- Keep unsupported and unassigned scene objects on native rendering and preserve controller-owned restore behavior.

## 0.1.0-preview.11 - 2026-09-20

- Add one-click scene setup through LoogaWorldController.
- Discover terrain tiles and base cameras without changing TerrainData.
- Configure terrain surfaces, vegetation providers, shared visibility, and water camera support.
- Export compatible MicroSplat surfaces and connect MicroVerse terrain and vegetation updates automatically.
- Preserve existing components and remove only controller-owned components when native rendering is restored.
- Keep generated consumer assets for repeatable setup and stable asset identifiers.

## 0.1.0-preview.10 - 2026-09-20

- Complete the Pass 12 compatibility and release-qualification fixtures.
- Initialize shared visibility explicitly in the terrain Hi-Z integration test.
- Keep conservative prior-frame culling and pixel-preservation checks separate from the large rejection benchmark.

## 0.1.0-preview.9 - 2026-09-19

- Consume the shared adaptive-quality state for terrain patch error and projected size.
- Apply adaptive vegetation density, pixel, LOD, and shadow limits to derived render data only.
- Keep TerrainData, native fallback, render scale, and authoring-tool ownership unchanged.

## 0.1.0-preview.8 - 2026-09-19

- Consume compressed, budget-aware vegetation pages from Looga Instancing.
- Add predictive interest prefetch and independent read, decompression, residency, upload, CPU, and GPU limits.
- Keep native TerrainData vegetation visible until required cells and GPU uploads are complete.

## 0.1.0-preview.7 - 2026-09-19

- Update Looga Instancing for interactive proxy ownership and general impostor assets.
- Keep TerrainData and MicroVerse vegetation inputs unchanged.
- Preserve native vegetation fallback when proxy or impostor features are unused.

## 0.1.0-preview.6 - 2026-09-19

- Integrate deterministic compute detail placement with streamed TerrainData vegetation.
- Keep native detail prefabs and MicroVerse-written density as the authoring inputs.
- Support mesh, cross-card, and billboard detail prototypes through the existing prototype compiler.
- Refresh only changed height, hole, and detail regions and keep native rendering during page warmup.
- Preserve explicit placement sources and the native detail-placement path.

## 0.1.0-preview.5 - 2026-09-19

- Consume Looga Instancing's camera-scoped visibility context for terrain Hi-Z.
- Share stack and scope ownership with instances without a second depth pyramid.
- Register terrain separately in shared visibility diagnostics.

## 0.1.0-preview.4 - 2026-09-19

- Publish resident and streamed TerrainData trees and details into Looga Instancing's shared world-cell registry.
- Keep tree and detail layouts independent so later visibility, streaming, impostor, and HLOD passes can use content-specific cell sizes.
- Preserve native TerrainData authoring and fallback ownership; the registry is derived runtime metadata only.

## 0.1.0-preview.3 - 2026-09-19

- Report TerrainData tree/detail and MicroVerse ownership through the shared Looga Instancing adapter status.
- Keep complete native terrain vegetation visible during MicroVerse synchronization, unsupported prototype fallback, streaming warmup, and competing renderer ownership.
- Restore the terrain's prior Draw Trees And Foliage state when a provider is disabled.
- Add focused TerrainData transfer, unsupported-prototype and disable/restore coverage.

## 0.1.0-preview.2 - 2026-09-19

- Add adaptive terrain Hi-Z participation and adaptive shadow batching.
- Batch compatible built-in terrain surfaces into shared texture arrays and one indirect submission.
- Add projected-quality controls and asynchronous patch and triangle metrics for native-parity tests.
- Localize terrain RVT feedback to page-cell, view, projection and invalidation changes.
- Add optional single-level RVT sampling and an RGBA8 material cache mode.

- Replace the terrain-only full-resolution Hi-Z redraw with Looga Instancing's shared prior-frame opaque-depth pyramid.
- Share one depth hierarchy between terrain patches and vegetation, and retain camera-cut and stale-history fallbacks.

- Use native terrain fallback for Global Normals and Geomap combinations that do not reproduce native MicroSplat output; retain native tessellation and displacement.
- Require configured Global Tint, Texture Clusters and Anti-Tile module textures before direct rendering.
- Qualify holes, snow, streams, wind, texture clusters, Anti-Tile, triplanar, parallax and terrain blending on D3D12 Deferred+.
- Preserve native MicroVerse 1.7 road shader builds by removing invalid low-target DOTS variants in the optional editor integration.

- Complete the graphics-test pipeline switch before releasing fixture resources in headless editors.

- Add separate tree/detail visibility and quality policies to resident and streamed TerrainData providers.
- Expose upload and camera draw diagnostics for controlled vegetation benchmarks.
- Declare engine-module dependencies required by clean consumers.

- Move general BRG rendering, mesh placement, containers and SpeedTree profiles into the shared Looga Instancing dependency.
- Preserve script GUIDs, serialized type migration and placement IDs; retain terrain-specific providers and ground adapters.
- Complete mesh masks, snapshots, collider/edit ownership and public road, spline, MicroVerse and RAM source workflows.
- Qualify direct and cached ground receivers and Graphics Pro material parity in Forward+ and Deferred+.

- Remove the FR2 wind integration setup and its validation assets; retain Looga-owned wind and the optional Unity SpeedTree bridge.

- Add explicit SpeedTree and custom global-wind material profiles without replacing source shaders.
- Share native SpeedTree wind proxies and current/history property buffers across resident cells.
- Add SpeedTree percentage LOD selection and preserve native LOD-group tree disappearance distances.
- Add bounded terrain vegetation cell residency, prefetch hysteresis and native fallback while loading.
- Release cell GPU resources on eviction and preserve ownership during synchronization or observer failures.

- Add Looga Vegetation Lit with bounded wind and matching surface, depth, normal and shadow deformation.
- Add distance-based GPU LOD crossfades using Unity's packed visibility contract.
- Add separate vegetation shadow distance and minimum shadow LOD controls.
- Exclude inactive prototype children and reject unsupported animated or SpeedTree fade modes.
- Validate fixed wind phases, native image parity and shadow controls in an isolated D3D12 player.

- Add opt-in native terrain tree and instanced mesh-detail providers with complete-population fallback.
- Refresh bounded detail patches after paint, height and hole changes without rewriting TerrainData.
- Match terrain tree LOD distance to the native placement pivot.
- Add optional public-event MicroVerse suspension, source diagnostics and Undo refresh.

- Add an optional static-instance BRG core with GPU visibility, basic LOD and stable handles.
- Keep instance transforms resident and upload only changed slots.
- Add explicit containers, mirrored/submesh support and isolated native/player comparisons.
- Limit initial material support to opaque/cutout URP Lit and Unlit on Windows/D3D12.

- Tighten stitch-aware frustum bounds and rebuild metadata only for bounded height edits.
- Add opt-in current-frame terrain Hi-Z with per-camera resources and native-hole support.
- Add independent light-aware shadow culling and nearby cross-tile shadow batches.
- Preserve BC4 player hole textures and original passes for material-specific clipping.
- Compact GPU feedback into unique leaf counts with a complete-feedback overflow fallback.
- Skip redundant cache lookups in direct close-range MicroSplat shading.
- Add moving-camera, hole-edit, feedback-overflow and world-player comparisons.

- Add optional GPU BC3 terrain cache storage while retaining HDR emission and material channels.
- Add Burst worker feedback decoding, sorting, and leaf aggregation with nonblocking consumption.
- Retain uncompressed and serial modes for fallback and comparison.
- Add compression accuracy and lifecycle tests plus the four-mode world benchmark.

## 0.1.0-preview.1

- Separate direct MicroSplat module support from static-cache eligibility.
- Guard unassigned dynamic stream maps and mismatched generated surface/producer pairs.
- Defer feedback texture release until the pending GPU readback completes.
- Add module, resource-lifetime and same-view cache regression checks.

- Preserve unaffected surface pages after bounded native height and paint edits.
- Add regional refresh APIs, bounded source-change history and conservative full-tile fallbacks.
- Verify page-slot retention and full-image equivalence; benchmark localized refreshes in the current world.

- Add an optional Graphics Pro clipmap writer export that reuses the existing MicroSplat page producer.

- Add native MicroSplat terrain-blend refresh after control, height and source changes.
- Normalize redundant MicroSplat URP markers and reject object shaders in terrain exports.
- Add an opt-in URP water camera component with reversible texture options.
- Verify MicroVerse roads, native trees/details and RAM water with direct and cached terrain.
- Add camera serialization and native vegetation ownership regression checks.

- Add conservative integration diagnostics and native fallback with automatic recovery.
- Synchronize native MicroSplat material values without changing shared exported assets.
- Record source shader and feature metadata in exported surfaces.
- Separate surface refresh, geometry refresh and live per-draw bindings.
- Coordinate MicroVerse proxy ownership, membership and public authoring events.
- Track native texture revisions and editor Undo; clear removed live property overrides.
- Add material, fallback, proxy and authoring regression checks.
- Restrict Looga rendering to Direct3D 12 until the Direct3D 11 buffer issue is corrected.

- Add GPU terrain patch selection and indirect rendering.
- Add native TerrainData height and hole binding.
- Add URP surface, depth, normal, and shadow passes.
- Add optional MicroVerse completion-event integration.
- Add a version-checked MicroSplat 3.9 shader exporter.
- Add an isolated two-tile validation scene builder.
- Add GPU coverage, LOD edge, height sampling, culling, and lifecycle tests.
- Preserve native visibility across scene save and reload.
- Add an isolated player benchmark with timing reports and comparison captures.
- Document native/GPU benchmark results and remaining lighting integration limits.
- Add standard URP Deferred+ G-buffer capture and lighting validation to the benchmark.
- Add tile-count, heightmap, terrain-paint and layer-count benchmark options.
- Record active CPU thread timing and optional per-frame selection/submission samples.
- Register the renderer profiler marker explicitly for standalone sampling.
- Document the 64-tile and higher-resolution results without a performance-improvement claim.
- Add cached GPU height bounds and geometric-error LOD selection.
- Stitch adjacent tile LODs without forcing minimum border patch size.
- Share camera state and cache source and neighbor bindings.
- Preserve MicroSplat per-pixel terrain normals in exported shaders.
- Refresh cached bindings through native callbacks and the MicroSplat bridge.
- Add narrow-peak, flat-terrain, orthographic, and four-direction tile-edge tests.
- Add detailed CPU scopes, error tolerance, and 256-tile benchmark controls.
- Validate authoring refresh, Deferred+ surfaces, higher resolutions, and a 256 square kilometre stress case.
- Document measured improvements and the remaining native terrain advantage.
- Cache per-camera selection commands and remove repeated invariant parameter binding.
- Combine surface and shadow submissions for fully visible tiles.
- Omit offscreen surface submissions while retaining shadow casters.
- Record indirect submission counts in benchmark reports.
- Validate camera switching, culling changes, source edits and renderer reconfiguration.
- Batch compatible tiles into shared GPU selection and indirect argument buffers.
- Copy revised node metadata on the GPU without a CPU height readback.
- Retain per-tile surface bindings and per-camera output ownership.
- Add mixed-tile selection, counter reset, and shared-offset edge tests.
- Add terrain shadow casting controls and a paired benchmark option.

- Add optional runtime virtual-texture surface caching with a fixed physical page budget.
- Add asynchronous terrain feedback, coarse-page fallback, eviction and bounded page fills.
- Preserve direct shading for close detail and unavailable cache pages.
- Invalidate cached surfaces through existing source refresh callbacks.
- Add settings assets, cache statistics and an inspector refresh control.
- Add an optional static-feature MicroSplat virtual-surface and page-producer exporter.
- Add CPU page tests and GPU multi-slice, edit, eviction and lifecycle checks.
- Add direct/virtual comparisons, cache-budget controls and sustained moving-camera benchmarks.
- Validate Direct3D 12 Deferred+ rendering at 64 tiles and under higher-resolution cache pressure.
- Document CPU feedback overhead, source-memory limits and the failed Direct3D 11 player geometry check.

- Support Kubera's full MicroSplat feature combination in the virtual-surface exporter.
- Cache stable base layers and apply detail, distance and normal noise after cache sampling.
- Preserve hybrid height blending through direct shading and crossfade to its cached distance endpoint.
- Support height noise and FBM without removing source shader features.
- Recover MicroSplat XY normals before live noise composition.
- Add six optional feature-compatibility tests and document material synchronization.
- Validate direct/cached rendering on four real world tiles in an isolated preview.
