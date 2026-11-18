using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// GPU simulation of a <see cref="LoogaErosionStamp"/> on one tile. The grid is aligned to the world heightmap
    /// samples, so each tile that runs it gets the same cells from the same heights. See LoogaErosion.compute.
    /// </summary>
    internal sealed class ErosionStampPass
    {
        private const string ComputeName = "LoogaErosion";
        // Steps between two submissions of the queued GPU work, so one submission never holds many steps.
        private const int StepsPerFlush = 50;

        private static readonly int TileResolutionId = Shader.PropertyToID("_TileResolution");
        private static readonly int HaloId = Shader.PropertyToID("_Halo");
        private static readonly int ReachId = Shader.PropertyToID("_Reach");
        private static readonly int TargetSizeId = Shader.PropertyToID("_TargetSize");
        private static readonly int ApronInId = Shader.PropertyToID("_ApronIn");
        private static readonly int TargetId = Shader.PropertyToID("_Target");
        private static readonly int GridSizeId = Shader.PropertyToID("_GridSize");
        private static readonly int GridApronOffsetId = Shader.PropertyToID("_GridApronOffset");
        private static readonly int CellTexelsId = Shader.PropertyToID("_CellTexels");
        private static readonly int CellSizeId = Shader.PropertyToID("_CellSize");
        private static readonly int GridOriginId = Shader.PropertyToID("_GridOrigin");
        private static readonly int TargetOriginId = Shader.PropertyToID("_TargetOrigin");
        private static readonly int TexelSizeId = Shader.PropertyToID("_TexelSize");
        private static readonly int TimeStepId = Shader.PropertyToID("_TimeStep");
        private static readonly int RainId = Shader.PropertyToID("_Rain");
        private static readonly int EvaporationId = Shader.PropertyToID("_Evaporation");
        private static readonly int CapacityId = Shader.PropertyToID("_Capacity");
        private static readonly int DissolveId = Shader.PropertyToID("_Dissolve");
        private static readonly int DepositId = Shader.PropertyToID("_Deposit");
        private static readonly int DepthLimitId = Shader.PropertyToID("_DepthLimit");
        private static readonly int MinimumTiltId = Shader.PropertyToID("_MinimumTilt");
        private static readonly int HardnessErosionId = Shader.PropertyToID("_HardnessErosion");
        private static readonly int ThermalRateId = Shader.PropertyToID("_ThermalRate");
        private static readonly int CreepId = Shader.PropertyToID("_Creep");
        private static readonly int SoftTalusId = Shader.PropertyToID("_SoftTalus");
        private static readonly int HardTalusId = Shader.PropertyToID("_HardTalus");
        private static readonly int HardnessScaleId = Shader.PropertyToID("_HardnessScale");
        private static readonly int HardnessContrastId = Shader.PropertyToID("_HardnessContrast");
        private static readonly int SeedId = Shader.PropertyToID("_Seed");
        private static readonly int MaxChangeId = Shader.PropertyToID("_MaxChange");
        private static readonly int StrengthId = Shader.PropertyToID("_Strength");
        private static readonly int TileToStampId = Shader.PropertyToID("_TileToStamp");
        private static readonly int GroundId = Shader.PropertyToID("_Ground");
        private static readonly int GroundInId = Shader.PropertyToID("_GroundIn");
        private static readonly int InitialId = Shader.PropertyToID("_Initial");
        private static readonly int InitialInId = Shader.PropertyToID("_InitialIn");
        private static readonly int HardnessId = Shader.PropertyToID("_Hardness");
        private static readonly int HardnessInId = Shader.PropertyToID("_HardnessIn");
        private static readonly int WaterOutId = Shader.PropertyToID("_WaterOut");
        private static readonly int WaterInId = Shader.PropertyToID("_WaterIn");
        private static readonly int FluxOutId = Shader.PropertyToID("_FluxOut");
        private static readonly int FluxInId = Shader.PropertyToID("_FluxIn");
        private static readonly int VelocityOutId = Shader.PropertyToID("_VelocityOut");
        private static readonly int VelocityInId = Shader.PropertyToID("_VelocityIn");
        private static readonly int SedimentOutId = Shader.PropertyToID("_SedimentOut");
        private static readonly int SedimentInId = Shader.PropertyToID("_SedimentIn");
        private static readonly int ThermalAId = Shader.PropertyToID("_ThermalA");
        private static readonly int ThermalBId = Shader.PropertyToID("_ThermalB");
        private static readonly int ThermalAInId = Shader.PropertyToID("_ThermalAIn");
        private static readonly int ThermalBInId = Shader.PropertyToID("_ThermalBIn");
        private static readonly int ValleyErosionId = Shader.PropertyToID("_ValleyErosion");
        private static readonly int ValleyCapacityId = Shader.PropertyToID("_ValleyCapacity");
        private static readonly int ValleyDepositId = Shader.PropertyToID("_ValleyDeposit");
        private static readonly int AreaExponentId = Shader.PropertyToID("_AreaExponent");
        private static readonly int FlowSpreadId = Shader.PropertyToID("_FlowSpread");
        private static readonly int RouteOutId = Shader.PropertyToID("_RouteOut");
        private static readonly int RouteInId = Shader.PropertyToID("_RouteIn");
        private static readonly int FlowAId = Shader.PropertyToID("_FlowA");
        private static readonly int FlowBId = Shader.PropertyToID("_FlowB");
        private static readonly int FlowAInId = Shader.PropertyToID("_FlowAIn");
        private static readonly int FlowBInId = Shader.PropertyToID("_FlowBIn");
        private static readonly int SteepestId = Shader.PropertyToID("_Steepest");
        private static readonly int SteepestInId = Shader.PropertyToID("_SteepestIn");

        private readonly ComputeShader _compute;
        private readonly HeightStampEvaluator.ComputeTarget _target;
        private readonly int _extractKernel;
        private readonly int _fluxKernel;
        private readonly int _waterKernel;
        private readonly int _erodeKernel;
        private readonly int _transportKernel;
        private readonly int _thermalOutKernel;
        private readonly int _thermalInKernel;
        private readonly int _flowWeightsKernel;
        private readonly int _routeKernel;
        private readonly int _inciseKernel;
        private readonly int _applyKernel;

        internal ErosionStampPass()
        {
            _compute = Resources.Load<ComputeShader>(ComputeName);
            if (!_compute)
            {
                throw new NotSupportedException("Looga erosion stamps require the " + ComputeName + " compute shader.");
            }
            _target = new HeightStampEvaluator.ComputeTarget(_compute);
            _extractKernel = _compute.FindKernel("Extract");
            _fluxKernel = _compute.FindKernel("Flux");
            _waterKernel = _compute.FindKernel("Water");
            _erodeKernel = _compute.FindKernel("Erode");
            _transportKernel = _compute.FindKernel("Transport");
            _thermalOutKernel = _compute.FindKernel("ThermalOut");
            _thermalInKernel = _compute.FindKernel("ThermalIn");
            _flowWeightsKernel = _compute.FindKernel("FlowWeights");
            _routeKernel = _compute.FindKernel("Route");
            _inciseKernel = _compute.FindKernel("Incise");
            _applyKernel = _compute.FindKernel("ApplyErosion");
        }

        /// <param name="halo">Halo of the state: 0 in parity mode.</param>
        /// <param name="reach">Apron reach in samples.</param>
        /// <param name="apron">Heights before the stamp over the state plus the reach on each side.</param>
        internal void Apply(in StampTile tile, LoogaErosionStamp stamp, HeightMapping mapping, int halo, int reach,
            RenderTexture apron, RenderTexture state, SplineDistanceField splineFields, WaterDistanceField waterFields)
        {
            // The stamp world also passes tiles within the reach of the area. The stamp changes nothing on them.
            if (!tile.WorldRect.Overlaps(stamp.Area)) return;
            float spacing = tile.Size.x / (tile.Resolution - 1);
            int cellTexels = Mathf.Max(1, Mathf.RoundToInt(stamp.CellSize / spacing));
            float cellSize = cellTexels * spacing;
            int apronSize = apron.width;
            // Global sample indices: world position / spacing. Tiles of a world share the sample lattice.
            Vector2 apronOrigin = new Vector2(tile.Position.x, tile.Position.z) - Vector2.one * ((halo + reach) * spacing);
            int apronX = Mathf.RoundToInt(apronOrigin.x / spacing);
            int apronY = Mathf.RoundToInt(apronOrigin.y / spacing);
            Rect simulated = stamp.SimulatedArea;
            int x0 = FloorToMultiple(Mathf.FloorToInt(simulated.xMin / spacing), cellTexels);
            int y0 = FloorToMultiple(Mathf.FloorToInt(simulated.yMin / spacing), cellTexels);
            int x1 = CeilToMultiple(Mathf.CeilToInt(simulated.xMax / spacing), cellTexels);
            int y1 = CeilToMultiple(Mathf.CeilToInt(simulated.yMax / spacing), cellTexels);
            // The reach keeps the grid inside the apron. A grid that does not fit is cut, and the tiles then disagree.
            int clampedX0 = Mathf.Max(x0, CeilToMultiple(apronX, cellTexels));
            int clampedY0 = Mathf.Max(y0, CeilToMultiple(apronY, cellTexels));
            int clampedX1 = Mathf.Min(x1, FloorToMultiple(apronX + apronSize, cellTexels));
            int clampedY1 = Mathf.Min(y1, FloorToMultiple(apronY + apronSize, cellTexels));
            if (clampedX0 != x0 || clampedY0 != y0 || clampedX1 != x1 || clampedY1 != y1)
            {
                Debug.LogWarning("Looga erosion stamp '" + stamp.name + "': the simulated area does not fit the apron of " +
                    tile.Terrain.name + ". The tile seams can disagree.", stamp);
            }
            int width = (clampedX1 - clampedX0) / cellTexels;
            int height = (clampedY1 - clampedY0) / cellTexels;
            if (width < 3 || height < 3) return;

            RenderTexture groundA = Temporary(width, height, GraphicsFormat.R32_SFloat);
            RenderTexture groundB = Temporary(width, height, GraphicsFormat.R32_SFloat);
            RenderTexture initial = Temporary(width, height, GraphicsFormat.R32_SFloat);
            RenderTexture hardness = Temporary(width, height, GraphicsFormat.R32_SFloat);
            RenderTexture waterA = Temporary(width, height, GraphicsFormat.R32_SFloat);
            RenderTexture waterB = Temporary(width, height, GraphicsFormat.R32_SFloat);
            RenderTexture fluxA = Temporary(width, height, GraphicsFormat.R32G32B32A32_SFloat);
            RenderTexture fluxB = Temporary(width, height, GraphicsFormat.R32G32B32A32_SFloat);
            RenderTexture velocity = Temporary(width, height, GraphicsFormat.R32G32_SFloat);
            RenderTexture sedimentA = Temporary(width, height, GraphicsFormat.R32_SFloat);
            RenderTexture sedimentB = Temporary(width, height, GraphicsFormat.R32_SFloat);
            RenderTexture thermalA = Temporary(width, height, GraphicsFormat.R32G32B32A32_SFloat);
            RenderTexture thermalB = Temporary(width, height, GraphicsFormat.R32G32B32A32_SFloat);
            RenderTexture routeA = Temporary(width, height, GraphicsFormat.R32G32_SFloat);
            RenderTexture routeB = Temporary(width, height, GraphicsFormat.R32G32_SFloat);
            RenderTexture flowA = Temporary(width, height, GraphicsFormat.R32G32B32A32_SFloat);
            RenderTexture flowB = Temporary(width, height, GraphicsFormat.R32G32B32A32_SFloat);
            RenderTexture steepest = Temporary(width, height, GraphicsFormat.R32_SFloat);
            try
            {
                ComputeShader compute = _compute;
                HeightStampEvaluator.SetTileParameters(_target, tile);
                compute.SetInt(TileResolutionId, tile.Resolution);
                compute.SetInt(HaloId, halo);
                compute.SetInt(ReachId, reach);
                compute.SetInts(TargetSizeId, state.width, state.height);
                compute.SetInts(GridSizeId, width, height);
                compute.SetInts(GridApronOffsetId, clampedX0 - apronX, clampedY0 - apronY);
                compute.SetInt(CellTexelsId, cellTexels);
                compute.SetFloat(CellSizeId, cellSize);
                compute.SetVector(GridOriginId,
                    new Vector2((clampedX0 + 0.5f * (cellTexels - 1)) * spacing,
                        (clampedY0 + 0.5f * (cellTexels - 1)) * spacing));
                compute.SetVector(TargetOriginId,
                    new Vector2(tile.Position.x, tile.Position.z) - Vector2.one * (halo * spacing));
                compute.SetFloat(TexelSizeId, spacing);
                compute.SetFloat(TimeStepId, stamp.TimeStep);
                compute.SetFloat(RainId, stamp.Rain);
                compute.SetFloat(EvaporationId, stamp.Evaporation);
                compute.SetFloat(CapacityId, stamp.Capacity);
                compute.SetFloat(DissolveId, stamp.Dissolve);
                compute.SetFloat(DepositId, stamp.Deposit);
                compute.SetFloat(DepthLimitId, stamp.DepthLimit);
                compute.SetFloat(MinimumTiltId, stamp.MinimumTilt);
                compute.SetFloat(HardnessErosionId, stamp.HardnessErosion);
                compute.SetFloat(ThermalRateId, stamp.ThermalRate);
                compute.SetFloat(CreepId, stamp.Creep);
                compute.SetFloat(SoftTalusId, stamp.SoftTalus);
                compute.SetFloat(HardTalusId, stamp.HardTalus);
                compute.SetFloat(HardnessScaleId, stamp.HardnessScale);
                compute.SetFloat(HardnessContrastId, stamp.HardnessContrast);
                compute.SetFloat(SeedId, stamp.Seed);
                compute.SetFloat(MaxChangeId, stamp.MaxChange);
                compute.SetFloat(StrengthId, stamp.Strength);
                compute.SetFloat(ValleyErosionId, stamp.ValleyErosion);
                compute.SetFloat(ValleyCapacityId, stamp.ValleyCapacity);
                compute.SetFloat(ValleyDepositId, stamp.ValleyDeposit);
                compute.SetFloat(AreaExponentId, stamp.AreaExponent);
                compute.SetFloat(FlowSpreadId, stamp.FlowSpread);

                compute.SetTexture(_extractKernel, ApronInId, apron);
                compute.SetTexture(_extractKernel, GroundId, groundA);
                compute.SetTexture(_extractKernel, InitialId, initial);
                compute.SetTexture(_extractKernel, HardnessId, hardness);
                compute.SetTexture(_extractKernel, WaterOutId, waterA);
                compute.SetTexture(_extractKernel, FluxOutId, fluxA);
                compute.SetTexture(_extractKernel, VelocityOutId, velocity);
                compute.SetTexture(_extractKernel, SedimentOutId, sedimentA);
                compute.SetTexture(_extractKernel, RouteOutId, routeA);
                Dispatch(_extractKernel, width, height);

                // Thermal erosion and soil creep of one step.
                void Thermal()
                {
                    compute.SetTexture(_thermalOutKernel, GroundInId, groundA);
                    compute.SetTexture(_thermalOutKernel, HardnessInId, hardness);
                    compute.SetTexture(_thermalOutKernel, ThermalAId, thermalA);
                    compute.SetTexture(_thermalOutKernel, ThermalBId, thermalB);
                    Dispatch(_thermalOutKernel, width, height);
                    compute.SetTexture(_thermalInKernel, GroundInId, groundA);
                    compute.SetTexture(_thermalInKernel, InitialInId, initial);
                    compute.SetTexture(_thermalInKernel, ThermalAInId, thermalA);
                    compute.SetTexture(_thermalInKernel, ThermalBInId, thermalB);
                    compute.SetTexture(_thermalInKernel, GroundId, groundB);
                    Dispatch(_thermalInKernel, width, height);
                    (groundA, groundB) = (groundB, groundA);
                }

                // Valley pass: the first step routes until the flow can cross the grid, later steps start from the
                // previous drainage and only follow its changes.
                compute.SetFloat(TimeStepId, stamp.ValleyTimeStep);
                for (int step = 0; step < stamp.ValleySteps; step++)
                {
                    compute.SetTexture(_flowWeightsKernel, GroundInId, groundA);
                    compute.SetTexture(_flowWeightsKernel, FlowAId, flowA);
                    compute.SetTexture(_flowWeightsKernel, FlowBId, flowB);
                    compute.SetTexture(_flowWeightsKernel, SteepestId, steepest);
                    Dispatch(_flowWeightsKernel, width, height);

                    int passes = step == 0 ? width + height : stamp.ValleyPasses;
                    for (int pass = 0; pass < passes; pass++)
                    {
                        compute.SetTexture(_routeKernel, RouteInId, routeA);
                        compute.SetTexture(_routeKernel, FlowAInId, flowA);
                        compute.SetTexture(_routeKernel, FlowBInId, flowB);
                        compute.SetTexture(_routeKernel, SteepestInId, steepest);
                        compute.SetTexture(_routeKernel, HardnessInId, hardness);
                        compute.SetTexture(_routeKernel, RouteOutId, routeB);
                        Dispatch(_routeKernel, width, height);
                        (routeA, routeB) = (routeB, routeA);
                    }

                    compute.SetTexture(_inciseKernel, GroundInId, groundA);
                    compute.SetTexture(_inciseKernel, InitialInId, initial);
                    compute.SetTexture(_inciseKernel, RouteInId, routeA);
                    compute.SetTexture(_inciseKernel, FlowAInId, flowA);
                    compute.SetTexture(_inciseKernel, FlowBInId, flowB);
                    compute.SetTexture(_inciseKernel, SteepestInId, steepest);
                    compute.SetTexture(_inciseKernel, HardnessInId, hardness);
                    compute.SetTexture(_inciseKernel, GroundId, groundB);
                    Dispatch(_inciseKernel, width, height);
                    (groundA, groundB) = (groundB, groundA);
                    Thermal();
                    GL.Flush();
                }
                compute.SetFloat(TimeStepId, stamp.TimeStep);

                for (int step = 0; step < stamp.Iterations; step++)
                {
                    // Flux from the ground and the rained water.
                    compute.SetTexture(_fluxKernel, GroundInId, groundA);
                    compute.SetTexture(_fluxKernel, WaterInId, waterA);
                    compute.SetTexture(_fluxKernel, FluxInId, fluxA);
                    compute.SetTexture(_fluxKernel, FluxOutId, fluxB);
                    Dispatch(_fluxKernel, width, height);
                    (fluxA, fluxB) = (fluxB, fluxA);

                    // Water depth and velocity after the flux.
                    compute.SetTexture(_waterKernel, WaterInId, waterA);
                    compute.SetTexture(_waterKernel, FluxInId, fluxA);
                    compute.SetTexture(_waterKernel, WaterOutId, waterB);
                    compute.SetTexture(_waterKernel, VelocityOutId, velocity);
                    Dispatch(_waterKernel, width, height);
                    (waterA, waterB) = (waterB, waterA);

                    // Erosion and deposition.
                    compute.SetTexture(_erodeKernel, GroundInId, groundA);
                    compute.SetTexture(_erodeKernel, InitialInId, initial);
                    compute.SetTexture(_erodeKernel, HardnessInId, hardness);
                    compute.SetTexture(_erodeKernel, WaterInId, waterA);
                    compute.SetTexture(_erodeKernel, VelocityInId, velocity);
                    compute.SetTexture(_erodeKernel, SedimentInId, sedimentA);
                    compute.SetTexture(_erodeKernel, GroundId, groundB);
                    compute.SetTexture(_erodeKernel, SedimentOutId, sedimentB);
                    Dispatch(_erodeKernel, width, height);
                    (groundA, groundB) = (groundB, groundA);
                    (sedimentA, sedimentB) = (sedimentB, sedimentA);

                    // Sediment transport and evaporation.
                    compute.SetTexture(_transportKernel, SedimentInId, sedimentA);
                    compute.SetTexture(_transportKernel, VelocityInId, velocity);
                    compute.SetTexture(_transportKernel, WaterInId, waterA);
                    compute.SetTexture(_transportKernel, SedimentOutId, sedimentB);
                    compute.SetTexture(_transportKernel, WaterOutId, waterB);
                    Dispatch(_transportKernel, width, height);
                    (sedimentA, sedimentB) = (sedimentB, sedimentA);
                    (waterA, waterB) = (waterB, waterA);

                    Thermal();

                    if ((step + 1) % StepsPerFlush == 0) GL.Flush();
                }

                _target.Kernel = _applyKernel;
                compute.SetMatrix(TileToStampId, StampMapping.TileToStamp(tile, stamp.transform, mapping));
                HeightStampEvaluator.SetFalloff(_target, tile, stamp.Falloff, splineFields, waterFields);
                compute.SetTexture(_applyKernel, ApronInId, apron);
                compute.SetTexture(_applyKernel, GroundInId, groundA);
                compute.SetTexture(_applyKernel, InitialInId, initial);
                compute.SetTexture(_applyKernel, TargetId, state);
                Dispatch(_applyKernel, state.width, state.height);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(groundA);
                RenderTexture.ReleaseTemporary(groundB);
                RenderTexture.ReleaseTemporary(initial);
                RenderTexture.ReleaseTemporary(hardness);
                RenderTexture.ReleaseTemporary(waterA);
                RenderTexture.ReleaseTemporary(waterB);
                RenderTexture.ReleaseTemporary(fluxA);
                RenderTexture.ReleaseTemporary(fluxB);
                RenderTexture.ReleaseTemporary(velocity);
                RenderTexture.ReleaseTemporary(sedimentA);
                RenderTexture.ReleaseTemporary(sedimentB);
                RenderTexture.ReleaseTemporary(thermalA);
                RenderTexture.ReleaseTemporary(thermalB);
                RenderTexture.ReleaseTemporary(routeA);
                RenderTexture.ReleaseTemporary(routeB);
                RenderTexture.ReleaseTemporary(flowA);
                RenderTexture.ReleaseTemporary(flowB);
                RenderTexture.ReleaseTemporary(steepest);
            }
        }

        private static int FloorToMultiple(int value, int multiple)
        {
            return Mathf.FloorToInt(value / (float)multiple) * multiple;
        }

        private static int CeilToMultiple(int value, int multiple)
        {
            return Mathf.CeilToInt(value / (float)multiple) * multiple;
        }

        private static RenderTexture Temporary(int width, int height, GraphicsFormat format)
        {
            var descriptor = new RenderTextureDescriptor(width, height, format, 0) { enableRandomWrite = true };
            RenderTexture texture = RenderTexture.GetTemporary(descriptor);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        private void Dispatch(int kernel, int width, int height)
        {
            _compute.Dispatch(kernel, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);
        }
    }
}
