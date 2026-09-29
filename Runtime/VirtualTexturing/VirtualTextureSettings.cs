using UnityEngine;

namespace LoogaSoft.Terrain
{
    /// <summary>
    /// Configures a shared runtime surface cache. Create another asset for a separate cache.
    /// </summary>
    [CreateAssetMenu(menuName = "Looga/Terrain/Virtual Texture Settings")]
    public sealed class VirtualTextureSettings : ScriptableObject
    {
        [SerializeField] private Shader _feedbackShader;
        [SerializeField] private int _virtualResolution = 16384;
        [SerializeField] private int _residentPages = 512;
        [SerializeField] private bool _preserveCloseDetail = true;
        [SerializeField] private int _pageUpdatesPerFrame = 16;
        [SerializeField, Range(1, 8)] private int _feedbackDownsample = 8;
        [SerializeField, Range(1, 16)] private int _feedbackInterval = 4;
        [SerializeField, Range(15, 600)] private int _stationaryFeedbackInterval = 120;
        [SerializeField, Range(0.05f, 5f)] private float _feedbackRotationDegrees = 0.5f;
        [SerializeField] private bool _blendMipTransitions = true;
        [SerializeField] private bool _highDynamicRangeMaterialCache = true;

        [SerializeField] private ComputeShader _compressionShader;
        [SerializeField] private bool _compressCache;
        [SerializeField] private bool _parallelFeedback = true;
        [SerializeField] private ComputeShader _feedbackCompactionShader;
        /// <summary>Gets the optional GPU feedback compaction shader.</summary>
        public ComputeShader FeedbackCompactionShader => _feedbackCompactionShader;

        /// <summary>Enables GPU feedback compaction. Assign null to use the pixel readback path.</summary>
        public void ConfigureFeedbackCompaction(ComputeShader shader)
        {
            _feedbackCompactionShader = shader;
            Revision++;
        }

        /// <summary>Gets the optional GPU block encoder.</summary>
        public ComputeShader CompressionShader => _compressionShader;
        /// <summary>Requests BC3 color and normal storage while retaining HDR emission.</summary>
        public bool CompressCache => _compressCache;
        /// <summary>Uses worker jobs to decode and count visible feedback.</summary>
        public bool ParallelFeedback => _parallelFeedback;

        /// <summary>Sets cache optimization options before registration.</summary>
        public void ConfigureOptimizations(ComputeShader compression, bool compressCache = true, bool parallelFeedback = true)
        {
            _compressionShader = compression;
            _compressCache = compressCache;
            _parallelFeedback = parallelFeedback;
            Revision++;
        }

        internal int Revision { get; private set; }

        private void OnValidate()
        {
            Revision++;
        }

        /// <summary>Gets the virtual texture resolution per terrain tile.</summary>
        public int VirtualResolution => Mathf.Clamp(Mathf.ClosestPowerOfTwo(_virtualResolution), 512, 32768);
        /// <summary>Uses direct shading when pixels need detail finer than the virtual resolution.</summary>
        public bool PreserveCloseDetail => _preserveCloseDetail;
        /// <summary>Gets the shared physical page capacity.</summary>
        public int ResidentPages => Mathf.Clamp(_residentPages, 16, 2048);
        /// <summary>Gets the maximum page fills per frame.</summary>
        public int PageUpdatesPerFrame => Mathf.Clamp(_pageUpdatesPerFrame, 1, 128);
        /// <summary>Gets the feedback resolution divisor.</summary>
        public int FeedbackDownsample => Mathf.Clamp(_feedbackDownsample, 1, 8);
        /// <summary>Gets the minimum frames between feedback requests.</summary>
        public int FeedbackInterval => Mathf.Clamp(_feedbackInterval, 1, 16);
        /// <summary>Gets the maximum frames between stationary feedback probes.</summary>
        public int StationaryFeedbackInterval => Mathf.Clamp(_stationaryFeedbackInterval, 15, 600);
        /// <summary>Gets the camera rotation that requests new feedback.</summary>
        public float FeedbackRotationDegrees => Mathf.Clamp(_feedbackRotationDegrees, 0.05f, 5f);
        /// <summary>Blends adjacent virtual levels. Disable this option to reduce receiver samples.</summary>
        public bool BlendMipTransitions => _blendMipTransitions;
        /// <summary>Stores emission and metallic data in a high dynamic range texture.</summary>
        public bool HighDynamicRangeMaterialCache => _highDynamicRangeMaterialCache;
        /// <summary>Gets the feedback shader included in the player build.</summary>
        public Shader FeedbackShader => _feedbackShader;
        /// <summary>Gets the texels within a page, excluding its border.</summary>
        public const int PageSize = 128;
        /// <summary>Gets the border width used for filtered page sampling.</summary>
        public const int Border = 4;

        /// <summary>Sets explicit cache settings before registration.</summary>
        public void Configure(Shader feedback, int virtualResolution = 16384, int residentPages = 512,
            int pageUpdatesPerFrame = 16, int feedbackInterval = 4)
        {
            _feedbackShader = feedback;
            _virtualResolution = virtualResolution;
            _residentPages = residentPages;
            _pageUpdatesPerFrame = pageUpdatesPerFrame;
            _feedbackInterval = feedbackInterval;
            Revision++;
        }

        /// <summary>Sets feedback timing for moving and stationary cameras.</summary>
        public void ConfigureFeedbackPolicy(int movingInterval, int stationaryInterval, float rotationDegrees = 0.5f)
        {
            _feedbackInterval = Mathf.Clamp(movingInterval, 1, 16);
            _stationaryFeedbackInterval = Mathf.Clamp(stationaryInterval, 15, 600);
            _feedbackRotationDegrees = Mathf.Clamp(rotationDegrees, 0.05f, 5f);
            Revision++;
        }

        /// <summary>Sets receiver bandwidth and material-cache precision.</summary>
        public void ConfigureBandwidth(bool blendMipTransitions, bool highDynamicRangeMaterialCache)
        {
            _blendMipTransitions = blendMipTransitions;
            _highDynamicRangeMaterialCache = highDynamicRangeMaterialCache;
            Revision++;
        }
    }
}
