using System.Collections.Generic;
using LoogaSoft.Instancing;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // One flat 1 km tile. A zero Poisson disk keeps every candidate on its grid point, so the counts are exact: density 1
    // gives round(512 x 1^2) = 512 candidates, and all of them are inside the tile.
    public sealed class PlacementTests
    {
        private const int Candidates = 512;

        private Scene _scene;
        private TerrainData _data;
        private Texture2D _disk;
        private GameObject _prefab;
        private GameObject _otherPrefab;
        private LoogaStampWorld _world;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Placement stamps require compute shaders.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _data = new TerrainData { heightmapResolution = 33, alphamapResolution = 64 };
            _data.size = new Vector3(1000f, 200f, 1000f);
            GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data);
            SceneManager.MoveGameObjectToScene(tile, _scene);
            var root = new GameObject("Stamp World");
            SceneManager.MoveGameObjectToScene(root, _scene);
            _world = root.AddComponent<LoogaStampWorld>();
            _world.SetTerrains(new[] { tile.GetComponent<UnityEngine.Terrain>() });
            _disk = new Texture2D(16, 1, TextureFormat.RGFloat, false, true);
            _disk.SetPixels(new Color[16]);
            _disk.Apply();
            _prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _otherPrefab = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            SceneManager.MoveGameObjectToScene(_prefab, _scene);
            SceneManager.MoveGameObjectToScene(_otherPrefab, _scene);
        }

        [TearDown]
        public void TearDown()
        {
            if (_data == null) return;
            _world.ReleaseResources();
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_data);
            Object.DestroyImmediate(_disk);
        }

        [Test]
        public void GlobalStampPlacesEveryCandidate()
        {
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            StampPlacements placements = _world.EvaluatePlacements();
            Assert.That(placements.Get(stamp, 0).Count, Is.EqualTo(Candidates));
        }

        [Test]
        public void ZeroWeightPlacesNothing()
        {
            AddObjectStamp(0f, false);
            Assert.That(_world.EvaluatePlacements().Count, Is.EqualTo(0));
        }

        [Test]
        public void PositionsHaveHalfPrecision()
        {
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            foreach (StampPlacements.Placement placement in _world.EvaluatePlacements().Get(stamp, 0))
            {
                float u = placement.Position.x / 1000f;
                Assert.That(Mathf.HalfToFloat(Mathf.FloatToHalf(u)), Is.EqualTo(u));
            }
        }

        // The clear sum applies only when the last covering clear comes after the object stamp, and it removes a
        // candidate only when it reaches 1. The clear map stores the sum in 8 bits, so 0.5 is a rounding tie and the
        // cases avoid it.
        [TestCase(new[] { 1f }, false, 0)]
        [TestCase(new[] { 0.6f, 0.6f }, false, 0)]
        [TestCase(new[] { 0.4f, 0.4f }, false, Candidates)]
        [TestCase(new[] { 0.6f }, false, Candidates)]
        public void ClearAfterTheStampRemovesAtSumOne(float[] weights, bool densityByWeight, int expected)
        {
            LoogaObjectStamp stamp = AddObjectStamp(1f, densityByWeight);
            foreach (float weight in weights)
            {
                AddClear(weight);
            }
            Assert.That(_world.EvaluatePlacements().Get(stamp, 0).Count, Is.EqualTo(expected));
        }

        [Test]
        public void ClearBeforeTheStampKeepsThePlacements()
        {
            AddClear(1f);
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            Assert.That(_world.EvaluatePlacements().Get(stamp, 0).Count, Is.EqualTo(Candidates));
        }

        // Density by weight tests the spawn with the full weight, then maps the cleared weight: 1 - 0.6 = 0.4 is below
        // 0.5 and spawns nothing, 1 - 0.3 = 0.7 spawns.
        [TestCase(0.6f, 0)]
        [TestCase(0.3f, Candidates)]
        public void DensityByWeightUsesTheClearedWeight(float clear, int expected)
        {
            LoogaObjectStamp stamp = AddObjectStamp(1f, true);
            AddClear(clear);
            Assert.That(_world.EvaluatePlacements().Get(stamp, 0).Count, Is.EqualTo(expected));
        }

        [Test]
        public void DisabledPrototypeIsNeverPicked()
        {
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            var first = new ObjectPrototype(_prefab);
            first.SetPick(false, 5f, Vector2.zero, false);
            var second = new ObjectPrototype(_otherPrefab);
            second.SetPick(true, 0f, Vector2.zero, false);
            stamp.SetPrototypes(new[] { first, second });
            StampPlacements placements = _world.EvaluatePlacements();
            Assert.That(placements.Get(stamp, 0).Count, Is.EqualTo(0), "Disabled");
            Assert.That(placements.Get(stamp, 1).Count, Is.EqualTo(Candidates), "Enabled");
        }

        [Test]
        public void ScaleLockGivesEqualAxes()
        {
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            var prototype = new ObjectPrototype(_prefab);
            prototype.SetPick(true, 0f, Vector2.zero, false);
            prototype.SetScale(new Vector2(0.5f, 2f), new Vector2(3f, 4f), new Vector2(5f, 6f), AxisLock.XYZ, 1f);
            stamp.SetPrototypes(new[] { prototype });
            foreach (StampPlacements.Placement placement in _world.EvaluatePlacements().Get(stamp, 0))
            {
                Assert.That(placement.Scale.y, Is.EqualTo(placement.Scale.x));
                Assert.That(placement.Scale.z, Is.EqualTo(placement.Scale.x));
                Assert.That(placement.Scale.x, Is.InRange(0.5f, 2f));
            }
        }

        // A ramp that rises 100 m over the 1000 m tile in x: a slope of 0.1. A cube scaled by 20 has a base radius of
        // 0.5 x sqrt(2) x 20 = 14.1 m (its lowest vertices are its bottom corners), so a full ground fit sinks it by about
        // 1.41 m: the drop to the lowest ring sample on the downhill side. The heights pass through half floats, so the
        // check allows 0.25 m.
        [Test]
        public void GroundFitSinksWideBasesToTheLowestGround()
        {
            const int resolution = 33;
            var heights = new float[resolution, resolution];
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    heights[z, x] = 0.25f + 0.5f * x / (resolution - 1);
                }
            }
            _data.SetHeights(0, 0, heights);
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            var prototype = new ObjectPrototype(_prefab);
            prototype.SetPick(true, 0f, Vector2.zero, false);
            prototype.SetScale(new Vector2(20f, 20f), new Vector2(20f, 20f), new Vector2(20f, 20f), AxisLock.XYZ, 1f);
            stamp.SetPrototypes(new[] { prototype });
            IReadOnlyList<StampPlacements.Placement> upright = _world.EvaluatePlacements().Get(stamp, 0);
            prototype.SetGroundFit(1f, 0f);
            stamp.SetPrototypes(new[] { prototype });
            IReadOnlyList<StampPlacements.Placement> fitted = _world.EvaluatePlacements().Get(stamp, 0);

            Assert.That(fitted.Count, Is.EqualTo(upright.Count));
            int checkedCount = 0;
            for (int index = 0; index < upright.Count; index++)
            {
                // Away from the tile edges, where the ring samples clamp.
                float x = upright[index].Position.x, z = upright[index].Position.z;
                if (x < 50f || x > 950f || z < 50f || z > 950f) continue;
                Assert.That(upright[index].Position.y - fitted[index].Position.y, Is.EqualTo(1.414f).Within(0.25f));
                checkedCount++;
            }
            Assert.That(checkedCount, Is.GreaterThan(100));
        }

        [Test]
        public void WriteFillsTheInstanceSet()
        {
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            _world.RebuildPlacements();
            InstanceSet set = _world.PlacementSet;
            Assert.That(set, Is.Not.Null);
            Assert.That(set.GetPlacementCount(stamp, _prefab), Is.EqualTo(Candidates));
            stamp.SetSpawning(1f, 0, _disk, 1f, 0f, false);
            _world.RebuildPlacements();
            Assert.That(set.GetPlacementCount(stamp, _prefab), Is.EqualTo(0), "Empty result removes the entry");
        }

        // A path along z = 500 with an object clear of 20 m + 10 m smoothness. The grid row at z = 500 goes, rows more
        // than 30 m away stay. A stamp that paths do not occlude keeps every candidate.
        [TestCase(true)]
        [TestCase(false)]
        public void PathClearRemovesObjectsNearThePath(bool occluded)
        {
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            stamp.SetOccludedByPaths(occluded);
            var container = new GameObject("Path spline").AddComponent<SplineContainer>();
            container.transform.SetParent(_world.transform, false);
            container.Spline = new Spline(new[]
            {
                new BezierKnot(new float3(-100, 0, 500)), new BezierKnot(new float3(1100, 0, 500))
            });
            var path = new GameObject("Path").AddComponent<LoogaSplinePath>();
            path.transform.SetParent(_world.transform, false);
            path.SetSpline(container, new List<LoogaSplinePath.SplineWidths>(), FalloffEasing.Linear);
            path.SetDistanceField(1024, 64, 33f, 30f);
            path.enabled = false;
            var clear = path.gameObject.AddComponent<LoogaPathClear>();
            clear.SetClear(new LoogaPathClear.ClearShape(true, 20f, 10f), new LoogaPathClear.ClearShape(false, 0f, 0f),
                new LoogaPathClear.ClearShape(false, 0f, 0f));

            IReadOnlyList<StampPlacements.Placement> placements = _world.EvaluatePlacements().Get(stamp, 0);
            if (!occluded)
            {
                Assert.That(placements.Count, Is.EqualTo(Candidates));
                return;
            }
            Assert.That(placements.Count, Is.LessThan(Candidates).And.GreaterThan(Candidates - 50));
            foreach (StampPlacements.Placement placement in placements)
            {
                Assert.That(Mathf.Abs(placement.Position.z - 500f), Is.GreaterThanOrEqualTo(20f));
            }
        }

        // A ramp that rises tan(10 deg) x 1000 m over the 1000 m tile in x. A clear with a true steepness filter that
        // starts at 7 deg removes every candidate, one that starts at 13 deg removes none. The heightmap texel spacing
        // can differ from size / resolution by one texel in 32, so the limits keep 3 deg from the ramp angle.
        [TestCase(7f, 0)]
        [TestCase(13f, Candidates)]
        public void ClearSteepnessFilterUsesTheGroundAngle(float from, int expected)
        {
            const int resolution = 33;
            float rise = Mathf.Tan(10f * Mathf.Deg2Rad) * 1000f / _data.size.y;
            var heights = new float[resolution, resolution];
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    heights[z, x] = rise * x / (resolution - 1);
                }
            }
            _data.SetHeights(0, 0, heights);
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            LoogaClearStamp clear = AddClear(1f);
            clear.SlopeFilter.Set(true, new Vector2(from, 90f), Vector2.zero, 1f);
            clear.SetSlopeMeasure(SlopeMeasure.Steepness);
            Assert.That(_world.EvaluatePlacements().Get(stamp, 0).Count, Is.EqualTo(expected));
        }

        // The west half of the tile is flat, the east half is a 10 deg ramp from x = 500 m. A clear with a steepness
        // filter from 7 deg removes the ramp candidates. With a reach of 100 m it also removes the flat candidates
        // within about 100 m of the ramp. The heightmap texel is 31 m and the candidate grid is 44 m, so the limits
        // allow about one of each.
        [Test]
        public void SteepnessReachClearsTheGroundNextToTheSlope()
        {
            const int resolution = 33;
            float slope = Mathf.Tan(10f * Mathf.Deg2Rad) * 1000f / _data.size.y;
            var heights = new float[resolution, resolution];
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    heights[z, x] = slope * Mathf.Max(0f, (float)x / (resolution - 1) - 0.5f);
                }
            }
            _data.SetHeights(0, 0, heights);
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            LoogaClearStamp clear = AddClear(1f);
            clear.SlopeFilter.Set(true, new Vector2(7f, 90f), Vector2.zero, 1f);

            clear.SetSlopeMeasure(SlopeMeasure.Steepness);
            float nearest = EastmostX(_world.EvaluatePlacements().Get(stamp, 0));
            clear.SetSlopeMeasure(SlopeMeasure.Steepness, 100f);
            float reached = EastmostX(_world.EvaluatePlacements().Get(stamp, 0));

            Assert.That(nearest, Is.InRange(450f, 530f), "Without reach the flat side stays up to the ramp");
            Assert.That(nearest - reached, Is.InRange(60f, 140f), "The reach clears about 100 m more");
        }

        private static float EastmostX(IReadOnlyList<StampPlacements.Placement> placements)
        {
            float eastmost = float.MinValue;
            foreach (StampPlacements.Placement placement in placements)
            {
                eastmost = Mathf.Max(eastmost, placement.Position.x);
            }
            return eastmost;
        }

        // A clear stamp takes the falloff texture of its texture source like its own falloff texture.
        [Test]
        public void ClearTakesTheFalloffOfItsTextureSource()
        {
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            LoogaClearStamp clear = AddClear(1f);
            clear.transform.position = new Vector3(500f, 0f, 500f);
            Texture2D mask = HalfMask();
            Texture2D copy = HalfMask();
            try
            {
                clear.Falloff.SetShape(FalloffShape.Texture, Vector2.one, FalloffEasing.Linear);
                clear.Falloff.SetTexture(mask, 0, new Vector2(1f, 0f), 0f, 1f, Vector2.zero);
                int expected = _world.EvaluatePlacements().Get(stamp, 0).Count;
                clear.Falloff.SetTexture(null, 0, new Vector2(1f, 0f), 0f, 1f, Vector2.zero);
                clear.gameObject.AddComponent<LoogaStampTextureSource>().SetFalloffTexture(copy);
                Assert.That(_world.EvaluatePlacements().Get(stamp, 0).Count, Is.EqualTo(expected));
                Assert.That(expected, Is.GreaterThan(0).And.LessThan(Candidates), "The mask clears a part");
            }
            finally
            {
                Object.DestroyImmediate(mask);
                Object.DestroyImmediate(copy);
            }
        }

        // An object stamp takes the second weight noise texture of its texture source like its own noise texture. With
        // an active source and no texture at all, the stamp waits and places nothing.
        [Test]
        public void ObjectStampTakesTheWeightNoiseOfItsTextureSource()
        {
            LoogaObjectStamp stamp = AddObjectStamp(1f, false);
            stamp.Weight2Noise.Set(StampNoiseType.Texture, StampNoiseSpace.World, 10f, 1f, 0f, 0f);
            stamp.Weight2Noise.SetOperation(StampNoiseOperation.Multiply);
            Texture2D mask = HalfMask();
            Texture2D copy = HalfMask();
            try
            {
                stamp.Weight2Noise.SetTexture(mask, new Vector4(0.001f, 0.001f, 0f, 0f), 0);
                stamp.MarkChanged();
                int expected = _world.EvaluatePlacements().Get(stamp, 0).Count;
                stamp.Weight2Noise.SetTexture(null, new Vector4(0.001f, 0.001f, 0f, 0f), 0);
                var source = stamp.gameObject.AddComponent<LoogaStampTextureSource>();
                Assert.That(_world.EvaluatePlacements().Get(stamp, 0).Count, Is.EqualTo(0), "Waits for the texture");
                source.SetWeightNoiseTexture(true, copy);
                Assert.That(_world.EvaluatePlacements().Get(stamp, 0).Count, Is.EqualTo(expected));
                Assert.That(expected, Is.GreaterThan(0).And.LessThan(Candidates), "The mask keeps a part");
            }
            finally
            {
                Object.DestroyImmediate(mask);
                Object.DestroyImmediate(copy);
            }
        }

        private LoogaObjectStamp AddObjectStamp(float weight, bool densityByWeight)
        {
            var stamp = new GameObject("Object Stamp").AddComponent<LoogaObjectStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.position = new Vector3(500f, 0f, 500f);
            stamp.transform.localScale = new Vector3(1000f, 100f, 1000f);
            stamp.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            stamp.SetSpawning(1f, 0, _disk, 1f, weight, false);
            var prototype = new ObjectPrototype(_prefab);
            prototype.SetPick(true, 0f, Vector2.zero, densityByWeight);
            stamp.SetPrototypes(new List<ObjectPrototype> { prototype });
            return stamp;
        }

        private LoogaClearStamp AddClear(float weight)
        {
            var clear = new GameObject("Clear").AddComponent<LoogaClearStamp>();
            clear.transform.SetParent(_world.transform, false);
            clear.transform.localScale = new Vector3(1000f, 100f, 1000f);
            clear.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            clear.SetClear(true, true, true, weight, false);
            return clear;
        }

        // An 8-bit mask: 1 on the west half, 0 on the east half.
        private static Texture2D HalfMask()
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.R8, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new byte[size * size];
            for (int index = 0; index < pixels.Length; index++)
            {
                pixels[index] = (byte)(index % size < size / 2 ? 255 : 0);
            }
            texture.SetPixelData(pixels, 0);
            texture.Apply(false, false);
            return texture;
        }
    }
}
