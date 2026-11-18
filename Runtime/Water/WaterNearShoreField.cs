using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// A fine shore texture in a window around the camera, with the same channels as the shore texture of the body:
    /// R is the signed distance from the water edge in metres (positive in the water) and G the water depth (-1 outside
    /// the surface). The shore texture of a large body has texels of several metres, so the water edge, the shore waves
    /// and the shore foam that follow it show the texel polygons. This window reads the ground every half metre.
    /// The shader blends to the shore texture of the body at the border of the window.
    /// The main thread reads the terrain heights of the window. A job finds the depth and the distances, and the next
    /// camera after the job puts the result in the texture. Until then the previous window stays in use.
    /// </summary>
    internal sealed class WaterNearShoreField
    {
        // Texels along each side of the window and their size in metres.
        private const int Size = 256;
        private const float Texel = 0.5f;
        // The window moves when the camera is this far from its centre, in metres.
        private const float MoveDistance = 24f;
        // The window centre snaps to this step, in metres, so the texels stay on the same world grid.
        private const float Snap = 8f;
        private const float OutsideDepth = -1f;
        private const float MaximumDistance = 10000f;

        private static readonly List<TileHeights> TileList = new();
        private static readonly List<float[,]> TileValues = new();

        private Texture2D _texture;
        private Vector2 _origin;
        private Vector2 _centre;
        private bool _stale = true;

        private JobHandle _job;
        private bool _pending;
        private Vector2 _pendingOrigin;
        private Vector2 _pendingCentre;
        private NativeArray<TileHeights> _tiles;
        private NativeArray<float> _heights;
        private NativeArray<float> _coarseDepth;
        private NativeArray<byte> _coarseObstacle;
        private NativeArray<ushort> _pixels;

        public Texture2D Texture => _texture;
        /// <summary>True when the texture holds a window.</summary>
        public bool IsBuilt { get; private set; }
        /// <summary>Main thread time of the last start of a build, in milliseconds.</summary>
        public float ScheduleMilliseconds { get; private set; }

        /// <summary>Maps world XZ to texture UV: uv = (world - xy) * zw. Zero when the window is not built.</summary>
        public Vector4 UVRect => IsBuilt
            ? new Vector4(_origin.x, _origin.y, 1f / (Size * Texel), 1f / (Size * Texel))
            : Vector4.zero;

        /// <summary>True when no build runs and the window is old or too far from the camera.</summary>
        public bool NeedsBuild(Vector3 camera)
        {
            if (_pending) return false;

            return _stale || !IsBuilt ||
                (new Vector2(camera.x, camera.z) - _centre).sqrMagnitude > MoveDistance * MoveDistance;
        }

        /// <summary>The next camera builds the window again, for example after the shore changed.</summary>
        public void Invalidate()
        {
            _stale = true;
        }

        /// <summary>Stops using the window. The texture stays for the next build.</summary>
        public void Clear()
        {
            Complete(false);
            IsBuilt = false;
            _stale = true;
        }

        /// <summary>
        /// Starts a build of the window around a camera from the ground and the shore texture of the body. The body has
        /// one water level (Area shape).
        /// </summary>
        /// <returns>False when a build runs or the shore texture or the terrain is missing.</returns>
        public bool Schedule(WaterShoreField shore, float level, Vector3 camera)
        {
            float start = Time.realtimeSinceStartup;
            Texture2D coarse = shore.Texture;
            if (_pending || !shore.IsBuilt || !coarse || !coarse.isReadable) return false;

            var centre = new Vector2(Mathf.Round(camera.x / Snap) * Snap, Mathf.Round(camera.z / Snap) * Snap);
            Vector2 origin = centre - Vector2.one * (Size * Texel * 0.5f);
            var window = new Rect(origin, Vector2.one * (Size * Texel));
            if (!WaterShoreField.CollectTerrains(window)) return false;

            ReadHeights(window);
            Vector4 coarseRect = shore.UVRect;
            ReadCoarse(shore, coarse, coarseRect, window, out int firstColumn, out int firstRow, out int coarseWidth,
                out int coarseHeight);
            _pixels = new NativeArray<ushort>(Size * Size * 4, Allocator.Persistent);
            var job = new BuildJob
            {
                Tiles = _tiles,
                Heights = _heights,
                CoarseDepth = _coarseDepth,
                CoarseObstacle = _coarseObstacle,
                CoarseWidth = coarseWidth,
                CoarseHeight = coarseHeight,
                CoarseOrigin = new float2(coarseRect.x, coarseRect.y) +
                    new float2(firstColumn, firstRow) * shore.TexelSize,
                CoarseTexel = shore.TexelSize,
                Origin = origin,
                Level = level,
                Pixels = _pixels
            };
            _job = job.Schedule();
            JobHandle.ScheduleBatchedJobs();
            _pending = true;
            _pendingOrigin = origin;
            _pendingCentre = centre;
            ScheduleMilliseconds = (Time.realtimeSinceStartup - start) * 1000f;
            return true;
        }

        /// <summary>Puts the result of a finished build in the texture.</summary>
        /// <returns>True when the texture and its window changed.</returns>
        public bool TryComplete()
        {
            if (!_pending || !_job.IsCompleted) return false;

            return Complete(true);
        }

        /// <summary>Waits for a running build and destroys the texture.</summary>
        public void Release()
        {
            Complete(false);
            IsBuilt = false;
            _stale = true;
            if (!_texture) return;

            if (Application.isPlaying)
            {
                Object.Destroy(_texture);
            }
            else
            {
                Object.DestroyImmediate(_texture);
            }
            _texture = null;
        }

        private bool Complete(bool apply)
        {
            if (!_pending) return false;

            _job.Complete();
            _pending = false;
            if (apply)
            {
                if (!_texture)
                {
                    _texture = new Texture2D(Size, Size, TextureFormat.RGBAHalf, false, true)
                    {
                        name = "Looga Water Near Shore", hideFlags = HideFlags.DontSave,
                        wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
                    };
                }
                _texture.SetPixelData(_pixels, 0);
                _texture.Apply(false, false);
                _origin = _pendingOrigin;
                _centre = _pendingCentre;
                _stale = false;
                IsBuilt = true;
            }
            _tiles.Dispose();
            _heights.Dispose();
            _coarseDepth.Dispose();
            _coarseObstacle.Dispose();
            _pixels.Dispose();
            return apply;
        }

        // Heights of the terrains under the window, smallest terrain first, with one sample of margin.
        private void ReadHeights(Rect window)
        {
            TileList.Clear();
            TileValues.Clear();
            int total = 0;
            foreach (UnityEngine.Terrain terrain in WaterShoreField.CollectedTerrains)
            {
                TerrainData data = terrain.terrainData;
                Vector3 position = terrain.GetPosition();
                int resolution = data.heightmapResolution;
                var step = new Vector2(data.size.x / (resolution - 1), data.size.z / (resolution - 1));
                int firstX = Mathf.Clamp(Mathf.FloorToInt((window.xMin - position.x) / step.x) - 1, 0, resolution - 1);
                int firstZ = Mathf.Clamp(Mathf.FloorToInt((window.yMin - position.z) / step.y) - 1, 0, resolution - 1);
                int lastX = Mathf.Clamp(Mathf.CeilToInt((window.xMax - position.x) / step.x) + 1, 0, resolution - 1);
                int lastZ = Mathf.Clamp(Mathf.CeilToInt((window.yMax - position.z) / step.y) + 1, 0, resolution - 1);
                if (lastX <= firstX || lastZ <= firstZ) continue;

                int width = lastX - firstX + 1;
                int height = lastZ - firstZ + 1;
                TileValues.Add(data.GetHeights(firstX, firstZ, width, height));
                TileList.Add(new TileHeights
                {
                    Origin = new float2(position.x + firstX * step.x, position.z + firstZ * step.y),
                    Step = new float2(step.x, step.y),
                    Width = width,
                    Height = height,
                    Offset = total,
                    Scale = data.size.y,
                    Base = position.y
                });
                total += width * height;
            }
            _tiles = new NativeArray<TileHeights>(TileList.Count, Allocator.Persistent);
            _heights = new NativeArray<float>(Mathf.Max(total, 1), Allocator.Persistent);
            for (int tile = 0; tile < TileList.Count; tile++)
            {
                _tiles[tile] = TileList[tile];
                float[,] values = TileValues[tile];
                int width = TileList[tile].Width;
                int offset = TileList[tile].Offset;
                // GetHeights returns [z, x].
                for (int z = 0; z < TileList[tile].Height; z++)
                {
                    for (int x = 0; x < width; x++) _heights[offset + z * width + x] = values[z, x];
                }
            }
            TileValues.Clear();
        }

        // Depth (-1 outside the surface mesh) and collider texels of the shore texture of the body under the window.
        private void ReadCoarse(WaterShoreField shore, Texture2D coarse, Vector4 coarseRect, Rect window,
            out int firstColumn, out int firstRow, out int width, out int height)
        {
            float texel = shore.TexelSize;
            firstColumn = Mathf.Clamp(Mathf.FloorToInt((window.xMin - coarseRect.x) / texel), 0, coarse.width - 1);
            firstRow = Mathf.Clamp(Mathf.FloorToInt((window.yMin - coarseRect.y) / texel), 0, coarse.height - 1);
            int lastColumn = Mathf.Clamp(Mathf.CeilToInt((window.xMax - coarseRect.x) / texel), 0, coarse.width - 1);
            int lastRow = Mathf.Clamp(Mathf.CeilToInt((window.yMax - coarseRect.y) / texel), 0, coarse.height - 1);
            width = lastColumn - firstColumn + 1;
            height = lastRow - firstRow + 1;
            Color[] block = coarse.GetPixels(firstColumn, firstRow, width, height);
            _coarseDepth = new NativeArray<float>(block.Length, Allocator.Persistent);
            _coarseObstacle = new NativeArray<byte>(block.Length, Allocator.Persistent);
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    _coarseDepth[index] = block[index].g;
                    var point = new Vector2(coarseRect.x + (firstColumn + column + 0.5f) * texel,
                        coarseRect.y + (firstRow + row + 0.5f) * texel);
                    bool obstacle = shore.IsObstacle(point);
                    _coarseObstacle[index] = (byte)(obstacle ? 1 : 0);
                }
            }
        }

        private struct TileHeights
        {
            public float2 Origin;
            public float2 Step;
            public int Width;
            public int Height;
            public int Offset;
            // Height of the value 1, and the world height of the value 0.
            public float Scale;
            public float Base;
        }

        [BurstCompile]
        private struct BuildJob : IJob
        {
            [ReadOnly] public NativeArray<TileHeights> Tiles;
            [ReadOnly] public NativeArray<float> Heights;
            [ReadOnly] public NativeArray<float> CoarseDepth;
            [ReadOnly] public NativeArray<byte> CoarseObstacle;
            public int CoarseWidth;
            public int CoarseHeight;
            public float2 CoarseOrigin;
            public float CoarseTexel;
            public float2 Origin;
            public float Level;
            public NativeArray<ushort> Pixels;

            public void Execute()
            {
                const int count = Size * Size;
                var dry = new NativeArray<bool>(count, Allocator.Temp);
                var depth = new NativeArray<float>(count, Allocator.Temp);
                for (int row = 0; row < Size; row++)
                {
                    for (int column = 0; column < Size; column++)
                    {
                        int index = row * Size + column;
                        float2 point = Origin + new float2(column + 0.5f, row + 0.5f) * Texel;
                        // The shore texture of the body tells where its surface mesh is (depth -1 outside it) and where
                        // a collider goes through the surface. The fine ground decides the water edge everywhere, so
                        // the edge does not follow the texels of the shore texture.
                        var cell = (int2)math.floor((point - CoarseOrigin) / CoarseTexel);
                        bool covered = cell.x >= 0 && cell.y >= 0 && cell.x < CoarseWidth && cell.y < CoarseHeight;
                        int coarseIndex = covered ? cell.y * CoarseWidth + cell.x : 0;
                        bool outside = covered && CoarseDepth[coarseIndex] < -0.5f;
                        bool obstacle = covered && !outside && CoarseObstacle[coarseIndex] != 0;
                        float waterDepth = TrySampleGround(point, out float ground)
                            ? Level - ground
                            : covered ? CoarseDepth[coarseIndex] : 0f;
                        bool isDry = obstacle || waterDepth <= 0f;
                        // Dry ground outside the surface keeps depth -1, so the shader knows the outline there.
                        depth[index] = isDry ? (outside ? OutsideDepth : 0f) : waterDepth;
                        dry[index] = isDry;
                    }
                }

                var toDry = new NativeArray<float>(count, Allocator.Temp);
                var toWet = new NativeArray<float>(count, Allocator.Temp);
                DistanceTo(dry, true, toDry);
                DistanceTo(dry, false, toWet);
                for (int index = 0; index < count; index++)
                {
                    // The edge is between a dry texel and a wet one, as in the shore texture of the body.
                    float metres = dry[index]
                        ? -math.max(math.sqrt(toWet[index]) - 0.5f, 0f) * Texel
                        : math.max(math.sqrt(toDry[index]) - 0.5f, 0f) * Texel;
                    metres = math.clamp(metres, -MaximumDistance, MaximumDistance);
                    Pixels[index * 4] = (ushort)math.f32tof16(metres);
                    Pixels[index * 4 + 1] = (ushort)math.f32tof16(depth[index]);
                    Pixels[index * 4 + 2] = 0;
                    Pixels[index * 4 + 3] = 0;
                }
            }

            // Bilinear height from the smallest terrain that covers the point.
            private bool TrySampleGround(float2 point, out float height)
            {
                for (int tile = 0; tile < Tiles.Length; tile++)
                {
                    TileHeights heights = Tiles[tile];
                    float2 local = (point - heights.Origin) / heights.Step;
                    if (local.x < 0f || local.y < 0f || local.x > heights.Width - 1 || local.y > heights.Height - 1)
                    {
                        continue;
                    }
                    int2 cell = math.min((int2)math.floor(local), new int2(heights.Width - 2, heights.Height - 2));
                    float2 f = local - cell;
                    int index = heights.Offset + cell.y * heights.Width + cell.x;
                    float bottom = math.lerp(Heights[index], Heights[index + 1], f.x);
                    float top = math.lerp(Heights[index + heights.Width], Heights[index + heights.Width + 1], f.x);
                    height = math.lerp(bottom, top, f.y) * heights.Scale + heights.Base;
                    return true;
                }
                height = 0f;
                return false;
            }

            // Squared distance in texels to the nearest texel whose dry value is the target. The same transform as the
            // shore texture of the body (Felzenszwalb and Huttenlocher).
            private static void DistanceTo(NativeArray<bool> dry, bool target, NativeArray<float> result)
            {
                const float infinity = 1e20f;
                for (int index = 0; index < result.Length; index++) result[index] = dry[index] == target ? 0f : infinity;
                var line = new NativeArray<float>(Size, Allocator.Temp);
                var output = new NativeArray<float>(Size, Allocator.Temp);
                var hull = new NativeArray<int>(Size, Allocator.Temp);
                var bounds = new NativeArray<float>(Size + 1, Allocator.Temp);
                for (int column = 0; column < Size; column++)
                {
                    for (int row = 0; row < Size; row++) line[row] = result[row * Size + column];
                    Transform1D(line, output, hull, bounds);
                    for (int row = 0; row < Size; row++) result[row * Size + column] = output[row];
                }
                for (int row = 0; row < Size; row++)
                {
                    for (int column = 0; column < Size; column++) line[column] = result[row * Size + column];
                    Transform1D(line, output, hull, bounds);
                    for (int column = 0; column < Size; column++) result[row * Size + column] = output[column];
                }
            }

            // Lower envelope of the parabolas at each sample: output[q] = min over p of (q - p)^2 + values[p].
            private static void Transform1D(NativeArray<float> values, NativeArray<float> output, NativeArray<int> hull,
                NativeArray<float> bounds)
            {
                const float infinity = 1e20f;
                int k = 0;
                hull[0] = 0;
                bounds[0] = -infinity;
                bounds[1] = infinity;
                for (int q = 1; q < Size; q++)
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
                        bounds[1] = infinity;
                        continue;
                    }
                    k++;
                    hull[k] = q;
                    bounds[k] = s;
                    bounds[k + 1] = infinity;
                }
                k = 0;
                for (int q = 0; q < Size; q++)
                {
                    while (bounds[k + 1] < q) k++;
                    int p = hull[k];
                    output[q] = (q - p) * (q - p) + values[p];
                }
            }
        }
    }
}
