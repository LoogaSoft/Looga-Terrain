using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // Two 100 m tiles, 129 samples, 200 m high: one 16-bit step is 400 m / 65535 = 6.1 mm.
    public sealed class HeightStampEvaluatorTests
    {
        private const int Resolution = 129;
        private const float RealHeight = 400f;
        private Scene _scene;
        private TerrainData[] _data;
        private UnityEngine.Terrain[] _terrains;
        private LoogaStampWorld _world;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Height stamps require compute shaders.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _data = new TerrainData[2];
            _terrains = new UnityEngine.Terrain[2];
            for (int i = 0; i < 2; i++)
            {
                _data[i] = new TerrainData { heightmapResolution = Resolution, size = new Vector3(100f, 200f, 100f) };
                GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data[i]);
                SceneManager.MoveGameObjectToScene(tile, _scene);
                tile.transform.position = new Vector3(i * 100f, 0f, 0f);
                _terrains[i] = tile.GetComponent<UnityEngine.Terrain>();
            }
            _terrains[0].SetNeighbors(null, null, _terrains[1], null);
            _terrains[1].SetNeighbors(_terrains[0], null, null, null);
            var root = new GameObject("Stamp World");
            SceneManager.MoveGameObjectToScene(root, _scene);
            _world = root.AddComponent<LoogaStampWorld>();
            _world.SetTerrains(_terrains);
        }

        [TearDown]
        public void TearDown()
        {
            if (_data == null) return;
            EditorSceneManager.ClosePreviewScene(_scene);
            foreach (TerrainData data in _data)
            {
                Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void FlatOverrideGivesStampHeight()
        {
            AddStamp(HeightCombineMode.Override, new Vector3(100f, 50.3f, 50f), 400f, FalloffShape.Global);
            using StampHeights heights = _world.EvaluateHeights();
            int expected = Mathf.RoundToInt(50.3f / RealHeight * 65535f);
            Assert.That(heights.ReadTile(0), Has.All.EqualTo((ushort)expected));
            Assert.That(heights.ReadTile(1), Has.All.EqualTo((ushort)expected));
        }

        // MicroVerse rounds to 16 bits after each stamp, so adds below half a step have no effect.
        [TestCase(HeightMapping.MicroVerseParity, 0)]
        [TestCase(HeightMapping.WorldAligned, 3)]
        public void SmallAddsDependOnPerStampRounding(HeightMapping mapping, int expectedSteps)
        {
            SetMapping(mapping);
            AddStamp(HeightCombineMode.Override, new Vector3(100f, 50.3f, 50f), 400f, FalloffShape.Global);
            for (int i = 0; i < 10; i++)
            {
                AddStamp(HeightCombineMode.Add, new Vector3(100f, 0.002f, 50f), 400f, FalloffShape.Global);
            }
            using StampHeights heights = _world.EvaluateHeights();
            int start = Mathf.RoundToInt(50.3f / RealHeight * 65535f);
            ushort[] tile = heights.ReadTile(0);
            Assert.That(tile[64 * Resolution + 64] - start, Is.EqualTo(expectedSteps));
        }

        [Test]
        public void RangeFalloffKeepsCentreAndOutside()
        {
            AddStamp(HeightCombineMode.Override, new Vector3(100f, 20f, 50f), 400f, FalloffShape.Global);
            LoogaHeightStamp hill = AddStamp(HeightCombineMode.Max, new Vector3(50f, 60f, 50f), 60f, FalloffShape.Range);
            hill.Falloff.SetShape(FalloffShape.Range, new Vector2(0.5f, 1f), FalloffEasing.Smoothstep);
            using StampHeights heights = _world.EvaluateHeights();
            ushort[] tile = heights.ReadTile(0);
            int low = Mathf.RoundToInt(20f / RealHeight * 65535f);
            int high = Mathf.RoundToInt(60f / RealHeight * 65535f);
            Assert.That(tile[64 * Resolution + 64], Is.EqualTo(high), "Centre");
            Assert.That(tile[64 * Resolution + 10], Is.EqualTo(low), "Outside the outer radius");
            int edge = tile[64 * Resolution + 40];
            Assert.That(edge, Is.GreaterThan(low).And.LessThan(high), "Inside the falloff band");
        }

        [Test]
        public void SharedEdgesAreEqual()
        {
            LoogaHeightStamp slope = AddStamp(HeightCombineMode.Max, new Vector3(100f, 0f, 50f), 160f,
                FalloffShape.Global);
            slope.SetHeight(HeightCombineMode.Max, null, 0f, 1f, new Vector2(0.3f, 0.1f));
            using StampHeights heights = _world.EvaluateHeights();
            ushort[] left = heights.ReadTile(0);
            ushort[] right = heights.ReadTile(1);
            for (int z = 0; z < Resolution; z++)
            {
                Assert.That(left[z * Resolution + Resolution - 1], Is.EqualTo(right[z * Resolution]), "Row " + z);
            }
        }

        [Test]
        public void RebuildWritesEvaluatedHeights()
        {
            LoogaHeightStamp hill = AddStamp(HeightCombineMode.Max, new Vector3(60f, 30f, 40f), 50f, FalloffShape.Range);
            hill.Falloff.SetShape(FalloffShape.Range, new Vector2(0.2f, 1f), FalloffEasing.Linear);
            ushort[] expected;
            using (StampHeights heights = _world.EvaluateHeights())
            {
                expected = heights.ReadTile(0);
            }
            _world.RebuildHeights();
            float[,] written = _data[0].GetHeights(0, 0, Resolution, Resolution);
            for (int z = 0; z < Resolution; z += 8)
            {
                for (int x = 0; x < Resolution; x += 8)
                {
                    Assert.That(Mathf.RoundToInt(written[z, x] * 32766f), Is.EqualTo(expected[z * Resolution + x]));
                }
            }
        }

        // A small stamp move copies only the changed samples. The CPU heights after the region update and its partial
        // synchronization are the same as after a full rebuild.
        [Test]
        public void RegionUpdateCopiesOnlyTheChangedArea()
        {
            AddStamp(HeightCombineMode.Override, new Vector3(100f, 20f, 50f), 400f, FalloffShape.Global);
            LoogaHeightStamp bump = AddStamp(HeightCombineMode.Max, new Vector3(30f, 40f, 50f), 10f,
                FalloffShape.Range);
            _world.RebuildHeights();
            bump.transform.position += new Vector3(4f, 0f, 0f);
            using (StampHeights moved = _world.EvaluateHeights())
            using (var evaluator = new HeightStampEvaluator())
            {
                var tiles = new List<StampTile>();
                foreach (UnityEngine.Terrain terrain in moved.Terrains)
                {
                    tiles.Add(new StampTile(terrain));
                }
                RectInt[] areas = evaluator.FindChangedAreas(tiles, new[] { moved.Textures[0], moved.Textures[1] });
                Assert.That(moved.Terrains[0], Is.EqualTo(_terrains[0]));
                float spacing = 100f / (Resolution - 1);
                Assert.That(areas[1].width, Is.EqualTo(0), "Tile 1 has no change");
                Assert.That(areas[0].xMin, Is.GreaterThanOrEqualTo(Mathf.FloorToInt(25f / spacing)), "x min");
                Assert.That(areas[0].xMax, Is.LessThanOrEqualTo(Mathf.CeilToInt(39f / spacing) + 1), "x max");
                Assert.That(areas[0].width * areas[0].height, Is.LessThan(Resolution * Resolution / 20), "Small area");
            }
            _world.UpdateRegion(new Rect(20f, 40f, 24f, 20f));
            _world.SynchronizeHeights();
            float[,] region = _data[0].GetHeights(0, 0, Resolution, Resolution);
            _world.RebuildHeights();
            float[,] full = _data[0].GetHeights(0, 0, Resolution, Resolution);
            for (int z = 0; z < Resolution; z++)
            {
                for (int x = 0; x < Resolution; x++)
                {
                    Assert.That(region[z, x], Is.EqualTo(full[z, x]), "Sample " + x + ", " + z);
                }
            }
        }

        // A region update without a wait copies the region at once. The GPU check then finds the changed samples
        // outside the region, and the next completion copies them. The result equals a full rebuild.
        [Test]
        public void RegionUpdateWithoutWaitCopiesChangesOutsideTheRegionLater()
        {
            AddStamp(HeightCombineMode.Override, new Vector3(100f, 20f, 50f), 400f, FalloffShape.Global);
            LoogaHeightStamp bump = AddStamp(HeightCombineMode.Max, new Vector3(30f, 40f, 50f), 10f,
                FalloffShape.Range);
            _world.RebuildHeights();
            bump.transform.position += new Vector3(4f, 0f, 0f);
            // The region holds only the new bump. The old bump (x 25 to 35) also changes.
            Assert.That(_world.UpdateRegionWithoutWait(new Rect(32f, 40f, 12f, 20f)), Is.EqualTo(1));
            Assert.That(_world.HeightsSynchronized, Is.False, "The check is pending");
            _world.WaitForPendingHeights();
            _world.SynchronizeHeights();
            Assert.That(_world.HeightsSynchronized, Is.True);
            float[,] region = _data[0].GetHeights(0, 0, Resolution, Resolution);
            _world.RebuildHeights();
            float[,] full = _data[0].GetHeights(0, 0, Resolution, Resolution);
            for (int z = 0; z < Resolution; z++)
            {
                for (int x = 0; x < Resolution; x++)
                {
                    Assert.That(region[z, x], Is.EqualTo(full[z, x]), "Sample " + x + ", " + z);
                }
            }
        }

        // Render texture copies from a texture source give the same heights as the stamp's own textures.
        [Test]
        public void TextureSourceReplacesTheStampTextures()
        {
            AddStamp(HeightCombineMode.Override, new Vector3(100f, 20f, 50f), 400f, FalloffShape.Global);
            Texture2D height = Gradient(true);
            Texture2D falloff = Gradient(false);
            LoogaHeightStamp stamp = AddStamp(HeightCombineMode.Max, new Vector3(100f, 15f, 50f), 120f,
                FalloffShape.Texture);
            stamp.SetHeight(HeightCombineMode.Max, height, 0f, 1f, Vector2.zero);
            stamp.Falloff.SetTexture(falloff, 0, new Vector2(1f, 0f), 0f, 1f, Vector2.zero);
            ushort[][] expected = EvaluateBoth();
            RenderTexture heightCopy = Copy(height);
            RenderTexture falloffCopy = Copy(falloff);
            try
            {
                stamp.SetHeight(HeightCombineMode.Max, null, 0f, 1f, Vector2.zero);
                stamp.Falloff.SetTexture(null, 0, new Vector2(1f, 0f), 0f, 1f, Vector2.zero);
                stamp.gameObject.AddComponent<LoogaStampTextureSource>().SetTextures(heightCopy, falloffCopy);
                ushort[][] actual = EvaluateBoth();
                Assert.That(actual[0], Is.EqualTo(expected[0]), "Tile 0");
                Assert.That(actual[1], Is.EqualTo(expected[1]), "Tile 1");
                int flat = Mathf.RoundToInt(20f / RealHeight * 65535f);
                Assert.That(expected[1][64 * Resolution + 64], Is.GreaterThan(flat), "The stamp raises tile 1");
            }
            finally
            {
                Object.DestroyImmediate(height);
                Object.DestroyImmediate(falloff);
                heightCopy.Release();
                falloffCopy.Release();
                Object.DestroyImmediate(heightCopy);
                Object.DestroyImmediate(falloffCopy);
            }
        }

        // A Min stamp with an empty texture would cut its area down to its bottom. While its source has no texture,
        // the stamp is left out.
        [Test]
        public void StampWaitingForSourceTexturesHasNoEffect()
        {
            AddStamp(HeightCombineMode.Override, new Vector3(100f, 20f, 50f), 400f, FalloffShape.Global);
            ushort[][] before = EvaluateBoth();
            LoogaHeightStamp stamp = AddStamp(HeightCombineMode.Min, new Vector3(100f, 5f, 50f), 120f,
                FalloffShape.Global);
            stamp.gameObject.AddComponent<LoogaStampTextureSource>();
            ushort[][] after = EvaluateBoth();
            Assert.That(after[0], Is.EqualTo(before[0]), "Tile 0");
            Assert.That(after[1], Is.EqualTo(before[1]), "Tile 1");
        }

        // A 16-bit gradient along x (heights) or a radial weight (falloff).
        private static Texture2D Gradient(bool alongX)
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
                    float value = alongX ? x / (size - 1f) : Mathf.Clamp01(1.2f - radius);
                    pixels[y * size + x] = (ushort)Mathf.RoundToInt(value * 65535f);
                }
            }
            texture.SetPixelData(pixels, 0);
            texture.Apply(false, false);
            return texture;
        }

        private static RenderTexture Copy(Texture2D texture)
        {
            var copy = new RenderTexture(texture.width, texture.height, 0,
                UnityEngine.Experimental.Rendering.GraphicsFormat.R16_UNorm)
            {
                filterMode = texture.filterMode,
                wrapMode = texture.wrapMode
            };
            copy.Create();
            Graphics.CopyTexture(texture, copy);
            return copy;
        }

        private ushort[][] EvaluateBoth()
        {
            using StampHeights heights = _world.EvaluateHeights();
            return new[] { heights.ReadTile(0), heights.ReadTile(1) };
        }

        private LoogaHeightStamp AddStamp(HeightCombineMode mode, Vector3 position, float size, FalloffShape shape)
        {
            var stamp = new GameObject("Stamp").AddComponent<LoogaHeightStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.position = position;
            stamp.transform.localScale = new Vector3(size, 10f, size);
            stamp.SetHeight(mode, null, 0f, 1f, Vector2.zero);
            stamp.Falloff.SetShape(shape, new Vector2(0.8f, 1f), FalloffEasing.Linear);
            return stamp;
        }

        private void SetMapping(HeightMapping mapping)
        {
            var serialized = new UnityEditor.SerializedObject(_world);
            serialized.FindProperty("_mapping").enumValueIndex = (int)mapping;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
