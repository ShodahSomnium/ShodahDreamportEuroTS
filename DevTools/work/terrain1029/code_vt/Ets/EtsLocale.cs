using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The game's own words and interface colours, read from the player's install (owner D; [gameui §0.1,
    /// §2.1, §2.2, §4], PLAN.md C1, §7.D). Unity-free, so the harness runs every line of it.
    ///
    /// On 1.61 the locale is plain text. /locale/en_gb/local.sii is a 608-byte SiiNunit file (it starts
    /// EF BB BF 53 69 69 4E) holding one unit, localization_db : .localization, whose key[] and val[] arrays
    /// run on through its @includes: /locale/common.sui (which itself includes /locale/ui_colors.sui),
    /// localization.sui, photoalbum.sui, loading_screens.sui, academy.sui, tutorial_hints.sui, paintjobs.sui
    /// and accessories.sui. local.override.sii follows with the same unit, empty on the demo. The README's
    /// "3nK-obfuscated" was true of 1.48 only. Probe 1881 reads 12,514 keys this way (probe 1645 counted the
    /// same) in 33-37 ms in mono (the whole 1.2 MB of text), of them the 154 colours of /locale/ui_colors.sui (153
    /// clr_* and solid_bg_main). That is too long for one frame at 90 Hz, so Phase 2 loads it while the HQ void or
    /// a loading screen is up (EtsFlow), not in the middle of play.
    ///
    /// Colours are stored AABBGGRR, not AARRGGBB: clr_blue FFFFB204 is blue, clr_red FF4C4CFC red and
    /// clr_loading_bar FF03B0FA the orange of the loading bar only when read that way (the user's screenshot
    /// shows "an orange progress line"). The /def/map_data.sii colours are the same order (navigation_color
    /// FF0C0CCF is the GPS route's red). TryColour hands back ARGB, the order EtsUiPainter and Unity's
    /// Color32 conversions take.
    ///
    /// Nothing loads it while EtsFeatures.NewFlow is false: today's front end keeps its own wording
    /// (EtsGameUi.Words) until Phase 2 moves the screens over, so the old screens stay exactly as they were.
    /// Load swaps whole tables, so a reader on another thread sees the old table or the new one, never half.
    /// </summary>
    public static class EtsLocale
    {
        private sealed class Table
        {
            public readonly Dictionary<string, string> Words = new Dictionary<string, string>(StringComparer.Ordinal);
            public string Language = "";
            public int Redefined;
            public int Colours;
            public double Milliseconds;
        }

        private static Table _table = new Table();

        /// <summary>Deepest @@key@@ nesting Resolve follows; probe 1881 measured 2 on 1.61 (long_dist_desc_distance names
        /// clr_skill_desc_highlight, which is a colour).</summary>
        private const int MaxDepth = 8;

        private static readonly string[] DayKeys = { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" };

        public static bool Loaded { get; private set; }

        /// <summary>The language loaded ("en_gb"), or "".</summary>
        public static string Language => _table.Language;

        /// <summary>Keys loaded; 12,514 for en_gb on 1.61.</summary>
        public static int Count => _table.Words.Count;

        /// <summary>Of Count, the colours of /locale/ui_colors.sui (keys clr_* and solid_*); 154 on 1.61.</summary>
        public static int ColourCount => _table.Colours;

        /// <summary>Keys a later file set again (the later value wins, as the game's own override file intends).</summary>
        public static int Redefined => _table.Redefined;

        /// <summary>How long the last Load took, for the log.</summary>
        public static double LoadMilliseconds => _table.Milliseconds;

        /// <summary>
        /// Reads /locale/&lt;language&gt;/local.sii with its includes, then local.override.sii. False (and the
        /// previous table kept) when the first file is missing or holds no key. Not cached in the file
        /// system's definition cache: the table is kept here, once.
        /// </summary>
        public static bool Load(EtsFileSystem files, string language = "en_gb")
        {
            if (files == null || string.IsNullOrEmpty(language))
            {
                return false;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var table = new Table { Language = language };
            string[] paths = { "/locale/" + language + "/local.sii", "/locale/" + language + "/local.override.sii" };

            for (int f = 0; f < paths.Length; f++)
            {
                string text = files.ReadText(paths[f]);

                if (text == null)
                {
                    if (f == 0) return false;
                    continue;
                }

                EtsSiiFile parsed = EtsSii.Parse(text, paths[f], files.ReadText);

                foreach (EtsSiiUnit unit in parsed.Units)
                {
                    EtsSiiValue keys = unit.Get("key"), values = unit.Get("val");

                    if (keys == null || values == null || !keys.IsArray || !values.IsArray)
                    {
                        continue;
                    }

                    int n = Math.Min(keys.Count, values.Count);

                    for (int i = 0; i < n; i++)
                    {
                        string key = EtsSiiValue.Unquote(keys[i]);
                        string value = EtsSiiValue.Unquote(values[i]) ?? "";

                        if (string.IsNullOrEmpty(key))
                        {
                            continue;
                        }

                        if (table.Words.ContainsKey(key))
                        {
                            table.Redefined++;
                        }

                        table.Words[key] = value;
                    }
                }
            }

            if (table.Words.Count == 0)
            {
                return false;
            }

            foreach (string key in table.Words.Keys)
            {
                if (key.StartsWith("clr_", StringComparison.Ordinal) || key.StartsWith("solid_", StringComparison.Ordinal)) table.Colours++;
            }

            table.Milliseconds = watch.Elapsed.TotalMilliseconds;
            _table = table;
            Loaded = true;
            return true;
        }

        /// <summary>Forgets the table (for the harness; statics outlive a world visit, PLAN §3.2).</summary>
        public static void Clear()
        {
            _table = new Table();
            Loaded = false;
        }

        /// <summary>The raw value of a key (nested keys not resolved). A key may be given with its @@.</summary>
        public static bool TryWord(string key, out string value)
        {
            value = null;

            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            return _table.Words.TryGetValue(Strip(key), out value);
        }

        /// <summary>
        /// The words for a key, every nested @@key@@ resolved. An unknown key comes back as the key itself,
        /// so a missing word shows on screen and in the log for what it is rather than as a blank.
        /// </summary>
        public static string Word(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "";
            }

            string bare = Strip(key);
            return _table.Words.TryGetValue(bare, out string value) ? Resolve(value, 1) : bare;
        }

        /// <summary>
        /// Text with every @@key@@ replaced by its words, recursively (a value may name a key, which may name
        /// a colour: "@@clr_sel@@" inside markup becomes "FF03B0FA"). Markup is left as it is. An unknown key
        /// is left as written, @@ included, so a caller (and the eye) can tell it was not found.
        /// </summary>
        public static string Resolve(string text)
        {
            return Resolve(text, 0);
        }

        private static string Resolve(string text, int depth)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("@@", StringComparison.Ordinal) < 0 || depth >= MaxDepth)
            {
                return text ?? "";
            }

            var built = new StringBuilder(text.Length + 16);
            int at = 0;

            while (at < text.Length)
            {
                int open = text.IndexOf("@@", at, StringComparison.Ordinal);

                if (open < 0)
                {
                    built.Append(text, at, text.Length - at);
                    break;
                }

                int close = text.IndexOf("@@", open + 2, StringComparison.Ordinal);

                if (close < 0)
                {
                    built.Append(text, at, text.Length - at);
                    break;
                }

                built.Append(text, at, open - at);
                string key = text.Substring(open + 2, close - open - 2);

                if (key.Length > 0 && IsKey(key) && _table.Words.TryGetValue(key, out string value))
                {
                    built.Append(Resolve(value, depth + 1));
                    at = close + 2;
                }
                else
                {
                    // Not a key (or not a known one): keep the first @@ and look again from the second, so
                    // "@@ @@clr_sel@@" still finds the colour.
                    built.Append("@@");
                    at = open + 2;
                }
            }

            return built.ToString();
        }

        /// <summary>
        /// A named colour: stored AABBGGRR in /locale/ui_colors.sui, returned AARRGGBB. The key may carry its
        /// @@. False when the key is unknown or its value is not 8 hex digits.
        /// </summary>
        public static bool TryColour(string key, out uint argb)
        {
            argb = 0;
            return TryWord(key, out string value) && TryParseColour(Resolve(value, 1), out argb);
        }

        /// <summary>
        /// A colour as the markup writes one - AABBGGRR hex ("FF03B0FA", "0xFF0C0CCF"), or a @@clr_*@@ key -
        /// as AARRGGBB. False when it is neither.
        /// </summary>
        public static bool TryParseColour(string text, out uint argb)
        {
            argb = 0;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string s = text.Trim();

            if (s.StartsWith("@@", StringComparison.Ordinal))
            {
                return TryColour(s, out argb);
            }

            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(2);
            }

            if (s.Length != 8 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint abgr))
            {
                return false;
            }

            argb = AbgrToArgb(abgr);
            return true;
        }

        /// <summary>AABBGGRR to AARRGGBB (red and blue swap places; alpha and green stay).</summary>
        public static uint AbgrToArgb(uint abgr)
        {
            return (abgr & 0xFF00FF00u) | ((abgr & 0xFFu) << 16) | ((abgr >> 16) & 0xFFu);
        }

        /// <summary>
        /// Money the way the game prints it: the prefix symbol (europ, "€"), the digits grouped with dg (","),
        /// the suffix symbol (euros, "" in en_gb). Money(2000) = "€2,000", the start balance of
        /// /def/initial_save/normal/game.sii as the user's screenshot shows it.
        /// </summary>
        public static string Money(long euros)
        {
            string prefix = Raw("europ", "€"), suffix = Raw("euros", "");
            string sign = euros < 0 ? "-" : "";
            return sign + prefix + Group(euros < 0 ? -euros : euros) + suffix;
        }

        /// <summary>A whole number with the locale's digit grouping (dg, "," in en_gb).</summary>
        public static string Group(long value)
        {
            string digits = (value < 0 ? -value : value).ToString(CultureInfo.InvariantCulture);
            string separator = Raw("dg", ",");
            var built = new StringBuilder(digits.Length + digits.Length / 3 + 1);

            if (value < 0)
            {
                built.Append('-');
            }

            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0)
                {
                    built.Append(separator);
                }

                built.Append(digits[i]);
            }

            return built.ToString();
        }

        /// <summary>
        /// "Monday 10:00": the day's full name from the locale (monday .. sunday) and the time of day, from
        /// minutes since Monday 00:00 (the session clock, PLAN §3.11; game_time 600 in
        /// /def/initial_save/normal/game.sii is Monday 10:00). 24-hour, as the user's loading-screen
        /// screenshot shows it; the game's 12-hour option (am/pm keys) is not reproduced. Whether the game
        /// writes a leading zero before a one-digit hour in 24-hour mode is not in the files: none is written.
        /// </summary>
        public static string DayTime(int gameMinutes)
        {
            int minutes = gameMinutes % (7 * 1440);
            if (minutes < 0) minutes += 7 * 1440;
            int day = minutes / 1440, hour = (minutes % 1440) / 60, minute = minutes % 60;
            return Word(DayKeys[day]) + " " + hour.ToString(CultureInfo.InvariantCulture) + ":" + minute.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string Raw(string key, string fallback)
        {
            return _table.Words.TryGetValue(key, out string value) ? value : fallback;
        }

        private static string Strip(string key)
        {
            string k = key.Trim();

            if (k.Length > 4 && k.StartsWith("@@", StringComparison.Ordinal) && k.EndsWith("@@", StringComparison.Ordinal))
            {
                k = k.Substring(2, k.Length - 4);
            }

            return k;
        }

        /// <summary>Locale keys are lower-case identifiers; anything else between two @@ is not one.</summary>
        private static bool IsKey(string key)
        {
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];

                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && c != '.' && c != '-')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
