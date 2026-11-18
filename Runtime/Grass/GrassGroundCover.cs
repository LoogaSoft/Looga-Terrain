using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LoogaSoft.Terrain.Grass
{
    /// <summary>
    /// The ground cover maps of a <see cref="ProceduralGrassRenderer"/>: the base maps of the other plants with the
    /// procedural grass added on the GPU from the coverage that the renderer draws (LoogaGrassGroundCover.compute). A
    /// grass edit updates the maps in the next frame, without a CPU build. The maps are not saved.
    /// </summary>
    internal sealed class GrassGroundCover : IDisposable
    {
        private const int MaximumTypes = 8;

        private readonly Vector4[] _typeData = new Vector4[MaximumTypes * 2];
        private readonly Vector4[] _cells = new Vector4[4];
        private readonly Vector4[] _info = new Vector4[4];
        private readonly ProceduralGrassRenderer.TerrainState[] _slots = new ProceduralGrassRenderer.TerrainState[4];
        private ComputeShader _compute;
        private int _copyKernel;
        private int _addKernel;
        private GraphicsBuffer _types;
        private Texture2DArray _emptyTerrain;

        // The inputs of the last composition. A change of any of them composes the maps again.
        private Texture _baseCoverage;
        private Texture _baseCanopy;
        private uint _baseCoverageUpdates;
        private uint _baseCanopyUpdates;
        private Vector4 _area;
        private float _colorScale;
        private int _grassVersion;
        private int _composedVersion = -1;
        // Changes since the last composition, for the cache pages.
        private Rect _changedArea;
        private bool _hasChangedArea;
        private bool _changedAll;

        /// <summary>The composed coverage map: R closure, G cast share.</summary>
        internal RenderTexture Coverage { get; private set; }

        /// <summary>The composed canopy map: A own-colour share, RGB own colour times the share.</summary>
        internal RenderTexture Canopy { get; private set; }

        /// <summary>True when the base maps are set and the maps can be composed.</summary>
        internal bool HasBase => _baseCoverage;

        /// <summary>The grass changed: coverage, types or terrains. The next composition adds it again.</summary>
        /// <param name="worldArea">Changed world XZ area. Null: all of the maps.</param>
        internal void MarkGrassChanged(Rect? worldArea = null)
        {
            _grassVersion++;
            if (!worldArea.HasValue)
            {
                _changedAll = true;
                return;
            }
            Rect area = worldArea.Value;
            _changedArea = _hasChangedArea ? Rect.MinMaxRect(Mathf.Min(_changedArea.xMin, area.xMin),
                Mathf.Min(_changedArea.yMin, area.yMin), Mathf.Max(_changedArea.xMax, area.xMax),
                Mathf.Max(_changedArea.yMax, area.yMax)) : area;
            _hasChangedArea = true;
        }

        // Cache pages bake the canopy relief at the composed coverage. The far surface reads the coverage up to about
        // 2.5 mips coarser, so the pages within this many coverage texels of a change are invalid too.
        private const float ChangedTexelMargin = 8f;

        private void NotifyCacheOfChanges(int width)
        {
            if (_changedAll)
            {
                TerrainVirtualTexture.InvalidateWorldArea(null);
            }
            else if (_hasChangedArea)
            {
                float margin = ChangedTexelMargin * _area.z / Mathf.Max(width, 1);
                TerrainVirtualTexture.InvalidateWorldArea(Rect.MinMaxRect(_changedArea.xMin - margin,
                    _changedArea.yMin - margin, _changedArea.xMax + margin, _changedArea.yMax + margin));
            }
            _changedAll = false;
            _hasChangedArea = false;
        }

        /// <summary>Sets the base maps. Returns false when compute shaders or the composition shader are missing.</summary>
        internal bool SetBase(Texture coverage, Texture canopy, Vector4 area, float colorScale)
        {
            if (!EnsureCompute()) return false;
            canopy = canopy ? canopy : Texture2D.blackTexture;
            if (coverage != _baseCoverage || canopy != _baseCanopy || coverage.updateCount != _baseCoverageUpdates ||
                canopy.updateCount != _baseCanopyUpdates || area != _area || !Mathf.Approximately(colorScale, _colorScale))
            {
                _baseCoverage = coverage;
                _baseCanopy = canopy;
                _baseCoverageUpdates = coverage.updateCount;
                _baseCanopyUpdates = canopy.updateCount;
                _area = area;
                _colorScale = colorScale;
                _composedVersion = -1;
                _changedAll = true;
            }
            EnsureTargets(coverage.width, coverage.height);
            return true;
        }

        /// <summary>Composes the maps when the base maps or the grass changed since the last composition.</summary>
        internal void ComposeIfChanged(IReadOnlyList<ProceduralGrassRenderer.TerrainState> terrains,
            IReadOnlyList<ProceduralGrassRenderer.GrassLayer> layers, int typeCount, float maximumDensity, float closure)
        {
            if (!_baseCoverage || !Coverage || _composedVersion == _grassVersion) return;
            _composedVersion = _grassVersion;
            int width = Coverage.width;
            int depth = Coverage.height;
            var grid = new Vector4(_area.x, _area.y, _area.z / width, _area.w / depth);
            typeCount = Mathf.Min(typeCount, MaximumTypes);
            for (int index = 0; index < typeCount; index++)
            {
                layers[index].Type.WriteCoverGpu(_typeData, index, maximumDensity, _colorScale);
            }
            _types.SetData(_typeData);

            _compute.SetTexture(_copyKernel, ShaderIds.BaseCoverage, _baseCoverage);
            _compute.SetTexture(_copyKernel, ShaderIds.BaseCanopy, _baseCanopy);
            _compute.SetTexture(_copyKernel, ShaderIds.Coverage, Coverage);
            _compute.SetTexture(_copyKernel, ShaderIds.Canopy, Canopy);
            _compute.SetVector(ShaderIds.Grid, grid);
            _compute.SetInts(ShaderIds.Size, width, depth, typeCount, 0);
            _compute.SetFloat(ShaderIds.Closure, closure * maximumDensity);
            _compute.Dispatch(_copyKernel, Groups(width), Groups(depth), 1);

            _compute.SetTexture(_addKernel, ShaderIds.BaseCoverage, _baseCoverage);
            _compute.SetTexture(_addKernel, ShaderIds.BaseCanopy, _baseCanopy);
            _compute.SetTexture(_addKernel, ShaderIds.Coverage, Coverage);
            _compute.SetTexture(_addKernel, ShaderIds.Canopy, Canopy);
            _compute.SetBuffer(_addKernel, ShaderIds.GrassTypes, _types);
            if (typeCount > 0)
            {
                foreach (ProceduralGrassRenderer.TerrainState terrain in terrains)
                {
                    AddTerrain(terrain, terrains, grid, width, depth);
                }
            }
            Coverage.GenerateMips();
            Canopy.GenerateMips();
            NotifyCacheOfChanges(width);
        }

        // Adds the grass of the texels whose minimum corner lies on the terrain. The cells of a texel come from the
        // terrain and its neighbours on +X, +Z and +XZ.
        private void AddTerrain(ProceduralGrassRenderer.TerrainState terrain,
            IReadOnlyList<ProceduralGrassRenderer.TerrainState> terrains, Vector4 grid, int width, int depth)
        {
            Rect rect = terrain.Rect;
            int x0 = Mathf.Max(0, Mathf.CeilToInt((rect.xMin - grid.x) / grid.z));
            int z0 = Mathf.Max(0, Mathf.CeilToInt((rect.yMin - grid.y) / grid.w));
            int x1 = Mathf.Min(width, Mathf.CeilToInt((rect.xMax - grid.x) / grid.z));
            int z1 = Mathf.Min(depth, Mathf.CeilToInt((rect.yMax - grid.y) / grid.w));
            if (x1 <= x0 || z1 <= z0) return;
            _slots[0] = terrain;
            _slots[1] = Neighbour(terrains, rect.xMax, rect.yMin);
            _slots[2] = Neighbour(terrains, rect.xMin, rect.yMax);
            _slots[3] = Neighbour(terrains, rect.xMax, rect.yMax);
            for (int slot = 0; slot < 4; slot++)
            {
                ProceduralGrassRenderer.TerrainState state = _slots[slot];
                bool present = state != null && state.Coverage;
                int resolution = present ? state.Coverage.width : 1;
                _compute.SetTexture(_addKernel, ShaderIds.Terrains[slot], present ? state.Coverage : _emptyTerrain);
                _cells[slot] = present
                    ? new Vector4(state.Rect.xMin, state.Rect.yMin, state.Rect.width / resolution,
                        state.Rect.height / resolution)
                    : Vector4.zero;
                _info[slot] = new Vector4(resolution, present ? 1f : 0f, 0f, 0f);
            }
            _compute.SetVectorArray(ShaderIds.TerrainCells, _cells);
            _compute.SetVectorArray(ShaderIds.TerrainInfo, _info);
            _compute.SetInts(ShaderIds.Rect, x0, z0, x1 - x0, z1 - z0);
            _compute.Dispatch(_addKernel, Groups(x1 - x0), Groups(z1 - z0), 1);
        }

        // The terrain whose minimum corner is at a point, or null.
        private static ProceduralGrassRenderer.TerrainState Neighbour(
            IReadOnlyList<ProceduralGrassRenderer.TerrainState> terrains, float x, float z)
        {
            foreach (ProceduralGrassRenderer.TerrainState terrain in terrains)
            {
                if (Mathf.Abs(terrain.Rect.xMin - x) < 0.01f && Mathf.Abs(terrain.Rect.yMin - z) < 0.01f) return terrain;
            }
            return null;
        }

        private static int Groups(int texels)
        {
            return Mathf.Max(1, (texels + 7) / 8);
        }

        private bool EnsureCompute()
        {
            if (_compute) return true;
            if (!SystemInfo.supportsComputeShaders) return false;
            _compute = Resources.Load<ComputeShader>("LoogaGrassGroundCover");
            if (!_compute) return false;
            _copyKernel = _compute.FindKernel("CopyBase");
            _addKernel = _compute.FindKernel("AddGrass");
            _types = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaximumTypes * 2, 16);
            _emptyTerrain = new Texture2DArray(1, 1, 1, TextureFormat.R8, false, true)
            {
                name = "Looga Grass Cover Empty Terrain", hideFlags = HideFlags.HideAndDontSave
            };
            _emptyTerrain.Apply(false, true);
            return true;
        }

        private void EnsureTargets(int width, int depth)
        {
            if (Coverage && Coverage.width == width && Coverage.height == depth) return;
            ReleaseTargets();
            // Linear, clamped and trilinear with box-filtered mipmaps, as the base maps.
            Coverage = CreateTarget("Looga Grass Ground Cover Coverage", width, depth, GraphicsFormat.R8G8_UNorm);
            Canopy = CreateTarget("Looga Grass Ground Cover Canopy", width, depth, GraphicsFormat.R8G8B8A8_UNorm);
            _composedVersion = -1;
        }

        private static RenderTexture CreateTarget(string name, int width, int depth, GraphicsFormat format)
        {
            var descriptor = new RenderTextureDescriptor(width, depth, format, 0)
            {
                enableRandomWrite = true, useMipMap = true, autoGenerateMips = false, sRGB = false
            };
            var target = new RenderTexture(descriptor)
            {
                name = name, hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear
            };
            target.Create();
            return target;
        }

        private void ReleaseTargets()
        {
            foreach (RenderTexture target in new[] { Coverage, Canopy })
            {
                if (target)
                {
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
            }
            Coverage = null;
            Canopy = null;
        }

        public void Dispose()
        {
            ReleaseTargets();
            _types?.Release();
            _types = null;
            if (_emptyTerrain)
            {
                UnityEngine.Object.DestroyImmediate(_emptyTerrain);
            }
            _emptyTerrain = null;
            _compute = null;
            _baseCoverage = null;
            _baseCanopy = null;
        }

        private static class ShaderIds
        {
            internal static readonly int BaseCoverage = Shader.PropertyToID("_CoverBaseCoverage");
            internal static readonly int BaseCanopy = Shader.PropertyToID("_CoverBaseCanopy");
            internal static readonly int Coverage = Shader.PropertyToID("_CoverCoverage");
            internal static readonly int Canopy = Shader.PropertyToID("_CoverCanopy");
            internal static readonly int GrassTypes = Shader.PropertyToID("_CoverGrassTypes");
            internal static readonly int TerrainCells = Shader.PropertyToID("_CoverTerrainCells");
            internal static readonly int TerrainInfo = Shader.PropertyToID("_CoverTerrainInfo");
            internal static readonly int Grid = Shader.PropertyToID("_CoverGrid");
            internal static readonly int Size = Shader.PropertyToID("_CoverSize");
            internal static readonly int Rect = Shader.PropertyToID("_CoverRect");
            internal static readonly int Closure = Shader.PropertyToID("_CoverClosure");
            internal static readonly int[] Terrains =
            {
                Shader.PropertyToID("_CoverTerrain0"), Shader.PropertyToID("_CoverTerrain1"),
                Shader.PropertyToID("_CoverTerrain2"), Shader.PropertyToID("_CoverTerrain3")
            };
        }
    }
}
