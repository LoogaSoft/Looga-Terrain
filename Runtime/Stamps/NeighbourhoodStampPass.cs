using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// GPU passes of the <see cref="LoogaNeighbourhoodStamp"/>s for one tile. It builds an apron from the prefix heights
    /// of the tile and the border rings of its neighbours. For a <see cref="LoogaSmoothStamp"/> it then blurs the apron
    /// with a separable Gaussian and writes the weighted result into the tile state. See LoogaHeightSmooth.compute.
    /// </summary>
    /// <remarks>
    /// A border ring keeps the prefix heights of a tile within the stamp reach of its edges, about 1 MB for a 2049 tile
    /// and a 15 m reach. The rings stay between evaluations. A tile that is not written then needs no evaluation for its
    /// neighbours: an edit changes the heights of a tile only through an evaluation of that tile, which also replaces
    /// its rings.
    /// </remarks>
    internal sealed class NeighbourhoodStampPass : IDisposable
    {
        private const string ComputeName = "LoogaHeightSmooth";
        private const string TalusComputeName = "LoogaTalus";

        // Slot 4 is the tile itself, which has no ring.
        private static readonly int[] RingIds =
        {
            Shader.PropertyToID("_Ring0"), Shader.PropertyToID("_Ring1"), Shader.PropertyToID("_Ring2"),
            Shader.PropertyToID("_Ring3"), 0, Shader.PropertyToID("_Ring5"), Shader.PropertyToID("_Ring6"),
            Shader.PropertyToID("_Ring7"), Shader.PropertyToID("_Ring8")
        };
        private static readonly int RingMaskId = Shader.PropertyToID("_RingMask");
        private static readonly int RingDepthId = Shader.PropertyToID("_RingDepth");
        private static readonly int StateId = Shader.PropertyToID("_State");
        private static readonly int RingOutId = Shader.PropertyToID("_RingOut");
        private static readonly int TileResolutionId = Shader.PropertyToID("_TileResolution");
        private static readonly int HaloId = Shader.PropertyToID("_Halo");
        private static readonly int ReachId = Shader.PropertyToID("_Reach");
        private static readonly int TargetSizeId = Shader.PropertyToID("_TargetSize");
        private static readonly int ApronSizeId = Shader.PropertyToID("_ApronSize");
        private static readonly int SigmaTexelsId = Shader.PropertyToID("_SigmaTexels");
        private static readonly int ApronId = Shader.PropertyToID("_Apron");
        private static readonly int ApronInId = Shader.PropertyToID("_ApronIn");
        private static readonly int BlurTempId = Shader.PropertyToID("_BlurTemp");
        private static readonly int BlurTempInId = Shader.PropertyToID("_BlurTempIn");
        private static readonly int BlurredId = Shader.PropertyToID("_Blurred");
        private static readonly int BlurredInId = Shader.PropertyToID("_BlurredIn");
        private static readonly int WeightId = Shader.PropertyToID("_Weight");
        private static readonly int WeightInId = Shader.PropertyToID("_WeightIn");
        private static readonly int TargetId = Shader.PropertyToID("_Target");
        private static readonly int TileToStampId = Shader.PropertyToID("_TileToStamp");
        private static readonly int MaxChangeId = Shader.PropertyToID("_MaxChange");
        private static readonly int StrengthId = Shader.PropertyToID("_Strength");
        private static readonly int HeightNoiseTypeId = Shader.PropertyToID("_HeightNoiseType");
        private static readonly int HeightNoiseParamsId = Shader.PropertyToID("_HeightNoiseParams");
        private static readonly int KeepAboveWaterId = Shader.PropertyToID("_KeepAboveWater");
        private static readonly int KeepUnderwaterId = Shader.PropertyToID("_KeepUnderwater");
        private static readonly int RimFieldId = Shader.PropertyToID("_RimField");
        private static readonly int RimFieldScaleId = Shader.PropertyToID("_RimFieldScale");
        private static readonly int RimWidthId = Shader.PropertyToID("_RimWidth");
        private static readonly int WaterClearanceId = Shader.PropertyToID("_WaterClearance");
        private static readonly int RimNotFoundId = Shader.PropertyToID("_RimNotFound");
        private static readonly int ApronOriginId = Shader.PropertyToID("_ApronOrigin");
        private static readonly int TexelSizeId = Shader.PropertyToID("_TexelSize");
        private static readonly int SteepSlopeId = Shader.PropertyToID("_SteepSlope");
        private static readonly int SeedRiseId = Shader.PropertyToID("_SeedRise");
        private static readonly int SeedRadiusId = Shader.PropertyToID("_SeedRadius");
        private static readonly int SeedDropId = Shader.PropertyToID("_SeedDrop");
        private static readonly int MaximumRiseId = Shader.PropertyToID("_MaximumRise");
        private static readonly int FaceRadiusId = Shader.PropertyToID("_FaceRadius");
        private static readonly int MinimumFaceRiseId = Shader.PropertyToID("_MinimumFaceRise");
        private static readonly int ApronHeightId = Shader.PropertyToID("_ApronHeight");
        private static readonly int ApronReachTexelsId = Shader.PropertyToID("_ApronReachTexels");
        private static readonly int ProfilePowerId = Shader.PropertyToID("_ProfilePower");
        private static readonly int HeightVariationId = Shader.PropertyToID("_HeightVariation");
        private static readonly int VariationSizeId = Shader.PropertyToID("_VariationSize");
        private static readonly int StepAxisId = Shader.PropertyToID("_StepAxis");
        private static readonly int StepDiagonalId = Shader.PropertyToID("_StepDiagonal");
        private static readonly int SteepId = Shader.PropertyToID("_Steep");
        private static readonly int SteepInId = Shader.PropertyToID("_SteepIn");
        private static readonly int DistanceInId = Shader.PropertyToID("_DistanceIn");
        private static readonly int DistanceOutId = Shader.PropertyToID("_DistanceOut");
        private static readonly int RaisedId = Shader.PropertyToID("_Raised");
        private static readonly int RaisedInId = Shader.PropertyToID("_RaisedIn");
        private static readonly int DilateInId = Shader.PropertyToID("_DilateIn");
        private static readonly int DilateOutId = Shader.PropertyToID("_DilateOut");

        private readonly ComputeShader _compute;
        private readonly HeightStampEvaluator.ComputeTarget _target;
        private readonly int _ringKernel;
        private readonly int _apronKernel;
        private readonly int _blurXKernel;
        private readonly int _blurYKernel;
        private readonly int _clearWeightKernel;
        private readonly int _waterWeightKernel;
        private readonly int _applyKernel;
        private ComputeShader _talusCompute;
        private HeightStampEvaluator.ComputeTarget _talusTarget;
        private int _steepKernel;
        private int _seedKernel;
        private int _distanceKernel;
        private int _raiseKernel;
        private int _dilateKernel;
        private int _talusClearWeightKernel;
        private int _talusWaterWeightKernel;
        private int _talusApplyKernel;
        private ErosionStampPass _erosion;
        private readonly Dictionary<(UnityEngine.Terrain, LoogaNeighbourhoodStamp), Ring> _rings = new();
        private readonly List<(UnityEngine.Terrain, LoogaNeighbourhoodStamp)> _deadRings = new();

        // The heights of a ring depend on the mapping, and its layout on the resolution and the depth.
        private struct Ring
        {
            public RenderTexture Texture;
            public HeightMapping Mapping;
            public int Resolution;
            public int Depth;
        }

        internal NeighbourhoodStampPass()
        {
            _compute = Resources.Load<ComputeShader>(ComputeName);
            if (!_compute)
            {
                throw new NotSupportedException("Looga neighbourhood stamps require the " + ComputeName + " compute shader.");
            }
            _target = new HeightStampEvaluator.ComputeTarget(_compute);
            _ringKernel = _compute.FindKernel("ExtractRing");
            _apronKernel = _compute.FindKernel("BuildApron");
            _blurXKernel = _compute.FindKernel("BlurX");
            _blurYKernel = _compute.FindKernel("BlurY");
            _clearWeightKernel = _compute.FindKernel("ClearWeight");
            _waterWeightKernel = _compute.FindKernel("WaterWeight");
            _applyKernel = _compute.FindKernel("ApplySmooth");
        }

        public void Dispose()
        {
            foreach (Ring ring in _rings.Values)
            {
                Release(ring.Texture);
            }
            _rings.Clear();
        }

        /// <summary>Graphics memory of the stored border rings.</summary>
        internal long RingBytes
        {
            get
            {
                long bytes = 0;
                foreach (Ring ring in _rings.Values)
                {
                    bytes += (long)ring.Texture.width * ring.Texture.height * 4;
                }
                return bytes;
            }
        }

        /// <summary>Releases the rings of removed terrains and stamps.</summary>
        internal void ReleaseDeadRings()
        {
            _deadRings.Clear();
            foreach (var key in _rings.Keys)
            {
                if (!key.Item1 || !key.Item2) _deadRings.Add(key);
            }
            foreach (var key in _deadRings)
            {
                Release(_rings[key].Texture);
                _rings.Remove(key);
            }
        }

        /// <summary>True when the tile has a ring for the stamp with this mapping, resolution and reach.</summary>
        /// <param name="halo">Halo of the state: 0 in parity mode.</param>
        internal bool HasRing(in StampTile tile, LoogaNeighbourhoodStamp stamp, HeightMapping mapping, int halo)
        {
            return TryGetRing(tile.Terrain, tile, stamp, mapping, halo, out _);
        }

        /// <summary>Stores the border ring of the tile state before the stamp. It replaces the old one.</summary>
        /// <param name="halo">Halo of the state: 0 in parity mode.</param>
        internal void StoreRing(in StampTile tile, LoogaNeighbourhoodStamp stamp, HeightMapping mapping, int halo,
            RenderTexture state)
        {
            int depth = Depth(tile, stamp, halo);
            var key = (tile.Terrain, stamp);
            if (!_rings.TryGetValue(key, out Ring ring) || ring.Resolution != tile.Resolution || ring.Depth != depth)
            {
                if (ring.Texture) Release(ring.Texture);
                var descriptor = new RenderTextureDescriptor(tile.Resolution, 4 * depth, GraphicsFormat.R32_SFloat, 0)
                {
                    enableRandomWrite = true
                };
                ring.Texture = new RenderTexture(descriptor)
                {
                    name = "Looga Stamps Smooth Ring " + tile.Terrain.name, filterMode = FilterMode.Point
                };
                ring.Texture.Create();
                ring.Resolution = tile.Resolution;
                ring.Depth = depth;
            }
            ring.Mapping = mapping;
            _rings[key] = ring;
            _compute.SetInt(TileResolutionId, tile.Resolution);
            _compute.SetInt(HaloId, halo);
            _compute.SetInt(RingDepthId, depth);
            _compute.SetTexture(_ringKernel, StateId, state);
            _compute.SetTexture(_ringKernel, RingOutId, ring.Texture);
            _compute.Dispatch(_ringKernel, Mathf.CeilToInt(tile.Resolution / 8f), Mathf.CeilToInt(4 * depth / 8f), 1);
        }

        /// <summary>
        /// Builds the apron of the tile state: the state plus the stamp reach on each side, with the border rings of the
        /// neighbours. The caller releases it with RenderTexture.ReleaseTemporary.
        /// </summary>
        /// <param name="halo">Halo of the state: 0 in parity mode.</param>
        /// <param name="neighbours">The tile (slot 4) and its eight neighbours. See LoogaHeightSmooth.compute.</param>
        internal RenderTexture BuildApron(in StampTile tile, LoogaNeighbourhoodStamp stamp, HeightMapping mapping,
            int halo, RenderTexture state, UnityEngine.Terrain[] neighbours)
        {
            int reach = Reach(tile, stamp);
            int target = state.width;
            int apronSize = target + 2 * reach;
            RenderTexture apron = Temporary(apronSize);
            _compute.SetInt(TileResolutionId, tile.Resolution);
            _compute.SetInt(HaloId, halo);
            _compute.SetInt(ReachId, reach);
            _compute.SetInts(TargetSizeId, target, target);
            _compute.SetInts(ApronSizeId, apronSize, apronSize);

            int mask = 0;
            for (int slot = 0; slot < 9; slot++)
            {
                if (slot == 4) continue;
                bool present = TryGetRing(neighbours[slot], tile, stamp, mapping, halo, out Ring ring);
                if (present) mask |= 1 << slot;
                _compute.SetTexture(_apronKernel, RingIds[slot], present ? ring.Texture : state);
            }
            _compute.SetInt(RingMaskId, mask);
            _compute.SetInt(RingDepthId, Depth(tile, stamp, halo));
            _compute.SetTexture(_apronKernel, StateId, state);
            _compute.SetTexture(_apronKernel, ApronId, apron);
            Dispatch(_apronKernel, apronSize);
            return apron;
        }

        /// <param name="halo">Halo of the state: 0 in parity mode.</param>
        /// <param name="neighbours">The tile (slot 4) and its eight neighbours. See LoogaHeightSmooth.compute.</param>
        internal void ApplySmooth(in StampTile tile, LoogaSmoothStamp stamp, HeightMapping mapping, int halo,
            RenderTexture state, UnityEngine.Terrain[] neighbours, SplineDistanceField splineFields,
            WaterDistanceField waterFields)
        {
            float spacing = tile.Size.x / (tile.Resolution - 1);
            int target = state.width;
            int apronSize = target + 2 * Reach(tile, stamp);
            RenderTexture apron = BuildApron(tile, stamp, mapping, halo, state, neighbours);
            RenderTexture blurTemp = Temporary(apronSize);
            RenderTexture blurred = Temporary(apronSize);
            RenderTexture weight = Temporary(target);
            try
            {
                _compute.SetFloat(SigmaTexelsId, stamp.Sigma / spacing);

                _compute.SetTexture(_blurXKernel, ApronInId, apron);
                _compute.SetTexture(_blurXKernel, BlurTempId, blurTemp);
                Dispatch(_blurXKernel, apronSize);
                _compute.SetTexture(_blurYKernel, BlurTempInId, blurTemp);
                _compute.SetTexture(_blurYKernel, BlurredId, blurred);
                Dispatch(_blurYKernel, apronSize);

                _target.Kernel = _waterWeightKernel;
                HeightStampEvaluator.SetTileParameters(_target, tile);
                _compute.SetTexture(_clearWeightKernel, WeightId, weight);
                Dispatch(_clearWeightKernel, target);
                foreach (StampWaterFilter filter in new[] { stamp.WaterFilter, stamp.SecondWaterFilter })
                {
                    if (!filter.Enabled) continue;
                    HeightStampEvaluator.SetWaterFilter(_target, tile, filter, waterFields);
                    _compute.SetTexture(_waterWeightKernel, ApronInId, apron);
                    _compute.SetTexture(_waterWeightKernel, WeightId, weight);
                    Dispatch(_waterWeightKernel, target);
                }

                _target.Kernel = _applyKernel;
                _compute.SetMatrix(TileToStampId, StampMapping.TileToStamp(tile, stamp.transform, mapping));
                HeightStampEvaluator.SetFalloff(_target, tile, stamp.Falloff, splineFields, waterFields);
                _compute.SetFloat(MaxChangeId, stamp.MaxChange / tile.RealHeight);
                _compute.SetFloat(StrengthId, stamp.Strength);
                _compute.SetInt(HeightNoiseTypeId, (int)stamp.HeightNoise.Type);
                _compute.SetVector(HeightNoiseParamsId, stamp.HeightNoise.Parameters);
                bool rim = stamp.KeepAboveWater && stamp.WaterFilter.Area;
                bool underwater = stamp.KeepUnderwater && stamp.WaterFilter.Area;
                _compute.SetInt(KeepAboveWaterId, rim ? 1 : 0);
                _compute.SetInt(KeepUnderwaterId, underwater ? 1 : 0);
                _compute.SetTexture(_applyKernel, RimFieldId,
                    rim || underwater ? waterFields.Get(tile, stamp.WaterFilter.Area) : Texture2D.blackTexture);
                _compute.SetVector(RimFieldScaleId, HeightStampEvaluator.WaterFieldScale(tile));
                _compute.SetFloat(RimWidthId, stamp.RimWidth);
                _compute.SetFloat(WaterClearanceId, stamp.WaterClearance);
                _compute.SetFloat(RimNotFoundId, WaterDistanceField.NotFound * 0.5f);
                _compute.SetTexture(_applyKernel, ApronInId, apron);
                _compute.SetTexture(_applyKernel, BlurredInId, blurred);
                _compute.SetTexture(_applyKernel, WeightInId, weight);
                _compute.SetTexture(_applyKernel, TargetId, state);
                Dispatch(_applyKernel, target);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(apron);
                RenderTexture.ReleaseTemporary(blurTemp);
                RenderTexture.ReleaseTemporary(blurred);
                RenderTexture.ReleaseTemporary(weight);
            }
        }

        /// <param name="halo">Halo of the state: 0 in parity mode.</param>
        /// <param name="neighbours">The tile (slot 4) and its eight neighbours. See LoogaHeightSmooth.compute.</param>
        internal void ApplyTalus(in StampTile tile, LoogaTalusStamp stamp, HeightMapping mapping, int halo,
            RenderTexture state, UnityEngine.Terrain[] neighbours, SplineDistanceField splineFields,
            WaterDistanceField waterFields)
        {
            LoadTalus();
            float spacing = tile.Size.x / (tile.Resolution - 1);
            int reach = Reach(tile, stamp);
            int target = state.width;
            int apronSize = target + 2 * reach;
            RenderTexture apron = BuildApron(tile, stamp, mapping, halo, state, neighbours);
            RenderTexture steep = Temporary(apronSize);
            RenderTexture distanceA = Temporary(apronSize);
            RenderTexture distanceB = Temporary(apronSize);
            RenderTexture raised = Temporary(apronSize);
            RenderTexture dilateA = Temporary(apronSize);
            RenderTexture dilateB = Temporary(apronSize);
            RenderTexture weight = Temporary(target);
            try
            {
                ComputeShader compute = _talusCompute;
                compute.SetInt(TileResolutionId, tile.Resolution);
                compute.SetInt(HaloId, halo);
                compute.SetInt(ReachId, reach);
                compute.SetInts(TargetSizeId, target, target);
                compute.SetInts(ApronSizeId, apronSize, apronSize);
                Vector2 apronOrigin = new Vector2(tile.Position.x, tile.Position.z) -
                                      Vector2.one * ((halo + reach) * spacing);
                compute.SetVector(ApronOriginId, apronOrigin);
                compute.SetFloat(TexelSizeId, spacing);
                compute.SetFloat(SteepSlopeId, Mathf.Tan(stamp.FaceAngle * Mathf.Deg2Rad) * spacing / tile.RealHeight);
                compute.SetFloat(SeedRiseId, 0.1f / tile.RealHeight);
                // A toe has gentle ground within the seed radius: it drops less than the talus slope there. A bench
                // on a face, also a wide one, has the face below it within the radius.
                const float seedRadius = 6f;
                compute.SetInt(SeedRadiusId, Mathf.Max(1, Mathf.RoundToInt(seedRadius / spacing)));
                compute.SetFloat(SeedDropId,
                    seedRadius * Mathf.Tan(stamp.TalusAngle * Mathf.Deg2Rad) / tile.RealHeight);
                compute.SetFloat(MaximumRiseId, stamp.ApronHeight * (1f + stamp.HeightVariation) / tile.RealHeight);
                // The face must rise the minimum face height within the horizontal run of such a face, plus one metre.
                float faceRun = stamp.MinimumFaceHeight / Mathf.Tan(stamp.FaceAngle * Mathf.Deg2Rad) + 1f;
                compute.SetInt(FaceRadiusId, Mathf.Max(1, Mathf.CeilToInt(faceRun / spacing)));
                compute.SetFloat(MinimumFaceRiseId, stamp.MinimumFaceHeight / tile.RealHeight);
                compute.SetFloat(ApronHeightId, stamp.ApronHeight / tile.RealHeight);
                compute.SetFloat(ApronReachTexelsId, stamp.ApronReach / spacing);
                compute.SetFloat(ProfilePowerId, stamp.ProfilePower);
                compute.SetFloat(HeightVariationId, stamp.HeightVariation);
                compute.SetFloat(VariationSizeId, stamp.VariationSize);
                float step = Mathf.Tan(stamp.TalusAngle * Mathf.Deg2Rad) * spacing / tile.RealHeight;
                compute.SetFloat(StepAxisId, step);
                compute.SetFloat(StepDiagonalId, step * Mathf.Sqrt(2f));
                compute.SetFloat(StrengthId, stamp.Strength);
                compute.SetInt(HeightNoiseTypeId, (int)stamp.HeightNoise.Type);
                compute.SetVector(HeightNoiseParamsId, stamp.HeightNoise.Parameters);

                compute.SetTexture(_steepKernel, ApronInId, apron);
                compute.SetTexture(_steepKernel, SteepId, steep);
                Dispatch(compute, _steepKernel, apronSize);
                compute.SetTexture(_seedKernel, ApronInId, apron);
                compute.SetTexture(_seedKernel, SteepInId, steep);
                compute.SetTexture(_seedKernel, DistanceOutId, distanceA);
                Dispatch(compute, _seedKernel, apronSize);

                // Each pass spreads the distance one texel, so the pass count is the apron reach in texels.
                int distancePasses = Mathf.CeilToInt(stamp.ApronReach / spacing) + 1;
                for (int pass = 0; pass < distancePasses; pass++)
                {
                    compute.SetTexture(_distanceKernel, SteepInId, steep);
                    compute.SetTexture(_distanceKernel, DistanceInId, distanceA);
                    compute.SetTexture(_distanceKernel, DistanceOutId, distanceB);
                    Dispatch(compute, _distanceKernel, apronSize);
                    (distanceA, distanceB) = (distanceB, distanceA);
                }

                compute.SetTexture(_raiseKernel, ApronInId, apron);
                compute.SetTexture(_raiseKernel, SteepInId, steep);
                compute.SetTexture(_raiseKernel, DistanceInId, distanceA);
                compute.SetTexture(_raiseKernel, RaisedId, raised);
                Dispatch(compute, _raiseKernel, apronSize);
                Graphics.CopyTexture(raised, dilateA);

                // The raised toe fills the face base: one texel per pass up to the fill reach.
                int fillPasses = Mathf.CeilToInt(stamp.FillReach / spacing) + 1;
                for (int pass = 0; pass < fillPasses; pass++)
                {
                    compute.SetTexture(_dilateKernel, DilateInId, dilateA);
                    compute.SetTexture(_dilateKernel, DilateOutId, dilateB);
                    Dispatch(compute, _dilateKernel, apronSize);
                    (dilateA, dilateB) = (dilateB, dilateA);
                }

                _talusTarget.Kernel = _talusWaterWeightKernel;
                HeightStampEvaluator.SetTileParameters(_talusTarget, tile);
                compute.SetTexture(_talusClearWeightKernel, WeightId, weight);
                Dispatch(compute, _talusClearWeightKernel, target);
                foreach (StampWaterFilter filter in new[] { stamp.WaterFilter, stamp.SecondWaterFilter })
                {
                    if (!filter.Enabled) continue;
                    HeightStampEvaluator.SetWaterFilter(_talusTarget, tile, filter, waterFields);
                    compute.SetTexture(_talusWaterWeightKernel, ApronInId, apron);
                    compute.SetTexture(_talusWaterWeightKernel, WeightId, weight);
                    Dispatch(compute, _talusWaterWeightKernel, target);
                }

                _talusTarget.Kernel = _talusApplyKernel;
                compute.SetMatrix(TileToStampId, StampMapping.TileToStamp(tile, stamp.transform, mapping));
                HeightStampEvaluator.SetFalloff(_talusTarget, tile, stamp.Falloff, splineFields, waterFields);
                compute.SetTexture(_talusApplyKernel, ApronInId, apron);
                compute.SetTexture(_talusApplyKernel, SteepInId, steep);
                compute.SetTexture(_talusApplyKernel, RaisedInId, raised);
                compute.SetTexture(_talusApplyKernel, DilateInId, dilateA);
                compute.SetTexture(_talusApplyKernel, WeightInId, weight);
                compute.SetTexture(_talusApplyKernel, TargetId, state);
                Dispatch(compute, _talusApplyKernel, target);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(apron);
                RenderTexture.ReleaseTemporary(steep);
                RenderTexture.ReleaseTemporary(distanceA);
                RenderTexture.ReleaseTemporary(distanceB);
                RenderTexture.ReleaseTemporary(raised);
                RenderTexture.ReleaseTemporary(dilateA);
                RenderTexture.ReleaseTemporary(dilateB);
                RenderTexture.ReleaseTemporary(weight);
            }
        }

        /// <param name="halo">Halo of the state: 0 in parity mode.</param>
        /// <param name="neighbours">The tile (slot 4) and its eight neighbours. See LoogaHeightSmooth.compute.</param>
        internal void ApplyErosion(in StampTile tile, LoogaErosionStamp stamp, HeightMapping mapping, int halo,
            RenderTexture state, UnityEngine.Terrain[] neighbours, SplineDistanceField splineFields,
            WaterDistanceField waterFields)
        {
            // The erosion kernels load on first use, so worlds without an erosion stamp do not load them.
            _erosion ??= new ErosionStampPass();
            RenderTexture apron = BuildApron(tile, stamp, mapping, halo, state, neighbours);
            try
            {
                _erosion.Apply(tile, stamp, mapping, halo, Reach(tile, stamp), apron, state, splineFields, waterFields);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(apron);
            }
        }

        // The talus kernels load on first use, so worlds without a talus stamp do not load them.
        private void LoadTalus()
        {
            if (_talusCompute) return;
            _talusCompute = Resources.Load<ComputeShader>(TalusComputeName);
            if (!_talusCompute)
            {
                throw new NotSupportedException("Looga talus stamps require the " + TalusComputeName + " compute shader.");
            }
            _talusTarget = new HeightStampEvaluator.ComputeTarget(_talusCompute);
            _steepKernel = _talusCompute.FindKernel("Steepness");
            _seedKernel = _talusCompute.FindKernel("Seed");
            _distanceKernel = _talusCompute.FindKernel("Distance");
            _raiseKernel = _talusCompute.FindKernel("Raise");
            _dilateKernel = _talusCompute.FindKernel("Dilate");
            _talusClearWeightKernel = _talusCompute.FindKernel("ClearWeight");
            _talusWaterWeightKernel = _talusCompute.FindKernel("WaterWeight");
            _talusApplyKernel = _talusCompute.FindKernel("ApplyTalus");
        }

        // A neighbour ring fits when the neighbour has the resolution of the tile. Tiles of a world have one size.
        private bool TryGetRing(UnityEngine.Terrain terrain, in StampTile tile, LoogaNeighbourhoodStamp stamp,
            HeightMapping mapping, int halo, out Ring ring)
        {
            return _rings.TryGetValue((terrain, stamp), out ring) && ring.Texture && ring.Mapping == mapping &&
                ring.Resolution == tile.Resolution && ring.Depth == Depth(tile, stamp, halo);
        }

        // Stamp reach in samples.
        internal static int Reach(in StampTile tile, LoogaNeighbourhoodStamp stamp)
        {
            float spacing = tile.Size.x / (tile.Resolution - 1);
            return Mathf.Max(1, Mathf.CeilToInt(stamp.Reach / spacing));
        }

        // Ring depth in samples: the apron reads neighbour samples within the reach and the halo of the shared edge.
        private static int Depth(in StampTile tile, LoogaNeighbourhoodStamp stamp, int halo)
        {
            return Mathf.Min(tile.Resolution, Reach(tile, stamp) + halo + 1);
        }

        private static void Release(RenderTexture texture)
        {
            if (!texture) return;
            texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static RenderTexture Temporary(int size)
        {
            var descriptor = new RenderTextureDescriptor(size, size, GraphicsFormat.R32_SFloat, 0)
            {
                enableRandomWrite = true
            };
            RenderTexture texture = RenderTexture.GetTemporary(descriptor);
            texture.filterMode = FilterMode.Point;
            return texture;
        }

        private void Dispatch(int kernel, int size)
        {
            Dispatch(_compute, kernel, size);
        }

        private static void Dispatch(ComputeShader compute, int kernel, int size)
        {
            int groups = Mathf.CeilToInt(size / 8f);
            compute.Dispatch(kernel, groups, groups, 1);
        }
    }
}
