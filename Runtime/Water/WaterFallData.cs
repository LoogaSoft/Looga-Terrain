using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// A foam shell over the falling water. A shell stands off the sheet and gives the white water volume. Distances are
    /// in fall units: metres for a fall with a scale of 1.
    /// </summary>
    [Serializable]
    public struct WaterFallShell
    {
        [Tooltip("Distance of the shell edges inside each jet edge.")]
        [SerializeField, Min(0f)] private float _inset;
        [Tooltip("Distance downstream of the lip where the shell starts.")]
        [SerializeField] private float _start;
        [Tooltip("Distance of the shell off the sheet at the lip.")]
        [SerializeField, Min(0f)] private float _lift;
        [Tooltip("More distance off the sheet at the plunge. The jet looks thicker lower down.")]
        [SerializeField, Min(0f)] private float _growth;
        [Tooltip("Moves the texture and lump pattern across the flow, so stacked shells differ.")]
        [SerializeField] private float _patternShift;

        public WaterFallShell(float inset, float start, float lift, float growth, float patternShift)
        {
            _inset = inset;
            _start = start;
            _lift = lift;
            _growth = growth;
            _patternShift = patternShift;
        }

        public float Inset => _inset;
        public float Start => _start;
        public float Lift => _lift;
        public float Growth => _growth;
        public float PatternShift => _patternShift;
    }

    /// <summary>
    /// Mesh settings of a fall sheet. Distances are in fall units: metres for a fall with a scale of 1. A fall object
    /// with a smaller scale makes the streak, foam and lump patterns larger relative to the fall.
    /// </summary>
    [Serializable]
    public sealed class WaterFallSettings
    {
        [Tooltip("Distance upstream of the lip where the sheet starts. The water that feeds the fall ends exactly " +
                 "there (weld line), and the sheet carries its look over the lip, so no seam shows.")]
        [SerializeField, Min(0f)] private float _sheetStart = 6f;
        [Tooltip("Distance before the weld line over which the waves of the feeding water calm down, in metres. On " +
                 "the weld line the water does not move, so the water and the sheet meet with the same positions.")]
        [SerializeField, Min(0.1f)] private float _weldCalm = 10f;
        [Tooltip("Strength of the ripples in the light at the brink, as a multiple of their strength on the feeding " +
                 "water. Below 1 the water smooths out as it speeds up over a smooth crest; above 1 it gets choppy.")]
        [SerializeField, Range(0f, 3f)] private float _lipRipples = 0.4f;
        [Tooltip("Rushing water before the brink, 0 to 1: boils, white streaks and bubbles that make the water cloudy, " +
                 "so the bed under the lip does not show. It grows over the calm distance and the sheet start.")]
        [SerializeField, Range(0f, 1f)] private float _lipRush;
        [Tooltip("Distance between the mesh rows along the flow.")]
        [SerializeField, Min(0.05f)] private float _rowSpacing = 0.6f;
        [Tooltip("Distance between the mesh columns across the flow.")]
        [SerializeField, Min(0.05f)] private float _columnSpacing = 0.75f;
        [Tooltip("Radius of the round bend where the water turns over the brink, from the level of the feeding " +
                 "water down to the falling sheet. 0 = the shape of a free jet, which turns down within a few metres.")]
        [SerializeField, Min(0f)] private float _brinkRadius;
        [Tooltip("Radius of the round bend where the falling water turns into the water below.")]
        [SerializeField, Min(0f)] private float _bendRadius = 3f;
        [Tooltip("Distance that the sheet runs out flat over the water below, past the plunge. It fades out there.")]
        [SerializeField, Min(0f)] private float _runOut = 4f;
        [Tooltip("Height of the run-out above the water below.")]
        [SerializeField, Min(0f)] private float _runOutLift = 0.05f;
        [Tooltip("Smallest water thickness of the sheet. The thickness comes from the flow per metre and the speed.")]
        [SerializeField, Min(0.01f)] private float _minimumThickness = 0.45f;
        [Tooltip("Length of the streak texture along the flow at rest. With the stretch speed it sets the streak " +
                 "length at the mean speed of the drop. The streaks move exactly with the water, so they are " +
                 "shorter where the water is slower and longer where it is faster.")]
        [SerializeField, Min(0.1f)] private float _streakLength = 2.2f;
        [Tooltip("Speed at which the streak length is twice its length at rest, in fall units per second.")]
        [SerializeField, Min(0.1f)] private float _streakStretchSpeed = 12f;
        [Tooltip("Foam shells over each jet, in drawing order.")]
        [SerializeField] private List<WaterFallShell> _shells = new()
        {
            new WaterFallShell(0.3f, 0.3f, 0.35f, 1.1f, 0f),
            new WaterFallShell(0.6f, 1.5f, 0.8f, 2f, 7.3f)
        };

        public float SheetStart => _sheetStart;
        public float WeldCalm => Mathf.Max(0.1f, _weldCalm);
        public float LipRipples => Mathf.Clamp(_lipRipples, 0f, 3f);
        public float LipRush => Mathf.Clamp01(_lipRush);
        public float RowSpacing => Mathf.Max(0.05f, _rowSpacing);
        public float ColumnSpacing => Mathf.Max(0.05f, _columnSpacing);
        public float BrinkRadius => _brinkRadius;
        public float BendRadius => _bendRadius;
        public float RunOut => _runOut;
        public float RunOutLift => _runOutLift;
        public float MinimumThickness => Mathf.Max(0.01f, _minimumThickness);
        public float StreakLength => Mathf.Max(0.1f, _streakLength);
        public float StreakStretchSpeed => Mathf.Max(0.1f, _streakStretchSpeed);
        public IReadOnlyList<WaterFallShell> Shells => _shells;

        /// <summary>Copies all values from another settings object.</summary>
        public void CopyFrom(WaterFallSettings other)
        {
            if (other == null) return;

            _sheetStart = other._sheetStart;
            _weldCalm = other._weldCalm;
            _lipRipples = other._lipRipples;
            _lipRush = other._lipRush;
            _rowSpacing = other._rowSpacing;
            _columnSpacing = other._columnSpacing;
            _brinkRadius = other._brinkRadius;
            _bendRadius = other._bendRadius;
            _runOut = other._runOut;
            _runOutLift = other._runOutLift;
            _minimumThickness = other._minimumThickness;
            _streakLength = other._streakLength;
            _streakStretchSpeed = other._streakStretchSpeed;
            _shells = new List<WaterFallShell>(other._shells);
        }

        internal void AppendTo(ref Hash128 hash)
        {
            hash.Append(_sheetStart);
            hash.Append(_weldCalm);
            hash.Append(_lipRipples);
            hash.Append(_lipRush);
            hash.Append(_rowSpacing);
            hash.Append(_columnSpacing);
            hash.Append(_brinkRadius);
            hash.Append(_bendRadius);
            hash.Append(_runOut);
            hash.Append(_runOutLift);
            hash.Append(_minimumThickness);
            hash.Append(_streakLength);
            hash.Append(_streakStretchSpeed);
            hash.Append(_shells.Count);
            foreach (WaterFallShell shell in _shells)
            {
                WaterFallShell value = shell;
                hash.Append(ref value);
            }
        }
    }

    /// <summary>
    /// A solved fall of a <see cref="LoogaWaterNetwork"/>: a channel end that drops freely into the water below. The
    /// network makes a fall sheet and a plunge zone for it.
    /// </summary>
    public sealed class WaterNetworkFall
    {
        internal readonly List<WaterFallPoint> PathList = new();
        internal WaterFallEdge Edge;
        // Index of the path point where the falling water meets the water below.
        internal int PlungeIndex;

        internal WaterNetworkFall(int connection, int channel, int receiver, LoogaWaterFall fallOverride)
        {
            Connection = connection;
            Channel = channel;
            Receiver = receiver;
            Override = fallOverride;
        }

        /// <summary>Connection index in the network.</summary>
        public int Connection { get; }
        /// <summary>Element index of the channel that falls.</summary>
        public int Channel { get; }
        /// <summary>Element index of the water that takes the fall.</summary>
        public int Receiver { get; }
        /// <summary>The fall component that shapes this fall, or null when the network generates the fall.</summary>
        public LoogaWaterFall Override { get; }
        /// <summary>World centre of the lip, at the upstream water level.</summary>
        public Vector3 Lip { get; internal set; }
        /// <summary>World centre of the plunge, where the falling water meets the water below.</summary>
        public Vector3 Plunge { get; internal set; }
        /// <summary>World XZ unit direction of the flow over the lip.</summary>
        public Vector2 Direction { get; internal set; }
        /// <summary>Lip width, in metres.</summary>
        public float Width { get; internal set; }
        /// <summary>Upstream water level minus the level below, in metres.</summary>
        public float Drop { get; internal set; }
        /// <summary>Flow per metre of lip width, in square metres per second.</summary>
        public float UnitDischarge { get; internal set; }
        /// <summary>Speed of the water toward the lip, in metres per second.</summary>
        public float LipSpeed { get; internal set; }
        /// <summary>Speed of the falling water where it meets the water below, in metres per second.</summary>
        public float ImpactSpeed { get; internal set; }
        /// <summary>Surface speed of the water that the plunge pushes out, in metres per second.</summary>
        public float PlungeSpeed { get; internal set; }
        /// <summary>Distance over which the plunge flow loses most of its speed, in metres.</summary>
        public float PlungeDecay { get; internal set; }
        /// <summary>Changes when any input of the fall sheet changes.</summary>
        public Hash128 Hash { get; internal set; }
        /// <summary>World points of the sheet centreline, from upstream of the lip to the run-out end.</summary>
        public void GetPath(List<Vector3> points)
        {
            points.Clear();
            foreach (WaterFallPoint point in PathList)
            {
                points.Add(Edge.FrameToWorld.MultiplyPoint3x4(new Vector3(0f, point.Surface.y, point.Surface.x)));
            }
        }
    }

    /// <summary>
    /// A solved crest fall of a <see cref="LoogaWaterNetwork"/>: an area body that spills over a lip line into the
    /// water below. The lip, the outward normals and the landing have one point for each lip sample.
    /// </summary>
    public sealed class WaterNetworkCrest
    {
        internal readonly List<Vector3> LipList = new();
        internal readonly List<Vector2> NormalList = new();
        internal readonly List<Vector3> LandingList = new();
        // The sheet path from upstream of the lip to the run-out end, the same at each lip point. The frame of the
        // edge is the world: heights are world heights and the downstream distance runs along each normal.
        internal readonly List<WaterFallPoint> PathList = new();
        internal WaterFallEdge Edge;
        internal int PlungeIndex = -1;

        internal WaterNetworkCrest(LoogaWaterFall fall, int body)
        {
            Fall = fall;
            Body = body;
            Receiver = -1;
        }

        /// <summary>The fall component that marks the crest.</summary>
        public LoogaWaterFall Fall { get; }
        /// <summary>Element index of the body that spills.</summary>
        public int Body { get; }
        /// <summary>Element index of the water below, or -1 when the crest finds none.</summary>
        public int Receiver { get; internal set; }
        /// <summary>World points of the lip at the body level.</summary>
        public IReadOnlyList<Vector3> Lip => LipList;
        /// <summary>World XZ unit directions away from the body, one for each lip point.</summary>
        public IReadOnlyList<Vector2> Normals => NormalList;
        /// <summary>World points where the free jet meets the water below, one for each lip point.</summary>
        public IReadOnlyList<Vector3> Landing => LandingList;
        /// <summary>Lip length, in metres.</summary>
        public float Length { get; internal set; }
        /// <summary>Body level minus the level below, in metres. 0 without water below.</summary>
        public float Drop { get; internal set; }
        /// <summary>Flow per metre of lip, in square metres per second.</summary>
        public float UnitDischarge { get; internal set; }
        /// <summary>True when the fall component sets the flow. False when the inflow of the body sets it.</summary>
        public bool DesignDischarge { get; internal set; }
        /// <summary>Critical depth over the lip, in metres.</summary>
        public float LipDepth { get; internal set; }
        /// <summary>Critical speed over the lip, in metres per second.</summary>
        public float LipSpeed { get; internal set; }
        /// <summary>Speed of the falling water where it meets the water below, in metres per second.</summary>
        public float ImpactSpeed { get; internal set; }
        /// <summary>Distance from the lip to the plunge along each normal, in metres. 0 without a sheet path.</summary>
        public float PlungeDistance { get; internal set; }
        /// <summary>Changes when any input of the crest sheet changes.</summary>
        public Hash128 Hash { get; internal set; }
    }

    /// <summary>
    /// The inputs of one fall sheet in its frame. The lip lies along the frame X axis at z = 0, centred at x = 0. The
    /// water flows toward frame +Z. Heights are frame Y. Lengths, speeds and gravity are in frame units.
    /// </summary>
    internal struct WaterFallEdge
    {
        public Matrix4x4 FrameToWorld;
        // Metres per frame unit.
        public float Scale;
        // Water level at the lip, bed height at the lip and water level below.
        public float Level;
        public float Crest;
        public float Landing;
        public float Width;
        public float LipSpeed;
        public float UnitDischarge;
        public float Gravity;
        public int Bays;
        public float PierWidth;
        public bool Walls;
        // World XZ unit flow direction and world vertex spacing of the river, for the lip surface pass.
        public Vector2 FlowDirection;
        public float LipSpacing;
        // Regime values of the river at the lip (x = turbulence, y = aeration).
        public Vector2 LipRegime;
        // Plunge surface speed (m/s) and the distance (m) over which it decays, in world units.
        public float PlungeSpeed;
        public float PlungeDecay;
    }

    /// <summary>One point of the fall sheet surface along the flow, in frame units.</summary>
    internal struct WaterFallPoint
    {
        // Downstream distance and height of the surface.
        public Vector2 Surface;
        public Vector2 Normal;
        // Seconds since the start, from the speed.
        public float Travel;
        // Below 0 upstream of the lip, 0 to 1 down the drop, above 1 past the plunge.
        public float Progress;
        // Distance along the surface from the start.
        public float Distance;
        // Distance along the surface past the lip. Negative before it.
        public float Brink;
        public float Thickness;
        // Streak texture tiles along the flow from the start.
        public float Stretch;
        public float Speed;
    }
}
