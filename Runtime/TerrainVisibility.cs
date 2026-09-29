using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain
{
    public sealed partial class TerrainRenderer
    {
        // Material holes can differ from TerrainData holes. Unknown depth contracts retain their normal material passes.
        internal bool SupportsNativeDepth => _surfaceMaterial
            && _surfaceMaterial.GetTag("LoogaTerrainDepth", false, "") == "NativeHeightsAndHoles";

        internal void DrawOccluders(Camera camera, CommandBuffer commands, Material material)
        {
            if (!SupportsNativeDepth || !_cameras.TryGetValue(camera, out CameraBuffers buffers) || ClassifyBounds(_bounds) < 0) return;
            commands.DrawMeshInstancedIndirect(_mesh, 0, material, 0, buffers.VisibleArguments,
                buffers.ArgumentIndex * GraphicsBuffer.IndirectDrawIndexedArgs.size, buffers.VisibleProperties);
        }

        internal static void CullShadows(Camera camera, CommandBuffer commands, Vector4[] planes, Vector4[] lights, int lightCount)
        {
            if (_cameraCommands.TryGetValue(camera, out SelectionCommands selection))
            {
                selection.CullShadows(commands, planes, lights, lightCount);
            }
        }

        internal static void CullOccluded(Camera camera, CommandBuffer commands, Texture depth, Matrix4x4 projection,
            int width, int height, int levels, float bias, bool measure, int sampleId)
        {
            if (!_cameraCommands.TryGetValue(camera, out SelectionCommands selection)) return;
            selection.CullDepth(commands, depth, projection, width, height, levels, bias, measure, sampleId);
        }
    }
}
