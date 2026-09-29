using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class VirtualTextureCompressionTests
    {
        [Test]
        public void BlockCopiesPreserveSignedNormalsSmoothnessAndOcclusionAcrossSlices()
        {
            if (!VirtualTextureCompression.IsSupported) Assert.Ignore("D3D12 compression is required.");
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/VirtualTexturing/VirtualTextureCompression.compute");
            using var compression = new VirtualTextureCompression(shader, 16);
            using var commands = new CommandBuffer();
            var source = new Texture2D(136, 136, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave };
            var readback = new Texture2D(136, 136, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave };
            var output = new RenderTexture(136, 136, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            try
            {
                output.Create();
                for (int slot = 0; slot < 3; slot++)
                {
                    for (int channel = 0; channel < 2; channel++)
                    {
                        var values = new Color[136 * 136];
                        for (int y = 0; y < 136; y++)
                        {
                            for (int x = 0; x < 136; x++)
                            {
                                float alpha = (x % 16) / 15f;
                                Vector3 normal = new Vector3(-0.6f + x / 340f, -0.2f + y / 680f, 1).normalized;
                                values[y * 136 + x] = channel == 0
                                    ? new Color(0.05f + x / 200f, 0.1f + y / 220f, 0.25f + slot * 0.1f, alpha)
                                    : new Color(normal.x, normal.y, normal.z, alpha);
                                if (channel == 0 && slot == 2)
                                {
                                    values[y * 136 + x] = ((x + y) & 1) == 0
                                        ? new Color(1, 0, 0, alpha) : new Color(0, 1, 0, alpha);
                                }
                            }
                        }
                        var input = (Color[])values.Clone();
                        if (channel != 0)
                        {
                            for (int i = 0; i < input.Length; i++)
                            {
                                input[i].r *= 3;
                                input[i].g *= 3;
                                input[i].b *= 3;
                            }
                        }
                        source.SetPixels(input);
                        source.Apply();
                        Graphics.Blit(source, compression.Staging(channel));
                        commands.Clear();
                        compression.Encode(commands, slot);
                        Graphics.ExecuteCommandBuffer(commands);
                        var slice = new Texture2D(136, 136, TextureFormat.DXT5, false, channel != 0)
                        { hideFlags = HideFlags.HideAndDontSave };
                        try
                        {
                            slice.Apply(false, true);
                            Graphics.CopyTexture(compression.Texture(channel), slot, 0, slice, 0, 0);
                            Graphics.Blit(slice, output);
                            RenderTexture.active = output;
                            readback.ReadPixels(new Rect(0, 0, 136, 136), 0, 0);
                            readback.Apply();
                            Color[] actual = readback.GetPixels();
                            double sum = 0;
                            float alphaError = 0;
                            float normalAngle = 0;
                            for (int i = 0; i < actual.Length; i++)
                            {
                                Vector3 rgb = new Vector3(actual[i].r, actual[i].g, actual[i].b);
                                if (channel != 0)
                                {
                                    rgb = rgb * 2 - Vector3.one;
                                    normalAngle = Mathf.Max(normalAngle, Vector3.Angle(rgb, new Vector3(values[i].r, values[i].g, values[i].b)));
                                }
                                sum += Mathf.Abs(rgb.x - values[i].r) + Mathf.Abs(rgb.y - values[i].g) + Mathf.Abs(rgb.z - values[i].b);
                                alphaError = Mathf.Max(alphaError, Mathf.Abs(actual[i].a - values[i].a));
                            }
                            Assert.That(sum / (actual.Length * 3), Is.LessThan(0.025), "RGB error");
                            Assert.That(alphaError, Is.LessThan(0.025f), "Material alpha error");
                            Assert.That(normalAngle, Is.LessThan(5f), "Normal angle error");
                        }
                        finally
                        {
                            Object.DestroyImmediate(slice);
                        }
                    }
                }
                Assert.That(compression.CacheBytes, Is.EqualTo(136L * 136 * 16 * 2));
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(readback);
                output.Release();
                Object.DestroyImmediate(output);
            }
        }
    }
}
