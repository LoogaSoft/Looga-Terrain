using System;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    public enum RockFaceDropSide
    {
        Auto,
        Right,
        Left
    }

    /// <summary>
    /// Raised ground around the foot of a rock face, so the terrain meets the rock in height as well as in texture. A
    /// talus stamp that links to the face applies it to the terrain.
    /// </summary>
    [Serializable]
    public sealed class RockFaceApron
    {
        [Tooltip("Raise the terrain around the foot of the rock.")]
        public bool Enabled = true;
        [Min(0f), Tooltip("Height of the raised ground at the foot of the rock, in meters.")]
        public float Height = 2.5f;
        [Range(1f, 30f), Tooltip("Distance from the foot where the raised ground meets the terrain again, in meters.")]
        public float Reach = 10f;
        [Range(15f, 60f), Tooltip("Slope in degrees at which the raised ground fills the base of the rock.")]
        public float FillAngle = 35f;
        [Range(0f, 1f), Tooltip("Change of the height along the foot. 0 = constant, 1 = from 0 to twice the height.")]
        public float HeightVariation = 0.5f;

        internal bool Same(RockFaceApron other)
        {
            return other != null && Enabled == other.Enabled && Height == other.Height && Reach == other.Reach &&
                   FillAngle == other.FillAngle && HeightVariation == other.HeightVariation;
        }

        internal RockFaceApron Copy()
        {
            return (RockFaceApron)MemberwiseClone();
        }
    }

    [Serializable]
    public sealed class RockFaceSettings
    {
        [Header("Spline Range")]
        [Range(0f, 1f)] public float RangeStart;
        [Range(0f, 1f)] public float RangeEnd = 1f;
        [Tooltip("Parts of the spline where the face builds no rock, as start and end shares of the spline length. " +
                 "For example, another face already covers the cliff there.")]
        public Vector2[] ExcludedRanges = new Vector2[0];
        [Tooltip("Side of the spline where the ground drops. Auto compares the terrain on the two sides.")]
        public RockFaceDropSide DropSide = RockFaceDropSide.Auto;
        [Tooltip("The road network gives the face an opening along each road near it, so no rock stands on a road. " +
                 "The rock next to a road is cut back at 45 degrees.")]
        public bool ClearRoads;

        [Header("Resolution")]
        [Min(0.1f), Tooltip("Distance between mesh columns along the cliff, in meters.")]
        public float ColumnSpacing = 0.35f;
        [Min(0.1f), Tooltip("Largest distance between mesh rows down the face, in meters.")]
        public float RowSpacing = 0.35f;

        [Header("Face Detection")]
        [Min(0.05f), Tooltip("Distance between terrain height samples across the cliff, in meters.")]
        public float ProfileStep = 0.25f;
        [Min(0f), Tooltip("Distance behind the spline where the face search starts, in meters.")]
        public float SearchInward = 10f;
        [Min(1f), Tooltip("Distance in front of the spline where the face search stops, in meters.")]
        public float SearchOutward = 60f;
        [Range(20f, 80f), Tooltip("Ground steeper than this angle, in degrees, is part of the face.")]
        public float FaceAngle = 40f;
        [Min(0f), Tooltip("A flatter bench shorter than this distance does not end the face, in meters.")]
        public float BenchTolerance = 1.5f;
        [Min(0f), Tooltip("A column with a lower face gets no rock, in meters.")]
        public float MinimumFaceHeight = 6f;
        [Min(0f), Tooltip("Distance along the cliff over which rim and foot positions are averaged, in meters.")]
        public float ProfileSmoothing = 3f;

        [Header("Envelope")]
        [Min(0.5f), Tooltip("Width of the rock cap behind the rim, in meters. The cap edge goes below the ground.")]
        public float CapLength = 3f;
        [Min(0.5f), Tooltip("Width of the skirt in front of the foot, in meters. The skirt edge goes below the ground.")]
        public float SkirtLength = 2.5f;
        [Min(0f), Tooltip("Smallest distance between the rock surface and the terrain, in meters.")]
        public float Clearance = 0.35f;
        [Min(0.1f), Tooltip("Depth below the ground of the cap, skirt and end edges, in meters.")]
        public float TuckDepth = 0.8f;
        [Min(0f), Tooltip("Length at each end of a face over which the rock goes into the ground, in meters.")]
        public float EndFade = 8f;

        [Header("Mass")]
        [Min(0f), Tooltip("Largest outward bulge of the buttresses, in meters.")]
        public float MassDepth = 3f;
        [Min(1f), Tooltip("Typical width of a buttress along the cliff, in meters.")]
        public float MassScale = 16f;

        [Header("Strata")]
        [Min(0f), Tooltip("Outward step of a hard layer over a soft layer, in meters.")]
        public float StrataDepth = 1.2f;
        [Tooltip("Smallest and largest layer thickness, in meters.")]
        public Vector2 StrataThickness = new(0.4f, 5f);
        [Range(0f, 1f), Tooltip("Share of hard layers. Hard layers form ledges. Soft layers recede.")]
        public float HardShare = 0.4f;
        [Range(-15f, 15f), Tooltip("Dip of the layers along the cliff, in degrees.")]
        public float StrataDip = 2f;
        [Min(0f), Tooltip("Largest vertical waviness of the layers, in meters.")]
        public float StrataWarp = 2.5f;
        [Min(1f), Tooltip("Distance along the cliff over which a ledge changes strength, in meters.")]
        public float StrataContinuity = 14f;
        [Min(0f), Tooltip("Largest local thickening or thinning of the layers along the cliff, in meters.")]
        public float PinchDepth = 1.2f;
        [Min(0.05f), Tooltip("Largest width of the weathered, sloped top of a hard layer, in meters. Small values give " +
                             "ledges with crisp edges.")]
        public float StrataBevel = 0.6f;

        [Header("Ridged Detail")]
        [Min(0f), Tooltip("Largest outward offset of the ridged fractal detail, in meters. Ridged multifractal noise " +
                          "(Musgrave) gives sharp creases instead of round bumps, and its fine octaves gather along the " +
                          "ridges. 0 = off.")]
        public float RidgedDepth;
        [Min(0.2f), Tooltip("Size of the largest ridged features along the layers, in meters.")]
        public float RidgedScale = 3f;
        [Range(1f, 6f), Tooltip("Stretch of the ridged features along the layers. 1 = round, larger values follow the " +
                               "bedding.")]
        public float RidgedStretch = 3f;
        [Min(0f), Tooltip("Domain warp of the ridged detail, as a share of its scale. It bends the creases into flowing, " +
                          "weathered shapes.")]
        public float RidgedWarp = 0.6f;
        [Range(0f, 1f), Tooltip("Share of the ridged detail on soft layers. Hard layers get all of it.")]
        public float RidgedSoftShare = 0.4f;
        [Range(1f, 6f), Tooltip("Sharpness of the ridge crests. 2 = classic ridged noise; larger values give narrow, " +
                               "knife-like crests.")]
        public float RidgedSharpness = 2f;
        [Range(0, 8), Tooltip("Number of terraces of the ridged detail: each ridge steps up in flat treads with steep " +
                             "risers, so it gets more sharp edges. 0 = smooth.")]
        public int RidgedTerraces;
        [Range(0f, 1f), Tooltip("Share of a second ridged set that runs up the face, across the layers, like joints.")]
        public float RidgedCross;

        [Header("Fractures")]
        [Min(0f), Tooltip("Largest outward step between two fracture faces, in meters. Fracture faces are flat, tilted " +
                          "cells at three sizes, so the rock breaks into angular planes with crisp edges. 0 = off.")]
        public float FractureDepth;
        [Min(0.5f), Tooltip("Size of the largest fracture faces, in meters. Each smaller size is a third of the size " +
                            "before it.")]
        public float FractureSize = 4f;
        [Range(0f, 1f), Tooltip("Tilt of the fracture faces: the largest outward change across one face, as a share of " +
                               "the fracture depth.")]
        public float FractureTilt = 0.6f;
        [Min(0f), Tooltip("Depth of the cracks along the edges of the fracture faces, in meters.")]
        public float CrackDepth = 0.12f;

        [Header("Faults")]
        [Tooltip("Smallest and largest distance between faults along the cliff, in meters.")]
        public Vector2 FaultSpacing = new(15f, 45f);
        [Min(0f), Tooltip("Largest vertical shift of the layers between two fault blocks, in meters.")]
        public float FaultThrow = 2.5f;
        [Range(0f, 10f), Tooltip("Largest tilt of the layers in one fault block, in degrees.")]
        public float FaultTilt = 3f;

        [Header("Joints")]
        [Tooltip("Smallest and largest distance between vertical joints, in meters.")]
        public Vector2 JointSpacing = new(2.5f, 9f);
        [Min(0f), Tooltip("Depth of a joint groove, in meters.")]
        public float JointDepth = 0.6f;
        [Min(0.05f), Tooltip("Half width of a joint groove, in meters.")]
        public float JointWidth = 0.4f;
        [Range(-0.5f, 0.5f), Tooltip("Horizontal lean of the joints per meter of height.")]
        public float JointLean = 0.08f;
        [Range(0f, 1f), Tooltip("Share of joint segments that are open. A closed segment has no groove and no block step.")]
        public float JointPersistence = 0.55f;
        [Min(0f), Tooltip("Largest outward offset of one block between open joints and layer boundaries, in meters.")]
        public float BlockRelief = 0.7f;

        [Header("Spalls")]
        [Range(0f, 1f), Tooltip("Share of blocks that lost their upper part.")]
        public float SpallChance = 0.15f;
        [Min(0f), Tooltip("Largest depth of a spall scar, in meters.")]
        public float SpallDepth = 0.9f;

        [Header("Erosion Grooves")]
        [Min(0f), Tooltip("Depth of the grooves down the fall line, in meters.")]
        public float GrooveDepth = 0.3f;
        [Min(0.2f), Tooltip("Typical distance between grooves, in meters.")]
        public float GrooveSpacing = 1.8f;

        [Header("Roughness")]
        [Min(0f), Tooltip("Largest sideways shift of the layer and joint lines, in meters.")]
        public float DomainWarp = 1.2f;
        [Min(0f), Tooltip("Depth of the fine surface roughness, in meters.")]
        public float RoughnessDepth = 0.25f;
        [Min(0.2f), Tooltip("Typical size of the fine surface roughness, in meters.")]
        public float RoughnessScale = 1.5f;

        [Header("Ground Blend")]
        [Min(0f), Tooltip("Height above the terrain over which the rock fades into the ground texture, in meters. " +
                          "0 = no ground texture.")]
        public float GroundBlendHeight = 1.5f;
        [Range(0f, 1f), Tooltip("Change of the blend height along the cliff, as a share of the blend height.")]
        public float GroundBlendNoise = 0.6f;
        [Min(0f), Tooltip("Distance past the cap and skirt edges where the ground blend reads the terrain layers, in " +
                          "meters. The terrain under the rock has its own steep-ground layers, which the rock hides.")]
        public float GroundReach = 1.5f;

        [Header("Top")]
        [Tooltip("A closed spline that drops on its outer side also gets a rock top over the ground inside the rim, so " +
                 "the walls and the top form one rock body.")]
        public bool FillTop;
        [Min(0f), Tooltip("Height of the tallest crags on the top, in meters.")]
        public float CragHeight = 9f;
        [Min(2f), Tooltip("Typical width of one crag, in meters.")]
        public float CragSize = 40f;
        [Range(1f, 5f), Tooltip("Shape of the crags. 1 = rounded knobs, larger values = narrow peaks and sharp crests.")]
        public float CragSharpness = 1.6f;
        [Range(0f, 1f), Tooltip("Broken, jagged crest lines on the crags.")]
        public float CragJaggedness = 0.5f;
        [Range(0f, 1f), Tooltip("Share of the top that is crag. The rest is low, broken rock ground.")]
        public float CragCoverage = 0.7f;
        [Range(0f, 1f), Tooltip("Crags near the rim are taller and closer, so the walls rise on into the crags.")]
        public float CragEdgeRise = 0.6f;
        [Range(0f, 1f), Tooltip("Flat ledges at the strata heights of the walls.")]
        public float CragLedges = 0.35f;
        [Range(0f, 1f), Tooltip("Share of the crag height that holds soil in the low hollows of the top. The soil " +
                                "takes the terrain ground.")]
        public float TopSoil = 0.04f;
        [Min(0.25f), Tooltip("Distance between the grid points of the top, in meters. Tall, sharp crags need a fine " +
                             "grid.")]
        public float TopSpacing = 0.5f;
        [Tooltip("The top gets a collider, so characters walk on the rock and not on the terrain below it.")]
        public bool TopCollider = true;
        [Tooltip("The rock gets colliders. Turn it off for rock that nothing can reach, for example far outside the " +
                 "playable area.")]
        public bool Colliders = true;

        [Header("Blocks")]
        [Tooltip("Rock blocks with fissures and bedding joints on the face.")]
        public RockFaceBlocks Blocks = new();
        [Tooltip("The face is built from separate rock blocks, as the walls of a block formation, in place of a skin. " +
                 "The blocks stand from below the foot to above the top edge.")]
        public bool BlockWalls;
        [Tooltip("Shape of the blocks when the face is built from separate rock blocks.")]
        public RockBlockShape WallBlocks = new();

        [Header("Variation")]
        public int Seed = 1;

        /// <summary>A copy for a build on a worker thread. All settings are values.</summary>
        public RockFaceSettings Copy()
        {
            var copy = (RockFaceSettings)MemberwiseClone();
            copy.Blocks = Blocks.Copy();
            copy.WallBlocks = WallBlocks.Copy();
            copy.ExcludedRanges = ExcludedRanges != null ? (Vector2[])ExcludedRanges.Clone() : new Vector2[0];
            return copy;
        }
    }
}
