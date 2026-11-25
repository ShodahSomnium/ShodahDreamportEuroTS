using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Draws a sign template's area - its texts, images and frames - into an RGBA picture, the way the game composes a
    /// sign face at run time (2026-10-14, sign texts). Pure C#: it runs on the sign-face worker thread.
    ///
    /// Measured from the files (README "2026-10-14: sign texts"):
    /// - Coordinates are metres from the area's bottom-left; a bound board's own bottom-left sits on the area's
    ///   (cz_distance_v02: a 4.4 x 3 m board in a 4.2 x 2.25 m area, items at y 0.116..1.929 - only bottom-left
    ///   alignment keeps them inside).
    /// - font_size is the face's line box, the .font's vert_span: the cz distance board's names are font_size 0.6 in
    ///   rectangles exactly 0.6 m tall. Glyph numbers are atlas pixels, x the atlas scale = the header's reference
    ///   pixels (EtsFontFile). line_spacing and letter_spacing are metres added to the line box and the advance.
    /// - The sign fonts are multi-channel distance fields (ui.font.msdf, aux[0] = width, height, padding): the
    ///   median of r, g, b is the distance, 0.5 on the edge, +-0.5 over +-padding atlas pixels.
    /// - Images: /def/sign/atlas image_atlas, material_path -&gt; texture, images_coords (left, right, bottom, top) in
    ///   pixels; tinted by "color" unless nil. Frames: sign_frame_template nine-slice, border_width/height metres,
    ///   corner/horizontal/vertical/center pixel rectangles of the top-left quarter, mirrored for the others.
    /// - Markup seen in the demo's texts and overrides: &lt;br&gt; (1,192), &lt;sub [scale=]&gt; (773), &lt;font xscale= yscale=&gt;
    ///   (329), &lt;sup&gt; (70). sub/sup without a scale are drawn at 0.7 (not read from the game; an assumption).
    /// </summary>
    public sealed class EtsSignArt
    {
        public sealed class Face
        {
            public EtsFontFile File;
            public byte[][] Fields;
            public int[] Widths, Heights;
        }

        private readonly EtsFileSystem _files;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Face> _faces = new Dictionary<string, Face>(StringComparer.Ordinal);
        private readonly Dictionary<string, EtsBitmap> _images = new Dictionary<string, EtsBitmap>(StringComparer.Ordinal);

        public EtsSignArt(EtsFileSystem files)
        {
            _files = files;
        }

        public int FacesRead { get { lock (_lock) return _faces.Count; } }
        public int ImagesRead { get { lock (_lock) return _images.Count; } }

        /// <summary>A sign font with its distance-field atlases decoded (median of r, g, b; row 0 = the image's top).</summary>
        public Face Font(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            lock (_lock)
            {
                if (_faces.TryGetValue(path, out Face cached)) return cached;
            }

            Face face = null;
            string text = _files.ReadText(path);

            if (text != null)
            {
                EtsFontFile file = EtsFontFile.Parse(text, path);

                if (file.Atlases.Length > 0 && file.Glyphs.Count > 0 && file.VertSpan > 0f)
                {
                    face = new Face
                    {
                        File = file,
                        Fields = new byte[file.Atlases.Length][],
                        Widths = new int[file.Atlases.Length],
                        Heights = new int[file.Atlases.Length]
                    };

                    for (int i = 0; i < file.Atlases.Length; i++)
                    {
                        EtsBitmap image = Material(file.Atlases[i].MaterialPath);
                        if (image == null) continue;
                        var field = new byte[image.Width * image.Height];

                        for (int p = 0, o = 0; p < field.Length; p++, o += 4)
                        {
                            byte r = image.Rgba[o], g = image.Rgba[o + 1], b = image.Rgba[o + 2];
                            field[p] = r > g ? (g > b ? g : (r > b ? b : r)) : (r > b ? r : (g > b ? b : g));
                        }

                        face.Fields[i] = field;
                        face.Widths[i] = image.Width;
                        face.Heights[i] = image.Height;
                    }

                    if (face.Fields[0] == null) face = null;
                }
            }

            lock (_lock)
            {
                _faces[path] = face;
            }

            return face;
        }

        /// <summary>The picture a material draws (its texture_base, or "texture" for the ui effects), decoded; null when unreadable.</summary>
        public EtsBitmap Material(string matPath)
        {
            if (string.IsNullOrEmpty(matPath)) return null;

            lock (_lock)
            {
                if (_images.TryGetValue(matPath, out EtsBitmap cached)) return cached;
            }

            EtsBitmap image = null;
            string text = _files.ReadText(matPath);

            if (text != null)
            {
                EtsMaterialDef def = EtsMaterialDef.Parse(text, matPath);
                string tobj = def.BaseTexture;
                if (string.IsNullOrEmpty(tobj) && def.Textures.TryGetValue("texture", out string plain)) tobj = plain;

                if (!string.IsNullOrEmpty(tobj))
                {
                    if (!tobj.StartsWith("/", StringComparison.Ordinal))
                    {
                        int slash = matPath.LastIndexOf('/');
                        tobj = (slash >= 0 ? matPath.Substring(0, slash + 1) : "/") + tobj;
                    }

                    EtsDds dds = EtsTextureCache.ReadDds(_files, tobj);
                    image = dds != null ? EtsPixels.Decode(dds) : null;
                }
            }

            lock (_lock)
            {
                _images[matPath] = image;
            }

            return image;
        }
    }

    public sealed class EtsSignCanvas
    {
        public readonly int Width, Height;
        public readonly float Ppm, PpmX, PpmY;
        public readonly float AreaWidth, AreaHeight;

        /// <summary>Straight-alpha colour, linear in the stored sRGB values, row 0 = the area's top.</summary>
        public readonly float[] Pixels;

        public EtsSignCanvas(float areaWidth, float areaHeight, float ppm)
        {
            AreaWidth = areaWidth;
            AreaHeight = areaHeight;
            Ppm = ppm;
            // Multiples of 4 (block compression), so each axis gets its own scale (under 1 % apart).
            Width = Math.Max(4, ((int)Math.Ceiling(areaWidth * ppm) + 3) / 4 * 4);
            Height = Math.Max(4, ((int)Math.Ceiling(areaHeight * ppm) + 3) / 4 * 4);
            PpmX = Width / areaWidth;
            PpmY = Height / areaHeight;
            Pixels = new float[Width * Height * 4];
        }

        public void Over(int x, int y, float r, float g, float b, float a)
        {
            if (a <= 0f || x < 0 || y < 0 || x >= Width || y >= Height) return;
            if (a > 1f) a = 1f;
            int o = (y * Width + x) * 4;
            float da = Pixels[o + 3];
            float oa = a + da * (1f - a);
            if (oa <= 0f) return;
            float k = da * (1f - a);
            Pixels[o] = (r * a + Pixels[o] * k) / oa;
            Pixels[o + 1] = (g * a + Pixels[o + 1] * k) / oa;
            Pixels[o + 2] = (b * a + Pixels[o + 2] * k) / oa;
            Pixels[o + 3] = oa;
        }

        /// <summary>RGBA32 rows, top row first (the order the world uploads every game texture in: row 0 at v = 0).</summary>
        public byte[] ToRgba32(out bool any)
        {
            var bytes = new byte[Width * Height * 4];
            any = false;

            for (int i = 0; i < bytes.Length; i++)
            {
                float v = Pixels[i];
                bytes[i] = (byte)(v <= 0f ? 0 : v >= 1f ? 255 : (int)(v * 255f + 0.5f));
            }

            for (int i = 3; i < bytes.Length; i += 4)
            {
                if (bytes[i] != 0) { any = true; break; }
            }

            return bytes;
        }
    }

    public static class EtsSignRaster
    {
        /// <summary>Default sub/sup scale when the tag names none (assumed, see the class notes).</summary>
        public const float SubScale = 0.7f;

        public static int GlyphsDrawn, GlyphsMissing, ImagesDrawn, FramesDrawn, ItemsSkipped;

        /// <summary>A few characters no glyph was found for (diagnostics, the log line).</summary>
        public static string MissingSample = string.Empty;

        /// <summary>Draws the items (already in drawing order) into a fresh canvas of the area's size.</summary>
        public static EtsSignCanvas Area(EtsSignLibrary library, EtsSignArt art, float width, float height, float ppm, List<EtsSignItem> items)
        {
            var canvas = new EtsSignCanvas(width, height, ppm);

            foreach (EtsSignItem item in items)
            {
                if (item.Opacity <= 0.001f || item.R <= item.L || item.T <= item.B && item.Kind != EtsSignItemKind.Text && item.Kind != EtsSignItemKind.Mileage)
                {
                    ItemsSkipped++;
                    continue;
                }

                switch (item.Kind)
                {
                    case EtsSignItemKind.Image: DrawImage(library, art, canvas, item); break;
                    case EtsSignItemKind.Frame: DrawFrame(library, art, canvas, item); break;
                    default: DrawText(art, canvas, item, library); break;
                }
            }

            return canvas;
        }

        private static void Rgb(uint c, out float r, out float g, out float b)
        {
            r = (c & 0xFF) / 255f;
            g = ((c >> 8) & 0xFF) / 255f;
            b = ((c >> 16) & 0xFF) / 255f;
        }

        private static void Sample(EtsBitmap image, float x, float y, out float r, out float g, out float b, out float a)
        {
            if (x < 0f) x = 0f;
            if (y < 0f) y = 0f;
            float mx = image.Width - 1, my = image.Height - 1;
            if (x > mx) x = mx;
            if (y > my) y = my;
            int x0 = (int)x, y0 = (int)y;
            int x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
            float fx = x - x0, fy = y - y0;
            byte[] p = image.Rgba;
            int o00 = (y0 * image.Width + x0) * 4, o10 = (y0 * image.Width + x1) * 4, o01 = (y1 * image.Width + x0) * 4, o11 = (y1 * image.Width + x1) * 4;
            float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
            float a00 = p[o00 + 3] * w00, a10 = p[o10 + 3] * w10, a01 = p[o01 + 3] * w01, a11 = p[o11 + 3] * w11;
            a = (a00 + a10 + a01 + a11) / 255f;

            if (a <= 0.0001f)
            {
                r = g = b = 0f;
                return;
            }

            // Alpha-weighted so a transparent texel's colour never bleeds into the edge.
            float sum = a00 + a10 + a01 + a11;
            r = (p[o00] * a00 + p[o10] * a10 + p[o01] * a01 + p[o11] * a11) / (sum * 255f);
            g = (p[o00 + 1] * a00 + p[o10 + 1] * a10 + p[o01 + 1] * a01 + p[o11 + 1] * a11) / (sum * 255f);
            b = (p[o00 + 2] * a00 + p[o10 + 2] * a10 + p[o01 + 2] * a01 + p[o11 + 2] * a11) / (sum * 255f);
        }

        /// <summary>
        /// Blits the source rectangle (pixels, top &lt; bottom) of an image into the area rectangle (metres from the
        /// bottom-left), with a tint and opacity, mirrored as asked.
        /// </summary>
        private static void Blit(EtsSignCanvas canvas, EtsBitmap image, float sl, float sr, float st, float sb,
            float l, float r, float bottom, float top, float tr, float tg, float tb, float opacity, bool hflip, bool vflip)
        {
            if (r <= l || top <= bottom) return;
            int px0 = (int)Math.Floor(l * canvas.PpmX), px1 = (int)Math.Ceiling(r * canvas.PpmX);
            int py0 = (int)Math.Floor((canvas.AreaHeight - top) * canvas.PpmY), py1 = (int)Math.Ceiling((canvas.AreaHeight - bottom) * canvas.PpmY);

            for (int py = Math.Max(0, py0); py < Math.Min(canvas.Height, py1); py++)
            {
                float y = canvas.AreaHeight - (py + 0.5f) / canvas.PpmY;
                float fy = (top - y) / (top - bottom);
                if (fy < 0f || fy > 1f) continue;
                if (vflip) fy = 1f - fy;
                float sy = st + fy * (sb - st) - 0.5f;

                for (int px = Math.Max(0, px0); px < Math.Min(canvas.Width, px1); px++)
                {
                    float x = (px + 0.5f) / canvas.PpmX;
                    float fx = (x - l) / (r - l);
                    if (fx < 0f || fx > 1f) continue;
                    if (hflip) fx = 1f - fx;
                    float sx = sl + fx * (sr - sl) - 0.5f;
                    Sample(image, sx, sy, out float cr, out float cg, out float cb, out float ca);
                    canvas.Over(px, py, cr * tr, cg * tg, cb * tb, ca * opacity);
                }
            }
        }

        private static void DrawImage(EtsSignLibrary library, EtsSignArt art, EtsSignCanvas canvas, EtsSignItem item)
        {
            EtsSignAtlasDef atlas = library.Atlas(item.AtlasPath);
            if (atlas == null || atlas.Coords.Count == 0) { ItemsSkipped++; return; }
            EtsBitmap image = art.Material(atlas.MaterialPath);
            if (image == null) { ItemsSkipped++; return; }
            float[] c = atlas.Coords[Math.Max(0, Math.Min(atlas.Coords.Count - 1, item.ImageIdx))];
            float tr = 1f, tg = 1f, tb = 1f;
            if (!item.ColorNil) Rgb(item.Color, out tr, out tg, out tb);
            // images_coords: left, right, bottom, top (pixels from the top: bottom > top).
            Blit(canvas, image, c[0], c[1], Math.Min(c[2], c[3]), Math.Max(c[2], c[3]), item.L, item.R, item.B, item.T, tr, tg, tb, item.Opacity, item.HFlip, item.VFlip);
            ImagesDrawn++;
        }

        private static void DrawFrame(EtsSignLibrary library, EtsSignArt art, EtsSignCanvas canvas, EtsSignItem item)
        {
            EtsSignFrameDef frame = library.Frame(item.FramePath);
            if (frame == null) { ItemsSkipped++; return; }
            EtsBitmap image = art.Material(frame.Material);
            if (image == null) { ItemsSkipped++; return; }
            float tr = 1f, tg = 1f, tb = 1f;
            if (!item.ColorNil) Rgb(item.Color, out tr, out tg, out tb);
            float bw = Math.Min(frame.BorderWidth, (item.R - item.L) * 0.5f), bh = Math.Min(frame.BorderHeight, (item.T - item.B) * 0.5f);
            float l = item.L, r = item.R, b = item.B, t = item.T, op = item.Opacity;
            float[] co = frame.Corner, ho = frame.Horizontal, ve = frame.Vertical, ce = frame.Center;
            float cl = co[0], cr = co[1], ct = Math.Min(co[2], co[3]), cb = Math.Max(co[2], co[3]);
            float hl = ho[0], hr = ho[1], ht = Math.Min(ho[2], ho[3]), hb = Math.Max(ho[2], ho[3]);
            float vl = ve[0], vr = ve[1], vt = Math.Min(ve[2], ve[3]), vb = Math.Max(ve[2], ve[3]);
            float el = ce[0], er = ce[1], et = Math.Min(ce[2], ce[3]), eb = Math.Max(ce[2], ce[3]);

            // Centre, then the four edges, then the four corners (the top-left tiles, mirrored).
            Blit(canvas, image, el, er, et, eb, l + bw, r - bw, b + bh, t - bh, tr, tg, tb, op, false, false);
            Blit(canvas, image, hl, hr, ht, hb, l + bw, r - bw, t - bh, t, tr, tg, tb, op, false, false);
            Blit(canvas, image, hl, hr, ht, hb, l + bw, r - bw, b, b + bh, tr, tg, tb, op, false, true);
            Blit(canvas, image, vl, vr, vt, vb, l, l + bw, b + bh, t - bh, tr, tg, tb, op, false, false);
            Blit(canvas, image, vl, vr, vt, vb, r - bw, r, b + bh, t - bh, tr, tg, tb, op, true, false);
            Blit(canvas, image, cl, cr, ct, cb, l, l + bw, t - bh, t, tr, tg, tb, op, false, false);
            Blit(canvas, image, cl, cr, ct, cb, r - bw, r, t - bh, t, tr, tg, tb, op, true, false);
            Blit(canvas, image, cl, cr, ct, cb, l, l + bw, b, b + bh, tr, tg, tb, op, false, true);
            Blit(canvas, image, cl, cr, ct, cb, r - bw, r, b, b + bh, tr, tg, tb, op, true, true);
            FramesDrawn++;
        }

        // ------------------------------------------------------------------ text

        public struct Run
        {
            public int Code;
            public float ScaleX, ScaleY;
            public int Shift; // 0 baseline, 1 sup (top aligned)
            public bool Break;
        }

        /// <summary>The text with its markup read: one entry per character or line break.</summary>
        public static List<Run> Markup(string text)
        {
            var runs = new List<Run>();
            var scales = new Stack<float[]>();
            float sx = 1f, sy = 1f;
            int shift = 0;
            int i = 0;

            while (i < text.Length)
            {
                char ch = text[i];

                if (ch == '<')
                {
                    int end = text.IndexOf('>', i + 1);

                    if (end > i)
                    {
                        string tag = text.Substring(i + 1, end - i - 1).Trim().ToLowerInvariant();
                        i = end + 1;

                        if (tag == "br" || tag == "br/")
                        {
                            runs.Add(new Run { Break = true });
                        }
                        else if (tag.StartsWith("/", StringComparison.Ordinal))
                        {
                            if (scales.Count > 0)
                            {
                                float[] s = scales.Pop();
                                sx = s[0];
                                sy = s[1];
                                shift = (int)s[2];
                            }
                        }
                        else if (tag.StartsWith("sub", StringComparison.Ordinal) || tag.StartsWith("sup", StringComparison.Ordinal))
                        {
                            scales.Push(new[] { sx, sy, shift });
                            float k = Attribute(tag, "scale", SubScale);
                            sx *= k;
                            sy *= k;
                            shift = tag.StartsWith("sup", StringComparison.Ordinal) ? 1 : 0;
                        }
                        else if (tag.StartsWith("font", StringComparison.Ordinal))
                        {
                            scales.Push(new[] { sx, sy, shift });
                            sx *= Attribute(tag, "xscale", Attribute(tag, "x_scale", 1f));
                            sy *= Attribute(tag, "yscale", Attribute(tag, "y_scale", 1f));
                        }

                        continue;
                    }
                }

                int code = ch;

                if (char.IsHighSurrogate(ch) && i + 1 < text.Length)
                {
                    code = char.ConvertToUtf32(ch, text[i + 1]);
                    i++;
                }

                if (code == '\n')
                {
                    runs.Add(new Run { Break = true });
                }
                else if (code >= 32)
                {
                    runs.Add(new Run { Code = code, ScaleX = sx, ScaleY = sy, Shift = shift });
                }

                i++;
            }

            return runs;
        }

        private static float Attribute(string tag, string name, float fallback)
        {
            int at = tag.IndexOf(name + "=", StringComparison.Ordinal);
            if (at < 0) return fallback;
            int start = at + name.Length + 1, end = start;
            while (end < tag.Length && tag[end] != ' ') end++;
            string v = tag.Substring(start, end - start).Trim('"', '\'');
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f > 0f ? f : fallback;
        }

        private static EtsFontFile.Glyph FindGlyph(EtsFontFile file, int code)
        {
            if (file.Glyphs.TryGetValue(code, out EtsFontFile.Glyph g)) return g;
            string s = char.ConvertFromUtf32(code);
            string up = s.ToUpperInvariant(), low = s.ToLowerInvariant();
            if (up != s && up.Length == 1 && file.Glyphs.TryGetValue(up[0], out g)) return g;
            if (low != s && low.Length == 1 && file.Glyphs.TryGetValue(low[0], out g)) return g;
            return null;
        }

        private static float Kern(EtsFontFile file, int left, int right)
        {
            foreach (EtsFontFile.Kern k in file.Kerns)
            {
                if (k.Left == left && k.Right == right) return k.Amount;
            }

            return 0f;
        }

        /// <summary>The string a text or mileage item shows, or null when nothing is drawn.</summary>
        public static string Content(EtsSignItem item, EtsSignLibrary library)
        {
            string text = item.Text ?? string.Empty;

            if (item.Kind == EtsSignItemKind.Mileage && !item.TextSet)
            {
                // The editor stores the computed name and distance as overrides; without one only a target's name can
                // be shown (a distance needs the game's route search).
                EtsMileageTarget target = item.Display == "name" ? library.Mileage(item.Target) : null;
                if (target == null || string.IsNullOrEmpty(target.DefaultName)) return null;
                text = target.DefaultName;
            }

            // "@@fp_cz_natural95@@": a locale key (the fuel prices on the station boards are /locale words).
            if (text.IndexOf("@@", StringComparison.Ordinal) >= 0 && EtsLocale.Loaded) text = EtsLocale.Resolve(text);
            if (text.IndexOf("@@", StringComparison.Ordinal) >= 0) return null;
            if (item.Kind == EtsSignItemKind.Mileage && item.Uppercase) text = text.ToUpperInvariant();
            return text.Length == 0 ? null : text;
        }

        private static void DrawText(EtsSignArt art, EtsSignCanvas canvas, EtsSignItem item, EtsSignLibrary library)
        {
            string text = Content(item, library);
            if (text == null) { ItemsSkipped++; return; }
            EtsSignArt.Face face = art.Font(item.FontFace);
            if (face == null) { ItemsSkipped++; return; }
            EtsFontFile file = face.File;
            float size = item.FontSize > 0f ? item.FontSize : 0.5f;
            float metresPerRef = size / file.VertSpan;
            List<Run> runs = Markup(text);

            // Lines.
            var lines = new List<List<Run>> { new List<Run>() };
            foreach (Run run in runs)
            {
                if (run.Break) lines.Add(new List<Run>());
                else lines[lines.Count - 1].Add(run);
            }

            float advanceLine = size + item.LineSpacing;
            float block = size + (lines.Count - 1) * advanceLine;
            float top = item.VAlign == 0 ? item.T : item.VAlign == 2 ? item.B + block : (item.T + item.B) * 0.5f + block * 0.5f;
            float r = 1f, g = 1f, b = 1f;
            if (!item.ColorNil) Rgb(item.Color, out r, out g, out b);

            for (int li = 0; li < lines.Count; li++)
            {
                List<Run> line = lines[li];
                if (line.Count == 0) continue;
                float lineTop = top - li * advanceLine;
                float baseline = lineTop - file.MaxAbove * metresPerRef;

                // Width first, for the alignment.
                float width = 0f;
                for (int i = 0; i < line.Count; i++)
                {
                    EtsFontFile.Glyph glyph = FindGlyph(file, line[i].Code);
                    float scale = file.Atlases[glyph != null && glyph.Image < file.Atlases.Length ? glyph.Image : 0].ScaleX;
                    float adv = glyph != null ? glyph.Advance * scale * metresPerRef * line[i].ScaleX : size * 0.3f;
                    if (i > 0) width += Kern(file, line[i - 1].Code, line[i].Code) * scale * metresPerRef * line[i].ScaleX;
                    width += adv + (i < line.Count - 1 ? item.LetterSpacing : 0f);
                }

                float x = item.HAlign == 0 ? item.L : item.HAlign == 2 ? item.R - width : (item.L + item.R - width) * 0.5f;

                for (int i = 0; i < line.Count; i++)
                {
                    Run run = line[i];
                    EtsFontFile.Glyph glyph = FindGlyph(file, run.Code);

                    if (glyph == null)
                    {
                        if (run.Code != ' ') { GlyphsMissing++; if (MissingSample.Length < 80) MissingSample += (char)run.Code + "(" + System.IO.Path.GetFileNameWithoutExtension(item.FontFace) + ") "; }
                        x += size * 0.3f + item.LetterSpacing;
                        continue;
                    }

                    int atlasIndex = glyph.Image < file.Atlases.Length ? glyph.Image : 0;
                    EtsFontFile.Atlas atlas = file.Atlases[atlasIndex];
                    float mpaX = atlas.ScaleX * metresPerRef * run.ScaleX;
                    float mpaY = atlas.ScaleY * metresPerRef * run.ScaleY;
                    if (i > 0) x += Kern(file, line[i - 1].Code, run.Code) * atlas.ScaleX * metresPerRef * run.ScaleX;

                    // A scaled run sits on the baseline (sub) or hangs from the line's top (sup).
                    float penTop = run.Shift == 1 ? lineTop : baseline + file.MaxAbove * metresPerRef * run.ScaleY;

                    if (glyph.Width > 0f && glyph.Height > 0f && face.Fields[atlasIndex] != null)
                    {
                        DrawGlyph(canvas, face, atlasIndex, glyph, x, penTop, mpaX, mpaY, atlas.Padding, r, g, b, item.Opacity);
                        GlyphsDrawn++;
                    }

                    x += glyph.Advance * mpaX + item.LetterSpacing;
                }
            }
        }

        private static void DrawGlyph(EtsSignCanvas canvas, EtsSignArt.Face face, int atlasIndex, EtsFontFile.Glyph glyph,
            float penX, float penTop, float mpaX, float mpaY, float pad, float r, float g, float b, float opacity)
        {
            byte[] field = face.Fields[atlasIndex];
            int fw = face.Widths[atlasIndex], fh = face.Heights[atlasIndex];

            // The ink rectangle grown by the field's padding, in metres (y up).
            float left = penX + (glyph.PlaneX - pad) * mpaX;
            float right = left + (glyph.Width + 2f * pad) * mpaX;
            float topY = penTop - (glyph.PlaneY - pad) * mpaY;
            float bottomY = topY - (glyph.Height + 2f * pad) * mpaY;
            float ppmX = canvas.PpmX, ppmY = canvas.PpmY;
            float pxPerAtlas = Math.Min(mpaX * ppmX, mpaY * ppmY);
            float range = 2f * pad * pxPerAtlas;

            int px0 = Math.Max(0, (int)Math.Floor(left * ppmX)), px1 = Math.Min(canvas.Width, (int)Math.Ceiling(right * ppmX));
            int py0 = Math.Max(0, (int)Math.Floor((canvas.AreaHeight - topY) * ppmY)), py1 = Math.Min(canvas.Height, (int)Math.Ceiling((canvas.AreaHeight - bottomY) * ppmY));

            for (int py = py0; py < py1; py++)
            {
                float y = canvas.AreaHeight - (py + 0.5f) / ppmY;
                float ay = glyph.AtlasY - pad + (topY - y) / mpaY - 0.5f;

                for (int px = px0; px < px1; px++)
                {
                    float xm = (px + 0.5f) / ppmX;
                    float ax = glyph.AtlasX - pad + (xm - left) / mpaX - 0.5f;
                    float v = Field(field, fw, fh, ax, ay);
                    float coverage = (v - 0.5f) * range + 0.5f;
                    if (coverage <= 0f) continue;
                    if (coverage > 1f) coverage = 1f;
                    canvas.Over(px, py, r, g, b, coverage * opacity);
                }
            }
        }

        private static float Field(byte[] field, int w, int h, float x, float y)
        {
            if (x < 0f || y < 0f || x > w - 1 || y > h - 1) return 0f;
            int x0 = (int)x, y0 = (int)y;
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            float fx = x - x0, fy = y - y0;
            float a = field[y0 * w + x0] + (field[y0 * w + x1] - field[y0 * w + x0]) * fx;
            float c = field[y1 * w + x0] + (field[y1 * w + x1] - field[y1 * w + x0]) * fx;
            return (a + (c - a) * fy) / 255f;
        }
    }

    /// <summary>One composed face of a templated sign: where it sits on the model and its picture.</summary>
    public sealed class EtsSignFacePlan
    {
        public string Area;

        /// <summary>The area locator's position and rotation (x, y, z, w) in the model's own (game) frame.</summary>
        public float[] Position, Rotation;

        public float Width, Height;
        public int PixelsWide, PixelsHigh;
        public byte[] Rgba;
        public int Items;
    }

    /// <summary>
    /// Plans a templated map sign's faces (pure; the sign-face worker runs it): the template's items and bound boards
    /// grouped by area, the map item's board swaps and overrides applied to copies, each area drawn at the area
    /// locator's size.
    /// </summary>
    public static class EtsSignPlanner
    {
        /// <summary>Pixels per metre at most, and the longest side of a face picture.</summary>
        public static float MaxPpm = 128f;
        public static int MaxSide = 512;

        public static int Planned, NoTemplate, NoLocator, Empty;

        /// <summary>The hookup name of every locator of a model (its .pmg string pool), null where none.</summary>
        public static string[] Hookups(byte[] pmg, EtsModelData model)
        {
            var names = new string[model.Locators.Length];
            if (pmg == null || pmg.Length < 96) return names;
            int poolOffset = BitConverter.ToInt32(pmg, 88), poolSize = BitConverter.ToInt32(pmg, 92);
            if (poolOffset <= 0 || poolSize <= 0 || poolOffset + poolSize > pmg.Length) return names;

            for (int i = 0; i < names.Length; i++)
            {
                int at = model.Locators[i].Hookup;
                if (at < 0 || at >= poolSize) continue;
                int start = poolOffset + at, end = start;
                while (end < poolOffset + poolSize && pmg[end] != 0) end++;
                names[i] = end > start ? Encoding.ASCII.GetString(pmg, start, end - start) : null;
            }

            return names;
        }

        private sealed class AreaWork
        {
            public string Name;
            public EtsSignBoardDef Board;
            public readonly List<EtsSignItem> BoardItems = new List<EtsSignItem>();
            public readonly List<EtsSignItem> Items = new List<EtsSignItem>();
        }

        public static List<EtsSignFacePlan> Plan(EtsSignLibrary library, EtsSignArt art, EtsModelData model, string[] hookups,
            string template, EtsMapSignArea[] areaRecords, EtsMapSignOverride[] overrides, out string note)
        {
            var faces = new List<EtsSignFacePlan>();
            note = null;
            EtsSignTemplateDef def = library.Template(template);

            if (def == null)
            {
                NoTemplate++;
                note = "template " + template + " not in the archives";
                return faces;
            }

            var areas = new Dictionary<string, AreaWork>(StringComparer.Ordinal);

            AreaWork Get(string name)
            {
                if (!areas.TryGetValue(name, out AreaWork w)) areas[name] = w = new AreaWork { Name = name };
                return w;
            }

            foreach (KeyValuePair<string, string> binding in def.Bindings)
            {
                Get(binding.Key).Board = library.Board(binding.Value);
            }

            if (areaRecords != null)
            {
                foreach (EtsMapSignArea record in areaRecords)
                {
                    if ((record.Flags & 2) == 0 || record.Board == 0) continue;
                    EtsSignBoardDef swapped = library.Board("sign_board." + EtsToken.Decode(record.Board));
                    if (swapped != null) Get(EtsToken.Decode(record.Area)).Board = swapped;
                }
            }

            foreach (AreaWork w in areas.Values)
            {
                if (w.Board == null) continue;
                foreach (EtsSignItem item in w.Board.Items) w.BoardItems.Add(item.Clone());
            }

            foreach (EtsSignItem item in def.Items)
            {
                Get(item.Area).Items.Add(item.Clone());
            }

            if (overrides != null)
            {
                foreach (EtsMapSignOverride o in overrides)
                {
                    string areaName = o.Area != 0 ? EtsToken.Decode(o.Area) : string.Empty;
                    EtsSignItem target = null;

                    if (areas.TryGetValue(areaName, out AreaWork w))
                    {
                        target = Find(w.Items, o.Id) ?? Find(w.BoardItems, o.Id);
                    }

                    if (target == null)
                    {
                        foreach (AreaWork other in areas.Values)
                        {
                            target = Find(other.Items, o.Id) ?? Find(other.BoardItems, o.Id);
                            if (target != null) break;
                        }
                    }

                    if (target == null || o.Attributes == null) continue;
                    foreach (EtsMapSignAttribute a in o.Attributes) target.Apply(a);
                }
            }

            foreach (AreaWork w in areas.Values)
            {
                if (w.Items.Count == 0 && w.BoardItems.Count == 0) continue;
                int locator = -1;

                for (int i = 0; i < model.Locators.Length; i++)
                {
                    if (model.Locators[i].Name == w.Name) { locator = i; break; }
                }

                if (locator < 0)
                {
                    NoLocator++;
                    note = (note == null ? "" : note + "; ") + "no locator " + w.Name;
                    continue;
                }

                float[] size = library.Area(hookups != null && locator < hookups.Length ? hookups[locator] : null);
                float width = size != null ? size[0] : 0f, height = size != null ? size[1] : 0f;

                if (width <= 0f || height <= 0f)
                {
                    if (w.Board != null) { width = w.Board.Width; height = w.Board.Height; }
                    foreach (EtsSignItem item in w.Items) { width = Math.Max(width, item.R); height = Math.Max(height, item.T); }
                    foreach (EtsSignItem item in w.BoardItems) { width = Math.Max(width, item.R); height = Math.Max(height, item.T); }
                }

                if (width <= 0.01f || height <= 0.01f) continue;
                float ppm = Math.Min(MaxPpm, MaxSide / Math.Max(width, height));
                w.BoardItems.Sort((a, b) => a.Z.CompareTo(b.Z));
                w.Items.Sort((a, b) => a.Z.CompareTo(b.Z));
                var all = new List<EtsSignItem>(w.BoardItems);
                all.AddRange(w.Items);
                EtsSignCanvas canvas = EtsSignRaster.Area(library, art, width, height, ppm, all);
                byte[] rgba = canvas.ToRgba32(out bool any);

                if (!any)
                {
                    Empty++;
                    continue;
                }

                EtsModelLocator l = model.Locators[locator];
                faces.Add(new EtsSignFacePlan
                {
                    Area = w.Name,
                    Position = (float[])l.Position.Clone(),
                    Rotation = (float[])l.Rotation.Clone(),
                    Width = width,
                    Height = height,
                    PixelsWide = canvas.Width,
                    PixelsHigh = canvas.Height,
                    Rgba = rgba,
                    Items = all.Count
                });
                Planned++;
            }

            return faces;
        }

        private static EtsSignItem Find(List<EtsSignItem> items, uint id)
        {
            foreach (EtsSignItem item in items)
            {
                if (item.Id == id) return item;
            }

            return null;
        }
    }
}
