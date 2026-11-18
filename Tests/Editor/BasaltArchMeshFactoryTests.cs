using LoogaSoft.Terrain.Geology;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class BasaltArchMeshFactoryTests
    {
        [Test]
        public void Create_FollowsCurvedSplineAndClosesBothEnds()
        {
            GameObject gameObject = new("Basalt arch test");
            try
            {
                SplineContainer container = gameObject.AddComponent<SplineContainer>();
                Spline spline = new();
                spline.Add(new BezierKnot(new float3(-50f, 0f, 0f)), TangentMode.AutoSmooth);
                spline.Add(new BezierKnot(new float3(0f, 60f, 0f)), TangentMode.AutoSmooth);
                spline.Add(new BezierKnot(new float3(50f, 0f, 0f)), TangentMode.AutoSmooth);
                container.Spline = spline;

                Mesh mesh = BasaltArchMeshFactory.Create(container, gameObject.transform,
                    5f, 128, 8, 5f, 4f, 0.12f, 0.04f, 0.05f, 71);
                try
                {
                    Assert.That(mesh.vertexCount, Is.GreaterThan(100));
                    Assert.That(mesh.triangles.Length, Is.GreaterThan(300));
                    Assert.That(mesh.bounds.max.y, Is.GreaterThan(55f));
                    Assert.That(mesh.bounds.min.x, Is.LessThan(-49f));
                    Assert.That(mesh.bounds.max.x, Is.GreaterThan(49f));
                    foreach (Vector3 vertex in mesh.vertices)
                    {
                        Assert.That(float.IsFinite(vertex.x));
                        Assert.That(float.IsFinite(vertex.y));
                        Assert.That(float.IsFinite(vertex.z));
                    }
                }
                finally
                {
                    Object.DestroyImmediate(mesh);
                }
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Create_IsDeterministicForSameSplineAndSeed()
        {
            GameObject gameObject = new("Basalt arch determinism test");
            try
            {
                SplineContainer container = gameObject.AddComponent<SplineContainer>();
                container.Spline = new Spline(new[]
                {
                    new BezierKnot(new float3(0f, 0f, 0f)),
                    new BezierKnot(new float3(0f, 20f, 50f)),
                    new BezierKnot(new float3(0f, 0f, 100f))
                });
                for (int index = 0; index < container.Spline.Count; index++)
                    container.Spline.SetTangentMode(index, TangentMode.AutoSmooth);

                Mesh first = BasaltArchMeshFactory.Create(container, gameObject.transform,
                    6f, 64, 7, 4f, 3f, 0.1f, 0.06f, 0.08f, 199);
                Mesh second = BasaltArchMeshFactory.Create(container, gameObject.transform,
                    6f, 64, 7, 4f, 3f, 0.1f, 0.06f, 0.08f, 199);
                try
                {
                    Assert.That(second.vertices, Is.EqualTo(first.vertices));
                    Assert.That(second.triangles, Is.EqualTo(first.triangles));
                }
                finally
                {
                    Object.DestroyImmediate(first);
                    Object.DestroyImmediate(second);
                }
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void CreateBundle_ThinsTowardTheCrownAndConnectsTheFeet()
        {
            GameObject gameObject = new("Basalt column arch test");
            try
            {
                SplineContainer container = CreateArchSpline(gameObject, 300f, 240f);
                var bundle = new BasaltArchBundleSettings
                {
                    ColumnRadius = 4f, CenterWander = 0f, FootSpread = 0f, CoreRadius = 0.25f, CrownReachFalloff = 4f
                };
                Mesh mesh = BasaltArchMeshFactory.CreateBundle(container, gameObject.transform,
                    8f, 128, 30f, 24f, 0.12f, 0.04f, 71, bundle);
                try
                {
                    Vector3[] vertices = mesh.vertices;
                    float footWidth = Extent(vertices, vertex => vertex.x < -270f);
                    float crownWidth = Extent(vertices, vertex => Mathf.Abs(vertex.x) < 4f && vertex.y > 180f);
                    Assert.That(crownWidth, Is.GreaterThan(0f), "Core columns must connect the feet at the crown.");
                    Assert.That(footWidth, Is.GreaterThan(crownWidth * 1.5f));
                    foreach (Vector3 vertex in vertices)
                    {
                        Assert.That(float.IsFinite(vertex.x) && float.IsFinite(vertex.y) && float.IsFinite(vertex.z));
                    }
                }
                finally
                {
                    Object.DestroyImmediate(mesh);
                }
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void CreateBundle_IsDeterministicAndFacesPointOutward()
        {
            GameObject gameObject = new("Basalt column arch determinism test");
            try
            {
                SplineContainer container = CreateArchSpline(gameObject, 300f, 240f);
                var bundle = new BasaltArchBundleSettings { ColumnRadius = 4f };
                Mesh first = BasaltArchMeshFactory.CreateBundle(container, gameObject.transform,
                    8f, 128, 30f, 24f, 0.12f, 0.04f, 71, bundle);
                Mesh second = BasaltArchMeshFactory.CreateBundle(container, gameObject.transform,
                    8f, 128, 30f, 24f, 0.12f, 0.04f, 71, bundle);
                try
                {
                    Assert.That(second.vertices, Is.EqualTo(first.vertices));
                    Assert.That(second.triangles, Is.EqualTo(first.triangles));
                    // Each face normal of a side quad agrees with its stored normal, so no face is inside out.
                    Vector3[] vertices = first.vertices;
                    Vector3[] normals = first.normals;
                    int[] triangles = first.triangles;
                    int agreeing = 0;
                    for (int index = 0; index < triangles.Length; index += 3)
                    {
                        Vector3 a = vertices[triangles[index]];
                        Vector3 geometric = Vector3.Cross(vertices[triangles[index + 1]] - a,
                            vertices[triangles[index + 2]] - a);
                        if (geometric.sqrMagnitude < 1e-8f) continue;
                        Assert.That(Vector3.Dot(geometric.normalized, normals[triangles[index]]),
                            Is.GreaterThan(0.5f), "Triangle winding must match the stored normal.");
                        agreeing++;
                    }
                    Assert.That(agreeing, Is.GreaterThan(1000));
                }
                finally
                {
                    Object.DestroyImmediate(first);
                    Object.DestroyImmediate(second);
                }
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void CreateBundle_WeatheredColumnsAreDeterministicFiniteAndFaceOutward()
        {
            GameObject gameObject = new("Weathered basalt column arch test");
            try
            {
                SplineContainer container = CreateArchSpline(gameObject, 300f, 240f);
                var bundle = new BasaltArchBundleSettings { ColumnRadius = 4f, Weathered = true };
                Mesh smooth = BasaltArchMeshFactory.CreateBundle(container, gameObject.transform,
                    8f, 128, 30f, 24f, 0.12f, 0.04f, 71, new BasaltArchBundleSettings { ColumnRadius = 4f });
                Mesh first = BasaltArchMeshFactory.CreateBundle(container, gameObject.transform,
                    8f, 128, 30f, 24f, 0.12f, 0.04f, 71, bundle);
                Mesh second = BasaltArchMeshFactory.CreateBundle(container, gameObject.transform,
                    8f, 128, 30f, 24f, 0.12f, 0.04f, 71, bundle);
                try
                {
                    Assert.That(second.vertices, Is.EqualTo(first.vertices));
                    Assert.That(first.vertexCount, Is.GreaterThan(smooth.vertexCount),
                        "Drum joints must add rings.");
                    Vector3[] vertices = first.vertices;
                    Vector3[] normals = first.normals;
                    int[] triangles = first.triangles;
                    foreach (Vector3 vertex in vertices)
                    {
                        Assert.That(float.IsFinite(vertex.x) && float.IsFinite(vertex.y) && float.IsFinite(vertex.z));
                    }
                    int agreeing = 0;
                    int total = 0;
                    for (int index = 0; index < triangles.Length; index += 3)
                    {
                        Vector3 a = vertices[triangles[index]];
                        Vector3 geometric = Vector3.Cross(vertices[triangles[index + 1]] - a,
                            vertices[triangles[index + 2]] - a);
                        if (geometric.sqrMagnitude < 1e-8f) continue;
                        total++;
                        if (Vector3.Dot(geometric.normalized, normals[triangles[index]]) > 0f) agreeing++;
                    }
                    // Normals are smoothed along the columns. Only the short steps between shifted drums face along
                    // the column and can disagree with the smoothed normal.
                    Assert.That(agreeing, Is.GreaterThan(total * 0.999f), "Weathered triangles must face outward.");
                }
                finally
                {
                    Object.DestroyImmediate(smooth);
                    Object.DestroyImmediate(first);
                    Object.DestroyImmediate(second);
                }
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        private static SplineContainer CreateArchSpline(GameObject gameObject, float halfSpan, float height)
        {
            SplineContainer container = gameObject.AddComponent<SplineContainer>();
            Spline spline = new();
            spline.Add(new BezierKnot(new float3(-halfSpan, 0f, 0f)), TangentMode.AutoSmooth);
            spline.Add(new BezierKnot(new float3(0f, height, 0f)), TangentMode.AutoSmooth);
            spline.Add(new BezierKnot(new float3(halfSpan, 0f, 0f)), TangentMode.AutoSmooth);
            container.Spline = spline;
            return container;
        }

        // The extent across the arch plane of the vertices that match the filter.
        private static float Extent(Vector3[] vertices, System.Func<Vector3, bool> filter)
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            foreach (Vector3 vertex in vertices)
            {
                if (!filter(vertex)) continue;
                min = Mathf.Min(min, vertex.z);
                max = Mathf.Max(max, vertex.z);
            }
            return max > min ? max - min : 0f;
        }

        [Test]
        public void Create_SideFacesAndNormalsPointOutward()
        {
            GameObject gameObject = new("Basalt arch winding test");
            try
            {
                SplineContainer container = gameObject.AddComponent<SplineContainer>();
                container.Spline = new Spline(new[]
                {
                    new BezierKnot(new float3(0f, 0f, 0f)),
                    new BezierKnot(new float3(0f, 0f, 100f))
                });

                const int sideCount = 8;
                Mesh mesh = BasaltArchMeshFactory.Create(container, gameObject.transform,
                    10f, 64, sideCount, 5f, 4f, 0.12f, 0f, 0f, 227);
                try
                {
                    Vector3[] vertices = mesh.vertices;
                    Vector3[] normals = mesh.normals;
                    int[] triangles = mesh.triangles;
                    int pointsPerRing = sideCount * 2;
                    int sideVertexCount = mesh.vertexCount - (pointsPerRing + 1) * 2;
                    int checkedTriangles = 0;

                    for (int index = 0; index < triangles.Length; index += 3)
                    {
                        int a = triangles[index];
                        int b = triangles[index + 1];
                        int c = triangles[index + 2];
                        if (a >= sideVertexCount || b >= sideVertexCount ||
                            c >= sideVertexCount)
                            continue;

                        Vector3 center = (vertices[a] + vertices[b] + vertices[c]) / 3f;
                        Vector3 radial = new(center.x, center.y, 0f);
                        Vector3 geometricNormal = Vector3.Cross(vertices[b] - vertices[a],
                            vertices[c] - vertices[a]).normalized;

                        Assert.That(Vector3.Dot(geometricNormal, radial.normalized),
                            Is.GreaterThan(0.5f), "Triangle winding must face away from the spline axis.");
                        Assert.That(Vector3.Dot(normals[a], radial.normalized),
                            Is.GreaterThan(0.5f), "Stored side normals must face away from the spline axis.");
                        checkedTriangles++;
                    }

                    Assert.That(checkedTriangles, Is.GreaterThan(0));
                }
                finally
                {
                    Object.DestroyImmediate(mesh);
                }
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
