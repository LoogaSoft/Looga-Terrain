using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    // Thread-safe 2D gradient noise in [0, 1], so rock face builds can run on worker threads.
    internal static class RockFaceNoise
    {
        public static float Perlin(float x, float y)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float xf = x - xi;
            float yf = y - yi;
            float u = Fade(xf);
            float v = Fade(yf);
            float a = Gradient(Hash(xi, yi), xf, yf);
            float b = Gradient(Hash(xi + 1, yi), xf - 1f, yf);
            float c = Gradient(Hash(xi, yi + 1), xf, yf - 1f);
            float d = Gradient(Hash(xi + 1, yi + 1), xf - 1f, yf - 1f);
            float value = Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
            // The raw range is about -0.7 to 0.7.
            return Mathf.Clamp01(value * 0.7071f + 0.5f);
        }

        // Smooth noise from -1 to 1 in world space, from three planes, so walls and tops get the same noise.
        public static float Surface(Vector3 position, float scale)
        {
            float sum = 0f;
            float amplitude = 1f;
            float total = 0f;
            for (int octave = 0; octave < 2; octave++)
            {
                float x = position.x / scale;
                float y = position.y / scale;
                float z = position.z / scale;
                float value = Perlin(x + 11.3f, y + 3.7f) + Perlin(z + 57.1f, y + 29.3f) +
                              Perlin(x + 91.7f, z + 13.9f);
                sum += amplitude * (value / 1.5f - 1f);
                total += amplitude;
                amplitude *= 0.45f;
                scale /= 2.3f;
            }
            return Mathf.Clamp(1.6f * sum / total, -1f, 1f);
        }

        // Vertical grooves from -1 to 1: noise stretched along the height, projected on the wall from the X and Z sides.
        public static float Flutes(Vector3 position, Vector3 normal, float spacing)
        {
            float weightX = Mathf.Abs(normal.x);
            float weightZ = Mathf.Abs(normal.z);
            float total = weightX + weightZ;
            if (total < 1e-4f) return 0f;
            float y = position.y / (spacing * 6f);
            float alongX = 1f - Mathf.Abs(2f * Perlin(position.z / spacing + 41.3f, y + 5.9f) - 1f);
            float alongZ = 1f - Mathf.Abs(2f * Perlin(position.x / spacing + 73.1f, y + 17.3f) - 1f);
            // A ridge of the noise is a rib between two grooves.
            float ribs = (weightX * alongX + weightZ * alongZ) / total;
            return 2f * ribs * ribs - 1f;
        }

        // Hash of integer keys, from 0 to 1.
        public static float Hash01(int seed, int a, int b, int salt)
        {
            unchecked
            {
                uint hash = (uint)seed * 0x9e3779b9u ^ (uint)a * 0x85ebca6bu ^ (uint)b * 0xc2b2ae35u ^
                            (uint)salt * 0x27d4eb2fu;
                hash ^= hash >> 16;
                hash *= 0x7feb352du;
                hash ^= hash >> 15;
                hash *= 0x846ca68bu;
                hash ^= hash >> 16;
                return (hash & 0xffffff) / 16777216f;
            }
        }

        private static float Fade(float t)
        {
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        private static uint Hash(int x, int y)
        {
            unchecked
            {
                uint hash = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u;
                hash ^= hash >> 13;
                hash *= 0x5bd1e995u;
                hash ^= hash >> 15;
                return hash;
            }
        }

        private static float Gradient(uint hash, float x, float y)
        {
            switch (hash & 7u)
            {
                case 0u: return x + y;
                case 1u: return -x + y;
                case 2u: return x - y;
                case 3u: return -x - y;
                case 4u: return x;
                case 5u: return -x;
                case 6u: return y;
                default: return -y;
            }
        }
    }
}
