using System;
using System.Collections.Generic;
using SomniumSpace.Bridge;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// What the app shows about the player and the room (owner E; [tablet §4.3], PLAN.md C18, §7.E). Fed by
    /// EtsSession and EtsProfileStore only: the prototype's "master while not connected" rule is dropped - during
    /// Connecting nobody is master yet (C18).
    /// </summary>
    public interface IEtsTabletData
    {
        string PlayerName { get; }
        bool IsMaster { get; }
        string MasterName { get; }
        long Money { get; }
        int Level { get; }

        /// <summary>The XP bar, 0..1.</summary>
        float LevelProgress { get; }

        /// <summary>"" while playing; otherwise why the app cannot be used yet.</summary>
        string Blocked { get; }
    }

    /// <summary>
    /// The app's data from the session (A) and the career store (G). Where those do not exist - every build with
    /// EtsFeatures.NewFlow off, and the editor - it says what today's front end says: the money is
    /// EtsJobs.Balance (EtsFrontEnd.cs:533), the name "Somnium Driver" (EtsFrontEnd.cs:41), level 0 (a new
    /// profile in G's EtsXpTable, [persist] line 617), no master and nothing blocking. Values that come from the
    /// Bridge are re-read at most every 2 s, and the blocked line only when the phase or the master changes, so a
    /// 4 Hz refresh allocates nothing while nothing changes.
    /// </summary>
    public sealed class EtsTabletSessionData : IEtsTabletData
    {
        public EtsMenu Menu;

        private string _name = "";
        private float _nextName;
        private EtsPhase _blockedPhase = (EtsPhase)(-1);
        private string _blockedMaster;
        private string _blocked = "";

        public string PlayerName
        {
            get
            {
                if (_name.Length == 0 || Time.unscaledTime >= _nextName)
                {
                    _nextName = Time.unscaledTime + 2f;
                    string nick = null;

                    try
                    {
                        nick = SomniumBridge.PlayersContainer?.LocalPlayer?.Properties?.NickName;
                    }
                    catch (Exception)
                    {
                        // No client (editor).
                    }

                    _name = string.IsNullOrEmpty(nick) ? "Somnium Driver" : nick.Replace('|', '/'); // '|' separates a look's values
                }

                return _name;
            }
        }

        public bool IsMaster
        {
            get
            {
                EtsSession session = EtsSession.Instance;
                return session != null && session.IsMaster;
            }
        }

        /// <summary>
        /// The top bar's "Master: {name}" (PLAN §3.7 item 1): the player whose own slot carries the "I am the room master"
        /// bit (EtsPlayerView.IsMaster from EtsNet.Players; PLAN §12 U3 - the server's Fusion has no GetMasterClient, so each
        /// player can only say it of themselves), else the session's (EtsSession.MasterName: the chooser, or A's own pick of
        /// the slot bit). A loop over at most 50 rows by index: no allocation at 4 Hz.
        /// </summary>
        public string MasterName
        {
            get
            {
                EtsNet net = EtsNet.Instance;
                IReadOnlyList<EtsPlayerView> players = net != null ? net.Players : null;

                if (players != null)
                {
                    for (int i = 0; i < players.Count; i++)
                    {
                        EtsPlayerView view = players[i];

                        if (view != null && view.IsMaster && !view.IsLocal && !string.IsNullOrEmpty(view.Name))
                        {
                            return view.Name;
                        }
                    }
                }

                EtsSession session = EtsSession.Instance;
                return session != null ? session.MasterName ?? "" : "";
            }
        }

        public long Money
        {
            get
            {
                EtsProfileStore store = EtsProfileStore.Current;

                if (store != null && store.IsReady)
                {
                    return store.Money;
                }

                EtsJobs jobs = EtsTabletApp.JobsOf(Menu);
                return jobs != null ? jobs.Balance : EtsProfileCodec.StartingMoney;
            }
        }

        public int Level
        {
            get
            {
                EtsProfileStore store = EtsProfileStore.Current;
                return store != null && store.IsReady ? store.Level : 0;
            }
        }

        public float LevelProgress
        {
            get
            {
                EtsProfileStore store = EtsProfileStore.Current;
                return store != null && store.IsReady ? store.LevelProgress : 0f;
            }
        }

        /// <summary>PLAN §5.2's tablet column: blocked from Connecting until InWorld. The wording is not in the locale except "loading".</summary>
        public string Blocked
        {
            get
            {
                EtsSession session = EtsSession.Instance;

                if (session == null)
                {
                    return "";
                }

                EtsPhase phase = session.Current;
                string master = session.MasterName ?? "";

                if (phase == _blockedPhase && master == _blockedMaster)
                {
                    return _blocked;
                }

                _blockedPhase = phase;
                _blockedMaster = master;

                switch (phase)
                {
                    case EtsPhase.Connecting:
                        _blocked = "Connecting ..."; // not in the locale
                        break;
                    case EtsPhase.Offline:
                    case EtsPhase.ChoosingHq:
                        _blocked = "Choose where to start on the map in front of you"; // not in the locale ([tablet §4.3])
                        break;
                    case EtsPhase.WaitingForMaster:
                        _blocked = "Waiting for " + (master.Length > 0 ? master : "the instance master") + " to choose the headquarters"; // not in the locale (PLAN §3.7)
                        break;
                    case EtsPhase.Loading:
                    case EtsPhase.Relocating:
                        _blocked = EtsTabletApp.Word("loading"); // "LOADING ..." (/locale/en_gb, probe 1900)
                        break;
                    default:
                        _blocked = "";
                        break;
                }

                return _blocked;
            }
        }
    }

    /// <summary>
    /// One page of the app, standing for one of the game's screen links in one category of its desktop menu bar
    /// (owner E; [tablet §4.3], PLAN.md C3, §7.E; D's game pages derive from it).
    ///
    /// The app builds a page the first time it is shown in a host (the tablet or the fallback panel) - or earlier,
    /// hidden, one page a frame while the session is still starting (EtsTabletApp.PrebuildWhileStarting), so Build
    /// must not assume it is on screen: Shown says that - and again, from scratch, whenever it moves to another host
    /// (the tablet appears or is re-made): Build must not assume it runs once. Root is the whole 1440 x 900 frame, so a game screen built into it with
    /// using (EtsUiHost.Into(Root)) EtsGameUi.BuildScreen(...) lands exactly where its layout file puts it; the top
    /// bar (853..893) and the bottom bar (7..77, or the menu bar 7..133 on the home page) are drawn over it, where the
    /// game's own screens leave room for them (/ui/quick_job.sii's window is 7..1433 x 80..850).
    /// </summary>
    public abstract class EtsTabletPage
    {
        public abstract string Caption { get; }

        /// <summary>Material path; Drive = /material/ui/drive_n.mat (PLAN C6). Under /material/ui/link/ the game's own look draws it.</summary>
        public abstract string Icon { get; }

        public virtual string Id => GetType().Name;

        /// <summary>drive, jobs, maps, online, personal, fleet, services, browsers, media, quit - or "home" for the desktop itself.</summary>
        public virtual string Category => "jobs";

        /// <summary>
        /// NEW (E): the game's screen link this page stands for ("jobs.quick", "career.progress", "options.selector"),
        /// so it takes that link's place, icon and words in the category's menu. Null: the page is added after the
        /// game's links, with its own Caption and Icon.
        /// </summary>
        public virtual string ScreenLink => null;

        public virtual bool Available => true;
        public virtual string WhyNot => "Not available in this world.";

        protected EtsTabletApp App { get; private set; }

        /// <summary>The whole 1440 x 900 frame of the page, in the host.</summary>
        protected RectTransform Root { get; private set; }

        public void Attach(EtsTabletApp app, RectTransform root)
        {
            App = app;
            Root = root;
            Build();
        }

        protected abstract void Build();

        public virtual void Shown() { }
        public virtual void Hidden() { }

        /// <summary>4 Hz while the page is on screen.</summary>
        public virtual void Tick() { }
    }

    /// <summary>
    /// The "ETS 2" app: Euro Truck Simulator 2's own 1.61 desktop - the top bar, the pages, the bottom menu bar with
    /// the game's ten categories and their screen links - drawn in the game's 1440 x 900 frame, into the Somnium
    /// tablet (EtsTabletLink.Frame) or, when there is none, into a panel of its own (owner E; [tablet §4.3, §4.6],
    /// PLAN.md §3.2, §3.7, §7.E).
    ///
    /// The same game: the menu bar is read from the player's install at build time - the categories, their order
    /// and separators from /def/ui_desktop_ind_full.sii and its includes, each screen link's icon and words from
    /// /ui/!desc.sii (probes 1900-1902: 10 categories in 3 groups, 40 links on 1.61) - and every rectangle is the game's
    /// (EtsTabletLayout). A link a page stands for is live; the others are shown the way the game shows an
    /// unavailable link, greyed at clr_white_alpha_25 (Q-E3), and a category with no live link is greyed too.
    /// Each element is drawn by the game's own look through EtsGameUi.Template (owner D); where that returns
    /// nothing (before D's templates, or a look that fails) a plain drawing in the game's measured colours
    /// (/locale/ui_colors.sui, probe 1900) stands in, so the shell always works.
    ///
    /// Hosts: linked, the app lives in the tablet and the client opens and closes it; unlinked, after
    /// FallbackAfter seconds of a local player with no tablet (at once in the editor), the panel is
    /// EtsGameUi.CreateCanvas - a screen overlay on a monitor, a room panel in a headset (EtsVrPanel), pinned in
    /// front of the driver's eye while seated. The panel opens with Tab (only when no tablet is linked: Tab is
    /// the client's tablet key) and, in the new flow, with EtsMenu's Escape / left X.
    ///
    /// While the app is in front of a driver the truck takes no keys and no pedals (as EtsMenu.Open does); in a
    /// headset the pedals come back only once both triggers are let go, so the trigger that pressed a tablet
    /// button never becomes throttle ([tablet §4.5]).
    ///
    /// With EtsFeatures.NewFlow off nothing creates this component (EtsNet.Awake calls Ensure only in the new flow);
    /// the editor menu "Somnium Euro Truck/Tablet/..." adds it in Play mode to look at the shell.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EtsTabletApp : MonoBehaviour
    {
        public const string Owned = EtsTabletLink.Owned;
        public const string HomeId = "home";

        /// <summary>D's Company page (EtsPageTruckPreference.Id): where the first open in the world lands, and the Drive page's "Choose another truck".</summary>
        public const string TruckPreferenceId = "truck_preference";

        public static EtsTabletApp Current { get; private set; }

        /// <summary>Whether the app is on screen: in an open tablet, or the fallback panel open.</summary>
        public static bool IsVisible => Current != null && Current.Visible;

        public EtsTabletLink Link;
        public IEtsTabletData Data;
        public EtsMenu Menu;

        [Tooltip("Seconds after the local player exists without a Somnium tablet before the app uses its own panel (PLAN C19; at once in the editor).")]
        public float FallbackAfter = 20f;

        public bool Visible => Linked ? Link.AppOpen && Link.TabletOpen : _padOpen;

        /// <summary>The fallback panel exists (no tablet).</summary>
        public bool HasPanel => _pad != null;

        public IReadOnlyList<EtsTabletPage> Pages => _pageList;

        private bool Linked => Link != null && Link.IsLinked;

        /// <summary>
        /// The layer everything the app draws goes on as it is made: the client's UI layer when linked (the XR ray tests
        /// each graphic's layer; the link's own pass only runs at 4 Hz, so a new menu or nav bar was on the default layer,
        /// and not pressable with the ray, for up to 250 ms: checker probe 2081), EtsLayers.Ui in the world's own panel.
        /// </summary>
        private int UiLayer => Linked ? Link.UiLayer : EtsLayers.Ui;

        /// <summary>Gets or adds the app and its link on the bootstrap object (EtsNet.Awake, PLAN §3.2) and makes the link a real one.</summary>
        public static EtsTabletApp Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsTabletApp app = host.GetComponent<EtsTabletApp>();

            if (app == null)
            {
                app = host.AddComponent<EtsTabletApp>();
            }

            EtsTabletLink link = host.GetComponent<EtsTabletLink>();

            if (link == null)
            {
                link = host.AddComponent<EtsTabletLink>();
            }

            // Phase 0's probe link (EtsNet.Awake, LogOnly) becomes the real one. Its U4 report is still written, as
            // "Tablet: linked - ..." when it links (EtsTabletLink.TryLink). The link itself also wants NewFlow.
            link.LogOnly = false;
            link.enabled = true;
            app.Use(link);

            // The game's radio (Media > Radio, Drive's "Radio on / off") lives beside the app; it stays off until the
            // player turns it on.
            EtsRadio.Ensure(host);
            return app;
        }

        // ------------------------------------------------------------------ state

        private sealed class Slot
        {
            public EtsTabletPage Page;
            public RectTransform Root;
            public bool Failed;

            // Tick threw once in this build: logged once, not 4 times a second (checker probe 1916 measured 4 lines in
            // 0.6 s from one page before this). Cleared when the page is built again.
            public bool TickFailed;
        }

        private sealed class Category
        {
            public string Key;      // unit name: drive, jobs, maps, online, personal, fleet, services, browsers, media, quit
            public string Icon;     // /material/ui/link/<Icon>.mat
            public string Name;     // the locale's words
            public bool NoMenu;     // type no_menu (Drive)
            public int Group;       // groups are split by the separators
            public readonly List<MenuLink> Links = new List<MenuLink>();
            public bool Live;
        }

        private sealed class MenuLink
        {
            public string ScreenLink;
            public string Icon;     // link name, or a full material path for a page's own icon
            public string Label;
            public Slot Slot;       // null: not in this world
            public bool Live => Slot != null && !Slot.Failed && Slot.Page.Available;
        }

        private sealed class Section
        {
            public string Look;
            public float Left, Right;
            public string Value;
            public RectTransform Made;
            public Action Click;
            public Func<Section, RectTransform> Fallback;
        }

        private readonly List<EtsTabletPage> _pageList = new List<EtsTabletPage>();
        private readonly List<Slot> _slots = new List<Slot>();
        private readonly List<Category> _categories = new List<Category>();
        private Slot _current;
        private Category _currentCategory;
        private bool _onHome = true;
        private string _wanted;

        private RectTransform _host;
        private RectTransform _pagesRoot;
        private RectTransform _chrome;
        private RectTransform _homeRoot;
        private RectTransform _menuBar;
        private RectTransform _navBar;
        private RectTransform _popup;
        private Category _popupCategory;
        private RectTransform _blockedPanel;
        private TextMeshProUGUI _blockedText;
        private TextMeshProUGUI _note;
        private readonly List<Section> _sections = new List<Section>();
        private bool _builtWithMenu;
        private bool _builtWithLocale;

        private GameObject _pad;
        private RectTransform _padFrame;
        private RectTransform _padCanvasRect;
        private Vector2 _padCanvasSize;
        private bool _padOpen;
        private Transform _padAnchor;
        private bool _padSeated;
        private EtsTruckController _padTruck;
        private int _padSeat;
        private float _waitingSince = -1f;
        private bool _hadPlayer;
        private CursorLockMode _cursorLockWas;
        private bool _cursorVisibleWas;

        private float _nextTick;


        private bool _inputWasEnabled;
        private EtsTruckController _heldTruck;
        private bool _holdReported;
        private string _toast = "";
        private float _toastUntil;
        private bool _ending;
        private bool _started;
        private bool _openPending;

        // Phase 2: the first open in the world, the HUD hint, the own truck's arrival.
        private bool _landed;
        private bool _ownSubscribed;
        private float _ownSince = -1f;
        private bool _seatedInOwn;
        private GameObject _hint;
        private RectTransform _hintPlate;
        private TextMeshProUGUI _hintText;
        private bool _hintVr;
        private string _hintLogged; // the words last logged (EtsTabletLayout.HintText returns constants: compared by reference)
        private Transform _hintHead;
        private float _nextHintHead;
        private bool _hintPlaced;
        private bool _hintFollowing;

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            // The app of this visit (statics outlive a world unload, [tablet §2.8]); replaced, never trusted.
            Current = this;
        }

        private void Start()
        {
            _started = true;

            if (Menu == null)
            {
                Menu = GetComponent<EtsMenu>();
            }

            if (Menu == null)
            {
                Menu = FindFirstObjectByType<EtsMenu>();
            }

            if (Data == null)
            {
                Data = new EtsTabletSessionData { Menu = Menu };
            }

            if (Link == null)
            {
                Use(GetComponent<EtsTabletLink>());
            }

            // The world's own pages (E), then the game's (D). AddPage ignores an Id already present, so D's EtsFlow may
            // add them too.
            AddPage(new EtsPageDrive());
            AddPage(new EtsPageConvoys());
            AddPage(new EtsPageSystem());
            AddPage(new EtsPageLeave());
            AddPage(new EtsPageRadio());
            AddPage(new EtsPageService()); // Services - Vehicle Diagnostics: the damage report and the repair (EtsDamage.cs, 2026-10-04)

            // The own truck arriving (EtsNet.SpawnOwnTruck -> EtsLocalTruck.SetOwn, owners A and C) moves the HUD hint on to
            // "get in" and says so on the app's notification line. A static event: unsubscribed in OnDestroy.
            EtsLocalTruck.OwnChanged += OnOwnChanged;
            _ownSubscribed = true;

            if (EtsLocalTruck.Own != null)
            {
                _ownSince = Time.unscaledTime;
            }

            try
            {
                foreach (EtsTabletPage page in EtsGamePages.Create())
                {
                    AddPage(page);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Tablet: the game's pages could not be made (" + e.GetType().Name + ": " + e.Message + ").");
            }

            Rehost();
        }

        private void OnDestroy()
        {
            _ending = true;
            ReleaseHeld();

            if (_padOpen)
            {
                RestoreCursor();
            }

            if (Link != null)
            {
                Link.LinkChanged -= OnLinkChanged;
                Link.AppShown -= OnAppShown;
            }

            if (_ownSubscribed)
            {
                EtsLocalTruck.OwnChanged -= OnOwnChanged;
                _ownSubscribed = false;
            }

            if (_pad != null) Destroy(_pad);
            if (_padAnchor != null) Destroy(_padAnchor.gameObject);
            if (_hint != null) Destroy(_hint);
            if (_landing != null) Destroy(_landing);

            if (_reader != null)
            {
                _reader.Dispose(); // the thread ends at its next wake; nothing of it touches this object
                _reader = null;
            }

            if (_isoReader != null)
            {
                _isoReader.Dispose();
                _isoReader = null;
            }

            if (Current == this)
            {
                Current = null;
            }
        }

        /// <summary>Which link this app follows (Ensure calls it; Start finds the one beside it otherwise).</summary>
        public void Use(EtsTabletLink link)
        {
            if (link == Link)
            {
                return;
            }

            if (Link != null)
            {
                Link.LinkChanged -= OnLinkChanged;
                Link.AppShown -= OnAppShown;
            }

            Link = link;

            if (Link != null)
            {
                Link.LinkChanged += OnLinkChanged;
                Link.AppShown += OnAppShown;
            }

            if (_started)
            {
                Rehost();
            }
        }

        // ------------------------------------------------------------------ the contract

        /// <summary>Adds a page (ignored when a page with the same Id is there). Pages are built when first shown.</summary>
        public void AddPage(EtsTabletPage page)
        {
            if (page == null)
            {
                return;
            }

            foreach (EtsTabletPage existing in _pageList)
            {
                if (existing.Id == page.Id)
                {
                    return;
                }
            }

            _pageList.Add(page);
            _slots.Add(new Slot { Page = page });

            if (_host != null)
            {
                // The page's link joins its category at the next 4 Hz tick: the chrome is rebuilt ONCE for however many
                // pages arrive together. Rebuilding it here cost 26-30 ms per AddPage with the 1.61 install mounted
                // (checker probe 1917), so six pages added in one frame were a ~170 ms hitch. The page itself can be
                // shown at once (Show finds its slot); its nav bar appears with the rebuild.
                _chromeDirty = true;
            }
        }

        private bool _chromeDirty;

        /// <summary>Shows a page by its Id, or the desktop for "home" / null. Before a host exists it is remembered.</summary>
        public void Show(string pageId)
        {
            if (_host == null)
            {
                _wanted = pageId;
                return;
            }

            if (string.IsNullOrEmpty(pageId) || pageId == HomeId)
            {
                ShowHome();
                return;
            }

            Slot slot = SlotOf(pageId);

            if (slot == null)
            {
                Debug.LogWarning("[SomniumEuroTruck] Tablet: no page '" + pageId + "'.");
                return;
            }

            if (!slot.Page.Available)
            {
                Toast(slot.Page.WhyNot);
                return;
            }

            ShowSlot(slot);
        }

        /// <summary>A line in the app's notification area for 10 s, and on the HUD (EtsJobs.Tell), and in the log.</summary>
        public void Toast(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _toast = text;
            _toastUntil = Time.unscaledTime + 10f;

            if (_note != null)
            {
                _note.text = text;
            }

            EtsJobs jobs = JobsOf(Menu);

            if (jobs != null)
            {
                jobs.Tell(text);
            }

            EtsGameUi.Note("tablet says: " + text);
        }

        /// <summary>
        /// Brings the app up at a page (null: where it was). Linked, the app is put on the tablet's screen; the
        /// tablet itself is the player's to open (Q-E1: no reflection), so a HUD line says how when it is closed.
        /// Unlinked, the fallback panel opens.
        /// </summary>
        public void Open(string pageId = null)
        {
            if (Linked)
            {
                Link.OpenApp();

                if (!Link.TabletOpen)
                {
                    Toast("Open your tablet (Tab, or tap B or Y) for ETS 2"); // not in the locale (PLAN §5.2)
                }
            }
            else if (_pad != null)
            {
                SetPad(true, "world");
            }
            else
            {
                _openPending = true; // no host yet: open as soon as there is one (the tablet links, or the panel is made)
            }

            if (pageId != null)
            {
                Show(pageId);
            }
        }

        public void Close()
        {
            if (Linked)
            {
                Link.CloseApp();
            }
            else
            {
                SetPad(false, "world");
            }
        }

        public void Toggle(string why)
        {
            if (Linked)
            {
                if (Link.AppOpen) Link.CloseApp(); else Open();
            }
            else if (_pad != null)
            {
                SetPad(!_padOpen, why);
            }
        }

        // ------------------------------------------------------------------ "Go to" (Convoys, Phase 3)

        private GameObject _landing;
        private float _landingSince;
        private string _landingFor;

        /// <summary>
        /// Stands the local player at `spot` facing `yaw` (EtsVr.Teleport: the Bridge's DoTeleportToPoint, which moves the
        /// body and its controller together). The ground under a remote truck may not be on this client yet: the map streams
        /// round the player on foot (EtsTruckSeat.StreamFocus, owner B), so a player sent kilometres away would arrive before
        /// the road and fall. When no ground is under the spot (the ray EtsTruckSeat.StandBeside uses), a landing pad - a
        /// bare 10 x 10 m collider on Environment (23: the avatar stands on it, as on the HQ void's floor, and it is outside
        /// EtsLayers.GroundMask, so no truck, traffic or map ray ever takes it for ground) - holds the player at the truck's
        /// own height (EtsTruckController.PlaceOnGround stands a truck 0.08 m over its road) until the road streams in under
        /// it or EtsTabletLayout.GoToPadSeconds pass. False with no Somnium player (the editor).
        /// </summary>
        public bool GoTo(Vector3 spot, float yaw, string what)
        {
            if (EtsVr.Motion == null)
            {
                return false;
            }

            DropLanding("replaced"); // first, so the ray below cannot take an earlier pad for ground
            int solid = EtsLayers.GroundMask | (1 << EtsLayers.Environment);
            Vector3 standAt = spot + Vector3.up * 0.05f;
            bool ground = Physics.Raycast(spot + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 30f, solid, QueryTriggerInteraction.Ignore);

            if (ground)
            {
                standAt = hit.point + Vector3.up * 0.05f;
            }
            else
            {
                _landing = new GameObject("Ets go-to landing pad");
                _landing.layer = EtsLayers.Environment;
                _landing.transform.SetPositionAndRotation(spot + Vector3.down * 0.2f, Quaternion.Euler(0f, yaw, 0f));
                var box = _landing.AddComponent<BoxCollider>();
                box.size = new Vector3(10f, 0.4f, 10f);
                _landingSince = Time.unscaledTime;
                _landingFor = what;
                Physics.SyncTransforms(); // the pad is solid before the teleport lands on it
            }

            bool moved = EtsVr.Teleport(standAt, yaw);
            Debug.Log("[SomniumEuroTruck] Tablet: Convoys - Go to " + what + ": stood at (" + standAt.x.ToString("0.0") + ", " + standAt.y.ToString("0.0") + ", " + standAt.z.ToString("0.0") + ")" +
                      (ground ? "." : ", on a landing pad until the road there streams in.") + (moved ? "" : " The Bridge would not move the player."));

            if (!moved)
            {
                DropLanding("not moved");
            }

            return moved;
        }

        /// <summary>4 Hz: the landing pad goes once the map's own ground is under it, or after GoToPadSeconds.</summary>
        private void KeepLanding()
        {
            if (_landing == null)
            {
                return;
            }

            Vector3 centre = _landing.transform.position;

            if (Physics.Raycast(centre + Vector3.up * 3.2f, Vector3.down, 10f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
            {
                DropLanding("the road streamed in after " + (Time.unscaledTime - _landingSince).ToString("0.0") + " s");
            }
            else if (Time.unscaledTime - _landingSince > EtsTabletLayout.GoToPadSeconds)
            {
                DropLanding("no road after " + EtsTabletLayout.GoToPadSeconds.ToString("0") + " s");
            }
        }

        private void DropLanding(string why)
        {
            if (_landing == null)
            {
                return;
            }

            _landing.SetActive(false); // out of the physics scene now; Destroy waits for the end of the frame
            Destroy(_landing);
            _landing = null;
            Debug.Log("[SomniumEuroTruck] Tablet: Convoys - the landing pad at " + _landingFor + " is gone: " + why + ".");
        }

        internal static EtsJobs JobsOf(EtsMenu menu)
        {
            EtsJobs jobs = EtsJobs.Instance;
            return jobs != null ? jobs : menu != null ? menu.Jobs : null;
        }

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            bool linked = Linked;

            // In a headset the seated panel is pinned to the truck (KeepPadPlaced -> EtsVrPanel.AttachTo parents it to
            // the truck), so a truck despawned under it takes the panel and its pages along. Unity's null test sees that;
            // the reference is still set. Forget the dead panel, so Visible does not stay true with nothing on screen
            // (keys held, the door refused), and open the next one, which WaitForTabletOrFallBack makes at once.
            if (_pad == null && !ReferenceEquals(_pad, null))
            {
                PadLost();
            }

            if (!linked && _pad == null)
            {
                WaitForTabletOrFallBack();
            }

            // Only while no Somnium tablet is linked: then Tab (the client's tablet key) opens ours.
            if (!linked && _pad != null && Input.GetKeyDown(KeyCode.Tab))
            {
                SetPad(!_padOpen, "Tab");
            }

            HoldInputWhileVisible();
            PrebuildWhileStarting();

            if (_hintVr && _hint != null && _hint.activeSelf)
            {
                PlaceHintInFrontOfTheHead(); // every frame: a line that trails the head by a tick would judder in a headset
            }

            if (Time.unscaledTime < _nextTick)
            {
                return;
            }

            _nextTick = Time.unscaledTime + 0.25f;
            LandFirstOpen();
            UpdateHint();
            KeepLanding();

            if (_isoReader != null)
            {
                _isoReader.Drain(Always); // the flag's country, when D's catalogue did not have it (FlagFromDefs)
            }

            if (_reader != null)
            {
                _reader.Drain(Always);
            }
            else if (EtsEngine.Files != null && (MenuCache == null || MenuFiles != EtsEngine.Files || (!EtsLocale.Loaded && LocaleTriedFor != EtsEngine.Files)))
            {
                ReadInTheBackground();
            }

            if (_host != null && (_chromeDirty || (!_builtWithMenu && MenuCache != null && MenuFiles == EtsEngine.Files) || (!_builtWithLocale && EtsLocale.Loaded)))
            {
                RebuildChrome(); // pages added since, or the game's menu or its words arrived after the first build
            }

            if (_toast.Length > 0 && Time.unscaledTime > _toastUntil)
            {
                _toast = "";

                if (_note != null)
                {
                    _note.text = "";
                }
            }

            if (_padOpen)
            {
                KeepPadPlaced();
            }

            if (!Visible || _host == null)
            {
                return;
            }

            RefreshChrome();

            // A page that failed to build shows its "not available" line and is not ticked: its fields were never made.
            if (_current != null && !_current.Failed && _current.Root != null && _current.Root.gameObject.activeSelf)
            {
                try
                {
                    _current.Page.Tick();
                }
                catch (Exception e)
                {
                    if (!_current.TickFailed)
                    {
                        _current.TickFailed = true;
                        Debug.LogWarning("[SomniumEuroTruck] Tablet: page " + _current.Page.Id + " Tick: " + e.GetType().Name + ": " + e.Message + " (reported once).");
                    }
                }
            }
        }

        // The game's menu, once per client session and install (statics outlive a visit; the install does not change).
        private static List<EtsMenuCategory> MenuCache;
        private static EtsFileSystem MenuFiles;
        private static EtsFileSystem LocaleTriedFor;
        private static readonly Func<bool> Always = () => true;
        private EtsWorker _reader;

        /// <summary>
        /// The menu (EtsTabletLayout.ReadMenu: 19 ms cold, 2.3-2.5 ms warm in mono, probe 1903) and, if nobody has yet,
        /// the locale (33-37 ms, EtsLocale's own measure) are read on a worker thread of the app's own, never in a frame:
        /// both are pure (EtsFileSystem.ReadText and EtsSii only; the archive's one stream read is locked, EtsWorker's
        /// note) and EtsLocale.Load swaps whole tables. Until they arrive the shell stands on probe 1900's list.
        /// </summary>
        private void ReadInTheBackground()
        {
            EtsFileSystem files = EtsEngine.Files;
            bool menu = MenuCache == null || MenuFiles != files;
            bool words = !EtsLocale.Loaded && LocaleTriedFor != files;
            LocaleTriedFor = files;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _reader = new EtsWorker("Ets tablet reader");
            _reader.Post(() =>
            {
                List<EtsMenuCategory> read = menu ? EtsTabletLayout.ReadMenu(files) : null;

                if (words)
                {
                    EtsLocale.Load(files);
                }

                return read;
            }, (result, failure) =>
            {
                watch.Stop();

                if (failure != null)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Tablet: reading the game's menu failed (" + failure.GetType().Name + ": " + failure.Message + ") - probe 1900's list stays.");
                    MenuCache = EtsTabletLayout.MeasuredMenu();
                    MenuFiles = files;
                }
                else if (menu)
                {
                    MenuCache = result as List<EtsMenuCategory> ?? EtsTabletLayout.MeasuredMenu();
                    MenuFiles = files;
                }

                int links = 0;

                foreach (EtsMenuCategory category in MenuCache)
                {
                    links += category.Links.Count;
                }

                Debug.Log("[SomniumEuroTruck] Tablet: the game's menu (" + MenuCache.Count + " categories, " + links + " links)" + (words ? " and words (locale " + (EtsLocale.Loaded ? "loaded" : "NOT loaded") + ")" : "") +
                          " read in " + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms on " + (_reader != null && _reader.Threaded ? "a worker thread" : "the main thread (no thread)") + ".");
                EtsWorker done = _reader;
                _reader = null;
                done?.Dispose();
            });
        }

        private void WaitForTabletOrFallBack()
        {
            if (Application.isEditor)
            {
                MakePad(); // no client, no tablet
                return;
            }

            bool player;

            try
            {
                player = SomniumBridge.PlayersContainer?.LocalPlayer != null; // SomniumBridge.HasInstance is internal (CS0122)
            }
            catch (Exception)
            {
                player = false;
            }

            // The tablet comes with the local player: the clock starts when the player exists (or at the app's start
            // if one never does), and the panel is made FallbackAfter seconds later if nothing linked by then.
            if (_waitingSince < 0f || (player && !_hadPlayer))
            {
                _waitingSince = Time.unscaledTime;
            }

            _hadPlayer |= player;

            if (Time.unscaledTime - _waitingSince > FallbackAfter)
            {
                MakePad();
            }
        }

        // ------------------------------------------------------------------ hosts

        private void OnLinkChanged()
        {
            if (_ending)
            {
                return;
            }

            if (!Linked && _pad == null)
            {
                // The tablet went (an avatar or mode change re-makes it, TabletFeature.cs:47-50): give the new one the
                // same FallbackAfter to be found before the panel is made.
                _waitingSince = Time.unscaledTime;
            }

            Rehost();
        }

        private void OnAppShown(bool shown)
        {
            if (_ending)
            {
                return;
            }

            if (shown && _host == null)
            {
                Rehost();
            }

            if (shown && _current == null && !_onHome)
            {
                ShowHome();
            }

            ClosePopup();

            if (shown)
            {
                LandFirstOpen();
                UpdateHint(); // off at once, not at the next tick
            }
        }

        /// <summary>
        /// The first time the app is on screen in the world and not blocked, it opens at Company - Truck Preference when
        /// the player has no truck yet (EtsTabletLayout.LandOnTruckPreference; the task: "the first open lands on Company >
        /// Truck Preference"). Once per visit; after that the app opens where the player left it.
        /// </summary>
        private void LandFirstOpen()
        {
            if (_landed || _host == null || !Visible)
            {
                return;
            }

            EtsSession session = EtsSession.Instance;
            bool blocked = Data != null && !string.IsNullOrEmpty(Data.Blocked);

            if (session != null && blocked)
            {
                return; // the first open that is not blocked lands
            }

            _landed = true;

            if (!EtsTabletLayout.LandOnTruckPreference(session != null, blocked, EtsLocalTruck.Own != null))
            {
                return;
            }

            Slot slot = SlotOf(TruckPreferenceId);

            if (slot == null)
            {
                // D's page under another Id: the first live link of Company (the game's "fleet").
                foreach (Category category in _categories)
                {
                    if (category.Key != "fleet")
                    {
                        continue;
                    }

                    foreach (MenuLink link in category.Links)
                    {
                        if (slot == null && link.Live)
                        {
                            slot = link.Slot;
                        }
                    }
                }
            }

            if (slot == null || !slot.Page.Available)
            {
                Debug.Log("[SomniumEuroTruck] Tablet: first open in the world - no Truck Preference page to open, the desktop stays.");
                return;
            }

            ShowSlot(slot);
            Debug.Log("[SomniumEuroTruck] Tablet: first open in the world - Company > " + slot.Page.Caption + " (no truck yet).");
        }

        /// <summary>A page with this Id is in the app (the Drive page asks before offering Company's truck choice).</summary>
        public bool HasPage(string pageId)
        {
            Slot slot = SlotOf(pageId);
            return slot != null && !slot.Failed && slot.Page.Available;
        }

        private void OnOwnChanged(EtsTruckRig rig)
        {
            if (_ending || !EtsFeatures.NewFlow)
            {
                return;
            }

            _seatedInOwn = false;
            _ownSince = rig != null ? Time.unscaledTime : -1f;

            if (rig != null)
            {
                // Not in the locale. The game's own way back to the truck is its Drive button, first in the menu bar.
                Toast(EtsVr.Present ? "Your truck is ready - Drive, or point at it and pull the trigger" : "Your truck is ready - Drive, or click it to get in");
            }

            UpdateHint();
        }

        // ------------------------------------------------------------------ the HUD hint (Phase 2; Q-E1: never opening the tablet)

        // Desktop: a strip at the top centre of the screen, in the 1440 x 900 frame the HUD scales with. Headset: the same
        // strip 1.3 m ahead and 0.35 m below the eyes (15 degrees down), 0.8 m wide (27 degrees; the Big face's ~24 units
        // are ~1 degree tall there), following the head lazily - it stays put until the head has turned 30 degrees away or
        // moved 0.7 m, then glides back in front - because a line welded to the head cannot be read (EtsVrPanel's note).
        private const float HintWidth = 820f, HintHeight = 54f, HintTopMargin = 18f;
        private const int HintOrder = 90; // under the world's panels (350), the loading screens (500) and the HQ stage
        private const float HintMetres = 0.8f, HintDistance = 1.3f, HintDrop = 0.35f;
        private const float HintFollowAngle = 30f, HintFollowDistance = 0.7f, HintCatchUp = 4f;

        /// <summary>
        /// Four times a second (and when the app shows or the own truck changes): which line the HUD shows, if any
        /// (EtsTabletLayout.Hint / HintText). The line is made the first time it is needed and hidden, not destroyed, when
        /// it is not; its text is set only when it changes, so this allocates nothing in steady state. The words are logged
        /// each time they change - a stage, the host (tablet or panel), the headset - ("Tablet: HUD hint - ...").
        /// </summary>
        private void UpdateHint()
        {
            EtsTruckRig own = EtsLocalTruck.Own;
            EtsTruckSeat seat = Menu != null ? Menu.Seat : null;

            if (own != null && seat != null && seat.Seated)
            {
                _seatedInOwn = true;
            }

            EtsSession session = EtsSession.Instance;
            EtsTabletLayout.HintStage stage = EtsTabletLayout.Hint(EtsFeatures.NewFlow, session != null && session.Current == EtsPhase.InWorld, Visible,
                own != null, Menu != null && Menu.OnFoot, _seatedInOwn, _ownSince >= 0f ? Time.unscaledTime - _ownSince : 0f);

            if (stage == EtsTabletLayout.HintStage.None)
            {
                if (_hint != null && _hint.activeSelf)
                {
                    _hint.SetActive(false);
                }

                return;
            }

            bool vr = EtsVr.Present;
            string text = EtsTabletLayout.HintText(stage, vr, !Linked && _pad != null);

            if (_hint == null || _hintVr != vr)
            {
                BuildHint(vr);
            }

            if (_hintText.text != text)
            {
                _hintText.text = text;
            }

            if (!_hint.activeSelf)
            {
                _hintPlaced = false;
                _hint.SetActive(true);
            }

            if (!ReferenceEquals(text, _hintLogged))
            {
                _hintLogged = text;
                Debug.Log("[SomniumEuroTruck] Tablet: HUD hint - \"" + text + "\" (" + (vr ? "headset" : "screen") + ").");
            }
        }

        private void BuildHint(bool vr)
        {
            if (_hint != null)
            {
                Destroy(_hint);
            }

            _hintVr = vr;
            _hint = new GameObject(Owned + "Hint", typeof(RectTransform));
            _hint.SetActive(false);
            var canvas = _hint.AddComponent<Canvas>(); // no raycaster: a line to read, never something that takes a click

            if (vr)
            {
                canvas.renderMode = RenderMode.WorldSpace;
                _hintPlate = (RectTransform)_hint.transform;
                _hintPlate.sizeDelta = new Vector2(HintWidth, HintHeight);
                _hintPlate.localScale = Vector3.one * (HintMetres / HintWidth);
            }
            else
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = HintOrder;
                var scaler = _hint.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(EtsTabletLayout.FrameWidth, EtsTabletLayout.FrameHeight);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f; // as EtsGameUi.CreateCanvas
                _hintPlate = new GameObject("plate", typeof(RectTransform)).GetComponent<RectTransform>();
                _hintPlate.SetParent(_hint.transform, false);
                _hintPlate.anchorMin = _hintPlate.anchorMax = _hintPlate.pivot = new Vector2(0.5f, 1f);
                _hintPlate.anchoredPosition = new Vector2(0f, -HintTopMargin);
                _hintPlate.sizeDelta = new Vector2(HintWidth, HintHeight);
            }

            // The game's desktop plate colour and its selected (amber) text colour, from /locale/ui_colors.sui.
            Plate(_hintPlate, Colour("clr_desktop_bg_main", DesktopBackground), false);
            RectTransform line = EtsGameUi.Place(_hintPlate, "line", 16f, HintWidth - 16f, 0f, HintHeight);
            _hintText = Text(line, "", EtsFont.Big, TextAlignmentOptions.Center, Colour("clr_sel", SelectedText));

            if (vr)
            {
                EtsLayers.SetRecursively(_hint.transform, EtsLayers.Ui);
            }
        }

        /// <summary>Headset only, every frame while shown: the lazy follow described at HintMetres. No allocation.</summary>
        private void PlaceHintInFrontOfTheHead()
        {
            if (_hintHead == null || Time.unscaledTime >= _nextHintHead)
            {
                _nextHintHead = Time.unscaledTime + 0.5f; // EtsVr.Head may search the scene when Camera.main is not live
                Camera head = EtsVr.Head();
                _hintHead = head != null ? head.transform : null;
            }

            if (_hintHead == null)
            {
                return;
            }

            Vector3 eye = _hintHead.position;
            Vector3 forward = _hintHead.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            Vector3 want = eye + forward * HintDistance + Vector3.down * HintDrop;
            Transform t = _hint.transform;

            if (!_hintPlaced)
            {
                t.position = want;
                _hintPlaced = true;
                _hintFollowing = false;
            }
            else
            {
                Vector3 flat = t.position - eye;
                flat.y = 0f;
                float away = flat.sqrMagnitude > 1e-4f ? Vector3.Angle(flat, forward) : 180f;

                if (!_hintFollowing && (away > HintFollowAngle || (t.position - want).sqrMagnitude > HintFollowDistance * HintFollowDistance))
                {
                    _hintFollowing = true;
                }

                if (_hintFollowing)
                {
                    t.position = Vector3.Lerp(t.position, want, 1f - Mathf.Exp(-HintCatchUp * Time.unscaledDeltaTime));

                    if ((t.position - want).sqrMagnitude < 0.02f * 0.02f)
                    {
                        _hintFollowing = false;
                    }
                }
            }

            Vector3 facing = t.position - eye;

            if (facing.sqrMagnitude > 1e-6f)
            {
                t.rotation = Quaternion.LookRotation(facing, Vector3.up); // a world canvas reads from behind its forward
            }
        }

        /// <summary>Builds into the tablet when linked, else into the panel; tears the old host's objects down first.</summary>
        private void Rehost()
        {
            if (_ending)
            {
                return;
            }

            RectTransform host = Linked ? Link.Frame : _padFrame;

            if (host == _host && host != null)
            {
                return;
            }

            TearDown();
            _host = host;

            if (host == null)
            {
                return;
            }

            if (Linked && _pad != null)
            {
                // The real tablet turned up after all.
                SetPad(false, "tablet linked");
                Destroy(_pad);
                _pad = null;
                _padFrame = null;
                Debug.Log("[SomniumEuroTruck] Tablet: the Somnium tablet turned up - the world's own panel is gone.");
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            _pagesRoot = FullFrame(host, Owned + "Pages");
            _homeRoot = FullFrame(_pagesRoot, Owned + "Desktop");
            BuildHome(_homeRoot);
            _chrome = FullFrame(host, Owned + "Chrome");
            BuildChrome();
            watch.Stop();

            string wanted = _wanted;
            _wanted = null;
            Slot again = _current;
            _current = null;

            if (wanted != null)
            {
                Show(wanted);
            }
            else if (again != null && !_onHome)
            {
                ShowSlot(again);
            }
            else
            {
                ShowHome();
            }

            EtsLayers.SetRecursively(host, UiLayer); // linked, the client's UI layer (EtsTabletLink.UiLayer) at once

            Debug.Log("[SomniumEuroTruck] Tablet: app built into " + (Linked ? "the Somnium tablet" : "the world's own panel") + " in " + watch.Elapsed.TotalMilliseconds.ToString("0.0") +
                      " ms: " + _categories.Count + " categories, " + CountLinks(true) + " live links of " + CountLinks(false) + ", " + _slots.Count + " pages (each built when first shown, or before while the session starts), menu " +
                      (EtsEngine.Files != null ? "from the install" : "from probe 1900's list (archives not mounted)") + ", words " + (EtsLocale.Loaded ? "from the locale" : "measured fallback") + ".");

            if (_openPending)
            {
                _openPending = false;
                Open();
            }
        }

        private void TearDown()
        {
            if (_current != null)
            {
                SafeHidden(_current);
            }

            foreach (Slot slot in _slots)
            {
                slot.Root = null; // destroyed with the pages root; built again when next shown
                slot.Failed = false;
            }

            if (_pagesRoot != null) { _pagesRoot.gameObject.SetActive(false); Destroy(_pagesRoot.gameObject); }
            if (_chrome != null) { _chrome.gameObject.SetActive(false); Destroy(_chrome.gameObject); }
            _pagesRoot = null;
            _chrome = null;
            _homeRoot = null;
            _menuBar = null;
            _navBar = null;
            _popup = null;
            _popupCategory = null;
            _blockedPanel = null;
            _blockedText = null;
            _note = null;
            _sections.Clear();
            _host = null;
        }

        private void MakePad()
        {
            // A canvas of our own in the 1440 x 900 frame: a screen overlay on a monitor, a room panel in a headset
            // (EtsGameUi.CreateCanvas -> EtsVrPanel.Adopt), as every world screen is today.
            Canvas canvas = EtsGameUi.CreateCanvas(Owned + "Panel", 350);
            _pad = canvas.gameObject;
            _padCanvasRect = (RectTransform)_pad.transform;

            if (!EtsVr.Present)
            {
                // A dimming sheet over the whole screen that also takes the pointer, so a click that misses the
                // panel never reaches the world (EtsMenu's Shade, EtsMenu.cs:312-323).
                RectTransform shade = EtsGameUi.Place(_pad.transform, "shade", 0f, 1f, 0f, 1f);
                shade.anchorMin = Vector2.zero;
                shade.anchorMax = Vector2.one;
                shade.offsetMin = new Vector2(-4000f, -4000f);
                shade.offsetMax = new Vector2(4000f, 4000f);
                Plate(shade, new Color(0f, 0f, 0f, 0.72f), true);
            }

            _padFrame = new GameObject(Owned + "Frame", typeof(RectTransform)).GetComponent<RectTransform>();
            _padFrame.SetParent(_pad.transform, false);
            _padFrame.anchorMin = _padFrame.anchorMax = _padFrame.pivot = new Vector2(0.5f, 0.5f);
            _padFrame.sizeDelta = new Vector2(EtsTabletLayout.FrameWidth, EtsTabletLayout.FrameHeight);
            _padFrame.gameObject.AddComponent<RectMask2D>();

            // The game's desktop colour behind every page, as the tablet's app canvas has (EtsTabletLink.Inject): the
            // screens' windows stand on it, not on the dimmed world (probe 1905's first pictures showed the shade).
            Plate(_padFrame, Colour("clr_desktop_bg_main", DesktopBackground), true);
            _pad.SetActive(false);
            // Escape and left X reach the panel only in the new flow (EtsMenu, EtsTabletLayout.MenuKey); with the flag off
            // (the editor preview) they are still the old menu's, and Tab alone opens the panel.
            Debug.Log("[SomniumEuroTruck] Tablet: no Somnium tablet (" + (Link != null ? Link.Report : "no link") + ") - using the world's own panel; " +
                      (!EtsFeatures.NewFlow ? "Tab" : EtsVr.Present ? "left X" : "Tab or Escape") + " opens it.");
            Rehost();
        }

        private void PadLost()
        {
            bool wasOpen = _padOpen;
            _pad = null;
            _padFrame = null;
            _padCanvasRect = null;
            _padSeated = false;
            _padTruck = null;

            if (_padOpen)
            {
                _padOpen = false;
                RestoreCursor();
            }

            _openPending |= wasOpen;
            Debug.Log("[SomniumEuroTruck] Tablet: the world's own panel was destroyed with what it was pinned to (" + (wasOpen ? "open" : "closed") + ") - making it again.");
            Rehost(); // no host now: the old pages' objects are gone with it
        }

        private void SetPad(bool open, string why)
        {
            if (_pad == null || _padOpen == open)
            {
                return;
            }

            _padOpen = open;
            _pad.SetActive(open);

            if (open)
            {
                if (!EtsVr.Present)
                {
                    _cursorLockWas = Cursor.lockState;
                    _cursorVisibleWas = Cursor.visible;
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }

                _padCanvasSize = Vector2.zero;
                KeepPadPlaced(true);

                if (_current == null && !_onHome)
                {
                    ShowHome();
                }

                LandFirstOpen();
                UpdateHint();
            }
            else
            {
                RestoreCursor();
                ClosePopup();
            }

            EtsGameUi.Note(open ? "tablet panel opened (" + why + ")" : "tablet panel closed (" + why + ")");
        }

        private void RestoreCursor()
        {
            if (!EtsVr.Present)
            {
                Cursor.lockState = _cursorLockWas;
                Cursor.visible = _cursorVisibleWas;
            }
        }

        /// <summary>
        /// Desktop: the frame fitted whole into the overlay canvas, centred (it follows the screen's size). Headset: the
        /// panel stands 0.8 m ahead on foot and follows past 2.5 m (EtsVrPanel's rule); seated it is pinned to the cab
        /// 0.5 m in front of the eye and 0.12 m down, scaled to the same 61 degrees, because a room panel would jump
        /// every 2.5 m at road speed ([tablet §4.6], placement: not seen in a headset yet).
        /// </summary>
        private void KeepPadPlaced(bool force = false)
        {
            if (_pad == null || _padFrame == null)
            {
                return;
            }

            var vr = _pad.GetComponent<EtsVrPanel>();

            if (vr == null)
            {
                Vector2 size = _padCanvasRect.rect.size;

                if (force || size != _padCanvasSize)
                {
                    _padCanvasSize = size;
                    float fit = size.x > 1f && size.y > 1f ? EtsTabletLayout.Fit(size.x, size.y) : 1f;
                    _padFrame.localScale = new Vector3(fit, fit, 1f);
                }

                return;
            }

            EtsTruckController truck = Menu != null ? Menu.Truck : null;
            bool seated = truck != null && Menu.Seat != null && Menu.Seat.HasPlayer && Menu.Seat.Seated;
            var focus = new Vector2(EtsTabletLayout.FrameWidth * 0.5f, EtsTabletLayout.FrameHeight * 0.5f);

            if (seated)
            {
                int seat = Menu.Seat.SeatIndex;

                if (!force && _padSeated && _padTruck == truck && _padSeat == seat)
                {
                    return;
                }

                _padSeated = true;
                _padTruck = truck;
                _padSeat = seat;
                Vector3 eye = truck.EyeLocalFor(seat); // seat 0: DriverEyeLocal; a passenger's is moved by the seat offset (C, PLAN §7.C)
                vr.AttachTo(truck.transform, eye + new Vector3(0f, -0.12f, 0.5f), eye, focus, EtsVrPanel.Width * 0.5f / EtsVrPanel.Distance);
                return;
            }

            _padTruck = null;

            Camera head = EtsVr.Head();

            if (head == null)
            {
                return;
            }

            if (_padAnchor == null)
            {
                _padAnchor = new GameObject(Owned + "PanelAnchor").transform;
            }

            bool far = (head.transform.position - _padAnchor.position).sqrMagnitude > 2.5f * 2.5f + EtsVrPanel.Distance * EtsVrPanel.Distance;

            if (!force && !_padSeated && !far)
            {
                return;
            }

            _padSeated = false;
            Vector3 forward = head.transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            _padAnchor.SetPositionAndRotation(head.transform.position + forward * EtsVrPanel.Distance, Quaternion.LookRotation(forward, Vector3.up));
            vr.AttachTo(_padAnchor, Vector3.zero, new Vector3(0f, 0f, -EtsVrPanel.Distance), focus, EtsVrPanel.Width);
        }

        // ------------------------------------------------------------------ input

        /// <summary>
        /// While the app is in front of the local driver, the truck takes no keys and no pedals (EtsMenu.Open's contract,
        /// EtsMenu.cs:121-125) - and in a headset while the Somnium tablet is open at all, since its buttons (the "ETS 2"
        /// one on its home grid first) are pressed with the trigger that is the throttle (EtsTabletLayout.HoldTruck).
        /// When it goes, the truck gets back what it had, and in a headset its pedals are disarmed
        /// (EtsTruckController.DisarmVrPedals, PLAN §7.C/§7.X): they wait until both triggers are let go, the rule the
        /// truck keeps for getting in, so the trigger that pressed the last tablet button never becomes throttle
        /// ([tablet §4.5]). The steering stick is the driver's again at once. The first hold of a visit is logged.
        /// </summary>
        private void HoldInputWhileVisible()
        {
            EtsTruckController truck = Menu != null ? Menu.Truck : null;
            bool driving = truck != null && Menu.Driving;

            if (EtsTabletLayout.HoldTruck(driving, Visible, EtsVr.Present, Linked && Link.TabletOpen))
            {
                if (_heldTruck != truck)
                {
                    ReleaseHeld(); // the truck changed under the hold: the old one gets its keys back
                    _heldTruck = truck;
                    _inputWasEnabled = truck.InputEnabled;

                    if (!_holdReported)
                    {
                        _holdReported = true;
                        Debug.Log("[SomniumEuroTruck] Tablet: the truck's keys and pedals are held while " + (Visible ? "the ETS 2 app" : "the Somnium tablet") +
                                  " is up (" + (_inputWasEnabled ? "they had them" : "they had none") + "); they come back when it goes.");
                    }
                }

                if (truck.InputEnabled)
                {
                    truck.InputEnabled = false;
                }

                // In a headset the Somnium tablet alone - open, but not on the app (its home, a client tab, or after the
                // app's Back or Drive) - still holds the truck. Nothing on screen said so: a player who closed only the
                // app pulled the throttle and the truck just coasted (checker, Phase 2). Said once each time that starts.
                bool tabletOnly = EtsTabletLayout.HeldByTabletAlone(Visible, _inputWasEnabled);

                if (tabletOnly && !_heldByTabletOnly)
                {
                    Toast(TabletHoldsTheTruck);
                }

                _heldByTabletOnly = tabletOnly;
                return;
            }

            _heldByTabletOnly = false;

            if (_heldTruck != null)
            {
                ReleaseHeld();
            }
        }

        /// <summary>Not in the locale. "Tap B or Y" is the client's own tablet tap ([tablet §2.7]).</summary>
        private const string TabletHoldsTheTruck = "Your tablet is open, so the truck waits - tap B or Y to close it and drive";

        private bool _heldByTabletOnly;

        private void ReleaseHeld()
        {
            if (_heldTruck == null)
            {
                return;
            }

            bool back = _inputWasEnabled && Menu != null && Menu.Truck == _heldTruck && Menu.Driving && !Menu.IsOpen;

            if (back && EtsVr.Present)
            {
                _heldTruck.DisarmVrPedals();
            }

            _heldTruck.InputEnabled = back;
            _heldTruck = null;
        }

        // ------------------------------------------------------------------ words and colours

        /// <summary>
        /// The game's words for a locale key (EtsLocale, owner D). Before the locale is loaded, the words probe 1900 read
        /// from /locale/en_gb/localization.sui for the keys the shell itself uses, then the bare key.
        /// </summary>
        public static string Word(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "";
            }

            string bare = key.Trim().Trim('@');

            if (EtsLocale.Loaded)
            {
                return EtsLocale.Word(bare);
            }

            return Measured.TryGetValue(bare, out string word) ? word : bare;
        }

        /// <summary>Text that may hold @@keys@@ (a screen link's icon_label), resolved.</summary>
        public static string Words(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            if (EtsLocale.Loaded)
            {
                return EtsLocale.Resolve(text);
            }

            string t = text.Trim();
            return t.StartsWith("@@", StringComparison.Ordinal) && t.EndsWith("@@", StringComparison.Ordinal) && t.Length > 4 ? Word(t) : t;
        }

        // /locale/en_gb/localization.sui, read by probe 1900 (S/harness/out1900.txt) - used only until EtsLocale loads.
        private static readonly Dictionary<string, string> Measured = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "drive", "Drive" }, { "drive_tooltip", "Back to the driver's seat." }, { "profile_level", "Level" }, { "loading", "LOADING ..." },
            { "options", "Options" }, { "desktop_menu_category_jobs", "Jobs" }, { "desktop_menu_category_map", "Navigation" },
            { "desktop_menu_category_social", "Convoys" }, { "desktop_menu_category_personal", "Career" }, { "desktop_menu_category_company", "Company" },
            { "desktop_menu_category_services", "Services" }, { "desktop_menu_category_content_browsers", "Catalogue" },
            { "desktop_menu_category_media", "Media" }, { "desktop_menu_category_system", "System" },
            { "job_quick", "Quick Job" }, { "career_progress", "Career Progress" }, { "truckman_link_title", "Truck Manager" },
            { "convoy_current_session", "Active Convoy" }, { "world_map_link", "World Map" }, { "quick_travel_icon_label", "Quick Travel" },
            // S/design/gameui/dump/locale_en_gb_localization.sui (1.61): the quit message box and the controls title.
            { "quit_game", "Quit" }, { "wanna_quit", "Your progress will be saved automatically. <br><br> Do you really want to quit?" },
            { "yes", "Yes" }, { "no", "No" }, { "controller", "Controls" },
        };

        /// <summary>A colour of /locale/ui_colors.sui through EtsLocale (ABGR stored, ARGB returned), or the value probe 1900 decoded.</summary>
        public static Color Colour(string key, Color measured)
        {
            if (EtsLocale.TryColour(key, out uint argb))
            {
                return new Color32((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
            }

            return measured;
        }

        // Probe 1900, /locale/ui_colors.sui decoded AABBGGRR -> RGBA.
        public static readonly Color DesktopBackground = new Color(0.090f, 0.094f, 0.098f, 0.961f); // clr_desktop_bg_main F5191817
        public static readonly Color SelectedText = new Color(0.980f, 0.690f, 0.012f, 1f);           // clr_sel FF03B0FA
        public static readonly Color NormalText = new Color(0.925f, 0.925f, 0.925f, 1f);             // clr_txt FFECECEC
        public static readonly Color DimText = new Color(0.651f, 0.651f, 0.651f, 1f);                // clr_txt_d FFA6A6A6
        public static readonly Color Hover = new Color(0.298f, 0.357f, 0.400f, 1f);                  // clr_desktop_link_bg_s, clr_top_bar_s, clr_button_basic_s FF665b4c
        public static readonly Color NavHover = new Color(0.231f, 0.271f, 0.306f, 1f);               // clr_navbar_s FF4e453b
        public static readonly Color ButtonFace = new Color(0.231f, 0.271f, 0.302f, 1f);             // clr_button_basic_n FF4d453b
        public static readonly Color XpBack = new Color(0.224f, 0.224f, 0.224f, 1f);                 // clr_personal_progress_bar_bg FF393939
        public static readonly Color XpFront = new Color(0.706f, 0.506f, 0.047f, 1f);                // clr_personal_progress_bar FF0c81b4

        /// <summary>clr_white_alpha_25 (40FFFFFF): the game's tint for what cannot be used (every d_pml).</summary>
        public const float DisabledAlpha = 64f / 255f;

        // ------------------------------------------------------------------ drawing primitives (for pages too)

        /// <summary>A rectangle over the whole of a 1440 x 900 parent.</summary>
        public static RectTransform FullFrame(Transform parent, string name)
        {
            RectTransform rect = EtsGameUi.Place(parent, name, 0f, EtsTabletLayout.FrameWidth, 0f, EtsTabletLayout.FrameHeight);
            rect.gameObject.layer = parent.gameObject.layer;
            return rect;
        }

        public static Image Plate(RectTransform rect, Color colour, bool takesPointer)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.color = colour;
            image.raycastTarget = takesPointer;
            return image;
        }

        /// <summary>A label in one of the game's faces, one line, shrinking to fit rather than spilling.</summary>
        public static TextMeshProUGUI Text(RectTransform rect, string text, string font, TextAlignmentOptions alignment, Color colour)
        {
            TextMeshProUGUI label = EtsGameUi.Label(rect, text, font, alignment, colour);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.enableAutoSizing = true;
            label.fontSizeMax = label.fontSize;
            label.fontSizeMin = Mathf.Max(8f, label.fontSize * 0.6f);
            label.overflowMode = TextOverflowModes.Ellipsis;
            return label;
        }

        /// <summary>An SDF icon (ui.sdf.rfx, [gameui §2.4]) painted by D's EtsGameUi.SdfIcon; nothing while that is not there.</summary>
        public static RawImage Icon(RectTransform rect, string materialPath)
        {
            Texture2D texture = null;

            try
            {
                texture = EtsGameUi.SdfIcon(materialPath, Mathf.Max(8, Mathf.RoundToInt(rect.sizeDelta.x)), Mathf.Max(8, Mathf.RoundToInt(rect.sizeDelta.y)));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Tablet: icon " + materialPath + ": " + e.Message);
            }

            if (texture == null)
            {
                return null;
            }

            var image = rect.gameObject.AddComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// One of the game's looks through D's EtsGameUi.Template, in frame coordinates; null when there is no template
        /// yet or it failed (the caller draws its stand-in). A failure is logged once per look.
        /// </summary>
        public static RectTransform Look(RectTransform host, string look, string value, float left, float right, float bottom, float top, Action onClick)
        {
            try
            {
                return EtsGameUi.Template(host, look, value, left, right, bottom, top, onClick);
            }
            catch (Exception e)
            {
                if (FailedLooks.Add(look))
                {
                    Debug.LogWarning("[SomniumEuroTruck] Tablet: look " + look + " failed (" + e.GetType().Name + ": " + e.Message + ") - drawn plainly.");
                }

                return null;
            }
        }

        private static readonly HashSet<string> FailedLooks = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>A transparent catcher with a hover plate: the pointer's answer for stand-in drawings.</summary>
        public static Button Clickable(RectTransform rect, Color hover, Action onClick)
        {
            RectTransform lit = EtsGameUi.Place(rect, "hover", 0f, 1f, 0f, 1f);
            lit.anchorMin = Vector2.zero;
            lit.anchorMax = Vector2.one;
            lit.offsetMin = Vector2.zero;
            lit.offsetMax = Vector2.zero;
            lit.SetAsFirstSibling();
            Image plate = Plate(lit, hover, true);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = plate;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock colours = button.colors;
            colours.normalColor = new Color(1f, 1f, 1f, 0f);
            colours.highlightedColor = Color.white;
            colours.selectedColor = new Color(1f, 1f, 1f, 0f);
            colours.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            colours.disabledColor = new Color(1f, 1f, 1f, 0f);
            colours.fadeDuration = 0.08f;
            button.colors = colours;

            if (onClick != null)
            {
                button.onClick.AddListener(() => { EtsUiSound.Click(); onClick(); }); // the game's ui_click (2026-10-14)
            }

            return button;
        }

        /// <summary>
        /// A button in the game's btn.normal look (the clr_button_basic_n plate, the caption in white big_bold), through
        /// the template or its stand-in. Its caption is fixed; state goes in a label beside it.
        /// </summary>
        public Button Button(RectTransform parent, string caption, float left, float right, float bottom, float top, Action onClick)
        {
            string pressed = caption;
            Action click = () =>
            {
                EtsGameUi.Note("tablet: pressed \"" + pressed + "\"");
                onClick?.Invoke();
            };

            RectTransform made = Look(parent, "btn.normal", caption, left, right, bottom, top, click);
            Button button = made != null ? made.GetComponentInChildren<Button>(true) : null;

            if (made != null && button == null)
            {
                button = Clickable(made, new Color(1f, 1f, 1f, 0.08f), click);
            }

            if (button != null)
            {
                return button;
            }

            RectTransform rect = EtsGameUi.Place(parent, "button " + caption, left, right, bottom, top);
            Plate(rect, Colour("clr_button_basic_n", ButtonFace), false);
            button = Clickable(rect, Colour("clr_button_basic_s", Hover), click);
            RectTransform label = EtsGameUi.Place(rect, "caption", 8f, right - left - 8f, 0f, top - bottom);
            Text(label, caption, EtsFont.Big, TextAlignmentOptions.Center, Color.white);
            return button;
        }

        /// <summary>Greys a control the way the game greys what cannot be used (clr_white_alpha_25) and stops it taking the pointer.</summary>
        public static void SetUsable(Component control, bool usable)
        {
            SetUsable(control, usable, false);
        }

        /// <summary>
        /// Greyed as SetUsable greys, but when `pressable` still taking the press, so the page can say why nothing happens
        /// (Convoys' "Ride along" / "Go to", Drive's "Get out" above 10 km/h: the task's "refused ... with the reason").
        /// </summary>
        public static void SetUsable(Component control, bool usable, bool pressable)
        {
            if (control == null)
            {
                return;
            }

            var group = control.GetComponent<CanvasGroup>();

            if (group == null)
            {
                group = control.gameObject.AddComponent<CanvasGroup>();
            }

            float alpha = usable ? 1f : DisabledAlpha;
            bool takes = usable || pressable;

            if (group.alpha != alpha) group.alpha = alpha;
            if (group.interactable != takes) group.interactable = takes;
        }

        /// <summary>A game screen's window (txt.window.bcg_rect4 in clr_bg_main) with its title in clr_sel.</summary>
        public static RectTransform Window(RectTransform root, string title, float left, float right, float bottom, float top)
        {
            RectTransform window = Look(root, "txt.window.bcg_rect4", "@@clr_bg_main@@", left, right, bottom, top, null);

            if (window == null)
            {
                window = EtsGameUi.Place(root, "window", left, right, bottom, top);
                Plate(window, Colour("clr_bg_main", DesktopBackground), true);
            }

            RectTransform caption = EtsGameUi.Place(root, "title", left + 20f, right - 20f, top - 50f, top - 10f);
            Text(caption, title, EtsFont.Title, TextAlignmentOptions.Left, Colour("clr_sel", SelectedText));
            return window;
        }

        // ------------------------------------------------------------------ the desktop

        private void BuildHome(RectTransform root)
        {
            Slot home = null;

            foreach (Slot slot in _slots)
            {
                if (slot.Page.Category == HomeId)
                {
                    home = slot;
                    break;
                }
            }

            if (home != null)
            {
                return; // D's desktop page is the home (built when shown)
            }

            // No desktop page yet: the game's desktop colour and its logo (/material/ui/logo/ets2_logo.mat, 1024 x 512).
            Plate(root, Colour("clr_desktop_bg_main", DesktopBackground), true);
            Texture2D logo = EtsEngine.Files != null ? EtsGameUi.Material("/material/ui/logo/ets2_logo.mat") : null;

            if (logo != null)
            {
                EtsGameUi.Picture(EtsGameUi.Place(root, "logo", 420f, 1020f, 400f, 700f), logo, true);
            }
        }

        private void ShowHome()
        {
            Slot home = null;

            foreach (Slot slot in _slots)
            {
                if (slot.Page.Category == HomeId)
                {
                    home = slot;
                    break;
                }
            }

            if (home != null)
            {
                ShowSlot(home);
                _onHome = true;
                _currentCategory = null;
                UpdateBars();
                return;
            }

            if (_current != null)
            {
                SafeHidden(_current);

                if (_current.Root != null)
                {
                    _current.Root.gameObject.SetActive(false);
                }
            }

            _current = null;
            _onHome = true;
            _currentCategory = null;

            if (_homeRoot != null)
            {
                _homeRoot.gameObject.SetActive(true);
            }

            EtsGameUi.Note("tablet: desktop");
            UpdateBars();
        }

        private void ShowSlot(Slot slot)
        {
            if (_host == null || slot == null)
            {
                return;
            }

            ClosePopup();

            if (_current != null && _current != slot)
            {
                SafeHidden(_current);

                if (_current.Root != null)
                {
                    _current.Root.gameObject.SetActive(false);
                }
            }

            if (slot.Root == null)
            {
                BuildSlot(slot, "on first show");
            }

            if (_homeRoot != null)
            {
                _homeRoot.gameObject.SetActive(false);
            }

            bool wasCurrent = _current == slot;
            _current = slot;
            _onHome = slot.Page.Category == HomeId;
            _currentCategory = CategoryOf(slot);
            slot.Root.gameObject.SetActive(true);

            if (!wasCurrent && !slot.Failed)
            {
                try
                {
                    slot.Page.Shown();
                    slot.Page.Tick();
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Tablet: page " + slot.Page.Id + " Shown: " + e.GetType().Name + ": " + e.Message);
                }

                EtsGameUi.Note("tablet: " + slot.Page.Caption);
            }

            UpdateBars();
        }

        /// <summary>A page's objects in this host: its whole-frame root, then its Build inside an EtsUiHost scope.</summary>
        private void BuildSlot(Slot slot, string when)
        {
            slot.Root = FullFrame(_pagesRoot, Owned + "Page " + slot.Page.Id);
            slot.TickFailed = false;
            var watch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using (EtsUiHost.Into(slot.Root))
                {
                    slot.Page.Attach(this, slot.Root);
                }
            }
            catch (Exception e)
            {
                slot.Failed = true;
                Debug.LogWarning("[SomniumEuroTruck] Tablet: page " + slot.Page.Id + " failed to build (" + e.GetType().Name + ": " + e.Message + ").");
                RectTransform line = EtsGameUi.Place(slot.Root, "failed", 200f, 1240f, 420f, 480f);
                Text(line, slot.Page.Caption + ": not available (" + e.GetType().Name + ")", EtsFont.Big, TextAlignmentOptions.Center, Colour("clr_txt_d", DimText));
            }

            watch.Stop();

            if (watch.Elapsed.TotalMilliseconds > 2.0)
            {
                Debug.Log("[SomniumEuroTruck] Tablet: page " + slot.Page.Id + " built " + when + " in " + watch.Elapsed.TotalMilliseconds.ToString("0.0") + " ms.");
            }

            EtsLayers.SetRecursively(slot.Root, UiLayer);
        }

        /// <summary>
        /// While the session is still starting - Connecting, the HQ choice, Loading: the player is in the void or on a
        /// loading screen, where one long frame is not seen as a stutter - the pages this host has not built yet are
        /// built one per frame, hidden, so the first open in the world does not pay for them. Probe 1905 measured the
        /// Options page's first build at 203 ms with the template, font and icon caches cold, 7 ms warm; at 90 Hz in a
        /// headset that is 18 frames dropped while driving. In the world (and whenever there is no session: the
        /// flag off, the editor preview) a page is built when it is first shown, as before.
        /// </summary>
        private void PrebuildWhileStarting()
        {
            EtsSession session = EtsSession.Instance;

            if (_host == null || _pagesRoot == null || session == null || session.Current == EtsPhase.InWorld)
            {
                return;
            }

            // D's caches first (Phase 2 integration, D's request): built before EtsFlow's warm-up ends, a page pays the
            // cold template and font cost on the main thread - 51-56 ms each for Truck Preference and Quick Job in E's
            // bootstrap test - while the player may be pointing at the HQ map. CachesWarm turns true under the loading
            // screen (EtsFlowRules.LateWarm); a page not prebuilt by arrival is built when first shown, as before.
            EtsFlow flow = EtsFlow.Instance;

            if (flow != null && !flow.CachesWarm)
            {
                return;
            }

            foreach (Slot slot in _slots)
            {
                if (slot.Root == null && slot.Page.Available)
                {
                    BuildSlot(slot, "while the session starts");

                    if (slot != _current)
                    {
                        slot.Root.gameObject.SetActive(false);
                    }

                    return; // one a frame
                }
            }
        }

        private static void SafeHidden(Slot slot)
        {
            try
            {
                if (slot.Root != null)
                {
                    slot.Page.Hidden();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Tablet: page " + slot.Page.Id + " Hidden: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private Slot SlotOf(string id)
        {
            foreach (Slot slot in _slots)
            {
                if (slot.Page.Id == id)
                {
                    return slot;
                }
            }

            return null;
        }

        private Category CategoryOf(Slot slot)
        {
            foreach (Category category in _categories)
            {
                foreach (MenuLink link in category.Links)
                {
                    if (link.Slot == slot)
                    {
                        return category;
                    }
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ the menu, from the game's files

        /// <summary>
        /// The categories and links as the install defines them (EtsTabletLayout.ReadMenu: /def/ui_desktop_ind_full.sii
        /// and /ui/!desc.sii, 10 categories in 3 groups, 40 links on 1.61), with the locale's words; without archives,
        /// probe 1900's ten categories. Then the pages: each takes its ScreenLink's place, or joins its category after
        /// the game's links.
        /// </summary>
        private void ReadMenu()
        {
            _categories.Clear();
            EtsFileSystem files = EtsEngine.Files;
            List<EtsMenuCategory> read = files != null && MenuFiles == files ? MenuCache : null;

            foreach (EtsMenuCategory source in read ?? EtsTabletLayout.MeasuredMenu())
            {
                var category = new Category { Key = source.Key, Icon = source.Icon, Name = Clean(Words(source.NameKey)), NoMenu = source.NoMenu, Group = source.Group };

                foreach (EtsMenuLink link in source.Links)
                {
                    category.Links.Add(new MenuLink { ScreenLink = link.ScreenLink, Icon = link.Icon, Label = Clean(link.LabelKey.Length > 0 ? Words(link.LabelKey) : link.ScreenLink) });
                }

                _categories.Add(category);
            }

            // The pages: into their game link's place, or after the game's links of their category.
            foreach (Slot slot in _slots)
            {
                EtsTabletPage page = slot.Page;

                if (page.Category == HomeId)
                {
                    continue;
                }

                Category category = null;
                MenuLink placed = null;
                string standsFor = StandsFor(page);

                foreach (Category c in _categories)
                {
                    if (c.Key == page.Category)
                    {
                        category = c;
                    }

                    foreach (MenuLink link in c.Links)
                    {
                        if (placed == null && standsFor != null && link.ScreenLink == standsFor && link.Slot == null)
                        {
                            placed = link;
                        }
                    }
                }

                if (placed != null)
                {
                    placed.Slot = slot;

                    // The link keeps the game's place and icon and reads the page's own title once the locale is in. For
                    // Quick Job, Career Progress, Options and Quit the two are the same words (job_quick, career_progress,
                    // options, quit_game); for D's Truck Preference in Truck Manager's place (PLAN §12: "D2 and E2 settle
                    // the menu word") it is preffered_truck_title "Truck Preference", so Company's menu reads exactly
                    // what the player is told to press: Company > Truck Preference.
                    string caption = EtsLocale.Loaded ? Clean(page.Caption) : "";

                    if (caption.Length > 0)
                    {
                        placed.Label = caption;
                    }

                    continue;
                }

                if (category == null)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Tablet: page " + page.Id + " names category '" + page.Category + "', which the game's menu bar does not have.");
                    continue;
                }

                category.Links.Add(new MenuLink { ScreenLink = null, Icon = page.Icon, Label = Clean(page.Caption), Slot = slot });
            }

            foreach (Category category in _categories)
            {
                category.Live = false;

                foreach (MenuLink link in category.Links)
                {
                    category.Live |= link.Live;
                }
            }

            _builtWithMenu = read != null;
            _builtWithLocale = EtsLocale.Loaded;
        }

        /// <summary>
        /// The game screen link a page takes the place of: its own ScreenLink, or - for D's Truck Preference page while it
        /// names none - Company's first link, screen.truckman ("Truck Manager", icon truck_man: probe 1902). PLAN §12:
        /// "EtsPageTruckPreference stands in for screen.truckman". Without this it would be appended after Company's five
        /// greyed links as a sixth.
        /// </summary>
        private static string StandsFor(EtsTabletPage page)
        {
            string link = page.ScreenLink;

            if (string.IsNullOrEmpty(link) && page.Id == TruckPreferenceId && page.Category == "fleet")
            {
                link = "screen.truckman";
            }

            return string.IsNullOrEmpty(link) ? null : link;
        }

        /// <summary>'|' separates a look's values (%0|%1|...): never inside one.</summary>
        private static string Clean(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : text.IndexOf('|') < 0 ? text : text.Replace('|', '/');
        }

        private int CountLinks(bool live)
        {
            int n = 0;

            foreach (Category category in _categories)
            {
                foreach (MenuLink link in category.Links)
                {
                    if (!live || link.Live) n++;
                }
            }

            return n;
        }

        // ------------------------------------------------------------------ the chrome: top bar, menu bar, nav bar

        private void RebuildChrome()
        {
            if (_host == null || _chrome == null)
            {
                return;
            }

            _chrome.gameObject.SetActive(false); // Destroy waits for the end of the frame
            Destroy(_chrome.gameObject);
            _chrome = FullFrame(_host, Owned + "Chrome");
            _menuBar = null;
            _navBar = null;
            _popup = null;
            _popupCategory = null;
            _blockedPanel = null;
            _blockedText = null;
            _note = null;
            _sections.Clear();

            if (_homeRoot != null && _pagesRoot != null)
            {
                // The built-in desktop again too: its logo needs the archives that may just have arrived.
                bool shown = _homeRoot.gameObject.activeSelf;
                _homeRoot.gameObject.SetActive(false);
                Destroy(_homeRoot.gameObject);
                _homeRoot = FullFrame(_pagesRoot, Owned + "Desktop");
                _homeRoot.SetAsFirstSibling();
                BuildHome(_homeRoot);
                _homeRoot.gameObject.SetActive(shown);
            }

            BuildChrome();
            _currentCategory = _current != null ? CategoryOf(_current) : null;
            UpdateBars();

            // Linked, only what was made again here (the pages keep the layer they were given when built).
            int layer = UiLayer;
            EtsLayers.SetRecursively(Linked && _homeRoot != null ? _homeRoot : _pagesRoot != null ? _pagesRoot : _chrome, layer);
            EtsLayers.SetRecursively(_chrome, layer);
        }

        private void BuildChrome()
        {
            _chromeDirty = false; // every page added so far is in the menu read below
            ReadMenu();
            BuildBlockedPanel();
            BuildTopBar();
            _menuBar = FullFrame(_chrome, "menu bar");
            BuildMenuBar(_menuBar);
            _navBar = FullFrame(_chrome, "nav bar");
            RefreshChrome();
        }

        private void BuildBlockedPanel()
        {
            _blockedPanel = EtsGameUi.Place(_chrome, "blocked", 0f, EtsTabletLayout.FrameWidth, 0f, EtsTabletLayout.TopBarBottom - 3f);
            Plate(_blockedPanel, Colour("clr_desktop_bg_main", DesktopBackground), true);
            RectTransform line = EtsGameUi.Place(_blockedPanel, "line", 120f, 1320f, 400f, 480f);
            _blockedText = Text(line, "", EtsFont.Big, TextAlignmentOptions.Center, Colour("clr_sel", SelectedText));
            _blockedPanel.gameObject.SetActive(false);
        }

        private void BuildTopBar()
        {
            const float b = EtsTabletLayout.TopBarBottom, t = EtsTabletLayout.TopBarTop;
            RectTransform plate = Look(_chrome, "txt.window.bcg_rect4", "@@clr_desktop_bg_main@@", EtsTabletLayout.TopBarLeft, EtsTabletLayout.TopBarRight, b, t, null);

            if (plate == null)
            {
                plate = EtsGameUi.Place(_chrome, "top bar", EtsTabletLayout.TopBarLeft, EtsTabletLayout.TopBarRight, b, t);
                Plate(plate, Colour("clr_desktop_bg_main", DesktopBackground), true);
            }

            // Home: back to the desktop (btn.desktop.top_bar.home, 66..110).
            AddSection("btn.desktop.top_bar.home", EtsTabletLayout.HomeLeft, EtsTabletLayout.HomeRight, () => Show(HomeId), s =>
            {
                RectTransform r = EtsGameUi.Place(_chrome, "home", s.Left, s.Right, b, t);
                Icon(EtsGameUi.Place(r, "icon", 6f, 42f, 1f, 37f), "/material/ui/desktop/home.mat");
                Clickable(r, Colour("clr_top_bar_s", Hover), s.Click);
                return r;
            }).Value = "";

            // The driver (btn.desktop.top_bar.personal): avatar | name | XP bar width | level. Pressed, it opens the
            // career, as the game's does (the page standing for career.progress, once D's Career page is there).
            AddSection("btn.desktop.top_bar.personal", EtsTabletLayout.PersonalLeft, EtsTabletLayout.PersonalRight, () => ShowScreenLink("career.progress"), s => DrawPersonal(s));

            // The master badge (PLAN §3.7) in the company slot 367..623: the game has no such element, so it is drawn
            // in the company look's places (32 x 32 picture at +9, words from +48), not by the template.
            AddSection(null, EtsTabletLayout.CompanyLeft, EtsTabletLayout.CompanyRight, null, s => DrawBadge(s));

            // Notifications (637..1037): the latest toast.
            RectTransform note = EtsGameUi.Place(_chrome, "notification", EtsTabletLayout.NotificationLeft + 8f, EtsTabletLayout.NotificationRight - 8f, b, t);
            _note = Text(note, _toast, EtsFont.Normal, TextAlignmentOptions.Center, Colour("clr_txt", NormalText));

            // The HQ country's flag, the money, the clock.
            AddSection("btn.desktop.top_bar.country", EtsTabletLayout.CountryLeft, EtsTabletLayout.CountryRight, null, s => DrawFlag(s));
            AddSection("btn.desktop.top_bar.bank", EtsTabletLayout.BankLeft, EtsTabletLayout.BankRight, null, s => DrawTextSection(s, "bank"));
            AddSection("txt.desktop.top_bar.date_time", EtsTabletLayout.ClockLeft, EtsTabletLayout.ClockRight, null, s => DrawTextSection(s, "clock"));
        }

        private Section AddSection(string look, float left, float right, Action click, Func<Section, RectTransform> fallback)
        {
            var section = new Section { Look = look, Left = left, Right = right, Click = click, Fallback = fallback };
            _sections.Add(section);
            return section;
        }

        /// <summary>Draws a section for a new value: the game's look, else its stand-in. Only when the value changed.</summary>
        private void SetSection(Section section, string value)
        {
            if (section == null || (section.Made != null && section.Value == value))
            {
                return;
            }

            section.Value = value;

            if (section.Made != null)
            {
                Destroy(section.Made.gameObject);
                section.Made = null;
            }

            if (string.IsNullOrEmpty(value) && section.Look != "btn.desktop.top_bar.home")
            {
                return; // nothing to show (no flag, no clock, no badge)
            }

            RectTransform made = section.Look != null ? Look(_chrome, section.Look, value, section.Left, section.Right, EtsTabletLayout.TopBarBottom, EtsTabletLayout.TopBarTop, section.Click) : null;
            section.Made = made != null ? made : section.Fallback(section);

            if (section.Made != null)
            {
                EtsLayers.SetRecursively(section.Made, UiLayer);
            }
        }

        private RectTransform DrawPersonal(Section s)
        {
            // value = avatar|name|bar width|level (btn.desktop.top_bar.personal's %0..%3).
            string[] parts = s.Value.Split('|');
            const float b = EtsTabletLayout.TopBarBottom, t = EtsTabletLayout.TopBarTop;
            RectTransform r = EtsGameUi.Place(_chrome, "personal", s.Left, s.Right, b, t);
            Texture2D avatar = EtsEngine.Files != null ? EtsGameUi.Material(parts[0] + ".mat") : null; // /material/ui/driver/empty.mat, 96 x 96 (probe 1900)

            if (avatar != null)
            {
                EtsGameUi.Picture(EtsGameUi.Place(r, "avatar", EtsTabletLayout.BadgeIconLeft, EtsTabletLayout.BadgeIconLeft + 32f, 4f, 36f), avatar, true);
            }

            float x = EtsTabletLayout.PersonalTextLeft;
            Text(EtsGameUi.Place(r, "name", x, s.Right - s.Left - 4f, 20f, 40f), parts.Length > 1 ? parts[1] : "", EtsFont.Normal, TextAlignmentOptions.Left, Colour("clr_sel", SelectedText));
            Plate(EtsGameUi.Place(r, "xp back", x, x + EtsTabletLayout.XpBarWidth, 4f, 18f), Colour("clr_personal_progress_bar_bg", XpBack), false);
            int lit = parts.Length > 2 && int.TryParse(parts[2], out int w) ? w : 0;

            if (lit > 0)
            {
                Plate(EtsGameUi.Place(r, "xp", x, x + lit, 4f, 18f), Colour("clr_personal_progress_bar", XpFront), false);
            }

            Text(EtsGameUi.Place(r, "level", x + 3f, x + EtsTabletLayout.XpBarWidth, 3f, 19f), Word("profile_level") + " " + (parts.Length > 3 ? parts[3] : "0"), EtsFont.Small, TextAlignmentOptions.Left, Colour("clr_txt", NormalText));

            if (s.Click != null)
            {
                Clickable(r, Colour("clr_top_bar_s", Hover), s.Click);
            }

            return r;
        }

        /// <summary>Shows the page standing for one of the game's screen links, when this world has it live; otherwise nothing.</summary>
        private void ShowScreenLink(string screenLink)
        {
            foreach (Category category in _categories)
            {
                foreach (MenuLink link in category.Links)
                {
                    if (link.ScreenLink == screenLink)
                    {
                        if (link.Live)
                        {
                            ShowSlot(link.Slot);
                        }

                        return;
                    }
                }
            }
        }

        private RectTransform DrawBadge(Section s)
        {
            // value = "1" (this player is the master) or "0|name".
            const float b = EtsTabletLayout.TopBarBottom, t = EtsTabletLayout.TopBarTop;
            RectTransform r = EtsGameUi.Place(_chrome, "master", s.Left, s.Right, b, t);
            bool me = s.Value == "1";

            if (me)
            {
                // /material/ui/icon/star_orange.mat: ui.sdf.rfx, 39 x 37 (probe 1900); D renders it (D1 acceptance).
                Icon(EtsGameUi.Place(r, "star", EtsTabletLayout.BadgeIconLeft, EtsTabletLayout.BadgeIconLeft + 32f, 4f, 36f), "/material/ui/icon/star_orange.mat");
                Text(EtsGameUi.Place(r, "words", EtsTabletLayout.CompanyTextLeft, s.Right - s.Left - 4f, 0f, 40f), "Instance master", EtsFont.Normal, TextAlignmentOptions.Left, Colour("clr_sel", SelectedText)); // not in the locale (PLAN §3.7)
            }
            else
            {
                string name = s.Value.Length > 2 ? s.Value.Substring(2) : "";
                Text(EtsGameUi.Place(r, "words", EtsTabletLayout.BadgeIconLeft, s.Right - s.Left - 4f, 0f, 40f), "Master: " + name, EtsFont.Normal, TextAlignmentOptions.Left, Colour("clr_txt_d", DimText)); // not in the locale
            }

            return r;
        }

        private RectTransform DrawFlag(Section s)
        {
            // btn.desktop.top_bar.country: /material/ui/flags/<iso>.mat (64 x 64) cut left=p2 right=p62 top=p12 bottom=p52, shown 30 x 20.
            if (EtsEngine.Files == null)
            {
                return null;
            }

            RectTransform r = EtsGameUi.Place(_chrome, "country", s.Left, s.Right, EtsTabletLayout.TopBarBottom, EtsTabletLayout.TopBarTop);
            float cx = (s.Right - s.Left) * 0.5f;
            EtsBitmap flag = EtsGameUi.Painter.Region("/material/ui/flags/" + s.Value + ".mat", 2, 12, 62, 52, 30, 20, 0xFFFFFFFF);

            if (flag != null)
            {
                EtsGameUi.Painted(EtsGameUi.Place(r, "flag", cx - 15f, cx + 15f, 10f, 30f), flag);
            }

            return r;
        }

        private RectTransform DrawTextSection(Section s, string name)
        {
            RectTransform r = EtsGameUi.Place(_chrome, name, s.Left + 5f, s.Right - 5f, EtsTabletLayout.TopBarBottom, EtsTabletLayout.TopBarTop);
            Text(r, s.Value, EtsFont.Normal, TextAlignmentOptions.Center, Colour("clr_txt", NormalText));
            return r;
        }

        private void BuildMenuBar(RectTransform bar)
        {
            // Groups split by the separators: [Drive] [Jobs .. Media] [System].
            int groups = 0;

            foreach (Category category in _categories)
            {
                groups = Math.Max(groups, category.Group + 1);
            }

            var sizes = new int[groups];

            foreach (Category category in _categories)
            {
                sizes[category.Group]++;
            }

            var lefts = new float[_categories.Count];
            EtsTabletLayout.LayOutMenuBar(sizes, EtsTabletLayout.MenuMargin, lefts);
            const float b = EtsTabletLayout.BarBottom, t = EtsTabletLayout.BarTop;
            int index = 0;

            for (int g = 0; g < groups; g++)
            {
                if (sizes[g] == 0)
                {
                    continue;
                }

                // The group's plate (category_background_look txt.window.bcg_rect4 in clr_desktop_bg_main, margin 0).
                float gl = lefts[index], gr = lefts[index + sizes[g] - 1] + EtsTabletLayout.LinkWidth;
                RectTransform plate = Look(bar, "txt.window.bcg_rect4", "@@clr_desktop_bg_main@@", gl, gr, b, t, null);

                if (plate == null)
                {
                    Plate(EtsGameUi.Place(bar, "group " + g, gl, gr, b, t), Colour("clr_desktop_bg_main", DesktopBackground), true);
                }

                int inGroup = 0;

                foreach (Category category in _categories)
                {
                    if (category.Group != g)
                    {
                        continue;
                    }

                    float l = lefts[index++];
                    Category clicked = category;
                    Action click = () => OnCategory(clicked);
                    RectTransform made;

                    if (category.NoMenu)
                    {
                        made = Look(bar, "btn.desktop.drive", "", l, l + EtsTabletLayout.LinkWidth, b, t, click);

                        if (made == null)
                        {
                            made = EtsGameUi.Place(bar, "drive", l, l + EtsTabletLayout.LinkWidth, b, t);
                            // n_pml: the 50 x 50 drive_n.mat 25 from the top, the caption 85 from the top.
                            Icon(EtsGameUi.Place(made, "icon", 22f, 72f, t - b - 75f, t - b - 25f), "/material/ui/drive_n.mat");
                            Text(EtsGameUi.Place(made, "caption", 2f, 92f, t - b - 108f, t - b - 85f), category.Name, EtsFont.Normal, TextAlignmentOptions.Center, Colour("clr_txt", NormalText));
                            Clickable(made, Colour("clr_desktop_link_bg_s", Hover), click);
                        }
                    }
                    else
                    {
                        string look = EtsTabletLayout.LinkLook(inGroup, sizes[g]);
                        made = Look(bar, look, category.Icon + "|" + category.Name + "|00000000", l, l + EtsTabletLayout.LinkWidth, b, t, click);

                        if (made == null)
                        {
                            made = EtsGameUi.Place(bar, category.Key, l, l + EtsTabletLayout.LinkWidth, b, t);
                            // n_pml: the expand chevron at the top, the 48 x 48 icon 26 from the top, the caption 85 from the top.
                            Icon(EtsGameUi.Place(made, "expand", 37f, 57f, t - b - 21f, t - b - 1f), "/material/ui/desktop/expand.mat");
                            Icon(EtsGameUi.Place(made, "icon", 23f, 71f, t - b - 74f, t - b - 26f), "/material/ui/link/" + category.Icon + ".mat");
                            Text(EtsGameUi.Place(made, "caption", 2f, 92f, t - b - 108f, t - b - 85f), category.Name, EtsFont.Normal, TextAlignmentOptions.Center, Colour("clr_txt", NormalText));
                            Clickable(made, Colour("clr_desktop_link_bg_s", Hover), click);
                        }
                    }

                    if (!category.Live)
                    {
                        // No link of it is in this world: greyed as the game greys what cannot be used (Q-E3); still
                        // pressable, so its menu shows what the category holds and why not.
                        SetUsableLook(made, false);
                    }

                    inGroup++;
                }
            }
        }

        private void OnCategory(Category category)
        {
            EtsGameUi.Note("tablet: " + category.Name);

            if (category.NoMenu || (category.Links.Count == 1 && category.Links[0].Live))
            {
                ClosePopup();
                MenuLink only = null;

                foreach (MenuLink link in category.Links)
                {
                    if (link.Live)
                    {
                        only = link;
                        break;
                    }
                }

                if (only != null)
                {
                    ShowSlot(only.Slot);
                }
                else
                {
                    Toast(Word("drive_tooltip"));
                }

                return;
            }

            if (_popup != null && _popupCategory == category)
            {
                ClosePopup();
                return;
            }

            OpenPopup(category);
        }

        private void OpenPopup(Category category)
        {
            ClosePopup();
            float buttonLeft = 0f;
            float[] lefts = new float[_categories.Count];
            int groups = 0;

            foreach (Category c in _categories)
            {
                groups = Math.Max(groups, c.Group + 1);
            }

            var sizes = new int[groups];

            foreach (Category c in _categories)
            {
                sizes[c.Group]++;
            }

            EtsTabletLayout.LayOutMenuBar(sizes, EtsTabletLayout.MenuMargin, lefts);

            for (int i = 0; i < _categories.Count; i++)
            {
                if (_categories[i] == category)
                {
                    buttonLeft = lefts[i];
                }
            }

            int n = Math.Max(1, category.Links.Count);
            EtsTabletLayout.Popup(buttonLeft, buttonLeft + EtsTabletLayout.LinkWidth, n, out float left, out float right, out float bottom, out float top);
            _popup = FullFrame(_chrome, "menu " + category.Key);
            _popupCategory = category;

            // A click anywhere else in the app closes the menu (and goes nowhere else).
            RectTransform catcher = FullFrame(_popup, "outside");
            Image catcherPlate = Plate(catcher, new Color(0f, 0f, 0f, 0f), true);
            var closer = catcher.gameObject.AddComponent<Button>();
            closer.targetGraphic = catcherPlate;
            closer.transition = Selectable.Transition.None;
            closer.onClick.AddListener(ClosePopup);

            if (Look(_popup, "txt.window.bcg_rect4", "@@clr_desktop_bg_main@@", left, right, bottom, top, null) == null)
            {
                Plate(EtsGameUi.Place(_popup, "plate", left, right, bottom, top), Colour("clr_desktop_bg_main", DesktopBackground), true);
            }

            int columns = Math.Max(1, Math.Min(EtsTabletLayout.ScreenLinkColumns, n));

            for (int i = 0; i < category.Links.Count; i++)
            {
                MenuLink link = category.Links[i];
                int row = i / columns, column = i % columns;
                float l = left + EtsTabletLayout.MenuMargin + column * EtsTabletLayout.ScreenLinkWidth;
                float tt = top - EtsTabletLayout.MenuMargin - row * EtsTabletLayout.ScreenLinkHeight;
                RectTransform made = DrawLink(_popup, "btn.desktop.screen_link", link, l, l + EtsTabletLayout.ScreenLinkWidth, tt - EtsTabletLayout.ScreenLinkHeight, tt, 68f);

                if (!link.Live && made != null)
                {
                    SetUsableLook(made, false);
                }
            }

            if (category.Links.Count == 0)
            {
                Text(EtsGameUi.Place(_popup, "empty", left + 10f, right - 10f, bottom + 7f, top - 7f), "Not available in this world.", EtsFont.Normal, TextAlignmentOptions.Center, Colour("clr_txt_d", DimText)); // not in the locale
            }

            EtsLayers.SetRecursively(_popup, UiLayer);
        }

        /// <summary>A screen link in a menu or the nav bar: the game's look, else icon + label + hover. Greyed links say why not.</summary>
        private RectTransform DrawLink(RectTransform parent, string look, MenuLink link, float left, float right, float bottom, float top, float textLeft)
        {
            MenuLink target = link;
            Action click = () =>
            {
                if (target.Live)
                {
                    ShowSlot(target.Slot);
                }
                else
                {
                    Toast(target.Slot != null ? target.Slot.Page.WhyNot : "Not available in this world."); // not in the locale
                }
            };

            bool linkIcon = IsLinkIcon(link.Icon, out string iconName);
            RectTransform made = linkIcon ? Look(parent, look, iconName + "|" + link.Label + "||00000000|00000000", left, right, bottom, top, click) : null;

            if (made != null)
            {
                return made;
            }

            made = EtsGameUi.Place(parent, "link " + link.Label, left, right, bottom, top);
            float h = top - bottom;
            string material = linkIcon ? "/material/ui/link/" + iconName + ".mat" : link.Icon;

            if (!string.IsNullOrEmpty(material))
            {
                Icon(EtsGameUi.Place(made, "icon", 12f, 52f, h * 0.5f - 20f, h * 0.5f + 20f), material);
            }

            Text(EtsGameUi.Place(made, "label", textLeft, right - left - 5f, 0f, h), link.Label, EtsFont.Normal, TextAlignmentOptions.Left, Colour("clr_txt", NormalText));
            Clickable(made, Colour(look == "btn.screen_link.nav_bar" ? "clr_navbar_s" : "clr_desktop_link_bg_s", look == "btn.screen_link.nav_bar" ? NavHover : Hover), click);
            return made;
        }

        /// <summary>
        /// Greys a link or a category the game's way but keeps it pressable, so it can say why not. A look with a d_pml
        /// (btn.desktop.screen_link, btn.screen_link.nav_bar, btn.desktop.drive: /ui/template/button.sii) shows that
        /// state through D's EtsUiLook - its button stays live, unlike EtsGameUi.SetEnabled; one without (the
        /// btn.desktop_link family has none) and every stand-in drawing is tinted as those d_pml tint everything,
        /// clr_white_alpha_25.
        /// </summary>
        private static void SetUsableLook(RectTransform made, bool usable)
        {
            if (made == null)
            {
                return;
            }

            var look = made.GetComponent<EtsUiLook>();

            if (look != null && look.Disabled != null)
            {
                look.SetEnabled(usable);
                return;
            }

            var group = made.GetComponent<CanvasGroup>();

            if (group == null)
            {
                group = made.gameObject.AddComponent<CanvasGroup>();
            }

            group.alpha = usable ? 1f : DisabledAlpha;
        }

        private static bool IsLinkIcon(string icon, out string name)
        {
            name = icon;

            if (string.IsNullOrEmpty(icon))
            {
                return false;
            }

            const string prefix = "/material/ui/link/";

            if (icon.StartsWith(prefix, StringComparison.Ordinal) && icon.EndsWith(".mat", StringComparison.Ordinal))
            {
                name = icon.Substring(prefix.Length, icon.Length - prefix.Length - 4);
                return true;
            }

            return icon.IndexOf('/') < 0;
        }

        private void ClosePopup()
        {
            if (_popup != null)
            {
                Destroy(_popup.gameObject);
            }

            _popup = null;
            _popupCategory = null;
        }

        /// <summary>The menu bar on the desktop; on a screen, the nav bar with its category's links (/ui/navigation_bar.sii).</summary>
        private void UpdateBars()
        {
            if (_menuBar == null || _navBar == null)
            {
                return;
            }

            bool blocked = _blockedPanel != null && _blockedPanel.gameObject.activeSelf;
            _menuBar.gameObject.SetActive(_onHome && !blocked);

            for (int i = _navBar.childCount - 1; i >= 0; i--)
            {
                Destroy(_navBar.GetChild(i).gameObject);
            }

            bool nav = !_onHome && !blocked && _currentCategory != null;
            _navBar.gameObject.SetActive(nav);

            if (!nav)
            {
                return;
            }

            if (Look(_navBar, "txt.window.bcg_rect4", "@@clr_desktop_bg_main@@", EtsTabletLayout.NavLeft, EtsTabletLayout.NavRight, EtsTabletLayout.NavBottom, EtsTabletLayout.NavTop, null) == null)
            {
                Plate(EtsGameUi.Place(_navBar, "plate", EtsTabletLayout.NavLeft, EtsTabletLayout.NavRight, EtsTabletLayout.NavBottom, EtsTabletLayout.NavTop), Colour("clr_desktop_bg_main", DesktopBackground), true);
            }

            int n = _currentCategory.Links.Count;

            for (int i = 0; i < n; i++)
            {
                MenuLink link = _currentCategory.Links[i];
                EtsTabletLayout.NavLink(n, i, out float l, out float r);

                if (link.Slot == _current)
                {
                    // The screen on show: the nav bar's pressed colour behind it (clr_navbar_p).
                    Plate(EtsGameUi.Place(_navBar, "current", l, r, EtsTabletLayout.NavBottom, EtsTabletLayout.NavTop), Colour("clr_navbar_p", Hover), false);
                }

                RectTransform made = DrawLink(_navBar, "btn.screen_link.nav_bar", link, l, r, EtsTabletLayout.NavBottom, EtsTabletLayout.NavTop, 60f);

                if (!link.Live && made != null)
                {
                    SetUsableLook(made, false);
                }
            }

            EtsLayers.SetRecursively(_navBar, UiLayer);
        }

        /// <summary>4 Hz while visible: the top bar's values (drawn again only when one changed) and the blocked line.</summary>
        private void RefreshChrome()
        {
            if (_chrome == null || Data == null)
            {
                return;
            }

            try
            {
                if (_sections.Count >= 6)
                {
                    SetSection(_sections[0], "");
                    string name = Data.PlayerName ?? "";
                    int level = Data.Level;
                    int bar = EtsTabletLayout.XpBar(Data.LevelProgress);
                    Section personal = _sections[1];

                    if (personal.Made == null || personal.Value == null || !SamePersonal(personal.Value, name, bar, level))
                    {
                        SetSection(personal, "/material/ui/driver/empty|" + name + "|" + bar + "|" + level);
                    }

                    bool master = Data.IsMaster;
                    string masterName = master ? "" : (Data.MasterName ?? "");
                    Section badge = _sections[2];

                    if (badge.Value == null || !SameBadge(badge.Value, master, masterName))
                    {
                        SetSection(badge, master ? "1" : masterName.Length > 0 ? "0|" + masterName : "");
                    }
                    SetSection(_sections[3], CountryIso());
                    long money = Data.Money;

                    if (_sections[4].Made == null || money != _shownMoney)
                    {
                        _shownMoney = money;
                        SetSection(_sections[4], EtsLocale.Money(money));
                    }

                    EtsSession session = EtsSession.Instance;
                    int minutes = session != null ? session.GameClockMinutes : -1;

                    if (minutes != _shownMinutes || (_sections[5].Made == null && minutes >= 0))
                    {
                        _shownMinutes = minutes;
                        SetSection(_sections[5], minutes >= 0 ? EtsLocale.DayTime(minutes) : "");
                    }
                }

                string blocked = Data.Blocked ?? "";
                bool isBlocked = blocked.Length > 0;

                if (_blockedPanel != null && _blockedPanel.gameObject.activeSelf != isBlocked)
                {
                    _blockedPanel.gameObject.SetActive(isBlocked);

                    if (isBlocked)
                    {
                        ClosePopup();
                    }

                    UpdateBars();
                }

                if (_blockedText != null && _blockedText.text != blocked)
                {
                    _blockedText.text = blocked;
                }
            }
            catch (Exception e)
            {
                if (!_refreshFailureReported)
                {
                    _refreshFailureReported = true;
                    Debug.LogWarning("[SomniumEuroTruck] Tablet: top bar refresh: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        private long _shownMoney = long.MinValue;
        private int _shownMinutes = int.MinValue;
        private bool _refreshFailureReported;

        private static bool SamePersonal(string value, string name, int bar, int level)
        {
            // "/material/ui/driver/empty|name|bar|level" without building the new string (4 Hz, no allocation).
            int a = value.IndexOf('|');
            int c = a >= 0 ? value.IndexOf('|', a + 1) : -1;
            int d = c >= 0 ? value.IndexOf('|', c + 1) : -1;

            if (d < 0 || c - a - 1 != name.Length || string.CompareOrdinal(value, a + 1, name, 0, name.Length) != 0)
            {
                return false;
            }

            return ParseInt(value, c + 1, d) == bar && ParseInt(value, d + 1, value.Length) == level;
        }

        /// <summary>The badge's value is "1" (this player is the master), "0|name" or "": compared without building it (4 Hz).</summary>
        private static bool SameBadge(string value, bool master, string name)
        {
            if (master)
            {
                return value == "1";
            }

            if (name.Length == 0)
            {
                return value.Length == 0;
            }

            return value.Length == name.Length + 2 && value[0] == '0' && value[1] == '|' && string.CompareOrdinal(value, 2, name, 0, name.Length) == 0;
        }

        private static int ParseInt(string s, int from, int to)
        {
            int n = 0;

            for (int i = from; i < to; i++)
            {
                char ch = s[i];

                if (ch < '0' || ch > '9')
                {
                    return int.MinValue;
                }

                n = n * 10 + (ch - '0');
            }

            return n;
        }

        /// <summary>
        /// The HQ city's country for the flag. btn.desktop.top_bar.country takes the country's iso_country_code ("pol":
        /// /def/country/poland.sui; its n_pml draws /material/ui/flags/%0.mat, /ui/template/button.sii:5008). From D's
        /// catalogue when it has the city (the master always does: it chose from it); otherwise - a joiner who went
        /// straight to Loading never builds the catalogue (PLAN §3.6) - from the game's own two defs, read once on a worker
        /// thread (FlagFromDefs). Only a found answer is kept, so a catalogue that arrives later is still used; the
        /// catalogue is walked by index (no enumerator allocation at 4 Hz) and at most once a second until then.
        /// </summary>
        private string CountryIso()
        {
            EtsSession session = EtsSession.Instance;

            if (session == null || !session.HasChoice)
            {
                return "";
            }

            ulong city = session.Choice.City;

            if (city == _isoCity)
            {
                return _iso;
            }

            if (Time.unscaledTime < _nextIsoTry)
            {
                return "";
            }

            _nextIsoTry = Time.unscaledTime + 1f;
            IReadOnlyList<EtsHqCity> cities = EtsHqCatalog.Cities;

            for (int i = 0; cities != null && i < cities.Count; i++)
            {
                EtsHqCity hq = cities[i];

                if (hq != null && hq.Token == city && !string.IsNullOrEmpty(hq.CountryIso))
                {
                    _isoCity = city;
                    _iso = hq.CountryIso;
                    return _iso;
                }
            }

            FlagFromDefs(city);
            return "";
        }

        private ulong _isoCity = ulong.MaxValue;
        private string _iso = "";
        private float _nextIsoTry;
        private ulong _isoAskedFor = ulong.MaxValue;
        private EtsWorker _isoReader;

        /// <summary>
        /// The flag without D's catalogue: EtsTabletLayout.CountryIsoOf (the city's def, then its country's), on a worker
        /// thread once per city - it is pure (EtsFileSystem.ReadText and EtsSii only). No answer: no flag.
        /// </summary>
        private void FlagFromDefs(ulong city)
        {
            EtsFileSystem files = EtsEngine.Files;

            if (files == null || _isoReader != null || _isoAskedFor == city)
            {
                return;
            }

            _isoAskedFor = city;
            string name = EtsToken.Decode(city);
            _isoReader = new EtsWorker("Ets tablet flag");
            _isoReader.Post(() => EtsTabletLayout.CountryIsoOf(files, name), (result, failure) =>
            {
                string iso = result as string;

                if (failure == null && !string.IsNullOrEmpty(iso) && _isoAskedFor == city)
                {
                    _isoCity = city;
                    _iso = iso;
                }

                Debug.Log("[SomniumEuroTruck] Tablet: the flag for " + name + " from the game's defs: " +
                          (failure != null ? "failed (" + failure.GetType().Name + ": " + failure.Message + ")" : string.IsNullOrEmpty(iso) ? "no iso_country_code found" : iso) + ".");
                EtsWorker done = _isoReader;
                _isoReader = null;
                done?.Dispose();
            });
        }

        // ------------------------------------------------------------------ help texts (the System page)

        /// <summary>The desktop keys as they stand: R and Escape are the client's while the app is on its tablet.</summary>
        public static string DesktopHelp()
        {
            if (EtsTabletLink.KeysRetired)
            {
                return "W/S drive (S reverses from a stop)   A/D steer   Space brake   P parking brake   H horn\n1/2 camera   C cruise control   T trailer   [ ] indicators   L lights   K main beam   Shift+P wipers\nTab tablet (ETS 2)   On foot: click the truck to get in"; // not in the locale
            }

            if (!EtsFeatures.NewFlow)
            {
                // The editor preview: Escape is still the old menu's (EtsTabletLayout.MenuKey), only Tab is this panel's.
                return "W/S drive (S reverses from a stop)   A/D steer   Space brake   P parking brake   H horn\n1/2 camera   C cruise control   T trailer   [ ] indicators   L lights   K main beam   Shift+P wipers\nR respawn   Esc menu   Tab this panel   On foot: click the truck to get in"; // not in the locale
            }

            return "W/S drive (S reverses from a stop)   A/D steer   Space brake   P parking brake   H horn\n1/2 camera   C cruise control   T trailer   [ ] indicators   L lights   K main beam   Shift+P wipers\nR respawn   Esc / Tab this panel   On foot: click the truck to get in"; // not in the locale
        }
    }

    /// <summary>
    /// Drive (the game's Drive button, `.drive` type no_menu, "Back to the driver's seat."): what EtsMenu offered
    /// while driving, in the game's category [tablet §4.9], and the way into the own truck:
    ///   - "Back to the driver's seat." (drive_tooltip): seated, back to driving (the app closes); on foot with a truck, into
    ///     its driver's seat through the truck's own door (EtsTruckDoor.Use, owner B, with every rule it keeps - the app
    ///     closes first, because the door refuses while the app is on screen: EtsTruckDoor.UsableOnRig); with no truck,
    ///     Company - Truck Preference;
    ///   - get out and walk (the old left Y, which is the client's tablet tap), refused for a passenger above 10 km/h (Q-B2);
    ///   - the truck back on the road (the old R, which is the client's PopUpMenu); another truck (Company);
    ///   - the camera and the sound;
    ///   - the controls as they stand (EtsVr.Help / EtsTabletApp.DesktopHelp), under the game's "Controls".
    /// </summary>
    public sealed class EtsPageDrive : EtsTabletPage
    {
        public override string Caption => EtsTabletApp.Word("drive");
        public override string Icon => "/material/ui/drive_n.mat";
        public override string Id => "Drive";
        public override string Category => "drive";

        private TextMeshProUGUI _state, _help;
        private Button _getOut, _respawn, _choose, _camera;

        protected override void Build()
        {
            // The window 270..1170 x 100..830 in the 1440 x 900 frame, under the top bar (853) and over the nav bar (77):
            // the state line, three rows of two btn.normal buttons, then the controls.
            const float l = 270f, r = 1170f, b = 100f, t = 830f;
            const float half = (r - l) * 0.5f;
            const float c1 = l + 40f, c2 = l + half + 10f, w = half - 50f;
            EtsTabletApp.Window(Root, Caption, l, r, b, t);
            _state = EtsTabletApp.Text(EtsGameUi.Place(Root, "state", l + 30f, r - 30f, t - 100f, t - 60f), "", EtsFont.Normal, TextAlignmentOptions.Center,
                EtsTabletApp.Colour("clr_txt", EtsTabletApp.NormalText));
            float y = t - 170f;
            App.Button(Root, EtsTabletApp.Word("drive_tooltip"), c1, c1 + w, y, y + 50f, GetIn);
            _getOut = App.Button(Root, "Get out and walk", c2, c2 + w, y, y + 50f, GetOut); // not in the locale (the old left Y)
            y -= 70f;
            _respawn = App.Button(Root, "Put the truck back on the road", c1, c1 + w, y, y + 50f, () => // not in the locale (the old R)
            {
                EtsMenu menu = App.Menu;
                EtsTruckController truck = menu != null ? menu.Truck : null;

                if (truck != null && menu.Driving)
                {
                    truck.Respawn();
                    App.Close();
                }
            });
            _choose = App.Button(Root, "Choose another truck", c2, c2 + w, y, y + 50f, () => App.Show(EtsTabletApp.TruckPreferenceId)); // not in the locale
            y -= 70f;
            _camera = App.Button(Root, "Camera: inside / behind", c1, c1 + w, y, y + 50f, () => // not in the locale
            {
                EtsTruckCamera camera = App.Menu != null ? App.Menu.Camera : null;
                if (camera != null) camera.View = camera.View == EtsTruckCamera.Mode.Interior ? EtsTruckCamera.Mode.Chase : EtsTruckCamera.Mode.Interior;
            });
            App.Button(Root, "Sound on / off", c2, c2 + w, y, y + 50f, () => // not in the locale
            {
                EtsTruckAudio audio = App.Menu != null ? App.Menu.Audio : null;
                if (audio != null) audio.Volume = audio.Volume > 0.01f ? 0f : 0.5f;
            });
            y -= 70f;
            App.Button(Root, "Radio on / off", c1, c1 + w, y, y + 50f, () => // not in the locale; the game's radio, off until turned on
            {
                EtsRadio radio = EtsRadio.Ensure(App.gameObject);
                if (radio == null) return;
                radio.Toggle();
                App.Toast(radio.Status);
            });
            App.Button(Root, "Radio stations", c2, c2 + w, y, y + 50f, () => App.Show(EtsPageRadio.PageId)); // not in the locale
            EtsTabletApp.Text(EtsGameUi.Place(Root, "controls title", l + 30f, r - 30f, y - 60f, y - 25f), EtsTabletApp.Word("controller"), EtsFont.Big, TextAlignmentOptions.Left,
                EtsTabletApp.Colour("clr_sel", EtsTabletApp.SelectedText));
            _help = EtsTabletApp.Text(EtsGameUi.Place(Root, "controls", l + 30f, r - 30f, b + 20f, y - 70f), "", EtsFont.Normal, TextAlignmentOptions.TopLeft,
                EtsTabletApp.Colour("clr_txt_d", EtsTabletApp.DimText));
            _help.textWrappingMode = TextWrappingModes.Normal;
            _help.enableAutoSizing = false;
        }

        /// <summary>The game's Drive: back to the driver's seat, from wherever the player is.</summary>
        private void GetIn()
        {
            EtsMenu menu = App.Menu;

            if (menu == null || !menu.OnFoot)
            {
                App.Close(); // seated (or no Somnium player: the editor drives from the start) - back to driving
                return;
            }

            EtsTruckRig own = EtsLocalTruck.Own;

            if (own == null || own.Controller == null)
            {
                App.Toast("Choose your truck first (Company)"); // not in the locale (PLAN §3.5's wording)
                App.Show(EtsTabletApp.TruckPreferenceId);
                return;
            }

            if (!own.Controller.Placed)
            {
                App.Toast("Your truck is still being placed - a moment ..."); // not in the locale
                return;
            }

            App.Close();
            EtsTruckDoor door = own.GetComponent<EtsTruckDoor>();

            if (door == null)
            {
                menu.EnterTruck(own, 0); // a rig without a handle: nothing to take off it first
            }
            else if (door.Usable)
            {
                door.Use("tablet: Drive");
            }
            else
            {
                App.Toast(EtsVr.Present ? "Walk to your truck, point at it and pull the trigger to get in." : "Walk to your truck and click it to get in."); // not in the locale
            }
        }

        private void GetOut()
        {
            EtsMenu menu = App.Menu;

            if (menu == null || menu.OnFoot)
            {
                return;
            }

            // A passenger may not get out above 10 km/h (Q-B2): the seat says why (EtsTruckSeat.CanGetOut, owner B).
            if (menu.Seat != null && menu.Seat.Seated && !menu.Seat.CanGetOut(out string why))
            {
                App.Toast(why);
                return;
            }

            menu.GetOut();

            if (menu.OnFoot)
            {
                App.Close();
            }
        }

        private int _shownKey = int.MinValue;

        public override void Tick()
        {
            EtsMenu menu = App.Menu;
            EtsTruckController truck = menu != null ? menu.Truck : null;
            bool onFoot = menu != null && menu.OnFoot;
            bool driving = menu != null && menu.Driving;
            bool seated = menu != null && menu.Seat != null && menu.Seat.HasPlayer && !onFoot;
            bool own = EtsLocalTruck.Own != null;

            // A passenger (Phase 3: Convoys' Ride along or a click on another player's truck) sees whose truck and its
            // speed; "Get out and walk" stays pressable but greyed while the seat refuses it (Q-B2: 10 km/h), and a
            // press says why (GetOut below, EtsTruckSeat.CanGetOut, owner B).
            bool passenger = seated && menu.Seat.Seated && menu.Seat.SeatIndex != 0;
            bool canGetOut = !passenger || menu.Seat.CanGetOut(out _);

            // The line is written again only when what it says changes (whole km/h, gear, on foot, no truck; a passenger's
            // km/h and whether they may get out).
            int kmh = truck != null ? Mathf.RoundToInt(truck.SpeedKmh) : 0;
            int key = truck == null ? (own ? -3 : -1) : onFoot ? -2 : passenger ? -10 - kmh * 2 - (canGetOut ? 1 : 0) : kmh * 64 + (truck.Gear + 32);

            if (_state != null && key != _shownKey)
            {
                _shownKey = key;

                if (passenger)
                {
                    EtsTruckRig rig = menu.Seat.Rig;
                    EtsRiders riders = EtsRiders.Instance;
                    _state.text = EtsTabletLayout.PassengerLine(rig != null ? EtsNet.NameOf(rig.OwnerPlayerId) : "", kmh, canGetOut,
                        riders != null ? riders.PassengerExitMaxKmh : 10f);
                }
                else
                {
                    _state.text = truck == null && !own ? "No truck yet - choose one under Company." : // not in the locale
                                  truck == null || onFoot ? "On foot. \"Back to the driver's seat.\" takes you to your truck." : // not in the locale
                                  kmh + " km/h, gear " + truck.GearLabel;
                }
            }

            string help = EtsVr.Present ? EtsVr.Help : EtsTabletApp.DesktopHelp(); // constants: comparing them allocates nothing

            if (_help != null && !ReferenceEquals(_help.text, help) && _help.text != help)
            {
                _help.text = help;
            }

            EtsTabletApp.SetUsable(_getOut, seated && canGetOut, seated);
            EtsTabletApp.SetUsable(_respawn, driving && truck != null);
            EtsTabletApp.SetUsable(_camera, (driving || seated) && truck != null);
            EtsTabletApp.SetUsable(_choose, App.HasPage(EtsTabletApp.TruckPreferenceId));
        }
    }

    /// <summary>
    /// Convoys - Active Convoy (the game's convoy.current.session link in the `.online` category, icon community; PLAN C4,
    /// §5.5 "Passenger", [tablet §4] row 4): every player in the room, one row each from EtsNet.Players (owner A, one row
    /// per claimed slot, the local player's own composed at 4 Hz) - the master's star from that player's own slot bit
    /// (PLAN §12 U3: the server has no GetMasterClient, so nobody names the master for someone else), their status (on
    /// foot, driving, passenger with whom, standing on whose truck, loading n %), their truck and their job. Jobs stay each
    /// player's own (the user: "jobs aren't shared"); this only shows them.
    ///
    /// Two buttons per other player:
    ///   - "Ride along": EtsNet.RequestSeat(their slot, 1) exactly as clicking their truck does (EtsTruckDoor.UseRig, owner
    ///     B): EtsRiders stops publishing while EtsNet writes the requesting rider record ([net §2.7]), and a grant sits the
    ///     player in their drawn cab through EtsMenu.EnterTruck(rig, 1) (sitting pose, B). The answer - granted, refused
    ///     with the owner's reason, or "did not answer" (A's 3 s, else this page's own EtsTabletLayout.SeatAnswerSeconds) -
    ///     shows on the page's result line and the HUD.
    ///   - "Go to": a teleport beside their truck's passenger door (EtsTabletApp.GoTo), refused while seated or while the
    ///     truck is driving off.
    /// A button that cannot act now is greyed but still takes the press, so pressing it says why (the rules are
    /// EtsTabletLayout.RideAlong / GoTo, tested by probe 2270).
    ///
    /// The 4 Hz refresh allocates nothing while nothing shown changes: rows are made once per build, each row's text is
    /// rewritten only when its key (the fields it shows) changes, names are cached per token, the order is an in-place
    /// insertion sort, and the drawn puppets are found through EtsNet.TryGetVehicleFrame and kept by slot.
    /// </summary>
    public class EtsPageConvoys : EtsTabletPage
    {
        public override string Caption => EtsTabletApp.Word("convoy_current_session");
        public override string Icon => "/material/ui/link/community.mat";
        public override string Id => "Convoys";
        public override string Category => "online";
        public override string ScreenLink => "convoy.current.session";

        // The window 60..1380 x 100..830 in the 1440 x 900 frame (under the top bar, over the nav bar, as the Drive page).
        private const float L = 60f, R = 1380f, B = 100f, T = 830f;
        private const float RowHeight = 56f, RowStep = 62f, HeaderTop = T - 62f, FirstRowTop = T - 102f;

        private sealed class Row
        {
            public RectTransform Root;
            public GameObject Star;
            public TextMeshProUGUI Name, Status, Truck, Job;
            public Button Ride, Go;
            public long Key = long.MinValue;
            public string ShownName;
            public int Refusals = -1;
            public bool Mine;
            public EtsPlayerView View;
        }

        private readonly Row[] _rows = new Row[EtsTabletLayout.ConvoyRows];
        private TextMeshProUGUI _result, _pageLabel;
        private Button _prev, _next;
        private int _page, _shownPage = -1, _shownPages = -1;

        // One pass's players, in the Convoys order (no allocation: fixed arrays of EtsNet.MaxSlots).
        private readonly EtsPlayerView[] _views = new EtsPlayerView[EtsNet.MaxSlots];
        private readonly int[] _slots = new int[EtsNet.MaxSlots];
        private readonly bool[] _mine = new bool[EtsNet.MaxSlots];
        private readonly int[] _order = new int[EtsNet.MaxSlots];
        private int _count;

        // The drawn puppets by slot (C builds them, A owns them); a scene search refreshes it only when a truck is missing.
        private readonly EtsTruckRig[] _rigBySlot = new EtsTruckRig[EtsNet.MaxSlots];
        private float _nextRigSearch;

        // The seat request out, if any (one at a time).
        private bool _asking;
        private bool _holding; // this page holds EtsRiders' publishing (RideAlong); only then does EndAsk lift it
        private int _askToken;
        private float _askSince;
        private string _askName = "";

        private string _said = "";
        private float _saidUntil;

        private static readonly Dictionary<ulong, string> CityNames = new Dictionary<ulong, string>();
        private static readonly Dictionary<ulong, string> CargoNames = new Dictionary<ulong, string>();
        private static readonly Dictionary<int, string> TruckNames = new Dictionary<int, string>();
        private static EtsTruckCatalog _namesFrom;

        protected override void Build()
        {
            EtsTabletApp.Window(Root, Caption, L, R, B, T);
            Color dim = EtsTabletApp.Colour("clr_txt_d", EtsTabletApp.DimText);
            Header("Driver", L + 56f, L + 300f, dim); // not in the locale
            Header("Status", L + 310f, L + 570f, dim); // not in the locale
            Header("Truck", L + 580f, L + 800f, dim); // not in the locale
            Header("Job", L + 810f, L + 1040f, dim); // not in the locale

            for (int i = 0; i < _rows.Length; i++)
            {
                _rows[i] = MakeRow(i);
            }

            _result = EtsTabletApp.Text(EtsGameUi.Place(Root, "result", L + 20f, R - 300f, B + 12f, B + 64f), _said, EtsFont.Normal, TextAlignmentOptions.Left,
                EtsTabletApp.Colour("clr_txt", EtsTabletApp.NormalText));
            _result.textWrappingMode = TextWrappingModes.Normal;
            _prev = App.Button(Root, "<", R - 280f, R - 220f, B + 14f, B + 62f, () => Turn(-1));
            _pageLabel = EtsTabletApp.Text(EtsGameUi.Place(Root, "page", R - 215f, R - 85f, B + 14f, B + 62f), "", EtsFont.Normal, TextAlignmentOptions.Center, dim);
            _next = App.Button(Root, ">", R - 80f, R - 20f, B + 14f, B + 62f, () => Turn(+1));
            _shownPage = -1;
            _shownPages = -1;
        }

        private void Header(string words, float left, float right, Color colour)
        {
            EtsTabletApp.Text(EtsGameUi.Place(Root, "header " + words, left, right, HeaderTop - 34f, HeaderTop), words, EtsFont.Small, TextAlignmentOptions.Left, colour);
        }

        private Row MakeRow(int i)
        {
            float top = FirstRowTop - i * RowStep;
            var row = new Row();
            row.Root = EtsGameUi.Place(Root, "row " + (i + 1), L + 8f, R - 8f, top - RowHeight, top);
            float x0 = L + 8f;

            if ((i & 1) == 0)
            {
                EtsTabletApp.Plate(EtsGameUi.Place(row.Root, "stripe", 0f, R - L - 16f, 0f, RowHeight), new Color(1f, 1f, 1f, 0.035f), false);
            }

            // /material/ui/icon/star_orange.mat (ui.sdf.rfx, 39 x 37; the top bar's master badge, PLAN §3.7); a "*" in clr_sel
            // stands in while D's SDF painter has not drawn it.
            RectTransform star = EtsGameUi.Place(row.Root, "master", L + 16f - x0, L + 48f - x0, 12f, 44f);

            if (EtsTabletApp.Icon(star, "/material/ui/icon/star_orange.mat") == null)
            {
                EtsTabletApp.Text(star, "*", EtsFont.Big, TextAlignmentOptions.Center, EtsTabletApp.Colour("clr_sel", EtsTabletApp.SelectedText));
            }

            row.Star = star.gameObject;
            Color normal = EtsTabletApp.Colour("clr_txt", EtsTabletApp.NormalText), dim = EtsTabletApp.Colour("clr_txt_d", EtsTabletApp.DimText);
            row.Name = EtsTabletApp.Text(EtsGameUi.Place(row.Root, "name", L + 56f - x0, L + 300f - x0, 8f, 48f), "", EtsFont.Normal, TextAlignmentOptions.Left, normal);
            row.Status = EtsTabletApp.Text(EtsGameUi.Place(row.Root, "status", L + 310f - x0, L + 570f - x0, 8f, 48f), "", EtsFont.Normal, TextAlignmentOptions.Left, normal);
            row.Truck = EtsTabletApp.Text(EtsGameUi.Place(row.Root, "truck", L + 580f - x0, L + 800f - x0, 8f, 48f), "", EtsFont.Normal, TextAlignmentOptions.Left, dim);
            row.Job = EtsTabletApp.Text(EtsGameUi.Place(row.Root, "job", L + 810f - x0, L + 1040f - x0, 8f, 48f), "", EtsFont.Normal, TextAlignmentOptions.Left, dim);
            int index = i;
            row.Ride = App.Button(row.Root, "Ride along", L + 1050f - x0, L + 1175f - x0, 6f, 50f, () => RideAlong(index)); // not in the locale
            row.Go = App.Button(row.Root, "Go to", L + 1185f - x0, L + 1300f - x0, 6f, 50f, () => GoTo(index)); // not in the locale
            row.Root.gameObject.SetActive(false);
            return row;
        }

        private void Turn(int by)
        {
            _page += by;
            Tick();
        }

        public override void Shown()
        {
            Tick();
        }

        // ------------------------------------------------------------------ the 4 Hz refresh

        public override void Tick()
        {
            if (_asking && Time.unscaledTime - _askSince > EtsTabletLayout.SeatAnswerSeconds)
            {
                EndAsk();
                Say(_askName + " did not answer."); // [net §2.7] step 3's words; A's own timeout normally says it first
            }

            if (_said.Length > 0 && Time.unscaledTime > _saidUntil)
            {
                _said = "";
            }

            Gather();
            int first = EtsTabletLayout.ConvoyPageRows(_count, ref _page, out int shown);
            int pages = EtsTabletLayout.ConvoyPages(_count);

            for (int i = 0; i < _rows.Length; i++)
            {
                Row row = _rows[i];

                if (row == null)
                {
                    continue;
                }

                if (i < shown)
                {
                    Fill(row, _views[_order[first + i]]);
                }
                else if (row.Root.gameObject.activeSelf)
                {
                    row.Root.gameObject.SetActive(false);
                    row.View = null;
                    row.Key = long.MinValue;
                }
            }

            if (_page != _shownPage || pages != _shownPages)
            {
                _shownPage = _page;
                _shownPages = pages;

                if (_pageLabel != null)
                {
                    _pageLabel.text = pages > 1 ? (_page + 1) + " / " + pages : "";
                }

                if (_prev != null) _prev.gameObject.SetActive(pages > 1);
                if (_next != null) _next.gameObject.SetActive(pages > 1);
            }

            if (pages > 1)
            {
                EtsTabletApp.SetUsable(_prev, _page > 0);
                EtsTabletApp.SetUsable(_next, _page < pages - 1);
            }

            string line = _said.Length > 0 ? _said : _count <= 1 ? AloneLine : "";

            if (_result != null && !ReferenceEquals(_result.text, line) && _result.text != line)
            {
                _result.text = line;
            }
        }

        private const string AloneLine = "You are the only driver here so far. Everyone who joins shows up in this list."; // not in the locale

        /// <summary>The players of this pass, in the Convoys order: the local player first, then by slot.</summary>
        private void Gather()
        {
            _count = 0;
            IReadOnlyList<EtsPlayerView> players = ReadPlayers();

            if (players == null)
            {
                return;
            }

            for (int i = 0; i < players.Count && _count < _views.Length; i++)
            {
                EtsPlayerView view = players[i];

                if (view == null || view.Status == EtsPlayerWire.Status.Free)
                {
                    continue;
                }

                _views[_count] = view;
                _slots[_count] = view.Slot;
                _mine[_count] = view.IsLocal;
                _count++;
            }

            EtsTabletLayout.ConvoyOrder(_slots, _mine, _order, _count);
        }

        private void Fill(Row row, EtsPlayerView view)
        {
            if (!row.Root.gameObject.activeSelf)
            {
                row.Root.gameObject.SetActive(true);
            }

            row.View = view;
            EtsTabletLayout.ConvoyState state = StateOf(view.Status);
            EtsTabletLayout.RideOn on = RideOnOf(view, state, out int ownerSlot);
            long key = view.PlayerId;
            key = key * 131 + view.Slot;
            key = key * 131 + (int)state;
            key = key * 131 + (state == EtsTabletLayout.ConvoyState.Loading ? view.LoadPercent : 0);
            key = key * 131 + (int)on;
            key = key * 131 + ownerSlot;
            key = key * 131 + (view.IsMaster ? 1 : 0) + (view.IsLocal ? 2 : 0) + (view.HasTruck ? 4 : 0) + (view.HasJob ? 8 : 0);
            key = key * 131 + (view.HasTruck ? view.Truck.Preview * 256 + (view.Truck.CatalogueHash & 0xFF) : -1);
            key = key * 131 + (view.HasJob ? (long)(view.CargoToken ^ (view.DestinationCityToken * 31UL)) : 0L);
            key = key * 131 + NamesReady(); // the names come as C's catalogue, the map and the locale arrive
            string name = view.Name ?? "";

            if (key != row.Key || name != row.ShownName)
            {
                row.Key = key;
                row.ShownName = name;
                row.Mine = view.IsLocal;
                string shownName = DisplayName(view);
                row.Name.text = view.IsLocal ? shownName + " (you)" : shownName; // not in the locale
                row.Status.text = EtsTabletLayout.ConvoyStatus(state, view.LoadPercent, on, OwnerName(ownerSlot), ownerSlot >= 0 && IsMySlot(ownerSlot));
                row.Truck.text = view.HasTruck ? TruckName(view.Truck) : "No truck"; // not in the locale
                row.Job.text = EtsTabletLayout.ConvoyJob(view.HasJob, CargoName(view.CargoToken), CityName(view.DestinationCityToken));

                if (row.Star.activeSelf != view.IsMaster)
                {
                    row.Star.SetActive(view.IsMaster);
                }

                row.Ride.gameObject.SetActive(!view.IsLocal);
                row.Go.gameObject.SetActive(!view.IsLocal);
            }

            if (view.IsLocal)
            {
                return;
            }

            EtsTabletLayout.ConvoyAsk ask = Ask(view, out _);
            bool ride = EtsTabletLayout.RideAlong(ask) == EtsTabletLayout.ConvoyRefusal.None;
            bool go = EtsTabletLayout.GoTo(ask) == EtsTabletLayout.ConvoyRefusal.None;
            int refusals = (ride ? 1 : 0) + (go ? 2 : 0);

            if (refusals != row.Refusals)
            {
                row.Refusals = refusals;
                EtsTabletApp.SetUsable(row.Ride, ride, true);
                EtsTabletApp.SetUsable(row.Go, go, true);
            }
        }

        // ------------------------------------------------------------------ the sources (A's contract; virtual for the offline app tests)

        /// <summary>
        /// EtsNet.Players (PLAN §7.A). Virtual only so the offline app tests can list simulated players through a page of the
        /// same Id added before the app's own (EtsTabletApp.AddPage keeps the first page of an Id); the world never overrides it.
        /// </summary>
        protected virtual IReadOnlyList<EtsPlayerView> ReadPlayers()
        {
            EtsNet net = EtsNet.Instance;
            return net != null ? net.Players : null;
        }

        /// <summary>EtsNet.RemoteRiders (PLAN §7.A, B's list): who rides or stands on what. Virtual as ReadPlayers.</summary>
        protected virtual IReadOnlyList<EtsRemoteRider> ReadRiders()
        {
            EtsNet net = EtsNet.Instance;
            return net != null ? net.RemoteRiders : null;
        }

        /// <summary>Whether a seat request can be sent at all (EtsNet exists). Virtual as ReadPlayers.</summary>
        protected virtual bool CanRequestSeat => EtsNet.Instance != null;

        /// <summary>Whether there is a Somnium player to seat or move (none in the editor). Virtual as ReadPlayers.</summary>
        protected virtual bool PlayerPresent(EtsTruckSeat seat)
        {
            return seat != null && seat.HasPlayer;
        }

        /// <summary>
        /// EtsNet.RequestSeat(ownerSlot, seat, granted, refused) (PLAN §7.A, [net §2.7]): the owner's client grants within
        /// 0.25 s plus the network delay, refuses ("The seat is taken"), or it times out ("{owner} did not answer", 3 s).
        /// Virtual as ReadPlayers.
        /// </summary>
        protected virtual void RequestSeat(int ownerSlot, int seat, Action granted, Action<string> refused)
        {
            EtsNet net = EtsNet.Instance;

            if (net == null)
            {
                refused?.Invoke("Not connected to the room."); // not in the locale
                return;
            }

            net.RequestSeat(ownerSlot, seat, granted, refused);
        }

        /// <summary>The page's result line as shown (for the log and the offline tests).</summary>
        public string ResultLine => _result != null ? _result.text : _said;

        /// <summary>Whether a seat request of this page is waiting for its answer.</summary>
        public bool Asking => _asking;

        // ------------------------------------------------------------------ the rules' inputs

        private static EtsTabletLayout.ConvoyState StateOf(EtsPlayerWire.Status status)
        {
            switch (status)
            {
                case EtsPlayerWire.Status.Loading: return EtsTabletLayout.ConvoyState.Loading;
                case EtsPlayerWire.Status.ChoosingHq: return EtsTabletLayout.ConvoyState.ChoosingHq;
                case EtsPlayerWire.Status.Waiting: return EtsTabletLayout.ConvoyState.Waiting;
                case EtsPlayerWire.Status.OnFoot: return EtsTabletLayout.ConvoyState.OnFoot;
                case EtsPlayerWire.Status.Driving: return EtsTabletLayout.ConvoyState.Driving;
                case EtsPlayerWire.Status.Passenger: return EtsTabletLayout.ConvoyState.Passenger;
                case EtsPlayerWire.Status.Standing: return EtsTabletLayout.ConvoyState.Standing;
                default: return EtsTabletLayout.ConvoyState.Joining;
            }
        }

        /// <summary>
        /// What a passenger or a standing rider is on, and the owner slot of that truck or trailer (-1 for a car or none):
        /// the local player from the seat (B), anyone else from their rider record (EtsNet.RemoteRiders: kind, index = the
        /// owner's slot for a tractor or trailer, PLAN §5.3 int 33).
        /// </summary>
        private EtsTabletLayout.RideOn RideOnOf(EtsPlayerView view, EtsTabletLayout.ConvoyState state, out int ownerSlot)
        {
            ownerSlot = -1;

            if (state != EtsTabletLayout.ConvoyState.Passenger && state != EtsTabletLayout.ConvoyState.Standing)
            {
                return EtsTabletLayout.RideOn.None;
            }

            if (view.IsLocal)
            {
                EtsTruckSeat seat = App.Menu != null ? App.Menu.Seat : null;
                EtsTruckRig rig = seat != null && seat.Seated ? seat.Rig : null;

                if (rig != null)
                {
                    ownerSlot = rig.OwnerSlot;
                    return EtsTabletLayout.RideOn.Truck;
                }

                return EtsTabletLayout.RideOn.None;
            }

            IReadOnlyList<EtsRemoteRider> riders = ReadRiders();

            if (riders == null)
            {
                return EtsTabletLayout.RideOn.None;
            }

            for (int i = 0; i < riders.Count; i++)
            {
                EtsRemoteRider rider = riders[i];

                if (rider.PlayerId != view.PlayerId)
                {
                    continue;
                }

                switch (rider.Sampled.Kind)
                {
                    case EtsTargetKind.Tractor:
                        ownerSlot = rider.Sampled.Index;
                        return EtsTabletLayout.RideOn.Truck;
                    case EtsTargetKind.Trailer:
                        ownerSlot = rider.Sampled.Index;
                        return EtsTabletLayout.RideOn.Trailer;
                    case EtsTargetKind.TrafficCar:
                        return EtsTabletLayout.RideOn.Car;
                    default:
                        return EtsTabletLayout.RideOn.None;
                }
            }

            return EtsTabletLayout.RideOn.None;
        }

        private EtsTabletLayout.ConvoyAsk Ask(EtsPlayerView view, out EtsTruckRig rig)
        {
            EtsMenu menu = App.Menu;
            EtsTruckSeat seat = menu != null ? menu.Seat : null;
            EtsTruckRig sitting = seat != null && seat.Seated ? seat.Rig : null;
            EtsSession session = EtsSession.Instance;
            rig = view.HasTruck ? RigOf(view.Slot) : null;
            EtsTruckController truck = rig != null ? rig.Controller : null;

            return new EtsTabletLayout.ConvoyAsk
            {
                Mine = view.IsLocal,
                InWorld = session == null || session.Current == EtsPhase.InWorld,
                HavePlayer = PlayerPresent(seat),
                TheyHaveTruck = view.HasTruck,
                TruckDrawn = truck != null && truck.Placed,
                SeatFree = view.PassengerSeatFree,
                Asking = _asking,
                Seated = seat != null && seat.Seated,
                SeatedAsDriver = seat != null && seat.IsDriver,
                RidingWithThem = sitting != null && sitting.IsPuppet && sitting.OwnerSlot == view.Slot,
                TheirKmh = truck != null ? truck.SpeedKmh : 0f
            };
        }

        /// <summary>
        /// The drawn puppet of a slot's truck: kept by slot and checked each time (destroyed, or no longer that slot's), found
        /// again through EtsNet.TryGetVehicleFrame (no allocation); only without EtsNet (the offline app tests) by a scene
        /// search, at most every 2 s while a truck is missing.
        /// </summary>
        private EtsTruckRig RigOf(int slot, bool searchNow = false)
        {
            if (slot < 0 || slot >= _rigBySlot.Length)
            {
                return null;
            }

            EtsTruckRig rig = _rigBySlot[slot];

            if (rig != null && rig.IsPuppet && rig.OwnerSlot == slot)
            {
                return rig;
            }

            _rigBySlot[slot] = null;

            // In a session A says where each drawn puppet is (EtsNet.TryGetVehicleFrame, PLAN §7.A: the rig's root once C's
            // build is done, a bare frame before): no scene search, so a truck that is never drawn here (past the 16-truck
            // cap, Q-A4, or another install's catalogue: a stand-in for good) costs nothing while the page is open.
            EtsNet net = EtsNet.Instance;

            if (net != null)
            {
                if (net.TryGetVehicleFrame(EtsTargetKind.Tractor, slot, 0, out Transform frame))
                {
                    EtsTruckRig drawn = frame.GetComponent<EtsTruckRig>();

                    if (drawn != null && drawn.IsPuppet && drawn.OwnerSlot == slot)
                    {
                        _rigBySlot[slot] = drawn;
                    }
                }

                return _rigBySlot[slot];
            }

            // No EtsNet (the editor's offline app tests, whose simulated puppets EtsNet does not know): the scene, at most
            // every 2 s while a listed truck is missing.
            if (!searchNow && Time.unscaledTime < _nextRigSearch)
            {
                return null;
            }

            _nextRigSearch = Time.unscaledTime + 2f;

            foreach (EtsTruckRig found in UnityEngine.Object.FindObjectsByType<EtsTruckRig>(FindObjectsSortMode.None))
            {
                if (found != null && found.IsPuppet && found.OwnerSlot >= 0 && found.OwnerSlot < _rigBySlot.Length)
                {
                    _rigBySlot[found.OwnerSlot] = found;
                }
            }

            return _rigBySlot[slot];
        }

        private bool IsMySlot(int slot)
        {
            EtsNet net = EtsNet.Instance;
            return net != null && net.MySlot >= 0 && net.MySlot == slot;
        }

        private string OwnerName(int slot)
        {
            if (slot < 0)
            {
                return "";
            }

            for (int i = 0; i < _count; i++)
            {
                if (_views[i] != null && _views[i].Slot == slot)
                {
                    return DisplayName(_views[i]);
                }
            }

            return "";
        }

        /// <summary>Which name sources exist yet (a row whose names were made from token letters is made again when they arrive).</summary>
        private static int NamesReady()
        {
            EtsMapIndex index = EtsMapIndex.Instance;
            return (EtsTruckCatalog.Current != null ? 1 : 0) + (EtsMapViewer.Current != null ? 2 : 0) + (index != null && index.Ready ? 4 : 0) + (EtsLocale.Loaded ? 8 : 0);
        }

        private static string DisplayName(EtsPlayerView view)
        {
            return !string.IsNullOrEmpty(view.Name) ? view.Name : "Driver " + (view.Slot + 1); // not in the locale
        }

        // ------------------------------------------------------------------ names (cached; made only when a row changes)

        private static string TruckName(EtsTruckIdentity id)
        {
            EtsTruckCatalog catalogue = EtsTruckCatalog.Current;

            if (catalogue != _namesFrom)
            {
                _namesFrom = catalogue;
                TruckNames.Clear();
            }

            if (catalogue == null || id.Preview < 0 || id.Preview >= catalogue.Trucks.Count || (catalogue.Hash & 0xFF) != (id.CatalogueHash & 0xFF))
            {
                return "A truck"; // not in the locale: another install's list, or the catalogue not read yet
            }

            if (TruckNames.TryGetValue(id.Preview, out string known))
            {
                return known;
            }

            EtsPreviewTruck truck = catalogue.Trucks[id.Preview];
            string series = truck.SpecValues != null && truck.SpecValues.Length > 0 ? truck.SpecValues[0] : null;
            string brand = !string.IsNullOrEmpty(truck.BrandName) ? truck.BrandName : truck.Brand;
            string name = !string.IsNullOrEmpty(brand) && !string.IsNullOrEmpty(series) ? brand + " " + series : !string.IsNullOrEmpty(truck.Stem) ? truck.Stem : "A truck";
            TruckNames[id.Preview] = name;
            return name;
        }

        private static string CityName(ulong token)
        {
            if (token == 0UL)
            {
                return "";
            }

            if (CityNames.TryGetValue(token, out string known))
            {
                return known;
            }

            string name = null;

            try
            {
                EtsMap map = EtsMapViewer.Current != null ? EtsMapViewer.Current.Map : null;
                EtsSiiUnit unit = map != null ? map.City(token) : null;
                name = unit != null ? unit.GetString("city_name", null) : null;
            }
            catch (Exception)
            {
                // The map not mounted (editor preview): the token's own letters below.
            }

            name = Pretty(name, EtsToken.Decode(token));

            if (EtsMapViewer.Current != null)
            {
                CityNames[token] = name; // cached only once the map could answer
            }

            return name;
        }

        private static string CargoName(ulong token)
        {
            if (token == 0UL)
            {
                return "";
            }

            if (CargoNames.TryGetValue(token, out string known))
            {
                return known;
            }

            string name = null;
            EtsMapIndex index = EtsMapIndex.Instance;
            EtsSiiFile cargo = index != null ? index.Cargo : null;

            if (cargo != null)
            {
                EtsSiiUnit unit = cargo.Find("cargo." + EtsToken.Decode(token));
                name = unit != null ? unit.GetString("name", null) : null;
            }

            name = Pretty(name, EtsToken.Decode(token));

            if (cargo != null)
            {
                CargoNames[token] = name;
            }

            return name;
        }

        /// <summary>
        /// A definition's name through the locale (EtsTabletApp.Words), else the way EtsJobs.Pretty shows it when the key is
        /// not in the locale (EtsJobs.cs "Pretty": "@@cn_apples@@" -> "Apples"); the bare token's letters when there is none.
        /// </summary>
        private static string Pretty(string text, string fallback)
        {
            string s = string.IsNullOrEmpty(text) ? fallback ?? "" : EtsTabletApp.Words(text);
            s = s.Replace("@@", "");

            if (s.StartsWith("cn_", StringComparison.Ordinal))
            {
                s = s.Substring(3);
            }

            s = s.Replace('_', ' ');
            return s.Length > 1 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;
        }

        // ------------------------------------------------------------------ the buttons

        private void RideAlong(int index)
        {
            Row row = _rows[index];
            EtsPlayerView view = row != null ? row.View : null;

            if (view == null)
            {
                return;
            }

            EtsTabletLayout.ConvoyAsk ask = Ask(view, out EtsTruckRig rig);

            if (ask.TheyHaveTruck && !ask.TruckDrawn)
            {
                RigOf(view.Slot, true); // one search now rather than a refusal while the 2 s search pause runs
                ask = Ask(view, out rig);
            }

            string name = DisplayName(view);
            EtsTabletLayout.ConvoyRefusal refusal = EtsTabletLayout.RideAlong(ask);

            if (refusal != EtsTabletLayout.ConvoyRefusal.None)
            {
                Say(EtsTabletLayout.Why(refusal, name, SeatedWith(), _askName));
                return;
            }

            if (!CanRequestSeat)
            {
                Say("Not connected to the room."); // not in the locale
                return;
            }

            int slot = view.Slot;
            int token = ++_askToken;
            _asking = true;
            _askSince = Time.unscaledTime;
            _askName = name;
            Say("Asking " + name + " for the passenger seat ..."); // not in the locale
            Debug.Log("[SomniumEuroTruck] Tablet: Convoys - Ride along: asking " + name + " (slot " + slot + ") for the passenger seat.");

            try
            {
                RequestSeat(slot, 1, () => Granted(token, slot, name), reason => Refused(token, name, reason));
            }
            catch (Exception e)
            {
                Refused(token, name, "The request failed (" + e.GetType().Name + ")."); // not in the locale
            }

            // As EtsTruckDoor.UseRig: EtsNet writes the requesting rider record itself, so EtsRiders holds its own record
            // until the answer ([net §2.7] step 1; the hold lapses by itself after 5 s). Held AFTER the request, and only
            // while it is still out: EtsRiders' hold is one flag, and EtsNet.RequestSeat answers a request it replaces
            // ("Another request took its place", a door click still out) inside this call - that answer's handler lifts
            // the hold (EtsTruckDoor's refused does), so a hold taken before the call was lifted under our own request, and
            // a player standing on a vehicle would publish over the requesting record (the owner never sees it: "did not
            // answer"). A request refused at once (not online, the seat taken) never holds, so it cannot lift anyone's hold
            // either (checker probe 2275). Same frame, before EtsRiders' next EarlyPass, so nothing is published between.
            EtsRiders riders = EtsRiders.Instance;

            if (_asking && token == _askToken && riders != null)
            {
                riders.HoldPublishing(true);
                _holding = true;
            }
        }

        private void Granted(int token, int slot, string name)
        {
            if (token != _askToken || !_asking)
            {
                Debug.Log("[SomniumEuroTruck] Tablet: Convoys - " + name + "'s grant came after the page gave up; not sitting down.");
                LeaveGrantedSeat();
                return;
            }

            EndAsk();
            EtsTruckRig rig = RigOf(slot, true);
            EtsMenu menu = App.Menu;

            if (rig == null || rig.Controller == null)
            {
                Say(name + "'s truck is gone."); // not in the locale
                LeaveGrantedSeat();
                return;
            }

            if (menu == null || menu.Seat == null || !menu.OnFoot)
            {
                Say(menu != null && menu.Seat != null && menu.Seat.Seated ? "You are already seated." : "There is no Somnium player to seat here (editor)."); // not in the locale
                LeaveGrantedSeat();
                return;
            }

            // The app off the screen first: the ride is to be seen, and in a headset the open app holds the hands.
            App.Close();
            menu.EnterTruck(rig, 1);

            if (menu.Seat.Seated)
            {
                Say(name + " said yes: you ride along."); // not in the locale
                App.Toast(name + " said yes: you ride along in the passenger seat."); // not in the locale
                Debug.Log("[SomniumEuroTruck] Tablet: Convoys - Ride along: seat 1 of " + name + "'s truck granted; seated.");
            }
            else
            {
                Say("The seat was granted, but sitting down failed."); // not in the locale
                Debug.Log("[SomniumEuroTruck] Tablet: Convoys - Ride along: granted, but EtsMenu.EnterTruck did not seat the player.");
                LeaveGrantedSeat();
            }
        }

        private void Refused(int token, string name, string reason)
        {
            if (token != _askToken || !_asking)
            {
                return;
            }

            EndAsk();
            string why = string.IsNullOrEmpty(reason) ? name + " said no." : reason; // not in the locale
            Say(why);
            App.Toast(why);
            Debug.Log("[SomniumEuroTruck] Tablet: Convoys - Ride along with " + name + " refused: " + why);
        }

        private void EndAsk()
        {
            _asking = false;

            if (!_holding)
            {
                return; // never held (refused inside RequestSeat): the hold, if any, is someone else's (a door click's)
            }

            _holding = false;
            EtsRiders riders = EtsRiders.Instance;

            if (riders != null)
            {
                riders.HoldPublishing(false);
            }
        }

        /// <summary>
        /// A grant the page does not use: out of the owner's seat table at once rather than after their 2 s check ([net §2.7]
        /// step 4). Never while the player sits somewhere (a door click may have seated them meanwhile; that seat is B's).
        /// </summary>
        private void LeaveGrantedSeat()
        {
            EtsNet net = EtsNet.Instance;
            EtsTruckSeat seat = App.Menu != null ? App.Menu.Seat : null;

            if (net != null && (seat == null || !seat.Seated))
            {
                net.LeaveSeat();
            }
        }

        private void GoTo(int index)
        {
            Row row = _rows[index];
            EtsPlayerView view = row != null ? row.View : null;

            if (view == null)
            {
                return;
            }

            EtsTabletLayout.ConvoyAsk ask = Ask(view, out EtsTruckRig rig);

            if (ask.TheyHaveTruck && !ask.TruckDrawn)
            {
                RigOf(view.Slot, true);
                ask = Ask(view, out rig);
            }

            string name = DisplayName(view);
            EtsTabletLayout.ConvoyRefusal refusal = EtsTabletLayout.GoTo(ask);

            if (refusal != EtsTabletLayout.ConvoyRefusal.None)
            {
                Say(EtsTabletLayout.Why(refusal, name, SeatedWith(), _askName));
                return;
            }

            // Beside the passenger door, as EtsTruckSeat.StandBeside stands a passenger who gets out: 1.6 m past the
            // passenger seat's x (C's EtsTruckController.SeatLocalFor(1)), at the truck's own height, facing the cab.
            Transform frame = rig.transform;
            Vector3 seatLocal = rig.Controller.SeatLocalFor(1);
            float side = seatLocal.x < 0f ? -1f : 1f;
            Vector3 door = frame.TransformPoint(new Vector3(seatLocal.x + side * EtsTabletLayout.GoToSideMetres, 0f, seatLocal.z));
            float yaw = frame.eulerAngles.y - side * 90f;
            App.Close();

            if (!App.GoTo(door, yaw, name + "'s truck"))
            {
                Say("There is no Somnium player to move here (editor)."); // not in the locale
            }
        }

        private string SeatedWith()
        {
            EtsTruckSeat seat = App.Menu != null ? App.Menu.Seat : null;
            EtsTruckRig rig = seat != null && seat.Seated ? seat.Rig : null;
            return rig != null && rig.IsPuppet ? EtsNet.NameOf(rig.OwnerPlayerId) : "";
        }

        private void Say(string text)
        {
            _said = text ?? "";
            _saidUntil = Time.unscaledTime + 12f;

            if (_result != null)
            {
                _result.text = _said;
            }

            EtsGameUi.Note("convoys: " + _said);
        }
    }

    /// <summary>
    /// System's last link, mbox.quit (the game's "Quit", icon quit_game: probe 1902), as the game shows it: its message box
    /// with the game's own words (wanna_quit "Your progress will be saved automatically. Do you really want to quit?",
    /// yes / no: /locale/en_gb/localization.sui). "Yes" leaves the world the Somnium way: the app closes and the tablet's
    /// own Exit World is pressed, which asks once more in the client's pop-up (EtsTabletLink.ExitWorld). The career is
    /// already saved after every delivery (EtsProfileStore), so there is nothing to save here. With no Somnium tablet
    /// (the world's own panel) there is no Exit World to press: "Yes" is greyed and the line says where leaving is.
    /// </summary>
    public sealed class EtsPageLeave : EtsTabletPage
    {
        public override string Caption => EtsTabletApp.Word("quit_game");
        public override string Icon => "/material/ui/link/quit_game.mat";
        public override string Id => "Leave";
        public override string Category => "quit";
        public override string ScreenLink => "mbox.quit";

        private TextMeshProUGUI _where;
        private Button _yes;
        private int _shownKey = -1;

        protected override void Build()
        {
            // A message box in the middle of the frame: 480 x 300 at 480..960 x 300..600.
            const float l = 480f, r = 960f, b = 300f, t = 600f;
            EtsTabletApp.Window(Root, Caption, l, r, b, t);
            TextMeshProUGUI question = EtsTabletApp.Text(EtsGameUi.Place(Root, "question", l + 30f, r - 30f, t - 190f, t - 60f), EtsTabletApp.Word("wanna_quit"), EtsFont.Normal,
                TextAlignmentOptions.Center, EtsTabletApp.Colour("clr_txt", EtsTabletApp.NormalText));
            question.textWrappingMode = TextWrappingModes.Normal;
            question.richText = true; // the game's <br>
            _where = EtsTabletApp.Text(EtsGameUi.Place(Root, "where", l + 30f, r - 30f, b + 80f, b + 110f), "", EtsFont.Small, TextAlignmentOptions.Center,
                EtsTabletApp.Colour("clr_txt_d", EtsTabletApp.DimText));
            _yes = App.Button(Root, EtsTabletApp.Word("yes"), l + 40f, l + 220f, b + 20f, b + 70f, () =>
            {
                EtsTabletLink link = App.Link;

                if (link == null || !link.ExitWorld())
                {
                    App.Toast("Leave with Somnium's own Exit World."); // not in the locale
                }
            });
            App.Button(Root, EtsTabletApp.Word("no"), r - 220f, r - 40f, b + 20f, b + 70f, () => App.Show(EtsTabletApp.HomeId));
        }

        public override void Tick()
        {
            bool can = App.Link != null && App.Link.CanExitWorld;
            int key = can ? 1 : 0;

            if (_where != null && key != _shownKey)
            {
                _shownKey = key;
                _where.text = can ? "Somnium asks once more before you go." : "No Somnium tablet here: leave with Somnium's own Exit World."; // not in the locale
            }

            EtsTabletApp.SetUsable(_yes, can);
        }
    }

    /// <summary>
    /// System's first link, options.selector ("Options", icon "options": probe 1901): the controls as they stand, the
    /// tablet's own options, and a word on saving. The game's other System links (backgrounds, save, load, title
    /// screen, quit) are greyed: the career saves itself, and leaving is the client's.
    /// </summary>
    public sealed class EtsPageSystem : EtsTabletPage
    {
        public override string Caption => EtsTabletApp.Word("options");
        public override string Icon => "/material/ui/link/options.mat";
        public override string Id => "Options";
        public override string Category => "quit";
        public override string ScreenLink => "options.selector";

        private TextMeshProUGUI _help, _tablet, _vibrationText;
        private int _shownKey = -1;
        private Button _large, _carry, _vibration, _time, _weather;
        private TextMeshProUGUI _dayText;
        private int _shownDay = -1;
        private string _shownVibration;

        protected override void Build()
        {
            const float l = 220f, r = 1220f, b = 110f, t = 820f;
            EtsTabletApp.Window(Root, Caption, l, r, b, t);
            _help = EtsTabletApp.Text(EtsGameUi.Place(Root, "controls", l + 30f, r - 30f, t - 230f, t - 70f), "", EtsFont.Normal, TextAlignmentOptions.TopLeft,
                EtsTabletApp.Colour("clr_txt", EtsTabletApp.NormalText));
            _help.textWrappingMode = TextWrappingModes.Normal;
            _help.enableAutoSizing = false;
            _tablet = EtsTabletApp.Text(EtsGameUi.Place(Root, "tablet", l + 30f, r - 30f, t - 300f, t - 250f), "", EtsFont.Normal, TextAlignmentOptions.Left,
                EtsTabletApp.Colour("clr_txt_d", EtsTabletApp.DimText));
            float y = t - 370f;
            _large = App.Button(Root, "Tablet: large for the app, on / off", l + 150f, r - 150f, y, y + 40f, () => // not in the locale
            {
                if (App.Link != null) App.Link.ForceLarge = !App.Link.ForceLarge;
            });
            y -= 60f;
            _carry = App.Button(Root, "Tablet: carried with the truck, on / off", l + 150f, r - 150f, y, y + 40f, () => // not in the locale
            {
                if (App.Link != null) App.Link.CarryWithVehicle = !App.Link.CarryWithVehicle;
            });

            // (The "Seat: tilt with the cab" option was removed on 2026-09-25 with the cab tilt itself - EtsTruckSeat.)
            // The wheel's vibration strength in a headset (EtsWheelHaptics: normal -> strong -> off -> light), for this visit only.
            y -= 60f;
            _vibration = App.Button(Root, "Wheel: vibration strength", l + 150f, r - 150f, y, y + 40f, () => EtsWheelHaptics.CycleStrength()); // not in the locale
            _vibrationText = EtsTabletApp.Text(EtsGameUi.Place(Root, "vibration", l + 30f, r - 30f, y - 45f, y - 5f), "", EtsFont.Normal, TextAlignmentOptions.Center,
                EtsTabletApp.Colour("clr_txt_d", EtsTabletApp.DimText));

            // 2026-10-14: the running day and the weather, for this player only (EtsDayNight): the time can stay at 10:00 for
            // comfort, and the weather can be held clear or rainy; everyone else keeps the shared clock and weather.
            y -= 105f;
            float mid = (l + r) * 0.5f;
            _time = App.Button(Root, "Time: game clock / fixed 10:00", l + 60f, mid - 10f, y, y + 40f, () => EtsDayNight.FixedTime = !EtsDayNight.FixedTime); // not in the locale
            _weather = App.Button(Root, "Weather: game's / clear / rain", mid + 10f, r - 60f, y, y + 40f, () => EtsDayNight.WeatherChoice = (EtsDayNight.WeatherChoice + 1) % 3); // not in the locale
            _dayText = EtsTabletApp.Text(EtsGameUi.Place(Root, "day", l + 30f, r - 30f, y - 42f, y - 4f), "", EtsFont.Normal, TextAlignmentOptions.Center,
                EtsTabletApp.Colour("clr_txt_d", EtsTabletApp.DimText));
            EtsTabletApp.Text(EtsGameUi.Place(Root, "save", l + 30f, r - 30f, b + 30f, b + 70f), "Your career is saved by itself after every delivery.", EtsFont.Normal, TextAlignmentOptions.Center, // not in the locale
                EtsTabletApp.Colour("clr_txt_d", EtsTabletApp.DimText));
        }


        private static string TimeLabelFor(int minutes)
        {
            return (minutes / 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ":" + (minutes % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }
        public override void Tick()
        {
            string help = EtsVr.Present ? EtsVr.Help : EtsTabletApp.DesktopHelp();

            if (_help != null && _help.text != help)
            {
                _help.text = help;
            }

            bool linked = App.Link != null && App.Link.IsLinked;
            int key = !linked ? 0 : 1 + (App.Link.ForceLarge ? 2 : 0) + (App.Link.CarryWithVehicle ? 4 : 0);

            if (_tablet != null && key != _shownKey)
            {
                _shownKey = key;
                _tablet.text = !linked ? "No Somnium tablet here: this is the world's own panel." : // not in the locale
                               "Large for the app: " + (App.Link.ForceLarge ? "on" : "off") + "     Carried with the truck: " + (App.Link.CarryWithVehicle ? "on" : "off");
            }

            EtsTabletApp.SetUsable(_large, linked);
            EtsTabletApp.SetUsable(_carry, linked && !App.Link.IsDesktopTablet);

            // StrengthLabel returns constants, so the text is only set (and allocates) when it changes.
            string vibration = EtsWheelHaptics.StrengthLabel;

            if (_vibrationText != null && !ReferenceEquals(vibration, _shownVibration))
            {
                _shownVibration = vibration;
                _vibrationText.text = vibration + " - the engine and the road through the rim (headset)"; // not in the locale
            }

            EtsTabletApp.SetUsable(_vibration, EtsVr.Present);

            // The day's line, written again only when something on it changed (every ten game minutes at most).
            int dayKey = (EtsDayNight.FixedTime ? 1 : 0) + EtsDayNight.WeatherChoice * 2 + (EtsDayNight.Rain > 0.05f ? 8 : 0) + (int)(EtsDayNight.GameMinutes / 10.0) % 144 * 16;

            if (_dayText != null && dayKey != _shownDay)
            {
                _shownDay = dayKey;
                int m = ((int)EtsDayNight.GameMinutes % 1440 + 1440) % 1440;
                _dayText.text = "Time " + TimeLabelFor(m) + " - " + EtsDayNight.TimeLabel + ".  Weather: " + EtsDayNight.WeatherLabel + (EtsDayNight.Rain > 0.05f ? " (raining)" : "") + "."; // not in the locale
            }
        }
    }
}
