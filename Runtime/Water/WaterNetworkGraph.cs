using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// The elements, joints and validation results of a <see cref="LoogaWaterNetwork"/>, and the flow solve that makes
    /// them. The solve reads the water bodies. It does not change them.
    /// </summary>
    /// <remarks>
    /// Each channel point gets the slope of the water surface, the Manning speed for the design bed depth, the
    /// discharge and the Froude number. A reach that is too flat for Manning takes its speed from the discharge that
    /// comes in from upstream. The regime comes from the Froude number, the slope and the surface angle. Each channel
    /// end that drops freely into lower water gets a fall: the sheet path from the lip to the plunge.
    /// </remarks>
    public sealed class WaterNetworkGraph
    {
        private const float Gravity = 9.81f;
        // Distance between the points of a body outline, in metres. A very long outline gets fewer points.
        private const float OutlineSpacing = 4f;
        private const int MaximumOutlinePoints = 8192;
        // Below this slope Manning gives no useful discharge. Such a reach takes its speed from the inflow.
        private const float FlowSlope = 0.002f;
        // A Manning discharge outside this factor of the channel discharge is a continuity conflict.
        private const float DischargeConflict = 2f;
        // Shortest conflict run that gives a warning, in metres.
        private const float ConflictLength = 20f;

        private readonly List<WaterNetworkElement> _elements = new();
        private readonly List<WaterNetworkConnection> _connections = new();
        private readonly List<WaterNetworkIssue> _issues = new();
        private readonly List<WaterJointFlow> _jointFlows = new();
        private readonly List<WaterNetworkFall> _falls = new();
        private readonly List<WaterNetworkCrest> _crests = new();
        private readonly List<LoogaWaterFlow> _lipZones = new();
        private readonly List<WaterSplinePoint> _points = new();
        private readonly List<float> _ownDischarge = new();
        private readonly List<float> _median = new();
        private readonly List<float> _smoothTurbulence = new();
        private readonly List<float> _smoothAeration = new();
        // Discharge solve state of each element: 0 = open, 1 = in progress, 2 = done.
        private readonly List<int> _dischargeState = new();
        private WaterNetworkSettings _settings;

        public IReadOnlyList<WaterNetworkElement> Elements => _elements;
        public IReadOnlyList<WaterNetworkConnection> Connections => _connections;
        public IReadOnlyList<WaterNetworkIssue> Issues => _issues;
        /// <summary>Channel ends that drop freely into lower water, with their sheet paths and plunges.</summary>
        public IReadOnlyList<WaterNetworkFall> Falls => _falls;
        /// <summary>Area bodies that spill over a lip line marked by a fall component, with their landings.</summary>
        public IReadOnlyList<WaterNetworkCrest> Crests => _crests;
        public float SolveMilliseconds { get; private set; }
        /// <summary>Jets and mouth pulls at the joints where a channel flows into or out of a body.</summary>
        internal IReadOnlyList<WaterJointFlow> JointFlows => _jointFlows;

        /// <summary>Number of issues with the severity.</summary>
        public int IssueCount(WaterIssueSeverity severity)
        {
            int count = 0;
            foreach (WaterNetworkIssue issue in _issues)
            {
                if (issue.Severity == severity) count++;
            }
            return count;
        }

        /// <summary>
        /// Builds the elements from the bodies, connects them, solves the flow and solves the falls. A fall component
        /// of a channel end shapes the fall there.
        /// </summary>
        internal void Build(IReadOnlyList<LoogaWaterBody> bodies, IReadOnlyList<LoogaWaterFall> fallOverrides,
            WaterNetworkSettings settings, WaterFallSettings fallSettings)
        {
            float start = Time.realtimeSinceStartup;
            _settings = settings;
            _elements.Clear();
            _connections.Clear();
            _issues.Clear();
            _jointFlows.Clear();
            _falls.Clear();
            _crests.Clear();
            _ownDischarge.Clear();
            foreach (LoogaWaterBody body in bodies)
            {
                AddElements(body);
            }
            foreach (WaterNetworkElement element in _elements)
            {
                _ownDischarge.Add(element.IsChannel ? SolveManning(element) : 0f);
            }
            Connect();
            SolveDischarge();
            for (int index = 0; index < _elements.Count; index++)
            {
                if (_elements[index].IsChannel) FinishChannel(index);
            }
            ValidateJoints();
            BuildFalls(fallOverrides, fallSettings);
            BuildCrests(fallOverrides);
            BuildJointFlows();
            SolveMilliseconds = (Time.realtimeSinceStartup - start) * 1000f;
        }

        #region Lookup
        /// <summary>Index of the element of a body and spline, or -1. A body area always has spline 0.</summary>
        internal int FindElement(LoogaWaterBody body, int splineIndex)
        {
            for (int index = 0; index < _elements.Count; index++)
            {
                WaterNetworkElement element = _elements[index];
                if (element.Body == body && element.SplineIndex == splineIndex) return index;
            }
            return -1;
        }

        /// <summary>
        /// Solved channel speed and regime values (x = turbulence, y = aeration) at a knot position, interpolated
        /// between the solve points.
        /// </summary>
        internal bool TryGetState(int element, float knot, out float speed, out Vector2 regime)
        {
            speed = 0f;
            regime = Vector2.zero;
            if (element < 0 || element >= _elements.Count) return false;

            List<WaterChannelSample> samples = _elements[element].SampleList;
            if (samples.Count == 0) return false;

            int low = 0;
            int high = samples.Count - 1;
            if (knot <= samples[low].Knot)
            {
                high = low;
            }
            else if (knot >= samples[high].Knot)
            {
                low = high;
            }
            while (high - low > 1)
            {
                int middle = (low + high) / 2;
                if (samples[middle].Knot <= knot)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }
            WaterChannelSample a = samples[low];
            WaterChannelSample b = samples[high];
            float span = b.Knot - a.Knot;
            float t = span > 1e-6f ? Mathf.Clamp01((knot - a.Knot) / span) : 0f;
            speed = Mathf.Lerp(a.Speed, b.Speed, t);
            regime = Vector2.Lerp(new Vector2(a.Turbulence, a.Aeration), new Vector2(b.Turbulence, b.Aeration), t);
            return true;
        }

        /// <summary>Horizontal distance to the outline of a body element: positive inside, negative outside.</summary>
        internal float SignedOutlineDistance(int element, Vector2 point)
        {
            WaterNetworkElement body = _elements[element];
            float distance = OutlineDistance(body, point);
            return IsInside(body, point) ? distance : -distance;
        }
        #endregion

        #region Joint flow
        // Longest channel length that the search for an outline crossing walks from a joint, in metres.
        private const float CrossingSearch = 200f;

        // A channel that flows into or out of a body gives the body a jet or a mouth pull. A fall gives the body a
        // plunge where the falling water meets it.
        private void BuildJointFlows()
        {
            foreach (WaterNetworkConnection connection in _connections)
            {
                bool inflow = connection.Joint == WaterJoint.ChannelToBody;
                if (connection.Fall || !inflow && connection.Joint != WaterJoint.BodyToChannel) continue;

                int channel = inflow ? connection.Upstream : connection.Downstream;
                int body = inflow ? connection.Downstream : connection.Upstream;
                if (TryGetCrossing(channel, body, inflow, out WaterJointFlow joint)) _jointFlows.Add(joint);
            }
            foreach (WaterNetworkFall fall in _falls)
            {
                if (_elements[fall.Receiver].IsChannel) continue;

                _jointFlows.Add(new WaterJointFlow
                {
                    Body = fall.Receiver,
                    Channel = fall.Channel,
                    Inflow = true,
                    Plunge = true,
                    Decay = fall.PlungeDecay,
                    Point = Flat(fall.Plunge),
                    Direction = fall.Direction,
                    HalfWidth = 0.5f * fall.Width,
                    Speed = fall.PlungeSpeed,
                    Discharge = _elements[fall.Channel].Discharge,
                    Turbulence = 1f,
                    Aeration = 1f
                });
            }
            foreach (WaterNetworkCrest crest in _crests)
            {
                AddCrestJoints(crest);
            }
        }

        // Splits the lip into pieces. Each piece pulls the water of the body over the lip at the brink speed, and
        // makes a plunge where its sheet meets the water below. The plunge point is on the sheet path, so the flow,
        // the impact foam and the sheet meet at the same place.
        private void AddCrestJoints(WaterNetworkCrest crest)
        {
            List<Vector3> lip = crest.LipList;
            if (lip.Count < 2) return;

            float length = 0f;
            for (int index = 1; index < lip.Count; index++)
            {
                length += Vector2.Distance(Flat(lip[index - 1]), Flat(lip[index]));
            }
            int pieces = Mathf.Max(1, Mathf.RoundToInt(length / CrestJointSpacing));
            float pieceLength = length / pieces;
            float brinkSpeed = WaterFallSheet.FreeOverfallSpeed(crest.UnitDischarge, Gravity);
            bool plunge = crest.Receiver >= 0 && !_elements[crest.Receiver].IsChannel && crest.PlungeIndex >= 0;
            float plungeSpeed = Mathf.Min(crest.Edge.PlungeSpeed, MaximumCrestPlungeSpeed);
            float run = 0f;
            int first = 0;
            for (int piece = 0; piece < pieces; piece++)
            {
                float end = piece == pieces - 1 ? float.MaxValue : (piece + 1) * pieceLength;
                Vector2 point = Vector2.zero;
                Vector2 normal = Vector2.zero;
                int count = 0;
                int last = first;
                while (last < lip.Count)
                {
                    point += Flat(lip[last]);
                    normal += crest.NormalList[last];
                    count++;
                    if (last + 1 >= lip.Count) break;

                    float step = Vector2.Distance(Flat(lip[last]), Flat(lip[last + 1]));
                    if (run + step > end) break;

                    run += step;
                    last++;
                }
                first = Mathf.Min(last + 1, lip.Count - 1);
                if (count == 0 || normal.sqrMagnitude < 1e-8f) continue;

                point /= count;
                normal.Normalize();
                _jointFlows.Add(new WaterJointFlow
                {
                    Body = crest.Body,
                    Channel = crest.Body,
                    Crest = true,
                    Decay = CrestPullDecay,
                    Point = point,
                    Direction = normal,
                    HalfWidth = 0.5f * pieceLength,
                    Speed = brinkSpeed,
                    Discharge = crest.UnitDischarge * pieceLength
                });
                if (!plunge) continue;

                _jointFlows.Add(new WaterJointFlow
                {
                    Body = crest.Receiver,
                    Channel = crest.Body,
                    Inflow = true,
                    Plunge = true,
                    Crest = true,
                    Decay = crest.Edge.PlungeDecay,
                    Point = point + normal * crest.PlungeDistance,
                    Direction = normal,
                    HalfWidth = 0.5f * pieceLength,
                    Speed = plungeSpeed,
                    Discharge = crest.UnitDischarge * pieceLength,
                    Turbulence = 1f,
                    Aeration = 1f
                });
            }
        }

        // Walks from the joint end of the channel to the first point outside the body. The crossing is between that
        // point and its neighbour inside the body. A channel end outside the body crosses at the end.
        private bool TryGetCrossing(int channel, int body, bool inflow, out WaterJointFlow joint)
        {
            joint = default;
            List<WaterChannelSample> samples = _elements[channel].SampleList;
            WaterNetworkElement element = _elements[body];
            int count = samples.Count;
            int end = inflow ? count - 1 : 0;
            int step = inflow ? -1 : 1;
            int outside = end;
            while (outside >= 0 && outside < count && IsInside(element, Flat(samples[outside].Position)) &&
                Mathf.Abs(samples[outside].Station - samples[end].Station) < CrossingSearch)
            {
                outside += step;
            }
            outside = Mathf.Clamp(outside, 0, count - 1);
            int inside = outside == end ? end : outside - step;
            // The flow goes from the lower sample index to the higher one.
            int from = Mathf.Min(inside, outside);
            int to = Mathf.Max(inside, outside);
            if (from == to)
            {
                from = Mathf.Max(0, end - 1);
                to = Mathf.Min(count - 1, from + 1);
            }
            Vector2 direction = Flat(samples[to].Position) - Flat(samples[from].Position);
            if (direction.sqrMagnitude < 1e-8f) return false;

            WaterChannelSample sample = samples[inside];
            joint = new WaterJointFlow
            {
                Body = body,
                Channel = channel,
                Inflow = inflow,
                Point = 0.5f * (Flat(samples[inside].Position) + Flat(samples[outside].Position)),
                Direction = direction.normalized,
                HalfWidth = 0.5f * sample.Width,
                Speed = sample.Speed,
                Discharge = sample.Discharge,
                Turbulence = sample.Turbulence,
                Aeration = sample.Aeration
            };
            return true;
        }
        #endregion

        #region Falls
        // Share of the impact speed that the plunge gives the surface water. The falling water dives deep, and more
        // than half of its energy goes into turbulence in the pool.
        private const float PlungeShare = 0.45f;
        // The plunge flow decays over this multiple of the drop height, within these limits, in metres. The water
        // that the plunge pushes up carries the foam out in streaks before the current solve takes over.
        private const float PlungeDecayPerDrop = 3f;
        // A crest lip gives its bodies one pull joint and one plunge joint for each piece of about this length, in
        // metres.
        private const float CrestJointSpacing = 40f;
        // Decay length of the pull toward a crest lip, in metres. The water speeds up over about three decay lengths
        // upstream of the lip, so the pull shows from a distance.
        private const float CrestPullDecay = 30f;
        // The surface flow out of a crest plunge is at most this fast, in metres per second. A tall crest plunges deep,
        // and the water comes up as a broad boil.
        private const float MaximumCrestPlungeSpeed = 4f;
        private const float MinimumPlungeDecay = 4f;
        private const float MaximumPlungeDecay = 90f;
        // Distance upstream of a channel end over which the flow direction at the lip is measured, in metres.
        private const float LipDirectionRun = 4f;
        // A plunge farther than this outside the outline of the water below gives a warning, in metres.
        private const float PlungeOutside = 2f;
        private static readonly Vector2[] NoProfile = new Vector2[0];

        private void BuildFalls(IReadOnlyList<LoogaWaterFall> fallOverrides, WaterFallSettings fallSettings)
        {
            for (int index = 0; index < _connections.Count; index++)
            {
                WaterNetworkConnection connection = _connections[index];
                if (!connection.Fall) continue;

                WaterNetworkElement upstream = _elements[connection.Upstream];
                if (!upstream.IsChannel || connection.UpstreamSample != upstream.SampleList.Count - 1)
                {
                    AddIssue(WaterIssueSeverity.Info, connection.Upstream, connection.Point,
                        "A fall from a body or from a channel side gets no fall sheet.");
                    continue;
                }
                LoogaWaterFall fallOverride = FindFallOverride(fallOverrides, upstream);
                var fall = new WaterNetworkFall(index, connection.Upstream, connection.Downstream, fallOverride);
                if (SolveFall(fall, connection, fallOverride ? fallOverride.Settings : fallSettings)) _falls.Add(fall);
            }
        }

        private static LoogaWaterFall FindFallOverride(IReadOnlyList<LoogaWaterFall> fallOverrides,
            WaterNetworkElement channel)
        {
            foreach (LoogaWaterFall fall in fallOverrides)
            {
                if (fall && fall.Channel == channel.Body && fall.SplineIndex == channel.SplineIndex) return fall;
            }
            return null;
        }

        // The frame of a generated fall stands at the channel end and faces the flow, with a scale of 1. A fall
        // component gives its own frame, structure and design flow.
        private bool SolveFall(WaterNetworkFall fall, WaterNetworkConnection connection, WaterFallSettings settings)
        {
            WaterNetworkElement channel = _elements[fall.Channel];
            List<WaterChannelSample> samples = channel.SampleList;
            WaterChannelSample end = samples[samples.Count - 1];
            Vector2 direction = LipDirection(samples);
            LoogaWaterFall fallOverride = fall.Override;
            Matrix4x4 frame;
            float scale = 1f;
            IReadOnlyList<Vector2> profile = NoProfile;
            if (fallOverride)
            {
                Transform transform = fallOverride.transform;
                frame = transform.localToWorldMatrix;
                scale = Mathf.Max(Mathf.Abs(transform.lossyScale.x), 1e-4f);
                direction = Flat(transform.forward).normalized;
                profile = fallOverride.Profile;
            }
            else
            {
                frame = Matrix4x4.TRS(new Vector3(end.Position.x, 0f, end.Position.z),
                    Quaternion.LookRotation(new Vector3(direction.x, 0f, direction.y)), Vector3.one);
            }
            if (direction.sqrMagnitude < 0.5f) return false;

            Matrix4x4 worldToFrame = frame.inverse;
            float level = worldToFrame.MultiplyPoint3x4(end.Position).y;
            float landing = worldToFrame.MultiplyPoint3x4(connection.Landing).y;
            float lipSpeed = ApproachSpeed(end, direction);
            float unitDischarge = fallOverride && fallOverride.UnitDischarge > 0f
                ? fallOverride.UnitDischarge
                : channel.Discharge / Mathf.Max(end.Width, 0.1f);
            float drop = end.Position.y - connection.Landing.y;
            float impact = Mathf.Sqrt(lipSpeed * lipSpeed + 2f * Gravity * Mathf.Max(drop, 0f));
            fall.Edge = new WaterFallEdge
            {
                FrameToWorld = frame,
                Scale = scale,
                Level = level,
                Crest = WaterFallSheet.CrestHeight(profile, level - end.Depth / scale),
                Landing = landing,
                Width = fallOverride && fallOverride.Width > 0f ? fallOverride.Width : end.Width / scale,
                LipSpeed = lipSpeed / scale,
                UnitDischarge = unitDischarge / (scale * scale),
                Gravity = Gravity / scale,
                Bays = fallOverride ? fallOverride.Bays : 1,
                PierWidth = fallOverride ? fallOverride.PierWidth : 0f,
                Walls = fallOverride && fallOverride.Walls,
                FlowDirection = direction,
                LipSpacing = Mathf.Max(0.25f, channel.Body.VertexSpacing),
                LipRegime = new Vector2(end.Turbulence, end.Aeration),
                PlungeSpeed = PlungeShare * impact,
                PlungeDecay = Mathf.Clamp(PlungeDecayPerDrop * drop, MinimumPlungeDecay, MaximumPlungeDecay)
            };
            fall.PlungeIndex = WaterFallSheet.SolvePath(fall.Edge, profile, settings, fall.PathList);
            if (fall.PlungeIndex < 0)
            {
                AddIssue(WaterIssueSeverity.Warning, fall.Channel, end.Position,
                    "The fall of " + channel.Name + " does not reach the water below.");
                return false;
            }

            Vector3 plunge = frame.MultiplyPoint3x4(new Vector3(0f, landing,
                fall.PathList[fall.PlungeIndex].Surface.x));
            fall.Lip = frame.MultiplyPoint3x4(new Vector3(0f, level, 0f));
            fall.Plunge = plunge;
            fall.Direction = direction;
            fall.Width = fall.Edge.Width * scale;
            fall.Drop = drop;
            fall.UnitDischarge = unitDischarge;
            fall.LipSpeed = lipSpeed;
            fall.ImpactSpeed = impact;
            fall.PlungeSpeed = fall.Edge.PlungeSpeed;
            fall.PlungeDecay = fall.Edge.PlungeDecay;
            fall.Hash = HashFall(fall, profile, settings);
            CheckPlunge(fall);
            return true;
        }

        // The direction of the last metres of the channel, so a short kink at the end does not turn the fall.
        private static Vector2 LipDirection(List<WaterChannelSample> samples)
        {
            WaterChannelSample end = samples[samples.Count - 1];
            int index = samples.Count - 2;
            while (index > 0 && end.Station - samples[index].Station < LipDirectionRun)
            {
                index--;
            }
            Vector2 direction = Flat(end.Position) - Flat(samples[Mathf.Max(index, 0)].Position);
            return direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector2.zero;
        }

        // The speed of the water toward the lip: the solved channel speed, then the flow zones over the lip, so the
        // sheet moves with the river surface there.
        private float ApproachSpeed(WaterChannelSample end, Vector2 direction)
        {
            Vector2 point = Flat(end.Position);
            Vector2 flow = direction * end.Speed;
            _lipZones.Clear();
            foreach (LoogaWaterFlow zone in LoogaWaterFlow.Active)
            {
                if (zone && zone.Reach.Contains(point) && zone.ReachesHeight(end.Position.y, end.Position.y))
                {
                    _lipZones.Add(zone);
                }
            }
            _lipZones.Sort(CompareZones);
            foreach (LoogaWaterFlow zone in _lipZones)
            {
                zone.Apply(point, ref flow);
            }
            return flow.magnitude;
        }

        private static int CompareZones(LoogaWaterFlow a, LoogaWaterFlow b)
        {
            int order = a.Order.CompareTo(b.Order);
            return order != 0 ? order : string.CompareOrdinal(a.name, b.name);
        }

        private static Hash128 HashFall(WaterNetworkFall fall, IReadOnlyList<Vector2> profile,
            WaterFallSettings settings)
        {
            var hash = new Hash128();
            WaterFallEdge edge = fall.Edge;
            hash.Append(ref edge);
            hash.Append(profile.Count);
            foreach (Vector2 point in profile)
            {
                Vector2 value = point;
                hash.Append(ref value);
            }
            settings.AppendTo(ref hash);
            return hash;
        }

        #region Crests
        // Distance between the lip samples of a crest, in metres.
        private const float CrestSpacing = 4f;
        // Distance from the lip at which the side of the body is tested, in metres.
        private const float CrestProbe = 2f;
        // The normals are averaged along the lip over this many throw distances, at least the minimum, in metres. A
        // sheet that leaves a tight curve would fold where its rows cross.
        private const float NormalSmoothingPerThrow = 2f;
        private const float MinimumNormalSmoothing = 12f;
        private readonly List<Vector2> _rawNormals = new();
        private readonly List<float> _lipArc = new();

        // A crest of an area body: the lip line of a fall component, the water below and the free jet that lands in
        // it. The lip speed is the critical speed over a broad crest. The flow is the design flow of the component, or
        // the inflow of the body spread over the lip.
        private void BuildCrests(IReadOnlyList<LoogaWaterFall> fallOverrides)
        {
            foreach (LoogaWaterFall fall in fallOverrides)
            {
                if (!fall || fall.Source != LoogaWaterFall.FallSource.BodyCrest) continue;

                int body = FindElement(fall.Body, 0);
                if (body < 0 || _elements[body].IsChannel)
                {
                    AddIssue(WaterIssueSeverity.Warning, -1, fall.transform.position,
                        "The crest " + fall.name + " needs an area body of this network.");
                    continue;
                }
                if (!fall.Lip || fall.Lip.Splines.Count == 0)
                {
                    AddIssue(WaterIssueSeverity.Warning, body, fall.transform.position,
                        "The crest " + fall.name + " has no lip spline.");
                    continue;
                }
                var crest = new WaterNetworkCrest(fall, body);
                if (SolveCrest(crest)) _crests.Add(crest);
            }
        }

        private bool SolveCrest(WaterNetworkCrest crest)
        {
            WaterNetworkElement body = _elements[crest.Body];
            WaterSplines.Sample(crest.Fall.Lip.Splines[0], crest.Fall.Lip.transform.localToWorldMatrix, CrestSpacing,
                _points);
            if (_points.Count < 2)
            {
                AddIssue(WaterIssueSeverity.Warning, crest.Body, crest.Fall.transform.position,
                    "The lip of " + crest.Fall.name + " is too short.");
                return false;
            }

            // The lip stands at the body level. The normals point to the side where the body is not.
            float length = 0f;
            int outward = 0;
            _rawNormals.Clear();
            _lipArc.Clear();
            for (int index = 0; index < _points.Count; index++)
            {
                Vector3 point = _points[index].Position;
                crest.LipList.Add(new Vector3(point.x, body.Level, point.z));
                Vector2 before = Flat(_points[Mathf.Max(0, index - 1)].Position);
                Vector2 after = Flat(_points[Mathf.Min(_points.Count - 1, index + 1)].Position);
                Vector2 tangent = (after - before).normalized;
                var normal = new Vector2(tangent.y, -tangent.x);
                _rawNormals.Add(normal);
                Vector2 flat = Flat(point);
                float side = SignedOutlineDistance(crest.Body, flat + normal * CrestProbe) -
                    SignedOutlineDistance(crest.Body, flat - normal * CrestProbe);
                outward += side < 0f ? 1 : -1;
                if (index > 0) length += Vector2.Distance(Flat(_points[index - 1].Position), flat);
                _lipArc.Add(length);
            }
            if (outward < 0)
            {
                for (int index = 0; index < _rawNormals.Count; index++)
                {
                    _rawNormals[index] = -_rawNormals[index];
                }
            }
            crest.Length = length;

            float design = crest.Fall.UnitDischarge;
            crest.DesignDischarge = design > 0f;
            crest.UnitDischarge = crest.DesignDischarge ? design : BodyInflow(crest.Body) / Mathf.Max(length, 1f);
            crest.LipDepth = Mathf.Pow(crest.UnitDischarge * crest.UnitDischarge / Gravity, 1f / 3f);
            crest.LipSpeed = Mathf.Sqrt(Gravity * crest.LipDepth);
            if (!crest.DesignDischarge)
            {
                AddIssue(WaterIssueSeverity.Info, crest.Body, crest.LipList[crest.LipList.Count / 2],
                    "The crest " + crest.Fall.name + " takes its flow from the inflow of " + body.Name + ": " +
                    crest.UnitDischarge.ToString("F2") + " m²/s per metre.");
            }

            int middle = crest.LipList.Count / 2;
            // The receiver search uses the raw normals. The smoothing needs the throw, which needs the receiver.
            crest.NormalList.AddRange(_rawNormals);
            crest.Receiver = FindCrestReceiver(crest, middle);
            if (crest.Receiver < 0)
            {
                AddIssue(WaterIssueSeverity.Warning, crest.Body, crest.LipList[middle],
                    "The crest " + crest.Fall.name + " finds no water below its lip.");
                for (int index = 0; index < crest.LipList.Count; index++)
                {
                    crest.LandingList.Add(crest.LipList[index]);
                }
                return true;
            }

            float level = _elements[crest.Receiver].Level;
            crest.Drop = body.Level - level;
            float brinkSpeed = WaterFallSheet.FreeOverfallSpeed(crest.UnitDischarge, Gravity);
            crest.ImpactSpeed = Mathf.Sqrt(brinkSpeed * brinkSpeed + 2f * Gravity * crest.Drop);
            float throwDistance = brinkSpeed * Mathf.Sqrt(2f * crest.Drop / Gravity);
            SmoothNormals(crest, Mathf.Max(MinimumNormalSmoothing, NormalSmoothingPerThrow * throwDistance));
            SolveCrestPath(crest, level);
            float plungeDistance = crest.PlungeIndex >= 0 ? crest.PlungeDistance : throwDistance;
            int outside = 0;
            for (int index = 0; index < crest.LipList.Count; index++)
            {
                Vector3 lip = crest.LipList[index];
                Vector2 offset = crest.NormalList[index] * plungeDistance;
                var landing = new Vector3(lip.x + offset.x, level, lip.z + offset.y);
                crest.LandingList.Add(landing);
                if (SignedOutlineDistance(crest.Receiver, Flat(landing)) < -PlungeOutside) outside++;
            }
            if (outside > 0)
            {
                AddIssue(WaterIssueSeverity.Warning, crest.Receiver, crest.LandingList[middle],
                    "The crest " + crest.Fall.name + " lands outside " + _elements[crest.Receiver].Name + " at " +
                    outside + " of " + crest.LandingList.Count + " lip points.");
            }
            return true;
        }

        // A box average of the normals over the window along the lip, then normalised.
        private void SmoothNormals(WaterNetworkCrest crest, float window)
        {
            float half = 0.5f * window;
            int start = 0;
            int end = 0;
            Vector2 sum = Vector2.zero;
            for (int index = 0; index < _rawNormals.Count; index++)
            {
                while (end < _rawNormals.Count && _lipArc[end] <= _lipArc[index] + half)
                {
                    sum += _rawNormals[end++];
                }
                while (_lipArc[start] < _lipArc[index] - half)
                {
                    sum -= _rawNormals[start++];
                }
                crest.NormalList[index] = sum.sqrMagnitude > 1e-8f ? sum.normalized : _rawNormals[index];
            }
        }

        // The sheet path of the crest: no structure face, a free jet from the lip at the critical speed, the bend
        // into the water below and the run-out. The frame is the world, with a scale of 1.
        private void SolveCrestPath(WaterNetworkCrest crest, float level)
        {
            WaterNetworkElement body = _elements[crest.Body];
            WaterFallSettings settings = crest.Fall.Settings;
            crest.Edge = new WaterFallEdge
            {
                FrameToWorld = Matrix4x4.identity,
                Scale = 1f,
                Level = body.Level,
                Crest = body.Level - WaterFallSheet.CriticalHeadShare * crest.LipDepth,
                Landing = level,
                Width = crest.Length,
                LipSpeed = crest.LipSpeed,
                UnitDischarge = crest.UnitDischarge,
                Gravity = Gravity,
                Bays = 1,
                PierWidth = 0f,
                Walls = false,
                FlowDirection = Vector2.zero,
                LipSpacing = Mathf.Max(0.25f, body.Body.VertexSpacing),
                LipRegime = Vector2.zero,
                PlungeSpeed = PlungeShare * crest.ImpactSpeed,
                PlungeDecay = Mathf.Clamp(PlungeDecayPerDrop * crest.Drop, MinimumPlungeDecay, MaximumPlungeDecay)
            };
            crest.PlungeIndex = WaterFallSheet.SolvePath(crest.Edge, NoProfile, settings, crest.PathList);
            if (crest.PlungeIndex < 0)
            {
                AddIssue(WaterIssueSeverity.Warning, crest.Body, crest.LipList[crest.LipList.Count / 2],
                    "The crest " + crest.Fall.name + " does not reach the water below.");
                return;
            }
            crest.PlungeDistance = crest.PathList[crest.PlungeIndex].Surface.x;

            var hash = new Hash128();
            WaterFallEdge edge = crest.Edge;
            hash.Append(ref edge);
            settings.AppendTo(ref hash);
            hash.Append(crest.LipList.Count);
            for (int index = 0; index < crest.LipList.Count; index++)
            {
                Vector3 lip = crest.LipList[index];
                Vector2 normal = crest.NormalList[index];
                hash.Append(ref lip);
                hash.Append(ref normal);
            }
            crest.Hash = hash;
        }

        // The free jet leaves the lip at the brink speed of a free overfall along the normal and falls to the level
        // below.
        private static Vector3 CrestLanding(WaterNetworkCrest crest, int index, float level)
        {
            Vector3 lip = crest.LipList[index];
            float fallTime = Mathf.Sqrt(2f * Mathf.Max(lip.y - level, 0f) / Gravity);
            float brinkSpeed = WaterFallSheet.FreeOverfallSpeed(crest.UnitDischarge, Gravity);
            Vector2 throwDistance = crest.NormalList[index] * (brinkSpeed * fallTime);
            return new Vector3(lip.x + throwDistance.x, level, lip.z + throwDistance.y);
        }

        // The highest body below the lip that the jet from the middle lip point reaches, within the snap distance.
        private int FindCrestReceiver(WaterNetworkCrest crest, int middle)
        {
            float upper = _elements[crest.Body].Level;
            int found = -1;
            float bestLevel = float.MinValue;
            for (int index = 0; index < _elements.Count; index++)
            {
                WaterNetworkElement element = _elements[index];
                if (element.IsChannel || index == crest.Body) continue;
                if (element.Level > upper - _settings.FallDrop || element.Level <= bestLevel) continue;

                Vector2 landing = Flat(CrestLanding(crest, middle, element.Level));
                if (!Expand(element.Bounds, _settings.SnapDistance).Contains(landing)) continue;
                if (SignedOutlineDistance(index, landing) < -_settings.SnapDistance) continue;

                bestLevel = element.Level;
                found = index;
            }
            return found;
        }

        // Channel discharge into the body minus channel discharge out of it.
        private float BodyInflow(int body)
        {
            float flow = 0f;
            foreach (WaterNetworkConnection connection in _connections)
            {
                if (connection.Downstream == body && _elements[connection.Upstream].IsChannel)
                {
                    flow += _elements[connection.Upstream].Discharge;
                }
                else if (connection.Upstream == body && _elements[connection.Downstream].IsChannel)
                {
                    flow -= _elements[connection.Downstream].Discharge;
                }
            }
            return Mathf.Max(flow, 0f);
        }
        #endregion

        // The falling water must land in the water below, not on its bank.
        private void CheckPlunge(WaterNetworkFall fall)
        {
            if (_elements[fall.Receiver].IsChannel) return;

            float inside = SignedOutlineDistance(fall.Receiver, Flat(fall.Plunge));
            if (inside >= -PlungeOutside) return;

            AddIssue(WaterIssueSeverity.Warning, fall.Receiver, fall.Plunge,
                "The fall of " + _elements[fall.Channel].Name + " lands " + (-inside).ToString("F1") +
                " m outside " + _elements[fall.Receiver].Name + ".");
        }
        #endregion

        #region Elements
        private void AddElements(LoogaWaterBody body)
        {
            if (!body || !body.Spline || body.Spline.Splines.Count == 0) return;

            if (body.Shape == LoogaWaterBody.WaterShape.Area)
            {
                AddBody(body);
                return;
            }
            for (int splineIndex = 0; splineIndex < body.Spline.Splines.Count; splineIndex++)
            {
                AddChannel(body, splineIndex);
            }
        }

        private void AddBody(LoogaWaterBody body)
        {
            var element = new WaterNetworkElement(body, 0, body.name) { Level = body.transform.position.y };
            Matrix4x4 splineToWorld = body.Spline.transform.localToWorldMatrix;
            Rect bounds = default;
            foreach (Spline spline in body.Spline.Splines)
            {
                float length = spline.CalculateLength(splineToWorld);
                WaterSplines.Sample(spline, splineToWorld, Mathf.Max(OutlineSpacing, length / MaximumOutlinePoints),
                    _points);
                if (_points.Count < 3) continue;

                var outline = new List<Vector2>(_points.Count);
                foreach (WaterSplinePoint point in _points)
                {
                    outline.Add(new Vector2(point.Position.x, point.Position.z));
                }
                bounds = Encapsulate(bounds, element.OutlineList.Count == 0, outline);
                element.OutlineList.Add(outline);
            }
            if (element.OutlineList.Count == 0) return;

            element.Bounds = bounds;
            _elements.Add(element);
        }

        private void AddChannel(LoogaWaterBody body, int splineIndex)
        {
            Spline spline = body.Spline.Splines[splineIndex];
            WaterSplines.Sample(spline, body.Spline.transform.localToWorldMatrix, _settings.SampleSpacing, _points);
            if (_points.Count < 2) return;

            string name = body.Spline.Splines.Count > 1 ? body.name + " (spline " + splineIndex + ")" : body.name;
            var element = new WaterNetworkElement(body, splineIndex, name);
            IReadOnlyList<Vector2> widthPoints = splineIndex < body.Widths.Count && body.Widths[splineIndex] != null
                ? body.Widths[splineIndex].Points
                : null;
            float station = 0f;
            Vector2 min = new(_points[0].Position.x, _points[0].Position.z);
            Vector2 max = min;
            for (int index = 0; index < _points.Count; index++)
            {
                Vector3 position = _points[index].Position;
                position.y += body.SurfaceOffset;
                Vector2 flat = new(position.x, position.z);
                if (index > 0)
                {
                    Vector3 previous = _points[index - 1].Position;
                    station += Vector2.Distance(new Vector2(previous.x, previous.z), flat);
                }
                min = Vector2.Min(min, flat);
                max = Vector2.Max(max, flat);
                element.SampleList.Add(new WaterChannelSample
                {
                    Position = position,
                    Knot = _points[index].Knot,
                    Station = station,
                    Width = 2f * Mathf.Max(0.05f, WaterSplines.Width(widthPoints, _points[index].Knot, body.Width)),
                    Depth = body.BedDepth
                });
            }
            element.Length = station;
            element.Bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            _elements.Add(element);
        }

        private static Rect Encapsulate(Rect bounds, bool first, List<Vector2> outline)
        {
            Vector2 min = first ? outline[0] : bounds.min;
            Vector2 max = first ? outline[0] : bounds.max;
            foreach (Vector2 point in outline)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }
        #endregion

        #region Flow
        // Sets the slope, the angle and the Manning speed of each point. Returns the median Manning discharge of the
        // points that are steep enough for Manning, or 0 when the channel is flat.
        private float SolveManning(WaterNetworkElement element)
        {
            List<WaterChannelSample> samples = element.SampleList;
            int count = samples.Count;
            float half = _settings.SlopeWindow * 0.5f;
            int back = 0;
            int ahead = 0;
            _median.Clear();
            for (int index = 0; index < count; index++)
            {
                // Back: the last point at least half a window upstream. Ahead: the first point half a window downstream.
                while (back < index && samples[index].Station - samples[back + 1].Station >= half)
                {
                    back++;
                }
                ahead = Mathf.Max(ahead, index);
                while (ahead < count - 1 && samples[ahead].Station - samples[index].Station < half)
                {
                    ahead++;
                }
                WaterChannelSample sample = samples[index];
                float span = samples[ahead].Station - samples[back].Station;
                sample.Slope = span > 1e-3f ? (samples[back].Position.y - samples[ahead].Position.y) / span : 0f;
                int next = Mathf.Min(index + 1, count - 1);
                int previous = next == index ? index - 1 : index;
                float run = samples[next].Station - samples[previous].Station;
                float drop = samples[previous].Position.y - samples[next].Position.y;
                sample.Angle = Mathf.Atan2(drop, Mathf.Max(run, 1e-4f)) * Mathf.Rad2Deg;
                sample.Speed = ManningSpeed(element.Body.Roughness, sample.Width, sample.Depth, sample.Slope);
                samples[index] = sample;
                if (sample.Slope >= FlowSlope) _median.Add(sample.Speed * sample.Width * sample.Depth);
            }
            if (_median.Count == 0) return 0f;

            _median.Sort();
            return _median[_median.Count / 2];
        }

        // Manning: V = R^(2/3) * S^(1/2) / n, with R the hydraulic radius of a rectangular section. The steep-stream
        // roughness of Jarrett (1984), n = 0.39 * S^0.38 * R^-0.16, replaces a lower channel roughness.
        private float ManningSpeed(float roughness, float width, float depth, float slope)
        {
            if (slope <= 0f || depth <= 0f) return 0f;

            float radius = width * depth / (width + 2f * depth);
            float n = Mathf.Max(roughness, 1e-3f);
            if (_settings.SteepRoughness)
            {
                n = Mathf.Max(n, 0.39f * Mathf.Pow(slope, 0.38f) * Mathf.Pow(radius, -0.16f));
            }
            return Mathf.Pow(radius, 2f / 3f) * Mathf.Sqrt(slope) / n;
        }

        private void SolveDischarge()
        {
            _dischargeState.Clear();
            for (int index = 0; index < _elements.Count; index++)
            {
                _dischargeState.Add(0);
            }
            for (int index = 0; index < _elements.Count; index++)
            {
                ResolveDischarge(index);
            }
        }

        // A channel carries its own Manning discharge or the inflow from upstream, whichever is larger. A body passes
        // its inflow on in equal shares to the channels that leave it. A split branch takes its own Manning discharge.
        private float ResolveDischarge(int index)
        {
            WaterNetworkElement element = _elements[index];
            if (_dischargeState[index] == 2) return element.Discharge;
            // A loop in the network. The validation reports the rise that makes it.
            if (_dischargeState[index] == 1) return 0f;

            _dischargeState[index] = 1;
            float inflow = 0f;
            foreach (WaterNetworkConnection connection in _connections)
            {
                if (connection.Downstream != index) continue;

                if (connection.Joint == WaterJoint.BodyToChannel)
                {
                    inflow += ResolveDischarge(connection.Upstream) / CountOutflows(connection.Upstream);
                }
                else if (connection.Joint != WaterJoint.Split)
                {
                    inflow += ResolveDischarge(connection.Upstream);
                }
            }
            element.Inflow = inflow;
            element.Discharge = element.IsChannel ? Mathf.Max(_ownDischarge[index], inflow) : inflow;
            _dischargeState[index] = 2;
            if (element.IsChannel) CheckContinuity(index);
            return element.Discharge;
        }

        // A channel must carry the water that comes in. A Manning discharge far from the inflow shows a channel that is
        // too wide, too deep or too steep for its water, or the opposite. A flat channel has no Manning discharge.
        private void CheckContinuity(int index)
        {
            WaterNetworkElement element = _elements[index];
            float own = _ownDischarge[index];
            if (element.Inflow <= 0f || own <= 0f) return;

            float ratio = own / element.Inflow;
            if (ratio <= DischargeConflict && ratio >= 1f / DischargeConflict) return;

            AddIssue(WaterIssueSeverity.Warning, index, element.SampleList[0].Position,
                "The Manning discharge " + own.ToString("F1") + " m³/s is " + ratio.ToString("F2") +
                "x the inflow of " + element.Inflow.ToString("F1") + " m³/s. Check the width, the bed depth or the " +
                "slope.");
        }

        private int CountOutflows(int body)
        {
            int count = 0;
            foreach (WaterNetworkConnection connection in _connections)
            {
                if (connection.Upstream == body && connection.Joint == WaterJoint.BodyToChannel) count++;
            }
            return Mathf.Max(count, 1);
        }

        // Sets the final speed, discharge, Froude number and regime of each point. Reports rises and conflicts.
        private void FinishChannel(int index)
        {
            WaterNetworkElement element = _elements[index];
            List<WaterChannelSample> samples = element.SampleList;
            float discharge = element.Discharge;
            int conflictStart = -1;
            float conflictLow = float.MaxValue;
            float conflictHigh = 0f;
            for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
            {
                WaterChannelSample sample = samples[sampleIndex];
                float area = sample.Width * sample.Depth;
                float manning = sample.Speed * area;
                // The water cannot carry the channel discharge slower than the discharge over the section area.
                sample.Speed = Mathf.Max(sample.Speed, area > 0f ? discharge / area : 0f);
                sample.Discharge = sample.Speed * area;
                sample.Froude = sample.Depth > 0f ? sample.Speed / Mathf.Sqrt(Gravity * sample.Depth) : 0f;
                sample.Regime = Classify(sample);
                SetRegimeValues(ref sample);
                samples[sampleIndex] = sample;
                if (sampleIndex < samples.Count - 1)
                {
                    element.RegimeLengthArray[(int)sample.Regime] +=
                        samples[sampleIndex + 1].Station - sample.Station;
                }

                float ratio = discharge > 0f ? manning / discharge : 1f;
                bool conflict = sample.Slope >= FlowSlope &&
                    (ratio > DischargeConflict || ratio < 1f / DischargeConflict);
                if (conflict)
                {
                    if (conflictStart < 0) conflictStart = sampleIndex;
                    conflictLow = Mathf.Min(conflictLow, ratio);
                    conflictHigh = Mathf.Max(conflictHigh, ratio);
                }
                bool runEnds = conflictStart >= 0 && (!conflict || sampleIndex == samples.Count - 1);
                if (!runEnds) continue;

                AddConflict(index, conflictStart, sampleIndex, conflictLow, conflictHigh);
                conflictStart = -1;
                conflictLow = float.MaxValue;
                conflictHigh = 0f;
            }
            SmoothRegimeValues(samples);
            ReportRises(index);
        }

        // Continuous values for the shading. They use the regime thresholds, so one setting moves both the regime and
        // the look. The shader gets these values and not the regime index.
        private void SetRegimeValues(ref WaterChannelSample sample)
        {
            if (sample.Regime == WaterRegime.FreeFall)
            {
                sample.Turbulence = 1f;
                sample.Aeration = 1f;
                return;
            }
            if (sample.Regime == WaterRegime.Still) return;

            float rapidsFroude = Mathf.Max(_settings.RapidsFroude, _settings.FastFroude + 0.01f);
            float rapidsSlope = Mathf.Max(_settings.RapidsSlope, 1e-4f);
            sample.Turbulence = Mathf.Max(
                Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(_settings.FastFroude, rapidsFroude, sample.Froude)),
                Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f * rapidsSlope, rapidsSlope, sample.Slope)));
            sample.Aeration = Mathf.Max(
                Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(rapidsFroude, 1.5f * rapidsFroude, sample.Froude)),
                Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(rapidsSlope,
                    Mathf.Max(_settings.CascadeSlope, rapidsSlope + 1e-3f), sample.Slope)));
        }

        // Averages the turbulence and the aeration over the slope window, so a slope change along a channel gives a
        // smooth change of the look and not a step at a regime boundary.
        private void SmoothRegimeValues(List<WaterChannelSample> samples)
        {
            int count = samples.Count;
            int reach = Mathf.Max(1, Mathf.RoundToInt(0.5f * _settings.SlopeWindow / _settings.SampleSpacing));
            _smoothTurbulence.Clear();
            _smoothAeration.Clear();
            for (int index = 0; index < count; index++)
            {
                float turbulence = 0f;
                float aeration = 0f;
                int first = Mathf.Max(0, index - reach);
                int last = Mathf.Min(count - 1, index + reach);
                for (int other = first; other <= last; other++)
                {
                    turbulence += samples[other].Turbulence;
                    aeration += samples[other].Aeration;
                }
                int used = last - first + 1;
                _smoothTurbulence.Add(turbulence / used);
                _smoothAeration.Add(aeration / used);
            }
            for (int index = 0; index < count; index++)
            {
                WaterChannelSample sample = samples[index];
                sample.Turbulence = _smoothTurbulence[index];
                sample.Aeration = _smoothAeration[index];
                samples[index] = sample;
            }
        }

        private WaterRegime Classify(WaterChannelSample sample)
        {
            if (sample.Angle >= _settings.FallAngle) return WaterRegime.FreeFall;
            if (sample.Speed < _settings.StillSpeed) return WaterRegime.Still;
            if (sample.Slope >= _settings.CascadeSlope) return WaterRegime.Cascade;
            if (sample.Froude >= _settings.RapidsFroude || sample.Slope >= _settings.RapidsSlope)
            {
                return WaterRegime.Rapids;
            }
            return sample.Froude >= _settings.FastFroude ? WaterRegime.Fast : WaterRegime.Flowing;
        }

        private void AddConflict(int element, int first, int last, float low, float high)
        {
            List<WaterChannelSample> samples = _elements[element].SampleList;
            float length = samples[last].Station - samples[first].Station;
            if (length < ConflictLength) return;

            // The run holds only conflict points, so low >= 1 means that all points carry too much water.
            string range = low >= 1f ? "up to " + high.ToString("F1") + "x" :
                high <= 1f ? "down to " + low.ToString("F2") + "x" :
                low.ToString("F2") + "x to " + high.ToString("F1") + "x";
            AddIssue(WaterIssueSeverity.Warning, element, samples[(first + last) / 2].Position,
                "The Manning discharge is " + range + " the channel discharge over " + length.ToString("F0") +
                " m. Check the width, the bed depth or the slope.");
        }

        // The water surface must not rise above a lower point upstream. Each rise run gives one error at its peak.
        private void ReportRises(int element)
        {
            List<WaterChannelSample> samples = _elements[element].SampleList;
            float lowest = samples[0].Position.y;
            float peak = 0f;
            int peakIndex = -1;
            for (int index = 1; index <= samples.Count; index++)
            {
                float rise = index < samples.Count ? samples[index].Position.y - lowest : 0f;
                if (rise > _settings.HeightTolerance)
                {
                    if (rise > peak)
                    {
                        peak = rise;
                        peakIndex = index;
                    }
                    continue;
                }
                if (peakIndex >= 0)
                {
                    AddIssue(WaterIssueSeverity.Error, element, samples[peakIndex].Position,
                        "The water height rises " + peak.ToString("F2") + " m downstream at " +
                        samples[peakIndex].Station.ToString("F0") + " m.");
                    peak = 0f;
                    peakIndex = -1;
                }
                if (index < samples.Count) lowest = Mathf.Min(lowest, samples[index].Position.y);
            }
        }
        #endregion

        #region Connections
        // Ends connect first, so a start that is already the downstream side of a series joint is skipped.
        private void Connect()
        {
            for (int index = 0; index < _elements.Count; index++)
            {
                if (_elements[index].IsChannel) ConnectEnd(index);
            }
            for (int index = 0; index < _elements.Count; index++)
            {
                if (_elements[index].IsChannel) ConnectStart(index);
            }
        }

        private void ConnectEnd(int channel)
        {
            List<WaterChannelSample> samples = _elements[channel].SampleList;
            WaterChannelSample end = samples[samples.Count - 1];
            int last = samples.Count - 1;
            if (TryFindChannelStart(channel, end.Position, out int next))
            {
                WaterChannelSample start = _elements[next].SampleList[0];
                AddConnection(WaterJoint.Series, channel, last, next, 0, end.Position,
                    end.Position.y - start.Position.y, end.Width);
                return;
            }
            if (TryFindChannelPoint(channel, end.Position, out int main, out int mainSample))
            {
                AddConnection(WaterJoint.Confluence, channel, last, main, mainSample, end.Position,
                    end.Position.y - _elements[main].SampleList[mainSample].Position.y, end.Width);
                return;
            }
            if (TryFindBody(end.Position, true, out int body))
            {
                AddConnection(WaterJoint.ChannelToBody, channel, last, body, -1, end.Position,
                    end.Position.y - _elements[body].Level, end.Width);
                return;
            }
            AddIssue(WaterIssueSeverity.Warning, channel, end.Position, "The channel end connects to no water.");
        }

        private void ConnectStart(int channel)
        {
            foreach (WaterNetworkConnection connection in _connections)
            {
                if (connection.Joint == WaterJoint.Series && connection.Downstream == channel) return;
            }
            WaterChannelSample start = _elements[channel].SampleList[0];
            if (TryFindChannelPoint(channel, start.Position, out int main, out int mainSample))
            {
                Vector3 point = _elements[main].SampleList[mainSample].Position;
                AddConnection(WaterJoint.Split, main, mainSample, channel, 0, point,
                    point.y - start.Position.y, start.Width);
                return;
            }
            if (TryFindBody(start.Position, false, out int body))
            {
                float level = _elements[body].Level;
                AddConnection(WaterJoint.BodyToChannel, body, -1, channel, 0,
                    new Vector3(start.Position.x, level, start.Position.z), level - start.Position.y, start.Width);
                return;
            }
            AddIssue(WaterIssueSeverity.Info, channel, start.Position,
                "The channel starts with no water upstream, as at a spring.");
        }

        private void AddConnection(WaterJoint joint, int upstream, int upstreamSample, int downstream,
            int downstreamSample, Vector3 point, float drop, float width)
        {
            _connections.Add(new WaterNetworkConnection
            {
                Joint = joint,
                Upstream = upstream,
                Downstream = downstream,
                UpstreamSample = _elements[upstream].IsChannel ? upstreamSample : -1,
                DownstreamSample = _elements[downstream].IsChannel ? downstreamSample : -1,
                Point = point,
                Landing = GetLanding(downstream, downstreamSample, point),
                Drop = drop,
                Width = width,
                Fall = drop >= _settings.FallDrop
            });
        }

        // A channel takes the water at its joint point. A body takes it below the joint, or at the nearest point of
        // its outline when the joint is outside the outline.
        private Vector3 GetLanding(int downstream, int downstreamSample, Vector3 point)
        {
            WaterNetworkElement element = _elements[downstream];
            if (element.IsChannel) return element.SampleList[downstreamSample].Position;

            Vector2 flat = Flat(point);
            if (!IsInside(element, flat)) NearestOutlinePoint(element, flat, out flat);
            return new Vector3(flat.x, element.Level, flat.y);
        }

        // The nearest channel start within the snap distance of the point.
        private bool TryFindChannelStart(int channel, Vector3 point, out int found)
        {
            found = -1;
            float best = _settings.SnapDistance;
            for (int index = 0; index < _elements.Count; index++)
            {
                if (index == channel || !_elements[index].IsChannel) continue;

                float distance = FlatDistance(point, _elements[index].SampleList[0].Position);
                if (distance > best) continue;

                best = distance;
                found = index;
            }
            return found >= 0;
        }

        // The nearest point of another channel whose water is within the snap distance of the point.
        private bool TryFindChannelPoint(int channel, Vector3 point, out int found, out int foundSample)
        {
            found = -1;
            foundSample = -1;
            float best = float.MaxValue;
            for (int index = 0; index < _elements.Count; index++)
            {
                WaterNetworkElement element = _elements[index];
                if (index == channel || !element.IsChannel) continue;
                if (!Expand(element.Bounds, _settings.SnapDistance + MaximumHalfWidth(element)).Contains(Flat(point)))
                {
                    continue;
                }
                for (int sampleIndex = 0; sampleIndex < element.SampleList.Count; sampleIndex++)
                {
                    WaterChannelSample sample = element.SampleList[sampleIndex];
                    // Distance past the water edge of the other channel.
                    float gap = FlatDistance(point, sample.Position) - 0.5f * sample.Width;
                    if (gap > _settings.SnapDistance || gap >= best) continue;

                    best = gap;
                    found = index;
                    foundSample = sampleIndex;
                }
            }
            return found >= 0;
        }

        // A channel end falls into the highest body that is not above it. A channel start leaves the lowest body that
        // is not below it. Without such a body, the body with the nearest level wins, and the validation reports it.
        // Falling water lands away from the lip, as below an ogee spillway face or a jet. So a channel end that is a
        // fall above a body reaches it within the snap distance plus the drop height.
        private bool TryFindBody(Vector3 point, bool channelEnd, out int found)
        {
            found = -1;
            int nearest = -1;
            float nearestGap = float.MaxValue;
            float bestLevel = channelEnd ? float.MinValue : float.MaxValue;
            Vector2 flat = Flat(point);
            for (int index = 0; index < _elements.Count; index++)
            {
                WaterNetworkElement element = _elements[index];
                if (element.IsChannel) continue;

                float level = element.Level;
                float drop = point.y - level;
                float reach = _settings.SnapDistance + (channelEnd && drop >= _settings.FallDrop ? drop : 0f);
                if (!Expand(element.Bounds, reach).Contains(flat)) continue;
                if (!IsInside(element, flat) && OutlineDistance(element, flat) > reach) continue;

                float gap = Mathf.Abs(level - point.y);
                if (gap < nearestGap)
                {
                    nearestGap = gap;
                    nearest = index;
                }
                bool fits = channelEnd
                    ? level <= point.y + _settings.HeightTolerance && level > bestLevel
                    : level >= point.y - _settings.HeightTolerance && level < bestLevel;
                if (!fits) continue;

                bestLevel = level;
                found = index;
            }
            if (found < 0) found = nearest;
            return found >= 0;
        }

        private void ValidateJoints()
        {
            foreach (WaterNetworkConnection connection in _connections)
            {
                if (connection.Drop >= -_settings.HeightTolerance) continue;

                AddIssue(WaterIssueSeverity.Error, connection.Downstream, connection.Point,
                    "The water rises " + (-connection.Drop).ToString("F2") + " m from " +
                    _elements[connection.Upstream].Name + " to " + _elements[connection.Downstream].Name + " (" +
                    connection.Joint + ").");
            }
        }

        private void AddIssue(WaterIssueSeverity severity, int element, Vector3 position, string message)
        {
            _issues.Add(new WaterNetworkIssue
            {
                Severity = severity,
                Element = element,
                Position = position,
                Message = message
            });
        }
        #endregion

        #region Geometry
        // Outline segments in one chunk of the outline tests. The points of an outline are in order, so a chunk is small.
        private const int OutlineChunk = 32;
        // Margin of the chunk skip in the distance test, in metres and as a share of the distance. It is larger than the
        // float rounding of world coordinates, so a skipped chunk cannot have a nearer segment.
        private const float ChunkSkipMargin = 0.01f;
        private const float ChunkSkipShare = 1e-4f;

        // Even-odd test over all outlines, as the water triangulator fills them. Segment index joins the point before
        // it (the last point for index 0) to the point at index. A chunk whose points are all above or all below the
        // point height has no segment that changes the result, so the test skips it. The sea outline has about 15000
        // points.
        private static bool IsInside(WaterNetworkElement element, Vector2 point)
        {
            Vector4[] chunks = OutlineChunks(element);
            bool inside = false;
            int chunk = 0;
            foreach (List<Vector2> outline in element.OutlineList)
            {
                for (int start = 0; start < outline.Count; start += OutlineChunk, chunk++)
                {
                    Vector4 bounds = chunks[chunk];
                    if (point.y < bounds.y || point.y > bounds.w) continue;

                    int end = Mathf.Min(start + OutlineChunk, outline.Count);
                    for (int index = start; index < end; index++)
                    {
                        Vector2 a = outline[index];
                        Vector2 b = outline[index == 0 ? outline.Count - 1 : index - 1];
                        if (a.y > point.y == b.y > point.y) continue;
                        if (point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
                    }
                }
            }
            return inside;
        }

        // The same value as NearestOutlinePoint. The chunk nearest by its bounds sets a first distance, and the test
        // skips each chunk whose bounds are farther than the nearest distance so far.
        private static float OutlineDistance(WaterNetworkElement element, Vector2 point)
        {
            Vector4[] chunks = OutlineChunks(element);
            int nearestChunk = 0;
            float nearestBounds = float.MaxValue;
            for (int chunk = 0; chunk < chunks.Length; chunk++)
            {
                float distance = BoundsDistance(chunks[chunk], point);
                if (distance >= nearestBounds) continue;

                nearestBounds = distance;
                nearestChunk = chunk;
            }
            float best = float.MaxValue;
            for (int pass = 0; pass < 2; pass++)
            {
                int chunk = 0;
                foreach (List<Vector2> outline in element.OutlineList)
                {
                    for (int start = 0; start < outline.Count; start += OutlineChunk, chunk++)
                    {
                        if (pass == 0 && chunk != nearestChunk) continue;
                        if (pass == 1 && (chunk == nearestChunk ||
                            BoundsDistance(chunks[chunk], point) > best + ChunkSkipMargin + ChunkSkipShare * best))
                        {
                            continue;
                        }

                        int end = Mathf.Min(start + OutlineChunk, outline.Count);
                        for (int index = start; index < end; index++)
                        {
                            Vector2 previous = outline[index == 0 ? outline.Count - 1 : index - 1];
                            float distance = Vector2.Distance(point, SegmentPoint(point, previous, outline[index]));
                            if (distance < best) best = distance;
                        }
                    }
                }
            }
            return best;
        }

        // The bounds of each chunk of outline segments, with both points of each segment.
        private static Vector4[] OutlineChunks(WaterNetworkElement element)
        {
            if (element.OutlineChunks != null) return element.OutlineChunks;

            var chunks = new List<Vector4>();
            foreach (List<Vector2> outline in element.OutlineList)
            {
                for (int start = 0; start < outline.Count; start += OutlineChunk)
                {
                    int end = Mathf.Min(start + OutlineChunk, outline.Count);
                    Vector2 min = outline[start == 0 ? outline.Count - 1 : start - 1];
                    Vector2 max = min;
                    for (int index = start; index < end; index++)
                    {
                        min = Vector2.Min(min, outline[index]);
                        max = Vector2.Max(max, outline[index]);
                    }
                    chunks.Add(new Vector4(min.x, min.y, max.x, max.y));
                }
            }
            element.OutlineChunks = chunks.ToArray();
            return element.OutlineChunks;
        }

        // Distance from a point to the bounds of a chunk. 0 inside the bounds.
        private static float BoundsDistance(Vector4 bounds, Vector2 point)
        {
            float x = Mathf.Max(bounds.x - point.x, 0f, point.x - bounds.z);
            float y = Mathf.Max(bounds.y - point.y, 0f, point.y - bounds.w);
            return Mathf.Sqrt(x * x + y * y);
        }

        // Returns the distance to the nearest point of the outlines.
        private static float NearestOutlinePoint(WaterNetworkElement element, Vector2 point, out Vector2 nearest)
        {
            float best = float.MaxValue;
            nearest = point;
            foreach (List<Vector2> outline in element.OutlineList)
            {
                for (int index = 0, previous = outline.Count - 1; index < outline.Count; previous = index++)
                {
                    Vector2 candidate = SegmentPoint(point, outline[previous], outline[index]);
                    float distance = Vector2.Distance(point, candidate);
                    if (distance >= best) continue;

                    best = distance;
                    nearest = candidate;
                }
            }
            return best;
        }

        private static Vector2 SegmentPoint(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 along = b - a;
            float lengthSquared = along.sqrMagnitude;
            float t = lengthSquared > 1e-8f ? Mathf.Clamp01(Vector2.Dot(point - a, along) / lengthSquared) : 0f;
            return a + along * t;
        }

        private static float MaximumHalfWidth(WaterNetworkElement element)
        {
            float width = 0f;
            foreach (WaterChannelSample sample in element.SampleList)
            {
                width = Mathf.Max(width, sample.Width);
            }
            return 0.5f * width;
        }

        private static Rect Expand(Rect rect, float margin)
        {
            return Rect.MinMaxRect(rect.xMin - margin, rect.yMin - margin, rect.xMax + margin, rect.yMax + margin);
        }

        private static Vector2 Flat(Vector3 point)
        {
            return new Vector2(point.x, point.z);
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            return Vector2.Distance(Flat(a), Flat(b));
        }
        #endregion
    }
}
