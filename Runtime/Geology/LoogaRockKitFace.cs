using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Covers the terrain cliff face below a cliff-top spline with copies of sculpted rock meshes. Each copy gets its own
    /// orientation, mirror, scale and bend. The copy bends along the cliff and its back is pressed onto the terrain face,
    /// so the copies form one continuous face that keeps the detail of the meshes.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Rock Kit Face")]
    public sealed class LoogaRockKitFace : MonoBehaviour
    {
        // One column of the face frame: a point on the spline, the outward direction and the terrain profile.
        private struct Column
        {
            public Vector3 Origin;
            public Vector3 Tangent;
            public Vector3 Outward;
            public float RimHeight;
            public float FootHeight;
            public float RimDistance;
            public float FootDistance;
            public bool Active;
        }

        // The vertex data of one kit mesh, centred on its bounds.
        private sealed class KitMesh
        {
            public Vector3[] Positions;
            public Vector3[] Normals;
            public Vector2[] Uvs;
            public int[] Triangles;
        }

        // One vertex of the output. The colour holds the cavity in green. Red and blue hold a value for each piece.
        private struct OutputVertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector2 Uv;
            public Color Color;
        }

        private const float ColumnSpacing = 0.5f;
        private const float ProfileStep = 0.25f;
        private const float SearchInward = 4f;
        private const float MinimumFaceHeight = 2f;

        [Header("Placement")]
        [Tooltip("Spline along the top of the cliff.")]
        [SerializeField] private SplineContainer _spline;
        [Tooltip("Side of the spline where the ground drops. Auto compares the terrain on the two sides.")]
        [SerializeField] private RockFaceDropSide _dropSide = RockFaceDropSide.Auto;
        [Tooltip("Part of the spline that gets rock, as start and end shares of the spline length.")]
        [SerializeField] private Vector2 _range = new(0f, 1f);
        [Tooltip("Ground steeper than this angle, in degrees, is part of the face.")]
        [Range(30f, 85f)]
        [SerializeField] private float _faceAngle = 50f;
        [Tooltip("Distance in front of the spline where the face search stops, in meters.")]
        [Min(2f)]
        [SerializeField] private float _searchDistance = 20f;

        [Header("Kit")]
        [Tooltip("Sculpted or scanned rock meshes. In a player build, a mesh must be readable.")]
        [SerializeField] private List<Mesh> _meshes = new();
        [SerializeField] private Material _material;
        [Tooltip("Smallest and largest piece height, as a share of the face height.")]
        [SerializeField] private Vector2 _pieceSize = new(0.35f, 0.8f);
        [Tooltip("Share of each piece that overlaps the pieces next to it.")]
        [Range(0f, 0.6f)]
        [SerializeField] private float _overlap = 0.3f;
        [Tooltip("Largest change of the scale on each axis of a piece, as a share. 0 = uniform scale.")]
        [Range(0f, 0.6f)]
        [SerializeField] private float _scaleVariation = 0.25f;
        [Tooltip("Largest tilt of a piece away from its upright orientation, in degrees.")]
        [Range(0f, 45f)]
        [SerializeField] private float _tilt = 12f;
        [Tooltip("Largest bend of a piece out of the face, in meters.")]
        [Min(0f)]
        [SerializeField] private float _bend = 0.5f;
        [Tooltip("Distance from the terrain face to the front of each piece, in meters. The widest outline of each " +
                 "piece lies on the face, and the depth of the front half scales to this distance.")]
        [Min(0.1f)]
        [SerializeField] private float _standOut = 1.5f;
        [Tooltip("Thickness of the layer behind the terrain face that holds the back of each piece, in meters.")]
        [Min(0.05f)]
        [SerializeField] private float _backDepth = 0.4f;

        [Header("Shape")]
        [Tooltip("Largest distance that buttresses stand out of the face, in meters. 0 = an even face.")]
        [Min(0f)]
        [SerializeField] private float _macroDepth = 2.5f;
        [Tooltip("Typical width of the buttresses and bays along the cliff, in meters.")]
        [Min(2f)]
        [SerializeField] private float _macroSize = 20f;
        [Tooltip("Largest height that pieces rise above the rim, in meters. Larger values give a more broken top edge.")]
        [Min(0f)]
        [SerializeField] private float _topRise = 1.2f;

        [Header("Coverage")]
        [Tooltip("Share of the face that the pieces must cover. The build stops at this share or at the piece limit.")]
        [Range(0.5f, 1f)]
        [SerializeField] private float _targetCoverage = 0.97f;
        [Min(1)]
        [SerializeField] private int _maxPieces = 150;
        [Tooltip("Size of one coverage cell, in meters. Smaller cells find smaller gaps.")]
        [Min(0.1f)]
        [SerializeField] private float _cellSize = 0.25f;

        [Header("Output")]
        [SerializeField] private ShadowCastingMode _shadowCasting = ShadowCastingMode.On;
        [Tooltip("Largest length of one chunk along the cliff, in meters. Each chunk is one draw call.")]
        [Min(4f)]
        [SerializeField] private float _chunkLength = 30f;
        [Tooltip("Screen height of a chunk below which it uses the second and the third level of detail.")]
        [SerializeField] private Vector2 _lodTransitions = new(0.25f, 0.06f);
        [Tooltip("Size of the vertex clusters of the second and the third level of detail, in meters.")]
        [SerializeField] private Vector2 _lodCellSizes = new(0.3f, 1f);
        [SerializeField] private int _seed = 1;

        private readonly List<Mesh> _generatedMeshes = new();
        private GameObject _root;
        private bool _buildQueued;

        public int PieceCount { get; private set; }
        public float Coverage { get; private set; }
        public int TriangleCount { get; private set; }
        public string Diagnostic { get; private set; }

        #region Built-in
        private void OnEnable()
        {
            Spline.Changed += OnSplineChanged;
            Rebuild();
        }

        private void OnDisable()
        {
            Spline.Changed -= OnSplineChanged;
            Release();
        }

        // OnValidate must not create objects, so it only queues the build.
        private void OnValidate()
        {
            if (!GeneratedReloadHandover.KeepsOutputOnLoad) QueueBuild();
        }
        #endregion

        /// <summary>Builds the face now. Main thread.</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            _buildQueued = false;
            Release();
            Diagnostic = null;
            if (!isActiveAndEnabled || !_spline || _spline.Spline == null || _spline.Spline.Count < 2) return;
            List<KitMesh> kit = ReadKit();
            if (kit.Count == 0)
            {
                Diagnostic = "The kit has no readable meshes.";
                return;
            }
            Column[] columns = BuildFrame(out float start);
            if (columns == null)
            {
                Diagnostic = "No terrain face in the spline range is steep and high enough.";
                return;
            }
            var random = new System.Random(_seed);
            List<List<OutputVertex>> chunkVertices;
            List<List<int>> chunkTriangles;
            PlacePieces(columns, start, kit, random, out chunkVertices, out chunkTriangles);
            CreateOutput(chunkVertices, chunkTriangles);
        }

        #region Face Frame
        // Samples the spline in columns and finds the terrain face in front of each column.
        private Column[] BuildFrame(out float start)
        {
            float length = _spline.CalculateLength();
            start = Mathf.Clamp01(Mathf.Min(_range.x, _range.y)) * length;
            float end = Mathf.Clamp01(Mathf.Max(_range.x, _range.y)) * length;
            int count = Mathf.FloorToInt((end - start) / ColumnSpacing) + 1;
            if (count < 2) return null;

            Bounds bounds = _spline.Spline.GetBounds(_spline.transform.localToWorldMatrix);
            float margin = _searchDistance + SearchInward + 4f;
            var region = new Rect(bounds.min.x - margin, bounds.min.z - margin, bounds.size.x + 2f * margin,
                bounds.size.z + 2f * margin);
            using var terrain = new TerrainHeightSampler(region);
            terrain.WaitForReads();

            var columns = new Column[count];
            int active = 0;
            int steps = Mathf.CeilToInt((_searchDistance + SearchInward) / ProfileStep) + 1;
            var profile = new float[steps];
            float minimumSlope = Mathf.Tan(_faceAngle * Mathf.Deg2Rad) * ProfileStep;
            for (int index = 0; index < count; index++)
            {
                float t = Mathf.Clamp01((start + index * ColumnSpacing) / length);
                Vector3 origin = _spline.EvaluatePosition(t);
                Vector3 tangent = _spline.EvaluateTangent(t);
                tangent.y = 0f;
                tangent = tangent.sqrMagnitude > 1e-6f ? tangent.normalized : Vector3.forward;
                Vector3 outward = new Vector3(tangent.z, 0f, -tangent.x) * DropSign(terrain, origin, tangent);
                var column = new Column { Origin = new Vector3(origin.x, 0f, origin.z), Tangent = tangent, Outward = outward };

                for (int step = 0; step < steps; step++)
                {
                    Vector3 point = column.Origin + outward * (step * ProfileStep - SearchInward);
                    profile[step] = terrain.TrySampleHeight(point.x, point.z, out float height) ? height : float.NaN;
                }
                // The face is the tallest run of samples that drop faster than the face angle.
                int bestStart = -1;
                int bestEnd = -1;
                float bestDrop = 0f;
                int runStart = -1;
                for (int step = 1; step <= steps; step++)
                {
                    bool steep = step < steps && !float.IsNaN(profile[step]) && !float.IsNaN(profile[step - 1]) &&
                                 profile[step - 1] - profile[step] >= minimumSlope;
                    if (steep && runStart < 0) runStart = step - 1;
                    if (steep || runStart < 0) continue;
                    float drop = profile[runStart] - profile[step - 1];
                    if (drop > bestDrop)
                    {
                        bestDrop = drop;
                        bestStart = runStart;
                        bestEnd = step - 1;
                    }
                    runStart = -1;
                }
                if (bestDrop >= MinimumFaceHeight)
                {
                    column.Active = true;
                    column.RimHeight = profile[bestStart];
                    column.FootHeight = profile[bestEnd];
                    column.RimDistance = bestStart * ProfileStep - SearchInward;
                    column.FootDistance = bestEnd * ProfileStep - SearchInward;
                    active++;
                }
                columns[index] = column;
            }
            return active > 0 ? columns : null;
        }

        // +1 when the ground drops on the right of the spline direction, -1 when it drops on the left.
        private float DropSign(TerrainHeightSampler terrain, Vector3 origin, Vector3 tangent)
        {
            if (_dropSide == RockFaceDropSide.Right) return 1f;
            if (_dropSide == RockFaceDropSide.Left) return -1f;
            var right = new Vector3(tangent.z, 0f, -tangent.x);
            Vector3 a = origin + right * 8f;
            Vector3 b = origin - right * 8f;
            bool hasA = terrain.TrySampleHeight(a.x, a.z, out float heightA);
            bool hasB = terrain.TrySampleHeight(b.x, b.z, out float heightB);
            return !hasA || !hasB || heightA <= heightB ? 1f : -1f;
        }

        // World position of a face-space point: u along the cliff, v height, w out of the face.
        private static Vector3 ToWorld(Column[] columns, float start, float u, float v, float w, out Column frame)
        {
            float position = Mathf.Clamp((u - start) / ColumnSpacing, 0f, columns.Length - 1.001f);
            int index = (int)position;
            float blend = position - index;
            Column a = Nearest(columns, index, -1);
            Column b = Nearest(columns, index + 1, 1);
            frame = new Column
            {
                Origin = Vector3.Lerp(a.Origin, b.Origin, blend),
                Tangent = Vector3.Slerp(a.Tangent, b.Tangent, blend),
                Outward = Vector3.Slerp(a.Outward, b.Outward, blend),
                RimHeight = Mathf.Lerp(a.RimHeight, b.RimHeight, blend),
                FootHeight = Mathf.Lerp(a.FootHeight, b.FootHeight, blend),
                RimDistance = Mathf.Lerp(a.RimDistance, b.RimDistance, blend),
                FootDistance = Mathf.Lerp(a.FootDistance, b.FootDistance, blend),
                Active = true
            };
            // The face runs straight from the rim to the foot. Below the foot and above the rim it keeps that slope.
            float share = (frame.RimHeight - v) / Mathf.Max(0.01f, frame.RimHeight - frame.FootHeight);
            float faceDistance = Mathf.LerpUnclamped(frame.RimDistance, frame.FootDistance, share);
            Vector3 point = frame.Origin + frame.Outward * (faceDistance + w);
            point.y = v;
            return point;
        }

        // The nearest active column from the index in one direction, or the nearest in the other direction.
        private static Column Nearest(Column[] columns, int index, int direction)
        {
            index = Mathf.Clamp(index, 0, columns.Length - 1);
            for (int offset = 0; offset < columns.Length; offset++)
            {
                int first = index + offset * direction;
                if (first >= 0 && first < columns.Length && columns[first].Active) return columns[first];
                int second = index - offset * direction;
                if (second >= 0 && second < columns.Length && columns[second].Active) return columns[second];
            }
            return columns[index];
        }
        #endregion

        #region Pieces
        // Places pieces until the coverage target or the piece limit, and adds their triangles to the chunks.
        private void PlacePieces(Column[] columns, float start, List<KitMesh> kit, System.Random random,
            out List<List<OutputVertex>> chunkVertices,
            out List<List<int>> chunkTriangles)
        {
            float faceLength = (columns.Length - 1) * ColumnSpacing;
            float bottom = float.MaxValue;
            float top = float.MinValue;
            foreach (Column column in columns)
            {
                if (!column.Active) continue;
                bottom = Mathf.Min(bottom, column.FootHeight);
                top = Mathf.Max(top, column.RimHeight);
            }
            int cellsU = Mathf.Max(1, Mathf.CeilToInt(faceLength / _cellSize));
            int cellsV = Mathf.Max(1, Mathf.CeilToInt((top - bottom) / _cellSize));
            var valid = new bool[cellsU, cellsV];
            var covered = new bool[cellsU, cellsV];
            int validCount = 0;
            for (int cu = 0; cu < cellsU; cu++)
            {
                Column column = columns[Mathf.Min(columns.Length - 1, Mathf.RoundToInt((cu + 0.5f) * _cellSize / ColumnSpacing))];
                if (!column.Active) continue;
                for (int cv = 0; cv < cellsV; cv++)
                {
                    float v = bottom + (cv + 0.5f) * _cellSize;
                    if (v < column.FootHeight || v > column.RimHeight) continue;
                    valid[cu, cv] = true;
                    validCount++;
                }
            }

            chunkVertices = new List<List<OutputVertex>>();
            chunkTriangles = new List<List<int>>();
            int chunkCount = Mathf.Max(1, Mathf.CeilToInt(faceLength / _chunkLength));
            for (int index = 0; index < chunkCount; index++)
            {
                chunkVertices.Add(new List<OutputVertex>());
                chunkTriangles.Add(new List<int>());
            }

            int coveredCount = 0;
            int pieces = 0;
            float smallestPiece = float.MaxValue;
            float largestPiece = 0f;
            int scanU = 0;
            while (pieces < _maxPieces && coveredCount < validCount * _targetCoverage)
            {
                // The next gap is the lowest uncovered cell in the first column that has one.
                int seedU = -1;
                int seedV = -1;
                for (int cu = scanU; cu < cellsU && seedU < 0; cu++)
                {
                    for (int cv = 0; cv < cellsV; cv++)
                    {
                        if (!valid[cu, cv] || covered[cu, cv]) continue;
                        seedU = cu;
                        seedV = cv;
                        break;
                    }
                    if (seedU < 0) scanU = cu + 1;
                }
                if (seedU < 0) break;

                Column frame = columns[Mathf.Min(columns.Length - 1, Mathf.RoundToInt((seedU + 0.5f) * _cellSize / ColumnSpacing))];
                float faceHeight = frame.RimHeight - frame.FootHeight;
                int gap = 0;
                while (seedV + gap < cellsV && valid[seedU, seedV + gap] && !covered[seedU, seedV + gap]) gap++;
                float wanted = Mathf.Max(gap * _cellSize * (1f + _overlap), _pieceSize.x * faceHeight);
                float height = Mathf.Min(wanted, _pieceSize.y * faceHeight) * RandomRange(random, 0.85f, 1.15f);

                smallestPiece = Mathf.Min(smallestPiece, height);
                largestPiece = Mathf.Max(largestPiece, height);
                KitMesh mesh = kit[random.Next(kit.Count)];
                Piece piece = CreatePiece(mesh, height, random);
                // The piece centre is up and along from the gap, so the rounded piece covers the gap and overlaps the
                // pieces below and behind it. A box corner is not used: a rounded rock never covers its box corner.
                float offset = Mathf.Max(0f, 0.5f - _overlap);
                float u = (seedU + 0.5f) * _cellSize + piece.Size.x * offset - (piece.Min.x + piece.Size.x * 0.5f);
                float v = bottom + (seedV + 0.5f) * _cellSize + piece.Size.y * offset - (piece.Min.y + piece.Size.y * 0.5f);
                v = Mathf.Min(v, frame.RimHeight + RandomRange(random, 0f, _topRise) - piece.Max.y);
                int added = AddPiece(piece, mesh, columns, start, u, v, bottom, valid, covered,
                    chunkVertices, chunkTriangles, (float)random.NextDouble());
                coveredCount += added;
                if (!covered[seedU, seedV])
                {
                    // A gap that the new piece does not cover must not get the next piece too.
                    covered[seedU, seedV] = true;
                    coveredCount++;
                }
                pieces++;
            }
            PieceCount = pieces;
            Coverage = validCount > 0 ? (float)coveredCount / validCount : 0f;
            Diagnostic = "Face " + bottom.ToString("F1") + "-" + top.ToString("F1") + " m, " + validCount + " cells, pieces " +
                         smallestPiece.ToString("F1") + "-" + largestPiece.ToString("F1") + " m high.";
        }

        // One transformed copy of a kit mesh in face space: x along the cliff, y up, z out of the face.
        private sealed class Piece
        {
            public Vector3[] Positions;
            public Vector3[] Normals;
            public bool Mirrored;
            public Vector3 Min;
            public Vector3 Max;
            public Vector3 Size => Max - Min;
            public float MiddleDepth => (Min.z + Max.z) * 0.5f;
            public float DepthScale;
            public float BendPhaseU;
            public float BendPhaseV;
            public float BendScale;
        }

        private Piece CreatePiece(KitMesh mesh, float height, System.Random random)
        {
            // One of the four sides of the mesh faces out, then a random roll and a small tilt.
            Vector3 front = random.Next(4) switch
            {
                0 => Vector3.right,
                1 => Vector3.left,
                2 => Vector3.forward,
                _ => Vector3.back
            };
            Quaternion rotation = Quaternion.Euler(RandomRange(random, -_tilt, _tilt), RandomRange(random, -_tilt, _tilt),
                                      RandomRange(random, 0f, 360f)) * Quaternion.FromToRotation(front, Vector3.forward);
            var scale = new Vector3(1f + RandomRange(random, -_scaleVariation, _scaleVariation),
                1f + RandomRange(random, -_scaleVariation, _scaleVariation),
                1f + RandomRange(random, -_scaleVariation, _scaleVariation));
            bool mirrored = random.Next(2) == 0;
            if (mirrored) scale.x = -scale.x;

            var positions = new Vector3[mesh.Positions.Length];
            var normals = new Vector3[mesh.Positions.Length];
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int index = 0; index < positions.Length; index++)
            {
                Vector3 position = Vector3.Scale(rotation * mesh.Positions[index], scale);
                positions[index] = position;
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
                Vector3 normal = rotation * mesh.Normals[index];
                normals[index] = new Vector3(normal.x / scale.x, normal.y / scale.y, normal.z / scale.z).normalized;
            }
            float factor = height / Mathf.Max(0.01f, max.y - min.y);
            for (int index = 0; index < positions.Length; index++) positions[index] *= factor;
            return new Piece
            {
                DepthScale = _standOut / Mathf.Max(0.01f, (max.z - min.z) * 0.5f * factor),
                Positions = positions,
                Normals = normals,
                Mirrored = mirrored,
                Min = min * factor,
                Max = max * factor,
                BendPhaseU = RandomRange(random, 0f, 100f),
                BendPhaseV = RandomRange(random, 0f, 100f),
                BendScale = RandomRange(random, 0.15f, 0.35f)
            };
        }

        // Bends the piece onto the face, adds its triangles to a chunk and marks the cells it covers. Returns the
        // number of newly covered cells.
        private int AddPiece(Piece piece, KitMesh mesh, Column[] columns, float start, float u, float v, float bottom,
            bool[,] valid, bool[,] covered, List<List<OutputVertex>> chunkVertices, List<List<int>> chunkTriangles,
            float pieceValue)
        {
            int count = piece.Positions.Length;
            var world = new Vector3[count];
            var normals = new Vector3[count];
            var depth = new float[count];
            var front = new float[count];
            var colors = new Color[count];
            var face = new Vector2[count];
            bool inverted = false;
            for (int index = 0; index < count; index++)
            {
                Vector3 local = piece.Positions[index];
                face[index] = new Vector2(u + local.x, v + local.y);
                float bend = _bend * (Mathf.PerlinNoise(piece.BendPhaseU + local.x * piece.BendScale,
                    piece.BendPhaseV + local.y * piece.BendScale) - 0.5f) * 2f;
                // Distance in front of the widest outline of the piece.
                float outline = (local.z - piece.MiddleDepth) * piece.DepthScale + bend;
                float macro = MacroOffset(start + face[index].x);
                float w = macro + outline;
                if (outline < 0f)
                {
                    // The back of the piece reaches back to a thin layer behind the terrain face, also under a buttress.
                    float reach = macro + _backDepth;
                    w = -_backDepth + reach * Mathf.Exp(outline / reach);
                }
                depth[index] = w;
                front[index] = outline;
                // The rock is dark where its surface turns back into the seams between pieces. The stretched back of a
                // piece at the side of a buttress is a wall, not a seam, so it gets lighter with its depth.
                float cavity = outline >= 0f
                    ? Mathf.Pow(1f - Mathf.Clamp01(outline / (_standOut * 0.7f)), 2f)
                    : Mathf.Lerp(1f, 0.4f, Mathf.Clamp01(-outline / (_standOut + macro)));
                colors[index] = new Color(pieceValue, cavity, 1f - pieceValue, 0f);
                world[index] = ToWorld(columns, start, start + face[index].x, face[index].y, w, out Column frame);
                // When along, up and out form a mirrored frame, the frame turns the triangles inside out.
                if (index == 0) inverted = Vector3.Dot(Vector3.Cross(frame.Tangent, Vector3.up), frame.Outward) < 0f;
                Vector3 normal = piece.Normals[index];
                // The depth scale also tilts the normals.
                normal.z /= piece.DepthScale;
                normals[index] = (frame.Tangent * normal.x + Vector3.up * normal.y + frame.Outward * normal.z).normalized;
            }

            int chunk = Mathf.Clamp(Mathf.FloorToInt((u + piece.Min.x + piece.Size.x * 0.5f) / _chunkLength), 0,
                chunkVertices.Count - 1);
            List<OutputVertex> vertices = chunkVertices[chunk];
            List<int> triangles = chunkTriangles[chunk];
            var remap = new int[count];
            for (int index = 0; index < count; index++) remap[index] = -1;
            int added = 0;
            int[] source = mesh.Triangles;
            float hidden = -_backDepth * 0.6f;
            for (int index = 0; index < source.Length; index += 3)
            {
                int a = source[index];
                int b = source[index + 1];
                int c = source[index + 2];
                // A triangle deep inside the back layer is never visible.
                if (depth[a] < hidden && depth[b] < hidden && depth[c] < hidden) continue;
                // A mirrored piece or a mirrored frame turns the triangles inside out, so the triangle swaps two corners.
                if (piece.Mirrored != inverted) (b, c) = (c, b);
                triangles.Add(Vertex(a));
                triangles.Add(Vertex(b));
                triangles.Add(Vertex(c));
                if (front[a] > -0.05f && front[b] > -0.05f && front[c] > -0.05f)
                {
                    added += Rasterize(face[a], face[b], face[c], bottom, valid, covered);
                }
            }
            return added;

            int Vertex(int index)
            {
                if (remap[index] >= 0) return remap[index];
                remap[index] = vertices.Count;
                vertices.Add(new OutputVertex
                {
                    Position = world[index],
                    Normal = normals[index],
                    Uv = mesh.Uvs[index],
                    Color = colors[index]
                });
                return remap[index];
            }
        }

        // Distance that the buttresses and bays move the face out, in meters: 0 in a bay, the macro depth on a buttress.
        // The offset changes only along the cliff. A buttress then runs down the full face and has no overhang, which
        // would cast a dark shadow that looks like a hole.
        private float MacroOffset(float u)
        {
            if (_macroDepth <= 0f) return 0f;
            float x = u / _macroSize + _seed * 7.31f;
            float noise = Mathf.PerlinNoise(x, _seed * 3.17f) * 0.7f + Mathf.PerlinNoise(x * 2.3f + 17f, 5f) * 0.3f;
            return _macroDepth * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.7f, noise));
        }

        // Marks the coverage cells whose centres are inside the triangle. Returns the number of newly covered cells.
        private int Rasterize(Vector2 a, Vector2 b, Vector2 c, float bottom, bool[,] valid, bool[,] covered)
        {
            int cellsU = covered.GetLength(0);
            int cellsV = covered.GetLength(1);
            int minU = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / _cellSize));
            int maxU = Mathf.Min(cellsU - 1, Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / _cellSize));
            int minV = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - bottom) / _cellSize));
            int maxV = Mathf.Min(cellsV - 1, Mathf.FloorToInt((Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - bottom) / _cellSize));
            float area = Cross(b - a, c - a);
            if (Mathf.Abs(area) < 1e-8f) return 0;
            int added = 0;
            for (int cu = minU; cu <= maxU; cu++)
            {
                for (int cv = minV; cv <= maxV; cv++)
                {
                    if (!valid[cu, cv] || covered[cu, cv]) continue;
                    var point = new Vector2((cu + 0.5f) * _cellSize, bottom + (cv + 0.5f) * _cellSize);
                    float w0 = Cross(b - point, c - point) / area;
                    float w1 = Cross(c - point, a - point) / area;
                    if (w0 < 0f || w1 < 0f || w0 + w1 > 1f) continue;
                    covered[cu, cv] = true;
                    added++;
                }
            }
            return added;
        }
        #endregion

        #region Output
        private void CreateOutput(List<List<OutputVertex>> chunkVertices, List<List<int>> chunkTriangles)
        {
            _root = new GameObject("Rock kit " + name) { hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer };
            int triangles = 0;
            for (int index = 0; index < chunkVertices.Count; index++)
            {
                List<OutputVertex> vertices = chunkVertices[index];
                if (vertices.Count == 0) continue;
                var positions = new Vector3[vertices.Count];
                var normals = new Vector3[vertices.Count];
                var uvs = new Vector2[vertices.Count];
                var colors = new Color[vertices.Count];
                Vector3 center = Vector3.zero;
                foreach (OutputVertex vertex in vertices) center += vertex.Position;
                center /= vertices.Count;
                for (int vertex = 0; vertex < vertices.Count; vertex++)
                {
                    positions[vertex] = vertices[vertex].Position - center;
                    normals[vertex] = vertices[vertex].Normal;
                    uvs[vertex] = vertices[vertex].Uv;
                    colors[vertex] = vertices[vertex].Color;
                }
                triangles += chunkTriangles[index].Count / 3;

                var chunk = new GameObject("Chunk " + index) { hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer };
                chunk.transform.SetParent(_root.transform, false);
                chunk.transform.position = center;
                var levels = new LOD[3];
                for (int level = 0; level < 3; level++)
                {
                    Mesh mesh = level == 0
                        ? CreateMesh(positions, normals, uvs, colors, chunkTriangles[index])
                        : Simplify(positions, normals, uvs, colors, chunkTriangles[index],
                            level == 1 ? _lodCellSizes.x : _lodCellSizes.y);
                    mesh.name = name + " chunk " + index + " LOD" + level;
                    _generatedMeshes.Add(mesh);
                    var part = new GameObject("LOD" + level) { hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer };
                    part.transform.SetParent(chunk.transform, false);
                    part.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = part.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = _material;
                    renderer.shadowCastingMode = _shadowCasting;
                    float height = level == 0 ? _lodTransitions.x : level == 1 ? _lodTransitions.y : 0.005f;
                    levels[level] = new LOD(height, new Renderer[] { renderer });
                }
                var group = chunk.AddComponent<LODGroup>();
                group.SetLODs(levels);
                group.RecalculateBounds();
            }
            TriangleCount = triangles;
        }

        private static Mesh CreateMesh(Vector3[] positions, Vector3[] normals, Vector2[] uvs, Color[] colors,
            List<int> triangles)
        {
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Vertex clustering: the vertices in one grid cell that face about the same way become one vertex. A triangle
        // with two corners in one cluster is removed. It is fast and keeps the outline of the rock at a distance.
        private static Mesh Simplify(Vector3[] positions, Vector3[] normals, Vector2[] uvs, Color[] colors,
            List<int> triangles, float cellSize)
        {
            var clusters = new Dictionary<long, int>();
            var remap = new int[positions.Length];
            var sumPositions = new List<Vector3>();
            var sumNormals = new List<Vector3>();
            var sumColors = new List<Color>();
            var clusterUvs = new List<Vector2>();
            var counts = new List<int>();
            for (int index = 0; index < positions.Length; index++)
            {
                Vector3 position = positions[index];
                Vector3 normal = normals[index];
                long key = ((long)(Mathf.FloorToInt(position.x / cellSize) + 32768) << 35) |
                           ((long)(Mathf.FloorToInt(position.y / cellSize) + 32768) << 19) |
                           ((long)(Mathf.FloorToInt(position.z / cellSize) + 32768) << 3) | (long)Direction(normal);
                if (!clusters.TryGetValue(key, out int cluster))
                {
                    cluster = counts.Count;
                    clusters.Add(key, cluster);
                    sumPositions.Add(Vector3.zero);
                    sumNormals.Add(Vector3.zero);
                    sumColors.Add(Color.clear);
                    clusterUvs.Add(uvs[index]);
                    counts.Add(0);
                }
                sumPositions[cluster] += position;
                sumNormals[cluster] += normal;
                sumColors[cluster] += colors[index];
                counts[cluster]++;
                remap[index] = cluster;
            }
            var outPositions = new Vector3[counts.Count];
            var outNormals = new Vector3[counts.Count];
            var outColors = new Color[counts.Count];
            for (int cluster = 0; cluster < counts.Count; cluster++)
            {
                outPositions[cluster] = sumPositions[cluster] / counts[cluster];
                outNormals[cluster] = sumNormals[cluster].normalized;
                outColors[cluster] = sumColors[cluster] / counts[cluster];
            }
            var outTriangles = new List<int>(triangles.Count / 4);
            for (int index = 0; index < triangles.Count; index += 3)
            {
                int a = remap[triangles[index]];
                int b = remap[triangles[index + 1]];
                int c = remap[triangles[index + 2]];
                if (a == b || b == c || a == c) continue;
                outTriangles.Add(a);
                outTriangles.Add(b);
                outTriangles.Add(c);
            }
            return CreateMesh(outPositions, outNormals, clusterUvs.ToArray(), outColors, outTriangles);
        }

        // The main axis direction of a normal: 0 to 5 for +x, -x, +y, -y, +z, -z.
        private static int Direction(Vector3 normal)
        {
            Vector3 size = new Vector3(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
            if (size.x >= size.y && size.x >= size.z) return normal.x > 0f ? 0 : 1;
            if (size.y >= size.z) return normal.y > 0f ? 2 : 3;
            return normal.z > 0f ? 4 : 5;
        }

        private void Release()
        {
            if (_root) DestroyNow(_root);
            _root = null;
            foreach (Mesh mesh in _generatedMeshes)
            {
                if (mesh) DestroyNow(mesh);
            }
            _generatedMeshes.Clear();
            PieceCount = 0;
            Coverage = 0f;
            TriangleCount = 0;
        }

        private static void DestroyNow(Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
        #endregion

        #region Helpers
        // Reads the kit meshes. In the editor, the mesh data API also reads meshes that are not readable.
        private List<KitMesh> ReadKit()
        {
            var kit = new List<KitMesh>();
            foreach (Mesh source in _meshes)
            {
                if (!source) continue;
                using Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(source);
                Mesh.MeshData data = dataArray[0];
                int count = data.vertexCount;
                using var positions = new NativeArray<Vector3>(count, Allocator.Temp);
                using var normals = new NativeArray<Vector3>(count, Allocator.Temp);
                using var uvs = new NativeArray<Vector2>(count, Allocator.Temp);
                data.GetVertices(positions);
                data.GetNormals(normals);
                if (data.HasVertexAttribute(VertexAttribute.TexCoord0)) data.GetUVs(0, uvs);
                var triangles = new List<int>();
                for (int subMesh = 0; subMesh < data.subMeshCount; subMesh++)
                {
                    using var indices = new NativeArray<int>(data.GetSubMesh(subMesh).indexCount, Allocator.Temp);
                    data.GetIndices(indices, subMesh);
                    triangles.AddRange(indices);
                }
                Vector3 center = source.bounds.center;
                var mesh = new KitMesh
                {
                    Positions = new Vector3[count],
                    Normals = normals.ToArray(),
                    Uvs = uvs.ToArray(),
                    Triangles = triangles.ToArray()
                };
                for (int index = 0; index < count; index++) mesh.Positions[index] = positions[index] - center;
                kit.Add(mesh);
            }
            return kit;
        }

        private static float RandomRange(System.Random random, float minimum, float maximum)
        {
            return minimum + (float)random.NextDouble() * (maximum - minimum);
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private void QueueBuild()
        {
            if (_buildQueued) return;
            _buildQueued = true;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this && _buildQueued) Rebuild();
            };
#endif
        }

        private void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification)
        {
            if (Application.isPlaying || !_spline) return;
            foreach (Spline own in _spline.Splines)
            {
                if (own != spline) continue;
                QueueBuild();
                return;
            }
        }
        #endregion
    }
}
