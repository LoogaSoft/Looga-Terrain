using System;
using System.Collections.Generic;
using JBooth.MicroSplat;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations
{
    /// <summary>Synchronizes native MicroSplat inputs with an independent Looga surface.</summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(TerrainRenderer))]
    public sealed class MicroSplatTerrainBridge : MonoBehaviour
    {
        private static readonly int[] ControlProperties = CreateControlProperties();
        private static readonly int DynamicStreamControl = Shader.PropertyToID("_DynamicStreamControl");
        private static readonly (string Feature, string[] Textures)[] RequiredModuleTextures =
        {
            ("_GLOBALTINT", new[] { "_GlobalTintTex" }),
            ("_TEXTURECLUSTER3", new[] { "_ClusterNoise", "_ClusterDiffuse2", "_ClusterNormal2",
                "_ClusterDiffuse3", "_ClusterNormal3" }),
            ("_NORMALNOISE2", new[] { "_NormalNoise2" })
        };
        private TerrainRenderer _renderer;
        private MicroSplatTerrain _source;
        private Material _boundMaterial;
        private Material _blendInstance;
        private int _blendRevision = -1;
        private Material _surface;
        private string _featuresTag;
        private readonly HashSet<string> _exportFeatures = new();
        private readonly HashSet<string> _checkedFeatures = new();
        private string _unsupported = string.Empty;

        /// <summary>Gets the current compatibility failure. Empty indicates no blocking failure.</summary>
        public string Diagnostic { get; private set; } = string.Empty;

        #region Built-in
        private void OnEnable()
        {
            _renderer = GetComponent<TerrainRenderer>();
            _renderer.BindMaterialProperties += Bind;
            _renderer.BindDynamicMaterialProperties += BindDynamic;
            _renderer.ValidateIntegrations += ValidateSource;
            ValidateSource();
            _renderer.RefreshCompatibility();
            _renderer.RefreshSurface();
        }

        private void OnDisable()
        {
            if (_source)
            {
                _source.OnMaterialSync -= OnMaterialSync;
            }
            _source = null;
            if (_renderer)
            {
                _renderer.BindMaterialProperties -= Bind;
                _renderer.BindDynamicMaterialProperties -= BindDynamic;
                _renderer.ValidateIntegrations -= ValidateSource;
                _renderer.SetIntegrationBlock(this, null);
                _renderer.SetMaterialSource(null);
            }
            _boundMaterial = null;
        }
        #endregion

        /// <summary>Refreshes native control references after public authoring changes.</summary>
        public void RefreshControls()
        {
            if (_renderer)
            {
                _renderer.RefreshSurface();
            }
        }

        /// <summary>Copies current material values to runtime rendering. Shared exported assets remain unchanged.</summary>
        public void SynchronizeMaterial()
        {
            ValidateSource();
            if (_renderer && _source && string.IsNullOrEmpty(Diagnostic))
            {
                _renderer.SetMaterialSource(_source.matInstance);
                _boundMaterial = _source.matInstance;
            }
        }

        /// <summary>Updates the native object-blend instance after terrain edits. The shared blend asset remains unchanged.</summary>
        public void RefreshTerrainBlending()
        {
            if (!_source || !_source.blendMat || !_renderer || !string.IsNullOrEmpty(Diagnostic)) return;
            TerrainData data = _renderer.Source.terrainData;
            if (!data) return;
            Material blend = _source.GetBlendMatInstance();
            if (!blend || (_blendInstance == blend && _blendRevision == _renderer.SurfaceRevision)) return;
            _source.ApplyBlendMap();
            Texture2D[] controls = data.alphamapTextures;
            for (int index = 0; index < ControlProperties.Length; index++)
            {
                blend.SetTexture(ControlProperties[index], index < controls.Length ? controls[index] : Texture2D.blackTexture);
            }
            var descriptor = _source.GetTerrainDescriptor();
            blend.SetTexture("_TerrainHeightmapTexture", descriptor.heightMap);
            blend.SetTexture("_TerrainNormalmapTexture", descriptor.normalMap);
            blend.SetTexture("_PerPixelNormal", descriptor.normalMap ? descriptor.normalMap : Texture2D.grayTexture);
            blend.SetVector("_TerrainHeightmapScale", descriptor.heightMapScale);
            Vector3 origin = _renderer.Source.transform.position;
            blend.SetVector("_TerrainBounds", new Vector4(origin.x, origin.z, data.size.x, data.size.z));
            _blendInstance = blend;
            _blendRevision = _renderer.SurfaceRevision;
        }

        private void OnMaterialSync(Material material)
        {
            SynchronizeMaterial();
        }

        private void ValidateSource()
        {
            var source = GetComponent<MicroSplatTerrain>();
            if (_source != source)
            {
                if (_source)
                {
                    _source.OnMaterialSync -= OnMaterialSync;
                }
                _source = source;
                if (_source)
                {
                    _source.OnMaterialSync += OnMaterialSync;
                }
                _boundMaterial = null;
                _renderer.SetMaterialSource(null);
            }

            Material surface = _renderer.SurfaceMaterial;
            string tag = surface ? surface.GetTag(MicroSplatCompatibility.FeaturesTag, false, "") : "";
            if (_surface != surface || _featuresTag != tag)
            {
                _surface = surface;
                _featuresTag = tag;
                _exportFeatures.Clear();
                foreach (string feature in tag.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    _exportFeatures.Add(feature);
                }
            }

            Diagnostic = string.Empty;
            if (_renderer.Source.terrainData && _renderer.Source.terrainData.alphamapTextureCount > 8)
            {
                Diagnostic = "This bridge supports at most 32 terrain layers. Use native rendering.";
                _renderer.SetIntegrationBlock(this, Diagnostic);
                return;
            }
            if (_source)
            {
                if (!_source.matInstance || !_source.keywordSO)
                {
                    Diagnostic = "MicroSplat needs a synchronized material and feature asset. Native rendering remains available.";
                }
                else
                {
                    var features = _source.keywordSO.keywords;
                    if (!_checkedFeatures.SetEquals(features))
                    {
                        _checkedFeatures.Clear();
                        _checkedFeatures.UnionWith(features);
                        _unsupported = MicroSplatCompatibility.GetUnsupported(features);
                    }
                    if (!string.IsNullOrEmpty(_unsupported))
                    {
                        Diagnostic = "Unverified MicroSplat features: " + _unsupported + ". Use native rendering.";
                    }
                    else if (TryGetMissingModuleTextures(features, _source.matInstance, out string missing))
                    {
                        Diagnostic = "MicroSplat module textures are unassigned: " + missing + ". Use native rendering.";
                    }
                    else if (string.IsNullOrEmpty(tag))
                    {
                        Diagnostic = "Re-export this MicroSplat surface to add compatibility metadata. Native rendering remains available.";
                    }
                    else if (!_exportFeatures.SetEquals(features)
                        || surface.GetTag(MicroSplatCompatibility.ShaderTag, false, "") != _source.matInstance.shader.name)
                    {
                        Diagnostic = "MicroSplat shader features changed. Re-export the surface and producer before using Looga.";
                    }
                    else if (_boundMaterial != _source.matInstance)
                    {
                        _boundMaterial = _source.matInstance;
                        _renderer.SetMaterialSource(_boundMaterial);
                    }
                }
            }
            _renderer.SetIntegrationBlock(this, Diagnostic);
        }

        private void Bind(MaterialPropertyBlock properties)
        {
            RefreshTerrainBlending();
            TerrainData data = _renderer.Source.terrainData;
            // Read current references after native callbacks, layer replacement, or MicroVerse completion.
            Texture2D[] controls = data.alphamapTextures;
            for (int index = 0; index < ControlProperties.Length; index++)
            {
                properties.SetTexture(ControlProperties[index], index < controls.Length ? controls[index] : Texture2D.blackTexture);
            }
            properties.SetTexture("_TerrainHolesTexture", data.holesTexture ? data.holesTexture : Texture2D.whiteTexture);
            properties.SetTexture("_TerrainHeightmapTexture", data.heightmapTexture);
            Texture normals = _renderer.Source.normalmapTexture;
            properties.SetTexture("_TerrainNormalmapTexture", normals ? normals : Texture2D.grayTexture);
            properties.SetTexture("_PerPixelNormal", normals ? normals : Texture2D.grayTexture);
        }

        private void BindDynamic(MaterialPropertyBlock properties)
        {
            if (_source && _source.matInstance && _source.matInstance.HasProperty(DynamicStreamControl))
            {
                Texture stream = _source.matInstance.GetTexture(DynamicStreamControl);
                properties.SetTexture(DynamicStreamControl, stream ? stream : Texture2D.blackTexture);
            }
        }

        private static int[] CreateControlProperties()
        {
            var properties = new int[8];
            for (int index = 0; index < properties.Length; index++)
            {
                properties[index] = Shader.PropertyToID("_Control" + index);
            }
            return properties;
        }

        private static bool TryGetMissingModuleTextures(IEnumerable<string> features, Material material, out string missing)
        {
            var active = new HashSet<string>(features);
            var properties = new SortedSet<string>(StringComparer.Ordinal);
            foreach ((string feature, string[] textures) in RequiredModuleTextures)
            {
                if (!active.Contains(feature)) continue;
                foreach (string texture in textures)
                {
                    if (!material.HasProperty(texture) || !material.GetTexture(texture))
                    {
                        properties.Add(texture);
                    }
                }
            }
            missing = string.Join(", ", properties);
            return properties.Count > 0;
        }
    }
}
