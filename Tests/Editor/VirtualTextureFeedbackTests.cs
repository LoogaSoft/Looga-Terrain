using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class VirtualTextureFeedbackTests
    {
        [Test]
        public void ParallelFeedbackMatchesSerialHistogram()
        {
            var pixels = new float[131072];
            var expected = new Dictionary<ulong, int>();
            var random = new Random(9183);
            for (int i = 0; i < pixels.Length; i += 2)
            {
                uint page = (uint)random.Next(0, 342);
                uint tile = (uint)random.Next(0, 38);
                pixels[i] = page;
                pixels[i + 1] = tile;
                if (page == 0 || page > 341 || tile == 0) continue;
                ulong key = ((ulong)tile << 32) | (page - 1);
                expected.TryGetValue(key, out int count);
                expected[key] = count + 1;
            }
            using var input = new NativeArray<float>(pixels, Allocator.TempJob);
            using var batch = new VirtualTextureFeedbackBatch();
            batch.Schedule(input, 341);
            var actual = batch.Complete();
            Assert.That(actual.Length, Is.EqualTo(expected.Count));
            foreach (var item in actual)
            {
                Assert.That(item.Count, Is.EqualTo(expected[item.Key]));
            }
            batch.Schedule(input, 341);
            Assert.That(batch.Complete().Length, Is.EqualTo(expected.Count));
        }

        [Test]
        public void InvalidFeedbackIsRejectedAndPendingDisposalIsSafe()
        {
            using var pixels = new NativeArray<float>(new[] { float.NaN, 1, 1, float.PositiveInfinity, -1, 1, 1.5f, 1 }, Allocator.TempJob);
            var batch = new VirtualTextureFeedbackBatch();
            batch.Schedule(pixels, 341);
            Assert.That(batch.Complete().Length, Is.Zero);
            batch.Schedule(pixels, 341);
            Assert.Throws<InvalidOperationException>(() => batch.Schedule(pixels, 341));
            batch.Dispose();
            batch.Dispose();
        }
    }
}
