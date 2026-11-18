using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Raises the ground at the toe of steep faces, as scree and soil collect below a cliff. Gentle ground next to the
    /// foot of a face rises by the apron height and falls back to the terrain over the apron reach. The base of the face
    /// fills from the raised toe at the talus angle. The rim of a face gets no apron. The transform sets a box like a
    /// <see cref="LoogaHeightStamp"/>. The falloff, the strength and up to two water filters set the weight. Like every
    /// <see cref="LoogaNeighbourhoodStamp"/>, it splits the stamp order.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Talus Stamp")]
    public sealed class LoogaTalusStamp : LoogaNeighbourhoodStamp
    {
        [Header("Talus")]
        [Tooltip("Optional rock face. Its edge apron sets the height, reach, fill angle and height variation, and an " +
                 "apron that is off raises nothing.")]
        [SerializeField] private LoogaSoft.Terrain.Geology.LoogaRockFace _rockFace;
        [System.NonSerialized] private LoogaSoft.Terrain.Geology.LoogaRockFace _subscribedFace;
        [Tooltip("Height of the apron at the toe of a face, in metres.")]
        [SerializeField, Min(0f)] private float _apronHeight = 1.5f;
        [Tooltip("Distance from the toe at which the apron meets the terrain again, in metres.")]
        [SerializeField, Range(1f, 30f)] private float _apronReach = 8f;
        [Tooltip("Slopes steeper than this angle, in degrees, are faces. Their toe gets an apron.")]
        [SerializeField, Range(20f, 80f)] private float _faceAngle = 50f;
        [Tooltip("A face must rise at least this height above its toe, in metres. Lower cuts, such as road cuts and " +
                 "river banks, get no apron.")]
        [SerializeField, Min(0f)] private float _minimumFaceHeight = 8f;
        [Tooltip("Slope in degrees at which the raised toe fills the base of the face.")]
        [SerializeField, Range(15f, 60f)] private float _talusAngle = 35f;
        [Tooltip("Shape of the apron. 1 = straight slope, larger values = a fuller toe that flattens out.")]
        [SerializeField, Range(0.5f, 4f)] private float _profilePower = 1.5f;
        [Tooltip("Change of the apron height along the toe. 0 = constant, 1 = from 0 to twice the apron height.")]
        [SerializeField, Range(0f, 1f)] private float _heightVariation = 0.5f;
        [Tooltip("Typical size of the apron height change, in metres.")]
        [SerializeField, Min(1f)] private float _variationSize = 20f;
        [SerializeField, Range(0f, 1f)] private float _strength = 1f;
        [Tooltip("Noise added to the raised ground, in metres.")]
        [SerializeField] private StampNoiseSettings _heightNoise = new StampNoiseSettings();

        [Header("Falloff")]
        [SerializeField] private StampFalloff _falloff = new StampFalloff();

        [Header("Water")]
        [Tooltip("Multiplies the weight, for example to keep the apron out of rivers.")]
        [SerializeField] private StampWaterFilter _waterFilter = new StampWaterFilter();
        [SerializeField] private StampWaterFilter _secondWaterFilter = new StampWaterFilter();

        /// <summary>The rock face whose edge apron sets the talus, or null.</summary>
        public LoogaSoft.Terrain.Geology.LoogaRockFace RockFace => _rockFace;
        public float ApronHeight => _rockFace ? (_rockFace.Apron.Enabled ? _rockFace.Apron.Height : 0f) : _apronHeight;
        public float ApronReach => _rockFace ? _rockFace.Apron.Reach : _apronReach;
        public float FaceAngle => _faceAngle;
        public float MinimumFaceHeight => _minimumFaceHeight;
        public float TalusAngle => _rockFace ? _rockFace.Apron.FillAngle : _talusAngle;
        public float ProfilePower => _profilePower;
        public float HeightVariation => _rockFace ? _rockFace.Apron.HeightVariation : _heightVariation;
        public float VariationSize => _variationSize;
        public float Strength => _strength;
        public StampNoiseSettings HeightNoise => _heightNoise;
        public StampFalloff Falloff => _falloff;
        public StampWaterFilter WaterFilter => _waterFilter;
        public StampWaterFilter SecondWaterFilter => _secondWaterFilter;

        /// <summary>Horizontal distance in metres that the raised toe fills into the face.</summary>
        public float FillReach => ApronHeight * (1f + HeightVariation) /
                                  Mathf.Tan(Mathf.Clamp(TalusAngle, 15f, 60f) * Mathf.Deg2Rad);

        /// <summary>The apron reads the toe within the apron reach and the face within the fill reach.</summary>
        public override float Reach => Mathf.Max(ApronReach + FillReach,
            _minimumFaceHeight / Mathf.Tan(Mathf.Clamp(_faceAngle, 20f, 80f) * Mathf.Deg2Rad) + 1f) + 2f;

        /// <summary>Links the talus to a rock face, whose edge apron then sets it. Null uses the own values.</summary>
        public void SetRockFace(LoogaSoft.Terrain.Geology.LoogaRockFace rockFace)
        {
            if (_rockFace == rockFace) return;
            _rockFace = rockFace;
            Subscribe();
            MarkChanged();
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            if (_subscribedFace) _subscribedFace.ApronChanged -= MarkChanged;
            _subscribedFace = null;
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            if (isActiveAndEnabled) Subscribe();
        }

        // Follows the apron changes of the linked face, also after the link changes in the inspector.
        private void Subscribe()
        {
            if (_subscribedFace == _rockFace) return;
            if (_subscribedFace) _subscribedFace.ApronChanged -= MarkChanged;
            _subscribedFace = _rockFace;
            if (_subscribedFace) _subscribedFace.ApronChanged += MarkChanged;
        }

        public void SetTalus(float apronHeight, float apronReach, float faceAngle, float talusAngle)
        {
            _apronHeight = Mathf.Max(0f, apronHeight);
            _apronReach = Mathf.Clamp(apronReach, 1f, 30f);
            _faceAngle = Mathf.Clamp(faceAngle, 20f, 80f);
            _talusAngle = Mathf.Clamp(talusAngle, 15f, 60f);
            MarkChanged();
        }

        protected override Rect GetArea()
        {
            return GetCullRect(HeightMapping.WorldAligned);
        }

        protected override void Apply(NeighbourhoodTile tile)
        {
            tile.ApplyTalus(this);
        }

        internal override Rect GetCullRect(HeightMapping mapping)
        {
            Rect rect = StampMapping.CullRect(transform, mapping);
            if (_falloff.TryGetAreaRect(out Rect area))
            {
                rect = StampMapping.Intersect(rect, area);
            }
            return _secondWaterFilter.Limit(_waterFilter.Limit(rect));
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.85f, 0.7f, 0.45f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f),
                Vector3.one);
            Vector3 scale = transform.lossyScale;
            Gizmos.DrawWireCube(new Vector3(0f, scale.y * 0.5f, 0f), scale);
        }
    }
}
