using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// ETS2's radio: the game's internet stations, played in the world.
    ///
    /// The stations are the game's own list, read at run time and never shipped: /def/live_streams.sii in the player's
    /// install, then the copy the game keeps in its home folder (Documents\Euro Truck Simulator 2\live_streams.sii, the
    /// one the full game refreshes). Each entry is a "stream_data[]" string "URL|name|genre|language|bitrate|favourite"
    /// (the 1.61 demo's /def copy is only the empty template - "URL of the live stream | Stream Name represented in UI" -
    /// and so is a home copy the demo never filled). When the game gives no station at all, a short list of long-running
    /// public MP3 streams stands in (World list below), so the radio is never silent for want of a list.
    ///
    /// The sound goes the one way proven to be heard in the Somnium client (AomMusic in the Age of Mythology world):
    /// UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG) with DownloadHandlerAudioClip.streamAudio = true into
    /// a plain 2D AudioSource. No OnAudioFilterRead, no PCMReaderCallback. A live stream never finishes downloading, so
    /// unlike AomMusic's files the request is not awaited: the clip is taken and played once the first StartBytes are
    /// in. When the endless stream's clip ends or stalls, or the request errors, the station is tuned in again; a station
    /// that never plays is marked unavailable and the next MP3 station is tried.
    ///
    /// Off until the player turns it on (the tablet's Media > Radio page, or Drive's "Radio on / off"); opening the
    /// radio page turns it on unless the player switched it off.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EtsRadio : MonoBehaviour
    {
        public sealed class Station
        {
            public string Url;
            public string Name;
            public string Genre;
            public string Language;
            public string Bitrate;
            public bool Favourite;

            /// <summary>"game" (/def), "home" (the game's home copy) or "world" (the stand-in list).</summary>
            public string Source;

            /// <summary>Would not play this session; skipped by Next / Previous and the automatic skip.</summary>
            public bool Failed;
        }

        public static EtsRadio Current { get; private set; }

        /// <summary>Bytes of a stream in before its clip is played (about 3 s at 128 kbit/s).</summary>
        private const ulong StartBytes = 48 * 1024;

        /// <summary>Seconds a station has to deliver StartBytes before it counts as failed.</summary>
        private const float ConnectSeconds = 12f;

        /// <summary>Seconds of silence while tuned in before the station is tuned in again.</summary>
        private const float StallSeconds = 3f;

        /// <summary>A stream's clip keeps what it downloaded: tuned in again after this long to let that go.</summary>
        private const float RefreshSeconds = 30f * 60f;

        /// <summary>
        /// The stand-in list, used only when the game's own live_streams.sii lists nothing: public stations that have
        /// streamed MP3 at these addresses for years (not game data).
        /// </summary>
        private static readonly string[] WorldList =
        {
            "https://stream.radioparadise.com/mp3-128|Radio Paradise|Eclectic rock|EN|128",
            "https://stream.srg-ssr.ch/m/rsp/mp3_128|Radio Swiss Pop|Pop|DE|128",
            "https://ice1.somafm.com/bootliquor-128-mp3|SomaFM Boot Liquor|Country / Americana|EN|128",
            "https://ice1.somafm.com/u80s-128-mp3|SomaFM Underground 80s|80s|EN|128",
            "https://ice1.somafm.com/seventies-128-mp3|SomaFM Left Coast 70s|70s rock|EN|128",
            "https://ice1.somafm.com/groovesalad-128-mp3|SomaFM Groove Salad|Chill|EN|128",
            "https://ice1.somafm.com/indiepop-128-mp3|SomaFM Indie Pop Rocks|Indie|EN|128",
            "https://ice1.somafm.com/metal-128-mp3|SomaFM Metal Detector|Metal|EN|128",
            "https://icecast.radiofrance.fr/fip-midfi.mp3|FIP|Eclectic|FR|128",
            "https://stream.srg-ssr.ch/m/rsj/mp3_128|Radio Swiss Jazz|Jazz|DE|128",
            "https://stream.srg-ssr.ch/m/rsc_de/mp3_128|Radio Swiss Classic|Classical|DE|128",
            "https://ice1.somafm.com/secretagent-128-mp3|SomaFM Secret Agent|Lounge|EN|128",
        };

        // stream_data[]: "..." or stream_data[3]: "..." at the start of a line; a line commented with # or // does not match.
        private static readonly Regex EntryLine = new Regex("^[ \\t]*(\\w+)[ \\t]*\\[[ \\t]*\\d*[ \\t]*\\][ \\t]*:[ \\t]*\"([^\"]*)\"", RegexOptions.Multiline);

        private readonly List<Station> _stations = new List<Station>();
        private AudioSource _source;
        private UnityWebRequest _request;
        private AudioClip _clip;
        private int _generation;
        private bool _loadedFromArchive;
        private bool _loadedOnce;
        private float _volume = 0.5f;

        public IReadOnlyList<Station> Stations => _stations;

        /// <summary>The station tuned (or last tuned); -1 before the list is read.</summary>
        public int Index { get; private set; } = -1;

        /// <summary>The player wants the radio on (it may still be tuning in).</summary>
        public bool On { get; private set; }

        /// <summary>The player switched it off: opening the radio page does not turn it back on.</summary>
        public bool UserStopped { get; private set; }

        /// <summary>Sound is coming out now.</summary>
        public bool Playing => _source != null && _source.isPlaying;

        /// <summary>One line for the tablet: what is playing, tuning in, or why nothing is.</summary>
        public string Status { get; private set; } = "Radio off";

        /// <summary>Whether the stations are the game's (false: the world's stand-in list).</summary>
        public bool FromGame { get; private set; }

        /// <summary>0..1.</summary>
        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Mathf.Clamp01(value);

                if (_source != null)
                {
                    _source.volume = _volume;
                }
            }
        }

        /// <summary>Gets or adds the radio on the bootstrap object ('Ets Net', beside EtsTabletApp).</summary>
        public static EtsRadio Ensure(GameObject host)
        {
            if (Current != null)
            {
                return Current;
            }

            if (host == null)
            {
                return null;
            }

            EtsRadio radio = host.GetComponent<EtsRadio>();
            return radio != null ? radio : host.AddComponent<EtsRadio>();
        }

        private void Awake()
        {
            if (Current == null)
            {
                Current = this;
            }
        }

        private void OnDestroy()
        {
            _generation++;
            Release();

            if (Current == this)
            {
                Current = null;
            }
        }

        private AudioSource Source()
        {
            if (_source == null)
            {
                _source = gameObject.AddComponent<AudioSource>();
                _source.playOnAwake = false;
                _source.loop = false;
                _source.spatialBlend = 0f; // 2D: the radio is in the player's ears wherever they stand
                _source.priority = 32;
                _source.volume = _volume;
            }

            return _source;
        }

        // ------------------------------------------------------------------ the list

        /// <summary>
        /// Reads the stations if they are not read yet (and once more when the install is mounted after a read that had
        /// no archive). Cheap once read.
        /// </summary>
        public void LoadStations()
        {
            bool mounted = EtsEngine.IsMounted;

            if (_loadedOnce && (_loadedFromArchive || !mounted))
            {
                return;
            }

            if (_loadedOnce && FromGame)
            {
                return; // a home list already found; the archive would only add the template's nothing
            }

            _loadedOnce = true;
            _loadedFromArchive = mounted;
            string current = Index >= 0 && Index < _stations.Count ? _stations[Index].Url : null;
            var found = new List<Station>();
            int fromDef = 0, fromHome = 0;

            try
            {
                string text = mounted ? EtsEngine.Files.ReadText("/def/live_streams.sii") : null;
                fromDef = Parse(text, "game", found);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Radio: /def/live_streams.sii could not be read (" + e.GetType().Name + ": " + e.Message + ").");
            }

            try
            {
                string home = EtsInstall.HomeFolder();
                string path = string.IsNullOrEmpty(home) ? null : Path.Combine(home, "live_streams.sii");

                if (path != null && File.Exists(path))
                {
                    fromHome = Parse(File.ReadAllText(path), "home", found);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Radio: the game's home live_streams.sii could not be read (" + e.GetType().Name + ": " + e.Message + ").");
            }

            FromGame = found.Count > 0;

            if (!FromGame)
            {
                foreach (string entry in WorldList)
                {
                    Add(entry, "world", found);
                }
            }

            // Favourites first, as the game's radio screen lists them; otherwise the file's order.
            var ordered = new List<Station>(found.Count);
            foreach (Station s in found) if (s.Favourite) ordered.Add(s);
            foreach (Station s in found) if (!s.Favourite) ordered.Add(s);

            _stations.Clear();
            _stations.AddRange(ordered);
            Index = -1;

            for (int i = 0; i < _stations.Count; i++)
            {
                if (current != null && _stations[i].Url == current)
                {
                    Index = i;
                }
            }

            if (Index < 0 && _stations.Count > 0)
            {
                Index = NextPlayable(-1, 1);
            }

            Debug.Log("[SomniumEuroTruck] Radio: " + _stations.Count + " stations (" + fromDef + " from /def/live_streams.sii, " + fromHome +
                      " from the game's home live_streams.sii" + (FromGame ? "" : "; the game lists none, so the world's own list of public MP3 streams") +
                      (mounted ? "" : "; install not mounted yet") + ").");
        }

        private static int Parse(string text, string source, List<Station> into)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int added = 0;

            foreach (Match m in EntryLine.Matches(text))
            {
                string key = m.Groups[1].Value;

                if (key.StartsWith("stream", StringComparison.OrdinalIgnoreCase) && Add(m.Groups[2].Value, source, into))
                {
                    added++;
                }
            }

            return added;
        }

        private static bool Add(string entry, string source, List<Station> into)
        {
            string[] parts = entry.Split('|');
            string url = parts[0].Trim();

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (Station s in into)
            {
                if (string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            string Part(int i) => parts.Length > i ? parts[i].Trim() : "";
            string name = Part(1);
            string fav = Part(5);

            into.Add(new Station
            {
                Url = url,
                Name = name.Length > 0 ? name : url,
                Genre = Part(2),
                Language = Part(3),
                Bitrate = Part(4),
                Favourite = fav == "1" || fav.Equals("true", StringComparison.OrdinalIgnoreCase),
                Source = source,
            });
            return true;
        }

        /// <summary>Not MP3 by its address (AAC, Ogg, Opus, FLAC, HLS): Unity is asked for MPEG only, so these are skipped.</summary>
        public static bool LooksMp3(Station station)
        {
            string u = station.Url.ToLowerInvariant();
            return !(u.Contains("aac") || u.Contains(".ogg") || u.Contains("opus") || u.Contains(".flac") || u.Contains(".m3u8") || u.Contains("/hls"));
        }

        private bool Playable(int i)
        {
            return i >= 0 && i < _stations.Count && !_stations[i].Failed && LooksMp3(_stations[i]);
        }

        /// <summary>The next playable station after `from` going `step` (+1 / -1), wrapping; -1 when none.</summary>
        private int NextPlayable(int from, int step)
        {
            int n = _stations.Count;

            for (int k = 1; k <= n; k++)
            {
                int i = (((from + step * k) % n) + n) % n;

                if (Playable(i))
                {
                    return i;
                }
            }

            return -1;
        }

        // ------------------------------------------------------------------ controls

        /// <summary>Tunes in to a station (the current one when out of range) and turns the radio on.</summary>
        public void Play(int index)
        {
            LoadStations();

            if (_stations.Count == 0)
            {
                Status = "No radio stations found";
                return;
            }

            if (index < 0 || index >= _stations.Count)
            {
                index = Index;
            }

            if (index >= 0 && index < _stations.Count && _stations[index].Failed)
            {
                _stations[index].Failed = false; // picked by hand: worth another try
            }

            if (!Playable(index))
            {
                index = NextPlayable(index, 1);
            }

            if (index < 0)
            {
                // Every station failed this session: the player asked again, so all get another chance.
                foreach (Station s in _stations) s.Failed = false;
                index = NextPlayable(-1, 1);
            }

            if (index < 0)
            {
                Status = "No MP3 radio station to play";
                return;
            }

            Index = index;
            On = true;
            UserStopped = false;
            int generation = ++_generation;
            StopAllCoroutines();
            Release();
            StartCoroutine(Run(generation));
        }

        /// <summary>Stops the radio (the player's Stop).</summary>
        public void Stop()
        {
            On = false;
            UserStopped = true;
            _generation++;
            StopAllCoroutines();
            Release();
            Status = "Radio off";
        }

        public void Toggle()
        {
            if (On)
            {
                Stop();
            }
            else
            {
                Play(Index);
            }
        }

        public void Next()
        {
            Step(1);
        }

        public void Previous()
        {
            Step(-1);
        }

        private void Step(int step)
        {
            LoadStations();
            int i = NextPlayable(Index, step);

            if (i < 0)
            {
                return;
            }

            if (On)
            {
                Play(i);
            }
            else
            {
                Index = i;
            }
        }

        // ------------------------------------------------------------------ playing

        private IEnumerator Run(int generation)
        {
            int failures = 0;

            while (generation == _generation && On)
            {
                if (!Playable(Index))
                {
                    int next = NextPlayable(Index, 1);

                    if (next < 0)
                    {
                        On = false;
                        Status = "No radio station could be played";
                        Debug.LogWarning("[SomniumEuroTruck] Radio: no station could be played; the radio is off.");
                        yield break;
                    }

                    Index = next;
                    failures = 0;
                }

                Station station = _stations[Index];
                float heard = 0f;
                string error = null;
                IEnumerator attempt = Attempt(station, generation, seconds => heard = seconds, why => error = why);

                while (attempt.MoveNext())
                {
                    yield return attempt.Current;
                }

                if (generation != _generation || !On)
                {
                    yield break;
                }

                Release();

                if (heard >= 10f)
                {
                    // It played: an endless stream's clip ended, stalled or was refreshed. Straight back to the same station.
                    failures = 0;
                    continue;
                }

                failures++;
                Debug.LogWarning("[SomniumEuroTruck] Radio: '" + station.Name + "' (" + station.Url + ") " + (error ?? "stopped at once") +
                                 " (try " + failures + ").");

                if (failures >= 2)
                {
                    station.Failed = true;
                    failures = 0;
                    Status = "'" + station.Name + "' is unavailable - next station ...";
                }
                else
                {
                    yield return new WaitForSecondsRealtime(1f);
                }
            }
        }

        /// <summary>One tune-in: connect, play once StartBytes are in, then hold while it plays. Reports seconds heard and why it ended.</summary>
        private IEnumerator Attempt(Station station, int generation, Action<float> heardFor, Action<string> endedBecause)
        {
            Status = "Tuning in to " + station.Name + " ...";
            UnityWebRequest request;

            try
            {
                request = UnityWebRequestMultimedia.GetAudioClip(station.Url, AudioType.MPEG);
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
                _request = request;
                request.SendWebRequest();
            }
            catch (Exception e)
            {
                endedBecause(e.GetType().Name + ": " + e.Message);
                yield break;
            }

            float started = Time.unscaledTime;
            AudioClip clip = null;

            while (clip == null)
            {
                if (generation != _generation)
                {
                    yield break;
                }

                if (request.isDone)
                {
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        clip = TakeClip(request); // a short finite file: all of it is in
                    }

                    if (clip == null)
                    {
                        endedBecause("could not be tuned in: " + (string.IsNullOrEmpty(request.error) ? "no audio" : request.error));
                        yield break;
                    }

                    break;
                }

                if (request.downloadedBytes >= StartBytes)
                {
                    clip = TakeClip(request);
                }

                if (clip == null && Time.unscaledTime - started > ConnectSeconds)
                {
                    endedBecause("sent " + request.downloadedBytes + " bytes in " + ConnectSeconds + " s, not enough to play");
                    yield break;
                }

                if (clip == null)
                {
                    yield return null;
                }
            }

            _clip = clip;
            AudioSource source = Source();
            source.clip = clip;
            source.loop = false;
            source.volume = _volume;
            source.Play();
            Status = "Playing " + station.Name + (station.Genre.Length > 0 ? " - " + station.Genre : "");
            Debug.Log("[SomniumEuroTruck] Radio: playing '" + station.Name + "' (" + station.Url + ")");

            float playedFrom = Time.unscaledTime;
            float lastSound = playedFrom;

            while (generation == _generation)
            {
                float now = Time.unscaledTime;

                if (source.isPlaying)
                {
                    lastSound = now;
                }
                else if (now - lastSound > StallSeconds)
                {
                    heardFor(lastSound - playedFrom);
                    endedBecause("went quiet");
                    yield break;
                }

                if (request.isDone && request.result != UnityWebRequest.Result.Success && request.result != UnityWebRequest.Result.InProgress)
                {
                    heardFor(now - playedFrom);
                    endedBecause("dropped: " + request.error);
                    yield break;
                }

                if (now - playedFrom > RefreshSeconds)
                {
                    heardFor(now - playedFrom);
                    yield break;
                }

                yield return null;
            }
        }

        private static AudioClip TakeClip(UnityWebRequest request)
        {
            try
            {
                var handler = request.downloadHandler as DownloadHandlerAudioClip;
                return handler != null ? handler.audioClip : null;
            }
            catch (Exception)
            {
                return null; // not enough of the stream in yet to make a clip
            }
        }

        /// <summary>Stops the sound and lets the request and its clip go.</summary>
        private void Release()
        {
            if (_source != null)
            {
                _source.Stop();
                _source.clip = null;
            }

            if (_request != null)
            {
                try
                {
                    if (!_request.isDone)
                    {
                        _request.Abort();
                    }

                    _request.Dispose();
                }
                catch (Exception)
                {
                    // already gone
                }

                _request = null;
            }

            if (_clip != null)
            {
                Destroy(_clip);
                _clip = null;
            }
        }
    }

    /// <summary>
    /// Media - Radio (the game's misc.radio link, icon /material/ui/link/radio.mat; owner E): the stations from EtsRadio
    /// with name and genre, eight to a screen, each with Play; Play / Stop, previous / next station and volume. Opening
    /// the page turns the radio on unless the player switched it off.
    /// </summary>
    public sealed class EtsPageRadio : EtsTabletPage
    {
        public const string PageId = "Radio";

        public override string Caption => "Radio"; // not in the locale ("RADIO PLAYER" is the game's screen title)
        public override string Icon => "/material/ui/link/radio.mat";
        public override string Id => PageId;
        public override string Category => "media";
        public override string ScreenLink => "misc.radio";

        private const int Rows = 8;
        private readonly TextMeshProUGUI[] _names = new TextMeshProUGUI[Rows];
        private readonly Button[] _plays = new Button[Rows];
        private TextMeshProUGUI _now, _footer;
        private Button _pageUp, _pageDown;
        private int _first;
        private int _shownKey = int.MinValue;

        private EtsRadio Radio => EtsRadio.Ensure(App != null ? App.gameObject : null);

        protected override void Build()
        {
            const float l = 270f, r = 1170f, b = 100f, t = 830f;
            EtsTabletApp.Window(Root, Caption, l, r, b, t);
            Color normal = EtsTabletApp.Colour("clr_txt", EtsTabletApp.NormalText);

            _now = EtsTabletApp.Text(EtsGameUi.Place(Root, "now playing", l + 30f, r - 30f, t - 100f, t - 60f), "", EtsFont.Normal, TextAlignmentOptions.Center,
                EtsTabletApp.Colour("clr_sel", EtsTabletApp.SelectedText));

            // Play / Stop, previous, next, volume - and +.
            float x = l + 30f, y = t - 170f;
            float w = (r - l - 60f - 40f) / 5f;
            App.Button(Root, "Play / Stop", x, x + w, y, y + 50f, () => { EtsRadio radio = Radio; if (radio != null) radio.Toggle(); });
            x += w + 10f;
            App.Button(Root, "Previous", x, x + w, y, y + 50f, () => { EtsRadio radio = Radio; if (radio != null) radio.Previous(); });
            x += w + 10f;
            App.Button(Root, "Next", x, x + w, y, y + 50f, () => { EtsRadio radio = Radio; if (radio != null) radio.Next(); });
            x += w + 10f;
            App.Button(Root, "Volume -", x, x + w, y, y + 50f, () => { EtsRadio radio = Radio; if (radio != null) radio.Volume -= 0.1f; });
            x += w + 10f;
            App.Button(Root, "Volume +", x, x + w, y, y + 50f, () => { EtsRadio radio = Radio; if (radio != null) radio.Volume += 0.1f; });

            // The stations, eight at a time.
            for (int i = 0; i < Rows; i++)
            {
                float top = t - 190f - i * 50f, bottom = top - 44f;
                _names[i] = EtsTabletApp.Text(EtsGameUi.Place(Root, "station " + i, l + 30f, r - 200f, bottom, top), "", EtsFont.Normal, TextAlignmentOptions.Left, normal);
                int row = i;
                _plays[i] = App.Button(Root, "Play", r - 180f, r - 30f, bottom + 2f, top - 2f, () =>
                {
                    EtsRadio radio = Radio;
                    if (radio != null) radio.Play(_first + row);
                });
            }

            _pageUp = App.Button(Root, "Previous page", l + 30f, l + 260f, b + 20f, b + 64f, () => { _first = Math.Max(0, _first - Rows); _shownKey = int.MinValue; Tick(); });
            _pageDown = App.Button(Root, "Next page", r - 260f, r - 30f, b + 20f, b + 64f, () =>
            {
                EtsRadio radio = Radio;
                if (radio != null && _first + Rows < radio.Stations.Count) _first += Rows;
                _shownKey = int.MinValue;
                Tick();
            });
            _footer = EtsTabletApp.Text(EtsGameUi.Place(Root, "footer", l + 280f, r - 280f, b + 20f, b + 64f), "", EtsFont.Normal, TextAlignmentOptions.Center,
                EtsTabletApp.Colour("clr_txt_d", EtsTabletApp.DimText));
            _shownKey = int.MinValue;
        }

        public override void Shown()
        {
            EtsRadio radio = Radio;

            if (radio == null)
            {
                return;
            }

            radio.LoadStations();

            // Off by default; opening this page turns it on, unless the player switched it off.
            if (!radio.On && !radio.UserStopped)
            {
                radio.Play(radio.Index);
            }

            // Show the page with the tuned station on it.
            if (radio.Index >= 0)
            {
                _first = radio.Index / Rows * Rows;
            }

            _shownKey = int.MinValue;
            Tick();
        }

        public override void Tick()
        {
            EtsRadio radio = Radio;

            if (radio == null || _now == null)
            {
                return;
            }

            string status = radio.Status;

            if (_now.text != status)
            {
                _now.text = status;
            }

            IReadOnlyList<EtsRadio.Station> stations = radio.Stations;
            int count = stations.Count;

            if (_first >= count)
            {
                _first = 0;
            }

            // Rows are rewritten only when what they show changes: the page, the tuned station, on/off, failures, volume.
            int failed = 0;
            for (int i = 0; i < Rows && _first + i < count; i++) if (stations[_first + i].Failed) failed |= 1 << i;
            int volume = Mathf.RoundToInt(radio.Volume * 100f);
            int key = (((((_first * 31 + count) * 31 + radio.Index) * 31 + (radio.On ? 1 : 0)) * 31 + failed) * 31 + volume) * 31 + (radio.FromGame ? 1 : 0);

            if (key == _shownKey)
            {
                return;
            }

            _shownKey = key;

            for (int i = 0; i < Rows; i++)
            {
                int s = _first + i;

                if (s >= count)
                {
                    if (_names[i] != null) _names[i].text = "";
                    EtsTabletApp.SetUsable(_plays[i], false);
                    continue;
                }

                EtsRadio.Station st = stations[s];
                bool tuned = s == radio.Index && radio.On;
                string line = (tuned ? "<color=#FAB003>> " : "") + st.Name + (tuned ? "</color>" : "") +
                              (st.Genre.Length > 0 ? "  <color=#A6A6A6>" + st.Genre + (st.Language.Length > 0 ? ", " + st.Language : "") + "</color>" : "") +
                              (st.Failed ? "  <color=#A6A6A6>(unavailable)</color>" : !EtsRadio.LooksMp3(st) ? "  <color=#A6A6A6>(not MP3)</color>" : "");

                if (_names[i] != null) _names[i].text = line;
                EtsTabletApp.SetUsable(_plays[i], EtsRadio.LooksMp3(st));
            }

            EtsTabletApp.SetUsable(_pageUp, _first > 0);
            EtsTabletApp.SetUsable(_pageDown, _first + Rows < count);

            if (_footer != null)
            {
                _footer.text = (count == 0 ? "No stations" : "Stations " + (_first + 1) + "-" + Math.Min(count, _first + Rows) + " of " + count) +
                               "   Volume " + volume + " %" + (count > 0 && !radio.FromGame ? "\nThe game lists no stations: public MP3 streams" : "");
            }
        }
    }
}
