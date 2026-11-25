using System;
using System.Collections.Generic;
using SomniumSpace.Bridge.Player;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// What a player's rider record says: which vehicle, which seat, where on it (owner A; PLAN.md §7.A, C7:
    /// riders travel in each player's own slot, not in [elev]'s 16 rider objects).
    /// </summary>
    public enum EtsTargetKind { None = 0, Tractor = 1, Trailer = 2, TrafficCar = 3 }

    /// <summary>
    /// One player's rider record, in the target's frame as drawn by its writer (owner A for the wire, B for the
    /// meaning; PLAN §7.A, [elev §7.3], [net §2.7]). Replaces [net §2.11]'s EtsPlayerWire.Rider. On the wire it is
    /// ints 33-35 and 43 of the rider's own slot (EtsPlayerWire.PackRider).
    /// </summary>
    public struct EtsRider
    {
        public EtsTargetKind Kind; public int Index; public int Serial;
        public int Seat;                        // 0 driver, 1..4 passenger, 255 standing
        public bool Requesting;                 // a seat request is pending
        public int Epoch;                       // bumped on target or mode change; receivers snap
        public bool Airborne, Xr, YawCarried;
        public Vector3 Root; public float Yaw;  // metres, degrees (yaw relative to the target)
    }

    /// <summary>A remote player's rider record as this client samples it (owner A; PLAN §7.A, read by B's EtsRiders; filled from EtsRiderTrack).</summary>
    public struct EtsRemoteRider
    {
        public int PlayerId; public ISomniumPlayer Player; public bool Xr;
        public EtsRider Sampled;                // offsets sampled on the rider's own timeline (EtsNetTimeline)
        public Vector3 Velocity;                // of Root, in the target frame
    }

    /// <summary>One player as the Convoys page and the top bar show them (owner A; PLAN §7.A, §3.7). Filled by EtsNet from the slots (Phase 3).</summary>
    public sealed class EtsPlayerView
    {
        public int PlayerId, Slot; public string Name; public bool IsMaster, IsLocal, Xr;
        public EtsPlayerWire.Status Status; public int LoadPercent;
        public bool HasTruck; public EtsTruckIdentity Truck; public bool PassengerSeatFree;
        public bool HasJob; public ulong CargoToken, DestinationCityToken; public long Money;
    }

    /// <summary>
    /// A player slot's 64 ints, "Ets Net Player 0..49" (owner A; [net §2.4, §2.11] with PLAN.md §5.3's changes).
    /// Only the slot's owner writes it. Pure: no Fusion, no scene and no Unity engine calls (only Vector3 and
    /// Quaternion arithmetic, which is plain C# in UnityEngine.CoreModule), so NetTest2 (probe 1823) runs every
    /// method here outside Unity. That is also why rotations go through FromEuler/ToEuler below and never
    /// through Quaternion.Euler, eulerAngles, Inverse or Slerp, which are native calls.
    ///
    /// Positions are origin-independent ("WirePos", [net §2.4]): integer centimetres in game-origin Unity axes,
    /// x = game x, y = game y, z = -game z, so two clients whose maps are centred on different points still
    /// agree. The ints are packed from a Unity position and the writer's EtsMapViewer.CenterGame, in double, so
    /// a float Unity position 45 km out (4 mm steps, [net F13]) loses nothing more.
    ///
    ///   0  magic 'ETP1' (0 = never written; anything else is not a slot of this build)
    ///   1  seq, bumped when the truck's motion (ints 5, 9-25) changes, on a claim, and when int 43's rider time would
    ///      leave its range (EtsNet.WriteOwnSlot, Phase 3): the truck timeline's sample key (EtsNetTimeline dedups on it)
    ///   2  stamp: the writer's Time.fixedTimeAsDouble in ms, the clock EtsNetTimeline places samples on; moves with seq
    ///   3  owner PlayerId+1 (0-15) | claim generation (16-23) | session epoch &amp; 0xFF (24-31)
    ///   4  status (0-3) | load % (8-15) | flags (16: truck present, 17: has job, 18: VR,
    ///     19: the writer is the room master, IsSharedModeMasterClient - PLAN §12 U3)
    ///   5  placement epoch (0-7) | truck serial (8-15) | trailer serial (16-23) | spare
    ///   6  truck Preview index (0-7, 255 = none) | chassis 0 (8-15) | cabin 0 (16-23) | catalogue hash (24-31)
    ///      [PLAN C12: the choice is one of the sorted preview stems; chassis and cabin stay reserved]
    ///   7  paint RGB24 (0-23) | paint job 0 (24-31); 0xFFFFFFFF = the preview save's own paint (SavePaint)
    ///   8  reserved 0 (was accessory preset, engine, gearbox: the preview file defines them, C12)
    ///   9  truck flags (Truck* below)
    ///  10  tractor position, WirePos (3 ints)
    ///  13  yaw Angle16 | pitch Angle16 (Unity's Euler order, see ToEuler)
    ///  14  roll Angle16 | steer S16(steer * 32767)
    ///  15  velocity x | z, S16 cm/s (Unity axes, +-327 m/s)
    ///  16  velocity y S16 cm/s | rpm (0-65535)
    ///  17  gear sbyte (0-7) | throttle (8-15, of 255) | brake (16-23, of 255)
    ///  18  articulation, the coupled trailer relative to the tractor: yaw Angle16 | pitch Angle16
    ///  19  trailer family (0-7) | variant (8-15) | cargo (16-31)
    ///  20  trailer company hash
    ///  21  parked trailer position, WirePos (3 ints; valid when present and not coupled)
    ///  24  parked trailer yaw | pitch (Angle16)
    ///  25  trailer roll Angle16 (coupled: of the articulation; parked: of the trailer) | spare
    ///  26  seat 1 occupant PlayerId+1 | seat 2 (16-31)      written by the truck's owner only (PLAN C8)
    ///  27  seat 3 | seat 4 (reserved)
    ///  28  cargo token (2 ints), 30 destination city token (2 ints): the job summary others see
    ///  32  money in EUR: a display value only; the balance lives in LocalStorage (PLAN §5.3)
    ///  33  rider: kind (0-3) | target index (4-11) | target serial (12-19) | seat (20-27) | flags (28: requesting)
    ///  34  rider root offset x | y, S16 mm in the target's frame (+-32.767 m)
    ///  35  rider root offset z S16 mm | yaw relative to the target, Angle16
    ///  36-41  reserved 0 (VR head and hands: PLAN C26 - they ride the rigid correction instead)
    ///  42  this client's map signature (EtsSessionWire.MapSignature)
    ///  43  rider epoch (0-7) | rider flags (8: airborne, 9: XR, 10: yaw carried) | rider stamp - int 2,
    ///      S16 ms (16-31): the rider sample's own time on the writer's clock (PLAN §5.3)
    ///  44  traffic hit report (EtsTraffic.HitReport: the last AI car this client's vehicle hit, for the traffic runner;
    ///      written and read straight from the network array - it is past Used, so no slot record copies it)
    ///  45-63  spare
    ///
    /// Measured offline (probe 1674 for the layout, NetTest2 = probe 1823 for this code): 535-663 B/s for a
    /// driving truck and trailer at Fusion's 16 Hz send rate, half of the old float wire's 1131-1342 B/s;
    /// quantisation 8.7 mm at worst (the centimetre grid), the rebuilt trailer within 1 cm.
    /// </summary>
    public static class EtsPlayerWire
    {
        /// <summary>Scene objects "Ets Net Player 0".."Ets Net Player 49" [net §2.3] (the YAML is S/design/session-net/scene_player_slots.yaml).</summary>
        public const string ObjectPrefix = "Ets Net Player ";

        public const int Magic = 0x45545031;
        public const int Ints = 64;

        /// <summary>Ints 0..43 are laid out; 44..63 are spare.</summary>
        public const int Used = 44;

        public const int AtMagic = 0;
        public const int AtSeq = 1;
        public const int AtStamp = 2;
        public const int AtOwner = 3;
        public const int AtStatus = 4;
        public const int AtSerials = 5;
        public const int AtTruck = 6;
        public const int AtPaint = 7;
        public const int AtTruckReserved = 8;
        public const int AtFlags = 9;
        public const int AtPos = 10;
        public const int AtYawPitch = 13;
        public const int AtRollSteer = 14;
        public const int AtVelXZ = 15;
        public const int AtVelYRpm = 16;
        public const int AtPedals = 17;
        public const int AtArticulation = 18;
        public const int AtTrailer = 19;
        public const int AtTrailerCompany = 20;
        public const int AtTrailerPos = 21;
        public const int AtTrailerYawPitch = 24;
        public const int AtTrailerRoll = 25;
        public const int AtSeats12 = 26;
        public const int AtSeats34 = 27;
        public const int AtCargo = 28;
        public const int AtCity = 30;
        public const int AtMoney = 32;
        public const int AtRider = 33;
        public const int AtRiderXY = 34;
        public const int AtRiderZYaw = 35;
        public const int AtReservedHands = 36;
        public const int AtMapSignature = 42;
        public const int AtRiderMeta = 43;
        public const int AtTrafficHit = 44;   // EtsTraffic.HitReport (EtsNet.WriteOwnSlot writes it, EtsNet.ReadSlots hands it on)

        // Int 4, bits 16 and up.
        public const int StatusTruck = 1 << 16;
        public const int StatusJob = 1 << 17;
        public const int StatusVr = 1 << 18;

        /// <summary>
        /// Int 4 bit 19: "I am the room master" - the writer's own NetworkRunner.IsSharedModeMasterClient (PLAN §12, U3 ruling:
        /// the server's Fusion has no GetMasterClient, so each player says it of themselves and the others read it here).
        /// </summary>
        public const int StatusMaster = 1 << 19;

        /// <summary>
        /// Int 4 bit 20: "I have no game" - the writer's EtsFlow.NoGame (no ETS2 archives mounted, D3). A room master with this
        /// bit cannot open the HQ map, so the present player with the game and the lowest PlayerId acts as master for the
        /// session's choice (EtsSlotRules.ActingMaster; integrator, Phase 3).
        /// </summary>
        public const int StatusNoGame = 1 << 20;

        // Int 9: the tractor's flags. Bits 0-2 match EtsNetWire's Flag* (placed, driven, parking brake), so
        // EtsTruckController.ApplyPuppet's parking-brake test reads the same bit from either wire.
        public const int TruckPlaced = 1;
        public const int TruckDriven = 2;
        public const int TruckParkingBrake = 4;
        public const int TruckHorn = 8;
        public const int TruckLowBeam = 16;
        public const int TruckHighBeam = 32;
        public const int TruckBlinkLeft = 64;
        public const int TruckBlinkRight = 128;
        public const int TruckHazard = 256;
        public const int TruckBrakeLights = 512;
        public const int TruckReverse = 1024;
        public const int TruckEngine = 2048;
        public const int TruckTrailer = 4096;
        public const int TruckTrailerCoupled = 8192;
        public const int TruckBeacon = 16384;

        /// <summary>2026-10-14 (headlights): the parking lights (TruckLowBeam and TruckHighBeam, declared above, now carry the beams) and the wipers.</summary>
        public const int TruckParkingLights = 1 << 15;
        public const int TruckWipers = 1 << 16;

        /// <summary>A rider's seat value for standing (not seated) [net §2.4 int 33].</summary>
        public const int SeatStanding = 255;

        /// <summary>Int 7 for EtsTruckIdentity.SavePaint: paint-job byte 0xFF (every bit set).</summary>
        public const int PaintFromSave = -1;

        public enum Status { Free, Loading, ChoosingHq, Waiting, OnFoot, Driving, Passenger, Standing }

        public struct Header
        {
            public int Seq, StampMs, Owner, Generation, SessionEpoch, Progress, Flags;
            public Status Status;

            /// <summary>Int 42: the writer's map signature (0 = not known yet).</summary>
            public int MapSignature;

            public bool Has(int flag)
            {
                return (Flags & flag) != 0;
            }
        }

        /// <summary>
        /// What moves: the tractor's pose and controls, the serials, and the trailer's pose (ints 5, 9-18, 21-25).
        /// Unity frame of the packing or reading client. Articulation is the coupled trailer's rotation relative
        /// to the tractor; TrailerPosition/TrailerRotation are a parked (present, uncoupled) trailer's own pose.
        /// </summary>
        public struct Motion
        {
            public int PlacementEpoch;
            public int TrailerSerial;
            public int Flags;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
            public float Steer;
            public float Rpm;
            public float Throttle;
            public float Brake;
            public int Gear;
            public Quaternion Articulation;
            public Vector3 TrailerPosition;
            public Quaternion TrailerRotation;

            public bool Has(int flag)
            {
                return (Flags & flag) != 0;
            }
        }

        // ------------------------------------------------------------------ header (ints 0-4, 42)

        /// <summary>Ints 0-4 and 42. Owner is a PlayerId (-1 = nobody, packed as 0).</summary>
        public static void PackHeader(in Header h, int[] into)
        {
            into[AtMagic] = Magic;
            into[AtSeq] = h.Seq;
            into[AtStamp] = h.StampMs;
            into[AtOwner] = ((h.Owner < 0 ? 0 : h.Owner + 1) & 0xFFFF) | ((h.Generation & 0xFF) << 16) | ((h.SessionEpoch & 0xFF) << 24);
            into[AtStatus] = ((int)h.Status & 0xF) | ((Mathf.Clamp(h.Progress, 0, 100) & 0xFF) << 8) | (h.Flags & ~0xFFFF);
            into[AtMapSignature] = h.MapSignature;
        }

        /// <summary>False unless the slot holds this build's magic (0 = never written; 'ETS1' and others are not slots).</summary>
        public static bool Unpack(int[] from, out Header h)
        {
            h = default;

            if (from == null || from.Length < Used || from[AtMagic] != Magic)
            {
                return false;
            }

            h.Seq = from[AtSeq];
            h.StampMs = from[AtStamp];
            h.Owner = (from[AtOwner] & 0xFFFF) - 1;
            h.Generation = (from[AtOwner] >> 16) & 0xFF;
            h.SessionEpoch = (from[AtOwner] >> 24) & 0xFF;
            h.Status = (Status)(from[AtStatus] & 0xF);
            h.Progress = (from[AtStatus] >> 8) & 0xFF;
            h.Flags = from[AtStatus] & ~0xFFFF;
            h.MapSignature = from[AtMapSignature];
            return true;
        }

        /// <summary>The slot's owner as a PlayerId, -1 when never written or free.</summary>
        public static int OwnerOf(int[] from)
        {
            return from != null && from.Length > AtOwner && from[AtMagic] == Magic ? (from[AtOwner] & 0xFFFF) - 1 : -1;
        }

        // ------------------------------------------------------------------ truck identity (ints 5 bits 8-15, 6-8)

        /// <summary>
        /// The truck's identity and serial. The serial (+1 per spawned truck) shares int 5 with the placement
        /// epoch and trailer serial (PackMotion); each writer keeps the other's bits. Does not touch the "truck
        /// present" flag, which is the header's (StatusTruck).
        /// </summary>
        public static void PackTruck(in EtsTruckIdentity id, int truckSerial, int[] into)
        {
            into[AtSerials] = (into[AtSerials] & ~0xFF00) | ((truckSerial & 0xFF) << 8);
            int preview = id.Preview < 0 || id.Preview > 254 ? 0xFF : id.Preview;
            into[AtTruck] = preview | ((id.CatalogueHash & 0xFF) << 24);

            // EtsTruckIdentity.SavePaint (-1, "the preview save's own colour", C's Phase 1 addition) is all 32 bits
            // set: masked to 24 bits it read back as 0xFFFFFF, and every watcher drew that truck white (checker
            // probe 1831). The paint-job byte 0xFF says it; a real white is 0x00FFFFFF.
            into[AtPaint] = id.PaintRgb < 0 ? PaintFromSave : id.PaintRgb & 0xFFFFFF;
            into[AtTruckReserved] = 0;
        }

        /// <summary>False unless the slot is written and says a truck is present; the identity has Preview -1 then.</summary>
        public static bool UnpackTruck(int[] from, out EtsTruckIdentity id, out int truckSerial)
        {
            id = default;
            id.Preview = -1;
            truckSerial = 0;

            if (from == null || from.Length < Used || from[AtMagic] != Magic || (from[AtStatus] & StatusTruck) == 0)
            {
                return false;
            }

            int preview = from[AtTruck] & 0xFF;
            id.Preview = preview == 0xFF ? -1 : preview;
            id.CatalogueHash = (from[AtTruck] >> 24) & 0xFF;
            id.PaintRgb = ((from[AtPaint] >> 24) & 0xFF) == 0xFF ? EtsTruckIdentity.SavePaint : from[AtPaint] & 0xFFFFFF;
            truckSerial = (from[AtSerials] >> 8) & 0xFF;
            return true;
        }

        // ------------------------------------------------------------------ motion (ints 5 bits 0-7 and 16-23, 9-18, 21-25)

        /// <summary>The tractor and trailer as they move, relative to the packing client's map centre (game coordinates).</summary>
        public static void PackMotion(in Motion m, Vector3 centerGame, int[] into)
        {
            into[AtSerials] = (into[AtSerials] & 0xFF00) | (m.PlacementEpoch & 0xFF) | ((m.TrailerSerial & 0xFF) << 16);
            into[AtFlags] = m.Flags;
            PutPos(into, AtPos, m.Position, centerGame);
            ToEuler(m.Rotation, out float pitch, out float yaw, out float roll);
            into[AtYawPitch] = Pair(Angle16(yaw), Angle16(pitch));
            into[AtRollSteer] = Pair(Angle16(roll), S16(m.Steer * 32767f));
            into[AtVelXZ] = Pair(S16(m.Velocity.x * 100f), S16(m.Velocity.z * 100f));
            into[AtVelYRpm] = Pair(S16(m.Velocity.y * 100f), Mathf.Clamp(Mathf.RoundToInt(m.Rpm), 0, 65535));
            into[AtPedals] = (m.Gear & 0xFF) | (Mathf.Clamp(Mathf.RoundToInt(m.Throttle * 255f), 0, 255) << 8) | (Mathf.Clamp(Mathf.RoundToInt(m.Brake * 255f), 0, 255) << 16);

            bool coupled = (m.Flags & TruckTrailer) != 0 && (m.Flags & TruckTrailerCoupled) != 0;
            bool parked = (m.Flags & TruckTrailer) != 0 && !coupled;
            float trailerRoll = 0f;

            if (coupled)
            {
                ToEuler(m.Articulation, out float ap, out float ay, out float ar);
                into[AtArticulation] = Pair(Angle16(ay), Angle16(ap));
                trailerRoll = ar;
            }
            else
            {
                into[AtArticulation] = 0;
            }

            if (parked)
            {
                PutPos(into, AtTrailerPos, m.TrailerPosition, centerGame);
                ToEuler(m.TrailerRotation, out float tp, out float ty, out float tr);
                into[AtTrailerYawPitch] = Pair(Angle16(ty), Angle16(tp));
                trailerRoll = tr;
            }
            else
            {
                into[AtTrailerPos] = 0;
                into[AtTrailerPos + 1] = 0;
                into[AtTrailerPos + 2] = 0;
                into[AtTrailerYawPitch] = 0;
            }

            into[AtTrailerRoll] = (coupled || parked) ? Angle16(trailerRoll) : 0;
        }

        /// <summary>The motion in the reading client's frame. False unless the slot is written.</summary>
        public static bool UnpackMotion(int[] from, Vector3 centerGame, out Motion m)
        {
            m = default;
            m.Rotation = Quaternion.identity;
            m.Articulation = Quaternion.identity;
            m.TrailerRotation = Quaternion.identity;

            if (from == null || from.Length < Used || from[AtMagic] != Magic)
            {
                return false;
            }

            m.PlacementEpoch = from[AtSerials] & 0xFF;
            m.TrailerSerial = (from[AtSerials] >> 16) & 0xFF;
            m.Flags = from[AtFlags];
            m.Position = GetPos(from, AtPos, centerGame);
            m.Rotation = FromEuler(FromAngle16(Hi(from[AtYawPitch])), FromAngle16(Lo(from[AtYawPitch])), FromAngle16(Lo(from[AtRollSteer])));
            m.Steer = Hi(from[AtRollSteer]) / 32767f;
            m.Velocity = new Vector3(Lo(from[AtVelXZ]) / 100f, Lo(from[AtVelYRpm]) / 100f, Hi(from[AtVelXZ]) / 100f);
            m.Rpm = (from[AtVelYRpm] >> 16) & 0xFFFF;
            m.Gear = (sbyte)(from[AtPedals] & 0xFF);
            m.Throttle = ((from[AtPedals] >> 8) & 0xFF) / 255f;
            m.Brake = ((from[AtPedals] >> 16) & 0xFF) / 255f;
            float roll = FromAngle16(from[AtTrailerRoll] & 0xFFFF);

            if (m.Has(TruckTrailer) && m.Has(TruckTrailerCoupled))
            {
                m.Articulation = FromEuler(FromAngle16(Hi(from[AtArticulation])), FromAngle16(Lo(from[AtArticulation])), roll);
            }
            else if (m.Has(TruckTrailer))
            {
                m.TrailerPosition = GetPos(from, AtTrailerPos, centerGame);
                m.TrailerRotation = FromEuler(FromAngle16(Hi(from[AtTrailerYawPitch])), FromAngle16(Lo(from[AtTrailerYawPitch])), roll);
            }

            return true;
        }

        /// <summary>
        /// A slot's ints as a sample for EtsNetTimeline, which stays the drawing clock ([net §2.4] "The timeline
        /// stays"). A coupled trailer's articulation rides in TrailerRotation with TrailerPosition zero: the
        /// timeline blends it like any rotation and the reader rebuilds the trailer after Sample (TrailerPose).
        /// A parked trailer's own pose is in TrailerPosition/TrailerRotation. Flags are the Truck* bits.
        /// Epoch folds the placement epoch, the truck serial, the claim generation and the owner together, so
        /// a teleport, a new truck and a new owner each snap - a new owner brings its own clock, and the same
        /// epoch across writers had the timeline refuse every sample on the traffic wire (Program1502, 0 of 50;
        /// EtsTrafficWire.ToTimeline). It stays below 0x10000000: never negative, never the timeline's -1.
        /// </summary>
        public static EtsNetWire.State ToTimeline(int[] ints, Vector3 centerGame)
        {
            if (!UnpackMotion(ints, centerGame, out Motion m))
            {
                return new EtsNetWire.State { Rotation = Quaternion.identity, TrailerRotation = Quaternion.identity, Driver = -1 };
            }

            int owner = (ints[AtOwner] & 0xFFFF) - 1;
            int generation = (ints[AtOwner] >> 16) & 0xFF;
            int truckSerial = (ints[AtSerials] >> 8) & 0xFF;
            bool coupled = m.Has(TruckTrailer) && m.Has(TruckTrailerCoupled);

            return new EtsNetWire.State
            {
                Seq = ints[AtSeq],
                StampMs = ints[AtStamp],
                Driver = m.Has(TruckDriven) ? owner : -1,
                Flags = m.Flags,
                Gear = m.Gear,
                Epoch = ((owner + 1) & 0xF) << 24 | (generation & 0xFF) << 16 | (truckSerial & 0xFF) << 8 | (m.PlacementEpoch & 0xFF),
                TrailerSerial = m.TrailerSerial,
                Position = m.Position,
                Rotation = m.Rotation,
                Velocity = m.Velocity,
                Steer = m.Steer,
                Rpm = m.Rpm,
                Throttle = m.Throttle,
                Brake = m.Brake,
                TrailerPosition = coupled ? Vector3.zero : m.TrailerPosition,
                TrailerRotation = coupled ? m.Articulation : m.TrailerRotation,
                MapId = ints[AtMapSignature]
            };
        }

        /// <summary>
        /// A coupled trailer rebuilt from the drawn tractor and the articulation, as probe 1674 did: the trailer
        /// turns by the articulation about the tractor, and its kingpin (the trailer's "hook" locator, local;
        /// EtsTrailer.cs:140-147) sits on the fifth wheel (the tractor's "hook_t", EtsTruckController.FifthWheel).
        /// </summary>
        public static void TrailerPose(Vector3 tractorPosition, Quaternion tractorRotation, Quaternion articulation, Vector3 fifthWheelLocal, Vector3 kingpinLocal,
                                       out Vector3 trailerPosition, out Quaternion trailerRotation)
        {
            trailerRotation = tractorRotation * articulation;
            trailerPosition = tractorPosition + tractorRotation * fifthWheelLocal - trailerRotation * kingpinLocal;
        }

        /// <summary>The trailer's rotation relative to the tractor (what PackMotion sends for a coupled trailer). Both unit quaternions.</summary>
        public static Quaternion Articulation(Quaternion tractorRotation, Quaternion trailerRotation)
        {
            return Conjugate(tractorRotation) * trailerRotation;
        }

        // ------------------------------------------------------------------ trailer identity (ints 19-20)

        public static void PackTrailer(in EtsTrailerIdentity id, int[] into)
        {
            into[AtTrailer] = (id.Family & 0xFF) | ((id.Variant & 0xFF) << 8) | ((id.Cargo & 0xFFFF) << 16);
            into[AtTrailerCompany] = id.CompanyHash;
        }

        /// <summary>False unless the slot is written and its truck flags say a trailer is present.</summary>
        public static bool UnpackTrailer(int[] from, out EtsTrailerIdentity id)
        {
            id = default;

            if (from == null || from.Length < Used || from[AtMagic] != Magic || (from[AtFlags] & TruckTrailer) == 0)
            {
                return false;
            }

            id.Family = from[AtTrailer] & 0xFF;
            id.Variant = (from[AtTrailer] >> 8) & 0xFF;
            id.Cargo = (from[AtTrailer] >> 16) & 0xFFFF;
            id.CompanyHash = from[AtTrailerCompany];
            return true;
        }

        // ------------------------------------------------------------------ seat table (ints 26-27)

        /// <summary>
        /// The seat table: the PlayerIds of passenger seats 1..4 (index 0 = seat 1; -1 = empty), written by the
        /// truck's owner only (PLAN C8). A late joiner sees who sits where straight from the ints [net §2.7].
        /// </summary>
        public static void PackSeats(int[] occupants, int[] into)
        {
            int s1 = Occupant(occupants, 0), s2 = Occupant(occupants, 1), s3 = Occupant(occupants, 2), s4 = Occupant(occupants, 3);
            into[AtSeats12] = Pair(s1, s2);
            into[AtSeats34] = Pair(s3, s4);
        }

        /// <summary>Fills occupants (seat 1 first, -1 = empty) from ints 26-27. False (all empty) unless the slot is written.</summary>
        public static bool UnpackSeats(int[] from, int[] occupants)
        {
            bool written = from != null && from.Length >= Used && from[AtMagic] == Magic;

            if (occupants == null)
            {
                return written;
            }

            for (int i = 0; i < occupants.Length; i++)
            {
                if (!written || i > 3)
                {
                    occupants[i] = -1;
                    continue;
                }

                int packed = from[i < 2 ? AtSeats12 : AtSeats34];
                int raw = (i & 1) == 0 ? packed & 0xFFFF : (packed >> 16) & 0xFFFF;
                occupants[i] = raw - 1;
            }

            return written;
        }

        private static int Occupant(int[] occupants, int i)
        {
            return occupants != null && i < occupants.Length && occupants[i] >= 0 ? (occupants[i] + 1) & 0xFFFF : 0;
        }

        // ------------------------------------------------------------------ job summary (ints 28-32)

        /// <summary>
        /// The job summary others see, ints 28-32: cargo and destination city tokens, and the money in EUR as a
        /// display value only (PLAN §5.3; the balance itself stays in LocalStorage). Jobs stay per player [net §2.6].
        /// </summary>
        public static void PackJob(ulong cargoToken, ulong destinationCityToken, int moneyEur, int[] into)
        {
            into[AtCargo] = (int)(cargoToken & 0xFFFFFFFFUL);
            into[AtCargo + 1] = (int)(cargoToken >> 32);
            into[AtCity] = (int)(destinationCityToken & 0xFFFFFFFFUL);
            into[AtCity + 1] = (int)(destinationCityToken >> 32);
            into[AtMoney] = moneyEur;
        }

        /// <summary>No job, no money and false unless the slot is written.</summary>
        public static bool UnpackJob(int[] from, out ulong cargoToken, out ulong destinationCityToken, out int moneyEur)
        {
            cargoToken = 0;
            destinationCityToken = 0;
            moneyEur = 0;

            if (from == null || from.Length < Used || from[AtMagic] != Magic)
            {
                return false;
            }

            cargoToken = (uint)from[AtCargo] | ((ulong)(uint)from[AtCargo + 1] << 32);
            destinationCityToken = (uint)from[AtCity] | ((ulong)(uint)from[AtCity + 1] << 32);
            moneyEur = from[AtMoney];
            return true;
        }

        /// <summary>
        /// Int 32's value from the career balance (G's EtsProfileStore.Money, a long; Phase 2): clamped into an int,
        /// since it is a display value only and the balance itself never crosses the wire (PLAN §5.3). The clamp
        /// matters only past €2,147,483,647 - at the demo's pay (probe 1692's jobs, €2,032-€14,104) over 150,000
        /// deliveries.
        /// </summary>
        public static int MoneyDisplay(long euros)
        {
            return euros > int.MaxValue ? int.MaxValue : euros < int.MinValue ? int.MinValue : (int)euros;
        }

        /// <summary>
        /// Int 4's status from what the local player is doing (EtsNet composes it; PLAN §5.2's "Net (A)" column):
        /// Connecting, Loading and Relocating are Loading; the HQ choice is ChoosingHq (an Offline player chooses
        /// their own); WaitingForMaster is Waiting; in the world, Driving at the wheel, Passenger in another seat,
        /// else OnFoot. Standing on a vehicle (B's rider record, Phase 3) is not told here.
        /// </summary>
        public static Status StatusFor(EtsPhase phase, bool seated, bool driver)
        {
            switch (phase)
            {
                case EtsPhase.Offline:
                case EtsPhase.ChoosingHq:
                    return Status.ChoosingHq;
                case EtsPhase.WaitingForMaster:
                    return Status.Waiting;
                case EtsPhase.InWorld:
                    return !seated ? Status.OnFoot : driver ? Status.Driving : Status.Passenger;
                default:
                    return Status.Loading;
            }
        }

        /// <summary>What the local player's own slot says about them (EtsNet fills it at 4 Hz; ComposeOwn packs it).</summary>
        public struct Own
        {
            public int LocalId, SessionEpoch, TruckSerial, MapSignature, StampMs;
            public EtsPhase Phase;
            public float LoadProgress;          // 0..1, shown while Loading or Relocating
            public bool Seated, Driver, TruckPlaced, HasJob, Xr;
            public bool Standing;               // B's rider record says standing on a vehicle (seat 255): status Standing in the world
            public bool RoomMaster;             // IsSharedModeMasterClient: int 4 bit 19 (StatusMaster)
            public bool NoGame;                 // EtsFlow.NoGame: int 4 bit 20 (StatusNoGame)
            public EtsTruckIdentity Truck;
            public ulong Cargo, City;
            public long Money;                  // the career balance (G's EtsProfileStore.Money)
        }

        /// <summary>
        /// The own slot's header (status from StatusFor, load %, the truck/job/VR flags, owner, session epoch, map
        /// signature), the truck identity and serial while a truck is placed, and the job summary with int 32 =
        /// MoneyDisplay(balance) (PLAN §5.3). Leaves the seq, the motion, seats and rider ints as they are: those are
        /// the slot writer's (Phase 3). Returns the status. Probe 2002 round-trips it.
        /// </summary>
        public static Status ComposeOwn(in Own own, int[] into)
        {
            Status status = StatusFor(own.Phase, own.Seated, own.Driver);

            if (status == Status.OnFoot && own.Standing)
            {
                status = Status.Standing;
            }

            bool loading = own.Phase == EtsPhase.Loading || own.Phase == EtsPhase.Relocating;
            float progress = own.LoadProgress < 0f ? 0f : own.LoadProgress > 1f ? 1f : own.LoadProgress;

            var header = new Header
            {
                Seq = into[AtSeq],
                StampMs = own.StampMs,
                Owner = own.LocalId,
                Generation = (into[AtOwner] >> 16) & 0xFF,
                SessionEpoch = own.SessionEpoch,
                Progress = loading ? (int)Math.Round(progress * 100f) : 0,
                Flags = (own.TruckPlaced ? StatusTruck : 0) | (own.HasJob ? StatusJob : 0) | (own.Xr ? StatusVr : 0) | (own.RoomMaster ? StatusMaster : 0) |
                        (own.NoGame ? StatusNoGame : 0),
                Status = status,
                MapSignature = own.MapSignature
            };

            PackHeader(header, into);

            if (own.TruckPlaced)
            {
                PackTruck(own.Truck, own.TruckSerial, into);
            }

            PackJob(own.HasJob ? own.Cargo : 0UL, own.HasJob ? own.City : 0UL, MoneyDisplay(own.Money), into);
            return status;
        }

        // ------------------------------------------------------------------ rider (ints 33-35, 43)

        /// <summary>
        /// This player's rider record. riderStampMs is when the offset was measured, on the same clock as the
        /// slot's stamp (int 2), which must already be in into: int 43 carries the difference (PLAN §5.3), so
        /// the reader recovers the rider's own sample time even when the truck ints were written at another
        /// moment. The difference is clamped to +-32.767 s. Ints 36-41 stay 0 (C26). Kind None clears the record.
        /// </summary>
        public static void PackRider(in EtsRider rider, int riderStampMs, int[] into)
        {
            if (rider.Kind == EtsTargetKind.None)
            {
                ClearRider(into);
                return;
            }

            int seat = rider.Seat < 0 ? 0 : rider.Seat > 255 ? 255 : rider.Seat;
            into[AtRider] = ((int)rider.Kind & 0xF) | ((rider.Index & 0xFF) << 4) | ((rider.Serial & 0xFF) << 12) | (seat << 20) | (rider.Requesting ? 1 << 28 : 0);
            into[AtRiderXY] = Pair(S16(rider.Root.x * 1000f), S16(rider.Root.y * 1000f));
            into[AtRiderZYaw] = Pair(S16(rider.Root.z * 1000f), Angle16(rider.Yaw));

            for (int i = AtReservedHands; i < AtMapSignature; i++)
            {
                into[i] = 0;
            }

            // Wrapping subtraction: both are the writer's millisecond clock, which wraps at 2^31 ms (24.8 days) the
            // same way for both, so the difference is right across the wrap (EtsNetTimeline.Unwrap reads it back).
            int delta = unchecked(riderStampMs - into[AtStamp]);
            int flags = (rider.Airborne ? 1 : 0) | (rider.Xr ? 2 : 0) | (rider.YawCarried ? 4 : 0);
            into[AtRiderMeta] = (rider.Epoch & 0xFF) | (flags << 8) | (S16(delta) << 16);
        }

        /// <summary>No rider (standing on the ground, or seated nowhere): ints 33-41 and 43 zero.</summary>
        public static void ClearRider(int[] into)
        {
            for (int i = AtRider; i < AtMapSignature; i++)
            {
                into[i] = 0;
            }

            into[AtRiderMeta] = 0;
        }

        /// <summary>False when the slot is unwritten or its owner rides nothing. riderStampMs is the rider sample's time on the writer's clock.</summary>
        public static bool UnpackRider(int[] from, out EtsRider rider, out int riderStampMs)
        {
            rider = default;
            riderStampMs = 0;

            if (from == null || from.Length < Used || from[AtMagic] != Magic)
            {
                return false;
            }

            int packed = from[AtRider];
            rider.Kind = (EtsTargetKind)(packed & 0xF);

            if (rider.Kind == EtsTargetKind.None || (int)rider.Kind > 3)
            {
                rider = default;
                return false;
            }

            rider.Index = (packed >> 4) & 0xFF;
            rider.Serial = (packed >> 12) & 0xFF;
            rider.Seat = (packed >> 20) & 0xFF;
            rider.Requesting = (packed & (1 << 28)) != 0;
            rider.Root = new Vector3(Lo(from[AtRiderXY]) / 1000f, Hi(from[AtRiderXY]) / 1000f, Lo(from[AtRiderZYaw]) / 1000f);
            rider.Yaw = FromAngle16(from[AtRiderZYaw] >> 16);
            int meta = from[AtRiderMeta];
            rider.Epoch = meta & 0xFF;
            rider.Airborne = (meta & (1 << 8)) != 0;
            rider.Xr = (meta & (2 << 8)) != 0;
            rider.YawCarried = (meta & (4 << 8)) != 0;
            riderStampMs = unchecked(from[AtStamp] + Hi(meta));
            return true;
        }

        // ------------------------------------------------------------------ WirePos

        /// <summary>
        /// One WirePos axis in centimetres, "game-origin Unity axes" [net §2.4]: round((unity + centre) * 100) for
        /// x and y, round((unity - centre) * 100) for z (negate), because Unity z = centre z - game z
        /// (EtsMapBuilder.ToWorld) and wire z = -game z. In double: the sum of a far float position and the centre
        /// keeps every bit either had.
        /// </summary>
        public static int ToWireCm(float unity, float centre, bool negate)
        {
            double sum = (double)unity + (negate ? -(double)centre : centre);
            return (int)Math.Round(sum * 100.0);
        }

        /// <summary>The inverse of ToWireCm, into the reading client's Unity frame.</summary>
        public static float FromWireCm(int cm, float centre, bool negate)
        {
            return (float)(cm / 100.0 - (negate ? -(double)centre : centre));
        }

        public static void PutPos(int[] into, int at, Vector3 unity, Vector3 centerGame)
        {
            into[at] = ToWireCm(unity.x, centerGame.x, false);
            into[at + 1] = ToWireCm(unity.y, centerGame.y, false);
            into[at + 2] = ToWireCm(unity.z, centerGame.z, true);
        }

        public static Vector3 GetPos(int[] from, int at, Vector3 centerGame)
        {
            return new Vector3(FromWireCm(from[at], centerGame.x, false), FromWireCm(from[at + 1], centerGame.y, false), FromWireCm(from[at + 2], centerGame.z, true));
        }

        // ------------------------------------------------------------------ small encodings

        /// <summary>A turn in 16 bits: round(deg / 360 * 65536), wrapped (0.0055 degree steps).</summary>
        public static int Angle16(float degrees)
        {
            double t = degrees / 360.0;
            t -= Math.Floor(t);
            return (int)Math.Round(t * 65536.0) & 0xFFFF;
        }

        /// <summary>-180..180 degrees from the low 16 bits.</summary>
        public static float FromAngle16(int value)
        {
            double d = (value & 0xFFFF) * (360.0 / 65536.0);
            return (float)(d > 180.0 ? d - 360.0 : d);
        }

        /// <summary>A value clamped to a signed 16-bit range, in the low 16 bits.</summary>
        public static int S16(double value)
        {
            double r = Math.Round(value);
            int v = r < -32768.0 ? -32768 : r > 32767.0 ? 32767 : (int)r;
            return v & 0xFFFF;
        }

        public static int Pair(int lo, int hi)
        {
            return (lo & 0xFFFF) | (hi << 16);
        }

        public static short Lo(int v)
        {
            return (short)(v & 0xFFFF);
        }

        public static short Hi(int v)
        {
            return (short)((v >> 16) & 0xFFFF);
        }

        // ------------------------------------------------------------------ rotations without native calls

        /// <summary>
        /// Quaternion.Euler(pitch, yaw, roll) in plain C#: Unity rotates about z, then x, then y ("applied in that
        /// order", the Unity manual for Quaternion.Euler), which is q = qy * qx * qz. Written out because
        /// Quaternion.Euler is a native call that cannot run in the offline tests.
        /// </summary>
        public static Quaternion FromEuler(float pitch, float yaw, float roll)
        {
            double hx = pitch * (Math.PI / 360.0), hy = yaw * (Math.PI / 360.0), hz = roll * (Math.PI / 360.0);
            double sx = Math.Sin(hx), cx = Math.Cos(hx), sy = Math.Sin(hy), cy = Math.Cos(hy), sz = Math.Sin(hz), cz = Math.Cos(hz);

            // qy * qx = (cy sx, sy cx, -sy sx, cy cx); then * qz.
            double ax = cy * sx, ay = sy * cx, az = -sy * sx, aw = cy * cx;
            return new Quaternion(
                (float)(ax * cz + ay * sz),
                (float)(ay * cz - ax * sz),
                (float)(aw * sz + az * cz),
                (float)(aw * cz - az * sz));
        }

        /// <summary>
        /// The inverse of FromEuler (the same order as Unity's eulerAngles, but -180..180): pitch = asin(2(wx - yz)),
        /// yaw = atan2(2(wy + xz), 1 - 2(x² + y²)), roll = atan2(2(wz + xy), 1 - 2(x² + z²)) - the matrix of
        /// Ry·Rx·Rz read back at [1][2], [0][2]/[2][2] and [1][0]/[1][1].
        /// </summary>
        public static void ToEuler(Quaternion q, out float pitch, out float yaw, out float roll)
        {
            double x = q.x, y = q.y, z = q.z, w = q.w;
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);

            if (n < 1e-9 || double.IsNaN(n))
            {
                pitch = yaw = roll = 0f;
                return;
            }

            x /= n; y /= n; z /= n; w /= n;
            double sinp = 2.0 * (w * x - y * z);
            sinp = sinp < -1.0 ? -1.0 : sinp > 1.0 ? 1.0 : sinp;
            pitch = (float)(Math.Asin(sinp) * (180.0 / Math.PI));
            yaw = (float)(Math.Atan2(2.0 * (w * y + x * z), 1.0 - 2.0 * (x * x + y * y)) * (180.0 / Math.PI));
            roll = (float)(Math.Atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (x * x + z * z)) * (180.0 / Math.PI));
        }

        /// <summary>The yaw of a rotation, degrees -180..180 (as ToEuler; Quaternion.eulerAngles is native).</summary>
        public static float YawOf(Quaternion q)
        {
            ToEuler(q, out _, out float yaw, out _);
            return yaw;
        }

        /// <summary>The inverse of a unit quaternion (Quaternion.Inverse is native).</summary>
        public static Quaternion Conjugate(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, -q.z, q.w);
        }
    }

    /// <summary>
    /// One remote player's rider record, drawn on its own clock: the reading half of ints 33-35 and 43 (owner A;
    /// [net §2.7] "Each offset is interpolated on the rider's slot stamps", PLAN §7.A EtsRemoteRider). The offset
    /// goes through the same EtsNetTimeline as the trucks (stamps on the writer's clock, slewed lag, Hermite, snap
    /// on a new epoch), with the offset as Position, its finite difference as Velocity (the wire carries none,
    /// and a zero velocity makes a walker's Hermite ease in and out at every sample) and the yaw as Rotation.
    /// A seated rider's offset never changes, so nothing is resent and the sample is exact (probe 1823: under
    /// 1 mm on a truck at 25 m/s with the two clients' truck delays 110 ms apart).
    /// </summary>
    public sealed class EtsRiderTrack
    {
        public readonly EtsNetTimeline Timeline = new EtsNetTimeline();

        private EtsRider _last;
        private bool _have;
        private int _lastStamp;
        private int _lastKey = -1;
        private Vector3 _lastRoot;

        /// <summary>A rider record is present in the slot (from the last Add).</summary>
        public bool Present => _have;

        /// <summary>The last record read, with its discrete fields (target, seat, flags).</summary>
        public EtsRider Latest => _last;

        public void Clear()
        {
            _have = false;
            _lastKey = -1;
            Timeline.Clear();
        }

        /// <summary>The slot's ints as read at local time now. False when nothing new was taken.</summary>
        public bool Add(int[] ints, double now)
        {
            if (!EtsPlayerWire.UnpackRider(ints, out EtsRider rider, out int stamp))
            {
                if (_have)
                {
                    Clear();
                }

                return false;
            }

            int key = Key(rider, ints);

            if (_have && key == _lastKey && stamp == _lastStamp)
            {
                // The same sample, carried on by a truck-only write.
                _last = rider;
                return false;
            }

            Vector3 velocity = Vector3.zero;
            double dt = (stamp - (double)_lastStamp) / 1000.0;

            if (_have && key == _lastKey && dt > 1e-3 && dt < 1.0)
            {
                velocity = (rider.Root - _lastRoot) / (float)dt;
            }

            float half = rider.Yaw * (float)(Math.PI / 360.0);
            var state = new EtsNetWire.State
            {
                Seq = stamp,
                StampMs = stamp,
                Epoch = key,
                Position = rider.Root,
                Velocity = velocity,
                Rotation = new Quaternion(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half)),
                TrailerRotation = Quaternion.identity
            };

            _have = true;
            _last = rider;
            _lastKey = key;
            _lastStamp = stamp;
            _lastRoot = rider.Root;
            return Timeline.Add(state, now);
        }

        /// <summary>The rider's offset and yaw at local time now (the discrete fields are the latest record's).</summary>
        public bool Sample(double now, out EtsRider rider, out Vector3 velocity)
        {
            rider = _last;
            velocity = Vector3.zero;

            if (!_have || !Timeline.Sample(now, out EtsNetWire.State s))
            {
                return false;
            }

            rider.Root = s.Position;
            rider.Yaw = EtsPlayerWire.YawOf(s.Rotation);
            velocity = s.Velocity;
            return true;
        }

        /// <summary>
        /// What the timeline snaps on: the rider's epoch, target, seat and the slot's claim generation. Below
        /// 0x10000000, so never negative (the timeline's "none" is -1).
        /// </summary>
        private static int Key(in EtsRider r, int[] ints)
        {
            int seat = r.Seat == EtsPlayerWire.SeatStanding ? 7 : r.Seat & 7;
            int generation = (ints[EtsPlayerWire.AtOwner] >> 16) & 0x7;

            // Bits: epoch 0-7, kind 8-9, index 10-17, seat 18-20, serial 21-24, generation 25-27.
            return (r.Epoch & 0xFF) | (((int)r.Kind & 3) << 8) | ((r.Index & 0xFF) << 10) | (seat << 18) | ((r.Serial & 0xF) << 21) | (generation << 25);
        }
    }

    /// <summary>
    /// What every client makes of the 50 player slots it reads (owner A; [net §2.4] "Reading a slot", [net §2.5],
    /// PLAN.md §5.3, §12). Pure (no Fusion, no scene): the 3-client room of probe 2200 runs these exact rules.
    /// "present" is the room's ActivePlayers as PlayerIds, "slot" a slot's 64 ints as last read.
    /// </summary>
    public static class EtsSlotRules
    {
        public static bool Contains(IReadOnlyList<int> present, int playerId)
        {
            if (present == null || playerId < 0)
            {
                return false;
            }

            for (int i = 0; i < present.Count; i++)
            {
                if (present[i] == playerId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A slot this client may claim [net §2.5 step 2]: never written (magic is not 'ETP1'), written free (owner 0),
        /// owned by a player no longer in the room, or already this client's (a claim that outlived a room blink).
        /// </summary>
        public static bool FreeFor(int[] slot, int localId, IReadOnlyList<int> present)
        {
            if (slot == null || slot.Length < EtsPlayerWire.Used || slot[EtsPlayerWire.AtMagic] != EtsPlayerWire.Magic)
            {
                return true;
            }

            int owner = EtsPlayerWire.OwnerOf(slot);
            return owner < 0 || owner == localId || !Contains(present, owner);
        }

        /// <summary>A slot the player list shows (the Convoys page, the top bar): written, its owner in the room and not this client.</summary>
        public static bool Listed(int[] slot, int localId, IReadOnlyList<int> present)
        {
            int owner = EtsPlayerWire.OwnerOf(slot);
            return owner >= 0 && owner != localId && Contains(present, owner);
        }

        /// <summary>
        /// A slot whose truck and rider are drawn here [net §2.4]: Listed, and written under this client's session epoch
        /// (a player still choosing or waiting in the void is not in the same world yet).
        /// </summary>
        public static bool Counts(int[] slot, int localId, IReadOnlyList<int> present, int sessionEpoch)
        {
            return Listed(slot, localId, present) && ((slot[EtsPlayerWire.AtOwner] >> 24) & 0xFF) == (sessionEpoch & 0xFF);
        }

        /// <summary>
        /// The room master's clean-up [net §2.5 step 5]: SceneNetworking hands the master every object nobody holds
        /// (SceneNetworking.ReassignNullObjectsAuthority on OnPlayerLeft), so a left player's slot comes here naming them.
        /// It is written free once (WriteFree), so no client reads the stale truck twice.
        /// </summary>
        public static bool Orphaned(int[] slot, bool haveAuthority, bool mine, IReadOnlyList<int> present)
        {
            if (!haveAuthority || mine || slot == null || slot.Length < EtsPlayerWire.Used || slot[EtsPlayerWire.AtMagic] != EtsPlayerWire.Magic)
            {
                return false;
            }

            int owner = EtsPlayerWire.OwnerOf(slot);
            return owner >= 0 && !Contains(present, owner);
        }

        /// <summary>
        /// Slot housekeeping, run by whoever holds a slot's authority (4 Hz): write it free when it names a player who has
        /// left (the master's clean-up, Orphaned), or this client itself while it is not the slot this client claims - a
        /// claim record left behind when a simultaneous claim moved this client on to another slot. Without the second
        /// rule the stale record keeps the slot taken and lists this player twice in everyone's Convoys page.
        /// </summary>
        public static bool ToFree(int[] slot, bool haveAuthority, bool claimedHere, int localId, IReadOnlyList<int> present)
        {
            if (!haveAuthority || claimedHere || slot == null || slot.Length < EtsPlayerWire.Used || slot[EtsPlayerWire.AtMagic] != EtsPlayerWire.Magic)
            {
                return false;
            }

            int owner = EtsPlayerWire.OwnerOf(slot);
            return owner >= 0 && (owner == localId || !Contains(present, owner));
        }

        /// <summary>The free record: 'ETP1', seq + 1, the stamp, owner 0 with the claim generation kept, status Free, all else 0.</summary>
        public static void WriteFree(int[] into, int stampMs)
        {
            int seq = into[EtsPlayerWire.AtSeq] + 1;
            int generation = into[EtsPlayerWire.AtMagic] == EtsPlayerWire.Magic ? (into[EtsPlayerWire.AtOwner] >> 16) & 0xFF : 0;
            Array.Clear(into, 0, into.Length);
            into[EtsPlayerWire.AtMagic] = EtsPlayerWire.Magic;
            into[EtsPlayerWire.AtSeq] = seq;
            into[EtsPlayerWire.AtStamp] = stampMs;
            into[EtsPlayerWire.AtOwner] = generation << 16;
        }

        /// <summary>
        /// A fresh claim [net §2.5 step 2]: every int cleared, then owner = this client, the new generation, status
        /// Loading and the session epoch. The rest (truck, job, money, rider) is composed onto it by the owner's writes.
        /// </summary>
        public static void WriteClaim(int[] into, int localId, int generation, int sessionEpoch, int stampMs)
        {
            int seq = into[EtsPlayerWire.AtMagic] == EtsPlayerWire.Magic ? into[EtsPlayerWire.AtSeq] + 1 : 1;
            Array.Clear(into, 0, into.Length);
            EtsPlayerWire.PackHeader(new EtsPlayerWire.Header
            {
                Seq = seq,
                StampMs = stampMs,
                Owner = localId,
                Generation = generation,
                SessionEpoch = sessionEpoch,
                Status = EtsPlayerWire.Status.Loading
            }, into);
        }

        /// <summary>
        /// Who the room master is, as the slots say it (int 4 bit 19, each player's own IsSharedModeMasterClient): the
        /// owner of the first Listed slot carrying the bit, -1 when none does (this client itself is never named here;
        /// it asks the runner). Two at once only during a hand-over, for one 4 Hz write; the first slot wins.
        /// </summary>
        public static int MasterOf(int[][] slots, int count, int localId, IReadOnlyList<int> present)
        {
            for (int k = 0; k < count; k++)
            {
                int[] slot = slots[k];

                if (Listed(slot, localId, present) && (slot[EtsPlayerWire.AtStatus] & EtsPlayerWire.StatusMaster) != 0)
                {
                    return EtsPlayerWire.OwnerOf(slot);
                }
            }

            return -1;
        }

        /// <summary>
        /// Who acts as the master (chooses the headquarters, is named "Master", wears the star): the room master
        /// (IsSharedModeMasterClient, int 4 bit 19) - unless that player has no game (bit 20, StatusNoGame). A visitor
        /// without ETS2 cannot open the HQ map (nothing of the game ships with the world), and a no-game visitor who was
        /// first in held every later joiner on "Waiting for {name}" until they left (D's checker, Request 2). Then the
        /// present player with the game whose slot is claimed and whose PlayerId is lowest acts for them: Fusion's ids
        /// grow with the join order, so that is the longest-present one, and every client reaches the same answer from
        /// the same slots. Only the session's choice moves (EtsSession feeds this to EtsSessionLogic as IsMaster, which
        /// then asks the session object's authority with RequestStateAuthority, as a new master always has); the room's
        /// own master still holds the orphaned objects (SceneNetworking), which the slot clean-up needs. -1: no master
        /// known yet. The no-game room master itself when nobody present has the game (nobody is then waiting on it).
        /// localClaimed: this client's own slot is claimed (the others can read its bit 20), so it may stand in.
        /// </summary>
        public static int ActingMaster(int[][] slots, int count, int localId, IReadOnlyList<int> present, bool localRoomMaster, bool localNoGame, bool localClaimed)
        {
            int master = -1;
            bool masterNoGame = false;

            if (localRoomMaster)
            {
                master = localId;
                masterNoGame = localNoGame;
            }
            else
            {
                for (int k = 0; k < count; k++)
                {
                    int[] slot = slots[k];

                    if (Listed(slot, localId, present) && (slot[EtsPlayerWire.AtStatus] & EtsPlayerWire.StatusMaster) != 0)
                    {
                        master = EtsPlayerWire.OwnerOf(slot);
                        masterNoGame = (slot[EtsPlayerWire.AtStatus] & EtsPlayerWire.StatusNoGame) != 0;
                        break;
                    }
                }
            }

            if (master < 0 || !masterNoGame)
            {
                return master;
            }

            int best = !localNoGame && localClaimed && localId >= 0 ? localId : int.MaxValue;

            for (int k = 0; k < count; k++)
            {
                int[] slot = slots[k];

                if (Listed(slot, localId, present) && (slot[EtsPlayerWire.AtStatus] & EtsPlayerWire.StatusNoGame) == 0)
                {
                    int owner = EtsPlayerWire.OwnerOf(slot);

                    if (owner < best)
                    {
                        best = owner;
                    }
                }
            }

            return best == int.MaxValue ? master : best;
        }

        /// <summary>
        /// Another present player is loading or in the world under a chosen session (epoch 1 or more): EtsSessionLogic's
        /// OthersHaveSession guard against a late join snapshot (probe 1824 case 5).
        /// </summary>
        public static bool OthersHaveSession(int[][] slots, int count, int localId, IReadOnlyList<int> present)
        {
            for (int k = 0; k < count; k++)
            {
                int[] slot = slots[k];

                if (!Listed(slot, localId, present) || ((slot[EtsPlayerWire.AtOwner] >> 24) & 0xFF) == 0)
                {
                    continue;
                }

                var status = (EtsPlayerWire.Status)(slot[EtsPlayerWire.AtStatus] & 0xF);

                if (status == EtsPlayerWire.Status.Loading || status >= EtsPlayerWire.Status.OnFoot)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Other players' trucks on the road ("truck present" in a Listed slot): the MaxTrucks count [net §2.6 step 1].</summary>
        public static int OtherTrucks(int[][] slots, int count, int localId, IReadOnlyList<int> present)
        {
            int n = 0;

            for (int k = 0; k < count; k++)
            {
                if (Listed(slots[k], localId, present) && (slots[k][EtsPlayerWire.AtStatus] & EtsPlayerWire.StatusTruck) != 0)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// A rider's truck serial against the truck's: equal, or 0 on the rider's side. 0 is "whatever truck that slot has
        /// now": a serial is 1-255 once a truck is spawned, and a rider record written before the rider knew the serial
        /// (B's Phase 2 EtsRiders wrote 0) still names the slot's current truck.
        /// </summary>
        public static bool SerialMatches(int riderSerial, int truckSerial)
        {
            return (riderSerial & 0xFF) == 0 || (riderSerial & 0xFF) == (truckSerial & 0xFF);
        }
    }

    /// <summary>
    /// This client's claim on one player slot [net §2.5] (owner A). Pure: EtsNet feeds it what the room says each frame
    /// and carries out what it asks (RequestStateAuthority on AskAuthority; write the claim record on WriteClaim). The
    /// probe 2200 room runs three of these against a simulated Fusion (last authority request wins, writes without
    /// authority dropped, a delay on everything) through joins, leaves, simultaneous claims and a master hand-over.
    ///   Idle -> Asking: the first free slot from LocalId % 50 on (EtsSlotRules.FreeFor), not tried in this round.
    ///   Asking -> Confirming: authority came (within AuthorityWait); the claim record is written (generation + 1).
    ///   Confirming -> Owned: ConfirmAfter later the slot still names this client and it still holds authority. Another
    ///     present player named there (two claimed at once; Fusion gave the last request authority and dropped the
    ///     other's writes) sends it on to the next free slot: "slot k lost to player N". Authority lost while the slot
    ///     still names this client is asked back after LostPatience, as in Owned (a later claimer that read this record
    ///     first moves on holding the authority; checker probe 2215).
    ///   Owned: authority lost for LostPatience (1.5 s) with the slot still naming this client is asked back once a
    ///     second (as the old LostTheWheel did); another present player named there means it was taken over, and the
    ///     claim starts again. A slot read free while holding authority is claimed afresh (new generation).
    /// Nothing is sent on leaving: the slot's owner simply stops being present, and the master writes it free.
    /// </summary>
    public sealed class EtsSlotClaim
    {
        public const int Slots = 50;
        public const double AuthorityWait = 3.0;
        public const double ConfirmAfter = 1.0;
        public const double AskEvery = 1.0;
        public const double LostPatience = 1.5;
        public const double RoundPause = 5.0;

        public enum Stage { Idle, Asking, Confirming, Owned }

        public struct Inputs
        {
            public double Now;
            public int LocalId;

            /// <summary>The room is up, the slots are found and the session is read (not Connecting or Offline) [net §2.5 step 2].</summary>
            public bool Allowed;

            /// <summary>How many slot objects there are (50 with the Phase 2 scene).</summary>
            public int SlotCount;
        }

        public Stage Current { get; private set; }

        /// <summary>The slot being claimed or held, -1 when none.</summary>
        public int Slot { get; private set; } = -1;

        /// <summary>The claim generation written (int 3 bits 16-23).</summary>
        public int Generation { get; private set; }

        /// <summary>This step: ask for this slot's state authority (-1: nothing to ask).</summary>
        public int AskAuthority { get; private set; } = -1;

        /// <summary>This step: write the claim record (EtsSlotRules.WriteClaim with Generation) into Slot.</summary>
        public bool WriteClaim { get; private set; }

        /// <summary>The slot may be written this step (with authority): being confirmed or owned.</summary>
        public bool Holding => Slot >= 0 && (Current == Stage.Confirming || Current == Stage.Owned);

        /// <summary>Time from the first ask to Owned, seconds (for the log).</summary>
        public double ClaimSeconds { get; private set; }

        private readonly bool[] _tried = new bool[Slots];
        private double _since, _nextAsk, _lostAt = -1.0, _roundAt = double.NegativeInfinity, _began;
        private int _localId = -1;
        private string _note;

        /// <summary>The last thing worth a log line, once (null when nothing happened).</summary>
        public string TakeNote()
        {
            string note = _note;
            _note = null;
            return note;
        }

        public void Reset()
        {
            Current = Stage.Idle;
            Slot = -1;
            _lostAt = -1.0;
            Array.Clear(_tried, 0, _tried.Length);
            _roundAt = double.NegativeInfinity;
        }

        public void Step(in Inputs input, int[][] slots, bool[] valid, bool[] authority, IReadOnlyList<int> present)
        {
            AskAuthority = -1;
            WriteClaim = false;

            if (!input.Allowed || input.LocalId < 0 || input.LocalId != _localId)
            {
                if (Current != Stage.Idle || Slot >= 0)
                {
                    _note = "claim dropped (" + (!input.Allowed ? "the room or the session is not up" : "a new local player id") + ")";
                }

                Reset();
                _localId = input.LocalId;

                if (!input.Allowed || input.LocalId < 0)
                {
                    return;
                }
            }

            double now = input.Now;
            int count = Math.Min(Math.Min(input.SlotCount, Slots), slots.Length);

            switch (Current)
            {
                case Stage.Idle:
                {
                    if (now < _roundAt)
                    {
                        return;
                    }

                    int k = FindFree(input.LocalId, count, slots, valid, present);

                    if (k < 0)
                    {
                        bool any = false;

                        for (int i = 0; i < count; i++)
                        {
                            any |= _tried[i];
                        }

                        Array.Clear(_tried, 0, _tried.Length);
                        _roundAt = now + RoundPause;
                        _note = any ? "no slot could be claimed this round; trying all again in " + RoundPause + " s" : "no free slot (" + count + " slots)";
                        return;
                    }

                    Slot = k;
                    Current = Stage.Asking;
                    _since = now;
                    _began = now;
                    _nextAsk = now + AskEvery;
                    AskAuthority = k;
                    return;
                }

                case Stage.Asking:
                {
                    if (!valid[Slot] || !EtsSlotRules.FreeFor(slots[Slot], input.LocalId, present))
                    {
                        Give("slot " + Slot + " taken by player " + EtsPlayerWire.OwnerOf(slots[Slot]) + " before the claim; trying the next");
                        return;
                    }

                    if (authority[Slot])
                    {
                        int[] slot = slots[Slot];
                        Generation = ((slot[EtsPlayerWire.AtMagic] == EtsPlayerWire.Magic ? (slot[EtsPlayerWire.AtOwner] >> 16) & 0xFF : 0) + 1) & 0xFF;
                        WriteClaim = true;
                        Current = Stage.Confirming;
                        _since = now;
                        return;
                    }

                    if (now - _since >= AuthorityWait)
                    {
                        Give("no authority over slot " + Slot + " in " + AuthorityWait + " s; trying the next");
                        return;
                    }

                    if (now >= _nextAsk)
                    {
                        AskAuthority = Slot;
                        _nextAsk = now + AskEvery;
                    }

                    return;
                }

                case Stage.Confirming:
                {
                    int owner = EtsPlayerWire.OwnerOf(slots[Slot]);

                    if (owner >= 0 && owner != input.LocalId && EtsSlotRules.Contains(present, owner))
                    {
                        Give("slot " + Slot + " lost to player " + owner + ", trying " + ((Slot + 1) % Math.Max(1, count)));
                        return;
                    }

                    if (!authority[Slot])
                    {
                        if (owner == input.LocalId)
                        {
                            // Our claim record stands, but a later request took the authority. Either a simultaneous
                            // claimer, whose own record replaces ours within a round trip (the "lost" case above), or one
                            // that read our record before its grant came, moved on to another slot and now holds an
                            // authority it never uses: giving up here left our record in the slot for good, nobody able
                            // or willing to clear it, and this player listed twice in every client's player list
                            // (checker probe 2215: 24 of 161 start offsets at 60/60 ms). So, as Owned does, it is asked
                            // back after LostPatience, and confirmed afresh once it is here again.
                            if (_lostAt < 0.0)
                            {
                                _lostAt = now;
                                _nextAsk = now + LostPatience;
                            }
                            else if (now >= _nextAsk)
                            {
                                AskAuthority = Slot;
                                _nextAsk = now + AskEvery;
                            }

                            return;
                        }

                        // Another claimer's request came after ours: its write names it shortly (above). If none comes, try elsewhere.
                        if (now - _since >= AuthorityWait)
                        {
                            Give("authority over slot " + Slot + " went elsewhere and nobody wrote it; trying the next");
                        }

                        return;
                    }

                    if (_lostAt >= 0.0)
                    {
                        // The authority is back (above): a full ConfirmAfter again before the slot counts as owned.
                        _lostAt = -1.0;
                        _since = now;
                        return;
                    }

                    if (owner != input.LocalId)
                    {
                        // Our claim record was overwritten (a master clean-up racing it): write it again.
                        if (now - _since >= ConfirmAfter)
                        {
                            WriteClaim = true;
                            _since = now;
                        }

                        return;
                    }

                    if (now - _since >= ConfirmAfter)
                    {
                        Current = Stage.Owned;
                        ClaimSeconds = now - _began;
                        _lostAt = -1.0;
                        Array.Clear(_tried, 0, _tried.Length);
                        _note = "slot " + Slot + " claimed (generation " + Generation + ") in " + Math.Round(ClaimSeconds * 1000.0) + " ms";
                    }

                    return;
                }

                case Stage.Owned:
                {
                    int owner = EtsPlayerWire.OwnerOf(slots[Slot]);

                    if (owner >= 0 && owner != input.LocalId && EtsSlotRules.Contains(present, owner))
                    {
                        int lost = Slot;
                        Reset();
                        _note = "slot " + lost + " was taken over by player " + owner + "; claiming again";
                        return;
                    }

                    if (!authority[Slot])
                    {
                        if (_lostAt < 0.0)
                        {
                            _lostAt = now;
                            _nextAsk = now + LostPatience;
                        }
                        else if (now >= _nextAsk)
                        {
                            AskAuthority = Slot;
                            _nextAsk = now + AskEvery;
                        }

                        return;
                    }

                    _lostAt = -1.0;

                    if (owner != input.LocalId)
                    {
                        Generation = (Generation + 1) & 0xFF;
                        WriteClaim = true;
                        _note = "slot " + Slot + " read " + (owner < 0 ? "free" : "as player " + owner + "'s (gone)") + " while held; claimed again (generation " + Generation + ")";
                    }

                    return;
                }
            }
        }

        private void Give(string why)
        {
            if (Slot >= 0 && Slot < _tried.Length)
            {
                _tried[Slot] = true;
            }

            Current = Stage.Idle;
            Slot = -1;
            _lostAt = -1.0;
            _note = why;
        }

        private int FindFree(int localId, int count, int[][] slots, bool[] valid, IReadOnlyList<int> present)
        {
            if (count <= 0)
            {
                return -1;
            }

            int k0 = localId % count;

            for (int i = 0; i < count; i++)
            {
                int k = (k0 + i) % count;

                if (valid[k] && !_tried[k] && EtsSlotRules.FreeFor(slots[k], localId, present))
                {
                    return k;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// A truck owner's passenger seats 1-4, ints 26-27 of its own slot [net §2.7], PLAN C8: only the owner writes them
    /// (owner A). Pure. Every ServeEvery (0.25 s) EtsNet offers it each Listed slot's rider record (Offer) and closes the
    /// pass (End): a request naming this truck and a free seat gets it; a taken seat is left alone ([net §2.7] step 2);
    /// an occupant whose record has not named this truck, seat and serial for StaleAfter (2 s), or who left the room,
    /// or a truck gone, frees the seat (step 4). A late joiner sees who sits where straight from the ints.
    /// </summary>
    public sealed class EtsSeatTable
    {
        public const int Seats = 4;
        public const double ServeEvery = 0.25;
        public const double StaleAfter = 2.0;

        /// <summary>Seat 1..4 occupants' PlayerIds (index 0 = seat 1), -1 empty: what PackSeats writes.</summary>
        public readonly int[] Occupants = { -1, -1, -1, -1 };

        private readonly double[] _staleSince = { -1.0, -1.0, -1.0, -1.0 };
        private readonly bool[] _seen = new bool[Seats];
        private string _note;

        /// <summary>The table changed since the owner last packed it.</summary>
        public bool Changed { get; set; }

        public string TakeNote()
        {
            string note = _note;
            _note = null;
            return note;
        }

        public void Clear()
        {
            for (int i = 0; i < Seats; i++)
            {
                if (Occupants[i] >= 0)
                {
                    Changed = true;
                }

                Occupants[i] = -1;
                _staleSince[i] = -1.0;
            }
        }

        public bool Holds(int playerId)
        {
            for (int i = 0; i < Seats; i++)
            {
                if (Occupants[i] == playerId && playerId >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        public void Begin()
        {
            Array.Clear(_seen, 0, Seats);
        }

        /// <summary>One remote player's rider record, from a Listed slot (mySlot and myTruckSerial are this owner's).</summary>
        public void Offer(int playerId, in EtsRider rider, int mySlot, int myTruckSerial)
        {
            if (playerId < 0 || mySlot < 0 || rider.Kind != EtsTargetKind.Tractor || rider.Index != mySlot || rider.Seat < 1 || rider.Seat > Seats ||
                !EtsSlotRules.SerialMatches(rider.Serial, myTruckSerial))
            {
                return;
            }

            int s = rider.Seat - 1;

            if (Occupants[s] == playerId)
            {
                _seen[s] = true;
                return;
            }

            if (!rider.Requesting || Occupants[s] >= 0)
            {
                return;
            }

            // One seat per player: a request for another seat moves them.
            for (int i = 0; i < Seats; i++)
            {
                if (Occupants[i] == playerId)
                {
                    Occupants[i] = -1;
                    _staleSince[i] = -1.0;
                }
            }

            Occupants[s] = playerId;
            _seen[s] = true;
            _staleSince[s] = -1.0;
            Changed = true;
            _note = "seat " + rider.Seat + " granted to player " + playerId;
        }

        public void End(double now, IReadOnlyList<int> present, bool haveTruck)
        {
            for (int i = 0; i < Seats; i++)
            {
                int who = Occupants[i];

                if (who < 0)
                {
                    continue;
                }

                if (!haveTruck || !EtsSlotRules.Contains(present, who))
                {
                    Free(i, !haveTruck ? "the truck is gone" : "player " + who + " left");
                    continue;
                }

                if (_seen[i])
                {
                    _staleSince[i] = -1.0;
                    continue;
                }

                if (_staleSince[i] < 0.0)
                {
                    _staleSince[i] = now;
                }
                else if (now - _staleSince[i] >= StaleAfter)
                {
                    Free(i, "player " + who + "'s rider record has not named it for " + StaleAfter + " s");
                }
            }
        }

        private void Free(int i, string why)
        {
            _note = "seat " + (i + 1) + " freed: " + why;
            Occupants[i] = -1;
            _staleSince[i] = -1.0;
            Changed = true;
        }
    }

    /// <summary>
    /// A passenger's side of a seat request [net §2.7] steps 1 and 3 (owner A). Pure. The requester's rider record says
    /// "requesting seat s of slot k" (EtsNet writes it); the answer is the owner's seat table: this player named there is
    /// Granted, anyone else Taken, nothing within Timeout (3 s) NoAnswer, and the owner's truck vanishing (left, despawned,
    /// a new serial) Gone. State-based on purpose: no RPC to lose.
    /// </summary>
    public sealed class EtsSeatRequest
    {
        public const double Timeout = 3.0;

        public enum Answer { None, Waiting, Granted, Taken, NoAnswer, Gone }

        public bool Active { get; private set; }
        public int OwnerSlot { get; private set; } = -1;
        public int OwnerId { get; private set; } = -1;
        public int Seat { get; private set; }
        public int TruckSerial { get; private set; }
        public double Since { get; private set; }

        private readonly int[] _occupants = new int[EtsSeatTable.Seats];

        public void Begin(int ownerSlot, int ownerId, int seat, int truckSerial, double now)
        {
            Active = true;
            OwnerSlot = ownerSlot;
            OwnerId = ownerId;
            Seat = seat;
            TruckSerial = truckSerial & 0xFF;
            Since = now;
        }

        public void End()
        {
            Active = false;
        }

        /// <summary>ownerSlot: the owner's slot as read this frame, or null when it no longer counts here.</summary>
        public Answer Step(int[] ownerSlot, int localId, double now)
        {
            if (!Active)
            {
                return Answer.None;
            }

            if (ownerSlot == null || EtsPlayerWire.OwnerOf(ownerSlot) != OwnerId || !EtsPlayerWire.UnpackTruck(ownerSlot, out _, out int serial) ||
                !EtsSlotRules.SerialMatches(TruckSerial, serial))
            {
                Active = false;
                return Answer.Gone;
            }

            EtsPlayerWire.UnpackSeats(ownerSlot, _occupants);
            int who = Seat >= 1 && Seat <= EtsSeatTable.Seats ? _occupants[Seat - 1] : -1;

            if (who == localId)
            {
                Active = false;
                return Answer.Granted;
            }

            if (who >= 0)
            {
                Active = false;
                return Answer.Taken;
            }

            if (now - Since >= Timeout)
            {
                Active = false;
                return Answer.NoAnswer;
            }

            return Answer.Waiting;
        }
    }
}
