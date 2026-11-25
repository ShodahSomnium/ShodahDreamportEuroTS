using System;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>A client's phase in the session flow (owner A; [net §2.2], PLAN.md §5.2, §7.A).</summary>
    public enum EtsPhase { Connecting, Offline, WaitingForMaster, ChoosingHq, Loading, InWorld, Relocating }

    /// <summary>
    /// The master's headquarters choice: city, yard, origin and arrival (owner A; PLAN §7.A, §3.6; made by D's HQ
    /// stage through EtsSession.ChooseHeadquarters). Game coordinates, as EtsMapViewer's centre. On the wire
    /// (EtsSessionWire) positions are centimetres, so a choice read back is within 5 mm per axis of the one made.
    /// </summary>
    public struct EtsHqChoice
    {
        public ulong City;          // city token (EtsToken)
        public ulong YardPrefabUid; // map prefab uid of the yard
        public ulong YardCompany;   // company token, 0 for a garage
        public Vector3 OriginGame;  // becomes EtsMapViewer's centre (game coordinates)
        public Vector3 ArrivalGame; // where players are teleported (game coordinates)
        public float ArrivalYaw;    // degrees, Unity yaw
    }

    /// <summary>
    /// The session object's 64 ints, "Ets Net Session" (today's "Ets Net Truck 0", renamed in Phase 2 with the
    /// same fileIDs and SortKey 3877532583): what the instance master chose, which every client reads before it
    /// loads anything (owner A; [net §2.2, §2.4, §2.11], PLAN.md §5.3, §3.11). Only the master writes it. Pure,
    /// like EtsNetWire and EtsPlayerWire, so the offline tests (probe 1824) run it outside Unity.
    ///
    ///   0  magic 'ETSS' (0 = unwritten; 'ETS1' is the old truck wire on the same object: also "unwritten")
    ///   1  seq, bumped on every write
    ///   2  writer PlayerId+1 (0-15) | phase (16-23: 1 Choosing, 2 Chosen) | wire version (24-31) = 1
    ///   3  session epoch: 1 at the first choice (+1 on each HQ change, Phase 5 only: Q-A1 is "no")
    ///   4  HQ city token (2 ints), 6 yard prefab uid (2), 8 yard company token (2; 0 for a garage)
    ///  10  map origin, WirePos cm (x = game x, y = game y, z = -game z): every client's EtsMapViewer centre
    ///  13  arrival point, WirePos cm: where joiners are teleported
    ///  16  arrival yaw Angle16 (0-15) | options (16-31: bit 0 traffic on, bit 1 standing on vehicles allowed)
    ///  17  the master's map signature (MapSignature below); each reader compares its own
    ///  18  MaxTrucks the master allows (0 = the default, 16)
    ///  19  preview city token (2 ints): the master's hover while Choosing, lit on the watchers' maps
    ///  21  game clock, minutes since Monday 00:00 (600 = Monday 10:00, /def/initial_save/normal/game.sii)
    ///  22-63  spare
    /// </summary>
    public static class EtsSessionWire
    {
        /// <summary>The scene object once Phase 2 renames it (m_Name at SCENE:1521; fileIDs 2900000110-113 unchanged).</summary>
        public const string ObjectName = "Ets Net Session";

        /// <summary>The same scene object's name until then; the old single-truck EtsNet still reads it (EtsNet.StateObjectName).</summary>
        public const string OldObjectName = "Ets Net Truck 0";

        public const int Magic = 0x45545353;
        public const int Ints = 64;
        public const int Version = 1;
        public const int Used = 22;

        public const int AtMagic = 0;
        public const int AtSeq = 1;
        public const int AtHeader = 2;
        public const int AtEpoch = 3;
        public const int AtCity = 4;
        public const int AtYardPrefab = 6;
        public const int AtYardCompany = 8;
        public const int AtOrigin = 10;
        public const int AtArrival = 13;
        public const int AtArrivalYaw = 16;
        public const int AtMapSignature = 17;
        public const int AtMaxTrucks = 18;
        public const int AtPreviewCity = 19;
        public const int AtClock = 21;

        public const int OptionTraffic = 1;
        public const int OptionStanding = 2;

        /// <summary>Game minutes at the start: /def/initial_save/normal/game.sii game_time: 600 (Monday 10:00; PLAN §3.11).</summary>
        public const int StartClockMinutes = 600;

        public enum Phase { None = 0, Choosing = 1, Chosen = 2 }

        public struct Session
        {
            public int Seq, Writer, Epoch, MapSignature, MaxTrucks, ClockMinutes, Options;
            public Phase Phase;
            public ulong City, YardPrefab, YardCompany, PreviewCity;
            public Vector3Int OriginCm, ArrivalCm;
            public float ArrivalYaw;
        }

        /// <summary>Writer is a PlayerId (-1 = nobody, packed as 0).</summary>
        public static void Pack(in Session s, int[] into)
        {
            into[AtMagic] = Magic;
            into[AtSeq] = s.Seq;
            into[AtHeader] = ((s.Writer < 0 ? 0 : s.Writer + 1) & 0xFFFF) | (((int)s.Phase & 0xFF) << 16) | (Version << 24);
            into[AtEpoch] = s.Epoch;
            PutToken(into, AtCity, s.City);
            PutToken(into, AtYardPrefab, s.YardPrefab);
            PutToken(into, AtYardCompany, s.YardCompany);
            into[AtOrigin] = s.OriginCm.x;
            into[AtOrigin + 1] = s.OriginCm.y;
            into[AtOrigin + 2] = s.OriginCm.z;
            into[AtArrival] = s.ArrivalCm.x;
            into[AtArrival + 1] = s.ArrivalCm.y;
            into[AtArrival + 2] = s.ArrivalCm.z;
            into[AtArrivalYaw] = EtsPlayerWire.Angle16(s.ArrivalYaw) | ((s.Options & 0xFFFF) << 16);
            into[AtMapSignature] = s.MapSignature;
            into[AtMaxTrucks] = s.MaxTrucks;
            PutToken(into, AtPreviewCity, s.PreviewCity);
            into[AtClock] = s.ClockMinutes;
        }

        /// <summary>False unless magic and version match: zeros, 'ETS1' and another build's version all read as unwritten.</summary>
        public static bool Unpack(int[] from, out Session s)
        {
            s = default;

            if (from == null || from.Length < Used || from[AtMagic] != Magic || ((from[AtHeader] >> 24) & 0xFF) != Version)
            {
                return false;
            }

            s.Seq = from[AtSeq];
            s.Writer = (from[AtHeader] & 0xFFFF) - 1;
            s.Phase = (Phase)((from[AtHeader] >> 16) & 0xFF);
            s.Epoch = from[AtEpoch];
            s.City = GetToken(from, AtCity);
            s.YardPrefab = GetToken(from, AtYardPrefab);
            s.YardCompany = GetToken(from, AtYardCompany);
            s.OriginCm = new Vector3Int(from[AtOrigin], from[AtOrigin + 1], from[AtOrigin + 2]);
            s.ArrivalCm = new Vector3Int(from[AtArrival], from[AtArrival + 1], from[AtArrival + 2]);
            s.ArrivalYaw = EtsPlayerWire.FromAngle16(from[AtArrivalYaw] & 0xFFFF);
            s.Options = (from[AtArrivalYaw] >> 16) & 0xFFFF;
            s.MapSignature = from[AtMapSignature];
            s.MaxTrucks = from[AtMaxTrucks];
            s.PreviewCity = GetToken(from, AtPreviewCity);
            s.ClockMinutes = from[AtClock];
            return s.Phase == Phase.Choosing || s.Phase == Phase.Chosen;
        }

        /// <summary>A game position as WirePos centimetres: (game x, game y, -game z) * 100 [net §2.4].</summary>
        public static Vector3Int GameToWire(Vector3 game)
        {
            return new Vector3Int((int)Math.Round(game.x * 100.0), (int)Math.Round(game.y * 100.0), (int)Math.Round(-(double)game.z * 100.0));
        }

        public static Vector3 WireToGame(Vector3Int cm)
        {
            return new Vector3((float)(cm.x / 100.0), (float)(cm.y / 100.0), (float)(-cm.z / 100.0));
        }

        /// <summary>The choice a Chosen session describes, in game coordinates (EtsHqChoice, PLAN §7.A).</summary>
        public static EtsHqChoice ToChoice(in Session s)
        {
            return new EtsHqChoice
            {
                City = s.City,
                YardPrefabUid = s.YardPrefab,
                YardCompany = s.YardCompany,
                OriginGame = WireToGame(s.OriginCm),
                ArrivalGame = WireToGame(s.ArrivalCm),
                ArrivalYaw = s.ArrivalYaw
            };
        }

        public static void FromChoice(in EtsHqChoice c, ref Session s)
        {
            s.City = c.City;
            s.YardPrefab = c.YardPrefabUid;
            s.YardCompany = c.YardCompany;
            s.OriginCm = GameToWire(c.OriginGame);
            s.ArrivalCm = GameToWire(c.ArrivalGame);
            s.ArrivalYaw = c.ArrivalYaw;
        }

        /// <summary>
        /// The map signature [net §2.4]: the low 32 bits of the game's CityHash over the sorted map sector names
        /// joined with '\n', XORed with the same hash of the game version string ("" when not known). Two
        /// installs with the same sectors agree; a full game with DLC maps against the demo does not, and the
        /// reader says "Your Euro Truck Simulator 2 installation differs from the master's". Never 0 (0 = not known).
        /// </summary>
        public static int MapSignature(IEnumerable<string> sectorNames, string gameVersion)
        {
            var names = new List<string>(sectorNames ?? Array.Empty<string>());
            names.Sort(StringComparer.Ordinal);
            ulong sectors = EtsCityHash.Hash(string.Join("\n", names.ToArray()));
            ulong version = EtsCityHash.Hash(gameVersion ?? "");
            int signature = (int)((sectors ^ version) & 0xFFFFFFFFUL);
            return signature == 0 ? 1 : signature;
        }

        private static void PutToken(int[] into, int at, ulong token)
        {
            into[at] = (int)(token & 0xFFFFFFFFUL);
            into[at + 1] = (int)(token >> 32);
        }

        private static ulong GetToken(int[] from, int at)
        {
            return (uint)from[at] | ((ulong)(uint)from[at + 1] << 32);
        }
    }

    /// <summary>
    /// The session's phase machine, pure (owner A; [net §2.2], PLAN.md §5.2, §5.5). EtsSession feeds it what the
    /// room says each frame (Inputs) and carries out what it asks: ask for the session object's authority, write
    /// the ints it holds (WriteInto), raise the events. Nothing here touches Fusion or the scene, so the four
    /// journeys of [net §2.2] - the master choosing, a late joiner, the master leaving before and after the
    /// choice - run offline against a simulated room (probe 1824).
    ///
    /// The rules, per client (Connecting → Offline | WaitingForMaster | ChoosingHq → Loading → InWorld):
    ///   - Connecting until the room and the session object are up. No room after OfflineAfter (20 s; the
    ///     client log has the room ready ~6.5 s after scene load, Player.log:879-947) or no client at all
    ///     (the editor): Offline, its own master, nothing written.
    ///   - A Chosen session: Loading, master or not, with no HQ map.
    ///   - The master with the session unwritten: it waits SnapshotSettle (1.5 s) after the object became
    ///     valid, so a join snapshot still on its way is not mistaken for an empty session [net risk 8], then
    ///     writes Choosing and shows the map (ChoosingHq). A session already Choosing (a master that left before
    ///     choosing) is taken over at once. While another player's slot shows them loading or in the world
    ///     (OthersHaveSession, from Phase 3's slots) it never takes the session for unwritten at all: probe 1824
    ///     measured a snapshot 2 s late being overwritten without that.
    ///   - Anyone else: WaitingForMaster. A non-master never decides the session is unwritten; it waits. Nor
    ///     does anyone show a waiting screen before the snapshot could have arrived: an unread session keeps
    ///     the client Connecting for SnapshotSettle, so a late joiner goes straight to Loading.
    ///   - The master leaving before choosing: the new master (Fusion elects one; SceneNetworking hands it the
    ///     unowned objects within ~1 s, [net F5]) goes from WaitingForMaster to ChoosingHq. A Chosen that reaches
    ///     it while it chooses (the old master chose just before leaving) wins over its own Choosing, written or
    ///     not (checker probe 1832 R).
    ///   - Nothing is written before the session has been read since the room came (back) up: the snapshot, or
    ///     SnapshotSettle after the object became valid (probe 1832 B2: a room blink while choosing).
    ///   - IsMaster/MasterId are what the world shows (PLAN §7.A, C18): false/-1 while Connecting, true/-1 while
    ///     Offline; the rules themselves go by the room's answer.
    ///   - The master leaving after choosing: nothing reloads; the new master inherits the session ints and
    ///     writes nothing (only the clock, and only when ClockScale is set).
    ///   - Offline and then the room comes up: before a choice, the online rules take over; after one, a master
    ///     facing an empty session writes its offline choice as the session, and a client facing another choice
    ///     stays on its own (Relocating is Phase 5, Q-A1 "no") and says so.
    ///   - The room going away before a choice (Phase 2; Phase 1's checker found WaitingForMaster waited for ever):
    ///     a client in WaitingForMaster or ChoosingHq whose room has been down for OfflineAfter (20 s, the same
    ///     budget as Connecting) plays Offline - its own master with an interactive map - and the online rules
    ///     take over again if the room comes back before it chose. A master that lost its room could not choose
    ///     at all before this (Choose needs the room's master answer, which a missing room does not give).
    ///   - Loading ends when EtsSession's loader says so (Inputs.LoadDone: the map built at the origin, ground
    ///     under the arrival spot, the player teleported; EtsLoadTracker below, also pure).
    ///   - A master already loading or in the world on an online choice while the session reads unchosen (the room
    ///     emptied and came back fresh, or a late join snapshot's Choosing whose writer left) writes its choice as the
    ///     session again (RepairUnchosen), or nobody would ever choose for the waiting players (checker probe 2010).
    /// </summary>
    public sealed class EtsSessionLogic
    {
        /// <summary>What the room says this frame (EtsSession reads it from SceneNetworking and the session object).</summary>
        public struct Inputs
        {
            public double Now;          // local seconds
            public bool NoClient;       // no Somnium client at all (the editor): Offline at once
            public bool RoomReady;      // SceneNetworking.IsNetworkReady with a runner
            public bool SessionFound;   // the scene has the session object
            public bool SessionValid;   // its NetworkObject IsValid
            public bool HasAuthority;   // this client holds its state authority
            public bool IsMaster;       // runner.IsSharedModeMasterClient
            public int LocalId;         // this client's PlayerId (-1 unknown)
            public int MasterId;        // the master's PlayerId as the player slots say it (EtsNet.SlotMasterId: another player's "I am the room master" bit, Phase 3); -1 when none does, and then it is named from the session's writer (the server's Fusion has no GetMasterClient)
            public int[] Live;          // the session's ints as this client sees them (null when not valid)
            public bool LoadDone;       // EtsSession's loader: the yard is built and the arrival done

            /// <summary>
            /// Another present player's slot says it is Loading or in the world under a session epoch of 1 or more
            /// (EtsNet reads the slots, Phase 3; false until then). The session is chosen, whatever this client
            /// reads: a master then never takes it for unwritten, however late its join snapshot is (probe 1824
            /// case 5: a snapshot 2 s late was overwritten with Choosing without this).
            /// </summary>
            public bool OthersHaveSession;
        }

        /// <summary>Seconds without a room before the client plays Offline [net §2.2]; independent of the tablet's FallbackAfter (PLAN C19).</summary>
        public float OfflineAfter = 20f;

        /// <summary>How long after the session object became valid an unwritten session is believed [net §2.2].</summary>
        public float SnapshotSettle = 1.5f;

        /// <summary>
        /// Game minutes per real minute for the master's clock. 0 (the default) keeps the clock at the session's
        /// value: how fast ETS2 1.61 runs its clock is not in its files (probes 1821-1822: no time scale key in
        /// 1,106 /def files, no "time_compression" line in 70,700 /def, /ui and /system files, although the exe
        /// holds that name), so it is not guessed. Readers never extrapolate: they show the master's minute.
        /// </summary>
        public float ClockScale;

        /// <summary>What the master writes into ints 18 and 16 (EtsNet.MaxTrucks, the options).</summary>
        public int MaxTrucksToWrite = 16;
        public int OptionsToWrite = EtsSessionWire.OptionTraffic | EtsSessionWire.OptionStanding;

        /// <summary>This install's map signature (EtsSessionWire.MapSignature; 0 = not known yet).</summary>
        public int LocalMapSignature;

        public EtsPhase Phase { get; private set; } = EtsPhase.Connecting;

        /// <summary>
        /// What the world is told (PLAN §7.A, C18): false while Connecting - nobody is master before the session
        /// is read - and true while Offline, room or no room (the player picks their own headquarters; probe
        /// 1832 M2: an Offline client whose room was up without a session object read false before). Otherwise
        /// the room's own answer. The rules below decide by the room's answer (_roomMaster), not by this.
        /// </summary>
        public bool IsMaster { get; private set; }

        /// <summary>The master's PlayerId; -1 while Connecting, Offline or unknown (PLAN §7.A).</summary>
        public int MasterId { get; private set; } = -1;
        public bool HasChoice { get; private set; }
        public EtsHqChoice Choice { get; private set; }
        public int Epoch { get; private set; }
        public int Options { get; private set; } = EtsSessionWire.OptionTraffic | EtsSessionWire.OptionStanding;
        public int MaxTrucks { get; private set; }
        public ulong PreviewCity { get; private set; }
        public int ClockMinutes { get; private set; } = EtsSessionWire.StartClockMinutes;

        /// <summary>The headquarters were picked while Offline and the room has not confirmed them.</summary>
        public bool ChoseOffline { get; private set; }

        /// <summary>The master's install has other map sectors than this one (both signatures known).</summary>
        public bool MapMismatch { get; private set; }

        /// <summary>The session as last read (valid when SeenValid).</summary>
        public EtsSessionWire.Session Seen => _seen;
        public bool SeenValid { get; private set; }

        /// <summary>Why the last phase change or notice happened, for the log.</summary>
        public string Reason { get; private set; } = "";

        /// <summary>Ask for the session object's state authority: there is something to write and it is held elsewhere.</summary>
        public bool WantAuthority { get; private set; }

        /// <summary>
        /// There are ints to write (WriteInto), to be written only while holding authority. Never before the
        /// session has been read since the room (re)appeared: after a blink a pending hover or Choosing waits for
        /// the snapshot, so it cannot land on a choice another master made meanwhile (probe 1832 B2).
        /// </summary>
        public bool HasWrite => _writing && _mayWrite;

        private EtsSessionWire.Session _seen;
        private EtsSessionWire.Session _want;
        private bool _writing;
        private bool _mayWrite;
        private bool _roomMaster;
        private readonly int[] _scratch = new int[EtsSessionWire.Ints];
        private ulong _myPreview;
        private double _startedAt = double.NaN;
        private double _validSince = double.NaN;
        private double _downSince = double.NaN;
        private double _now;
        private int _localId = -1;
        private bool _phaseChanged, _masterChanged, _noticeChanged;
        private bool _offlineMismatchSaid;
        private bool _othersHaveSession;
        private bool _snapshotWaitSaid;
        private bool _clockAuthor;
        private int _clockBase;
        private double _clockBaseAt;

        /// <summary>True once after each phase change (EtsSession raises PhaseChanged).</summary>
        public bool TakePhaseChanged()
        {
            bool changed = _phaseChanged;
            _phaseChanged = false;
            return changed;
        }

        /// <summary>True once after the master (or whether it is this client) changed (EtsSession raises MasterChanged).</summary>
        public bool TakeMasterChanged()
        {
            bool changed = _masterChanged;
            _masterChanged = false;
            return changed;
        }

        /// <summary>True once after a notice that is not a phase change (Reason says what).</summary>
        public bool TakeNotice()
        {
            bool changed = _noticeChanged;
            _noticeChanged = false;
            return changed;
        }

        /// <summary>The ints this client wants the session object to hold (all 64; write those that differ).</summary>
        public void WriteInto(int[] into)
        {
            EtsSessionWire.Pack(_want, into);
        }

        // ------------------------------------------------------------------ the frame

        public void Step(in Inputs input)
        {
            _now = input.Now;
            _localId = input.LocalId;
            _othersHaveSession = input.OthersHaveSession;

            if (double.IsNaN(_startedAt))
            {
                _startedAt = input.Now;
            }

            bool online = input.RoomReady && input.SessionFound && input.SessionValid && input.Live != null;

            if (online)
            {
                if (double.IsNaN(_validSince))
                {
                    _validSince = input.Now;
                }

                SeenValid = EtsSessionWire.Unpack(input.Live, out _seen);

                if (_writing && Landed(input.Live))
                {
                    _writing = false;
                }
            }
            else
            {
                _validSince = double.NaN;
                SeenValid = false;
            }

            // How long the room (or its session object) has been missing, for the Offline fallback of the phases
            // before a choice. Connecting keeps its own rule (OfflineAfter from the scene start).
            if (online)
            {
                _downSince = double.NaN;
            }
            else if (double.IsNaN(_downSince))
            {
                _downSince = input.Now;
            }

            bool downTooLong = !online && input.Now - _downSince >= OfflineAfter;
            bool settled = online && input.Now - _validSince >= SnapshotSettle;
            bool read = SeenValid || settled;
            bool chosen = SeenValid && _seen.Phase == EtsSessionWire.Phase.Chosen;
            Master(input);

            switch (Phase)
            {
                case EtsPhase.Connecting:
                    if (input.NoClient)
                    {
                        Go(EtsPhase.Offline, "no Somnium client (the editor): this client is its own master");
                    }
                    else if (online)
                    {
                        // Up: decide once the join snapshot is in or SnapshotSettle has passed, never Offline.
                        if (read)
                        {
                            Decide(chosen);
                        }
                    }
                    else if (input.Now - _startedAt >= OfflineAfter)
                    {
                        Go(EtsPhase.Offline, input.RoomReady
                            ? "the room is up but its session object is not (" + (input.SessionFound ? "not valid" : "not in the scene") + ") after " + OfflineAfter.ToString("0") + " s"
                            : "no room after " + OfflineAfter.ToString("0") + " s");
                    }

                    break;

                case EtsPhase.Offline:
                    // The room came up before a choice was made: the online rules take over.
                    if (online && read && !input.NoClient)
                    {
                        Decide(chosen);
                    }

                    break;

                case EtsPhase.WaitingForMaster:
                    // After a room blink the snapshot is awaited again before a new master writes Choosing.
                    if (online && read)
                    {
                        Decide(chosen);
                    }
                    else if (downTooLong)
                    {
                        // Waiting for a master of a room that has gone: nobody will ever choose for this client.
                        Go(EtsPhase.Offline, "the room went away while waiting for the master (" + OfflineAfter.ToString("0") + " s); this client is its own master");
                    }

                    break;

                case EtsPhase.ChoosingHq:
                    if (!online || !read)
                    {
                        // The room blinked: the map stays up, and nothing is decided or written until the session
                        // has been read again (another master may have chosen meanwhile; probe 1832 B2). Gone for
                        // OfflineAfter: the choice is this client's own (Choose accepts it only Offline then).
                        if (downTooLong)
                        {
                            _writing = false;
                            Go(EtsPhase.Offline, "the room went away while choosing (" + OfflineAfter.ToString("0") + " s); the choice will be this client's own");
                        }

                        break;
                    }

                    if (chosen)
                    {
                        // Another client's choice: this client's own Chosen moves it to Loading in Choose, so in
                        // ChoosingHq a Chosen on the wire is always someone else's. It wins, even over a Choosing
                        // of this client's still waiting for authority: a master that chooses and leaves at once
                        // hands the next master a Choosing view first and the Chosen ints ~100 ms later, and
                        // writing the Choosing then split the room (probe 1832 R: the others loaded the choice,
                        // later joiners waited for a master on the HQ map).
                        EnterLoading("the session was chosen by player " + _seen.Writer);
                    }
                    else if (!_roomMaster)
                    {
                        _writing = false;
                        Go(EtsPhase.WaitingForMaster, "no longer the master");
                    }
                    else
                    {
                        KeepChoosingWritten();
                    }

                    break;

                case EtsPhase.Loading:
                    if (input.LoadDone)
                    {
                        Go(EtsPhase.InWorld, "loaded");
                    }

                    break;
            }

            // A phase change above (to Offline, say) may change who is master in the same frame.
            Master(input);

            if (ChoseOffline && online && read)
            {
                ReconcileOffline(chosen);
            }
            else if (online && read && !chosen && HasChoice && _roomMaster && !_othersHaveSession && !_writing &&
                     (Phase == EtsPhase.Loading || Phase == EtsPhase.InWorld))
            {
                RepairUnchosen();
            }

            if (online && chosen && !ChoseOffline)
            {
                MapMismatch = _seen.MapSignature != 0 && LocalMapSignature != 0 && _seen.MapSignature != LocalMapSignature;
            }

            PreviewCity = Phase == EtsPhase.ChoosingHq ? _myPreview : SeenValid && _seen.Phase == EtsSessionWire.Phase.Choosing ? _seen.PreviewCity : 0UL;
            Clock(online, chosen);
            _mayWrite = online && read;
            WantAuthority = _writing && _mayWrite && !input.HasAuthority;
        }

        // ------------------------------------------------------------------ what the UI asks

        /// <summary>
        /// The master (or an Offline client) picks the headquarters (EtsSession.ChooseHeadquarters). Anyone else,
        /// or any other phase, is refused (false).
        /// </summary>
        public bool Choose(in EtsHqChoice choice)
        {
            if (Phase == EtsPhase.Offline)
            {
                Choice = choice;
                HasChoice = true;
                ChoseOffline = true;
                Epoch = 0;
                Go(EtsPhase.Loading, "chose city " + choice.City + " offline");
                return true;
            }

            if (Phase != EtsPhase.ChoosingHq || !_roomMaster)
            {
                return false;
            }

            _want = ChosenFrom(choice, (SeenValid ? _seen.Epoch : 0) + 1);
            _writing = true;
            Choice = choice;
            HasChoice = true;
            Epoch = _want.Epoch;
            Options = _want.Options;
            MaxTrucks = _want.MaxTrucks;
            Go(EtsPhase.Loading, "chose city " + choice.City);
            return true;
        }

        /// <summary>The master's hover while choosing, lit on the watchers' maps. Ignored otherwise (false).</summary>
        public bool Preview(ulong city)
        {
            if (Phase != EtsPhase.ChoosingHq || !_roomMaster)
            {
                return false;
            }

            if (_myPreview == city)
            {
                return true;
            }

            _myPreview = city;
            PreviewCity = city;
            _want = ChoosingState();
            _writing = true;
            return true;
        }

        // ------------------------------------------------------------------ the rules

        private void Master(in Inputs input)
        {
            int roomMasterId;

            // The room's own answer, which the rules decide by (a master in Connecting must still take the
            // unwritten session once the snapshot has settled).
            if (input.RoomReady)
            {
                // Who the master is, when it is not this client: the session's writer. The Somnium server
                // compiles worlds against a Fusion without NetworkRunner.GetMasterClient (the upload of
                // 2026-09-23 failed on it: CS1061), and the sibling worlds (Quake, Age of Mythology) only ever
                // ask IsSharedModeMasterClient. The choosing master writes Choosing and Chosen itself, so this names
                // it through the whole HQ choice with no extra write. Since Phase 3 input.MasterId (above) names the
                // master first, from each player's own "I am the master" slot bit (PLAN §12, A3), so after a hand-over
                // the name follows the new master; the writer is only the fallback before any slot says so. The
                // session is never rewritten just to rename its master (probes 1824/1832: nobody overwrites a
                // chosen session - that is what keeps a master who chooses and leaves from splitting the room).
                // A writer that is this client itself, while the room says it is not the master (it chose, then its
                // room blinked and another player became master meanwhile), is not named: "unknown" (-1) rather than
                // this player's own name on their "Master:" line (checker probe 2010, A8).
                _roomMaster = input.IsMaster;
                roomMasterId = input.MasterId >= 0 ? input.MasterId
                             : input.IsMaster ? input.LocalId
                             : SeenValid && _seen.Writer >= 0 && _seen.Writer != input.LocalId ? _seen.Writer : -1;
            }
            else
            {
                _roomMaster = Phase == EtsPhase.Offline || ChoseOffline;
                roomMasterId = -1;
            }

            // What the world is told (PLAN §7.A, C18): nobody while Connecting; an Offline client is its own
            // master, named by no PlayerId. Step calls this again after a phase change, so the event follows.
            bool master = Phase == EtsPhase.Offline || (Phase != EtsPhase.Connecting && _roomMaster);
            int masterId = Phase == EtsPhase.Offline || Phase == EtsPhase.Connecting ? -1 : roomMasterId;

            if (master != IsMaster || masterId != MasterId)
            {
                IsMaster = master;
                MasterId = masterId;
                _masterChanged = true;
            }
        }

        private void Decide(bool chosen)
        {
            if (chosen)
            {
                EnterLoading(Phase == EtsPhase.Connecting ? "the session is chosen" : "the master chose");
                return;
            }

            if (!SeenValid && _othersHaveSession)
            {
                // Others are already in the chosen world: this client's copy has simply not arrived yet. A master
                // must not write Choosing over it, and nobody should flash "waiting for the master" meanwhile.
                if (_roomMaster && !_snapshotWaitSaid)
                {
                    _snapshotWaitSaid = true;
                    Note("master, the session reads unwritten but other players are already in the world; waiting for the join snapshot");
                }

                return;
            }

            if (_roomMaster)
            {
                _myPreview = SeenValid ? _seen.PreviewCity : 0UL;
                _want = ChoosingState();
                _writing = true;
                Go(EtsPhase.ChoosingHq, SeenValid ? "master; the last master left before choosing" : "master; the session is unwritten");
                return;
            }

            if (Phase != EtsPhase.WaitingForMaster)
            {
                Go(EtsPhase.WaitingForMaster, SeenValid ? "the master is choosing" : "the session is not written yet");
            }
        }

        private void EnterLoading(string why)
        {
            _writing = false;
            Choice = EtsSessionWire.ToChoice(_seen);
            HasChoice = true;
            Epoch = _seen.Epoch;
            Options = _seen.Options;
            MaxTrucks = _seen.MaxTrucks;
            Go(EtsPhase.Loading, why);
        }

        /// <summary>While choosing, the wire says Choosing, written by this client, with its preview (rewritten if anything else lands).</summary>
        private void KeepChoosingWritten()
        {
            if (_writing)
            {
                return;
            }

            if (SeenValid && _seen.Phase == EtsSessionWire.Phase.Choosing && _seen.Writer == _localId && _seen.PreviewCity == _myPreview)
            {
                return;
            }

            _want = ChoosingState();
            _writing = true;
        }

        private void ReconcileOffline(bool chosen)
        {
            if (chosen)
            {
                if (_seen.City == Choice.City && _seen.OriginCm == EtsSessionWire.GameToWire(Choice.OriginGame))
                {
                    ChoseOffline = false;
                    Epoch = _seen.Epoch;
                    Note("the room came up with the same headquarters; joined it");
                }
                else if (!_offlineMismatchSaid)
                {
                    _offlineMismatchSaid = true;
                    Note("the room chose other headquarters (city " + _seen.City + ") than the ones picked offline (city " + Choice.City + "); this client stays on its own until it rejoins");
                }

                return;
            }

            if (_roomMaster && !_othersHaveSession)
            {
                // The room has no choice yet: the one made offline becomes the session.
                _want = ChosenFrom(Choice, (SeenValid ? _seen.Epoch : 0) + 1);
                _writing = true;
                Epoch = _want.Epoch;
                ChoseOffline = false;
                Note("the room came up after the offline choice; this client is its master and wrote that choice as the session");
            }
        }

        /// <summary>
        /// The room's master is loading or in the world on an online choice, but the session reads unchosen (unwritten
        /// or Choosing): the room emptied and came back fresh while this player's connection blinked, or a joiner whose
        /// join snapshot came after SnapshotSettle wrote Choosing over the choice [net risk 8] and then left before
        /// choosing. A master in Loading or InWorld never chooses again, so without this nobody would: every waiting
        /// player and every later joiner would stay on the HQ map for good, since the room is up and the Offline
        /// fallback never comes (checker probe 2010: A alone chooses, its room empties, B joins a fresh room and leaves
        /// before choosing, A is master again). So this client's choice becomes the session again, as an offline choice
        /// does (ReconcileOffline). Never over a Chosen session (Step asks only when the session is not chosen): a
        /// different choice on the wire is the room's, and this client stays on its own (Relocating is Phase 5, Q-A1).
        /// </summary>
        private void RepairUnchosen()
        {
            _want = ChosenFrom(Choice, (SeenValid ? _seen.Epoch : 0) + 1);
            _writing = true;
            Epoch = _want.Epoch;
            Note("master in the world while the session reads " + (SeenValid ? "Choosing" : "unwritten") + "; wrote this client's headquarters as the session again");
        }

        private EtsSessionWire.Session ChoosingState()
        {
            return new EtsSessionWire.Session
            {
                Seq = NextSeq(),
                Writer = _localId,
                Phase = EtsSessionWire.Phase.Choosing,
                Epoch = SeenValid ? _seen.Epoch : 0,
                PreviewCity = _myPreview,
                MapSignature = LocalMapSignature,
                MaxTrucks = MaxTrucksToWrite,
                Options = OptionsToWrite,
                ClockMinutes = ClockMinutes
            };
        }

        private EtsSessionWire.Session ChosenFrom(in EtsHqChoice choice, int epoch)
        {
            var s = new EtsSessionWire.Session
            {
                Seq = NextSeq(),
                Writer = _localId,
                Phase = EtsSessionWire.Phase.Chosen,
                Epoch = epoch,
                MapSignature = LocalMapSignature,
                MaxTrucks = MaxTrucksToWrite,
                Options = OptionsToWrite,
                ClockMinutes = ClockMinutes
            };

            EtsSessionWire.FromChoice(choice, ref s);
            return s;
        }

        private int NextSeq()
        {
            int seen = SeenValid ? _seen.Seq : 0;
            return Math.Max(seen, _writing ? _want.Seq : 0) + 1;
        }

        /// <summary>The wanted ints are on the wire (what this client reads back equals what it wrote).</summary>
        private bool Landed(int[] live)
        {
            EtsSessionWire.Pack(_want, _scratch);

            for (int i = 0; i < EtsSessionWire.Used; i++)
            {
                if (live[i] != _scratch[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The master's clock: it counts from the session's minute when it takes over and writes each new minute
        /// (one int; only when ClockScale is set). Everyone else shows the written minute. A new master starts
        /// from the last written minute, so a change of master can drop the part-minute in progress (probe 1824
        /// case 6: 631 where an unbroken clock would read 632).
        /// </summary>
        private void Clock(bool online, bool chosen)
        {
            bool author = ClockScale > 0f && (online ? _roomMaster && chosen && (Phase == EtsPhase.Loading || Phase == EtsPhase.InWorld) : Phase == EtsPhase.Loading || Phase == EtsPhase.InWorld);

            if (!author)
            {
                _clockAuthor = false;

                if (online && SeenValid && _seen.ClockMinutes > 0)
                {
                    ClockMinutes = _seen.ClockMinutes;
                }

                return;
            }

            if (!_clockAuthor)
            {
                _clockAuthor = true;
                _clockBase = online && SeenValid && _seen.ClockMinutes > 0 ? _seen.ClockMinutes : ClockMinutes;
                _clockBaseAt = _now;
            }

            int minutes = _clockBase + (int)Math.Floor((_now - _clockBaseAt) * ClockScale / 60.0);

            if (minutes == ClockMinutes)
            {
                return;
            }

            ClockMinutes = minutes;

            if (online && !_writing)
            {
                _want = _seen;
                _want.Seq = NextSeq();
                _want.Writer = _localId;
                _want.ClockMinutes = minutes;
                _writing = true;
            }
        }

        private void Go(EtsPhase phase, string why)
        {
            Reason = why;

            if (phase == Phase)
            {
                return;
            }

            Phase = phase;
            _phaseChanged = true;
        }

        private void Note(string why)
        {
            Reason = why;
            _noticeChanged = true;
        }
    }

    /// <summary>
    /// The Loading phase's loader, pure (owner A; [net §2.9], PLAN.md §5.2 row Loading, §5.5 steps 5-6, U17, U19):
    /// the map built at the chosen origin, then ground under this player's arrival spot, then the teleport onto it,
    /// then done - EtsSessionLogic.Inputs.LoadDone. EtsSession feeds it what the scene says each frame and carries
    /// out what it asks (ProbeGround: cast for the arrival spot; AskTeleport: the Bridge's DoTeleportToPoint), so
    /// the stages, their order and every timeout run offline (probes 2001 and the Phase 2 cases of 1824/1832).
    ///
    /// Why each wait exists:
    ///   - Map. EtsMapViewer.LoadAt throws the scene's old build away and builds round the new centre; its
    ///     AnyCompleted is false until then (EtsMapViewer.ForgetBuild). A map that cannot be opened (no install:
    ///     MapFailed) will never build, so Loading ends at once, without a teleport, rather than never.
    ///   - Ground. The first build covers _radius (1200 m, SCENE Ets Map Viewer) round the origin, and the arrival is
    ///     ~6 m from the origin's stand ([gameui §3.1], EtsHqCatalog: "the first stand plus 6 m to its right"),
    ///     so ground is normally there the moment the map is. GroundWait (30 s) covers a yard whose surface is not
    ///     built (D's catalogue flags two such cities, NodeRecovered): the teleport then goes to the raw arrival
    ///     point, logged as such.
    ///   - Teleport. The Bridge moves the player at the end of a frame and calls back (MotionFeature.InternalTeleport
    ///     awaits WaitForEndOfFrame, then DoTeleportToPoint; the XR variant calls back one more frame later:
    ///     XRMotionFeature.cs:127-148). Asked again every TeleportRetry (3 s) while no call-back came; ended after
    ///     TeleportAsks (4) asks or PlayerWait (30 s) with no Bridge player at all (U17), logged either way - the
    ///     void must not hold a player for ever. In the editor there is no Bridge player (NoClient): the teleport is
    ///     EtsSession's editor camera move, and the stage ends at once.
    /// </summary>
    public sealed class EtsLoadTracker
    {
        public enum Stage { Idle, Map, Ground, Teleport, Done }

        /// <summary>What the scene says this frame (EtsSession reads it from the map viewer, the physics and the Bridge).</summary>
        public struct Inputs
        {
            public double Now;
            public bool MapReady;     // the map viewer is centred on the chosen origin (within 1 m) and has built round it
            public bool MapFailed;    // the archives could not be opened: nothing will ever be built
            public bool GroundFound;  // the last ground probe found a free standing spot on the drive surface
            public bool HasPlayer;    // the Bridge's local player (its motion feature) exists
            public bool NoClient;     // no Somnium client at all (the editor): nobody to teleport
            public bool Teleported;   // the Bridge called back, or the body stands within reach of the spot
        }

        public float GroundWait = 30f;
        public float TeleportRetry = 3f;
        public int TeleportAsks = 4;
        public float PlayerWait = 30f;
        public float MapGiveUp = 180f;

        /// <summary>Seconds between ground probes (4 Hz: a few rays each, never per frame).</summary>
        public float ProbeEvery = 0.25f;

        public Stage Current { get; private set; } = Stage.Idle;

        /// <summary>Cast for the arrival spot this frame.</summary>
        public bool ProbeGround { get; private set; }

        /// <summary>Ask the Bridge to teleport this frame.</summary>
        public bool AskTeleport { get; private set; }

        /// <summary>No ground appeared within GroundWait: the teleport goes to the raw arrival point.</summary>
        public bool GroundTimedOut { get; private set; }

        /// <summary>The teleport was not confirmed (asks used up, or no Bridge player): the player may still be in the void.</summary>
        public bool Unconfirmed { get; private set; }

        /// <summary>Why the last stage change happened, for the log.</summary>
        public string Why { get; private set; } = "";

        public double StartedAt { get; private set; }
        public double MapAt { get; private set; }
        public double GroundAt { get; private set; }
        public double DoneAt { get; private set; }
        public int Asks { get; private set; }

        private double _nextProbe, _askedAt, _teleportSince;
        private bool _changed;

        public bool Done => Current == Stage.Done;

        /// <summary>True once after each stage change (EtsSession logs it).</summary>
        public bool TakeChanged()
        {
            bool changed = _changed;
            _changed = false;
            return changed;
        }

        public void Begin(double now)
        {
            StartedAt = now;
            MapAt = GroundAt = DoneAt = double.NaN;
            Asks = 0;
            _nextProbe = now;
            _askedAt = double.NegativeInfinity;
            GroundTimedOut = false;
            Unconfirmed = false;
            ProbeGround = false;
            AskTeleport = false;
            Go(Stage.Map, "building the map at the headquarters");
        }

        public void Reset()
        {
            Current = Stage.Idle;
            ProbeGround = false;
            AskTeleport = false;
        }

        /// <summary>0..1 over the whole Loading: the map build is 90 %, the ground and the teleport the rest.</summary>
        public float Progress(float mapProgress)
        {
            switch (Current)
            {
                case Stage.Map: return 0.9f * Math.Max(0f, Math.Min(1f, mapProgress));
                case Stage.Ground: return 0.92f;
                case Stage.Teleport: return 0.96f;
                case Stage.Done: return 1f;
                default: return 0f;
            }
        }

        public void Step(in Inputs input)
        {
            ProbeGround = false;
            AskTeleport = false;

            switch (Current)
            {
                case Stage.Map:
                    if (input.MapReady)
                    {
                        MapAt = input.Now;
                        _nextProbe = input.Now;
                        Go(Stage.Ground, "the map is built at the headquarters");
                        goto case Stage.Ground;
                    }

                    if (input.MapFailed)
                    {
                        Finish(input.Now, "the game's archives could not be opened, so there is no map to arrive on; the player is not moved");
                        Unconfirmed = true;
                    }
                    else if (input.Now - StartedAt >= MapGiveUp)
                    {
                        Finish(input.Now, "the map did not finish building in " + MapGiveUp.ToString("0") + " s; the player is not moved");
                        Unconfirmed = true;
                    }

                    break;

                case Stage.Ground:
                    if (input.GroundFound)
                    {
                        GroundAt = input.Now;
                        Go(Stage.Teleport, "ground under the arrival spot");
                        EnterTeleport(input);
                        break;
                    }

                    if (input.Now - MapAt >= GroundWait)
                    {
                        GroundAt = input.Now;
                        GroundTimedOut = true;
                        Go(Stage.Teleport, "no ground under the arrival spot after " + GroundWait.ToString("0") + " s; teleporting to the raw arrival point");
                        EnterTeleport(input);
                        break;
                    }

                    if (input.Now >= _nextProbe)
                    {
                        _nextProbe = input.Now + ProbeEvery;
                        ProbeGround = true;
                    }

                    break;

                case Stage.Teleport:
                    TeleportStep(input);
                    break;
            }
        }

        private void EnterTeleport(in Inputs input)
        {
            _teleportSince = input.Now;
            TeleportStep(input);
        }

        private void TeleportStep(in Inputs input)
        {
            if (input.Teleported)
            {
                Finish(input.Now, "the player arrived");
                return;
            }

            if (input.NoClient)
            {
                // The editor: EtsSession moves its camera instead, in this same frame.
                AskTeleport = true;
                Asks++;
                Finish(input.Now, "no Somnium client (the editor): the camera was moved to the arrival spot");
                return;
            }

            if (!input.HasPlayer)
            {
                if (input.Now - _teleportSince >= PlayerWait)
                {
                    Unconfirmed = true;
                    Finish(input.Now, "no Bridge player to move after " + PlayerWait.ToString("0") + " s (U17)");
                }

                return;
            }

            if (Asks >= TeleportAsks)
            {
                if (input.Now - _askedAt >= TeleportRetry)
                {
                    Unconfirmed = true;
                    Finish(input.Now, "the Bridge did not confirm " + TeleportAsks + " teleports");
                }

                return;
            }

            if (Asks == 0 || input.Now - _askedAt >= TeleportRetry)
            {
                Asks++;
                _askedAt = input.Now;
                AskTeleport = true;
            }
        }

        private void Finish(double now, string why)
        {
            DoneAt = now;
            Go(Stage.Done, why);
        }

        private void Go(Stage stage, string why)
        {
            Why = why;

            if (Current != stage)
            {
                Current = stage;
                _changed = true;
            }
        }
    }

    /// <summary>
    /// Where players stand and trucks are put at the headquarters yard, pure (owner A; [net §2.6, §2.9], PLAN.md §3.6,
    /// C23, U18). Unity positions are relative to the map viewer's centre, which the session sets to the chosen origin
    /// (EtsMapBuilder.ToWorld: x and y minus the centre, z negated).
    /// </summary>
    public static class EtsArrival
    {
        /// <summary>The ring players arrive on round the session's arrival point: 8 places 45 degrees apart ([net §2.9]).</summary>
        public const int RingPlaces = 8;
        public const float RingRadius = 2.5f;

        /// <summary>A second ring for a first ring blocked by walls or trucks.</summary>
        public const float OuterRingRadius = 5f;

        /// <summary>
        /// The sideways step between trucks put beside a yard's own stands ([net §2.6] "a 4 m lateral step"): a
        /// preview truck is 2.5-2.6 m wide (the game's own bounding boxes of the 18 previews), so 4 m leaves 1.4 m
        /// between mirrors. Needed at nearly every yard: D's U18 measurement found 1-8 stands within 150 m, median 1.
        /// </summary>
        public const float LateralStep = 4f;

        /// <summary>Lateral places tried on each side of the first stand.</summary>
        public const int LateralPerSide = 6;

        /// <summary>A game position in Unity, relative to the map centre (EtsMapBuilder.ToWorld for a Vector3).</summary>
        public static Vector3 GameToUnity(Vector3 game, Vector3 centerGame)
        {
            return new Vector3(game.x - centerGame.x, game.y - centerGame.y, -(game.z - centerGame.z));
        }

        /// <summary>Unity's right of a Unity yaw (degrees): (cos, 0, -sin).</summary>
        public static Vector3 Right(float yawDegrees)
        {
            double r = yawDegrees * Math.PI / 180.0;
            return new Vector3((float)Math.Cos(r), 0f, (float)-Math.Sin(r));
        }

        /// <summary>Unity's forward of a Unity yaw (degrees): (sin, 0, cos).</summary>
        public static Vector3 Forward(float yawDegrees)
        {
            double r = yawDegrees * Math.PI / 180.0;
            return new Vector3((float)Math.Sin(r), 0f, (float)Math.Cos(r));
        }

        /// <summary>
        /// This player's arrival candidates, best first: its own place on the ring (index % 8, measured from the
        /// arrival yaw), the other seven in turn, the same eight on the outer ring, then the arrival point itself.
        /// Two players with different indexes never share a first choice. Fills `into` (cleared first; no allocation).
        /// </summary>
        public static void RingCandidates(Vector3 arrival, float arrivalYaw, int index, List<Vector3> into)
        {
            into.Clear();
            int k0 = ((index % RingPlaces) + RingPlaces) % RingPlaces;

            for (int ring = 0; ring < 2; ring++)
            {
                float radius = ring == 0 ? RingRadius : OuterRingRadius;

                for (int n = 0; n < RingPlaces; n++)
                {
                    float angle = arrivalYaw + ((k0 + n) % RingPlaces) * (360f / RingPlaces);
                    into.Add(arrival + Forward(angle) * radius);
                }
            }

            into.Add(arrival);
        }

        /// <summary>One place a truck may be put: a yard stand (Authored, from the game's spawn points) or a lateral step beside the first.</summary>
        public struct Stand
        {
            public Vector3 Position;  // Unity
            public float Yaw;         // Unity degrees
            public int Type;          // 14 garage, 10 parking, 1 trailer bay (already moved 13.68 m ahead); 0 for a lateral step
            public bool Authored;
            public int Step;          // lateral: +1 right, -1 left, +2, -2 ... ; 0 for authored stands
            public ulong Prefab;      // the map prefab the stand belongs to (the tie-break of OrderYardStands)
        }

        /// <summary>
        /// A yard's stands in the HQ catalogue's order (D's EtsHqCatalogBuilder, PLAN §3.6): garage, then parking,
        /// then trailer bays (never 13), each by horizontal distance from the yard's anchor, ties by prefab uid; a
        /// stand closer than MinimumSpacing (3 m) to one already kept dropped, since two trucks there would stand in
        /// each other. For a client without the catalogue (a joiner: only the master builds it, PLAN §3.6), which
        /// reads the same stands from the map round the anchor with D's SpotsOf - so both lists agree. Fills `into`.
        /// </summary>
        public static void OrderYardStands(List<Stand> stands, Vector3 anchor, List<Stand> into)
        {
            into.Clear();
            stands.RemoveAll(s => !StandType(s.Type));
            stands.Sort((a, b) =>
            {
                int ra = TypeRank(a.Type), rb = TypeRank(b.Type);
                if (ra != rb) return ra.CompareTo(rb);
                int byDistance = Flat(a.Position, anchor).CompareTo(Flat(b.Position, anchor));
                return byDistance != 0 ? byDistance : a.Prefab.CompareTo(b.Prefab);
            });

            foreach (Stand s in stands)
            {
                bool clash = false;

                foreach (Stand kept in into)
                {
                    float dx = s.Position.x - kept.Position.x, dz = s.Position.z - kept.Position.z;

                    if (dx * dx + dz * dz < MinimumSpacing * MinimumSpacing)
                    {
                        clash = true;
                        break;
                    }
                }

                if (!clash)
                {
                    into.Add(s);
                }
            }
        }

        /// <summary>Two stands closer than this are one: D's own constant, so the two lists cannot drift apart.</summary>
        public const float MinimumSpacing = EtsHqCatalogBuilder.MinimumSpacing;

        /// <summary>
        /// The yard's stands from D's HQ catalogue row (the master's, and an Offline player's: they built it to choose
        /// from), in Unity coordinates round the map centre; already in the catalogue's order. Fills `into`.
        /// </summary>
        public static void StandsFromCatalogue(EtsHqCity city, Vector3 centerGame, List<Stand> into)
        {
            into.Clear();

            if (city == null || city.Spots == null)
            {
                return;
            }

            foreach (EtsHqSpot spot in city.Spots)
            {
                into.Add(new Stand
                {
                    Position = GameToUnity(new Vector3(spot.GameX, spot.GameY, spot.GameZ), centerGame),
                    Yaw = spot.Yaw,
                    Type = spot.Type,
                    Authored = true,
                    Prefab = spot.PrefabUid
                });
            }
        }

        /// <summary>
        /// The same stands for a client without the catalogue (a joiner; PLAN §3.6 "joiners never need the
        /// catalogue"), read from the map round the yard's anchor (the session's origin): every prefab whose box
        /// centre lies within YardRadius + 200 m, D's own EtsHqCatalogBuilder.SpotsOf for its stands (the frame the
        /// drawn yard stands in, trailer bays moved to the tractor's place), those within YardRadius (150 m) kept, in
        /// the catalogue's order (OrderYardStands). The sectors are the map viewer's, already read round the same
        /// centre, so this reads no file; it allocates, once per spawn. Probe 2003 compares it with D's catalogue for
        /// every headquarters city.
        /// </summary>
        public static void StandsFromMap(EtsMap map, Vector3 anchorGame, Vector3 centerGame, List<Stand> into)
        {
            into.Clear();

            if (map == null)
            {
                return;
            }

            float radius = EtsHqCatalogBuilder.YardRadius;
            float reach = radius + 200f;
            var found = new List<Stand>();

            foreach (EtsMapSector sector in map.SectorsAround(anchorGame.x, anchorGame.z, reach))
            {
                foreach (EtsMapPrefab prefab in sector.Prefabs)
                {
                    float px = (prefab.KdopMin[0] + prefab.KdopMax[0]) / 2f - anchorGame.x, pz = (prefab.KdopMin[2] + prefab.KdopMax[2]) / 2f - anchorGame.z;

                    if (px * px + pz * pz > reach * reach)
                    {
                        continue;
                    }

                    foreach (EtsHqSpot spot in EtsHqCatalogBuilder.SpotsOf(map, prefab, true))
                    {
                        float dx = spot.GameX - anchorGame.x, dz = spot.GameZ - anchorGame.z;

                        if (dx * dx + dz * dz > radius * radius)
                        {
                            continue;
                        }

                        found.Add(new Stand
                        {
                            Position = GameToUnity(new Vector3(spot.GameX, spot.GameY, spot.GameZ), centerGame),
                            Yaw = spot.Yaw,
                            Type = spot.Type,
                            Authored = true,
                            Prefab = spot.PrefabUid
                        });
                    }
                }
            }

            OrderYardStands(found, GameToUnity(anchorGame, centerGame), into);
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Whether a spawn point type may take a truck: garage, parking, trailer bay; never 13, the company's map marker (PLAN C23).</summary>
        public static bool StandType(int type)
        {
            return type == EtsPrefabDescriptor.SpawnGarage || type == EtsPrefabDescriptor.SpawnParking || type == EtsPrefabDescriptor.SpawnTrailer;
        }

        /// <summary>Garage first, then parking, then trailer bays (PLAN §3.6, C23 as amended: the yard's spawn order).</summary>
        public static int TypeRank(int type)
        {
            return type == EtsPrefabDescriptor.SpawnGarage ? 0 : type == EtsPrefabDescriptor.SpawnParking ? 1 : type == EtsPrefabDescriptor.SpawnTrailer ? 2 : 3;
        }

        /// <summary>
        /// The order a truck is tried in: the authored stands (any other type dropped), starting at `start` and
        /// wrapping, then LateralPerSide steps on each side of the first stand, right first ([net §2.6]). With no
        /// authored stand the steps go beside `fallback` (the arrival point) instead. `start` is the player's slot in
        /// Phase 3, so two players spawning at once aim at different stands; 0 until slots exist. Fills `into`.
        /// </summary>
        public static void SpawnOrder(IReadOnlyList<Stand> authored, int start, Stand fallback, List<Stand> into)
        {
            into.Clear();
            int kept = 0;

            for (int i = 0; i < authored.Count; i++)
            {
                if (StandType(authored[i].Type)) kept++;
            }

            if (kept > 0)
            {
                // The authored stands in their given order, keeping the valid ones, rotated to start at `start`.
                int k0 = ((start % kept) + kept) % kept;

                for (int n = 0; n < kept; n++)
                {
                    int want = (k0 + n) % kept, seen = 0;

                    for (int i = 0; i < authored.Count; i++)
                    {
                        if (!StandType(authored[i].Type)) continue;

                        if (seen++ == want)
                        {
                            Stand s = authored[i];
                            s.Authored = true;
                            s.Step = 0;
                            into.Add(s);
                            break;
                        }
                    }
                }
            }

            // The first authored stand in the given order (not the rotated one): every client steps beside the same stand.
            Stand anchor = fallback;

            for (int i = 0; i < authored.Count; i++)
            {
                if (StandType(authored[i].Type)) { anchor = authored[i]; break; }
            }

            Vector3 right = Right(anchor.Yaw);

            for (int n = 1; n <= LateralPerSide; n++)
            {
                for (int side = 1; side >= -1; side -= 2)
                {
                    into.Add(new Stand
                    {
                        Position = anchor.Position + right * (LateralStep * n * side),
                        Yaw = anchor.Yaw,
                        Type = 0,
                        Authored = false,
                        Step = n * side
                    });
                }
            }
        }
    }
}
