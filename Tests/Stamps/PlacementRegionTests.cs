using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // Three flat 1 km tiles in a row along x, with one detail layer in coverage mode. The stamps cross the tile edges,
    // so a region update must keep the results of the tiles that it does not place again.
    public sealed class PlacementRegionTests
    {
        private const int TileCount = 3;
        private const int DetailResolution = 64;

        private Scene _scene;
        private TerrainData[] _data;
        private UnityEngine.Terrain[] _terrains;
        private Texture2D _disk;
        private Texture2D _grass;
        private GameObject _prefab;
        private LoogaStampWorld _world;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Placement stamps require compute shaders.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _grass = new Texture2D(4, 4);
            _data = new TerrainData[TileCount];
            _terrains = new UnityEngine.Terrain[TileCount];
            for (int i = 0; i < TileCount; i++)
            {
                _data[i] = new TerrainData { heightmapResolution = 33, alphamapResolution = 64 };
                _data[i].size = new Vector3(1000f, 200f, 1000f);
                _data[i].SetDetailResolution(DetailResolution, 16);
                _data[i].SetDetailScatterMode(DetailScatterMode.CoverageMode);
                _data[i].detailPrototypes = new[]
                {
                    new DetailPrototype { prototypeTexture = _grass, renderMode = DetailRenderMode.Grass }
                };
                GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data[i]);
                tile.transform.position = new Vector3(i * 1000f, 0f, 0f);
                SceneManager.MoveGameObjectToScene(tile, _scene);
                _terrains[i] = tile.GetComponent<UnityEngine.Terrain>();
            }
            var root = new GameObject("Stamp World");
            SceneManager.MoveGameObjectToScene(root, _scene);
            _world = root.AddComponent<LoogaStampWorld>();
            _world.SetTerrains(_terrains);
            _disk = new Texture2D(16, 1, TextureFormat.RGFloat, false, true);
            _disk.SetPixels(new Color[16]);
            _disk.Apply();
            _prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(_prefab, _scene);
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
            Object.DestroyImmediate(_disk);
            Object.DestroyImmediate(_grass);
        }

        [Test]
        public void ObjectStampMoveEqualsFullEvaluation()
        {
            LoogaObjectStamp stamp = AddObjectStamp(new Vector3(900f, 0f, 500f));
            AddDetailStamp(new Vector3(1900f, 0f, 500f), 0.5f);
            _world.RebuildPlacements();
            Rect before = stamp.GetCullRect(HeightMapping.MicroVerseParity);
            stamp.transform.position += new Vector3(300f, 0f, 0f);
            Rect after = stamp.GetCullRect(HeightMapping.MicroVerseParity);
            int placed = _world.UpdatePlacementRegion(Union(before, after), modifier => modifier == stamp);
            Assert.That(placed, Is.EqualTo(2), "Terrains placed again");
            AssertStoredEqualsFull();
            Assert.That(_world.PlacementSet.GetPlacementCount(stamp, _prefab),
                Is.EqualTo(_world.StoredPlacements.Get(stamp, 0).Count));
        }

        [Test]
        public void DetailStampChangeEqualsFullEvaluation()
        {
            AddObjectStamp(new Vector3(900f, 0f, 500f));
            LoogaDetailStamp detail = AddDetailStamp(new Vector3(1900f, 0f, 500f), 0.5f);
            _world.RebuildPlacements();
            detail.SetDetail(0, 0.8f, new Vector2(0f, 999999f), false, true);
            _world.UpdatePlacementRegion(detail.GetCullRect(HeightMapping.MicroVerseParity),
                modifier => modifier == detail);
            AssertStoredEqualsFull();
            int[,] layer = _data[1].GetDetailLayer(0, 0, DetailResolution, DetailResolution, 0);
            // Texel x 56 is at x = 1880 m, inside the stamp (1600..2200 m).
            Assert.That(layer[32, 56], Is.EqualTo(204), "Weight 0.8 inside the stamp");
        }

        [Test]
        public void DisabledStampLosesItsEntry()
        {
            LoogaObjectStamp stamp = AddObjectStamp(new Vector3(900f, 0f, 500f));
            _world.RebuildPlacements();
            Assert.That(_world.PlacementSet.GetPlacementCount(stamp, _prefab), Is.GreaterThan(0));
            stamp.enabled = false;
            _world.UpdatePlacementRegion(stamp.GetCullRect(HeightMapping.MicroVerseParity),
                modifier => modifier == stamp);
            Assert.That(_world.PlacementSet.GetPlacementCount(stamp, _prefab), Is.EqualTo(0));
            Assert.That(_world.StoredPlacements.Contains(stamp), Is.False);
        }

        [Test]
        public void StartedRegionUpdateEqualsFullEvaluationWhenComplete()
        {
            LoogaObjectStamp stamp = AddObjectStamp(new Vector3(900f, 0f, 500f));
            LoogaDetailStamp detail = AddDetailStamp(new Vector3(1900f, 0f, 500f), 0.5f);
            _world.RebuildPlacements();
            Rect before = stamp.GetCullRect(HeightMapping.MicroVerseParity);
            stamp.transform.position += new Vector3(300f, 0f, 0f);
            detail.SetDetail(0, 0.8f, new Vector2(0f, 999999f), false, true);
            Rect region = Union(Union(before, stamp.GetCullRect(HeightMapping.MicroVerseParity)),
                detail.GetCullRect(HeightMapping.MicroVerseParity));
            int placed = _world.StartPlacementRegion(region, modifier => modifier == stamp || modifier == detail);
            Assert.That(placed, Is.EqualTo(3), "Terrains placed again");
            Assert.That(_world.PlacementsPending, Is.True, "Readbacks complete only in a later call");
            _world.WaitForPendingPlacements();
            Assert.That(_world.PlacementsPending, Is.False);
            AssertStoredEqualsFull();
            Assert.That(_world.PlacementSet.GetPlacementCount(stamp, _prefab),
                Is.EqualTo(_world.StoredPlacements.Get(stamp, 0).Count));
            int[,] layer = _data[1].GetDetailLayer(0, 0, DetailResolution, DetailResolution, 0);
            Assert.That(layer[32, 56], Is.EqualTo(204), "Weight 0.8 inside the stamp");
        }

        [Test]
        public void CancelledRegionUpdateKeepsTheStoredResult()
        {
            LoogaObjectStamp stamp = AddObjectStamp(new Vector3(900f, 0f, 500f));
            _world.RebuildPlacements();
            int count = _world.PlacementSet.GetPlacementCount(stamp, _prefab);
            Assert.That(count, Is.GreaterThan(0));
            stamp.enabled = false;
            _world.StartPlacementRegion(stamp.GetCullRect(HeightMapping.MicroVerseParity), modifier => modifier == stamp);
            _world.CancelPendingPlacements();
            Assert.That(_world.PlacementsPending, Is.False);
            Assert.That(_world.PlacementSet.GetPlacementCount(stamp, _prefab), Is.EqualTo(count));
            // A later update does not get the dropped results.
            stamp.enabled = true;
            _world.UpdatePlacementRegion(stamp.GetCullRect(HeightMapping.MicroVerseParity), modifier => modifier == stamp);
            AssertStoredEqualsFull();
        }

        [Test]
        public void ClearMoveInsideObjectStampEqualsFullEvaluation()
        {
            // The object stamp covers parts of two tiles. The clear moves inside it, so the region update evaluates only
            // the candidates near the clear and keeps the stored placements elsewhere on the same tiles.
            LoogaObjectStamp stamp = AddObjectStamp(new Vector3(900f, 0f, 500f));
            var clear = new GameObject("Clear").AddComponent<LoogaClearStamp>();
            clear.transform.SetParent(_world.transform, false);
            clear.transform.position = new Vector3(800f, 0f, 400f);
            clear.transform.localScale = new Vector3(80f, 100f, 80f);
            clear.Falloff.SetShape(FalloffShape.Box, Vector2.one, FalloffEasing.Linear);
            clear.SetClear(true, false, false, 1f, false);
            _world.RebuildPlacements();
            int before = _world.StoredPlacements.Get(stamp, 0).Count;
            Rect old = clear.GetCullRect(HeightMapping.MicroVerseParity);
            clear.transform.position += new Vector3(150f, 0f, 120f);
            _world.UpdatePlacementRegion(Union(old, clear.GetCullRect(HeightMapping.MicroVerseParity)));
            AssertStoredEqualsFull();
            Assert.That(_world.StoredPlacements.Get(stamp, 0).Count, Is.GreaterThan(before / 2), "Placements outside stay");
        }

        [Test]
        public void RegionUpdateWithoutStoredResultRebuildsAll()
        {
            AddObjectStamp(new Vector3(900f, 0f, 500f));
            int placed = _world.UpdatePlacementRegion(new Rect(2500f, 500f, 1f, 1f));
            Assert.That(placed, Is.EqualTo(TileCount));
            AssertStoredEqualsFull();
        }

        private void AssertStoredEqualsFull()
        {
            StampPlacements stored = _world.StoredPlacements;
            StampPlacements full = _world.EvaluatePlacements();
            foreach (LoogaObjectStamp stamp in full.Stamps)
            {
                Assert.That(Sorted(stored.Get(stamp, 0)), Is.EqualTo(Sorted(full.Get(stamp, 0))), stamp.name);
            }
            foreach (UnityEngine.Terrain terrain in _terrains)
            {
                bool inFull = full.TryGetDetailLayer(terrain, 0, out byte[] expected);
                bool inStored = stored.TryGetDetailLayer(terrain, 0, out byte[] actual);
                Assert.That(inStored, Is.EqualTo(inFull), terrain.name);
                if (inFull)
                {
                    Assert.That(actual, Is.EqualTo(expected), terrain.name);
                }
            }
        }

        private static List<Vector3> Sorted(IReadOnlyList<StampPlacements.Placement> placements)
        {
            var positions = new List<Vector3>(placements.Count);
            foreach (StampPlacements.Placement placement in placements)
            {
                positions.Add(placement.Position);
            }
            positions.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
            return positions;
        }

        private static Rect Union(Rect a, Rect b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax),
                Mathf.Max(a.yMax, b.yMax));
        }

        private LoogaObjectStamp AddObjectStamp(Vector3 position)
        {
            var stamp = new GameObject("Object Stamp").AddComponent<LoogaObjectStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.position = position;
            stamp.transform.localScale = new Vector3(600f, 100f, 600f);
            stamp.Falloff.SetShape(FalloffShape.Box, Vector2.one, FalloffEasing.Linear);
            stamp.SetSpawning(1f, 0, _disk, 1f, 1f, false);
            var prototype = new ObjectPrototype(_prefab);
            prototype.SetPick(true, 0f, Vector2.zero, false);
            stamp.SetPrototypes(new List<ObjectPrototype> { prototype });
            return stamp;
        }

        private LoogaDetailStamp AddDetailStamp(Vector3 position, float weight)
        {
            var stamp = new GameObject("Detail Stamp").AddComponent<LoogaDetailStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.position = position;
            stamp.transform.localScale = new Vector3(600f, 100f, 600f);
            stamp.Falloff.SetShape(FalloffShape.Box, Vector2.one, FalloffEasing.Linear);
            stamp.SetDetail(0, weight, new Vector2(0f, 999999f), false, true);
            return stamp;
        }
    }
}
