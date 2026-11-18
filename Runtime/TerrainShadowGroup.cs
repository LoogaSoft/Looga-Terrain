using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LoogaSoft.Terrain
{
    public sealed partial class TerrainRenderer
    {
        private const string DefaultShadowShaderPath =
            "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/TerrainBatchedShadows.shader";
        private static readonly ProfilerMarker ShadowGroupMarker = new(ProfilerCategory.Render, "Looga Terrain.ShadowGroupCull");
        private static ShadowGroup _shadowGroup;
        private static Camera _shadowGroupCamera;
#if UNITY_EDITOR
        private static Shader _defaultShadowShader;
#endif

        private Shader ShadowShader
        {
            get
            {
#if UNITY_EDITOR
                if (!_shadowShader && !_defaultShadowShader)
                {
                    _defaultShadowShader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(DefaultShadowShaderPath);
                }
                return _shadowShader ? _shadowShader : _defaultShadowShader;
#else
                return _shadowShader;
#endif
            }
        }

        // True when the shadow BatchRendererGroup draws the tile's shadows.
        private bool UsesShadowGroup()
        {
            Shader shader = ShadowShader;
            return CastShadows && _shadowGroup != null && shader && shader.isSupported;
        }

        // Keeps the shadow BatchRendererGroup while an active tile casts shadows.
        private static void RefreshShadowGroup()
        {
            bool used = false;
            foreach (TerrainRenderer renderer in Active)
            {
                used |= renderer.CastShadows;
            }
            if (used && _shadowGroup == null)
            {
                _shadowGroup = new ShadowGroup();
            }
            else if (!used && _shadowGroup != null)
            {
                _shadowGroup.Dispose();
                _shadowGroup = null;
            }
        }

        // Shadow renderers that cache static casters keep the old terrain until this call.
        private static void NotifyShadowCastersChanged()
        {
            InstanceShadowSplits.NotifyStaticCastersChanged();
        }

        // Region is a changed area of the tile in normalized texture coordinates. The shadow patches next to it can
        // change their detail, so the area grows by one patch on each side.
        private void NotifyShadowCastersChanged(Rect region)
        {
            if (!IsReady || _sourceData != Source.terrainData)
            {
                NotifyShadowCastersChanged();
                return;
            }
            float margin = 1f / Mathf.Max(1, _activePatchCount);
            var bounds = new Bounds();
            bounds.SetMinMax(
                new Vector3(_origin.x + (region.xMin - margin) * _size.x, _origin.y - 1f,
                    _origin.z + (region.yMin - margin) * _size.z),
                new Vector3(_origin.x + (region.xMax + margin) * _size.x, _origin.y + _size.y + 1f,
                    _origin.z + (region.yMax + margin) * _size.z));
            InstanceShadowSplits.NotifyStaticCastersChanged(bounds);
        }

        /// <summary>
        /// One BatchRendererGroup for the shadows of all tiles. Its culling callback selects light-space patches per
        /// culling split on the GPU, and draws them as static shadow casters with indirect draw commands, one per
        /// split and tile.
        /// </summary>
        private sealed class ShadowGroup : IDisposable
        {
            // Looga Shadows uses up to eight splits: four levels and four cache strips.
            public const int MaximumSplits = 8;
            private static int _nextGeneration;
            private readonly Mesh _mesh;
            private readonly GraphicsBuffer _batchData;
            private bool _warnedSplits;

            public readonly int Generation;
            public readonly BatchRendererGroup Group;
            public readonly BatchID Batch;
            public readonly BatchMeshID Mesh;

            public ShadowGroup()
            {
                Generation = ++_nextGeneration;
                _mesh = PatchMesh.Create(PatchResolution);
                _mesh.hideFlags = HideFlags.HideAndDontSave;
                Group = new BatchRendererGroup(Cull, IntPtr.Zero);
                Group.SetEnabledViewTypes(new[] { BatchCullingViewType.Light });
                Group.SetGlobalBounds(new Bounds(Vector3.zero, Vector3.one * 100000f));
                Mesh = Group.RegisterMesh(_mesh);
                // The first 64 bytes stay zero, as BatchRendererGroup expects. Then two constant identity float3x4
                // matrices. The shader builds world positions itself and reads no per-instance data.
                var data = new float[16 + 24];
                for (int matrix = 0; matrix < 2; matrix++)
                {
                    int start = 16 + matrix * 12;
                    data[start] = 1f;
                    data[start + 4] = 1f;
                    data[start + 8] = 1f;
                }
                _batchData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, data.Length, 4);
                _batchData.SetData(data);
                var metadata = new NativeArray<MetadataValue>(2, Allocator.Temp);
                metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 16 * 4 };
                metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 28 * 4 };
                Batch = Group.AddBatch(metadata, _batchData.bufferHandle);
                metadata.Dispose();
            }

            public void Dispose()
            {
                Group.Dispose();
                _batchData.Dispose();
                DestroyResource(_mesh);
            }

            private unsafe JobHandle Cull(BatchRendererGroup group, BatchCullingContext context, BatchCullingOutput output,
                IntPtr userContext)
            {
                if (context.viewType != BatchCullingViewType.Light) return default;
                int light = context.viewID.GetInstanceID();
                // Looga Shadows draws the main light itself and ignores URP's cull of it.
                if (InstanceShadowSplits.IsCullIgnored(light)) return default;
                Camera camera = _shadowGroupCamera;
                if (!camera || !_cameraCommands.TryGetValue(camera, out SelectionCommands selection)) return default;
                int splitCount = context.cullingSplits.Length;
                if (splitCount == 0) return default;
                if (splitCount > MaximumSplits)
                {
                    if (!_warnedSplits)
                    {
                        _warnedSplits = true;
                        Debug.LogWarning("Looga Terrain shadows: a shadow view has more than " + MaximumSplits +
                            " splits. The terrain casts no shadows in the splits after them.");
                    }
                    splitCount = MaximumSplits;
                }

                Span<float> texels = stackalloc float[splitCount];
                int skippedSplits = 0;
                int persistentSplits = 0;
                // Looga Shadows publishes the texel size of each split. Other shadow renderers do not.
                bool published = splitCount == context.cullingSplits.Length
                    && context.projectionType == BatchCullingProjectionType.Orthographic
                    && InstanceShadowSplits.TryGetSplitTexelSizes(light, texels, out skippedSplits,
                        out persistentSplits);
                if (!published)
                {
                    skippedSplits = 0;
                    persistentSplits = 0;
                    float resolution = GetShadowTileResolution(context.projectionType);
                    for (int split = 0; split < splitCount; split++)
                    {
                        texels[split] = Mathf.Max(0.01f, 2f * context.cullingSplits[split].sphereRadius / resolution);
                    }
                }

                var commands = (BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr();
                *commands = default;
                using (ShadowGroupMarker.Auto())
                {
                    selection.CullShadowGroup(this, context, commands, texels, skippedSplits, persistentSplits);
                }
                return default;
            }

            // An estimate of the texels of one URP shadow tile. URP packs cascades two per row.
            private static float GetShadowTileResolution(BatchCullingProjectionType projection)
            {
                if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset asset) return 1024f;
                if (projection == BatchCullingProjectionType.Orthographic)
                {
                    return asset.mainLightShadowmapResolution / (asset.shadowCascadeCount > 1 ? 2f : 1f);
                }
                return asset.additionalLightsShadowmapResolution / 4f;
            }
        }

        private sealed partial class SelectionCommands
        {
            public unsafe void CullShadowGroup(ShadowGroup group, in BatchCullingContext context,
                BatchCullingOutputDrawCommands* output, ReadOnlySpan<float> texels, int skippedSplits,
                int persistentSplits)
            {
                int tiles = 0;
                foreach (SelectionBatch batch in _batches)
                {
                    tiles += batch.MemberCount;
                }
                if (tiles == 0) return;
                output->indirectDrawCommands = Allocate<BatchDrawCommandIndirect>(tiles * texels.Length);
                output->drawRanges = Allocate<BatchDrawRange>(tiles);
                foreach (SelectionBatch batch in _batches)
                {
                    batch.CullShadowGroup(group, context, output, texels, skippedSplits, persistentSplits);
                }
            }

            // Unity frees culling output memory after the culling job.
            private static unsafe T* Allocate<T>(int count) where T : unmanaged
            {
                return (T*)UnsafeUtility.Malloc(count * UnsafeUtility.SizeOf<T>(), UnsafeUtility.AlignOf<T>(), Allocator.TempJob);
            }
        }

        private sealed partial class SelectionBatch
        {
            // A view set that no shadow view used for this many camera renders is released. The first one stays.
            private const int ViewSetReleaseRenders = 600;
            private static readonly UnityEngine.Profiling.CustomSampler GroupSelectionSample =
                UnityEngine.Profiling.CustomSampler.Create("Looga Terrain Shadow Group Selection", true);
            private static readonly int DrawTilesId = Shader.PropertyToID("_DrawTiles");
            private static readonly int DrawNeighborsId = Shader.PropertyToID("_DrawNeighbors");
            private static readonly int DrawLodId = Shader.PropertyToID("_DrawLod");
            private static readonly int DrawPatchCountId = Shader.PropertyToID("_DrawPatchCount");
            private static readonly int DrawHeightResolutionId = Shader.PropertyToID("_DrawHeightResolution");
            private static readonly int ShadowTileId = Shader.PropertyToID("_ShadowTile");
            private static readonly int ShadowLevelStrideId = Shader.PropertyToID("_ShadowLevelStride");
            private static readonly int ShadowHeightId = Shader.PropertyToID("_ShadowHeight");
            private static readonly int ShadowHolesId = Shader.PropertyToID("_ShadowHoles");
            private static readonly int SplitLodId = Shader.PropertyToID("_SplitLod");
            private static readonly int SplitPatchesId = Shader.PropertyToID("_SplitPatches");
            private static readonly int SplitArgumentsId = Shader.PropertyToID("_SplitArguments");
            private static readonly int SplitArgumentCountId = Shader.PropertyToID("_SplitArgumentCount");
            private static readonly int SplitErrorsId = Shader.PropertyToID("_SplitErrors");
            private static readonly int SplitSlotId = Shader.PropertyToID("_SplitSlot");
            private static readonly int SplitTexelId = Shader.PropertyToID("_SplitTexel");
            private static readonly int SplitFollowCameraId = Shader.PropertyToID("_SplitFollowCamera");
            private static readonly int SplitPlanesId = Shader.PropertyToID("_SplitPlanes");
            private static readonly int SplitPlaneCountId = Shader.PropertyToID("_SplitPlaneCount");
            private static readonly int BatchNodeDataId = Shader.PropertyToID("_BatchNodeData");
            private static readonly int BatchTilesId = Shader.PropertyToID("_BatchTiles");
            private readonly List<ShadowViewSet> _viewSets = new();
            private readonly Vector4[] _splitPlaneValues = new Vector4[ShadowSplitData.maximumCullingPlaneCount];
            private GraphicsBuffer _tileErrors;
            private GraphicsBuffer _tileNeighbors;
            private float[] _tileErrorValues;
            private Vector4[] _tileNeighborValues;
            private int[] _tileSplitMasks;
            private int _resetKernel = -1;
            private int _selectKernel = -1;
            private int _groupGeneration;
            private Shader _groupShader;
            private int _groupRender;
            // Split counts of the views that found no free set in the last camera render.
            private readonly List<int> _missingViews = new();

            /// <summary>
            /// Buffers and materials for the patches of one shadow view. Each split has one LOD map and one patch list
            /// per tile. A view set holds the splits of one view, so its size follows the view: one split for a spot
            /// light, six for a point light, up to eight for the sun.
            /// </summary>
            private sealed class ShadowViewSet
            {
                public int Splits;
                public GraphicsBuffer Lod;
                public GraphicsBuffer Patches;
                public GraphicsBuffer Arguments;
                public Material[] Materials;
                public BatchMaterialID[] MaterialIds;
                public bool[] HoleVariants;
                public GraphicsBuffer BoundNeighbors;
                public bool InUse;
                public int LastUsedRender;
            }

            public int MemberCount => _members.Count;

            // Called before the camera renders, outside culling. New view sets are created here, so the materials of a
            // set are registered before a culling callback uses them.
            public void BeginShadowGroupFrame()
            {
                _groupRender++;
                ShadowGroup group = _shadowGroup;
                Shader shader = _members[0].ShadowShader;
                if (group == null || group.Generation != _groupGeneration || shader != _groupShader)
                {
                    ReleaseShadowGroup();
                }
                if (group == null || !shader || !shader.isSupported || !AnyMemberUsesShadowGroup()) return;
                _groupGeneration = group.Generation;
                _groupShader = shader;
                for (int index = _viewSets.Count - 1; index >= 1; index--)
                {
                    if (_groupRender - _viewSets[index].LastUsedRender <= ViewSetReleaseRenders) continue;
                    ReleaseViewSet(_viewSets[index]);
                    _viewSets.RemoveAt(index);
                }
                if (_viewSets.Count == 0)
                {
                    _viewSets.Add(CreateViewSet(group, ShadowGroup.MaximumSplits));
                }
                // Views that found no free set in the last render draw from the next render on.
                foreach (int splits in _missingViews)
                {
                    _viewSets.Add(CreateViewSet(group, splits));
                }
                _missingViews.Clear();
                foreach (ShadowViewSet set in _viewSets)
                {
                    set.InUse = false;
                }
            }

            // A shadow patch is never finer than the camera patch at its place, except in a persistent split. A finer
            // caster lies above a coarse visible patch on convex ground, so the ground shadows itself in round spots.
            // A persistent split, such as a Looga Shadows cache strip, keeps its casters when the camera moves.
            public unsafe void CullShadowGroup(ShadowGroup group, in BatchCullingContext context,
                BatchCullingOutputDrawCommands* output, ReadOnlySpan<float> texels, int skippedSplits,
                int persistentSplits)
            {
                if (group.Generation != _groupGeneration || !AnyViewUsesShadowGroup()) return;
                ShadowViewSet set = TakeViewSet(texels.Length);
                if (set == null)
                {
                    _missingViews.Add(texels.Length);
                    return;
                }
                int members = _members.Count;
                int patchCount = _members[0]._activePatchCount;
                int capacity = patchCount * patchCount;
                UploadTileShadowData();
                BindViewSet(set);

                // GPU selection: one LOD map and one patch list per split and tile.
                CommandBuffer commands = CommandBufferPool.Get("Looga Terrain Shadow Group");
                commands.BeginSample(GroupSelectionSample);
                commands.SetComputeIntParam(_compute, SplitArgumentCountId, texels.Length * members);
                commands.SetComputeBufferParam(_compute, _resetKernel, SplitArgumentsId, set.Arguments);
                commands.DispatchCompute(_compute, _resetKernel, (texels.Length * members + 63) / 64, 1, 1);
                commands.SetComputeBufferParam(_compute, _selectKernel, BatchNodeDataId, _nodes);
                commands.SetComputeBufferParam(_compute, _selectKernel, BatchTilesId, _tiles);
                commands.SetComputeBufferParam(_compute, _selectKernel, SplitLodId, set.Lod);
                commands.SetComputeBufferParam(_compute, _selectKernel, SplitPatchesId, set.Patches);
                commands.SetComputeBufferParam(_compute, _selectKernel, SplitArgumentsId, set.Arguments);
                commands.SetComputeBufferParam(_compute, _selectKernel, SplitErrorsId, _tileErrors);
                Array.Clear(_tileSplitMasks, 0, members);
                for (int split = 0; split < texels.Length; split++)
                {
                    if ((skippedSplits & (1 << split)) != 0) continue;
                    // The planes stay in _splitPlaneValues for the dispatch below.
                    ReadOnlySpan<Vector4> planes = GetSplitPlanes(context, split);
                    if (!MarkSplitTiles(planes, split)) continue;
                    commands.SetComputeIntParam(_compute, SplitSlotId, split);
                    commands.SetComputeIntParam(_compute, SplitPlaneCountId, planes.Length);
                    commands.SetComputeFloatParam(_compute, SplitTexelId, texels[split]);
                    commands.SetComputeIntParam(_compute, SplitFollowCameraId,
                        (persistentSplits & (1 << split)) != 0 ? 0 : 1);
                    commands.SetComputeVectorArrayParam(_compute, SplitPlanesId, _splitPlaneValues);
                    commands.DispatchCompute(_compute, _selectKernel, (patchCount + 7) / 8, (patchCount + 7) / 8, members);
                }
                commands.EndSample(GroupSelectionSample);
                // Culling callbacks run before the frame's shadow draws.
                Graphics.ExecuteCommandBuffer(commands);
                CommandBufferPool.Release(commands);

                // One indirect command per split and tile, one draw range per tile.
                for (int index = 0; index < members; index++)
                {
                    TerrainRenderer renderer = _members[index];
                    int begin = output->indirectDrawCommandCount;
                    for (int split = 0; split < texels.Length; split++)
                    {
                        if ((_tileSplitMasks[index] & (1 << split)) == 0) continue;
                        int slot = split * members + index;
                        output->indirectDrawCommands[output->indirectDrawCommandCount++] = new BatchDrawCommandIndirect
                        {
                            batchID = group.Batch,
                            meshID = group.Mesh,
                            materialID = set.MaterialIds[index],
                            flags = BatchDrawCommandFlags.None,
                            splitVisibilityMask = (ushort)(1 << split),
                            lightmapIndex = ushort.MaxValue,
                            topology = MeshTopology.Triangles,
                            visibleOffset = (uint)(slot * capacity),
                            visibleInstancesBufferHandle = set.Patches.bufferHandle,
                            visibleInstancesBufferWindowSizeBytes = 0,
                            indirectArgsBufferHandle = set.Arguments.bufferHandle,
                            indirectArgsBufferOffset = (uint)(slot * GraphicsBuffer.IndirectDrawIndexedArgs.size)
                        };
                    }
                    int count = output->indirectDrawCommandCount - begin;
                    if (count == 0) continue;
                    output->drawRanges[output->drawRangeCount++] = new BatchDrawRange
                    {
                        drawCommandsType = BatchDrawCommandType.Indirect,
                        drawCommandsBegin = (uint)begin,
                        drawCommandsCount = (uint)count,
                        filterSettings = new BatchFilterSettings
                        {
                            layer = (byte)renderer.gameObject.layer,
                            renderingLayerMask = renderer.Source.renderingLayerMask,
                            sceneCullingMask = ulong.MaxValue,
                            shadowCastingMode = ShadowCastingMode.On,
                            receiveShadows = false,
                            staticShadowCaster = true,
                            motionMode = MotionVectorGenerationMode.Camera
                        }
                    };
                }
            }

            public void ReleaseShadowGroup()
            {
                foreach (ShadowViewSet set in _viewSets)
                {
                    ReleaseViewSet(set);
                }
                _viewSets.Clear();
                _tileErrors?.Dispose();
                _tileNeighbors?.Dispose();
                _tileErrors = _tileNeighbors = null;
                _tileErrorValues = null;
                _tileNeighborValues = null;
                _tileSplitMasks = null;
                _groupGeneration = 0;
                _groupShader = null;
                _missingViews.Clear();
            }

            private bool AnyMemberUsesShadowGroup()
            {
                foreach (TerrainRenderer renderer in _members)
                {
                    if (renderer.UsesShadowGroup()) return true;
                }
                return false;
            }

            private bool AnyViewUsesShadowGroup()
            {
                foreach (CameraBuffers view in _views)
                {
                    if (view.ShadowsFromGroup) return true;
                }
                return false;
            }

            // The smallest free set that has room for the view's splits.
            private ShadowViewSet TakeViewSet(int splits)
            {
                ShadowViewSet chosen = null;
                foreach (ShadowViewSet set in _viewSets)
                {
                    if (set.InUse || set.Splits < splits || (chosen != null && chosen.Splits <= set.Splits)) continue;
                    chosen = set;
                }
                if (chosen != null)
                {
                    chosen.InUse = true;
                    chosen.LastUsedRender = _groupRender;
                }
                return chosen;
            }

            private ShadowViewSet CreateViewSet(ShadowGroup group, int splits)
            {
                int members = _members.Count;
                int patchCount = _members[0]._activePatchCount;
                int capacity = patchCount * patchCount;
                if (_resetKernel < 0)
                {
                    _resetKernel = _compute.FindKernel("ResetShadowSplitArguments");
                    _selectKernel = _compute.FindKernel("SelectShadowSplit");
                }
                var set = new ShadowViewSet
                {
                    Splits = splits,
                    Lod = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splits * members * capacity, 4),
                    // Raw, because the BatchRendererGroup reads it as its visible instance buffer.
                    Patches = new GraphicsBuffer(GraphicsBuffer.Target.Raw, splits * members * capacity, 4),
                    Arguments = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments,
                        splits * members * 5, 4),
                    Materials = new Material[members],
                    MaterialIds = new BatchMaterialID[members],
                    HoleVariants = new bool[members],
                    LastUsedRender = _groupRender
                };
                for (int index = 0; index < members; index++)
                {
                    var material = new Material(_groupShader) { hideFlags = HideFlags.HideAndDontSave };
                    material.SetBuffer(DrawTilesId, _tiles);
                    material.SetBuffer(DrawLodId, set.Lod);
                    material.SetInteger(DrawPatchCountId, patchCount);
                    material.SetInteger(ShadowLevelStrideId, members * capacity);
                    material.SetInteger(ShadowTileId, index);
                    set.Materials[index] = material;
                    set.MaterialIds[index] = group.Group.RegisterMaterial(material);
                }
                return set;
            }

            private void ReleaseViewSet(ShadowViewSet set)
            {
                // Material IDs of an older group went away with that group.
                if (_shadowGroup != null && _shadowGroup.Generation == _groupGeneration)
                {
                    foreach (BatchMaterialID id in set.MaterialIds)
                    {
                        _shadowGroup.Group.UnregisterMaterial(id);
                    }
                }
                foreach (Material material in set.Materials)
                {
                    DestroyResource(material);
                }
                set.Lod.Dispose();
                set.Patches.Dispose();
                set.Arguments.Dispose();
            }

            // Binds what can change between culls: the neighbor buffer, heightmaps and holes. The textures are set for
            // each cull: a scene save cleared them from the materials while the terrains kept the same textures, and the
            // cached level of Looga Shadows then drew the terrain without heights (a large dark area in the Scene view).
            private void BindViewSet(ShadowViewSet set)
            {
                bool neighbors = set.BoundNeighbors != _tileNeighbors;
                set.BoundNeighbors = _tileNeighbors;
                for (int index = 0; index < _members.Count; index++)
                {
                    TerrainRenderer renderer = _members[index];
                    TerrainData data = renderer.Source.terrainData;
                    Material material = set.Materials[index];
                    if (neighbors)
                    {
                        material.SetBuffer(DrawNeighborsId, _tileNeighbors);
                    }
                    material.SetInteger(DrawHeightResolutionId, data.heightmapResolution);
                    material.SetTexture(ShadowHeightId, data.heightmapTexture);
                    material.SetTexture(ShadowHolesId, data.holesTexture ? data.holesTexture : Texture2D.whiteTexture);
                    // The clip variant stops the fast depth-only path. Only a tile with holes uses it.
                    if (set.HoleVariants[index] != renderer._hasHoles)
                    {
                        set.HoleVariants[index] = renderer._hasHoles;
                        if (renderer._hasHoles)
                        {
                            material.EnableKeyword("_ALPHATEST_ON");
                        }
                        else
                        {
                            material.DisableKeyword("_ALPHATEST_ON");
                        }
                    }
                }
            }

            // Per-tile error limits and neighbor indices for the selection and the edge stitching.
            private void UploadTileShadowData()
            {
                int members = _members.Count;
                if (_tileErrors == null)
                {
                    _tileErrors = new GraphicsBuffer(GraphicsBuffer.Target.Structured, members, 4);
                    _tileNeighbors = new GraphicsBuffer(GraphicsBuffer.Target.Structured, members, 16);
                    _tileErrorValues = new float[members];
                    _tileNeighborValues = new Vector4[members];
                    _tileSplitMasks = new int[members];
                }
                for (int index = 0; index < members; index++)
                {
                    TerrainRenderer renderer = _members[index];
                    _tileErrorValues[index] = renderer.ShadowErrorTexels;
                    Vector4 neighbors = Vector4.one * -1;
                    for (int side = 0; side < 4; side++)
                    {
                        TerrainRenderer neighbor = renderer._neighbors[side];
                        neighbors[side] = neighbor ? _members.IndexOf(neighbor) : -1;
                    }
                    _tileNeighborValues[index] = neighbors;
                }
                _tileErrors.SetData(_tileErrorValues);
                _tileNeighbors.SetData(_tileNeighborValues);
            }

            private ReadOnlySpan<Vector4> GetSplitPlanes(in BatchCullingContext context, int split)
            {
                CullingSplit cullingSplit = context.cullingSplits[split];
                int count = Mathf.Min(cullingSplit.cullingPlaneCount, _splitPlaneValues.Length);
                for (int index = 0; index < count; index++)
                {
                    Plane plane = context.cullingPlanes[cullingSplit.cullingPlaneOffset + index];
                    _splitPlaneValues[index] = new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
                }
                return new ReadOnlySpan<Vector4>(_splitPlaneValues, 0, count);
            }

            // Marks the casting tiles inside the split. True when the split has one.
            private bool MarkSplitTiles(ReadOnlySpan<Vector4> planes, int split)
            {
                bool any = false;
                for (int index = 0; index < _members.Count; index++)
                {
                    if (!_views[index].ShadowsFromGroup || !Intersects(_members[index]._bounds, planes)) continue;
                    _tileSplitMasks[index] |= 1 << split;
                    any = true;
                }
                return any;
            }

            private static bool Intersects(Bounds bounds, ReadOnlySpan<Vector4> planes)
            {
                Vector3 center = bounds.center;
                Vector3 extent = bounds.extents;
                foreach (Vector4 plane in planes)
                {
                    float radius = Mathf.Abs(plane.x) * extent.x + Mathf.Abs(plane.y) * extent.y + Mathf.Abs(plane.z) * extent.z;
                    if (plane.x * center.x + plane.y * center.y + plane.z * center.z + plane.w < -radius) return false;
                }
                return true;
            }
        }
    }
}
