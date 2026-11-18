using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// A vertical round shaft of a <see cref="LoogaCaveSystem"/>, for example a sinkhole above a chamber. The shaft
    /// starts at the object position and goes up by its height. A shaft that reaches above the terrain opens the cave
    /// there, and the cave system cuts the terrain at the opening.
    /// </summary>
    [AddComponentMenu("Looga Terrain/Cave Shaft")]
    public sealed class LoogaCaveShaft : MonoBehaviour
    {
        [Header("Shape")]
        [Tooltip("Height from the object position to the shaft top, in meters. Reach above the terrain to open the " +
                 "shaft.")]
        [SerializeField, Min(1f)] private float _height = 40f;
        [Tooltip("Diameter at the bottom in meters.")]
        [SerializeField, Min(0.5f)] private float _bottomWidth = 14f;
        [Tooltip("Diameter at the top in meters.")]
        [SerializeField, Min(0.5f)] private float _topWidth = 10f;
        [Tooltip("Height of the rock bumps on the shaft walls in meters.")]
        [SerializeField, Min(0f)] private float _bumpHeight = 1.5f;

        public float Height => _height;
        public float BottomWidth => _bottomWidth;
        public float TopWidth => _topWidth;
        public float BumpHeight => _bumpHeight;

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

        /// <summary>Sets the shaft size and builds the cave again.</summary>
        public void SetSize(float height, float bottomWidth, float topWidth)
        {
            _height = Mathf.Max(1f, height);
            _bottomWidth = Mathf.Max(0.5f, bottomWidth);
            _topWidth = Mathf.Max(0.5f, topWidth);
            RequestBuild();
        }

        internal void AppendSegment(List<CaveSegment> segments, float margin)
        {
            float3 bottom = transform.position;
            float3 top = bottom + new float3(0f, _height, 0f);
            float reach = math.max(_bottomWidth, _topWidth) * 0.5f + _bumpHeight + margin;
            segments.Add(new CaveSegment
            {
                Start = bottom,
                End = top,
                StartSize = new float2(_bottomWidth, _height),
                EndSize = new float2(_topWidth, _height),
                Shape = CaveShape.Shaft,
                NoiseAmplitude = _bumpHeight,
                StartPlane = new float2(0f, 1f),
                EndPlane = new float2(0f, 1f),
                BoundsMin = bottom - new float3(reach, margin, reach),
                BoundsMax = top + new float3(reach, margin, reach)
            });
        }

        private void RequestBuild()
        {
            LoogaCaveSystem system = GetComponentInParent<LoogaCaveSystem>(true);
            if (system) system.QueueBuild();
        }
    }
}
