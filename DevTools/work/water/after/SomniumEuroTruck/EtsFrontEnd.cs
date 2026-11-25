using System;
using System.Collections;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The game's front end, in the game's order: the launchpad over the garage, the loading
    /// screen, the desktop with its button bar, the job market, the loading screen again with the
    /// job on it, the drive with the route advisor, the delivery summary, and - this being the
    /// demo - the demo's end screen.
    ///
    /// Every screen is laid out where the game's own file puts it (/ui/launchpad.sii,
    /// /ui/desktop_button_bar.sii, /ui/quick_job.sii, /ui/loading.sii, /ui/delivery.sii), set in
    /// the game's own faces (EtsFont), on chrome painted from the game's own atlases the way its
    /// templates paint it (EtsUiPainter), with its own icons, logos and photographs. Behind the
    /// menus is the garage /def/desktop/config.sii describes (EtsGarageScene), with the truck in
    /// it. The wording alone is written here, because the locale files are obfuscated.
    ///
    /// The new flow (EtsFeatures.NewFlow, Phase 2, owner D) keeps only its diagnostics: F9 (blended ground), F10 (what
    /// is under the mouse), F11 and the automatic ground census round the local truck, and the 10 s heartbeat line -
    /// added by EtsFlow through EnsureDiagnostics. Its launchpad, garage, loading screens, desktop, job market and
    /// delivery summary never show there, even if something adds it with Enabled set: the HQ stage, EtsLoadingScreen
    /// and the tablet's pages are the new flow's screens. With the flag off it is exactly the old front end, which is the
    /// fallback.
    /// </summary>
    public class EtsFrontEnd : MonoBehaviour
    {
        [Tooltip("Show the game's front end before driving.")]
        public bool Enabled = true;

        /// <summary>Only F9-F11 and the heartbeat (the new flow). Set by EnsureDiagnostics; implied by EtsFeatures.NewFlow.</summary>
        public bool DiagnosticsOnly;

        /// <summary>The diagnostics-only front end on the bootstrap object (EtsFlow.Start, new flow). Unity's null test, not ??.</summary>
        public static EtsFrontEnd EnsureDiagnostics(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsFrontEnd frontEnd = host.GetComponent<EtsFrontEnd>();

            if (frontEnd == null)
            {
                frontEnd = host.AddComponent<EtsFrontEnd>();
            }

            frontEnd.DiagnosticsOnly = true;
            frontEnd.Enabled = false;
            return frontEnd;
        }

        private bool Diagnostics => DiagnosticsOnly || EtsFeatures.NewFlow;

        [Tooltip("Prefer photographs from the base game over ones belonging to a DLC.")]
        public bool BaseGamePicturesOnly = true;

        public EtsMenu Menu;
        public EtsTruckController Truck;
        public EtsJobs Jobs;
        public EtsRouteAdvisor Advisor;

        /// <summary>The built truck, copied into the garage behind the menus.</summary>
        public GameObject TruckRoot;

        public string ProfileName = "Somnium Driver";

        private enum Stage { Launchpad, Loading, Desktop, JobMarket, JobLoading, Driving, Delivery, DemoEnd }

        private Stage _stage = Stage.Launchpad;
        private GameObject _launchpad;
        private GameObject _loading;
        private GameObject _desktop;
        private GameObject _market;
        private GameObject _delivery;
        private EtsGarageScene _garage;
        private Camera _worldCamera;
        private RectTransform _progressBar;
        private float _progressBarFullWidth;
        private TextMeshProUGUI _jobLine;
        private Coroutine _progressWatch;

        private void Start()
        {
            if (Diagnostics)
            {
                // The new flow: no screens of this front end, ever (class notes).
                _stage = Stage.Driving;
                StartCoroutine(Heartbeat());
                return;
            }

            if (!Enabled)
            {
                _stage = Stage.Driving;
                return;
            }

            StartCoroutine(Run());
        }

        private void OnDestroy()
        {
            if (Jobs != null)
            {
                Jobs.JobStarting -= OnJobStarting;
                Jobs.JobStarted -= OnJobStarted;
                Jobs.JobFailed -= OnJobFailed;
                Jobs.Delivered -= OnDelivered;
            }
        }

        private void Update()
        {
            // F7 shows or hides the map viewer's on-screen diagnostics text (hidden by default).
            if (Input.GetKeyDown(KeyCode.F7))
            {
                EtsMapViewer.StatusShown = !EtsMapViewer.StatusShown;
                EtsMapViewer viewer = FindAnyObjectByType<EtsMapViewer>();

                if (viewer != null)
                {
                    viewer.ApplyStatusShown();
                }

                Debug.Log("[SomniumEuroTruck] F7: diagnostics text " + (EtsMapViewer.StatusShown ? "shown" : "hidden") + ".");
            }

            // F9 swaps the ground's blended-pair meshes for their single-brush stand-ins and
            // back, so which of the two is drawing wrongly can be seen rather than argued about.
            if (Input.GetKeyDown(KeyCode.F9))
            {
                EtsTerrainBuilder.SetPairsVisible(!EtsTerrainBuilder.PairsVisible);
                EtsGameUi.Note("F9: blended ground " + (EtsTerrainBuilder.PairsVisible ? "SHOWN (two-layer shader)" : "hidden (single-brush stand-ins shown)") + " on " + EtsTerrainBuilder.Pairs.Count + " groups. " + EtsTerrainBuilder.Diagnose());
            }

            // F10 says what is under the mouse: the object, its mesh, material, textures and
            // whether anything is drawn there at all. Point at a wrong-looking patch and press it.
            if (Input.GetKeyDown(KeyCode.F10))
            {
                EtsGameUi.Note("F10: " + EtsProbe.UnderCursor());
            }

            // F11 surveys the ground round the truck for holes and for surfaces lying in one plane
            // (the ones that flicker), naming each. It also runs once by itself a minute in, when
            // the world round the bay has streamed and settled, so the log always carries one.
            bool censusKey = Input.GetKeyDown(KeyCode.F11);

            // The new flow's truck is the local player's (the one they sit in, else their own), not a scene field.
            GameObject root = TruckRoot;

            if (Diagnostics)
            {
                EtsTruckRig rig = EtsLocalTruck.Occupied != null ? EtsLocalTruck.Occupied : EtsLocalTruck.Own;
                root = rig != null ? rig.gameObject : null;
            }

            if (root != null && EtsMapViewer.AnyCompleted)
            {
                // Ten seconds after the first build finishes the streamed ring round the bay has
                // settled; census then, and again once the truck has driven well clear of the
                // first spot, so a log from a short drive still carries the ground it crossed.
                if (_readyAt < 0f) _readyAt = Time.timeSinceLevelLoad;
                Vector3 at = root.transform.position;
                bool first = _censusCount == 0 && Time.timeSinceLevelLoad > _readyAt + 10f;
                bool moved = _censusCount == 1 && (at - _censusAt).magnitude > 150f && Time.timeSinceLevelLoad > _readyAt + 40f;
                bool refilled = _censusCount >= 1 && EtsGroundFill.Builds >= 2 && EtsGroundFill.Builds != _censusFillBuilds;

                if (censusKey || first || moved || refilled)
                {
                    _censusCount++;
                    _censusAt = at;
                    _censusFillBuilds = EtsGroundFill.Builds;
                    EtsGameUi.Note((censusKey ? "F11: " : "auto " + _censusCount + ": ") + EtsProbe.GroundCensus(at, 120f, 2f));
                }
            }
        }

        private float _readyAt = -1f;
        private int _censusCount;
        private Vector3 _censusAt;

        // The fill rebuilds every 300 m the truck moves; a census after each one shows whether it held.
        private int _censusFillBuilds;

        private IEnumerator Run()
        {
            while (!EtsEngine.IsMounted)
            {
                yield return new WaitForSeconds(0.1f);
            }

            // A frame, so every component added alongside this one has had its Start.
            yield return null;

            if (EventSystem.current == null)
            {
                var events = new GameObject("EventSystem");
                events.AddComponent<EventSystem>();
                events.AddComponent<StandaloneInputModule>();
            }

            if (Jobs != null)
            {
                Jobs.JobStarting += OnJobStarting;
                Jobs.JobStarted += OnJobStarted;
                Jobs.JobFailed += OnJobFailed;
                Jobs.Delivered += OnDelivered;
            }

            _worldCamera = Camera.main;
            HoldTheDrive();
            BuildGarage();
            ShowLaunchpad();
            StartCoroutine(Heartbeat());
        }

        /// <summary>
        /// Every ten seconds: which screen is up, where the truck is, how fast, what job. So the
        /// console reads as a diary of where the player was.
        /// </summary>
        private IEnumerator Heartbeat()
        {
            while (true)
            {
                yield return new WaitForSeconds(10f);

                if (Diagnostics)
                {
                    EtsGameUi.Note(NewFlowHeartbeat());
                    continue;
                }

                string where = "stage " + _stage;

                if (Truck != null)
                {
                    Vector3 p = Truck.transform.position;
                    where += ", truck at (" + p.x.ToString("0") + ", " + p.y.ToString("0") + ", " + p.z.ToString("0") + ") " + Mathf.RoundToInt(Truck.SpeedKmh) + " km/h";
                }

                if (Jobs != null)
                {
                    if (Jobs.Current != null)
                    {
                        float remaining = Jobs.Route != null && Jobs.Route.Visible && Truck != null ? Jobs.Route.RemainingDistance(Truck.transform.position) : -1f;
                        where += ", job " + Jobs.Current.CargoName + " to " + Jobs.Current.ToCompanyName + " " + Jobs.Current.ToCityName + (remaining >= 0f ? ", " + Jobs.Current.ToGameKm(remaining).ToString("0.0") + " km to go" : "");
                    }
                    else
                    {
                        where += ", no job, " + Jobs.Offers.Count + " offers" + (Jobs.MapIndexed ? "" : ", map still indexing");
                    }
                }

                if (Menu != null && Menu.IsOpen)
                {
                    where += ", Escape menu open";
                }

                EtsGameUi.Note(where);
            }
        }

        /// <summary>
        /// The new flow's 10 s line: the phase and what the flow shows, the master, the local truck (own or ridden), the
        /// job or the offers, and whether the tablet app is on screen - so a client log reads as a diary of the player's
        /// journey through PLAN §5.5.
        /// </summary>
        private static string NewFlowHeartbeat()
        {
            var line = new System.Text.StringBuilder(200);
            EtsSession session = EtsSession.Instance;
            EtsFlow flow = EtsFlow.Instance;
            line.Append("phase ").Append(session != null ? session.Current.ToString() : "none");

            if (session != null)
            {
                line.Append(session.IsMaster ? " (master me)" : session.MasterName.Length > 0 ? " (master " + session.MasterName + ")" : "");
            }

            if (flow != null)
            {
                line.Append(", shows ").Append(flow.View.ToString());
            }

            EtsTruckRig rig = EtsLocalTruck.Occupied != null ? EtsLocalTruck.Occupied : EtsLocalTruck.Own;

            if (rig != null && rig.Controller != null)
            {
                Vector3 p = rig.transform.position;
                line.Append(", ").Append(rig == EtsLocalTruck.Occupied ? "in " : "own truck ").Append(rig.Stem ?? "truck").Append(" at (")
                    .Append(p.x.ToString("0")).Append(", ").Append(p.y.ToString("0")).Append(", ").Append(p.z.ToString("0")).Append(") ")
                    .Append(Mathf.RoundToInt(rig.Controller.SpeedKmh)).Append(" km/h");
            }
            else
            {
                line.Append(", no truck");
            }

            EtsJobs jobs = EtsJobs.Instance;

            if (jobs != null)
            {
                if (jobs.Current != null)
                {
                    line.Append(", job ").Append(jobs.Current.CargoName).Append(" to ").Append(jobs.Current.ToCompanyName).Append(' ').Append(jobs.Current.ToCityName);
                }
                else
                {
                    line.Append(", no job, ").Append(jobs.Offers.Count).Append(" offers").Append(jobs.MapIndexed ? "" : ", map still indexing");
                }
            }

            line.Append(EtsTabletApp.IsVisible ? ", tablet app open" : "");
            return line.ToString();
        }

        private void HoldTheDrive()
        {
            if (Menu != null)
            {
                Menu.OpenAtStart = false;
                Menu.AllowEscape = false;
                Menu.Close();
            }

            if (Truck != null)
            {
                Truck.InputEnabled = false;
            }

            if (Advisor != null)
            {
                Advisor.Visible = false;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // ------------------------------------------------------------------ the garage

        private void BuildGarage()
        {
            _garage = gameObject.GetComponent<EtsGarageScene>() ?? gameObject.AddComponent<EtsGarageScene>();
            string report = _garage.Build(TruckRoot);
            Debug.Log("[SomniumEuroTruck] Garage: " + report + ".");
            GarageOn(true);
        }

        private void GarageOn(bool on)
        {
            // In a headset the camera is the headset's and must never be switched off, and the
            // garage is a backdrop for a fixed camera 2.5 km under the map with no floor to stand
            // on. So the menus hang in front of the player wherever they are instead (EtsVrPanel).
            if (EtsVr.Present)
            {
                if (_garage != null && _garage.Built)
                {
                    _garage.Show(false);
                }

                return;
            }

            if (_garage != null && _garage.Built)
            {
                _garage.Show(on);
            }

            if (_worldCamera != null)
            {
                _worldCamera.enabled = !on;
            }
        }

        // ------------------------------------------------------------------ the launchpad

        /// <summary>
        /// /ui/launchpad.sii, element by element: the window 308..1132 x 288..818 with its title
        /// and close button, the big Continue Game plate 333..1107 x 437..692 that btn.launcher.
        /// continue.game draws with the logo and the words beneath, the profile row, the six
        /// screen links at 430..1010 x 315..417 with their icons from /material/ui/link, the two
        /// news boxes 212..717 and 723..1228 x 80..278, and the academy link at 1340..1420.
        /// </summary>
        private void ShowLaunchpad()
        {
            _stage = Stage.Launchpad;
            _launchpad = Screen("Ets launchpad", 400, EtsVr.Present || _garage == null || !_garage.Built, out RectTransform frame);

            Window(frame, 308f, 1132f, 288f, 818f, false);
            EtsGameUi.Label(EtsGameUi.Place(frame, "title", 308f, 1132f, 775f, 805f), EtsGameUi.Text("@@launchpad_t@@"), EtsFont.Title, TextAlignmentOptions.Center, EtsGameUi.SelectedColour);
            CloseButton(frame, 1090f, 1120f, 775f, 805f, () => Application.Quit());

            // The big plate: the logo, the words, the whole thing a button.
            RectTransform plate = EtsGameUi.Place(frame, "continue", 333f, 1107f, 437f, 692f);
            RectTransform logo = EtsGameUi.Place(plate, "logo", (774f - 512f) * 0.5f, (774f + 512f) * 0.5f, 10f, 266f);
            logo.anchoredPosition = new Vector2((774f - 512f) * 0.5f, 5f);
            EtsGameUi.Painted(logo, EtsGameUi.Painter.Whole("/material/ui/launcher/game_button_normal.mat", 512, 256, 0xFFFFFFFF));
            RectTransform lit = EtsGameUi.Place(plate, "lit", (774f - 512f) * 0.5f, (774f + 512f) * 0.5f, 5f, 261f);
            RawImage litImage = EtsGameUi.Painted(lit, EtsGameUi.Painter.Whole("/material/ui/launcher/game_button_hover.mat", 512, 256, 0xFFFFFFFF));
            litImage.color = new Color(1f, 1f, 1f, 0f);
            EtsGameUi.Label(EtsGameUi.Place(plate, "words", 0f, 774f, 6f, 40f), EtsGameUi.Text("@@continue_game@@"), EtsFont.Big, TextAlignmentOptions.Center, Color.white);
            PlateButton(plate, litImage, BeginLoading);

            // The profile: the driver's picture (the top-left 96 pixels of the 128-pixel image, as
            // the markup crops it), and the row naming the profile and its level.
            RectTransform avatar = EtsGameUi.Place(frame, "avatar", 333f, 397f, 707f, 771f);
            EtsGameUi.Painted(avatar, EtsGameUi.Painter.Region("/material/ui/driver/empty.mat", 0, 0, 96, 96, 64, 64, 0xFFFFFFFF));
            RectTransform profile = EtsGameUi.Place(frame, "profile", 427f, 804f, 674f, 706f);
            EtsGameUi.Painted(profile, EtsGameUi.Painter.Strip("/material/ui/button/btn_dark.mat", 377, 32, 0xFFFFFFFF));
            RectTransform small = EtsGameUi.Place(profile, "small avatar", 12f, 40f, 2f, 30f);
            EtsGameUi.Painted(small, EtsGameUi.Painter.Region("/material/ui/driver/empty.mat", 0, 0, 96, 96, 28, 28, 0xFFFFFFFF));
            TextMeshProUGUI name = EtsGameUi.Label(EtsGameUi.Place(profile, "name", 52f, 370f, 0f, 32f), "", EtsFont.Normal, TextAlignmentOptions.Left, EtsGameUi.TextColour);
            name.richText = true;
            name.text = "<color=#00AEFF>" + ProfileName + "</color> <color=#D7D7D7>(" + EtsGameUi.Text("@@profile_level@@") + " 1)</color>";

            RectTransform mods = EtsGameUi.Place(frame, "mod manager", 916f, 980f, 692f, 756f);
            EtsGameUi.Painted(mods, EtsGameUi.Painter.Whole("/material/ui/button/mod_manager_icon.mat", 64, 64, 0xFFFFFFFF));

            // The six screen links, each an icon over a caption, all leading nowhere this world has.
            string[] targets = { "edit_curr_prof", "manage_profiles", "dlc_browser_open", "mod_manager", "options", "credits" };
            string[] captions = { "@@edit_profile_btn@@", "@@manage_profiles_btn@@", "@@dlc_browser_btn@@", "@@mod_manager_btn@@", "@@options@@", "@@about@@" };

            for (int i = 0; i < targets.Length; i++)
            {
                float left = 430f + i * 100f;
                ScreenLink(frame, targets[i], EtsGameUi.Text(captions[i]), left, left + 80f, 315f, 417f, null);
            }

            ScreenLink(frame, "academy", EtsGameUi.Text("@@academy@@"), 1340f, 1420f, 113f, 215f, null);

            // The two news boxes, as the game shows them without a connection.
            NewsBox(frame, 212f, 717f, "@@latest_news_t@@", 230f, 700f, 337f, 599f, 236f, 600f, 610f);
            NewsBox(frame, 723f, 1228f, "@@newest_dlc_t@@", 741f, 1211f, 850f, 1112f, 747f, 1111f, 1122f);

            Debug.Log("[SomniumEuroTruck] Launchpad shown over the garage.");
        }

        private void NewsBox(RectTransform frame, float left, float right, string titleKey, float titleLeft, float titleRight,
            float buttonLeft, float buttonRight, float textLeft, float textRight, float dotsLeft)
        {
            Window(frame, left, right, 80f, 278f, true);
            EtsGameUi.Label(EtsGameUi.Place(frame, "news title", titleLeft, titleRight, 239f, 269f), EtsGameUi.Text(titleKey), EtsFont.Title, TextAlignmentOptions.Center, EtsGameUi.SelectedColour);
            EtsGameUi.MakeButton(EtsGameUi.Place(frame, "enable news", buttonLeft, buttonRight, 152f, 184f), EtsGameUi.Text("@@enable_news@@"), null);
            EtsGameUi.Label(EtsGameUi.Place(frame, "news off", textLeft, textRight, 103f, 136f), EtsGameUi.Text("@@news_disabled@@"), EtsFont.Big, TextAlignmentOptions.Left, EtsGameUi.SelectedColour);

            for (int i = 0; i < 5; i++)
            {
                RectTransform dot = EtsGameUi.Place(frame, "dot", dotsLeft + i * 17f, dotsLeft + i * 17f + 16f, 112f, 128f);
                EtsGameUi.Painted(dot, EtsGameUi.Painter.Whole("/material/ui/button/dot.mat", 16, 16, 0xFF808080));
            }
        }

        /// <summary>
        /// btn.screen_link: the icon from /material/ui/link/&lt;name&gt;.mat, left half at rest and
        /// right half under the pointer, with the caption 66 pixels down in the small face.
        /// </summary>
        private void ScreenLink(RectTransform parent, string linkName, string caption, float left, float right, float bottom, float top, Action onClick)
        {
            RectTransform rect = EtsGameUi.Place(parent, "link " + linkName, left, right, bottom, top);
            float width = right - left, height = top - bottom;
            RectTransform icon = EtsGameUi.Place(rect, "icon", (width - 64f) * 0.5f, (width + 64f) * 0.5f, height - 64f, height);
            EtsGameUi.LinkIcon(icon, linkName, false);
            RectTransform litRect = EtsGameUi.Place(rect, "lit", (width - 64f) * 0.5f, (width + 64f) * 0.5f, height - 64f, height);
            RawImage lit = EtsGameUi.LinkIcon(litRect, linkName, true);
            lit.color = new Color(1f, 1f, 1f, 0f);
            TextMeshProUGUI label = EtsGameUi.Label(EtsGameUi.Place(rect, "caption", -10f, width + 10f, 0f, height - 66f), caption, EtsFont.Small, TextAlignmentOptions.Top, EtsGameUi.TextColour);
            label.textWrappingMode = TextWrappingModes.Normal;
            PlateButton(rect, lit, onClick);
        }

        /// <summary>A button whose lit face fades in under the pointer, as the game's do.</summary>
        private static Button PlateButton(RectTransform rect, Graphic lit, Action onClick)
        {
            // Something on the rectangle has to take the pointer. A painted plate already there
            // will do; otherwise a clear image is added. (Only one graphic may sit on an object.)
            Graphic catcher = rect.GetComponent<Graphic>();

            if (catcher == null)
            {
                var clear = rect.gameObject.AddComponent<Image>();
                clear.color = new Color(1f, 1f, 1f, 0f);
                catcher = clear;
            }

            catcher.raycastTarget = true;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = lit;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock colours = button.colors;
            colours.normalColor = new Color(1f, 1f, 1f, 0f);
            colours.highlightedColor = Color.white;
            colours.selectedColor = new Color(1f, 1f, 1f, 0f);
            colours.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            colours.fadeDuration = 0.1f;
            button.colors = colours;

            string pressed = rect.name;
            button.onClick.AddListener(() =>
            {
                EtsGameUi.Note("pressed \"" + pressed + "\"" + (onClick == null ? " (nothing behind it in this world)" : ""));
                onClick?.Invoke();
            });

            return button;
        }

        private static void CloseButton(RectTransform frame, float left, float right, float bottom, float top, Action onClick)
        {
            RectTransform rect = EtsGameUi.Place(frame, "close", left, right, bottom, top);
            EtsGameUi.Painted(rect, EtsGameUi.Painter.Half("/material/ui/button/close.mat", false, 30, 30, 0xFFC0C0C0));
            RectTransform litRect = EtsGameUi.Place(rect, "lit", 0f, 30f, 0f, 30f);
            RawImage lit = EtsGameUi.Painted(litRect, EtsGameUi.Painter.Half("/material/ui/button/close.mat", true, 30, 30, 0xFFFFFFFF));
            lit.color = new Color(1f, 1f, 1f, 0f);
            PlateButton(rect, lit, onClick);
        }

        private static void Window(RectTransform frame, float left, float right, float bottom, float top, bool dark)
        {
            EtsGameUi.WindowPlate(EtsGameUi.Place(frame, dark ? "dark window" : "window", left, right, bottom, top), dark);
        }

        /// <summary>A canvas in the 1440 x 900 frame, with or without a sheet behind it.</summary>
        private static GameObject Screen(string name, int sortingOrder, bool opaque, out RectTransform frame)
        {
            Canvas canvas = EtsGameUi.CreateCanvas(name, sortingOrder);
            frame = EtsGameUi.Place(canvas.transform, "frame", 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);

            RectTransform sheetRect = EtsGameUi.Place(frame, "sheet", 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);
            sheetRect.anchorMin = Vector2.zero;
            sheetRect.anchorMax = Vector2.one;
            sheetRect.offsetMin = new Vector2(-2000f, -2000f);
            sheetRect.offsetMax = new Vector2(2000f, 2000f);
            var sheet = sheetRect.gameObject.AddComponent<Image>();
            sheet.color = opaque ? new Color(0.04f, 0.05f, 0.07f, 1f) : new Color(0f, 0f, 0f, 0f);
            sheet.raycastTarget = true;
            return canvas.gameObject;
        }

        // ------------------------------------------------------------------ loading

        private void BeginLoading()
        {
            Clear(ref _launchpad);
            _stage = Stage.Loading;
            ShowLoading(null);
            _progressWatch = StartCoroutine(WatchProgress());
        }

        private void ShowLoading(EtsJobs.Offer job)
        {
            Picture chosen = ChoosePicture();
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "txt.loading.pic", chosen.MaterialPath },
                { "txt.normal.right", chosen.Location },
                { "txt.normal.orange.right.top", chosen.Collection },
                { "txt.lscr.author", "SCS Software" }
            };

            if (job != null)
            {
                overrides["txt.loading.job"] = job.CargoName + "   " + job.FromCityName + " - " + job.ToCityName + "   " + Mathf.RoundToInt(job.ShownKm) + " km   " + job.Reward.ToString("N0") + " EUR";
            }

            _loading = EtsGameUi.BuildScreen("/ui/loading.sii", 450, new Dictionary<string, Action>(), overrides,
                out string report, out Dictionary<string, RectTransform> byLook);

            if (_loading == null)
            {
                Debug.Log("[SomniumEuroTruck] Loading screen: " + report);
                return;
            }

            _loading.transform.SetParent(transform, false);
            Transform frame = _loading.transform.GetChild(0);

            _progressBarFullWidth = 976f - 464f;
            _progressBar = EtsGameUi.Place(frame, "progress", 464f, 976f, 169f, 180f);
            var fill = _progressBar.gameObject.AddComponent<Image>();
            fill.color = new Color(0f, 0.68f, 1f);
            fill.raycastTarget = false;
            _progressBar.sizeDelta = new Vector2(1f, _progressBar.sizeDelta.y);

            byLook.TryGetValue("txt.loading.job", out RectTransform jobRect);
            _jobLine = jobRect != null ? jobRect.GetComponent<TextMeshProUGUI>() : null;

            Debug.Log("[SomniumEuroTruck] Loading screen from the game's own layout - " + report + "; picture " + chosen.Name + " (" + chosen.Location + ").");
        }

        private IEnumerator WatchProgress()
        {
            while (true)
            {
                float progress = Mathf.Clamp01(EtsMapViewer.LoadProgress);
                SetProgress(progress);

                if (_jobLine != null)
                {
                    _jobLine.text = EtsMapViewer.LoadStage + "   -   " + Mathf.RoundToInt(progress * 100f) + " %";
                }

                if (EtsMapViewer.AnyCompleted && progress >= 0.999f && Truck != null && Truck.Placed)
                {
                    break;
                }

                yield return null;
            }

            yield return new WaitForSeconds(0.6f);
            Clear(ref _loading);
            ShowDesktop();
        }

        private void SetProgress(float progress)
        {
            if (_progressBar != null)
            {
                _progressBar.sizeDelta = new Vector2(Mathf.Max(1f, _progressBarFullWidth * Mathf.Clamp01(progress)), _progressBar.sizeDelta.y);
            }
        }

        // ------------------------------------------------------------------ the desktop

        /// <summary>
        /// The desktop: the garage with the truck, the strip across the top with the profile and
        /// the money, and the button bar /ui/desktop_button_bar.sii lays across the foot of the
        /// screen at 260..1180 x 5..133 with its screen links.
        /// </summary>
        private void ShowDesktop()
        {
            _stage = Stage.Desktop;
            GarageOn(true);
            HoldTheDrive();
            _desktop = Screen("Ets desktop", 400, EtsVr.Present || _garage == null || !_garage.Built, out RectTransform frame);

            RectTransform strip = EtsGameUi.Place(frame, "top bar", 0f, 1440f, 860f, 900f);
            var stripImage = strip.gameObject.AddComponent<Image>();
            stripImage.color = new Color(0.035f, 0.035f, 0.035f, 0.94f);
            stripImage.raycastTarget = false;
            RectTransform avatar = EtsGameUi.Place(frame, "avatar", 24f, 56f, 864f, 896f);
            EtsGameUi.Painted(avatar, EtsGameUi.Painter.Region("/material/ui/driver/empty.mat", 0, 0, 96, 96, 32, 32, 0xFFFFFFFF));
            EtsGameUi.Label(EtsGameUi.Place(frame, "profile", 64f, 500f, 860f, 900f), ProfileName, EtsFont.Normal, TextAlignmentOptions.Left, new Color(0f, 0.68f, 1f));
            // The saved balance (EtsJobs.Balance: the career's money, or the game's starting 2000 plus this visit's
            // pay). It equals the old 2000 + Earnings while nothing spends money, and is a long, so a large career
            // cannot overflow it (Phase 1 check G).
            long money = Jobs != null ? Jobs.Balance : EtsProfileCodec.StartingMoney;
            EtsGameUi.Label(EtsGameUi.Place(frame, "money", 1000f, 1416f, 860f, 900f), "€ " + money.ToString("N0"), EtsFont.Normal, TextAlignmentOptions.Right, EtsGameUi.SelectedColour);
            EtsGameUi.Label(EtsGameUi.Place(frame, "scene", 560f, 880f, 860f, 900f), EtsGameUi.Text("@@garage_scene@@"), EtsFont.Normal, TextAlignmentOptions.Center, EtsGameUi.TextColour);

            RectTransform bar = EtsGameUi.Place(frame, "button bar", 260f, 1180f, 5f, 133f);
            EtsGameUi.Painted(bar, EtsGameUi.Painter.Whole("/material/ui/window/link_bcg.mat", 920, 128, 0xFFFFFFFF));

            ScreenLink(frame, "job_market", "Job market", 540f, 640f, 21f, 119f, ShowJobMarket);
            ScreenLink(frame, "quick_travel", "Drive", 660f, 760f, 21f, 119f, StartDriving);
            ScreenLink(frame, "truck_config", "Truck", 780f, 880f, 21f, 119f, null);

            Debug.Log("[SomniumEuroTruck] Desktop shown: job market, drive.");
        }

        // ------------------------------------------------------------------ the job market

        private readonly List<GameObject> _offerRows = new List<GameObject>();
        private RectTransform _marketFrame;
        private RectTransform _marketDetails;
        private TextMeshProUGUI _marketEmpty;
        private int _selectedOffer = -1;
        private int _shownOffers = -1;
        private float _nextOfferTry;

        /// <summary>
        /// /ui/quick_job.sii: the window 7..1433 x 138..896, the title, the list of offers down the
        /// left in 28..780, the chosen job's details in the panel at 802..1420 x 239..809 and the
        /// Take job button beneath it at 802..1420 x 161..193. Company logos are the game's own,
        /// /material/ui/company/small.
        /// </summary>
        private void ShowJobMarket()
        {
            Clear(ref _desktop);
            _stage = Stage.JobMarket;
            _market = Screen("Ets job market", 410, false, out RectTransform frame);
            _marketFrame = frame;
            Window(frame, 7f, 1433f, 138f, 896f, false);
            EtsGameUi.Label(EtsGameUi.Place(frame, "title", 0f, 1440f, 856f, 888f), "Job market", EtsFont.Title, TextAlignmentOptions.Center, EtsGameUi.SelectedColour);
            CloseButton(frame, 1390f, 1420f, 855f, 885f, () => { Clear(ref _market); ShowDesktop(); });

            RectTransform header = EtsGameUi.Place(frame, "header", 28f, 780f, 812f, 837f);
            var headerImage = header.gameObject.AddComponent<Image>();
            headerImage.color = new Color(0.03f, 0.03f, 0.03f, 0.75f);
            headerImage.raycastTarget = false;
            EtsGameUi.Label(EtsGameUi.Place(frame, "h1", 40f, 320f, 812f, 837f), "Company", EtsFont.Small, TextAlignmentOptions.Left, new Color(0.62f, 0.63f, 0.65f));
            EtsGameUi.Label(EtsGameUi.Place(frame, "h2", 330f, 560f, 812f, 837f), "Cargo", EtsFont.Small, TextAlignmentOptions.Left, new Color(0.62f, 0.63f, 0.65f));
            EtsGameUi.Label(EtsGameUi.Place(frame, "h3", 560f, 660f, 812f, 837f), "Distance", EtsFont.Small, TextAlignmentOptions.Right, new Color(0.62f, 0.63f, 0.65f));
            EtsGameUi.Label(EtsGameUi.Place(frame, "h4", 660f, 770f, 812f, 837f), "Reward", EtsFont.Small, TextAlignmentOptions.Right, new Color(0.62f, 0.63f, 0.65f));

            _marketDetails = EtsGameUi.Place(frame, "details", 802f, 1420f, 239f, 809f);
            EtsGameUi.WindowPlate(_marketDetails, true);

            _marketEmpty = EtsGameUi.Label(EtsGameUi.Place(frame, "empty", 918f, 1313f, 462f, 567f), "", EtsFont.Normal, TextAlignmentOptions.Center, EtsGameUi.TextColour);

            EtsGameUi.MakeButton(EtsGameUi.Place(frame, "take", 802f, 1420f, 161f, 193f), EtsGameUi.Text("@@take_job@@"), TakeSelected);

            _shownOffers = -1;
            _selectedOffer = -1;
            RefreshMarket();
            StartCoroutine(MarketWatch());
            EtsGameUi.Note("job market shown with " + (Jobs != null ? Jobs.Offers.Count : 0) + " offers" + (Jobs != null && !Jobs.MapIndexed ? " (map still indexing)" : ""));
        }

        private IEnumerator MarketWatch()
        {
            while (_stage == Stage.JobMarket && _market != null)
            {
                if (Jobs != null && Jobs.MapIndexed && !Jobs.HasJob && Jobs.Offers.Count == 0 && Time.unscaledTime > _nextOfferTry)
                {
                    _nextOfferTry = Time.unscaledTime + 2f;
                    Jobs.MakeOffers();
                }

                RefreshMarket();
                yield return new WaitForSeconds(0.5f);
            }
        }

        private void RefreshMarket()
        {
            if (Jobs == null || _marketFrame == null)
            {
                return;
            }

            int count = Jobs.Offers.Count;

            if (_marketEmpty != null)
            {
                _marketEmpty.text = count > 0 ? "" : Jobs.MapIndexed ? EtsGameUi.Text("@@no_job_found@@") + " yet - looking ..." : "Reading the map for depots and roads ...";
            }

            if (count == _shownOffers)
            {
                return;
            }

            _shownOffers = count;

            foreach (GameObject row in _offerRows)
            {
                Destroy(row);
            }

            _offerRows.Clear();
            float top = 806f;

            for (int i = 0; i < count; i++)
            {
                int index = i;
                EtsJobs.Offer offer = Jobs.Offers[i];
                RectTransform row = EtsGameUi.Place(_marketFrame, "offer " + i, 28f, 780f, top - 60f, top);
                RawImage plate = EtsGameUi.Painted(row, EtsGameUi.Painter.Strip("/material/ui/button/btn_dark.mat", 752, 60, 0xFFFFFFFF));
                plate.raycastTarget = true;
                RectTransform litRect = EtsGameUi.Place(row, "lit", 0f, 752f, 0f, 60f);
                RawImage lit = EtsGameUi.Painted(litRect, EtsGameUi.Painter.Strip("/material/ui/button/btn_normal_focus.mat", 752, 60, 0xFFFFFFFF));
                lit.color = new Color(1f, 1f, 1f, 0f);

                Logo(row, offer.From.Name, 12f, 140f, 14f, 46f);
                EtsGameUi.Label(EtsGameUi.Place(row, "arrow", 142f, 162f, 0f, 60f), ">", EtsFont.Big, TextAlignmentOptions.Center, EtsGameUi.SelectedColour);
                Logo(row, offer.To.Name, 164f, 292f, 14f, 46f);
                EtsGameUi.Label(EtsGameUi.Place(row, "cargo", 302f, 532f, 0f, 60f), offer.Units + " x " + offer.CargoName + "\n" + offer.ToCityName, EtsFont.Normal, TextAlignmentOptions.Left, EtsGameUi.TextColour);
                EtsGameUi.Label(EtsGameUi.Place(row, "km", 532f, 632f, 0f, 60f), Mathf.RoundToInt(offer.ShownKm) + " km", EtsFont.Normal, TextAlignmentOptions.Right, EtsGameUi.TextColour);
                EtsGameUi.Label(EtsGameUi.Place(row, "reward", 632f, 742f, 0f, 60f), offer.Reward.ToString("N0") + " €", EtsFont.Normal, TextAlignmentOptions.Right, EtsGameUi.SelectedColour);
                PlateButton(row, lit, () => SelectOffer(index));
                _offerRows.Add(row.gameObject);
                top -= 64f;
            }

            if (count > 0 && (_selectedOffer < 0 || _selectedOffer >= count))
            {
                SelectOffer(0);
            }
            else if (count == 0)
            {
                ShowDetails(null);
            }
        }

        private void SelectOffer(int index)
        {
            _selectedOffer = index;
            EtsJobs.Offer offer = index >= 0 && index < Jobs.Offers.Count ? Jobs.Offers[index] : null;
            ShowDetails(offer);

            if (offer != null)
            {
                EtsGameUi.Note("offer " + (index + 1) + " selected: " + offer.Line);
            }
        }

        private void ShowDetails(EtsJobs.Offer offer)
        {
            if (_marketDetails == null)
            {
                return;
            }

            for (int i = _marketDetails.childCount - 1; i >= 0; i--)
            {
                Destroy(_marketDetails.GetChild(i).gameObject);
            }

            if (offer == null)
            {
                return;
            }

            float w = 1420f - 802f, h = 809f - 239f;
            Logo(_marketDetails, offer.From.Name, 40f, 296f, h - 100f, h - 36f);
            EtsGameUi.Label(EtsGameUi.Place(_marketDetails, "to", 296f, 336f, h - 100f, h - 36f), ">", EtsFont.Title, TextAlignmentOptions.Center, EtsGameUi.SelectedColour);
            Logo(_marketDetails, offer.To.Name, 336f, 592f, h - 100f, h - 36f);

            string[] labels = { "Cargo", "Weight", "From", "To", "Distance", "Reward" };
            string[] values =
            {
                offer.Units + " x " + offer.CargoName,
                Mathf.RoundToInt(offer.CargoMass / 1000f) + " t",
                offer.FromCompanyName + ", " + offer.FromCityName,
                offer.ToCompanyName + ", " + offer.ToCityName,
                offer.ShownKm.ToString("0.0") + " km",
                offer.Reward.ToString("N0") + " €"
            };

            float top = h - 130f;

            for (int i = 0; i < labels.Length; i++)
            {
                EtsGameUi.Label(EtsGameUi.Place(_marketDetails, "label " + i, 40f, 220f, top - 26f, top), labels[i], EtsFont.Normal, TextAlignmentOptions.Left, new Color(0.62f, 0.63f, 0.65f));
                EtsGameUi.Label(EtsGameUi.Place(_marketDetails, "value " + i, 220f, w - 40f, top - 26f, top), values[i], EtsFont.Normal, TextAlignmentOptions.Right, i == labels.Length - 1 ? EtsGameUi.SelectedColour : EtsGameUi.TextColour);
                top -= 30f;
            }

            EtsGameUi.Label(EtsGameUi.Place(_marketDetails, "note", 40f, w - 40f, 30f, 90f),
                "The trailer is loaded and coupled at " + offer.FromCompanyName + ". Follow the route advisor to " + offer.ToCompanyName + " and stop in the marked bay.",
                EtsFont.Small, TextAlignmentOptions.TopLeft, new Color(0.62f, 0.63f, 0.65f));
        }

        /// <summary>A company's logo from /material/ui/company/small, or its name when there is none.</summary>
        private static void Logo(RectTransform parent, string company, float left, float right, float bottom, float top)
        {
            RectTransform rect = EtsGameUi.Place(parent, "logo " + company, left, right, bottom, top);
            EtsBitmap logo = EtsGameUi.Painter.Texture("/material/ui/company/small/" + company + ".mat");

            if (logo != null)
            {
                EtsGameUi.Painted(rect, EtsGameUi.Painter.Whole("/material/ui/company/small/" + company + ".mat", EtsGameUi.PixelWidth(rect), EtsGameUi.PixelHeight(rect), 0xFFFFFFFF));
            }
            else
            {
                EtsGameUi.Label(rect, company, EtsFont.Normal, TextAlignmentOptions.Center, EtsGameUi.TextColour);
            }
        }

        private void TakeSelected()
        {
            if (Jobs == null || _selectedOffer < 0 || _selectedOffer >= Jobs.Offers.Count)
            {
                EtsGameUi.Note("Take job pressed with nothing selected");
                return;
            }

            EtsGameUi.Note("taking offer " + (_selectedOffer + 1) + ": " + Jobs.Offers[_selectedOffer].Line);
            Jobs.Take(_selectedOffer);
        }

        // ------------------------------------------------------------------ the job

        private void OnJobStarting(EtsJobs.Offer offer)
        {
            EtsGameUi.Note("job starting: loading screen while the truck is moved to " + offer.FromCompanyName + ", " + offer.FromCityName + " and the trailer coupled");
            Clear(ref _market);
            Clear(ref _desktop);
            Clear(ref _delivery);
            _stage = Stage.JobLoading;
            GarageOn(false);
            HoldTheDrive();
            ShowLoading(offer);
            StartCoroutine(FakeProgress());
        }

        private IEnumerator FakeProgress()
        {
            float t = 0f;

            while (_stage == Stage.JobLoading && _loading != null)
            {
                t += Time.deltaTime;
                SetProgress(1f - Mathf.Exp(-t / 6f));
                yield return null;
            }
        }

        private void OnJobStarted(EtsJobs.Offer offer)
        {
            SetProgress(1f);
            StartCoroutine(StartDrivingSoon());
        }

        /// <summary>
        /// The depot never built. Off the loading screen and back to driving, so the menu (take it
        /// again), R and the truck's door all work; without this the loading screen stayed for good.
        /// </summary>
        private void OnJobFailed(EtsJobs.Offer offer)
        {
            EtsGameUi.Note("job to " + offer.ToCityName + " could not start: " + offer.FromCompanyName + "'s yard never built - back to driving");
            Clear(ref _loading);
            StartDriving();
        }

        private IEnumerator StartDrivingSoon()
        {
            yield return new WaitForSeconds(0.6f);
            Clear(ref _loading);
            StartDriving();
        }

        private void StartDriving()
        {
            Clear(ref _desktop);
            Clear(ref _market);
            Clear(ref _launchpad);
            _stage = Stage.Driving;
            GarageOn(false);

            if (Menu != null)
            {
                Menu.AllowEscape = true;
                Menu.Close();
            }

            // A Somnium player on foot gets the keys when they get in (they click the truck,
            // EtsTruckDoor); in the editor, with nobody to walk, the truck has them at once.
            bool onFoot = Menu != null && Menu.OnFoot;

            if (Truck != null)
            {
                Truck.InputEnabled = !onFoot;
            }

            if (Advisor != null)
            {
                Advisor.Visible = true;
            }

            Debug.Log("[SomniumEuroTruck] Driving. F1 job page, F2 map zoom, Escape menu." + (onFoot ? " The player is on foot: " + (EtsVr.Present ? "point at the truck and pull the trigger" : "click the truck") + " to get in." : ""));
        }

        private void OnDelivered(EtsJobs.DeliveryStats stats)
        {
            EtsGameUi.Note("delivered " + stats.Job.CargoName + " to " + stats.Job.ToCompanyName + ": " + stats.Job.ToGameKm(stats.DistanceMetres).ToString("0.0") + " km in " + Mathf.RoundToInt(stats.Seconds / 60f) + " min, paid " + stats.Reward.ToString("N0") + " EUR - delivery summary shown");
            _stage = Stage.Delivery;
            HoldTheDrive();
            ShowDeliveryScreen(stats);
        }

        /// <summary>
        /// /ui/delivery.sii with the run's own numbers in it: what was delivered where, the
        /// distance, the time, the fuel, and the money.
        /// </summary>
        private void ShowDeliveryScreen(EtsJobs.DeliveryStats stats)
        {
            EtsJobs.Offer job = stats.Job;
            int minutes = Mathf.RoundToInt(stats.Seconds / 60f);
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "job.description", job.CargoName + " delivered from " + job.FromCityName + " to " + job.ToCityName + "." },
                { "driven.distance.val", job.ToGameKm(stats.DistanceMetres).ToString("0.0") + " km" },
                { "time.taken.val", (minutes / 60) + " h. " + (minutes % 60) + " min." },
                { "fuel.consumed.val", Mathf.RoundToInt(stats.FuelLitres) + " l" },
                { "level.low", EtsGameUi.Text("@@profile_level@@") + " 1" },
                { "level.high", EtsGameUi.Text("@@profile_level@@") + " 2" },
                { "current.xp", "Experience: " + Mathf.RoundToInt(stats.DistanceMetres / 100f).ToString("N0") + " / 1,000" },
                { "current.level", EtsGameUi.Text("@@profile_level@@") + " 1 - Rookie" }
            };

            _delivery = EtsGameUi.BuildScreen("/ui/delivery.sii", 460, new Dictionary<string, Action>(), overrides, out string report, out _);

            if (_delivery == null)
            {
                Debug.Log("[SomniumEuroTruck] Delivery screen: " + report);
                AfterDelivery();
                return;
            }

            _delivery.transform.SetParent(transform, false);
            Transform frame = _delivery.transform.GetChild(0);
            EtsGameUi.Label(EtsGameUi.Place(frame, "paid", 460f, 980f, 214f, 250f), EtsGameUi.Text("@@total_profit@@") + ":  " + stats.Reward.ToString("N0") + " €", EtsFont.Big, TextAlignmentOptions.Center, EtsGameUi.SelectedColour);
            EtsGameUi.MakeButton(EtsGameUi.Place(frame, "continue", 560f, 880f, 172f, 206f), "Continue", AfterDelivery);
            Debug.Log("[SomniumEuroTruck] Delivery summary from the game's own layout - " + report + ".");
        }

        private void AfterDelivery()
        {
            Clear(ref _delivery);

            if (Jobs != null && Jobs.DemoRun)
            {
                _stage = Stage.DemoEnd;
                EtsGameUi.Note("delivery summary closed - the demo's end screen");
                EtsDemoEndScreen.Show(Jobs, StartDriving);
                return;
            }

            EtsGameUi.Note("delivery summary closed - back to the desktop");
            ShowDesktop();
        }

        private void Clear(ref GameObject screen)
        {
            if (screen != null)
            {
                Destroy(screen);
                screen = null;
            }
        }

        // ------------------------------------------------------------------ the pictures

        private struct Picture
        {
            public string Name;
            public string MaterialPath;
            public string Location;
            public string Collection;
        }

        /// <summary>
        /// One of the game's own loading photographs, with the place it was taken. Each picture has
        /// a .sii beside it naming a location key and, when it belongs to one, a DLC.
        /// </summary>
        private Picture ChoosePicture()
        {
            var fallback = new Picture
            {
                Name = "none",
                MaterialPath = "/material/ui/loading/bar_pics/pic01.mat",
                Location = "",
                Collection = ""
            };

            if (EtsEngine.Files == null || !EtsEngine.Files.TryList("/material/ui/loading/bar_pics", out List<string> files, out _))
            {
                return fallback;
            }

            var candidates = new List<string>();

            foreach (string file in files)
            {
                if (file.EndsWith(".sii", StringComparison.Ordinal))
                {
                    candidates.Add(file.Substring(0, file.Length - 4));
                }
            }

            if (candidates.Count == 0)
            {
                return fallback;
            }

            for (int attempt = 0; attempt < 12; attempt++)
            {
                string name = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                EtsSiiFile description = EtsEngine.Files.ReadSii("/material/ui/loading/bar_pics/" + name + ".sii");

                if (description == null || description.Units.Count == 0)
                {
                    continue;
                }

                EtsSiiUnit unit = description.Units[0];
                string dlc = unit.GetString("dlc_id", "") ?? "";

                if (BaseGamePicturesOnly && dlc.Length > 0 && attempt < 8)
                {
                    continue;
                }

                return new Picture
                {
                    Name = name,
                    MaterialPath = "/material/ui/loading/bar_pics/" + name + ".mat",
                    Location = EtsGameUi.Text(unit.GetString("location", "") ?? ""),
                    Collection = dlc.Length > 0 ? EtsGameUi.Text(dlc.Replace("eut2_", "")) + " DLC" : "Euro Truck Simulator 2"
                };
            }

            return fallback;
        }
    }
}
