using System;
using System.Collections.Generic;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The game's speeding fines, as rules with no Unity in them (EtsFines drives them in the world, the offline
    /// simulation in DevTools/work/fines drives them on the bench). Everything is read from the player's install or
    /// measured in the 1.61 demo's own program; README "2026-10-04: speeding fines" has the evidence.
    ///
    /// /def/police_data.sii (police.data): fine_amounts[3] 200 "speeding (camera)" and [6] 200 "speeding"; the level
    /// factor min(fine_factor_base 0.2 + level * fine_factor_step 0.08, fine_factor_limit 1.0); the overspeed
    /// progression fine *= min(fine_overspeed_step_multiplier 1.877 ^ (overspeed / fine_overspeed_step 2.777778 m/s),
    /// fine_overspeed_multiplier_limit 12.5); rounding UP to fine_amount_rounding 20; police_nearby_fine_rate 2 with a
    /// police car near. The order of those operations, and that an overspeed under one step (10 km/h) takes no
    /// progression at all, were read off eurotrucks2.exe (the function that references " (+%s)<br>%s", file offset
    /// 0x82b180..0x82b2b0): fine = ceil(amount * level factor * police factor * progression / 20) * 20.
    /// </summary>
    public sealed class EtsPoliceData
    {
        public const int Crash = 0, SpeedingCamera = 3, Speeding = 6;

        public float FactorBase = 0.2f, FactorStep = 0.08f, FactorLimit = 1f;
        public int Rounding = 20;
        public float OverspeedStep = 2.777778f, OverspeedMultiplier = 1.877f, OverspeedLimit = 12.5f;
        public float PoliceNearbyRate = 2f, PoliceNearbyTimer = 15f;
        public readonly List<int> Amounts = new List<int>();
        public readonly List<float> CheckDelay = new List<float>(), PoliceCheckDelay = new List<float>();
        public readonly List<float> Probability = new List<float>(), PoliceProbability = new List<float>();
        public readonly List<string> Messages = new List<string>(), Icons = new List<string>();
        public bool FromFile;

        public static EtsPoliceData Load(EtsFileSystem files)
        {
            var data = new EtsPoliceData();
            EtsSiiFile file = files?.ReadSii("/def/police_data.sii");
            EtsSiiUnit unit = file?.Find("police.data");

            if (unit == null)
            {
                return data;
            }

            data.FromFile = true;
            data.FactorBase = unit.GetFloat("fine_factor_base", data.FactorBase);
            data.FactorStep = unit.GetFloat("fine_factor_step", data.FactorStep);
            data.FactorLimit = unit.GetFloat("fine_factor_limit", data.FactorLimit);
            data.Rounding = unit.GetInt("fine_amount_rounding", data.Rounding);
            data.OverspeedStep = unit.GetFloat("fine_overspeed_step", data.OverspeedStep);
            data.OverspeedMultiplier = unit.GetFloat("fine_overspeed_step_multiplier", data.OverspeedMultiplier);
            data.OverspeedLimit = unit.GetFloat("fine_overspeed_multiplier_limit", data.OverspeedLimit);
            data.PoliceNearbyRate = unit.GetFloat("police_nearby_fine_rate", data.PoliceNearbyRate);
            data.PoliceNearbyTimer = unit.GetFloat("police_nearby_offence_timer", data.PoliceNearbyTimer);

            foreach (string s in unit.GetArray("fine_amounts")) data.Amounts.Add((int)EtsSiiValue.ParseFloat(s, 0f));
            foreach (string s in unit.GetArray("offence_check_delay")) data.CheckDelay.Add(EtsSiiValue.ParseFloat(s, 0f));
            foreach (string s in unit.GetArray("offence_police_check_delay")) data.PoliceCheckDelay.Add(EtsSiiValue.ParseFloat(s, 0f));
            foreach (string s in unit.GetArray("offence_probabilty")) data.Probability.Add(EtsSiiValue.ParseFloat(s, 0f));
            foreach (string s in unit.GetArray("offence_police_probabilty")) data.PoliceProbability.Add(EtsSiiValue.ParseFloat(s, 0f));
            foreach (string s in unit.GetArray("offence_message")) data.Messages.Add(EtsSiiValue.Unquote(s));
            foreach (string s in unit.GetArray("offence_icon")) data.Icons.Add(EtsSiiValue.Unquote(s));
            return data;
        }

        private static T At<T>(List<T> list, int i, T fallback) => i >= 0 && i < list.Count ? list[i] : fallback;

        public int Amount(int offence) => At(Amounts, offence, 200);
        public float Chance(int offence, bool police) => police ? At(PoliceProbability, offence, 0f) : At(Probability, offence, 0f);
        public float Delay(int offence, bool police) => police ? At(PoliceCheckDelay, offence, 0f) : At(CheckDelay, offence, 0f);
        public string Icon(int offence) => At(Icons, offence, "/material/ui/hud/speed_camera.mat");
        public string MessageMarkup(int offence) => At(Messages, offence, "@@offence_speeding@@<color value=@@clr_sel@@>");

        /// <summary>The level factor: min(base + level * step, limit).</summary>
        public float LevelFactor(int level) => Math.Min(FactorBase + Math.Max(0, level) * FactorStep, FactorLimit);

        /// <summary>The progression an overspeed (m/s over the limit) earns: none under one step, else min(m ^ (o / step), limit).</summary>
        public float Progression(float overspeed) =>
            overspeed < OverspeedStep || OverspeedStep <= 0f ? 1f : Math.Min((float)Math.Pow(OverspeedMultiplier, overspeed / OverspeedStep), OverspeedLimit);

        /// <summary>The fine in euros, exactly as the program computes it (class notes).</summary>
        public long Fine(int offence, int level, float overspeed, bool policeNearby)
        {
            float fine = Amount(offence) * LevelFactor(level) * (policeNearby ? PoliceNearbyRate : 1f);

            if (offence == SpeedingCamera || offence == Speeding)
            {
                fine *= Progression(overspeed);
            }

            float rounding = Math.Max(1f, Rounding);
            return (long)(Math.Ceiling(fine / rounding) * rounding);
        }

        /// <summary>
        /// The HUD text the program builds: offence_message (which ends by switching to clr_sel), then for speeding
        /// with an overspeed of a step or more " (+N km/h)" (overspeed * 3.6 rounded, "%u%s%s"), then "&lt;br&gt;" and the
        /// money as a loss. Otherwise "&lt;br&gt;" and the money. Game markup; EtsLocale.Resolve fills the @@keys@@.
        /// </summary>
        public string Markup(int offence, float overspeed, long fine, string unitWord)
        {
            var text = new StringBuilder(MessageMarkup(offence));

            if ((offence == SpeedingCamera || offence == Speeding) && overspeed >= OverspeedStep)
            {
                text.Append(" (+").Append((int)(overspeed * 3.6f + 0.5f)).Append(' ').Append(unitWord).Append(')');
            }

            text.Append("<br>").Append(EtsLocale.Money(-fine));
            return text.ToString();
        }
    }

    /// <summary>
    /// One speed camera: a locator of a sign model whose hookup is a speed_camera unit (/unit/hookup/speed_camera.sii:
    /// speedcam.20 and speedcam.r - trigger_distance 8, measurement_distance 20, reverse_direction on .r; speedcam.toll
    /// with measurement_distance 0). The locators are "ml1", "ml2", "ml3" on the /model/speedcamera models (the
    /// "_fake" UK model has none), each standing on a lane a few metres off the pole.
    ///
    /// Direction, measured (probe conv.cs, Dover and Prague, 9 of 9): a speedcam.20 locator's -Z points against the
    /// traffic of the lane it stands on, speedcam.r's along it - so a camera watches vehicles moving along the
    /// locator's +Z, or -Z when reverse_direction is set (the program negates the direction test on that flag).
    /// Tolerance: the unit attribute 'tolerance' is not in the file, so the program's default stands - 0.1, set by the
    /// speed_camera_u constructor (eurotrucks2.exe file offset 0xa9fbb3: +0x130 measurement_distance = 30.0, +0x134
    /// tolerance = 0.1; the attribute table at 0x2d9d6b0 names those two offsets). The check (0xaa0380): the camera
    /// fires when the speed is at least (1 + tolerance) * the lane's limit for the vehicle, i.e. more than 10 % over.
    /// </summary>
    public sealed class EtsSpeedCamera
    {
        public ulong SignUid;
        public string SignModel;
        public string ModelPath;
        public string Locator;
        public string Hookup;

        /// <summary>Game frame, metres.</summary>
        public float X, Y, Z;

        /// <summary>The direction watched traffic moves in, game frame, horizontal unit vector.</summary>
        public float DirX, DirZ;

        public float TriggerDistance = 8f, MeasurementDistance = 20f, Tolerance = 0.1f;
        public bool Reverse;
    }

    /// <summary>A sign carrying a traffic rule that changes the speed limit (or ends one), in the game frame.</summary>
    public sealed class EtsLimitSign
    {
        public ulong Uid;
        public string Rule;
        public float X, Y, Z;

        /// <summary>The node's -Z (horizontal unit): the traffic it governs moves this way (probe: 196 of 198 limit signs).</summary>
        public float DirX, DirZ;

        public EtsSpeedRules.RuleEffect Effect;
    }

    /// <summary>
    /// Which speed limit applies to the player's truck, from the game's files:
    ///   /def/country/&lt;c&gt;/speed_limits.sii, unit vehicle_speed_class truck: per lane speed class a limit, an urban_limit
    ///     and a max_limit (Czech: local 80/50 max 80; UK: local 50/30, expressway 60/30, motorway 60/60 - miles an hour,
    ///     the country has imperial_units: true in /def/country/uk.sui).
    ///   /def/world/traffic_lane.sii: a lane type's speed_class; a road look's lanes_right/lanes_left name lane types.
    ///   /def/world/traffic_rules.sii: rule "speed_limit" num_params[0] N (0 ends the limit; "implicit" is the reset at a
    ///     crossroad end), num_params[1] 1 = the vehicle types listed are excluded, str_params "truck_all" = trucks;
    ///     additional_rules chain (limit_100_60 is 100 for others and limit_60_t for trucks); city_start/city_end add
    ///     urban_start/urban_end and impl_reset; motorway_st/expressw_st/..._end reset too.
    /// A sign's rule comes from its sign model's traffic_rule (/def/world/sign.sii).
    /// How the program attaches a sign to the lanes it governs is not decoded; here a sign governs the traffic moving
    /// along its -Z that passes it (EtsFineJudge). How it caps a sign's number for trucks by max_limit is not decoded
    /// either; the cap is applied (the file's word "max" for the truck class).
    /// </summary>
    public sealed class EtsSpeedRules
    {
        public sealed class RuleEffect
        {
            /// <summary>Km/h (or mph in an imperial country) for a truck; 0 none; -1 the rule ends the sign limit.</summary>
            public float TruckLimit;
            public bool EndsLimit;
            public int Urban; // +1 urban starts, -1 ends, 0 no change
            public string Name;
        }

        private readonly EtsFileSystem _files;
        private readonly Dictionary<string, string> _laneClass = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, float[]>> _truck = new Dictionary<string, Dictionary<string, float[]>>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _imperial = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, RuleEffect> _rules = new Dictionary<string, RuleEffect>(StringComparer.Ordinal);
        private EtsSiiFile _ruleFile, _countryFile;

        public EtsSpeedRules(EtsFileSystem files)
        {
            _files = files;
            EtsSiiFile lanes = files.ReadDefinitionFamily("/def/world", "traffic_lane");

            foreach (EtsSiiUnit unit in lanes.Units)
            {
                _laneClass[unit.Name] = unit.GetString("speed_class", "local_road");
            }
        }

        public string LaneClass(string laneType) => laneType != null && _laneClass.TryGetValue(laneType, out string c) ? c : null;

        public bool Imperial(string country)
        {
            if (string.IsNullOrEmpty(country)) return false;
            if (_imperial.TryGetValue(country, out bool known)) return known;
            if (_countryFile == null) _countryFile = _files.ReadDefinitionFamily("/def", "country");
            EtsSiiUnit unit = _countryFile.Find("country.data." + country);
            bool imperial = unit != null && unit.GetBool("imperial_units", false);
            _imperial[country] = imperial;
            return imperial;
        }

        /// <summary>Km/h per unit of the country's limits: 1.609344 where the country counts in miles.</summary>
        public float UnitKmh(string country) => Imperial(country) ? 1.609344f : 1f;

        /// <summary>{ limit, urban_limit, max_limit } for a truck on a lane speed class, in the country's unit; null when absent.</summary>
        public float[] TruckRow(string country, string speedClass)
        {
            if (string.IsNullOrEmpty(country)) return null;

            if (!_truck.TryGetValue(country, out Dictionary<string, float[]> table))
            {
                table = new Dictionary<string, float[]>(StringComparer.Ordinal);
                EtsSiiFile file = _files.ReadSii("/def/country/" + country + "/speed_limits.sii");

                if (file != null)
                {
                    foreach (EtsSiiUnit unit in file.Units)
                    {
                        if (unit.GetString("vehicle_speed_class") != "truck") continue;
                        List<string> classes = unit.GetArray("lane_speed_class"), limit = unit.GetArray("limit"),
                            urban = unit.GetArray("urban_limit"), max = unit.GetArray("max_limit");

                        for (int i = 0; i < classes.Count; i++)
                        {
                            float l = i < limit.Count ? EtsSiiValue.ParseFloat(limit[i], 0f) : 0f;
                            float u = i < urban.Count ? EtsSiiValue.ParseFloat(urban[i], 0f) : 0f;
                            float m = i < max.Count ? EtsSiiValue.ParseFloat(max[i], 0f) : 0f;
                            table[classes[i]] = new[] { l, u, m };
                        }
                    }
                }

                _truck[country] = table;
            }

            return table.TryGetValue(speedClass ?? "local_road", out float[] row) || table.TryGetValue("local_road", out row) ? row : null;
        }

        /// <summary>A truck's limit in km/h on a lane speed class, urban or not, with a sign's number (country unit, 0 none). 0 = unknown.</summary>
        public float TruckLimitKmh(string country, string speedClass, bool urban, float signLimit)
        {
            float[] row = TruckRow(country, speedClass);
            float unit = UnitKmh(country);

            if (row == null)
            {
                return signLimit > 0f ? signLimit * unit : 0f;
            }

            float limit = urban && row[1] > 0f ? row[1] : row[0];

            if (signLimit > 0f)
            {
                limit = row[2] > 0f ? Math.Min(signLimit, row[2]) : signLimit;
            }

            return limit * unit;
        }

        /// <summary>What a traffic rule (traffic_rule.limit_80, .city_start, ...) does to a truck's limit; null when nothing.</summary>
        public RuleEffect Rule(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_rules.TryGetValue(name, out RuleEffect known)) return known;

            var effect = new RuleEffect { Name = name };
            _rules[name] = effect;   // before the walk: a cycle ends here
            Walk(name, effect, 0);

            if (effect.TruckLimit <= 0f && !effect.EndsLimit && effect.Urban == 0)
            {
                _rules[name] = null;
                return null;
            }

            return effect;
        }

        private void Walk(string name, RuleEffect effect, int depth)
        {
            if (depth > 6) return;
            if (_ruleFile == null) _ruleFile = _files.ReadDefinitionFamily("/def/world", "traffic_rules");
            EtsSiiUnit unit = _ruleFile.Find(name);
            if (unit == null) return;

            if (name == "traffic_rule.urban_start") effect.Urban = 1;
            if (name == "traffic_rule.urban_end") effect.Urban = -1;

            if (EtsSiiValue.Unquote(unit.GetString("rule")) == "speed_limit")
            {
                List<string> num = unit.GetArray("num_params"), str = unit.GetArray("str_params");
                float n = num.Count > 0 ? EtsSiiValue.ParseFloat(num[0], 0f) : 0f;
                bool exclude = num.Count > 1 && EtsSiiValue.ParseFloat(num[1], 0f) == 1f && !Has(str, "implicit");
                bool listsTrucks = Has(str, "truck_all") || Has(str, "truck");
                bool typed = str.Count > 0 && !Has(str, "implicit");
                bool forTrucks = !typed || (exclude ? !listsTrucks : listsTrucks);

                if (forTrucks)
                {
                    if (n <= 0f) effect.EndsLimit = true;
                    else effect.TruckLimit = n;
                }
            }

            foreach (string more in unit.GetArray("additional_rules"))
            {
                Walk(more, effect, depth + 1);
            }
        }

        private static bool Has(List<string> list, string word)
        {
            foreach (string s in list)
            {
                if (EtsSiiValue.Unquote(s) == word) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// What a sector holds for fines, gathered once per sector: its speed cameras (map SIGN items whose model has a
    /// speedcam hookup locator), its signs with a speed rule, its roads (for the lane speed class) and its cities
    /// (country, urban). Game frame throughout. Reading a sign model's .pmg for its locators is cached per model.
    /// Prefab descriptors' own signs are not searched (no camera model among the demo's prefab signs was looked for).
    /// </summary>
    public sealed class EtsFineSector
    {
        public readonly List<EtsSpeedCamera> Cameras = new List<EtsSpeedCamera>();
        public readonly List<EtsLimitSign> Signs = new List<EtsLimitSign>();
        public readonly List<EtsMapRoad> Roads = new List<EtsMapRoad>();
        public readonly List<EtsMapCity> Cities = new List<EtsMapCity>();
        public int SectorX, SectorZ;
    }

    public sealed class EtsFineMap
    {
        public sealed class HookupUnit
        {
            public float TriggerDistance = 8f, MeasurementDistance = 30f, Tolerance = 0.1f;
            public bool Reverse;
        }

        public sealed class CameraLocator
        {
            public string Name, Hookup;
            public float[] Position, Rotation;
        }

        public readonly EtsMap Map;
        public readonly EtsSpeedRules Rules;
        private readonly EtsFileSystem _files;
        private readonly Dictionary<string, HookupUnit> _hookups = new Dictionary<string, HookupUnit>(StringComparer.Ordinal);
        private readonly Dictionary<ulong, List<CameraLocator>> _modelLocators = new Dictionary<ulong, List<CameraLocator>>();
        private readonly Dictionary<long, EtsFineSector> _sectors = new Dictionary<long, EtsFineSector>();

        public EtsFineMap(EtsFileSystem files, EtsMap map)
        {
            _files = files;
            Map = map;
            Rules = new EtsSpeedRules(files);
            EtsSiiFile units = files.ReadSii("/unit/hookup/speed_camera.sii");

            if (units != null)
            {
                foreach (EtsSiiUnit unit in units.Units)
                {
                    if (unit.Type != "speed_camera") continue;
                    _hookups[unit.Name] = new HookupUnit
                    {
                        TriggerDistance = unit.GetFloat("trigger_distance", 8f),
                        // The program's defaults (speed_camera_u constructor) where the file is silent: 30 m and 0.1.
                        MeasurementDistance = unit.GetFloat("measurement_distance", 30f),
                        Tolerance = unit.GetFloat("tolerance", 0.1f),
                        Reverse = unit.GetBool("reverse_direction", false)
                    };
                }
            }
        }

        public int HookupUnits => _hookups.Count;

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        public bool HasSector(int x, int z) => _sectors.ContainsKey(Key(x, z));

        public EtsFineSector Sector(int x, int z)
        {
            _sectors.TryGetValue(Key(x, z), out EtsFineSector s);
            return s;
        }

        public void Forget(int x, int z) => _sectors.Remove(Key(x, z));

        public IEnumerable<EtsFineSector> Sectors => _sectors.Values;

        /// <summary>A sector being gathered a slice at a time (Continue), so a big one (2,335 signs, 39 ms in one go) spreads over frames.</summary>
        public sealed class Gathering
        {
            public EtsMapSector Source;
            public EtsFineSector Result;
            public int Next;
        }

        public Gathering Begin(EtsMapSector sector)
        {
            var g = new Gathering { Source = sector, Result = new EtsFineSector { SectorX = sector.SectorX, SectorZ = sector.SectorZ } };
            g.Result.Roads.AddRange(sector.Roads);
            g.Result.Cities.AddRange(sector.Cities);
            return g;
        }

        /// <summary>Up to 'budget' more signs; true (and the sector registered) when the last is done. Main thread (EtsMap's node index).</summary>
        public bool Continue(Gathering g, int budget)
        {
            List<EtsMapSign> signs = g.Source.Signs;
            int end = Math.Min(signs.Count, g.Next + Math.Max(1, budget));

            for (; g.Next < end; g.Next++)
            {
                Add(signs[g.Next], g.Result);
            }

            if (g.Next < signs.Count) return false;
            _sectors[Key(g.Source.SectorX, g.Source.SectorZ)] = g.Result;
            return true;
        }

        /// <summary>Gathers a read map sector in one go (offline probes). Main thread.</summary>
        public EtsFineSector Gather(EtsMapSector sector)
        {
            Gathering g = Begin(sector);
            Continue(g, int.MaxValue);
            return g.Result;
        }

        private void Add(EtsMapSign sign, EtsFineSector fs)
        {
            {
                EtsMapNode node = Map.FindNode(sign.NodeUid);
                if (node == null) return;
                EtsSiiUnit model = Map.SignModel(sign.ModelToken);
                if (model == null) return;

                RuleEffectOf(model, sign, node, fs);

                foreach (CameraLocator locator in Locators(sign.ModelToken, model))
                {
                    if (locator.Hookup == null || !_hookups.TryGetValue(locator.Hookup, out HookupUnit unit)) continue;

                    float[] at = EtsMapNode.Rotate(node.Rotation, locator.Position[0], locator.Position[1], locator.Position[2]);
                    float[] z = EtsMapNode.Rotate(locator.Rotation, 0f, 0f, 1f);
                    float[] dir = EtsMapNode.Rotate(node.Rotation, z[0], z[1], z[2]);
                    float sign2 = unit.Reverse ? -1f : 1f;
                    float len = (float)Math.Sqrt(dir[0] * dir[0] + dir[2] * dir[2]);
                    if (len < 1e-4f) continue;

                    fs.Cameras.Add(new EtsSpeedCamera
                    {
                        SignUid = sign.Uid,
                        SignModel = model.Name,
                        ModelPath = model.GetString("model_desc"),
                        Locator = locator.Name,
                        Hookup = locator.Hookup,
                        X = node.Position[0] + at[0],
                        Y = node.Position[1] + at[1],
                        Z = node.Position[2] + at[2],
                        DirX = sign2 * dir[0] / len,
                        DirZ = sign2 * dir[2] / len,
                        TriggerDistance = unit.TriggerDistance,
                        MeasurementDistance = unit.MeasurementDistance,
                        Tolerance = unit.Tolerance,
                        Reverse = unit.Reverse
                    });
                }
            }
        }

        private void RuleEffectOf(EtsSiiUnit model, EtsMapSign sign, EtsMapNode node, EtsFineSector fs)
        {
            EtsSpeedRules.RuleEffect effect = Rules.Rule(model.GetString("traffic_rule"));
            if (effect == null) return;
            float[] f = node.Forward();
            float len = (float)Math.Sqrt(f[0] * f[0] + f[2] * f[2]);
            if (len < 1e-4f) return;

            fs.Signs.Add(new EtsLimitSign
            {
                Uid = sign.Uid,
                Rule = effect.Name,
                X = node.Position[0], Y = node.Position[1], Z = node.Position[2],
                DirX = f[0] / len, DirZ = f[2] / len,
                Effect = effect
            });
        }

        /// <summary>
        /// A sign model's locators that name a hookup, read straight from its .pmg (version 0x15): locator count at +20,
        /// locator table at +80 (44 bytes: name token, position, scale, rotation w first, u32 offset into the string
        /// pool or ~0), string pool at +88 - "speedcam.20" sits at pool offset 0 of speedcamera_hw_cz.pmg (file +380).
        /// </summary>
        private List<CameraLocator> Locators(ulong token, EtsSiiUnit model)
        {
            if (_modelLocators.TryGetValue(token, out List<CameraLocator> known)) return known;

            // With the scan (ScanCameraModels, off the main thread) every model's answer is known without a file read.
            if (_cameraModels != null)
            {
                if (!_cameraModels.TryGetValue(model.Name, out known)) known = NoLocators;
                _modelLocators[token] = known;
                return known;
            }

            List<CameraLocator> list = ReadLocators(_files, model.GetString("model_desc"));
            _modelLocators[token] = list;
            return list;
        }

        private static readonly List<CameraLocator> NoLocators = new List<CameraLocator>();
        private Dictionary<string, List<CameraLocator>> _cameraModels;

        /// <summary>Whether the camera-model scan has been handed over (SetCameraModels).</summary>
        public bool CameraModelsKnown => _cameraModels != null;

        /// <summary>The scan's result (a Dictionary built off the main thread and not touched there again). Gathered sectors are dropped so they are gathered again with it.</summary>
        public void SetCameraModels(Dictionary<string, List<CameraLocator>> table)
        {
            _cameraModels = table ?? new Dictionary<string, List<CameraLocator>>();
            _modelLocators.Clear();
            _sectors.Clear();
        }

        /// <summary>
        /// Every sign model of /def/world/sign*.sii whose .pmg has a locator hooked to a speedcam unit, by unit name
        /// ("sign.1011"). Pure and thread safe (archive reads; the definition family through the locked ReadSii cache):
        /// 8,551 models, 63 cameras, 1.9 s in mono (probe allsigns.cs) - which is why it runs on a worker. Ten of the 63
        /// (the Dutch motorway portals "stand_portal_..._cam_nl") have no "camera" in their path, so no name filter will do.
        /// </summary>
        public static Dictionary<string, List<CameraLocator>> ScanCameraModels(EtsFileSystem files)
        {
            var table = new Dictionary<string, List<CameraLocator>>(StringComparer.Ordinal);
            EtsSiiFile signs = files.ReadDefinitionFamily("/def/world", "sign");

            foreach (EtsSiiUnit unit in signs.Units)
            {
                List<CameraLocator> list = ReadLocators(files, unit.GetString("model_desc"));
                if (list.Count > 0) table[unit.Name] = list;
            }

            return table;
        }

        private static List<CameraLocator> ReadLocators(EtsFileSystem files, string desc)
        {
            var list = new List<CameraLocator>();
            if (string.IsNullOrEmpty(desc) || !desc.EndsWith(".pmd", StringComparison.Ordinal)) return list;
            byte[] b = files.Read(desc.Substring(0, desc.Length - 4) + ".pmg");
            if (b == null || b.Length < 112 || b[0] != 0x15) return list;

            int count = BitConverter.ToInt32(b, 20), table = BitConverter.ToInt32(b, 80), pool = BitConverter.ToInt32(b, 88);
            if (count <= 0 || count > 4096 || table <= 0 || table + count * 44 > b.Length) return list;

            for (int i = 0; i < count; i++)
            {
                int o = table + i * 44;
                uint h = BitConverter.ToUInt32(b, o + 40);
                if (h == 0xFFFFFFFFu || pool <= 0 || pool + h >= b.Length) continue;
                int s = (int)(pool + h), e = s;
                while (e < b.Length && b[e] != 0) e++;
                string hookup = Encoding.ASCII.GetString(b, s, e - s);
                if (!hookup.StartsWith("speedcam", StringComparison.Ordinal)) continue;

                list.Add(new CameraLocator
                {
                    Name = EtsToken.Decode(BitConverter.ToUInt64(b, o)),
                    Hookup = hookup,
                    Position = new[] { BitConverter.ToSingle(b, o + 8), BitConverter.ToSingle(b, o + 12), BitConverter.ToSingle(b, o + 16) },
                    Rotation = new[] { BitConverter.ToSingle(b, o + 28), BitConverter.ToSingle(b, o + 32), BitConverter.ToSingle(b, o + 36), BitConverter.ToSingle(b, o + 24) }
                });
            }

            return list;
        }

        // ------------------------------------------------------------------ where the truck is

        /// <summary>
        /// The road under a point and the lane speed class of the side whose direction matches the heading (lanes_right
        /// run start to end, lanes_left end to start: EtsLaneGraph's measurement). Searches the gathered sectors round the
        /// point; false when no road centre line is within reach (a junction: prefab lanes' class is not read here).
        /// </summary>
        public bool RoadClassAt(float x, float y, float z, float hx, float hz, out string speedClass, out ulong roadUid)
        {
            speedClass = null;
            roadUid = 0;
            float best = float.MaxValue;
            EtsMapSector.SectorOf(x, z, out int sx, out int sz);

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    EtsFineSector fs = Sector(sx + dx, sz + dz);
                    if (fs == null) continue;

                    foreach (EtsMapRoad road in fs.Roads)
                    {
                        // Bounding volume first (kdop axes 0 and 2 are x and z), widened by a carriageway.
                        if (x < road.KdopMin[0] - 20f || x > road.KdopMax[0] + 20f || z < road.KdopMin[2] - 20f || z > road.KdopMax[2] + 20f) continue;
                        if (y < road.KdopMin[1] - 8f || y > road.KdopMax[1] + 8f) continue;

                        EtsMapNode a = Map.FindNode(road.StartNode), b = Map.FindNode(road.EndNode);
                        if (a == null || b == null) continue;
                        EtsSiiUnit look = Map.RoadLook(road.LookToken);
                        if (look == null) continue;

                        float d = Distance(road, a, b, x, y, z, out float tx, out float tz);
                        List<string> right = look.GetArray("lanes_right"), left = look.GetArray("lanes_left");
                        int lanes = Math.Max(1, Math.Max(right.Count, left.Count));
                        float reach = Math.Abs(look.GetFloat("road_offset", 0f)) + 4.5f * (lanes + 0.5f);   // EtsLaneGraph.LaneWidth
                        if (d > reach || d >= best) continue;

                        bool along = tx * hx + tz * hz >= 0f;
                        List<string> side = along ? right : left;
                        if (side.Count == 0) side = along ? left : right;
                        string cls = null;

                        foreach (string lane in side)
                        {
                            cls = Rules.LaneClass(lane);
                            if (cls != null) break;
                        }

                        if (cls == null) continue;
                        best = d;
                        speedClass = cls;
                        roadUid = road.Uid;
                    }
                }
            }

            return speedClass != null;
        }

        /// <summary>Horizontal distance from a point to a road's centre curve (Hermite on its nodes, 16 pieces), and the curve's direction there.</summary>
        private static float Distance(EtsMapRoad road, EtsMapNode a, EtsMapNode b, float x, float y, float z, out float tx, out float tz)
        {
            float length = road.Length > 0.5f ? road.Length : 1f;
            float[] fa = a.Forward(), fb = b.Forward();
            float best = float.MaxValue;
            tx = 0f;
            tz = 1f;
            float px = a.Position[0], pz = a.Position[2];

            for (int k = 1; k <= 16; k++)
            {
                float t = k / 16f, t2 = t * t, t3 = t2 * t;
                float h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t, h01 = -2 * t3 + 3 * t2, h11 = t3 - t2;
                float qx = h00 * a.Position[0] + h10 * fa[0] * length + h01 * b.Position[0] + h11 * fb[0] * length;
                float qz = h00 * a.Position[2] + h10 * fa[2] * length + h01 * b.Position[2] + h11 * fb[2] * length;
                float ex = qx - px, ez = qz - pz, e2 = ex * ex + ez * ez;
                float u = e2 > 1e-6f ? Math.Max(0f, Math.Min(1f, ((x - px) * ex + (z - pz) * ez) / e2)) : 0f;
                float cx = px + ex * u - x, cz = pz + ez * u - z;
                float d = (float)Math.Sqrt(cx * cx + cz * cz);

                if (d < best)
                {
                    best = d;
                    float el = (float)Math.Sqrt(e2);
                    if (el > 1e-4f) { tx = ex / el; tz = ez / el; }
                }

                px = qx;
                pz = qz;
            }

            return best;
        }

        /// <summary>
        /// The country of the nearest city item among the gathered sectors round the point (3 x 3), and whether the point
        /// is inside a city's area (node +- width/2, height/2) - EtsLaneGraph.CountryAt's rule, over the neighbours too
        /// (a sector with no city of its own would otherwise fall back to Poland: Dover's motorway lanes did).
        /// </summary>
        public string CountryAt(float x, float z, out bool urban)
        {
            urban = false;
            string country = null;
            float best = float.MaxValue;
            EtsMapSector.SectorOf(x, z, out int sx, out int sz);

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    EtsFineSector fs = Sector(sx + dx, sz + dz);
                    if (fs == null) continue;

                    foreach (EtsMapCity city in fs.Cities)
                    {
                        EtsMapNode node = Map.FindNode(city.NodeUid);
                        if (node == null) continue;
                        float ddx = x - node.Position[0], ddz = z - node.Position[2];
                        if (Math.Abs(ddx) <= city.Width * 0.5f && Math.Abs(ddz) <= city.Height * 0.5f) urban = true;
                        float d = ddx * ddx + ddz * ddz;
                        if (d >= best) continue;
                        string named = Map.City(city.CityToken)?.GetString("country");
                        if (string.IsNullOrEmpty(named)) continue;
                        best = d;
                        country = named;
                    }
                }
            }

            return country;
        }
    }

    /// <summary>
    /// The referee: fed the truck's state each tick, it keeps the limit in force and says when a camera has caught it.
    /// Pure, so the offline simulation runs the same code as the world. Game frame; speeds in m/s.
    ///
    ///   - the limit: the truck's row of the country's speed_limits.sii for the road's speed class (urban or not), or the
    ///     last speed-limit sign passed (capped by max_limit), until a sign ends it (limit_end, a city/motorway start or
    ///     end, impl_reset). A sign is passed when the truck crosses the line through it across its direction while
    ///     driving along it (heading . sign direction above 0.5) within SignReach metres of it.
    ///   - a camera: inside its trigger_distance (a sphere round the locator), moving along its direction, at or over
    ///     (1 + tolerance) * the limit -> one offence per pass (re-armed once the truck is out of the trigger again).
    ///     offence_probabilty[3] is 1.0 and offence_check_delay[3] 0 in police_data.sii, so every such pass is fined.
    ///   - the overspeed the fine is reckoned on is |speed| - the limit (the program's camera report: 0xaa0199).
    /// </summary>
    public sealed class EtsFineJudge
    {
        public sealed class Caught
        {
            public int Offence;
            public EtsSpeedCamera Camera;
            public float SpeedKmh, LimitKmh, OverspeedMs;
        }

        /// <summary>How far beside a sign the truck may pass and still be governed by it (a three-lane carriageway and the verge).</summary>
        public float SignReach = 16f;

        public readonly EtsFineMap Map;
        public string Country { get; private set; }
        public string SpeedClass { get; private set; }
        public bool Urban { get; private set; }
        public float SignLimit { get; private set; }
        public string SignRule { get; private set; }
        public float LimitKmh { get; private set; }
        public bool Imperial { get; private set; }

        private int _signUrban;   // +1 / -1 from city signs, 0 none
        private float _lastX, _lastZ;
        private bool _haveLast;
        private readonly HashSet<EtsSpeedCamera> _inside = new HashSet<EtsSpeedCamera>();
        private readonly List<EtsSpeedCamera> _gone = new List<EtsSpeedCamera>();

        public EtsFineJudge(EtsFineMap map)
        {
            Map = map;
        }

        public void Reset()
        {
            _haveLast = false;
            SignLimit = 0f;
            SignRule = null;
            _signUrban = 0;
            _inside.Clear();
        }

        private bool _urbanArea;

        /// <summary>
        /// One tick: position (game frame), horizontal heading of the truck (unit), signed speed along it (m/s). 'locate'
        /// re-reads the road's class and the country (bounding boxes of 9 sectors' roads: EtsFines asks for it twice a
        /// second, the signs and cameras every tick).
        /// </summary>
        public Caught Step(float x, float y, float z, float hx, float hz, float speed, bool locate = true)
        {
            float travelX = speed >= 0f ? hx : -hx, travelZ = speed >= 0f ? hz : -hz;

            if (locate || Country == null)
            {
                if (Map.RoadClassAt(x, y, z, travelX, travelZ, out string cls, out _)) SpeedClass = cls;
                string country = Map.CountryAt(x, z, out _urbanArea);
                if (country != null) Country = country;
                Imperial = Map.Rules.Imperial(Country);
            }

            bool urbanArea = _urbanArea;

            if (_haveLast)
            {
                PassSigns(_lastX, _lastZ, x, y, z, travelX, travelZ);
            }

            _lastX = x;
            _lastZ = z;
            _haveLast = true;
            Urban = _signUrban != 0 ? _signUrban > 0 : urbanArea;
            LimitKmh = Map.Rules.TruckLimitKmh(Country, SpeedClass ?? "local_road", Urban, SignLimit);
            return Cameras(x, y, z, travelX, travelZ, Math.Abs(speed));
        }

        private void PassSigns(float x0, float z0, float x1, float y1, float z1, float hx, float hz)
        {
            EtsMapSector.SectorOf(x1, z1, out int sx, out int sz);

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    EtsFineSector fs = Map.Sector(sx + dx, sz + dz);
                    if (fs == null) continue;

                    foreach (EtsLimitSign sign in fs.Signs)
                    {
                        float ax = x1 - sign.X, az = z1 - sign.Z;
                        if (ax * ax + az * az > 60f * 60f || Math.Abs(y1 - sign.Y) > 10f) continue;
                        if (hx * sign.DirX + hz * sign.DirZ < 0.5f) continue;

                        float before = (x0 - sign.X) * sign.DirX + (z0 - sign.Z) * sign.DirZ;
                        float after = ax * sign.DirX + az * sign.DirZ;
                        float beside = Math.Abs(ax * sign.DirZ - az * sign.DirX);
                        if (before >= 0f || after < 0f || beside > SignReach) continue;

                        Apply(sign.Effect);
                    }
                }
            }
        }

        private void Apply(EtsSpeedRules.RuleEffect effect)
        {
            if (effect.EndsLimit)
            {
                SignLimit = 0f;
                SignRule = effect.Name;
            }

            if (effect.TruckLimit > 0f)
            {
                SignLimit = effect.TruckLimit;
                SignRule = effect.Name;
            }

            if (effect.Urban != 0)
            {
                _signUrban = effect.Urban;
            }
        }

        private Caught Cameras(float x, float y, float z, float hx, float hz, float speed)
        {
            EtsMapSector.SectorOf(x, z, out int sx, out int sz);
            Caught caught = null;
            _gone.Clear();

            foreach (EtsSpeedCamera cam in _inside)
            {
                float dx = x - cam.X, dy = y - cam.Y, dz = z - cam.Z;
                if (dx * dx + dy * dy + dz * dz > cam.TriggerDistance * cam.TriggerDistance) _gone.Add(cam);
            }

            foreach (EtsSpeedCamera cam in _gone) _inside.Remove(cam);

            for (int ddx = -1; ddx <= 1; ddx++)
            {
                for (int ddz = -1; ddz <= 1; ddz++)
                {
                    EtsFineSector fs = Map.Sector(sx + ddx, sz + ddz);
                    if (fs == null) continue;

                    foreach (EtsSpeedCamera cam in fs.Cameras)
                    {
                        float dx = x - cam.X, dy = y - cam.Y, dz = z - cam.Z;
                        if (dx * dx + dy * dy + dz * dz > cam.TriggerDistance * cam.TriggerDistance || _inside.Contains(cam)) continue;
                        if (hx * cam.DirX + hz * cam.DirZ < 0f) continue;   // the program's direction test: dot >= 0
                        _inside.Add(cam);

                        if (LimitKmh <= 0f || speed * 3.6f < (1f + cam.Tolerance) * LimitKmh) continue;

                        caught = new Caught
                        {
                            Offence = EtsPoliceData.SpeedingCamera,
                            Camera = cam,
                            SpeedKmh = speed * 3.6f,
                            LimitKmh = LimitKmh,
                            OverspeedMs = Math.Max(0f, speed - LimitKmh / 3.6f)
                        };
                    }
                }
            }

            return caught;
        }

        /// <summary>The limit as the sign shows it: km/h, or mph in an imperial country (the speed_limits.sii figure).</summary>
        public int SignFigure => LimitKmh <= 0f ? 0 : (int)Math.Round(Imperial ? LimitKmh / 1.609344f : LimitKmh);
    }
}
