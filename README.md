# Looga Terrain

Looga Terrain 0.1.0-preview.15 is a Unity 6.3 URP renderer prototype. It reads native TerrainData and retains native TerrainColliders.

The GPU selects terrain patches and writes indirect draw arguments. The renderer submits the visible surface and a separate shadow list.
The core has no MicroSplat or MicroVerse references. Integrations have separate assemblies with package version constraints.

General BRG rendering, containers, mesh painting and scene-object ownership come from the shared **Looga Instancing** dependency.
Terrain vegetation providers and ground/tool integration remain in this package. See [instance documentation](Documentation~/instances.md).
Resident and streamed TerrainData trees and details publish derived occupancy to the shared world-cell registry after uploads complete. This does not change TerrainData, MicroVerse authoring, native fallback, or collider ownership.
Terrain patch Hi-Z uses Looga Instancing's shared camera visibility context. It does not create a second depth pyramid when instances use the same camera scope.

## Requirements

- Unity 6000.3 and URP 17.3
- Direct3D 12, compute shaders, and GPU instancing. Direct3D 11 currently falls back to native terrain.
- Axis-aligned terrains with world scale one
- Adjacent converted tiles with equal physical size and patch settings

## Setup

1. Add the package with Unity Package Manager. For local development, select this package's `package.json`.
2. Select **GameObject > Looga > Create and Configure World**.
3. Review the report on `LoogaWorldController`.
4. Save the scene after the report shows the expected terrain and camera counts.

The controller discovers every terrain and base camera in its scene. It adds terrain surfaces, vegetation providers,
camera support, and the shared Hi-Z renderer feature. Optional setup extensions configure installed MicroSplat and
MicroVerse integrations. Generated materials stay under `Assets/Looga/Generated/Terrain` by default.

For authored scene vegetation, rocks, or repeated props, drag only their top-level static or LODGroup objects into
**Scene Instance Roots**. The controller creates one shared `SceneInstanceProvider`; supported roots move to BRG and
unsupported roots stay on native rendering with a report. Looga never scans all scene meshes or takes ownership of
buildings, characters, transparent objects, or interactive props implicitly.

Assign generated MicroVerse object containers to **MicroVerse Object Outputs** when those objects should use the same
instancing path. The controller connects their refresh to MicroVerse synchronization. Leaving the list empty keeps
those outputs native.

Enable **Use Terrain Virtual Texturing** when the surface cache is useful for the target world. The controller creates
one shared settings asset with GPU feedback compaction, parallel feedback processing, and cache compression. Basic
Looga surfaces use their own producer. Compatible MicroSplat terrains receive matching generated virtual surfaces and
page producers. Leave this disabled for direct shading; it is a measured quality/performance choice rather than a
requirement for Looga Terrain.

Select **Restore Native Rendering** to remove only components and renderer features created by the controller.
The command preserves existing components, TerrainData, colliders, source materials, and generated assets.

Advanced users can still add components directly:

1. Add `TerrainRenderer` to a native Terrain. Keep the Terrain and TerrainCollider enabled.
2. Assign `Runtime/Shaders/TerrainSelection.compute` and a material that uses `Looga/Terrain/Lit`.
3. Select **Apply Renderer Settings** after resource or patch-count changes.
4. Disable `TerrainRenderer` to restore the original native heightmap visibility.

Use **Tools > Looga > Terrain > Create Validation Scene** to create two synthetic test tiles.

The sample opens additively and restores the previous active scene. It uses layer 31 for its camera. The camera is disabled until explicitly rendered or enabled.
No world terrain is converted automatically. No heightmap, alphamap, or collider is replaced by the renderer.

## Spline basalt escarpments

Add **Looga Terrain > Basalt Escarpment** to a GameObject and assign a Unity `SplineContainer`, or use
**GameObject > Looga > Create Basalt Escarpment** to create both together. The spline marks the top edge of the wall.
Columns extend vertically downward from it; the generator never rotates a combined cliff mesh onto the spline.

Set the pillar Radius range in the Pillars section. The radius is measured from the center to a corner. The size
changes slowly across the group within the range, and Radius Jitter varies each pillar. Edge Radius Scale and Edge
Width make the pillars smaller toward the outer edge and the ends of the group. Each row of pillars follows the spline
at a distance set by the radius of the rows in front of it, so large and small pillars pack without large gaps.
Heights follow the cliff below the spline when Relative To Cliff is on. Depth and Density in the Wall section control
the group footprint, and Outline Noise and End Taper break up the silhouette. Rarely used settings are in the
collapsed Advanced section. Data saved with the older Thickness range loads as a radius of half that width. Six-, seven-, and eight-sided columns share a small set of worn and cracked meshes and render through
Looga Instancing.

Use **Build Local Preview** while authoring. Preview Center and Preview Length isolate a short spline section so an
incorrect direction or scale cannot fill the scene. Use **Build Whole Spline** only after that section is approved;
the editor asks for confirmation. **Clear Preview** releases every generated instance and temporary mesh. Generated
previews do not edit terrain data or the source spline. Terrain base clamping is optional and is disabled by default
because a spline on a cliff rim usually samples the upper plateau rather than the lower basin.

## MicroSplat integration

Install MicroSplat 3.9 and its URP module. Select a synchronized MicroSplat terrain, then use **Tools > Looga > Terrain > Export Selected MicroSplat Surface**.
The command creates a separate shader and material under consumer Assets. Assign that material to `TerrainRenderer` and add `MicroSplatTerrainBridge` to the same tile.
The bridge binds the tile's native controls, holes, and normal textures. The layer order must match the MicroSplat texture array.

The exporter replaces geometry in the generated 3.9 URP shader. It preserves fragment shading and does not modify vendor code or source materials.
The exporter retains per-pixel terrain normals when it removes native instancing. Re-export existing Looga derivatives after this update.
After MicroSplat feature changes, export again. The `Export` API can update its own assets with `updateExisting: true` and retain their GUIDs.
Vertex displacement and tessellation keep native terrain rendering. Custom control maps and features outside the explicit policy also keep native rendering. The installed-module qualification scope is listed below.
Existing generator warnings about deprecated URP keywords and duplicate pragmas can remain in the derivative shader.
Do not distribute generated MicroSplat shaders with this package. Consumers must own the required MicroSplat modules.

### Compatibility guards and material synchronization

Use **Tools > Looga > Terrain > Inspect Selected Compatibility** to inspect a native MicroSplat terrain.
The bridge requires export metadata that matches the native shader and feature asset.
Missing metadata, a changed feature set, unsupported modules or more than 32 native layers restore native rendering with a diagnostic.
Re-export existing surfaces before using automatic synchronization. Keep each virtual surface paired with its producer.
The admission policy is conservative for both direct and cached shading. Rejected modules are not silently removed.

The bridge observes the public MicroSplat material-sync event and material-instance replacement.
Property changes update the runtime surface and its page producer without editing the shared exported assets.
Surface refresh does not rebuild terrain geometry. Source shader or feature changes still require a new export.
Core integration guards allow each optional package to report and clear its own blocking reason.


## Roads, object blending, vegetation and water

These integrations keep the native authoring components and data. Looga replaces only the terrain surface.
MicroVerse Roads and RAM remain optional. The core does not reference their assemblies.

### MicroVerse Roads and vegetation

Keep RoadSystem, Road and the generated road meshes. Keep MicroVerseTerrainBridge on the authoring root.
Use MicroVerse's normal road regeneration and terrain update commands.
The bridge refreshes Looga after MicroVerse publishes terrain changes. Road meshes continue to use their native materials.

MicroVerse TreeStamp and DetailStamp continue to write native tree instances and detail layers. Keep the native Terrain enabled because it remains the authoring and fallback source.
Add `TerrainVegetationProvider` when those trees and mesh details should use Looga Instancing. The provider reads TerrainData without rewriting it, transfers visibility only after the complete population is ready, and restores the prior Draw Trees And Foliage state when disabled. Unsupported prototypes, active vendor owners, streaming warmup, and MicroVerse synchronization retain native rendering with a diagnostic.
Use `MicroVerseVegetationBridge` to suspend the provider while MicroVerse publishes terrain changes. `MicroVersePlacementBridge` performs the same reversible handoff for generated object roots and refreshes painted exclusion masks after synchronization.
Use tree prefabs with qualified mesh, LOD and wind shaders. Instanced details require a supported mesh prefab and material. Unsupported populations stay native rather than disappearing.

### MicroSplat Terrain Blending

Enable Terrain Blending in the native MicroSplat material and generate its native object-blend shader.
Re-export the Looga terrain surface and its matching page producer.
Keep MicroSplatTerrainBridge on each tile and MicroSplatBlendableObject on each blended mesh.
Keep the native object-blend shader on meshes. Do not export that object shader as a Looga terrain surface.

The bridge updates the native blend instance after control-map, height or TerrainData changes.
Shared blend materials and exported assets remain unchanged.
The tested scope includes the base Terrain Blending feature with the existing supported terrain features.
Additional blend variants remain outside the admission policy until validated.
Object shading remains native and direct when the terrain uses virtual texturing. Cache filtering can introduce small appearance differences.

### RAM water

Keep RamSpline, its generated water mesh and its native water material.
RAM carving and painting continue to target native TerrainData.
Add **Looga > Terrain > Water Camera** to each URP base camera that needs depth-based water and refraction.
The component requests depth and opaque textures. Disabling it restores the previous options, unless those options were explicitly changed.
The component does not change the pipeline asset or edit water flow, river profiles or terrain.

The combined fixture passed native, direct Looga and cached Looga captures in standard URP Deferred+ on Direct3D 12.
It contains two 64-metre terrains, a regenerated curved road, native trees, instanced details, a blended object and a RAM river.
Separate visibility checks confirmed tree, detail, water and object-blend rendering.
Road regeneration, control-map growth, height edits and TerrainData replacement refreshed correctly.
These checks do not qualify all RAM shaders, underwater effects, reflections, camera stacks, XR or third-party vegetation renderers.
A combined standalone-player qualification and a separate whole-world benchmark are available. They cover the stated fixture and material profiles, not every asset variant.

## Optional Graphics Pro world clipmaps

Graphics Pro RVT can capture native MicroSplat terrain through the existing Looga Terrain page producer.
This is a separate world-space receiver cache. The terrain page cache below remains unchanged.

Use **Tools > Looga Terrain > Export Graphics Pro Clipmap Writer** with an existing page producer and its matching native material.
Assign the exported material to **Terrain Writers** on the Graphics Pro RVT renderer feature.
The adapter reuses the producer surface code and its conservative feature validation.
It does not require TerrainRenderer or TerrainVirtualTexture components on the source terrain.
Native height, holes, normal and control textures remain read-only.
The stable base surface is captured; omitted camera-dependent effects are not added back.
See Graphics Pro's runtime virtual texturing documentation for atlas channels, cache refresh and tested limits.

## Runtime virtual texturing

Virtual texturing caches blended surface properties before lighting. Native TerrainData and source texture arrays remain the authoring inputs.
The cache stores albedo, smoothness, normals, occlusion, emission and metallic values.
Lighting, holes and geometry still update through the normal renderer.

1. Create an asset with **Create > Looga > Terrain > Virtual Texture Settings**.
2. Assign the package's **Runtime/Shaders/VirtualTexturing/VirtualTextureFeedback.shader** to its Feedback Shader field.
3. Add **TerrainVirtualTexture** to each participating Looga terrain.
4. Assign the same settings asset to tiles that must share one cache.
5. Assign a matching page-producer material. The basic **Looga/Terrain/Lit** material can also serve as its own producer.
6. For MicroSplat, follow the export steps below and assign the separate generated producer.
7. Use the component inspector to inspect page use, memory, pending requests and evictions.

Disable **TerrainVirtualTexture** to restore direct surface shading. Disable **TerrainRenderer** to restore native terrain rendering.
The feature is opt-in. The sample builder does not convert existing world terrain.

The default settings use 16384 virtual texels per tile and 512 physical pages.
Each page contains 128 by 128 texels and a four-texel border.
Three uncompressed surface arrays use 180.625 MiB at 512 pages, or 45.15625 MiB at 128 pages.
This physical allocation does not grow with terrain count. Each tile also needs an 87380-byte page table at the default virtual resolution.
Feedback targets, source textures and geometry use additional memory. The inspector reports physical cache memory only.

Reduced-resolution feedback identifies visible terrain pages. Asynchronous readback sends requests to a bounded CPU page scheduler.
The scheduler fills coarse pages first and replaces pages outside current demand.
The default fill limit is 16 pages per frame. Feedback runs every four frames at one-eighth resolution.
Missing pages use a resident parent page or direct shading. Cache pressure can reduce surface detail.
**Preserve Close Detail** uses direct shading when a pixel needs detail finer than the virtual texture resolution.
At one kilometre per tile, the default virtual texel spacing is approximately 6.1 centimetres.

Feedback is requested after invalidation, a virtual page-cell crossing, a projection change, a 0.5-degree camera rotation, or 120 stationary frames. Configure these limits with `VirtualTextureSettings.ConfigureFeedbackPolicy`. This avoids repeated feedback readback for an unchanged view.

Disable **Blend Mip Transitions** to sample one virtual level per pixel. Disable **High Dynamic Range Material Cache** to store the material/emission page in RGBA8 instead of RGBA16F. Both are quality choices and stay enabled by default. Validate banding, bright emission and moving-camera transitions before using the lower-bandwidth modes.

## Submission and quality controls

The built-in `Looga/Terrain/Lit` surface can merge compatible tiles into one indirect surface submission. Surface batching starts at four tiles by default. It excludes MicroSplat derivatives, terrain RVT receivers and integration-bound materials because those surfaces need per-tile bindings. Use `SetSurfaceBatching` to change the threshold.

Shadow batching is also adaptive and starts at four compatible tiles. Small groups retain per-tile shadow submissions to avoid texture-array setup overhead. Configure it on `TerrainOcclusionCamera`.

Use `SetProjectedQuality` to set the projected patch target used for native-parity comparisons. `RequestQualityMetrics` reports visible and shadow patch counts plus estimated triangle counts. Compare native and Looga with the same camera, render scale, shadow distance, material, and screen-space error before treating timing differences as renderer savings.

Native height and terrain-paint callbacks invalidate intersecting surface pages across mip levels. Filtering borders and normal-derivative neighbors are included. Other page slots remain resident. A bounded version history combines changes until each cache observes them.

Call **TerrainRenderer.RefreshSurface(Rect)** after a known regional source-texture edit that needs binding refresh. The rectangle uses normalized terrain coordinates. Call **TerrainVirtualTexture.Invalidate(Rect)** when the current bindings are valid and only cached pixels need refresh. These APIs do not edit terrain data.

Unknown material changes, source-layout changes, history overflow and integration callbacks without bounds invalidate the whole affected tile. Use **TerrainVirtualTexture.Invalidate()** or **Refresh cached surfaces** for those changes. Coarse pages refill first, within the existing per-frame budget; missing pages retain the parent/direct fallback.

The isolated height and paint checks each invalidated 15 of 85 resident pages. The complete rendered image matched a full rebuild. A 32 by 32 metre refresh benchmark in the existing world required 64 page fills across 16 updates, versus 384 fills for whole-tile invalidation. Geometry metadata still rebuilds at tile scope after height changes; this optimization covers surface caches.

### Optional MicroSplat page producer

Select a synchronized MicroSplat terrain.
Use **Tools > Looga > Terrain > Export Selected MicroSplat Virtual Surface**.
Assign the generated surface material to **TerrainRenderer**.
Assign its **PageProducer** material to **TerrainVirtualTexture**.
Keep **MicroSplatTerrainBridge** on the tile.

The exporter supports the complete feature combination used by the current Kubera MicroSplat 3.9 material.
This includes hybrid height blending, height noise with FBM, detail noise, distance noise and normal noise.
Base blending, stochastic sampling, triplanar mapping and the existing per-texture controls remain supported.
Generated MicroSplat shaders remain consumer assets. This package does not contain licensed generated shader source.

The producer caches base layers before detail, distance and normal noise.
The consumer applies those noise effects once, using the current camera distance and screen derivatives.
Hybrid height blending remains direct while its camera-dependent weights change.
After that blend completes, the shader crossfades to the cached endpoint over another ten percent of the blend distance.
The minimum crossfade width is one metre.
With Kubera's 300-metre setting, direct hybrid blending continues through 300 metres. The cache crossfade ends at 330 metres.
Close-detail fallback can retain direct shading beyond that distance when the screen requires finer texels.
The integration does not promise the same saving at player height as in distant views.

Re-export both surface and producer when updating from the initial static-only integration.
Keep each generated surface paired with its matching producer.
With a native MicroSplatTerrain component, edit and synchronize the native material. The bridge updates the runtime surface and invalidates its cached pages.
The exported assets remain unchanged. Re-export after shader feature changes or to add compatibility metadata.
Without a native MicroSplatTerrain component, the bridge uses static exported values. Call RefreshSurface after edits.

Four real Kubera terrain tiles passed isolated ground and aerial visual comparisons without changing their source data.
Tests also cover near, transition and distant cameras, live detail noise, and material refresh.
The tested combination preserves the world's enabled features. It does not support every optional MicroSplat module.
Unknown features, including wetness, snow, tessellation and per-texture noise strength, still fail export explicitly.

### Virtual-texture benchmark

Assign Virtual Settings and Page Producer on the isolated benchmark runner.
Use **-looga-virtual-texture** to compare Direct/Virtual/Virtual/Direct with identical Looga geometry.
Use **-looga-vt-pages 128** to test a smaller physical cache.
The regular grid, height, control and layer options still apply.
Without **-looga-validation**, view 3 moves throughout 2400 measured frames after 120 warmup frames.
The path advances 0.15 metres along X per frame, with proportional Z movement.
This is a fixed-step stress path. Its real-time speed varies with frame rate.
With **-looga-validation**, view 3 instead checks fixed lighting and hole fixtures.

Set both Graphics and Quality pipelines to the benchmark pipeline before building.
Otherwise, URP can strip required player shader variants. Restore the original settings after the build.

The final Direct3D 12 release checks used standard URP Deferred+, four static MicroSplat layers and an RTX 5080 at 1920 by 1080.
The table averages two pass medians. Times measure the full frame.

| 64 tiles, 1025 heights, 256 controls | Direct GPU ms | Virtual GPU ms | Direct frame ms | Virtual frame ms |
| --- | ---: | ---: | ---: | ---: |
| Ground | 1.15 | 0.68 | 1.34 | 1.30 |
| Middle | 1.12 | 0.30 | 1.28 | 0.89 |
| Overview | 0.35 | 0.17 | 0.84 | 0.93 |
| Moving | 1.17 | 0.32 | 1.35 | 1.00 |

Feedback increased main-thread work. The overview frame became slower despite lower GPU time.
A 16-tile test used 2049 heights, 1024 controls and 128 pages. It completed within the 45.15625 MiB physical budget.
Moving tests exercised eviction. All three final Direct3D 12 cases completed with valid timings and no captured errors.
Page fills stayed within 16 per frame. Image comparisons showed small filtering differences, with larger differences under cache pressure.
These results compare a static material with itself. They do not establish speed relative to native terrain or the complete Kubera material.

The additional Direct3D 11 player check failed visual validation in both direct and virtual modes.
Terrain was absent in both modes. The player reported an incompatible indirect/raw buffer flag combination.
Do not use those timings. Direct3D 11 player support requires a separate geometry-buffer investigation.

### Virtual-texture limits

This is runtime surface caching, not disk texture streaming. Source heightmaps, paint maps and MicroSplat arrays remain resident.
Physical pages are uncompressed. Packed formats and disk page producers remain future work.
The feedback pass includes terrain depth but does not account for occlusion from other scene objects.
CPU feedback processing, sparse demand, rapid camera cuts and long multi-camera workloads need further optimization and validation.
Cache quality depends on virtual resolution, physical capacity, feedback rate and fill budget.
Additional MicroSplat modules, clean optional-dependency absence and Looga Graphics Pro deferred integration remain unverified or unsupported.

## MicroVerse and other authoring tools

Add MicroVerseTerrainBridge on the MicroVerse root or assign its MicroVerse field explicitly.
The bridge discovers Looga renderers on the source terrains by default. Explicit renderer targets remain available.
Use MicroVerse's AlwaysUnity proxy mode. Other proxy modes block Looga rendering with a diagnostic.
An active proxy retains visibility ownership. Looga resumes when the source returns to AlwaysUnity.

The bridge observes public begin, completion, cancellation and terrain-layer events.
It checks CPU height synchronization separately from generation completion. It does not request generation or saveback.
Do not save terrain until MicroVerse modification has stopped and its height synchronization is complete.

The core observes native height and texture callbacks, texture revisions and editor Undo.
Height changes rebuild geometry metadata. Surface-only changes invalidate bindings and cached pages.
Use RefreshSource after external geometry changes. Use RefreshSurface after material changes.
BindMaterialProperties supplies stable source bindings. BindDynamicMaterialProperties supplies live per-draw values.
Live values must not affect the cached base surface. Removing a live binder clears its old property overrides.
The MicroSplat bridge binds the current control textures and provides a live Streams texture binding.
This is a binding foundation; Streams shading and other dynamic modules remain blocked until their shader paths are implemented and checked.

Two isolated adjacent tiles passed MicroVerse generation, RAM carving and painting, layer growth and shrinkage,
height Undo, TerrainData replacement, rendered holes and cached-page refresh checks.
The carved seam matched on both tiles, and the native colliders retained the expected TerrainData.
RAM still owns its water meshes, materials and collisions. Water rendering and all authoring modules are not yet qualified.


## Validation

Enable this package in the consumer manifest's `testables` array. Run the `LoogaSoft.Terrain.EditorTests` EditMode assembly.
Thirty-two core tests cover patch coverage, height-error selection, orthographic selection, culling and height decoding.
They also cover interior and cross-tile edges, native visibility and collider preservation.
Virtual-texture tests cover page addressing, eviction, multiple physical slices, edits, settings changes, close-detail fallback, holes and component lifecycle.
Eleven optional MicroSplat tests cover feature admission, fallback, material synchronization and source replacement.
Four optional MicroVerse tests cover synchronization reporting, proxy transitions and terrain membership.
All 47 cases passed. The final repeat invoked their NUnit methods in place to preserve dirty open scenes.
Seven isolated assembly compiler checks passed for optional-package and player boundaries. These are not clean UPM consumer imports or complete player builds.
Batch tests compare four mixed-setting tiles with independent selection. They check counter resets, buffer offsets, and neighbor edges.
A separate preview-scene check confirmed that a height edit rebuilt selection data, unchanged draws reused bindings, and a hole edit updated the rendered surface. The native collider retained the same TerrainData.

The initial Windows validation used Unity 6000.3.21f1, URP 17.3.0, and an RTX 5080.
Two adjacent 128 m tiles used 129 and 257 height samples. Visual checks covered the basic material, exported MicroSplat material, terrain holes, a shared edge, and received shadows.
Saving and reopening the sample preserved native visibility restoration. The live world remained on its native renderer.
A separate Windows player benchmark used four synthetic 1 km tiles and a standard URP Forward renderer.
At 1920 by 1080 on Direct3D 12, Looga used approximately 1.7 to 2.8 times the native GPU frame time.
The native baseline used pixel error 5. Looga used 64 minimum patches per edge and target patch size 128 pixels.
These settings do not provide equal measured geometric error. The result does not support world rollout or a performance-improvement claim.

## Player benchmark

`Benchmarks/Runtime/TerrainBenchmark.cs` creates a configurable grid of synthetic tiles in a separate player scene.
Assign the camera, compute shader, native material, matching exported material, and terrain layers on the component.
Use a separate benchmark scene with one camera and directional light. Keep the world scene out of the player build.
Enable Frame Timing Stats before building. Restore the original project settings after the build.
Start the player with `-looga-terrain-benchmark -looga-output "<output directory>"` in a visible window.
The runner uses 120 warmup frames and at least 360 measured frames and one second per pass. Each view runs native, Looga, Looga, then native.
Three views produce GPU, active main/render thread, present-wait, and total CPU timing statistics, patch counts, memory counters, JSON reports, and comparison captures. The overview camera scales with grid size.
Synthetic normal textures and explicit native height bindings make the runtime-created native baseline usable in the player.
`TimingValid` checks timing availability, captured errors, and requested G-buffer capture completion. Review the captures separately before accepting a comparison.
Hidden or minimized windows can suppress rendering. Such runs are invalid.
The benchmark measures the full frame, including lighting. It does not isolate terrain-only GPU duration.

Use `-looga-grid 2|4|6|8|16` for 4, 16, 36, 64, or 256 tiles. Use `-looga-height 257|1025|2049|4097`,
`-looga-control 256|1024|2048`, and `-looga-layers 4` to vary source resolution and layer blending.
`-looga-seconds` sets the minimum measured time per pass. Keep the material pattern at a constant world scale.
These controls change native heightmaps and terrain-paint maps. They do not resize MicroSplat surface arrays.
Use `-looga-error 1..16` to set projected height-error tolerance (default 2 pixels).
Use `-looga-no-terrain-shadows` to disable terrain casting in both compared renderers. Shadow reception and other scene shadows remain enabled.
The difference between paired full-frame runs estimates shadow cost. It is not an isolated GPU pass timestamp.
Use `-looga-no-culling` to disable Looga patch-frustum culling. Unity draw culling and native baseline culling remain active.
`SetupMilliseconds` covers tile creation; it does not include all deferred GPU metadata work. Warmup excludes that work from steady-state timings.

For standard URP Deferred+ validation, add `TerrainGBufferCapture` to the separate benchmark renderer and assign it on the runner.
Assign a URP Lit fixture material. Use `-looga-validation` to add a fourth camera view, point lights, pillars and a hole.
The feature captures G-buffer attachments only when requested, outside timing measurements. Diagnostic normal PNGs use UNorm conversion.
Use a Development build and `-looga-profile` to record preparation/submission, selection, binding, and draw-submission markers.
Accept submission measurements only when `ProfileValid` is true. Do not mix Development and release timing tables.

A standard Deferred+ release sweep completed with four blended layers, 1025 heights and 256 controls.
The initial projected-size renderer used 4.486 ms GPU at the 64-tile middle view. The height-error renderer used 2.514 ms.
At the 64-tile overview, GPU time changed from 2.782 to 0.837 ms. The active middle-view main thread changed from 1.027 to 0.732 ms.
These are averages of two pass medians on an RTX 5080 at 1920 by 1080, Direct3D 12. They measure full frames.
Native terrain still used less time: 1.813 ms GPU and 0.603 ms active main thread at the new middle-view run.
Native pixel error 5 and Looga height error 2 are not verified equivalent quality settings. The comparison does not establish a universal speed advantage.

The updated 4/16/36/64-tile middle-view Looga GPU results were 1.285/1.886/2.208/2.514 ms.
Separate runs completed with 16 tiles at 2049 heights and 1024 controls, and four tiles at 4097 heights and 2048 controls.
A 256-tile stress case covered 16 km by 16 km with 257 height samples per tile and Looga patch-frustum culling disabled.
Looga full-frame GPU time was 3.126 ms ground, 3.631 ms middle, and 1.915 ms overview. Both renderers completed the test.
This is 256 square kilometres at approximately 3.9 m source spacing. It is not a first-person detail test or a capacity ceiling.
Surface arrays remained 1024 square in all cases. Memory counters are not isolated renderer memory or VRAM use.

## Submission optimization

The next pass cached selection commands and combined surface/shadow submissions when terrain bounds were fully visible.
At 64 tiles, indirect submissions changed from 128 to 77 ground, 78 middle, and 64 overview.
The Development-build CPU preparation/submission scope changed from 0.216/0.210/0.187 ms to 0.196/0.192/0.166 ms.
This is approximately 9 to 11 percent less CPU time in that scope. It is not a total frame-time improvement.

The final release build used 2.622 ms GPU at the 64-tile middle view. A repeat used 2.525 ms.
The previous pass used 2.514 ms. These runs do not establish a GPU speedup. Total CPU thread results varied by view.
The final 4/16/36/64-tile, higher-resolution and 256-tile cases completed with valid timings and no captured errors.
Fourteen tests and isolated camera, source replacement, edit and lifecycle checks passed.
The final validation G-buffer captures match the previous pass byte-for-byte. The native renderer remains faster in these workloads.

## GPU batching results

The next pass grouped compatible tiles into shared GPU selection buffers.
At 64 tiles, the same-session middle-view comparison changed from 2.670 to 2.191 ms GPU. A separate repeat used 2.193 ms.
At 256 tiles, the same comparison changed from 3.634 to 2.255 ms. A separate repeat used 2.189 ms.
The 256-tile workload used 257 height samples per kilometre and disabled Looga patch-frustum culling. Unity draw culling remained active.
These are full-frame release results on the same RTX 5080 / Direct3D 12 / Deferred+ setup described above.
Native middle GPU time remained lower at approximately 1.935 and 1.942 ms respectively.
All compared patch counts and validation G-buffers were unchanged.
Disabling terrain shadows saved about 0.08-0.09 ms in ground views. Other tested views showed little measurable change.

## LOD and tile continuity

Selection uses cached GPU bounds and height-error data. Each node compares source height samples with its 16 by 16 triangle grid.
`SetLodQuality()` controls projected height-error tolerance. The previous projected-size mode remains available through serialized settings.
The error estimate does not include all displacement from edge stitching. It is not a strict final screen-space error guarantee.
Bounded native height edits rebuild affected metadata nodes and their ancestors on the next rendering callback.
Unknown changes and explicit RefreshSource calls retain a full rebuild.

Tiles share one camera update and complete selection before any draw reads adjacent LOD maps.
Shared edges collapse to the coarser tile's vertex spacing. Border patches no longer require minimum patch size.
Neighbors require exact grid origins, equal physical bounds and equal patch counts. Source heights must already agree at the shared edge.
Different heightmap resolutions are allowed only when their shared heights agree. Culling bounds include the ancestor height range and coordinate range required by collapsed vertices.
Material bindings are cached until a source revision or neighbor resource changes.
Selection commands are recorded per camera and replayed every frame. Camera movement still updates GPU selection every frame.
Tiles with the same selection shader and patch count share a selection batch. Each batch contains up to 256 tiles.
One reset dispatch and one selection dispatch update each batch per camera. Each tile retains separate source textures, materials and collision.
GPU metadata copies occur only after source changes or batch creation. Changes to LOD settings update the batch parameter buffer.
Changes to membership or renderer resources rebuild batches. Each camera owns separate output buffers.
Shared patch and LOD buffers use explicit per-tile offsets. Neighbor reads use the corresponding tile offset.

Fully visible tiles use one indirect submission for their surface and shadow passes.
Partially visible tiles retain separate surface and shadow submissions. Offscreen tiles retain shadow-only submissions.
When patch-frustum culling is disabled, each tile uses one combined submission. Unity still culls draw bounds.
`SetShadowCasting(bool)` controls Looga terrain casting without changing native source data.
`GetLastSubmissionCount(camera)` returns the most recent indirect submission count for a tile.
This is a CPU submission count, not the number of GPU draws across all render passes.

## Preview limits

Optional camera components now provide terrain Hi-Z and conservative light-aware shadow culling.
Offscreen selected patches remain in the shadow list. Shadow detail follows camera LOD.
An isolated check covered two cameras, camera movement, height/hole edits, and renderer reconfiguration. Large multi-camera workloads and moving LOD transitions need further validation.
Finite-difference normals can differ at a tile edge; this preview does not sample a neighboring tile's height texture.

The basic material includes URP lighting, depth, normals, shadows, and terrain holes.
Camera motion uses URP camera vectors. Surface edits and LOD changes have no previous-frame geometry vectors.
Standard URP Forward player rendering passed the synthetic visual check.
The exported MicroSplat surface also passed standard URP Deferred+ G-buffer, hole, point-light and received-shadow checks. The basic Looga Lit shader remains forward-only.
The previous Looga Graphics Pro generated-pass stripping and indirect-lighting fallback-buffer shutdown defects were fixed. The combined-cache fixture and current-world Deferred+ player runs now complete without runtime errors. Wider lighting variants remain part of release qualification.
All shadow/light modes, full player shader coverage, and render-pass parity still require validation.
Runtime virtual texturing is optional. There is no disk streaming, XR, vegetation renderer, navigation rebuild, or runtime collision deformation system.
An XR camera restores native terrain rendering. Unsupported graphics APIs retain native rendering during setup.
Core assembly isolation is designed for optional packages; a clean consumer with those packages absent still needs a UPM integration test.


## Windows installed-module qualification

The release target is Unity 6000.3.21f1, URP 17.3, Windows and Direct3D 12.
The MicroSplat version is 3.9. The MicroVerse version is 1.7.

There are three explicit rendering routes:

| Route | Behavior |
| --- | --- |
| Cached surface | Cache stable channels. Apply the supported live detail effects during normal shading. |
| Direct Looga surface | Use Looga geometry and the full generated MicroSplat surface on every draw. No terrain surface cache is used. |
| Native terrain | Preserve native MicroSplat rendering when geometry or material features are outside the Looga policy. |

The direct and cached feature policies are separate. Acceptance of a direct surface does not imply that its effects can be cached.
The direct policy now includes the tested alpha-hole texture, global tint/normal/geo texture, snow, stream/puddle/wetness, wind/glitter, three-texture cluster, additional normal noise/distance resample, triplanar height blend, parallax and terrain-blend variants.
The exact keywords are in `MicroSplatCompatibility`. Other combinations retain a native fallback until checked. Tessellation and vertex displacement remain native.

Editor GPU comparisons include visible alpha-hole, global snow-level, dynamic stream-map and wind-map changes.
Do not bake those changing values into a static page producer. The cache exporter rejects these direct-only features.
MicroSplat stream maps can be unassigned while the stream manager starts. The bridge binds a black texture until a live stream map is available.

New virtual surface exports record a source-code schema in both materials. A mismatched surface/producer pair cannot register in the cache.
Re-export old pairs to add this check. Legacy pairs with no schema remain supported for migration.
A schema identifies generated source code, not arbitrary changes to textures or material values. Use the normal material synchronization API for those changes.

MicroVerse roads, splines, objects, ambiance and vegetation retain their native components. Terrain changes use public MicroVerse events and native TerrainData notifications.
Native trees, details, object meshes, road meshes and RAM water remain outside the terrain surface cache. They keep their original rendering and authoring paths.
RAM materials are not automatically changed into Graphics Pro RVT receivers.

Camera teardown retains feedback textures until pending GPU readback finishes. This prevents release of an in-flight readback source.
The compatibility test matrix also compiles core-only and individual optional integration assemblies. A clean UPM installation is a separate distribution test; these compilation checks do not replace it.

This remains a preview package. The checked profiles are suitable for controlled project testing. This document does not certify every module combination, all RAM shaders, or a final published release.

## Cache compression and parallel feedback

Virtual Texture Settings exposes Compress Cache, Compression Shader, and Parallel Feedback.
Compression is opt-in. Assign Runtime/Shaders/VirtualTexturing/VirtualTextureCompression.compute when enabling it.
ConfigureOptimizations sets the same options from code before cache registration.
Parallel Feedback defaults to enabled. Disable it for serial comparisons.

The compressed path requires Direct3D 12 and Linear color space.
Unsupported hardware or a missing encoder uses the uncompressed cache with a warning.
Color and smoothness use BC3 SRGB. Signed normals and occlusion use BC3 UNorm with signed decoding during sampling.
Emission and metallic retain their original RGBA16F storage. HDR emission is not clamped.
The encoder runs on the GPU and copies blocks into resident arrays before publishing each page.
No compression readback or CPU block encoding is required.

At 512 pages, resident surface storage falls from 180.625 to 90.3125 MiB.
The reusable encoder workspace adds approximately 0.3704 MiB.
These figures exclude page tables, feedback storage, driver allocation padding, and source textures.
Compression is lossy. The focused ramp tests require mean RGB error below 0.025 and normal error below five degrees.
Sharp texture transitions and different material profiles need their own visual checks.
Re-export preexisting generated shaders only if they contain an embedded copy of the old sampling include.
Normal package include references import the new decode path automatically.

Feedback copies transient readback data into reusable native storage.
Burst jobs decode pixels in parallel, sort page keys, and count distinct leaves on workers.
The main thread consumes completed results without waiting during normal rendering.
Residency changes and parent expansion remain on the main thread.
Disposal completes outstanding jobs before releasing native memory.
This path adds worker latency and storage. It does not promise a lower GPU time.

A 37-terrain Kubera world-copy benchmark completed 32 D3D12/Deferred+ cases at 1920 by 1080 on an RTX 5080.
The table compares the uncompressed serial baseline with compression and parallel feedback together.
Values are means of two pass medians, in milliseconds, for the entire frame.

| View | Baseline CPU frame | Optimized CPU frame | Baseline GPU | Optimized GPU |
| --- | ---: | ---: | ---: | ---: |
| Ground | 2.596 | 2.511 | 1.908 | 1.901 |
| Overview | 2.955 | 2.742 | 1.224 | 1.188 |
| Moving | 2.692 | 2.610 | 2.336 | 2.334 |
| Bounded refresh | 2.639 | 2.577 | 1.897 | 1.942 |

Compression halves resident cache storage. Timing gains are small and depend on the view.
This comparison uses identical Looga geometry across modes. It does not compare native terrain or establish a universal FPS improvement.
The benchmark runner accepts -looga-cache-optimizations for all four compression/feedback combinations.

## Visibility and feedback optimization

TerrainRenderer keeps patch-frustum culling enabled by default. Node bounds include possible edge-collapse vertices.
Native height callbacks carry a bounded metadata update. LastMetadataNodeCount reports the work from the last update.
Unknown changes, source replacement and explicit RefreshSource calls use complete metadata rebuilds.

Add TerrainOcclusionCamera to a rendering camera to use the optional visibility passes.
Add InstanceOcclusionRendererFeature from Looga Instancing to the selected URP renderer for Hi-Z.
Assign Runtime/Shaders/TerrainBatchedShadows.shader to enable compatible shadow batches.
Keep the shadow shader reference in the build. Configure retains its first shader argument for serialized compatibility, but temporal Hi-Z no longer uses the old terrain-only depth shader.
For shadow culling without Hi-Z, use Configure(null, shadowShader, true, false).

Hi-Z consumes the same prior-frame opaque-depth pyramid as Looga Instancing. Terrain and vegetation therefore share one 512 by 512 hierarchy, one reduction pass and the same camera-cut checks. The terrain path no longer redraws terrain into a private full-resolution depth target or reduces that target a second time.
The shared depth contains all opaque objects rendered by the camera. Transparent water does not act as an occluder. Camera cuts, projection changes and configurable movement thresholds fail visible; moving opaque occluders can expose terrain one frame before matching depth history is available.
Each camera owns shared history in the renderer feature. Disabling the terrain component releases its consumer lease and shadow groups; inactive camera history is reclaimed by the feature.
Stereo and unsupported graphics APIs do not use this path. Large camera stacks need separate workload qualification.
The component does not add itself to Scene View cameras.

The depth contract is the LoogaTerrainDepth material tag with value NativeHeightsAndHoles.
The basic surface and compatible new MicroSplat exports declare this contract.
Re-export existing MicroSplat surfaces before using their geometry as occluders or generic shadow casters.
Material alpha holes and unknown depth contracts retain their original material shadow passes and cannot act as simplified occluders.
Do not add this tag to a shader with different displacement or clipping behavior.

Directional-light culling retains patches whose shadow extrusion can reach the padded camera view.
Point and spot lights retain every caster inside their range. Unknown light types or more than 16 shadow lights retain all casters.
This is conservative receiver-view culling, not a separate optimal selection for each shadow cascade.
Shadow LOD still follows the viewing camera.

Shadow batches group nearby tiles with matching source dimensions and scene layers.
Texture arrays preserve the player BC4 hole format. Source copies occur after texture revisions.
Each group limits duplicate source storage to 128 MiB. Other storage includes patch, mapping and argument buffers.
Unsupported groups retain per-tile shadow submissions. Surface draws retain their individual MicroSplat material bindings.
Cross-tile surface-material batching is not implemented by this shadow path.

Assign VirtualTextureCompact.compute through VirtualTextureSettings.ConfigureFeedbackCompaction to compact requests before GPU readback.
A bounded GPU hash table counts duplicate leaf requests. Readback transfers the header and unique key/count pairs.
Overflow, unsupported key ranges and readback failures retain the original pixel-feedback fallback.
ConfigureFeedbackCompaction(null) restores the existing Burst or serial feedback path.
FeedbackBytes measures successful header and payload transfers. Driver overhead is excluded.

MicroSplat virtual-surface exports skip redundant cache lookups while the direct close-range blend is zero.
Hybrid height transitions and their texture gradients retain live sampling.
This change does not replace close-range shading with approximate cached endpoints.

These options can cost more than they save. Compare GPU frame time, CPU frame time and image quality for representative cameras.
Lower draw submission counts and lower readback traffic do not establish an FPS improvement by themselves.
The final 37-terrain world comparison used 72 Windows/D3D12 Deferred+ cases on an RTX 5080 at 1920 by 1080.
All 81 terrain tests passed. The player reported no runtime errors; existing project warnings remain.
The following table compares the existing compressed cache with compact feedback and shadow culling together.
Hi-Z and shadow batching are disabled in the selected profile. Values are means of two whole-frame medians in milliseconds.

| View | Existing GPU | Selected GPU | Existing CPU | Selected CPU |
| --- | ---: | ---: | ---: | ---: |
| Ground | 1.925 | 1.790 | 3.168 | 3.110 |
| Overview | 1.217 | 1.225 | 3.131 | 3.025 |
| Moving route | 2.533 | 2.143 | 3.428 | 3.141 |
| Bounded refresh | 1.969 | 1.819 | 3.192 | 3.124 |

Compaction reduced feedback transfer bytes by approximately 99.1-99.5%. Ground shadow selection fell from 5,071 to 2,443 patches.
The retired terrain-only Hi-Z rejected only one extra ground patch in this view, and its full-resolution depth pass cost more than that rejection saved. The temporal replacement removes that duplicate pass; it still remains opt-in because sparse open views can save too little raster work to repay patch testing.
Spatial shadow batching reduced ground submissions from 54 to 28 after culling, but also cost more GPU time.
Keep those two options opt-in. The benchmark runner accepts -looga-visibility for all nine modes, including Balanced.
These measurements do not establish a universal FPS gain. Native and Looga LOD quality are not calibrated equivalents.
## MicroVerse 1.7 road shader build limit

The optional MicroVerse editor integration removes invalid DOTS variants from three generated MicroVerse 1.7 shaders that target shader model 3.0. Both local and global keyword declarations are handled. Native road rendering and terrain authoring remain available, and vendor files remain unchanged. The workaround is restricted to package version 1.7.0 and the known generated asset paths.

The shared editor validator also rejects their known Unity compiler errors before instance ownership. This safeguard does not add BRG support to those low-target road shaders. Keep these generated roads under their native renderer, or use a separately validated DOTS-capable material before conversion. A future MicroVerse version must be checked again instead of inheriting this version-specific workaround.
