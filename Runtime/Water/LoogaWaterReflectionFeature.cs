using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Gives the Looga water shader screen-space reflections. The feature builds a closest-depth pyramid after the
    /// opaque objects. The water traces one reflection ray for each pixel through the pyramid. Where the ray leaves
    /// the screen or finds nothing, the water uses the reflection probes and the sky. It also draws the soft depth
    /// (_LoogaSoftDepth): the depth of passes with the LightMode tag LoogaSoftDepth, such as the jets of a fall sheet
    /// (Looga/Water fall mode), before the transparent objects. Looga/Water Mist fades out in front of it. With Surface
    /// Depth on, the transparent objects after the water read a camera depth texture with the water surfaces in it,
    /// for example a height fog overlay, which otherwise fogs the water by the distance to the ground under it.
    /// </summary>
    public sealed class LoogaWaterReflectionFeature : ScriptableRendererFeature
    {
        private const string ComputeName = "LoogaWaterDepthPyramid";
        // Level 0 is the camera depth texture. Coarser levels than 7 skip too much detail and save few steps.
        private const int MaximumLevels = 8;

        private static readonly int PyramidId = Shader.PropertyToID("_LoogaWaterHiZ");
        private static readonly int PyramidSizeId = Shader.PropertyToID("_LoogaWaterHiZSize");
        private static readonly int ReflectionId = Shader.PropertyToID("_LoogaWaterReflection");
        private static readonly int SourceId = Shader.PropertyToID("_Source");
        private static readonly int DestinationId = Shader.PropertyToID("_Destination");
        private static readonly int ReductionSizeId = Shader.PropertyToID("_ReductionSize");
        private static readonly int SourceMipId = Shader.PropertyToID("_SourceMip");
        private static readonly int ReversedId = Shader.PropertyToID("_Reversed");
        private static readonly int SoftDepthId = Shader.PropertyToID("_LoogaSoftDepth");
        private static readonly int SoftDepthOnId = Shader.PropertyToID("_LoogaSoftDepthOn");
        private static readonly int CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int OpaqueDepthId = Shader.PropertyToID("_LoogaOpaqueDepth");
        private static readonly int OpaqueDepthOnId = Shader.PropertyToID("_LoogaOpaqueDepthOn");
        private const string SurfaceDepthShaderName = "LoogaWaterSurfaceDepth";

        [Tooltip("Compute shader that builds the depth pyramid. Empty = the package shader.")]
        [SerializeField] private ComputeShader _depthPyramid;

        [Header("Screen-Space Reflections")]
        [Tooltip("Off = the water reflects only the reflection probes and the sky.")]
        [SerializeField] private bool _screenSpaceReflections = true;
        [Tooltip("Maximum trace steps for each water pixel. More steps find more far hits and cost more.")]
        [SerializeField, Range(8, 128)] private int _maximumSteps = 32;
        [Tooltip("Depth behind a surface that still counts as a hit, in meters. 1 % of the distance adds to it. " +
                 "Larger = fewer gaps, more smear behind thin objects.")]
        [SerializeField, Min(0.01f)] private float _thickness = 1.5f;
        [Tooltip("Share of the screen at each edge where the reflection fades into the probe reflection.")]
        [SerializeField, Range(0.01f, 0.5f)] private float _edgeFade = 0.08f;
        [Tooltip("Maximum length of a reflection ray, in meters.")]
        [SerializeField, Min(1)] private float _maximumDistance = 2000;

        [Header("Surface Depth")]
        [Tooltip("The transparent objects after the water read a camera depth texture with the water surfaces in it, " +
                 "for example a height fog overlay. Off = they read the depth of the opaque objects, so they treat " +
                 "the water as the ground under it.")]
        [SerializeField] private bool _surfaceDepth;

        private PyramidPass _pyramidPass;
        private SoftDepthPass _softDepthPass;
        private SurfaceDepthPass _surfaceDepthPass;
        private Material _surfaceDepthMaterial;
        private ResetPass _resetPass;
        private TimingPass _timingBegin;
        private TimingPass _timingEnd;

        /// <summary>Turns the screen-space reflections on or off for all cameras of this renderer.</summary>
        public bool ScreenSpaceReflections
        {
            get => _screenSpaceReflections;
            set => _screenSpaceReflections = value;
        }

        /// <summary>
        /// Diagnostics. Times all transparent objects of game cameras on the GPU in the sampler
        /// "Looga Water.Transparents". The URP transparent pass gives no GPU time of its own.
        /// </summary>
        public bool MeasureTransparents { get; set; }

        #region Built-in
        public override void Create()
        {
            _pyramidPass = new PyramidPass();
            _softDepthPass = new SoftDepthPass();
            _surfaceDepthPass = new SurfaceDepthPass();
            _resetPass = new ResetPass();
            var sampler = CustomSampler.Create("Looga Water.Transparents", true);
            _timingBegin = new TimingPass(sampler, true);
            _timingEnd = new TimingPass(sampler, false);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection) return;

            // The reset pass runs for every camera, so no camera uses the pyramid of another camera.
            renderer.EnqueuePass(_resetPass);
            renderer.EnqueuePass(_softDepthPass);
            if (MeasureTransparents && cameraType == CameraType.Game)
            {
                renderer.EnqueuePass(_timingBegin);
                renderer.EnqueuePass(_timingEnd);
            }
            _resetPass.RestoreDepth = false;
            ComputeShader shader = _depthPyramid ? _depthPyramid : Resources.Load<ComputeShader>(ComputeName);
            if (_screenSpaceReflections && shader && SystemInfo.supportsComputeShaders)
            {
                _pyramidPass.Shader = shader;
                _pyramidPass.Settings = new Vector4(_maximumSteps, _thickness, _edgeFade, _maximumDistance);
                renderer.EnqueuePass(_pyramidPass);
            }
            // After the pyramid, which reads the opaque depth.
            if (_surfaceDepth && RequireSurfaceDepthMaterial())
            {
                _surfaceDepthPass.Material = _surfaceDepthMaterial;
                _resetPass.RestoreDepth = true;
                renderer.EnqueuePass(_surfaceDepthPass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_surfaceDepthMaterial);
            _surfaceDepthMaterial = null;
        }
        #endregion

        private bool RequireSurfaceDepthMaterial()
        {
            if (_surfaceDepthMaterial) return true;

            var shader = Resources.Load<Shader>(SurfaceDepthShaderName);
            if (!shader) return false;

            _surfaceDepthMaterial = CoreUtils.CreateEngineMaterial(shader);
            return _surfaceDepthMaterial;
        }

        private sealed class PyramidPass : ScriptableRenderPass
        {
            internal ComputeShader Shader;
            internal Vector4 Settings;

            public PyramidPass()
            {
                // After the copy of the opaque depth and color, before the water draws.
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
                profilingSampler = new ProfilingSampler("Looga Water.Depth Pyramid");
                ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color);
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                UniversalResourceData resources = frame.Get<UniversalResourceData>();
                UniversalCameraData camera = frame.Get<UniversalCameraData>();
                TextureHandle depth = resources.cameraDepthTexture;
                if (!depth.IsValid()) return;

                int width = camera.cameraTargetDescriptor.width;
                int height = camera.cameraTargetDescriptor.height;
                if (width < 4 || height < 4) return;

                int levels = Mathf.Min(MaximumLevels, 1 + Mathf.FloorToInt(Mathf.Log(Mathf.Min(width, height), 2)));
                var description = new TextureDesc(width >> 1, height >> 1)
                {
                    format = GraphicsFormat.R32_SFloat,
                    enableRandomWrite = true,
                    useMipMap = true,
                    autoGenerateMips = false,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "Looga Water Depth Pyramid"
                };
                TextureHandle pyramid = graph.CreateTexture(description);

                using var builder = graph.AddUnsafePass("Looga Water.Depth Pyramid", out PassData data,
                    profilingSampler);
                data.Shader = Shader;
                data.Kernel = Shader.FindKernel("Reduce");
                data.Depth = depth;
                data.Pyramid = pyramid;
                data.Width = width;
                data.Height = height;
                data.Levels = levels;
                data.Settings = Settings;
                builder.UseTexture(depth, AccessFlags.Read);
                builder.UseTexture(pyramid, AccessFlags.ReadWrite);
                builder.SetGlobalTextureAfterPass(pyramid, PyramidId);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData pass, UnsafeGraphContext context) => Build(pass, context));
            }

            // Level 1 reduces the depth texture. Each next level reduces the level before it.
            private static void Build(PassData pass, UnsafeGraphContext context)
            {
                CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                cmd.SetComputeIntParam(pass.Shader, ReversedId, SystemInfo.usesReversedZBuffer ? 1 : 0);
                int sourceWidth = pass.Width;
                int sourceHeight = pass.Height;
                for (int level = 1; level < pass.Levels; level++)
                {
                    int width = Mathf.Max(1, sourceWidth >> 1);
                    int height = Mathf.Max(1, sourceHeight >> 1);
                    if (level == 1)
                    {
                        cmd.SetComputeTextureParam(pass.Shader, pass.Kernel, SourceId, pass.Depth);
                    }
                    else
                    {
                        cmd.SetComputeTextureParam(pass.Shader, pass.Kernel, SourceId, pass.Pyramid);
                    }
                    cmd.SetComputeIntParam(pass.Shader, SourceMipId, Mathf.Max(0, level - 2));
                    cmd.SetComputeIntParams(pass.Shader, ReductionSizeId, sourceWidth, sourceHeight, width, height);
                    cmd.SetComputeTextureParam(pass.Shader, pass.Kernel, DestinationId, pass.Pyramid, level - 1);
                    cmd.DispatchCompute(pass.Shader, pass.Kernel, (width + 7) / 8, (height + 7) / 8, 1);
                    sourceWidth = width;
                    sourceHeight = height;
                }
                cmd.SetGlobalVector(PyramidSizeId, new Vector4(pass.Width, pass.Height, pass.Levels, 1));
                cmd.SetGlobalVector(ReflectionId, pass.Settings);
            }

            private sealed class PassData
            {
                internal ComputeShader Shader;
                internal int Kernel;
                internal TextureHandle Depth;
                internal TextureHandle Pyramid;
                internal int Width;
                internal int Height;
                internal int Levels;
                internal Vector4 Settings;
            }
        }

        // Draws the passes with the LightMode tag LoogaSoftDepth into a depth texture of their own, before the
        // transparent objects. The camera depth stays as it is, so effects in front of these surfaces are not cut;
        // Looga/Water Mist reads the texture and fades out softly in front of them. Only materials after the water
        // queues draw: every Looga/Water material has the pass, and only the soft depth copies of the fall sheets use
        // it.
        private sealed class SoftDepthPass : ScriptableRenderPass
        {
            private const int FirstQueue = 3001;

            // A ShaderTagId may not be made in the static initializer of a renderer feature, so the pass makes it.
            private readonly ShaderTagId _tag;

            public SoftDepthPass()
            {
                _tag = new ShaderTagId("LoogaSoftDepth");
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
                profilingSampler = new ProfilingSampler("Looga Water.Soft Depth");
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                UniversalCameraData camera = frame.Get<UniversalCameraData>();
                UniversalRenderingData rendering = frame.Get<UniversalRenderingData>();
                int width = camera.cameraTargetDescriptor.width;
                int height = camera.cameraTargetDescriptor.height;
                if (width < 1 || height < 1) return;

                var description = new TextureDesc(width, height)
                {
                    depthBufferBits = DepthBits.Depth32,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    clearBuffer = true,
                    name = "Looga Soft Depth"
                };
                TextureHandle depth = graph.CreateTexture(description);
                var listDescription = new RendererListDesc(_tag, rendering.cullResults, camera.camera)
                {
                    renderQueueRange = new RenderQueueRange(FirstQueue, RenderQueueRange.all.upperBound),
                    sortingCriteria = SortingCriteria.CommonOpaque
                };
                RendererListHandle list = graph.CreateRendererList(listDescription);

                using var builder = graph.AddRasterRenderPass("Looga Water.Soft Depth", out PassData data,
                    profilingSampler);
                data.List = list;
                builder.UseRendererList(list);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(depth, SoftDepthId);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(pass.List);
                    context.cmd.SetGlobalFloat(SoftDepthOnId, 1f);
                });
            }

            private sealed class PassData
            {
                internal RendererListHandle List;
            }
        }

        // A depth texture with the opaque depth and the water surfaces (pass LoogaWaterDepth of Looga/Water). It becomes
        // the camera depth texture of the transparent objects; the water itself reads the opaque depth
        // (_LoogaOpaqueDepth). The URP depth texture can be a color copy, so the pass writes a texture of its own.
        private sealed class SurfaceDepthPass : ScriptableRenderPass
        {
            internal Material Material;
            private readonly ShaderTagId _tag;

            public SurfaceDepthPass()
            {
                _tag = new ShaderTagId("LoogaWaterDepth");
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
                profilingSampler = new ProfilingSampler("Looga Water.Surface Depth");
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                UniversalResourceData resources = frame.Get<UniversalResourceData>();
                UniversalCameraData camera = frame.Get<UniversalCameraData>();
                UniversalRenderingData rendering = frame.Get<UniversalRenderingData>();
                TextureHandle opaqueDepth = resources.cameraDepthTexture;
                if (!opaqueDepth.IsValid() || !Material) return;

                TextureDesc source = graph.GetTextureDesc(opaqueDepth);
                var description = new TextureDesc(source.width, source.height)
                {
                    depthBufferBits = DepthBits.Depth32,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    clearBuffer = false,
                    name = "Looga Water Surface Depth"
                };
                TextureHandle surfaceDepth = graph.CreateTexture(description);
                var listDescription = new RendererListDesc(_tag, rendering.cullResults, camera.camera)
                {
                    renderQueueRange = RenderQueueRange.all,
                    sortingCriteria = SortingCriteria.CommonOpaque
                };
                RendererListHandle list = graph.CreateRendererList(listDescription);

                using var builder = graph.AddRasterRenderPass("Looga Water.Surface Depth", out PassData data,
                    profilingSampler);
                data.Material = Material;
                data.OpaqueDepth = opaqueDepth;
                data.List = list;
                builder.UseTexture(opaqueDepth, AccessFlags.Read);
                builder.UseRendererList(list);
                builder.SetRenderAttachmentDepth(surfaceDepth, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(surfaceDepth, CameraDepthId);
                builder.SetGlobalTextureAfterPass(opaqueDepth, OpaqueDepthId);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalTexture(OpaqueDepthId, pass.OpaqueDepth);
                    context.cmd.SetGlobalFloat(OpaqueDepthOnId, 1f);
                    context.cmd.DrawProcedural(Matrix4x4.identity, pass.Material, 0, MeshTopology.Triangles, 3);
                    context.cmd.DrawRendererList(pass.List);
                });
            }

            private sealed class PassData
            {
                internal Material Material;
                internal TextureHandle OpaqueDepth;
                internal RendererListHandle List;
            }
        }

        // Clears the pyramid flag after the transparent objects, so later cameras without a pyramid use the probes.
        // With Surface Depth it gives the camera depth texture back to the passes after the transparent objects.
        private sealed class ResetPass : ScriptableRenderPass
        {
            internal bool RestoreDepth;

            public ResetPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                using var builder = graph.AddUnsafePass("Looga Water.Reflection Reset", out PassData _);
                TextureHandle depth = frame.Get<UniversalResourceData>().cameraDepthTexture;
                if (RestoreDepth && depth.IsValid())
                {
                    builder.UseTexture(depth, AccessFlags.Read);
                    builder.SetGlobalTextureAfterPass(depth, CameraDepthId);
                }
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData _, UnsafeGraphContext context) =>
                {
                    context.cmd.SetGlobalVector(PyramidSizeId, Vector4.zero);
                    context.cmd.SetGlobalFloat(SoftDepthOnId, 0f);
                    context.cmd.SetGlobalFloat(OpaqueDepthOnId, 0f);
                });
            }

            private sealed class PassData
            {
            }
        }

        // Starts the GPU sample before the transparent objects or ends it after them.
        private sealed class TimingPass : ScriptableRenderPass
        {
            private readonly CustomSampler _sampler;
            private readonly bool _begin;

            public TimingPass(CustomSampler sampler, bool begin)
            {
                _sampler = sampler;
                _begin = begin;
                // The begin pass runs after the pyramid pass, so the time holds only the transparent objects.
                renderPassEvent = begin ? RenderPassEvent.BeforeRenderingTransparents + 1
                    : RenderPassEvent.AfterRenderingTransparents;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                using var builder = graph.AddUnsafePass(_begin ? "Looga Water.Timing Begin" : "Looga Water.Timing End",
                    out PassData data);
                data.Sampler = _sampler;
                data.Begin = _begin;
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData pass, UnsafeGraphContext context) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    if (pass.Begin)
                    {
                        cmd.BeginSample(pass.Sampler);
                    }
                    else
                    {
                        cmd.EndSample(pass.Sampler);
                    }
                });
            }

            private sealed class PassData
            {
                internal CustomSampler Sampler;
                internal bool Begin;
            }
        }
    }
}
