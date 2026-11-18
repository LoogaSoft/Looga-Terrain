using UnityEngine;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// The flow that a channel gives a body at one network joint, where the channel centreline crosses the body
    /// outline. A channel that flows in makes a jet that spreads into the body and slows down. A channel that flows out
    /// pulls the body water into its mouth. The flow at the crossing is the channel flow, so the body and the channel
    /// have the same flow at the joint. A fall makes a plunge at the point where the falling water meets the body. A
    /// crest lip pulls the body water over the lip.
    /// </summary>
    internal struct WaterJointFlow
    {
        // Half angle of the jet spread, in degrees. Turbulent plane jets spread at about 11 to 13 degrees.
        private const float SpreadAngle = 12f;
        // Jet length as a multiple of the half width at the crossing. The jet fades out over the last part.
        private const float JetLength = 60f;
        private const float JetFadeStart = 0.6f;
        // Distance over which the jet edges and the jet start fade out, in half widths and at least in metres.
        private const float EdgeFalloff = 1f;
        private const float MinimumFalloff = 4f;
        // Reach of the pull around an outlet mouth, in half widths and at least in metres.
        private const float OutletReach = 4f;
        private const float MinimumOutletReach = 10f;
        // Half angle of the plunge spread, in degrees. The water that a plunge pushes up spreads wider than a jet.
        private const float PlungeSpreadAngle = 20f;
        // The plunge flow fades out from the first to the second distance, in decay lengths.
        private const float PlungeFadeStart = 2f;
        private const float PlungeLength = 4f;
        // The pull toward a crest lip fades out at this distance upstream of the lip, in decay lengths.
        private const float CrestPullLength = 3f;
        // A crest plunge also drives the water between the fall face and the landing line, up to this distance
        // behind the landing line, in metres. Its speed there falls to this share toward the face.
        private const float CrestPlungeBehind = 40f;
        private const float CrestPlungeBehindSpeed = 0.5f;

        // Element indices in the network graph.
        public int Body;
        public int Channel;
        // True when the channel flows into the body.
        public bool Inflow;
        // True at a fall: the point is the plunge, and the speed decays over the decay length.
        public bool Plunge;
        // True at a crest: the point is the centre of a piece of the lip, and the body water speeds up toward the lip
        // over the decay length. With Plunge, the point is the centre of the landing line of the piece, and the
        // water flows out along the direction. The body applies crest joints after its flow zones.
        public bool Crest;
        public float Decay;
        // World XZ crossing of the channel centreline and the body outline, or the plunge centre of a fall.
        public Vector2 Point;
        // World XZ unit direction of the channel flow at the crossing.
        public Vector2 Direction;
        public float HalfWidth;
        // Channel speed at the crossing, in metres per second.
        public float Speed;
        // Channel discharge at the crossing, in cubic metres per second.
        public float Discharge;
        // Regime values of the channel at the crossing, from 0 to 1. See WaterChannelSample.
        public float Turbulence;
        public float Aeration;

        /// <summary>World XZ rectangle where the joint changes the flow.</summary>
        public Rect Reach
        {
            get
            {
                float reach = Crest && Plunge ? PlungeLength * Decay + HalfWidth + Falloff + CrestPlungeBehind :
                    Crest ? CrestPullLength * Decay + HalfWidth + Falloff :
                    Plunge ? PlungeLength * Decay + HalfWidth + Falloff :
                    Inflow ? JetLength * HalfWidth + HalfWidth + Falloff : OutletRadius + Falloff;
                return new Rect(Point.x - reach, Point.y - reach, 2f * reach, 2f * reach);
            }
        }

        private float Falloff => Mathf.Max(EdgeFalloff * HalfWidth, MinimumFalloff);
        private float OutletRadius => Mathf.Max(OutletReach * HalfWidth, MinimumOutletReach);

        /// <summary>
        /// Blends the joint flow into the flow at a world XZ point. The regime values (x = turbulence, y = aeration)
        /// take the larger of their value and the joint values, which fade with the joint flow.
        /// </summary>
        public void Apply(Vector2 point, ref Vector2 flow, ref Vector2 regime)
        {
            if (HalfWidth <= 0f || Speed <= 0f) return;

            if (Crest && Plunge)
            {
                ApplyCrestPlunge(point, ref flow, ref regime);
            }
            else if (Crest)
            {
                ApplyCrest(point, ref flow);
            }
            else if (Plunge)
            {
                ApplyPlunge(point, ref flow, ref regime);
            }
            else if (Inflow)
            {
                ApplyJet(point, ref flow, ref regime);
            }
            else
            {
                ApplyOutlet(point, ref flow, ref regime);
            }
        }

        // A plane jet keeps its momentum (speed squared times width), so its speed falls as it widens. The flow turns
        // out toward the jet edges, as if it came from a point behind the crossing.
        private void ApplyJet(Vector2 point, ref Vector2 flow, ref Vector2 regime)
        {
            Vector2 offset = point - Point;
            float along = Vector2.Dot(offset, Direction);
            // Positive to the left of the flow.
            float side = Direction.x * offset.y - Direction.y * offset.x;
            float run = Mathf.Max(along, 0f);
            float tangent = Mathf.Tan(SpreadAngle * Mathf.Deg2Rad);
            float width = HalfWidth + run * tangent;
            float length = JetLength * HalfWidth;
            float weight = Fade(Mathf.Abs(side) - width) * Fade(-along) *
                (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(JetFadeStart * length, length, run)));
            if (weight <= 0f) return;

            float angle = Mathf.Atan2(side, HalfWidth / tangent + run);
            float sine = Mathf.Sin(angle);
            float cosine = Mathf.Cos(angle);
            var direction = new Vector2(cosine * Direction.x - sine * Direction.y,
                sine * Direction.x + cosine * Direction.y);
            float speed = Speed * Mathf.Sqrt(HalfWidth / width);
            flow = Vector2.Lerp(flow, direction * speed, weight);
            // The jet energy falls with the square of its speed, and the white water with it.
            float energy = HalfWidth / width;
            regime = Vector2.Max(regime, new Vector2(Turbulence, Aeration) * (weight * energy));
        }

        // Falling water plunges deep and pushes the water up and out at the surface. The surface flow spreads like a
        // jet, but it loses most of its speed within a few decay lengths, and the current solve takes over. The white
        // water is full at the plunge and thins out much faster than the speed, so the flow carries the foam of the
        // boil out in streaks.
        private void ApplyPlunge(Vector2 point, ref Vector2 flow, ref Vector2 regime)
        {
            Vector2 offset = point - Point;
            float along = Vector2.Dot(offset, Direction);
            // Positive to the left of the flow.
            float side = Direction.x * offset.y - Direction.y * offset.x;
            float run = Mathf.Max(along, 0f);
            float decay = Mathf.Max(Decay, 0.1f);
            float tangent = Mathf.Tan(PlungeSpreadAngle * Mathf.Deg2Rad);
            float width = HalfWidth + run * tangent;
            float weight = Fade(Mathf.Abs(side) - width) * Fade(-along) *
                (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(PlungeFadeStart * decay, PlungeLength * decay, run)));
            if (weight <= 0f) return;

            float angle = Mathf.Atan2(side, HalfWidth / tangent + run);
            float sine = Mathf.Sin(angle);
            float cosine = Mathf.Cos(angle);
            var direction = new Vector2(cosine * Direction.x - sine * Direction.y,
                sine * Direction.x + cosine * Direction.y);
            float energy = Mathf.Exp(-run / decay);
            flow = Vector2.Lerp(flow, direction * (Speed * Mathf.Sqrt(HalfWidth / width) * energy), weight);
            // The white water fades over a third of the decay length.
            float aeration = energy * energy * energy;
            regime = Vector2.Max(regime, new Vector2(Turbulence * energy, Aeration * aeration) * weight);
        }

        // Upstream of a free overfall the water draws down and speeds up to the brink speed at the lip. The flow turns
        // along the lip normal, and the speed falls off over the decay length upstream. The pull never slows the
        // water that already moves toward the lip faster.
        private void ApplyCrest(Vector2 point, ref Vector2 flow)
        {
            Vector2 offset = point - Point;
            float behind = Mathf.Max(-Vector2.Dot(offset, Direction), 0f);
            float side = Mathf.Abs(Direction.x * offset.y - Direction.y * offset.x);
            float decay = Mathf.Max(Decay, 0.1f);
            float weight = Fade(side - HalfWidth) *
                (1f - Mathf.SmoothStep(0f, 1f, behind / (CrestPullLength * decay)));
            if (weight <= 0f) return;

            float speed = Mathf.Max(Speed * Mathf.Exp(-behind / decay), Vector2.Dot(flow, Direction));
            flow = Vector2.Lerp(flow, Direction * speed, weight);
        }

        // Below a crest the pieces of the landing line lie side by side, so the flow goes straight out along the
        // direction and does not spread like the plunge of one fall. Neighbour pieces have almost the same direction
        // and speed, and the field has no edges between them. Between the fall face and the landing line the water
        // also flows out, slower toward the face, so no water moves toward the fall.
        private void ApplyCrestPlunge(Vector2 point, ref Vector2 flow, ref Vector2 regime)
        {
            Vector2 offset = point - Point;
            float along = Vector2.Dot(offset, Direction);
            float side = Mathf.Abs(Direction.x * offset.y - Direction.y * offset.x);
            float run = Mathf.Max(along, 0f);
            float behind = Mathf.Max(-along, 0f);
            float decay = Mathf.Max(Decay, 0.1f);
            float weight = Fade(side - HalfWidth) * Fade(behind - CrestPlungeBehind) *
                (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(PlungeFadeStart * decay, PlungeLength * decay, run)));
            if (weight <= 0f) return;

            float energy = Mathf.Exp(-run / decay);
            float speed = Speed * energy *
                Mathf.Lerp(1f, CrestPlungeBehindSpeed, Mathf.Clamp01(behind / CrestPlungeBehind));
            flow = Vector2.Lerp(flow, Direction * speed, weight);
            regime = Vector2.Max(regime, new Vector2(Turbulence * energy, Aeration * energy * energy * energy) * weight);
        }

        // The body water flows to a point a half width inside the channel mouth and reaches the channel speed at the
        // mouth. The pull fades out over the outlet reach.
        private void ApplyOutlet(Vector2 point, ref Vector2 flow, ref Vector2 regime)
        {
            Vector2 target = Point + Direction * HalfWidth;
            Vector2 toTarget = target - point;
            float distance = toTarget.magnitude;
            if (distance < 1e-3f) return;

            float radius = OutletRadius;
            float weight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(HalfWidth, radius, distance));
            if (weight <= 0f) return;

            // The speed rises toward the mouth, as the water from a wide area goes through the narrow mouth.
            float rise = Mathf.Clamp01(2f * HalfWidth / (HalfWidth + distance));
            flow = Vector2.Lerp(flow, toTarget / distance * (Speed * rise), weight);
            regime = Vector2.Max(regime, new Vector2(Turbulence, Aeration) * (weight * rise * rise));
        }

        private float Fade(float distance)
        {
            if (distance <= 0f) return 1f;

            float falloff = Falloff;
            if (distance >= falloff) return 0f;

            float t = 1f - distance / falloff;
            return t * t * (3f - 2f * t);
        }
    }
}
