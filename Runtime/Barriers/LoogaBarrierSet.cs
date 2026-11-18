using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Barriers
{
    /// <summary>How a barrier piece follows the ground between its two ends.</summary>
    public enum BarrierSlopeMode
    {
        /// <summary>The piece stays level at the lower end height. Use it for walls and blocks.</summary>
        Step,
        /// <summary>The piece tilts along the slope, up to the maximum pitch. Use it for fences.</summary>
        Follow
    }

    public enum BarrierColliderMode
    {
        None,
        /// <summary>One box around each piece. It is cheap, but it closes holes in the piece.</summary>
        Box,
        /// <summary>A mesh collider for each mesh of the piece. It keeps holes, for example crouch holes.</summary>
        Mesh
    }

    /// <summary>One piece model of a barrier set and its selection weight.</summary>
    [Serializable]
    public sealed class BarrierPiece
    {
        [SerializeField] private GameObject _prefab;
        [Tooltip("Relative chance to use this piece. 0 = never.")]
        [Min(0f)]
        [SerializeField] private float _weight = 1f;

        public GameObject Prefab => _prefab;
        public float Weight => _weight;
    }

    /// <summary>
    /// Describes one barrier type, for example a wall or a fence. A <see cref="LoogaBarrierRun"/> places the pieces
    /// along splines. Piece models must use +Z as the run direction. All pieces of a set must have the same length.
    /// </summary>
    [CreateAssetMenu(menuName = "LoogaSoft/Terrain/Barrier Set", fileName = "Barrier Set")]
    public sealed class LoogaBarrierSet : ScriptableObject
    {
        [Header("Pieces")]
        [Tooltip("Pieces that fill the run. The run selects one piece for each segment by weight.")]
        [SerializeField] private List<BarrierPiece> _pieces = new();
        [Tooltip("Optional post at each joint and at the two run ends. Leave empty when the pieces include posts.")]
        [SerializeField] private GameObject _post;

        [Header("Size")]
        [Tooltip("Uniform scale of the pieces and the posts.")]
        [Min(0.01f)]
        [SerializeField] private float _scale = 1f;
        [Tooltip("Distance from one joint to the next at scale 1, in meters.")]
        [Min(0.1f)]
        [SerializeField] private float _pieceLength = 2f;
        [Tooltip("Pivot position of a piece relative to the segment centre on the ground at scale 1. " +
                 "X = right, Y = up, Z = along the run.")]
        [SerializeField] private Vector3 _pieceOffset;
        [Tooltip("Pivot position of a post relative to its joint on the ground at scale 1. X = right, Y = up, Z = along the run.")]
        [SerializeField] private Vector3 _postOffset;

        [Header("Placement")]
        [Tooltip("Step keeps pieces level. Follow tilts pieces along the slope.")]
        [SerializeField] private BarrierSlopeMode _slopeMode = BarrierSlopeMode.Step;
        [Tooltip("Largest tilt in Follow mode, in degrees.")]
        [Range(0f, 45f)]
        [SerializeField] private float _maximumPitch = 20f;
        [Tooltip("Scales each piece along the run so that the joints meet the spline exactly. " +
                 "Off: pieces keep their length and the run ends early.")]
        [SerializeField] private bool _stretchToFit = true;
        [Tooltip("Random turn of each piece around the vertical axis, in degrees.")]
        [Range(0f, 5f)]
        [SerializeField] private float _yawJitter;

        [Header("Gate")]
        [Tooltip("Gate model for the gates of a run. Its pivot is at the gate centre, and +Z is the run direction. " +
                 "Empty = a gate is an opening.")]
        [SerializeField] private GameObject _gate;
        [Tooltip("Distance between the two gate posts at scale 1, in meters. " +
                 "For a gate of two pieces, use two times the piece length.")]
        [Min(0.1f)]
        [SerializeField] private float _gateWidth = 3f;
        [Tooltip("Pivot position of the gate relative to the gate centre on the ground at scale 1. " +
                 "X = right, Y = up, Z = along the run.")]
        [SerializeField] private Vector3 _gateOffset;
        [Tooltip("The gate model has its own posts. The run places no set post at the gate edges.")]
        [SerializeField] private bool _gateHasPosts;
        [Tooltip("Leaf turn of an open gate, in degrees. Positive = toward the right (+X) side of the gate. " +
                 "The gate model needs a LoogaBarrierGate component.")]
        [Range(-180f, 180f)]
        [SerializeField] private float _gateOpenAngle = 90f;

        [Header("Collision")]
        [SerializeField] private BarrierColliderMode _colliders = BarrierColliderMode.Mesh;

        public IReadOnlyList<BarrierPiece> Pieces => _pieces;
        public GameObject Post => _post;
        public float Scale => _scale;
        public float PieceLength => _pieceLength;
        public Vector3 PieceOffset => _pieceOffset;
        public Vector3 PostOffset => _postOffset;
        public BarrierSlopeMode SlopeMode => _slopeMode;
        public float MaximumPitch => _maximumPitch;
        public bool StretchToFit => _stretchToFit;
        public float YawJitter => _yawJitter;
        public GameObject Gate => _gate;
        public Vector3 GateOffset => _gateOffset;
        public bool GateHasPosts => _gateHasPosts;
        public float GateOpenAngle => _gateOpenAngle;
        public BarrierColliderMode Colliders => _colliders;
        /// <summary>Distance from one joint to the next in meters, scale included.</summary>
        public float Step => _pieceLength * _scale;
        /// <summary>Distance between the two gate posts in meters, scale included.</summary>
        public float GateSpan => _gateWidth * _scale;

        /// <summary>Selects a piece by weight. Returns null when no piece has a prefab and a weight above 0.</summary>
        public GameObject PickPiece(System.Random random)
        {
            float total = 0f;
            foreach (BarrierPiece piece in _pieces)
            {
                if (piece != null && piece.Prefab && piece.Weight > 0f)
                {
                    total += piece.Weight;
                }
            }
            if (total <= 0f)
                return null;

            float pick = (float)random.NextDouble() * total;
            GameObject last = null;
            foreach (BarrierPiece piece in _pieces)
            {
                if (piece != null && piece.Prefab && piece.Weight > 0f)
                {
                    last = piece.Prefab;
                    pick -= piece.Weight;
                    if (pick <= 0f)
                        return last;
                }
            }
            // Rounding can leave a small positive rest. The last valid piece takes it.
            return last;
        }
    }
}
