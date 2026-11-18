using System.Collections.Generic;
using LoogaSoft.Terrain.Stamps.Editor;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // Three flat 100 m tiles in a row along x. Covers the editor integration of a stamp world: CPU height
    // synchronization spread over calls and texture reimport detection.
    public sealed class StampIntegrationTests
    {
        private const int TileCount = 3;

        private Scene _scene;
        private TerrainData[] _data;
        private Texture2D _texture;
        private LoogaStampWorld _world;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Height stamps require compute shaders.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _data = new TerrainData[TileCount];
            var terrains = new UnityEngine.Terrain[TileCount];
            for (int i = 0; i < TileCount; i++)
            {
                _data[i] = new TerrainData { heightmapResolution = 33, size = new Vector3(100f, 200f, 100f) };
                GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data[i]);
                tile.transform.position = new Vector3(i * 100f, 0f, 0f);
                SceneManager.MoveGameObjectToScene(tile, _scene);
                terrains[i] = tile.GetComponent<UnityEngine.Terrain>();
            }
            var root = new GameObject("Stamp World");
            SceneManager.MoveGameObjectToScene(root, _scene);
            _world = root.AddComponent<LoogaStampWorld>();
            _world.SetTerrains(terrains);
            _texture = new Texture2D(4, 4);
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
            Object.DestroyImmediate(_texture);
        }

        [Test]
        public void HeightSynchronizationSpreadsOverCalls()
        {
            AddHeightStamp(null);
            Assert.That(_world.UpdateRegion(_world.WorldRect()), Is.EqualTo(TileCount));
            Assert.That(_world.HeightsSynchronized, Is.False, "After the write");
            Assert.That(_world.SynchronizeHeights(1), Is.False, "One terrain");
            Assert.That(_world.SynchronizeHeights(1), Is.False, "Two terrains");
            Assert.That(_world.SynchronizeHeights(1), Is.True, "Three terrains");
            Assert.That(_world.HeightsSynchronized, Is.True);
            Assert.That(_data[2].GetHeight(16, 16), Is.EqualTo(20f).Within(0.01f), "CPU height");
        }

        [Test]
        public void UnchangedTilesNeedNoSynchronization()
        {
            AddHeightStamp(null);
            _world.RebuildHeights();
            Assert.That(_world.HeightsSynchronized, Is.True, "After the rebuild");
            Assert.That(_world.UpdateRegion(_world.WorldRect()), Is.EqualTo(TileCount), "Evaluated tiles");
            Assert.That(_world.HeightsSynchronized, Is.True, "Same heights");
        }

        // UpdateSettled reports the union of the updated areas once, after the heights are synchronized.
        [Test]
        public void UpdateSettledReportsTheUnionOnceWhenComplete()
        {
            var settled = new System.Collections.Generic.List<Rect>();
            _world.UpdateSettled += settled.Add;
            AddHeightStamp(null);
            _world.UpdateRegion(_world.WorldRect());
            _world.NoteUpdated(new Rect(10f, 10f, 20f, 20f));
            _world.NoteUpdated(new Rect(200f, 0f, 10f, 10f));
            Assert.That(_world.SettleIfComplete(), Is.False, "Heights not synchronized");
            _world.SynchronizeHeights();
            _world.IsUpdating = true;
            Assert.That(_world.SettleIfComplete(), Is.False, "While updating");
            _world.IsUpdating = false;
            Assert.That(_world.SettleIfComplete(), Is.True, "Complete");
            Assert.That(_world.SettleIfComplete(), Is.False, "Once");
            Assert.That(settled, Is.EqualTo(new[] { Rect.MinMaxRect(10f, 0f, 210f, 30f) }));
        }

        [Test]
        public void MarkChangedRecordsNestedSettingChanges()
        {
            LoogaHeightStamp stamp = AddHeightStamp(null);
            int revision = stamp.Revision;
            stamp.Falloff.SetNoise(StampNoiseType.Fbm, StampNoiseSpace.World, 20f, 2f, 0f, 0f);
            Assert.That(stamp.Revision, Is.EqualTo(revision), "A falloff setter alone");
            stamp.MarkChanged();
            Assert.That(stamp.Revision, Is.GreaterThan(revision));
        }

        [Test]
        public void ReimportedTextureMarksItsUsers()
        {
            LoogaHeightStamp user = AddHeightStamp(_texture);
            LoogaHeightStamp other = AddHeightStamp(null);
            var changed = new HashSet<Texture> { _texture };
            Assert.That(LoogaStampWorldUpdater.UsesChangedTexture(user, changed), Is.True, "Height texture");
            Assert.That(LoogaStampWorldUpdater.UsesChangedTexture(other, changed), Is.False, "No texture");
            other.Falloff.SetShape(FalloffShape.Texture, Vector2.one, FalloffEasing.Linear);
            other.Falloff.SetTexture(_texture, 0, new Vector2(1f, 0f), 0f, 1f, Vector2.zero);
            Assert.That(LoogaStampWorldUpdater.UsesChangedTexture(other, changed), Is.True, "Falloff texture");
            Assert.That(LoogaStampWorldUpdater.UsesChangedTexture(user, new HashSet<Texture>()), Is.False, "Empty set");
        }

        // A flat Override stamp at 20 m over all tiles, or a texture stamp when a texture is given.
        private LoogaHeightStamp AddHeightStamp(Texture2D texture)
        {
            var stamp = new GameObject("Height Stamp").AddComponent<LoogaHeightStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.position = new Vector3(150f, 20f, 50f);
            stamp.transform.localScale = new Vector3(300f, 100f, 100f);
            stamp.SetHeight(HeightCombineMode.Override, texture, 0f, 1f, Vector2.zero);
            stamp.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            return stamp;
        }
    }
}
