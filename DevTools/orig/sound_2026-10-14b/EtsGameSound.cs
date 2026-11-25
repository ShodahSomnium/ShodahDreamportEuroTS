using System;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// One FMOD Studio event of the game ("/sound/truck/daf_eu6.bank#engine/engine"), loaded from the player's own
    /// install (never shipped): its instruments as the bank's metadata describes them (EtsFmodStudio) and one AudioClip
    /// per sample it plays. Ready once every clip is uploaded; Failure says why when it cannot be. Values holds the
    /// event's parameter values (by EtsFmodStudio.Params index; NaN = the parameter's default) - set them with Set.
    /// </summary>
    public sealed class EtsGameEvent
    {
        public readonly string Source;
        public readonly string BankPath;
        public readonly string EventPath;
        public EtsFmodStudio Studio { get; internal set; }
        public EtsFmodStudio.Event Event { get; internal set; }

        /// <summary>[instrument][playlist entry]; an entry is null when its sample was skipped or failed.</summary>
        public AudioClip[][] Clips { get; internal set; }

        /// <summary>The FSB5 sample names, like Clips.</summary>
        public string[][] SampleNames { get; internal set; }

        public float[] Values { get; internal set; }
        public bool Ready { get; internal set; }
        public string Failure { get; internal set; }
        public bool Failed => Failure != null;

        internal readonly float MaxSeconds;
        internal readonly bool WavesOnly;
        internal readonly string[] FixedNames;
        internal readonly float[] FixedValues;
        internal int[][] SampleIds;
        internal float Seconds;

        internal EtsGameEvent(string source, string bank, string path, float maxSeconds, bool wavesOnly, string[] fixedNames, float[] fixedValues)
        {
            Source = source;
            BankPath = bank;
            EventPath = path;
            MaxSeconds = maxSeconds;
            WavesOnly = wavesOnly;
            FixedNames = fixedNames;
            FixedValues = fixedValues;
        }

        /// <summary>Sets every parameter of this event with that name (no allocation).</summary>
        public void Set(string name, float value)
        {
            if (Event == null || Values == null) return;
            List<int> ps = Event.Params;
            for (int i = 0; i < ps.Count; i++)
                if (string.Equals(Studio.Params[ps[i]].Name, name, StringComparison.Ordinal)) Values[ps[i]] = value;
        }

        /// <summary>The first instrument whose sample name contains <paramref name="part"/> (and not <paramref name="not"/>), or -1.</summary>
        public int FindInstrument(string part, string not = null)
        {
            if (SampleNames == null) return -1;
            for (int i = 0; i < SampleNames.Length; i++)
            {
                string[] names = SampleNames[i];
                for (int k = 0; names != null && k < names.Length; k++)
                {
                    string n = names[k];
                    if (n != null && Clips[i][k] != null && n.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0
                        && (not == null || n.IndexOf(not, StringComparison.OrdinalIgnoreCase) < 0)) return i;
                }
            }
            return -1;
        }

        private bool NameHas(int instrument, string part)
        {
            string[] names = SampleNames != null && instrument < SampleNames.Length ? SampleNames[instrument] : null;
            for (int k = 0; names != null && k < names.Length; k++)
                if (names[k] != null && names[k].IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>A clip of the instrument (a random playlist entry for a multi instrument), or null.</summary>
        public AudioClip PickClip(int instrument)
        {
            if (Clips == null || instrument < 0 || instrument >= Clips.Length) return null;
            AudioClip[] c = Clips[instrument];
            if (c == null || c.Length == 0) return null;
            int start = c.Length > 1 ? UnityEngine.Random.Range(0, c.Length) : 0;
            for (int k = 0; k < c.Length; k++)
            {
                AudioClip clip = c[(start + k) % c.Length];
                if (clip != null) return clip;
            }
            return null;
        }

        /// <summary>
        /// Plays the event once on <paramref name="source"/>: every instrument that sounds at the current parameter values
        /// (its region, crossfades and track levels) - a random playlist entry for a multi instrument - at its own gain.
        /// Instruments whose sample names contain <paramref name="skip"/> are left out. True when anything played.
        /// </summary>
        public bool PlayOnce(AudioSource source, float level, string skip = null)
        {
            if (!Ready || source == null || !source.isActiveAndEnabled) return false;
            bool any = false;
            for (int i = 0; i < Event.Instruments.Count; i++)
            {
                if (skip != null && NameHas(i, skip)) continue;
                Studio.Evaluate(Event.Instruments[i], Values, out float gain, out _);
                if (gain <= 0.0005f) continue;
                AudioClip clip = PickClip(i);
                if (clip == null) continue;
                source.PlayOneShot(clip, Mathf.Clamp01(level * gain));
                any = true;
            }
            return any;
        }

        /// <summary>One instrument of the event, once.</summary>
        public bool PlayInstrument(AudioSource source, int instrument, float level)
        {
            if (!Ready || source == null || !source.isActiveAndEnabled || instrument < 0) return false;
            AudioClip clip = PickClip(instrument);
            if (clip == null) return false;
            source.PlayOneShot(clip, Mathf.Clamp01(level * EtsFmodStudio.DbToGain(Event.Instruments[instrument].VolumeDb)));
            return true;
        }

        public override string ToString() => Source;
    }

    /// <summary>
    /// Plays a game event's looping instruments the way FMOD does for a parameter sheet (no FMOD here: one AudioSource per
    /// instrument): every frame Drive evaluates each instrument at the event's parameter values - in its region or
    /// silent, its crossfade automations, its track's dB automations, its auto-pitch - and sets that source's volume and
    /// pitch, starting a loop at a random point when it becomes audible and stopping it when it falls silent. Looping
    /// instruments on the event's timeline (a horn, a reversing beeper) play while TimelineActive. No allocation per frame.
    /// </summary>
    public sealed class EtsGameEventPlayer
    {
        public readonly EtsGameEvent Ev;
        public readonly AudioSource[] Sources;
        public bool TimelineActive;
        /// <summary>Below this (about -42 dB, where FMOD tracks park their idle layers) a loop is stopped to save a voice.</summary>
        private const float Silent = 0.008f;
        public int Playing { get; private set; }

        public EtsGameEventPlayer(EtsGameEvent ev, Func<AudioSource> newSource)
        {
            Ev = ev;
            int n = ev.Event.Instruments.Count;
            Sources = new AudioSource[n];
            for (int i = 0; i < n; i++)
            {
                EtsFmodStudio.Instrument ins = ev.Event.Instruments[i];
                if (!ins.Loop || ev.PickClip(i) == null) continue; // one-shots are played by PlayOnce
                AudioSource s = newSource();
                s.loop = true;
                s.clip = ev.PickClip(i);
                Sources[i] = s;
            }
        }

        public void Drive(float master)
        {
            int playing = 0;
            EtsFmodStudio studio = Ev.Studio;
            List<EtsFmodStudio.Instrument> list = Ev.Event.Instruments;
            for (int i = 0; i < Sources.Length; i++)
            {
                AudioSource s = Sources[i];
                if (s == null) continue;
                EtsFmodStudio.Instrument ins = list[i];
                float gain = 0f, pitch = 1f;
                if (!ins.OnTimeline || TimelineActive) studio.Evaluate(ins, Ev.Values, out gain, out pitch);
                float v = gain * master;
                if (v > Silent)
                {
                    s.volume = v > 1f ? 1f : v;
                    s.pitch = pitch < 0.05f ? 0.05f : pitch > 3f ? 3f : pitch;
                    if (!s.isPlaying)
                    {
                        if (s.clip != null && !ins.OnTimeline) s.time = UnityEngine.Random.value * s.clip.length * 0.9f;
                        s.Play();
                    }
                    playing++;
                }
                else if (s.isPlaying)
                {
                    s.Stop();
                }
            }
            Playing = playing;
        }

        public void Stop()
        {
            for (int i = 0; i < Sources.Length; i++)
                if (Sources[i] != null && Sources[i].isPlaying) Sources[i].Stop();
            Playing = 0;
        }

        public void SetSpatialBlend(float blend)
        {
            for (int i = 0; i < Sources.Length; i++)
                if (Sources[i] != null) Sources[i].spatialBlend = blend;
        }

        public void Destroy()
        {
            for (int i = 0; i < Sources.Length; i++)
            {
                if (Sources[i] != null) UnityEngine.Object.Destroy(Sources[i]);
                Sources[i] = null;
            }
        }
    }

    /// <summary>
    /// Loads the game's own sounds from the player's install at run time (nothing is shipped in the world): FMOD Studio
    /// banks are read from the archives, their FSB5 Vorbis samples decoded (EtsFmodBank: headers rebuilt, vendored
    /// NVorbis) and their event metadata parsed (EtsFmodStudio) on a worker thread ('Ets sound decode'); the main thread
    /// turns the PCM into AudioClips (AudioClip.Create + SetData - the route the Somnium client plays; OnAudioFilterRead
    /// is never called for world code) a slice at a time, at most <see cref="UploadFloatsPerFrame"/> floats a frame.
    /// Each bank is read once per batch of requests; each sample is decoded once and its clip shared by every event and
    /// every player that uses it. Pump runs once a frame (EtsTruckAudio/EtsCarAudio/EtsAmbience call it).
    /// </summary>
    public static class EtsGameSound
    {
        /// <summary>Off = everything stays synthesised (the fallback the sound components keep).</summary>
        public static bool Enabled = true;

        /// <summary>The upload budget: 2 x 65536 floats (about 0.5 MB of PCM, a few tenths of a millisecond) a frame.</summary>
        public const int UploadFloatsPerFrame = 131072;

        public static long PcmBytes { get; private set; }
        public static int ClipCount { get; private set; }
        public static double DecodeMs { get; private set; }

        private sealed class BankState
        {
            public string Path;
            public EtsFmodStudio Studio;
            public string Failure;
            public bool Busy;
            public readonly List<EtsGameEvent> Waiting = new List<EtsGameEvent>();
            public readonly Dictionary<int, AudioClip> Clips = new Dictionary<int, AudioClip>();
            public readonly HashSet<int> Failed = new HashSet<int>();
            public readonly HashSet<int> Claimed = new HashSet<int>(); // under lock(Claimed): decoded or being decoded
        }

        private sealed class Decoded
        {
            public BankState Bank;
            public int Sample;
            public string Name;
            public float[] Pcm;
            public int Channels, Rate;
            public string Failure;
        }

        private sealed class JobResult
        {
            public BankState Bank;
            public EtsFmodStudio Studio;
            public string Failure;
            public readonly List<Decoded> Samples = new List<Decoded>();
            public List<EtsGameEvent> Events;
            public double Ms;
        }

        private sealed class Upload
        {
            public Decoded D;
            public AudioClip Clip;
            public int Done;
        }

        private static EtsWorker s_worker;
        private static readonly Dictionary<string, EtsGameEvent> s_events = new Dictionary<string, EtsGameEvent>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, BankState> s_banks = new Dictionary<string, BankState>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<Upload> s_uploads = new Queue<Upload>();
        private static readonly List<EtsGameEvent> s_pending = new List<EtsGameEvent>();
        private static float[] s_slice;
        private static int s_pumpFrame = -1;
        private static int s_drained;
        private static readonly Func<bool> s_more = () => s_drained++ < 2;
        private static bool s_loggedStart;

        /// <summary>
        /// The event named by <paramref name="source"/> ("bank#event"), loading if new. maxSeconds caps each sample (a
        /// long ambience bed is cut there and its end crossfaded into its start, so it still loops seamlessly); wavesOnly
        /// leaves out multi instruments; fixedParams ("skid_speed=0,aero_wnd=0") are parameters the world never moves -
        /// instruments whose region excludes that value are never decoded. Null when the source is malformed, game sounds
        /// are off or the archives are not mounted yet (nothing is cached then - ask again later).
        /// </summary>
        public static EtsGameEvent Get(string source, float maxSeconds = 0f, bool wavesOnly = false, string fixedParams = null)
        {
            if (string.IsNullOrEmpty(source)) return null;
            if (s_events.TryGetValue(source, out EtsGameEvent ev)) return ev;
            if (!Enabled || EtsEngine.Files == null) return null; // not cached: ask again once the archives are mounted
            int hash = source.IndexOf('#');
            if (hash <= 0 || hash == source.Length - 1) return null;
            string bank = source.Substring(0, hash).Trim();
            string path = source.Substring(hash + 1).Trim();
            string[] names = null;
            float[] values = null;
            if (!string.IsNullOrEmpty(fixedParams))
            {
                string[] parts = fixedParams.Split(',');
                names = new string[parts.Length];
                values = new float[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    string[] kv = parts[i].Split('=');
                    names[i] = kv[0].Trim();
                    values[i] = kv.Length > 1 ? EtsSiiValue.ParseFloat(kv[1].Trim(), 0f) : 0f;
                }
            }
            ev = new EtsGameEvent(source, bank, path, maxSeconds, wavesOnly, names, values);
            s_events[source] = ev;

            if (!s_banks.TryGetValue(bank, out BankState b))
            {
                b = new BankState { Path = bank };
                s_banks[bank] = b;
            }
            if (b.Failure != null) { ev.Failure = b.Failure; return ev; }
            b.Waiting.Add(ev);
            Start(b);
            return ev;
        }

        /// <summary>Reads a .soundref ("source=\"/sound/x.bank#event\"") and returns its source, or the input when it already is one. Any thread.</summary>
        public static string ResolveSoundref(EtsFileSystem files, string reference)
        {
            if (string.IsNullOrEmpty(reference) || reference == "-") return null;
            reference = reference.Trim();
            if (reference.IndexOf('#') > 0) return reference;
            if (!reference.EndsWith(".soundref", StringComparison.OrdinalIgnoreCase) || files == null) return null;
            string text = files.ReadText(reference);
            if (text == null) return null;
            int at = text.IndexOf("source", StringComparison.Ordinal);
            if (at < 0) return null;
            int q1 = text.IndexOf('"', at), q2 = q1 >= 0 ? text.IndexOf('"', q1 + 1) : -1;
            return q1 >= 0 && q2 > q1 ? text.Substring(q1 + 1, q2 - q1 - 1) : null;
        }

        private static void Start(BankState b)
        {
            if (b.Busy || b.Waiting.Count == 0) return;
            if (s_worker == null) s_worker = new EtsWorker("Ets sound decode");
            b.Busy = true;
            var events = new List<EtsGameEvent>(b.Waiting);
            b.Waiting.Clear();
            EtsFileSystem files = EtsEngine.Files;
            EtsFmodStudio known = b.Studio;
            if (!s_loggedStart)
            {
                s_loggedStart = true;
                Debug.Log("[SomniumEuroTruck] Game sound: decoding the game's FMOD banks from the install (" + (s_worker.Threaded ? "worker thread" : "main thread, no worker: " + s_worker.Failure) + ").");
            }
            s_worker.Post(() => Work(files, b, known, events), (result, error) => Done(b, events, result as JobResult, error));
        }

        // ---------------------------------------------------------------- worker thread (pure: no Unity calls)

        private static JobResult Work(EtsFileSystem files, BankState b, EtsFmodStudio known, List<EtsGameEvent> events)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var r = new JobResult { Bank = b, Events = events };
            byte[] bytes = files.Read(b.Path);
            if (bytes == null) { r.Failure = "bank " + b.Path + " is not in the archives"; return r; }
            EtsFmodBank fsb = EtsFmodBank.Parse(bytes, out string err);
            if (fsb == null) { r.Failure = b.Path + ": " + err; return r; }
            EtsFmodStudio studio = known ?? EtsFmodStudio.Parse(bytes, files.ReadText(b.Path + ".guids"), out err);
            if (studio == null) { r.Failure = b.Path + ": " + err; return r; }
            r.Studio = studio;

            foreach (EtsGameEvent ev in events)
            {
                EtsFmodStudio.Event e = studio.FindEvent(ev.EventPath);
                if (e == null) continue; // reported on the main thread
                foreach (EtsFmodStudio.Instrument ins in e.Instruments)
                {
                    if (!Wanted(studio, ev, ins)) continue;
                    foreach (int s in ins.Samples)
                    {
                        if (s < 0 || s >= fsb.Samples.Count) continue;
                        lock (b.Claimed)
                        {
                            if (!b.Claimed.Add(s)) continue;
                        }
                        EtsFmodBank.Sample sample = fsb.Samples[s];
                        var d = new Decoded { Bank = b, Sample = s, Name = sample.Name, Channels = sample.Channels, Rate = sample.Rate };
                        d.Pcm = fsb.Decode(sample, out string derr);
                        if (d.Pcm == null) d.Failure = derr;
                        else if (ev.MaxSeconds > 0f) d.Pcm = CapLoop(d.Pcm, sample.Channels, (int)(ev.MaxSeconds * sample.Rate), sample.Rate / 2);
                        r.Samples.Add(d);
                    }
                }
            }
            r.Ms = clock.Elapsed.TotalMilliseconds;
            return r;
        }

        /// <summary>Instruments a request never needs: multi instruments when wavesOnly; regions that exclude a fixed parameter value.</summary>
        internal static bool Wanted(EtsFmodStudio studio, EtsGameEvent ev, EtsFmodStudio.Instrument ins)
        {
            if (ev.WavesOnly && ins.IsMulti) return false;
            if (ins.RegionParam < 0 || ev.FixedNames == null) return true;
            string name = studio.Params[ins.RegionParam].Name;
            for (int i = 0; i < ev.FixedNames.Length; i++)
                if (string.Equals(ev.FixedNames[i], name, StringComparison.Ordinal))
                    return ev.FixedValues[i] >= ins.RegionMin && ev.FixedValues[i] <= ins.RegionMax;
            return true;
        }

        /// <summary>The first <paramref name="frames"/> frames, the end crossfaded (equal power, <paramref name="fade"/> frames) into what followed, so it loops without a seam.</summary>
        private static float[] CapLoop(float[] pcm, int ch, int frames, int fade)
        {
            int total = pcm.Length / ch;
            if (frames <= 0 || total <= frames + fade) return pcm;
            var o = new float[frames * ch];
            Array.Copy(pcm, o, o.Length);
            for (int i = 0; i < fade; i++)
            {
                double t = (i + 0.5) / fade;
                float a = (float)Math.Sin(t * Math.PI * 0.5), bgain = (float)Math.Cos(t * Math.PI * 0.5);
                for (int c = 0; c < ch; c++) o[i * ch + c] = pcm[i * ch + c] * a + pcm[(frames + i) * ch + c] * bgain;
            }
            return o;
        }

        // ---------------------------------------------------------------- main thread

        private static void Done(BankState b, List<EtsGameEvent> events, JobResult r, Exception error)
        {
            b.Busy = false;
            if (r == null || r.Failure != null || error != null)
            {
                string why = r != null && r.Failure != null ? r.Failure : error != null ? error.GetType().Name + ": " + error.Message : "no result";
                if (b.Studio == null) b.Failure = why;
                foreach (EtsGameEvent ev in events) ev.Failure = why;
                Debug.Log("[SomniumEuroTruck] Game sound: " + b.Path + " failed - " + why + ".");
                Start(b);
                return;
            }
            b.Studio = r.Studio;
            DecodeMs += r.Ms;
            foreach (Decoded d in r.Samples)
            {
                if (d.Pcm == null)
                {
                    b.Failed.Add(d.Sample);
                    Debug.Log("[SomniumEuroTruck] Game sound: " + b.Path + " sample '" + d.Name + "' did not decode - " + d.Failure + ".");
                    continue;
                }
                AudioClip clip = AudioClip.Create("ets " + d.Name, d.Pcm.Length / d.Channels, d.Channels, d.Rate, false);
                s_uploads.Enqueue(new Upload { D = d, Clip = clip });
            }
            foreach (EtsGameEvent ev in events)
            {
                EtsFmodStudio.Event e = r.Studio.FindEvent(ev.EventPath);
                if (e == null) { ev.Failure = "no event '" + ev.EventPath + "' in " + b.Path; continue; }
                ev.Studio = r.Studio;
                ev.Event = e;
                var values = new float[r.Studio.Params.Count];
                for (int i = 0; i < values.Length; i++) values[i] = float.NaN;
                ev.Values = values;
                if (ev.FixedNames != null) for (int i = 0; i < ev.FixedNames.Length; i++) ev.Set(ev.FixedNames[i], ev.FixedValues[i]);
                ev.SampleIds = new int[e.Instruments.Count][];
                for (int i = 0; i < e.Instruments.Count; i++)
                    ev.SampleIds[i] = Wanted(r.Studio, ev, e.Instruments[i]) ? e.Instruments[i].Samples.ToArray() : new int[0];
                s_pending.Add(ev);
            }
            Start(b);
        }

        /// <summary>Main thread, every frame (any caller; runs once a frame).</summary>
        public static void Pump()
        {
            int frame = Time.frameCount;
            if (frame == s_pumpFrame) return;
            s_pumpFrame = frame;
            if (s_worker != null)
            {
                s_drained = 0;
                s_worker.Drain(s_more);
            }

            int budget = UploadFloatsPerFrame;
            while (budget > 0 && s_uploads.Count > 0)
            {
                Upload u = s_uploads.Peek();
                float[] pcm = u.D.Pcm;
                int left = pcm.Length - u.Done;
                int n = Math.Min(Math.Min(left, 65536), budget);
                n -= n % u.D.Channels;
                if (n <= 0) n = left;
                float[] slice = n == 65536 ? (s_slice ?? (s_slice = new float[65536])) : new float[n];
                Array.Copy(pcm, u.Done, slice, 0, n);
                u.Clip.SetData(slice, u.Done / u.D.Channels);
                u.Done += n;
                budget -= n;
                if (u.Done >= pcm.Length)
                {
                    s_uploads.Dequeue();
                    u.D.Bank.Clips[u.D.Sample] = u.Clip;
                    PcmBytes += (long)pcm.Length * 4;
                    ClipCount++;
                    u.D.Pcm = null;
                }
            }

            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                EtsGameEvent ev = s_pending[i];
                if (TryFinish(ev)) s_pending.RemoveAt(i);
            }
        }

        private static bool TryFinish(EtsGameEvent ev)
        {
            BankState b = s_banks[ev.BankPath];
            int[][] ids = ev.SampleIds;
            bool waiting = false;
            foreach (int[] list in ids)
                foreach (int s in list)
                    if (!b.Clips.ContainsKey(s) && !b.Failed.Contains(s))
                    {
                        bool claimed;
                        lock (b.Claimed) claimed = b.Claimed.Contains(s);
                        if (claimed) waiting = true;
                    }
            if (waiting) return false;

            var clips = new AudioClip[ids.Length][];
            var names = new string[ids.Length][];
            int count = 0;
            float seconds = 0f;
            for (int i = 0; i < ids.Length; i++)
            {
                clips[i] = new AudioClip[ids[i].Length];
                names[i] = new string[ids[i].Length];
                for (int k = 0; k < ids[i].Length; k++)
                {
                    if (b.Clips.TryGetValue(ids[i][k], out AudioClip c) && c != null)
                    {
                        clips[i][k] = c;
                        names[i][k] = c.name.StartsWith("ets ", StringComparison.Ordinal) ? c.name.Substring(4) : c.name;
                        count++;
                        seconds += c.length;
                    }
                }
            }
            ev.Clips = clips;
            ev.SampleNames = names;
            ev.Seconds = seconds;
            if (count == 0)
            {
                ev.Failure = "no sample of '" + ev.EventPath + "' decoded";
                Debug.Log("[SomniumEuroTruck] Game sound: " + ev.Source + " unusable - " + ev.Failure + ".");
                return true;
            }
            ev.Ready = true;
            Debug.Log("[SomniumEuroTruck] Game sound: " + ev.Source + " ready - " + ev.Event.Instruments.Count + " instruments, " + count + " samples, "
                + seconds.ToString("0.0") + " s of audio (all game sound so far: " + ClipCount + " clips, " + (PcmBytes / 1048576.0).ToString("0.0") + " MB PCM, decode "
                + DecodeMs.ToString("0") + " ms on the worker).");
            return true;
        }
    }

    /// <summary>
    /// Which game events a truck plays, read from its own definitions the way the game assembles them: the engine,
    /// transmission and chassis parts' "sounds[]" ("engine|/def/.../sound_engine.soundref"), and the cabin's and
    /// interior's sound.sui in the truck's folder; a name the truck does not list falls back to /sound/truck/default
    /// (interior_blinker_on.soundref ...). Each entry is a .soundref ("source=\"/sound/truck/daf_eu6.bank#engine/engine\"")
    /// or a bank#event id. Resolved on the sound worker; Sources maps the game's sound names to bank#event.
    /// </summary>
    public sealed class EtsGameTruckSounds
    {
        public readonly string Key;
        public bool Resolved { get; private set; }
        public string Failure { get; private set; }
        public readonly Dictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, EtsGameTruckSounds> s_cache = new Dictionary<string, EtsGameTruckSounds>(StringComparer.Ordinal);
        private static EtsWorker s_worker;
        private static readonly Func<bool> s_always = () => true;

        private static readonly string[] Defaults =
        {
            "blinker_on|/sound/truck/default/interior_blinker_on.soundref",
            "blinker_off|/sound/truck/default/interior_blinker_off.soundref",
            "stick_park_brake|/sound/truck/default/interior_stick_park_brake.soundref",
            "stick_park_brake_off|/sound/truck/default/interior_stick_park_brake_off.soundref",
            "stick_blinker|/sound/truck/default/interior_stick_blinker.soundref",
            "stick_blinker_off|/sound/truck/default/interior_stick_blinker_off.soundref",
            "air_brake|/sound/truck/default/effects_air_brake.soundref",
            "air_gear|/sound/truck/default/effects_air_gear.soundref",
            "reverse|/sound/truck/default/effects_reverse.soundref",
            "hook_attach|/sound/truck/default/effects_hook_attach.soundref",
            "hook_detach|/sound/truck/default/effects_hook_detach.soundref",
            "trailer_cables|/sound/truck/default/effects_trailer_cables.soundref",
            "aero_noise|/sound/truck/default/noise_aero.soundref",
            "horn|/sound/truck/default/horn_default.soundref",
            // The tyres are not in any definition: the exe builds "/sound/tires/" + the surface + ".soundref".
            "tires_asphalt|/sound/tires/asphalt.soundref",
            "tires_gravel|/sound/tires/gravel.soundref",
            "tires_grass|/sound/tires/grass.soundref",
        };

        private EtsGameTruckSounds(string key) { Key = key; }

        public static EtsGameTruckSounds For(EtsVehicleSpec spec)
        {
            if (spec == null || EtsEngine.Files == null) return null;
            string key = spec.Folder + "|" + spec.EnginePath + "|" + spec.TransmissionPath + "|" + spec.ChassisPath;
            if (s_cache.TryGetValue(key, out EtsGameTruckSounds set)) return set;
            set = new EtsGameTruckSounds(key);
            s_cache[key] = set;
            if (s_worker == null) s_worker = new EtsWorker("Ets sound defs");
            EtsFileSystem files = EtsEngine.Files;
            string folder = spec.Folder, engine = spec.EnginePath, gearbox = spec.TransmissionPath, chassis = spec.ChassisPath;
            s_worker.Post(() => Read(files, folder, engine, gearbox, chassis), (result, error) =>
            {
                if (result is Dictionary<string, string> map) foreach (var kv in map) set.Sources[kv.Key] = kv.Value;
                set.Failure = error != null ? error.GetType().Name + ": " + error.Message : set.Sources.ContainsKey("engine") ? null : "no engine sound in " + engine;
                set.Resolved = true;
            });
            return set;
        }

        /// <summary>Main thread, every frame: hands back finished resolutions.</summary>
        public static void Pump()
        {
            s_worker?.Drain(s_always);
        }

        private static Dictionary<string, string> Read(EtsFileSystem files, string folder, string engine, string gearbox, string chassis)
        {
            var raw = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in Defaults) Add(raw, line, false);
            AddPart(files, raw, chassis);
            AddPart(files, raw, gearbox);
            AddPart(files, raw, engine);
            AddText(raw, files.ReadText(folder + "/cabin/sound.sui"));
            AddText(raw, files.ReadText(folder + "/interior/sound.sui"));
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in raw)
            {
                string source = EtsGameSound.ResolveSoundref(files, kv.Value);
                if (source != null) map[kv.Key] = source;
            }
            return map;
        }

        private static void AddPart(EtsFileSystem files, Dictionary<string, string> raw, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            EtsSiiFile f = files.ReadSii(path);
            if (f == null) return;
            foreach (EtsSiiUnit u in f.Units)
            {
                List<string> list = u.GetArray("sounds");
                if (list == null) continue;
                foreach (string entry in list) Add(raw, entry, true);
            }
        }

        private static void AddText(Dictionary<string, string> raw, string text)
        {
            if (text == null) return;
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (!t.StartsWith("sounds[]", StringComparison.Ordinal)) continue;
                int q1 = t.IndexOf('"'), q2 = q1 >= 0 ? t.IndexOf('"', q1 + 1) : -1;
                if (q2 > q1) Add(raw, t.Substring(q1 + 1, q2 - q1 - 1), true);
            }
        }

        private static void Add(Dictionary<string, string> raw, string entry, bool overwrite)
        {
            if (entry == null) return;
            entry = EtsSiiValue.Unquote(entry.Trim());
            int bar = entry.IndexOf('|');
            if (bar <= 0) return;
            string name = entry.Substring(0, bar).Trim(), value = entry.Substring(bar + 1).Trim();
            if (overwrite || !raw.ContainsKey(name)) raw[name] = value;
        }
    }
}
