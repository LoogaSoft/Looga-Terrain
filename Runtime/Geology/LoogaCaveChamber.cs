using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// One room of a <see cref="LoogaCaveSystem"/> with a flat floor. The object position is the floor centre, and the
    /// object turns the room about the vertical axis. A mined room is a box with rounded edges. A natural room is a
    /// dome with rock bumps. Rooms of different sizes that overlap at different floor heights make steps, for example
    /// the benches of a mined cavern.
    /// </summary>
    [AddComponentMenu("Looga Terrain/Cave Chamber")]
    public sealed class LoogaCaveChamber : MonoBehaviour
    {
        // A natural room bulges to this share of its width and depth above the floor.
        private const float NaturalBulge = 1.1f;

        [Header("Shape")]
        [SerializeField] private CavePassageShape _shape = CavePassageShape.Natural;
        [Tooltip("Size along the local X axis of the object, in meters.")]
        [SerializeField, Min(1f)] private float _width = 30f;
        [Tooltip("Size along the local Z axis of the object, in meters.")]
        [SerializeField, Min(1f)] private float _depth = 20f;
        [Tooltip("Height from the floor to the roof in meters.")]
        [SerializeField, Min(1f)] private float _height = 12f;
        [Tooltip("Radius of the rounded edges of a mined room in meters.")]
        [SerializeField, Min(0f)] private float _cornerRadius = 1f;
        [Tooltip("Height of the rock bumps on the walls and the roof of a natural room, in meters.")]
        [SerializeField, Min(0f)] private float _bumpHeight = 1.5f;

        public CavePassageShape Shape => _shape;
        public float Width => _width;
        public float Depth => _depth;
        public float Height => _height;

        #region Built-in
        private void OnEnable()
        {
            RequestBuild();
        }

        private void OnDisable()
        {
            RequestBuild();
        }

        private void OnValidate()
        {
            RequestBuild();
        }
        #endregion

        /// <summary>Sets the room shape and size and builds the cave again.</summary>
        public void SetShape(CavePassageShape shape, float width, float depth, float height, float cornerRadius,
            float bumpHeight)
        {
            _shape = shape;
            _width = Mathf.Max(1f, width);
            _depth = Mathf.Max(1f, depth);
            _height = Mathf.Max(1f, height);
            _cornerRadius = Mathf.Max(0f, cornerRadius);
            _bumpHeight = Mathf.Max(0f, bumpHeight);
            RequestBuild();
        }

        internal void AppendSegment(List<CaveSegment> segments, float margin)
        {
            bool natural = _shape == CavePassageShape.Natural;
            float bumps = natural ? _bumpHeight : 0f;
            float3 floor = transform.position;
            Vector3 right = transform.right;
            float2 axis = math.normalizesafe(new float2(right.x, right.z), new float2(1f, 0f));
            float reach = math.length(new float2(_width, _depth)) * 0.5f * (natural ? NaturalBulge : 1f) + bumps +
                margin;
            segments.Add(new CaveSegment
            {
                Start = floor,
                End = floor + new float3(0f, _height, 0f),
                StartSize = new float2(_width, _depth),
                EndSize = new float2(_height, _height),
                CornerRadius = _cornerRadius,
                Shape = natural ? CaveShape.NaturalRoom : CaveShape.MinedRoom,
                NoiseAmplitude = bumps,
                StartPlane = axis,
                EndPlane = axis,
                BoundsMin = floor - new float3(reach, margin, reach),
                BoundsMax = floor + new float3(reach, _height + bumps + margin, reach)
            });
        }

        private void RequestBuild()
        {
            LoogaCaveSystem system = GetComponentInParent<LoogaCaveSystem>(true);
            if (system) system.QueueBuild();
        }
    }
}
