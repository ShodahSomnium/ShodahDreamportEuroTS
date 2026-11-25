using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Traffic lights: the semaphores a prefab descriptor carries, stood where the descriptor puts them with the
    /// model their profile names, and lamps that switch on the game's own programme from a shared clock.
    ///
    /// Everything here was read off the demo 1.61.1.0 (probes Det1-Det6):
    ///
    /// - WHERE: the descriptor's semaphore records (<see cref="EtsPrefabDescriptor.Semaphore"/>: position and a
    ///   rotation stored w first, in the prefab's own frame - the frame the prefab model is built in, so a light is a
    ///   child of the prefab at that local pose). Checked: round the Szczecin start all 12 semaphores of the four
    ///   junction descriptors have their lenses facing AGAINST the nav curve that names their id (the traffic they
    ///   stop), 4.4 m from its start; read the other way round only 8 of 12 do. Built through the real code offline
    ///   (probe Det7): all 248 lamps of the 27 heads round Szczecin and all 100 of the 25 round Cambridge (UK posts,
    ///   lenses on the other side of the head) face the traffic their id stops.
    /// - WHICH MODEL: the semaphore's profile (tr_sem_prof.&lt;token&gt; in /def/world/semaphore_profile*.sii; the
    ///   map prefab item's own profile when it names one) gives per semaphore id a model name ("single", "city", ""
    ///   for none), a type (traffic_light, _minor, _major, model_only, barrier_*), the phase lengths
    ///   (interval: green, yellow, red, yellow seconds - "green > yellow > red > yellow" in the file's own comment)
    ///   and the id's offset into the cycle (cycle[id]). A profile has country versions (tr_sem_prof.cr_2x1.uk)
    ///   and inherits from another ("inherited:"), overriding what it names; an array of one entry serves every id
    ///   (cr_1x1.uk: model[]: "city"). The model name picks tr_sem_mdl.&lt;name&gt;.&lt;country&gt; (single.poland ->
    ///   /model/traffic_light/single_pl.pmd, city.uk -> city_uk.pmd) or, failing that, tr_sem_mdl.&lt;name&gt;. The
    ///   country is the nearest city's (/def/city.sii "country"). The model's variants are major/minor: the priority
    ///   sign under the lamps.
    /// - THE LAMPS are not in the model's geometry: its lenses are an unlit "eut2.lamp" surface, and the light is a
    ///   flare hung on a locator whose hookup string (pmg string pool at +88/+92, locator +40 = offset into it) names
    ///   a unit of /unit/hookup/tr_light_flares.sii - flare.traffic.red, .yellow, .green, .yellow.blink.sleep. Each
    ///   says in which semaphore states it burns: "active_state" bits 0 orange-to-red, 1 red, 2 orange-to-green,
    ///   3 green, 5 sleep (red = 6: red and red-amber; yellow = 5: both ambers), or "semaphore_states[]" by name,
    ///   plus blink delays. Colours from /def/traffic_light_lamp_colors.sii (red 16,0,0; orange 35.8,17.8,0;
    ///   green 0,12,0.15; white 12,12,12 - normalised).
    /// - WHEN: descriptor semaphores of type 0 ("use profile") take the profile's interval and cycle[id]; any other
    ///   type carries its own intervals and cycle delay (TruckLib.Models Ppd/Semaphore.cs). Barrier types are not
    ///   built (a closed barrier drawn static would block the road).
    ///
    /// The clock is UTC seconds by default - the same on every client to within their clock sync, so every player sees
    /// the same colour without a byte of network traffic. <see cref="Clock"/> can be pointed at a network clock.
    /// <see cref="State"/> is the call for the traffic simulation: the colour a nav curve's semaphore shows now.
    /// </summary>
    public static class EtsSemaphores
    {
        public enum Light
        {
            Off = 0,
            Green = 1,
            YellowToRed = 2,
            Red = 3,
            RedYellow = 4,
            Sleep = 5
        }

        /// <summary>The state bits the flare definitions use (active_state).</summary>
        public const int BitYellowToRed = 1, BitRed = 2, BitRedYellow = 4, BitGreen = 8, BitGreenTrans = 16, BitSleep = 32;

        /// <summary>One light's programme.</summary>
        public sealed class Program
        {
            public float Green = 15f, Yellow = 2f, Red = 23f, RedYellow = 2f;

            /// <summary>Seconds this light's cycle is shifted by (the profile's cycle[id], or the descriptor's delay).</summary>
            public float Offset;

            /// <summary>Game minutes of the day the light sleeps (blinks amber) between, -1 when it never does.</summary>
            public int SleepStart = -1, SleepEnd = -1;

            /// <summary>False for a model-only semaphore: the post is there, nothing burns.</summary>
            public bool Lights = true;

            public string Profile = "";
            public string Type = "";
            public string Model = "";

            public float Period => Green + Yellow + Red + RedYellow;
        }

        /// <summary>The shared clock, seconds. UTC by default (see the class notes).</summary>
        public static Func<double> Clock = UtcSeconds;

        /// <summary>The game clock in minutes of the day, for the profiles' sleep window; null = the lights never sleep.</summary>
        public static Func<int> GameClockMinutes;

        // ---------------------------------------------------------------- counters, for the log

        public static int Lights, LightsSkipped, Lamps, Barriers, ModelOnly;
        public static readonly HashSet<string> ProfilesUsed = new HashSet<string>(StringComparer.Ordinal);

        public static string Report()
        {
            return Lights + " traffic lights (" + ProfilesUsed.Count + " profiles, " + Lamps + " lamps" +
                   (LightsSkipped > 0 ? ", " + LightsSkipped + " skipped" : "") + (Barriers > 0 ? ", " + Barriers + " barriers left out" : "") +
                   (ModelOnly > 0 ? ", " + ModelOnly + " model-only" : "") + ")";
        }

        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Logs a failure once per kind (the kind is <paramref name="what"/> plus the exception type).</summary>
        public static void Warn(string what, Exception e)
        {
            string key = what + "|" + (e != null ? e.GetType().Name : "");

            if (Warned.Count < 200 && Warned.Add(key))
            {
                Debug.LogWarning("[SomniumEuroTruck] Details: " + what + " failed" + (e != null ? ": " + e.GetType().Name + ": " + e.Message : "") + " (logged once; carrying on).");
            }
        }

        /// <summary>Logs a note once per text.</summary>
        public static void Note(string text)
        {
            if (Warned.Count < 200 && Warned.Add(text))
            {
                Debug.Log("[SomniumEuroTruck] Details: " + text + " (logged once).");
            }
        }

        // ---------------------------------------------------------------- the programme

        private static double UtcSeconds()
        {
            return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        }

        /// <summary>What a programme shows at time <paramref name="t"/> (seconds on <see cref="Clock"/>).</summary>
        public static Light StateOf(Program p, double t, int gameMinutes)
        {
            if (p == null || !p.Lights)
            {
                return Light.Off;
            }

            if (gameMinutes >= 0 && p.SleepStart >= 0 && p.SleepEnd >= 0 && p.SleepStart != p.SleepEnd && InWindow(gameMinutes, p.SleepStart, p.SleepEnd))
            {
                return Light.Sleep;
            }

            double period = p.Period;

            if (period <= 0.01)
            {
                return Light.Off;
            }

            double local = ((t - p.Offset) % period + period) % period;

            if (local < p.Green) return Light.Green;
            local -= p.Green;
            if (local < p.Yellow) return Light.YellowToRed;
            local -= p.Yellow;
            if (local < p.Red) return Light.Red;
            return Light.RedYellow;
        }

        private static bool InWindow(int minute, int start, int end)
        {
            return start <= end ? minute >= start && minute < end : minute >= start || minute < end;
        }

        /// <summary>The active_state bit of a state.</summary>
        public static int Bit(Light light)
        {
            switch (light)
            {
                case Light.Green: return BitGreen;
                case Light.YellowToRed: return BitYellowToRed;
                case Light.Red: return BitRed;
                case Light.RedYellow: return BitRedYellow;
                case Light.Sleep: return BitSleep;
                default: return 0;
            }
        }

        // ---------------------------------------------------------------- the registry the traffic reads

        private static readonly Dictionary<ulong, Dictionary<int, Program>> Programs = new Dictionary<ulong, Dictionary<int, Program>>();

        /// <summary>
        /// The colour semaphore <paramref name="semaphoreId"/> of prefab item <paramref name="prefabUid"/> shows now - the id
        /// a nav curve names (EtsPrefabNav.Curve.Semaphore). Off when that prefab's lights are not known (never built
        /// or prepared, see <see cref="Prepare"/>) or the id is not one of them.
        /// </summary>
        public static Light State(ulong prefabUid, int semaphoreId)
        {
            return TryGetProgram(prefabUid, semaphoreId, out Program p) ? StateOf(p, Clock(), Minutes()) : Light.Off;
        }

        /// <summary>The programme behind <see cref="State"/>, for a caller that wants to look ahead (time to the next change).</summary>
        public static bool TryGetProgram(ulong prefabUid, int semaphoreId, out Program program)
        {
            program = null;
            return Programs.TryGetValue(prefabUid, out Dictionary<int, Program> ids) && ids.TryGetValue(semaphoreId, out program);
        }

        /// <summary>Whether a prefab's lights are known (it has semaphores and they have been read).</summary>
        public static bool Known(ulong prefabUid)
        {
            return Programs.ContainsKey(prefabUid);
        }

        /// <summary>
        /// Reads a prefab's programmes without building anything, so <see cref="State"/> answers for a junction the
        /// map viewer has not built (yet). Cheap after the first call per profile; safe to call repeatedly.
        /// </summary>
        public static void Prepare(EtsMap map, EtsMapPrefab prefab)
        {
            if (map == null || prefab == null || Programs.ContainsKey(prefab.Uid))
            {
                return;
            }

            try
            {
                EtsSiiUnit def = map.PrefabDefinition(prefab.PrefabToken);
                EtsPrefabDescriptor ppd = def != null ? map.Descriptor(def.GetString("prefab_desc"), out _) : null;

                if (ppd == null || ppd.Semaphores.Length == 0)
                {
                    return;
                }

                string country = CountryAt(map, prefab);

                foreach (EtsPrefabDescriptor.Semaphore semaphore in ppd.Semaphores)
                {
                    Register(prefab.Uid, semaphore.Id, ProgramFor(prefab, semaphore, country));
                }
            }
            catch (Exception e)
            {
                Warn("reading the traffic lights of a prefab", e);
            }
        }

        private static void Register(ulong prefabUid, int id, Program program)
        {
            if (program == null)
            {
                return;
            }

            if (!Programs.TryGetValue(prefabUid, out Dictionary<int, Program> ids))
            {
                ids = new Dictionary<int, Program>();
                Programs[prefabUid] = ids;
            }

            // Several heads can share an id (the UK junctions put one each side of the road); they run as one.
            if (!ids.ContainsKey(id))
            {
                ids[id] = program;
            }
        }

        private static int Minutes()
        {
            try
            {
                return GameClockMinutes != null ? GameClockMinutes() : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // ---------------------------------------------------------------- profiles

        private sealed class Profile
        {
            public string Token = "";
            public List<string> Models = new List<string>();
            public List<string> Types = new List<string>();
            public List<float[]> Intervals = new List<float[]>();
            public List<float> Cycle = new List<float>();
            public int SleepStart = -1, SleepEnd = -1;
            public bool Found;
        }

        private static EtsSiiFile _profileFile, _modelFile;
        private static readonly Dictionary<string, Profile> ProfileCache = new Dictionary<string, Profile>(StringComparer.Ordinal);

        private static EtsSiiFile ProfileFile()
        {
            if (_profileFile == null && EtsEngine.Files != null)
            {
                _profileFile = EtsEngine.Files.ReadDefinitionFamily("/def/world", "semaphore_profile");
            }

            return _profileFile;
        }

        private static EtsSiiFile ModelFile()
        {
            if (_modelFile == null && EtsEngine.Files != null)
            {
                _modelFile = EtsEngine.Files.ReadDefinitionFamily("/def/world", "semaphore_model");
            }

            return _modelFile;
        }

        /// <summary>A profile by its token for a country, with its inherited chain folded in (parent first). Never null.</summary>
        private static Profile LoadProfile(string token, string country)
        {
            string key = token + "|" + (country ?? "");

            if (ProfileCache.TryGetValue(key, out Profile cached))
            {
                return cached;
            }

            var profile = new Profile { Token = token };
            ProfileCache[key] = profile;
            EtsSiiFile file = ProfileFile();

            if (file == null || string.IsNullOrEmpty(token))
            {
                return profile;
            }

            EtsSiiUnit unit = (!string.IsNullOrEmpty(country) ? file.Find("tr_sem_prof." + token + "." + country) : null) ?? file.Find("tr_sem_prof." + token);
            var chain = new List<EtsSiiUnit>();

            while (unit != null && chain.Count < 8 && !chain.Contains(unit))
            {
                chain.Insert(0, unit);
                string parent = unit.GetString("inherited", null);
                unit = string.IsNullOrEmpty(parent) ? null : file.Find(parent.Trim());
            }

            foreach (EtsSiiUnit u in chain)
            {
                profile.Found = true;

                if (u.Has("model")) Replace(profile.Models, u.GetArray("model"));
                if (u.Has("type")) Replace(profile.Types, u.GetArray("type"));

                if (u.Has("interval"))
                {
                    profile.Intervals.Clear();
                    foreach (string s in u.GetArray("interval")) profile.Intervals.Add(EtsSiiValue.ParseFloats(s));
                }

                if (u.Has("cycle"))
                {
                    profile.Cycle.Clear();
                    foreach (string s in u.GetArray("cycle")) profile.Cycle.Add(EtsSiiValue.ParseFloat(s, 0f));
                }

                if (u.Has("sleep_time_start")) profile.SleepStart = u.GetInt("sleep_time_start", -1);
                if (u.Has("sleep_time_end")) profile.SleepEnd = u.GetInt("sleep_time_end", -1);
            }

            if (profile.Found)
            {
                ProfilesUsed.Add(chain[chain.Count - 1].Name);
            }

            return profile;
        }

        private static void Replace(List<string> into, List<string> values)
        {
            into.Clear();

            foreach (string v in values)
            {
                into.Add((v ?? "").Trim());
            }
        }

        /// <summary>Entry <paramref name="id"/> of a per-id array: one entry serves every id; past the end, the fallback.</summary>
        private static T At<T>(List<T> list, int id, T fallback)
        {
            if (list == null || list.Count == 0) return fallback;
            if (list.Count == 1) return list[0];
            return id >= 0 && id < list.Count ? list[id] : fallback;
        }

        private static bool IsBarrier(int type, string typeName)
        {
            return type == 5 || type == 6 || type == 8 || type == 10 || (typeName != null && typeName.StartsWith("barrier", StringComparison.Ordinal));
        }

        /// <summary>
        /// A semaphore's programme and model name. Null when nothing is to be built or run for it (a barrier; a
        /// virtual light).
        /// </summary>
        private static Program ProgramFor(EtsMapPrefab prefab, EtsPrefabDescriptor.Semaphore semaphore, string country)
        {
            ulong token = prefab.SemaphoreProfile != 0 ? prefab.SemaphoreProfile : semaphore.Profile;
            Profile profile = LoadProfile(token != 0 ? EtsToken.Decode(token) : "", country);
            string typeName = At(profile.Types, semaphore.Id, "");

            if (IsBarrier(semaphore.Type, typeName) || semaphore.Type == 9)
            {
                Barriers++;
                return null;
            }

            var program = new Program { Profile = profile.Token, Type = typeName };
            float[] intervals;

            if (semaphore.Type == 0 && profile.Found)
            {
                intervals = At(profile.Intervals, semaphore.Id, null);
                program.Offset = At(profile.Cycle, semaphore.Id, 0f);
            }
            else
            {
                intervals = semaphore.Intervals;
                program.Offset = semaphore.CycleDelay;
            }

            if (intervals == null || intervals.Length < 4 || intervals[0] + intervals[1] + intervals[2] + intervals[3] <= 0.01f)
            {
                intervals = semaphore.Intervals != null && semaphore.Intervals.Length >= 4 && semaphore.Intervals[0] + semaphore.Intervals[1] + semaphore.Intervals[2] + semaphore.Intervals[3] > 0.01f
                    ? semaphore.Intervals
                    : new[] { 15f, 2f, 23f, 2f };
            }

            program.Green = Mathf.Max(0f, intervals[0]);
            program.Yellow = Mathf.Max(0f, intervals[1]);
            program.Red = Mathf.Max(0f, intervals[2]);
            program.RedYellow = Mathf.Max(0f, intervals[3]);
            program.SleepStart = profile.SleepStart;
            program.SleepEnd = profile.SleepEnd;

            // Which model: the profile's name for this id; a descriptor light with no profile gets the plain post.
            program.Model = profile.Found ? At(profile.Models, semaphore.Id, "") : (semaphore.Type == 0 || semaphore.Type >= 2 ? "single" : "");
            program.Lights = semaphore.Type != 1 && typeName != "model_only";

            if (!program.Lights)
            {
                ModelOnly++;
            }

            return program;
        }

        /// <summary>The model unit a profile's model name means in a country: tr_sem_mdl.&lt;name&gt;.&lt;country&gt;, else tr_sem_mdl.&lt;name&gt;, else by its "name" field.</summary>
        private static string ModelPath(string name, string country)
        {
            EtsSiiFile file = ModelFile();

            if (file == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            EtsSiiUnit unit = (!string.IsNullOrEmpty(country) ? file.Find("tr_sem_mdl." + name + "." + country) : null) ?? file.Find("tr_sem_mdl." + name);

            if (unit == null)
            {
                foreach (EtsSiiUnit u in file.Units)
                {
                    if (string.Equals(u.GetString("name", null), name, StringComparison.Ordinal))
                    {
                        unit = u;
                        break;
                    }
                }
            }

            return unit != null ? unit.GetString("model_desc", null) : null;
        }

        // ---------------------------------------------------------------- country

        private static readonly Dictionary<long, string> CountryCache = new Dictionary<long, string>();

        /// <summary>The prefab's country: that of the nearest city area among the sectors read (null when none within 40 km).</summary>
        public static string CountryAt(EtsMap map, EtsMapPrefab prefab)
        {
            EtsMapNode node = prefab.NodeUids.Length > 0 ? map.FindNode(prefab.NodeUids[0]) : null;
            return node == null ? null : CountryAt(map, node.Position[0], node.Position[2]);
        }

        public static string CountryAt(EtsMap map, float x, float z)
        {
            long key = ((long)Mathf.FloorToInt(x / 2000f) << 32) ^ (uint)Mathf.FloorToInt(z / 2000f);

            if (CountryCache.TryGetValue(key, out string cached))
            {
                return cached;
            }

            string best = null;
            float bestDistance = 40000f;

            foreach (EtsMapSector sector in map.LoadedSectors)
            {
                foreach (EtsMapCity city in sector.Cities)
                {
                    if (!sector.NodesByUid.TryGetValue(city.NodeUid, out EtsMapNode node))
                    {
                        node = map.FindNode(city.NodeUid);
                    }

                    if (node == null)
                    {
                        continue;
                    }

                    // The city area is the rectangle from its node over its width and height.
                    float dx = Mathf.Max(0f, Mathf.Max(node.Position[0] - x, x - (node.Position[0] + city.Width)));
                    float dz = Mathf.Max(0f, Mathf.Max(node.Position[2] - z, z - (node.Position[2] + city.Height)));
                    float d = Mathf.Sqrt(dx * dx + dz * dz);

                    if (d < bestDistance)
                    {
                        EtsSiiUnit unit = map.City(city.CityToken);
                        string country = unit != null ? unit.GetString("country", null) : null;

                        if (!string.IsNullOrEmpty(country))
                        {
                            bestDistance = d;
                            best = country.Trim();
                        }
                    }
                }
            }

            CountryCache[key] = best;
            return best;
        }

        // ---------------------------------------------------------------- lamps: hookups and flares

        private sealed class Flare
        {
            public int Mask;
            public float BlinkOn, BlinkOff, BlinkShift;
            public Color Colour;
            public Color GlowColour;
        }

        private static readonly Dictionary<string, string[]> HookupsByModel = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Flare> Flares = new Dictionary<string, Flare>(StringComparer.Ordinal);
        private static EtsSiiFile _flareFile;
        private static bool _flareFileRead;

        /// <summary>
        /// Each locator's hookup string, by locator index: the pmg keeps a string pool (offset at +88, size at +92) and a
        /// locator's u32 at +40 is an offset into it. Read here because the model reader keeps only the number.
        /// </summary>
        private static string[] Hookups(string pmdPath, EtsModelData model)
        {
            if (HookupsByModel.TryGetValue(pmdPath, out string[] cached))
            {
                return cached;
            }

            var names = new string[model.Locators.Length];
            HookupsByModel[pmdPath] = names;
            byte[] pmg = EtsEngine.Files != null ? EtsEngine.Files.Read(pmdPath.Substring(0, pmdPath.Length - 1) + "g") : null;

            if (pmg == null || pmg.Length < 112)
            {
                return names;
            }

            int poolOffset = BitConverter.ToInt32(pmg, 88);
            int poolSize = BitConverter.ToInt32(pmg, 92);

            if (poolOffset <= 0 || poolSize <= 0 || poolOffset + poolSize > pmg.Length)
            {
                return names;
            }

            for (int i = 0; i < names.Length; i++)
            {
                int at = model.Locators[i].Hookup;

                if (at < 0 || at >= poolSize)
                {
                    continue;
                }

                int start = poolOffset + at, end = start;

                while (end < poolOffset + poolSize && pmg[end] != 0)
                {
                    end++;
                }

                names[i] = end > start ? Encoding.ASCII.GetString(pmg, start, end - start) : null;
            }

            return names;
        }

        /// <summary>The flare a hookup names, from /unit/hookup/tr_light_flares.sii; null when it is not a traffic flare.</summary>
        private static Flare FlareFor(string hookup)
        {
            if (string.IsNullOrEmpty(hookup) || !hookup.StartsWith("flare.traffic", StringComparison.Ordinal))
            {
                return null;
            }

            if (Flares.TryGetValue(hookup, out Flare cached))
            {
                return cached;
            }

            if (!_flareFileRead)
            {
                _flareFileRead = true;

                try
                {
                    _flareFile = EtsEngine.Files != null ? EtsEngine.Files.ReadSii("/unit/hookup/tr_light_flares.sii") : null;
                }
                catch (Exception e)
                {
                    Warn("reading /unit/hookup/tr_light_flares.sii", e);
                }
            }

            EtsSiiUnit unit = _flareFile != null ? _flareFile.Find(hookup) : null;
            var flare = new Flare();
            string model = unit != null ? unit.GetString("model", "") : "";
            string colourName = model.Length > 0 ? model : hookup;

            if (unit != null && unit.Has("active_state"))
            {
                flare.Mask = unit.GetInt("active_state", 0);
            }
            else if (unit != null && unit.Has("semaphore_states"))
            {
                foreach (string state in unit.GetArray("semaphore_states"))
                {
                    switch (state.Trim())
                    {
                        case "orange_to_red": flare.Mask |= BitYellowToRed; break;
                        case "red": flare.Mask |= BitRed; break;
                        case "orange_to_green": flare.Mask |= BitRedYellow; break;
                        case "green": flare.Mask |= BitGreen; break;
                        case "green_trans": flare.Mask |= BitGreenTrans; break;
                        case "sleep": flare.Mask |= BitSleep; break;
                    }
                }
            }
            else
            {
                // No definition: go by the name, as the four the demo's posts use are named.
                flare.Mask = hookup.Contains(".sleep") ? BitSleep
                           : hookup.Contains("red") ? BitRed | BitRedYellow
                           : hookup.Contains("yellow") ? BitYellowToRed | BitRedYellow
                           : hookup.Contains("green") ? BitGreen : 0;
            }

            if (unit != null)
            {
                flare.BlinkOn = unit.GetFloat("blink_delay_on", 0f);
                flare.BlinkOff = unit.GetFloat("blink_delay_off", 0f);
                flare.BlinkShift = unit.GetFloat("blink_cycle_shift", 0f);
            }

            // The lens: the lamp colour slot the flare lights (lamp_color_slot, else the one its flare model is named
            // for), coloured from /def/traffic_light_lamp_colors.sii. The glow: the flare's own light colour, which
            // the file gives as lumen, hue, saturation ("setup: lumen_hue_saturation": red hue 0, yellow 48, green 120).
            string slot = unit != null ? unit.GetString("lamp_color_slot", null) : null;
            int slotIndex = slot == "red" ? 0 : slot == "orange" ? 1 : slot == "green" ? 2
                          : colourName.Contains("red") ? 0 : colourName.Contains("yellow") || colourName.Contains("orange") ? 1
                          : colourName.Contains("green") ? 2 : 3;
            flare.Colour = LampColour(slotIndex);
            flare.GlowColour = flare.Colour;

            if (unit != null && unit.GetString("setup", "") == "lumen_hue_saturation")
            {
                float[] lhs = unit.GetFloats("diffuse_color");

                if (lhs.Length >= 3)
                {
                    flare.GlowColour = Color.HSVToRGB(Mathf.Repeat(lhs[1], 360f) / 360f, Mathf.Clamp01(lhs[2] / 100f), 1f);
                }
            }

            Flares[hookup] = flare.Mask != 0 ? flare : null;
            return Flares[hookup];
        }

        private static Color[] _lampColours;

        /// <summary>
        /// Colour slot i of /def/traffic_light_lamp_colors.sii (0 red 16,0,0; 1 orange 35.8,17.8,0; 2 green 0,12,0.15;
        /// 3 white 12,12,12 - linear and far above 1), scaled so the brightest channel is 1.
        /// </summary>
        private static Color LampColour(int slot)
        {
            if (_lampColours == null)
            {
                _lampColours = new[] { new Color(1f, 0f, 0f), new Color(1f, 0.497f, 0f), new Color(0f, 1f, 0.0125f), Color.white };

                try
                {
                    EtsSiiFile file = EtsEngine.Files != null ? EtsEngine.Files.ReadSii("/def/traffic_light_lamp_colors.sii") : null;

                    if (file != null)
                    {
                        foreach (EtsSiiUnit unit in file.Units)
                        {
                            if (!unit.Has("color"))
                            {
                                continue;
                            }

                            List<string> colours = unit.GetArray("color");

                            for (int i = 0; i < colours.Count && i < _lampColours.Length; i++)
                            {
                                float[] c = EtsSiiValue.ParseFloats(colours[i]);
                                float max = c.Length >= 3 ? Mathf.Max(c[0], Mathf.Max(c[1], c[2])) : 0f;

                                if (max > 0.0001f)
                                {
                                    _lampColours[i] = new Color(c[0] / max, c[1] / max, c[2] / max);
                                }
                            }

                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Warn("reading /def/traffic_light_lamp_colors.sii", e);
                }
            }

            return _lampColours[Mathf.Clamp(slot, 0, _lampColours.Length - 1)];
        }

        // ---------------------------------------------------------------- building

        /// <summary>
        /// The prefab's traffic lights: programmes registered now (so <see cref="State"/> answers at once), models and
        /// lamps built a few a frame by <see cref="EtsDetailDriver"/> under a "detail semaphores" child of the prefab.
        /// </summary>
        public static void BuildPrefab(EtsMap map, EtsMapPrefab prefab, EtsPrefabDescriptor ppd, Transform prefabRoot, EtsMapBuilder.Options options)
        {
            if (ppd == null || ppd.Semaphores.Length == 0 || prefabRoot == null)
            {
                return;
            }

            string country = CountryAt(map, prefab);
            Transform container = null;

            foreach (EtsPrefabDescriptor.Semaphore semaphore in ppd.Semaphores)
            {
                Program program;

                try
                {
                    program = ProgramFor(prefab, semaphore, country);
                }
                catch (Exception e)
                {
                    Warn("reading a traffic light's profile", e);
                    LightsSkipped++;
                    continue;
                }

                if (program == null)
                {
                    continue;
                }

                Register(prefab.Uid, semaphore.Id, program);
                string path = ModelPath(program.Model, country);

                if (string.IsNullOrEmpty(path))
                {
                    if (!string.IsNullOrEmpty(program.Model))
                    {
                        Note("no semaphore model '" + program.Model + "' for " + (country ?? "no country"));
                        LightsSkipped++;
                    }

                    continue;
                }

                if (container == null)
                {
                    container = new GameObject("detail semaphores").transform;
                    container.SetParent(prefabRoot, false);
                }

                Transform parent = container;
                EtsPrefabDescriptor.Semaphore s = semaphore;
                bool colliders = options != null && options.Colliders;
                EtsDetailDriver.Enqueue(() => BuildHead(parent, s, program, path, colliders));
            }
        }

        private static void BuildHead(Transform parent, EtsPrefabDescriptor.Semaphore semaphore, Program program, string path, bool colliders)
        {
            if (parent == null)
            {
                return;   // the prefab was unloaded before its turn came
            }

            try
            {
                EtsModelData model = EtsEngine.LoadModel(path, out string failure);

                if (model == null)
                {
                    Note("traffic light model " + path + ": " + failure);
                    LightsSkipped++;
                    return;
                }

                string variant = program.Type.Contains("minor") || semaphore.Type == 3 ? "minor" : "major";

                EtsModelBuilder.Result built = EtsModelBuilder.Build(model, new EtsModelBuilder.Options
                {
                    Look = "default",
                    Variants = new[] { variant },
                    CreateLocators = false,
                    AddColliders = colliders,
                    IncludeCollision = false
                }, parent, EtsEngine.Materials);

                GameObject root = built.Root;
                root.name = "traffic light " + semaphore.Id + " (" + program.Profile + ")";
                root.transform.localPosition = EtsModelBuilder.ToUnity(semaphore.Position);
                root.transform.localRotation = EtsModelBuilder.ToUnityRotation(semaphore.Rotation);

                // The post is an obstacle; lamps get no collider.
                if (colliders)
                {
                    EtsLayers.SetRecursively(root.transform, EtsLayers.Environment);
                }

                var head = new Head { Root = root, Program = program };

                if (program.Lights)
                {
                    AddLamps(head, model, path, variant);
                }

                SmallDetailCulling(root, 0.004f);
                Heads.Add(head);
                Lights++;
            }
            catch (Exception e)
            {
                LightsSkipped++;
                Warn("building a traffic light", e);
            }
        }

        private static void AddLamps(Head head, EtsModelData model, string path, string variant)
        {
            string[] hookups = Hookups(path, model);
            int variantIndex = Mathf.Max(0, model.VariantIndex(variant));
            var lamps = new List<Lamp>();

            foreach (EtsModelPart part in model.Parts)
            {
                if (model.Variants.Length > 0 && !part.IsVisible(variantIndex))
                {
                    continue;
                }

                for (int l = part.LocatorStart; l < part.LocatorStart + part.LocatorCount && l < model.Locators.Length; l++)
                {
                    Flare flare = l < hookups.Length ? FlareFor(hookups[l]) : null;

                    if (flare == null)
                    {
                        continue;
                    }

                    EtsModelLocator locator = model.Locators[l];
                    lamps.Add(MakeLamp(head.Root.transform, locator, flare));
                }
            }

            head.Lamps = lamps.ToArray();
            Lamps += lamps.Count;
        }

        // ---------------------------------------------------------------- the lamps and the clock

        private sealed class Lamp
        {
            public Renderer[] Renderers;
            public Flare Flare;
            public bool On;
        }

        private sealed class Head
        {
            public GameObject Root;
            public Program Program;
            public Lamp[] Lamps = new Lamp[0];
        }

        private static readonly List<Head> Heads = new List<Head>();

        /// <summary>Lamp radius (the demo's lenses sit 0.33-0.39 m apart) and its glow.</summary>
        private const float LensRadius = 0.1f, GlowRadius = 0.36f;

        private static Mesh _disc, _glow;

        /// <summary>
        /// Whether a mesh is one of the lamp meshes every traffic light shares - the map viewer frees an unloaded item's
        /// own meshes and must leave these alone, as it leaves EtsModelBuilder's (EtsModelBuilder.IsShared).
        /// </summary>
        public static bool IsSharedMesh(Mesh mesh)
        {
            return mesh != null && (mesh == _disc || mesh == _glow);
        }
        private static readonly Dictionary<int, Material> LensMaterials = new Dictionary<int, Material>();
        private static readonly Dictionary<int, Material> GlowMaterials = new Dictionary<int, Material>();
        private static bool _glowShaderMissing;

        private static Lamp MakeLamp(Transform root, EtsModelLocator locator, Flare flare)
        {
            var lamp = new Lamp { Flare = flare };
            var renderers = new List<Renderer>();

            // The locator's forward (game -z turned by its rotation, Unity +z) is where the lens looks.
            Quaternion rotation = EtsModelBuilder.ToUnityRotation(locator.Rotation);
            Vector3 position = EtsModelBuilder.ToUnity(locator.Position) + rotation * Vector3.forward * 0.02f;

            Material lensMaterial = LensMaterial(flare.Colour);

            if (lensMaterial != null)
            {
                var lens = new GameObject("lamp");
                lens.transform.SetParent(root, false);
                lens.transform.localPosition = position;
                lens.transform.localRotation = rotation;
                lens.transform.localScale = Vector3.one * LensRadius;
                lens.AddComponent<MeshFilter>().sharedMesh = Disc();
                var lensRenderer = lens.AddComponent<MeshRenderer>();
                lensRenderer.sharedMaterial = lensMaterial;
                lensRenderer.shadowCastingMode = ShadowCastingMode.Off;
                lensRenderer.receiveShadows = false;
                renderers.Add(lensRenderer);
            }

            Material glowMaterial = GlowMaterial(flare.GlowColour);

            if (glowMaterial != null)
            {
                var glow = new GameObject("glow");
                glow.transform.SetParent(root, false);
                glow.transform.localPosition = position + rotation * Vector3.forward * 0.01f;
                glow.transform.localRotation = rotation;
                glow.transform.localScale = Vector3.one * GlowRadius;
                glow.AddComponent<MeshFilter>().sharedMesh = Glow();
                var glowRenderer = glow.AddComponent<MeshRenderer>();
                glowRenderer.sharedMaterial = glowMaterial;
                glowRenderer.shadowCastingMode = ShadowCastingMode.Off;
                glowRenderer.receiveShadows = false;
                renderers.Add(glowRenderer);
            }

            lamp.Renderers = renderers.ToArray();

            foreach (Renderer r in lamp.Renderers)
            {
                r.enabled = false;
            }

            return lamp;
        }

        /// <summary>A unit disc in the xy plane (12 segments), both faces drawn by its material.</summary>
        private static Mesh Disc()
        {
            if (_disc != null)
            {
                return _disc;
            }

            _disc = Fan(12, 1f, 1f, "traffic lamp");
            return _disc;
        }

        /// <summary>The glow: the same fan with the vertex alpha falling from the centre to nothing at the rim.</summary>
        private static Mesh Glow()
        {
            if (_glow != null)
            {
                return _glow;
            }

            _glow = Fan(16, 0.55f, 0f, "traffic lamp glow");
            return _glow;
        }

        private static Mesh Fan(int segments, float centreAlpha, float rimAlpha, string name)
        {
            var vertices = new Vector3[segments + 1];
            var colours = new Color[segments + 1];
            var normals = new Vector3[segments + 1];
            var indices = new int[segments * 3];
            vertices[0] = Vector3.zero;
            colours[0] = new Color(1f, 1f, 1f, centreAlpha);
            normals[0] = Vector3.forward;

            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                vertices[i + 1] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                colours[i + 1] = new Color(1f, 1f, 1f, rimAlpha);
                normals[i + 1] = Vector3.forward;
                indices[i * 3] = 0;
                indices[i * 3 + 1] = 1 + (i + 1) % segments;
                indices[i * 3 + 2] = 1 + i;
            }

            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            mesh.colors = colours;
            mesh.normals = normals;
            mesh.triangles = indices;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static int ColourKey(Color c)
        {
            return (Mathf.RoundToInt(c.r * 255f) << 16) | (Mathf.RoundToInt(c.g * 255f) << 8) | Mathf.RoundToInt(c.b * 255f);
        }

        /// <summary>The lit lens: URP Unlit (always in a URP build), both faces.</summary>
        private static Material LensMaterial(Color colour)
        {
            int key = ColourKey(colour);

            if (LensMaterials.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");

            if (shader == null)
            {
                Note("no URP shader for the traffic lamps");
                return null;
            }

            var material = new Material(shader) { name = "traffic lamp " + key.ToString("x6") };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
            if (material.HasProperty("_Color")) material.SetColor("_Color", colour);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            if (material.HasProperty("_EmissionColor")) { material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", colour); }
            LensMaterials[key] = material;
            return material;
        }

        /// <summary>
        /// The glow round a burning lamp: Sprites/Default (vertex colour times tint, alpha blended, no culling). Left out
        /// when the client does not have the shader - the lens alone still shows the colour.
        /// </summary>
        private static Material GlowMaterial(Color colour)
        {
            if (_glowShaderMissing)
            {
                return null;
            }

            int key = ColourKey(colour);

            if (GlowMaterials.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            Shader shader = Shader.Find("Sprites/Default");

            if (shader == null)
            {
                _glowShaderMissing = true;
                Note("Sprites/Default not found; traffic lamps are drawn without their glow");
                return null;
            }

            var material = new Material(shader) { name = "traffic lamp glow " + key.ToString("x6") };
            material.SetColor("_Color", new Color(colour.r, colour.g, colour.b, 1f));
            GlowMaterials[key] = material;
            return material;
        }

        /// <summary>
        /// Every frame, from <see cref="EtsDetailDriver"/>: each head's state from the clock, and each lamp switched only
        /// when it changes. A few hundred lamps cost microseconds.
        /// </summary>
        public static void Tick()
        {
            if (Heads.Count == 0)
            {
                return;
            }

            double t = Clock();
            int minutes = Minutes();

            for (int i = Heads.Count - 1; i >= 0; i--)
            {
                Head head = Heads[i];

                if (head == null || head.Root == null || head.Lamps == null)
                {
                    Heads.RemoveAt(i);
                    continue;
                }

                try
                {
                    int bit = Bit(StateOf(head.Program, t, minutes));

                    foreach (Lamp lamp in head.Lamps)
                    {
                        bool on = (lamp.Flare.Mask & bit) != 0 && BlinkOn(lamp.Flare, t);

                        if (on == lamp.On)
                        {
                            continue;
                        }

                        lamp.On = on;

                        foreach (Renderer r in lamp.Renderers)
                        {
                            if (r != null) r.enabled = on;
                        }
                    }
                }
                catch (Exception e)
                {
                    // One bad head is dropped, not retried every frame.
                    Heads.RemoveAt(i);
                    Warn("switching a traffic light", e);
                }
            }
        }

        private static bool BlinkOn(Flare flare, double t)
        {
            float period = flare.BlinkOn + flare.BlinkOff;

            if (flare.BlinkOn <= 0f || flare.BlinkOff <= 0f || period <= 0.01f)
            {
                return true;
            }

            double local = ((t + flare.BlinkShift * period) % period + period) % period;
            return local < flare.BlinkOn;
        }

        // ---------------------------------------------------------------- shared with the sign builder

        /// <summary>
        /// Culls a small detail when it falls under <paramref name="screenFraction"/> of the screen's height (one LOD
        /// level, no mesh swap): a 7 m post at 0.004 goes at about 700 m in a 100 degree view, a 2.5 m sign at 0.005 at
        /// about 200 m (Unity scales both by the quality lod bias). Unity does this in its culling pass; no script runs.
        /// </summary>
        public static void SmallDetailCulling(GameObject root, float screenFraction)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

            if (renderers.Length == 0)
            {
                return;
            }

            LODGroup group = root.GetComponent<LODGroup>();

            if (group == null)
            {
                group = root.AddComponent<LODGroup>();
            }

            group.SetLODs(new[] { new LOD(screenFraction, renderers) });
            group.RecalculateBounds();
        }
    }
}
