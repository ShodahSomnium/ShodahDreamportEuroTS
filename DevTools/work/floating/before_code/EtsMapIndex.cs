using System;
using System.Collections;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The whole-map index, built once per client: every sector read, the road graph over all of them, every
    /// company (depot) and city area, a prefab lookup by uid, and the cargo definitions (owner G; PLAN.md §3.6, §7.G).
    /// EtsJobs makes its offers from it; D's EtsHqCatalog is to read its cities and yards from it.
    ///
    /// Extracted from EtsJobs.IndexMap with the same behaviour, frame for frame: it waits for the map viewer's map,
    /// reads every name of EtsMap.AvailableSectors in order, yields after every second one it counts (so 282
    /// sectors take 141 frames), then in one frame indexes the road graph, collects the depots and reads
    /// /def/cargo*.sii, and logs the same "Map read: N depots, M road nodes." line. Probe 1954 checks that against a
    /// verbatim copy of the old loop on the demo: the same frames, sector order, depots, graph edges and cargo, and
    /// the same offers from them. Measured in the client with the old flow: that line came 10-20 s after the front
    /// end started (Player.log:1308, 1366-1367; PLAN U16); a sector parse is 43 ms on average, up to 308 ms
    /// (Program1400), so reading two a frame costs about 86 ms per frame during those seconds - today's cost, kept
    /// as it is on purpose (PLAN §8 G1: "same behaviour").
    ///
    /// Phase 2 (G2): the sectors are parsed on the map's reader thread instead (Reader.StepAhead, EtsMap.ReadAhead,
    /// two queued ahead; F's Phase 2 report measured the two-a-frame read at 86-106 ms a frame for ~140 frames, while
    /// the VR player looks at the HQ map) - same names, same order; a frame only counts the sectors that have landed.
    /// After the last one the road graph is built a sector at a time, the lists and the cargo table each in a frame of
    /// their own (Reader.FinishStep: ~100 ms in one frame before, probe 2113 "Finish"; G2 checker), then frozen a slice
    /// per frame (EtsRoadGraph.FreezeStep, 4 ms a frame: arrays and NearestNode's grid, 114-133 ms in all on .NET,
    /// probe 2113) and only then is the index Ready, so EtsJobs can make offers a slice per frame instead of in one long frame.
    ///
    /// Who starts it: EtsJobs.Start calls <see cref="Begin"/>, so the read starts on exactly the frame it did when
    /// EtsJobs owned it; with the new flow EtsNet.Awake adds it through <see cref="Ensure"/> and its own Start begins
    /// it. Begin is idempotent. EtsTraffic relies on this read too: it leaves unread sectors to it for SectorWait
    /// seconds (EtsTraffic.cs "MayRead").
    ///
    /// The pure part is <see cref="Reader"/> (no engine calls), so the offline probe drives the very code the
    /// coroutine runs.
    /// </summary>
    public sealed class EtsMapIndex : MonoBehaviour
    {
        /// <summary>
        /// The reading itself, one step per frame. Step reads names until two more have been counted (a name shorter
        /// than "sec+0000+0000" is skipped without counting, as the old loop did) and says whether it stopped to
        /// yield; Finish builds the graph and the lists. No Unity calls, so the probe runs it as it is.
        /// </summary>
        public sealed class Reader
        {
            private readonly EtsMap _map;
            private int _next;

            public Reader(EtsMap map)
            {
                _map = map;
                Graph = new EtsRoadGraph(map);
            }

            /// <summary>Sector names counted so far (the old loop's "read").</summary>
            public int Counted { get; private set; }

            public int Total => _map.AvailableSectors.Count;
            public EtsRoadGraph Graph { get; }
            public readonly List<EtsMapCompany> Companies = new List<EtsMapCompany>();
            public readonly List<EtsMapCity> Cities = new List<EtsMapCity>();
            public readonly Dictionary<ulong, EtsMapPrefab> Prefabs = new Dictionary<ulong, EtsMapPrefab>();
            public EtsSiiFile Cargo { get; private set; }

            /// <summary>Sectors kept queued on the map's reader thread ahead of the first unread name (StepAhead).</summary>
            public const int ReadWindow = 2;

            /// <summary>True when it stopped after a second counted name (yield a frame); false when every name has been read.</summary>
            public bool Step()
            {
                List<string> names = _map.AvailableSectors;

                while (_next < names.Count)
                {
                    string name = names[_next++];

                    if (name.Length < 13)
                    {
                        continue;
                    }

                    if (SectorOf(name, out int x, out int z))
                    {
                        _map.GetSector(x, z);
                    }

                    if (++Counted % 2 == 0)
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// One frame of the read with the parsing off the main thread (Phase 2, F's request): the next ReadWindow
            /// sectors are queued on the map's reader thread (EtsMap.ReadAhead), and the names at the head that are read
            /// by now are counted - none is parsed here. Same names, same order, same counting rule as Step (a name
            /// shorter than "sec+0000+0000" is skipped uncounted; the reader thread is one FIFO, so the index's sectors
            /// join the map in name order). True while names are left. Why: Step parses two sectors a frame on the main
            /// thread, 86-106 ms frames for ~140 frames (F's Phase 2 report) while a VR player looks at the HQ map; here a
            /// frame pays only the map's Pump (adopting a read sector: a dictionary insert per node, 1.8 ms at worst in
            /// probe 2098). The window is small on purpose: the viewer's own reads (TryReadAround, the yard the player is
            /// arriving at) queue behind at most ReadWindow index sectors on the same thread. Without a reader thread
            /// (EtsMap.ReaderThreaded false) it is Step.
            /// </summary>
            public bool StepAhead()
            {
                if (!_map.ReaderThreaded)
                {
                    return Step();
                }

                List<string> names = _map.AvailableSectors;

                // Count the head while it is read (IsRead adopts finished reads first).
                while (_next < names.Count)
                {
                    string name = names[_next];

                    if (name.Length >= 13 && SectorOf(name, out int x, out int z) && !_map.IsRead(x, z))
                    {
                        break;
                    }

                    _next++;

                    if (name.Length >= 13)
                    {
                        Counted++;
                    }
                }

                // Keep the window queued (ReadAhead queues a sector once; a read one answers true at once).
                int queued = 0;

                for (int i = _next; i < names.Count && queued < ReadWindow; i++)
                {
                    string name = names[i];

                    if (name.Length >= 13 && SectorOf(name, out int x, out int z))
                    {
                        _map.ReadAhead(x, z);
                        queued++;
                    }
                }

                return _next < names.Count;
            }

            /// <summary>
            /// "sec+0003-0004": x from the five characters at 3, z from those at 8 - what int.TryParse(name.Substring(..., 5))
            /// read before, without the two strings a name (StepAhead runs every frame of the read). Probe 2118 compares it
            /// with int.TryParse over every sector name of the demo.
            /// </summary>
            public static bool SectorOf(string name, out int x, out int z)
            {
                z = 0;
                return Field(name, 3, out x) && Field(name, 8, out z);
            }

            private static bool Field(string s, int at, out int value)
            {
                value = 0;
                int end = at + 5;

                if (s.Length < end)
                {
                    return false;
                }

                int i = at;
                bool negative = s[i] == '-';

                if (negative || s[i] == '+')
                {
                    i++;
                }

                if (i >= end)
                {
                    return false;
                }

                for (; i < end; i++)
                {
                    char d = s[i];

                    if (d < '0' || d > '9')
                    {
                        value = 0;
                        return false;
                    }

                    value = value * 10 + (d - '0');
                }

                if (negative)
                {
                    value = -value;
                }

                return true;
            }

            /// <summary>
            /// Freezes the graph a slice at a time after Finish (EtsRoadGraph.FreezeStep: its arrays and NearestNode's
            /// grid, Phase 2); true when done. The coroutine gives it a few ms a frame, the offline probes all at once.
            /// </summary>
            public bool FreezeStep(double budgetMs)
            {
                return Graph.FreezeStep(budgetMs);
            }

            /// <summary>The graph over every sector read, the depots and city areas in sector order, prefabs by uid, the cargo table (all of FinishStep at once: the offline probes).</summary>
            public void Finish(EtsFileSystem files)
            {
                while (!FinishStep(files, double.MaxValue))
                {
                }
            }

            private int _finishStage;

            /// <summary>
            /// Finish a piece at a time, true when done: the road graph a sector at a time (EtsRoadGraph.IndexStep, about
            /// budgetMs a call), then the lists, then the cargo table, each in a call of its own. Why: in one frame it was
            /// 101 ms on .NET (probe 2113's "Finish"), the last long frame of the read while a VR player looks at the HQ
            /// map (checker 2121 has the pieces). Same sectors in the same order, so the same graph and lists as before.
            /// </summary>
            public bool FinishStep(EtsFileSystem files, double budgetMs)
            {
                switch (_finishStage)
                {
                    case 0:
                        if (!Graph.IndexStep(budgetMs))
                        {
                            return false;
                        }

                        _finishStage = 1;
                        return false;

                    case 1:
                        Companies.Clear();
                        Cities.Clear();
                        Prefabs.Clear();

                        foreach (EtsMapSector sector in _map.LoadedSectors)
                        {
                            Companies.AddRange(sector.Companies);
                            Cities.AddRange(sector.Cities);

                            foreach (EtsMapPrefab prefab in sector.Prefabs)
                            {
                                // The first one wins, as EtsMap.FindPrefab's walk over the sectors in the same order answers it.
                                if (!Prefabs.ContainsKey(prefab.Uid))
                                {
                                    Prefabs.Add(prefab.Uid, prefab);
                                }
                            }
                        }

                        _finishStage = 2;
                        return false;

                    case 2:
                        Cargo = files != null ? files.ReadDefinitionFamily("/def", "cargo") : new EtsSiiFile();
                        _finishStage = 3;
                        return true;

                    default:
                        return true;
                }
            }
        }

        private static readonly IReadOnlyList<EtsMapCompany> NoCompanies = Array.Empty<EtsMapCompany>();
        private static readonly IReadOnlyList<EtsMapCity> NoCities = Array.Empty<EtsMapCity>();

        public static EtsMapIndex Instance { get; private set; }

        /// <summary>The index on this object, added if missing (PLAN §3.2). Unity's null test, not ??.</summary>
        public static EtsMapIndex Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsMapIndex index = host.GetComponent<EtsMapIndex>();

            if (index == null)
            {
                index = host.AddComponent<EtsMapIndex>();
            }

            return index;
        }

        /// <summary>Work the graph freeze may do per frame, ms (the read's own frames cost ~86 ms each, PLAN §3.6).</summary>
        private const double FreezeSliceMs = 4.0;

        private Reader _reader;
        private bool _started;
        private float _startedAt = -1f;

        /// <summary>Every sector read and indexed; the lists below are complete.</summary>
        public bool Ready { get; private set; }

        /// <summary>0..1 over the sector names.</summary>
        public float Progress => Ready ? 1f : _reader != null && _reader.Total > 0 ? (float)_reader.Counted / _reader.Total : 0f;

        /// <summary>For the menu's job line: "Reading the map: n of m sectors", then "Map read: ...".</summary>
        public string Status { get; private set; } = "Reading the map ...";

        public IReadOnlyList<EtsMapCompany> Companies => Ready ? (IReadOnlyList<EtsMapCompany>)_reader.Companies : NoCompanies;
        public IReadOnlyList<EtsMapCity> Cities => Ready ? (IReadOnlyList<EtsMapCity>)_reader.Cities : NoCities;
        public EtsRoadGraph Graph => Ready ? _reader.Graph : null;

        /// <summary>The map that was indexed (the viewer's).</summary>
        public EtsMap Map { get; private set; }

        /// <summary>/def/cargo.sii and its family, read once with the index.</summary>
        public EtsSiiFile Cargo => Ready ? _reader.Cargo : null;

        /// <summary>Raised once, in the frame the index is complete.</summary>
        public event Action Indexed;

        /// <summary>
        /// The prefab item with this uid, or null: the same answer as EtsMap.FindPrefab (the first in sector order;
        /// probe 1954 compares all 365 depots and 1,500 random prefabs), from a dictionary instead of a walk over every prefab of the map.
        /// Falls back to the walk before the index is ready.
        /// </summary>
        public EtsMapPrefab Prefab(ulong uid)
        {
            if (Ready)
            {
                return _reader.Prefabs.TryGetValue(uid, out EtsMapPrefab prefab) ? prefab : null;
            }

            return Map != null ? Map.FindPrefab(uid) : null;
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            Begin();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>Starts reading now (it waits for the map viewer's map first). Does nothing once started.</summary>
        public void Begin()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            while (EtsMapViewer.Current == null || EtsMapViewer.Current.Map == null)
            {
                yield return null;
            }

            _startedAt = Time.realtimeSinceStartup;
            Map = EtsMapViewer.Current.Map;
            _reader = new Reader(Map);

            // The sectors are parsed on the map's reader thread (Reader.StepAhead); this frame only counts those read.
            // The status string is rebuilt only when the count moves.
            int shown = -1;

            while (_reader.StepAhead())
            {
                if (_reader.Counted != shown)
                {
                    shown = _reader.Counted;
                    Status = "Reading the map: " + shown + " of " + _reader.Total + " sectors";
                }

                yield return null;
            }

            // The graph, the lists and the cargo table, a piece a frame (Reader.FinishStep; in one frame it was ~100 ms).
            while (!_reader.FinishStep(EtsEngine.Files, FreezeSliceMs))
            {
                yield return null;
            }

            // Then the road graph is frozen over the next frames (EtsRoadGraph.FreezeStep, FreezeSliceMs a frame), before
            // anything reads the index: in one piece it would add 114-133 ms (.NET, probe 2113) to this frame.
            float freezingSince = Time.realtimeSinceStartup;
            int freezeFrames = 0;

            while (!_reader.FreezeStep(FreezeSliceMs))
            {
                freezeFrames++;
                yield return null;
            }

            Ready = true;
            Status = "Map read: " + _reader.Companies.Count + " depots, " + _reader.Graph.NodeCount + " road nodes.";
            Debug.Log("[SomniumEuroTruck] " + Status);
            Debug.Log("[SomniumEuroTruck] Map index: " + _reader.Counted + " sectors in " + (Time.realtimeSinceStartup - _startedAt).ToString("0.0") +
                      " s; " + _reader.Cities.Count + " city areas, " + _reader.Prefabs.Count + " prefabs, " + _reader.Cargo.Units.Count + " cargo units; road graph frozen in " +
                      (Time.realtimeSinceStartup - freezingSince).ToString("0.00") + " s over " + (freezeFrames + 1) + " frames (" + _reader.Graph.DenseCount + " nodes, " +
                      _reader.Graph.GridEntries + " in NearestNode's grid).");
            Indexed?.Invoke();
        }
    }
}
