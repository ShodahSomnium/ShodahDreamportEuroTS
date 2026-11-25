using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// A material file: which shader effect to use and which textures feed it.
    ///
    /// Two spellings exist in the archives and both are read. The older one, hand-written:
    ///
    ///     material : "eut2.dif.spec.weight"
    ///     {
    ///         texture : "ger_01.tobj"
    ///         texture_name : "texture_base"
    ///         specular : { 1.6, 1.6, 1.6 }
    ///     }
    ///
    /// and the generated one under /automat, which the model files reference by hash:
    ///
    ///     effect : "eut2.dif.spec.decal.over.rfx" {
    ///         texture : "texture_base" {
    ///             source : "/model/road/crack_patch1.tobj"
    ///             sampler : default
    ///         }
    ///     }
    ///
    /// Either way the result is the same: an effect name, and a map from slot name
    /// ("texture_base", "texture_nmap", ...) to the absolute path of a tobj.
    /// </summary>
    public sealed class EtsMaterialDef
    {
        public string Path;
        public string Effect = string.Empty;

        /// <summary>Slot name to absolute tobj path.</summary>
        public readonly Dictionary<string, string> Textures = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Every scalar or tuple attribute, by name, as written.</summary>
        public readonly Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        public string BaseTexture => Textures.TryGetValue("texture_base", out string p) ? p : null;

        public string NormalTexture => Textures.TryGetValue("texture_nmap", out string p) ? p : null;

        public float GetFloat(string key, float fallback)
        {
            return Attributes.TryGetValue(key, out string raw) ? EtsSiiValue.ParseFloat(raw, fallback) : fallback;
        }

        public float[] GetFloats(string key)
        {
            return Attributes.TryGetValue(key, out string raw) ? EtsSiiValue.ParseFloats(raw) : new float[0];
        }

        public bool EffectHas(string fragment)
        {
            return Effect.IndexOf(fragment, StringComparison.Ordinal) >= 0;
        }

        public static EtsMaterialDef Parse(string text, string ownPath)
        {
            var def = new EtsMaterialDef { Path = ownPath };

            if (string.IsNullOrEmpty(text))
            {
                return def;
            }

            var tokens = Tokenize(text);
            int i = 0;

            // Header: material : "effect" {   or   effect : "name" {
            if (i + 2 < tokens.Count && (tokens[i] == "material" || tokens[i] == "effect") && tokens[i + 1] == ":")
            {
                def.Effect = EtsSiiValue.Unquote(tokens[i + 2]);
                i += 3;
            }

            while (i < tokens.Count && tokens[i] != "{")
            {
                i++;
            }

            i++;

            // Old spelling pairs a "texture" line with the "texture_name" line that follows it.
            var pendingTextures = new List<string>();
            var pendingNames = new List<string>();

            while (i < tokens.Count)
            {
                string key = tokens[i];

                if (key == "}")
                {
                    break;
                }

                // Strip an index: texture[0] and texture_name[0] pair up by position anyway.
                int bracket = key.IndexOf('[');
                string bare = bracket < 0 ? key : key.Substring(0, bracket);

                if (i + 1 >= tokens.Count || tokens[i + 1] != ":")
                {
                    i++;
                    continue;
                }

                i += 2;

                if (i >= tokens.Count)
                {
                    break;
                }

                // New spelling: texture : "slot" { source : "path" sampler : x }
                if (bare == "texture" && i + 1 < tokens.Count && tokens[i + 1] == "{")
                {
                    string slot = EtsSiiValue.Unquote(tokens[i]);
                    i += 2;
                    string source = null;

                    while (i < tokens.Count && tokens[i] != "}")
                    {
                        if (tokens[i] == "source" && i + 2 < tokens.Count && tokens[i + 1] == ":")
                        {
                            source = EtsSiiValue.Unquote(tokens[i + 2]);
                            i += 3;
                            continue;
                        }

                        i++;
                    }

                    i++;

                    if (source != null)
                    {
                        def.Textures[slot] = EtsTobj.Resolve(source, ownPath);
                    }

                    continue;
                }

                string value = tokens[i++];

                if (bare == "texture")
                {
                    pendingTextures.Add(EtsSiiValue.Unquote(value));
                }
                else if (bare == "texture_name")
                {
                    pendingNames.Add(EtsSiiValue.Unquote(value));
                }
                else
                {
                    def.Attributes[key] = value;
                }
            }

            for (int t = 0; t < pendingTextures.Count; t++)
            {
                string slot = t < pendingNames.Count ? pendingNames[t] : (t == 0 ? "texture_base" : "texture_" + t);
                def.Textures[slot] = EtsTobj.Resolve(pendingTextures[t], ownPath);
            }

            return def;
        }

        /// <summary>
        /// Splits a material file into words, quoted strings, brackets and tuples. A tuple in
        /// curly braces that holds only numbers is one token, so that a colour survives intact;
        /// a brace that opens a block stays a brace.
        /// </summary>
        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '#' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (c == '"')
                {
                    int end = text.IndexOf('"', i + 1);

                    if (end < 0)
                    {
                        end = text.Length - 1;
                    }

                    tokens.Add(text.Substring(i, end - i + 1));
                    i = end + 1;
                    continue;
                }

                if (c == '{')
                {
                    int end = text.IndexOf('}', i + 1);

                    if (end > i)
                    {
                        string inner = text.Substring(i + 1, end - i - 1);
                        bool numeric = inner.Trim().Length > 0;

                        foreach (char ch in inner)
                        {
                            if (!(char.IsDigit(ch) || ch == '.' || ch == ',' || ch == '-' || ch == '+' || ch == 'e' || ch == 'E' || char.IsWhiteSpace(ch)))
                            {
                                numeric = false;
                                break;
                            }
                        }

                        if (numeric)
                        {
                            tokens.Add(text.Substring(i, end - i + 1));
                            i = end + 1;
                            continue;
                        }
                    }

                    tokens.Add("{");
                    i++;
                    continue;
                }

                if (c == '}' || c == ':')
                {
                    tokens.Add(c.ToString());
                    i++;
                    continue;
                }

                int start = i;

                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ':' && text[i] != '{' && text[i] != '}' && text[i] != '"')
                {
                    i++;
                }

                if (i == start)
                {
                    i++;
                    continue;
                }

                tokens.Add(text.Substring(start, i - start));
            }

            return tokens;
        }
    }
}
