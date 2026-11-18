using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// The arrival field of the waves that come to the shore. R is an arrival time in seconds: the lines of equal time
    /// are the wave crests, and a crest moves to smaller times. A wave in shallow water moves at sqrt(g depth), so the
    /// crests turn toward the shore, wrap around headlands and come closer together in shallow water. Without swell, R is
    /// the time that a wave needs from a texel to the water edge, so the crests come straight to every shore, as on a
    /// lake. With swell, the waves come in from the upwind edge of the texture in one direction: offshore their crests
    /// are straight lines, and only shallow water turns them. G and B are the gradient of the time in the direction of
    /// the waves (its length is 1 / wave speed, in seconds per metre). A is 1 where the waves of one front arrive and
    /// falls to 0 where two fronts meet, so the shader has no crease there. With swell, A is also lower where the waves
    /// turned far from the swell direction, for example on a shore that faces away from it. The field comes from the
    /// shore texture (R distance, G depth), so it is built again after a script reload. Built from lines, R is the time
    /// that a wave needs from the lines to a texel instead (the waves that run out from the plunge of a fall), and GB
    /// points back toward the lines.
    /// </summary>
    internal sealed class WaterShoreWaveField
    {
        private const float Gravity = 9.81f;
        private const float Unreached = 1e9f;
        // Depth used for the wave speed in very shallow water, in metres.
        private const float MinimumDepth = 0.2f;
        // Rounds of the four sweep orders of the eikonal solver.
        private const int SweepRounds = 3;
        // Radius of the neighbourhood for the gradient and the agreement of directions, in texels.
        private const int GradientRadius = 1;
        private const int AgreementRadius = 2;
        // Agreement of the directions (mean length of unit vectors) where the fade starts and ends.
        private const float AgreementLow = 0.55f;
        private const float AgreementHigh = 0.9f;
        // Dry texels next to the water that get the field of their wet neighbours. The ocean near grid draws over a
        // dry margin, and the bilinear filter of the edge texels reads the dry texels.
        private const int DryMarginTexels = 3;
        // Swell: smallest share of the swell direction across a texture side that makes the side an upwind side.
        private const float UpwindShare = 0.05f;
        // Swell: wave height share where the waves turned against the swell, and the cosines of the turn where the
        // shelter starts and ends. A wave that turned 90 degrees keeps about 40 % of its height.
        private const float ShelteredHeight = 0.3f;
        private const float ShelterStart = -0.2f;
        private const float ShelterEnd = 0.6f;

        private readonly string _name;
        private float[] _time;
        // Built from lines: the distance along the lines of the line point that the wave of a texel came from, in
        // metres. Null for the shore waves.
        private float[] _along;
        private Color[] _pixels;
        private Texture2D _texture;
        private Texture2D _alongTexture;

        public WaterShoreWaveField(string name = "Looga Water Shore Waves")
        {
            _name = name;
        }

        public Texture2D Texture => _texture;
        /// <summary>Built from lines: R is the distance along the lines where the wave of a texel started.</summary>
        public Texture2D AlongTexture => _alongTexture;
        public float BuildMilliseconds { get; private set; }

        /// <summary>
        /// Builds the field from the shore texture. The deep water speed is the speed of a wave with the period, so
        /// deep water does not give waves that move too fast.
        /// </summary>
        /// <param name="swell">Unit direction in world XZ that the swell moves toward, or zero for waves that come
        /// straight to every shore. When no upwind texel is water, the field uses the waves without swell.</param>
        public void Build(Texture2D shore, float texel, float period, Vector2 swell)
        {
            float start = Time.realtimeSinceStartup;
            int width = shore.width;
            int height = shore.height;
            int count = width * height;
            Color[] source = shore.GetPixels();
            float deepSpeed = Gravity * Mathf.Max(period, 1f) / (2f * Mathf.PI);
            var slowness = new float[count];
            if (_time == null || _time.Length != count)
            {
                _time = new float[count];
                _pixels = new Color[count];
            }

            // Wet texels near the edge start with the time over their distance to the edge. Dry texels are not part
            // of the field.
            for (int index = 0; index < count; index++)
            {
                float distance = source[index].r;
                float depth = source[index].g;
                if (distance <= 0f)
                {
                    slowness[index] = -1f;
                    _time[index] = Unreached;
                    continue;
                }
                float speed = Mathf.Min(Mathf.Sqrt(Gravity * Mathf.Max(depth, MinimumDepth)), deepSpeed);
                slowness[index] = 1f / speed;
                _time[index] = distance < texel * 1.5f ? distance * slowness[index] : Unreached;
            }
            bool swellMode = swell.sqrMagnitude > 0.25f && SeedSwell(slowness, width, height, texel, swell.normalized,
                1f / deepSpeed);
            _along = null;
            Solve(slowness, width, height, texel);
            if (swellMode) InvertTime(slowness);
            WritePixels(slowness, width, height, texel, swellMode ? swell.normalized : Vector2.zero);
            ExtendIntoDryMargin(slowness, width, height);
            Upload(width, height);
            BuildMilliseconds = (Time.realtimeSinceStartup - start) * 1000f;
        }

        /// <summary>
        /// Builds the field of the waves that run out from lines on the water, such as the plunge lines of falls. The
        /// time starts at 0 on the lines and grows outward, so a crest moves to larger times. Returns false, and
        /// releases the texture, when no line is on a wet texel.
        /// </summary>
        /// <param name="origin">World XZ of the shore texture corner.</param>
        /// <param name="lines">World XZ segments: x, y = start, z, w = end.</param>
        public bool BuildFromLines(Texture2D shore, float texel, Vector2 origin, float period,
            IReadOnlyList<Vector4> lines)
        {
            float start = Time.realtimeSinceStartup;
            int width = shore.width;
            int height = shore.height;
            int count = width * height;
            Color[] source = shore.GetPixels();
            float deepSpeed = Gravity * Mathf.Max(period, 0.5f) / (2f * Mathf.PI);
            var slowness = new float[count];
            if (_time == null || _time.Length != count)
            {
                _time = new float[count];
                _pixels = new Color[count];
            }
            for (int index = 0; index < count; index++)
            {
                _time[index] = Unreached;
                if (source[index].r <= 0f)
                {
                    slowness[index] = -1f;
                    continue;
                }
                float speed = Mathf.Min(Mathf.Sqrt(Gravity * Mathf.Max(source[index].g, MinimumDepth)), deepSpeed);
                slowness[index] = 1f / speed;
            }

            // Wet texels near a line start with the time over their distance to the line, and with the distance along
            // the lines to their nearest line point. The lines of one fall follow each other, so the distance runs on
            // over their joints.
            if (_along == null || _along.Length != count) _along = new float[count];
            bool seeded = false;
            float seedReach = texel * 1.5f;
            float run = 0f;
            foreach (Vector4 line in lines)
            {
                var a = new Vector2(line.x, line.y);
                var b = new Vector2(line.z, line.w);
                float length = Vector2.Distance(a, b);
                int minColumn = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - origin.x) / texel) - 2);
                int maxColumn = Mathf.Min(width - 1, Mathf.CeilToInt((Mathf.Max(a.x, b.x) - origin.x) / texel) + 2);
                int minRow = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, b.y) - origin.y) / texel) - 2);
                int maxRow = Mathf.Min(height - 1, Mathf.CeilToInt((Mathf.Max(a.y, b.y) - origin.y) / texel) + 2);
                for (int row = minRow; row <= maxRow; row++)
                {
                    for (int column = minColumn; column <= maxColumn; column++)
                    {
                        int index = row * width + column;
                        if (slowness[index] < 0f) continue;

                        var centre = new Vector2(origin.x + (column + 0.5f) * texel, origin.y + (row + 0.5f) * texel);
                        float share = ProjectOnSegment(centre, a, b);
                        float distance = Vector2.Distance(centre, Vector2.Lerp(a, b, share));
                        float time = distance * slowness[index];
                        if (distance > seedReach || time >= _time[index]) continue;

                        _time[index] = time;
                        _along[index] = run + share * length;
                        seeded = true;
                    }
                }
                run += length;
            }
            if (!seeded)
            {
                Release();
                return false;
            }
            Solve(slowness, width, height, texel);
            WritePixels(slowness, width, height, texel, Vector2.zero);
            ExtendIntoDryMargin(slowness, width, height);
            Upload(width, height);
            UploadAlong(width, height);
            BuildMilliseconds = (Time.realtimeSinceStartup - start) * 1000f;
            return true;
        }

        // Share of the segment from a to b at the point nearest to the point, from 0 to 1.
        private static float ProjectOnSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            return ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector2.Dot(point - a, ab) / ab.sqrMagnitude) : 0f;
        }

        private void UploadAlong(int width, int height)
        {
            if (!_alongTexture || _alongTexture.width != width || _alongTexture.height != height)
            {
                ReleaseAlong();
                _alongTexture = new Texture2D(width, height, TextureFormat.RFloat, false, true)
                {
                    name = _name + " Along", hideFlags = HideFlags.DontSave,
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
                };
            }
            _alongTexture.SetPixelData(_along, 0);
            _alongTexture.Apply(false, false);
        }

        private void ReleaseAlong()
        {
            if (!_alongTexture) return;

            if (Application.isPlaying)
            {
                Object.Destroy(_alongTexture);
            }
            else
            {
                Object.DestroyImmediate(_alongTexture);
            }
            _alongTexture = null;
        }

        private void Upload(int width, int height)
        {
            if (!_texture || _texture.width != width || _texture.height != height)
            {
                Release();
                _texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
                {
                    name = _name, hideFlags = HideFlags.DontSave,
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
                };
            }
            _texture.SetPixels(_pixels);
            _texture.Apply(false, false);
        }

        /// <summary>Takes a built field texture, for example from a shipped file. Not for a field built from lines.</summary>
        public void Adopt(Texture2D texture)
        {
            Release();
            _texture = texture;
        }

        /// <summary>Destroys the texture.</summary>
        public void Release()
        {
            ReleaseAlong();
            if (!_texture) return;

            if (Application.isPlaying)
            {
                Object.Destroy(_texture);
            }
            else
            {
                Object.DestroyImmediate(_texture);
            }
            _texture = null;
        }

        // Swell comes in through the upwind sides of the texture as straight crests: the water texels on those sides
        // start with the deep water time along the swell direction. Returns false, and keeps the start at the water
        // edge, when no upwind texel is water.
        private bool SeedSwell(float[] slowness, int width, int height, float texel, Vector2 swell, float deepSlowness)
        {
            bool fromWest = swell.x > UpwindShare;
            bool fromEast = swell.x < -UpwindShare;
            bool fromSouth = swell.y > UpwindShare;
            bool fromNorth = swell.y < -UpwindShare;
            bool found = false;
            for (int pass = 0; pass < 2; pass++)
            {
                // The first pass only looks for an upwind water texel. The second pass replaces the start.
                if (pass == 1)
                {
                    if (!found) return false;
                    for (int index = 0; index < _time.Length; index++) _time[index] = Unreached;
                }
                for (int row = 0; row < height; row++)
                {
                    for (int column = 0; column < width; column++)
                    {
                        bool upwind = (fromWest && column == 0) || (fromEast && column == width - 1) ||
                            (fromSouth && row == 0) || (fromNorth && row == height - 1);
                        int index = row * width + column;
                        if (!upwind || slowness[index] < 0f) continue;

                        found = true;
                        if (pass == 1)
                        {
                            var position = new Vector2((column + 0.5f) * texel, (row + 0.5f) * texel);
                            _time[index] = Vector2.Dot(position, swell) * deepSlowness;
                        }
                    }
                }
            }
            return true;
        }

        // The swell solve gives the time since the waves came in, which grows in the direction of the waves. The
        // shader moves a crest to smaller times, so the field stores the latest time minus the time.
        private void InvertTime(float[] slowness)
        {
            float latest = 0f;
            for (int index = 0; index < _time.Length; index++)
            {
                if (slowness[index] >= 0f && _time[index] < Unreached) latest = Mathf.Max(latest, _time[index]);
            }
            for (int index = 0; index < _time.Length; index++)
            {
                if (slowness[index] >= 0f && _time[index] < Unreached) _time[index] = latest - _time[index];
            }
        }

        // Fast sweeping solver of the eikonal equation |grad T| = slowness (Zhao 2005): Gauss-Seidel passes in the four
        // diagonal orders, each with the upwind Godunov update. The arrival time is isotropic, so the crests are round
        // around a point and do not take the octagon shape of a graph search.
        private void Solve(float[] slowness, int width, int height, float texel)
        {
            for (int round = 0; round < SweepRounds; round++)
            {
                for (int order = 0; order < 4; order++)
                {
                    bool forwardX = (order & 1) == 0;
                    bool forwardY = (order & 2) == 0;
                    for (int step = 0; step < height; step++)
                    {
                        int row = forwardY ? step : height - 1 - step;
                        for (int stepX = 0; stepX < width; stepX++)
                        {
                            int column = forwardX ? stepX : width - 1 - stepX;
                            int index = row * width + column;
                            float cost = slowness[index];
                            if (cost < 0f) continue;

                            float a = Mathf.Min(column > 0 ? _time[index - 1] : Unreached,
                                column < width - 1 ? _time[index + 1] : Unreached);
                            float b = Mathf.Min(row > 0 ? _time[index - width] : Unreached,
                                row < height - 1 ? _time[index + width] : Unreached);
                            if (a >= Unreached && b >= Unreached) continue;

                            float h = cost * texel;
                            float candidate;
                            if (Mathf.Abs(a - b) >= h)
                            {
                                candidate = Mathf.Min(a, b) + h;
                            }
                            else
                            {
                                candidate = 0.5f * (a + b + Mathf.Sqrt(2f * h * h - (a - b) * (a - b)));
                            }
                            if (candidate >= _time[index]) continue;

                            _time[index] = candidate;
                            // The texel takes the start point of the upwind neighbours that its time came from.
                            if (_along != null) _along[index] = UpwindAlong(index, column, row, width, a, b, candidate);
                        }
                    }
                }
            }
        }

        // The along distance of the upwind neighbours: of the earlier one in x and in y, weighted by how much each one
        // is earlier than the texel. A neighbour that is not earlier has no weight.
        private float UpwindAlong(int index, int column, int row, int width, float a, float b, float time)
        {
            float weightX = a < Unreached ? Mathf.Max(time - a, 0f) : 0f;
            float weightY = b < Unreached ? Mathf.Max(time - b, 0f) : 0f;
            float alongX = weightX > 0f ? _along[column > 0 && _time[index - 1] == a ? index - 1 : index + 1] : 0f;
            float alongY = weightY > 0f ? _along[row > 0 && _time[index - width] == b ? index - width : index + width] : 0f;
            float sum = weightX + weightY;
            return sum > 1e-9f ? (alongX * weightX + alongY * weightY) / sum : _along[index];
        }

        // Gives each dry texel within the margin the mean field of its filled neighbours, one ring per pass. Without
        // it the swash of the shore foam stops where a dry texel is near the real water edge.
        private void ExtendIntoDryMargin(float[] slowness, int width, int height)
        {
            int count = width * height;
            var filled = new bool[count];
            for (int index = 0; index < count; index++)
            {
                filled[index] = slowness[index] >= 0f && _time[index] < Unreached;
            }
            var ring = new System.Collections.Generic.List<int>();
            for (int pass = 0; pass < DryMarginTexels; pass++)
            {
                ring.Clear();
                for (int row = 0; row < height; row++)
                {
                    for (int column = 0; column < width; column++)
                    {
                        int index = row * width + column;
                        if (filled[index]) continue;

                        Color sum = default;
                        int samples = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int x = column + dx;
                                int y = row + dy;
                                if (x < 0 || y < 0 || x >= width || y >= height || !filled[y * width + x]) continue;
                                sum += _pixels[y * width + x];
                                samples++;
                            }
                        }
                        if (samples == 0) continue;
                        _pixels[index] = sum / samples;
                        ring.Add(index);
                    }
                }
                foreach (int index in ring) filled[index] = true;
            }
        }

        // Writes the arrival time, the gradient in the direction of the waves and the agreement fade. Texels without a time get
        // zeros, so the shader has no waves there. With swell (a unit direction, else zero), waves that turned far from
        // the swell direction are lower: their energy spread out on the way around a headland or into a sheltered cove.
        private void WritePixels(float[] slowness, int width, int height, float texel, Vector2 swell)
        {
            var directions = new Vector2[width * height];
            var gradients = new Vector2[width * height];
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (slowness[index] < 0f || _time[index] >= Unreached) continue;

                    // Central differences over the neighbourhood. Missing neighbours use the centre, which gives a
                    // one-sided difference at the edge.
                    Vector2 sum = Vector2.zero;
                    float weight = 0f;
                    for (int dy = -GradientRadius; dy <= GradientRadius; dy++)
                    {
                        for (int dx = -GradientRadius; dx <= GradientRadius; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int x = Mathf.Clamp(column + dx, 0, width - 1);
                            int y = Mathf.Clamp(row + dy, 0, height - 1);
                            float other = _time[y * width + x];
                            if (other >= Unreached) continue;
                            var offset = new Vector2(x - column, y - row);
                            if (offset == Vector2.zero) continue;
                            float length = offset.magnitude;
                            sum += offset / length * ((other - _time[index]) / (length * texel));
                            weight += 1f;
                        }
                    }
                    if (weight <= 0f) continue;
                    // The mean of the directional slopes is half the gradient in two dimensions.
                    Vector2 gradient = sum / weight * 2f;
                    gradients[index] = -gradient;
                    directions[index] = gradient.sqrMagnitude > 1e-12f ? -gradient.normalized : Vector2.zero;
                }
            }

            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (slowness[index] < 0f || _time[index] >= Unreached)
                    {
                        _pixels[index] = new Color(0f, 0f, 0f, 0f);
                        continue;
                    }
                    // Where two wave fronts meet, the directions around the texel point different ways and their mean
                    // gets short.
                    Vector2 mean = Vector2.zero;
                    int samples = 0;
                    for (int dy = -AgreementRadius; dy <= AgreementRadius; dy++)
                    {
                        for (int dx = -AgreementRadius; dx <= AgreementRadius; dx++)
                        {
                            int x = column + dx;
                            int y = row + dy;
                            if (x < 0 || y < 0 || x >= width || y >= height) continue;
                            Vector2 direction = directions[y * width + x];
                            if (direction == Vector2.zero) continue;
                            mean += direction;
                            samples++;
                        }
                    }
                    float agreement = samples > 0 ? mean.magnitude / samples : 0f;
                    float fade = Mathf.SmoothStep(0f, 1f,
                        Mathf.InverseLerp(AgreementLow, AgreementHigh, agreement));
                    if (swell != Vector2.zero)
                    {
                        float turn = Vector2.Dot(directions[index], swell);
                        fade *= Mathf.Lerp(ShelteredHeight, 1f,
                            Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ShelterStart, ShelterEnd, turn)));
                    }
                    Vector2 toShore = gradients[index];
                    _pixels[index] = new Color(_time[index], toShore.x, toShore.y, fade);
                }
            }
        }
    }
}
