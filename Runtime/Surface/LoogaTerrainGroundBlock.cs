using UnityEngine;

namespace LoogaSoft.Terrain.Surface
{
    /// <summary>
    /// The terrain ground values of one terrain for a renderer that blends into the terrain below it (shader include
    /// LoogaTerrainGround.hlsl): the splat control maps, the tile area and, when the terrain draws with a Looga terrain
    /// surface, its layer arrays and layer properties.
    /// </summary>
    public static class LoogaTerrainGroundBlock
    {
        // Eight control maps hold 32 layers, the layer range of the texture stamps and the Looga terrain surface.
        private static readonly int[] GroundControlIds =
        {
            Shader.PropertyToID("_GroundControl0"), Shader.PropertyToID("_GroundControl1"),
            Shader.PropertyToID("_GroundControl2"), Shader.PropertyToID("_GroundControl3"),
            Shader.PropertyToID("_GroundControl4"), Shader.PropertyToID("_GroundControl5"),
            Shader.PropertyToID("_GroundControl6"), Shader.PropertyToID("_GroundControl7")
        };
        private static readonly int GroundTileId = Shader.PropertyToID("_GroundTile");
        private static readonly int GroundControlResolutionId = Shader.PropertyToID("_GroundControlResolution");
        private static readonly int GroundLayerCountId = Shader.PropertyToID("_GroundLayerCount");
        private static readonly int GroundControlPackedId = Shader.PropertyToID("_GroundControlPacked");
        private static readonly int GroundControlIndexId = Shader.PropertyToID("_GroundControlIndex");
        private static readonly int GroundControlWeightId = Shader.PropertyToID("_GroundControlWeight");
        private static readonly int GroundAlbedoArrayId = Shader.PropertyToID("_GroundAlbedoArray");
        private static readonly int GroundNormalArrayId = Shader.PropertyToID("_GroundNormalArray");
        private static readonly int GroundMaskArrayId = Shader.PropertyToID("_GroundMaskArray");
        private static readonly int GroundLayerPropsId = Shader.PropertyToID("_GroundLayerProps");
        private static readonly int GroundLayoutId = Shader.PropertyToID("_GroundLayout");
        private static readonly int GroundSurfaceUVId = Shader.PropertyToID("_GroundSurfaceUV");
        private static readonly int GroundLuminanceId = Shader.PropertyToID("_GroundLuminance");
        private static readonly int GroundStochasticId = Shader.PropertyToID("_GroundStochastic");

        /// <summary>
        /// The ground values of one terrain: its control maps and tile area, and the layer data of its Looga terrain
        /// surface when it has one. Without a surface the material keeps its own layer arrays (MicroSplat layout).
        /// </summary>
        public static MaterialPropertyBlock Create(UnityEngine.Terrain terrain)
        {
            TerrainData terrainData = terrain.terrainData;
            Texture2D[] controls = terrainData.alphamapTextures;
            var block = new MaterialPropertyBlock();
            for (int index = 0; index < GroundControlIds.Length; index++)
            {
                Texture2D control = index < controls.Length ? controls[index] : Texture2D.blackTexture;
                block.SetTexture(GroundControlIds[index], control);
            }
            Vector3 origin = terrain.GetPosition();
            Vector3 size = terrainData.size;
            block.SetVector(GroundTileId, new Vector4(origin.x, origin.z, 1f / size.x, 1f / size.z));
            block.SetFloat(GroundControlResolutionId, terrainData.alphamapResolution);
            block.SetFloat(GroundLayerCountId, Mathf.Min(terrainData.alphamapLayers, controls.Length * 4));
            block.SetVector(GroundControlPackedId, Vector4.zero);
            block.SetTexture(GroundControlIndexId, Texture2D.blackTexture);
            block.SetTexture(GroundControlWeightId, Texture2D.blackTexture);

            if (terrain.TryGetComponent(out LoogaTerrainSurfaceBinding binding) && binding.isActiveAndEnabled
                && binding.IsApplied && binding.Surface && binding.Surface.AlbedoArray && binding.Surface.NormalArray)
            {
                LoogaTerrainSurface surface = binding.Surface;
                bool quality = surface.ArrayLayout == LoogaTerrainSurface.PackingLayout.Quality;
                block.SetTexture(GroundAlbedoArrayId, surface.AlbedoArray);
                block.SetTexture(GroundNormalArrayId, surface.NormalArray);
                // The compact layout has no mask array. The shader does not read it then, but the slot needs an array.
                block.SetTexture(GroundMaskArrayId, quality ? surface.MaskArray : surface.NormalArray);
                block.SetTexture(GroundLayerPropsId, surface.LayerProperties);
                block.SetFloat(GroundLayoutId, quality ? 2f : 1f);
                block.SetVector(GroundSurfaceUVId, new Vector4(surface.WorldUVScale.x, surface.WorldUVScale.y,
                    surface.WorldUVOffset.x, surface.WorldUVOffset.y));
                block.SetVector(GroundLuminanceId, surface.SaturationLuminance);
                block.SetVector(GroundStochasticId, new Vector4(surface.Stochastic ? 1f : 0f,
                    surface.StochasticCellScale, surface.StochasticContrast, 0f));
                surface.ApplyCoverTo(block);
                // The tile values of the surface mesh target, the same as the binding sets for the terrain.
                binding.FillTileValues(block);
                LoogaPackedControls packed = binding.PackedControls;
                if (packed != null)
                {
                    block.SetVector(GroundControlPackedId, new Vector4(1f, packed.MipCount, packed.Index.width,
                        packed.Index.height));
                    block.SetTexture(GroundControlIndexId, packed.Index);
                    block.SetTexture(GroundControlWeightId, packed.Weight);
                }
            }
            return block;
        }

        /// <summary>
        /// The material that draws the Looga terrain surface of the terrain on a mesh over its ground (shader
        /// <see cref="LoogaTerrainSurface.MeshShaderName"/>). The block from <see cref="Create"/> holds its tile values.
        /// Null when the terrain has no applied Looga terrain surface.
        /// </summary>
        public static Material SurfaceMeshMaterial(UnityEngine.Terrain terrain)
        {
            return terrain && terrain.TryGetComponent(out LoogaTerrainSurfaceBinding binding) &&
                   binding.isActiveAndEnabled && binding.IsApplied && binding.Surface
                ? binding.Surface.MeshMaterial
                : null;
        }

        /// <summary>
        /// True when the splat control maps of a block from <see cref="Create"/> still exist. A terrain can replace its
        /// splat map textures and destroy the old ones (seen after a scene save). A block that shows destroyed maps has
        /// no ground layers, so the owner must make it again.
        /// </summary>
        public static bool HasControlMaps(MaterialPropertyBlock block)
        {
            if (block.GetVector(GroundControlPackedId).x > 0f) return block.GetTexture(GroundControlIndexId);

            return block.GetTexture(GroundControlIds[0]);
        }

        /// <summary>
        /// The index of the smallest terrain that holds a world position in X and Z, or -1. A small terrain tile is
        /// more detailed than a large background terrain.
        /// </summary>
        public static int FindTerrain(Vector3 position, UnityEngine.Terrain[] terrains)
        {
            int found = -1;
            float foundArea = float.MaxValue;
            for (int index = 0; index < terrains.Length; index++)
            {
                UnityEngine.Terrain terrain = terrains[index];
                if (!terrain || !terrain.terrainData) continue;
                Vector3 origin = terrain.GetPosition();
                Vector3 size = terrain.terrainData.size;
                if (position.x < origin.x || position.z < origin.z || position.x > origin.x + size.x ||
                    position.z > origin.z + size.z)
                {
                    continue;
                }
                float area = size.x * size.z;
                if (area >= foundArea) continue;
                found = index;
                foundArea = area;
            }
            return found;
        }
    }
}
