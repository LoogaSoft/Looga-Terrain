using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Runs placement modifiers on the GPU, one tile at a time, like MicroVerse 1.7 object and detail stamps. The path
    /// clears first write the occlusion masks of the tile. The clear stamps then write the object and detail clear maps
    /// in hierarchy order. Each object stamp writes its candidates into three half-float targets. The stamps of a tile
    /// copy them into three shared atlas textures, and the CPU reads back each atlas once and keeps the candidates that
    /// spawn. Each detail stamp writes one R8 detail layer. Stamps of the same layer keep the larger value, and the CPU
    /// reads back each layer once.
    /// </summary>
    internal sealed class PlacementEvaluator : IDisposable
    {
        private const string RasterName = "LoogaPlacementRaster";
        private const int ClearPass = 0;
        private const int ObjectPass = 1;
        private const int DetailPass = 2;
        private const int PathClearPass = 3;
        private const int MergePass = 4;
        private const int DisplacePass = 5;
        private const int CandidateWidth = 512;
        private const int TextureFilterLayers = 32;
        private const int DensityByWeightFlag = 1;
        private const int DisabledFlag = 2;
        // The evaluator completes the open readbacks when they reach this size.
        private const long ReadbackBudget = 64L * 1024 * 1024;
        // Each readback creates a GPU staging buffer (about 0.13 ms of main thread each), so the object stamps of a tile
        // share their readbacks. Atlas heights are multiples of AtlasRowStep rows, at most MaxAtlasRows for a readback.
        private const int AtlasRowStep = 256;
        private const int MaxAtlasRows = 4096;
        // Idle atlases above this size are destroyed.
        private const long MaxIdleAtlasBytes = 64L * 1024 * 1024;
        private const GraphicsFormat CandidateFormat = GraphicsFormat.R16G16B16A16_SFloat;

        private static readonly int TileToStampId = Shader.PropertyToID("_TileToStamp");
        private static readonly int NoiseUvId = Shader.PropertyToID("_NoiseUv");
        private static readonly int TerrainSizeXZId = Shader.PropertyToID("_TerrainSizeXZ");
        private static readonly int ClearLayerId = Shader.PropertyToID("_ClearLayer");
        private static readonly int WeightId = Shader.PropertyToID("_Weight");
        private static readonly int ClampFalloffTextureId = Shader.PropertyToID("_ClampFalloffTexture");
        private static readonly int RealHeightId = Shader.PropertyToID("_RealHeight");
        private static readonly int HeightmapId = Shader.PropertyToID("_Heightmap");
        private static readonly int HeightmapTexelSizeId = Shader.PropertyToID("_Heightmap_TexelSize");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexTexelSizeId = Shader.PropertyToID("_MainTex_TexelSize");
        private static readonly int UseHeightFilterId = Shader.PropertyToID("_UseHeightFilter");
        private static readonly int HeightFilterRangeId = Shader.PropertyToID("_HeightFilterRange");
        private static readonly int HeightFilterSmoothnessId = Shader.PropertyToID("_HeightFilterSmoothness");
        private static readonly int HeightFilterWeightId = Shader.PropertyToID("_HeightFilterWeight");
        private static readonly int HeightFilterNoiseTypeId = Shader.PropertyToID("_HeightFilterNoiseType");
        private static readonly int HeightFilterNoiseParamsId = Shader.PropertyToID("_HeightFilterNoiseParams");
        private static readonly int UseSlopeFilterId = Shader.PropertyToID("_UseSlopeFilter");
        private static readonly int SlopeFilterRangeId = Shader.PropertyToID("_SlopeFilterRange");
        private static readonly int SlopeFilterSmoothnessId = Shader.PropertyToID("_SlopeFilterSmoothness");
        private static readonly int SlopeFilterWeightId = Shader.PropertyToID("_SlopeFilterWeight");
        private static readonly int SlopeFilterNoiseTypeId = Shader.PropertyToID("_SlopeFilterNoiseType");
        private static readonly int SlopeFilterNoiseParamsId = Shader.PropertyToID("_SlopeFilterNoiseParams");
        private static readonly int SlopeFilterMeasureId = Shader.PropertyToID("_SlopeFilterMeasure");
        private static readonly int SlopeFilterReachId = Shader.PropertyToID("_SlopeFilterReach");
        private static readonly int SlopeFilterReachFadeId = Shader.PropertyToID("_SlopeFilterReachFade");
        private static readonly int PrototypesId = Shader.PropertyToID("_Prototypes");
        private static readonly int PrototypeCountId = Shader.PropertyToID("_PrototypeCount");
        private static readonly int TotalWeightsId = Shader.PropertyToID("_TotalWeights");
        private static readonly int InstanceCountId = Shader.PropertyToID("_InstanceCount");
        private static readonly int YCountId = Shader.PropertyToID("_YCount");
        private static readonly int SeedId = Shader.PropertyToID("_Seed");
        private static readonly int DiskStrengthId = Shader.PropertyToID("_DiskStrength");
        private static readonly int RandomTexId = Shader.PropertyToID("_RandomTex");
        private static readonly int IndexMapId = Shader.PropertyToID("_IndexMap");
        private static readonly int WeightMapId = Shader.PropertyToID("_WeightMap");
        private static readonly int UseTextureFilterId = Shader.PropertyToID("_UseTextureFilter");
        private static readonly int TextureLayerWeightsId = Shader.PropertyToID("_TextureLayerWeights");
        private static readonly int ClearMaskId = Shader.PropertyToID("_ClearMask");
        private static readonly int PlacementMaskId = Shader.PropertyToID("_PlacementMask");
        private static readonly int ObjectMaskId = Shader.PropertyToID("_ObjectMask");
        private static readonly int[] WeightNoiseIds = NoiseIds("_WeightNoise");
        private static readonly int[] Weight2NoiseIds = NoiseIds("_Weight2Noise");
        private static readonly int Weight2NoiseOperationId = Shader.PropertyToID("_Weight2NoiseOperation");
        private static readonly int WeightRangeId = Shader.PropertyToID("_WeightRange");
        private static readonly int PathDistanceId = Shader.PropertyToID("_PathDistance");
        private static readonly int MaskWidthId = Shader.PropertyToID("_MaskWidth");
        private static readonly int MaskSmoothnessId = Shader.PropertyToID("_MaskSmoothness");
        private static readonly int MergeId = Shader.PropertyToID("_Merge");
        private static readonly int DisplacementId = Shader.PropertyToID("_Displacement");
        private static readonly Vector4[] ChannelMasks =
        {
            new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f),
            new Vector4(0f, 0f, 0f, 1f)
        };

        // Layout must agree with PrototypeData in Resources/LoogaPlacementRaster.shader.
        [StructLayout(LayoutKind.Sequential)]
        private struct PrototypeData
        {
            public float Weight;
            public Vector2 WeightRange;
            public Vector2 RotationX;
            public Vector2 RotationY;
            public Vector2 RotationZ;
            public Vector2 ScaleX;
            public Vector2 ScaleY;
            public Vector2 ScaleZ;
            public int ScaleLock;
            public int RotationLock;
            public float SlopeAlignment;
            public Vector2 Sink;
            public float ScaleAtBoundaries;
            public float GroundFit;
            // Base radius in prefab units. 0 turns the ground fit off.
            public float GroundRadius;
            public int Flags;
        }

        // A GPU readback of one tile. When it is done, it adds its result to the output. A null output drops the result.
        // The buffers stay allocated until the readback is done, because the GPU writes into them.
        private abstract class Readback : IDisposable
        {
            internal StampTile Tile;
            internal StampPlacements Output;
            // One bit for each request that is not done. The completion callbacks record the results: Unity disposes a
            // done request after its frame, and a later status read reports an error also when the data arrived.
            private int _open;
            private int _requests;
            private bool _failed;

            internal abstract long Bytes { get; }
            internal bool IsDone => _open == 0;
            protected bool HasFailed => _failed;
            internal abstract void Wait();
            internal abstract void Apply();
            public abstract void Dispose();

            /// <summary>Gives the completion callback for the next request of the readback.</summary>
            internal Action<AsyncGPUReadbackRequest> NextCallback()
            {
                int bit = 1 << _requests++;
                _open |= bit;
                return request =>
                {
                    _open &= ~bit;
                    _failed |= request.hasError;
                };
            }

            // Waits for a request that is not done. The wait runs its completion callback.
            protected void WaitFor(AsyncGPUReadbackRequest request, int index)
            {
                if ((_open & (1 << index)) != 0)
                {
                    request.WaitForCompletion();
                }
            }

            protected InvalidOperationException Failed(string what)
            {
                return new InvalidOperationException("GPU readback failed for " + what + " on " + Tile.Terrain.name + ".");
            }
        }

        // Candidate readbacks of the object stamps of one tile: four halves for each candidate in each target. The
        // stamps write their rows one after the other into three atlases. The evaluator owns the atlases, and they stay
        // in use until the readback is done.
        private sealed class ObjectReadback : Readback
        {
            internal readonly List<StampRows> Stamps = new();
            internal readonly RenderTexture[] Atlases = new RenderTexture[3];
            internal PlacementEvaluator Owner;
            // Rows of the smallest atlas, and the rows in use.
            internal int Capacity;
            internal int Rows;
            internal NativeArray<ushort> PositionWeight;
            internal NativeArray<ushort> Rotation;
            internal NativeArray<ushort> ScaleIndex;
            internal AsyncGPUReadbackRequest PositionRequest;
            internal AsyncGPUReadbackRequest RotationRequest;
            internal AsyncGPUReadbackRequest ScaleRequest;

            internal override long Bytes => 3L * PositionWeight.Length * sizeof(ushort);

            internal override void Wait()
            {
                if (IsDone) return;
                using (StampSections.Measure(StampSection.PlacementReadbackWait))
                {
                    WaitFor(PositionRequest, 0);
                    WaitFor(RotationRequest, 1);
                    WaitFor(ScaleRequest, 2);
                }
            }

            internal override void Apply()
            {
                if (HasFailed)
                {
                    throw Failed(Stamps.Count == 1 ? Stamps[0].Stamp.name
                        : Stamps[0].Stamp.name + " and " + (Stamps.Count - 1) + " other object stamps");
                }
                using (StampSections.Measure(StampSection.PlacementUnpack))
                {
                    foreach (StampRows rows in Stamps)
                    {
                        Unpack(this, rows, Tile, Output);
                    }
                }
            }

            public override void Dispose()
            {
                if (PositionWeight.IsCreated) PositionWeight.Dispose();
                if (Rotation.IsCreated) Rotation.Dispose();
                if (ScaleIndex.IsCreated) ScaleIndex.Dispose();
                for (int i = 0; i < Atlases.Length; i++)
                {
                    Owner.ReturnAtlas(Atlases[i]);
                    Atlases[i] = null;
                }
            }
        }

        // Rows of one object stamp in the candidate atlases.
        private struct StampRows
        {
            internal LoogaObjectStamp Stamp;
            internal int Offset;
            internal int Count;
        }

        // Readback of one detail layer of one tile: one byte for each texel, row = z.
        private sealed class DetailReadback : Readback
        {
            internal int Layer;
            internal NativeArray<byte> Values;
            internal AsyncGPUReadbackRequest Request;

            internal override long Bytes => Values.Length;

            internal override void Wait()
            {
                if (IsDone) return;
                using (StampSections.Measure(StampSection.DetailReadbackWait))
                {
                    WaitFor(Request, 0);
                }
            }

            internal override void Apply()
            {
                if (HasFailed)
                {
                    throw Failed("detail layer " + Layer);
                }
                Output.SetDetailLayer(Tile.Terrain, Layer, Values.ToArray());
            }

            public override void Dispose()
            {
                if (Values.IsCreated) Values.Dispose();
            }
        }

        private readonly Material _material;
        private readonly HeightStampEvaluator.MaterialTarget _target;
        private readonly SplineDistanceField _distanceFields;
        private readonly WaterDistanceField _waterFields;
        private readonly RenderBuffer[] _renderTargets = new RenderBuffer[3];
        private readonly Vector4[] _textureWeights = new Vector4[TextureFilterLayers];
        private readonly List<Readback> _readbacks = new();
        private readonly List<RenderTexture> _idleAtlases = new();
        // Measured ground fit base radii of the prefabs. A prefab edit takes effect after a domain reload.
        private readonly Dictionary<GameObject, float> _baseRadii = new();
        // Object candidates of the current tile that are not read back yet, and the candidate rows still to come.
        private ObjectReadback _batch;
        private int _rowsLeft;
        private Texture2D _randomTexture;
        private long _readbackBytes;
        // GPU memory of the temporary textures of the current tile, for the memory statistics.
        private long _liveBytes;
        // Terrain fields of the current tile for the field filters, or null.
        private TerrainFieldMaps.TileFields _tileFields;
        // Placement field map of the current tile for the placement field filters, or null.
        private Texture _placementFields;

        /// <param name="distanceFields">Spline distance field cache, shared with the height and texture passes.</param>
        /// <param name="waterFields">Water field cache, shared with the height and texture passes.</param>
        internal PlacementEvaluator(SplineDistanceField distanceFields, WaterDistanceField waterFields)
        {
            var shader = Resources.Load<Shader>(RasterName);
            if (!shader || !SystemInfo.supportsComputeShaders)
            {
                throw new NotSupportedException("Looga placement stamps require the " + RasterName + " shader.");
            }
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _target = new HeightStampEvaluator.MaterialTarget(_material);
            _distanceFields = distanceFields;
            _waterFields = waterFields;
        }

        /// <summary>Object stamp passes of the last <see cref="EvaluateTile"/> call.</summary>
        internal int LastObjectPassCount { get; private set; }

        /// <summary>Clear stamp passes of the last <see cref="EvaluateTile"/> call.</summary>
        internal int LastClearPassCount { get; private set; }

        /// <summary>Detail stamp passes of the last <see cref="EvaluateTile"/> call.</summary>
        internal int LastDetailPassCount { get; private set; }

        /// <summary>Path clear passes of the last <see cref="EvaluateTile"/> call.</summary>
        internal int LastPathClearPassCount { get; private set; }

        /// <summary>
        /// Largest GPU memory of the temporary textures of one tile since the last reset, in bytes. The candidate targets
        /// count until their readback is requested.
        /// </summary>
        internal long PeakTemporaryBytes { get; private set; }

        internal void ResetPeakTemporaryBytes()
        {
            PeakTemporaryBytes = 0;
        }

        public void Dispose()
        {
            foreach (Readback readback in _readbacks)
            {
                readback.Output = null;
            }
            DiscardBatch();
            CompleteReadbacks(true);
            foreach (RenderTexture atlas in _idleAtlases)
            {
                Destroy(atlas);
            }
            _idleAtlases.Clear();
            if (_material) UnityEngine.Object.DestroyImmediate(_material);
            if (_randomTexture) UnityEngine.Object.DestroyImmediate(_randomTexture);
        }

        /// <summary>
        /// Places the object and detail stamps on one tile and adds the placements and detail layers to the output.
        /// Clear stamps remove the placements of earlier stamps. Path clears remove placements of all stamps that
        /// paths occlude.
        /// </summary>
        /// <param name="pathClears">Active path clears. Their order has no effect.</param>
        /// <param name="maskSize">Resolution of the path occlusion masks.</param>
        /// <param name="heights">Heights of the tile with anisoLevel 0.</param>
        /// <param name="layers">Layer and weight maps of the tile before the edge copy, as MicroVerse places.</param>
        /// <param name="wait">
        /// False returns before the GPU readbacks are done. <see cref="CompleteReadbacks"/> then adds the results to the
        /// output in a later call, and <see cref="IsPending"/> is true until it has.
        /// </param>
        /// <param name="fields">Terrain fields of the tile for the field filters. Null turns the field filters off.</param>
        /// <param name="placementFields">
        /// Placement field map of the tile (PlacementFieldMaps). Null turns the placement field filters off.
        /// </param>
        internal void EvaluateTile(in StampTile tile, IReadOnlyList<LoogaPlacementModifier> modifiers,
            IReadOnlyList<LoogaPathClear> pathClears, int maskSize, RenderTexture heights,
            TextureStampEvaluator.TileLayers layers, TerrainFieldMaps.TileFields fields, Texture placementFields,
            StampPlacements output, bool wait = true)
        {
            _tileFields = fields;
            _placementFields = placementFields;
            LastObjectPassCount = 0;
            LastClearPassCount = 0;
            LastDetailPassCount = 0;
            LastPathClearPassCount = 0;
            _liveBytes = 0;
            TerrainData data = tile.Terrain.terrainData;
            Rect tileRect = tile.WorldRect;
            RenderTexture previous = RenderTexture.active;
            RenderTexture placementMask = null;
            RenderTexture objectMask = null;
            RenderTexture clearMap = null;
            RenderTexture detailClearMap = null;
            var detailLayers = new Dictionary<int, RenderTexture>();
            try
            {
                SetTile(tile, heights);
                _rowsLeft = CountCandidateRows(tile, modifiers);
                RenderPathMasks(tile, pathClears, maskSize, out placementMask, out objectMask);
                clearMap = RenderClearMap(tile, modifiers, false, data.alphamapResolution, layers);
                detailClearMap = RenderClearMap(tile, modifiers, true, data.detailResolution, layers);
                var masks = new PathMasks { Placement = placementMask, Objects = objectMask };
                // The clear layers count the clears of each kind that come before the stamp.
                int clearLayer = -1;
                int detailClearLayer = -1;
                foreach (LoogaPlacementModifier modifier in modifiers)
                {
                    if (!StampMapping.Touches(modifier.GetCullRect(HeightMapping.MicroVerseParity), tileRect)) continue;
                    if (modifier is LoogaClearStamp clear)
                    {
                        if (clear.ClearObjects)
                        {
                            clearLayer++;
                        }
                        if (clear.ClearDetails)
                        {
                            detailClearLayer++;
                        }
                    }
                    else if (modifier is LoogaObjectStamp stamp)
                    {
                        RenderObjects(tile, stamp, layers, clearMap, clearLayer, masks, output);
                        if (_readbackBytes > ReadbackBudget)
                        {
                            CompleteReadbacks(true);
                        }
                    }
                    else if (modifier is LoogaDetailStamp detail)
                    {
                        RenderDetails(tile, detail, layers, detailClearMap, detailClearLayer, masks, detailLayers);
                    }
                }
                SubmitBatch();
                RequestDetailReadbacks(tile, detailLayers, output);
                if (wait)
                {
                    CompleteReadbacks(true);
                }
            }
            finally
            {
                _tileFields = null;
                _placementFields = null;
                DiscardBatch();
                RenderTexture.active = previous;
                Release(placementMask);
                Release(objectMask);
                Release(clearMap);
                Release(detailClearMap);
                foreach (RenderTexture layer in detailLayers.Values)
                {
                    Release(layer);
                }
            }
        }

        /// <summary>
        /// Renders only the object clear map of one tile, for tests and parity checks. Null when no modifier clears
        /// objects. The caller releases the map with RenderTexture.ReleaseTemporary.
        /// </summary>
        internal RenderTexture EvaluateClearMap(in StampTile tile, IReadOnlyList<LoogaPlacementModifier> modifiers,
            RenderTexture heights)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                SetTile(tile, heights);
                return RenderClearMap(tile, modifiers, false, tile.Terrain.terrainData.alphamapResolution, null);
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private void SetTile(in StampTile tile, RenderTexture heights)
        {
            float densityScale = tile.Size.x / 1000f;
            _material.SetVector(NoiseUvId, new Vector3(tile.Position.x / tile.Size.x, tile.Position.z / tile.Size.z,
                densityScale));
            _material.SetFloat(RealHeightId, tile.RealHeight);
            _material.SetTexture(HeightmapId, heights);
            // Unity sets texel sizes only for the textures of a Properties block, and this shader has none.
            _material.SetVector(HeightmapTexelSizeId, TexelSize(heights));
            _material.SetVector(TerrainSizeXZId, new Vector2(tile.Size.x, tile.Size.z));
        }

        // Counts a new temporary texture in the memory statistics.
        private RenderTexture Track(RenderTexture texture)
        {
            _liveBytes += Bytes(texture);
            PeakTemporaryBytes = Math.Max(PeakTemporaryBytes, _liveBytes);
            return texture;
        }

        private void Release(RenderTexture texture)
        {
            if (!texture) return;
            _liveBytes -= Bytes(texture);
            RenderTexture.ReleaseTemporary(texture);
        }

        private static long Bytes(RenderTexture texture)
        {
            return (long)texture.width * texture.height * GraphicsFormatUtility.GetBlockSize(texture.graphicsFormat);
        }

        #region Path clears
        // Occlusion masks of one tile.
        private struct PathMasks
        {
            internal RenderTexture Placement;
            internal RenderTexture Objects;
        }

        // The occlusion masks of the path clears that touch the tile, like the MicroVerse occlusion data: an RGBA8
        // placement mask (z trees, w details) and an R8 object mask. Each mask is null when no clear writes it.
        private void RenderPathMasks(in StampTile tile, IReadOnlyList<LoogaPathClear> clears, int size,
            out RenderTexture placementMask, out RenderTexture objectMask)
        {
            placementMask = null;
            objectMask = null;
            Rect tileRect = tile.WorldRect;
            foreach (LoogaPathClear clear in clears)
            {
                if (!clear.Path || !clear.Path.Spline) continue;
                if (!StampMapping.Touches(clear.GetCullRect(HeightMapping.MicroVerseParity), tileRect)) continue;
                LoogaPathClear.ClearShape trees = clear.Trees;
                LoogaPathClear.ClearShape details = clear.Details;
                LoogaPathClear.ClearShape objects = clear.Objects;
                if (!trees.Enabled && !details.Enabled && !objects.Enabled) continue;
                Texture distance = _distanceFields.GetPath(tile, clear.Path);
                if (trees.Enabled || details.Enabled)
                {
                    placementMask ??= CreateMask(size, GraphicsFormat.R8G8B8A8_UNorm);
                    var width = new Vector4(-1f, -1f, trees.Enabled ? trees.Width : -1f,
                        details.Enabled ? details.Width : -1f);
                    var smoothness = new Vector4(0f, 0f, trees.Smoothness, details.Smoothness);
                    placementMask = AddPathClear(placementMask, distance, width, smoothness);
                }
                if (objects.Enabled)
                {
                    objectMask ??= CreateMask(size, GraphicsFormat.R8_UNorm);
                    var width = new Vector4(objects.Width, -1f, -1f, -1f);
                    var smoothness = new Vector4(objects.Smoothness, 0f, 0f, 0f);
                    objectMask = AddPathClear(objectMask, distance, width, smoothness);
                }
            }
        }

        private RenderTexture CreateMask(int size, GraphicsFormat format)
        {
            RenderTexture mask = Track(RenderTexture.GetTemporary(size, size, 0, format));
            mask.name = "Looga Stamps Path Mask";
            mask.filterMode = FilterMode.Bilinear;
            mask.wrapMode = TextureWrapMode.Clamp;
            RenderTexture.active = mask;
            GL.Clear(false, true, Color.clear);
            return mask;
        }

        // One path clear pass from the previous mask into a new mask. The previous mask is released.
        private RenderTexture AddPathClear(RenderTexture mask, Texture distance, Vector4 width, Vector4 smoothness)
        {
            RenderTexture next = Track(RenderTexture.GetTemporary(mask.width, mask.height, 0, mask.graphicsFormat));
            next.name = mask.name;
            next.filterMode = FilterMode.Bilinear;
            next.wrapMode = TextureWrapMode.Clamp;
            // Blit binds its source only for shaders with a Properties block, and this shader has none.
            _material.SetTexture(MainTexId, mask);
            _material.SetTexture(PathDistanceId, distance);
            _material.SetVector(MaskWidthId, width);
            _material.SetVector(MaskSmoothnessId, smoothness);
            Graphics.Blit(mask, next, _material, PathClearPass);
            Release(mask);
            LastPathClearPassCount++;
            return next;
        }

        private void SetPathMasks(PathMasks masks, bool occluded, bool objects)
        {
            _material.SetTexture(PlacementMaskId, occluded && masks.Placement ? masks.Placement : Texture2D.blackTexture);
            _material.SetTexture(ObjectMaskId,
                occluded && objects && masks.Objects ? masks.Objects : Texture2D.blackTexture);
        }
        #endregion

        #region Clear stamps
        // A clear map at the given resolution: one pass for each clear of the kind that touches the tile. Null when no
        // clear stamp of the world clears the kind, and for details also when no detail stamp reads the map. The stamp
        // passes then read black.
        // Without texture layers the clear texture filters are off.
        private RenderTexture RenderClearMap(in StampTile tile, IReadOnlyList<LoogaPlacementModifier> modifiers,
            bool details, int size, TextureStampEvaluator.TileLayers layers)
        {
            bool clears = false;
            bool detailStamps = false;
            foreach (LoogaPlacementModifier modifier in modifiers)
            {
                clears |= modifier is LoogaClearStamp clear && Clears(clear, details);
                detailStamps |= modifier is LoogaDetailStamp;
            }
            if (!clears || (details && !detailStamps) || size <= 0) return null;
            RenderTexture clearMap = CreateClearMap(size);
            RenderTexture.active = clearMap;
            GL.Clear(false, true, Color.clear);
            Rect tileRect = tile.WorldRect;
            int layer = 0;
            foreach (LoogaPlacementModifier modifier in modifiers)
            {
                if (modifier is not LoogaClearStamp clear || !Clears(clear, details)) continue;
                if (!StampMapping.Touches(clear.GetCullRect(HeightMapping.MicroVerseParity), tileRect)) continue;
                SetClear(tile, clear, layers);
                _material.SetFloat(ClearLayerId, layer);
                RenderTexture next = CreateClearMap(size);
                // Blit binds its source only for shaders with a Properties block, and this shader has none.
                _material.SetTexture(MainTexId, clearMap);
                Graphics.Blit(clearMap, next, _material, ClearPass);
                Release(clearMap);
                clearMap = next;
                layer++;
                LastClearPassCount++;
            }
            return clearMap;
        }

        private static bool Clears(LoogaClearStamp clear, bool details)
        {
            return details ? clear.ClearDetails : clear.ClearObjects;
        }

        // RG 8-bit like the MicroVerse clear map. Temporary textures are bilinear with clamp.
        private RenderTexture CreateClearMap(int size)
        {
            RenderTexture map = Track(RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.RG16,
                RenderTextureReadWrite.Linear));
            map.name = "Looga Stamps Clear Map";
            map.filterMode = FilterMode.Bilinear;
            map.wrapMode = TextureWrapMode.Clamp;
            return map;
        }

        private void SetClear(in StampTile tile, LoogaClearStamp clear, TextureStampEvaluator.TileLayers layers)
        {
            _material.SetMatrix(TileToStampId, StampMapping.TileToStampSplat(tile, clear.transform));
            _material.SetFloat(WeightId, clear.Weight);
            _material.SetInteger(ClampFalloffTextureId, clear.ClampFalloffTexture ? 1 : 0);
            HeightStampEvaluator.SetFalloff(_target, tile, clear.Falloff, _distanceFields, _waterFields,
                LoogaStampTextureSource.FalloffTextureOf(clear, clear.Falloff));
            SetNoise(WeightNoiseIds, clear.WeightNoise,
                LoogaStampTextureSource.WeightNoiseTextureOf(clear, clear.WeightNoise, false));
            SetNoise(Weight2NoiseIds, clear.Weight2Noise,
                LoogaStampTextureSource.WeightNoiseTextureOf(clear, clear.Weight2Noise, true));
            _material.SetInteger(Weight2NoiseOperationId, (int)clear.Weight2Noise.Operation);
            SetHeightFilter(tile, clear.HeightFilter);
            SetSlopeFilter(clear.SlopeFilter, clear.SlopeMeasure, clear.SlopeReach, false);
            HeightStampEvaluator.SetWaterFilter(_target, tile, clear.WaterFilter, _waterFields);
            TerrainFieldMaps.SetFilters(_material, clear.FieldFilter, clear.FieldFilter2, _tileFields, _placementFields);
            clear.GetTextureFilterWeights(tile.Terrain.terrainData.terrainLayers, _textureWeights);
            SetTextureFilter(clear.TextureFilterEnabled, layers);
        }
        #endregion

        #region Object stamps
        private void RenderObjects(in StampTile tile, LoogaObjectStamp stamp, TextureStampEvaluator.TileLayers layers,
            RenderTexture clearMap, int clearLayer, PathMasks masks, StampPlacements output)
        {
            IReadOnlyList<ObjectPrototype> prototypes = stamp.Prototypes;
            Texture2D disk = stamp.PoissonDisk;
            int count = CandidateCount(tile, stamp);
            if (count <= 0) return;
            int rowCount = CandidateRows(count);

            _material.SetMatrix(TileToStampId, StampMapping.TileToStampSplat(tile, stamp.transform));
            _material.SetFloat(WeightId, stamp.Weight);
            _material.SetInteger(ClampFalloffTextureId, stamp.ClampFalloffTexture ? 1 : 0);
            HeightStampEvaluator.SetFalloff(_target, tile, stamp.Falloff, _distanceFields, _waterFields,
                LoogaStampTextureSource.FalloffTextureOf(stamp, stamp.Falloff));
            SetNoise(WeightNoiseIds, stamp.WeightNoise,
                LoogaStampTextureSource.WeightNoiseTextureOf(stamp, stamp.WeightNoise, false));
            SetNoise(Weight2NoiseIds, stamp.Weight2Noise,
                LoogaStampTextureSource.WeightNoiseTextureOf(stamp, stamp.Weight2Noise, true));
            _material.SetInteger(Weight2NoiseOperationId, (int)stamp.Weight2Noise.Operation);
            SetHeightFilter(tile, stamp.HeightFilter);
            SetSlopeFilter(stamp.SlopeFilter, stamp.SlopeMeasure, stamp.SlopeReach, false);
            HeightStampEvaluator.SetWaterFilter(_target, tile, stamp.WaterFilter, _waterFields);
            // A field source ignores the placement fields, which its own placements give.
            TerrainFieldMaps.SetFilters(_material, stamp.FieldFilter, stamp.FieldFilter2, _tileFields,
                stamp.IsFieldSource ? null : _placementFields);

            stamp.GetTextureFilterWeights(tile.Terrain.terrainData.terrainLayers, _textureWeights);
            SetTextureFilter(stamp.TextureFilterEnabled, layers);
            _material.SetTexture(ClearMaskId, clearMap ? clearMap : Texture2D.blackTexture);
            _material.SetFloat(ClearLayerId, clearLayer);
            SetPathMasks(masks, stamp.OccludedByPaths, true);

            _material.SetTexture(RandomTexId, GetRandomTexture());
            _material.SetFloat(DiskStrengthId, stamp.PoissonDiskStrength);
            _material.SetFloat(InstanceCountId, count);
            _material.SetFloat(YCountId, rowCount);
            _material.SetFloat(SeedId, stamp.Seed);
            disk.wrapMode = TextureWrapMode.Repeat;
            disk.filterMode = FilterMode.Point;
            _material.SetTexture(MainTexId, disk);
            _material.SetVector(MainTexTexelSizeId, TexelSize(disk));

            var data = new PrototypeData[prototypes.Count];
            float totalWeight = 0f;
            for (int i = 0; i < prototypes.Count; i++)
            {
                data[i] = ToData(prototypes[i], GroundFitRadius(prototypes[i]));
                totalWeight += prototypes[i].Weight + 1;
            }
            _material.SetInteger(PrototypeCountId, prototypes.Count);
            _material.SetFloat(TotalWeightsId, totalWeight);
            var buffer = new ComputeBuffer(data.Length, Marshal.SizeOf<PrototypeData>());
            RenderTexture positionWeight = CreateCandidateTarget(rowCount, "Looga Stamps Candidates Position");
            RenderTexture rotation = CreateCandidateTarget(rowCount, "Looga Stamps Candidates Rotation");
            RenderTexture scaleIndex = CreateCandidateTarget(rowCount, "Looga Stamps Candidates Scale");
            try
            {
                buffer.SetData(data);
                _material.SetBuffer(PrototypesId, buffer);
                _renderTargets[0] = positionWeight.colorBuffer;
                _renderTargets[1] = rotation.colorBuffer;
                _renderTargets[2] = scaleIndex.colorBuffer;
                Graphics.SetRenderTarget(_renderTargets, positionWeight.depthBuffer);
                Graphics.Blit(disk, _material, ObjectPass);
                AddToBatch(tile, output, stamp, positionWeight, rotation, scaleIndex, rowCount);
                LastObjectPassCount++;
            }
            finally
            {
                buffer.Dispose();
                Release(positionWeight);
                Release(rotation);
                Release(scaleIndex);
            }
        }

        // Uses the texture filter weights in _textureWeights.
        private void SetTextureFilter(bool enabled, TextureStampEvaluator.TileLayers layers)
        {
            bool useTextureFilter = enabled && layers != null;
            _material.SetInteger(UseTextureFilterId, useTextureFilter ? 1 : 0);
            _material.SetVectorArray(TextureLayerWeightsId, _textureWeights);
            _material.SetTexture(IndexMapId, useTextureFilter ? layers.Layers : Texture2D.blackTexture);
            _material.SetTexture(WeightMapId, useTextureFilter ? layers.Weights : Texture2D.blackTexture);
        }

        private static Vector4 TexelSize(Texture texture)
        {
            return new Vector4(1f / texture.width, 1f / texture.height, texture.width, texture.height);
        }

        // The base radius of the ground fit in prefab units, or 0 when the prototype has no ground fit.
        private float GroundFitRadius(ObjectPrototype prototype)
        {
            if (prototype.GroundFit <= 0f || !prototype.Prefab) return 0f;
            if (prototype.GroundFitRadius > 0f) return prototype.GroundFitRadius;
            if (!_baseRadii.TryGetValue(prototype.Prefab, out float radius))
            {
                radius = MeasureBaseRadius(prototype.Prefab, out bool keep);
                if (keep)
                {
                    _baseRadii.Add(prototype.Prefab, radius);
                }
            }
            return radius;
        }

        // The largest horizontal distance from the pivot of the lowest 5 % of the LOD 0 mesh vertices, in prefab units.
        // A player cannot read the vertices of a mesh without Read/Write, so the radius is then 0 (no ground fit).
        // keep is false when the editor refused a read (PlacementFieldMaps.TryReadVertices): the next call measures
        // again.
        private static float MeasureBaseRadius(GameObject prefab, out bool keep)
        {
            keep = true;
            var lodGroup = prefab.GetComponentInChildren<LODGroup>();
            Renderer[] renderers = lodGroup && lodGroup.lodCount > 0
                ? lodGroup.GetLODs()[0].renderers
                : prefab.GetComponentsInChildren<Renderer>();
            var points = new List<Vector3>();
            Matrix4x4 toPrefab = prefab.transform.worldToLocalMatrix;
            foreach (Renderer renderer in renderers)
            {
                if (!renderer || !renderer.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) continue;
                Mesh mesh = filter.sharedMesh;
                if (!PlacementFieldMaps.TryReadVertices(mesh, out Vector3[] vertices))
                {
                    keep &= !Application.isEditor;
                    continue;
                }
                Matrix4x4 toLocal = toPrefab * renderer.transform.localToWorldMatrix;
                foreach (Vector3 vertex in vertices)
                {
                    points.Add(toLocal.MultiplyPoint3x4(vertex));
                }
            }
            if (points.Count == 0) return 0f;
            float bottom = float.MaxValue, top = float.MinValue;
            foreach (Vector3 point in points)
            {
                bottom = Mathf.Min(bottom, point.y);
                top = Mathf.Max(top, point.y);
            }
            float limit = bottom + 0.05f * (top - bottom);
            float radius = 0f;
            foreach (Vector3 point in points)
            {
                if (point.y <= limit) radius = Mathf.Max(radius, new Vector2(point.x, point.z).magnitude);
            }
            return radius;
        }

        private static PrototypeData ToData(ObjectPrototype prototype, float groundRadius)
        {
            int flags = (prototype.DensityByWeight ? DensityByWeightFlag : 0) | (prototype.Enabled ? 0 : DisabledFlag);
            return new PrototypeData
            {
                Weight = prototype.Weight,
                WeightRange = prototype.WeightRange,
                RotationX = prototype.RotationX,
                RotationY = prototype.RotationY,
                RotationZ = prototype.RotationZ,
                ScaleX = prototype.ScaleX,
                ScaleY = prototype.ScaleY,
                ScaleZ = prototype.ScaleZ,
                ScaleLock = (int)prototype.ScaleLock,
                RotationLock = (int)prototype.RotationLock,
                SlopeAlignment = prototype.SlopeAlignment,
                Sink = prototype.Sink,
                ScaleAtBoundaries = prototype.ScaleAtBoundaries,
                GroundFit = prototype.GroundFit,
                GroundRadius = groundRadius,
                Flags = flags
            };
        }

        // Candidates of the stamp on the tile. 0 when the stamp places nothing.
        private static int CandidateCount(in StampTile tile, LoogaObjectStamp stamp)
        {
            if (!stamp.PoissonDisk || stamp.Prototypes.Count == 0) return 0;
            float densityScale = tile.Size.x / 1000f;
            float density = stamp.Density;
            return Mathf.Max(0, Mathf.RoundToInt(512 * density * density * densityScale * densityScale));
        }

        // Rows of 512 candidates. MicroVerse adds a row when the count is not a multiple of 512.
        private static int CandidateRows(int count)
        {
            float rows = count / 512.0f;
            int rowCount = Mathf.FloorToInt(count / 512);
            if (rows != Mathf.FloorToInt(rows))
            {
                rowCount += 1;
            }
            return rowCount;
        }

        // Candidate rows of the object stamps on the tile, for the atlas size.
        private static int CountCandidateRows(in StampTile tile, IReadOnlyList<LoogaPlacementModifier> modifiers)
        {
            Rect tileRect = tile.WorldRect;
            int rows = 0;
            foreach (LoogaPlacementModifier modifier in modifiers)
            {
                if (modifier is LoogaObjectStamp stamp &&
                    StampMapping.Touches(modifier.GetCullRect(HeightMapping.MicroVerseParity), tileRect))
                {
                    rows += CandidateRows(CandidateCount(tile, stamp));
                }
            }
            return rows;
        }

        private RenderTexture CreateCandidateTarget(int rows, string name)
        {
            RenderTexture target = Track(RenderTexture.GetTemporary(CandidateWidth, rows, 0, RenderTextureFormat.ARGBHalf,
                RenderTextureReadWrite.Linear));
            target.name = name;
            return target;
        }

        // MicroVerse random values: Unity.Mathematics.Random with state 71, one float4 for each texel, as halves.
        private Texture2D GetRandomTexture()
        {
            if (_randomTexture) return _randomTexture;
            _randomTexture = new Texture2D(64, 64, TextureFormat.RGBAHalf, false, true)
            {
                name = "Looga Stamps Placement Random",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave
            };
            NativeArray<Unity.Mathematics.half4> data = _randomTexture.GetRawTextureData<Unity.Mathematics.half4>();
            var random = new Unity.Mathematics.Random(71);
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (Unity.Mathematics.half4)random.NextFloat4(0f, 1f);
            }
            _randomTexture.Apply(false, false);
            return _randomTexture;
        }
        #endregion

        #region Detail stamps
        // Renders one detail stamp at the detail resolution and keeps the larger value of each texel of its layer. The
        // terrains use coverage mode, so the prototype density does not change the values.
        private void RenderDetails(in StampTile tile, LoogaDetailStamp stamp, TextureStampEvaluator.TileLayers layers,
            RenderTexture clearMap, int clearLayer, PathMasks masks, Dictionary<int, RenderTexture> detailLayers)
        {
            TerrainData data = tile.Terrain.terrainData;
            if (stamp.DetailLayer >= data.detailPrototypes.Length) return;
            _material.SetMatrix(TileToStampId, StampMapping.TileToStampSplat(tile, stamp.transform));
            _material.SetFloat(WeightId, stamp.Weight);
            _material.SetInteger(ClampFalloffTextureId, stamp.ClampFalloffTexture ? 1 : 0);
            HeightStampEvaluator.SetFalloff(_target, tile, stamp.Falloff, _distanceFields, _waterFields,
                LoogaStampTextureSource.FalloffTextureOf(stamp, stamp.Falloff));
            SetNoise(WeightNoiseIds, stamp.WeightNoise,
                LoogaStampTextureSource.WeightNoiseTextureOf(stamp, stamp.WeightNoise, false));
            SetNoise(Weight2NoiseIds, stamp.Weight2Noise,
                LoogaStampTextureSource.WeightNoiseTextureOf(stamp, stamp.Weight2Noise, true));
            _material.SetInteger(Weight2NoiseOperationId, (int)stamp.Weight2Noise.Operation);
            SetHeightFilter(tile, stamp.HeightFilter);
            SetSlopeFilter(stamp.SlopeFilter, stamp.SlopeMeasure, stamp.SlopeReach, stamp.SlopeReachFade);
            HeightStampEvaluator.SetWaterFilter(_target, tile, stamp.WaterFilter, _waterFields);
            TerrainFieldMaps.SetFilters(_material, stamp.FieldFilter, stamp.FieldFilter2, _tileFields, _placementFields);
            stamp.GetTextureFilterWeights(data.terrainLayers, _textureWeights);
            SetTextureFilter(stamp.TextureFilterEnabled, layers);
            _material.SetTexture(ClearMaskId, clearMap ? clearMap : Texture2D.blackTexture);
            _material.SetFloat(ClearLayerId, clearLayer);
            SetPathMasks(masks, stamp.OccludedByPaths, false);
            _material.SetVector(WeightRangeId, stamp.WeightRange);

            RenderTexture result = CreateDetailLayer(data.detailResolution);
            Graphics.Blit(null, result, _material, DetailPass);
            LastDetailPassCount++;
            DisplaceLayers(stamp, result, detailLayers);
            if (detailLayers.TryGetValue(stamp.DetailLayer, out RenderTexture previous))
            {
                RenderTexture merged = CreateDetailLayer(data.detailResolution);
                _material.SetTexture(MainTexId, previous);
                _material.SetTexture(MergeId, result);
                Graphics.Blit(previous, merged, _material, MergePass);
                Release(previous);
                Release(result);
                result = merged;
            }
            detailLayers[stamp.DetailLayer] = result;
        }

        // The displaced layers of earlier stamps make room for the stamp: value x (1 - displacement x stamp value).
        private void DisplaceLayers(LoogaDetailStamp stamp, RenderTexture stampLayer,
            Dictionary<int, RenderTexture> detailLayers)
        {
            if (stamp.Displacement <= 0f) return;
            foreach (int layer in stamp.DisplacedLayers)
            {
                if (layer == stamp.DetailLayer || !detailLayers.TryGetValue(layer, out RenderTexture displaced)) continue;
                RenderTexture thinned = CreateDetailLayer(displaced.width);
                _material.SetTexture(MainTexId, displaced);
                _material.SetTexture(MergeId, stampLayer);
                _material.SetFloat(DisplacementId, stamp.Displacement);
                Graphics.Blit(displaced, thinned, _material, DisplacePass);
                Release(displaced);
                detailLayers[layer] = thinned;
            }
        }

        private RenderTexture CreateDetailLayer(int size)
        {
            RenderTexture layer = Track(RenderTexture.GetTemporary(size, size, 0, GraphicsFormat.R8_UNorm));
            layer.name = "Looga Stamps Detail Layer";
            layer.filterMode = FilterMode.Bilinear;
            layer.wrapMode = TextureWrapMode.Clamp;
            return layer;
        }

        // Requests the detail layers of one tile for the output.
        private void RequestDetailReadbacks(in StampTile tile, Dictionary<int, RenderTexture> detailLayers,
            StampPlacements output)
        {
            foreach (KeyValuePair<int, RenderTexture> pair in detailLayers)
            {
                RenderTexture layer = pair.Value;
                var readback = new DetailReadback
                {
                    Tile = tile,
                    Output = output,
                    Layer = pair.Key,
                    Values = new NativeArray<byte>(layer.width * layer.height, Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory)
                };
                readback.Request = AsyncGPUReadback.RequestIntoNativeArray(ref readback.Values, layer, 0,
                    readback.NextCallback());
                _readbacks.Add(readback);
                _readbackBytes += readback.Bytes;
            }
        }
        #endregion

        #region Filters
        private static int[] NoiseIds(string prefix)
        {
            return new[]
            {
                Shader.PropertyToID(prefix + "Type"), Shader.PropertyToID(prefix + "Space"),
                Shader.PropertyToID(prefix + "Params"), Shader.PropertyToID(prefix + "Texture"),
                Shader.PropertyToID(prefix + "ScaleOffset"), Shader.PropertyToID(prefix + "Channel")
            };
        }

        // The noise texture is the texture that the evaluation uses (WeightNoiseTextureOf).
        private void SetNoise(int[] ids, StampWeightNoise noise, Texture noiseTexture)
        {
            bool texture = noise != null && noise.Type == StampNoiseType.Texture && noiseTexture;
            StampNoiseType type = noise == null || (noise.Type == StampNoiseType.Texture && !texture)
                ? StampNoiseType.None : noise.Type;
            _material.SetInteger(ids[0], (int)type);
            _material.SetInteger(ids[1], noise != null ? (int)noise.Space : 0);
            _material.SetVector(ids[2], noise != null ? noise.Parameters : Vector4.zero);
            _material.SetTexture(ids[3], texture ? noiseTexture : Texture2D.grayTexture);
            _material.SetVector(ids[4], noise != null ? noise.TextureScaleOffset : new Vector4(1f, 1f, 0f, 0f));
            _material.SetVector(ids[5], ChannelMasks[noise != null ? noise.TextureChannel : 0]);
        }

        private void SetHeightFilter(in StampTile tile, StampRangeFilter filter)
        {
            _material.SetInteger(UseHeightFilterId, filter.Enabled ? 1 : 0);
            _material.SetVector(HeightFilterRangeId, filter.Range / tile.RealHeight);
            _material.SetVector(HeightFilterSmoothnessId, filter.Smoothness / tile.RealHeight);
            _material.SetFloat(HeightFilterWeightId, filter.Weight);
            _material.SetInteger(HeightFilterNoiseTypeId, (int)filter.Noise.Type);
            _material.SetVector(HeightFilterNoiseParamsId, filter.Noise.Parameters);
        }

        private void SetSlopeFilter(StampRangeFilter filter, SlopeMeasure measure, float reach, bool reachFade)
        {
            bool enabled = filter != null && filter.Enabled;
            _material.SetInteger(SlopeFilterMeasureId, enabled ? (int)measure : 0);
            _material.SetFloat(SlopeFilterReachId, enabled && measure == SlopeMeasure.Steepness ? reach : 0f);
            _material.SetInteger(SlopeFilterReachFadeId, reachFade ? 1 : 0);
            _material.SetInteger(UseSlopeFilterId, enabled ? 1 : 0);
            _material.SetVector(SlopeFilterRangeId, enabled ? filter.Range * Mathf.Deg2Rad : Vector2.zero);
            _material.SetVector(SlopeFilterSmoothnessId, enabled ? filter.Smoothness * Mathf.Deg2Rad : Vector2.zero);
            _material.SetFloat(SlopeFilterWeightId, enabled ? filter.Weight : 0f);
            _material.SetInteger(SlopeFilterNoiseTypeId, enabled ? (int)filter.Noise.Type : 0);
            _material.SetVector(SlopeFilterNoiseParamsId, enabled ? filter.Noise.Parameters : Vector4.zero);
        }
        #endregion

        #region Readback
        // Copies the candidates of one stamp into the atlases of the tile. A full batch is read back first.
        private void AddToBatch(in StampTile tile, StampPlacements output, LoogaObjectStamp stamp,
            RenderTexture positionWeight, RenderTexture rotation, RenderTexture scaleIndex, int rows)
        {
            if (_batch != null && _batch.Rows + rows > _batch.Capacity)
            {
                SubmitBatch();
            }
            if (_batch == null)
            {
                _batch = new ObjectReadback { Tile = tile, Output = output, Owner = this, Capacity = int.MaxValue };
                int needed = Mathf.Max(rows, Mathf.Min(_rowsLeft, MaxAtlasRows));
                for (int i = 0; i < _batch.Atlases.Length; i++)
                {
                    RenderTexture atlas = Track(AcquireAtlas(needed));
                    _batch.Atlases[i] = atlas;
                    _batch.Capacity = Mathf.Min(_batch.Capacity, atlas.height);
                }
            }
            int offset = _batch.Rows;
            Graphics.CopyTexture(positionWeight, 0, 0, 0, 0, CandidateWidth, rows, _batch.Atlases[0], 0, 0, 0, offset);
            Graphics.CopyTexture(rotation, 0, 0, 0, 0, CandidateWidth, rows, _batch.Atlases[1], 0, 0, 0, offset);
            Graphics.CopyTexture(scaleIndex, 0, 0, 0, 0, CandidateWidth, rows, _batch.Atlases[2], 0, 0, 0, offset);
            _batch.Stamps.Add(new StampRows { Stamp = stamp, Offset = offset, Count = rows });
            _batch.Rows += rows;
            _rowsLeft -= rows;
        }

        // Requests the readbacks of the open batch, only the rows in use.
        private void SubmitBatch()
        {
            ObjectReadback batch = _batch;
            if (batch == null) return;
            _batch = null;
            // Buffers that the evaluator owns. Unity's own readback buffers come from the frame allocator, and many
            // large reads in one frame can run out of memory.
            int length = batch.Rows * CandidateWidth * 4;
            batch.PositionWeight = new NativeArray<ushort>(length, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            batch.Rotation = new NativeArray<ushort>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batch.ScaleIndex = new NativeArray<ushort>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            // The callback order gives the request indices of ObjectReadback.Wait.
            batch.PositionRequest = RequestRows(ref batch.PositionWeight, batch.Atlases[0], batch.Rows,
                batch.NextCallback());
            batch.RotationRequest = RequestRows(ref batch.Rotation, batch.Atlases[1], batch.Rows, batch.NextCallback());
            batch.ScaleRequest = RequestRows(ref batch.ScaleIndex, batch.Atlases[2], batch.Rows, batch.NextCallback());
            // The atlases count in the memory statistics until their readback is requested.
            foreach (RenderTexture atlas in batch.Atlases)
            {
                _liveBytes -= Bytes(atlas);
            }
            _readbacks.Add(batch);
            _readbackBytes += batch.Bytes;
        }

        private static AsyncGPUReadbackRequest RequestRows(ref NativeArray<ushort> values, RenderTexture atlas, int rows,
            Action<AsyncGPUReadbackRequest> callback)
        {
            return AsyncGPUReadback.RequestIntoNativeArray(ref values, atlas, 0, 0, CandidateWidth, 0, rows, 0, 1,
                CandidateFormat, callback);
        }

        // Gives back the atlases of a batch that is not read back, after an error.
        private void DiscardBatch()
        {
            if (_batch == null) return;
            foreach (RenderTexture atlas in _batch.Atlases)
            {
                if (atlas) _liveBytes -= Bytes(atlas);
            }
            _batch.Dispose();
            _batch = null;
        }

        // The smallest idle atlas with at least this many rows, or a new one.
        private RenderTexture AcquireAtlas(int rows)
        {
            int best = -1;
            for (int i = 0; i < _idleAtlases.Count; i++)
            {
                int height = _idleAtlases[i].height;
                if (height >= rows && (best < 0 || height < _idleAtlases[best].height))
                {
                    best = i;
                }
            }
            if (best >= 0)
            {
                RenderTexture idle = _idleAtlases[best];
                _idleAtlases.RemoveAt(best);
                return idle;
            }
            int atlasRows = Mathf.Max(rows, Mathf.Min(Mathf.CeilToInt(rows / (float)AtlasRowStep) * AtlasRowStep,
                SystemInfo.maxTextureSize));
            var atlas = new RenderTexture(new RenderTextureDescriptor(CandidateWidth, atlasRows, CandidateFormat, 0))
            {
                name = "Looga Stamps Candidate Atlas",
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave
            };
            atlas.Create();
            return atlas;
        }

        // Keeps the atlas for the next batches. The oldest idle atlases above MaxIdleAtlasBytes are destroyed.
        private void ReturnAtlas(RenderTexture atlas)
        {
            if (!atlas) return;
            _idleAtlases.Add(atlas);
            long idleBytes = 0;
            foreach (RenderTexture idle in _idleAtlases)
            {
                idleBytes += Bytes(idle);
            }
            while (idleBytes > MaxIdleAtlasBytes)
            {
                idleBytes -= Bytes(_idleAtlases[0]);
                Destroy(_idleAtlases[0]);
                _idleAtlases.RemoveAt(0);
            }
        }

        /// <summary>Destroys the idle atlases. The next batches create new ones.</summary>
        /// <returns>The GPU memory released, in bytes.</returns>
        internal long ReleaseIdleAtlases()
        {
            long bytes = 0;
            foreach (RenderTexture atlas in _idleAtlases)
            {
                if (!atlas) continue;
                bytes += Bytes(atlas);
                Destroy(atlas);
            }
            _idleAtlases.Clear();
            return bytes;
        }

        private static void Destroy(RenderTexture texture)
        {
            texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
        }

        /// <summary>
        /// Adds the results of the done readbacks to their outputs. Without wait, it does not wait and keeps the open
        /// readbacks. A failed readback drops the results of all open readbacks and throws.
        /// </summary>
        /// <remarks>
        /// A readback completes in the editor frame loop. Do not call this in a loop to wait: use wait = true.
        /// </remarks>
        internal void CompleteReadbacks(bool wait = false)
        {
            for (int index = 0; index < _readbacks.Count;)
            {
                Readback readback = _readbacks[index];
                if (wait)
                {
                    readback.Wait();
                }
                else if (!readback.IsDone)
                {
                    index++;
                    continue;
                }
                _readbacks.RemoveAt(index);
                _readbackBytes -= readback.Bytes;
                try
                {
                    if (readback.Output != null)
                    {
                        readback.Apply();
                    }
                }
                catch
                {
                    Drop(null);
                    throw;
                }
                finally
                {
                    readback.Dispose();
                }
            }
        }

        /// <summary>True while a readback for the output is open.</summary>
        internal bool IsPending(StampPlacements output)
        {
            return _readbacks.Exists(readback => readback.Output == output);
        }

        /// <summary>
        /// Drops the results of the open readbacks for the output, or of all open readbacks for null. It does not wait:
        /// the buffers are released when the readbacks are done.
        /// </summary>
        internal void Drop(StampPlacements output)
        {
            foreach (Readback readback in _readbacks)
            {
                if (output == null || readback.Output == output)
                {
                    readback.Output = null;
                }
            }
        }

        // Finds the candidates of a readback, in texel order: the first value of each spawned texel and the number of
        // texels with a prototype index out of range. Most texels are empty, so the main thread decodes only the found
        // ones. Half to float is exact, so the tests give the same result as Mathf.HalfToFloat.
        [BurstCompile(CompileSynchronously = true)]
        private struct FindCandidatesJob : IJob
        {
            [ReadOnly] public NativeArray<ushort> PositionWeight;
            [ReadOnly] public NativeArray<ushort> ScaleIndex;
            public int Prototypes;
            public NativeList<int> Candidates;
            public NativeReference<int> Rejected;

            public void Execute()
            {
                int rejected = 0;
                for (int i = 0; i < PositionWeight.Length; i += 4)
                {
                    float weight = math.f16tof32(PositionWeight[i + 3]);
                    if (!(weight > 0f)) continue;
                    int index = (int)math.f16tof32(ScaleIndex[i + 3]);
                    if (index < 0 || index >= Prototypes)
                    {
                        rejected++;
                        continue;
                    }
                    Candidates.Add(i);
                }
                Rejected.Value = rejected;
            }
        }

        // Like the MicroVerse unpack: position = half UV and height x terrain size + terrain position.
        private static void Unpack(ObjectReadback readback, in StampRows rows, in StampTile tile,
            StampPlacements output)
        {
            int start = rows.Offset * CandidateWidth * 4;
            int length = rows.Count * CandidateWidth * 4;
            NativeArray<ushort> position = readback.PositionWeight.GetSubArray(start, length);
            NativeArray<ushort> rotation = readback.Rotation.GetSubArray(start, length);
            NativeArray<ushort> scale = readback.ScaleIndex.GetSubArray(start, length);
            Vector3 size = tile.Size;
            Vector3 origin = tile.Position;
            using var candidates = new NativeList<int>(Allocator.TempJob);
            using var rejected = new NativeReference<int>(Allocator.TempJob);
            new FindCandidatesJob
            {
                PositionWeight = position,
                ScaleIndex = scale,
                Prototypes = rows.Stamp.Prototypes.Count,
                Candidates = candidates,
                Rejected = rejected
            }.Run();
            output.RejectedCandidates += rejected.Value;
            foreach (int i in candidates)
            {
                int index = (int)Mathf.HalfToFloat(scale[i + 3]);
                var world = new Vector3(Mathf.HalfToFloat(position[i]) * size.x,
                    Mathf.HalfToFloat(position[i + 1]) * size.y, Mathf.HalfToFloat(position[i + 2]) * size.z) + origin;
                var turn = new Quaternion(Mathf.HalfToFloat(rotation[i]), Mathf.HalfToFloat(rotation[i + 1]),
                    Mathf.HalfToFloat(rotation[i + 2]), Mathf.HalfToFloat(rotation[i + 3]));
                var objectScale = new Vector3(Mathf.HalfToFloat(scale[i]), Mathf.HalfToFloat(scale[i + 1]),
                    Mathf.HalfToFloat(scale[i + 2]));
                output.Add(rows.Stamp, tile.Terrain, index, world, turn, objectScale);
            }
        }
        #endregion
    }
}
