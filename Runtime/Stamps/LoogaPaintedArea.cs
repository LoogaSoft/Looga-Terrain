using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Painted weight mask over a box. The transform sets the box: x and z scale give the size, the y rotation turns it.
    /// Stamps that refer to it multiply their falloff with the mask. Outside the box the mask keeps its edge value.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Painted Area")]
    public sealed class LoogaPaintedArea : MonoBehaviour
    {
        [Tooltip("Limit Global stamps that use this area to the box.")]
        [SerializeField] private bool _limitToBounds;
        [SerializeField, HideInInspector] private int _size;
        // Raw 16-bit mask values, row by row from the box corner at local -0.5, -0.5.
        [SerializeField, HideInInspector] private byte[] _mask;

        private Texture2D _texture;
        private bool _textureChanged;

        public bool LimitToBounds => _limitToBounds;
        public int Size => _size;

        /// <summary>Changes after each settings or mask change. Change tracking compares it.</summary>
        internal int Revision { get; private set; }

        /// <summary>Sets the mask from raw 16-bit data (two bytes for each sample, little endian).</summary>
        public void SetMask(byte[] data, int size, bool limitToBounds)
        {
            _mask = data;
            _size = size;
            _limitToBounds = limitToBounds;
            ReleaseTexture();
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        /// <summary>The mask as a texture, created on first use. An empty mask gives full weight.</summary>
        internal Texture2D GetTexture()
        {
            if (_textureChanged)
            {
                ReleaseTexture();
                _textureChanged = false;
            }
            if (_texture) return _texture;
            if (_mask == null || _size <= 0 || _mask.Length != _size * _size * 2) return Texture2D.whiteTexture;
            _texture = new Texture2D(_size, _size, TextureFormat.R16, false, true)
            {
                name = "Looga Painted Area " + name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
            _texture.LoadRawTextureData(_mask);
            _texture.Apply(false, false);
            return _texture;
        }

        // Unity does not allow object destruction during OnValidate. GetTexture rebuilds the texture later.
        private void OnValidate()
        {
            _textureChanged = true;
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        private void OnDestroy()
        {
            ReleaseTexture();
        }

        private void ReleaseTexture()
        {
            if (_texture)
            {
                DestroyImmediate(_texture);
            }
            _texture = null;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.6f, 0.2f, 1f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(new Vector3(0f, 0.5f, 0f), Vector3.one);
        }
    }
}
