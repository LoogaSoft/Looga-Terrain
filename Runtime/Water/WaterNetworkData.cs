using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Flow state of the water at one point of a <see cref="LoogaWaterNetwork"/>. The order goes from calm to falling
    /// water. Shaders get continuous values. The regime is for geometry, such as fall sheets, and for the debug view.
    /// </summary>
    public enum WaterRegime
    {
        Still = 0,
        Flowing = 1,
        Fast = 2,
        Rapids = 3,
        Cascade = 4,
        FreeFall = 5
    }

    /// <summary>How two network elements join. The flow goes from the upstream element to the downstream element.</summary>
    public enum WaterJoint
    {
        // The end of a channel is in or near a body.
        ChannelToBody = 0,
        // The start of a channel is in or near a body.
        BodyToChannel = 1,
        // The end of a channel is at the start of the next channel.
        Series = 2,
        // The end of a channel is on another channel.
        Confluence = 3,
        // The start of a channel is on another channel.
        Split = 4
    }

    public enum WaterIssueSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    /// <summary>Settings of the network solve. The regime thresholds are first values for tuning.</summary>
    [Serializable]
    public struct WaterNetworkSettings
    {
        [Tooltip("Distance within which a channel end connects to a body outline or to another channel, in metres.")]
        [SerializeField, Min(0f)] private float _snapDistance;
        [Tooltip("Distance between the solve points along a channel, in metres.")]
        [SerializeField, Min(0.5f)] private float _sampleSpacing;
        [Tooltip("Length over which the slope at each point is measured, in metres. Longer values ignore small steps.")]
        [SerializeField, Min(0f)] private float _slopeWindow;
        [Tooltip("Rise of the water height downstream that the validation ignores, in metres.")]
        [SerializeField, Min(0f)] private float _heightTolerance;
        [Tooltip("Adds the steep-stream roughness of Jarrett (1984). Steep beds then slow the water as boulders and " +
                 "steps do. Off: only the Manning roughness of each channel applies.")]
        [SerializeField] private bool _steepRoughness;

        [Header("Regimes")]
        [Tooltip("Channel water slower than this is still, in metres per second.")]
        [SerializeField, Min(0f)] private float _stillSpeed;
        [Tooltip("Froude number from which the water is fast.")]
        [SerializeField, Min(0f)] private float _fastFroude;
        [Tooltip("Froude number from which the water is rapids.")]
        [SerializeField, Min(0f)] private float _rapidsFroude;
        [Tooltip("Slope from which the water is rapids, as a fraction. 0.03 = 3 %. Step-pool beds start near 3 %.")]
        [SerializeField, Min(0f)] private float _rapidsSlope;
        [Tooltip("Slope from which the water is a cascade, as a fraction. 0.15 = 15 %.")]
        [SerializeField, Min(0f)] private float _cascadeSlope;
        [Tooltip("Angle of the water surface from which a channel point is in free fall, in degrees.")]
        [SerializeField, Range(10f, 90f)] private float _fallAngle;
        [Tooltip("Height drop at a joint from which the water falls freely, in metres. An example is a river that " +
                 "ends above the level of a body.")]
        [SerializeField, Min(0f)] private float _fallDrop;

        public float SnapDistance => _snapDistance;
        public float SampleSpacing => Mathf.Max(0.5f, _sampleSpacing);
        public float SlopeWindow => _slopeWindow;
        public float HeightTolerance => _heightTolerance;
        public bool SteepRoughness => _steepRoughness;
        public float StillSpeed => _stillSpeed;
        public float FastFroude => _fastFroude;
        public float RapidsFroude => _rapidsFroude;
        public float RapidsSlope => _rapidsSlope;
        public float CascadeSlope => _cascadeSlope;
        public float FallAngle => _fallAngle;
        public float FallDrop => _fallDrop;

        public static WaterNetworkSettings Default => new()
        {
            _snapDistance = 10f,
            _sampleSpacing = 2f,
            _slopeWindow = 10f,
            _heightTolerance = 0.05f,
            _steepRoughness = true,
            _stillSpeed = 0.2f,
            _fastFroude = 0.5f,
            _rapidsFroude = 1f,
            _rapidsSlope = 0.03f,
            _cascadeSlope = 0.15f,
            _fallAngle = 55f,
            _fallDrop = 2f
        };

        internal void AppendTo(ref Hash128 hash)
        {
            hash.Append(_snapDistance);
            hash.Append(_sampleSpacing);
            hash.Append(_slopeWindow);
            hash.Append(_heightTolerance);
            hash.Append(_steepRoughness ? 1 : 0);
            hash.Append(_stillSpeed);
            hash.Append(_fastFroude);
            hash.Append(_rapidsFroude);
            hash.Append(_rapidsSlope);
            hash.Append(_cascadeSlope);
            hash.Append(_fallAngle);
            hash.Append(_fallDrop);
        }
    }

    /// <summary>The solved flow at one point of a channel centreline.</summary>
    public struct WaterChannelSample
    {
        // World position on the water surface.
        public Vector3 Position;
        // Knot position on the spline: curve index plus the fraction along the curve.
        public float Knot;
        // Horizontal distance from the channel start, in metres.
        public float Station;
        // Drop of the water surface per horizontal metre over the slope window. Positive downstream.
        public float Slope;
        // Angle of the water surface to the next point, in degrees. Positive downstream.
        public float Angle;
        // Full width, in metres.
        public float Width;
        // Design water depth over the bed, in metres.
        public float Depth;
        // Mean speed, in metres per second.
        public float Speed;
        // Discharge, in cubic metres per second.
        public float Discharge;
        public float Froude;
        public WaterRegime Regime;
        // Rough, fast-looking water from 0 to 1: it rises from the fast regime to the rapids regime.
        public float Turbulence;
        // White water from 0 to 1: it rises from the rapids regime to the cascade regime and is full in free fall.
        public float Aeration;
    }

    /// <summary>A body or one spline of a channel in a <see cref="LoogaWaterNetwork"/>.</summary>
    public sealed class WaterNetworkElement
    {
        internal readonly List<WaterChannelSample> SampleList = new();
        internal readonly List<List<Vector2>> OutlineList = new();
        internal readonly float[] RegimeLengthArray = new float[RegimeCount];
        // Bounds of each run of outline segments (x, y = minimum, z, w = maximum), for the outline tests of the network
        // graph. Made at the first test. The outlines do not change after the graph adds the element.
        internal Vector4[] OutlineChunks;

        /// <summary>Number of regimes in <see cref="WaterRegime"/>.</summary>
        public const int RegimeCount = (int)WaterRegime.FreeFall + 1;

        internal WaterNetworkElement(LoogaWaterBody body, int splineIndex, string name)
        {
            Body = body;
            SplineIndex = splineIndex;
            Name = name;
        }

        public LoogaWaterBody Body { get; }
        /// <summary>Spline of the body container. The spline index of a channel. 0 for a body.</summary>
        public int SplineIndex { get; }
        public string Name { get; }
        public bool IsChannel => Body.Shape == LoogaWaterBody.WaterShape.River;
        /// <summary>Water level of a body, in metres.</summary>
        public float Level { get; internal set; }
        /// <summary>World XZ rectangle of the element.</summary>
        public Rect Bounds { get; internal set; }
        /// <summary>Horizontal length of a channel, in metres.</summary>
        public float Length { get; internal set; }
        /// <summary>Channel discharge, in cubic metres per second. A body: the sum of its inflows.</summary>
        public float Discharge { get; internal set; }
        /// <summary>Discharge that comes in from upstream elements, in cubic metres per second.</summary>
        public float Inflow { get; internal set; }
        /// <summary>Solve points of a channel, from upstream to downstream. Empty for a body.</summary>
        public IReadOnlyList<WaterChannelSample> Samples => SampleList;
        /// <summary>World XZ outlines of a body. Empty for a channel.</summary>
        public IReadOnlyList<List<Vector2>> Outlines => OutlineList;

        /// <summary>Horizontal channel length with the regime, in metres.</summary>
        public float RegimeLength(WaterRegime regime)
        {
            return RegimeLengthArray[(int)regime];
        }
    }

    /// <summary>A joint between two network elements.</summary>
    public struct WaterNetworkConnection
    {
        public WaterJoint Joint;
        // Element indices in the network.
        public int Upstream;
        public int Downstream;
        // Sample index of each channel at the joint. -1 for a body.
        public int UpstreamSample;
        public int DownstreamSample;
        // World position of the joint at the upstream water height.
        public Vector3 Point;
        // World position where the water reaches the downstream element, at its water height. Below a fall, this is
        // the nearest point of the body outline when the lip is outside the outline.
        public Vector3 Landing;
        // Upstream water height minus downstream water height at the joint, in metres.
        public float Drop;
        // Width of the channel at the joint, in metres.
        public float Width;
        // True when the drop is a free fall.
        public bool Fall;
    }

    /// <summary>A validation result of the network.</summary>
    public struct WaterNetworkIssue
    {
        public WaterIssueSeverity Severity;
        // Element index in the network. -1 when the issue has no element.
        public int Element;
        public Vector3 Position;
        public string Message;
    }
}
