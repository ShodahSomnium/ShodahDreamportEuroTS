using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// What the game's files say about traffic, read once: the lane types, the countries' speed
    /// limits, and the cars. Pure (no Unity). Every number here is from the demo 1.61.1.0:
    ///
    ///   /def/world/traffic_lane.sii: 41 lane types (Program1380). Each names a speed_class
    ///     (local_road, expressway, motorway, slow_road, rail_*) and traffic rules; a car may use
    ///     a lane whose rules include traffic_rule.road and not bus_lane/bus_transit; the car
    ///     density comes from its s_car_* rule, whose third number is a count limit
    ///     (traffic_rules_spawn.sui: vl 4, l 8, h 30, b 30, u 40, uh 50 by day). The 'frequency'
    ///     number's unit is NOT DETERMINED, so the count limit is used as the relative weight.
    ///   /def/country/&lt;c&gt;/speed_limits.sii: per vehicle speed class, per lane speed class, a
    ///     limit and an urban limit in km/h (Poland, car: local 90/50, expressway 120/50,
    ///     motorway 140/140).
    ///   /def/vehicle/traffic_storage_car.sii: 114 car units (Program1372), every model present
    ///     in the demo archives, each with chassis (model + 3 lods), wheels, colours (linear
    ///     floats), max_speed; /def/country/&lt;c&gt;/traffic.sii gives each a spawn_ratio (Poland:
    ///     29 units above zero, 20 of them cars).
    /// The car table's ORDER is the file's, so every client with the same install numbers the
    /// cars the same; TableHash goes on the wire so a client whose table differs can tell.
    ///
    /// Built off the main thread (EtsTraffic posts the constructor and Prepare to EtsWorker; 37 ms
    /// offline, harness Program1402), so it reads its definition files itself, straight from the
    /// archives, and never through EtsFileSystem.ReadSii, whose cache the main thread writes.
    /// Once handed over, only the main thread uses it.
    /// </summary>
    public sealed class EtsTrafficCatalog
    {
        public sealed class LaneType
        {
            public string Name;
            public string SpeedClass;
            public bool Cars;
            public float Density;
        }

        public sealed class Vehicle
        {
            public int Index;
            public string Unit;
            public string Model;
            public string[] Lods = new string[0];
            public string Variant = "default";
            public string Look = "default";
            public string FrontWheel;
            public string RearWheel;
            public float[][] Colours = new float[0][];
            public float MaxSpeedKmh = 130f;
            public float Mass = 1500f;

            /// <summary>Bounds from the smallest lod, game frame (x right, -z front): min x,y,z, max x,y,z. Zero until read.</summary>
            public float[] Bounds = new float[6];

            public float Length => Math.Max(3f, Bounds[5] - Bounds[2]);
            public float Width => Math.Max(1.5f, Bounds[3] - Bounds[0]);

            /// <summary>Whether the bounds have been read (ReadBounds): the size a spawn needs.</summary>
            public bool BoundsKnown => Bounds[5] - Bounds[2] >= 0.1f;
        }

        private readonly EtsFileSystem _files;
        private readonly Dictionary<string, LaneType> _lanes = new Dictionary<string, LaneType>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, float[]>> _limits = new Dictionary<string, Dictionary<string, float[]>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<int, float>> _ratios = new Dictionary<string, Dictionary<int, float>>(StringComparer.Ordinal);

        public readonly List<Vehicle> Vehicles = new List<Vehicle>();
        public int TableHash { get; private set; }

        public EtsTrafficCatalog(EtsFileSystem files)
        {
            _files = files;
            ReadLanes();
            ReadVehicles();
        }

        /// <summary>
        /// Everything a country's traffic will ask for, read now rather than in a simulation step:
        /// its spawn ratios, its speed limits and the bounds of every car it spawns (ReadBounds is
        /// a file read and a parse, up to 3.2 ms each, Program1402).
        /// </summary>
        public void Prepare(string country)
        {
            SpeedLimit(country, "local_road", false);

            foreach (int index in Ratios(country).Keys)
            {
                Vehicle v = Vehicles[index];

                if (!v.BoundsKnown && v.Model != null)
                {
                    ReadBounds(v);
                }
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
            var density = new Dictionary<string, float>(StringComparer.Ordinal);

            foreach (EtsSiiUnit rule in rules.Units)
            {
                // "density" rules for cars: str_params[1] is "car", num_params in threes
                // (hour, frequency, count limit); the first count limit is the day's.
                List<string> str = rule.GetArray("str_params");
                List<string> num = rule.GetArray("num_params");

                if (str.Count >= 2 && EtsSiiValue.Unquote(str[str.Count - 1]) == "car" && num.Count >= 3)
                {
                    density[rule.Name] = EtsSiiValue.ParseFloat(num[2], 0f);
                }
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

                    if (density.TryGetValue(rule, out float d))
                    {
                        type.Density = Math.Max(type.Density, d);
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

        /// <summary>A car's limit in m/s on a lane speed class in a country; urban from a city area.</summary>
        public float SpeedLimit(string country, string speedClass, bool urban)
        {
            if (!_limits.TryGetValue(country ?? "", out Dictionary<string, float[]> table))
            {
                table = ReadLimits(country);
                _limits[country ?? ""] = table;
            }

            if (table.TryGetValue(speedClass ?? "local_road", out float[] limit) || table.TryGetValue("local_road", out limit))
            {
                return (urban ? limit[1] : limit[0]) / 3.6f;
            }

            return (urban ? 50f : 90f) / 3.6f;
        }

        private Dictionary<string, float[]> ReadLimits(string country)
        {
            var table = new Dictionary<string, float[]>(StringComparer.Ordinal);
            EtsSiiFile file = string.IsNullOrEmpty(country) ? null : ReadSii("/def/country/" + country + "/speed_limits.sii");
            EtsSiiUnit car = null;

            if (file != null)
            {
                foreach (EtsSiiUnit unit in file.Units)
                {
                    if (unit.GetString("vehicle_speed_class") == "car") car = unit;
                }
            }

            if (car != null)
            {
                List<string> classes = car.GetArray("lane_speed_class"), limits = car.GetArray("limit"), urban = car.GetArray("urban_limit");

                for (int i = 0; i < classes.Count; i++)
                {
                    float l = i < limits.Count ? EtsSiiValue.ParseFloat(limits[i], 90f) : 90f;
                    float u = i < urban.Count ? EtsSiiValue.ParseFloat(urban[i], 50f) : 50f;

                    // 0 is 'no limit': Germany's car unit, expressway 0 (urban 50) and motorway 0/0,
                    // the only zeros of any country in 1.61 (probe 1544). Taken as 0 m/s it had cars
                    // crawling at the 1 m/s floor on 2,306 lanes (245 km) within 7 km of the start.
                    // 130 km/h, the German advisory speed, stands in: spawn speed and the braking term
                    // for a slower lane ahead use the limit directly, so it must be finite; each
                    // car's own max_speed still caps it. An urban 0 falls back to the open-road figure.
                    if (l <= 0f) l = 130f;
                    if (u <= 0f) u = l;
                    table[classes[i]] = new[] { l, u };
                }
            }

            return table;
        }

        // ------------------------------------------------------------------ cars

        private void ReadVehicles()
        {
            EtsSiiFile storage = ReadSii("/def/vehicle/traffic_storage_car.sii");
            uint hash = 2166136261;

            if (storage == null)
            {
                return;
            }

            foreach (EtsSiiUnit unit in storage.Units)
            {
                if (unit.Type != "traffic_vehicle")
                {
                    continue;
                }

                var vehicle = new Vehicle
                {
                    Index = Vehicles.Count,
                    Unit = unit.Name,
                    MaxSpeedKmh = unit.GetFloat("max_speed", 130f),
                    Mass = unit.GetFloat("vehicle_mass", 1500f)
                };

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

                    if (accessory.EndsWith(".chassis", StringComparison.Ordinal))
                    {
                        vehicle.Model = data.GetString("model");
                        vehicle.Lods = data.GetArray("lods").ToArray();
                        vehicle.Variant = data.GetString("variant", "default");
                        vehicle.Look = data.GetString("look", "default");
                    }
                    else if (accessory.EndsWith(".fwheel", StringComparison.Ordinal))
                    {
                        vehicle.FrontWheel = data.GetString("model");
                    }
                    else if (accessory.EndsWith(".rwheel", StringComparison.Ordinal))
                    {
                        vehicle.RearWheel = data.GetString("model");
                    }
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

            TableHash = (int)(hash & 0xFF);
        }

        /// <summary>Reads a car's bounds from its smallest lod (a few kB), so a spawn knows its length without building it.</summary>
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

            for (int k = 0; k < 3; k++)
            {
                vehicle.Bounds[k] = model.BoundsMin[k];
                vehicle.Bounds[k + 3] = model.BoundsMax[k];
            }
        }

        /// <summary>The country's spawn ratio per car index (country/&lt;c&gt;/traffic.sii), zero for cars it does not name.</summary>
        public Dictionary<int, float> Ratios(string country)
        {
            if (_ratios.TryGetValue(country ?? "", out Dictionary<int, float> cached))
            {
                return cached;
            }

            var byUnit = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Vehicle v in Vehicles) byUnit[v.Unit] = v.Index;
            var ratios = new Dictionary<int, float>();
            EtsSiiFile file = string.IsNullOrEmpty(country) ? null : ReadSii("/def/country/" + country + "/traffic.sii");

            if (file != null)
            {
                foreach (EtsSiiUnit unit in file.Units)
                {
                    string target = unit.GetString("object");
                    float ratio = unit.GetFloat("spawn_ratio", 0f);

                    if (target != null && ratio > 0f && byUnit.TryGetValue(target, out int index))
                    {
                        ratios[index] = ratio;
                    }
                }
            }

            _ratios[country ?? ""] = ratios;
            return ratios;
        }
    }
}
