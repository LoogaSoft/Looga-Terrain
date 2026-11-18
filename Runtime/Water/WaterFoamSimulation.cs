using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Persistent foam of a water body on the GPU. The foam moves with the flow, collects where the flow converges and
    /// fades over its lifetime. The shore band and fast water keep it up, so foam trails go downstream from banks and
    /// rocks. A body that fits in the window simulates in full. A larger body simulates a window around the camera.
    /// </summary>
    internal sealed class WaterFoamSimulation
    {
        private const string ComputeName = "LoogaWaterFoam";
        private const int MaximumSide = 512;
        // Steps per second and the longest step, in seconds.
        private const float StepInterval = 0.05f;
        private const float MaximumStep = 0.1f;
        // Time in which the foam rises toward a source, in seconds.
        private const float RiseTime = 0.5f;
        // A new or moved window simulates the foam of several lifetimes at once in long steps, so it starts with the
        // trails and plumes that it has after a long time. Fewer lifetimes left a plume that still grew, with a sharp
        // front, after the camera came back.
        private const float WarmUpStep = 0.25f;
        private const float WarmUpLifetimes = 3f;
        private const int MaximumWarmUpSteps = 240;
        // The window of a large body moves only when its focus has moved this share of the window side, so a camera
        // that turns does not move the window all the time.
        private const float RecenterShare = 0.2f;
        // Space around a body that fits in the window, so the shader fade at the window edge is outside the water.
        private const float WindowMargin = 3f;
        // Segments of impact lines that the simulation reads.
        private const int MaximumImpacts = 64;

        private static readonly int PreviousId = Shader.PropertyToID("_Previous");
        private static readonly int ResultId = Shader.PropertyToID("_Result");
        private static readonly int ShoreMapId = Shader.PropertyToID("_ShoreMap");
        private static readonly int FoamDetailMapId = Shader.PropertyToID("_FoamDetailMap");
        private static readonly int WindowId = Shader.PropertyToID("_Window");
        private static readonly int PreviousWindowId = Shader.PropertyToID("_PreviousWindow");
        private static readonly int KeptId = Shader.PropertyToID("_Kept");
        private static readonly int SizeId = Shader.PropertyToID("_Size");
        private static readonly int ShoreMapRectId = Shader.PropertyToID("_ShoreMapRect");
        private static readonly int InflowId = Shader.PropertyToID("_Inflow");
        private static readonly int RegimeMapId = Shader.PropertyToID("_RegimeMap");
        private static readonly int RegimeWhiteWaterId = Shader.PropertyToID("_RegimeWhiteWater");
        private static readonly int InflowRectId = Shader.PropertyToID("_InflowRect");
        private static readonly int DeltaTimeId = Shader.PropertyToID("_DeltaTime");
        private static readonly int SimulationTimeId = Shader.PropertyToID("_SimulationTime");
        private static readonly int DecayId = Shader.PropertyToID("_Decay");
        private static readonly int RiseId = Shader.PropertyToID("_Rise");
        private static readonly int ConvergenceId = Shader.PropertyToID("_Convergence");
        private static readonly int FoamDetailSizeId = Shader.PropertyToID("_FoamDetailSize");
        private static readonly int ShoreFoamWidthId = Shader.PropertyToID("_ShoreFoamWidth");
        private static readonly int ShoreFoamId = Shader.PropertyToID("_ShoreFoam");
        private static readonly int ShoreFoamPulseId = Shader.PropertyToID("_ShoreFoamPulse");
        private static readonly int FastSpeedId = Shader.PropertyToID("_FastSpeed");
        private static readonly int FastFoamId = Shader.PropertyToID("_FastFoam");
        private static readonly int FastFoamStartId = Shader.PropertyToID("_FastFoamStart");
        private static readonly int ImpactsId = Shader.PropertyToID("_Impacts");
        private static readonly int ImpactStrengthsId = Shader.PropertyToID("_ImpactStrengths");
        private static readonly int ImpactCountId = Shader.PropertyToID("_ImpactCount");

        private static ComputeShader _compute;
        private static int _kernel;

        private RenderTexture _current;
        private RenderTexture _next;
        private Vector2 _origin;
        private float _texel;
        private int _width;
        private int _height;
        private bool _hasFoam;
        private ComputeBuffer _impacts;
        private ComputeBuffer _impactStrengths;
        private readonly Vector4[] _impactData = new Vector4[MaximumImpacts];
        private readonly float[] _impactStrengthData = new float[MaximumImpacts];
        private int _impactCount;
        private float _lastStep = float.NegativeInfinity;
        // Focus that the window is placed around.
        private Vector3 _focus;

        public Texture Texture => _hasFoam ? _current : null;

        /// <summary>Maps world XZ to texture UV: uv = (world - xy) * zw. Zero when there is no foam.</summary>
        public Vector4 UVRect => _hasFoam
            ? new Vector4(_origin.x, _origin.y, 1f / (_width * _texel), 1f / (_height * _texel))
            : Vector4.zero;

        /// <summary>
        /// Fade of the window sides in the shader: x = west, y = south, z = east, w = north. 1 = the side cuts
        /// through the water of a large body and fades. 0 = the side lies at the edge of the body, where the water of a
        /// body that continues this one starts, so the foam goes on to the edge.
        /// </summary>
        public Vector4 FadeSides { get; private set; } = Vector4.one;

        /// <summary>
        /// Time since the last step, in seconds. The water shader moves the foam on with the flow over this time, so the
        /// foam moves smoothly between steps.
        /// </summary>
        public float StepAge => _hasFoam ? Mathf.Clamp(Time.realtimeSinceStartup - _lastStep, 0f, MaximumStep) : 0f;

        /// <summary>
        /// Sets the impact lines, where falling water lands and boils up as white water. Each segment has xy = start
        /// and zw = end in world XZ, and a strength from 0 to 1.
        /// </summary>
        public void SetImpacts(IReadOnlyList<Vector4> segments, IReadOnlyList<float> strengths)
        {
            _impactCount = Mathf.Min(segments.Count, MaximumImpacts);
            for (int index = 0; index < _impactCount; index++)
            {
                _impactData[index] = segments[index];
                _impactStrengthData[index] = strengths[index];
            }
        }

        /// <summary>Steps the foam when a step is due. Returns true when the texture or its rectangle changed.</summary>
        /// <param name="focus">World position that a window of a large body follows, for example the camera.</param>
        /// <param name="windowSize">Side of the largest simulated area, in metres.</param>
        /// <param name="texelSize">Smallest texel, in metres.</param>
        /// <param name="lifetime">Time in which foam without a source fades to 37 %, in seconds.</param>
        /// <param name="convergence">Strength of the gathering of foam where the flow converges.</param>
        /// <param name="regimeMap">Regime values (R turbulence, G aeration) in the shore map layout, or null.</param>
        /// <param name="inflow">Foam of the body upstream, or null.</param>
        /// <param name="inflowRect">UV rectangle of the upstream foam, see <see cref="UVRect"/>.</param>
        public bool Update(Rect bodyRect, Vector3 focus, Texture shoreMap, Texture regimeMap, Vector4 shoreRect,
            Material material, float windowSize, float texelSize, float lifetime, float convergence, Texture inflow,
            Vector4 inflowRect)
        {
            float now = Time.realtimeSinceStartup;
            if (_hasFoam && now - _lastStep < StepInterval) return false;
            if (!shoreMap || !material || !Load()) return false;

            var drift = new Vector2(focus.x - _focus.x, focus.z - _focus.z);
            if (!_hasFoam || drift.magnitude > windowSize * RecenterShare) _focus = focus;
            Rect window = PlaceWindow(bodyRect, _focus, windowSize, texelSize, out float texel, out Vector4 fadeSides);
            FadeSides = fadeSides;
            int width = Mathf.Max(1, Mathf.CeilToInt(window.width / texel));
            int height = Mathf.Max(1, Mathf.CeilToInt(window.height / texel));
            bool resized = !_current || _width != width || _height != height || !Mathf.Approximately(_texel, texel);
            if (resized)
            {
                Release();
                _current = CreateTexture(width, height);
                _next = CreateTexture(width, height);
                _width = width;
                _height = height;
                _texel = texel;
            }

            Vector4 previous = _hasFoam && !resized
                ? new Vector4(_origin.x, _origin.y, 1f / (_width * _texel), 1f / (_height * _texel))
                : Vector4.zero;
            // A small move keeps the foam: the step reads it from the previous window, and new texels start from the
            // sources. A jump, for example a camera teleport, starts again.
            Vector2 move = window.position - _origin;
            bool jumped = move.magnitude > 0.5f * Mathf.Max(window.width, window.height);
            _origin = window.position;
            SetInputs(shoreMap, regimeMap, shoreRect, material, convergence);
            _compute.SetTexture(_kernel, InflowId, inflow ? inflow : Texture2D.blackTexture);
            _compute.SetVector(InflowRectId, inflow ? inflowRect : Vector4.zero);
            int warmUpSteps = Mathf.Clamp(Mathf.CeilToInt(WarmUpLifetimes * lifetime / WarmUpStep), 8,
                MaximumWarmUpSteps);
            float elapsed = now - _lastStep;
            if (!_hasFoam || resized || jumped)
            {
                // A new window starts from the sources. The warm-up gives it the foam of several lifetimes.
                WarmUp(warmUpSteps, now, lifetime, ref previous, Vector4.zero);
            }
            else
            {
                // A moved window uncovers water without a foam history. The warm-up gives that water the foam of
                // several lifetimes, while the water of the previous window keeps its foam, so the foam in view does
                // not jump.
                if (move != Vector2.zero)
                {
                    Vector4 kept = previous;
                    WarmUp(warmUpSteps, now - Mathf.Min(elapsed, MaximumStep), lifetime, ref previous, kept);
                }
                Dispatch(Mathf.Min(elapsed, MaximumStep), now, lifetime, previous, Vector4.zero);
            }
            _hasFoam = true;
            _lastStep = now;
            return true;
        }

        public void Release()
        {
            _hasFoam = false;
            _impacts?.Release();
            _impactStrengths?.Release();
            _impacts = null;
            _impactStrengths = null;
            Destroy(_current);
            Destroy(_next);
            _current = null;
            _next = null;
        }

        // A body that fits in the window simulates in full. A larger body simulates a window around the focus, moved
        // in whole texels so the foam does not swim. Its sides at the body edge do not fade (fadeSides 0).
        private static Rect PlaceWindow(Rect bodyRect, Vector3 focus, float windowSize, float texelSize,
            out float texel, out Vector4 fadeSides)
        {
            fadeSides = Vector4.one;
            Rect padded = Rect.MinMaxRect(bodyRect.xMin - WindowMargin, bodyRect.yMin - WindowMargin,
                bodyRect.xMax + WindowMargin, bodyRect.yMax + WindowMargin);
            float side = Mathf.Max(windowSize, 1f);
            if (padded.width <= side && padded.height <= side)
            {
                texel = Mathf.Max(texelSize, Mathf.Max(padded.width, padded.height) / MaximumSide);
                return padded;
            }
            texel = Mathf.Max(texelSize, side / MaximumSide);
            float x = Mathf.Clamp(focus.x - side * 0.5f, padded.xMin, Mathf.Max(padded.xMin, padded.xMax - side));
            float z = Mathf.Clamp(focus.z - side * 0.5f, padded.yMin, Mathf.Max(padded.yMin, padded.yMax - side));
            x = Mathf.Floor(x / texel) * texel;
            z = Mathf.Floor(z / texel) * texel;
            var window = new Rect(x, z, Mathf.Min(side, padded.width), Mathf.Min(side, padded.height));
            fadeSides = new Vector4(window.xMin > padded.xMin + texel ? 1f : 0f,
                window.yMin > padded.yMin + texel ? 1f : 0f, window.xMax < padded.xMax - texel ? 1f : 0f,
                window.yMax < padded.yMax - texel ? 1f : 0f);
            return window;
        }

        private void SetInputs(Texture shoreMap, Texture regimeMap, Vector4 shoreRect, Material material,
            float convergence)
        {
            _compute.SetTexture(_kernel, ShoreMapId, shoreMap);
            _compute.SetTexture(_kernel, RegimeMapId, regimeMap ? regimeMap : Texture2D.blackTexture);
            _compute.SetFloat(RegimeWhiteWaterId, regimeMap ? Read(material, RegimeWhiteWaterId, 0.4f) : 0f);
            Texture detail = material.HasProperty(FoamDetailMapId) ? material.GetTexture(FoamDetailMapId) : null;
            _compute.SetTexture(_kernel, FoamDetailMapId, detail ? detail : Texture2D.grayTexture);
            _compute.SetVector(ShoreMapRectId, shoreRect);
            _compute.SetFloat(ConvergenceId, convergence);
            _compute.SetFloat(FoamDetailSizeId, Read(material, FoamDetailSizeId, 4f));
            _compute.SetFloat(ShoreFoamWidthId, Read(material, ShoreFoamWidthId, 1.5f));
            // With ocean waves the water shader draws the shore foam with the swash of the shore waves. The
            // simulation then only carries the foam of fast water with the current.
            bool ocean = material.IsKeywordEnabled("_OCEAN_WAVES");
            _compute.SetFloat(ShoreFoamId, ocean ? 0f : Read(material, ShoreFoamId, 0.8f));
            _compute.SetFloat(ShoreFoamPulseId, Read(material, ShoreFoamPulseId, 0.3f));
            _compute.SetFloat(FastSpeedId, Read(material, FastSpeedId, 1f));
            _compute.SetFloat(FastFoamId, Read(material, FastFoamId, 0f));
            _compute.SetFloat(FastFoamStartId, Read(material, FastFoamStartId, 0f));
            _impacts ??= new ComputeBuffer(MaximumImpacts, 16);
            _impactStrengths ??= new ComputeBuffer(MaximumImpacts, 4);
            _impacts.SetData(_impactData);
            _impactStrengths.SetData(_impactStrengthData);
            _compute.SetBuffer(_kernel, ImpactsId, _impacts);
            _compute.SetBuffer(_kernel, ImpactStrengthsId, _impactStrengths);
            _compute.SetInt(ImpactCountId, _impactCount);
            _compute.SetInts(SizeId, _width, _height);
            _compute.SetVector(WindowId, new Vector4(_origin.x, _origin.y, _texel, 0f));
        }

        // Long steps that end at a time. The first step reads the foam of the previous window, the others the foam of
        // the current window. Texels in the kept rectangle (same layout as a window, zero = none) keep their foam.
        private void WarmUp(int steps, float endTime, float lifetime, ref Vector4 previousWindow, Vector4 kept)
        {
            for (int step = 0; step < steps; step++)
            {
                Dispatch(WarmUpStep, endTime - (steps - 1 - step) * WarmUpStep, lifetime, previousWindow, kept);
                previousWindow = new Vector4(_origin.x, _origin.y, 1f / (_width * _texel), 1f / (_height * _texel));
            }
        }

        private void Dispatch(float deltaTime, float time, float lifetime, Vector4 previousWindow, Vector4 kept)
        {
            _compute.SetVector(KeptId, kept);
            _compute.SetFloat(DeltaTimeId, deltaTime);
            _compute.SetFloat(SimulationTimeId, time);
            _compute.SetFloat(DecayId, Mathf.Exp(-deltaTime / Mathf.Max(lifetime, 0.1f)));
            _compute.SetFloat(RiseId, 1f - Mathf.Exp(-deltaTime / RiseTime));
            _compute.SetVector(PreviousWindowId, previousWindow);
            _compute.SetTexture(_kernel, PreviousId, _current);
            _compute.SetTexture(_kernel, ResultId, _next);
            _compute.Dispatch(_kernel, (_width + 7) / 8, (_height + 7) / 8, 1);
            (_current, _next) = (_next, _current);
        }

        private static float Read(Material material, int id, float fallback)
        {
            return material.HasProperty(id) ? material.GetFloat(id) : fallback;
        }

        private static bool Load()
        {
            if (_compute) return true;
            if (!SystemInfo.supportsComputeShaders) return false;

            _compute = Resources.Load<ComputeShader>(ComputeName);
            if (!_compute) return false;
            _kernel = _compute.FindKernel("Step");
            return true;
        }

        private static RenderTexture CreateTexture(int width, int height)
        {
            var texture = new RenderTexture(width, height, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear)
            {
                name = "Looga Water Foam", hideFlags = HideFlags.DontSave, enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            texture.Create();
            return texture;
        }

        private static void Destroy(RenderTexture texture)
        {
            if (!texture) return;
            texture.Release();
            if (Application.isPlaying)
            {
                Object.Destroy(texture);
            }
            else
            {
                Object.DestroyImmediate(texture);
            }
        }
    }
}
