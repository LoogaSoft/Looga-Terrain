using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain
{
    public sealed partial class TerrainRenderer
    {
        private sealed class SelectionCommands : IDisposable
        {
            private readonly List<TerrainRenderer> _members = new();
            private readonly List<SelectionBatch> _batches = new();

            public void Execute(Camera camera)
            {
                bool rebuild = _members.Count != Prepared.Count;
                for (int index = 0; !rebuild && index < Prepared.Count; index++)
                {
                    rebuild = _members[index] != Prepared[index];
                }
                if (rebuild)
                {
                    Invalidate();
                    _members.AddRange(Prepared);
                    var groups = new Dictionary<(ComputeShader, int), List<TerrainRenderer>>();
                    foreach (TerrainRenderer renderer in _members)
                    {
                        var key = (renderer._selectionCompute, renderer._activePatchCount);
                        if (!groups.TryGetValue(key, out List<TerrainRenderer> group))
                        {
                            group = new List<TerrainRenderer>();
                            groups.Add(key, group);
                        }
                        group.Add(renderer);
                    }
                    foreach (List<TerrainRenderer> group in groups.Values)
                    {
                        // Limit each allocation while retaining one selection dispatch for up to 256 tiles.
                        for (int start = 0; start < group.Count; start += 256)
                        {
                            _batches.Add(new SelectionBatch(camera, group.GetRange(start, Math.Min(256, group.Count - start))));
                        }
                    }
                }
                foreach (SelectionBatch batch in _batches)
                {
                    batch.Execute();
                }
            }

            public void CullDepth(CommandBuffer commands, Texture depth, Matrix4x4 projection, int width, int height,
                int levels, float bias, bool measure, int sampleId)
            {
                foreach (SelectionBatch batch in _batches)
                {
                    batch.CullDepth(commands, depth, projection, width, height, levels, bias, measure, sampleId);
                }
            }

            public void CullShadows(CommandBuffer commands, Vector4[] planes, Vector4[] lights, int lightCount)
            {
                foreach (SelectionBatch batch in _batches)
                {
                    batch.CullShadows(commands, planes, lights, lightCount);
                }
            }

            public void SubmitShadowBatches(Shader shader, int minimumTiles)
            {
                foreach (SelectionBatch batch in _batches)
                {
                    batch.SubmitShadowBatches(shader, minimumTiles);
                }
            }

            public void SubmitSurfaceBatches()
            {
                foreach (SelectionBatch batch in _batches)
                {
                    batch.SubmitSurfaceBatches();
                }
            }

            public void ReleaseShadowBatches()
            {
                foreach (SelectionBatch batch in _batches)
                {
                    batch.ReleaseShadowBatches();
                }
            }

            public void Invalidate()
            {
                foreach (SelectionBatch batch in _batches)
                {
                    batch.Dispose();
                }
                _batches.Clear();
                _members.Clear();
            }

            public void Dispose()
            {
                Invalidate();
            }
        }

        private sealed partial class SelectionBatch : IDisposable
        {
            private struct TileParameters
            {
                public Vector4 Origin;
                public Vector4 Size;
                public Vector4 Quality;
            }

            private readonly Camera _camera;
            private readonly List<TerrainRenderer> _members;
            private readonly TileParameters[] _parameters;
            private readonly int[] _metadataVersions;
            private readonly int[] _selectionVersions;
            private readonly CameraBuffers[] _views;
            private readonly int _nodeCount;
            private readonly int _copyKernel;
            private ComputeShader _compute;
            private CommandBuffer _commands;
            private GraphicsBuffer _nodes;
            private GraphicsBuffer _tiles;
            private GraphicsBuffer _shadowCandidates;
            private GraphicsBuffer _shadowCounts;
            private GraphicsBuffer _occlusionStatistics;

            public GraphicsBuffer Visible { get; private set; }
            public GraphicsBuffer Shadows { get; private set; }
            public GraphicsBuffer LodMap { get; private set; }
            public GraphicsBuffer VisibleArguments { get; private set; }
            public GraphicsBuffer ShadowArguments { get; private set; }

            public SelectionBatch(Camera camera, List<TerrainRenderer> members)
            {
                _camera = camera;
                _members = members;
                int count = members.Count;
                int patchCount = members[0]._activePatchCount;
                int capacity = patchCount * patchCount;
                _nodeCount = (capacity * 4 - 1) / 3;
                _parameters = new TileParameters[count];
                _metadataVersions = new int[count];
                _selectionVersions = new int[count];
                _views = new CameraBuffers[count];
                Array.Fill(_metadataVersions, -1);
                Array.Fill(_selectionVersions, -1);
                try
                {
                    _compute = Instantiate(members[0]._selectionCompute);
                    _compute.hideFlags = HideFlags.HideAndDontSave;
                    _copyKernel = _compute.FindKernel("CopyBatchNodes");
                    int resetKernel = _compute.FindKernel("ResetBatchArguments");
                    int selectKernel = _compute.FindKernel("SelectBatch");
                    _nodes = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _nodeCount * count, 16);
                    _tiles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 48);
                    Visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity * count, 16);
                    Shadows = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, capacity * count, 16);
                    LodMap = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity * count, 4);
                    var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments;
                    VisibleArguments = new GraphicsBuffer(target, count, GraphicsBuffer.IndirectDrawIndexedArgs.size);
                    ShadowArguments = new GraphicsBuffer(target | GraphicsBuffer.Target.CopySource, count, GraphicsBuffer.IndirectDrawIndexedArgs.size);
                    _occlusionStatistics = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 8);
                    _compute.SetConstantBuffer(CameraParametersId, _cameraParameters, 0, 128);
                    _compute.SetInt(PatchCountId, patchCount);
                    _compute.SetInt(NodeCountId, _nodeCount);
                    _compute.SetInt(FixedEdgesId, 0);
                    _compute.SetInt("_BatchTileCount", count);
                    _compute.SetInt("_BatchIndexCount", (int)members[0]._mesh.GetIndexCount(0));
                    _compute.SetBuffer(_copyKernel, "_BatchNodeData", _nodes);
                    _compute.SetBuffer(selectKernel, "_BatchNodeData", _nodes);
                    _compute.SetBuffer(selectKernel, "_BatchTiles", _tiles);
                    _compute.SetBuffer(selectKernel, "_BatchVisible", Visible);
                    _compute.SetBuffer(selectKernel, "_BatchShadows", Shadows);
                    _compute.SetBuffer(selectKernel, "_BatchLod", LodMap);
                    _compute.SetBuffer(selectKernel, "_BatchVisibleArguments", VisibleArguments);
                    _compute.SetBuffer(selectKernel, "_BatchShadowArguments", ShadowArguments);
                    _compute.SetBuffer(resetKernel, "_BatchVisibleArguments", VisibleArguments);
                    _compute.SetBuffer(resetKernel, "_BatchShadowArguments", ShadowArguments);
                    _commands = new CommandBuffer { name = "Looga Terrain Batched Selection" };
                    _commands.DispatchCompute(_compute, resetKernel, (count + 63) / 64, 1, 1);
                    _commands.DispatchCompute(_compute, selectKernel, (patchCount + 7) / 8, (patchCount + 7) / 8, count);
                    for (int index = 0; index < count; index++)
                    {
                        _views[index] = new CameraBuffers(this, index, capacity);
                        members[index]._cameras[camera] = _views[index];
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Execute()
            {
                bool parametersChanged = false;
                for (int index = 0; index < _members.Count; index++)
                {
                    TerrainRenderer renderer = _members[index];
                    if (_metadataVersions[index] != renderer._metadataVersion)
                    {
                        // Copy metadata on the GPU only after source heights change.
                        _compute.SetBuffer(_copyKernel, NodeDataId, renderer._nodeData);
                        _compute.SetInt("_BatchNodeOffset", index * _nodeCount);
                        _compute.Dispatch(_copyKernel, (_nodeCount + 63) / 64, 1, 1);
                        _metadataVersions[index] = renderer._metadataVersion;
                    }
                    if (_selectionVersions[index] == renderer._selectionVersion) continue;
                    _parameters[index] = new TileParameters
                    {
                        Origin = renderer._origin,
                        Size = renderer._size,
                        Quality = new Vector4(renderer.EffectiveTargetPatchPixels, renderer.EffectiveErrorPixels,
                            renderer._useGeometricError ? 1 : 0, renderer._frustumCulling ? 1 : 0)
                    };
                    _selectionVersions[index] = renderer._selectionVersion;
                    parametersChanged = true;
                }
                if (parametersChanged)
                {
                    _tiles.SetData(_parameters);
                }
                foreach (CameraBuffers view in _views)
                {
                    view.OcclusionApplied = false;
                    view.SurfacesBatched = false;
                    view.ShadowsBatched = false;
                    view.SubmissionCount = 0;
                }
                Graphics.ExecuteCommandBuffer(_commands);
            }

            public void CullShadows(CommandBuffer commands, Vector4[] planes, Vector4[] lights, int lightCount)
            {
                _shadowCandidates ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination, Shadows.count, 16);
                _shadowCounts ??= new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopyDestination, ShadowArguments.count * 5, 4);
                commands.CopyBuffer(Shadows, _shadowCandidates);
                commands.CopyBuffer(ShadowArguments, _shadowCounts);
                int reset = _compute.FindKernel("ResetShadowArguments"), cull = _compute.FindKernel("CullShadowBatch");
                commands.SetComputeBufferParam(_compute, reset, "_BatchShadowArguments", ShadowArguments);
                commands.DispatchCompute(_compute, reset, (_members.Count + 63) / 64, 1, 1);
                commands.SetComputeBufferParam(_compute, cull, "_BatchShadowArguments", ShadowArguments);
                commands.SetComputeBufferParam(_compute, cull, "_BatchShadows", Shadows);
                commands.SetComputeBufferParam(_compute, cull, "_ShadowCandidates", _shadowCandidates);
                commands.SetComputeBufferParam(_compute, cull, "_ShadowCounts", _shadowCounts);
                commands.SetComputeBufferParam(_compute, cull, "_BatchNodeData", _nodes);
                commands.SetComputeBufferParam(_compute, cull, "_BatchTiles", _tiles);
                commands.SetComputeVectorArrayParam(_compute, "_ShadowPlanes", planes);
                commands.SetComputeVectorArrayParam(_compute, "_ShadowLights", lights);
                commands.SetComputeIntParam(_compute, "_ShadowLightCount", lightCount);
                commands.DispatchCompute(_compute, cull, (_members[0]._activePatchCount * _members[0]._activePatchCount + 63) / 64, _members.Count, 1);
                foreach (CameraBuffers view in _views)
                {
                    view.OcclusionApplied = true;
                }
            }

            public void CullDepth(CommandBuffer commands, Texture depth, Matrix4x4 projection, int width, int height,
                int levels, float bias, bool measure, int sampleId)
            {
                commands.SetComputeIntParam(_compute, "_ReversedDepth", SystemInfo.usesReversedZBuffer ? 1 : 0);
                int reset = _compute.FindKernel("ResetVisibleArguments");
                int cull = _compute.FindKernel("CullDepthBatch");
                commands.SetComputeBufferParam(_compute, reset, "_BatchVisibleArguments", VisibleArguments);
                commands.SetComputeBufferParam(_compute, reset, "_BatchShadowArguments", ShadowArguments);
                commands.SetComputeBufferParam(_compute, reset, "_BatchOcclusionStatistics", _occlusionStatistics);
                commands.DispatchCompute(_compute, reset, (_members.Count + 63) / 64, 1, 1);
                commands.SetComputeBufferParam(_compute, cull, "_BatchVisibleArguments", VisibleArguments);
                commands.SetComputeBufferParam(_compute, cull, "_BatchShadowArguments", ShadowArguments);
                commands.SetComputeBufferParam(_compute, cull, "_BatchNodeData", _nodes);
                commands.SetComputeBufferParam(_compute, cull, "_BatchTiles", _tiles);
                commands.SetComputeBufferParam(_compute, cull, "_BatchVisible", Visible);
                commands.SetComputeBufferParam(_compute, cull, "_BatchShadows", Shadows);
                commands.SetComputeBufferParam(_compute, cull, "_BatchOcclusionStatistics", _occlusionStatistics);
                commands.SetComputeTextureParam(_compute, cull, "_DepthPyramid", depth);
                commands.SetComputeVectorParam(_compute, "_DepthSize", new Vector4(width, height, levels - 1, 0));
                commands.SetComputeMatrixParam(_compute, "_DepthViewProjection", projection);
                commands.SetComputeFloatParam(_compute, "_DepthBias", bias);
                commands.DispatchCompute(_compute, cull, (_members[0]._activePatchCount * _members[0]._activePatchCount + 63) / 64, _members.Count, 1);
                if (measure)
                {
                    Camera sampledCamera = _camera;
                    commands.RequestAsyncReadback(_occlusionStatistics, request =>
                    {
                        if (request.hasError || !sampledCamera)
                        {
                            return;
                        }
                        var values = request.GetData<uint>();
                        uint candidates = 0;
                        uint visible = 0;
                        for (int index = 0; index + 1 < values.Length; index += 2)
                        {
                            candidates += values[index];
                            visible += values[index + 1];
                        }
                        LoogaSoft.Instancing.InstanceVisibility.Report(sampledCamera,
                            sampleId, candidates, visible);
                    });
                }
                foreach (CameraBuffers view in _views)
                {
                    view.OcclusionApplied = true;
                }
            }

            public void Dispose()
            {
                for (int index = 0; index < _members.Count; index++)
                {
                    TerrainRenderer renderer = _members[index];
                    if (renderer && renderer._cameras.TryGetValue(_camera, out CameraBuffers view) && view == _views[index])
                    {
                        renderer._cameras.Remove(_camera);
                    }
                }
                _commands?.Dispose();
                _commands = null;
                ReleaseSurfaceBatches();
                ReleaseShadowBatches();
                _shadowCandidates?.Dispose();
                _shadowCounts?.Dispose();
                _occlusionStatistics?.Dispose();
                _shadowCandidates = _shadowCounts = null;
                _occlusionStatistics = null;
                _nodes?.Dispose();
                _tiles?.Dispose();
                Visible?.Dispose();
                Shadows?.Dispose();
                LodMap?.Dispose();
                VisibleArguments?.Dispose();
                ShadowArguments?.Dispose();
                _nodes = null;
                _tiles = null;
                Visible = Shadows = LodMap = VisibleArguments = ShadowArguments = null;
                DestroyResource(_compute);
                _compute = null;
            }
        }
    }
}
