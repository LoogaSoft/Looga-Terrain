# World vegetation controls

Select the Looga World object to edit its Vegetation section. Assign a Vegetation Settings asset to share separate detail and tree defaults across all terrain providers in that scene. A null asset keeps each provider's local settings.

View distance and LOD bias use zero to inherit the original behavior. View fade is a band before the view limit. Tagged shaders can fade pixels; other shaders use stable whole-instance thinning. Density and minimum pixel size reductions require decorative sources without gameplay colliders. Shadow range is capped by the provider and render pipeline limits. Shadow mode can inherit the source renderer or select On, Off, or Two Sided.

The Toolkit **Details** and **Trees** foldouts show square preview tiles named for each prefab or texture source. Click a tile to highlight it and open its boxed quality overrides and usage inside the same foldout. Each category has a collapsed **Defaults** foldout. Check only the fields to override, or reset a source to inherit all defaults. Overrides survive prototype reordering and apply to every terrain slot that uses that source. Scene InstanceContainers remain a separate system.

Enable **Looga Scene view preview** in the World Controller to stream full authored vegetation quality around Scene view cameras without changing runtime observers or TerrainData. Native foliage stays visible while required cells load. Selecting a Terrain or TerrainData restores native authoring; saving, compilation, and Play transitions also release the temporary preview and restore native rendering.

Source counts are explicit, cached reads. Tree counts are source placements. Detail counts are paint units, which can differ from generated mesh instances. Loaded counts are resident placements. Visible counts are explicit camera samples in Play mode and require a GPU readback; they count each placement once across mesh parts and LOD transitions. Refresh the catalog after changing terrain prototypes.

Quality edits do not rewrite TerrainData or source materials. Policy changes update live renderers. Longer ranges can load additional streaming cells and require larger budgets. Streaming conservatively retains every type in a cell to the longest configured range for its category; camera selection still applies each source's range. Invalid runtime policies keep the previous renderer policy and display a provider diagnostic.

For material distance fade, use the shader tag `LoogaInstanceDistanceFade=Material` with float properties `_FadeStart` and `_FadeEnd`. The renderer owns per-population material clones. The shader must keep camera fade independent of its ShadowCaster pass so camera range does not truncate shadow policy.
