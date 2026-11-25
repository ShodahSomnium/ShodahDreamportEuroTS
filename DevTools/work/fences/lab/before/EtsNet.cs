using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Fusion;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player;
using SomniumSpace.Network.Bridge;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The world over the network: the bootstrap, the session's tick, one player slot per player, every player's own
    /// truck drawn on every client, passengers and riders (owner A; PLAN.md §3.2, §3.3, §5.3-§5.5, §7.A, §8 Phase 3;
    /// [net §2.3-§2.12]).
    ///
    /// Every client builds the map and the trucks itself, from its own install, so nothing but state crosses the wire.
    /// The pattern is the AoM world's (AomNet): the Community Modules' Photon Fusion session every player in the instance
    /// is already in, shared mode, networked objects placed in the scene by hand, because none can be made at run time
    /// and no behaviour of the world's own can be networked (the world assembly is compiled on the server, unwoven).
    ///
    /// 'Ets Net' is the bootstrap (Awake adds each owner's per-player singleton in PLAN §3.2's order) and the one place
    /// the world orders its passes. Update (PLAN §5.4 step 2): the session (EtsSession.Tick), then every "Ets Net Player
    /// k" slot read (the claim, the housekeeping, a pending seat request, the own seat table, the remote riders), then
    /// every other player's truck drawn (EtsNetVehicle), then EtsRiders.EarlyPass, then the own slot written. LateUpdate:
    /// EtsRiders.LatePass.
    ///
    /// Slots ([net §2.5]; the pure rules are EtsSlotRules, EtsSlotClaim, EtsSeatTable and EtsSeatRequest, run by probe
    /// 2200's simulated 3-client room). Each player claims a free "Ets Net Player k" (a NetworkBridgeData64) by asking for
    /// its state authority, writes its own record there (EtsPlayerWire: status, the room-master bit, truck identity and
    /// serial, truck and trailer motion, job summary, seat table, rider record, money display) and nothing anywhere else.
    /// Leaving sends nothing: the slot's owner is no longer in the room, every client stops drawing it, and whoever holds
    /// its authority (SceneNetworking gives orphans to the master) writes it free.
    ///
    /// Alone, in the editor, before the room is joined, or with EtsFeatures.NewFlow off, none of the networking does
    /// anything. The old one-truck sync ("Ets Net Truck 0", EtsNetWire.Pack, Claim/TakeOver/Orphaned, the driver-avatar
    /// placement) is retired: that object is "Ets Net Session" since Phase 2, and riders place avatars now (EtsRiders).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class EtsNet : MonoBehaviour
    {
        /// <summary>The retired one-truck object's name: nothing reads it; the editor tool (EtsNetSceneSetup) refuses to make it.</summary>
        public const string StateObjectName = "Ets Net Truck 0";
        public const string EventsObjectName = "Ets Net Events";

        /// <summary>Most writes of the own slot a second [net §2.4]; Fusion itself sends at its own rate (16 Hz, probe 1674).</summary>
        private const float WriteEvery = 1f / 30f;

        private const byte NewsText = 1;

        public static EtsNet Instance { get; private set; }

        public EtsTruckController Truck;
        public EtsTruckSeat Seat;
        public EtsMenu Menu;
        public EtsJobs Jobs;
        public TMP_FontAsset Font;

        private NetworkBridgeEvents _events;
        private bool _subscribed;
        private bool _playerEvents;
        private float _nextFind;
        private string _notice = "";
        private float _noticeUntil;

        /// <summary>Something about the network worth showing (the menu shows it).</summary>
        public string Notice => Time.time < _noticeUntil ? _notice : "";

        /// <summary>Hooks the scene truck up (the old flow's EtsModelViewer.SetUpDriving; without an install it is never called).</summary>
        public static EtsNet Attach(GameObject host, EtsTruckController truck, EtsTruckSeat seat, EtsMenu menu, EtsJobs jobs, TMP_FontAsset font)
        {
            EtsNet net = Instance;

            // Unity's own null test, not ??: in the editor a missing component is a fake null that ?? keeps.
            if (net == null) net = host.GetComponent<EtsNet>();
            if (net == null) net = host.AddComponent<EtsNet>();
            net.Truck = truck;
            net.Seat = seat;
            net.Menu = menu;
            net.Jobs = jobs;
            net.Font = font;
            return net;
        }

        private void Awake()
        {
            Instance = this;

            for (int k = 0; k < MaxSlots; k++)
            {
                _slots[k] = new int[EtsPlayerWire.Ints];
                _riderTracks[k] = new EtsRiderTrack();
            }

            // The integrated plan's bootstrap (PLAN.md §3.2), in its order: the session first, then
            // the traffic (below), then each owner's per-player singleton.
            if (EtsFeatures.NewFlow)
            {
                EtsSession.Ensure(gameObject);
            }

            // The AI traffic lives beside the networking, on 'Ets Net', and finds its own scene objects ("Ets Net
            // Traffic 0..3") by name. Unity's null test, not ??: a missing component is a fake null in the editor.
            if (GetComponent<EtsTraffic>() == null)
            {
                gameObject.AddComponent<EtsTraffic>();
            }

            if (EtsFeatures.NewFlow)
            {
                EtsProfileStore.Ensure(gameObject);
                EtsMapIndex.Ensure(gameObject);
                EtsJobs jobs = EtsJobs.Ensure(gameObject);
                EtsRiders.Ensure(gameObject);
                EtsTruckSeat seat = EtsTruckSeat.Ensure(gameObject);
                EtsTruckCamera.Ensure(gameObject);
                EtsSteeringGrab.Ensure(gameObject);
                EtsTruckAudio.Ensure(gameObject);
                EtsMenu menu = EtsMenu.Ensure(gameObject);
                EtsTabletApp.Ensure(gameObject);
                EtsRouteAdvisor.Ensure(gameObject);
                EtsFlow.Ensure(gameObject);

                // The per-player kit the rest of this class and the traffic read: EtsTraffic.LocalFocus streams its
                // cars round Seat.StreamFocus, and the news lines go to the HUD through Jobs.Tell (Say). Truck follows
                // the own truck (FollowOwnTruck).
                Seat = seat;
                Menu = menu;
                Jobs = jobs;

                // A player joining or leaving re-reads the room's player list at once rather than at the next 4 Hz
                // refresh ([net §2.5] step 4: "SceneNetworking.OnOtherPlayerLeft lets them do it at once").
                SceneNetworking.OnOtherPlayerJoined += OnRoomPlayersChanged;
                SceneNetworking.OnOtherPlayerLeft += OnRoomPlayersChanged;
                _playerEvents = true;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            if (_playerEvents)
            {
                SceneNetworking.OnOtherPlayerJoined -= OnRoomPlayersChanged;
                SceneNetworking.OnOtherPlayerLeft -= OnRoomPlayersChanged;
                _playerEvents = false;
            }

            // Statics outlive a world visit (the assembly is loaded once per client session), so the local truck registry
            // is emptied with the bootstrap object (PLAN.md §3.2). Only the new flow fills it.
            if (EtsFeatures.NewFlow)
            {
                _spawning = null;
                _spawnActive = false;
                _spawnSerial++;
                EtsLocalTruck.Reset();

                for (int k = 0; k < MaxSlots; k++)
                {
                    if (_puppets[k] != null)
                    {
                        Destroy(_puppets[k].gameObject);
                        _puppets[k] = null;
                    }
                }
            }

            if (_subscribed && _events != null)
            {
                _events.OnMessageToAll -= OnNews;
            }
        }

        // ---------------------------------------------------------------- who and what

        private static NetworkRunner Runner => SceneNetworking.NetworkRunnerRef;

        private static bool Valid(NetworkBehaviour behaviour)
        {
            return behaviour != null && behaviour.Object != null && behaviour.Object.IsValid;
        }

        /// <summary>In a room (PLAN §7.A): the new flow with the room up (SceneNetworking.IsNetworkReady and a runner).</summary>
        public bool Online => EtsFeatures.NewFlow && SceneNetworking.IsNetworkReady && Runner != null;

        public int LocalId => Runner != null ? Runner.LocalPlayer.PlayerId : -1;

        /// <summary>
        /// A player's name from their Fusion id. The Bridge's PhotonId is the player's network object's creator id (the
        /// client's BridgePlayerProperties: Creator.PlayerId), which in shared mode is the player themselves. A slot's owner
        /// is answered from the cache (RefreshPlayers, at most once a second) without walking the Bridge's list.
        /// </summary>
        public static string NameOf(int playerId)
        {
            EtsNet net = Instance;

            if (net != null && playerId >= 0)
            {
                for (int k = 0; k < net._slotCount; k++)
                {
                    if (net._slotPlayerIds[k] == playerId && !string.IsNullOrEmpty(net._slotNames[k]))
                    {
                        return net._slotNames[k];
                    }
                }
            }

            try
            {
                List<ISomniumPlayer> all = SomniumBridge.PlayersContainer?.AllPlayers;

                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        ISomniumPlayer player = all[i];

                        if (player?.Properties != null && player.Properties.PhotonId == playerId && !string.IsNullOrEmpty(player.Properties.NickName))
                        {
                            return player.Properties.NickName;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // No client (the editor).
            }

            return playerId < 0 ? "" : "Player " + playerId;
        }

        /// <summary>
        /// Kept for callers of the one-truck days (EtsJobs.Take in the old flow, EtsTruckDoor on a scene truck): every truck
        /// is its owner's now (PLAN C10), so there is nothing to claim. Granted at once, as it always was alone.
        /// </summary>
        public void Claim(Action granted, Action<string> refused)
        {
            granted?.Invoke();
        }

        // ---------------------------------------------------------------- the frame (PLAN §5.4)

        private void Update()
        {
            // The old flow (EtsFeatures.NewFlow off): the scene truck plays on each client alone. Its one-truck sync is
            // retired - its object is "Ets Net Session" since Phase 2 - so there is nothing to do here.
            if (!EtsFeatures.NewFlow)
            {
                return;
            }

            // 2.1 The session first: phases, the session writes, Loading and the arrival.
            EtsSession session = EtsSession.Instance;

            if (session != null)
            {
                session.Tick();
            }

            FindEvents();
            FollowOwnTruck();
            double now = Time.unscaledTimeAsDouble;

            // 2.2 Read the slots; 2.3 draw the puppets; 2.4 the riders; 2.5 write the own slot.
            ReadSlots(session, now);
            DrawPuppets(now);
            UpdatePlates();

            EtsRiders riders = EtsRiders.Instance;

            if (riders != null)
            {
                // Guarded: a throw in a pass of another owner's must not stop this client writing its own slot every frame
                // after (the others would see its truck freeze).
                try
                {
                    riders.EarlyPass();
                }
                catch (Exception e)
                {
                    RidersThrew("EarlyPass", e);
                }
            }

            WriteOwnSlot(session);
            Report(now);
        }

        private void LateUpdate()
        {
            if (!EtsFeatures.NewFlow)
            {
                return;
            }

            // §5.4 step 6: remote riders re-placed after Fusion's Render (B), before the seat, the camera and VRIK.
            EtsRiders riders = EtsRiders.Instance;

            if (riders != null)
            {
                try
                {
                    riders.LatePass();
                }
                catch (Exception e)
                {
                    RidersThrew("LatePass", e);
                }
            }

        }

        private float _ridersThrewAt = -100f;

        private void RidersThrew(string pass, Exception e)
        {
            if (Time.unscaledTime - _ridersThrewAt < 10f)
            {
                return;
            }

            _ridersThrewAt = Time.unscaledTime;
            Debug.LogWarning("[SomniumEuroTruck] Network: EtsRiders." + pass + " threw " + e.GetType().Name + ": " + e.Message + " (logged at most every 10 s)\n" + e.StackTrace);
        }

        // ---------------------------------------------------------------- news

        /// <summary>A news line to everyone in the room, and to this client's log (public: PLAN.md §7.A).</summary>
        public void Announce(string text)
        {
            Debug.Log("[SomniumEuroTruck] Network: " + text);

            if (!Valid(_events) || string.IsNullOrEmpty(text))
            {
                return;
            }

            // To all, with the sender's id: the RPC comes back to the sender too (Q3Link.OnMessage
            // says the same of Quake's), and the sender already knows.
            byte[] body = Encoding.UTF8.GetBytes(text);
            int length = Math.Min(body.Length, 400);
            var message = new byte[5 + length];
            int from = LocalId;
            message[0] = NewsText;
            message[1] = (byte)from;
            message[2] = (byte)(from >> 8);
            message[3] = (byte)(from >> 16);
            message[4] = (byte)(from >> 24);
            Array.Copy(body, 0, message, 5, length);
            _events.RPC_SendMessageToAll(message);
        }

        private void OnNews(byte[] message)
        {
            if (message == null || message.Length < 6 || message[0] != NewsText)
            {
                return;
            }

            int from = message[1] | (message[2] << 8) | (message[3] << 16) | (message[4] << 24);

            if (from == LocalId)
            {
                return;
            }

            Say(Encoding.UTF8.GetString(message, 5, message.Length - 5));
        }

        private void Say(string text)
        {
            _notice = text;
            _noticeUntil = Time.time + 8f;
            Debug.Log("[SomniumEuroTruck] Network: " + text);
            Jobs?.Tell(text);
        }

        /// <summary>Seconds of searching for "Ets Net Events" before giving up (a scene without it has no news).</summary>
        private const float EventsSearchFor = 30f;

        private float _eventsSearchUntil = -1f;

        /// <summary>"Ets Net Events" for the news lines (Announce, OnNews), once a second for up to EventsSearchFor.</summary>
        private void FindEvents()
        {
            if (_events != null || Time.time < _nextFind)
            {
                return;
            }

            if (_eventsSearchUntil < 0f)
            {
                _eventsSearchUntil = Time.time + EventsSearchFor;
            }
            else if (Time.time > _eventsSearchUntil)
            {
                return;
            }

            _nextFind = Time.time + 1f;

            foreach (NetworkBridgeEvents events in FindObjectsByType<NetworkBridgeEvents>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (events.name == EventsObjectName && !(events is NetworkBridgeData))
                {
                    _events = events;
                }
            }

            if (_events != null && !_subscribed)
            {
                _events.OnMessageToAll += OnNews;
                _subscribed = true;
            }
        }

        /// <summary>
        /// Truck is the own truck's controller (EtsTraffic.LocalFocus and older readers). Unity's null test: a despawned rig
        /// reads null. A new own truck (or none) empties the seat table: a passenger of the old one is not in the new one.
        /// </summary>
        private void FollowOwnTruck()
        {
            EtsTruckRig own = EtsLocalTruck.Own;
            EtsTruckController controller = own != null ? own.Controller : null;

            if (Truck != controller)
            {
                Truck = controller;
            }

            if (own != _ownSeen)
            {
                _ownSeen = own;
                _seats.Clear();
                _ownOwnerSlot = int.MinValue;
            }

            if (own != null && _ownOwnerSlot != MySlot)
            {
                // B's EtsRiders names a truck by its owner's slot (rig.OwnerSlot, so the own truck by this client's slot)
                // and the exact serial TryGetVehicleFrame answers with; the rig says whose it is too (-1 = the local
                // player's, EtsTruckRig.OwnerPlayerId).
                _ownOwnerSlot = MySlot;
                own.SetOwner(-1, MySlot);
            }
        }

        private EtsTruckRig _ownSeen;
        private int _ownOwnerSlot = int.MinValue;

        // ---------------------------------------------------------------- the slots ([net §2.3-§2.5])

        /// <summary>Player slots "Ets Net Player 0..49" [net §2.3]; the instance allows 50.</summary>
        public const int MaxSlots = 50;

        /// <summary>
        /// Trucks at once (Q-A4, settled: 16): set by client CPU and GPU, not bandwidth [net §2.10] (one DAF build is
        /// ~240 ms of main thread, sliced; a truck and trailer ~50 renderers). A soft cap: a simultaneous 17th is harmless.
        /// NonSerialized: 'Ets Net' is a scene object, and a value in the scene would override the code's default (memory
        /// "ets2-161-port", Trap 1).
        /// </summary>
        [NonSerialized]
        public int MaxTrucks = 16;

        /// <summary>Beyond this distance from the local stream focus another player's truck is not drawn; its timeline is still fed [net §2.6].</summary>
        [NonSerialized]
        public float TruckDrawRadius = 1500f;

        /// <summary>
        /// A puppet's interior is shown within this distance, or while the local player sits in it [net §2.6], PLAN C3. For
        /// readers only: the rule itself is the rig's (EtsTruckRig.InteriorRadius / InteriorRadiusOut, with hysteresis).
        /// </summary>
        public float InteriorRadius => EtsTruckRig.InteriorRadius;

        /// <summary>
        /// Q-A3 (settled): player trucks collide below this relative speed and pass through above it. C's constant: the rule
        /// runs in EtsTruckRig.UpdateVehicleContacts (EtsNetVehicle.Contacts calls it per puppet, every frame).
        /// </summary>
        public float TrucksPassThroughKmh => EtsTruckRig.ContactKmh;

        /// <summary>A slot that stops counting is hidden at once and its vehicle destroyed after this grace ([net §2.4]: 2 s).</summary>
        private const float VehicleGrace = 2f;

        /// <summary>
        /// The room's player list is re-read at once on a join or a leave (SceneNetworking's events), and once a second besides
        /// as a safety net: runner.ActivePlayers is an iterator and allocates on every walk.
        /// </summary>
        private const float PresentEvery = 1f;

        /// <summary>Slot housekeeping, the seat table and the player rows: 4 Hz ([net §2.7]: "every 0.25 s").</summary>
        private const float ServeEvery = 0.25f;

        private readonly NetworkBridgeData[] _slotObjects = new NetworkBridgeData[MaxSlots];
        private readonly int[][] _slots = new int[MaxSlots][];
        private readonly bool[] _valid = new bool[MaxSlots];
        private readonly bool[] _authority = new bool[MaxSlots];
        private readonly bool[] _listed = new bool[MaxSlots];
        private readonly bool[] _counts = new bool[MaxSlots];
        private readonly EtsRiderTrack[] _riderTracks = new EtsRiderTrack[MaxSlots];
        private readonly EtsNetVehicle[] _puppets = new EtsNetVehicle[MaxSlots];
        private readonly ISomniumPlayer[] _slotPlayers = new ISomniumPlayer[MaxSlots];
        private readonly int[] _slotPlayerIds = Filled(MaxSlots, -1);
        private readonly string[] _slotNames = new string[MaxSlots];
        private readonly EtsPlayerView[] _views = new EtsPlayerView[MaxSlots];
        private readonly List<int> _present = new List<int>(64);
        private readonly EtsSlotClaim _claim = new EtsSlotClaim();
        private readonly EtsSeatTable _seats = new EtsSeatTable();
        private readonly EtsSeatRequest _request = new EtsSeatRequest();
        private readonly List<EtsRemoteRider> _remoteRiders = new List<EtsRemoteRider>(MaxSlots);
        private readonly int[] _occupants = new int[EtsSeatTable.Seats];

        private int _slotCount;
        private bool _slotsComplete;
        private int _slotsLogged;
        private float _nextSlotFind;
        private float _presentAt;
        private float _nextServe;
        private int _lastSlot = -1;
        private int _roomMasterId = -1;
        private bool _othersHaveSession;
        private bool _roomMaster;
        private int _sessionEpoch;

        private static int[] Filled(int n, int value)
        {
            var a = new int[n];

            for (int i = 0; i < n; i++)
            {
                a[i] = value;
            }

            return a;
        }

        /// <summary>This client's player slot; -1 until claimed ([net §2.5]; the claim takes about a second once the session is read).</summary>
        public int MySlot => _claim.Holding ? _claim.Slot : -1;

        /// <summary>
        /// The room master as the slots say it (int 4 bit 19 of another present player's slot), -1 when no other slot says
        /// so (or this client is the master itself). EtsSession names the master from it before the session's writer (PLAN
        /// §12, the U3 ruling: the server's Fusion has no GetMasterClient).
        /// </summary>
        public int SlotMasterId => _roomMasterId;

        private int _actingMasterId = -1;
        private int _actingLogged = -1;

        /// <summary>This visitor has no game (D's EtsFlow.NoGame): published as int 4 bit 20, and never the acting master while someone with it is here.</summary>
        private static bool LocalNoGame => EtsFlow.Instance != null && EtsFlow.Instance.NoGame;

        /// <summary>
        /// Whether this client acts as the master (EtsSession feeds it to EtsSessionLogic as IsMaster): the room master with
        /// the game at once, as before; otherwise only when the slots name it (EtsSlotRules.ActingMaster, 4 Hz): a room
        /// master without the game hands the headquarters choice to the longest-present player with it, and keeps it only
        /// while nobody with the game is here. roomMaster is the runner's own IsSharedModeMasterClient.
        /// </summary>
        public bool ActsAsMaster(bool roomMaster)
        {
            if (roomMaster && !LocalNoGame)
            {
                return true;
            }

            int local = LocalId;
            return local >= 0 && _actingMasterId == local;
        }

        /// <summary>
        /// Another present player's slot shows them loading or in the world under a session epoch of 1 or more:
        /// EtsSession's guard against a late join snapshot (EtsSessionLogic.Inputs.OthersHaveSession, probe 1824 case 5).
        /// </summary>
        public bool OthersHaveSession => _othersHaveSession;

        private void OnRoomPlayersChanged(PlayerRef player)
        {
            _presentAt = 0f;
        }

        /// <summary>
        /// "Ets Net Player 0..49" by name, as AomNet.Find does, once a second until all are found; the scene's objects exist
        /// from load, and Fusion makes them valid when the room is joined (checked per frame, Valid).
        /// </summary>
        private void FindSlots()
        {
            if (_slotsComplete || Time.unscaledTime < _nextSlotFind)
            {
                return;
            }

            _nextSlotFind = Time.unscaledTime + 1f;
            int found = 0, highest = -1;

            foreach (NetworkBridgeData data in FindObjectsByType<NetworkBridgeData>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string name = data.name;

                if (!name.StartsWith(EtsPlayerWire.ObjectPrefix, StringComparison.Ordinal) || !int.TryParse(name.Substring(EtsPlayerWire.ObjectPrefix.Length), out int k) ||
                    k < 0 || k >= MaxSlots)
                {
                    continue;
                }

                if (data.IntCount < EtsPlayerWire.Used)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Network: '" + name + "' holds " + data.IntCount + " ints, a player slot needs " + EtsPlayerWire.Used + "; use a NetworkBridgeData64.");
                    continue;
                }

                _slotObjects[k] = data;
                found++;
                highest = Math.Max(highest, k);
            }

            // Slots are used 0..count-1: a gap (a hand-edited scene) ends the range at the gap.
            int count = 0;

            while (count < MaxSlots && _slotObjects[count] != null)
            {
                count++;
            }

            _slotCount = count;
            _slotsComplete = count == MaxSlots;

            // Logged when the count changes, not every second of an incomplete scene.
            if (found != _slotsLogged)
            {
                _slotsLogged = found;
                Debug.Log("[SomniumEuroTruck] Network: " + found + " player slot object(s) found ('" + EtsPlayerWire.ObjectPrefix + "0.." + highest + "'), " + count + " usable" +
                          (_slotsComplete ? "." : "; still looking every 5 s (a scene without all 50 plays the rest locally)."));
            }

            if (!_slotsComplete)
            {
                // A scene with fewer slots (hand-edited, or an old scene): keep looking, slowly (FindObjectsByType allocates).
                _nextSlotFind = Time.unscaledTime + 5f;
            }
        }

        /// <summary>The room's players as PlayerIds (runner.ActivePlayers), at PresentEvery and on a join or leave.</summary>
        private void RefreshPresent(bool online)
        {
            if (!online)
            {
                if (_present.Count > 0)
                {
                    _present.Clear();
                }

                return;
            }

            if (Time.unscaledTime < _presentAt)
            {
                return;
            }

            _presentAt = Time.unscaledTime + PresentEvery;
            _present.Clear();

            foreach (PlayerRef player in Runner.ActivePlayers)
            {
                _present.Add(player.PlayerId);
            }
        }

        private int _readInts, _wroteInts;

        /// <summary>
        /// §5.4 step 2.2: every slot read (only the laid-out ints, and only those of written slots), the claim stepped and
        /// carried out, the housekeeping, a pending seat request answered, the own seat table served, and the remote
        /// riders sampled for B's EarlyPass.
        /// </summary>
        private void ReadSlots(EtsSession session, double now)
        {
            bool online = Online;

            if (online)
            {
                FindSlots();
            }

            RefreshPresent(online);
            int localId = LocalId;
            _sessionEpoch = session != null ? session.Epoch : 0;
            _roomMaster = online && SceneNetworking.IsMasterClient;

            for (int k = 0; k < _slotCount; k++)
            {
                NetworkBridgeData data = _slotObjects[k];
                int[] slot = _slots[k];
                bool valid = online && Valid(data);
                _valid[k] = valid;
                _authority[k] = valid && data.Object.HasStateAuthority;
                _listed[k] = false;
                _counts[k] = false;

                if (!valid)
                {
                    if (slot[EtsPlayerWire.AtMagic] != 0)
                    {
                        Array.Clear(slot, 0, slot.Length);
                    }

                    continue;
                }

                NetworkArray<int> ints = data.IntArray;
                int magic = ints.Get(EtsPlayerWire.AtMagic);

                if (magic != EtsPlayerWire.Magic)
                {
                    if (slot[EtsPlayerWire.AtMagic] == EtsPlayerWire.Magic)
                    {
                        Array.Clear(slot, 0, slot.Length);
                    }

                    slot[EtsPlayerWire.AtMagic] = magic;
                    continue;
                }

                int n = Math.Min(ints.Length, EtsPlayerWire.Used);

                for (int i = 0; i < n; i++)
                {
                    int v = ints.Get(i);

                    if (v != slot[i])
                    {
                        slot[i] = v;
                        _readInts++;
                    }
                }

                _listed[k] = EtsSlotRules.Listed(slot, localId, _present);
                _counts[k] = _listed[k] && EtsSlotRules.Counts(slot, localId, _present, _sessionEpoch);

                // Another player's hit on an AI car (their EtsTraffic.HitReport): the traffic runner makes it a wreck too.
                if (_counts[k] && EtsTraffic.Instance != null && ints.Length > EtsPlayerWire.AtTrafficHit)
                {
                    EtsTraffic.Instance.ApplyHitReport(EtsPlayerWire.OwnerOf(slot), ints.Get(EtsPlayerWire.AtTrafficHit));
                }
            }

            StepClaim(session, now, online, localId);

            if (Time.unscaledTime >= _nextServe)
            {
                _nextServe = Time.unscaledTime + ServeEvery;
                Housekeeping(localId);
                ServeSeats(now, localId);
                int roomMasterBit = _roomMaster ? localId : EtsSlotRules.MasterOf(_slots, _slotCount, localId, _present);
                _actingMasterId = online ? EtsSlotRules.ActingMaster(_slots, _slotCount, localId, _present, _roomMaster, LocalNoGame, MySlot >= 0) : -1;
                _roomMasterId = _actingMasterId == localId ? -1 : _actingMasterId;

                if (_actingMasterId != _actingLogged && roomMasterBit >= 0 && _actingMasterId >= 0)
                {
                    _actingLogged = _actingMasterId;

                    if (_actingMasterId != roomMasterBit)
                    {
                        Debug.Log("[SomniumEuroTruck] Network: the room's master (" + (roomMasterBit == localId ? "this client" : NameOf(roomMasterBit)) + ") has no game; " +
                                  (_actingMasterId == localId ? "this client" : NameOf(_actingMasterId)) + " acts as master and chooses the headquarters.");
                    }
                }
                _othersHaveSession = EtsSlotRules.OthersHaveSession(_slots, _slotCount, localId, _present);
            }

            AnswerSeatRequest(now, localId);
            RefreshPlayers();
            SampleRiders(now);
        }

        private void StepClaim(EtsSession session, double now, bool online, int localId)
        {
            EtsPhase phase = session != null ? session.Current : EtsPhase.Connecting;
            var input = new EtsSlotClaim.Inputs
            {
                Now = now,
                LocalId = localId,
                Allowed = online && _slotCount > 0 && phase != EtsPhase.Connecting && phase != EtsPhase.Offline,
                SlotCount = _slotCount
            };

            _claim.Step(input, _slots, _valid, _authority, _present);

            if (_claim.AskAuthority >= 0)
            {
                NetworkBridgeData data = _slotObjects[_claim.AskAuthority];

                if (Valid(data))
                {
                    data.Object.RequestStateAuthority();
                }
            }

            if (_claim.WriteClaim)
            {
                // A fresh record ([net §2.5] step 2), then everything the owner says composed onto it at once, and written
                // this frame: the seat table and the rider record are packed again from what this client holds.
                EtsSlotRules.WriteClaim(_ownSlot, localId, _claim.Generation, _sessionEpoch, StampMs());
                ComposeOwnSlot(session);
                _seats.Changed = true;
                _motionCleared = false;
                _forceWrite = true;
            }

            string note = _claim.TakeNote();

            if (note != null)
            {
                Debug.Log("[SomniumEuroTruck] Network: " + note + ".");
            }

            if (MySlot != _lastSlot)
            {
                _lastSlot = MySlot;
                _forceWrite = true;
            }
        }

        /// <summary>
        /// Whoever holds a slot's authority writes it free when it names a player who has left (the master, to whom
        /// SceneNetworking gives the orphans), or this client while it is not the slot this client claims [net §2.5 step 5].
        /// </summary>
        private void Housekeeping(int localId)
        {
            for (int k = 0; k < _slotCount; k++)
            {
                if (!EtsSlotRules.ToFree(_slots[k], _authority[k], k == _claim.Slot, localId, _present))
                {
                    continue;
                }

                int gone = EtsPlayerWire.OwnerOf(_slots[k]);
                EtsSlotRules.WriteFree(_slots[k], StampMs());
                Push(_slotObjects[k], _slots[k]);
                Debug.Log("[SomniumEuroTruck] Network: slot " + k + " written free (" + (gone == localId ? "this client's own record, left behind by a lost claim" : "player " + gone + " left") + ").");
            }
        }

        /// <summary>
        /// The own truck's passenger seats ([net §2.7] steps 2 and 4): every Listed slot's rider record offered to the table,
        /// then stale and gone occupants freed. Packed into ints 26-27 when it changed.
        /// </summary>
        private void ServeSeats(double now, int localId)
        {
            int mySlot = MySlot;
            bool truck = mySlot >= 0 && OwnTruckPlaced;
            _seats.Begin();

            if (truck)
            {
                for (int k = 0; k < _slotCount; k++)
                {
                    if (_listed[k] && EtsPlayerWire.UnpackRider(_slots[k], out EtsRider rider, out _))
                    {
                        _seats.Offer(EtsPlayerWire.OwnerOf(_slots[k]), rider, mySlot, OwnTruckSerial);
                    }
                }
            }

            _seats.End(now, _present, truck);
            string note = _seats.TakeNote();

            if (note != null)
            {
                Debug.Log("[SomniumEuroTruck] Network: own truck: " + note + ".");
            }
        }

        /// <summary>The remote riders B places ([net §2.7]): each counted slot's rider record on its own timeline, sampled now.</summary>
        private void SampleRiders(double now)
        {
            _remoteRiders.Clear();

            for (int k = 0; k < _slotCount; k++)
            {
                EtsRiderTrack track = _riderTracks[k];

                if (!_counts[k])
                {
                    if (track.Present)
                    {
                        track.Clear();
                    }

                    continue;
                }

                track.Add(_slots[k], now);

                // A requesting record is not a place yet (its offset is the seat the rider hopes for): not drawn there.
                if (!track.Present || track.Latest.Requesting || !track.Sample(now, out EtsRider rider, out Vector3 velocity))
                {
                    continue;
                }

                _remoteRiders.Add(new EtsRemoteRider
                {
                    PlayerId = EtsPlayerWire.OwnerOf(_slots[k]),
                    Player = _slotPlayers[k],
                    Xr = rider.Xr,
                    Sampled = rider,
                    Velocity = velocity
                });
            }
        }

        private float _nextPlayerWalk;

        /// <summary>
        /// Each Listed slot's Bridge player and nickname (B's remote riders need the ISomniumPlayer; the nameplates and
        /// the Convoys page the name): one walk of the Bridge's player list, at most once a second while a slot lacks its
        /// player or name, else every 5 s (a mode or avatar switch brings a new player object under the same id).
        /// </summary>
        private void RefreshPlayers()
        {
            bool missing = false;

            for (int k = 0; k < _slotCount; k++)
            {
                if (!_listed[k])
                {
                    continue;
                }

                int owner = EtsPlayerWire.OwnerOf(_slots[k]);

                if (_slotPlayerIds[k] != owner || _slotPlayers[k] == null)
                {
                    missing = true;
                    break;
                }
            }

            float now = Time.unscaledTime;

            if (now < _nextPlayerWalk || (!missing && now < _nextPlayerWalk + 4f))
            {
                return;
            }

            _nextPlayerWalk = now + 1f;
            List<ISomniumPlayer> all = null;

            try
            {
                all = SomniumBridge.PlayersContainer?.AllPlayers;
            }
            catch (Exception)
            {
                // No client (the editor).
            }

            for (int k = 0; k < _slotCount; k++)
            {
                int owner = _listed[k] ? EtsPlayerWire.OwnerOf(_slots[k]) : -1;
                ISomniumPlayer found = null;

                if (owner >= 0 && all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        ISomniumPlayer p = all[i];

                        if (p?.Properties != null && !p.Properties.IsLocal && p.Properties.PhotonId == owner)
                        {
                            found = p;
                            break;
                        }
                    }
                }

                _slotPlayerIds[k] = owner;
                _slotPlayers[k] = found;
                string nick = found?.Properties?.NickName;
                _slotNames[k] = !string.IsNullOrEmpty(nick) ? nick : owner >= 0 ? "Player " + owner : null;
            }
        }

        // ---------------------------------------------------------------- the puppets ([net §2.6])

        private EtsNetVehicle _buildingPuppet;

        /// <summary>
        /// §5.4 step 2.3: one EtsNetVehicle per counted slot with "truck present", fed and drawn; built one at a time
        /// (C's factory, sliced); rebuilt on a new truck serial or identity; hidden at once and destroyed after the grace
        /// when its slot stops counting; destroyed at once when its truck is despawned. Then the truck-against-truck rule.
        /// </summary>
        private void DrawPuppets(double now)
        {
            Vector3 center = CenterGame();
            Vector3 focus = LocalFocus();
            float dt = Time.deltaTime;
            float radius2 = TruckDrawRadius * TruckDrawRadius;
            EtsTruckRig own = EtsLocalTruck.Own;

            for (int k = 0; k < MaxSlots; k++)
            {
                EtsNetVehicle puppet = _puppets[k];
                int[] slot = _slots[k];
                EtsTruckIdentity identity = default;
                int truckSerial = 0;
                bool truck = k < _slotCount && _counts[k] && EtsPlayerWire.UnpackTruck(slot, out identity, out truckSerial) && Placed(slot);

                if (!truck)
                {
                    if (puppet == null)
                    {
                        continue;
                    }

                    bool counts = k < _slotCount && _counts[k];

                    if (counts || puppet.MissingSince >= 0f && Time.unscaledTime - puppet.MissingSince >= VehicleGrace)
                    {
                        RemovePuppet(k, counts ? "despawned" : !EtsSlotRules.Contains(_present, puppet.OwnerId) ? "owner left" : "slot no longer counts here (epoch or claim)");
                        continue;
                    }

                    if (puppet.MissingSince < 0f)
                    {
                        puppet.MissingSince = Time.unscaledTime;
                    }

                    puppet.Draw(now, dt, false);
                    continue;
                }

                int owner = EtsPlayerWire.OwnerOf(slot);

                if (puppet != null && (puppet.OwnerId != owner || puppet.TruckSerial != truckSerial || !puppet.Identity.Same(identity)))
                {
                    RemovePuppet(k, puppet.OwnerId != owner ? "new owner" : "new truck (serial " + truckSerial + ")");
                    puppet = null;
                }

                if (puppet == null)
                {
                    puppet = EtsNetVehicle.Create(k, owner, identity, truckSerial);
                    _puppets[k] = puppet;
                }

                puppet.MissingSince = -1f;
                puppet.Timeline.Add(EtsPlayerWire.ToTimeline(slot, center), now);
                bool present = EtsPlayerWire.UnpackTrailer(slot, out EtsTrailerIdentity trailerId);
                puppet.WantTrailer(present, (slot[EtsPlayerWire.AtSerials] >> 16) & 0xFF, trailerId);

                Transform frame = puppet.Frame;
                bool near = !puppet.Drawn || (frame.position - focus).sqrMagnitude <= radius2;
                puppet.Draw(now, dt, near);

                // The cab is the rig's own rule (EtsTruckRig.UpdateInterior, C3): built and shown within InteriorRadius (25 m)
                // or while the local player sits in it, hidden beyond 35 m. No SetInteriorVisible here: a call takes over
                // from that rule, and a per-frame one without the 25/35 m hysteresis flipped the cab of a truck standing at
                // the radius on and off every frame (integrator, C's checker).
                puppet.Contacts(own);
            }

            StartNextBuild();
        }

        private static bool Placed(int[] slot)
        {
            return (slot[EtsPlayerWire.AtFlags] & EtsPlayerWire.TruckPlaced) != 0;
        }

        /// <summary>
        /// One puppet build at a time (each is sliced at 1.5 ms a frame; two at once would be 3 ms): the lowest slot's truck
        /// waiting, else the lowest slot's trailer waiting (C's sliced EtsTrailer.BuildPuppet, EtsNetVehicle.BuildTrailer).
        /// </summary>
        private void StartNextBuild()
        {
            if (_buildingPuppet != null && (_buildingPuppet.Building || _buildingPuppet.TrailerBuilding))
            {
                return;
            }

            _buildingPuppet = null;

            // A visitor without the game (EtsFlow.NoGame) has nothing to build from: no builds, no failure logged per truck
            // (D's request). The archives turning up later (EtsMapViewer.LoadAt retries) lets the builds start then.
            if (!EtsEngine.IsMounted)
            {
                return;
            }

            for (int k = 0; k < MaxSlots; k++)
            {
                EtsNetVehicle puppet = _puppets[k];

                if (puppet == null || puppet.Rig != null || puppet.Building || puppet.BuildFailed || !puppet.Drawn)
                {
                    continue;
                }

                _buildingPuppet = puppet;
                puppet.StartCoroutine(puppet.Build(Built));
                return;
            }

            for (int k = 0; k < MaxSlots; k++)
            {
                EtsNetVehicle puppet = _puppets[k];

                if (puppet == null || !puppet.Drawn || !puppet.NeedsTrailer)
                {
                    continue;
                }

                _buildingPuppet = puppet;
                puppet.StartCoroutine(puppet.BuildTrailer(TrailerBuilt));
                return;
            }
        }

        private void TrailerBuilt(EtsNetVehicle puppet)
        {
            string who = NameOf(puppet.OwnerId);

            if (puppet.TrailerFailure != null)
            {
                Debug.LogWarning("[SomniumEuroTruck] Network: trailer of " + who + " (slot " + puppet.Slot + "): could not be built here - " + puppet.TrailerFailure + "; their truck is drawn without it.");
                return;
            }

            Debug.Log("[SomniumEuroTruck] Network: trailer of " + who + " (slot " + puppet.Slot + "): " + (puppet.Trailer != null ? "built in " + Mathf.RoundToInt(puppet.TrailerSeconds * 1000f) + " ms (" + EtsTrailer.LastBuild + ")" : "built, but out of date on arrival (thrown away)") + ".");
        }

        private void Built(EtsNetVehicle puppet)
        {
            string who = NameOf(puppet.OwnerId);

            if (puppet.BuildFailed)
            {
                Debug.LogWarning("[SomniumEuroTruck] Network: vehicle of " + who + " (slot " + puppet.Slot + "): could not be built here - " + puppet.BuildFailure + "; a stand-in box is drawn instead.");
                return;
            }

            Debug.Log("[SomniumEuroTruck] Network: vehicle of " + who + " (slot " + puppet.Slot + "): built in " + Mathf.RoundToInt(puppet.BuiltSeconds * 1000f) + " ms (identity preview " +
                      puppet.Identity.Preview + ", hash " + (puppet.Identity.CatalogueHash & 0xFF) + ", paint " + (puppet.Identity.PaintRgb < 0 ? "the save's" : puppet.Identity.PaintRgb.ToString("x6")) +
                      ", truck serial " + puppet.TruckSerial + "; " + EtsTruckFactory.LastPace + ").");
        }

        private void RemovePuppet(int k, string why)
        {
            EtsNetVehicle puppet = _puppets[k];
            _puppets[k] = null;

            if (puppet == null)
            {
                return;
            }

            if (_buildingPuppet == puppet)
            {
                _buildingPuppet = null;
            }

            Debug.Log("[SomniumEuroTruck] Network: vehicle of " + NameOf(puppet.OwnerId) + " (slot " + k + ") removed (" + why + ").");
            Destroy(puppet.gameObject);
        }

        /// <summary>
        /// The nameplates' words at 4 Hz ([net §2.6], PLAN §3.7 item 4): the player's name, the star from their slot's master
        /// bit, and the destination while they have a job. D's EtsNameplate turns and scales itself (its own LateUpdate).
        /// </summary>
        private void UpdatePlates()
        {
            if (Time.unscaledTime < _nextPlates)
            {
                return;
            }

            _nextPlates = Time.unscaledTime + ServeEvery;

            for (int k = 0; k < _slotCount; k++)
            {
                EtsNetVehicle puppet = _puppets[k];

                if (puppet == null)
                {
                    continue;
                }

                int[] slot = _slots[k];
                // The acting master (the room master, or who stands in for one without the game): EtsSlotRules.ActingMaster.
                bool master = _actingMasterId >= 0 && EtsPlayerWire.OwnerOf(slot) == _actingMasterId;
                bool job = (slot[EtsPlayerWire.AtStatus] & EtsPlayerWire.StatusJob) != 0;
                ulong city = job ? (uint)slot[EtsPlayerWire.AtCity] | ((ulong)(uint)slot[EtsPlayerWire.AtCity + 1] << 32) : 0UL;
                puppet.SetPlate(_slotNames[k] ?? NameOf(puppet.OwnerId), master, city);
            }
        }

        private float _nextPlates;

        /// <summary>Where this client stands: its seat's stream focus (the truck it is in, else the avatar), else the camera.</summary>
        private Vector3 LocalFocus()
        {
            Transform focus = Seat != null ? Seat.StreamFocus : null;

            if (focus != null)
            {
                return focus.position;
            }

            Camera eye = Camera.main;
            return eye != null ? eye.transform.position : Vector3.zero;
        }

        /// <summary>The map centre every WirePos is relative to (the session's origin once Loading; [net §2.4]).</summary>
        private static Vector3 CenterGame()
        {
            EtsMapViewer viewer = EtsMapViewer.Current;
            return viewer != null ? viewer.CenterGame : Vector3.zero;
        }

        private static int StampMs()
        {
            return (int)(Time.fixedTimeAsDouble * 1000.0);
        }

        // ---------------------------------------------------------------- the own slot (written by this client only)

        /// <summary>How often the own slot's header and the players' rows are composed (4 Hz: a display, not a motion).</summary>
        private const float ComposeEvery = 0.25f;

        /// <summary>
        /// A rider record older than this is re-stamped now when the slot is written for another reason: int 43 carries the
        /// rider's time as a signed 16-bit difference from int 2 (PLAN §5.3), ±32.7 s. A still rider (a seated passenger)
        /// is still there now, so the newer stamp is true, and the difference never clamps.
        /// </summary>
        private const int RiderRestampMs = 20000;

        private float _nextCompose;
        private float _nextWrite;
        private bool _forceWrite;
        private bool _motionCleared;
        private readonly int[] _written = new int[EtsPlayerWire.Ints];

        private EtsRider _localRider;
        private bool _hasLocalRider;
        private int _riderStampMs;
        private bool _riderFromRequest;
        private double _grantedAt = -1.0;
        private EtsTrailer _ownTrailer;
        private int _ownTrailerSerial;

        /// <summary>
        /// This client's player-slot record ([net §2.4] with PLAN §5.3). The header, identity and job summary are composed at
        /// 4 Hz (ComposeOwnSlot); the motion, seat table and rider record at the write (WriteOwnSlot, at most 30 Hz, only
        /// when something but the seq and stamp changed, so a parked truck costs nothing).
        /// </summary>
        private readonly int[] _ownSlot = new int[EtsPlayerWire.Ints];

        private readonly EtsPlayerView _me = new EtsPlayerView { Slot = -1, PlayerId = -1, Name = "", IsLocal = true };
        private readonly List<EtsPlayerView> _players = new List<EtsPlayerView>(MaxSlots);
        private ulong _jobCargo, _jobCity;
        private bool _hasJob;

        /// <summary>The own slot record's ints, as last composed (PLAN §5.3; read-only use: probes and the log).</summary>
        public IReadOnlyList<int> OwnSlotRecord => _ownSlot;

        /// <summary>
        /// The money shown for this player - int 32 of their slot, the top bar's figure for others: G's career
        /// balance when the store exists (EtsProfileStore.Money: the starting €2,000 until the record is read), else
        /// EtsJobs' own balance.
        /// </summary>
        public long LocalMoney
        {
            get
            {
                EtsProfileStore store = EtsProfileStore.Current;

                if (store != null)
                {
                    return store.Money;
                }

                EtsJobs jobs = Jobs != null ? Jobs : EtsJobs.Instance;
                return jobs != null ? jobs.Balance : 0L;
            }
        }

        private bool OwnTruckPlaced
        {
            get
            {
                EtsTruckRig own = EtsLocalTruck.Own;
                return own != null && own.Controller != null && own.Controller.Placed;
            }
        }

        /// <summary>
        /// §5.4 step 2.5. The header at 4 Hz; then, holding the claimed slot's authority, the motion, the seat table and the
        /// rider record, and the write: every int but the seq and the stamp compared with what was last written (the rider's
        /// time difference taken against the old stamp, or a still rider would rewrite the slot every frame), then seq + 1 and
        /// the new stamp only when the motion changed (see below), and the changed ints set (Fusion sends only those).
        /// </summary>
        private void WriteOwnSlot(EtsSession session)
        {
            if (Time.unscaledTime >= _nextCompose)
            {
                _nextCompose = Time.unscaledTime + ComposeEvery;
                ComposeOwnSlot(session);
                BuildPlayers();
            }

            ReleaseStaleRequestRecord();

            int k = _claim.Slot;

            if (!_claim.Holding || k < 0 || !_authority[k] || (!_forceWrite && Time.unscaledTime < _nextWrite))
            {
                return;
            }

            _nextWrite = Time.unscaledTime + WriteEvery;
            _forceWrite = false;
            PackOwnMotion();

            if (_seats.Changed)
            {
                _seats.Changed = false;
                EtsPlayerWire.PackSeats(_seats.Occupants, _ownSlot);
            }

            _ownSlot[EtsPlayerWire.AtSeq] = _written[EtsPlayerWire.AtSeq];
            _ownSlot[EtsPlayerWire.AtStamp] = _written[EtsPlayerWire.AtStamp];
            PackOwnRider();

            // The last AI car this client's truck hit, for the traffic runner (a header change, not a motion sample).
            EtsTraffic trafficHit = EtsTraffic.Instance;
            _ownSlot[EtsPlayerWire.AtTrafficHit] = trafficHit != null ? trafficHit.HitReport : 0;
            bool motion = false, changed = false;

            for (int i = 0; i < EtsPlayerWire.Ints; i++)
            {
                if (i == EtsPlayerWire.AtSeq || i == EtsPlayerWire.AtStamp || _ownSlot[i] == _written[i])
                {
                    continue;
                }

                changed = true;
                motion |= Motion(i);
            }

            // Also when the slot as read is not what was written (a claim record written by the claim, a clean-up racing).
            bool claim = _slots[k][EtsPlayerWire.AtOwner] != _ownSlot[EtsPlayerWire.AtOwner];
            int stamp = StampMs();
            int riderAhead = _riderStampMs - _written[EtsPlayerWire.AtStamp];

            if (!changed && !claim)
            {
                return;
            }

            // Seq and stamp move with the truck's motion (the truck timeline's samples, EtsNetTimeline dedups on Seq), with a
            // claim, and when the rider's time would leave int 43's +-32.7 s difference from the stamp. A rider-only change
            // (a walker on a truck, B's 20 Hz) moves int 43's own time difference instead: 2-3 ints a send, not 5 (probe 2200
            // section 7). A header-only change (status, load %, job) needs no new sample either.
            if (motion || claim || (_hasLocalRider && (riderAhead > RiderAheadMs || riderAhead < -RiderAheadMs)))
            {
                if (_hasLocalRider && stamp - _riderStampMs > RiderRestampMs)
                {
                    _riderStampMs = stamp;
                }

                _ownSlot[EtsPlayerWire.AtSeq] = _written[EtsPlayerWire.AtSeq] + 1;
                _ownSlot[EtsPlayerWire.AtStamp] = stamp;
                PackOwnRider();
            }

            Push(_slotObjects[k], _ownSlot);
            Array.Copy(_ownSlot, _written, EtsPlayerWire.Ints);
        }

        /// <summary>A rider's time may run this far ahead of (or behind) the slot's stamp before the stamp is moved to now.</summary>
        private const int RiderAheadMs = 30000;

        /// <summary>The ints of the truck's motion ([net §2.4] 5, 9-25): a change there is a new sample for everyone's timeline.</summary>
        private static bool Motion(int i)
        {
            return i == EtsPlayerWire.AtSerials || (i >= EtsPlayerWire.AtFlags && i <= EtsPlayerWire.AtTrailerRoll);
        }

        /// <summary>Sets the ints that differ (Fusion sends a changed int only); nothing without the object's authority.</summary>
        private void Push(NetworkBridgeData data, int[] record)
        {
            if (!Valid(data) || !data.Object.HasStateAuthority)
            {
                return;
            }

            NetworkArray<int> ints = data.IntArray;
            int n = Math.Min(ints.Length, record.Length);

            for (int i = 0; i < n; i++)
            {
                if (ints.Get(i) != record[i])
                {
                    ints.Set(i, record[i]);
                    _wroteInts++;
                }
            }
        }

        /// <summary>
        /// The own truck and its trailer into ints 5 (placement epoch, trailer serial), 9-25 ([net §2.4]): the Rigidbody's
        /// pose and velocity (the physics state, on the same fixed clock as the stamp), steering, engine and pedals, and the
        /// trailer as the articulation when coupled (the rebuild on the other side is exact to 1 cm, probe 1823) or its own
        /// pose when parked. No truck: the motion ints are cleared once.
        /// </summary>
        private void PackOwnMotion()
        {
            EtsTruckRig own = EtsLocalTruck.Own;
            EtsTruckController truck = own != null ? own.Controller : null;
            Rigidbody body = truck != null && truck.Placed ? truck.Body : null;

            if (body == null)
            {
                if (!_motionCleared)
                {
                    _motionCleared = true;

                    for (int i = EtsPlayerWire.AtFlags; i <= EtsPlayerWire.AtTrailerRoll; i++)
                    {
                        _ownSlot[i] = 0;
                    }

                    _ownSlot[EtsPlayerWire.AtSerials] &= 0xFF00;
                }

                return;
            }

            _motionCleared = false;
            EtsTrailer trailer = OwnTrailer(own);

            if (trailer != _ownTrailer)
            {
                _ownTrailer = trailer;

                if (trailer != null)
                {
                    _ownTrailerSerial = (_ownTrailerSerial % 255) + 1;
                }
            }

            bool coupled = trailer != null && trailer.Attached && trailer.Body != null;
            EtsTruckSeat seat = Seat;
            bool driven = seat != null && seat.IsDriver && seat.Rig == own;
            Quaternion rotation = body.rotation;
            var m = new EtsPlayerWire.Motion
            {
                PlacementEpoch = truck.Placements & 0xFF,
                TrailerSerial = trailer != null ? _ownTrailerSerial : 0,
                Flags = EtsPlayerWire.TruckPlaced |
                        (driven ? EtsPlayerWire.TruckDriven : 0) |
                        (truck.ParkingBrake ? EtsPlayerWire.TruckParkingBrake : 0) |
                        (truck.Brake > 0.05f ? EtsPlayerWire.TruckBrakeLights : 0) |
                        (truck.Gear < 0 ? EtsPlayerWire.TruckReverse : 0) |
                        (truck.EngineRunning ? EtsPlayerWire.TruckEngine : 0) |
                        (trailer != null ? EtsPlayerWire.TruckTrailer : 0) |
                        (coupled ? EtsPlayerWire.TruckTrailerCoupled : 0) |
                        truck.IndicatorWireFlags,
                Position = body.position,
                Rotation = rotation,
                Velocity = body.isKinematic ? Vector3.zero : body.linearVelocity,
                Steer = truck.SteerInput,
                Rpm = truck.Rpm,
                Throttle = truck.Throttle,
                Brake = truck.Brake,
                Gear = truck.Gear,
                Articulation = coupled ? EtsPlayerWire.Articulation(rotation, trailer.Body.rotation) : Quaternion.identity,
                TrailerPosition = trailer != null ? trailer.transform.position : Vector3.zero,
                TrailerRotation = trailer != null ? trailer.transform.rotation : Quaternion.identity
            };

            EtsPlayerWire.PackMotion(m, CenterGame(), _ownSlot);

            if (trailer != null)
            {
                EtsPlayerWire.PackTrailer(trailer.Identity, _ownSlot);
            }
            else
            {
                _ownSlot[EtsPlayerWire.AtTrailer] = 0;
                _ownSlot[EtsPlayerWire.AtTrailerCompany] = 0;
            }
        }

        /// <summary>The own truck's trailer: the coupled one (the rig's), else the job's standing one (EtsJobs).</summary>
        private EtsTrailer OwnTrailer(EtsTruckRig own)
        {
            if (own == null)
            {
                return null;
            }

            EtsTrailer trailer = own.Controller != null ? own.Controller.Trailer : null;

            if (trailer == null)
            {
                trailer = own.Trailer;
            }

            if (trailer == null)
            {
                EtsJobs jobs = Jobs != null ? Jobs : EtsJobs.Instance;
                trailer = jobs != null ? jobs.Trailer : null;
            }

            return trailer;
        }

        private void PackOwnRider()
        {
            if (_hasLocalRider)
            {
                EtsPlayerWire.PackRider(_localRider, _riderStampMs, _ownSlot);
            }
            else
            {
                EtsPlayerWire.ClearRider(_ownSlot);
            }
        }

        /// <summary>
        /// The own slot's header at 4 Hz (status, load %, flags incl. the room-master bit, owner, session epoch, map
        /// signature), the truck identity and serial, and the job summary with int 32 = the career balance (a display
        /// value; the balance itself stays in LocalStorage). Probe 2002 checks what it composes.
        /// </summary>
        private void ComposeOwnSlot(EtsSession session)
        {
            EtsTruckSeat seat = Seat;
            EtsTruckRig own = EtsLocalTruck.Own;
            bool seated = seat != null && seat.Seated;
            bool truck = own != null && own.Controller != null && own.Controller.Placed;
            bool roomMaster = Online && SceneNetworking.IsMasterClient;

            var state = new EtsPlayerWire.Own
            {
                LocalId = LocalId,
                SessionEpoch = session != null ? session.Epoch : 0,
                TruckSerial = _truckSerial,
                MapSignature = session != null ? session.MapSignature : 0,
                StampMs = _ownSlot[EtsPlayerWire.AtStamp],
                Phase = session != null ? session.Current : EtsPhase.Connecting,
                LoadProgress = session != null ? session.LoadProgress : 0f,
                Seated = seated,
                Driver = seated && seat.IsDriver,
                TruckPlaced = truck,
                HasJob = _hasJob,
                Xr = EtsVr.Present,
                Standing = !seated && _hasLocalRider && _localRider.Seat == EtsPlayerWire.SeatStanding,
                RoomMaster = roomMaster,
                NoGame = LocalNoGame,
                Truck = truck ? own.Identity : new EtsTruckIdentity { Preview = -1 },
                Cargo = _jobCargo,
                City = _jobCity,
                Money = LocalMoney
            };

            EtsPlayerWire.Status status = EtsPlayerWire.ComposeOwn(state, _ownSlot);

            // The local player's own row, read back from the record just composed, so the row shows what the slot says.
            _me.PlayerId = LocalId;
            _me.Slot = MySlot;
            _me.Name = LocalNick();
            _me.IsMaster = session != null && session.IsMaster;
            _me.IsLocal = true;
            _me.Xr = state.Xr;
            _me.Status = status;
            _me.LoadPercent = (_ownSlot[EtsPlayerWire.AtStatus] >> 8) & 0xFF;
            _me.HasTruck = truck;
            _me.Truck = state.Truck;
            _me.PassengerSeatFree = truck && _seats.Occupants[0] < 0;
            _me.HasJob = _hasJob;
            _me.CargoToken = _jobCargo;
            _me.DestinationCityToken = _jobCity;
            _me.Money = state.Money;
        }

        /// <summary>
        /// The Convoys page and the top bar's rows (PLAN §7.A, §3.7): this player first, then every Listed slot in slot order,
        /// one kept EtsPlayerView per slot filled field by field (no allocation at 4 Hz once each slot has been seen).
        /// </summary>
        private void BuildPlayers()
        {
            _players.Clear();
            _players.Add(_me);

            for (int k = 0; k < _slotCount; k++)
            {
                if (!_listed[k])
                {
                    continue;
                }

                int[] slot = _slots[k];
                EtsPlayerView view = _views[k] ?? (_views[k] = new EtsPlayerView());
                int flags = slot[EtsPlayerWire.AtStatus];
                view.PlayerId = EtsPlayerWire.OwnerOf(slot);
                view.Slot = k;
                view.Name = _slotNames[k] ?? "Player " + view.PlayerId;
                view.IsMaster = _actingMasterId >= 0 && view.PlayerId == _actingMasterId; // EtsSlotRules.ActingMaster (bit 19, or who stands in for a master without the game)
                view.IsLocal = false;
                view.Xr = (flags & EtsPlayerWire.StatusVr) != 0;
                view.Status = (EtsPlayerWire.Status)(flags & 0xF);
                view.LoadPercent = (flags >> 8) & 0xFF;
                view.HasTruck = EtsPlayerWire.UnpackTruck(slot, out EtsTruckIdentity id, out _);
                view.Truck = id;
                EtsPlayerWire.UnpackSeats(slot, _occupants);
                view.PassengerSeatFree = view.HasTruck && _occupants[0] < 0;
                view.HasJob = (flags & EtsPlayerWire.StatusJob) != 0;
                EtsPlayerWire.UnpackJob(slot, out view.CargoToken, out view.DestinationCityToken, out int money);
                view.Money = money;
                _players.Add(view);
            }
        }

        /// <summary>The local player's nickname, or "" without a client; no allocation (the Bridge keeps the string).</summary>
        private static string LocalNick()
        {
            try
            {
                string nick = SomniumBridge.PlayersContainer?.LocalPlayer?.Properties?.NickName;
                return nick ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        // ---------------------------------------------------------------- the log line every 10 s ([net §2.12])

        private float _nextReport;
        private float _reportFrom;

        private void Report(double now)
        {
            if (Time.unscaledTime < _nextReport)
            {
                return;
            }

            float span = Time.unscaledTime - _reportFrom;
            _reportFrom = Time.unscaledTime;
            _nextReport = Time.unscaledTime + 10f;

            if (!Online || span <= 0f || span > 60f)
            {
                _readInts = 0;
                _wroteInts = 0;
                return;
            }

            int players = 0, trucks = 0, built = 0;
            float worstDelay = 0f;

            for (int k = 0; k < _slotCount; k++)
            {
                players += _listed[k] ? 1 : 0;
                EtsNetVehicle puppet = _puppets[k];

                if (puppet != null)
                {
                    trucks++;
                    built += puppet.Rig != null ? 1 : 0;
                    worstDelay = Mathf.Max(worstDelay, puppet.Timeline.Delay);
                }
            }

            Debug.Log("[SomniumEuroTruck] Network: slots: " + (players + 1) + " players (this one in slot " + MySlot + (_claim.Holding ? "" : ", " + _claim.Current) + "), " +
                      trucks + " other trucks (" + built + " built), " + _remoteRiders.Count + " riders; wrote " + Mathf.RoundToInt(_wroteInts / span) + " ints/s (" +
                      Mathf.RoundToInt(_wroteInts * 4f / span) + " B/s); read " + Mathf.RoundToInt(_readInts / span) + " ints/s (" + Mathf.RoundToInt(_readInts * 4f / span) +
                      " B/s); worst puppet delay " + Mathf.RoundToInt(worstDelay * 1000f) + " ms; master " + (_actingMasterId >= 0 && _actingMasterId == LocalId ? "me" : _roomMasterId >= 0 ? NameOf(_roomMasterId) : "unknown") +
                      (_roomMaster && _actingMasterId != LocalId ? " (this client is the room's master but has no game: " + (_roomMasterId >= 0 ? "they choose for it" : "nobody with the game is here") + ")" : "") + ".");
            _readInts = 0;
            _wroteInts = 0;
        }

        // ---------------------------------------------------------------- the own truck (PLAN §5.5 step 8, [net §2.6])

        /// <summary>
        /// How long a spawn waits for the map's first build before looking for ground (EtsTruckRig.PlaceWhenGroundExists;
        /// a spawn is only allowed InWorld, when the yard is built, so this is a guard, not an expected wait).
        /// </summary>
        private const float SpawnMapWait = 30f;

        /// <summary>The margin round a truck's body box that must be free of other vehicles and players at a stand ([net §2.6]: "truck bounds + 0.5 m").</summary>
        private const float OccupancyMargin = 0.5f;

        /// <summary>A lateral step's ground must lie within this of its anchor stand's height (a yard is flat; a ditch or a roof is not the yard).</summary>
        private const float LateralGroundRange = 2f;

        /// <summary>
        /// What must not be at a stand: vehicles (every truck, trailer and AI car is one box on layer 28), and players
        /// - the avatar's CharacterController is on Ignore Raycast (2), the VR hands' boxes on PhysicalObject (7), the
        /// Player layer (24) (EtsLayers' notes, from the client's own layer matrix).
        /// </summary>
        private const int StandBlockers = (1 << EtsLayers.Vehicle) | (1 << EtsLayers.IgnoreRaycast) | (1 << EtsLayers.PhysicalObject) | (1 << EtsLayers.Player);

        private Coroutine _spawning;
        private Action<string> _spawnFailed;

        /// <summary>
        /// A spawn is in progress: set before StartCoroutine, cleared by whatever ends it. The truth for "on its way",
        /// not _spawning: a spawn that fails before its first yield (no free place, an identity the factory refuses at
        /// once) has ended inside StartCoroutine, before its handle could be stored, and a stored handle would then say
        /// "on its way" for good (checker, Phase 2).
        /// </summary>
        private bool _spawnActive;

        /// <summary>+1 per spawn started or cancelled: a spawn routine's finally touches the shared state only while it is the current one.</summary>
        private int _spawnSerial;

        /// <summary>The holder of the truck being built or placed, destroyed if the spawn is cancelled or fails (never left standing).</summary>
        private GameObject _building;
        private GameObject _ownHolder;
        private int _truckSerial;
        private readonly List<EtsArrival.Stand> _stands = new List<EtsArrival.Stand>();
        private readonly List<EtsArrival.Stand> _order = new List<EtsArrival.Stand>();
        private readonly Collider[] _overlap = new Collider[16];
        private EtsTraffic _traffic;

        /// <summary>The own truck's serial, +1 per spawn (1-255; 0 = none yet): int 5 bits 8-15 of the slot, and the serial a rider on it names.</summary>
        public int OwnTruckSerial => _truckSerial & 0xFF;

        /// <summary>A spawn is being built or placed.</summary>
        public bool SpawningTruck => _spawnActive;

        /// <summary>
        /// Whether a truck may be spawned now, and if not, why (the Truck Preference page shows it; none of these
        /// words are in the game's locale). Only in the world (the yard exists and the player stands in it), not
        /// while another spawn runs, not during a job (PLAN §3.5: "Finish or cancel your job first"), not while
        /// sitting in another player's truck (§3.5), and only with the game's files mounted.
        /// </summary>
        public bool CanSpawnTruck(out string reason)
        {
            reason = "";

            if (!EtsFeatures.NewFlow)
            {
                reason = "This world's own truck is the scene's."; // not in the locale
                return false;
            }

            EtsSession session = EtsSession.Instance;

            if (session == null || session.Current != EtsPhase.InWorld)
            {
                reason = "You can choose a truck once you have arrived at the headquarters."; // not in the locale
                return false;
            }

            if (_spawnActive)
            {
                reason = "Your truck is on its way."; // not in the locale
                return false;
            }

            EtsJobs jobs = Jobs != null ? Jobs : EtsJobs.Instance;

            if (jobs != null && jobs.HasJob)
            {
                reason = "Finish or cancel your job first"; // not in the locale (PLAN §3.5)
                return false;
            }

            EtsTruckRig occupied = EtsLocalTruck.Occupied;

            if (occupied != null && occupied != EtsLocalTruck.Own)
            {
                reason = "Get out of " + (occupied.OwnerPlayerId >= 0 ? NameOf(occupied.OwnerPlayerId) + "'s" : "that") + " truck first"; // not in the locale (PLAN §3.5)
                return false;
            }

            if (!EtsEngine.IsMounted)
            {
                reason = "The game's files could not be opened."; // not in the locale
                return false;
            }

            // The cap ([net §2.6] step 1, Q-A4 16): other players' trucks on the road (their slots' "truck present"); this
            // player's own is replaced, so it does not count. Soft: two spawning at once may make 17, which is harmless.
            int others = EtsSlotRules.OtherTrucks(_slots, _slotCount, LocalId, _present);

            if (others >= MaxTrucks)
            {
                reason = "All " + MaxTrucks + " trucks are on the road - ride along with someone"; // not in the locale (PLAN §8 A3)
                return false;
            }

            return true;
        }

        /// <summary>
        /// Builds the chosen preview truck through C's factory and stands it on the first free stand of the session's
        /// yard (PLAN §5.5 step 8, §3.6, C23, [net §2.6]):
        ///   1. One truck per player ([net §2.6]): a previous own truck goes first (DespawnOwnTruck's steps), and a
        ///      frame passes so its stand is free.
        ///   2. The yard's stands: D's HQ catalogue row for the chosen city (garage 14, parking 10, trailer bays 1,
        ///      never 13), or - on a client without the catalogue, a joiner - the same stands read from the map round
        ///      the yard's anchor with D's own EtsHqCatalogBuilder.SpotsOf and ordered the catalogue's way
        ///      (EtsArrival.StandsFromMap; probe 2003: identical for all 72 cities). Then the lateral 4 m steps beside
        ///      the first stand (EtsArrival.SpawnOrder; D's U18 measurement: 1-8 stands a yard, median 1).
        ///   3. The first place whose box (a truck's body box + 0.5 m) holds no vehicle and no player; a lateral step
        ///      also no building and ground within 2 m of the stand's height.
        ///   4. EtsTruckFactory.Build (sliced at 1.5 ms a frame; C's probe 2030/2031) under an "Ets Own Truck" holder
        ///      standing there, so the finished rig appears in its place; the place checked again with the built
        ///      truck's own box (something may have come there during the build).
        ///   5. EtsTruckRig.PlaceWhenGroundExists (C: the viewer's own wait for ground, never placing without it),
        ///      parked, so the player gets in by clicking it (B's door).
        ///   6. EtsLocalTruck.SetOwn(rig): the seat, camera, sound, jobs, menu and tablet rebind (PLAN §3.1).
        /// `spawned` gets the placed rig; `failed` a reason for the player. Log lines "Network: own truck: ...".
        /// </summary>
        public void SpawnOwnTruck(EtsTruckIdentity id, Action<EtsTruckRig> spawned, Action<string> failed)
        {
            if (!CanSpawnTruck(out string reason))
            {
                Debug.Log("[SomniumEuroTruck] Network: own truck: spawn of preview " + id.Preview + " refused - " + reason);
                failed?.Invoke(reason);
                return;
            }

            int serial = ++_spawnSerial;
            _spawnFailed = failed;
            _spawnActive = true;
            Coroutine routine = StartCoroutine(SpawnRoutine(serial, id, spawned, failed));

            // A spawn that ended inside StartCoroutine (it failed before its first yield) has cleared _spawnActive already.
            _spawning = _spawnActive && _spawnSerial == serial ? routine : null;
        }

        /// <summary>
        /// A preview truck's body box before it is built, for choosing where to build it: the 18 previews are 2.5-2.6 m
        /// wide ([net §2.6], EtsArrival.LateralStep's note); 8 m long covers a 6x4 tractor. The built truck's own box
        /// (EtsTruckController.BodyBox) is checked again after the build.
        /// </summary>
        private static readonly Vector3 EstimatedBoxSize = new Vector3(2.6f, 3f, 8f);
        private static readonly Vector3 EstimatedBoxCentre = new Vector3(0f, 2.1f, 0f);

        private IEnumerator SpawnRoutine(int serial, EtsTruckIdentity id, Action<EtsTruckRig> spawned, Action<string> failed)
        {
            float began = Time.realtimeSinceStartup;
            EtsSession session = EtsSession.Instance;
            EtsMapViewer viewer = EtsMapViewer.Current;
            string stem = StemOf(id);
            bool ended = false;

            // try/finally (no catch: C# allows no yield inside a try with a catch): whatever ends this routine - its own
            // end, a failure, an exception thrown out of it (Unity logs it and drops the coroutine) - leaves no "on its
            // way" state and no half-built truck standing behind (checker, Phase 2: a despawn while the built truck was
            // being placed left its holder in the world, since _building was cleared once the build was done).
            try
            {
                // One truck per player ([net §2.6]): the old one goes first, and a frame passes, so its box has left the
                // physics scene (Destroy takes effect at the end of the frame) and its stand is free for the new one.
                if (EtsLocalTruck.Own != null || _ownHolder != null)
                {
                    RemoveOwn("replaced by " + stem);
                    yield return null;
                }

                string source = YardStands(session, viewer, _stands);
                EtsArrival.Stand fallback = FallbackStand(session, viewer);

                // The rotation start: the player's slot in Phase 3 (two players spawning at once aim at different stands).
                // Before slots exist (Phase 2), the Fusion id - 1: 0 for the room's first player, so a player alone in a new
                // instance gets the first stand (the garage); a later joiner starts further along the yard's stands.
                int start = MySlot >= 0 ? MySlot : Math.Max(0, LocalId - 1);
                EtsArrival.SpawnOrder(_stands, start, fallback, _order);
                Debug.Log("[SomniumEuroTruck] Network: own truck: building " + stem + " (preview " + id.Preview + ", catalogue hash " + (id.CatalogueHash & 0xFF) +
                          ", paint " + (id.PaintRgb < 0 ? "the save's" : id.PaintRgb.ToString("x6")) + "); " + _stands.Count + " yard stand(s) from " + source +
                          ", " + (_order.Count - _stands.Count) + " lateral places; this player starts at stand " + (_stands.Count > 0 ? start % _stands.Count : 0) + ".");

                // Where to build it: the first free place for a preview truck's size, so the finished rig appears there.
                int chosen = ChooseStand(null, EstimatedBoxSize, EstimatedBoxCentre, _order, out string blocked);

                if (chosen < 0)
                {
                    ended = true;
                    EndSpawn(failed, "no free place for " + stem + " at the yard (" + _order.Count + " tried; " + blocked + ").", "There is no free place for your truck at the yard."); // not in the locale
                    yield break;
                }

                var holder = new GameObject("Ets Own Truck");
                holder.transform.SetPositionAndRotation(_order[chosen].Position, Quaternion.Euler(0f, _order[chosen].Yaw, 0f));

                // Kept until the truck is the player's (below): a cancel while it is built OR placed destroys it.
                _building = holder;

                // The factory, drained here so a throw inside it ends this spawn with a reason instead of a stuck
                // "on its way" (C# allows no yield inside a try with a catch, hence the split).
                EtsTruckRig rig = null;
                string failure = null;
                IEnumerator build = EtsTruckFactory.Build(id, holder.transform, false, r => rig = r, f => failure = f);

                while (true)
                {
                    bool more;
                    object current = null;

                    try
                    {
                        more = build.MoveNext();

                        if (more)
                        {
                            current = build.Current;
                        }
                    }
                    catch (Exception e)
                    {
                        failure = "the truck factory threw " + e.GetType().Name + ": " + e.Message;
                        Debug.LogException(e);
                        more = false;
                    }

                    if (!more)
                    {
                        break;
                    }

                    yield return current;
                }

                if (rig == null || rig.Controller == null)
                {
                    ended = true;
                    EndSpawn(failed, stem + " could not be built: " + (failure ?? "no reason given") + ".", "Your truck could not be built."); // not in the locale
                    yield break;
                }

                float built = Time.realtimeSinceStartup - began;

                // The build took a second or two: a car or a player may stand there now. Checked again with the truck's own box.
                BoxCollider box = rig.Controller.BodyBox;
                int again = ChooseStand(rig.transform, box != null ? box.size : EstimatedBoxSize, box != null ? box.center : EstimatedBoxCentre, _order, out blocked);

                if (again < 0)
                {
                    ended = true;
                    EndSpawn(failed, "no free place for " + stem + " at the yard once built (" + _order.Count + " tried; " + blocked + ").", "There is no free place for your truck at the yard."); // not in the locale
                    yield break;
                }

                EtsArrival.Stand stand = _order[again];

                if (again != chosen)
                {
                    rig.transform.SetPositionAndRotation(stand.Position, Quaternion.Euler(0f, stand.Yaw, 0f));
                }

                bool placed = false;
                IEnumerator place = rig.PlaceWhenGroundExists(stand.Position, stand.Yaw, true, SpawnMapWait, ok => placed = ok);

                while (place.MoveNext())
                {
                    yield return place.Current;
                }

                if (!placed || rig == null || rig.Controller == null)
                {
                    ended = true;
                    EndSpawn(failed, stem + " found no ground at its stand " + stand.Position.ToString("0.0") + " within 30 s.", "Your truck found no ground at the yard."); // not in the locale
                    yield break;
                }

                ended = true;
                _truckSerial = (_truckSerial % 255) + 1;
                _building = null;
                _ownHolder = holder;
                _spawning = null;
                _spawnActive = false;
                _spawnFailed = null;
                EtsLocalTruck.SetOwn(rig);
                Truck = rig.Controller;
                Debug.Log("[SomniumEuroTruck] Network: own truck: " + stem + " stands at " + (stand.Authored ? "yard stand " + again + " (type " + stand.Type + ")" : "lateral step " + stand.Step + " beside the first stand") +
                          (again != chosen ? " (built at place " + chosen + ", which was taken meanwhile)" : "") + ", Unity " + rig.transform.position.ToString("0.00") + " facing " + stand.Yaw.ToString("0") +
                          " degrees; built in " + built.ToString("0.0") + " s (" + EtsTruckFactory.LastPace + "), placed after " + (Time.realtimeSinceStartup - began).ToString("0.0") + " s; truck serial " + _truckSerial + ".");
                spawned?.Invoke(rig);
            }
            finally
            {
                // A cancel (DespawnOwnTruck) moved _spawnSerial on and cleaned up itself; a newer spawn's state is not ours.
                if (_spawnSerial == serial)
                {
                    if (_building != null)
                    {
                        Destroy(_building);
                        _building = null;
                    }

                    _spawning = null;
                    _spawnActive = false;

                    if (!ended)
                    {
                        Action<string> told = _spawnFailed;
                        _spawnFailed = null;
                        Debug.LogWarning("[SomniumEuroTruck] Network: own truck: the spawn of " + stem + " ended by an exception (logged above); nothing was left standing.");
                        told?.Invoke("Your truck could not be built."); // not in the locale
                    }
                }
            }
        }

        /// <summary>A spawn that failed: the half-built truck goes (its holder), the state is cleared, the player is told.</summary>
        private void EndSpawn(Action<string> failed, string logged, string told)
        {
            if (_building != null)
            {
                Destroy(_building);
                _building = null;
            }

            _spawning = null;
            _spawnActive = false;
            _spawnFailed = null;
            Debug.LogWarning("[SomniumEuroTruck] Network: own truck: " + logged);
            failed?.Invoke(told);
        }

        /// <summary>
        /// The first place of the order where a truck's body box (size and centre in its own frame, + OccupancyMargin
        /// across and along) holds no vehicle and no player - `own`'s colliders aside, the rig itself standing there -
        /// and a lateral step also no building (Environment) and ground at the stand's height; -1 when none. One
        /// OverlapBox and at most one ray per place, twice per spawn.
        /// </summary>
        private int ChooseStand(Transform own, Vector3 size, Vector3 centre, List<EtsArrival.Stand> order, out string blocked)
        {
            blocked = "";
            var half = new Vector3(size.x * 0.5f + OccupancyMargin, size.y * 0.5f, size.z * 0.5f + OccupancyMargin);
            int occupied = 0, noGround = 0;

            for (int i = 0; i < order.Count; i++)
            {
                EtsArrival.Stand stand = order[i];
                Quaternion rotation = Quaternion.Euler(0f, stand.Yaw, 0f);
                int mask = StandBlockers | (stand.Authored ? 0 : 1 << EtsLayers.Environment);
                int n = Physics.OverlapBoxNonAlloc(stand.Position + rotation * centre, half, _overlap, rotation, mask, QueryTriggerInteraction.Ignore);
                bool free = true;

                for (int k = 0; k < n && free; k++)
                {
                    free = _overlap[k] == null || (own != null && _overlap[k].transform.IsChildOf(own));
                }

                if (!free)
                {
                    occupied++;
                    continue;
                }

                if (!stand.Authored && !GroundAt(stand.Position))
                {
                    noGround++;
                    continue;
                }

                if (occupied + noGround > 0)
                {
                    Debug.Log("[SomniumEuroTruck] Network: own truck: " + occupied + " place(s) taken and " + noGround + " without yard ground before place " + i + ".");
                }

                return i;
            }

            blocked = occupied + " taken, " + noGround + " without yard ground";
            return -1;
        }

        /// <summary>A lateral step stands on the drive surface (not a building) within LateralGroundRange of its anchor's height.</summary>
        private static bool GroundAt(Vector3 position)
        {
            if (!Physics.Raycast(position + Vector3.up * 60f, Vector3.down, out RaycastHit hit, 120f, EtsLayers.GroundMask | (1 << EtsLayers.Environment), QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            return ((1 << hit.collider.gameObject.layer) & EtsLayers.GroundMask) != 0 && Mathf.Abs(hit.point.y - position.y) <= LateralGroundRange;
        }

        /// <summary>
        /// The session yard's stands in Unity coordinates (the map viewer's centre is the chosen origin from Loading
        /// on), in the catalogue's order; what they came from, for the log.
        /// </summary>
        private static string YardStands(EtsSession session, EtsMapViewer viewer, List<EtsArrival.Stand> into)
        {
            into.Clear();

            if (session == null || !session.HasChoice || viewer == null)
            {
                return "nowhere (no headquarters chosen)";
            }

            EtsHqChoice choice = session.Choice;

            // The master (and an Offline player) built D's catalogue to choose from: its row is the yard.
            if (EtsHqCatalog.TryGetCity(choice.City, out EtsHqCity city) && city.Spots != null && city.Spots.Length > 0 && city.Yard.YardPrefabUid == choice.YardPrefabUid)
            {
                EtsArrival.StandsFromCatalogue(city, viewer.CenterGame, into);
                return "the HQ catalogue (" + city.Name + ")";
            }

            // A joiner never needs the catalogue (PLAN §3.6): the same stands from the map the viewer has read round
            // the yard, with D's own SpotsOf and the catalogue's radius, order and spacing (probe 2003).
            if (viewer.Map == null)
            {
                return "nowhere (the map is not open)";
            }

            EtsArrival.StandsFromMap(viewer.Map, choice.OriginGame, viewer.CenterGame, into);
            return "the map round the yard (no HQ catalogue on this client)";
        }

        /// <summary>
        /// Where lateral steps go when the yard has no stand at all: the first stand's place, which is the session's
        /// raw arrival point 6 m back along its yaw (D's catalogue: the arrival is the first stand + 6 m to its right,
        /// facing back at it; EtsHqCatalogBuilder.ArrivalSideStep), turned the stand's way (the arrival yaw + 90).
        /// </summary>
        private static EtsArrival.Stand FallbackStand(EtsSession session, EtsMapViewer viewer)
        {
            if (session == null || !session.HasChoice || viewer == null)
            {
                return new EtsArrival.Stand { Position = Vector3.zero, Yaw = 0f };
            }

            EtsHqChoice choice = session.Choice;
            Vector3 arrival = EtsArrival.GameToUnity(choice.ArrivalGame, viewer.CenterGame);
            return new EtsArrival.Stand
            {
                Position = arrival + EtsArrival.Forward(choice.ArrivalYaw) * EtsHqCatalogBuilder.ArrivalSideStep,
                Yaw = choice.ArrivalYaw + 90f
            };
        }

        private static string StemOf(EtsTruckIdentity id)
        {
            EtsTruckCatalog catalog = EtsTruckCatalog.Current;
            return catalog != null && id.Preview >= 0 && id.Preview < catalog.Trucks.Count ? catalog.Trucks[id.Preview].Stem : "preview truck " + id.Preview;
        }

        /// <summary>
        /// Takes the own truck away (the tablet's "return truck", a replacement, or a spawn cancelled mid-way). A job
        /// running on it is abandoned first (PLAN §3.5: the tablet asks for the confirmation), and its trailer goes
        /// with it: in the new flow the trailer is the owner's to remove ([net §2.6]; it stands under the same holder,
        /// EtsJobs builds it beside the truck). EtsLocalTruck.SetOwn(null) comes before the destroy, so the listeners
        /// let go while the rig still stands - B's seat lets a driver out beside it (EtsTruckSeat.OnOwnChanged).
        /// </summary>
        public void DespawnOwnTruck()
        {
            if (_spawnActive)
            {
                // The serial moves on first, so the stopped routine's finally (if Unity runs it) leaves this state alone.
                _spawnSerial++;

                if (_spawning != null)
                {
                    StopCoroutine(_spawning);
                }

                _spawning = null;
                _spawnActive = false;

                if (_building != null)
                {
                    Destroy(_building);
                    _building = null;
                }

                Action<string> failed = _spawnFailed;
                _spawnFailed = null;
                Debug.Log("[SomniumEuroTruck] Network: own truck: the spawn in progress was cancelled.");
                failed?.Invoke("Cancelled."); // not in the locale
            }

            RemoveOwn("despawned");
        }

        private void RemoveOwn(string why)
        {
            EtsTruckRig own = EtsLocalTruck.Own;
            GameObject holder = _ownHolder;
            _ownHolder = null;

            if (own == null && holder == null)
            {
                return;
            }

            EtsJobs jobs = Jobs != null ? Jobs : EtsJobs.Instance;
            bool hadJob = jobs != null && jobs.HasJob;
            bool hadTrailer = jobs != null && jobs.Trailer != null;

            if (hadJob || hadTrailer)
            {
                jobs.LetGoOfTrailer();
            }

            string stem = own != null ? own.Stem : "?";
            EtsLocalTruck.SetOwn(null);
            Truck = null;

            if (holder != null)
            {
                Destroy(holder);
            }
            else if (own != null)
            {
                Destroy(own.gameObject);
            }

            Debug.Log("[SomniumEuroTruck] Network: own truck: " + stem + " " + why + (hadJob ? "; its job was abandoned" : "") + (hadTrailer ? "; its trailer went with it" : "") + ".");
        }

        /// <summary>The job summary others see ([net §2.6]; ints 28-31 of the own slot). G calls it on a take.</summary>
        public void SetJob(ulong cargoToken, ulong destinationCityToken)
        {
            _jobCargo = cargoToken;
            _jobCity = destinationCityToken;
            _hasJob = true;
        }

        /// <summary>G calls it on a delivery or an abandon.</summary>
        public void ClearJob()
        {
            _jobCargo = 0UL;
            _jobCity = 0UL;
            _hasJob = false;
        }

        // ---------------------------------------------------------------- passengers ([net §2.7], PLAN §5.5 "Passenger")

        private Action _seatGranted;
        private Action<string> _seatRefused;

        /// <summary>After a grant, B's own rider record (EtsRiders.Publish) replaces the request's within this; else it is dropped.</summary>
        private const double GrantedHandOver = 5.0;

        /// <summary>
        /// Asks the owner of slot ownerSlot for passenger seat `seat` (1-4) of their truck ([net §2.7], PLAN §5.5 step 1-2): this
        /// client's rider record says "requesting seat s of slot k, truck serial n" (with the seat's own offset, so the
        /// granted record already puts the body in the seat), the owner's client writes its seat table within 0.25 s, and
        /// `granted` runs when this player is named there; `refused` with the reason when someone else is ("The seat is
        /// taken"), after 3 s ("{owner} did not answer"), or when the truck goes. One request at a time. EtsTruckDoor (a
        /// click on a puppet) and the tablet's Convoys "Ride along" come here.
        /// </summary>
        public void RequestSeat(int ownerSlot, int seat, Action granted, Action<string> refused)
        {
            string reason = SeatRefusal(ownerSlot, seat, out int ownerId, out int serial);

            if (reason != null)
            {
                Debug.Log("[SomniumEuroTruck] Network: seat " + seat + " of slot " + ownerSlot + "'s truck: refused at once - " + reason);
                refused?.Invoke(reason);
                return;
            }

            if (_request.Active)
            {
                Action<string> superseded = _seatRefused;
                _request.End();
                _seatGranted = null;
                _seatRefused = null;
                superseded?.Invoke("Another request took its place."); // not in the locale
            }

            double now = Time.unscaledTimeAsDouble;
            _seatGranted = granted;
            _seatRefused = refused;
            _request.Begin(ownerSlot, ownerId, seat, serial, now);

            EtsNetVehicle puppet = _puppets[ownerSlot];
            EtsTruckController cab = puppet != null && puppet.Rig != null ? puppet.Rig.Controller : null;
            _localRider = new EtsRider
            {
                Kind = EtsTargetKind.Tractor,
                Index = ownerSlot,
                Serial = serial,
                Seat = seat,
                Requesting = true,
                Epoch = (_localRider.Epoch + 1) & 0xFF,
                Xr = EtsVr.Present,
                Root = cab != null ? cab.SeatLocalFor(seat) : Vector3.zero,
                Yaw = 0f
            };

            _hasLocalRider = true;
            _riderFromRequest = true;
            _grantedAt = -1.0;
            _riderStampMs = StampMs();
            _forceWrite = true;
            Debug.Log("[SomniumEuroTruck] Network: seat " + seat + " of " + NameOf(ownerId) + "'s truck (slot " + ownerSlot + ", serial " + serial + "): asked.");
        }

        /// <summary>Why a seat request cannot even be sent (null: it can). The words are not in the game's locale.</summary>
        private string SeatRefusal(int ownerSlot, int seat, out int ownerId, out int serial)
        {
            ownerId = -1;
            serial = 0;

            if (!Online)
            {
                return "Riding along needs other players: you are playing alone.";
            }

            if (!_claim.Holding)
            {
                return "Still joining the session; try again in a moment.";
            }

            if (seat < 1 || seat > EtsSeatTable.Seats)
            {
                return "There is no such seat.";
            }

            if (ownerSlot < 0 || ownerSlot >= _slotCount || !_counts[ownerSlot])
            {
                return "That truck is not in this session any more.";
            }

            int[] slot = _slots[ownerSlot];
            ownerId = EtsPlayerWire.OwnerOf(slot);

            if (!EtsPlayerWire.UnpackTruck(slot, out _, out serial))
            {
                return NameOf(ownerId) + " has no truck out.";
            }

            EtsPlayerWire.UnpackSeats(slot, _occupants);
            int who = _occupants[seat - 1];
            return who >= 0 && who != LocalId ? "The seat is taken" : null; // PLAN §5.5 / [net §2.7]
        }

        /// <summary>A pending seat request against the owner's slot as read this frame ([net §2.7] step 3).</summary>
        private void AnswerSeatRequest(double now, int localId)
        {
            if (!_request.Active)
            {
                return;
            }

            int k = _request.OwnerSlot;
            int[] owner = k >= 0 && k < _slotCount && _counts[k] ? _slots[k] : null;
            EtsSeatRequest.Answer answer = _request.Step(owner, localId, now);

            if (answer == EtsSeatRequest.Answer.Waiting || answer == EtsSeatRequest.Answer.None)
            {
                return;
            }

            Action granted = _seatGranted;
            Action<string> refused = _seatRefused;
            _seatGranted = null;
            _seatRefused = null;
            string who = NameOf(_request.OwnerId);
            int ms = (int)Math.Round((now - _request.Since) * 1000.0);

            if (answer == EtsSeatRequest.Answer.Granted)
            {
                // The record stays (target, seat, the seat's offset), no longer "requesting": the owner's validation keeps the
                // seat while it names the truck, and watchers draw the body in the seat until B's own record replaces it.
                _localRider.Requesting = false;
                _grantedAt = now;
                _forceWrite = true;
                Debug.Log("[SomniumEuroTruck] Network: seat " + _request.Seat + " of " + who + "'s truck: granted in " + ms + " ms.");
                granted?.Invoke();
                return;
            }

            string reason;
            string logged;

            switch (answer)
            {
                case EtsSeatRequest.Answer.Taken:
                    EtsPlayerWire.UnpackSeats(owner, _occupants);
                    reason = "The seat is taken";
                    logged = "taken by " + NameOf(_occupants[Mathf.Clamp(_request.Seat - 1, 0, EtsSeatTable.Seats - 1)]);
                    break;
                case EtsSeatRequest.Answer.NoAnswer:
                    reason = who + " did not answer";
                    logged = "no answer in " + ms + " ms";
                    break;
                default:
                    reason = who + "'s truck is gone";
                    logged = "the truck is gone (owner left, despawned or a new truck)";
                    break;
            }

            DropRequestRecord();
            Debug.Log("[SomniumEuroTruck] Network: seat " + _request.Seat + " of " + who + "'s truck: " + logged + ".");
            refused?.Invoke(reason); // not in the locale ([net §2.7])
        }

        /// <summary>
        /// A granted request's record that B never took over (the player could not be seated after all): dropped after
        /// GrantedHandOver, or the owner would keep the seat for a player who is not in it.
        /// </summary>
        private void ReleaseStaleRequestRecord()
        {
            if (!_riderFromRequest || _grantedAt < 0.0 || Time.unscaledTimeAsDouble - _grantedAt < GrantedHandOver)
            {
                return;
            }

            EtsTruckRig occupied = EtsLocalTruck.Occupied;
            EtsNetVehicle puppet = _localRider.Index >= 0 && _localRider.Index < MaxSlots ? _puppets[_localRider.Index] : null;

            if (occupied != null && puppet != null && occupied == puppet.Rig)
            {
                // Seated there after all; B's record simply has not come: keep it (it is the same place).
                _grantedAt = -1.0;
                return;
            }

            Debug.Log("[SomniumEuroTruck] Network: the granted seat was not taken within " + GrantedHandOver + " s; its record is dropped.");
            DropRequestRecord();
        }

        private void DropRequestRecord()
        {
            if (_riderFromRequest)
            {
                _hasLocalRider = false;
                _riderFromRequest = false;
                _grantedAt = -1.0;
                _forceWrite = true;
            }
        }

        /// <summary>
        /// Gets out of a passenger seat as far as the network is concerned: a pending request is withdrawn (its `refused`
        /// runs with "Cancelled."), and a passenger record is cleared, so the owner frees the seat within 2 s.
        /// </summary>
        public void LeaveSeat()
        {
            if (_request.Active)
            {
                Action<string> refused = _seatRefused;
                _request.End();
                _seatGranted = null;
                _seatRefused = null;
                DropRequestRecord();
                refused?.Invoke("Cancelled."); // not in the locale
            }

            if (_hasLocalRider && _localRider.Seat >= 1 && _localRider.Seat <= EtsSeatTable.Seats)
            {
                ClearLocalRider();
            }
        }

        // ---------------------------------------------------------------- riders (B's contract, PLAN §7.A)

        /// <summary>
        /// B: this player's rider record (ints 33-35, 43), in the target's frame as drawn on this client, written with the
        /// next slot write. Its time is now; the record replaces a seat request's.
        /// </summary>
        public void SetLocalRider(in EtsRider rider)
        {
            _localRider = rider;
            _hasLocalRider = rider.Kind != EtsTargetKind.None;
            _riderFromRequest = false;
            _grantedAt = -1.0;
            _riderStampMs = StampMs();
        }

        /// <summary>B: riding nothing (ints 33-41 and 43 zero at the next write).</summary>
        public void ClearLocalRider()
        {
            if (_request.Active)
            {
                // B's publishing is held during a request (EtsRiders.HoldPublishing); a clear now is not about the request.
                return;
            }

            _hasLocalRider = false;
            _riderFromRequest = false;
            _grantedAt = -1.0;
        }

        /// <summary>B: every other player's rider record, sampled on its own timeline this frame (a requesting one left out).</summary>
        public IReadOnlyList<EtsRemoteRider> RemoteRiders => _remoteRiders;

        /// <summary>
        /// The players (the Convoys page and the top bar, PLAN §7.A): this player first, then every other player in the room
        /// with a slot, in slot order, refreshed at 4 Hz. Empty on the old flow.
        /// </summary>
        public IReadOnlyList<EtsPlayerView> Players => _players;

        /// <summary>
        /// The other players' trucks in the room now (Listed slots with "truck present"), for EtsSession's LoadPlan
        /// ([net §2.9]). Listed, not counted: a joiner starting to load is not in the session epoch's world yet.
        /// </summary>
        public void RemoteTruckIdentities(List<EtsTruckIdentity> into)
        {
            if (into == null)
            {
                return;
            }

            into.Clear();

            for (int k = 0; k < _slotCount; k++)
            {
                if (_listed[k] && EtsPlayerWire.UnpackTruck(_slots[k], out EtsTruckIdentity id, out _))
                {
                    into.Add(id);
                }
            }
        }

        private readonly List<EtsVehicleView> _vehicles = new List<EtsVehicleView>(MaxSlots + 1);
        private int _vehiclesFrame = -1;

        /// <summary>
        /// Every player's truck this client draws (EtsTraffic.Gather, PLAN §7.A): the own truck (its Rigidbody's velocity) and
        /// each puppet that has been drawn (its drawn velocity, its rebuilt trailer). One list, refilled once a frame.
        /// </summary>
        public IReadOnlyList<EtsVehicleView> AllVehicles
        {
            get
            {
                if (_vehiclesFrame == Time.frameCount)
                {
                    return _vehicles;
                }

                _vehiclesFrame = Time.frameCount;
                _vehicles.Clear();
                EtsTruckController truck = EtsFeatures.NewFlow ? (EtsLocalTruck.Own != null ? EtsLocalTruck.Own.Controller : null) : Truck;

                if (truck != null && truck.Placed)
                {
                    Rigidbody body = truck.Body;
                    _vehicles.Add(new EtsVehicleView
                    {
                        Tractor = truck.transform,
                        Velocity = body != null && !body.isKinematic ? body.linearVelocity : Vector3.zero,
                        Trailer = EtsFeatures.NewFlow ? OwnTrailer(EtsLocalTruck.Own) : truck.Trailer,
                        Remote = false,
                        Box = truck.BodyBox
                    });
                }

                for (int k = 0; k < MaxSlots; k++)
                {
                    EtsNetVehicle puppet = _puppets[k];

                    if (puppet == null || !puppet.Drawn || puppet.MissingSince >= 0f)
                    {
                        continue;
                    }

                    _vehicles.Add(new EtsVehicleView
                    {
                        Tractor = puppet.Frame,
                        Velocity = puppet.DrawnVelocity,
                        Trailer = puppet.Trailer,
                        Remote = true,
                        Box = puppet.Rig != null && puppet.Rig.Controller != null ? puppet.Rig.Controller.BodyBox : null
                    });
                }

                return _vehicles;
            }
        }

        /// <summary>
        /// A vehicle's drawn transform (B's rider frames, PLAN §7.A): a tractor or trailer by its owner's slot and truck serial
        /// (this client's own at index MySlot, or a puppet; serial 0 = whatever truck that slot has now,
        /// EtsSlotRules.SerialMatches), or a traffic car by global slot and 4-bit serial (EtsTraffic.TryGetCarFrame).
        /// </summary>
        public bool TryGetVehicleFrame(EtsTargetKind kind, int index, int serial, out Transform frame)
        {
            frame = null;

            switch (kind)
            {
                case EtsTargetKind.Tractor:
                case EtsTargetKind.Trailer:
                {
                    bool trailer = kind == EtsTargetKind.Trailer;

                    if (index >= 0 && index == MySlot)
                    {
                        EtsTruckRig own = EtsLocalTruck.Own;

                        if (own != null && EtsSlotRules.SerialMatches(serial, OwnTruckSerial))
                        {
                            EtsTrailer t = trailer ? OwnTrailer(own) : null;
                            frame = trailer ? (t != null ? t.transform : null) : own.transform;
                        }

                        break;
                    }

                    EtsNetVehicle puppet = index >= 0 && index < MaxSlots ? _puppets[index] : null;

                    if (puppet != null && puppet.Drawn && EtsSlotRules.SerialMatches(serial, puppet.TruckSerial))
                    {
                        frame = trailer ? (puppet.Trailer != null ? puppet.Trailer.transform : null) : puppet.Frame;
                    }

                    break;
                }

                case EtsTargetKind.TrafficCar:
                {
                    if (_traffic == null)
                    {
                        _traffic = GetComponent<EtsTraffic>();
                    }

                    if (_traffic != null && _traffic.TryGetCarFrame(index, serial, out Transform body, out _))
                    {
                        frame = body;
                    }

                    break;
                }
            }

            return frame != null;
        }

        /// <summary>
        /// Another player's drawn truck by their slot (EtsPlayerView.Slot), null while it is not built here: what the tablet's
        /// Convoys "Ride along" seats the player in once RequestSeat is granted (EtsMenu.EnterTruck(rig, 1), as EtsTruckDoor does).
        /// </summary>
        public EtsTruckRig RigOfSlot(int slot)
        {
            EtsNetVehicle puppet = slot >= 0 && slot < MaxSlots ? _puppets[slot] : null;
            return puppet != null ? puppet.Rig : null;
        }

        /// <summary>
        /// B: the truck serial a rider record on this rig must carry (int 5 bits 8-15 of its owner's slot): the own truck's
        /// OwnTruckSerial, a puppet's the serial it was built for; 0 (any) when the rig is neither.
        /// </summary>
        public int TruckSerialOf(EtsTruckRig rig)
        {
            if (rig == null)
            {
                return 0;
            }

            if (rig == EtsLocalTruck.Own)
            {
                return OwnTruckSerial;
            }

            for (int k = 0; k < MaxSlots; k++)
            {
                if (_puppets[k] != null && _puppets[k].Rig == rig)
                {
                    return _puppets[k].TruckSerial;
                }
            }

            return 0;
        }

        /// <summary>
        /// B: what a walker stepped on, as a rider target (a ground ray's hit, not every frame): the own truck or trailer
        /// (index MySlot), another player's (their slot), or an AI car (EtsTraffic.TryIdentifyCar). False for anything else.
        /// </summary>
        public bool TryIdentifyVehicle(Transform hit, out EtsTargetKind kind, out int index, out int serial)
        {
            kind = EtsTargetKind.None;
            index = -1;
            serial = 0;

            if (hit == null)
            {
                return false;
            }

            EtsTruckRig rig = hit.GetComponentInParent<EtsTruckRig>();
            EtsTrailer trailer = hit.GetComponentInParent<EtsTrailer>();
            EtsTruckRig own = EtsLocalTruck.Own;

            if (rig != null && rig == own && MySlot >= 0)
            {
                kind = EtsTargetKind.Tractor;
                index = MySlot;
                serial = OwnTruckSerial;
                return true;
            }

            if (trailer != null && own != null && trailer == OwnTrailer(own) && MySlot >= 0)
            {
                kind = EtsTargetKind.Trailer;
                index = MySlot;
                serial = OwnTruckSerial;
                return true;
            }

            for (int k = 0; k < MaxSlots; k++)
            {
                EtsNetVehicle puppet = _puppets[k];

                if (puppet == null)
                {
                    continue;
                }

                if (rig != null && puppet.Rig == rig)
                {
                    kind = EtsTargetKind.Tractor;
                    index = k;
                    serial = puppet.TruckSerial;
                    return true;
                }

                if (trailer != null && puppet.Trailer == trailer)
                {
                    kind = EtsTargetKind.Trailer;
                    index = k;
                    serial = puppet.TruckSerial;
                    return true;
                }
            }

            Rigidbody rb = hit.GetComponentInParent<Rigidbody>();

            if (_traffic == null)
            {
                _traffic = GetComponent<EtsTraffic>();
            }

            if (rb != null && _traffic != null && _traffic.TryIdentifyCar(rb, out int car, out int carSerial))
            {
                kind = EtsTargetKind.TrafficCar;
                index = car;
                serial = carSerial;
                return true;
            }

            return false;
        }
    }
}
