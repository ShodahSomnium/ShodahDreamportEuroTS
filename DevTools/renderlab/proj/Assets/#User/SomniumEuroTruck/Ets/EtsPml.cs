using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The game's UI markup ("pml": /ui/template/text.sii and button.sii, the text / n_pml / s_pml / d_pml fields
    /// of 678 templates on 1.61) laid out into drawing operations (owner D; [gameui §2.3], PLAN.md §7.D, U7).
    /// Unity-free apart from Vector2Int in the signature, so the harness runs it.
    ///
    /// The reading is probe 1650's, which painted txt.window.bcg_rect4, btn.generic (n and s), btn.normal,
    /// btn.desktop_link, btn.desktop_link.single and txt.ttip_simple so that every piece tiles exactly
    /// (bcg_rect4 at 620 x 325 = 16 + 588 + 16 wide and 4 + 305 + 16 tall; png/pml_test.png). Probe 1882 lays the
    /// same seven out with this class, paints them with EtsUiPainter.PaintOp and compares the result with that
    /// PNG pixel for pixel:
    ///
    ///   - the cursor starts at the box's top-left; an &lt;img&gt; draws at the cursor and moves x right by its
    ///     width, and the tallest image of the line is the line's height;
    ///   - &lt;ret&gt; puts the cursor back at the box origin (a new layer on top) and ends every &lt;align&gt;;
    ///     the colour carries on (desktop_link.single sets white again after its &lt;ret&gt;, which it would not
    ///     need to if &lt;ret&gt; reset it);
    ///   - &lt;br [offset=n]&gt;: y += line height + n, x back to the line start; &lt;hbr&gt;: x back only;
    ///   - &lt;offset hshift= vshift=&gt; moves the cursor;
    ///   - &lt;align hstyle= vstyle=&gt; places what follows in the box: centre and right/bottom ignore the
    ///     cursor on that axis (the cursor still advances);
    ///   - &lt;img src left right top bottom&gt;: the source rectangle, "pN" in texture pixels, a bare number a
    ///     fraction of the texture; a reversed pair mirrors. width/height: the destination size, a negative value
    ///     being the room left from the cursor to the box edge minus |n|; xscale/yscale=stretch fills to the box
    ///     edge; the default is the source size. color= tints this image only;
    ///   - &lt;color value=AABBGGRR&gt; tints what follows; @@clr_*@@ keys are resolved through EtsLocale first,
    ///     and %0..%F are the element's |-separated value parts (a missing one is empty).
    ///
    /// Literal line breaks inside the markup are layout whitespace, not &lt;br&gt;, and an image that no longer fits
    /// on its line wraps to the next (y += line height, x back to the start). txt.window.dyn_bcg.nl writes each
    /// six-piece band over five lines of the file with a &lt;br&gt; only after the first band, and its bottom band
    /// starts a new row only because the middle band has filled the width; txt.truck_prefer.description ends each
    /// of its eight rows with "&lt;br&gt;" AND a newline, and the rows are single-spaced in the game; btn.pic_truck's
    /// bottom border (xscale=stretch after a full-width top border) spans the button only if it wraps. Probe 1650
    /// read a newline as &lt;br&gt; instead; for its seven templates both readings place every image identically.
    ///
    /// Read but not exercised by any probe yet, and so marked unverified: &lt;margin left= right=&gt; (the box
    /// narrowed; a negative right counts from the right edge), &lt;align ... left= right= top= bottom=&gt; sub-boxes
    /// (the same convention), stretch_full (taken as stretch), xscale=repeat, &lt;/color&gt; (restores the colour
    /// before the matching &lt;color&gt;). ka= (9 uses), &lt;alignv&gt; (a typo in btn.desktop.top_bar.bank, 2 uses)
    /// and &lt;link&gt; are ignored.
    ///
    /// Text has no width here: a text block does not move the cursor sideways, so an image written after text on
    /// the same line (btn.job_offer's "from &lt;arrow&gt; to") lands where the text began. Such templates are laid out
    /// by hand until a measurer is added.
    ///
    /// Text does have a height (Phase 2, owner D; E's probe 1906, S/harness/out1906.txt): a text run makes its line
    /// at least LineHeight(face) x yscale tall, so a &lt;br&gt; after text moves down by the text's line, not only by
    /// the images on it. And an &lt;align vstyle=center|bottom&gt; group that spans lines (a &lt;br&gt; inside it) is
    /// placed as one block: laid out top-down, then moved so the block's height is centred (or bottom-aligned) in the
    /// box. Before, each part was centred on its own, so btn.desktop.top_bar.personal - the driver's name over a
    /// 14-unit XP bar in a 40-unit box - drew the name's centre line and the bar's on top of each other, and the bar
    /// hid the name (probe 1906: TEXT 'Deriick' y 0 h 40 valign centre, IMG bar y 13 h 14). A group on one line keeps
    /// the old per-part centring, which is what a button's centred caption needs (TMP centres it in the box). Probe
    /// 2041 lays out all 678 templates both ways and lists every template whose image operations moved.
    ///
    /// Zero-sized images are not emitted (Phase 2): btn.desktop.top_bar.personal's XP bar is "width=%2", and at 0 XP
    /// that is width 0 - EtsGameUi.Place makes any rectangle at least one unit wide, which drew the 1-unit orange
    /// sliver E reported at 0 XP.
    /// </summary>
    public static class EtsPml
    {
        private const string Hex = "0123456789ABCDEF";

        private struct Box
        {
            public float X0, Y0, X1, Y1;
        }

        private struct AlignState
        {
            public int H, V;
            public Box Box;
        }

        /// <summary>
        /// The height of one text line in the 1440 x 900 frame for a face at yscale 1: what EtsGameUi's labels draw
        /// (EtsFont.Size: the file's vert_span x 900/768 x default_scale 0.83; EtsGameUi sets this to EtsFont.Size in
        /// Unity). The default is the same arithmetic on [gameui §2.5]'s vert_span per face (probe 1652), so the harness
        /// lays text out as Unity will: normal 20.80, normal_bold 20.96, big 24.45, big_bold 24.82, title 26.40,
        /// title_bold 26.76, small 18.47, small_bold 18.75.
        /// </summary>
        public static Func<string, float> LineHeight = DefaultLineHeight;

        public static float DefaultLineHeight(string face)
        {
            const float k = 900f / 768f * 0.83f;
            string f = face ?? "";
            float span = f.EndsWith("/normal_bold.font", StringComparison.Ordinal) ? 21.55f :
                         f.EndsWith("/big_bold.font", StringComparison.Ordinal) ? 25.52f :
                         f.EndsWith("/big.font", StringComparison.Ordinal) ? 25.14f :
                         f.EndsWith("/title_bold.font", StringComparison.Ordinal) ? 27.52f :
                         f.EndsWith("/title.font", StringComparison.Ordinal) ? 27.14f :
                         f.EndsWith("/small_bold.font", StringComparison.Ordinal) ? 19.28f :
                         f.EndsWith("/small.font", StringComparison.Ordinal) ? 18.99f : 21.39f;
            return span * k;
        }

        private static float TextLine(string face, float yscale)
        {
            float h;

            try
            {
                h = LineHeight != null ? LineHeight(face) : DefaultLineHeight(face);
            }
            catch (Exception)
            {
                h = DefaultLineHeight(face);
            }

            if (float.IsNaN(h) || float.IsInfinity(h) || h <= 0f) h = DefaultLineHeight(face);
            return h * (yscale > 0f ? yscale : 1f);
        }

        public static List<EtsPmlOp> Layout(string markup, string[] args, int width, int height,
                                            Func<string, Vector2Int> textureSize)
        {
            return Layout(markup, args, width, height, textureSize, null);
        }

        /// <summary>As Layout, with isSdf telling which materials are signed-distance-field icons (op.IsSdf).</summary>
        public static List<EtsPmlOp> Layout(string markup, string[] args, int width, int height,
                                            Func<string, Vector2Int> textureSize, Func<string, bool> isSdf)
        {
            var ops = new List<EtsPmlOp>();

            if (string.IsNullOrEmpty(markup))
            {
                return ops;
            }

            string text = EtsLocale.Resolve(Substitute(markup, args));

            var box = new Box { X0 = 0f, Y0 = 0f, X1 = width, Y1 = height };
            var margins = new Stack<Box>();
            var aligns = new Stack<AlignState>();
            var colours = new Stack<uint>();
            float x = 0f, y = 0f, lineH = 0f;
            int hAlign = 0, vAlign = 0;
            Box alignBox = box;
            uint tint = 0xFFFFFFFFu;
            string face = "/font/normal.font";
            float fontX = 1f, fontY = 1f;
            bool wrap = true;
            EtsPmlOp block = null;
            StringBuilder blockText = null;

            // The vertical group (class notes): the outermost open <align vstyle=center|bottom>, its first op, where its
            // flow began, how low it reached, whether it spans lines; each op's top-down y in FlowY.
            int groupDepth = 0, groupStart = -1, groupV = 0;
            float groupTop = 0f, groupBottom = 0f;
            bool groupLines = false;
            Box groupBox = box;
            var flowY = new List<float>();

            void Close()
            {
                if (block != null)
                {
                    block.Text = blockText.ToString();
                    block = null;
                    blockText = null;
                }
            }

            void Emit(EtsPmlOp op, float fy, float fh)
            {
                ops.Add(op);
                flowY.Add(fy);

                if (groupStart >= 0)
                {
                    groupBottom = Math.Max(groupBottom, fy + fh);
                }
            }

            void EndGroup()
            {
                if (groupStart >= 0 && groupLines)
                {
                    float height = groupBottom - groupTop;
                    float top = groupV == 1 ? groupBox.Y0 + (groupBox.Y1 - groupBox.Y0 - height) / 2f : groupBox.Y1 - height;
                    float dy = top - groupTop;

                    for (int i = groupStart; i < ops.Count; i++)
                    {
                        EtsPmlOp op = ops[i];
                        op.Y = flowY[i] + dy;

                        if (op.IsText)
                        {
                            // Top-aligned in its own line now that the block is placed.
                            op.VAlign = 0;
                            op.H = Math.Max(1f, Math.Min(op.H, groupBox.Y1 - op.Y));
                        }
                    }
                }

                groupDepth = 0;
                groupStart = -1;
                groupLines = false;
            }

            int at = 0;

            while (at < text.Length)
            {
                if (text[at] != '<')
                {
                    int next = text.IndexOf('<', at);
                    if (next < 0) next = text.Length;
                    string run = text.Substring(at, next - at).Replace("\r", "").Replace("\n", "").Replace("\t", "");
                    at = next;

                    if (run.Trim().Length == 0 && block == null)
                    {
                        continue;
                    }

                    float lh = TextLine(face, fontY);
                    Box tb = hAlign == 0 && vAlign == 0 ? box : alignBox;
                    float flow = Math.Max(y, tb.Y0);

                    if (block == null)
                    {
                        run = run.TrimStart();
                        float bx0 = hAlign == 0 ? Math.Max(x, tb.X0) : tb.X0;
                        float by0 = vAlign == 0 ? flow : tb.Y0;
                        block = new EtsPmlOp
                        {
                            IsText = true, X = bx0, Y = by0, W = tb.X1 - bx0, H = tb.Y1 - by0, HAlign = hAlign, VAlign = vAlign,
                            Argb = tint, FontPath = face, XScale = fontX, YScale = fontY, Wrap = wrap
                        };
                        blockText = new StringBuilder();
                        Emit(block, flow, lh);
                    }
                    else if (groupStart >= 0)
                    {
                        // A later line of the same block (after a <br>) reaches lower.
                        groupBottom = Math.Max(groupBottom, flow + lh);
                    }

                    if (run.Trim().Length > 0)
                    {
                        lineH = Math.Max(lineH, lh);
                    }

                    blockText.Append(run);
                    continue;
                }

                int end = text.IndexOf('>', at);
                if (end < 0) break;
                string tag = text.Substring(at + 1, end - at - 1).Trim();
                at = end + 1;

                bool closing = tag.StartsWith("/", StringComparison.Ordinal);
                string name = ReadName(tag, closing ? 1 : 0, out int attrStart);
                Dictionary<string, string> attrs = closing ? null : ReadAttributes(tag, attrStart);

                if (closing)
                {
                    switch (name)
                    {
                        case "align":
                            Close();

                            if (groupStart >= 0 && --groupDepth <= 0)
                            {
                                EndGroup();
                            }

                            if (aligns.Count > 0)
                            {
                                AlignState s = aligns.Pop();
                                hAlign = s.H; vAlign = s.V; alignBox = s.Box;
                            }
                            else
                            {
                                hAlign = 0; vAlign = 0; alignBox = box;
                            }
                            break;
                        case "margin":
                            Close();
                            if (margins.Count > 0) box = margins.Pop();
                            alignBox = box;
                            break;
                        case "color":
                            if (colours.Count > 0) tint = colours.Pop();
                            if (block != null) blockText.Append(RichColour(tint));
                            break;
                        case "nowrap":
                            wrap = true;
                            break;
                    }

                    continue;
                }

                switch (name)
                {
                    case "ret":
                        Close();
                        EndGroup();
                        x = box.X0; y = box.Y0; lineH = 0f;
                        hAlign = 0; vAlign = 0; aligns.Clear(); alignBox = box;
                        break;

                    case "hbr":
                        Close();
                        x = box.X0;
                        break;

                    case "br":
                        y += lineH + Number(attrs, "offset", 0f);
                        x = box.X0;
                        lineH = 0f;
                        if (block != null) blockText.Append('\n');
                        if (groupStart >= 0) groupLines = true;
                        break;

                    case "offset":
                    {
                        float dx = Number(attrs, "hshift", 0f), dy = Number(attrs, "vshift", 0f);
                        x += dx;
                        y += dy;
                        if (block != null && dx != 0f) blockText.Append("<space=" + dx.ToString("0.##", CultureInfo.InvariantCulture) + ">");
                        break;
                    }

                    case "align":
                        Close();
                        aligns.Push(new AlignState { H = hAlign, V = vAlign, Box = alignBox });
                        if (attrs.TryGetValue("hstyle", out string hs)) hAlign = hs == "center" ? 1 : hs == "right" ? 2 : 0;
                        if (attrs.TryGetValue("vstyle", out string vs)) vAlign = vs == "center" ? 1 : vs == "bottom" ? 2 : 0;
                        alignBox = SubBox(box, attrs);
                        if (x < alignBox.X0) x = alignBox.X0;
                        if (y < alignBox.Y0) y = alignBox.Y0;

                        if (groupStart >= 0)
                        {
                            groupDepth++;
                        }
                        else if (vAlign != 0)
                        {
                            groupStart = ops.Count;
                            groupDepth = 1;
                            groupV = vAlign;
                            groupBox = alignBox;
                            groupTop = y;
                            groupBottom = y;
                            groupLines = false;
                        }

                        break;

                    case "margin":
                        Close();
                        margins.Push(box);
                        box = SubBox(box, attrs);
                        alignBox = box;
                        if (x < box.X0) x = box.X0;
                        break;

                    case "color":
                        if (attrs.TryGetValue("value", out string cv) && EtsLocale.TryParseColour(cv, out uint c))
                        {
                            colours.Push(tint);
                            tint = c;
                            if (block != null) blockText.Append(RichColour(tint));
                        }
                        break;

                    case "font":
                        if (attrs.TryGetValue("face", out string f) && f.Length > 0 && block == null) face = f;
                        fontX = Number(attrs, "xscale", 1f);
                        fontY = Number(attrs, "yscale", fontX);
                        if (attrs.TryGetValue("color", out string fc) && EtsLocale.TryParseColour(fc, out uint fcol))
                        {
                            colours.Push(tint);
                            tint = fcol;
                            if (block != null) blockText.Append(RichColour(tint));
                        }
                        break;

                    case "nowrap":
                        wrap = false;
                        if (block != null) block.Wrap = false;
                        break;

                    case "img":
                    {
                        Close();

                        if (!attrs.TryGetValue("src", out string src) || src.Length == 0)
                        {
                            break;
                        }

                        Vector2Int size = textureSize != null ? textureSize(src) : Vector2Int.zero;

                        if (size.x <= 0 || size.y <= 0)
                        {
                            // Missing (probe 1650: "missing ..."): nothing drawn and the cursor stays.
                            break;
                        }

                        float sl = Coord(attrs, "left", size.x, 0f), sr = Coord(attrs, "right", size.x, size.x);
                        float st = Coord(attrs, "top", size.y, 0f), sb = Coord(attrs, "bottom", size.y, size.y);
                        Box b = hAlign == 0 && vAlign == 0 ? box : alignBox;
                        float bw = b.X1, bh = b.Y1;

                        // No room left on the line: the image wraps to the next one (class notes).
                        if (x > b.X0 && x >= bw - 0.001f)
                        {
                            y += lineH; x = b.X0; lineH = 0f;
                        }

                        Size(attrs, sl, sr, st, sb, x, y, bw, bh, out float w, out float h, out bool tileX, out bool tileY);

                        if (x > b.X0 && x + w > bw + 0.5f)
                        {
                            y += lineH; x = b.X0; lineH = 0f;
                            Size(attrs, sl, sr, st, sb, x, y, bw, bh, out w, out h, out tileX, out tileY);
                        }

                        uint imageTint = tint;

                        if (attrs.TryGetValue("color", out string ic) && EtsLocale.TryParseColour(ic, out uint icol))
                        {
                            imageTint = icol;
                        }

                        float px = hAlign == 1 ? b.X0 + (b.X1 - b.X0 - w) / 2f : hAlign == 2 ? b.X1 - w : x;
                        float py = vAlign == 1 ? b.Y0 + (b.Y1 - b.Y0 - h) / 2f : vAlign == 2 ? b.Y1 - h : y;

                        int isl = (int)Math.Round(sl), isr = (int)Math.Round(sr), ist = (int)Math.Round(st), isb = (int)Math.Round(sb);

                        // Nothing to draw at zero size (class notes: the 0-XP bar); the cursor still moves as before.
                        if (w > 0f && h > 0f)
                        {
                            Emit(new EtsPmlOp
                            {
                                Material = src, SrcX = isl, SrcY = ist, SrcW = isr - isl, SrcH = isb - ist,
                                X = px, Y = py, W = w, H = h, Argb = imageTint, IsSdf = isSdf != null && isSdf(src),
                                TileX = tileX, TileY = tileY
                            }, y, h);
                        }

                        x += w;
                        lineH = Math.Max(lineH, h);
                        break;
                    }
                }
            }

            Close();
            EndGroup();
            return ops;
        }

        /// <summary>%0..%F replaced by the value's parts; a part not given is empty.</summary>
        public static string Substitute(string markup, string[] args)
        {
            if (markup.IndexOf('%') < 0)
            {
                return markup;
            }

            var built = new StringBuilder(markup.Length + 32);

            for (int i = 0; i < markup.Length; i++)
            {
                char c = markup[i];

                if (c == '%' && i + 1 < markup.Length)
                {
                    int index = Hex.IndexOf(markup[i + 1]);

                    if (index >= 0)
                    {
                        if (args != null && index < args.Length && args[index] != null) built.Append(args[index]);
                        i++;
                        continue;
                    }
                }

                built.Append(c);
            }

            return built.ToString();
        }

        /// <summary>
        /// A table template's cell markup (ui::text_table_template: leftmost_cell_pml, middle_cell_pml, rightmost_cell_pml)
        /// with its ${...} expressions worked out from the table's numbers - "${width}", "${height}",
        /// "${(height - v_cellspacing)/2 + v_cellspacing}" are the forms 1.61 writes (txt.table.singlecell and .interl,
        /// probe 1891) - and "%#", the row's colour, filled in. + - * / and parentheses; an unknown name is 0.
        /// </summary>
        public static string Expand(string markup, Func<string, float> variable, string rowColour)
        {
            if (string.IsNullOrEmpty(markup))
            {
                return markup ?? "";
            }

            var built = new StringBuilder(markup.Length + 16);
            int at = 0;

            while (at < markup.Length)
            {
                int open = markup.IndexOf("${", at, StringComparison.Ordinal);

                if (open < 0)
                {
                    built.Append(markup, at, markup.Length - at);
                    break;
                }

                int close = markup.IndexOf('}', open + 2);

                if (close < 0)
                {
                    built.Append(markup, at, markup.Length - at);
                    break;
                }

                built.Append(markup, at, open - at);
                string expression = markup.Substring(open + 2, close - open - 2);
                int pos = 0;
                float result = Sum(expression, ref pos, variable);
                built.Append(result.ToString("0.###", CultureInfo.InvariantCulture));
                at = close + 1;
            }

            return built.ToString().Replace("%#", rowColour ?? "");
        }

        private static float Sum(string s, ref int pos, Func<string, float> variable)
        {
            float value = Product(s, ref pos, variable);

            while (true)
            {
                Skip(s, ref pos);
                if (pos >= s.Length || (s[pos] != '+' && s[pos] != '-')) return value;
                char op = s[pos++];
                float right = Product(s, ref pos, variable);
                value = op == '+' ? value + right : value - right;
            }
        }

        private static float Product(string s, ref int pos, Func<string, float> variable)
        {
            float value = Atom(s, ref pos, variable);

            while (true)
            {
                Skip(s, ref pos);
                if (pos >= s.Length || (s[pos] != '*' && s[pos] != '/')) return value;
                char op = s[pos++];
                float right = Atom(s, ref pos, variable);
                value = op == '*' ? value * right : right != 0f ? value / right : 0f;
            }
        }

        private static float Atom(string s, ref int pos, Func<string, float> variable)
        {
            Skip(s, ref pos);

            if (pos >= s.Length)
            {
                return 0f;
            }

            if (s[pos] == '(')
            {
                pos++;
                float inner = Sum(s, ref pos, variable);
                Skip(s, ref pos);
                if (pos < s.Length && s[pos] == ')') pos++;
                return inner;
            }

            if (s[pos] == '-')
            {
                pos++;
                return -Atom(s, ref pos, variable);
            }

            int start = pos;

            if (char.IsDigit(s[pos]) || s[pos] == '.')
            {
                while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) pos++;
                return Parse(s.Substring(start, pos - start));
            }

            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
            return pos > start && variable != null ? variable(s.Substring(start, pos - start)) : 0f;
        }

        private static void Skip(string s, ref int pos)
        {
            while (pos < s.Length && s[pos] == ' ') pos++;
        }

        /// <summary>The value of an element split into the %n parts, as the templates take it ("a|b|c").</summary>
        public static string[] Arguments(string value)
        {
            return string.IsNullOrEmpty(value) ? new string[0] : value.Split('|');
        }

        /// <summary>
        /// An image's destination size at the cursor (x, y) in a box ending at (bw, bh): the source size, unless
        /// width/height say otherwise (negative = the room left minus |n|) or xscale/yscale stretch or repeat
        /// to the box edge. The order of the arithmetic is probe 1650's, so the pixels match it exactly.
        /// </summary>
        private static void Size(Dictionary<string, string> attrs, float sl, float sr, float st, float sb, float x, float y,
                                 float bw, float bh, out float w, out float h, out bool tileX, out bool tileY)
        {
            w = Math.Abs(sr - sl);
            h = Math.Abs(sb - st);
            tileX = tileY = false;

            if (attrs.TryGetValue("width", out string ws)) { float v = Parse(ws); w = v < 0 ? (bw - x) + v : v; }
            if (attrs.TryGetValue("height", out string hs)) { float v = Parse(hs); h = v < 0 ? (bh - y) + v : v; }

            if (attrs.TryGetValue("xscale", out string xs))
            {
                if (xs.StartsWith("stretch", StringComparison.Ordinal)) w = bw - x;
                else if (xs == "repeat") { w = bw - x; tileX = true; }
            }

            if (attrs.TryGetValue("yscale", out string ys))
            {
                if (ys.StartsWith("stretch", StringComparison.Ordinal)) h = bh - y;
                else if (ys == "repeat") { h = bh - y; tileY = true; }
            }
        }

        /// <summary>A tag's attribute: "pN" is N texture pixels, a bare number a fraction of the texture size.</summary>
        private static float Coord(Dictionary<string, string> attrs, string key, int size, float fallback)
        {
            if (!attrs.TryGetValue(key, out string v) || v.Length == 0)
            {
                return fallback;
            }

            if (v[0] == 'p')
            {
                return Parse(v.Substring(1));
            }

            return Parse(v) * size;
        }

        /// <summary>
        /// A sub-box of margin or align: left/top from the near edge, right/bottom from the far edge when
        /// negative (the "width=-16" convention) or from the near edge when positive. Unverified (class notes).
        /// </summary>
        private static Box SubBox(Box outer, Dictionary<string, string> attrs)
        {
            Box b = outer;
            if (attrs.TryGetValue("left", out string l)) { float v = Parse(l); b.X0 = v >= 0 ? outer.X0 + v : outer.X1 + v; }
            if (attrs.TryGetValue("right", out string r)) { float v = Parse(r); b.X1 = v <= 0 ? outer.X1 + v : outer.X0 + v; }
            if (attrs.TryGetValue("top", out string t)) { float v = Parse(t); b.Y0 = v >= 0 ? outer.Y0 + v : outer.Y1 + v; }
            if (attrs.TryGetValue("bottom", out string bo)) { float v = Parse(bo); b.Y1 = v <= 0 ? outer.Y1 + v : outer.Y0 + v; }
            if (b.X1 < b.X0) b.X1 = b.X0;
            if (b.Y1 < b.Y0) b.Y1 = b.Y0;
            return b;
        }

        private static string RichColour(uint argb)
        {
            uint rgba = (argb << 8) | (argb >> 24);
            return "<color=#" + rgba.ToString("X8", CultureInfo.InvariantCulture) + ">";
        }

        private static string ReadName(string tag, int start, out int end)
        {
            int i = start;
            while (i < tag.Length && (char.IsLetterOrDigit(tag[i]) || tag[i] == '_')) i++;
            end = i;
            return tag.Substring(start, i - start);
        }

        private static Dictionary<string, string> ReadAttributes(string tag, int start)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = start;

            while (i < tag.Length)
            {
                while (i < tag.Length && tag[i] == ' ') i++;
                int keyStart = i;
                while (i < tag.Length && tag[i] != '=' && tag[i] != ' ') i++;
                string key = tag.Substring(keyStart, i - keyStart);

                if (i < tag.Length && tag[i] == '=')
                {
                    i++;
                    int valueStart = i;
                    while (i < tag.Length && tag[i] != ' ') i++;
                    string value = tag.Substring(valueStart, i - valueStart).Trim('"');
                    if (key.Length > 0) result[key] = value;
                }
                else if (key.Length > 0)
                {
                    result[key] = "";
                }
            }

            return result;
        }

        private static float Number(Dictionary<string, string> attrs, string key, float fallback)
        {
            return attrs != null && attrs.TryGetValue(key, out string v) && v.Length > 0 ? Parse(v) : fallback;
        }

        /// <summary>A number of the markup; 0 when it is not one - "NaN" and "Infinity" included, which float.TryParse
        /// accepts and which would reach a RectTransform (probe 1899's hostile markup; no game file writes them).</summary>
        private static float Parse(string v)
        {
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && !float.IsNaN(f) && !float.IsInfinity(f) ? f : 0f;
        }
    }

    /// <summary>One cell of a table background (EtsUiLayout.TableCells): its expanded markup and its box, y down.</summary>
    public sealed class EtsUiCell
    {
        public string Markup;
        public float X, Y, W, H;
    }

    /// <summary>How a layout element is drawn (EtsUiLayout.KindOf).</summary>
    public enum EtsUiKind
    {
        Container,
        Template,
        Markup,
        Fill,
        Portal,
        Engine
    }

    /// <summary>
    /// One placed element of a game screen (a /ui/*.sii unit with a rectangle), as the layout file gives it: the
    /// unit's qualified type and name (EtsSii reads "ui::text_common : _nameless._.loading.bar.upper"), its look
    /// template, value and markup, and its rectangle in the 1440 x 900 frame, y up from the bottom
    /// (EtsGameUi.Place's convention).
    /// </summary>
    public sealed class EtsUiElement
    {
        public string Type, Name, Look, Value, Text;
        public float Left, Right, Bottom, Top;
        public EtsSiiUnit Unit;

        /// <summary>
        /// The unit's "layer": the game draws a screen's elements by it, low first, file order among equals - read off
        /// /ui/loading.sii, whose orange loading.bar.upper (layer 4) is written BEFORE its grey loading.bar.bottom
        /// (layer 3) yet must lie on it, and whose rounded screenshot.frame corners (2) cover the picture (1); the
        /// message box's full-screen obscurer is 0 under its window (1), buttons (2), title (3) and close button (4).
        /// Not stated anywhere in the files; the reading every layout listed by probe 1891 is consistent with.
        /// </summary>
        public int Layer;

        /// <summary>The parent unit's name (my_parent); "" at the root. A hidden element hides its children.</summary>
        public string Parent = "";

        /// <summary>Position in the file among the placed elements (0 first).</summary>
        public int Order;

        /// <summary>How the element is drawn (EtsUiLayout.KindOf).</summary>
        public EtsUiKind Kind;

        /// <summary>The name without its "_nameless._." prefix ("loading.bar.upper"); the whole name when there is none.</summary>
        public string Key
        {
            get
            {
                if (string.IsNullOrEmpty(Name)) return "";
                int at = Name.LastIndexOf("._.", StringComparison.Ordinal);
                return at >= 0 ? Name.Substring(at + 3) : Name;
            }
        }

        public float Width => Right - Left;
        public float Height => Top - Bottom;

        public override string ToString()
        {
            return Key + " [" + Look + "] " + Left.ToString("0", CultureInfo.InvariantCulture) + ".." + Right.ToString("0", CultureInfo.InvariantCulture) +
                   " x " + Bottom.ToString("0", CultureInfo.InvariantCulture) + ".." + Top.ToString("0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The game's screen layouts and look templates, read for the new screens (owner D; [gameui §3], PLAN.md §7.D).
    /// Unity-free: probe 1885 reads the layouts the HQ stage, the loading screen and the tablet pages are built
    /// from and checks their rectangles against the design's tables.
    ///
    /// Read with the qualified unit types (EtsSii.Parse), so every element keeps its name and a caller finds
    /// "loading.bar.upper" by name rather than by position in the file - which is also why a template can be
    /// found at all: /ui/template/text.sii (332) and button.sii (346), plus the ETS-only text.ets.sii (3) and
    /// button.ets.sii (1), are looked up by unit name (Template).
    /// </summary>
    public static class EtsUiLayout
    {
        private static readonly string[] TemplateFiles =
        {
            "/ui/template/text.sii", "/ui/template/text.ets.sii", "/ui/template/button.sii", "/ui/template/button.ets.sii"
        };

        private static EtsFileSystem _templatesFrom;
        private static readonly List<EtsSiiFile> Templates = new List<EtsSiiFile>();

        /// <summary>
        /// The four template files parsed (pure; EtsFlow runs it on a worker thread in the HQ void so the first screen
        /// built does not parse 682 templates on its frame - Phase 2, owner D). AdoptTemplates installs them.
        ///
        /// Parsed here, not through EtsFileSystem.ReadSii: ReadSii keeps its parsed files in a plain Dictionary
        /// (EtsFileSystem._siiCache) with no lock, and the main thread calls ReadSii in the same seconds after the
        /// archives mount (EtsProfileStore's XP table, EtsMapViewer.Open's climate, EtsUiMapFrame.Read's
        /// /def/map_data.sii for the HQ map, the loading screen's pictures). Two threads writing one Dictionary can corrupt its buckets, and a corrupted bucket chain can loop
        /// for ever inside TryGetValue (Phase 2 check of D). What remains here is what ReadSii does minus its cache:
        /// ReadText, whose one archive stream read is locked (EtsHashFs.ReadExactly), and EtsSii.Parse, which keeps no
        /// state between calls.
        /// </summary>
        public static List<EtsSiiFile> ReadTemplates(EtsFileSystem files)
        {
            var read = new List<EtsSiiFile>();

            if (files == null)
            {
                return read;
            }

            foreach (string path in TemplateFiles)
            {
                string p = "/" + EtsHashFs.Normalize(path);
                string text = files.ReadText(p);
                EtsSiiFile file = text == null ? null : EtsSii.Parse(text, p, files.ReadText);
                if (file != null) read.Add(file);
            }

            return read;
        }

        /// <summary>Main thread: the templates of an install, read by ReadTemplates; ignored when that install's are already in.</summary>
        public static void AdoptTemplates(EtsFileSystem files, List<EtsSiiFile> read)
        {
            if (files == null || read == null || ReferenceEquals(files, _templatesFrom))
            {
                return;
            }

            Templates.Clear();
            Templates.AddRange(read);
            _templatesFrom = files;
        }

        /// <summary>This install's templates are in (Template answers without reading).</summary>
        public static bool TemplatesReady(EtsFileSystem files)
        {
            return files != null && ReferenceEquals(files, _templatesFrom);
        }

        /// <summary>
        /// Every unit of a layout that carries a rectangle (coords_l/r/t/b), in file order. Empty when the file is
        /// missing. The rectangle is normalised so Bottom &lt;= Top.
        /// </summary>
        public static List<EtsUiElement> Read(EtsFileSystem files, string uiPath)
        {
            var elements = new List<EtsUiElement>();
            EtsSiiFile screen = files != null ? files.ReadSii(uiPath) : null;

            if (screen == null)
            {
                return elements;
            }

            foreach (EtsSiiUnit unit in screen.Units)
            {
                if (!unit.Has("coords_l") || !unit.Has("coords_t"))
                {
                    continue;
                }

                float t = unit.GetFloat("coords_t", 0f), b = unit.GetFloat("coords_b", 0f);
                string parent = unit.GetString("my_parent", "") ?? "";
                var element = new EtsUiElement
                {
                    Type = unit.Type, Name = unit.Name ?? "", Look = unit.GetString("look_template", "") ?? "",
                    Value = unit.GetString("value", "") ?? "", Text = unit.GetString("text", "") ?? "",
                    Left = unit.GetFloat("coords_l", 0f), Right = unit.GetFloat("coords_r", 0f),
                    Bottom = Math.Min(t, b), Top = Math.Max(t, b), Unit = unit,
                    Layer = unit.GetInt("layer", 0), Parent = parent == "null" ? "" : parent, Order = elements.Count
                };

                element.Kind = KindOf(element);
                elements.Add(element);
            }

            return elements;
        }

        /// <summary>
        /// How an element is drawn: a look template with the element's value (Template); its own markup, a ui::text
        /// (Markup); a plain fill, ui_fullscreen with its color (Fill); a child layout, ui::portal with child_path
        /// (Portal); something the engine draws that the files do not describe - ui_job_map, ui_truck_scene_base,
        /// ui_vehicle_preview, ui_desktop_menu_bar, ui::link_text, sliders, table rows (Engine); or a window or group
        /// that only holds others (Container). A look_template of "null" (ui::link_text's) is none.
        /// </summary>
        public static EtsUiKind KindOf(EtsUiElement e)
        {
            string type = e.Type ?? "";

            if (type == "ui::portal") return EtsUiKind.Portal;
            if (type == "ui_fullscreen") return EtsUiKind.Fill;
            if (type == "ui::window" || type == "ui::group" || type == "ui_blend_window") return EtsUiKind.Container;
            if (type == "ui::text" && e.Text.Length > 0) return EtsUiKind.Markup;
            if (e.Look.Length > 0 && e.Look != "null" && !e.Look.StartsWith("sld.", StringComparison.Ordinal) &&
                (type == "ui::text_common" || type == "ui::button_common" || type == "ui::button_row" || type == "ui::text_table_common" || type == "ui::overlay_point"))
            {
                return EtsUiKind.Template;
            }

            return type == "ui::text" ? EtsUiKind.Container : EtsUiKind.Engine;
        }

        /// <summary>
        /// A screen's elements in the order they are drawn (Layer, then file order), without the ones hidden by key
        /// and without anything under a hidden element (my_parent chains). The outermost window (the whole 1440 x 900
        /// frame, my_parent null) is left out: it is the frame itself.
        /// </summary>
        public static List<EtsUiElement> Plan(IList<EtsUiElement> elements, ICollection<string> hiddenKeys)
        {
            return Plan(elements, hiddenKeys, null);
        }

        /// <summary>
        /// As Plan, keeping only the elements <paramref name="keep"/> accepts (a screen's owner naming what this world
        /// has, where the file's elements are unnamed: quick_job.sii names 20 of its 68). An element dropped either way
        /// drops everything under it.
        /// </summary>
        public static List<EtsUiElement> Plan(IList<EtsUiElement> elements, ICollection<string> hiddenKeys, Func<EtsUiElement, bool> keep)
        {
            if (keep != null)
            {
                var dropped = new HashSet<string>(hiddenKeys ?? (ICollection<string>)Array.Empty<string>(), StringComparer.Ordinal);

                foreach (EtsUiElement e in elements)
                {
                    // The root window is the frame itself and never asked about.
                    if (e.Parent.Length > 0 && !keep(e)) dropped.Add(e.Key);
                }

                hiddenKeys = dropped;
            }

            var byName = new Dictionary<string, EtsUiElement>(StringComparer.Ordinal);

            foreach (EtsUiElement e in elements)
            {
                if (!string.IsNullOrEmpty(e.Name)) byName[e.Name] = e;
            }

            var plan = new List<EtsUiElement>();

            foreach (EtsUiElement e in elements)
            {
                if (e.Parent.Length == 0 && e.Kind == EtsUiKind.Container)
                {
                    continue;
                }

                bool hidden = false;
                EtsUiElement at = e;

                for (int guard = 0; at != null && guard < 32 && !hidden; guard++)
                {
                    hidden = hiddenKeys != null && hiddenKeys.Contains(at.Key);
                    at = at.Parent.Length > 0 && byName.TryGetValue(at.Parent, out EtsUiElement up) ? up : null;
                }

                if (!hidden) plan.Add(e);
            }

            plan.Sort((a, b) => a.Layer != b.Layer ? a.Layer.CompareTo(b.Layer) : a.Order.CompareTo(b.Order));
            return plan;
        }

        /// <summary>
        /// The cells of a table background (a ui::text_table_common with a ui::text_table_template look:
        /// txt.table.singlecell, .interl): rows of row_height every row_height + v_cellspacing down the element, columns
        /// of col_widths (one of the element's width when none), each cell the template's leftmost / middle / rightmost
        /// markup expanded (EtsPml.Expand) with its width, height and the row's colour - the element's |-separated
        /// value, cycled ("@@clr_table_bg_light@@|@@clr_table_bg_dark@@"). Cell X/Y are in the element's box, y down.
        /// The row count (whole rows that fit) is a reading; the engine fills tables by their content.
        /// </summary>
        public static List<EtsUiCell> TableCells(EtsFileSystem files, EtsUiElement e, string value)
        {
            var cells = new List<EtsUiCell>();
            EtsSiiUnit template = e != null ? Template(files, e.Look) : null;

            if (template == null || !template.Has("middle_cell_pml"))
            {
                return cells;
            }

            float rowHeight = Math.Max(1f, e.Unit.GetFloat("row_height", 30f)), vGap = e.Unit.GetFloat("v_cellspacing", 0f), hGap = e.Unit.GetFloat("h_cellspacing", 0f);
            var widths = new List<float>();

            foreach (string part in (e.Unit.GetString("col_widths", "") ?? "").Split('|'))
            {
                string p = part.Trim();
                if (p.Length > 0 && float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out float w) && w > 0f) widths.Add(w);
            }

            if (widths.Count == 0) widths.Add(e.Width);
            string[] colours = EtsPml.Arguments(value);
            if (colours.Length == 0) colours = new[] { "FFFFFFFF" };
            int rows = Math.Max(1, (int)((e.Height + vGap) / (rowHeight + vGap)));

            for (int r = 0; r < rows; r++)
            {
                float x = 0f, y = r * (rowHeight + vGap);

                for (int c = 0; c < widths.Count; c++)
                {
                    string field = c == 0 ? "leftmost_cell_pml" : c == widths.Count - 1 ? "rightmost_cell_pml" : "middle_cell_pml";
                    string pml = template.GetString(field, null) ?? template.GetString("middle_cell_pml", "");
                    float width = widths[c];
                    string expanded = EtsPml.Expand(pml, name => name == "width" ? width : name == "height" ? rowHeight : name == "v_cellspacing" ? vGap : name == "h_cellspacing" ? hGap : 0f,
                                                    colours[r % colours.Length].Trim());
                    cells.Add(new EtsUiCell { Markup = expanded, X = x, Y = y, W = width, H = rowHeight });
                    x += width + hGap;
                }
            }

            return cells;
        }

        /// <summary>
        /// The markup an element's look draws in one state ("n" at rest, "s" pointed at, "p" pressed or chosen, "d"
        /// disabled): a text template's "text", a button's n_pml / s_pml / p_pml / d_pml; for a Markup element its own
        /// text. Null when the look or the state is not there.
        /// </summary>
        public static string MarkupOf(EtsFileSystem files, EtsUiElement e, string state)
        {
            if (e.Kind == EtsUiKind.Markup)
            {
                return state == "n" ? e.Text : null;
            }

            if (e.Kind != EtsUiKind.Template)
            {
                return null;
            }

            EtsSiiUnit template = Template(files, e.Look);

            if (template == null)
            {
                return null;
            }

            if (template.Has("n_pml"))
            {
                string pml = template.GetString(state + "_pml", null);
                return string.IsNullOrEmpty(pml) ? null : pml;
            }

            return state == "n" ? template.GetString("text", null) : null;
        }

        /// <summary>The first element whose name ends with the given key ("loading.bar.upper"), or null.</summary>
        public static EtsUiElement Find(IList<EtsUiElement> elements, string key)
        {
            if (elements == null || string.IsNullOrEmpty(key))
            {
                return null;
            }

            foreach (EtsUiElement e in elements)
            {
                if (e.Name != null && (e.Name == key || e.Name.EndsWith("." + key, StringComparison.Ordinal)))
                {
                    return e;
                }
            }

            return null;
        }

        /// <summary>Every element drawn with a look template, in file order.</summary>
        public static List<EtsUiElement> WithLook(IList<EtsUiElement> elements, string look)
        {
            var found = new List<EtsUiElement>();

            if (elements != null)
            {
                foreach (EtsUiElement e in elements)
                {
                    if (e.Look == look) found.Add(e);
                }
            }

            return found;
        }

        /// <summary>
        /// A look template by name ("btn.generic", "txt.window.bcg_rect4"): its unit, whose "text" (a text
        /// template) or "n_pml" / "s_pml" / "p_pml" / "d_pml" (a button template, with "width"/"height") is the
        /// markup EtsPml lays out. Null when no template file has it.
        /// </summary>
        public static EtsSiiUnit Template(EtsFileSystem files, string look)
        {
            if (files == null || string.IsNullOrEmpty(look))
            {
                return null;
            }

            if (!ReferenceEquals(files, _templatesFrom))
            {
                AdoptTemplates(files, ReadTemplates(files));
            }

            // Later files win, as the game's own .ets additions are meant to.
            for (int i = Templates.Count - 1; i >= 0; i--)
            {
                EtsSiiUnit unit = Templates[i].Find(look);
                if (unit != null) return unit;
            }

            return null;
        }
    }
}
