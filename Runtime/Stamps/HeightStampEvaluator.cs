using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Runs height modifiers on the GPU, one tile at a time, in heightmap texture units (0.5 = full terrain height).
    /// MicroVerse parity mode blits each modifier from one 16-bit tile texture into another, like MicroVerse. World
    /// Aligned mode uses compute kernels on a float work texture with a halo. The result goes to a 16-bit texture with
    /// the terrain heightmap format.
    /// </summary>
    internal sealed class HeightStampEvaluator : IDisposable
    {
        private const string ComputeName = "LoogaHeightStamps";
        private const string RasterName = "LoogaHeightStampRaster";
        private const int RasterStampPass = 0;
        private const int RasterPathPass = 1;
        private const int RasterSnapshotPass = 2;
        private const int RasterWaterBedPass = 3;

        private static readonly int HeightsId = Shader.PropertyToID("_Heights");
        private static readonly int NewHeightsId = Shader.PropertyToID("_NewHeights");
        private static readonly int OldHeightsId = Shader.PropertyToID("_OldHeights");
        private static readonly int ChangesId = Shader.PropertyToID("_Changes");
        private static readonly int ChangeIndexId = Shader.PropertyToID("_ChangeIndex");
        private static readonly int HeightsSizeId = Shader.PropertyToID("_HeightsSize");
        private static readonly int HaloId = Shader.PropertyToID("_Halo");
        private static readonly int ResolutionId = Shader.PropertyToID("_Resolution");
        private static readonly int RectOffsetId = Shader.PropertyToID("_RectOffset");
        private static readonly int RectSizeId = Shader.PropertyToID("_RectSize");
        private static readonly int StampUvScaleId = Shader.PropertyToID("_StampUvScale");
        private static readonly int TileToStampId = Shader.PropertyToID("_TileToStamp");
        private static readonly int NoiseTransformId = Shader.PropertyToID("_NoiseTransform");
        private static readonly int RealHeightId = Shader.PropertyToID("_RealHeight");
        private static readonly int TerrainHeightId = Shader.PropertyToID("_TerrainHeight");
        private static readonly int TerrainSizeXZId = Shader.PropertyToID("_TerrainSizeXZ");
        private static readonly int CombineModeId = Shader.PropertyToID("_CombineMode");
        private static readonly int StampTextureId = Shader.PropertyToID("_StampTexture");
        private static readonly int MipBiasId = Shader.PropertyToID("_MipBias");
        private static readonly int UsePowerTiltId = Shader.PropertyToID("_UsePowerTilt");
        private static readonly int PowerId = Shader.PropertyToID("_Power");
        private static readonly int TiltId = Shader.PropertyToID("_Tilt");
        private static readonly int HeightRangeId = Shader.PropertyToID("_HeightRange");
        private static readonly int FalloffShapeId = Shader.PropertyToID("_FalloffShape");
        private static readonly int FalloffRangeId = Shader.PropertyToID("_FalloffRange");
        private static readonly int FalloffEasingId = Shader.PropertyToID("_FalloffEasing");
        private static readonly int FalloffTextureId = Shader.PropertyToID("_FalloffTexture");
        private static readonly int FalloffTextureChannelId = Shader.PropertyToID("_FalloffTextureChannel");
        private static readonly int FalloffTextureParamsId = Shader.PropertyToID("_FalloffTextureParams");
        private static readonly int FalloffTextureTransformId = Shader.PropertyToID("_FalloffTextureTransform");
        private static readonly int AreaDistanceId = Shader.PropertyToID("_AreaDistance");
        private static readonly int AreaRangeId = Shader.PropertyToID("_AreaRange");
        private static readonly int AreaBoostId = Shader.PropertyToID("_AreaBoost");
        private static readonly int AreaSideId = Shader.PropertyToID("_AreaSide");
        private static readonly int UsePaintedAreaId = Shader.PropertyToID("_UsePaintedArea");
        private static readonly int PaintedAreaId = Shader.PropertyToID("_PaintedArea");
        private static readonly int PaintedAreaWorldToLocalId = Shader.PropertyToID("_PaintedAreaWorldToLocal");
        private static readonly int NoiseTypeId = Shader.PropertyToID("_NoiseType");
        private static readonly int NoiseSpaceId = Shader.PropertyToID("_NoiseSpace");
        private static readonly int NoiseParamsId = Shader.PropertyToID("_NoiseParams");
        private static readonly int PathDistanceId = Shader.PropertyToID("_PathDistance");
        private static readonly int PathWidthId = Shader.PropertyToID("_PathWidth");
        private static readonly int PathSmoothnessId = Shader.PropertyToID("_PathSmoothness");
        private static readonly int PathFillShoulderId = Shader.PropertyToID("_PathFillShoulder");
        private static readonly int PathFillRampId = Shader.PropertyToID("_PathFillRamp");
        private static readonly int PathTrenchId = Shader.PropertyToID("_PathTrench");
        private static readonly int PathUseTrenchCurveId = Shader.PropertyToID("_PathUseTrenchCurve");
        private static readonly int PathTrenchCurveId = Shader.PropertyToID("_PathTrenchCurve");
        private static readonly int PathEasingId = Shader.PropertyToID("_PathEasing");
        private static readonly int PathNoiseTypeId = Shader.PropertyToID("_PathNoiseType");
        private static readonly int PathNoiseParamsId = Shader.PropertyToID("_PathNoiseParams");
        private static readonly int OutputId = Shader.PropertyToID("_Output");
        private static readonly int SeamAId = Shader.PropertyToID("_SeamA");
        private static readonly int SeamBId = Shader.PropertyToID("_SeamB");
        private static readonly int SeamStartAId = Shader.PropertyToID("_SeamStartA");
        private static readonly int SeamStartBId = Shader.PropertyToID("_SeamStartB");
        private static readonly int SeamStepId = Shader.PropertyToID("_SeamStep");
        private static readonly int SeamLengthId = Shader.PropertyToID("_SeamLength");
        private static readonly int SeamStoredId = Shader.PropertyToID("_SeamStored");
        private static readonly int UseWaterFilterId = Shader.PropertyToID("_UseWaterFilter");
        private static readonly int WaterFieldId = Shader.PropertyToID("_WaterField");
        private static readonly int WaterFieldScaleId = Shader.PropertyToID("_WaterFieldScale");
        private static readonly int WaterTerrainHeightId = Shader.PropertyToID("_WaterTerrainHeight");
        private static readonly int WaterRealHeightId = Shader.PropertyToID("_WaterRealHeight");
        private static readonly int WaterBandSettingsId = Shader.PropertyToID("_WaterBandSettings");
        private static readonly int WaterBandRangesId = Shader.PropertyToID("_WaterBandRanges");
        private static readonly int WaterBandNoisesId = Shader.PropertyToID("_WaterBandNoises");
        private static readonly Vector4[] WaterBandSettings = new Vector4[3];
        private static readonly Vector4[] WaterBandRanges = new Vector4[3];
        private static readonly Vector4[] WaterBandNoises = new Vector4[3];
        private static readonly int SnapshotId = Shader.PropertyToID("_Snapshot");
        private static readonly int SnapshotOffsetId = Shader.PropertyToID("_SnapshotOffset");
        private static readonly int WaterBedFieldId = Shader.PropertyToID("_WaterBedField");
        private static readonly int WaterBedFieldScaleId = Shader.PropertyToID("_WaterBedFieldScale");
        private static readonly int WaterBedParamsId = Shader.PropertyToID("_WaterBedParams");
        private static readonly int WaterBedStrengthId = Shader.PropertyToID("_WaterBedStrength");
        private static readonly int WaterBedNotFoundId = Shader.PropertyToID("_WaterBedNotFound");
        private static readonly int WaterBedNoiseTypeId = Shader.PropertyToID("_WaterBedNoiseType");
        private static readonly int WaterBedNoiseParamsId = Shader.PropertyToID("_WaterBedNoiseParams");
        private static readonly int WaterBedDropId = Shader.PropertyToID("_WaterBedDrop");
        private static readonly int WaterBedKeepDepthId = Shader.PropertyToID("_WaterBedKeepDepth");
        private static readonly int WaterBedUseDropAreaId = Shader.PropertyToID("_WaterBedUseDropArea");
        private static readonly int WaterBedDropAreaFadeId = Shader.PropertyToID("_WaterBedDropAreaFade");
        private static readonly int WaterBedDropAreaId = Shader.PropertyToID("_WaterBedDropArea");
        private static readonly int WaterBedDropNoiseTypeId = Shader.PropertyToID("_WaterBedDropNoiseType");
        private static readonly int WaterBedDropNoiseParamsId = Shader.PropertyToID("_WaterBedDropNoiseParams");

        private static readonly Vector4[] ChannelMasks =
        {
            new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f),
            new Vector4(0f, 0f, 0f, 1f)
        };

        #region Parameter targets
        // Compute kernels and raster passes take the same parameters through this interface.
        internal interface IParameterTarget
        {
            void SetFloat(int id, float value);
            void SetInt(int id, int value);
            void SetVector(int id, Vector4 value);
            void SetMatrix(int id, Matrix4x4 value);
            void SetTexture(int id, Texture value);
            void SetVectorArray(int id, Vector4[] values);
        }

        internal sealed class ComputeTarget : IParameterTarget
        {
            private readonly ComputeShader _shader;

            internal ComputeTarget(ComputeShader shader)
            {
                _shader = shader;
            }

            internal int Kernel { get; set; }

            public void SetFloat(int id, float value) => _shader.SetFloat(id, value);
            public void SetInt(int id, int value) => _shader.SetInt(id, value);
            public void SetVector(int id, Vector4 value) => _shader.SetVector(id, value);
            public void SetMatrix(int id, Matrix4x4 value) => _shader.SetMatrix(id, value);
            public void SetTexture(int id, Texture value) => _shader.SetTexture(Kernel, id, value);
            public void SetVectorArray(int id, Vector4[] values) => _shader.SetVectorArray(id, values);
        }

        internal sealed class MaterialTarget : IParameterTarget
        {
            private readonly Material _material;

            internal MaterialTarget(Material material)
            {
                _material = material;
            }

            public void SetFloat(int id, float value) => _material.SetFloat(id, value);
            public void SetInt(int id, int value) => _material.SetInteger(id, value);
            public void SetVector(int id, Vector4 value) => _material.SetVector(id, value);
            public void SetMatrix(int id, Matrix4x4 value) => _material.SetMatrix(id, value);
            public void SetTexture(int id, Texture value) => _material.SetTexture(id, value);
            public void SetVectorArray(int id, Vector4[] values) => _material.SetVectorArray(id, values);
        }
        #endregion

        private readonly ComputeShader _compute;
        private readonly int _changesKernel;
        private readonly int _clearKernel;
        private readonly int _applyKernel;
        private readonly int _writeKernel;
        private readonly int _seamKernel;
        private readonly int _pathKernel;
        private readonly int _copySeamKernel;
        private readonly int _snapshotKernel;
        private readonly int _waterBedKernel;
        private readonly ComputeTarget _computeTarget;
        private readonly Material _rasterMaterial;
        private readonly MaterialTarget _rasterTarget;
        private readonly SplineDistanceField _distanceField = new();
        private readonly WaterDistanceField _waterFields = new();
        private NeighbourhoodStampPass _neighbourhood;
        private RenderTexture _work;
        private RenderTexture _rasterSwap;
        // Snapshot copies of the current tile, and free copies for the next tiles.
        private readonly Dictionary<LoogaHeightSnapshot, RenderTexture> _snapshots = new();
        private readonly List<RenderTexture> _snapshotPool = new();
        private readonly HashSet<LoogaHeightSnapshot> _usedSnapshots = new();
        private readonly HashSet<LoogaHeightSnapshot> _seenSnapshots = new();
        // Snapshots that a limit in a later segment reads on the current tile.
        private readonly HashSet<LoogaHeightSnapshot> _laterSnapshots = new();
        // Snapshot copies for later segments, for each tile. A neighbourhood stamp can come between a snapshot and its
        // limit. ReleaseSnapshots releases the copies.
        private readonly Dictionary<UnityEngine.Terrain, Dictionary<LoogaHeightSnapshot, RenderTexture>>
            _carriedSnapshots = new();

        /// <summary>Spline distance fields with their cache.</summary>
        internal SplineDistanceField DistanceFields => _distanceField;

        /// <summary>Water fields with their cache.</summary>
        internal WaterDistanceField WaterFields => _waterFields;

        /// <summary>Modifiers applied by the last <see cref="EvaluateTile"/> call.</summary>
        internal int LastStampCount { get; private set; }

        /// <summary>Snapshot copies made by the last <see cref="EvaluateTile"/> call.</summary>
        internal int LastSnapshotCount { get; private set; }

        internal HeightStampEvaluator()
        {
            _compute = Resources.Load<ComputeShader>(ComputeName);
            var rasterShader = Resources.Load<Shader>(RasterName);
            if (!SystemInfo.supportsComputeShaders || !_compute || !rasterShader)
            {
                throw new NotSupportedException("Looga height stamps require compute shaders and the " + RasterName +
                    " shader.");
            }
            _clearKernel = _compute.FindKernel("ClearHeights");
            _applyKernel = _compute.FindKernel("ApplyHeightStamp");
            _writeKernel = _compute.FindKernel("WriteHeights");
            _seamKernel = _compute.FindKernel("AverageSeam");
            _pathKernel = _compute.FindKernel("ApplySplinePath");
            _copySeamKernel = _compute.FindKernel("CopySeam");
            _changesKernel = _compute.FindKernel("CountChanges");
            _snapshotKernel = _compute.FindKernel("ApplySnapshotLimit");
            _waterBedKernel = _compute.FindKernel("ApplyWaterBed");
            _computeTarget = new ComputeTarget(_compute);
            _rasterMaterial = new Material(rasterShader) { hideFlags = HideFlags.HideAndDontSave };
            _rasterTarget = new MaterialTarget(_rasterMaterial);
        }

        public void Dispose()
        {
            _distanceField.Dispose();
            _waterFields.Dispose();
            _neighbourhood?.Dispose();
            Release(ref _work);
            Release(ref _rasterSwap);
            ReleaseSnapshots();
            UnityEngine.Object.DestroyImmediate(_rasterMaterial);
        }

        /// <summary>
        /// Returns, for each tile, the smallest sample rectangle that holds every sample where its output differs from
        /// the current terrain heightmap. An unchanged tile gives an empty rectangle. A tile with another heightmap
        /// size gives the full tile. One GPU readback for all tiles; it waits for the GPU.
        /// </summary>
        internal RectInt[] FindChangedAreas(IReadOnlyList<StampTile> tiles, RenderTexture[] outputs)
        {
            using ChangeCheck check = RequestChangedAreas(tiles, outputs);
            check.Wait();
            return check.Read();
        }

        /// <summary>
        /// Starts the comparison of <see cref="FindChangedAreas"/> and returns without a wait. The caller reads the
        /// result when <see cref="ChangeCheck.IsDone"/> is true, and disposes the check.
        /// </summary>
        internal ChangeCheck RequestChangedAreas(IReadOnlyList<StampTile> tiles, RenderTexture[] outputs)
        {
            var check = new ChangeCheck(tiles.Count);
            if (tiles.Count == 0) return check;
            // For each tile: count, x min, y min, x max, y max of the changed samples. See CountChanges.
            var values = new uint[tiles.Count * 5];
            for (int i = 0; i < tiles.Count; i++)
            {
                values[i * 5 + 1] = uint.MaxValue;
                values[i * 5 + 2] = uint.MaxValue;
            }
            check.Buffer = new ComputeBuffer(values.Length, sizeof(uint));
            check.Buffer.SetData(values);
            _compute.SetBuffer(_changesKernel, ChangesId, check.Buffer);
            for (int i = 0; i < tiles.Count; i++)
            {
                RenderTexture current = tiles[i].Terrain.terrainData.heightmapTexture;
                if (current.width != outputs[i].width || current.height != outputs[i].height)
                {
                    check.Resized[i] = new RectInt(0, 0, outputs[i].width, outputs[i].height);
                    continue;
                }
                _compute.SetTexture(_changesKernel, NewHeightsId, outputs[i]);
                _compute.SetTexture(_changesKernel, OldHeightsId, current);
                _compute.SetInts(HeightsSizeId, outputs[i].width, outputs[i].height);
                _compute.SetInt(ChangeIndexId, i);
                int groups = Mathf.CeilToInt(outputs[i].width / 8f);
                _compute.Dispatch(_changesKernel, groups, groups, 1);
            }
            check.Start();
            return check;
        }

        /// <summary>A comparison of tile outputs with the terrain heightmaps that runs on the GPU.</summary>
        internal sealed class ChangeCheck : IDisposable
        {
            internal ComputeBuffer Buffer;
            // The full tile for a tile with another heightmap size, else empty.
            internal readonly RectInt[] Resized;
            private AsyncGPUReadbackRequest _request;
            private bool _done;
            private uint[] _values;

            internal ChangeCheck(int tiles)
            {
                Resized = new RectInt[tiles];
            }

            /// <summary>True when <see cref="Read"/> gives the result without a wait.</summary>
            internal bool IsDone => Buffer == null || _done;

            internal void Start()
            {
                _request = AsyncGPUReadback.Request(Buffer, OnComplete);
            }

            // Unity disposes a done request and its data after its frame, so the callback copies the result.
            private void OnComplete(AsyncGPUReadbackRequest request)
            {
                _done = true;
                _values = request.hasError ? null : request.GetData<uint>().ToArray();
            }

            internal void Wait()
            {
                if (!IsDone) _request.WaitForCompletion();
            }

            internal RectInt[] Read()
            {
                var areas = new RectInt[Resized.Length];
                if (Buffer != null && _values == null)
                {
                    throw new InvalidOperationException("GPU readback of the changed heights failed.");
                }
                uint[] values = _values;
                for (int i = 0; i < areas.Length; i++)
                {
                    int first = i * 5;
                    if (Resized[i].width > 0)
                    {
                        areas[i] = Resized[i];
                        continue;
                    }
                    if (values[first] == 0) continue;
                    areas[i] = new RectInt((int)values[first + 1], (int)values[first + 2],
                        (int)(values[first + 3] - values[first + 1]) + 1, (int)(values[first + 4] - values[first + 2]) + 1);
                }
                return areas;
            }

            public void Dispose()
            {
                Buffer?.Release();
                Buffer = null;
            }
        }

        /// <summary>Creates a 16-bit output texture in the heightmap format of the tile.</summary>
        internal static RenderTexture CreateOutput(in StampTile tile)
        {
            GraphicsFormat format = tile.Terrain.terrainData.heightmapTexture.graphicsFormat;
            var descriptor = new RenderTextureDescriptor(tile.Resolution, tile.Resolution, format, 0)
            {
                enableRandomWrite = true
            };
            var output = new RenderTexture(descriptor)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "Looga Stamps Heights " + tile.Terrain.name
            };
            output.Create();
            return output;
        }

        /// <summary>Applies the modifiers, in list order, to one tile and writes the tile into the output.</summary>
        /// <param name="halo">Extra samples around the tile for World Aligned mode. Parity mode has no halo.</param>
        internal void EvaluateTile(in StampTile tile, IReadOnlyList<LoogaHeightModifier> modifiers,
            HeightMapping mapping, int halo, RenderTexture output)
        {
            FindUsedSnapshots(tile, modifiers, mapping, halo);
            try
            {
                if (mapping == HeightMapping.MicroVerseParity)
                {
                    EvaluateRaster(tile, modifiers, output);
                }
                else
                {
                    EvaluateCompute(tile, modifiers, halo, output);
                }
            }
            finally
            {
                LastSnapshotCount = _snapshots.Count;
                _snapshotPool.AddRange(_snapshots.Values);
                _snapshots.Clear();
            }
        }

        /// <summary>
        /// Applies a segment of the modifiers to the evaluation state of one tile. The state is the 16-bit tile texture
        /// in parity mode and the float work texture (tile plus halo) in World Aligned mode. The first segment starts
        /// from zero heights. The stamp world runs segments between smooth stamps.
        /// </summary>
        /// <param name="laterModifiers">
        /// The modifiers of the later segments. The tile keeps the snapshot copies that their limits read.
        /// </param>
        internal void EvaluateSegment(in StampTile tile, IReadOnlyList<LoogaHeightModifier> modifiers,
            IReadOnlyList<LoogaHeightModifier> laterModifiers, HeightMapping mapping, int halo, RenderTexture state,
            bool first)
        {
            FindUsedSnapshots(tile, modifiers, mapping, halo, laterModifiers);
            TakeCarriedSnapshots(tile.Terrain);
            try
            {
                if (mapping == HeightMapping.MicroVerseParity)
                {
                    EvaluateRaster(tile, modifiers, state, first);
                }
                else
                {
                    EvaluateCompute(tile, modifiers, halo, null, state, first);
                }
            }
            finally
            {
                LastSnapshotCount = _snapshots.Count;
                CarrySnapshots(tile.Terrain);
            }
        }

        /// <summary>Creates an evaluation state texture for <see cref="EvaluateSegment"/>.</summary>
        internal static RenderTexture CreateState(in StampTile tile, HeightMapping mapping, int halo)
        {
            if (mapping == HeightMapping.MicroVerseParity) return CreateOutput(tile);
            int size = tile.Resolution + 2 * halo;
            var state = new RenderTexture(new RenderTextureDescriptor(size, size, GraphicsFormat.R32_SFloat, 0)
            {
                enableRandomWrite = true
            })
            {
                name = "Looga Stamps Height State " + tile.Terrain.name
            };
            state.Create();
            return state;
        }

        /// <summary>Writes an evaluation state into a tile output texture.</summary>
        internal void WriteState(in StampTile tile, HeightMapping mapping, int halo, RenderTexture state,
            RenderTexture output)
        {
            if (mapping == HeightMapping.MicroVerseParity)
            {
                if (state != output)
                {
                    Graphics.CopyTexture(state, output);
                }
                return;
            }
            _compute.SetInts(HeightsSizeId, state.width, state.height);
            _compute.SetInt(HaloId, halo);
            _compute.SetTexture(_writeKernel, HeightsId, state);
            _compute.SetTexture(_writeKernel, OutputId, output);
            Dispatch(_writeKernel, tile.Resolution, tile.Resolution);
        }

        /// <summary>
        /// Applies a neighbourhood stamp to the evaluation state of one tile. The stamp reads the state and the stored
        /// border rings of the neighbours: slot = (z offset + 1) * 3 + (x offset + 1), slot 4 is the tile. A null slot
        /// or a slot without a ring is a missing neighbour.
        /// </summary>
        internal void ApplyNeighbourhood(in StampTile tile, LoogaNeighbourhoodStamp stamp, HeightMapping mapping,
            int halo, RenderTexture state, UnityEngine.Terrain[] neighbours)
        {
            _neighbourhood ??= new NeighbourhoodStampPass();
            var context = new NeighbourhoodTile(_neighbourhood, tile, stamp, mapping, StateHalo(mapping, halo), state,
                neighbours, _distanceField, _waterFields);
            try
            {
                stamp.ApplyTile(context);
            }
            finally
            {
                context.Release();
            }
        }

        /// <summary>True when the tile has a stored border ring for the neighbourhood stamp.</summary>
        internal bool HasNeighbourhoodRing(in StampTile tile, LoogaNeighbourhoodStamp stamp, HeightMapping mapping,
            int halo)
        {
            return _neighbourhood != null && _neighbourhood.HasRing(tile, stamp, mapping, StateHalo(mapping, halo));
        }

        /// <summary>Stores the border ring of the tile state before the neighbourhood stamp.</summary>
        internal void StoreNeighbourhoodRing(in StampTile tile, LoogaNeighbourhoodStamp stamp, HeightMapping mapping,
            int halo, RenderTexture state)
        {
            _neighbourhood ??= new NeighbourhoodStampPass();
            _neighbourhood.StoreRing(tile, stamp, mapping, StateHalo(mapping, halo), state);
        }

        internal void ReleaseDeadNeighbourhoodRings()
        {
            _neighbourhood?.ReleaseDeadRings();
        }

        internal long NeighbourhoodRingBytes => _neighbourhood?.RingBytes ?? 0;

        // The parity state is the 16-bit tile texture, without a halo.
        private static int StateHalo(HeightMapping mapping, int halo)
        {
            return mapping == HeightMapping.MicroVerseParity ? 0 : halo;
        }

        /// <summary>Releases the snapshot copies. The stamp world calls it after it evaluates its tiles.</summary>
        internal void ReleaseSnapshots()
        {
            _snapshotPool.AddRange(_snapshots.Values);
            _snapshots.Clear();
            foreach (Dictionary<LoogaHeightSnapshot, RenderTexture> carried in _carriedSnapshots.Values)
            {
                _snapshotPool.AddRange(carried.Values);
            }
            _carriedSnapshots.Clear();
            for (int i = 0; i < _snapshotPool.Count; i++)
            {
                RenderTexture copy = _snapshotPool[i];
                Release(ref copy);
            }
            _snapshotPool.Clear();
        }

        /// <summary>
        /// Writes the mean of each shared edge into both tiles, in tile order, like the MicroVerse height seamer.
        /// Only neighbors in the list with the same resolution take part. An edge against a terrain that is not in the
        /// list takes the stored edge of that terrain (the patched MicroVerse rule for partial updates). A corner that
        /// touches a terrain that is not in the list keeps its own stored value: pairwise averaging in tile order can give
        /// the four tiles at a corner different values, so a copied neighbor corner can differ from a full rebuild.
        /// </summary>
        internal void AverageSeams(IReadOnlyList<StampTile> tiles, IReadOnlyList<RenderTexture> outputs)
        {
            var indices = new Dictionary<UnityEngine.Terrain, int>(tiles.Count);
            for (int i = 0; i < tiles.Count; i++)
            {
                indices[tiles[i].Terrain] = i;
            }
            for (int i = 0; i < tiles.Count; i++)
            {
                UnityEngine.Terrain terrain = tiles[i].Terrain;
                int last = tiles[i].Resolution - 1;
                AverageSeam(i, terrain.leftNeighbor, new Vector2Int(0, 0), new Vector2Int(last, 0), Vector2Int.up);
                AverageSeam(i, terrain.rightNeighbor, new Vector2Int(last, 0), new Vector2Int(0, 0), Vector2Int.up);
                AverageSeam(i, terrain.topNeighbor, new Vector2Int(0, last), new Vector2Int(0, 0), Vector2Int.right);
                AverageSeam(i, terrain.bottomNeighbor, new Vector2Int(0, 0), new Vector2Int(0, last),
                    Vector2Int.right);
            }
            for (int i = 0; i < tiles.Count; i++)
            {
                UnityEngine.Terrain terrain = tiles[i].Terrain;
                UnityEngine.Terrain left = terrain.leftNeighbor;
                UnityEngine.Terrain right = terrain.rightNeighbor;
                UnityEngine.Terrain top = terrain.topNeighbor;
                UnityEngine.Terrain bottom = terrain.bottomNeighbor;
                int last = tiles[i].Resolution - 1;
                KeepStoredCorner(i, 0, 0, left, bottom, Diagonal(left, bottom, false, false));
                KeepStoredCorner(i, last, 0, right, bottom, Diagonal(right, bottom, true, false));
                KeepStoredCorner(i, 0, last, left, top, Diagonal(left, top, false, true));
                KeepStoredCorner(i, last, last, right, top, Diagonal(right, top, true, true));
            }

            void KeepStoredCorner(int tile, int x, int y, UnityEngine.Terrain a, UnityEngine.Terrain b,
                UnityEngine.Terrain c)
            {
                if (IsEvaluated(a) && IsEvaluated(b) && IsEvaluated(c)) return;
                TerrainData data = tiles[tile].Terrain.terrainData;
                RenderTexture stored = data ? data.heightmapTexture : null;
                RenderTexture output = outputs[tile];
                if (!stored || stored.width != output.width || stored.graphicsFormat != output.graphicsFormat) return;
                Graphics.CopyTexture(stored, 0, 0, x, y, 1, 1, output, 0, 0, x, y);
            }

            // A missing terrain does not limit a corner.
            bool IsEvaluated(UnityEngine.Terrain neighbor)
            {
                return !neighbor || indices.ContainsKey(neighbor);
            }

            void AverageSeam(int tile, UnityEngine.Terrain neighbor, Vector2Int startA, Vector2Int startB,
                Vector2Int step)
            {
                if (!neighbor) return;
                if (!indices.TryGetValue(neighbor, out int other))
                {
                    CopyStoredSeam(tiles[tile], outputs[tile], neighbor, startA, startB, step);
                    return;
                }
                if (tiles[other].Resolution != tiles[tile].Resolution) return;
                _compute.SetTexture(_seamKernel, SeamAId, outputs[tile]);
                _compute.SetTexture(_seamKernel, SeamBId, outputs[other]);
                _compute.SetInts(SeamStartAId, startA.x, startA.y);
                _compute.SetInts(SeamStartBId, startB.x, startB.y);
                _compute.SetInts(SeamStepId, step.x, step.y);
                _compute.SetInt(SeamLengthId, tiles[tile].Resolution);
                _compute.Dispatch(_seamKernel, Mathf.CeilToInt(tiles[tile].Resolution / 64f), 1, 1);
            }
        }

        // Terrain at the diagonal of a corner, found through either side neighbor.
        private static UnityEngine.Terrain Diagonal(UnityEngine.Terrain side, UnityEngine.Terrain vertical, bool right,
            bool top)
        {
            UnityEngine.Terrain diagonal = side ? (top ? side.topNeighbor : side.bottomNeighbor) : null;
            if (!diagonal && vertical)
            {
                diagonal = right ? vertical.rightNeighbor : vertical.leftNeighbor;
            }
            return diagonal;
        }

        private void CopyStoredSeam(in StampTile tile, RenderTexture output, UnityEngine.Terrain neighbor,
            Vector2Int startA, Vector2Int startB, Vector2Int step)
        {
            if (!neighbor.terrainData) return;
            RenderTexture stored = neighbor.terrainData.heightmapTexture;
            if (!stored || stored.width != output.width || stored.graphicsFormat != output.graphicsFormat) return;
            _compute.SetTexture(_copySeamKernel, SeamAId, output);
            _compute.SetTexture(_copySeamKernel, SeamStoredId, stored);
            _compute.SetInts(SeamStartAId, startA.x, startA.y);
            _compute.SetInts(SeamStartBId, startB.x, startB.y);
            _compute.SetInts(SeamStepId, step.x, step.y);
            _compute.SetInt(SeamLengthId, tile.Resolution);
            _compute.Dispatch(_copySeamKernel, Mathf.CeilToInt(tile.Resolution / 64f), 1, 1);
        }

        #region Raster parity
        // Each modifier is a full-screen blit from one 16-bit texture into the other, as in MicroVerse. The GPU then
        // interpolates the stamp UV and converts to 16 bits exactly as for MicroVerse. The conversion is not
        // round-to-nearest on every GPU: on an RTX 5080 it drops the value to 20 fractional bits first.
        private void EvaluateRaster(in StampTile tile, IReadOnlyList<LoogaHeightModifier> modifiers,
            RenderTexture output, bool clear = true)
        {
            EnsureRasterSwap(output);
            if (clear)
            {
                Clear(output);
            }
            Clear(_rasterSwap);
            SetTileParameters(_rasterTarget, tile);
            _rasterMaterial.SetFloat(ResolutionId, tile.Resolution);
            _rasterMaterial.SetVector(StampUvScaleId, Vector2.one);

            RenderTexture previous = RenderTexture.active;
            RenderTexture source = output;
            RenderTexture target = _rasterSwap;
            Rect tileRect = tile.WorldRect;
            LastStampCount = 0;
            for (int i = 0; i < modifiers.Count; i++)
            {
                LoogaHeightModifier modifier = modifiers[i];
                if (modifier is LoogaHeightSnapshot snapshot)
                {
                    CopySnapshot(snapshot, source);
                    continue;
                }
                if (!StampMapping.Touches(modifier.GetCullRect(HeightMapping.MicroVerseParity), tileRect)) continue;
                if (modifier is LoogaHeightStamp stamp)
                {
                    SetStampParameters(_rasterTarget, tile, stamp,
                        StampMapping.TileToStamp(tile, stamp.transform, HeightMapping.MicroVerseParity));
                    Graphics.Blit(source, target, _rasterMaterial, RasterStampPass);
                }
                else if (modifier is LoogaSplinePath path)
                {
                    SetPathParameters(_rasterTarget, path, _distanceField.GetPath(tile, path));
                    Graphics.Blit(source, target, _rasterMaterial, RasterPathPass);
                }
                else if (modifier is LoogaSnapshotLimit limit && TryGetSnapshot(limit, out RenderTexture copy))
                {
                    SetLimitParameters(_rasterTarget, tile, limit,
                        StampMapping.TileToStamp(tile, limit.transform, HeightMapping.MicroVerseParity), copy);
                    Graphics.Blit(source, target, _rasterMaterial, RasterSnapshotPass);
                }
                else if (modifier is LoogaWaterBed bed && bed.Area)
                {
                    SetWaterBedParameters(_rasterTarget, tile, bed, HeightMapping.MicroVerseParity);
                    Graphics.Blit(source, target, _rasterMaterial, RasterWaterBedPass);
                }
                else
                {
                    continue;
                }
                (source, target) = (target, source);
                LastStampCount++;
            }
            if (source != output)
            {
                Graphics.CopyTexture(source, output);
            }
            // Graphics.Blit leaves its target active.
            RenderTexture.active = previous;
        }

        private void EnsureRasterSwap(RenderTexture output)
        {
            if (_rasterSwap && _rasterSwap.width == output.width && _rasterSwap.graphicsFormat == output.graphicsFormat)
            {
                return;
            }
            Release(ref _rasterSwap);
            var descriptor = new RenderTextureDescriptor(output.width, output.height, output.graphicsFormat, 0);
            _rasterSwap = new RenderTexture(descriptor)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "Looga Stamps Raster Heights"
            };
            _rasterSwap.Create();
        }

        private static void Clear(RenderTexture texture)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
        }
        #endregion

        #region Compute
        // Without a state the evaluation uses the shared work texture and writes the output. With a state it runs on
        // the state and leaves the output alone.
        private void EvaluateCompute(in StampTile tile, IReadOnlyList<LoogaHeightModifier> modifiers, int halo,
            RenderTexture output, RenderTexture state = null, bool clear = true)
        {
            int size = tile.Resolution + 2 * halo;
            RenderTexture work = state;
            if (!work)
            {
                EnsureWork(size);
                work = _work;
            }
            _compute.SetInts(HeightsSizeId, size, size);
            _compute.SetInt(HaloId, halo);
            _compute.SetFloat(ResolutionId, tile.Resolution);
            if (clear)
            {
                _compute.SetTexture(_clearKernel, HeightsId, work);
                Dispatch(_clearKernel, size, size);
            }
            SetTileParameters(_computeTarget, tile);

            float margin = halo * tile.Size.x / (tile.Resolution - 1);
            Rect tileRect = tile.WorldRect;
            tileRect = Rect.MinMaxRect(tileRect.xMin - margin, tileRect.yMin - margin, tileRect.xMax + margin,
                tileRect.yMax + margin);
            LastStampCount = 0;
            for (int i = 0; i < modifiers.Count; i++)
            {
                LoogaHeightModifier modifier = modifiers[i];
                if (modifier is LoogaHeightSnapshot snapshot)
                {
                    CopySnapshot(snapshot, work);
                    continue;
                }
                if (!StampMapping.Touches(modifier.GetCullRect(HeightMapping.WorldAligned), tileRect)) continue;
                if (modifier is LoogaHeightStamp stamp)
                {
                    Matrix4x4 tileToStamp = StampMapping.TileToStamp(tile, stamp.transform, HeightMapping.WorldAligned);
                    RectInt texels = StampMapping.StampTexels(tileToStamp, tile.Resolution, halo);
                    if (texels.width <= 0 || texels.height <= 0) continue;
                    _computeTarget.Kernel = _applyKernel;
                    _compute.SetTexture(_applyKernel, HeightsId, work);
                    _compute.SetInts(RectOffsetId, texels.x, texels.y);
                    _compute.SetInts(RectSizeId, texels.width, texels.height);
                    SetStampParameters(_computeTarget, tile, stamp, tileToStamp);
                    Dispatch(_applyKernel, texels.width, texels.height);
                }
                else if (modifier is LoogaSplinePath path)
                {
                    // A path changes every texel that its distance field reaches, so it runs on the full tile.
                    _computeTarget.Kernel = _pathKernel;
                    _compute.SetTexture(_pathKernel, HeightsId, work);
                    _compute.SetInts(RectOffsetId, 0, 0);
                    _compute.SetInts(RectSizeId, size, size);
                    SetPathParameters(_computeTarget, path, _distanceField.GetPath(tile, path));
                    Dispatch(_pathKernel, size, size);
                }
                else if (modifier is LoogaSnapshotLimit limit && TryGetSnapshot(limit, out RenderTexture copy))
                {
                    Matrix4x4 tileToStamp = StampMapping.TileToStamp(tile, limit.transform, HeightMapping.WorldAligned);
                    RectInt texels = StampMapping.StampTexels(tileToStamp, tile.Resolution, halo);
                    if (texels.width <= 0 || texels.height <= 0) continue;
                    _computeTarget.Kernel = _snapshotKernel;
                    _compute.SetTexture(_snapshotKernel, HeightsId, work);
                    _compute.SetInts(RectOffsetId, texels.x, texels.y);
                    _compute.SetInts(RectSizeId, texels.width, texels.height);
                    SetLimitParameters(_computeTarget, tile, limit, tileToStamp, copy);
                    Dispatch(_snapshotKernel, texels.width, texels.height);
                }
                else if (modifier is LoogaWaterBed bed && bed.Area)
                {
                    // A bed changes every texel that its water field reaches, so it runs on the full tile.
                    _computeTarget.Kernel = _waterBedKernel;
                    _compute.SetTexture(_waterBedKernel, HeightsId, work);
                    _compute.SetInts(RectOffsetId, 0, 0);
                    _compute.SetInts(RectSizeId, size, size);
                    SetWaterBedParameters(_computeTarget, tile, bed, HeightMapping.WorldAligned);
                    Dispatch(_waterBedKernel, size, size);
                }
                else
                {
                    continue;
                }
                LastStampCount++;
            }

            if (state) return;
            _compute.SetTexture(_writeKernel, HeightsId, work);
            _compute.SetTexture(_writeKernel, OutputId, output);
            Dispatch(_writeKernel, tile.Resolution, tile.Resolution);
        }

        private void EnsureWork(int size)
        {
            if (_work && _work.width == size) return;
            Release(ref _work);
            var descriptor = new RenderTextureDescriptor(size, size, GraphicsFormat.R32_SFloat, 0)
            {
                enableRandomWrite = true
            };
            _work = new RenderTexture(descriptor)
            {
                name = "Looga Stamps Work Heights"
            };
            _work.Create();
        }

        private void Dispatch(int kernel, int width, int height)
        {
            _compute.Dispatch(kernel, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);
        }
        #endregion

        #region Snapshots
        // Finds the snapshots that an enabled limit later in the order reads on this tile. Only these get a copy.
        // The later modifiers are the segments after the current one.
        private void FindUsedSnapshots(in StampTile tile, IReadOnlyList<LoogaHeightModifier> modifiers,
            HeightMapping mapping, int halo, IReadOnlyList<LoogaHeightModifier> laterModifiers = null)
        {
            _usedSnapshots.Clear();
            _seenSnapshots.Clear();
            _laterSnapshots.Clear();
            float margin = mapping == HeightMapping.MicroVerseParity ? 0f : halo * tile.Size.x / (tile.Resolution - 1);
            Rect tileRect = tile.WorldRect;
            tileRect = Rect.MinMaxRect(tileRect.xMin - margin, tileRect.yMin - margin, tileRect.xMax + margin,
                tileRect.yMax + margin);
            AddUsedSnapshots(modifiers, mapping, tileRect, false);
            if (laterModifiers != null)
            {
                AddUsedSnapshots(laterModifiers, mapping, tileRect, true);
            }
        }

        private void AddUsedSnapshots(IReadOnlyList<LoogaHeightModifier> modifiers, HeightMapping mapping,
            Rect tileRect, bool later)
        {
            for (int i = 0; i < modifiers.Count; i++)
            {
                if (modifiers[i] is LoogaHeightSnapshot snapshot)
                {
                    _seenSnapshots.Add(snapshot);
                }
                else if (modifiers[i] is LoogaSnapshotLimit limit && limit.Snapshot &&
                         StampMapping.Touches(limit.GetCullRect(mapping), tileRect))
                {
                    if (_seenSnapshots.Contains(limit.Snapshot))
                    {
                        _usedSnapshots.Add(limit.Snapshot);
                    }
                    if (later)
                    {
                        _laterSnapshots.Add(limit.Snapshot);
                    }
                }
            }
        }

        // Gives the tile its copies from earlier segments, so that the limits of this segment can read them.
        private void TakeCarriedSnapshots(UnityEngine.Terrain terrain)
        {
            if (!_carriedSnapshots.Remove(terrain, out Dictionary<LoogaHeightSnapshot, RenderTexture> carried)) return;
            foreach (KeyValuePair<LoogaHeightSnapshot, RenderTexture> pair in carried)
            {
                _snapshots[pair.Key] = pair.Value;
            }
        }

        // Keeps the copies that a later segment reads on the tile. The other copies go back to the pool.
        private void CarrySnapshots(UnityEngine.Terrain terrain)
        {
            Dictionary<LoogaHeightSnapshot, RenderTexture> carried = null;
            foreach (KeyValuePair<LoogaHeightSnapshot, RenderTexture> pair in _snapshots)
            {
                if (_laterSnapshots.Contains(pair.Key))
                {
                    carried ??= new Dictionary<LoogaHeightSnapshot, RenderTexture>();
                    carried[pair.Key] = pair.Value;
                }
                else
                {
                    _snapshotPool.Add(pair.Value);
                }
            }
            _snapshots.Clear();
            if (carried != null)
            {
                _carriedSnapshots[terrain] = carried;
            }
        }

        private void CopySnapshot(LoogaHeightSnapshot snapshot, RenderTexture heights)
        {
            if (!_usedSnapshots.Contains(snapshot)) return;
            if (_snapshots.Remove(snapshot, out RenderTexture previous))
            {
                _snapshotPool.Add(previous);
            }
            RenderTexture copy = null;
            for (int i = 0; i < _snapshotPool.Count; i++)
            {
                RenderTexture candidate = _snapshotPool[i];
                if (!candidate || candidate.width != heights.width || candidate.height != heights.height ||
                    candidate.graphicsFormat != heights.graphicsFormat) continue;
                copy = candidate;
                _snapshotPool.RemoveAt(i);
                break;
            }
            if (!copy)
            {
                var descriptor = new RenderTextureDescriptor(heights.width, heights.height, heights.graphicsFormat, 0);
                copy = new RenderTexture(descriptor)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Point,
                    name = "Looga Stamps Height Snapshot"
                };
                copy.Create();
            }
            Graphics.CopyTexture(heights, copy);
            _snapshots[snapshot] = copy;
        }

        // A limit reads its snapshot only when the snapshot came earlier in the order on this tile.
        private bool TryGetSnapshot(LoogaSnapshotLimit limit, out RenderTexture copy)
        {
            copy = null;
            return limit.Snapshot && _snapshots.TryGetValue(limit.Snapshot, out copy);
        }
        #endregion

        #region Parameters
        internal static void SetTileParameters(IParameterTarget target, in StampTile tile)
        {
            float noiseScale = tile.Size.x / 1000f;
            target.SetVector(NoiseTransformId,
                new Vector4(tile.Position.x / tile.Size.x, tile.Position.z / tile.Size.z, noiseScale, 0f));
            target.SetVector(TerrainSizeXZId, new Vector2(tile.Size.x, tile.Size.z));
            target.SetFloat(RealHeightId, tile.RealHeight);
            target.SetFloat(TerrainHeightId, tile.Position.y);
        }

        private void SetStampParameters(IParameterTarget target, in StampTile tile, LoogaHeightStamp stamp,
            Matrix4x4 tileToStamp)
        {
            // Stamp heights in heightmap texture units. A flat stamp samples 0 and gives the bottom height.
            Transform transform = stamp.transform;
            float bottom = tile.WorldToLocal.MultiplyPoint3x4(transform.position).y;
            float top = bottom + transform.lossyScale.y;
            bool usePowerTilt = stamp.Power != 1f || stamp.Tilt != Vector2.zero;

            target.SetMatrix(TileToStampId, tileToStamp);
            Texture texture = stamp.EvaluatedTexture;
            target.SetTexture(StampTextureId, texture ? texture : Texture2D.blackTexture);
            target.SetFloat(MipBiasId, stamp.MipBias);
            target.SetInt(UsePowerTiltId, usePowerTilt ? 1 : 0);
            target.SetFloat(PowerId, stamp.Power);
            target.SetVector(TiltId, stamp.Tilt);
            target.SetVector(HeightRangeId, new Vector2(bottom, top) / tile.RealHeight);
            target.SetInt(CombineModeId, (int)stamp.Mode);
            SetFalloff(target, tile, stamp.Falloff, _distanceField, _waterFields, stamp.EvaluatedFalloffTexture);
        }

        /// <summary>Sets the falloff uniforms of StampFalloff.hlsl. Height and texture passes share them.</summary>
        /// <param name="texture">Falloff texture in place of the falloff settings texture, or null.</param>
        internal static void SetFalloff(IParameterTarget target, in StampTile tile, StampFalloff falloff,
            SplineDistanceField distanceFields, WaterDistanceField waterFields, Texture texture = null)
        {
            Texture areaDistance = Texture2D.whiteTexture;
            float areaRange = falloff.SplineAreaRange;
            float areaBoost = falloff.SplineAreaBoost;
            int areaSide = 0;
            if (falloff.Shape == FalloffShape.SplineArea && falloff.SplineArea)
            {
                areaDistance = distanceFields.GetArea(tile, falloff.SplineArea);
                areaSide = falloff.SplineAreaSide;
            }
            else if (falloff.Shape == FalloffShape.WaterArea && falloff.WaterArea)
            {
                areaDistance = waterFields.Get(tile, falloff.WaterArea);
                areaRange = falloff.WaterAreaRange;
                areaBoost = falloff.WaterAreaBoost;
            }
            target.SetTexture(AreaDistanceId, areaDistance);
            target.SetFloat(AreaRangeId, areaRange);
            target.SetFloat(AreaBoostId, areaBoost);
            target.SetInt(AreaSideId, areaSide);
            LoogaPaintedArea painted = falloff.PaintedArea;
            target.SetInt(UsePaintedAreaId, painted ? 1 : 0);
            target.SetTexture(PaintedAreaId, painted ? painted.GetTexture() : Texture2D.whiteTexture);
            target.SetMatrix(PaintedAreaWorldToLocalId,
                painted ? painted.transform.worldToLocalMatrix : Matrix4x4.identity);
            target.SetInt(FalloffShapeId, (int)falloff.Shape);
            target.SetVector(FalloffRangeId, falloff.Range);
            target.SetInt(FalloffEasingId, (int)falloff.Easing);
            if (!texture)
            {
                texture = falloff.Texture;
            }
            target.SetTexture(FalloffTextureId, texture ? texture : Texture2D.whiteTexture);
            target.SetVector(FalloffTextureChannelId, ChannelMasks[Mathf.Clamp(falloff.TextureChannel, 0, 3)]);
            target.SetVector(FalloffTextureParamsId, falloff.TextureAmplitudeBalance);
            target.SetVector(FalloffTextureTransformId, new Vector4(falloff.TextureRotation, falloff.TextureScale,
                falloff.TextureOffset.x, falloff.TextureOffset.y));
            target.SetInt(NoiseTypeId, (int)falloff.NoiseType);
            target.SetInt(NoiseSpaceId, (int)falloff.NoiseSpace);
            target.SetVector(NoiseParamsId, new Vector4(falloff.NoiseFrequency, falloff.NoiseAmplitude,
                falloff.NoiseOffset, falloff.NoiseBalance));
        }

        private void SetLimitParameters(IParameterTarget target, in StampTile tile, LoogaSnapshotLimit limit,
            Matrix4x4 tileToStamp, Texture snapshot)
        {
            target.SetMatrix(TileToStampId, tileToStamp);
            target.SetTexture(SnapshotId, snapshot);
            target.SetFloat(SnapshotOffsetId, limit.Offset / tile.RealHeight);
            target.SetInt(CombineModeId, (int)limit.Mode);
            SetFalloff(target, tile, limit.Falloff, _distanceField, _waterFields);
        }

        private void SetWaterBedParameters(IParameterTarget target, in StampTile tile, LoogaWaterBed bed,
            HeightMapping mapping)
        {
            target.SetMatrix(TileToStampId, StampMapping.TileToStamp(tile, bed.transform, mapping));
            SetFalloff(target, tile, bed.Falloff, _distanceField, _waterFields);
            target.SetTexture(WaterBedFieldId, _waterFields.Get(tile, bed.Area));
            target.SetVector(WaterBedFieldScaleId, WaterFieldScale(tile));
            target.SetVector(WaterBedParamsId, new Vector4(bed.MaxDepth, 3f / Mathf.Max(0.5f, bed.ShelfWidth),
                bed.BeachWidth, bed.BeachMaxHeight));
            target.SetFloat(WaterBedStrengthId, bed.Strength);
            target.SetFloat(WaterBedNotFoundId, WaterDistanceField.NotFound);
            target.SetInt(WaterBedNoiseTypeId, (int)bed.BedNoise.Type);
            target.SetVector(WaterBedNoiseParamsId, bed.BedNoise.Parameters);
            target.SetVector(WaterBedDropId, new Vector4(bed.DropDepth, bed.DropStart, bed.DropWidth, 0f));
            target.SetFloat(WaterBedKeepDepthId, bed.KeepDepth);
            bool dropArea = bed.DropArea;
            target.SetInt(WaterBedUseDropAreaId, dropArea ? 1 : 0);
            target.SetFloat(WaterBedDropAreaFadeId, bed.DropAreaFade);
            target.SetTexture(WaterBedDropAreaId,
                dropArea ? _distanceField.GetArea(tile, bed.DropArea) : Texture2D.blackTexture);
            target.SetInt(WaterBedDropNoiseTypeId, (int)bed.DropNoise.Type);
            target.SetVector(WaterBedDropNoiseParamsId, bed.DropNoise.Parameters);
        }

        /// <summary>Sets the uniforms of WaterFilter.hlsl. Texture and placement passes share them.</summary>
        internal static void SetWaterFilter(IParameterTarget target, in StampTile tile, StampWaterFilter filter,
            WaterDistanceField waterFields)
        {
            bool enabled = filter != null && filter.Enabled;
            target.SetInt(UseWaterFilterId, enabled ? 1 : 0);
            target.SetTexture(WaterFieldId, enabled ? waterFields.Get(tile, filter.Area) : Texture2D.blackTexture);
            if (!enabled) return;
            target.SetVector(WaterFieldScaleId, WaterFieldScale(tile));
            target.SetFloat(WaterTerrainHeightId, tile.Position.y);
            target.SetFloat(WaterRealHeightId, tile.RealHeight);
            SetWaterBand(0, filter.EdgeBand);
            SetWaterBand(1, filter.LowGroundDistance);
            SetWaterBand(2, filter.LowGroundHeight);
            target.SetVectorArray(WaterBandSettingsId, WaterBandSettings);
            target.SetVectorArray(WaterBandRangesId, WaterBandRanges);
            target.SetVectorArray(WaterBandNoisesId, WaterBandNoises);
        }

        /// <summary>Field UV = tile UV over the terrain size times this scale.</summary>
        internal static Vector2 WaterFieldScale(in StampTile tile)
        {
            return new Vector2(tile.Size.x / tile.SampleSpan.x, tile.Size.z / tile.SampleSpan.y);
        }

        private static void SetWaterBand(int band, StampRangeFilter filter)
        {
            WaterBandSettings[band] = new Vector4(filter.Enabled ? 1f : 0f, filter.Weight, (int)filter.Noise.Type, 0f);
            WaterBandRanges[band] = new Vector4(filter.Range.x, filter.Range.y, filter.Smoothness.x,
                filter.Smoothness.y);
            WaterBandNoises[band] = filter.Noise.Parameters;
        }

        private static void SetPathParameters(IParameterTarget target, LoogaSplinePath path, Texture distance)
        {
            target.SetTexture(PathDistanceId, distance);
            target.SetTexture(PathTrenchCurveId, path.GetTrenchTexture());
            target.SetFloat(PathWidthId, path.Width);
            target.SetFloat(PathSmoothnessId, path.Smoothness);
            target.SetFloat(PathFillShoulderId, path.FillShoulder);
            target.SetFloat(PathFillRampId, LoogaSplinePath.FillRamp);
            target.SetFloat(PathTrenchId, path.Trench);
            target.SetInt(PathUseTrenchCurveId, path.UseTrenchCurve ? 1 : 0);
            target.SetInt(PathEasingId, (int)path.EmbankmentEasing);
            target.SetInt(PathNoiseTypeId, (int)path.EmbankmentNoise.Type);
            target.SetVector(PathNoiseParamsId, path.EmbankmentNoise.Parameters);
            target.SetInt(CombineModeId, (int)path.Mode);
        }
        #endregion

        private static void Release(ref RenderTexture texture)
        {
            if (texture)
            {
                texture.Release();
                UnityEngine.Object.DestroyImmediate(texture);
            }
            texture = null;
        }
    }
}
