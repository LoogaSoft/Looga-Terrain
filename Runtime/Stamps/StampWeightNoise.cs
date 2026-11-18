using System;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    // Values must agree with Shaders/PlacementFilters.hlsl.
    public enum StampNoiseOperation
    {
        Add = 0,
        Subtract = 1,
        Multiply = 2,
        /// <summary>Multiplies by 1 + noise.</summary>
        MultiplyOnePlus = 3,
        Min = 4,
        Max = 5
    }

    /// <summary>
    /// Weight noise of a placement stamp. FBM noise gives (FBM(position * frequency + offset) - balance) * amplitude.
    /// Texture noise gives the texture channel * amplitude + balance, at position * scale + offset.
    /// </summary>
    [Serializable]
    public sealed class StampWeightNoise
    {
        [SerializeField] private StampNoiseType _type = StampNoiseType.None;
        [Tooltip("World: the position is the terrain UV in units of 1000 m. Stamp: the position is the stamp UV.")]
        [SerializeField] private StampNoiseSpace _space = StampNoiseSpace.World;
        [SerializeField] private float _frequency = 10f;
        [SerializeField] private float _amplitude = 1f;
        [SerializeField] private float _offset;
        [SerializeField] private float _balance;

        [Header("Texture")]
        [SerializeField] private Texture2D _texture;
        [Tooltip("x, y scale and z, w offset of the texture UV.")]
        [SerializeField] private Vector4 _textureScaleOffset = new Vector4(1f, 1f, 0f, 0f);
        [SerializeField, Range(0, 3)] private int _textureChannel;

        [Header("Combine")]
        [Tooltip("Second weight noise only: how the noise combines with the first weight noise.")]
        [SerializeField] private StampNoiseOperation _operation = StampNoiseOperation.Add;

        public StampNoiseType Type => _type;
        public StampNoiseSpace Space => _space;
        public float Frequency => _frequency;
        public float Amplitude => _amplitude;
        public float Offset => _offset;
        public float Balance => _balance;
        public Texture2D Texture => _texture;
        public Vector4 TextureScaleOffset => _textureScaleOffset;
        public int TextureChannel => _textureChannel;
        public StampNoiseOperation Operation => _operation;

        /// <summary>Frequency, amplitude, offset and balance as one vector for the shaders.</summary>
        internal Vector4 Parameters => new Vector4(_frequency, _amplitude, _offset, _balance);

        public void Set(StampNoiseType type, StampNoiseSpace space, float frequency, float amplitude, float offset,
            float balance)
        {
            _type = type;
            _space = space;
            _frequency = frequency;
            _amplitude = amplitude;
            _offset = offset;
            _balance = balance;
        }

        public void SetTexture(Texture2D texture, Vector4 scaleOffset, int channel)
        {
            _texture = texture;
            _textureScaleOffset = scaleOffset;
            _textureChannel = Mathf.Clamp(channel, 0, 3);
        }

        public void SetOperation(StampNoiseOperation operation)
        {
            _operation = operation;
        }
    }
}
