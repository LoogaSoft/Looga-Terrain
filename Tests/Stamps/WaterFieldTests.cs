using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // Two 100 m tiles side by side, 129 samples, 200 m high. Water fields use 256 samples (0.39 m) and 32 m reach.
    public sealed class WaterFieldTests
    {
        private const int Resolution = 129;
        private const float RealHeight = 400f;
        private const int FieldResolution = 256;
        private const float MaxDistance = 32f;
        private Scene _scene;
        private TerrainData[] _data;
        private UnityEngine.Terrain[] _terrains;
        private LoogaStampWorld _world;
        private readonly List<Mesh> _meshes = new();

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Water fields require compute shaders.");
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
            foreach (Mesh mesh in _meshes)
            {
                Object.DestroyImmediate(mesh);
            }
            _meshes.Clear();
        }

        [Test]
        public void BoundaryEdgesIgnoreSplitVertices()
        {
            // Two triangles without shared vertices, like a UV seam: the diagonal must not count as an edge.
            Mesh mesh = CreateMesh(new[]
            {
                new Vector3(0, 0, 0), new Vector3(0, 0, 10), new Vector3(10, 0, 10),
                new Vector3(0, 0, 0), new Vector3(10, 0, 10), new Vector3(10, 0, 0)
            }, new[] { 0, 1, 2, 3, 4, 5 });
            var edges = new List<Vector4>();
            WaterDistanceField.GetBoundaryEdges(mesh, Matrix4x4.identity, edges);
            Assert.That(edges.Count, Is.EqualTo(4));
        }

        // The rectangle is off-centre and crosses the tile border, so a flipped or shifted field fails.
        [TestCase(0)]
        [TestCase(1)]
        public void FieldGivesExactSignedDistanceAndLevel(int tile)
        {
            var outer = Rect.MinMaxRect(60f, 30f, 130f, 80f);
            LoogaWaterArea area = CreateArea(CreateRectSource(outer, null, 10f));
            AssertField(tile, area, point => SignedDistance(point, outer, null), 10f);
        }

        [Test]
        public void HoleIsOutsideTheWater()
        {
            var outer = Rect.MinMaxRect(20f, 20f, 80f, 80f);
            var hole = Rect.MinMaxRect(40f, 40f, 60f, 60f);
            LoogaWaterArea area = CreateArea(CreateRectSource(outer, hole, 5f));
            AssertField(0, area, point => SignedDistance(point, outer, hole), 5f);
        }

        [Test]
        public void OverlappingSourcesGiveLevelOfNearestEdge()
        {
            var low = Rect.MinMaxRect(10f, 10f, 60f, 60f);
            var high = Rect.MinMaxRect(40f, 40f, 90f, 90f);
            LoogaWaterArea area = CreateArea(CreateRectSource(low, null, 4f), CreateRectSource(high, null, 7f));
            Vector2[] field = ReadField(0, area);
            Assert.That(Sample(field, 0, 85f, 85f).y, Is.EqualTo(7f).Within(1e-4f), "Near the high edges");
            Assert.That(Sample(field, 0, 15f, 15f).y, Is.EqualTo(4f).Within(1e-4f), "Near the low edges");
            Assert.That(Sample(field, 0, 50f, 50f).x, Is.LessThan(0f), "Overlap is inside");
        }

        [Test]
        public void SourcesThatMeetEndToEndHaveNoEdgeBetweenThem()
        {
            // Two rivers that meet at x = 50 m, like a river split into two RAM splines. The shared line is water.
            var upper = Rect.MinMaxRect(10f, 30f, 50f, 70f);
            var lower = Rect.MinMaxRect(50f, 30f, 90f, 70f);
            LoogaWaterArea area = CreateArea(CreateRectSource(upper, null, 3f), CreateRectSource(lower, null, 3f));
            Vector2[] field = ReadField(0, area);
            Assert.That(Sample(field, 0, 50.2f, 50f).x, Is.EqualTo(-20f).Within(0.5f), "Middle of the shared line");
            Assert.That(Sample(field, 0, 50.2f, 33f).x, Is.EqualTo(-3f).Within(0.5f), "Near the outer edge");
            Assert.That(Sample(field, 0, 50.2f, 75f).x, Is.EqualTo(5f).Within(0.5f), "Outside the water");
        }

        [Test]
        public void SlopedRiverGivesLevelAtNearestEdgePoint()
        {
            // A strip from x 0 to 100 m, z 46 to 54 m, whose surface rises 0.1 m for each metre along x.
            var vertices = new Vector3[22];
            for (int i = 0; i <= 10; i++)
            {
                vertices[i * 2] = new Vector3(i * 10f, 2f + i, 46f);
                vertices[i * 2 + 1] = new Vector3(i * 10f, 2f + i, 54f);
            }
            var triangles = new List<int>();
            for (int i = 0; i < 10; i++)
            {
                int a = i * 2;
                triangles.AddRange(new[] { a, a + 1, a + 3, a, a + 3, a + 2 });
            }
            var filter = new GameObject("River").AddComponent<MeshFilter>();
            filter.transform.SetParent(_world.transform, false);
            filter.sharedMesh = CreateMesh(vertices, triangles.ToArray());
            LoogaWaterArea area = CreateArea(filter.gameObject.AddComponent<LoogaMeshWaterSource>());
            Vector2[] field = ReadField(0, area);
            float texel = _data[0].heightmapScale.x * Resolution / FieldResolution;
            foreach (float z in new[] { 60f, 50f, 40f })
            {
                int column = Mathf.FloorToInt(35.3f / texel), row = Mathf.FloorToInt(z / texel);
                float x = (column + 0.5f) * texel;
                Assert.That(field[row * FieldResolution + column].y, Is.EqualTo(2f + 0.1f * x).Within(0.01f), "z " + z);
            }
            Assert.That(Sample(field, 0, 50f, 95f).y, Is.EqualTo(-WaterDistanceField.NotFound), "Beyond the reach");
        }

        [Test]
        public void SourceOutOfReachGivesNoWater()
        {
            LoogaWaterArea area = CreateArea(CreateRectSource(Rect.MinMaxRect(400f, 400f, 450f, 450f), null, 3f));
            Vector2[] field = ReadField(0, area);
            Assert.That(field, Has.All.EqualTo(new Vector2(WaterDistanceField.NotFound, -WaterDistanceField.NotFound)));
        }

        [Test]
        public void CacheKeepsFieldUntilInvalidated()
        {
            LoogaWaterArea area = CreateArea(CreateRectSource(Rect.MinMaxRect(20f, 20f, 80f, 80f), null, 3f));
            using var evaluator = new HeightStampEvaluator();
            var tile = new StampTile(_terrains[0]);
            evaluator.WaterFields.Get(tile, area);
            evaluator.WaterFields.Get(tile, area);
            Assert.That(evaluator.WaterFields.BuildCount, Is.EqualTo(1), "Built once");
            Assert.That(evaluator.WaterFields.CacheHits, Is.EqualTo(1), "Second call from the cache");
            evaluator.WaterFields.Invalidate(area, Rect.MinMaxRect(150f, 0f, 160f, 10f));
            evaluator.WaterFields.Get(tile, area);
            Assert.That(evaluator.WaterFields.BuildCount, Is.EqualTo(1), "Region on the other tile");
            evaluator.WaterFields.Invalidate(area, Rect.MinMaxRect(10f, 10f, 20f, 20f));
            evaluator.WaterFields.Get(tile, area);
            Assert.That(evaluator.WaterFields.BuildCount, Is.EqualTo(2), "Region on this tile");
        }

        // Boost = range = 6 m: weight 1 inside water and at the edge, 0 at 6 m outside.
        [TestCase(HeightMapping.MicroVerseParity)]
        [TestCase(HeightMapping.WorldAligned)]
        public void WaterAreaFalloffFollowsTheEdgeDistance(HeightMapping mapping)
        {
            var serialized = new UnityEditor.SerializedObject(_world);
            serialized.FindProperty("_mapping").enumValueIndex = (int)mapping;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var water = Rect.MinMaxRect(20f, 20f, 50f, 80f);
            LoogaWaterArea area = CreateArea(CreateRectSource(water, null, 2f));
            AddStamp(20f, FalloffShape.Global);
            LoogaHeightStamp stamp = AddStamp(60f, FalloffShape.WaterArea);
            stamp.Falloff.SetWaterArea(area, 6f, 6f);
            ushort[] heights;
            using (StampHeights result = _world.EvaluateHeights())
            {
                heights = result.ReadTile(0);
            }
            // Row z = 50 m crosses the right water edge at x = 50 m, far from the corners.
            int row = Mathf.RoundToInt(50f / 100f * (Resolution - 1));
            for (int column = 0; column < Resolution; column++)
            {
                // Both mappings read area fields at tile UV (i + 0.5) / resolution over the sample span.
                float x = (column + 0.5f) / Resolution * _data[0].heightmapScale.x * Resolution;
                if (x < 25f || x > 70f) continue;
                float weight = Mathf.Clamp01((6f - (x - 50f)) / 6f);
                int expected = Steps(Mathf.Lerp(20f, 60f, weight));
                Assert.That(Mathf.Abs(heights[row * Resolution + column] - expected), Is.LessThanOrEqualTo(4),
                    "x " + x.ToString("F2"));
            }
        }

        [Test]
        public void MovedRiverVertexChangesOnlyNearbyEdges()
        {
            // A river strip 100 m long and 8 m wide, with 1 m segments.
            const int segments = 100;
            var vertices = new Vector3[(segments + 1) * 2];
            for (int i = 0; i <= segments; i++)
            {
                vertices[i * 2] = new Vector3(i, 3f, 46f);
                vertices[i * 2 + 1] = new Vector3(i, 3f, 54f);
            }
            var triangles = new List<int>();
            for (int i = 0; i < segments; i++)
            {
                int a = i * 2;
                triangles.AddRange(new[] { a, a + 1, a + 3, a, a + 3, a + 2 });
            }
            Mesh mesh = CreateMesh(vertices, triangles.ToArray());
            var stored = new HashSet<LoogaSoft.Terrain.Stamps.Editor.LoogaStampWorldUpdater.EdgeKey>();
            var edges = new List<Vector4>();
            WaterDistanceField.GetBoundaryEdges(mesh, Matrix4x4.identity, edges);
            Rect? first = LoogaSoft.Terrain.Stamps.Editor.LoogaStampWorldUpdater.ReplaceEdges(stored, edges);
            Assert.That(first.HasValue && first.Value.width >= 99f, Is.True, "First capture covers the river");

            vertices[25 * 2 + 1] = new Vector3(25f, 3f, 55f);
            mesh.vertices = vertices;
            edges.Clear();
            WaterDistanceField.GetBoundaryEdges(mesh, Matrix4x4.identity, edges);
            Rect? changed = LoogaSoft.Terrain.Stamps.Editor.LoogaStampWorldUpdater.ReplaceEdges(stored, edges);
            Assert.That(changed.HasValue, Is.True, "Edit found");
            Assert.That(changed.Value.xMin, Is.EqualTo(24f).Within(0.01f));
            Assert.That(changed.Value.xMax, Is.EqualTo(26f).Within(0.01f));
            Assert.That(LoogaSoft.Terrain.Stamps.Editor.LoogaStampWorldUpdater.ReplaceEdges(stored, edges), Is.Null,
                "Same edges again");
        }

        // The meshes are flat, so every edge in reach gives the same level.
        private void AssertField(int tile, LoogaWaterArea area, System.Func<Vector2, float> distance, float level)
        {
            Vector2[] field = ReadField(tile, area);
            float span = _data[tile].heightmapScale.x * Resolution;
            float texel = span / FieldResolution;
            Vector3 origin = _terrains[tile].transform.position;
            int checkedSamples = 0;
            for (int z = 0; z < FieldResolution; z += 3)
            {
                for (int x = 0; x < FieldResolution; x += 3)
                {
                    var point = new Vector2(origin.x + (x + 0.5f) * texel, origin.z + (z + 0.5f) * texel);
                    Vector2 value = field[z * FieldResolution + x];
                    float expected = distance(point);
                    string where = "at " + point.ToString("F2");
                    if (Mathf.Abs(expected) > MaxDistance + 0.01f)
                    {
                        Assert.That(Mathf.Abs(value.x), Is.EqualTo(WaterDistanceField.NotFound), "Not found " + where);
                        float beyond = expected < 0f ? level : -WaterDistanceField.NotFound;
                        Assert.That(value.y, Is.EqualTo(beyond).Within(1e-4f), "Level beyond reach " + where);
                        continue;
                    }
                    if (Mathf.Abs(expected) > MaxDistance - 0.01f) continue;
                    Assert.That(Mathf.Abs(value.x), Is.EqualTo(Mathf.Abs(expected)).Within(0.01f), "Distance " + where);
                    Assert.That(value.y, Is.EqualTo(level).Within(1e-4f), "Level " + where);
                    if (Mathf.Abs(expected) > texel)
                    {
                        Assert.That(Mathf.Sign(value.x), Is.EqualTo(Mathf.Sign(expected)), "Sign " + where);
                    }
                    checkedSamples++;
                }
            }
            Assert.That(checkedSamples, Is.GreaterThan(100));
        }

        private Vector2[] ReadField(int tile, LoogaWaterArea area)
        {
            using var evaluator = new HeightStampEvaluator();
            Texture texture = evaluator.WaterFields.Get(new StampTile(_terrains[tile]), area);
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, 0);
            request.WaitForCompletion();
            return request.GetData<Vector2>().ToArray();
        }

        private Vector2 Sample(Vector2[] field, int tile, float x, float z)
        {
            float texel = _data[tile].heightmapScale.x * Resolution / FieldResolution;
            Vector3 origin = _terrains[tile].transform.position;
            int column = Mathf.FloorToInt((x - origin.x) / texel);
            int row = Mathf.FloorToInt((z - origin.z) / texel);
            return field[row * FieldResolution + column];
        }

        // Exact signed distance to the boundary of a rectangle with an optional rectangular hole.
        private static float SignedDistance(Vector2 point, Rect outer, Rect? hole)
        {
            float distance = BoundaryDistance(point, outer);
            bool inside = outer.Contains(point);
            if (hole.HasValue)
            {
                distance = Mathf.Min(distance, BoundaryDistance(point, hole.Value));
                inside &= !hole.Value.Contains(point);
            }
            return inside ? -distance : distance;
        }

        private static float BoundaryDistance(Vector2 point, Rect rect)
        {
            if (rect.Contains(point))
            {
                return Mathf.Min(Mathf.Min(point.x - rect.xMin, rect.xMax - point.x),
                    Mathf.Min(point.y - rect.yMin, rect.yMax - point.y));
            }
            float dx = Mathf.Max(rect.xMin - point.x, 0f, point.x - rect.xMax);
            float dz = Mathf.Max(rect.yMin - point.y, 0f, point.y - rect.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private LoogaWaterArea CreateArea(params LoogaWaterSource[] sources)
        {
            var area = new GameObject("Water Area").AddComponent<LoogaWaterArea>();
            area.transform.SetParent(_world.transform, false);
            area.Set(sources, FieldResolution, MaxDistance);
            return area;
        }

        // A rectangle at a level, or a ring around a hole, from a 4 x 4 vertex grid.
        private LoogaMeshWaterSource CreateRectSource(Rect outer, Rect? hole, float level)
        {
            Rect inner = hole ?? Rect.MinMaxRect(outer.center.x - 1f, outer.center.y - 1f, outer.center.x + 1f,
                outer.center.y + 1f);
            float[] xs = { outer.xMin, inner.xMin, inner.xMax, outer.xMax };
            float[] zs = { outer.yMin, inner.yMin, inner.yMax, outer.yMax };
            var vertices = new Vector3[16];
            for (int z = 0; z < 4; z++)
            {
                for (int x = 0; x < 4; x++)
                {
                    vertices[z * 4 + x] = new Vector3(xs[x], level, zs[z]);
                }
            }
            var triangles = new List<int>();
            for (int z = 0; z < 3; z++)
            {
                for (int x = 0; x < 3; x++)
                {
                    if (hole.HasValue && x == 1 && z == 1) continue;
                    int corner = z * 4 + x;
                    triangles.AddRange(new[] { corner, corner + 4, corner + 5, corner, corner + 5, corner + 1 });
                }
            }
            var source = new GameObject("Water").AddComponent<MeshFilter>();
            source.transform.SetParent(_world.transform, false);
            source.sharedMesh = CreateMesh(vertices, triangles.ToArray());
            return source.gameObject.AddComponent<LoogaMeshWaterSource>();
        }

        private Mesh CreateMesh(Vector3[] vertices, int[] triangles)
        {
            var mesh = new Mesh { vertices = vertices, triangles = triangles };
            mesh.RecalculateBounds();
            _meshes.Add(mesh);
            return mesh;
        }

        private LoogaHeightStamp AddStamp(float height, FalloffShape shape)
        {
            var stamp = new GameObject("Stamp").AddComponent<LoogaHeightStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.position = new Vector3(100f, height, 50f);
            stamp.transform.localScale = new Vector3(400f, 10f, 400f);
            stamp.SetHeight(HeightCombineMode.Override, null, 0f, 1f, Vector2.zero);
            stamp.Falloff.SetShape(shape, new Vector2(0.8f, 1f), FalloffEasing.Linear);
            return stamp;
        }

        private static int Steps(float metres)
        {
            return Mathf.RoundToInt(metres / RealHeight * 65535f);
        }
    }
}
