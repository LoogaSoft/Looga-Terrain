using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// The current field of a large water body, solved from its inlets and outlets. Water comes in at the inlet zones,
    /// leaves at the outlet zones, and the rest that the outlets take comes evenly from the whole body (rain and small
    /// streams). The field is a potential flow with depth: div(h grad phi) = -sources, with no flow through the shore.
    /// So the current fans out from an inlet, follows the coast, goes around headlands and gathers at an outlet, and
    /// it never points into land. The solve uses a coarse copy of the shore texture (R distance, G depth).
    /// </summary>
    internal sealed class WaterCurrentField
    {
        // Shore texels per side of a field cell.
        private const int Coarsen = 4;
        // A field cell is water when at least this share of its shore texels is water.
        private const float WetShare = 0.25f;
        // The water depth that carries the current, in metres. Deep water moves as a surface layer of this depth.
        private const float MinimumDepth = 0.5f;
        private const float MaximumDepth = 10f;
        private const float MaximumSpeed = 3f;
        // Depth of the layer that crosses an inlet or an outlet at the zone speed, in metres. A river mouth or the lip
        // of a waterfall carries a shallow layer, so the deep water before it moves slower than the zone speed.
        private const float DischargeDepth = 1.5f;
        // Distance in field cells within which a zone point finds a water cell.
        private const int SnapCells = 3;
        private const int MaximumIterations = 4000;
        private const double Tolerance = 1e-6;

        private int _width;
        private int _height;
        private float _cell;
        private Vector2 _origin;
        private Vector2[] _velocity;
        private bool[] _wet;

        /// <summary>True when the field holds a solved current.</summary>
        public bool IsBuilt { get; private set; }
        public float BuildMilliseconds { get; private set; }
        public int Iterations { get; private set; }

        /// <summary>
        /// Solves the field. The zones are the inlets (sources) and the outlets (sinks). The network joints add the
        /// discharge of the channels that flow in or out across their mouths. Returns false when no zone or joint
        /// touches the water, so the body keeps its own drift.
        /// </summary>
        /// <param name="shore">Shore texture of the body: R signed distance to the edge, G depth (-1 outside).</param>
        /// <param name="texel">Size of a shore texel in metres.</param>
        /// <param name="origin">World XZ of the shore texture corner.</param>
        public bool Build(Texture2D shore, float texel, Vector2 origin, IReadOnlyList<LoogaWaterFlow> zones,
            IReadOnlyList<WaterJointFlow> joints)
        {
            float start = Time.realtimeSinceStartup;
            IsBuilt = false;
            Iterations = 0;
            _cell = texel * Coarsen;
            _origin = origin;
            _width = Mathf.CeilToInt(shore.width / (float)Coarsen);
            _height = Mathf.CeilToInt(shore.height / (float)Coarsen);
            int count = _width * _height;
            _wet = new bool[count];
            _velocity = new Vector2[count];
            var depth = new float[count];
            ReadCells(shore, depth);

            var source = new double[count];
            bool any = false;
            foreach (LoogaWaterFlow zone in zones)
            {
                if (zone.Shape != LoogaWaterFlow.FlowShape.Inlet && zone.Shape != LoogaWaterFlow.FlowShape.Outlet) continue;

                float sign = zone.Shape == LoogaWaterFlow.FlowShape.Inlet ? 1f : -1f;
                any |= AddLines(zone.Lines, sign * zone.Speed * zone.Strength, source);
            }
            foreach (WaterJointFlow joint in joints)
            {
                any |= AddMouth(joint, source);
            }
            if (!any) return false;

            int[] component = LabelComponents(out int components);
            Balance(source, component, components);
            double[] potential = Solve(depth, source);
            WriteVelocity(potential, depth);
            IsBuilt = true;
            BuildMilliseconds = (Time.realtimeSinceStartup - start) * 1000f;
            return true;
        }

        /// <summary>Current at a world XZ point, in metres per second. Zero outside the field or on land.</summary>
        public Vector2 Sample(Vector2 world)
        {
            if (!IsBuilt) return Vector2.zero;

            float x = (world.x - _origin.x) / _cell - 0.5f;
            float y = (world.y - _origin.y) / _cell - 0.5f;
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            float fx = x - x0;
            float fy = y - y0;
            Vector2 sum = Vector2.zero;
            float weight = 0f;
            for (int dy = 0; dy <= 1; dy++)
            {
                for (int dx = 0; dx <= 1; dx++)
                {
                    int cx = x0 + dx;
                    int cy = y0 + dy;
                    if (cx < 0 || cy < 0 || cx >= _width || cy >= _height) continue;
                    int index = cy * _width + cx;
                    if (!_wet[index]) continue;
                    float w = (dx == 0 ? 1f - fx : fx) * (dy == 0 ? 1f - fy : fy);
                    sum += _velocity[index] * w;
                    weight += w;
                }
            }
            return weight > 1e-4f ? sum / weight : Vector2.zero;
        }

        public void Clear()
        {
            IsBuilt = false;
            _velocity = null;
            _wet = null;
        }

        // Water cells and their depth (mean of the water texels, limited to the current layer).
        private void ReadCells(Texture2D shore, float[] depth)
        {
            Color[] pixels = shore.GetPixels();
            int width = shore.width;
            int height = shore.height;
            for (int cy = 0; cy < _height; cy++)
            {
                for (int cx = 0; cx < _width; cx++)
                {
                    int wet = 0;
                    int total = 0;
                    float sum = 0f;
                    for (int y = cy * Coarsen; y < Mathf.Min(height, (cy + 1) * Coarsen); y++)
                    {
                        for (int x = cx * Coarsen; x < Mathf.Min(width, (cx + 1) * Coarsen); x++)
                        {
                            Color pixel = pixels[y * width + x];
                            total++;
                            if (pixel.r <= 0f || pixel.g < 0f) continue;
                            wet++;
                            sum += pixel.g;
                        }
                    }
                    int index = cy * _width + cx;
                    _wet[index] = total > 0 && wet >= total * WetShare;
                    depth[index] = _wet[index] ? Mathf.Clamp(sum / wet, MinimumDepth, MaximumDepth) : 0f;
                }
            }
        }

        // Adds the discharge of the lines: speed x discharge depth for each metre of line, in cubic metres per second.
        private bool AddLines(IReadOnlyList<List<Vector2>> lines, float speed, double[] source)
        {
            bool added = false;
            foreach (List<Vector2> line in lines)
            {
                for (int index = 1; index < line.Count; index++)
                {
                    Vector2 a = line[index - 1];
                    Vector2 b = line[index];
                    float length = Vector2.Distance(a, b);
                    if (length <= 0f) continue;
                    int cell = NearestWet((a + b) * 0.5f);
                    if (cell < 0) continue;
                    source[cell] += speed * DischargeDepth * length;
                    added = true;
                }
            }
            return added;
        }

        // Spreads the channel discharge over the cells across the channel mouth. An inflow is a source and an outflow
        // is a sink.
        private bool AddMouth(WaterJointFlow joint, double[] source)
        {
            if (joint.Discharge <= 0f || joint.HalfWidth <= 0f) return false;

            var across = new Vector2(-joint.Direction.y, joint.Direction.x) * joint.HalfWidth;
            int pieces = Mathf.Max(1, Mathf.CeilToInt(2f * joint.HalfWidth / _cell));
            double share = (joint.Inflow ? 1.0 : -1.0) * joint.Discharge / pieces;
            bool added = false;
            for (int piece = 0; piece < pieces; piece++)
            {
                float t = (piece + 0.5f) / pieces * 2f - 1f;
                int cell = NearestWet(joint.Point + across * t);
                if (cell < 0) continue;
                source[cell] += share;
                added = true;
            }
            return added;
        }

        private int NearestWet(Vector2 world)
        {
            int cx = Mathf.FloorToInt((world.x - _origin.x) / _cell);
            int cy = Mathf.FloorToInt((world.y - _origin.y) / _cell);
            int best = -1;
            int bestDistance = int.MaxValue;
            for (int dy = -SnapCells; dy <= SnapCells; dy++)
            {
                for (int dx = -SnapCells; dx <= SnapCells; dx++)
                {
                    int x = cx + dx;
                    int y = cy + dy;
                    if (x < 0 || y < 0 || x >= _width || y >= _height || !_wet[y * _width + x]) continue;
                    int distance = dx * dx + dy * dy;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = y * _width + x;
                }
            }
            return best;
        }

        // Connected water areas. Each area must take in as much as it gives out, or the solve has no answer.
        private int[] LabelComponents(out int components)
        {
            var label = new int[_wet.Length];
            for (int index = 0; index < label.Length; index++) label[index] = -1;
            components = 0;
            var stack = new Stack<int>();
            for (int seed = 0; seed < _wet.Length; seed++)
            {
                if (!_wet[seed] || label[seed] >= 0) continue;
                label[seed] = components;
                stack.Push(seed);
                while (stack.Count > 0)
                {
                    int index = stack.Pop();
                    int x = index % _width;
                    int y = index / _width;
                    for (int side = 0; side < 4; side++)
                    {
                        int nx = x + (side == 0 ? 1 : side == 1 ? -1 : 0);
                        int ny = y + (side == 2 ? 1 : side == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= _width || ny >= _height) continue;
                        int next = ny * _width + nx;
                        if (!_wet[next] || label[next] >= 0) continue;
                        label[next] = components;
                        stack.Push(next);
                    }
                }
                components++;
            }
            return label;
        }

        // The water that the outlets take more than the inlets give comes evenly from the whole area (and the other
        // way around).
        private static void Balance(double[] source, int[] component, int components)
        {
            var total = new double[components];
            var cells = new int[components];
            for (int index = 0; index < source.Length; index++)
            {
                if (component[index] < 0) continue;
                total[component[index]] += source[index];
                cells[component[index]]++;
            }
            for (int index = 0; index < source.Length; index++)
            {
                int area = component[index];
                if (area < 0) continue;
                source[index] -= total[area] / cells[area];
            }
        }

        // Conjugate gradients with a diagonal preconditioner on sum over water neighbours of h_face (phi_c - phi_n) =
        // source_c. The flux through a face is h_face (phi_c - phi_n) in cubic metres per second per metre of cell.
        private double[] Solve(float[] depth, double[] source)
        {
            int count = _wet.Length;
            var phi = new double[count];
            var residual = new double[count];
            var search = new double[count];
            var product = new double[count];
            var preconditioned = new double[count];
            var diagonal = new double[count];
            for (int index = 0; index < count; index++)
            {
                if (!_wet[index]) continue;
                diagonal[index] = Neighbours(index, depth, null);
                residual[index] = source[index];
            }
            double norm = Dot(source, source);
            if (norm <= 0.0) return phi;

            for (int index = 0; index < count; index++)
            {
                preconditioned[index] = diagonal[index] > 0.0 ? residual[index] / diagonal[index] : 0.0;
                search[index] = preconditioned[index];
            }
            double rz = Dot(residual, preconditioned);
            for (int iteration = 0; iteration < MaximumIterations; iteration++)
            {
                for (int index = 0; index < count; index++)
                {
                    product[index] = _wet[index] ? Neighbours(index, depth, search) : 0.0;
                }
                double pAp = Dot(search, product);
                if (pAp <= 0.0) break;
                double alpha = rz / pAp;
                for (int index = 0; index < count; index++)
                {
                    phi[index] += alpha * search[index];
                    residual[index] -= alpha * product[index];
                }
                Iterations = iteration + 1;
                if (Dot(residual, residual) < Tolerance * Tolerance * norm) break;
                for (int index = 0; index < count; index++)
                {
                    preconditioned[index] = diagonal[index] > 0.0 ? residual[index] / diagonal[index] : 0.0;
                }
                double next = Dot(residual, preconditioned);
                double beta = next / rz;
                rz = next;
                for (int index = 0; index < count; index++)
                {
                    search[index] = preconditioned[index] + beta * search[index];
                }
            }
            return phi;
        }

        // With values: sum of h_face (v_c - v_n) over the water neighbours. Without values: sum of h_face.
        private double Neighbours(int index, float[] depth, double[] values)
        {
            int x = index % _width;
            int y = index / _width;
            double sum = 0.0;
            for (int side = 0; side < 4; side++)
            {
                int nx = x + (side == 0 ? 1 : side == 1 ? -1 : 0);
                int ny = y + (side == 2 ? 1 : side == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= _width || ny >= _height) continue;
                int next = ny * _width + nx;
                if (!_wet[next]) continue;
                double face = 0.5 * (depth[index] + depth[next]);
                sum += values == null ? face : face * (values[index] - values[next]);
            }
            return sum;
        }

        private static double Dot(double[] a, double[] b)
        {
            double sum = 0.0;
            for (int index = 0; index < a.Length; index++) sum += a[index] * b[index];
            return sum;
        }

        // Velocity of a cell: the mean of its face velocities along each axis. A face to land has no flow, so the
        // current near the shore runs along the shore. One box pass smooths the cell steps.
        private void WriteVelocity(double[] phi, float[] depth)
        {
            var raw = new Vector2[_wet.Length];
            for (int index = 0; index < _wet.Length; index++)
            {
                if (!_wet[index]) continue;
                int x = index % _width;
                int y = index / _width;
                float u = 0.5f * (Face(phi, index, x - 1, y, -1) + Face(phi, index, x + 1, y, 1));
                float v = 0.5f * (Face(phi, index, x, y - 1, -1) + Face(phi, index, x, y + 1, 1));
                var velocity = new Vector2(u, v);
                raw[index] = Vector2.ClampMagnitude(velocity, MaximumSpeed);
            }
            for (int index = 0; index < _wet.Length; index++)
            {
                if (!_wet[index]) continue;
                int x = index % _width;
                int y = index / _width;
                Vector2 sum = Vector2.zero;
                int samples = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= _width || ny >= _height || !_wet[ny * _width + nx]) continue;
                        sum += raw[ny * _width + nx];
                        samples++;
                    }
                }
                _velocity[index] = sum / samples;
            }
        }

        // Velocity through the face to a neighbour, along the positive axis. Zero to land or past the field edge.
        private float Face(double[] phi, int index, int nx, int ny, int direction)
        {
            if (nx < 0 || ny < 0 || nx >= _width || ny >= _height) return 0f;
            int next = ny * _width + nx;
            if (!_wet[next]) return 0f;
            // Flow goes from high to low potential: the flow toward the neighbour is (phi_c - phi_n) / cell.
            return (float)((phi[index] - phi[next]) / _cell) * direction;
        }
    }
}
