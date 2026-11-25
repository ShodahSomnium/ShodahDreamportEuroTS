using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// One attribute value from a definition file, kept as text with typed readers on top.
    ///
    /// Kept as text because the file does not say what type a value is - "1" may be a count, a
    /// flag or a token - and only the code that asks for it knows. Arrays are values that were
    /// written as attr[]: or attr[3]:, in the order they appeared.
    /// </summary>
    public sealed class EtsSiiValue
    {
        /// <summary>The raw text of a single value. For an array, the last element written.</summary>
        public string Raw;

        /// <summary>Every element, for an array. Null for a plain value.</summary>
        public List<string> Array;

        public bool IsArray => Array != null;

        public int Count => Array != null ? Array.Count : 1;

        public string this[int index] => Array != null ? Array[index] : Raw;

        public override string ToString()
        {
            return Array != null ? "[" + string.Join(", ", Array.ToArray()) + "]" : Raw;
        }

        /// <summary>The value without surrounding quotes.</summary>
        public string AsString()
        {
            return Unquote(Raw);
        }

        public float AsFloat(float fallback = 0f)
        {
            return ParseFloat(Raw, fallback);
        }

        public int AsInt(int fallback = 0)
        {
            return int.TryParse(Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : (int)ParseFloat(Raw, fallback);
        }

        public bool AsBool(bool fallback = false)
        {
            if (string.IsNullOrEmpty(Raw))
            {
                return fallback;
            }

            if (Raw == "true" || Raw == "1")
            {
                return true;
            }

            if (Raw == "false" || Raw == "0")
            {
                return false;
            }

            return fallback;
        }

        /// <summary>
        /// The numbers inside a tuple such as (1, 2, 3) or (0.5; 1, 0, 0), in order. A plain
        /// number comes back as a one-element array.
        /// </summary>
        public float[] AsFloats()
        {
            return ParseFloats(Raw);
        }

        public static string Unquote(string text)
        {
            if (text == null)
            {
                return null;
            }

            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
            {
                return text.Substring(1, text.Length - 2);
            }

            return text;
        }

        /// <summary>
        /// Parses one number the way the engine writes them: decimal, or a hexadecimal float
        /// written as &amp;3f800000, which the game uses to store values exactly.
        /// </summary>
        public static float ParseFloat(string text, float fallback)
        {
            if (string.IsNullOrEmpty(text))
            {
                return fallback;
            }

            string t = text.Trim();

            if (t.Length > 1 && t[0] == '&')
            {
                if (uint.TryParse(t.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits))
                {
                    return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
                }

                return fallback;
            }

            return float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }

        public static float[] ParseFloats(string text)
        {
            var values = new List<float>();

            if (string.IsNullOrEmpty(text))
            {
                return values.ToArray();
            }

            foreach (string part in text.Split(new[] { '(', ')', '{', '}', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                values.Add(ParseFloat(part, 0f));
            }

            return values.ToArray();
        }
    }

    /// <summary>One unit: a typed, named block of attributes.</summary>
    public sealed class EtsSiiUnit
    {
        public string Type;
        public string Name;
        public readonly Dictionary<string, EtsSiiValue> Attributes = new Dictionary<string, EtsSiiValue>(StringComparer.Ordinal);

        /// <summary>Which file the unit came from, for messages.</summary>
        public string Source;

        public bool Has(string key)
        {
            return Attributes.ContainsKey(key);
        }

        public EtsSiiValue Get(string key)
        {
            return Attributes.TryGetValue(key, out EtsSiiValue v) ? v : null;
        }

        public string GetString(string key, string fallback = null)
        {
            EtsSiiValue v = Get(key);
            return v == null ? fallback : v.AsString();
        }

        public float GetFloat(string key, float fallback = 0f)
        {
            EtsSiiValue v = Get(key);
            return v == null ? fallback : v.AsFloat(fallback);
        }

        public int GetInt(string key, int fallback = 0)
        {
            EtsSiiValue v = Get(key);
            return v == null ? fallback : v.AsInt(fallback);
        }

        public bool GetBool(string key, bool fallback = false)
        {
            EtsSiiValue v = Get(key);
            return v == null ? fallback : v.AsBool(fallback);
        }

        public float[] GetFloats(string key)
        {
            EtsSiiValue v = Get(key);
            return v == null ? new float[0] : v.AsFloats();
        }

        /// <summary>Every element of an array attribute, unquoted. Empty when absent.</summary>
        public List<string> GetArray(string key)
        {
            var result = new List<string>();
            EtsSiiValue v = Get(key);

            if (v == null)
            {
                return result;
            }

            for (int i = 0; i < v.Count; i++)
            {
                result.Add(EtsSiiValue.Unquote(v[i]));
            }

            return result;
        }

        public override string ToString()
        {
            return Type + " : " + Name + " (" + Attributes.Count + " attributes)";
        }
    }

    /// <summary>A parsed definition file, or several merged into one.</summary>
    public sealed class EtsSiiFile
    {
        public readonly List<EtsSiiUnit> Units = new List<EtsSiiUnit>();
        private readonly Dictionary<string, EtsSiiUnit> _byName = new Dictionary<string, EtsSiiUnit>(StringComparer.Ordinal);

        public void Add(EtsSiiUnit unit)
        {
            Units.Add(unit);

            if (!string.IsNullOrEmpty(unit.Name))
            {
                _byName[unit.Name] = unit;
            }
        }

        public void AddAll(EtsSiiFile other)
        {
            foreach (EtsSiiUnit unit in other.Units)
            {
                Add(unit);
            }
        }

        public EtsSiiUnit Find(string name)
        {
            return name != null && _byName.TryGetValue(name, out EtsSiiUnit unit) ? unit : null;
        }

        public IEnumerable<EtsSiiUnit> OfType(string type)
        {
            foreach (EtsSiiUnit unit in Units)
            {
                if (unit.Type == type)
                {
                    yield return unit;
                }
            }
        }
    }

    /// <summary>
    /// Reads the engine's text definition format - the SiiNunit files under /def that describe
    /// every road look, prefab, truck part and city in the game.
    ///
    /// The shape is simple and the parser follows it directly rather than through a grammar:
    ///
    ///     SiiNunit
    ///     {
    ///     road_look : road.look0
    ///     {
    ///         name: "Road 1 lane double"
    ///         road_size_left: 4.5
    ///         lanes_left[]: traffic_lane.road.local.no_overtake
    ///         @include "sound.sui"
    ///     }
    ///     }
    ///
    /// Values are strings in quotes, numbers, tokens, tuples in round or curly brackets, and the
    /// occasional hexadecimal float. Comments start with # or //. An @include pulls another
    /// file's attributes into the current unit, resolved relative to the including file when the
    /// path is not absolute; the caller supplies the reader, because only the file system knows
    /// where a path lives.
    /// </summary>
    public static class EtsSii
    {
        /// <summary>Reads a file's text by archive path, or returns null when it does not exist.</summary>
        public delegate string IncludeReader(string path);

        /// <summary>
        /// Whether \r and \xHH escapes in quoted strings are decoded (ReadQuoted). True; false restores the reading
        /// before Phase 1, for probe 1880's comparison only.
        /// </summary>
        public static bool DecodeEscapes = true;

        /// <summary>
        /// Parses a definition file. A qualified unit type - the 1.61 interface's
        /// "ui::text_common : _nameless._.title {" - is read as one type, "ui::text_common", named
        /// "_nameless._.title".
        ///
        /// Before Phase 1 the type stopped at the first ':' (not an identifier character), that ':'
        /// was taken as the type/name separator and no name was found, so every ui:: unit came out
        /// as Type "ui", Name "" and a stray attribute "text_common" holding the name and the brace;
        /// its real attributes still followed, which is why screens built from coordinates alone
        /// worked. Measured by probe 1651 on 1.61: 2 of the 40 units of /ui/delivery.sii kept a
        /// name, none of the 332 of /ui/template/text.sii, so no template could be found by name,
        /// and the delivery screen's name-suffix overrides (EtsFrontEnd, "driven.distance.val")
        /// never matched - it showed the layout's sample run, "Tomatoes delivered from Berlin to
        /// Frankfurt", "1,300 km".
        ///
        /// Probe 1880 (PLAN.md U9) read all 71,424 .sii/.sui files of the demo both ways. 469
        /// change: 461 under /ui, 7 under /system (engine UI) and /def/tutorial_hint_conditions.sii
        /// ("hcd::on_job"), which the world never reads; the other 70,955 parse identically. What
        /// EtsGameUi.BuildScreen places from /ui/loading.sii, delivery.sii, quick_job.sii,
        /// desktop.sii and desktop_standalone_demo.sii - element count, names, kinds, rectangles -
        /// is identical under both readings; only the delivery screen's six overridden labels now
        /// carry the run's numbers. So the qualified reading is the default; Parse(..., false) keeps
        /// the old one for comparison.
        /// </summary>
        public static EtsSiiFile Parse(string text, string sourcePath, IncludeReader includes)
        {
            return Parse(text, sourcePath, includes, true);
        }

        /// <summary>
        /// Parse with the unit-type reading chosen: qualifiedTypes false is the reading before
        /// Phase 1 (probe 1880 found it byte-identical to the old parser on every file).
        /// </summary>
        public static EtsSiiFile Parse(string text, string sourcePath, IncludeReader includes, bool qualifiedTypes)
        {
            var file = new EtsSiiFile();
            var reader = new Reader(text, sourcePath, includes, file, qualifiedTypes);
            reader.ParseFile();
            return file;
        }

        private sealed class Reader
        {
            private readonly string _text;
            private readonly string _source;
            private readonly IncludeReader _includes;
            private readonly EtsSiiFile _file;
            private readonly bool _qualifiedTypes;
            private int _pos;

            public Reader(string text, string source, IncludeReader includes, EtsSiiFile file, bool qualifiedTypes)
            {
                _text = text ?? string.Empty;
                _source = source;
                _includes = includes;
                _file = file;
                _qualifiedTypes = qualifiedTypes;

                // A byte order mark survives UTF-8 decoding as U+FEFF and would otherwise be the
                // first character of the first identifier.
                if (_text.Length > 0 && _text[0] == '﻿')
                {
                    _pos = 1;
                }
            }

            public void ParseFile()
            {
                SkipSpace();

                // Header, when there is one. Fragments pulled in by @include have none and go
                // straight to attributes; a whole file starts with SiiNunit and an open brace.
                string first = PeekIdentifier();

                if (first == "SiiNunit")
                {
                    ReadIdentifier();
                    SkipSpace();
                    Expect('{');
                    ParseUnits();
                    SkipSpace();

                    if (Peek() == '}')
                    {
                        _pos++;
                    }

                    return;
                }

                ParseUnits();
            }

            private void ParseUnits()
            {
                while (true)
                {
                    SkipSpace();

                    if (AtEnd || Peek() == '}')
                    {
                        return;
                    }

                    // An include between units brings in whole units: /def/city.sii is nothing
                    // but a list of @include "city/berlin.sui" lines, each holding one city.
                    if (Peek() == '@')
                    {
                        ParseUnitInclude();
                        continue;
                    }

                    string type = ReadIdentifier();

                    // "ui::text_common": the qualifier is part of the type (see Parse).
                    while (_qualifiedTypes && type.Length > 0 && Peek() == ':' && _pos + 1 < _text.Length && _text[_pos + 1] == ':')
                    {
                        _pos += 2;
                        type += "::" + ReadIdentifier();
                    }

                    if (type.Length == 0)
                    {
                        // Something this parser does not understand. Step over it rather than
                        // spin forever on it.
                        _pos++;
                        continue;
                    }

                    SkipSpace();
                    Expect(':');
                    SkipSpace();
                    string name = ReadIdentifier();
                    SkipSpace();
                    Expect('{');

                    var unit = new EtsSiiUnit { Type = type, Name = name, Source = _source };
                    ParseAttributes(unit);
                    SkipSpace();
                    Expect('}');
                    _file.Add(unit);
                }
            }

            /// <summary>Attributes up to, but not including, the unit's closing brace.</summary>
            public void ParseAttributes(EtsSiiUnit unit)
            {
                while (true)
                {
                    SkipSpace();

                    if (AtEnd || Peek() == '}')
                    {
                        return;
                    }

                    if (Peek() == '@')
                    {
                        ParseInclude(unit);
                        continue;
                    }

                    string key = ReadIdentifier();

                    if (key.Length == 0)
                    {
                        _pos++;
                        continue;
                    }

                    bool isArray = false;

                    SkipInlineSpace();

                    if (Peek() == '[')
                    {
                        isArray = true;
                        int close = _text.IndexOf(']', _pos);
                        _pos = close < 0 ? _text.Length : close + 1;
                    }

                    SkipSpace();
                    Expect(':');
                    SkipInlineSpace();
                    string value = ReadValue();

                    if (isArray)
                    {
                        if (!unit.Attributes.TryGetValue(key, out EtsSiiValue existing) || existing.Array == null)
                        {
                            existing = new EtsSiiValue { Array = new List<string>() };
                            unit.Attributes[key] = existing;
                        }

                        existing.Array.Add(value);
                        existing.Raw = value;
                    }
                    else
                    {
                        unit.Attributes[key] = new EtsSiiValue { Raw = value };
                    }
                }
            }

            /// <summary>An @include at file level: the included text is a list of units.</summary>
            private void ParseUnitInclude()
            {
                string directive = ReadIdentifier();
                SkipInlineSpace();
                string target = EtsSiiValue.Unquote(ReadValue());

                if (directive != "@include" || string.IsNullOrEmpty(target) || _includes == null)
                {
                    return;
                }

                string path = target.StartsWith("/", StringComparison.Ordinal)
                    ? target
                    : DirectoryOf(_source) + "/" + target;

                string included = _includes(path);

                if (included == null)
                {
                    return;
                }

                var inner = new Reader(included, path, _includes, _file, _qualifiedTypes);
                inner.ParseFile();
            }

            private void ParseInclude(EtsSiiUnit unit)
            {
                string directive = ReadIdentifier(); // "@include"
                SkipInlineSpace();
                string target = EtsSiiValue.Unquote(ReadValue());

                if (directive != "@include" || string.IsNullOrEmpty(target) || _includes == null)
                {
                    return;
                }

                string path = target.StartsWith("/", StringComparison.Ordinal)
                    ? target
                    : DirectoryOf(_source) + "/" + target;

                string included = _includes(path);

                if (included == null)
                {
                    return;
                }

                var inner = new Reader(included, path, _includes, _file, _qualifiedTypes);
                inner.SkipSpace();
                inner.ParseAttributes(unit);
            }

            private static string DirectoryOf(string path)
            {
                if (string.IsNullOrEmpty(path))
                {
                    return string.Empty;
                }

                int slash = path.LastIndexOf('/');
                return slash < 0 ? string.Empty : path.Substring(0, slash);
            }

            // ------------------------------------------------------------ lexing

            private bool AtEnd => _pos >= _text.Length;

            private char Peek()
            {
                return _pos < _text.Length ? _text[_pos] : '\0';
            }

            private void Expect(char c)
            {
                if (Peek() == c)
                {
                    _pos++;
                }
            }

            private void SkipInlineSpace()
            {
                while (!AtEnd && (Peek() == ' ' || Peek() == '\t'))
                {
                    _pos++;
                }
            }

            private void SkipSpace()
            {
                while (!AtEnd)
                {
                    char c = Peek();

                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    {
                        _pos++;
                        continue;
                    }

                    if (c == '#' || (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/'))
                    {
                        while (!AtEnd && Peek() != '\n')
                        {
                            _pos++;
                        }

                        continue;
                    }

                    if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '*')
                    {
                        int end = _text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                        _pos = end < 0 ? _text.Length : end + 2;
                        continue;
                    }

                    return;
                }
            }

            private static bool IsIdentifierChar(char c)
            {
                return char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '@' || c == '-' || c == '+' || c == '$' || c == '&';
            }

            private string PeekIdentifier()
            {
                int save = _pos;
                string id = ReadIdentifier();
                _pos = save;
                return id;
            }

            private string ReadIdentifier()
            {
                int start = _pos;

                while (!AtEnd && IsIdentifierChar(Peek()))
                {
                    _pos++;
                }

                return _text.Substring(start, _pos - start);
            }

            /// <summary>
            /// One value: a quoted string, a bracketed tuple (or two in a row, which is how a
            /// placement is written), or a bare word up to the end of the line.
            /// </summary>
            private string ReadValue()
            {
                char c = Peek();

                if (c == '"')
                {
                    return ReadQuoted();
                }

                if (c == '(' || c == '{')
                {
                    var tuple = new StringBuilder();

                    while (Peek() == '(' || Peek() == '{')
                    {
                        tuple.Append(ReadBracketed());
                        SkipInlineSpace();
                    }

                    return tuple.ToString();
                }

                int start = _pos;

                while (!AtEnd && Peek() != '\n' && Peek() != '\r' && Peek() != '}' && Peek() != '#')
                {
                    // A trailing // comment ends the value too.
                    if (Peek() == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                    {
                        break;
                    }

                    _pos++;
                }

                return _text.Substring(start, _pos - start).Trim();
            }

            /// <summary>
            /// A quoted string with its escapes: \n, \t, \r, and \xHH bytes - runs of which are UTF-8 (the
            /// game writes "\xe2\x82\xac1.000.-" for "€1.000.-" in /ui/quick_job_detail.sii, and
            /// "&lt;ret&gt;\r &lt;align ...&gt;" in /ui/loading.sii). Before Phase 1 \r came out as "r" and
            /// \xe2 as "xe2" (probe 1880 (D) counts the files and values this changes).
            /// </summary>
            private string ReadQuoted()
            {
                var result = new StringBuilder();
                result.Append('"');
                _pos++;
                List<byte> bytes = null;

                while (!AtEnd)
                {
                    char c = _text[_pos++];

                    if (c == '\\' && !AtEnd)
                    {
                        char n = _text[_pos++];

                        if (DecodeEscapes && n == 'x' && _pos + 1 < _text.Length && IsHex(_text[_pos]) && IsHex(_text[_pos + 1]))
                        {
                            (bytes ??= new List<byte>()).Add((byte)((HexValue(_text[_pos]) << 4) | HexValue(_text[_pos + 1])));
                            _pos += 2;
                            continue;
                        }

                        if (bytes != null) Flush(ref bytes, result);
                        result.Append(n == 'n' ? '\n' : n == 't' ? '\t' : DecodeEscapes && n == 'r' ? '\r' : n);
                        continue;
                    }

                    // Guarded here rather than only inside Flush: an unconditional call per character made the whole
                    // parse 5-7 % slower than before Phase 1 (probe 1898b: all 71,424 files 2,820 -> 2,961 ms in mono);
                    // guarded, 2,797 ms, and identical output on every file.
                    if (bytes != null) Flush(ref bytes, result);

                    if (c == '"')
                    {
                        break;
                    }

                    result.Append(c);
                }

                if (bytes != null) Flush(ref bytes, result);
                result.Append('"');
                return result.ToString();
            }

            private static void Flush(ref List<byte> bytes, StringBuilder into)
            {
                if (bytes != null && bytes.Count > 0)
                {
                    into.Append(Encoding.UTF8.GetString(bytes.ToArray()));
                    bytes.Clear();
                }
            }

            private static bool IsHex(char c)
            {
                return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            }

            private static int HexValue(char c)
            {
                return c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
            }

            private string ReadBracketed()
            {
                int start = _pos;
                int depth = 0;

                while (!AtEnd)
                {
                    char c = _text[_pos++];

                    if (c == '(' || c == '{')
                    {
                        depth++;
                    }
                    else if (c == ')' || c == '}')
                    {
                        depth--;

                        if (depth == 0)
                        {
                            break;
                        }
                    }
                }

                return _text.Substring(start, _pos - start);
            }
        }
    }
}
