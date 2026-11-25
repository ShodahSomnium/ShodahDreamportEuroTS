using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The world's menu, built at runtime from Unity UI so nothing but code ships: the title, the
    /// job board with the current offers, and the settings that matter while driving. Escape opens
    /// and closes it; while it is open the truck ignores the keys and the cursor is free.
    ///
    /// The integrated plan (owner E; PLAN.md §6, §7.E, [tablet §4.9]) moves every screen of it into the "ETS 2"
    /// tablet app, and keeps here what is not UI: the on-foot rule, getting in and out, and which truck the local
    /// player is at. All of that is behind EtsFeatures.NewFlow; with the flag off (every build through Phase 1)
    /// this class does exactly what it did before, line for line:
    ///   - new flow: no canvas of its own - Open and Close are the app's (the tablet, or the app's own panel when
    ///     there is no tablet); Escape and left X open that panel only while no tablet is linked, and do nothing of
    ///     the world's once the app is on the client's tablet (Escape is the client's Exit, left X its Inspector,
    ///     left Y a tap of its tablet: EtsTabletLayout.MenuKey / SeatKey, Q-E4); the truck it drives follows
    ///     EtsLocalTruck (Occupied, else Own); only a driver (seat 0) ever gets the keys; EnterTruck(rig, seat)
    ///     seats the player in any seat of any rig.
    ///   - IsOpen is also true while the app is on screen, so the door (EtsTruckDoor.Usable) and the front end see
    ///     the tablet app as a menu. The app exists only in the new flow (or the editor preview), so today it is
    ///     the old menu alone.
    /// </summary>
    public class EtsMenu : MonoBehaviour
    {
        public EtsTruckController Truck;
        public EtsTruckCamera Camera;
        public EtsTruckAudio Audio;
        public EtsJobs Jobs;
        public EtsTruckSeat Seat;

        /// <summary>
        /// Whether the Somnium player is walking rather than in the driver's seat. Never true in
        /// the editor, which has no player: there the truck is driven from the start as before.
        /// </summary>
        public bool OnFoot => Seat != null && Seat.HasPlayer && !Seat.Seated;

        /// <summary>
        /// The local player drives the truck: seated (in the new flow, in seat 0 - a passenger never gets the keys,
        /// PLAN §6), or there is no Somnium player at all (the editor, where the truck is driven from the start).
        /// </summary>
        public bool Driving => !OnFoot && (!EtsFeatures.NewFlow || Seat == null || !Seat.HasPlayer || Seat.SeatIndex == 0);

        public bool OpenAtStart = true;

        /// <summary>Whether Escape opens the menu. Off while the front end's own screens are up.</summary>
        public bool AllowEscape = true;

        private GameObject _canvasObject;
        private GameObject _panel;
        private TMP_FontAsset _font;
        private TMP_Text _cameraLabel;
        private TMP_Text _soundLabel;
        private TMP_Text _jobLabel;
        private readonly GameObject[] _offerButtons = new GameObject[3];
        private readonly TMP_Text[] _offerLabels = new TMP_Text[3];
        private TMP_Text _offersButtonLabel;
        private TMP_Text _noOffers;
        private float _nextOfferTry;
        private bool _open;
        private bool _truckInputWasEnabled = true;
        private CursorLockMode _cursorLockWas;
        private bool _cursorVisibleWas;
        private EtsVrPanel _vrPanel;
        private TMP_Text _seatLabel;

        /// <summary>The old menu is open, or (new flow, editor preview) the tablet app is on screen (PLAN §7.E).</summary>
        public bool IsOpen => _open || EtsTabletApp.IsVisible;

        private bool _followsLocalTruck;

        private void Start()
        {
            if (EtsFeatures.NewFlow)
            {
                // No canvas of its own: the tablet app (or its own panel) holds every screen. The editor still needs an
                // EventSystem for that panel; the client brings its own.
                EnsureEventSystem();

                // The bootstrap puts the seat, the camera, the sound and the jobs on this same object before the menu
                // (EtsNet.Awake: EtsJobs/EtsTruckSeat/EtsTruckCamera/EtsTruckAudio.Ensure, PLAN §3.2), and in the new flow
                // nothing else wires them here (EtsModelViewer.cs:319-348 does it for the old flow's one truck). Without
                // the seat, EnterTruck(rig, seat) - the door's way in - would return at once and GetOut do nothing.
                if (Seat == null) Seat = GetComponent<EtsTruckSeat>();
                if (Camera == null) Camera = GetComponent<EtsTruckCamera>();
                if (Audio == null) Audio = GetComponent<EtsTruckAudio>();
                if (Jobs == null) Jobs = EtsJobs.Instance != null ? EtsJobs.Instance : GetComponent<EtsJobs>();
                EtsLocalTruck.OwnChanged += OnLocalTruckChanged;
                EtsLocalTruck.OccupiedChanged += OnLocalTruckChanged;
                _followsLocalTruck = true;
                OnLocalTruckChanged(null);
                return;
            }

            Build();

            if (OpenAtStart)
            {
                Open();
            }
            else
            {
                Close();
            }
        }

        private void OnDestroy()
        {
            if (_followsLocalTruck)
            {
                EtsLocalTruck.OwnChanged -= OnLocalTruckChanged;
                EtsLocalTruck.OccupiedChanged -= OnLocalTruckChanged;
            }
        }

        /// <summary>New flow: the truck is the one the player sits in, else their own (so on foot its keys stay off).</summary>
        private void OnLocalTruckChanged(EtsTruckRig changed)
        {
            EtsTruckRig rig = EtsLocalTruck.Occupied != null ? EtsLocalTruck.Occupied : EtsLocalTruck.Own;

            if (rig != null && rig.Controller != null)
            {
                Truck = rig.Controller;
            }
            else if (EtsLocalTruck.Own == null && EtsLocalTruck.Occupied == null && changed != null)
            {
                Truck = null; // the own truck was despawned
            }
        }

        private void Update()
        {
            // Today (NewFlow off) MenuKey is always OldMenu and SeatKey GetOut: the lines below are the old ones.
            bool newFlow = EtsFeatures.NewFlow;
            bool linked = EtsTabletLink.Instance != null && EtsTabletLink.Instance.IsLinked;

            if (AllowEscape && Input.GetKeyDown(KeyCode.Escape))
            {
                UseMenuKey(EtsTabletLayout.MenuKey(newFlow, linked, true), "Escape");
            }

            // In a headset: left X is Escape, left Y gets out of the truck (EtsVr). Getting in is
            // pointing at the truck (EtsTruckDoor).
            if (AllowEscape && EtsVr.MenuPressed)
            {
                UseMenuKey(EtsTabletLayout.MenuKey(newFlow, linked, true), "left X");
            }

            if (AllowEscape && EtsVr.SeatPressed && !OnFoot && EtsTabletLayout.SeatKey(newFlow, linked, true, true) == EtsTabletLayout.KeyUse.GetOut)
            {
                GetOut();
            }

            // On foot, the keys walk the avatar and the view is the client's, whatever else has
            // happened (the front end handing over, the menu closing, a key): checked every frame,
            // so no path can leave a walking player steering the truck. In the new flow the same
            // holds for a passenger (seated, not in seat 0: PLAN §6 "InputEnabled for drivers only");
            // with the flag off Driving is simply "seated" here, so this is the old test.
            if (Seat != null && Seat.HasPlayer)
            {
                bool seated = Seat.Seated;

                if (Camera != null && Camera.Active != seated)
                {
                    Camera.SetActive(seated);
                }

                if ((!seated || !Driving) && Truck != null && Truck.InputEnabled)
                {
                    Truck.InputEnabled = false;
                }
            }

            if (_open)
            {
                RefreshJobs();
            }
        }

        /// <summary>Escape / left X: the old menu (today, word for word), the app's own panel (new flow, no tablet), or nothing.</summary>
        private void UseMenuKey(EtsTabletLayout.KeyUse use, string key)
        {
            if (use == EtsTabletLayout.KeyUse.OldMenu)
            {
                if (key == "Escape")
                {
                    EtsGameUi.Note(_open ? "Escape: menu closed, driving" : "Escape: menu opened");
                }
                else
                {
                    EtsGameUi.Note(_open ? "left X: menu closed" : "left X: menu opened");
                }

                if (_open) Close(); else Open();
            }
            else if (use == EtsTabletLayout.KeyUse.AppPanel)
            {
                EtsTabletApp app = EtsTabletApp.Current;

                if (app != null)
                {
                    app.Toggle(key);
                }
            }
        }

        public void Open()
        {
            if (EtsFeatures.NewFlow)
            {
                EtsTabletApp app = EtsTabletApp.Current;

                if (app != null)
                {
                    app.Open();
                }

                return;
            }

            if (_panel == null) return;
            _open = true;
            _panel.SetActive(true);

            if (_vrPanel != null)
            {
                _vrPanel.Face();
            }

            if (Truck != null)
            {
                _truckInputWasEnabled = Truck.InputEnabled;
                Truck.InputEnabled = false;
            }

            _cursorLockWas = Cursor.lockState;
            _cursorVisibleWas = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Refresh();
        }

        public void Close()
        {
            if (EtsFeatures.NewFlow)
            {
                // The app gives the truck its keys back itself (EtsTabletApp's input hold).
                EtsTabletApp app = EtsTabletApp.Current;

                if (app != null)
                {
                    app.Close();
                }

                return;
            }

            if (_panel == null) return;
            _open = false;
            _panel.SetActive(false);

            if (Truck != null)
            {
                Truck.InputEnabled = !OnFoot && (_truckInputWasEnabled || Camera == null || Camera.Active);
            }

            Cursor.lockState = _cursorLockWas;
            Cursor.visible = _cursorVisibleWas;
        }

        /// <summary>
        /// Out of the truck onto the ground beside the door. The client has the player and the
        /// view again, and the truck is parked (EtsTruckController.SetDriven) - frozen, braked,
        /// solid. Getting back in is clicking the truck (EtsTruckDoor -> EnterTruck).
        /// </summary>
        public void GetOut()
        {
            if (Seat == null || OnFoot)
            {
                return;
            }

            // A passenger above 10 km/h (Q-B2) is refused by the seat (EtsTruckSeat.CanGetOut, owner B): say why, rather than
            // the "no Somnium player" note below, which only the editor earns.
            if (Seat.Seated && !Seat.CanGetOut(out string why))
            {
                EtsGameUi.Note("get out refused: " + why);
                EtsTabletApp app = EtsTabletApp.Current;

                if (app != null)
                {
                    app.Toast(why);
                }
                else if (Jobs != null)
                {
                    Jobs.Tell(why);
                }

                return;
            }

            if (!Seat.GetOut())
            {
                EtsGameUi.Note("get out: there is no Somnium player to let out (editor run)");
                return;
            }

            _truckInputWasEnabled = false;

            if (Truck != null)
            {
                Truck.InputEnabled = false;
            }

            if (Camera != null)
            {
                Camera.SetActive(false);
            }

            EtsGameUi.Note("got out of the truck to walk");
            Refresh();
        }

        /// <summary>
        /// Into any seat of any rig: seat 0 of the own truck, or a granted passenger seat of a puppet (PLAN.md §5.5
        /// "Passenger", §7.E). The seat does the sitting (EtsTruckSeat.GetIn(rig, seat), owner B: pose, capsule,
        /// EtsLocalTruck.SetOccupied); here the camera goes to that seat's eye and only a driver gets the keys.
        /// Today's truck has no rig: a null rig and seat 0 is today's EnterTruck().
        /// </summary>
        public void EnterTruck(EtsTruckRig rig, int seat)
        {
            if (rig == null)
            {
                if (seat == 0)
                {
                    EnterTruck();
                }

                return;
            }

            if (Seat == null || !OnFoot)
            {
                return;
            }

            Seat.GetIn(rig, seat);

            if (!Seat.Seated)
            {
                EtsGameUi.Note("get in: the Bridge gave no player to seat");
                return;
            }

            if (rig.Controller != null)
            {
                Truck = rig.Controller;
            }

            _truckInputWasEnabled = seat == 0;

            if (Camera != null)
            {
                Camera.Bind(rig, seat);
                Camera.SetActive(true);
            }

            if (Truck != null)
            {
                Truck.InputEnabled = seat == 0 && !IsOpen && AllowEscape;
            }

            EtsGameUi.Note(seat == 0 ? "got in the truck" : "got in as a passenger (seat " + seat + ")");
            Refresh();
        }

        /// <summary>Gets or adds the menu on the bootstrap object (EtsNet.Awake, new flow only; PLAN §3.2).</summary>
        public static EtsMenu Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsMenu menu = host.GetComponent<EtsMenu>();
            return menu != null ? menu : host.AddComponent<EtsMenu>();
        }

        /// <summary>An EventSystem for the editor, where there is no client to bring one (EtsFrontEnd and the old menu do the same).</summary>
        internal static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var events = new GameObject("Ets Event System");
                events.AddComponent<EventSystem>();
                events.AddComponent<StandaloneInputModule>();
            }
        }

        /// <summary>Into the driver's seat, from the truck being clicked (EtsTruckDoor).</summary>
        public void EnterTruck()
        {
            if (Seat == null || !OnFoot)
            {
                return;
            }

            Seat.GetIn();

            if (!Seat.Seated)
            {
                EtsGameUi.Note("get in: the Bridge gave no player to seat");
                return;
            }

            _truckInputWasEnabled = true;

            if (Camera != null)
            {
                Camera.SetActive(true);
            }

            if (Truck != null && !IsOpen && AllowEscape)
            {
                Truck.InputEnabled = true;
            }

            EtsGameUi.Note("got in the truck");
            Refresh();
        }

        private void Refresh()
        {
            if (_cameraLabel != null) _cameraLabel.text = "Camera: " + (Camera != null && Camera.View == EtsTruckCamera.Mode.Interior ? "inside the cab" : "behind the truck");
            if (_soundLabel != null) _soundLabel.text = "Sound: " + (Audio != null && Audio.Volume > 0.01f ? "on" : "off");
            if (_seatLabel != null) _seatLabel.text = OnFoot ? (EtsVr.Present ? "Point at the truck and pull the trigger to get in" : "Click the truck to get in") : "Get out and walk";
            RefreshJobs();
        }

        private void RefreshJobs()
        {
            if (Jobs == null || _jobLabel == null) return;
            _jobLabel.text = Jobs.Describe();

            // The board fills itself. MakeOffers runs once when the map finishes indexing, and if
            // the truck was not on the ground yet at that moment it returns with nothing and the
            // player is left looking at an empty board wondering what to press. Asking again every
            // couple of seconds while the board is open and empty costs nothing and means the jobs
            // simply turn up.
            if (Jobs.MapIndexed && !Jobs.HasJob && Jobs.Offers.Count == 0 && Time.unscaledTime > _nextOfferTry)
            {
                _nextOfferTry = Time.unscaledTime + 2f;
                Jobs.MakeOffers();
            }

            int shown = 0;

            for (int i = 0; i < _offerButtons.Length; i++)
            {
                bool show = !Jobs.HasJob && i < Jobs.Offers.Count;
                if (_offerButtons[i].activeSelf != show) _offerButtons[i].SetActive(show);
                if (show) { _offerLabels[i].text = "Take this load  -  " + Jobs.Offers[i].Line; shown++; }
            }

            if (_noOffers != null)
            {
                // Something is always said here, because an empty board with no explanation is the
                // one state that reads as broken. The map index takes a few seconds at the start.
                _noOffers.text = shown > 0 ? "" :
                    Jobs.HasJob ? "You are carrying a load. Follow the route on the GPS map." :
                    Jobs.MapIndexed ? "No loads here at the moment - press \"Look for other jobs\"." :
                    "Reading the map for depots and roads. The loads appear here in a moment ...";
            }

            if (_offersButtonLabel != null)
            {
                _offersButtonLabel.text = Jobs.HasJob ? "Abandon this job" : Jobs.MapIndexed ? "Look for other jobs" : "Reading the map ...";
            }
        }

        private void Build()
        {
            _font = Truck != null && Truck.Hud != null ? Truck.Hud.font : null;

            if (_font == null)
            {
                TMP_Text any = FindFirstObjectByType<TMP_Text>();
                _font = any != null ? any.font : TMP_Settings.defaultFontAsset;
            }

            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var events = new GameObject("Ets Event System");
                events.AddComponent<EventSystem>();
                events.AddComponent<StandaloneInputModule>();
            }

            _canvasObject = new GameObject("Ets Menu");
            var canvas = _canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = _canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;

            // The game lays its screens out in a 1440 x 900 frame - /def/ui.sii says so - and its
            // panels are dark plates with an amber title. Match that rather than a frame of my own.
            scaler.referenceResolution = new Vector2(EtsGameUi.ReferenceWidth, EtsGameUi.ReferenceHeight);
            _canvasObject.AddComponent<GraphicRaycaster>();
            _vrPanel = EtsVrPanel.Adopt(canvas, true);

            _panel = new GameObject("Panel", typeof(RectTransform));
            _panel.transform.SetParent(_canvasObject.transform, false);

            // A dimming sheet across the whole screen behind the panel, so the road behind does not
            // read through the text. It takes the pointer too, which stops a click that misses the
            // panel from falling through to the world.
            var shadeObject = new GameObject("Shade");
            shadeObject.transform.SetParent(_panel.transform, false);
            var shade = shadeObject.AddComponent<Image>();
            shade.color = new Color(0f, 0f, 0f, 0.72f);
            var shadeRect = shade.rectTransform;
            shadeRect.anchorMin = Vector2.zero;
            shadeRect.anchorMax = Vector2.one;
            shadeRect.offsetMin = new Vector2(-4000f, -4000f);
            shadeRect.offsetMax = new Vector2(4000f, 4000f);

            var plateObject = new GameObject("Plate");
            plateObject.transform.SetParent(_panel.transform, false);
            var panelImage = plateObject.AddComponent<Image>();
            panelImage.color = new Color(EtsGameUi.PanelColour.r, EtsGameUi.PanelColour.g, EtsGameUi.PanelColour.b, 0.98f);
            var plateRect = panelImage.rectTransform;
            plateRect.anchorMin = Vector2.zero;
            plateRect.anchorMax = Vector2.one;
            plateRect.offsetMin = Vector2.zero;
            plateRect.offsetMax = Vector2.zero;

            var panelRect = _panel.GetComponent<RectTransform>();
            panelRect.sizeDelta = new Vector2(880f, 840f);
            panelRect.anchoredPosition = Vector2.zero;

            float y = 370f;

            // The game's own logo, from /material/ui/logo/ets2_logo.mat in the player's archives.
            Texture2D logo = EtsGameUi.Material("/material/ui/logo/ets2_logo.mat");

            if (logo != null)
            {
                var logoRect = new GameObject("logo", typeof(RectTransform)).GetComponent<RectTransform>();
                logoRect.SetParent(_panel.transform, false);
                logoRect.sizeDelta = new Vector2(420f, 76f);
                logoRect.anchoredPosition = new Vector2(0f, y);
                EtsGameUi.Picture(logoRect, logo, true);
            }
            else
            {
                MakeText(_panel.transform, "Euro Truck Simulator 2", 38, y, 840f, 56f, FontStyles.Bold);
            }

            y -= 62f;
            MakeText(_panel.transform, "Somnium Dreamport - built from your own installation.", 18, y, 840f, 28f, FontStyles.Normal);
            y -= 58f;

            _jobLabel = MakeText(_panel.transform, "", 19, y, 840f, 70f, FontStyles.Normal);
            _jobLabel.alignment = TextAlignmentOptions.TopLeft;
            y -= 82f;

            var market = MakeText(_panel.transform, "JOB MARKET", 21, y, 840f, 34f, FontStyles.Bold); market.alignment = TextAlignmentOptions.Left; market.color = new Color(1f, 0.78f, 0.24f);
            y -= 40f;

            for (int i = 0; i < _offerButtons.Length; i++)
            {
                int index = i;
                _offerButtons[i] = MakeButton(_panel.transform, "", y, 840f, () => { if (Jobs != null) { Close(); Jobs.Take(index); } }, true, out _offerLabels[i]);
                y -= 52f;
            }

            _noOffers = MakeText(_panel.transform, "", 18, y + 104f, 840f, 60f, FontStyles.Italic);
            _noOffers.color = new Color(0.72f, 0.74f, 0.77f);

            MakeButton(_panel.transform, "Look for other jobs", y, 840f, () => { if (Jobs != null) { if (Jobs.HasJob) Jobs.Abandon(); else Jobs.MakeOffers(); } Refresh(); }, out _offersButtonLabel);
            y -= 66f;

            MakeButton(_panel.transform, "Drive", y, 410f, () => Close(), out _);
            y -= 52f;
            MakeButton(_panel.transform, "Camera", y, 410f, () => { if (Camera != null) Camera.View = Camera.View == EtsTruckCamera.Mode.Interior ? EtsTruckCamera.Mode.Chase : EtsTruckCamera.Mode.Interior; Refresh(); }, out _cameraLabel);
            y -= 52f;
            MakeButton(_panel.transform, "Sound", y, 410f, () => { if (Audio != null) Audio.Volume = Audio.Volume > 0.01f ? 0f : 0.5f; Refresh(); }, out _soundLabel);
            y -= 52f;
            MakeButton(_panel.transform, "Respawn the truck", y, 410f, () => { if (Truck != null) Truck.Respawn(); Close(); }, out _);
            y -= 52f;
            MakeButton(_panel.transform, "Get out and walk", y, 410f, () => { if (!OnFoot) { GetOut(); Close(); } }, out _seatLabel);
            y -= 62f;

            MakeText(_panel.transform, EtsVr.Present ? EtsVr.Help :
                "W/S drive (S reverses from a stop)   A/D steer   Space brake   P parking brake   H horn\n1/2 camera   C cruise control   T trailer   [ ] indicators\nR respawn   Esc menu   On foot: click the truck to get in", 16, y, 840f, 56f, FontStyles.Normal);
        }

        private TMP_Text MakeText(Transform parent, string text, float size, float y, float width, float height, FontStyles style)
        {
            var textObject = new GameObject("Text");
            textObject.transform.SetParent(parent, false);
            var tmp = textObject.AddComponent<TextMeshProUGUI>();
            if (_font != null) tmp.font = _font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            var rect = tmp.rectTransform;
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(0f, y);
            return tmp;
        }

        private GameObject MakeButton(Transform parent, string label, float y, float width, UnityEngine.Events.UnityAction action, out TMP_Text text)
        {
            return MakeButton(parent, label, y, width, action, false, out text);
        }

        /// <summary>
        /// A button in the game's colours rather than Unity's blue. /ui/template/button.sii plates
        /// btn.normal with /material/ui/button/btn_dark.mat, an 8x32 strip averaging 42,42,42, and
        /// writes the caption over it; the accent is the amber the game titles everything in.
        /// </summary>
        private GameObject MakeButton(Transform parent, string label, float y, float width,
            UnityEngine.Events.UnityAction action, bool accent, out TMP_Text text)
        {
            var buttonObject = new GameObject("Button " + label);
            buttonObject.transform.SetParent(parent, false);
            var image = buttonObject.AddComponent<Image>();
            image.color = accent ? new Color(0.36f, 0.26f, 0.07f, 1f) : new Color(0.165f, 0.165f, 0.168f, 1f);
            var button = buttonObject.AddComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = accent ? new Color(1.7f, 1.7f, 1.7f, 1f) : new Color(1.85f, 1.85f, 1.9f, 1f);
            colors.pressedColor = new Color(0.65f, 0.65f, 0.68f, 1f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            button.onClick.AddListener(action);
            var rect = buttonObject.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, 44f);
            rect.anchoredPosition = new Vector2(0f, y);
            text = MakeText(buttonObject.transform, label, 19, 0f, width - 24f, 40f, FontStyles.Normal);
            text.color = accent ? new Color(1f, 0.84f, 0.45f) : new Color(0.88f, 0.89f, 0.91f);
            text.enableAutoSizing = true;
            text.fontSizeMin = 13f;
            text.fontSizeMax = 19f;
            return buttonObject;
        }
    }
}
