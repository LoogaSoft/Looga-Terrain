using LoogaSoft.Instancing;
using NUnit.Framework;
using System;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class VegetationSettingsTests
    {
        // Before, the provider compared only the detail prefabs, so a size edit kept the old instances until a domain
        // reload. DetailPrototype.Equals ignores alignToGround.
        [Test]
        public void DetailSettingEditsAreChanges()
        {
            var original = new DetailPrototype { minWidth = 0.5f, maxWidth = 1f, minHeight = 0.5f, maxHeight = 2f, density = 1f };
            Assert.IsTrue(TerrainVegetationProvider.SameDetailSettings(original, new DetailPrototype(original)));
            Action<DetailPrototype>[] edits =
            {
                detail => detail.maxHeight = 2.5f, detail => detail.minWidth = 0.4f, detail => detail.density = 1.25f,
                detail => detail.alignToGround = 0.25f, detail => detail.noiseSeed = 3,
                detail => detail.healthyColor = Color.red
            };
            foreach (Action<DetailPrototype> edit in edits)
            {
                var copy = new DetailPrototype(original);
                edit(copy);
                Assert.IsFalse(TerrainVegetationProvider.SameDetailSettings(original, copy));
            }
        }

        [Test]
        public void ResolvesDefaultsAndSelectedSourceFields()
        {
            VegetationSettings settings = ScriptableObject.CreateInstance<VegetationSettings>();
            GameObject first = new GameObject("First detail source");
            GameObject second = new GameObject("Second detail source");
            try
            {
                InstanceQualitySettings defaults = InstanceQualitySettings.Default;
                defaults.ViewDistance = 240;
                defaults.ShadowDistance = 80;
                settings.SetDefaults(VegetationKind.Detail, defaults);
                InstanceQualitySettings change = InstanceQualitySettings.Default;
                change.ViewDistance = 120;
                change.ShadowDistance = 20;
                settings.SetOverride(VegetationKind.Detail, first, VegetationQualityFields.ViewDistance, change);

                Assert.AreEqual(120, settings.Resolve(VegetationKind.Detail, first).ViewDistance);
                Assert.AreEqual(80, settings.Resolve(VegetationKind.Detail, first).ShadowDistance);
                Assert.AreEqual(240, settings.Resolve(VegetationKind.Detail, second).ViewDistance);
                Assert.AreEqual(0, settings.Resolve(VegetationKind.Tree, first).ViewDistance);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void SourceOverridesFollowReferencesAfterPrototypeOrderChanges()
        {
            VegetationSettings settings = ScriptableObject.CreateInstance<VegetationSettings>();
            GameObject oak = new GameObject("Oak");
            GameObject pine = new GameObject("Pine");
            try
            {
                InstanceQualitySettings change = InstanceQualitySettings.Default;
                change.ViewDistance = 410;
                settings.SetOverride(VegetationKind.Tree, oak, VegetationQualityFields.ViewDistance, change);
                GameObject[] originalOrder = { oak, pine };
                GameObject[] reordered = { pine, oak };

                Assert.AreEqual(410, settings.Resolve(VegetationKind.Tree, originalOrder[0]).ViewDistance);
                Assert.AreEqual(410, settings.Resolve(VegetationKind.Tree, reordered[1]).ViewDistance);
                Assert.AreEqual(0, settings.Resolve(VegetationKind.Tree, reordered[0]).ViewDistance);
                GameObject[] duplicateSlots = { oak, oak };
                Assert.AreEqual(410, settings.Resolve(VegetationKind.Tree, duplicateSlots[0]).ViewDistance);
                Assert.AreEqual(410, settings.Resolve(VegetationKind.Tree, duplicateSlots[1]).ViewDistance);
                settings.RemoveOverride(VegetationKind.Tree, oak);
                Assert.AreEqual(0, settings.Resolve(VegetationKind.Tree, duplicateSlots[0]).ViewDistance);
                Assert.AreEqual(0, settings.Resolve(VegetationKind.Tree, duplicateSlots[1]).ViewDistance);
            }
            finally
            {
                Object.DestroyImmediate(oak);
                Object.DestroyImmediate(pine);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ObjectPolicyResolvesInstanceQualityAndNotifiesSources()
        {
            VegetationSettings settings = ScriptableObject.CreateInstance<VegetationSettings>();
            GameObject rock = new GameObject("Rock");
            GameObject tree = new GameObject("Tree");
            int changes = 0;
            try
            {
                IInstanceQualitySource source = settings;
                source.Changed += () => changes++;
                InstanceQualitySettings defaults = InstanceQualitySettings.Default;
                defaults.ViewDistance = 500;
                settings.SetDefaults(VegetationKind.Object, defaults);
                InstanceQualitySettings change = InstanceQualitySettings.Default;
                change.LodBias = 2;
                settings.SetOverride(VegetationKind.Object, rock, VegetationQualityFields.LodBias, change);

                Assert.AreEqual(2, changes);
                Assert.AreEqual(2, source.ResolveInstanceQuality(rock).LodBias);
                Assert.AreEqual(500, source.ResolveInstanceQuality(rock).ViewDistance);
                Assert.AreEqual(0, source.ResolveInstanceQuality(tree).LodBias);
                Assert.AreEqual(0, settings.Resolve(VegetationKind.Tree, rock).LodBias,
                    "An Object override must not change the Tree category.");
            }
            finally
            {
                Object.DestroyImmediate(rock);
                Object.DestroyImmediate(tree);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void RejectsInvalidRangesAndSourceKinds()
        {
            VegetationSettings settings = ScriptableObject.CreateInstance<VegetationSettings>();
            Texture2D texture = new Texture2D(1, 1);
            try
            {
                InstanceQualitySettings invalid = InstanceQualitySettings.Default;
                invalid.ViewDistance = float.NaN;
                Assert.Throws<ArgumentOutOfRangeException>(() => settings.SetDefaults(VegetationKind.Detail, invalid));
                Assert.Throws<ArgumentException>(() => settings.SetOverride(VegetationKind.Tree,
                    texture, VegetationQualityFields.ViewDistance, InstanceQualitySettings.Default));
                Assert.Throws<ArgumentException>(() => settings.SetOverride(VegetationKind.Object,
                    texture, VegetationQualityFields.ViewDistance, InstanceQualitySettings.Default));
            }
            finally
            {
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void RemovingSharedPolicyRestoresLocalQualityWithoutRefresh()
        {
            GameObject gameObject = new GameObject("Vegetation provider");
            VegetationSettings settings = ScriptableObject.CreateInstance<VegetationSettings>();
            try
            {
                gameObject.AddComponent<UnityEngine.Terrain>();
                TerrainVegetationProvider provider = gameObject.AddComponent<TerrainVegetationProvider>();
                InstanceQualitySettings local = InstanceQualitySettings.Default;
                local.ViewDistance = 100;
                provider.ConfigureQuality(InstanceQualitySettings.Default, local, InstanceVisibilityMode.Direct);
                InstanceQualitySettings shared = InstanceQualitySettings.Default;
                shared.ViewDistance = 300;
                settings.SetDefaults(VegetationKind.Detail, shared);
                FieldInfo refresh = typeof(TerrainVegetationProvider).GetField("_fullRefresh",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(refresh);
                refresh.SetValue(provider, false);

                provider.SetVegetationSettings(settings);
                Assert.AreEqual(300, provider.GetEffectiveQuality(VegetationKind.Detail, null).ViewDistance);
                Assert.IsFalse((bool)refresh.GetValue(provider));
                provider.SetVegetationSettings(null);
                Assert.AreEqual(100, provider.GetEffectiveQuality(VegetationKind.Detail, null).ViewDistance);
                Assert.IsFalse((bool)refresh.GetValue(provider));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void StreamingObserverCannotCutOffTheEffectiveViewRange()
        {
            Type stream = typeof(TerrainVegetationProvider).Assembly.GetType(
                "LoogaSoft.Terrain.Instances.TerrainVegetationStream");
            MethodInfo radius = stream.GetMethod("RequiredObserverRadius",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(radius);
            Assert.AreEqual(500f, (float)radius.Invoke(null, new object[] { 270f, 500f }));
            Assert.AreEqual(500f, (float)radius.Invoke(null, new object[] { 500f, 250f }));
        }
    }
}
