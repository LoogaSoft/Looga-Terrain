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
