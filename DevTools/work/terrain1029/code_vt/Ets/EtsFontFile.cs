using System;
using System.Collections.Generic;
using System.Globalization;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The game's font description, /font/*.font - a plain text file over a set of distance-field
    /// atlases.
    ///
    /// Read from the files, not assumed. The header names the atlases ("image:" lines - path,
    /// width, height, scale x, scale y, distance-field padding) and gives the face metrics in
    /// "reference pixels", the size the font is drawn at in a 1024x768 frame. Every glyph line
    /// after it is one character:
    ///
    ///     x0041,  118.4191,  246.4191,  28.1618,  27.1618,  -0.4676,  8.1277,  27.3747,  0
    ///     code    atlas x    atlas y    width     height    plane x   plane y  advance   image
    ///
    /// and every number on it is in the PIXELS OF THAT ATLAS. Multiplying by the atlas's scale
    /// gives reference pixels: the capital A is 27.16 atlas pixels tall, 27.16 x 0.501 = 13.6
    /// reference pixels, which is exactly the header's "typical height of M". Plane y is measured
    /// down from a pen that sits at the top of the tallest glyph; A's bottom lands at
    /// (8.13 + 27.16) x 0.501 = 17.69, which is exactly the header's "max pixels above the line",
    /// so the pen is that far above the baseline and the maths closes.
    ///
    /// "kern:" lines carry pair adjustments, in the same atlas pixels.
    /// </summary>
    public sealed class EtsFontFile
    {
        public sealed class Atlas
        {
            public string MaterialPath;
            public int Width;
            public int Height;
            public float ScaleX;
            public float ScaleY;
            public float Padding;
        }

        public sealed class Glyph
        {
            public int Code;
            public float AtlasX;
            public float AtlasY;
            public float Width;
            public float Height;
            public float PlaneX;
            public float PlaneY;
            public float Advance;
            public int Image;
        }

        public struct Kern
        {
            public int Left;
            public int Right;
            public float Amount;
        }

        public string Path;

        /// <summary>Lowest to highest pixel over any two glyphs, in reference pixels: the line height.</summary>
        public float VertSpan;

        public float LineSpacing;
        public float DefaultScale = 0.83f;

        /// <summary>Pen-to-baseline and baseline-to-descender, in reference pixels.</summary>
        public float MaxAbove;
        public float MaxBelow;
        public float TypicalHeight;

        public Atlas[] Atlases = new Atlas[0];
        public readonly Dictionary<int, Glyph> Glyphs = new Dictionary<int, Glyph>();
        public readonly List<Kern> Kerns = new List<Kern>();

        public static EtsFontFile Parse(string text, string path)
        {
            var font = new EtsFontFile { Path = path };
            var atlases = new List<Atlas>();

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();

                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] == '#')
                {
                    // The face metrics only appear as comments, so they are read from those.
                    font.MaxAbove = HeaderNumber(line, "Max pixels 'above the line':", font.MaxAbove);
                    font.MaxBelow = HeaderNumber(line, "Max pixels 'below the line':", font.MaxBelow);
                    font.TypicalHeight = HeaderNumber(line, "Typical height 'above the line':", font.TypicalHeight);
                    continue;
                }

                int hash = line.IndexOf('#');

                if (hash >= 0)
                {
                    line = line.Substring(0, hash).Trim();
                }

                if (line.StartsWith("vert_span:", StringComparison.Ordinal))
                {
                    font.VertSpan = Number(line.Substring(10), font.VertSpan);
                }
                else if (line.StartsWith("line_spacing:", StringComparison.Ordinal))
                {
                    font.LineSpacing = Number(line.Substring(13), 0f);
                }
                else if (line.StartsWith("default_scale:", StringComparison.Ordinal))
                {
                    font.DefaultScale = Number(line.Substring(14), 0.83f);
                }
                else if (line.StartsWith("image:", StringComparison.Ordinal))
                {
                    string[] f = line.Substring(6).Split(',');

                    if (f.Length >= 6)
                    {
                        atlases.Add(new Atlas
                        {
                            MaterialPath = f[0].Trim(),
                            Width = (int)Number(f[1], 0f),
                            Height = (int)Number(f[2], 0f),
                            ScaleX = Number(f[3], 1f),
                            ScaleY = Number(f[4], 1f),
                            Padding = Number(f[5], 8f)
                        });
                    }
                }
                else if (line.StartsWith("kern:", StringComparison.Ordinal))
                {
                    string[] f = line.Substring(5).Split(',');

                    if (f.Length >= 3 && TryCode(f[0], out int left) && TryCode(f[1], out int right))
                    {
                        font.Kerns.Add(new Kern { Left = left, Right = right, Amount = Number(f[2], 0f) });
                    }
                }
                else if (line[0] == 'x')
                {
                    string[] f = line.Split(',');

                    if (f.Length >= 9 && TryCode(f[0], out int code))
                    {
                        font.Glyphs[code] = new Glyph
                        {
                            Code = code,
                            AtlasX = Number(f[1], 0f),
                            AtlasY = Number(f[2], 0f),
                            Width = Number(f[3], 0f),
                            Height = Number(f[4], 0f),
                            PlaneX = Number(f[5], 0f),
                            PlaneY = Number(f[6], 0f),
                            Advance = Number(f[7], 0f),
                            Image = (int)Number(f[8], 0f)
                        };
                    }
                }
            }

            font.Atlases = atlases.ToArray();
            return font;
        }

        private static bool TryCode(string field, out int code)
        {
            code = 0;
            string s = field.Trim();

            if (s.Length < 2 || (s[0] != 'x' && s[0] != 'X'))
            {
                return false;
            }

            return int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
        }

        private static float Number(string field, float fallback)
        {
            return float.TryParse(field.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }

        private static float HeaderNumber(string comment, string label, float current)
        {
            int at = comment.IndexOf(label, StringComparison.Ordinal);

            if (at < 0)
            {
                return current;
            }

            string rest = comment.Substring(at + label.Length).Trim();
            int end = 0;

            while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.' || rest[end] == '-'))
            {
                end++;
            }

            return Number(rest.Substring(0, end), current);
        }
    }
}
