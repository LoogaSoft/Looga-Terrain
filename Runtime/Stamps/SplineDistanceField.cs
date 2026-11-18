using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Builds spline distance fields for one tile on the GPU. Each spline is a full-screen blit from the previous field
    /// into the next, like MicroVerse, so texel positions match MicroVerse exactly. See LoogaSplineDistance.shader for
    /// the channels. GetPath and GetArea keep compact copies in a cache with a memory budget. The caller must
    /// invalidate an owner after its splines or settings change.
    /// </summary>
    /// <remarks>
    /// Like MicroVerse, a field coarser than the terrain alphamap (at most 2048) is also rendered at the alphamap
    /// resolution: interior texels copy the coarse field, texels near three tile edges are computed again. The fine
    /// field is then scaled down to the field resolution.
    /// </remarks>
    internal sealed class SplineDistanceField : IDisposable
    {
        private const string ShaderName = "LoogaSplineDistance";
        // MicroVerse spline areas search 128 points on each curve.
        private const int AreaSegments = 128;
        private const float NotFound = 99999f;
        private const int DistancePass = 0;
        private const int CopyPathPass = 1;
        private const int CopyAreaPass = 2;
        private const int BandPass = 3;
        private const int BandOuterSpline = 0;
        private const int BandInnerSpline = 1;

        private static readonly int SampleSpanId = Shader.PropertyToID("_SampleSpan");
        private static readonly int TileToWorldId = Shader.PropertyToID("_TileToWorld");
        private static readonly int CurvesId = Shader.PropertyToID("_Curves");
        private static readonly int CurveCountId = Shader.PropertyToID("_CurveCount");
        private static readonly int CurveIndicesId = Shader.PropertyToID("_CurveIndices");
        private static readonly int SegmentsId = Shader.PropertyToID("_Segments");
        private static readonly int SearchLimitId = Shader.PropertyToID("_SearchLimit");
        private static readonly int SplineBoundsId = Shader.PropertyToID("_SplineBounds");
        private static readonly int AreaModeId = Shader.PropertyToID("_AreaMode");
        private static readonly int WidthsId = Shader.PropertyToID("_Widths");
        private static readonly int WidthCountId = Shader.PropertyToID("_WidthCount");
        private static readonly int WidthEasingId = Shader.PropertyToID("_WidthEasing");
        private static readonly int WidthNoiseTypeId = Shader.PropertyToID("_WidthNoiseType");
        private static readonly int WidthNoiseParamsId = Shader.PropertyToID("_WidthNoiseParams");
        private static readonly int CoarseId = Shader.PropertyToID("_Coarse");
        private static readonly int EdgePassId = Shader.PropertyToID("_EdgePass");
        private static readonly int OverlapBlendId = Shader.PropertyToID("_OverlapBlend");
        private static readonly int BlendInnerId = Shader.PropertyToID("_BlendInner");
        private static readonly int BlendOuterId = Shader.PropertyToID("_BlendOuter");
        private static readonly int SquareEndsId = Shader.PropertyToID("_SquareEnds");
        private static readonly int LastCurveId = Shader.PropertyToID("_LastCurve");
        private static readonly int BandOuterId = Shader.PropertyToID("_BandOuter");
        private const int MaximumFineResolution = 2048;
        // Segments of one chunk. Must agree with CHUNK_SEGMENTS in LoogaSplineDistance.shader.
        private const int ChunkSegments = 32;
        // Margin of the chunk boxes: the GPU curve points can differ from the CPU points by rounding.
        private const float ChunkMargin = 0.05f;
        private static readonly int ChunkBoxesId = Shader.PropertyToID("_ChunkBoxes");
        private static readonly int ChunkCountId = Shader.PropertyToID("_ChunkCount");

        private readonly Material _material;
        private readonly List<WorldCurve> _curves = new();
        private readonly List<WorldCurve> _keptCurves = new();
        private readonly List<int> _curveIndices = new();
        private readonly List<Vector4> _chunkBoxes = new();
        private readonly List<Vector2> _widths = new();
        private RenderTexture _front;
        private RenderTexture _back;
        private RenderTexture _fineFront;
        private RenderTexture _fineBack;
        private bool _useFine;
        private GraphicsBuffer _curveBuffer;
        private GraphicsBuffer _curveIndexBuffer;
        private GraphicsBuffer _chunkBoxBuffer;
        private GraphicsBuffer _widthBuffer;
        private Rect _tileRect;
        private readonly TileFieldCache _cache = new(1024L * 1024 * 1024);

        /// <summary>Cache memory limit in bytes. The least recently used fields go first.</summary>
        internal long CacheBudget
        {
            get => _cache.Budget;
            set => _cache.Budget = value;
        }

        /// <summary>GPU memory of the cached fields, in bytes.</summary>
        internal long CachedBytes => _cache.CachedBytes;

        /// <summary>Fields built since the last ResetStatistics call, not taken from the cache.</summary>
        internal int BuildCount => _cache.BuildCount;

        /// <summary>Fields taken from the cache since the last ResetStatistics call.</summary>
        internal int CacheHits => _cache.CacheHits;

        internal SplineDistanceField()
        {
            var shader = Resources.Load<Shader>(ShaderName);
            if (!shader)
            {
                throw new NotSupportedException("Looga spline distance fields require the " + ShaderName + " shader.");
            }
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public void Dispose()
        {
            ClearCache();
            Release(ref _front);
            Release(ref _back);
            Release(ref _fineFront);
            Release(ref _fineBack);
            _curveBuffer?.Dispose();
            _curveBuffer = null;
            _curveIndexBuffer?.Dispose();
            _curveIndexBuffer = null;
            _chunkBoxBuffer?.Dispose();
            _chunkBoxBuffer = null;
            _widthBuffer?.Dispose();
            _widthBuffer = null;
            UnityEngine.Object.DestroyImmediate(_material);
        }

        internal void ResetStatistics()
        {
            _cache.ResetStatistics();
        }

        /// <summary>
        /// Releases the build textures. The cached fields stay. The next build creates the textures again and clears
        /// them, so the result does not change.
        /// </summary>
        /// <returns>The GPU memory released, in bytes.</returns>
        internal long ReleaseScratch()
        {
            long bytes = GetBytes(_front) + GetBytes(_back) + GetBytes(_fineFront) + GetBytes(_fineBack);
            Release(ref _front);
            Release(ref _back);
            Release(ref _fineFront);
            Release(ref _fineBack);
            return bytes;
        }

        // The build textures use R32G32B32A32_SFloat: 16 bytes for each texel.
        private static long GetBytes(RenderTexture texture)
        {
            return texture ? 16L * texture.width * texture.height : 0;
        }

        /// <summary>Cached path field for one tile: x = distance outside the width, y = height.</summary>
        internal Texture GetPath(in StampTile tile, LoogaSplinePath path)
        {
            if (_cache.TryGet(path, tile, out RenderTexture cached)) return cached;
            using (StampSections.Measure(StampSection.FieldBuild))
            {
                RenderTexture field = BuildPath(tile, path);
                return Store(path, tile, field, GraphicsFormat.R32G32_SFloat, CopyPathPass);
            }
        }

        /// <summary>Cached area field for one tile: x = signed distance, or the band value of a band area.</summary>
        internal Texture GetArea(in StampTile tile, LoogaSplineArea area)
        {
            if (_cache.TryGet(area, tile, out RenderTexture cached)) return cached;
            using (StampSections.Measure(StampSection.FieldBuild))
            {
                if (area.Band) return BuildBand(tile, area);

                RenderTexture field = BuildArea(tile, area);
                return Store(area, tile, field, GraphicsFormat.R32_SFloat, CopyAreaPass);
            }
        }

        // The build textures hold one field, so the outer edge goes to a copy before the inner edge builds.
        private RenderTexture BuildBand(in StampTile tile, LoogaSplineArea area)
        {
            RenderTexture outerField = BuildArea(tile, area, BandOuterSpline);
            RenderTexture outer = RenderTexture.GetTemporary(outerField.width, outerField.height, 0,
                GraphicsFormat.R32_SFloat);
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(outerField, outer, _material, CopyAreaPass);
            RenderTexture.active = previous;
            RenderTexture innerField = BuildArea(tile, area, BandInnerSpline);
            _material.SetTexture(BandOuterId, outer);
            RenderTexture band = Store(area, tile, innerField, GraphicsFormat.R32_SFloat, BandPass);
            _material.SetTexture(BandOuterId, null);
            RenderTexture.ReleaseTemporary(outer);
            return band;
        }

        /// <summary>Removes the cached fields of one owner on the tiles that a world rectangle touches.</summary>
        internal void Invalidate(UnityEngine.Object owner, Rect? region = null)
        {
            _cache.Invalidate(owner, region);
        }

        internal void ClearCache()
        {
            _cache.Clear();
        }

        private RenderTexture Store(UnityEngine.Object owner, in StampTile tile, RenderTexture field,
            GraphicsFormat format, int copyPass)
        {
            var descriptor = new RenderTextureDescriptor(field.width, field.height, format, 0);
            var compact = new RenderTexture(descriptor)
            {
                name = "Looga Stamps Cached Distance " + owner.name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            compact.Create();
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(field, compact, _material, copyPass);
            RenderTexture.active = previous;
            _cache.Add(owner, tile, compact);
            return compact;
        }

        /// <summary>Signed distance field of a spline area for one tile. Zero or more: only the spline at that index.</summary>
        internal RenderTexture BuildArea(in StampTile tile, LoogaSplineArea area, int onlySpline = -1)
        {
            Begin(tile, area.Resolution);
            _material.SetFloat(OverlapBlendId, 0f);
            _material.SetInteger(SquareEndsId, 0);
            SplineContainer container = area.Spline;
            if (!container) return _front;
            Matrix4x4 localToWorld = container.transform.localToWorldMatrix;
            // The first spline decides the mode for all splines of the container (MicroVerse behavior).
            bool areaMode = container.Spline != null && container.Spline.Closed && !area.ClosedAsPath;
            IReadOnlyList<Spline> splines = container.Splines;
            for (int i = 0; i < splines.Count; i++)
            {
                if (onlySpline >= 0 && i != onlySpline) continue;

                AddSpline(splines[i], localToWorld, AreaSegments, area.MaxDistance, areaMode, null,
                    FalloffEasing.Linear, null);
            }
            return Finish();
        }

        /// <summary>Distance field of a spline path for one tile, with its widths and width noise.</summary>
        internal RenderTexture BuildPath(in StampTile tile, LoogaSplinePath path)
        {
            Begin(tile, path.Resolution);
            // A spline weighs fully on its surface (the path width) and fades out over the embankment. The copy pass
            // that follows the build reads the same blend setting.
            _material.SetFloat(OverlapBlendId, path.OverlapBlend);
            _material.SetFloat(BlendInnerId, path.Width + path.FillShoulder);
            _material.SetFloat(BlendOuterId, path.Width + path.FillShoulder + Mathf.Max(path.Smoothness, 0.01f));
            _material.SetInteger(SquareEndsId, path.SquareEnds ? 1 : 0);
            SplineContainer container = path.Spline;
            if (!container) return _front;
            Matrix4x4 localToWorld = container.transform.localToWorldMatrix;
            IReadOnlyList<Spline> splines = container.Splines;
            for (int i = 0; i < splines.Count; i++)
            {
                LoogaSplinePath.SplineWidths widths = i < path.Widths.Count ? path.Widths[i] : null;
                AddSpline(splines[i], localToWorld, path.SegmentsPerCurve, path.SearchDistance, false,
                    widths, path.WidthEasing, path.WidthNoise, path.SquareEnds);
            }
            return Finish();
        }

        private void Begin(in StampTile tile, int resolution)
        {
            EnsureTexture(ref _front, resolution);
            EnsureTexture(ref _back, resolution);
            ClearToNotFound(_front);
            ClearToNotFound(_back);
            int fineResolution = Mathf.Min(MaximumFineResolution,
                Mathf.Max(tile.Terrain.terrainData.alphamapResolution, resolution));
            _useFine = fineResolution != resolution;
            if (_useFine)
            {
                EnsureTexture(ref _fineFront, fineResolution);
                EnsureTexture(ref _fineBack, fineResolution);
                ClearToNotFound(_fineFront);
                ClearToNotFound(_fineBack);
            }
            _tileRect = tile.WorldRect;
            _material.SetVector(SampleSpanId, tile.SampleSpan);
            _material.SetMatrix(TileToWorldId, tile.Terrain.transform.localToWorldMatrix);
        }

        private void AddSpline(Spline spline, Matrix4x4 localToWorld, int segments, float searchDistance, bool areaMode,
            LoogaSplinePath.SplineWidths widths, FalloffEasing widthEasing, StampNoiseSettings widthNoise,
            bool squareEnds = false)
        {
            if (spline.Count < 2) return;
            SplineGeometry.GetWorldCurves(spline, localToWorld, _curves);
            // An open spline gets a last curve of zero length at its last knot. It adds no distance, but square ends
            // need the last real curve, which has the end direction.
            if (squareEnds && !spline.Closed) _curves.RemoveAt(_curves.Count - 1);
            _widths.Clear();
            float maximumWidth = 0f;
            if (widths != null)
            {
                foreach (Vector2 point in widths.Points)
                {
                    _widths.Add(point);
                    maximumWidth = Mathf.Max(maximumWidth, point.y);
                }
            }

            float searchLimit = (searchDistance + maximumWidth) * 2f;
            Bounds bounds = spline.GetBounds((float4x4)localToWorld);
            // Outside the spline bounds plus the search limit the pass keeps the previous field, so a spline that
            // cannot reach the tile needs no pass. A spline near the tile always gets its pass: inside its bounds it
            // writes "nothing found" distances, also without a kept curve.
            if (!Reaches(bounds.min.x, bounds.max.x, bounds.min.z, bounds.max.z, searchLimit)) return;
            KeepCurves(areaMode, searchLimit);
            _curveBuffer = EnsureBuffer(_curveBuffer, Mathf.Max(1, _keptCurves.Count), WorldCurve.Stride);
            _curveBuffer.SetData(_keptCurves);
            _curveIndexBuffer = EnsureBuffer(_curveIndexBuffer, Mathf.Max(1, _curveIndices.Count), sizeof(int));
            _curveIndexBuffer.SetData(_curveIndices);
            int chunkCount = AddChunkBoxes(Mathf.Max(2, segments));
            _chunkBoxBuffer = EnsureBuffer(_chunkBoxBuffer, Mathf.Max(1, _chunkBoxes.Count), 16);
            _chunkBoxBuffer.SetData(_chunkBoxes);
            _widthBuffer = EnsureBuffer(_widthBuffer, Mathf.Max(1, _widths.Count), 8);
            if (_widths.Count > 0)
            {
                _widthBuffer.SetData(_widths);
            }

            _material.SetBuffer(CurvesId, _curveBuffer);
            _material.SetBuffer(CurveIndicesId, _curveIndexBuffer);
            _material.SetBuffer(ChunkBoxesId, _chunkBoxBuffer);
            _material.SetInteger(ChunkCountId, chunkCount);
            _material.SetBuffer(WidthsId, _widthBuffer);
            _material.SetInteger(CurveCountId, _keptCurves.Count);
            _material.SetInteger(LastCurveId, _curves.Count - 1);
            _material.SetInteger(SegmentsId, Mathf.Max(2, segments));
            _material.SetFloat(SearchLimitId, searchLimit);
            _material.SetVector(SplineBoundsId, new Vector4(bounds.min.x, bounds.max.x, bounds.min.z, bounds.max.z));
            _material.SetInteger(AreaModeId, areaMode ? 1 : 0);
            _material.SetInteger(WidthCountId, _widths.Count);
            _material.SetInteger(WidthEasingId, (int)widthEasing);
            bool useNoise = widthNoise != null && widthNoise.Type != StampNoiseType.None && widthNoise.Amplitude != 0f;
            _material.SetInteger(WidthNoiseTypeId, useNoise ? 1 : 0);
            _material.SetVector(WidthNoiseParamsId, widthNoise != null ? widthNoise.Parameters : Vector4.zero);

            RenderTexture previous = RenderTexture.active;
            _material.SetInteger(EdgePassId, 0);
            Graphics.Blit(_front, _back, _material, DistancePass);
            (_front, _back) = (_back, _front);
            if (_useFine)
            {
                _material.SetInteger(EdgePassId, 1);
                _material.SetTexture(CoarseId, _front);
                Graphics.Blit(_fineFront, _fineBack, _material, DistancePass);
                (_fineFront, _fineBack) = (_fineBack, _fineFront);
            }
            RenderTexture.active = previous;
        }

        // A path field keeps only the curves that can reach the tile. The shader tests a curve only when the texel is
        // within the search limit of the curve control points, and a path does not count crossings, so the other
        // curves have no effect. The kept curves keep their spline index for the knot position. An area keeps all
        // curves: curves outside the tile still change the crossing parity.
        private void KeepCurves(bool areaMode, float searchLimit)
        {
            _keptCurves.Clear();
            _curveIndices.Clear();
            for (int i = 0; i < _curves.Count; i++)
            {
                WorldCurve curve = _curves[i];
                if (!areaMode)
                {
                    float minX = Mathf.Min(Mathf.Min(curve.P0.x, curve.P1.x), Mathf.Min(curve.P2.x, curve.P3.x));
                    float maxX = Mathf.Max(Mathf.Max(curve.P0.x, curve.P1.x), Mathf.Max(curve.P2.x, curve.P3.x));
                    float minZ = Mathf.Min(Mathf.Min(curve.P0.z, curve.P1.z), Mathf.Min(curve.P2.z, curve.P3.z));
                    float maxZ = Mathf.Max(Mathf.Max(curve.P0.z, curve.P1.z), Mathf.Max(curve.P2.z, curve.P3.z));
                    if (!Reaches(minX, maxX, minZ, maxZ, searchLimit)) continue;
                }
                _keptCurves.Add(curve);
                _curveIndices.Add(i);
            }
        }

        // True when a world box grown by the search limit touches the tile. Texel positions reach up to one texel past
        // the tile; the extra margin only keeps more.
        private bool Reaches(float minX, float maxX, float minZ, float maxZ, float searchLimit)
        {
            float margin = searchLimit + 0.01f * Mathf.Max(_tileRect.width, _tileRect.height) + 1f;
            return maxX >= _tileRect.xMin - margin && minX <= _tileRect.xMax + margin &&
                maxZ >= _tileRect.yMin - margin && minZ <= _tileRect.yMax + margin;
        }

        // Boxes of the curve points of each chunk of segments, for each kept curve. The shader skips a chunk when its
        // box is too far away. Chunk k holds the segments that end at points k * ChunkSegments + 1 to
        // (k + 1) * ChunkSegments. The box of a chunk is the box of the control points of its part of the curve, which
        // contains that part. Returns the number of chunks of each curve.
        private int AddChunkBoxes(int pointCount)
        {
            _chunkBoxes.Clear();
            int lastPoint = pointCount - 1;
            int chunkCount = (lastPoint - 1) / ChunkSegments + 1;
            foreach (WorldCurve curve in _keptCurves)
            {
                for (int k = 0; k < chunkCount; k++)
                {
                    double start = (double)(k * ChunkSegments) / lastPoint;
                    double end = (double)Mathf.Min(lastPoint, (k + 1) * ChunkSegments) / lastPoint;
                    Vector2 a = Blossom(curve, start, start, start), b = Blossom(curve, start, start, end);
                    Vector2 c = Blossom(curve, start, end, end), d = Blossom(curve, end, end, end);
                    _chunkBoxes.Add(new Vector4(
                        Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)) - ChunkMargin,
                        Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y)) - ChunkMargin,
                        Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x)) + ChunkMargin,
                        Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y)) + ChunkMargin));
                }
            }
            return chunkCount;
        }

        // Blossom of the cubic curve in XZ: de Casteljau steps at three parameters. (u, u, v) and similar give the
        // control points of the part of the curve from u to v.
        private static Vector2 Blossom(in WorldCurve curve, double u1, double u2, double u3)
        {
            double x01 = curve.P0.x + (curve.P1.x - curve.P0.x) * u1, z01 = curve.P0.z + (curve.P1.z - curve.P0.z) * u1;
            double x12 = curve.P1.x + (curve.P2.x - curve.P1.x) * u1, z12 = curve.P1.z + (curve.P2.z - curve.P1.z) * u1;
            double x23 = curve.P2.x + (curve.P3.x - curve.P2.x) * u1, z23 = curve.P2.z + (curve.P3.z - curve.P2.z) * u1;
            double x012 = x01 + (x12 - x01) * u2, z012 = z01 + (z12 - z01) * u2;
            double x123 = x12 + (x23 - x12) * u2, z123 = z12 + (z23 - z12) * u2;
            return new Vector2((float)(x012 + (x123 - x012) * u3), (float)(z012 + (z123 - z012) * u3));
        }

        // Scales the fine field down to the field resolution. Without a fine field the coarse field is the result.
        private RenderTexture Finish()
        {
            if (!_useFine) return _front;
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(_fineFront, _back);
            RenderTexture.active = previous;
            return _back;
        }

        private static void ClearToNotFound(RenderTexture texture)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            GL.Clear(false, true, new Color(NotFound, NotFound, NotFound, NotFound));
            RenderTexture.active = previous;
        }

        private static void EnsureTexture(ref RenderTexture texture, int resolution)
        {
            if (texture && texture.width == resolution) return;
            Release(ref texture);
            var descriptor = new RenderTextureDescriptor(resolution, resolution, GraphicsFormat.R32G32B32A32_SFloat, 0);
            texture = new RenderTexture(descriptor)
            {
                name = "Looga Stamps Spline Distance",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            texture.Create();
        }

        private static GraphicsBuffer EnsureBuffer(GraphicsBuffer buffer, int count, int stride)
        {
            if (buffer != null && buffer.count >= count) return buffer;
            buffer?.Dispose();
            return new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(count), stride);
        }

        private static void Release(ref RenderTexture texture)
        {
            if (texture)
            {
                texture.Release();
                UnityEngine.Object.DestroyImmediate(texture);
            }
            texture = null;
        }
    }
}
