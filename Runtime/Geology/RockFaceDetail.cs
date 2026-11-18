using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    // Surface data for the rock shader. All values are 0 to 1.
    internal struct RockFaceSample
    {
        public float Hardness;
        public float Cavity;
        public float Layer;
    }

    // Layered rock rules in cliff space: u is the distance along the cliff, y is the world height.
    // Each rule gives an outward offset in meters. The sum is clamped to zero or more.
    internal sealed class RockFaceDetail
    {
        private const float UndersideBevel = 0.15f;
        private const float SpallEdge = 0.12f;
        private const float FaultWidth = 0.6f;
        private const int FractureOctaves = 3;
        private const int RidgedOctaves = 4;
        // Share of each terrace taken by its steep riser.
        private const float TerraceRiser = 0.3f;
        private const float CrackWidth = 0.25f;

        private readonly RockFaceSettings _settings;
        private readonly float[] _bandBottoms;
        private readonly float[] _bandHardness;
        private readonly float[] _joints;
        private readonly float[] _faults;
        private readonly float _dipSlope;
        private readonly float _noiseOffset;

        public RockFaceDetail(RockFaceSettings settings, float uMinimum, float uMaximum,
            float yMinimum, float yMaximum)
        {
            _settings = settings;
            _dipSlope = Mathf.Tan(settings.StrataDip * Mathf.Deg2Rad);
            _noiseOffset = 100f + Hash(settings.Seed, 7, 11) * 900f;

            // Dip and warp move the layers, so the band list covers the extra height.
            float uExtent = Mathf.Max(Mathf.Abs(uMinimum), Mathf.Abs(uMaximum));
            float faultTilt = Mathf.Tan(settings.FaultTilt * Mathf.Deg2Rad) * settings.FaultSpacing.y;
            float margin = settings.StrataWarp + Mathf.Abs(_dipSlope) * uExtent + settings.FaultThrow +
                           faultTilt + settings.PinchDepth + 10f;
            BuildBands(yMinimum - margin, yMaximum + margin, out _bandBottoms, out _bandHardness);
            _joints = BuildJoints(uMinimum - 20f, uMaximum + 20f, yMaximum);
            _faults = BuildFaults(uMinimum - 60f, uMaximum + 60f);
        }

        // Structure scales the strata, joint, block and spall rules. Mass and roughness always apply.
        public float Evaluate(float u, float y, float structure, out RockFaceSample sample)
        {
            RockFaceSettings s = _settings;
            // Buttresses are tall, so the mass noise changes slowly with height.
            float mass = s.MassDepth * Fractal(u / s.MassScale, y / (s.MassScale * 3f), 3.1f);

            // A small domain warp keeps layer and joint lines from being straight.
            float warpedU = u + s.DomainWarp * (2f * Noise(u / 9f, y / 9f + 50f) - 1f);
            float warpedY = y + 0.5f * s.DomainWarp * (2f * Noise(u / 9f + 70f, y / 9f) - 1f);
            float strataY = warpedY + u * _dipSlope + s.StrataWarp * Signed(u / 40f, 5.7f) + FaultShift(u);
            // A height-dependent warp makes layers thicken and thin along the cliff.
            strataY += s.PinchDepth * (2f * Noise(u / 25f + 31.7f, strataY / 6f) - 1f);
            int band = FindBand(strataY, out float bandHeight, out float thickness);
            // Ledges come and go along the cliff, so a hard layer does not form one continuous ring.
            float continuity = Smooth01((Noise(u / s.StrataContinuity, band * 3.7f) - 0.25f) / 0.5f);
            float hardness = _bandHardness[band] * continuity;

            // A hard layer has a sharp underside, which overhangs the soft layer below,
            // and a weathered top that slopes back.
            float topBevel = Mathf.Min(0.5f * thickness, s.StrataBevel);
            float underside = Smooth01(bandHeight / UndersideBevel);
            float top = 1f - Smooth01((bandHeight - (thickness - topBevel)) / topBevel);
            float strata = s.StrataDepth * hardness * underside * Mathf.Lerp(0.25f, 1f, top);

            float jointU = warpedU + s.JointLean * y + 2f * Signed(y / 12f, 17.1f);
            int cell = FindCell(jointU, out int nearestJoint, out float jointDistance);
            float joint = IsJointOpen(nearestJoint, band)
                ? s.JointDepth * (1f - Smooth01(jointDistance / s.JointWidth))
                : 0f;

            // A block reaches from one open joint to the next, so closed joints leave no step.
            int block = cell;
            while (!IsJointOpen(block, band) && cell - block < 4) block--;
            float blockOffset = s.BlockRelief * Hash(block, band, 2 + s.Seed) * Mathf.Lerp(0.4f, 1f, hardness);

            float spall = 0f;
            if (Hash(block, band, 3 + s.Seed) < s.SpallChance)
            {
                float cut = Mathf.Lerp(0.2f, 0.7f, Hash(block, band, 4 + s.Seed)) * thickness;
                spall = s.SpallDepth * Mathf.Lerp(0.5f, 1f, Hash(block, band, 5 + s.Seed)) *
                        Smooth01((bandHeight - cut) / SpallEdge) * Smooth01(jointDistance / SpallEdge);
            }

            float grooveU = warpedU + 0.8f * Signed(y / 7f, 23.3f);
            float ridge = 1f - Mathf.Abs(2f * Noise(grooveU / s.GrooveSpacing, y / 25f + 11.7f) - 1f);
            // Grooves form where water runs off the rim, so they come in patches along the cliff.
            float runoff = Smooth01((Noise(u / 18f, 91.3f) - 0.35f) / 0.3f);
            float groove = s.GrooveDepth * runoff * ridge * ridge * ridge * Mathf.Lerp(1f, 0.4f, hardness);

            float rough = s.RoughnessDepth * Fractal(u / s.RoughnessScale, y / s.RoughnessScale, 41.3f);

            float crack = 0f;
            float fracture = s.FractureDepth > 0f ? Fracture(warpedU, warpedY, out crack) : 0f;
            float ridged = 0f;
            if (s.RidgedDepth > 0f)
            {
                float along = Ridged(u / (s.RidgedScale * s.RidgedStretch), strataY / s.RidgedScale, 0f);
                // The cross set runs up the face: its features are stretched along the height.
                float across = s.RidgedCross > 0f
                    ? Ridged(u / s.RidgedScale, y / (s.RidgedScale * s.RidgedStretch), 53.7f)
                    : 0f;
                float value = Mathf.Max(along, s.RidgedCross * across);
                ridged = s.RidgedDepth * Mathf.Lerp(s.RidgedSoftShare, 1f, hardness) * Terrace(value);
            }

            sample.Hardness = hardness;
            sample.Layer = Mathf.Clamp01(Hash(band, 83, s.Seed) + 0.6f * (Noise(u / 30f, band * 1.3f + 60f) - 0.5f));
            sample.Cavity = Mathf.Clamp01(0.9f * structure * joint / Mathf.Max(s.JointDepth, 0.01f) +
                                          0.7f * structure * spall / Mathf.Max(s.SpallDepth, 0.01f) +
                                          0.6f * groove / Mathf.Max(s.GrooveDepth, 0.01f) + 0.3f * (1f - hardness) +
                                          0.8f * structure * crack / Mathf.Max(s.CrackDepth, 0.01f));
            float layered = structure * (strata + blockOffset - joint - spall + fracture - crack + ridged);
            return Mathf.Max(0f, mass + layered + rough - groove);
        }

        // Ridged multifractal (Musgrave): each octave is folded at zero into sharp creases, and each finer octave is
        // weighted by the coarser signal, so the fine detail gathers along the ridges and the hollows stay calm. The
        // input is warped by smooth noise, and the features are stretched along the layers. 0 to 1.
        private float Ridged(float x, float y, float phase)
        {
            RockFaceSettings s = _settings;
            float warp = s.RidgedWarp;
            float warpedX = x + warp * Signed(x * 0.5f + 17.3f + phase, y * 0.5f + 5.1f);
            float warpedY = y + warp * Signed(x * 0.5f + 61.7f + phase, y * 0.5f + 29.9f);
            float sum = 0f;
            float total = 0f;
            float weight = 1f;
            float amplitude = 1f;
            for (int octave = 0; octave < RidgedOctaves; octave++)
            {
                float signal = 1f - Mathf.Abs(2f * Noise(warpedX + octave * 37.1f + phase, warpedY + octave * 11.3f) - 1f);
                signal = Mathf.Pow(signal, s.RidgedSharpness) * weight;
                weight = Mathf.Clamp01(signal * 2f);
                sum += signal * amplitude;
                total += amplitude;
                amplitude *= 0.5f;
                warpedX *= 2.07f;
                warpedY *= 2.07f;
            }
            return sum / total;
        }

        // Terraces of the ridged detail: flat treads and steep risers, each riser a sharp edge.
        private float Terrace(float value)
        {
            int steps = _settings.RidgedTerraces;
            if (steps <= 0) return value;
            float scaled = value * steps;
            float step = Mathf.Floor(scaled);
            float rise = Smooth01((scaled - step - (1f - TerraceRiser)) / TerraceRiser);
            return (step + rise) / steps;
        }

        // Fracture faces: Voronoi cells at three sizes, each a flat face with its own outward step and tilt, so the
        // rock breaks into angular planes. The steps between cells are the crisp edges. A crack follows each cell edge.
        private float Fracture(float u, float y, out float crack)
        {
            RockFaceSettings s = _settings;
            float offset = 0f;
            crack = 0f;
            float size = s.FractureSize;
            float depth = s.FractureDepth;
            float crackDepth = s.CrackDepth;
            for (int octave = 0; octave < FractureOctaves; octave++)
            {
                float cellU = u / size;
                float cellY = y / size;
                int baseU = Mathf.FloorToInt(cellU);
                int baseY = Mathf.FloorToInt(cellY);
                float nearest = float.MaxValue;
                float second = float.MaxValue;
                int nearestU = 0;
                int nearestY = 0;
                float pointU = 0f;
                float pointY = 0f;
                int seed = s.Seed * 7 + octave * 131;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int du = -1; du <= 1; du++)
                    {
                        int cu = baseU + du;
                        int cy = baseY + dy;
                        float pu = cu + 0.1f + 0.8f * Hash(cu, cy, seed + 1);
                        float py = cy + 0.1f + 0.8f * Hash(cu, cy, seed + 2);
                        float distance = (pu - cellU) * (pu - cellU) + (py - cellY) * (py - cellY);
                        if (distance < nearest)
                        {
                            second = nearest;
                            nearest = distance;
                            nearestU = cu;
                            nearestY = cy;
                            pointU = pu;
                            pointY = py;
                        }
                        else if (distance < second)
                        {
                            second = distance;
                        }
                    }
                }
                // A flat face: a step for the cell and a tilt across it.
                float tiltU = (2f * Hash(nearestU, nearestY, seed + 3) - 1f) * s.FractureTilt;
                float tiltY = (2f * Hash(nearestU, nearestY, seed + 4) - 1f) * s.FractureTilt;
                offset += depth * (Hash(nearestU, nearestY, seed + 5) + tiltU * (cellU - pointU) + tiltY * (cellY - pointY));
                // Distance to the cell edge in meters, from the two nearest feature points.
                float edge = 0.5f * (Mathf.Sqrt(second) - Mathf.Sqrt(nearest)) * size;
                crack = Mathf.Max(crack, crackDepth * (1f - Smooth01(edge / CrackWidth)));
                size /= 3f;
                depth *= 0.5f;
                crackDepth *= 0.6f;
            }
            return offset;
        }

        #region Layout
        private void BuildBands(float bottom, float top, out float[] bottoms, out float[] hardness)
        {
            List<float> bandBottoms = new();
            List<float> bandHardness = new();
            float height = bottom;
            int index = 0;
            while (height < top)
            {
                bandBottoms.Add(height);
                bool hard = Hash(index, 31, _settings.Seed) < _settings.HardShare;
                float strength = Hash(index, 37, _settings.Seed);
                bandHardness.Add(hard ? Mathf.Lerp(0.6f, 1f, strength) : Mathf.Lerp(0f, 0.25f, strength));
                height += Mathf.Lerp(_settings.StrataThickness.x, _settings.StrataThickness.y,
                    Hash(index, 41, _settings.Seed));
                index++;
            }
            bandBottoms.Add(height);
            bottoms = bandBottoms.ToArray();
            hardness = bandHardness.ToArray();
        }

        private float[] BuildJoints(float start, float end, float yMaximum)
        {
            // Lean moves joint positions by up to lean x height, so the list covers that range too.
            float lean = Mathf.Abs(_settings.JointLean) * Mathf.Max(Mathf.Abs(yMaximum), 1f) + 5f;
            List<float> joints = new();
            float position = start - lean;
            int index = 0;
            while (position < end + lean)
            {
                joints.Add(position);
                position += Mathf.Lerp(_settings.JointSpacing.x, _settings.JointSpacing.y,
                    Hash(index, 53, _settings.Seed));
                index++;
            }
            joints.Add(position);
            return joints.ToArray();
        }

        private float[] BuildFaults(float start, float end)
        {
            List<float> faults = new();
            float position = start;
            int index = 0;
            while (position < end)
            {
                faults.Add(position);
                position += Mathf.Lerp(_settings.FaultSpacing.x, Mathf.Max(_settings.FaultSpacing.x, _settings.FaultSpacing.y),
                    Hash(index, 89, _settings.Seed));
                index++;
            }
            faults.Add(position);
            return faults.ToArray();
        }

        // Each fault block shifts and tilts its layers. The change is blended over a narrow zone at each fault.
        private float FaultShift(float u)
        {
            if (_settings.FaultThrow <= 0f && _settings.FaultTilt <= 0f) return 0f;
            int index = Array.BinarySearch(_faults, u);
            if (index < 0) index = ~index - 1;
            index = Mathf.Clamp(index, 1, _faults.Length - 3);
            float left = _faults[index];
            float right = _faults[index + 1];
            float shift = BlockShift(index, u);
            if (u - left < FaultWidth * 0.5f)
            {
                return Mathf.Lerp(BlockShift(index - 1, u), shift, Smooth01((u - left) / FaultWidth + 0.5f));
            }
            if (right - u < FaultWidth * 0.5f)
            {
                return Mathf.Lerp(shift, BlockShift(index + 1, u), Smooth01((u - right) / FaultWidth + 0.5f));
            }
            return shift;
        }

        private float BlockShift(int block, float u)
        {
            float center = 0.5f * (_faults[block] + _faults[block + 1]);
            float tilt = Mathf.Tan((2f * Hash(block, 103, _settings.Seed) - 1f) * _settings.FaultTilt * Mathf.Deg2Rad);
            return (2f * Hash(block, 101, _settings.Seed) - 1f) * _settings.FaultThrow + (u - center) * tilt;
        }

        private int FindBand(float height, out float bandHeight, out float thickness)
        {
            int index = Array.BinarySearch(_bandBottoms, height);
            if (index < 0) index = ~index - 1;
            index = Mathf.Clamp(index, 0, _bandHardness.Length - 1);
            thickness = _bandBottoms[index + 1] - _bandBottoms[index];
            bandHeight = Mathf.Clamp(height - _bandBottoms[index], 0f, thickness);
            return index;
        }

        private int FindCell(float position, out int nearestJoint, out float jointDistance)
        {
            int index = Array.BinarySearch(_joints, position);
            if (index < 0) index = ~index - 1;
            index = Mathf.Clamp(index, 0, _joints.Length - 2);
            float left = Mathf.Abs(position - _joints[index]);
            float right = Mathf.Abs(_joints[index + 1] - position);
            nearestJoint = left <= right ? index : index + 1;
            jointDistance = Mathf.Min(left, right);
            return index;
        }

        // A joint stays open or closed through groups of about three layers, so joints have different lengths.
        private bool IsJointOpen(int joint, int band)
        {
            int group = (band + (int)(Hash(joint, 71, _settings.Seed) * 3f)) / 3;
            return Hash(joint, group, 73 + _settings.Seed) < _settings.JointPersistence;
        }
        #endregion

        #region Noise
        private float Noise(float x, float y)
        {
            return RockFaceNoise.Perlin(x + _noiseOffset, y + _noiseOffset * 0.61f);
        }

        private float Signed(float x, float phase)
        {
            return 2f * Noise(x, phase) - 1f;
        }

        private float Fractal(float x, float y, float phase)
        {
            float first = Noise(x + phase, y);
            float second = Noise(x * 2.03f + phase * 1.7f, y * 2.03f);
            return Mathf.Clamp01(first * 0.67f + second * 0.33f);
        }

        private static float Smooth01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        private static float Hash(int a, int b, int c)
        {
            unchecked
            {
                uint hash = (uint)a * 0x8da6b343u ^ (uint)b * 0xd8163841u ^ (uint)c * 0xcb1ab31fu;
                hash ^= hash >> 13;
                hash *= 0x5bd1e995u;
                hash ^= hash >> 15;
                return (hash & 0xFFFFFFu) / 16777216f;
            }
        }
        #endregion
    }
}
