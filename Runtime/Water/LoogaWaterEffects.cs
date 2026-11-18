using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Mist and spray of a <see cref="LoogaWaterNetwork"/>. The component places emitters from the solved network:
    /// fog, spray and splash at each fall plunge, mist down each fall face and low spray over rapids and cascades.
    /// A <see cref="LoogaWaterEffectFall"/> adds a fall that the network does not solve.
    /// The particles have no simulation state. The <c>Looga/Water Mist</c> shader calculates each particle from its
    /// emitter, its number and one world clock. Thus a camera move, a reload or a disabled effect cannot restart the
    /// particles, and an effect is at its steady state when it becomes visible.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(LoogaWaterNetwork))]
    [AddComponentMenu("Looga Terrain/Water Effects")]
    public sealed class LoogaWaterEffects : MonoBehaviour
    {
        // The world clock wraps after this time, in seconds. The shader rounds each lifetime so that a whole number
        // of lives fits into it, so the wrap shows no seam. The shader uses the same value.
        private const double ClockPeriod = 1200.0;
        private const string EffectsKeyword = "_WATER_EFFECTS";
        private static readonly int TimeId = Shader.PropertyToID("_LoogaWaterEffectTime");
        private static readonly int EmittersId = Shader.PropertyToID("_WaterEffectEmitters");
        private static readonly int ParticlesId = Shader.PropertyToID("_WaterEffectParticles");

        [Header("Look")]
        [Tooltip("Looga/Water Mist material of all effects. The component draws a copy with the effects keyword on.")]
        [SerializeField] private Material _mistMaterial;

        [Header("Budget")]
        [Tooltip("Largest number of particles of all emitters. The rapids spray gives way first.")]
        [SerializeField, Min(0)] private int _particleBudget = 4096;
        [Tooltip("Camera distance from which an emitter draws fewer particles, in metres. The remaining particles " +
                 "get more opacity, so the effect keeps its density.")]
        [SerializeField, Min(0f)] private float _lodStart = 200f;
        [Tooltip("Share of the particles that an emitter keeps at its fade end.")]
        [SerializeField, Range(0.05f, 1f)] private float _lodKeep = 0.35f;

        [Header("Falls")]
        [Tooltip("Particle rate of the fall effects. 0 = no fall effects.")]
        [SerializeField, Min(0f)] private float _fallScale = 1f;
        [Tooltip("Camera distances over which the fall effects fade out, in metres.")]
        [SerializeField] private Vector2 _fallFade = new(400f, 1200f);

        [Header("Rapids")]
        [Tooltip("Particle rate of the spray over rapids and cascades. 0 = no rapids spray.")]
        [SerializeField, Min(0f)] private float _rapidsScale = 1f;
        [Tooltip("Aeration of a channel point from which it gets spray, from 0 to 1.")]
        [SerializeField, Range(0f, 1f)] private float _rapidsAeration = 0.35f;
        [Tooltip("Distance between the spray emitters along a channel, in metres.")]
        [SerializeField, Min(2f)] private float _rapidsSpacing = 10f;
        [Tooltip("Camera distances over which the rapids spray fades out, in metres.")]
        [SerializeField] private Vector2 _rapidsFade = new(150f, 450f);

        [Header("Editor")]
        [Tooltip("Moves the effects in the Scene view outside play mode. The Scene view then repaints about 20 " +
                 "times a second while an effect is in its fade range.")]
        [SerializeField] private bool _animateInEditMode = true;

        private readonly List<WaterEffectEmitter> _emitters = new();
        private WaterEffectEmitterData[] _emitterData = new WaterEffectEmitterData[0];
        private uint[] _particleData = new uint[0];
        private LoogaWaterNetwork _network;
        private GraphicsBuffer _emitterBuffer;
        private GraphicsBuffer _particleBuffer;
        private MaterialPropertyBlock _properties;
        private Material _material;
        private Material _materialSource;
        private int _materialHash;
        private Bounds _bounds;
        private float _drawDistance;
        private int _plannedSolve = -1;
        private bool _isDirty = true;
        private float _clock;
#if UNITY_EDITOR
        private double _lastRepaint;
#endif

        public Material MistMaterial => _mistMaterial;
        public int ParticleBudget => _particleBudget;
        public float FallScale => _fallScale;
        public float RapidsScale => _rapidsScale;
        public float RapidsAeration => _rapidsAeration;
        public float RapidsSpacing => Mathf.Max(2f, _rapidsSpacing);
        /// <summary>Number of emitters of the last plan.</summary>
        public int EmitterCount => _emitters.Count;
        /// <summary>Number of particles of the last plan, after the budget.</summary>
        public int ParticleCount { get; private set; }
        /// <summary>Number of particles that the last plan asked for, before the budget.</summary>
        public int RequestedParticleCount { get; private set; }

        internal Vector4 FallFade => GetFade(_fallFade);
        internal Vector4 RapidsFade => GetFade(_rapidsFade);

        #region Built-in
        private void OnEnable()
        {
            _network = GetComponent<LoogaWaterNetwork>();
            _plannedSolve = -1;
            _isDirty = true;
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += OnEditorUpdate;
#endif
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= OnEditorUpdate;
#endif
            ReleaseBuffers();
            ReleaseMaterial();
            _emitters.Clear();
            ParticleCount = 0;
        }

        private void OnValidate()
        {
            _isDirty = true;
        }
        #endregion

        #region Public
        /// <summary>Sets the mist material. The effects plan again.</summary>
        public void SetMistMaterial(Material material)
        {
            _mistMaterial = material;
            _isDirty = true;
        }

        /// <summary>Makes the effects plan again before the next draw.</summary>
        internal void MarkDirty()
        {
            _isDirty = true;
        }

        /// <summary>Plans the emitters again from the last network solve.</summary>
        [ContextMenu("Plan Again")]
        public void PlanAgain()
        {
            _isDirty = true;
            PlanIfChanged();
        }
        #endregion

        #region Plan
        private void PlanIfChanged()
        {
            if (!_network) return;
            if (!_isDirty && _plannedSolve == _network.SolveCount) return;

            _isDirty = false;
            _plannedSolve = _network.SolveCount;
            RequestedParticleCount = WaterEffectPlanner.Plan(_network, this, _emitters);
            Upload();
        }

        // Writes the emitters and one entry per particle (emitter index in the high 16 bits, particle number in the
        // emitter in the low 16 bits). The bounds hold every place that a particle can reach.
        private void Upload()
        {
            int count = 0;
            foreach (WaterEffectEmitter emitter in _emitters)
            {
                count += Mathf.Min(emitter.Count, 0xFFFF);
            }
            ParticleCount = count;
            if (count == 0 || _emitters.Count > 0xFFFF)
            {
                ReleaseBuffers();
                ParticleCount = 0;
                return;
            }

            if (_emitterData.Length != _emitters.Count) _emitterData = new WaterEffectEmitterData[_emitters.Count];
            if (_particleData.Length != count) _particleData = new uint[count];
            int particle = 0;
            _drawDistance = 0f;
            for (int index = 0; index < _emitters.Count; index++)
            {
                WaterEffectEmitter emitter = _emitters[index];
                _emitterData[index] = emitter.Data;
                int emitterCount = Mathf.Min(emitter.Count, 0xFFFF);
                for (int local = 0; local < emitterCount; local++)
                {
                    _particleData[particle++] = ((uint)index << 16) | (uint)local;
                }
                Bounds reach = GetReach(emitter.Data);
                if (index == 0)
                {
                    _bounds = reach;
                }
                else
                {
                    _bounds.Encapsulate(reach);
                }
                _drawDistance = Mathf.Max(_drawDistance, emitter.Data.Fade.z);
            }

            if (_emitterBuffer == null || _emitterBuffer.count != _emitterData.Length)
            {
                _emitterBuffer?.Release();
                _emitterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _emitterData.Length,
                    WaterEffectEmitterData.Stride);
            }
            if (_particleBuffer == null || _particleBuffer.count != count)
            {
                _particleBuffer?.Release();
                _particleBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
            }
            _emitterBuffer.SetData(_emitterData);
            _particleBuffer.SetData(_particleData);
        }

        // The space that the particles of one emitter can reach in their longest life.
        private static Bounds GetReach(in WaterEffectEmitterData data)
        {
            float life = data.Life.y;
            float speed = Mathf.Max(((Vector3)data.VelocityMin).magnitude, ((Vector3)data.VelocityMax).magnitude);
            float travel = (speed + 1.6f * data.Shape.z) * life + 0.5f * data.VelocityMax.w * life * life;
            float size = data.Life.w * Mathf.Max(data.Shape.x, data.Shape.y);
            float box = Mathf.Max(data.Across.w, data.Forward.w, data.VelocityMin.w);
            return new Bounds(data.Centre, Vector3.one * (box + 2f * (travel + size)));
        }

        private Vector4 GetFade(Vector2 fade)
        {
            float start = Mathf.Max(0f, fade.x);
            float end = Mathf.Max(start + 1f, fade.y);
            return new Vector4(Mathf.Min(_lodStart, start), start, end, _lodKeep);
        }
        #endregion

        #region Draw
        // One clock for all cameras, so the Scene view and the Game view show the same particles.
        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            bool animate = Application.isPlaying || _animateInEditMode;
            if (animate)
            {
                double now = Application.isPlaying ? Time.timeAsDouble : Time.realtimeSinceStartupAsDouble;
                _clock = (float)(now % ClockPeriod);
            }
            Shader.SetGlobalFloat(TimeId, _clock);
            PlanIfChanged();
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (ParticleCount == 0 || camera.cameraType == CameraType.Preview) return;
            if (_bounds.SqrDistance(camera.transform.position) > _drawDistance * _drawDistance) return;

            Material material = GetMaterial();
            if (!material) return;

            _properties ??= new MaterialPropertyBlock();
            _properties.SetBuffer(EmittersId, _emitterBuffer);
            _properties.SetBuffer(ParticlesId, _particleBuffer);
            var parameters = new RenderParams(material)
            {
                camera = camera,
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = true,
                matProps = _properties,
                worldBounds = _bounds
            };
            Graphics.RenderPrimitives(parameters, MeshTopology.Triangles, ParticleCount * 6);
        }

        // A copy of the mist material with the effects keyword on. It follows edits of the source material.
        private Material GetMaterial()
        {
            if (!_mistMaterial)
            {
                ReleaseMaterial();
                return null;
            }
            if (_material && _materialSource == _mistMaterial && (Application.isPlaying ||
                _mistMaterial.ComputeCRC() == _materialHash)) return _material;

            if (!_material || _material.shader != _mistMaterial.shader)
            {
                ReleaseMaterial();
                _material = new Material(_mistMaterial)
                {
                    name = _mistMaterial.name + " (effects)",
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
            else
            {
                _material.CopyPropertiesFromMaterial(_mistMaterial);
            }
            _materialSource = _mistMaterial;
            _materialHash = _mistMaterial.ComputeCRC();
            _material.EnableKeyword(new LocalKeyword(_material.shader, EffectsKeyword));
            return _material;
        }

        private void ReleaseMaterial()
        {
            if (_material)
            {
                if (Application.isPlaying)
                {
                    Destroy(_material);
                }
                else
                {
                    DestroyImmediate(_material);
                }
            }
            _material = null;
            _materialSource = null;
        }

        private void ReleaseBuffers()
        {
            _emitterBuffer?.Release();
            _emitterBuffer = null;
            _particleBuffer?.Release();
            _particleBuffer = null;
        }
        #endregion

#if UNITY_EDITOR
        #region Editor
        // The Scene view draws only when something changes. The effects move, so the Scene view repaints while an
        // effect is in its fade range.
        private void OnEditorUpdate()
        {
            if (Application.isPlaying || !_animateInEditMode || ParticleCount == 0) return;

            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (now - _lastRepaint < 0.05) return;

            UnityEditor.SceneView view = UnityEditor.SceneView.lastActiveSceneView;
            if (!view || !view.camera) return;
            if (_bounds.SqrDistance(view.camera.transform.position) > _drawDistance * _drawDistance) return;

            _lastRepaint = now;
            view.Repaint();
        }
        #endregion
#endif
    }
}
