using System;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The sizes and rules of a truck's nameplate (owner D; PLAN.md §3.7 item 4, §8 D3), pure so probe 2260 checks them.
    ///
    /// Readable in a headset: the name's glyphs are NameSize x UnitsToMetres = 0.32 m tall at the plate's own scale, which
    /// is 1.5 degrees of view at NearDistance (12 m) - about 30 pixels on a Quest 2's ~20 pixels per degree, where
    /// 10-12 pixels is the least that reads. Beyond 12 m the plate grows with the distance (Scale), so the name keeps
    /// those 1.5 degrees out to 48 m (MaxScale 4), then shrinks like everything else and goes at HideBeyond. That is
    /// how games keep names readable at range without a screen overlay, which a headset does not have.
    /// </summary>
    public static class EtsNameplateRules
    {
        /// <summary>Above the truck's origin, in world up: EtsNet.Nameplate's 4.6 m ([net §2.6]), clear of a 4 m cab roof.</summary>
        public const float Height = 4.6f;

        /// <summary>The canvas's metres per unit at scale 1.</summary>
        public const float UnitsToMetres = 0.005f;

        /// <summary>Canvas units: the name's font size, the job line's, the star's box, the gap after it, the padding, the two line heights.</summary>
        public const float NameSize = 64f, DetailSize = 40f, StarWidth = 64f, StarHeight = 61f, Gap = 14f, PadX = 24f, PadY = 12f, NameLine = 82f, DetailLine = 52f;

        /// <summary>Closer than this the plate keeps its size; farther, it grows with the distance.</summary>
        public const float NearDistance = 12f;

        public const float MaxScale = 4f;

        /// <summary>Not drawn beyond this (a name 400 m away is a speck in a crowd of them) nor nearer than HideNearer (the viewer's head in it).</summary>
        public const float HideBeyond = 400f, HideNearer = 1.5f;

        /// <summary>Names longer than this are cut, with "..." (the client allows long display names; a plate must stay a plate).</summary>
        public const int MaxName = 24, MaxDetail = 40;

        public static float Scale(float distance)
        {
            return distance <= NearDistance ? 1f : Math.Min(MaxScale, distance / NearDistance);
        }

        /// <summary>Drawn when in range and the viewer is not sitting in the very truck it names.</summary>
        public static bool Visible(float distance, bool viewerInThatTruck)
        {
            return !viewerInThatTruck && distance >= HideNearer && distance < HideBeyond;
        }

        /// <summary>The degrees of view the name's glyph size spans at a distance (for the probe and the notes).</summary>
        public static float NameDegrees(float distance)
        {
            if (distance <= 0f)
            {
                return 180f;
            }

            double size = NameSize * UnitsToMetres * Scale(distance);
            return (float)(2.0 * Math.Atan(size * 0.5 / distance) * 180.0 / Math.PI);
        }

        /// <summary>
        /// A name as a plate shows it: control characters dropped, spaces trimmed, cut at max with "...". The same string
        /// instance comes back when nothing needed changing, so a caller passing the same name every frame allocates
        /// nothing. The label shows it with rich text off, so a name like "&lt;size=900&gt;" is only text.
        /// </summary>
        public static string Clean(string text, int max = MaxName)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            bool clean = text.Length <= max && !char.IsWhiteSpace(text[0]) && !char.IsWhiteSpace(text[text.Length - 1]);

            for (int i = 0; clean && i < text.Length; i++)
            {
                if (char.IsControl(text[i]))
                {
                    clean = false;
                }
            }

            if (clean)
            {
                return text;
            }

            var chars = new System.Text.StringBuilder(text.Length);

            foreach (char c in text)
            {
                if (!char.IsControl(c))
                {
                    chars.Append(c);
                }
            }

            string s = chars.ToString().Trim();

            if (s.Length <= max)
            {
                return s;
            }

            // Never through a surrogate pair: an emoji is two chars, and half of one is drawn as a missing-glyph box
            // (checker probe 2265: a 30-emoji name cut at 21 left a lone high surrogate).
            int cut = Math.Max(1, max - 3);

            if (cut > 1 && char.IsHighSurrogate(s[cut - 1]))
            {
                cut--;
            }

            return s.Substring(0, cut).TrimEnd() + "...";
        }
    }

    /// <summary>
    /// The name over a player's truck, for everyone who sees it (owner D builds it, owner A places one per drawn truck;
    /// PLAN.md §3.7 item 4, §8 D3, [net §2.6]): the player's name, the game's orange star in front of it when they are the
    /// instance master (/material/ui/icon/star_orange.mat, the SDF icon the HUD badge and the tablet's top bar use), and
    /// an optional smaller line under it (the job's destination, say), on a dark translucent plate.
    ///
    /// How A uses it:
    ///     EtsNameplate plate = EtsNameplate.Create(rig.transform);      // once, when the truck is drawn
    ///     plate.Set(view.Name, view.IsMaster, detail);                   // whenever the slot is read; free when unchanged
    ///     Destroy(plate.gameObject);                                     // when the truck goes (or it goes by itself)
    ///
    /// Why it is built this way:
    /// - A world-space Canvas with UGUI, not a TextMeshPro 3D text: the star is a texture, and a world canvas draws a
    ///   RawImage and TextMeshProUGUI in the same shaders every world panel here uses and the client is known to draw
    ///   (the HQ map, the VR dash, the VR panels). No GraphicRaycaster and raycastTarget off everywhere, so it never
    ///   takes the client's mouse or VR ray. On the UI layer (5): drawn by the desktop camera (culling mask 363020535
    ///   includes 5, CameraSystem.prefab) and left out of the route advisor's map camera, which culls 5.
    /// - Not parented to the truck: it follows it (Follow's position plus Height in world up), so the rig's layer
    ///   sweeps, interior toggles and child searches never meet it, and it stays upright over a truck on a slope. When
    ///   the followed truck is destroyed the plate destroys itself.
    /// - Faces the viewer's camera every frame (LateUpdate at order 10000, after the camera rigs and VRIK 9998) and scales
    ///   with the distance (EtsNameplateRules.Scale). Hidden from a viewer seated in that truck (EtsLocalTruck.Occupied).
    /// - Per frame: two transform writes and a few compares, no allocation (probe 2261 counts it). The canvas's mesh is
    ///   rebuilt only when Set changes the words or the star. The viewer's camera is looked up at most twice a second.
    /// The font is the game's normal.font (warm by then, EtsFlow.Warm), else the scene's own status-line font.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class EtsNameplate : MonoBehaviour
    {
        public const string StarMaterial = "/material/ui/icon/star_orange.mat";

        /// <summary>The master star painted at the nameplate's size (64 x 61, star_orange's 39 x 37 shape scaled); cached by EtsGameUi.SdfIcon. Null without the archives.</summary>
        public static Texture2D StarIcon()
        {
            return EtsGameUi.SdfIcon(StarMaterial, 64, 61);
        }

        /// <summary>
        /// Paints the star and makes the font ahead, so the first plate A builds in the world is not the cold one (EtsFlow.Warm
        /// calls it under the loading screen). Probe 2261 times the parts cold and a plate warm.
        /// </summary>
        public static void Prepare()
        {
            StarIcon();

            if (EtsEngine.IsMounted)
            {
                EtsFont.Get(EtsFont.Normal);
            }
        }

        /// <summary>The truck (or any object) the plate floats over.</summary>
        public Transform Follow => _follow;

        /// <summary>Metres above Follow's origin.</summary>
        public float HeightAbove = EtsNameplateRules.Height;

        /// <summary>A's switch (for example the own truck, or a truck not placed yet). The plate also hides itself (Visible).</summary>
        public bool Hidden;

        public string PlayerName => _name;
        public bool Master => _master;
        public string Detail => _detail;

        /// <summary>Drawn in the last frame.</summary>
        public bool Shown => _canvas != null && _canvas.enabled;

        /// <summary>The plate's scale factor last frame (1 up to 12 m; for the probe).</summary>
        public float LastScale => _scale / EtsNameplateRules.UnitsToMetres;

        private static TMP_FontAsset _fallbackFont;
        private static Camera _eye;
        private static float _eyeNext;

        private Transform _t, _follow;
        private Canvas _canvas;
        private RectTransform _back, _starRect, _nameRect, _detailRect;
        private RawImage _star;
        private TextMeshProUGUI _nameText, _detailText;
        private string _name = "", _detail = "";
        private bool _master, _haveName;
        private float _scale = -1f;

        /// <summary>A plate over follow (usually the truck rig's transform), hidden until Set gives it a name.</summary>
        public static EtsNameplate Create(Transform follow, float height = EtsNameplateRules.Height)
        {
            var host = new GameObject("Ets nameplate", typeof(RectTransform));
            host.layer = EtsLayers.Ui;
            var plate = host.AddComponent<EtsNameplate>();
            plate.Build(follow, height);
            return plate;
        }

        /// <summary>
        /// The name, the master star and the line under it (null or "" for none). Nothing happens when all three are
        /// what the plate already shows; otherwise the texts change and the plate is laid out again (one canvas rebuild).
        /// </summary>
        public void Set(string playerName, bool master, string detail = null)
        {
            string name = EtsNameplateRules.Clean(playerName);
            string more = EtsNameplateRules.Clean(detail, EtsNameplateRules.MaxDetail);

            if (_haveName && master == _master && string.Equals(name, _name, StringComparison.Ordinal) && string.Equals(more, _detail, StringComparison.Ordinal))
            {
                return;
            }

            _name = name;
            _detail = more;
            _master = master;
            _haveName = name.Length > 0;
            Layout();
        }

        /// <summary>The per-frame work (LateUpdate calls it; public so the probe can count its allocations): follow, face, scale, show or hide.</summary>
        public void Refresh()
        {
            if (_follow == null)
            {
                // The truck was destroyed (a despawn, the owner left): the plate goes with it.
                Destroy(gameObject);
                return;
            }

            Vector3 at = _follow.position;
            at.y += HeightAbove;
            _t.position = at;

            Camera eye = Eye();

            if (eye == null || Hidden || !_haveName)
            {
                SetShown(false);
                return;
            }

            Vector3 d = at - eye.transform.position;
            float distance = d.magnitude;
            EtsTruckRig occupied = EtsLocalTruck.Occupied;
            bool inside = occupied != null && (_follow == occupied.transform || _follow.IsChildOf(occupied.transform));
            bool show = EtsNameplateRules.Visible(distance, inside);
            SetShown(show);

            if (!show)
            {
                return;
            }

            // Square to the line of sight, so the words are never seen at a slant; a viewer almost straight below keeps
            // the last facing (LookRotation has no yaw there).
            if (d.x * d.x + d.z * d.z > 1e-4f * distance * distance)
            {
                _t.rotation = Quaternion.LookRotation(d * (1f / distance), Vector3.up);
            }

            float scale = EtsNameplateRules.UnitsToMetres * EtsNameplateRules.Scale(distance);

            if (scale != _scale)
            {
                _scale = scale;
                _t.localScale = new Vector3(scale, scale, scale);
            }
        }

        /// <summary>The font a screen can use when the game's are not there: the scene's status line's (LiberationSans SDF, SCENE:1266, shipped with the world), else TextMesh Pro's default, else any in the scene.</summary>
        public static TMP_FontAsset FallbackFont()
        {
            if (_fallbackFont != null)
            {
                return _fallbackFont;
            }

            GameObject status = GameObject.Find("Status Canvas");
            TMP_Text text = status != null ? status.GetComponentInChildren<TMP_Text>(true) : null;
            _fallbackFont = text != null && text.font != null ? text.font : TMP_Settings.defaultFontAsset;

            if (_fallbackFont == null)
            {
                TMP_Text any = FindFirstObjectByType<TMP_Text>();
                _fallbackFont = any != null ? any.font : null;
            }

            return _fallbackFont;
        }

        private void Build(Transform follow, float height)
        {
            _t = transform;
            _follow = follow;
            HeightAbove = height;

            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.enabled = false;
            var rect = (RectTransform)_t;
            rect.pivot = new Vector2(0.5f, 0f); // grows upwards from the point over the truck
            rect.sizeDelta = new Vector2(1200f, EtsNameplateRules.NameLine + EtsNameplateRules.DetailLine + 2f * EtsNameplateRules.PadY);

            TMP_FontAsset font = EtsEngine.IsMounted ? EtsFont.Get(EtsFont.Normal) : null;

            if (font == null)
            {
                font = FallbackFont();
            }

            var back = new GameObject("plate", typeof(RectTransform)).AddComponent<Image>();
            _back = Child(back.rectTransform);
            back.color = new Color(EtsGameUi.PanelColour.r, EtsGameUi.PanelColour.g, EtsGameUi.PanelColour.b, 0.62f);
            back.raycastTarget = false;

            Texture2D starTexture = StarIcon();
            _star = new GameObject("star", typeof(RectTransform)).AddComponent<RawImage>();
            _starRect = Child(_star.rectTransform);
            _starRect.sizeDelta = new Vector2(EtsNameplateRules.StarWidth, EtsNameplateRules.StarHeight);
            _star.texture = starTexture;
            _star.raycastTarget = false;
            _star.enabled = false;

            _nameText = Label("name", font, EtsNameplateRules.NameSize, EtsGameUi.Colour("clr_txt", new Color(0.93f, 0.94f, 0.95f)));
            _nameRect = _nameText.rectTransform;
            _detailText = Label("detail", font, EtsNameplateRules.DetailSize, EtsGameUi.Colour("clr_txt_d", new Color(0.62f, 0.64f, 0.67f)));
            _detailRect = _detailText.rectTransform;

            _t.position = follow != null ? follow.position + Vector3.up * height : Vector3.zero;
            _scale = EtsNameplateRules.UnitsToMetres;
            _t.localScale = new Vector3(_scale, _scale, _scale);
            EtsLayers.SetRecursively(_t, EtsLayers.Ui);

            if (starTexture == null)
            {
                Debug.Log("[SomniumEuroTruck] Nameplate: no star texture (the archives are not mounted) - a master's plate shows the name only.");
            }
        }

        private RectTransform Child(RectTransform child)
        {
            child.SetParent(_t, false);
            child.anchorMin = child.anchorMax = new Vector2(0.5f, 0f);
            child.pivot = new Vector2(0.5f, 0.5f);
            return child;
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
            text.richText = false; // a player's name is text, never markup
            text.raycastTarget = false;
            return text;
        }

        /// <summary>After a change only: the texts, the star, and the plate fitted round them.</summary>
        private void Layout()
        {
            if (_nameText == null)
            {
                return;
            }

            bool detail = _detail.Length > 0;
            bool star = _master && _star.texture != null;
            _nameText.text = _name;
            _detailText.text = _detail;
            _detailText.gameObject.SetActive(detail);
            _star.enabled = star;

            float nameWidth = _name.Length > 0 ? _nameText.GetPreferredValues(_name, 4000f, 200f).x : 0f;
            float detailWidth = detail ? _detailText.GetPreferredValues(_detail, 4000f, 200f).x : 0f;
            float lead = star ? EtsNameplateRules.StarWidth + EtsNameplateRules.Gap : 0f;
            float row = lead + nameWidth;
            float width = Math.Max(row, detailWidth) + 2f * EtsNameplateRules.PadX;

            float y = EtsNameplateRules.PadY;

            if (detail)
            {
                _detailRect.sizeDelta = new Vector2(detailWidth + 8f, EtsNameplateRules.DetailLine);
                _detailRect.anchoredPosition = new Vector2(0f, y + EtsNameplateRules.DetailLine * 0.5f);
                y += EtsNameplateRules.DetailLine;
            }

            float nameY = y + EtsNameplateRules.NameLine * 0.5f;
            _nameRect.sizeDelta = new Vector2(nameWidth + 8f, EtsNameplateRules.NameLine);
            _nameRect.anchoredPosition = new Vector2(-row * 0.5f + lead + nameWidth * 0.5f, nameY);
            _starRect.anchoredPosition = new Vector2(-row * 0.5f + EtsNameplateRules.StarWidth * 0.5f, nameY);

            float height = y + EtsNameplateRules.NameLine + EtsNameplateRules.PadY;
            _back.sizeDelta = new Vector2(width, height);
            _back.anchoredPosition = new Vector2(0f, height * 0.5f);

            // A rig's layer sweep can never reach the plate (it is not the rig's child), but the new label objects are
            // fresh: all on UI.
            EtsLayers.SetRecursively(_t, EtsLayers.Ui);
        }

        private void SetShown(bool shown)
        {
            if (_canvas != null && _canvas.enabled != shown)
            {
                _canvas.enabled = shown;
            }
        }

        /// <summary>
        /// The camera the local player sees through (EtsVr.Head: Camera.main, else the headset's), asked again twice a
        /// second, never more: EtsVr.Head searches every camera (and allocates) when there is no main camera, and sixteen
        /// plates must not do that every frame.
        /// </summary>
        private static Camera Eye()
        {
            float now = Time.unscaledTime;

            if (now >= _eyeNext || now < _eyeNext - 1f)
            {
                _eyeNext = now + 0.5f;
                _eye = EtsVr.Head();
            }

            return _eye != null && _eye.isActiveAndEnabled ? _eye : null;
        }

        private void LateUpdate()
        {
            Refresh();
        }
    }
}
