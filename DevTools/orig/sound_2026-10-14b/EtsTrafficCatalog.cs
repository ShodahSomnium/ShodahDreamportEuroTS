using System;
using System.Collections.Generic;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// What the game's files say about traffic, read once: the lane types and their spawn rules, every country's
    /// speed limits and traffic mix, and the vehicles. Pure (no Unity). Every number here is from the demo 1.61.1.0.
    ///
    /// Lanes. /def/world/traffic_lane.sii: 41 lane types. Each names a speed_class (local_road, expressway, motorway,
    /// ...) and traffic rules; a road vehicle may use a lane whose rules include traffic_rule.road and not
    /// bus_lane/bus_transit. Its "density" rules (traffic_rules_spawn.sui) say WHAT spawns there: str_params[1] is a
    /// vehicle type with tag filters ("car", "truck", "truck_light", "truck_medium", "bus", "police !customs"), and
    /// num_params come in threes (from hour, frequency, count limit). A motorway lane (s_car_h, s_trl_r, s_trm_r,
    /// s_truck_h, s_bus_h, s_police) allows 30 cars, 1 van, 1 lorry, 10 semi trucks, 2 buses and 1 police car by day.
    /// The count limit is used as the type's weight (the frequency's unit is NOT DETERMINED); the slot is the one
    /// whose hour is the latest at or before Hour.
    ///
    /// Vehicles. /def/vehicle/traffic_storage_&lt;type&gt;.sii (with its family, e.g. traffic_storage_truck.daf_2021.sii):
    /// traffic_vehicle units with a chassis (model + lods), wheels, colours, max_speed, vehicle_mass, engine_power,
    /// tags, trailer_chains, spawn_ratio, variant[], spawn_condition[]. The game logged (game.log.txt 00:00:45)
    /// "Loaded traffic vehicles of type 'car': 99 ... 'truck': 72 ... 'bus': 32 ... 'police': 68". The table keeps the
    /// road types the lane rules spawn: car, police, truck_light, truck_medium, truck_heavy, truck, bus (fire and
    /// medical are named only by urban rules no lane type uses). Left out: units listed as another's variant[] (the
    /// game spawns them as variants of their parent), units with a spawn_condition (a named city), and units towing
    /// anything but a semi-trailer (caravans, car trailers: not built here).
    ///
    /// Mix. A vehicle's weight in a country is its own spawn_ratio, replaced by the country's
    /// /def/country/&lt;c&gt;/traffic.sii country_traffic_info for that object (the last one read wins, as the file says).
    /// A unit without spawn_ratio weighs 1.0: the traffic_vehicle_u constructor stores 1.0f at the attribute's offset
    /// 0x260 (eurotrucks2.exe 1.61 file offset 0x97f611, next to displacement_limit = -1 at 0x258; the attribute table
    /// entry 'spawn_ratio' at 0x2d865e8 gives the offset); traffic_trailer_u the same at 0x208 (0x979c7d). This is why
    /// the UK names traffic.a3/audi_a6/bmw5 with 0.
    ///
    /// Trailers. traffic_vehicle trailer_chains "truck_semi" (67 tractors) or "truck_semi low" (14) take a trailer from
    /// traffic_trailer_type truck_semi's storage (/def/vehicle/traffic_storage_trailer_semi.sii, 24 loaded), filtered by
    /// the chain's tags (low: tags[] low), weighted by the trailer's spawn_ratio. Which trailer is a pure function of
    /// the car's slot and serial (TrailerFor), so every client draws the same one with nothing on the wire.
    ///
    /// Speed limits. /def/country/&lt;c&gt;/speed_limits.sii: per vehicle speed class (car, truck, bus; the vehicle types'
    /// speed_class in traffic_vehicle_type.sii: police car, truck_light car, truck_medium/heavy truck), per lane speed
    /// class, a limit, an urban limit and (trucks, buses) max_limit. In a country whose country_data says
    /// imperial_units (the UK) the figures are mph and are converted here (x 1.609344).
    ///
    /// Countries. /def/country.sii country_data units (country_id, imperial_units, pos). The map's road and prefab nodes
    /// carry the country id in their flags (EtsLaneGraph), which is how a lane knows its country.
    ///
    /// Built off the main thread (EtsTraffic posts the constructor and Prepare to EtsWorker), so it reads its files itself,
    /// straight from the archives, never through EtsFileSystem.ReadSii, whose cache the main thread writes. After the
    /// constructor everything is fixed except each vehicle's bounds, which Prepare fills (an array swapped in whole).
    /// </summary>
    public sealed class EtsTrafficCatalog
    {
        // ------------------------------------------------------------------ kinds

        public const int KindCar = 0, KindPolice = 1, KindVan = 2, KindLorry = 3, KindHeavy = 4, KindTruck = 5, KindBus = 6, KindTrailer = 7;

        /// <summary>The traffic vehicle types the table holds, in table order (traffic_storage_&lt;name&gt;.sii, traffic.vehicle_type.&lt;name&gt;).</summary>
        public static readonly string[] TypeNames = { "car", "police", "truck_light", "truck_medium", "truck_heavy", "truck", "bus" };

        /// <summary>Speed classes of speed_limits.sii.</summary>
        public const int ClassCar = 0, ClassTruck = 1, ClassBus = 2;

        private static readonly string[] ClassNames = { "car", "truck", "bus" };

        public const float MphToKmh = 1.609344f;

        /// <summary>Most vehicles the wire can name (9 bits: EtsTrafficWire).</summary>
        public const int WireVehicles = 512;

        public sealed class LaneType
        {
            public string Name;
            public string SpeedClass;
            public bool Cars;

            /// <summary>Sum of the spawn rules' count limits at the catalog's hour: how busy the lane is (spawn lane choice).</summary>
            public float Density;

            /// <summary>The lane's spawn rules (vehicle type, tag filters, count limit by hour).</summary>
            public readonly List<SpawnRule> Spawns = new List<SpawnRule>();
        }

        public sealed class SpawnRule
        {
            public string Rule;

            /// <summary>Index into TypeNames, -1 for a type this table does not hold.</summary>
            public int Type = -1;

            public string[] Required = new string[0];
            public string[] Excluded = new string[0];
            public float[] Hours = new float[0];
            public float[] Counts = new float[0];

            /// <summary>The count limit of the slot in force at 'hour' (the slot whose hour is the latest at or before it, wrapping).</summary>
            public float CountAt(float hour)
            {
                if (Counts.Length == 0) return 0f;
                int best = -1, latest = 0;

                for (int i = 0; i < Hours.Length; i++)
                {
                    if (Hours[i] <= hour && (best < 0 || Hours[i] > Hours[best])) best = i;
                    if (Hours[i] > Hours[latest]) latest = i;
                }

                return Counts[best >= 0 ? best : latest];
            }
        }

        public sealed class Vehicle
        {
            public int Index;
            public string Unit;
            public int Kind;

            /// <summary>Index into TypeNames (-1: a trailer).</summary>
            public int Type = -1;

            /// <summary>ClassCar, ClassTruck or ClassBus (speed_limits.sii).</summary>
            public int SpeedClass;

            /// <summary>Takes the junction curves meant for large vehicles (EtsPrefabNav bit 6) rather than the small ones' (bit 5).</summary>
            public bool Large;

            public string Model;
            public string[] Lods = new string[0];
            public string Variant = "default";
            public string Look = "default";
            public string FrontWheel;
            public string RearWheel;
            public float[][] Colours = new float[0][];
            public float MaxSpeedKmh = 130f;
            public float Mass = 1500f;

            /// <summary>engine_power (kW), 0 when the unit gives none.</summary>
            public float PowerKw;

            /// <summary>Own spawn_ratio (1.0 when the unit has none, the constructor's default).</summary>
            public float OwnRatio = 1f;

            public string[] Tags = new string[0];

            /// <summary>Tractor: pulls a truck_semi trailer, with these tags required of it ("truck_semi low").</summary>
            public bool Semi;
            public string[] TrailerTags = new string[0];

            /// <summary>Trailer: its cargo_mass and trailer_mass, kg.</summary>
            public float CargoMass;

            /// <summary>
            /// Bounds from the smallest lod, game frame (x right, -z front): min x,y,z, max x,y,z. Zero until read
            /// (Prepare swaps in a filled array in one store, so a reader on another thread sees all or nothing).
            /// </summary>
            public float[] Bounds = new float[6];

            /// <summary>Tractor: the fifth wheel (locator hook_t*), game frame z. Trailer: the kingpin (locator 'hook') z and the mean z of its wheels.</summary>
            public float HitchZ = 1.5f;
            public float KingpinZ = -5f;
            public float AxleZ = 3f;

            public float Length => Math.Max(3f, Bounds[5] - Bounds[2]);
            public float Width => Math.Max(1.5f, Bounds[3] - Bounds[0]);

            /// <summary>Whether the bounds have been read (ReadBounds): the size a spawn needs.</summary>
            public bool BoundsKnown => Bounds[5] - Bounds[2] >= 0.1f;
        }

        public sealed class Country
        {
            public int Id;
            public string Name;
            public bool Imperial;
            public float X, Z;

            /// <summary>Per speed class (ClassCar..ClassBus): lane speed class -> { limit, urban, max } in km/h (converted from mph where Imperial).</summary>
            public readonly Dictionary<string, float[]>[] Limits = { new Dictionary<string, float[]>(StringComparer.Ordinal), new Dictionary<string, float[]>(StringComparer.Ordinal), new Dictionary<string, float[]>(StringComparer.Ordinal) };

            /// <summary>Weight of every vehicle in the table here (own spawn_ratio, overridden by the country's traffic.sii).</summary>
            public float[] Ratios = new float[0];
        }

        private readonly EtsFileSystem _files;
        private readonly Dictionary<string, LaneType> _lanes = new Dictionary<string, LaneType>(StringComparer.Ordinal);
        private readonly Dictionary<string, Country> _countries = new Dictionary<string, Country>(StringComparer.Ordinal);
        private readonly Dictionary<int, Country> _countryById = new Dictionary<int, Country>();
        private readonly List<int> _trailers = new List<int>();

        public readonly List<Vehicle> Vehicles = new List<Vehicle>();
        public int TableHash { get; private set; }

        /// <summary>The hour the spawn rules are read for (12: the midday slot).</summary>
        public readonly float Hour;

        /// <summary>Vehicles on the wire (the rest of the table are trailers, from VehicleCount on).</summary>
        public int VehicleCount { get; private set; }

        public IEnumerable<Country> Countries => _countries.Values;

        public EtsTrafficCatalog(EtsFileSystem files) : this(files, 12f)
        {
        }

        public EtsTrafficCatalog(EtsFileSystem files, float hour)
        {
            _files = files;
            Hour = hour;
            ReadLanes();
            ReadVehicles();
            ReadTrailers();
            ReadCountries();
        }

        /// <summary>
        /// Everything a country's traffic will ask for, read now rather than in a simulation step: the bounds of every
        /// vehicle it spawns, and of every semi-trailer (with the hitch, kingpin and axle locators). ReadBounds is a file
        /// read and a parse, up to 3.2 ms for a car; a tractor or trailer's LOD0 locators cost 3-14 ms more (GDeflate).
        /// </summary>
        public void Prepare(string country)
        {
            Country c = CountryNamed(country);

            for (int i = 0; i < VehicleCount; i++)
            {
                Vehicle v = Vehicles[i];

                if (!v.BoundsKnown && v.Model != null && c != null && i < c.Ratios.Length && c.Ratios[i] > 0f)
                {
                    ReadBounds(v);
                }
            }

            foreach (int t in _trailers)
            {
                if (!Vehicles[t].BoundsKnown) ReadBounds(Vehicles[t]);
            }
        }

        // ------------------------------------------------------------------ files, without the shared cache

        private EtsSiiFile ReadSii(string path)
        {
            string p = "/" + EtsHashFs.Normalize(path);
            string text = _files.ReadText(p);
            return text == null ? null : EtsSii.Parse(text, p, _files.ReadText);
        }

        /// <summary>As EtsFileSystem.ReadDefinitionFamily: base.sii first, then every base.*.sii beside it.</summary>
        private EtsSiiFile ReadFamily(string folder, string baseName)
        {
            var merged = new EtsSiiFile();

            if (!_files.TryList(folder, out List<string> files, out _))
            {
                return merged;
            }

            var matching = new List<string>();

            foreach (string file in files)
            {
                if (file == baseName + ".sii")
                {
                    matching.Insert(0, file);
                }
                else if (file.StartsWith(baseName + ".", StringComparison.Ordinal) && file.EndsWith(".sii", StringComparison.Ordinal))
                {
                    matching.Add(file);
                }
            }

            foreach (string file in matching)
            {
                EtsSiiFile parsed = ReadSii(EtsHashFs.Normalize(folder) + "/" + file);

                if (parsed != null)
                {
                    merged.AddAll(parsed);
                }
            }

            return merged;
        }

        // ------------------------------------------------------------------ lanes

        private void ReadLanes()
        {
            EtsSiiFile rules = ReadFamily("/def/world", "traffic_rules");
            var spawns = new Dictionary<string, SpawnRule>(StringComparer.Ordinal);

            foreach (EtsSiiUnit rule in rules.Units)
            {
                // "density" rules: str_params[last] is a vehicle type and tag filters ("police !customs"), num_params in
                // threes (hour, frequency, count limit).
                if (rule.GetString("rule") != "density") continue;
                List<string> str = rule.GetArray("str_params");
                List<string> num = rule.GetArray("num_params");
                if (str.Count < 2 || num.Count < 3) continue;

                string[] words = EtsSiiValue.Unquote(str[str.Count - 1]).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                var spawn = new SpawnRule { Rule = rule.Name, Type = Array.IndexOf(TypeNames, words[0]) };
                var required = new List<string>();
                var excluded = new List<string>();

                for (int i = 1; i < words.Length; i++)
                {
                    if (words[i].StartsWith("!", StringComparison.Ordinal)) excluded.Add(words[i].Substring(1));
                    else required.Add(words[i]);
                }

                spawn.Required = required.ToArray();
                spawn.Excluded = excluded.ToArray();
                int slots = num.Count / 3;
                spawn.Hours = new float[slots];
                spawn.Counts = new float[slots];

                for (int i = 0; i < slots; i++)
                {
                    spawn.Hours[i] = EtsSiiValue.ParseFloat(num[i * 3], 0f);
                    spawn.Counts[i] = EtsSiiValue.ParseFloat(num[i * 3 + 2], 0f);
                }

                spawns[rule.Name] = spawn;
            }

            EtsSiiFile lanes = ReadFamily("/def/world", "traffic_lane");

            foreach (EtsSiiUnit unit in lanes.Units)
            {
                var type = new LaneType { Name = unit.Name, SpeedClass = unit.GetString("speed_class", "local_road") };
                bool road = false, bus = false;

                foreach (string rule in unit.GetArray("traffic_rules"))
                {
                    road |= rule == "traffic_rule.road";
                    bus |= rule == "traffic_rule.bus_lane" || rule == "traffic_rule.bus_transit";

                    if (spawns.TryGetValue(rule, out SpawnRule spawn) && spawn.Type >= 0)
                    {
                        type.Spawns.Add(spawn);
                        type.Density += spawn.CountAt(Hour);
                    }
                }

                type.Cars = road && !bus;
                _lanes[unit.Name] = type;
            }
        }

        public LaneType Lane(string unitName)
        {
            return unitName != null && _lanes.TryGetValue(unitName, out LaneType type) ? type : null;
        }

        // ------------------------------------------------------------------ countries and limits

        private void ReadCountries()
        {
            EtsSiiFile file = ReadFamily("/def", "country");

            foreach (EtsSiiUnit unit in file.Units)
            {
                if (unit.Type != "country_data" || !unit.Name.StartsWith("country.data.", StringComparison.Ordinal)) continue;
                float[] pos = unit.GetFloats("pos");

                var country = new Country
                {
                    Id = unit.GetInt("country_id", -1),
                    Name = unit.Name.Substring("country.data.".Length),
                    Imperial = unit.GetBool("imperial_units", false),
                    X = pos != null && pos.Length >= 3 ? pos[0] : 0f,
                    Z = pos != null && pos.Length >= 3 ? pos[2] : 0f
                };

                _countries[country.Name] = country;
                if (country.Id >= 0) _countryById[country.Id] = country;
            }

            foreach (Country country in _countries.Values)
            {
                ReadLimits(country);
                ReadRatios(country);
            }
        }

        public Country CountryNamed(string name)
        {
            return name != null && _countries.TryGetValue(name, out Country c) ? c : null;
        }

        public Country CountryById(int id)
        {
            return _countryById.TryGetValue(id, out Country c) ? c : null;
        }

        /// <summary>The country whose country_data pos is nearest (game x, z): the last resort for a lane no node or city places.</summary>
        public Country NearestCountry(float x, float z)
        {
            Country best = null;
            float bestD = float.MaxValue;

            foreach (Country c in _countries.Values)
            {
                float dx = c.X - x, dz = c.Z - z, d = dx * dx + dz * dz;
                if (d < bestD && (c.X != 0f || c.Z != 0f)) { bestD = d; best = c; }
            }

            return best;
        }

        /// <summary>A car's limit in m/s on a lane speed class in a country; urban from a city area.</summary>
        public float SpeedLimit(string country, string speedClass, bool urban)
        {
            return SpeedLimit(CountryNamed(country), speedClass, urban, ClassCar);
        }

        /// <summary>A vehicle speed class's limit in m/s on a lane speed class in a country (max_limit caps it; mph converted).</summary>
        public float SpeedLimit(Country country, string speedClass, bool urban, int vehicleClass)
        {
            Dictionary<string, float[]> table = country != null ? country.Limits[Math.Max(0, Math.Min(2, vehicleClass))] : null;

            if (table == null || table.Count == 0)
            {
                // A class the country does not list falls back to its cars' row.
                table = country != null ? country.Limits[ClassCar] : null;
            }

            if (table != null && (table.TryGetValue(speedClass ?? "local_road", out float[] limit) || table.TryGetValue("local_road", out limit)))
            {
                float kmh = urban ? limit[1] : limit[0];
                if (limit[2] > 0f) kmh = Math.Min(kmh, limit[2]);
                return kmh / 3.6f;
            }

            return (urban ? 50f : 90f) / 3.6f;
        }

        private void ReadLimits(Country country)
        {
            EtsSiiFile file = ReadSii("/def/country/" + country.Name + "/speed_limits.sii");
            if (file == null) return;
            float unit = country.Imperial ? MphToKmh : 1f;

            foreach (EtsSiiUnit row in file.Units)
            {
                int cls = Array.IndexOf(ClassNames, row.GetString("vehicle_speed_class"));
                if (cls < 0) continue;
                List<string> classes = row.GetArray("lane_speed_class"), limits = row.GetArray("limit"), urban = row.GetArray("urban_limit"), max = row.GetArray("max_limit");

                for (int i = 0; i < classes.Count; i++)
                {
                    float l = i < limits.Count ? EtsSiiValue.ParseFloat(limits[i], 90f) : 90f;
                    float u = i < urban.Count ? EtsSiiValue.ParseFloat(urban[i], 50f) : 50f;
                    float m = i < max.Count ? EtsSiiValue.ParseFloat(max[i], 0f) : 0f;

                    // 0 is 'no limit': Germany's car unit, expressway 0 (urban 50) and motorway 0/0, the only zeros of any
                    // country in 1.61 (probe 1544). 130 km/h, the German advisory speed, stands in: spawn speed and the braking
                    // term for a slower lane ahead use the limit directly, so it must be finite; each vehicle's own max_speed
                    // still caps it. An urban 0 falls back to the open-road figure.
                    if (l <= 0f) l = 130f / unit;
                    if (u <= 0f) u = l;
                    country.Limits[cls][classes[i]] = new[] { l * unit, u * unit, m * unit };
                }
            }
        }

        // ------------------------------------------------------------------ vehicles

        private void ReadVehicles()
        {
            uint hash = 2166136261;
            var types = new Dictionary<string, EtsSiiUnit>(StringComparer.Ordinal);
            EtsSiiFile typeFile = ReadFamily("/def/vehicle", "traffic_vehicle_type");
            foreach (EtsSiiUnit unit in typeFile.Units) types[unit.Name] = unit;

            for (int t = 0; t < TypeNames.Length; t++)
            {
                EtsSiiFile storage = ReadFamily("/def/vehicle", "traffic_storage_" + TypeNames[t]);
                string speedClass = types.TryGetValue("traffic.vehicle_type." + TypeNames[t], out EtsSiiUnit typeUnit) ? typeUnit.GetString("speed_class", "car") : "car";
                int cls = Math.Max(0, Array.IndexOf(ClassNames, speedClass));

                // Units another unit lists as its variant[] spawn only as that variant.
                var variants = new HashSet<string>(StringComparer.Ordinal);

                foreach (EtsSiiUnit unit in storage.Units)
                {
                    if (unit.Type == "traffic_vehicle") foreach (string v in unit.GetArray("variant")) variants.Add(v);
                }

                foreach (EtsSiiUnit unit in storage.Units)
                {
                    if (unit.Type != "traffic_vehicle" || variants.Contains(unit.Name) || unit.GetArray("spawn_condition").Count > 0)
                    {
                        continue;
                    }

                    string chain = null;

                    foreach (string c in unit.GetArray("trailer_chains"))
                    {
                        string text = EtsSiiValue.Unquote(c).Trim();
                        if (text.Length > 0) chain = text;
                    }

                    string[] chainWords = chain != null ? chain.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) : new string[0];

                    // Towing anything but a semi-trailer (caravans, car trailers, tractors' trailers): not built here.
                    if (chainWords.Length > 0 && chainWords[0] != "truck_semi")
                    {
                        continue;
                    }

                    var vehicle = new Vehicle
                    {
                        Index = Vehicles.Count,
                        Unit = unit.Name,
                        Type = t,
                        Kind = t,
                        SpeedClass = cls,
                        Large = cls != ClassCar,
                        MaxSpeedKmh = unit.GetFloat("max_speed", 130f),
                        Mass = unit.GetFloat("vehicle_mass", 1500f),
                        PowerKw = unit.GetFloat("engine_power", 0f),
                        OwnRatio = unit.Has("spawn_ratio") ? unit.GetFloat("spawn_ratio", 1f) : 1f,
                        Tags = unit.GetArray("tags").ToArray(),
                        Semi = chainWords.Length > 0
                    };

                    if (vehicle.Semi)
                    {
                        var tags = new List<string>();
                        for (int i = 1; i < chainWords.Length; i++) tags.Add(chainWords[i]);
                        vehicle.TrailerTags = tags.ToArray();
                    }

                    ReadAccessories(storage, unit, vehicle);

                    if (vehicle.Model == null)
                    {
                        continue;
                    }

                    var colours = new List<float[]>();

                    foreach (string colour in unit.GetArray("color"))
                    {
                        float[] rgb = EtsSiiValue.ParseFloats(colour);
                        if (rgb.Length >= 3) colours.Add(rgb);
                    }

                    vehicle.Colours = colours.ToArray();
                    Vehicles.Add(vehicle);

                    foreach (char ch in unit.Name)
                    {
                        hash = (hash ^ ch) * 16777619;
                    }
                }
            }

            VehicleCount = Vehicles.Count;
            TableHash = (int)(hash & 0xFF);
        }

        /// <summary>Chassis (an accessory whose data is accessory_chassis_data), front and rear wheels, from the unit's accessories.</summary>
        private void ReadAccessories(EtsSiiFile storage, EtsSiiUnit unit, Vehicle vehicle)
        {
            foreach (string accessory in unit.GetArray("accessories"))
            {
                EtsSiiUnit link = storage.Find(accessory);
                string dataPath = link?.GetString("data_path");
                EtsSiiUnit data = null;

                if (!string.IsNullOrEmpty(dataPath))
                {
                    EtsSiiFile file = ReadSii(dataPath);
                    data = file != null && file.Units.Count > 0 ? file.Units[0] : null;
                }

                if (data == null) continue;

                if (data.Type == "accessory_chassis_data")
                {
                    vehicle.Model = data.GetString("model");
                    vehicle.Lods = data.GetArray("lods").ToArray();
                    vehicle.Variant = data.GetString("variant", "default");
                    vehicle.Look = data.GetString("look", "default");
                    if (vehicle.Kind == KindTrailer) vehicle.Mass = data.GetFloat("trailer_mass", vehicle.Mass);
                }
                else if (data.Type == "accessory_wheel_data")
                {
                    // Cars: .fwheel / .rwheel; trucks also .rwheel2 (offset 2); trailers .trwheel0..n. Every rear wheel is one model.
                    string model = data.GetString("model");

                    if (accessory.EndsWith(".fwheel", StringComparison.Ordinal))
                    {
                        vehicle.FrontWheel = model;
                    }
                    else if (vehicle.RearWheel == null)
                    {
                        vehicle.RearWheel = model;
                    }
                }
            }
        }

        private void ReadTrailers()
        {
            EtsSiiFile storage = ReadFamily("/def/vehicle", "traffic_storage_trailer_semi");
            uint hash = (uint)TableHash;

            // As the vehicles: a trailer another lists as its variant[] (t_car_trans2..9 of t_car_trans1) or one with a
            // spawn_condition is not a spawn of its own.
            var variants = new HashSet<string>(StringComparer.Ordinal);

            foreach (EtsSiiUnit unit in storage.Units)
            {
                if (unit.Type == "traffic_trailer") foreach (string v in unit.GetArray("variant")) variants.Add(v);
            }

            foreach (EtsSiiUnit unit in storage.Units)
            {
                if (unit.Type != "traffic_trailer" || variants.Contains(unit.Name) || unit.GetArray("spawn_condition").Count > 0) continue;

                var trailer = new Vehicle
                {
                    Index = Vehicles.Count,
                    Unit = unit.Name,
                    Kind = KindTrailer,
                    SpeedClass = ClassTruck,
                    Large = true,
                    Mass = 6000f,
                    CargoMass = unit.GetFloat("cargo_mass", 0f),
                    OwnRatio = unit.Has("spawn_ratio") ? unit.GetFloat("spawn_ratio", 1f) : 1f,
                    Tags = unit.GetArray("tags").ToArray()
                };

                ReadAccessories(storage, unit, trailer);

                if (trailer.Model == null || trailer.OwnRatio <= 0f)
                {
                    continue;
                }

                // A trailer turns no wheel by steering: both wheel slots take the trailer wheel.
                trailer.FrontWheel = trailer.RearWheel;
                Vehicles.Add(trailer);
                _trailers.Add(trailer.Index);
                foreach (char ch in unit.Name) hash = (hash ^ ch) * 16777619;
            }

            TableHash = (int)(hash & 0xFF);
        }

        /// <summary>Reads a vehicle's bounds from its smallest lod (a few kB), so a spawn knows its length without building it; tractors and trailers also their LOD0 locators.</summary>
        public void ReadBounds(Vehicle vehicle)
        {
            string path = vehicle.Lods.Length > 0 ? vehicle.Lods[vehicle.Lods.Length - 1] : vehicle.Model;
            byte[] pmd = path != null ? _files.Read(path) : null;
            byte[] pmg = path != null ? _files.Read(path.Substring(0, path.Length - 4) + ".pmg") : null;

            if (pmd == null || pmg == null)
            {
                return;
            }

            EtsModelData model = EtsModelData.Parse(pmd, pmg, path);
            var bounds = new float[6];

            for (int k = 0; k < 3; k++)
            {
                bounds[k] = model.BoundsMin[k];
                bounds[k + 3] = model.BoundsMax[k];
            }

            if (vehicle.Semi || vehicle.Kind == KindTrailer)
            {
                ReadHitch(vehicle);
            }

            // Last: BoundsKnown turns true only once everything above is in place.
            vehicle.Bounds = bounds;
        }

        /// <summary>
        /// The coupling, from the LOD0 geometry's locator table only (pmg header +20 count, +80 table, 44-byte records:
        /// token, position): a tractor's fifth wheel 'hook_t*' (AI scania_r_2016 hook_t_0 at z 1.48), a trailer's kingpin
        /// 'hook' (curtain_sider ch_3 at z -5.07) and its wheels 'wheel_*' (z 1.54..4.16).
        /// </summary>
        private void ReadHitch(Vehicle vehicle)
        {
            string path = vehicle.Model;
            byte[] b = path != null && path.EndsWith(".pmd", StringComparison.Ordinal) ? _files.Read(path.Substring(0, path.Length - 4) + ".pmg") : null;

            if (b == null || b.Length < 112 || b[0] != 0x15)
            {
                return;
            }

            int count = BitConverter.ToInt32(b, 20), table = BitConverter.ToInt32(b, 80);

            if (count <= 0 || count > 4096 || table <= 0 || table + count * 44 > b.Length)
            {
                return;
            }

            float wheels = 0f;
            int wheelCount = 0;

            for (int i = 0; i < count; i++)
            {
                int o = table + i * 44;
                string name = EtsToken.Decode(BitConverter.ToUInt64(b, o));
                float z = BitConverter.ToSingle(b, o + 16);
                if (name == null) continue;

                if (vehicle.Kind == KindTrailer)
                {
                    if (name == "hook") vehicle.KingpinZ = z;
                    else if (name.StartsWith("wheel_", StringComparison.Ordinal)) { wheels += z; wheelCount++; }
                }
                else if (name.StartsWith("hook_t", StringComparison.Ordinal))
                {
                    vehicle.HitchZ = z;
                }
            }

            if (wheelCount > 0)
            {
                vehicle.AxleZ = wheels / wheelCount;
            }
        }

        // ------------------------------------------------------------------ mixes

        private void ReadRatios(Country country)
        {
            var ratios = new float[Vehicles.Count];
            var byUnit = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < Vehicles.Count; i++)
            {
                ratios[i] = Vehicles[i].OwnRatio;
                byUnit[Vehicles[i].Unit] = i;
            }

            EtsSiiFile file = ReadSii("/def/country/" + country.Name + "/traffic.sii");

            if (file != null)
            {
                foreach (EtsSiiUnit unit in file.Units)
                {
                    string target = unit.GetString("object");

                    if (target != null && byUnit.TryGetValue(target, out int index))
                    {
                        ratios[index] = unit.GetFloat("spawn_ratio", 0f);
                    }
                }
            }

            country.Ratios = ratios;
        }

        /// <summary>The country's spawn ratio per vehicle index (above zero only), for the pool and the log.</summary>
        public Dictionary<int, float> Ratios(string country)
        {
            var result = new Dictionary<int, float>();
            Country c = CountryNamed(country);

            if (c != null)
            {
                for (int i = 0; i < VehicleCount && i < c.Ratios.Length; i++)
                {
                    if (c.Ratios[i] > 0f) result[i] = c.Ratios[i];
                }
            }

            return result;
        }

        /// <summary>Whether a vehicle passes a spawn rule's type and tag filters.</summary>
        public static bool Matches(Vehicle v, SpawnRule rule)
        {
            if (v.Type != rule.Type) return false;
            foreach (string tag in rule.Required) if (Array.IndexOf(v.Tags, tag) < 0) return false;
            foreach (string tag in rule.Excluded) if (Array.IndexOf(v.Tags, tag) >= 0) return false;
            return true;
        }

        // ------------------------------------------------------------------ trailers

        /// <summary>
        /// The semi-trailer a tractor pulls, the same on every client: chosen by the trailers' spawn ratios among those
        /// carrying the chain's tags, with a hash of 'key' (EtsTrafficSim: slot and serial) as the roll. -1: none.
        /// </summary>
        public int TrailerFor(int vehicle, int key)
        {
            if (vehicle < 0 || vehicle >= VehicleCount || !Vehicles[vehicle].Semi || _trailers.Count == 0)
            {
                return -1;
            }

            string[] want = Vehicles[vehicle].TrailerTags;
            float total = 0f;

            foreach (int t in _trailers)
            {
                if (HasTags(Vehicles[t], want)) total += Vehicles[t].OwnRatio;
            }

            if (total <= 0f)
            {
                return -1;
            }

            uint h = (uint)key * 2654435761u ^ (uint)vehicle * 40503u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            float roll = (h & 0xFFFFFF) / 16777216f * total;
            int last = -1;

            foreach (int t in _trailers)
            {
                if (!HasTags(Vehicles[t], want)) continue;
                last = t;
                roll -= Vehicles[t].OwnRatio;
                if (roll <= 0f) return t;
            }

            return last;
        }

        private static bool HasTags(Vehicle trailer, string[] want)
        {
            foreach (string tag in want) if (Array.IndexOf(trailer.Tags, tag) < 0) return false;
            return true;
        }

        /// <summary>The semi-trailers, table indices.</summary>
        public IList<int> Trailers => _trailers;

        /// <summary>
        /// Metres from a tractor's origin back to the rear of the trailer it pulls (its fifth wheel, then the trailer from
        /// kingpin to tail), or the tractor's own half length when it pulls none: the sim's Back.
        /// </summary>
        public float BackOf(int vehicle, int trailer)
        {
            Vehicle v = Vehicles[vehicle];

            if (trailer < 0 || trailer >= Vehicles.Count)
            {
                return v.Length * 0.5f;
            }

            Vehicle t = Vehicles[trailer];
            return Math.Max(v.Length * 0.5f, v.HitchZ + (t.Bounds[5] - t.KingpinZ));
        }

        /// <summary>For the log: "uk 47 vehicles: 30 car, 2 police, ..." - how many of each type the country's mix holds.</summary>
        public string Describe(string country)
        {
            Country c = CountryNamed(country);
            if (c == null) return country + ": no such country";
            var counts = new int[TypeNames.Length];
            int n = 0;

            for (int i = 0; i < VehicleCount; i++)
            {
                if (c.Ratios[i] > 0f) { counts[Vehicles[i].Type]++; n++; }
            }

            var sb = new StringBuilder(country + " (id " + c.Id + (c.Imperial ? ", mph" : "") + "): " + n + " vehicles");
            for (int t = 0; t < TypeNames.Length; t++) sb.Append(", " + counts[t] + " " + TypeNames[t]);
            return sb.ToString();
        }
    }
}
