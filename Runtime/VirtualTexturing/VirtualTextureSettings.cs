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
        [Tooltip("Physical pages of the shared cache. With Scale Pool With Resolution, the pages at 2560 x 1440.")]
        [SerializeField] private int _residentPages = 512;
        [Tooltip("Scales the pages with the square root of the pixels of the largest camera that renders the cache. " +
                 "Up to the 2048-page array limit; above it the residency mip bias takes over.")]
        [SerializeField] private bool _scalePoolWithResolution;
        [SerializeField] private bool _preserveCloseDetail = true;
        [SerializeField] private int _pageUpdatesPerFrame = 16;
        [SerializeField, Range(1, 8)] private int _feedbackDownsample = 8;
        [SerializeField, Range(1, 16)] private int _feedbackInterval = 4;
        [SerializeField, Range(15, 600)] private int _stationaryFeedbackInterval = 120;
        [SerializeField, Range(0.05f, 5f)] private float _feedbackRotationDegrees = 0.5f;
        [SerializeField] private bool _blendMipTransitions = true;
        [SerializeField] private bool _highDynamicRangeMaterialCache = true;
        [Tooltip("Mip selection for oblique views. 1 = the larger footprint sets the mip. 4 = up to 4 times sharper " +
                 "across the view, read with anisotropic filtering.")]
        [SerializeField, Range(1, 4)] private int _maxAnisotropy = 1;
        [Tooltip("Steeper terrain pixels shade directly. 90 = no limit.")]
        [SerializeField, Range(10f, 90f)] private float _cachedSlopeLimit = 90f;
        [Tooltip("Pixels whose ground footprint is longer than this ratio times its width shade directly. These are " +
                 "far pixels at a very low view angle. 0 = no limit.")]
        [SerializeField, Range(0f, 128f)] private float _cachedFootprintRatio;
        [Tooltip("When the views request more pages than the cache holds, all cache reads move to coarser mips until " +
                 "the pages fit. Off: the cache evicts visible pages every frame.")]
        [SerializeField] private bool _residencyMipBias;
        [Tooltip("A pixel whose page is missing reads a coarser resident page at most this many mips coarser. Beyond " +
                 "that it shades directly, so missing pages do not show as blur. -1 = any coarser page.")]
        [SerializeField, Range(-1, 8)] private int _maxFallbackMips = -1;

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
        public int ResidentPages => Mathf.Clamp(_residentPages, 16, MaxResidentPages);
        /// <summary>Gets the largest page count. A texture array has at most 2048 slices.</summary>
        public const int MaxResidentPages = 2048;
        /// <summary>Scales the pages with the camera resolution.</summary>
        public bool ScalePoolWithResolution => _scalePoolWithResolution;
        /// <summary>Screen pixels of the reference resolution of <see cref="ResidentPages"/>.</summary>
        public const long ReferencePixels = 2560L * 1440;

        /// <summary>Gets the page count for a camera with the given pixels. Steps of 64 pages limit cache rebuilds.</summary>
        public int PagesForPixels(long pixels)
        {
            if (!_scalePoolWithResolution || pixels <= 0) return ResidentPages;
            // The requested pages grow with about the square root of the pixels (KuberaWorld, 720p to 1440p: 2 to
            // 2.9 times the pages for 4 times the pixels), because a smaller view reads coarser mips of the same area.
            double pages = ResidentPages * System.Math.Sqrt((double)pixels / ReferencePixels);
            return Mathf.Clamp((int)System.Math.Ceiling(pages / 64.0) * 64, 64, MaxResidentPages);
        }

        /// <summary>Turns resolution scaling of the pool on or off.</summary>
        public void ConfigurePoolScaling(bool scaleWithResolution)
        {
            _scalePoolWithResolution = scaleWithResolution;
            Revision++;
        }
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
        /// <summary>Gets the maximum anisotropy of the mip selection. 1 = isotropic.</summary>
        public int MaxAnisotropy => Mathf.Clamp(_maxAnisotropy, 1, 4);
        /// <summary>Gets the steepest terrain slope in degrees that reads the cache. Steeper pixels shade directly.</summary>
        public float CachedSlopeLimit => Mathf.Clamp(_cachedSlopeLimit, 10f, 90f);
        /// <summary>Gets the largest footprint ratio of a cached pixel. 0 = no limit.</summary>
        public float CachedFootprintRatio => Mathf.Clamp(_cachedFootprintRatio, 0f, 128f);
        /// <summary>Moves all reads to coarser mips while the pool is oversubscribed.</summary>
        public bool ResidencyMipBias => _residencyMipBias;
        /// <summary>Gets the most mips that a missing page can fall back to a coarser page. -1 = no limit.</summary>
        public int MaxFallbackMips => Mathf.Clamp(_maxFallbackMips, -1, 8);
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

        /// <summary>Sets the page sampling of oblique views and the steep-slope limit of the cache.</summary>
        /// <param name="maxAnisotropy">Maximum anisotropy of the mip selection, 1 to 4. The page reads take up to 4 probes.</param>
        /// <param name="cachedSlopeLimit">Steepest cached terrain slope in degrees. 90 = no limit.</param>
        /// <param name="cachedFootprintRatio">Largest footprint ratio of a cached pixel. 0 = no limit.</param>
        public void ConfigureSampling(int maxAnisotropy, float cachedSlopeLimit = 90f, float cachedFootprintRatio = 0f)
        {
            _maxAnisotropy = Mathf.Clamp(maxAnisotropy, 1, 4);
            _cachedSlopeLimit = Mathf.Clamp(cachedSlopeLimit, 10f, 90f);
            _cachedFootprintRatio = Mathf.Clamp(cachedFootprintRatio, 0f, 128f);
            Revision++;
        }

        /// <summary>Turns the residency mip bias on or off.</summary>
        /// <param name="residencyMipBias">Moves all reads to coarser mips while the pool is oversubscribed.</param>
        /// <param name="maxFallbackMips">Most mips that a missing page falls back to a coarser page. -1 = no limit.</param>
        public void ConfigureResidency(bool residencyMipBias, int maxFallbackMips = -1)
        {
            _residencyMipBias = residencyMipBias;
            _maxFallbackMips = Mathf.Clamp(maxFallbackMips, -1, 8);
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
