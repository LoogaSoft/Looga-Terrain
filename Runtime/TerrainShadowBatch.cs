using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace LoogaSoft.Terrain
{
    public sealed partial class TerrainRenderer
    {
        internal static void SubmitShadowBatches(Camera camera, Shader shader, int minimumTiles)
        {
            if (shader && shader.isSupported && _cameraCommands.TryGetValue(camera, out SelectionCommands selection))
            {
                selection.SubmitShadowBatches(shader, minimumTiles);
            }
        }

        internal static void ReleaseShadowBatches(Camera camera)
        {
            if (camera && _cameraCommands.TryGetValue(camera, out SelectionCommands selection))
            {
                selection.ReleaseShadowBatches();
            }
        }

        private sealed partial class SelectionBatch
        {
            private readonly List<ShadowDrawGroup> _shadowGroups = new();
            private Shader _shadowShader;
            private int _shadowMinimumTiles;

            public void SubmitShadowBatches(Shader shader, int minimumTiles)
            {
                minimumTiles = Mathf.Clamp(minimumTiles, 2, 16);
                if (_shadowShader != shader || _shadowMinimumTiles != minimumTiles)
                {
                    ReleaseShadowBatches();
                    _shadowShader = shader;
                    _shadowMinimumTiles = minimumTiles;
                    var groups = new Dictionary<(int, int, int, Vector2Int), List<int>>();
                    for (int index = 0; index < _members.Count; index++)
                    {
                        TerrainRenderer renderer = _members[index];
                        TerrainData data = renderer.Source.terrainData;
                        var cell = new Vector2Int(Mathf.FloorToInt(renderer._origin.x / (data.size.x * 4)),
                            Mathf.FloorToInt(renderer._origin.z / (data.size.z * 4)));
                        var key = (data.heightmapResolution, data.holesResolution, renderer.gameObject.layer, cell);
                        if (!groups.TryGetValue(key, out List<int> group))
                        {
                            group = new List<int>();
                            groups.Add(key, group);
                        }
                        group.Add(index);
                    }
                    foreach (List<int> group in groups.Values)
                    {
                        TerrainData source = _members[group[0]].Source.terrainData;
                        // Cap duplicate height and hole storage at 128 MiB per draw group.
                        long bytesPerTile = (long)source.heightmapResolution * source.heightmapResolution * 2
                            + (long)source.holesResolution * source.holesResolution;
                        if (group.Count >= minimumTiles && bytesPerTile * group.Count <= 128L * 1024 * 1024)
                        {
                            _shadowGroups.Add(new ShadowDrawGroup(this, group, shader));
                        }
                    }
                }
                foreach (ShadowDrawGroup group in _shadowGroups)
                {
                    group.Submit(minimumTiles);
                }
            }

            public void ReleaseShadowBatches()
            {
                foreach (ShadowDrawGroup group in _shadowGroups)
                {
                    group.Dispose();
                }
                _shadowGroups.Clear();
                _shadowShader = null;
                _shadowMinimumTiles = 0;
            }

            private sealed class ShadowDrawGroup : IDisposable
            {
                private static readonly uint[] EmptyArguments = { 1536, 0, 0, 0, 0 };
                private readonly int _layer;
                private readonly SelectionBatch _owner;
                private readonly List<int> _indices;
                private readonly Texture2DArray _heights;
                private readonly Texture2DArray _holes;
                private readonly GraphicsBuffer _slices;
                private readonly GraphicsBuffer _mergeTiles;
                private readonly GraphicsBuffer _neighbors;
                private readonly GraphicsBuffer _patches;
                private readonly GraphicsBuffer _arguments;
                private readonly Material _material;
                private readonly MaterialPropertyBlock _properties = new();
                private readonly int[] _sliceValues;
                private readonly Vector4[] _neighborValues;
                private readonly Texture[] _heightSources;
                private readonly Texture[] _holeSources;
                private readonly uint[] _heightVersions;
                private readonly uint[] _holeVersions;
                private readonly int _mergeKernel;

                public ShadowDrawGroup(SelectionBatch owner, List<int> indices, Shader shader)
                {
                    _owner = owner;
                    _indices = indices;
                    _layer = owner._members[indices[0]].gameObject.layer;
                    TerrainData first = owner._members[indices[0]].Source.terrainData;
                    try
                    {
                        _heights = CreateArray(first.heightmapTexture, indices.Count);
                        _holes = CreateArray(first.holesTexture, indices.Count);
                        _mergeTiles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, indices.Count, 4);
                        _mergeTiles.SetData(indices);
                        _slices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, owner._members.Count, 4);
                        _neighbors = new GraphicsBuffer(GraphicsBuffer.Target.Structured, owner._members.Count, 16);
                        _patches = new GraphicsBuffer(GraphicsBuffer.Target.Structured, owner._members[0]._activePatchCount * owner._members[0]._activePatchCount * indices.Count, 16);
                        _arguments = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments, 1, 20);
                        _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                        _sliceValues = new int[owner._members.Count];
                        _neighborValues = new Vector4[owner._members.Count];
                        _heightSources = new Texture[indices.Count];
                        _holeSources = new Texture[indices.Count];
                        _heightVersions = new uint[indices.Count];
                        _holeVersions = new uint[indices.Count];
                        _mergeKernel = owner._compute.FindKernel("MergeShadowDraws");
                        _properties.SetBuffer("_DrawTiles", owner._tiles);
                        _properties.SetBuffer("_DrawSlices", _slices);
                        _properties.SetBuffer("_DrawNeighbors", _neighbors);
                        _properties.SetBuffer("_DrawPatches", _patches);
                        _properties.SetBuffer("_DrawLod", owner.LodMap);
                        _properties.SetTexture("_DrawHeights", _heights);
                        _properties.SetTexture("_DrawHoles", _holes);
                        _properties.SetInt("_DrawPatchCount", owner._members[0]._activePatchCount);
                        _properties.SetInt("_DrawHeightResolution", first.heightmapResolution);
                    }
                    catch
                    {
                        Dispose();
                        throw;
                    }
                }

                private static Texture2DArray CreateArray(Texture source, int count)
                {
                    // Player terrain holes can use BC4. These arrays are copy destinations, not render targets.
                    var result = new Texture2DArray(source.width, source.height, count, source.graphicsFormat,
                        TextureCreationFlags.None)
                    {
                        name = "Looga batched shadow source", hideFlags = HideFlags.HideAndDontSave
                    };
                    result.Apply(false, true);
                    return result;
                }
                public void Submit(int minimumTiles)
                {
                    Array.Fill(_sliceValues, -1);
                    int enabled = 0;
                    Bounds bounds = default;
                    for (int slice = 0; slice < _indices.Count; slice++)
                    {
                        int index = _indices[slice];
                        TerrainRenderer renderer = _owner._members[index];
                        TerrainData data = renderer.Source.terrainData;
                        Texture height = data.heightmapTexture, holes = data.holesTexture;
                        if (!renderer.SupportsNativeDepth || renderer.gameObject.layer != _layer
                            || !renderer._castShadows || !height || !holes || height.width != _heights.width
                            || holes.width != _holes.width || height.graphicsFormat != _heights.graphicsFormat
                            || holes.graphicsFormat != _holes.graphicsFormat) continue;
                        bool compatible = true;
                        Vector4 neighbors = Vector4.one * -1;
                        for (int side = 0; side < 4; side++)
                        {
                            TerrainRenderer neighbor = renderer._neighbors[side];
                            int neighborIndex = neighbor ? _owner._members.IndexOf(neighbor) : -1;
                            compatible &= !neighbor || neighborIndex >= 0;
                            neighbors[side] = neighborIndex;
                        }
                        if (!compatible) continue;
                        _neighborValues[index] = neighbors;
                        _sliceValues[index] = slice;
                        if (_heightSources[slice] != height || _heightVersions[slice] != height.updateCount)
                        {
                            Graphics.CopyTexture(height, 0, 0, _heights, slice, 0);
                            _heightSources[slice] = height;
                            _heightVersions[slice] = height.updateCount;
                        }
                        if (_holeSources[slice] != holes || _holeVersions[slice] != holes.updateCount)
                        {
                            Graphics.CopyTexture(holes, 0, 0, _holes, slice, 0);
                            _holeSources[slice] = holes;
                            _holeVersions[slice] = holes.updateCount;
                        }
                        if (enabled++ == 0)
                        {
                            bounds = renderer._bounds;
                        }
                        else
                        {
                            bounds.Encapsulate(renderer._bounds);
                        }
                    }
                    if (enabled < minimumTiles) return;
                    _slices.SetData(_sliceValues);
                    _neighbors.SetData(_neighborValues);
                    _arguments.SetData(EmptyArguments);
                    ComputeShader compute = _owner._compute;
                    compute.SetBuffer(_mergeKernel, "_BatchShadows", _owner.Shadows);
                    compute.SetBuffer(_mergeKernel, "_BatchShadowArguments", _owner.ShadowArguments);
                    compute.SetBuffer(_mergeKernel, "_DrawSlices", _slices);
                    compute.SetBuffer(_mergeKernel, "_MergeTiles", _mergeTiles);
                    compute.SetBuffer(_mergeKernel, "_MergedShadows", _patches);
                    compute.SetBuffer(_mergeKernel, "_MergedArguments", _arguments);
                    compute.Dispatch(_mergeKernel, (_owner._members[0]._activePatchCount * _owner._members[0]._activePatchCount + 63) / 64, _indices.Count, 1);
                    var parameters = new RenderParams(_material)
                    {
                        camera = _owner._camera, layer = _layer,
                        worldBounds = bounds, matProps = _properties, shadowCastingMode = ShadowCastingMode.ShadowsOnly
                    };
                    Graphics.RenderMeshIndirect(parameters, _owner._members[0]._mesh, _arguments);
                    bool counted = false;
                    foreach (int index in _indices)
                    {
                        if (_sliceValues[index] < 0) continue;
                        _owner._views[index].ShadowsBatched = true;
                        _owner._views[index].SubmissionCount += counted ? 0 : 1;
                        counted = true;
                    }
                }

                public void Dispose()
                {
                    DestroyResource(_heights);
                    DestroyResource(_holes);
                    DestroyResource(_material);
                    _slices?.Dispose();
                    _mergeTiles?.Dispose();
                    _neighbors?.Dispose();
                    _patches?.Dispose();
                    _arguments?.Dispose();
                }
            }
        }
    }
}
