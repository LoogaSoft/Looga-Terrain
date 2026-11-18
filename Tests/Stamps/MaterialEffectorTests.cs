using System.Collections.Generic;
using System.Linq;
using LoogaSoft.Instancing;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    public sealed class MaterialEffectorTests
    {
        private const string MarkedShader = "Hidden/Looga/Tests/Effector Targets";

        private Scene _scene;
        private readonly List<Object> _created = new();
        private readonly List<string> _properties = new();
        private readonly List<InstanceEffectorOperation> _operations = new();
        private readonly List<Vector4> _values = new();

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created)
            {
                if (created)
                {
                    Object.DestroyImmediate(created);
                }
            }
            _created.Clear();
            EditorSceneManager.ClosePreviewScene(_scene);
        }

        [Test]
        public void OneConstantVariantGivesOneSharedRow()
        {
            LoogaMaterialEffector effector = CreateEffector(new LoogaMaterialEffector.Variant("A", 1f, new[]
            {
                new LoogaMaterialEffector.Value("_TestFloat", InstanceEffectorOperation.Set, new Vector4(5f, 0f, 0f, 0f))
            }));
            bool built = effector.Build(CreatePrefab(MarkedShader), Placements(10), _properties, _operations, _values);

            Assert.That(built, Is.True);
            Assert.That(_properties, Is.EqualTo(new[] { "_TestFloat" }));
            Assert.That(_operations, Is.EqualTo(new[] { InstanceEffectorOperation.Set }));
            Assert.That(_values[0].x, Is.EqualTo(5f));
        }

        [Test]
        public void PrefabWithoutMarkedPropertiesGetsNoValues()
        {
            LoogaMaterialEffector effector = CreateEffector(new LoogaMaterialEffector.Variant("A", 1f, new[]
            {
                new LoogaMaterialEffector.Value("_TestFloat", InstanceEffectorOperation.Set, Vector4.one)
            }));
            bool built = effector.Build(CreatePrefab("Universal Render Pipeline/Lit"), Placements(10), _properties,
                _operations, _values);

            Assert.That(built, Is.False);
            Assert.That(_properties, Is.Empty);
        }

        [Test]
        public void VariantsArePickedPerPlacementByWeightAndStay()
        {
            LoogaMaterialEffector effector = CreateEffector(
                new LoogaMaterialEffector.Variant("Float", 1f, new[]
                {
                    new LoogaMaterialEffector.Value("_TestFloat", InstanceEffectorOperation.Set, Vector4.one)
                }),
                new LoogaMaterialEffector.Variant("Color", 1f, new[]
                {
                    new LoogaMaterialEffector.Value("_TestColor", InstanceEffectorOperation.Multiply, new Vector4(2f, 2f, 2f, 1f))
                }));
            GameObject prefab = CreatePrefab(MarkedShader);
            List<InstanceSet.Placement> placements = Placements(400);
            Assert.That(effector.Build(prefab, placements, _properties, _operations, _values), Is.True);

            Assert.That(_properties, Is.EqualTo(new[] { "_TestFloat", "_TestColor" }));
            Assert.That(_operations.Count, Is.EqualTo(placements.Count * 2));
            int floats = 0;
            for (int placement = 0; placement < placements.Count; placement++)
            {
                InstanceEffectorOperation first = _operations[placement * 2];
                InstanceEffectorOperation second = _operations[placement * 2 + 1];
                // Each placement takes one variant: the other column keeps the material value.
                Assert.That((first == InstanceEffectorOperation.Set) != (second == InstanceEffectorOperation.Multiply));
                if (first == InstanceEffectorOperation.Set)
                {
                    floats++;
                }
            }
            Assert.That(floats, Is.InRange(150, 250), "Equal weights give about half of the placements to each variant.");

            InstanceEffectorOperation[] before = _operations.ToArray();
            effector.Build(prefab, placements, _properties, _operations, _values);
            Assert.That(_operations, Is.EqualTo(before), "A rebuild gives each placement the same variant.");
        }

        [Test]
        public void RandomValuesStayInRange()
        {
            var random = new LoogaMaterialEffector.Value("_TestFloat", InstanceEffectorOperation.Set, Vector4.one,
                new Vector4(3f, 0f, 0f, 0f));
            LoogaMaterialEffector effector = CreateEffector(new LoogaMaterialEffector.Variant("A", 1f, new[] { random }));
            List<InstanceSet.Placement> placements = Placements(200);
            Assert.That(effector.Build(CreatePrefab(MarkedShader), placements, _properties, _operations, _values), Is.True);

            Assert.That(_values.Count, Is.EqualTo(placements.Count));
            Assert.That(_values.All(value => value.x >= 1f && value.x <= 3f), Is.True);
            Assert.That(_values.Select(value => value.x).Distinct().Count(), Is.GreaterThan(100));
        }

        // The component stays on an inactive object, so the stamp does not join a world.
        private LoogaMaterialEffector CreateEffector(params LoogaMaterialEffector.Variant[] variants)
        {
            var owner = new GameObject("Effector");
            owner.SetActive(false);
            SceneManager.MoveGameObjectToScene(owner, _scene);
            _created.Add(owner);
            var effector = owner.AddComponent<LoogaMaterialEffector>();
            effector.SetVariants(variants);
            return effector;
        }

        private GameObject CreatePrefab(string shaderName)
        {
            GameObject prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(prefab, _scene);
            Object.DestroyImmediate(prefab.GetComponent<Collider>());
            Shader shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null, shaderName);
            var material = new Material(shader);
            prefab.GetComponent<Renderer>().sharedMaterial = material;
            _created.Add(prefab);
            _created.Add(material);
            return prefab;
        }

        private static List<InstanceSet.Placement> Placements(int count)
        {
            var placements = new List<InstanceSet.Placement>(count);
            for (int index = 0; index < count; index++)
            {
                placements.Add(new InstanceSet.Placement
                {
                    Position = new Vector3(index * 1.37f, 0f, index * 0.91f),
                    Scale = Vector3.one
                });
            }
            return placements;
        }
    }
}
