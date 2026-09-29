using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class VirtualTextureGpuFeedbackTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void CountsDuplicatesAndSignalsProbeOverflow(bool overflow)
        {
            const int capacity = 256;
            const int entries = 65536;
            var source = new Texture2D(256, 1, TextureFormat.RGFloat, false, true);
            var feedback = new RenderTexture(256, 1, 0, RenderTextureFormat.RGFloat);
            var shader = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/VirtualTexturing/VirtualTextureCompact.compute"));
            using var hash = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 8);
            using var compact = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 8);
            using var header = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 4);
            using var commands = new CommandBuffer();
            try
            {
                var pixels = new Vector2[256];
                var expected = new Dictionary<uint, uint>();
                for (int index = 0; index < pixels.Length; index++)
                {
                    // Overflow keys share a hash slot and exceed the bounded probe count.
                    uint page = overflow ? (uint)(1 + index * 256) : (uint)(1 + index % 31);
                    pixels[index] = new Vector2(page, 2);
                    uint key = entries + page;
                    expected.TryGetValue(key, out uint count);
                    expected[key] = count + 1;
                }
                source.SetPixelData(pixels, 0);
                source.Apply();
                feedback.Create();
                Graphics.CopyTexture(source, feedback);
                shader.SetInt("_Capacity", capacity);
                shader.SetInt("_EntryCount", entries);
                shader.SetInts("_FeedbackSize", 256, 1);
                foreach (string name in new[] { "Clear", "Count", "Compact" })
                {
                    int kernel = shader.FindKernel(name);
                    shader.SetBuffer(kernel, "_Hash", hash);
                    shader.SetBuffer(kernel, "_Header", header);
                    if (name == "Count")
                    {
                        shader.SetTexture(kernel, "_Feedback", feedback);
                    }
                    if (name == "Compact")
                    {
                        shader.SetBuffer(kernel, "_Compact", compact);
                    }
                    shader.Dispatch(kernel, name == "Count" ? 32 : 4, 1, 1);
                }
                var counts = new uint[2];
                header.GetData(counts);
                Assert.That(counts[1], Is.EqualTo(overflow ? 1u : 0u));
                if (overflow) return;
                Assert.That(counts[0], Is.EqualTo(expected.Count));
                var values = new uint[capacity * 2];
                compact.GetData(values);
                var actual = new Dictionary<uint, uint>();
                for (int index = 0; index < counts[0]; index++)
                {
                    actual.Add(values[index * 2], values[index * 2 + 1]);
                }
                CollectionAssert.AreEquivalent(expected, actual);
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(feedback);
                Object.DestroyImmediate(shader);
            }
        }
    }
}
