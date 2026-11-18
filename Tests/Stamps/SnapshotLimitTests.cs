using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // Two 100 m tiles, 129 samples, 200 m high: one 16-bit step is 400 m / 65535 = 6.1 mm.
    public sealed class SnapshotLimitTests
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
            _world.ReleaseResources();
            EditorSceneManager.ClosePreviewScene(_scene);
            foreach (TerrainData data in _data)
            {
                Object.DestroyImmediate(data);
            }
        }

        [TestCase(HeightMapping.MicroVerseParity)]
        [TestCase(HeightMapping.WorldAligned)]
        public void MinLimitRestoresSnapshotHeight(HeightMapping mapping)
        {
            SetMapping(mapping);
            AddStamp(HeightCombineMode.Override, 50f);
            LoogaHeightSnapshot snapshot = AddSnapshot();
            AddStamp(HeightCombineMode.Override, 80f);
            AddLimit(snapshot, HeightCombineMode.Min, 0f);
            Assert.That(Evaluate(0), Has.All.EqualTo(Steps(50f)));
            Assert.That(Evaluate(1), Has.All.EqualTo(Steps(50f)));
        }

        [TestCase(HeightCombineMode.Min, 10f, 80f, 60f)]
        [TestCase(HeightCombineMode.Max, 0f, 30f, 50f)]
        [TestCase(HeightCombineMode.Override, -5f, 80f, 45f)]
        public void ModeAndOffsetCombineWithSnapshot(HeightCombineMode mode, float offset, float later, float expected)
        {
            AddStamp(HeightCombineMode.Override, 50f);
            LoogaHeightSnapshot snapshot = AddSnapshot();
            AddStamp(HeightCombineMode.Override, later);
            AddLimit(snapshot, mode, offset);
            Assert.That(Evaluate(0), Has.All.EqualTo(Steps(expected)));
        }

        [Test]
        public void LimitHasNoEffectOutsideItsBox()
        {
            AddStamp(HeightCombineMode.Override, 50f);
            LoogaHeightSnapshot snapshot = AddSnapshot();
            AddStamp(HeightCombineMode.Override, 80f);
            LoogaSnapshotLimit limit = AddLimit(snapshot, HeightCombineMode.Min, 0f);
            limit.transform.position = new Vector3(25f, 0f, 50f);
            limit.transform.localScale = new Vector3(40f, 10f, 200f);
            ushort[] tile = Evaluate(0);
            Assert.That(tile[Index(25f, 50f)], Is.EqualTo(Steps(50f)), "Inside the box");
            Assert.That(tile[Index(75f, 50f)], Is.EqualTo(Steps(80f)), "Outside the box");
            Assert.That(Evaluate(1), Has.All.EqualTo(Steps(80f)), "Tile outside the box");
        }

        [Test]
        public void FalloffWeightsTheLimit()
        {
            AddStamp(HeightCombineMode.Override, 50f);
            LoogaHeightSnapshot snapshot = AddSnapshot();
            AddStamp(HeightCombineMode.Override, 80f);
            LoogaSnapshotLimit limit = AddLimit(snapshot, HeightCombineMode.Min, 0f);
            limit.transform.position = new Vector3(50f, 0f, 50f);
            limit.transform.localScale = new Vector3(60f, 10f, 60f);
            limit.Falloff.SetShape(FalloffShape.Range, new Vector2(0.5f, 1f), FalloffEasing.Linear);
            ushort[] tile = Evaluate(0);
            Assert.That(tile[Index(50f, 50f)], Is.EqualTo(Steps(50f)), "Centre");
            Assert.That(tile[Index(50f, 72f)], Is.GreaterThan(Steps(50f)).And.LessThan(Steps(80f)), "Falloff band");
            Assert.That(tile[Index(50f, 95f)], Is.EqualTo(Steps(80f)), "Outside the range");
        }

        [Test]
        public void LimitBeforeItsSnapshotHasNoEffect()
        {
            AddStamp(HeightCombineMode.Override, 50f);
            LoogaSnapshotLimit limit = AddLimit(null, HeightCombineMode.Min, 0f);
            LoogaHeightSnapshot snapshot = AddSnapshot();
            limit.SetLimit(snapshot, HeightCombineMode.Min, -20f);
            AddStamp(HeightCombineMode.Override, 80f);
            Assert.That(Evaluate(0), Has.All.EqualTo(Steps(80f)));
        }

        // A smooth stamp splits the order into segments. The limit after it must still read the earlier snapshot.
        [TestCase(HeightMapping.MicroVerseParity)]
        [TestCase(HeightMapping.WorldAligned)]
        public void LimitAfterASmoothStampReadsTheSnapshot(HeightMapping mapping)
        {
            SetMapping(mapping);
            AddStamp(HeightCombineMode.Override, 50f);
            LoogaHeightSnapshot snapshot = AddSnapshot();
            AddStamp(HeightCombineMode.Override, 80f);
            var smooth = new GameObject("Smooth").AddComponent<LoogaSmoothStamp>();
            smooth.transform.SetParent(_world.transform, false);
            smooth.transform.position = new Vector3(100f, 0f, 50f);
            smooth.transform.localScale = new Vector3(400f, 10f, 400f);
            smooth.SetSmoothing(2f, 1f, 1f);
            smooth.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            AddLimit(snapshot, HeightCombineMode.Min, 0f);
            Assert.That(Evaluate(0), Has.All.EqualTo(Steps(50f)), "Tile 0");
            Assert.That(Evaluate(1), Has.All.EqualTo(Steps(50f)), "Tile 1");
        }

        [Test]
        public void DisabledSnapshotGivesNoLimit()
        {
            AddStamp(HeightCombineMode.Override, 50f);
            LoogaHeightSnapshot snapshot = AddSnapshot();
            AddStamp(HeightCombineMode.Override, 80f);
            AddLimit(snapshot, HeightCombineMode.Min, 0f);
            snapshot.enabled = false;
            Assert.That(Evaluate(0), Has.All.EqualTo(Steps(80f)));
        }

        [Test]
        public void EvaluatorCopiesOnlySnapshotsThatALimitReads()
        {
            AddStamp(HeightCombineMode.Override, 50f);
            LoogaHeightSnapshot used = AddSnapshot();
            AddSnapshot();
            AddStamp(HeightCombineMode.Override, 80f);
            LoogaSnapshotLimit limit = AddLimit(used, HeightCombineMode.Min, 0f);
            limit.transform.position = new Vector3(50f, 0f, 50f);
            limit.transform.localScale = new Vector3(60f, 10f, 60f);
            var modifiers = new List<LoogaHeightModifier>();
            _world.GetHeightModifiers(modifiers);
            using var evaluator = new HeightStampEvaluator();
            var tile = new StampTile(_terrains[0]);
            RenderTexture output = HeightStampEvaluator.CreateOutput(tile);
            try
            {
                evaluator.EvaluateTile(tile, modifiers, HeightMapping.MicroVerseParity, 0, output);
                Assert.That(evaluator.LastSnapshotCount, Is.EqualTo(1), "Tile under the limit");
                evaluator.EvaluateTile(new StampTile(_terrains[1]), modifiers, HeightMapping.MicroVerseParity, 0,
                    output);
                Assert.That(evaluator.LastSnapshotCount, Is.EqualTo(0), "Tile outside the limit");
            }
            finally
            {
                output.Release();
                Object.DestroyImmediate(output);
            }
        }

        private LoogaHeightStamp AddStamp(HeightCombineMode mode, float height)
        {
            var stamp = new GameObject("Stamp").AddComponent<LoogaHeightStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.position = new Vector3(100f, height, 50f);
            stamp.transform.localScale = new Vector3(400f, 10f, 400f);
            stamp.SetHeight(mode, null, 0f, 1f, Vector2.zero);
            stamp.Falloff.SetShape(FalloffShape.Global, new Vector2(0.8f, 1f), FalloffEasing.Linear);
            return stamp;
        }

        private LoogaHeightSnapshot AddSnapshot()
        {
            var snapshot = new GameObject("Snapshot").AddComponent<LoogaHeightSnapshot>();
            snapshot.transform.SetParent(_world.transform, false);
            return snapshot;
        }

        private LoogaSnapshotLimit AddLimit(LoogaHeightSnapshot snapshot, HeightCombineMode mode, float offset)
        {
            var limit = new GameObject("Limit").AddComponent<LoogaSnapshotLimit>();
            limit.transform.SetParent(_world.transform, false);
            limit.transform.position = new Vector3(100f, 0f, 50f);
            limit.transform.localScale = new Vector3(400f, 10f, 400f);
            limit.SetLimit(snapshot, mode, offset);
            limit.Falloff.SetShape(FalloffShape.Global, new Vector2(0.8f, 1f), FalloffEasing.Linear);
            return limit;
        }

        private ushort[] Evaluate(int tile)
        {
            using StampHeights heights = _world.EvaluateHeights();
            return heights.ReadTile(tile);
        }

        private void SetMapping(HeightMapping mapping)
        {
            var serialized = new UnityEditor.SerializedObject(_world);
            serialized.FindProperty("_mapping").enumValueIndex = (int)mapping;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static int Index(float x, float z)
        {
            int column = Mathf.RoundToInt(x / 100f * (Resolution - 1));
            int row = Mathf.RoundToInt(z / 100f * (Resolution - 1));
            return row * Resolution + column;
        }

        private static ushort Steps(float metres)
        {
            return (ushort)Mathf.RoundToInt(metres / RealHeight * 65535f);
        }
    }
}
