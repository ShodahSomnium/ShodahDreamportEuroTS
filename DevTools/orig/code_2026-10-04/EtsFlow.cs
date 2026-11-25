using System;
using System.Collections;
using System.Collections.Generic;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.LocalStorage;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>What the flow puts on screen in one phase (EtsFlowRules.For). A plain value, so the harness checks the table.</summary>
    public struct EtsFlowView
    {
        /// <summary>The HQ void (EtsHqStage) is up: before the arrival, the void stays, map or not.</summary>
        public bool Stage;

        /// <summary>The map is clickable (the master choosing, or a player alone offline).</summary>
        public bool Interactive;

        /// <summary>The HQ map is shown (hidden under the loading screen; the void stays).</summary>
        public bool Map;

        /// <summary>The player is put back into the void when they leave it (never during Loading: the arrival teleport is A's).</summary>
        public bool KeepPlayer;

        /// <summary>The session's loading screen ("Monday 10:00", EtsSession.LoadProgress).</summary>
        public bool SessionLoading;

        /// <summary>The stage's banner (PLAN §3.7 item 3); not in the locale.</summary>
        public string Banner;

        /// <summary>EtsPostFx.PhaseWeight: 0 while a full-view screen of the game is up (PLAN C20), 1 in the world.</summary>
        public float PostWeight;

        /// <summary>The HUD master badge (PLAN §3.7 item 2): in the world, for the master.</summary>
        public bool Badge;

        /// <summary>The scene's desktop status line is hidden (it would lie over the full-view screens).</summary>
        public bool HudHidden;

        /// <summary>The visitor has no game (EtsFlow.NoGame): the void with the no-game notice, whatever the phase (Phase 3, D3).</summary>
        public bool Notice;

        public override string ToString()
        {
            return (Notice ? "no-game notice in the void" : Stage ? (Interactive ? "HQ map (choosing)" : Map ? "HQ map (watching)" : "void") : "world") + (SessionLoading ? " + loading screen" : "") +
                   (KeepPlayer ? ", player kept in the void" : "") + ", post weight " + PostWeight.ToString("0") + (Badge ? ", master badge" : "") +
                   (string.IsNullOrEmpty(Banner) ? "" : ", banner \"" + Banner + "\"");
        }
    }

    /// <summary>
    /// The phase table of PLAN.md §5.2 as a pure rule (owner D): which of the HQ stage, its banner, the session's
    /// loading screen, the post-processing weight and the master badge each EtsSession phase shows. A job's own loading
    /// screen (EtsJobs.JobStarting, InWorld only) turns the weight down and hides the badge and the status line while it
    /// is up. Probe 2043 walks every phase through it.
    /// </summary>
    public static class EtsFlowRules
    {
        public static EtsFlowView For(EtsPhase phase, bool isMaster, string masterName, bool jobLoading)
        {
            return For(phase, isMaster, masterName, jobLoading, false);
        }

        /// <summary>
        /// jobLoading: a job's loading screen is up - the player's own (EtsJobs.JobStarting) or a passenger's "Riding to"
        /// one (PLAN §3.5). noGame: this visitor has no ETS2 install (EtsFlow.NoGame): whatever the phase, the void with
        /// the notice, kept there - no map can be drawn, no loading screen built and no yard arrived at, and the black
        /// void is the one place in this world that is not a fall through empty space.
        /// </summary>
        public static EtsFlowView For(EtsPhase phase, bool isMaster, string masterName, bool jobLoading, bool noGame)
        {
            if (noGame)
            {
                return new EtsFlowView { Stage = true, Map = false, KeepPlayer = true, Notice = true, Banner = "", PostWeight = 0f, HudHidden = true };
            }

            var v = new EtsFlowView { PostWeight = 0f, HudHidden = true, Map = true, KeepPlayer = true, Stage = true };

            switch (phase)
            {
                case EtsPhase.Connecting:
                    v.Banner = EtsHqStageContent.ConnectingBanner;
                    break;
                case EtsPhase.Offline:
                    v.Interactive = true;
                    v.Banner = EtsHqStageContent.OfflineBanner;
                    break;
                case EtsPhase.ChoosingHq:
                    // Only the master is ever in ChoosingHq (EtsSessionLogic); a non-master there watches.
                    v.Interactive = isMaster;
                    v.Banner = isMaster ? EtsHqStageContent.MasterBanner : EtsHqStageContent.WaitingBanner(masterName);
                    break;
                case EtsPhase.WaitingForMaster:
                    v.Banner = EtsHqStageContent.WaitingBanner(masterName);
                    break;
                case EtsPhase.Loading:
                case EtsPhase.Relocating:
                    v.Map = false;
                    v.KeepPlayer = false;
                    v.SessionLoading = true;
                    v.Banner = "";
                    break;
                default:
                    v.Stage = false;
                    v.Map = false;
                    v.KeepPlayer = false;
                    v.Banner = "";
                    v.PostWeight = jobLoading ? 0f : 1f;
                    v.HudHidden = jobLoading;
                    v.Badge = isMaster && !jobLoading;
                    break;
            }

            return v;
        }

        /// <summary>
        /// Whether EtsFlow.Warm's heavy steps (the truck catalogue, ~25 ms, and the studio, ~225 ms cold, each one frame)
        /// may run: under the loading screen or in the world, never while the HQ map is up and being pointed at.
        /// </summary>
        public static bool LateWarm(EtsPhase phase)
        {
            return phase == EtsPhase.Loading || phase == EtsPhase.Relocating || phase == EtsPhase.InWorld;
        }

        /// <summary>The job loading screen's progress: 1 - e^(-t/6), held under 0.95 until the job has started (the old front end's curve).</summary>
        public static float JobProgress(float seconds, bool started)
        {
            if (started)
            {
                return 1f;
            }

            float p = 1f - (float)Math.Exp(-Math.Max(0f, seconds) / 6f);
            return Math.Min(0.95f, p);
        }

        /// <summary>The LocalStorage key of the HQ catalogue cache: prefix, revision, map signature, language (PLAN §3.6).</summary>
        public static string CatalogueKey(string signature, string language)
        {
            return EtsHqCatalog.CacheKeyPrefix + EtsHqCatalog.CacheRevision + "_" + (signature ?? "") + "_" + (string.IsNullOrEmpty(language) ? "none" : language);
        }

        /// <summary>The delivery toast: the locale's "JOB COMPLETED" and the pay and XP in the delivery screen's "+ €n" / "+ n XP" form.</summary>
        public static string DeliveryToast(long reward, long xp)
        {
            return EtsLocale.Word("job_completed_t") + ": + " + EtsLocale.Money(reward) + "   + " + EtsLocale.Group(xp) + " " + EtsLocale.Word("xp");
        }

        // ------------------------------------------------------------------ a passenger's job loading screen (PLAN §3.5, D3)

        /// <summary>A ridden truck that moved farther than this between two 4 Hz looks was teleported by its owner (a job start, EtsJobs.Take).</summary>
        public const float RideJumpMetres = 50f;

        /// <summary>
        /// ... and faster than this, in m/s: at 4 Hz a truck at 144 km/h moves 10 m a look, and a one-second hitch at
        /// 100 km/h 28 m, while a job start moves it kilometres at once. So a hitch never reads as a jump.
        /// </summary>
        public const float RideJumpSpeed = 100f;

        /// <summary>The passenger's screen goes after this even with no ground (EtsJobs itself waits 40 s for its yard, then sets down anyway).</summary>
        public const float RideGiveUp = 90f;

        /// <summary>The depot nearest the landing spot, within this, names the "Riding to" line (a job starts on its company's own bay).</summary>
        public const float RideCompanyRadius = 300f;

        /// <summary>Not in the locale (the game has no passengers).</summary>
        public const string RidingTo = "Riding to", RidingWith = "Riding with", With = "with";

        /// <summary>Whether a ridden truck jumped: farther than RideJumpMetres and faster than RideJumpSpeed over the look's seconds.</summary>
        public static bool RideJumped(float metres, float seconds)
        {
            return metres > RideJumpMetres && metres > RideJumpSpeed * Math.Max(0f, seconds);
        }

        /// <summary>
        /// The passenger's job line in the 1.61 loading screen's form (EtsLoadingContent.JobValue): "Monday 10:00 | Riding to
        /// | Tradeaux | Hannover | with Anna", the label grey as the game's "Current job:" is. Without a depot at the landing
        /// spot: "Monday 10:00 | Riding with | Anna". The slot carries only the job's cargo and destination city, not the
        /// company where it starts, so the company is the depot nearest where the truck landed (EtsFlow.RideNames).
        /// </summary>
        public static string RideValue(int gameMinutes, string company, string city, string owner)
        {
            string day = EtsLoadingContent.Clean(EtsLocale.DayTime(gameMinutes));
            const string grey = "<color value=@@clr_txt_d@@>";
            string who = Plain(EtsNameplateRules.Clean(owner));

            if (!string.IsNullOrEmpty(company))
            {
                return day + "|" + grey + RidingTo + " |" + Plain(company) + "|" + Plain(city) + "|" + (who.Length > 0 ? With + " " + who : "");
            }

            return day + "|" + grey + RidingWith + " |" + (who.Length > 0 ? who : "the driver") + "|" + Plain(city) + "|";
        }

        /// <summary>
        /// A name as a value of the game's markup: no '|' (it would split the value, EtsLoadingContent.Clean) and no angle
        /// brackets - the line is PML and then TextMeshPro rich text, so a player called "&lt;img src=...&gt;" or
        /// "&lt;b&gt;Zed" would be read as a tag (probe 2260 saw "&lt;b&gt;" swallowed). They become round brackets.
        /// </summary>
        public static string Plain(string text)
        {
            string s = EtsLoadingContent.Clean(text);
            return s.IndexOf('<') < 0 && s.IndexOf('>') < 0 ? s : s.Replace('<', '(').Replace('>', ')');
        }

        // ------------------------------------------------------------------ a visitor without the game (D3)

        /// <summary>Not in the locale. EtsHqStageContent.NoGameBanner when no install was found at all.</summary>
        public const string NoGameOpenTitle = "Euro Truck Simulator 2 was found on this PC but could not be opened";

        public static string NoGameTitle(bool installFound)
        {
            return installFound ? NoGameOpenTitle : EtsHqStageContent.NoGameBanner;
        }

        /// <summary>
        /// What the notice says (not in the locale). The truth, not a promise: the roads, towns and trucks are read from
        /// the player's own install at run time and nothing of the game ships with the world (the project's rule), so a
        /// visitor without it can be shown who is here and what they do, but not their trucks or the map.
        /// </summary>
        public static string NoGameBody(bool installFound, bool isMaster)
        {
            string body = "This world builds its roads, towns and trucks from the copy of the game on your own PC - none of it comes with the world. " +
                          "Without it there is nothing here to drive or to see, so you are waiting in this room.\n\n" +
                          (installFound
                              ? "To join in: let Steam finish updating Euro Truck Simulator 2 (this world reads version 1.61), start it once, then come back to this world."
                              : "To join in: install the free Euro Truck Simulator 2 Demo from Steam, start it once, then come back to this world.");

            if (isMaster)
            {
                // Integrator, Phase 3: the choice is handed on (EtsSlotRules.ActingMaster), so nobody waits on a no-game master.
                body += "\n\nYou are the instance master. Choosing where everyone drives needs the game, so the first player who joins with it chooses the headquarters for everyone.";
            }

            return body;
        }

        /// <summary>What one player is doing, for the notice's list (not in the locale).</summary>
        public static string StatusWords(EtsPlayerWire.Status status)
        {
            switch (status)
            {
                case EtsPlayerWire.Status.Loading: return "loading";
                case EtsPlayerWire.Status.ChoosingHq: return "choosing the headquarters";
                case EtsPlayerWire.Status.Waiting: return "waiting for the headquarters";
                case EtsPlayerWire.Status.OnFoot: return "on foot";
                case EtsPlayerWire.Status.Driving: return "driving";
                case EtsPlayerWire.Status.Passenger: return "riding along";
                case EtsPlayerWire.Status.Standing: return "riding on a truck";
                default: return "";
            }
        }

        /// <summary>"In this world now:" and one line per other player: "Anna (instance master) - driving".</summary>
        public static string PlayersText(IReadOnlyList<EtsPlayerView> players)
        {
            var text = new System.Text.StringBuilder("In this world now:");
            int listed = 0;

            for (int i = 0; players != null && i < players.Count; i++)
            {
                EtsPlayerView p = players[i];

                if (p == null || p.IsLocal)
                {
                    continue;
                }

                listed++;
                string name = EtsNameplateRules.Clean(p.Name);
                text.Append('\n').Append(name.Length > 0 ? name : "Player " + p.PlayerId);

                if (p.IsMaster)
                {
                    text.Append(" (instance master)");
                }

                string doing = StatusWords(p.Status);

                if (doing.Length > 0)
                {
                    text.Append(" - ").Append(doing);
                }
            }

            return listed > 0 ? text.ToString() : "Nobody else is in this world yet.";
        }

        /// <summary>A cheap fingerprint of the list (ids, names, master, status), so the notice is rewritten only when it changed. No allocation.</summary>
        public static int PlayersSignature(IReadOnlyList<EtsPlayerView> players)
        {
            unchecked
            {
                int h = 17;

                for (int i = 0; players != null && i < players.Count; i++)
                {
                    EtsPlayerView p = players[i];

                    if (p == null)
                    {
                        continue;
                    }

                    h = h * 31 + p.PlayerId;
                    h = h * 31 + (int)p.Status;
                    h = h * 31 + (p.IsMaster ? 1 : 0) + (p.IsLocal ? 2 : 0);
                    h = h * 31 + (p.Name != null ? p.Name.GetHashCode() : 0);
                }

                return h * 31 + (players != null ? players.Count : -1);
            }
        }
    }

    /// <summary>
    /// A passenger's job loading screen as a pure state machine (owner D; PLAN.md §3.5, §8 D3), stepped by EtsFlow at 4 Hz
    /// with the ridden truck, its position and a ground test, so probe 2260 can drive it through a whole ride offline.
    ///
    /// - A new truck (or none) resets it; the first look at a truck only records where it is.
    /// - Between two looks the truck jumped (EtsFlowRules.RideJumped: more than 50 m and faster than 100 m/s) while no job
    ///   screen of the player's own is up: Show, unless the ground is already there (JumpedOntoGround, logged only).
    /// - While up: once the ground test passes, Grounded (the bar full), and Hide 0.6 s later; Hide at RideGiveUp (90 s)
    ///   without ground; Hide at once when the player leaves the seat or the truck changes (§3.5: a vanished truck
    ///   releases the seat). A second jump while up waits for the new place's ground (Grounded is cleared).
    /// Allocation-free: Why is always one of the literal strings below.
    /// </summary>
    public sealed class EtsRideWatch
    {
        public enum Action { None, Show, Hide, JumpedOntoGround }

        /// <summary>The screen is up.</summary>
        public bool Up { get; private set; }

        /// <summary>The ground was found under the truck; the screen goes 0.6 s later.</summary>
        public bool Grounded { get; private set; }

        /// <summary>When the screen came up (the caller's clock).</summary>
        public float Since { get; private set; }

        /// <summary>How far the truck jumped, for the log.</summary>
        public float Moved { get; private set; }

        /// <summary>Why the screen went.</summary>
        public string Why { get; private set; } = "";

        public const string WhyGround = "this map has ground under the truck";
        public const string WhyLeft = "the player is no longer seated as a passenger";
        public const string WhyChanged = "the player is in another truck";
        public const string WhyGaveUp = "no ground under the truck after 90 s (is the map streaming round the ridden truck?)";

        private object _truck;
        private Vector3 _last;
        private float _lastAt, _doneAt = -1f;

        /// <summary>
        /// One look. truck: the ridden truck (any reference; null when the player is not seated as a passenger), at: its
        /// position, jobLoading: the player's own job screen is up, ground: whether this client's map has ground under a
        /// point (called only while it matters: after a jump, and while the screen is up).
        /// </summary>
        public Action Step(float now, object truck, Vector3 at, bool jobLoading, Func<Vector3, bool> ground)
        {
            if (truck == null)
            {
                _truck = null;
                return Up ? End(WhyLeft) : Action.None;
            }

            if (!ReferenceEquals(truck, _truck))
            {
                bool wasUp = Up;
                _truck = truck;
                _last = at;
                _lastAt = now;
                return wasUp ? End(WhyChanged) : Action.None;
            }

            float moved = (at - _last).magnitude;
            float seconds = now - _lastAt;
            _last = at;
            _lastAt = now;

            if (Up)
            {
                // Another jump while up (the owner's job failed and EtsJobs put the truck back, or a respawn): the ground
                // that counts is the new place's, even if the old place's had already been found (checker probe 2265).
                if (EtsFlowRules.RideJumped(moved, seconds))
                {
                    Moved = moved;
                    Grounded = false;
                    _doneAt = -1f;
                }

                if (_doneAt > 0f)
                {
                    return now >= _doneAt ? End(WhyGround) : Action.None;
                }

                if (ground != null && ground(at))
                {
                    Grounded = true;
                    _doneAt = now + 0.6f;
                    return Action.None;
                }

                return now - Since > EtsFlowRules.RideGiveUp ? End(WhyGaveUp) : Action.None;
            }

            if (jobLoading || !EtsFlowRules.RideJumped(moved, seconds))
            {
                return Action.None;
            }

            Moved = moved;

            if (ground != null && ground(at))
            {
                return Action.JumpedOntoGround;
            }

            Up = true;
            Grounded = false;
            _doneAt = -1f;
            Since = now;
            return Action.Show;
        }

        /// <summary>
        /// The player has just sat down in truck as a passenger (EtsFlow.WatchRide calls it before Step while the screen is
        /// down). Where this map has no ground under that truck yet - the tablet's Ride along seats the player in a truck
        /// kilometres away, before the map there has streamed (E's and E's checker's request) - the same screen comes up
        /// until the ground arrives, exactly as after a jump (Step then hides it). Not a boarding (the same truck as last
        /// look), or ground already there: None, and Step carries on as before. One ground ray per boarding, nothing else.
        /// </summary>
        public Action Board(float now, object truck, Vector3 at, bool jobLoading, Func<Vector3, bool> ground)
        {
            if (truck == null || Up || ReferenceEquals(truck, _truck))
            {
                return Action.None;
            }

            _truck = truck;
            _last = at;
            _lastAt = now;

            if (jobLoading || ground == null || ground(at))
            {
                return Action.None;
            }

            Moved = 0f;
            Up = true;
            Grounded = false;
            _doneAt = -1f;
            Since = now;
            return Action.Show;
        }

        /// <summary>The player's own job screen took over (EtsFlow.OnJobStarting).</summary>
        public void Cancel()
        {
            Up = false;
            _doneAt = -1f;
        }

        private Action End(string why)
        {
            Up = false;
            _doneAt = -1f;
            Why = why;
            return Action.Hide;
        }
    }

    /// <summary>
    /// What is on screen in each phase: the HQ stage, the loading screens, the arrival hand-off (owner D; PLAN.md
    /// §5.2, §5.5, §7.D). The last entry of EtsNet.Awake's bootstrap list (PLAN §3.2), which calls Ensure only when
    /// EtsFeatures.NewFlow is on.
    ///
    /// The phases (EtsSession, owner A) go through EtsFlowRules.For:
    /// - Connecting (at most EtsSession.OfflineAfter, 20 s): the void and the HQ map, read-only, "Joining the instance ...".
    /// - Offline (no room, or the editor): the map for the player alone; nothing is written to the room.
    /// - ChoosingHq (the master): the "YOUR HEADQUARTERS" message box, the map, the master's banner; the hovered city is
    ///   sent to the watchers (EtsSession.PreviewCity), Confirm hands the city's yard to EtsSession.ChooseHeadquarters
    ///   and keeps the city in the career (EtsProfileStore.SetHqCity, the next visit's preselection; default Szczecin,
    ///   the player's own 1.61 HQ). A choice the session does not take (the master role moved) makes Confirm live again
    ///   with the refused banner for 6 s.
    /// - WaitingForMaster: the map read-only with the master's hovered city lit (EtsSession.PreviewCityToken, 4 Hz).
    /// - Loading: the map goes, the void stays (the player waits in it while the yard streams 600 m below, U19), the 1.61
    ///   loading screen with EtsSession.LoadProgress every frame; the arrival teleport is A's, so the player is no longer
    ///   kept in the void.
    /// - InWorld (A enters it only after the arrival teleport is confirmed): the loading screen and the void go. The
    ///   HUD master badge for the master.
    /// EtsPostFx.PhaseWeight is 0 while the HQ stage or a loading screen is up and 1 in the world (PLAN C20). The scene's
    /// desktop status line (its "Status Canvas", where EtsMapViewer reports) is hidden while a full-view screen is up.
    ///
    /// Jobs (EtsJobs, owner G): JobStarting shows the job loading screen ("Monday 10:00  Current job: Stone Dust  Hanover
    /// - Hanover  €368"), JobStarted takes it down 0.6 s later, JobFailed at once; Delivered hands the delivery to the
    /// tablet's report page (EtsPageDelivery.Record, with the career's experience before and after), selects that page and
    /// says "JOB COMPLETED: + €412   + 16 XP" on the tablet's toast line (Q-E2: the report is in the tablet, not full view;
    /// Q-E1: the world never opens the tablet itself).
    ///
    /// Behind the scenes, in the void: the HQ catalogue (EtsHqCatalog) from its Bridge LocalStorage cache, or built from
    /// the map index and then cached (PLAN §3.6); the locale on the main thread only if nobody (the tablet app's worker)
    /// has loaded it 8 s after the archives are mounted; and the page caches warmed one face a frame (the eight fonts
    /// the game's templates name, the truck catalogue), so the tablet's first page build in the world is the ~6 ms warm one
    /// and not the ~209 ms cold one E measured (Phase 1 request). EtsFrontEnd stays only for its diagnostics (F9-F11 and
    /// the 10 s heartbeat): its launchpad, garage, job market and desktop never show in the new flow.
    ///
    /// Phase 3 (D3):
    /// - A passenger's job loading screen (PLAN §3.5): when the truck the player sits in as a passenger (EtsLocalTruck.
    ///   Occupied, a puppet) jumps more than 50 m between two looks (its owner took a job, EtsJobs.Take teleports it),
    ///   the 1.61 loading screen comes up with "Riding to {company} {city} with {owner}" - the depot nearest where it
    ///   landed - until this client's own map has ground under the truck (the ray EtsJobs uses: 60 m above, 300 m down,
    ///   EtsLayers.GroundMask), then goes 0.6 s later; at most 90 s. In a headset its panel is fixed in the cab
    ///   (EtsLoadingScreen.RideWith), since the owner may drive off before this map is built.
    /// - A visitor without the game (NoGame): 1.5 s after the flow began the archives are still not mounted (EtsSession.
    ///   Start opens the map viewer synchronously in the first frame). They stay in the void, whatever the phase, with a
    ///   notice: what is missing, how to get it, and who is in the world doing what (EtsNet.Players, 1 Hz, rewritten only
    ///   on a change). Their trucks cannot be shown: each client reads the truck models and the map from its own install.
    /// - The HUD master badge follows every master change (EtsSession.MasterChanged and the 4 Hz look), logged when it flips.
    ///
    /// Per frame: nothing but the loading bar's width while a loading screen is up; everything else is a 4 Hz tick.
    /// </summary>
    public sealed class EtsFlow : MonoBehaviour
    {
        public static EtsFlow Instance { get; private set; }

        /// <summary>The flow on the bootstrap object, added if missing (PLAN §3.2). Unity's null test, not ??.</summary>
        public static EtsFlow Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsFlow flow = host.GetComponent<EtsFlow>();

            if (flow == null)
            {
                flow = host.AddComponent<EtsFlow>();
            }

            return flow;
        }

        /// <summary>What the flow shows now (for the log, the heartbeat and E's tablet).</summary>
        public EtsFlowView View { get; private set; }

        /// <summary>The phase last applied.</summary>
        public EtsPhase Phase => _phase;

        /// <summary>A job's loading screen is up.</summary>
        public bool JobLoading => _jobLoading;

        /// <summary>
        /// The page caches are warm (Warm ran to its end: the eight template faces, the truck catalogue, the studio). For E
        /// (Phase 2 check): a page built before this pays the cold cost on the main thread - 51-56 ms each for Truck
        /// Preference and Quick Job in E's bootstrap test, ~209 ms fully cold - while after it the build is ~6 ms.
        /// </summary>
        public bool CachesWarm => _cachesWarm;

        /// <summary>This visitor has no usable ETS2 install (class notes). For A (a slot status) and E (the tablet app's words).</summary>
        public bool NoGame => _noGame;

        /// <summary>A passenger's "Riding to" loading screen is up (PLAN §3.5).</summary>
        public bool RideLoading => _ride.Up;

        private EtsSession _session;
        private EtsJobs _jobs;
        private EtsHqStage _stage;
        private EtsHqStage _stageSubscribed;
        private EtsLoadingScreen _loading;
        private bool _loadingIsJob;
        private bool _jobLoading, _jobStarted;
        private float _jobLoadingSince, _jobDoneAt = -1f;
        private EtsPhase _phase;
        private bool _havePhase;
        private float _nextTick;
        private float _refusedUntil;
        private bool _preselected;
        private float _startedAt;
        private string _masterName = "";
        private bool _isMaster;
        private Canvas _statusCanvas;
        private bool _statusSearched;
        private bool _statusHidden;
        private EtsMasterBadge _badge;
        private float _filesAt = -1f;
        private bool _localeForced;
        private bool _prefetched, _facesReady, _cachesWarm;

        // No game (D3).
        private bool _noGame, _noGameDecided, _installFound;
        private float _nextNotice;
        private int _noticeSignature = int.MinValue;

        // The passenger's loading screen (D3).
        private bool _loadingIsRide;
        private readonly EtsRideWatch _ride = new EtsRideWatch();
        private Func<Vector3, bool> _groundUnder;
        private bool _badgeShown;

        /// <summary>
        /// Materials the first screens draw that are worth decoding off the main thread before them: the HQ map's
        /// 2048-square picture and the pan hint, the loading screen's logo, frame corner and spinner sheet, and the
        /// chrome most templates cut from (corner_4px, white, btn_normal). The loading photograph is one of 26 picked
        /// at random when shown, so it is not among them.
        /// </summary>
        private static readonly string[] PrefetchMaterials =
        {
            EtsHqMapView.Whole, EtsHqStageContent.PanIcon, "/material/ui/loading/logo.mat", "/material/ui/loading/corner.mat", EtsLoadingContent.Spinner,
            "/material/ui/corner_4px.mat", "/material/ui/white.mat", "/material/ui/button/btn_normal.mat"
        };

        private void Awake()
        {
            Instance = this;

            // The HQ map waits for the prefetch and the faces made from it (Warm; at most 8 s, EtsHqStage.TryBuildMap), so its
            // first build is not the cold one. Set in Awake: EtsFlow.Start shows the stage.
            if (EtsFeatures.NewFlow)
            {
                EtsHqStage.ReadyToBuild = () => _facesReady;
            }
        }

        private void Start()
        {
            if (!EtsFeatures.NewFlow)
            {
                // Only EtsNet.Awake adds this, and only in the new flow; a stray copy does nothing.
                enabled = false;
                return;
            }

            _startedAt = Time.realtimeSinceStartup;
            BindSession();
            BindJobs();
            EtsFrontEnd.EnsureDiagnostics(gameObject);
            Tick(true);
            StartCoroutine(Prefetch());
            StartCoroutine(Catalogue());
            StartCoroutine(Warm());
            Debug.Log("[SomniumEuroTruck] Flow: started - phase " + _phase + ", " + View + ".");
        }

        private void OnDestroy()
        {
            if (_session != null)
            {
                _session.PhaseChanged -= OnPhaseChanged;
                _session.MasterChanged -= OnMasterChanged;
            }

            if (_jobs != null)
            {
                _jobs.JobStarting -= OnJobStarting;
                _jobs.JobStarted -= OnJobStarted;
                _jobs.JobFailed -= OnJobFailed;
                _jobs.Delivered -= OnDelivered;
            }

            if (_stageSubscribed != null)
            {
                _stageSubscribed.Hovered -= OnHovered;
                _stageSubscribed.Confirmed -= OnConfirmed;
            }

            _badge?.Destroy();

            // The world is going: the post weight and the stage's hook are statics that outlive it (PLAN §3.2).
            EtsPostFx.PhaseWeight = 1f;
            EtsHqStage.ReadyToBuild = null;

            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ------------------------------------------------------------------ binding

        private void BindSession()
        {
            EtsSession session = EtsSession.Instance;

            if (session == _session)
            {
                return;
            }

            if (_session != null)
            {
                _session.PhaseChanged -= OnPhaseChanged;
                _session.MasterChanged -= OnMasterChanged;
            }

            _session = session;

            if (_session != null)
            {
                _session.PhaseChanged += OnPhaseChanged;
                _session.MasterChanged += OnMasterChanged;
            }
        }

        private void BindJobs()
        {
            EtsJobs jobs = EtsJobs.Instance;

            if (jobs == _jobs)
            {
                return;
            }

            if (_jobs != null)
            {
                _jobs.JobStarting -= OnJobStarting;
                _jobs.JobStarted -= OnJobStarted;
                _jobs.JobFailed -= OnJobFailed;
                _jobs.Delivered -= OnDelivered;
            }

            _jobs = jobs;

            if (_jobs != null)
            {
                _jobs.JobStarting += OnJobStarting;
                _jobs.JobStarted += OnJobStarted;
                _jobs.JobFailed += OnJobFailed;
                _jobs.Delivered += OnDelivered;
            }
        }

        private void OnPhaseChanged(EtsPhase phase)
        {
            // Raised inside EtsSession.Tick (EtsNet.Update) or ChooseHeadquarters: applied at once, so the HQ map and
            // the loading screen swap in the same frame.
            Tick(true);
        }

        private void OnMasterChanged(bool isMe, string name)
        {
            Tick(true);
        }

        // ------------------------------------------------------------------ the phases

        private void Update()
        {
            // Per frame only the loading bar (a sizeDelta) while a loading screen is up.
            if (_loading != null)
            {
                float progress = _loadingIsRide ? EtsFlowRules.JobProgress(Time.realtimeSinceStartup - _ride.Since, _ride.Grounded)
                               : _loadingIsJob ? EtsFlowRules.JobProgress(Time.realtimeSinceStartup - _jobLoadingSince, _jobStarted)
                               : _session != null ? _session.LoadProgress : 0f;
                _loading.SetProgress(progress);
            }

            if (Time.unscaledTime >= _nextTick)
            {
                Tick(false);
            }
        }

        /// <summary>The 4 Hz look at the session (events can be missed: a component added late, a world re-entered) and what follows from it.</summary>
        private void Tick(bool now)
        {
            _nextTick = Time.unscaledTime + 0.25f;
            BindSession();
            BindJobs();

            // No session is the editor without a room or a bootstrap without A's component: the player is alone.
            EtsPhase phase = _session != null ? _session.Current : EtsPhase.Offline;
            bool isMaster = _session == null || _session.IsMaster;
            string masterName = _session != null ? _session.MasterName ?? "" : "";

            if (_jobLoading && _jobDoneAt > 0f && Time.realtimeSinceStartup >= _jobDoneAt)
            {
                EndJobLoading("the job started");
            }
            else if (_jobLoading && Time.realtimeSinceStartup - _jobLoadingSince > 120f)
            {
                EndJobLoading("no word from the job for 120 s");
            }

            DecideNoGame();
            WatchRide(phase);

            bool changed = !_havePhase || phase != _phase || isMaster != _isMaster || masterName != _masterName;
            _phase = phase;
            _havePhase = true;
            _isMaster = isMaster;
            _masterName = masterName;
            EtsFlowView view = EtsFlowRules.For(phase, isMaster, masterName, _jobLoading || _ride.Up, _noGame);

            if (changed || now || !Same(view, View))
            {
                Apply(view, changed);
            }

            if (view.Notice)
            {
                UpdateNotice(changed);
            }
            else if (_stage != null && _stage.HasNotice)
            {
                // The archives turned up after all (DecideNoGame): the notice goes and the map takes its place.
                _stage.ClearNotice();
                _noticeSignature = int.MinValue;
            }

            // Watchers see the master's hover (PLAN C14).
            if (_stage != null && !view.Interactive)
            {
                _stage.Highlight(_session != null ? _session.PreviewCityToken : 0UL);
            }

            if (_stage != null && view.Interactive && !_preselected)
            {
                Preselect();
            }

            if (_stage != null && _refusedUntil > 0f && Time.realtimeSinceStartup > _refusedUntil)
            {
                _refusedUntil = 0f;
                _stage.SetBanner(view.Banner);
            }

            ForceLocaleIfNobodyDid();
        }

        private static bool Same(EtsFlowView a, EtsFlowView b)
        {
            return a.Stage == b.Stage && a.Interactive == b.Interactive && a.Map == b.Map && a.KeepPlayer == b.KeepPlayer && a.SessionLoading == b.SessionLoading &&
                   a.Banner == b.Banner && a.PostWeight == b.PostWeight && a.Badge == b.Badge && a.HudHidden == b.HudHidden && a.Notice == b.Notice;
        }

        private void Apply(EtsFlowView view, bool phaseChanged)
        {
            EtsFlowView was = View;
            View = view;

            // The void and the map.
            if (view.Stage)
            {
                _stage = EtsHqStage.Show(view.Interactive);

                if (_stageSubscribed != _stage)
                {
                    if (_stageSubscribed != null)
                    {
                        _stageSubscribed.Hovered -= OnHovered;
                        _stageSubscribed.Confirmed -= OnConfirmed;
                    }

                    _stage.Hovered += OnHovered;
                    _stage.Confirmed += OnConfirmed;
                    _stageSubscribed = _stage;
                    _preselected = false;
                }

                _stage.SetMapShown(view.Map);
                _stage.KeepPlayer = view.KeepPlayer;

                if (_refusedUntil <= 0f)
                {
                    _stage.SetBanner(view.Banner);
                }
            }
            else if (_stage != null)
            {
                // After the arrival teleport (A makes the phase InWorld only once it is confirmed).
                _stage.Hide();
                _stage = null;
            }

            // The session's loading screen.
            if (view.SessionLoading)
            {
                if (_loading == null || _loadingIsJob)
                {
                    ShowLoading(null, false);
                }
            }
            else if (_loading != null && !_loadingIsJob)
            {
                HideLoading();
            }

            EtsPostFx.PhaseWeight = view.PostWeight;
            SetStatusHidden(view.HudHidden);
            SetBadge(view.Badge, phaseChanged);

            if (phaseChanged || !Same(view, was))
            {
                Debug.Log("[SomniumEuroTruck] Flow: phase " + _phase + (_isMaster ? " (master me)" : _masterName.Length > 0 ? " (master " + _masterName + ")" : "") + " - " + view + ".");
            }
        }

        // ------------------------------------------------------------------ the HQ choice

        private void OnHovered(EtsHqCity city)
        {
            if (city != null && _session != null && View.Interactive)
            {
                _session.PreviewCity(city.Token);
            }
        }

        private void OnConfirmed(EtsHqCity city)
        {
            if (city == null || _stage == null)
            {
                return;
            }

            EtsHqChoice choice = city.Yard;
            choice.City = city.Token;

            // The career keeps the last HQ for the next visit's preselection (PLAN §5.5 step 4).
            EtsProfileStore store = EtsProfileStore.Current;

            if (store != null && store.IsReady)
            {
                store.SetHqCity(city.Id);
            }

            if (_session == null)
            {
                Debug.LogWarning("[SomniumEuroTruck] Flow: " + city.Name + " was confirmed but there is no session to take it.");
                _stage.Unconfirm();
                return;
            }

            _session.ChooseHeadquarters(choice);

            if (_session.Current == EtsPhase.Loading || _session.Current == EtsPhase.InWorld)
            {
                Debug.Log("[SomniumEuroTruck] Flow: headquarters " + city.Name + " (" + city.CountryName + ") taken by the session - yard prefab " + choice.YardPrefabUid.ToString("x16") +
                          ", origin " + choice.OriginGame.ToString("0.0") + ", arrival " + choice.ArrivalGame.ToString("0.0") + ".");
                return;
            }

            // Not taken (the session logged why): Confirm live again, and say so for a while.
            _stage.Unconfirm();
            _refusedUntil = Time.realtimeSinceStartup + 6f;
            _stage.SetBanner(EtsHqStageContent.RefusedBanner);
            Debug.LogWarning("[SomniumEuroTruck] Flow: the session did not take " + city.Name + " (phase " + _session.Current + ").");
        }

        /// <summary>
        /// The career's last HQ, or Szczecin (the player's own 1.61 HQ, garage.szczecin in their save; the world's
        /// start since Phase 0) - once the career has answered or 6 s after the flow began, whichever is first.
        /// </summary>
        private void Preselect()
        {
            EtsProfileStore store = EtsProfileStore.Current;
            bool storeAnswered = store == null || store.IsReady;

            if (!storeAnswered && Time.realtimeSinceStartup - _startedAt < 6f)
            {
                return;
            }

            _preselected = true;
            string id = store != null && store.IsReady && store.Profile != null && !string.IsNullOrEmpty(store.Profile.HqCity) ? store.Profile.HqCity : "szczecin";
            _stage.Preselect(EtsToken.Encode(id));
            Debug.Log("[SomniumEuroTruck] Flow: preselected " + id + (id == "szczecin" && (store == null || store.Profile == null || string.IsNullOrEmpty(store.Profile.HqCity)) ? " (no HQ in the career yet)" : " (the career's last HQ)") + ".");
        }

        // ------------------------------------------------------------------ loading screens

        private void ShowLoading(string jobLine, bool job, bool ride = false)
        {
            if (_loading != null)
            {
                _loading.Hide();
            }

            _loading = EtsLoadingScreen.Show(jobLine);
            _loadingIsJob = job && _loading != null;
            _loadingIsRide = ride && _loading != null;

            if (_loading != null)
            {
                _loading.SetProgress(0f);
            }
        }

        private void HideLoading()
        {
            if (_loading != null)
            {
                _loading.Hide();
                _loading = null;
            }

            _loadingIsJob = false;
            _loadingIsRide = false;
        }

        private void OnJobStarting(EtsJobs.Offer offer)
        {
            if (offer == null || _phase != EtsPhase.InWorld)
            {
                return;
            }

            // A passenger cannot take a job (EtsJobs.CanTake, PLAN §3.5); should one start anyway, the player's own wins.
            if (_ride.Up)
            {
                _ride.Cancel();
                _loadingIsRide = false;
            }

            int minutes = _session != null ? _session.GameClockMinutes : 600;
            string line = EtsLoadingContent.JobValue(minutes, offer.CargoName, offer.FromCityName, offer.ToCityName, offer.Reward);
            _jobLoading = true;
            _jobStarted = false;
            _jobDoneAt = -1f;
            _jobLoadingSince = Time.realtimeSinceStartup;
            ShowLoading(line, true);
            Tick(true);
            Debug.Log("[SomniumEuroTruck] Flow: job loading screen - " + offer.CargoName + " from " + offer.FromCompanyName + ", " + offer.FromCityName + " to " + offer.ToCompanyName + ", " + offer.ToCityName + ".");
        }

        private void OnJobStarted(EtsJobs.Offer offer)
        {
            if (!_jobLoading)
            {
                return;
            }

            // The bar full, then off 0.6 s later (the old front end's pause).
            _jobStarted = true;
            _jobDoneAt = Time.realtimeSinceStartup + 0.6f;
        }

        private void OnJobFailed(EtsJobs.Offer offer)
        {
            if (_jobLoading)
            {
                EndJobLoading("the job could not start (its yard never built)");
            }
        }

        private void EndJobLoading(string why)
        {
            _jobLoading = false;
            _jobDoneAt = -1f;

            if (_loadingIsJob && !_loadingIsRide)
            {
                HideLoading();
            }

            Debug.Log("[SomniumEuroTruck] Flow: job loading screen down after " + (Time.realtimeSinceStartup - _jobLoadingSince).ToString("0.0") + " s - " + why + ".");
            Tick(true);
        }

        private void OnDelivered(EtsJobs.DeliveryStats stats)
        {
            if (stats == null || stats.Job == null)
            {
                return;
            }

            // The delivery's own before/after (G, EtsJobs.DeliveryStats): exact even while the credit is queued for a
            // career that is still opening, which the store's experience minus the delivery XP is not (G2 check).
            long before = stats.ExperienceBefore;
            long after = stats.ExperienceAfter;
            EtsPageDelivery.Record(stats, before, after);

            EtsTabletApp app = EtsTabletApp.Current;

            if (app != null)
            {
                app.Show(EtsPageDelivery.PageId);
                app.Toast(EtsFlowRules.DeliveryToast(stats.Reward, stats.Xp));
            }

            Debug.Log("[SomniumEuroTruck] Flow: delivered " + stats.Job.CargoName + " - report page ready, + " + stats.Reward + " EUR, + " + stats.Xp + " XP (experience " + before + " -> " + after + ").");
        }

        // ------------------------------------------------------------------ a passenger's job loading screen (PLAN §3.5, D3)

        /// <summary>
        /// The 4 Hz look at the truck this player rides in as a passenger (class notes), through the pure EtsRideWatch (probe
        /// 2260). A puppet (EtsTruckRig.IsPuppet) the local player occupies and does not own: B seats passengers only in
        /// other players' trucks (PLAN §5.5). Standing riders are left where they stood (§3.5), so only a seat counts.
        /// Allocation-free until a jump is seen: the ground ray's delegate is made once.
        /// </summary>
        private void WatchRide(EtsPhase phase)
        {
            EtsTruckRig rig = phase == EtsPhase.InWorld && !_noGame ? EtsLocalTruck.Occupied : null;
            bool passenger = rig != null && rig.IsPuppet && !ReferenceEquals(rig, EtsLocalTruck.Own);
            Vector3 at = passenger ? rig.transform.position : Vector3.zero;

            if (_groundUnder == null)
            {
                _groundUnder = GroundUnder;
            }

            float now = Time.realtimeSinceStartup;
            EtsRideWatch.Action action = passenger ? _ride.Board(now, rig, at, _jobLoading, _groundUnder) : EtsRideWatch.Action.None;

            if (action == EtsRideWatch.Action.None)
            {
                action = _ride.Step(now, passenger ? rig : null, at, _jobLoading, _groundUnder);
            }

            switch (action)
            {
                case EtsRideWatch.Action.Show:
                    StartRideLoading(rig, at, _ride.Moved);
                    break;
                case EtsRideWatch.Action.Hide:
                    EndRideLoading(_ride.Why);
                    break;
                case EtsRideWatch.Action.JumpedOntoGround:
                    Debug.Log("[SomniumEuroTruck] Flow: the ridden truck jumped " + _ride.Moved.ToString("0") + " m to Unity " + at.ToString("0") + "; this map already has ground there - no loading screen.");
                    break;
            }
        }

        private void StartRideLoading(EtsTruckRig rig, Vector3 at, float moved)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            RideNames(at, out string company, out string city);
            string owner = rig.OwnerPlayerId >= 0 ? EtsNet.NameOf(rig.OwnerPlayerId) : "";
            int minutes = _session != null ? _session.GameClockMinutes : 600;
            string line = EtsFlowRules.RideValue(minutes, company, city, owner);
            double namesMs = watch.Elapsed.TotalMilliseconds;
            ShowLoading(line, true, true);

            // In a headset the panel rides in the cab, ahead of the passenger's eye (seat 1: B seats passengers there).
            EtsTruckController controller = rig.Controller;

            if (_loading != null && controller != null && EtsVr.Present)
            {
                _loading.RideWith(controller.transform, controller.EyeLocalFor(1));
            }

            Debug.Log("[SomniumEuroTruck] Flow: riding - " + (owner.Length > 0 ? owner + "'s" : "the") + (moved > 0f ? " truck jumped " + moved.ToString("0") + " m to Unity " : " truck was boarded where this map has no ground yet, at Unity ") + at.ToString("0") +
                      "; job loading screen \"" + (company != null ? EtsFlowRules.RidingTo + " " + company + ", " + city : EtsFlowRules.RidingWith + " " + owner) + "\" until this map has ground under it (names in " +
                      namesMs.ToString("0.0") + " ms" + (_loading == null ? "; NO SCREEN - the archives are not mounted" : "") + ").");
        }

        private void EndRideLoading(string why)
        {
            if (_loadingIsRide)
            {
                HideLoading();
            }

            Debug.Log("[SomniumEuroTruck] Flow: riding - loading screen down after " + (Time.realtimeSinceStartup - _ride.Since).ToString("0.0") + " s - " + why + ".");
        }

        /// <summary>This client's map has ground under a point: the ray EtsJobs waits on for a job's yard (60 m above, 300 m down, EtsLayers.GroundMask).</summary>
        private static bool GroundUnder(Vector3 at)
        {
            return Physics.Raycast(at + Vector3.up * 60f, Vector3.down, 300f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// The company and city of the depot nearest a Unity point, within RideCompanyRadius (a job starts on its company's
        /// own bay, EtsJobs.Bay). The map index's depots (365 on 1.61) by their map node; once per jump, so a walk is fine.
        /// Nulls when the index is not ready or no depot is near.
        /// </summary>
        private static void RideNames(Vector3 at, out string company, out string city)
        {
            company = null;
            city = null;
            EtsMapIndex index = EtsMapIndex.Instance;
            EtsMapViewer viewer = EtsMapViewer.Current;
            EtsMap map = viewer != null ? viewer.Map : null;

            if (index == null || !index.Ready || map == null)
            {
                return;
            }

            IReadOnlyList<EtsMapCompany> companies = index.Companies;
            EtsMapCompany best = null;
            float bestSq = EtsFlowRules.RideCompanyRadius * EtsFlowRules.RideCompanyRadius;

            for (int i = 0; i < companies.Count; i++)
            {
                EtsMapCompany candidate = companies[i];
                EtsMapNode node = candidate != null ? map.FindNode(candidate.NodeUid) : null;

                if (node == null)
                {
                    continue;
                }

                Vector3 w = EtsMapBuilder.ToWorld(node.Position, viewer.CenterGame);
                float dx = w.x - at.x, dz = w.z - at.z, sq = dx * dx + dz * dz;

                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = candidate;
                }
            }

            if (best == null)
            {
                return;
            }

            try
            {
                EtsSiiUnit companyUnit = map.Company(best.CompanyToken);
                EtsSiiUnit cityUnit = map.City(best.CityToken);
                company = Words(companyUnit != null ? companyUnit.GetString("name", null) : null) ?? best.Name;
                city = Words(cityUnit != null ? cityUnit.GetString("city_name", null) : null) ?? best.CityId;
            }
            catch (Exception e)
            {
                company = best.Name;
                city = best.CityId;
                Debug.LogWarning("[SomniumEuroTruck] Flow: riding - the depot's names could not be read (" + e.GetType().Name + ": " + e.Message + ").");
            }
        }

        private static string Words(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            return text.Contains("@@") && EtsLocale.Loaded ? EtsLocale.Resolve(text) : text;
        }

        // ------------------------------------------------------------------ a visitor without the game (D3)

        /// <summary>
        /// Once, 1.5 s after the flow began (class notes): no archives mounted and no open map means no game on this PC,
        /// or one that could not be opened - EtsInstall.Locate tells the two apart (a walk of Steam's library list, a few
        /// ms, once). Should the archives turn up after all (a later LoadAt mounting them), the notice goes.
        /// </summary>
        private void DecideNoGame()
        {
            if (_noGame && EtsEngine.IsMounted)
            {
                _noGame = false;
                Debug.Log("[SomniumEuroTruck] Flow: the game's archives are mounted after all - the no-game notice goes.");
                return;
            }

            if (_noGameDecided || Time.realtimeSinceStartup - _startedAt < 1.5f)
            {
                return;
            }

            _noGameDecided = true;
            EtsMapViewer viewer = EtsMapViewer.Current;

            if (EtsEngine.IsMounted || (viewer != null && viewer.IsOpen))
            {
                return;
            }

            _noGame = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string root = null;

            try
            {
                root = EtsInstall.Locate();
            }
            catch (Exception)
            {
            }

            _installFound = root != null;
            Debug.Log("[SomniumEuroTruck] Flow: NO GAME - the archives are not mounted 1.5 s after the flow began (" +
                      (root != null ? "an install at " + root + " could not be opened" : "no Euro Truck Simulator 2 install was found") + ", looked in " + watch.ElapsedMilliseconds +
                      " ms); the visitor stays in the void with the notice and the list of players.");
        }

        /// <summary>The notice's words, rewritten only when the players, their doings or the master role changed (checked once a second).</summary>
        private void UpdateNotice(bool force)
        {
            if (_stage == null)
            {
                return;
            }

            float now = Time.unscaledTime;

            if (!force && now < _nextNotice)
            {
                return;
            }

            _nextNotice = now + 1f;
            EtsNet net = EtsNet.Instance;
            IReadOnlyList<EtsPlayerView> players = net != null ? net.Players : null;
            int signature = EtsFlowRules.PlayersSignature(players) ^ (_isMaster ? 0x5bd1e995 : 0);

            if (!force && signature == _noticeSignature)
            {
                return;
            }

            bool first = _noticeSignature == int.MinValue;
            _noticeSignature = signature;
            string players2 = EtsFlowRules.PlayersText(players);
            _stage.SetNotice(EtsFlowRules.NoGameTitle(_installFound), EtsFlowRules.NoGameBody(_installFound, _isMaster), players2);

            if (first)
            {
                Debug.Log("[SomniumEuroTruck] Flow: no-game notice shown (" + (_isMaster ? "this visitor is the instance master" : "master " + (_masterName.Length > 0 ? _masterName : "unknown")) + "; " +
                          (players != null ? players.Count : 0) + " player view(s)).");
            }
        }

        // ------------------------------------------------------------------ the HUD

        /// <summary>
        /// The scene's "Status Canvas" (a screen overlay at sorting order 100, SCENE:1197-1260, where EtsMapViewer and the
        /// trucks write their status line): hidden while a full-view screen of the game is up, since an overlay draws over
        /// the world-space HQ map. Found once by name; nothing if the scene has none.
        /// </summary>
        private void SetStatusHidden(bool hidden)
        {
            if (!_statusSearched)
            {
                _statusSearched = true;
                GameObject found = GameObject.Find("Status Canvas");
                _statusCanvas = found != null ? found.GetComponent<Canvas>() : null;
            }

            if (_statusCanvas == null || _statusHidden == hidden)
            {
                return;
            }

            _statusHidden = hidden;
            _statusCanvas.enabled = !hidden;
        }

        /// <summary>
        /// The HUD master badge (PLAN §3.7 item 2). A master change reaches it through EtsSession.MasterChanged (Tick at once)
        /// and the 4 Hz look, whichever is first; each flip in the world is logged with the new master's name (Phase 3: the
        /// master leaves, the next player's badge comes up; A's news line says it to everyone).
        /// </summary>
        private void SetBadge(bool shown, bool masterOrPhaseChanged)
        {
            if (shown && _badge == null)
            {
                _badge = EtsMasterBadge.Create(transform);
            }

            _badge?.SetShown(shown);

            if (shown != _badgeShown)
            {
                _badgeShown = shown;

                if (_phase == EtsPhase.InWorld && masterOrPhaseChanged)
                {
                    Debug.Log("[SomniumEuroTruck] Flow: HUD master badge " + (shown ? "shown - this player is the instance master" : "hidden - " + (_isMaster ? "a full-view screen is up" : "the master is " + (_masterName.Length > 0 ? _masterName : "someone else"))) +
                              (EtsVr.Present ? " (in a headset: on the dash plate, EtsVrDash)" : "") + ".");
                }
            }
        }

        // ------------------------------------------------------------------ in the void, behind the scenes

        /// <summary>
        /// The tablet app reads the locale on a worker thread as soon as the archives are mounted (EtsTabletApp.
        /// ReadInTheBackground). Should nobody have 8 s later (no app), it is read here, on the main thread, in the void
        /// (33-37 ms, EtsLocale's own measure).
        /// </summary>
        private void ForceLocaleIfNobodyDid()
        {
            if (EtsLocale.Loaded || _localeForced || EtsEngine.Files == null)
            {
                return;
            }

            if (_filesAt < 0f)
            {
                _filesAt = Time.realtimeSinceStartup;
                return;
            }

            if (Time.realtimeSinceStartup - _filesAt < 8f)
            {
                return;
            }

            _localeForced = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool ok = EtsLocale.Load(EtsEngine.Files);
            Debug.Log("[SomniumEuroTruck] Flow: the locale was not loaded 8 s after the archives - read here in " + watch.ElapsedMilliseconds + " ms (" + (ok ? EtsLocale.Count + " words" : "FAILED") + ").");
        }

        /// <summary>
        /// The HQ catalogue (PLAN §3.6): from its LocalStorage cache (CatalogueKey: this install's map signature and
        /// language), else built from EtsMapIndex in 1.5 ms slices (EtsHqCatalog.Build) and then written to the cache,
        /// so the second visit has the pins at once. Data derived on the player's PC from their install, not an asset.
        /// </summary>
        private IEnumerator Catalogue()
        {
            if (EtsHqCatalog.Ready)
            {
                Debug.Log("[SomniumEuroTruck] Flow: HQ catalogue already in memory (" + EtsHqCatalog.Cities.Count + " cities).");
                yield break;
            }

            float began = Time.realtimeSinceStartup;

            // The opened map (EtsSession.Start opens it) and, so the names are the locale's, the words (at most 10 s).
            while (EtsEngine.Files == null || EtsMapViewer.Current == null || EtsMapViewer.Current.Map == null)
            {
                yield return new WaitForSeconds(0.25f);
            }

            while (!EtsLocale.Loaded && Time.realtimeSinceStartup - began < 10f)
            {
                yield return new WaitForSeconds(0.25f);
            }

            EtsFileSystem files = EtsEngine.Files;
            EtsMap map = EtsMapViewer.Current.Map;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string signature = EtsHqCatalog.MapSignature(files, map);
            string key = EtsFlowRules.CatalogueKey(signature, EtsLocale.Loaded ? EtsLocale.Language : "");
            ISomniumWorldsLocalStorage storage = Storage();
            string cacheNote;

            if (storage != null)
            {
                string text = null;

                try
                {
                    text = storage.TextExists(key) ? storage.ReadText(key) : null;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Flow: HQ catalogue cache '" + key + "' could not be read (" + e.GetType().Name + ": " + e.Message + ").");
                }

                List<EtsHqCity> cached = EtsHqCatalog.Deserialize(text, signature);

                if (cached != null && cached.Count > 0)
                {
                    EtsHqCatalog.Adopt(cached);
                    Debug.Log("[SomniumEuroTruck] Flow: HQ catalogue: " + cached.Count + " cities from the LocalStorage cache '" + key + "' in " + watch.ElapsedMilliseconds + " ms, " +
                              (Time.realtimeSinceStartup - _startedAt).ToString("0.0") + " s after the flow began.");
                    yield break;
                }

                cacheNote = text == null ? "no cache yet" : "the cache was stale or damaged";
            }
            else
            {
                cacheNote = "no LocalStorage (the editor, or a Bridge without storage)";
            }

            // Built from the whole-map index (G, EtsMapIndex; 10-20 s in the client, U16), in slices.
            float buildBegan = Time.realtimeSinceStartup;
            IEnumerator build = EtsHqCatalog.Build(EtsMapIndex.Instance);

            while (build.MoveNext())
            {
                yield return build.Current;
            }

            if (!EtsHqCatalog.Ready)
            {
                Debug.LogWarning("[SomniumEuroTruck] Flow: HQ catalogue NOT built - " + EtsHqCatalog.Report + ".");
                yield break;
            }

            Debug.Log("[SomniumEuroTruck] Flow: HQ catalogue: " + EtsHqCatalog.Cities.Count + " cities built in " + (Time.realtimeSinceStartup - buildBegan).ToString("0.0") + " s (" + cacheNote + "); " + EtsHqCatalog.Report + ".");

            if (storage == null)
            {
                yield break;
            }

            watch.Restart();
            string written = EtsHqCatalog.Serialize(EtsHqCatalog.Cities, signature);
            bool ok = false;

            try
            {
                ok = storage.WriteText(key, written);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Flow: HQ catalogue cache could not be written (" + e.GetType().Name + ": " + e.Message + ").");
            }

            Debug.Log("[SomniumEuroTruck] Flow: HQ catalogue cache '" + key + "' " + (ok ? "written" : "NOT written") + " (" + written.Length + " characters, " + watch.ElapsedMilliseconds + " ms).");
        }

        private static ISomniumWorldsLocalStorage Storage()
        {
            try
            {
                return SomniumBridge.LocalStorage;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The cold half of the first screens, off the main thread, as soon as the archives are mounted (Phase 1 request:
        /// "a ~209 ms cold page build must be sliced or done during loading"): the eight faces' atlas decodes and median
        /// fields (EtsFont.Prepare, 210-230 ms each on the frame in probe 2045), the 682 look templates
        /// (EtsUiLayout.ReadTemplates) and the PrefetchMaterials' textures (EtsTextureCache.Prepare), on one EtsWorker
        /// thread. The main thread only takes the results, one a frame (template lists, texture uploads); the HQ map waits
        /// for this (EtsHqStage.ReadyToBuild, at most 8 s). Logged once with its times.
        /// </summary>
        private IEnumerator Prefetch()
        {
            while (EtsEngine.Files == null)
            {
                yield return new WaitForSeconds(0.1f);
            }

            EtsFileSystem files = EtsEngine.Files;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var worker = new EtsWorker("Ets UI prefetch");
            int pending = 0, posted = 0;
            double worstTake = 0.0;

            if (!EtsUiLayout.TemplatesReady(files))
            {
                pending++;
                posted++;
                worker.Post(() => EtsUiLayout.ReadTemplates(files), (result, failure) =>
                {
                    pending--;
                    EtsUiLayout.AdoptTemplates(files, result as List<EtsSiiFile>);
                });
            }

            foreach (string face in EtsGameUi.TemplateFaces)
            {
                if (EtsFont.IsReady(face))
                {
                    continue;
                }

                string f = face;
                pending++;
                posted++;
                worker.Post(() => EtsFont.Prepare(files, f), (result, failure) =>
                {
                    pending--;
                    EtsFont.Adopt(f, result as EtsFont.Prepared);
                });
            }

            foreach (string material in PrefetchMaterials)
            {
                // The .mat is a few hundred bytes: read here; the texture's read and decode go to the worker.
                string texture = EtsGameUi.TexturePathOf(files, material);

                if (texture == null || EtsEngine.Textures == null || EtsEngine.Textures.IsLoaded(texture, false))
                {
                    continue;
                }

                pending++;
                posted++;
                worker.Post(() => EtsTextureCache.Prepare(files, texture, false, false), (result, failure) =>
                {
                    pending--;

                    if (result is EtsTextureCache.Prepared prepared && EtsEngine.Textures != null)
                    {
                        EtsEngine.Textures.Adopt(prepared);
                    }
                });
            }

            float began = Time.realtimeSinceStartup;

            // One result a frame: a texture upload or a template list swap each stays a frame's work, not all of them in one.
            while (pending > 0 && Time.realtimeSinceStartup - began < 30f)
            {
                var take = System.Diagnostics.Stopwatch.StartNew();
                int taken = 0;
                worker.Drain(() => taken++ < 1);
                worstTake = Math.Max(worstTake, take.Elapsed.TotalMilliseconds);
                yield return null;
            }

            worker.Dispose();
            _prefetched = true;
            Debug.Log("[SomniumEuroTruck] Flow: UI prefetch - " + posted + " jobs on " + (worker.Threaded ? "a worker thread" : "the main thread (no thread)") + " in " +
                      watch.Elapsed.TotalSeconds.ToString("0.00") + " s, " + (pending > 0 ? pending + " NOT finished, " : "") + "worst frame taking a result " + worstTake.ToString("0.0") + " ms.");
        }

        /// <summary>
        /// Warms, one piece a frame and only once the prefetch is in (or 12 s on), what a tablet page's first build would
        /// otherwise pay in the world (E's Phase 1 measure: ~209 ms cold against 6-7 ms warm): the eight faces the game's
        /// templates name ([gameui §2.5]; each a TextMeshPro font made from its prefetched field) and the truck catalogue
        /// (21 ms, probe 1862) for Truck Preference, and its studio. Each step's time is logged once, all together.
        /// </summary>
        private IEnumerator Warm()
        {
            float began = Time.realtimeSinceStartup;

            while (EtsEngine.Files == null || (!_prefetched && Time.realtimeSinceStartup - began < 12f))
            {
                yield return new WaitForSeconds(0.25f);
            }

            var steps = new List<KeyValuePair<string, Action>>();

            foreach (string face in EtsGameUi.TemplateFaces)
            {
                string f = face;
                steps.Add(new KeyValuePair<string, Action>(f.Substring(f.LastIndexOf('/') + 1), () => EtsFont.Get(f)));
            }

            steps.Add(new KeyValuePair<string, Action>("truck catalogue", () =>
            {
                if (EtsTruckCatalog.Current == null)
                {
                    EtsTruckCatalog.Load(EtsEngine.Files);
                }
            }));

            // Truck Preference's dark studio (the scene model, a light, a camera and its texture), so the page's first
            // open in the world does not build it; its truck is then built in the factory's 1.5 ms slices.
            steps.Add(new KeyValuePair<string, Action>("truck studio", () => EtsTruckStudio.Get()));

            // The nameplates' star (SDF, painted at 64 x 61) for the first remote truck A draws (Phase 3, D3).
            steps.Add(new KeyValuePair<string, Action>("nameplate star", EtsNameplate.Prepare));

            var parts = new List<string>();
            double total = 0.0, worst = 0.0;
            float held = 0f;

            foreach (KeyValuePair<string, Action> step in steps)
            {
                yield return null;

                // The faces first (the HQ map waits for them, and nothing is on screen yet but the black void). The
                // catalogue and the studio wait for the loading screen: the studio's first build is one ~225 ms frame
                // (the dark scene's models, its light, camera and texture; probe 2045 F, cold) and the catalogue ~25 ms,
                // and neither may land while the player is pointing at the HQ map. Under the loading screen a hitch is
                // what a loading screen is for, and both are still ready before the tablet's first page in the world.
                if (parts.Count == EtsGameUi.TemplateFaces.Length)
                {
                    float waitBegan = Time.realtimeSinceStartup;

                    while (!EtsFlowRules.LateWarm(_phase))
                    {
                        yield return new WaitForSeconds(0.25f);
                    }

                    held = Time.realtimeSinceStartup - waitBegan;
                }

                var watch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    step.Value();
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Flow: warming " + step.Key + " failed (" + e.GetType().Name + ": " + e.Message + ").");
                }

                double ms = watch.Elapsed.TotalMilliseconds;
                total += ms;
                worst = Math.Max(worst, ms);
                parts.Add(step.Key + " " + ms.ToString("0"));

                // The faces made (the first TemplateFaces.Length steps): the HQ map may be built on warm fonts now.
                if (parts.Count == EtsGameUi.TemplateFaces.Length)
                {
                    _facesReady = true;
                }
            }

            _facesReady = true;
            _cachesWarm = true;

            Debug.Log("[SomniumEuroTruck] Flow: page caches warmed in " + steps.Count + " frames, " + total.ToString("0") + " ms in all, worst " + worst.ToString("0") + " ms (" + string.Join(", ", parts.ToArray()) +
                      "); the catalogue and the studio held " + held.ToString("0.0") + " s for the loading screen.");

            // The truck the page will open on, built ahead in slices (the page asks again for whatever it wants).
            EtsTruckStudio studio = EtsTruckStudio.Current;
            int first = EtsPageTruckPreference.DefaultIndex(EtsTruckCatalog.Current);

            if (studio != null && first >= 0)
            {
                studio.Want(first);
            }
        }
    }

    /// <summary>
    /// The HUD master badge on a monitor (PLAN §3.7 item 2): the game's orange star (/material/ui/icon/star_orange.mat,
    /// an SDF icon painted by EtsGameUi.SdfIcon, probe 1895) and "Instance master" in clr_sel, top left, just above the
    /// scene's status line - a screen overlay with the status canvas's own scaler (1920 x 1080, width-matched,
    /// SCENE:1242-1290) and sorting order 101, so it sits on the same grid. In a headset the dash plate says it
    /// (EtsVrDash). Built once; shown and hidden by EtsFlow; nothing per frame. The words are not in the locale.
    /// </summary>
    internal sealed class EtsMasterBadge
    {
        public const string Words = "Instance master"; // not in the locale (PLAN §3.7)

        private GameObject _root;

        public static EtsMasterBadge Create(Transform parent)
        {
            var badge = new EtsMasterBadge();

            if (EtsVr.Present)
            {
                // No screen corner in a headset; the dash plate carries it (EtsVrDash).
                return badge;
            }

            var host = new GameObject("Ets master badge");
            host.transform.SetParent(parent, false);
            var canvas = host.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 101;
            var scaler = host.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0f;

            var row = new GameObject("badge", typeof(RectTransform)).GetComponent<RectTransform>();
            row.SetParent(host.transform, false);
            row.anchorMin = row.anchorMax = new Vector2(0f, 1f);
            row.pivot = new Vector2(0f, 1f);
            row.anchoredPosition = new Vector2(32f, -5f);
            row.sizeDelta = new Vector2(360f, 24f);

            Texture2D star = EtsGameUi.SdfIcon("/material/ui/icon/star_orange.mat", 25, 24);

            if (star != null)
            {
                var icon = new GameObject("star", typeof(RectTransform)).AddComponent<RawImage>();
                icon.rectTransform.SetParent(row, false);
                icon.rectTransform.anchorMin = icon.rectTransform.anchorMax = new Vector2(0f, 0.5f);
                icon.rectTransform.pivot = new Vector2(0f, 0.5f);
                icon.rectTransform.anchoredPosition = Vector2.zero;
                icon.rectTransform.sizeDelta = new Vector2(25f, 24f);
                icon.texture = star;
                icon.raycastTarget = false;
            }

            var text = new GameObject("words", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            text.rectTransform.SetParent(row, false);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(32f, 0f);
            text.rectTransform.offsetMax = Vector2.zero;
            TMP_FontAsset face = EtsFont.Get(EtsFont.Normal);
            if (face != null) text.font = face;
            text.fontSize = 20f;
            text.alignment = TextAlignmentOptions.Left;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.color = EtsGameUi.Colour("clr_sel", new Color(0.98f, 0.69f, 0.01f));
            text.text = Words;
            text.raycastTarget = false;

            badge._root = host;
            host.SetActive(false);
            Debug.Log("[SomniumEuroTruck] Flow: HUD master badge built (" + (star != null ? "star from star_orange.mat" : "NO star - the archives are not mounted") + ").");
            return badge;
        }

        public void SetShown(bool shown)
        {
            if (_root != null && _root.activeSelf != shown)
            {
                _root.SetActive(shown);
            }
        }

        public void Destroy()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }
        }
    }
}
