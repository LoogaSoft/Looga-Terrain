using System.Collections.Generic;
using LoogaSoft.Terrain.Geology;
using NUnit.Framework;
using UnityEngine;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class BasaltColumnGeneratorTests
    {
        [Test]
        public void Layout_IsDeterministicVerticalAndWithinRanges()
        {
            List<BasaltSplineSample> samples = CreateSamples(240f, 3.5f);
            BasaltColumnLayoutSettings settings = CreateSettings();
            List<BasaltColumnPlacement> first = BasaltColumnLayoutGenerator.Generate(samples, settings);
            List<BasaltColumnPlacement> second = BasaltColumnLayoutGenerator.Generate(samples, settings);

            Assert.That(first.Count, Is.GreaterThan(15));
            Assert.That(second.Count, Is.EqualTo(first.Count));
            for (int index = 0; index < first.Count; index++)
            {
                BasaltColumnPlacement a = first[index];
                BasaltColumnPlacement b = second[index];
                Assert.That(a.Matrix, Is.EqualTo(b.Matrix));
                Assert.That(a.SideCount, Is.EqualTo(6));
                Assert.That(a.ShapeVariant, Is.InRange(0, settings.ShapeVariants - 1));
                Assert.That(a.Thickness, Is.InRange(20f, 25f));
                Vector4 up = a.Matrix.GetColumn(1);
                Assert.That(Mathf.Abs(up.y), Is.GreaterThan(Mathf.Abs(up.x)));
                Assert.That(Mathf.Abs(up.y), Is.GreaterThan(Mathf.Abs(up.z)));
            }
        }

        [Test]
        public void Layout_RejectsNearDuplicateCentersWithoutThinningField()
        {
            List<BasaltSplineSample> samples = CreateSamples(220f, 3.5f);
            BasaltColumnLayoutSettings settings = CreateSettings();
            settings.WallDepth = 48f;
            settings.Radius = new Vector2(8f, 15f);
            settings.MinimumCenterSeparation = 0.5f;
            settings.OuterRowDropout = 0f;
            List<BasaltColumnPlacement> placements =
                BasaltColumnLayoutGenerator.Generate(samples, settings);

            for (int first = 0; first < placements.Count; first++)
            for (int second = first + 1; second < placements.Count; second++)
            {
                Vector3 a = placements[first].Matrix.MultiplyPoint3x4(Vector3.down * 0.5f);
                Vector3 b = placements[second].Matrix.MultiplyPoint3x4(Vector3.down * 0.5f);
                float horizontalDistance = Vector2.Distance(
                    new Vector2(a.x, a.z), new Vector2(b.x, b.z));
                float minimum = settings.MinimumCenterSeparation;
                Assert.That(horizontalDistance, Is.GreaterThanOrEqualTo(minimum - 0.001f));
            }
            Assert.That(placements.Count, Is.GreaterThan(25));
        }

        [Test]
        public void Layout_UsesOneSplineRelativeBottomPlane()
        {
            List<BasaltSplineSample> samples = CreateSamples(240f, 16f);
            BasaltColumnLayoutSettings settings = CreateSettings();
            settings.BottomDepth = 200f;
            settings.LeanDegrees = 0f;
            List<BasaltColumnPlacement> placements = BasaltColumnLayoutGenerator.Generate(samples, settings);

            Assert.That(placements.Count, Is.GreaterThan(0));
            foreach (BasaltColumnPlacement placement in placements)
            {
                float bottom = placement.Matrix.GetColumn(3).y - placement.Height * 0.5f;
                Assert.That(bottom, Is.EqualTo(20f).Within(0.0001f));
            }
        }

        [Test]
        public void Layout_LeanKeepsBottomCentersOnSharedPlane()
        {
            List<BasaltSplineSample> samples = CreateSamples(240f, 16f);
            BasaltColumnLayoutSettings settings = CreateSettings();
            settings.BottomDepth = 200f;
            settings.LeanDegrees = 4f;
            List<BasaltColumnPlacement> placements = BasaltColumnLayoutGenerator.Generate(samples, settings);

            Assert.That(placements.Count, Is.GreaterThan(0));
            foreach (BasaltColumnPlacement placement in placements)
            {
                Vector3 bottomCenter = placement.Matrix.MultiplyPoint3x4(Vector3.down * 0.5f);
                Assert.That(bottomCenter.y, Is.EqualTo(20f).Within(0.0001f));
            }
        }

        [Test]
        public void HeightCurve_CanBiasMostColumnsTowardMaximum()
        {
            List<BasaltSplineSample> samples = CreateSamples(500f, 3.5f);
            BasaltColumnLayoutSettings settings = CreateSettings();
            settings.Height = new BasaltRangeDistribution(100f, 200f,
                new AnimationCurve(new Keyframe(0f, 0.55f), new Keyframe(1f, 1f)));
            settings.WallDepth = 1f;
            settings.EndTaperLength = 0f;
            List<BasaltColumnPlacement> placements = BasaltColumnLayoutGenerator.Generate(samples, settings);
            float sum = 0f;
            foreach (BasaltColumnPlacement placement in placements) sum += placement.Height;
            Assert.That(sum / placements.Count, Is.GreaterThan(170f));
        }

        [Test]
        public void OuterRowHeightScale_ControlsTheOuterRowHeight()
        {
            List<BasaltSplineSample> samples = CreateSamples(300f, 3.5f);
            BasaltColumnLayoutSettings settings = CreateSettings();
            settings.UseRelativeHeight = false;
            settings.Height = new BasaltRangeDistribution(100f, 100f);
            settings.WallDepth = 60f;
            settings.BoundaryNoise = 0f;
            settings.EndTaperLength = 0f;
            settings.OuterRowDropout = 0f;
            settings.TopVariation = 0f;

            settings.OuterRowHeightScale = 1f;
            foreach (BasaltColumnPlacement placement in BasaltColumnLayoutGenerator.Generate(samples, settings))
            {
                Assert.That(placement.Height, Is.EqualTo(100f).Within(0.01f));
            }

            settings.OuterRowHeightScale = 0.5f;
            float lowest = float.MaxValue;
            foreach (BasaltColumnPlacement placement in BasaltColumnLayoutGenerator.Generate(samples, settings))
            {
                lowest = Mathf.Min(lowest, placement.Height);
            }
            Assert.That(lowest, Is.LessThan(90f));
        }

        [Test]
        public void FillGaps_PlacesFittedPillarsInEnclosedGaps()
        {
            List<BasaltSplineSample> samples = CreateSamples(300f, 3.5f);
            BasaltColumnLayoutSettings settings = CreateSettings();
            settings.WallDepth = 60f;
            settings.BoundaryNoise = 0f;
            // Missing pillars inside the group leave enclosed gaps.
            settings.Density = 0.75f;
            List<BasaltColumnPlacement> open = BasaltColumnLayoutGenerator.Generate(samples, settings);
            settings.FillGaps = true;
            List<BasaltColumnPlacement> filled = BasaltColumnLayoutGenerator.Generate(samples, settings);

            Assert.That(filled.Count, Is.GreaterThan(open.Count));
            for (int index = 0; index < open.Count; index++)
            {
                Assert.That(filled[index].Matrix, Is.EqualTo(open[index].Matrix));
            }
            for (int index = open.Count; index < filled.Count; index++)
            {
                Vector3 filler = filled[index].Matrix.MultiplyPoint3x4(Vector3.down * 0.5f);
                Assert.That(filled[index].Radius, Is.LessThanOrEqualTo(settings.RadiusMaximum + 0.001f));
                for (int other = 0; other < filled.Count; other++)
                {
                    if (other == index) continue;
                    Vector3 position = filled[other].Matrix.MultiplyPoint3x4(Vector3.down * 0.5f);
                    float distance = Vector2.Distance(new Vector2(filler.x, filler.z), new Vector2(position.x, position.z));
                    Assert.That(distance, Is.GreaterThan(0.55f * (filled[index].Radius + filled[other].Radius)));
                }
            }
        }

        [Test]
        public void RelativeHeight_UsesEscarpmentRangeAndUpperBias()
        {
            List<BasaltSplineSample> samples = CreateSamples(500f, 3.5f);
            BasaltColumnLayoutSettings settings = CreateSettings();
            settings.UseRelativeHeight = true;
            settings.Height = new BasaltRangeDistribution(100f, 200f,
                BasaltColumnLayoutSettings.CreateUpperBiasedDistribution());
            settings.BottomDepth = 200f;
            settings.MinimumEscarpmentHeight = 0.6f;
            settings.MaximumHeightAboveEscarpment = 50f;
            settings.MaximumHeightScale = 1.15f;
            settings.WallDepth = 1f;
            settings.EndTaperLength = 0f;
            settings.OuterRowDropout = 0f;
            settings.TopVariation = 0f;
            List<BasaltColumnPlacement> placements = BasaltColumnLayoutGenerator.Generate(samples, settings);

            float sum = 0f;
            foreach (BasaltColumnPlacement placement in placements)
            {
                Assert.That(placement.Height, Is.InRange(120f, 287.5f));
                sum += placement.Height;
            }
            Assert.That(sum / placements.Count, Is.GreaterThan(225f));
        }

        [Test]
        public void Layout_EncodesDirectionalContactOcclusionForNeighbours()
        {
            List<BasaltSplineSample> samples = CreateSamples(160f, 3.5f);
            BasaltColumnLayoutSettings settings = CreateSettings();
            settings.WallDepth = 36f;
            settings.Density = 1f;
            settings.OuterRowDropout = 0f;
            List<BasaltColumnPlacement> placements =
                BasaltColumnLayoutGenerator.Generate(samples, settings);

            int contacted = 0;
            foreach (BasaltColumnPlacement placement in placements)
            {
                Vector4 contact = placement.ContactOcclusion;
                Assert.That(contact.x, Is.InRange(0f, 1f));
                Assert.That(contact.y, Is.InRange(0f, 1f));
                Assert.That(contact.z, Is.InRange(0f, 1f));
                Assert.That(contact.w, Is.InRange(0f, 1f));
                if (contact.sqrMagnitude > 0.0001f) contacted++;
            }

            Assert.That(contacted, Is.GreaterThan(placements.Count / 2));
        }

        [Test]
        public void BoundaryControls_LowerAndThinTheGroupEnds()
        {
            List<BasaltSplineSample> samples = CreateSamples(240f, 3.5f);
            BasaltColumnLayoutSettings settings = CreateSettings();
            settings.EndTaperLength = 55f;
            settings.EndHeightScale = 0.2f;
            settings.EndDropout = 0.8f;
            List<BasaltColumnPlacement> placements = BasaltColumnLayoutGenerator.Generate(samples, settings);
            float edgeHeight = 0f;
            int edgeCount = 0;
            float middleHeight = 0f;
            int middleCount = 0;
            foreach (BasaltColumnPlacement placement in placements)
            {
                if (placement.NormalizedDistance < 0.12f || placement.NormalizedDistance > 0.88f)
                {
                    edgeHeight += placement.Height;
                    edgeCount++;
                }
                else if (placement.NormalizedDistance > 0.4f && placement.NormalizedDistance < 0.6f)
                {
                    middleHeight += placement.Height;
                    middleCount++;
                }
            }
            Assert.That(edgeCount, Is.GreaterThan(0));
            Assert.That(middleCount, Is.GreaterThan(0));
            Assert.That(edgeHeight / edgeCount, Is.LessThan(middleHeight / middleCount));
        }

        [Test]
        public void EdgeRadiusScale_MakesPillarsSmallerAtTheGroupEnds()
        {
            List<BasaltSplineSample> samples = CreateSamples(400f, 2f);
            BasaltColumnLayoutSettings settings = CreateSettings();
            settings.Radius = new Vector2(10f, 10f);
            settings.EdgeRadiusScale = 0.4f;
            settings.EdgeWidth = 40f;
            settings.EndDropout = 0f;
            // A deep wall keeps the front row out of the outer edge band.
            settings.WallDepth = 100f;
            settings.BoundaryNoise = 0f;
            List<BasaltColumnPlacement> placements = BasaltColumnLayoutGenerator.Generate(samples, settings);
            float edgeRadius = 0f;
            int edgeCount = 0;
            float middleRadius = 0f;
            int middleCount = 0;
            foreach (BasaltColumnPlacement placement in placements)
            {
                if (placement.NormalizedDistance < 0.04f || placement.NormalizedDistance > 0.96f)
                {
                    edgeRadius += placement.Radius;
                    edgeCount++;
                }
                else if (placement.NormalizedDistance > 0.3f && placement.NormalizedDistance < 0.7f &&
                         placement.Row == 0)
                {
                    middleRadius += placement.Radius;
                    middleCount++;
                }
            }
            Assert.That(edgeCount, Is.GreaterThan(0));
            Assert.That(middleCount, Is.GreaterThan(0));
            Assert.That(middleRadius / middleCount, Is.EqualTo(10f).Within(0.001f));
            Assert.That(edgeRadius / edgeCount, Is.LessThan(8f));
        }

        [Test]
        public void Settings_MigrateLegacyThicknessToRadius()
        {
            // JsonUtility ignores FormerlySerializedAs, so the data uses the current name of the legacy field.
            // Scene and prefab data use the old name "Thickness", which the serializer maps to the same field.
            BasaltColumnLayoutSettings settings = JsonUtility.FromJson<BasaltColumnLayoutSettings>(
                "{\"_legacyThickness\":{\"_range\":{\"x\":20.0,\"y\":30.0}}}");
            Assert.That(settings.Radius.x, Is.EqualTo(10f).Within(0.0001f));
            Assert.That(settings.Radius.y, Is.EqualTo(15f).Within(0.0001f));

            BasaltColumnLayoutSettings current = JsonUtility.FromJson<BasaltColumnLayoutSettings>(
                JsonUtility.ToJson(new BasaltColumnLayoutSettings { Radius = new Vector2(4f, 6f) }));
            Assert.That(current.Radius, Is.EqualTo(new Vector2(4f, 6f)));
        }

        [Test]
        public void MeshFactory_ProducesWornCrackedPolygonalMesh()
        {
            Mesh mesh = BasaltColumnMeshFactory.Create(7, 6, 2, 0.4f, 0.1f, 0.08f, 99);
            try
            {
                Assert.That(mesh.vertexCount, Is.GreaterThan(7 * 4));
                Assert.That(mesh.colors.Length, Is.EqualTo(mesh.vertexCount));
                Assert.That(mesh.uv2.Length, Is.EqualTo(mesh.vertexCount));
                Assert.That(mesh.bounds.size.y, Is.InRange(0.97f, 1f));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MeshFactory_CapRimsExactlyMatchSideRims()
        {
            const int sides = 6;
            const int verticalSegments = 6;
            const int pointsPerRing = sides * 2;
            Mesh mesh = BasaltColumnMeshFactory.Create(sides, verticalSegments, 2, 0.4f, 0.1f, 0.08f, 99);
            try
            {
                Vector3[] vertices = mesh.vertices;
                int sideVertexCount = pointsPerRing * verticalSegments * 4;
                int topCapStart = sideVertexCount;
                int bottomCapStart = topCapStart + 1 + pointsPerRing;
                for (int point = 0; point < pointsPerRing; point++)
                {
                    int topSideVertex = ((verticalSegments - 1) * pointsPerRing + point) * 4 + 1;
                    int bottomSideVertex = point * 4;
                    Assert.That(Vector3.Distance(vertices[topSideVertex], vertices[topCapStart + 1 + point]),
                        Is.LessThan(0.00001f));
                    Assert.That(Vector3.Distance(vertices[bottomSideVertex], vertices[bottomCapStart + 1 + point]),
                        Is.LessThan(0.00001f));
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MeshFactory_AllSideSeamsAreClosedIncludingWrapSeam()
        {
            const int sides = 6;
            const int verticalSegments = 6;
            const int pointsPerRing = sides * 2;
            Mesh mesh = BasaltColumnMeshFactory.Create(sides, verticalSegments, 4, 0.4f, 0.1f, 0.08f, 99);
            try
            {
                Vector3[] vertices = mesh.vertices;
                for (int ring = 0; ring < verticalSegments; ring++)
                {
                    for (int point = 0; point < pointsPerRing; point++)
                    {
                        int nextPoint = (point + 1) % pointsPerRing;
                        int current = (ring * pointsPerRing + point) * 4;
                        int next = (ring * pointsPerRing + nextPoint) * 4;
                        Assert.That(Vector3.Distance(vertices[current + 3], vertices[next]),
                            Is.LessThan(0.00001f));
                        Assert.That(Vector3.Distance(vertices[current + 2], vertices[next + 1]),
                            Is.LessThan(0.00001f));
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MeshFactory_EncodesSoftChamferHighlightCoordinates()
        {
            const int sides = 6;
            const int verticalSegments = 8;
            const int pointsPerRing = sides * 2;
            Mesh mesh = BasaltColumnMeshFactory.Create(sides, verticalSegments, 4,
                0.4f, 0.1f, 0.08f, 99);
            try
            {
                Vector2[] masks = mesh.uv2;
                for (int point = 0; point < pointsPerRing; point++)
                {
                    int faceStart = point * 4;
                    if ((point & 1) == 0)
                    {
                        Assert.That(masks[faceStart].x, Is.EqualTo(0f));
                        Assert.That(masks[faceStart + 1].x, Is.EqualTo(0f));
                        Assert.That(masks[faceStart + 2].x, Is.EqualTo(1f));
                        Assert.That(masks[faceStart + 3].x, Is.EqualTo(1f));
                    }
                    else
                    {
                        Assert.That(masks[faceStart].x, Is.EqualTo(2f));
                        Assert.That(masks[faceStart + 1].x, Is.EqualTo(2f));
                        Assert.That(masks[faceStart + 2].x, Is.EqualTo(3f));
                        Assert.That(masks[faceStart + 3].x, Is.EqualTo(3f));
                    }
                    Assert.That(masks[faceStart].y, Is.EqualTo(0f));
                    Assert.That(masks[faceStart + 1].y,
                        Is.EqualTo(1f / verticalSegments).Within(0.00001f));
                }

                int topCapStart = pointsPerRing * verticalSegments * 4;
                int bottomCapStart = topCapStart + 1 + pointsPerRing;
                Assert.That(masks[topCapStart], Is.EqualTo(new Vector2(-1f, 0f)));
                Assert.That(masks[topCapStart + 1], Is.EqualTo(new Vector2(-1f, 1f)));
                Assert.That(masks[bottomCapStart], Is.EqualTo(new Vector2(-2f, 0f)));
                Assert.That(masks[bottomCapStart + 1], Is.EqualTo(new Vector2(-2f, 1f)));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MeshFactory_EncodesPillarLocalCrackCoordinates()
        {
            Mesh mesh = BasaltColumnMeshFactory.Create(6, 8, 4,
                0.4f, 0.1f, 0.08f, 99);
            try
            {
                List<Vector4> pillarData = new();
                mesh.GetUVs(2, pillarData);
                List<Vector4> contactData = new();
                mesh.GetUVs(3, contactData);
                Assert.That(pillarData.Count, Is.EqualTo(mesh.vertexCount));
                Assert.That(contactData.Count, Is.EqualTo(mesh.vertexCount));
                Vector3[] vertices = mesh.vertices;
                for (int index = 0; index < vertices.Length; index++)
                {
                    Assert.That(new Vector3(pillarData[index].x, pillarData[index].y,
                        pillarData[index].z), Is.EqualTo(vertices[index]));
                    Assert.That(pillarData[index].w, Is.EqualTo(-1f));
                    Assert.That(contactData[index], Is.EqualTo(-Vector4.one));
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MeshFactory_SmoothsSideNormalsAndBlendsIntoCap()
        {
            const int sides = 6;
            const int verticalSegments = 8;
            const int pointsPerRing = sides * 2;
            Mesh mesh = BasaltColumnMeshFactory.Create(sides, verticalSegments, 4, 0.4f, 0.1f, 0.08f, 99);
            try
            {
                Vector3[] normals = mesh.normals;
                for (int ring = 0; ring < verticalSegments; ring++)
                {
                    for (int point = 0; point < pointsPerRing; point++)
                    {
                        int previous = (point - 1 + pointsPerRing) % pointsPerRing;
                        int currentFace = (ring * pointsPerRing + point) * 4;
                        int previousFace = (ring * pointsPerRing + previous) * 4;
                        Assert.That(Vector3.Distance(normals[currentFace], normals[previousFace + 3]),
                            Is.LessThan(0.00001f));
                        Assert.That(Vector3.Distance(normals[currentFace + 1], normals[previousFace + 2]),
                            Is.LessThan(0.00001f));
                    }
                }

                int topCapStart = pointsPerRing * verticalSegments * 4;
                Assert.That(normals[topCapStart], Is.EqualTo(Vector3.up));
                int topCapRim = topCapStart + 1;
                Assert.That(Vector3.Dot(normals[topCapRim], Vector3.up), Is.EqualTo(1f).Within(0.00001f));
                int topSideVertex = ((verticalSegments - 1) * pointsPerRing) * 4 + 1;
                Assert.That(Vector3.Distance(normals[topSideVertex], normals[topCapRim]),
                    Is.LessThan(0.00001f));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MeshFactory_ProducesDeterministicChamferedClosedMesh()
        {
            const int sides = 6;
            const int verticalSegments = 6;
            const int pointsPerRing = sides * 2;
            Mesh first = BasaltColumnMeshFactory.Create(sides, verticalSegments, 3, 0.65f, 0.08f, 0.1f, 99);
            Mesh second = BasaltColumnMeshFactory.Create(sides, verticalSegments, 3, 0.65f, 0.08f, 0.1f, 99);
            try
            {
                Assert.That(first.vertices, Is.EqualTo(second.vertices));
                int expected = pointsPerRing * verticalSegments * 4 + 2 * (1 + pointsPerRing);
                Assert.That(first.vertexCount, Is.EqualTo(expected));
                int topCapStart = pointsPerRing * verticalSegments * 4;
                for (int point = 0; point < pointsPerRing; point++)
                {
                    float y = first.vertices[topCapStart + 1 + point].y;
                    Assert.That(y, Is.EqualTo(first.vertices[topCapStart].y).Within(0.00001f));
                }
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void MeshFactory_TopBevelNeverRisesAboveFlatTopFace()
        {
            const int sides = 6;
            const int verticalSegments = 8;
            const int pointsPerRing = sides * 2;
            Mesh mesh = BasaltColumnMeshFactory.Create(sides, verticalSegments, 5, 0.8f, 0.1f, 0.12f, 99);
            try
            {
                Vector3[] vertices = mesh.vertices;
                int topCapStart = pointsPerRing * verticalSegments * 4;
                float faceHeight = vertices[topCapStart].y;
                for (int point = 0; point < pointsPerRing; point++)
                {
                    Assert.That(vertices[topCapStart + 1 + point].y,
                        Is.LessThanOrEqualTo(faceHeight + 0.00001f));
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MeshFactory_TopBevelContractsMonotonicallyTowardCap()
        {
            const int sides = 6;
            const int verticalSegments = 8;
            const int pointsPerRing = sides * 2;
            Mesh mesh = BasaltColumnMeshFactory.Create(sides, verticalSegments, 2, 0.65f, 0.08f, 0.08f, 99);
            try
            {
                Vector3[] vertices = mesh.vertices;
                int point = 4;
                Vector3 lower = vertices[((verticalSegments - 3) * pointsPerRing + point) * 4];
                Vector3 bevelStart = vertices[((verticalSegments - 2) * pointsPerRing + point) * 4];
                Vector3 bevelMiddle = vertices[((verticalSegments - 1) * pointsPerRing + point) * 4];
                Vector3 capEdge = vertices[((verticalSegments - 1) * pointsPerRing + point) * 4 + 1];
                float lowerRadius = new Vector2(lower.x, lower.z).magnitude;
                float bevelStartRadius = new Vector2(bevelStart.x, bevelStart.z).magnitude;
                float bevelMiddleRadius = new Vector2(bevelMiddle.x, bevelMiddle.z).magnitude;
                float capRadius = new Vector2(capEdge.x, capEdge.z).magnitude;
                Assert.That(bevelStartRadius, Is.EqualTo(lowerRadius).Within(0.00001f));
                Assert.That(bevelMiddleRadius, Is.LessThan(bevelStartRadius));
                Assert.That(capRadius, Is.LessThan(bevelMiddleRadius));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MeshFactory_KeepsWearBevelsNarrowOnTallColumns()
        {
            const int sides = 6;
            const int verticalSegments = 8;
            Mesh mesh = BasaltColumnMeshFactory.Create(sides, verticalSegments, 1, 0.5f, 0.08f, 0.08f, 99);
            try
            {
                // Vertex one is the upper edge of the first wall segment. Keeping it close to
                // -0.5 means the normalized bevel remains a narrow band after height scaling.
                Assert.That(mesh.vertices[1].y, Is.LessThan(-0.48f));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        private static BasaltColumnLayoutSettings CreateSettings() => new()
        {
            WallDepth = 18f,
            Density = 1f,
            Height = new BasaltRangeDistribution(100f, 200f),
            Radius = new Vector2(10f, 12.5f),
            EdgeRadiusScale = 1f,
            RadiusJitter = 0f,
            MinimumSides = 6,
            MaximumSides = 6,
            ShapeVariants = 6,
            EndTaperLength = 0f,
            Seed = 42
        };

        private static List<BasaltSplineSample> CreateSamples(float length, float spacing)
        {
            int count = Mathf.CeilToInt(length / spacing) + 1;
            List<BasaltSplineSample> result = new(count);
            for (int index = 0; index < count; index++)
            {
                float distance = Mathf.Min(length, index * spacing);
                result.Add(new BasaltSplineSample(new Vector3(distance, 220f, 0f), Vector3.right,
                    distance, distance / length));
            }
            return result;
        }
    }
}
