using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using LoogaSoft.Terrain.ImageDecoding.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using PackingLayout = LoogaSoft.Terrain.Surface.LoogaTerrainSurface.PackingLayout;
using TextureChannel = LoogaSoft.Terrain.Surface.LoogaTerrainSurface.TextureChannel;

namespace LoogaSoft.Terrain.Surface.Editor
{
    /// <summary>
    /// Builds the texture arrays of a Looga terrain surface from the source maps of its layers. The builder decodes
    /// PNG, JPG and TGA files directly on worker threads (bundled StbImageSharp), so the sources are not compressed
    /// twice. Other files use the imported texture.
    /// Each slice is box-filtered to the array size, packed on the GPU, compressed and saved as an array asset.
    /// <para>
    /// Every compressed slice is kept in a cache in the Library folder under a hash of its inputs. A build packs only
    /// the slices that are not in the cache, so a change to one layer packs one layer. The surface records the input
    /// hash of each build, so the editor can find out-of-date arrays without a build.
    /// </para>
    /// </summary>
    public static class LoogaTerrainSurfaceArrayBuilder
    {
        /// <summary>Result of one build.</summary>
        public readonly struct Report
        {
            public Report(PackingLayout layout, long gpuBytes, int packedSlices, int cachedSlices, Timings timings,
                string details)
            {
                Layout = layout;
                GpuBytes = gpuBytes;
                PackedSlices = packedSlices;
                CachedSlices = cachedSlices;
                Times = timings;
                Details = details;
            }

            public PackingLayout Layout { get; }
            /// <summary>GPU memory of all arrays of the layout.</summary>
            public long GpuBytes { get; }
            /// <summary>Slices packed and compressed from the source maps.</summary>
            public int PackedSlices { get; }
            /// <summary>Slices copied from the slice cache.</summary>
            public int CachedSlices { get; }
            public Timings Times { get; }
            public string Details { get; }

            public override string ToString()
            {
                return Layout + " arrays: " + (GpuBytes / (1024.0 * 1024.0)).ToString("F1") + " MB GPU memory, "
                    + PackedSlices + " slices packed, " + CachedSlices + " from the cache. " + Times + " " + Details;
            }
        }

        /// <summary>Time of each build phase in seconds.</summary>
        public struct Timings
        {
            public double Decode;
            public double Pack;
            public double Compress;
            public double Cache;
            public double Save;
            public double Total;

            public override string ToString()
            {
                return "Total " + Total.ToString("F2") + " s (decode " + Decode.ToString("F2") + ", pack " + Pack.ToString("F2")
                    + ", compress " + Compress.ToString("F2") + ", cache " + Cache.ToString("F2") + ", save "
                    + Save.ToString("F2") + ").";
            }
        }

        /// <summary>State of the arrays of the requested layout.</summary>
        public enum ArrayState
        {
            /// <summary>The surface uses the arrays of the last build, and the inputs did not change.</summary>
            UpToDate,
            /// <summary>Up-to-date arrays exist, but the surface uses other arrays.</summary>
            Unassigned,
            /// <summary>An input changed after the last build.</summary>
            OutOfDate,
            /// <summary>The arrays of the layout do not exist.</summary>
            Missing
        }

        /// <summary>Array state of a surface and the work that a build needs.</summary>
        public readonly struct Status
        {
            public Status(ArrayState state, PackingLayout layout, int layersToPack, int layerCount, long gpuBytes)
            {
                State = state;
                Layout = layout;
                LayersToPack = layersToPack;
                LayerCount = layerCount;
                GpuBytes = gpuBytes;
            }

            public ArrayState State { get; }
            public PackingLayout Layout { get; }
            /// <summary>Layers that are not in the slice cache. A build packs only these layers.</summary>
            public int LayersToPack { get; }
            public int LayerCount { get; }
            /// <summary>GPU memory of the arrays of the layout.</summary>
            public long GpuBytes { get; }
        }

        private const string PackShaderName = "Hidden/Looga/Terrain/Surface Pack";
        private const string CacheFolder = "Library/LoogaTerrainSurface/Slices";
        // Size limit of the slice cache. A build removes the least recently used slices above it.
        private const long CacheLimitBytes = 2L * 1024 * 1024 * 1024;
        // Change this value when the build gives different results for the same inputs (not for pack shader edits:
        // the hash includes the pack shader file).
        // 2: source files are decoded with StbImageSharp instead of ImageConversion.LoadImage (JPG output differs).
        private const int BuilderVersion = 2;

        // Passes of the pack shader.
        private enum PackPass
        {
            AlbedoHeight = 0,
            NormalCompact = 1,
            NormalQuality = 2,
            MaskQuality = 3,
            ArraySlice = 4
        }

        // One array of a layout: name suffix, format, color space and pack pass.
        private readonly struct ArraySpec
        {
            public ArraySpec(string suffix, TextureFormat format, bool linear, PackPass pass)
            {
                Suffix = suffix;
                Format = format;
                Linear = linear;
                Pass = pass;
            }

            public string Suffix { get; }
            public TextureFormat Format { get; }
            public bool Linear { get; }
            public PackPass Pass { get; }
        }

        private static readonly int AlbedoId = Shader.PropertyToID("_Albedo");
        private static readonly int NormalId = Shader.PropertyToID("_Normal");
        private static readonly int HeightId = Shader.PropertyToID("_Height");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int OcclusionId = Shader.PropertyToID("_Occlusion");
        private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        private static readonly int SourceArrayId = Shader.PropertyToID("_SourceArray");
        private static readonly int LodsId = Shader.PropertyToID("_Lods");
        private static readonly int Lods2Id = Shader.PropertyToID("_Lods2");
        private static readonly int PresentId = Shader.PropertyToID("_Present");
        private static readonly int HeightMaskId = Shader.PropertyToID("_HeightMask");
        private static readonly int SmoothnessMaskId = Shader.PropertyToID("_SmoothnessMask");
        private static readonly int OcclusionMaskId = Shader.PropertyToID("_OcclusionMask");
        private static readonly int MetallicMaskId = Shader.PropertyToID("_MetallicMask");
        private static readonly int FlagsId = Shader.PropertyToID("_Flags");
        private static readonly int OptionsId = Shader.PropertyToID("_Options");
        private static readonly int SliceId = Shader.PropertyToID("_Slice");

        #region Build
        /// <summary>
        /// Builds the arrays of one packing layout, saves them and assigns them to the surface. The layout becomes the
        /// requested layout of the surface. A build replaces the arrays of an earlier build in place, so their GUIDs
        /// stay the same. <see cref="LoogaTerrainSurfaceAutoBuild"/> runs the same build over several editor updates.
        /// </summary>
        /// <param name="surface">Surface with its layers and source maps.</param>
        /// <param name="layout">Packing layout to build.</param>
        /// <param name="folder">Project folder of the array assets. Null uses "[surface name] Arrays" beside the surface.</param>
        public static Report Build(LoogaTerrainSurface surface, PackingLayout layout, string folder = null)
        {
            using var run = new BuildRun(surface, layout, folder);
            try
            {
                while (!run.IsDone)
                {
                    EditorUtility.DisplayProgressBar("Looga Terrain Surface", run.Description, run.Progress);
                    run.Step();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return run.Finish();
        }

        /// <summary>Assigns the arrays of an earlier build of a layout and makes it the requested layout.</summary>
        /// <returns>False when an array asset of the layout is missing.</returns>
        public static bool Assign(LoogaTerrainSurface surface, PackingLayout layout, string folder = null)
        {
            folder ??= DefaultFolder(surface);
            Texture2DArray[] arrays = LoadArrays(surface, layout, ContainerPath(folder, surface, layout));
            if (arrays == null) return false;
            surface.SetPacking(layout);
            surface.SetTextureArrays(arrays[0], arrays[1], arrays.Length > 2 ? arrays[2] : null);
            MeasureAlbedoMeans(surface);
            EditorUtility.SetDirty(surface);
            AssetDatabase.SaveAssetIfDirty(surface);
            return true;
        }

        /// <summary>
        /// Finds the state of the arrays of the requested layout. It reads file sizes, dates and hashes of changed files,
        /// but it does not load or decode textures.
        /// </summary>
        public static Status GetStatus(LoogaTerrainSurface surface)
        {
            var plan = new Plan(surface, surface.Packing, null);
            int layersToPack = 0;
            foreach (LayerPlan layer in plan.Layers)
            {
                if (Array.Exists(layer.SliceKeys, key => !File.Exists(CachePath(key))))
                {
                    layersToPack++;
                }
            }
            ArrayState state;
            if (!AssetExists(plan.ContainerPath))
            {
                state = ArrayState.Missing;
            }
            else if (surface.GetArrayBuildHash(plan.Layout) != plan.InputHash)
            {
                state = ArrayState.OutOfDate;
            }
            else if (!IsAssigned(surface, plan))
            {
                state = ArrayState.Unassigned;
            }
            else
            {
                state = ArrayState.UpToDate;
            }
            return new Status(state, plan.Layout, layersToPack, plan.Layers.Length, plan.GpuBytes());
        }

        /// <summary>
        /// Measures the mean linear albedo of each albedo array slice from a small mip level and stores it in the
        /// surface. The ground cover reads the soil luminance relative to these means. Does not save the asset.
        /// </summary>
        public static void MeasureAlbedoMeans(LoogaTerrainSurface surface)
        {
            Texture2DArray array = surface.AlbedoArray;
            if (!array) return;

            // A mip level of about 16 x 16 texels averages the whole texture.
            int mip = Mathf.Max(0, array.mipmapCount - 5);
            bool srgb = GraphicsFormatUtility.IsSRGBFormat(array.graphicsFormat);
            var means = new Color[array.depth];
            for (int slice = 0; slice < array.depth; slice++)
            {
                Color32[] pixels = ReadSlice(array, slice, mip);
                Vector3 sum = Vector3.zero;
                foreach (Color32 pixel in pixels)
                {
                    var value = new Vector3(pixel.r, pixel.g, pixel.b) / 255f;
                    sum += srgb
                        ? new Vector3(Mathf.GammaToLinearSpace(value.x), Mathf.GammaToLinearSpace(value.y),
                            Mathf.GammaToLinearSpace(value.z))
                        : value;
                }
                sum /= Mathf.Max(1, pixels.Length);
                means[slice] = new Color(sum.x, sum.y, sum.z, 1f);
            }
            surface.SetAlbedoMeans(means);
        }

        /// <summary>
        /// Decodes one slice of an array into linear RGBA32 pixels (sRGB arrays stay sRGB-encoded) for checks.
        /// </summary>
        public static Color32[] ReadSlice(Texture2DArray array, int slice, int mip = 0)
        {
            int size = Mathf.Max(1, array.width >> mip);
            bool srgb = GraphicsFormatUtility.IsSRGBFormat(array.graphicsFormat);
            var target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var material = new Material(Shader.Find(PackShaderName)) { hideFlags = HideFlags.HideAndDontSave };
            var commands = new CommandBuffer();
            Texture2D texture = null;
            try
            {
                material.SetTexture(SourceArrayId, array);
                material.SetVector(SliceId, new Vector4(slice, mip, 0, 0));
                commands.SetRenderTarget(target);
                commands.DrawProcedural(Matrix4x4.identity, material, (int)PackPass.ArraySlice, MeshTopology.Triangles, 3);
                Graphics.ExecuteCommandBuffer(commands);
                texture = ReadTarget(target, srgb);
                return texture.GetPixels32();
            }
            finally
            {
                commands.Release();
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(texture);
                RenderTexture.ReleaseTemporary(target);
            }
        }

        /// <summary>GPU memory of an array: all slices and mips. A freshly built array also has a CPU copy until reload.</summary>
        public static long ArrayBytes(Texture2DArray array)
        {
            return SliceBytes(array.width, array.mipmapCount, array.graphicsFormat) * array.depth;
        }

        /// <summary>Deletes the slice cache. The next build of every surface packs all slices again.</summary>
        public static void ClearCache()
        {
            if (Directory.Exists(CacheFolder))
            {
                Directory.Delete(CacheFolder, true);
            }
        }

        private static ArraySpec[] Specs(PackingLayout layout)
        {
            if (layout == PackingLayout.Quality)
            {
                return new[]
                {
                    new ArraySpec("Albedo", TextureFormat.BC7, false, PackPass.AlbedoHeight),
                    new ArraySpec("Normal", TextureFormat.BC5, true, PackPass.NormalQuality),
                    new ArraySpec("Mask", TextureFormat.BC7, true, PackPass.MaskQuality)
                };
            }
            return new[]
            {
                new ArraySpec("Albedo", TextureFormat.DXT5, false, PackPass.AlbedoHeight),
                new ArraySpec("Normal", TextureFormat.DXT5, true, PackPass.NormalCompact)
            };
        }

        private static long SliceBytes(int resolution, int mipCount, GraphicsFormat format)
        {
            long bytes = 0;
            for (int mip = 0; mip < mipCount; mip++)
            {
                int size = Mathf.Max(1, resolution >> mip);
                bytes += GraphicsFormatUtility.ComputeMipmapSize(size, size, format);
            }
            return bytes;
        }

        private static int MipCount(int resolution)
        {
            return Mathf.FloorToInt(Mathf.Log(resolution, 2f)) + 1;
        }

        private static string DefaultFolder(LoogaTerrainSurface surface)
        {
            string path = AssetDatabase.GetAssetPath(surface);
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Save the surface asset before a build.");
            return (Path.GetDirectoryName(path) + "/" + surface.name + " Arrays").Replace('\\', '/');
        }

        // One binary asset per layout holds the arrays of the layout as sub-assets.
        private static string ContainerPath(string folder, LoogaTerrainSurface surface, PackingLayout layout)
        {
            return (folder + "/" + surface.name + " " + layout + ".asset").Replace('\\', '/');
        }

        private static string ArrayName(LoogaTerrainSurface surface, PackingLayout layout, ArraySpec spec)
        {
            return surface.name + " " + layout + " " + spec.Suffix;
        }

        // The arrays of a layout in spec order, or null when one is missing.
        private static Texture2DArray[] LoadArrays(LoogaTerrainSurface surface, PackingLayout layout, string containerPath)
        {
            ArraySpec[] specs = Specs(layout);
            var arrays = new Texture2DArray[specs.Length];
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(containerPath))
            {
                if (asset is not Texture2DArray array) continue;
                int index = Array.FindIndex(specs, spec => ArrayName(surface, layout, spec) == array.name);
                if (index >= 0) arrays[index] = array;
            }
            return Array.TrueForAll(arrays, array => array) ? arrays : null;
        }

        private static bool AssetExists(string path)
        {
            return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path, AssetPathToGUIDOptions.OnlyExistingAssets));
        }

        private static bool IsAssigned(LoogaTerrainSurface surface, Plan plan)
        {
            Texture2DArray[] assigned = { surface.AlbedoArray, surface.NormalArray, surface.MaskArray };
            for (int index = 0; index < assigned.Length; index++)
            {
                if (index >= plan.ArrayNames.Length)
                {
                    if (assigned[index]) return false;
                    continue;
                }
                if (!assigned[index] || assigned[index].name != plan.ArrayNames[index] ||
                    AssetDatabase.GetAssetPath(assigned[index]) != plan.ContainerPath)
                {
                    return false;
                }
            }
            return true;
        }

        // Packs one slice, builds its mips on the CPU and compresses it.
        private static Texture2D PackSlice(Material material, CommandBuffer commands, ArraySpec spec, int resolution,
            TextureCompressionQuality quality, ref Timings timings)
        {
            var stopwatch = Stopwatch.StartNew();
            var target = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGB32,
                spec.Linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            Texture2D packed;
            try
            {
                commands.Clear();
                commands.SetRenderTarget(target);
                commands.DrawProcedural(Matrix4x4.identity, material, (int)spec.Pass, MeshTopology.Triangles, 3);
                Graphics.ExecuteCommandBuffer(commands);
                packed = ReadTarget(target, !spec.Linear);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(target);
            }
            timings.Pack += stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();
            EditorUtility.CompressTexture(packed, spec.Format, quality);
            timings.Compress += stopwatch.Elapsed.TotalSeconds;
            return packed;
        }

        // Reads a render texture into a texture with a full mip chain. The mips are box-filtered on the CPU.
        private static Texture2D ReadTarget(RenderTexture target, bool srgb)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, true, !srgb);
            texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            RenderTexture.active = previous;
            texture.Apply(true, false);
            return texture;
        }

        // Saves the arrays as sub-assets of the binary container. An array keeps its CPU data until it is saved. The
        // saved arrays are not readable, so a player does not keep a CPU copy of the pixels.
        private static Texture2DArray[] SaveArrays(Texture2DArray[] arrays, string path)
        {
            var container = AssetDatabase.LoadAssetAtPath<LoogaTerrainSurfaceArrays>(path);
            if (!container)
            {
                container = ScriptableObject.CreateInstance<LoogaTerrainSurfaceArrays>();
                container.name = Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(container, path);
            }
            var existing = new Dictionary<string, Texture2DArray>();
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is Texture2DArray array) existing[array.name] = array;
            }
            var saved = new Texture2DArray[arrays.Length];
            for (int index = 0; index < arrays.Length; index++)
            {
                if (existing.TryGetValue(arrays[index].name, out Texture2DArray target))
                {
                    // Copy into the existing sub-asset, so materials and surfaces keep their references.
                    EditorUtility.CopySerialized(arrays[index], target);
                    Object.DestroyImmediate(arrays[index]);
                }
                else
                {
                    AssetDatabase.AddObjectToAsset(arrays[index], container);
                    target = arrays[index];
                }
                var serialized = new SerializedObject(target);
                serialized.FindProperty("m_IsReadable").boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(target);
                saved[index] = target;
            }
            EditorUtility.SetDirty(container);
            AssetDatabase.SaveAssetIfDirty(container);
            return saved;
        }
        #endregion

        #region Plan
        // Inputs of one build: the resolved sources of each layer and the hashes of each slice and of the whole build.
        private sealed class Plan
        {
            public Plan(LoogaTerrainSurface surface, PackingLayout layout, string folder)
            {
                Layout = layout;
                Folder = folder ?? DefaultFolder(surface);
                Resolution = surface.ArrayResolution;
                Compression = surface.Compression switch
                {
                    LoogaTerrainSurface.CompressionQuality.Fast => TextureCompressionQuality.Fast,
                    LoogaTerrainSurface.CompressionQuality.Normal => TextureCompressionQuality.Normal,
                    _ => TextureCompressionQuality.Best
                };
                Specs = LoogaTerrainSurfaceArrayBuilder.Specs(layout);
                ContainerPath = LoogaTerrainSurfaceArrayBuilder.ContainerPath(Folder, surface, layout);
                ArrayNames = Array.ConvertAll(Specs, spec => ArrayName(surface, layout, spec));
                Hash128 shaderHash = PackShaderHash();
                Layers = new LayerPlan[surface.Layers.Count];
                var inputHash = new Hash128();
                inputHash.Append((int)layout);
                inputHash.Append(Resolution);
                inputHash.Append(Layers.Length);
                for (int layer = 0; layer < Layers.Length; layer++)
                {
                    var sources = new ResolvedSources(surface, surface.Layers[layer]);
                    Hash128 layerHash = sources.ComputeHash();
                    var keys = new Hash128[Specs.Length];
                    for (int index = 0; index < Specs.Length; index++)
                    {
                        keys[index] = SliceKey(layerHash, Specs[index], Resolution, Compression, shaderHash);
                        inputHash.Append(keys[index].ToString());
                    }
                    Layers[layer] = new LayerPlan(sources, keys);
                }
                InputHash = inputHash.ToString();
                SourceFileHashes.Save();
            }

            public PackingLayout Layout { get; }
            public string Folder { get; }
            public int Resolution { get; }
            public TextureCompressionQuality Compression { get; }
            public ArraySpec[] Specs { get; }
            public string ContainerPath { get; }
            public string[] ArrayNames { get; }
            public LayerPlan[] Layers { get; }
            public string InputHash { get; }

            public long GpuBytes()
            {
                long bytes = 0;
                foreach (ArraySpec spec in Specs)
                {
                    GraphicsFormat format = GraphicsFormatUtility.GetGraphicsFormat(spec.Format, !spec.Linear);
                    bytes += SliceBytes(Resolution, MipCount(Resolution), format) * Mathf.Max(1, Layers.Length);
                }
                return bytes;
            }

            private static Hash128 SliceKey(Hash128 layerHash, ArraySpec spec, int resolution,
                TextureCompressionQuality compression, Hash128 shaderHash)
            {
                var key = new Hash128();
                key.Append(BuilderVersion);
                key.Append(layerHash.ToString());
                key.Append(shaderHash.ToString());
                key.Append((int)spec.Format);
                key.Append(spec.Linear ? 1 : 0);
                key.Append((int)spec.Pass);
                key.Append(resolution);
                // Only the BC7 encoder uses the quality. The other slices stay in the cache when it changes.
                if (spec.Format == TextureFormat.BC7)
                {
                    key.Append((int)compression);
                }
                return key;
            }

            private static Hash128 PackShaderHash()
            {
                var shader = Shader.Find(PackShaderName);
                string path = shader ? AssetDatabase.GetAssetPath(shader) : null;
                return string.IsNullOrEmpty(path) ? default : SourceFileHashes.Get(FileUtil.GetPhysicalPath(path));
            }
        }

        private sealed class LayerPlan
        {
            public LayerPlan(ResolvedSources sources, Hash128[] sliceKeys)
            {
                Sources = sources;
                SliceKeys = sliceKeys;
            }

            public ResolvedSources Sources { get; }
            public Hash128[] SliceKeys { get; }
        }

        // The source textures of one layer after the terrain layer fallback, and the channel of each data map.
        private sealed class ResolvedSources
        {
            public ResolvedSources(LoogaTerrainSurface surface, LoogaTerrainSurface.Layer layer)
            {
                Generate = surface.GenerateMissingFromNormal;
                LoogaTerrainSurface.SourceMaps maps = layer.Sources;
                TerrainLayer terrainLayer = layer.UseTerrainLayerMaps ? layer.TerrainLayer : null;
                Texture2D mask = terrainLayer ? terrainLayer.maskMapTexture : null;
                Albedo = maps.Albedo ? maps.Albedo : terrainLayer ? terrainLayer.diffuseTexture : null;
                Normal = maps.Normal ? maps.Normal : terrainLayer ? terrainLayer.normalMapTexture : null;
                Height = Channel(maps.Height, maps.HeightChannel, mask, TextureChannel.B);
                Smoothness = Channel(maps.Smoothness, maps.SmoothnessChannel, mask, TextureChannel.A);
                Occlusion = Channel(maps.Occlusion, maps.OcclusionChannel, mask, TextureChannel.G);
                Metallic = Channel(maps.Metallic, maps.MetallicChannel, mask, TextureChannel.R);
                Roughness = maps.Smoothness && maps.SmoothnessIsRoughness;
            }

            public Texture2D Albedo { get; }
            public Texture2D Normal { get; }
            public (Texture2D Texture, TextureChannel Channel) Height { get; }
            public (Texture2D Texture, TextureChannel Channel) Smoothness { get; }
            public (Texture2D Texture, TextureChannel Channel) Occlusion { get; }
            public (Texture2D Texture, TextureChannel Channel) Metallic { get; }
            public bool Roughness { get; }
            public bool Generate { get; }

            // The source textures in the order that LayerSources loads them.
            public IEnumerable<Texture2D> Textures()
            {
                Texture2D[] textures = { Albedo, Normal, Height.Texture, Smoothness.Texture, Occlusion.Texture, Metallic.Texture };
                foreach (Texture2D texture in textures)
                {
                    if (texture) yield return texture;
                }
            }

            // The hash uses the file contents and import settings, not dates, so it is the same on every computer.
            public Hash128 ComputeHash()
            {
                var hash = new Hash128();
                hash.Append(Generate ? 1 : 0);
                hash.Append(Roughness ? 1 : 0);
                AppendTexture(ref hash, Albedo);
                AppendTexture(ref hash, Normal);
                AppendChannel(ref hash, Height);
                AppendChannel(ref hash, Smoothness);
                AppendChannel(ref hash, Occlusion);
                AppendChannel(ref hash, Metallic);
                return hash;
            }

            private static (Texture2D, TextureChannel) Channel(Texture2D source, TextureChannel channel, Texture2D mask,
                TextureChannel maskChannel)
            {
                if (source) return (source, channel);
                return mask ? (mask, maskChannel) : (null, channel);
            }

            private static void AppendChannel(ref Hash128 hash, (Texture2D Texture, TextureChannel Channel) source)
            {
                AppendTexture(ref hash, source.Texture);
                hash.Append(source.Texture ? (int)source.Channel : -1);
            }

            private static void AppendTexture(ref Hash128 hash, Texture2D texture)
            {
                string path = texture ? AssetDatabase.GetAssetPath(texture) : null;
                if (string.IsNullOrEmpty(path))
                {
                    // A texture without an asset file has no content hash. The instance ID is stable until the editor
                    // reloads, so the arrays are built again once after each reload.
                    hash.Append(texture ? "unsaved " + texture.GetInstanceID() : "none");
                    return;
                }
                string file = FileUtil.GetPhysicalPath(path);
                hash.Append(SourceFileHashes.Get(file).ToString());
                hash.Append(SourceFileHashes.Get(file + ".meta").ToString());
            }
        }
        #endregion

        #region Run
        /// <summary>
        /// One build that runs one layer per <see cref="Step"/>. Layers with all slices in the cache need no source
        /// decode. Dispose releases the unsaved arrays when the run stops before <see cref="Finish"/>.
        /// </summary>
        internal sealed class BuildRun : IDisposable
        {
            private readonly LoogaTerrainSurface _surface;
            private readonly Plan _plan;
            private readonly Texture2DArray[] _arrays;
            private readonly long[] _sliceBytes;
            private readonly System.Text.StringBuilder _details = new();
            private readonly SourcePrefetch _prefetch;
            private Material _material;
            private CommandBuffer _commands;
            private Timings _timings;
            private int _layer;
            private int _packedSlices;
            private int _cachedSlices;
            private bool _finished;

            public BuildRun(LoogaTerrainSurface surface, PackingLayout layout, string folder = null)
            {
                _surface = surface;
                _plan = new Plan(surface, layout, folder);
                int resolution = _plan.Resolution;
                int depth = Mathf.Max(1, _plan.Layers.Length);
                _arrays = new Texture2DArray[_plan.Specs.Length];
                _sliceBytes = new long[_plan.Specs.Length];
                for (int index = 0; index < _arrays.Length; index++)
                {
                    ArraySpec spec = _plan.Specs[index];
                    _arrays[index] = new Texture2DArray(resolution, resolution, depth, spec.Format, true, spec.Linear)
                    {
                        name = _plan.ArrayNames[index],
                        hideFlags = HideFlags.HideAndDontSave,
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Trilinear,
                        anisoLevel = surface.ArrayAnisoLevel
                    };
                    _sliceBytes[index] = SliceBytes(resolution, _arrays[index].mipmapCount, _arrays[index].graphicsFormat);
                }
                _prefetch = new SourcePrefetch(PendingRequests());
            }

            public LoogaTerrainSurface Surface => _surface;
            public PackingLayout Layout => _plan.Layout;
            public string InputHash => _plan.InputHash;
            public bool IsDone => _layer >= _plan.Layers.Length;
            public float Progress => _plan.Layers.Length == 0 ? 1f : (float)_layer / _plan.Layers.Length;
            public string Description => IsDone ? "Saving the arrays" : "Layer " + (_layer + 1) + " of " + _plan.Layers.Length;

            /// <summary>Writes all slices of the next layer, from the cache or from the source maps.</summary>
            public void Step()
            {
                if (IsDone) return;
                LayerPlan layer = _plan.Layers[_layer];
                var missing = new List<int>();
                var stopwatch = Stopwatch.StartNew();
                for (int index = 0; index < _plan.Specs.Length; index++)
                {
                    if (!ReadCache(layer.SliceKeys[index], index, _layer))
                    {
                        missing.Add(index);
                    }
                }
                _timings.Cache += stopwatch.Elapsed.TotalSeconds;
                _cachedSlices += _plan.Specs.Length - missing.Count;
                if (missing.Count > 0)
                {
                    PackLayer(layer, missing);
                }
                _layer++;
            }

            /// <summary>Saves the arrays, assigns them to the surface and records the input hash of the build.</summary>
            public Report Finish()
            {
                while (!IsDone)
                {
                    Step();
                }
                var stopwatch = Stopwatch.StartNew();
                Directory.CreateDirectory(_plan.Folder);
                foreach (Texture2DArray array in _arrays)
                {
                    array.hideFlags = HideFlags.None;
                    array.Apply(false, false);
                }
                Texture2DArray[] saved = SaveArrays(_arrays, _plan.ContainerPath);
                long gpuBytes = 0;
                for (int index = 0; index < saved.Length; index++)
                {
                    _arrays[index] = saved[index];
                    gpuBytes += ArrayBytes(saved[index]);
                }
                _finished = true;
                _surface.SetPacking(_plan.Layout);
                _surface.SetArrayBuildHash(_plan.Layout, _plan.InputHash);
                _surface.SetTextureArrays(_arrays[0], _arrays[1], _arrays.Length > 2 ? _arrays[2] : null);
                MeasureAlbedoMeans(_surface);
                EditorUtility.SetDirty(_surface);
                AssetDatabase.SaveAssetIfDirty(_surface);
                _timings.Save += stopwatch.Elapsed.TotalSeconds;
                stopwatch.Restart();
                PruneCache();
                _timings.Cache += stopwatch.Elapsed.TotalSeconds;
                _timings.Total = _timings.Decode + _timings.Pack + _timings.Compress + _timings.Cache + _timings.Save;
                return new Report(_plan.Layout, gpuBytes, _packedSlices, _cachedSlices, _timings, _details.ToString());
            }

            public void Dispose()
            {
                if (!_finished)
                {
                    foreach (Texture2DArray array in _arrays)
                    {
                        Object.DestroyImmediate(array);
                    }
                }
                _prefetch.Dispose();
                _commands?.Release();
                _commands = null;
                if (_material)
                {
                    Object.DestroyImmediate(_material);
                }
            }

            private void PackLayer(LayerPlan layer, List<int> specIndices)
            {
                if (!_material)
                {
                    var shader = Shader.Find(PackShaderName);
                    if (!shader) throw new InvalidOperationException("The shader '" + PackShaderName + "' is missing.");
                    _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    _commands = new CommandBuffer { name = "Looga terrain surface pack" };
                }
                var stopwatch = Stopwatch.StartNew();
                using var sources = new LayerSources(layer.Sources, _plan.Resolution, _prefetch);
                _timings.Decode += stopwatch.Elapsed.TotalSeconds;
                sources.Bind(_material);
                _details.Append(sources.Describe(_layer));
                foreach (int index in specIndices)
                {
                    Texture2D packed = PackSlice(_material, _commands, _plan.Specs[index], _plan.Resolution,
                        _plan.Compression, ref _timings);
                    try
                    {
                        stopwatch.Restart();
                        var data = new byte[_sliceBytes[index]];
                        int offset = 0;
                        for (int mip = 0; mip < packed.mipmapCount; mip++)
                        {
                            var mipData = packed.GetPixelData<byte>(mip);
                            Unity.Collections.NativeArray<byte>.Copy(mipData, 0, data, offset, mipData.Length);
                            offset += mipData.Length;
                        }
                        WriteSlice(data, index, _layer);
                        WriteCache(layer.SliceKeys[index], data);
                        _timings.Cache += stopwatch.Elapsed.TotalSeconds;
                    }
                    finally
                    {
                        Object.DestroyImmediate(packed);
                    }
                    _packedSlices++;
                }
            }

            // Decode requests of the layers that are not in the slice cache, in the order that PackLayer uses them.
            private List<DecodeRequest> PendingRequests()
            {
                var requests = new List<DecodeRequest>();
                foreach (LayerPlan layer in _plan.Layers)
                {
                    if (Array.TrueForAll(layer.SliceKeys, key => File.Exists(CachePath(key)))) continue;
                    foreach (Texture2D texture in layer.Sources.Textures())
                    {
                        DecodeRequest request = DecodeRequest.Create(texture);
                        if (request != null) requests.Add(request);
                    }
                }
                return requests;
            }

            private bool ReadCache(Hash128 key, int specIndex, int slice)
            {
                string path = CachePath(key);
                if (!File.Exists(path)) return false;
                byte[] data = File.ReadAllBytes(path);
                if (data.Length != _sliceBytes[specIndex]) return false;
                WriteSlice(data, specIndex, slice);
                // The write date orders the cache files for the size limit.
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return true;
            }

            private void WriteSlice(byte[] data, int specIndex, int slice)
            {
                Texture2DArray array = _arrays[specIndex];
                int offset = 0;
                for (int mip = 0; mip < array.mipmapCount; mip++)
                {
                    int size = Mathf.Max(1, array.width >> mip);
                    int bytes = (int)GraphicsFormatUtility.ComputeMipmapSize(size, size, array.graphicsFormat);
                    array.SetPixelData(data, mip, slice, offset);
                    offset += bytes;
                }
            }

            private static void WriteCache(Hash128 key, byte[] data)
            {
                Directory.CreateDirectory(CacheFolder);
                string path = CachePath(key);
                // Write to a temporary file first, so a stopped write cannot leave a short cache file.
                string temporary = path + ".tmp";
                File.WriteAllBytes(temporary, data);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temporary, path);
            }

            private static void PruneCache()
            {
                if (!Directory.Exists(CacheFolder)) return;
                var files = new List<FileInfo>(new DirectoryInfo(CacheFolder).GetFiles("*.slice"));
                long total = 0;
                foreach (FileInfo file in files)
                {
                    total += file.Length;
                }
                if (total <= CacheLimitBytes) return;
                files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                foreach (FileInfo file in files)
                {
                    if (total <= CacheLimitBytes) break;
                    total -= file.Length;
                    file.Delete();
                }
            }
        }

        private static string CachePath(Hash128 key)
        {
            return CacheFolder + "/" + key + ".slice";
        }
        #endregion

        #region Sources
        // Source textures of one layer, loaded at full quality and bound to the pack material.
        private sealed class LayerSources : IDisposable
        {
            private readonly Dictionary<Texture2D, Loaded> _loaded = new();
            private readonly SourcePrefetch _prefetch;
            private readonly int _resolution;
            private readonly Loaded _albedo;
            private readonly Loaded _normal;
            private readonly (Loaded Texture, TextureChannel Channel) _height;
            private readonly (Loaded Texture, TextureChannel Channel) _smoothness;
            private readonly (Loaded Texture, TextureChannel Channel) _occlusion;
            private readonly (Loaded Texture, TextureChannel Channel) _metallic;
            private readonly bool _roughness;
            private readonly bool _generate;

            private sealed class Loaded
            {
                public Texture2D Texture;
                public bool Created;
                public bool PackedNormal;
                public bool FlipGreen;
                public string Origin;
            }

            public LayerSources(ResolvedSources sources, int resolution, SourcePrefetch prefetch)
            {
                _prefetch = prefetch;
                _resolution = resolution;
                _generate = sources.Generate;
                _roughness = sources.Roughness;
                _albedo = Load(sources.Albedo);
                _normal = Load(sources.Normal);
                _height = (Load(sources.Height.Texture), sources.Height.Channel);
                _smoothness = (Load(sources.Smoothness.Texture), sources.Smoothness.Channel);
                _occlusion = (Load(sources.Occlusion.Texture), sources.Occlusion.Channel);
                _metallic = (Load(sources.Metallic.Texture), sources.Metallic.Channel);
            }

            public void Bind(Material material)
            {
                material.SetTexture(AlbedoId, Texture(_albedo, Texture2D.whiteTexture));
                material.SetTexture(NormalId, Texture(_normal, Texture2D.normalTexture));
                material.SetTexture(HeightId, Texture(_height.Texture, Texture2D.grayTexture));
                material.SetTexture(SmoothnessId, Texture(_smoothness.Texture, Texture2D.blackTexture));
                material.SetTexture(OcclusionId, Texture(_occlusion.Texture, Texture2D.whiteTexture));
                material.SetTexture(MetallicId, Texture(_metallic.Texture, Texture2D.blackTexture));
                material.SetVector(LodsId, new Vector4(Lod(_albedo), Lod(_normal), Lod(_height.Texture), Lod(_smoothness.Texture)));
                material.SetVector(Lods2Id, new Vector4(Lod(_occlusion.Texture), Lod(_metallic.Texture), 0, 0));
                material.SetVector(PresentId, new Vector4(Present(_height.Texture), Present(_smoothness.Texture),
                    Present(_occlusion.Texture), Present(_metallic.Texture)));
                material.SetVector(HeightMaskId, Mask(_height.Channel));
                material.SetVector(SmoothnessMaskId, Mask(_smoothness.Channel));
                material.SetVector(OcclusionMaskId, Mask(_occlusion.Channel));
                material.SetVector(MetallicMaskId, Mask(_metallic.Channel));
                material.SetVector(FlagsId, new Vector4(Present(_albedo), Present(_normal),
                    _normal != null && _normal.PackedNormal ? 1 : 0, _normal != null && _normal.FlipGreen ? 1 : 0));
                material.SetVector(OptionsId, new Vector4(_roughness ? 1 : 0, _generate ? 1 : 0, 0, 0));
            }

            public string Describe(int layer)
            {
                return "[" + layer + " albedo " + Origin(_albedo) + ", normal " + Origin(_normal) + ", height "
                    + Origin(_height.Texture) + ", smoothness " + Origin(_smoothness.Texture) + ", occlusion "
                    + Origin(_occlusion.Texture) + "] ";
            }

            public void Dispose()
            {
                foreach (Loaded loaded in _loaded.Values)
                {
                    if (loaded.Created)
                    {
                        Object.DestroyImmediate(loaded.Texture);
                    }
                }
                _loaded.Clear();
            }

            private float Lod(Loaded loaded)
            {
                if (loaded == null) return 0f;
                return Mathf.Max(0f, Mathf.Log(loaded.Texture.width / (float)_resolution, 2f));
            }

            private static Texture Texture(Loaded loaded, Texture fallback)
            {
                return loaded != null ? loaded.Texture : fallback;
            }

            private static float Present(Loaded loaded)
            {
                return loaded != null ? 1f : 0f;
            }

            private static string Origin(Loaded loaded)
            {
                return loaded != null ? loaded.Origin : "default";
            }

            private static Vector4 Mask(TextureChannel channel)
            {
                var mask = Vector4.zero;
                mask[(int)channel] = 1f;
                return mask;
            }

            private Loaded Load(Texture2D source)
            {
                if (!source) return null;
                if (_loaded.TryGetValue(source, out Loaded loaded)) return loaded;
                loaded = Decode(source, _prefetch) ?? Imported(source);
                _loaded[source] = loaded;
                return loaded;
            }

            // The imported texture: compressed, and for a normal map packed by the importer.
            private static Loaded Imported(Texture2D source)
            {
                var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source)) as TextureImporter;
                bool normal = importer && importer.textureType == TextureImporterType.NormalMap;
                return new Loaded { Texture = source, PackedNormal = normal, Origin = "imported" };
            }

            // The source file decoded without the importer, on a worker thread. Returns null when the file or the
            // import settings need the importer, for example a normal map made from a grayscale image.
            private static Loaded Decode(Texture2D source, SourcePrefetch prefetch)
            {
                DecodeRequest request = DecodeRequest.Create(source);
                DecodedImage image = request != null ? prefetch.Take(request) : null;
                if (image == null) return null;
                // Sampling decodes sRGB like the imported texture does, so data maps imported as sRGB keep that meaning.
                var texture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, true, request.Linear)
                {
                    name = source.name + " (source)",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear
                };
                texture.SetPixelData(image.Rgba, 0);
                texture.Apply(true, false);
                return new Loaded
                {
                    Texture = texture,
                    Created = true,
                    FlipGreen = request.FlipGreen,
                    Origin = request.Extension + " " + image.Width
                };
            }
        }

        // A source file that the builder can decode itself. Create it on the main thread: it reads import settings.
        private sealed class DecodeRequest
        {
            private DecodeRequest(string assetPath, string file, string extension, bool linear, bool flipGreen)
            {
                AssetPath = assetPath;
                File = file;
                Extension = extension;
                Linear = linear;
                FlipGreen = flipGreen;
            }

            public string AssetPath { get; }
            /// <summary>File system path. Package paths are not file system paths.</summary>
            public string File { get; }
            public string Extension { get; }
            public bool Linear { get; }
            public bool FlipGreen { get; }

            /// <summary>Returns null when the file type or the import settings need the importer.</summary>
            public static DecodeRequest Create(Texture2D source)
            {
                string path = AssetDatabase.GetAssetPath(source);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (!importer || importer.convertToNormalmap || importer.alphaSource == TextureImporterAlphaSource.FromGrayScale)
                {
                    return null;
                }
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension != ".tga" && extension != ".png" && extension != ".jpg" && extension != ".jpeg") return null;
                bool normal = importer.textureType == TextureImporterType.NormalMap;
                return new DecodeRequest(path, FileUtil.GetPhysicalPath(path), extension.TrimStart('.'),
                    normal || !importer.sRGBTexture, normal && importer.flipGreenChannel);
            }
        }

        private sealed class DecodedImage
        {
            public DecodedImage(int width, int height, byte[] rgba)
            {
                Width = width;
                Height = height;
                Rgba = rgba;
            }

            public int Width { get; }
            public int Height { get; }
            /// <summary>RGBA32 pixels, bottom row first.</summary>
            public byte[] Rgba { get; }
        }

        /// <summary>
        /// Decodes source files on worker threads ahead of the layer that uses them. The number of decoded images that
        /// wait at the same time is limited, which also limits the memory: a 4096 source is 64 MB of pixels.
        /// </summary>
        private sealed class SourcePrefetch : IDisposable
        {
            private readonly List<DecodeRequest> _queue = new();
            private readonly Dictionary<string, Task<DecodedImage>> _running = new();
            private readonly int _limit;
            private int _next;

            public SourcePrefetch(IEnumerable<DecodeRequest> requests)
            {
                var queued = new HashSet<string>();
                foreach (DecodeRequest request in requests)
                {
                    if (queued.Add(request.AssetPath)) _queue.Add(request);
                }
                _limit = Mathf.Clamp(SystemInfo.processorCount - 1, 1, 8);
                Pump();
            }

            /// <summary>Returns the decoded file. It waits for a running decode, or decodes a file that is not queued.</summary>
            public DecodedImage Take(DecodeRequest request)
            {
                if (_running.TryGetValue(request.AssetPath, out Task<DecodedImage> task))
                {
                    _running.Remove(request.AssetPath);
                }
                else
                {
                    // A file that an earlier layer used, or a layer outside the queue. Decode it now.
                    task = Task.Run(() => DecodeFile(request.File));
                }
                DecodedImage image = task.Result;
                Pump();
                return image;
            }

            /// <summary>Waits for running decodes, so no worker reads files after the build stops.</summary>
            public void Dispose()
            {
                try
                {
                    Task.WaitAll(new List<Task<DecodedImage>>(_running.Values).ToArray(), 10000);
                }
                catch (AggregateException)
                {
                    // A failed decode only falls back to the imported texture.
                }
                _running.Clear();
                _next = _queue.Count;
            }

            private void Pump()
            {
                while (_running.Count < _limit && _next < _queue.Count)
                {
                    DecodeRequest request = _queue[_next++];
                    _running[request.AssetPath] = Task.Run(() => DecodeFile(request.File));
                }
            }

            private static DecodedImage DecodeFile(string file)
            {
                try
                {
                    return SourceImageDecoder.TryDecode(File.ReadAllBytes(file), out int width, out int height, out byte[] rgba)
                        ? new DecodedImage(width, height, rgba)
                        : null;
                }
                catch (IOException)
                {
                    return null;
                }
            }
        }
        #endregion
    }
}
