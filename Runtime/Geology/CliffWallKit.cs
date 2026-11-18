using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>How a <see cref="LoogaCliffWall"/> covers the terrain face.</summary>
    public enum CliffWallStyle
    {
        /// <summary>One generated rock mesh that follows the wall profile.</summary>
        Procedural,
        /// <summary>Rows of rock prefabs set against the terrain face, with boulders at the foot.</summary>
        Kit
    }

    /// <summary>
    /// Settings of the kit style of a cliff wall. Wall pieces cover the terrain face from the rim down to the foot, until
    /// the coverage target. Each piece stands with its back in the terrain, so its back and underside do not show.
    /// </summary>
    [Serializable]
    public sealed class CliffWallKit
    {
        [Tooltip("Wall pieces: rock prefabs with closed meshes (Assets > Looga > Close Open Rock Meshes), of one rock type, " +
                 "so the wall reads as one cliff. The build finds their cut sides and buries them in the terrain.")]
        public List<GameObject> Pieces = new();
        [Tooltip("Boulders at the foot of the wall. Optional.")]
        public List<GameObject> FootBoulders = new();
        [Tooltip("Smallest and largest length of the large pieces, in meters. They give the wall its form.")]
        public Vector2 LargeSize = new(10f, 20f);
        [Tooltip("Smallest and largest length of the medium pieces, in meters.")]
        public Vector2 MediumSize = new(6f, 10f);
        [Tooltip("Smallest and largest length of the small pieces that close the gaps, in meters.")]
        public Vector2 SmallSize = new(2.6f, 5f);
        [Tooltip("Smallest and largest scale of a mesh. Far from 1 the rock detail looks too small or too large.")]
        public Vector2 ScaleLimits = new(0.24f, 1.3f);
        [Tooltip("Share of the terrain face to cover. The build stops at this share or at the piece limit.")]
        [Range(0f, 1f)] public float TargetCoverage = 0.995f;
        [Tooltip("Largest number of wall pieces.")]
        [Min(1)] public int MaximumPieces = 1500;
        [Tooltip("Slope in degrees where the brow above the steep face stops. The wall covers the brow down from there.")]
        [Range(5f, 45f)] public float ShoulderAngle = 18f;
        [Tooltip("Distance from the rim down to the top of the highest pieces, in meters.")]
        [Min(0f)] public float RimInset = 0.3f;
        [Tooltip("Distance of the back of a piece behind the terrain face, in meters.")]
        [Min(0f)] public float BackDepth = 0.4f;
        [Tooltip("Smallest and largest distance of the front of a piece in front of the terrain face, in meters. It " +
                 "changes along the wall, so parts of the wall stand out and parts stand back.")]
        public Vector2 ProudRange = new(1f, 4.5f);
        [Tooltip("Share of the face lean that the pieces follow. 0 = upright pieces.")]
        [Range(0f, 1f)] public float FollowFace = 0.85f;
        [Tooltip("Largest angle of the rock bedding from the horizontal, in degrees. The long axis of each piece follows " +
                 "the bedding, which changes slowly along the wall.")]
        [Range(0f, 45f)] public float BeddingDip = 14f;
        [Tooltip("Share of the wall where the long axes run upright, as buttresses.")]
        [Range(0f, 1f)] public float ButtressShare = 0.3f;
        [Tooltip("Largest random tilt of a piece, in degrees.")]
        [Range(0f, 20f)] public float Tilt = 6f;
        [Tooltip("Largest random turn of a piece away from its view, in degrees.")]
        [Range(0f, 45f)] public float Turn = 8f;
        [Tooltip("Tilted pieces on top of the flat layer for each 100 square meters of face. They give ledges and show " +
                 "other sides of the meshes.")]
        [Min(0f)] public float ReliefDensity = 1.6f;
        [Tooltip("Share of the pieces that are turned upside down.")]
        [Range(0f, 1f)] public float FlipChance = 0.3f;
        [Tooltip("Smallest and largest scale of a foot boulder.")]
        public Vector2 FootScale = new(0.9f, 1.6f);
        [Tooltip("Mean distance between two foot boulders along the wall, in meters. 0 = no foot boulders.")]
        [Min(0f)] public float FootSpacing = 6f;
        [Tooltip("Smallest and largest distance of a foot boulder in front of the foot of the face, in meters.")]
        public Vector2 FootDistance = new(0.5f, 4f);
        [Tooltip("Share of the height of a foot boulder that is below the ground.")]
        [Range(0f, 0.9f)] public float FootSink = 0.35f;
        public int Seed = 1;

        internal CliffWallKit Copy()
        {
            var copy = (CliffWallKit)MemberwiseClone();
            copy.Pieces = new List<GameObject>(Pieces);
            copy.FootBoulders = new List<GameObject>(FootBoulders);
            return copy;
        }
    }
}
