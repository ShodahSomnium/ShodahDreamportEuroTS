using System;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The game's own screens, built from its own layout files.
    ///
    /// Every screen in Euro Truck Simulator is a `.sii` under /ui: a tree of units carrying a
    /// rectangle (coords_l, coords_r, coords_t, coords_b, in a 1440 x 900 frame with y measured up
    /// from the bottom, the reference resolution /def/ui.sii states), a look template naming its
    /// style, and either a locale key in "value" or markup in "text". Images are written as
    /// &lt;img src=/material/ui/...mat&gt;, and those materials and their textures ship with the game.
    /// So a screen can be rebuilt here rather than invented: the demo's own end screen,
    /// /ui/desktop_standalone_demo.sii, is eight units and every one is reproduced.
    ///
    /// The wording. On 1.48 the locale was 3nK-obfuscated and the English below was written against
    /// the key names. On 1.61 the locale is plain text and EtsLocale reads all 12,514 keys (probe
    /// 1881); the new screens (EtsHqStage, EtsLoadingScreen, EtsGamePages, Template) take every word
    /// from it. Text() and its hand-written Words stay only for today's front end, so that while
    /// EtsFeatures.NewFlow is false its screens read exactly as before (PLAN.md C1). The new flow never shows those screens
    /// (EtsFrontEnd keeps only its diagnostics there), so Words stays only for the NewFlow=false fallback.
    ///
    /// Colours. The game writes them AABBGGRR (/locale/ui_colors.sui; EtsLocale). ArgbColour reads a
    /// hex literal that way, which changes nothing on the screens built today: on 1.61 the five
    /// bcg_rect values of loading, delivery, quick_job, desktop and the demo end screen are all
    /// @@clr_bg_main@@, which both readings leave to the fallback (probe 1899; 1.48's launchpad literal
    /// was the grey F0090909, the same either way). 8 of the 338 bcg_rect values in /ui do change -
    /// non-grey or 0x-prefixed literals on screens nothing builds today. A @@clr_*@@ key is resolved
    /// only under the new flow (GameLook), since before it no locale is guaranteed to be loaded.
    /// </summary>
    public static class EtsGameUi
    {
        /// <summary>
        /// A line in the console about something the player did or saw: every button pressed,
        /// every screen shown, where the truck is. So that a log can be read back as "where were
        /// you, what did you press, what happened next" without guessing.
        /// </summary>
        public static void Note(string what)
        {
            Debug.Log("[SomniumEuroTruck][You] " + what);
        }

        /// <summary>Reference frame of the game's screens, from /def/ui.sii.</summary>
        public const float ReferenceWidth = 1440f;

        public const float ReferenceHeight = 900f;

        /// <summary>
        /// The wording for the locale keys these screens use. The locale files are obfuscated, so
        /// these are written from the key names rather than read; everything else on the screen is
        /// the game's own.
        /// </summary>
        private static readonly Dictionary<string, string> Words = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "demo_ended", "Demo ended" },
            { "standalone_demo_complete", "You have completed the demo delivery.\nThe full game continues from here." },
            { "go_to_store_page", "Go to store page" },
            { "demo_thank_you", "Thank you for playing" },
            { "demo_quit", "Quit" },

            // The launchpad.
            { "launchpad_t", "EURO TRUCK SIMULATOR 2 - LAUNCHPAD" },
            { "edit_profile_btn", "Edit Profile" },
            { "manage_profiles_btn", "Manage Profiles" },
            { "dlc_browser_btn", "DLC Browser" },
            { "mod_manager_btn", "Mod Manager" },
            { "options", "Options" },
            { "about", "About" },
            { "latest_news_t", "LATEST NEWS" },
            { "newest_dlc_t", "NEWEST DLC" },
            { "enable_news", "Enable news" },
            { "news_disabled", "News currently disabled" },
            { "academy", "Academy" },
            { "continue_game", "Continue Game" },
            { "profile_level", "Level" },

            // The delivery summary, /ui/delivery.sii.
            { "job_completed_t", "JOB COMPLETED" },
            { "excelent_t", "Excellent" },
            { "distance", "Distance" },
            { "time_taken", "Time taken" },
            { "fuel_consumed", "Fuel consumed" },
            { "total_profit", "Total profit" },
            { "money", "Money" },

            // The job market, /ui/quick_job.sii, and the advisor.
            { "take_job", "Take job" },
            { "sort_by", "Sort by" },
            { "no_job_found", "No job found" },
            { "navigation_title", "Navigation" },
            { "current_job_title", "Current job" },
            { "cargo_name", "Cargo" },
            { "dest", "Destination" },
            { "expected_on", "Expected on" },
            { "cancel_current_job", "Cancel current job" },
            { "garage_scene", "Garage" }
        };

        public static string Text(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            string key = value.Trim();

            // Several fields pack more than one thing into a value, separated by bars: a button is
            // "target|@@label@@|", the loading screen's job line is "time|cargo|route|money". Take
            // the part that carries the words.
            if (key.IndexOf('|') >= 0)
            {
                string[] parts = key.Split('|');
                string best = "";

                foreach (string part in parts)
                {
                    string trimmed = part.Trim();

                    if (trimmed.StartsWith("@@", StringComparison.Ordinal))
                    {
                        return Text(trimmed);
                    }

                    if (trimmed.Length > best.Length && !IsNumber(trimmed))
                    {
                        best = trimmed;
                    }
                }

                key = best;
            }

            // A bare colour - the value of a background rectangle - is not a word.
            if (key.Length == 8 && IsHex(key))
            {
                return "";
            }

            if (key.IndexOf("@@", StringComparison.Ordinal) < 0 && key.IndexOf('<') < 0)
            {
                return key;
            }

            // Values can carry several keys and markup at once: "@@profile_level@@ <color
            // value=@@clr_txt@@>1". Every key becomes its words (nothing, when unknown) and
            // the tags go, leaving the words in order.
            var built = new System.Text.StringBuilder(key.Length);
            int at = 0;

            while (at < key.Length)
            {
                if (key[at] == '<')
                {
                    int close = key.IndexOf('>', at);
                    at = close < 0 ? key.Length : close + 1;
                    continue;
                }

                if (at + 1 < key.Length && key[at] == '@' && key[at + 1] == '@')
                {
                    int end = key.IndexOf("@@", at + 2, StringComparison.Ordinal);

                    if (end > at + 2)
                    {
                        built.Append(Word(key.Substring(at + 2, end - at - 2)));
                        at = end + 2;
                        continue;
                    }
                }

                built.Append(key[at]);
                at++;
            }

            return built.ToString().Trim();
        }

        /// <summary>The words for one locale key, or nothing when they cannot be known.</summary>
        private static string Word(string key)
        {
            if (Words.TryGetValue(key, out string word))
            {
                return word;
            }

            // The locale is obfuscated, but a loading picture's key reads as the place itself:
            // "ls_d941_le_puy_notre_dame_france".
            if (key.StartsWith("ls_", StringComparison.Ordinal))
            {
                return Capitalise(key.Substring(3).Replace('_', ' '));
            }

            // Colour keys inside markup are not words either.
            return "";
        }

        private static bool IsHex(string s)
        {
            foreach (char c in s)
            {
                if (!Uri.IsHexDigit(c)) return false;
            }

            return s.Length > 0;
        }

        /// <summary>
        /// True under the new flow once the locale is loaded: the screens then take the game's own colours
        /// (clr_txt, clr_sel, @@clr_*@@ values). False today, so the old screens keep the colours they had.
        /// </summary>
        public static bool GameLook => EtsFeatures.NewFlow && EtsLocale.Loaded;

        /// <summary>
        /// A colour the way the markup writes one - AABBGGRR in hex (FF03B0FA is clr_sel's orange; the method
        /// kept its old name) - or, under GameLook, a @@clr_*@@ key; otherwise the fallback. Before Phase 1 this
        /// read AARRGGBB; on the screens built today it only meets @@clr_bg_main@@ (the fallback either way; class
        /// notes, probe 1899).
        /// </summary>
        public static Color ArgbColour(string value, Color fallback)
        {
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }

            string s = value.Trim();

            if (s.StartsWith("@@", StringComparison.Ordinal))
            {
                return GameLook && EtsLocale.TryColour(s, out uint named) ? ToColor(named) : fallback;
            }

            return EtsLocale.TryParseColour(s, out uint argb) ? ToColor(argb) : fallback;
        }

        /// <summary>A named colour of /locale/ui_colors.sui ("clr_sel" or "@@clr_sel@@"), or the fallback when it is not loaded.</summary>
        public static Color Colour(string key, Color fallback)
        {
            return EtsLocale.TryColour(key, out uint argb) ? ToColor(argb) : fallback;
        }

        /// <summary>AARRGGBB as a Unity colour.</summary>
        public static Color ToColor(uint argb)
        {
            return new Color32((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
        }

        private static bool IsNumber(string s)
        {
            foreach (char c in s)
            {
                if (!char.IsDigit(c) && c != '.' && c != '-') return false;
            }

            return s.Length > 0;
        }

        private static string Capitalise(string s)
        {
            if (s.Length == 0)
            {
                return s;
            }

            var built = new System.Text.StringBuilder(s.Length);
            bool start = true;

            foreach (char c in s)
            {
                built.Append(start ? char.ToUpperInvariant(c) : c);
                start = c == ' ';
            }

            return built.ToString();
        }

        /// <summary>
        /// A canvas that scales the game's 1440 x 900 frame to whatever this screen is - or, in a
        /// headset, the same frame hung in front of the player as a panel the controllers can
        /// point at (EtsVrPanel). interactive is false for a display nothing is pressed on.
        /// </summary>
        public static Canvas CreateCanvas(string name, int sortingOrder, bool interactive = true)
        {
            var canvasObject = new GameObject(name);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            canvasObject.AddComponent<GraphicRaycaster>();
            EtsVrPanel.Adopt(canvas, interactive);
            return canvas;
        }

        /// <summary>
        /// Places a rectangle where the game's layout puts it: x from the left, y measured up from
        /// the bottom of the 1440 x 900 frame, with the top edge the larger of the two y values.
        /// </summary>
        public static RectTransform Place(Transform parent, EtsSiiUnit unit, string name)
        {
            float l = unit.GetFloat("coords_l", 0f), r = unit.GetFloat("coords_r", 0f);
            float t = unit.GetFloat("coords_t", 0f), b = unit.GetFloat("coords_b", 0f);
            return Place(parent, name, l, r, Mathf.Min(t, b), Mathf.Max(t, b));
        }

        public static RectTransform Place(Transform parent, string name, float left, float right, float bottom, float top)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);

            // The parent's layer at birth (E's check, Phase 2): the client's XR ray (TrackedDeviceGraphicRaycaster) hits only
            // graphics on its UI layer, and a row a page adds during its update would otherwise sit on layer 0 until the
            // tablet link's 4 Hz pass relayers it - up to 250 ms of a dead ray.
            if (parent != null)
            {
                rect.gameObject.layer = parent.gameObject.layer;
            }

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(left, bottom);
            rect.sizeDelta = new Vector2(Mathf.Max(1f, right - left), Mathf.Max(1f, top - bottom));
            return rect;
        }

        /// <summary>The texture behind an &lt;img src=...mat&gt;, loaded from the game.</summary>
        public static Texture2D Image(string markup)
        {
            if (string.IsNullOrEmpty(markup))
            {
                return null;
            }

            int at = markup.IndexOf("src=", StringComparison.Ordinal);

            if (at < 0)
            {
                return null;
            }

            int start = at + 4;
            int end = start;

            while (end < markup.Length && markup[end] != ' ' && markup[end] != '>' && markup[end] != '"')
            {
                end++;
            }

            return Material(markup.Substring(start, end - start));
        }

        /// <summary>
        /// The texture of a UI material. These use the game's older material syntax, where the
        /// texture is named plainly and relative to the material's own folder.
        /// </summary>
        public static Texture2D Material(string materialPath)
        {
            if (string.IsNullOrEmpty(materialPath) || EtsEngine.Files == null)
            {
                return null;
            }

            string texture = TexturePathOf(EtsEngine.Files, materialPath);
            return texture == null ? null : EtsEngine.Textures.Load(texture, false);
        }

        /// <summary>
        /// The texture a UI material names - its base texture, or its plain "texture", relative to the material's own
        /// folder unless absolute - or null. Pure (EtsFlow prefetches the HQ map's texture off the main thread with it).
        /// </summary>
        public static string TexturePathOf(EtsFileSystem files, string materialPath)
        {
            string text = files != null && !string.IsNullOrEmpty(materialPath) ? files.ReadText(materialPath) : null;

            if (text == null)
            {
                return null;
            }

            EtsMaterialDef def = EtsMaterialDef.Parse(text, materialPath);
            string texture = def.BaseTexture;

            if (string.IsNullOrEmpty(texture) && def.Textures.TryGetValue("texture", out string plain))
            {
                texture = plain;
            }

            if (string.IsNullOrEmpty(texture))
            {
                return null;
            }

            if (!texture.StartsWith("/", StringComparison.Ordinal))
            {
                int slash = materialPath.LastIndexOf('/');
                texture = (slash >= 0 ? materialPath.Substring(0, slash + 1) : "/") + texture;
            }

            return texture;
        }

        public static TextMeshProUGUI Label(RectTransform rect, string content, float size, TextAlignmentOptions alignment, Color colour)
        {
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.text = content;

            // Every label is set in the game's own face when it can be read from the files.
            TMP_FontAsset face = EtsFont.Get(EtsFont.Normal);

            if (face != null)
            {
                label.font = face;
            }

            label.fontSize = size;
            label.alignment = alignment;
            label.color = colour;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.raycastTarget = false;
            return label;
        }

        /// <summary>
        /// Draws one of the game's textures. A DDS stores its top row first and Unity's raw upload
        /// puts that first row at v = 0, which a screen quad draws at its bottom - so a picture put
        /// straight on screen comes out upside down, as the launchpad's logo did. (Models are not
        /// affected: their own v addresses the same row as in the game.) A raw image with its v
        /// range reversed is the cheapest way to put it the right way up, and it avoids building a
        /// sprite for every picture as well.
        /// </summary>
        public static RawImage Picture(RectTransform rect, Texture2D texture, bool preserveAspect)
        {
            var image = rect.gameObject.AddComponent<RawImage>();
            image.texture = texture;
            image.uvRect = new Rect(0f, 1f, 1f, -1f);
            image.raycastTarget = false;

            if (texture != null && preserveAspect)
            {
                var fitter = rect.gameObject.AddComponent<AspectRatioFitter>();
                fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fitter.aspectRatio = (float)texture.width / Mathf.Max(1, texture.height);
            }

            return image;
        }

        /// <summary>
        /// A picture kept at its texture's shape INSIDE rect (centred, as large as fits). Picture(rect, texture, true)
        /// puts an AspectRatioFitter on rect itself, whose FitInParent sizes rect to fit rect's PARENT - in a 1440 x 900
        /// frame that is the whole frame (probe 2045: the desktop's ETS2 logo, placed 600 x 300, drawn 1440 wide). The
        /// new screens use this; the old front end keeps Picture as it has always drawn.
        /// </summary>
        public static RawImage PictureFit(RectTransform rect, Texture2D texture)
        {
            RectTransform inner = Place(rect, "picture", 0f, 1f, 0f, 1f);
            inner.anchorMin = Vector2.zero;
            inner.anchorMax = Vector2.one;
            inner.offsetMin = Vector2.zero;
            inner.offsetMax = Vector2.zero;
            inner.gameObject.layer = rect.gameObject.layer;
            RawImage image = Picture(inner, texture, false);

            if (texture != null)
            {
                var fitter = inner.gameObject.AddComponent<AspectRatioFitter>();
                fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fitter.aspectRatio = (float)texture.width / Mathf.Max(1, texture.height);
            }

            return image;
        }

        // ------------------------------------------------------------------ the game's own chrome

        /// <summary>
        /// The colour of the game's window panels, measured off /material/ui/window/bcg2.mat: a
        /// near-black sheet averaging 18,18,18. Used where a plain plate is wanted.
        /// </summary>
        public static readonly Color PanelColour = new Color(0.07f, 0.072f, 0.078f);

        private static EtsUiPainter _painter;

        /// <summary>
        /// The compositor that paints the game's chrome the way the game's markup builds it - see
        /// EtsUiPainter. It reads the same atlases the templates name and cuts them the same way;
        /// its output was written to PNG and looked at before it was trusted here.
        /// </summary>
        public static EtsUiPainter Painter
        {
            get
            {
                if (_painter == null)
                {
                    // The material reader only matters for the 1.61 SDF icons (their aux[] colours); every
                    // painting today's screens ask for goes through LoadBitmap exactly as before.
                    _painter = new EtsUiPainter(LoadBitmap, ReadMaterialText);
                }

                return _painter;
            }
        }

        private static string ReadMaterialText(string materialPath)
        {
            return EtsEngine.Files != null && !string.IsNullOrEmpty(materialPath) ? EtsEngine.Files.ReadText(materialPath) : null;
        }

        private static EtsBitmap LoadBitmap(string materialPath)
        {
            if (EtsEngine.Files == null || string.IsNullOrEmpty(materialPath))
            {
                return null;
            }

            string text = EtsEngine.Files.ReadText(materialPath);

            if (text == null)
            {
                return null;
            }

            EtsMaterialDef def = EtsMaterialDef.Parse(text, materialPath);
            string tobjPath = def.BaseTexture;

            if (string.IsNullOrEmpty(tobjPath) && def.Textures.TryGetValue("texture", out string plain))
            {
                tobjPath = plain;
            }

            if (string.IsNullOrEmpty(tobjPath))
            {
                return null;
            }

            if (!tobjPath.StartsWith("/", StringComparison.Ordinal))
            {
                int slash = materialPath.LastIndexOf('/');
                tobjPath = (slash >= 0 ? materialPath.Substring(0, slash + 1) : "/") + tobjPath;
            }

            // A packed image on 1.61, a tobj and a DDS on 1.48; ReadDds takes whichever is there.
            EtsDds dds = EtsTextureCache.ReadDds(EtsEngine.Files, tobjPath);
            return dds == null ? null : EtsPixels.Decode(dds);
        }

        /// <summary>A painted bitmap as a texture, rows turned over into Unity's order.</summary>
        public static Texture2D ToTexture(EtsBitmap bitmap)
        {
            if (bitmap == null)
            {
                return null;
            }

            var flipped = new byte[bitmap.Rgba.Length];
            int stride = bitmap.Width * 4;

            for (int y = 0; y < bitmap.Height; y++)
            {
                Buffer.BlockCopy(bitmap.Rgba, y * stride, flipped, (bitmap.Height - 1 - y) * stride, stride);
            }

            var texture = new Texture2D(bitmap.Width, bitmap.Height, TextureFormat.RGBA32, false, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            texture.LoadRawTextureData(flipped);
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>Shows a painted bitmap in a rectangle.</summary>
        public static RawImage Painted(RectTransform rect, EtsBitmap bitmap)
        {
            var image = rect.gameObject.AddComponent<RawImage>();
            image.texture = ToTexture(bitmap);
            image.raycastTarget = false;
            return image;
        }

        /// <summary>The size a rectangle was placed at, in the layout's pixels.</summary>
        public static int PixelWidth(RectTransform rect)
        {
            return Mathf.Max(2, Mathf.RoundToInt(rect.sizeDelta.x));
        }

        public static int PixelHeight(RectTransform rect)
        {
            return Mathf.Max(2, Mathf.RoundToInt(rect.sizeDelta.y));
        }

        /// <summary>
        /// Fills a rectangle with the game's window - txt.window.dyn_bcg.nl, or the dark variant
        /// the news boxes use - painted at the rectangle's own size from the game's atlases.
        /// </summary>
        public static void WindowPlate(RectTransform rect, bool dark)
        {
            EtsBitmap painted = Painter.Window(PixelWidth(rect), PixelHeight(rect), dark);
            Painted(rect, painted);
        }

        /// <summary>Kept for callers that pass an opacity: the window is painted, opacity ignored.</summary>
        public static void WindowPlate(RectTransform rect, float alpha)
        {
            WindowPlate(rect, false);
        }

        /// <summary>
        /// One of the game's screen-link icons, /material/ui/link/&lt;name&gt;.mat - a 128 x 64
        /// image whose left half is the icon at rest and right half the icon lit.
        /// </summary>
        public static RawImage LinkIcon(RectTransform rect, string linkName, bool lit)
        {
            return Painted(rect, Painter.Half("/material/ui/link/" + linkName + ".mat", lit, PixelWidth(rect), PixelHeight(rect), 0xFFFFFFFF));
        }

        // ------------------------------------------------------------------ 1.61: the game's own looks

        private static readonly Dictionary<string, Texture2D> SdfTextures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Vector2Int> TextureSizes = new Dictionary<string, Vector2Int>(StringComparer.Ordinal);

        /// <summary>
        /// The eight faces the 1.61 look templates name in face= ([gameui §2.5], probe 1652): every page a tablet shows is
        /// set in some of them, and building one (an atlas decode plus a TextMeshPro font, EtsFont.Build) is most of a
        /// page's cold first build (E's Phase 1 measure: ~209 ms cold, 6-7 ms warm). EtsFlow warms them one a frame in
        /// the HQ void.
        /// </summary>
        public static readonly string[] TemplateFaces =
        {
            "/font/normal.font", "/font/normal_bold.font", "/font/big.font", "/font/big_bold.font",
            "/font/title.font", "/font/title_bold.font", "/font/small.font", "/font/small_bold.font"
        };

        /// <summary>
        /// A signed-distance-field icon (effect "ui.sdf.rfx": the Jobs, Drive and Navigation links, the city pins,
        /// /material/ui/icon/star_orange.mat for the master badge, PLAN §3.7) painted at a size by EtsUiPainter.Sdf
        /// - probe 1882 matched its painting of six icons with png/sdf_icons.png pixel for pixel, and painted
        /// star_orange (39 x 37 nominal) as a clean orange five-pointed star; star_full_n.mat, the fallback the plan
        /// named, does not exist on 1.61 (PLAN.md §7.D; [gameui §2.4]). Painted once per material and size and
        /// kept. Null when the archives are not mounted or the material is not an SDF icon.
        /// </summary>
        public static Texture2D SdfIcon(string materialPath, int width, int height)
        {
            if (EtsEngine.Files == null || string.IsNullOrEmpty(materialPath) || width <= 0 || height <= 0)
            {
                return null;
            }

            string key = materialPath + "|" + width + "x" + height;

            if (SdfTextures.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }

            EtsBitmap painted = Painter.Sdf(materialPath, width, height, 0xFFFFFFFFu);
            Texture2D texture = painted != null ? ToTexture(painted) : null;
            SdfTextures[key] = texture;
            return texture;
        }

        /// <summary>
        /// One of the game's look templates (txt.window.bcg_rect4, btn.normal, btn.city_pin, btn.desktop_link, ...)
        /// placed at a rectangle of the 1440 x 900 frame with its value (the |-separated %0..%F), and clickable when
        /// onClick is given (PLAN.md §7.D, owner D; [gameui §2.3, §4]). Null when the archives are not mounted or
        /// there is no such template.
        ///
        /// The template's markup (a text template's "text"; a button's n_pml, with s_pml shown under the pointer and
        /// d_pml when SetEnabled(rect, false)) is laid out by EtsPml - the reading probe 1882 matched with
        /// png/pml_test.png pixel for pixel - and drawn on the GPU: one RawImage per image operation, cut from the
        /// material's texture by uvRect (mirrored where the markup mirrors, a single texel where it stretches one),
        /// tinted by the operation's colour; an SDF icon from its painting (SdfIcon). Text operations become
        /// TextMeshPro labels in the game's faces (EtsFont), their runs' colours and gaps as rich text. Painting a
        /// whole window on the CPU instead would cost tens of milliseconds a screen (1.26 million pixels for a
        /// full-frame bcg_rect4); the RawImages cost nothing per frame.
        /// </summary>
        public static RectTransform Template(RectTransform host, string lookTemplate, string value,
                                             float left, float right, float bottom, float top, Action onClick = null)
        {
            if (host == null || string.IsNullOrEmpty(lookTemplate) || EtsEngine.Files == null)
            {
                return null;
            }

            EtsSiiUnit template = EtsUiLayout.Template(EtsEngine.Files, lookTemplate);

            if (template == null)
            {
                return null;
            }

            RectTransform rect = Place(host, lookTemplate, left, right, bottom, top);
            rect.gameObject.layer = host.gameObject.layer;
            BuildLook(rect, template, value, onClick, lookTemplate + (string.IsNullOrEmpty(value) ? "" : " \"" + value + "\""));
            return rect;
        }

        /// <summary>
        /// A template's states laid out in rect (Template's work, shared with BuildLayout): the "n" layer (a text
        /// template's text, a button's n_pml) shown, and a button's s_pml, p_pml and d_pml built hidden for EtsUiLook to
        /// swap in; a button (or anything given a click) also gets a transparent pointer target and a Button. Returns
        /// the EtsUiLook, or null for a plain text template.
        /// </summary>
        private static EtsUiLook BuildLook(RectTransform rect, EtsSiiUnit template, string value, Action onClick, string what)
        {
            float w = Mathf.Max(1f, rect.sizeDelta.x), h = Mathf.Max(1f, rect.sizeDelta.y);
            string[] args = EtsPml.Arguments(value);
            bool button = template.Has("n_pml");

            RectTransform normal = Layer(rect, "n", template.GetString(button ? "n_pml" : "text", ""), args, w, h);
            RectTransform hover = button && template.Has("s_pml") ? Layer(rect, "s", template.GetString("s_pml", ""), args, w, h) : null;
            RectTransform pressed = button && template.Has("p_pml") ? Layer(rect, "p", template.GetString("p_pml", ""), args, w, h) : null;
            RectTransform disabled = button && template.Has("d_pml") ? Layer(rect, "d", template.GetString("d_pml", ""), args, w, h) : null;

            if (hover != null) hover.gameObject.SetActive(false);
            if (pressed != null) pressed.gameObject.SetActive(false);
            if (disabled != null) disabled.gameObject.SetActive(false);

            if (!button && onClick == null)
            {
                return null;
            }

            var catcher = rect.gameObject.AddComponent<Image>();
            catcher.color = new Color(1f, 1f, 1f, 0f);
            catcher.raycastTarget = true;

            var look = rect.gameObject.AddComponent<EtsUiLook>();
            look.Normal = normal != null ? normal.gameObject : null;
            look.Hover = hover != null ? hover.gameObject : null;
            look.Pressed = pressed != null ? pressed.gameObject : null;
            look.Disabled = disabled != null ? disabled.gameObject : null;

            var click = rect.gameObject.AddComponent<Button>();
            click.targetGraphic = catcher;
            click.transition = Selectable.Transition.None;
            click.onClick.AddListener(() =>
            {
                Note("pressed " + what);
                onClick?.Invoke();
            });

            return look;
        }

        /// <summary>A Template button greyed the game's way (its d_pml) and deaf to the pointer, or live again.</summary>
        public static void SetEnabled(RectTransform templateRect, bool enabled)
        {
            if (templateRect == null)
            {
                return;
            }

            var look = templateRect.GetComponent<EtsUiLook>();
            if (look != null) look.SetEnabled(enabled);
            var click = templateRect.GetComponent<Button>();
            if (click != null) click.interactable = enabled;
        }

        /// <summary>A Template button shown in its chosen state (p_pml: the orange city pin, the pressed tab), or not.</summary>
        public static void SetChosen(RectTransform templateRect, bool chosen)
        {
            var look = templateRect != null ? templateRect.GetComponent<EtsUiLook>() : null;
            if (look != null) look.SetChosen(chosen);
        }

        /// <summary>
        /// A 1.61 screen built from its layout file into a 1440 x 900 frame (the tablet page's Root, a surface from
        /// EtsUiSurface.Create, the HQ stage's world-space canvas), the way [gameui §6] asks BuildScreen to become:
        /// every element of EtsUiLayout.Plan (drawn by layer, then file order; hidden keys and their children left out)
        /// placed at its rectangle under a GameObject named by its key and drawn by its kind - a look template through
        /// its markup (BuildLook, every state), its own markup (Layer), a ui_fullscreen fill in its color; portals and
        /// engine-drawn elements (maps, 3D scenes, link text) become empty rectangles for the caller to fill. values
        /// replace an element's value by key; an element with no entry keeps the layout file's own, which for most text
        /// is a sample ("Famouse Bridge") and for buttons the real words ("@@take_job@@"). Null when the archives are not
        /// mounted or the layout cannot be read.
        ///
        /// Cost: one RawImage per image operation and one TextMeshPro label per text block - built once, nothing per
        /// frame. The build itself runs in the frame it is asked for (tens of labels), which is why screens are built on
        /// events (a page opened, a loading screen shown), never per frame.
        /// </summary>
        public static EtsGameScreen BuildLayout(RectTransform frame, string uiPath, IDictionary<string, string> values, ICollection<string> hidden)
        {
            return BuildLayout(frame, uiPath, values, hidden, null);
        }

        /// <summary>As BuildLayout, keeping only the elements keep accepts (EtsUiLayout.Plan).</summary>
        public static EtsGameScreen BuildLayout(RectTransform frame, string uiPath, IDictionary<string, string> values, ICollection<string> hidden,
                                                Func<EtsUiElement, bool> keep)
        {
            if (frame == null || EtsEngine.Files == null)
            {
                return null;
            }

            List<EtsUiElement> elements = EtsUiLayout.Read(EtsEngine.Files, uiPath);

            if (elements.Count == 0)
            {
                return null;
            }

            var screen = new EtsGameScreen(uiPath, frame, EtsUiLayout.Plan(elements, hidden, keep));

            foreach (EtsUiElement e in screen.Elements)
            {
                string value = values != null && values.TryGetValue(e.Key, out string given) ? given : e.Value;
                screen.Add(e, value);
            }

            return screen;
        }

        /// <summary>One planned element built under frame by its kind (EtsGameScreen.Add and Set).</summary>
        internal static RectTransform BuildElement(RectTransform frame, EtsUiElement e, string value, Action click)
        {
            RectTransform rect = Place(frame, e.Key.Length > 0 ? e.Key : "element", e.Left, e.Right, e.Bottom, e.Top);
            rect.gameObject.layer = frame.gameObject.layer;

            switch (e.Kind)
            {
                case EtsUiKind.Template:
                {
                    EtsSiiUnit template = EtsUiLayout.Template(EtsEngine.Files, e.Look);

                    if (template != null && template.Has("middle_cell_pml"))
                    {
                        // A table background: its cells laid out row by row (EtsUiLayout.TableCells).
                        float height = rect.sizeDelta.y;

                        foreach (EtsUiCell cell in EtsUiLayout.TableCells(EtsEngine.Files, e, value))
                        {
                            RectTransform box = Place(rect, "cell", cell.X, cell.X + cell.W, height - cell.Y - cell.H, height - cell.Y);
                            box.gameObject.layer = frame.gameObject.layer;
                            Layer(box, "n", cell.Markup, null, cell.W, cell.H);
                        }
                    }
                    else if (template != null)
                    {
                        // Only a button template takes the pointer; a text template must let it through to what is
                        // under it (the HQ title lies over the map's portal).
                        BuildLook(rect, template, value, template.Has("n_pml") ? click : null, e.Key + " [" + e.Look + "]");
                    }

                    break;
                }

                case EtsUiKind.Markup:
                    Layer(rect, "n", e.Text, EtsPml.Arguments(value), rect.sizeDelta.x, rect.sizeDelta.y);
                    break;

                case EtsUiKind.Fill:
                {
                    var fill = rect.gameObject.AddComponent<Image>();
                    fill.color = FillColour(e.Unit.GetString("color", null));
                    fill.raycastTarget = false;
                    break;
                }
            }

            return rect;
        }

        /// <summary>A ui_fullscreen color, "(0, 0, 0)" in 0..255 (loading.sii's black overlay); black when unreadable.</summary>
        private static Color FillColour(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Color.black;
            }

            string[] parts = value.Trim('(', ')', ' ').Split(',');
            float[] c = { 0f, 0f, 0f };

            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out c[i]);
            }

            return new Color(c[0] / 255f, c[1] / 255f, c[2] / 255f, 1f);
        }

        /// <summary>
        /// One state of a template (its n_pml, s_pml, d_pml or text), laid out and built under rect: a RawImage per
        /// image operation, a label per text block. Null for empty markup.
        /// </summary>
        public static RectTransform Layer(RectTransform rect, string name, string markup, string[] args, float width, float height)
        {
            if (string.IsNullOrEmpty(markup))
            {
                return null;
            }

            List<EtsPmlOp> ops = EtsPml.Layout(markup, args, Mathf.RoundToInt(width), Mathf.RoundToInt(height), TemplateTextureSize, Painter.IsSdf);
            RectTransform layer = Place(rect, name, 0f, width, 0f, height);
            layer.anchorMin = Vector2.zero;
            layer.anchorMax = Vector2.one;
            layer.offsetMin = Vector2.zero;
            layer.offsetMax = Vector2.zero;
            layer.gameObject.layer = rect.gameObject.layer;

            foreach (EtsPmlOp op in ops)
            {
                // The markup counts y down from the top; Place counts it up from the bottom.
                RectTransform piece = Place(layer, op.IsText ? "text" : "img", op.X, op.X + op.W, height - op.Y - op.H, height - op.Y);
                piece.gameObject.layer = rect.gameObject.layer;

                if (op.IsText)
                {
                    TextBlock(piece, op);
                }
                else
                {
                    ImageOp(piece, op);
                }
            }

            return layer;
        }

        /// <summary>
        /// The size markup coordinates of a material refer to, kept per material: an SDF icon's nominal aux[0] size
        /// (EtsUiPainter.TrySize), otherwise the texture's own, from the GPU texture Material loads anyway - not from a
        /// CPU decode, which for a 2048-wide loading photograph would cost tens of milliseconds just to learn its size.
        /// </summary>
        private static Vector2Int TemplateTextureSize(string materialPath)
        {
            if (string.IsNullOrEmpty(materialPath))
            {
                return Vector2Int.zero;
            }

            if (TextureSizes.TryGetValue(materialPath, out Vector2Int size))
            {
                return size;
            }

            if (Painter.IsSdf(materialPath))
            {
                size = Painter.TrySize(materialPath, out int w, out int h) ? new Vector2Int(w, h) : Vector2Int.zero;
            }
            else
            {
                Texture2D texture = Material(materialPath);
                size = texture != null ? new Vector2Int(texture.width, texture.height) : Vector2Int.zero;
            }

            TextureSizes[materialPath] = size;
            return size;
        }

        private static void ImageOp(RectTransform piece, EtsPmlOp op)
        {
            Vector2Int size = TemplateTextureSize(op.Material);
            Texture2D texture;
            bool whole = op.SrcX == 0 && op.SrcY == 0 && op.SrcW == size.x && op.SrcH == size.y;

            if (op.IsSdf)
            {
                // Painted at the size it is drawn when the whole icon is used (sharp); else cut from the nominal painting.
                texture = whole ? SdfIcon(op.Material, Mathf.Max(1, Mathf.RoundToInt(op.W)), Mathf.Max(1, Mathf.RoundToInt(op.H)))
                                : SdfIcon(op.Material, size.x, size.y);
            }
            else
            {
                texture = Material(op.Material);
            }

            var image = piece.gameObject.AddComponent<RawImage>();
            image.texture = texture;
            image.color = ToColor(op.Argb);
            image.raycastTarget = false;

            if (texture == null || size.x <= 0 || size.y <= 0)
            {
                image.enabled = false;
                return;
            }

            if (op.IsSdf && whole)
            {
                // SdfIcon's rows are already turned the right way up (ToTexture).
                image.uvRect = new Rect(0f, 0f, 1f, 1f);
                return;
            }

            // Source pixels (y down from the top) to uv. A game texture's first row is its top and lands at v = 0
            // (Picture's note), so the rect's top edge is v = srcY / h; a painted SDF was turned over by ToTexture.
            float w = size.x, hh = size.y;
            float u0 = op.SrcX / w, uw = op.SrcW / w;
            float vTop = op.SrcY / hh, vh = op.SrcH / hh;

            // A zero- or one-pixel source is one texel stretched: sample its centre so neighbours do not bleed in.
            if (Mathf.Abs(op.SrcW) <= 1) { u0 = (Mathf.Min(op.SrcX, op.SrcX + op.SrcW) + 0.5f) / w; uw = 0f; }
            if (Mathf.Abs(op.SrcH) <= 1) { vTop = (Mathf.Min(op.SrcY, op.SrcY + op.SrcH) + 0.5f) / hh; vh = 0f; }

            image.uvRect = op.IsSdf ? new Rect(u0, 1f - vTop - vh, uw, vh) : new Rect(u0, vTop + vh, uw, -vh);
        }

        private static void TextBlock(RectTransform piece, EtsPmlOp op)
        {
            string face = string.IsNullOrEmpty(op.FontPath) ? EtsFont.Normal : op.FontPath;
            TextAlignmentOptions alignment =
                op.VAlign == 1 ? (op.HAlign == 1 ? TextAlignmentOptions.Center : op.HAlign == 2 ? TextAlignmentOptions.Right : TextAlignmentOptions.Left) :
                op.VAlign == 2 ? (op.HAlign == 1 ? TextAlignmentOptions.Bottom : op.HAlign == 2 ? TextAlignmentOptions.BottomRight : TextAlignmentOptions.BottomLeft) :
                                 (op.HAlign == 1 ? TextAlignmentOptions.Top : op.HAlign == 2 ? TextAlignmentOptions.TopRight : TextAlignmentOptions.TopLeft);
            TextMeshProUGUI label = Label(piece, op.Text ?? "", face, alignment, ToColor(op.Argb));
            label.richText = true;
            label.fontSize = EtsFont.Size(face) * (op.YScale > 0f ? op.YScale : 1f);
            label.textWrappingMode = op.Wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
        }

        /// <summary>
        /// A button the way btn.normal makes one: the btn_dark strip drawn edge to edge, the
        /// caption in the big face, btn_normal_focus laid over it while the pointer rests on it.
        /// </summary>
        public static Button MakeButton(RectTransform rect, string label, Action onClick)
        {
            int w = PixelWidth(rect), h = PixelHeight(rect);
            RawImage plate = Painted(rect, Painter.Strip("/material/ui/button/btn_dark.mat", w, h, 0xFFFFFFFF));
            plate.raycastTarget = true;

            // The lit face, shown only under the pointer. The game overlays btn_normal_focus; a
            // second painted plate switched on and off does the same.
            RectTransform litRect = Place(rect, "lit", 0f, w, 0f, h);
            litRect.anchorMin = Vector2.zero;
            litRect.anchorMax = Vector2.one;
            litRect.offsetMin = Vector2.zero;
            litRect.offsetMax = Vector2.zero;
            RawImage lit = Painted(litRect, Painter.Strip("/material/ui/button/btn_normal_focus.mat", w, h, 0xFFFFFFFF));
            lit.color = new Color(1f, 1f, 1f, 0f);

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = lit;
            button.transition = Selectable.Transition.ColorTint;
            var colours = button.colors;
            colours.normalColor = new Color(1f, 1f, 1f, 0f);
            colours.highlightedColor = new Color(1f, 1f, 1f, 1f);
            colours.selectedColor = new Color(1f, 1f, 1f, 0f);
            colours.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colours.fadeDuration = 0.08f;
            button.colors = colours;

            RectTransform text = Place(rect, "label", 0f, w, 0f, h);
            text.anchorMin = Vector2.zero;
            text.anchorMax = Vector2.one;
            text.offsetMin = new Vector2(6f, 1f);
            text.offsetMax = new Vector2(-6f, -1f);
            TextMeshProUGUI caption = Label(text, label, EtsFont.Big, TextAlignmentOptions.Center, TextColour);
            caption.enableAutoSizing = true;
            caption.fontSizeMin = 10f;
            caption.fontSizeMax = Mathf.Max(12f, EtsFont.Size(EtsFont.Big));

            string pressed = label;
            button.onClick.AddListener(() =>
            {
                Note("pressed \"" + pressed + "\"");
                onClick?.Invoke();
            });

            return button;
        }

        /// <summary>
        /// @@clr_txt@@ and @@clr_sel@@ as the game's screens use them: its body text, and the amber it selects and
        /// titles with. Under GameLook they are the locale's own (clr_txt FFECECEC, clr_sel FF03B0FA read
        /// AABBGGRR: 236,236,236 and 250,176,3); until then the values measured by eye on 1.48 that today's
        /// screens were tuned with.
        /// </summary>
        public static Color TextColour => GameLook ? Colour("clr_txt", OldTextColour) : OldTextColour;

        public static Color SelectedColour => GameLook ? Colour("clr_sel", OldSelectedColour) : OldSelectedColour;

        private static readonly Color OldTextColour = new Color(0.86f, 0.87f, 0.89f);
        private static readonly Color OldSelectedColour = new Color(1f, 0.78f, 0.24f);

        /// <summary>A label in one of the game's faces at the size the game draws it.</summary>
        public static TextMeshProUGUI Label(RectTransform rect, string content, string fontPath, TextAlignmentOptions alignment, Color colour)
        {
            TextMeshProUGUI label = Label(rect, content, EtsFont.Size(fontPath), alignment, colour);
            EtsFont.Apply(label, fontPath, EtsFont.Size(fontPath));
            return label;
        }

        /// <summary>
        /// Builds a screen straight from its layout file. Every unit that carries a rectangle is
        /// placed: images from their markup, text from its locale key, buttons from the btn look.
        /// </summary>
        public static GameObject BuildScreen(string uiPath, int sortingOrder, Dictionary<string, Action> actions, out string report)
        {
            return BuildScreen(uiPath, sortingOrder, actions, null, out report, out _);
        }

        /// <summary>
        /// Builds a screen, replacing the value of any element whose look template is named in
        /// <paramref name="overrides"/> - which is how the loading screen gets today's picture,
        /// its caption and the job line without departing from the game's own layout. The elements
        /// created are handed back by look template so a caller can keep updating them.
        /// </summary>
        public static GameObject BuildScreen(string uiPath, int sortingOrder, Dictionary<string, Action> actions,
            Dictionary<string, string> overrides, out string report, out Dictionary<string, RectTransform> byLook)
        {
            report = "";
            byLook = new Dictionary<string, RectTransform>(StringComparer.Ordinal);

            if (EtsEngine.Files == null)
            {
                report = "the game's archives are not mounted";
                return null;
            }

            EtsSiiFile screen = EtsEngine.Files.ReadSii(uiPath);

            if (screen == null || screen.Units.Count == 0)
            {
                report = uiPath + " could not be read";
                return null;
            }

            // A canvas of its own, as always - or, inside a tablet page's EtsUiHost.Into scope, a frame in that page
            // (EtsUiSurface; with no scope open, which is every call while EtsFeatures.NewFlow is false, it makes
            // exactly the canvas CreateCanvas made here before).
            GameObject surface = EtsUiSurface.Create("Ets screen " + uiPath, sortingOrder);
            var frame = Place(surface.transform, "frame", 0f, ReferenceWidth, 0f, ReferenceHeight);

            // The game's screens cover everything behind them. Without this the world showed
            // through the launchpad, along with the editor's own overlay text.
            RectTransform backdropRect = Place(frame, "backdrop", 0f, ReferenceWidth, 0f, ReferenceHeight);
            backdropRect.anchorMin = Vector2.zero;
            backdropRect.anchorMax = Vector2.one;
            backdropRect.offsetMin = Vector2.zero;
            backdropRect.offsetMax = Vector2.zero;
            var backdrop = backdropRect.gameObject.AddComponent<Image>();
            backdrop.color = new Color(0.04f, 0.05f, 0.07f, 1f);
            backdrop.raycastTarget = true;

            int placed = 0, pictures = 0, buttons = 0;

            foreach (EtsSiiUnit unit in screen.Units)
            {
                if (!unit.Has("coords_l") || !unit.Has("coords_t"))
                {
                    continue;
                }

                float width = Mathf.Abs(unit.GetFloat("coords_r", 0f) - unit.GetFloat("coords_l", 0f));

                // The outermost window is the frame itself, already made.
                if (width >= ReferenceWidth - 1f)
                {
                    continue;
                }

                string look = unit.GetString("look_template", "") ?? "";
                string markup = unit.GetString("text", "") ?? "";
                string value = unit.GetString("value", "") ?? "";

                // An override may name a look template or the end of a unit's name - the delivery
                // summary has five values all in txt.emph.right, told apart only by names such as
                // "_nameless._.driven.distance.val".
                if (overrides != null)
                {
                    string replacement = null;

                    if (look.Length > 0 && overrides.TryGetValue(look, out string byLookValue))
                    {
                        replacement = byLookValue;
                    }
                    else if (!string.IsNullOrEmpty(unit.Name))
                    {
                        foreach (KeyValuePair<string, string> candidate in overrides)
                        {
                            if (candidate.Key.Length > 0 && unit.Name.EndsWith(candidate.Key, StringComparison.Ordinal))
                            {
                                replacement = candidate.Value;
                                break;
                            }
                        }
                    }

                    if (replacement != null)
                    {
                        value = replacement;
                        markup = "";
                    }
                }

                // Sliders and scroll bars are furniture for lists this world does not have.
                if (look.StartsWith("sld", StringComparison.Ordinal))
                {
                    continue;
                }

                RectTransform rect = Place(frame, unit, look.Length > 0 ? look : "element");
                placed++;

                if (look.Length > 0 && !byLook.ContainsKey(look))
                {
                    byLook[look] = rect;
                }

                if (markup.Contains("<img"))
                {
                    Texture2D texture = Image(markup);
                    Picture(rect, texture, !markup.Contains("yscale=stretch"));
                    if (texture != null) pictures++;
                    continue;
                }

                // Some elements name a material directly as their value - the loading screen's
                // photograph is written exactly that way.
                if (value.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                {
                    Texture2D picture = Material(value);
                    Picture(rect, picture, false);
                    if (picture != null) pictures++;
                    continue;
                }

                if (look.StartsWith("btn", StringComparison.Ordinal))
                {
                    string caption = Text(value);

                    // A button whose wording cannot be read would be a blank plate. The game only
                    // shows these when they have something to say, so leave them out.
                    if (caption.Length == 0)
                    {
                        UnityEngine.Object.Destroy(rect.gameObject);
                        placed--;
                        continue;
                    }

                    actions.TryGetValue(caption, out Action action);
                    MakeButton(rect, caption, action);
                    buttons++;
                    continue;
                }

                // A window's dynamic background is the panel the rest sits on. Its value holds
                // layout numbers rather than words, so nothing is written on it.
                if (look.Contains("window") || look.Contains("bcg"))
                {
                    // txt.window.bcg_rect is a plain rounded rectangle in the colour its value
                    // names (F0090909 on the launchpad); the dyn_bcg templates are the painted
                    // frames.
                    if (look.Contains("bcg_rect"))
                    {
                        var plate = rect.gameObject.AddComponent<Image>();
                        plate.color = ArgbColour(value, new Color(0.035f, 0.035f, 0.035f, 0.94f));
                        plate.raycastTarget = false;
                    }
                    else
                    {
                        WindowPlate(rect, look.Contains("dark"));
                    }

                    rect.SetAsFirstSibling();
                    continue;
                }

                string words = Text(value);

                if (words.Length == 0)
                {
                    UnityEngine.Object.Destroy(rect.gameObject);
                    placed--;
                    continue;
                }

                // The template names the face: txt.title.* is set in title.font, txt.big.* and
                // txt.emph.big.* in big.font, everything else in normal.font.
                bool title = look.Contains("title");
                bool big = look.Contains("big");
                string face = title ? EtsFont.Title : big ? EtsFont.Big : EtsFont.Normal;
                TextAlignmentOptions alignment = look.Contains("center") ? TextAlignmentOptions.Center :
                                                 look.Contains("right") ? TextAlignmentOptions.Right : TextAlignmentOptions.Left;
                Color colour = title || look.Contains("orange") || look.Contains("emph") ? SelectedColour : TextColour;
                Label(rect, words, face, alignment, colour);
            }

            report = uiPath + ": " + placed + " elements (" + pictures + " of the game's own images, " + buttons + " buttons)";
            return surface;
        }
    }

    /// <summary>
    /// The states of a look template made by EtsGameUi.Template or BuildLayout: its n_pml at rest, its s_pml while
    /// the pointer is on it (the game's "selected" state) or while Highlighted from outside (the HQ map shows
    /// watchers the master's hover so), its p_pml while chosen (the orange city pin, a pressed tab), its d_pml when
    /// disabled. Swaps whole layers on pointer events and calls, so nothing runs per frame. Hovered reports the
    /// pointer arriving and leaving (the HQ map's tooltip).
    /// </summary>
    public sealed class EtsUiLook : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
    {
        public GameObject Normal;
        public GameObject Hover;
        public GameObject Pressed;
        public GameObject Disabled;

        /// <summary>Raised with true when the pointer arrives, false when it leaves.</summary>
        public event Action<bool> Hovered;

        private bool _enabled = true;
        private bool _over;
        private bool _chosen;
        private bool _highlighted;

        public bool IsEnabled => _enabled;
        public bool IsChosen => _chosen;

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            Show();
        }

        public void SetChosen(bool chosen)
        {
            _chosen = chosen;
            Show();
        }

        public void SetHighlighted(bool highlighted)
        {
            _highlighted = highlighted;
            Show();
        }

        public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData)
        {
            _over = true;
            Show();
            Hovered?.Invoke(true);
        }

        public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData)
        {
            _over = false;
            Show();
            Hovered?.Invoke(false);
        }

        private void OnDisable()
        {
            _over = false;
            Show();
        }

        private void Show()
        {
            bool disabled = !_enabled && Disabled != null;
            bool chosen = !disabled && _chosen && Pressed != null;
            bool hover = !disabled && !chosen && (_enabled && _over || _highlighted) && Hover != null;
            if (Normal != null) Normal.SetActive(!disabled && !chosen && !hover);
            if (Hover != null) Hover.SetActive(hover);
            if (Pressed != null) Pressed.SetActive(chosen);
            if (Disabled != null) Disabled.SetActive(disabled);
        }
    }

    /// <summary>
    /// A 1.61 screen built by EtsGameUi.BuildLayout: its elements by key ("loading.bar.upper", "confirm.btn"), for the
    /// screen's owner to fill, click, show, hide, grey or re-value after the build (owner D; [gameui §6]). Set rebuilds
    /// one element with a new value (its look laid out again), which is how a page's lines follow the game state;
    /// it is called on events, never per frame.
    /// </summary>
    public sealed class EtsGameScreen
    {
        private sealed class Entry
        {
            public EtsUiElement Element;
            public RectTransform Rect;
            public string Value;
            public Action Click;
            public bool Enabled = true;
            public bool Chosen;
        }

        private readonly Dictionary<string, Entry> _byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public string UiPath { get; }

        /// <summary>The 1440 x 900 frame the screen was built into.</summary>
        public RectTransform Frame { get; }

        /// <summary>The planned elements, in drawing order.</summary>
        public List<EtsUiElement> Elements { get; }

        internal EtsGameScreen(string uiPath, RectTransform frame, List<EtsUiElement> plan)
        {
            UiPath = uiPath;
            Frame = frame;
            Elements = plan;
        }

        internal void Add(EtsUiElement e, string value)
        {
            var entry = new Entry { Element = e, Value = value };
            entry.Rect = EtsGameUi.BuildElement(Frame, e, value, () => entry.Click?.Invoke());

            if (!_byKey.ContainsKey(e.Key))
            {
                _byKey[e.Key] = entry;
            }
        }

        public bool Has(string key) => key != null && _byKey.ContainsKey(key);

        /// <summary>The element's rectangle, or null.</summary>
        public RectTransform this[string key] => key != null && _byKey.TryGetValue(key, out Entry e) ? e.Rect : null;

        public EtsUiElement ElementOf(string key) => key != null && _byKey.TryGetValue(key, out Entry e) ? e.Element : null;

        public string ValueOf(string key) => key != null && _byKey.TryGetValue(key, out Entry e) ? e.Value : null;

        /// <summary>What a click on the element does (a button's look, or any element built with one).</summary>
        public void OnClick(string key, Action click)
        {
            if (key != null && _byKey.TryGetValue(key, out Entry e)) e.Click = click;
        }

        /// <summary>The element built again with a new value, in place (same sibling slot); nothing when unchanged.</summary>
        public void Set(string key, string value)
        {
            if (key == null || !_byKey.TryGetValue(key, out Entry e) || e.Value == value || e.Rect == null)
            {
                return;
            }

            int slot = e.Rect.GetSiblingIndex();
            bool shown = e.Rect.gameObject.activeSelf;
            UnityEngine.Object.Destroy(e.Rect.gameObject);
            e.Value = value;
            e.Rect = EtsGameUi.BuildElement(Frame, e.Element, value, () => e.Click?.Invoke());
            e.Rect.SetSiblingIndex(slot);
            e.Rect.gameObject.SetActive(shown);
            EtsGameUi.SetEnabled(e.Rect, e.Enabled);
            EtsGameUi.SetChosen(e.Rect, e.Chosen);
        }

        /// <summary>
        /// Shows or hides an element and everything under it in the layout (my_parent), since the built rectangles are
        /// all siblings in the frame: a ui::group such as quick_job.sii's job.empty.screen draws nothing itself.
        /// </summary>
        public void SetShown(string key, bool shown)
        {
            if (key == null || !_byKey.TryGetValue(key, out Entry target))
            {
                return;
            }

            string name = target.Element.Name;

            foreach (Entry e in _byKey.Values)
            {
                if (e.Rect != null && (e == target || IsUnder(e.Element, name)))
                {
                    e.Rect.gameObject.SetActive(shown);
                }
            }
        }

        private bool IsUnder(EtsUiElement e, string ancestorName)
        {
            if (string.IsNullOrEmpty(ancestorName))
            {
                return false;
            }

            string parent = e.Parent;

            for (int guard = 0; guard < 32 && !string.IsNullOrEmpty(parent); guard++)
            {
                if (parent == ancestorName)
                {
                    return true;
                }

                EtsUiElement up = null;

                foreach (Entry candidate in _byKey.Values)
                {
                    if (candidate.Element.Name == parent)
                    {
                        up = candidate.Element;
                        break;
                    }
                }

                parent = up != null ? up.Parent : null;
            }

            return false;
        }

        public void SetEnabled(string key, bool enabled)
        {
            if (key != null && _byKey.TryGetValue(key, out Entry e))
            {
                e.Enabled = enabled;
                EtsGameUi.SetEnabled(e.Rect, enabled);
            }
        }

        public void SetChosen(string key, bool chosen)
        {
            if (key != null && _byKey.TryGetValue(key, out Entry e))
            {
                e.Chosen = chosen;
                EtsGameUi.SetChosen(e.Rect, chosen);
            }
        }
    }
}
