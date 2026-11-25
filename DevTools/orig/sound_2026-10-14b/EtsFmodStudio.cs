using System;
using System.Collections.Generic;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The event metadata of one FMOD Studio bank (the RIFF "LIST PROJ" tree beside the FSB5 samples), read only as far
    /// as the world needs to play an event the way the game's sound designer built it. Measured on ETS2 1.61's banks
    /// (DevTools/work/sound, 2026-10-14) - FMOD publishes no spec, so every field below names the chunk and offset it
    /// was found at and what it held in daf_eu6.bank / scania_2016.bank / effects.bank / noise.bank / tires.bank:
    ///   PRMB  parameter: guid, u16 name length at +24, name at +26, then float min, max, default
    ///         (daf_eu6 "rpm" 350..3000 default 350, "load" 0..1, "brake" 0..1, "turbo" 0..1).
    ///   WAV   waveform: guid, u16 FSB5 sample index at +22.
    ///   WAIB  waveform instrument: instrument guid, waveform guid; its INST follows.
    ///   MUIB  multi instrument: guid; PLST (playlist: 8 bytes, u16 2n+1, u16 20, n x {guid, float weight}); INST.
    ///   INST  instrument body, 130 bytes (+32 when placed on a parameter sheet): +0 timeline guid (zero on a sheet),
    ///         +16 volume dB, +20 pitch semitones, +24 loop count (-1 = loops), +56 auto-pitch parameter guid (zero =
    ///         off), +72 auto-pitch reference (the value where the sample plays at its own pitch: daf_eu6 loaded_1 620,
    ///         unloaded_6 2160 rpm), +86 output track (group bus) guid, +122 auto-pitch "at minimum" (0.70..0.86 in the
    ///         engines), and on a sheet the region: parameter guid at size-28, start at size-8, end at size-4.
    ///   CTRL  automation: guid, owner (instrument or bus), curve guid, u32 property (4 = instrument volume, linear
    ///         0..1 - the crossfades between engine loops; 0 = track volume in dB; 1000+ = effect settings).
    ///   CURV  curve: guid, guid, u16 2n+1, u16 16, n x {float x, float y, float shape, u32 flags}.
    ///   PMLB  parameter sheet: guid, parameter guid, u16 2n+1, u16 16, n instrument guids; its CTRO (u16 2n+1, u16 16,
    ///         n automation guids) says which automations run along that parameter.
    ///   GBSB  group bus (a track): guid, u16 24, parent bus guid at +18.
    ///   EVTB  event: guid; its timeline guid and parameter-sheet guids appear inside it (found by matching).
    /// Event paths come from the bank's ".bank.guids" text beside it in the archive ("{guid} event:/engine/engine").
    /// Pure C#: parse on a worker thread.
    /// </summary>
    public sealed class EtsFmodStudio
    {
        public sealed class Param
        {
            public Guid Id;
            public string Name;
            public float Min, Max, Default;
        }

        public sealed class Curve
        {
            public float[] X;
            public float[] Y;

            /// <summary>Piecewise linear through the points, held flat outside them.</summary>
            public float Eval(float x)
            {
                int n = X.Length;
                if (n == 0) return 0f;
                if (x <= X[0]) return Y[0];
                if (x >= X[n - 1]) return Y[n - 1];
                for (int i = 1; i < n; i++)
                {
                    if (x <= X[i])
                    {
                        float span = X[i] - X[i - 1];
                        if (span <= 1e-6f) return Y[i];
                        return Y[i - 1] + (Y[i] - Y[i - 1]) * (x - X[i - 1]) / span;
                    }
                }
                return Y[n - 1];
            }
        }

        /// <summary>An automation along one parameter (Param = -1: along the timeline, which the world does not play).</summary>
        public sealed class Automation
        {
            public int Param = -1;
            public int Property;
            public Curve Curve;
        }

        public sealed class Bus
        {
            public Guid Id;
            public Bus Parent;
            public readonly List<Automation> Automations = new List<Automation>();
        }

        public sealed class Instrument
        {
            public Guid Id;
            public bool IsMulti;
            /// <summary>FSB5 sample indices: one for a waveform instrument, the playlist for a multi instrument.</summary>
            public readonly List<int> Samples = new List<int>();
            public float VolumeDb;
            public float PitchSemitones;
            public bool Loop;
            public bool OnTimeline;
            public int RegionParam = -1;
            public float RegionMin, RegionMax;
            public int AutoPitchParam = -1;
            public float AutoPitchRef = 1f, AutoPitchAtMin = 1f;
            public Bus Out;
            public readonly List<Automation> Automations = new List<Automation>();
        }

        public sealed class Event
        {
            public Guid Id;
            public string Path;
            public readonly List<Instrument> Instruments = new List<Instrument>();
            /// <summary>The parameters any of its sheets, regions, auto-pitch or automations use.</summary>
            public readonly List<int> Params = new List<int>();
        }

        public readonly List<Param> Params = new List<Param>();
        public readonly Dictionary<string, Event> Events = new Dictionary<string, Event>(StringComparer.OrdinalIgnoreCase);

        private sealed class Chunk
        {
            public string Id;
            public int Off, Size;
        }

        private byte[] _d;
        private readonly List<Chunk> _chunks = new List<Chunk>();

        public static EtsFmodStudio Parse(byte[] bank, string guidsText, out string error)
        {
            error = null;
            if (bank == null || bank.Length < 12) { error = "bank missing"; return null; }
            var s = new EtsFmodStudio { _d = bank };
            try
            {
                s.Walk(12, bank.Length, 0);
                s.Build(guidsText);
            }
            catch (Exception e)
            {
                error = "metadata parse failed: " + e.GetType().Name + " " + e.Message;
                return null;
            }
            return s;
        }

        public int FindParam(string name)
        {
            for (int i = 0; i < Params.Count; i++) if (string.Equals(Params[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public Event FindEvent(string path)
        {
            if (path == null) return null;
            path = path.Trim().TrimStart('/');
            if (path.StartsWith("event:/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(7);
            Events.TryGetValue(path, out Event e);
            return e;
        }

        private void Walk(int p, int end, int depth)
        {
            if (depth > 12) return;
            while (p + 8 <= end)
            {
                string id = Encoding.ASCII.GetString(_d, p, 4);
                int size = BitConverter.ToInt32(_d, p + 4);
                if (size < 0 || p + 8 + size > end) return;
                if (id == "LIST") Walk(p + 12, p + 8 + size, depth + 1);
                else if (id != "SND ") _chunks.Add(new Chunk { Id = id, Off = p + 8, Size = size });
                p += 8 + size;
                if ((size & 1) != 0) p++;
            }
        }

        private Guid G(int o) => o >= 0 && o + 16 <= _d.Length ? new Guid(Slice(o, 16)) : Guid.Empty;
        private byte[] Slice(int o, int n) { var b = new byte[n]; Array.Copy(_d, o, b, 0, n); return b; }
        private float F(int o) => BitConverter.ToSingle(_d, o);
        private int I(int o) => BitConverter.ToInt32(_d, o);
        private int U16(int o) => _d[o] | (_d[o + 1] << 8);

        private void Build(string guidsText)
        {
            var paramIndex = new Dictionary<Guid, int>();
            var wavSample = new Dictionary<Guid, int>();
            var instruments = new Dictionary<Guid, Instrument>();
            var buses = new Dictionary<Guid, Bus>();
            var busParent = new Dictionary<Guid, Guid>();
            var curves = new Dictionary<Guid, Curve>();
            var automationAxis = new Dictionary<Guid, int>();
            var sheets = new Dictionary<Guid, KeyValuePair<int, List<Guid>>>();
            var timelines = new HashSet<Guid>();

            // Two passes: the parameters (PRMS) and waveforms (WAVS) come after the sheets and instruments that name them.
            for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < _chunks.Count; i++)
            {
                Chunk c = _chunks[i];
                int o = c.Off;
                bool late = c.Id == "PMLB" || c.Id == "WAIB" || c.Id == "MUIB";
                if (late != (pass == 1)) continue;
                switch (c.Id)
                {
                    case "PRMB":
                    {
                        if (c.Size < 26) break;
                        int len = U16(o + 24);
                        if (26 + len + 12 > c.Size) break;
                        var p = new Param { Id = G(o), Name = Encoding.ASCII.GetString(_d, o + 26, len).TrimEnd('\0') };
                        int q = o + 26 + len;
                        p.Min = F(q); p.Max = F(q + 4); p.Default = F(q + 8);
                        paramIndex[p.Id] = Params.Count;
                        Params.Add(p);
                        break;
                    }
                    case "WAV ":
                        if (c.Size >= 24) wavSample[G(o)] = U16(o + 22);
                        break;
                    case "GBSB":
                    case "MBSB":
                    case "IBSB":
                        if (c.Size >= 34) busParent[G(o)] = G(o + 18);
                        break;
                    case "TLNB":
                        if (c.Size >= 16) timelines.Add(G(o));
                        break;
                    case "CURV":
                    {
                        if (c.Size < 36) break;
                        int n = (U16(o + 32) - 1) / 2, es = U16(o + 34);
                        if (n < 0 || es < 8 || 36 + n * es > c.Size) break;
                        var cv = new Curve { X = new float[n], Y = new float[n] };
                        for (int k = 0; k < n; k++) { cv.X[k] = F(o + 36 + k * es); cv.Y[k] = F(o + 40 + k * es); }
                        curves[G(o)] = cv;
                        break;
                    }
                    case "PMLB":
                    {
                        if (c.Size < 36) break;
                        int n = (U16(o + 32) - 1) / 2, es = U16(o + 34);
                        var list = new List<Guid>();
                        for (int k = 0; k < n && 36 + (k + 1) * es <= c.Size; k++) list.Add(G(o + 36 + k * es));
                        int par = paramIndex.TryGetValue(G(o + 16), out int pi) ? pi : -1;
                        sheets[G(o)] = new KeyValuePair<int, List<Guid>>(par, list);
                        if (i + 1 < _chunks.Count && _chunks[i + 1].Id == "CTRO") ReadAxis(_chunks[i + 1], par, automationAxis);
                        break;
                    }
                    case "WAIB":
                    case "MUIB":
                    {
                        // The instrument body is the next INST (a multi instrument has its PLST in between).
                        var ins = new Instrument { Id = G(o), IsMulti = c.Id == "MUIB" };
                        int j = i + 1;
                        if (ins.IsMulti && j < _chunks.Count && _chunks[j].Id == "PLST")
                        {
                            Chunk pl = _chunks[j];
                            if (pl.Size >= 12)
                            {
                                int n = (U16(pl.Off + 8) - 1) / 2, es = U16(pl.Off + 10);
                                for (int k = 0; k < n && 12 + (k + 1) * es <= pl.Size; k++) ins.Samples.Add(-1 - instrumentsRefIndex(G(pl.Off + 12 + k * es)));
                            }
                            j++;
                        }
                        if (c.Id == "WAIB" && c.Size >= 32 && wavSample.TryGetValue(G(o + 16), out int si)) ins.Samples.Add(si);
                        else if (c.Id == "WAIB" && c.Size >= 32) _pendingWav.Add(new KeyValuePair<Instrument, Guid>(ins, G(o + 16)));
                        if (j < _chunks.Count && _chunks[j].Id == "INST") ReadInstrument(_chunks[j], ins, paramIndex);
                        instruments[ins.Id] = ins;
                        break;
                    }
                }
            }

            // Waveforms listed after their instruments.
            foreach (var kv in _pendingWav)
                if (wavSample.TryGetValue(kv.Value, out int si)) kv.Key.Samples.Add(si);

            // Multi-instrument playlists name other (waveform) instruments: flatten to their samples.
            foreach (Instrument ins in instruments.Values)
            {
                if (!ins.IsMulti) continue;
                var flat = new List<int>();
                foreach (int code in ins.Samples)
                {
                    if (code >= 0) { flat.Add(code); continue; }
                    Guid g = _refs[-1 - code];
                    if (instruments.TryGetValue(g, out Instrument inner)) foreach (int s in inner.Samples) if (s >= 0) flat.Add(s);
                }
                ins.Samples.Clear();
                ins.Samples.AddRange(flat);
            }

            // Buses and their parents.
            Bus BusFor(Guid g, int depth)
            {
                if (g == Guid.Empty || depth > 6) return null;
                if (buses.TryGetValue(g, out Bus b)) return b;
                b = new Bus { Id = g };
                buses[g] = b;
                if (busParent.TryGetValue(g, out Guid parent) && parent != g) b.Parent = BusFor(parent, depth + 1);
                return b;
            }

            foreach (Instrument ins in instruments.Values)
                if (_outs.TryGetValue(ins, out Guid outBus)) ins.Out = BusFor(outBus, 0);

            // Automations: on an instrument or on a bus.
            for (int i = 0; i < _chunks.Count; i++)
            {
                Chunk c = _chunks[i];
                if (c.Id == "CTRO" && (i == 0 || _chunks[i - 1].Id != "PMLB")) ReadAxis(c, -1, automationAxis, false);
                if (c.Id != "CTRL" || c.Size < 52) continue;
                Guid owner = G(c.Off + 16), curve = G(c.Off + 32), self = G(c.Off);
                if (!curves.TryGetValue(curve, out Curve cv)) continue;
                var a = new Automation { Property = I(c.Off + 48), Curve = cv, Param = automationAxis.TryGetValue(self, out int ax) ? ax : -1 };
                if (instruments.TryGetValue(owner, out Instrument ins)) ins.Automations.Add(a);
                else BusFor(owner, 0)?.Automations.Add(a);
            }

            // Events: name, timeline and sheets (found inside EVTB by matching known guids).
            var names = new Dictionary<Guid, string>();
            if (guidsText != null)
            {
                foreach (string line in guidsText.Split('\n'))
                {
                    string t = line.Trim();
                    int close = t.IndexOf('}');
                    if (t.Length < 40 || t[0] != '{' || close < 0) continue;
                    string rest = t.Substring(close + 1).Trim();
                    if (!rest.StartsWith("event:/", StringComparison.Ordinal)) continue;
                    if (Guid.TryParse(t.Substring(1, close - 1), out Guid g)) names[g] = rest.Substring(7);
                }
            }

            var byTimeline = new Dictionary<Guid, List<Instrument>>();
            foreach (KeyValuePair<Instrument, Guid> kv in _timelineOf)
            {
                if (!byTimeline.TryGetValue(kv.Value, out var l)) byTimeline[kv.Value] = l = new List<Instrument>();
                l.Add(kv.Key);
            }

            foreach (Chunk c in _chunks)
            {
                if (c.Id != "EVTB" || c.Size < 16) continue;
                var ev = new Event { Id = G(c.Off) };
                if (!names.TryGetValue(ev.Id, out ev.Path)) continue;
                for (int p = c.Off + 16; p + 16 <= c.Off + c.Size; p++)
                {
                    Guid g = G(p);
                    if (timelines.Contains(g) && byTimeline.TryGetValue(g, out var tl))
                    {
                        foreach (Instrument ins in tl) { ins.OnTimeline = true; if (!ev.Instruments.Contains(ins)) ev.Instruments.Add(ins); }
                        p += 15;
                    }
                    else if (sheets.TryGetValue(g, out var sheet))
                    {
                        if (sheet.Key >= 0 && !ev.Params.Contains(sheet.Key)) ev.Params.Add(sheet.Key);
                        foreach (Guid ig in sheet.Value)
                            if (instruments.TryGetValue(ig, out Instrument ins) && !ev.Instruments.Contains(ins)) ev.Instruments.Add(ins);
                        p += 15;
                    }
                }
                foreach (Instrument ins in ev.Instruments)
                {
                    AddParam(ev, ins.RegionParam);
                    AddParam(ev, ins.AutoPitchParam);
                    foreach (Automation a in ins.Automations) AddParam(ev, a.Param);
                    for (Bus b = ins.Out; b != null; b = b.Parent) foreach (Automation a in b.Automations) AddParam(ev, a.Param);
                }
                Events[ev.Path] = ev;
            }
        }

        private static void AddParam(Event ev, int p)
        {
            if (p >= 0 && !ev.Params.Contains(p)) ev.Params.Add(p);
        }

        private readonly List<KeyValuePair<Instrument, Guid>> _pendingWav = new List<KeyValuePair<Instrument, Guid>>();
        private readonly List<KeyValuePair<Instrument, Guid>> _timelineOf = new List<KeyValuePair<Instrument, Guid>>();
        private readonly Dictionary<Instrument, Guid> _outs = new Dictionary<Instrument, Guid>();
        private readonly List<Guid> _refs = new List<Guid>();

        private int instrumentsRefIndex(Guid g)
        {
            _refs.Add(g);
            return _refs.Count - 1;
        }

        private void ReadAxis(Chunk c, int param, Dictionary<Guid, int> axis, bool overwrite = true)
        {
            if (c.Size < 4) return;
            int n = (U16(c.Off) - 1) / 2, es = U16(c.Off + 2);
            if (es < 16) return;
            for (int k = 0; k < n && 4 + (k + 1) * es <= c.Size; k++)
            {
                Guid g = G(c.Off + 4 + k * es);
                if (overwrite || !axis.ContainsKey(g)) axis[g] = param;
            }
        }

        private void ReadInstrument(Chunk c, Instrument ins, Dictionary<Guid, int> paramIndex)
        {
            int o = c.Off, size = c.Size;
            if (size < 126) return;
            Guid tl = G(o);
            if (tl != Guid.Empty) _timelineOf.Add(new KeyValuePair<Instrument, Guid>(ins, tl));
            ins.VolumeDb = F(o + 16);
            ins.PitchSemitones = F(o + 20);
            ins.Loop = I(o + 24) == -1;
            if (paramIndex.TryGetValue(G(o + 56), out int ap))
            {
                ins.AutoPitchParam = ap;
                ins.AutoPitchRef = F(o + 72);
                ins.AutoPitchAtMin = F(o + 122);
            }
            _outs[ins] = G(o + 86);
            if (size >= 158 && paramIndex.TryGetValue(G(o + size - 28), out int rp))
            {
                ins.RegionParam = rp;
                ins.RegionMin = F(o + size - 8);
                ins.RegionMax = F(o + size - 4);
            }
        }

        // ---------------------------------------------------------------- evaluation (no allocation)

        /// <summary>
        /// One instrument's linear gain and pitch ratio for the event's parameter values (indexed like Params; NaN = the
        /// parameter's default): zero outside its region; its volume, its crossfade automations (property 4, linear) and
        /// the dB automations of its track and the tracks above it; auto-pitch through (min, at-minimum) and
        /// (reference, 1), extended past the reference on the same line.
        /// </summary>
        public void Evaluate(Instrument ins, float[] values, out float gain, out float pitch)
        {
            pitch = (float)Math.Pow(2.0, ins.PitchSemitones / 12.0);
            gain = 0f;
            if (ins.RegionParam >= 0)
            {
                float v = Value(values, ins.RegionParam);
                if (v < ins.RegionMin || v > ins.RegionMax) return;
            }
            float g = DbToGain(ins.VolumeDb);
            float db = 0f;
            for (int i = 0; i < ins.Automations.Count; i++)
            {
                Automation a = ins.Automations[i];
                if (a.Param < 0) continue;
                float y = a.Curve.Eval(Value(values, a.Param));
                if (a.Property == 4) g *= Clamp01(y);
                else if (a.Property == 0) db += y;
                else if (a.Property == 1) pitch *= (float)Math.Pow(2.0, y / 12.0);
            }
            int guard = 0;
            for (Bus b = ins.Out; b != null && guard < 8; b = b.Parent, guard++)
            {
                for (int i = 0; i < b.Automations.Count; i++)
                {
                    Automation a = b.Automations[i];
                    if (a.Param < 0 || a.Property != 0) continue;
                    db += a.Curve.Eval(Value(values, a.Param));
                }
            }
            gain = g * DbToGain(db);
            if (ins.AutoPitchParam >= 0)
            {
                Param p = Params[ins.AutoPitchParam];
                float v = Value(values, ins.AutoPitchParam);
                float span = ins.AutoPitchRef - p.Min;
                float ap = span > 1e-5f ? 1f + (v - ins.AutoPitchRef) * (1f - ins.AutoPitchAtMin) / span : 1f;
                pitch *= ap < 0.05f ? 0.05f : ap;
            }
        }

        private float Value(float[] values, int p)
        {
            Param par = Params[p];
            float v = values != null && p < values.Length && !float.IsNaN(values[p]) ? values[p] : par.Default;
            return v < par.Min ? par.Min : v > par.Max ? par.Max : v;
        }

        public static float DbToGain(float db) => db <= -80f ? 0f : (float)Math.Pow(10.0, db / 20.0);
        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
