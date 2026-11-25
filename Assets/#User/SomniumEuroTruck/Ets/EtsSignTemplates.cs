using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The game's sign template definitions, read from /def/sign (2026-10-14, sign texts). Pure: no Unity call, so the
    /// map prefetch worker can read them.
    ///
    /// - /def/sign/templates*.sii: sign_template_def "sign_templ.&lt;name&gt;" -&gt; path of a sign_template: model_name (the
    ///   sign model token), boards[] (sign_template_board_binding: area_name = model locator, linked_board =
    ///   "sign_board.&lt;name&gt;"), items[] (what is drawn in the model's areas).
    /// - /def/sign/boards*.sii: sign_board_def -&gt; a sign_board_template: width, height (metres) and items[] laid out
    ///   on the board's own rectangle, which the binding centres in the area.
    /// - items: sign_template_text (text, font_face, font_size, text_halign/valign, font_color, font_opacity,
    ///   line_spacing, letter_spacing), sign_template_image (atlas_path, image_idx, color, opacity, hflip, vflip),
    ///   sign_template_frame (frame_path, color, opacity), sign_template_mileage (a text whose value is a mileage
    ///   target's name or distance). Each has id, area_name, coords_l/r/t/b in metres from the area's (board's)
    ///   bottom-left corner, z_order.
    /// - A model locator's hookup names the area: sign_editable_area "sign.420x225.reflective" in
    ///   /unit/hookup/sign_area.sii (width, height); the locator is the area's centre (sr_right_fr: ml3 at x 0.298,
    ///   y 3.011; its board piece spans x -0.99..1.58, y 2.59..3.43).
    /// - Colours are 0x00BBGGRR (the Belgian project palette: 13390859 = blue, 6859529 = green, 52479 = amber,
    ///   997514 = brown); "nil" is the font material's own colour, aux[1] = white in every /font/sign/*_0.mat.
    /// - Override attribute indices (all 58,517 overrides of the demo map, tallied by item kind, probe signs/tally):
    ///   text and mileage 0 text, 1 font_face, 2 font_size, 3 font_color, 4 font_opacity, 5 text_halign,
    ///   6 text_valign, 7 line_spacing, 8 letter_spacing; mileage 100 target, 101 display, 102 variant_idx,
    ///   103 show_units, 104 uppercase; image 0 atlas_path, 1 image_idx, 2 color, 3 opacity, 4 hflip, 5 vflip;
    ///   frame 0 color, 1 opacity (5,120 frame overrides are 4:1 opacity toggles, 50 are 3:0 colours).
    /// </summary>
    public enum EtsSignItemKind
    {
        Text,
        Image,
        Frame,
        Mileage
    }

    public sealed class EtsSignItem
    {
        public EtsSignItemKind Kind;
        public uint Id;
        public string Area = string.Empty;
        public float L, R, T, B;
        public int Z;

        public string Text = string.Empty;
        public string FontFace = string.Empty;
        public float FontSize = 0.5f;

        /// <summary>0 left, 1 centre, 2 right.</summary>
        public int HAlign = 1;

        /// <summary>0 top, 1 centre, 2 bottom.</summary>
        public int VAlign = 1;

        /// <summary>0x00BBGGRR; ColorNil = the face's (or image's) own colour.</summary>
        public uint Color = 0xFFFFFF;

        public bool ColorNil = true;
        public float Opacity = 1f;
        public float LineSpacing;
        public float LetterSpacing;

        public string Target = string.Empty;
        public string Display = "name";
        public int VariantIdx = -1;
        public bool ShowUnits;
        public bool Uppercase;

        public string AtlasPath = string.Empty;
        public int ImageIdx;
        public bool HFlip, VFlip;

        public string FramePath = string.Empty;

        /// <summary>The map item gave this item its text (a mileage item's name or distance as the editor computed it).</summary>
        public bool TextSet;

        public EtsSignItem Clone()
        {
            return (EtsSignItem)MemberwiseClone();
        }

        /// <summary>
        /// One map override attribute (index table in the class notes). Returns false for an index this world does not
        /// know, which leaves the template's value.
        /// </summary>
        public bool Apply(EtsMapSignAttribute a)
        {
            if (Kind == EtsSignItemKind.Image)
            {
                switch (a.Index)
                {
                    case 0: if (a.Text != null) AtlasPath = a.Text; return true;
                    case 1: ImageIdx = a.Type == 4 ? (int)a.Float : (int)a.Number; return true;
                    case 2: Color = a.Number; ColorNil = a.Number == 0xFFFFFFFF; return true;
                    case 3: Opacity = a.Type == 4 ? a.Float : a.Number; return true;
                    case 4: HFlip = a.Number != 0; return true;
                    case 5: VFlip = a.Number != 0; return true;
                }

                return false;
            }

            if (Kind == EtsSignItemKind.Frame)
            {
                switch (a.Index)
                {
                    case 0: Color = a.Number; ColorNil = a.Number == 0xFFFFFFFF; return true;
                    case 1: Opacity = a.Type == 4 ? a.Float : a.Number; return true;
                }

                return false;
            }

            switch (a.Index)
            {
                case 0: Text = a.Text ?? string.Empty; TextSet = true; return true;
                case 1: if (a.Text != null) FontFace = a.Text; return true;
                case 2: FontSize = a.Float; return true;
                case 3: Color = a.Number; ColorNil = a.Number == 0xFFFFFFFF; return true;
                case 4: Opacity = a.Float; return true;
                case 5: HAlign = a.Number <= 2 ? (int)a.Number : 1; return true;
                case 6: VAlign = a.Number <= 2 ? (int)a.Number : 1; return true;
                case 7: LineSpacing = a.Float; return true;
                case 8: LetterSpacing = a.Float; return true;
                case 100: Target = a.Token != 0 ? EtsToken.Decode(a.Token) : (a.Text ?? string.Empty); return true;
                case 101: Display = a.Number == 1 ? "distance" : a.Number == 0 ? "name" : Display; return true;
                case 102: VariantIdx = (int)a.Number; return true;
                case 103: ShowUnits = a.Number != 0; return true;
                case 104: Uppercase = a.Number != 0; return true;
            }

            return false;
        }
    }

    public sealed class EtsSignBoardDef
    {
        public string Name;
        public float Width, Height;
        public readonly List<EtsSignItem> Items = new List<EtsSignItem>();
    }

    public sealed class EtsSignTemplateDef
    {
        public string Name;
        public string ModelName;
        public readonly List<KeyValuePair<string, string>> Bindings = new List<KeyValuePair<string, string>>();
        public readonly List<EtsSignItem> Items = new List<EtsSignItem>();
    }

    public sealed class EtsSignAtlasDef
    {
        public string MaterialPath;

        /// <summary>Per image: left, right, bottom, top in texture pixels (the file's "left right bottom top").</summary>
        public readonly List<float[]> Coords = new List<float[]>();
    }

    public sealed class EtsSignFrameDef
    {
        public string Material;
        public float BorderWidth, BorderHeight;

        /// <summary>l, r, b, t in texture pixels.</summary>
        public float[] Corner = new float[4], Horizontal = new float[4], Vertical = new float[4], Center = new float[4];
    }

    public sealed class EtsMileageTarget
    {
        public string Name;
        public string DefaultName;
        public float DistanceOffset;
        public ulong NodeUid;
        public float X, Y, Z;
        public bool HasPosition;
    }

    /// <summary>The sign_overlay hookup a legacy board locator names (/unit/hookup/sign_*.sii).</summary>
    public struct EtsSignOverlayHookup
    {
        /// <summary>0 road, 1 city1, 2 city2.</summary>
        public int Type;

        public int Board;
        public float Scale;
    }

    public sealed class EtsSignLibrary
    {
        private readonly EtsFileSystem _files;
        private readonly object _lock = new object();
        private Dictionary<string, string> _templatePaths, _boardPaths;
        private readonly Dictionary<string, EtsSignTemplateDef> _templates = new Dictionary<string, EtsSignTemplateDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, EtsSignBoardDef> _boards = new Dictionary<string, EtsSignBoardDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, EtsSignAtlasDef> _atlases = new Dictionary<string, EtsSignAtlasDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, EtsSignFrameDef> _frames = new Dictionary<string, EtsSignFrameDef>(StringComparer.Ordinal);
        private Dictionary<string, float[]> _areas;
        private Dictionary<string, EtsSignOverlayHookup> _overlays;
        private Dictionary<string, EtsMileageTarget> _mileage;

        public EtsSignLibrary(EtsFileSystem files)
        {
            _files = files;
        }

        public EtsFileSystem Files => _files;

        public int TemplateCount { get { lock (_lock) { Index(); return _templatePaths.Count; } } }

        public int BoardCount { get { lock (_lock) { Index(); return _boardPaths.Count; } } }

        private void Index()
        {
            if (_templatePaths != null)
            {
                return;
            }

            _templatePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            _boardPaths = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (EtsSiiUnit unit in _files.ReadDefinitionFamily("/def/sign", "templates").Units)
            {
                string path = unit.GetString("path", null);
                if (!string.IsNullOrEmpty(path)) _templatePaths[unit.Name] = path;
            }

            foreach (EtsSiiUnit unit in _files.ReadDefinitionFamily("/def/sign", "boards").Units)
            {
                string path = unit.GetString("path", null);
                if (!string.IsNullOrEmpty(path)) _boardPaths[unit.Name] = path;
            }
        }

        /// <summary>The template a map sign names ("sign_templ.0700q"); null when the demo has no such file.</summary>
        public EtsSignTemplateDef Template(string name)
        {
            lock (_lock)
            {
                if (_templates.TryGetValue(name, out EtsSignTemplateDef cached)) return cached;
                Index();
                EtsSignTemplateDef def = null;

                if (_templatePaths.TryGetValue(name, out string path))
                {
                    EtsSiiFile file = _files.ReadSii(path);
                    EtsSiiUnit root = null;

                    if (file != null)
                    {
                        foreach (EtsSiiUnit u in file.OfType("sign_template")) { root = u; break; }
                    }

                    if (root != null)
                    {
                        def = new EtsSignTemplateDef { Name = name, ModelName = root.GetString("model_name", "") };

                        foreach (string bindingName in root.GetArray("boards"))
                        {
                            EtsSiiUnit binding = file.Find(bindingName);
                            if (binding == null) continue;
                            def.Bindings.Add(new KeyValuePair<string, string>(binding.GetString("area_name", ""), binding.GetString("linked_board", "")));
                        }

                        ReadItems(file, root, def.Items);
                    }
                }

                _templates[name] = def;
                return def;
            }
        }

        /// <summary>A board, "sign_board.0700s"; null when missing.</summary>
        public EtsSignBoardDef Board(string name)
        {
            lock (_lock)
            {
                if (_boards.TryGetValue(name, out EtsSignBoardDef cached)) return cached;
                Index();
                EtsSignBoardDef def = null;

                if (_boardPaths.TryGetValue(name, out string path))
                {
                    EtsSiiFile file = _files.ReadSii(path);
                    EtsSiiUnit root = null;

                    if (file != null)
                    {
                        foreach (EtsSiiUnit u in file.OfType("sign_board_template")) { root = u; break; }
                    }

                    if (root != null)
                    {
                        def = new EtsSignBoardDef { Name = name, Width = root.GetFloat("width", 0f), Height = root.GetFloat("height", 0f) };
                        ReadItems(file, root, def.Items);
                    }
                }

                _boards[name] = def;
                return def;
            }
        }

        private static void ReadItems(EtsSiiFile file, EtsSiiUnit root, List<EtsSignItem> into)
        {
            foreach (string itemName in root.GetArray("items"))
            {
                EtsSiiUnit u = file.Find(itemName);
                if (u == null) continue;
                EtsSignItem item = ReadItem(u);
                if (item != null) into.Add(item);
            }
        }

        private static EtsSignItem ReadItem(EtsSiiUnit u)
        {
            var item = new EtsSignItem();

            switch (u.Type)
            {
                case "sign_template_text": item.Kind = EtsSignItemKind.Text; break;
                case "sign_template_mileage": item.Kind = EtsSignItemKind.Mileage; break;
                case "sign_template_image": item.Kind = EtsSignItemKind.Image; break;
                case "sign_template_frame": item.Kind = EtsSignItemKind.Frame; break;
                default: return null;
            }

            EtsSiiValue id = u.Get("id");
            item.Id = id != null && uint.TryParse(id.Raw, out uint v) ? v : 0;
            item.Area = u.GetString("area_name", "") ?? "";
            item.L = u.GetFloat("coords_l", 0f);
            item.R = u.GetFloat("coords_r", 0f);
            item.T = u.GetFloat("coords_t", 0f);
            item.B = u.GetFloat("coords_b", 0f);
            item.Z = u.GetInt("z_order", 0);

            if (item.Kind == EtsSignItemKind.Text || item.Kind == EtsSignItemKind.Mileage)
            {
                item.Text = u.GetString("text", "") ?? "";
                item.FontFace = u.GetString("font_face", "") ?? "";
                item.FontSize = u.GetFloat("font_size", 0.5f);
                item.HAlign = Align(u.GetString("text_halign", "center"), "left", "right");
                item.VAlign = Align(u.GetString("text_valign", "center"), "top", "bottom");
                ReadColor(u, "font_color", item);
                item.Opacity = u.GetFloat("font_opacity", 1f);
                item.LineSpacing = u.GetFloat("line_spacing", 0f);
                item.LetterSpacing = u.GetFloat("letter_spacing", 0f);

                if (item.Kind == EtsSignItemKind.Mileage)
                {
                    item.Target = u.GetString("target", "") ?? "";
                    item.Display = u.GetString("display", "name") ?? "name";
                    string variant = u.GetString("variant_idx", "nil");
                    item.VariantIdx = variant != null && int.TryParse(variant, out int vi) ? vi : -1;
                    item.ShowUnits = u.GetBool("show_units", false);
                    item.Uppercase = u.GetBool("uppercase", false);
                }
            }
            else if (item.Kind == EtsSignItemKind.Image)
            {
                item.AtlasPath = u.GetString("atlas_path", "") ?? "";
                item.ImageIdx = u.GetInt("image_idx", 0);
                ReadColor(u, "color", item);
                item.Opacity = u.GetFloat("opacity", 1f);
                item.HFlip = u.GetBool("hflip", false);
                item.VFlip = u.GetBool("vflip", false);
            }
            else
            {
                item.FramePath = u.GetString("frame_path", "") ?? "";
                ReadColor(u, "color", item);
                item.Opacity = u.GetFloat("opacity", 1f);
            }

            return item;
        }

        private static int Align(string value, string low, string high)
        {
            return value == low ? 0 : value == high ? 2 : 1;
        }

        private static void ReadColor(EtsSiiUnit u, string key, EtsSignItem item)
        {
            EtsSiiValue v = u.Get(key);

            if (v == null || v.Raw == "nil" || !uint.TryParse(v.Raw, out uint c))
            {
                item.ColorNil = true;
                return;
            }

            item.ColorNil = false;
            item.Color = c;
        }

        /// <summary>An image atlas, /def/sign/atlas/*.sii.</summary>
        public EtsSignAtlasDef Atlas(string path)
        {
            lock (_lock)
            {
                if (_atlases.TryGetValue(path, out EtsSignAtlasDef cached)) return cached;
                EtsSignAtlasDef def = null;
                EtsSiiFile file = string.IsNullOrEmpty(path) ? null : _files.ReadSii(path);

                if (file != null)
                {
                    foreach (EtsSiiUnit u in file.OfType("image_atlas"))
                    {
                        def = new EtsSignAtlasDef { MaterialPath = u.GetString("material_path", "") };
                        EtsSiiValue coords = u.Get("images_coords");

                        if (coords != null)
                        {
                            for (int i = 0; i < coords.Count; i++)
                            {
                                float[] f = EtsSiiValue.ParseFloats(coords[i]);
                                if (f.Length >= 4) def.Coords.Add(f);
                            }
                        }

                        break;
                    }
                }

                _atlases[path] = def;
                return def;
            }
        }

        /// <summary>A nine-slice frame, /def/sign/frame/*.sii.</summary>
        public EtsSignFrameDef Frame(string path)
        {
            lock (_lock)
            {
                if (_frames.TryGetValue(path, out EtsSignFrameDef cached)) return cached;
                EtsSignFrameDef def = null;
                EtsSiiFile file = string.IsNullOrEmpty(path) ? null : _files.ReadSii(path);

                if (file != null)
                {
                    foreach (EtsSiiUnit u in file.OfType("sign_frame_template"))
                    {
                        def = new EtsSignFrameDef
                        {
                            Material = u.GetString("material", ""),
                            BorderWidth = u.GetFloat("border_width", 0.1f),
                            BorderHeight = u.GetFloat("border_height", 0.1f)
                        };
                        Quad(u, "corner", def.Corner);
                        Quad(u, "horizontal", def.Horizontal);
                        Quad(u, "vertical", def.Vertical);
                        Quad(u, "center", def.Center);
                        break;
                    }
                }

                _frames[path] = def;
                return def;
            }
        }

        private static void Quad(EtsSiiUnit u, string name, float[] into)
        {
            into[0] = u.GetFloat(name + "_coords_l", 0f);
            into[1] = u.GetFloat(name + "_coords_r", 0f);
            into[2] = u.GetFloat(name + "_coords_b", 0f);
            into[3] = u.GetFloat(name + "_coords_t", 0f);
        }

        /// <summary>A sign_editable_area hookup's size in metres ("sign.420x225.reflective"); null when not an area.</summary>
        public float[] Area(string hookup)
        {
            lock (_lock)
            {
                if (_areas == null)
                {
                    _areas = new Dictionary<string, float[]>(StringComparer.Ordinal);
                    EtsSiiFile file = _files.ReadSii("/unit/hookup/sign_area.sii");

                    if (file != null)
                    {
                        foreach (EtsSiiUnit u in file.OfType("sign_editable_area"))
                        {
                            _areas[u.Name] = new[] { u.GetFloat("width", 0f), u.GetFloat("height", 0f) };
                        }
                    }
                }

                return hookup != null && _areas.TryGetValue(hookup, out float[] size) ? size : null;
            }
        }

        /// <summary>A legacy board's overlay hookup ("sign.city1_0", "sign.road_1s"); false when the name is not one.</summary>
        public bool Overlay(string hookup, out EtsSignOverlayHookup overlay)
        {
            lock (_lock)
            {
                if (_overlays == null)
                {
                    _overlays = new Dictionary<string, EtsSignOverlayHookup>(StringComparer.Ordinal);

                    if (_files.TryList("/unit/hookup", out List<string> files, out _))
                    {
                        foreach (string f in files)
                        {
                            if (!f.StartsWith("sign_", StringComparison.Ordinal) || !f.EndsWith(".sii", StringComparison.Ordinal)) continue;
                            EtsSiiFile file = _files.ReadSii("/unit/hookup/" + f);
                            if (file == null) continue;

                            foreach (EtsSiiUnit u in file.OfType("sign_overlay"))
                            {
                                string type = u.GetString("model_type", "");
                                _overlays[u.Name] = new EtsSignOverlayHookup
                                {
                                    Type = type == "road" ? 0 : type == "city1" ? 1 : type == "city2" ? 2 : -1,
                                    Board = u.GetInt("board_index", 0),
                                    Scale = u.GetFloat("scale", 1f)
                                };
                            }
                        }
                    }
                }

                if (hookup != null && _overlays.TryGetValue(hookup, out overlay) && overlay.Type >= 0) return true;
                overlay = default;
                return false;
            }
        }

        public int OverlayHookupCount { get { lock (_lock) { return _overlays != null ? _overlays.Count : 0; } } }

        /// <summary>A mileage target, "mileage.uk_dover" (from /def/sign/mileage_targets*.sii).</summary>
        public EtsMileageTarget Mileage(string name)
        {
            lock (_lock)
            {
                if (_mileage == null)
                {
                    _mileage = new Dictionary<string, EtsMileageTarget>(StringComparer.Ordinal);

                    foreach (EtsSiiUnit u in _files.ReadDefinitionFamily("/def/sign", "mileage_targets").Units)
                    {
                        if (u.Type != "mileage_target") continue;
                        float[] p = u.GetFloats("position");
                        string node = u.GetString("node_uid", "nil");
                        var t = new EtsMileageTarget
                        {
                            Name = u.Name,
                            DefaultName = u.GetString("default_name", ""),
                            DistanceOffset = u.GetFloat("distance_offset", 0f),
                            NodeUid = node != null && ulong.TryParse(node, out ulong n) ? n : 0
                        };

                        if (p != null && p.Length >= 3 && Math.Abs(p[0]) < 1e30f)
                        {
                            t.X = p[0];
                            t.Y = p[1];
                            t.Z = p[2];
                            t.HasPosition = true;
                        }

                        _mileage[u.Name] = t;
                    }
                }

                if (string.IsNullOrEmpty(name)) return null;
                if (_mileage.TryGetValue(name, out EtsMileageTarget target)) return target;
                return _mileage.TryGetValue("mileage." + name, out target) ? target : null;
            }
        }
    }
}
