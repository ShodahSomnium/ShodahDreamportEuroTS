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
    /// Laid out from the game's own files. /ui/adviser_map.sii puts the panel at 1069 to 1419
    /// across and 83 to 288 up in the 1440 x 900 frame, the map itself in 1069..1419 x 83..266,
    /// the title "Navigation" top left in txt.adv_title (normal.font, 10-pixel margin), the zoom
    /// key top right at 1353..1414 x 231..260, the speed-limit sign at 1079..1111 x 223..255 and
    /// a line of route notes at 1091..1399 x 174..215. /ui/adviser_job.sii is the other page in
    /// the same panel: a title and rows twenty pixels tall of "label | value" in txt.adv_line.
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

        [Tooltip("Metres from the truck to the edge of the map at each zoom step; F2 cycles them.")]
        public float[] ZoomSteps = { 120f, 260f, 600f };

        public int Zoom = 1;

        [Tooltip("Built hidden; the front end shows it once the drive begins.")]
        public bool StartHidden = true;

        private const float PanelLeft = 1069f, PanelRight = 1419f, PanelBottom = 83f, PanelTop = 288f;
        private const float MapTop = 266f;

        private GameObject _canvasObject;
        private RectTransform _frame;
        private GameObject _mapPage;
        private GameObject _jobPage;
        private Camera _mapCamera;
        private RenderTexture _mapTexture;
        private TextMeshProUGUI _routeLine;
        private TextMeshProUGUI _distanceLine;
        private readonly List<TextMeshProUGUI> _jobValues = new List<TextMeshProUGUI>();
        private bool _showJob;
        private float _refresh;

        public bool Visible
        {
            get => _canvasObject != null && _canvasObject.activeSelf;
            set { if (_canvasObject != null) _canvasObject.SetActive(value); }
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
                _mapTexture.Release();
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
                    Vector3 eye = truck.DriverEyeLocal;
                    var focus = new Vector2((PanelLeft + PanelRight) * 0.5f, (PanelBottom + PanelTop) * 0.5f);
                    vr.AttachTo(truck.transform, eye + new Vector3(0.42f, -0.34f, 0.62f), eye, focus, 0.28f * EtsGameUi.ReferenceWidth / (PanelRight - PanelLeft));
                }
                else
                {
                    _canvasObject.transform.SetParent(transform, false);
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
            // Built again after a truck took the headset's panel: the old camera and texture go first.
            if (_mapCamera != null)
            {
                Destroy(_mapCamera.gameObject);
                _mapCamera = null;
            }

            if (_mapTexture != null)
            {
                _mapTexture.Release();
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

            if (vr != null && Truck != null)
            {
                Vector3 eye = Truck.DriverEyeLocal;
                var focus = new Vector2((PanelLeft + PanelRight) * 0.5f, (PanelBottom + PanelTop) * 0.5f);
                vr.AttachTo(Truck.transform, eye + new Vector3(0.42f, -0.34f, 0.62f), eye, focus, 0.28f * EtsGameUi.ReferenceWidth / (PanelRight - PanelLeft));
            }
            _frame = EtsGameUi.Place(canvas.transform, "frame", 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);

            // The panel, painted the way the game paints its windows.
            RectTransform panel = EtsGameUi.Place(_frame, "panel", PanelLeft, PanelRight, PanelBottom, PanelTop);
            EtsGameUi.WindowPlate(panel, true);

            BuildMapPage();
            BuildJobPage();
            SetPage(false);

            if (StartHidden)
            {
                Visible = false;
                _mapCamera.enabled = false;
            }
        }

        private void BuildMapPage()
        {
            _mapPage = new GameObject("map page", typeof(RectTransform));
            _mapPage.transform.SetParent(_frame, false);
            Stretch((RectTransform)_mapPage.transform);

            // The map: the world from above, drawn to a texture the size of the map area.
            int width = Mathf.RoundToInt(PanelRight - PanelLeft), height = Mathf.RoundToInt(MapTop - PanelBottom);
            _mapTexture = new RenderTexture(width * 2, height * 2, 16, RenderTextureFormat.ARGB32) { name = "advisor map" };
            _mapTexture.Create();

            RectTransform mapRect = EtsGameUi.Place(_mapPage.transform, "map", PanelLeft + 4f, PanelRight - 4f, PanelBottom + 4f, MapTop - 2f);
            var map = mapRect.gameObject.AddComponent<RawImage>();
            map.texture = _mapTexture;
            map.raycastTarget = false;

            var cameraObject = new GameObject("advisor map camera");
            cameraObject.transform.SetParent(transform, false);
            _mapCamera = cameraObject.AddComponent<Camera>();
            _mapCamera.orthographic = true;
            _mapCamera.orthographicSize = ZoomSteps[Mathf.Clamp(Zoom, 0, ZoomSteps.Length - 1)];
            _mapCamera.nearClipPlane = 1f;
            _mapCamera.farClipPlane = 900f;
            _mapCamera.clearFlags = CameraClearFlags.SolidColor;
            _mapCamera.backgroundColor = new Color(0.10f, 0.10f, 0.10f, 1f);
            _mapCamera.targetTexture = _mapTexture;
            _mapCamera.cullingMask = ~((1 << 5) | (1 << EtsLayers.Player));
            _mapCamera.allowMSAA = false;
            _mapCamera.depth = -10;
            _mapCamera.enabled = false;

            // The truck, drawn as the game draws it on its map: a small icon at the centre.
            RectTransform truckRect = EtsGameUi.Place(_mapPage.transform, "truck", 0f, 32f, 0f, 16f);
            truckRect.anchoredPosition = new Vector2((PanelLeft + PanelRight) * 0.5f - 16f, (PanelBottom + MapTop) * 0.5f - 8f);
            EtsGameUi.Painted(truckRect, EtsGameUi.Painter.Whole("/material/ui/adviser/truck_small.mat", 32, 16, 0xFFFFFFFF));

            // Title, top left, as txt.adv_title lays it: normal face, ten pixels in.
            RectTransform titleRect = EtsGameUi.Place(_mapPage.transform, "title", PanelLeft + 10f, PanelLeft + 200f, MapTop + 1f, PanelTop);
            EtsGameUi.Label(titleRect, "Navigation", EtsFont.Normal, TextAlignmentOptions.Left, EtsGameUi.SelectedColour);

            // The zoom key, top right of the map: the F2 plate the game draws.
            RectTransform zoomRect = EtsGameUi.Place(_mapPage.transform, "zoom", 1353f, 1414f, 231f, 260f);
            EtsGameUi.Painted(zoomRect, EtsGameUi.Painter.Strip("/material/ui/adviser/key_button_bcg1.mat", 61, 29, 0xFFFFFFFF));
            RectTransform zoomIcon = EtsGameUi.Place(_mapPage.transform, "zoom icon", 1361f, 1377f, 238f, 254f);
            EtsGameUi.Painted(zoomIcon, EtsGameUi.Painter.Whole("/material/ui/adviser/map_zoom1.mat", 16, 16, 0xFFFFFFFF));
            RectTransform zoomText = EtsGameUi.Place(_mapPage.transform, "zoom text", 1379f, 1412f, 232f, 259f);
            EtsGameUi.Label(zoomText, "F2", EtsFont.Small, TextAlignmentOptions.Left, new Color(0.81f, 0.81f, 0.81f));

            // The route notes: next turn on one line, distance and time on the other.
            RectTransform routeRect = EtsGameUi.Place(_mapPage.transform, "route", 1091f, 1399f, 195f, 215f);
            _routeLine = EtsGameUi.Label(routeRect, "", EtsFont.Normal, TextAlignmentOptions.Left, EtsGameUi.TextColour);
            RectTransform distanceRect = EtsGameUi.Place(_mapPage.transform, "distance", 1091f, 1399f, 174f, 195f);
            _distanceLine = EtsGameUi.Label(distanceRect, "", EtsFont.Normal, TextAlignmentOptions.Left, EtsGameUi.SelectedColour);
        }

        private void BuildJobPage()
        {
            _jobPage = new GameObject("job page", typeof(RectTransform));
            _jobPage.transform.SetParent(_frame, false);
            Stretch((RectTransform)_jobPage.transform);

            RectTransform titleRect = EtsGameUi.Place(_jobPage.transform, "title", PanelLeft + 10f, PanelLeft + 200f, MapTop + 1f, PanelTop);
            EtsGameUi.Label(titleRect, "Current job", EtsFont.Normal, TextAlignmentOptions.Left, EtsGameUi.SelectedColour);

            // adviser_job.sii: rows twenty pixels tall from 246 down, label on the left, value on
            // the right, alternating grey and plain.
            string[] labels = { "Cargo", "Destination", "Distance", "Reward", "Delivered" };
            float top = 246f;

            for (int i = 0; i < labels.Length; i++)
            {
                RectTransform labelRect = EtsGameUi.Place(_jobPage.transform, "label " + i, PanelLeft + 10f, PanelLeft + 150f, top - 20f, top);
                EtsGameUi.Label(labelRect, labels[i], EtsFont.Normal, TextAlignmentOptions.Left, i % 2 == 0 ? new Color(0.62f, 0.63f, 0.65f) : EtsGameUi.TextColour);
                RectTransform valueRect = EtsGameUi.Place(_jobPage.transform, "value " + i, PanelLeft + 150f, PanelRight - 10f, top - 20f, top);
                _jobValues.Add(EtsGameUi.Label(valueRect, "", EtsFont.Normal, TextAlignmentOptions.Right, EtsGameUi.TextColour));
                top -= 20f;
            }

            RectTransform hintRect = EtsGameUi.Place(_jobPage.transform, "hint", PanelLeft + 10f, PanelRight - 10f, PanelBottom + 4f, PanelBottom + 26f);
            EtsGameUi.Label(hintRect, "F1 map   F2 zoom", EtsFont.Small, TextAlignmentOptions.Right, new Color(0.55f, 0.56f, 0.58f));
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
            if (_mapCamera != null) _mapCamera.enabled = !job && Visible;
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
                Zoom = (Zoom + 1) % ZoomSteps.Length;
                if (_mapCamera != null) _mapCamera.orthographicSize = ZoomSteps[Zoom];
                EtsGameUi.Note("F2: advisor map zoom " + ZoomSteps[Zoom] + " m");
            }

            if (_mapCamera != null)
            {
                _mapCamera.enabled = !_showJob && Visible;
            }

            if ((_refresh += Time.deltaTime) < 0.2f)
            {
                return;
            }

            _refresh = 0f;

            if (FollowLocalTruck)
            {
                // A job's loading screen comes and goes without a truck change.
                bool show = ShowsFor(EtsLocalTruck.Occupied);

                if (show != Visible)
                {
                    Visible = show;
                }
            }

            RefreshText();
        }

        private void LateUpdate()
        {
            if (_mapCamera == null || Truck == null || !_mapCamera.enabled)
            {
                return;
            }

            // Straight down from high over the truck, the truck's heading at the top of the map.
            Transform truck = Truck.transform;
            float yaw = truck.eulerAngles.y;
            _mapCamera.transform.position = truck.position + Vector3.up * 400f;
            _mapCamera.transform.rotation = Quaternion.Euler(90f, yaw, 0f);
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
                // Not in the locale. The new flow's job market is the tablet app's Jobs - Quick Job (PLAN §5.5 step 9).
                string none = FollowLocalTruck ? "No job. Tablet: ETS 2 - Jobs - Quick Job" : "No job. Open the job market (Esc).";
                if (_routeLine != null && _routeLine.text != none) _routeLine.text = none;
                if (_distanceLine != null) _distanceLine.text = "";
                foreach (TextMeshProUGUI value in _jobValues) value.text = "-";
                return;
            }

            EtsRouteLine route = Jobs.Route;
            float remaining = route != null && route.Visible && Truck != null ? route.RemainingDistance(Truck.transform.position)
                            : Truck != null ? Vector3.Distance(Truck.transform.position, job.ToBay) : job.RouteLength;

            string turn = route != null && route.Visible && Truck != null ? NextTurn(route, Truck.transform.position) : "";

            if (_routeLine != null)
            {
                _routeLine.text = remaining < 300f ? "Destination ahead - stop in the marked bay" : turn;
            }

            if (_distanceLine != null)
            {
                float speed = Truck != null ? Mathf.Max(30f, Truck.SpeedKmh) : 60f;
                float minutes = remaining / 1000f / speed * 60f;
                _distanceLine.text = GameKm(job, remaining) + "   " + job.ToCompanyName + ", " + job.ToCityName + "   " + Mathf.RoundToInt(minutes) + " min";
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
