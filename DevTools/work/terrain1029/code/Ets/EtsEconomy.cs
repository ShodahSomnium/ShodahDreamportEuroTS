using System;
using System.Collections.Generic;
using System.Globalization;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Quick-job pay, delivery XP and the game's own kilometres, from the demo's files (owner G; [gameui §5], PLAN.md
    /// C16, §7.G). Pure C#; probe 1952 tests it against the two jobs the player's saves record.
    ///
    /// PAY. The files give every constant except the quick job's fixed part. /def/economy_data.sii (probe 1950,
    /// S/phase1/G/probe/out1950.txt): driver_revenue_coef_per_km 0.67 ("used during quick job and drivers jobs"),
    /// reward_bonus_level 0.015, delivery_window 400/250/90 min with delivery_window_coef 1.0/1.15/1.4. The quick
    /// job's own quick_job_revenue_fixed and quick_job_revenue_coef_per_km are named in the executable but set by no
    /// def (probe 1644 over 70,290 files; absent from economy_data, probe 1950), so they are built in. The formula is
    /// the community's (trucksimulator.wiki.gg Economy; forum.scssoft.com t=300120, from observation, unverified):
    ///     pay = 350 + units * unit_reward_per_km * km * 0.67 * window_coef[urgency] + min(base, base * 0.015 * level)
    /// Checked against the one recorded quick job, (a) the player's 1.48 save (delivery_log_entry params: carbn_pwdr_c,
    /// 15 units, 16 km, 412 EUR; re-read with Program906 into S/phase1/G/probe): 441 predicted, 7 % high. The
    /// exact formula stays undetermined until the user reads real offers from the demo (Q-G3).
    ///
    /// KILOMETRES. The game's km are not map metres. /map/europe.mbd holds the map's scales, 19 and 3 (f32 at +60 and
    /// +64, map version 907; probe 1951): 19 outside city areas and 3 inside them. Job (b), the player's 1.61 first job
    /// (save player_job: transinet.szczecin -> rt_log.rostock, planned_distance_km 215), routed on this world's road
    /// graph is 13.351 map km, of which 1.995 km inside city areas: 11.319 * 19 + 2.032 * 3 = 221.1 game km, 2.8 %
    /// over the game's 215 (all at 19 would be 253.7, 18 % over; only unflagged areas 238.4). So km for pay and XP are
    /// <see cref="RouteGameKm"/>; the world's own displays (route advisor, delivery distance) stay in metres driven.
    ///
    /// XP. exp_cargo_delivery 1.0 "per km of real world": one XP per game km (C16). Job (a) paid 16 XP for its 16 km
    /// (delivery_log_entry params[6] = 16; info.sii 16 XP after it, [persist §2.8]). The parking bonuses
    /// (exp_park_bonus 15/40/90) are not added: that job earned none, and when the game adds them is undetermined.
    ///
    /// Static game data only (no player data), so it may outlive a world visit; <see cref="Load"/> re-reads per visit.
    /// </summary>
    public static class EtsEconomy
    {
        /// <summary>The quick job's fixed part in euros: the community value, in no file (Q-G3 calibrates it).</summary>
        public const int QuickFixed = 350;

        // The measured 1.61 values (probe 1950): used when a file cannot be read.
        private static readonly float[] FallbackWindowCoef = { 1.0f, 1.15f, 1.4f };
        private static readonly int[] FallbackWindowMinutes = { 400, 250, 90 };

        private static float[] _windowCoef = FallbackWindowCoef;
        private static int[] _windowMinutes = FallbackWindowMinutes;

        /// <summary>economy_data driver_revenue_coef_per_km: 0.67 on 1.61.</summary>
        public static float QuickCoefPerKm { get; private set; } = 0.67f;

        /// <summary>economy_data reward_bonus_level: 0.015 on 1.61.</summary>
        public static float RewardBonusLevel { get; private set; } = 0.015f;

        /// <summary>economy_data exp_cargo_delivery: 1.0 on 1.61.</summary>
        public static float ExpCargoDelivery { get; private set; } = 1.0f;

        /// <summary>economy_data exp_free_roam: 0.5 on 1.61 (Phase 2: EtsProfileStore.AddDriving).</summary>
        public static float ExpFreeRoam { get; private set; } = 0.5f;

        /// <summary>/map/europe.mbd: 19 on 1.61 (map version 907).</summary>
        public static float NormalScale { get; private set; } = 19f;

        /// <summary>/map/europe.mbd: 3 on 1.61.</summary>
        public static float CityScale { get; private set; } = 3f;

        /// <summary>economy_data.sii was read (not the fallback).</summary>
        public static bool FromGame { get; private set; }

        /// <summary>europe.mbd's scales were read (not the fallback).</summary>
        public static bool ScalesFromGame { get; private set; }

        /// <summary>
        /// Reads /def/economy_data.sii and /map/europe.mbd. False when the economy file could not be read (the
        /// measured values stay in use). Safe to call more than once.
        /// </summary>
        public static bool Load(EtsFileSystem files)
        {
            if (files == null)
            {
                return false;
            }

            ScalesFromGame = ReadScales(files.Read("/map/europe.mbd"));

            EtsSiiFile sii = files.ReadSii("/def/economy_data.sii");

            if (sii == null)
            {
                return false;
            }

            foreach (EtsSiiUnit unit in sii.OfType("economy_data"))
            {
                QuickCoefPerKm = Positive(unit, "driver_revenue_coef_per_km", 0.67f);
                RewardBonusLevel = Math.Max(0f, unit.GetFloat("reward_bonus_level", 0.015f));
                ExpCargoDelivery = Math.Max(0f, unit.GetFloat("exp_cargo_delivery", 1.0f));
                ExpFreeRoam = Math.Max(0f, unit.GetFloat("exp_free_roam", 0.5f));
                _windowCoef = Floats(unit.GetArray("delivery_window_coef"), FallbackWindowCoef);
                _windowMinutes = Ints(unit.GetArray("delivery_window"), FallbackWindowMinutes);
                FromGame = true;
                return true;
            }

            return false;
        }

        /// <summary>The revenue coefficient of an urgency (0 standard, 1 important, 2 urgent); 1 outside the table.</summary>
        public static float WindowCoef(int urgency)
        {
            return urgency >= 0 && urgency < _windowCoef.Length ? _windowCoef[urgency] : 1f;
        }

        /// <summary>The delivery window of an urgency in game minutes (400/250/90 on 1.61; the save's job (b), urgency 0: 1108 - 708 = 400).</summary>
        public static int WindowMinutes(int urgency)
        {
            return urgency >= 0 && urgency < _windowMinutes.Length ? _windowMinutes[urgency] : _windowMinutes[0];
        }

        /// <summary>
        /// A quick job's pay in euros (the community formula above): 350 + base + level bonus, base = units *
        /// unitRewardPerKm * routeKm (game km) * 0.67 * window coefficient.
        /// </summary>
        public static int QuickJobRevenue(int units, float unitRewardPerKm, float routeKm, int urgency, int level)
        {
            if (units <= 0 || unitRewardPerKm <= 0f || routeKm <= 0f || float.IsNaN(routeKm) || float.IsInfinity(routeKm))
            {
                return QuickFixed;
            }

            double pay = (double)units * unitRewardPerKm * routeKm * QuickCoefPerKm * WindowCoef(urgency);
            double bonus = Math.Min(pay, pay * RewardBonusLevel * Math.Max(0, level));
            double total = QuickFixed + Math.Round(pay + bonus, MidpointRounding.AwayFromZero);
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        /// <summary>1 XP per game km (exp_cargo_delivery, C16); no parking bonus (undetermined).</summary>
        public static int DeliveryXp(float routeKm)
        {
            if (routeKm <= 0f || float.IsNaN(routeKm))
            {
                return 0;
            }

            double xp = Math.Round((double)routeKm * ExpCargoDelivery, MidpointRounding.AwayFromZero);
            return xp >= int.MaxValue ? int.MaxValue : (int)xp;
        }

        /// <summary>
        /// The map scale at a game position: <see cref="CityScale"/> inside any city area's box, else <see cref="NormalScale"/>.
        /// Every city area counts, flagged or not: probe 1951, job (b) 221.1 km against the game's 215 with all of them,
        /// 238.4 with the unflagged only. The boxes are looked up in a 4 km grid of the list (built on the first call
        /// for a list, rebuilt when another list is passed): a route's game km asked this once per step, over all 270
        /// boxes, which was most of RouteGameKm's 2-3 ms per offer (Phase 1 check). A point is inside a box only in a
        /// cell the box touches, so the answer is the full walk's (probe 2113 compares them). Main thread only.
        /// </summary>
        public static float ScaleAt(IReadOnlyList<EtsMapCity> cities, float gameX, float gameZ)
        {
            if (cities == null || cities.Count == 0)
            {
                return NormalScale;
            }

            if (!ReferenceEquals(cities, _gridFor) || cities.Count != _gridCount)
            {
                BuildCityGrid(cities);
            }

            if (_cityCells.TryGetValue(CityCellKey(CityCellOf(gameX), CityCellOf(gameZ)), out List<int> inCell))
            {
                for (int n = 0; n < inCell.Count; n++)
                {
                    EtsMapCity c = cities[inCell[n]];

                    if (gameX >= c.KdopMin[0] && gameX <= c.KdopMax[0] && gameZ >= c.KdopMin[2] && gameZ <= c.KdopMax[2])
                    {
                        return CityScale;
                    }
                }
            }

            for (int n = 0; n < _bigBoxes.Count; n++)
            {
                EtsMapCity c = cities[_bigBoxes[n]];

                if (gameX >= c.KdopMin[0] && gameX <= c.KdopMax[0] && gameZ >= c.KdopMin[2] && gameZ <= c.KdopMax[2])
                {
                    return CityScale;
                }
            }

            return NormalScale;
        }

        /// <summary>ScaleAt as the walk over every box, without the grid: the reference probe 2113 checks the grid against.</summary>
        public static float ScaleAtByWalk(IReadOnlyList<EtsMapCity> cities, float gameX, float gameZ)
        {
            if (cities != null)
            {
                for (int i = 0; i < cities.Count; i++)
                {
                    EtsMapCity c = cities[i];

                    if (gameX >= c.KdopMin[0] && gameX <= c.KdopMax[0] && gameZ >= c.KdopMin[2] && gameZ <= c.KdopMax[2])
                    {
                        return CityScale;
                    }
                }
            }

            return NormalScale;
        }

        private const double CityCell = 4000.0;

        // The list the grid was built for (the index's own list; held until another is passed - map data, no player data).
        private static IReadOnlyList<EtsMapCity> _gridFor;
        private static int _gridCount;
        private static readonly Dictionary<long, List<int>> _cityCells = new Dictionary<long, List<int>>();
        private static readonly List<int> _bigBoxes = new List<int>();

        /// <summary>City boxes too large for the grid, checked on every call (0 on the demo; probe 2113).</summary>
        public static int CityBoxesOutsideGrid => _bigBoxes.Count;

        private static int CityCellOf(float v)
        {
            return (int)Math.Floor(v / CityCell);
        }

        private static long CityCellKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        private static void BuildCityGrid(IReadOnlyList<EtsMapCity> cities)
        {
            _cityCells.Clear();
            _bigBoxes.Clear();

            for (int i = 0; i < cities.Count; i++)
            {
                EtsMapCity c = cities[i];

                if (c == null || c.KdopMin == null || c.KdopMax == null || c.KdopMin.Length < 3 || c.KdopMax.Length < 3)
                {
                    continue;
                }

                // A box whose bounds are not numbers contains no point (every comparison with NaN is false).
                if (float.IsNaN(c.KdopMin[0]) || float.IsNaN(c.KdopMax[0]) || float.IsNaN(c.KdopMin[2]) || float.IsNaN(c.KdopMax[2]))
                {
                    continue;
                }

                int x0 = CityCellOf(c.KdopMin[0]), x1 = CityCellOf(c.KdopMax[0]);
                int z0 = CityCellOf(c.KdopMin[2]), z1 = CityCellOf(c.KdopMax[2]);

                // An inverted box contains no point. A huge one (none on the demo) is checked on every call instead of
                // filling the grid, so the answer stays the walk's.
                if (x1 < x0 || z1 < z0)
                {
                    continue;
                }

                if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 4096)
                {
                    _bigBoxes.Add(i);
                    continue;
                }

                for (int x = x0; x <= x1; x++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        long key = CityCellKey(x, z);

                        if (!_cityCells.TryGetValue(key, out List<int> list))
                        {
                            list = new List<int>(2);
                            _cityCells[key] = list;
                        }

                        list.Add(i);
                    }
                }
            }

            _gridFor = cities;
            _gridCount = cities.Count;
        }

        /// <summary>
        /// A route's length in the game's km: each step's graph length (a road's stored length, or a junction's
        /// straight distance, exactly as EtsRoadGraph weighs its edges) times the scale at the step's middle.
        /// The bay-to-road ends are the caller's (<see cref="ScaleAt"/> at each bay).
        /// </summary>
        public static float RouteGameKm(EtsMap map, IList<EtsRoadGraph.Step> path, IReadOnlyList<EtsMapCity> cities)
        {
            if (map == null || path == null)
            {
                return 0f;
            }

            double metres = 0;
            EtsMapNode previous = path.Count > 0 ? map.FindNode(path[0].NodeUid) : null;

            for (int i = 1; i < path.Count; i++)
            {
                EtsMapNode node = map.FindNode(path[i].NodeUid);

                if (node == null || previous == null)
                {
                    previous = node;
                    continue;
                }

                float dx = node.Position[0] - previous.Position[0], dz = node.Position[2] - previous.Position[2];
                EtsMapRoad road = path[i].Road;
                float length = road != null ? (road.Length > 0.5f ? road.Length : 1f) : Math.Max(1f, (float)Math.Sqrt(dx * dx + dz * dz));
                metres += length * ScaleAt(cities, (node.Position[0] + previous.Position[0]) * 0.5f, (node.Position[2] + previous.Position[2]) * 0.5f);
                previous = node;
            }

            return (float)(metres / 1000.0);
        }

        // /map/europe.mbd on 1.61 (version 907, 69 bytes): f32 19 at +60, f32 3 at +64 (probe 1951).
        private static bool ReadScales(byte[] mbd)
        {
            if (mbd == null || mbd.Length < 68 || BitConverter.ToUInt32(mbd, 0) < 900)
            {
                return false;
            }

            float normal = BitConverter.ToSingle(mbd, 60), city = BitConverter.ToSingle(mbd, 64);

            if (!(normal >= 1f && normal <= 100f) || !(city >= 1f && city <= normal))
            {
                return false;
            }

            NormalScale = normal;
            CityScale = city;
            return true;
        }

        private static float Positive(EtsSiiUnit unit, string key, float fallback)
        {
            float v = unit.GetFloat(key, fallback);
            return v > 0f ? v : fallback;
        }

        private static float[] Floats(List<string> values, float[] fallback)
        {
            var result = new float[values.Count];

            for (int i = 0; i < values.Count; i++)
            {
                if (!float.TryParse(values[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]) || result[i] <= 0f)
                {
                    return fallback;
                }
            }

            return result.Length > 0 ? result : fallback;
        }

        private static int[] Ints(List<string> values, int[] fallback)
        {
            var result = new int[values.Count];

            for (int i = 0; i < values.Count; i++)
            {
                if (!int.TryParse(values[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]) || result[i] <= 0)
                {
                    return fallback;
                }
            }

            return result.Length > 0 ? result : fallback;
        }
    }
}
