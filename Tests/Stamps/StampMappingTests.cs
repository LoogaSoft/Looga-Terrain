using NUnit.Framework;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // Mapping numbers come from Plans/LoogaStamps/MEASUREMENTS.md 5.1 (MicroVerse probe 1).
    public sealed class StampMappingTests
    {
        private const int Resolution = 2049;
        private TerrainData _data;
        private GameObject _terrain;
        private GameObject _stamp;

        [SetUp]
        public void SetUp()
        {
            _data = new TerrainData { heightmapResolution = Resolution, size = new Vector3(1000f, 2000f, 1000f) };
            _terrain = UnityEngine.Terrain.CreateTerrainGameObject(_data);
            _terrain.transform.position = new Vector3(3000f, 0f, -2000f);
            _stamp = new GameObject("Stamp");
            _stamp.transform.SetPositionAndRotation(new Vector3(3250f, 120f, -1250f), Quaternion.Euler(0f, 30f, 0f));
            _stamp.transform.localScale = new Vector3(300f, 50f, 200f);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_stamp);
            Object.DestroyImmediate(_terrain);
            Object.DestroyImmediate(_data);
        }

        // MicroVerse reads the stamp half a sample in +x and +z and scales it by (resolution - 1) / resolution.
        [Test]
        public void ParityMappingShiftsHalfSampleAndScales()
        {
            var tile = new StampTile(_terrain.GetComponent<UnityEngine.Terrain>());
            Matrix4x4 map = StampMapping.TileToStamp(tile, _stamp.transform, HeightMapping.MicroVerseParity);
            float spacing = 1000f / (Resolution - 1);
            float scale = (Resolution - 1) / (float)Resolution;
            foreach (Vector2Int sample in new[] { new Vector2Int(400, 1500), new Vector2Int(512, 1536), new Vector2Int(620, 1600) })
            {
                Vector2 expected = IdealStampUv(new Vector2(sample.x * spacing, sample.y * spacing) +
                    new Vector2(spacing, spacing) * 0.5f, scale);
                Vector2 actual = Map(map, sample, Resolution);
                Assert.That(actual.x, Is.EqualTo(expected.x).Within(2e-5f));
                Assert.That(actual.y, Is.EqualTo(expected.y).Within(2e-5f));
            }
        }

        [Test]
        public void WorldAlignedMappingUsesSamplePositions()
        {
            var tile = new StampTile(_terrain.GetComponent<UnityEngine.Terrain>());
            Matrix4x4 map = StampMapping.TileToStamp(tile, _stamp.transform, HeightMapping.WorldAligned);
            float spacing = 1000f / (Resolution - 1);
            var sample = new Vector2Int(512, 1536);
            Vector2 expected = IdealStampUv(new Vector2(sample.x * spacing, sample.y * spacing), 1f);
            Vector2 actual = Map(map, sample, Resolution);
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(2e-5f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(2e-5f));
        }

        // MicroVerse adds only two turned corners to the bounds of a turned stamp.
        [Test]
        public void ParityCullRectKeepsMicroVerseBounds()
        {
            _stamp.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 45f, 0f));
            _stamp.transform.localScale = new Vector3(100f, 10f, 100f);
            Rect parity = StampMapping.CullRect(_stamp.transform, HeightMapping.MicroVerseParity);
            Rect world = StampMapping.CullRect(_stamp.transform, HeightMapping.WorldAligned);
            float diagonal = 100f * Mathf.Sqrt(0.5f);
            Assert.That(parity.xMax, Is.EqualTo(diagonal).Within(1e-3f));
            Assert.That(parity.yMax, Is.EqualTo(50f).Within(1e-3f));
            Assert.That(world.xMax, Is.EqualTo(diagonal).Within(1e-3f));
            Assert.That(world.yMax, Is.EqualTo(diagonal).Within(1e-3f));
        }

        [Test]
        public void StampTexelsCoverEveryInsideSample()
        {
            const int resolution = 65;
            const int halo = 4;
            // A resolution change also changes the terrain size. Set the size again.
            _data.heightmapResolution = resolution;
            _data.size = new Vector3(1000f, 2000f, 1000f);
            var tile = new StampTile(_terrain.GetComponent<UnityEngine.Terrain>());
            Matrix4x4 map = StampMapping.TileToStamp(tile, _stamp.transform, HeightMapping.MicroVerseParity);
            RectInt texels = StampMapping.StampTexels(map, resolution, halo);
            int inside = 0;
            for (int z = 0; z < resolution + 2 * halo; z++)
            {
                for (int x = 0; x < resolution + 2 * halo; x++)
                {
                    Vector2 uv = Map(map, new Vector2Int(x - halo, z - halo), resolution);
                    if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) continue;
                    inside++;
                    Assert.That(texels.Contains(new Vector2Int(x, z)), Is.True, "Texel " + x + ", " + z);
                }
            }
            Assert.That(inside, Is.GreaterThan(0));
        }

        // Stamp UV of a point in tile space (metres from the tile corner), for a stamp scale factor.
        private Vector2 IdealStampUv(Vector2 tilePoint, float scale)
        {
            Transform stamp = _stamp.transform;
            Vector2 offset = tilePoint - new Vector2(stamp.position.x - 3000f, stamp.position.z + 2000f);
            float yaw = stamp.eulerAngles.y * Mathf.Deg2Rad;
            var turned = new Vector2(Mathf.Cos(yaw) * offset.x - Mathf.Sin(yaw) * offset.y,
                Mathf.Sin(yaw) * offset.x + Mathf.Cos(yaw) * offset.y);
            Vector3 size = stamp.lossyScale;
            return new Vector2(0.5f + turned.x * scale / size.x, 0.5f + turned.y * scale / size.z);
        }

        private static Vector2 Map(Matrix4x4 map, Vector2Int sample, int resolution)
        {
            var uv = new Vector2((sample.x + 0.5f) / resolution, (sample.y + 0.5f) / resolution);
            Vector3 stampUv = map.MultiplyPoint3x4(new Vector3(uv.x, uv.y, 0f));
            return new Vector2(stampUv.x, stampUv.y);
        }
    }
}
