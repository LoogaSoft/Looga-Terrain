using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // One 100 m tile, 129 samples, 200 m high: one 16-bit step is 400 m / 65535 = 6.1 mm.
    public sealed class SplineFalloffTests
    {
        private const int Resolution = 129;
        private const float RealHeight = 400f;
        private Scene _scene;
        private TerrainData _data;
        private LoogaStampWorld _world;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Height stamps require compute shaders.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _data = new TerrainData { heightmapResolution = Resolution, size = new Vector3(100f, 200f, 100f) };
            GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data);
            SceneManager.MoveGameObjectToScene(tile, _scene);
            var root = new GameObject("Stamp World");
            SceneManager.MoveGameObjectToScene(root, _scene);
            _world = root.AddComponent<LoogaStampWorld>();
            _world.SetTerrains(new[] { tile.GetComponent<UnityEngine.Terrain>() });
            AddStamp(HeightCombineMode.Override, 20f, FalloffShape.Global);
        }

        [TearDown]
        public void TearDown()
        {
            if (_data == null) return;
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_data);
        }

        [Test]
        public void SplineAreaFalloffIsFullInsideAndZeroOutside()
        {
            LoogaSplineArea area = CreateArea(new[] { new float3(30, 0, 30), new float3(70, 0, 30), new float3(70, 0, 70),
                new float3(30, 0, 70) });
            LoogaHeightStamp stamp = AddStamp(HeightCombineMode.Override, 60f, FalloffShape.SplineArea);
            stamp.Falloff.SetSplineArea(area, 10f, 0f);
            ushort[] tile = Evaluate();
            Assert.That(tile[Index(50f, 50f)], Is.EqualTo(Steps(60f)), "Inside");
            Assert.That(tile[Index(5f, 5f)], Is.EqualTo(Steps(20f)), "Outside the falloff range");
            // The weight grows from the spline (0) to the falloff range inside the area (1).
            int edge = tile[Index(50f, 35f)];
            Assert.That(edge, Is.GreaterThan(Steps(20f)).And.LessThan(Steps(60f)), "Inside the falloff range");
        }

        [Test]
        public void SplineAreaEdgeFalloffIsFullAlongTheLineOnBothSides()
        {
            // The edges sit on heightmap samples (0.78125 m apart). The test reads samples at equal distances on both sides.
            LoogaSplineArea area = CreateArea(new[] { new float3(31.25f, 0, 31.25f), new float3(68.75f, 0, 31.25f),
                new float3(68.75f, 0, 68.75f), new float3(31.25f, 0, 68.75f) });
            LoogaHeightStamp stamp = AddStamp(HeightCombineMode.Override, 60f, FalloffShape.SplineArea);
            // Full weight within 4 m of the line, 0 from 12 m.
            stamp.Falloff.SetSplineArea(area, 8f, 12f, edge: true);
            ushort[] tile = Evaluate();
            Assert.That(Mathf.Abs(tile[Index(50f, 31.25f)] - Steps(60f)), Is.LessThanOrEqualTo(1), "On the line");
            Assert.That(Mathf.Abs(tile[Index(50f, 28.125f)] - Steps(60f)), Is.LessThanOrEqualTo(1), "Outside, near");
            Assert.That(Mathf.Abs(tile[Index(50f, 34.375f)] - Steps(60f)), Is.LessThanOrEqualTo(1), "Inside, near");
            Assert.That(tile[Index(50f, 50f)], Is.EqualTo(Steps(20f)), "Area centre");
            Assert.That(tile[Index(50f, 15.625f)], Is.EqualTo(Steps(20f)), "Far outside");
            int fadeInside = tile[Index(50f, 40.625f)];
            int fadeOutside = tile[Index(50f, 21.875f)];
            Assert.That(fadeInside, Is.GreaterThan(Steps(20f)).And.LessThan(Steps(60f)), "Inside fade");
            Assert.That(fadeOutside, Is.GreaterThan(Steps(20f)).And.LessThan(Steps(60f)), "Outside fade");
            // The area field samples at texel centres, so the line moves about half a heightmap sample toward -z.
            // That is 0.05 of the weight here.
            Assert.That(Mathf.Abs(fadeInside - fadeOutside), Is.LessThanOrEqualTo(Steps(26f) - Steps(20f)),
                "The fade is the same on both sides");
        }

        [Test]
        public void SplinePathSetsHeightInsideItsWidth()
        {
            var container = new GameObject("Path spline").AddComponent<SplineContainer>();
            container.transform.SetParent(_world.transform, false);
            container.Spline = new Spline(new[] { new BezierKnot(new float3(10, 30, 50)), new BezierKnot(new float3(90, 30, 50)) });
            var path = new GameObject("Path").AddComponent<LoogaSplinePath>();
            path.transform.SetParent(_world.transform, false);
            path.SetSpline(container, new List<LoogaSplinePath.SplineWidths>(), FalloffEasing.Linear);
            path.SetHeight(HeightCombineMode.Override, 4f, 6f, 0f, null, FalloffEasing.Linear);
            path.SetDistanceField(256, 64, 13f, 6f);
            ushort[] tile = Evaluate();
            Assert.That(Mathf.Abs(tile[Index(50f, 50f)] - Steps(30f)), Is.LessThanOrEqualTo(1), "On the path");
            Assert.That(tile[Index(50f, 80f)], Is.EqualTo(Steps(20f)), "Beyond width and smoothness");
            int embankment = tile[Index(50f, 57f)];
            Assert.That(embankment, Is.GreaterThan(Steps(20f)).And.LessThan(Steps(30f)), "On the embankment");
        }

        [Test]
        public void PaintedAreaScalesGlobalStamp()
        {
            var painted = new GameObject("Painted").AddComponent<LoogaPaintedArea>();
            painted.transform.SetParent(_world.transform, false);
            painted.transform.position = new Vector3(50f, 0f, 50f);
            painted.transform.localScale = new Vector3(100f, 1f, 100f);
            const int size = 64;
            var mask = new byte[size * size * 2];
            for (int z = 0; z < size; z++)
            {
                for (int x = size / 2; x < size; x++)
                {
                    mask[(z * size + x) * 2] = 0xFF;
                    mask[(z * size + x) * 2 + 1] = 0xFF;
                }
            }
            painted.SetMask(mask, size, false);
            LoogaHeightStamp stamp = AddStamp(HeightCombineMode.Override, 60f, FalloffShape.Global);
            stamp.Falloff.SetPaintedArea(painted);
            ushort[] tile = Evaluate();
            Assert.That(tile[Index(20f, 50f)], Is.EqualTo(Steps(20f)), "Mask 0");
            Assert.That(tile[Index(80f, 50f)], Is.EqualTo(Steps(60f)), "Mask 1");
        }

        private LoogaSplineArea CreateArea(float3[] corners)
        {
            var knots = new BezierKnot[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                knots[i] = new BezierKnot(corners[i]);
            }
            var container = new GameObject("Area").AddComponent<SplineContainer>();
            container.transform.SetParent(_world.transform, false);
            container.Spline = new Spline(knots, true);
            var area = container.gameObject.AddComponent<LoogaSplineArea>();
            area.Set(container, 256, 20f, false);
            return area;
        }

        private LoogaHeightStamp AddStamp(HeightCombineMode mode, float height, FalloffShape shape)
        {
            var stamp = new GameObject("Stamp").AddComponent<LoogaHeightStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.position = new Vector3(50f, height, 50f);
            stamp.transform.localScale = new Vector3(400f, 10f, 400f);
            stamp.SetHeight(mode, null, 0f, 1f, Vector2.zero);
            stamp.Falloff.SetShape(shape, new Vector2(0.8f, 1f), FalloffEasing.Linear);
            return stamp;
        }

        private ushort[] Evaluate()
        {
            using StampHeights heights = _world.EvaluateHeights();
            return heights.ReadTile(0);
        }

        private static int Index(float x, float z)
        {
            int column = Mathf.RoundToInt(x / 100f * (Resolution - 1));
            int row = Mathf.RoundToInt(z / 100f * (Resolution - 1));
            return row * Resolution + column;
        }

        private static int Steps(float metres)
        {
            return Mathf.RoundToInt(metres / RealHeight * 65535f);
        }
    }
}
