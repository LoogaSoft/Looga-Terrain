using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Runs texture modifiers on the GPU, one tile at a time, at the alphamap resolution of the tile. Each modifier is a
    /// full-screen pass from one pair of 8-bit layer and weight maps into another, last modifier first, like MicroVerse.
    /// The edge copy then makes shared tile edges equal, and the alphamap pass divides the four weights by their sum.
    /// </summary>
    internal sealed class TextureStampEvaluator : IDisposable
    {
        private const string RasterName = "LoogaTextureStampRaster";
        private const string ComputeName = "LoogaTextureStamps";
        // Eight alphamaps hold 32 terrain layers, the layer index range of the layer maps.
        private const int MaximumAlphamaps = 8;
        private const int StampPass = 0;
        private const int NormalPass = 1;
        private const int PathPass = 2;

        private static readonly int TileToStampId = Shader.PropertyToID("_TileToStamp");
        private static readonly int LayerMapId = Shader.PropertyToID("_LayerMap");
        private static readonly int WeightMapId = Shader.PropertyToID("_WeightMap");
        private static readonly int AlphamapSizeId = Shader.PropertyToID("_AlphamapSize");
        private static readonly int StampScaleId = Shader.PropertyToID("_StampScale");
        private static readonly int NoiseUvId = Shader.PropertyToID("_NoiseUv");
        private static readonly int LayerId = Shader.PropertyToID("_Layer");
        private static readonly int WeightId = Shader.PropertyToID("_Weight");
        private static readonly int ClampFalloffTextureId = Shader.PropertyToID("_ClampFalloffTexture");
        private static readonly int TerrainSizeXZId = Shader.PropertyToID("_TerrainSizeXZ");
        private static readonly int TileLayersId = Shader.PropertyToID("_TileLayers");
        private static readonly int TileWeightsId = Shader.PropertyToID("_TileWeights");
        private static readonly int NeighborLayersId = Shader.PropertyToID("_NeighborLayers");
        private static readonly int NeighborWeightsId = Shader.PropertyToID("_NeighborWeights");
        private static readonly int LayerRemapId = Shader.PropertyToID("_LayerRemap");
        private static readonly int SideId = Shader.PropertyToID("_Side");
        private static readonly int WidthId = Shader.PropertyToID("_Width");
        private static readonly int HeightId = Shader.PropertyToID("_Height");
        private static readonly int AlphamapCountId = Shader.PropertyToID("_AlphamapCount");
        private static readonly int NewAlphamapId = Shader.PropertyToID("_NewAlphamap");
        private static readonly int OldAlphamapId = Shader.PropertyToID("_OldAlphamap");
        private static readonly int AlphamapChangesId = Shader.PropertyToID("_AlphamapChanges");
        private static readonly int AlphamapCheckSizeId = Shader.PropertyToID("_AlphamapCheckSize");
        private static readonly int AlphamapCheckIndexId = Shader.PropertyToID("_AlphamapCheckIndex");
        private static readonly int AlphamapIndexId = Shader.PropertyToID("_AlphamapIndex");
        private static readonly int WeightNoiseTypeId = Shader.PropertyToID("_WeightNoiseType");
        private static readonly int WeightNoiseParamsId = Shader.PropertyToID("_WeightNoiseParams");
        private static readonly int UseHeightFilterId = Shader.PropertyToID("_UseHeightFilter");
        private static readonly int HeightFilterRangeId = Shader.PropertyToID("_HeightFilterRange");
        private static readonly int HeightFilterSmoothnessId = Shader.PropertyToID("_HeightFilterSmoothness");
        private static readonly int HeightFilterWeightId = Shader.PropertyToID("_HeightFilterWeight");
        private static readonly int HeightFilterNoiseTypeId = Shader.PropertyToID("_HeightFilterNoiseType");
        private static readonly int HeightFilterNoiseParamsId = Shader.PropertyToID("_HeightFilterNoiseParams");
        private static readonly int RealHeightId = Shader.PropertyToID("_RealHeight");
        private static readonly int HeightmapId = Shader.PropertyToID("_Heightmap");
        private static readonly int UseSlopeFilterId = Shader.PropertyToID("_UseSlopeFilter");
        private static readonly int SlopeFilterRangeId = Shader.PropertyToID("_SlopeFilterRange");
        private static readonly int SlopeFilterSmoothnessId = Shader.PropertyToID("_SlopeFilterSmoothness");
        private static readonly int SlopeFilterWeightId = Shader.PropertyToID("_SlopeFilterWeight");
        private static readonly int SlopeFilterNoiseTypeId = Shader.PropertyToID("_SlopeFilterNoiseType");
        private static readonly int SlopeFilterNoiseParamsId = Shader.PropertyToID("_SlopeFilterNoiseParams");
        private static readonly int NormalmapId = Shader.PropertyToID("_Normalmap");
        private static readonly int PathDistanceId = Shader.PropertyToID("_PathDistance");
        private static readonly int PathFieldSizeId = Shader.PropertyToID("_PathFieldSize");
        private static readonly int PathTextureWidthId = Shader.PropertyToID("_PathTextureWidth");
        private static readonly int PathTextureSmoothnessId = Shader.PropertyToID("_PathTextureSmoothness");
        private static readonly int PathTextureWeightId = Shader.PropertyToID("_PathTextureWeight");
        private static readonly int[] NeighborHeightmapIds =
        {
            Shader.PropertyToID("_HeightmapPX"), Shader.PropertyToID("_HeightmapPY"),
            Shader.PropertyToID("_HeightmapNX"), Shader.PropertyToID("_HeightmapNY")
        };
        private static readonly int[] HasNeighborIds =
        {
            Shader.PropertyToID("_HasPX"), Shader.PropertyToID("_HasPY"), Shader.PropertyToID("_HasNX"),
            Shader.PropertyToID("_HasNY")
        };
        private static readonly int[] AlphamapIds =
        {
            Shader.PropertyToID("_Alphamap0"), Shader.PropertyToID("_Alphamap1"), Shader.PropertyToID("_Alphamap2"),
            Shader.PropertyToID("_Alphamap3"), Shader.PropertyToID("_Alphamap4"), Shader.PropertyToID("_Alphamap5"),
            Shader.PropertyToID("_Alphamap6"), Shader.PropertyToID("_Alphamap7")
        };

        /// <summary>Layer and weight maps of one tile: the four strongest layers of each texel.</summary>
        internal sealed class TileLayers : IDisposable
        {
            internal UnityEngine.Terrain Terrain;
            internal RenderTexture Layers;
            internal RenderTexture Weights;

            public void Dispose()
            {
                RenderTexture.ReleaseTemporary(Layers);
                RenderTexture.ReleaseTemporary(Weights);
                Layers = null;
                Weights = null;
            }
        }

        private readonly Material _material;
        private readonly HeightStampEvaluator.MaterialTarget _target;
        private readonly ComputeShader _compute;
        private readonly int _copyEdgeKernel;
        private readonly int _writeKernel;
        private readonly int _writeOneKernel;
        private readonly int _checkKernel;
        private readonly SplineDistanceField _distanceFields;
        private readonly WaterDistanceField _waterFields;
        private readonly RenderBuffer[] _renderTargets = new RenderBuffer[2];
        private readonly float[] _remap = new float[256];
        private GraphicsBuffer _remapBuffer;
        private RenderTexture _unusedAlphamap;

        /// <param name="distanceFields">Spline distance field cache, shared with the height passes.</param>
        /// <param name="waterFields">Water field cache, shared with the height passes.</param>
        internal TextureStampEvaluator(SplineDistanceField distanceFields, WaterDistanceField waterFields)
        {
            var shader = Resources.Load<Shader>(RasterName);
            _compute = Resources.Load<ComputeShader>(ComputeName);
            if (!shader || !_compute || !SystemInfo.supportsComputeShaders)
            {
                throw new NotSupportedException("Looga texture stamps require compute shaders and the " + RasterName +
                    " shader.");
            }
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _target = new HeightStampEvaluator.MaterialTarget(_material);
            _copyEdgeKernel = _compute.FindKernel("CopyEdge");
            _writeKernel = _compute.FindKernel("WriteAlphamaps");
            _writeOneKernel = _compute.FindKernel("WriteAlphamap");
            _checkKernel = _compute.FindKernel("CountAlphamapChanges");
            _distanceFields = distanceFields;
            _waterFields = waterFields;
        }

        /// <summary>Texture passes applied by the last <see cref="EvaluateTile"/> call.</summary>
        internal int LastPassCount { get; private set; }

        public void Dispose()
        {
            if (_material) UnityEngine.Object.DestroyImmediate(_material);
            _remapBuffer?.Release();
            _remapBuffer = null;
            if (_unusedAlphamap)
            {
                _unusedAlphamap.Release();
                UnityEngine.Object.DestroyImmediate(_unusedAlphamap);
            }
        }

        /// <summary>Applies the modifiers to one tile, last modifier first. The caller disposes the result.</summary>
        /// <param name="heights">
        /// Current heights with anisoLevel 0 for a terrain of the world, or null for other terrains. Height and slope
        /// filters read the tile, and the slope filter also reads its neighbors.
        /// </param>
        /// <param name="normalResolution">
        /// Normal map resolution for every tile. MicroVerse uses the heightmap resolution of its first terrain.
        /// </param>
        /// <param name="fields">Measures the terrain fields of the field filters. Null turns them off.</param>
        /// <param name="placementFields">
        /// Placement field map of the tile (PlacementFieldMaps). Null skips the stamps that read placement fields.
        /// </param>
        internal TileLayers EvaluateTile(in StampTile tile, IReadOnlyList<LoogaTextureModifier> modifiers,
            Func<UnityEngine.Terrain, RenderTexture> heights, int normalResolution, TerrainFieldMaps fields,
            Texture placementFields = null)
        {
            TerrainData data = tile.Terrain.terrainData;
            // Random write: the edge copy writes into the maps.
            var descriptor = new RenderTextureDescriptor(data.alphamapWidth, data.alphamapHeight,
                GraphicsFormat.R8G8B8A8_UNorm, 0)
            {
                enableRandomWrite = true
            };
            RenderTexture layers = CreateMap(descriptor, "Looga Stamps Layers");
            RenderTexture weights = CreateMap(descriptor, "Looga Stamps Weights");
            RenderTexture nextLayers = CreateMap(descriptor, "Looga Stamps Layers B");
            RenderTexture nextWeights = CreateMap(descriptor, "Looga Stamps Weights B");
            TerrainLayer[] terrainLayers = data.terrainLayers;
            Rect tileRect = tile.WorldRect;
            RenderTexture previous = RenderTexture.active;
            RenderTexture normals = null;
            LastPassCount = 0;
            try
            {
                _material.SetVector(AlphamapSizeId, new Vector2(descriptor.width, descriptor.height));
                _material.SetVector(NoiseUvId, new Vector3(tile.Position.x / tile.Size.x, tile.Position.z / tile.Size.z,
                    1f));
                _material.SetVector(TerrainSizeXZId, new Vector2(tile.Size.x, tile.Size.z));
                for (int i = modifiers.Count - 1; i >= 0; i--)
                {
                    LoogaTextureModifier modifier = modifiers[i];
                    if (!StampMapping.Touches(modifier.GetCullRect(HeightMapping.MicroVerseParity), tileRect)) continue;
                    int layer = Array.IndexOf(terrainLayers, modifier.Layer);
                    if (layer < 0) continue;
                    int pass;
                    if (modifier is LoogaTextureStamp stamp)
                    {
                        if (stamp.ReadsPlacementFields && !placementFields) continue;
                        if (stamp.SlopeFilter.Enabled && !normals)
                        {
                            normals = CreateNormalMap(tile, heights, normalResolution);
                        }
                        bool readsHeights = stamp.HeightFilter.Enabled || stamp.WaterFilter.ReadsHeights;
                        SetStamp(tile, stamp, layer, readsHeights ? heights(tile.Terrain) : null, normals);
                        bool flow = TerrainFieldMaps.UsesFlow(stamp.FieldFilter, stamp.FieldFilter2);
                        TerrainFieldMaps.TileFields tileFields =
                            stamp.UsesTerrainFields ? fields?.Get(tile, heights, flow) : null;
                        TerrainFieldMaps.SetFilters(_material, stamp.FieldFilter, stamp.FieldFilter2, tileFields,
                            placementFields);
                        pass = StampPass;
                    }
                    else if (modifier is LoogaPathTexture pathTexture && pathTexture.Path)
                    {
                        SetPathTexture(tile, pathTexture, layer);
                        pass = PathPass;
                    }
                    else
                    {
                        continue;
                    }
                    _material.SetTexture(LayerMapId, layers);
                    _material.SetTexture(WeightMapId, weights);
                    _renderTargets[0] = nextLayers.colorBuffer;
                    _renderTargets[1] = nextWeights.colorBuffer;
                    Graphics.SetRenderTarget(_renderTargets, nextLayers.depthBuffer);
                    Graphics.Blit(null, _material, pass);
                    (layers, nextLayers) = (nextLayers, layers);
                    (weights, nextWeights) = (nextWeights, weights);
                    LastPassCount++;
                }
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(nextLayers);
                RenderTexture.ReleaseTemporary(nextWeights);
                if (normals)
                {
                    RenderTexture.ReleaseTemporary(normals);
                }
            }
            return new TileLayers { Terrain = tile.Terrain, Layers = layers, Weights = weights };
        }

        private void SetStamp(in StampTile tile, LoogaTextureStamp stamp, int layer, RenderTexture height,
            RenderTexture normals)
        {
            Transform transform = stamp.transform;
            Vector3 scale = transform.lossyScale;
            _material.SetMatrix(TileToStampId, StampMapping.TileToStampSplat(tile, transform));
            _material.SetVector(StampScaleId, new Vector2(scale.x, scale.z));
            _material.SetFloat(LayerId, layer);
            _material.SetFloat(WeightId, stamp.Weight);
            _material.SetInteger(ClampFalloffTextureId, stamp.ClampFalloffTexture ? 1 : 0);
            HeightStampEvaluator.SetFalloff(_target, tile, stamp.Falloff, _distanceFields, _waterFields,
                LoogaStampTextureSource.FalloffTextureOf(stamp, stamp.Falloff));

            StampNoiseSettings weightNoise = stamp.WeightNoise;
            _material.SetInteger(WeightNoiseTypeId, (int)weightNoise.Type);
            _material.SetVector(WeightNoiseParamsId, weightNoise.Parameters);

            StampRangeFilter heightFilter = stamp.HeightFilter;
            bool useHeight = heightFilter.Enabled && height;
            _material.SetInteger(UseHeightFilterId, useHeight ? 1 : 0);
            _material.SetTexture(HeightmapId, height ? height : Texture2D.blackTexture);
            HeightStampEvaluator.SetWaterFilter(_target, tile, stamp.WaterFilter, _waterFields);
            _material.SetFloat(RealHeightId, tile.RealHeight);
            _material.SetVector(HeightFilterRangeId, heightFilter.Range / tile.RealHeight);
            _material.SetVector(HeightFilterSmoothnessId, heightFilter.Smoothness / tile.RealHeight);
            _material.SetFloat(HeightFilterWeightId, heightFilter.Weight);
            _material.SetInteger(HeightFilterNoiseTypeId, (int)heightFilter.Noise.Type);
            _material.SetVector(HeightFilterNoiseParamsId, heightFilter.Noise.Parameters);

            StampRangeFilter slopeFilter = stamp.SlopeFilter;
            bool useSlope = slopeFilter.Enabled && normals;
            _material.SetInteger(UseSlopeFilterId, useSlope ? 1 : 0);
            _material.SetTexture(NormalmapId, useSlope ? normals : Texture2D.blackTexture);
            _material.SetVector(SlopeFilterRangeId, slopeFilter.Range * Mathf.Deg2Rad);
            _material.SetVector(SlopeFilterSmoothnessId, slopeFilter.Smoothness * Mathf.Deg2Rad);
            _material.SetFloat(SlopeFilterWeightId, slopeFilter.Weight);
            _material.SetInteger(SlopeFilterNoiseTypeId, (int)slopeFilter.Noise.Type);
            _material.SetVector(SlopeFilterNoiseParamsId, slopeFilter.Noise.Parameters);
        }

        private void SetPathTexture(in StampTile tile, LoogaPathTexture pathTexture, int layer)
        {
            Texture distance = _distanceFields.GetPath(tile, pathTexture.Path);
            _material.SetTexture(PathDistanceId, distance);
            _material.SetVector(PathFieldSizeId, new Vector2(distance.width, distance.height));
            _material.SetFloat(LayerId, layer);
            _material.SetFloat(PathTextureWidthId, pathTexture.Width);
            _material.SetFloat(PathTextureSmoothnessId, pathTexture.Smoothness);
            _material.SetFloat(PathTextureWeightId, pathTexture.Weight);
        }

        // MicroVerse normal map: 8-bit ARGB with mipmaps, made with the default color space settings of the project.
        private RenderTexture CreateNormalMap(in StampTile tile, Func<UnityEngine.Terrain, RenderTexture> heights,
            int resolution)
        {
            RenderTexture own = heights(tile.Terrain);
            if (!own) return null;
            var descriptor = new RenderTextureDescriptor(resolution, resolution, RenderTextureFormat.ARGB32, 0)
            {
                useMipMap = true
            };
            RenderTexture normals = RenderTexture.GetTemporary(descriptor);
            normals.name = "Looga Stamps Normals";
            normals.wrapMode = TextureWrapMode.Clamp;
            UnityEngine.Terrain terrain = tile.Terrain;
            UnityEngine.Terrain[] neighbors =
                { terrain.rightNeighbor, terrain.topNeighbor, terrain.leftNeighbor, terrain.bottomNeighbor };
            _material.SetTexture(HeightmapId, own);
            for (int i = 0; i < neighbors.Length; i++)
            {
                RenderTexture neighbor = neighbors[i] ? heights(neighbors[i]) : null;
                bool has = neighbor;
                _material.SetTexture(NeighborHeightmapIds[i], has ? neighbor : own);
                _material.SetInteger(HasNeighborIds[i], has ? 1 : 0);
            }
            Graphics.Blit(null, normals, _material, NormalPass);
            return normals;
        }

        /// <summary>A cleared layer or weight map for <see cref="TileLayers"/>. Release it with ReleaseTemporary.</summary>
        internal static RenderTexture CreateMap(RenderTextureDescriptor descriptor, string name)
        {
            RenderTexture map = RenderTexture.GetTemporary(descriptor);
            map.name = name;
            map.filterMode = FilterMode.Point;
            map.wrapMode = TextureWrapMode.Clamp;
            RenderTexture.active = map;
            GL.Clear(false, true, Color.clear);
            return map;
        }

        #region Edges and alphamaps
        /// <summary>
        /// Copies the shared edges of neighbor tiles in the list, in list order, like the MicroVerse alpha seamer. Each
        /// tile takes the edge of its left, right, top and bottom neighbor in turn.
        /// </summary>
        internal void CopyEdges(IReadOnlyList<TileLayers> tiles)
        {
            var byTerrain = new Dictionary<UnityEngine.Terrain, TileLayers>(tiles.Count);
            foreach (TileLayers tile in tiles)
            {
                byTerrain[tile.Terrain] = tile;
            }
            _remapBuffer ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, _remap.Length, sizeof(float));
            foreach (TileLayers tile in tiles)
            {
                UnityEngine.Terrain terrain = tile.Terrain;
                CopyEdge(tile, terrain.leftNeighbor, 0, byTerrain);
                CopyEdge(tile, terrain.rightNeighbor, 1, byTerrain);
                CopyEdge(tile, terrain.topNeighbor, 2, byTerrain);
                CopyEdge(tile, terrain.bottomNeighbor, 3, byTerrain);
            }
        }

        /// <summary>
        /// After the edge copy of a region update, gives each tile its stored texels where the copy depends on terrains
        /// that the update did not evaluate: every edge against such a terrain, and two by two texels at every corner
        /// that touches one. The copy order decides these texels in a full update, and a neighbor with another
        /// resolution maps the texel next to a corner to its own corner. The region must stay away from these texels.
        /// </summary>
        /// <param name="stored">Maps of the last write for all terrains of the world.</param>
        internal static void KeepStoredBorders(IReadOnlyList<TileLayers> tiles,
            IReadOnlyDictionary<UnityEngine.Terrain, TileLayers> stored)
        {
            var evaluated = new HashSet<UnityEngine.Terrain>();
            foreach (TileLayers tile in tiles)
            {
                evaluated.Add(tile.Terrain);
            }
            foreach (TileLayers tile in tiles)
            {
                if (!stored.TryGetValue(tile.Terrain, out TileLayers previous)) continue;
                if (previous.Layers.width != tile.Layers.width || previous.Layers.height != tile.Layers.height) continue;
                UnityEngine.Terrain terrain = tile.Terrain;
                int width = tile.Layers.width;
                int height = tile.Layers.height;
                if (IsOutside(terrain.leftNeighbor)) Keep(0, 0, 1, height);
                if (IsOutside(terrain.rightNeighbor)) Keep(width - 1, 0, 1, height);
                if (IsOutside(terrain.topNeighbor)) Keep(0, height - 1, width, 1);
                if (IsOutside(terrain.bottomNeighbor)) Keep(0, 0, width, 1);
                KeepCorner(0, 0, terrain.leftNeighbor, terrain.bottomNeighbor, false, false);
                KeepCorner(width - 2, 0, terrain.rightNeighbor, terrain.bottomNeighbor, true, false);
                KeepCorner(0, height - 2, terrain.leftNeighbor, terrain.topNeighbor, false, true);
                KeepCorner(width - 2, height - 2, terrain.rightNeighbor, terrain.topNeighbor, true, true);

                void KeepCorner(int x, int y, UnityEngine.Terrain side, UnityEngine.Terrain vertical, bool toRight,
                    bool toTop)
                {
                    UnityEngine.Terrain diagonal = side ? (toTop ? side.topNeighbor : side.bottomNeighbor) : null;
                    if (!diagonal && vertical)
                    {
                        diagonal = toRight ? vertical.rightNeighbor : vertical.leftNeighbor;
                    }
                    if (IsOutside(side) || IsOutside(vertical) || IsOutside(diagonal))
                    {
                        Keep(x, y, 2, 2);
                    }
                }

                void Keep(int x, int y, int blockWidth, int blockHeight)
                {
                    Graphics.CopyTexture(previous.Layers, 0, 0, x, y, blockWidth, blockHeight, tile.Layers, 0, 0, x, y);
                    Graphics.CopyTexture(previous.Weights, 0, 0, x, y, blockWidth, blockHeight, tile.Weights, 0, 0, x,
                        y);
                }
            }

            // A terrain of the world that the update did not evaluate. Other terrains take no part in the edge copy.
            bool IsOutside(UnityEngine.Terrain neighbor)
            {
                return neighbor && !evaluated.Contains(neighbor) && stored.ContainsKey(neighbor);
            }
        }

        private void CopyEdge(TileLayers tile, UnityEngine.Terrain neighbor, int side,
            Dictionary<UnityEngine.Terrain, TileLayers> byTerrain)
        {
            if (!neighbor || !byTerrain.TryGetValue(neighbor, out TileLayers other)) return;
            TerrainLayer[] own = tile.Terrain.terrainData.terrainLayers;
            TerrainLayer[] theirs = neighbor.terrainData.terrainLayers;
            Array.Clear(_remap, 0, _remap.Length);
            for (int i = 0; i < theirs.Length && i < _remap.Length; i++)
            {
                _remap[i] = Array.IndexOf(own, theirs[i]);
            }
            _remapBuffer.SetData(_remap);
            int width = tile.Layers.width - 1;
            int height = tile.Layers.height - 1;
            _compute.SetBuffer(_copyEdgeKernel, LayerRemapId, _remapBuffer);
            _compute.SetTexture(_copyEdgeKernel, TileLayersId, tile.Layers);
            _compute.SetTexture(_copyEdgeKernel, TileWeightsId, tile.Weights);
            _compute.SetTexture(_copyEdgeKernel, NeighborLayersId, other.Layers);
            _compute.SetTexture(_copyEdgeKernel, NeighborWeightsId, other.Weights);
            _compute.SetInt(SideId, side);
            _compute.SetInt(WidthId, width);
            _compute.SetInt(HeightId, height);
            int length = (side <= 1 ? height : width) + 1;
            _compute.Dispatch(_copyEdgeKernel, Mathf.CeilToInt(length / 64f), 1, 1);
        }

        /// <summary>
        /// Writes the alphamaps of one tile: one RGBA texture for each four terrain layers, in the alphamap format of
        /// the terrain. The caller releases the textures with RenderTexture.ReleaseTemporary.
        /// </summary>
        internal RenderTexture[] WriteAlphamaps(TileLayers tile)
        {
            TerrainData data = tile.Terrain.terrainData;
            int count = AlphamapCount(data);
            var descriptor = new RenderTextureDescriptor(data.alphamapWidth, data.alphamapHeight,
                GraphicsFormat.R8G8B8A8_UNorm, 0)
            {
                enableRandomWrite = true
            };
            var alphamaps = new RenderTexture[count];
            for (int i = 0; i < MaximumAlphamaps; i++)
            {
                RenderTexture target;
                if (i < count)
                {
                    alphamaps[i] = RenderTexture.GetTemporary(descriptor);
                    alphamaps[i].name = "Looga Stamps Alphamap " + i;
                    target = alphamaps[i];
                }
                else
                {
                    target = GetUnusedAlphamap();
                }
                _compute.SetTexture(_writeKernel, AlphamapIds[i], target);
            }
            _compute.SetTexture(_writeKernel, LayerMapId, tile.Layers);
            _compute.SetTexture(_writeKernel, WeightMapId, tile.Weights);
            _compute.SetInt(AlphamapCountId, count);
            _compute.SetInts(AlphamapSizeId, descriptor.width, descriptor.height);
            _compute.Dispatch(_writeKernel, Mathf.CeilToInt(descriptor.width / 8f),
                Mathf.CeilToInt(descriptor.height / 8f), 1);
            return alphamaps;
        }

        /// <summary>
        /// Writes one alphamap of a tile into a target with the alphamap size and format of the terrain. One target can
        /// stage all alphamaps of a tile in turn.
        /// </summary>
        internal void WriteAlphamap(TileLayers tile, int index, RenderTexture target)
        {
            _compute.SetTexture(_writeOneKernel, AlphamapIds[0], target);
            _compute.SetTexture(_writeOneKernel, LayerMapId, tile.Layers);
            _compute.SetTexture(_writeOneKernel, WeightMapId, tile.Weights);
            _compute.SetInt(AlphamapIndexId, index);
            _compute.SetInts(AlphamapSizeId, target.width, target.height);
            _compute.Dispatch(_writeOneKernel, Mathf.CeilToInt(target.width / 8f), Mathf.CeilToInt(target.height / 8f),
                1);
        }

        /// <summary>Number of alphamaps that the passes write for a terrain (32 layers at most).</summary>
        internal static int AlphamapCount(TerrainData data)
        {
            return Mathf.Min(data.alphamapTextureCount, MaximumAlphamaps);
        }

        /// <summary>
        /// Writes the alphamaps of evaluated tiles and compares them with the current terrain alphamaps on the GPU.
        /// Nothing is written to the terrains. The caller reads the result when <see cref="AlphamapCheck.IsDone"/> is
        /// true, and disposes the check. The tiles stay with the caller.
        /// </summary>
        internal AlphamapCheck StartAlphamapCheck(IReadOnlyList<TileLayers> tiles)
        {
            var check = new AlphamapCheck(tiles);
            if (tiles.Count == 0) return check;

            // For each tile: count, x min, y min, x max, y max of the changed texels. See CountAlphamapChanges.
            var values = new uint[tiles.Count * 5];
            for (int i = 0; i < tiles.Count; i++)
            {
                values[i * 5 + 1] = uint.MaxValue;
                values[i * 5 + 2] = uint.MaxValue;
            }
            check.Buffer = new ComputeBuffer(values.Length, sizeof(uint));
            check.Buffer.SetData(values);
            _compute.SetBuffer(_checkKernel, AlphamapChangesId, check.Buffer);
            for (int i = 0; i < tiles.Count; i++)
            {
                TerrainData data = tiles[i].Terrain.terrainData;
                RenderTexture[] alphamaps = WriteAlphamaps(tiles[i]);
                try
                {
                    _compute.SetInts(AlphamapCheckSizeId, data.alphamapWidth, data.alphamapHeight);
                    _compute.SetInt(AlphamapCheckIndexId, i);
                    for (int m = 0; m < alphamaps.Length; m++)
                    {
                        _compute.SetTexture(_checkKernel, NewAlphamapId, alphamaps[m]);
                        _compute.SetTexture(_checkKernel, OldAlphamapId, data.GetAlphamapTexture(m));
                        _compute.Dispatch(_checkKernel, Mathf.CeilToInt(data.alphamapWidth / 8f),
                            Mathf.CeilToInt(data.alphamapHeight / 8f), 1);
                    }
                }
                finally
                {
                    foreach (RenderTexture alphamap in alphamaps)
                    {
                        RenderTexture.ReleaseTemporary(alphamap);
                    }
                }
            }
            check.Start();
            return check;
        }

        /// <summary>A comparison of evaluated alphamaps with the terrain alphamaps that runs on the GPU.</summary>
        internal sealed class AlphamapCheck : IDisposable
        {
            internal ComputeBuffer Buffer;
            private readonly UnityEngine.Terrain[] _terrains;
            private bool _done;
            private uint[] _values;

            internal AlphamapCheck(IReadOnlyList<TileLayers> tiles)
            {
                _terrains = new UnityEngine.Terrain[tiles.Count];
                for (int i = 0; i < tiles.Count; i++)
                {
                    _terrains[i] = tiles[i].Terrain;
                }
            }

            /// <summary>True when <see cref="ReadChangedAreas"/> gives the result without a wait.</summary>
            internal bool IsDone => Buffer == null || _done;

            internal void Start()
            {
                AsyncGPUReadback.Request(Buffer, OnComplete);
            }

            // Unity disposes a done request and its data after its frame, so the callback copies the result.
            private void OnComplete(AsyncGPUReadbackRequest request)
            {
                _done = true;
                _values = request.hasError ? null : request.GetData<uint>().ToArray();
            }

            /// <summary>
            /// The world XZ rectangles of the changed texels of each changed terrain. Null when the readback failed:
            /// the caller must then update all terrains.
            /// </summary>
            internal List<Rect> ReadChangedAreas()
            {
                if (Buffer != null && _values == null) return null;

                var areas = new List<Rect>();
                for (int i = 0; i < _terrains.Length; i++)
                {
                    int first = i * 5;
                    if (_values == null || _values[first] == 0 || !_terrains[i]) continue;
                    TerrainData data = _terrains[i].terrainData;
                    Vector3 position = _terrains[i].GetPosition();
                    float texelX = data.size.x / data.alphamapWidth;
                    float texelZ = data.size.z / data.alphamapHeight;
                    areas.Add(Rect.MinMaxRect(position.x + _values[first + 1] * texelX,
                        position.z + _values[first + 2] * texelZ, position.x + (_values[first + 3] + 1) * texelX,
                        position.z + (_values[first + 4] + 1) * texelZ));
                }
                return areas;
            }

            public void Dispose()
            {
                Buffer?.Release();
                Buffer = null;
            }
        }

        /// <summary>Staging texture for <see cref="WriteAlphamap"/>. Release it with ReleaseTemporary.</summary>
        internal static RenderTexture CreateAlphamapTarget(TerrainData data)
        {
            var descriptor = new RenderTextureDescriptor(data.alphamapWidth, data.alphamapHeight,
                GraphicsFormat.R8G8B8A8_UNorm, 0)
            {
                enableRandomWrite = true
            };
            RenderTexture target = RenderTexture.GetTemporary(descriptor);
            target.name = "Looga Stamps Alphamap Staging";
            return target;
        }

        // Binding for alphamap slots that the terrain does not have. The kernel does not write it.
        private RenderTexture GetUnusedAlphamap()
        {
            if (_unusedAlphamap) return _unusedAlphamap;
            var descriptor = new RenderTextureDescriptor(1, 1, GraphicsFormat.R8G8B8A8_UNorm, 0)
            {
                enableRandomWrite = true
            };
            _unusedAlphamap = new RenderTexture(descriptor) { name = "Looga Stamps Unused Alphamap" };
            _unusedAlphamap.Create();
            return _unusedAlphamap;
        }
        #endregion
    }
}
