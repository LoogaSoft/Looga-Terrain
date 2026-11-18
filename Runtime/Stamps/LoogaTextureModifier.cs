using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Base of the components that paint terrain layers. A <see cref="LoogaStampWorld"/> applies them in reverse
    /// hierarchy order: a later modifier takes its weight first, an earlier one fills the weight that is left.
    /// </summary>
    public abstract class LoogaTextureModifier : MonoBehaviour
    {
        /// <summary>Changes after each settings change. Change tracking compares it.</summary>
        internal int Revision { get; private set; }

        [System.NonSerialized] private int _serializedHash;

        /// <summary>The terrain layer that the modifier paints.</summary>
        public abstract TerrainLayer Layer { get; }

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
    }
}
