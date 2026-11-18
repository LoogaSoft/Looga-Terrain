using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Grass
{
    /// <summary>
    /// The ground parts of the <see cref="GrassGround"/> objects near the grass cameras, drawn from above into a
    /// world-aligned XZ map: the highest ground height, the offset to the terrain point whose ground it shows and the
    /// grass density of the ground (less than 1 near the edge of masked ground). The grass places blades on it. Only objects within the blade distance of a camera are drawn, so ground far away does
    /// not make the map texels larger. The map draws again after a registry change or when that set of objects changes.
    /// </summary>
    internal sealed class GrassGroundMap : IDisposable
    {
        private const string ShaderName = "LoogaGrassGroundCapture";
        // World size of one texel, in meters. A blade can stand up to half a texel past the edge of the ground.
        private const float TexelSize = 0.25f;
        // Largest texel count along one side. A larger area gets larger texels.
        private const int MaximumTexels = 4096;
        // Margin around the ground bounds, in meters.
        private const float Margin = 1f;
        // Height of a texel without ground. The compute pass tests against it.
        internal const float NoGround = -1e6f;
        // Smallest up component of the ground normal: ground steeper than about 45 degrees grows no grass.
        private const float MinimumUp = 0.7f;

        private static readonly int ViewProjectionId = Shader.PropertyToID("_GrassGroundViewProjection");
        private static readonly int MinimumUpId = Shader.PropertyToID("_GrassGroundMinimumUp");
        private static readonly int MaskId = Shader.PropertyToID("_GrassGroundMask");
        private static readonly int MaskOnId = Shader.PropertyToID("_GrassGroundMaskOn");

        private readonly List<GrassGroundMesh> _meshes = new();
        // Objects in the map, and the objects near the cameras in this update.
        private readonly List<IGrassGround> _selected = new();
        private readonly List<IGrassGround> _candidates = new();
        private Material _material;
        private RenderTexture _height;
        private RenderTexture _offset;
        private int _revision = -1;

        /// <summary>True when the map holds ground.</summary>
        internal bool HasGround { get; private set; }

        /// <summary>World XZ rectangle of the map.</summary>
        internal Rect Rect { get; private set; }

        /// <summary>Highest ground in the map, for the tile culling.</summary>
        internal float MaximumHeight { get; private set; }

        /// <summary>World X and Z of the map origin, texel size, and 1 when the map holds ground.</summary>
        internal Vector4 Area => HasGround ? new Vector4(Rect.xMin, Rect.yMin, TexelWorldSize, 1f) : Vector4.zero;

        /// <summary>Texel counts along X and Z.</summary>
        internal Vector4 Texels => HasGround ? new Vector4(_height.width, _height.height, 0f, 0f) : Vector4.zero;

        internal Texture HeightTexture => HasGround ? _height : Texture2D.blackTexture;
        internal Texture OffsetTexture => HasGround ? _offset : Texture2D.blackTexture;

        private float TexelWorldSize { get; set; } = TexelSize;

        /// <summary>
        /// Draws the map again when the registry changed or when other objects come within reach of the cameras (XZ
        /// distance in meters, usually the blade distance). Main thread.
        /// </summary>
        internal void Update(List<Vector3> cameraPositions, float reach)
        {
            _candidates.Clear();
            float reachSquared = reach * reach;
            foreach (IGrassGround surface in GrassGround.All)
            {
                if (surface == null || surface is UnityEngine.Object unityObject && !unityObject) continue;
                Bounds bounds = surface.GrassGroundBounds;
                foreach (Vector3 position in cameraPositions)
                {
                    if (bounds.SqrDistance(new Vector3(position.x, bounds.center.y, position.z)) > reachSquared) continue;
                    _candidates.Add(surface);
                    break;
                }
            }
            if (_revision == GrassGround.Revision && SameSelection()) return;
            _revision = GrassGround.Revision;
            _selected.Clear();
            _selected.AddRange(_candidates);
            Capture();
        }

        private bool SameSelection()
        {
            if (_candidates.Count != _selected.Count) return false;
            for (int index = 0; index < _candidates.Count; index++)
            {
                if (!ReferenceEquals(_candidates[index], _selected[index])) return false;
            }
            return true;
        }

        public void Dispose()
        {
            ReleaseTextures();
            if (_material) UnityEngine.Object.DestroyImmediate(_material);
            _material = null;
            _revision = -1;
        }

        private void Capture()
        {
            _meshes.Clear();
            Bounds? union = null;
            foreach (IGrassGround surface in _selected)
            {
                if (surface == null || surface is UnityEngine.Object unityObject && !unityObject) continue;
                int count = _meshes.Count;
                surface.GetGrassGroundMeshes(_meshes);
                if (_meshes.Count == count) continue;
                Bounds bounds = surface.GrassGroundBounds;
                if (union.HasValue)
                {
                    Bounds value = union.Value;
                    value.Encapsulate(bounds);
                    union = value;
                }
                else
                {
                    union = bounds;
                }
            }
            if (!union.HasValue || !LoadMaterial())
            {
                ReleaseTextures();
                return;
            }

            Bounds area = union.Value;
            float texel = TexelSize;
            float sizeX = area.size.x + 2f * Margin;
            float sizeZ = area.size.z + 2f * Margin;
            while (Mathf.Max(sizeX, sizeZ) / texel > MaximumTexels) texel *= 2f;
            TexelWorldSize = texel;
            float xMin = Mathf.Floor((area.min.x - Margin) / texel) * texel;
            float zMin = Mathf.Floor((area.min.z - Margin) / texel) * texel;
            int width = Mathf.CeilToInt((area.max.x + Margin - xMin) / texel);
            int height = Mathf.CeilToInt((area.max.z + Margin - zMin) / texel);
            Rect = new Rect(xMin, zMin, width * texel, height * texel);
            MaximumHeight = area.max.y;
            EnsureTextures(width, height);
            Render(area);
            HasGround = true;
        }

        // Draws the ground parts with an orthographic view from above. The depth test keeps the highest ground.
        private void Render(Bounds area)
        {
            Rect rect = Rect;
            float top = area.max.y + 10f;
            float depth = area.size.y + 20f;
            var eye = new Vector3(rect.center.x, top, rect.center.y);
            // Looking down with +Z up on the screen, so screen X is world X and screen Y is world Z.
            Quaternion rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            Matrix4x4 view = Matrix4x4.TRS(eye, rotation, new Vector3(1f, 1f, -1f)).inverse;
            Matrix4x4 projection = Matrix4x4.Ortho(-0.5f * rect.width, 0.5f * rect.width, -0.5f * rect.height,
                0.5f * rect.height, 0.1f, depth);
            _material.SetMatrix(ViewProjectionId, GL.GetGPUProjectionMatrix(projection, true) * view);
            _material.SetFloat(MinimumUpId, MinimumUp);

            var commands = new CommandBuffer { name = "Looga Grass Ground Capture" };
            var unmasked = new MaterialPropertyBlock();
            unmasked.SetTexture(MaskId, Texture2D.whiteTexture);
            unmasked.SetFloat(MaskOnId, 0f);
            try
            {
                commands.SetRenderTarget(_offset);
                commands.ClearRenderTarget(false, true, Color.clear);
                commands.SetRenderTarget(_height);
                commands.ClearRenderTarget(true, true, new Color(NoGround, 0f, 0f, 0f));
                commands.SetRenderTarget(new RenderTargetIdentifier[] { _height, _offset }, _height.depthBuffer);
                foreach (GrassGroundMesh part in _meshes)
                {
                    if (!part.Mesh || part.SubMesh >= part.Mesh.subMeshCount) continue;
                    MaterialPropertyBlock properties = unmasked;
                    if (part.GroundMask)
                    {
                        properties = new MaterialPropertyBlock();
                        properties.SetTexture(MaskId, part.GroundMask);
                        properties.SetFloat(MaskOnId, 1f);
                    }
                    commands.DrawMesh(part.Mesh, part.LocalToWorld, _material, part.SubMesh, 0, properties);
                }
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally
            {
                commands.Release();
            }
        }

        private bool LoadMaterial()
        {
            if (_material) return true;
            var shader = Resources.Load<Shader>(ShaderName);
            if (!shader || !shader.isSupported) return false;
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        private void EnsureTextures(int width, int height)
        {
            if (_height && _height.width == width && _height.height == height) return;
            ReleaseTextures();
            _height = new RenderTexture(new RenderTextureDescriptor(width, height, GraphicsFormat.R32_SFloat, 32))
            {
                name = "Looga Grass Ground Height",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _height.Create();
            _offset = new RenderTexture(new RenderTextureDescriptor(width, height, GraphicsFormat.R16G16B16A16_SFloat, 0))
            {
                name = "Looga Grass Ground Offset",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _offset.Create();
        }

        private void ReleaseTextures()
        {
            HasGround = false;
            if (_height) UnityEngine.Object.DestroyImmediate(_height);
            if (_offset) UnityEngine.Object.DestroyImmediate(_offset);
            _height = null;
            _offset = null;
        }
    }
}
