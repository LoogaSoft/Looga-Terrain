using UnityEngine;

namespace LoogaSoft.Terrain.Surface
{
    /// <summary>
    /// Binds one colour map over one world area as global shader properties. The terrain surface uses the map colour
    /// in place of the layer colour, and the layers keep their own fine brightness variation. A typical source is the
    /// colour output of a terrain generator for the same area as its heightmap.
    /// </summary>
    /// <remarks>
    /// One region colour map can be active at a time. The last enabled component binds its map.
    /// </remarks>
    [ExecuteAlways]
    [AddComponentMenu("Looga/Terrain/Region Color Map")]
    public sealed class LoogaRegionColor : MonoBehaviour
    {
        private static readonly int MapId = Shader.PropertyToID("_LoogaRegionColorMap");
        private static readonly int WeightId = Shader.PropertyToID("_LoogaRegionColorWeight");
        private static readonly int StateId = Shader.PropertyToID("_LoogaRegionColorState");
        private static readonly int AreaId = Shader.PropertyToID("_LoogaRegionColorArea");
        private static readonly int FadeId = Shader.PropertyToID("_LoogaRegionColorFade");
        private static readonly int GradeId = Shader.PropertyToID("_LoogaRegionColorGrade");

        private static LoogaRegionColor _active;

        [Header("Map")]
        [Tooltip("Colour map (sRGB). Row 0 of the image is the north edge, as an imported image shows it.")]
        [SerializeField] private Texture2D _colorMap;
        [Tooltip("Optional weight map (R channel). 1 = full map colour, 0 = the terrain layers. Empty = 1 everywhere.")]
        [SerializeField] private Texture2D _weightMap;
        [Tooltip("World X and Z of the south-west corner of the map.")]
        [SerializeField] private Vector2 _origin;
        [Tooltip("Width and depth of the map area in meters.")]
        [Min(1f)]
        [SerializeField] private float _size = 1000f;

        [Header("Colour")]
        [Tooltip("Multiplies the map colour. Use it to match the map to the scene light. 1 = no change.")]
        [Min(0f)]
        [SerializeField] private float _brightness = 1f;
        [Tooltip("Saturation of the map colour. 0 = grey, 1 = no change.")]
        [Min(0f)]
        [SerializeField] private float _saturation = 1f;

        [Header("Blend")]
        [Tooltip("How much the layer textures keep their own brightness variation. 0 = flat map colour, 1 = full grain.")]
        [Range(0f, 1f)]
        [SerializeField] private float _layerDetail = 1f;
        [Tooltip("Map strength near the camera and far from the camera.")]
        [SerializeField] private Vector2 _strengthNearFar = Vector2.one;
        [Tooltip("Camera distances in meters where the strength goes from the near value to the far value.")]
        [SerializeField] private Vector2 _fadeDistances = new(30f, 120f);

        public Texture2D ColorMap => _colorMap;

        #region Built-in

        private void OnEnable()
        {
            _active = this;
            Bind();
        }

        private void OnDisable()
        {
            if (_active != this) return;
            _active = null;
            Shader.SetGlobalVector(StateId, Vector4.zero);
        }

        private void OnValidate()
        {
            if (_active == this && isActiveAndEnabled)
            {
                Bind();
            }
        }

        #endregion

        /// <summary>Sets the maps and the area, then binds them when this component is active.</summary>
        public void SetMap(Texture2D colorMap, Texture2D weightMap, Vector2 origin, float size)
        {
            _colorMap = colorMap;
            _weightMap = weightMap;
            _origin = origin;
            _size = Mathf.Max(1f, size);
            if (_active == this && isActiveAndEnabled)
            {
                Bind();
            }
        }

        private void Bind()
        {
            if (!_colorMap)
            {
                Shader.SetGlobalVector(StateId, Vector4.zero);
                return;
            }
            Shader.SetGlobalTexture(MapId, _colorMap);
            Shader.SetGlobalTexture(WeightId, _weightMap ? _weightMap : Texture2D.whiteTexture);
            Shader.SetGlobalVector(AreaId, new Vector4(_origin.x, _origin.y, 1f / _size, 1f / _size));
            Shader.SetGlobalVector(FadeId, new Vector4(_strengthNearFar.x, _strengthNearFar.y, _fadeDistances.x,
                Mathf.Max(_fadeDistances.y, _fadeDistances.x + 0.01f)));
            Shader.SetGlobalVector(GradeId, new Vector4(_brightness, _saturation, 0f, 0f));
            Shader.SetGlobalVector(StateId, new Vector4(1f, _layerDetail, _weightMap ? 1f : 0f, 0f));
        }
    }
}
