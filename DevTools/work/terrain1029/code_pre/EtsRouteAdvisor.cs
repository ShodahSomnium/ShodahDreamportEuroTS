using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The route advisor: the game's sat nav, the panel in the bottom right of the screen with the
    /// map, the next turn, and the job.
    ///
    /// Laid out from the game's own file, 1.61's /ui/adviser_gps.sii widget 1000 "adviser.widget" (2026-10-14; the
    /// adviser_map.sii the old layout quoted is not in 1.61): the widget 1145..1425 x 15..259 of the 1440 x 900 frame
    /// on txt.window.bcg_rect4 FF1E1E1E, its window kept to the right (/ui/adviser.sii window_keep_aspect_standard:
    /// right), the map (ui_map 1700) 1150..1420 x 42..232, above it the status strip (txt.adv_time 1145..1425 x
    /// 235..259, here the next turn) with the speed box over the map's corner (txt.adv_speed 1150..1190 x 215..255 on
    /// bcg_rect4 9A595959), the limit sign under it (txt.adv_navi_spd 1302: 1154..1186 x 183..215), the zoom key in
    /// the map's bottom right ("F2" txt.normal.right.top 1266..1388 x 46..68 + zoom.mat 22 x 22 at 1390..1412) and
    /// under the map the route strip (1200: txt.normal.center 1150..1420 x 15..42, destination.mat 14 x 18, a 6 px
    /// gap, distance and time). No text is drawn across the map. The F1 job page uses the same panel.
    ///
    /// The map is the world itself seen from above: a camera high over the truck, turned so the
    /// truck's heading is up, drawn into a texture the panel shows. Roads, yards and buildings
    /// read as a map at that scale, and the route line is drawn in the world so it appears on
    /// it for free. F2 cycles the zoom as in the game; F1 turns the page between the map and
    /// the job.
    ///
    /// The new flow (Phase 2, owner D; PLAN.md §3.1 "the driver kit ... rebinds on those events"): one advisor for the
    /// local player, added once by EtsNet.Awake (Ensure) and bound to the truck they sit in
    /// (EtsLocalTruck.OccupiedChanged), shown only while that is their own truck - a passenger's advisor would show a job
    /// that is not theirs. In a headset it moves onto the dashboard of each truck it is bound to, and the dash plate
    /// (EtsVrDash) goes with it; a truck taken away (despawned) takes its panel with it, and the next bind builds
    /// both again. With the flag off it is today's advisor, bound by EtsModelViewer to the one scene truck.
    /// </summary>
    public class EtsRouteAdvisor : MonoBehaviour
    {
        public EtsTruckController Truck;
        public EtsJobs Jobs;

        /// <summary>The new flow's binding: follow EtsLocalTruck.Occupied instead of a truck given once (Ensure sets it).</summary>
        public bool FollowLocalTruck;

        /// <summary>The GPS map's zoom: 0 close, 1 far - the game's two minimap zooms (map_data.sii zoom_uplift[2], [3]); F2 toggles.</summary>
        public int Zoom;

        [Tooltip("Built hidden; the front end shows it once the drive begins.")]
        public bool StartHidden = true;

        // /ui/adviser_gps.sii: the widget (group 1000) and its map (ui_map 1700), 1440 x 900 frame units.
        private const float PanelLeft = 1145f, PanelRight = 1425f, PanelBottom = 15f, PanelTop = 259f;
        private const float MapLeft = 1150f, MapRight = 1420f, MapBottom = 42f, MapTop = 232f;
        // The route strip under the map (1200) and the status strip over it (1101, txt.adv_time).
        private const float StripLeft = 1150f, StripRight = 1420f, StripBottom = 15f, StripTop = 42f;
        private const float StatusBottom = 235f, StatusTop = 259f;
        private const string DestinationIcon = "<img src=/material/ui/adviser/destination.mat width=14 height=18 color=@@clr_txt@@>";

        /// <summary>
        /// Any tablet open on this client (2026-10-14): the world's app (EtsTabletApp) or the Somnium client's own tablet
        /// (EtsTabletLink.TabletOpen, its animator's IsOpen). On a desktop the HUD (this panel, the fines notification)
        /// gives way to it.
        /// </summary>
        public static bool AnyTabletOpen => EtsTabletApp.IsVisible || (EtsTabletLink.Instance != null && EtsTabletLink.Instance.TabletOpen);

        /// <summary>The limit in force (EtsFines sets it; 0 none), drawn as the game's txt.adv_navi_spd sign on the panel.</summary>
        public int SpeedLimit
        {
            get => _limit;
            set
            {
                if (value == _limit) return;
                _limit = value;
                BuildLimitSign();
            }
        }

        private int _limit;
        private RectTransform _limitSign;
        private TextMeshProUGUI _speedLabel;
        private string _speedPrefix = "", _speedSuffix = "";
        private int _shownSpeed = int.MinValue;
        private RectTransform _destinationIcon;
        private RectTransform _routeInfoRect;
        private string _routeInfo;
        private bool _routeInfoIcon;
        private bool _layoutLogged, _tabletHideLogged;

        private GameObject _canvasObject;
        private RectTransform _frame;
        private GameObject _mapPage;
        private GameObject _jobPage;
        // The GPS map (2026-10-05): roads and route drawn by EtsGpsMap into a CPU texture, ~6 times a second.
        private EtsGpsMap _gps;
        private Texture2D _mapTexture;
        private RectTransform _arrow;
        private float _gpsTimer, _collectTimer;
        private int _drawnZoom = -1;
        private bool _gpsLogged;
        private TextMeshProUGUI _routeLine;
        private TextMeshProUGUI _distanceLine;
        private readonly List<TextMeshProUGUI> _jobValues = new List<TextMeshProUGUI>();
        private bool _showJob;
        private float _refresh;
        private bool _onDash;

        /// <summary>Whether the canvas was made as a VR panel (EtsVrPanel.Adopt happens once, at creation). A switch between
        /// desktop and headset rebuilds it (Update): a desktop overlay is not drawn in a headset, and a VR panel left over
        /// on a desktop hung in front of the camera over the view (2026-10-10).</summary>
        private bool _builtForVr;

        /// <summary>Shown or not. A VR panel is only ever shown on the truck's dash: unattached it would hang in front of
        /// the eyes over the whole view.</summary>
        public bool Visible
        {
            get => _canvasObject != null && _canvasObject.activeSelf;
            set { if (_canvasObject != null) _canvasObject.SetActive(value && (!_builtForVr || _onDash)); }
        }

        /// <summary>
        /// The local player's advisor on the bootstrap object, added if missing and set to follow the local truck
        /// (PLAN.md §3.1, §3.2; EtsNet.Awake calls it only in the new flow). Unity's null test, not ??.
        /// </summary>
        public static EtsRouteAdvisor Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsRouteAdvisor advisor = host.GetComponent<EtsRouteAdvisor>();

            if (advisor == null)
            {
                advisor = host.AddComponent<EtsRouteAdvisor>();
            }

            advisor.FollowLocalTruck = true;
            advisor.StartHidden = true;
            return advisor;
        }

        private EtsVrDash _dash;
        private bool _following;

        private void Start()
        {
            Build();

            if (FollowLocalTruck)
            {
                _following = true;
                EtsLocalTruck.OccupiedChanged += Bind;
                EtsLocalTruck.OwnChanged += OnOwnChanged;
                Bind(EtsLocalTruck.Occupied);
            }
        }

        private void OnDestroy()
        {
            if (_following)
            {
                EtsLocalTruck.OccupiedChanged -= Bind;
                EtsLocalTruck.OwnChanged -= OnOwnChanged;
            }

            if (_dash != null)
            {
                Destroy(_dash.gameObject);
            }

            if (_mapTexture != null)
            {
                Destroy(_mapTexture);
            }
        }

        private void OnOwnChanged(EtsTruckRig own)
        {
            Bind(EtsLocalTruck.Occupied);
        }

        /// <summary>
        /// The new flow: the advisor (and in a headset its dashboard panel and the dash plate) on the truck the local
        /// player sits in, or on none. The panel is given back to this object before a truck it hung on can go.
        /// </summary>
        private void Bind(EtsTruckRig rig)
        {
            EtsTruckController truck = rig != null ? rig.Controller : null;
            Jobs = EtsJobs.Instance;

            if (_canvasObject == null)
            {
                // A despawned truck took the headset's panel with it: build it again.
                Build();
            }

            EtsVrPanel vr = _canvasObject != null ? _canvasObject.GetComponent<EtsVrPanel>() : null;

            if (vr != null)
            {
                if (truck != null)
                {
                    _onDash = true;
                    Vector3 eye = truck.DriverEyeLocal;
                    var focus = new Vector2((PanelLeft + PanelRight) * 0.5f, (PanelBottom + PanelTop) * 0.5f);
                    vr.AttachTo(truck.transform, eye + new Vector3(0.42f, -0.34f, 0.62f), eye, focus, 0.28f * EtsGameUi.ReferenceWidth / (PanelRight - PanelLeft));
                }
                else
                {
                    _onDash = false;
                    _canvasObject.transform.SetParent(transform, false);
                    _canvasObject.SetActive(false);
                }
            }

            if (Truck != truck && _dash != null)
            {
                Destroy(_dash.gameObject);
                _dash = null;
            }

            Truck = truck;

            if (truck != null && _dash == null)
            {
                _dash = EtsVrDash.Attach(truck, Jobs, EtsFont.Get(EtsFont.Normal));
            }

            Visible = ShowsFor(rig);
            Debug.Log("[SomniumEuroTruck] Route advisor: " + (rig != null ? "bound to " + (rig == EtsLocalTruck.Own ? "the own truck " : "a ridden truck ") + (rig.Stem ?? "") + (Visible ? ", shown" : ", hidden (not the own truck)") : "no truck, hidden") + ".");
        }

        /// <summary>Shown while the local player sits in their own truck (a passenger's job is not theirs), and not under a job's loading screen.</summary>
        private static bool ShowsFor(EtsTruckRig rig)
        {
            return rig != null && rig.Controller != null && rig == EtsLocalTruck.Own && (EtsFlow.Instance == null || !EtsFlow.Instance.JobLoading);
        }

        private void Build()
        {
            // Built again after a truck took the headset's panel: the old texture goes first.
            if (_mapTexture != null)
            {
                Destroy(_mapTexture);
                _mapTexture = null;
            }

            _jobValues.Clear();

            Canvas canvas = EtsGameUi.CreateCanvas("Ets Route Advisor", 60, false);
            _canvasObject = canvas.gameObject;
            _canvasObject.transform.SetParent(transform, false);
            Object.Destroy(_canvasObject.GetComponent<GraphicRaycaster>());

            // In a headset there is no bottom-right corner of a screen, so the advisor stands on
            // the dashboard instead, right of the wheel where the game's own GPS screen sits,
            // turned to the driver's eye. About 0.28 m across its 350-unit panel.
            EtsVrPanel vr = canvas.GetComponent<EtsVrPanel>();
            _builtForVr = vr != null;
            _onDash = false;

            if (vr != null && Truck != null)
            {
                Vector3 eye = Truck.DriverEyeLocal;
                var focus = new Vector2((PanelLeft + PanelRight) * 0.5f, (PanelBottom + PanelTop) * 0.5f);
                _onDash = true;
                vr.AttachTo(Truck.transform, eye + new Vector3(0.42f, -0.34f, 0.62f), eye, focus, 0.28f * EtsGameUi.ReferenceWidth / (PanelRight - PanelLeft));
            }
            _frame = EtsGameUi.Place(canvas.transform, "frame", 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);

            if (vr == null)
            {
                // The game scales its 1440 x 900 window to fit the screen and keeps the adviser's window to the right
                // (/ui/adviser.sii window_keep_aspect_standard: right): the whole frame fits (Expand, not the 0.5 match
                // that cut 46 units off the top of a 16:9 screen) and hangs from the bottom-right corner.
                CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null) scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
                _frame.anchorMin = _frame.anchorMax = new Vector2(1f, 0f);
                _frame.pivot = new Vector2(1f, 0f);
                _frame.anchoredPosition = Vector2.zero;
            }

            // The widget's plate: adviser_gps.sii 1001, txt.window.bcg_rect4 FF1E1E1E.
            RectTransform panel = EtsGameUi.Template(_frame, "txt.window.bcg_rect4", "FF1E1E1E", PanelLeft, PanelRight, PanelBottom, PanelTop);
            if (panel == null)
            {
                panel = EtsGameUi.Place(_frame, "panel", PanelLeft, PanelRight, PanelBottom, PanelTop);
                EtsGameUi.WindowPlate(panel, true);
            }

            BuildMapPage();
            BuildJobPage();
            SetPage(false);

            if (!_layoutLogged)
            {
                _layoutLogged = true;
                Debug.Log("[SomniumEuroTruck] Route advisor: laid out as adviser_gps.sii's widget " + PanelLeft + ".." + PanelRight + " x " + PanelBottom + ".." + PanelTop +
                          ", map " + MapLeft + ".." + MapRight + " x " + MapBottom + ".." + MapTop + ", route strip under the map, " + (vr == null ? "bottom-right of the screen (fit)" : "on the dash") + ".");
            }

            if (StartHidden)
            {
                Visible = false;
            }
        }

        private void BuildMapPage()
        {
            _mapPage = new GameObject("map page", typeof(RectTransform));
            _mapPage.transform.SetParent(_frame, false);
            Stretch((RectTransform)_mapPage.transform);

            // The GPS map as the game draws it (/def/map_data.sii, see EtsGpsMap): roads round the truck in
            // road_discovered_color, the route in navigation_color with its outline, heading up, one map pixel per
            // panel unit, redrawn a few times a second into a reused CPU texture (no camera, no world line needed).
            float mapL = MapLeft, mapR = MapRight, mapB = MapBottom, mapT = MapTop;
            int width = Mathf.RoundToInt(mapR - mapL), height = Mathf.RoundToInt(mapT - mapB);
            if (_gps == null || _gps.Width != width || _gps.Height != height)
            {
                _gps = new EtsGpsMap(width, height);
                string defs = _gps.LoadDefinitions(EtsEngine.Files);
                Debug.Log("[SomniumEuroTruck] GPS map: " + width + "x" + height + " px, " + defs + ".");
            }

            _mapTexture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = "advisor gps map", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _drawnZoom = -1;
            _gpsTimer = 1f;

            RectTransform mapRect = EtsGameUi.Place(_mapPage.transform, "map", mapL, mapR, mapB, mapT);
            var map = mapRect.gameObject.AddComponent<RawImage>();
            map.texture = _mapTexture;
            map.raycastTarget = false;

            // The player on the map: the game's own /material/ui/map/player.mat arrow at the centre (zoom_uplift is a
            // camera straight over the player), player_size metres long (sized in RenderGps).
            _arrow = EtsGameUi.Place(_mapPage.transform, "truck", 0f, 40f, 0f, 40f);
            _arrow.anchoredPosition = new Vector2((mapL + mapR) * 0.5f - 20f, (mapB + mapT) * 0.5f - 20f);
            Texture2D arrow = EtsGameUi.Material("/material/ui/map/player.mat");
            // No AspectRatioFitter (Picture's preserveAspect): its FitInParent sized the arrow to the 1440 x 900 map page,
            // the giant blurred blue triangle over the screen and the tablet (2026-10-14). The rect is square already.
            if (arrow != null) EtsGameUi.Picture(_arrow, arrow, false);

            // The status strip over the map (txt.adv_time's place, 1145..1425 x 235..259, centred, normal face, clr_txt):
            // the next turn. Kept clear of the speed box (to 1190) and symmetric about the strip's centre.
            RectTransform routeRect = EtsGameUi.Place(_mapPage.transform, "next turn", 1192f, 1378f, StatusBottom, StatusTop);
            _routeLine = OneLine(EtsGameUi.Label(routeRect, "", EtsFont.Normal, TextAlignmentOptions.Top, EtsGameUi.TextColour));

            // The speed box over the map's top-left corner: bcg_rect4 9A595959 1150..1190 x 214..254 and txt.adv_speed
            // "speed||@@clr_white@@" 1150..1190 x 215..255. The template is built once; its figure is swapped in.
            EtsGameUi.Template((RectTransform)_mapPage.transform, "txt.window.bcg_rect4", "9A595959", 1150f, 1190f, 214f, 254f);
            RectTransform speedRect = EtsGameUi.Template((RectTransform)_mapPage.transform, "txt.adv_speed", "~||@@clr_white@@", 1150f, 1190f, 215f, 255f);
            _speedLabel = speedRect != null ? speedRect.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            int mark = _speedLabel != null && _speedLabel.text != null ? _speedLabel.text.IndexOf('~') : -1;
            _speedPrefix = mark >= 0 ? _speedLabel.text.Substring(0, mark) : "";
            _speedSuffix = mark >= 0 ? _speedLabel.text.Substring(mark + 1) : "";
            if (_speedLabel != null) _speedLabel.text = _speedPrefix + "0" + _speedSuffix;
            _shownSpeed = int.MinValue;

            // The zoom key in the map's bottom right: "F2" (1401, txt.normal.right.top 1266..1388 x 46..68) and zoom.mat
            // 22 x 22 at 1390..1412 x 46..68.
            EtsGameUi.Template((RectTransform)_mapPage.transform, "txt.normal.right.top", "F2", 1266f, 1388f, 46f, 68f);
            RectTransform zoomIcon = EtsGameUi.Place(_mapPage.transform, "zoom icon", 1390f, 1412f, 46f, 68f);
            EtsGameUi.Layer(zoomIcon, "n", "<img src=/material/ui/adviser/zoom.mat width=22 height=22>", null, 22f, 22f);

            // The route strip under the map (1200, txt.normal.center 1150..1420 x 15..42): destination.mat 14 x 18, a 6 px
            // gap, then distance and time, the pair centred as one run (RefreshRouteInfo places them).
            _destinationIcon = EtsGameUi.Place(_mapPage.transform, "destination icon", 0f, 14f, 0f, 18f);
            EtsGameUi.Layer(_destinationIcon, "n", DestinationIcon, null, 14f, 18f);
            _routeInfoRect = EtsGameUi.Place(_mapPage.transform, "route info", StripLeft, StripRight, StripBottom, StripTop);
            _distanceLine = OneLine(EtsGameUi.Label(_routeInfoRect, "", EtsFont.Normal, TextAlignmentOptions.Center, EtsGameUi.TextColour));
            _routeInfo = null;

            BuildLimitSign();
        }

        /// <summary>The limit sign (txt.adv_navi_spd, adviser_gps.sii 1302: 1154..1186 x 183..215, under the speed box), rebuilt when the figure changes.</summary>
        private void BuildLimitSign()
        {
            if (_limitSign != null)
            {
                Destroy(_limitSign.gameObject);
                _limitSign = null;
            }

            if (_limit > 0 && _mapPage != null)
            {
                _limitSign = EtsGameUi.Template((RectTransform)_mapPage.transform, "txt.adv_navi_spd", _limit.ToString(), 1154f, 1186f, 183f, 215f);
            }
        }

        /// <summary>
        /// The route strip's text: with the destination icon the two are centred as one run (the game's txt.normal.center
        /// value "&lt;img destination.mat&gt;&lt;offset hshift=6&gt;time"); without it the text is centred alone.
        /// </summary>
        private void SetRouteInfo(string text, bool icon)
        {
            if (_distanceLine == null || (text == _routeInfo && icon == _routeInfoIcon))
            {
                return;
            }

            _routeInfo = text;
            _routeInfoIcon = icon;
            _distanceLine.text = text;

            if (_destinationIcon != null)
            {
                _destinationIcon.gameObject.SetActive(icon);
            }

            const float iconRun = 14f + 6f;
            float left = StripLeft;

            if (icon)
            {
                float width = Mathf.Min(_distanceLine.GetPreferredValues(text).x, StripRight - StripLeft - iconRun);
                float start = (StripLeft + StripRight - iconRun - width) * 0.5f;
                if (_destinationIcon != null) _destinationIcon.anchoredPosition = new Vector2(start, StripBottom + (StripTop - StripBottom - 18f) * 0.5f);
                left = start + iconRun;
            }

            _distanceLine.alignment = icon ? TextAlignmentOptions.Left : TextAlignmentOptions.Center;
            _routeInfoRect.anchoredPosition = new Vector2(left, StripBottom);
            _routeInfoRect.sizeDelta = new Vector2(StripRight - left, StripTop - StripBottom);
        }

        private void BuildJobPage()
        {
            _jobPage = new GameObject("job page", typeof(RectTransform));
            _jobPage.transform.SetParent(_frame, false);
            Stretch((RectTransform)_jobPage.transform);

            RectTransform titleRect = EtsGameUi.Place(_jobPage.transform, "title", PanelLeft + 10f, PanelLeft + 200f, MapTop + 1f, PanelTop);
            EtsGameUi.Label(titleRect, "Current job", EtsFont.Normal, TextAlignmentOptions.Left, EtsGameUi.SelectedColour);

            // Rows twenty pixels tall from the map's top (232) down, label on the left, value on
            // the right, alternating grey and plain.
            string[] labels = { "Cargo", "Destination", "Distance", "Reward", "Delivered" };
            float top = MapTop - 4f;

            for (int i = 0; i < labels.Length; i++)
            {
                RectTransform labelRect = EtsGameUi.Place(_jobPage.transform, "label " + i, PanelLeft + 10f, PanelLeft + 105f, top - 20f, top);
                EtsGameUi.Label(labelRect, labels[i], EtsFont.Normal, TextAlignmentOptions.Left, i % 2 == 0 ? new Color(0.62f, 0.63f, 0.65f) : EtsGameUi.TextColour);
                RectTransform valueRect = EtsGameUi.Place(_jobPage.transform, "value " + i, PanelLeft + 105f, PanelRight - 10f, top - 20f, top);
                _jobValues.Add(OneLine(EtsGameUi.Label(valueRect, "", EtsFont.Normal, TextAlignmentOptions.Right, EtsGameUi.TextColour)));
                top -= 20f;
            }

            RectTransform hintRect = EtsGameUi.Place(_jobPage.transform, "hint", PanelLeft + 10f, PanelRight - 10f, PanelBottom + 4f, PanelBottom + 26f);
            EtsGameUi.Label(hintRect, "F1 map   F2 zoom", EtsFont.Small, TextAlignmentOptions.Right, new Color(0.55f, 0.56f, 0.58f));
        }

        /// <summary>One line, shrinking (to 60 %) then cut with an ellipsis rather than wrapping into the row above.</summary>
        private static TextMeshProUGUI OneLine(TextMeshProUGUI label)
        {
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.enableAutoSizing = true;
            label.fontSizeMax = label.fontSize;
            label.fontSizeMin = Mathf.Max(8f, label.fontSize * 0.6f);
            label.overflowMode = TextOverflowModes.Ellipsis;
            return label;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private void SetPage(bool job)
        {
            _showJob = job;
            if (_mapPage != null) _mapPage.SetActive(!job);
            if (_jobPage != null) _jobPage.SetActive(job);
            _gpsTimer = 1f;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1))
            {
                SetPage(!_showJob);
                EtsGameUi.Note("F1: advisor shows the " + (_showJob ? "job page" : "map"));
            }

            if (Input.GetKeyDown(KeyCode.F2))
            {
                Zoom = (Zoom + 1) % 2;
                _gpsTimer = 1f;
                EtsGameUi.Note("F2: advisor map zoom " + (Zoom == 0 ? "close" : "far"));
            }

            if ((_refresh += Time.deltaTime) < 0.2f)
            {
                return;
            }

            _refresh = 0f;

            if (FollowLocalTruck)
            {
                if (_canvasObject != null && _builtForVr != EtsVr.Present)
                {
                    Debug.Log("[SomniumEuroTruck] Route advisor: rebuilt for " + (EtsVr.Present ? "the headset (on the dash)" : "the desktop (bottom right)") + " after a switch between desktop and headset.");
                    Destroy(_canvasObject);
                    _canvasObject = null;
                    Bind(EtsLocalTruck.Occupied);
                }

                // A job's loading screen comes and goes without a truck change.
                // On a desktop the advisor (bottom right) gives way to any tablet: the world's app (2026-10-05) and the
                // Somnium client's own tablet (2026-10-14, AnyTabletOpen); on the dash in a headset it is not in the way.
                bool show = ShowsFor(EtsLocalTruck.Occupied) && (_onDash || !AnyTabletOpen);

                if (show != Visible)
                {
                    Visible = show;

                    if (!show && !_onDash && !_tabletHideLogged && AnyTabletOpen)
                    {
                        _tabletHideLogged = true;
                        Debug.Log("[SomniumEuroTruck] Route advisor: hidden while a tablet is open (" + (EtsTabletApp.IsVisible ? "the world's app" : "the Somnium client's tablet") + ").");
                    }
                }
            }

            RefreshText();
        }

        private void LateUpdate()
        {
            if (_gps == null || _mapTexture == null || Truck == null || _showJob || !Visible)
            {
                return;
            }

            _collectTimer += Time.deltaTime;

            if ((_gpsTimer += Time.deltaTime) < 0.15f)
            {
                return;
            }

            _gpsTimer = 0f;
            RenderGps();
        }

        /// <summary>
        /// Redraws the GPS map round the truck, heading up: the road cache is refilled from the loaded map sectors when
        /// the truck has gone 500 m or every 4 s (sectors stream in), the route is the job's (EtsRouteLine.Points, the
        /// part still ahead). Allocation-free apart from those refills.
        /// </summary>
        private void RenderGps()
        {
            EtsMapViewer viewer = EtsMapViewer.Current;
            Transform truck = Truck.transform;
            Vector3 position = truck.position;

            if (viewer != null && viewer.Map != null && (!_gps.HasCollected || _collectTimer > 4f || (position - _gps.CollectedAround).sqrMagnitude > 500f * 500f))
            {
                _collectTimer = 0f;
                _gps.Collect(viewer.Map, viewer.Map.LoadedSectors, viewer.CenterGame, position, _gps.NeededRadius());
            }

            EtsRouteLine route = Jobs != null && Jobs.Current != null ? Jobs.Route : null;
            bool hasRoute = route != null && route.Visible;
            _gps.Render(position, truck.forward, Zoom, hasRoute ? route.Points : null, hasRoute ? route.Nearest : 0);
            _mapTexture.SetPixels32(_gps.Pixels);
            _mapTexture.Apply(false);

            if (_drawnZoom != Zoom && _arrow != null)
            {
                _drawnZoom = Zoom;
                float size = Mathf.Clamp(_gps.PlayerPixels(Zoom), 16f, 60f);
                _arrow.sizeDelta = new Vector2(size, size);
                _arrow.anchoredPosition = new Vector2((MapLeft + MapRight) * 0.5f - size * 0.5f, (MapBottom + MapTop) * 0.5f - size * 0.5f);
            }

            if (!_gpsLogged)
            {
                _gpsLogged = true;
                Debug.Log("[SomniumEuroTruck] GPS map drawn: " + _gps.Segments + " road segments round the truck, zoom " + Zoom + ", route " + (hasRoute ? route.Points.Count + " points" : "none") + ".");
            }
        }

        private static string s_noJob;

        /// <summary>The speed box's figure (txt.adv_speed %0), km/h, swapped into the built template only when it changes.</summary>
        private void ShowSpeed()
        {
            if (_speedLabel == null)
            {
                return;
            }

            int kmh = Truck != null ? Mathf.RoundToInt(Mathf.Abs(Truck.SpeedKmh)) : 0;

            if (kmh != _shownSpeed)
            {
                _shownSpeed = kmh;
                _speedLabel.text = _speedPrefix + kmh + _speedSuffix;
            }
        }

        private void RefreshText()
        {
            if (Jobs == null)
            {
                return;
            }

            EtsJobs.Offer job = Jobs.Current;

            if (job == null)
            {
                // The game's own short line (locale no_job), centred in the route strip under the map - not across the map.
                if (s_noJob == null) s_noJob = EtsLocale.TryWord("no_job", out string word) && !string.IsNullOrEmpty(word) ? word : "Currently you have no delivery job.";
                if (_routeLine != null && _routeLine.text.Length > 0) _routeLine.text = "";
                SetRouteInfo(s_noJob, false);
                ShowSpeed();
                foreach (TextMeshProUGUI value in _jobValues) value.text = "-";
                return;
            }

            EtsRouteLine route = Jobs.Route;
            float remaining = route != null && route.Visible && Truck != null ? route.RemainingDistance(Truck.transform.position)
                            : Truck != null ? Vector3.Distance(Truck.transform.position, job.ToBay) : job.RouteLength;

            string turn = route != null && route.Visible && Truck != null ? NextTurn(route, Truck.transform.position) : "";

            if (_routeLine != null)
            {
                string line = remaining < 300f ? "Stop in the marked bay" : turn;
                if (_routeLine.text != line) _routeLine.text = line;
            }

            ShowSpeed();

            if (_distanceLine != null)
            {
                // The game's route strip: distance and time to go (its sample "20h 15min"); the destination is on the F1 page.
                float speed = Truck != null ? Mathf.Max(30f, Truck.SpeedKmh) : 60f;
                int minutes = Mathf.RoundToInt(remaining / 1000f / speed * 60f);
                string time = minutes >= 60 ? (minutes / 60) + "h " + (minutes % 60) + "min" : minutes + "min";
                SetRouteInfo(GameKm(job, remaining) + "   " + time, true);
            }

            if (_jobValues.Count >= 5)
            {
                _jobValues[0].text = job.Units + " x " + job.CargoName + " (" + Mathf.RoundToInt(job.CargoMass / 1000f) + " t)";
                _jobValues[1].text = job.ToCompanyName + ", " + job.ToCityName;
                _jobValues[2].text = GameKm(job, remaining) + " to go of " + GameKm(job, job.RouteLength);
                // The new flow writes money the game's way (EtsLocale.Money, "€2,000"); the old advisor keeps its words.
                _jobValues[3].text = FollowLocalTruck ? EtsLocale.Money(job.Reward) : job.Reward.ToString("N0") + " EUR";
                _jobValues[4].text = Jobs.Completed + " load" + (Jobs.Completed == 1 ? "" : "s") + ", " + (FollowLocalTruck ? EtsLocale.Money(Jobs.Earnings) : Jobs.Earnings.ToString("N0") + " EUR");
            }
        }

        /// <summary>
        /// A distance left on the job, in the game's km (the km the offer and its pay were given in,
        /// EtsJobs.Offer.ToGameKm). Turn distances stay in metres driven (<see cref="Km"/>): those are
        /// for judging the next junction from the cab.
        /// </summary>
        private static string GameKm(EtsJobs.Offer job, float metres)
        {
            float km = job.ToGameKm(metres);
            return km >= 1f ? km.ToString("0.0") + " km" : Mathf.RoundToInt(km * 100f) * 10 + " m";
        }

        private static string Km(float metres)
        {
            return metres >= 1000f ? (metres / 1000f).ToString("0.0") + " km" : Mathf.RoundToInt(metres / 10f) * 10 + " m";
        }

        /// <summary>
        /// The next place the route bends by more than a lane change, and how far it is: the
        /// polyline is walked forward from the point nearest the truck until two consecutive
        /// segments differ by more than 35 degrees.
        /// </summary>
        private static string NextTurn(EtsRouteLine route, Vector3 position)
        {
            IReadOnlyList<Vector3> points = route.Points;
            IReadOnlyList<float> along = route.Cumulative;

            if (points == null || points.Count < 3 || along == null || along.Count != points.Count)
            {
                return "Follow the route";
            }

            int nearest = 0;
            float best = float.MaxValue;

            for (int i = 0; i < points.Count; i++)
            {
                float d = (points[i] - position).sqrMagnitude;
                if (d < best) { best = d; nearest = i; }
            }

            for (int i = Mathf.Max(nearest, 1); i + 1 < points.Count; i++)
            {
                Vector3 a = points[i] - points[i - 1];
                Vector3 b = points[i + 1] - points[i];
                a.y = 0f; b.y = 0f;

                if (a.sqrMagnitude < 1f || b.sqrMagnitude < 1f)
                {
                    continue;
                }

                float angle = Vector3.SignedAngle(a.normalized, b.normalized, Vector3.up);

                if (Mathf.Abs(angle) > 35f)
                {
                    float distance = Mathf.Max(0f, along[i] - along[nearest]);
                    string side = angle < 0f ? "left" : "right";
                    return (Mathf.Abs(angle) > 120f ? "Turn around" : "Turn " + side) + " in " + Km(distance);
                }
            }

            return "Continue straight on";
        }
    }
}
