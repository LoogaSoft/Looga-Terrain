using System;
using System.IO;
using LoogaSoft.Terrain.Geology;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// The shipped surface of a water body. A standalone player build writes the built output of each body to a file
    /// (<see cref="GeneratedMeshCache"/>): the mesh with its flow, the shore texture and its regime texture, the
    /// coverage mask and the shore wave field. The player loads the file when the body is enabled and does not build
    /// the surface, the shore and the currents at the scene load. A body without a file builds as before.
    /// </summary>
    /// <remarks>
    /// The file holds the output that the editor shows. The output key changes with each new output, so a player build
    /// writes the file again only after a change. The plunge waves and the near shore window are built in the player:
    /// they read the network solve and the camera.
    /// </remarks>
    public sealed partial class LoogaWaterBody : IGeneratedMeshSource
    {
        private const int ShippedFormatVersion = 1;

        // Changes with each new output. Kept over a script reload with the output.
        [NonSerialized] private Hash128 _outputKey;
        // The network flow of the shipped surface. The network does not build the flow again when it is the same.
        [NonSerialized] private Hash128 _shippedFlow;
        [NonSerialized] private bool _hasShippedFlow;
        // True from the load of a shipped surface to Start. The flow zones of the scene enable during the scene load,
        // and the shipped flow has them already.
        [NonSerialized] private bool _shippedLoading;

        /// <summary>
        /// The network data of the flow of a shipped surface (<see cref="LoogaWaterNetwork.TryGetBodyFlow"/> at the
        /// player build). False when the surface was built in the player.
        /// </summary>
        internal bool TryGetShippedFlow(out Hash128 flow)
        {
            flow = _shippedFlow;
            return _hasShippedFlow;
        }

        // A new output makes the shipped file old.
        private void NewOutput()
        {
            _outputKey = Hash128.Compute(Guid.NewGuid().ToString("N"));
        }

        #region Load
        // Loads the shipped surface in a player. Returns false when there is no file, or it cannot be read: the body
        // builds then.
        private bool LoadShipped()
        {
            if (!Application.isPlaying) return false;
            string path = GeneratedMeshCache.PlayerPath(this);
            if (path == null) return false;

            using ProfilerMarker.AutoScope load = LoogaWaterMarkers.Load.Auto();
            try
            {
                using BinaryReader reader = GeneratedMeshCache.OpenFile(path, null);
                if (reader == null) return false;
                ReadShipped(reader);
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException)
            {
                Debug.LogWarning($"{name}: the shipped water surface cannot be read ({exception.Message}). The body " +
                    "builds it.", this);
                Release();
                _hasShippedFlow = false;
                return false;
            }
            _shippedLoading = true;
            return true;
        }

        private void ReadShipped(BinaryReader reader)
        {
            if (reader.ReadInt32() != ShippedFormatVersion) throw new InvalidDataException("Other file version.");

            _mesh = GeneratedMeshCache.RawMesh.Read(reader);
            VertexCount = reader.ReadInt32();
            _worldRect = new Rect(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            _worldHeights = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            for (int index = 0; index < 16; index++)
            {
                _builtMatrix[index] = reader.ReadSingle();
            }
            _hasShippedFlow = reader.ReadBoolean();
            _shippedFlow = Hash128.Parse(reader.ReadString());
            if (reader.ReadBoolean())
            {
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                float texel = reader.ReadSingle();
                var origin = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                Texture2D shore = ReadTexture(reader);
                Texture2D regime = reader.ReadBoolean() ? ReadTexture(reader) : null;
                _shore.Adopt(shore, regime, width, height, texel, origin, ReadFlags(reader));
            }
            if (reader.ReadBoolean())
            {
                var rect = new Rect(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                bool open = reader.ReadBoolean();
                _coverage.Adopt(ReadTexture(reader), rect, open);
            }
            if (reader.ReadBoolean())
            {
                _shoreWaveField.Adopt(ReadTexture(reader));
            }

            _builtInputs = GetBuildInputs();
            BuildMilliseconds = 0f;
            ShoreMilliseconds = 0f;
            Diagnostic = string.Empty;
            _buildQueued = false;
            _flowQueued = false;
            _shoreQueued = false;
            // The file has no vertex data for a flow build. A flow change builds the surface again.
            _buildDataLost = true;
            NewOutput();
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            ApplyShore();
        }

        // Reads the texels straight into the texture memory, so the load makes no garbage.
        private static Texture2D ReadTexture(BinaryReader reader)
        {
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            var format = (TextureFormat)reader.ReadInt32();
            var texture = new Texture2D(width, height, format, false, true)
            {
                name = reader.ReadString(),
                hideFlags = HideFlags.DontSave,
                wrapMode = (TextureWrapMode)reader.ReadInt32(),
                filterMode = (FilterMode)reader.ReadInt32()
            };
            int bytes = reader.ReadInt32();
            NativeArray<byte> data = texture.GetRawTextureData<byte>();
            if (data.Length != bytes)
            {
                Destroy(texture);
                throw new InvalidDataException("Texture size " + bytes + " is not " + data.Length + ".");
            }
            Span<byte> texels = data.AsSpan();
            int read = 0;
            while (read < bytes)
            {
                int part = reader.BaseStream.Read(texels.Slice(read));
                if (part == 0) throw new InvalidDataException("Short file.");
                read += part;
            }
            texture.Apply(false, false);
            return texture;
        }

        private static bool[] ReadFlags(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count == 0) return null;
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new InvalidDataException("Short file.");
            var flags = new bool[count];
            for (int index = 0; index < count; index++)
            {
                flags[index] = bytes[index] != 0;
            }
            return flags;
        }
        #endregion

#if UNITY_EDITOR
        #region Player Build
        bool IGeneratedMeshSource.GetCacheFile(out string path, out Hash128 key)
        {
            path = GeneratedMeshCache.PathOf(this);
            key = default;
            if (!isActiveAndEnabled) return false;

            // A queued build, flow or network solve changes the output. The file cannot have this key.
            LoogaWaterNetwork network = LoogaWaterNetwork.Find(this);
            bool pending = _buildQueued || _flowQueued || _shoreQueued || network && network.SolveQueued;
            if (pending) return true;
            if (!_mesh || VertexCount == 0) return false;

            key = _outputKey;
            return true;
        }

        GeneratedMeshBuild IGeneratedMeshSource.BuildForPlayer()
        {
            if (!isActiveAndEnabled) return GeneratedMeshBuild.Never;

            // Output that came over a script reload has no obstacle texels for the file.
            if (_buildQueued || _buildDataLost) Rebuild();
            LoogaWaterNetwork network = LoogaWaterNetwork.Find(this);
            if (network) network.SolveIfChanged();
            if (_flowQueued) RebuildFlow();
            if (_shoreQueued) RebuildShore();
            if (_buildQueued || _flowQueued || _shoreQueued || !_mesh || VertexCount == 0)
            {
                return GeneratedMeshBuild.Never;
            }
            WriteShipped(network);
            return GeneratedMeshBuild.Built;
        }

        bool IGeneratedMeshSource.PlayerCanBuild => true;

        // Copies the output on the main thread. A worker thread writes the file.
        private void WriteShipped(LoogaWaterNetwork network)
        {
            GeneratedMeshCache.RawMesh mesh = GeneratedMeshCache.RawMesh.From(_mesh);
            int vertexCount = VertexCount;
            Rect worldRect = _worldRect;
            Vector2 heights = _worldHeights;
            Matrix4x4 matrix = _builtMatrix;
            Hash128 flow = default;
            bool hasFlow = network && network.TryGetBodyFlow(this, out flow);
            bool shore = _shore.IsBuilt;
            int shoreWidth = _shore.Width;
            int shoreHeight = _shore.Height;
            float texel = _shore.TexelSize;
            Vector2 origin = _shore.Origin;
            TextureCopy shoreMap = shore ? new TextureCopy(_shore.Texture) : null;
            TextureCopy regime = shore && _shore.RegimeTexture ? new TextureCopy(_shore.RegimeTexture) : null;
            // Only the near shore window reads the obstacles, in an area with shore waves.
            byte[] obstacles = shore && _shoreWaves && _shape == WaterShape.Area ? FlagBytes(_shore.Obstacles) : null;
            bool coverage = _coverage.IsBuilt;
            Rect coverageRect = _coverage.Rect;
            bool coverageOpen = _coverage.OpenOutside;
            TextureCopy coverageMap = coverage ? new TextureCopy(_coverage.Texture) : null;
            TextureCopy waves = _shoreWaveField.Texture ? new TextureCopy(_shoreWaveField.Texture) : null;

            GeneratedMeshCache.WriteFile(GeneratedMeshCache.PathOf(this), _outputKey, writer =>
            {
                writer.Write(ShippedFormatVersion);
                mesh.Write(writer);
                writer.Write(vertexCount);
                writer.Write(worldRect.x);
                writer.Write(worldRect.y);
                writer.Write(worldRect.width);
                writer.Write(worldRect.height);
                writer.Write(heights.x);
                writer.Write(heights.y);
                for (int index = 0; index < 16; index++)
                {
                    writer.Write(matrix[index]);
                }
                writer.Write(hasFlow);
                writer.Write(flow.ToString());
                writer.Write(shore);
                if (shore)
                {
                    writer.Write(shoreWidth);
                    writer.Write(shoreHeight);
                    writer.Write(texel);
                    writer.Write(origin.x);
                    writer.Write(origin.y);
                    shoreMap.Write(writer);
                    writer.Write(regime != null);
                    regime?.Write(writer);
                    writer.Write(obstacles?.Length ?? 0);
                    if (obstacles != null) writer.Write(obstacles);
                }
                writer.Write(coverage);
                if (coverage)
                {
                    writer.Write(coverageRect.x);
                    writer.Write(coverageRect.y);
                    writer.Write(coverageRect.width);
                    writer.Write(coverageRect.height);
                    writer.Write(coverageOpen);
                    coverageMap.Write(writer);
                }
                writer.Write(waves != null);
                waves?.Write(writer);
            });
        }

        private static byte[] FlagBytes(bool[] flags)
        {
            if (flags == null) return null;
            var bytes = new byte[flags.Length];
            for (int index = 0; index < flags.Length; index++)
            {
                bytes[index] = flags[index] ? (byte)1 : (byte)0;
            }
            return bytes;
        }

        // The texels and settings of a texture, copied on the main thread.
        private sealed class TextureCopy
        {
            private readonly int _width;
            private readonly int _height;
            private readonly TextureFormat _format;
            private readonly string _name;
            private readonly TextureWrapMode _wrap;
            private readonly FilterMode _filter;
            private readonly byte[] _texels;

            public TextureCopy(Texture2D texture)
            {
                _width = texture.width;
                _height = texture.height;
                _format = texture.format;
                _name = texture.name;
                _wrap = texture.wrapMode;
                _filter = texture.filterMode;
                _texels = texture.GetRawTextureData<byte>().ToArray();
            }

            public void Write(BinaryWriter writer)
            {
                writer.Write(_width);
                writer.Write(_height);
                writer.Write((int)_format);
                writer.Write(_name);
                writer.Write((int)_wrap);
                writer.Write((int)_filter);
                writer.Write(_texels.Length);
                writer.Write(_texels);
            }
        }
        #endregion
#endif
    }
}
