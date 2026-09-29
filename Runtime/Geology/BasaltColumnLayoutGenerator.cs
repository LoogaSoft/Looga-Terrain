using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace LoogaSoft.Terrain.Geology
{
    [Serializable]
    public sealed class BasaltRangeDistribution
    {
        [SerializeField] private Vector2 _range;
        [SerializeField] private AnimationCurve _distribution;

        public Vector2 Range => _range;
        public AnimationCurve Distribution => _distribution;
        public float Minimum => Mathf.Min(_range.x, _range.y);
        public float Maximum => Mathf.Max(_range.x, _range.y);
        public float TypicalValue => (Mathf.Min(_range.x, _range.y) + Mathf.Max(_range.x, _range.y)) * 0.5f;

        public BasaltRangeDistribution(float minimum, float maximum, AnimationCurve distribution = null)
        {
            _range = new Vector2(minimum, maximum);
            _distribution = distribution ?? AnimationCurve.Linear(0f, 0f, 1f, 1f);
        }

        public float Sample(float unit)
        {
            float minimum = Mathf.Min(_range.x, _range.y);
            float maximum = Mathf.Max(_range.x, _range.y);
            float mapped = _distribution == null ? unit : _distribution.Evaluate(Mathf.Clamp01(unit));
            return Mathf.Lerp(minimum, maximum, Mathf.Clamp01(mapped));
        }

        public void Validate(string name, float absoluteMinimum)
        {
            if (!float.IsFinite(_range.x) || !float.IsFinite(_range.y) ||
                Mathf.Max(_range.x, _range.y) < absoluteMinimum)
            {
                throw new ArgumentOutOfRangeException(name);
            }
        }
    }

    public readonly struct BasaltSplineSample
    {
        public Vector3 Position { get; }
        public Vector3 Tangent { get; }
        public float Distance { get; }
        public float NormalizedDistance { get; }

        public BasaltSplineSample(Vector3 position, Vector3 tangent, float distance, float normalizedDistance)
        {
            Position = position;
            Tangent = tangent;
            Distance = distance;
            NormalizedDistance = normalizedDistance;
        }
    }

    public readonly struct BasaltColumnPlacement
    {
        public Matrix4x4 Matrix { get; }
        public int SideCount { get; }
        public int ShapeVariant { get; }
        public float NormalizedDistance { get; }
        public float Height { get; }
        /// <summary>Width of the pillar from corner to corner, in meters. This is twice the radius.</summary>
        public float Thickness { get; }
        public int Row { get; }
        public Vector4 ContactOcclusion { get; }

        public float Radius => Thickness * 0.5f;

        public BasaltColumnPlacement(Matrix4x4 matrix, int sideCount, int shapeVariant,
            float normalizedDistance, float height, float thickness, int row,
            Vector4 contactOcclusion = default)
        {
            Matrix = matrix;
            SideCount = sideCount;
            ShapeVariant = shapeVariant;
            NormalizedDistance = normalizedDistance;
            Height = height;
            Thickness = thickness;
            Row = row;
            ContactOcclusion = contactOcclusion;
        }

        public BasaltColumnPlacement WithContactOcclusion(Vector4 contactOcclusion) =>
            new(Matrix, SideCount, ShapeVariant, NormalizedDistance, Height, Thickness,
                Row, contactOcclusion);

        public BasaltColumnPlacement WithMatrixAndHeight(Matrix4x4 matrix, float height) =>
            new(matrix, SideCount, ShapeVariant, NormalizedDistance, height, Thickness,
                Row, ContactOcclusion);
    }

    /// <summary>
    /// Layout settings of a basalt pillar group along a spline.
    /// The pillar size is a radius range. Pillars can become smaller toward the outer edge and the ends of the group.
    /// </summary>
    [Serializable]
    public sealed class BasaltColumnLayoutSettings : ISerializationCallbackReceiver
    {
        private const int CurrentVersion = 1;

        [Min(0.1f), Tooltip("Distance that the pillar group extends outward from the spline, in meters.")]
        public float WallDepth = 45f;
        [Range(0.05f, 1f), Tooltip("Share of pillar positions that receive a pillar. 1 = a closed group.")]
        public float Density = 1f;
        [Range(-0.15f, 0.35f), Tooltip("Gap between neighbour pillars as a fraction of their size. " +
                                       "Negative values make neighbours overlap slightly.")]
        public float JointGap = -0.04f;
        [Min(0.1f), Tooltip("Minimum center-to-center distance used only to reject duplicate or nearly duplicate pillars.")]
        public float MinimumCenterSeparation = 0.5f;
        public BasaltRangeDistribution Height = new(100f, 200f,
            CreateUpperBiasedDistribution());
        [Tooltip("Pillar radius range, in meters, from the center to a corner. " +
                 "The size changes slowly across the group within this range.")]
        public Vector2 Radius = new(9f, 14f);
        [Range(0.1f, 1f), Tooltip("Radius multiplier at the outer edge and at the ends of the group. 1 = no change.")]
        public float EdgeRadiusScale = 0.55f;
        [Min(0f), Tooltip("Distance from the outer edge and the ends over which pillars grow to full size, in meters.")]
        public float EdgeWidth = 25f;
        [Range(0f, 0.5f), Tooltip("Random radius change of each pillar, as a fraction of its radius.")]
        public float RadiusJitter = 0.12f;
        [Min(0.0001f), Tooltip("Scale of the slow size change across the group. Smaller values give larger areas of similar size.")]
        public float RadiusNoiseScale = 0.012f;
        [Min(0.1f)] public float BottomDepth = 200f;
        public bool UseRelativeHeight = true;
        [Range(0.1f, 1f)] public float MinimumEscarpmentHeight = 0.6f;
        [Min(0f)] public float MaximumHeightAboveEscarpment = 50f;
        [Min(1f)] public float MaximumHeightScale = 1.15f;
        [Range(5, 8)] public int MinimumSides = 6;
        [Range(5, 8)] public int MaximumSides = 6;
        [Range(1, 16)] public int ShapeVariants = 6;
        [Min(0f)] public float TopVariation = 7f;
        [Range(0f, 8f)] public float LeanDegrees = 2.5f;
        [Range(0f, 1f)] public float BoundaryNoise = 0.35f;
        [Min(0.001f)] public float BoundaryNoiseScale = 0.004f;
        [Min(0f)] public float BoundaryJitter = 4f;
        [Range(0f, 1f)] public float OuterRowDropout = 0.28f;
        [Range(0.1f, 1f), Tooltip("Height multiplier of the outer rows. 1 = the outer rows have the full height.")]
        public float OuterRowHeightScale = 0.72f;
        [Tooltip("Places a fitted pillar in each gap that neighbour pillars enclose. Rows with different local radii " +
                 "leave such gaps.")]
        public bool FillGaps;
        [Min(0f)] public float EndTaperLength = 45f;
        [Range(0f, 1f)] public float EndHeightScale = 0.6f;
        [Range(0f, 1f)] public float EndDropout = 0.65f;
        [Range(0f, 1f), Tooltip("Density multiplier at the beginning of the spline.")]
        public float StartDensityScale = 1f;
        [Min(0f), Tooltip("Distance over which start density reaches the normal density.")]
        public float StartDensityFadeLength;
        [Range(0f, 1f), Tooltip("Density multiplier at the end of the spline.")]
        public float EndDensityScale = 1f;
        [Min(0f), Tooltip("Distance over which end density falls from normal density.")]
        public float EndDensityFadeLength;
        public bool ReverseOutward;
        public int Seed = 14731;

        // Version 0 stored the pillar width as a thickness range. Version 1 stores the radius.
        [SerializeField, HideInInspector, FormerlySerializedAs("Thickness")]
        private BasaltRangeDistribution _legacyThickness;
        [SerializeField, HideInInspector] private int _version;

        public float RadiusMinimum => Mathf.Max(0.05f, Mathf.Min(Radius.x, Radius.y));
        public float RadiusMaximum => Mathf.Max(RadiusMinimum, Mathf.Max(Radius.x, Radius.y));

        public static AnimationCurve CreateUpperBiasedDistribution() => new(
            new Keyframe(0f, 0f, 0f, 3.4f),
            new Keyframe(0.15f, 0.58f, 3.4f, 1.35f),
            new Keyframe(0.45f, 0.86f, 1.35f, 0.42f),
            new Keyframe(0.75f, 0.96f, 0.42f, 0.16f),
            new Keyframe(1f, 1f, 0.16f, 0f));

        public void OnBeforeSerialize()
        {
            _version = CurrentVersion;
        }

        // Older data stored the width from corner to corner. The radius is half of that width.
        public void OnAfterDeserialize()
        {
            if (_version < CurrentVersion && _legacyThickness != null && _legacyThickness.Maximum > 0f)
            {
                Radius = _legacyThickness.Range * 0.5f;
            }
            _legacyThickness = null;
            _version = CurrentVersion;
        }

        public void Validate()
        {
            if (!float.IsFinite(WallDepth) || WallDepth < 0f)
                throw new ArgumentOutOfRangeException(nameof(WallDepth));
            if (!float.IsFinite(BottomDepth) || BottomDepth <= 0f)
                throw new ArgumentOutOfRangeException(nameof(BottomDepth));
            if (!float.IsFinite(MinimumEscarpmentHeight) ||
                MinimumEscarpmentHeight <= 0f || MinimumEscarpmentHeight > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(MinimumEscarpmentHeight));
            }
            if (!float.IsFinite(MaximumHeightAboveEscarpment) ||
                MaximumHeightAboveEscarpment < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumHeightAboveEscarpment));
            }
            if (!float.IsFinite(MaximumHeightScale) || MaximumHeightScale < 1f)
                throw new ArgumentOutOfRangeException(nameof(MaximumHeightScale));
            if (!float.IsFinite(MinimumCenterSeparation) ||
                MinimumCenterSeparation < 0.1f)
                throw new ArgumentOutOfRangeException(nameof(MinimumCenterSeparation));
            if (!float.IsFinite(StartDensityFadeLength) || StartDensityFadeLength < 0f)
                throw new ArgumentOutOfRangeException(nameof(StartDensityFadeLength));
            if (!float.IsFinite(EndDensityFadeLength) || EndDensityFadeLength < 0f)
                throw new ArgumentOutOfRangeException(nameof(EndDensityFadeLength));
            if (!float.IsFinite(Radius.x) || !float.IsFinite(Radius.y) || Mathf.Max(Radius.x, Radius.y) < 0.05f)
                throw new ArgumentOutOfRangeException(nameof(Radius));
            if (!float.IsFinite(EdgeRadiusScale) || EdgeRadiusScale <= 0f || EdgeRadiusScale > 1f)
                throw new ArgumentOutOfRangeException(nameof(EdgeRadiusScale));
            if (!float.IsFinite(EdgeWidth) || EdgeWidth < 0f)
                throw new ArgumentOutOfRangeException(nameof(EdgeWidth));
            if (!float.IsFinite(RadiusJitter) || RadiusJitter < 0f || RadiusJitter > 0.5f)
                throw new ArgumentOutOfRangeException(nameof(RadiusJitter));
            if (!float.IsFinite(RadiusNoiseScale) || RadiusNoiseScale <= 0f)
                throw new ArgumentOutOfRangeException(nameof(RadiusNoiseScale));
            if (!float.IsFinite(OuterRowHeightScale) || OuterRowHeightScale <= 0f || OuterRowHeightScale > 1f)
                throw new ArgumentOutOfRangeException(nameof(OuterRowHeightScale));
            if (Height == null) throw new ArgumentNullException(nameof(Height));
            Height.Validate(nameof(Height), 0.1f);
            if (MinimumSides < 5 || MaximumSides > 8 || MinimumSides > MaximumSides)
                throw new ArgumentOutOfRangeException(nameof(MinimumSides));
            if (ShapeVariants < 1 || ShapeVariants > 16)
                throw new ArgumentOutOfRangeException(nameof(ShapeVariants));
        }
    }

    /// <summary>
    /// Places basalt pillars in rows along a spline. Each row follows the spline at a distance that depends on the
    /// pillar radius of the rows in front of it, so large and small pillars pack without large gaps.
    /// </summary>
    public static class BasaltColumnLayoutGenerator
    {
        // Center spacing of touching hexagonal pillars: along a row sqrt(3) x radius, between rows 1.5 x radius.
        private const float AlongSpacingFactor = 1.7320508f;
        private const float RowSpacingFactor = 1.5f;
        // Pillars closer than this share of their summed radii overlap too much and are rejected.
        private const float OverlapLimit = 0.55f;
        // The gap search treats this share of the corner radius as covered. A hexagon's inner radius is 0.866.
        private const float FootprintShare = 0.87f;
        // Each pass fills every enclosed gap once. Large gaps need more than one pillar.
        private const int FillPasses = 4;
        // Filler pillars use keys below the row keys, so their random values differ from the row pillars.
        private const int FillerKeyBase = int.MinValue;

        private struct SplineFrame
        {
            public Vector3 Position;
            public Vector3 Tangent;
            public float NormalizedDistance;
        }

        public static List<BasaltColumnPlacement> Generate(IReadOnlyList<BasaltSplineSample> samples,
            BasaltColumnLayoutSettings settings, Func<Vector3, float?> terrainHeight = null)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            List<BasaltColumnPlacement> result = new();
            if (samples.Count == 0) return result;

            float totalLength = samples[samples.Count - 1].Distance;
            float packing = Mathf.Max(0.5f, 1f + settings.JointGap);
            float smallest = settings.RadiusMinimum * settings.EdgeRadiusScale;
            float largest = settings.RadiusMaximum * (1f + settings.RadiusJitter);
            float deepest = settings.WallDepth * (1f + settings.BoundaryNoise);
            int maximumRows = Mathf.Max(1, Mathf.CeilToInt(deepest / (RowSpacingFactor * smallest * packing)) + 1);
            float cellSize = Mathf.Max(settings.MinimumCenterSeparation, largest * 2f * OverlapLimit);
            Dictionary<Vector2Int, List<int>> cells = new();

            for (int row = 0; row < maximumRows; row++)
            {
                int sampleIndex = 0;
                int column = 0;
                // Odd rows start half a pillar along the spline, so neighbour rows interlock.
                float phase = ((row & 1) * 0.5f + Signed(settings.Seed, row, 12) * 0.15f) *
                    AlongSpacingFactor * settings.RadiusMinimum * packing;
                float distance = Mathf.Max(0f, phase);
                while (distance <= totalLength)
                {
                    SplineFrame frame = Evaluate(samples, distance, ref sampleIndex);
                    float endDistance = Mathf.Min(distance, Mathf.Max(0f, totalLength - distance));
                    float boundaryNoise = FractalNoise(frame.Position.x, frame.Position.z,
                        settings.BoundaryNoiseScale, settings.Seed);
                    float depthLimit = settings.WallDepth * Mathf.Lerp(1f - settings.BoundaryNoise,
                        1f + settings.BoundaryNoise, boundaryNoise);
                    float baseRadius = Mathf.Lerp(settings.RadiusMinimum, settings.RadiusMaximum,
                        FractalNoise(frame.Position.x, frame.Position.z, settings.RadiusNoiseScale,
                            settings.Seed + 7919));
                    RowFrame(row, baseRadius, depthLimit, endDistance, packing, settings,
                        out float offset, out float rowRadius);
                    int key = unchecked(row * 104729 + column);
                    column++;
                    distance += Mathf.Max(0.2f, AlongSpacingFactor * rowRadius * packing);
                    if (frame.Tangent.sqrMagnitude < 0.0001f || offset > depthLimit) continue;

                    BasaltColumnPlacement? placement = Place(frame, settings, key, row, offset, depthLimit,
                        rowRadius, endDistance, distance, totalLength, terrainHeight);
                    if (!placement.HasValue) continue;

                    Vector3 basePosition = BasePosition(placement.Value);
                    if (Overlaps(result, cells, basePosition, placement.Value.Radius,
                            settings.MinimumCenterSeparation, cellSize))
                        continue;
                    result.Add(placement.Value);
                    AddToSeparationGrid(cells, result.Count - 1, basePosition, cellSize);
                }
            }

            if (settings.FillGaps) FillEnclosedGaps(result, samples, settings, terrainHeight);
            ApplyContactOcclusion(result);
            return result;
        }

        // The offset from the spline and the radius of one row. Each row sits behind the rows in front of it.
        private static void RowFrame(int row, float baseRadius, float depthLimit, float endDistance, float packing,
            BasaltColumnLayoutSettings settings, out float offset, out float radius)
        {
            offset = 0f;
            radius = EdgeRadius(baseRadius, depthLimit, endDistance, settings);
            for (int index = 0; index < row; index++)
            {
                float estimate = EdgeRadius(baseRadius,
                    depthLimit - (offset + RowSpacingFactor * radius * packing), endDistance, settings);
                offset += 0.5f * RowSpacingFactor * (radius + estimate) * packing;
                radius = EdgeRadius(baseRadius, depthLimit - offset, endDistance, settings);
            }
        }

        private static float EdgeRadius(float baseRadius, float outerDistance, float endDistance,
            BasaltColumnLayoutSettings settings)
        {
            if (settings.EdgeWidth <= 0f) return baseRadius;

            float edge = Smooth01(Mathf.Min(Mathf.Max(outerDistance, 0f), endDistance) / settings.EdgeWidth);
            return baseRadius * Mathf.Lerp(settings.EdgeRadiusScale, 1f, edge);
        }

        private static BasaltColumnPlacement? Place(SplineFrame frame, BasaltColumnLayoutSettings settings, int key,
            int row, float offset, float depthLimit, float rowRadius, float endDistance, float distance,
            float totalLength, Func<Vector3, float?> terrainHeight)
        {
            Vector3 tangent = frame.Tangent;
            Vector3 outward = Vector3.Cross(Vector3.up, tangent) * (settings.ReverseOutward ? -1f : 1f);
            float endFactor = settings.EndTaperLength <= 0f
                ? 1f
                : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(endDistance / settings.EndTaperLength));
            float startDensity = settings.StartDensityFadeLength <= 0f
                ? 1f
                : Mathf.Lerp(settings.StartDensityScale, 1f, Smooth01(distance / settings.StartDensityFadeLength));
            float endDensity = settings.EndDensityFadeLength <= 0f
                ? 1f
                : Mathf.Lerp(settings.EndDensityScale, 1f,
                    Smooth01(Mathf.Max(0f, totalLength - distance) / settings.EndDensityFadeLength));

            float outer = depthLimit <= 0.01f ? 0f : Mathf.Clamp01(offset / depthLimit);
            float outerEdgeFactor = Smooth01((outer - 0.55f) / 0.45f);
            float occupancy = settings.Density * startDensity * endDensity;
            occupancy *= 1f - settings.OuterRowDropout * outerEdgeFactor;
            occupancy *= 1f - settings.EndDropout * (1f - endFactor);
            if (Unit(settings.Seed, key, 0) > occupancy) return null;

            float radius = rowRadius * (1f + Signed(settings.Seed, key, 4) * settings.RadiusJitter);
            // Small position jitter breaks the regular joint pattern without opening gaps.
            float alongJitter = Signed(settings.Seed, key, 13) * 0.12f * radius;
            float acrossJitter = Signed(settings.Seed, key, 14) * 0.1f * radius +
                Signed(settings.Seed, key, 2) * settings.BoundaryJitter * outerEdgeFactor;
            Vector3 position = frame.Position + tangent * alongJitter + outward * (offset + acrossJitter);

            return BuildPlacement(position, tangent, frame.Position.y, radius, key, row, frame.NormalizedDistance,
                endFactor, outerEdgeFactor, settings, terrainHeight);
        }

        // Builds one pillar at a position. The top is the spline height of the pillar's spline frame.
        private static BasaltColumnPlacement? BuildPlacement(Vector3 position, Vector3 tangent, float top, float radius,
            int key, int row, float normalizedDistance, float endFactor, float outerEdgeFactor,
            BasaltColumnLayoutSettings settings, Func<Vector3, float?> terrainHeight)
        {
            float baseHeight = top - settings.BottomDepth;
            float? surface = terrainHeight?.Invoke(position);
            if (surface.HasValue && float.IsFinite(surface.Value) &&
                surface.Value > baseHeight && surface.Value < top)
            {
                baseHeight = surface.Value;
            }

            float height = SampleHeight(settings, key, top, baseHeight, endFactor, outerEdgeFactor);
            if (height < 0.5f) return null;

            int sideCount = settings.MinimumSides + Mathf.FloorToInt(Unit(settings.Seed, key, 6) *
                (settings.MaximumSides - settings.MinimumSides + 1));
            sideCount = Mathf.Clamp(sideCount, settings.MinimumSides, settings.MaximumSides);
            int variant = Mathf.Min(settings.ShapeVariants - 1,
                Mathf.FloorToInt(Unit(settings.Seed, key, 7) * settings.ShapeVariants));
            float leanX = Signed(settings.Seed, key, 9) * settings.LeanDegrees;
            float leanZ = Signed(settings.Seed, key, 10) * settings.LeanDegrees;
            Quaternion latticeRotation = Quaternion.LookRotation(tangent, Vector3.up) *
                Quaternion.Euler(0f, Unit(settings.Seed, key, 15) * 60f, 0f);
            Quaternion lean = Quaternion.Euler(leanX, 0f, leanZ);
            Vector3 basePosition = new(position.x, baseHeight, position.z);
            float thickness = radius * 2f;
            Matrix4x4 matrix = Matrix4x4.TRS(basePosition, latticeRotation * lean,
                new Vector3(thickness, height, thickness)) * Matrix4x4.Translate(Vector3.up * 0.5f);
            return new BasaltColumnPlacement(matrix, sideCount, variant, normalizedDistance, height,
                thickness, row);
        }

        // Finds the gaps that pillars enclose and places the largest pillar that fits in each one.
        // A filler takes the radius of the free circle at the gap center, grown by the joint overlap.
        private static void FillEnclosedGaps(List<BasaltColumnPlacement> placements,
            IReadOnlyList<BasaltSplineSample> samples, BasaltColumnLayoutSettings settings,
            Func<Vector3, float?> terrainHeight)
        {
            if (placements.Count < 3) return;
            float smallest = settings.RadiusMinimum * settings.EdgeRadiusScale;
            float cellSize = Mathf.Max(0.25f, smallest * 0.25f);
            // A gap smaller than a circle of half the smallest radius is a joint between pillars. An enclosed area
            // larger than a circle of twice the largest radius is open ground inside a curved group, not a gap.
            float minimumArea = Mathf.PI * 0.25f * smallest * smallest;
            float maximumArea = Mathf.PI * 4f * settings.RadiusMaximum * settings.RadiusMaximum;
            float growth = Mathf.Max(1f, 1f - settings.JointGap);
            for (int pass = 0; pass < FillPasses; pass++)
            {
                List<Vector2> centers = FindEnclosedGapCenters(placements, cellSize, minimumArea, maximumArea);
                if (centers.Count == 0) return;
                for (int index = 0; index < centers.Count; index++)
                {
                    Vector2 center = centers[index];
                    float radius = Mathf.Min(settings.RadiusMaximum,
                        Clearance(placements, center) / FootprintShare * growth);
                    if (radius < smallest * 0.5f) continue;

                    BasaltSplineSample nearest = NearestSample(samples, center);
                    Vector3 tangent = nearest.Tangent;
                    tangent.y = 0f;
                    tangent = tangent.sqrMagnitude > 0.0001f ? tangent.normalized : Vector3.forward;
                    int key = unchecked(FillerKeyBase + pass * 65536 + index);
                    BasaltColumnPlacement? filler = BuildPlacement(new Vector3(center.x, 0f, center.y), tangent,
                        nearest.Position.y, radius, key, -1, nearest.NormalizedDistance, 1f, 0f, settings,
                        terrainHeight);
                    if (filler.HasValue) placements.Add(filler.Value);
                }
            }
        }

        // Rasterizes the pillar footprints. Uncovered cells that the outside cannot reach form enclosed gaps.
        // Returns the cell of each gap that is farthest from the pillars.
        private static List<Vector2> FindEnclosedGapCenters(List<BasaltColumnPlacement> placements, float cellSize,
            float minimumArea, float maximumArea)
        {
            var centers = new Vector2[placements.Count];
            var radii = new float[placements.Count];
            Vector2 min = new(float.MaxValue, float.MaxValue);
            Vector2 max = new(float.MinValue, float.MinValue);
            for (int index = 0; index < placements.Count; index++)
            {
                Vector3 position = BasePosition(placements[index]);
                centers[index] = new Vector2(position.x, position.z);
                radii[index] = placements[index].Radius * FootprintShare;
                min = Vector2.Min(min, centers[index] - Vector2.one * radii[index]);
                max = Vector2.Max(max, centers[index] + Vector2.one * radii[index]);
            }
            min -= Vector2.one * (2f * cellSize);
            max += Vector2.one * (2f * cellSize);
            int width = Mathf.CeilToInt((max.x - min.x) / cellSize);
            int depth = Mathf.CeilToInt((max.y - min.y) / cellSize);
            var covered = new bool[width * depth];
            for (int index = 0; index < centers.Length; index++)
            {
                float radius = radii[index];
                int x0 = Mathf.Max(0, Mathf.FloorToInt((centers[index].x - radius - min.x) / cellSize));
                int x1 = Mathf.Min(width - 1, Mathf.CeilToInt((centers[index].x + radius - min.x) / cellSize));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((centers[index].y - radius - min.y) / cellSize));
                int z1 = Mathf.Min(depth - 1, Mathf.CeilToInt((centers[index].y + radius - min.y) / cellSize));
                for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    Vector2 cell = new(min.x + (x + 0.5f) * cellSize, min.y + (z + 0.5f) * cellSize);
                    if ((cell - centers[index]).sqrMagnitude <= radius * radius) covered[z * width + x] = true;
                }
            }

            // 0 = not visited, 1 = reached from the border, 2 = enclosed.
            var state = new byte[width * depth];
            var queue = new Queue<int>();
            var component = new List<int>();
            for (int x = 0; x < width; x++)
            {
                Enqueue(x, 0, 1);
                Enqueue(x, depth - 1, 1);
            }
            for (int z = 0; z < depth; z++)
            {
                Enqueue(0, z, 1);
                Enqueue(width - 1, z, 1);
            }
            Flood();

            var gapCenters = new List<Vector2>();
            float cellArea = cellSize * cellSize;
            for (int cell = 0; cell < state.Length; cell++)
            {
                if (covered[cell] || state[cell] != 0) continue;
                Enqueue(cell % width, cell / width, 2);
                Flood();
                float area = component.Count * cellArea;
                if (area < minimumArea || area > maximumArea) continue;

                Vector2 best = default;
                float bestClearance = float.MinValue;
                foreach (int member in component)
                {
                    Vector2 point = new(min.x + (member % width + 0.5f) * cellSize,
                        min.y + (member / width + 0.5f) * cellSize);
                    float clearance = Clearance(placements, point);
                    if (clearance <= bestClearance) continue;
                    bestClearance = clearance;
                    best = point;
                }
                gapCenters.Add(best);
            }
            return gapCenters;

            void Enqueue(int x, int z, byte value)
            {
                if (x < 0 || z < 0 || x >= width || z >= depth) return;
                int cell = z * width + x;
                if (covered[cell] || state[cell] != 0) return;
                state[cell] = value;
                queue.Enqueue(cell);
            }

            void Flood()
            {
                component.Clear();
                while (queue.Count > 0)
                {
                    int cell = queue.Dequeue();
                    byte value = state[cell];
                    component.Add(cell);
                    int x = cell % width;
                    int z = cell / width;
                    Enqueue(x - 1, z, value);
                    Enqueue(x + 1, z, value);
                    Enqueue(x, z - 1, value);
                    Enqueue(x, z + 1, value);
                }
            }
        }

        // Distance from a point to the nearest pillar footprint.
        private static float Clearance(List<BasaltColumnPlacement> placements, Vector2 point)
        {
            float clearance = float.MaxValue;
            foreach (BasaltColumnPlacement placement in placements)
            {
                Vector3 position = BasePosition(placement);
                float distance = Vector2.Distance(point, new Vector2(position.x, position.z)) -
                    placement.Radius * FootprintShare;
                clearance = Mathf.Min(clearance, distance);
            }
            return clearance;
        }

        private static BasaltSplineSample NearestSample(IReadOnlyList<BasaltSplineSample> samples, Vector2 point)
        {
            BasaltSplineSample best = samples[0];
            float bestDistance = float.MaxValue;
            foreach (BasaltSplineSample sample in samples)
            {
                float dx = sample.Position.x - point.x;
                float dz = sample.Position.z - point.y;
                float distance = dx * dx + dz * dz;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = sample;
            }
            return best;
        }

        private static float SampleHeight(BasaltColumnLayoutSettings settings, int key, float top, float baseHeight,
            float endFactor, float outerEdgeFactor)
        {
            float heightUnit = Unit(settings.Seed, key, 5);
            float edgeHeight = Mathf.Lerp(settings.EndHeightScale, 1f, endFactor);
            float outerHeight = Mathf.Lerp(1f, settings.OuterRowHeightScale, outerEdgeFactor);
            float variation = Signed(settings.Seed, key, 3) * settings.TopVariation;
            if (!settings.UseRelativeHeight)
            {
                return Mathf.Max(0.1f, settings.Height.Sample(heightUnit) * edgeHeight * outerHeight + variation);
            }

            float escarpmentHeight = Mathf.Max(0.1f, top - baseHeight);
            float minimumHeight = escarpmentHeight * settings.MinimumEscarpmentHeight;
            float maximumHeight = (escarpmentHeight + settings.MaximumHeightAboveEscarpment) *
                settings.MaximumHeightScale;
            float mapped = settings.Height.Distribution == null
                ? heightUnit
                : settings.Height.Distribution.Evaluate(heightUnit);
            float relativeRange = Mathf.Max(0.1f, maximumHeight - minimumHeight);
            float normalized = Mathf.Clamp01(mapped) * edgeHeight * outerHeight + variation / relativeRange;
            return Mathf.Lerp(minimumHeight, maximumHeight, Mathf.Clamp01(normalized));
        }

        // Linear interpolation between the spline samples. The cursor only moves forward along one row.
        private static SplineFrame Evaluate(IReadOnlyList<BasaltSplineSample> samples, float distance, ref int index)
        {
            while (index < samples.Count - 2 && samples[index + 1].Distance < distance)
            {
                index++;
            }
            BasaltSplineSample a = samples[index];
            BasaltSplineSample b = samples[Mathf.Min(index + 1, samples.Count - 1)];
            float span = b.Distance - a.Distance;
            float t = span > 0.0001f ? Mathf.Clamp01((distance - a.Distance) / span) : 0f;
            Vector3 tangent = Vector3.Lerp(a.Tangent, b.Tangent, t);
            tangent.y = 0f;
            return new SplineFrame
            {
                Position = Vector3.Lerp(a.Position, b.Position, t),
                Tangent = tangent.sqrMagnitude > 0.0001f ? tangent.normalized : Vector3.zero,
                NormalizedDistance = Mathf.Lerp(a.NormalizedDistance, b.NormalizedDistance, t)
            };
        }

        private static bool Overlaps(IReadOnlyList<BasaltColumnPlacement> placements,
            IReadOnlyDictionary<Vector2Int, List<int>> cells, Vector3 candidatePosition, float radius,
            float minimumSeparation, float cellSize)
        {
            Vector2Int center = SeparationCell(candidatePosition, cellSize);
            for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++)
            {
                if (!cells.TryGetValue(center + new Vector2Int(x, z), out List<int> indices))
                    continue;
                foreach (int index in indices)
                {
                    BasaltColumnPlacement other = placements[index];
                    Vector3 otherPosition = BasePosition(other);
                    float limit = Mathf.Max(minimumSeparation, OverlapLimit * (radius + other.Radius));
                    float dx = otherPosition.x - candidatePosition.x;
                    float dz = otherPosition.z - candidatePosition.z;
                    if (dx * dx + dz * dz < limit * limit)
                        return true;
                }
            }
            return false;
        }

        private static void AddToSeparationGrid(
            IDictionary<Vector2Int, List<int>> cells, int placementIndex,
            Vector3 position, float cellSize)
        {
            Vector2Int cell = SeparationCell(position, cellSize);
            if (!cells.TryGetValue(cell, out List<int> indices))
            {
                indices = new List<int>();
                cells.Add(cell, indices);
            }
            indices.Add(placementIndex);
        }

        private static Vector2Int SeparationCell(Vector3 position,
            float cellSize) => new(
            Mathf.FloorToInt(position.x / cellSize),
            Mathf.FloorToInt(position.z / cellSize));

        private static void ApplyContactOcclusion(List<BasaltColumnPlacement> placements)
        {
            if (placements.Count < 2) return;

            float maximumThickness = 0.1f;
            foreach (BasaltColumnPlacement placement in placements)
                maximumThickness = Mathf.Max(maximumThickness, placement.Thickness);
            float cellSize = maximumThickness * 1.5f;
            Dictionary<Vector2Int, List<int>> cells = new();
            for (int index = 0; index < placements.Count; index++)
            {
                Vector3 position = BasePosition(placements[index]);
                Vector2Int cell = Cell(position, cellSize);
                if (!cells.TryGetValue(cell, out List<int> indices))
                {
                    indices = new List<int>();
                    cells.Add(cell, indices);
                }
                indices.Add(index);
            }

            BasaltColumnPlacement[] source = placements.ToArray();
            for (int index = 0; index < source.Length; index++)
            {
                BasaltColumnPlacement current = source[index];
                Vector3 currentBase = BasePosition(current);
                Vector3 axisX = current.Matrix.GetColumn(0);
                Vector3 axisZ = current.Matrix.GetColumn(2);
                axisX.y = 0f;
                axisZ.y = 0f;
                axisX = axisX.sqrMagnitude > 0.0001f ? axisX.normalized : Vector3.right;
                axisZ = axisZ.sqrMagnitude > 0.0001f ? axisZ.normalized : Vector3.forward;
                Vector4 contact = Vector4.zero;
                Vector2Int centerCell = Cell(currentBase, cellSize);

                for (int cellZ = -1; cellZ <= 1; cellZ++)
                for (int cellX = -1; cellX <= 1; cellX++)
                {
                    Vector2Int candidateCell = centerCell + new Vector2Int(cellX, cellZ);
                    if (!cells.TryGetValue(candidateCell, out List<int> candidates)) continue;
                    foreach (int candidateIndex in candidates)
                    {
                        if (candidateIndex == index) continue;
                        BasaltColumnPlacement neighbour = source[candidateIndex];
                        Vector3 neighbourBase = BasePosition(neighbour);
                        Vector3 offset = neighbourBase - currentBase;
                        offset.y = 0f;
                        float distance = offset.magnitude;
                        if (distance < 0.001f) continue;

                        float combinedThickness = current.Thickness + neighbour.Thickness;
                        float innerDistance = combinedThickness * 0.38f;
                        float outerDistance = combinedThickness * 0.72f;
                        if (distance >= outerDistance) continue;
                        float proximity = 1f - Smooth01((distance - innerDistance) /
                            Mathf.Max(0.01f, outerDistance - innerDistance));
                        float overlap = Mathf.Min(currentBase.y + current.Height,
                            neighbourBase.y + neighbour.Height) -
                            Mathf.Max(currentBase.y, neighbourBase.y);
                        float verticalContact = Mathf.Clamp01(overlap /
                            Mathf.Max(0.1f, Mathf.Min(current.Height, neighbour.Height)));
                        float amount = proximity * Mathf.Lerp(0.45f, 1f, verticalContact);
                        Vector3 direction = offset / distance;
                        float localX = Vector3.Dot(direction, axisX);
                        float localZ = Vector3.Dot(direction, axisZ);
                        if (localX >= 0f) contact.x = Mathf.Max(contact.x, amount * localX);
                        else contact.y = Mathf.Max(contact.y, amount * -localX);
                        if (localZ >= 0f) contact.z = Mathf.Max(contact.z, amount * localZ);
                        else contact.w = Mathf.Max(contact.w, amount * -localZ);
                    }
                }

                placements[index] = current.WithContactOcclusion(contact);
            }
        }

        public static void RecalculateContactOcclusion(List<BasaltColumnPlacement> placements)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            ApplyContactOcclusion(placements);
        }

        private static Vector3 BasePosition(BasaltColumnPlacement placement) =>
            placement.Matrix.MultiplyPoint3x4(Vector3.down * 0.5f);

        private static Vector2Int Cell(Vector3 position, float cellSize) => new(
            Mathf.FloorToInt(position.x / cellSize),
            Mathf.FloorToInt(position.z / cellSize));

        public static float Unit(int seed, int index, int channel)
        {
            uint value = unchecked((uint)seed);
            value ^= unchecked((uint)index * 0x9E3779B9u);
            value ^= unchecked((uint)channel * 0x85EBCA6Bu);
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return (value & 0x00FFFFFFu) / 16777216f;
        }

        private static float Signed(int seed, int index, int channel) =>
            Unit(seed, index, channel) * 2f - 1f;

        private static float Smooth01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        private static float FractalNoise(float x, float z, float scale, int seed)
        {
            float ox = (seed & 1023) * 0.137f;
            float oz = ((seed >> 10) & 1023) * 0.173f;
            float first = Mathf.PerlinNoise(x * scale + ox, z * scale + oz);
            float second = Mathf.PerlinNoise(x * scale * 2.13f + oz, z * scale * 2.13f + ox);
            return first * 0.68f + second * 0.32f;
        }
    }
}
