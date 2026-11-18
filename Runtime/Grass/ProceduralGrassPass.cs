using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace LoogaSoft.Terrain.Grass
{
    /// <summary>
    /// Places the blades of one ProceduralGrassRenderer and draws them into the deferred G-buffer after the opaque
    /// G-buffer pass. The renderer enqueues this pass for each camera, so no renderer feature asset is needed.
    /// </summary>
    internal sealed class ProceduralGrassPass : ScriptableRenderPass
    {
        private sealed class PassData
        {
            internal ProceduralGrassRenderer Renderer;
            internal ProceduralGrassRenderer.CameraState State;
        }

        private readonly ProceduralGrassRenderer _renderer;
        private ProceduralGrassRenderer.CameraState _state;

        internal ProceduralGrassPass(ProceduralGrassRenderer renderer)
        {
            _renderer = renderer;
            renderPassEvent = RenderPassEvent.AfterRenderingGbuffer;
        }

        internal void Prepare(ProceduralGrassRenderer.CameraState state)
        {
            _state = state;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            TextureHandle[] gBuffer = resources.gBuffer;
            // Forward rendering has no G-buffer. The grass draws only in deferred rendering.
            if (_state == null || gBuffer == null || gBuffer.Length < 4) return;
            for (int index = 0; index < 4; index++)
            {
                if (!gBuffer[index].IsValid()) return;
            }
            if (!resources.activeDepthTexture.IsValid()) return;

            // An unsafe pass, because the compute pass binds the terrain textures that the render graph does not own.
            using (IUnsafeRenderGraphBuilder builder =
                   renderGraph.AddUnsafePass("Looga Grass Generate", out PassData data))
            {
                data.Renderer = _renderer;
                data.State = _state;
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData pass, UnsafeGraphContext context) =>
                    pass.Renderer.Generate(context.cmd, pass.State));
            }

            using (IRasterRenderGraphBuilder builder =
                   renderGraph.AddRasterRenderPass("Looga Grass G-Buffer", out PassData data))
            {
                data.Renderer = _renderer;
                data.State = _state;
                for (int index = 0; index < 4; index++)
                {
                    builder.SetRenderAttachment(gBuffer[index], index, AccessFlags.ReadWrite);
                }
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData pass, RasterGraphContext context) =>
                    pass.Renderer.Draw(context.cmd, pass.State));
            }
            _state = null;
        }
    }

    /// <summary>
    /// Draws the blades of one ProceduralGrassRenderer into the URP motion vector texture before post-processing. URP
    /// wrote the camera motion of every pixel before. The blades add their wind motion where they are the visible
    /// surface, so temporal antialiasing follows the swaying blades instead of rejecting their history.
    /// </summary>
    internal sealed class ProceduralGrassMotionPass : ScriptableRenderPass
    {
        private sealed class PassData
        {
            internal ProceduralGrassRenderer Renderer;
            internal ProceduralGrassRenderer.CameraState State;
        }

        private readonly ProceduralGrassRenderer _renderer;
        private ProceduralGrassRenderer.CameraState _state;

        internal ProceduralGrassMotionPass(ProceduralGrassRenderer renderer)
        {
            _renderer = renderer;
            // Every URP motion vector schedule (after the prepass, the G-buffer, the opaques, the skybox or the
            // transparents) runs before this event.
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            ConfigureInput(ScriptableRenderPassInput.Motion);
        }

        internal void Prepare(ProceduralGrassRenderer.CameraState state)
        {
            _state = state;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            if (_state == null || !resources.motionVectorColor.IsValid() || !resources.activeDepthTexture.IsValid())
            {
                _state = null;
                return;
            }

            using (IRasterRenderGraphBuilder builder =
                   renderGraph.AddRasterRenderPass("Looga Grass Motion Vectors", out PassData data))
            {
                data.Renderer = _renderer;
                data.State = _state;
                // Read and write, so the camera motion of the other pixels stays.
                builder.SetRenderAttachment(resources.motionVectorColor, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData pass, RasterGraphContext context) =>
                    pass.Renderer.DrawMotion(context.cmd, pass.State));
            }
            _state = null;
        }
    }
}
