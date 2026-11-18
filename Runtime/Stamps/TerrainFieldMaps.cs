using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Measures the <see cref="TerrainField"/> maps of one tile on the GPU from the heights of the tile and its eight
    /// neighbours. The maps of the last tile stay until another tile is measured or until <see cref="Release"/>, so the
    /// texture and the placement passes of one tile measure it once. The flow map of each tile stays in a cache until
    /// heights change within <see cref="FlowReach"/> of the tile.
    /// </summary>
    internal sealed class TerrainFieldMaps : IDisposable
    {
        private const string ComputeName = "LoogaTerrainFields";

        /// <summary>Largest distance of the face search, in metres. Height changes affect the fields this far.</summary>
        internal const float Reach = 48f;

        /// <summary>
        /// Distance past the tile from which the flow fields collect water, in metres. Height changes affect the flow
        /// fields this far. Upstream areas that start farther away are cut.
        /// </summary>
        internal const float FlowReach = 512f;
        // Flow cells are about this size, in whole heightmap samples.
        private const float FlowCell = 2f;

        // Soil slopes can stand at about 50 degrees, so only steeper ground counts as a rock face.
        private const float FaceAngle = 58f;
        private const float MinimumRise = 1f;
        private const float SmallRadius = 4f;
        private const float LargeRadius = 20f;
        // Field texels are about this size, in whole heightmap samples.
        private const float TargetCell = 1f;

        private static readonly int FieldSizeId = Shader.PropertyToID("_FieldSize");
        private static readonly int ApronId = Shader.PropertyToID("_Apron");
        private static readonly int GroundSizeId = Shader.PropertyToID("_GroundSize");
        private static readonly int CellId = Shader.PropertyToID("_Cell");
        private static readonly int TexelOriginId = Shader.PropertyToID("_TexelOrigin");
        private static readonly int FacesInId = Shader.PropertyToID("_FacesIn");
        private static readonly int SlotsId = Shader.PropertyToID("_Slots");
        private static readonly int GroundHeightsId = Shader.PropertyToID("_GroundHeights");
        private static readonly int GroundHeightsInId = Shader.PropertyToID("_GroundHeightsIn");
        private static readonly int GroundId = Shader.PropertyToID("_Ground");
        private static readonly int GroundInId = Shader.PropertyToID("_GroundIn");
        private static readonly int ShapeId = Shader.PropertyToID("_Shape");
        private static readonly int FacesId = Shader.PropertyToID("_Faces");
        private static readonly int FaceAngleId = Shader.PropertyToID("_FaceAngle");
        private static readonly int MinimumRiseId = Shader.PropertyToID("_MinimumRise");
        private static readonly int ReachId = Shader.PropertyToID("_Reach");
        private static readonly int SmallRadiusId = Shader.PropertyToID("_SmallRadius");
        private static readonly int LargeRadiusId = Shader.PropertyToID("_LargeRadius");
        private static readonly int SurfaceId = Shader.PropertyToID("_Surface");
        private static readonly int WaterInId = Shader.PropertyToID("_WaterIn");
        private static readonly int SurfaceSizeId = Shader.PropertyToID("_SurfaceSize");
        private static readonly int SurfaceOriginId = Shader.PropertyToID("_SurfaceOrigin");
        private static readonly int SurfaceEndId = Shader.PropertyToID("_SurfaceEnd");
        private static readonly int[] HeightsIds =
        {
            Shader.PropertyToID("_Heights0"), Shader.PropertyToID("_Heights1"), Shader.PropertyToID("_Heights2"),
            Shader.PropertyToID("_Heights3"), Shader.PropertyToID("_Heights4"), Shader.PropertyToID("_Heights5"),
            Shader.PropertyToID("_Heights6"), Shader.PropertyToID("_Heights7"), Shader.PropertyToID("_Heights8")
        };

        // Filter uniforms of the raster passes (TerrainFieldFilter.hlsl).
        private static readonly int FieldShapeId = Shader.PropertyToID("_TerrainFieldShape");
        private static readonly int FieldFacesId = Shader.PropertyToID("_TerrainFieldFaces");
        private static readonly int FieldWaterId = Shader.PropertyToID("_TerrainFieldWater");
        private static readonly int FieldPlacementsId = Shader.PropertyToID("_TerrainFieldPlacements");
        private static readonly int FieldFilterSettingsId = Shader.PropertyToID("_FieldFilterSettings");
        private static readonly int FieldFilterRangesId = Shader.PropertyToID("_FieldFilterRanges");
        private static readonly int FieldFilterNoiseId = Shader.PropertyToID("_FieldFilterNoise");

        /// <summary>The two field maps of one tile. See TerrainField for the channels.</summary>
        internal sealed class TileFields
        {
            internal UnityEngine.Terrain Terrain;
            /// <summary>R steepness, G relative height 4 m, B relative height 20 m, A northness.</summary>
            internal RenderTexture Shape;
            /// <summary>R face above distance, G face above angle, B face below distance, A face below drop.</summary>
            internal RenderTexture Faces;
            /// <summary>R log10 upstream area, G wetness index. Null until a flow field is used.</summary>
            internal Texture2D Water;

            internal void Release()
            {
                RenderTexture.ReleaseTemporary(Shape);
                RenderTexture.ReleaseTemporary(Faces);
                Shape = null;
                Faces = null;
                Water = null;
            }
        }

        private sealed class WaterEntry
        {
            internal Texture2D Texture;
            // World XZ area whose heights the flow read.
            internal Rect Window;
        }

        private readonly ComputeShader _compute;
        private readonly int _gatherKernel;
        private readonly int _steepnessKernel;
        private readonly int _fieldsKernel;
        private readonly int _blurKernel;
        private readonly int _surfaceKernel;
        private readonly Vector4[] _slots = new Vector4[9];
        // Filter uniforms. Only the main thread binds filters.
        private static readonly Vector4[] FilterSettings = new Vector4[2];
        private static readonly Vector4[] FilterRanges = new Vector4[2];
        private static readonly Vector4[] FilterNoise = new Vector4[2];
        private TileFields _current;
        private readonly Dictionary<UnityEngine.Terrain, WaterEntry> _water = new();
        private readonly List<UnityEngine.Terrain> _stale = new();

        internal TerrainFieldMaps()
        {
            _compute = Resources.Load<ComputeShader>(ComputeName);
            if (!_compute || !SystemInfo.supportsComputeShaders)
            {
                throw new NotSupportedException("Looga terrain fields require compute shaders and " + ComputeName + ".");
            }
            _gatherKernel = _compute.FindKernel("Gather");
            _steepnessKernel = _compute.FindKernel("Steepness");
            _fieldsKernel = _compute.FindKernel("Fields");
            _blurKernel = _compute.FindKernel("BlurFaces");
            _surfaceKernel = _compute.FindKernel("SurfaceFields");
        }

        /// <summary>CPU time of the last measurement, in milliseconds. Timing checks read it.</summary>
        internal static double LastMeasureMilliseconds { get; private set; }

        /// <summary>CPU time of the last flow measurement, with the GPU readback, in milliseconds.</summary>
        internal static double LastFlowMilliseconds { get; private set; }

        public void Dispose()
        {
            Release();
            ClearFlow();
        }

        /// <summary>The field maps of a tile. The maps stay valid until another tile is measured or Release.</summary>
        /// <param name="heights">Current heights of a terrain of the world, or null for other terrains.</param>
        /// <param name="flow">Also gives the flow map of the tile.</param>
        internal TileFields Get(in StampTile tile, Func<UnityEngine.Terrain, RenderTexture> heights, bool flow = false)
        {
            if (_current == null || _current.Terrain != tile.Terrain)
            {
                Release();
                _current = Measure(tile, heights);
            }
            if (flow && _current != null && !_current.Water)
            {
                _current.Water = GetFlow(tile, heights);
            }
            return _current;
        }

        /// <summary>Drops the cached flow maps of the tiles whose flow read heights in the world XZ region.</summary>
        internal void InvalidateFlow(Rect region)
        {
            _stale.Clear();
            foreach (KeyValuePair<UnityEngine.Terrain, WaterEntry> pair in _water)
            {
                if (!pair.Key || pair.Value.Window.Overlaps(region))
                {
                    _stale.Add(pair.Key);
                }
            }
            foreach (UnityEngine.Terrain terrain in _stale)
            {
                Texture2D texture = _water[terrain].Texture;
                if (_current != null && _current.Water == texture)
                {
                    _current.Water = null;
                }
                DestroyTexture(texture);
                _water.Remove(terrain);
            }
        }

        /// <summary>Drops all cached flow maps.</summary>
        internal void ClearFlow()
        {
            foreach (WaterEntry entry in _water.Values)
            {
                DestroyTexture(entry.Texture);
            }
            _water.Clear();
            if (_current != null)
            {
                _current.Water = null;
            }
        }

        /// <summary>Releases the maps of the last tile.</summary>
        internal void Release()
        {
            _current?.Release();
            _current = null;
        }

        /// <summary>Measures new maps of a tile. The caller releases them with TileFields.Release.</summary>
        internal TileFields Measure(in StampTile tile, Func<UnityEngine.Terrain, RenderTexture> heights)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            RenderTexture own = heights(tile.Terrain);
            if (!own) return null;
            int step = Mathf.Max(1, Mathf.RoundToInt(TargetCell * (tile.Resolution - 1) / tile.Size.x));
            int fieldSize = (tile.Resolution - 1) / step + 1;
            float cell = tile.Size.x / fieldSize;
            int apron = Mathf.CeilToInt(Reach / cell) + 2;
            int groundSize = fieldSize + 2 * apron;

            BindNeighbours(tile.Terrain, heights, own);
            _compute.SetInt(FieldSizeId, fieldSize);
            _compute.SetInt(ApronId, apron);
            _compute.SetInt(GroundSizeId, groundSize);
            _compute.SetFloat(CellId, cell);
            _compute.SetInts(TexelOriginId, Mathf.RoundToInt(tile.Position.x / cell),
                Mathf.RoundToInt(tile.Position.z / cell));
            _compute.SetFloat(FaceAngleId, FaceAngle);
            _compute.SetFloat(MinimumRiseId, MinimumRise);
            _compute.SetFloat(ReachId, Reach);
            _compute.SetFloat(SmallRadiusId, SmallRadius);
            _compute.SetFloat(LargeRadiusId, LargeRadius);

            RenderTexture groundHeights = CreateMap(groundSize, GraphicsFormat.R32_SFloat, "Looga Fields Ground Heights");
            RenderTexture ground = CreateMap(groundSize, GraphicsFormat.R32G32_SFloat, "Looga Fields Ground");
            RenderTexture rawFaces = CreateMap(fieldSize, GraphicsFormat.R16G16B16A16_SFloat, "Looga Fields Raw Faces");
            var fields = new TileFields
            {
                Terrain = tile.Terrain,
                Shape = CreateMap(fieldSize, GraphicsFormat.R16G16B16A16_SFloat, "Looga Fields Shape"),
                Faces = CreateMap(fieldSize, GraphicsFormat.R16G16B16A16_SFloat, "Looga Fields Faces")
            };
            try
            {
                int groundGroups = Mathf.CeilToInt(groundSize / 8f);
                _compute.SetTexture(_gatherKernel, GroundHeightsId, groundHeights);
                _compute.Dispatch(_gatherKernel, groundGroups, groundGroups, 1);

                _compute.SetTexture(_steepnessKernel, GroundHeightsInId, groundHeights);
                _compute.SetTexture(_steepnessKernel, GroundId, ground);
                _compute.Dispatch(_steepnessKernel, groundGroups, groundGroups, 1);

                int fieldGroups = Mathf.CeilToInt(fieldSize / 8f);
                _compute.SetTexture(_fieldsKernel, GroundInId, ground);
                _compute.SetTexture(_fieldsKernel, ShapeId, fields.Shape);
                _compute.SetTexture(_fieldsKernel, FacesId, rawFaces);
                _compute.Dispatch(_fieldsKernel, fieldGroups, fieldGroups, 1);

                _compute.SetTexture(_blurKernel, FacesInId, rawFaces);
                _compute.SetTexture(_blurKernel, FacesId, fields.Faces);
                _compute.Dispatch(_blurKernel, fieldGroups, fieldGroups, 1);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(groundHeights);
                RenderTexture.ReleaseTemporary(ground);
                RenderTexture.ReleaseTemporary(rawFaces);
            }
            LastMeasureMilliseconds = timer.Elapsed.TotalMilliseconds;
            return fields;
        }

        /// <summary>
        /// Writes texels of the surface field map of a tile. It measures the relative heights directly, without the
        /// face search of <see cref="Measure"/>.
        /// </summary>
        /// <returns>False when the tile has no heights.</returns>
        internal bool WriteSurfaceFields(in StampTile tile, Func<UnityEngine.Terrain, RenderTexture> heights,
            RenderTexture target, RectInt texels)
        {
            RenderTexture own = heights(tile.Terrain);
            Texture2D water = GetFlow(tile, heights);
            if (!own || !water) return false;
            int step = Mathf.Max(1, Mathf.RoundToInt(TargetCell * (tile.Resolution - 1) / tile.Size.x));
            int fieldSize = (tile.Resolution - 1) / step + 1;
            float cell = tile.Size.x / fieldSize;
            int apron = Mathf.CeilToInt(LargeRadius / cell) + 2;
            int groundSize = fieldSize + 2 * apron;

            BindNeighbours(tile.Terrain, heights, own);
            _compute.SetInt(FieldSizeId, fieldSize);
            _compute.SetInt(ApronId, apron);
            _compute.SetInt(GroundSizeId, groundSize);
            _compute.SetFloat(CellId, cell);
            _compute.SetFloat(SmallRadiusId, SmallRadius);
            _compute.SetFloat(LargeRadiusId, LargeRadius);
            _compute.SetInt(SurfaceSizeId, target.width);
            _compute.SetInts(SurfaceOriginId, texels.xMin, texels.yMin);
            _compute.SetInts(SurfaceEndId, texels.xMax, texels.yMax);

            RenderTexture groundHeights = CreateMap(groundSize, GraphicsFormat.R32_SFloat, "Looga Fields Ground Heights");
            RenderTexture ground = CreateMap(groundSize, GraphicsFormat.R32G32_SFloat, "Looga Fields Ground");
            try
            {
                int groundGroups = Mathf.CeilToInt(groundSize / 8f);
                _compute.SetTexture(_gatherKernel, GroundHeightsId, groundHeights);
                _compute.Dispatch(_gatherKernel, groundGroups, groundGroups, 1);

                _compute.SetTexture(_steepnessKernel, GroundHeightsInId, groundHeights);
                _compute.SetTexture(_steepnessKernel, GroundId, ground);
                _compute.Dispatch(_steepnessKernel, groundGroups, groundGroups, 1);

                _compute.SetTexture(_surfaceKernel, GroundInId, ground);
                _compute.SetTexture(_surfaceKernel, WaterInId, water);
                _compute.SetTexture(_surfaceKernel, SurfaceId, target, 0);
                _compute.Dispatch(_surfaceKernel, Mathf.CeilToInt(texels.width / 8f),
                    Mathf.CeilToInt(texels.height / 8f), 1);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(groundHeights);
                RenderTexture.ReleaseTemporary(ground);
            }
            return true;
        }

        // The flow map of a tile from the cache, or a new one: the heights of the tile and the flow reach around it go
        // to the CPU on a coarser grid, and TerrainFlow routes the water.
        private Texture2D GetFlow(in StampTile tile, Func<UnityEngine.Terrain, RenderTexture> heights)
        {
            if (_water.TryGetValue(tile.Terrain, out WaterEntry entry) && entry.Texture) return entry.Texture;
            RenderTexture own = heights(tile.Terrain);
            if (!own) return null;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int step = Mathf.Max(1, Mathf.RoundToInt(FlowCell * (tile.Resolution - 1) / tile.Size.x));
            int flowSize = (tile.Resolution - 1) / step + 1;
            float cell = tile.Size.x / flowSize;
            int apron = Mathf.CeilToInt(FlowReach / cell) + 1;
            int groundSize = flowSize + 2 * apron;

            BindNeighbours(tile.Terrain, heights, own);
            _compute.SetInt(FieldSizeId, flowSize);
            _compute.SetInt(ApronId, apron);
            _compute.SetInt(GroundSizeId, groundSize);
            RenderTexture ground = CreateMap(groundSize, GraphicsFormat.R32_SFloat, "Looga Fields Flow Ground");
            var groundHeights = new NativeArray<float>(groundSize * groundSize, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            var output = new NativeArray<ushort>(flowSize * flowSize * 2, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            try
            {
                int groups = Mathf.CeilToInt(groundSize / 8f);
                _compute.SetTexture(_gatherKernel, GroundHeightsId, ground);
                _compute.Dispatch(_gatherKernel, groups, groups, 1);
                AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref groundHeights, ground, 0);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    throw new InvalidOperationException("GPU readback of the flow heights failed for " +
                        tile.Terrain.name + ".");
                }
                TerrainFlow.Run(groundHeights, groundSize, apron, cell, output);
                var texture = new Texture2D(flowSize, flowSize, TextureFormat.RGHalf, false, true)
                {
                    name = "Looga Fields Flow " + tile.Terrain.name,
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                texture.SetPixelData(output, 0);
                texture.Apply(false, true);
                Rect tileRect = tile.WorldRect;
                _water[tile.Terrain] = new WaterEntry
                {
                    Texture = texture,
                    Window = Rect.MinMaxRect(tileRect.xMin - FlowReach, tileRect.yMin - FlowReach,
                        tileRect.xMax + FlowReach, tileRect.yMax + FlowReach)
                };
                LastFlowMilliseconds = timer.Elapsed.TotalMilliseconds;
                return texture;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(ground);
                groundHeights.Dispose();
                output.Dispose();
            }
        }

        private static void DestroyTexture(Texture2D texture)
        {
            if (!texture) return;
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(texture);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        /// <summary>
        /// Sets the field filter uniforms of a raster pass. A filter whose map is missing is off: null fields turn the
        /// shape and flow filters off, a null placement map the placement filters. Every pass that shares the material
        /// must call it.
        /// </summary>
        internal static void SetFilters(Material material, TerrainFieldFilter first, TerrainFieldFilter second,
            TileFields fields, Texture placements = null)
        {
            SetFilter(0, Available(first, fields, placements) ? first : null);
            SetFilter(1, Available(second, fields, placements) ? second : null);
            material.SetVectorArray(FieldFilterSettingsId, FilterSettings);
            material.SetVectorArray(FieldFilterRangesId, FilterRanges);
            material.SetVectorArray(FieldFilterNoiseId, FilterNoise);
            material.SetTexture(FieldShapeId, fields != null && fields.Shape ? fields.Shape : Texture2D.blackTexture);
            material.SetTexture(FieldFacesId, fields != null && fields.Faces ? fields.Faces : Texture2D.blackTexture);
            material.SetTexture(FieldWaterId, fields != null && fields.Water ? fields.Water : Texture2D.blackTexture);
            material.SetTexture(FieldPlacementsId, placements ? placements : Texture2D.blackTexture);
        }

        private static bool Available(TerrainFieldFilter filter, TileFields fields, Texture placements)
        {
            if (filter is not { Enabled: true }) return false;
            return IsPlacementField(filter.Field) ? (bool)placements : fields != null;
        }

        /// <summary>True for the fields that come from placements, not from the heights.</summary>
        internal static bool IsPlacementField(TerrainField field)
        {
            return field >= TerrainField.Canopy;
        }

        /// <summary>True when one of the filters reads a field that the heights give.</summary>
        internal static bool Uses(TerrainFieldFilter first, TerrainFieldFilter second)
        {
            return IsHeightField(first) || IsHeightField(second);
        }

        /// <summary>True when one of the filters reads a placement field.</summary>
        internal static bool UsesPlacements(TerrainFieldFilter first, TerrainFieldFilter second)
        {
            return IsPlacement(first) || IsPlacement(second);
        }

        private static bool IsHeightField(TerrainFieldFilter filter)
        {
            return filter is { Enabled: true } && !IsPlacementField(filter.Field);
        }

        private static bool IsPlacement(TerrainFieldFilter filter)
        {
            return filter is { Enabled: true } && IsPlacementField(filter.Field);
        }

        /// <summary>True when an enabled filter reads a flow field.</summary>
        internal static bool UsesFlow(TerrainFieldFilter first, TerrainFieldFilter second)
        {
            return IsFlow(first) || IsFlow(second);
        }

        /// <summary>Distance in metres over which height changes move the fields of the filters, or 0.</summary>
        internal static float ReachOf(TerrainFieldFilter first, TerrainFieldFilter second)
        {
            if (UsesFlow(first, second)) return FlowReach;
            return Uses(first, second) ? Reach : 0f;
        }

        private static bool IsFlow(TerrainFieldFilter filter)
        {
            return filter is { Enabled: true } && filter.Field is TerrainField.FlowArea or TerrainField.Wetness;
        }

        // Settings: x = 1 if enabled, y = field index, z = weight, w = noise type. Ranges: xy range, zw smoothness.
        private static void SetFilter(int index, TerrainFieldFilter filter)
        {
            bool enabled = filter is { Enabled: true };
            FilterSettings[index] = enabled
                ? new Vector4(1f, (int)filter.Field, filter.Weight, (int)filter.Noise.Type)
                : Vector4.zero;
            FilterRanges[index] = enabled
                ? new Vector4(filter.Range.x, filter.Range.y, filter.Smoothness.x, filter.Smoothness.y)
                : Vector4.zero;
            FilterNoise[index] = enabled ? filter.Noise.Parameters : Vector4.zero;
        }

        // Slots row by row from the bottom left: slot 4 is the tile, slot 5 its right neighbour, slot 7 its top one.
        private void BindNeighbours(UnityEngine.Terrain terrain, Func<UnityEngine.Terrain, RenderTexture> heights,
            RenderTexture own)
        {
            UnityEngine.Terrain left = terrain.leftNeighbor;
            UnityEngine.Terrain right = terrain.rightNeighbor;
            UnityEngine.Terrain bottom = terrain.bottomNeighbor;
            UnityEngine.Terrain top = terrain.topNeighbor;
            UnityEngine.Terrain[] slots =
            {
                Diagonal(bottom, left, false), bottom, Diagonal(bottom, right, false),
                left, terrain, right,
                Diagonal(top, left, true), top, Diagonal(top, right, true)
            };
            for (int i = 0; i < slots.Length; i++)
            {
                RenderTexture slotHeights = slots[i] ? heights(slots[i]) : null;
                bool has = slotHeights;
                TerrainData data = has ? slots[i].terrainData : terrain.terrainData;
                Vector3 position = has ? slots[i].transform.position : terrain.transform.position;
                _compute.SetTexture(_gatherKernel, HeightsIds[i], has ? slotHeights : own);
                _slots[i] = new Vector4(position.y, data.heightmapScale.y * 2f, data.heightmapResolution, has ? 1f : 0f);
            }
            _compute.SetVectorArray(SlotsId, _slots);

            // The corner tile through the vertical neighbour, else through the side neighbour.
            UnityEngine.Terrain Diagonal(UnityEngine.Terrain vertical, UnityEngine.Terrain side, bool above)
            {
                if (vertical)
                {
                    UnityEngine.Terrain corner = side == left ? vertical.leftNeighbor : vertical.rightNeighbor;
                    if (corner) return corner;
                }
                if (!side) return null;
                return above ? side.topNeighbor : side.bottomNeighbor;
            }
        }

        private static RenderTexture CreateMap(int size, GraphicsFormat format, string name)
        {
            var descriptor = new RenderTextureDescriptor(size, size, format, 0) { enableRandomWrite = true };
            RenderTexture map = RenderTexture.GetTemporary(descriptor);
            map.name = name;
            map.filterMode = FilterMode.Bilinear;
            map.wrapMode = TextureWrapMode.Clamp;
            return map;
        }
    }
}
