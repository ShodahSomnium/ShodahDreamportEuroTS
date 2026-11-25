using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// One place a truck can be stood at an HQ yard: where (game coordinates; a trailer bay already moved to where
    /// the tractor stands), which way (Unity yaw, degrees, as EtsMapBuilder.TryPrefabSpawn gives it), what kind of
    /// spawn point (14 garage, 10 parking, 1 trailer bay) and on which prefab, and how far from the yard's anchor.
    /// </summary>
    public sealed class EtsHqSpot
    {
        public float GameX, GameY, GameZ, Yaw, Distance;
        public int Type;
        public ulong PrefabUid;
    }

    /// <summary>
    /// One headquarters city on the HQ map: name, country, position, map U/V and its resolved yard (owner D;
    /// [gameui §3.1], PLAN.md §3.6, §7.D). Spots (an addition to §7) is the yard's ordered list of truck stands,
    /// SpawnSpots its length, for EtsNet.SpawnOwnTruck (A).
    /// </summary>
    public sealed class EtsHqCity
    {
        public ulong Token; public string Name, CountryName, CountryIso;
        public float GameX, GameY, GameZ; public int Population; public float U, V;
        public EtsHqChoice Yard; public int SpawnSpots;

        /// <summary>The city id ("szczecin"), the country id ("poland") and the yard prefab's model ("garage_small_...").</summary>
        public string Id, Country, YardModel;

        /// <summary>The yard's truck stands in order: garage (14), then parking (10), then trailer bays (1), each by distance.</summary>
        public EtsHqSpot[] Spots = new EtsHqSpot[0];

        /// <summary>True when no garage prefab could be used and the yard is a company's (parking or trailer points).</summary>
        public bool CompanyYard;

        /// <summary>
        /// True when a node the yard stands on was missing from the parsed sector and was recovered from the sector
        /// file's bytes by the catalogue (EtsHqCatalogBuilder.RecoverNode). The map viewer reads the same sector with
        /// the same parser, so until that parser reads the whole node table the yard itself will not be built there:
        /// Dijon and Munich on 1.61 before F's Phase 2 node-table fix (probes 1887-1889); with the fix, none (probe 2042).
        /// The HQ stage shows such a pin disabled.
        /// </summary>
        public bool NodeRecovered;

        /// <summary>
        /// The city name's offset from its dot on the whole-map view, UI pixels (x right, y down): the city
        /// definition's map_x_offsets[7] / map_y_offsets[7] (Szczecin -38 / -26, Felixstowe -13 / -28; [gameui §3.1]).
        /// </summary>
        public float LabelDx, LabelDy;
    }

    /// <summary>
    /// The 72 headquarters cities with their yards (owner D; [gameui §3.1], PLAN.md §3.6, §7.D, U18).
    ///
    /// Which cities. A city can be a headquarters in the demo when it has a definition (/def/city.sii family) and a
    /// garage: a prefab with a GARAGE spawn point (type 14) whose box centre lies inside one of the city's areas
    /// (its map city items, grown by 300 m). That is probe 1653's rule; the player's own 1.61 save (84 garage units,
    /// 12 of them without a city definition) gives the same 72 ([gameui §0.7]). Six defined cities have no garage
    /// and are left out: Antwerp, Eindhoven, Harwich, Hull, Travemunde, Trieste.
    ///
    /// Where the dot goes. The city's biggest area's box centre (probe 1647: it equals the item's node plus half
    /// its size), put on the UI map by /def/map_data.sii's ui_map_center (-13500, -4950) and ui_map_size
    /// (5308.2, 5200) "real km", divided by the map scale (/map/europe.mbd f32 at +60, 19): W = 279,379 m,
    /// H = 273,684 m, u = (x - (cx - W/2)) / W, v = (z - (cz - H/2)) / H (v down). Verified on all 114 city items and
    /// 36 country positions (probes/hq_overlay_crop.png).
    ///
    /// The yard (PLAN §3.6, C23). The city's garage prefab (if two lay in the areas, the one nearest the centre;
    /// probe 1653 found exactly one for each of the 72). Its first type-14 point is the anchor. The stands are every
    /// type 14, 10 and 1 point of every prefab with a point within 150 m of the anchor, ordered garage, then
    /// parking, then trailer bays, each by distance from the anchor; never type 13, which is the company's MAP
    /// MARKER (EtsModelViewer.cs, "NOT the company point"). A trailer bay's stand is 13.68 m ahead along the
    /// point's own axis, where the game stands the tractor to couple (EtsModelViewer.TrailerCouplingOffset,
    /// measured from the 1.61 demo's game.log). A stand closer than 3 m to one already taken is dropped, since
    /// two trucks would stand in each other. Positions go through the prefab's own frame from
    /// EtsMapBuilder.TryPrefabFrame (owner F) - the same frame BuildPrefab and TryPrefabSpawn stand the yard in,
    /// node 0's full rotation where it fits the prefab's other nodes, else its yaw - so a stand is where the drawn
    /// yard is, tilt included; probe 1892 checks it against the game's own "[actor] Initialized at
    /// [14994.0;46.8;-15235.2]" at the TransiNet yard.
    ///
    /// Missing nodes. On 1.61 the sector parser (EtsMapSector.FindNodeTable, owner F) settles on the wrong run of
    /// records in some .base files and reads almost none of their node table: sec-0006+0004.base (Dijon) has a
    /// 920-node table at 140965 of which it reads 179 misaligned records, sec+0000+0003.base (Munich) a 1,527-node
    /// table at 259755 of which it reads 5 (probes 1887-1889). Both garages' nodes are in those tables. The builder
    /// then finds the node record itself - 56 bytes whose uid is the node's and whose backward or forward item is
    /// the prefab - and flags the city (EtsHqCity.NodeRecovered), since the map viewer, reading with the same
    /// parser, will not build that yard until F's parser does.
    ///
    /// The choice handed to the session (EtsHqChoice): OriginGame is the anchor stand (the map viewer's centre),
    /// ArrivalGame the first stand plus 6 m to its right, ArrivalYaw facing back towards it - where a player
    /// arrives and sees the spot the truck will appear on. The 6 m side step comes from PLAN §3.6 and has not been
    /// tried in Play.
    ///
    /// When. The index of the whole map took 10-20 s in the client (Player.log:1308,1366-1367; PLAN U16). Build
    /// waits for G's EtsMapIndex and then works through the indexed map in slices of at most ~1.5 ms a frame.
    /// The 72 rows (with their stands) are small, so EtsFlow keeps them in Bridge LocalStorage under the key
    /// EtsFlowRules.CatalogueKey (CacheKeyPrefix, CacheRevision, MapSignature, the locale's language): data derived on the player's PC from the player's install, not an asset.
    /// </summary>
    public static class EtsHqCatalog
    {
        public const string CacheKeyPrefix = "ets_hq_catalog_";

        /// <summary>The format of Serialize's text; a different number is not read back.</summary>
        public const int CacheFormat = 1;

        /// <summary>
        /// Part of the cache key (EtsFlowRules.CatalogueKey), bumped whenever the builder or a parser it reads through
        /// finds something else in the same install - MapSignature hashes the install's files, not this code. 2: F's
        /// Phase 2 node-table fix (EtsMapSector.FindNodeTable, probes 2090-2093), which gives Dijon's and Munich's
        /// garages their nodes, so a catalogue built before it (both flagged NodeRecovered, greyed) must not be read.
        /// </summary>
        public const int CacheRevision = 2;

        private static readonly IReadOnlyList<EtsHqCity> None = Array.Empty<EtsHqCity>();
        private static IReadOnlyList<EtsHqCity> _cities = None;
        private static Dictionary<ulong, EtsHqCity> _byToken = new Dictionary<ulong, EtsHqCity>();
        private static EtsFileSystem _files;
        private static EtsMap _map;

        public static bool Ready { get; private set; }
        public static float Progress { get; private set; }
        public static IReadOnlyList<EtsHqCity> Cities => _cities;

        /// <summary>What the last build found, for the log.</summary>
        public static string Report { get; private set; } = "";

        /// <summary>
        /// The install and the map Build reads when they are not the defaults (EtsEngine.Files and the index's
        /// map, EtsMapIndex.Map): the harness, or an editor tool.
        /// </summary>
        public static void Configure(EtsFileSystem files, EtsMap map)
        {
            _files = files;
            _map = map;
        }

        /// <summary>
        /// Builds the catalogue: waits for the map index to be ready (when one is given), then walks the indexed
        /// map in slices of ~1.5 ms. With no index it reads every sector itself as it goes (a sector read is
        /// ~50 ms in mono, so that path is for the editor and the harness only). Nothing on failure; see Report.
        /// </summary>
        public static IEnumerator Build(EtsMapIndex index)
        {
            Ready = false;
            Progress = 0f;

            // Unity's null test on the component (a destroyed index reads as null).
            bool indexed = index != null;

            while (indexed && !index.Ready)
            {
                Progress = 0.5f * Mathf.Clamp01(index.Progress);
                yield return null;

                if (index == null)
                {
                    Report = "HQ catalogue: the map index went away";
                    yield break;
                }
            }

            EtsFileSystem files = _files != null ? _files : EtsEngine.Files;
            EtsMap map = _map != null ? _map : indexed ? index.Map : null;

            if (files == null || map == null)
            {
                Report = "HQ catalogue: not configured (no install or map)";
                yield break;
            }

            var builder = new EtsHqCatalogBuilder(files, map, !indexed);
            var watch = new System.Diagnostics.Stopwatch();
            float from = index != null ? 0.5f : 0f;

            while (true)
            {
                watch.Restart();
                bool done = builder.Step(watch, 1.5);
                Progress = from + (1f - from) * builder.Progress;

                if (done)
                {
                    break;
                }

                yield return null;
            }

            Adopt(builder.Result);
            Report = builder.Report;
        }

        /// <summary>Makes a list the catalogue (from a build, or from the LocalStorage cache).</summary>
        public static void Adopt(List<EtsHqCity> cities)
        {
            var byToken = new Dictionary<ulong, EtsHqCity>();

            if (cities != null)
            {
                foreach (EtsHqCity city in cities)
                {
                    byToken[city.Token] = city;
                }
            }

            _byToken = byToken;
            _cities = cities != null ? (IReadOnlyList<EtsHqCity>)cities.AsReadOnly() : None;
            Ready = cities != null && cities.Count > 0;
            Progress = 1f;
        }

        /// <summary>Forgets everything (statics outlive a world visit, PLAN §3.2).</summary>
        public static void Clear()
        {
            _cities = None;
            _byToken = new Dictionary<ulong, EtsHqCity>();
            Ready = false;
            Progress = 0f;
            Report = "";
        }

        public static bool TryGetCity(ulong cityToken, out EtsHqCity city)
        {
            return _byToken.TryGetValue(cityToken, out city);
        }

        /// <summary>The yard of an HQ city, as the session writes it (A, master only; PLAN §7.X).</summary>
        public static bool TryResolveYard(ulong cityToken, out EtsHqChoice yard)
        {
            yard = default;

            if (!_byToken.TryGetValue(cityToken, out EtsHqCity city) || city.SpawnSpots == 0)
            {
                return false;
            }

            yard = city.Yard;
            return true;
        }

        /// <summary>
        /// What the catalogue was derived from: the sector list, /def/map_data.sii and the city definitions, hashed
        /// (FNV-1a 32). A different install or map version gives a different key, so a stale cache is never read.
        /// </summary>
        public static string MapSignature(EtsFileSystem files, EtsMap map)
        {
            uint hash = 2166136261u;
            var names = new List<string>(map != null ? map.AvailableSectors : new List<string>());
            names.Sort(StringComparer.Ordinal);

            foreach (string name in names) hash = Fnv(hash, name);

            hash = Fnv(hash, files != null ? files.ReadText("/def/map_data.sii") ?? "" : "");
            hash = Fnv(hash, files != null ? files.ReadText("/def/city.sii") ?? "" : "");
            return CacheFormat + "_" + hash.ToString("x8", CultureInfo.InvariantCulture);
        }

        private static uint Fnv(uint hash, string text)
        {
            foreach (char c in text)
            {
                hash = (hash ^ (c & 0xFFu)) * 16777619u;
                hash = (hash ^ (uint)(c >> 8)) * 16777619u;
            }

            return hash;
        }

        /// <summary>
        /// The catalogue as text for the LocalStorage cache: a header line "ets_hq_catalog|format|signature|count",
        /// then one line per city (fields split by |, its stands by ;). Positions to the centimetre.
        /// </summary>
        public static string Serialize(IReadOnlyList<EtsHqCity> cities, string signature)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append("ets_hq_catalog|").Append(CacheFormat).Append('|').Append(signature).Append('|').Append(cities.Count).Append('\n');

            foreach (EtsHqCity c in cities)
            {
                EtsHqChoice y = c.Yard;
                sb.Append(c.Token.ToString(CultureInfo.InvariantCulture)).Append('|').Append(Clean(c.Id)).Append('|').Append(Clean(c.Name)).Append('|')
                  .Append(Clean(c.Country)).Append('|').Append(Clean(c.CountryName)).Append('|').Append(Clean(c.CountryIso)).Append('|')
                  .Append(F(c.GameX)).Append('|').Append(F(c.GameY)).Append('|').Append(F(c.GameZ)).Append('|')
                  .Append(c.Population.ToString(CultureInfo.InvariantCulture)).Append('|').Append(c.U.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                  .Append(c.V.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                  .Append(y.YardPrefabUid.ToString(CultureInfo.InvariantCulture)).Append('|').Append(y.YardCompany.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(F(y.OriginGame.x)).Append('|').Append(F(y.OriginGame.y)).Append('|').Append(F(y.OriginGame.z)).Append('|')
                  .Append(F(y.ArrivalGame.x)).Append('|').Append(F(y.ArrivalGame.y)).Append('|').Append(F(y.ArrivalGame.z)).Append('|')
                  .Append(F(y.ArrivalYaw)).Append('|').Append(Clean(c.YardModel)).Append('|').Append(c.CompanyYard ? '1' : '0').Append('|')
                  .Append(c.NodeRecovered ? '1' : '0').Append('|').Append(F(c.LabelDx)).Append('|').Append(F(c.LabelDy)).Append('|');

                for (int i = 0; i < c.Spots.Length; i++)
                {
                    EtsHqSpot s = c.Spots[i];
                    if (i > 0) sb.Append(';');
                    sb.Append(s.Type.ToString(CultureInfo.InvariantCulture)).Append(',').Append(F(s.GameX)).Append(',').Append(F(s.GameY)).Append(',')
                      .Append(F(s.GameZ)).Append(',').Append(F(s.Yaw)).Append(',').Append(F(s.Distance)).Append(',').Append(s.PrefabUid.ToString(CultureInfo.InvariantCulture));
                }

                sb.Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>Serialize's text read back; null when it is not the same format and signature, or malformed.</summary>
        public static List<EtsHqCity> Deserialize(string text, string signature)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            string[] lines = text.Split('\n');
            string[] head = lines[0].Split('|');

            if (head.Length != 4 || head[0] != "ets_hq_catalog" || head[1] != CacheFormat.ToString(CultureInfo.InvariantCulture) || head[2] != signature ||
                !int.TryParse(head[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            {
                return null;
            }

            var cities = new List<EtsHqCity>(count);

            try
            {
                for (int l = 1; l < lines.Length; l++)
                {
                    if (lines[l].Length == 0) continue;
                    string[] f = lines[l].Split('|');
                    if (f.Length != 27) return null;

                    var c = new EtsHqCity
                    {
                        Token = ulong.Parse(f[0], CultureInfo.InvariantCulture), Id = f[1], Name = f[2], Country = f[3], CountryName = f[4], CountryIso = f[5],
                        GameX = P(f[6]), GameY = P(f[7]), GameZ = P(f[8]), Population = int.Parse(f[9], CultureInfo.InvariantCulture),
                        U = P(f[10]), V = P(f[11]), YardModel = f[21], CompanyYard = f[22] == "1", NodeRecovered = f[23] == "1",
                        LabelDx = P(f[24]), LabelDy = P(f[25])
                    };

                    c.Yard = new EtsHqChoice
                    {
                        City = c.Token, YardPrefabUid = ulong.Parse(f[12], CultureInfo.InvariantCulture), YardCompany = ulong.Parse(f[13], CultureInfo.InvariantCulture),
                        OriginGame = new Vector3(P(f[14]), P(f[15]), P(f[16])), ArrivalGame = new Vector3(P(f[17]), P(f[18]), P(f[19])), ArrivalYaw = P(f[20])
                    };

                    var spots = new List<EtsHqSpot>();

                    if (f[26].Length > 0)
                    {
                        foreach (string s in f[26].Split(';'))
                        {
                            string[] v = s.Split(',');
                            if (v.Length != 7) return null;
                            spots.Add(new EtsHqSpot
                            {
                                Type = int.Parse(v[0], CultureInfo.InvariantCulture), GameX = P(v[1]), GameY = P(v[2]), GameZ = P(v[3]), Yaw = P(v[4]),
                                Distance = P(v[5]), PrefabUid = ulong.Parse(v[6], CultureInfo.InvariantCulture)
                            });
                        }
                    }

                    c.Spots = spots.ToArray();
                    c.SpawnSpots = c.Spots.Length;
                    cities.Add(c);
                }
            }
            catch (FormatException)
            {
                return null;
            }
            catch (OverflowException)
            {
                return null;
            }

            return cities.Count == count ? cities : null;
        }

        private static string F(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static float P(string text)
        {
            return float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string Clean(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : text.Replace('|', '/').Replace(';', ',').Replace('\n', ' ');
        }
    }

    /// <summary>
    /// The game's UI map: where its picture lies in the world and how its cities are drawn, from /def/map_data.sii
    /// and /map/europe.mbd ([gameui §3.1]). ui_map_center (-13500, -4950) and ui_map_size (5308.2, 5200) "real km"
    /// over the map scale (19, the .mbd f32 at +60) give the picture's 279,379 x 273,684 game metres, u = (x - X0) /
    /// Width, v = (z - Z0) / Height, v down - verified on all 114 city items (probe 1647). city_icon_size[7] (13 x 13)
    /// is a city dot at the whole-map zoom (zoom_uplift[7], "whole world map zoom"); city_pin_75/100/150_pop (0,
    /// 200,000, 8,000,000) scale it to 75, 100 or 150 % - steps, since whether the game blends between them is not in
    /// the files; ui_map_camera_min/max bound where the map may be panned to.
    /// </summary>
    public sealed class EtsUiMapFrame
    {
        public float X0 = -13500f - 279378.9f / 2f, Z0 = -4950f - 273684.2f / 2f, Width = 279378.9f, Height = 273684.2f;
        public float Scale = 19f;
        public bool ScaleRead;
        public float PinSize = 13f;
        public float Pop75, Pop100 = 200000f, Pop150 = 8000000f;
        public float CameraMinX = -82000f, CameraMinZ = -122000f, CameraMaxX = 51000f, CameraMaxZ = 69000f;

        public float U(float gameX) => (gameX - X0) / Width;
        public float V(float gameZ) => (gameZ - Z0) / Height;

        /// <summary>A city dot's scale for its population: 0.75, 1 or 1.5.</summary>
        public float PinScale(int population)
        {
            return population >= Pop150 ? 1.5f : population >= Pop100 ? 1f : 0.75f;
        }

        public static EtsUiMapFrame Read(EtsFileSystem files)
        {
            var frame = new EtsUiMapFrame();
            EtsSiiFile mapData = files != null ? files.ReadSii("/def/map_data.sii") : null;
            EtsSiiUnit md = mapData != null && mapData.Units.Count > 0 ? mapData.Units[0] : null;
            float[] centre = md != null ? md.GetFloats("ui_map_center") : null;
            float[] size = md != null ? md.GetFloats("ui_map_size") : null;

            // /map/europe.mbd (69 bytes on 1.61, map version 907): f32 map scale 19 at +60, city scale 3 at +64.
            byte[] mbd = files != null ? files.Read("/map/europe.mbd") : null;

            if (mbd != null && mbd.Length >= 64)
            {
                float scale = BitConverter.ToSingle(mbd, 60);

                if (scale > 1f && scale < 100f)
                {
                    frame.Scale = scale;
                    frame.ScaleRead = true;
                }
            }

            float cx = centre != null && centre.Length >= 2 ? centre[0] : -13500f, cz = centre != null && centre.Length >= 2 ? centre[1] : -4950f;
            float sx = size != null && size.Length >= 2 ? size[0] : 5308.2f, sz = size != null && size.Length >= 2 ? size[1] : 5200f;
            frame.Width = sx * 1000f / frame.Scale;
            frame.Height = sz * 1000f / frame.Scale;
            frame.X0 = cx - frame.Width / 2f;
            frame.Z0 = cz - frame.Height / 2f;

            if (md != null)
            {
                EtsSiiValue icons = md.Get("city_icon_size");

                if (icons != null && icons.Count > 7)
                {
                    string[] wh = icons[7].Trim('(', ')', ' ').Split(',');
                    if (wh.Length >= 1 && float.TryParse(wh[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float pin)) frame.PinSize = pin;
                }

                frame.Pop75 = md.GetFloat("city_pin_75_pop", frame.Pop75);
                frame.Pop100 = md.GetFloat("city_pin_100_pop", frame.Pop100);
                frame.Pop150 = md.GetFloat("city_pin_150_pop", frame.Pop150);
                float[] min = md.GetFloats("ui_map_camera_min"), max = md.GetFloats("ui_map_camera_max");
                if (min != null && min.Length >= 2) { frame.CameraMinX = min[0]; frame.CameraMinZ = min[1]; }
                if (max != null && max.Length >= 2) { frame.CameraMaxX = max[0]; frame.CameraMaxZ = max[1]; }
            }

            return frame;
        }
    }

    /// <summary>
    /// The catalogue's work, in steps a coroutine can spread over frames (EtsHqCatalog.Build) or the harness can
    /// run to the end (RunAll). Unity-free apart from EtsHqChoice's Vector3 fields.
    /// </summary>
    public sealed class EtsHqCatalogBuilder
    {
        /// <summary>Metres ahead of a trailer bay's point the tractor stands (EtsModelViewer.TrailerCouplingOffset).</summary>
        public const float TrailerCouplingOffset = 13.68f;

        /// <summary>A city area is grown by this before a garage is looked for in it (probe 1653).</summary>
        public const float AreaMargin = 300f;

        /// <summary>Stands are taken from prefabs within this of the yard's anchor (PLAN §3.6).</summary>
        public const float YardRadius = 150f;

        /// <summary>A stand this close to one already taken is dropped.</summary>
        public const float MinimumSpacing = 3f;

        /// <summary>Metres from the first stand to the arrival point, to its right (PLAN §3.6).</summary>
        public const float ArrivalSideStep = 6f;

        private static readonly int[] StandTypes = { EtsPrefabDescriptor.SpawnGarage, EtsPrefabDescriptor.SpawnParking, EtsPrefabDescriptor.SpawnTrailer };

        private sealed class Area
        {
            public float X0, Z0, X1, Z1, Size;
            public float CX, CY, CZ;
        }

        private sealed class CityInfo
        {
            public ulong Token;
            public string Id;
            public EtsSiiUnit Def;
            public readonly List<Area> Areas = new List<Area>();
            public readonly List<EtsMapPrefab> Garages = new List<EtsMapPrefab>();
            public readonly List<EtsMapPrefab> Near = new List<EtsMapPrefab>();
        }

        private readonly EtsFileSystem _files;
        private readonly EtsMap _map;
        private readonly bool _readsSectors;
        private readonly Dictionary<ulong, List<EtsPrefabDescriptor.SpawnPoint>> _standsByToken = new Dictionary<ulong, List<EtsPrefabDescriptor.SpawnPoint>>();
        private readonly Dictionary<ulong, CityInfo> _cities = new Dictionary<ulong, CityInfo>();
        private readonly List<EtsMapPrefab> _prefabs = new List<EtsMapPrefab>();
        private readonly List<EtsMapCompany> _companies = new List<EtsMapCompany>();
        private readonly Dictionary<ulong, EtsMapNode> _nodes = new Dictionary<ulong, EtsMapNode>();
        private readonly HashSet<ulong> _missingNodes = new HashSet<ulong>();
        private int _prefabsRead;
        private List<string> _sectorNames;
        private List<CityInfo> _order;
        private EtsSiiFile _cityDefs, _countryDefs;
        private float _mapX0, _mapZ0, _mapW, _mapH;
        private int _stage, _cursor, _prefabCursor;
        private EtsMapSector _sector;
        private int _fallbacks, _dropped, _unplaced, _recovered;
        private readonly Dictionary<string, byte[]> _sectorBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly HashSet<ulong> _recoveredFor = new HashSet<ulong>();
        private readonly HashSet<ulong> _unrecoverable = new HashSet<ulong>();

        public List<EtsHqCity> Result { get; private set; }
        public float Progress { get; private set; }
        public string Report { get; private set; } = "";

        /// <summary>The map scale read from /map/europe.mbd (19 on 1.61), and whether it was read rather than assumed.</summary>
        public float MapScale { get; private set; } = 19f;
        public bool MapScaleRead { get; private set; }

        public EtsHqCatalogBuilder(EtsFileSystem files, EtsMap map, bool readSectors)
        {
            _files = files;
            _map = map;
            _readsSectors = readSectors;
        }

        /// <summary>Runs every step (the harness; or a worker, if EtsFileSystem is ever shown safe there).</summary>
        public static List<EtsHqCity> RunAll(EtsFileSystem files, EtsMap map, bool readSectors, out string report)
        {
            var builder = new EtsHqCatalogBuilder(files, map, readSectors);
            var watch = new System.Diagnostics.Stopwatch();

            while (!builder.Step(watch, double.MaxValue))
            {
            }

            report = builder.Report;
            return builder.Result;
        }

        /// <summary>Does work until budgetMs of watch have passed; true when the catalogue is done.</summary>
        public bool Step(System.Diagnostics.Stopwatch watch, double budgetMs)
        {
            if (!watch.IsRunning) watch.Start();

            while (watch.Elapsed.TotalMilliseconds < budgetMs)
            {
                switch (_stage)
                {
                    case 0:
                        // The definitions, one file family per turn (together they took 13-14 ms in mono, probe 1883).
                        if (_cursor == 0) _cityDefs = _files.ReadDefinitionFamily("/def", "city");
                        else if (_cursor == 1) _countryDefs = _files.ReadDefinitionFamily("/def", "country");
                        else
                        {
                            ReadMapData();
                            _sectorNames = new List<string>(_map.AvailableSectors);
                            _sectorNames.Sort(StringComparer.Ordinal);
                            _stage = 1;
                            _cursor = 0;
                            break;
                        }

                        _cursor++;
                        break;

                    case 1:
                        // Sector by sector: city areas, companies, and the prefabs that carry stands - one prefab per
                        // turn, since a prefab model seen for the first time costs a descriptor read.
                        if (_sector != null)
                        {
                            if (_prefabCursor < _sector.Prefabs.Count)
                            {
                                AddPrefab(_sector, _sector.Prefabs[_prefabCursor++]);
                            }
                            else
                            {
                                _sector = null;
                            }

                            break;
                        }

                        if (_cursor >= _sectorNames.Count)
                        {
                            _stage = _readsSectors && _missingNodes.Count > 0 ? 5 : 2;
                            _cursor = 0;
                            break;
                        }

                        string name = _sectorNames[_cursor++];
                        Progress = 0.6f * _cursor / Math.Max(1, _sectorNames.Count);
                        _sector = SectorOf(name);
                        _prefabCursor = 0;

                        if (_sector != null)
                        {
                            AddSector(_sector);
                        }

                        break;

                    case 5:
                        // Reading sectors here (no index): a prefab's node 0 can sit in a sector read before the prefab
                        // was; one more pass picks those up.
                        if (_cursor >= _sectorNames.Count)
                        {
                            _stage = 2;
                            _cursor = 0;
                            break;
                        }

                        EtsMapSector again = SectorOf(_sectorNames[_cursor++]);

                        if (again != null)
                        {
                            foreach (EtsMapNode node in again.Nodes)
                            {
                                if (_missingNodes.Remove(node.Uid)) _nodes[node.Uid] = node;
                            }
                        }

                        if (_missingNodes.Count == 0) _cursor = _sectorNames.Count;
                        break;

                    case 2:
                        // Prefab by prefab: which city's area holds it, and does it carry stands.
                        if (_cursor >= _prefabs.Count)
                        {
                            _order = new List<CityInfo>();

                            foreach (CityInfo c in _cities.Values)
                            {
                                if (c.Def != null && c.Areas.Count > 0) _order.Add(c);
                            }

                            _order.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                            Result = new List<EtsHqCity>();
                            _stage = 3;
                            _cursor = 0;
                            break;
                        }

                        Progress = 0.6f + 0.3f * _cursor / Math.Max(1, _prefabs.Count);
                        PlacePrefab(_prefabs[_cursor++]);
                        break;

                    case 3:
                        // City by city: the yard and its stands.
                        if (_cursor >= _order.Count)
                        {
                            _stage = 4;
                            break;
                        }

                        Progress = 0.9f + 0.1f * _cursor / Math.Max(1, _order.Count);
                        EtsHqCity city = Resolve(_order[_cursor++]);

                        if (city != null)
                        {
                            Result.Add(city);
                        }

                        break;

                    default:
                        Progress = 1f;
                        Summarise();
                        return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------ steps

        private void ReadMapData()
        {
            EtsUiMapFrame frame = EtsUiMapFrame.Read(_files);
            MapScale = frame.Scale;
            MapScaleRead = frame.ScaleRead;
            _mapW = frame.Width;
            _mapH = frame.Height;
            _mapX0 = frame.X0;
            _mapZ0 = frame.Z0;
        }

        private EtsMapSector SectorOf(string name)
        {
            if (name.Length < 13 || !int.TryParse(name.Substring(3, 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(name.Substring(8, 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
            {
                return null;
            }

            if (!_readsSectors)
            {
                // After the index (G) every sector is in the map already, and this only looks it up.
                return _map.GetSector(x, z);
            }

            // No index: read the sector's .base and .aux here (not .data, the terrain) and keep only what the
            // catalogue needs; the map's own cache would hold every sector with its terrain, which ran the harness
            // out of memory.
            byte[] baseBytes = _files.Read(EtsMapSector.FileName(x, z, "base"));
            byte[] auxBytes = _files.Read(EtsMapSector.FileName(x, z, "aux"));
            return baseBytes == null && auxBytes == null ? null : EtsMapSector.Parse(x, z, baseBytes, auxBytes, null);
        }

        private void AddSector(EtsMapSector sector)
        {
            foreach (EtsMapCity item in sector.Cities)
            {
                if (!_cities.TryGetValue(item.CityToken, out CityInfo info))
                {
                    string id = EtsToken.Decode(item.CityToken);
                    info = new CityInfo { Token = item.CityToken, Id = id, Def = _cityDefs != null ? _cityDefs.Find("city." + id) : null };
                    _cities[item.CityToken] = info;
                }

                float cx = (item.KdopMin[0] + item.KdopMax[0]) / 2f, cz = (item.KdopMin[2] + item.KdopMax[2]) / 2f;
                info.Areas.Add(new Area
                {
                    X0 = cx - item.Width / 2f, Z0 = cz - item.Height / 2f, X1 = cx + item.Width / 2f, Z1 = cz + item.Height / 2f,
                    Size = item.Width * item.Height, CX = cx, CY = (item.KdopMin[1] + item.KdopMax[1]) / 2f, CZ = cz
                });
            }

            if (_readsSectors)
            {
                foreach (EtsMapNode node in sector.Nodes)
                {
                    if (_missingNodes.Remove(node.Uid)) _nodes[node.Uid] = node;
                }
            }

            _companies.AddRange(sector.Companies);
        }

        /// <summary>Keeps a prefab that carries a stand, with its node 0 when the builder reads the sectors itself.</summary>
        private void AddPrefab(EtsMapSector sector, EtsMapPrefab prefab)
        {
            _prefabsRead++;

            if (prefab.NodeUids.Length == 0 || StandsOf(prefab.PrefabToken).Count == 0)
            {
                return;
            }

            _prefabs.Add(prefab);

            if (!_readsSectors)
            {
                return;
            }

            // Every node of the prefab, not only node 0: EtsMapBuilder.TryPrefabFrame measures the fit of node 0's
            // full rotation against the prefab's other nodes, and a node the parsed map does not hold is what
            // RecoverNode (and the city's NodeRecovered flag) is for - so the builder must hold every parsed one.
            foreach (ulong uid in prefab.NodeUids)
            {
                if (sector.NodesByUid.TryGetValue(uid, out EtsMapNode node))
                {
                    _nodes[uid] = node;
                    _missingNodes.Remove(uid);
                }
                else if (!_nodes.ContainsKey(uid))
                {
                    _missingNodes.Add(uid);
                }
            }
        }

        /// <summary>A prefab's map node 0: the builder's own when it read the sectors itself, else the map's.</summary>
        private EtsMapNode FindNode(ulong uid)
        {
            return _nodes.TryGetValue(uid, out EtsMapNode node) ? node : _map.FindNode(uid);
        }

        /// <summary>The node lookup for one prefab: the parsed map first, then the prefab's own sector file (RecoverNode).</summary>
        private Func<ulong, EtsMapNode> NodesOf(EtsMapPrefab prefab)
        {
            return uid => FindNode(uid) ?? RecoverNode(prefab, uid);
        }

        /// <summary>
        /// A node the parsed sectors do not hold, found in the raw .base / .aux of the sector the prefab's box centre is
        /// in (class notes: EtsMapSector.FindNodeTable misses whole node tables in some 1.61 files). Kept, and the
        /// prefab remembered, so the city is flagged. Null when the record is not there either.
        /// </summary>
        private EtsMapNode RecoverNode(EtsMapPrefab prefab, ulong uid)
        {
            if (uid == 0 || _unrecoverable.Contains(uid))
            {
                return null;
            }

            float cx = (prefab.KdopMin[0] + prefab.KdopMax[0]) / 2f, cz = (prefab.KdopMin[2] + prefab.KdopMax[2]) / 2f;
            EtsMapSector.SectorOf(cx, cz, out int sx, out int sz);

            foreach (string ext in new[] { "base", "aux" })
            {
                string path = EtsMapSector.FileName(sx, sz, ext);

                if (!_sectorBytes.TryGetValue(path, out byte[] bytes))
                {
                    bytes = _files.Read(path);
                    _sectorBytes[path] = bytes;
                }

                EtsMapNode node = FindNodeRecord(bytes, uid, prefab.Uid);

                if (node != null)
                {
                    _nodes[uid] = node;
                    _recoveredFor.Add(prefab.Uid);
                    _recovered++;
                    return node;
                }
            }

            _unrecoverable.Add(uid);
            return null;
        }

        /// <summary>
        /// A 56-byte node record (the sector format EtsMapSector.ReadNodes reads: uid, x y z as int/256, w x y z, backward
        /// item, forward item, flags) whose uid is <paramref name="uid"/> and whose backward or forward item is
        /// <paramref name="itemUid"/>, anywhere in a sector file; null when there is none. Probe 1887 found each of the
        /// four Dijon and Munich garage nodes exactly so: the uid, and 36 or 44 bytes on the garage prefab's uid.
        /// </summary>
        public static EtsMapNode FindNodeRecord(byte[] b, ulong uid, ulong itemUid)
        {
            if (b == null)
            {
                return null;
            }

            byte first = (byte)(uid & 0xFF);

            for (int o = 0; o + 56 <= b.Length; o++)
            {
                if (b[o] != first || BitConverter.ToUInt64(b, o) != uid)
                {
                    continue;
                }

                ulong back = BitConverter.ToUInt64(b, o + 36), forward = BitConverter.ToUInt64(b, o + 44);

                if (back != itemUid && forward != itemUid)
                {
                    continue;
                }

                var node = new EtsMapNode { Uid = uid, BackwardItem = back, ForwardItem = forward, Flags = BitConverter.ToUInt32(b, o + 52) };
                node.Position[0] = BitConverter.ToInt32(b, o + 8) / 256f;
                node.Position[1] = BitConverter.ToInt32(b, o + 12) / 256f;
                node.Position[2] = BitConverter.ToInt32(b, o + 16) / 256f;
                node.Rotation[3] = BitConverter.ToSingle(b, o + 20);
                node.Rotation[0] = BitConverter.ToSingle(b, o + 24);
                node.Rotation[1] = BitConverter.ToSingle(b, o + 28);
                node.Rotation[2] = BitConverter.ToSingle(b, o + 32);
                return node;
            }

            return null;
        }

        private void PlacePrefab(EtsMapPrefab prefab)
        {
            float px = (prefab.KdopMin[0] + prefab.KdopMax[0]) / 2f, pz = (prefab.KdopMin[2] + prefab.KdopMax[2]) / 2f;
            bool garage = StandsOf(prefab.PrefabToken).Exists(s => s.Type == EtsPrefabDescriptor.SpawnGarage);

            foreach (CityInfo city in _cities.Values)
            {
                if (city.Def == null)
                {
                    continue;
                }

                bool near = false, inArea = false;

                foreach (Area a in city.Areas)
                {
                    if (px >= a.X0 - AreaMargin && px <= a.X1 + AreaMargin && pz >= a.Z0 - AreaMargin && pz <= a.Z1 + AreaMargin) inArea = true;
                    if (px >= a.X0 - AreaMargin - YardRadius && px <= a.X1 + AreaMargin + YardRadius &&
                        pz >= a.Z0 - AreaMargin - YardRadius && pz <= a.Z1 + AreaMargin + YardRadius) near = true;
                }

                if (!near)
                {
                    continue;
                }

                city.Near.Add(prefab);

                if (inArea && garage)
                {
                    city.Garages.Add(prefab);
                }
            }
        }


        /// <summary>A prefab model's type 14, 10 and 1 points, in its descriptor's order (cached per model).</summary>
        private List<EtsPrefabDescriptor.SpawnPoint> StandsOf(ulong prefabToken)
        {
            if (_standsByToken.TryGetValue(prefabToken, out List<EtsPrefabDescriptor.SpawnPoint> cached))
            {
                return cached;
            }

            var stands = new List<EtsPrefabDescriptor.SpawnPoint>();
            EtsSiiUnit def = _map.PrefabDefinition(prefabToken);
            string ppdPath = def != null ? def.GetString("prefab_desc", null) : null;
            EtsPrefabDescriptor ppd = string.IsNullOrEmpty(ppdPath) ? null : _map.Descriptor(ppdPath, out _);

            if (ppd != null)
            {
                foreach (EtsPrefabDescriptor.SpawnPoint point in ppd.SpawnPoints)
                {
                    if (Array.IndexOf(StandTypes, point.Type) >= 0) stands.Add(point);
                }
            }

            _standsByToken[prefabToken] = stands;
            return stands;
        }

        private EtsHqCity Resolve(CityInfo info)
        {
            // The biggest area is the city's dot (probe 1647).
            Area main = info.Areas[0];
            foreach (Area a in info.Areas) if (a.Size > main.Size) main = a;

            // The garage: the one nearest the centre when there is more than one.
            EtsMapPrefab yard = null;
            float best = float.MaxValue;

            foreach (EtsMapPrefab g in info.Garages)
            {
                float gx = (g.KdopMin[0] + g.KdopMax[0]) / 2f - main.CX, gz = (g.KdopMin[2] + g.KdopMax[2]) / 2f - main.CZ;
                float d = gx * gx + gz * gz;
                if (d < best) { best = d; yard = g; }
            }

            if (yard == null)
            {
                // Not an HQ city: no garage in its areas (Antwerp, Eindhoven, Harwich, Hull, Travemunde, Trieste).
                return null;
            }

            List<EtsHqSpot> anchorStands = SpotsOf(_map, yard, true, NodesOf(yard));
            EtsHqSpot anchor = anchorStands.Find(s => s.Type == EtsPrefabDescriptor.SpawnGarage);
            bool companyYard = false;
            ulong yardCompany = 0;

            if (anchor == null)
            {
                // The garage's own transform could not be made (its node or descriptor missing): the nearest company
                // prefab of the city with parking or trailer points stands in - never its type-13 marker (C23).
                _fallbacks++;
                companyYard = true;
                best = float.MaxValue;

                foreach (EtsMapCompany company in _companies)
                {
                    if (company.CityToken != info.Token || company.PrefabUid == 0) continue;
                    EtsMapPrefab p = _prefabs.Find(q => q.Uid == company.PrefabUid);
                    if (p == null) continue;
                    List<EtsHqSpot> s = SpotsOf(_map, p, true, NodesOf(p));
                    if (s.Count == 0) continue;
                    float dx = s[0].GameX - main.CX, dz = s[0].GameZ - main.CZ, d = dx * dx + dz * dz;
                    if (d < best) { best = d; anchor = s[0]; yard = p; yardCompany = company.CompanyToken; }
                }

                if (anchor == null)
                {
                    // Nothing of the city can be placed, not even with nodes recovered from the sector's bytes (none on
                    // 1.61 since RecoverNode; before it Dijon ended here, probe 1883). The city stays on the map, as it is
                    // in the game, with no stand: its pin is shown disabled and TryResolveYard refuses it.
                    _unplaced++;
                    yard = info.Garages[0];
                    anchor = new EtsHqSpot
                    {
                        GameX = (yard.KdopMin[0] + yard.KdopMax[0]) / 2f, GameY = (yard.KdopMin[1] + yard.KdopMax[1]) / 2f,
                        GameZ = (yard.KdopMin[2] + yard.KdopMax[2]) / 2f, PrefabUid = yard.Uid
                    };
                    companyYard = false;
                }
            }

            // Every stand within the radius, on any prefab near the city.
            var candidates = new List<EtsHqSpot>();

            foreach (EtsMapPrefab p in anchor.Type == 0 ? new List<EtsMapPrefab>() : info.Near)
            {
                foreach (EtsHqSpot s in SpotsOf(_map, p, true, NodesOf(p)))
                {
                    float dx = s.GameX - anchor.GameX, dz = s.GameZ - anchor.GameZ;
                    s.Distance = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (s.Distance <= YardRadius) candidates.Add(s);
                }
            }

            candidates.Sort((a, b) =>
            {
                int ra = Array.IndexOf(StandTypes, a.Type), rb = Array.IndexOf(StandTypes, b.Type);
                if (ra != rb) return ra.CompareTo(rb);
                int byDistance = a.Distance.CompareTo(b.Distance);
                return byDistance != 0 ? byDistance : a.PrefabUid.CompareTo(b.PrefabUid);
            });

            var spots = new List<EtsHqSpot>();

            foreach (EtsHqSpot s in candidates)
            {
                bool clash = false;

                foreach (EtsHqSpot taken in spots)
                {
                    float dx = s.GameX - taken.GameX, dz = s.GameZ - taken.GameZ;
                    if (dx * dx + dz * dz < MinimumSpacing * MinimumSpacing) { clash = true; break; }
                }

                if (clash) { _dropped++; continue; }
                spots.Add(s);
            }

            EtsHqSpot first = spots.Count > 0 ? spots[0] : anchor;
            float yawRad = first.Yaw * (float)Math.PI / 180f;

            // Unity right of a yaw is (cos, 0, -sin); in game coordinates z is negated.
            float rightX = (float)Math.Cos(yawRad), rightZ = (float)Math.Sin(yawRad);
            var arrival = new Vector3(first.GameX + rightX * ArrivalSideStep, first.GameY, first.GameZ + rightZ * ArrivalSideStep);

            string countryId = info.Def.GetString("country", "") ?? "";
            EtsSiiUnit country = _countryDefs != null ? _countryDefs.Find("country.data." + countryId) : null;
            EtsSiiUnit model = _map.PrefabDefinition(yard.PrefabToken);
            // "map_x_offsets[]: -38" eight times, one per zoom level: an array, not a tuple (GetFloats reads tuples).
            List<string> labelX = info.Def.GetArray("map_x_offsets"), labelY = info.Def.GetArray("map_y_offsets");
            bool recovered = false;

            foreach (EtsHqSpot s in spots)
            {
                recovered |= _recoveredFor.Contains(s.PrefabUid);
            }

            recovered |= _recoveredFor.Contains(yard.Uid);

            var city = new EtsHqCity
            {
                Token = info.Token,
                Id = info.Id,
                Name = Words(info.Def.GetString("city_name_localized", null), info.Def.GetString("city_name", info.Id)),
                Country = countryId,
                CountryName = country != null ? Words(country.GetString("name_localized", null), country.GetString("name", countryId)) : countryId,
                CountryIso = country != null ? country.GetString("iso_country_code", "") ?? "" : "",
                GameX = main.CX, GameY = main.CY, GameZ = main.CZ,
                Population = info.Def.GetInt("population", 0),
                U = (main.CX - _mapX0) / _mapW,
                V = (main.CZ - _mapZ0) / _mapH,
                YardModel = model != null ? model.GetString("model_desc", EtsToken.Decode(yard.PrefabToken)) : EtsToken.Decode(yard.PrefabToken),
                CompanyYard = companyYard,
                NodeRecovered = recovered,
                LabelDx = labelX.Count > 7 ? EtsSiiValue.ParseFloat(labelX[7], 0f) : 0f,
                LabelDy = labelY.Count > 7 ? EtsSiiValue.ParseFloat(labelY[7], 0f) : 0f,
                Spots = spots.ToArray(),
                SpawnSpots = spots.Count
            };

            city.Yard = new EtsHqChoice
            {
                City = info.Token,
                YardPrefabUid = yard.Uid,
                YardCompany = yardCompany,
                OriginGame = new Vector3(anchor.GameX, anchor.GameY, anchor.GameZ),
                ArrivalGame = arrival,
                ArrivalYaw = Normalise(first.Yaw - 90f)
            };

            return city;
        }

        /// <summary>The builder's stage (0 definitions, 1 sectors, 5 second node pass, 2 prefabs, 3 cities, 4 done), for measurements.</summary>
        public int Stage => _stage;

        /// <summary>
        /// Every stand (types 14, 10, 1; closer than MinimumSpacing to a nearer one dropped) of every prefab the build
        /// kept within radius metres of a point, nearest first - to measure how far a yard would have to reach for
        /// a number of trucks (U18). The build keeps every prefab of the map that carries a stand.
        /// </summary>
        public List<EtsHqSpot> StandsNear(float gameX, float gameZ, float radius)
        {
            var found = new List<EtsHqSpot>();

            foreach (EtsMapPrefab p in _prefabs)
            {
                float px = (p.KdopMin[0] + p.KdopMax[0]) / 2f - gameX, pz = (p.KdopMin[2] + p.KdopMax[2]) / 2f - gameZ;
                if (px * px + pz * pz > (radius + 200f) * (radius + 200f)) continue;

                foreach (EtsHqSpot s in SpotsOf(_map, p, true, NodesOf(p)))
                {
                    float dx = s.GameX - gameX, dz = s.GameZ - gameZ;
                    s.Distance = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (s.Distance <= radius) found.Add(s);
                }
            }

            found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            var kept = new List<EtsHqSpot>();

            foreach (EtsHqSpot s in found)
            {
                if (!kept.Exists(t => (t.GameX - s.GameX) * (t.GameX - s.GameX) + (t.GameZ - s.GameZ) * (t.GameZ - s.GameZ) < MinimumSpacing * MinimumSpacing)) kept.Add(s);
            }

            return kept;
        }

        /// <summary>For the harness: a defined city's garages and what their transform gave.</summary>
        public string Explain(string cityId)
        {
            foreach (CityInfo c in _cities.Values)
            {
                if (c.Id != cityId) continue;
                var sb = new StringBuilder(cityId + ": " + c.Areas.Count + " areas, " + c.Garages.Count + " garages, " + c.Near.Count + " prefabs near");

                foreach (EtsMapPrefab g in c.Garages)
                {
                    EtsSiiUnit def = _map.PrefabDefinition(g.PrefabToken);
                    ulong node = g.NodeUids.Length > 0 ? g.NodeUids[0] : 0;
                    sb.Append("; garage " + g.Uid.ToString("x16") + " " + (def != null ? def.GetString("prefab_desc", "?") : "no def") + ", node " + node.ToString("x16") +
                              (FindNode(node) != null ? " found" : " MISSING") + ", origin " + g.Origin + ", stands " + StandsOf(g.PrefabToken).Count + ", placed " + SpotsOf(_map, g, true, NodesOf(g)).Count);
                }

                return sb.ToString();
            }

            return cityId + ": not on the map";
        }

        private void Summarise()
        {
            int min = int.MaxValue, max = 0, total = 0, under16 = 0;

            foreach (EtsHqCity c in Result)
            {
                min = Math.Min(min, c.SpawnSpots);
                max = Math.Max(max, c.SpawnSpots);
                total += c.SpawnSpots;
                if (c.SpawnSpots < 16) under16++;
            }

            int defined = 0;
            foreach (CityInfo c in _cities.Values) if (c.Def != null) defined++;

            Report = "HQ catalogue: " + Result.Count + " cities (of " + defined + " defined, " + _cities.Count + " on the map), " + _prefabsRead +
                     " prefabs read (" + _prefabs.Count + " with a stand), " + _standsByToken.Count + " prefab models looked at; stands per yard " + (Result.Count > 0 ? min + ".." + max : "-") +
                     " (" + total + " in all, " + under16 + " yards under 16), " + _dropped + " stands dropped as closer than " + MinimumSpacing +
                     " m, " + _fallbacks + " garages not placeable (" + (_fallbacks - _unplaced) + " took a company yard, " + _unplaced + " left without a stand), " +
                     _recovered + " nodes recovered from sector bytes (" + _recoveredFor.Count + " prefabs), " + Result.FindAll(c => c.NodeRecovered).Count + " cities flagged; map scale " + MapScale + (MapScaleRead ? "" : " (assumed)") +
                     (_readsSectors ? "; sectors read by the catalogue itself" : "; sectors from the map index");
        }

        // ------------------------------------------------------------------ the prefab transform

        /// <summary>
        /// Every type 14, 10 and 1 point of a prefab item, where it stands in the game's coordinates and which way
        /// (Unity yaw), put through the prefab's own frame exactly as EtsMapBuilder.TryPrefabSpawn does it (owner F:
        /// EtsMapBuilder.TryPrefabFrame with the node lookup given, the map viewer's centre at the game origin): the
        /// point = frame origin + R * point, its facing R * the point's own rotation * forward, its yaw atan2(x, z).
        /// With moveTrailerBays, a type-1 point's stand is moved 13.68 m ahead along that facing (on a tilted prefab
        /// the stand stays on the prefab's plane). Empty when the prefab has no such point or no frame (its node or
        /// descriptor missing). Game coordinates are Unity's with z negated (EtsModelBuilder.ToUnity); floats, so a
        /// position 60 km from the origin is good to ~4 mm.
        /// </summary>
        public static List<EtsHqSpot> SpotsOf(EtsMap map, EtsMapPrefab prefab, bool moveTrailerBays)
        {
            return SpotsOf(map, prefab, moveTrailerBays, map.FindNode);
        }

        /// <summary>As SpotsOf, with the map nodes looked up by findNode.</summary>
        public static List<EtsHqSpot> SpotsOf(EtsMap map, EtsMapPrefab prefab, bool moveTrailerBays, Func<ulong, EtsMapNode> findNode)
        {
            var result = new List<EtsHqSpot>();
            EtsSiiUnit def = map.PrefabDefinition(prefab.PrefabToken);

            if (def == null || prefab.NodeUids.Length == 0)
            {
                return result;
            }

            EtsPrefabDescriptor ppd = map.Descriptor(def.GetString("prefab_desc"), out _);

            if (ppd == null || ppd.Nodes.Length == 0 || ppd.SpawnPoints.Length == 0 ||
                !EtsMapBuilder.TryPrefabFrame(prefab, ppd, Vector3.zero, findNode, out Vector3 origin, out Quaternion rotation, out _, out _))
            {
                return result;
            }

            foreach (EtsPrefabDescriptor.SpawnPoint point in ppd.SpawnPoints)
            {
                if (Array.IndexOf(StandTypes, point.Type) < 0)
                {
                    continue;
                }

                Vector3 world = origin + rotation * EtsModelBuilder.ToUnity(point.Position);
                Vector3 facing = rotation * (EtsModelBuilder.ToUnityRotation(point.Rotation) * Vector3.forward);

                if (facing.x * facing.x + facing.z * facing.z < 0.0001f)
                {
                    facing = rotation * Vector3.forward;
                }

                float yaw = (float)(Math.Atan2(facing.x, facing.z) * 180.0 / Math.PI);

                if (moveTrailerBays && point.Type == EtsPrefabDescriptor.SpawnTrailer)
                {
                    world += facing.normalized * TrailerCouplingOffset;
                }

                result.Add(new EtsHqSpot
                {
                    GameX = world.x, GameY = world.y, GameZ = -world.z, Yaw = Normalise(yaw), Type = point.Type, PrefabUid = prefab.Uid
                });
            }

            return result;
        }

        private static float Normalise(float degrees)
        {
            float d = degrees % 360f;
            return d < 0f ? d + 360f : d;
        }

        /// <summary>A name through the locale ("@@szczecin@@" -> "Szczecin"), or the plain one when the key is not there.</summary>
        private static string Words(string localized, string plain)
        {
            if (!string.IsNullOrEmpty(localized) && EtsLocale.Loaded)
            {
                string w = EtsLocale.Resolve(localized);
                if (w.Length > 0 && w.IndexOf("@@", StringComparison.Ordinal) < 0) return w;
            }

            return plain ?? "";
        }
    }
}
