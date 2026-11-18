using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Base of the components that place objects or remove placements. A <see cref="LoogaStampWorld"/> applies them in
    /// hierarchy order. A clear stamp removes only the placements of earlier placement stamps.
    /// </summary>
    public abstract class LoogaPlacementModifier : MonoBehaviour
    {
        // A global falloff without a painted area changes every terrain.
        private static readonly Rect Everywhere = Rect.MinMaxRect(-1e9f, -1e9f, 1e9f, 1e9f);

        /// <summary>Changes after each settings change. Change tracking compares it.</summary>
        internal int Revision { get; private set; }

        [System.NonSerialized] private int _serializedHash;

        /// <summary>
        /// Marks the settings as changed, so the editor updater applies them again. Scripts call it after they change
        /// nested settings, such as the falloff, the noise or the filters, through their own setters.
        /// </summary>
        public void MarkChanged()
        {
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        // Unity also calls OnValidate after a script reload and after an undo of another object in the same hierarchy,
        // for example the spline of a generated stamp. Only a change of the serialized settings marks a change.
        protected virtual void OnValidate()
        {
            if (!SerializedState.Changed(this, ref _serializedHash)) return;
            Revision++;
            LoogaStampWorld.RevisionChanges++;
        }

        /// <summary>World XZ rectangle that decides which tiles get the modifier.</summary>
        internal abstract Rect GetCullRect(HeightMapping mapping);

        // MicroVerse GetBounds of placement stamps: a spline area limits the stamp to the area, a global falloff limits it
        // only with a clamped painted area, and every other falloff uses the stamp bounds.
        internal static Rect FalloffCullRect(StampFalloff falloff, Transform stamp, HeightMapping mapping)
        {
            if (falloff.TryGetAreaRect(out Rect area))
            {
                return area;
            }
            if (falloff.Shape == FalloffShape.Global)
            {
                LoogaPaintedArea painted = falloff.PaintedArea;
                return painted && painted.LimitToBounds ? StampMapping.CullRect(painted.transform, mapping) : Everywhere;
            }
            return StampMapping.CullRect(stamp, mapping);
        }
    }
}
