// SomniAssetStore - shared editor styling so the Store, Publisher and Tools windows
// look like one cohesive, professional tool instead of raw IMGUI.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SomniAssetStore.Shared
{
    public static class SomniStyle
    {
        // ---- palette (adapts to light / dark editor skin) --------------------
        public static Color Accent;
        public static Color AccentHover;
        public static Color AccentText;
        public static Color AccentSoft;
        public static Color AccentLine;
        public static Color Green;
        public static Color Amber;
        public static Color Danger;
        public static Color CardBg;
        public static Color CardBorder;
        public static Color Divider;
        public static Color TextMute;
        public static Color WindowBg;

        // ---- styles ----------------------------------------------------------
        public static GUIStyle Title, Subtitle, SectionLabel, CardTitle, IdText, Desc, Meta, ChipText;
        public static GUIStyle Card, HeaderBar, PrimaryBtn, SecondaryBtn, DangerBtn;
        public static Texture2D Logo;
        public static Texture2D BannerBg;

        private static bool _init;
        private static Texture2D _cardBg, _headerBg, _accentBg, _accentHover, _dangerBg, _dangerHover;

        public static void Init()
        {
            if (_init) return;
            bool pro = EditorGUIUtility.isProSkin;

            Accent = Hex("8b7dbe");
            AccentHover = Hex("a99dd6");
            AccentText = Hex("c4b8f0");
            AccentSoft = WithAlpha(Accent, 0.16f);
            AccentLine = WithAlpha(Accent, 0.45f);
            Green = Hex("3fb950");
            Amber = Hex("d29922");
            Danger = Hex("f85149");

            WindowBg = pro ? Hex("0a0a0c") : Hex("ffffff");
            CardBg = pro ? Hex("141416") : Hex("ffffff");
            CardBorder = pro ? Hex("1e1e22") : Hex("dfe2e6");
            Divider = pro ? new Color(1f, 1f, 1f, 0.06f) : new Color(0f, 0f, 0f, 0.08f);
            TextMute = pro ? Hex("8b929c") : Hex("6a737d");
            Color text = pro ? Hex("e6edf3") : Hex("1c2128");

            _cardBg = Solid(CardBg);
            _headerBg = Solid(CardBg);

            Title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 };
            Title.normal.textColor = text;

            Subtitle = new GUIStyle(EditorStyles.label) { fontSize = 11, wordWrap = true };
            Subtitle.normal.textColor = TextMute;

            SectionLabel = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = 11 };
            SectionLabel.normal.textColor = TextMute;

            CardTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
            CardTitle.normal.textColor = text;

            IdText = new GUIStyle(EditorStyles.label) { fontSize = 10 };
            IdText.normal.textColor = TextMute;

            Desc = new GUIStyle(EditorStyles.label) { fontSize = 11, wordWrap = true, richText = false };
            Desc.normal.textColor = pro ? Hex("c9d1d9") : Hex("444d56");

            Meta = new GUIStyle(EditorStyles.miniLabel);
            Meta.normal.textColor = TextMute;

            ChipText = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 2, 2),
                fontSize = 10,
            };

            Card = new GUIStyle
            {
                normal = { background = _cardBg },
                border = new RectOffset(1, 1, 1, 1),
                padding = new RectOffset(12, 12, 12, 12),
                margin = new RectOffset(0, 0, 0, 10),
            };

            HeaderBar = new GUIStyle
            {
                normal = { background = _headerBg },
                padding = new RectOffset(12, 12, 10, 10),
                margin = new RectOffset(0, 0, 0, 8),
            };

            const int radius = 6;
            _accentBg = RoundedRect(32, 32, radius, AccentSoft);
            _accentHover = RoundedRect(32, 32, radius, WithAlpha(Accent, 0.24f));
            _dangerBg = RoundedRect(32, 32, radius, WithAlpha(Danger, 0.14f));
            _dangerHover = RoundedRect(32, 32, radius, WithAlpha(Danger, 0.24f));

            PrimaryBtn = MakeButton(_accentBg, _accentHover, AccentText, radius);
            SecondaryBtn = new GUIStyle(EditorStyles.miniButton)
            {
                fixedHeight = 24,
                padding = new RectOffset(12, 12, 4, 4),
                normal = { textColor = text },
                hover = { textColor = text },
                active = { textColor = text },
            };
            DangerBtn = MakeButton(_dangerBg, _dangerHover, Danger, radius);

            Logo = RoundedRect(28, 28, 6, Accent);
            BannerBg = RoundedRect(32, 32, 6, WithAlpha(Amber, 0.14f));

            _init = true;
        }

        private static GUIStyle MakeButton(Texture2D bg, Texture2D hover, Color textColor, int radius)
        {
            var s = new GUIStyle(EditorStyles.miniButton)
            {
                fixedHeight = 24,
                fontStyle = FontStyle.Bold,
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(14, 14, 4, 4),
                border = new RectOffset(radius, radius, radius, radius),
            };
            s.normal.background = bg;
            s.normal.textColor = textColor;
            s.hover.background = hover;
            s.hover.textColor = textColor;
            s.active.background = hover;
            s.active.textColor = textColor;
            return s;
        }

        // ---- helpers ---------------------------------------------------------

        public static Texture2D RoundedRect(int w, int h, int radius, Color fill)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            { hideFlags = HideFlags.HideAndDontSave };
            Color[] pixels = new Color[w * h];
            float r2 = radius * radius;
            float cxTL = radius - 0.5f;
            float cyTL = radius - 0.5f;
            float cxTR = w - radius - 0.5f;
            float cyTR = radius - 0.5f;
            float cxBL = radius - 0.5f;
            float cyBL = h - radius - 0.5f;
            float cxBR = w - radius - 0.5f;
            float cyBR = h - radius - 0.5f;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool inside = true;
                    if (x < radius && y < radius)
                    {
                        float dx = x - cxTL, dy = y - cyTL;
                        if (dx * dx + dy * dy > r2) inside = false;
                    }
                    else if (x >= w - radius && y < radius)
                    {
                        float dx = x - cxTR, dy = y - cyTR;
                        if (dx * dx + dy * dy > r2) inside = false;
                    }
                    else if (x < radius && y >= h - radius)
                    {
                        float dx = x - cxBL, dy = y - cyBL;
                        if (dx * dx + dy * dy > r2) inside = false;
                    }
                    else if (x >= w - radius && y >= h - radius)
                    {
                        float dx = x - cxBR, dy = y - cyBR;
                        if (dx * dx + dy * dy > r2) inside = false;
                    }
                    pixels[y * w + x] = inside ? fill : Color.clear;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Paint the entire window a single flat background (matches the website's --bg). Call this
        /// FIRST in every Somni window's OnGUI so no default editor grey shows through behind the
        /// header, between cards, or in the empty scroll area.
        /// </summary>
        public static void FillBackground(float width, float height)
        {
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(new Rect(0f, 0f, width, height), WindowBg);
        }

        /// <summary>Draw a small coloured pill (flat) and return its label.</summary>
        public static void Chip(string text, Color color)
        {
            var content = new GUIContent(text);
            Vector2 size = ChipText.CalcSize(content);
            Rect r = GUILayoutUtility.GetRect(size.x, 18f, GUILayout.Width(size.x));
            var tint = new Color(color.r, color.g, color.b, 0.16f);
            EditorGUI.DrawRect(r, tint);
            var prev = ChipText.normal.textColor;
            ChipText.normal.textColor = color;
            GUI.Label(r, content, ChipText);
            ChipText.normal.textColor = prev;
        }

        public static void StatusChip(string status)
        {
            Color c = status == "approved" ? Green : status == "pending" ? Amber : TextMute;
            Chip(status, c);
        }

        public static void Divider1()
        {
            Rect r = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, Divider);
        }

        /// <summary>1px outline around the last laid-out rect.</summary>
        public static void OutlineLastRect()
        {
            if (Event.current.type != EventType.Repaint) return;
            Rect r = GUILayoutUtility.GetLastRect();
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), CardBorder);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), CardBorder);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1, r.height), CardBorder);
            EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), CardBorder);
        }

        /// <summary>Human label for a category id.</summary>
        public static string CategoryLabel(string category)
        {
            return string.IsNullOrEmpty(category) ? "other" : category.Replace('-', ' ');
        }

        public static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        public static Color WithAlpha(Color c, float a) =>
            new Color(c.r, c.g, c.b, a);

        public static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString("#" + hex, out var c);
            return c;
        }
    }
}
#endif
