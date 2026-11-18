using System.Collections.Generic;
using LoogaSoft.Instancing;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Planar reflection for one flat water body, for example a sea. Before a view camera renders, a hidden camera
    /// renders the scene mirrored at the water surface. The Looga water shader then reflects that image on all water at
    /// the height of the body. The reflection runs only while the water covers enough of the view.
    /// </summary>
    /// <remarks>
    /// The hidden camera is a Reflection camera. The Looga grass, ambient occlusion, post-processing, lighting and
    /// shadow passes skip that camera type, so the reflection is a reduced scene. The reflection renders at a lower
    /// resolution with a lower LOD bias, which also removes small instances. Water surfaces do not show in it. Use one
    /// planar reflection in a scene: the water shader has one reflection plane.
    /// </remarks>
    [AddComponentMenu("Looga/Terrain/Water Planar Reflection")]
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(LoogaWaterBody))]
    public sealed class LoogaPlanarReflection : MonoBehaviour
    {
        private static readonly int TextureId = Shader.PropertyToID("_LoogaPlanarReflection");
        private static readonly int ParametersId = Shader.PropertyToID("_LoogaPlanarReflectionParams");
        private static readonly int PlanarPassId = Shader.PropertyToID("_LoogaWaterPlanarPass");
        // Screen samples of the coverage check.
        private const int CoverageColumns = 8;
        private const int CoverageRows = 6;
        // Seconds between two coverage checks of a view.
        private const float CoverageInterval = 0.25f;
        // The reflection stops when the coverage falls below this share of the minimum, so it does not flicker.
        private const float CoverageHysteresis = 0.7f;

        [Header("Activation")]
        [Tooltip("The reflection runs while the water covers this share of the view. 0.15 = 15 %.")]
        [SerializeField, Range(0f, 1f)] private float _minimumCoverage = 0.15f;
        [Tooltip("Water farther from the camera than this does not count for the coverage, in meters.")]
        [SerializeField, Min(1f)] private float _coverageDistance = 1500f;
        [Tooltip("The reflection stops when the camera is higher above the water than this, in meters.")]
        [SerializeField, Min(1f)] private float _maximumHeight = 400f;
        [Tooltip("Layers that hide the water in the coverage check, for example the terrain and rocks.")]
        [SerializeField] private LayerMask _occluders = Physics.DefaultRaycastLayers;
        [Tooltip("Seconds for the reflection to fade in or out.")]
        [SerializeField, Min(0.01f)] private float _fadeTime = 0.5f;
        [Tooltip("Off = only game cameras get the reflection.")]
        [SerializeField] private bool _sceneView = true;

        [Header("Quality")]
        [Tooltip("Resolution of the reflection relative to the view. 0.5 = half width and half height.")]
        [SerializeField, Range(0.125f, 1f)] private float _resolution = 0.5f;
        [Tooltip("LOD bias of the reflection relative to the quality setting. " +
                 "Lower = coarser meshes and fewer small objects.")]
        [SerializeField, Range(0.05f, 1f)] private float _lodBias = 0.4f;
        [Tooltip("The reflection renders every n-th frame of a view. 1 = every frame.")]
        [SerializeField, Range(1, 8)] private int _updateInterval = 1;
        [Tooltip("URP main light shadows in the reflection. Off is cheaper. Looga shadows always skip the reflection.")]
        [SerializeField] private bool _shadows;
        [Tooltip("Far clip distance of the reflection, in meters. 0 = the far clip of the view. A shorter distance " +
                 "skips far geometry: the reflection camera has no occlusion culling, so hidden land also draws.")]
        [SerializeField, Min(0f)] private float _farClip;
        [Tooltip("Skips Looga instance populations with Decorative quality, for example terrain detail grass.")]
        [SerializeField] private bool _skipDecorative = true;
        [Tooltip("Skips Looga instances smaller than this many pixels in the reflection, for example small rocks.")]
        [SerializeField, Min(0f)] private float _minimumPixels = 16f;
        [Tooltip("Skips Looga instances farther than this from the camera, in meters. 0 = no limit. The far clip " +
                 "does not limit them: the oblique near plane moves the far plane of the reflection projection.")]
        [SerializeField, Min(0f)] private float _instanceDistance;
        [Tooltip("Layers that the reflection shows.")]
        [SerializeField] private LayerMask _layers = -1;
        [Tooltip("The clip plane lies this far above the water, in meters, so objects under the water do not show.")]
        [SerializeField] private float _clipOffset = 0.1f;
        [Tooltip("Shift of the reflection by the surface normals, as a share of the screen.")]
        [SerializeField, Range(0f, 0.2f)] private float _distortion = 0.04f;

        // GPU time of the reflection renders, in the sampler "Looga Water.Planar Reflection". The commands run at once,
        // so they enclose the work of the reflection camera, which submits its own commands.
        private static CustomSampler _sampler;
        private static CommandBuffer _beginSample;
        private static CommandBuffer _endSample;

        private readonly Dictionary<Camera, View> _views = new();
        private readonly List<Camera> _lostViews = new();
        private float _height;
        private bool _rendering;

        /// <summary>Height of the reflection plane in world space, in meters.</summary>
        public float Height => _height;
        /// <summary>Empty when the reflection can run. Otherwise the reason why it cannot.</summary>
        public string Diagnostic { get; private set; } = string.Empty;

        /// <summary>
        /// The reflection of a view camera. Returns the fade weight (0 = off) and the share of the view that the
        /// water covered at the last check.
        /// </summary>
        public float GetWeight(Camera camera, out float coverage)
        {
            coverage = 0f;
            if (!camera || !_views.TryGetValue(camera, out View view)) return 0f;
            coverage = view.Coverage;
            return view.Weight;
        }

        #region Built-in
        private void OnEnable()
        {
            RenderPipelineManager.beginContextRendering += OnBeginContext;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContext;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            foreach (View view in _views.Values)
            {
                view.Release();
            }
            _views.Clear();
            Shader.SetGlobalVector(ParametersId, Vector4.zero);
            Shader.SetGlobalFloat(PlanarPassId, 0f);
        }
        #endregion

        #region Rendering
        // Renders the reflections before any camera of the context renders. A render inside the callbacks of a view
        // camera would change the camera state that other systems keep for that view.
        private void OnBeginContext(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (_rendering) return;
            RemoveLostViews();
            if (!TryGetHeight(out _height))
            {
                foreach (View view in _views.Values)
                {
                    view.Weight = 0f;
                    view.ReleaseTexture();
                }
                return;
            }
            foreach (Camera camera in cameras)
            {
                if (!IsViewCamera(camera)) continue;
                if (!_views.TryGetValue(camera, out View view))
                {
                    view = new View(name);
                    _views.Add(camera, view);
                }
                UpdateWeight(camera, view);
                if (view.Weight <= 0f)
                {
                    view.ReleaseTexture();
                    continue;
                }
                view.Frame++;
                if (view.Texture && view.Frame % _updateInterval != 0) continue;
                Render(camera, view);
            }
        }

        // Sets the reflection of the view for the water shader. The reflection camera hides all water surfaces.
        private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Looga Water.Planar Reflection Bind");
            bool reflection = IsReflectionCamera(camera);
            cmd.SetGlobalFloat(PlanarPassId, reflection ? 1f : 0f);
            if (!reflection && camera && _views.TryGetValue(camera, out View view) && view.Weight > 0f &&
                view.Texture)
            {
                cmd.SetGlobalTexture(TextureId, view.Texture);
                cmd.SetGlobalVector(ParametersId, new Vector4(_height, view.Weight, view.Texture.mipmapCount,
                    _distortion));
            }
            else
            {
                cmd.SetGlobalVector(ParametersId, Vector4.zero);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void Render(Camera source, View view)
        {
            int width = Mathf.Max(16, Mathf.RoundToInt(source.pixelWidth * _resolution));
            int height = Mathf.Max(16, Mathf.RoundToInt(source.pixelHeight * _resolution));
            view.EnsureTexture(width, height);
            Camera reflection = view.Camera;
            reflection.fieldOfView = source.fieldOfView;
            reflection.aspect = source.aspect;
            reflection.nearClipPlane = source.nearClipPlane;
            reflection.farClipPlane = _farClip > 0f ? Mathf.Min(_farClip, source.farClipPlane) : source.farClipPlane;
            reflection.clearFlags = source.clearFlags;
            reflection.backgroundColor = source.backgroundColor;
            reflection.cullingMask = source.cullingMask & _layers;
            view.Data.renderShadows = _shadows;
            view.Filter.SkipDecorative = _skipDecorative;
            view.Filter.MinimumPixels = _minimumPixels;
            view.Filter.MaximumDistance = _instanceDistance;

            // The pose is the mirror image of the view camera, so distance-based detail matches the reflected view.
            Transform pose = source.transform;
            Vector3 position = pose.position;
            Vector3 forward = pose.forward;
            Vector3 up = pose.up;
            reflection.transform.SetPositionAndRotation(new Vector3(position.x, 2f * _height - position.y, position.z),
                Quaternion.LookRotation(new Vector3(forward.x, -forward.y, forward.z), new Vector3(up.x, -up.y, up.z)));
            Matrix4x4 mirror = Matrix4x4.identity;
            mirror.m11 = -1f;
            mirror.m13 = 2f * _height;
            reflection.worldToCameraMatrix = source.worldToCameraMatrix * mirror;
            // The oblique near plane is the water surface, so the reflection shows nothing under the water.
            reflection.ResetProjectionMatrix();
            reflection.projectionMatrix = reflection.CalculateObliqueMatrix(ClipPlane(reflection.worldToCameraMatrix));

            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = view.Texture };
            if (!RenderPipeline.SupportsRenderRequest(reflection, request))
            {
                Diagnostic = "The active render pipeline cannot render a single camera request.";
                return;
            }
            float lodBias = QualitySettings.lodBias;
            bool invertCulling = GL.invertCulling;
            _rendering = true;
            BeginSample();
            try
            {
                QualitySettings.lodBias = lodBias * _lodBias;
                // The mirror turns the winding of every triangle.
                GL.invertCulling = !invertCulling;
                RenderPipeline.SubmitRenderRequest(reflection, request);
            }
            finally
            {
                GL.invertCulling = invertCulling;
                QualitySettings.lodBias = lodBias;
                _rendering = false;
            }
            // The mips are the blur levels for rough water.
            view.Texture.GenerateMips();
            Graphics.ExecuteCommandBuffer(_endSample);
            Diagnostic = string.Empty;
        }

        private static void BeginSample()
        {
            if (_sampler == null)
            {
                _sampler = CustomSampler.Create("Looga Water.Planar Reflection", true);
                _beginSample = new CommandBuffer { name = "Looga Water.Planar Reflection Begin" };
                _beginSample.BeginSample(_sampler);
                _endSample = new CommandBuffer { name = "Looga Water.Planar Reflection End" };
                _endSample.EndSample(_sampler);
            }
            Graphics.ExecuteCommandBuffer(_beginSample);
        }

        private Vector4 ClipPlane(Matrix4x4 worldToCamera)
        {
            Vector3 point = worldToCamera.MultiplyPoint(new Vector3(0f, _height + _clipOffset, 0f));
            Vector3 normal = worldToCamera.MultiplyVector(Vector3.up).normalized;
            return new Vector4(normal.x, normal.y, normal.z, -Vector3.Dot(point, normal));
        }
        #endregion

        #region Activation
        private void UpdateWeight(Camera camera, View view)
        {
            float now = Time.realtimeSinceStartup;
            float step = view.LastUpdate > 0f ? Mathf.Max(0f, now - view.LastUpdate) / _fadeTime : 0f;
            view.LastUpdate = now;
            float cameraHeight = camera.transform.position.y - _height;
            bool above = cameraHeight > _clipOffset && cameraHeight < _maximumHeight;
            if (now >= view.NextCheck)
            {
                view.NextCheck = now + CoverageInterval;
                view.Coverage = above ? Coverage(camera) : 0f;
            }
            float minimum = view.Wanted ? _minimumCoverage * CoverageHysteresis : _minimumCoverage;
            view.Wanted = above && view.Coverage >= minimum;
            view.Weight = Mathf.MoveTowards(view.Weight, view.Wanted ? 1f : 0f, step);
        }

        // Share of the view that shows the water within the coverage distance. Rays hit the occluders, for example the
        // terrain colliders, so land in front of the water does not count.
        private float Coverage(Camera camera)
        {
            int water = 0;
            for (int row = 0; row < CoverageRows; row++)
            {
                for (int column = 0; column < CoverageColumns; column++)
                {
                    Ray ray = camera.ViewportPointToRay(new Vector3((column + 0.5f) / CoverageColumns,
                        (row + 0.5f) / CoverageRows, 0f));
                    if (ray.direction.y > -1e-4f) continue;
                    float distance = (ray.origin.y - _height) / -ray.direction.y;
                    if (distance > _coverageDistance) continue;
                    // A hit at the water height is a collider of the water itself.
                    if (Physics.Raycast(ray, out RaycastHit hit, distance, _occluders,
                            QueryTriggerInteraction.Ignore) && hit.point.y > _height + 0.5f) continue;
                    water++;
                }
            }
            return water / (float)(CoverageColumns * CoverageRows);
        }

        // An Area body has one surface height. The mesh bounds have the same wave padding above and below it.
        private bool TryGetHeight(out float height)
        {
            height = _height;
            if (!TryGetComponent(out LoogaWaterBody body) || body.Shape != LoogaWaterBody.WaterShape.Area)
            {
                Diagnostic = "A planar reflection needs an Area water body, which has one surface height.";
                return false;
            }
            if (!TryGetComponent(out MeshFilter filter) || !filter.sharedMesh)
            {
                Diagnostic = "The water body has no mesh.";
                return false;
            }
            height = transform.TransformPoint(filter.sharedMesh.bounds.center).y;
            return true;
        }

        private bool IsViewCamera(Camera camera)
        {
            if (!camera || camera.orthographic) return false;
            return camera.cameraType == CameraType.Game || _sceneView && camera.cameraType == CameraType.SceneView;
        }

        private bool IsReflectionCamera(Camera camera)
        {
            foreach (View view in _views.Values)
            {
                if (view.Camera == camera) return true;
            }
            return false;
        }

        // Views of destroyed cameras, for example a closed scene view.
        private void RemoveLostViews()
        {
            _lostViews.Clear();
            foreach (KeyValuePair<Camera, View> entry in _views)
            {
                if (!entry.Key) _lostViews.Add(entry.Key);
            }
            foreach (Camera camera in _lostViews)
            {
                _views[camera].Release();
                _views.Remove(camera);
            }
        }
        #endregion

        // The reflection camera and texture of one view camera. Each view has its own camera, because the terrain and
        // instance renderers keep state for each camera.
        private sealed class View
        {
            internal readonly Camera Camera;
            internal readonly UniversalAdditionalCameraData Data;
            internal readonly InstanceCameraFilter Filter;
            internal RenderTexture Texture;
            internal float Weight;
            internal float Coverage;
            internal bool Wanted;
            internal float NextCheck;
            internal float LastUpdate;
            internal int Frame;

            internal View(string owner)
            {
                var cameraObject = new GameObject(owner + " Planar Reflection")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                Camera = cameraObject.AddComponent<Camera>();
                Camera.enabled = false;
                Camera.cameraType = CameraType.Reflection;
                Data = cameraObject.AddComponent<UniversalAdditionalCameraData>();
                Data.renderPostProcessing = false;
                Data.antialiasing = AntialiasingMode.None;
                // The height fog overlay reads the depth, so the reflection fogs along the reflected path.
                Data.requiresDepthOption = CameraOverrideOption.On;
                Data.requiresColorOption = CameraOverrideOption.Off;
                // The reflection draws no small instances. The camera has no occlusion for land behind its sky.
                Filter = cameraObject.AddComponent<InstanceCameraFilter>();
            }

            internal void EnsureTexture(int width, int height)
            {
                if (Texture && Texture.width == width && Texture.height == height) return;
                ReleaseTexture();
                var descriptor = new RenderTextureDescriptor(width, height, GraphicsFormat.B10G11R11_UFloatPack32,
                    GraphicsFormat.D24_UNorm_S8_UInt)
                {
                    useMipMap = true,
                    autoGenerateMips = false,
                    msaaSamples = 1
                };
                Texture = new RenderTexture(descriptor)
                {
                    name = Camera.name,
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                Texture.Create();
            }

            internal void ReleaseTexture()
            {
                if (!Texture) return;
                Texture.Release();
                DestroyObject(Texture);
                Texture = null;
            }

            internal void Release()
            {
                ReleaseTexture();
                if (Camera) DestroyObject(Camera.gameObject);
            }

            private static void DestroyObject(Object target)
            {
                if (Application.isPlaying)
                {
                    Object.Destroy(target);
                }
                else
                {
                    Object.DestroyImmediate(target);
                }
            }
        }
    }
}
