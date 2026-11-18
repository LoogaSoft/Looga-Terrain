using System;
using System.Collections.Generic;
using JBooth.MicroSplat;
using LoogaSoft.Instancing;
using UnityEngine;
using UnityEngine.Rendering;

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
        private static readonly int[][] RequiredModuleTextureIds = Array.ConvertAll(RequiredModuleTextures,
            module => Array.ConvertAll(module.Textures, Shader.PropertyToID));
        private static int _shaderRevision;
        private TerrainRenderer _renderer;
        private MicroSplatTerrain _source;
        private Material _boundMaterial;
        private Material _blendInstance;
        private int _blendRevision = -1;
        // ValidateSource runs for every camera. Material.GetTag and Object.name allocate strings, so the bridge
        // reads the tags again only after a material, shader, pipeline or shader import change.
        private Material _surface;
        private Shader _surfaceShader;
        private Shader _sourceShader;
        private RenderPipelineAsset _pipeline;
        private int _tagRevision = -1;
        private string _featuresTag;
        private bool _sourceShaderMatches;
        private readonly HashSet<string> _exportFeatures = new();
        private bool _exportMatches;
        private bool _exportMatchesDirty = true;
        private readonly List<string> _checkedKeywords = new();
        private readonly HashSet<string> _checkedFeatures = new();
        private string _unsupported = string.Empty;
        private string _unsupportedDiagnostic = string.Empty;
        private int _missingTextures;
        private string _missingDiagnostic = string.Empty;

        /// <summary>Gets the current compatibility failure. Empty indicates no blocking failure.</summary>
        public string Diagnostic { get; private set; } = string.Empty;
        /// <summary>
        /// Gets the unverified MicroSplat features when they are the reason for native rendering. Otherwise empty.
        /// </summary>
        public string UnverifiedFeatures { get; private set; } = string.Empty;

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

        /// <summary>
        /// Makes every bridge read the shader tags and names again. The editor calls this after a shader import.
        /// </summary>
        public static void MarkShadersChanged()
        {
            _shaderRevision++;
        }

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
            // Another surface system, such as a Looga terrain surface binding, supplies the material. The bridge does
            // not block the renderer and binds nothing.
            if (_renderer.HasSurfaceOverride)
            {
                if (_boundMaterial)
                {
                    _boundMaterial = null;
                    _renderer.SetMaterialSource(null);
                }
                Diagnostic = string.Empty;
                UnverifiedFeatures = string.Empty;
                _renderer.SetIntegrationBlock(this, null);
                return;
            }
            // TryGetComponent makes no editor null-object garbage when the component is missing.
            TryGetComponent(out MicroSplatTerrain source);
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

            RefreshShaderTags(_renderer.SurfaceMaterial,
                _source && _source.matInstance ? _source.matInstance.shader : null);

            Diagnostic = string.Empty;
            UnverifiedFeatures = string.Empty;
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
                    List<string> features = _source.keywordSO.keywords;
                    // An unchanged keyword list keeps the checked set. The set check allocates garbage,
                    // so it runs only after a list change.
                    if (!SameKeywords(_checkedKeywords, features))
                    {
                        _checkedKeywords.Clear();
                        _checkedKeywords.AddRange(features);
                        _exportMatchesDirty = true;
                        if (!_checkedFeatures.SetEquals(features))
                        {
                            _checkedFeatures.Clear();
                            _checkedFeatures.UnionWith(features);
                            _unsupported = MicroSplatCompatibility.GetUnsupported(features);
                            _unsupportedDiagnostic = "Unverified MicroSplat features: " + _unsupported + ". Use native rendering.";
                        }
                    }
                    if (!string.IsNullOrEmpty(_unsupported))
                    {
                        Diagnostic = _unsupportedDiagnostic;
                        UnverifiedFeatures = _unsupported;
                    }
                    else if (TryGetMissingModuleTextures(_source.matInstance, out string missingDiagnostic))
                    {
                        Diagnostic = missingDiagnostic;
                    }
                    else if (string.IsNullOrEmpty(_featuresTag))
                    {
                        Diagnostic = "Re-export this MicroSplat surface to add compatibility metadata. Native rendering remains available.";
                    }
                    else if (!ExportMatchesFeatures() || !_sourceShaderMatches)
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
            if (_renderer.HasSurfaceOverride) return;
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
            if (_renderer.HasSurfaceOverride) return;
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

        private void RefreshShaderTags(Material surface, Shader sourceShader)
        {
            Shader surfaceShader = surface ? surface.shader : null;
            // Material.GetTag reads the active subshader, and the render pipeline selects it.
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (_tagRevision == _shaderRevision && _surface == surface && _surfaceShader == surfaceShader
                && _sourceShader == sourceShader && _pipeline == pipeline) return;
            _tagRevision = _shaderRevision;
            _surface = surface;
            _surfaceShader = surfaceShader;
            _sourceShader = sourceShader;
            _pipeline = pipeline;
            string tag = InstanceShaderInspection.GetTag(surface, MicroSplatCompatibility.FeaturesTag);
            if (_featuresTag != tag)
            {
                _featuresTag = tag;
                _exportFeatures.Clear();
                foreach (string feature in tag.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    _exportFeatures.Add(feature);
                }
                _exportMatchesDirty = true;
            }
            _sourceShaderMatches = sourceShader
                && InstanceShaderInspection.GetTag(surface, MicroSplatCompatibility.ShaderTag) == sourceShader.name;
        }

        // Compares the keyword list with the previous copy in order. It makes no garbage.
        private static bool SameKeywords(List<string> previous, List<string> current)
        {
            if (previous.Count != current.Count) return false;
            for (int index = 0; index < current.Count; index++)
            {
                if (!string.Equals(previous[index], current[index], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private bool ExportMatchesFeatures()
        {
            if (_exportMatchesDirty)
            {
                _exportMatches = _exportFeatures.SetEquals(_checkedFeatures);
                _exportMatchesDirty = false;
            }
            return _exportMatches;
        }

        // Each bit marks one unassigned texture of an active module. The diagnostic changes only with the bits.
        private bool TryGetMissingModuleTextures(Material material, out string diagnostic)
        {
            int missing = 0;
            int bit = 0;
            for (int module = 0; module < RequiredModuleTextures.Length; module++)
            {
                bool active = _checkedFeatures.Contains(RequiredModuleTextures[module].Feature);
                foreach (int texture in RequiredModuleTextureIds[module])
                {
                    if (active && (!material.HasProperty(texture) || !material.GetTexture(texture)))
                    {
                        missing |= 1 << bit;
                    }
                    bit++;
                }
            }
            if (missing != _missingTextures)
            {
                _missingTextures = missing;
                _missingDiagnostic = missing == 0 ? string.Empty
                    : "MicroSplat module textures are unassigned: " + GetTextureNames(missing) + ". Use native rendering.";
            }
            diagnostic = _missingDiagnostic;
            return missing != 0;
        }

        private static string GetTextureNames(int missing)
        {
            var properties = new SortedSet<string>(StringComparer.Ordinal);
            int bit = 0;
            foreach ((string _, string[] textures) in RequiredModuleTextures)
            {
                foreach (string texture in textures)
                {
                    if ((missing & 1 << bit) != 0)
                    {
                        properties.Add(texture);
                    }
                    bit++;
                }
            }
            return string.Join(", ", properties);
        }
    }
}
