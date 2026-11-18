using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// The shore texture of a water surface. R is the signed horizontal distance from the water edge, positive in the
    /// water and negative on dry ground, in metres. The sign puts the edge between the texels when the texture is
    /// filtered, so large texels still give a band of the correct width. G is the water depth in metres, and -1 outside
    /// the surface mesh, so the shader can find an outline that cuts through water instead of a shore.
    /// B and A are the world XZ flow of the surface in metres per second.
    /// The water edge is where the surface meets the terrain or a collider that goes through the surface, for example
    /// a rock in a river, so the foam band has the same width on flat and steep banks, and it does not change with the
    /// camera like a depth-buffer test. The surface heights stay in memory, so a terrain change reads the ground again
    /// only in the changed area.
    /// </summary>
    internal sealed class WaterShoreField
    {
        // Largest texture side. Large water bodies get larger texels.
        private const int MaximumSide = 1024;
        // Margin around the surface, in texels, so the edge texels of the water have dry neighbours.
        private const int Margin = 2;
        private const float Infinity = 1e20f;
        private const float MaximumDistance = 10000f;
        // Depth value of the texels outside the surface mesh.
        private const float OutsideDepth = -1f;
        // A texel outside the surface takes the level of the nearest surface texel within this many texels. Ground more
        // than OpenWaterDepth below that level is open water past the outline, for example where another river
        // continues this one or a river mouth opens into a lake. It is not a shore, so it gets no shore foam.
        // All texels outside the surface within the reach take the flow of the nearest surface texel, so the foam
        // simulation sees no flow stop at the outline and does not pile foam up there.
        private const int OpenWaterReach = 8;
        private const float OpenWaterDepth = 0.25f;
        // Half height of the box that finds colliders through the surface, in metres.
        private const float ObstacleHalfHeight = 0.05f;
        private const int ObstacleHits = 4;

        private static readonly List<UnityEngine.Terrain> Terrains = new();
        // The colliders that can stop the water in the area of a refresh, and the texels in their bounds.
        private static readonly Collider[] Candidates = new Collider[2048];
        private static readonly HashSet<int> CandidateIds = new();
        private static readonly List<int> Indices = new();

        private int _width;
        private int _height;
        private float _texel;
        private Vector2 _origin;
        private int _obstacleLayers;
        private float[] _surface;
        // Level and flow of the nearest surface texel for the texels outside the surface. Level NaN beyond the open
        // water reach.
        private float[] _nearLevel;
        private Vector2[] _nearFlow;
        private Vector2[] _flow;
        private float[] _depth;
        private bool[] _dry;
        // Wet texels that a collider through the surface makes dry.
        private bool[] _obstacle;
        // The obstacle texels of the refreshed area before the refresh, row by row, so a refresh finds a change.
        private bool[] _obstacleBefore;
        private float[] _distance;
        private Color[] _pixels;
        private Texture2D _texture;
        // Regime values of the surface at each texel (x = turbulence, y = aeration), from the mesh UV1.
        private Vector2[] _regime;
        private byte[] _regimeBytes;
        private Texture2D _regimeTexture;

        public Texture2D Texture => _texture;
        /// <summary>
        /// R = turbulence, G = aeration of the flow regime, 0 to 1, with the layout of the shore texture. Null when the
        /// surface has no regime values, for example outside a water network.
        /// </summary>
        public Texture2D RegimeTexture => _regimeTexture;
        public int Width => _width;
        public int Height => _height;
        public float TexelSize => _texel;
        public Vector2 Origin => _origin;
        /// <summary>True when the texture holds the shore of the last built surface.</summary>
        public bool IsBuilt { get; private set; }

        /// <summary>Maps world XZ to texture UV: uv = (world - xy) * zw. Zero when the texture is not built.</summary>
        public Vector4 UVRect => IsBuilt
            ? new Vector4(_origin.x, _origin.y, 1f / (_width * _texel), 1f / (_height * _texel))
            : Vector4.zero;

        /// <summary>
        /// Wet texels that a collider through the surface makes dry, row by row. Null when the field has no texel data,
        /// for example after a script reload.
        /// </summary>
        public bool[] Obstacles => _obstacle;

        /// <summary>
        /// Takes a built texture, for example of the last domain after a script reload or from a shipped file.
        /// <see cref="RefreshFlow"/> and <see cref="Refresh"/> have no data then.
        /// </summary>
        /// <param name="obstacles">The <see cref="Obstacles"/> of the build, or null.</param>
        public void Adopt(Texture2D texture, Texture2D regimeTexture, int width, int height, float texel,
            Vector2 origin, bool[] obstacles)
        {
            _texture = texture;
            _regimeTexture = regimeTexture;
            _width = width;
            _height = height;
            _texel = texel;
            _origin = origin;
            _obstacle = obstacles != null && obstacles.Length == width * height ? obstacles : null;
            IsBuilt = true;
        }

        /// <summary>Builds the shore texture of a surface mesh. UV0 xy of the mesh is the world XZ flow.</summary>
        /// <param name="obstacleLayers">Layers of the colliders that go through the surface and stop the water.</param>
        /// <returns>False when no terrain is under the surface.</returns>
        public bool Build(Mesh mesh, Matrix4x4 localToWorld, Rect worldRect, float texelSize, int obstacleLayers)
        {
            IsBuilt = false;
            _texel = Mathf.Max(texelSize,
                Mathf.Max(worldRect.width, worldRect.height) / (MaximumSide - 2 * Margin - 1));
            _width = Mathf.CeilToInt(worldRect.width / _texel) + 2 * Margin;
            _height = Mathf.CeilToInt(worldRect.height / _texel) + 2 * Margin;
            _origin = new Vector2(worldRect.xMin - Margin * _texel, worldRect.yMin - Margin * _texel);
            _obstacleLayers = obstacleLayers;
            int count = _width * _height;
            _surface = new float[count];
            _flow = new Vector2[count];
            _regime = new Vector2[count];
            Rasterize(mesh, localToWorld, _surface, _flow, _regime);
            NearSurface(_surface, _flow, _width, _height, out _nearLevel, out _nearFlow);
            _depth = new float[count];
            _dry = new bool[count];
            _obstacle = new bool[count];
            _distance = new float[count];
            _pixels = new Color[count];
            return Refresh(new Rect(_origin, new Vector2(_width, _height) * _texel));
        }

        /// <summary>True when a collider through the surface makes the texel at a world XZ point dry.</summary>
        /// <returns>False outside the texture or when the field has no obstacle data.</returns>
        internal bool IsObstacle(Vector2 world)
        {
            if (_obstacle == null) return false;

            int column = Mathf.FloorToInt((world.x - _origin.x) / _texel);
            int row = Mathf.FloorToInt((world.y - _origin.y) / _texel);
            if (column < 0 || row < 0 || column >= _width || row >= _height) return false;

            return _obstacle[row * _width + column];
        }

        /// <summary>Reads the flow of the mesh again, for example after flow zones changed.</summary>
        public void RefreshFlow(Mesh mesh, Matrix4x4 localToWorld)
        {
            if (!IsBuilt || mesh.vertexCount == 0) return;

            if (_regime == null || _regime.Length != _flow.Length) _regime = new Vector2[_flow.Length];
            Rasterize(mesh, localToWorld, null, _flow, _regime);
            NearSurface(_surface, _flow, _width, _height, out _nearLevel, out _nearFlow);
            WriteTexture();
        }

        /// <summary>Reads the ground again in a world XZ area and updates the texture.</summary>
        /// <returns>False when the field has no surface or no terrain is under it.</returns>
        public bool Refresh(Rect region)
        {
            return Refresh(region, out _);
        }

        /// <summary>
        /// Reads the ground again in a world XZ area and updates the texture when a texel of the area changed.
        /// </summary>
        /// <param name="changed">
        /// True when the depth, the dry state or an obstacle of a texel changed, so the texture changed. False keeps
        /// the texture: it holds the same values.
        /// </param>
        /// <returns>False when the field has no surface or no terrain is under it.</returns>
        public bool Refresh(Rect region, out bool changed)
        {
            changed = false;
            if (_surface == null) return false;

            int minColumn = Mathf.Max(0, Mathf.FloorToInt((region.xMin - _origin.x) / _texel));
            int maxColumn = Mathf.Min(_width - 1, Mathf.CeilToInt((region.xMax - _origin.x) / _texel));
            int minRow = Mathf.Max(0, Mathf.FloorToInt((region.yMin - _origin.y) / _texel));
            int maxRow = Mathf.Min(_height - 1, Mathf.CeilToInt((region.yMax - _origin.y) / _texel));
            if (minColumn > maxColumn || minRow > maxRow) return IsBuilt;
            var area = Rect.MinMaxRect(_origin.x + minColumn * _texel, _origin.y + minRow * _texel,
                _origin.x + (maxColumn + 1) * _texel, _origin.y + (maxRow + 1) * _texel);
            if (!CollectTerrains(area)) return false;

            int columns = maxColumn - minColumn + 1;
            int count = columns * (maxRow - minRow + 1);
            if (_obstacleBefore == null || _obstacleBefore.Length < count)
            {
                _obstacleBefore = new bool[count];
            }
            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    int index = row * _width + column;
                    float level = _surface[index];
                    Vector2 point = _origin + new Vector2(column + 0.5f, row + 0.5f) * _texel;
                    float depth = OutsideDepth;
                    bool dry = true;
                    if (!float.IsNaN(level))
                    {
                        depth = TrySampleGround(point, out float ground) ? Mathf.Max(level - ground, 0f) : 0f;
                        dry = depth <= 0f;
                    }
                    else if (!float.IsNaN(_nearLevel[index]) && TrySampleGround(point, out float ground) &&
                             ground < _nearLevel[index] - OpenWaterDepth)
                    {
                        dry = false;
                    }
                    changed |= depth != _depth[index] || dry != _dry[index];
                    _depth[index] = depth;
                    _dry[index] = dry;
                    _obstacleBefore[(row - minRow) * columns + column - minColumn] = _obstacle[index];
                    _obstacle[index] = false;
                }
            }
            MarkObstacles(minColumn, maxColumn, minRow, maxRow);
            for (int row = minRow; row <= maxRow && !changed; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    int before = (row - minRow) * columns + column - minColumn;
                    if (_obstacle[row * _width + column] == _obstacleBefore[before]) continue;
                    changed = true;
                    break;
                }
            }
            // The distances and the texture read only these texels. A built texture already holds the same values.
            if (!changed && IsBuilt) return true;

            changed = true;

            float[] toDry = DistanceTo(_dry, true, _width, _height);
            float[] toWet = DistanceTo(_dry, false, _width, _height);
            for (int index = 0; index < _distance.Length; index++)
            {
                // The edge of the water is between a dry texel and a wet one, half a texel from each centre.
                float metres = _dry[index]
                    ? -Mathf.Max(Mathf.Sqrt(toWet[index]) - 0.5f, 0f) * _texel
                    : Mathf.Max(Mathf.Sqrt(toDry[index]) - 0.5f, 0f) * _texel;
                // Half floats hold the distances that matter for foam. A surface without dry texels has no edge.
                _distance[index] = Mathf.Clamp(metres, -MaximumDistance, MaximumDistance);
            }
            WriteTexture();
            IsBuilt = true;
            return true;
        }

        private void WriteTexture()
        {
            if (!_texture || _texture.width != _width || _texture.height != _height)
            {
                Release();
                _texture = new Texture2D(_width, _height, TextureFormat.RGBAHalf, false, true)
                {
                    name = "Looga Water Shore", hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
            }
            for (int index = 0; index < _pixels.Length; index++)
            {
                Vector2 flow = float.IsNaN(_surface[index]) ? _nearFlow[index] : _flow[index];
                _pixels[index] = new Color(_distance[index], _depth[index], flow.x, flow.y);
            }
            _texture.SetPixels(_pixels);
            _texture.Apply(false, false);
            WriteRegimeTexture();
        }

        // Eight bits for each value is enough for the foam sources. A surface without regime values has no texture.
        private void WriteRegimeTexture()
        {
            // After a script reload the field has the adopted texture but no texel data until the next flow update.
            if (_regime == null) return;

            bool any = false;
            foreach (Vector2 value in _regime)
            {
                if (value.x <= 0f && value.y <= 0f) continue;

                any = true;
                break;
            }
            if (!any)
            {
                DestroyTexture(ref _regimeTexture);
                return;
            }
            if (!_regimeTexture || _regimeTexture.width != _width || _regimeTexture.height != _height)
            {
                DestroyTexture(ref _regimeTexture);
                _regimeTexture = new Texture2D(_width, _height, TextureFormat.RG16, false, true)
                {
                    name = "Looga Water Regime", hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
            }
            if (_regimeBytes == null || _regimeBytes.Length != _regime.Length * 2)
            {
                _regimeBytes = new byte[_regime.Length * 2];
            }
            for (int index = 0; index < _regime.Length; index++)
            {
                _regimeBytes[index * 2] = (byte)Mathf.RoundToInt(Mathf.Clamp01(_regime[index].x) * 255f);
                _regimeBytes[index * 2 + 1] = (byte)Mathf.RoundToInt(Mathf.Clamp01(_regime[index].y) * 255f);
            }
            _regimeTexture.SetPixelData(_regimeBytes, 0);
            _regimeTexture.Apply(false, false);
        }

        // Wet texels where a collider goes through the surface become dry with depth 0, so the shore goes around the
        // collider. Terrain colliders and moving bodies are not obstacles: the terrain is sampled already. One query
        // over the area finds the candidate colliders, and only the texels in their bounds get a query each.
        private void MarkObstacles(int minColumn, int maxColumn, int minRow, int maxRow)
        {
            if (_obstacleLayers == 0) return;

            float low = float.PositiveInfinity;
            float high = float.NegativeInfinity;
            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    int index = row * _width + column;
                    if (_dry[index]) continue;
                    low = Mathf.Min(low, _surface[index]);
                    high = Mathf.Max(high, _surface[index]);
                }
            }
            if (low > high) return;

            Vector2 areaMin = _origin + new Vector2(minColumn, minRow) * _texel;
            Vector2 areaMax = _origin + new Vector2(maxColumn + 1, maxRow + 1) * _texel;
            var areaCentre = new Vector3((areaMin.x + areaMax.x) * 0.5f, (low + high) * 0.5f,
                (areaMin.y + areaMax.y) * 0.5f);
            var areaExtents = new Vector3((areaMax.x - areaMin.x) * 0.5f, (high - low) * 0.5f + ObstacleHalfHeight,
                (areaMax.y - areaMin.y) * 0.5f);
            int found = Physics.OverlapBoxNonAlloc(areaCentre, areaExtents, Candidates, Quaternion.identity,
                _obstacleLayers, QueryTriggerInteraction.Ignore);
            CandidateIds.Clear();
            Indices.Clear();
            var inBounds = new bool[(maxColumn - minColumn + 1) * (maxRow - minRow + 1)];
            for (int candidate = 0; candidate < found; candidate++)
            {
                Collider collider = Candidates[candidate];
                if (!collider || collider is TerrainCollider ||
                    (collider.attachedRigidbody && !collider.attachedRigidbody.isKinematic)) continue;
                CandidateIds.Add(collider.GetInstanceID());
                Bounds bounds = collider.bounds;
                int fromColumn = Mathf.Max(minColumn, Mathf.FloorToInt((bounds.min.x - _origin.x) / _texel));
                int toColumn = Mathf.Min(maxColumn, Mathf.FloorToInt((bounds.max.x - _origin.x) / _texel));
                int fromRow = Mathf.Max(minRow, Mathf.FloorToInt((bounds.min.z - _origin.y) / _texel));
                int toRow = Mathf.Min(maxRow, Mathf.FloorToInt((bounds.max.z - _origin.y) / _texel));
                for (int row = fromRow; row <= toRow; row++)
                {
                    for (int column = fromColumn; column <= toColumn; column++)
                    {
                        int index = row * _width + column;
                        float level = _surface[index];
                        if (_dry[index] || level < bounds.min.y - ObstacleHalfHeight ||
                            level > bounds.max.y + ObstacleHalfHeight) continue;
                        int local = (row - minRow) * (maxColumn - minColumn + 1) + column - minColumn;
                        if (inBounds[local]) continue;
                        inBounds[local] = true;
                        Indices.Add(index);
                    }
                }
            }
            if (Indices.Count == 0) return;

            var commands = new NativeArray<OverlapBoxCommand>(Indices.Count, Allocator.TempJob);
            var results = new NativeArray<ColliderHit>(Indices.Count * ObstacleHits, Allocator.TempJob);
            try
            {
                var query = new QueryParameters(_obstacleLayers, false, QueryTriggerInteraction.Ignore, false);
                var extents = new Vector3(_texel * 0.5f, ObstacleHalfHeight, _texel * 0.5f);
                for (int command = 0; command < Indices.Count; command++)
                {
                    int index = Indices[command];
                    Vector2 world = _origin + new Vector2(index % _width + 0.5f, index / _width + 0.5f) * _texel;
                    var centre = new Vector3(world.x, _surface[index], world.y);
                    commands[command] = new OverlapBoxCommand(centre, extents, Quaternion.identity, query);
                }
                OverlapBoxCommand.ScheduleBatch(commands, results, 64, ObstacleHits).Complete();
                for (int command = 0; command < Indices.Count; command++)
                {
                    for (int hit = 0; hit < ObstacleHits; hit++)
                    {
                        int instance = results[command * ObstacleHits + hit].instanceID;
                        if (instance == 0) break;
                        if (!CandidateIds.Contains(instance)) continue;
                        _dry[Indices[command]] = true;
                        _obstacle[Indices[command]] = true;
                        _depth[Indices[command]] = 0f;
                        break;
                    }
                }
            }
            finally
            {
                commands.Dispose();
                results.Dispose();
            }
        }

        /// <summary>Destroys the textures.</summary>
        public void Release()
        {
            IsBuilt = false;
            DestroyTexture(ref _texture);
            DestroyTexture(ref _regimeTexture);
        }

        private static void DestroyTexture(ref Texture2D texture)
        {
            if (!texture) return;

            if (Application.isPlaying)
            {
                Object.Destroy(texture);
            }
            else
            {
                Object.DestroyImmediate(texture);
            }
            texture = null;
        }

        // The terrains that cover the region, smallest first, so a large background terrain under the tiles is not used.
        // The water body also uses it for the open sides of a river end.
        internal static bool CollectTerrains(Rect region)
        {
            Terrains.Clear();
            foreach (UnityEngine.Terrain terrain in UnityEngine.Terrain.activeTerrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                Vector3 position = terrain.GetPosition();
                Vector3 size = terrain.terrainData.size;
                if (new Rect(position.x, position.z, size.x, size.z).Overlaps(region)) Terrains.Add(terrain);
            }
            Terrains.Sort((a, b) =>
            {
                Vector3 sizeA = a.terrainData.size;
                Vector3 sizeB = b.terrainData.size;
                return (sizeA.x * sizeA.z).CompareTo(sizeB.x * sizeB.z);
            });
            return Terrains.Count > 0;
        }

        /// <summary>The terrains of the last CollectTerrains call, smallest first.</summary>
        internal static IReadOnlyList<UnityEngine.Terrain> CollectedTerrains => Terrains;

        // Ground height from the terrains of the last CollectTerrains call.
        internal static bool TrySampleGround(Vector2 world, out float height)
        {
            foreach (UnityEngine.Terrain terrain in Terrains)
            {
                Vector3 position = terrain.GetPosition();
                Vector3 size = terrain.terrainData.size;
                if (world.x < position.x || world.y < position.z || world.x > position.x + size.x ||
                    world.y > position.z + size.z) continue;
                height = terrain.SampleHeight(new Vector3(world.x, 0f, world.y)) + position.y;
                return true;
            }
            height = 0f;
            return false;
        }

        // Surface height (NaN outside the surface), flow and regime values (zero outside) at each texel centre, from
        // the mesh triangles before the waves. A null surface array is not written.
        private void Rasterize(Mesh mesh, Matrix4x4 localToWorld, float[] surface, Vector2[] flow, Vector2[] regime)
        {
            Vector2 origin = _origin;
            float texel = _texel;
            int width = _width;
            int height = _height;
            if (surface != null)
            {
                for (int index = 0; index < surface.Length; index++) surface[index] = float.NaN;
            }
            System.Array.Clear(flow, 0, flow.Length);
            System.Array.Clear(regime, 0, regime.Length);
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            var uvs = new List<Vector4>();
            mesh.GetUVs(0, uvs);
            bool hasFlow = uvs.Count == vertices.Length;
            var regimeUVs = new List<Vector2>();
            mesh.GetUVs(1, regimeUVs);
            bool hasRegime = regimeUVs.Count == vertices.Length;
            for (int index = 0; index < vertices.Length; index++)
            {
                vertices[index] = localToWorld.MultiplyPoint3x4(vertices[index]);
            }
            for (int index = 0; index < triangles.Length; index += 3)
            {
                Vector3 a = vertices[triangles[index]];
                Vector3 b = vertices[triangles[index + 1]];
                Vector3 c = vertices[triangles[index + 2]];
                // Texel coordinates of the corners.
                var pa = new Vector2((a.x - origin.x) / texel - 0.5f, (a.z - origin.y) / texel - 0.5f);
                var pb = new Vector2((b.x - origin.x) / texel - 0.5f, (b.z - origin.y) / texel - 0.5f);
                var pc = new Vector2((c.x - origin.x) / texel - 0.5f, (c.z - origin.y) / texel - 0.5f);
                float area = Cross(pb - pa, pc - pa);
                if (Mathf.Abs(area) < 1e-8f) continue;
                int minColumn = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.x, Mathf.Min(pb.x, pc.x))));
                int maxColumn = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(pa.x, Mathf.Max(pb.x, pc.x))));
                int minRow = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.y, Mathf.Min(pb.y, pc.y))));
                int maxRow = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(pa.y, Mathf.Max(pb.y, pc.y))));
                for (int row = minRow; row <= maxRow; row++)
                {
                    for (int column = minColumn; column <= maxColumn; column++)
                    {
                        var p = new Vector2(column, row);
                        float wa = Cross(pb - p, pc - p) / area;
                        float wb = Cross(pc - p, pa - p) / area;
                        float wc = 1f - wa - wb;
                        if (wa < -1e-4f || wb < -1e-4f || wc < -1e-4f) continue;
                        int texelIndex = row * width + column;
                        if (surface != null) surface[texelIndex] = wa * a.y + wb * b.y + wc * c.y;
                        if (!hasFlow) continue;
                        Vector4 fa = uvs[triangles[index]];
                        Vector4 fb = uvs[triangles[index + 1]];
                        Vector4 fc = uvs[triangles[index + 2]];
                        flow[texelIndex] = new Vector2(wa * fa.x + wb * fb.x + wc * fc.x,
                            wa * fa.y + wb * fb.y + wc * fc.y);
                        if (!hasRegime) continue;

                        regime[texelIndex] = wa * regimeUVs[triangles[index]] + wb * regimeUVs[triangles[index + 1]] +
                            wc * regimeUVs[triangles[index + 2]];
                    }
                }
            }
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        // Squared distance in texels from each texel to the nearest texel where dry has the target value: the exact
        // Euclidean distance transform of Felzenszwalb and Huttenlocher, one pass along the columns and one along the rows.
        // Spreads the level and flow of each surface texel to the texels outside the surface, up to OpenWaterReach
        // texels in four-neighbour steps. Texels farther away keep level NaN and no flow.
        private static void NearSurface(float[] surface, Vector2[] flow, int width, int height, out float[] levels,
            out Vector2[] flows)
        {
            levels = new float[surface.Length];
            flows = new Vector2[surface.Length];
            var steps = new int[surface.Length];
            var queue = new Queue<int>();
            for (int index = 0; index < surface.Length; index++)
            {
                levels[index] = surface[index];
                flows[index] = flow[index];
                if (!float.IsNaN(surface[index])) queue.Enqueue(index);
            }
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                if (steps[index] >= OpenWaterReach) continue;
                int column = index % width;
                int row = index / width;
                for (int side = 0; side < 4; side++)
                {
                    int x = column + (side == 0 ? 1 : side == 1 ? -1 : 0);
                    int y = row + (side == 2 ? 1 : side == 3 ? -1 : 0);
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    int next = y * width + x;
                    if (!float.IsNaN(levels[next])) continue;
                    levels[next] = levels[index];
                    flows[next] = flows[index];
                    steps[next] = steps[index] + 1;
                    queue.Enqueue(next);
                }
            }
        }

        /// <summary>Squared distance in texels from each texel to the nearest texel whose dry value is the target.</summary>
        internal static float[] DistanceTo(bool[] dry, bool target, int width, int height)
        {
            var result = new float[width * height];
            for (int index = 0; index < result.Length; index++) result[index] = dry[index] == target ? 0f : Infinity;
            int side = Mathf.Max(width, height);
            var line = new float[side];
            var output = new float[side];
            var hull = new int[side];
            var bounds = new float[side + 1];
            for (int column = 0; column < width; column++)
            {
                for (int row = 0; row < height; row++) line[row] = result[row * width + column];
                Transform1D(line, height, output, hull, bounds);
                for (int row = 0; row < height; row++) result[row * width + column] = output[row];
            }
            for (int row = 0; row < height; row++)
            {
                System.Array.Copy(result, row * width, line, 0, width);
                Transform1D(line, width, output, hull, bounds);
                System.Array.Copy(output, 0, result, row * width, width);
            }
            return result;
        }

        // Lower envelope of the parabolas at each sample: output[q] = min over p of (q - p)^2 + values[p].
        private static void Transform1D(float[] values, int count, float[] output, int[] hull, float[] bounds)
        {
            int k = 0;
            hull[0] = 0;
            bounds[0] = -Infinity;
            bounds[1] = Infinity;
            for (int q = 1; q < count; q++)
            {
                float s;
                while (true)
                {
                    int p = hull[k];
                    s = (values[q] + q * q - (values[p] + p * p)) / (2f * q - 2f * p);
                    if (s > bounds[k] || k == 0) break;
                    k--;
                }
                if (s <= bounds[k] && k == 0)
                {
                    hull[0] = q;
                    bounds[1] = Infinity;
                    continue;
                }
                k++;
                hull[k] = q;
                bounds[k] = s;
                bounds[k + 1] = Infinity;
            }
            k = 0;
            for (int q = 0; q < count; q++)
            {
                while (bounds[k + 1] < q) k++;
                int p = hull[k];
                output[q] = (q - p) * (q - p) + values[p];
            }
        }
    }
}
