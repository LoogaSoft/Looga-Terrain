using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Places the water effect emitters from a solved <see cref="WaterNetworkGraph"/> (falls and crests) and from the
    /// <see cref="LoogaWaterEffectFall"/> components of the effects. Each fall gets fog, spray and splash at the plunge
    /// and mist down its face. A tall fall also gets a rising plume. Rapids and cascades get low spray where the
    /// aeration is high. The size and the rate scale with the drop height and the flow. The particle budget limits the
    /// total count.
    /// </summary>
    internal static class WaterEffectPlanner
    {
        private const float Gravity = 9.81f;
        // The fall that the recipes were tuned on: the coast spillway dam (drop, flow per metre, station width).
        private const float ReferenceDrop = 22.6f;
        private const float ReferenceUnitDischarge = 2.79f;
        private const float ReferencePitch = 11.1f;
        // Largest station width of a fall without bays at the reference reach, in metres.
        private const float StationWidth = 12f;
        // Face kinds of a tall fall lie in slices of at most this height down the face, in metres, and at most this
        // many slices.
        private const float FaceSliceHeight = 12f;
        private const int MaximumFaceSlices = 8;
        // Largest box width of a station on a fall without bays, as a share of the station width.
        private const float ContinuousBox = 1.15f;
        // Largest opacity of the fog bank of a tall fall.
        private const float MaximumFogAlpha = 0.5f;
        // Rapids spray stays this far from a fall lip, in metres. The fall effects cover it.
        private const float LipClearance = 15f;
        // Water fade of the rapids spray, in metres. It sits close to the water.
        private const float RapidsWaterFade = 0.6f;
        private static readonly Color MistColor = new(0.96f, 0.97f, 0.98f, 1f);

        private static readonly List<WaterEffectEmitter> Planned = new();
        private static readonly List<Vector3> Lips = new();
        private static readonly List<Vector3> Landings = new();

        // The fall kinds of the spillway dam effects. Heights of the plunge kinds are metres above the water; heights
        // and box heights of the face kinds are shares of the drop. Box widths are shares of the station width.
        private static readonly Recipe[] FallRecipes =
        {
            new()
            {
                Kind = WaterEffectKind.FogBank, Rate = 11f, Life = new Vector2(6f, 9f), Size = new Vector2(10f, 18f),
                Growth = new Vector2(0.7f, 1.6f), Box = new Vector3(1.92f, 1f, 10f),
                VelocityMin = new Vector3(-0.6f, 0.05f, 0.2f), VelocityMax = new Vector3(0.6f, 0.4f, 0.7f),
                Drift = 0.4f, Alpha = 0.2f, Height = 0.6f, Offset = 2.8f, RiseWithReach = true,
                SpreadWithReach = true
            },
            // The plume starts low in the plunge cloud and rises out of it. It shows only at full opacity a few
            // metres above its start, so a higher start left a gap above the plunge cloud.
            new()
            {
                Kind = WaterEffectKind.RisingPlume, Rate = 4.5f, Life = new Vector2(3.5f, 5.9f),
                Size = new Vector2(24f, 41f), Growth = new Vector2(0.7f, 1.6f), Box = new Vector3(1.15f, 6f, 4.2f),
                VelocityMin = new Vector3(-0.5f, 3.4f, 0.5f), VelocityMax = new Vector3(0.5f, 5.9f, 1.5f),
                Drift = 0.7f, Alpha = 0.28f, Height = 4f, Offset = 7f, MinimumDrop = 60f
            },
            new()
            {
                Kind = WaterEffectKind.FaceMistUpper, Rate = 6f, Life = new Vector2(2f, 3f), Size = new Vector2(4f, 8f),
                Growth = new Vector2(0.7f, 1.6f), Box = new Vector3(1.22f, 0.265f, 2f),
                VelocityMin = new Vector3(-0.4f, -3f, 0.3f), VelocityMax = new Vector3(0.4f, -1f, 1f),
                Drift = 0.5f, Alpha = 0.07f, Height = 0.48f, Offset = 2f, OnFace = true
            },
            new()
            {
                Kind = WaterEffectKind.FaceMistLower, Rate = 9f, Life = new Vector2(2.5f, 3.5f),
                Size = new Vector2(6f, 12f), Growth = new Vector2(0.7f, 1.6f), Box = new Vector3(1.35f, 0.354f, 3f),
                VelocityMin = new Vector3(-0.5f, -2f, 0.5f), VelocityMax = new Vector3(0.5f, 0f, 1.5f),
                Drift = 0.6f, Alpha = 0.16f, Height = 0.21f, Offset = 2f, OnFace = true
            },
            new()
            {
                Kind = WaterEffectKind.FaceSprayFine, Rate = 7f, Life = new Vector2(1.8f, 2.6f),
                Size = new Vector2(1.2f, 2.5f), Growth = new Vector2(0.6f, 3f), Box = new Vector3(1.15f, 0.53f, 1.5f),
                VelocityMin = new Vector3(-0.3f, -5f, 0.4f), VelocityMax = new Vector3(0.3f, -2.5f, 1.2f),
                Drift = 0.4f, Alpha = 0.14f, Height = 0.37f, Offset = 4f, OnFace = true
            },
            new()
            {
                Kind = WaterEffectKind.JetSpray, Rate = 24f, Life = new Vector2(2.5f, 4f), Size = new Vector2(6f, 12f),
                Growth = new Vector2(0.7f, 1.6f), Box = new Vector3(1.22f, 3f, 4f),
                VelocityMin = new Vector3(-0.8f, 0.6f, 1f), VelocityMax = new Vector3(0.8f, 2.2f, 3f),
                Drift = 0.6f, Alpha = 0.32f, Height = 1.5f, Offset = 0.7f, RiseWithReach = true
            },
            new()
            {
                Kind = WaterEffectKind.ImpactSplash, Rate = 16f, Life = new Vector2(1.2f, 2f),
                Size = new Vector2(2.5f, 5f), Growth = new Vector2(0.7f, 1.6f), Box = new Vector3(1.22f, 0f, 2f),
                VelocityMin = new Vector3(-0.8f, 2f, 1f), VelocityMax = new Vector3(0.8f, 6f, 3f),
                GravityModifier = 0.5f, Drift = 0.6f, Alpha = 0.4f, Height = 0.5f, Offset = 0.4f
            }
        };

        private struct Recipe
        {
            public WaterEffectKind Kind;
            // Particles per second of one reference station.
            public float Rate;
            public Vector2 Life;
            public Vector2 Size;
            public Vector2 Growth;
            public Vector3 Box;
            // Velocity across, up and downstream, in m/s.
            public Vector3 VelocityMin;
            public Vector3 VelocityMax;
            public float GravityModifier;
            public float Drift;
            public float Alpha;
            public float Height;
            // Downstream distance from the plunge, or from the falling water for a face kind, in metres.
            public float Offset;
            public bool OnFace;
            // Drop from which the kind shows, in metres. It is full at 2.5 times this drop. 0 = always.
            public float MinimumDrop;
            // The rise speed grows with the reach, not with its square root. The base cloud of a tall fall then
            // grows as tall as the fall needs.
            public bool RiseWithReach;
            // The cloud reaches farther downstream on a tall fall: the plunge distance and the box length grow with
            // reach^1.5 and the downstream speed with the reach. The count grows with the square root of the reach,
            // so the longer cloud keeps its density.
            public bool SpreadWithReach;
        }

        /// <summary>Fills the emitters in drawing order. Returns the particle count before the budget.</summary>
        public static int Plan(LoogaWaterNetwork network, LoogaWaterEffects effects, List<WaterEffectEmitter> emitters)
        {
            Planned.Clear();
            WaterNetworkGraph graph = network.SolvedGraph;
            if (effects.FallScale > 0f)
            {
                foreach (WaterNetworkFall fall in graph.Falls)
                {
                    // A fall shows effects only when it shows a sheet.
                    bool shown = fall.Override ? fall.Override.IsShown : network.FallMaterial != null;
                    if (shown) PlanFall(fall, effects);
                }
                foreach (WaterNetworkCrest crest in graph.Crests)
                {
                    if (crest.Fall && crest.Fall.IsShown) PlanCrest(crest, effects);
                }
                foreach (LoogaWaterEffectFall fall in LoogaWaterEffectFall.Active)
                {
                    if (fall && fall.Effects == effects && fall.RateScale > 0f) PlanEffectFall(fall, effects);
                }
            }
            if (effects.RapidsScale > 0f)
            {
                foreach (WaterNetworkElement element in graph.Elements)
                {
                    if (element.IsChannel) PlanRapids(element, graph, effects);
                }
            }
            int requested = ApplyBudget(effects.ParticleBudget);

            // Drawing order: by kind, so the low fog draws first and the splash last.
            emitters.Clear();
            for (int kind = 0; kind <= (int)WaterEffectKind.RapidsSpray; kind++)
            {
                foreach (WaterEffectEmitter emitter in Planned)
                {
                    if ((int)emitter.Kind == kind && emitter.Count > 0) emitters.Add(emitter);
                }
            }
            Planned.Clear();
            return requested;
        }

        #region Falls
        // One place along a fall lip where the fall emitters stand.
        private struct FallStation
        {
            // Where the falling water meets the water below, at the level of that water.
            public Vector3 Plunge;
            public Vector3 Across;
            // Horizontal unit vector downstream.
            public Vector3 Forward;
            // Station width, in metres.
            public float Pitch;
            // Drop from the upper water level to the water below, in metres.
            public float Drop;
            // Largest box width as a share of the pitch. The wide fog of a bay reaches over the piers, but on a
            // continuous fall it would reach past the fall ends.
            public float MaximumBox;
        }

        private static void PlanFall(WaterNetworkFall fall, LoogaWaterEffects effects)
        {
            WaterFallEdge edge = fall.Edge;
            if (fall.PathList.Count < 2 || fall.PlungeIndex < 0) return;

            Matrix4x4 frame = edge.FrameToWorld;
            Vector3 across = frame.MultiplyVector(Vector3.right).normalized;
            Vector3 forward = frame.MultiplyVector(Vector3.forward);
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            float scale = Mathf.Max(edge.Scale, 1e-4f);
            float plungeDistance = fall.PathList[fall.PlungeIndex].Surface.x;
            float reach = Reach(fall.Drop);

            // One station per bay, or stations of at most the station width over a fall without bays. Positions are
            // in frame units across the lip.
            int bays = Mathf.Max(1, edge.Bays);
            float pier = Mathf.Max(0f, edge.PierWidth);
            float bayWidth = Mathf.Max(0.1f, (edge.Width - (bays + 1) * pier) / bays);
            int stations = bays;
            float pitch = bayWidth + pier;
            if (bays == 1)
            {
                stations = Mathf.Max(1, Mathf.CeilToInt(edge.Width * scale / (StationWidth * reach)));
                pitch = edge.Width / stations;
            }
            for (int station = 0; station < stations; station++)
            {
                float x = bays == 1
                    ? -0.5f * edge.Width + (station + 0.5f) * pitch
                    : -0.5f * edge.Width + pier + station * (bayWidth + pier) + 0.5f * bayWidth;
                var place = new FallStation
                {
                    Plunge = frame.MultiplyPoint3x4(new Vector3(x, edge.Landing, plungeDistance)),
                    Across = across,
                    Forward = forward,
                    Pitch = pitch * scale,
                    Drop = fall.Drop,
                    MaximumBox = bays == 1 ? ContinuousBox : float.MaxValue
                };
                // A point of the falling water at a share of the drop above the water below.
                Vector3 Face(float share)
                {
                    float height = edge.Landing + share * (edge.Level - edge.Landing);
                    return frame.MultiplyPoint3x4(new Vector3(x, height,
                        FaceDistance(fall.PathList, fall.PlungeIndex, height)));
                }
                AddFallEmitters(place, Face, fall.UnitDischarge, effects.FallScale, effects);
            }
        }

        // An effect fall gives its lip and landing points. The water falls on a parabola from the lip to the landing:
        // the horizontal distance grows with the square root of the height that the water fell.
        private static void PlanEffectFall(LoogaWaterEffectFall fall, LoogaWaterEffects effects)
        {
            fall.GetPath(Lips, Landings);
            PlanLip(share => Mathf.Sqrt(Mathf.Clamp01(1f - share)), fall.UnitDischarge,
                effects.FallScale * fall.RateScale, effects);
        }

        // A crest falls from each lip point along its normal. The plunge lies the plunge distance of the sheet path out
        // from the lip, at the level of the water below, and the face follows the sheet path. So the effects follow the
        // sheet, also with a long sheet approach or a round bend over the brink.
        private static void PlanCrest(WaterNetworkCrest crest, LoogaWaterEffects effects)
        {
            if (crest.PlungeIndex < 0 || crest.PlungeDistance <= 0f || crest.Drop <= 0f) return;

            Lips.Clear();
            Landings.Clear();
            for (int index = 0; index < crest.LipList.Count; index++)
            {
                Vector3 lip = crest.LipList[index];
                Vector2 normal = crest.NormalList[index] * crest.PlungeDistance;
                Lips.Add(lip);
                Landings.Add(new Vector3(lip.x + normal.x, crest.Edge.Landing, lip.z + normal.y));
            }
            WaterFallEdge edge = crest.Edge;
            float FaceShare(float share)
            {
                float height = Mathf.Lerp(edge.Landing, edge.Level, share);
                return Mathf.Clamp01(FaceDistance(crest.PathList, crest.PlungeIndex, height) / crest.PlungeDistance);
            }
            PlanLip(FaceShare, crest.UnitDischarge, effects.FallScale, effects);
        }

        // Stations at equal steps along the lip points in Lips, with the landing points in Landings. faceShare gives
        // the share of the horizontal distance from the lip to the landing at a share of the drop above the landing.
        private static void PlanLip(System.Func<float, float> faceShare, float unitDischarge, float rateScale,
            LoogaWaterEffects effects)
        {
            int count = Lips.Count;
            if (count < 2) return;

            float length = 0f;
            float drop = 0f;
            for (int index = 0; index < count; index++)
            {
                drop += (Lips[index].y - Landings[index].y) / count;
                if (index > 0) length += Horizontal(Lips[index] - Lips[index - 1]).magnitude;
            }
            if (drop <= 0f || length <= 0f) return;

            int stations = Mathf.Max(1, Mathf.CeilToInt(length / (StationWidth * Reach(drop))));
            float pitch = length / stations;
            int segment = 1;
            float segmentStart = 0f;
            for (int station = 0; station < stations; station++)
            {
                float target = (station + 0.5f) * pitch;
                float segmentLength = Horizontal(Lips[segment] - Lips[segment - 1]).magnitude;
                while (segment < count - 1 && segmentStart + segmentLength < target)
                {
                    segmentStart += segmentLength;
                    segment++;
                    segmentLength = Horizontal(Lips[segment] - Lips[segment - 1]).magnitude;
                }
                float t = segmentLength > 0f ? Mathf.Clamp01((target - segmentStart) / segmentLength) : 0f;
                Vector3 lip = Vector3.Lerp(Lips[segment - 1], Lips[segment], t);
                Vector3 landing = Vector3.Lerp(Landings[segment - 1], Landings[segment], t);
                Vector3 tangent = Horizontal(Lips[segment] - Lips[segment - 1]).normalized;
                Vector3 travel = Horizontal(landing - lip);
                Vector3 forward = travel.sqrMagnitude > 0.25f ? travel.normalized : Vector3.Cross(tangent, Vector3.up);
                var place = new FallStation
                {
                    Plunge = landing,
                    Across = Vector3.Cross(Vector3.up, forward),
                    Forward = forward,
                    Pitch = pitch,
                    Drop = lip.y - landing.y,
                    MaximumBox = ContinuousBox
                };
                Vector3 Face(float share)
                {
                    Vector3 point = lip + travel * faceShare(share);
                    point.y = Mathf.Lerp(landing.y, lip.y, share);
                    return point;
                }
                AddFallEmitters(place, Face, unitDischarge, rateScale, effects);
            }
        }

        // The recipes of the reference fall at one station. Heights, plunge distances, boxes, water fades and fade
        // distances grow with the reach. Particle sizes, lives and vertical speeds grow with its square root: a tall fall gets a
        // thicker cloud, not single puffs of tens of metres. The particle count per metre falls with the reach.
        private static void AddFallEmitters(in FallStation station, System.Func<float, Vector3> face,
            float unitDischarge, float rateScale, LoogaWaterEffects effects)
        {
            float reach = Reach(station.Drop);
            float flow = Mathf.Clamp(Mathf.Sqrt(unitDischarge / ReferenceUnitDischarge), 0.3f, 2f);
            float stationRate = flow * station.Pitch / (ReferencePitch * reach) * rateScale;
            float growth = Mathf.Sqrt(reach);
            float soft = Mathf.Max(1f, growth);
            // A tall fall stays visible from farther away.
            Vector4 fade = effects.FallFade * Mathf.Max(1f, reach);
            fade.w = effects.FallFade.w;
            Vector3 across = station.Across;
            Vector3 forward = station.Forward;
            foreach (Recipe recipe in FallRecipes)
            {
                // Some kinds show only above a drop, for example the plume of a tall fall.
                float presence = recipe.MinimumDrop > 0f
                    ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(recipe.MinimumDrop, 2.5f * recipe.MinimumDrop,
                        station.Drop))
                    : 1f;
                if (presence <= 0f) continue;

                float spread = recipe.SpreadWithReach ? reach * growth : reach;
                var velocityScale = new Vector3(1f, recipe.RiseWithReach ? reach : growth,
                    recipe.SpreadWithReach ? reach : 1f);
                // The face mist of a tall fall covers a tall curtain. Less opacity keeps it a veil, not single puffs.
                // The spread fog of a tall fall gets more opacity, so it reads as a dense bank also from above.
                float alpha = recipe.OnFace ? recipe.Alpha / growth : recipe.Alpha;
                if (recipe.SpreadWithReach) alpha = Mathf.Min(alpha * growth, MaximumFogAlpha);
                Vector2 life = recipe.Life * growth;
                // The count is the rate times the mean life of the reference fall, so a longer life does not add
                // particles.
                float count = recipe.Rate * stationRate * 0.5f * (recipe.Life.x + recipe.Life.y);
                if (recipe.SpreadWithReach) count *= growth;
                // A face kind of a tall fall lies in slices down the face, each on the face at its own height, so the
                // mist follows the curve of the falling water. A single tall box stood in front of the upper face,
                // where the face leans back toward the lip.
                int slices = recipe.OnFace
                    ? Mathf.Clamp(Mathf.CeilToInt(recipe.Box.y * station.Drop / FaceSliceHeight), 1, MaximumFaceSlices)
                    : 1;
                for (int slice = 0; slice < slices; slice++)
                {
                    Vector3 centre;
                    float boxHeight;
                    float boxLength;
                    Vector3 velocityMin = Vector3.Scale(recipe.VelocityMin, velocityScale);
                    Vector3 velocityMax = Vector3.Scale(recipe.VelocityMax, velocityScale);
                    if (recipe.OnFace)
                    {
                        float share = Mathf.Clamp01(recipe.Height + recipe.Box.y * ((slice + 0.5f) / slices - 0.5f));
                        centre = face(share) + forward * recipe.Offset;
                        boxHeight = recipe.Box.y * station.Drop / slices;
                        boxLength = recipe.Box.z;
                        if (slices > 1)
                        {
                            // The particles fall and move out with the face: its horizontal distance per metre of
                            // drop in this slice.
                            float half = 0.5f * recipe.Box.y / slices;
                            Vector3 above = face(Mathf.Clamp01(share + half));
                            Vector3 below = face(Mathf.Clamp01(share - half));
                            float slope = Vector3.Dot(below - above, forward) / Mathf.Max(above.y - below.y, 0.01f);
                            velocityMin.z -= velocityMin.y * slope;
                            velocityMax.z -= velocityMax.y * slope;
                        }
                    }
                    else
                    {
                        centre = station.Plunge + forward * (recipe.Offset * spread) +
                            Vector3.up * (recipe.Height * reach);
                        boxHeight = recipe.Box.y * reach;
                        boxLength = recipe.Box.z * spread;
                    }
                    var data = new WaterEffectEmitterData
                    {
                        Centre = new Vector4(centre.x, centre.y, centre.z, station.Plunge.y),
                        Across = new Vector4(across.x, across.y, across.z,
                            Mathf.Min(recipe.Box.x, station.MaximumBox) * station.Pitch),
                        Forward = new Vector4(forward.x, forward.y, forward.z, boxLength),
                        VelocityMin = velocityMin,
                        VelocityMax = velocityMax,
                        Life = new Vector4(life.x, life.y, recipe.Size.x * growth, recipe.Size.y * growth),
                        Shape = new Vector4(recipe.Growth.x, recipe.Growth.y, recipe.Drift, 0.3f),
                        Color = Linear(alpha * presence),
                        Fade = fade,
                        Extra = new Vector4(Seed(centre, recipe.Kind), 0f, soft, Mathf.Max(1f, reach))
                    };
                    data.VelocityMin.w = boxHeight;
                    data.VelocityMax.w = recipe.GravityModifier * Gravity;
                    Add(recipe.Kind, count / slices, data);
                }
            }
        }

        // Size factor of a fall relative to the reference fall.
        private static float Reach(float drop)
        {
            return Mathf.Clamp(Mathf.Sqrt(Mathf.Max(drop, 0f) / ReferenceDrop), 0.3f, 3f);
        }

        private static Vector3 Horizontal(Vector3 vector)
        {
            vector.y = 0f;
            return vector;
        }

        // Downstream distance of the falling water at a frame height, between the lip and the plunge. Level parts of
        // the path, such as the sheet approach upstream of the lip, do not count: at the upper level the distance is
        // where the water starts to fall.
        private static float FaceDistance(List<WaterFallPoint> path, int plungeIndex, float height)
        {
            for (int index = 1; index <= plungeIndex; index++)
            {
                WaterFallPoint above = path[index - 1];
                WaterFallPoint below = path[index];
                if (below.Surface.y >= above.Surface.y || below.Surface.y > height || above.Surface.y < height) continue;

                float t = Mathf.InverseLerp(above.Surface.y, below.Surface.y, height);
                return Mathf.Lerp(above.Surface.x, below.Surface.x, t);
            }
            return path[plungeIndex].Surface.x;
        }
        #endregion

        #region Rapids
        // One low spray station per spacing along each run of aerated water. The spray rises less than a metre and
        // the flow carries it downstream. Short boxes over the centre of the channel keep the spray over the water at
        // bends. Many small, faint particles make a haze, not single puffs.
        private static void PlanRapids(WaterNetworkElement element, WaterNetworkGraph graph, LoogaWaterEffects effects)
        {
            IReadOnlyList<WaterChannelSample> samples = element.Samples;
            if (samples.Count < 2) return;

            float spacing = effects.RapidsSpacing;
            float minimum = effects.RapidsAeration;
            float next = float.NegativeInfinity;
            for (int index = 0; index < samples.Count; index++)
            {
                WaterChannelSample sample = samples[index];
                if (sample.Aeration < minimum || sample.Regime == WaterRegime.FreeFall) continue;
                if (sample.Station < next || IsNearLip(sample.Position, graph)) continue;

                next = sample.Station + spacing;
                WaterChannelSample other = samples[Mathf.Min(index + 1, samples.Count - 1)];
                Vector3 forward = index + 1 < samples.Count
                    ? other.Position - sample.Position
                    : sample.Position - samples[index - 1].Position;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-6f) continue;

                forward.Normalize();
                Vector3 across = Vector3.Cross(Vector3.up, forward);
                float strength = Mathf.Lerp(0.35f, 1f, Mathf.InverseLerp(minimum, 1f, sample.Aeration));
                float speed = Mathf.Max(sample.Speed, 0.5f);
                float width = Mathf.Max(1f, 0.6f * sample.Width);
                float length = 0.9f * spacing;
                var size = new Vector2(0.6f, 1.6f) * (0.7f + 0.5f * strength);
                var life = new Vector2(0.9f, 1.8f);
                float meanSize = 0.5f * (size.x + size.y);
                float count = Mathf.Clamp(0.8f * strength * width * length / (meanSize * meanSize), 3f, 48f) *
                    effects.RapidsScale;
                Vector3 centre = sample.Position + Vector3.up * 0.25f;
                var data = new WaterEffectEmitterData
                {
                    Centre = new Vector4(centre.x, centre.y, centre.z, sample.Position.y),
                    Across = new Vector4(across.x, across.y, across.z, width),
                    Forward = new Vector4(forward.x, forward.y, forward.z, length),
                    VelocityMin = new Vector4(-0.3f, 0.3f, 0.15f * speed, 0.3f),
                    VelocityMax = new Vector4(0.3f, 0.6f + 1.2f * strength, 0.4f * speed, 0.35f * Gravity),
                    Life = new Vector4(life.x, life.y, size.x, size.y),
                    Shape = new Vector4(0.7f, 1.8f, 0.3f, 0.3f),
                    Color = Linear(0.05f + 0.1f * strength),
                    Fade = effects.RapidsFade,
                    Extra = new Vector4(Seed(centre, WaterEffectKind.RapidsSpray), RapidsWaterFade, 0f, 0f)
                };
                Add(WaterEffectKind.RapidsSpray, count, data);
            }
        }

        private static bool IsNearLip(Vector3 position, WaterNetworkGraph graph)
        {
            foreach (WaterNetworkFall fall in graph.Falls)
            {
                float reach = LipClearance + 0.5f * fall.Width;
                Vector2 offset = new Vector2(position.x - fall.Lip.x, position.z - fall.Lip.z);
                if (offset.sqrMagnitude < reach * reach) return true;
            }
            return false;
        }
        #endregion

        #region Budget
        private static void Add(WaterEffectKind kind, float count, WaterEffectEmitterData data)
        {
            Planned.Add(new WaterEffectEmitter
            {
                Kind = kind,
                Count = Mathf.Max(1, Mathf.RoundToInt(count)),
                Data = data
            });
        }

        // Rapids give way first. The falls scale down only when they alone are over the budget.
        private static int ApplyBudget(int budget)
        {
            int falls = 0;
            int rapids = 0;
            foreach (WaterEffectEmitter emitter in Planned)
            {
                if (emitter.Kind == WaterEffectKind.RapidsSpray)
                {
                    rapids += emitter.Count;
                }
                else
                {
                    falls += emitter.Count;
                }
            }
            int requested = falls + rapids;
            if (requested <= budget) return requested;

            float fallShare = falls > budget ? (float)budget / falls : 1f;
            float rapidsShare = rapids > 0 ? Mathf.Clamp01((float)(budget - Mathf.Min(falls, budget)) / rapids) : 0f;
            for (int index = 0; index < Planned.Count; index++)
            {
                WaterEffectEmitter emitter = Planned[index];
                float share = emitter.Kind == WaterEffectKind.RapidsSpray ? rapidsShare : fallShare;
                emitter.Count = Mathf.FloorToInt(emitter.Count * share);
                Planned[index] = emitter;
            }
            return requested;
        }
        #endregion

        private static Vector4 Linear(float alpha)
        {
            Color color = MistColor.linear;
            return new Vector4(color.r, color.g, color.b, alpha);
        }

        // A seed below 2^24, so the float holds it exactly.
        private static float Seed(Vector3 position, WaterEffectKind kind)
        {
            int hash = Mathf.RoundToInt(position.x * 4f) * 73856093 ^ Mathf.RoundToInt(position.y * 4f) * 19349663 ^
                Mathf.RoundToInt(position.z * 4f) * 83492791 ^ ((int)kind + 1) * 2654435;
            return hash & 0xFFFFFF;
        }
    }
}
