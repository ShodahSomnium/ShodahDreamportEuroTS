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

        /// <summary>[instrument][playlist entry]; an entry is null when its sample was skipped, failed or is streamed.</summary>
        public AudioClip[][] Clips { get; internal set; }

        /// <summary>The FSB5 sample names, like Clips.</summary>
        public string[][] SampleNames { get; internal set; }

        /// <summary>[instrument]: the long looping sample this instrument streams (EtsGameStream), or null.</summary>
        public EtsGameStreamSpec[] Streams { get; internal set; }

        public float[] Values { get; internal set; }
        public bool Ready { get; internal set; }
        public string Failure { get; internal set; }
        public bool Failed => Failure != null;

        internal readonly float MaxSeconds;
        internal readonly bool WavesOnly;
        internal readonly bool Stream;
        internal readonly string[] FixedNames;
        internal readonly float[] FixedValues;
        internal long[][] ClipKeys;
        internal bool[] StreamMask;
        internal float Seconds;
        /// <summary>Players alive on this event, and since when none is (EtsGameSound evicts idle ambience events).</summary>
        internal int Users;
        internal float IdleSince;
        internal string CacheKey;

        internal EtsGameEvent(string source, string bank, string path, float maxSeconds, bool wavesOnly, string[] fixedNames, float[] fixedValues, bool stream = false)
        {
            Source = source;
            BankPath = bank;
            EventPath = path;
            MaxSeconds = maxSeconds;
            WavesOnly = wavesOnly;
            FixedNames = fixedNames;
            FixedValues = fixedValues;
            Stream = stream;
        }

        /// <summary>Sets every parameter of this event with that name (no allocation).</summary>
        public void Set(string name, float value)
        {
            if (Event == null || Values == null) return;
            List<int> ps = Event.Params;
            for (int i = 0; i < ps.Count; i++)
                if (string.Equals(Studio.Params[ps[i]].Name, name, StringComparison.Ordinal)) Values[ps[i]] = value;
        }

        /// <summary>True when the event has a parameter of that name.</summary>
        public bool HasParam(string name)
        {
            if (Event == null) return false;
            for (int i = 0; i < Event.Params.Count; i++)
                if (string.Equals(Studio.Params[Event.Params[i]].Name, name, StringComparison.Ordinal)) return true;
            return false;
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

        /// <summary>A clip of the instrument (a playlist entry by the bank's weights, silence left out), or null.</summary>
        public AudioClip PickClip(int instrument)
        {
            return PickEntry(instrument, false, out AudioClip clip) ? clip : null;
        }

        /// <summary>
        /// One playlist entry by the bank's weights: true with a clip, or false - with a null clip - when the draw fell on
        /// the playlist's silence (withSilence) or nothing is loaded.
        /// </summary>
        public bool PickEntry(int instrument, bool withSilence, out AudioClip clip)
        {
            clip = null;
            if (Clips == null || instrument < 0 || instrument >= Clips.Length) return false;
            AudioClip[] c = Clips[instrument];
            if (c == null || c.Length == 0) return false;
            EtsFmodStudio.Instrument ins = Event.Instruments[instrument];
            List<float> w = ins.Weights;
            float total = withSilence ? ins.SilenceWeight : 0f;
            for (int k = 0; k < c.Length; k++) if (c[k] != null) total += k < w.Count ? Math.Max(0f, w[k]) : 1f;
            if (total <= 0f)
            {
                for (int k = 0; k < c.Length; k++) if (c[k] != null) { clip = c[k]; return true; }
                return false;
            }
            float r = UnityEngine.Random.value * total;
            for (int k = 0; k < c.Length; k++)
            {
                if (c[k] == null) continue;
                r -= k < w.Count ? Math.Max(0f, w[k]) : 1f;
                if (r <= 0f) { clip = c[k]; return true; }
            }
            if (withSilence && ins.SilenceWeight > 0f) return false;
            for (int k = c.Length - 1; k >= 0; k--) if (c[k] != null) { clip = c[k]; return true; }
            return false;
        }

        /// <summary>
        /// Plays the event once on <paramref name="source"/>: every instrument that sounds at the current parameter values
        /// (its region, crossfades and track levels) - a playlist entry by weight for a multi instrument, which may be its
        /// silence - at its own gain. Scatterers are left out (EtsGameEventPlayer spawns them). Instruments whose sample
        /// names contain <paramref name="skip"/> are left out. True when anything played.
        /// </summary>
        public bool PlayOnce(AudioSource source, float level, string skip = null)
        {
            if (!Ready || source == null || !source.isActiveAndEnabled) return false;
            bool any = false;
            for (int i = 0; i < Event.Instruments.Count; i++)
            {
                EtsFmodStudio.Instrument ins = Event.Instruments[i];
                if (ins.IsScatter) continue;
                if (skip != null && NameHas(i, skip)) continue;
                Studio.Evaluate(ins, Values, out float gain, out _);
                if (gain <= 0.0005f) continue;
                if (!PickEntry(i, true, out AudioClip clip)) { any |= ins.SilenceWeight > 0f; continue; }
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

    /// <summary>A long looping sample's packets (copied out of its bank) for EtsGameStream.</summary>
    public sealed class EtsGameStreamSpec
    {
        public string Name;
        public byte[] Packets;
        public int Channels, Rate, Frames;
        public int Coded; // channels in the Vorbis stream (6 for the 5.1 beds, mixed down to Channels)
        public uint SetupCrc;
        public float Seconds => Rate > 0 ? Frames / (float)Rate : 0f;
        /// <summary>Events holding it, and its sample in its bank (EtsGameSound frees it with the last).</summary>
        internal int Refs;
        internal int Sample;
    }

    /// <summary>
    /// Streams a long looping sample (an ambience bed of 40-90 s) through a 4 s ring AudioClip instead of decoding it
    /// whole: its Vorbis packets stay compressed in memory (EtsGameStreamSpec, 3-7 MB for the longest beds instead of
    /// 12-18 MB of float PCM), a worker thread ('Ets sound stream') decodes half a second at a time (EtsFmodBank.LoopReader,
    /// round and round), and the main thread writes each half second into the ring just behind where the AudioSource is
    /// playing it (AudioClip.SetData on the looping clip, which Unity reads as it plays). At most two writes a frame
    /// (2 x 24000 floats for 48 kHz mono); buffers are recycled. Needs Pump every frame while it plays.
    /// </summary>
    public sealed class EtsGameStream
    {
        public const int Blocks = 8;
        private static EtsWorker s_worker;
        private static readonly Func<bool> s_all = () => true;

        public static int Live { get; private set; }
        public static long PacketBytes { get; private set; }
        private static bool s_logged;

        private readonly EtsGameStreamSpec _spec;
        private readonly AudioSource _source;
        private readonly AudioClip _ring;
        private readonly int _block, _ringFrames;
        private EtsFmodBank.LoopReader _reader; // worker thread only
        private readonly Queue<float[]> _ready = new Queue<float[]>();
        private readonly Stack<float[]> _free = new Stack<float[]>();
        private int _inFlight;
        private long _written, _wraps;
        private int _lastPos;
        private bool _playing, _dead;

        public EtsGameStream(EtsGameStreamSpec spec, AudioSource source)
        {
            _spec = spec;
            _source = source;
            _block = Math.Max(1024, spec.Rate / 2);
            _ringFrames = _block * Blocks;
            _ring = AudioClip.Create("ets stream " + spec.Name, _ringFrames, spec.Channels, spec.Rate, false);
            Live++;
            PacketBytes += spec.Packets != null ? spec.Packets.Length : 0;
            if (!s_logged)
            {
                s_logged = true;
                Debug.Log("[SomniumEuroTruck] Game sound: streaming the game's long ambience beds (first: " + spec.Name + ", " + spec.Seconds.ToString("0") + " s, "
                    + (spec.Packets != null ? (spec.Packets.Length / 1048576.0).ToString("0.0") : "0") + " MB of Vorbis packets kept, a " + (_ringFrames / (float)spec.Rate).ToString("0") + " s ring decoded on 'Ets sound stream').");
            }
        }

        public bool Playing => _playing;

        /// <summary>Starts playing once the first blocks are decoded (false until then - call again next frame).</summary>
        public bool Start()
        {
            if (_dead) return false;
            if (_playing) return true;
            Request();
            if (_ready.Count < 3) return false;
            _wraps = 0;
            _lastPos = 0;
            _written = 0;
            for (int i = 0; i < 3; i++) Write(_ready.Dequeue());
            _source.Stop();
            _source.clip = _ring;
            _source.loop = true;
            _source.timeSamples = 0;
            _source.Play();
            _playing = true;
            return true;
        }

        public void Stop()
        {
            if (_playing && _source != null) _source.Stop();
            _playing = false;
        }

        /// <summary>Every frame: keep the ring ahead of the play head and the worker busy.</summary>
        public void Pump()
        {
            if (_dead) return;
            Request();
            if (!_playing || _source == null) return;
            int pos = _source.timeSamples;
            if (pos < _lastPos) _wraps++;
            _lastPos = pos;
            long playBlock = (_wraps * _ringFrames + pos) / _block;
            if (_written <= playBlock) _written = playBlock + 1; // starved: never write behind the head
            int writes = 0;
            while (_ready.Count > 0 && _written < playBlock + Blocks - 1 && writes < 2)
            {
                Write(_ready.Dequeue());
                writes++;
            }
        }

        private void Write(float[] buf)
        {
            _ring.SetData(buf, (int)(_written % Blocks) * _block);
            _free.Push(buf);
            _written++;
        }

        private void Request()
        {
            if (s_worker == null) s_worker = new EtsWorker("Ets sound stream");
            while (_ready.Count + _inFlight < 4)
            {
                float[] buf = _free.Count > 0 ? _free.Pop() : new float[_block * _spec.Channels];
                _inFlight++;
                s_worker.Post(() => { Fill(buf); return buf; }, (result, error) =>
                {
                    _inFlight--;
                    if (error == null && !_dead) _ready.Enqueue(buf);
                    else _free.Push(buf);
                });
            }
        }

        /// <summary>Worker thread.</summary>
        private void Fill(float[] buf)
        {
            if (_reader == null) _reader = new EtsFmodBank.LoopReader(_spec.Packets, _spec.Channels, _spec.Rate, _spec.SetupCrc, _spec.Coded);
            _reader.Read(buf);
        }

        public void Destroy()
        {
            if (_dead) return;
            Stop();
            _dead = true;
            Live--;
            PacketBytes -= _spec.Packets != null ? _spec.Packets.Length : 0;
            if (_source != null && ReferenceEquals(_source.clip, _ring)) _source.clip = null;
            UnityEngine.Object.Destroy(_ring);
        }

        /// <summary>Main thread, every frame (EtsGameSound.Pump).</summary>
        internal static void PumpWorker()
        {
            s_worker?.Drain(s_all);
        }
    }

    /// <summary>
    /// A few 3D AudioSources for the one-shots scatterer instruments spawn round their event (birds, dogs, gulls, the
    /// forest's owls): each plays one call where it is put, falling to silence at its own max distance (the game's
    /// sound_item_data max_dist for map sounds) along an inverse curve tapered to zero there.
    /// </summary>
    public sealed class EtsGameShotPool
    {
        private readonly AudioSource[] _sources;
        private static AnimationCurve s_curve;

        /// <summary>A multiplier on every shot's volume (the cab's damping, the listener's ambience level).</summary>
        public float Gain = 1f;

        public int Played { get; private set; }

        public EtsGameShotPool(Transform parent, int count, int priority)
        {
            _sources = new AudioSource[count];
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("ets sound shot " + i);
                go.transform.SetParent(parent, false);
                AudioSource s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.loop = false;
                s.spatialBlend = 1f;
                s.dopplerLevel = 0f;
                s.priority = priority;
                ApplyRolloff(s, 100f);
                _sources[i] = s;
            }
        }

        /// <summary>A source falling to zero at <paramref name="maxDistance"/> (full within a tenth of it, then about 1/d).</summary>
        public static void ApplyRolloff(AudioSource s, float maxDistance)
        {
            if (s_curve == null)
            {
                s_curve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.1f, 1f), new Keyframe(0.2f, 0.5f), new Keyframe(0.4f, 0.24f),
                    new Keyframe(0.7f, 0.09f), new Keyframe(1f, 0f));
            }
            s.rolloffMode = AudioRolloffMode.Custom;
            s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, s_curve);
            s.minDistance = 1f;
            s.maxDistance = Mathf.Max(5f, maxDistance);
        }

        public bool Play(AudioClip clip, Vector3 at, float volume, float pitch, float maxDistance)
        {
            if (clip == null) return false;
            float v = volume * Gain;
            if (v <= 0.0005f) return false;
            for (int i = 0; i < _sources.Length; i++)
            {
                AudioSource s = _sources[i];
                if (s == null || s.isPlaying) continue;
                s.transform.position = at;
                if (Mathf.Abs(s.maxDistance - maxDistance) > 0.5f) s.maxDistance = Mathf.Max(5f, maxDistance);
                s.clip = clip;
                s.volume = Mathf.Clamp01(v);
                s.pitch = pitch;
                s.Play();
                Played++;
                return true;
            }
            return false;
        }

        public void StopAll()
        {
            for (int i = 0; i < _sources.Length; i++) if (_sources[i] != null && _sources[i].isPlaying) _sources[i].Stop();
        }
    }

    /// <summary>
    /// Plays a game event the way FMOD does for its parameter sheets and timeline (no FMOD here: one AudioSource per
    /// looping instrument), every frame in Drive, without allocation:
    ///  - looping instruments: evaluated at the event's parameter values - in their region or silent, their crossfade
    ///    automations, their track's dB automations, their auto-pitch - which set that source's volume and pitch; a loop
    ///    starts at a random point when it becomes audible and stops when it falls silent. Looping instruments on the
    ///    timeline (a horn, a reversing beeper) play while TimelineActive. A long looping sample the event was asked to
    ///    stream plays through EtsGameStream. A looping multi instrument with silence in its playlist (the game's bird
    ///    songs) plays entry after entry by weight, waiting out the silence entries' length;
    ///  - scatterer instruments (with Shots set): every spawn interval (a random time between the bank's min and max)
    ///    one entry of its playlist - or its silence - at a random spot within its scatter distance of Anchor, up to its
    ///    polyphony at once;
    ///  - non-looping instruments on a sheet (church bells by the hour, brake squeal shots): once each time the event's
    ///    values bring them in (their gain rises from silence), as FMOD triggers an instrument entering its region.
    /// </summary>
    public sealed class EtsGameEventPlayer
    {
        public readonly EtsGameEvent Ev;
        public readonly AudioSource[] Sources;
        public bool TimelineActive;
        /// <summary>Below this (about -42 dB, where FMOD tracks park their idle layers) a loop is stopped to save a voice.</summary>
        private const float Silent = 0.008f;
        public int Playing { get; private set; }

        /// <summary>Scatterers spawn here (null: scatterers stay silent).</summary>
        public EtsGameShotPool Shots;
        public Vector3 Anchor;
        /// <summary>The distance at which a spawned call is silent (map sounds: the item's max_dist).</summary>
        public float ShotMaxDistance = 100f;

        private readonly EtsGameStream[] _streams;
        private readonly bool[] _sequence;
        private readonly float[] _wait;
        private readonly bool[] _wasIn;
        private readonly float[][] _live;
        private AudioSource _shot;
        private readonly Func<AudioSource> _newSource;
        private uint _rng;
        private bool _destroyed;

        public EtsGameEventPlayer(EtsGameEvent ev, Func<AudioSource> newSource)
        {
            Ev = ev;
            _newSource = newSource;
            int n = ev.Event.Instruments.Count;
            Sources = new AudioSource[n];
            _streams = new EtsGameStream[n];
            _sequence = new bool[n];
            _wait = new float[n];
            _wasIn = new bool[n];
            _live = new float[n][];
            _rng = (uint)ev.Source.GetHashCode() ^ (uint)UnityEngine.Random.Range(1, int.MaxValue);
            ev.Users++;
            for (int i = 0; i < n; i++)
            {
                EtsFmodStudio.Instrument ins = ev.Event.Instruments[i];
                if (ins.IsScatter)
                {
                    _live[i] = new float[Mathf.Clamp(ins.ScatterPolyphony, 1, 16)];
                    _wait[i] = Time.time + Rand() * ins.ScatterMax;
                    continue;
                }
                if (!ins.Loop) continue; // one-shots: PlayOnce, or the sheet one-shots below
                EtsGameStreamSpec spec = ev.Streams != null && i < ev.Streams.Length ? ev.Streams[i] : null;
                if (spec == null && ev.PickClip(i) == null) continue;
                AudioSource s = newSource();
                Sources[i] = s;
                if (spec != null)
                {
                    _streams[i] = new EtsGameStream(spec, s);
                    continue;
                }
                _sequence[i] = ins.IsMulti && ins.SilenceWeight > 0f;
                s.loop = !_sequence[i];
                s.clip = ev.PickClip(i);
            }
        }

        private float Rand()
        {
            _rng = _rng * 1664525u + 1013904223u;
            return (_rng >> 8) / 16777216f;
        }

        public void Drive(float master)
        {
            int playing = 0;
            EtsFmodStudio studio = Ev.Studio;
            List<EtsFmodStudio.Instrument> list = Ev.Event.Instruments;
            float now = Time.time;
            for (int i = 0; i < list.Count; i++)
            {
                EtsFmodStudio.Instrument ins = list[i];
                AudioSource s = Sources[i];
                if (s == null)
                {
                    if (ins.IsScatter) Scatter(i, ins, master, now);
                    else if (!ins.Loop && !ins.OnTimeline && ins.RegionParam >= 0) SheetShot(i, ins, master);
                    continue;
                }
                float gain = 0f, pitch = 1f;
                if (!ins.OnTimeline || TimelineActive) studio.Evaluate(ins, Ev.Values, out gain, out pitch);
                float v = gain * master;
                EtsGameStream stream = _streams[i];
                if (v > Silent)
                {
                    s.volume = v > 1f ? 1f : v;
                    s.pitch = pitch < 0.05f ? 0.05f : pitch > 3f ? 3f : pitch;
                    if (stream != null)
                    {
                        stream.Start();
                        stream.Pump();
                    }
                    else if (_sequence[i])
                    {
                        if (!s.isPlaying && now >= _wait[i])
                        {
                            if (Ev.PickEntry(i, true, out AudioClip clip)) { s.clip = clip; s.Play(); }
                            else _wait[i] = now + ins.SilenceSeconds * (0.5f + Rand());
                        }
                    }
                    else if (!s.isPlaying)
                    {
                        if (s.clip != null && !ins.OnTimeline) s.time = Rand() * s.clip.length * 0.9f;
                        s.Play();
                    }
                    playing++;
                }
                else if (stream != null)
                {
                    if (stream.Playing) stream.Stop();
                    stream.Pump(); // keeps the next blocks decoded
                }
                else if (s.isPlaying)
                {
                    s.Stop();
                }
            }
            Playing = playing;
        }

        private void Scatter(int i, EtsFmodStudio.Instrument ins, float master, float now)
        {
            if (Shots == null || now < _wait[i]) return;
            _wait[i] = now + ins.ScatterMin + (ins.ScatterMax - ins.ScatterMin) * Rand();
            if (master <= 0.0005f || (ins.OnTimeline && !TimelineActive)) return;
            Ev.Studio.Evaluate(ins, Ev.Values, out float gain, out float pitch);
            if (gain * master <= 0.0005f) return;
            float[] live = _live[i];
            int slot = -1;
            for (int k = 0; k < live.Length; k++) if (live[k] <= now) { slot = k; break; }
            if (slot < 0) return; // its polyphony is full
            if (!Ev.PickEntry(i, true, out AudioClip clip)) return; // the playlist's silence came up
            float angle = Rand() * Mathf.PI * 2f;
            float d = Mathf.Max(2f, ins.ScatterDistance) * Mathf.Sqrt(Rand());
            var at = new Vector3(Anchor.x + Mathf.Cos(angle) * d, Anchor.y + 1f + 4f * Rand(), Anchor.z + Mathf.Sin(angle) * d);
            if (Shots.Play(clip, at, gain * master, pitch, ShotMaxDistance)) live[slot] = now + clip.length / Mathf.Max(0.1f, pitch);
        }

        private void SheetShot(int i, EtsFmodStudio.Instrument ins, float master)
        {
            Ev.Studio.Evaluate(ins, Ev.Values, out float gain, out _);
            bool inside = gain > 0.0005f;
            if (inside && !_wasIn[i] && master > 0.0005f)
            {
                if (_shot == null && _newSource != null)
                {
                    _shot = _newSource();
                    _shot.loop = false;
                    _shot.volume = 1f; // PlayOneShot's level multiplies the source's own volume
                }
                if (_shot != null && Ev.PickEntry(i, true, out AudioClip clip)) _shot.PlayOneShot(clip, Mathf.Clamp01(gain * master));
            }
            _wasIn[i] = inside;
        }

        public void Stop()
        {
            for (int i = 0; i < Sources.Length; i++)
            {
                if (_streams[i] != null) _streams[i].Stop();
                else if (Sources[i] != null && Sources[i].isPlaying) Sources[i].Stop();
            }
            Playing = 0;
        }

        public void SetSpatialBlend(float blend)
        {
            for (int i = 0; i < Sources.Length; i++)
                if (Sources[i] != null) Sources[i].spatialBlend = blend;
            if (_shot != null) _shot.spatialBlend = blend;
        }

        /// <summary>Every source this player made (loops and its one-shot source), for setting their 3D settings.</summary>
        public void ForEachSource(Action<AudioSource> act)
        {
            for (int i = 0; i < Sources.Length; i++) if (Sources[i] != null) act(Sources[i]);
            if (_shot != null) act(_shot);
        }

        public void Destroy()
        {
            if (!_destroyed)
            {
                _destroyed = true;
                Ev.Users--;
                Ev.IdleSince = Time.time;
            }
            for (int i = 0; i < Sources.Length; i++)
            {
                _streams[i]?.Destroy();
                _streams[i] = null;
                if (Sources[i] != null) UnityEngine.Object.Destroy(Sources[i]);
                Sources[i] = null;
            }
            if (_shot != null) UnityEngine.Object.Destroy(_shot);
            _shot = null;
        }
    }

    /// <summary>
    /// Loads the game's own sounds from the player's install at run time (nothing is shipped in the world): FMOD Studio
    /// banks are read from the archives, their FSB5 Vorbis samples decoded (EtsFmodBank: headers rebuilt, vendored
    /// NVorbis) and their event metadata parsed (EtsFmodStudio) on a worker thread ('Ets sound decode'); the main thread
    /// turns the PCM into AudioClips (AudioClip.Create + SetData - the route the Somnium client plays; OnAudioFilterRead
    /// is never called for world code) a slice at a time, at most <see cref="UploadFloatsPerFrame"/> floats a frame.
    /// Each bank is read once per batch of requests; each sample is decoded once and its clip shared by every event and
    /// every player that uses it. A timeline instrument that plays only part of its sample (ui.bank's click: 18 ms of a
    /// 7.9 s take) gets its own clip of just that part. Long looping samples of events asked to stream are not decoded
    /// here: their packets are kept for EtsGameStream. Pump runs once a frame (EtsTruckAudio/EtsCarAudio/EtsAmbience call it).
    /// </summary>
    public static class EtsGameSound
    {
        /// <summary>Off = everything stays synthesised (the fallback the sound components keep).</summary>
        public static bool Enabled = true;

        /// <summary>The upload budget: 2 x 65536 floats (about 0.5 MB of PCM, a few tenths of a millisecond) a frame.</summary>
        public const int UploadFloatsPerFrame = 131072;

        /// <summary>A looping waveform longer than this is streamed when its event was asked to stream.</summary>
        public const float StreamSeconds = 12f;

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
            public readonly Dictionary<long, AudioClip> Clips = new Dictionary<long, AudioClip>();
            public readonly HashSet<long> Failed = new HashSet<long>();
            public readonly HashSet<long> Claimed = new HashSet<long>(); // under lock(Claimed): decoded or being decoded
            public readonly Dictionary<int, EtsGameStreamSpec> Streams = new Dictionary<int, EtsGameStreamSpec>(); // under lock(Streams)
            public readonly Dictionary<long, int> Refs = new Dictionary<long, int>(); // clip key -> events holding it
        }

        private sealed class Decoded
        {
            public BankState Bank;
            public long Key;
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
            public readonly Dictionary<EtsGameEvent, long[][]> Keys = new Dictionary<EtsGameEvent, long[][]>();
            public readonly Dictionary<EtsGameEvent, bool[]> Streamed = new Dictionary<EtsGameEvent, bool[]>();
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
        /// instruments whose region excludes that value are never decoded; stream keeps looping waveforms longer than
        /// StreamSeconds compressed for EtsGameStream instead of decoding them (maxSeconds then caps only the rest). Null
        /// when the source is malformed, game sounds are off or the archives are not mounted yet (nothing is cached then -
        /// ask again later).
        /// </summary>
        public static EtsGameEvent Get(string source, float maxSeconds = 0f, bool wavesOnly = false, string fixedParams = null, bool stream = false)
        {
            if (string.IsNullOrEmpty(source)) return null;
            string key = stream ? source + "|stream" : source;
            if (s_events.TryGetValue(key, out EtsGameEvent ev)) return ev;
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
            ev = new EtsGameEvent(source, bank, path, maxSeconds, wavesOnly, names, values, stream);
            ev.CacheKey = key;
            s_events[key] = ev;

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

        /// <summary>A .soundref or bank#event resolved and loaded (Get), or null. Main thread (the archive read is small).</summary>
        public static EtsGameEvent GetRef(string reference, float maxSeconds = 0f, bool wavesOnly = false, string fixedParams = null, bool stream = false)
        {
            if (EtsEngine.Files == null || !Enabled) return null;
            return Get(ResolveSoundref(EtsEngine.Files, reference), maxSeconds, wavesOnly, fixedParams, stream);
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

        /// <summary>
        /// The clip an instrument plays of a sample: the sample itself, or - for a non-looping waveform instrument that
        /// starts into its sample or ends before it does (its timeline length) - that part alone, keyed apart.
        /// </summary>
        private static long ClipKey(EtsFmodStudio.Instrument ins, int sample, int frames, out int start, out int length)
        {
            start = 0;
            length = 0;
            if (ins.Loop || ins.IsMulti || ins.Samples.Count != 1 || frames <= 0) return sample;
            int s = Math.Min(Math.Max(0, ins.StartFrames), Math.Max(0, frames - 1));
            int len = ins.LengthFrames > 0 ? Math.Min(ins.LengthFrames, frames - s) : frames - s;
            if (s == 0 && len >= frames - 480) return sample;
            start = s;
            length = Math.Max(1, len);
            return (long)(sample & 0xFFFF) | ((long)(start & 0xFFFFFF) << 16) | ((long)(length & 0xFFFFFF) << 40);
        }

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
                var keys = new long[e.Instruments.Count][];
                var streamed = new bool[e.Instruments.Count];
                for (int i = 0; i < e.Instruments.Count; i++)
                {
                    EtsFmodStudio.Instrument ins = e.Instruments[i];
                    if (!Wanted(studio, ev, ins)) { keys[i] = new long[0]; continue; }
                    keys[i] = new long[ins.Samples.Count];
                    for (int k = 0; k < ins.Samples.Count; k++)
                    {
                        int s = ins.Samples[k];
                        if (s < 0 || s >= fsb.Samples.Count) { keys[i][k] = -1; continue; }
                        EtsFmodBank.Sample sample = fsb.Samples[s];

                        // A long looping bed of an event that streams: keep its packets, decode nothing here.
                        if (ev.Stream && ins.Loop && !ins.IsMulti && sample.Rate > 0 && sample.Frames > StreamSeconds * sample.Rate)
                        {
                            keys[i][k] = -1;
                            if (fsb.DecodesAsNoise(sample))
                            {
                                Debug.LogWarning("[SomniumEuroTruck] Game sound: " + sample.Name + " decodes as static noise; left out.");
                                continue;
                            }
                            streamed[i] = true;
                            lock (b.Streams)
                            {
                                if (!b.Streams.ContainsKey(s))
                                    b.Streams[s] = new EtsGameStreamSpec { Name = sample.Name, Packets = fsb.CopyPackets(sample), Channels = sample.Channels, Coded = sample.Coded, Rate = sample.Rate, Frames = sample.Frames, SetupCrc = sample.SetupCrc, Sample = s };
                            }
                            continue;
                        }

                        long key = ClipKey(ins, s, sample.Frames, out int start, out int length);
                        keys[i][k] = key;
                        lock (b.Claimed)
                        {
                            if (!b.Claimed.Add(key)) continue;
                        }
                        var d = new Decoded { Bank = b, Key = key, Name = sample.Name, Channels = sample.Channels, Rate = sample.Rate };
                        d.Pcm = fsb.Decode(sample, out string derr);
                        if (d.Pcm == null) d.Failure = derr;
                        else if (length > 0) d.Pcm = Trim(d.Pcm, sample.Channels, start, length);
                        else if (ev.MaxSeconds > 0f && ins.Loop) d.Pcm = CapLoop(d.Pcm, sample.Channels, (int)(ev.MaxSeconds * sample.Rate), sample.Rate / 2);
                        else if (ev.MaxSeconds > 0f && !ev.Stream) d.Pcm = CapLoop(d.Pcm, sample.Channels, (int)(ev.MaxSeconds * sample.Rate), sample.Rate / 2);
                        else if (ev.MaxSeconds > 0f) d.Pcm = CutFade(d.Pcm, sample.Channels, (int)(ev.MaxSeconds * 1.5f * sample.Rate), sample.Rate); // an ambience one-shot (a pub's 190 s of music): 30 s at most
                        if (length > 0) d.Name = sample.Name + "@" + start + "+" + length;
                        r.Samples.Add(d);
                    }
                }
                r.Keys[ev] = keys;
                r.Streamed[ev] = streamed;
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

        /// <summary>The part of a sample a timeline instrument plays (start, length in frames), 2 ms faded at the cut end.</summary>
        private static float[] Trim(float[] pcm, int ch, int start, int length)
        {
            int total = pcm.Length / ch;
            start = Math.Min(start, Math.Max(0, total - 1));
            length = Math.Max(1, Math.Min(length, total - start));
            var o = new float[length * ch];
            Array.Copy(pcm, start * ch, o, 0, o.Length);
            int fade = Math.Min(96, length / 4);
            for (int i = 0; i < fade; i++)
            {
                float g = (i + 0.5f) / fade;
                for (int c = 0; c < ch; c++) o[(length - 1 - i) * ch + c] *= g;
            }
            return o;
        }

        /// <summary>The first <paramref name="frames"/> frames, the last <paramref name="fade"/> of them faded out.</summary>
        private static float[] CutFade(float[] pcm, int ch, int frames, int fade)
        {
            int total = pcm.Length / ch;
            if (frames <= 0 || total <= frames) return pcm;
            var o = new float[frames * ch];
            Array.Copy(pcm, o, o.Length);
            fade = Math.Min(fade, frames);
            for (int i = 0; i < fade; i++)
            {
                float g = (i + 0.5f) / fade;
                for (int c = 0; c < ch; c++) o[(frames - 1 - i) * ch + c] *= g;
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
                    b.Failed.Add(d.Key);
                    Debug.Log("[SomniumEuroTruck] Game sound: " + b.Path + " sample '" + d.Name + "' did not decode - " + d.Failure + ".");
                    continue;
                }
                AudioClip clip = AudioClip.Create("ets " + d.Name, d.Pcm.Length / d.Channels, d.Channels, d.Rate, false);
                s_uploads.Enqueue(new Upload { D = d, Clip = clip });
            }
            foreach (EtsGameEvent ev in events)
            {
                EtsFmodStudio.Event e = r.Studio.FindEvent(ev.EventPath);
                if (e == null || !r.Keys.TryGetValue(ev, out long[][] keys)) { ev.Failure = "no event '" + ev.EventPath + "' in " + b.Path; continue; }
                ev.Studio = r.Studio;
                ev.Event = e;
                var values = new float[r.Studio.Params.Count];
                for (int i = 0; i < values.Length; i++) values[i] = float.NaN;
                ev.Values = values;
                if (ev.FixedNames != null) for (int i = 0; i < ev.FixedNames.Length; i++) ev.Set(ev.FixedNames[i], ev.FixedValues[i]);
                ev.ClipKeys = keys;
                ev.StreamMask = r.Streamed[ev];
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
            EtsGameStream.PumpWorker();

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
                    u.D.Bank.Clips[u.D.Key] = u.Clip;
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

            EtsUiSound.Pump();
            if (Time.time >= s_nextSweep) { s_nextSweep = Time.time + 5f; Sweep(); }
        }

        private static bool TryFinish(EtsGameEvent ev)
        {
            BankState b = s_banks[ev.BankPath];
            long[][] ids = ev.ClipKeys;
            bool waiting = false;
            foreach (long[] list in ids)
                foreach (long s in list)
                    if (s >= 0 && !b.Clips.ContainsKey(s) && !b.Failed.Contains(s))
                    {
                        bool claimed;
                        lock (b.Claimed) claimed = b.Claimed.Contains(s);
                        if (claimed) waiting = true;
                    }
            if (waiting) return false;

            var clips = new AudioClip[ids.Length][];
            var names = new string[ids.Length][];
            var streams = new EtsGameStreamSpec[ids.Length];
            int count = 0;
            float seconds = 0f;
            for (int i = 0; i < ids.Length; i++)
            {
                clips[i] = new AudioClip[ids[i].Length];
                names[i] = new string[ids[i].Length];
                if (ev.StreamMask != null && ev.StreamMask[i] && ev.Event.Instruments[i].Samples.Count > 0)
                {
                    lock (b.Streams) b.Streams.TryGetValue(ev.Event.Instruments[i].Samples[0], out streams[i]);
                    if (streams[i] != null) { count++; names[i][0] = streams[i].Name; }
                    continue;
                }
                for (int k = 0; k < ids[i].Length; k++)
                {
                    if (ids[i][k] >= 0 && b.Clips.TryGetValue(ids[i][k], out AudioClip c) && c != null)
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
            ev.Streams = streams;
            ev.Seconds = seconds;
            bool silentOnly = count == 0 && HasSilence(ev);
            if (count == 0 && !silentOnly)
            {
                ev.Failure = "no sample of '" + ev.EventPath + "' decoded";
                Debug.Log("[SomniumEuroTruck] Game sound: " + ev.Source + " unusable - " + ev.Failure + ".");
                return true;
            }
            ev.Ready = true;
            AddRefs(b, ev, 1);
            ev.IdleSince = Time.time;
            int streamed = 0;
            for (int i = 0; i < streams.Length; i++) if (streams[i] != null) streamed++;
            Debug.Log("[SomniumEuroTruck] Game sound: " + ev.Source + " ready - " + ev.Event.Instruments.Count + " instruments, " + count + " samples, "
                + seconds.ToString("0.0") + " s of audio" + (streamed > 0 ? " + " + streamed + " streamed" : "") + " (all game sound so far: " + ClipCount + " clips, " + (PcmBytes / 1048576.0).ToString("0.0") + " MB PCM, decode "
                + DecodeMs.ToString("0") + " ms on the worker).");
            return true;
        }

        private static bool HasSilence(EtsGameEvent ev)
        {
            foreach (EtsFmodStudio.Instrument ins in ev.Event.Instruments) if (ins.SilenceWeight > 0f) return true;
            return false;
        }

        // ---------------------------------------------------------------- freeing what the ambience left behind

        /// <summary>An idle streamed (ambience) event is dropped this long after its last player went.</summary>
        public const float EvictSeconds = 120f;
        private static float s_nextSweep;
        private static readonly List<EtsGameEvent> s_evict = new List<EtsGameEvent>();
        public static int Evicted { get; private set; }

        private static void AddRefs(BankState b, EtsGameEvent ev, int delta)
        {
            var counted = new HashSet<long>();
            foreach (long[] list in ev.ClipKeys)
            {
                foreach (long key in list)
                {
                    if (key < 0 || !counted.Add(key) || !b.Clips.ContainsKey(key)) continue;
                    b.Refs.TryGetValue(key, out int n);
                    n += delta;
                    if (n > 0) { b.Refs[key] = n; continue; }
                    b.Refs.Remove(key);
                    if (delta >= 0) continue;
                    // The last event holding this clip went: free it, and let a later request decode it again.
                    AudioClip clip = b.Clips[key];
                    b.Clips.Remove(key);
                    lock (b.Claimed) b.Claimed.Remove(key);
                    if (clip != null)
                    {
                        PcmBytes -= (long)clip.samples * clip.channels * 4;
                        ClipCount--;
                        UnityEngine.Object.Destroy(clip);
                    }
                }
            }
            if (ev.Streams == null) return;
            foreach (EtsGameStreamSpec spec in ev.Streams)
            {
                if (spec == null) continue;
                spec.Refs += delta;
                if (spec.Refs > 0 || delta >= 0) continue;
                lock (b.Streams)
                {
                    if (b.Streams.TryGetValue(spec.Sample, out EtsGameStreamSpec held) && ReferenceEquals(held, spec)) b.Streams.Remove(spec.Sample);
                }
                spec.Packets = null;
            }
        }

        /// <summary>
        /// Every 5 s: streamed (ambience) events no player has used for EvictSeconds leave the cache, and the clips and
        /// packets only they held are freed - so a long drive past many map sounds does not keep every one of them.
        /// The truck's and the traffic's events (not streamed) stay: their sets are small and come back often.
        /// </summary>
        private static void Sweep()
        {
            float now = Time.time;
            s_evict.Clear();
            foreach (EtsGameEvent ev in s_events.Values)
                if (ev.Stream && ev.Ready && ev.Users <= 0 && now - ev.IdleSince > EvictSeconds) s_evict.Add(ev);
            foreach (EtsGameEvent ev in s_evict)
            {
                s_events.Remove(ev.CacheKey);
                if (s_banks.TryGetValue(ev.BankPath, out BankState b)) AddRefs(b, ev, -1);
                ev.Ready = false;
                ev.Failure = "evicted (idle)";
                Evicted++;
            }
            if (s_evict.Count > 0)
            {
                Debug.Log("[SomniumEuroTruck] Game sound: freed " + s_evict.Count + " idle ambience event(s) (first " + s_evict[0].Source + "); now " + ClipCount + " clips, "
                    + (PcmBytes / 1048576.0).ToString("0.0") + " MB PCM, " + EtsGameStream.Live + " streams.");
            }
        }
    }

    /// <summary>
    /// The game's UI sounds (/sound/ui/ui.bank, the exe's /sound/ui/ui_*.soundref): a 2D source the tablet's and the game
    /// UI's buttons play their press through. "click" is ui_click.soundref = ui.bank#click (18 ms of the click take).
    /// Loaded on the first press (that press is silent while it decodes).
    /// </summary>
    public static class EtsUiSound
    {
        public static float Level = 0.5f;
        private static AudioSource s_source;
        private static EtsGameEvent s_click;
        private static readonly Dictionary<string, EtsGameEvent> s_named = new Dictionary<string, EtsGameEvent>(StringComparer.Ordinal);
        private static bool s_logged;

        /// <summary>A button press: the game's ui_click.</summary>
        public static void Click()
        {
            if (s_click == null) s_click = EtsGameSound.GetRef("/sound/ui/ui_click.soundref");
            Play(s_click);
        }

        /// <summary>A UI sound by its soundref name ("ui_positive", "ui_money", "ui_error" ...).</summary>
        public static void Play(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (!s_named.TryGetValue(name, out EtsGameEvent ev) || ev == null)
            {
                ev = EtsGameSound.GetRef("/sound/ui/" + name + ".soundref");
                s_named[name] = ev;
            }
            Play(ev);
        }

        private static void Play(EtsGameEvent ev)
        {
            if (ev == null || !ev.Ready) return;
            if (s_source == null)
            {
                var go = new GameObject("Ets ui sound");
                s_source = go.AddComponent<AudioSource>();
                s_source.playOnAwake = false;
                s_source.spatialBlend = 0f;
                s_source.priority = 40;
            }
            if (ev.PlayOnce(s_source, Level) && !s_logged)
            {
                s_logged = true;
                Debug.Log("[SomniumEuroTruck] UI sound: the game's " + ev.Source + " plays on button presses.");
            }
        }

        /// <summary>Warms the click up so the first press is heard.</summary>
        internal static void Pump()
        {
            if (s_click == null && EtsEngine.Files != null && Time.frameCount % 120 == 7) s_click = EtsGameSound.GetRef("/sound/ui/ui_click.soundref");
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
            "rain_noise|/sound/truck/default/noise_rain.soundref",
            "noise|/sound/truck/default/interior_noise.soundref",
            // Not in any truck definition - the exe's own defaults (/sound/truck/default/*.soundref): the air horn (the
            // game's second horn, played without an air-horn accessory), suspension noise, brake squeal.
            "air_horn|/sound/truck/default/air_horn_default.soundref",
            "suspension|/sound/truck/default/noise_suspension.soundref",
            "brake_squeal|/sound/truck/default/brake_squeal.soundref",
            // The tyres are not in any definition: the exe builds "/sound/tires/" + the surface + ".soundref".
            "tires_asphalt|/sound/tires/asphalt.soundref",
            "tires_gravel|/sound/tires/gravel.soundref",
            "tires_grass|/sound/tires/grass.soundref",
            // The trailer's (the exe's /sound/trailer/ defaults; a trailer chassis may name its own brace sounds).
            "trailer_tires_asphalt|/sound/trailer/tires/asphalt.soundref",
            "trailer_tires_gravel|/sound/trailer/tires/gravel.soundref",
            "trailer_tires_grass|/sound/trailer/tires/grass.soundref",
            "trailer_suspension|/sound/trailer/default/trailer_noise_suspension.soundref",
            "trailer_brake_squeal|/sound/trailer/default/trailer_brake_squeal.soundref",
            "trailer_brace_up|/sound/trailer/default/trailer_brace_up.soundref",
            "trailer_brace_down|/sound/trailer/default/trailer_brace_down.soundref",
            // Crashes (/sound/collisions/*.soundref).
            "crash_hard|/sound/collisions/crash_hard.soundref",
            "crash_metal|/sound/collisions/crash_metal.soundref",
            "crash_soft|/sound/collisions/crash_soft.soundref",
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
