using System;
using System.Collections.Generic;
using System.Reflection;
using SomniumSpace.Bridge;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The Somnium client's own tablet, borrowed for the "ETS 2" app (owner E; [tablet §3, §4.2], PLAN.md §7.E).
    ///
    /// There is no tablet API for worlds: the Bridge XML has no tablet member and its DLL no "tablet" string
    /// [tablet §2.1]. The client instantiates Tablet.XR / Tablet.NonXR into "MasterScene" (TabletManager.cs:69-79,
    /// WorldsHelper.cs:16,34) and parents it under the local player when "tablet follows player" is on (the default,
    /// SettingsValueValidation.asset:58-64). So it is found by searching, by the client's component TYPE names first
    /// (TabletControl, TabsContainer, HomeTab, TabletStatusBarView) with object names as the fallback; the client
    /// is Mono, so nothing renames them [persist §2.3].
    ///
    /// Two modes:
    ///   LogOnly (Phase 0's probe, PLAN §4 U4; what EtsNet.Awake adds today): searches, writes ONE line and touches
    ///     nothing - no button, no canvas, no clone, no Resize press, no layer change:
    ///       [SomniumEuroTruck] Tablet (log only): linked - 'Tablet.XR(Clone)' under ... (how each part was found)
    ///       [SomniumEuroTruck] Tablet (log only): MISSING ... (what was searched, for how long)
    ///   Linked (EtsTabletApp.Ensure clears LogOnly): the app's button and canvas on the tablet. This also needs
    ///     EtsFeatures.NewFlow: with the flag off a non-log-only link does nothing at all, so no build with the
    ///     flag off can ever put anything on the client's tablet, whatever component set-up it has.
    ///
    /// Phase 0's client run (Player.log 2026-09-23 01:55, VR) found every part this needs, by type: "'Tablet.XR(Clone)'
    /// under 'XR.Body' in scene 'MasterScene', XR, root by type TabletControl; TabsContainer by type, HomeTab by type,
    /// TabletStatusBarView by type; 14 tabs (14 with a ContentView), home grid 19 buttons, home 960x1080 (Small), UI layer
    /// 5, canvas layer 9, raycaster TrackedDeviceGraphicRaycaster ... all parts found". So with the new flow on, the same
    /// search links and injects. Once linked, the button is kept first in the grid and its measured cell is logged once
    /// against the grid's Mask (KeepButtonFirst), and the System page's Quit can press the tablet's own Exit World
    /// (ExitWorld), which asks the player in the client's pop-up.
    ///
    /// Where Blades Edge's SomniumTabletManager fails, measured against the client source [tablet §3]:
    ///  - never DontDestroyOnLoad: the tablet lives in MasterScene and outlives the world, so OnDestroy takes the
    ///    button and the canvas off it and restores everything hidden or resized; objects an earlier visit left
    ///    (named ETS.Tablet.*) are swept at every link, and a Tabs container an earlier visit left hidden is shown;
    ///  - the home button is ONE client button cloned under an INACTIVE holder and stripped to Unity UI and TMP
    ///    before it is ever active, so no client script (SwitchTabButton, LocalizeStringEvent ...) wakes on the
    ///    copy; its persistent onClick is dropped with a new ButtonClickedEvent; it goes FIRST in the home grid
    ///    (16 visible cells, 14 used in XR: probe 1603), never where the grid's Mask can hide it;
    ///  - nothing of the client's Tabs container is cloned: the app's canvas is created, a sibling of the tabs
    ///    under "Canvases" (so it fades and stops taking the ray with the tablet, Armature_CloseFrame.anim), on
    ///    the HomeTab canvas's layer, with its sorting, camera and raycasters mirrored;
    ///  - the size is measured: Resize is pressed only when the HomeTab measures Small (960 wide), and pressed
    ///    back on close only if we enlarged it and it is still Large; the canvas follows the HomeTab rect every
    ///    LateUpdate, so the player's own Resize is followed too;
    ///  - a client tab switch (any tab's ContentView changing) closes the app, so the tablet's mesh Map, Chat ...
    ///    buttons are never swallowed by the hidden Tabs container.
    ///
    /// Placement (the client's tablet, not ours to own, is only nudged):
    ///  - Desktop (PLAN C22): TabletControl snaps the open tablet to its point every Update (TabletControl.cs:263-291)
    ///    and EtsTruckCamera moves the camera - the point's parent - in LateUpdate, so the tablet trails by a frame's
    ///    travel, 0.42 m at 90 km/h and 60 fps. It is put back on Camera.main/TabletPoint in
    ///    RenderPipelineManager.beginCameraRendering, which runs after every LateUpdate whatever the execution order
    ///    (U1), and only when it is that frame's travel away (EtsTabletLayout.DesktopSnap).
    ///  - VR, "follow player" off and not held: the riding platform's frame delta is applied to it in LateUpdate
    ///    (RidingOn, PLAN C21 = EtsRiders.LocalPlatformFrame); a delta is right whatever ran before.
    ///
    /// Cost: discovery is a depth-first walk of MasterScene and the other scenes, skipping the world's own "Ets ..."
    /// subtrees (the streamed map alone is tens of thousands of objects) and remote tablets ("Simulator"), every
    /// 0.5 s for a minute and every 5 s after that, only while unlinked. Linked, a frame costs an Animator bool and a
    /// rect copy; four times a second the camera, the tab snapshot and the app's layers are checked. No steady-state
    /// allocation (the body root is re-read at 4 Hz, not per frame).
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class EtsTabletLink : MonoBehaviour
    {
        /// <summary>The prefix of everything this puts on the client's tablet; swept on every link.</summary>
        public const string Owned = "ETS.Tablet.";

        public static EtsTabletLink Instance { get; private set; }

        /// <summary>
        /// Whether the world's Escape, R, VR left X and left Y are retired in favour of the tablet ([tablet §4.9]):
        /// only with the new flow on AND the app linked into the client's tablet. False in every build today.
        /// </summary>
        public static bool KeysRetired => EtsTabletLayout.KeysRetired(EtsFeatures.NewFlow, Instance != null && Instance.IsLinked);

        [Tooltip("Phase 0 probe mode: discovery and one report line only; nothing is added to the client's tablet.")]
        public bool LogOnly;

        [Tooltip("Seconds between searches for the tablet while it has not been found (5 s after the first minute).")]
        public float SearchInterval = 0.5f;

        [Tooltip("LogOnly: how long to keep searching before reporting what is missing.")]
        public float LogOnlyFor = 60f;

        [Tooltip("Caption under the app's icon on the tablet's home screen.")]
        public string AppTitle = "ETS 2";

        [Tooltip("Press the tablet's Resize button when the app opens on a Small tablet, and again when it closes.")]
        public bool ForceLarge = true;

        [Tooltip("Desktop: put the open tablet back on the camera's TabletPoint after the truck camera has moved it.")]
        public bool FixDesktopLag = true;

        [Tooltip("VR, tablet not following the player: carry it along with the vehicle the player rides in.")]
        public bool CarryWithVehicle = true;

        /// <summary>The home-screen icon; painted from the game's logo when the archives are mounted, if left null.</summary>
        public Texture2D AppIcon;

        /// <summary>The platform the local player rides (the truck), or null: = () => EtsRiders.Instance?.LocalPlatformFrame (PLAN C21).</summary>
        public Func<Transform> RidingOn;

        public event Action<bool> AppShown;
        public event Action<bool> TabletOpenChanged;
        public event Action LinkChanged;

        public bool IsLinked => !LogOnly && _t != null && _t.Root != null;
        public bool IsDesktopTablet => _t != null && _t.Desktop;
        public bool TabletOpen { get; private set; }
        public bool AppOpen { get; private set; }

        /// <summary>The 1440 x 900 rectangle the app lays itself out in, inside the tablet's screen; null unless linked.</summary>
        public RectTransform Frame => IsLinked ? _frame : null;

        /// <summary>
        /// The layer the client's own tablet graphics carry (5, UI: Player.log 2026-09-23 01:55 "UI layer 5"), which the XR
        /// ray tests per graphic (TrackedDeviceGraphicRaycaster.ShouldTestGraphic): what the app puts on what it draws
        /// into Frame at once, rather than leaving it on the default layer until the next 4 Hz pass here (checker probe
        /// 2081). EtsLayers.Ui while not linked.
        /// </summary>
        public int UiLayer => IsLinked ? _t.UiLayer : EtsLayers.Ui;

        /// <summary>The last discovery result, as logged.</summary>
        public string Report { get; private set; } = "not searched yet";

        private sealed class Parts
        {
            public Transform Root;
            public bool Desktop;
            public Animator Animator;
            public bool AnimatorHasIsOpen;
            public GameObject GrabCollider;
            public XRGrabInteractable Grab;
            public Transform Canvases;
            public CanvasGroup CanvasesGroup;
            public Transform Tabs;
            public Canvas HomeCanvas;
            public RectTransform Home;
            public GridLayoutGroup HomeGrid;
            public Button ReferenceButton;
            public RectTransform StatusBar;
            public Button Back;
            public Button Resize;
            public Button ExitWorld;
            public int GridShown;
            public int UiLayer = 5;
            public readonly List<GameObject> TabContents = new List<GameObject>();
            public readonly List<UnityEvent> MeshHome = new List<UnityEvent>();
        }

        private static readonly int IsOpenHash = Animator.StringToHash("IsOpen");
        private static readonly List<MonoBehaviour> Buffer = new List<MonoBehaviour>();
        private static readonly Vector3[] Corners = new Vector3[4];

        private Parts _t;
        private GameObject _button;
        private Image _buttonIcon;
        private GameObject _app;
        private Canvas _appCanvas;
        private RectTransform _frame;
        private float _nextSlow;
        private bool[] _tabSnapshot;
        private bool _backWasActive;
        private bool _enlarged;
        private float _resizeSettleUntil;
        private Transform _desktopPoint;
        private Camera _desktopPointCamera;
        private bool _desktopPointMissingReported;
        private int _placedFrame = -1;
        private int _desktopSnaps;
        private int _desktopSkips;
        private bool _shakeFixReported;
        private Transform _lastPlatform;
        private Vector3 _platformPos;
        private Quaternion _platformRot;
        private Transform _bodyRoot;
        private float _nextBodyRead;
        private EtsFileSystem _iconTriedFor;

        // LogOnly (Phase 0) bookkeeping, unchanged.
        private float _startedAt = -1f;
        private float _nextSearch;
        private int _searches;
        private double _slowestMs;
        private bool _reported;

        // Linked-mode search bookkeeping.
        private float _firstSearchAt = -1f;
        private bool _notLinkedReported;
        private bool _renderHooked;
        private bool _ending;

        // Phase 2: the button's place in the home grid, checked in the client once it is laid out.
        private float _linkedAt;
        private bool _placeReported;
        private bool _reorderReported;

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            // Statics survive a world re-entry in the same client session (the assembly is loaded once:
            // WorldScriptLoader.ValidateAssembly, [tablet §2.8]), so the previous visit's instance is simply
            // replaced, never trusted.
            Instance = this;

            // The contract's default (PLAN C21), one delegate for the component's life (no per-frame allocation).
            if (RidingOn == null)
            {
                RidingOn = DefaultRidingOn;
            }
        }

        private void Start()
        {
            // Start, not Awake: EtsNet sets LogOnly right after AddComponent, and Awake has run by then.
            _startedAt = Time.unscaledTime;
        }

        private void OnEnable()
        {
            if (IsLinked)
            {
                HookRendering(true);
            }
        }

        private void OnDisable()
        {
            HookRendering(false);
        }

        /// <summary>The render callback exists only while linked: the log-only probe and an unlinked app cost nothing per camera.</summary>
        private void HookRendering(bool on)
        {
            if (on == _renderHooked)
            {
                return;
            }

            _renderHooked = on;

            if (on)
            {
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            }
            else
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            }
        }

        private void OnDestroy()
        {
            // The world is unloading (or the component is removed): leave the client's tablet as it was. No events
            // from here: a listener rebuilding its UI now would create objects in whatever scene is active next.
            _ending = true;
            HookRendering(false);
            Unlink(true, "world unloading");

            if (Instance == this)
            {
                Instance = null;
            }
        }

        private static Transform DefaultRidingOn()
        {
            EtsRiders riders = EtsRiders.Instance;
            return riders != null ? riders.LocalPlatformFrame : null;
        }

        private void Update()
        {
            if (LogOnly)
            {
                ProbeUpdate();
                return;
            }

            // Belt and braces for Phase 1: a real link only ever exists with the new flow on (PLAN §8 Phase 1 rule).
            if (!EtsFeatures.NewFlow)
            {
                return;
            }

            if (!IsLinked)
            {
                if (_t != null)
                {
                    Unlink(false, "the tablet was destroyed (avatar or mode change)"); // TabletFeature.Clean -> TabletManager.Destroy
                }

                if (Time.unscaledTime >= _nextSearch)
                {
                    if (_firstSearchAt < 0f)
                    {
                        _firstSearchAt = Time.unscaledTime;
                    }

                    // Every SearchInterval for the first minute (the tablet comes with the local player), then every 5 s.
                    float interval = Time.unscaledTime - _firstSearchAt < 60f ? Mathf.Max(0.1f, SearchInterval) : 5f;
                    _nextSearch = Time.unscaledTime + interval;
                    TryLink();
                }

                return;
            }

            bool open = ReadTabletOpen();

            if (open != TabletOpen)
            {
                TabletOpen = open;
                EtsGameUi.Note(open ? "tablet opened" : "tablet closed");
                TabletOpenChanged?.Invoke(open);
            }

            if (Time.unscaledTime < _nextSlow)
            {
                return;
            }

            _nextSlow = Time.unscaledTime + 0.25f;

            if (_appCanvas != null)
            {
                Camera eventCamera = _t.HomeCanvas != null ? _t.HomeCanvas.worldCamera : null;
                Camera wanted = eventCamera != null ? eventCamera : Camera.main;

                if (_appCanvas.worldCamera != wanted)
                {
                    _appCanvas.worldCamera = wanted;
                }
            }

            if (_buttonIcon != null && _buttonIcon.sprite == null)
            {
                SetIcon(); // the archives mounted after the link
            }

            KeepButtonFirst();

            if (AppOpen)
            {
                if (TabsChangedSinceOpen())
                {
                    Close(false, "client switched tab"); // mesh Map/Chat/..., a deep link, the exit dialog
                    return;
                }

                SetLayer(_app.transform, _t.UiLayer); // rows added after the build need the ray's layer too
            }
        }

        private void LateUpdate()
        {
            if (!IsLinked)
            {
                return;
            }

            FollowHomeRect();

            if (!TabletOpen)
            {
                _lastPlatform = null;
                return;
            }

            if (!_t.Desktop && CarryWithVehicle)
            {
                Carry();
            }
            else if (_t.Desktop)
            {
                // 2026-10-05 tablet shake: placed here too, not only at render time. World-space canvases are batched
                // in PostLateUpdate (PlayerUpdateCanvases), before beginCameraRendering: a tablet moved only at render
                // drew its body on the camera's point but its screen where the camera was a frame before, so the
                // screen shook while driving. Order 1000 runs after EtsTruckCamera (0) has placed the camera.
                SnapDesktop(false);
            }
        }

        // ------------------------------------------------------------------ Phase 0: log only (U4)

        private void ProbeUpdate()
        {
            if (_reported || Time.unscaledTime < _nextSearch)
            {
                return;
            }

            _nextSearch = Time.unscaledTime + Mathf.Max(0.1f, SearchInterval);
            float elapsed = Time.unscaledTime - _startedAt;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool complete;
            string report;

            try
            {
                Discover(out report, out complete);
            }
            catch (Exception e)
            {
                // A search that throws (a client object half built, say) counts as one that found nothing: it is
                // retried silently, and the one report line names the fault if no later search succeeds.
                complete = false;
                report = "the last search threw " + e.GetType().Name + ": " + e.Message;
            }

            watch.Stop();
            _searches++;
            _slowestMs = Math.Max(_slowestMs, watch.Elapsed.TotalMilliseconds);
            Report = report;

            if (!complete && elapsed < LogOnlyFor)
            {
                return;
            }

            _reported = true;
            string searched = "; " + _searches + " searches in " + elapsed.ToString("0.0") + " s, slowest " + _slowestMs.ToString("0.0") + " ms";
            Debug.Log("[SomniumEuroTruck] Tablet (log only): " + (complete ? "linked - " : "MISSING ") + report + searched + ".");

            // Nothing more to do in this mode: stop the per-frame check. The component stays for Instance.
            enabled = false;
        }

        // ------------------------------------------------------------------ discovery

        private void TryLink()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Parts found;
            bool complete;
            string report;

            try
            {
                found = Discover(out report, out complete);
            }
            catch (Exception e)
            {
                found = null;
                complete = false;
                report = "the last search threw " + e.GetType().Name + ": " + e.Message;
            }

            watch.Stop();
            _searches++;
            _slowestMs = Math.Max(_slowestMs, watch.Elapsed.TotalMilliseconds);
            Report = report;

            if (!complete)
            {
                // One line, once, so a log shows why the app fell back to its own panel (EtsTabletApp.FallbackAfter 20 s).
                if (!_notLinkedReported && Time.unscaledTime - _firstSearchAt > 20f)
                {
                    _notLinkedReported = true;
                    // The slowest search is in the line: with no tablet the search goes on every 5 s for the whole visit,
                    // and its cost in the client (a walk of MasterScene and the other scenes) is not measured offline.
                    Debug.Log("[SomniumEuroTruck] Tablet: not linked after " + (Time.unscaledTime - _firstSearchAt).ToString("0") + " s - " + report +
                              "; still searching every " + (Time.unscaledTime - _firstSearchAt < 60f ? SearchInterval.ToString("0.0") : "5") + " s; " +
                              _searches + " searches, slowest " + _slowestMs.ToString("0.0") + " ms.");
                }

                return;
            }

            _t = found;
            RemoveLeftovers(_t);

            try
            {
                Inject();
            }
            catch (Exception e)
            {
                // Half an injection is worse than none: take off whatever went on, and look again later.
                Debug.LogWarning("[SomniumEuroTruck] Tablet: injection failed (" + e.GetType().Name + ": " + e.Message + ") - " + report);
                Unlink(true, "injection failed");
                _nextSearch = Time.unscaledTime + 5f;
                return;
            }

            Debug.Log("[SomniumEuroTruck] Tablet: linked - " + report + "; " + _searches + " searches, slowest " + _slowestMs.ToString("0.0") + " ms.");
            _linkedAt = Time.unscaledTime;
            _placeReported = false;
            HookRendering(isActiveAndEnabled);
            LinkChanged?.Invoke();
        }

        /// <summary>
        /// Four times a second while linked: the "ETS 2" button stays the grid's first child (a client that re-sorts its
        /// buttons would otherwise push it past the Mask's 16 cells, probe 1603), and once - at least 0.5 s after the
        /// link, with the home grid active, so the grid has laid it out (GridLayoutGroup places children at the canvas
        /// update of the frame they change) - its measured cell is logged against the grid's own rect, which is the Mask's:
        ///   Tablet: the ETS 2 button is cell 1 of 15 shown in the home grid (19 client buttons + ETS 2), at 10..245 x 0..250 of the
        ///   960 x 1000 view (expected 10..245 x 0..250), inside the mask: yes.
        /// That line is the in-client proof that the button is visible (the task: "prove ours is visible").
        /// </summary>
        private void KeepButtonFirst()
        {
            if (_button == null || _t.HomeGrid == null)
            {
                return;
            }

            Transform button = _button.transform;

            if (button.parent == _t.HomeGrid.transform && button.GetSiblingIndex() != 0)
            {
                button.SetSiblingIndex(0);

                if (!_reorderReported)
                {
                    _reorderReported = true;
                    Debug.Log("[SomniumEuroTruck] Tablet: the client re-ordered its home grid; the ETS 2 button is first again.");
                }
            }

            if (_placeReported || Time.unscaledTime - _linkedAt < 0.5f || !_t.HomeGrid.gameObject.activeInHierarchy)
            {
                return;
            }

            _placeReported = true;
            var grid = (RectTransform)_t.HomeGrid.transform;
            int shown = 0, index = -1, buttons = 0;

            for (int i = 0; i < grid.childCount; i++)
            {
                Transform child = grid.GetChild(i);
                buttons += child != button && child.GetComponent<Button>() != null ? 1 : 0; // the client's own, as discovery counts them

                if (!child.gameObject.activeSelf)
                {
                    continue; // not laid out (LayoutGroup.rectChildren skips inactive children)
                }

                if (child == button)
                {
                    index = shown;
                }

                shown++;
            }

            Rect view = grid.rect;
            ((RectTransform)button).GetWorldCorners(Corners);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;

            for (int i = 0; i < 4; i++)
            {
                Vector3 p = grid.InverseTransformPoint(Corners[i]);
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y);
                maxY = Mathf.Max(maxY, p.y);
            }

            bool inside = minX >= view.xMin - 0.5f && maxX <= view.xMax + 0.5f && minY >= view.yMin - 0.5f && maxY <= view.yMax + 0.5f;
            bool masked = grid.GetComponent<Mask>() != null || grid.GetComponent<RectMask2D>() != null;
            EtsTabletLayout.HomeGridCell(view.width, view.height, shown, Mathf.Max(0, index), out float expectLeft, out float expectTop);
            Debug.Log("[SomniumEuroTruck] Tablet: the ETS 2 button is cell " + (index + 1) + " of " + shown + " shown in the home grid (" + buttons + " client buttons + ETS 2), at " +
                      (minX - view.xMin).ToString("0") + ".." + (maxX - view.xMin).ToString("0") + " x " + (view.yMax - maxY).ToString("0") + ".." + (view.yMax - minY).ToString("0") +
                      " of the " + view.width.ToString("0") + " x " + view.height.ToString("0") + " view (expected " + expectLeft.ToString("0") + ".." + (expectLeft + EtsTabletLayout.GridCellWidth).ToString("0") +
                      " x " + expectTop.ToString("0") + ".." + (expectTop + EtsTabletLayout.GridCellHeight).ToString("0") + "), " + (masked ? "inside the mask: " : "no mask on the grid, inside its rect: ") + (inside ? "yes." : "NO."));
        }

        /// <summary>Whether the tablet's own "Exit World" button (its status bar) was found, for the System page's Quit.</summary>
        public bool CanExitWorld => IsLinked && _t.ExitWorld != null;

        /// <summary>
        /// The System page's "Quit game" (the game's mbox.quit): the app closes (the tablet back to Small and its Home, so
        /// the client's pop-up is not behind the app's canvas) and the client's own Exit World button is pressed. That
        /// asks the player in the client's pop-up before anything happens (TabletQuickActionsControl.DoActionInternal:
        /// QuickAction.ExitWorld -> PopUpType.ExitWorld -> WorldTransitionControl.StartExitFromWorld on yes), exactly as
        /// pressing it by hand does. False when there is no such button (no tablet, or a client without it).
        /// </summary>
        public bool ExitWorld()
        {
            if (!CanExitWorld)
            {
                return false;
            }

            Close(true, "the player chose to leave");
            Debug.Log("[SomniumEuroTruck] Tablet: Exit World pressed for the player - the client asks them to confirm.");
            _t.ExitWorld.onClick.Invoke();
            return true;
        }

        /// <summary>
        /// One search. Returns the parts when a tablet root was found (complete or not) and null otherwise;
        /// <paramref name="complete"/> is true when the parts the app needs - Tabs, the HomeTab canvas, its grid and a
        /// reference button ([tablet §4.2] step 4) - are all there. The report names how each part was found; its
        /// wording is Phase 0's U4 line, unchanged.
        /// </summary>
        private static Parts Discover(out string report, out bool complete)
        {
            complete = false;
            Scene master = SceneManager.GetSceneByName("MasterScene"); // WorldsHelper.MASTER_SCENE_NAME
            bool masterLoaded = master.IsValid() && master.isLoaded;
            var roots = new List<GameObject>();
            Transform root = null;
            bool rootByType = false;

            if (masterLoaded)
            {
                master.GetRootGameObjects(roots);
                root = SearchRoots(roots, out rootByType);
            }

            int others = 0;

            for (int i = 0; root == null && i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);

                if (!scene.isLoaded || scene == master)
                {
                    continue;
                }

                others++;
                scene.GetRootGameObjects(roots);
                root = SearchRoots(roots, out rootByType);
            }

            if (root == null)
            {
                report = "no transform with a TabletControl component, and none named 'Tablet.XR(...' or 'Tablet.NonXR(...', in MasterScene (" +
                         (masterLoaded ? "loaded" : "NOT loaded") + ") or " + others + " other loaded scene(s); 'Simulator' objects (remote tablets) and the world's 'Ets ...' subtrees skipped";
                return null;
            }

            var t = new Parts { Root = root };

            Transform tabs = FindTyped(root, "TabsContainer");
            string tabsHow = tabs != null ? "type" : null;

            if (tabs == null)
            {
                tabs = FindNamed(root, "Tablet.XR.Tabs", 3);
                tabsHow = tabs != null ? "name" : "-";
            }

            Transform home = FindTyped(root, "HomeTab");
            string homeHow = home != null ? "type" : null;

            if (home == null)
            {
                home = FindNamed(root, "Tablet.XR.HomeTab", 4);
                homeHow = home != null ? "name" : "-";
            }

            Transform bar = FindTyped(root, "TabletStatusBarView");
            string barHow = bar != null ? "type" : null;

            if (bar == null)
            {
                bar = FindNamed(root, "Tablet.XR.StatusBar", 3);
                barHow = bar != null ? "name" : "-";
            }

            t.Tabs = tabs;
            t.Home = home as RectTransform;
            t.StatusBar = bar as RectTransform;
            t.Canvases = tabs != null ? tabs.parent : FindNamed(root, "Canvases", 1);
            t.CanvasesGroup = t.Canvases != null ? t.Canvases.GetComponent<CanvasGroup>() : null;
            t.GrabCollider = FindNamed(root, "GrabCollider", 1)?.gameObject;
            t.Grab = root.GetComponent<XRGrabInteractable>();

            Canvas homeCanvas = home != null ? home.GetComponent<Canvas>() : null;
            GridLayoutGroup grid = home != null ? home.GetComponentInChildren<GridLayoutGroup>(true) : null;
            t.HomeCanvas = homeCanvas;
            t.HomeGrid = grid;
            Button reference = null;
            int uiLayer = 5;
            int gridButtons = 0;
            int gridShown = 0;

            if (grid != null)
            {
                foreach (Transform child in grid.transform)
                {
                    var b = child.GetComponent<Button>();

                    if (b == null)
                    {
                        continue;
                    }

                    gridButtons++;
                    gridShown += child.gameObject.activeSelf && !child.name.StartsWith(Owned, StringComparison.Ordinal) ? 1 : 0; // the client hides what this mode lacks (probe 1603)

                    // Never an earlier visit's clone: a copy of a copy would carry no client look at all.
                    if (reference == null && child.GetComponentInChildren<TMP_Text>(true) != null && !child.name.StartsWith(Owned, StringComparison.Ordinal))
                    {
                        reference = b;
                        var graphic = child.GetComponent<Graphic>();
                        uiLayer = graphic != null ? graphic.gameObject.layer : child.gameObject.layer;
                    }
                }
            }

            t.ReferenceButton = reference;
            t.UiLayer = uiLayer;
            t.GridShown = gridShown;
            bool back = false;
            bool resize = false;

            if (bar != null)
            {
                foreach (Button b in bar.GetComponentsInChildren<Button>(true))
                {
                    bool isBack = b.name.IndexOf("Back", StringComparison.Ordinal) >= 0;
                    bool isResize = b.name.IndexOf("Resize", StringComparison.Ordinal) >= 0;
                    back |= isBack;
                    resize |= isResize;
                    if (isBack && t.Back == null) t.Back = b;
                    if (isResize && t.Resize == null) t.Resize = b;

                    // "Tablet.XR.StatusBar.Exit_World" (Tablet.XR.StatusBar.prefab:775): QuickAction.ExitWorld, the client's
                    // "Exit world?" pop-up (TabletQuickActionsControl.cs:85-92). The System page's Quit presses it.
                    if (t.ExitWorld == null && b.name.IndexOf("Exit_World", StringComparison.Ordinal) >= 0) t.ExitWorld = b;
                }
            }

            int tabCount = 0;
            int contentViews = 0;

            if (tabs != null)
            {
                foreach (Transform tab in tabs)
                {
                    tabCount++;
                    Transform content = tab.Find("ContentView");
                    contentViews += content != null ? 1 : 0;
                    t.TabContents.Add(content != null ? content.gameObject : null);
                }
            }

            var animator = root.GetComponent<Animator>();
            bool isOpenParameter = false;

            if (animator != null && animator.isActiveAndEnabled)
            {
                foreach (AnimatorControllerParameter p in animator.parameters)
                {
                    isOpenParameter |= p.type == AnimatorControllerParameterType.Bool && p.name == "IsOpen";
                }
            }

            t.Animator = animator;
            t.AnimatorHasIsOpen = isOpenParameter;
            bool meshHome = FindMeshHome(root, t.MeshHome);
            bool grab = HasType(root, "XRGrabInteractable");
            bool desktop = root.name.StartsWith("Tablet.NonXR", StringComparison.Ordinal) || !EtsVr.Present;
            t.Desktop = desktop;
            var homeRect = home as RectTransform;
            string size = (homeRect == null ? "home size unknown" :
                          "home " + homeRect.rect.width.ToString("0") + "x" + homeRect.rect.height.ToString("0") + (EtsTabletLayout.IsLarge(homeRect.rect.width) ? " (Large)" : " (Small)")) +
                          ", " + gridShown + " of the grid's buttons shown";

            string missing = (tabs == null ? " Tabs" : "") + (homeCanvas == null ? " HomeCanvas" : "") + (grid == null ? " HomeGrid" : "") +
                             (reference == null ? " ReferenceButton" : "") + (!back ? " Back" : "") + (!resize ? " Resize" : "") +
                             (animator == null ? " Animator" : isOpenParameter ? "" : " IsOpen");

            report = "'" + root.name + "' under '" + (root.parent != null ? root.parent.name : "(scene root)") + "' in scene '" + root.gameObject.scene.name + "', " +
                     (desktop ? "desktop" : "XR") + ", root by " + (rootByType ? "type TabletControl" : "name") +
                     "; TabsContainer by " + tabsHow + ", HomeTab by " + homeHow + ", TabletStatusBarView by " + barHow +
                     "; " + tabCount + " tabs (" + contentViews + " with a ContentView), home grid " + gridButtons + " buttons, " + size +
                     ", UI layer " + uiLayer + ", canvas layer " + (homeCanvas != null ? homeCanvas.gameObject.layer.ToString() : "-") +
                     ", raycaster " + Raycaster(homeCanvas) + ", mesh Home " + (meshHome ? "yes" : "no") + ", grab " + (grab ? "yes" : "no") +
                     ", Exit World " + (t.ExitWorld != null ? "yes" : "no") +
                     (missing.Length > 0 ? "; MISSING:" + missing : "; all parts found");

            complete = tabs != null && homeCanvas != null && grid != null && reference != null;
            return t;
        }

        /// <summary>
        /// The first transform carrying a TabletControl, else the first named "Tablet.XR(" / "Tablet.NonXR(".
        /// Remote players' tablets ("Simulator") and the world's own "Ets ..." subtrees are not entered.
        /// </summary>
        private static Transform SearchRoots(List<GameObject> roots, out bool byType)
        {
            byType = false;
            var stack = new Stack<Transform>();

            foreach (GameObject go in roots)
            {
                if (go != null)
                {
                    stack.Push(go.transform);
                }
            }

            Transform named = null;

            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                string name = t.name;

                if (name.StartsWith("Ets ", StringComparison.Ordinal) || name.IndexOf("Simulator", StringComparison.Ordinal) >= 0)
                {
                    continue;
                }

                if (HasType(t, "TabletControl"))
                {
                    byType = true;
                    return t; // the client's own component: the surest mark
                }

                if (named == null && (name.StartsWith("Tablet.XR(", StringComparison.Ordinal) || name.StartsWith("Tablet.NonXR(", StringComparison.Ordinal)))
                {
                    named = t;
                }

                for (int i = 0; i < t.childCount; i++)
                {
                    stack.Push(t.GetChild(i));
                }
            }

            return named;
        }

        private static bool HasType(Transform t, string typeName)
        {
            t.GetComponents(Buffer);

            foreach (MonoBehaviour mb in Buffer)
            {
                if (mb != null && mb.GetType().Name == typeName)
                {
                    Buffer.Clear();
                    return true;
                }
            }

            Buffer.Clear();
            return false;
        }

        private static Transform FindTyped(Transform root, string typeName)
        {
            foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb != null && mb.GetType().Name == typeName)
                {
                    return mb.transform;
                }
            }

            return null;
        }

        private static Transform FindNamed(Transform root, string name, int maxDepth)
        {
            if (maxDepth < 0)
            {
                return null;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);

                if (child.name == name)
                {
                    return child;
                }
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform deeper = FindNamed(root.GetChild(i), name, maxDepth - 1);

                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
        }

        /// <summary>
        /// The tablet's mesh Home button (Model/.../BottomFrame/HomeButton) fires XRInteractableButton.ButtonPressed on a
        /// poke; the event is a public UnityEvent field, reached by reflection ([tablet §4.2], optional: without it
        /// only Back closes the app). Collects the events; true when there is one.
        /// </summary>
        private static bool FindMeshHome(Transform root, List<UnityEvent> into)
        {
            Transform meshHome = FindNamed(root, "HomeButton", 8);

            if (meshHome == null)
            {
                return false;
            }

            foreach (MonoBehaviour mb in meshHome.GetComponents<MonoBehaviour>())
            {
                FieldInfo field = mb != null ? mb.GetType().GetField("ButtonPressed", BindingFlags.Public | BindingFlags.Instance) : null;

                if (field != null && field.GetValue(mb) is UnityEvent pressed)
                {
                    into.Add(pressed);
                }
            }

            return into.Count > 0;
        }

        /// <summary>Which raycaster the home canvas carries: the app's canvas mirrors it [tablet §4.2].</summary>
        private static string Raycaster(Canvas canvas)
        {
            if (canvas == null)
            {
                return "-";
            }

            string found = "";

            foreach (MonoBehaviour mb in canvas.GetComponents<MonoBehaviour>())
            {
                string type = mb != null ? mb.GetType().Name : "";

                if (type.EndsWith("Raycaster", StringComparison.Ordinal))
                {
                    found += (found.Length > 0 ? "+" : "") + type;
                }
            }

            return found.Length > 0 ? found : "none";
        }

        // ------------------------------------------------------------------ injection

        /// <summary>
        /// What an earlier visit left on this tablet in the same client session (statics and MasterScene outlive a
        /// world, [tablet §2.8]): its ETS.Tablet.* button and canvas go, and a Tabs container it left hidden (the app
        /// was open when that visit ended without an OnDestroy) is shown again.
        /// </summary>
        private static void RemoveLeftovers(Parts t)
        {
            var doomed = new List<GameObject>();

            foreach (Transform child in t.HomeGrid.transform)
            {
                if (child.name.StartsWith(Owned, StringComparison.Ordinal)) doomed.Add(child.gameObject);
            }

            if (t.Canvases != null)
            {
                foreach (Transform child in t.Canvases)
                {
                    if (child.name.StartsWith(Owned, StringComparison.Ordinal)) doomed.Add(child.gameObject);
                }
            }

            foreach (GameObject go in doomed)
            {
                // Out of the grid at once (Destroy is deferred to the end of the frame, and the grid lays out before).
                go.SetActive(false);
                Destroy(go);
            }

            bool tabsShown = false;

            if (t.Tabs != null && !t.Tabs.gameObject.activeSelf)
            {
                t.Tabs.gameObject.SetActive(true);
                tabsShown = true;
            }

            if (doomed.Count > 0 || tabsShown)
            {
                Debug.Log("[SomniumEuroTruck] Tablet: removed " + doomed.Count + " objects left by an earlier visit" + (tabsShown ? "; its hidden Tabs container shown again" : "") + ".");
            }
        }

        private void Inject()
        {
            // The home-screen button: a copy of ONE client button, made under an inactive holder so that no client
            // script on it ever wakes (Instantiate under an inactive parent runs no Awake/OnEnable), stripped to
            // Unity UI + TextMeshPro, and only then moved into the grid - first.
            var staging = new GameObject(Owned + "Staging");
            staging.SetActive(false);
            staging.transform.SetParent(transform, false);

            try
            {
                _button = Instantiate(_t.ReferenceButton.gameObject, staging.transform, false);
                _button.name = Owned + "Button";
                StripToUnityUi(_button);
                _button.transform.SetParent(_t.HomeGrid.transform, false);
                _button.transform.SetSiblingIndex(0);
            }
            finally
            {
                Destroy(staging);
            }

            var button = _button.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent(); // drops the persistent call into the destroyed SwitchTabButton
            button.onClick.AddListener(OnAppButton);
            button.interactable = true;

            foreach (TMP_Text caption in _button.GetComponentsInChildren<TMP_Text>(true))
            {
                caption.text = AppTitle;
            }

            Transform iconTransform = _button.transform.Find("Icon");
            _buttonIcon = iconTransform != null ? iconTransform.GetComponent<Image>() : null;
            SetIcon();
            _button.SetActive(true);

            // The app's own canvas, a sibling of the client's tab canvases under "Canvases" (never inside the Tabs
            // container, which is hidden while the app shows), so the tablet's close fade hides it too.
            _app = new GameObject(Owned + "App", typeof(RectTransform));
            _app.SetActive(false);
            _app.layer = _t.HomeCanvas.gameObject.layer;
            _app.transform.SetParent(_t.Canvases != null ? _t.Canvases : _t.Tabs.parent, false);
            _appCanvas = _app.AddComponent<Canvas>();
            _appCanvas.renderMode = RenderMode.WorldSpace;
            _appCanvas.sortingLayerID = _t.HomeCanvas.sortingLayerID;
            _appCanvas.sortingOrder = _t.HomeCanvas.sortingOrder;
            _appCanvas.additionalShaderChannels = _t.HomeCanvas.additionalShaderChannels;
            _appCanvas.worldCamera = _t.HomeCanvas.worldCamera;

            // Nested canvases need their own raycaster ([tablet §2.5]); mirror what HomeTab really has (the NonXR variant
            // swaps the TrackedDeviceGraphicRaycasters for GraphicRaycasters, Tablet.NonXR.prefab:5298-5388), both if neither.
            bool tracked = _t.HomeCanvas.GetComponent<TrackedDeviceGraphicRaycaster>() != null;
            bool mouse = _t.HomeCanvas.GetComponent<GraphicRaycaster>() != null;

            if (tracked || !mouse)
            {
                _app.AddComponent<TrackedDeviceGraphicRaycaster>();
            }

            if (mouse || !tracked)
            {
                _app.AddComponent<GraphicRaycaster>();
            }

            // RectMask2D clips the +-4000 sheets screens stretch, and filters raycasts to the tablet's screen.
            _app.AddComponent<RectMask2D>();
            var backdrop = _app.AddComponent<Image>();
            backdrop.color = new Color(0.090f, 0.094f, 0.098f, 1f); // clr_desktop_bg_main (ABGR F5191817, probe 1900), opaque
            backdrop.raycastTarget = true; // a miss on the app never falls through to the hidden tabs

            _frame = new GameObject(Owned + "Frame", typeof(RectTransform)).GetComponent<RectTransform>();
            _frame.SetParent(_app.transform, false);
            _frame.anchorMin = _frame.anchorMax = _frame.pivot = new Vector2(0.5f, 0.5f);
            _frame.sizeDelta = new Vector2(EtsTabletLayout.FrameWidth, EtsTabletLayout.FrameHeight);
            _frame.gameObject.layer = _t.UiLayer;
            FollowHomeRect();

            foreach (UnityEvent pressed in _t.MeshHome)
            {
                pressed.AddListener(OnMeshHome);
            }
        }

        /// <summary>The game's logo, painted upright (EtsGameUi.ToTexture turns the rows over), once the archives are mounted.</summary>
        private void SetIcon()
        {
            if (_buttonIcon == null)
            {
                return;
            }

            // Painted once per mounted install: Update asks again at 4 Hz while the icon has no sprite, and a paint that
            // throws must not become a warning (and a 1024 x 512 decode) four times a second.
            if (AppIcon == null && EtsEngine.Files != null && EtsEngine.Files != _iconTriedFor)
            {
                _iconTriedFor = EtsEngine.Files;

                try
                {
                    // /material/ui/logo/ets2_logo.mat: 1024 x 512, effect ui.rfx (probe 1900); the icon cell is 150 x 150.
                    AppIcon = EtsGameUi.ToTexture(EtsGameUi.Painter.Whole("/material/ui/logo/ets2_logo.mat", 256, 128, 0xFFFFFFFF));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Tablet: the app icon could not be painted (" + e.Message + ").");
                    AppIcon = null;
                }
            }

            if (AppIcon != null)
            {
                _buttonIcon.sprite = Sprite.Create(AppIcon, new Rect(0f, 0f, AppIcon.width, AppIcon.height), new Vector2(0.5f, 0.5f), 100f);
                _buttonIcon.preserveAspect = true;
                _buttonIcon.enabled = true;
            }
            else
            {
                // Not the client's "My Avatar" picture under our caption: no picture until the logo can be painted.
                _buttonIcon.sprite = null;
                _buttonIcon.enabled = false;
            }
        }

        /// <summary>
        /// Every MonoBehaviour outside Unity UI and TextMeshPro goes, last-added first (a component is normally added
        /// after what it requires), in up to four passes for RequireComponent chains.
        /// </summary>
        private static void StripToUnityUi(GameObject root)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                int foreign = 0;
                MonoBehaviour[] all = root.GetComponentsInChildren<MonoBehaviour>(true);

                for (int i = all.Length - 1; i >= 0; i--)
                {
                    MonoBehaviour mb = all[i];

                    if (mb == null)
                    {
                        continue;
                    }

                    string assembly = mb.GetType().Assembly.GetName().Name;

                    if (assembly == "UnityEngine.UI" || assembly == "Unity.TextMeshPro")
                    {
                        continue;
                    }

                    foreign++;
                    DestroyImmediate(mb);
                }

                if (foreign == 0)
                {
                    return;
                }
            }
        }

        private void Unlink(bool restore, string why)
        {
            if (AppOpen)
            {
                Close(restore, why);
            }

            if (_t != null)
            {
                foreach (UnityEvent pressed in _t.MeshHome)
                {
                    pressed.RemoveListener(OnMeshHome);
                }
            }

            if (_button != null)
            {
                _button.SetActive(false);
                Destroy(_button);
            }

            if (_app != null)
            {
                Destroy(_app);
            }

            bool was = _t != null;
            _button = null;
            _buttonIcon = null;
            _app = null;
            _appCanvas = null;
            _frame = null;
            _t = null;
            TabletOpen = false;
            _lastPlatform = null;
            HookRendering(false);

            if (was)
            {
                Debug.Log("[SomniumEuroTruck] Tablet: unlinked (" + why + ")" + (_desktopSnaps + _desktopSkips > 0 ? "; desktop re-placed " + _desktopSnaps + " frames, left " + _desktopSkips : "") + ".");

                if (!_ending)
                {
                    LinkChanged?.Invoke();
                }
            }
        }

        // ------------------------------------------------------------------ showing and hiding

        private void OnAppButton()
        {
            EtsGameUi.Note("tablet: ETS 2 app button");
            Open("app button");
        }

        private void OnBack()
        {
            Close(true, "Back");
        }

        private void OnMeshHome()
        {
            Close(true, "mesh Home");
        }

        public void OpenApp()
        {
            Open("world");
        }

        public void CloseApp()
        {
            Close(true, "world");
        }

        private void Open(string why)
        {
            if (!IsLinked || AppOpen || _app == null)
            {
                return;
            }

            _tabSnapshot = SnapshotTabs();
            _t.Tabs.gameObject.SetActive(false);
            _app.SetActive(true);
            SetLayer(_app.transform, _t.UiLayer);
            _app.layer = _t.HomeCanvas.gameObject.layer;

            if (_t.Back != null)
            {
                _backWasActive = _t.Back.gameObject.activeSelf;
                _t.Back.gameObject.SetActive(true); // TabletStatusBarView hides it on HOME (TabletStatusBarView.cs:65-76)
                _t.Back.onClick.AddListener(OnBack);
            }

            float width = _t.Home != null ? _t.Home.rect.width : 0f;
            string size = EtsTabletLayout.IsLarge(width) ? "Large" : "Small";

            if (EtsTabletLayout.ShouldPressResize(ForceLarge, _t.Resize != null, width) && Time.unscaledTime >= _resizeSettleUntil)
            {
                _t.Resize.onClick.Invoke(); // TabletResize toggles Small <-> Large (TabletResize.cs:42-59)
                _enlarged = true;
                _resizeSettleUntil = Time.unscaledTime + 0.35f; // its DOTween runs 0.2 s
                size = "Small->Large";
            }

            AppOpen = true;
            Debug.Log("[SomniumEuroTruck] Tablet: app opened (" + why + ", size " + size + ").");
            AppShown?.Invoke(true);
        }

        private void Close(bool restoreSize, string why)
        {
            if (!AppOpen)
            {
                return;
            }

            AppOpen = false;

            if (_app != null) _app.SetActive(false);
            if (_t != null && _t.Tabs != null) _t.Tabs.gameObject.SetActive(true);

            if (_t != null && _t.Back != null)
            {
                _t.Back.onClick.RemoveListener(OnBack);
                _t.Back.gameObject.SetActive(_backWasActive);
            }

            bool shrunk = false;

            if (restoreSize && _enlarged && _t != null && _t.Resize != null && IsLargeNow())
            {
                _t.Resize.onClick.Invoke();
                _resizeSettleUntil = Time.unscaledTime + 0.35f;
                shrunk = true;
            }

            _enlarged = false;
            Debug.Log("[SomniumEuroTruck] Tablet: app closed (" + why + (shrunk ? ", size Large->Small" : "") + ").");

            if (!_ending)
            {
                AppShown?.Invoke(false);
            }
        }

        private bool IsLargeNow()
        {
            if (Time.unscaledTime < _resizeSettleUntil)
            {
                return true; // our own press is still tweening
            }

            RectTransform measured = _t.Home != null ? _t.Home : _t.StatusBar;
            return measured != null && EtsTabletLayout.IsLarge(measured.rect.width);
        }

        private bool ReadTabletOpen()
        {
            if (_t.Animator != null && _t.AnimatorHasIsOpen)
            {
                return _t.Animator.GetBool(IsOpenHash); // TabletTransitionControl.cs:46/57
            }

            if (_t.CanvasesGroup != null)
            {
                // Armature_CloseFrame.anim fades the Canvases CanvasGroup to alpha 0, not interactable, in 0.15 s.
                return _t.CanvasesGroup.alpha > 0.5f && _t.CanvasesGroup.blocksRaycasts;
            }

            return _t.GrabCollider == null || _t.GrabCollider.activeSelf; // TabletTransitionControl.cs:104-105
        }

        private bool[] SnapshotTabs()
        {
            var shot = new bool[_t.TabContents.Count];

            for (int i = 0; i < shot.Length; i++)
            {
                GameObject content = _t.TabContents[i];
                shot[i] = content != null && content.activeSelf;
            }

            return shot;
        }

        private bool TabsChangedSinceOpen()
        {
            if (_tabSnapshot == null || _tabSnapshot.Length != _t.TabContents.Count)
            {
                return false;
            }

            for (int i = 0; i < _tabSnapshot.Length; i++)
            {
                GameObject content = _t.TabContents[i];

                if ((content != null && content.activeSelf) != _tabSnapshot[i])
                {
                    return true;
                }
            }

            return false;
        }

        private void FollowHomeRect()
        {
            if (_app == null || _t.Home == null)
            {
                return;
            }

            // TabBase.ScaleView tweens a closing tab's scale to (0,0,1) (TabBase.cs:153, 290-306): a home canvas caught
            // mid-tween is not copied. Its size and place are what TabletResizeWatcher animates (Small 960x1080 at
            // (0,-110), Large 1920x1080 at (480,-110): Tablet.XR.HomeTab.prefab:424-434).
            var rect = (RectTransform)_app.transform;

            if (rect.parent == null || _t.Home.lossyScale.x < rect.parent.lossyScale.x * 0.5f)
            {
                return;
            }

            Vector2 size = _t.Home.rect.size;
            rect.localScale = Vector3.one;
            rect.pivot = _t.Home.pivot;
            rect.sizeDelta = size;
            rect.SetPositionAndRotation(_t.Home.position, _t.Home.rotation);

            if (_frame != null)
            {
                float fit = EtsTabletLayout.Fit(size.x, size.y); // Large 1920 x 1080: 1.2; Small 960 x 1080: 0.667
                _frame.localScale = new Vector3(fit, fit, 1f);
            }
        }

        private static void SetLayer(Transform t, int layer)
        {
            // The canvas root keeps the HomeTab canvas's layer (9, Overlay); every graphic under it takes the layer
            // the client's own graphics carry (5, UI), which the XR ray tests per graphic
            // (TrackedDeviceGraphicRaycaster.ShouldTestGraphic, XRI 3.x :644-650).
            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);

                if (child.gameObject.layer != layer)
                {
                    child.gameObject.layer = layer;
                }

                SetLayer(child, layer);
            }
        }

        // ------------------------------------------------------------------ placement while riding

        /// <summary>
        /// PLAN C22: after every LateUpdate, before the first on-screen camera draws, the open desktop tablet is put
        /// back on the camera's TabletPoint. Once per frame, at the first game camera that draws to the screen
        /// (render-texture cameras - the mirrors - are not it).
        /// </summary>
        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_placedFrame == Time.frameCount || camera == null || camera.targetTexture != null || camera.cameraType != CameraType.Game)
            {
                return;
            }

            _placedFrame = Time.frameCount;
            SnapDesktop(true);
        }

        private void SnapDesktop(bool atRender)
        {
            if (!FixDesktopLag || !IsLinked || !_t.Desktop || !TabletOpen)
            {
                return;
            }

            Transform point = DesktopPoint();

            if (point == null)
            {
                return;
            }

            float lag = Vector3.Distance(_t.Root.position, point.position);

            // 1.5 m: a frame's travel at 90 km/h down to 17 fps, at 160 km/h down to 30 fps. Further than that, the
            // client has put its tablet somewhere else on purpose and it is left there.
            if (!EtsTabletLayout.DesktopSnap(_t.Desktop, TabletOpen, lag, 1.5f))
            {
                _desktopSkips++;
                return;
            }

            if (atRender && lag < 0.0005f)
            {
                return; // LateUpdate already put it there (a camera moved after LateUpdate still gets this snap)
            }

            if (!atRender && !_shakeFixReported)
            {
                _shakeFixReported = true;
                Debug.Log("[SomniumEuroTruck] Tablet: desktop tablet placed in LateUpdate, before the canvases are batched (" + lag.ToString("0.000") + " m from the camera's TabletPoint).");
            }

            _desktopSnaps++;
            _t.Root.SetPositionAndRotation(point.position, point.rotation);
        }

        private Transform DesktopPoint()
        {
            Camera camera = Camera.main;

            if (camera != _desktopPointCamera)
            {
                _desktopPointCamera = camera;
                // CameraSystem/Camera/TabletPoint, local (-0.224, 0, 0.724) (CameraSystem.prefab, [tablet §2.2]).
                _desktopPoint = camera != null ? camera.transform.Find("TabletPoint") : null;

                if (_desktopPoint == null && camera != null && !_desktopPointMissingReported)
                {
                    _desktopPointMissingReported = true;
                    Debug.Log("[SomniumEuroTruck] Tablet: no TabletPoint under the main camera '" + camera.name + "' - the desktop tablet is left where the client puts it.");
                }
            }

            return _desktopPoint;
        }

        private void Carry()
        {
            Transform platform = RidingOn != null ? RidingOn() : null;

            if (Time.unscaledTime >= _nextBodyRead)
            {
                _nextBodyRead = Time.unscaledTime + 0.25f;

                try
                {
                    _bodyRoot = SomniumBridge.PlayersContainer?.LocalPlayer?.References?.Body?.Root;
                }
                catch (Exception)
                {
                    _bodyRoot = null; // no client (editor)
                }
            }

            bool follows = _bodyRoot != null && _t.Root.IsChildOf(_bodyRoot); // "tablet follows player": the rig carries it already
            bool held = _t.Grab != null && _t.Grab.isSelected;

            if (platform == null || follows || held)
            {
                _lastPlatform = null;
                return;
            }

            if (_lastPlatform == platform)
            {
                // The same delta the platform moved this frame, applied to the free-standing tablet.
                Quaternion turn = platform.rotation * Quaternion.Inverse(_platformRot);
                Vector3 offset = _t.Root.position - _platformPos;
                _t.Root.SetPositionAndRotation(platform.position + turn * offset, turn * _t.Root.rotation);
            }

            _lastPlatform = platform;
            _platformPos = platform.position;
            _platformRot = platform.rotation;
        }
    }
}
