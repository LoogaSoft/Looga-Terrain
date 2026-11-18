using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// The falling water at the end of a river that drops into lower water, for example over a cliff or a spillway. A
    /// <see cref="LoogaWaterNetwork"/> finds each such fall and generates its sheet. Add this component to shape one
    /// fall: a structure face, piers, a design flow, materials or mesh settings. With the Body Crest source, the
    /// component marks a lip line where an area body spills over its edge into lower water.
    /// </summary>
    /// <remarks>
    /// The lip lies along the local X axis at z = 0, centred at x = 0, and the water flows toward local +Z. The network
    /// solves the fall and sets the mesh and the renderer materials. The object scale is the fall unit: a smaller scale
    /// makes the streak, foam and lump patterns larger relative to the fall.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Looga Terrain/Water Fall")]
    public sealed class LoogaWaterFall : MonoBehaviour
    {
        /// <summary>What falls.</summary>
        public enum FallSource
        {
            // The end of a river drops into lower water.
            RiverEnd = 0,
            // An area body spills over a crest line into lower water.
            BodyCrest = 1
        }

        private static readonly List<LoogaWaterFall> ActiveFalls = new();
        private static readonly int DepthOffsetId = Shader.PropertyToID("_DepthOffset");
        private static readonly int ZWriteId = Shader.PropertyToID("_WaterZWrite");
        private static readonly int ColorMaskId = Shader.PropertyToID("_WaterColorMask");
        private static readonly int DepthEndId = Shader.PropertyToID("_FallDepthEnd");
        private static readonly int DepthPushBackId = Shader.PropertyToID("_FallDepthPushBack");
        private static readonly int ClockId = Shader.PropertyToID("_LoogaWaterClock");
        // The water clock goes to the shader as whole periods of this length and the seconds in the period.
        private const double ClockPeriod = 1024.0;
        private const string FallKeyword = "_WATER_FALL";
        private const string FallDepthKeyword = "_WATER_FALL_DEPTH";
        private const string OceanKeyword = "_OCEAN_WAVES";
        // The fall values of a fall material: every shader property with this prefix.
        private const string FallPropertyPrefix = "_Fall";
        private const string ForwardPass = "UniversalForward";
        private const string SoftDepthPass = "LoogaSoftDepth";
        // The water surface depth for the transparent objects after the water. Only the sheet copy draws it.
        private const string SurfaceDepthPass = "LoogaWaterDepth";
        // The soft depth draws after the transparent water and before the mist.
        private const int SoftDepthQueue = 3040;
        // Within this distance before the weld line the near ocean grid reaches the line (WeldGridReach in
        // LoogaWaterSurface.hlsl), so the weld field covers it, in metres.
        private const float WeldGridReach = 24f;
        // Extra margin of the weld field around the calm and grid distances, in metres.
        private const float WeldMargin = 4f;

        [Header("Joint")]
        [Tooltip("River End: the end of a river falls here. Body Crest: an area body spills over the lip line.")]
        [SerializeField] private FallSource _source;
        [Tooltip("River End: river whose end falls here. This fall replaces the generated fall of that river end.")]
        [SerializeField] private LoogaWaterBody _channel;
        [Tooltip("River End: spline of the river container whose end falls here.")]
        [SerializeField, Min(0)] private int _splineIndex;
        [Tooltip("Body Crest: area body whose water spills over the lip.")]
        [SerializeField] private LoogaWaterBody _body;
        [Tooltip("Body Crest: lip line along the body outline (first spline). The water falls to the side away " +
                 "from the body.")]
        [SerializeField] private SplineContainer _lip;

        [Header("Structure")]
        [Tooltip("Face under the falling water in local space: x = distance downstream of the lip, y = height. " +
                 "Empty = the water falls freely from the lip.")]
        [SerializeField] private List<Vector2> _profile = new();
        [Tooltip("Lip width in local units. 0 = the river width at its end.")]
        [SerializeField, Min(0f)] private float _width;
        [Tooltip("Number of jets across the lip. Piers separate the jets.")]
        [SerializeField, Min(1)] private int _bays = 1;
        [Tooltip("Width of each pier in local units. Piers also stand at both lip ends.")]
        [SerializeField, Min(0f)] private float _pierWidth;
        [Tooltip("The jets rub walls at their sides, which makes white water along the walls.")]
        [SerializeField] private bool _walls;
        [Tooltip("Flow per metre of lip width, in square metres per second. 0 = the solved river discharge. A " +
                 "structure can set its design flow, which sets the sheet thickness.")]
        [SerializeField, Min(0f)] private float _unitDischarge;

        [Header("Materials")]
        [Tooltip("Looga/Water material with Fall Sheet on. The sheet draws with a copy of the material of the water " +
                 "that feeds the fall, with the fall values of this material, so the water and the fall look like " +
                 "one surface.")]
        [SerializeField] private Material _fallMaterial;
        [Tooltip("Looga/Water material with Fall Sheet on whose fall values draw the soft depth of the jets, so mist " +
                 "fades in front of them. Empty = the fall material.")]
        [SerializeField] private Material _depthMaterial;

        [Header("Sheet")]
        [SerializeField] private WaterFallSettings _settings = new();

        // A fall that the network made. It is not saved and it does not show in the hierarchy.
        [SerializeField, HideInInspector] private bool _generated;
        [SerializeField, HideInInspector] private LoogaWaterNetwork _owner;

        private readonly DerivedMaterial _derivedSheet = new();
        private readonly DerivedMaterial _derivedPrepass = new();
        private readonly DerivedMaterial _derivedDepth = new();
        private readonly Material[] _materials = new Material[3];
        private readonly WaterWeldField _weld = new();
        private readonly List<Vector2> _weldLine = new();
        private readonly List<Vector2> _weldNormals = new();
        private MaterialPropertyBlock _bodyProperties;
        private LoogaWaterBody _weldedBody;
        private Mesh _mesh;
        private Hash128 _shownHash;
        private bool _isShown;

        public FallSource Source => _source;
        public LoogaWaterBody Channel => _channel;
        public int SplineIndex => _splineIndex;
        public LoogaWaterBody Body => _body;
        public SplineContainer Lip => _lip;
        public IReadOnlyList<Vector2> Profile => _profile;
        public float Width => _width;
        public int Bays => Mathf.Max(1, _bays);
        public float PierWidth => _pierWidth;
        public bool Walls => _walls;
        public float UnitDischarge => _unitDischarge;
        public Material FallMaterial => _fallMaterial;
        public WaterFallSettings Settings => _settings;
        /// <summary>The water body that feeds the fall: the river of a river end, the body of a crest.</summary>
        public LoogaWaterBody Upstream => _source == FallSource.BodyCrest ? _body : _channel;
        /// <summary>True for a fall that a network made. It is not saved.</summary>
        public bool IsGenerated => _generated;
        /// <summary>True while the fall shows a sheet.</summary>
        public bool IsShown => _isShown;
        /// <summary>Distance from the lip to the plunge along the flow, in local units. 0 before a solve.</summary>
        public float PlungeDistance { get; private set; }

        /// <summary>Enabled falls, in no fixed order.</summary>
        internal static IReadOnlyList<LoogaWaterFall> Active => ActiveFalls;
        internal LoogaWaterNetwork Owner => _owner;

        #region Built-in
        private void OnEnable()
        {
            ActiveFalls.Add(this);
            UnityEngine.Splines.Spline.Changed += OnSplineChanged;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
#if UNITY_EDITOR
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
#endif
            QueueNetworkSolve();
        }

        private void OnDisable()
        {
            ActiveFalls.Remove(this);
            UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
#if UNITY_EDITOR
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
#endif
            Hide();
            _weld.Release();
            _derivedSheet.Release();
            _derivedPrepass.Release();
            _derivedDepth.Release();
            if (!_generated) QueueNetworkSolve();
        }

        private void OnValidate()
        {
            _shownHash = default;
            if (isActiveAndEnabled) QueueNetworkSolve();
        }

        // A moved fall object moves the lip and the plunge.
        private void Update()
        {
            if (_generated || !transform.hasChanged) return;

            transform.hasChanged = false;
            QueueNetworkSolve();
        }

        // An edit of the lip line moves the crest.
        private void OnSplineChanged(UnityEngine.Splines.Spline spline, int knotIndex, SplineModification modification)
        {
            if (!_lip) return;

            foreach (UnityEngine.Splines.Spline own in _lip.Splines)
            {
                if (own != spline) continue;

                QueueNetworkSolve();
                return;
            }
        }
        #endregion

        #region Setup
        /// <summary>Sets the river end that falls here.</summary>
        public void SetChannel(LoogaWaterBody channel, int splineIndex)
        {
            _source = FallSource.RiverEnd;
            _channel = channel;
            _splineIndex = Mathf.Max(0, splineIndex);
            OnValidate();
        }

        /// <summary>
        /// Sets an area body that spills over a lip line. A unit discharge of 0 spreads the inflow of the body over
        /// the lip length.
        /// </summary>
        public void SetCrest(LoogaWaterBody body, SplineContainer lip, float unitDischarge)
        {
            _source = FallSource.BodyCrest;
            _body = body;
            _lip = lip;
            _unitDischarge = Mathf.Max(0f, unitDischarge);
            OnValidate();
        }

        /// <summary>
        /// Sets the structure under the falling water. The profile and the widths are in local units. A unit
        /// discharge of 0 uses the solved river discharge.
        /// </summary>
        public void SetStructure(IReadOnlyList<Vector2> profile, float width, int bays, float pierWidth, bool walls,
            float unitDischarge)
        {
            _profile = profile != null ? new List<Vector2>(profile) : new List<Vector2>();
            _width = Mathf.Max(0f, width);
            _bays = Mathf.Max(1, bays);
            _pierWidth = Mathf.Max(0f, pierWidth);
            _walls = walls;
            _unitDischarge = Mathf.Max(0f, unitDischarge);
            OnValidate();
        }

        /// <summary>Sets the fall materials. An empty depth material uses the fall values of the fall material.</summary>
        public void SetMaterials(Material fallMaterial, Material depthMaterial)
        {
            _fallMaterial = fallMaterial;
            _depthMaterial = depthMaterial;
            OnValidate();
        }

        internal static LoogaWaterFall CreateGenerated(LoogaWaterNetwork owner)
        {
            var gameObject = new GameObject("Looga Water Fall (generated)") { hideFlags = HideFlags.HideAndDontSave };
            gameObject.SetActive(false);
            var fall = gameObject.AddComponent<LoogaWaterFall>();
            fall._generated = true;
            fall._owner = owner;
            gameObject.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            gameObject.SetActive(true);
            return fall;
        }

        // A generated fall takes the river end, the material and the settings of its network.
        internal void Configure(LoogaWaterBody channel, int splineIndex, Material fallMaterial,
            WaterFallSettings settings)
        {
            _channel = channel;
            _splineIndex = splineIndex;
            _fallMaterial = fallMaterial;
            _settings.CopyFrom(settings);
            if (channel) gameObject.layer = channel.gameObject.layer;
        }

        internal void DestroyGenerated()
        {
            if (!_generated) return;

            if (Application.isPlaying)
            {
                Destroy(gameObject);
            }
            else
            {
                DestroyImmediate(gameObject);
            }
        }

        internal void AppendTo(ref Hash128 hash)
        {
            hash.Append(GetInstanceID());
            hash.Append((int)_source);
            hash.Append(_channel ? _channel.GetInstanceID() : 0);
            hash.Append(_splineIndex);
            hash.Append(_body ? _body.GetInstanceID() : 0);
            if (_lip)
            {
                Matrix4x4 lipMatrix = _lip.transform.localToWorldMatrix;
                hash.Append(ref lipMatrix);
            }
            WaterSplines.AppendKnots(ref hash, _lip);
            Matrix4x4 matrix = transform.localToWorldMatrix;
            hash.Append(ref matrix);
            hash.Append(_profile.Count);
            foreach (Vector2 point in _profile)
            {
                Vector2 value = point;
                hash.Append(ref value);
            }
            hash.Append(_width);
            hash.Append(_bays);
            hash.Append(_pierWidth);
            hash.Append(_walls ? 1 : 0);
            hash.Append(_unitDischarge);
            _settings.AppendTo(ref hash);
        }

        private void QueueNetworkSolve()
        {
            LoogaWaterBody member = Upstream;
            LoogaWaterNetwork network = _owner ? _owner : member ? LoogaWaterNetwork.Find(member) : null;
            if (network) network.QueueSolve();
        }
        #endregion

        #region Sheet
        /// <summary>Builds the sheet of a solved fall when its inputs changed. Sets the renderer materials.</summary>
        internal void Show(WaterNetworkFall fall, WaterFallSettings settings)
        {
            if (_generated)
            {
                Matrix4x4 frame = fall.Edge.FrameToWorld;
                transform.SetPositionAndRotation(frame.GetPosition(), frame.rotation);
                transform.localScale = Vector3.one;
            }
            if (!_fallMaterial)
            {
                Hide();
                return;
            }

            ApplyMaterials();
            Hash128 hash = fall.Hash;
            if (_isShown && _mesh && hash == _shownHash) return;

            _shownHash = hash;
            PlungeDistance = fall.PathList[fall.PlungeIndex].Surface.x;
            if (!_mesh) _mesh = new Mesh { name = "Looga Water Fall", hideFlags = HideFlags.DontSave };
            WaterSurfaceSampler sampler = PrepareSampler(fall.Edge.FrameToWorld, fall.Edge.Width, settings);
            WaterFallSheet.BuildMesh(_mesh, fall.Edge, fall.PathList, fall.PlungeIndex, settings, sampler, _weldLine,
                _weldNormals);
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            _isShown = true;
            ApplyWeld(settings.SheetStart * fall.Edge.Scale, settings);
        }

        /// <summary>Builds the sheet of a solved crest when its inputs changed. Sets the renderer materials.</summary>
        internal void ShowCrest(WaterNetworkCrest crest)
        {
            if (!_fallMaterial || crest.PlungeIndex < 0)
            {
                Hide();
                return;
            }

            ApplyMaterials();
            Hash128 hash = crest.Hash;
            Matrix4x4 worldToLocal = transform.worldToLocalMatrix;
            hash.Append(ref worldToLocal);
            if (_isShown && _mesh && hash == _shownHash) return;

            _shownHash = hash;
            PlungeDistance = crest.PlungeDistance;
            if (!_mesh) _mesh = new Mesh { name = "Looga Water Fall", hideFlags = HideFlags.DontSave };
            WaterSurfaceSampler sampler = PrepareSampler(crest.Lip, _settings);
            WaterFallSheet.BuildCrestMesh(_mesh, crest.Edge, crest.PathList, crest.PlungeIndex, _settings, crest.Lip,
                crest.Normals, worldToLocal, sampler, _weldLine, _weldNormals);
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            _isShown = true;
            ApplyWeld(_settings.SheetStart * crest.Edge.Scale, _settings);
        }

        /// <summary>Removes the sheet. The network calls it when the fall has no free-fall joint.</summary>
        internal void Hide()
        {
            _isShown = false;
            _shownHash = default;
            if (_weldedBody) _weldedBody.ClearWeld(this);
            _weldedBody = null;
            if (TryGetComponent(out MeshFilter meshFilter) && meshFilter.sharedMesh == _mesh)
            {
                meshFilter.sharedMesh = null;
            }
            if (!_mesh) return;

            if (Application.isPlaying)
            {
                Destroy(_mesh);
            }
            else
            {
                DestroyImmediate(_mesh);
            }
            _mesh = null;
        }

        // The sheet starts on the weld line. The feeding water ends exactly there and calms toward it. The sheet start
        // is in metres.
        private void ApplyWeld(float sheetStart, WaterFallSettings settings)
        {
            LoogaWaterBody upstream = Upstream;
            if (_weldedBody && _weldedBody != upstream) _weldedBody.ClearWeld(this);
            _weldedBody = upstream;
            if (!upstream) return;

            _weld.Build(_weldLine, _weldNormals, sheetStart + settings.WeldCalm + WeldGridReach + WeldMargin);
            upstream.SetWeld(this, _weld.Texture, _weld.UVRect,
                new Vector4(sheetStart, settings.WeldCalm, settings.LipRipples, settings.LipRush));
        }

        // Reads the vertex data of the feeding water on the weld line of a fall in its frame.
        private WaterSurfaceSampler PrepareSampler(Matrix4x4 frameToWorld, float width, WaterFallSettings settings)
        {
            Vector3 left = frameToWorld.MultiplyPoint3x4(new Vector3(-0.5f * width, 0f, -settings.SheetStart));
            Vector3 right = frameToWorld.MultiplyPoint3x4(new Vector3(0.5f * width, 0f, -settings.SheetStart));
            return PrepareSampler(new[] { left, right }, settings);
        }

        // Reads the vertex data of the feeding water around the points. The near ocean grid reads its flow from the
        // shore map in the shader, and the sheet does the same there, so an ocean body needs no mesh samples.
        private WaterSurfaceSampler PrepareSampler(IReadOnlyList<Vector3> points, WaterFallSettings settings)
        {
            LoogaWaterBody upstream = Upstream;
            if (!upstream || !upstream.TryGetComponent(out MeshRenderer bodyRenderer) ||
                !upstream.TryGetComponent(out MeshFilter bodyFilter) || points.Count == 0) return null;
            Material material = bodyRenderer.sharedMaterial;
            if (material && material.IsKeywordEnabled(OceanKeyword)) return null;

            float margin = settings.SheetStart * Mathf.Abs(transform.lossyScale.x) + 2f;
            var min = new Vector2(points[0].x, points[0].z);
            Vector2 max = min;
            foreach (Vector3 point in points)
            {
                min = Vector2.Min(min, new Vector2(point.x, point.z));
                max = Vector2.Max(max, new Vector2(point.x, point.z));
            }
            Rect area = Rect.MinMaxRect(min.x - margin, min.y - margin, max.x + margin, max.y + margin);
            var sampler = new WaterSurfaceSampler();
            return sampler.Prepare(bodyFilter, area) ? sampler : null;
        }

        // Material 0 writes the depth of the solid parts of the jets, material 1 draws the sheet, material 2 draws the
        // soft depth for the mist. All three draw the one submesh of the sheet. Each is a copy of the material of the
        // feeding water with the fall values of the fall material, so the sheet has the same light as that water.
        private void ApplyMaterials()
        {
            LoogaWaterBody upstream = Upstream;
            Material body = upstream && upstream.TryGetComponent(out MeshRenderer upstreamRenderer)
                ? upstreamRenderer.sharedMaterial
                : null;
            Material depthSource = _depthMaterial ? _depthMaterial : _fallMaterial;
            _materials[0] = _derivedPrepass.Get(body, _fallMaterial, SetUpPrepass);
            _materials[1] = _derivedSheet.Get(body, _fallMaterial, SetUpSheet);
            _materials[2] = _derivedDepth.Get(body, depthSource, SetUpSoftDepth);
            var meshRenderer = GetComponent<MeshRenderer>();
            Material[] current = meshRenderer.sharedMaterials;
            bool same = current.Length == _materials.Length;
            for (int index = 0; same && index < current.Length; index++)
            {
                same = current[index] == _materials[index];
            }
            if (!same) meshRenderer.sharedMaterials = _materials;
        }

        // The sheet draws after the water bodies, without depth writes, in front of the depth of its own prepass.
        private static void SetUpSheet(Material copy, Material fall)
        {
            copy.renderQueue = fall.renderQueue;
            copy.SetFloat(ZWriteId, 0f);
            copy.SetFloat(ColorMaskId, 15f);
            copy.SetFloat(DepthOffsetId, -1f);
            copy.DisableKeyword(FallDepthKeyword);
            copy.SetShaderPassEnabled(ForwardPass, true);
            copy.SetShaderPassEnabled(SoftDepthPass, false);
            copy.SetShaderPassEnabled(SurfaceDepthPass, true);
        }

        // Before the sheet draws, its solid parts write their depth, so the lower jets do not draw over the near jets
        // and effects behind the jets stay hidden.
        private static void SetUpPrepass(Material copy, Material fall)
        {
            copy.renderQueue = fall.renderQueue - 1;
            copy.SetFloat(ZWriteId, 1f);
            copy.SetFloat(ColorMaskId, 0f);
            copy.SetFloat(DepthOffsetId, 0f);
            copy.EnableKeyword(FallDepthKeyword);
            copy.SetShaderPassEnabled(ForwardPass, true);
            copy.SetShaderPassEnabled(SoftDepthPass, false);
            copy.SetShaderPassEnabled(SurfaceDepthPass, false);
        }

        // Only the soft depth pass. The mist reads it and fades out in front of the jets.
        private static void SetUpSoftDepth(Material copy, Material fall)
        {
            copy.renderQueue = SoftDepthQueue;
            copy.DisableKeyword(FallDepthKeyword);
            copy.SetShaderPassEnabled(ForwardPass, false);
            copy.SetShaderPassEnabled(SoftDepthPass, true);
            copy.SetShaderPassEnabled(SurfaceDepthPass, false);
            copy.SetFloat(DepthEndId, 1f);
            copy.SetFloat(DepthPushBackId, 0f);
        }

        // The sheet takes the per-renderer values of the feeding water (shore map, foam simulation, weld), so its
        // body look matches that water exactly.
        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            SetClock();
            LoogaWaterBody upstream = Upstream;
            if (!_isShown || !upstream || !upstream.TryGetComponent(out MeshRenderer bodyRenderer)) return;

            _bodyProperties ??= new MaterialPropertyBlock();
            bodyRenderer.GetPropertyBlock(_bodyProperties);
            GetComponent<MeshRenderer>().SetPropertyBlock(_bodyProperties);
        }

#if UNITY_EDITOR
        // Material edits in the editor reach the copies before the next frame.
        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (_isShown) ApplyMaterials();
        }
#endif
        #endregion

        // The water clock for the fall shader in two parts: whole periods and the seconds in the period. A float
        // clock lost precision after hours, and the fall textures moved in uneven steps.
        private static void SetClock()
        {
            double now = Application.isPlaying ? Time.timeAsDouble : Time.realtimeSinceStartupAsDouble;
            double periods = System.Math.Floor(now / ClockPeriod);
            Shader.SetGlobalVector(ClockId, new Vector4((float)periods, (float)(now - periods * ClockPeriod), 1f, 0f));
        }

        // A copy of the material of the feeding water with the fall values of a fall material. A change of either
        // material copies the properties again. Without a feeding water material of the same shader, the copy is of
        // the fall material.
        private sealed class DerivedMaterial
        {
            private Material _body;
            private Material _fall;
            private Material _copy;
            private int _sourceHash;

            public Material Get(Material body, Material fall, Action<Material, Material> setUp)
            {
                if (!fall)
                {
                    Release();
                    return null;
                }
                Material source = body && body.shader == fall.shader ? body : fall;
                int hash = source.ComputeCRC() * 31 + fall.ComputeCRC();
                if (_copy && body == _body && fall == _fall && hash == _sourceHash) return _copy;

                if (!_copy || _copy.shader != source.shader)
                {
                    Release();
                    _copy = new Material(source)
                    {
                        name = fall.name + " (fall)",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }
                else
                {
                    _copy.CopyPropertiesFromMaterial(source);
                    _copy.shaderKeywords = source.shaderKeywords;
                }
                if (source != fall) CopyFallProperties(fall, _copy);
                _copy.EnableKeyword(FallKeyword);
                _body = body;
                _fall = fall;
                _sourceHash = hash;
                setUp(_copy, fall);
                return _copy;
            }

            public void Release()
            {
                if (_copy)
                {
                    if (Application.isPlaying)
                    {
                        UnityEngine.Object.Destroy(_copy);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(_copy);
                    }
                }
                _copy = null;
                _body = null;
                _fall = null;
            }

            private static void CopyFallProperties(Material fall, Material target)
            {
                Shader shader = fall.shader;
                int count = shader.GetPropertyCount();
                for (int index = 0; index < count; index++)
                {
                    if (!shader.GetPropertyName(index).StartsWith(FallPropertyPrefix, StringComparison.Ordinal)) continue;

                    int id = shader.GetPropertyNameId(index);
                    switch (shader.GetPropertyType(index))
                    {
                        case ShaderPropertyType.Color:
                            target.SetColor(id, fall.GetColor(id));
                            break;
                        case ShaderPropertyType.Vector:
                            target.SetVector(id, fall.GetVector(id));
                            break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            target.SetFloat(id, fall.GetFloat(id));
                            break;
                        case ShaderPropertyType.Texture:
                            target.SetTexture(id, fall.GetTexture(id));
                            break;
                        case ShaderPropertyType.Int:
                            target.SetInteger(id, fall.GetInteger(id));
                            break;
                    }
                }
            }
        }
    }
}
