using System;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The wire shape of the traffic: cars packed into the networked ints of the scene objects
    /// "Ets Net Traffic 0..3" (NetworkBridgeData64 each, 64 ints), written by the one client that
    /// runs the traffic and read by everyone else. Pure, like EtsNetWire, so it tests offline.
    ///
    /// As with the truck, the state lives in networked ints, not messages: Fusion sends an int
    /// only when it changes (a car standing at a junction costs nothing), and a late joiner reads
    /// every car straight out of the arrays.
    ///
    /// One block (64 ints):
    ///   0  magic "ETR1", 0 until the block has been written once
    ///   1  sequence, bumped on every write of the block, and at least once a second while the
    ///      runner lives (EtsTraffic's heartbeat: a block that stops changing has lost its runner)
    ///   2  the writer's clock in ms (Time.fixedTimeAsDouble), shared by the block's cars
    ///   3  writer: Fusion PlayerId + 1 (bits 0-15) | writer generation (16-23) | car table hash (24-31)
    ///   4..63  fifteen cars, four ints each:
    ///     +0  x, Unity frame, centimetres (map spans x -75,650..53,017 m, Program1381)
    ///     +1  z, Unity frame, centimetres (-71,303..48,824 m)
    ///     +2  y in centimetres, signed 16 bits (road nodes span -113.2..223.1 m) | yaw, 16 bits of a turn
    ///     +3  speed in 0.1 m/s (10 bits, to 102 m/s) | vehicle index low 7 bits (EtsTrafficCatalog order) |
    ///         colour (4 bits; the most any vehicle has is 11) | slot serial (4 bits) | flags (4 bits: brake,
    ///         blink left, blink right, wreck) | vehicle index top 2 bits (bits 29-30, 2026-10-14: 284 vehicles) |
    ///         alive (bit 31). A tractor's trailer is not on the wire: EtsTrafficCatalog.TrailerFor(vehicle, slot and
    ///         serial) gives every client the same one.
    /// An empty slot is four zeros. A new car in a slot comes with the next serial, so a watcher
    /// snaps to it instead of sliding the old car across the map.
    /// </summary>
    public static class EtsTrafficWire
    {
        public const int Magic = 0x45545231;
        public const int Ints = 64;
        public const int HeaderInts = 4;
        public const int IntsPerCar = 4;
        public const int CarsPerBlock = (Ints - HeaderInts) / IntsPerCar;

        public const int AtMagic = 0;
        public const int AtSeq = 1;
        public const int AtStamp = 2;
        public const int AtWriter = 3;

        public const int FlagBrake = 1;
        public const int FlagBlinkLeft = 2;
        public const int FlagBlinkRight = 4;

        /// <summary>The four flag bits in use (brake, blink left, blink right, wreck); bits 29-30 carry the vehicle index's top bits.</summary>
        public const int FlagMask = 0xF;

        /// <summary>Most vehicles the wire can name (9 bits).</summary>
        public const int MaxVehicles = 512;

        public struct Car
        {
            public bool Alive;
            public Vector3 Position;
            public float Yaw;
            public float Speed;
            public int Vehicle;
            public int Colour;
            public int Serial;
            public int Flags;
        }

        public struct Header
        {
            public int Seq;
            public int StampMs;
            public int Writer;
            public int Generation;
            public int TableHash;
        }

        public static int At(int carInBlock)
        {
            return HeaderInts + carInBlock * IntsPerCar;
        }

        public static void PackHeader(Header h, int[] into)
        {
            into[AtMagic] = Magic;
            into[AtSeq] = h.Seq;
            into[AtStamp] = h.StampMs;
            into[AtWriter] = ((h.Writer + 1) & 0xFFFF) | ((h.Generation & 0xFF) << 16) | ((h.TableHash & 0xFF) << 24);
        }

        public static bool UnpackHeader(int[] from, out Header h)
        {
            h = default;

            if (from == null || from.Length < Ints || from[AtMagic] != Magic)
            {
                return false;
            }

            h.Seq = from[AtSeq];
            h.StampMs = from[AtStamp];
            h.Writer = (from[AtWriter] & 0xFFFF) - 1;
            h.Generation = (from[AtWriter] >> 16) & 0xFF;
            h.TableHash = (from[AtWriter] >> 24) & 0xFF;
            return true;
        }

        public static void PackCar(Car c, int[] into, int at)
        {
            if (!c.Alive)
            {
                into[at] = into[at + 1] = into[at + 2] = into[at + 3] = 0;
                return;
            }

            into[at] = Centimetres(c.Position.x);
            into[at + 1] = Centimetres(c.Position.z);
            int y = Math.Max(short.MinValue, Math.Min(short.MaxValue, (int)Math.Round(c.Position.y * 100f)));
            float turns = c.Yaw / 360f;
            turns -= (float)Math.Floor(turns);
            int yaw = (int)Math.Round(turns * 65536f) & 0xFFFF;
            into[at + 2] = (y & 0xFFFF) | (yaw << 16);
            int speed = Math.Max(0, Math.Min(1023, (int)Math.Round(c.Speed * 10f)));
            // The vehicle index takes 9 bits since 2026-10-14 (284 vehicles: cars, police, vans, lorries, trucks, buses): its
            // low 7 in bits 10-16 as before, its top 2 in the two flag bits nothing used (bits 29-30).
            into[at + 3] = speed | ((c.Vehicle & 0x7F) << 10) | ((c.Colour & 0xF) << 17) | ((c.Serial & 0xF) << 21) | ((c.Flags & FlagMask) << 25) | (((c.Vehicle >> 7) & 3) << 29) | (1 << 31);
        }

        public static Car UnpackCar(int[] from, int at)
        {
            var c = new Car();
            int packed = from[at + 3];

            if ((packed & (1 << 31)) == 0)
            {
                return c;
            }

            c.Alive = true;
            c.Position = new Vector3(from[at] / 100f, (short)(from[at + 2] & 0xFFFF) / 100f, from[at + 1] / 100f);
            c.Yaw = ((from[at + 2] >> 16) & 0xFFFF) * (360f / 65536f);
            c.Speed = (packed & 0x3FF) / 10f;
            c.Vehicle = ((packed >> 10) & 0x7F) | (((packed >> 29) & 3) << 7);
            c.Colour = (packed >> 17) & 0xF;
            c.Serial = (packed >> 21) & 0xF;
            c.Flags = (packed >> 25) & FlagMask;
            return c;
        }

        private static int Centimetres(float metres)
        {
            double cm = Math.Round(metres * 100.0);
            return cm > int.MaxValue ? int.MaxValue : cm < int.MinValue ? int.MinValue : (int)cm;
        }

        /// <summary>
        /// A runner's first write over a block it has just gained: the block as the watchers
        /// have it becomes the shadow, with the magic zeroed so the header is rewritten (this
        /// writer, its generation). Write then sends every difference - an empty slot where the
        /// old runner had a car included. Returns the sequence to carry on from.
        /// </summary>
        public static int Seed(int[] live, int[] shadow)
        {
            Array.Copy(live, shadow, Ints);
            shadow[AtMagic] = 0;
            return live[AtSeq] + 1;
        }

        /// <summary>
        /// Whether a block must be written: a car's ints differ from the shadow, the header was
        /// not written by this writer yet, or the heartbeat is due (so a block of standing cars
        /// still changes once a second, and a watcher can tell it from a runner that has gone).
        /// </summary>
        public static bool Due(int[] scratch, int[] shadow, bool beat)
        {
            if (beat || shadow[AtMagic] != Magic)
            {
                return true;
            }

            for (int j = HeaderInts; j < Ints; j++)
            {
                if (scratch[j] != shadow[j]) return true;
            }

            return false;
        }

        /// <summary>
        /// A car as EtsNetTimeline wants it, so the truck's proven interpolation serves the cars
        /// too: stamped on the writer's clock, Hermite through position and velocity, a new
        /// epoch (writer, generation, serial) snapped to rather than slid to.
        ///
        /// The epoch carries all 16 bits of the writer: Fusion hands out PlayerIds that only
        /// grow in a room, so with 8 bits writers 5 and 261 on their first generation (each
        /// client starts at 0) gave the same epoch, the timeline did not reset, and every sample
        /// on the new writer's lower clock was refused (Program1502: 0 of 50 taken). At most
        /// 0x0FFFFFFF: never negative, so never the -1 that EtsNetTimeline and EtsTraffic's slots
        /// use for "none". The serial stays the low 4 bits (EtsTraffic reads it back from there).
        /// </summary>
        public static EtsNetWire.State ToTimeline(Car c, Header h)
        {
            float yaw = c.Yaw * Mathf.Deg2Rad;
            var forward = new Vector3((float)Math.Sin(yaw), 0f, (float)Math.Cos(yaw));

            return new EtsNetWire.State
            {
                Seq = h.Seq,
                StampMs = h.StampMs,
                Epoch = ((h.Writer & 0xFFFF) << 12) | ((h.Generation & 0xFF) << 4) | (c.Serial & 0xF),
                Position = c.Position,
                Rotation = new Quaternion(0f, (float)Math.Sin(yaw * 0.5f), 0f, (float)Math.Cos(yaw * 0.5f)),
                Velocity = forward * c.Speed,
                Flags = c.Flags,
                Rpm = c.Vehicle,
                Gear = c.Colour
            };
        }
    }

    /// <summary>
    /// Who runs the traffic, pure: EtsTraffic hands it, every frame, who holds each block
    /// (Fusion's state authority) and what each block's header says; the offline harness hands
    /// it a simulated room (Program1733). The rules are in EtsTraffic's class notes; in short:
    ///   - holding every block: run it, master or not (only a client whose traffic is built
    ///     ever asks, so only such a client can hold all four by its own doing);
    ///   - somebody else is writing a block it holds (changed within TakeoverSeconds): that one
    ///     runs, watch;
    ///   - a lower PlayerId took a block within ContestSeconds (two asked at once): it takes
    ///     the rest, watch;
    ///   - running already and a block went to nobody or to a higher PlayerId that is not
    ///     writing it: carry on, ask for it back;
    ///   - the room's master asks for blocks nobody holds (a runner left), as SceneNetworking
    ///     does, and only those;
    ///   - no block changed for TakeoverSeconds + TakeoverStep x (players with a lower
    ///     PlayerId holding no block): ask for every block, until holding all or told to watch
    ///     as above.
    /// A block changes at least every HeartbeatSeconds while its runner lives, so "no block
    /// changed" means nobody is running the traffic. First sight of a block is not a change:
    /// a departed runner's last write must not count as a live one.
    /// </summary>
    public sealed class EtsTrafficAuthority
    {
        /// <summary>The runner rewrites every block at least this often, seconds (2 ints a block: seq and stamp).</summary>
        public const float HeartbeatSeconds = 1f;

        /// <summary>A block unchanged this long has lost its runner: its cars leave the road on watchers (four missed heartbeats).</summary>
        public const float StaleSeconds = 4f;

        /// <summary>
        /// No block changed this long: the first client in line asks for all of them. Below
        /// StaleSeconds, so it still draws the cars and carries them on (EtsTraffic.Adopt)
        /// rather than starting from an empty road.
        /// </summary>
        public const float TakeoverSeconds = 3f;

        /// <summary>
        /// Added for each player with a lower PlayerId that holds no block (one sitting on the
        /// silent blocks is the runner that went quiet, or a master that cannot run the traffic:
        /// it will not take over). A takeover is writing within about half a second (authority
        /// round trip, one 10 Hz write, one 16 Hz send), well inside a step.
        /// </summary>
        public const float TakeoverStep = 1.5f;

        /// <summary>
        /// A lower PlayerId that took a block this recently may be taking the rest: a higher one
        /// stands aside. It asks again within a second and holds all within one round trip
        /// more; longer would only delay the takeover past StaleSeconds when the fresh holder is
        /// a master that cannot run the traffic (Program1733 S2: 5 s here and the cars were gone
        /// before anyone could carry them on).
        /// </summary>
        public const float ContestSeconds = 2f;

        private readonly long[] _heard;
        private readonly float[] _heardAt;
        private readonly int[] _holder;
        private readonly float[] _holderSince;
        private float _watchSince = -1f;

        /// <summary>Asking for every block because the room went silent, until it holds them all or is told to watch.</summary>
        public bool Contending { get; private set; }

        public int Count => _heard.Length;

        public EtsTrafficAuthority(int blocks)
        {
            _heard = new long[blocks];
            _heardAt = new float[blocks];
            _holder = new int[blocks];
            _holderSince = new float[blocks];

            for (int b = 0; b < blocks; b++)
            {
                _heard[b] = long.MinValue;
                _heardAt[b] = float.NegativeInfinity;
                _holder[b] = int.MinValue;
            }
        }

        /// <summary>
        /// A block as seen now: whether its header is written (the magic is there), the header's
        /// writer int and sequence, and the PlayerId holding it (-1: nobody). Every frame.
        /// </summary>
        public void See(int b, bool written, int writer, int seq, int holder, float now)
        {
            long heard = written ? ((long)writer << 32) | (uint)seq : 0L;

            if (_heard[b] == long.MinValue)
            {
                // First sight is not news (see the class notes).
                _heard[b] = heard;
            }
            else if (heard != _heard[b])
            {
                _heard[b] = heard;
                _heardAt[b] = now;
            }

            if (holder != _holder[b])
            {
                // First sight counts as just taken: a client that has just started stands aside
                // for a lower holder for ContestSeconds, rather than assume it is long idle.
                _holder[b] = holder;
                _holderSince[b] = now;
            }
        }

        /// <summary>Every block is live: the silence before any write counts from the first call.</summary>
        public void Online(float now)
        {
            if (_watchSince < 0f)
            {
                _watchSince = now;
            }
        }

        /// <summary>The room is gone (role Local): nothing to contend for.</summary>
        public void Offline()
        {
            Contending = false;
        }

        /// <summary>The block changed within StaleSeconds: somebody is writing it.</summary>
        public bool Fresh(int b, float now)
        {
            return now - _heardAt[b] < StaleSeconds;
        }

        /// <summary>When the block last changed (-infinity: not seen to change).</summary>
        public float ChangedAt(int b)
        {
            return _heardAt[b];
        }

        /// <summary>Whether the player holds any block, as last seen: such a player is not counted in the takeover queue.</summary>
        public bool HoldsAny(int player)
        {
            for (int b = 0; b < _holder.Length; b++)
            {
                if (_holder[b] == player) return true;
            }

            return false;
        }

        /// <summary>The PlayerId the block's header names as its writer, -1 if unwritten.</summary>
        public int Writer(int b)
        {
            long heard = _heard[b];
            return heard == 0L || heard == long.MinValue ? -1 : (int)((heard >> 32) & 0xFFFF) - 1;
        }

        /// <summary>Seconds since any block changed, or since the room was first online if none has.</summary>
        public float Silence(float now)
        {
            if (_watchSince < 0f)
            {
                return 0f;
            }

            float last = _watchSince;

            for (int b = 0; b < _heardAt.Length; b++)
            {
                if (_heardAt[b] > last) last = _heardAt[b];
            }

            return now - last;
        }

        /// <summary>
        /// Whether this client runs the traffic (role Master), and which blocks to ask for, a
        /// bit each. mine: this client holds the block. running: it runs the traffic now.
        /// master: it is the room's shared-mode master. below: how many players have a lower
        /// PlayerId and hold no block (HoldsAny), called only while the room is silent; null
        /// when it is not time to ask (the
        /// caller looks four times a second, asks at most once), which answers only the role.
        /// began: this call started contending (for the log).
        /// </summary>
        public bool Decide(bool[] mine, int me, bool running, bool master, float now, Func<int> below, out int ask, out bool began)
        {
            ask = 0;
            began = false;
            bool all = true, anyNone = false, taken = false;

            for (int b = 0; b < _heard.Length; b++)
            {
                if (mine[b])
                {
                    continue;
                }

                all = false;
                int holder = _holder[b];

                if (holder < 0)
                {
                    anyNone = true;
                    continue;
                }

                // Its holder is writing it (it changed within TakeoverSeconds - three heartbeats -
                // and the header names the holder): a live runner, whatever its PlayerId. A runner
                // back from a long hitch finds its blocks carried on by another and stands down,
                // rather than roll the cars back. Not StaleSeconds: a runner that went quiet must
                // stop counting as live before the watchers let go of its cars, or nobody could
                // take over in time to carry them on (Program1733 S4: 0 cars adopted that way).
                if (now - _heardAt[b] < TakeoverSeconds && Writer(b) == holder)
                {
                    taken = true;
                    continue;
                }

                // Two asked at once and split the blocks: the lower PlayerId takes the rest.
                if (holder < me && now - _holderSince[b] < ContestSeconds)
                {
                    taken = true;
                }
            }

            if (all)
            {
                Contending = false;
                return true;
            }

            if (taken)
            {
                Contending = false;
                return false;
            }

            if (below == null)
            {
                return running;
            }

            bool orphans = master && anyNone;

            if (!running && !Contending && !orphans)
            {
                float silence = Silence(now);

                if (silence >= TakeoverSeconds && silence >= TakeoverSeconds + TakeoverStep * below())
                {
                    Contending = began = true;
                }
            }

            bool every = running || Contending;

            for (int b = 0; b < _heard.Length; b++)
            {
                if (!mine[b] && (every || (orphans && _holder[b] < 0)))
                {
                    ask |= 1 << b;
                }
            }

            return running;
        }
    }
}
