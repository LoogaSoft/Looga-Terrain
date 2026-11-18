using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // One 100 m tile with a 64 x 64 alphamap and five layers (two alphamap textures).
    public sealed class TextureStampTests
    {
        private const int AlphamapResolution = 64;
        private const int LayerCount = 5;

        private Scene _scene;
        private TerrainData _data;
        private TerrainLayer[] _layers;
        private LoogaStampWorld _world;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Texture stamps require compute shaders.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _layers = new TerrainLayer[LayerCount];
            for (int i = 0; i < LayerCount; i++)
            {
                _layers[i] = new TerrainLayer { name = "Layer " + (char)('A' + i) };
            }
            _data = new TerrainData { heightmapResolution = 33, alphamapResolution = AlphamapResolution };
            _data.size = new Vector3(100f, 200f, 100f);
            _data.terrainLayers = _layers;
            GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data);
            SceneManager.MoveGameObjectToScene(tile, _scene);
            var root = new GameObject("Stamp World");
            SceneManager.MoveGameObjectToScene(root, _scene);
            _world = root.AddComponent<LoogaStampWorld>();
            _world.SetTerrains(new[] { tile.GetComponent<UnityEngine.Terrain>() });
        }

        [TearDown]
        public void TearDown()
        {
            if (_data == null) return;
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_data);
            foreach (TerrainLayer layer in _layers)
            {
                Object.DestroyImmediate(layer);
            }
        }

        [Test]
        public void GlobalStampPaintsTheWholeTile()
        {
            AddStamp(1, 1f, FalloffShape.Global);
            byte[][] maps = Evaluate();
            Assert.That(Weight(maps, 1, 2f, 2f), Is.EqualTo(255), "Corner");
            Assert.That(Weight(maps, 1, 50f, 50f), Is.EqualTo(255), "Centre");
            Assert.That(Weight(maps, 0, 50f, 50f), Is.EqualTo(0), "Other layer");
        }

        [Test]
        public void LaterStampTakesItsWeightFirst()
        {
            AddStamp(0, 1f, FalloffShape.Global);
            AddStamp(1, 1f, FalloffShape.Global);
            byte[][] maps = Evaluate();
            Assert.That(Weight(maps, 1, 50f, 50f), Is.EqualTo(255), "Later stamp");
            Assert.That(Weight(maps, 0, 50f, 50f), Is.EqualTo(0), "Earlier stamp");
        }

        [Test]
        public void BoxStampHasAHardEdge()
        {
            AddStamp(0, 1f, FalloffShape.Global);
            LoogaTextureStamp box = AddStamp(1, 1f, FalloffShape.Box);
            box.transform.localScale = new Vector3(40f, 1f, 40f);
            box.Falloff.SetShape(FalloffShape.Box, new Vector2(1f, 1f), FalloffEasing.Linear);
            byte[][] maps = Evaluate();
            Assert.That(Weight(maps, 1, 50f, 50f), Is.EqualTo(255), "Inside");
            Assert.That(Weight(maps, 0, 10f, 10f), Is.EqualTo(255), "Outside");
        }

        [Test]
        public void StampWeightSharesTheTexel()
        {
            AddStamp(0, 1f, FalloffShape.Global);
            AddStamp(1, 0.5f, FalloffShape.Global);
            byte[][] maps = Evaluate();
            int later = Weight(maps, 1, 50f, 50f);
            int earlier = Weight(maps, 0, 50f, 50f);
            Assert.That(later, Is.InRange(127, 128), "Later stamp at weight 0.5");
            Assert.That(later + earlier, Is.InRange(254, 256), "Normalized sum");
        }

        [Test]
        public void TexelKeepsOnlyTheFourStrongestLayers()
        {
            // Applied last first: layers E, D, C and B take 0.21 each, then A gets the 0.15 that is left. That is less
            // than the fourth entry, so A does not get a slot.
            AddStamp(0, 1f, FalloffShape.Global);
            for (int layer = 1; layer < LayerCount; layer++)
            {
                AddStamp(layer, 0.21f, FalloffShape.Global);
            }
            byte[][] maps = Evaluate();
            Assert.That(Weight(maps, 0, 50f, 50f), Is.EqualTo(0), "Fifth layer");
            for (int layer = 1; layer < LayerCount; layer++)
            {
                Assert.That(Weight(maps, layer, 50f, 50f), Is.InRange(63, 64), "Layer " + layer);
            }
        }

        // The test terrain is flat at height 0.
        [TestCase(0f, 10f, 255)]
        [TestCase(50f, 100f, 0)]
        public void HeightFilterKeepsTheHeightRange(float minimum, float maximum, int expected)
        {
            AddStamp(0, 1f, FalloffShape.Global);
            LoogaTextureStamp filtered = AddStamp(1, 1f, FalloffShape.Global);
            filtered.HeightFilter.Set(true, new Vector2(minimum, maximum), new Vector2(1f, 1f), 1f);
            byte[][] maps = Evaluate();
            Assert.That(Weight(maps, 1, 50f, 50f), Is.EqualTo(expected));
        }

        // Flat ground has slope 0, also with the MicroVerse height term (height 0).
        [TestCase(0f, 10f, 255)]
        [TestCase(30f, 60f, 0)]
        public void SlopeFilterKeepsTheSlopeRange(float minimum, float maximum, int expected)
        {
            AddStamp(0, 1f, FalloffShape.Global);
            LoogaTextureStamp filtered = AddStamp(1, 1f, FalloffShape.Global);
            filtered.SlopeFilter.Set(true, new Vector2(minimum, maximum), new Vector2(1f, 1f), 1f);
            byte[][] maps = Evaluate();
            Assert.That(Weight(maps, 1, 50f, 50f), Is.EqualTo(expected));
        }

        [Test]
        public void PathTexturePaintsAlongADisabledPath()
        {
            AddStamp(0, 1f, FalloffShape.Global);
            var container = new GameObject("Path").AddComponent<SplineContainer>();
            container.transform.SetParent(_world.transform, false);
            container.Spline = new Spline(new[]
            {
                new BezierKnot(new float3(10, 0, 50)), new BezierKnot(new float3(90, 0, 50))
            });
            var path = container.gameObject.AddComponent<LoogaSplinePath>();
            path.SetSpline(container, new List<LoogaSplinePath.SplineWidths>(), FalloffEasing.Linear);
            path.SetDistanceField(256, 64, 13f, 6f);
            // A disabled path changes no heights but still gives the distance field.
            path.enabled = false;
            var texture = container.gameObject.AddComponent<LoogaPathTexture>();
            texture.SetTexture(_layers[1], 4f, 2f, 1f);
            byte[][] maps = Evaluate();
            Assert.That(Weight(maps, 1, 50f, 50f), Is.EqualTo(255), "On the path");
            Assert.That(Weight(maps, 0, 50f, 90f), Is.EqualTo(255), "Away from the path");
        }

        // A falloff texture from a texture source paints the same weights as the same texture on the stamp.
        [Test]
        public void TextureSourceFalloffReplacesTheStampFalloff()
        {
            AddStamp(0, 1f, FalloffShape.Global);
            LoogaTextureStamp stamp = AddStamp(1, 1f, FalloffShape.Texture);
            Texture2D falloff = RadialFalloff();
            Texture2D copy = RadialFalloff();
            try
            {
                stamp.Falloff.SetTexture(falloff, 0, new Vector2(1f, 0f), 0f, 1f, Vector2.zero);
                byte[][] expected = Evaluate();
                stamp.Falloff.SetTexture(null, 0, new Vector2(1f, 0f), 0f, 1f, Vector2.zero);
                stamp.gameObject.AddComponent<LoogaStampTextureSource>().SetFalloffTexture(copy);
                byte[][] actual = Evaluate();
                Assert.That(actual, Is.EqualTo(expected));
                Assert.That(Weight(expected, 1, 50f, 50f), Is.GreaterThan(Weight(expected, 1, 3f, 3f)),
                    "The falloff shapes the stamp");
            }
            finally
            {
                Object.DestroyImmediate(falloff);
                Object.DestroyImmediate(copy);
            }
        }

        // Without a falloff texture the stamp would paint its whole box. While its source has none, it is left out.
        [Test]
        public void StampWaitingForSourceFalloffHasNoEffect()
        {
            AddStamp(0, 1f, FalloffShape.Global);
            byte[][] before = Evaluate();
            LoogaTextureStamp stamp = AddStamp(1, 1f, FalloffShape.Texture);
            stamp.gameObject.AddComponent<LoogaStampTextureSource>();
            Assert.That(Evaluate(), Is.EqualTo(before));
        }

        // A falloff change with a region reports the region once and does not mark the whole stamp as changed.
        [Test]
        public void SourceFalloffRegionIsReportedOnce()
        {
            LoogaTextureStamp stamp = AddStamp(1, 1f, FalloffShape.Texture);
            var source = stamp.gameObject.AddComponent<LoogaStampTextureSource>();
            int revision = stamp.Revision;
            source.SetFalloffTexture(Texture2D.whiteTexture, new Rect(10f, 10f, 5f, 5f));
            source.SetFalloffTexture(Texture2D.whiteTexture, new Rect(30f, 20f, 5f, 5f));
            Assert.That(stamp.Revision, Is.EqualTo(revision), "Revision with a region");
            Assert.That(source.TakeChangedRegion(out Rect region), Is.True, "Region");
            Assert.That(region, Is.EqualTo(Rect.MinMaxRect(10f, 10f, 35f, 25f)));
            Assert.That(source.TakeChangedRegion(out _), Is.False, "Taken once");
            source.SetFalloffTexture(Texture2D.whiteTexture);
            Assert.That(stamp.Revision, Is.Not.EqualTo(revision), "Revision without a region");
        }

        private LoogaTextureStamp AddStamp(int layer, float weight, FalloffShape shape)
        {
            var stamp = new GameObject("Stamp " + layer).AddComponent<LoogaTextureStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.position = new Vector3(50f, 0f, 50f);
            stamp.transform.localScale = new Vector3(100f, 1f, 100f);
            stamp.SetTexture(_layers[layer], weight, false);
            stamp.Falloff.SetShape(shape, new Vector2(1f, 1f), FalloffEasing.Linear);
            return stamp;
        }

        private byte[][] Evaluate()
        {
            using StampAlphamaps alphamaps = _world.EvaluateTextures();
            var maps = new byte[alphamaps.GetAlphamaps(0).Count][];
            for (int i = 0; i < maps.Length; i++)
            {
                maps[i] = alphamaps.ReadAlphamap(0, i);
            }
            return maps;
        }

        // A 16-bit radial weight: 1 in the centre, 0 at the edge.
        private static Texture2D RadialFalloff()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.R16, false, true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new ushort[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float radius = new Vector2(x - 31.5f, y - 31.5f).magnitude / 32f;
                    pixels[y * size + x] = (ushort)Mathf.RoundToInt(Mathf.Clamp01(1.2f - radius) * 65535f);
                }
            }
            texture.SetPixelData(pixels, 0);
            texture.Apply(false, false);
            return texture;
        }

        // Alphamap value (0 to 255) of one layer at a world position.
        private static int Weight(byte[][] maps, int layer, float x, float z)
        {
            int column = Mathf.Clamp(Mathf.FloorToInt(x / 100f * AlphamapResolution), 0, AlphamapResolution - 1);
            int row = Mathf.Clamp(Mathf.FloorToInt(z / 100f * AlphamapResolution), 0, AlphamapResolution - 1);
            return maps[layer / 4][(row * AlphamapResolution + column) * 4 + layer % 4];
        }
    }
}
