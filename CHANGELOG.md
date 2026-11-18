# Changelog

## Unreleased

- Water: `_WATER_FALL` and `_WATER_FALL_DEPTH` are `multi_compile_local`. `LoogaWaterFall` enables them on material
  copies at runtime, so player builds stripped the `shader_feature` variants and falls rendered as plain water.
- Stamps: removed the console log "the saved result is not used at the load". The reason stays in the update note.
- Surface: packed control maps (`LoogaPackedControls`). Two RGBA8 maps hold the 4 strongest splat layers of each
  texel: the layer indices / 255 and their weights with a sum of 1. Each mip packs the same mip of the control maps
  (`Resources/LoogaControlPack.compute`). The surface and the terrain ground read them through
  `LoogaTerrainControlPacked.hlsl`: the reader loads the 4 texels of the bilinear footprint at the mip that a bilinear
  sample reads, adds the weights of each layer and keeps the 4 strongest (on equal weights the higher layer, as
  before). `LoogaTerrainSurfaceBinding` packs on the GPU when the surface applies, and it packs the region of each
  alphamap texture callback again (a GPU write such as a stamp world write does not change the texture update count).
  New binding setting Pack Control Maps (`PackControls`, on by default); off keeps the control map read. New per-tile
  values `_LoogaControlPacked`, `_LoogaControlIndex` and `_LoogaControlWeight` in the three surface shaders, and
  `_GroundControlPacked`, `_GroundControlIndex` and `_GroundControlWeight` for the terrain ground.
  `LoogaTerrainGroundBlock` takes the surface tile values from the new `LoogaTerrainSurfaceBinding.FillTileValues`.
  Player builds: the new step `LoogaPackedControlsPlayerBuild` ships the packed maps as textures without a CPU copy and
  gives each packed terrain a copy of its terrain data with 16 x 16 control maps. Terrain colliders and components
  with terrain data fields get the copy; the terrain data assets do not change. Kubera World: 30 layers at 1024 x 1024
  are 8 RGBA32 control maps with mips and a CPU copy, 45 MB of GPU memory per 1 km tile (2.45 GB in the player); the
  packed maps are 11 MB per tile and have no CPU copy. Renders against the control map read differ only within the
  difference of two equal renders.

- Stamps: new `LoogaMaterialEffector` (Looga/Terrain/Stamps/Material Effector) on a `LoogaObjectStamp`. It changes
  material values of the stamp placements through Looga Instancing effector values (Graphics Pro 2.x, properties
  marked with `[LoogaEffectorTarget]`). Weighted variants: each placement takes one variant by a stable random value
  of its position. A value is Constant or Random (per placement between a minimum and a maximum) and uses Set,
  Multiply or Add. A value applies only to the prototypes whose materials mark the property. One constant variant
  stores one shared row. `LoogaStampWorld.WritePlacementEntries` writes the effector values of each stamp and prefab
  entry, and a stamp without an enabled effector clears them. `LoogaStampWorld.RewriteEffectors(stamp)` writes them
  again from the stored placements, so an effector change does not evaluate placements (about 0.2 ms for a stamp
  without values). Inspector: property dropdown from the marked properties of the stamp prototypes, value fields by
  property type (float, HDR color, vector), "applies to n of m prototypes". The stamps editor assembly now
  references `LoogaSoft.Instancing.Runtime`. Tests: `MaterialEffectorTests` (shared row, unmarked prefab, weighted
  variants stable over rebuilds, random range).
- Grass: new `ProceduralGrassRenderer` setting Minimum Screen Width (pixels, 0 = off, draw parameter 2 w). Each blade
  row that shows narrower than this width on screen gets wider across the view, at right angles to the view ray and to
  the blade axis. The minimum pixel width only limits the world width, so edge-on blades and tapered rows stayed
  thinner than a pixel and flickered (Kubera World, 2026-11-16). The blade does not turn, so its facing, twist and
  shading stay. The tip stays one vertex. It replaces the Edge-On Turn and Tip Width settings of the same release,
  which did not stop the flicker and are removed.
- Grass: the procedural grass no longer uses the gusts of the Looga wind field. The gust press, its extra bend, the
  flutter and the gust sheen (albedo and smoothness) are removed from `ProceduralGrassBlade.hlsl` and
  `LoogaProceduralGrass.shader`. At low views the moving gust edges showed as hard lines over the grass (Kubera World,
  2026-11-16; with the gusts off the lines stopped and the user kept that look). The grass keeps the field strength,
  the steady lean and the idle sway, so it looks as with a gust amplitude of 0, and each blade vertex evaluates no gust
  noise.
- Stamps: a refused prefab mesh vertex read is no longer kept for the session. After an editor start, Unity refused
  `Mesh.vertices` for some prefab meshes without Read/Write for a short time ("Not allowed to access vertices on
  mesh ..."); a later read of the same mesh worked. `PlacementFieldMaps.MeasureRadii` and
  `PlacementEvaluator.MeasureBaseRadius` then cached fallback sizes for the whole session, so stamps that read
  placement fields or a ground fit (Kubera World: three "Rocky Hills ... around rocks" stamps, 26 detail layers on
  four tiles) evaluated differently. Both now read through `PlacementFieldMaps.TryReadVertices`, keep no measure from
  a refused read, and the world notes the tile (`NoteRefusedReads`); the editor updater updates that area again
  2 s after the last refused read (`LoogaStampWorld.TakeRefusedReadRetry`). The stamp code version changes, so the
  next load of a saved result runs a full update once. Checked: retry path with a simulated refusal; an editor start
  with a full update afterwards gave identical placements (61887) and alphamaps.
- Stamps: a quit or a script reload soon after a scene save no longer loses the trusted load record. The save reads
  the texture maps back from the GPU over the next frames, and the record is written when the last readback is done;
  a quit in that time dropped the readbacks. `LoogaStampTrustedLoad` now keeps the readback requests and completes
  them at `EditorApplication.quitting` and `AssemblyReloadEvents.beforeAssemblyReload` (the callbacks run inside
  `WaitForCompletion`). Kubera World: save and quit in the same editor tick, record written 0.8 s after the save,
  next editor start used the saved result (work ended 51.1 s after the domain load).
- Stamps: the trusted load now works at an editor start, not only at a scene open. Two causes made every editor start
  ignore the saved result (Kubera World: full load update, work ended about 104-110 s after the domain load):
  `LoogaRoadLive` looked for the road network in its static constructor, before the scene was loaded, and then only
  after a hierarchy change, so the shoulder and clear masks came after the release and the source hash differed.
  It now searches again when it has no network and the number of enabled stamp worlds changes. The updater also lets
  the first change capture of a new world wait one editor tick (`WorldState.Captured`), so editor hooks such as the
  live road masks have subscribed before it, whatever the order of the update callbacks. Kubera World editor start
  with a valid record: work ended 58.5 s after the domain load; afterwards 0 alphamap areas differed from a full
  evaluation and the stored placements were identical.
- Stamps: a load that does not use the saved result now logs why ("Looga Stamps: the saved result is not used at
  the load: ..."): no record, a changed or dirty terrain data file (with both file stamps), a changed scene file or
  stamp code, a placement set change, a modifier that still waited after the wait limit, or source textures that
  differ from the save. The record now also holds the name, texture names and hash of each source
  (`Record.SourceParts`), so the message names the first source that differs. Older records still load; their
  message only gives the cause.
- Water: in edit mode a body build no longer solves the water network again for each body. Before, each surface build
  of a network member read `LoogaWaterNetwork.Graph`, which solved the whole network when any body had changed (about
  0.4 s), so a scene load solved it about once per member. Now bodies read `LoogaWaterNetwork.BuildGraph`: it solves
  only when no solve ran in this editor update and the first solve after `OnEnable` is not waiting; otherwise the
  body reads the last solve and one solve is queued (as in play mode). Each body keeps the network data of its last
  flow write (`LoogaWaterBody.TryGetWrittenFlow`), and `NotifyBodies` compares with it, so a body that wrote its flow
  from an older solve updates exactly when its data changed. The queued solve uses `EditorApplication.delayCall`, so
  in an editor without focus the network flow waits until the editor has focus again.
- Water: the outline tests of the water network (`WaterNetworkGraph.IsInside`, `OutlineDistance`, used by
  `SignedOutlineDistance`) skip chunks of 32 outline segments by their bounds, with the same per-segment arithmetic.
  The sea of Kubera World has an outline of 14515 points; a river that overlaps it ran both tests for each vertex
  near the mouth (1.3 ms each). Now 0.08 ms; a network solve takes 77 ms instead of 404 ms. 6600 test points gave
  the same results as the full tests. Kubera World scene open: water `OnEnable` 8.4 s -> 4.4-4.9 s, the scene load
  call 13.4 s -> 10.4-10.9 s, one network solve per load. The water output (meshes, flow and regime UVs, shore
  textures) of all 210 bodies was identical before and after the change.
- Stamps: trusted load (`LoogaStampTrustedLoad`, editor). After a scene save in which a stamp world is settled
  (`LoogaStampWorldUpdater.IsSettled`, no modifier waits for its texture source, all texture maps and placements
  stored), the texture layer and weight maps and the stored placements go to `Library/LoogaStampWorld/Trusted`, with
  a record of the scene and terrain files (size and write time), the stamp code version and a hash of the source
  textures of the texture and placement modifiers. At the next load (editor start or scene open) the world takes the
  maps and placements back when the files and the code are unchanged. When the source textures then also hash the
  same, the load update of the modifiers that waited for their sources is replaced by a GPU check
  (`LoogaStampWorld.StartTextureCheck`, kernel `CountAlphamapChanges`): the textures are evaluated without a write,
  four terrains for each editor tick, and compared with the saved alphamaps. Only the areas that differ update
  (Kubera World: 4 of 49 terrains differed at opens after saves of a full texture rebuild; the cause was not found).
  The record holds `LiveUpdatesOnly`: false when `RebuildTextures` ran since the scene load
  (`LoogaStampWorld.TexturesRebuiltSinceLoad`, also the full load update without a saved result). Only such records
  are checked; a save of a state that only live updates wrote is used without the check. Any other difference gives
  the normal load update. The stamps are written as GlobalObjectIds. `StampPlacements.Write/Read` take the ID
  mapping of the caller. Kubera World scene open: full load update 72-74 s, with the check 65 s, without the check
  39 s (alphamaps and placements equal to a full evaluation after each).

- Stamps: the trusted load finds the saved stamps with one `GlobalObjectId.GlobalObjectIdentifiersToObjectsSlow`
  call. Each single call took about 63 ms (Kubera World, 88 stamps: 5.6 s in the first editor tick, now 0.07 s).

- Stamps: `LoogaStampWorld` copies the heights and alphamaps of terrains with GPU writes that are not yet on the CPU
  when it is disabled outside a script reload (scene close, editor quit) and marks their TerrainData dirty. Before, a
  scene close without a save lost the list of these terrains: the terrain assets stayed loaded with GPU-only changes
  and no dirty flag, so a reopen could trust a saved result against them.

- Stamps: after a load, the editor updater collects the areas of the texture and placement modifiers that waited for
  their texture source at the first capture, and updates them together when no modifier of that set waits any more
  (at most 240 s after the load). The world does not count as updating meanwhile, so the tools that make the source
  textures keep running. Before, each group of sources that became ready updated all terrains again (Kubera World:
  9 updates of 42-43 of 49 terrains at each editor start).

- Geology: the script reload handover (`GeneratedReloadHandover`) and the generated mesh cache key now use the version
  of the generator code (`GeneratedReloadHandover.CodeVersion`), not the assembly version. The version is a hash of
  the files in `Runtime/Geology`, `Runtime/Surface` and `Runtime/Water` (code, shaders and compute shaders). Before,
  a change of any code in the `LoogaSoft.Terrain.Runtime` assembly, for example the grass, or a clean compile, refused
  the handover and missed every cache file. Each such reload then built all rock faces, formations, walls, caves and
  water bodies again (Kubera World: reloads of about 117 s). The first reload after this change builds once, because
  the key is new.
- Stamps: `LoogaStampWorld` keeps its texture layer and weight maps over a script reload. The next domain takes them
  back in `OnEnable` when the stamp code (`GeneratedReloadHandover.StampCodeVersion`, the files in `Runtime/Stamps`)
  did not change. Before, a reload released the maps, so the first texture update after each reload, also for a small
  edit, rebuilt the textures of all terrains (Kubera World: frames of 37 s and 24 s, then vegetation and ground cover
  updates on every terrain).
- Stamps: `LoogaStampWorld` keeps its stored placement result over a script reload. Before the reload it writes the
  result to `Library/LoogaStampWorld` (stamps and terrains by instance ID; Kubera World: about 80 MB), and the next
  domain reads it in `OnEnable` when the stamp code did not change. Before, the first placement update after each
  reload placed all terrains again in one frame (Kubera World: 22 s).
- Water: `LoogaWaterBody.RefreshShore` stops when the shore texture does not change in the area
  (`WaterShoreField.Refresh(Rect, out bool)` compares the depth, the dry state and the obstacles of each texel). The
  bodies refresh their shore after every stamp world update that overlaps them, also after a texture edit. Each refresh
  solved the currents, wrote the vertex flow, built the shore waves and queued the flow of the joined rivers again.
  Kubera World: the sea covers the whole world, so every edit cost about 2.7 s in the settle tick and 4.1 s of flow
  builds.

- Grass: Sway Coherence on `ProceduralGrassRenderer` (0 to 1, default 0 = as before). Each blade swayed with its own
  random phase, gust delay and flutter phase, so neighbouring blades moved in opposite directions every frame. On thin
  blades that showed as jitter, most of all without temporal antialiasing. Higher values move neighbouring blades
  together in waves (the random phase, delay and flutter offset scale with 1 - coherence).
- Stamps: `LoogaStampWorldPlayerBuild` saves the current stamp result before a player build. It stops the build while
  a stamp world updates, waits for the placements, copies the heights and alphamaps to the CPU and saves the terrain
  data. An asset save without a scene save (the save at the start of a build, or a save of a scene without changes)
  wrote old CPU copies, so a player could ship an old result (Kubera World: cliff rock and scree patches missing).
- Terrain: a `LoogaTerrainSurfaceBinding` above its `TerrainRenderer` on the object (enabled first) now gives the renderer
  its surface when the renderer is enabled (`TerrainRenderer.Enabling`). Before, such a renderer drew the exported
  surface asset after each scene load; only a renderer above the binding got the surface.
- Water: player builds ship the water surfaces. `LoogaWaterBody` is a shipped generator of `GeneratedMeshPlayerBuild`:
  before a standalone build it writes its built output to a file (mesh with flow and regime, shore and regime texture,
  coverage mask, shore wave field, and the obstacle texels of an area with shore waves), and a player loads the file
  in `OnEnable` and does not build the surface, the shore and the currents at the scene load (marker
  `LoogaWaterMarkers.BodyLoad`). The file key changes with each new output, so a build writes only changed bodies again.
  The file holds the network flow of the body (`LoogaWaterNetwork.TryGetBodyFlow`); the first network solve in the
  player builds the flow again only when it differs. Flow zones that enable during the scene load do not queue a flow
  build of a shipped body. The plunge waves and the near shore window still build in the player.
  `GeneratedMeshPlayerBuild` now reads the key of each generator again after all builds, and a generator that built
  for the build but changed again later (an area queues the flow of the rivers that join it) builds once more. `WaterShoreField.TryGetSurface`
  is replaced by `IsObstacle`, its only use.
- Water: in play mode a water body builds first in `Start`, not in `OnEnable`. The water network and the flow zones of
  the scene are then enabled, so a body does not build its flow again in the first frame (Kubera World: 6.7 s of flow
  builds after the load became 0.9 s). A body that is enabled later builds in its first `Start` or `Update`, as before.
- Water: in play mode `LoogaWaterNetwork` solves at most once in a frame. Each body build changes the network inputs, so
  the bodies that build in one frame solved the whole network again for each body. The next frame solves once with all
  changes. `Solve()` still solves at once.
- Geology: player builds ship the generated geology meshes. A player built them from their splines at each scene
  load (25.5 s of a 36.8 s Kubera World load). Before a standalone build, `GeneratedMeshPlayerBuild` checks the cache
  file (`GeneratedMeshCache`) of each rock face, rock formation, cliff wall and cave of the open scenes and builds a
  generator again when its file is old. After the build it copies the current files into
  `StreamingAssets/LoogaGeneratedMeshes`, named by the scene, the hierarchy path and the type of each generator
  (`GeneratedMeshCache.PlayerFileName`). In a player these generators load the shipped file (`LoadPlayerMeshes`) and do
  not build. The files hold the meshes that the editor shows, so a player also gets the rock formations on their stamp
  snapshot. A formation whose snapshot waits stops the build with a message; the editor builds it in the next
  seconds. A generator without a current file, in a scene that was not open, or on another target builds at the scene
  load as before. A cave does not build in the build step, because its build writes terrain holes. The cache read code
  (`TryRead`, `OpenFile`, `RockFaceOutput.LoadCache`, `RockFaceChunks.BeginMesh`/`EndMesh`, the cave entrance records) is
  now in player builds too; a null key skips the key check. `GeneratedMeshCache.WaitForWrites` waits for the file
  writes. New internal interface `IGeneratedMeshSource`.
- Stamps: player builds leave out the stamp textures (`LoogaStampPlayerBuild`): the height maps, falloff textures and
  masks of the height, texture and placement modifiers. A player does not update the stamp world, so it does not read
  them (2.2 GB of GPU memory in Kubera World). A rock face or rock formation on a `LoogaSnapshotGround` cannot build in
  a player now (`RockFaceGroundSource.ReadableInPlayer` is false), so the player build stops when such a generator has
  no current mesh file (`IGeneratedMeshSource.PlayerCanBuild`). A rock face whose build finds no steep face now writes
  an empty cache file too, so a player loads the empty result and does not build the face (7 faces in Kubera World).
- Geology: profiler markers for the generated geology builds and the shipped mesh loads (`LoogaGeologyMarkers`), and
  for the water body, flow and shore builds (`LoogaWaterMarkers`). A ProfilerRecorder in a development player reads
  them, for example for a load time measurement.
- Roads: player builds leave out the full slab meshes of slabs with level of detail chunks
  (`LoogaRoadSlabPlayerBuild`). The disabled slab renderer kept the mesh only for the editor tools, so the build held
  the slab triangles two times (49 MB in Kubera World).
- Roads: far levels of detail for the asphalt slabs (`LoogaRoadSlabLods`). The slab builder and the live slab update
  split each slab mesh into chunks of 50 m on a world grid (by triangle centre). Each chunk has a LODGroup with LOD0
  (the slab triangles), LOD1 (quadric error 0.02 m, from 120 m) and LOD2 (0.08 m, from 350 m), with no cross fade.
  The decimation locks the open edges of each chunk, so chunk borders and the edge lip stay closed at all levels.
  The chunks keep the vertex streams of the slab (road coordinates in TEXCOORD1, joins in COLOR), so the road wear
  does not change. The chunk meshes are sub-assets of the slab mesh asset. The slab object keeps its MeshFilter with
  the full mesh (live update and build comparison) and its renderer is off. Context menu of `LoogaRoadNetwork`:
  Build Slab Levels Of Detail applies the chunks to the slabs in the scene without a road rebuild.
- Geology: `RockDecimator` is internal to the road editor assembly too (`InternalsVisibleTo`).
- Surface: three new layer blend modes. Height Offset (3) adds the texture height to the weight (Mishkinis 2013)
  instead of multiplying them, so a layer can move an edge only within Height Reach and does not show as speckles
  far inside another layer. Crisp Edge (4) adds a band-limited world edge noise (Edge Noise Size and Strength) and
  sets the transition to Edge Width screen pixels at every distance; the shader reads the control maps and the noise
  one pixel away for the width, because the surface can shade in divergent flow. The global Height Blend Contrast is
  its smallest transition, and it ignores the per-layer interpolation contrast. Variance Preserving (5) is a linear
  blend that scales the blended albedo and layer normals away from the blended layer means by
  1 / sqrt(sum of squared weights), limited by Variance Gain Limit (Heitz and Neyret 2018), so soft transitions keep
  their contrast. It uses the linear path. The layer property object row now holds the adjusted mean albedo in yzw.
  None of the new modes uses the camera distance, so cache pages keep one look per footprint. Modes 0 to 2 give the
  same result as before. New material vector `_LoogaSurfaceEdge`; `_LoogaSurfaceBlend.w` is the height reach.
- Surface: a change of a surface setting now invalidates the virtual texture pages of each bound terrain
  (`LoogaTerrainSurfaceBinding`). Before, the cached far terrain kept the old look, for example the old blend mode,
  until another change invalidated the pages.
- Caves: after a script reload, `LoogaCaveEntrance` takes the terrain surface mesh material of its cover submesh
  again (`RefreshDestroyedGround`). The reload destroyed that runtime material and the entrance kept the destroyed
  reference, so the cover drew magenta.
- Caves: player builds compile again. `LoogaCaveSystem.OnEnable` called the editor-only `LoadFromCache` outside
  `#if UNITY_EDITOR` (error CS0103 in the player build). A player builds the cave at load.
- Player builds: `GeneratedReloadHandover` has player versions of `Store`, `TryTake` and `Find` (no handover in a
  player). `LoogaWaterNetwork` called them outside an editor guard (error CS0117 in the player build).
- Surface: region colour map (`LoogaRegionColor`). One colour map over one world area, with an optional weight map,
  replaces the layer colour on the terrain surface. Virtual texture pages store the ground before the map, and the
  screen pixel applies the map at its own camera distance (`ApplyLoogaCachedRegionColor`): each page mip used to hold
  the map at the strength of its own footprint, so a mip change, a coarser fallback page or a residency bias step
  shifted the colour while the camera moved. Pages need no refill when the map changes. Vegetation that reads the
  ground cache gets the ground before the map. The layers keep their brightness
  variation relative to their measured mean albedo (Layer Detail). Strength can change between a near and a far camera
  distance. A typical source is the colour output of a terrain generator for the area of its heightmap. Brightness and Saturation grade the map colour to the scene light.
- Water: one water surface across falls (design section 6c). `Looga/Water` has a fall mode (Fall Sheet, keyword
  `_WATER_FALL`) that draws a fall sheet from its per-vertex fall data (progress, travel, stretch, thickness, wall,
  shell): streaks, clumps, white water, foam shells, pool foam, the jet depth and the soft depth, ported from
  `Looga/Water Fall`. The surface code moved to `Shaders/LoogaWaterSurface.hlsl` (no change for water bodies): the
  water light (refraction, absorption, scatter, probe, screen-space and planar reflection, sun) is one function that
  the bodies and the fall sheets both use. A `LoogaWaterFall` draws its sheet with a copy of the material of the
  water that feeds it, with the `_Fall*` values of its fall material, so near the brink the sheet draws exactly the
  body look and the fall look comes in past the brink over the Clear Handoff. The sheet has one submesh and three
  materials: a depth prepass (white water and opaque body look), the sheet, and the soft depth for the mist. The lip
  pass (submesh 0 with the river material, the crest lip copy without ocean waves) and the lip material are removed;
  `SetMaterials` takes the fall and the depth material only. `Looga/Water Fall` is no longer used.
- Water: Surface Depth on `LoogaWaterReflectionFeature` (default off). The transparent objects after the water read
  a camera depth texture with the water surfaces in it (new pass LoogaWaterDepth of Looga/Water and
  `Hidden/Looga/Water Surface Depth`), so a height fog overlay fogs water by the distance to its surface and not to
  the ground under it. The water reads the opaque depth through `_LoogaOpaqueDepth`.
- Water: lip rush. A fall setting Lip Rush (0 to 1) makes the water before the brink rush: boils, white streaks
  and cloudy water that hides the bed under the lip and the drop past it. Lip Ripples may now be above 1 (choppy
  water toward the brink). Both are functions of the distance to the brink on the body and on the sheet.
  Near the camera the rushing water gets at least the average foam that it shows far away, so the lighter band
  before the brink does not go away up close.
- Water: the weld field fades its span out over 8 m past each end of the weld line, and the distance to the brink
  grows there, so the calm, the lip ripples and the rush fade out beside the ends of a fall. Before, they reached
  past the ends onto the shore beside a long sheet approach.
- Water: fall streaks move exactly with the water. The three streak layers of fall mode use the travel time of the
  water (like the clumps and the pool foam) instead of two flow phases with a noise phase offset. The offset came from
  a fine foam texture and shifted the texture by up to about 7 tiles in a fixed pattern, so the streaks changed in
  place and did not move down with the water. The sheet mesh now has one streak rate per sheet (the rate at the mean
  speed of the drop), so the streaks are shorter in slow water and longer in fast water. `_FallFlowCycle` is removed.
- Water: fall setting Brink Radius (default 0 = free jet shape). The water turns over the brink on a round arc of
  this radius, tangent to the level of the feeding water and to the falling sheet, so the top edge is not sharp.
- Water: fall streak texture columns (one tile across) get a random shift along and across the flow, with a blend of
  0.2 tile at the column edges that keeps the contrast, so the streak tiles do not repeat in rows or lanes. The
  samples use the gradients of the unshifted position, so the column edges do not select a blurred mip.
- Water: crest falls of the network get mist, spray and splash (`WaterEffectPlanner.PlanCrest`). The plunge lies the
  plunge distance of the crest sheet path out from each lip point, and the face mist follows the sheet path, so the
  effects follow the sheet. Effect falls and crests share the station placement (`PlanLip`). `FaceDistance` skips
  level parts of the path, so the top of the face is where the water starts to fall, not the sheet start.
- Water: removed the retired `Looga/Water Fall` shader (`LoogaWaterFall.shader`). Fall mode of `Looga/Water` replaces it.
- Water: `Looga/Water Mist` fades in front of the ground with the opaque depth (`_LoogaOpaqueDepth`) while Surface
  Depth is on. The camera depth texture then holds the water sheets, and the mist faded out over the ground soft distance
  in front of them: a gap in the face mist and hard lines along the sheet edges. The soft depth fade (water sheets) now
  grows with the particle size like the ground fade. The rising plume of tall falls starts lower (height 4, box 6 x
  reach, full station width), so it rises out of the plunge cloud without a gap.
- Water: `Looga/Water Mist` (network effects) takes one sun shadow value per particle, the mean of 9 samples over its
  area in the vertex stage. A shadow per pixel cut the flat particles along straight lines where a shadow edge crossed
  the mist. Particle systems and screen space shadows keep the shadow per pixel.
- Water: `Looga/Water Mist` lights the mist as a medium: the sun scatters to all sides with a Henyey-Greenstein phase
  (new `_SunScatter` 0.6, `_ForwardScatter` 0.35) and the ambient is the mean of the sky light from above and from the
  camera side. `_Translucency` is removed. The surface light on the camera-facing quad left mist with the sun behind it
  dark, so materials used a constant emission, which made the mist glow in the shade.
- Water: free overfall shape. A sheet without a structure profile (crests, plain river falls) follows a free overfall
  (Rouse): the head over the lip is 1.5 critical depths, the surface draws down on a parabola to the brink depth of
  0.715 critical depths, and the jet leaves the lip at the brink speed (flow per metre / brink depth) with the brink
  slope of the drawn-down surface (0.39). The thickness down the face is the flow per metre at the falling speed. The
  crest landing, impact speed and plunge use the brink speed, so the sheet, the mist and the plunge stay together.
  Before, the jet left the lip level at the critical speed and landed about 5 m short on a 180 m crest.
- Water: the face mist and spray of tall falls lie in slices of at most 12 m (up to 8) down the sheet path. Each slice
  is on the face at its height and its particles move out with the face slope, so the mist follows the curve of the
  falling water. One tall box stood in front of the upper face, where the face leans back to the lip.
- Water: water clock. `LoogaWaterFall` sets `_LoogaWaterClock` (whole 1024 s periods and the seconds in the period,
  from a double time), and the fall textures take their phase from it (`WaterClockPhase`). `_Time.y` is a float, so
  after hours of editor time the fall textures moved in uneven steps (a flicker at the top of tall falls).
- Water: flow zones change only the water near their own height. A zone applies to a body when the surface height
  range of the body is within 10 m of the height range of the zone spline. Before, a zone matched by its XZ rectangle
  only, so a current at the top of a crest also drove the lake 180 m below it toward the fall. Place a zone spline at
  the level of its water.
- Water: crest joints. The water network splits each crest lip into pieces of about 40 m. Each piece pulls the water
  of the spilling body to the lip at the brink speed: the speed falls off over 30 m upstream, and the pull applies
  after the flow zones, so it wins at the lip. Each piece also makes a plunge on the water below where its sheet
  meets the water (impact foam). The crest plunge flows straight out along the piece direction, without the spread
  of a single fall plunge, at most 4 m/s, and also between the fall face and the landing line (half speed at 40 m
  behind it), so no water moves toward the fall. Spread fans of side by side pieces overwrote each other and made
  hard edges and crossing flows. The pieces are sinks and sources of the current solve.
- Water: in a current, the foam detail of water without ocean waves is the current foam of the ocean path (hex tiles
  without a repeat, one fixed flow cycle), and the foam under the surface moves with the two current phases. The
  detail for slow water shortened its cycle in fast water, so the foam pulsed, and its plain tiles repeated in rows
  over the wide foam below a fall.
- Water: plunge waves. Below the plunge of a network fall the water body has a short-crested chop with whitecaps.
  `WaterShoreWaveField.BuildFromLines` solves the time from the plunge lines over the shore texture (wave speed from
  the depth, at most the deep water speed of the period) and carries the distance along the lines where each wave
  started (`_PlungeAlongMap`). Four wave trains run out at different angles and lengths in these two coordinates, so
  the crests follow the plunge and the shore and are exact far from the world origin; a slow phase noise per train
  breaks them up also past the ends of the lines. Whitecaps form in a band behind the main crests where the sum of
  the trains is high, so they come as short soft streaks that move out and change. The whitecaps widen and fade to
  about 12 % where a pixel covers 4 cm to 25 cm of water, so far away they do not draw thin, hard lines, and they stay
  off past the ends of the plunge lines, where the trains run out in rings. `Looga/Water` (with and without
  Ocean Waves): Plunge Wave Height (0.25 m, 0 = off), Period (2.5 s), Reach (160 m), Plunge Foam (1.6), Plunge Foam
  Time (1.5 s, unused by the chop). `WaterClockPhase` moved before the shore waves.
- Water: crest impact foam comes from short boils of random strength along each crest piece (about one per 14 m,
  4 to 9 m long, strength 0.3 to 0.75) that wander slowly along the landing line and pulse, so the outflow carries
  thin, wandering streaks instead of one broad sheet of foam.
- Water: welded lips. The sheet starts on a weld line at Sheet Start upstream of the brink, at the level of the feeding
  water (no tuck under the river), with the flow and regime values of the body mesh on that line
  (`WaterSurfaceSampler`). The fall gives the line to the body as a signed distance field (`WaterWeldField`, 0.5 m,
  Burst). The body surface and the ocean near grid end exactly on the line, the waves of the body calm to rest on the
  line (Weld Calm, 10 m) and the ripples fade to Lip Ripples (0.4) at the brink, the same on both sides. Within 24 m
  of the line the near grid draws up to it and the ocean shore data counts as open water, so an outline that does not
  follow the lip leaves no gap or flat band. The sheet takes the property block of the body each camera (shore map,
  foam simulation, weld). The soft depth pass now draws only materials after queue 3000.
- Water: crest falls step 2 - curved sheets. A crest solves one sheet path (free jet at the critical speed, bend into
  the water below, run-out) and `WaterFallSheet.BuildCrestMesh` sweeps it along the lip: each lip point has its own
  frame (outward normal downstream, world up), the columns are equal steps along the lip, with the same mesh data as
  the straight sheet. The normals are averaged along the lip over two throw distances (at least 12 m), so the rows do
  not cross at tight curves. The lip pass of a crest copies the body material without Ocean Waves.
- Water: the ocean near grid no longer draws over land holes outside the shore map (for example a valley inside an
  island below the sea level). An Ocean Waves area builds a coarse coverage mask from its outlines (`WaterCoverageMask`,
  at least 4 m per texel, the bounds of the inner outlines) and the grid reads it where the shore map has no data.
- Water: crest falls (falls from a body, step 1). `LoogaWaterFall` has a Source: River End (as before) or Body Crest
  (an area body and a lip spline along its outline). The network solves each crest: lip points and outward normals,
  the highest body below that the jet reaches, the drop, the flow per metre (the design flow of the component, or the
  inflow of the body over the lip length), the critical depth and speed over a broad crest and the free-jet landing
  line. Issues report a crest without water below or a landing outside it. The overlay draws the lip, the landing and
  drop lines, and the inspector lists the crests. No sheet, plunge or effects come from a crest yet.
- Water: the ocean near grid no longer draws a floating strip past a crest or a cliff edge. In the 12 m dry margin past
  the body outline it now draws only where the ground under the pixel is at most 3 m below the water (scene depth).
  Before, the grid drew the whole margin on the assumption that higher ground hides it, which fails where the ground
  falls away (the perched sea at the crest overflow showed a band of shore foam in the air).
- Water: network effects (water network phase 5). A `LoogaWaterEffects` component next to a `LoogaWaterNetwork` places
  mist and spray from the solved network: a fog bank, jet spray and splash at the plunge of each fall, mist and fine
  spray down the fall face (one station per bay), and low spray over rapids and cascades where the aeration is high.
  The size and the reach scale with the drop height, and the rate with the flow per metre and the station width. One
  particle budget (4096) covers all emitters, and the rapids spray gives way first. The particles have no simulation
  state: `Looga/Water Mist` calculates each particle in its vertex stage from the emitter, the particle number and one
  world clock (new `_WATER_EFFECTS` variant, drawn with `Graphics.RenderPrimitives`). Thus a camera move, a reload or
  a disabled effect cannot restart the particles, and an effect is always at its steady state, with no prewarm time.
  Each camera gets its own distance LOD: the emitters keep fewer particles with more opacity, then fade out. In edit
  mode the Scene view repaints about 20 times a second while an effect is in range. The mist shader reads the water
  level and the water fade per emitter in this variant.
- Water: `LoogaWaterEffectFall` gives a fall that the network does not solve (for example a lake that spills over a
  long crest) to the network effects: lip points and landing points, flow per metre and a rate scale. Its stations sit
  at equal steps along the lip, and the falling water follows a parabola from the lip to the landing. Tall falls get
  a dense rising plume (from a 60 m drop, full at 150 m; on a 180 m drop it rises 6-10 m/s from 35 m above the water). Heights, plunge distances, boxes and fade distances grow with the
  reach (square root of the drop over 22.6 m); particle sizes, lives, vertical speeds and the ground soft fade grow
  with its square root. The fog bank and the jet spray rise with the full reach, and the water fade grows with the
  reach, so a tall fall gets a tall, soft base cloud. The fog bank of a tall fall also reaches farther downstream
  (plunge distance and box length x reach^1.5, downstream speed x reach, count x square root of the reach), and its
  opacity grows with the square root of the reach, up to 0.5. The face mist of a tall fall gets less opacity. The fog box of a
  fall without bays stays within 1.15 station widths.
- Water: generated falls (water network phase 4). A channel end that drops freely into lower water gets a fall sheet
  and a plunge. The sheet follows a free jet from the lip speed and gravity, or the face of a structure, with the
  thickness from the flow per metre, a round bend into the water below and a short run-out (`WaterFallSheet`, the
  generic form of the hand-built spillway sheet). The network generates the sheet of each fall (hidden, not saved) when
  it has a Fall Material. A `LoogaWaterFall` component shapes one fall: a face profile in local space, bays and piers,
  walls, a design flow per metre, materials and mesh settings. Its object scale is the fall unit, so patterns keep
  their size relative to the fall. The lip pass uses the river material (a copy one queue earlier) and the soft depth
  pass a copy of the fall material when no material is set. The plunge is a joint flow of the body below: an impact
  foam line across the fall width, a surface jet of 45 % of the impact speed that decays over three drop heights, and
  white water (turbulence and aeration) that fades over one drop height. The overlay draws the fall paths and plunges.
  The inspector lists the falls and can add a fall component. `Looga/Water Fall` reads its sheet data from UV4 (was
  UV1), because UV1 holds the regime values of the lip pass. A sheet built for UV1 must be built again.
- Water: a body that built before its network was enabled, for example after a script reload, now gets its network
  flow. Before, the network trusted the flow state stored before the reload.
- Water: regime shading (water network phase 3). The network gives each channel point a turbulence and an aeration
  (0 to 1) from the Froude number and the slope, with the regime thresholds, smoothed over the slope window. Jets and
  outlet pulls carry them into areas. The bodies write them to UV1. `Looga/Water` uses them: turbulence acts like fast
  water (detail normals, wave damping, boils) and aeration adds white water that patches break up. New material
  settings: Regime Turbulence (1), Regime Boils Height (0.15 m), Regime White Water (0.4). The shore field writes a
  regime texture (RG8), and the foam simulation uses the aeration as a foam source, so rapids leave trails that drift
  on across joints. Bodies outside a network are not changed.
- Water: one flow field across the joints of a `LoogaWaterNetwork` (phase 2). Rivers in a network flow at the solved
  speed of each point. A river that flows into an area gives it a jet that spreads at 12 degrees and slows as it
  widens. An area that drains into a river pulls its water into the river mouth. The joints are sources and sinks of
  the solved current, so an area with joints always solves its current. Inside an area, a river takes the area flow,
  blended in over a band at the outline, so both surfaces move the same way where they overlap. A body without Inflow
  From takes its foam from the upstream body of the joint nearest its foam window. Hand flow zones still apply on top.
  Falls are not changed. The network keeps the flow inputs of each body over a script reload, so only bodies with
  changed network data build again.
- Water: `LoogaWaterNetwork` (phase 1 of the water network). It connects the water bodies (areas) and channels (rivers)
  under its object: channel ends in or near a body, series joints, confluences and splits. An editor-time flow solve gives
  each channel point the slope, Manning speed for the design bed depth (with the Jarrett steep-stream roughness), discharge,
  Froude number and a regime (still, flowing, fast, rapids, cascade, free fall). A channel end above a body is a free fall
  with a landing point. The validation reports water that rises downstream, free channel ends and discharge conflicts. A
  Scene view debug view (selected network, or Always Show) and an inspector summary show the result. The network does not
  change the water surfaces. `LoogaWaterBody` has Bed Depth and Roughness (Hydraulics) for rivers.
- Water: `Looga/Water Fall` sheets that run out onto water. End Pin (meters along the mesh from the end of the sheet,
  UV1.z): the bulges, clumps and boils fade out, the foam shell fades out before its end, and the part past the plunge
  draws as white water (blended in over the bend), so the jet meets the water as one calm surface. Sea Boil: the calm
  end moves with the boils of the water under it (`LoogaBoils`, now shared in LoogaWaterFoam.hlsl with Looga/Water
  Turbulence). End Sink (optional) sinks the end below its mesh. 0 (the default) keeps the old ends. The foam shell
  now draws only white water: its clear water refracted the scene, and the folds of its lumps showed as glassy
  slivers.
- Water: the turbulence boils of `Looga/Water` are lower in shallow water (full height from 2 m depth), so their troughs
  do not show the ground.
- Water: `Looga/Water Mist`, a particle shader for mist and spray at water. It fades out softly in front of the ground,
  in front of the new soft depth and just above a water level (material settings), so a large particle that reaches
  into the water or behind a jet has no hard edge. `LoogaWaterReflectionFeature` draws the soft depth
  (`_LoogaSoftDepth`): passes with the LightMode tag LoogaSoftDepth, before the transparent objects. `Looga/Water Fall`
  has such a pass (SoftDepth) for its jets; it replaces a jet depth in the camera depth, which cut the mist hard.
- Water: Turbulence for `Looga/Water` (Turbulence Height, Start and Full speeds). Fast water boils: two layers of noise
  bumps that rise and fall in place, as below a waterfall. The mesh moves with them where its vertices are close
  enough, and the light of the surface follows them up to 250 m. 0 (the default) keeps fast water smooth.
- Water: swell for Shore Waves. `LoogaWaterBody` Shore Wave Swell and Swell Direction (degrees clockwise from north
  that the swell moves toward). The arrival field then starts at the upwind sides of the shore texture with straight
  crests along the swell direction instead of at the water edge, so offshore crests are straight and only shallow
  water turns them toward the shore (in a cove the crests bulge toward the beach, at a headland they wrap around it).
  Waves that turned far from the swell direction are lower (30 % at the most sheltered shores), so shores that face
  away from the swell are calmer. Without an upwind water texel the field keeps the waves without swell. The swash at
  the water edge now follows the arrival time of the field at the edge (the water shader samples it toward the
  shore, the terrain wetness on the dry margin), so it starts when a crest reaches that part of the beach.
- Water: foam simulation prewarm. A new or jumped window simulates three foam lifetimes at once in 0.25 s steps
  (at most 240 steps, about 4 ms for 144 steps of a 512 x 512 window), so a plume is complete when the camera arrives;
  60 steps of 0.1 s left a growing plume with a sharp front. The window of a large body moves only when its focus has
  moved a fifth of the window, so a camera that turns does not move it all the time. A moved window warms up the water
  that it uncovers in the same way, while the texels of the previous window keep their foam (`_Kept` in the foam
  compute shader), so the foam in view does not jump (about 1.6 ms once per move). Extra steps for the whole window
  while the camera moved fast made the foam flicker when the view turned.
- Water: far away the simulated foam blurs over a radius that grows with the view distance (0.04 per metre, at most
  12 m, nine samples), so a plume fades out softly into the water around it.
- Water: the foam simulation window of a large body stays around a camera that is lower than 25 % of the window
  above the water, wherever the camera looks (`LoogaWaterBody.FoamFocus`). A higher camera moves the window toward
  the point where its view meets the water, at most 35 % of the window and at most its height above 25 % of the
  window. A window around a high camera left most of the water in view outside it, and the foam stopped at a straight
  window edge. A lead in the view direction for a low camera moved the window when the view turned: it cut off the
  foam trail behind the camera, and the trail popped in again after the window recentred on the turn back.
- Water: `LoogaWaterFlow` Current zones have a Spread angle (0 to 45 degrees, `SetSpread`). The current fans out
  like a jet that enters open water: the flow comes from a point behind the line start, so the band edge leaves the
  start at the spread angle, the band widens by the same angle, and the speed falls so that the momentum stays the same
  (speed squared times width). Foam that the current carries widens into a fan. 0 keeps the old behavior.
- Water: the flow phase of the Looga/Water flow maps varies over the surface with smooth noise (48 m cells,
  `LoogaFoamNoise`). The foam mask texture gave a grainy phase, and with a long cycle in fast water the grain tore the
  textures into streaks along the flow.
- Water: ocean waves from a wave spectrum. `LoogaOceanWaves` (Looga/Water/Ocean Waves) runs a GPU FFT each frame
  (`Resources/LoogaOceanFFT.compute`): a JONSWAP spectrum from wind speed, fetch, direction and spread, three cascades
  of 256 x 256 texels (default tiles 509, 91 and 17 m) on separate wave number bands, choppy displacement, slopes
  and the Jacobian. It sets `_LoogaOceanDisplacement`, `_LoogaOceanDerivatives` and `_LoogaOceanSizes` as globals.
  `Looga/Water` has an Ocean Waves toggle (`_OCEAN_WAVES`): vertices move with the cascades at a mip level that fits
  their spacing, the pixel normal and crest height come from all cascades, and the waves fade to 0 at the water edge
  over the Ocean Full Wave Depth (shore map depth). Without the component the material keeps its Gerstner waves.
- Water: near grid for the ocean waves. A sea mesh with 16 m vertex spacing cannot show waves shorter than about
  24 m. Set Near Grid > Surface on `LoogaOceanWaves` and the component draws a 257 x 257 vertex grid around each
  camera with the surface material. Its cells are 0.5 m at the camera and grow to the Grid Radius (480 m). The
  grid snaps to two cells. Inside the radius the grid draws the water (only where the shore map shows water) and the
  surface mesh clips. The grid reads the flow of the surface from the shore map. Global `_LoogaOceanGrid`.
- Water: ocean waves rebuilt on the techniques of GodotOceanWaves (2Retr0, MIT). Each `LoogaOceanWaves` cascade
  (up to 4, `OceanCascade`) has its own tile size, displacement and normal scale, wind speed and direction, fetch,
  swell, spread, detail, whitecap and foam amount, and holds every wave its tile resolves. The spectrum is TMA
  (JONSWAP with a depth term, Depth 20 m) with Hasselmann spreading and swell, and finite-depth dispersion. Resolution
  256, 512 (default) or 1024 texels (compute keywords `OCEAN_SIZE_512` / `OCEAN_SIZE_1024`). Defaults: tiles 88, 57
  and 16 m, winds 10, 5 and 20 m/s, height scale 0.5, choppiness 1. Slopes are divided by 1 + the horizontal
  compression, so the crests stay sharp. Foam grows where the Jacobian is below the cascade whitecap value and decays
  (GodotOceanWaves rates), with wind drift and streaks. Globals: `_LoogaOceanParams` (cascade count, texels),
  `_LoogaOceanCascades[4]` (1 / tile, displacement scale, normal scale, tile), `_LoogaOceanFoam`, `_LoogaOceanWind`.
- Fixed: the ocean horizontal displacement had the wrong sign. It moved the surface away from the crests, so every
  crest was a round hill and every trough a sharp crease. It now moves the surface toward the crests (Gerstner
  trochoids), and it agrees with the Jacobian.
- Water: `Looga/Water` with Ocean Waves uses only the cascade slopes for its normals (no tiling detail normal maps on
  the ocean path, and no Fresnel normal flattening). The slopes fade with the distance (Ocean Normal Distance Falloff)
  and the whitecaps are smoothstep(0.75 x summed cascade foam) faded by Ocean Foam Distance Falloff. Near the camera
  the clumps and bubbles of the foam detail texture use the whitecap coverage as their threshold, with soft edges,
  partly clear thin foam and bubble brightness, so whitecaps are not flat white shapes. Default cascade foam: 9.5 on
  the 88 m cascade (foam stays about 1.7 s), 0 on the 16 m cascade (its short foam made small white spots). In
  shallow water each cascade fades with its own depth: Ocean Full Wave Depth (default 4 m) is the depth for the first
  cascade, and smaller tiles reach their full height at a proportionally smaller depth. The slopes fade only over
  Ocean Full Ripple Depth (0.4 m) at the water edge, so shallow water keeps its ripples. The crest foam
  rule is off on the ocean path: Ocean Crest Height now sets only the crest light. Lakes and rivers (Gerstner path)
  and the shore foam do not change.
- Water: shore waves and surf. `LoogaWaterBody` Shore Waves builds an arrival field from the shore texture
  (`WaterShoreWaveField`, RGBAFloat `_ShoreWaveMap`: R = time a wave needs to the water edge at sqrt(g depth),
  limited to the deep water speed of the period; GB = gradient toward the shore; A = fade where the waves of two
  shores meet). It uses a fast sweeping eikonal solver, so the crests are lines of equal arrival time: they turn
  toward the shore, wrap around headlands and come closer in shallow water. It is built again with each shore build or
  refresh and after a script reload (about 270 ms for 1023 x 508 texels). `Looga/Water` with Ocean Waves adds two
  Gerstner shore waves (Shore Wave Height, Period, Reach, Steepness) to the vertices, the normal and the crest light.
  The height grows by Green's law and stays at 0.39 of the depth; the surplus breaks, and Surf Foam forms behind the
  crests in segments and fades over Surf Foam Time. At the water edge each crest widens the shore foam (swash).
  `LoogaOceanWaves` sets the global `_LoogaOceanTime`, so the shore waves move with the cascades.
  The main wave comes in groups of five (a set of large waves, then smaller ones), the second shorter wave makes no
  surf, and the surf is strongest at the break line. The surf foam rises over 12 % of the period behind each crest, so
  it has soft edges. Defaults: Period 11 s, Steepness 0.35, Surf Foam Time 1.5 s.
- Water: shore and contact foam reach the water edge. The outline cut (shore texture depth -1 outside the mesh) now
  applies only where the ground past the outline is still under water; over dry ground the foam goes on to the edge,
  so a coarse shore texture no longer cuts it in texel steps. The ocean near grid also draws over 8 m of dry ground
  next to the water: the ground hides it, so the water edge is where the ground meets the surface instead of a line
  of the coarse shore texture. Foam fades over the last 8 cm of water (was 3 cm). On the ocean the shore and contact
  foam moves up and down the shore with the swash of the shore waves instead of drifting with the body flow.
  The shore wave field now also covers 3 dry texels next to the water, and the swash keeps 40 % of its movement where
  the waves of two shores meet. Before, the shore foam stopped moving where the coarse shore texture read as dry.
- Water: swash run-up. `Looga/Water` has Swash Run-Up Height (meters, 0 = off). On the ocean near grid the water at
  the shore rises after each shore wave over 30 % of the period and drains back over the rest, so a thin sheet runs up
  the beach and recedes. Larger waves of a group run up farther. The rise covers the band from the dry margin to 25 m
  from the water edge and fades out before the edge of the near grid. The water surface also moves up the shore and
  back with it, so its ripples and its foam move together. The near grid dry margin is now 12 m (was 8 m).
- Terrain surface: wet sand at the shore. The ground below the swash run-up limit is damp (darker, a little smoother),
  and where the swash has just drained it is darker still for a few seconds. The timing comes from the
  new shared include `Runtime/Water/Shaders/LoogaShoreSwash.hlsl`; the first `LoogaWaterBody` with Ocean Waves,
  Shore Waves and a Swash Run-Up Height sets the globals `_LoogaSwash`, `_LoogaSwashShoreMap` and
  `_LoogaSwashShoreRect` before each camera. It applies after the virtual texture cache. The phase change of the
  shore waves along the shore is now a procedural pattern (was a foam texture sample), so the terrain can match it.
  The terrain uses the same weights as the water run-up (shore wave field fade `_LoogaSwashWaveMap`, near grid fade),
  and the ground under a thin swash front soaks over a soft band, so the wet ground meets the water edge without a
  line. The run-up of the water no longer calms in a current.
- Water: smooth water edge on the ocean. The shore texture reads that shape the water edge (the depth limit of the
  shore waves and the cascades, the shore wave field and the shore foam band) use a cubic B-spline sample
  (`LoogaSampleCubic`), so the edge follows smooth curves instead of the texel polygons. In the swash band the mesh
  rises 6 cm and the fragment cuts the edge at the wave height of the pixel, so the mesh triangles do not show.
- Water: swash tongues. Each wave runs up in rounded tongues of different lengths along the shore (0.55 to 1.25 of
  the run-up, a new pattern for each wave), so the swash front curves on a straight beach. The wet ground uses the
  same tongues.
- Water: foam breaks up the reflection. Where there is foam, the water is rougher (blurred probe and planar
  reflection), the reflection direction tilts with the foam detail, the planar and screen reflections are weaker, and
  dense foam hides the reflection and the sun highlight.
- Water: ragged water edge. In the swash band a noise of foam clumps raises and lowers the water at the pixel by up
  to 3 cm, so the water edge and its contact foam are ragged and curved even where the ground has straight contours.
- Water: fine shore window. A `LoogaWaterBody` with Shore Waves (Area shape) reads the ground every 0.5 m in a 128 m
  window around the camera (`WaterNearShoreField`, `_ShoreFineMap`, `_ShoreFineRect`) and builds it again when the
  camera moves 24 m or the shore changes. The water depth, the shore waves and the shore foam band read it near the
  camera and blend to the shore texture at its border, so they follow the ground instead of the large shore
  texels. `WaterShoreField` keeps a mask of the collider texels for it.
- Fixed: on the ocean the shore foam band slid away from the water edge on the backwash, because it was measured on the
  moving surface. It is now measured where the pixel is, and the foam fades over the last 2.5 cm of water (was 8 cm),
  so it reaches the water edge on a gentle beach.
- Water: the fine shore window builds in a Burst job (`LoogaSoft.Terrain.Water` references `Unity.Burst`). The main
  thread only reads the terrain heights of the window; the next camera after the job uploads the texture.
- Water: receding swash edge. On the ocean the contact foam keeps its full width when the swash drains back, so the
  receding edge has a foam rim like the run-up front. Just-drained sand is a smooth wet film: glossy, with a normal mostly
  straight up, so neither glints nor the folds of the terrain shape show in it. Past the highest point of the run-up a steady
  sheen fades out far above it (it does not change with the waves), and the ground under the receding water is fully
  wet up to the water edge, so the wet ground ends nowhere near the water edge in a line.
- Water: `LoogaOceanWaves` Preview > Freeze stops the waves, the shore waves, the swash and the wet sand at Frozen
  Time, so views and changes can be compared at the same moment.
- Fixed: a thin straight line in the wet sand on the backwash, where the full wetness under the water ended just below
  the water edge and the drained sheen started at it. Each wave also keeps one group height for its whole cycle.
- Terrain renderer: shore detail. `TerrainRenderer.SetShoreDetail(waterLevel, errorScale)` makes patches whose height
  range reaches 0.5 m below to 1 m above the water level use the error pixels times the scale (camera parameter
  `_CameraSettings.zw`). The `LoogaWaterBody` with the swash sets it (scale 0.1), so the water edge on a flat beach no
  longer jumps when a terrain patch changes its detail with the camera distance.
- Water: impact foam below waterfalls. A fast `LoogaWaterFlow` inlet zone (from 0.3 m/s, full at 1 m/s) is where
  falling water lands: the foam simulation of the body makes dense white water at once on a line across the first
  point of the inlet line (zone width each side, full to 1.5 m, none past 5 m), in churning patches that surge
  (`WaterFoamSimulation.SetImpacts`). The current carries the boil away as patches that thin out downstream. Near the
  impact (up to 15 m) the spreading inflow does not thin the foam. The fast water foam spots of the simulation move
  with the flow (2 s flow map cycle), so their trails no longer stay in place. The spots come from value noise that
  does not repeat (`LoogaFoamNoise`, integer hash), so their trails do not form rows. The divergence of the flow (foam that
  gathers or thins) uses samples one shore map texel apart: the flow is bilinear between texels, so samples one foam
  texel apart gave a divergence that jumped at each texel edge, and the foam showed bands with straight hard sides.
  The foam simulation steps 20 times per second. The water shader now moves the simulated foam on with the flow over
  the time since the last step (`_FoamSimAge`, set before each camera renders), so fast foam no longer moves in jumps. On the ocean, the simulated foam
  replaces the fast water foam of the shader inside the simulation window. In a current faster than 0.4 m/s (full at
  1.2 m/s) the foam detail is two small flow-mapped layers (0.45 and 0.29 of the foam detail size, each with a cycle
  that fits its size) with flat clumps (contrast 0.3), so the foam shapes come from the simulation and the 5 m tile of
  the detail texture no longer shows. The detail is hex tiled (Mikkelsen 2022, `HexFoamDetail`): each vertex of a
  triangle grid (about 1.2 m cells) shows the texture with its own random turn and offset, blended with sharpened
  weights, so the same clump shapes do not come back in rows. Two bent layers still showed a repeat, and an even mix
  of two layers gave worm shapes. The current detail and the foam under the surface use one flow cycle of 1.5 s: a cycle that
  became shorter in fast water restarted the detail up to 16 times per second, and the foam jittered. The foam under the surface moves with the flow too. Slow water keeps the swash
  foam.
- Water: `LoogaOceanWaves` has a GPU profiler marker `Looga Ocean.Simulate` around the spectrum, FFT, field and foam
  dispatches.
- Water: one foam look for all `Looga/Water` foam (`FoamFromDetail`): whitecaps, the shore band, the contact line
  and fast water. The clumps (R) and the bubbles (G) of the foam detail texture use the coverage as their threshold,
  with soft edges, partly clear thin foam and bubble brightness. Clumps grow faster than the coverage, so thin foam is
  bubbles and small clumps. The lace channel (B) is no longer read: its cell walls showed as a regular pattern
  (`FoamErosion` removed). With Ocean Waves, the wave height at the pixel widens the shore band and thickens the
  contact line under a crest and thins them under a trough.
- Water: solved currents. `LoogaWaterBody` (Area shape) has Solved Currents (Flow header). The body solves a
  potential flow over the shore texture (`WaterCurrentField`, cells of 4 shore texels, conjugate gradient, no flow
  through the shore): water comes in at Inlet zones and leaves at Outlet zones of each connected water area, follows
  the shore, fans out from the inlets and gathers at the outlets. Speed is the zone speed over a 1.5 m discharge depth,
  limited to 3 m/s. The field replaces the drift and is solved again with each shore build or refresh and when the
  flow zones change (about 0.5 s on the sea). Outlet zones still pull on top of the field; other zones still apply.
  `LoogaWaterFlow` has a new shape Inlet: water comes from the spline (without solved currents the flow goes away from
  the nearest point of the spline). On a material with Ocean Waves the foam simulation has no shore foam source, so it
  makes only current foam (Fast Foam) and does not drag the shore foam away from the shore.
- Fixed: after a script reload `LoogaOceanWaves` kept its compute shader reference but not newly added kernel
  fields. `OnDisable` now clears the reference, so the kernels load again.
- Rock face ground blend is now a dusting. The rock no longer takes the full terrain texture, normal and light at its
  base. A thin film of the ground colour settles in the low parts of the rock, broken by noise, and the rock normal
  stays. Only terrain layers with the new surface layer value Object Dust (0 to 1, layer property row 8) give dust:
  use 1 on sand and dry dirt, 0 on grass and rock. Without a Looga terrain surface (MicroSplat layout) there is no
  dust. New material values: Ground Dust Strength (default 0.6) and Ground Dust Reach (default 0.5, the share of the
  blend height that gets dust, so the band is half as high as before). Ground Terrain Light is removed.
  `LoogaSampleTerrainGround` has an overload with an `objectDust` output; the other overloads do not change.
- Stamp falloff: Spline Area has an Edge option. The weight then comes from the distance to the spline line on both
  sides: 0 at the boost distance, full at the boost minus the range. Use it with a Smooth Stamp to soften the ground
  along the edge of an area, for example a waterline, without a second spline. The Smooth Stamp box limits the length
  of edge that changes. Height, texture and placement stamps all read it. The shader uniform `_AreaOutside` is now
  `_AreaSide` (0 inside, 1 outside, 2 edge), and `AreaWeight` in `StampFalloff.hlsl` holds the shared formula.
  `SetSplineArea` has an optional `edge` argument.
- Fixed: a Snapshot Limit after a Smooth Stamp or Talus Stamp had no effect when its snapshot came before that stamp.
  A neighbourhood stamp splits the order into segments, and the evaluator released the snapshot copies at the end of
  each segment. The evaluator now keeps a copy for each tile where a later limit reads it, until the evaluation ends.
- Terrain surface: bare far cover. A surface layer has a Bare Far Cover value (0 to 1, layer cover row w). Where the
  ground cover maps have no plants, a layer with bare far cover changes to the bare canopy colour of the Terrain
  Surface Globals over the canopy distances, with the far canopy occlusion, sun shade and detail of plant cover. The
  ground near the camera does not change, because no plants draw there. The bare cover fades out where the coverage
  map has plants, so it meets the plant cover without a seam. Cached pages store it in the cover page z channel.
- Added barrier runs (`Runtime/Barriers`, assembly `LoogaSoft.Terrain.Barriers`; editor `LoogaSoft.Terrain.Barriers.Editor`).
  A `LoogaBarrierSet` asset (Create > LoogaSoft > Terrain > Barrier Set) lists weighted piece prefabs, an optional post
  for each joint, scale, joint spacing, pivot offsets, slope mode (Step keeps pieces level at the lower end, Follow
  tilts them), stretch to fit, yaw jitter, and colliders (none, box, or mesh). A `LoogaBarrierRun` on a
  SplineContainer places the set along each spline (each spline is one section, so the space between splines is an
  opening) on the terrain. Pieces keep their prefab link and are saved with the scene. A run builds again after a
  spline edit, an inspector edit of the run or its set, or a stamp-world terrain change under the run; loading a scene
  does not rebuild. Piece models use +Z as the run direction.
- Barrier runs: `LoogaBarrierInstancing` draws the generated pieces of all runs in the scene through Looga Instancing
  (`SceneInstanceProvider`, colliders stay). The barriers assembly now references `LoogaSoft.Instancing.Runtime`. Runs
  raise the static `Rebuilding` and `Rebuilt` events and expose `Generated`. While a run builds again, all pieces draw
  natively; the component registers them again after the edits stop.
- Barrier runs: each gate has a kind (Closed, Open, or Opening) and a width. The kind replaces the Open toggle
  (existing values carry over). An Opening is a gap without a gate model, with set posts at both sides, and its width
  is measured along the spline, so it can go around corners. A gate model stays straight. The run has a Ground
  setting: Terrain (default) or Spline, which keeps the pieces at the spline height, for example on rock above the
  terrain.
- Barrier runs: Joints At Knots (run) places a joint and a post at every knot and fits the pieces of each curve
  separately, so that no piece spans a sharp corner. Gate Has Posts (set) stops the set posts at the gate edges for a
  gate model with its own posts. `LoogaBarrierGate` finds the side of each leaf from the meshes below its hinge, so
  leaves no longer need to extend along the hinge +Z axis.
- Barrier runs: gates. A run has a list of gates (spline index, position 0-1 along the spline, open). The set defines
  the gate model, the gate width (distance between the gate posts at scale 1), the gate offset and the leaf open
  angle. A gate replaces the pieces that it covers. The pieces on each side stop at a post at the gate edge, and no
  post stands in the gate. The run fits the pieces between gates separately. The gate is straight on curves and level
  at the lower post on slopes (each hinged leaf steps up to the lower ground of its own half). A set without a gate
  model makes an opening. The new `LoogaBarrierGate` component lists the leaf hinges of a gate model. An open gate
  turns each leaf to the same side. In Box collider mode, each leaf gets its own box. Gates that do not fit or overlap
  are skipped with a warning. The Scene view has a handle for each gate that moves it along its spline.
- Rock blocks (block formations and block cliff walls): flat tops without a lip. The wall rules (structure detail and
  flutes) moved the rounded top edge along its normal, which points up near the top, so a positive offset lifted the
  edge above the top and it showed as a raised rim. On the rounded edge they now move it only outward or inward with
  the wall. The top of a lower tier (a ledge) also starts at the height of its wavy joint edge and fades the wave
  toward the centre, so ledges have no lip or dip at the edge. Other block shapes do not change.
- Stamp world: a removed or disabled detail stamp clears its detail layer also after a script reload or a scene load.
  The world now saves the detail layers that its detail stamps painted (`_paintedDetailLayers`, hidden). A full
  placement write clears each listed layer that no stamp paints any more. Before, the list of painted layers was only
  in memory, so the first full write after a reload left the old details of a removed stamp in the terrains.
  Hand-painted layers that no stamp painted do not change.
- Water: planar reflection for one flat water body, for example a sea (`LoogaPlanarReflection`, on an Area
  `LoogaWaterBody`). Before a game or scene view camera renders, a hidden Reflection camera renders the scene mirrored
  at the surface with an oblique near plane, into a mipmapped texture at a lower resolution and LOD bias. `Looga/Water`
  shows that image on all water at the height of the body (distorted by the normals, rough water reads a lower mip)
  and skips the screen-space trace there. Water surfaces do not render in the reflection. The reflection runs only
  while the water covers enough of the view (ray checks against the occluder layers, every 0.25 s) and the camera is
  low enough above it, and it fades in and out. The Looga grass, ambient occlusion, post-processing, lighting and
  shadow passes skip Reflection cameras. The reflection camera gets an `InstanceCameraFilter` (Looga Graphics Pro):
  no Decorative populations, no instances under 16 pixels, optional instance distance limit. The water assembly now
  references `LoogaSoft.Instancing.Runtime`. GPU sampler "Looga Water.Planar Reflection".
- Cave system: the rock cover reads the vertices of a rock chunk from its finest level that keeps a CPU copy. Rock
  meshes outside the boxes of cave entrances keep only their GPU copy at level 0, so the cover logged "Not allowed to
  access vertices" for nearby cliff walls and rock formations and skipped them. The collider level (level 1) now stands
  in. A chunk with no readable level is skipped without an error.
- Water: screen-space reflections. The new `LoogaWaterReflectionFeature` (URP renderer feature) builds a closest-depth
  pyramid after the opaque objects. `Looga/Water` traces one reflection ray for each pixel through the pyramid
  (hierarchical Z) and reflects the opaque texture. Where the ray leaves the screen, passes behind an object or finds
  nothing, the water uses the reflection probes and the sky, as before. The reflection fades at the screen edges.
  No ray is traced where the reflection is almost invisible (low Fresnel or dense foam). `Screen-Space Reflection` on
  the material sets the share (default 1). Without the feature on the renderer, the water reflects only the probes.
  Feature defaults: 32 trace steps, 1.5 m hit thickness, 0.08 edge fade, 2000 m rays. `MeasureTransparents`
  (diagnostics) times all transparent objects of game cameras in the GPU sampler "Looga Water.Transparents". The water
  assembly now references the URP runtime assemblies.
- Water shore map: ground just outside a water body more than 0.25 m below the nearby surface level is open water, not
  a shore. Where another river continues a river end to end, or a river mouth opens into a lake, the outline gets no
  shore foam. Banks at the water line keep their foam. The shore map texels just outside a body take the flow of the
  nearest water, so the foam simulation no longer piles foam up where a river ends at its outline, and foam that flows
  in past the outline starts at the source value instead of none.
- Water body: `ShoreMapTexelSize` (texel of the built shore map) and `SetShoreTexelSize`, so tools can give joined bodies
  the same shore texel and the shore foam band does not change at the joint.
- Water body: `Inflow From` (`SetInflowFrom`). Open water just outside the body holds the simulated foam of that body,
  so foam trails go on across an end-to-end joint instead of starting again.
- Water foam simulation: a camera window of a large body does not fade on a side that lies at the body edge, so the
  simulated foam reaches the end of a river where the next river continues it.
- Water body (River): the first and last ribbon rows are perpendicular to the exact spline direction at the ends, not
  to the chord of the last sample step. Two rivers that meet end to end with the same knot direction now share one end
  line, so no dry wedge between them reads as a shore in the water fields.
- Clear stamp: weight noise (two noises, like placement stamps) and a texture filter. They shape the clear weight, so
  a partial detail clear with the weight noise of an object stamp thins the grass only where that stamp places.
  Defaults are off, so existing clears do not change.
- Rock face (Asset UV Textures, terrain ground on): optional ground fringe (`Ground Fringe Strength`, tint, width as a
  mask mip). The rock around the asset ground takes a moss and soil tint, so the grey rock does not meet the green
  ground at a hard color step. A coarse mip of mask red spreads the painted ground past its edge (asset masks are
  almost binary), ground on up faces spreads further down the normal, and a world noise breaks the band. Off by default.
- Rock face (Asset UV Textures): optional tiled rock layer, as the detail maps of a lit material. Albedo and mask (G
  occlusion, A smoothness) multiply into the asset textures (`Tiled Rock Albedo Strength`, mid grey of the asset albedo
  = no change); the normal adds fine grain (`Tiled Rock Normal Strength`, whiteout blend). `Tiled Rock UV` scales and
  offsets the asset UV; alpha of `Tiled Rock Area` at the asset UV limits the layer (a detail mask). Vendor rocks lost
  their detail normal or their tiled rock color without it and rendered smoother than the source. Off by default.
- Procedural grass: on mesh ground, the blade root height blends the four nearest ground map texels when they are
  within 1.5 m of each other. A single texel gave steps on steep rims, so the blades showed in rows.
  `LoogaMeshGroundCover` `Grass Max Slope` (default 80 deg) sets how steep a masked face can carry grass. The grass
  ground capture drops faces steeper than 45 deg, so grass stopped short of rounded green rims; the cover mesh now
  skips faces past the limit and gives the kept faces an up normal.
- Procedural grass: `GrassGroundMesh` can carry a ground mask. The grass ground capture then reads red of the mask at
  the first UV for each pixel, so the grass follows the painted ground of an asset and not its coarse vertices (cliff
  triangles are metres wide, and a weight at the corners ended the grass short of the green edge). The ground map
  stores a grass density (offset texture now RGBA half): it rises from mask 0.25 to full at 0.7, and the grass thins
  in tufts over that soft edge as at a lower coverage. Ground without a mask keeps density 1.
  `LoogaMeshGroundCover` Asset Mask builds one grass submesh for each mask map and does not need readable masks. Its
  up-face ground (`Asset Ground On Up Faces`) uses the smooth vertex normals as the shader does; the face normals
  left whole triangles of the cave top without grass.
- Procedural grass: the grass ground map holds only the mesh ground within the blade distance of the grass cameras.
  It held the union of all registered ground in the world, so ground far apart (quarry walls and a test area 2.4 km
  away) made its texels 1 m instead of 0.25 m. A 1 m texel has one height for a bumpy top, so the blades sank into
  the rock and the tops showed rows of tufts. The map draws again when the set of near objects changes.
  `LoogaMeshGroundCover` waits for the terrain surface after a script reload and builds again when the surface
  replaced its layer arrays.
- Geology: Looga/Rock Face `Asset UV Textures` (`_ROCK_UV`): draws a rock asset with its own UV textures (albedo,
  normal with a derivative tangent frame, mask R ground / G AO / B height / A smoothness). Red of the mask marks the
  ground, for example the grass top of a vendor cliff: there the terrain ground path draws the terrain layers, world
  colour and ground cover of a point beside the asset (`_RockGroundCenter`). `Asset Ground On Up Faces` adds ground
  on faces that look up. `LoogaMeshGroundCover` mode `Asset Mask` sets the terrain values on every renderer and
  registers a grass ground mesh weighted by the mask, so the grass grows on the masked top with the grass of one land
  point 3 m past the bounds (one point keeps the interpolated offsets exact). `_RockGroundCenter` and
  `_GroundUpStart` are also declared in project shaders that include LoogaRockFaceInput.hlsl.
- Geology: `LoogaMeshGroundCover` (Looga Terrain/Mesh Ground Cover). Covers the up-facing parts (full up to 30 deg,
  ends at 50 deg along a ragged edge) and the foot (0.6 m over the terrain) of any rock or cliff prefab with the Looga
  terrain surface of the ground below, so its top and foot match the terrain around it. The procedural grass grows
  on the covered top (`IGrassGround`). The cover is a scene-only mesh over the finest LOD and builds again when the
  object moves.
- Procedural grass wind: gust sheen. A gust press raises the blade albedo by up to 20 % and the smoothness by 0.1, so
  gusts read as lighter patches that travel over the field. The bend alone barely changed the far grass, whose
  normals turn up with distance. `GrassWind` returns the press; the blade vertex carries it to the fragment.
- Stamps: object stamp Field Source `Canopy and Footprint` (trees: crowns give Canopy, bases give Object Foot
  Distance). The footprint radius reads the vertices of the lowest quarter of the mesh, at most 2 m above its bottom.
- Stamps: placement fields. Field filters can read `Canopy` (crown cover 0-1) and `Object Foot Distance` (metres to
  the nearest base, 12 m cap) from the placements of object stamps whose new `Field Source` setting is Canopy or
  Footprint (`Field Radius Scale` multiplies the radius measured from the prefab). `PlacementFieldMaps` builds a 1 m map
  for each tile on the CPU (Burst, about 3 ms). A placement run that has both sources and readers places the sources
  first on all its terrains, then the other stamps. An update of a source grows its region by 32 m and places the
  readers again. Texture stamps read the fields from the stored placements; the texture layers that placement filters
  read leave them out, and a placement write that changes sources updates their textures again. A field source ignores
  placement field filters. `LoogaStampWorld.MeasurePlacementFields` gives the map of a terrain for debug views. A filter
  whose map is missing is off; shape and flow fields no longer start the 48 m height measurement for placement fields.
- Vegetation provider: hidden native foliage without an owner (no Looga provider and no vendor component) no longer
  blocks the provider with "Native vegetation is already disabled by another owner." The provider takes it, keeps it
  hidden while the vegetation loads, and restores it as visible. An interrupted editor handover (script reload or
  scene save) left all providers waiting for good, with an endless "loading cells" task. One warning per script domain.
- Procedural grass wind: each blade has its own gust stiffness (0.7 to 1.3) and a gust delay of up to 0.8 s, so a gust
  edge breaks up over a few metres and does not move over the grass as one line. Pressed blades get a fast, small
  flutter (4.3 times the sway rate), and the slow sway in a gust is damped by 40 % (was 60 %).
- Procedural grass: grass under the full coverage grows in tufts and not as single random blades. The coverage share
  removes whole clumps and the clump rims; the lattice share of the types still thins evenly. New type setting
  `Sparse Clumping` (default 0.5): 1 = fewer clumps at full size, 0 = all clumps, each one smaller. The mean blade
  count is unchanged, and a field at the full coverage is unchanged.
- Procedural grass wind: a gust presses the blades further downwind (`GrassGustBend`, 1.2 times the wind response at a
  full gust) and damps their idle sway. Between gusts the blades keep the steady lean and the idle sway. Uses the new
  gust patches of the Looga wind field.
- Spline paths and areas: the last curve of an open spline is now a point at its last knot. Unity GetCurve bends it
  by the out handle of the last knot, so a road end with a 32 m handle got road paint, clears and ground shaping up to
  13 m past the road (a grey pavement patch beside a junction). Other open splines ended up to 1.3 m sooner. This
  ends the MicroVerse parity of that curve.
- `Looga/Rock Face`: DOTS instancing variants (`multi_compile _ DOTS_INSTANCING_ON` in all four passes), so Looga
  instancing and object stamps can draw rock prefabs with this shader.
- Crack lines: the generator writes each vertex's distance to the nearest crack of the Cracks pass (divided by the age share)
  to UV3.y (`CrackLines` kernel, `RockField.CrackLineDistances`). `LoogaRockFace` draws a thin dark line there with new
  properties `_CrackLineWidth` (half width in meters, 0 = off) and `_CrackLineDarken`; the line strength follows its pixel
  coverage, so far lines fade. A Cracks pass with 0 depth and 0 weathering only draws lines.
  The line is shaded as a groove: its width changes along it (0.5-1.5x), its rims are rough at grain scale, its walls turn
  the normal toward the middle (surface gradient from screen derivatives), its core is dark and its lips are a little pale.
- Rock generator: pass kind `Cracks` (`RockCrackSettings`, `RockCrackTree`) splits the rock in order, after successive domain
  division (Bohn et al. 2005): the first crack splits the whole rock across its longest size (principal axis of points
  inside the base blocks), and each part splits again. Younger cracks end at older cracks in T junctions. Crack faces are
  self-affine (5 octaves, 2^-0.8 height per octave). Width, depth and weathering fall by Age Falloff per generation.
- Fracture sets: `RockFractureSet.Through` starts each fracture inside the rock and cuts its full depth; the generator then
  connects set fractures across the whole box, so each crack runs on to an older crack or out of the rock.
- Fracture pass: `RockFractureSettings.EdgeWeathering` rounds the crack lips with a shallow round channel up to four times
  the crack width. A crack fades out where its plane runs nearly along the surface, where it carved broad hollows.
- Rock generator: `RockRecipe.FractureSets` adds random fracture families (`RockFractureNetwork.Add`, then `Connect` for
  T junctions) to the authored fractures at each build, on a box of 85 % of the recipe size. A Fractures pass applies them;
  set its Step Limit to 0, or the limit also caps the crack depth. The fracture volume uses the same list.
- Fracture pass: a crack with depth keeps at least 1.5 cells of width up to its ends, and the ends fade in depth. Narrower
  cracks broke into rows of dots in the mesh.
- Rock generator noise pass: `PatchSize` and `PatchContrast` make smooth and rough patches. A broad noise scales the small
  noise layers (all but the first), so the large form stays. At contrast 1, smooth patches keep 15 % and rough patches get
  150 % of the small layers. 0 size = off (old result). The default noise pass of a new generator uses 4 m and 0.8.
- Rock generator: `RockRecipe.NeckWidth` (meters, default 0.3, 0 = off) removes a part that hangs on the rock through a neck
  narrower than the width, with its neck (`RockDualContour.RemoveNeckedParts`). Thin edges and ridges stay. The build
  report counts the removed parts.

- Cliff wall kit style: the builder now fills a map of the terrain face until the coverage target, in place of fixed
  rows. `CliffWallKitPiece` profiles each prefab: it finds the cut and stretched surfaces, the size and the views that
  can show. The builder buries the cut side. A flat layer of large, medium and small pieces fills the map in random
  order, and a relief layer adds tilted pieces on top. The long axes follow a rock bedding that changes along the wall, with buttresses, and
  the wall stands out and back along its length. The wall also covers the rounded brow above the steep face. New kit
  settings: `LargeSize`, `MediumSize`, `SmallSize`, `ScaleLimits`, `TargetCoverage`, `MaximumPieces`, `ShoulderAngle`,
  `BackDepth`, `ProudRange`, `BeddingDip`, `ButtressShare`, `ReliefDensity`, `FlipChance`. Removed: `Scale`, `FillerDensity`,
  `FillerScale`, `Overlap`, `RowOverlap`, `Embed`.
- Spline areas: a band area has an outer and an inner outline, and the falloff fades between them. The fade can so
  change width along the edges, for example a shore shelf that tapers to nothing.
- Water flow zones: the new Outlet shape pulls the water toward the nearest point of a spline, for example a waterfall
  crest. The flow reaches the zone speed over the falloff distance.
- Water: `_WaveCalmStart` and `_WaveCalmSpeed` set the flow speeds where the Gerstner waves fade. `_FastFoamStart` sets
  the speed where the fast water foam starts, in the shader and in the foam simulation. The defaults keep the old result.
- Water bodies: an area can have a detail area, for a body that runs far past its shores. Fine vertices and the shore
  texture stay in that rectangle. Outside it the vertices get coarser with distance, and the shader uses depth foam.
  The first spline is then the far outline, and later splines cut holes, for example an island.
- Water distance fields: the cover test grid gets larger cells on very large surfaces. An 8 m grid over triangles
  tens of kilometres wide stopped the editor.
- Generated mesh cache (editor): rock faces, rock formations, cliff walls and cave systems store their meshes in
  `Library/LoogaGeneratedMeshes`. An editor start loads them when the code, the settings, the spline and the terrain
  heights and holes near the generator are unchanged. `Tools/Looga/Clear Generated Mesh Cache` removes the files.
  Water bodies, rock kit faces and the kit style of the cliff wall still build.
- Script reload: rock formations, cliff walls, cave systems with their entrances and water bodies keep their generated
  meshes over an editor script reload, as rock faces do (`RockFaceOutput.StoreForReload`, `TakeAfterReload`,
  `GeneratedReloadHandover.KeepsOutputOnLoad`). The kit style of the cliff wall still builds. After a reload a changed
  rock mesh builds the cave, and a flow change builds the water body, because their build data stays in the last domain.
  A change of the Looga Terrain code still builds everything.
- Editor load: a height modifier that waits for its inputs at the first capture after a load, for example for a live
  texture source, no longer writes the heights when it turns active. The saved heights hold its result.
- Cave system: the enable of a passage or an entrance in the same load no longer queues a second build, and the first
  editor update no longer reads the passage transforms as moved.
- Rock formation: a formation built before its ground source was ready builds again when the source is ready. Before,
  it stood on the terrain until a height update happened to build it.
- Detail placement: instances now land with a tent distribution around their detail texel centre, up to one texel to
  each side, on all backends (`DetailPosition` in `DetailPlacement.hlsl`, `GeneratedVegetationPageSource.Position`). The
  instance density is then the bilinear filter of the detail map. Before, each instance landed uniformly inside its
  texel, so patches with a sharp edge showed the 2 m texel grid. Instances can land up to one texel past a cleared
  area. Page culling reach grew by one texel. Positions past the terrain edge mirror back. All placements move.
- Terrain surface fields (new, `Fields` header, all strengths 0 by default): erosion variation that splat layers cannot
  express: wet ground and channel floors darker and smoother, convex rims lighter with less far ground cover, concave
  feet with a deposit colour. Rim and foot end above `Max Slope`. A stamp world keeps one RGBA8 map per terrain (512 x 512, mips, about 1.4 MB) while a
  surface uses the fields: R wetness, G and B relative height 4 m and 20 m, A log10 flow area. Filled pits and level
  pads have no flow and are wet only in a hollow. The map is measured with neighbour tiles, so tile seams match. It
  updates after height changes, one terrain per editor tick, within 128 m of the change, and only that region of the
  cached pages refills. New registry `LoogaTerrainSurfaceFields` (runtime assembly); other producers can publish maps
  with `Set`. Cached pages and direct pixels run the same function. Shader values `_LoogaSurfaceFields`,
  `_LoogaFieldWetness`, `_LoogaFieldChannel`, `_LoogaFieldRim`, `_LoogaFieldFoot`, `_LoogaFieldFootRange`.
- Terrain surface distance fades (new, `Distance Fades` header, default unchanged): `Fade Distance Mode` Screen
  Footprint makes all distance fades (hybrid blend, parallax, detail and distance noise, resample, world colour,
  ground cover, far detail) read the size of a screen pixel on the ground, converted to the distance where a reference
  camera (vertical field of view and screen height, default 60 degrees and 1440 pixels) shows the same size. A cached
  surface page has a texel size but no camera, so this is the base for a virtual-texture path of the surface. At the
  reference camera the result matches Camera Distance within capture noise (KuberaWorld, 6 views at 1440p).
  Shader value `_LoogaSurfaceFade` (all three surface targets).
- Terrain surface virtual texture cache (new, off until a `TerrainVirtualTexture` is added): the patch target
  (`Looga/Terrain/Surface (Patches)`) has a `LoogaVTBake` pass and is its own page producer, so a
  `TerrainVirtualTexture` with only a settings asset caches the surface of a `LoogaTerrainSurfaceBinding` tile.
  - Page: the surface without the view (no parallax), with fades at the page texel size (Screen Footprint mode is
    required). Albedo and smoothness and the world normal and occlusion are stored before the ground cover. The third
    array stores the ground cover layer strength, the soil luminance ratio and one canopy detail luminance offset (the
    two direct factors without their small strength-squared term).
  - Pixel: the G-buffer pass reads the page where a pixel covers at least one virtual texel and applies the ground
    cover there with the same functions as the direct surface (view closure, sheen, canopy sun visibility, relief
    scale). The cached metallic is 0. Debug views and pixels past the optional limits shade directly.
  - Sampling: anisotropic page reads (up to 4 probes, gradients clamped to the 4-texel border) with the mip from the
    larger footprint divided by `Max Anisotropy` (new setting, 1 to 4, default 1 = the old rule). New settings
    `Cached Slope Limit` (degrees, default 90 = none) and `Cached Footprint Ratio` (default 0 = none) send steep or
    very oblique pixels to direct shading. Shader value `_LoogaVTSampling`.
  - `TerrainVirtualTexture.ShadeDirectly` (static, shader global `_LoogaVTShadeDirectly`) shades cached surfaces
    directly while the pages stay resident, for comparisons.
  - Memory: with compression on and the low-precision material array, the third array is BC3 too (linear data mode
    in the encoder). 3 bytes per texel instead of 6.
  - Residency mip bias (new setting, default off): when the views request more than 95 % of the pool, all cache reads
    and requests move 0.25 mip coarser per feedback, up to 3 mips. Below 60 % the bias steps back. One global bias
    (shader global `_LoogaVTResidencyBias`, the largest of all caches) because the feedback shader is shared. The
    inspector shows it (`VirtualTextureStatistics.ResidencyMipBias`).
  - Missing pages: new setting `Max Fallback Mips` (default -1 = any coarser page, the old rule). A pixel whose page
    is missing reads a coarser resident page at most this many mips coarser, else it shades directly. KuberaWorld
    uses 1: while approaching the ground, blurred areas fell from 13-26 % of the textured screen to 0-0.1 %.
  - Fill order: the pages that pixels read fill first, the page with the most feedback pixels first. Before, all
    pending pages filled coarse first, so near pages waited behind far ones and refined late.
  - Pool size: new setting `Scale Pool With Resolution` (default off). `Resident Pages` is then the page count at
    2560 x 1440, and the shared pool follows the pixels of the largest game camera (the largest Scene view when no
    game camera renders), in steps of 64 pages up to the 2048-slice array limit. The pool grows at once and shrinks
    after 10 s. A size change rebuilds the cache. `TerrainVirtualTexture.PoolPixels` and the inspector show it.
  - Camera priority: while a game camera renders, Scene view requests only take the room left below 90 % of the pool
    and fill after the game camera's pages, so the Scene view cannot blur the game view or raise its bias.
  - Fixes: the cache keeps the page requests of each camera and merges them. Before, the readback of one camera
    replaced the requests of the others, so with the Scene and Game views open one view fell back to coarse pages.
    `TerrainVirtualTexture` registers against the drawn surface (`ActiveSurfaceMaterial`), so a surface override
    re-registers. The compressed cache with a low-precision material array failed its page copy (staging format).
  - Surface library: `LoogaSurfaceInput` carries the screen derivatives of the position and the control UV. The
    passes read them before any branch. The control-map mip ignores the small derivative of the control UV noise, and
    the far strata band edge width uses the height derivatives.
  - KuberaWorld (1440p, RTX 5080 editor, 32768 virtual texels per tile, 2048 pages, BC3, anisotropy 4): cached vs
    direct within capture noise in 7 views (mean difference at most 0.06 levels above the direct/direct noise). Terrain
    GPU saving 0.39 to 0.60 ms at ground level and 1.2 to 1.8 ms in elevated views. A fill of 16 pages costs about
    1.5 ms, 8 pages about 0.8 ms (median). Views need 1100 to 2300 resident pages without a bias. With 1024 pages,
    the BC3 material array and the residency bias the cache uses 54 MB: bias 0.75 to 1.5 mips, saving 0.23 to 0.59 ms
    at ground level and 1.3 to 2.1 ms elevated, parity within noise except slightly softer pages close to the cache
    edge in the elevated review view (mean difference 0.34 levels above noise).
  - Live invalidation: a changed terrain area also invalidates the pages within the producer's dependency radius
    (new shader value `_LoogaVTDependencyRadius`, set by `LoogaTerrainSurface.ApplyTo` from the far curvature step and
    the control noise offset; 7.4 m in KuberaWorld). Before, pages next to a height edit kept the old curvature.
    New `TerrainVirtualTexture.InvalidateWorldArea(Rect?)` invalidates the pages of all tiles in a world XZ area (null:
    all pages). A grass edit invalidates its changed area after the ground cover composition (plus 8 coverage
    texels). A new world colour map or ground cover map binding in `LoogaTerrainSurfaceGlobals` invalidates all pages.
  - Fix: a page fill after a height edit could read the replaced heightmap texture (`SetHeights` creates a new one),
    so the page kept a wrong cliff. A tile fills only when its producer binding matches the renderer's surface version.
  - Fix: the page feedback kept the farthest surface along each pixel ray. Unity already reverses the depth test and
    the depth clear value on reversed-Z platforms, and the cache reversed them again. Pixels behind a near hill
    requested the pages of the terrain behind it, so their own pages were missing and they shaded directly or read a
    coarser page. This also caused part of the blur reported at 1024 pages.
  - Feedback: the mip of a request uses the footprint of one full-resolution pixel on the terrain plane (camera
    vectors `_LoogaVTCameraPosition`, `_LoogaVTCameraRight`, `_LoogaVTCameraUp`, `_LoogaVTCameraForward`), the finer of
    that and the screen derivatives. With `Feedback Downsample` above 1, each readback samples another cell of a grid
    of up to 4 x 4 positions inside the feedback pixels, and the requests of one pass over the grid stay active, so
    every screen pixel requests its page. After a view change the feedback runs every frame until the grid is
    complete. KuberaWorld uses a downsample of 4.
  - Residency bias fixes: after a bias change the bias waits until the request history of each active camera holds
    only rounds at the new bias. Before, it stepped again on each readback while the old rounds were still in the
    history, up to the 3-mip maximum. The load counts only the pages of the primary cameras, so a Scene view that fills
    the room left by the game camera no longer keeps the bias from stepping back.
  - KuberaWorld after these fixes (same 7 views, Scene view open): cached vs direct within capture noise, bias 0 to
    0.5, at most 18 evictions per 60 stationary frames. Terrain GPU saving 0.43 to 0.64 ms at ground level and 1.22 to
    1.96 ms elevated.
  - Batched page fills: the pages of one frame draw into their own staging slices, then one BC3 encode per channel
    covers all of them, and one command buffer submits the frame. Before, each page drew, encoded and copied through
    one staging set, so about 7 small GPU steps per page ran one after another. `VirtualTextureCompression` takes a
    `batch` size (its staging targets and block surface are arrays) and has `Encode(CommandBuffer, int[] slots, int
    count)`; `Encode(CommandBuffer, int slot)` encodes staging slice 0. KuberaWorld fly-through at 3 m per frame with 8
    fills per frame: fill GPU time per frame 1.00 -> 0.32 ms median, 2.19 -> 0.62 ms p99. A full invalidation refills
    about 1700 pages in about 110 frames at up to 0.55 ms per frame.
  - Request history after a view change: only the newest 4 rounds stay, then the grid fills again. Before, the 16
    rounds of a moving camera (64 frames at a feedback interval of 4) kept requesting the pages of old views, which
    doubled the requests after a view change.
  - `Scale Pool With Resolution` scales the pages with the square root of the pixel ratio (was linear). Measured
    demand in KuberaWorld grows 2 to 2.9 times from 720p to 1440p. With 2048 pages at 1440p: 1080p 1536 pages (81 MB),
    a 1287 x 574 Scene view 960 pages (51 MB, bias 0; was 448 pages at bias 1).

- Vegetation: a height change on a tile with native trees loads again only the 64 m tree cells in the changed area
  (`TerrainVegetationStream.RefreshTrees`), not the whole vegetation stream of the tile. An evicted detail patch
  measures its height range again (before, the range stayed from the first load). The non-streaming path still
  replaces all trees of the tile.
- Vegetation preview: an undo reloads the detail patches of a tile only when the undo itself changed the terrain data.
  The data versions are recorded on each editor tick; before, they were recorded at registration, so after any stamp
  edit every later undo reloaded all detail patches of the edited tiles.
- Ground cover updater: the stamp world subscription reads `LoogaStampWorld.EnabledWorlds` (new public read-only list)
  on each tick instead of searching the scenes on each hierarchy change.
- Stamp texture watcher: an imported texture that is not loaded is skipped, so a project-wide texture import does not
  load every texture. A stamp holds the textures that it samples.
- Terrain surface auto build: an imported, moved or deleted asset queues a check only of the surfaces that read it
  (their dependency paths at the last change). A changed surface asset or pack shader still queues every surface.
  Before, any texture import or asset deletion checked every surface and cleared the failure state of all of them.
- Terrain shadows: a height or hole change with a terrain callback region gives the static shadow cache the world
  bounds of the region (one patch wider, the full tile height) instead of a global change. A height texture change
  without a callback stays global. Undo no longer marks the terrain shadows of every tile as changed: changed heights
  reach them through the callbacks and the height texture revision.
- Water body undo: an undo or redo rebuilds a `LoogaWaterBody` (mesh, shore map, stamp change mark) only when its
  build inputs changed: spline knots and tangent modes, spline matrix and the shape, mesh, flow and shore settings.
  Before, every undo rebuilt all water bodies (KuberaWorld: 133 bodies, about 2.9 s). An undo that moves only
  obstacle colliders no longer refreshes the shore. `LoogaWaterFlow` zones notify the water on undo when their spline
  knots changed.
- Live roads: an undo alone no longer starts a `LoogaRoadLive` update. An undone road, pad or bridge marker change
  reaches it through the object change events of the restored objects. Before, every undo ran the road topology and
  the bridge check (KuberaWorld: about 0.3 s), also when the undo changed no road input.
- Live roads: the live bridge update sets the rock face bridge openings without undo records, like the bridge parts
  and the clearance. Before, the record came after the road edit, so the first Ctrl+Z reverted only the openings, and
  the update after an undo cleared the redo. The Build command still records them.

- Procedural grass edits: an edit of a `ProceduralGrassType` in the editor uploads the type data of the renderers
  that draw it with the next frame, with no coverage rebuild (before, a type edit showed only after Refresh
  Coverage). `ProceduralGrassRenderer.GrassChanged` occurs when a renderer draws changed grass data after its first
  build (an edited type, or rebuilt coverage after a layer change), so ground tools that color the far ground from
  the grass can build their maps again. Detail layer writes of the stamps do not raise it.
- Grass blade model for ground tools: `ProceduralGrassRenderer.MaximumDensity`, `ProceduralGrassType.FullCoverage`,
  `BladeShare` and `BladeFootprint`.
- Ground cover: the canopy colour is modulated by the soil luminance over the mean albedo luminance of the ground
  layers (`LoogaTerrainSurface.AlbedoMeans`, measured by the array builder; cover row z), so a bright or dark soil no
  longer brightens or darkens the whole canopy. The coarse canopy detail octave uses stochastic tiling.
- Ground cover far canopy (analytic far grass look, no bake): the ground between the far plants is the near ground
  under the plants (Under Canopy shade). The main light visibility of the far ground is split by luminance share:
  the ground between the plants takes the canopy shadow exp(-depth), and the plants take the mean visibility over
  their height (1 - exp(-depth)) / depth (Canopy Shadow density). Before, the far ground was in full sun, so the soil
  between dense plants was too bright, and only a lower Canopy Albedo could dim the plants (not their specular light).
  The split shares are in `LoogaWorldDebug.farShade`. `LoogaGroundCanopySunVisibility` takes them as a new
  parameter.
- Terrain surface Far Detail (new, off by default): detail of the far ground, where the texture detail is smaller than
  a pixel. Steep ground (Strata Slope + Slope Range) gets coarse world strata: light and dark bands of Strata Thickness
  with a height warp, a band contrast that changes along each band, and a ledge relief at the band edges (normal tilt).
  The curvature of the terrain heights (patch target only: four extra height samples at Curvature Step) darkens
  hollows and lightens crests. Both fade in from Fade Start over Fade Range. Material vectors `_LoogaFarDetail`,
  `_LoogaFarStrata`, `_LoogaFarCurvature`; `LoogaSurfaceInput.curvature`. Measured cost about 0.05 ms per 2560 x 1440
  view (two far views, interleaved A/B, within the editor timing noise).
- Grass ground cover on the GPU (new): `ProceduralGrassRenderer` adds its grass to the ground cover maps of the
  `LoogaTerrainSurfaceGlobals` from its own coverage arrays and grass types (`GrassGroundCover`,
  `LoogaGrassGroundCover.compute`). The base maps then hold only the other plants. A texel takes the detail cells whose
  centres lie in it, as CPU ground tools sum them: blade lattice share, lattice limit, blade footprint (Cover Closure,
  default 0.49, times the maximum density) and the own colour (distant base albedo over the canopy colour scale). The
  base closure turns back into a density, so 1 - (1 - base) x exp(-grass) and the density-weighted canopy colour match
  a CPU sum of both within one 8-bit step. A type edit, a coverage upload or a
  terrain rebuild composes the maps again before the next frame (5000 x 3000 texels: about 13 ms of GPU work once).
- Terrain surface globals: `ILoogaGroundCoverComposer` and `SetGroundCoverComposer` let another system add its plants
  to the ground cover maps before they are bound. `ComposeGroundCover` (default true) binds maps as given, for saved
  maps that already hold those plants. `BoundCoverage` and `BoundCanopy` return the bound (composed) maps.
- `ReplacedDetailLayers.Version` rises with each change of the replaced layers, so tools that leave the replaced layers
  out of their data can build again.
- Ground cover: new settings Canopy Sheen and Canopy Sun Sheen (`_LoogaGroundCoverDense.w`,
  `_LoogaGroundCoverDetail.w`, neutral 0 by default). The sheen is a grey albedo added to the far canopy colour: the
  base value from above, plus up to the sun value at a low view angle toward the sun ((1 - sin elevation)^4, 0.2 of
  it away from the sun). The rock face shaders declare `_LoogaGroundCoverDetail` for the sheen.

- Rock formation mesa mode: `LoogaRockFormation` has a Mesa mode (`RockFormationMode.Mesa`, `RockMesaShape`) for
  layered desert mesas and buttes. The spline is the top edge. The faces step down and out through horizontal layers
  at world heights: hard layers form steep cliffs (Cliff Angle) and ledges, soft layers form recesses in the cliff
  zone and slopes (Slope Angle) in the Slope Zone near the ground. A hard caprock (Cap Thickness) overhangs the layer
  below it, some hard layers form benches, and each layer face varies along the outline (Ledge Variation). The layer
  sequence depends only on the strata settings and Strata Seed, so formations with the same settings show the same
  layers at the same heights. The top is the formation top (use a low Crag Height for a flat mesa top).

- Far ground cover: the terrain surface shades the world color map part of the canopy color as the plants shade their
  blades (`LoogaTerrainSurfaceGlobals.SetGroundCoverMapShade`: RGB factor and saturation, neutral by default).
  `ProceduralGrassType.DistantShade`, `ColorMapBrightness`, `Saturation`, `Transmission` and `CanopyShadow` give the
  values for it. Ground Cover has new settings, neutral by default: Canopy Occlusion (sky light occlusion of the far
  canopy) and Canopy Detail Size, Strength and Normal (light and dark patches in the far canopy color and normal from
  the Anti-Tiling detail noise, two octaves). The far canopy takes no canopy shadow and no thin leaf lighting.

- Water foam simulation window: on slow open water far from a shore (below 0.2 of the fast speed, for example a
  sea drift) the shader keeps its own foam instead of the simulated spots, which showed as a square patch that moved
  with the camera. The simulated foam replaces the shader foam fully from 0.4 of the fast speed and in the shore band.
  The window edge fade is 24 m (was 2 m).

- Water river mouths: in a river end fade (`LoogaWaterBody` End Fade), a side whose edge is in open water (terrain
  more than 0.5 m under the surface) tapers its alpha to 0 over the outer 70 % of the half width, so the ribbon edge
  does not show where the river runs into a lake or the sea. Sides on dry ground keep their shore. The ground is read
  at the build; rebuild the river after large terrain changes at its ends.

- Procedural grass: `ProceduralGrassRenderer.Layers` gives the drawn detail layers with their types.
  `ProceduralGrassType.ColorMapShare` and `DistantBaseAlbedo` (the linear base color as thin, distant blades show
  it: root and tip mean, mean tip tint, saturation) let ground tools color the far canopy like the blades.

- Cliff wall kit style: `LoogaCliffWall` has a Style (Procedural, the default, or Kit) and `CliffWallKit` settings.
  The kit style finds the terrain face like the procedural wall (face angle, search, minimum height, drop side,
  excluded ranges) and covers it with copies of rock prefabs instead of one generated mesh: rows from the rim down
  (the top of each top-row piece is fitted below the rim by its mesh vertices; each piece stands against the face at
  its own height, with an embed share of its depth behind the face, and leans back with a share of the face lean),
  smaller filler pieces at random places on the face (density for each 100 m2), and boulders on the ground in front
  of the foot. The copies are drawn by an instance set on a hidden object that is not saved with the scene; they get
  collider copies when the rock face settings have Colliders on. In a player build, an unreadable mesh is fitted by
  its bounds.

- Stamp world collider area: `LoogaStampWorld` has an optional Collider Area (a `LoogaSplineArea`) and a margin
  (default 100 m). Object placements outside the area and its margin get no collider copies, for example placements
  far from the playable area. The world makes a 4 m grid of the area (inside of closed splines, chamfer distance for
  the margin) again only when the splines or the margin change, and gives it to the placement set as its collider
  filter at enable and at each placement write.

- Rock mesh openings: `RockMeshOpenings` (Geology) finds the open rims of solid meshes, such as rock scans with an
  open back or bottom, and makes closed copies with a cap over each rim (ear clipping in the plane of the rim, fan
  fallback, flat cap normal, rim UVs and colors). `CanShowOpening` tells if a tilted copy can show a main opening: a
  ground opening is safe only when it points down after the tilt and its rim stays in the lowest 15 % of the mesh.
  Foliage (more than four openings or many open edges) is ignored. The Assets > Looga > Close Open Rock Meshes command
  replaces the open meshes of the selected prefabs (renderers and mesh colliders) with closed copies in a
  "Closed Meshes" folder. The `LoogaObjectStamp` inspector warns when the rotation and slope alignment of a prototype
  can show an opening, and `ObjectStampOpenings.FindExposedPrefabs` gives the same check to tools.
- Fixed: a detail layer kept its old details when its last detail stamp was disabled or removed. The stamp world now
  keeps the layers that it painted before in its write list and writes them empty. After a domain reload the list
  starts empty again, so a layer whose stamps were all turned off before the reload is not cleared.
- Terrain field filters: texture, detail and object stamps get two `TerrainFieldFilter`s (default off). A filter
  keeps the weight where a shape value of the ground is in its range: steepness, relative height within 4 m and 20 m
  (convex rims and crests vs. hollows and feet), northness, and four values of the nearest faces (ground of 58 degrees
  or steeper; soil slopes stand up to about 50 degrees): distance to a face above, elevation angle to a face above (rockfall cone), distance to a face below
  (rim zone) and the drop to a face below (cliff height under a rim). `TerrainFieldMaps` measures the fields on the GPU
  per tile (`LoogaTerrainFields.compute`, about 1 m texels, 48 m reach into the neighbour tiles, so tile seams stay
  continuous) from the final heights, once per tile and evaluation, and only for tiles that a field-filter stamp
  touches. The face search turns its 32 rays by a hash of the world texel and blurs the result, so small faces do not
  draw star patterns. The editor updater grows texture and placement updates by the 48 m reach after a height edit
  when a field filter is in use. `LoogaStampWorld.MeasureTerrainFields` returns the maps of one terrain for debug views.

- Terrain field filters, flow: two more fields, `FlowArea` (log10 of the upstream area in square metres) and `Wetness`
  (topographic wetness index ln(area per metre of contour / slope tangent)). `TerrainFlow` (Burst) routes water on a
  2 m grid that reads 512 m past the tile: Priority-Flood with a 1 mm rise and a FIFO queue for filled cells (Barnes et
  al. 2014) fills pits and flats, then a multiple flow direction pass (Quinn et al. 1991) moves the area downhill.
  Filled pits and flats take the upstream area of their outlet and a fixed wetness of 14, so they show no straight
  fill lines and no seams. About 85-105 ms per tile; `TerrainFieldMaps` caches the flow map of each tile until a
  height write within 512 m (`LoogaStampWorld.HeightsUpdated` sites) or a full texture rebuild. Edit updates grow by
  at most 128 m for flow filters; farther downstream changes show after a full update.
  `LoogaStampWorld.MeasureTerrainFlow` returns the cached map for debug views.

- Clear stamps: the two terrain field filters (`_fieldFilter`, `_fieldFilter2`, default off), for example to thin
  details or objects in the rim zone of a cliff.

- Detail stamps: Slope Reach Fade (`_slopeReachFade`, default off). With the Steepness measure and a slope reach, a
  steep slope counts less with its distance (six rings of samples), so the weight falls off over the reach from a steep
  face instead of ending at a hard line. Only ground that rises at least 2 m over the point counts fully, so grass
  thins toward the foot of a cliff and not along its top. Clear and object stamps keep the hard reach.

- Cliff walls: slab droop (`CliffWallShape.CapDroop`, default 0 = the flat underside as before). The front of a slab
  top hangs down by the droop below its underside: it rolls over in a larger round, and the underside goes up from the
  hanging tip to the face, so a ground-covered slab hangs over the face as a thick turf lip. Soil Variation changes the
  droop along the wall. Slab rows are found by profile length (`TopLine.SlabEnd`), not by the underside height.

- Rock face shaders: the moss patch edge fades over a wide range of the moss amount (smoothstep 0 to 0.6, was a
  linear ramp to 0.25), so moss patches have softer edges.

- Rock kit face (prototype): `LoogaRockKitFace` covers the terrain cliff face below a cliff-top spline with copies of
  sculpted rock meshes. Each copy gets its own orientation, mirror, non-uniform scale and bend; it bends along the
  cliff, its widest outline lies on the face and its back is pressed into a thin layer behind the face. A 0.25 m
  coverage grid places pieces until a target share is covered. Output: one mesh per chunk, no LODs yet.

- Rock formations: a ground smoothing window wider than a small outline no longer reads outside the ground array
  (`RockFormationBuilder`).

- Rock face shaders: `_StainFlat` (Stain On Flat Faces, default 0 = steep faces only as before) lets the mineral
  stains also tint faces that look up, so broad ledge tops weather like the face instead of showing the pale base
  color. `_MossTextured` now uses `[ToggleUI]`; `[Toggle]` wrote an unused `_MOSSTEXTURED_ON` keyword to materials.

- Cave entrances: the rays copy the finest wall mesh (LOD0), not the level 1 collider, so the entrance keeps the fine
  ledges and cavity of the wall. Cliff walls and rock faces keep the CPU copy of the LOD0 chunks that touch an active
  entrance box (`LoogaCaveEntrance.DetailAreas`, `RockFaceChunks` `detailAreas`); the entrance adds temporary mesh
  colliders to them for its build. A wall built before a box moved there gives its collider shape until it builds
  again. Kubera quarry: 9 chunks, about 150k vertices kept on the CPU.

- Cave systems: floor ground. `LoogaCaveSystem` Floor Ground (a Transform on the terrain, empty = rock floors as
  before) and Floor Slope (normal Y range): cave and entrance floor vertices get the ground weight in the vertex
  alpha and the world offset to that point in UV1, so the rock material's terrain ground blend shows the ground of
  that point, darkened by the cave shade. Chunks without terrain quads get the ground block of the point's terrain.

- Rock face shaders: textured moss (`_MossTextured`, default 0 = the flat moss colors as before). A triplanar moss
  texture set (`_MossMap` albedo + smoothness, `_MossNormalMap`, `_MossMaskMap` G occlusion / B height / A
  smoothness, `_MossTiling`, `_MossTint`) is height-blended against the rock height (`_MossHeightBlend`, `_MossEdge`)
  with small clump noise, so moss fills the rock texture's low parts first and ends in a crisp ragged edge. Moss keeps
  its own normal (`_MossNormalStrength`), occlusion and a dark cushion rim (`_MossCushion`); a grazing-angle sheen
  (`_MossSheen`) gives the soft look. `_MossSunAvoid` reduces moss on faces toward the main light (all moss modes).

- Cave entrances: a wall surface above the terrain takes only the wall's ground cover. The up-facing ground cover of a
  rock-framed entrance put terrain ground patches on the wall ledge tops inside the box (rock beside the box). Floors
  at or below the terrain keep the up-facing cover.

- Cliff walls: form shade. `CliffWallShape.ShadeDepth` (default 0 = off) and `ShadeRadius`: each strip vertex is
  compared with the face smoothed over the radius (two box averages per grid direction); hollows behind the smoothed
  face raise the vertex cavity (G), so the large forms keep their shade at a distance and in the coarse LODs.

- Rock faces: ridged fractal detail. Geometry: `RockFaceSettings.RidgedDepth` (default 0 = off), `RidgedScale`,
  `RidgedStretch` (along the layers), `RidgedWarp`, `RidgedSoftShare`: 4-octave ridged multifractal (Musgrave) on
  domain-warped bed coordinates, added to the layered offset. Shader: `_FractalStrength` (default 0 = off),
  `_FractalScale`, `_FractalStretch`, `_FractalFade`: 3D ridged value-noise fractal as a height, bump-mapped from
  two extra world-space samples along the surface tangents (screen derivatives looked blocky per 2 x 2 pixels);
  octaves smaller than about two pixels fade out; the samples share the domain warp; valleys darken the occlusion;
  fades out with distance. `RidgedSharpness`
  (crest exponent, default 2 = classic), `RidgedTerraces` (0 = smooth; flat treads and steep risers), `RidgedCross`
  (share of a second ridged set stretched up the face, combined by max).

- Cliff walls: fractured face prototype (`CliffWallShape.Fracture`, `CliffWallFracture`, off by default), after the
  implicit block model of Paris et al. 2020. In a share of the wall the face rows are hidden and a block shell is
  built over the designed face (`FracturedFaceBuilder`): beds (thickness, hard share, dip) cut by leaning joints
  into bevelled box blocks with gaps; a back rock layer plus a front block per cell that stands out (hard) or goes
  back (soft) or is missing. Distance field sampled in face space and meshed with surface nets; vertex cavity from
  the rock in front. Three levels of detail (cell, 2x, 4x) split into chunks of about 20 m along the wall with
  LOD groups (`RockFaceOutput.AddLevels`, collider from level 1). A lid of at least 2.5 cells closes the top beds
  under the wall top; the blocks go back into the old face over `EndFade` at the range ends (the face rows end
  along a ragged edge); cave openings cut through the shell. Bed boundaries undulate on their own (`Undulation`,
  `UndulationLength`), bed steps come and go (`LedgeContinuity`), thickness is skewed to thin beds, so the layers
  do not repeat. The shell follows the face without the rock relief; over it the slab front drops that relief too.
  Under a slab top the shell reaches just under the ground-covered slab top and forms the slab front; the slab
  front, bevels and underside rows are hidden there (they floated over the blocks). Dual contouring places the
  vertices (QEF of the field normals at the edge crossings, kept in the cell), so block edges stay sharp; bevel
  0.015 m; front edges get chips (`ChipChance`, `ChipSize`) whose size changes along the edge.
  Panels: major joints split the wall into panels (`PanelLength`); each panel (and, by `TierChance`, its upper tier)
  steps out (`PanelStep`) and leans top-out or top-back (`PanelLean`, at least a third of it, twisting by
  `PanelTwist`), limited to `PanelReach`; joints close at the back of the lower panel. Mesh reduction:
  `FracturedFaceDecimator` (quadric edge collapse onto existing vertices, chunk borders and open edges locked, flip
  and valence limits, error 1/2/4 cm per level), per chunk in parallel; triangles under the terrain are dropped.

- Rock faces: fracture relief (`RockFaceSettings.FractureDepth`, default 0 = off; `FractureSize`, `FractureTilt`,
  `CrackDepth`): Voronoi fracture faces at three sizes (each a third of the one before), each a flat, tilted cell with
  its own step, plus cracks along the cell edges (vertex cavity). `StrataBevel` (default 0.6 = old fixed value) sets
  the width of the weathered top of hard layers. Rock face shaders: `_FacetShading` (default 0 = off) blends the
  triangle normal into the vertex normal near the camera, fading out at `_FacetFade` meters.

- Cliff wall slab top (prototype): `CliffWallShape.Top` = `Slab` (`CliffWallTop`, default `Ground` = old look). A
  rock slab like a countertop lies over the wall top: back edge into the ground at `CapBackAngle`, top `CapRise` over
  the top line or ground, front `CapLip` past the face top with `CapBevel` edges, `CapThickness`, flat underside back
  to the face, `CapRelief` share of the rock relief on the top. The buttress move grows in from the rim to the front.
  With `GroundOnTop` the whole slab shows the land ground (terrain surface mesh, cover 1, no ragged start or soil
  hang). Without it the slab is rock and its back takes the land ground through the rock ground blend over
  `CapBlend`. The slab front follows the buttresses plus the top face row relief as a running maximum over 2 m,
  averaged (`SlabFrontRelief`), so it covers the face top without spikes; all slab rows have the reduced relief. Slab
  rows ignore the cave tuck and lintel pull and are hidden only inside a cave. Defaults: rise 0.15, lip 0.1,
  thickness 0.4, bevel 0.1, relief 0.15.

- Cliff wall top cover: the ragged fade-in of the top now ends at the roll point (where the terrain starts to fall)
  or the rim, whichever is first, and the rounded edge is always fully covered. With a short top (`TopBack` 0.5 m,
  quarry walls) the 1.5 m fade never finished; the level-top buttress moved the edge rows about 3 m out, so one
  stretched quad had cover 0.5-0.65 and the cover clip cut holes through to the terrain face and cave holes.

- Terrain surface parallax occlusion mapping: `LoogaTerrainSurface.Parallax` (`ParallaxSettings`, off by default;
  depth in meters, fade start and end distance, maximum steps) in `_LoogaParallax`, and the per-layer
  `Layer.ParallaxDepth` multiplier (default 1, 0 = flat) in the layer HEIGHT row w. On the top projection the view
  ray steps into the height (albedo alpha) of the strongest layer and moves the world X and Z of all projections,
  so layer, noise and normal samples show depth near the camera. Stochastic layers keep the cell offsets of the
  pixel along the ray (up to three samples per step). The offset fades with the top projection weight, the
  distance, and where the two strongest layers have similar weights. 4 to `MaxSteps` steps by the view angle.

- Roads: plants come closer to the road edge. The road clear stays full up to the lip foot plus 0.4 m, then fades out
  over 0.5 to 3 m (patch noise along the edge, `ClearReach*`), so sparse plants reach the lip in places. Before, it
  faded over 70 % of the gravel fade (up to about 7 m). The gravel shoulder is narrower: full width 0.1 to 2 m (was
  3.5 m), fade 1 to 3.5 m (was 5 m).

- Procedural grass patches: `ProceduralGrassType._patchHeight` (default 0.4, 0 = off) and `_patchSize` (default
  6 m). Two-octave value noise at the clump centre makes areas of taller grass rise out of a closed mat (up to
  1.5 x the share taller, 0.35 x shorter), on top of the clump length difference, so large fields show depth. The GPU
  type layout gains `patches` (stride 144 -> 160 bytes).
- Terrain surface ground cover canopy shadow: `GroundCoverSettings.CanopyShadow` (default 1.8, 0 = off, in
  `_LoogaGroundCoverSoil.w`). Near the camera the ground under the plants loses the main light by its coverage
  (`LoogaGroundCanopySunVisibility`, Beer-Lambert as the grass canopy shadow), fading out toward the canopy end
  distance. The terrain G-buffer pass writes it in GBuffer1 B (`PackLoogaCanopyShadow`). One extra coverage sample
  per near ground cover pixel. The forward pass does not apply it.
- Procedural grass canopy shadow: `ProceduralGrassType._canopyShadow` (default 1.8, 0 = off). The grass above a blade
  point hides the sun from it (Beer-Lambert from the point to the canopy top toward the main light, scaled by the
  local fullness), so the lower part of dense grass is in shade. The blade writes the main light occlusion in GBuffer1
  B (`PackLoogaCanopyShadow`, Looga Graphics Pro); thin blades use the mean visibility of the blade. No extra pass.
- Procedural grass on mesh ground: `GrassGround` registers objects with mesh parts that show ground (`IGrassGround`).
  Each grass renderer draws those parts from above into a 0.25 m ground map (height and the offset to the terrain
  point that the part shows) after each registry change. Where the map has ground, blades stand on it, take the
  type and density of that terrain point, and move their depth toward the camera like the terrain surface mesh,
  so the mesh does not hide their lower part. A clump that moves a blade over a height step drops it, so no blade
  hangs over an edge. Steep parts (more than about 45 degrees) are left out. Quarry prototype: generate pass
  0.309 -> 0.317 ms, map about 11 MB.
- Cliff walls: `Grass On Top` (default on) registers the ground-covered top of the finest level of detail, so
  the meadow grass runs to the rock edge.
- Cliff walls: `Top Fall` (shape) sets how much of the fall of the ground past the terrain rim the wall top keeps.
  1 (default) keeps the rounded edge of before. Lower values make the buttresses grow in closer to the top.
  At 0 the top is level from where the terrain starts to roll over (8.5 degrees, not 35), the rounded edge moves
  out with the top face row, and the face keeps its full buttress and relief up to the top, so the ground meets
  the face at a sharp corner (Irish sea-cliff look). Before, the relief came back over 3 m below the soil and
  the buttresses over the top 30 % of the face; both moved the upper face out and made a 45 degree edge.
- Vines (prototype): `LoogaVines` (Looga Terrain/Vines) grows ivy over a wall from a spline along its top edge.
  Roots start in noise clumps: cascades grow over the edge and down the face, climbers grow up from the foot. Each
  vine grows in steps like the ivy generator of T. Luft (2007): previous direction, random direction and adhesion to
  the nearest surface, sliding along surfaces it hits; a vine that floats free for longer than a short length dies.
  Vines branch; leaves are separate cards from single atlas leaves with a per-leaf wind phase (vertex alpha) and
  flutter weight (vertex blue); stems have no wind. Temporary colliders on the visible rock level of detail give
  contact with the rock that shows. The growth (stems and leaves) is saved compactly as a binary asset next to the
  scene and the meshes (chunks along the spline, each culled alone) are built from it on load, so the vines grow
  again only when the spline or a setting changes.
- Rock faces, cliff walls, rock blocks and formation walls: the ground blend measures the height above the lower of
  the ground under the vertex and the ground 1.5 m in front of it. Before, the top meters of a steep face took the
  ground texture, because the terrain under them is the steep terrain face just behind the rock.
- Rock face shaders: mineral stains (`_StainStrength`, four colours, vertical stretch, streaks) and moss
  (`_MossAmount`, up-facing surfaces, cavities and drips). Both are off by default.
- Caves (prototype): `LoogaCaveSystem` (Looga Terrain/Cave System) with child `LoogaCavePassage` components builds
  the rock around empty space under the terrain. A passage is a spline on the floor centre line with a width and a
  height (per-knot values blend between knots): a rounded rectangle with a flat floor, as in a mined gallery. Burst
  jobs evaluate the field and mesh it with surface nets in 32 m chunks of 0.5 m cells, with colliders. Where the cave
  reaches the terrain surface, the system writes terrain holes (live, only changed texels; a removed or disabled system
  gives its holes back) and spreads them over the steep face inside the passage outline, so a portal shows one cut
  face up to the ground above it. In a hole the mesh draws the terrain surface with the Looga terrain surface mesh
  material, with a skirt that ends along a ragged edge, and the floor near the hole carries the terrain ground into
  the cave. The cave builds again when a passage or the terrain heights in its area change. Inner passage joints
  overlap, so a point on a joint plane is not left as rock. No levels of detail and no interior darkening yet.
  Passages have a `Shape`: `Mined` or `Natural` (an ellipse cut by a flat floor, with large rock bumps whose height the
  passage sets and whose size the system sets; the bumps fade out over the lowest 2 m of the walls). Each segment adds
  its bumps before the union, so a natural passage joins a mined gallery without a step. `LoogaCaveShaft` (Looga
  Terrain/Cave Shaft) adds a vertical tapered shaft, for example a sinkhole above a chamber. The terrain ground goes on
  into the cave only on floors within 2 m of the terrain surface.
  Cave daylight: each build finds the distance from the sky through the cave air on a coarse grid (Burst Dijkstra)
  and stores a shade per vertex in UV channel 3 (x). The rock face shaders multiply occlusion by 1 - shade, so the
  sky and bounce light (also HTrace GI, which reads GBuffer occlusion) fades with `Daylight Reach` (15 m) to
  `Darkness` (0.95) deep in the cave. The shaders read channel 3 only where the hidden `_LoogaCaveShade` property is
  1, which the cave sets on its renderers: other rock meshes have no channel 3, and the undefined input turned their
  shaded sides black and their sunlit sides too bright.
  `LoogaCaveChamber` (Looga Terrain/Cave Chamber): one room on a flat floor at the object position, turned by the
  object. Mined rooms are boxes with rounded edges, natural rooms are domes with rock bumps. Overlapping rooms with
  different floors make benches. A system now allows up to 1024 chunks.
  Cave openings: the terrain surface skirt around a hole now draws in full (it hid less of the texel hole edge and
  showed the rock below it along a ragged edge). The terrain surface also goes on over the cave surfaces with a sky
  distance under 2.5 m (`CaveLipJob`), with a ragged end on the rock, and each of these vertices shows the terrain
  where the daylight comes in (an XZ offset against the sky distance gradient). Cave air under a hole down to 6 m
  below the terrain height counts as sky, so an open mouth floor is lit and takes the ground of the terrain, and
  the walls of a sinkhole show the ground in place of pale rock. A terrain texel becomes a hole when its surface is
  0.2 m inside the cave (was 0.35 m), which removes small terrain spikes at the foot of a cut face.
  A floor near an opening shows the ground 1.5 m past the opening, and the floor under a hole that already drew the
  terrain surface takes the same offset. Before, the mouth floor showed the steep layers of the cut face above it,
  and the texel edge between it and the flat ground in front showed as jagged teeth.
  A chunk with no passage now also builds when terrain surface over a hole lies in it: a cut face that rises above
  the passages into the next chunk had holes with no mesh and showed through the hill. `LoogaCaveSystem.RockDistance`
  gives the distance to the cave.
- Caves (prototype): `LoogaCaveEntrance` (Looga Terrain/Cave Entrance), a child of a `LoogaCaveSystem` at an
  opening (on the floor at the face, forward out of the cave). In its box (`Width`, `Inside`, `Outside`, `Below`,
  `Height`, which grows to the ground in the box) one surface-nets mesh (0.25 m cells) draws the ground, the cliff wall
  and rock face surfaces (found with rays along forward) and the cave from one field, so the terrain, the walls and the
  cave meet without seams. The system holes the terrain in the box (less a 1 m edge band). Flat ground in daylight and
  the wall soil caps draw with the terrain surface mesh material over the rock; steep surfaces show the cave rock. On a
  wall surface the mesh takes the wall cavity and terrain cover. The rock face shaders and the terrain surface mesh
  shader cut their surfaces in the entrance boxes along a ragged edge (`LoogaCaveEntranceClip.hlsl`, global arrays, up
  to 8 boxes). The entrance mesh marks itself with show value 2 and cover weight + 2, so it stays. The other meshes keep
  their colliders; the entrance collider adds the ground over the holes. Later the same day: up to 16 boxes, with a
  world bounds and per-box sphere test first, so rock elsewhere skips the box loop. `Height` is the fixed top of the
  box; ground higher than its band keeps the terrain. Holes only where the terrain surface is within 1.5 m of the
  cave, so the terrain and its grass stay elsewhere; the mesh drops plain ground more than 2 texels from a hole.
  Without cliff walls or rock faces in the box (a natural mouth in a bank) the terrain surfaces keep the terrain look
  and cave walls near daylight show it too (terrain lip); with them steep ground is rock. The ground block is made
  again when a scene save destroys the splat maps. Fixes: the box clip starts two cells inside the box (the mesh has
  no quads in its outer cell, so pale cover fragments of other meshes showed along the box edge); no rock below fully
  covered ground on intact terrain (it showed at the cover edge); rock stays below the cover within one texel of a hole
  (the terrain blends its hole mask, a dark fleck showed); unframed mouths show the terrain on all surfaces that lie
  on intact terrain; mined portals darken their rock near the daylight edge (cavity up to 0.5 within 2 m of the
  cave). `Hole Reach` is a setting now (1.5 m; 3 m on bare quarry ground).
- Rock face shaders: `World Strata` material toggle (off by default). On, the layer value and the hardness come from
  the world position (`RockWorldStrata`: bands 0.2 to 1.8 times `Layer Thickness`, `Layer Warp`, `Hard Layer Share`,
  `Hard Layer Length`), not from the vertex colors. The soft-layer part of the vertex cavity (0.3 x (1 - hardness),
  added by `RockFaceDetail`) is replaced with the world value. So the cave, the cave mouth, cliff walls, rock faces and
  formations show the same layer at the same point, and no color seam shows where they meet.
- Caves: sloped passages had steps. Past an inner joint the floor of a segment stayed flat at the joint height, which
  was lower than the floor of the next segment up the slope, so the floor made a 1 m tread at each 2 m joint (up to
  20 cm high on a 20 % grade). Past its ends a segment now keeps its slope, and in the joint overlap its floor rises
  and its roof drops by 1 m per meter, so the next segment gives the floor and the roof there.
- Rock face shaders: `Floor Normal Strength` (`_TopNormalStrength`, default 1) scales the normal map on the top
  projection only. Cliff normal maps show ledges, which looked like steps on cave floors.
- Cliff walls: the rock fits around a cave opening vertex by vertex instead of dropping whole columns. Face rock
  within 3 m of the opening turns back into the terrain face, and its soil fades out, so the opening has a bare rock
  edge. The rounded edge over the face shrinks toward the rim, so it does not overhang the opening and its rows keep
  their order. It keeps its ground cover.
  Rock in the opening is not drawn, and the top behind the rim keeps its shape. Only a column whose face is in the
  cave over its whole height gets no rock. `CliffWallBuilder.Build` takes a cave distance in place of the cut test.
- Cliff walls: the top reads the land at most 2 m behind its start. On a narrow bench the full mirror read the steep
  foot of the next face and showed dark cliff rock on the bench.
- Cliff walls: over a cave opening less than 3 m below the top of the face (2 m blend), all rows above the opening,
  top and rounded edge included, go back as far as the column's row nearest to the cave, and stop 0.5 m in front of
  the terrain face. The fit fades out over 5 m beside the opening. Before, the rows above the opening turned back less
  than the rows next to it and the top stood out over the opening as a 3 m ledge.
- Cliff walls: `Excluded Ranges` now apply (shares of the spline length build no rock). Before, only rock faces read them.
- Cliff walls: a steep top keeps its own normal (normal height 0.4 to 0.7 blends to the terrain normal). With the flat
  land normal the triplanar mapping used only the top projection on steep ground cover and stretched it into rings.
- Cliff walls: a wall builds again when a cave shape changes near it.
- Cliff walls (prototype): `LoogaCliffWall` (Looga Terrain/Cliff Wall) builds a steep, coarse rock wall along an open
  or closed cliff-top spline. It finds the terrain face next to the spline and lays a designed profile in front of it:
  lean from -30 to 30 degrees (positive = overhang), a rounded notch at the foot (undercut), a bulge, a rounded top
  edge, buttresses with turned fronts and steep sides, angular facets, and the rock face strata, joints and roughness.
  Per-knot lean, undercut and bulge blend between knots. Chunks, levels of detail, colliders and the ground blend are
  those of the rock faces. The terrain face must be at least as steep as the rock face (85 to 90 degrees under an
  overhang). `RockFaceBuilder.EvaluateDetail` is now internal. The wall top shows the ground behind the rim
  (`Ground On Top`, `Brow Ground` share of the rounded edge), lies 0.05 m over the terrain, keeps the fall of the ground
  past the rim and rolls into the face in one arc (`Brow Radius`, default 2 m), so the terrain seems to run to a convex
  edge.
- Rock faces: a ground height change rebuilds a face only when it reaches the ground near the spline (rectangles of
  about 50 m of spline, grown by the search reach), not anywhere in the bounding region. A long face no longer
  rebuilds for edits far from it inside its bounding box (Kubera World: a 5.4 M vertex border wall, 5.4 s per edit).
  Edit mode builds start only after the ground heights stayed unchanged for 0.3 s, and the 10 s limit of the wait for
  a ground source starts again with each change, so a dragged stamp builds the rock once at the end.
- Contact stamps: the rock formations build again after a settled update only when a height update since the last
  settle reaches the stamp area. The settled region also holds texture and placement updates, which do not change
  the ground under the rock (Kubera World: five desert platform formations, 5.6 s in one frame after edits nearby).
- Rock formations: optional `Ground Source` (a `RockFaceGroundSource`, for example a snapshot before the contact
  stamps). The block and crag shapes stand on it and hide their buried points below it. The ground blend still
  reads the terrain. Before, a contact stamp and its formations formed a loop: the formation foot and the hidden
  points read the contact ramp, and the ramp reads the formation meshes, so every height update of the area changed
  the ramp and rebuilt the formations. With a ground source, a build whose shape changed raises `ShapeChanged`, so a
  change of the ground under the rock runs the contact stamp once more.
- Cliff walls: the face search skips steep runs lower than `Minimum Face Height` (for example a small step behind
  the rim) and continues to the next run near the spline. Before, such a step stopped the search and left the column
  without a wall.
- Cliff walls: the buttresses grow out below the top edge (over the top 30 % of the face), so the edge stays next to
  the terrain rim. The wall top reads the terrain layers right beside it (under itself, or 0.75 m behind the terrain
  rim), so it has no seam with the terrain. The top line starts 0.5 m behind the terrain rim at a height and fall
  averaged along the wall (the rim itself is a steep drop, so a height measured there made 1 m steps between
  columns). Behind it the top lies on the ground for 1.5 m and then sinks 0.15 m under the terrain over 3 m, so the
  terrain covers its inner end without a visible edge. A ground-covered top has no rock relief. Top vertices near the
  rim are always drawn (buried-vertex culling cut holes there). Revised: the top line starts 1.5 m behind the rim
  with the slope of the land there (not of the rounded rim), stays over the local ground up to the rim, and sinks
  0.15 m under the terrain over 3 m behind its start, so the terrain itself shows the ground up to the brink. The top
  reads the terrain layers 3 m behind the rim: closer, the terrain has its steep-ground layers, and the top was about
  half as bright as the land beside it. The wall line search skips the top behind the rim (its sunk part pushed the
  wall out by the top length, ~10 m) and the lowest 1.5 m of the face (the rock foot may stand in the rounded terrain
  toe).
- Cliff walls: soil cap (`Soil Depth`, default 1.5 m, `Soil Variation`). With `Brow Ground` 1 the ground of the land
  rolls over the whole rounded edge and hangs down the upper face with a ragged lower edge, so from the land above
  the edge shows ground and almost no rock. The soil hides 80 % of the face relief under it.
- Rock strips have a show value per vertex (`RockFaceStrip.Show`, stored in the normal w). Below 1 the rock shaders cut
  the surface away along a ragged world-space noise edge, in the color and the depth passes. The cliff wall top fades
  out behind the terrain rim, so it ends in an irregular edge under the terrain, not in a line where the surfaces
  cross. The whole brow of a ground-covered top is ground, and joint and facet darkening needs relief.
- Cliff walls: the top follows the terrain surface 0.05 m under it up to 3 m behind the terrain rim (not drawn
  there), then leaves it along the slope of the land and rolls into the face, so the two surfaces meet at a tangent.
  It reads the land's layer mix mirrored at that point. The show fade of the top is no longer used.
- Cliff walls and rock formations read the ground again when a terrain replaced its splat maps
  (`RockFaceOutput.RefreshDestroyedGround`). After a scene save their ground blend was black.
- Rock face ground blend: a full ground weight shows only ground. Before, bright rock grains also showed through
  there (the blend rule of the rock foot).
- Terrain ground blend: layers with stochastic sampling on the terrain get it in the ground blend too
  (`_GroundStochastic` from the terrain surface), so the ground on rock has no visible repetition.
- Terrain surface mesh target: shader `Looga/Terrain/Surface (Mesh)` and `LoogaTerrainSurface.MeshMaterial` draw a mesh
  over the terrain ground with the shading code of the terrain (`EvaluateLoogaTerrainSurface`), so its colour, layer
  pattern and lighting are the same as on the terrain. The vertex holds the offset to the terrain point that it shows
  and a cover weight; below a weight of 1 the mesh ends along a ragged world-space edge. The mesh moves 0.02 m (more
  at a distance) along its normal and casts no shadow. `LoogaTerrainGroundBlock.Create` also sets the per-tile surface
  values (`_Control0..7`, control size, terrain size); `LoogaTerrainGroundBlock.SurfaceMeshMaterial` returns the
  material of a terrain with an applied surface.
- Rock strips have terrain cover (`RockFaceStrip.TerrainCover`, vertex TEXCOORD2). Chunk meshes put the covered quads
  in submesh 1, drawn with the terrain surface mesh material. A fully covered quad is only terrain surface (no rock
  under it). Cliff walls write the ground-covered top and the soil on the face as terrain cover, so the strip on top
  of the wall has no seam with the terrain; the rock ground blend is used only at the wall foot.
- Rock face shadow caster: a surface turned away from the light gets no normal inset, and the pass clips by the show
  value. The inset moved a cliff wall face into the land behind it, and at a low sun the land next to the wall top
  was in shadow while the wall top was lit. Project shaders that use `UsePass "Looga/Rock Face/SHADOWCASTER"` take the
  change on their next import.
- Terrain surface mesh: the shader is in the AlphaTest queue (its ragged edge clips pixels). In the opaque queue the
  Looga occluder depth prepass drew it, and the terrain behind its clipped pixels was missing (sky-coloured spots).
- Rock face chunks: a quad with a show value of 0 at every grid point has no rock triangles.
- Cliff walls: the ground-covered top starts at `Top Back` (new shape value, default 3 m) behind the terrain rim and lies
  0.03 m over the terrain from there, so it hides the rock band and a raised lip along the terrain rim, and the coarse
  terrain levels of detail near the cliff (their chords cut under the true ground and showed as black notches). The
  top line turns up to clear a lip (largest slope in the window, then averaged along the wall), so it stays straight
  over the lip at every level of detail. The top fades in from its start with a ragged edge over 1.5 m, with the
  terrain normal and the layers of the land there; behind its start it lies 0.3 m under the ground and is not drawn.
  The rock under a ground-covered top is not drawn. The deep tuck at the inner end of the top is gone (a coarse level
  of detail joined a drawn vertex to a tucked one and showed a steep wall).
- Cliff walls: the ground-covered top takes the terrain normal of the land point whose layers it shows, and the
  rounded edge turns to its own normal. A top that turned up over a terrain lip faced a low sun and showed as a
  bright band.
- Cliff walls: the ground-covered top follows the terrain (0.03 m over it, 0.18 m in front of the fade, a little more
  over a convex shoulder) from Top Back to the roll point, where the terrain gets steeper than 35 degrees; from there
  it goes on straight with the terrain slope at that point and rolls into the face. Roll point and slope are averaged
  along the wall. A top on a straight line from Top Back stood up to 3 m over a wide rounded rim, and the gap under it
  showed close up. Replaces the lip-fitted top line of the previous entry.
- Terrain surface mesh: its depth moves 0.15 m + 0.4 % of the camera distance toward the camera (screen position and
  shading do not change). Coarse terrain triangles next to a cliff lie over the true ground and showed through the
  mesh as dark slivers.
- Terrain surface mesh: ShadowCaster pass. It casts only where the vertex cast value (`RockFaceStrip.TerrainCover.w`,
  now a Vector4) is 1, with no normal inset for surfaces turned away from the light. Cliff walls set it from the roll
  point on (rounded edge and soil on the face), so the top edge casts its shadow again; on the land the terrain
  casts.
- Rock face ground blend: only steep rock gets the flat terrain light at its foot. Ground on rock that lies like the
  terrain keeps its own normal, so a ground-covered top takes the light of the slope beside it.
- Terrain ground blend (rock faces, cliff walls, roads): reads up to 8 control maps (32 layers). Before, it read 4, so
  rock on ground of layer 17 or later blended into a black ground. The extra maps are read only when the terrain has
  more than 16 layers.
- Rock face ground blend: the ground takes the texture occlusion of the terrain layers (with the layer occlusion
  strength, as on the terrain). Before, it had no occlusion, so in the shade the ground at the rock foot was about
  twice as bright as the terrain beside it. `LoogaSampleTerrainGround` has an overload with an occlusion output.
- Texture stamps write up to 8 alphamaps (32 terrain layers, the range of the layer maps and of the Looga terrain
  surface). Before, the limit was 4 alphamaps: a 17th layer was added to the terrains but never painted, and its
  index wrote past the end of the 16 channels.
- Rock faces: `RockFaceSettings.ClearRoads` (default off). The road network then gives the face an opening along each
  road near it (`LoogaRoadBridges.Openings`, also on bridge updates): straight 12 m pieces 0.3 m under the terrain on
  the road center line, half width = road half width + 1.5 m. No rock then stands on a road. Block walls of canyon
  platforms next to roads reached the asphalt.
- Rock faces: `RockFaceOpening.SideSlope`, the rise of the allowed rock height beside an opening (0 or a saved opening
  = 1 m per meter as before; bridge openings keep it). Road openings use 10 m per meter: with the bridge notch the
  rock of a 30 m wall 20-30 m from a road was cut down to its lower half.
- Vegetation preview: a scene save writes the stored native foliage visibility also for terrains that wait for their
  provider after a script reload. Before, a save in that time wrote the hidden state into the scene. On the next load,
  each provider saw native foliage already off ("Native vegetation is already disabled by another owner"). It did not
  draw, so detail prefabs were missing and the progress bar showed "loading cells" without end.
- Procedural grass: `Width Thinning` (0 to 1, default 0). Blades narrower on screen than `Minimum Pixel Width` are
  widened as before. With thinning only the share natural width / minimum width of them stays, so the field keeps its
  coverage. A minimum of 2 pixels keeps far grass stable under temporal antialiasing (Kubera World had 0.75).
- Procedural grass: optional `Thin Blade Smoothing` (default 0). Color and occlusion of blades one to four pixels wide
  move to the blade average. Measured: flicker 43 to 37 % with TAA High, 1.0 to 0.6 % with TAA Very High, and a visibly
  flatter field. Averaging normals and gloss too removed the sun sheen and darkened the field.
- Procedural grass: optional `Motion Vectors` (default off). A second pass after the URP motion vector pass writes the
  wind and camera motion of the visible blades (shared blade code in `Shaders/Grass/ProceduralGrassBlade.hlsl`, previous
  wind field and `_LastTimeParameters`). Measured on 1 to 3 pixel blades it increases the flicker, so it stays off.
- Procedural grass: no draw while the compute shader has invalid kernels. A failed compute compile removed the D3D12
  device and crashed the editor.
- Procedural grass: the renderer counts an 8-frame noise cycle for each camera and passes it to the leaf contact
  shadow (needs Looga Graphics Pro with the same change). The contact shadow noise then averages under temporal
  antialiasing instead of showing as static.
- Stamps editor updater: a new or removed stamp updates only its own area. Before, any change of the stamp lists (also
  an added or deleted stamp) updated all terrains. Only a stamp that moves to a different place in the stamp order
  still updates all terrains. An update that covers more than half of the terrains logs its causes to the console.
- Editor activity: stamp and terrain work shows as the "Looga Stamps" and "Looga Terrain" tasks in the editor progress
  bar instead of the removed Scene view overlay.
- Terrain shadows: in a shadow split that is not persistent, a terrain shadow patch is never finer than the camera
  patch at its place. A finer caster lay above the coarse visible terrain on convex ground at some distances, so the
  ground shadowed itself in round dark spots. Persistent splits (Looga Shadows cache strips) keep the camera-free rule.
- Stamps: spline area falloffs have an `Outside` option. The weight is then 0 inside the area and up to the boost
  distance outside it, and it grows to full weight over the range. A clear stamp can remove details everywhere
  beyond a margin around a playable outline. Such a falloff does not limit the culling rectangle to the area.
- Stamps: `LoogaContactStamp` raises the terrain where rock formations stand on it, so the ground builds up against
  the rock. It renders the generated rock onto the height grid of each tile and finds the ground points inside the
  rock (winding number of the surfaces above the ground), then measures the distance to them (jump flooding). The
  ramp has a contact angle, a reach, a profile power and a reach variation along the rock. After the heights settle,
  the stamp builds its formations again, so their ground blend reads the raised ground.
- Geology: `LoogaRockFormation.ShapeChanged` (raised only after outline or settings changes, not after a build that
  reads new terrain heights) and `LoogaRockFormation.GetGeneratedRenderers(level, list)`.
- Geology: rock formations and block arches (`RockFaceOutput`) read the terrain ground again when a terrain surface
  binding applies or removes its surface. Chunks built before the binding applied, for example on scene load, had
  no terrain layer textures and showed a white ground blend.
- Geology: the rock face ground blend is lit like the terrain beside the rock. Its normal bends from the wall normal
  to the up direction (new `Ground Terrain Light`, default 1; 0 keeps the wall normal). A sunlit wall gave a bright
  band at the foot and a shaded wall a dark band.
- Geology: `LoogaRockFace.OutputVersion` and `LoogaRockFace.GetGeneratedRenderers(level, list)`. A reader outside the
  rock face, such as a ray tracing scene, gets the hidden generated renderers of one LOD level and reads them again
  when the version changes (each build or release).
- Surface: `LoogaTerrainAlbedo` renders the albedo of a terrain surface into a texture in terrain UV space through the
  material Meta pass (height grid mesh in terrain space, the per-tile splat property block, far-distance evaluation,
  linear colour). For systems that need the visible terrain colour without the surface shader, such as a ray
  tracing scene. Player builds can strip the Meta pass.
- Lighting: the procedural grass, road and terrain surface G-buffer passes write their ambient with
  `EvaluateLoogaGBufferAmbient` (Looga Graphics Pro), so `LoogaLightingFeature.externalIndirectDiffuse` removes it
  for an external indirect diffuse source. The grass leaf transmission ambient follows the same switch.
- Water: `LoogaWaterBody` is a `LoogaWaterSource` in the new `LoogaSoft.Terrain.Water` assembly, so water areas
  and stamp water filters read it. A Unity spline defines its shape. Area: closed splines at the height of the body
  object (lakes, seas); an outline inside another outline is a hole (island). A constrained Delaunay mesh
  (`WaterTriangulator`) keeps the sampled outline as the mesh boundary and fills the inside with points on a
  world-aligned grid (`Vertex Spacing`, `Edge Spacing`). River: open splines with half widths in
  `LoogaSplinePath.SplineWidths` units; the surface follows the spline height and can fade out at its ends.
  Spline edits, undo and moves rebuild the mesh and mark the source changed; the same edges give no stamp update.
  Flow: each vertex holds a world XZ flow in metres per second (UV0 xy, surface alpha in z). Rivers flow from the
  first knot to the last and slow down at the banks; areas drift in one direction. `LoogaWaterFlow` zones change the
  flow by area: an area zone (closed spline, one direction) or a current zone (follows its spline), with speed,
  falloff, strength and order. The `Looga/Water` shader moves its detail normals and foam with the vertex flow (two
  phases per `Flow Cycle`), gives fast water stronger normals and foam, uses the vertex alpha and has a
  `Depth Offset` for rivers over lakes. Waves, refraction, absorption, reflection and foam are as in the prototype.
  The `_Current` material property is removed. Not yet: waterfalls, shallow-water wave damping, additional lights,
  underwater view.
  Flowing water: the Gerstner waves fade on fast water (Fast Water Wave Damping, vertices and normals), the wind
  slicks move with the flow and drift with the wind only on still water, the detail layers lose their own drift on
  fast water, and the macro and swell layers move at the full flow speed (they moved at half). Fast water uses shorter
  flow cycles: the cycle halves for each step of speed, so a texture moves at most about half a tile per cycle, and
  two cycle lengths blend smoothly. Before, fast water faded its small detail and foam in place.
- Water bed (`LoogaWaterBed`, height modifier): shapes the ground under the water of a `LoogaWaterArea` from the
  distance to the water edge and the water level of the nearest edge. Inside the water the depth eases from 0 at the
  edge toward Max Depth (95 % at Shelf Width) with optional bed noise, then an optional smooth drop-off (Drop Depth,
  Drop Start, Drop Width, noise on the start) gives a gentle, irregular deepening. Outside the water a beach band
  lowers ground up to Beach Max Height to the slope of the bed at the edge; higher ground (cliffs) stays. An optional
  falloff (for example a spline area) limits the bed. Raster pass 3 and compute kernel `ApplyWaterBed`; the stamp
  updater tracks the bed area and its sources. Ground well above the water (inside) or above the beach slope (beside it) and ground far below the bed (Keep
  Depth) keep their height, so beds can follow the whole water outline without cutting cliffs or filling gorges. An
  optional Drop Area (spline area, Drop Area Fade) keeps authored shallows out of the drop-off.
- Water shader: detail layers turn by their own angles, a 4x larger copy of layer A takes over far away, a large wind
  slick mask varies the ripple strength, Gerstner wave normals and crests are computed per pixel with each wave faded
  below 8 pixels per wavelength, the vertices move only with waves that the vertex spacing (UV0 w) resolves, and a
  large-scale warp shifts each wave so far water shows no lattice. New properties: Far Detail Scale, Wind Slick Size,
  Wind Slick Strength, Wave Warp, Wave Warp Size, Far Wave Normal Strength. A Swell layer (Swell Size, Swell Strength) stays at every
  distance, so far and high views keep visible detail.
- Cave mouth (prototype): `LoogaCaveMouth` (Looga Terrain/Cave Mouth) builds a rock collar around a cave opening,
  like an entrance mesh over a terrain hole: it lines the passage behind the face, comes out over a lip and lies on
  the surface around the opening (cliff walls, rock faces, formations, terrain; found by rays, gaps filled from
  neighbouring columns and smoothed), with its outer edge buried in that surface. It hides the edges where the
  terrain, the cut face and the cave meet. Mesh collider; the cave material and the ground of the terrain below.
- Cave openings: steep terrain-copy faces beside rock meshes of the cave material move to the rock submesh (no dark
  cut face inside a rock frame); level texels at the foot of a holed face are holed too (no terrain spikes in a
  doorway); cave vertices just below the terrain at a hole edge rise to it. Cave shape hash and cliff wall build
  queue are not serialized, and walls build queued changes in the editor update, so walls fit the cave after a
  script reload.
- Rock colliders: `RockFaceSettings.Colliders` (on by default) turns off all colliders of a rock face or rock
  formation, for rock far outside the playable area. Basalt arch `Generate Collider` now also applies to block
  arches (each block chunk gets a collider from its level 1 mesh); it stays off by default.
- Water foam: each `LoogaWaterBody` builds a shore texture (`WaterShoreField`, RGHalf, `Shore Texel Size` 0.5 m, at
  most 1024 texels per side) from its mesh and the terrain heights: R = signed horizontal distance to the water edge,
  G = depth (-1 outside the mesh). The body gives it to its renderer with a property block (`_ShoreMap`,
  `_ShoreMapRect`) and reads the ground again only in the area of each stamp world `UpdateSettled` event
  (`RefreshShore(Rect)`). The shader's shore band now has a width in metres (Shore Foam Width, Shore Foam Pulse), and
  the screen depth term is only a thin contact line (Contact Foam Depth, Contact Foam Amount; vertical depth or
  depth along the view, so walls in the water also get it). Without a shore map the old depth band stays (Shore Foam
  Depth). Foam detail is a new generated RGBA texture (`Tools/Looga/Terrain/Generate Foam Detail Texture`, R clumps,
  G bubbles, B lace, A height, each channel equalized); the coverage is the threshold of each channel, so foam breaks
  into lace and bubbles where it thins. Foam shading: wrap diffuse, occlusion from the height, sun translucency in
  thin foam, a darker under-water foam layer (Under-Water Foam, Under-Water Foam Depth), open water patches (Open
  Water Foam) and a fade to the average far away (Foam Detail Fade Distance). Foam stays visible in the thin water at
  the edge, and no foam shows where the outline cuts through water. `Foam Size` is removed.
- Persistent water foam (`WaterFoamSimulation`, `Resources/LoogaWaterFoam.compute`): each `LoogaWaterBody` steps an
  RHalf foam texture at 20 Hz before a game or scene camera renders (game cameras lead; hidden bodies pause). Foam
  moves with the flow (semi-Lagrangian), gathers where the flow converges (`Foam Convergence`), fades over `Foam
  Lifetime` and rises toward its sources: the shore band and spots of fast water foam, so foam trails go downstream
  from banks, rocks and piers. A body up to `Foam Window` (256 m) simulates in full, a larger one a window around
  the camera; texels from `Foam Texel Size` (0.25 m), 512 per side at most; a new window warms up 6 s at once. The
  shader replaces its shore and fast water terms with the simulation inside the window (`_FoamSim`, `_FoamSimRect`,
  2 m edge fade); contact, crest and open water foam stay instantaneous. The shore map is now RGBAHalf with the
  world flow in BA, and colliders that go through the surface (`Obstacle Layers`; not terrain or moving bodies)
  are shore, found with one area overlap and `OverlapBoxCommand` per texel in candidate bounds. Shared band
  functions are in `LoogaWaterFoam.hlsl`.
- Water fall shader (`Looga/Water Fall`): falling or fast water on a steep sheet mesh, such as a spillway face or a
  waterfall. UV0 holds the distance across the sheet, the travel time along the flow, the aeration progress and the
  edge distance. Streaks move with the local flow speed, so they stretch as the water speeds up. The water turns
  white along the drop, keeps darker streaks, and breaks up into foam over the pool (progress 1 to 2).
  Version 2: three flow layers at non-matching speed ratios; a fall height texture moves the vertices (bulges on the
  drop, boils at the plunge) and shades the foam cavities; foam gets wrapped diffuse, backlight translucency and a broad
  highlight; clear water refracts the opaque texture with absorption by the water thickness. UV0 w is now the distance
  from the sheet start (start fade), and UV1 adds the wall distance (white water at walls and pier wakes), the water
  thickness and the distance to the sheet end. New textures: Fall Height, Aerated Foam Mask, Pool Bubbles.
  UV1 w = 1 marks a foam shell: a second sheet above the jet that draws only large white lumps (Shell Coverage) and
  bulges more (Shell Bulge Scale), so falling water looks thick. The plunge boil and the pool churn reach further.
  Version 3: UV0 y is now a stretched coordinate (texture tiles along the flow) and UV2 holds the tiles per second and
  the speed; the textures move in two time-cycled phases, so speed changes along the sheet no longer shear or smear
  them. Air starts at the brink (progress -0.3 to 0 before it). The pool foam is a world-space layer with a warped,
  slowing flow, patchy rafts and a solid boil band at the plunge (Pool Foam Size, Pool Boil Share, Pool Flow Warp).
  Clear water absorbs along the scene depth path. Travel Per Tile is replaced by Flow Cycle. Start Fade is separate from the
  end fade (Edge Fade), so a sheet that starts under another water surface needs no long fade-in. Air Onset keeps
  the water glassy over the brink and whitens it over the first part of the drop, and Min Path gives the clear water
  the depth colour of the water that feeds it.
  Version 4: the sheet data moves from UV0 to UV3. UV0 is free for `Looga/Water` flow data, so a sheet mesh can draw
  its top part first with the material of the feeding water (submesh 0) and then the whole sheet with this shader
  (submesh 1). The feeding water then continues over the brink with the same look and flow. Clear Handoff sets the
  distance past the brink where this clear water fades in, while the base under it fades out. UV2 z is the distance
  along the surface past the brink, and Air Onset is now in meters past the brink (default 0.5). The pass uses
  `ZTest Always` and clips against the opaque scene depth, so opaque surfaces still hide the sheet but the wave
  displacement of a water surface under the pool foam no longer cuts holes in it. The feeding ripple layer (Feeding
  Water Normal, Feeding Detail, Feeding Drift) is removed; the base pass replaces it.
  Version 5: a second pass (`JetDepth`, LightMode `SRPDefaultUnlit`) writes the depth of the opaque middle of the
  drop (no shell, brink fade, sheet edges or pool, up to Depth Write End progress), pushed Depth Push Back meters
  behind the surface. Spray and mist behind a jet no longer draw over it, and effects near the jet show no hard cut.
  The shared code moved to an `HLSLINCLUDE` block.
  Version 6: the depth reaches the plunge (Depth Write End 1, Push Back 1 m), so mist behind the lower jets and their
  foam shells stays hidden. The pool foam moves in flow coordinates: across the flow (spreading by Pool Foam Spread)
  and along the flow in the travel time of the water (UV2 w). It moves exactly with the water at every speed, with no
  flow cycle, so the fast outflow carries the foam away and slower water bunches it up. Pool Foam Tile Speed sets the
  foam length along the flow. Air Start moves the start of the white water along the flow (negative: before the
  brink), so the white can begin where the feeding water meets the drop.
  The forward pass tests the depth buffer again (`ZTest LEqual`, `Offset -1, -1`) in place of `ZTest Always`: with
  Always, the far pool foam drew over the near water at the brink. Toward the plunge and over the pool the vertex
  depth moves Pool Depth Bias meters toward the camera along the view ray (the screen position stays), so the waves
  of the water under the pool foam still do not cut holes in it. Depth Push Back can be negative (toward the camera,
  stopping 0.5 m in front of it). Within one material the JetDepth pass draws before ForwardLit, so a pull toward
  the camera needs a second material with only JetDepth enabled and a queue after the jet. JetDepth now clips by the
  alpha of the white water (the ForwardLit fragment, unlit parts compiled out), so the solid lumps of the foam shell
  write depth too and the mist behind them stays hidden. A sheet is one transparent draw, so where its triangles
  overlap on the screen the later one wins even when it lies farther away (a farther part of the jet painted over
  the near jet edge). Enable JetDepth with Depth Push Back 0 on the sheet material itself: it draws first at the
  surface, and ForwardLit (`ZTest LEqual`, `Offset -1, -1`) then draws only the nearest solid white water.
  Version 7, clumps: a large clump field moves with the water (across the flow in meters, along it in travel time,
  UV2 w). It moves the vertices out along the normal, more toward the plunge and more on the foam shell (Shell
  Clump Scale), so the jets swell and their outline billows. In the fragment it darkens the hollows (Clump Hollow
  Shadow), turns the normal over the clump slopes (Clump Normal Strength) and adds a soft glow at the silhouette
  (Edge Glow). New properties: Clump Size, Clump Length, Clump Height. The vertices follow only the broad octave of
  the clump field (finer detail than the mesh grid made spikes). Shell Softness widens the soft edge of the shell
  lumps, and Foam Normal Strength weakens the streak normals of fully white water, so foam looks soft, not glassy.
  Version 8, pool foam like real plunge pools: a solid boil of Plunge Boil Length with a ragged edge, then foam
  stretched into streaks along the flow (Pool Streak Width and Length) and bent by a slowly changing eddy noise. The
  amount falls by e over Pool Foam Reach from the plunge (Pool Length gives the pool progress in meters) and an
  erosion threshold eats the thin parts first, so the foam breaks into lace and trails with clear water between.
  Pool ripples move in the same travel-time coordinates. The plunge boil moves the vertices with the broad clump
  field only, so the pool surface no longer ripples in sharp, jumping points. Removed: the raft carpet and churn.
  The pool foam is a lace of bubble cells (new texture slot Pool Foam Lace) inside smooth lanes. Only the lanes
  follow the eddy noise: a warp of a tile or more smeared the foam into marble swirls that looked like warped glass.
  Near the plunge the water is aerated (Pool Aeration Reach, Aerated Water Color): milky, more opaque, with weak,
  rough reflections.
  Pool basin and open sea: milky water fills the basin up to Pool Milky Reach (Pool Basin Aeration) with a ragged
  edge, and the sheet covers the rest of the basin as deep water (Pool Basin Cover, Pool Basin Min Path), so the
  basin floor does not show. Past the training walls (Pool Open Start) the cover, the milky water and the sheet
  sides fade out over Pool Open Fade with a slow, smooth noise edge, so the pool has no rectangular outline. The
  pool foam is softer: a wide, partly clear edge, mip-blurred lace cells and flat shading. Milky water has weaker
  ripples and no refraction, so it does not look glassy.
- Grass (prototype): `ProceduralGrassRenderer` draws procedural grass blades on terrains. Each frame a compute pass
  places blades in camera-centred tile rings from the terrain heights, holes and the coverage of mapped detail
  layers (`ProceduralGrassType` per layer). Each farther ring keeps one blade in four, so far blades are a subset of
  near blades. A G-buffer pass after the opaque G-buffer builds each blade as a cubic Bezier strip from the vertex ID
  (15 vertices near, 7 far, 16 blades per instance) with the Looga wind field. The blades are opaque, have no
  alpha test and cast no shadow-map shadows. The renderer enqueues its own pass for each camera, so no renderer
  feature is needed. GPU samplers: `Looga Grass Generate`, `Looga Grass Draw`. Blades lean and droop by angle
  (`ProceduralGrassType` tilt, droop, wind lean and wind alignment), have a glossy midrib, per-clump tip colors and
  less gloss with distance, and mark their pixels as thin leaves in the G-buffer (Graphics Pro
  `PackLoogaThinLeafStrength`). Each clump reads the terrain world color map (`LoogaTerrainSurfaceGlobals`) at
  its centre; the type mixes it with its base color and sets root and tip shades, saturation and a second tip tint.
  Dense grass darkens its lower part more. The renderer turns on the Looga Lighting leaf contact shadow for the
  blades. Antialiasing for thin blades: the rounding, vein and gloss stripe fade out below 4 pixels of blade width
  (like texture mips), geometric specular antialiasing widens the gloss where the normal changes within a pixel, the
  minimum blade width is 1 pixel and 1.5 pixels for cameras without temporal antialiasing (Scene view). No distance
  bands: each blade of lattice level L ends at a random distance between F * 2^L and F * 2^(L + 1) (F = full
  density distance), so the density falls smoothly; near blades blend to the 7 vertex shape before they change to
  it. Temporary cameras release their grass buffers when they are destroyed. Sun highlights from all views: each
  blade twists around its axis (`Twist`, default 70 degrees) and has a random gloss (`Smoothness Variation`); blades
  that are too thin to show their rounding use the normal of their folded cross-section nearest to the sun highlight,
  and the arc variance widens the gloss lobe so that the highlight keeps the energy of the blade width. Not yet:
  motion vectors, interaction.
- Rock faces: the ground blend reads the terrain layers beside the rock (per-vertex ground offset past the cap and
  skirt edges, `Ground Reach`), not the steep-ground layers under it, and applies the world colour and ground cover of
  the Looga terrain surface (`LoogaTerrainSurfaceCover.hlsl`, shared with the terrain; `LoogaTerrainSurface.ApplyCoverTo`
  writes the values to the renderer blocks). Filled formations (`Fill Top`): a closed spline that drops on its outer
  side also gets a rock top inside the found rim, with outcrop relief, soil hollows that show the terrain ground, a
  fade into the wall cap and a collider; the top lies on the higher of the build ground and the final terrain. Edge
  apron: `LoogaRockFace.Apron` holds the raised ground around the foot; a `LoogaTalusStamp` linked to the face
  (`SetRockFace`) reads it and applies it again when it changes.
- Rock formations (prototype): `LoogaRockFormation` builds a rock body from a closed spline and a height (per-knot
  heights optional): walls from below the terrain to the rim with the rock face strata and joints, a rounded edge,
  and the crag top, with colliders. The terrain gives only the ground at the outline (`RockFormationBuilder`,
  `RockFaceOutput`). Crag tops: `CragHeight`, `CragSize`, `CragSharpness`, `CragJaggedness`, `CragCoverage`,
  `CragEdgeRise`, `CragLedges` replace the outcrop settings; the top uses the wall strata for its colours.
  Block mode (`RockFormationMode.Blocks`, `RockBlockShape`, `RockBlockBuilder`): the outline is cut into cells, and
  each cell is one closed rock block with walls, a rounded chipped top edge and a tilted top. Tall blocks split into
  stacked tiers with offsets, insets and wavy joints; gaps between cells give fissures; walls get vertical flutes.
  `RockFaceOutput.CreateGrouped` puts nearby blocks into shared chunks (`RockFaceChunks.CreateCombined`).
  Block mode is now the default mode of a new formation.
  Edge blocks are no longer clipped to the outline: cells also grow in a band around it and each edge block juts
  out (`EdgeJut`) or steps back (`EdgeRecess`) in groups (`JutSize`). `EdgeCut` is removed.
  `OutlineGrow` moves the formation edge out past the outline. `HeightIsMinimum` makes the formation height the
  lowest block top: blocks only rise above it, and tilted tops keep their low side at or above it.
  Hidden faces are not drawn: block walls below the terrain or inside a touching neighbour block get no triangles
  (`RockFaceStrip.CullHidden`; a quad is dropped only when all grid points of its span are hidden).
  Chunk meshes keep only the vertices that their triangles use. Rock face strips hide rock under the final
  terrain, and block arches hide their buried feet and the block parts inside the core.
- Rock face blocks: `RockFaceSettings.Blocks` (`RockFaceBlocks`, `RockFaceBlockCells`) cuts a rock face into blocks
  on the same grid: stratified vertical joints and bedding joints (steps between columns, closed joints join tall
  blocks), per-block relief and lean, fissure and bedding grooves with chipped round edges, raised block tops on the
  cap behind the rim, flutes and surface noise. No extra vertices. `RockFaceNoise` has the shared `Surface`,
  `Flutes` and `Hash01` helpers.
- Block cliff walls: `RockFaceSettings.BlockWalls` builds a rock face from separate rock blocks in place of the skin
  and the top (`RockFaceSettings.WallBlocks`, `RockBlockBuilder.BuildBand`). The blocks of a block formation fill a
  band from behind the found top edge to past the foot (`BandBack`, `BandToe`), jut out and step back at the band
  edges, and rise above the highest ground under them (`BandRise`). Tall blocks stack into tiers of uneven height
  (`BandTierHeight`). Bridge openings limit the block heights. Blocks share chunks by area and terrain, with
  colliders.
  `RockBlockShape.TopLevel`: formation blocks with tops at a world height (plus the band rise) and stacked
  tiers, for walls that stand from a valley floor up to a water level.
  `RockFaceSettings.ExcludedRanges`: parts of the spline where a face builds no rock, for example where another face
  already covers the same cliff. Chunks whose quads are all hidden are skipped (`RockFaceChunks.CreateCombined`
  returns null), so generated bounds no longer reach the world origin.
- Block arches: `BasaltArchConstruction.Blocks` builds an arch from rock blocks (`RockArchBuilder`,
  `RockArchBlocks`): outer cells of the cross section become block columns along the arch, cut into curved blocks
  by staggered, tilted joints, with relief, organic edges and buried feet. `RockFaceOutput.CreateGrouped` takes an
  optional collider flag and splits chunks by height too.
  Block arches thin toward the crown: outer columns reach less far from each foot (`MinimumReach`, `ReachShape`,
  `ReachJitter`) and inner columns show where the outer ones end. Blocks stand out or step back in groups and alone
  (`Relief`, `ReliefInward`, `ReliefGroupLength`). The relief grows only the outer half of a block, so the blocks
  stay fitted to their neighbours.
  `Roundness` and a round limit on each cell give rounder, more cylindrical blocks.
  A solid core tube (facing out and in) fills the hollow inside, so the gaps between the blocks show rock.
  A filled top with blocks is a block pavement (`RockFaceBlockCells.TopHeight`) that starts at the rim.
- Grass (production): `ReplacedDetailLayers` lets another renderer own detail layers of a terrain; the vegetation
  provider then places no instances for them (GPU-resident, generated and native detail paths). The procedural grass
  renderer registers its mapped layers on each grass terrain and gives them back when it is disabled. Coverage uploads
  are queued and copied to the GPU in row strips (about 4 MB in each frame); a terrain grows blades after its first
  upload. `ProceduralGrassRenderer.InvalidateDetails` uploads a changed detail area again, and the stamp world calls
  it with the vegetation provider after it writes detail layers. Height changes refresh the tile culling height
  range. Tiles farther than the blade distance in 3D (high cameras) are skipped.
- Grass: the procedural grass showed no blades after a scene save. A save of a terrain data asset destroys its
  height and holes textures and makes new ones, and the renderer kept the old ones. It now reads the current
  textures for each generate pass.
- Surface: a natively drawn terrain (for example a backdrop) showed only its first layer after a scene save. The save
  clears the textures in the terrain splat property block. `LoogaTerrainSurfaceBinding` now checks the block in each
  editor update and binds the control maps again. `TerrainRenderer` also reads the control maps again when one of
  them is destroyed.
- Roads: structure decks. `LoogaRoadBridge.StructureDeck` marks a bridge whose deck belongs to another structure,
  such as a dam crest road. Roads join its end markers as at any bridge and the road slabs meet it, but the editor
  builds no parts, ground clearance or rock face openings for it, and the network tools do not move its markers.
  Road ends at its markers take the marker height (less the slab thickness). The network export marks it
  (`structure`), and ApplyNetwork skips edits to it.
- Roads: road ends meet bridge decks at the deck grade. The network tools wrote each end knot with the profile
  grade at the end, which follows the ground just before the deck and can be much steeper; with long handles the
  end curve then bulged meters above the road (River bridge 9 in Kubera World: 3.5 m). `WriteRoad` now gives an end
  within 3 m of a bridge marker the deck grade. New `AlignBridgeEnds` sets it on existing roads (end handles only,
  knot heights stay) and reports each end with its old grade and the largest offset of its end curve from a
  straight line.
- Roads: the road paves over every bridge deck, structure decks included, so a road and its bridges are one
  continuous surface. `LoogaRoadSlabs` builds a deck slab ("Deck <bridge>") from end marker to end marker at the
  marker heights (the road surface), as wide as the road, with road coordinates and no undulation. Its narrow
  rounded edge ends on the deck top inside the railings and stays asphalt, and its sides count as joined, so the
  ground far below shows no edge band on it. The deck ends have no edge: the road slabs meet them flush. The live
  update builds deck slabs too. Bridge decks, piers and abutments now lie one slab thickness (0.1 m) lower, under
  the paving.
- Roads: ApplyNetwork changes only the roads near the edits. The settle passes level every road of the network, so a
  round of edits also moved roads far from it, by up to several meters. Now a road whose ends did not move and lie
  more than 12 m from an edited road, a moved pad or an edited bridge gets its knots from before the edits back. The
  report lists these roads. New `RestoreBackup` puts the road splines, road paths and pads back from an ApplyNetwork
  backup (same editor session).
- Roads: `LoogaRoadNetwork.ApplyGround` works with static-batched slabs. In a player, static batching gives each slab
  the combined mesh of its batch, and the slab loop used its submesh count: "MaterialPropertyBlock index N is out of
  range" (700 errors per benchmark run) and no terrain ground on the edge bands. The loop now covers the slab's own
  submesh range (`subMeshStartIndex`, world space bounds) and stops at its material count.
- Surface: players rendered the terrain black. `LoogaTerrainSurface` creates its terrain materials at runtime, and a
  player build keeps only the shader feature variants (`LOOGA_SURFACE_PACK_QUALITY`, `LOOGA_SURFACE_TRIPLANAR`,
  `LOOGA_SURFACE_HEIGHT_BLEND`) that saved materials use, so the player drew the terrain with the wrong array
  packing. Each surface asset now keeps two hidden material sub-assets with its keywords (native and patch target).
  `LoogaTerrainSurfaceBuildVariants` writes them before each player build; the menu Tools/Looga/Terrain/Update
  Surface Build Variants writes them at once.
- GPU samplers for the terrain command buffers outside the render graph: `Looga Terrain Selection`, `Looga Terrain
  Shadow Group Selection` (was a string sample with CPU time only), `Looga Terrain Hi-Z`, `Looga Virtual Texture
  Feedback`, `Looga Virtual Texture Fill` and `Looga Detail Placement`. A profiler `Recorder` gives their GPU time.
- Geology: edit mode rock face builds no longer stop the main thread for GPU readbacks. Before a face builds, its
  ground source reads the region in the background (`RockFaceGroundSource.Prefetch`). `LoogaSnapshotGround` evaluates
  the snapshot tiles and reads them with `AsyncGPUReadback` completion callbacks, at most 4 tile sets at a time. The
  face waits in the queue until its tiles are kept, and later faces of the queue can start first. The terrain heights
  of `RockFaceBuilder.Prepare` also arrive in the background, and the build goes to its worker thread after them. The
  keep rules do not change: tiles are averaged only within one evaluation and kept per exact terrain set and modifier
  signature. `Rebuild` (play mode, context menu) still reads and waits. Kubera World, 81 faces, forced rebuild: the
  meshes are byte-identical; readback waits in the scheduler 1.9 s -> 0 ms; scheduler time 4.5 s -> 2.3 s; slowest
  update 1.1 s -> 0.18 s; 25 -> 108 editor frames in 4.5 s (was 5.2 s).
- Geology: rock faces and basalt arches keep their generated meshes over an editor script reload
  (`GeneratedReloadHandover`). The next domain takes them back when the Looga Terrain assembly did not change (module
  version check) and the face had no waiting or running build; otherwise it builds as before. Kubera World: a script
  reload no longer builds the 81 rock faces (about 9 s at 1-3 fps), and the 5 arches skip their 1.35 s enable build.
  Unity calls `OnValidate` when the scripts load; `LoogaRockFace.OnValidate` no longer queues a build then.
- Terrain selection batches bind their compute state again each frame. The bindings were set once on the compute
  shader instance. In Unity 6.7 that instance lost them after shader imports ("_BatchTiles is not set"), and the
  terrain drew wrong patches.
- Roads: road coordinates on the slabs. `LoogaRoadSlabs` writes a second UV set: on a road the distance across the
  center line and along the spline (meters), the half width and a random value of the spline; on a pad or a junction
  patch the distance inward from the rim (rim stretches where a road joins count as 10 m inside) and a random value.
  The build comparison also checks these values.
- Roads: road wear in `Looga/Road` (`_ROAD_WEAR`, LoogaRoadWear.hlsl), placed in the road coordinates: wheel tracks
  and an oil stripe in each lane, dust along the edges, block cracking in large irregular cells, fatigue (alligator) cracking in worn wheel tracks, tar seals, rectangular repairs with a seam and
  their own age, large brightness variation and an oxidized gray age. Fatigue cracks are wide V grooves around small
  blocks; each block tilts a little, some blocks sink, and a ravelled rim of light, rough aggregate surrounds each
  crack. The cracks change width, and the cells bend through two sizes of noise. Crack
  normals come from the crack shapes in road space, not from screen derivatives (those shimmered under temporal
  jitter). There are no long cracks along the road (center joint or edge cracks). A road side along a pad or a
  junction patch (vertex color red, written by `LoogaRoadSlabs`) gets no edge dust. The asphalt cells also turn by a
  random angle, and the two wear detail normal maps use the same anti-tiling cells (three rotated samples, blended
  by weights divided by their length, so the detail keeps its strength): a plainly tiled detail normal showed a grid
  of diagonal lines on large pads. Kubera World: no measurable cost in a road-filled 1600 x 900 view (13.3 ms with wear, 13.7 ms
  without, full frame).
- Roads: the edge band of `Looga/Road` can show the terrain beside the road (**Edge Band From Terrain**, keyword
  `_GROUND_TERRAIN`), with the same terrain ground blend as `Looga/Rock Face`. Each slab mesh has one submesh for each
  terrain tile below it, and `LoogaRoadNetwork.ApplyGround` gives each submesh the control maps and the terrain surface
  layers of its tile (property blocks, set again on enable and when a terrain surface changes). The band reads the
  terrain layers just outside the slab edge and samples their textures at the pixel, so they do not stretch; on the
  rounded lip the mesh normal gives the outward direction. A project shader can add its own ground term with
  `LOOGA_TERRAIN_GROUND_HOOK`.
- Roads: the slab edge meets the terrain without a line. The rounded lip ends 1 cm above the ground instead of
  cutting 4 cm into it, and `Looga/Road` moves the lip toward the camera by a part of the view distance
  (**Lip Depth Offset**, default 0.0005), so the terrain does not show through it far away. The green vertex color of
  the slabs gives the place on the lip (0 at the rim, 1 on the ground). The ground always covers the rim, the lip and
  a short strip inside the rim, and down the lip the band takes the terrain layers at the pixel, so they match the
  terrain where the lip meets it.
- Roads: cracks no longer flicker when the view moves. The noise key of a road (from the slab vertex seed) is now a
  whole number. The interpolated vertex value has a tiny error that changes from pixel to pixel, and the hashes made
  that error into random crack widths, so cracks switched between solid and broken with each sub-pixel camera move.
  The same key also drives the track wander, the oil stripe and the repairs. Also: the V groove slope is the mean
  over one pixel, and the crack antialiasing adds the change of the noise warp across a pixel
  (`RoadWarpedFootprint`). Kubera World, Scene view pose without antialiasing: the change of crack pixels under a
  one-pixel pan fell from 17.3 to 4.0 (bare asphalt 5.0).
- Roads: the cracks across the road are removed (with their tar seals and the properties Cross Crack Spacing, Cross
  Crack Chance and Sealed Crack Chance). Their zigzag paths aliased into shimmering dashed lines. The seal width and
  color now only draw the repair seams.
- Roads: cracks stay visible far away. Each crack is antialiased with the pixel size across it (a crack that runs
  away from the camera stays sharp), keeps at least one pixel of width with a gentler contrast falloff, and a crack
  network whose cells are smaller than a few pixels shows its mean coverage instead of lines (`RoadNetworkCoverage`).
  The ravelled rims fade out before they cancel the dark cracks. New property **Crack Distance Visibility** (0 = true
  mean, default 0.7). Kubera World: crack contrast at 8 to 30 m is about 2 to 4 times higher; sub-pixel stability
  test unchanged.
- Roads: rounded pad corners. `LoogaRoadPads.RoundCorners` (pad inspector button **Round Corners**) makes each cut
  corner of a pad spline a quarter circle (Bezier handles) between the same two knots, so the long sides stay. The pad
  stamps follow the curves. `LoogaRoadPads.SampleOutline` gives the outline from the curves (straight sides as their
  ends, corners every 1.5 m or less); the slabs and the road network tools use it, so the slab follows the corners.
  Kubera World: 32 pads rounded (8 m radius); one road end near a pad corner now joins the pad and its junction
  patch is gone (101 slabs).
- Roads: a natural slab edge. The edge band width changes along the edge with world-space noise (**Edge Band
  Narrowest** 0.05 m to **Edge Band Widest** 1.8 m from the rim); near its inner border the asphalt breaks into
  chunks of about **Edge Chunk Size** (0.35 m) with a narrow gap between them, and the ground fills the low parts of
  the asphalt first. Only the outer part of the lip is always ground. The lip width outside the outline changes
  along the edge (0.35 to 1.1 m, `LoogaRoadSlabs`). Before, every road and pad had an even outline of ground.
- Terrain ground blocks: a terrain can replace its splat map textures and destroy the old ones (seen after a scene
  save). `LoogaTerrainGroundBlock.HasControlMaps` finds such a block, and `LoogaRoadNetwork` and `LoogaRockFace` make
  their blocks again. Before, the road edge band and the rock face ground went dark until the next rebuild.
- Surface: the terrain ground blend of the rock faces moved to shared files (LoogaTerrainGroundInput.hlsl,
  LoogaTerrainGround.hlsl: `LoogaSampleTerrainGround`, with an optional splat-map position apart from the texture
  position) and `LoogaTerrainGroundBlock` (property block of a terrain,
  terrain under a position). `LOOGA_ROCK_FACE_GROUND_HOOK` still works. Kubera World: rock faces render the same.
- Roads (new): the road system of Kubera World moved into the package, with Looga names. Runtime assembly
  `LoogaSoft.Terrain.Roads`: `LoogaRoadNetwork` (road classes, pad root, pad ground snapshot, bridge root and
  materials, slab material, shoulder and clear stamps, output folder), `LoogaRoadClass`, `LoogaRoad` (road class and
  follow-ground flag on each road spline path), `LoogaRoadBridge` (end markers) and `LoogaRoadPad` (road knot links
  that follow the pad). Editor assembly `LoogaSoft.Terrain.Roads.Editor`: `LoogaRoadSlabs` (asphalt slab meshes,
  shoulder and clear masks), `LoogaRoadLive` (live slab, mask and bridge updates after each settled terrain update),
  `LoogaRoadBridges` (deck parts, ground clearance, rock face openings), `LoogaRoadWidths`, `LoogaRoadPads` (auto
  level, road links), `LoogaRoadPadEditor` (with an `ExtraTools` hook for project buttons) and
  `LoogaRoadNetworkTools` (junction heights, pad contacts, cross slope, merging, widening, simplifying and the road
  planner edits; `WaterSource` supplies river and lake outlines for the export). Scene names, layer names and the
  Kubera stamp system are replaced by explicit network references. Kubera World: a full build in memory equals the
  scene before the move (102 slabs, 933,376 vertices, both masks, 11 bridges with 84 parts).
- Roads: new `Looga/Road` shader for the slabs (Looga Lite GBuffer pass, forward, shadow and depth passes). Asphalt
  projected from above with random-offset triangle cells and a height blend, two noise-driven wear layers and asphalt
  patches.
- Stamps: `LoogaNeighbourhoodStamp.MarkChanged(Rect changedRegion)` (protected) reports a change of the result inside a
  world region only; the editor updater then updates that region, not the whole area. Kubera World: a live channel
  compares each new pass with the previous one and reports the area around the changed samples; a 2 m knot move on
  a path cut updates 354 x 313 m (heights changed in 54 x 94 m), and its undo restores the heights bit-exactly.
- Shaders: no compiler warnings in the package shaders. Height stamps: `pow` of a texture sample uses `max(x, 0)`.
  Texture stamp raster and the rock face ground blend: single returns and an unrolled inner loop. Basalt column cracks:
  one mip level from the screen derivatives before the parallax loop (the loop sampled with undefined derivatives).
- Geology: in edit mode a `LoogaRockFace` builds again when its spline changes and when its ground source reports new
  heights that touch its region. New `RockFaceGroundSource.HeightsChanged` event (`RaiseHeightsChanged` for sources).
  `LoogaSnapshotGround` (now `[ExecuteAlways]`) raises it for each height write of the stamp world of its snapshot. The
  build waits for the ground as before, so a request during a terrain update builds on the new heights. Before, a rock
  face kept its old shape after cliff or stamp edits until a manual rebuild. A face without a ground source still
  follows only its spline. The test region is the written region of the stamp world. Kubera World: with the local
  cliff updates below, a 3 m knot move on a 2.4 km cliff rebuilds 7 of 74 faces (before: 68).
- Stamps: `LoogaStampTextureSource.SetTextures(height, falloff, changedRegion)` sets both textures and reports the
  world area where they changed, as `SetFalloffTexture` does for the falloff. The editor updater then updates only
  that area. Kubera World: a live cliff compares each render with the previous one on the GPU and reports the changed
  texels; a 3 m knot move updates 412 x 286 m in place of the whole 3.2 km cliff stamp.
- Stamps: `OnValidate` of height, texture and placement modifiers marks a change only when the serialized settings
  changed (hash of the editor JSON). Unity also calls `OnValidate` after a script reload and after an undo of another
  object in the same hierarchy: an undo of a cliff spline knot made its child stamps update their whole area.
- No Kubera names in package code: two comments name the patched MicroVerse rules, and a test comment is neutral.
- Stamps: `LoogaWaterSource.Revision` is public, so editor tools can see when a water surface changed (as the
  water distance field does). Kubera World: the live biome masks rasterize the water surfaces again only after a
  change.
- Stamps: a `LoogaStampTextureSource` also supplies the two weight noise textures (Texture noise type) of an object
  or detail stamp: `WeightNoiseTexture`, `Weight2NoiseTexture`, `SetWeightNoiseTexture(second, texture, changedRegion)`.
  A stamp with an active source and no noise texture waits, as for a missing falloff texture. Kubera World: live biome
  masks feed the teal ring weights of 11 object stamps this way.
- Stamps: slope measure option (`SlopeMeasure`: Parity, Steepness) for the slope filters of object, detail and clear
  stamps. Parity is the default and keeps the MicroVerse value, which also grows with the terrain height. Steepness
  is the true ground angle in degrees, from central height differences in world units. The edge texels of a tile keep
  the full gradient of a plane. Clear stamps get a slope filter (`SlopeFilter`, `SlopeMeasure`, `SetSlopeMeasure`).
  Texture stamps keep the parity slope. Kubera World: the two steep ground clears use Steepness in place of two baked
  4096 x 4096 masks. Test: on a 10 deg ramp the clear switches between limits of 9.5 and 10.5 deg. Slope Reach
  (Steepness only, metres): the filter reads the steepest ground within the reach (eight directions at the full and
  half reach), so a clear also takes the ground next to a steep face. Near a tile edge the samples clamp to the tile.
- `BasaltArch` draws in chunks: new **Chunk Size** (default 400 m, 0 = one renderer). The mesh is split by triangle
  center into cubes (`BasaltArchMeshFactory.Split`); each chunk is an unsaved, not editable child renderer ("Basalt
  Arch Chunk n") with the settings of the arch renderer. The arch is a selection base, so a Scene view click on a chunk
  selects the arch (the Scene view does not pick objects hidden in the hierarchy). Chunk objects are reused, not
  destroyed, when the arch deactivates. One renderer covered a whole arch (several kilometers), so no view could cull
  any of it. Kubera World (5 arches, 136 chunks, same triangles): at the review pose, submitted triangles 9.63M ->
  4.62M and GPU 17.9 -> 17.6 ms; looking at the arches, 10.7M -> 6.3M triangles, GPU within noise. Same image.
  `GeneratedMesh` is null while the arch draws in chunks without a collider; `ChunkCount` is new.
- Fixed: after a script reload during a stamp update (also when play mode started during one), the stamp world stayed
  "updating" with nothing scheduled, its CPU heights were not synchronized, and the vegetation drew native foliage. The
  reload kept `LoogaStampWorld.IsUpdating` (a private backing field) but dropped the updater state and the lists of
  terrains that wait for CPU height and alphamap synchronization, so a later scene save could also write old alphamaps.
  `IsUpdating` is no longer kept over a reload, and the editor updater finishes interrupted work before a reload: pending
  and queued placements, height checks, and CPU height and alphamap synchronization (a reload during a whole-world
  update took about 4 s longer in Kubera World).
- Fixed: a large dark area in the Scene view after a scene save (seen in the north of Kubera World; it moved with the
  view). A save cleared the heightmap and hole textures of the terrain shadow materials, and the shadow group set them
  again only when the terrain's texture object changed, which a save does not do. The cached coarse level of Looga
  Shadows then drew the terrain without heights. The shadow group now sets the textures for each cull.
- Fixed: "Looga Stamps: placement readback failed" without a GPU failure. Unity disposes a done GPU readback request
  after its frame, and a later `hasError` read of it reports an error although the data arrived. A placement readback
  that completed while the updater did not look (for example, another system waited for its own readback) failed.
  Placement, detail and height change-check readbacks now record their result in the completion callback (the height
  check also copies its values there).
- Fixed: the editor was slow during each script compilation and for seconds after the reload. The vegetation editor
  preview stopped when compilation started, so all terrains drew native foliage until the new providers loaded. The
  preview now keeps the loaded vegetation while scripts compile. Before a reload it keeps native foliage hidden on the
  terrains that it draws (`TerrainVegetationProvider.EndEditorPreviewForReload`), and the next script domain gives
  each terrain back to its provider (`AdoptHiddenNativeFoliage`, through `SessionState`), which keeps native foliage
  hidden until its stream draws. Terrains without a preview provider get their native foliage back.
- Editor: Looga Activity sources for stamp updates (requested, writing heights and textures, placing terrain n of m,
  reading back placements, CPU height synchronization with the terrains left), rock face builds, terrain tiles that
  initialize or draw natively, and vegetation that loads cells or fell back to native foliage (with the reason).
- Stamps: placement updates without waits are spread over editor ticks. `StartPlacementRegion` places at most four
  terrains, and each `CompletePendingPlacements` call places the next four before the result is written. The editor
  updater starts placements after the GPU checks of the last height write (`LoogaStampWorld.HeightChecksPending`, at
  most 2 s). Kubera whole-world update: longest placement frame 1.55 s -> 0.22 s, the 0.63 s wait for queued height
  work is gone, no "Ran out of Graphics Ring Buffer space" in four runs (one in two before). Results are identical
  (stored placements equal a full evaluation). Height and texture stages stay in one tick: their seam averaging and
  edge copy depend on the evaluated tile set.
- Fixed: GPU buffer leak on each domain reload ("Leak Detected: Persistent allocates 37 individual allocations"). The
  surface binding's OnDisable released its override after the renderer's OnDisable, and `RefreshCompatibility`
  initialized the renderer again (isActiveAndEnabled stays true during a reload). It now initializes only renderers in
  the active list.
- Fixed: "Editor preview cannot replace runtime observers" when play mode ended. The vegetation editor preview now skips
  providers of the play mode scene.
- Fixed: a terrain was invisible when its `TerrainRenderer` could not draw after a scene load. The scene saves the
  native heightmap as hidden while the renderer draws it; nothing restored it when an integration blocked the renderer
  (seen after leaving play mode: the MicroSplat bridge blocked the tiles before the Looga surface binding applied its
  surface). `RefreshCompatibility` now restores the captured native visibility while the renderer is not ready, except
  for blocks of an external renderer.
- New `TerrainRendererSettings` asset: shared patch count, error pixels, frustum culling, shadow casting, shadow error
  texels and surface batching. `TerrainRenderer` has a Settings reference (`Settings`, `SetSettings`); an assigned
  asset replaces the local values, and asset edits apply during the next camera render (a patch count change builds
  the resources again). The `TerrainRenderer` inspector shows the asset values in place of the local ones.
- Breaking: `LoogaWorldController` assigns terrain settings: **Terrain Settings** for every terrain plus **Terrain
  Groups** (`TerrainSettingsGroup`: root and settings; the first group whose root contains a terrain applies).
  `SetTerrainSettings`, `GetTerrainSettings`, `GetTerrainRenderers` and `ApplyTerrainSettings` are new (play mode
  applies them on enable). The controller fields Patch Count, Error Pixels and Cast Terrain Shadows and their
  properties are removed. `LoogaWorldSetupUtility` assigns the settings in place of those values.
- Breaking: terrain shadows come from one shadow BatchRendererGroup (`TerrainShadowGroup.cs`), as static shadow
  casters (`BatchFilterSettings.staticShadowCaster`), so Looga Shadows caches them in its coarse levels. Kubera World:
  terrain shadows cost 0.05-0.15 ms in the shadow pass at ground poses (were 0.3-1.0 ms), same image.
  - The culling callback selects light-space patches per culling split on the GPU (`SelectShadowSplit`): a node
    splits while its height error is larger than Shadow Error Texels (new setting, default 1) times the split's texel
    size. The camera is not part of the rule, so a Looga Shadows cache strip matches the cached texels. It writes one
    indirect draw command per split and tile with the split's visibility bit.
  - Texel sizes come from Looga Shadows (`InstanceShadowSplits.TryGetSplitTexelSizes`). Other shadow renderers use an
    estimate from the split sphere and the URP shadow resolution. It works with URP shadows, also for spot and point
    lights. It skips ignored culls and the splits that Looga Shadows draws from its cache.
  - Each shadow view gets a set of buffers sized to its split count (about 1.2 MB per split for 37 tiles). Sets are
    created before a camera render when a view needed one, and released after 600 unused camera renders.
  - Tiles call `InstanceShadowSplits.NotifyStaticCastersChanged` after height, hole, position and setting changes, so
    cached shadows follow stamp edits.
  - `TerrainBatchedShadows.shader` is now the DOTS instancing shader of the group: one tile per draw, the packed patch
    as the visible instance value, the tile's own heightmap, one LOD map per split, material values in
    `UnityPerMaterial` with a hidden Properties block (SRP Batcher).
  - New `TerrainRenderer` fields Shadow Error Texels and Shadow Shader (empty uses the package shader in the editor;
    player builds need the reference). `Configure` takes the shadow shader; `LoogaWorldSetupUtility` assigns it.
  - Removed: the shadow-only indirect draws and the combined surface and shadow draw of each tile, the adaptive shadow
    batches (`TerrainShadowBatch.cs`, `MergeShadowDraws`), and the light-aware shadow cull of `TerrainOcclusionCamera`
    (`CullShadowBatch`). `TerrainOcclusionCamera.Configure(depthShader, occlusionCulling)` and
    `LoogaWorldController.ConfigureFeatures` lost their shadow parameters; `CullTerrainShadows` is gone.
  - The runtime assembly references `Unity.RenderPipelines.Core.Runtime` and allows unsafe code (culling output).
- Preview: new terrain surface (phase 1, step 1), a replacement for MicroSplat. `LoogaTerrainSurface` (asset: layers in
  control-map order, per-layer settings, texture arrays) and `LoogaTerrainSurfaceBinding` (component: draws a native
  terrain with the shared material and binds the control maps through the splat property block). Shader library
  `Runtime/Shaders/Surface/LoogaTerrainSurface.hlsl` and the native target `Looga/Terrain/Surface` with the Looga
  GBuffer Lite contract. Top-4 control-map reader, normalized linear, height and hybrid blend, world UVs, per-layer
  UV, color and material settings, stochastic sampling, surface-gradient normals and nine debug views. The optional
  MicroSplat editor assembly has `MicroSplatSurfaceImporter` (context menu on a MicroSplat terrain).
- Preview: terrain surface texture array builder (phase 1, step 2). `LoogaTerrainSurfaceArrayBuilder` builds the
  arrays from the source maps of each layer (`LoogaTerrainSurface.SourceMaps`: maps, channels, roughness flag). It
  reads PNG, JPG and TGA files at full quality. Two layouts: Compact (DXT5 albedo and height, DXT5 normal, smoothness
  and occlusion) and Quality (BC7 albedo and height, BC5 normal, BC7 smoothness, occlusion and metallic; keyword
  `LOOGA_SURFACE_PACK_QUALITY`). The MicroSplat importer reads the source maps from the texture array config and no
  longer assigns the MicroSplat arrays; MicroSplat quality packing maps to the Quality layout on a new surface.
- Preview: automatic array builds. The surface records a content hash of the build inputs per layout
  (`LoogaTerrainSurface.Packing` is the requested layout, `ArrayLayout` the layout of the assigned arrays,
  `AutoRebuild` on by default). `LoogaTerrainSurfaceAutoBuild` checks surfaces after a surface, texture or terrain
  layer change, assigns up-to-date arrays and builds out-of-date arrays in the background (one layer per editor
  update, status bar progress, cancel). Every compressed slice is cached in `Library/LoogaTerrainSurface/Slices`, so a
  build packs only changed layers. The surface inspector shows the array state with Rebuild and Cancel.
  `SetTextureArrays` no longer takes a layout (the mask array selects it). `LoogaSoft.Terrain.Editor` references
  `LoogaSoft.Terrain.Stamps`, so builds wait while a stamp world updates.
- Preview: the arrays of a layout are sub-assets of one binary `LoogaTerrainSurfaceArrays` asset
  (`[PreferBinarySerialization]`): half the disk size of text arrays, save 1.8 s -> 0.5-0.6 s. New
  `LoogaTerrainSurface.Compression` (Fast / Normal / Best, default Normal) for the BC7 encoder; only BC7 slice cache
  keys include it.
- Fix: `LoogaTerrainSurfaceBinding` runtime state is `[NonSerialized]`. A script reload kept a queued-apply flag
  without its editor call, so bound terrains kept a destroyed material after the surface material was recreated.
  `LoogaTerrainSurface.OnEnable` raises `Changed`, so bindings also apply again after an asset reload.
- Preview: the array builder decodes PNG, JPG and TGA source files on worker threads with a bundled copy of
  StbImageSharp 2.30.16 (public domain, `Editor/ImageDecoding/StbImageSharp`, internal types through
  `STBSHARP_INTERNAL`) in the new editor assembly `LoogaSoft.Terrain.ImageDecoding.Editor`. PNG and TGA output is
  identical to the previous decoders (40 of 40 Kubera World sources); JPG can differ slightly, so the builder
  version is 2 and the slice cache is rebuilt once. The builder-owned TGA reader is removed.
- Preview: terrain surface anti-tiling (phase 1, step 3), each feature with its own switch in
  `LoogaTerrainSurface.AntiTiling`: detail noise, distance noise, up to three normal noises, noise height
  (procedural FBM), control UV noise (fades out near tile borders), and distance resample (albedo, normal or
  both; cross-fade, noise or constant fade; linear, overlay or lighter-colour blend). Layers scale each feature
  (`DetailNoiseStrength`, `NormalNoiseStrength`, `ResampleStrength` and others), for example a flat pavement
  layer. Noise sizes are world meters. The binding sets `_LoogaTerrainSize` per tile. The MicroSplat importer
  reads detail, distance and normal noises, noise height, control UV noise and distance resample, and converts
  the MicroSplat terrain-UV scales with the tile size.
- Preview: terrain surface triplanar (phase 1, step 4): `LoogaTerrainSurface.Triplanar` with plain and height-blended
  modes, a slope gate (top projection only on gentle slopes), optional face normals near sharp slope changes, and
  noises, stochastic sampling and distance resample on every projection. Triplanar is the shader keyword
  `LOOGA_SURFACE_TRIPLANAR` (the three-projection code made every pixel slower when it was only a uniform). The
  triplanar-weights debug view shows the X, top and Z projections. The MicroSplat importer reads `_TRIPLANAR`,
  the contrast and the height-blend mode.
- Terrain surface triplanar cost: the shader keyword `LOOGA_SURFACE_HEIGHT_BLEND` is on only for the height and
  hybrid blend modes and height-blended triplanar. Without it each layer samples albedo and material in one pass,
  and the triplanar projections run in a loop (one iteration on gentle slopes). Kubera World test tiles, Draw GBuffer
  with triplanar: 3.48 / 3.54 / 3.48 / 2.07 ms before, 3.33 / 3.24 / 3.16 / 1.70 ms now (MicroSplat 3.30 / 3.19 /
  3.09 / 1.60 ms). Without triplanar the surface is now 1-6 % faster than MicroSplat. The image is the same within
  the frame noise.
- Preview: terrain surface world colour map and ground cover (phase 1, step 5). New component
  `LoogaTerrainSurfaceGlobals` binds the world data as global shader properties: a world colour map (one texture over
  an area, or tiles: one array slice per cell and an index texture) and the ground cover maps (coverage: R closure,
  G bare-cast share; canopy: A own-colour share, RGB own colour times the share). Producers can replace the saved maps
  in memory (`SetWorldColorOverride`, `SetGroundCoverOverride`). New surface settings: `WorldColor` (Off, Multiply 2x,
  Overlay, Cross Fade; near and far strength; distance fade; maximum slope) and `GroundCover` (lighting rules only:
  canopy distances, far coverage mip, under-canopy shade and tint, canopy albedo, soil luminance, detail and relief,
  bare cast near and far, view-angle closure). Per-layer `WorldColorStrength` and `GroundCoverStrength` (property row
  7). Debug views World Color and Ground Cover. Shader include `LoogaTerrainSurfaceWorld.hlsl` for other shaders.
- Preview: `LoogaGroundCoverProducer` makes the ground cover maps from the terrain detail layers (closure from the
  covered ground of each detail cell, colour from the prototype texture mean times its tint, or a set colour) and
  binds them to a globals component. The editor updates the area of each Looga stamp placement update
  (`LoogaGroundCoverUpdater`). Terrains without detail prototypes do not widen the maps, and the texel size grows when
  the maps would pass the texture size limit.
- Fix: `LoogaTerrainSurfaceBinding` queues its re-apply after a surface change with a one-shot
  `EditorApplication.update` handler, not `delayCall`. A dropped `delayCall` (seen when the change came from an editor
  tool command, for example a surface re-import) left the terrain without a material and blocked later re-applies.
- Preview: terrain surface patch target (phase 1, step 7). New shader `Looga/Terrain/Surface (Patches)` draws the
  surface on the GPU-selected patches of the Looga `TerrainRenderer` (`TerrainGeometry.hlsl`, per-pixel heightmap
  normal, holes from the terrain data). Both targets share `LoogaTerrainSurfaceNativePasses.hlsl` (patch vertex path
  under `LOOGA_TERRAIN_PATCHES`). `LoogaTerrainSurface.PatchesMaterial` is the shared runtime material.
  `LoogaTerrainSurfaceBinding` gives it to a `TerrainRenderer` on the same terrain (`UseTerrainRenderer`, on by default)
  and binds the per-tile values through the renderer's property block; the native draw stays as the fallback.
- `TerrainRenderer.SetSurfaceOverride(owner, material)`: a component can supply the surface material at run time (not
  saved). `HasSurfaceOverride` tells other integrations to stand down: the MicroSplat bridge then neither blocks the
  renderer nor binds its values.
- Shared include `LoogaTerrainSurfaceLayers.hlsl`: the per-layer property rows, projection UVs, colour adjustment and
  array-layout decoding of the surface. The surface library and the rock faces use it.
- `LoogaRockFace` reads the terrain layers of a Looga terrain surface where the terrain below a chunk has a binding
  (`_GroundLayout` 1 compact, 2 quality, with `_GroundMaskArray`, `_GroundSurfaceUV`, `_GroundLuminance`); other
  chunks keep the MicroSplat layout of the material. The chunks follow `LoogaTerrainSurfaceBinding.SurfaceChanged`.
  `LoogaTerrainSurface.LayerProperties` exposes the per-layer property texture.
- `LoogaTerrainSurfaceBinding` default base map distance is 100000 m (was 20000), so large terrains such as a backdrop
  do not show the Unity base map. The surface shades every distance live, as MicroSplat does; it has no far-mode cache.
- Fix: `MicroSplatSurfaceImporter` reads the stochastic scale and contrast only when the MicroSplat shader has them.
- `TerrainRenderer` GPU fix: the surface patch target (`Looga/Terrain/Surface (Patches)`) clipped holes on every
  pixel. A shader with a clip loses early depth writes, so terrain hidden behind hills was fully shaded: Kubera World
  wide views cost 17-41 % more GBuffer time than MicroSplat. The clip is now in an `_ALPHATEST_ON` variant. The
  renderer checks the holes texture of its terrain on the GPU (`DetectHoles` kernel, asynchronous readback) on setup
  and after each hole change, and turns the keyword on only for terrains with holes. New `TerrainRenderer.HasHoles`.
  The batched shadow shader (`Hidden/Looga/Terrain/BatchedShadows`) uses the same `_ALPHATEST_ON` variant: a shadow
  batch turns it on only when one of its tiles has holes. `Looga/Terrain/Lit` and the occlusion depth shader still clip
  always.
- Terrain surface layer sync: when `LoogaStampWorld` changes the terrain layer lists, the bound surfaces follow
  (`LoogaTerrainSurfaceBinding.SyncSurfaceLayers`, `LoogaTerrainSurface.SyncLayers`). Layer settings stay with their
  terrain layer. Unused layers move to the end and keep their settings. The editor then rebuilds the arrays. A bound
  terrain draws with the old arrays while they rebuild (`ArraysCoverLayers`), so it does not lose its surface. The
  binding also checks the layers again one editor update after the layer list of its terrain changes. New
  `MatchesLayers` and `HasTextureArrays`.
- Rock face builds make less managed garbage. Kubera World: `RebuildAll` of 81 faces made 1.53 GB before, 30-40 MB
  now (the largest frame 295 MB, now 5-15 MB).
  - The spline points, point distances, columns, drop side votes, smoothed rims and feet, column height samples,
    profiles, arc lengths and output strips of a build are in a workspace that later builds use again (about 160 MB
    less). Each running build takes its own workspace, and a workspace grows to the largest rock face that used it.
    An edit mode build takes its workspace before `RockFaceBuilder.Prepare` and keeps it until `LoogaRockFace` made
    the meshes. `RockFaceInput.Points` and the `RockFaceStrip` arrays can be longer than the new `PointCount` and
    `VertexCount`. The meshes are the same.
  - `LoogaRockFace` uses one array for the terrain of each strip column, and `LoogaStampWorld.IsSnapshotReady` (the
    ground check before each build) uses one modifier list again.
  - The terrain heights of a build come from a GPU readback of the terrain heightmap crop into native memory, not
    from `TerrainData.GetHeights` (about 450 MB less). The heights are the same 16-bit values (checked on all 38 Kubera
    World terrains). The float arithmetic of the sampler compiles differently, so 257 of 4.28 M vertices get a ground
    blend (vertex alpha) that is 1/255 different. A terrain that changes only its CPU heightmap
    (`SetHeightsDelayLOD` before `SyncHeightmap`) gives old heights until the synchronization. Without GPU readback
    support the sampler uses `GetHeights`.
  - `RockFaceChunks` uses its vertex and index arrays again for the next mesh (about 240 MB less).
  - `LoogaSnapshotGround` keeps its snapshot tiles in native memory, not in managed arrays (about 650 MB less). All
    tiles that the builds read stay until the build queue is empty, because a build on a worker thread can still read
    tiles that a height write dropped.
  - Before a script reload, `LoogaRockFace` waits for the running edit mode builds and then calls `EndBuilds`, so the
    ground sources release the heights that they keep.
  - New overload `StampHeights.ReadTile(int, NativeArray<ushort>)` reads a tile into a native array of the caller.
- `LoogaStampWorld` releases the four spline distance build textures when an update settles (256 MB of GPU memory
  at a 2048 field). The cached fields stay. The next field build creates and clears the textures again, so the
  results do not change.

## 0.1.0-preview.26 - 2026-10-14

- Breaking: Looga Terrain now depends on Looga Graphics Pro 2.0.0. Graphics Pro contains the former Looga Instancing
  package, so the `com.loogasoft.loogainstancing` dependency is removed. Assembly definitions reference
  `LoogaSoft.Instancing.Runtime` (was `LoogaSoft.Instancing`). Shader includes of the vegetation ground library use
  `Packages/com.loogasoft.loogagraphicspro/Instancing/ShaderLibrary/`.
- `LoogaStampWorldUpdater` compares all modifiers with their last state only after a change signal: a stamp revision
  (new internal counter `LoogaStampWorld.RevisionChanges`, also for texture source regions), a spline change, undo,
  an object change other than a scene dirty flag, a hierarchy change or a texture import. It also compares while a
  world updates, and at least every 0.5 s for changes without a signal. Kubera World: 1.0-1.5 ms in each idle editor
  tick before (730 modifiers).
- `LoogaSnapshotGround` keeps the snapshot tiles of each rock face region for the next builds of the queue, so the
  81 Kubera World builds after a script reload evaluate and read back 81 tiles, not 187 (5.7 s before). Only a region
  with the same terrains reuses tiles, because seams are averaged only between the tiles of one evaluation. The tiles
  stay while the modifiers before the snapshot and their revisions are the same (new internal
  `LoogaStampWorld.SnapshotSignature`) and no world update waits; a height write drops the tiles of its region, and
  the tiles are released when the queue is empty. New overload `StampHeights.ReadTile(int, ushort[])` reads a tile
  into a caller array.
- `LoogaRockFace` edit mode builds run on worker threads, four at a time: the main thread reads the spline, the ground
  and the terrain heights (`RockFaceBuilder.Prepare`), a worker builds the strips from copies of the settings and the
  openings, and the main thread makes the meshes. The old meshes stay until the new meshes are ready; a newer request
  or a disabled rock face discards a running build. `LoogaRockFace.UpdateBuilds` replaces `BuildNextPending`, and
  `PendingCount` includes the running builds. The meshes are the same as from sequential builds. Kubera World: 81
  rock faces in 4.9 s with editor updates in between, before 8.3 s of main-thread builds (about 15 s after a reload).
- `RockFaceGroundSource.IsReady` and `EndBuilds`: edit mode builds wait (at most 10 s) while a source is not ready,
  and sources release kept heights when no build waits or runs. `LoogaSnapshotGround` is not ready while a stamp
  before the snapshot waits for its textures or the world waits for an update (new internal
  `LoogaStampWorld.IsSnapshotReady`). After a script reload, the first builds read the snapshot before scripts set the
  cliff textures again and built rock on ground without the cliffs.
- `RockFaceSettings.Copy` returns a copy for a build on a worker thread.
- `RockFaceBuilder`: the openings of a strip are read with an index loop. A foreach over the IReadOnlyList allocated
  an enumerator for each vertex.
- `LoogaStampWorld.WritePlacementEntries` reuses its prototype lists, and the new internal
  `StampPlacements.AddConverted` converts the placements without the list of `Get`. A placement write made new lists
  for each stamp and prototype. The entries are the same.
- `TerrainVegetationProvider` reads the tree and detail prototypes only after a change signal: an `OnTerrainChanged`
  message, an enable, or (in the editor) a TerrainData dirty count change, for example an undo. TerrainData returned
  new prototype arrays on each read, about 1.4 KB for each terrain in each frame (53 KB per frame in Kubera World).
- `TerrainVegetationStream`: a cached cell comparer and `TryGetComponent` for observers. The sort allocated a delegate
  and a comparer on each update, and `GetComponent` made editor null objects (27 KB per frame in Kubera World).
- `LoogaStampWorldUpdater`: the idle tick reuses its collections and pools the water source area lists. Water filters
  return in a reused list. The tick reads the enabled worlds from the new internal `LoogaStampWorld.Enabled` list,
  not from `FindObjectsByType`. Kubera World: 52 KB per frame before.
- `TerrainVegetationPreview`: the editor tick makes no garbage between discoveries (no LINQ, no new observer list, an
  index loop over `SceneView.sceneViews`). The discovery once each second still allocates.
- `MicroSplatTerrainBridge`: the compatibility check that runs for each tile and camera makes no garbage now (was
  about 1 KB for each tile in each frame, 37 KB per frame in Kubera World under `Looga Terrain.SelectAndSubmit`).
  The bridge reads the shader tags and the source shader name again only after a material, shader or render
  pipeline change, or after a shader import. The new public method `MicroSplatTerrainBridge.MarkShadersChanged`
  starts this read. The editor calls it after each shader import. Keyword sets, module texture checks and
  diagnostic texts are computed again only after a change. The diagnostics are the same as before.

## 0.1.0-preview.25 - 2026-10-14

- Shader tags (`LoogaTerrainDepth`, the virtual-texture schema and normal space, and the MicroSplat compatibility
  tags) are read with `InstanceShaderInspection.GetTag` of Looga Instancing. `Material.GetTag` reads only the active
  subshader. A URP shader with a fallback has no active subshader before the render pipeline exists. The MicroSplat
  integration assembly now references `LoogaSoft.Instancing`.
- `LoogaRockFaceForward.hlsl`: the rock surface is now in the function `RockSurface`. `RockFragment` lights it as
  before. A project shader can call `RockSurface` from its own GBuffer pass.
- `LoogaRockFace` fix: a curved strip that is not a closed loop could build inside out. The builder compared the
  summed grid normal with the drop side of the first column only. Now each column compares its normals with its own
  drop side. Example: a pillar face with a gap in its rock, where one strip turns more than half way around.
- `LoogaRockFace` fix: a strip now ends where the Auto drop side changes between two columns. Before, one strip
  crossed the spline there. It folded into a thin fin, and the part on one side of the change was inside out.
- `LoogaRockFace` **Openings**: passages where the rock stays below a segment, for example under a bridge deck. The
  allowed height rises one meter per meter outside the half width, so the rock forms a notch. Set them with
  `SetOpenings` and request a build.
- `LoogaRockFace` face search: past the rim, the search ends where the ground rises again by more than the minimum
  face height. A cliff across a gap, for example the far side of an island in front of a wall, no longer becomes the
  face of the wall (rock sheets across the island and a bare wall). The rim is the cap length in front of the spline.
  The search starts from the highest rim ground, so a knob on the rim does not end the search and the wall below it
  keeps its rock.
- `LoogaSplinePath` **Square Ends** (default off): past the first and the last spline point, seen along the spline,
  the path changes nothing, so it ends on a straight line across the path instead of a round cap. For example, a
  ground stamp under a bridge deck ends exactly at the deck ends. Each spline of the container ends square. The
  zero-length last curve of an open spline is left out of a square-ended field, so the end test uses the last real
  curve.
- `LoogaSplinePath` **Fill Shoulder** (metres, default 0): where the path stands above the old ground, the level part
  extends this much beyond the width before the embankment starts, so a raised road keeps level ground beside it.
  The extra width grows over the first metre of fill (`FillRamp`), so cut and level stretches keep the plain width.
- `LoogaSplinePath` **Overlap Blend** (metres, default 0): where splines of the container overlap, such as roads at a
  junction, the height is the weighted mean of the spline heights instead of the height of the nearest (or later)
  spline. The weight of a spline falls by e for each blend distance from its centre line and fades out over its
  embankment, so each spline keeps its height on its centre line and the surface between them has no step. 0 gives
  the old field exactly.
- A failed placement readback no longer updates the placements again with waits. That blocking update over a large
  region stalled the editor for more than 20 minutes when GPU memory was short. The updater now releases the cached
  distance fields and idle placement atlases (`LoogaStampWorld.ReleaseCachedFields`), waits one second and starts the
  update again without a wait, at most three times. After that the changed area keeps its old placements and the
  console shows an error.
- Editor update watchdog: a background thread logs each `LoogaStampWorldUpdater` tick that runs longer than 20 s
  (then every 30 s) to `Library/LoogaStampWatchdog.log`, with the update step, the innermost `StampSections` section
  and the number of sections entered, so a stalled update names where it stalls.
- `LoogaRockFace` output for large worlds: the surface is cut into chunks along the cliff (Chunk Length, default
  48 m), each with three levels of detail in a `LODGroup` (every 2nd and 4th grid line, so seams match). Meshes use
  a compact vertex layout (position, half normal, byte color, 16-bit indices) and drop their CPU copy. In edit
  mode builds wait in a queue that `RockFaceBuildScheduler` (Editor) works off one face per editor update. Builds
  run the profile sampling and the surface pass in parallel (copied terrain heights, own thread-safe noise):
  0.1-0.2 s for a 150-200 m face instead of 1.4-3.2 s. Face detection takes the steep run with the largest drop.
- `LoogaTalusStamp` seeds only toes below faces that rise at least the Minimum Face Height (default 8 m) and that
  have ground within 6 m below the talus slope, so road cuts, river banks and benches on a face get no apron.
- New `LoogaTalusStamp` (Add Component > Looga > Stamps > Talus Stamp), a neighbourhood stamp: gentle ground at the
  toe of a face steeper than the face angle rises by the apron height and falls back to the terrain over the apron
  reach. The raised toe fills the face base at the talus angle. Only toes seed the apron: a toe has a higher steep
  sample next to it and gentle ground in front, so the rim and the benches on a face get none. Kernels in
  `LoogaTalus.compute`; the falloff and two water filters set the weight like `LoogaSmoothStamp`.
- `BasaltArch` Column Bundle: new **Weathering** settings (`BasaltArchBundleSettings.Weathered`, off by default).
  Each column becomes stacked drums with their own small shift, twist and radius, worn grooves at the joints (crack
  channel set), broken corners, scooped faces and tilted, rough free ends. Normals follow the surface along the column.
  The weathered mesh skips tangents (the basalt shaders do not read them); bundle vertex colors are now 8-bit. Test
  `CreateBundle_WeatheredColumnsAreDeterministicFiniteAndFaceOutward`.
- New `RockFaceGroundSource` (Geology) and `LoogaSnapshotGround` (Stamps): a `LoogaRockFace` can build on the
  heights at a `LoogaHeightSnapshot`, for example before a talus stamp, so the talus buries the rock base.
  `Looga/Rock Face` has a terrain ground layer (**Blend Into Terrain Ground**, keyword `_GROUND_TERRAIN`): vertex
  alpha (height above the final terrain, with noise) blends the rock into the terrain ground itself. Each rock chunk
  gets the splat control maps of the terrain tile below it (chunks split at tile changes), and the material takes the
  terrain layer arrays, the per-layer property table and the triplanar settings of a MicroSplat terrain material. A
  project shader can wrap `LoogaRockFaceInput.hlsl`, `LoogaRockFaceForward.hlsl` and `LoogaRockFaceDepth.hlsl` and
  define `LOOGA_ROCK_FACE_GROUND_HOOK` to apply its own terrain ground colour.
- Preview: new `LoogaRockFace` (Add Component > Looga Terrain > Rock Face (Preview)). It reads the live terrain
  heights next to a cliff-top spline and finds the steep face. It builds a rock mesh over the face in cliff space
  (along the cliff and down the profile), so tall faces do not stretch. The mesh moves out from the terrain only.
  Strata (with faults that shift and tilt them, pinch and swell, and ledges that come and go), joints, spalls
  and erosion grooves give the detail. A cap behind the rim and a skirt past the foot go
  below the ground. The mesh is not saved with the scene and has no collider. Look test only: no LOD, streaming or
  rebuild on terrain changes yet.
  Shader `Looga/Rock Face` (forward only, URP lit): one rock texture, triplanar at two scales with a noise blend
  to hide tiling. The mesh vertex colors drive layer color, hard-layer brightness, cavity darkening and dust on
  faces that look up.
- `LoogaSmoothStamp` **Keep Underwater** (`SetKeepUnderwater`): inside the water edge of the first water filter
  area, ground below the water level never rises. The blur only lowers it. Before, a smoothing around a river blurred
  the shallow bed toward the higher banks and lifted it above the water, so the visible river got narrower (Kubera
  World: 3 m of a 9 m wide river). The water rim outside the edge is unchanged. Test
  `KeepUnderwaterKeepsTheBedNextToAHighBank`.
- New `LoogaSplineConnection` (Add Component > Looga > Stamps > Spline Connection): records how two splines meet -
  `Continues`, `Joins`, `FallsInto` or `Banks` - with `LoogaSplineAnchor` positions or ranges in meters along each
  spline (from its start or its end), never knot indices. Generators read `LoogaSplineConnection.On(spline)` and
  redo their work on `Changed`. The component holds data only; no stamp reads it yet.
- Water fields: a mesh boundary edge is no longer a water edge when another source of the area contains the point
  5 cm outside it. Two rivers that meet end to end (a river split into two splines) or a river mouth inside a lake
  had a shore line across the water, so water-filtered textures and clears painted a strip across the bed. Test
  `SourcesThatMeetEndToEndHaveNoEdgeBetweenThem`.
- `StampNoiseType.PreciseFbm` now also works for the smooth stamp height noise, the spline path embankment noise
  and the water filter band noise (texture, clear and placement water filters). Before, these noises read only
  `Fbm` and showed straight-edged blocks at high frequency far from the world origin. Existing `Fbm` results do not
  change. Falloff noise still supports only `Fbm`.
- `LoogaDetailStamp` displacement: the listed detail layers make room for the stamp. Each texel of a listed layer,
  as the earlier stamps painted it, is multiplied by 1 - displacement x the stamp coverage (placement pass 5). A
  displacement below 1 thins the other details without a gap; later stamps of a listed layer are not changed.
  `SetDisplacement`. Test `DisplacingStampLowersTheEarlierLayers`.
- `TerrainVegetationProvider` refreshes when a detail prototype setting changes (sizes, density, noise, colors, hole
  padding, alignToGround, jitter, coverage, render mode), not only its prefab or texture. Before, an edit of
  TerrainData detail sizes kept the resident instances and the GPU generator sizes until a domain reload.
  `DetailPrototype.Equals` ignores alignToGround, so `SameDetailSettings` compares the fields. Test
  `DetailSettingEditsAreChanges`.
- Object stamps: new prototype **Ground Fit** (0 to 1, default 0) and **Ground Fit Radius**. The object sinks by that
  share of the drop to the lowest ground on two rings under its base, before the sink range, so a wide base (roots,
  clusters, columns) meets the ground on the downhill side of a slope instead of floating. The object stays upright;
  slope alignment is separate. Radius 0 measures the base: the largest horizontal distance from the pivot of the
  lowest 5 % of the LOD 0 vertices, times the placement scale. A player build needs readable meshes for the automatic
  radius. Ring samples past the tile edge clamp to the edge. Test `GroundFitSinksWideBasesToTheLowestGround`.
- Vegetation streams: required cells over the GPU reservation (`VegetationStreamingBudget.ResidentGpuBytes`, with eight
  view buffers) stay missing, as cells over the instance limit do. Before, the stream threw "Required vegetation
  exceeds the GPU reservation" and the provider gave the terrain back to native detail rendering, which Looga Shadows
  draws in every shadow level (Kubera World: 600 ms editor frames over a dense meadow). A GPU cell now checks
  `InstanceRenderer.ProjectedGpuBytesAfterRanges` before it allocates, because the storage trim keeps a grown
  capacity. After a refused cell, no GPU cell loads until an eviction frees space or the budget changes. A
  budget-limited GPU-resident stream draws its loaded cells even before its first complete update (before: it could
  never become ready, so native details stayed on). The provider diagnostic names the GPU reservation. Test
  `RequiredCellsOverTheGpuReservationStayMissingWithoutLeaks`.
- Vegetation preview: a change of the preview cameras no longer reloads the vegetation. `BeginEditorPreview` on a
  running preview only replaces the observers; the stream keeps its resident cells and loads or evicts cells for the
  new cameras. Before, maximizing the Game view (which hides the Scene view camera) or restoring the layout released and
  rebuilt the vegetation of every terrain. In Kubera World all 37 terrains now keep drawing through a maximize.
- Looga Stamps: new placement weight noise type `StampNoiseType.PreciseFbm`. It is the FBM with an exact integer
  hash and the same variance. The MicroVerse FBM hash multiplies cell coordinates as floats. Beyond about 64 cells
  from the world origin it loses precision, and high frequencies then give straight-edged stripes and blocks (for
  example frequency 110 at 5 km gave hard-edged bare rectangles in detail stamps). `Fbm` is unchanged, so existing
  stamps keep their patterns.
- Looga Stamps: `LoogaStampWorld.UpdateSettled(Rect)` is raised by the editor updater once after the changes of an
  edit are complete (heights written and CPU-synchronized, textures and placements written), with the union of the
  updated areas. Generators that read CPU heights or details, such as meshes on the ground, update there.
- Looga Stamps: `LoogaStampTextureSource` supplies the falloff texture of texture, clear, object and detail stamps
  too, not only of height stamps. `SetFalloffTexture(texture, changedRegion)` updates only the changed world area
  instead of the whole stamp, for example one road of a mask over a large road network. A stamp with a source but no
  texture yet (no height texture, or no falloff texture for the Texture falloff shape) is left out of the evaluation;
  without a texture it would apply over its whole box.
- Vegetation preview: an undo or a scene save no longer rebuilds every vegetation stream. Before, each undo (of any
  object) refreshed all providers, and each save stopped the preview. Native foliage then drew and Looga Shadows
  redrew its cached levels while the streams loaded: about 10 s of 0.7-2 s editor frames in Kubera World. Now a
  provider refreshes when an undo restores its own fields (OnValidate) or changes its terrain data (detail patches
  reload while Looga draws the old ones). A save writes the native foliage visibility for the scene file and hides
  native foliage again after it (`TerrainVegetationProvider.RestoreNativeFoliage` / `ReclaimNativeFoliage`).
  Undo in Kubera World: one 121 ms frame, no stream reloads.
- Looga Stamps: the object stamps of a tile copy their candidates into three shared atlas textures, and the tile reads
  each atlas back once (3 readbacks for each tile, not 3 for each stamp). Each readback created a GPU staging buffer:
  a placement region update on a Kubera tile now creates 8 GPU resources instead of 104 (2.5-4.8 ms instead of
  13.6-15.7 ms of main thread). The evaluator keeps the atlases until their readbacks are done and reuses them (at
  most 64 MB idle). The placements are the same.
- Looga Stamps: when an asynchronous placement readback fails, the editor updater logs a warning and updates the
  started region again with waits. The full placement rebuild after a reload always waits for its readbacks (the
  asynchronous rebuild of all terrains looped on failed readbacks).
- Looga Stamps: the editor updater no longer waits for the GPU on the edit path. `UpdateRegionWithoutWait` copies
  the heights of the region at once; a GPU change check finds the changed samples outside it, and
  `CompletePendingHeights` copies them in a later tick (`HeightsSynchronized` is false until then).
  `StartPlacementRegion` starts a placement update whose readbacks complete in later ticks
  (`CompletePendingPlacements`, `PlacementsPending`, `CancelPendingPlacements`); synchronous placement calls first
  complete a pending update. Kubera pad move: longest editor freeze 0.57 s -> 0.16 s, totals unchanged or faster;
  terrain equal to a full evaluation after the tests.
- Looga Stamps: `LoogaStampWorld.BeforeChangeCapture` is raised by the editor updater at the start of each tick,
  before it compares the modifiers. A script that moves modifiers because of another edit (for example road knots
  that follow a pad) moves them there, so one update applies both edits. Exceptions of a handler are logged.
- Looga Stamps timing: change tests no longer wait for the GPU after each heightmap or alphamap write (the wait added
  GPU time to the measured edit). A one-texel readback is checked once per tick instead. Reports add visible times
  (heights, alphamaps, placements, grass loaded) and the changes that the editor updater captured
  (`LoogaStampWorldUpdater.ChangeCaptured`, internal diagnostics).
- Vegetation provider: a height write without a CPU synchronization no longer gives the terrain back to native
  vegetation. Looga keeps drawing the previous vegetation, and the changed detail patches load again after the
  synchronization. A height change rebuilds the complete stream only when the terrain has tree instances. Before, each
  stamp or sculpt edit drew native details on the edited tile for 2-5 s (200-880 ms editor frames near the camera).
  A stream without GPU-resident details still falls back to native while required cells reload.
- Looga Stamps: placement unpack finds the spawned texels of each readback with a Burst job; the main thread decodes
  only those (same math, same order). Detail layers expand from bytes to the values of `SetDetailLayer` with a Burst
  job. The assembly references Unity.Burst and Unity.Collections and allows unsafe code.
- Looga Stamps: a placement region update compares each detail layer with the layer that the terrain holds and writes
  only the changed area (`SetDetailLayer` with an offset); the vegetation provider refreshes only that area. Unchanged
  layers are not written. A full rebuild still writes every layer.
- Looga Stamps: `LoogaNeighbourhoodStamp` is the public base of height modifiers that read the heights around each
  sample at their place in the stamp order, also on the neighbour tiles. A project stamp implements `Apply` with a
  `NeighbourhoodTile` (target state, apron with the neighbour border rings, texel positions, height scale), usually
  with its own compute shader. `LoogaSmoothStamp` derives from it; the smooth pass is now `NeighbourhoodStampPass`.
  The stamp order splits at every neighbourhood stamp, and `GrowForSmoothing` grows a region by every neighbourhood
  reach. A stamp with `IsReady` false is left out of the evaluation and counts as inactive in the editor updater.
- Looga Stamps: `LoogaStampWorld.ReadSnapshot` evaluates the terrains of an area up to a `LoogaHeightSnapshot` and
  returns the heights; the terrains do not change. `StampHeights.TrySampleHeight` samples them at a world position.
- Looga Stamps: `LoogaStampTextureSource` supplies the height and falloff textures of the height stamp on the same
  object at evaluation time, for example render textures that an editor generator rebuilds when its inputs change.
  The textures are not saved. `SetTextures` marks the stamp as changed. A height stamp without a serialized texture
  and without a source texture is left out of the evaluation until the source has one.
- Looga Stamps: `LoogaHeightSnapshot` keeps a copy of the heights at its place in the stamp order. A later
  `LoogaSnapshotLimit` sets Min, Max or Override of the current height and the snapshot height plus an offset, with
  a falloff, inside its transform box. The evaluator copies a snapshot only for tiles that a limit reads, and frees the
  copies after each evaluation. Both mappings are supported.
- Looga Stamps: water sources and water fields. `LoogaWaterSource` (built-in `LoogaMeshWaterSource`) gives a water
  surface mesh. `LoogaWaterArea` lists sources and builds per-tile fields: exact signed distance to the mesh boundary
  edges and the highest water surface. The new falloff shape `WaterArea` uses the field like Spline Area. The editor
  updater compares the boundary edges of changed sources, so an edit updates only the region near the changed edges.
- Looga Stamps: water field y is now the water level at the nearest point of the nearest edge (inside water without
  an edge in reach: the surface), so "ground height - y" is the height above the water.
- Looga Stamps: `StampWaterFilter` on texture, clear, detail and object stamps. Weight = the larger of an edge band
  (distance to the water edge) and a low ground band (distance and height above the water), each a range filter
  with noise in metres. A filter that needs water in reach limits the stamp culling rectangle to the area.
  A tile with no water source in reach shares one 1 x 1 field. New world setting `_waterFieldBudget` (MB, default
  512).
- Looga Stamps: the spline distance field cache moved into `TileFieldCache`, which the water fields share.
  `InvalidateDistanceFields`, `ClearDistanceFields` and the statistics also cover water areas.
  `CachedWaterFieldBytes` and `WaterFieldStatistics` report the water field cache.
- Looga Stamps: `LoogaSmoothStamp` blurs the heights at its place in the stamp order with a separable Gaussian (sigma
  up to 21 m, reach 3 sigma). Limits: maximum change, strength, Fbm height noise, falloff and two water filters. The
  water rim option keeps the ground near water, and higher banks, above the water level plus a clearance. A smooth
  stamp splits the evaluation into segments. Each tile stores a border ring of its heights before the stamp (the
  samples within the reach of its edges, about 1 MB for a 2049 tile). A region update reads the rings of the
  neighbours and evaluates only the written tiles. The editor updater grows a height region by the reach near a
  smooth stamp (`LoogaStampWorld.GrowForSmoothing`). Both mappings are supported.
- Looga Stamps: a height write copies only the changed samples of each tile (one GPU pass finds their bounds), so the
  CPU height synchronization reads back only that area (a 390 x 575 area: 24 ms instead of 180 ms for a 2049 tile).
- Looga Stamps: profiler markers for the update sections (`StampSections`). The editor timing checks print the CPU
  time of each section, the editor tick gaps, and a comparison of the terrain heights with a full evaluation.
- Looga Stamps: faster spline distance fields with the same result. A spline that cannot reach the tile gets no
  pass; a path field uploads only the curves that can reach the tile and skips chunks of 32 segments that cannot
  hold the nearest point; path fields skip the crossing parity. Tile sweeps alternate their direction, so more
  fields stay in the cache. Kubera World full update 3.3 s -> 2.2-2.5 s.
- Removed the optional MicroVerse integration: `MicroVerseTerrainBridge`, `MicroVerseVegetationBridge`,
  `MicroVersePlacementBridge`, the MicroVerse world setup extension, the road shader DOTS workaround, the
  MicroVerse-to-Looga-Stamps converter and parity tools, and their tests. `LoogaWorldController` no longer has the
  Configure MicroVerse option or MicroVerse Object Outputs, and `ConfigureFeatures` has no `microVerse` argument.
  Looga Stamps generates terrain instead. Its MicroVerse Parity mapping mode stays.
- Data mode passes the group of an `IInstanceGroupSource` component on each object stamp, such as a Looga Graphics Pro
  Vegetation Grade Group, to the InstanceSet entries of the stamp. A changed group updates the entries at the next
  capture.
- The world setup report shows each message once. Equal messages of several terrains become one line with the count
  and the first terrain names, for example
  `INFO: 37 terrains: Uses native rendering. Unverified MicroSplat features: ... (Tile 0,0, ... and 34 more)`.
  `LoogaWorldSetupContext.AddInfo` and `AddWarning` take an optional subject object for this grouping.
- The report no longer lists an integration block twice. The core validation skips a `TerrainRenderer` diagnostic
  that comes from an integration block (`TerrainRenderer.HasIntegrationBlock`). The MicroSplat and MicroVerse setup
  extensions report their own blocks. Native rendering for unverified MicroSplat features is information, not a
  warning (`MicroSplatTerrainBridge.UnverifiedFeatures`).
- The Looga World Controller has three vegetation categories: Details, Trees and Objects. Details and Trees are
  terrain prototypes. Objects are the prefabs of InstanceSets and InstanceContainers, for example the MicroVerse
  object-stamp Data mode set. Their placement counts show at once. Each object source has the same override fields
  as a terrain source. The Owners list shows the sets and containers that place it.
- `VegetationKind.Object` and `VegetationSettings.ObjectDefaults` hold the Object policy. `GetDefaults(kind)` returns
  the defaults of any category. `VegetationSettings` implements the Looga Instancing `IInstanceQualitySource`, so
  sets and containers resolve the Object policy for each prototype. Before, an InstanceSet used one quality for all
  its prototypes, and source overrides did not reach it.
- `LoogaWorldController.SetVegetationSettings` and `ApplyVegetationSettings` also assign the shared settings to the
  InstanceSets and InstanceContainers of the scene. Null restores their local quality. `GetInstanceSets` and
  `GetInstanceContainers` find them. The inspector warns about object owners without the shared settings, for
  example a set that a tool created later, and can assign the settings to them.
- A new shared profile copies its Object defaults from the first InstanceSet of the scene. An existing profile gets
  default Object values: LOD bias 0 keeps the prefab LOD distances. Set the Object defaults before you assign it.

- Fix: when required vegetation cells did not fit the resident instance or cell limit, streaming threw. The provider
  then gave the whole terrain back to native rendering and stayed there until a full refresh. Native detail rendering
  of a dense terrain cost the editor hundreds of milliseconds each frame, also in each shadow view. Now the missing
  cells stay empty, loaded cells keep drawing, and the cells load when evictions free space.
  `TerrainVegetationStream.BudgetLimited` and the provider diagnostic report this state. GPU-resident detail cells
  check the limit before they allocate ranges, so a rejected cell no longer leaves queued pages or ranges.
- Fix: a cell outside every observer region kept its required state while the residency change limit delayed its
  eviction, so budget pressure could not release it. These cells are now not required.
- Detail cells are required by 3D observer distance, like the renderer culls details. Each detail patch reads its
  height range once. An observer high above the terrain no longer loads the details below it.
- Fix: Data mode kept drawing the entries of a data stamp whose GameObject or component was switched off. MicroVerse
  hides the output holder of such a stamp, but a fed stamp has no holder objects. While MicroVerse is active, the set
  now drops the entries of switched-off stamps. They return with the next MicroVerse update after the stamp is on again.
- `MicroVersePlacementBridge.ClearPlacements` takes the terrains that a MicroVerse update regenerates. The fed stamp
  keeps its saved placements on other terrains, so a bounded update no longer removes the objects of the rest of the
  world. A terrain owns the placements in its half-open XZ rectangle. Null means all terrains.
- Data mode skips set entries whose captured placements are unchanged (`InstanceSet.MatchesEntry`). Most MicroVerse
  updates run every object stamp again with the same result, so these captures no longer compile, upload or rebuild
  colliders. `MicroVerseObjectDataReport.UnchangedEntries` counts the skipped entries. Fed placements are sorted by
  position, because MicroVerse sends them in the order that its GPU readbacks complete.
- `MicroVersePlacementBridge` Data mode accepts fed stamps (`ConfigureData` argument `fedStamps`). A hook in the
  MicroVerse spawn path calls `ClearPlacements` and `AddPlacement` for these stamps, and the stamps spawn nothing.
  The bridge does not scan their outputs. The data set gets their placements when MicroVerse is idle and creates
  their colliders (`InstanceSet.SetEntry` with `colliders`). Repeated prototypes of a stamp share one entry.
  `DataStamps`, `FedStamps` and the report fields `FedStamps` and `RejectedPlacements` describe the state. Installed
  MicroVerse versions have no such hook, so a project that wants the feed must add one.
- Fix: a Data mode capture marked the scene dirty also when it changed nothing. The dirty mark raised a hierarchy
  change, and the editor refresh hook then requested the next capture. In the editor, the captures never stopped, and
  the bridge reported waiting most of the time. Only a capture that changes the set now marks the scene dirty.
- Data mode writes set entries for up to 250 ms in each `ProcessChanges` call. Before, it wrote one entry in each call,
  so a capture of 340 entries took about 340 editor frames.
- Fix: a MicroVerse data capture after a partial respawn removed the placements of every prefab of that stamp
  that MicroVerse did not respawn. This happened, for example, when an edited prototype prefab came back with
  native renderers. The capture now updates only prefabs with respawned roots, and a stamp keeps the entries of
  all prefabs that it still lists.
- Breaking: the **Looga/Terrain/Vegetation Lit** shader moved to Looga Graphics Pro, with its shader GUID and name.
  Texture detail prototypes (no prototype mesh) need Graphics Pro. Without it, Looga Terrain rejects those
  prototypes with an error that names Graphics Pro.

## 0.1.0-preview.24

- The editor vegetation preview also streams around enabled game cameras with a `VegetationInterest`. Before, it
  streamed only around Scene view cameras, so the Game view in Edit mode showed no grass away from the Scene view
  camera. After a script reload, a hidden Scene view kept its camera at the world origin, and the grass disappeared
  from the Game view until the Scene view drew again.
- The Scene view vegetation preview restores native rendering only on the selected terrains, and only while a
  terrain paint tool is active. Before, a selection of any terrain, also a terrain without a provider, stopped the
  preview on all terrains. Native foliage then drew on every terrain, and the preview streamed all terrains again
  after the selection changed.
- Add a Column Bundle construction to basalt arches. The arch becomes bent hexagonal columns that start at both feet.
  Columns near the bundle center reach the crown and join the columns from the other foot. Farther out, fewer
  columns reach the crown, so the arch becomes thinner toward the crown. Free column ends get the pillar-top
  shading. Solid Column stays the default, so existing arches do not change.
- `MicroVersePlacementBridge` captures object outputs after a scene load when MicroVerse is idle. It requires
  `IsHeightSyncd` only after it sees a MicroVerse update start.
- `MicroVerseVegetationBridge` resumes the providers when MicroVerse stops modifying and spawning. It no longer waits
  for `IsHeightSyncd`, which MicroVerse 1.7.0 leaves false after the first update of a session, so native vegetation
  stayed active after each scene load. The providers still wait for unsynchronized heightmap changes.
- Add Data mode to `MicroVersePlacementBridge` (`ConfigureData`). It copies MicroVerse object stamp outputs into one
  Looga Instancing `InstanceSet` and hides or strips the spawned render parts. Colliders stay. It requires the
  MicroVerse objects module and does not wait for `IsHeightSyncd`. Scene Objects mode stays the default.
  In a player build without MicroVerse, the set draws its saved placements and the bridge reports that it does not wait.
  After a stamp changes its prototypes, the next capture removes the set entries of the replaced prototypes.
  Before, the set drew the old and the new prototypes at the same positions.
- `MicroVersePlacementBridge` Data mode removes the set entries of stamps that are not in its data list, also of
  deleted stamps. Before, the set kept drawing the last placements of a deleted stamp.
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
