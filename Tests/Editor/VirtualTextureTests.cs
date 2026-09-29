using NUnit.Framework;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class VirtualTextureTests
    {
        [TestCase(1)]
        [TestCase(4)]
        [TestCase(128)]
        [TestCase(256)]
        public void PageCoordinatesRoundTripAndReachRoot(int width)
        {
            var layout = new VirtualTextureLayout(width);
            for (int index = 0; index < layout.EntryCount; index++)
            {
                var page = layout.Decode(index);
                Assert.That(layout.Index(page.x, page.y, page.z), Is.EqualTo(index));
                int parent = layout.Parent(index);
                if (page.z == layout.LastMip)
                {
                    Assert.That(parent, Is.EqualTo(index));
                }
                else
                {
                    var above = layout.Decode(parent);
                    Assert.That(above.x, Is.EqualTo(page.x / 2));
                    Assert.That(above.y, Is.EqualTo(page.y / 2));
                    Assert.That(above.z, Is.EqualTo(page.z + 1));
                }
            }
        }

        [Test]
        public void LayoutRejectsInvalidCoordinates()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new VirtualTextureLayout(3));
            var layout = new VirtualTextureLayout(4);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => layout.Index(4, 0, 0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => layout.Index(0, 0, -1));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => layout.Decode(layout.EntryCount));
        }

        [Test]
        public void CacheProtectsCurrentRequestsAndEvictsOldestUnusedPage()
        {
            var pool = new VirtualPagePool(2);
            ulong first = VirtualPagePool.Key(1, 5), second = VirtualPagePool.Key(2, 5), third = VirtualPagePool.Key(1, 6);
            Assert.That(pool.Acquire(first, out var evicted), Is.EqualTo(0));
            Assert.That(evicted, Is.Null);
            Assert.That(pool.Acquire(second, out _), Is.EqualTo(1));
            Assert.That(pool.Acquire(third, out _), Is.EqualTo(-1));
            pool.BeginEpoch();
            pool.Touch(second);
            Assert.That(pool.Acquire(third, out evicted), Is.EqualTo(0));
            Assert.That(evicted, Is.EqualTo(first));
            Assert.That(pool.Find(first), Is.EqualTo(-1));
            Assert.That(pool.Find(second), Is.EqualTo(1));
            Assert.That(pool.Count, Is.EqualTo(2));
        }

        [Test]
        public void RemovingOnePagePreservesOtherSlotsAndReusesTheFreedSlot()
        {
            var pool = new VirtualPagePool(3);
            ulong one = VirtualPagePool.Key(17, 8), two = VirtualPagePool.Key(17, 9), other = VirtualPagePool.Key(18, 8);
            int freed = pool.Acquire(one, out _);
            int sameTile = pool.Acquire(two, out _);
            int otherTile = pool.Acquire(other, out _);
            Assert.That(pool.Remove(one), Is.True);
            Assert.That(pool.Remove(one), Is.False);
            Assert.That(pool.Count, Is.EqualTo(2));
            Assert.That(pool.Find(two), Is.EqualTo(sameTile));
            Assert.That(pool.Find(other), Is.EqualTo(otherTile));
            Assert.That(pool.Acquire(VirtualPagePool.Key(17, 10), out var evicted), Is.EqualTo(freed));
            Assert.That(evicted, Is.Null);
        }

        [Test]
        public void RemovingOneTilePreservesOtherTilePages()
        {
            var pool = new VirtualPagePool(3);
            ulong one = VirtualPagePool.Key(17, 8), two = VirtualPagePool.Key(17, 9), other = VirtualPagePool.Key(18, 8);
            pool.Acquire(one, out _);
            pool.Acquire(two, out _);
            int otherSlot = pool.Acquire(other, out _);
            pool.RemoveTile(17);
            Assert.That(pool.Count, Is.EqualTo(1));
            Assert.That(pool.Find(other), Is.EqualTo(otherSlot));
            Assert.That(pool.Find(one), Is.EqualTo(-1));
            Assert.That(pool.Acquire(VirtualPagePool.Key(19, 8), out var evicted), Is.GreaterThanOrEqualTo(0));
            Assert.That(evicted, Is.Null);
        }
    }
}
