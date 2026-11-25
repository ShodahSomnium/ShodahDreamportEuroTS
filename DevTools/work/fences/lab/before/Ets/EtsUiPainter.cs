using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// One drawing operation of the game's UI markup: an image layer or a block of text (owner D; [gameui §2.3],
    /// PLAN.md §7.D). Coordinates are in the element's own box, x right and y DOWN from its top-left corner, in
    /// the 1440 x 900 frame's units - the way the markup counts, and the way EtsUiPainter paints. It is declared
    /// here rather than beside EtsPml so that this file stays free of UnityEngine: the harness list every
    /// implementer builds probes from (S/harness/harness119.rsp) compiles EtsUiPainter.cs without EtsPml.cs.
    ///
    /// An image takes (SrcX, SrcY, SrcW, SrcH) of its material's texture - negative SrcW/SrcH mirror it, and a
    /// zero SrcW/SrcH samples a single column/row, which is how the templates stretch one pixel - into
    /// (X, Y, W, H), tinted by Argb (AARRGGBB). IsSdf marks a signed-distance-field icon (effect ui.sdf.rfx),
    /// whose "texture" is the icon painted at its nominal aux[0] size. TileX/TileY (xscale=repeat) repeat the
    /// source instead of stretching it.
    ///
    /// A text block carries the run(s) of text as TextMeshPro rich text - colour changes as &lt;color=#RRGGBBAA&gt;,
    /// &lt;br&gt; as a line break, a horizontal &lt;offset&gt; as &lt;space=N&gt; - to be laid out inside
    /// (X, Y, W, H) with HAlign (0 left, 1 centre, 2 right) and VAlign (0 top, 1 centre, 2 bottom), in the face
    /// FontPath scaled by XScale/YScale, starting colour Argb, wrapping unless the markup said &lt;nowrap&gt;.
    /// </summary>
    public sealed class EtsPmlOp
    {
        public bool IsText; public string Material; public int SrcX, SrcY, SrcW, SrcH;   // src may be mirrored (negative W/H)
        public float X, Y, W, H; public uint Argb; public bool IsSdf;
        public string FontPath, Text; public float XScale, YScale; public int HAlign, VAlign; public bool Wrap;

        /// <summary>xscale=repeat / yscale=repeat: the source is repeated across the destination (40 uses in 1.61's templates).</summary>
        public bool TileX, TileY;

        public override string ToString()
        {
            return IsText
                ? "text '" + Text + "' box " + X + "," + Y + " " + W + "x" + H + " align " + HAlign + "/" + VAlign + " " + FontPath
                : "img " + Material + " src " + SrcX + "," + SrcY + " " + SrcW + "x" + SrcH + " -> " + X + "," + Y + " " + W + "x" + H + " " + Argb.ToString("X8") + (IsSdf ? " sdf" : "");
        }
    }

    /// <summary>
    /// Paints the game's interface chrome on the CPU, the way the game's own markup builds it.
    ///
    /// A window in the game is not a texture; it is a recipe in /ui/template/text.sii that cuts
    /// pieces out of a small atlas and lays them side by side. txt.window.dyn_bcg.nl reads, across
    /// each band: the left corner column of bcg2 (pixels 54 to 70), a single column at 70
    /// stretched, the section 70 to 256, the same section mirrored, the stretched column again,
    /// and the corner mirrored. For the launchpad's 824-pixel window that stretch comes out at
    /// exactly the 210 the layout passes in, so the reading is right. Vertically there is a top
    /// band with the curve, a one-pixel row stretched to fill, and a bottom band from bcg0.
    ///
    /// Doing this here rather than with sliced sprites means the result can be written to a PNG
    /// and looked at before it goes anywhere near a screen - which is how the seams and the white
    /// box of the earlier attempt would have been caught.
    ///
    /// Bitmaps are RGBA with rows from the top; colours are ARGB the way the markup writes them
    /// (FF00AEFF is opaque, blue-ish). Textures come through a loader so the harness and Unity can
    /// share every line of this.
    /// </summary>
    public sealed class EtsUiPainter
    {
        public delegate EtsBitmap Loader(string materialPath);

        /// <summary>The text of a .mat file, or null: the SDF icons' aux[] values are read from it.</summary>
        public delegate string MaterialReader(string materialPath);

        private readonly Loader _load;
        private readonly MaterialReader _readMaterial;
        private readonly Dictionary<string, EtsBitmap> _cache = new Dictionary<string, EtsBitmap>(StringComparer.Ordinal);
        private readonly Dictionary<string, float[][]> _aux = new Dictionary<string, float[][]>(StringComparer.Ordinal);
        private readonly Dictionary<string, EtsBitmap> _sdfNominal = new Dictionary<string, EtsBitmap>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _opaqueAlpha = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _isSdf = new Dictionary<string, bool>(StringComparer.Ordinal);

        public EtsUiPainter(Loader load) : this(load, null)
        {
        }

        public EtsUiPainter(Loader load, MaterialReader readMaterial)
        {
            _load = load;
            _readMaterial = readMaterial;
        }

        public EtsBitmap Texture(string materialPath)
        {
            if (string.IsNullOrEmpty(materialPath))
            {
                return null;
            }

            if (_cache.TryGetValue(materialPath, out EtsBitmap cached))
            {
                return cached;
            }

            EtsBitmap loaded = _load(materialPath);
            _cache[materialPath] = loaded;
            return loaded;
        }

        // ------------------------------------------------------------------ windows

        /// <summary>The numbers a window template cuts its atlas with.</summary>
        public struct WindowRecipe
        {
            public string TopMaterial;
            public string BottomMaterial;
            public int CornerLeft;
            public int CornerRight;
            public int TopStart;
            public int TopEnd;
            public int MiddleRow;
            public int BottomStart;
            public int BottomEnd;
        }

        /// <summary>txt.window.dyn_bcg.nl: the ordinary window, a light hairline round a dark sheet.</summary>
        public static readonly WindowRecipe Normal = new WindowRecipe
        {
            TopMaterial = "/material/ui/window/bcg2.mat",
            BottomMaterial = "/material/ui/window/bcg0.mat",
            CornerLeft = 54, CornerRight = 70, TopStart = 78, TopEnd = 100, MiddleRow = 100,
            BottomStart = 0, BottomEnd = 64
        };

        /// <summary>txt.window.dyn_bcg.dark: the darker panel the news boxes sit in.</summary>
        public static readonly WindowRecipe Dark = new WindowRecipe
        {
            TopMaterial = "/material/ui/window/bcg3.mat",
            BottomMaterial = "/material/ui/window/bcg1.mat",
            // bcg3 is 256x128: the corner turns at y 6, a lighter title strip runs to about 40 and
            // the dark inside continues to 96, which is the row the template stretches. bcg1 holds
            // the bottom corner in its first 64 rows (the template asks for 70; the image ends).
            CornerLeft = 54, CornerRight = 76, TopStart = 6, TopEnd = 96, MiddleRow = 96,
            BottomStart = 0, BottomEnd = 64
        };

        public EtsBitmap Window(int width, int height, bool dark)
        {
            return Window(width, height, dark ? Dark : Normal);
        }

        public EtsBitmap Window(int width, int height, WindowRecipe recipe)
        {
            var canvas = new EtsBitmap(width, height);
            EtsBitmap top = Texture(recipe.TopMaterial);
            EtsBitmap bottom = Texture(recipe.BottomMaterial);

            if (top == null)
            {
                Fill(canvas, 0, 0, width, height, 0xF0121212);
                return canvas;
            }

            int topHeight = recipe.TopEnd - recipe.TopStart;
            int bottomHeight = bottom != null ? recipe.BottomEnd - recipe.BottomStart : 0;
            int middleHeight = Math.Max(0, height - topHeight - bottomHeight);

            Band(canvas, 0, topHeight, top, recipe.TopStart, recipe.TopEnd, recipe.CornerLeft, recipe.CornerRight);
            Band(canvas, topHeight, middleHeight, top, recipe.MiddleRow, recipe.MiddleRow + 1, recipe.CornerLeft, recipe.CornerRight);

            if (bottom != null)
            {
                Band(canvas, topHeight + middleHeight, bottomHeight, bottom, recipe.BottomStart, recipe.BottomEnd, recipe.CornerLeft, recipe.CornerRight);
            }

            return canvas;
        }

        /// <summary>
        /// One horizontal band of a window: corner, stretched column, body, mirrored body,
        /// stretched column, mirrored corner - the six pieces the template lays down.
        /// </summary>
        private static void Band(EtsBitmap canvas, int y, int h, EtsBitmap atlas, int rowStart, int rowEnd, int cornerLeft, int cornerRight)
        {
            if (h <= 0)
            {
                return;
            }

            int corner = cornerRight - cornerLeft;
            int body = atlas.Width - cornerRight;
            int stretch = (canvas.Width - 2 * corner - 2 * body) / 2;

            if (stretch < 0)
            {
                // Too narrow for the whole body: keep the corners and share what is left.
                body = Math.Max(0, (canvas.Width - 2 * corner) / 2);
                stretch = 0;
            }

            int x = 0;
            Blit(canvas, x, y, corner, h, atlas, cornerLeft, rowStart, cornerRight, rowEnd, 0xFFFFFFFF); x += corner;
            Blit(canvas, x, y, stretch, h, atlas, cornerRight, rowStart, cornerRight + 1, rowEnd, 0xFFFFFFFF); x += stretch;
            Blit(canvas, x, y, body, h, atlas, cornerRight, rowStart, cornerRight + body, rowEnd, 0xFFFFFFFF); x += body;
            Blit(canvas, x, y, body, h, atlas, cornerRight + body, rowStart, cornerRight, rowEnd, 0xFFFFFFFF); x += body;
            Blit(canvas, x, y, stretch, h, atlas, cornerRight + 1, rowStart, cornerRight, rowEnd, 0xFFFFFFFF); x += stretch;
            Blit(canvas, x, y, canvas.Width - x, h, atlas, cornerRight, rowStart, cornerLeft, rowEnd, 0xFFFFFFFF);
        }

        // ------------------------------------------------------------------ pieces

        /// <summary>
        /// A strip texture repeated across a width - btn_dark, btn_light and the like are 8 pixels
        /// wide and meant to be drawn edge to edge.
        /// </summary>
        public EtsBitmap Strip(string materialPath, int width, int height, uint tint)
        {
            var canvas = new EtsBitmap(width, height);
            EtsBitmap strip = Texture(materialPath);

            if (strip == null)
            {
                Fill(canvas, 0, 0, width, height, 0xFF2A2A2A);
                return canvas;
            }

            Blit(canvas, 0, 0, width, height, strip, 0, 0, strip.Width, strip.Height, tint);
            return canvas;
        }

        /// <summary>
        /// Half of a texture: the game keeps a button's normal face in the left half of an image
        /// and its lit face in the right (or the other way about, for a few).
        /// </summary>
        public EtsBitmap Half(string materialPath, bool right, int width, int height, uint tint)
        {
            var canvas = new EtsBitmap(width, height);
            EtsBitmap image = Texture(materialPath);

            if (image == null)
            {
                return canvas;
            }

            int half = image.Width / 2;
            Blit(canvas, 0, 0, width, height, image, right ? half : 0, 0, right ? image.Width : half, image.Height, tint);
            return canvas;
        }

        /// <summary>
        /// A rectangle of a texture, in its pixels, stretched to a size: the markup's
        /// "right=p96 bottom=p96" on the 128-pixel profile picture, for one.
        /// </summary>
        public EtsBitmap Region(string materialPath, int sx0, int sy0, int sx1, int sy1, int width, int height, uint tint)
        {
            var canvas = new EtsBitmap(width, height);
            EtsBitmap image = Texture(materialPath);

            if (image != null)
            {
                Blit(canvas, 0, 0, width, height, image, sx0, sy0, Math.Min(sx1, image.Width), Math.Min(sy1, image.Height), tint);
            }

            return canvas;
        }

        /// <summary>A whole texture stretched to a size, tinted.</summary>
        public EtsBitmap Whole(string materialPath, int width, int height, uint tint)
        {
            var canvas = new EtsBitmap(width, height);
            EtsBitmap image = Texture(materialPath);

            if (image != null)
            {
                Blit(canvas, 0, 0, width, height, image, 0, 0, image.Width, image.Height, tint);
            }

            return canvas;
        }

        // ------------------------------------------------------------------ 1.61: signed-distance-field icons

        /// <summary>
        /// The aux[0..4] vectors of a material ("aux[1] : { 0.57758, 0.57758, 0.57758, 1.00000 }"), read from its
        /// text by hand: EtsMaterialDef strips the index and keeps only the last aux. Null when the material
        /// cannot be read or no reader was given.
        /// </summary>
        public float[][] Aux(string materialPath)
        {
            if (string.IsNullOrEmpty(materialPath) || _readMaterial == null)
            {
                return null;
            }

            if (_aux.TryGetValue(materialPath, out float[][] cached))
            {
                return cached;
            }

            string text = _readMaterial(materialPath);
            float[][] aux = null;

            if (text != null)
            {
                aux = new float[5][];

                foreach (string line in text.Split('\n'))
                {
                    string t = line.Trim();

                    if (!t.StartsWith("aux[", StringComparison.Ordinal) || t.Length < 6 || t[4] < '0' || t[4] > '4')
                    {
                        continue;
                    }

                    int open = t.IndexOf('{'), close = t.IndexOf('}');

                    if (open < 0 || close <= open)
                    {
                        continue;
                    }

                    string[] parts = t.Substring(open + 1, close - open - 1).Split(',');
                    var values = new float[parts.Length];

                    for (int k = 0; k < parts.Length; k++)
                    {
                        values[k] = EtsSiiValue.ParseFloat(parts[k].Trim(), 0f);
                    }

                    aux[t[4] - '0'] = values;
                }
            }

            _aux[materialPath] = aux;
            return aux;
        }

        /// <summary>Whether a material is a 1.61 signed-distance-field icon (effect "ui.sdf.rfx").</summary>
        public bool IsSdf(string materialPath)
        {
            if (string.IsNullOrEmpty(materialPath) || _readMaterial == null)
            {
                return false;
            }

            if (!_isSdf.TryGetValue(materialPath, out bool sdf))
            {
                string text = _readMaterial(materialPath);
                sdf = text != null && text.IndexOf("ui.sdf.rfx", StringComparison.Ordinal) >= 0;
                _isSdf[materialPath] = sdf;
            }

            return sdf;
        }

        /// <summary>
        /// The size markup coordinates of a material refer to: an SDF icon's nominal aux[0] size (job_market:
        /// 89 x 89, city_pin_2: 36 x 36), otherwise its texture's. False when there is no texture.
        /// </summary>
        public bool TrySize(string materialPath, out int width, out int height)
        {
            width = height = 0;

            if (IsSdf(materialPath))
            {
                float[][] aux = Aux(materialPath);

                if (aux != null && aux[0] != null && aux[0].Length >= 2 && Texture(materialPath) != null)
                {
                    width = Math.Max(1, (int)aux[0][0]);
                    height = Math.Max(1, (int)aux[0][1]);
                    return true;
                }
            }

            EtsBitmap texture = Texture(materialPath);

            if (texture == null)
            {
                return false;
            }

            width = texture.Width;
            height = texture.Height;
            return true;
        }

        /// <summary>
        /// A 1.61 icon painted at a size: effect ui.sdf.rfx ([gameui §2.4]). Every texture channel is one shape,
        /// a signed distance field with the edge at 0.5 and aux[0].z (2 on every icon seen) texels of range; aux[1..4]
        /// give each channel's colour, linear RGBA (alpha 0 = unused). Channels are laid R, G, B, A over a clear
        /// plate, each edge smoothed over one screen pixel, the colour converted linear to sRGB - the reading of
        /// probe 1648, which drew city_pin_0 (grey disc, light ring), city_pin_2 (the orange "chosen city" dot),
        /// the Jobs, Drive and Navigation links and the dice correctly (png/sdf_icons.png). Probe 1882 paints the
        /// same six through this method and compares them with that PNG pixel for pixel. An alpha channel that is
        /// opaque everywhere is the "X" of a B8G8R8X8 texture, not a shape, and is skipped (1648 skipped it by
        /// format). The result is multiplied by tint (AARRGGBB). Null when the material is not an SDF icon.
        /// </summary>
        public EtsBitmap Sdf(string materialPath, int width, int height, uint tint)
        {
            float[][] aux = Aux(materialPath);
            EtsBitmap src = Texture(materialPath);

            if (aux == null || src == null || !IsSdf(materialPath) || width <= 0 || height <= 0)
            {
                return null;
            }

            if (!_opaqueAlpha.TryGetValue(materialPath, out bool opaqueAlpha))
            {
                opaqueAlpha = true;

                for (int i = 3; i < src.Rgba.Length && opaqueAlpha; i += 4)
                {
                    opaqueAlpha = src.Rgba[i] == 255;
                }

                _opaqueAlpha[materialPath] = opaqueAlpha;
            }

            float range = aux[0] != null && aux[0].Length > 2 ? aux[0][2] : 2f;
            var dst = new EtsBitmap(width, height);
            float texPerScreenX = (float)src.Width / width, texPerScreenY = (float)src.Height / height;

            // Distance units: a channel's 0..1 spans +-range texels round the edge, so one screen pixel is
            // texPerScreen texels = texPerScreen / (2 range) in value; the edge is smoothed over half of that
            // either side.
            float w = 0.5f * texPerScreenX / (2f * range);
            float ta = ((tint >> 24) & 255) / 255f, tr = ((tint >> 16) & 255) / 255f, tg = ((tint >> 8) & 255) / 255f, tb = (tint & 255) / 255f;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float u = (x + 0.5f) * texPerScreenX - 0.5f, v = (y + 0.5f) * texPerScreenY - 0.5f;
                    float r = 0, g = 0, b = 0, a = 0;

                    for (int c = 0; c < 4; c++)
                    {
                        float[] col = aux[c + 1];

                        if (col == null || col.Length < 4 || col[3] <= 0f || (c == 3 && opaqueAlpha))
                        {
                            continue;
                        }

                        float d = SampleChannel(src, u, v, c);
                        float cov = Smooth(0.5f - w, 0.5f + w, d) * col[3];
                        float cr = Srgb(col[0]), cg = Srgb(col[1]), cb = Srgb(col[2]);
                        r = cr * cov + r * (1 - cov); g = cg * cov + g * (1 - cov); b = cb * cov + b * (1 - cov); a = cov + a * (1 - cov);
                    }

                    int o = dst.Offset(x, y);

                    // Straight (un-premultiplied) colour, as the probe stored it.
                    dst.Rgba[o] = (byte)(a > 0 ? Math.Min(255, r / a * 255) : 0);
                    dst.Rgba[o + 1] = (byte)(a > 0 ? Math.Min(255, g / a * 255) : 0);
                    dst.Rgba[o + 2] = (byte)(a > 0 ? Math.Min(255, b / a * 255) : 0);
                    dst.Rgba[o + 3] = (byte)(a * 255);

                    if (tint != 0xFFFFFFFFu)
                    {
                        dst.Rgba[o] = (byte)(dst.Rgba[o] * tr);
                        dst.Rgba[o + 1] = (byte)(dst.Rgba[o + 1] * tg);
                        dst.Rgba[o + 2] = (byte)(dst.Rgba[o + 2] * tb);
                        dst.Rgba[o + 3] = (byte)(dst.Rgba[o + 3] * ta);
                    }
                }
            }

            return dst;
        }

        /// <summary>
        /// An SDF icon painted once at its nominal aux[0] size and kept: the "texture" markup coordinates cut
        /// from (probe 1650 drew btn.desktop_link's 48 x 48 Jobs icon from the 89 x 89 painting this way).
        /// </summary>
        public EtsBitmap SdfNominal(string materialPath)
        {
            if (_sdfNominal.TryGetValue(materialPath, out EtsBitmap cached))
            {
                return cached;
            }

            EtsBitmap painted = TrySize(materialPath, out int w, out int h) ? Sdf(materialPath, w, h, 0xFFFFFFFFu) : null;
            _sdfNominal[materialPath] = painted;
            return painted;
        }

        private static float Srgb(float lin)
        {
            lin = Math.Max(0f, Math.Min(1f, lin));
            return lin <= 0.0031308f ? lin * 12.92f : 1.055f * (float)Math.Pow(lin, 1 / 2.4) - 0.055f;
        }

        private static float Smooth(float e0, float e1, float x)
        {
            float t = Math.Max(0f, Math.Min(1f, (x - e0) / (e1 - e0)));
            return t * t * (3 - 2 * t);
        }

        private static float SampleChannel(EtsBitmap s, float u, float v, int c)
        {
            int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
            float fx = u - x0, fy = v - y0;
            float top = Channel(s, x0, y0, c) * (1 - fx) + Channel(s, x0 + 1, y0, c) * fx;
            float bot = Channel(s, x0, y0 + 1, c) * (1 - fx) + Channel(s, x0 + 1, y0 + 1, c) * fx;
            return top * (1 - fy) + bot * fy;
        }

        private static float Channel(EtsBitmap s, int x, int y, int c)
        {
            x = Math.Max(0, Math.Min(s.Width - 1, x));
            y = Math.Max(0, Math.Min(s.Height - 1, y));
            return s.Rgba[s.Offset(x, y) + c] / 255f;
        }

        // ------------------------------------------------------------------ 1.61: markup operations

        /// <summary>
        /// Paints the image operations EtsPml.Layout made from a template, at their own positions, into a
        /// clear bitmap of the element's size. Text operations are left to TextMeshPro and skipped here.
        /// </summary>
        public EtsBitmap Ops(List<EtsPmlOp> ops, int width, int height)
        {
            var canvas = new EtsBitmap(width, height);

            if (ops != null)
            {
                foreach (EtsPmlOp op in ops)
                {
                    PaintOp(canvas, 0, 0, op);
                }
            }

            return canvas;
        }

        /// <summary>
        /// One image operation painted into dst with the element's top-left at (ox, oy): the source rectangle
        /// (mirrored when reversed) sampled bilinearly into the destination rounded to whole pixels, tinted,
        /// composited over what is there - probe 1650's blit, which is why its PNG can be matched exactly. An SDF
        /// icon is cut from its nominal-size painting (SdfNominal). A text operation paints nothing.
        /// </summary>
        public void PaintOp(EtsBitmap dst, int ox, int oy, EtsPmlOp op)
        {
            if (op == null || op.IsText || dst == null)
            {
                return;
            }

            EtsBitmap src = op.IsSdf || IsSdf(op.Material) ? SdfNominal(op.Material) : Texture(op.Material);

            if (src == null)
            {
                return;
            }

            int dx = ox + (int)Math.Round(op.X), dy = oy + (int)Math.Round(op.Y);
            int dw = (int)Math.Round(op.W), dh = (int)Math.Round(op.H);
            float sx0 = op.SrcX, sy0 = op.SrcY, sx1 = op.SrcX + op.SrcW, sy1 = op.SrcY + op.SrcH;

            if (!op.TileX && !op.TileY)
            {
                Blit(dst, dx, dy, dw, dh, src, sx0, sy0, sx1, sy1, op.Argb);
                return;
            }

            // xscale/yscale=repeat: the source at its own size, laid again and again (unverified; see EtsPml).
            int stepX = op.TileX ? Math.Max(1, Math.Abs(op.SrcW)) : dw, stepY = op.TileY ? Math.Max(1, Math.Abs(op.SrcH)) : dh;

            for (int ty = 0; ty < dh; ty += stepY)
            {
                for (int tx = 0; tx < dw; tx += stepX)
                {
                    int w = Math.Min(stepX, dw - tx), h = Math.Min(stepY, dh - ty);
                    float fx = op.TileX ? (float)w / stepX : 1f, fy = op.TileY ? (float)h / stepY : 1f;
                    Blit(dst, dx + tx, dy + ty, w, h, src, sx0, sy0, sx0 + (sx1 - sx0) * fx, sy0 + (sy1 - sy0) * fy, op.Argb);
                }
            }
        }

        // ------------------------------------------------------------------ 1.61: flipsheets

        /// <summary>
        /// A flipsheet animation's numbers (effect ui.flipsheet.rfx): /video/busy.mat, the loading spinner, has
        /// aux[0] = {30 frames, 8 columns, 30 fps} and aux[1] = {0.125, 0.25}, the frame's size in UV, on a
        /// 512 x 256 sheet ([gameui §2.4], probe 1643). Frame f sits at column f % columns, row f / columns, rows
        /// counted from the top. In Unity the loading screen moves a RawImage's uvRect over the sheet rather than
        /// painting frames, so nothing is allocated per frame.
        /// </summary>
        public bool TryFlipsheet(string materialPath, out int frames, out int columns, out float fps, out float frameU, out float frameV)
        {
            frames = columns = 0;
            fps = frameU = frameV = 0f;
            float[][] aux = Aux(materialPath);

            if (aux == null || aux[0] == null || aux[0].Length < 3 || aux[1] == null || aux[1].Length < 2)
            {
                return false;
            }

            frames = (int)aux[0][0];
            columns = Math.Max(1, (int)aux[0][1]);
            fps = aux[0][2];
            frameU = aux[1][0];
            frameV = aux[1][1];
            return frames > 0 && frameU > 0f && frameV > 0f;
        }

        /// <summary>One frame of a flipsheet cut out at its own size (for the harness and PNG checks).</summary>
        public EtsBitmap FlipFrame(string materialPath, int frame)
        {
            EtsBitmap sheet = Texture(materialPath);

            if (sheet == null || !TryFlipsheet(materialPath, out int frames, out int columns, out _, out float fu, out float fv))
            {
                return null;
            }

            int f = ((frame % frames) + frames) % frames;
            int w = (int)Math.Round(fu * sheet.Width), h = (int)Math.Round(fv * sheet.Height);
            int sx = (f % columns) * w, sy = (f / columns) * h;
            var canvas = new EtsBitmap(w, h);
            Blit(canvas, 0, 0, w, h, sheet, sx, sy, sx + w, sy + h, 0xFFFFFFFFu);
            return canvas;
        }

        // ------------------------------------------------------------------ primitives

        public static EtsBitmap Solid(int width, int height, uint argb)
        {
            var canvas = new EtsBitmap(width, height);
            Fill(canvas, 0, 0, width, height, argb);
            return canvas;
        }

        public static void Fill(EtsBitmap dst, int x, int y, int w, int h, uint argb)
        {
            byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;

            for (int yy = Math.Max(0, y); yy < Math.Min(dst.Height, y + h); yy++)
            {
                for (int xx = Math.Max(0, x); xx < Math.Min(dst.Width, x + w); xx++)
                {
                    Over(dst, dst.Offset(xx, yy), r, g, b, a);
                }
            }
        }

        /// <summary>
        /// Draws the source rectangle (sx0,sy0)-(sx1,sy1) into the destination rectangle, sampling
        /// bilinearly and compositing over what is there. A source rectangle given right to left
        /// or bottom to top comes out mirrored, which is how the templates flip their corners.
        /// </summary>
        public static void Blit(EtsBitmap dst, int dx, int dy, int dw, int dh, EtsBitmap src, float sx0, float sy0, float sx1, float sy1, uint tint)
        {
            if (dw <= 0 || dh <= 0 || src == null)
            {
                return;
            }

            float ta = ((tint >> 24) & 255) / 255f, tr = ((tint >> 16) & 255) / 255f, tg = ((tint >> 8) & 255) / 255f, tb = (tint & 255) / 255f;
            float spanX = sx1 - sx0, spanY = sy1 - sy0;
            bool pointX = Math.Abs(spanX) <= 1.0001f, pointY = Math.Abs(spanY) <= 1.0001f;

            for (int y = 0; y < dh; y++)
            {
                int py = dy + y;

                if (py < 0 || py >= dst.Height)
                {
                    continue;
                }

                float v = pointY ? Math.Min(sy0, sy1) : sy0 + (y + 0.5f) / dh * spanY - 0.5f;

                for (int x = 0; x < dw; x++)
                {
                    int px = dx + x;

                    if (px < 0 || px >= dst.Width)
                    {
                        continue;
                    }

                    float u = pointX ? Math.Min(sx0, sx1) : sx0 + (x + 0.5f) / dw * spanX - 0.5f;
                    Sample(src, u, v, out float r, out float g, out float b, out float a);
                    Over(dst, dst.Offset(px, py), (byte)(r * tr * 255f), (byte)(g * tg * 255f), (byte)(b * tb * 255f), (byte)(a * ta * 255f));
                }
            }
        }

        private static void Sample(EtsBitmap src, float u, float v, out float r, out float g, out float b, out float a)
        {
            int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
            float fx = u - x0, fy = v - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            x0 = Clamp(x0, 0, src.Width - 1); x1 = Clamp(x1, 0, src.Width - 1);
            y0 = Clamp(y0, 0, src.Height - 1); y1 = Clamp(y1, 0, src.Height - 1);

            int o00 = src.Offset(x0, y0), o10 = src.Offset(x1, y0), o01 = src.Offset(x0, y1), o11 = src.Offset(x1, y1);
            byte[] p = src.Rgba;

            r = Lerp(Lerp(p[o00], p[o10], fx), Lerp(p[o01], p[o11], fx), fy) / 255f;
            g = Lerp(Lerp(p[o00 + 1], p[o10 + 1], fx), Lerp(p[o01 + 1], p[o11 + 1], fx), fy) / 255f;
            b = Lerp(Lerp(p[o00 + 2], p[o10 + 2], fx), Lerp(p[o01 + 2], p[o11 + 2], fx), fy) / 255f;
            a = Lerp(Lerp(p[o00 + 3], p[o10 + 3], fx), Lerp(p[o01 + 3], p[o11 + 3], fx), fy) / 255f;
        }

        private static void Over(EtsBitmap dst, int o, byte r, byte g, byte b, byte a)
        {
            if (a == 0)
            {
                return;
            }

            byte[] p = dst.Rgba;

            if (a == 255)
            {
                p[o] = r; p[o + 1] = g; p[o + 2] = b; p[o + 3] = 255;
                return;
            }

            float sa = a / 255f, da = p[o + 3] / 255f;
            float outA = sa + da * (1f - sa);

            if (outA <= 0.0001f)
            {
                p[o] = 0; p[o + 1] = 0; p[o + 2] = 0; p[o + 3] = 0;
                return;
            }

            p[o] = (byte)((r * sa + p[o] * da * (1f - sa)) / outA);
            p[o + 1] = (byte)((g * sa + p[o + 1] * da * (1f - sa)) / outA);
            p[o + 2] = (byte)((b * sa + p[o + 2] * da * (1f - sa)) / outA);
            p[o + 3] = (byte)(outA * 255f);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }
    }
}
