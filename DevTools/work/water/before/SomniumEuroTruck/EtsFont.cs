using System;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The game's own typefaces, loaded from the player's installation into TextMeshPro.
    ///
    /// /font/normal.font, big.font, title.font and small.font are distance-field fonts - the same
    /// idea TextMeshPro renders with - described by a text file over a set of atlases (see
    /// EtsFontFile). On 1.48 three of the four shared one atlas, /font/normal_0; on 1.61 each of the
    /// eight faces the templates name (normal, big, title, small, each also _bold) has its own seven
    /// atlases, and every ASCII glyph plus €, ü, ö, è, ń and é sits on its atlas 0 (probe 1652,
    /// [gameui §2.5]), so Get(path) serves every face a template's face= names. The atlas is a multi-channel field
    /// (the material effect is ui.font.msdf) and TextMeshPro reads a single channel, so the median
    /// of the three channels is taken per pixel, which is exactly the distance the three encode,
    /// and uploaded as an alpha texture.
    ///
    /// Glyph rectangles in the file are the ink alone; the field spreads one padding beyond them,
    /// and it was checked that growing every rectangle by that padding overlaps nothing, so that
    /// is what is registered with TextMeshPro, which expects the padding inside the rectangle.
    /// Only the first atlas is loaded - every ASCII glyph is in it - and TextMeshPro's default
    /// face is set as the fallback for anything else.
    ///
    /// The face's metrics are in atlas pixels, with the "em" taken as the file's vert_span, the
    /// distance from the lowest to the highest pixel any two glyphs reach. Size() gives the size
    /// the game draws each face at in its 1440 x 900 frame, so a label sized with it matches.
    /// </summary>
    public static class EtsFont
    {
        public const string Normal = "/font/normal.font";
        public const string Big = "/font/big.font";
        public const string Title = "/font/title.font";
        public const string Small = "/font/small.font";

        private static readonly Dictionary<string, TMP_FontAsset> ByPath = new Dictionary<string, TMP_FontAsset>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> SizeByPath = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The face, built on first use; null when the files cannot be read.</summary>
        public static TMP_FontAsset Get(string fontPath)
        {
            if (string.IsNullOrEmpty(fontPath) || Failed.Contains(fontPath))
            {
                return null;
            }

            if (ByPath.TryGetValue(fontPath, out TMP_FontAsset cached) && cached != null)
            {
                return cached;
            }

            TMP_FontAsset built = null;

            try
            {
                built = Build(fontPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] font " + fontPath + ": " + e.Message);
            }

            if (built == null)
            {
                Failed.Add(fontPath);
                return null;
            }

            ByPath[fontPath] = built;
            return built;
        }

        /// <summary>
        /// The size the game draws the face at, in the 1440 x 900 frame the layouts use: the
        /// file's line span at its reference 1024 x 768, scaled to 900 tall and by the face's
        /// default scale. Normal comes out at about 19, which is the 20-pixel row the advisor
        /// lays its lines on.
        /// </summary>
        public static float Size(string fontPath)
        {
            if (SizeByPath.TryGetValue(fontPath, out float size))
            {
                return size;
            }

            Get(fontPath);
            return SizeByPath.TryGetValue(fontPath, out size) ? size : 19f;
        }

        /// <summary>Puts the face on a label, at the game's size unless a size is given.</summary>
        public static void Apply(TMP_Text label, string fontPath, float size = 0f)
        {
            TMP_FontAsset font = Get(fontPath);

            if (font != null)
            {
                label.font = font;
            }

            label.fontSize = size > 0f ? size : Size(fontPath);
        }

        /// <summary>
        /// A face read and decoded off the main thread (Prepare): the parsed .font file and its atlas 0 as the
        /// single-channel field, rows already in Unity's order. Finish turns it into the TextMeshPro font.
        /// </summary>
        public sealed class Prepared
        {
            internal EtsFontFile File;
            internal byte[] Field;
            internal int Width, Height;
            internal string FieldName;
        }

        private static readonly Dictionary<string, Prepared> PreparedByPath = new Dictionary<string, Prepared>(StringComparer.Ordinal);

        /// <summary>
        /// The pure half of building a face - archive reads, the atlas decode and the per-pixel median - with no Unity
        /// call in it, so EtsFlow runs it on an EtsWorker thread in the HQ void (Phase 2, owner D). Measured in the editor
        /// (probe 2045): 210-230 ms per face for the eight 1.61 faces when done on the frame, which is what a tablet page's
        /// first build paid ("~209 ms cold", E's Phase 1 measure). Null when the face cannot be read.
        /// </summary>
        public static Prepared Prepare(EtsFileSystem files, string fontPath)
        {
            if (files == null || string.IsNullOrEmpty(fontPath))
            {
                return null;
            }

            string text = files.ReadText(fontPath);

            if (text == null)
            {
                return null;
            }

            EtsFontFile file = EtsFontFile.Parse(text, fontPath);

            if (file.Atlases.Length == 0 || file.Glyphs.Count == 0)
            {
                return null;
            }

            byte[] field = ReadField(files, file.Atlases[0].MaterialPath, out int width, out int height, out string name);
            return field == null ? null : new Prepared { File = file, Field = field, Width = width, Height = height, FieldName = name };
        }

        /// <summary>Main thread: a face prepared off it (Prepare), kept until Get builds the font from it in a few milliseconds.</summary>
        public static void Adopt(string fontPath, Prepared prepared)
        {
            if (prepared != null && !string.IsNullOrEmpty(fontPath) && !ByPath.ContainsKey(fontPath))
            {
                PreparedByPath[fontPath] = prepared;
            }
        }

        /// <summary>The face is built, or prepared so that Get builds it cheaply, or known unreadable.</summary>
        public static bool IsReady(string fontPath)
        {
            return !string.IsNullOrEmpty(fontPath) && (ByPath.ContainsKey(fontPath) || PreparedByPath.ContainsKey(fontPath) || Failed.Contains(fontPath));
        }

        private static TMP_FontAsset Build(string fontPath)
        {
            if (EtsEngine.Files == null)
            {
                return null;
            }

            if (!PreparedByPath.TryGetValue(fontPath, out Prepared prepared))
            {
                prepared = Prepare(EtsEngine.Files, fontPath);
            }

            PreparedByPath.Remove(fontPath);

            if (prepared == null)
            {
                return null;
            }

            EtsFontFile file = prepared.File;
            EtsFontFile.Atlas atlas = file.Atlases[0];
            int width = prepared.Width, height = prepared.Height;
            var sdf = new Texture2D(width, height, TextureFormat.Alpha8, false, true)
            {
                name = prepared.FieldName,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            sdf.LoadRawTextureData(prepared.Field);
            sdf.Apply(false, true);

            float scale = atlas.ScaleY > 0.0001f ? atlas.ScaleY : 1f;
            float pad = atlas.Padding;
            float ascent = file.MaxAbove / scale;
            float descent = file.MaxBelow / scale;
            float em = file.VertSpan > 0f ? file.VertSpan / scale : ascent + descent;
            float capLine = file.TypicalHeight > 0f ? file.TypicalHeight / scale : ascent * 0.77f;

            var font = ScriptableObject.CreateInstance<TMP_FontAsset>();
            font.name = "ETS2 " + System.IO.Path.GetFileNameWithoutExtension(fontPath);
            font.atlasTextures = new[] { sdf };
            font.atlasPopulationMode = AtlasPopulationMode.Static;

            // TextMeshPro only sets these itself when it bakes an atlas; for one read from the
            // game they go straight into the serialized fields it reads them back from.
            SetField(font, "m_AtlasWidth", width);
            SetField(font, "m_AtlasHeight", height);
            SetField(font, "m_AtlasPadding", Mathf.RoundToInt(pad));
            SetField(font, "m_AtlasRenderMode", GlyphRenderMode.SDFAA);

            FaceInfo face = font.faceInfo;
            face.familyName = "Euro Truck Simulator 2";
            face.styleName = System.IO.Path.GetFileNameWithoutExtension(fontPath);
            face.pointSize = Mathf.Max(1, Mathf.RoundToInt(em));
            face.scale = 1f;
            face.lineHeight = em + file.LineSpacing / scale;
            face.ascentLine = ascent;
            face.descentLine = -descent;
            face.baseline = 0f;
            face.capLine = capLine;
            face.meanLine = capLine * 0.72f;
            face.underlineOffset = -descent * 0.4f;
            face.underlineThickness = Mathf.Max(1f, em * 0.05f);
            face.strikethroughOffset = capLine * 0.45f;
            face.strikethroughThickness = Mathf.Max(1f, em * 0.05f);
            face.superscriptOffset = ascent * 0.5f;
            face.superscriptSize = 0.5f;
            face.subscriptOffset = -descent;
            face.subscriptSize = 0.5f;
            font.faceInfo = face;

            var indexByCode = new Dictionary<int, uint>();
            uint next = 0;
            float spaceAdvance = 0f;

            foreach (KeyValuePair<int, EtsFontFile.Glyph> pair in file.Glyphs)
            {
                EtsFontFile.Glyph g = pair.Value;

                if (g.Image != 0 || g.Code < 0)
                {
                    continue;
                }

                GlyphRect rect;
                GlyphMetrics metrics;

                if (g.Width <= 0f || g.Height <= 0f)
                {
                    rect = new GlyphRect(0, 0, 0, 0);
                    metrics = new GlyphMetrics(0f, 0f, 0f, 0f, g.Advance);
                }
                else
                {
                    int x0 = Mathf.Clamp(Mathf.RoundToInt(g.AtlasX - pad), 0, width - 1);
                    int x1 = Mathf.Clamp(Mathf.RoundToInt(g.AtlasX + g.Width + pad), x0 + 1, width);
                    int yTop = Mathf.Clamp(Mathf.RoundToInt(g.AtlasY - pad), 0, height - 1);
                    int yBottom = Mathf.Clamp(Mathf.RoundToInt(g.AtlasY + g.Height + pad), yTop + 1, height);

                    // The file counts rows from the top; the atlas rectangle counts from the bottom.
                    rect = new GlyphRect(x0, height - yBottom, x1 - x0, yBottom - yTop);
                    metrics = new GlyphMetrics(x1 - x0, yBottom - yTop, g.PlaneX - pad, ascent - g.PlaneY + pad, g.Advance);
                }

                var glyph = new Glyph(next, metrics, rect, 1f, 0);
                font.glyphTable.Add(glyph);
                font.characterTable.Add(new TMP_Character((uint)g.Code, font, glyph));
                indexByCode[g.Code] = next;
                next++;

                if (g.Code == 32)
                {
                    spaceAdvance = g.Advance;
                }
            }

            face = font.faceInfo;
            face.tabWidth = spaceAdvance > 0f ? spaceAdvance * 4f : em;
            font.faceInfo = face;

            // The pair adjustments the file lists, in the same atlas pixels.
            if (font.fontFeatureTable != null && font.fontFeatureTable.glyphPairAdjustmentRecords != null)
            {
                foreach (EtsFontFile.Kern kern in file.Kerns)
                {
                    if (indexByCode.TryGetValue(kern.Left, out uint left) && indexByCode.TryGetValue(kern.Right, out uint right))
                    {
                        var first = new GlyphAdjustmentRecord(left, new GlyphValueRecord(0f, 0f, kern.Amount, 0f));
                        var second = new GlyphAdjustmentRecord(right, new GlyphValueRecord());
                        font.fontFeatureTable.glyphPairAdjustmentRecords.Add(new GlyphPairAdjustmentRecord(first, second));
                    }
                }
            }

            Shader shader = Shader.Find("TextMeshPro/Distance Field");

            if (shader == null && TMP_Settings.defaultFontAsset != null && TMP_Settings.defaultFontAsset.material != null)
            {
                shader = TMP_Settings.defaultFontAsset.material.shader;
            }

            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader) { name = font.name + " material" };
            material.SetTexture(ShaderUtilities.ID_MainTex, sdf);
            material.SetFloat(ShaderUtilities.ID_TextureWidth, width);
            material.SetFloat(ShaderUtilities.ID_TextureHeight, height);
            material.SetFloat(ShaderUtilities.ID_GradientScale, pad + 1f);
            material.SetFloat(ShaderUtilities.ID_WeightNormal, 0f);
            material.SetFloat(ShaderUtilities.ID_WeightBold, 0.75f);
            ShaderUtilities.UpdateShaderRatios(material);
            font.material = material;

            if (TMP_Settings.defaultFontAsset != null && TMP_Settings.defaultFontAsset != font)
            {
                font.fallbackFontAssetTable = new List<TMP_FontAsset> { TMP_Settings.defaultFontAsset };
            }

            // In the editor, ReadFontAssetDefinition "upgrades" any asset whose version string is
            // empty by copying metrics out of a legacy record that only assets baked by the old
            // tool have - on one made here that record is null and the upgrade throws. Marking
            // the asset as already current skips it.
            SetField(font, "m_Version", "1.1.0");
            font.ReadFontAssetDefinition();

            // The size the game draws it at, in the layouts' 900-tall frame.
            SizeByPath[fontPath] = file.VertSpan * (EtsGameUi.ReferenceHeight / 768f) * file.DefaultScale;
            return font;
        }

        private static void SetField(object target, string name, object value)
        {
            System.Reflection.FieldInfo field = target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

            if (field == null)
            {
                Debug.LogWarning("[SomniumEuroTruck] font: TextMeshPro has no field " + name + "; the atlas may not sample correctly.");
                return;
            }

            field.SetValue(target, value);
        }

        /// <summary>
        /// The atlas as a single-channel field: the median of the three channels of the game's
        /// multi-channel field, rows turned over to Unity's order. Pure (Prepare's note).
        /// </summary>
        private static byte[] ReadField(EtsFileSystem files, string materialPath, out int width, out int height, out string name)
        {
            width = height = 0;
            name = "";
            string text = files.ReadText(materialPath);

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
            EtsDds dds = EtsTextureCache.ReadDds(files, tobjPath);

            if (dds == null)
            {
                return null;
            }

            EtsBitmap image = EtsPixels.Decode(dds);

            if (image == null)
            {
                return null;
            }

            width = image.Width;
            height = image.Height;
            var field = new byte[width * height];

            for (int y = 0; y < height; y++)
            {
                int dstRow = (height - 1 - y) * width;

                for (int x = 0; x < width; x++)
                {
                    int o = image.Offset(x, y);
                    byte r = image.Rgba[o], g = image.Rgba[o + 1], b = image.Rgba[o + 2];
                    byte median = r > g ? (g > b ? g : (r > b ? b : r)) : (r > b ? r : (g > b ? b : g));
                    field[dstRow + x] = median;
                }
            }

            name = tobjPath + " field";
            return field;
        }
    }
}
