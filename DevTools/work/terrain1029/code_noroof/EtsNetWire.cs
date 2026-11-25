using System;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// RETIRED AS A WIRE (Phase 3, [net §2.12]): nothing in the world packs or unpacks this layout any more - every truck
    /// travels in its owner's player slot (EtsPlayerWire) - but State stays the sample type of EtsNetTimeline (the
    /// trucks' and EtsTrafficWire's), and Pack/Unpack stay only as NetTest2's (probe 1823) reference for the old wire's
    /// bandwidth. The notes below describe the one-truck wire as it was.
    ///
    /// The wire shape of one truck on the network, and nothing else: how the truck and its trailer
    /// are packed into the ints of a NetworkBridgeData64 and read back. Pure, so it can be tested
    /// without a scene (the AoM world's AomWire is the model for this).
    ///
    /// The state lives in networked ints, not in messages. Fusion sends an int only when it
    /// changes, so a parked truck costs nothing, and a player who joins late reads the truck
    /// straight out of the array with no census.
    ///
    ///   0  magic ("ETS1"), 0 until somebody has written the truck once
    ///   1  sequence, bumped on every write
    ///   2  the writer's physics clock in milliseconds (Time.fixedTimeAsDouble), for interpolation
    ///   3  the driver: Fusion PlayerId + 1, 0 = nobody in the seat
    ///   4  flags (Flag*)
    ///   5  gear (signed byte) | placement epoch (byte) | trailer serial (byte) | spare
    ///   6  position x, y, z (float bits)
    ///   9  rotation x, y, z, w (float bits)
    ///  13  velocity x, y, z (float bits)
    ///  16  steering input -1..1 (float bits): the wheel and the front wheels follow it
    ///  17  rpm (16 bits) | throttle (8, of 255) | brake (8, of 255)
    ///  18  trailer position x, y, z (float bits)
    ///  21  trailer rotation x, y, z, w (float bits)
    ///  25  map id: a hash of what the writer built its map from, so a mismatch can be told
    /// </summary>
    public static class EtsNetWire
    {
        public const int Magic = 0x45545331;
        public const int Used = 26;

        public const int AtMagic = 0;
        public const int AtSeq = 1;
        public const int AtStamp = 2;
        public const int AtDriver = 3;
        public const int AtFlags = 4;
        public const int AtGear = 5;
        public const int AtPos = 6;
        public const int AtRot = 9;
        public const int AtVel = 13;
        public const int AtSteer = 16;
        public const int AtEngine = 17;
        public const int AtTrailerPos = 18;
        public const int AtTrailerRot = 21;
        public const int AtMap = 25;

        public const int FlagPlaced = 1;
        public const int FlagDriven = 2;
        public const int FlagParkingBrake = 4;
        public const int FlagHorn = 8;
        public const int FlagTrailer = 16;
        public const int FlagTrailerCoupled = 32;

        public struct State
        {
            public int Seq;
            public int StampMs;

            /// <summary>Fusion PlayerId of the driver, or -1.</summary>
            public int Driver;

            public int Flags;
            public int Gear;
            public int Epoch;
            public int TrailerSerial;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
            public float Steer;
            public float Rpm;
            public float Throttle;
            public float Brake;
            public Vector3 TrailerPosition;
            public Quaternion TrailerRotation;
            public int MapId;

            public bool Has(int flag)
            {
                return (Flags & flag) != 0;
            }
        }

        public static void Pack(State s, int[] into)
        {
            into[AtMagic] = Magic;
            into[AtSeq] = s.Seq;
            into[AtStamp] = s.StampMs;
            into[AtDriver] = s.Driver < 0 ? 0 : s.Driver + 1;
            into[AtFlags] = s.Flags;
            into[AtGear] = (s.Gear & 0xFF) | ((s.Epoch & 0xFF) << 8) | ((s.TrailerSerial & 0xFF) << 16);
            PutVector(into, AtPos, s.Position);
            PutQuaternion(into, AtRot, s.Rotation);
            PutVector(into, AtVel, s.Velocity);
            into[AtSteer] = Bits(Mathf.Clamp(s.Steer, -1f, 1f));
            into[AtEngine] = Mathf.Clamp(Mathf.RoundToInt(s.Rpm), 0, 65535) |
                             (Mathf.Clamp(Mathf.RoundToInt(s.Throttle * 255f), 0, 255) << 16) |
                             (Mathf.Clamp(Mathf.RoundToInt(s.Brake * 255f), 0, 255) << 24);
            PutVector(into, AtTrailerPos, s.TrailerPosition);
            PutQuaternion(into, AtTrailerRot, s.TrailerRotation);
            into[AtMap] = s.MapId;
        }

        /// <summary>False when nobody has written the truck yet (the ints are still zero).</summary>
        public static bool Unpack(int[] from, out State s)
        {
            s = default;

            if (from == null || from.Length < Used || from[AtMagic] != Magic)
            {
                return false;
            }

            s.Seq = from[AtSeq];
            s.StampMs = from[AtStamp];
            s.Driver = from[AtDriver] - 1;
            s.Flags = from[AtFlags];
            s.Gear = (sbyte)(from[AtGear] & 0xFF);
            s.Epoch = (from[AtGear] >> 8) & 0xFF;
            s.TrailerSerial = (from[AtGear] >> 16) & 0xFF;
            s.Position = GetVector(from, AtPos);
            s.Rotation = GetQuaternion(from, AtRot);
            s.Velocity = GetVector(from, AtVel);
            s.Steer = Float(from[AtSteer]);
            s.Rpm = from[AtEngine] & 0xFFFF;
            s.Throttle = ((from[AtEngine] >> 16) & 0xFF) / 255f;
            s.Brake = ((from[AtEngine] >> 24) & 0xFF) / 255f;
            s.TrailerPosition = GetVector(from, AtTrailerPos);
            s.TrailerRotation = GetQuaternion(from, AtTrailerRot);
            s.MapId = from[AtMap];
            return IsFinite(s.Position) && IsFinite(s.TrailerPosition);
        }

        public static int Bits(float value)
        {
            return BitConverter.SingleToInt32Bits(value);
        }

        public static float Float(int bits)
        {
            return BitConverter.Int32BitsToSingle(bits);
        }

        private static void PutVector(int[] into, int at, Vector3 v)
        {
            into[at] = Bits(v.x);
            into[at + 1] = Bits(v.y);
            into[at + 2] = Bits(v.z);
        }

        private static Vector3 GetVector(int[] from, int at)
        {
            return new Vector3(Float(from[at]), Float(from[at + 1]), Float(from[at + 2]));
        }

        private static void PutQuaternion(int[] into, int at, Quaternion q)
        {
            into[at] = Bits(q.x);
            into[at + 1] = Bits(q.y);
            into[at + 2] = Bits(q.z);
            into[at + 3] = Bits(q.w);
        }

        private static Quaternion GetQuaternion(int[] from, int at)
        {
            var q = new Quaternion(Float(from[at]), Float(from[at + 1]), Float(from[at + 2]), Float(from[at + 3]));
            float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);

            // An unwritten or damaged rotation is not a promise: identity rather than NaN.
            return length > 0.5f && !float.IsNaN(length) ? new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length) : new Quaternion(0f, 0f, 0f, 1f);
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
                   !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }

        /// <summary>
        /// Normalised lerp along the short way round. Over the 30-60 ms between two samples it is
        /// indistinguishable from a slerp, and unlike Quaternion.Slerp it is plain C# (so the
        /// offline test can run it outside Unity).
        /// </summary>
        public static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            float sign = dot < 0f ? -1f : 1f;
            float x = a.x + (b.x * sign - a.x) * t;
            float y = a.y + (b.y * sign - a.y) * t;
            float z = a.z + (b.z * sign - a.z) * t;
            float w = a.w + (b.w * sign - a.w) * t;
            float length = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            return length > 1e-6f ? new Quaternion(x / length, y / length, z / length, w / length) : b;
        }
    }

    /// <summary>
    /// The receiving end: the truck as the network last described it, drawn a beat behind so there
    /// is always a real sample either side of the moment shown (the Quake world's bots do the same,
    /// Q3Bot.InterpolationDelay). Three things beyond Quake's two-sample lerp, because a truck at
    /// 90 km/h covers 2.5 m per 100 ms:
    ///   - samples are placed on the WRITER's clock (the stamp), not on when they happened to be
    ///     noticed here, so network jitter does not become position jitter;
    ///   - the render clock (now minus a lag) is steered gently, never stepped: the lag aims at the
    ///     least-delayed recent arrival plus the recent spread plus one send interval - which is what
    ///     it takes for the newest sample to always lie ahead of the moment drawn - and moves toward
    ///     that at a twentieth of real time, so a change in the connection is a slight slow-down or
    ///     speed-up, not a hitch;
    ///   - positions follow a cubic Hermite through the samples' velocities, so a bend is a curve
    ///     and not a string of straight chords.
    /// A jump (a job's depot, a respawn, a new driver) comes with a new epoch and is snapped to, not
    /// slid to. Pure: no Fusion, no scene, no Unity engine calls, so NetTest runs it offline.
    /// </summary>
    public sealed class EtsNetTimeline
    {
        public struct Entry
        {
            public double Time;
            public EtsNetWire.State State;
        }

        private const int Capacity = 64;
        private const int LagCapacity = 64;

        /// <summary>How long a remembered arrival counts toward the lag, seconds.</summary>
        private const double LagWindow = 2.5;

        /// <summary>How fast the render lag may change, seconds per second.</summary>
        private const double Slew = 0.05;

        /// <summary>A move between two samples this long is a jump, not driving, metres.</summary>
        private const float JumpMetres = 50f;

        private readonly Entry[] _samples = new Entry[Capacity];
        private int _count;
        private int _lastSeq = int.MinValue;
        private int _lastEpoch = -1;

        private readonly double[] _arrivals = new double[LagCapacity];
        private readonly double[] _lags = new double[LagCapacity];
        private int _lagCount;
        private int _lagNext;

        private double _renderLag = double.NaN;
        private double _lastNow = double.NaN;
        private long _stampBase;
        private int _lastStamp;
        private bool _haveStamp;
        private bool _snapPending;

        /// <summary>Least time kept between the newest sample and the moment drawn, seconds.</summary>
        public float MinDelay = 0.03f;

        /// <summary>Most, so a bad connection shows a late truck rather than a very late one.</summary>
        public float MaxDelay = 0.4f;

        /// <summary>How far past the newest sample the truck is carried on its velocity before it waits.</summary>
        public float MaxExtrapolation = 0.25f;

        /// <summary>
        /// Off (the truck): before the first sample of an epoch the first sample is shown as it is,
        /// so a thing that arrives moving stands still for the drawing delay and then jumps to
        /// speed. On (the traffic's cars, EtsTraffic): it is shown that far back along its own
        /// velocity (at most MaxLeadIn seconds), and carried on along it while it is the only
        /// sample, so it arrives already moving and passes through the first sample on time.
        /// Measured offline (traffic-install TrafficTest): the drawn car stood still for a frame
        /// and then jumped by 23.2 m/s at the start of each new car in its slot without it.
        /// </summary>
        public bool LeadIn;

        public float MaxLeadIn = 0.5f;

        public int Count => _count;

        /// <summary>The render lag over the best one: how far behind the newest data the truck is drawn.</summary>
        public float Delay { get; private set; }

        /// <summary>True for the one Sample() result that followed an epoch change.</summary>
        public bool WasSnap { get; private set; }

        public void Clear()
        {
            _count = 0;
            _lagCount = 0;
            _lagNext = 0;
            _renderLag = double.NaN;
            _lastEpoch = -1;

            // A cleared timeline takes the state it is handed next even if it is the one it had:
            // a traffic watcher that went Local and back re-reads an unchanged block (cars queued
            // at a junction are not rewritten), and a truck that becomes a puppet again re-adds
            // the latest state. Kept, that seq was refused and nothing was drawn until the next write.
            _lastSeq = int.MinValue;
        }

        /// <summary>A state read off the network at local time now. Returns false if it was not new.</summary>
        public bool Add(EtsNetWire.State state, double now)
        {
            if (state.Seq == _lastSeq)
            {
                return false;
            }

            _lastSeq = state.Seq;
            double stamp = Unwrap(state.StampMs);

            // A placement (depot, respawn, a new driver) is a new start: nothing before it is worth
            // sliding from, and a new writer brings its own clock.
            if (state.Epoch != _lastEpoch)
            {
                _lastEpoch = state.Epoch;
                _count = 0;
                _lagCount = 0;
                _lagNext = 0;
                _renderLag = double.NaN;
                _snapPending = true;
            }

            if (_count > 0 && stamp <= _samples[_count - 1].Time)
            {
                return false;
            }

            // A jump of more than 50 m between two samples is not driving (a job moving the truck to
            // its depot before it is set down): arrive, do not slide across the map. Quake does the
            // same for a bot that teleports (Q3Bot.ApplyNetworkState, 400 units).
            if (_count > 0 && (state.Position - _samples[_count - 1].State.Position).sqrMagnitude > JumpMetres * JumpMetres)
            {
                _count = 0;
                _snapPending = true;
            }

            _arrivals[_lagNext] = now;
            _lags[_lagNext] = now - stamp;
            _lagNext = (_lagNext + 1) % LagCapacity;
            _lagCount = Math.Min(_lagCount + 1, LagCapacity);

            if (_count == Capacity)
            {
                Array.Copy(_samples, 1, _samples, 0, Capacity - 1);
                _count--;
            }

            _samples[_count++] = new Entry { Time = stamp, State = state };
            return true;
        }

        /// <summary>The truck at local time now. False until there is anything to show.</summary>
        public bool Sample(double now, out EtsNetWire.State state)
        {
            state = default;

            if (_count == 0)
            {
                return false;
            }

            // The lag to aim at: best recent arrival, the spread above it, and one send interval.
            double best = double.MaxValue, worst = double.MinValue;

            for (int i = 0; i < _lagCount; i++)
            {
                if (now - _arrivals[i] > LagWindow)
                {
                    continue;
                }

                best = Math.Min(best, _lags[i]);
                worst = Math.Max(worst, _lags[i]);
            }

            if (best == double.MaxValue)
            {
                best = worst = _lags[(_lagNext + LagCapacity - 1) % LagCapacity];
            }

            double interval = _count > 1 ? (_samples[_count - 1].Time - _samples[Math.Max(0, _count - 9)].Time) / Math.Min(8, _count - 1) : 0.05;
            double delay = Clamp(worst - best + interval * 1.1 + 0.005, MinDelay, MaxDelay);
            double target = best + delay;
            double step = double.IsNaN(_lastNow) ? 0.0 : Math.Max(0.0, now - _lastNow);
            _lastNow = now;

            if (double.IsNaN(_renderLag) || Math.Abs(target - _renderLag) > 0.5)
            {
                _renderLag = target;
            }
            else
            {
                double most = Slew * step;
                _renderLag += Clamp(target - _renderLag, -most, most);
            }

            Delay = (float)(_renderLag - best);
            double t = now - _renderLag;
            WasSnap = _snapPending;
            _snapPending = false;

            if (_count == 1 || t <= _samples[0].Time)
            {
                state = _samples[0].State;

                // Before the first sample: back along its velocity. Past it with nothing newer yet
                // (an epoch starts with a short delay, 55 ms, below a 10 Hz writer's interval):
                // on along it, as past the newest sample below.
                if (LeadIn)
                {
                    double lead = Clamp(t - _samples[0].Time, -MaxLeadIn, MaxExtrapolation);
                    state.Position += state.Velocity * (float)lead;
                }

                return true;
            }

            Entry newest = _samples[_count - 1];

            if (t >= newest.Time)
            {
                // Past the newest: carry on along its velocity for a moment, then hold.
                float ahead = (float)Math.Min(t - newest.Time, MaxExtrapolation);
                state = newest.State;
                state.Position += newest.State.Velocity * ahead;
                return true;
            }

            int b = 1;

            while (b < _count - 1 && _samples[b].Time < t)
            {
                b++;
            }

            Entry from = _samples[b - 1], to = _samples[b];
            float span = (float)(to.Time - from.Time);
            float u = span > 1e-4f ? Mathf.Clamp01((float)(t - from.Time) / span) : 1f;

            state = u < 0.5f ? from.State : to.State;
            state.Position = Hermite(from.State.Position, from.State.Velocity, to.State.Position, to.State.Velocity, span, u);
            state.Rotation = EtsNetWire.Nlerp(from.State.Rotation, to.State.Rotation, u);
            state.Velocity = Vector3.Lerp(from.State.Velocity, to.State.Velocity, u);
            state.Steer = Mathf.Lerp(from.State.Steer, to.State.Steer, u);
            state.Rpm = Mathf.Lerp(from.State.Rpm, to.State.Rpm, u);
            state.Throttle = Mathf.Lerp(from.State.Throttle, to.State.Throttle, u);
            state.TrailerPosition = Vector3.Lerp(from.State.TrailerPosition, to.State.TrailerPosition, u);
            state.TrailerRotation = EtsNetWire.Nlerp(from.State.TrailerRotation, to.State.TrailerRotation, u);

            // Samples older than the one before the drawn moment are no longer needed.
            if (b > 2)
            {
                int drop = b - 2;
                Array.Copy(_samples, drop, _samples, 0, _count - drop);
                _count -= drop;
            }

            return true;
        }

        /// <summary>Cubic Hermite between two positions with their velocities over a span of seconds.</summary>
        public static Vector3 Hermite(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float span, float u)
        {
            float u2 = u * u, u3 = u2 * u;
            float h00 = 2f * u3 - 3f * u2 + 1f;
            float h10 = u3 - 2f * u2 + u;
            float h01 = -2f * u3 + 3f * u2;
            float h11 = u3 - u2;
            return p0 * h00 + v0 * (h10 * span) + p1 * h01 + v1 * (h11 * span);
        }

        private static double Clamp(double value, double low, double high)
        {
            return value < low ? low : value > high ? high : value;
        }

        /// <summary>The writer's millisecond clock, carried past int wrap-round (24 days).</summary>
        private double Unwrap(int stampMs)
        {
            if (_haveStamp && stampMs < _lastStamp && (long)_lastStamp - stampMs > int.MaxValue / 2)
            {
                _stampBase += 1L << 32;
            }

            _haveStamp = true;
            _lastStamp = stampMs;
            return (_stampBase + (uint)stampMs) / 1000.0;
        }
    }
}
