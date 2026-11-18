using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LoogaSoft.Terrain.Stamps;
using LoogaSoft.Terrain.Surface;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Roads.Editor
{
    /// <summary>
    /// Builds thin asphalt slab meshes on the paved roads and the paved pads of a <see cref="LoogaRoadNetwork"/>. A
    /// slab stands a little above the ground. A rounded edge with a ragged outline runs around it and ends below the
    /// ground, so the pavement looks poured on the ground. Where a road meets a pad or another road, the joining slab
    /// runs under the other slab and has no edge there, so no seam shows. A gravel shoulder stamp hides the soft edge
    /// of the painted terrain pavement. The terrain keeps its heights and its collider.
    /// </summary>
    public static class LoogaRoadSlabs
    {
        // Name of the slab parent that a network without one gets.
        private const string RootName = "Road slabs";
        // The network of the running build, update or comparison.
        private static LoogaRoadNetwork _network;

        private static string Folder => _network.OutputFolder;
        private static string ShoulderMaskPath => Folder + "/Road shoulder.png";
        private static string ClearMaskPath => Folder + "/Road clear.png";
        private static string MeshPrefix => _network.MeshPrefix;

        // The slab top is this far above the ground. The rounded edge starts LipInset inside the outline and ends
        // LipWidthAt outside it (LipWidthMin to LipWidthMax, changing along the edge, so the edge does not show as an
        // even ring). It leaves the top and meets the ground with zero slope (smoothstep), so no crease
        // shows at the top or at the ground. It ends LipLift above the ground and does not cut into it, so no line
        // shows where the two surfaces cross. The road shader moves the lip toward the camera with distance, so the
        // terrain does not show through it far away. LipSteps sets the number of segments in the curve.
        private const float Thickness = 0.1f;
        // The slab top above the ground, for the bridge deck tops that continue it (LoogaRoadNetworkTools).
        internal const float SlabThickness = Thickness;
        // A road end within this distance of a bridge deck end stops on the deck end.
        private const float BridgeEndReach = 3f;
        // Length of the bridge deck end that gets the plant clear (BridgeDeckEnds).
        private const float DeckEndLength = 12f;
        private const float LipInset = 0.25f;
        private const float LipWidthMin = 0.35f;
        private const float LipWidthMax = 1.1f;
        private const float LipLift = 0.01f;
        private const int LipSteps = 8;
        // The outline moves in and out by up to this distance, so the pour line is not exact.
        private const float EdgeNoise = 0.12f;
        // The top undulates up to about this height, so the light shows a poured surface. On a pad the undulation fades
        // out toward the rim, and on a road it fades out toward a pad, so the two tops meet there.
        private const float Undulation = 0.03f;
        private const float UndulationFade = 2.5f;
        // The pad top is a set of rings between the center and the outline, about this far apart.
        private const float PadRingStep = 1f;
        // A road slab runs RoadOverlap under the pad side that it crosses. Under a pad or a host road its vertices stay
        // Under lower than the other top, so the other slab covers the joint.
        private const float RoadOverlap = 0.5f;
        private const float Under = 0.003f;
        // A pad rim point where a road joins counts as this far inside the pad in the road coordinates, so the road
        // wear puts no edge dust or edge crack across the joint.
        private const float JoinedRimDepth = 10f;
        // Near a road the pad edge moves to the exact outline over this distance from the road slab edge.
        private const float PadJoinBlend = 1.5f;
        // A road end belongs to a pad when it is this close to the pad outline. A road that stops short of a moved or
        // turned pad then still joins it: its mouth covers the gap.
        private const float PadReach = 12f;
        // A road end that is this close to the top of another road joins that road.
        private const float JunctionReach = 1.5f;
        // Road ends this close to each other meet at a junction node. A patch slab covers the node, and the roads run
        // PatchOverlap under it.
        private const float NodeReach = 9f;
        // A junction patch covers the road tops this far along each road from the node, closed with the fillet radius.
        private const float FilletArm = 14f;
        private const float FilletRadius = 6f;
        private const float PatchOverlap = 2f;
        // Road mouths: a paved pad grows over the roads that end at it. The pad outline and the first MouthReach of
        // each road join in a raster. A closing of MouthRadius (grow, then shrink) then fills each concave corner with
        // a round fillet, so the road runs into the pad through a curb return. A gap of less than twice the radius
        // between a road and the pad also closes. The road slab ends under the grown pad.
        private const float MouthRadius = 7f;
        private const float MouthReach = MouthRadius + 1f;
        private const float MouthCell = 0.35f;
        private const int MouthRays = 2048;
        // After the closing, an opening of this radius (shrink, then grow) rounds the convex corners and removes
        // slivers narrower than twice the radius, such as the corner of a road end cap beside the pad.
        private const float MouthRounding = 2f;
        // A road row ends under the grown pad when both of its edges are this far inside the outline.
        private const float MouthDepth = 0.3f;
        // A road side that comes this close to another slab at about the same height extends across the gap and runs
        // MergeOverlap under that slab. Of two roads, the later piece extends under the earlier one.
        private const float MergeGap = 3f;
        private const float MergeOverlap = 0.3f;
        private const float MergeRise = 0.4f;
        private const int MergeTaper = 4;
        // Distance between the cross sections of a road slab, and the largest gap between points across it.
        private const float Step = 1f;
        private const float CrossStep = 1f;
        // Border noise (Border). The full gravel width varies between the two widths over about BandScale meters along
        // the edge, the fade length between the two lengths over about FadeScale meters, and the edge moves by up to
        // half the edge amplitude over about EdgeScale meters.
        private const float ShoulderFullMin = 0.1f;
        private const float ShoulderFullMax = 2f;
        private const float ShoulderFadeMin = 1f;
        private const float ShoulderFadeMax = 3.5f;
        private const float BandScale = 16f;
        private const float FadeScale = 9f;
        private const float EdgeScale = 3.5f;
        private const float EdgeAmplitude = 2.5f;
        // Grass and objects stay this far outside the lip foot, so no blade grows through the lip.
        private const float ClearMargin = 0.4f;
        // Beyond the margin the clear fades out over a reach between these lengths, which patch noise changes over about
        // ClearPatchScale meters along the edge. Where the reach is short, sparse plants grow up to the lip; where it is
        // long, a bare strip stays.
        private const float ClearReachMin = 0.5f;
        private const float ClearReachMax = 3f;
        private const float ClearPatchScale = 6f;
        // The mask cell is at least this size. A larger network gets larger cells, so the masks stay in 4096 texels.
        private const float MinMaskCell = 0.5f;
        private const int MaxMaskSize = 4096;

        // A paved pad, or a junction patch where road ends meet (no Transform).
        private sealed class Pad
        {
            public string Name;
            public Transform Transform;
            public Vector3 Origin;
            public Vector2[] Outline;
            public Vector2 Center;
            public Rect Bounds;
            // The outline before the road mouths. A pad with road mouths also has the signed distance field of the
            // grown pad (negative inside), in MouthCell cells from FieldOrigin.
            public Vector2[] Base;
            public float[] Field;
            public Vector2 FieldOrigin;
            public int FieldWidth;
            public int FieldDepth;
            // A bridge deck: a road end meets it only at its end side, so the road edges do not run on under it.
            public bool Deck;
        }

        // The road paving over a bridge deck: a straight slab from end marker to end marker. The markers give the road
        // surface, so the slab top meets the road slabs at the deck ends. The slab lies on the deck top, Thickness
        // below the markers.
        private sealed class DeckSlab
        {
            public string Name;
            public Vector3 Start;
            public Vector3 Finish;
            public float Half;
            public float Seed;

            // The road surface at a point: the marker heights along the deck.
            public float Surface(Vector2 point)
            {
                Vector2 a = Flat(Start), span = Flat(Finish) - a;
                float t = Mathf.Clamp01(Vector2.Dot(point - a, span) / Mathf.Max(span.sqrMagnitude, 1e-6f));
                return Mathf.Lerp(Start.y, Finish.y, t);
            }
        }

        private struct Row
        {
            public Vector2 Center;
            public Vector2 Right;
            public float Half;
            public Vector2 Left;
            public Vector2 RightEdge;
            // Distance along the spline in meters and a random value of the spline, for the road coordinates of the
            // slab vertices. A split or trimmed piece keeps them.
            public float Along;
            public float Seed;
        }

        // One end of a road piece: a pad, a host road or nothing.
        private sealed class End
        {
            public Pad Pad;
            public Vector2 CutPoint;
            public Vector2 CutNormal;
            public int Host = -1;

            public float Cut(Vector2 point)
            {
                return Vector2.Dot(point - CutPoint, CutNormal) + RoadOverlap;
            }
        }

        private sealed class Piece
        {
            public int Index;
            public string Name;
            public List<Row> Rows;
            // The rows before the trim. The full quad index holds them.
            public Row[] Untrimmed;
            public readonly End[] Ends = { new End(), new End() };
        }

        // The topology of the paved network: the road pieces with their ends, trims and merges, the pads, the junction
        // patches and the quad indexes that the meshes read. It does not depend on the slab meshes.
        private sealed class Network
        {
            public UnityEngine.Terrain[] Tiles;
            public List<Piece> Pieces;
            public List<Pad> Pads;
            public List<Pad> Patches;
            // The pads, then the junction patches.
            public List<Pad> Slabs;
            // The deck ends of the bridges, which get the plant clear of a slab but no gravel.
            public List<Pad> DeckEnds;
            // The road paving over the bridge decks.
            public List<DeckSlab> DeckSlabs;
            public QuadIndex Full;
            public QuadIndex Final;
            public int Junctions;
            public int Merged;
        }

        // The vertices and triangles of one slab, relative to its origin, and the road coordinates of each vertex
        // (second UV set). On a road: distance across from the center line (right positive) and along the spline in
        // meters, the half width and a random value of the spline. On a pad or a junction patch: the distance inward
        // from the rim, zero, zero and a random value of the pad. The vertex color (red) marks a road side that runs
        // along a pad or a junction patch: the two read as one surface there, so the road wear puts no edge on it.
        private sealed class SlabMesh
        {
            public string Name;
            public Vector3 Origin;
            public List<Vector3> Vertices;
            public List<int> Triangles;
            public List<Vector4> Road;
            public List<Color32> Joins;
            // The first triangle index of each submesh. Each submesh lies on one terrain tile, so the renderer can
            // give it the terrain ground of that tile (LoogaRoadNetwork.ApplyGround).
            public List<int> SubMeshStarts;
        }

        // The road tops as quads, found through an 8 m grid. Plan reuses its three indexes: Reset keeps the cell lists
        // and the quad arrays, so a live update makes no new index garbage. An index stays valid until the next plan.
        private sealed class QuadIndex
        {
            private const float Cell = 8f;
            private readonly Dictionary<long, List<(int piece, Vector2[] quad)>> _cells =
                new Dictionary<long, List<(int piece, Vector2[] quad)>>();
            private readonly List<Vector2[]> _quads = new List<Vector2[]>();
            private int _usedQuads;

            private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

            // An empty cell list finds nothing, as a missing cell.
            public void Reset()
            {
                foreach (List<(int piece, Vector2[] quad)> list in _cells.Values) list.Clear();
                _usedQuads = 0;
            }

            public void Add(int piece, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
            {
                if (_usedQuads == _quads.Count) _quads.Add(new Vector2[4]);
                Vector2[] quad = _quads[_usedQuads++];
                quad[0] = a;
                quad[1] = b;
                quad[2] = c;
                quad[3] = d;
                Add(piece, quad);
            }

            public void Add(int piece, Vector2[] quad)
            {
                var extent = new Extent();
                foreach (Vector2 point in quad) extent.Add(point);
                float minX = extent.MinX, maxX = extent.MaxX, minZ = extent.MinY, maxZ = extent.MaxY;
                for (int x = Mathf.FloorToInt(minX / Cell); x <= Mathf.FloorToInt(maxX / Cell); x++)
                {
                    for (int z = Mathf.FloorToInt(minZ / Cell); z <= Mathf.FloorToInt(maxZ / Cell); z++)
                    {
                        if (!_cells.TryGetValue(Key(x, z), out var list)) _cells[Key(x, z)] = list = new List<(int,
                            Vector2[])>();
                        list.Add((piece, quad));
                    }
                }
            }

            // The first road piece whose top covers the point, other than the excluded piece. -1 when none covers it.
            public int Covering(Vector2 point, int exclude = -1, int only = -1)
            {
                if (!_cells.TryGetValue(Key(Mathf.FloorToInt(point.x / Cell), Mathf.FloorToInt(point.y / Cell)),
                    out var list)) return -1;
                foreach ((int piece, Vector2[] quad) in list)
                {
                    if (piece == exclude || (only >= 0 && piece != only)) continue;
                    if (InQuad(quad, point)) return piece;
                }
                return -1;
            }
        }

        // Bounds of points with the comparisons of Enumerable.Min and Max: NaN counts as the smallest value. LINQ
        // allocated enumerators and iterators for each bounds.
        private struct Extent
        {
            public float MinX, MinY, MaxX, MaxY;
            private bool _any;

            public void Add(Vector2 point)
            {
                if (!_any)
                {
                    MinX = MaxX = point.x;
                    MinY = MaxY = point.y;
                    _any = true;
                    return;
                }
                if (point.x < MinX || float.IsNaN(point.x)) MinX = point.x;
                if (point.y < MinY || float.IsNaN(point.y)) MinY = point.y;
                if (point.x > MaxX || float.IsNaN(MaxX)) MaxX = point.x;
                if (point.y > MaxY || float.IsNaN(MaxY)) MaxY = point.y;
            }
        }

        /// <summary>
        /// Builds the slabs of every paved road piece and paved pad, the gravel shoulder and the clear around them.
        /// The pad pavement stamps then end at the pad outlines.
        /// </summary>
        public static string Build(LoogaRoadNetwork roadNetwork)
        {
            _network = roadNetwork;
            var clock = Stopwatch.StartNew();
            Transform stampRoot = StampRoot();
            Scene scene = stampRoot.gameObject.scene;
            Network network = Plan(stampRoot, Tiles());
            Material material = RequireMaterial();

            Transform old = _network.SlabRoot;
            Transform parent = old ? old.parent : null;
            string rootName = old ? old.name : RootName;
            if (old)
            {
                Undo.DestroyObjectImmediate(old.gameObject);
            }
            var rootObject = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(rootObject, "Add road slabs");
            SceneManager.MoveGameObjectToScene(rootObject, scene);
            if (parent)
            {
                rootObject.transform.SetParent(parent, false);
            }
            Undo.RecordObject(_network, "Add road slabs");
            _network.SetSlabRoot(rootObject.transform);

            var report = new StringBuilder();
            int vertices = 0;
            foreach (SlabMesh slab in Meshes(network))
            {
                Mesh asset = SaveMesh(slab);
                Transform slabObject = AddSlab(rootObject.transform, slab.Name, asset, slab.Origin, material);
                LoogaRoadSlabLods.Apply(slabObject, asset, material, _network);
                vertices += slab.Vertices.Count;
            }
            List<Piece> pieces = network.Pieces;
            report.AppendLine(pieces.Count + " road pieces (" + pieces.Sum(piece => piece.Rows.Count).ToString("N0") +
                " m), " +
                network.Pads.Count + " pads, " + network.Patches.Count + " junction patches, " + network.Junctions +
                " road ends on another road, " +
                pieces.Sum(piece => piece.Ends.Count(end => end.Pad != null && end.Pad.Transform)) +
                    " road ends at pads, " +
                network.Merged + " road edge points merged with a nearby slab, " + vertices.ToString("N0") +
                    " vertices.");

            ConfigureMasks(stampRoot, network, report);
            foreach (Pad pad in network.Pads)
            {
                EndPadPavement(pad);
            }
            report.AppendLine("Pad pavement stamps end at the outline (boost 0.3, range 0.6, noise 0.2). Time " +
                (clock.ElapsedMilliseconds / 1000f).ToString("F1") + " s.");
            EditorSceneManager.MarkSceneDirty(scene);
            // Save only the slab meshes and the material. SaveAssets would also write unrelated unsaved assets.
            foreach (MeshFilter filter in rootObject.GetComponentsInChildren<MeshFilter>())
            {
                AssetDatabase.SaveAssetIfDirty(filter.sharedMesh);
            }
            AssetDatabase.SaveAssetIfDirty(material);
            return report.ToString();
        }

        // The stamp root of the network: its stamp world.
        private static Transform StampRoot()
        {
            LoogaStampWorld world = _network.World;
            return world ? world.transform : throw new MissingReferenceException(_network.name +
                " is not under a stamp world.");
        }

        // The terrains of the stamp world. The slabs stand on them.
        private static UnityEngine.Terrain[] Tiles()
        {
            return _network.World.Terrains.Where(terrain => terrain && terrain.isActiveAndEnabled).ToArray();
        }

        // The road topology: pad ends, junction patches, host roads, trims and merges. It is cheap 2D work on the road
        // rows (the merges sample a few heights), so it always runs for the whole network and the joins stay as in a
        // full build. Appends one line to Library/LoogaRoadTrace.log, so a stalled update shows the last stage that it
        // started.
        internal static void Trace(string stage)
        {
            _tracePath ??= Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "LoogaRoadTrace.log");
            try
            {
                File.AppendAllText(_tracePath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + stage + "\n");
            }
            catch (IOException)
            {
            }
        }

        private static string _tracePath;

        // The full, trimmed and final quad indexes of the last plan.
        private static readonly QuadIndex[] PlanIndexes = { new QuadIndex(), new QuadIndex(), new QuadIndex() };

        private static Network Plan(Transform stampRoot, UnityEngine.Terrain[] tiles)
        {
            Trace("plan: pads and pieces");
            List<Pad> pads = RequirePavedPads(stampRoot);
            List<Piece> pieces = CollectPieces(stampRoot);
            Trace("plan: " + pads.Count + " pads, " + pieces.Count + " pieces, pad ends");
            // Pads first: a road end at a pad takes the cut line of the nearest pad side.
            foreach (Piece piece in pieces)
            {
                for (int side = 0; side < 2; side++)
                {
                    Vector2 end = side == 0 ? piece.Rows[0].Center : piece.Rows[piece.Rows.Count - 1].Center;
                    Pad pad = pads.OrderBy(candidate => PadDistance(candidate, end)).FirstOrDefault();
                    if (pad == null || PadDistance(pad, end) > PadReach) continue;
                    piece.Ends[side].Pad = pad;
                    (piece.Ends[side].CutPoint, piece.Ends[side].CutNormal) = NearestSide(pad, end);
                }
            }
            // Bridges: a road end at a bridge end stops on the deck end like on a pad side, square and without an edge.
            // The deck is not a slab, so it only gives the cut line.
            List<Pad> decks = BridgeDecks();
            foreach (Piece piece in pieces)
            {
                for (int side = 0; side < 2; side++)
                {
                    if (piece.Ends[side].Pad != null) continue;
                    Vector2 end = side == 0 ? piece.Rows[0].Center : piece.Rows[piece.Rows.Count - 1].Center;
                    // The first deck with the smallest distance, as OrderBy (float.CompareTo) and FirstOrDefault.
                    Pad deck = null;
                    float nearest = 0f;
                    foreach (Pad candidate in decks)
                    {
                        float distance = PadDistance(candidate, end);
                        if (deck != null && distance.CompareTo(nearest) >= 0) continue;
                        deck = candidate;
                        nearest = distance;
                    }
                    if (deck == null || Mathf.Abs(PadDistance(deck, end)) > BridgeEndReach) continue;
                    piece.Ends[side].Pad = deck;
                    (piece.Ends[side].CutPoint, piece.Ends[side].CutNormal) = NearestSide(deck, end);
                }
            }
            Trace("plan: junction splits");
            SplitAtJunctions(pieces);
            Trace("plan: mouths");
            AddMouths(pads, pieces);
            Trace("plan: edges");
            // Each piece reads only its own rows and ends, so the pieces run in parallel.
            System.Threading.Tasks.Parallel.ForEach(pieces, SetEdges);
            Trace("plan: junction patches");
            List<Pad> patches = AddJunctionPatches(pieces, tiles);
            Trace("plan: edges again");
            System.Threading.Tasks.Parallel.ForEach(pieces, SetEdges);
            Trace("plan: hosts");
            // Junctions: a road end on the top of another road runs under that road. Of two ends that meet, the later
            // piece runs under the earlier one.
            QuadIndex full = PlanIndexes[0];
            full.Reset();
            foreach (Piece piece in pieces)
            {
                AddQuads(full, piece);
                piece.Untrimmed = piece.Rows.ToArray();
            }
            int junctions = 0;
            foreach (Piece piece in pieces)
            {
                for (int side = 0; side < 2; side++)
                {
                    if (piece.Ends[side].Pad != null) continue;
                    Row row = side == 0 ? piece.Rows[0] : piece.Rows[piece.Rows.Count - 1];
                    Vector2 forward = Forward(row) * (side == 0 ? -1f : 1f);
                    int host = full.Covering(row.Center + forward * JunctionReach, piece.Index);
                    if (host < 0) host = full.Covering(row.Center, piece.Index);
                    if (host < 0) continue;
                    Piece other = pieces[host];
                    bool otherEnds = Vector2.Distance(other.Rows[0].Center, row.Center) < 6f ||
                        Vector2.Distance(other.Rows[other.Rows.Count - 1].Center, row.Center) < 6f;
                    if (otherEnds && other.Index > piece.Index) continue;
                    piece.Ends[side].Host = host;
                    junctions++;
                }
            }
            Trace("plan: trims");
            foreach (Piece piece in pieces) Trim(piece, full);
            List<Pad> slabs = pads.Concat(patches).ToList();
            QuadIndex trimmed = PlanIndexes[1];
            trimmed.Reset();
            foreach (Piece piece in pieces) AddQuads(trimmed, piece);
            var hits = new float[pieces.Count][,];
            Trace("plan: merge probes");
            System.Threading.Tasks.Parallel.For(0, pieces.Count, index => hits[index] = ProbeMerges(pieces[index],
                trimmed, slabs));
            Trace("plan: merges");
            int merged = pieces.Sum(piece => Merge(piece, hits[piece.Index], tiles));
            Trace("plan: done");
            QuadIndex final = PlanIndexes[2];
            final.Reset();
            foreach (Piece piece in pieces) AddQuads(final, piece);
            return new Network
            {
                Tiles = tiles, Pieces = pieces, Pads = pads, Patches = patches, Slabs = slabs,
                    DeckEnds = BridgeDeckEnds(), DeckSlabs = BridgeDeckSlabs(),
                Full = full, Final = final, Junctions = junctions, Merged = merged
            };
        }

        // The slab of each road piece with at least two rows, then the slab of each pad and junction patch.
        private static IEnumerable<SlabMesh> Meshes(Network network)
        {
            foreach (Piece piece in network.Pieces)
            {
                SlabMesh mesh = BuildRoadMesh(network, piece);
                if (mesh != null)
                {
                    yield return mesh;
                }
            }
            foreach (Pad pad in network.Slabs)
            {
                yield return BuildPadMesh(network, pad);
            }
            foreach (DeckSlab deck in network.DeckSlabs)
            {
                yield return BuildDeckMesh(network, deck);
            }
        }

        #region Pads
        // The pads whose surface stamp paints the pavement layer.
        private static List<Pad> RequirePavedPads(Transform stampRoot)
        {
            Transform group = _network.PadRoot
                ? _network.PadRoot
                : throw new MissingReferenceException("Pad root of the road network " + _network.name + ".");
            var pads = new List<Pad>();
            foreach (Transform transform in group)
            {
                if (!transform.gameObject.activeInHierarchy) continue;
                var container = transform.GetComponent<SplineContainer>();
                bool paved = transform.GetComponentsInChildren<LoogaTextureStamp>()
                    .Any(stamp => _network.IsPavedLayer(stamp.Layer));
                if (!container || !paved) continue;
                // The outline follows the rounded corners of the spline. The center is the mean of the knots, so the
                // corner points do not move it.
                Vector2[] points = LoogaRoadPads.SampleOutline(container);
                Vector2[] knots = container.Spline.Select(knot => Flat(transform.TransformPoint(knot.Position)))
                    .ToArray();
                pads.Add(new Pad
                {
                    Name = transform.name, Transform = transform, Origin = transform.position, Outline = points,
                        Base = points,
                    Center = knots.Aggregate(Vector2.zero, (sum, point) => sum + point) / knots.Length,
                    Bounds = Rect.MinMaxRect(points.Min(p => p.x), points.Min(p => p.y), points.Max(p => p.x),
                        points.Max(p => p.y))
                });
            }
            return pads;
        }

        // The bridge decks as outlines (no Transform): the deck rectangle from end marker to end marker.
        private static List<Pad> BridgeDecks()
        {
            var decks = new List<Pad>();
            foreach ((string name, Vector3 start, Vector3 finish, float halfWidth) in LoogaRoadBridges.Decks(_network))
            {
                if (Vector2.Distance(Flat(start), Flat(finish)) < 1f) continue;
                decks.Add(DeckRectangle(name, start, finish, halfWidth));
            }
            return decks;
        }

        // The first DeckEndLength meters of each deck end (at most half the deck). The ground there is just below the
        // deck, so no plants grow through it. The ground under the rest of a deck keeps its plants.
        private static List<Pad> BridgeDeckEnds()
        {
            var ends = new List<Pad>();
            foreach ((string name, Vector3 start, Vector3 finish, float halfWidth) in LoogaRoadBridges.Decks(_network))
            {
                float span = Vector2.Distance(Flat(start), Flat(finish));
                if (span < 1f) continue;
                float share = Mathf.Min(DeckEndLength, span * 0.5f) / span;
                ends.Add(DeckRectangle(name + " start", start, Vector3.Lerp(start, finish, share), halfWidth));
                ends.Add(DeckRectangle(name + " finish", Vector3.Lerp(finish, start, share), finish, halfWidth));
            }
            return ends;
        }

        // The road paving of each bridge deck, as wide as the road on it.
        private static List<DeckSlab> BridgeDeckSlabs()
        {
            var slabs = new List<DeckSlab>();
            foreach ((string name, Vector3 start, Vector3 finish, float halfWidth) in LoogaRoadBridges.Decks(_network))
            {
                if (Vector2.Distance(Flat(start), Flat(finish)) < 1f) continue;
                slabs.Add(new DeckSlab
                {
                    Name = "Deck " + name, Start = start, Finish = finish,
                    Half = halfWidth - LoogaRoadBridges.DeckMargin, Seed = RandomValue("Deck " + name)
                });
            }
            return slabs;
        }

        private static Pad DeckRectangle(string name, Vector3 start, Vector3 finish, float halfWidth)
        {
            Vector2 a = Flat(start), b = Flat(finish);
            Vector2 forward = (b - a).normalized;
            Vector2 right = new Vector2(forward.y, -forward.x) * halfWidth;
            Vector2[] outline = { a - right, b - right, b + right, a + right };
            return new Pad
            {
                Name = name, Origin = (start + finish) * 0.5f, Outline = outline, Base = outline, Center = (a +
                    b) * 0.5f,
                Bounds = Rect.MinMaxRect(outline.Min(p => p.x), outline.Min(p => p.y), outline.Max(p => p.x),
                    outline.Max(p => p.y)),
                Deck = true
            };
        }

        // Signed distance to the pad: negative inside. A pad with road mouths reads its field. Other pads, and points
        // outside the field, use the convex outline before the mouths.
        private static float PadDistance(Pad pad, Vector2 point)
        {
            if (pad.Field != null &&
                TrySampleField(pad.Field, pad.FieldOrigin, pad.FieldWidth, pad.FieldDepth, point, out float distance))
            {
                return distance;
            }
            return ConvexDistance(pad.Base ?? pad.Outline, pad.Center, point);
        }

        // Signed distance to a convex outline: negative inside. Outside a corner it is a little too small.
        private static float ConvexDistance(Vector2[] outline, Vector2 center, Vector2 point)
        {
            float distance = float.MinValue;
            for (int index = 0; index < outline.Length; index++)
            {
                Vector2 a = outline[index];
                Vector2 b = outline[(index + 1) % outline.Length];
                distance = Mathf.Max(distance, Vector2.Dot(point - a, Outward(a, b, center)));
            }
            return distance;
        }

        // The side of the outline before the mouths nearest to a point: a point on the side and the outward normal.
        private static (Vector2 point, Vector2 normal) NearestSide(Pad pad, Vector2 point)
        {
            Vector2[] outline = pad.Base ?? pad.Outline;
            (Vector2 point, Vector2 normal) best = (outline[0], Vector2.up);
            float nearest = float.MaxValue;
            for (int index = 0; index < outline.Length; index++)
            {
                Vector2 a = outline[index];
                Vector2 b = outline[(index + 1) % outline.Length];
                float distance = SegmentDistance(point, a, b);
                if (distance >= nearest) continue;
                nearest = distance;
                best = (a, Outward(a, b, pad.Center));
            }
            return best;
        }

        private static Vector2 Outward(Vector2 a, Vector2 b, Vector2 center)
        {
            Vector2 direction = (b - a).normalized;
            var normal = new Vector2(direction.y, -direction.x);
            return Vector2.Dot(normal, (a + b) * 0.5f - center) < 0f ? -normal : normal;
        }

        // A top of rings from the center to the ragged outline, and a rounded edge around it. Where a road slab runs
        // onto the pad, the top ends on the exact outline and has no edge, so the road surface continues the pad
        // surface.
        private static SlabMesh BuildPadMesh(Network network, Pad pad, SlabMesh reuse = null)
        {
            UnityEngine.Terrain[] tiles = network.Tiles;
            QuadIndex roads = network.Final;
            List<Vector2> ring = RingScratch;
            List<Vector2> normals = OutwardScratch;
            List<float> joins = JoinScratch;
            ring.Clear();
            normals.Clear();
            joins.Clear();
            int sides = pad.Outline.Length;
            for (int index = 0; index < sides; index++)
            {
                Vector2 a = pad.Outline[index];
                Vector2 b = pad.Outline[(index + 1) % sides];
                Vector2 normal = Outward(a, b, pad.Center);
                Vector2 previous = Outward(pad.Outline[(index + sides - 1) % sides], a, pad.Center);
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / Step));
                for (int step = 0; step < steps; step++)
                {
                    Vector2 point = Vector2.Lerp(a, b, step / (float)steps);
                    // The corner point takes the mean normal of its two sides, so the edge turns around the corner.
                    Vector2 outward = step == 0 ? (normal + previous).normalized : normal;
                    float join = Mathf.SmoothStep(0f, 1f, (RoadGap(roads, point, outward) - 0.1f) / PadJoinBlend);
                    ring.Add(point + outward * ((EdgeOffset(point) - LipInset) * join));
                    normals.Add(outward);
                    joins.Add(join);
                }
            }
            Vector3 origin = pad.Origin;
            SlabMesh mesh = StartMesh(reuse, pad.Name, origin);
            List<Vector3> vertices = mesh.Vertices;
            List<int> triangles = mesh.Triangles;
            List<Vector4> road = mesh.Road;
            List<Color32> colors = mesh.Joins;
            float seed = RandomValue(pad.Name);
            vertices.Add(new Vector3(pad.Center.x, Height(tiles, pad.Center) + Thickness, pad.Center.y));
            // Enumerable.Max: NaN counts as the smallest value.
            float radius = Vector2.Distance(ring[0], pad.Center);
            for (int index = 1; index < ring.Count; index++)
            {
                float distance = Vector2.Distance(ring[index], pad.Center);
                if (distance > radius || float.IsNaN(radius)) radius = distance;
            }
            int rings = Mathf.Max(1, Mathf.CeilToInt(radius / PadRingStep));
            // The center is as far from the rim as the nearest ring point.
            float centerDepth = radius;
            foreach (Vector2 edge in ring) centerDepth = Mathf.Min(centerDepth, Vector2.Distance(edge, pad.Center));
            road.Add(new Vector4(centerDepth, 0f, 0f, seed));
            for (int level = 1; level <= rings; level++)
            {
                float t = level / (float)rings;
                // The undulation starts one ring step inside the rim, so the rim and a road under it stay level.
                float fade = Mathf.SmoothStep(0f, 1f, ((1f - t) * radius - PadRingStep) / UndulationFade);
                for (int index = 0; index < ring.Count; index++)
                {
                    Vector2 edge = ring[index];
                    Vector2 point = Vector2.Lerp(pad.Center, edge, t);
                    vertices.Add(new Vector3(point.x, Height(tiles, point) + Thickness + Wave(point) * fade, point.y));
                    // Where a road joins the rim, the road surface continues, so the rim is no edge for the wear.
                    float depth = (1f - t) * Vector2.Distance(edge, pad.Center) + (1f - joins[index]) * JoinedRimDepth;
                    road.Add(new Vector4(depth, 0f, 0f, seed));
                }
            }
            for (int index = 0; index < vertices.Count; index++) vertices[index] -= origin;
            for (int index = 0; index < ring.Count; index++)
            {
                AddTriangle(triangles, vertices, 0, 1 + index, 1 + (index + 1) % ring.Count);
            }
            for (int level = 1; level < rings; level++)
            {
                int inner = 1 + (level - 1) * ring.Count;
                int outer = inner + ring.Count;
                for (int index = 0; index < ring.Count; index++)
                {
                    int next = (index + 1) % ring.Count;
                    AddQuad(triangles, vertices, inner + index, inner + next, outer + next, outer + index);
                }
            }
            List<int> rim = RimScratch;
            rim.Clear();
            for (int index = 0; index < ring.Count; index++) rim.Add(1 + (rings - 1) * ring.Count + index);
            Vector3 offset = origin;
            for (int index = colors.Count; index < vertices.Count; index++) colors.Add(default);
            AddRoundedLip(vertices, triangles, road, colors, rim, normals, point => roads.Covering(point) >= 0, tiles,
                offset);
            SplitByTerrain(mesh, tiles);
            return mesh;
        }

        // The distance outward from a pad rim point to the first road top, up to 3 m.
        private static float RoadGap(QuadIndex roads, Vector2 point, Vector2 outward)
        {
            for (float distance = 0f; distance < 3f; distance += 0.1f)
            {
                if (roads.Covering(point + outward * distance) >= 0) return distance;
            }
            return 3f;
        }
        #endregion

        #region Junctions
        // Road ends without a pad that lie within NodeReach of each other form a node. The patch covers the convex hull
        // of the road cross sections PatchOverlap back from each end, so every road runs that far under the patch, and
        // the road tops FilletArm along each road, closed with FilletRadius, so the corners between the roads are
        // round.
        private static List<Pad> AddJunctionPatches(List<Piece> pieces, UnityEngine.Terrain[] tiles)
        {
            var ends = new List<(Piece piece, int side)>();
            foreach (Piece piece in pieces)
            {
                for (int side = 0; side < 2; side++)
                {
                    if (piece.Ends[side].Pad == null) ends.Add((piece, side));
                }
            }
            Vector2 EndPoint((Piece piece, int side) end) =>
                end.side == 0 ? end.piece.Rows[0].Center : end.piece.Rows[end.piece.Rows.Count - 1].Center;
            var group = Enumerable.Range(0, ends.Count).ToArray();
            int Root(int index) => group[index] == index ? index : group[index] = Root(group[index]);
            for (int a = 0; a < ends.Count; a++)
            {
                for (int b = a + 1; b < ends.Count; b++)
                {
                    if (ends[a].piece == ends[b].piece) continue;
                    if (Vector2.Distance(EndPoint(ends[a]), EndPoint(ends[b])) < NodeReach) group[Root(a)] = Root(b);
                }
            }
            var patches = new List<Pad>();
            foreach (var node in Enumerable.Range(0, ends.Count).GroupBy(Root).Where(node => node.Count() > 1))
            {
                var points = new List<Vector2>();
                foreach (int index in node)
                {
                    (Piece piece, int side) = ends[index];
                    int back = Mathf.Min(Mathf.RoundToInt(PatchOverlap / Step), piece.Rows.Count - 1);
                    Row row = side == 0 ? piece.Rows[back] : piece.Rows[piece.Rows.Count - 1 - back];
                    points.Add(row.Center - row.Right * row.Half);
                    points.Add(row.Center + row.Right * row.Half);
                }
                Vector2[] hull = ConvexHull(points);
                Vector2 center = hull.Aggregate(Vector2.zero, (sum, point) => sum + point) / hull.Length;
                var patch = new Pad
                {
                    Name = "Junction " + (patches.Count + 1), Base = hull, Center = center,
                    Origin = new Vector3(center.x, Height(tiles, center), center.y)
                };
                // The road tops near the node, closed with the fillet radius, so the corners between the roads are
                // round.
                var arms = new List<Vector2[]>();
                foreach (int index in node)
                {
                    (Piece piece, int side) = ends[index];
                    int count = piece.Rows.Count;
                    int reach = Mathf.Min(count - 1, Mathf.RoundToInt(FilletArm / Step));
                    for (int step = 0; step < reach; step++)
                    {
                        Row a = piece.Rows[side == 0 ? step : count - 1 - step];
                        Row b = piece.Rows[side == 0 ? step + 1 : count - 2 - step];
                        float halfA = a.Half + LipInset, halfB = b.Half + LipInset;
                        arms.Add(new[]
                        {
                            a.Center - a.Right * halfA, a.Center + a.Right * halfA, b.Center + b.Right * halfB,
                            b.Center - b.Right * halfB
                        });
                    }
                }
                ulong filletKey = FilletKey(patch, arms);
                if (!FilletCache.TryGetValue(filletKey, out Mouth fillet))
                {
                    FilletCache[filletKey] = fillet = BuildMouth(patch, arms, filletKey, FilletRadius);
                }
                UsedFillets.Add(filletKey);
                patch.Outline = fillet.Outline;
                patch.Field = fillet.Field;
                patch.FieldOrigin = fillet.Origin;
                patch.FieldWidth = fillet.Width;
                patch.FieldDepth = fillet.Depth;
                patch.Bounds = Rect.MinMaxRect(patch.Outline.Min(q => q.x), patch.Outline.Min(q => q.y),
                    patch.Outline.Max(q => q.x), patch.Outline.Max(q => q.y));
                patches.Add(patch);
                foreach (int index in node)
                {
                    (Piece piece, int side) = ends[index];
                    End end = piece.Ends[side];
                    end.Pad = patch;
                    (end.CutPoint, end.CutNormal) = NearestSide(patch, EndPoint(ends[index]));
                }
            }
            // Only the fillets of the current junctions stay in the cache.
            foreach (ulong key in FilletCache.Keys)
            {
                if (!UsedFillets.Contains(key)) StaleFillets.Add(key);
            }
            foreach (ulong key in StaleFillets) FilletCache.Remove(key);
            StaleFillets.Clear();
            UsedFillets.Clear();
            return patches;
        }

        // The fillets of earlier plans by the hash of their inputs. BuildMouth reads only the patch hull, its center,
        // the arms and the fillet radius. An unchanged junction reuses its fillet, so a live update does not build
        // every fillet again (about ten distance transforms each).
        private static readonly Dictionary<ulong, Mouth> FilletCache = new Dictionary<ulong, Mouth>();
        private static readonly HashSet<ulong> UsedFillets = new HashSet<ulong>();
        private static readonly List<ulong> StaleFillets = new List<ulong>();

        private static ulong FilletKey(Pad patch, List<Vector2[]> arms)
        {
            var hash = Hash.Start();
            hash.Add(FilletRadius);
            hash.Add(patch.Center);
            hash.Add(patch.Base.Length);
            foreach (Vector2 point in patch.Base) hash.Add(point);
            hash.Add(arms.Count);
            foreach (Vector2[] quad in arms)
            {
                foreach (Vector2 point in quad) hash.Add(point);
            }
            return hash.Value;
        }

        // A road end on the body of another road splits the slab pieces of that road there, so the junction becomes a
        // node of road ends with one rounded patch (AddJunctionPatches). The road splines stay whole.
        private static void SplitAtJunctions(List<Piece> pieces)
        {
            for (int index = 0; index < pieces.Count; index++)
            {
                Piece piece = pieces[index];
                for (int side = 0; side < 2; side++)
                {
                    if (piece.Ends[side].Pad != null) continue;
                    Vector2 end = side == 0 ? piece.Rows[0].Center : piece.Rows[piece.Rows.Count - 1].Center;
                    for (int hostIndex = 0; hostIndex < pieces.Count; hostIndex++)
                    {
                        Piece host = pieces[hostIndex];
                        if (host == piece) continue;
                        int nearest = -1;
                        float distance = float.MaxValue;
                        for (int row = 0; row < host.Rows.Count; row++)
                        {
                            float candidate = Vector2.Distance(host.Rows[row].Center, end);
                            if (candidate >= distance) continue;
                            distance = candidate;
                            nearest = row;
                        }
                        if (distance > host.Rows[nearest].Half + JunctionReach) continue;
                        // An end near an end of the host is a node already.
                        if (nearest * Step < NodeReach || (host.Rows.Count - 1 - nearest) * Step < NodeReach) continue;
                        var first = new Piece { Name = host.Name + " a", Rows = host.Rows.GetRange(0, nearest + 1) };
                        var second = new Piece
                        {
                            Name = host.Name + " b", Rows = host.Rows.GetRange(nearest, host.Rows.Count - nearest)
                        };
                        first.Ends[0] = host.Ends[0];
                        second.Ends[1] = host.Ends[1];
                        pieces[hostIndex] = first;
                        pieces.Insert(hostIndex + 1, second);
                        if (hostIndex < index) index++;
                        break;
                    }
                }
            }
            for (int index = 0; index < pieces.Count; index++) pieces[index].Index = index;
        }

        // Andrew's monotone chain. The hull runs counterclockwise without repeated points.
        private static Vector2[] ConvexHull(List<Vector2> points)
        {
            var sorted = points.Distinct().OrderBy(point => point.x).ThenBy(point => point.y).ToList();
            if (sorted.Count < 3) return sorted.ToArray();
            float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            var hull = new List<Vector2>();
            foreach (Vector2 point in sorted)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1],
                    point) <= 0f) hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }
            int lower = hull.Count + 1;
            for (int index = sorted.Count - 2; index >= 0; index--)
            {
                Vector2 point = sorted[index];
                while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1],
                    point) <= 0f) hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }
            hull.RemoveAt(hull.Count - 1);
            return hull.ToArray();
        }
        #endregion

        #region Roads
        // The paved road pieces with cross sections every Step meters along each spline. The graded roads come before
        // the roads that follow the ground, in hierarchy order. The junction names and the shared road ends depend on
        // this order.
        private static List<Piece> CollectPieces(Transform stampRoot)
        {
            var pieces = new List<Piece>();
            foreach (LoogaRoad road in _network.GetRoads().OrderBy(road => road.FollowGround))
            {
                LoogaRoadClass roadClass = road.RoadClass;
                if (roadClass == null || !roadClass.Paved || !road.gameObject.activeInHierarchy) continue;
                string name = road.name;
                SplineContainer container = road.Container;
                LoogaSplinePath path = road.Path;
                for (int index = 0; index < container.Splines.Count; index++)
                {
                    // A copy has new length caches. After an undo, the scene spline keeps the caches of the undone
                    // shape (Unity Splines), and its positions can be off by about a meter.
                    var spline = new Spline(container.Splines[index]);
                    float length = SplineUtility.CalculateLength(spline, container.transform.localToWorldMatrix);
                    int count = Mathf.Max(2, Mathf.CeilToInt(length / Step) + 1);
                    float seed = RandomValue(name + " " + index);
                    var rows = new List<Row>(count);
                    for (int step = 0; step < count; step++)
                    {
                        float t = step / (float)(count - 1);
                        Vector3 center = container.transform.TransformPoint(spline.EvaluatePosition(t));
                        Vector2 forward =
                            Flat(container.transform.TransformDirection(spline.EvaluateTangent(t))).normalized;
                        // The pad mouths widen a road toward its pad, so the slab does not use the flare widths of the
                        // path.
                        rows.Add(new Row
                        {
                            Center = Flat(center), Right = new Vector2(forward.y, -forward.x), Half = path.Width,
                            Along = t * length, Seed = seed
                        });
                    }
                    pieces.Add(new Piece { Index = pieces.Count, Name = name + " " + index, Rows = rows });
                }
            }
            return pieces;
        }

        private static Vector2 Forward(Row row) => new Vector2(-row.Right.y, row.Right.x);

        // The top edge points of each row: LipInset inside the ragged outline. A side whose edge would reach a pad at
        // an end runs on under that pad instead. Inside a pad the points move along the road onto the cut line.
        private static void SetEdges(Piece piece)
        {
            for (int index = 0; index < piece.Rows.Count; index++)
            {
                Row row = piece.Rows[index];
                row.Left = Edge(piece, row, -1f);
                row.RightEdge = Edge(piece, row, 1f);
                piece.Rows[index] = row;
            }
        }

        private static Vector2 Edge(Piece piece, Row row, float sign)
        {
            Vector2 side = row.Right * sign;
            Vector2 point = row.Center + side * (row.Half + EdgeOffset(row.Center + side * row.Half) - LipInset);
            foreach (End end in piece.Ends)
            {
                // At a pad with road mouths the road runs on under the grown pad, so its edges keep their place.
                if (end.Pad == null || end.Pad.Field != null) continue;
                if (!end.Pad.Deck) point = UnderPad(end.Pad, point, side);
                float cut = end.Cut(point);
                float across = Vector2.Dot(Forward(row), end.CutNormal);
                if (cut < 0f && PadDistance(end.Pad, point) < 0f && Mathf.Abs(across) > 0.2f)
                {
                    point -= Forward(row) * (cut / across);
                }
            }
            return point;
        }

        // When the edge of a side would reach the pad, the side moves on until it is RoadOverlap inside the pad.
        private static Vector2 UnderPad(Pad pad, Vector2 edge, Vector2 side)
        {
            if (PadDistance(pad, edge + side * (LipInset + LipWidthAt(edge))) > 0f) return edge;
            for (float extra = 0f; extra < 4f; extra += 0.05f)
            {
                if (PadDistance(pad, edge + side * extra) < -RoadOverlap) return edge + side * extra;
            }
            return edge + side * 4f;
        }

        // Rows that lie fully under a pad or a host road are left out, but the last of them stays, so the slab still
        // runs under the other slab.
        private static void Trim(Piece piece, QuadIndex full)
        {
            bool Deep(int index, End end)
            {
                Row row = piece.Rows[index];
                if (end.Pad != null && end.Pad.Field != null)
                {
                    return PadDistance(end.Pad, row.Left) < -MouthDepth && PadDistance(end.Pad,
                        row.RightEdge) < -MouthDepth;
                }
                if (end.Pad != null)
                {
                    return end.Cut(row.Left) < 0.01f && end.Cut(row.RightEdge) < 0.01f && PadDistance(end.Pad,
                        row.Center) < 0f;
                }
                return end.Host >= 0 && full.Covering(row.Left, -1, end.Host) >= 0 && full.Covering(row.RightEdge, -1,
                    end.Host) >= 0;
            }
            for (int side = 0; side < 2; side++)
            {
                End end = piece.Ends[side];
                if (end.Pad == null && end.Host < 0) continue;
                int count = piece.Rows.Count;
                int deep = 0;
                while (deep < count - 2 && Deep(side == 0 ? deep : count - 1 - deep, end) &&
                    Deep(side == 0 ? deep + 1 : count - 2 - deep, end))
                {
                    deep++;
                }
                if (deep > 0) piece.Rows.RemoveRange(side == 0 ? 0 : count - deep, deep);
            }
        }

        private static void AddQuads(QuadIndex index, Piece piece)
        {
            for (int row = 0; row + 1 < piece.Rows.Count; row++)
            {
                Row a = piece.Rows[row], b = piece.Rows[row + 1];
                index.Add(piece.Index, a.Left, a.RightEdge, b.RightEdge, b.Left);
            }
        }

        // A reuse mesh gives its lists: the live update saves each slab at once, so one set of lists serves every slab.
        private static SlabMesh BuildRoadMesh(Network network, Piece piece, SlabMesh reuse = null)
        {
            UnityEngine.Terrain[] tiles = network.Tiles;
            QuadIndex full = network.Full;
            QuadIndex final = network.Final;
            List<Pad> slabs = network.Slabs;
            List<Row> rows = piece.Rows;
            if (rows.Count < 2) return null;
            var origin = new Vector3(rows[0].Center.x, Height(tiles, rows[0].Center), rows[0].Center.y);
            float widest = rows[0].Half;
            for (int index = 1; index < rows.Count; index++)
            {
                if (rows[index].Half > widest || float.IsNaN(widest)) widest = rows[index].Half;
            }
            int columns = Mathf.Max(2, Mathf.CeilToInt(2f * widest / CrossStep)) + 1;
            SlabMesh mesh = StartMesh(reuse, piece.Name, origin);
            List<Vector3> vertices = mesh.Vertices;
            List<int> triangles = mesh.Triangles;
            List<Vector4> road = mesh.Road;
            List<Color32> joins = mesh.Joins;
            foreach (Row row in rows)
            {
                var left = new Color32(JoinsSlab(slabs, row.Left, -row.Right) ? (byte)255 : (byte)0, 0, 0, 0);
                var right = new Color32(JoinsSlab(slabs, row.RightEdge, row.Right) ? (byte)255 : (byte)0, 0, 0, 0);
                for (int column = 0; column < columns; column++)
                {
                    Vector2 point = Vector2.Lerp(row.Left, row.RightEdge, column / (float)(columns - 1));
                    vertices.Add(Lift(piece, tiles, full, final, slabs, point) - origin);
                    road.Add(new Vector4(Vector2.Dot(point - row.Center, row.Right), row.Along, row.Half, row.Seed));
                    joins.Add(2 * column < columns - 1 ? left : right);
                }
            }
            for (int row = 0; row + 1 < rows.Count; row++)
            {
                for (int column = 0; column + 1 < columns; column++)
                {
                    int a = row * columns + column;
                    AddQuad(triangles, vertices, a, a + 1, a + columns + 1, a + columns);
                }
            }

            // One edge loop around the slab: the left side, the far end, the right side back and the near end. The
            // corner points take the mean of the side and end directions, so the corners are round.
            List<int> rim = RimScratch;
            List<Vector2> outward = OutwardScratch;
            rim.Clear();
            outward.Clear();
            int last = rows.Count - 1;
            for (int row = 0; row <= last; row++)
            {
                Vector2 side = -rows[row].Right;
                if (row == 0) side = (side - Forward(rows[0])).normalized;
                if (row == last) side = (side + Forward(rows[last])).normalized;
                rim.Add(row * columns);
                outward.Add(side);
            }
            for (int column = 1; column < columns - 1; column++)
            {
                rim.Add(last * columns + column);
                outward.Add(Forward(rows[last]));
            }
            for (int row = last; row >= 0; row--)
            {
                Vector2 side = rows[row].Right;
                if (row == last) side = (side + Forward(rows[last])).normalized;
                if (row == 0) side = (side - Forward(rows[0])).normalized;
                rim.Add(row * columns + columns - 1);
                outward.Add(side);
            }
            for (int column = columns - 2; column >= 1; column--)
            {
                rim.Add(column);
                outward.Add(-Forward(rows[0]));
            }
            // No edge where another slab covers this one: on a pad cut line, under a pad, under a host road, or where a
            // joining road covers this road.
            AddRoundedLip(vertices, triangles, road, joins, rim, outward,
                point => RoadLipCovered(piece, slabs, final, point), tiles, origin);
            SplitByTerrain(mesh, tiles);
            return mesh;
        }

        // The road paving of a bridge deck: rows across the deck from end marker to end marker, at the road surface,
        // without undulation. The rounded edge is narrow, asphalt to the end, and ends on the deck top inside the
        // railings. The sides count as joined (vertex color red), so the road wear puts no ground band on them: the
        // ground far below the deck does not show on it. The deck ends get no edge: the road slabs meet them there.
        private static SlabMesh BuildDeckMesh(Network network, DeckSlab deck, SlabMesh reuse = null)
        {
            Vector2 a = Flat(deck.Start), b = Flat(deck.Finish);
            float length = Vector2.Distance(a, b);
            Vector2 forward = (b - a) / length;
            var right = new Vector2(forward.y, -forward.x);
            int count = Mathf.Max(2, Mathf.CeilToInt(length / Step) + 1);
            int columns = Mathf.Max(2, Mathf.CeilToInt(2f * deck.Half / CrossStep)) + 1;
            var origin = new Vector3(a.x, deck.Start.y, a.y);
            SlabMesh mesh = StartMesh(reuse, deck.Name, origin);
            List<Vector3> vertices = mesh.Vertices;
            List<Vector4> road = mesh.Road;
            for (int row = 0; row < count; row++)
            {
                float along = row * length / (count - 1);
                Vector2 center = a + forward * along;
                Vector2 left = center - right * (deck.Half + EdgeOffset(center - right * deck.Half) - LipInset);
                Vector2 rightEdge = center + right * (deck.Half + EdgeOffset(center + right * deck.Half) - LipInset);
                for (int column = 0; column < columns; column++)
                {
                    Vector2 point = Vector2.Lerp(left, rightEdge, column / (float)(columns - 1));
                    vertices.Add(new Vector3(point.x, deck.Surface(point), point.y) - origin);
                    road.Add(new Vector4(Vector2.Dot(point - center, right), along, deck.Half, deck.Seed));
                    mesh.Joins.Add(new Color32(255, 0, 0, 0));
                }
            }
            for (int row = 0; row + 1 < count; row++)
            {
                for (int column = 0; column + 1 < columns; column++)
                {
                    int corner = row * columns + column;
                    AddQuad(mesh.Triangles, vertices, corner, corner + 1, corner + columns + 1, corner + columns);
                }
            }
            // The edge loop as on a road: the left side, the far end, the right side back and the near end.
            List<int> rim = RimScratch;
            List<Vector2> outward = OutwardScratch;
            rim.Clear();
            outward.Clear();
            int last = count - 1;
            for (int row = 0; row <= last; row++)
            {
                rim.Add(row * columns);
                outward.Add(-right);
            }
            for (int column = 1; column < columns - 1; column++)
            {
                rim.Add(last * columns + column);
                outward.Add(forward);
            }
            for (int row = last; row >= 0; row--)
            {
                rim.Add(row * columns + columns - 1);
                outward.Add(right);
            }
            for (int column = columns - 2; column >= 1; column--)
            {
                rim.Add(column);
                outward.Add(-forward);
            }
            AddRoundedLip(vertices, mesh.Triangles, road, mesh.Joins, rim, outward, point =>
                {
                    float along = Vector2.Dot(point - a, forward);
                    return along < 0.5f || along > length - 0.5f;
                }, network.Tiles, origin, point => deck.Surface(point) - Thickness, LipWidthMin, false);
            SplitByTerrain(mesh, network.Tiles);
            return mesh;
        }

        // No edge where another slab covers the road: on a pad cut line, under a pad, under a host road, or where a
        // joining road covers it. A loop, not Any with a lambda, which allocated for each rim point.
        // True when a pad or a junction patch lies just outside a road edge point, so the road side runs along it.
        private static bool JoinsSlab(List<Pad> slabs, Vector2 edge, Vector2 side)
        {
            return InsideAny(slabs, edge + side * (LipInset + 0.75f), 0.5f);
        }

        private static bool RoadLipCovered(Piece piece, List<Pad> slabs, QuadIndex final, Vector2 point)
        {
            foreach (End end in piece.Ends)
            {
                if (end.Pad != null &&
                    ((end.Pad.Field == null && end.Cut(point) < 0.05f) || PadDistance(end.Pad, point) < 0.2f))
                {
                    return true;
                }
            }
            return InsideAny(slabs, point, 0.2f) || final.Covering(point, piece.Index) >= 0;
        }

        // Scratch of the mesh builders on the main thread. A builder uses them only during its call.
        private static readonly List<int> RimScratch = new List<int>();
        private static readonly List<Vector2> OutwardScratch = new List<Vector2>();
        private static readonly List<Vector2> RingScratch = new List<Vector2>();
        private static readonly List<Vector2> UvScratch = new List<Vector2>();
        private static readonly List<Vector4> RoadScratch = new List<Vector4>();
        private static readonly List<Color32> JoinsScratch = new List<Color32>();
        private static readonly List<float> JoinScratch = new List<float>();
        private static readonly List<int> TileScratch = new List<int>();
        private static readonly List<int> SortScratch = new List<int>();
        private static int[] _lipRings = Array.Empty<int>();

        private static SlabMesh NewSlabMesh()
        {
            return new SlabMesh
            {
                Vertices = new List<Vector3>(), Triangles = new List<int>(), Road = new List<Vector4>(),
                Joins = new List<Color32>(), SubMeshStarts = new List<int>()
            };
        }

        // A random value in [0, 1) from a name, the same in every build.
        private static float RandomValue(string name)
        {
            Hash hash = Hash.Start();
            hash.Add(name);
            return (hash.Value % 65521UL) / 65521f;
        }

        // Orders the triangles by the terrain tile under their center, one submesh for each tile.
        private static void SplitByTerrain(SlabMesh mesh, UnityEngine.Terrain[] tiles)
        {
            List<int> triangles = mesh.Triangles;
            List<int> tileOfTriangle = TileScratch;
            List<int> sorted = SortScratch;
            tileOfTriangle.Clear();
            sorted.Clear();
            int lowest = int.MaxValue;
            int highest = int.MinValue;
            for (int index = 0; index < triangles.Count; index += 3)
            {
                Vector3 center = (mesh.Vertices[triangles[index]] + mesh.Vertices[triangles[index + 1]] +
                    mesh.Vertices[triangles[index + 2]]) / 3f + mesh.Origin;
                int tile = LoogaTerrainGroundBlock.FindTerrain(center, tiles);
                tileOfTriangle.Add(tile);
                lowest = Mathf.Min(lowest, tile);
                highest = Mathf.Max(highest, tile);
            }
            mesh.SubMeshStarts.Clear();
            for (int tile = lowest; tile <= highest; tile++)
            {
                int start = sorted.Count;
                for (int triangle = 0; triangle < tileOfTriangle.Count; triangle++)
                {
                    if (tileOfTriangle[triangle] != tile) continue;
                    sorted.Add(triangles[triangle * 3]);
                    sorted.Add(triangles[triangle * 3 + 1]);
                    sorted.Add(triangles[triangle * 3 + 2]);
                }
                if (sorted.Count > start) mesh.SubMeshStarts.Add(start);
            }
            if (mesh.SubMeshStarts.Count == 0) mesh.SubMeshStarts.Add(0);
            triangles.Clear();
            triangles.AddRange(sorted);
        }

        private static SlabMesh StartMesh(SlabMesh reuse, string name, Vector3 origin)
        {
            SlabMesh mesh = reuse ?? NewSlabMesh();
            mesh.Vertices.Clear();
            mesh.Triangles.Clear();
            mesh.Road.Clear();
            mesh.Joins.Clear();
            mesh.SubMeshStarts.Clear();
            mesh.Name = name;
            mesh.Origin = origin;
            return mesh;
        }

        // A road point under a pad, a junction patch, a host road or an earlier road stays a little lower than the
        // other top. The undulation fades out toward a pad at an end of the road.
        private static Vector3 Lift(Piece piece, UnityEngine.Terrain[] tiles, QuadIndex full, QuadIndex final,
            List<Pad> slabs,
            Vector2 point)
        {
            float lower = 0f;
            float fade = 1f;
            int other = final.Covering(point, piece.Index);
            if ((other >= 0 && other < piece.Index) || InsideAny(slabs, point, -0.01f)) lower = Under;
            foreach (End end in piece.Ends)
            {
                if (end.Pad != null)
                {
                    float distance = PadDistance(end.Pad, point);
                    if (distance < -0.01f) lower = Under;
                    fade = Mathf.Min(fade, Mathf.SmoothStep(0f, 1f, distance / UndulationFade));
                }
                else if (end.Host >= 0 && full.Covering(point, -1, end.Host) >= 0)
                {
                    lower = Under;
                }
            }
            return new Vector3(point.x, Height(tiles, point) + Thickness - lower + Wave(point) * fade, point.y);
        }

        // The distance from each side edge point to the first slab that it faces, or -1. The probes read only the road
        // quads and the pad outlines, so pieces run in parallel. The merge checks the heights on the main thread.
        private static float[,] ProbeMerges(Piece piece, QuadIndex trimmed, List<Pad> slabs)
        {
            int count = piece.Rows.Count;
            var hits = new float[2, count];
            for (int index = 0; index < count; index++)
            {
                Row row = piece.Rows[index];
                for (int side = 0; side < 2; side++)
                {
                    hits[side, index] = -1f;
                    Vector2 edge = side == 0 ? row.Left : row.RightEdge;
                    Vector2 direction = side == 0 ? -row.Right : row.Right;
                    // An edge that is already under a slab stays.
                    if (InsideAny(slabs, edge, 0f)) continue;
                    for (float distance = 0.1f; distance <= MergeGap + LipInset; distance += 0.1f)
                    {
                        Vector2 probe = edge + direction * distance;
                        int other = trimmed.Covering(probe, piece.Index);
                        bool hit = (other >= 0 && other < piece.Index) || InsideAny(slabs, probe, 0f);
                        if (!hit && other >= 0) break;
                        if (!hit) continue;
                        hits[side, index] = distance;
                        break;
                    }
                }
            }
            return hits;
        }

        // Moves the side edge points that face a nearby slab across the gap. A merged stretch tapers out over
        // MergeTaper rows at each end, so the outline has no step. Returns the number of merged points.
        private static int Merge(Piece piece, float[,] hits, UnityEngine.Terrain[] tiles)
        {
            int count = piece.Rows.Count;
            var reach = new float[2, count];
            int moved = 0;
            for (int index = 0; index < count; index++)
            {
                Row row = piece.Rows[index];
                for (int side = 0; side < 2; side++)
                {
                    float distance = hits[side, index];
                    if (distance < 0f) continue;
                    Vector2 edge = side == 0 ? row.Left : row.RightEdge;
                    Vector2 probe = edge + (side == 0 ? -row.Right : row.Right) * distance;
                    if (Mathf.Abs(Height(tiles, probe) - Height(tiles, edge)) < MergeRise)
                    {
                        reach[side, index] = distance + MergeOverlap;
                        moved++;
                    }
                }
            }
            for (int index = 0; index < count; index++)
            {
                Row row = piece.Rows[index];
                for (int side = 0; side < 2; side++)
                {
                    float extra = 0f;
                    for (int other = Mathf.Max(0, index - MergeTaper); other <= Mathf.Min(count - 1, index +
                        MergeTaper); other++)
                    {
                        float taper = Mathf.SmoothStep(0f, 1f, 1f - Mathf.Abs(other - index) / (float)(MergeTaper + 1));
                        extra = Mathf.Max(extra, reach[side, other] * taper);
                    }
                    if (extra <= 0f) continue;
                    if (side == 0) row.Left -= row.Right * extra;
                    else row.RightEdge += row.Right * extra;
                }
                piece.Rows[index] = row;
            }
            return moved;
        }

        private static bool InsideAny(List<Pad> slabs, Vector2 point, float margin)
        {
            foreach (Pad pad in slabs)
            {
                if (point.x < pad.Bounds.xMin - 1f || point.x > pad.Bounds.xMax + 1f ||
                    point.y < pad.Bounds.yMin - 1f || point.y > pad.Bounds.yMax + 1f) continue;
                if (PadDistance(pad, point) < margin) return true;
            }
            return false;
        }

        #endregion

        #region Mouths
        // A grown pad: the outline and the signed distance field of the closing, and the key of the inputs.
        private sealed class Mouth
        {
            public ulong Key;
            public Vector2[] Outline;
            public float[] Field;
            public Vector2 Origin;
            public int Width;
            public int Depth;
        }

        // The mouths of earlier plans by pad name. A pad whose outline and road ends did not change reuses its mouth,
        // so a live update computes only the mouths of the changed pads.
        private static readonly Dictionary<string, Mouth> MouthCache = new Dictionary<string, Mouth>();

        // Grows each paved pad over the roads that end at it. A pad without road ends keeps its outline.
        private static void AddMouths(List<Pad> pads, List<Piece> pieces)
        {
            var quads = pads.ToDictionary(pad => pad, pad => new List<Vector2[]>());
            foreach (Piece piece in pieces)
            {
                for (int side = 0; side < 2; side++)
                {
                    // Bridge decks are not in the pad list and get no mouth.
                    Pad pad = piece.Ends[side].Pad;
                    if (pad != null && quads.TryGetValue(pad, out List<Vector2[]> list)) list.AddRange(MouthQuads(piece,
                        side, pad));
                }
            }
            var mouths = new Mouth[pads.Count];
            System.Threading.Tasks.Parallel.For(0, pads.Count, index =>
            {
                Pad pad = pads[index];
                List<Vector2[]> roads = quads[pad];
                if (roads.Count == 0) return;
                ulong key = MouthKey(pad, roads);
                Mouth cached;
                lock (MouthCache)
                {
                    MouthCache.TryGetValue(pad.Name, out cached);
                }
                mouths[index] = cached != null && cached.Key == key ? cached : BuildMouth(pad, roads, key);
            });
            for (int index = 0; index < pads.Count; index++)
            {
                Mouth mouth = mouths[index];
                if (mouth == null) continue;
                MouthCache[pads[index].Name] = mouth;
                Pad pad = pads[index];
                pad.Outline = mouth.Outline;
                pad.Field = mouth.Field;
                pad.FieldOrigin = mouth.Origin;
                pad.FieldWidth = mouth.Width;
                pad.FieldDepth = mouth.Depth;
                pad.Bounds = Rect.MinMaxRect(mouth.Outline.Min(p => p.x), mouth.Outline.Min(p => p.y),
                    mouth.Outline.Max(p => p.x), mouth.Outline.Max(p => p.y));
            }
        }

        // The road top near a pad end as quads, LipInset wider on each side than the road, so the grown pad also covers
        // the rounded road edge. The quads run from the end to the first row more than MouthReach outside the pad. A
        // road end that stops short of the pad takes that gap in addition.
        private static IEnumerable<Vector2[]> MouthQuads(Piece piece, int side, Pad pad)
        {
            int count = piece.Rows.Count;
            Row RowAt(int step) => piece.Rows[side == 0 ? step : count - 1 - step];
            float reach = MouthReach + Mathf.Max(0f, PadDistance(pad, RowAt(0).Center));
            for (int step = 0; step + 1 < count; step++)
            {
                Row a = RowAt(step), b = RowAt(step + 1);
                if (PadDistance(pad, a.Center) > reach) yield break;
                float halfA = a.Half + LipInset, halfB = b.Half + LipInset;
                yield return new[]
                {
                    a.Center - a.Right * halfA, a.Center + a.Right * halfA, b.Center + b.Right * halfB,
                    b.Center - b.Right * halfB
                };
            }
        }

        private static ulong MouthKey(Pad pad, List<Vector2[]> roads)
        {
            var hash = Hash.Start();
            hash.Add(pad.Name);
            foreach (Vector2 point in pad.Base) hash.Add(point);
            foreach (Vector2[] quad in roads)
            {
                foreach (Vector2 point in quad) hash.Add(point);
            }
            return hash.Value;
        }

        // Rasters the pad and the road quads, closes the raster and keeps its signed distance field and outline.
        private static Mouth BuildMouth(Pad pad, List<Vector2[]> roads, ulong key, float closing = MouthRadius)
        {
            List<Vector2> points = pad.Base.Concat(roads.SelectMany(quad => quad)).ToList();
            float margin = closing + 2f;
            var origin = new Vector2(points.Min(p => p.x) - margin, points.Min(p => p.y) - margin);
            int width = Mathf.CeilToInt((points.Max(p => p.x) + margin - origin.x) / MouthCell) + 1;
            int depth = Mathf.CeilToInt((points.Max(p => p.y) + margin - origin.y) / MouthCell) + 1;
            Vector2 Cell(int x, int z) => origin + new Vector2(x, z) * MouthCell;

            var inside = new bool[width * depth];
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    inside[z * width + x] = ConvexDistance(pad.Base, pad.Center, Cell(x, z)) < 0f;
                }
            }
            foreach (Vector2[] quad in roads)
            {
                int x0 = Mathf.Max(0, Mathf.FloorToInt((quad.Min(p => p.x) - origin.x) / MouthCell));
                int x1 = Mathf.Min(width - 1, Mathf.CeilToInt((quad.Max(p => p.x) - origin.x) / MouthCell));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((quad.Min(p => p.y) - origin.y) / MouthCell));
                int z1 = Mathf.Min(depth - 1, Mathf.CeilToInt((quad.Max(p => p.y) - origin.y) / MouthCell));
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        if (InQuad(quad, Cell(x, z))) inside[z * width + x] = true;
                    }
                }
            }

            // Closing: grow by the radius, then keep the cells more than the radius inside the grown area.
            float radius = closing / MouthCell;
            float[] toInside = DistanceTo(inside, width, depth);
            var outsideGrown = new bool[inside.Length];
            for (int index = 0; index < inside.Length; index++) outsideGrown[index] = toInside[index] > radius;
            float[] toOutsideGrown = DistanceTo(outsideGrown, width, depth);
            var open = new bool[inside.Length];
            for (int index = 0; index < inside.Length; index++)
            {
                open[index] = !(inside[index] || toOutsideGrown[index] > radius);
            }

            // Opening: keep the cells more than the rounding inside, then grow them back by the rounding.
            float rounding = MouthRounding / MouthCell;
            float[] toOpen = DistanceTo(open, width, depth);
            var core = new bool[inside.Length];
            for (int index = 0; index < inside.Length; index++) core[index] = toOpen[index] > rounding;
            float[] toCore = DistanceTo(core, width, depth);
            var shape = new bool[inside.Length];
            for (int index = 0; index < inside.Length; index++) shape[index] = toCore[index] <= rounding;

            var mouth = new Mouth
            {
                Key = key, Field = SignedField(shape, width, depth), Origin = origin, Width = width, Depth = depth
            };
            mouth.Outline = TraceOutline(mouth, pad.Center);
            // The pad top covers only the traced outline, which is star-shaped from the center. Where the shape is not,
            // for example a road that runs along a pad side and on past a corner, the outline leaves part of the shape
            // out. The field then follows the outline, so the road slab stays where the pad top does not cover it.
            mouth.Field = SignedField(Fill(mouth.Outline, origin, width, depth), width, depth);
            return mouth;
        }

        // The signed distance of each cell to the edge of the shape in meters, negative inside. The half cell puts the
        // zero between an inside cell and an outside cell.
        private static float[] SignedField(bool[] shape, int width, int depth)
        {
            var outside = new bool[shape.Length];
            for (int index = 0; index < shape.Length; index++) outside[index] = !shape[index];
            float[] toShape = DistanceTo(shape, width, depth);
            float[] toOutside = DistanceTo(outside, width, depth);
            var field = new float[shape.Length];
            for (int index = 0; index < field.Length; index++)
            {
                field[index] = shape[index] ? -(toOutside[index] - 0.5f) * MouthCell :
                    (toShape[index] - 0.5f) * MouthCell;
            }
            return field;
        }

        // The cells inside a closed outline, by scan lines along x.
        private static bool[] Fill(Vector2[] outline, Vector2 origin, int width, int depth)
        {
            var inside = new bool[width * depth];
            var crossings = new List<float>();
            for (int z = 0; z < depth; z++)
            {
                float y = origin.y + z * MouthCell;
                crossings.Clear();
                for (int index = 0, previous = outline.Length - 1; index < outline.Length; previous = index++)
                {
                    Vector2 a = outline[previous], b = outline[index];
                    if ((a.y > y) == (b.y > y)) continue;
                    crossings.Add(a.x + (y - a.y) * (b.x - a.x) / (b.y - a.y));
                }
                crossings.Sort();
                for (int pair = 0; pair + 1 < crossings.Count; pair += 2)
                {
                    int x0 = Mathf.Max(0, Mathf.CeilToInt((crossings[pair] - origin.x) / MouthCell));
                    int x1 = Mathf.Min(width - 1, Mathf.FloorToInt((crossings[pair + 1] - origin.x) / MouthCell));
                    for (int x = x0; x <= x1; x++) inside[z * width + x] = true;
                }
            }
            return inside;
        }

        // The pad top is a set of rings around the center, so the outline must be star-shaped from the center. Each ray
        // takes the first zero of the field. The points are then resampled Step apart along the outline.
        private static Vector2[] TraceOutline(Mouth mouth, Vector2 center)
        {
            float march = MouthCell * 0.5f;
            float reach = Mathf.Sqrt(mouth.Width * mouth.Width + mouth.Depth * mouth.Depth) * MouthCell;
            float Sample(Vector2 point) =>
                TrySampleField(mouth.Field, mouth.Origin, mouth.Width, mouth.Depth, point, out float value) ? value :
                    MouthCell;
            var ring = new Vector2[MouthRays];
            for (int ray = 0; ray < MouthRays; ray++)
            {
                float angle = ray * 2f * Mathf.PI / MouthRays;
                var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                float previous = Sample(center);
                float radius = 0f;
                for (float distance = march; distance < reach; distance += march)
                {
                    float value = Sample(center + direction * distance);
                    if (value >= 0f)
                    {
                        radius = distance - march * value / Mathf.Max(value - previous, 1e-5f);
                        break;
                    }
                    previous = value;
                }
                ring[ray] = center + direction * radius;
            }
            var lengths = new float[MouthRays + 1];
            for (int index = 0; index < MouthRays; index++)
            {
                lengths[index + 1] = lengths[index] + Vector2.Distance(ring[index], ring[(index + 1) % MouthRays]);
            }
            int count = Mathf.Max(8, Mathf.CeilToInt(lengths[MouthRays] / Step));
            var outline = new Vector2[count];
            int segment = 0;
            for (int index = 0; index < count; index++)
            {
                float target = lengths[MouthRays] * index / count;
                while (lengths[segment + 1] < target) segment++;
                float span = Mathf.Max(lengths[segment + 1] - lengths[segment], 1e-5f);
                outline[index] = Vector2.Lerp(ring[segment], ring[(segment + 1) % MouthRays],
                    (target - lengths[segment]) / span);
            }
            return outline;
        }

        // Bilinear sample of a field whose cell (x, z) lies at origin + (x, z) * MouthCell. False outside the field.
        private static bool TrySampleField(float[] field, Vector2 origin, int width, int depth, Vector2 point,
            out float value)
        {
            float gx = (point.x - origin.x) / MouthCell;
            float gz = (point.y - origin.y) / MouthCell;
            if (gx < 0f || gz < 0f || gx > width - 1 || gz > depth - 1)
            {
                value = 0f;
                return false;
            }
            int x0 = Mathf.Min((int)gx, width - 2), z0 = Mathf.Min((int)gz, depth - 2);
            float fx = gx - x0, fz = gz - z0;
            int index = z0 * width + x0;
            value = Mathf.Lerp(Mathf.Lerp(field[index], field[index + 1], fx),
                Mathf.Lerp(field[index + width], field[index + width + 1], fx), fz);
            return true;
        }

        // The exact Euclidean distance in cells from each cell to the nearest target cell (Felzenszwalb and
        // Huttenlocher): one pass along the columns, then one along the rows.
        private static float[] DistanceTo(bool[] target, int width, int depth)
        {
            const double Far = 1e20;
            var squared = new double[width * depth];
            for (int index = 0; index < squared.Length; index++) squared[index] = target[index] ? 0.0 : Far;
            // Each worker keeps one scratch for all its lines. New arrays for each line made most of the garbage.
            int size = Mathf.Max(width, depth);
            System.Threading.Tasks.Parallel.For(0, width, () => new DistanceScratch(size), (x, _, scratch) =>
            {
                for (int z = 0; z < depth; z++) scratch.Line[z] = squared[z * width + x];
                DistanceLine(scratch.Line, depth, scratch.Output, scratch.Parabolas, scratch.Bounds);
                for (int z = 0; z < depth; z++) squared[z * width + x] = scratch.Output[z];
                return scratch;
            }, _ => { });
            System.Threading.Tasks.Parallel.For(0, depth, () => new DistanceScratch(size), (z, _, scratch) =>
            {
                Array.Copy(squared, z * width, scratch.Line, 0, width);
                DistanceLine(scratch.Line, width, scratch.Output, scratch.Parabolas, scratch.Bounds);
                Array.Copy(scratch.Output, 0, squared, z * width, width);
                return scratch;
            }, _ => { });
            var distance = new float[squared.Length];
            for (int index = 0; index < distance.Length; index++) distance[index] = (float)Math.Sqrt(squared[index]);
            return distance;
        }

        private sealed class DistanceScratch
        {
            public readonly double[] Line, Output, Bounds;
            public readonly int[] Parabolas;

            public DistanceScratch(int size)
            {
                Line = new double[size];
                Output = new double[size];
                Parabolas = new int[size];
                Bounds = new double[size + 1];
            }
        }

        // The squared distance transform of the first length values of a line: the lower envelope of the parabolas over
        // the sampled values.
        private static void DistanceLine(double[] values, int length, double[] output, int[] parabolas, double[] bounds)
        {
            int k = 0;
            parabolas[0] = 0;
            bounds[0] = double.NegativeInfinity;
            bounds[1] = double.PositiveInfinity;
            for (int q = 1; q < length; q++)
            {
                double s = Intersection(values, q, parabolas[k]);
                while (s <= bounds[k])
                {
                    k--;
                    s = Intersection(values, q, parabolas[k]);
                }
                k++;
                parabolas[k] = q;
                bounds[k] = s;
                bounds[k + 1] = double.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < length; q++)
            {
                while (bounds[k + 1] < q) k++;
                double offset = q - parabolas[k];
                output[q] = offset * offset + values[parabolas[k]];
            }
        }

        private static double Intersection(double[] values, int q, int p)
        {
            return (values[q] + (double)q * q - (values[p] + (double)p * p)) / (2.0 * q - 2.0 * p);
        }
        #endregion

        #region Edge
        // Adds a rounded edge outside the rim vertices of a slab top. The profile is a smoothstep from the top down to
        // the ground: it leaves the top level and meets the ground level, so neither line shows a crease. The rim
        // vertex is shared with the top, so its normal is smooth. A segment whose midpoint passes the skip test gets no
        // faces. The lip vertices take the road coordinates and the joins of their rim vertex, and the green vertex
        // color gives their place on the lip (0 at the rim, 1 on the ground) to the road shader.
        // A ground function replaces the terrain under the edge, and the edge is at most the widest width outside
        // the outline. Without a ground lip the edge keeps the slab surface to its end (vertex color green zero).
        private static void AddRoundedLip(List<Vector3> vertices, List<int> triangles, List<Vector4> road,
            List<Color32> joins, List<int> rim, List<Vector2> outward, Func<Vector2, bool> skip,
            UnityEngine.Terrain[] tiles, Vector3 origin, Func<Vector2, float> ground = null,
            float widest = LipWidthMax, bool groundLip = true)
        {
            // The vertex of each rim point and lip step, LipSteps + 1 for each point. The array stays for the next
            // slab.
            const int stride = LipSteps + 1;
            if (_lipRings.Length < rim.Count * stride) _lipRings = new int[rim.Count * stride * 2];
            int[] rings = _lipRings;
            for (int point = 0; point < rim.Count; point++)
            {
                rings[point * stride] = rim[point];
                Vector2 start = Flat(vertices[rim[point]] + origin);
                float reach = LipInset + Mathf.Min(LipWidthAt(start), widest);
                for (int step = 1; step <= LipSteps; step++)
                {
                    float u = step / (float)LipSteps;
                    Vector2 position = start + outward[point] * (reach * u);
                    float height = (ground != null ? ground(position) : Height(tiles, position)) + LipLift +
                        (Thickness - LipLift) *
                        (1f - Mathf.SmoothStep(0f, 1f, u));
                    rings[point * stride + step] = vertices.Count;
                    vertices.Add(new Vector3(position.x, height, position.y) - origin);
                    road.Add(road[rim[point]]);
                    Color32 join = joins[rim[point]];
                    join.g = groundLip ? (byte)Mathf.RoundToInt(u * 255f) : (byte)0;
                    joins.Add(join);
                }
            }
            for (int point = 0; point < rim.Count; point++)
            {
                int next = (point + 1) % rim.Count;
                Vector2 middle = (Flat(vertices[rim[point]]) + Flat(vertices[rim[next]])) * 0.5f + Flat(origin);
                if (skip(middle)) continue;
                for (int step = 0; step < LipSteps; step++)
                {
                    AddQuad(triangles, vertices, rings[point * stride + step], rings[next * stride + step],
                        rings[next * stride + step + 1], rings[point * stride + step + 1]);
                }
            }
        }
        #endregion

        #region Terrain
        // The mask texels over the network: cells of MinMaskCell or larger, so a mask has at most MaxMaskSize texels a
        // side.
        internal sealed class MaskGrid
        {
            public Rect Box;
            public float Cell;
            public int Width;
            public int Depth;

            public bool Matches(MaskGrid other)
            {
                return other != null && other.Box == Box && other.Cell == Cell && other.Width == Width &&
                    other.Depth == Depth;
            }

            // The texels whose centers can lie in the area, clamped to the grid. The rectangle is empty when none can.
            public RectInt Cells(Rect area)
            {
                int x0 = Mathf.Max(0, Mathf.FloorToInt((area.xMin - Box.xMin) / Cell));
                int x1 = Mathf.Min(Width - 1, Mathf.CeilToInt((area.xMax - Box.xMin) / Cell));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((area.yMin - Box.yMin) / Cell));
                int z1 = Mathf.Min(Depth - 1, Mathf.CeilToInt((area.yMax - Box.yMin) / Cell));
                return new RectInt(x0, z0, Mathf.Max(0, x1 - x0 + 1), Mathf.Max(0, z1 - z0 + 1));
            }

            public Vector2 Center(int x, int z)
            {
                return new Vector2(Box.xMin + (x + 0.5f) * Cell, Box.yMin + (z + 0.5f) * Cell);
            }
        }

        // One shape of the slab distance field: a road segment (Pad is null) or a pad outline, with the area that it
        // covers.
        private struct MaskShape
        {
            public Rect Area;
            public Vector2 A;
            public Vector2 B;
            public float Half;
            public Pad Pad;
            // A bridge deck end clears plants but paints no gravel.
            public bool ClearOnly;

            public float Distance(Vector2 point)
            {
                return Pad != null ? PadDistance(Pad, point) : SegmentDistance(point, A, B) - Half;
            }
        }

        private const float MaskMargin = ShoulderFullMax + ShoulderFadeMax + EdgeAmplitude + 2f;

        private static MaskGrid MaskGridFor(Network network)
        {
            var extent = new Extent();
            foreach (Piece piece in network.Pieces)
            {
                foreach (Row row in piece.Rows) extent.Add(row.Center);
            }
            foreach (Pad pad in network.Slabs)
            {
                foreach (Vector2 point in pad.Outline) extent.Add(point);
            }
            foreach (Pad pad in network.DeckEnds)
            {
                foreach (Vector2 point in pad.Outline) extent.Add(point);
            }
            Rect box = Rect.MinMaxRect(extent.MinX - MaskMargin - 20f, extent.MinY - MaskMargin - 20f,
                extent.MaxX + MaskMargin + 20f, extent.MaxY + MaskMargin + 20f);
            float cell = Mathf.Max(MinMaskCell, Mathf.Max(box.width, box.height) / (MaxMaskSize - 1));
            return new MaskGrid
            {
                Box = box, Cell = cell, Width = Mathf.CeilToInt(box.width / cell),
                    Depth = Mathf.CeilToInt(box.height / cell)
            };
        }

        // The road segments of each piece, then the outline of each pad, junction patch and bridge deck end. The list
        // is reused: the callers fill the masks before the next call.
        private static readonly List<MaskShape> ShapeScratch = new List<MaskShape>();

        private static List<MaskShape> MaskShapes(Network network)
        {
            List<MaskShape> shapes = ShapeScratch;
            shapes.Clear();
            foreach (Piece piece in network.Pieces)
            {
                for (int row = 0; row + 1 < piece.Rows.Count; row++)
                {
                    shapes.Add(SegmentShape(piece.Rows[row], piece.Rows[row + 1]));
                }
            }
            foreach (Pad pad in network.Slabs)
            {
                shapes.Add(new MaskShape { Area = PadMaskArea(pad), Pad = pad });
            }
            foreach (Pad deck in network.DeckEnds)
            {
                shapes.Add(new MaskShape { Area = PadMaskArea(deck), Pad = deck, ClearOnly = true });
            }
            return shapes;
        }

        private static MaskShape SegmentShape(Row a, Row b)
        {
            float half = Mathf.Max(a.Half, b.Half);
            return new MaskShape
            {
                Area = Rect.MinMaxRect(Mathf.Min(a.Center.x, b.Center.x) - half - MaskMargin,
                    Mathf.Min(a.Center.y, b.Center.y) - half - MaskMargin, Mathf.Max(a.Center.x, b.Center.x) + half +
                        MaskMargin,
                    Mathf.Max(a.Center.y, b.Center.y) + half + MaskMargin),
                A = a.Center, B = b.Center, Half = half
            };
        }

        private static Rect PadMaskArea(Pad pad)
        {
            return Rect.MinMaxRect(pad.Bounds.xMin - MaskMargin, pad.Bounds.yMin - MaskMargin, pad.Bounds.xMax +
                MaskMargin,
                pad.Bounds.yMax + MaskMargin);
        }

        // One signed distance field to the slabs (negative on a slab) gives the gravel shoulder and the clear. Fills
        // the texels in cells (grid indexes, row z * Width + x). Each texel takes the minimum distance of the shapes
        // whose area covers it, so a region gives the same values as the whole grid. Bands of rows run in parallel.
        private static void FillMasks(MaskGrid grid, List<MaskShape> shapes, RectInt cells, byte[] shoulder,
            byte[] clear)
        {
            const int bandRows = 32;
            int bands = (cells.height + bandRows - 1) / bandRows;
            int capacity = bandRows * cells.width;
            // Each worker keeps one pair of band arrays for all its bands.
            System.Threading.Tasks.Parallel.For(0, bands, () => (new float[capacity], new float[capacity]),
                (band, _, scratch) =>
            {
                int z0 = cells.yMin + band * bandRows;
                int z1 = Mathf.Min(cells.yMax, z0 + bandRows) - 1;
                // The gravel reads the paved shapes only, the clear reads all shapes.
                float[] paved = scratch.Item1;
                float[] field = scratch.Item2;
                int length = (z1 - z0 + 1) * cells.width;
                for (int index = 0; index < length; index++) paved[index] = field[index] = float.MaxValue;
                foreach (MaskShape shape in shapes)
                {
                    RectInt area = grid.Cells(shape.Area);
                    int ax0 = Mathf.Max(area.xMin, cells.xMin), ax1 = Mathf.Min(area.xMax, cells.xMax) - 1;
                    int az0 = Mathf.Max(area.yMin, z0), az1 = Mathf.Min(area.yMax - 1, z1);
                    for (int z = az0; z <= az1; z++)
                    {
                        for (int x = ax0; x <= ax1; x++)
                        {
                            int index = (z - z0) * cells.width + x - cells.xMin;
                            float distance = shape.Distance(grid.Center(x, z));
                            field[index] = Mathf.Min(field[index], distance);
                            if (!shape.ClearOnly) paved[index] = Mathf.Min(paved[index], distance);
                        }
                    }
                }
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = cells.xMin; x < cells.xMax; x++)
                    {
                        int local = (z - z0) * cells.width + x - cells.xMin;
                        int index = z * grid.Width + x;
                        Vector2 center = grid.Center(x, z);
                        float distance = field[local];
                        shoulder[index] = paved[local] == float.MaxValue
                            ? (byte)0
                            : (byte)Mathf.RoundToInt(Border(center, paved[local]).gravel * 255f);
                        clear[index] = distance == float.MaxValue
                            ? (byte)0
                            : (byte)Mathf.RoundToInt(Border(center, distance).cleared * 255f);
                    }
                }
                return scratch;
            }, _ => { });
        }

        // The gravel shoulder and the clear weights at a distance outside the slab edge (negative on a slab). Noise
        // varies the full gravel width, the fade length and the edge, so the border is not a uniform band. The clear is
        // full up to the lip foot plus ClearMargin, so no plant grows through the lip, then fades out over a short reach
        // that changes in patches, so plants come back sparse first and dense farther out, and in places reach the lip.
        private static (float gravel, float cleared) Border(Vector2 point, float distance)
        {
            float band = Mathf.PerlinNoise(point.x / BandScale + 31.7f, point.y / BandScale + 11.3f);
            float fade = Mathf.PerlinNoise(point.x / FadeScale + 5.1f, point.y / FadeScale + 47.9f);
            float edge = Mathf.PerlinNoise(point.x / EdgeScale + 71.3f, point.y / EdgeScale + 23.9f) - 0.5f;
            float full = Mathf.Lerp(ShoulderFullMin, ShoulderFullMax, band);
            float length = Mathf.Lerp(ShoulderFadeMin, ShoulderFadeMax, fade);
            float outside = distance + edge * EdgeAmplitude;
            float gravel = 1f - Mathf.SmoothStep(0f, 1f, (outside - full) / length);
            float lip = LipWidthAt(point) + ClearMargin;
            float patch = Mathf.PerlinNoise(point.x / ClearPatchScale + 13.7f, point.y / ClearPatchScale + 59.3f);
            float reach = Mathf.Lerp(ClearReachMin, ClearReachMax, Mathf.SmoothStep(0f, 1f, (patch - 0.3f) / 0.4f));
            float cleared = distance <= lip ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (outside - lip) / reach);
            return (Mathf.Clamp01(gravel), Mathf.Clamp01(cleared));
        }

        private static void ConfigureMasks(Transform stampRoot, Network network, StringBuilder report)
        {
            MaskGrid grid = MaskGridFor(network);
            var shoulder = new byte[grid.Width * grid.Depth];
            var clear = new byte[shoulder.Length];
            FillMasks(grid, MaskShapes(network), new RectInt(0, 0, grid.Width, grid.Depth), shoulder, clear);

            // The shoulder and the clear come after the roads and the pads, so they cover their soft pavement edge.
            ConfigureShoulder(stampRoot, WriteMask(ShoulderMaskPath, shoulder, grid.Width, grid.Depth), grid.Box);
            ConfigureClear(stampRoot, WriteMask(ClearMaskPath, clear, grid.Width, grid.Depth), grid.Box);
            report.AppendLine("Shoulder and clear: " + grid.Width + " x " + grid.Depth + " masks, " +
                grid.Cell.ToString("F2") +
                " m cells over " + grid.Box.width.ToString("F0") + " x " + grid.Box.height.ToString("F0") + " m.");
        }

        // Looga texture stamps have no path occlusion, so the shoulder also covers the soft road edges.
        private static void ConfigureShoulder(Transform stampRoot, Texture2D mask, Rect box)
        {
            LoogaTextureStamp texture = RequireStamp(_network.ShoulderStamp, "shoulder stamp");
            TerrainLayer layer = _network.ShoulderLayer
                ? _network.ShoulderLayer
                : throw new MissingReferenceException("Shoulder layer of the road network " + _network.name + ".");
            UseMask(texture.Falloff, texture.transform, stampRoot.transform, mask, box);
            texture.SetTexture(layer, 1f, true);
            EditorUtility.SetDirty(texture);
        }

        private static void ConfigureClear(Transform stampRoot, Texture2D mask, Rect box)
        {
            LoogaClearStamp stamp = RequireStamp(_network.ClearStamp, "clear stamp");
            UseMask(stamp.Falloff, stamp.transform, stampRoot.transform, mask, box);
            stamp.SetClear(true, true, true, 1f, true);
            EditorUtility.SetDirty(stamp);
        }

        // The shoulder and clear stamps come after the roads and the pads, so they cover their soft pavement edge.
        private static T RequireStamp<T>(T stamp, string role) where T : MonoBehaviour
        {
            if (!stamp)
            {
                throw new MissingReferenceException("Set the " + role + " of the road network " + _network.name + ".");
            }
            Undo.RecordObjects(new Object[] { stamp, stamp.transform }, "Update " + stamp.name);
            stamp.transform.SetAsLastSibling();
            return stamp;
        }

        private static Texture2D WriteMask(string path, byte[] pixels, int width, int depth)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(path, ImageConversion.EncodeArrayToPNG(pixels,
                UnityEngine.Experimental.Rendering.GraphicsFormat.R8_UNorm, (uint)width, (uint)depth));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            // The falloff reads only the red channel. A single channel import keeps the 8-bit values at a quarter of
            // the memory of the RGB import that a greyscale PNG gets by default.
            importer.textureType = TextureImporterType.SingleChannel;
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.singleChannelComponent = TextureImporterSingleChannelComponent.Red;
            importer.SetTextureSettings(settings);
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.isReadable = false;
            importer.sRGBTexture = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = MaxMaskSize;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // The texture falloff maps the red channel of the mask over the stamp box. The caller sets texture clamping on
        // the stamp and marks the stamp changed.
        private static void UseMask(StampFalloff falloff, Transform stamp, Transform parent, Texture2D mask, Rect box)
        {
            falloff.SetShape(FalloffShape.Texture, new Vector2(0f, 1f), falloff.Easing);
            falloff.SetTexture(mask, 0, new Vector2(1f, 0f), falloff.TextureRotation, falloff.TextureScale,
                falloff.TextureOffset);
            PlaceMaskStamp(stamp, parent, box);
        }

        // The stamp box is the mask box. Returns true when the transform changed.
        private static bool PlaceMaskStamp(Transform stamp, Transform parent, Rect box)
        {
            var position = new Vector3(box.center.x, 0f, box.center.y);
            Vector3 parentScale = parent.lossyScale;
            var scale = new Vector3(box.width / parentScale.x, 1f / parentScale.y, box.height / parentScale.z);
            if (stamp.position.Equals(position) && stamp.rotation == Quaternion.identity &&
                stamp.localScale.Equals(scale))
            {
                return false;
            }
            stamp.SetPositionAndRotation(position, Quaternion.identity);
            stamp.localScale = scale;
            return true;
        }

        // The pad pavement stamp ends at the outline. The slab covers the pad, so no painted pavement shows around it.
        // CreatePad gave every pad stamp boost 11, range 11 and noise 3.3.
        private static void EndPadPavement(Pad pad)
        {
            foreach (LoogaTextureStamp stamp in pad.Transform.GetComponentsInChildren<LoogaTextureStamp>(true))
            {
                Undo.RecordObject(stamp, "End pad pavement at the outline");
                StampFalloff falloff = stamp.Falloff;
                falloff.SetSplineArea(falloff.SplineArea, 0.6f, 0.3f);
                falloff.SetNoise(falloff.NoiseType, falloff.NoiseSpace, falloff.NoiseFrequency, 0.2f,
                    falloff.NoiseOffset,
                    falloff.NoiseBalance);
                stamp.MarkChanged();
                EditorUtility.SetDirty(stamp);
            }
        }
        #endregion

        #region Live
        // A slab signature holds the shapes of the slabs whose bounds come this close: the mesh reads their tops
        // (covering tests, the pad road gap probe of 3 m and the lip). A height change this close to a slab changes its
        // mesh.
        private const float NeighbourReach = 3.5f;
        private const float GroundReach = LipInset + LipWidthMax + 1f;
        // A changed mask texel changes the mip texels over this many texels around it. The stamps sample the masks at
        // most 2^5 texels per sample (placement cells of up to about 20 m).
        private const int MipReach = 32;
        // A height sample depends on the heightmap texels around it, so a forgotten area grows by this distance.
        private const float SampleReach = 2f;

        // The height cache of the running live update, or null. Full builds sample the terrain directly.
        private static HeightCache _heights;

        /// <summary>
        /// Ground heights of earlier live updates, in cells of 64 m. Only the cells in a changed area are sampled
        /// again, so a slab that a change reaches only in part costs only that part. The values are the terrain
        /// samples, so the meshes are the same as without the cache.
        /// </summary>
        internal sealed class HeightCache
        {
            private const float CellSize = 64f;
            private readonly Dictionary<long, Dictionary<long, float>> _cells = new Dictionary<long, Dictionary<long,
                float>>();

            public float Get(UnityEngine.Terrain[] tiles, Vector2 point)
            {
                long cell = CellKey(Mathf.FloorToInt(point.x / CellSize), Mathf.FloorToInt(point.y / CellSize));
                if (!_cells.TryGetValue(cell, out Dictionary<long, float> samples))
                {
                    _cells[cell] = samples = new Dictionary<long, float>();
                }
                long key = ((long)BitConverter.SingleToInt32Bits(point.x) << 32) |
                    (uint)BitConverter.SingleToInt32Bits(point.y);
                if (!samples.TryGetValue(key, out float height))
                {
                    samples[key] = height = SampleHeight(tiles, point);
                }
                return height;
            }

            // Walks the cached cells, not the cells of the area: a whole-world update can report an area of millions of
            // cells (a stamp with a very large box), and that loop stalled the editor for hours.
            public void Forget(Rect area)
            {
                area = Grow(area, SampleReach);
                double x0 = Math.Floor(area.xMin / (double)CellSize), x1 = Math.Floor(area.xMax / (double)CellSize);
                double z0 = Math.Floor(area.yMin / (double)CellSize), z1 = Math.Floor(area.yMax / (double)CellSize);
                // An empty cell samples again, as a removed cell. The cleared dictionaries keep their storage.
                foreach (KeyValuePair<long, Dictionary<long, float>> cell in _cells)
                {
                    int x = (int)(cell.Key >> 32), z = (int)(uint)cell.Key;
                    if (x >= x0 && x <= x1 && z >= z0 && z <= z1) cell.Value.Clear();
                }
            }

            public void Clear()
            {
                _cells.Clear();
            }

            private static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;
        }

        /// <summary>
        /// State of the live slabs that <see cref="LoogaRoadLive"/> keeps: a signature of each slab mesh, a hash of the
        /// shapes of each mask part, and the in-memory shoulder and clear masks.
        /// </summary>
        internal sealed class LiveState
        {
            internal Dictionary<string, ulong> Slabs;
            internal Dictionary<string, (ulong shape, Rect area)> MaskParts;
            internal MaskGrid Grid;
            internal byte[] Shoulder;
            internal byte[] Clear;
            internal byte[] NextShoulder;
            internal byte[] NextClear;
            internal Texture2D ShoulderTexture;
            internal Texture2D ClearTexture;
            internal readonly HeightCache Heights = new HeightCache();

            public void Dispose()
            {
                if (ShoulderTexture)
                {
                    Object.DestroyImmediate(ShoulderTexture);
                }
                if (ClearTexture)
                {
                    Object.DestroyImmediate(ClearTexture);
                }
                ShoulderTexture = null;
                ClearTexture = null;
            }
        }

        /// <summary>What one live update changed, and its time.</summary>
        internal sealed class LiveUpdate
        {
            public int Slabs;
            public int Rebuilt;
            public int Removed;
            public int Vertices;
            public bool MasksReplaced;
            public Rect? MaskRegion;
            public double TopologySeconds;
            public double MeshSeconds;
            public double MaskSeconds;

            public override string ToString()
            {
                return Rebuilt + " of " + Slabs + " slabs built (" + Vertices.ToString("N0") + " vertices), " +
                    Removed +
                    " removed, masks " + (MasksReplaced ? "replaced" : MaskRegion.HasValue ? "changed in " +
                        MaskRegion.Value : "unchanged") +
                    "; topology " + TopologySeconds.ToString("F3") + " s, meshes " + MeshSeconds.ToString("F3") +
                        " s, masks " +
                    MaskSeconds.ToString("F3") + " s.";
            }
        }

        /// <summary>True when the shoulder and clear stamps have texture sources, so the masks and the slabs are
        /// live.</summary>
        internal static bool IsLive(LoogaRoadNetwork roadNetwork)
        {
            if (!roadNetwork || !roadNetwork.World) return false;
            LoogaTextureStamp shoulder = roadNetwork.ShoulderStamp;
            LoogaClearStamp clear = roadNetwork.ClearStamp;
            return shoulder && clear && shoulder.TryGetComponent(out LoogaStampTextureSource _) &&
                clear.TryGetComponent(out LoogaStampTextureSource _);
        }

        /// <summary>Adds a texture source to the shoulder stamp and the clear stamp (undo records).</summary>
        internal static void MakeLive(LoogaRoadNetwork roadNetwork)
        {
            foreach (MonoBehaviour stamp in new MonoBehaviour[] { roadNetwork.ShoulderStamp, roadNetwork.ClearStamp })
            {
                if (!stamp)
                {
                    throw new MissingReferenceException("Set the shoulder and clear stamps of " + roadNetwork.name +
                        ".");
                }
                if (!stamp.TryGetComponent(out LoogaStampTextureSource _))
                {
                    Undo.AddComponent<LoogaStampTextureSource>(stamp.gameObject);
                }
            }
        }

        /// <summary>
        /// Brings the slabs and the shoulder and clear masks up to date with the roads, the pads and the ground. The
        /// topology runs in full. A slab mesh is built again when its signature changed, when heights changed near it
        /// or when its object is missing. The first update of a state keeps the current meshes. The masks change only
        /// in the changed texels, and the mask stamp sources get the changed area. Meshes update in place without undo
        /// records.
        /// </summary>
        internal static LiveUpdate UpdateLive(LoogaRoadNetwork roadNetwork, LiveState state, Rect? heights,
            bool rebuildAll)
        {
            _network = roadNetwork;
            if (!roadNetwork || !roadNetwork.World) return null;
            Transform stampRoot = roadNetwork.World.transform;
            var update = new LiveUpdate();
            var clock = Stopwatch.StartNew();
            if (rebuildAll || state.Slabs == null)
            {
                state.Heights.Clear();
            }
            else if (heights.HasValue)
            {
                state.Heights.Forget(heights.Value);
            }
            _heights = state.Heights;
            try
            {
                return UpdateLive(state, heights, rebuildAll, stampRoot, update, clock);
            }
            finally
            {
                _heights = null;
            }
        }

        private static LiveUpdate UpdateLive(LiveState state, Rect? heights, bool rebuildAll, Transform stampRoot,
            LiveUpdate update, Stopwatch clock)
        {
            Network network = Plan(stampRoot, Tiles());
            update.TopologySeconds = clock.Elapsed.TotalSeconds;

            clock.Restart();
            Dictionary<string, ulong> signatures = Signatures(network, out Dictionary<string, Rect> bounds);
            update.Slabs = signatures.Count;
            bool adopt = state.Slabs == null;
            Scene scene = stampRoot.gameObject.scene;
            bool sceneChanged = false;
            GameObject root = _network.SlabRoot ? _network.SlabRoot.gameObject : null;
            if (!root)
            {
                root = new GameObject(RootName);
                SceneManager.MoveGameObjectToScene(root, scene);
                _network.SetSlabRoot(root.transform);
                EditorUtility.SetDirty(_network);
                sceneChanged = true;
            }
            var objects = new Dictionary<string, Transform>();
            foreach (Transform child in root.transform)
            {
                objects.TryAdd(child.name, child);
            }
            Material material = null;
            var order = new List<Transform>();
            foreach ((string name, Piece piece, Pad pad, DeckSlab deck) in SlabSources(network))
            {
                objects.TryGetValue(name, out Transform slab);
                bool rebuild = rebuildAll || !slab || (!adopt &&
                    (!state.Slabs.TryGetValue(name, out ulong signature) || signature != signatures[name] ||
                        (heights.HasValue && heights.Value.Overlaps(Grow(bounds[name], GroundReach)))));
                if (rebuild)
                {
                    Trace("live: slab " + name);
                    SlabMesh mesh = piece != null ? BuildRoadMesh(network, piece, LiveMesh) :
                        pad != null ? BuildPadMesh(network, pad, LiveMesh) : BuildDeckMesh(network, deck, LiveMesh);
                    // A road piece without two rows has no slab. Its old object goes below.
                    if (mesh == null) continue;
                    Mesh asset = SaveMesh(mesh);
                    update.Rebuilt++;
                    update.Vertices += mesh.Vertices.Count;
                    if (!slab)
                    {
                        material ??= _network.SlabMaterial;
                        slab = AddSlab(root.transform, name, asset, mesh.Origin, material);
                        sceneChanged = true;
                    }
                    else
                    {
                        if (!slab.position.Equals(mesh.Origin))
                        {
                            slab.position = mesh.Origin;
                            sceneChanged = true;
                        }
                        var filter = slab.GetComponent<MeshFilter>();
                        if (filter && filter.sharedMesh != asset)
                        {
                            filter.sharedMesh = asset;
                            sceneChanged = true;
                        }
                        // The tile split can change the submesh count.
                        var renderer = slab.GetComponent<MeshRenderer>();
                        if (renderer && renderer.sharedMaterials.Length != asset.subMeshCount)
                        {
                            Material slabMaterial = renderer.sharedMaterial ? renderer.sharedMaterial :
                                _network.SlabMaterial;
                            renderer.sharedMaterials = Enumerable.Repeat(slabMaterial, asset.subMeshCount).ToArray();
                            sceneChanged = true;
                        }
                        if (renderer) _network.ApplyGround(renderer);
                    }
                    material ??= _network.SlabMaterial;
                    LoogaRoadSlabLods.Apply(slab, asset, material, _network);
                }
                order.Add(slab);
            }
            // Objects of slabs that no longer exist go. The others take the order of a full build.
            var keep = new HashSet<Transform>(order);
            for (int index = root.transform.childCount - 1; index >= 0; index--)
            {
                Transform child = root.transform.GetChild(index);
                if (keep.Contains(child)) continue;
                Object.DestroyImmediate(child.gameObject);
                update.Removed++;
                sceneChanged = true;
            }
            for (int index = 0; index < order.Count; index++)
            {
                if (order[index].GetSiblingIndex() == index) continue;
                order[index].SetSiblingIndex(index);
                sceneChanged = true;
            }
            state.Slabs = signatures;
            update.MeshSeconds = clock.Elapsed.TotalSeconds;

            clock.Restart();
            Trace("live: masks");
            sceneChanged |= UpdateMasks(state, network, stampRoot, update);
            update.MaskSeconds = clock.Elapsed.TotalSeconds;
            if (sceneChanged)
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
            return update;
        }

        // The lists of the slab that the live update builds and saves next.
        private static readonly SlabMesh LiveMesh = NewSlabMesh();

        // The slabs in the order of a full build: the road pieces, then the pads and the junction patches.
        private static IEnumerable<(string name, Piece piece, Pad pad, DeckSlab deck)> SlabSources(Network network)
        {
            foreach (Piece piece in network.Pieces) yield return (piece.Name, piece, null, null);
            foreach (Pad pad in network.Slabs) yield return (pad.Name, null, pad, null);
            foreach (DeckSlab deck in network.DeckSlabs) yield return (deck.Name, null, null, deck);
        }

        // The signature of each slab: its own shape, then the shapes of the slabs near it in build order.
        private static Dictionary<string, ulong> Signatures(Network network, out Dictionary<string, Rect> bounds)
        {
            var shapes = new List<(string name, Rect bounds, ulong shape)>();
            foreach ((string name, Piece piece, Pad pad, DeckSlab deck) in SlabSources(network))
            {
                shapes.Add(piece != null ? (name, PieceBounds(piece), PieceShape(piece)) :
                    pad != null ? (name, pad.Bounds, PadShape(pad)) : (name, DeckBounds(deck), DeckShape(deck)));
            }
            bounds = new Dictionary<string, Rect>();
            var signatures = new Dictionary<string, ulong>();
            for (int index = 0; index < shapes.Count; index++)
            {
                Rect reach = Grow(shapes[index].bounds, NeighbourReach);
                var hash = Hash.Start();
                hash.Add(shapes[index].shape);
                for (int other = 0; other < shapes.Count; other++)
                {
                    if (other != index && reach.Overlaps(shapes[other].bounds))
                    {
                        hash.Add(shapes[other].shape);
                    }
                }
                bounds[shapes[index].name] = shapes[index].bounds;
                signatures[shapes[index].name] = hash.Value;
            }
            return signatures;
        }

        private static Rect PieceBounds(Piece piece)
        {
            var extent = new Extent();
            foreach (Row row in piece.Untrimmed)
            {
                extent.Add(row.Left);
                extent.Add(row.RightEdge);
                extent.Add(row.Center);
            }
            foreach (Row row in piece.Rows)
            {
                extent.Add(row.Left);
                extent.Add(row.RightEdge);
                extent.Add(row.Center);
            }
            return Rect.MinMaxRect(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY);
        }

        // The values that the mesh of the piece reads from its own rows and ends, and the order of the piece.
        private static ulong PieceShape(Piece piece)
        {
            var hash = Hash.Start();
            hash.Add(piece.Index);
            hash.Add(piece.Name);
            foreach (Row row in piece.Untrimmed)
            {
                hash.Add(row.Left);
                hash.Add(row.RightEdge);
            }
            foreach (Row row in piece.Rows)
            {
                hash.Add(row.Center);
                hash.Add(row.Right);
                hash.Add(row.Half);
                hash.Add(row.Left);
                hash.Add(row.RightEdge);
            }
            foreach (End end in piece.Ends)
            {
                hash.Add(end.Pad != null ? PadShape(end.Pad) : 0UL);
                hash.Add(end.CutPoint);
                hash.Add(end.CutNormal);
                hash.Add(end.Host);
            }
            return hash.Value;
        }

        private static Rect DeckBounds(DeckSlab deck)
        {
            Vector2 a = Flat(deck.Start), b = Flat(deck.Finish);
            return Grow(Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x),
                Mathf.Max(a.y, b.y)), deck.Half);
        }

        private static ulong DeckShape(DeckSlab deck)
        {
            var hash = Hash.Start();
            hash.Add(deck.Name);
            hash.Add(Flat(deck.Start));
            hash.Add(Flat(deck.Finish));
            hash.Add(new Vector2(deck.Start.y, deck.Finish.y));
            hash.Add(deck.Half);
            return hash.Value;
        }

        private static ulong PadShape(Pad pad)
        {
            var hash = Hash.Start();
            hash.Add(pad.Name);
            foreach (Vector2 point in pad.Outline) hash.Add(point);
            hash.Add(pad.Center);
            hash.Add(pad.Origin);
            return hash.Value;
        }

        // Each road piece and each pad or patch with a hash of what its mask shapes read and the area that they cover.
        private static Dictionary<string, (ulong shape, Rect area)> MaskParts(Network network)
        {
            var parts = new Dictionary<string, (ulong, Rect)>();
            foreach (Piece piece in network.Pieces)
            {
                if (piece.Rows.Count < 2) continue;
                var hash = Hash.Start();
                Rect area = SegmentShape(piece.Rows[0], piece.Rows[1]).Area;
                foreach (Row row in piece.Rows)
                {
                    hash.Add(row.Center);
                    hash.Add(row.Half);
                }
                for (int row = 1; row + 1 < piece.Rows.Count; row++)
                {
                    area = Union(area, SegmentShape(piece.Rows[row], piece.Rows[row + 1]).Area);
                }
                parts["Road " + piece.Name] = (hash.Value, area);
            }
            foreach (Pad pad in network.Slabs)
            {
                var hash = Hash.Start();
                foreach (Vector2 point in pad.Outline) hash.Add(point);
                hash.Add(pad.Center);
                parts["Pad " + pad.Name] = (hash.Value, PadMaskArea(pad));
            }
            foreach (Pad deck in network.DeckEnds)
            {
                var hash = Hash.Start();
                foreach (Vector2 point in deck.Outline) hash.Add(point);
                parts["Bridge " + deck.Name] = (hash.Value, PadMaskArea(deck));
            }
            return parts;
        }

        // Fills the changed mask parts again, or the whole masks when the grid changed. Returns true when a stamp
        // moved.
        private static bool UpdateMasks(LiveState state, Network network, Transform stampRoot, LiveUpdate update)
        {
            Transform shoulderStamp = _network.ShoulderStamp ? _network.ShoulderStamp.transform : null;
            Transform clearStamp = _network.ClearStamp ? _network.ClearStamp.transform : null;
            if (!shoulderStamp || !clearStamp ||
                !shoulderStamp.TryGetComponent(out LoogaStampTextureSource shoulderSource) ||
                !clearStamp.TryGetComponent(out LoogaStampTextureSource clearSource)) return false;

            MaskGrid grid = MaskGridFor(network);
            Dictionary<string, (ulong shape, Rect area)> parts = MaskParts(network);
            List<MaskShape> shapes = MaskShapes(network);
            if (!grid.Matches(state.Grid) || !state.ShoulderTexture || !state.ClearTexture)
            {
                state.Dispose();
                int size = grid.Width * grid.Depth;
                state.Shoulder = new byte[size];
                state.Clear = new byte[size];
                state.NextShoulder = new byte[size];
                state.NextClear = new byte[size];
                FillMasks(grid, shapes, new RectInt(0, 0, grid.Width, grid.Depth), state.Shoulder, state.Clear);
                state.ShoulderTexture = CreateMask("Road shoulder (live)", grid, state.Shoulder);
                state.ClearTexture = CreateMask("Road clear (live)", grid, state.Clear);
                state.Grid = grid;
                state.MaskParts = parts;
                update.MasksReplaced = true;
                bool moved = PlaceMaskStamp(shoulderStamp, stampRoot, grid.Box);
                moved |= PlaceMaskStamp(clearStamp, stampRoot, grid.Box);
                shoulderSource.SetFalloffTexture(state.ShoulderTexture);
                clearSource.SetFalloffTexture(state.ClearTexture);
                return moved;
            }

            Rect? changed = null;
            foreach (KeyValuePair<string, (ulong shape, Rect area)> part in parts)
            {
                if (state.MaskParts.TryGetValue(part.Key, out (ulong shape, Rect area) old))
                {
                    if (old.shape == part.Value.shape) continue;
                    changed = Union(changed, old.area);
                }
                changed = Union(changed, part.Value.area);
            }
            foreach (KeyValuePair<string, (ulong shape, Rect area)> old in state.MaskParts)
            {
                if (!parts.ContainsKey(old.Key))
                {
                    changed = Union(changed, old.Value.area);
                }
            }
            state.MaskParts = parts;
            if (!changed.HasValue) return false;
            RectInt cells = grid.Cells(changed.Value);
            if (cells.width == 0 || cells.height == 0) return false;

            FillMasks(grid, shapes, cells, state.NextShoulder, state.NextClear);
            // Only the texels that differ count, so the stamps update the smallest area.
            int x0 = int.MaxValue, z0 = int.MaxValue, x1 = -1, z1 = -1;
            for (int z = cells.yMin; z < cells.yMax; z++)
            {
                for (int x = cells.xMin; x < cells.xMax; x++)
                {
                    int index = z * grid.Width + x;
                    if (state.NextShoulder[index] == state.Shoulder[index] &&
                        state.NextClear[index] == state.Clear[index]) continue;
                    x0 = Mathf.Min(x0, x);
                    x1 = Mathf.Max(x1, x);
                    z0 = Mathf.Min(z0, z);
                    z1 = Mathf.Max(z1, z);
                }
            }
            if (x1 < 0) return false;
            var texels = new RectInt(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
            CopyTexels(grid, texels, state.NextShoulder, state.Shoulder, state.ShoulderTexture);
            CopyTexels(grid, texels, state.NextClear, state.Clear, state.ClearTexture);
            Rect region = Rect.MinMaxRect(grid.Box.xMin + (x0 - MipReach) * grid.Cell, grid.Box.yMin +
                (z0 - MipReach) * grid.Cell,
                grid.Box.xMin + (x1 + 1 + MipReach) * grid.Cell, grid.Box.yMin + (z1 + 1 + MipReach) * grid.Cell);
            update.MaskRegion = region;
            shoulderSource.SetFalloffTexture(state.ShoulderTexture, region);
            clearSource.SetFalloffTexture(state.ClearTexture, region);
            return false;
        }

        // An in-memory mask with the sampling of the imported mask PNGs: linear, clamped, bilinear, box-filtered mips.
        private static Texture2D CreateMask(string name, MaskGrid grid, byte[] texels)
        {
            var texture = new Texture2D(grid.Width, grid.Depth, TextureFormat.R8, true, true)
            {
                name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 1,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.GetPixelData<byte>(0).CopyFrom(texels);
            texture.Apply(true, false);
            return texture;
        }

        private static void CopyTexels(MaskGrid grid, RectInt texels, byte[] source, byte[] target, Texture2D texture)
        {
            NativeArray<byte> level = texture.GetPixelData<byte>(0);
            for (int z = texels.yMin; z < texels.yMax; z++)
            {
                int start = z * grid.Width + texels.xMin;
                Array.Copy(source, start, target, start, texels.width);
                NativeArray<byte>.Copy(source, start, level, start, texels.width);
            }
            texture.Apply(true, false);
        }

        /// <summary>
        /// Compares the slab objects, their mesh assets, the mask stamps and the live masks with a full build in
        /// memory. Writes nothing. The mask PNGs are compared too while they exist.
        /// </summary>
        internal static string CompareWithBuild(LoogaRoadNetwork roadNetwork, LiveState state)
        {
            _network = roadNetwork;
            Transform stampRoot = StampRoot();
            var clock = Stopwatch.StartNew();
            Network network = Plan(stampRoot, Tiles());
            GameObject root = _network.SlabRoot ? _network.SlabRoot.gameObject : null;
            var report = new StringBuilder();
            var problems = new List<string>();
            var expected = new List<string>();
            int vertices = 0;
            foreach (SlabMesh slab in Meshes(network))
            {
                expected.Add(slab.Name);
                vertices += slab.Vertices.Count;
                Transform slabObject = root ? root.transform.Find(slab.Name) : null;
                Mesh mesh = slabObject && slabObject.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                string problem = !slabObject ? "no object" : !mesh ? "no mesh" :
                    AssetDatabase.GetAssetPath(mesh) != MeshPath(slab.Name) ? "other mesh asset" :
                    !slabObject.position.Equals(slab.Origin) ? "position" : MeshDifference(mesh, slab);
                if (problem != null)
                {
                    problems.Add(slab.Name + ": " + problem);
                }
            }
            List<string> names = root ? root.transform.Cast<Transform>().Select(child => child.name).ToList() :
                new List<string>();
            report.AppendLine(expected.Count + " slabs, " + vertices.ToString("N0") + " vertices; " + problems.Count +
                " differ" + (problems.Count > 0 ? " (" + string.Join("; ", problems.Take(10)) + ")" : "") + "; " +
                names.Except(expected).Count() + " extra objects; order " + (names.SequenceEqual(expected) ? "same" :
                    "differs") +
                ". Build in memory " + clock.Elapsed.TotalSeconds.ToString("F2") + " s.");

            MaskGrid grid = MaskGridFor(network);
            var shoulder = new byte[grid.Width * grid.Depth];
            var clear = new byte[shoulder.Length];
            FillMasks(grid, MaskShapes(network), new RectInt(0, 0, grid.Width, grid.Depth), shoulder, clear);
            bool boxes = MaskStampMatches(stampRoot, _network.ShoulderStamp, grid) &&
                MaskStampMatches(stampRoot, _network.ClearStamp, grid);
            report.AppendLine("Masks " + grid.Width + " x " + grid.Depth + ": stamp boxes " + (boxes ? "match" :
                "DIFFER") +
                "; PNG shoulder " + PngDifference(ShoulderMaskPath, grid, shoulder) + ", PNG clear " +
                PngDifference(ClearMaskPath, grid, clear) + "; live shoulder " + LiveDifference(state?.Grid, grid,
                    state?.Shoulder,
                    state?.ShoulderTexture, shoulder) + ", live clear " + LiveDifference(state?.Grid, grid,
                        state?.Clear,
                    state?.ClearTexture, clear) + ".");
            return report.ToString();
        }

        private static string MeshDifference(Mesh mesh, SlabMesh slab)
        {
            Vector3[] vertices = mesh.vertices;
            if (vertices.Length != slab.Vertices.Count) return "vertex count " + vertices.Length + " vs " +
                slab.Vertices.Count;
            float largest = 0f;
            int moved = 0;
            for (int index = 0; index < vertices.Length; index++)
            {
                if (vertices[index].Equals(slab.Vertices[index])) continue;
                moved++;
                largest = Mathf.Max(largest, (vertices[index] - slab.Vertices[index]).magnitude);
            }
            if (moved > 0) return moved + " vertices moved (largest " + largest.ToString("F4") + " m)";
            if (mesh.subMeshCount != slab.SubMeshStarts.Count) return "submeshes";
            if (!mesh.triangles.SequenceEqual(slab.Triangles)) return "triangles";
            mesh.GetUVs(1, RoadScratch);
            if (!RoadScratch.SequenceEqual(slab.Road)) return "road coordinates";
            mesh.GetColors(JoinsScratch);
            return JoinsScratch.SequenceEqual(slab.Joins) ? null : "joins";
        }

        private static bool MaskStampMatches(Transform stampRoot, MonoBehaviour maskStamp, MaskGrid grid)
        {
            if (!maskStamp) return false;
            Transform stamp = maskStamp.transform;
            Vector3 position = stamp.position;
            Vector3 scale = stamp.localScale;
            Vector3 parentScale = stampRoot.lossyScale;
            return position.Equals(new Vector3(grid.Box.center.x, 0f, grid.Box.center.y)) &&
                scale.Equals(new Vector3(grid.Box.width / parentScale.x, 1f / parentScale.y,
                    grid.Box.height / parentScale.z));
        }

        private static string PngDifference(string path, MaskGrid grid, byte[] texels)
        {
            if (!File.Exists(path)) return "absent";
            var png = new Texture2D(2, 2);
            try
            {
                png.LoadImage(File.ReadAllBytes(path));
                if (png.width != grid.Width || png.height != grid.Depth) return "size " + png.width + " x " +
                    png.height;
                Color32[] pixels = png.GetPixels32();
                return Difference(texels, index => pixels[index].r);
            }
            finally
            {
                Object.DestroyImmediate(png);
            }
        }

        private static string LiveDifference(MaskGrid liveGrid, MaskGrid grid, byte[] live, Texture2D texture,
            byte[] texels)
        {
            if (live == null || !texture) return "absent";
            if (!grid.Matches(liveGrid)) return "other grid";
            NativeArray<byte> level = texture.GetPixelData<byte>(0);
            string array = Difference(texels, index => live[index]);
            string data = Difference(texels, index => level[index]);
            return array == data ? array : array + " (texture " + data + ")";
        }

        private static string Difference(byte[] texels, Func<int, byte> other)
        {
            int differ = 0, largest = 0;
            for (int index = 0; index < texels.Length; index++)
            {
                int step = Mathf.Abs(texels[index] - other(index));
                if (step == 0) continue;
                differ++;
                largest = Mathf.Max(largest, step);
            }
            return differ == 0 ? "identical" : differ + " texels differ (up to " + largest + ")";
        }

        private static Rect Grow(Rect rect, float distance)
        {
            return Rect.MinMaxRect(rect.xMin - distance, rect.yMin - distance, rect.xMax + distance, rect.yMax +
                distance);
        }

        private static Rect Union(Rect a, Rect b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax),
                Mathf.Max(a.yMax, b.yMax));
        }

        private static Rect Union(Rect? a, Rect b)
        {
            return a.HasValue ? Union(a.Value, b) : b;
        }

        // FNV-1a over 32-bit values. Floats hash by their bits, so any change of a value changes the hash.
        private struct Hash
        {
            public ulong Value;

            public static Hash Start() => new Hash { Value = 14695981039346656037UL };

            public void Add(int value)
            {
                Value = (Value ^ (uint)value) * 1099511628211UL;
            }

            public void Add(ulong value)
            {
                Add((int)value);
                Add((int)(value >> 32));
            }

            public void Add(float value) => Add(BitConverter.SingleToInt32Bits(value));

            public void Add(Vector2 value)
            {
                Add(value.x);
                Add(value.y);
            }

            public void Add(Vector3 value)
            {
                Add(value.x);
                Add(value.y);
                Add(value.z);
            }

            public void Add(string value)
            {
                Add(value.Length);
                foreach (char character in value) Add(character);
            }
        }
        #endregion

        #region Mesh
        private static Material RequireMaterial()
        {
            return _network.SlabMaterial
                ? _network.SlabMaterial
                : throw new MissingReferenceException("Set the slab material of the road network " + _network.name +
                    ".");
        }

        private static Transform AddSlab(Transform root, string name, Mesh mesh, Vector3 origin, Material material)
        {
            var slab = new GameObject(name);
            slab.transform.SetParent(root, false);
            slab.transform.position = origin;
            slab.isStatic = true;
            slab.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = slab.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = Enumerable.Repeat(material, mesh.subMeshCount).ToArray();
            // A 10 cm slab casts no useful shadow. When it casts, it shadows its own rounded edge as a dark band.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            _network.ApplyGround(renderer);
            return slab.transform;
        }

        // An existing mesh asset is cleared and filled through the Mesh API. CopySerialized into it does not reach the
        // GPU copy, so the scene would draw the old shape.
        private static Mesh SaveMesh(SlabMesh slab)
        {
            string name = MeshPrefix + slab.Name;
            List<Vector3> vertices = slab.Vertices;
            Vector3 origin = slab.Origin;
            string path = MeshPath(slab.Name);
            Directory.CreateDirectory(Folder + "/Meshes");
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool created = !mesh;
            if (created)
            {
                mesh = new Mesh { name = name };
            }
            mesh.Clear();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            List<int> starts = slab.SubMeshStarts;
            mesh.subMeshCount = starts.Count;
            for (int index = 0; index < starts.Count; index++)
            {
                int end = index + 1 < starts.Count ? starts[index + 1] : slab.Triangles.Count;
                mesh.SetTriangles(slab.Triangles, starts[index], end - starts[index], index);
            }
            // World XZ in meters over the 9 m asphalt tile, so the slabs line up with each other.
            UvScratch.Clear();
            foreach (Vector3 vertex in vertices)
            {
                UvScratch.Add(new Vector2((vertex.x + origin.x) / 9f, (vertex.z + origin.z) / 9f));
            }
            mesh.SetUVs(0, UvScratch);
            mesh.SetUVs(1, slab.Road);
            mesh.SetColors(slab.Joins);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            if (created)
            {
                AssetDatabase.CreateAsset(mesh, path);
            }
            else
            {
                EditorUtility.SetDirty(mesh);
            }
            return mesh;
        }

        private static string MeshPath(string slab)
        {
            return Folder + "/Meshes/" + MeshPrefix + slab + ".asset";
        }

        private static void AddTriangle(List<int> triangles, List<Vector3> vertices, int a, int b, int c)
        {
            // Upward faces: the normal of a top triangle must point up.
            Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            if (normal.y < 0f) (b, c) = (c, b);
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
        }

        // A quad a, b, c, d in order around its outline. Every slab face points up or outward and up.
        private static void AddQuad(List<int> triangles, List<Vector3> vertices, int a, int b, int c, int d)
        {
            Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[d] - vertices[a]);
            if (normal.y < 0f)
            {
                (b, d) = (d, b);
            }
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
            triangles.Add(a); triangles.Add(c); triangles.Add(d);
        }
        #endregion

        // Point in a quad with corners in order around it. The quads of a road are convex.
        private static bool InQuad(Vector2[] quad, Vector2 point)
        {
            bool positive = false, negative = false;
            for (int corner = 0; corner < 4; corner++)
            {
                Vector2 a = quad[corner];
                Vector2 b = quad[(corner + 1) % 4];
                float side = (b.x - a.x) * (point.y - a.y) - (b.y - a.y) * (point.x - a.x);
                if (side > 0f) positive = true;
                if (side < 0f) negative = true;
                if (positive && negative) return false;
            }
            return true;
        }

        // A broad swell of a few meters with a finer ripple on it, up to about Undulation high.
        private static float Wave(Vector2 point)
        {
            float broad = Mathf.PerlinNoise(point.x * 0.3f + 41.3f, point.y * 0.3f + 17.9f) - 0.5f;
            float fine = Mathf.PerlinNoise(point.x * 0.65f + 5.1f, point.y * 0.65f + 29.4f) - 0.5f;
            return Undulation * (1.6f * broad + 0.8f * fine);
        }

        private static float EdgeOffset(Vector2 point)
        {
            float broad = Mathf.PerlinNoise(point.x * 0.21f + 13.1f, point.y * 0.21f + 7.7f) - 0.5f;
            float fine = Mathf.PerlinNoise(point.x * 1.3f + 3.3f, point.y * 1.3f + 1.9f) - 0.5f;
            return EdgeNoise * (1.4f * broad + 0.6f * fine);
        }

        // The width of the rounded edge outside the outline at a point: broad and fine noise between LipWidthMin and
        // LipWidthMax.
        private static float LipWidthAt(Vector2 point)
        {
            float broad = Mathf.PerlinNoise(point.x / 7f + 61.7f, point.y / 7f + 19.3f);
            float fine = Mathf.PerlinNoise(point.x / 1.7f + 8.9f, point.y / 1.7f + 37.1f);
            float mix = Mathf.InverseLerp(0.3f, 0.7f, 0.75f * broad + 0.25f * fine);
            return Mathf.Lerp(LipWidthMin, LipWidthMax, mix);
        }

        private static float Height(UnityEngine.Terrain[] tiles, Vector2 point)
        {
            if (_heights != null) return _heights.Get(tiles, point);
            return SampleHeight(tiles, point);
        }

        private static float SampleHeight(UnityEngine.Terrain[] tiles, Vector2 point)
        {
            var position = new Vector3(point.x, 0f, point.y);
            foreach (UnityEngine.Terrain tile in tiles)
            {
                Vector3 origin = tile.GetPosition();
                Vector3 size = tile.terrainData.size;
                if (point.x >= origin.x && point.y >= origin.z && point.x < origin.x + size.x && point.y < origin.z +
                    size.z)
                {
                    return tile.SampleHeight(position) + origin.y;
                }
            }
            throw new InvalidOperationException("No terrain at " + point + ".");
        }

        private static float SegmentDistance(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 segment = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(point - a, segment) / Mathf.Max(segment.sqrMagnitude, 1e-6f));
            return Vector2.Distance(point, a + segment * t);
        }

        private static Vector2 Flat(Vector3 point)
        {
            return new Vector2(point.x, point.z);
        }
    }
}
