using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// One of the world's screens, hung in the room for a headset.
    ///
    /// A screen space overlay has nowhere to go in a headset - Unity draws it to the desktop
    /// mirror and not to the eyes - so in VR every canvas the world builds is turned into the
    /// same canvas in world space, the way the AoM and Quake worlds do it:
    ///
    ///   - the UI layer (5) on the canvas and everything under it, because the client's ray is
    ///     masked by layer and AoM's panel only became pressable once all of it was on 5;
    ///   - a TrackedDeviceGraphicRaycaster beside the GraphicRaycaster, which is what answers the
    ///     client's XR ray (AoM: "the interface became pressable the moment it was given" one);
    ///   - the scaler switched off, since there is no screen for it to fit and left on it fights
    ///     the size set here (Q3MenuUi.Prepare);
    ///   - worldCamera asked for again twice a second, because the client's camera arrives late
    ///     and can be swapped (AomUiPanel.Update).
    ///
    /// Placed upright a fixed distance in front of the player and left there, facing them - a
    /// panel welded to the head cannot be looked at (Q3MenuUi.PlaceInWorld). If the player is
    /// moved far from it (seated in the truck, got out), it comes round in front again.
    ///
    /// Everything laid out in the game's 1440 x 900 frame stays exactly where it was: the canvas
    /// is given that size, so the frame's anchors land on its corners.
    /// </summary>
    public sealed class EtsVrPanel : MonoBehaviour
    {
        /// <summary>How far in front of the eyes a panel hangs, in metres.</summary>
        public const float Distance = 0.8f;

        /// <summary>How wide the 1440-unit frame is, in metres: about sixty degrees at that distance.</summary>
        public const float Width = 0.95f;

        /// <summary>How far the head may move from where a panel was placed before it is brought round again.</summary>
        private const float Follow = 2.5f;

        private Canvas _canvas;
        private bool _interactive;
        private bool _placed;
        private bool _attached;
        private Vector3 _placedFrom;
        private float _nextCheck;

        /// <summary>
        /// Turns a canvas into a VR panel when a headset is running; otherwise leaves it alone and
        /// returns null. Call it as the canvas is made, before its contents are.
        /// </summary>
        public static EtsVrPanel Adopt(Canvas canvas, bool interactive)
        {
            return Adopt(canvas, interactive, new Vector2(EtsGameUi.ReferenceWidth, EtsGameUi.ReferenceHeight));
        }

        public static EtsVrPanel Adopt(Canvas canvas, bool interactive, Vector2 frame)
        {
            if (canvas == null || !EtsVr.Present)
            {
                return null;
            }

            EtsVrPanel panel = canvas.GetComponent<EtsVrPanel>();

            if (panel == null)
            {
                panel = canvas.gameObject.AddComponent<EtsVrPanel>();
            }

            panel.Convert(interactive, frame);
            return panel;
        }

        private void Convert(bool interactive, Vector2 frame)
        {
            _canvas = GetComponent<Canvas>();
            _interactive = interactive;
            _canvas.renderMode = RenderMode.WorldSpace;

            var scaler = GetComponent<CanvasScaler>();

            if (scaler != null)
            {
                scaler.enabled = false;
            }

            var rect = (RectTransform)transform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = frame;
            rect.localScale = Vector3.one * (Width / EtsGameUi.ReferenceWidth);

            if (interactive && GetComponent<TrackedDeviceGraphicRaycaster>() == null)
            {
                gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
            }

            EtsLayers.SetRecursively(transform, EtsLayers.Ui);
            _canvas.worldCamera = EtsVr.Head();
            Face();
        }

        private void Start()
        {
            // The contents exist by now. Sheets stretched thousands of units past the frame, to
            // cover any screen shape, would be a wall metres wide in a room: fit them to the frame.
            foreach (RectTransform child in GetComponentsInChildren<RectTransform>(true))
            {
                if (child != transform && child.anchorMin == Vector2.zero && child.anchorMax == Vector2.one &&
                    (child.offsetMin.x < -500f || child.offsetMax.x > 500f))
                {
                    child.offsetMin = Vector2.zero;
                    child.offsetMax = Vector2.zero;
                }
            }

            EtsLayers.SetRecursively(transform, EtsLayers.Ui);
        }

        private void OnEnable()
        {
            if (_canvas != null && !_attached)
            {
                Face();
            }
        }

        /// <summary>Brings the panel round in front of the player, upright, at the fixed distance.</summary>
        public void Face()
        {
            if (_attached)
            {
                return;
            }

            Camera head = EtsVr.Head();

            if (head == null)
            {
                _placed = false;
                return;
            }

            _canvas.worldCamera = head;
            Vector3 forward = head.transform.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();

            // A panel drawn over another sits a hair nearer, so the two never fight for depth and
            // the one on top is the one the ray meets first.
            float distance = Distance - _canvas.sortingOrder * 0.0001f;
            transform.position = head.transform.position + forward * distance;
            transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            _placedFrom = head.transform.position;
            _placed = true;
        }

        /// <summary>
        /// Fixes the panel to something that moves - the truck's dashboard - rather than the room.
        /// focus is the point in the canvas frame (frame units, from its bottom left) that is put
        /// at the anchor point; the panel faces lookFrom; frameWidth is the whole frame's width in
        /// metres.
        /// </summary>
        public void AttachTo(Transform anchor, Vector3 localPoint, Vector3 lookFromLocal, Vector2 focus, float frameWidth)
        {
            _attached = true;
            var rect = (RectTransform)transform;
            transform.SetParent(anchor, false);
            float scale = frameWidth / rect.sizeDelta.x;
            rect.localScale = Vector3.one * scale;
            Vector3 facing = localPoint - lookFromLocal;
            rect.localRotation = Quaternion.LookRotation(facing.sqrMagnitude > 1e-6f ? facing : Vector3.forward, Vector3.up);
            Vector3 offset = new Vector3(focus.x - rect.sizeDelta.x * 0.5f, focus.y - rect.sizeDelta.y * 0.5f, 0f) * scale;
            rect.localPosition = localPoint - rect.localRotation * offset;
            EtsLayers.SetRecursively(transform, EtsLayers.Ui);
        }

        private void Update()
        {
            if (_canvas == null || Time.unscaledTime < _nextCheck)
            {
                return;
            }

            _nextCheck = Time.unscaledTime + 0.5f;

            if (_canvas.worldCamera == null || !_canvas.worldCamera.isActiveAndEnabled)
            {
                _canvas.worldCamera = EtsVr.Head();
            }

            if (!_attached)
            {
                Camera head = _canvas.worldCamera;

                if (!_placed || (head != null && (head.transform.position - _placedFrom).sqrMagnitude > Follow * Follow))
                {
                    Face();
                }
            }

            // Rows and labels added after the screen was first built (the job list filling in)
            // need the UI layer too, or the ray passes straight through them.
            if (_interactive)
            {
                EtsLayers.SetRecursively(transform, EtsLayers.Ui);
            }
        }
    }

    /// <summary>
    /// The speed, gear and job, on a small plate above the steering wheel, for a headset. On a
    /// monitor the status line in the corner of the screen says this; in a headset there is no
    /// corner of the screen.
    ///
    /// In the new flow the plate also carries the master indicator (PLAN.md §3.7 item 2, owner D): a strip on top of it
    /// with the game's orange star (/material/ui/icon/star_orange.mat, painted by EtsGameUi.SdfIcon) and "Instance
    /// master" in clr_sel while EtsSession says this player is the master; the words are not in the locale. The
    /// desktop's badge is EtsFlow's (EtsMasterBadge). EtsRouteAdvisor attaches a plate to each truck the local player
    /// sits in.
    /// </summary>
    public sealed class EtsVrDash : MonoBehaviour
    {
        private EtsTruckController _truck;
        private EtsJobs _jobs;
        private TextMeshProUGUI _text;
        private GameObject _master;
        private float _next;
        private string _shown;

        /// <summary>The plate (px, before the 0.001 scale) and the text's largest size; FitText shrinks it to the longest line.</summary>
        private const float PlateWidth = 460f, PlateHeight = 112f, MaxFont = 26f, MinFont = 11f;

        /// <summary>Builds the plate on the truck when a headset is running; otherwise does nothing.</summary>
        public static EtsVrDash Attach(EtsTruckController truck, EtsJobs jobs, TMP_FontAsset font)
        {
            if (truck == null || !EtsVr.Present)
            {
                return null;
            }

            var host = new GameObject("Ets VR dash", typeof(RectTransform));
            var canvas = host.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)host.transform;
            rect.sizeDelta = new Vector2(PlateWidth, PlateHeight);
            rect.SetParent(truck.transform, false);
            rect.localScale = Vector3.one * 0.001f;
            Vector3 eye = truck.DriverEyeLocal;
            Vector3 at = eye + new Vector3(0f, -0.27f, 0.58f);
            rect.localPosition = at;
            rect.localRotation = Quaternion.LookRotation(at - eye, Vector3.up);

            var plate = new GameObject("plate", typeof(RectTransform)).AddComponent<Image>();
            plate.rectTransform.SetParent(rect, false);
            plate.rectTransform.anchorMin = Vector2.zero;
            plate.rectTransform.anchorMax = Vector2.one;
            plate.rectTransform.offsetMin = plate.rectTransform.offsetMax = Vector2.zero;
            plate.color = new Color(EtsGameUi.PanelColour.r, EtsGameUi.PanelColour.g, EtsGameUi.PanelColour.b, 0.85f);
            plate.raycastTarget = false;

            var text = new GameObject("text", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            text.rectTransform.SetParent(rect, false);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(12f, 6f);
            text.rectTransform.offsetMax = new Vector2(-12f, -6f);

            if (font != null)
            {
                text.font = font;
            }

            // Sized by FitText, not TMP's auto-size: with word wrap on, auto-size let the damage lamp words run past the
            // plate (the user's 2026-10-29 screenshot). Lines break only where the dash puts a break.
            text.fontSize = MaxFont;
            text.enableAutoSizing = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Truncate;
            text.alignment = TextAlignmentOptions.Center;
            text.color = new Color(0.9f, 0.91f, 0.93f);
            text.raycastTarget = false;

            var dash = host.AddComponent<EtsVrDash>();
            dash._truck = truck;
            dash._jobs = jobs;
            dash._text = text;

            if (EtsFeatures.NewFlow)
            {
                dash._master = MasterStrip(rect, font);
            }

            EtsLayers.SetRecursively(host.transform, EtsLayers.Ui);
            return dash;
        }

        /// <summary>The master strip over the plate (class notes): 420 x 30 above its top edge, hidden until the session says so.</summary>
        private static GameObject MasterStrip(RectTransform plate, TMP_FontAsset font)
        {
            var strip = new GameObject("master", typeof(RectTransform)).GetComponent<RectTransform>();
            strip.SetParent(plate, false);
            strip.anchorMin = new Vector2(0f, 1f);
            strip.anchorMax = new Vector2(1f, 1f);
            strip.pivot = new Vector2(0.5f, 0f);
            strip.anchoredPosition = new Vector2(0f, 4f);
            strip.sizeDelta = new Vector2(0f, 30f);

            var back = strip.gameObject.AddComponent<Image>();
            back.color = new Color(EtsGameUi.PanelColour.r, EtsGameUi.PanelColour.g, EtsGameUi.PanelColour.b, 0.85f);
            back.raycastTarget = false;

            Texture2D star = EtsGameUi.SdfIcon("/material/ui/icon/star_orange.mat", 26, 25);

            if (star != null)
            {
                var icon = new GameObject("star", typeof(RectTransform)).AddComponent<RawImage>();
                icon.rectTransform.SetParent(strip, false);
                icon.rectTransform.anchorMin = icon.rectTransform.anchorMax = new Vector2(0f, 0.5f);
                icon.rectTransform.pivot = new Vector2(0f, 0.5f);
                icon.rectTransform.anchoredPosition = new Vector2(12f, 0f);
                icon.rectTransform.sizeDelta = new Vector2(26f, 25f);
                icon.texture = star;
                icon.raycastTarget = false;
            }

            var words = new GameObject("words", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            words.rectTransform.SetParent(strip, false);
            words.rectTransform.anchorMin = Vector2.zero;
            words.rectTransform.anchorMax = Vector2.one;
            words.rectTransform.offsetMin = new Vector2(46f, 0f);
            words.rectTransform.offsetMax = new Vector2(-12f, 0f);
            if (font != null) words.font = font;
            words.fontSize = 22f;
            words.alignment = TextAlignmentOptions.Left;
            words.textWrappingMode = TextWrappingModes.NoWrap;
            words.color = EtsGameUi.Colour("clr_sel", new Color(0.98f, 0.69f, 0.01f));
            words.text = "Instance master"; // not in the locale (PLAN §3.7)
            words.raycastTarget = false;

            strip.gameObject.SetActive(false);
            return strip.gameObject;
        }

        private void Update()
        {
            if (_truck == null || _text == null || Time.unscaledTime < _next)
            {
                return;
            }

            _next = Time.unscaledTime + 0.1f;

            if (_master != null)
            {
                EtsSession session = EtsSession.Instance;
                bool master = session != null && session.IsMaster && session.Current == EtsPhase.InWorld;

                if (_master.activeSelf != master)
                {
                    _master.SetActive(master);
                }
            }

            string line = Mathf.RoundToInt(_truck.SpeedKmh) + " km/h   gear " + _truck.GearLabel + EtsFines.DashNote(_truck) + // the speed limit in force (EtsFines, 2026-10-04)
                          (_truck.ParkingBrake ? "   <color=#ffb040>PARKING BRAKE</color>" : "") + _truck.DashNote;

            // The game's damage widget figures (EtsDamage, 2026-10-04) on their own line, so the lamp words never widen line one.
            string damage = EtsDamage.IsOwn(_truck) ? EtsDamage.HudNote : "";

            if (damage.Length > 0)
            {
                line += "\n" + damage.Trim();
            }

            string notice = _truck.Notice;

            if (notice != null)
            {
                line += "\n<color=#ffb040>" + notice + "</color>";
            }
            else if (_jobs != null && _jobs.Current != null)
            {
                float remaining = _jobs.Route != null && _jobs.Route.Visible ? _jobs.Route.RemainingDistance(_truck.transform.position) : -1f;
                line += "\n" + _jobs.Current.CargoName + " to " + _jobs.Current.ToCityName + (remaining >= 0f ? "   " + _jobs.Current.ToGameKm(remaining).ToString("0.0") + " km" : "");
            }

            if (!string.Equals(line, _shown))
            {
                _shown = line;
                FitText(_text, line);
            }
        }

        /// <summary>
        /// Sets the text at the largest size (MaxFont down to MinFont) at which every line fits the plate, measured with
        /// TMP's own layout (GetPreferredValues) - only when the text changes, at most ten times a second.
        /// </summary>
        private static void FitText(TextMeshProUGUI text, string line)
        {
            Rect area = text.rectTransform.rect;
            float width = area.width > 1f ? area.width : PlateWidth - 24f;
            float height = area.height > 1f ? area.height : PlateHeight - 12f;
            text.fontSize = MaxFont;
            Vector2 need = text.GetPreferredValues(line, float.PositiveInfinity, float.PositiveInfinity);
            float scale = 1f;
            if (need.x > width) scale = Mathf.Min(scale, width / need.x);
            if (need.y > height) scale = Mathf.Min(scale, height / need.y);
            text.fontSize = Mathf.Max(MinFont, Mathf.Floor(MaxFont * scale * 0.97f));
            text.text = line;
        }
    }
}
