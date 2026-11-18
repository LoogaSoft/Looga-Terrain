using System.Runtime.InteropServices;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>The kinds of emitters that a <see cref="LoogaWaterEffects"/> places, in drawing order.</summary>
    public enum WaterEffectKind
    {
        // Low, slow fog that drifts out over the plunge pool.
        FogBank = 0,
        // A tall cloud that rises from the plunge of a tall fall.
        RisingPlume = 1,
        // Faint spray that peels off the falling water halfway down.
        FaceMistUpper = 2,
        // Denser spray near the foot of the falling water.
        FaceMistLower = 3,
        // Small puffs that fall with the water and grow on their way down.
        FaceSprayFine = 4,
        // Thick spray thrown up where the falling water hits the water below.
        JetSpray = 5,
        // Water thrown up by the impact that falls back.
        ImpactSplash = 6,
        // Low spray over rapids and cascades.
        RapidsSpray = 7
    }

    /// <summary>
    /// One emitter of the water effects, as the GPU reads it. The particles of an emitter have no state: the shader
    /// calculates each particle from the emitter, the particle number and the world clock.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaterEffectEmitterData
    {
        public const int Stride = 10 * 16;

        // World centre of the spawn box (xyz) and the water level below the emitter (w).
        public Vector4 Centre;
        // World unit vector across the flow (xyz) and the box width (w).
        public Vector4 Across;
        // World horizontal unit vector downstream (xyz) and the box length (w).
        public Vector4 Forward;
        // Smallest start velocity across, up and downstream (xyz), in m/s, and the box height (w).
        public Vector4 VelocityMin;
        // Largest start velocity across, up and downstream (xyz), in m/s, and the downward acceleration (w), in m/s^2.
        public Vector4 VelocityMax;
        // Smallest and largest lifetime (xy), in seconds, and the smallest and largest start size (zw), in metres.
        public Vector4 Life;
        // Size factor at birth and at death (xy), drift speed (z), in m/s, and largest spin (w), in radians per second.
        public Vector4 Shape;
        // Linear colour and opacity.
        public Vector4 Color;
        // Distance where the particle count starts to fall (x), fade start (y) and fade end (z), in metres. Share of
        // the particles that stays at the fade end (w).
        public Vector4 Fade;
        // Random seed of the emitter (x). It comes from the emitter position, so a new plan keeps unchanged emitters.
        // Water fade height (y), in metres. 0 = the material value. Scale of the material soft distance (z) and of
        // the material water fade (w), for large particles. Values below 1 count as 1.
        public Vector4 Extra;
    }

    /// <summary>One planned emitter: its kind, its particle count and its GPU data.</summary>
    internal struct WaterEffectEmitter
    {
        public WaterEffectKind Kind;
        public int Count;
        public WaterEffectEmitterData Data;
    }
}
