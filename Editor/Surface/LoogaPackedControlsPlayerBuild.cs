using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Surface.Editor
{
    /// <summary>
    /// Ships packed control maps instead of the splat control maps in player builds. For each terrain whose Looga
    /// terrain surface binding packs its control maps (<see cref="LoogaTerrainSurfaceBinding.PackControls"/>), the build
    /// packs them into two RGBA8 textures (<see cref="LoogaPackedControls"/>) and gives the terrain a copy of its terrain
    /// data with 16 x 16 control maps. The terrain asset does not change.
    /// </summary>
    /// <remarks>
    /// A player does not update the stamp world, and the surface and the terrain ground read only the packed maps.
    /// Unity always loads the control maps of a terrain data, readable and with mips: 45 MB for each 1 km tile with 30
    /// layers at 1024 x 1024, and 2.45 GB of GPU memory in Kubera World. The packed maps use a quarter of that and only
    /// GPU memory. Components that reference the terrain data, such as the terrain collider, get the copy. The result
    /// depends on the scene and its terrain data assets, so an incremental build processes the scene again when the
    /// control maps change. Increase the callback version when the step changes.
    /// </remarks>
    [BuildCallbackVersion(1)]
    internal sealed class LoogaPackedControlsPlayerBuild : IProcessSceneWithReport
    {
        // The smallest control map size of a terrain data.
        private const int ShippedControlResolution = 16;
        // Types that can hold a terrain data reference, by type. Filled on first use.
        private static readonly Dictionary<Type, bool> ReferencesTerrainData = new();

        // After the other scene steps, which do not read the control maps.
        public int callbackOrder => 100;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // Play mode in the editor processes scenes without a report.
            if (report == null) return;

            var copies = new Dictionary<TerrainData, TerrainData>();
            long before = 0;
            long after = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (LoogaTerrainSurfaceBinding binding in root.GetComponentsInChildren<LoogaTerrainSurfaceBinding>(true))
                {
                    if (!binding.enabled || !binding.gameObject.activeInHierarchy || !binding.Surface) continue;
                    if (!binding.PackControls) continue;
                    if (!binding.TryGetComponent(out UnityEngine.Terrain terrain)) continue;

                    TerrainData data = terrain.terrainData;
                    if (!data || copies.ContainsKey(data)) continue;
                    if (data.alphamapTextureCount == 0 || data.alphamapTextureCount > LoogaPackedControls.MaxControls) continue;

                    Texture2D index;
                    Texture2D weight;
                    if (!TryPack(data, out index, out weight))
                    {
                        throw new BuildFailedException("[Looga Terrain] The control maps of " + data.name +
                            " could not be packed. The GPU needs compute shader support.");
                    }
                    foreach (Texture2D control in data.alphamapTextures)
                    {
                        before += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(control);
                    }
                    after += TextureBytes(index) + TextureBytes(weight);

                    var serialized = new SerializedObject(binding);
                    serialized.FindProperty("_shippedIndex").objectReferenceValue = index;
                    serialized.FindProperty("_shippedWeight").objectReferenceValue = weight;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    copies.Add(data, CreateShippedData(data));
                }
            }
            if (copies.Count == 0) return;

            int references = ReplaceReferences(scene, copies);
            Debug.Log("[Looga Terrain] Control maps of scene " + scene.name + ": " + copies.Count + " terrains ship " +
                "packed control maps (" + (after >> 20) + " MB) instead of their control maps (" + (before >> 20) +
                " MB). " + references + " terrain data references now use the copies with small control maps.");
        }

        // Packs the control maps on the GPU and reads the result into textures that the scene can store.
        private static bool TryPack(TerrainData data, out Texture2D index, out Texture2D weight)
        {
            index = null;
            weight = null;
            var packed = new LoogaPackedControls();
            try
            {
                packed.Pack(data.alphamapTextures);
                if (!packed.IsValid) return false;

                index = ReadBack(packed.Index, data.name + " Packed Control Index");
                weight = ReadBack(packed.Weight, data.name + " Packed Control Weight");
                return true;
            }
            finally
            {
                packed.Release();
            }
        }

        private static Texture2D ReadBack(Texture source, string name)
        {
            int mips = source.mipmapCount;
            var texture = new Texture2D(source.width, source.height, GraphicsFormat.R8G8B8A8_UNorm, mips,
                mips > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0
            };
            for (int mip = 0; mip < mips; mip++)
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(source, mip, TextureFormat.RGBA32);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    Object.DestroyImmediate(texture);
                    throw new BuildFailedException("[Looga Terrain] The GPU readback of " + name + " failed.");
                }
                texture.SetPixelData(request.GetData<byte>(), mip);
            }
            texture.Apply(false, false);
            // The player uploads the texture and then frees the CPU copy. The editor keeps the data for the build.
            var serialized = new SerializedObject(texture);
            serialized.FindProperty("m_IsReadable").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return texture;
        }

        private static long TextureBytes(Texture2D texture)
        {
            long bytes = 0;
            for (int mip = 0; mip < texture.mipmapCount; mip++)
            {
                bytes += (long)Mathf.Max(1, texture.width >> mip) * Mathf.Max(1, texture.height >> mip) * 4;
            }
            return bytes;
        }

        // A copy of the terrain data with the same layers and the smallest control maps. Heights, holes, details and
        // trees stay.
        private static TerrainData CreateShippedData(TerrainData data)
        {
            TerrainData copy = Object.Instantiate(data);
            copy.name = data.name;
            copy.alphamapResolution = ShippedControlResolution;
            return copy;
        }

        // Points every terrain data reference in the scene to the copy: terrains, terrain colliders and components
        // with terrain data fields. Returns the number of replaced references.
        private static int ReplaceReferences(Scene scene, Dictionary<TerrainData, TerrainData> copies)
        {
            int replaced = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (UnityEngine.Terrain terrain in root.GetComponentsInChildren<UnityEngine.Terrain>(true))
                {
                    if (terrain.terrainData && copies.TryGetValue(terrain.terrainData, out TerrainData copy))
                    {
                        terrain.terrainData = copy;
                        replaced++;
                    }
                }
                foreach (TerrainCollider collider in root.GetComponentsInChildren<TerrainCollider>(true))
                {
                    if (collider.terrainData && copies.TryGetValue(collider.terrainData, out TerrainData copy))
                    {
                        collider.terrainData = copy;
                        replaced++;
                    }
                }
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (!behaviour || !CanReference(behaviour.GetType())) continue;

                    replaced += ReplaceSerializedReferences(behaviour, copies);
                }
            }
            return replaced;
        }

        private static int ReplaceSerializedReferences(Object target, Dictionary<TerrainData, TerrainData> copies)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.GetIterator();
            int replaced = 0;
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (property.objectReferenceValue is not TerrainData data) continue;
                if (!copies.TryGetValue(data, out TerrainData copy)) continue;

                property.objectReferenceValue = copy;
                replaced++;
            }
            if (replaced > 0)
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return replaced;
        }

        // True when the serialized fields of the type, or of the serializable types in them, can hold a terrain data.
        // Most components cannot, so the step reads the serialized data of only a few.
        private static bool CanReference(Type type)
        {
            if (ReferencesTerrainData.TryGetValue(type, out bool result)) return result;

            result = HasTerrainDataField(type, 0);
            ReferencesTerrainData[type] = result;
            return result;
        }

        private static bool HasTerrainDataField(Type type, int depth)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly;
            for (Type current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(flags))
                {
                    if (field.IsNotSerialized) continue;

                    Type fieldType = ElementType(field.FieldType);
                    if (typeof(TerrainData).IsAssignableFrom(fieldType)) return true;
                    if (depth >= 3 || typeof(Object).IsAssignableFrom(fieldType) || fieldType.IsPrimitive) continue;
                    if (fieldType.IsEnum || fieldType == typeof(string)) continue;
                    if (!fieldType.IsSerializable && !fieldType.IsValueType) continue;
                    if (fieldType.Namespace != null && fieldType.Namespace.StartsWith("UnityEngine")) continue;
                    if (HasTerrainDataField(fieldType, depth + 1)) return true;
                }
            }
            return false;
        }

        private static Type ElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType && typeof(IList).IsAssignableFrom(type)) return type.GetGenericArguments()[0];

            return type;
        }
    }
}
