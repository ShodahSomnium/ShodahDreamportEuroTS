using System;
using SomniumSpace.Bridge;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The sizes and rules of the marker over the local player's own truck (EtsTruckMarker), pure so a probe can check them.
    ///
    /// Readable in a headset the way the nameplates are (EtsNameplateRules): the pin is PinHeight x UnitsToMetres = 0.95 m
    /// tall and the words' glyphs 0.32 m at scale 1, which is 1.5 degrees of view at NearDistance (12 m). Beyond 12 m the
    /// marker grows with the distance (Scale), so it keeps that size in view out to 96 m (MaxScale 8, a 7.6 m pin), then
    /// shrinks like everything else and goes at HideBeyond.
    /// </summary>
    public static class EtsTruckMarkerRules
    {
        /// <summary>The pin's tip above the truck's origin, in world up: clear of a 4 m cab roof and above a remote truck's nameplate height (4.6 m).</summary>
        public const float Height = 5.2f;

        /// <summary>The canvas's metres per unit at scale 1 (the nameplate's).</summary>
        public const float UnitsToMetres = 0.005f;

        /// <summary>Canvas units: the pin (the game's /material/ui/adviser/checkpoint, 14 x 19), its dark rim, the words' sizes and the plate round them.</summary>
        public const float PinWidth = 140f, PinHeight = 190f, Rim = 12f, WordsSize = 64f, DistanceSize = 44f, WordsLine = 80f, DistanceLine = 54f, PadX = 28f, PadY = 10f, Gap = 16f;

        /// <summary>Hidden while the player stands nearer than HideNearer to the truck, shown again beyond ShowFarther (metres, across the ground).</summary>
        public const float HideNearer = 6f, ShowFarther = 7f;

        /// <summary>Closer than this (to the viewer's eyes) the marker keeps its size; farther, it grows with the distance, up to MaxScale.</summary>
        public const float NearDistance = 12f;

        public const float MaxScale = 8f;

        /// <summary>Not drawn beyond this from the viewer.</summary>
        public const float HideBeyond = 2500f;

        /// <summary>The bob: metres up and down at scale 1, and how often a second.</summary>
        public const float BobMetres = 0.22f, BobHz = 0.55f;

        /// <summary>For this long after a truck is spawned the marker pulses, so the eye finds it.</summary>
        public const float SpawnPulseSeconds = 6f;

        /// <summary>The distance line shows metres rounded to this step, so the words change (and the canvas rebuilds) at most every few seconds of walking.</summary>
        public const int DistanceStep = 5;

        public static float Scale(float distance)
        {
            return distance <= NearDistance ? 1f : Math.Min(MaxScale, distance / NearDistance);
        }

        /// <summary>
        /// Drawn when the player has an own truck that stands in the world, is not sitting in any truck, is further from it
        /// than the hide distance (with hysteresis: a marker that is showing stays until HideNearer, a hidden one comes back
        /// beyond ShowFarther), and the viewer is within HideBeyond.
        /// </summary>
        public static bool Visible(bool shownNow, bool seated, float playerDistance, float viewDistance)
        {
            if (seated || viewDistance >= HideBeyond)
            {
                return false;
            }

            return playerDistance > (shownNow ? HideNearer : ShowFarther);
        }

        /// <summary>The scale factor of the spawn pulse, t seconds after the truck was spawned: up to 1.25, settling to 1 by SpawnPulseSeconds.</summary>
        public static float Pulse(float t)
        {
            if (t < 0f || t >= SpawnPulseSeconds)
            {
                return 1f;
            }

            float fade = 1f - t / SpawnPulseSeconds;
            return 1f + 0.25f * fade * Math.Abs((float)Math.Sin(t * Math.PI * 2.0 * 1.2));
        }
    }

    /// <summary>
    /// A floating marker over the local player's own truck (2026-09-23: "when you spawn a truck in, have an indicator above
    /// it"): the game's map pin (/material/ui/adviser/checkpoint.mat, an SDF icon painted by EtsGameUi.SdfIcon) in the
    /// game's orange (star_orange's colour), pointing down at the truck and bobbing, with "Your truck" and the distance on a
    /// dark plate above it. Only the local player sees it - every client builds its own, for its own truck.
    ///
    /// - Watches EtsLocalTruck.OwnChanged itself (and reads Own on enable, for a truck spawned before it); EtsTruckSeat.Ensure
    ///   adds it to 'Ets Net' beside the seat, so it needs no other hook. The spawn of a truck starts a short pulse.
    /// - Hidden while the player sits in any truck (EtsLocalTruck.Occupied), while they stand within 6 m of their truck
    ///   (shown again beyond 7 m), before the truck is set down (Placed), and with no Somnium player (the editor).
    /// - Built like EtsNameplate: a world-space Canvas on the UI layer (5), no GraphicRaycaster and raycastTarget off, so it
    ///   never takes the client's mouse or VR ray; not parented to the truck (it follows it, upright, turned about the
    ///   vertical to face the viewer, so the pin always points straight down); LateUpdate at order 10000, after the camera
    ///   rigs and VRIK. It is destroyed with 'Ets Net'.
    /// - Per frame: a few transform writes and compares, no allocation. The viewer's camera and the player's body are looked
    ///   up at most twice a second (EtsVr.Head searches every camera when there is no main one). The distance words are set
    ///   through TextMeshPro's allocation-free SetText, only when the rounded value changes.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class EtsTruckMarker : MonoBehaviour
    {
        public const string PinMaterial = "/material/ui/adviser/checkpoint.mat";

        /// <summary>The game's orange: /material/ui/icon/star_orange.mat's aux[1] (0.956, 0.434, 0.0009 linear) as sRGB.</summary>
        public static readonly Color Orange = new Color(0.980f, 0.690f, 0.012f, 1f);

        /// <summary>The words over the pin (not in the game's locale).</summary>
        public const string Words = "Your truck";

        /// <summary>Drawn in the last frame.</summary>
        public bool Shown => _canvas != null && _canvas.enabled;

        /// <summary>The truck the marker is over (the own truck), or null.</summary>
        public EtsTruckRig Truck => _own;

        private static Texture2D _fallbackPin;

        private EtsTruckRig _own;
        private bool _subscribed;
        private float _spawnedAt = -100f;
        private bool _pulsePending;
        private bool _reportedShown;

        private GameObject _host;
        private Transform _t;
        private Canvas _canvas;
        private TextMeshProUGUI _distanceText;
        private int _shownMetres = -1;
        private float _scale = -1f;

        private Camera _eye;
        private float _eyeNext;
        private Transform _player;
        private float _playerNext;

        /// <summary>Gets or adds the one marker on host ('Ets Net'; EtsTruckSeat.Ensure calls it).</summary>
        public static EtsTruckMarker Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsTruckMarker marker = host.GetComponent<EtsTruckMarker>();
            return marker != null ? marker : host.AddComponent<EtsTruckMarker>();
        }

        private void OnEnable()
        {
            if (!_subscribed)
            {
                _subscribed = true;
                EtsLocalTruck.OwnChanged += OnOwnChanged;
            }

            // A truck spawned before this was added (or while it was disabled): marked too, without the pulse.
            if (!ReferenceEquals(_own, EtsLocalTruck.Own))
            {
                _own = EtsLocalTruck.Own;
                _reportedShown = false;
            }
        }

        private void OnDisable()
        {
            if (_subscribed)
            {
                _subscribed = false;
                EtsLocalTruck.OwnChanged -= OnOwnChanged;
            }

            SetShown(false);
        }

        private void OnDestroy()
        {
            if (_host != null)
            {
                Destroy(_host);
                _host = null;
            }
        }

        private void OnOwnChanged(EtsTruckRig rig)
        {
            _own = rig;
            _reportedShown = false;

            if (rig == null)
            {
                SetShown(false);
                return;
            }

            // The pulse starts the first frame the marker shows: the truck may wait for ground before it is set down.
            _pulsePending = true;
            Debug.Log("[SomniumEuroTruck] Truck marker: over your " + (string.IsNullOrEmpty(rig.Stem) ? "truck" : rig.Stem) +
                      " - shown while you are on foot and more than " + EtsTruckMarkerRules.ShowFarther.ToString("0") + " m from it.");
        }

        private void LateUpdate()
        {
            Refresh();
        }

        /// <summary>The per-frame work (public so a probe can count its allocations): follow, face, scale, show or hide.</summary>
        public void Refresh()
        {
            EtsTruckRig own = _own;

            // Unity's null test: a despawned rig is a destroyed object that == null.
            if (own == null || own.Controller == null || !own.Controller.Placed)
            {
                SetShown(false);
                return;
            }

            Transform player = Player();
            Camera eye = Eye();

            if (player == null || eye == null)
            {
                SetShown(false);
                return;
            }

            Vector3 truck = own.transform.position;
            Vector3 eyeAt = eye.transform.position;
            Vector3 fromPlayer = truck - player.position;
            float playerDistance = Mathf.Sqrt(fromPlayer.x * fromPlayer.x + fromPlayer.z * fromPlayer.z);
            float viewDistance = (truck - eyeAt).magnitude;
            bool show = EtsTruckMarkerRules.Visible(Shown, EtsLocalTruck.Occupied != null, playerDistance, viewDistance);

            if (show && _host == null)
            {
                Build();
            }

            SetShown(show);

            if (!show)
            {
                return;
            }

            float distanceScale = EtsTruckMarkerRules.Scale(viewDistance);
            float now = Time.time;

            if (_pulsePending)
            {
                _pulsePending = false;
                _spawnedAt = now;
            }

            float scale = EtsTruckMarkerRules.UnitsToMetres * distanceScale * EtsTruckMarkerRules.Pulse(now - _spawnedAt);
            float bob = EtsTruckMarkerRules.BobMetres * distanceScale * (0.5f + 0.5f * Mathf.Sin(now * (2f * Mathf.PI * EtsTruckMarkerRules.BobHz)));

            Vector3 at = truck;
            at.y += EtsTruckMarkerRules.Height + bob;
            _t.position = at;

            // Turned about the vertical only, so the pin always points straight down at the truck; a viewer right above it
            // keeps the last facing.
            float dx = at.x - eyeAt.x, dz = at.z - eyeAt.z;

            if (dx * dx + dz * dz > 1e-4f)
            {
                _t.rotation = Quaternion.LookRotation(new Vector3(dx, 0f, dz), Vector3.up);
            }

            if (scale != _scale)
            {
                _scale = scale;
                _t.localScale = new Vector3(scale, scale, scale);
            }

            int metres = Mathf.RoundToInt(playerDistance / EtsTruckMarkerRules.DistanceStep) * EtsTruckMarkerRules.DistanceStep;

            if (metres != _shownMetres && _distanceText != null)
            {
                _shownMetres = metres;
                _distanceText.SetText("{0:0} m", metres);
            }

            if (!_reportedShown)
            {
                _reportedShown = true;
                Debug.Log("[SomniumEuroTruck] Truck marker: shown, " + Mathf.RoundToInt(playerDistance) + " m from your truck (" + (_pinFromGame ? "the game's pin" : "the fallback arrow") + ").");
            }
        }

        private bool _pinFromGame;

        private void SetShown(bool shown)
        {
            if (_canvas != null && _canvas.enabled != shown)
            {
                _canvas.enabled = shown;
            }
        }

        /// <summary>The camera the local player sees through (EtsVr.Head), asked again twice a second, never more.</summary>
        private Camera Eye()
        {
            float now = Time.unscaledTime;

            if (now >= _eyeNext || now < _eyeNext - 1f || (_eye != null && !_eye.isActiveAndEnabled))
            {
                _eyeNext = now + 0.5f;
                _eye = EtsVr.Head();
            }

            return _eye != null && _eye.isActiveAndEnabled ? _eye : null;
        }

        /// <summary>The local Somnium player's body root (null in the editor), asked again twice a second.</summary>
        private Transform Player()
        {
            float now = Time.unscaledTime;

            if (_player == null || now >= _playerNext || now < _playerNext - 1f)
            {
                _playerNext = now + 0.5f;
                _player = SomniumBridge.PlayersContainer?.LocalPlayer?.References?.Body?.Root;
            }

            return _player != null ? _player : null;
        }

        /// <summary>The canvas, the pin with its rim, and the plate with the words: built once, on the first frame it shows.</summary>
        private void Build()
        {
            _host = new GameObject("Ets truck marker", typeof(RectTransform));
            _host.layer = EtsLayers.Ui;
            _t = _host.transform;

            _canvas = _host.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.enabled = false;

            Texture2D pin = EtsEngine.IsMounted ? EtsGameUi.SdfIcon(PinMaterial, Mathf.RoundToInt(EtsTruckMarkerRules.PinWidth), Mathf.RoundToInt(EtsTruckMarkerRules.PinHeight)) : null;
            _pinFromGame = pin != null;

            if (pin == null)
            {
                pin = FallbackPin();
            }

            TMP_FontAsset font = EtsEngine.IsMounted ? EtsFont.Get(EtsFont.Normal) : null;

            if (font == null)
            {
                font = EtsNameplate.FallbackFont();
            }

            float pinW = EtsTruckMarkerRules.PinWidth, pinH = EtsTruckMarkerRules.PinHeight, rim = EtsTruckMarkerRules.Rim;

            // The rim: the same pin, larger and dark, behind the orange one - it keeps the pin's edge against a pale sky.
            Picture("rim", pin, new Color(0.04f, 0.04f, 0.045f, 0.75f), pinW + 2f * rim, pinH + 2f * rim, pinH * 0.5f);
            Picture("pin", pin, Orange, pinW, pinH, pinH * 0.5f);

            TextMeshProUGUI words = Label("words", font, EtsTruckMarkerRules.WordsSize, EtsGameUi.Colour("clr_txt", new Color(0.93f, 0.94f, 0.95f)));
            words.text = Words;
            _distanceText = Label("distance", font, EtsTruckMarkerRules.DistanceSize, Orange);
            _distanceText.text = "";

            float wordsWidth = words.GetPreferredValues(Words, 4000f, 200f).x;
            float distanceWidth = _distanceText.GetPreferredValues("2500 m", 4000f, 200f).x;
            float plateW = Mathf.Max(wordsWidth, distanceWidth) + 2f * EtsTruckMarkerRules.PadX;
            float plateH = EtsTruckMarkerRules.WordsLine + EtsTruckMarkerRules.DistanceLine + 2f * EtsTruckMarkerRules.PadY;
            float plateBottom = pinH + rim + EtsTruckMarkerRules.Gap;

            var plate = new GameObject("plate", typeof(RectTransform)).AddComponent<Image>();
            Child(plate.rectTransform);
            plate.rectTransform.SetSiblingIndex(0);
            plate.color = new Color(EtsGameUi.PanelColour.r, EtsGameUi.PanelColour.g, EtsGameUi.PanelColour.b, 0.72f);
            plate.raycastTarget = false;
            plate.rectTransform.sizeDelta = new Vector2(plateW, plateH);
            plate.rectTransform.anchoredPosition = new Vector2(0f, plateBottom + plateH * 0.5f);

            float y = plateBottom + EtsTruckMarkerRules.PadY;
            RectTransform distanceRect = _distanceText.rectTransform;
            distanceRect.sizeDelta = new Vector2(plateW, EtsTruckMarkerRules.DistanceLine);
            distanceRect.anchoredPosition = new Vector2(0f, y + EtsTruckMarkerRules.DistanceLine * 0.5f);
            y += EtsTruckMarkerRules.DistanceLine;
            RectTransform wordsRect = words.rectTransform;
            wordsRect.sizeDelta = new Vector2(plateW, EtsTruckMarkerRules.WordsLine);
            wordsRect.anchoredPosition = new Vector2(0f, y + EtsTruckMarkerRules.WordsLine * 0.5f);

            var rect = (RectTransform)_t;
            rect.pivot = new Vector2(0.5f, 0f); // grows upwards from the pin's tip
            rect.sizeDelta = new Vector2(Mathf.Max(plateW, pinW + 2f * rim), plateBottom + plateH);

            _scale = EtsTruckMarkerRules.UnitsToMetres;
            _t.localScale = new Vector3(_scale, _scale, _scale);
            _shownMetres = -1;
            EtsLayers.SetRecursively(_t, EtsLayers.Ui);

            Debug.Log("[SomniumEuroTruck] Truck marker: built (" + (_pinFromGame ? "the game's pin " + PinMaterial : "a drawn arrow; the game's pin was not available") +
                      ", font " + (font != null ? font.name : "none") + ").");
        }

        private RawImage Picture(string name, Texture2D texture, Color colour, float width, float height, float centreY)
        {
            var image = new GameObject(name, typeof(RectTransform)).AddComponent<RawImage>();
            Child(image.rectTransform);
            image.texture = texture;
            image.color = colour;
            image.raycastTarget = false;
            image.rectTransform.sizeDelta = new Vector2(width, height);
            image.rectTransform.anchoredPosition = new Vector2(0f, centreY);
            return image;
        }

        private TextMeshProUGUI Label(string name, TMP_FontAsset font, float size, Color colour)
        {
            var text = new GameObject(name, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            Child(text.rectTransform);

            if (font != null)
            {
                text.font = font;
            }

            text.fontSize = size;
            text.color = colour;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = false;
            text.raycastTarget = false;
            return text;
        }

        private void Child(RectTransform child)
        {
            child.SetParent(_t, false);
            child.anchorMin = child.anchorMax = new Vector2(0.5f, 0f);
            child.pivot = new Vector2(0.5f, 0.5f);
        }

        /// <summary>
        /// A white downward arrow (a head over a stem), drawn once, for when the game's pin cannot be painted (the archives
        /// not mounted). 4 x 4 samples a pixel for a smooth edge. Tinted orange by the image like the pin.
        /// </summary>
        private static Texture2D FallbackPin()
        {
            if (_fallbackPin != null)
            {
                return _fallbackPin;
            }

            const int w = 70, h = 95;
            var pixels = new Color32[w * h];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int inside = 0;

                    for (int sy = 0; sy < 4; sy++)
                    {
                        for (int sx = 0; sx < 4; sx++)
                        {
                            float px = (x + (sx + 0.5f) / 4f) / w;   // 0..1 across
                            float py = (y + (sy + 0.5f) / 4f) / h;   // 0 at the tip (Unity's rows run bottom-up)
                            bool head = py <= 0.55f && Mathf.Abs(px - 0.5f) <= py / 0.55f * 0.5f;
                            bool stem = py > 0.5f && Mathf.Abs(px - 0.5f) <= 0.17f;

                            if (head || stem)
                            {
                                inside++;
                            }
                        }
                    }

                    pixels[y * w + x] = new Color32(255, 255, 255, (byte)(inside * 255 / 16));
                }
            }

            _fallbackPin = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            _fallbackPin.SetPixels32(pixels);
            _fallbackPin.Apply(false, true);
            return _fallbackPin;
        }
    }
}
