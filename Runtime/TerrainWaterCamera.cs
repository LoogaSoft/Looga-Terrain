using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace LoogaSoft.Terrain
{
    /// <summary>Requests camera textures for depth-based water and restores previous camera options when disabled.</summary>
    [AddComponentMenu("Looga/Terrain/Water Camera")]
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(Camera), typeof(UniversalAdditionalCameraData))]
    public sealed class TerrainWaterCamera : MonoBehaviour
    {
        private UniversalAdditionalCameraData _cameraData;
        [SerializeField, HideInInspector] private CameraOverrideOption _previousDepth;
        [SerializeField, HideInInspector] private CameraOverrideOption _previousColor;
        [SerializeField, HideInInspector] private bool _captured;

        /// <summary>Reports camera configurations that need a separate water validation.</summary>
        public string Diagnostic => _cameraData && _cameraData.renderType == CameraRenderType.Overlay
            ? "Attach TerrainWaterCamera to the base camera. Overlay water needs separate validation."
            : string.Empty;

        #region Built-in
        private void OnEnable()
        {
            _cameraData = GetComponent<UniversalAdditionalCameraData>();
            if (!_captured)
            {
                _previousDepth = _cameraData.requiresDepthOption;
                _previousColor = _cameraData.requiresColorOption;
                _captured = true;
            }
            _cameraData.requiresDepthOption = CameraOverrideOption.On;
            _cameraData.requiresColorOption = CameraOverrideOption.On;
        }

        private void OnDisable()
        {
            if (!_captured || !_cameraData) return;
            // Preserve explicit camera option changes made while this component was active.
            if (_cameraData.requiresDepthOption == CameraOverrideOption.On)
            {
                _cameraData.requiresDepthOption = _previousDepth;
            }
            if (_cameraData.requiresColorOption == CameraOverrideOption.On)
            {
                _cameraData.requiresColorOption = _previousColor;
            }
            _captured = false;
        }
        #endregion
    }
}
