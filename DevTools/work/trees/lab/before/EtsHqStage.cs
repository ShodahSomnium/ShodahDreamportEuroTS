using System;
using System.Collections.Generic;
using SomniumSpace.Bridge;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Where the HQ map's picture and dots land in the 1440 x 900 frame (owner D; [gameui §3.1]). Pure, so the harness
    /// checks it (probe 1893).
    ///
    /// The picture is the game's own map: /material/ui/map/map.mat, one 2048-square picture of the whole UI map, or
    /// map0..map3.mat, four 2048-square quarters of the same map at 4096, column-major: map0 top-left (u 0..0.5, v
    /// 0..0.5; Iceland, UK), map1 bottom-left (Iberia), map2 top-right (Scandinavia), map3 bottom-right (the
    /// Mediterranean) - the order probe 1646's contact sheet showed (png/sheet_maptiles.png). The HQ stage uses the one
    /// picture (enough at ~0.9 texel per frame unit, and the quarters' last texel row and column are lighter, alpha 182
    /// against 193: a faint seam, probe 1896); the Quick Job route uses the one picture too (the quarters are not
    /// prefetched: 391 ms and 64 MB on the frame, probe 2056). It fills
    /// /ui/hq_selection.sii's portal (17..1423 x 77..847; the ui_job_map inside,
    /// /ui/hq_selection_map.sii, is drawn by the engine, whose camera for this screen is in no file). The framing is
    /// therefore [gameui §3.1]'s proposal, not the game's: the 72 cities' u/v extent grown by 0.02, fitted into the
    /// portal with the map's metres square (EtsUiMapFrame: 279,379 x 273,684 m for the whole picture). On 1.61 that is
    /// height-limited, ~126 game metres per frame unit. Dragging pans it, the centre kept inside
    /// ui_map_camera_min/max.
    /// </summary>
    public sealed class EtsHqMapView
    {
        /// <summary>/ui/hq_selection.sii's portal (probe 1885).</summary>
        public float PortalLeft = 17f, PortalRight = 1423f, PortalBottom = 77f, PortalTop = 847f;

        public readonly EtsUiMapFrame Map;

        /// <summary>The map u/v at the portal's centre, and frame units per whole u / v.</summary>
        public float CentreU = 0.5f, CentreV = 0.5f, FramePerU = 1400f, FramePerV = 1400f;

        public static readonly string[] Tiles = { "/material/ui/map/map0.mat", "/material/ui/map/map1.mat", "/material/ui/map/map2.mat", "/material/ui/map/map3.mat" };

        /// <summary>The whole map in one 2048-square picture.</summary>
        public const string Whole = "/material/ui/map/map.mat";

        public EtsHqMapView(EtsUiMapFrame map)
        {
            Map = map ?? new EtsUiMapFrame();
        }

        public float PortalWidth => PortalRight - PortalLeft;
        public float PortalHeight => PortalTop - PortalBottom;

        /// <summary>Game metres per frame unit at this framing.</summary>
        public float MetresPerUnit => Map.Width / FramePerU;

        /// <summary>Fits the cities' u/v extent grown by margin (0.02: u 0.313..0.657, v 0.296..0.651 on 1.61).</summary>
        public void Fit(IReadOnlyList<EtsHqCity> cities, float margin)
        {
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;

            if (cities != null)
            {
                foreach (EtsHqCity c in cities)
                {
                    u0 = Math.Min(u0, c.U); u1 = Math.Max(u1, c.U);
                    v0 = Math.Min(v0, c.V); v1 = Math.Max(v1, c.V);
                }
            }

            if (u0 > u1)
            {
                // No catalogue yet: [gameui §3.1]'s extent of the 72.
                u0 = 0.3334f; u1 = 0.6372f; v0 = 0.3161f; v1 = 0.6310f;
            }

            FitRange(u0 - margin, u1 + margin, v0 - margin, v1 + margin);
        }

        public void FitRange(float u0, float u1, float v0, float v1)
        {
            float widthM = Math.Max(1f, (u1 - u0) * Map.Width), heightM = Math.Max(1f, (v1 - v0) * Map.Height);
            float perMetre = Math.Min(PortalWidth / widthM, PortalHeight / heightM);
            FramePerU = perMetre * Map.Width;
            FramePerV = perMetre * Map.Height;
            CentreU = (u0 + u1) / 2f;
            CentreV = (v0 + v1) / 2f;
        }

        /// <summary>A map u/v in the frame (x right, y up).</summary>
        public Vector2 ToFrame(float u, float v)
        {
            return new Vector2((PortalLeft + PortalRight) / 2f + (u - CentreU) * FramePerU, (PortalBottom + PortalTop) / 2f - (v - CentreV) * FramePerV);
        }

        /// <summary>The map dragged by (dx, dy) frame units: the picture follows the pointer; the centre stays inside the camera limits.</summary>
        public void Pan(float dxFrame, float dyFrame)
        {
            CentreU -= dxFrame / FramePerU;
            CentreV += dyFrame / FramePerV;
            float uMin = Map.U(Map.CameraMinX), uMax = Map.U(Map.CameraMaxX), vMin = Map.V(Map.CameraMinZ), vMax = Map.V(Map.CameraMaxZ);
            CentreU = Math.Max(Math.Min(uMin, uMax), Math.Min(Math.Max(uMin, uMax), CentreU));
            CentreV = Math.Max(Math.Min(vMin, vMax), Math.Min(Math.Max(vMin, vMax), CentreV));
        }

        /// <summary>Where map tile 0..3 lies in the frame.</summary>
        public void TileBounds(int tile, out float left, out float right, out float bottom, out float top)
        {
            float u0 = tile < 2 ? 0f : 0.5f, v0 = tile % 2 == 0 ? 0f : 0.5f;
            Vector2 a = ToFrame(u0, v0), b = ToFrame(u0 + 0.5f, v0 + 0.5f);
            left = a.x; right = b.x; top = a.y; bottom = b.y;
        }

        /// <summary>Where the whole map (u, v 0..1; map.mat) lies in the frame.</summary>
        public void WholeBounds(out float left, out float right, out float bottom, out float top)
        {
            Vector2 a = ToFrame(0f, 0f), b = ToFrame(1f, 1f);
            left = a.x; right = b.x; top = a.y; bottom = b.y;
        }

        /// <summary>A city's dot size in frame units: city_icon_size[7] by its population step (13 x 0.75 / 1 / 1.5).</summary>
        public float PinSize(int population)
        {
            return Map.PinSize * Map.PinScale(population);
        }
    }

    /// <summary>What the HQ stage writes (owner D; [gameui §3.1], PLAN.md §3.7). Pure.</summary>
    public static class EtsHqStageContent
    {
        public const string Layout = "/ui/hq_selection.sii";
        public const string MessageBox = "/ui/message_box.sii";
        public const string PanIcon = "/material/ui/map/pan_both.mat";

        /// <summary>The template of a city pin, 32 x 32 around its dot (/ui/template/button.sii:2280).</summary>
        public const string PinLook = "btn.city_pin";
        public const float PinBox = 32f;

        /// <summary>The map panel: 3.2 m wide (so 2.0 m tall at 1440 x 900), 2.4 m from the eyes in a headset (PLAN §3.4, [gameui §3.1]).</summary>
        public const float PanelDistance = 2.4f, PanelWidth = 3.2f, PanelTall = PanelWidth * 900f / 1440f;

        /// <summary>
        /// How far in front of a monitor's camera the whole panel fits its view with an 8 % margin, never nearer than
        /// PanelDistance nor farther than 6 m: 2.97 m for the client's 40-degree camera (CameraSystem.prefab:831) at 16:9
        /// (probe 2043). EtsHqStage's class notes say why a monitor is not the headset's 2.4 m.
        /// </summary>
        public static float DesktopDistance(float verticalFovDegrees, float aspect)
        {
            double v = Math.Max(10.0, Math.Min(120.0, verticalFovDegrees)) * 0.5 * Math.PI / 180.0;
            double a = aspect > 0.1f ? aspect : 16.0 / 9.0;
            double tanV = Math.Tan(v), tanH = tanV * a;
            double fitV = PanelTall * 0.5 / tanV, fitH = PanelWidth * 0.5 / tanH;
            return (float)Math.Max(PanelDistance, Math.Min(6.0, Math.Max(fitV, fitH) * 1.08));
        }

        /// <summary>Not in the locale (PLAN §3.7 item 3; the game has no instance master).</summary>
        public const string MasterBanner = "You are the instance master: choose where everyone drives";

        /// <summary>Not in the locale (PLAN §3.7 item 3).</summary>
        public static string WaitingBanner(string masterName)
        {
            return "Waiting for " + (string.IsNullOrEmpty(masterName) ? "the instance master" : masterName) + " to choose the headquarters";
        }

        /// <summary>While the room is still being joined (PLAN §5.2, Connecting; ~6.5 s in the client, [net F11]). Not in the locale.</summary>
        public const string ConnectingBanner = "Joining the instance ...";

        /// <summary>
        /// No room after EtsSession.OfflineAfter (20 s), or the editor: the player chooses for themselves and nothing is
        /// written to the room (PLAN §5.2, Offline). Not in the locale.
        /// </summary>
        public const string OfflineBanner = "Playing offline: choose where you start";

        /// <summary>A choice the session did not take (EtsSession.ChooseHeadquarters logs why). Not in the locale.</summary>
        public const string RefusedBanner = "That choice was not taken - you may no longer be the instance master";

        /// <summary>No install to read the map from (EtsEngine not mounted): the no-game notice's title (EtsFlowRules.NoGameTitle, Phase 3). Not in the locale.</summary>
        public const string NoGameBanner = "Euro Truck Simulator 2 (or its free demo) was not found on this PC";

        /// <summary>The tooltip's %0: "Felixstowe" over "United Kingdom" ([gameui §3.1]).</summary>
        public static string TooltipValue(EtsHqCity city)
        {
            return city == null ? "" : EtsLoadingContent.Clean(city.Name) + "<br>" + EtsLoadingContent.Clean(city.CountryName);
        }

        /// <summary>The pin's value: its icon's width and height (%0|%1).</summary>
        public static string PinValue(EtsHqMapView view, EtsHqCity city)
        {
            string s = view.PinSize(city.Population).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return s + "|" + s;
        }

        /// <summary>
        /// Whether a city can be chosen: it has a stand, and its yard's nodes did not have to be recovered past the
        /// sector parser (EtsHqCity.NodeRecovered: the map viewer would not build that yard; Dijon and Munich on 1.61).
        /// </summary>
        public static bool Available(EtsHqCity city)
        {
            return city != null && city.SpawnSpots > 0 && !city.NodeRecovered;
        }

        /// <summary>"LOADING ... 42 %" while the catalogue is read (the game's @@loading@@, PLAN §3.6).</summary>
        public static string ReadingLine(float progress01)
        {
            int percent = (int)Math.Round(Math.Max(0f, Math.Min(1f, progress01)) * 100f);
            return EtsLocale.Word("loading") + " " + percent + " %";
        }
    }

    /// <summary>
    /// The headquarters choice: a black void at Unity (0, +600, 0) with the game's HQ map, world-space, 3.2 m
    /// wide in front of the player (owner D; [gameui §3.1], PLAN.md §3.4, §3.7 item 3, §5.2, §7.D). Above the map,
    /// not 2.5 km under it as EtsGarageScene builds its studio: the scene's RespawnHeightY (-1000, PLAN §3.4) would
    /// pull a player that deep back to the spawn platform. The map's origin is the chosen yard (EtsMapViewer.LoadAt),
    /// so the yard streams directly below the void during Loading (U19).
    ///
    /// What is built (all from the player's install): /ui/hq_selection.sii through EtsGameUi.BuildLayout - the window
    /// (txt.window.bcg_rect4 in clr_bg_main), the title (@@hq_t@@, "HEADQUARTER SELECTION"), "Confirm selection"
    /// (btn.normal, disabled until a city is chosen); in its portal the game's map picture and a btn.city_pin per catalogue
    /// city (EtsHqMapView), the city names at their definitions' map_x/y_offsets[7] (face and colour a guess:
    /// normal.font, clr_txt), txt.ttip_simple on the hovered pin ("Felixstowe" over "United Kingdom"); over it all, the
    /// first time the stage is interactive, the opening /ui/message_box.sii with "YOUR HEADQUARTERS" / "Select the city
    /// you want to start in." / "OK", which takes the pointer until OK is pressed. The overlay anchor at the bottom-left
    /// is where the game puts its controls hint, drawn here as the pan icon and "Move map" (overlay_map_move). The banner
    /// (SetBanner) sits left of Confirm - a place the files do not give; its words are not in the locale.
    ///
    /// Order (Phase 2): Show builds the void and teleports the player at once; the map follows as soon as the archives
    /// are mounted and the locale is loaded (EtsSession.Start opens the map viewer; the tablet app or EtsFlow loads the
    /// words), or 6 s on without the words; the pins as soon as the catalogue is ready - until then the map says
    /// "LOADING ... n %".
    ///
    /// The player. Teleported onto the floor (EtsVr.Teleport, retried every 0.25 s until the Bridge's local player
    /// exists - U17), and, while KeepPlayer (EtsFlow: every phase before Loading), put back whenever the body is more
    /// than 14 m from the centre or below the floor: the client spawns the avatar at the scene's spawn point when it is
    /// ready, which may come after the first teleport, and the floor is 24 x 24 m with 4 m walls on Environment (23) so
    /// nobody walks off it (layer 23: outside EtsLayers.GroundMask, solid for the avatar's capsule and in the VR ground
    /// check). During Loading the arrival teleport (A) is never undone. In the editor there is no Bridge player: the
    /// main camera is stood in the void instead, so the stage can be clicked in Play mode.
    ///
    /// Where the map hangs: in front of the camera the player looks through, not in front of the avatar.
    /// - Headset: upright, at eye height, 2.4 m ahead (PanelDistance) - inside the NearFarInteractor's 5 m reach (memory
    ///   "somnium-client-physics"); about 67 x 45 degrees, well within a headset's view.
    /// - Monitor: the client's camera is a Cinemachine orbit behind the avatar (CameraSystem.prefab: m_Orbits middle rig
    ///   radius 16.2 m scaled by the zoom, NonXRCameraFeature._zoomValue 0.3 at start; first person only below
    ///   _firstPersonSwitchValue 0.04, Player.NonXR.Features.prefab:569), with a 40-degree vertical field of view
    ///   (CameraSystem.prefab:831). A 2.0 m-tall panel at 2.4 m spans 45 degrees, so "Confirm selection" (27..57 of 900)
    ///   would fall off the bottom of the screen: on a monitor the panel is put along the camera's own forward, square to
    ///   it, at the distance where the whole panel fits the camera's view with an 8 % margin (DesktopDistance: 2.97 m at
    ///   40 degrees and 16:9).
    /// It is placed once the player stands in the void and the camera has settled (0.6 s), and brought round again only
    /// when the player has looked more than 100 degrees away from it for 4 s, or is more than 7 m from it or walks into
    /// it.
    ///
    /// Pointing. The canvas is world-space on the UI layer (5) with a GraphicRaycaster and a
    /// TrackedDeviceGraphicRaycaster, worldCamera the player's camera (asked again at 4 Hz). That is how the client's own
    /// desktop tablet takes the mouse: its tabs are world-space canvases with GraphicRaycasters ([tablet §1],
    /// Tablet.NonXR.prefab adds 17), driven by the NonXREventSystem's InputSystemUIInputModule with mouse input on
    /// (Network/Prefabs/Core.Logic.prefab: NonXREventSystem, module 01614664..., activated in desktop mode by
    /// LoadingSystem/EventSystemHandler.cs), and NonXRCameraFeature.CheckIfMouseOnUI (EventSystem.RaycastAll at the
    /// mouse, any hit on _uiLayer 5) stops the camera turning while the mouse is over it; the desktop camera draws layer
    /// 5 (CameraSystem.prefab m_CullingMask 363020535: layers 0-2, 4-7, 14, 16, 17, 21, 23, 24, 26, 28). In a headset
    /// the NearFarInteractor's ray (far mask includes 5) answers through the TrackedDeviceGraphicRaycaster, as every
    /// EtsVrPanel does. Log "HQ: pointer on {city} - mouse / VR ray" proves it in the client (U5).
    ///
    /// Nothing runs per frame but a 4 Hz check (the catalogue's progress, the player's placement, the camera, the
    /// panel's facing); pins and labels move only while the map is dragged.
    /// </summary>
    public sealed class EtsHqStage : MonoBehaviour
    {
        public static readonly Vector3 VoidCentre = new Vector3(0f, 600f, 0f);
        public const float VoidRadius = 30f;

        /// <summary>From the eyes in a headset (PLAN §3.4, [gameui §3.1]).</summary>
        public const float PanelDistance = EtsHqStageContent.PanelDistance;

        public const float PanelWidth = EtsHqStageContent.PanelWidth;

        /// <summary>The panel's height in metres: 3.2 x 900 / 1440.</summary>
        public const float PanelTall = EtsHqStageContent.PanelTall;

        /// <summary>The panel's centre above the floor when no camera is known yet: a standing player's eyes (not measured).</summary>
        public const float PanelHeight = 1.6f;

        /// <summary>Half the floor's side: a 24 x 24 m floor inside the 30 m sphere, walled 4 m high.</summary>
        public const float FloorHalf = 12f;

        public const float WallHeight = 4f;

        /// <summary>Beyond this from the centre (or below the floor), a kept player is put back.</summary>
        public const float KeepRadius = 14f;

        public static EtsHqStage Current { get; private set; }

        /// <summary>Whether the map may be built now (EtsFlow: its UI prefetch is in); null: at once. A static, cleared by its setter's OnDestroy.</summary>
        public static Func<bool> ReadyToBuild;

        public event Action<EtsHqCity> Hovered;
        public event Action<EtsHqCity> Confirmed;

        public EtsHqCity Selected => _selected;
        public bool Interactive => _interactive;
        public EtsHqMapView View => _view;

        /// <summary>The map canvas is built (the archives and the words were there).</summary>
        public bool HasMap => _screen != null;

        /// <summary>The pins are on the map (the catalogue was ready).</summary>
        public bool HasPins => _pinsBuilt;

        /// <summary>The player has been teleported into the void at least once.</summary>
        public bool Placed => _placed;

        /// <summary>
        /// While true the player is put back into the void whenever they leave it (EtsFlow: before Loading). False
        /// during Loading, so the arrival teleport (A) is never undone.
        /// </summary>
        public bool KeepPlayer = true;

        private sealed class Pin
        {
            public EtsHqCity City;
            public RectTransform Rect, Label;
            public EtsUiLook Look;
        }

        private readonly List<Pin> _pins = new List<Pin>();
        private readonly Dictionary<ulong, Pin> _byToken = new Dictionary<ulong, Pin>();
        private RectTransform _picture;

        private GameObject _void;
        private Mesh _voidMesh;
        private Material _voidMaterial;
        private Canvas _canvas;
        private RectTransform _frame, _map, _tooltip, _boxFrame;
        private EtsGameScreen _screen;
        private EtsHqMapView _view;
        private TextMeshProUGUI _banner, _reading;
        private string _bannerText = "";
        private string _confirmKey = "confirm.btn";
        private bool _interactive, _pinsBuilt, _placed, _boxShown, _mapShown = true, _panelPlaced, _confirmed, _editorCamera;
        private EtsHqCity _selected, _highlighted, _hovered;
        private ulong _preselect, _highlightWanted;
        private float _shownAt, _nextTick, _placedAt, _lastTeleport, _awayFor;
        private int _putBack;

        // The no-game notice (SetNotice, Phase 3).
        private Canvas _notice;
        private TextMeshProUGUI _noticeTitle, _noticeBody, _noticePlayers;

        /// <summary>The notice panel's frame: the map's 1440 x 900 (3.2 x 2.0 m): room for the master's three paragraphs and a dozen players (probe 2261's first 760-unit panel cut the master line).</summary>
        public const float NoticeWidth = 1440f, NoticeHeight = 900f;

        /// <summary>Builds the void and teleports the player in, and the map as soon as it can; an existing stage is kept and made (non-)interactive.</summary>
        public static EtsHqStage Show(bool interactive)
        {
            if (Current != null)
            {
                Current.SetInteractive(interactive);
                return Current;
            }

            var host = new GameObject("Ets HQ Stage");
            var stage = host.AddComponent<EtsHqStage>();
            Current = stage;
            stage.Build(interactive);
            return stage;
        }

        public void SetInteractive(bool interactive)
        {
            _interactive = interactive;

            if (_screen != null)
            {
                _screen.SetShown(_confirmKey, interactive);
                _screen.SetEnabled(_confirmKey, interactive && _selected != null && !_confirmed);
            }

            foreach (Pin pin in _pins)
            {
                var button = pin.Rect != null ? pin.Rect.GetComponent<Button>() : null;
                if (button != null) button.interactable = interactive && EtsHqStageContent.Available(pin.City);
            }

            if (interactive && _screen != null && !_boxShown)
            {
                BuildBox();
            }

            if (!interactive && _boxFrame != null)
            {
                CloseBox();
            }
        }

        public void SetBanner(string text)
        {
            _bannerText = text ?? "";

            if (_banner != null && _banner.text != _bannerText)
            {
                _banner.text = _bannerText;
            }
        }

        /// <summary>
        /// For a visitor without the game (EtsFlow.NoGame, Phase 3): a plain panel where the map would hang - a title, what
        /// to do, and who is in the world (players). The map is never built for them (no archives), so the panel is built
        /// from nothing of the game: a dark plate and TextMeshPro in the scene's own status-line font
        /// (EtsNameplate.FallbackFont; LiberationSans SDF, shipped with the world). Placed and kept in view like the map
        /// (PlacePanel, KeepPanelInView). Rich text is off in the list, since it carries players' names. Texts are
        /// written only when they differ.
        /// </summary>
        public void SetNotice(string title, string body, string players)
        {
            if (_notice == null)
            {
                BuildNotice();
            }

            SetText(_noticeTitle, title);
            SetText(_noticeBody, body);
            SetText(_noticePlayers, players);
        }

        /// <summary>The notice is up (for EtsFlow's tests and log).</summary>
        public bool HasNotice => _notice != null;

        /// <summary>
        /// Takes the notice away (EtsFlow, when the archives turned up after all: EtsMapViewer.LoadAt opens them again if
        /// the first Open failed). The map is brought round in front of the player again: while the notice was up it was
        /// the panel PlacePanel placed, and the map, built meanwhile, was never placed.
        /// </summary>
        public void ClearNotice()
        {
            if (_notice == null)
            {
                return;
            }

            Destroy(_notice.gameObject);
            _notice = null;
            _noticeTitle = _noticeBody = _noticePlayers = null;
            _panelPlaced = false;
            Debug.Log("[SomniumEuroTruck] HQ stage: no-game notice removed.");
        }

        private static void SetText(TextMeshProUGUI label, string text)
        {
            text = text ?? "";

            if (label != null && label.text != text)
            {
                label.text = text;
            }
        }

        private void BuildNotice()
        {
            var canvasObject = new GameObject("Ets HQ Notice", typeof(RectTransform));
            canvasObject.transform.SetParent(transform, false);
            canvasObject.layer = EtsLayers.Ui;
            _notice = canvasObject.AddComponent<Canvas>();
            _notice.renderMode = RenderMode.WorldSpace;
            _notice.worldCamera = EtsVr.Head();

            var rect = (RectTransform)canvasObject.transform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(NoticeWidth, NoticeHeight);
            rect.localScale = Vector3.one * (PanelWidth / NoticeWidth);
            rect.localPosition = new Vector3(0f, PanelHeight, PanelDistance);
            rect.localRotation = Quaternion.identity;

            TMP_FontAsset font = EtsNameplate.FallbackFont();
            var plate = EtsGameUi.Place(canvasObject.transform, "plate", 0f, NoticeWidth, 0f, NoticeHeight).gameObject.AddComponent<Image>();
            plate.color = new Color(EtsGameUi.PanelColour.r, EtsGameUi.PanelColour.g, EtsGameUi.PanelColour.b, 0.95f);
            plate.raycastTarget = false;

            // The game's window accent: a thin orange rule over the title (clr_sel's orange; the locale is not there).
            var rule = EtsGameUi.Place(canvasObject.transform, "rule", 60f, NoticeWidth - 60f, 800f, 806f).gameObject.AddComponent<Image>();
            rule.color = new Color(0.98f, 0.69f, 0.01f);
            rule.raycastTarget = false;

            _noticeTitle = NoticeLabel(EtsGameUi.Place(canvasObject.transform, "title", 60f, NoticeWidth - 60f, 816f, 880f), font, 44f, new Color(0.98f, 0.69f, 0.01f));
            _noticeBody = NoticeLabel(EtsGameUi.Place(canvasObject.transform, "body", 60f, NoticeWidth - 60f, 430f, 785f), font, 31f, new Color(0.9f, 0.91f, 0.93f));
            _noticePlayers = NoticeLabel(EtsGameUi.Place(canvasObject.transform, "players", 60f, NoticeWidth - 60f, 30f, 415f), font, 29f, new Color(0.72f, 0.74f, 0.77f));
            _noticeTitle.textWrappingMode = TextWrappingModes.NoWrap;
            _noticeTitle.enableAutoSizing = true;
            _noticeTitle.fontSizeMin = 24f;
            _noticeTitle.fontSizeMax = 44f;
            _noticePlayers.richText = false;

            // Long words or a long list shrink to fit rather than being cut (probe 2261 saw the master line cut at a fixed size).
            _noticeBody.enableAutoSizing = true;
            _noticeBody.fontSizeMin = 20f;
            _noticeBody.fontSizeMax = 31f;
            _noticePlayers.enableAutoSizing = true;
            _noticePlayers.fontSizeMin = 16f;
            _noticePlayers.fontSizeMax = 29f;

            EtsLayers.SetRecursively(canvasObject.transform, EtsLayers.Ui);

            if (_placed)
            {
                PlacePanel("notice built");
            }

            Debug.Log("[SomniumEuroTruck] HQ stage: no-game notice built (font " + (font != null ? font.name : "NONE - TextMesh Pro's default") + ").");
        }

        private static TextMeshProUGUI NoticeLabel(RectTransform rect, TMP_FontAsset font, float size, Color colour)
        {
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();

            if (font != null)
            {
                label.font = font;
            }

            label.fontSize = size;
            label.color = colour;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            return label;
        }

        /// <summary>The panel in front of the player: the no-game notice when there is one, else the map.</summary>
        private Canvas Panel => _notice != null ? _notice : _canvas;

        /// <summary>Watchers: the master's hover, shown as that pin's pointed-at state (0: none).</summary>
        public void Highlight(ulong cityToken)
        {
            _highlightWanted = cityToken;

            if (_highlighted != null && _highlighted.Token == cityToken)
            {
                return;
            }

            if (_highlighted != null && _byToken.TryGetValue(_highlighted.Token, out Pin old) && old.Look != null)
            {
                old.Look.SetHighlighted(false);
            }

            _highlighted = null;

            if (cityToken != 0 && _byToken.TryGetValue(cityToken, out Pin pin) && pin.Look != null)
            {
                pin.Look.SetHighlighted(true);
                _highlighted = pin.City;
            }
        }

        /// <summary>The city chosen before the player picks (the career's last HQ, PLAN §5.5 step 4); kept until the pins exist.</summary>
        public void Preselect(ulong cityToken)
        {
            _preselect = cityToken;

            if (_pinsBuilt && _selected == null && _byToken.TryGetValue(cityToken, out Pin pin) && EtsHqStageContent.Available(pin.City))
            {
                Choose(pin.City, false);
            }
        }

        /// <summary>The map shown or hidden (EtsFlow hides it under the loading screen; the void stays until the arrival).</summary>
        public void SetMapShown(bool shown)
        {
            bool was = _mapShown;
            _mapShown = shown;

            if (_canvas != null && _canvas.gameObject.activeSelf != shown)
            {
                _canvas.gameObject.SetActive(shown);
            }

            // Shown again after the loading screen: brought round in front of the player once more.
            if (shown && !was)
            {
                _panelPlaced = false;
            }
        }

        /// <summary>Removes the void, the map and the stage (after the arrival teleport).</summary>
        public void Hide()
        {
            Debug.Log("[SomniumEuroTruck] HQ stage: hidden after " + (Time.realtimeSinceStartup - _shownAt).ToString("0.0") + " s" +
                      (_selected != null ? ", chosen " + _selected.Name : "") + (_putBack > 0 ? ", the player put back " + _putBack + " time(s)" : "") + ".");

            if (Current == this)
            {
                Current = null;
            }

            Destroy(gameObject);
        }

        // ------------------------------------------------------------------ building

        private void Build(bool interactive)
        {
            _shownAt = Time.realtimeSinceStartup;
            _interactive = interactive;
            transform.position = VoidCentre;
            BuildVoid();
            PlacePlayer();
            TryBuildMap();
            Debug.Log("[SomniumEuroTruck] HQ stage: the void at " + VoidCentre + " (" + (interactive ? "interactive" : "watching") + ")" +
                      (_screen != null ? ", map built at once" : ", map when the archives and the words are there") + ".");
        }

        private void BuildVoid()
        {
            _void = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _void.name = "Ets HQ Void";
            var collider = _void.GetComponent<Collider>();

            if (collider != null)
            {
                // The player stands inside it: a solid sphere would hold their capsule.
                collider.enabled = false;
                Destroy(collider);
            }

            _void.transform.SetParent(transform, false);
            _void.transform.localPosition = Vector3.zero;
            _void.transform.localScale = Vector3.one * (VoidRadius * 2f);

            // Inside out: the triangles turned round and the normals reversed, so the sphere is seen from within.
            var filter = _void.GetComponent<MeshFilter>();
            Mesh mesh = Instantiate(filter.sharedMesh);
            int[] triangles = mesh.triangles;

            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int t = triangles[i];
                triangles[i] = triangles[i + 1];
                triangles[i + 1] = t;
            }

            mesh.triangles = triangles;
            Vector3[] normals = mesh.normals;
            for (int i = 0; i < normals.Length; i++) normals[i] = -normals[i];
            mesh.normals = normals;
            filter.sharedMesh = mesh;
            _voidMesh = mesh;

            // Unlit black, as EtsJobs finds its unlit shader.
            var renderer = _void.GetComponent<MeshRenderer>();
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");

            if (shader != null)
            {
                var material = new Material(shader) { color = Color.black };
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.black);
                renderer.sharedMaterial = material;
                _voidMaterial = material;
            }

            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            // The floor the player stands on and the walls round it: colliders only, the void stays black underfoot.
            // Environment (23), outside EtsLayers.GroundMask (0, 14): the ground fill's and the spawns' rays must
            // never take the void's floor for the map (EtsGroundFill casts from 400 m above its focus), while the
            // avatar still stands on it - its capsule (layer 2) collides with 23 and the VR ground check includes
            // 23 (EtsLayers' header; DynamicsManager.asset, Player.XR.Features.prefab). F's request, Phase 1.
            Box("Ets HQ Floor", new Vector3(0f, -0.5f, 0f), new Vector3(FloorHalf * 2f, 1f, FloorHalf * 2f));
            Box("Ets HQ Wall N", new Vector3(0f, WallHeight / 2f, FloorHalf + 0.5f), new Vector3(FloorHalf * 2f + 2f, WallHeight, 1f));
            Box("Ets HQ Wall S", new Vector3(0f, WallHeight / 2f, -FloorHalf - 0.5f), new Vector3(FloorHalf * 2f + 2f, WallHeight, 1f));
            Box("Ets HQ Wall E", new Vector3(FloorHalf + 0.5f, WallHeight / 2f, 0f), new Vector3(1f, WallHeight, FloorHalf * 2f + 2f));
            Box("Ets HQ Wall W", new Vector3(-FloorHalf - 0.5f, WallHeight / 2f, 0f), new Vector3(1f, WallHeight, FloorHalf * 2f + 2f));
        }

        private void Box(string name, Vector3 centre, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.layer = EtsLayers.Environment;
            var box = go.AddComponent<BoxCollider>();
            box.center = centre;
            box.size = size;
        }

        /// <summary>The map canvas, once the archives are mounted and the words loaded (or 6 s on, without the words).</summary>
        private void TryBuildMap()
        {
            if (_screen != null || _canvas != null || EtsEngine.Files == null)
            {
                return;
            }

            if (!EtsLocale.Loaded && Time.realtimeSinceStartup - _shownAt < 6f)
            {
                return;
            }

            // The cold half of the build (fonts, templates, the map picture) prepared off the main thread first (EtsFlow's
            // prefetch), for at most 8 s: the first build in probe 2045 took 1,059 ms cold and 74 ms warm.
            if (ReadyToBuild != null && !ReadyToBuild() && Time.realtimeSinceStartup - _shownAt < 8f)
            {
                return;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            BuildCanvas();
            _view = new EtsHqMapView(EtsUiMapFrame.Read(EtsEngine.Files));
            _view.Fit(EtsHqCatalog.Ready ? EtsHqCatalog.Cities : null, 0.02f);

            var values = new Dictionary<string, string>(StringComparer.Ordinal) { { "title", "@@hq_t@@" } };
            _screen = EtsGameUi.BuildLayout(_frame, EtsHqStageContent.Layout, values, new[] { "tooltip" }, e => e.Type != "ui::overlay_point");

            if (_screen == null)
            {
                Debug.Log("[SomniumEuroTruck] HQ stage: " + EtsHqStageContent.Layout + " could not be read.");
                return;
            }

            _screen.OnClick(_confirmKey, Confirm);
            _screen.SetEnabled(_confirmKey, false);
            _screen.SetShown(_confirmKey, _interactive);
            BuildMap();
            BuildHintAndBanner();

            if (_interactive)
            {
                BuildBox();
            }

            if (EtsHqCatalog.Ready)
            {
                BuildPins();
            }

            EtsLayers.SetRecursively(_canvas.transform, EtsLayers.Ui);
            _canvas.gameObject.SetActive(_mapShown);
            watch.Stop();
            Debug.Log("[SomniumEuroTruck] HQ stage: map shown (" + (_interactive ? "interactive" : "watching") + ") after " + (Time.realtimeSinceStartup - _shownAt).ToString("0.0") +
                      " s, built in " + watch.ElapsedMilliseconds + " ms - " + _screen.Elements.Count + " elements of " + EtsHqStageContent.Layout + ", map " +
                      _view.MetresPerUnit.ToString("0") + " m per unit, words " + (EtsLocale.Loaded ? "from the locale" : "NOT loaded") + ", catalogue " +
                      (EtsHqCatalog.Ready ? EtsHqCatalog.Cities.Count + " cities" : "not ready (" + Mathf.RoundToInt(EtsHqCatalog.Progress * 100f) + " %)") + ".");
        }

        private void BuildCanvas()
        {
            var canvasObject = new GameObject("Ets HQ Map");
            canvasObject.transform.SetParent(transform, false);
            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.worldCamera = EtsVr.Head();
            canvasObject.AddComponent<GraphicRaycaster>();
            canvasObject.AddComponent<TrackedDeviceGraphicRaycaster>();

            var rect = (RectTransform)canvasObject.transform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(EtsGameUi.ReferenceWidth, EtsGameUi.ReferenceHeight);
            rect.localScale = Vector3.one * (PanelWidth / EtsGameUi.ReferenceWidth);
            rect.localPosition = new Vector3(0f, PanelHeight, PanelDistance);
            rect.localRotation = Quaternion.identity;

            _frame = EtsGameUi.Place(canvasObject.transform, "frame", 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);

            if (_placed)
            {
                PlacePanel("built");
            }
        }

        private void BuildMap()
        {
            RectTransform portal = _screen["portal"];

            if (portal == null)
            {
                return;
            }

            portal.gameObject.AddComponent<RectMask2D>();
            var catcher = portal.gameObject.AddComponent<Image>();
            catcher.color = new Color(0f, 0f, 0f, 0f);
            catcher.raycastTarget = true;
            portal.gameObject.AddComponent<EtsHqMapDrag>().Stage = this;

            _map = EtsGameUi.Place(portal, "map", 0f, portal.sizeDelta.x, 0f, portal.sizeDelta.y);

            // The whole map in one picture (/material/ui/map/map.mat, 2048 square): at this framing ~0.9 texel per frame unit,
            // and no seam - the four 2048 tiles have a lighter last texel row and column (alpha 182 against 193, probe 1896).
            _picture = EtsGameUi.Place(_map, "picture", 0f, 1f, 0f, 1f);
            EtsGameUi.Picture(_picture, EtsGameUi.Material(EtsHqMapView.Whole), false);

            _reading = EtsGameUi.Label(EtsGameUi.Place(_map, "reading", 0f, portal.sizeDelta.x, 0f, portal.sizeDelta.y),
                                       EtsHqStageContent.ReadingLine(EtsHqCatalog.Progress), EtsFont.Normal, TextAlignmentOptions.Center,
                                       EtsGameUi.Colour("clr_txt", EtsGameUi.TextColour));
            _reading.gameObject.SetActive(!EtsHqCatalog.Ready);
            Relayout();
        }

        private void BuildHintAndBanner()
        {
            // The controls hint where the overlay anchor is (30..296 x 87..138): the pan icon and "Move map". The icon is
            // given its own texture's shape at 34 units tall - not an AspectRatioFitter, whose FitInParent fits it to the
            // whole 1440 x 900 frame (probe 2045's first pictures: a mouse 790 units tall over the map).
            Texture2D pan = EtsGameUi.Material(EtsHqStageContent.PanIcon);
            float hintText = 72f;

            if (pan != null)
            {
                float width = 34f * pan.width / Mathf.Max(1, pan.height);
                EtsGameUi.Picture(EtsGameUi.Place(_frame, "hint icon", 34f, 34f + width, 95f, 129f), pan, false);
                hintText = 34f + width + 8f;
            }

            EtsGameUi.Label(EtsGameUi.Place(_frame, "hint", hintText, 296f, 97f, 127f), EtsLocale.Word("overlay_map_move"), EtsFont.Normal,
                            TextAlignmentOptions.Left, EtsGameUi.Colour("clr_txt", EtsGameUi.TextColour));

            _banner = EtsGameUi.Label(EtsGameUi.Place(_frame, "banner", 30f, 565f, 27f, 57f), _bannerText, EtsFont.Normal, TextAlignmentOptions.Left,
                                      EtsGameUi.Colour("clr_sel", EtsGameUi.SelectedColour));
            _banner.textWrappingMode = TextWrappingModes.NoWrap;
            _banner.enableAutoSizing = true;
            _banner.fontSizeMin = 10f;
            _banner.fontSizeMax = EtsFont.Size(EtsFont.Normal);
        }

        /// <summary>The opening message box (/ui/message_box.sii): "YOUR HEADQUARTERS", "Select the city you want to start in.", OK.</summary>
        private void BuildBox()
        {
            if (_boxShown || _frame == null)
            {
                return;
            }

            _boxShown = true;
            List<EtsUiElement> elements = EtsUiLayout.Read(EtsEngine.Files, EtsHqStageContent.MessageBox);
            EtsUiElement title = elements.Find(e => e.Look == "txt.title.center");
            EtsUiElement text = elements.Find(e => e.Type == "ui::link_text");
            EtsUiElement row = elements.Find(e => e.Type == "ui::button_row");

            _boxFrame = EtsGameUi.Place(_frame, "message box", 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);

            // The game's box is modal: until OK, the pointer reaches nothing under it (the obscurer's images take none).
            var modal = _boxFrame.gameObject.AddComponent<Image>();
            modal.color = new Color(0f, 0f, 0f, 0f);
            modal.raycastTarget = true;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (title != null) values[title.Key] = "@@hq_msg_t@@";

            // The engine fills the link text and the button row; the rest is the file's.
            EtsGameScreen box = EtsGameUi.BuildLayout(_boxFrame, EtsHqStageContent.MessageBox, values, null,
                                                      e => e.Type != "ui::link_text" && e.Type != "ui::button_row" && e.Type != "ui::portal" && e.Type != "ui::slider_common");

            if (box == null)
            {
                Destroy(_boxFrame.gameObject);
                _boxFrame = null;
                return;
            }

            box.OnClick("close.button", CloseBox);

            if (text != null)
            {
                TextMeshProUGUI words = EtsGameUi.Label(EtsGameUi.Place(_boxFrame, "text", text.Left, text.Right, text.Bottom, text.Top), EtsLocale.Word("hq_msg"),
                                                        EtsFont.Normal, TextAlignmentOptions.Center, EtsGameUi.Colour("clr_txt", EtsGameUi.TextColour));
                words.richText = true;
            }

            if (row != null)
            {
                // button_row: centred, dynamic width, min_item_width 100, label_indent 25 - one OK button 100 wide.
                float middle = (row.Left + row.Right) / 2f;
                EtsGameUi.Template(_boxFrame, "btn.normal", "@@ok@@", middle - 50f, middle + 50f, row.Bottom, row.Top, CloseBox);
            }

            if (_canvas != null)
            {
                EtsLayers.SetRecursively(_canvas.transform, EtsLayers.Ui);
            }

            Debug.Log("[SomniumEuroTruck] HQ stage: message box \"" + EtsLocale.Word("hq_msg_t") + "\" shown.");
        }

        private void CloseBox()
        {
            if (_boxFrame != null)
            {
                Destroy(_boxFrame.gameObject);
                _boxFrame = null;
                EtsGameUi.Note("HQ: message box closed");
            }
        }

        private void BuildPins()
        {
            _pinsBuilt = true;
            _view.Fit(EtsHqCatalog.Cities, 0.02f);

            if (_reading != null)
            {
                _reading.gameObject.SetActive(false);
            }

            if (_map == null)
            {
                return;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            Color labelColour = EtsGameUi.Colour("clr_txt", EtsGameUi.TextColour);
            int available = 0;

            // Names first, so every dot lies over every name.
            foreach (EtsHqCity city in EtsHqCatalog.Cities)
            {
                var pin = new Pin { City = city };
                pin.Label = EtsGameUi.Place(_map, city.Id + " name", 0f, 200f, 0f, 24f);
                TextMeshProUGUI name = EtsGameUi.Label(pin.Label, city.Name, EtsFont.Normal, TextAlignmentOptions.TopLeft, labelColour);
                name.textWrappingMode = TextWrappingModes.NoWrap;
                _pins.Add(pin);
                _byToken[city.Token] = pin;
            }

            foreach (Pin pin in _pins)
            {
                EtsHqCity city = pin.City;
                pin.Rect = EtsGameUi.Template(_map, EtsHqStageContent.PinLook, EtsHqStageContent.PinValue(_view, city), 0f, EtsHqStageContent.PinBox, 0f,
                                              EtsHqStageContent.PinBox, () => Choose(city, true));

                if (pin.Rect == null)
                {
                    continue;
                }

                pin.Rect.name = city.Id;
                pin.Look = pin.Rect.GetComponent<EtsUiLook>();

                if (pin.Look != null)
                {
                    pin.Look.Hovered += over => OnPointer(city, over);
                }

                bool open = EtsHqStageContent.Available(city);
                if (open) available++;
                EtsGameUi.SetEnabled(pin.Rect, open);
                var button = pin.Rect.GetComponent<Button>();
                if (button != null) button.interactable = _interactive && open;
            }

            Relayout();
            EtsLayers.SetRecursively(_canvas.transform, EtsLayers.Ui);

            if (_preselect != 0)
            {
                Preselect(_preselect);
            }

            if (_highlightWanted != 0)
            {
                Highlight(_highlightWanted);
            }

            watch.Stop();
            Debug.Log("[SomniumEuroTruck] HQ stage: " + _pins.Count + " cities on the map (" + available + " to choose; " + (_pins.Count - available) +
                      " greyed: no yard the map viewer can build), " + _view.MetresPerUnit.ToString("0") + " m per unit, built in " + watch.ElapsedMilliseconds +
                      " ms, " + (Time.realtimeSinceStartup - _shownAt).ToString("0.0") + " s after the stage.");
        }

        /// <summary>The map picture, dots and names at the current framing (after a build or a drag).</summary>
        internal void Relayout()
        {
            if (_map == null)
            {
                return;
            }

            _view.WholeBounds(out float l, out float r, out float b, out float t);
            _picture.anchoredPosition = new Vector2(l - _view.PortalLeft, b - _view.PortalBottom);
            _picture.sizeDelta = new Vector2(r - l, t - b);

            foreach (Pin pin in _pins)
            {
                Vector2 at = _view.ToFrame(pin.City.U, pin.City.V) - new Vector2(_view.PortalLeft, _view.PortalBottom);

                if (pin.Rect != null)
                {
                    pin.Rect.anchoredPosition = at - new Vector2(EtsHqStageContent.PinBox / 2f, EtsHqStageContent.PinBox / 2f);
                }

                // map_x/y_offsets[7] from the dot to the name's top-left corner, y down (a reading of the numbers:
                // Szczecin's -38/-26 put its name above the dot, left of centre).
                pin.Label.anchoredPosition = new Vector2(at.x + pin.City.LabelDx, at.y - pin.City.LabelDy - pin.Label.sizeDelta.y);
            }

            if (_tooltip != null && _hovered != null)
            {
                PlaceTooltip(_hovered);
            }
        }

        internal void PanBy(Vector2 frameDelta)
        {
            _view.Pan(frameDelta.x, frameDelta.y);
            Relayout();
        }

        // ------------------------------------------------------------------ the pointer

        private void OnPointer(EtsHqCity city, bool over)
        {
            if (over)
            {
                if (_hovered != city)
                {
                    Debug.Log("[SomniumEuroTruck] HQ: pointer on " + city.Name + " (" + city.CountryName + ")" + (EtsVr.Present ? " - VR ray" : " - mouse"));
                }

                _hovered = city;
                ShowTooltip(city);
                Hovered?.Invoke(city);
                return;
            }

            if (_hovered == city)
            {
                _hovered = null;

                if (_tooltip != null)
                {
                    Destroy(_tooltip.gameObject);
                    _tooltip = null;
                }
            }
        }

        private void ShowTooltip(EtsHqCity city)
        {
            if (_tooltip != null)
            {
                Destroy(_tooltip.gameObject);
            }

            string value = EtsHqStageContent.TooltipValue(city);

            if (!EtsHqStageContent.Available(city))
            {
                value += "<br>Not yet available"; // not in the locale
            }

            _tooltip = EtsGameUi.Template(_frame, "txt.ttip_simple", value, 0f, 262f, 0f, EtsHqStageContent.Available(city) ? 60f : 84f);

            if (_tooltip != null)
            {
                _tooltip.name = "tooltip";

                // The tooltip must not take the pointer from the pin under it (it is drawn where the pointer is).
                foreach (Graphic g in _tooltip.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;

                EtsLayers.SetRecursively(_tooltip, EtsLayers.Ui);
                PlaceTooltip(city);
            }
        }

        /// <summary>The tooltip above and right of the dot, kept inside the frame (the game moves it with the pointer).</summary>
        private void PlaceTooltip(EtsHqCity city)
        {
            Vector2 at = _view.ToFrame(city.U, city.V);
            Vector2 size = _tooltip.sizeDelta;
            float x = Mathf.Clamp(at.x + 16f, 7f, EtsGameUi.ReferenceWidth - 7f - size.x);
            float y = Mathf.Clamp(at.y + 16f, 7f, EtsGameUi.ReferenceHeight - 7f - size.y);
            _tooltip.anchoredPosition = new Vector2(x, y);
        }

        private void Choose(EtsHqCity city, bool byPlayer)
        {
            if (city == null || (byPlayer && !_interactive) || !EtsHqStageContent.Available(city))
            {
                return;
            }

            if (_selected != null && _byToken.TryGetValue(_selected.Token, out Pin old))
            {
                EtsGameUi.SetChosen(old.Rect, false);
            }

            _selected = city;

            if (_byToken.TryGetValue(city.Token, out Pin pin))
            {
                EtsGameUi.SetChosen(pin.Rect, true);
            }

            _screen?.SetEnabled(_confirmKey, _interactive && !_confirmed);

            if (byPlayer)
            {
                EtsGameUi.Note("HQ: chose " + city.Name + " (" + city.CountryName + ")");
                Hovered?.Invoke(city);
            }
        }

        private void Confirm()
        {
            if (_selected == null || !_interactive || _confirmed)
            {
                return;
            }

            _confirmed = true;
            _screen?.SetEnabled(_confirmKey, false);
            Debug.Log("[SomniumEuroTruck] HQ: confirmed " + _selected.Name + " (" + _selected.CountryName + ") - yard " + _selected.YardModel + ", " + _selected.SpawnSpots + " stands, " +
                      (Time.realtimeSinceStartup - _shownAt).ToString("0.0") + " s after the stage.");
            Confirmed?.Invoke(_selected);
        }

        /// <summary>F8 (Update): the message box first, as OK would; then Confirm, as "Confirm selection" would.</summary>
        private void ConfirmByKey()
        {
            if (_boxFrame != null)
            {
                Debug.Log("[SomniumEuroTruck] HQ stage: F8 - message box closed.");
                CloseBox();
                return;
            }

            Debug.Log("[SomniumEuroTruck] HQ stage: F8 - " + (_selected != null ? "confirming " + _selected.Name + "." : "no city is selected yet (the pins are still coming)."));
            Confirm();
        }

        /// <summary>A confirmation the session did not take: Confirm is live again (EtsFlow).</summary>
        public void Unconfirm()
        {
            _confirmed = false;
            _screen?.SetEnabled(_confirmKey, _interactive && _selected != null);
        }

        // ------------------------------------------------------------------ the player

        private static Transform PlayerRoot()
        {
            try
            {
                return SomniumBridge.PlayersContainer?.LocalPlayer?.References?.Body?.Root;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void PlacePlayer()
        {
            if (_placed)
            {
                return;
            }

            // Onto the floor, facing +z.
            if (EtsVr.Teleport(VoidCentre + new Vector3(0f, 0.05f, 0f), 0f))
            {
                _placed = true;
                _placedAt = Time.realtimeSinceStartup;
                _lastTeleport = _placedAt;
                _panelPlaced = false;
                Debug.Log("[SomniumEuroTruck] HQ stage: player placed at " + VoidCentre + " after " + (Time.realtimeSinceStartup - _shownAt).ToString("0.0") + " s.");
                return;
            }

            // The editor has no Bridge player (EtsVr.Motion is null there, and the old front end's cameras were the
            // editor's own): its main camera is stood where a player's eyes would be, so the stage can be looked at and
            // clicked in Play mode. Never in the client - there the Bridge player arrives and the teleport above wins.
            if (Application.isEditor && !_editorCamera && Time.realtimeSinceStartup - _shownAt > 1f)
            {
                Camera eye = Camera.main;

                if (eye != null)
                {
                    _editorCamera = true;
                    eye.transform.SetPositionAndRotation(VoidCentre + new Vector3(0f, 1.7f, 0f), Quaternion.identity);
                    _placed = true;
                    _placedAt = Time.realtimeSinceStartup;
                    _lastTeleport = _placedAt;
                    _panelPlaced = false;
                    Debug.Log("[SomniumEuroTruck] HQ stage: no Bridge player (the editor) - the camera '" + eye.name + "' was stood in the void.");
                }
            }
        }

        /// <summary>While KeepPlayer: the body more than KeepRadius from the centre, or below the floor, is put back (class notes).</summary>
        private void KeepInVoid()
        {
            if (!KeepPlayer || !_placed || _editorCamera || Time.realtimeSinceStartup - _lastTeleport < 1.5f)
            {
                return;
            }

            Transform root = PlayerRoot();

            if (root == null)
            {
                return;
            }

            Vector3 p = root.position;
            Vector3 d = p - VoidCentre;
            bool away = new Vector2(d.x, d.z).magnitude > KeepRadius || d.y < -2f || d.y > 20f;

            if (!away)
            {
                return;
            }

            if (EtsVr.Teleport(VoidCentre + new Vector3(0f, 0.05f, 0f), 0f))
            {
                _putBack++;
                _lastTeleport = Time.realtimeSinceStartup;
                _placedAt = _lastTeleport;
                _panelPlaced = false;

                if (_putBack <= 10)
                {
                    Debug.Log("[SomniumEuroTruck] HQ stage: the player was at " + p + ", " + d.magnitude.ToString("0") + " m from the void - put back (" + _putBack + ").");
                }
            }
        }

        /// <summary>The map in front of the camera the player looks through (class notes: upright at eye height in a headset, square to the camera on a monitor).</summary>
        private void PlacePanel(string why)
        {
            Canvas panel = Panel;

            if (panel == null)
            {
                return;
            }

            Camera head = EtsVr.Head();

            if (head == null)
            {
                return;
            }

            Vector3 eye = head.transform.position;

            // Kept inside the void: an eye far outside it (the camera still at the spawn) places nothing.
            if ((eye - VoidCentre).magnitude > VoidRadius - 4f)
            {
                return;
            }

            bool vr = EtsVr.Present;
            Vector3 forward = head.transform.forward;
            float distance = vr ? PanelDistance : EtsHqStageContent.DesktopDistance(head.fieldOfView, head.aspect);

            if (vr)
            {
                forward.y = 0f;
            }
            else
            {
                // Level to 35 degrees at most: an orbit camera looking steeply down at the avatar would lay the map on the floor.
                forward.y = Mathf.Clamp(forward.y, -0.57f, 0.57f);
            }

            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            Vector3 at = eye + forward * distance;

            if (vr)
            {
                at.y = Mathf.Clamp(eye.y, VoidCentre.y + 0.9f, VoidCentre.y + 2.4f);
            }
            else
            {
                at.y = Mathf.Clamp(at.y, VoidCentre.y + PanelTall * 0.5f + 0.1f, VoidCentre.y + VoidRadius - PanelTall);
            }

            panel.transform.position = at;
            panel.transform.rotation = Quaternion.LookRotation(vr ? forward : (at - eye).normalized, Vector3.up);
            panel.worldCamera = head;
            _panelPlaced = true;
            _awayFor = 0f;
            Debug.Log("[SomniumEuroTruck] HQ stage: " + (panel == _notice ? "no-game notice" : "map") + " placed " + distance.ToString("0.00") + " m in front of the " + (vr ? "headset" : "camera (" + head.fieldOfView.ToString("0") + " degrees, aspect " +
                      head.aspect.ToString("0.00") + ")") + " (" + why + "), eye at " + (eye - VoidCentre).ToString("F1") + " from the centre.");
        }

        /// <summary>Brings the map round when the player has looked well away from it for a while, or walked far from it.</summary>
        private void KeepPanelInView(float step)
        {
            Canvas panel = Panel;

            if (panel == null || (panel == _canvas && !_mapShown) || !_placed)
            {
                return;
            }

            if (!_panelPlaced)
            {
                if (Time.realtimeSinceStartup - _placedAt > 0.6f)
                {
                    PlacePanel(_putBack > 0 ? "after putting the player back" : "the player arrived");
                }

                return;
            }

            Camera head = EtsVr.Head();

            if (head == null)
            {
                return;
            }

            Vector3 to = panel.transform.position - head.transform.position;
            Vector3 forward = head.transform.forward;
            to.y = 0f;
            forward.y = 0f;

            if (to.magnitude > 7f || to.magnitude < 0.8f)
            {
                PlacePanel(to.magnitude > 7f ? "the player moved away" : "the player walked into it");
                return;
            }

            bool away = forward.sqrMagnitude > 1e-4f && Vector3.Angle(forward, to) > 100f;
            _awayFor = away ? _awayFor + step : 0f;

            if (_awayFor > 4f)
            {
                PlacePanel("the player looked away");
            }
        }

        private void Update()
        {
            // F8 (Phase 2 integration): a way on through the keyboard for U5, which only the client can prove. Should the
            // world-space map not take the desktop mouse, the master would hold every player in the void; F8 closes the
            // message box, then confirms the selected city (the career's last headquarters or Szczecin is preselected,
            // EtsFlow.Preselect). Not Enter: Enter also ends a chat message, and a message sent in the void would choose.
            // Polled every frame, since GetKeyDown lasts one frame; one bool test while the key is up.
            if (_interactive && Input.GetKeyDown(KeyCode.F8))
            {
                ConfirmByKey();
            }

            if (Time.unscaledTime < _nextTick)
            {
                return;
            }

            const float step = 0.25f;
            _nextTick = Time.unscaledTime + step;

            // U17: retried until the Bridge's local player exists; then kept in the void while asked to.
            PlacePlayer();
            KeepInVoid();
            TryBuildMap();
            KeepPanelInView(step);

            if (_canvas != null && (_canvas.worldCamera == null || !_canvas.worldCamera.isActiveAndEnabled))
            {
                _canvas.worldCamera = EtsVr.Head();
            }

            if (_screen != null && !_pinsBuilt)
            {
                if (EtsHqCatalog.Ready)
                {
                    BuildPins();
                }
                else if (_reading != null)
                {
                    string line = EtsHqStageContent.ReadingLine(EtsHqCatalog.Progress);
                    if (_reading.text != line) _reading.text = line;
                }
            }
        }

        private void OnDestroy()
        {
            if (Current == this)
            {
                Current = null;
            }

            // The inside-out sphere's mesh and its material were made here, not loaded: destroying the GameObject
            // does not free them.
            if (_voidMesh != null) Destroy(_voidMesh);
            if (_voidMaterial != null) Destroy(_voidMaterial);
        }
    }

    /// <summary>Drags the HQ map (the portal takes the pointer): the picture follows it, in frame units.</summary>
    internal sealed class EtsHqMapDrag : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        public EtsHqStage Stage;

        public void OnBeginDrag(PointerEventData eventData)
        {
        }

        public void OnDrag(PointerEventData eventData)
        {
            var rect = (RectTransform)transform;

            if (Stage == null ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position, eventData.pressEventCamera, out Vector2 now) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position - eventData.delta, eventData.pressEventCamera, out Vector2 before))
            {
                return;
            }

            Stage.PanBy(now - before);
        }
    }
}
