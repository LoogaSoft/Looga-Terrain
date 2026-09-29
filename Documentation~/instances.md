# Instance and vegetation rendering

General mesh rendering and authoring are provided by the shared Looga Instancing package.
TerrainData providers, ground bindings and terrain-tool adapters remain in Looga Terrain.
Both products retain their existing virtual texture caches.
It uses public BatchRendererGroup and DOTS instancing APIs. It does not require a vendor vegetation renderer.

## Supported scope

- Windows, Direct3D 12, Unity 6000.3 and URP Forward+ or Deferred+.
- Ordinary Forward does not supply the complete per-object light list for this BRG path. Use a clustered mode for full lighting.
- Triangle meshes with opaque or alpha-cutout URP mesh shaders that provide standard DOTS variants.
- One root LODGroup, up to eight levels, with child transforms and multiple submeshes.
- Nonuniform and negative scale. Mirrored draws use separate winding commands.
- GPU frustum and distance culling. Screen-relative LOD uses the source LODGroup size and thresholds.
- Native shader depth and shadow passes. Shadow views retain a conservative union of their culling splits.
- Explicit placements, stable handles, persistent buffers and changed-slot uploads.

Native terrain providers, Looga wind, distance/timed crossfades and staged GPU cell residency are available.
Explicit SpeedTree and custom material profiles, collider residency and mesh painting are described below.
Basic shader acceptance uses pass and DOTS checks. Optional profiles supply deformation data. Source materials are never changed.
Hi-Z is not part of this initial instance module.

## Setup

Set **Project Settings > Graphics > Shader Stripping > BatchRendererGroup Variants** to **Keep All** before a player build.
Keep the SRP Batcher enabled. The culling compute shader is included through Resources.

For a serialized population, add `InstanceContainer`, assign a prefab and enter placements.
Placements use the container's local space. Container and parent transforms update automatically. Call `Rebuild` after source prefab changes.
The container draws while enabled, including Editor previews. It does not hide existing scene renderers.
Do not place the same population under two render owners.

For general runtime ownership, use `LoogaSoft.Instancing`. Terrain providers retain `LoogaSoft.Terrain.Instances`:

```csharp
var prototype = InstancePrototype.FromPrefab(prefab);
var instances = new InstanceRenderer { MaxDistance = 1000 };
int type = instances.Register(prototype);
InstanceHandle handle = instances.Add(type, Matrix4x4.TRS(position, rotation, scale));
instances.Flush();

instances.Update(handle, newTransform);
instances.Flush();

instances.Remove(handle);
instances.Flush();
instances.Dispose();
```

Call source methods on the main thread, before camera rendering. Retain the owner and dispose it when its scene or system stops.
Handles are valid only within that owner lifetime. They survive buffer growth. Removed handles cannot modify reused slots.
Reuse one immutable prototype object to share registration within an owner.
Recreate a prototype after changing mesh topology, materials, child transforms or LOD settings.

## Memory and diagnostics

Each resident slot stores a 48-byte culling record and two 48-byte matrices per mesh/submesh part.
Capacity grows to the next power of two. Each retained view adds two visibility buckets and draw arguments per part.
The renderer repeats GPU culling per draw part. The terrain streamer shares one tree renderer and one detail renderer across cells.
Changed transforms add two previous-matrix arrays. Optional appearance adds three float4 arrays per part. Timed fades add per-view history.

`GpuBytes` reports owned GPU buffer capacity. It excludes shared meshes, textures, pipeline resources and CPU storage.
`UploadedBytes` counts source-buffer uploads. An unchanged `Flush` uploads nothing.
`ReadDrawCountsForDiagnostics` blocks for GPU readback. Use it only outside timed samples or gameplay rendering.
Draw counts include submesh/LOD commands, not unique plants. A shadow view can retain a caster for multiple splits.

The module requires a valid SRP camera render context. It does not change camera culling matrices or private URP settings.
Each culling callback uses separate visibility and argument buffers until the render context completes.
Source changes expand the BRG global bound incrementally. Removal keeps a conservative bound until the owner becomes empty.

## Validation limits

Use the isolated Kubera vegetation fixture for player comparisons. It uses deterministic shared placements and a fixed seed.
The native baseline uses MeshRenderer and LODGroup objects. It is not a native Terrain grass comparison.
Do not infer whole-world gains, mature forest performance, or vendor superiority from this synthetic workload.

The saved-material Editor check requires a saved scene and runs outside the EditMode test runner.
Use Tools > Looga > Terrain > Validate Saved Instance Materials in the Kubera validation project.
The test runner uses an untitled scene that cannot host the additive fixture.
The package tests cover Unlit and Looga materials, perspective/orthographic views, mesh LOD, legacy probes and baked lightmaps.

Camera stacks, reflection cameras, Scene View selection and thumbnail rendering need separate visual qualification.
The player comparison covers opaque Lit instances. Saved-scene Editor checks also cover cutout and multiple material slots.
The imported vendor renderers are reference implementations, not measured competitors in this pass.

## Native terrain vegetation provider

Add TerrainVegetationProvider to a native Terrain for opt-in Play mode rendering.
The provider supports accepted URP Lit/Unlit and Looga Vegetation Lit prototypes, including alpha cutout.
The provider uses native TreeInstance transforms and ComputeDetailInstanceTransforms for detail placement.
Unity defines that API as returning the same transforms used by native detail rendering:
https://docs.unity3d.com/6000.0/Documentation/ScriptReference/TerrainData.ComputeDetailInstanceTransforms.html

Terrain trees use their terrain placement pivot for LOD distance. Ordinary instance prototypes retain their original LOD reference center.
The provider keeps tree and detail distance limits separate and reads terrain positions in world space.
It supports both native coverage and instance-count detail scatter modes.

Native rendering remains active until the complete supported population is ready.
An unsupported prototype, color profile or population limit restores native rendering for both trees and details.
This whole-population fallback avoids the shared drawTreesAndFoliage switch hiding one category or drawing another twice.
The provider restores the original flag when disabled and releases its GPU buffers.
Known BRG, Flora and FR2 components on the terrain block ownership. An already-disabled native flag also blocks initial ownership.
Other render owners must use SetIntegrationBlock or coordinate their ownership explicitly.

Normal Editor authoring stays native. Explicit preview-scene tests can request ProcessChanges outside Play mode.
The Inspector reports ownership, buffer allocation and fallback reasons. Refresh Source queues a complete rebuild.
Editor Undo/Redo also queues a complete rebuild. No terrain data or prototype material is modified by the provider.

Source update contracts:

- Call InvalidateDetails with detail-map coordinates after SetDetailLayer or custom detail painting.
- Call InvalidateTrees after same-count tree edits. Tree count changes are detected automatically.
- Call InvalidateAll after prototype, material, detail schema or scatter-mode changes.
- Height and hole callbacks invalidate affected detail patches. Tree arrays refresh after height changes.
- TerrainData replacement, terrain translation and detail density changes trigger complete refreshes.
- Apply ProcessChanges before rendering when editing sources after the normal LateUpdate step.

Detail paint has no general native TerrainCallbacks event. Custom tools must use the explicit invalidation API.
A local detail edit regenerates only its intersecting native patches, across all detail layers.
The default fully resident path extracts all placements synchronously. The optional streaming path extracts detail placements by cell.
Instance Limit bounds admitted resident instances, with a default of 250,000. It does not include shared meshes or textures.
A dirty patch can grow buffers. Buffer allocation and native placement extraction cannot be interrupted by the staged CPU budget.

## MicroVerse vegetation coordination

The optional MicroVerseVegetationBridge uses public begin, finish and cancel events.
Assign the MicroVerse source and explicit TerrainVegetationProvider targets.
The bridge suspends Looga ownership during generation. It resumes when MicroVerse stops modifying and spawning.
It does not wait for IsHeightSyncd. MicroVerse 1.7.0 leaves that flag false after the first update of a session.
Each provider waits for CPU heights itself: Unity reports unsynchronized heightmap changes to the provider.
It requests a full source refresh after resumption. It does not request generation or mutate private synchronization state.
Disable the bridge to remove its block. The core Instances assembly has no MicroVerse dependency.
Public road, spline and RAM adapters connect source changes to placement masks. See Road, water and MicroVerse masks below.

## Provider limits

Mesh details require native useInstancing and valid native prototypes. Texture grass uses Looga-owned crossed cards or cylindrical billboards.
Ground alignment samples the native terrain normal. Generated meshes and materials belong to the provider and are released with it.
Texture grass blends dryColor and healthyColor with stable spatial noise. This is Looga noise, not a reproduction of Unity's private grass color noise.
Trees require zero bendFactor. URP Lit/Unlit and Looga materials accept tree tint. Non-white lightmap color requires Looga Vegetation Lit.
Rotated or scaled Terrain objects remain native.
SpeedTree and custom shaders need an explicit material profile. SpeedTree appearance, billboard transitions and authored wind remain qualification gates.
The native Terrain provider has not received a wind/crossfade image qualification. New visual qualification uses explicit instance placements.
Collider proxies and terrain receivers are described below. Optional generated pages and asset loading have separate ownership.
Do not infer those features from the BRG core or claim the complete vegetation roadmap is finished.

## Looga vegetation wind and LOD transitions

Use Looga/Terrain/Vegetation Lit on explicitly assigned vegetation materials. The renderer does not convert vendor materials.
The shader supports albedo/color, alpha cutout, optional normal maps, smoothness and bounded local-space wind.
URP surface, depth, depth-normal and shadow passes use the same deformed position and corrected normal/tangent.
The Forward and GBuffer passes use URP shader includes. Player image qualification uses Deferred+.
RGB vertex masks control trunk, branch and leaf amplitudes. Alpha supplies leaf phase. Enable Vertex Wind Mask to use these channels.
The shader has current/previous object and wind deformation, URP GI/APV variants, and optional per-instance legacy probes.
Forward+ and Deferred+ image fixtures cover legacy probes and baked lightmaps. A separate baked APV fixture verifies live probe sampling in both clustered modes.

Wind controls:

- Wind Params: amplitude in mesh-local meters, angular speed, bend height and bend-base height.
- Wind Direction: local X/Z direction in the vector's first two components.
- Wind Time: -1 uses game time. A nonnegative value holds a reproducible wind phase.
- Branch Wind: amplitude, relative frequency and height phase.
- Leaf Wind: amplitude, relative frequency and height phase.
- Billboard rotates geometry around local Y toward the camera. Generated billboard grass disables shadows.
- The shader fixes vertices below the bend base and increases displacement over the bend height.

Prototype bounds include the sum of trunk, branch and leaf amplitudes before child and instance transforms.
Recreate the prototype or call provider InvalidateAll after increasing amplitude. Existing prototypes retain their captured bound.
Static mesh assets remain unchanged. This shader applies bounded procedural displacement; it does not simulate articulated branches.

Set LODGroup Fade Mode to Cross Fade. Disable Animate Cross Fading for distance transitions.
The GPU converts Fade Transition Width to a distance interval and draws both adjacent LODs during that interval.
Unity's packed BRG visibility stores signed eight-bit fade values and 24-bit instance slots.
The 24-bit limit is enforced before population growth. Fade quantization can change a small number of dither pixels.
Animate Cross Fading uses LODGroup.crossFadeAnimationDuration and separate GPU history per view.
Transitions can reverse continuously. Reused slots reset their history. Large translations, direction cuts, projection changes and interrupted views reset timed LOD history.
SpeedTree fade mode requires SpeedTreeMaterialProfile.
Inactive child renderers are excluded. Root activation does not change the captured prototype.

InstanceRenderer exposes ShadowDistance and MinimumShadowLod. Containers and terrain providers expose the same controls in their Inspector.
The camera distance remains an upper limit for shadows. Zero minimum LOD keeps normal LOD selection.
These controls apply per light view. Advanced per-cascade settings remain separate work.

Current tests cover stable handles, bounded uploads, tree tint, texture-grass lifetime, cancellation and native image parity.
Image tests include mirrored scale, child transforms, branch/leaf wind, billboards and perspective/orthographic cameras.
The native image control uses the same Looga shader on ordinary MeshRenderers. This does not prove native Terrain grass equivalence.
Player tests include cold shader startup. A real authored SpeedTree wind fixture remains a qualification limit.
Do not infer a frame-rate gain or complete replacement parity from these checks.

## SpeedTree and vendor profiles

Assign a material profile on InstanceContainer or call TerrainVegetationProvider.SetMaterialProfile.
Pass the same profile to InstancePrototype.FromPrefab for explicit populations.
Profiles keep original materials and shader passes. They do not convert imported assets.

SpeedTreeMaterialProfile supports URP SpeedTree8_PBRLit and SpeedTree9_URP Shader Graph contracts.
The optional assembly uses an original bridge in Unity.RenderPipelines.GPUDriven.Runtime through an asmref.
It reads Unity's native current and previous wind parameters without reflection or vendor source changes.
This bridge depends on internal Unity APIs; requalify it when upgrading Unity.

One hidden native wind proxy is shared per profile and source prefab. Ref-counted leases release it after the last population is removed.
One persistent renderer-ID array and one cached current/history block avoid per-frame allocation.
Each batch uploads 32 shared float4 properties, not a duplicate wind block per tree.
SpeedTree percentage LOD deformation uses packed visibility without the dither keyword until the final LOD.
Bounds Padding is a conservative mesh-local displacement allowance. Refresh prototypes after changing it.

Native SpeedTree animation requires authored wind data in the imported model.
The six Kubera New Trees models report bestWindQuality=0. Their static materials and LOD geometry can be checked,
but these assets do not qualify animated SpeedTree output or historical motion vectors.
SpeedTree9, billboard transitions, local/spherical WindZones and per-instance wind variation are not qualified.
The shared origin proxy is intended for directional global wind. Do not treat it as a local WindZone simulation.

GlobalWindMaterialProfile is an explicit shader allow-list with a displacement allowance.
Use it only after checking that the shader supports DOTS instancing, matching depth/shadow passes and the required LOD mode.
Looga Vegetation Lit supplies the package's own wind without an FR2, BRG Instanced Renderer or Flora controller.
The validation profile uses this Looga-owned shader. A custom profile is an extension contract, not a bundled vendor integration.
The SpeedTree bridge is separate and uses Unity's native wind data.

Looga is intended to replace the rendering and placement functions of FR2, BRG Instanced Renderer and Flora.
No runtime adapter for those products is included. The external-owner check only prevents duplicate terrain rendering
when another renderer is already active. It does not call or depend on that renderer's assembly.
The replacement is not yet feature-complete; the limits below still apply.

## Bounded terrain vegetation residency

Streaming is opt-in; the default remains fully resident. In code:

```csharp
provider.ConfigureStreaming(new[] { gameCamera.transform }, cellsPerUpdate: 4,
    prefetchMargin: 32, residentCells: 256);
```

Assign every gameplay camera as an observer. Camera transforms let LOD-group tree streaming
bound the visible region from camera projection, far clip distance, final LOD threshold and quality LOD bias.
Tree LOD groups control their own disappearance distance; Terrain.treeDistance applies to trees without a LOD group.
Perspective and orthographic camera regions are conservative. Custom projection matrices and unregistered extra cameras are not qualified.
A zero final LOD threshold can keep many cells resident until the far clip limit.

Trees are indexed in 64-meter XZ cells. Details use their native terrain patches and native placement API.
The source TerrainData and tree index remain in CPU memory. Mesh and texture assets remain resident.
Two shared renderers own tree/detail prototypes across cells. Eviction removes handles and trims unused trailing GPU capacity.
Stable handle generations survive trim and regrowth. Sparse live slots can retain capacity above the live count.

Required cells load before prefetch cells. Spatial grid lookup restricts the cells considered around registered observers.
ConfigureStreaming limits completed cells and page requests per update. SetStreamingBudget adds record, upload-byte and cooperative CPU limits:

```csharp
provider.SetStreamingBudget(new VegetationStreamingBudget
{
    PlacementRecords = 2048,
    UploadBytes = 262144,
    CpuMilliseconds = 3,
    ResidentGpuBytes = 268435456
});
```

ResidentGpuBytes checks projected source capacity before upload, reserving at least eight view buffers and timed history.
Prefetch cells are evicted first. A required-population limit failure releases Looga resources and restores native rendering.
Actual allocation is checked again. More than eight retained views can exceed the estimate until the next update.
Source assets, CPU records and pipeline allocations are outside this counter; it is not a total VRAM limit.
Native detail extraction, initial source indexing, buffer allocation and individual API calls remain synchronous.
The cooperative CPU limit checks between records. LastNativeReadMilliseconds exposes native extraction cost.
Native extraction remains the default. Optional Reference, Burst and Compute sources use the same page interface.

Prefetch Margin provides loading lead-in. Eviction uses twice that margin for hysteresis.

Native trees and details remain visible while required cells load, including after camera teleports.
The provider enables Looga draws only when all required cells are ready, then hides the native population.
Missing observers, unsupported data and capacity failures restore native ownership and report a Diagnostic.
Use InvalidateAll or change configuration to retry after a capacity failure.
DisableStreaming returns to full residency. Disable the provider to release all resources and restore its original native flag.

ResidentCells, LastCellLoads, LastCellEvictions, InstanceCount and GpuBytes report residency.
Local detail invalidation evicts and reloads only affected resident patches. Tree changes rebuild the tree index.
MicroVerse authoring blocks and terrain height synchronization suspend ownership through the existing provider contract.

## Instance appearance and transform history

Pass InstanceAppearance to Add or call UpdateAppearance on a live handle.
Tint multiplies the source material color. LightmapColor multiplies indirect illumination in the Looga shader.
Custom is a float4; the first component supplies a stable wind-phase offset. Set a persistent source random value explicitly.
The renderer converts color values to the active color space before upload. Custom values remain unchanged.
URP Lit/Unlit accept tint only. Unsupported custom shader channels reject before a population takes ownership.

Update retains the first previous transform within a frame. The next frame advances history after rendering.
Looga motion vectors use previous object matrices and previous wind time. Billboard motion reconstructs the previous camera position.
These paths require further moving-camera and temporal-effect image qualification.

FlushBudget returns true after all pending source records reach the GPU. Keep RenderingEnabled false until then.
Ordinary Flush uploads all records. The vegetation streamer manages suspension automatically.
Native rendering stays active while required pages or uploads are pending, including prefetch-triggered uploads.

## Optional placement pages

FileVegetationPageSource reads versioned binary placement pages on worker tasks. It does not load Unity assets.
SetPageSource installs a reader; null selects native source extraction. Disable or source replacement cancels pending requests.
The streamer permits at most four concurrent page reads. An evicted page cannot commit a late result.
Read errors or stale revisions restore native rendering and expose the Diagnostic.

Use FileVegetationPageSource.Write during an explicit bake. Use a new revision after any source or prototype-schema change.
Records contain terrain-local transforms, source prototype indices and InstanceAppearance. They must fit the source cell and captured displacement bounds.
Tree cells are 64 meters. Detail cells use native detail-patch coordinates. Missing pages are errors; write an empty page for an empty cell.
Readers validate length, record count, revision and appearance values before upload. The renderer also validates transforms and prototype indices.
The current provider still builds its cell index from TerrainData. Pages cannot introduce trees into cells absent from that index.
The caller owns page revisions and bake scheduling. Asset leases are supported below; independent disk-only source descriptors are not part of the TerrainData provider.

## Prototype lighting and mesh LOD

`WithLodBias` makes an immutable per-prototype quality override. Native QualitySettings.lodBias and maximumLODLevel still apply.
Unity 6 imported mesh LOD uses its index ranges, selection curve, renderer selection bias and forced level.
Mesh LOD expands draw parts and uses per-instance transformed mesh bounds. QualitySettings.enableLODCrossFade enables
per-view timed mesh transitions. Group fades take priority over mesh fades. Re-capture after importer or range changes.
`WithLegacyLightProbes` enables Unity's legacy SH and occlusion sampling during changed-slot uploads.
APV remains a live shader path. Legacy probe data uses 128 bytes per part/slot; lightmap scale/offset adds 16 bytes.
Baked lightmap indices and UV transforms come from source renderers. Owned material copies retain the baked texture bindings and keywords.
These copies are released with their population. Rebuild after rebaking, unloading or replacing the source lighting data.
The core rejects a baked prototype whose lightmap is unavailable. Source assets remain unchanged.

## Shared Looga wind

Add one `VegetationWindField` for world-space direction and gust controls. Strength and gust amplitude are bounded to zero through one.
Up to eight local spherical volumes blend direction using a smooth edge. The volume transform supplies position and forward direction.
Gusts travel downwind as fronts. Gust Wavelength sets the distance between fronts; they move at Gust Frequency x Gust Wavelength / 2 pi
meters per second. A moving noise curves the fronts and changes their strength. Lean is the part of the trunk amplitude that bends
steadily downwind; the rest sways. Configure both with `ConfigureWaves`.
Vegetation Lit's Gust sheen brightens and glosses the upper part of the plants while a front bends them. It is off by default.
The field never increases the sum of the captured trunk/branch/leaf displacement limits.
It retains the previous frame's field for motion vectors. Multiple cameras read the same frame history.
Disable releases its shader globals. Without a field, each material retains its original local-direction wind.

## Optional generated detail pages

`GeneratedVegetationPageSource` implements the existing placement-page interface. Select Reference, Burst or Compute.
All three use the same original integer-hash distribution, density/coverage controls, surface heights, holes, scale range, jitter and alignment.
This distribution is not Unity's private placement/noise algorithm. Use the default native provider when exact native positions are required.
No generation mode changes TerrainData. Detail paint is read per requested patch. Surface snapshots refresh after provider rebuilds and local hole edits;
standalone callers must use `RefreshSurface` after height or hole edits. Old-revision results are rejected. Local invalidation cancels affected requests without discarding unrelated pages.

```csharp
provider.ConfigureStreaming(observers);
provider.SetPageSource(new GeneratedVegetationPageSource(terrain, DetailGenerationBackend.Burst));
```

Compute generation currently uses asynchronous GPU readback before the common budgeted instance upload.
It is not a GPU-only placement-to-draw path and is not the default. Small pages can cost more than native extraction or Burst.
Surface snapshots, density capture and record assembly use CPU memory. The resident GPU budget is not a total source-memory or VRAM limit.
Native placement calls and buffer allocation remain non-preemptible. CPU time is a cooperative preparation budget, not a hard frame-time guarantee.

`VegetationInterest` transforms can join camera observers to keep placements resident around gameplay regions.
This is residency only; it does not create collision or gameplay proxies. Camera visibility remains separate.

## Optional asset loading

`IVegetationAssetSource` returns a `VegetationAssetLease`. Use `InstanceContainer.ConfigureAsync` to consume it.
A replacement or disable cancels pending acquisition. Stale results release their lease instead of replacing newer content.
The container disposes its renderer before releasing a resident asset. Re-enable a streamed container by requesting its asset again.
`AssetBundleVegetationSource` provides asynchronous bundle and prefab loading with shared reference counts.
Construct, acquire and dispose on Unity's main thread. The caller must keep dependency bundles resident.
Unity bundle IO cannot be cancelled mid-read; cancellation waits for that operation and then releases the reservation.
Disposing the source prevents new acquisitions; existing leases remain valid until their consumers release them.
Addressables adapters can implement the same interface. The core has no Addressables dependency.

## Shader Graph and DOTS contract

`InstanceShaderInspection.Inspect` reports active URP surface, depth, depth-normal, shadow and motion passes.
Prototype capture checks required passes before taking ownership. URP Unlit retains its native no-shadow behavior.
Basic rendering does not require a profile. Each required draw pass must declare DOTS instancing. The shader must also have an SRP Batcher-compatible material layout.
Keyword/pass inspection cannot prove that arbitrary shader code has a correct buffer layout or deformation model.
Editor checks inspect the resolved URP subshader, including custom and inherited passes.
Player checks cannot detect variants removed by build stripping. Keep All remains required.
`FromPrefab` accepts an explicit required-pass mask; its default requires surface, depth and normals.
Object motion remains disabled when the source material disables that pass. Require Motion explicitly when the application needs it.
A profile supplies optional wind data and conservative displacement bounds.
For appearance data, expose DOTS-instanced float4 properties `_BaseColor`, `_LoogaLightmapColor` and `_LoogaInstanceData`.
The first multiplies source tint, the second multiplies baked/probe diffuse lighting, and custom X can carry stable wind phase.
Default values are white, white and zero. Use `UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT` so ordinary renderers still work.
Apply the same alpha cutoff and deformation to surface, depth, normals, shadow and motion passes.
Motion uses previous object matrices and previous deformation parameters. Displacement must stay within the profile's captured bound.
A supported URP Shader Graph can use basic rendering without edits. Extra per-instance appearance requires the explicit contract above.
The core does not rewrite graphs or infer arbitrary property-buffer layouts.

## Pass-one qualification

On Unity 6000.3.21f1 and D3D12, native/Looga Editor images cover Forward+ and Deferred+ baked lightmaps,
legacy probes, real baked APV data, mesh LOD, mirrored/child transforms, grass cards, billboards and Looga wind.
The isolated player compares nine color/shadow views and reads back object and animated-wind motion.
The native control uses the same Looga shader; this is renderer parity, not a comparison against a vendor wind model.
File pages, real AssetBundle reference counts/cancellation, deterministic generated pages and stationary residency allocations are tested.
Native extraction remains the default. On the small 1,024-record page fixture it is faster than the optional generated backends.
Burst startup can produce a large cold spike. The Compute path includes GPU readback and CPU assembly.
These results do not establish whole-world frame-time gains or complete replacement of authoring tools.

## Scene-object ownership

Add `SceneInstanceProvider` to an owner object and assign loaded scene roots to Sources.
The component operates in edit and play mode. Prefab editing stays native.
Use one root LODGroup per source and one layer across its children. Identical static prototypes share draw storage.
Root movement updates existing slots. Child, hierarchy, material, lightmap and LOD changes rebuild the provider.
The per-frame change check reads every source, so its cost grows with the population (about 90 ms for 14,700 roots).
Turn off Track Source Changes for static populations whose owner calls Configure or Rebuild after each change.
This initial structural rebuild covers the provider, not a localized cell. LOD inspection currently allocates Unity's GetLODs arrays.
Separate large populations into providers until bounded structural updates are available.

The provider changes `forceRenderingOff` only after it has created the replacement draw.
It leaves Renderer.enabled, materials, GameObjects, scripts and colliders intact.
Disable, destruction and scene unload release owned batches and restore the source renderers.
A failed source remains native with an inspector diagnostic. Use Rebuild after correcting an unsupported source.
Nested roots, overlapping render ownership, property blocks, static batches and realtime lightmaps are rejected.
Use separate collider residency for packed populations. Explicit hierarchy snapshot conversion is described below.

```csharp
provider.Configure(sceneRoots);
provider.Synchronize();
provider.enabled = false; // Restore native submission.
```

## Optional Graphics Pro shaders

Graphics Pro's static Lit, Lit Lite, Lit Detail and Skin mesh passes have standard DOTS variants.
The custom lit template and material-extras pass use the same standard instance-ID setup.
The static material family shares one UnityPerMaterial layout across all passes.
Auxiliary passes use that layout too. Static motion uses Unity's object-motion functions. Deformed foliage needs its own historical deformation path.
The RVT surface receiver and SVT receiver/feedback shaders also declare DOTS variants.
The SVT instance profile uses the existing cache registry and collects feedback from BRG draws. Ground RVT receivers retain direct or coarse fallback.
Both products depend on Looga Instancing, but neither depends on the other product. Ordinary compatible materials do not need a Looga profile or shader replacement.
Transparent sources retain native rendering. Graphics Pro vegetation uses an explicit bounded wind profile. The supported material and auxiliary-pass matrix is described under Qualification below.

## Container placement editing

`InstanceContainer.Placement.Id` is a persistent string within one placement set. Empty IDs receive GUIDs during explicit import or configuration.
Keep IDs when saving data or recreating a streamed population. A render-buffer handle is not a saved identity.
IDs are scoped to a placement set. The game must pair them with its own persistent container or world-source identity.
Copying the same snapshot into two containers preserves its IDs. This does not make those containers one gameplay object.

Use `CopyPlacements` for a separate source snapshot. Use `SetPlacements` to replace the set while retaining handles for unchanged IDs.
`AddPlacements` returns IDs for appended records. `ApplyChanges` accepts ID-based additions, updates and removals in one batch.
Duplicate IDs, conflicting operations, unknown removal IDs and invalid transforms reject before source changes.
Source and batch validation is atomic. GPU allocation failures are not a transactional graphics-device recovery mechanism.
Edits run on the main thread. These APIs update serialized source data; a game owns save-game policy and network replication.

```csharp
var ids = container.AddPlacements(new[]
{
    new InstanceContainer.Placement { Position = spawnLocal, Scale = Vector3.one }
});
container.TryGetPlacement(ids[0], out var placement);
placement.Position += Vector3.up;
container.ApplyChanges(new[] { placement });
container.ApplyChanges(null, new[] { ids[0] });
```

Movement updates existing slots without reconstructing the BRG owner. Parent movement, rotation, negative scale and nonuniform scale are supported.
Unchanged synchronization does not allocate or upload source records. Changed sets use linear CPU reconciliation and changed-slot GPU uploads.
Bulk source APIs copy arrays and build lookup data. They are authoring/runtime bulk operations, not allocation-free per-instance hot loops.
Use the lower-level renderer for large, continuously animated populations. Call `Synchronize` before rendering when changes follow LateUpdate.

`QueryOrigins` appends IDs inside a world-space box. It is a linear origin query, not a mesh-overlap or collider query.
Queries and source edits remain available while the component is disabled. Re-enable recreates draws from the edited data.
`TryGetHandle` resolves a source ID to its current handle. Rebuild, disable or source replacement ends that handle's lifetime.
Failed streamed prototype replacements retain the previous population and lease. Disable still cancels loading and releases ownership.

The Inspector can save a new `InstancePlacementAsset` or load a copy from one. Loading supports Undo and Redo.
Shared assets remain unchanged by container edits. Snapshot assets contain placements only; assign the prototype on each container.
Placement IDs survive asset save/reload. New snapshots store a lossless binary TRS payload with UTF-8 IDs. Legacy Unity array assets remain readable.
The payload is separate from streamed vegetation pages. It does not quantize transforms or compress mesh and material assets.
The build check requires Keep All BRG variants for scene providers and containers present in build scenes.
Runtime-only owners created after scene processing must arrange variant preservation in their build configuration.

Hierarchy conversion, mesh brushes, collider residency and edit journals have explicit APIs described below.
No existing world objects are converted by these APIs or Inspector tools.

## Mesh scatter and mask painting

Add `MeshScatterAuthoring` to a scene object. Assign a readable MeshFilter and one weighted InstanceContainer per prototype.
Configure each container's prefab first. The authoring component supports independent surface and target transforms.
Target containers require positive uniform world scale without parent shear. Surface transforms can use nonuniform or mirrored scale.
No source mesh, vertex color, collider, material or importer is changed automatically.

Density specifies candidates per square meter of world-space mesh area. A fixed integer seed determines barycentric positions and prototype selection.
Filters support world height, slope, aspect and smooth inner falloffs. Flat upward surfaces ignore aspect.
Normal alignment, normal offsets, yaw and uniform scale ranges control placement transforms.
Zero-weight targets receive no generated placements. Candidate Limit counts candidates before filtering, including masked-out regions.

In the Inspector, enable Paint Mesh Mask. Use the left mouse button to paint; enable Erase or hold Shift to reverse the operation.
Radius uses world meters. Flow specifies mask change per dab. Falloff controls the radial fade.
The brush affects all mesh faces inside its spherical radius. The circle indicates the radius on the picked face.
Mouse release commits one Undo group. Escape cancels the current stroke. Alt retains normal Scene-view navigation.
Base Mask 1 starts with full coverage; Base Mask 0 starts empty. Clear Painted Mask returns to that base.
Mask dabs are serialized separately from the source mesh and follow its transform. Each author currently supports up to 4096 dabs.

Triangle index and candidate index form a persistent ID under the author's saved source identity.
Density changes retain earlier candidates. Mask edits retain accepted candidate positions and IDs instead of resampling the distribution.
Reseeding changes placement positions. Mesh topology changes can change triangle identity; use Regenerate Surface after those changes.
Existing manual placements and other author prefixes remain in each target. Target replacement removes only this author's previous output.
Copied authoring components must use separate targets. Shared-target identity collisions reject generation.
Generated containers keep their data when authoring is disabled or removed. Use placement snapshots to preserve a fixed result.

Brush changes regenerate only triangle bounds intersecting the affected region. Last Regenerated Triangles reports this work.
`InvalidateSurface(changedWorldBounds)` refreshes edited geometry inside a bound containing both the old and new positions.
Use `RequestRebuild` or `Regenerate` after topology changes. Import and Undo callbacks request a full refresh.
Editor transform changes trigger refresh when Auto Refresh is enabled. Play mode does not generate automatically; explicit API calls remain available.

Generation caches triangle outputs. Candidate sampling is local, but triangle-bound selection and final placement merging are linear scans.
Container reconciliation retains existing handles and uploads changed slots. Source arrays, masks and triangle caches consume CPU memory.
Picking uses mesh triangles without physics colliders. It currently scans triangle bounds; no BVH or asynchronous generation is included.
Unreadable meshes, invalid target transforms, duplicate targets and candidate-limit failures report actionable errors.
Changes to arbitrary mesh vertex buffers require explicit invalidation. They do not have a general Unity authoring event.

Mesh painting does not yet provide texture-mask import, terrain-layer filters or minimum-spacing collision rejection.
Road and RAM masks connect through the explicit adapters below.


## Persistent gameplay edits and colliders

`InstanceEditJournal` stores removal and transform overrides under a source key and placement ID.
Use `InstanceContainer.BindEdits(sourceKey, journal)` before loading a gameplay population.
The game must assign a stable, unique world key. Duplicated prefabs must not share a key accidentally.
`CopyPlacements` returns authored data. `CopyResolvedPlacements` applies the journal without changing the authored source.
Use `Import` to replace a journal already bound to containers. It validates the complete replacement and notifies receivers.
JSON deserialization can load an unbound journal. Rebind containers after replacing serialized data directly.
Call edit APIs on the main thread. The game owns storage, network replication and authority.

`EditedVegetationPageSource` wraps an existing asynchronous page source with the same journal.
Its record key contains tree/detail type, cell coordinates and record order within a source revision.
Keep the source key and record order stable across eviction and reload. Change the source revision after rebaking reordered records.
Invalidate affected resident pages after editing the journal. This adapter does not replace the page cache.

`InstanceColliderResidency` copies supported collider components around explicit `VegetationInterest` regions.
It supports box, sphere, capsule and mesh colliders. It never instantiates prototype scripts or renderers.
Physics residency works while the source container is disabled and does not depend on rendering cameras.
`InstanceColliderIdentity` links a physics hit to the source and placement ID.
Creation and residency limits are separate. Pending Count and Diagnostic report demand above the configured capacity.
Selection currently scans placement origins, sorts candidates and allocates a resolved snapshot each update.
Large collider footprints, nonuniform physics scale and dense-world performance still require qualification.

## Scene conversion

Use GameObject > Looga > Render Selected Roots With BRG for explicit eligible scene roots.
The provider retains the source objects, scripts, colliders and materials. Undo or disable restores native rendering.
Unsupported or overlapping ownership rejects conversion. The tool does not remove vendor components or migrate vendor shaders.
`InstanceConversionTools.BakeSnapshot` copies matching prefab transforms into a disabled preview container.
Save the source scene first so GlobalObjectId values identify the source objects persistently.
Use Tools > Looga > Instancing > Bake Selected Instance Snapshot to select an independent prototype.
The tool adds `InstanceSnapshotPreview` with snapshot rendering disabled.
Enable Use Snapshot to suppress original mesh rendering after the container starts successfully.
Disable Use Snapshot or the preview component to restore the originals. Scripts and colliders retain their original states.
The preview rejects already-owned sources. It restores native rendering if the container becomes unavailable.
Snapshots represent static source transforms. Use SceneInstanceProvider for sources that continue to move or change structure.
Do not enable a second collider-proxy population while original source colliders remain active.

GameObject > Looga > Export Standalone Instance Prototype creates a separate prefab with native mesh, LOD and collider components.
It excludes custom controllers and rejects unsupported renderer types, property blocks and external scene references.
Native internal references are remapped to the new hierarchy. Original assets remain unchanged.
The export reports dependencies under the installed FR2, BRGIR and Flora roots. Replace those meshes or materials before export.
It does not translate proprietary vendor shaders or delete vendor packages.

## Road, water and MicroVerse masks

`PlacementExclusion` accepts a local polyline corridor or a readable generated MeshFilter.
Corridors use world-space radius, falloff and height bands. Height bands preserve vegetation below elevated bridges.
Generated mesh masks use the projected surface with explicit below/above distances. Their edges do not use corridor falloff.
`MeshScatterAuthoring.SetExclusions` connects masks to a scatter population.
Mask events queue the union of old and new bounds. Editor Auto Refresh applies the queued work.
Call `ProcessExclusionChanges` after runtime source updates. Callback handlers do not regenerate placements inside a vendor event.
Use `Clear` after unloading a generated source. Arbitrary vertex-buffer edits still require `Refresh`.

`MicroVersePlacementBridge` belongs to the optional MicroVerse assembly.
Assign a dedicated scene-object provider, explicit object-output roots and any generated road/spline masks.
The bridge reconfigures its provider after each update, so turn off the provider's Track Source Changes for large outputs.
Begin events release native renderer suppression. Finished and cancelled updates recapture only after public height synchronization completes.
After a scene load with no MicroVerse update, the bridge captures when MicroVerse is idle. It does not wait for IsHeightSyncd.
MicroVerse 1.7.0 does not set IsHeightSyncd after the first update of a session.
After a scene load, Scene Objects mode therefore waits until a second MicroVerse update.

Data mode (`ConfigureData`) is an alternative for object stamps. It requires the MicroVerse objects module.
The bridge copies the spawned root transforms into one `InstanceSet`, with one entry for each stamp and prefab.
One set uses one renderer, so each view runs one culling callback for all Data mode prototypes.
The bridge creates the set as a child when none is assigned.
It matches a spawned root to a prefab by the meshes, materials and root-relative transforms of its renderers.
Prefab variants that share meshes and differ only in materials therefore stay separate. Roots that match no prefab stay native.
Data mode waits only until MicroVerse stops modifying and spawning. It does not wait for IsHeightSyncd.
The bridge writes one set entry in each call to `ProcessChanges`, so GPU uploads spread over several updates.
With Strip Native Parts on, the bridge removes the LOD groups and the render-only child objects of each spawned root.
The root keeps its colliders. With Strip Native Parts off, the bridge only hides the renderers.
The set keeps its placements while the output holds hidden or stripped roots.
A stamp needs a Parent Object. Data mode rejects Spawn As Prefab, because the MicroVerse pool can reuse a stripped instance.
`Configure` selects Scene Objects mode and disables the data set. Stripped roots stay invisible until MicroVerse respawns them.
The bridge never starts a MicroVerse bake or changes private synchronization state.
The optional editor bridge queues refresh after Undo, hierarchy changes and asset imports. It ignores these events during Play mode.
`RequestRefresh` supports explicit application hooks. Road and water masks regenerate only intersecting scatter triangles.
Mesh vertex edits must call `InvalidateSurface` with the affected bounds. Topology changes require `RequestRebuild` and `Regenerate`.
The bridge does not infer modified triangles from a vendor completion event. Tests exercise actual installed MicroVerse road generation, generated-object replacement and source deletion.

Import the RAM Placement Bridge sample only when RAM is installed.
Assign exactly one RamSpline or LakePolygon and a dedicated PlacementExclusion.
The adapter consumes public generation events and MainMeshFilter output. It does not generate water or modify terrain.
Disable, missing geometry and source unload clear its mask. Re-enable captures the current generated surface.
The sample includes river/lake event and cleanup tests, plus actual river generation, control-point edits and Undo checks.

## Direct ground blending and interaction

`TerrainGroundBindings` exposes up to sixteen explicit native height textures and optional world-UV tint maps.
Only one binding component owns the globals. Terrain transforms must have identity rotation and unit world scale.
Height sampling averages shared tile edges. CPU normals use adjacent terrain samples across those edges.
In Looga Vegetation Lit, Ground parameters control tint strength, normal strength, local conformance limit and blend height.
Conformance preserves the placement pivot and adjusts vertex height relative to the terrain below that pivot.
Supply consistent tint-map edges. An assigned tint map is not an automatic evaluation of a MicroSplat material.
Direct height/tint bindings work without either virtual cache.
Height history preserves the previous source texture across a terrain change. Multiple cameras share the previous frame's sample.
Stable tiles use one owned height copy. A changed tile can retain two copies until the next quiet frame.
Disable Track Height History only for terrain whose heights remain static.

Extra Map and Extra Data supply application-defined inputs for each terrain surface.
Custom shaders can include `Packages/com.loogasoft.loogainstancing/ShaderLibrary/VegetationGroundData.hlsl` and call `VegetationGroundExtra` with world XZ.
The function returns the sampled RGBA map and per-surface values separately. Shared edges average both inputs.
An unassigned map returns white. No biome or wind interpretation is imposed on these channels.

## Optional cached ground receivers

Add `TerrainGroundCacheBindings` from the optional GroundCache assembly when Looga terrain RVT is already configured.
Assign up to sixteen TerrainVirtualTexture sources that use one shared VirtualTextureSettings asset.
The receiver borrows the existing three physical page atlases. It owns one concatenated page-address buffer, not another surface cache.
Table revisions update only changed tile ranges. Invalid or missing pages fall back to direct ground sampling.
The receiver selects the finest resident page and checks coarser pages if necessary. It does not generate independent vegetation feedback.
Different cache identities and unsupported terrain transforms produce diagnostics and direct fallback.
Disable or invalidate releases the global bindings before the source resources can be destroyed.

The basic terrain producer declares world-space normals with the `LoogaVTNormalSpace=World` shader tag.
Other producers default to terrain tangent-space normals for compatibility with existing MicroSplat exports.
Use the tag only when the producer writes world-space normals into its normal atlas.
The tested MicroSplat producer supplies stable albedo, normal, occlusion, smoothness, emission and metallic channels.
This does not reproduce all live MicroSplat distance effects on vegetation. Shader extensions need their own receiver policy.

Graphics Pro Grass and Foliage use their package's RVT through Rvt Ground Params, disabled by default.
The vector controls color/material blend, normal blend, blend height and height offset.
Empty fine clipmaps try a coarser valid clipmap. The receiver uses the existing Graphics Pro capture atlases.
Bark has auxiliary rendering support but no automatic RVT ground blend.
Native/BRG surface comparisons and real basic/MicroSplat page readbacks passed focused tests.
D3D12 player comparisons also cover cached normals, object motion, changing ground heights and Graphics Pro deformation.
The focused Forward+ and Deferred+ cases match native rendering for surface color, normals and motion.
These checks cover the installed material contracts. Arbitrary shader extensions require their own receiver policy.

`VegetationInteractor` supplies up to sixteen interaction spheres to the Looga vegetation shader.
Interaction displacement remains bounded by the material limit and contributes to conservative instance bounds.
Graphics Pro foliage, grass and bark now include explicit DOTS and auxiliary passes with bounded deformation.
Focused tests cover previous deformation and nonzero motion with both renderers.
The fixed-pipeline material matrix also covers the supported Graphics Pro lighting presets, custom specular models, texture/cutout variants and real SVT feedback. It does not qualify arbitrary user shader code.
Include the actual player pipeline in Graphics or Quality settings before building validation scenes.
URP can strip required variants when a pipeline is only referenced by a scene asset.

## Shared package migration

General APIs and editor tools now use `LoogaSoft.Instancing` and `LoogaSoft.Instancing.Editor`.
Native SpeedTree profiles use `LoogaSoft.Instancing.SpeedTree`. Update source imports and assembly references.
Moved scripts retain their asset GUIDs and declare their former serialized type identity with `MovedFrom`.
Saved placement IDs and binary formats are unchanged. Existing legacy prefab and placement assets pass reload checks.

`SplinePlacementBridge` is in the optional `LoogaSoft.Instancing.Splines` assembly.
Assign a Unity SplineContainer and a dedicated PlacementExclusion. It samples a world-space corridor through the public spline API.
Spline edits, Undo, source replacement and transforms queue mask updates. Disable or source loss clears the mask.
Use this bridge with road exclusion widths and height bands; it does not alter a road, terrain or source spline.

## Qualification

The shared extraction and completed authoring integration pass 83 focused Editor tests.
Windows/D3D12 players pass 130 native/BRG comparisons in each of Forward+ and Deferred+.
Surface color, normals and object/deformation motion match exactly in these fixtures.
The matrix includes Graphics Pro Lit, Lite, Detail, Skin, vegetation, custom template, lighting presets and mapped/cutout variants.
A separate BRG-only feedback phase loads three SVT pages from disk, then matches native color and motion.
Core dependency tests and exported-prototype checks reject dependencies on FR2, BRG Instanced Renderer and Flora.

These are functional checks with small deterministic fixtures. They are not dense-world performance or release-soak measurements.
Authored SpeedTree animation still requires a model with nonzero authored wind quality.
Clean-project installation, broad camera and density matrices, and long-running stability remain release qualification work.

D3D12 debug-layer runs report native render-pass preservation errors also reproduced with a Unity MeshRenderer and all custom features disabled.
The exact root cause and long-running stability remain open; image parity is not a clean debug-layer qualification.
