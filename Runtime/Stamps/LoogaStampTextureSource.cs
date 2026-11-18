using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Supplies textures to the stamp on the same object at evaluation time, in place of its serialized textures: the
    /// height texture of a <see cref="LoogaHeightStamp"/>, the falloff texture (Texture falloff shape) of a height,
    /// texture, clear, object or detail stamp, and the two weight noise textures (Texture noise type) of an object or
    /// detail stamp. A generator can keep its results on the GPU or in memory, for example
    /// textures that it rebuilds when its inputs change. The textures are not saved: the generator sets them again
    /// after a scene load. A null texture keeps the serialized one. A stamp that has neither a serialized nor a supplied
    /// texture is left out of the evaluation until its source has one.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Stamp Texture Source")]
    [DisallowMultipleComponent]
    public sealed class LoogaStampTextureSource : MonoBehaviour
    {
        private Texture _heightTexture;
        private Texture _falloffTexture;
        private Texture _weightNoiseTexture;
        private Texture _weight2NoiseTexture;
        private Rect? _changedRegion;

        /// <summary>Height texture, 0 = bottom and 1 = top of the stamp. Null keeps the stamp texture.</summary>
        public Texture HeightTexture => _heightTexture;

        /// <summary>Falloff texture for the Texture falloff shape. Null keeps the falloff texture.</summary>
        public Texture FalloffTexture => _falloffTexture;

        /// <summary>Texture of the first weight noise (Texture noise type). Null keeps the noise texture.</summary>
        public Texture WeightNoiseTexture => _weightNoiseTexture;

        /// <summary>Texture of the second weight noise (Texture noise type). Null keeps the noise texture.</summary>
        public Texture Weight2NoiseTexture => _weight2NoiseTexture;

        /// <summary>
        /// Sets the textures and marks the stamp as changed, so the editor updater applies them over the whole stamp
        /// area.
        /// </summary>
        public void SetTextures(Texture heightTexture, Texture falloffTexture)
        {
            _heightTexture = heightTexture;
            _falloffTexture = falloffTexture;
            MarkStampChanged();
        }

        /// <summary>
        /// Sets the height and the falloff texture. Call it also after the contents of the same textures changed.
        /// </summary>
        /// <param name="changedRegion">
        /// World XZ area where the textures changed. The editor updater then updates only this area, for example when
        /// one knot of a long generated cliff moved. Null updates the whole stamp area. Outside the region, the new
        /// textures must give the same result as the previous ones.
        /// </param>
        public void SetTextures(Texture heightTexture, Texture falloffTexture, Rect? changedRegion)
        {
            _heightTexture = heightTexture;
            _falloffTexture = falloffTexture;
            MarkChanged(changedRegion);
        }

        /// <summary>
        /// Sets the falloff texture. Call it also after the contents of the same texture changed.
        /// </summary>
        /// <param name="changedRegion">
        /// World XZ area where the texture changed. The editor updater then updates only this area, not the whole
        /// stamp, for example when one road of a mask over a large road network moved. Null updates the whole stamp
        /// area. Outside the region, the new texture must give the same falloff as the previous one.
        /// </param>
        public void SetFalloffTexture(Texture falloffTexture, Rect? changedRegion = null)
        {
            _falloffTexture = falloffTexture;
            MarkChanged(changedRegion);
        }

        /// <summary>
        /// Sets the texture of the first or the second weight noise. Call it also after the contents of the same
        /// texture changed.
        /// </summary>
        /// <param name="second">True for the second weight noise.</param>
        /// <param name="changedRegion">World XZ area where the texture changed, as for SetFalloffTexture.</param>
        public void SetWeightNoiseTexture(bool second, Texture texture, Rect? changedRegion = null)
        {
            if (second)
            {
                _weight2NoiseTexture = texture;
            }
            else
            {
                _weightNoiseTexture = texture;
            }
            MarkChanged(changedRegion);
        }

        // Marks the stamp as changed over the region, or over its whole area without a region.
        private void MarkChanged(Rect? changedRegion)
        {
            if (!changedRegion.HasValue)
            {
                MarkStampChanged();
                return;
            }
            Rect region = changedRegion.Value;
            _changedRegion = _changedRegion.HasValue
                ? Rect.MinMaxRect(Mathf.Min(_changedRegion.Value.xMin, region.xMin),
                    Mathf.Min(_changedRegion.Value.yMin, region.yMin), Mathf.Max(_changedRegion.Value.xMax, region.xMax),
                    Mathf.Max(_changedRegion.Value.yMax, region.yMax))
                : region;
            LoogaStampWorld.RevisionChanges++;
        }

        /// <summary>Takes the area of the texture changes since the last call. The editor updater calls it.</summary>
        internal bool TakeChangedRegion(out Rect region)
        {
            region = _changedRegion ?? default;
            bool changed = _changedRegion.HasValue;
            _changedRegion = null;
            return changed;
        }

        /// <summary>The falloff texture that the evaluation uses: from an active source, else the falloff settings.</summary>
        internal static Texture FalloffTextureOf(Component stamp, StampFalloff falloff)
        {
            return TryGetActive(stamp, out LoogaStampTextureSource source) && source._falloffTexture
                ? source._falloffTexture
                : falloff.Texture;
        }

        /// <summary>The weight noise texture that the evaluation uses: from an active source, else the noise.</summary>
        /// <param name="second">True for the second weight noise.</param>
        internal static Texture WeightNoiseTextureOf(Component stamp, StampWeightNoise noise, bool second)
        {
            Texture supplied = !TryGetActive(stamp, out LoogaStampTextureSource source) ? null
                : second ? source._weight2NoiseTexture : source._weightNoiseTexture;
            return supplied ? supplied : noise.Texture;
        }

        /// <summary>
        /// True when the stamp has an active source but no texture yet: no height texture for a height stamp, no
        /// falloff texture for a Texture falloff shape, or no texture for a Texture weight noise. A stamp without its
        /// texture would apply over its whole box.
        /// </summary>
        internal static bool Waits(Component stamp)
        {
            if (!TryGetActive(stamp, out LoogaStampTextureSource source)) return false;
            if (stamp is LoogaHeightStamp height && !height.Texture && !source._heightTexture) return true;
            StampFalloff falloff = stamp switch
            {
                LoogaHeightStamp s => s.Falloff,
                LoogaTextureStamp s => s.Falloff,
                LoogaClearStamp s => s.Falloff,
                LoogaObjectStamp s => s.Falloff,
                LoogaDetailStamp s => s.Falloff,
                _ => null
            };
            if (falloff != null && falloff.Shape == FalloffShape.Texture && !falloff.Texture && !source._falloffTexture)
            {
                return true;
            }
            (StampWeightNoise first, StampWeightNoise second) = stamp switch
            {
                LoogaObjectStamp s => (s.WeightNoise, s.Weight2Noise),
                LoogaDetailStamp s => (s.WeightNoise, s.Weight2Noise),
                LoogaClearStamp s => (s.WeightNoise, s.Weight2Noise),
                _ => (null, null)
            };
            return WaitsForNoise(first, source._weightNoiseTexture) ||
                WaitsForNoise(second, source._weight2NoiseTexture);
        }

        private static bool WaitsForNoise(StampWeightNoise noise, Texture supplied)
        {
            return noise != null && noise.Type == StampNoiseType.Texture && !noise.Texture && !supplied;
        }

        private static bool TryGetActive(Component stamp, out LoogaStampTextureSource source)
        {
            return stamp.TryGetComponent(out source) && source.isActiveAndEnabled;
        }

        private void MarkStampChanged()
        {
            if (TryGetComponent(out LoogaHeightModifier height)) height.MarkChanged();
            if (TryGetComponent(out LoogaTextureModifier texture)) texture.MarkChanged();
            if (TryGetComponent(out LoogaPlacementModifier placement)) placement.MarkChanged();
        }
    }
}
