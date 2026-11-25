using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The game's damage rules, read from the player's install (2026-10-04, "Add the truck damage"). Unity-free, so the
    /// offline probe (DevTools/work/damage/probe) runs every line of it against the archives.
    ///
    /// Evidence (1.61 demo, read with the probe; values in the comments are the file's):
    ///   /def/damage_data.sii, unit vehicle_damage_data .damage_data:
    ///     truck_damage_coef 0.0007 "truck damage coef per hit, impact speed squared modified by weights",
    ///     trailer_damage_coef 0.0007, dragged_trailer_damage_coef 0.00002, truck_to_trailer_dmg 0.2 "how much damage is
    ///     transferred from truck to trailer (and vice versa)", side_damage_factor 6, roof_damage_factor 11;
    ///     the per-hit share of each part: cabin 0.8, chassis 1.0, engine 0.5, transmission 0.3, wheel 0.15 (fixable) and
    ///     0.04 / 0.05 / 0.02 / 0.01 / 0.01 (unfixable); trailer body 0.8, chassis 1.0, wheel 0.15 (fixable) and
    ///     0.04 / 0.05 / 0.01 (unfixable); cargo_damage_ratio 1.0;
    ///     wear per km: engine, transmission, wheel 2e-6 (fixable) and 2e-7 / 2e-7 / 2e-5 (unfixable); cabin and chassis 0;
    ///     trailer wheel 2e-6 / 2e-5, trailer body and chassis 0.
    ///   /def/economy_data.sii: cargo_damage_cost 5.0 "-5 Euro per 1% of damage", cargo_damage_cost_factor 0.04 "-4% total
    ///     cost penalty per 1% of damage", exp_damaged_cargo 5 "-5 XP per 1% of cargo damage", exp_damaged_cargo_factor 0.04,
    ///     reward_damage_level[] 0.5 2 5 10 20 40 100 with reward_title[] / reward_name[] (excellent ... damaged, reckless).
    ///   /def/tutorial_hint_conditions.sii cond.damage.core_max10 (hcd::vehicle_damage, part core, value 10, at_most): the
    ///     engine-start hints only show with the core damage at most 10 % - above that the game's engine may be off for
    ///     another reason, its malfunction (locale engine_malfunction "Vehicle malfunction! Check vehicle condition or
    ///     consider visiting a service."). So 10 % is where this world's malfunctions start (<see cref="MalfunctionLimit"/>).
    ///   Part prices: each accessory's price (e.g. daf.xf_euro6 chassis 4x2 26160, cabin space 19440, engine mx11_270
    ///     12000, transmission zf_12tx2420td 10500, /def/vehicle/f_tire/1.sii 1430, r_tire/1.sii 3640).
    ///
    /// NOT in any file (named in eurotrucks2.exe, set by no def on the demo, so its built-in value is unknown):
    ///   damage_price_ratio / accessory_repair_factor - the repair price per unit of damage. This world uses 1.0: a part's
    ///   repair costs its price times its damage (<see cref="RepairPriceRatio"/>).
    ///   warning_core_damage_treshold / fine_core_damage_treshold - the HUD's engine and transmission lamps and the police
    ///   fine; the lamps use the hint's 10 % here, there is no police.
    ///
    /// Reading "impact speed squared modified by weights": damage = coef x v^2 x W x direction factor, v the impact
    /// speed along the contact normal (m/s), W = m_other / (m_self + m_other) for a body that moves (a traffic car), 1 for
    /// anything that does not (a wall, a parked or remote truck), and the direction factor 1 front/back, side_damage_factor
    /// on the sides, roof_damage_factor on the roof. A wall at 60 km/h is 0.0007 x 16.7^2 = 19.4 % to the hit.
    /// </summary>
    public sealed class EtsDamageRules
    {
        public const int Cabin = 0, Chassis = 1, Engine = 2, Transmission = 3, Wheels = 4;
        public const int TrailerBody = 0, TrailerChassis = 1, TrailerWheels = 2;

        /// <summary>The game's order on its service screen (/ui/truck_service.sii rows: engine, transmission, chassis, cabin, wheels).</summary>
        public static readonly int[] ServiceOrder = { Engine, Transmission, Chassis, Cabin, Wheels };

        public static readonly string[] TruckPartNames = { "cabin", "chassis", "engine", "transmission", "wheels" };
        public static readonly string[] TrailerPartNames = { "body", "chassis", "wheels" };

        public float TruckCoef = 0.0007f;
        public float TrailerCoef = 0.0007f;
        public float DraggedTrailerCoef = 0.00002f;
        public float TruckToTrailer = 0.2f;
        public float SideFactor = 6f;
        public float RoofFactor = 11f;

        public float[] TruckRatio = { 0.8f, 1.0f, 0.5f, 0.3f, 0.15f };
        public float[] TruckUnfixableRatio = { 0.04f, 0.05f, 0.02f, 0.01f, 0.01f };
        public float[] TrailerRatio = { 0.8f, 1.0f, 0.15f };
        public float[] TrailerUnfixableRatio = { 0.04f, 0.05f, 0.01f };
        public float CargoRatio = 1f;

        public float[] TruckWear = { 0f, 0f, 2e-6f, 2e-6f, 2e-6f };
        public float[] TruckWearUnfixable = { 0f, 0f, 2e-7f, 2e-7f, 2e-5f };
        public float[] TrailerWear = { 0f, 0f, 2e-6f };
        public float[] TrailerWearUnfixable = { 0f, 0f, 2e-5f };

        public float CargoDamageCost = 5f;
        public float CargoDamageCostFactor = 0.04f;
        public float ExpDamagedCargo = 5f;
        public float ExpDamagedCargoFactor = 0.04f;
        public float[] RewardDamageLevel = { 0.5f, 2f, 5f, 10f, 20f, 40f, 100f };
        public string[] RewardTitle = { "@@excelent_t@@", "@@good_work_t@@", "@@jobresult_sat_t@@", "@@jobresult_reas_t@@", "@@jobresult_tol_t@@", "@@jobresult_dam_t@@", "@@jobresult_reck_t@@" };
        public string[] RewardName = { "@@jobresult_excellent@@", "@@jobresult_good_work@@", "@@jobresult_sat@@", "@@jobresult_reas@@", "@@jobresult_tol@@", "@@jobresult_dam@@", "@@jobresult_reck@@" };

        /// <summary>cond.damage.core_max10: above this a part's malfunctions and the HUD lamp start (fraction).</summary>
        public float MalfunctionLimit = 0.10f;

        /// <summary>damage_price_ratio is set by no file on the demo: a part's repair = its price x its damage x this.</summary>
        public float RepairPriceRatio = 1f;

        /// <summary>damage_data.sii was read (else the 1.61 values above, measured by the probe).</summary>
        public bool FromGame;

        /// <summary>economy_data.sii's damage keys were read.</summary>
        public bool EconomyFromGame;

        public static EtsDamageRules Load(EtsFileSystem files)
        {
            var r = new EtsDamageRules();

            if (files == null)
            {
                return r;
            }

            EtsSiiFile damage = files.ReadSii("/def/damage_data.sii");

            if (damage != null)
            {
                foreach (EtsSiiUnit u in damage.OfType("vehicle_damage_data"))
                {
                    r.TruckCoef = u.GetFloat("truck_damage_coef", r.TruckCoef);
                    r.TrailerCoef = u.GetFloat("trailer_damage_coef", r.TrailerCoef);
                    r.DraggedTrailerCoef = u.GetFloat("dragged_trailer_damage_coef", r.DraggedTrailerCoef);
                    r.TruckToTrailer = u.GetFloat("truck_to_trailer_dmg", r.TruckToTrailer);
                    r.SideFactor = u.GetFloat("side_damage_factor", r.SideFactor);
                    r.RoofFactor = u.GetFloat("roof_damage_factor", r.RoofFactor);
                    Read(u, r.TruckRatio, "cabin_damage_ratio", "chassis_damage_ratio", "engine_damage_ratio", "transmission_damage_ratio", "wheel_damage_ratio");
                    Read(u, r.TruckUnfixableRatio, "cabin_unfixable_damage_ratio", "chassis_unfixable_damage_ratio", "engine_unfixable_damage_ratio", "transmission_unfixable_damage_ratio", "wheel_unfixable_damage_ratio");
                    Read(u, r.TrailerRatio, "trailer_body_damage_ratio", "trailer_chassis_damage_ratio", "trailer_wheel_damage_ratio");
                    Read(u, r.TrailerUnfixableRatio, "trailer_body_unfixable_damage_ratio", "trailer_chassis_unfixable_damage_ratio", "trailer_wheel_unfixable_damage_ratio");
                    r.CargoRatio = u.GetFloat("cargo_damage_ratio", r.CargoRatio);
                    Read(u, r.TruckWear, "cabin_wear", "chassis_wear", "engine_wear", "transmission_wear", "wheel_wear");
                    Read(u, r.TruckWearUnfixable, "cabin_wear_unfixable", "chassis_wear_unfixable", "engine_wear_unfixable", "transmission_wear_unfixable", "wheel_wear_unfixable");
                    Read(u, r.TrailerWear, "trailer_body_wear", "trailer_chassis_wear", "trailer_wheel_wear");
                    Read(u, r.TrailerWearUnfixable, "trailer_body_wear_unfixable", "trailer_chassis_wear_unfixable", "trailer_wheel_wear_unfixable");
                    r.FromGame = true;
                    break;
                }
            }

            EtsSiiFile economy = files.ReadSii("/def/economy_data.sii");

            if (economy != null)
            {
                foreach (EtsSiiUnit u in economy.OfType("economy_data"))
                {
                    r.CargoDamageCost = u.GetFloat("cargo_damage_cost", r.CargoDamageCost);
                    r.CargoDamageCostFactor = u.GetFloat("cargo_damage_cost_factor", r.CargoDamageCostFactor);
                    r.ExpDamagedCargo = u.GetFloat("exp_damaged_cargo", r.ExpDamagedCargo);
                    r.ExpDamagedCargoFactor = u.GetFloat("exp_damaged_cargo_factor", r.ExpDamagedCargoFactor);
                    List<string> levels = u.GetArray("reward_damage_level");
                    List<string> titles = u.GetArray("reward_title");
                    List<string> names = u.GetArray("reward_name");

                    if (levels.Count > 0 && levels.Count == titles.Count && levels.Count == names.Count)
                    {
                        var parsed = new float[levels.Count];
                        bool ok = true;

                        for (int i = 0; i < levels.Count && ok; i++)
                        {
                            ok = float.TryParse(levels[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]);
                        }

                        if (ok)
                        {
                            r.RewardDamageLevel = parsed;
                            r.RewardTitle = titles.ToArray();
                            r.RewardName = names.ToArray();
                        }
                    }

                    r.EconomyFromGame = true;
                    break;
                }
            }

            EtsSiiFile hints = files.ReadSii("/def/tutorial_hint_conditions.sii");

            if (hints != null)
            {
                foreach (EtsSiiUnit u in hints.Units)
                {
                    if (u.Name == "cond.damage.core_max10" && u.Has("value"))
                    {
                        float v = u.GetFloat("value", 10f);
                        if (v > 0f && v < 100f) r.MalfunctionLimit = v / 100f;
                    }
                }
            }

            return r;
        }

        private static void Read(EtsSiiUnit u, float[] into, params string[] keys)
        {
            for (int i = 0; i < keys.Length && i < into.Length; i++)
            {
                into[i] = Math.Max(0f, u.GetFloat(keys[i], into[i]));
            }
        }

        /// <summary>side_damage_factor / roof_damage_factor by contact side: 0 front or back, 1 side, 2 roof.</summary>
        public float DirectionFactor(int side)
        {
            return side == 2 ? RoofFactor : side == 1 ? SideFactor : 1f;
        }

        /// <summary>
        /// A hit's damage to the body that took it (fraction, before the part shares): coef x v^2 x weight x side factor.
        /// v the impact speed along the contact normal in m/s, weight m_other / (m_self + m_other) or 1 (see the class).
        /// </summary>
        public double HitDamage(bool trailer, double impactSpeed, double weight, int side)
        {
            if (!(impactSpeed > 0.0) || !(weight > 0.0))
            {
                return 0.0;
            }

            double d = (trailer ? TrailerCoef : TruckCoef) * impactSpeed * impactSpeed * Math.Min(1.0, weight) * DirectionFactor(side);
            return Math.Min(1.0, d);
        }

        /// <summary>Weight of the other body: 1 for an immovable one (mass 0 or less), else m_other / (m_self + m_other).</summary>
        public static double Weight(double selfMass, double otherMass)
        {
            if (!(otherMass > 0.0))
            {
                return 1.0;
            }

            return otherMass / (Math.Max(1.0, selfMass) + otherMass);
        }

        // ------------------------------------------------------------------ delivery (economy_data.sii)

        /// <summary>
        /// The delivery's pay with its cargo damage (fraction 0..1): reward x (1 - cargo_damage_cost_factor x %) -
        /// cargo_damage_cost x %, never below nothing. 3 % on 10,000 EUR: 10,000 x 0.88 - 15 = 8,785.
        /// </summary>
        public int CargoPay(int reward, double cargoDamage)
        {
            double pct = Math.Max(0.0, Math.Min(100.0, cargoDamage * 100.0));

            if (pct <= 0.0 || reward <= 0)
            {
                return Math.Max(0, reward);
            }

            double pay = reward * Math.Max(0.0, 1.0 - CargoDamageCostFactor * pct) - CargoDamageCost * pct;
            return (int)Math.Max(0.0, Math.Round(pay, MidpointRounding.AwayFromZero));
        }

        /// <summary>The delivery's XP with its cargo damage: xp x (1 - exp_damaged_cargo_factor x %) - exp_damaged_cargo x %, never below 0.</summary>
        public int CargoXp(int xp, double cargoDamage)
        {
            double pct = Math.Max(0.0, Math.Min(100.0, cargoDamage * 100.0));

            if (pct <= 0.0 || xp <= 0)
            {
                return Math.Max(0, xp);
            }

            double v = xp * Math.Max(0.0, 1.0 - ExpDamagedCargoFactor * pct) - ExpDamagedCargo * pct;
            return (int)Math.Max(0.0, Math.Round(v, MidpointRounding.AwayFromZero));
        }

        /// <summary>The delivery's grade by cargo damage: the first reward_damage_level (in %) the damage is at most; 0 = excellent.</summary>
        public int Rating(double cargoDamage)
        {
            double pct = cargoDamage * 100.0;

            for (int i = 0; i < RewardDamageLevel.Length; i++)
            {
                if (pct <= RewardDamageLevel[i] + 1e-9)
                {
                    return i;
                }
            }

            return RewardDamageLevel.Length - 1;
        }

        // ------------------------------------------------------------------ part prices (repair)

        /// <summary>
        /// The truck's five parts' prices (cabin, chassis, engine, transmission, wheels = front + rear tyre accessory), from
        /// the accessory files the truck is built of: the spec's chassis, engine and transmission paths and data.sii's
        /// fallback[] for the cabin and tyres (a tyre file is looked for in the truck's folder, then /def/vehicle/). A part
        /// whose file cannot be read costs 0 to repair, and `missing` names it.
        /// </summary>
        public static int[] TruckPartPrices(EtsFileSystem files, string truckFolder, string chassisPath, string enginePath, string transmissionPath, out string missing)
        {
            var prices = new int[5];
            var notFound = new List<string>();
            var fallbacks = new Dictionary<string, string>(StringComparer.Ordinal);

            EtsSiiFile data = files != null && !string.IsNullOrEmpty(truckFolder) ? files.ReadSii(truckFolder + "/data.sii") : null;

            if (data != null)
            {
                foreach (EtsSiiUnit u in data.Units)
                {
                    foreach (string entry in u.GetArray("fallback"))
                    {
                        int bar = entry.IndexOf('|');
                        if (bar > 0) fallbacks[entry.Substring(0, bar)] = entry.Substring(bar + 1);
                    }
                }
            }

            string cabin = fallbacks.TryGetValue("cabin", out string c) ? truckFolder + "/cabin/" + c : null;
            prices[Cabin] = Price(files, cabin, "cabin", notFound);
            prices[Chassis] = Price(files, chassisPath ?? (fallbacks.TryGetValue("chassis", out string ch) ? truckFolder + "/chassis/" + ch : null), "chassis", notFound);
            prices[Engine] = Price(files, enginePath ?? (fallbacks.TryGetValue("engine", out string e) ? truckFolder + "/engine/" + e : null), "engine", notFound);
            prices[Transmission] = Price(files, transmissionPath ?? (fallbacks.TryGetValue("transmission", out string t) ? truckFolder + "/transmission/" + t : null), "transmission", notFound);
            prices[Wheels] = Price(files, Tyre(files, truckFolder, fallbacks, "f_tire"), "front tyre", notFound) +
                             Price(files, Tyre(files, truckFolder, fallbacks, "r_tire"), "rear tyre", notFound);
            missing = string.Join(", ", notFound.ToArray());
            return prices;
        }

        private static string Tyre(EtsFileSystem files, string truckFolder, Dictionary<string, string> fallbacks, string slot)
        {
            if (!fallbacks.TryGetValue(slot, out string file))
            {
                return null;
            }

            string own = truckFolder + "/" + slot + "/" + file;
            return files != null && files.Exists(own) ? own : "/def/vehicle/" + slot + "/" + file;
        }

        private static int Price(EtsFileSystem files, string path, string what, List<string> notFound)
        {
            EtsSiiFile f = files != null && !string.IsNullOrEmpty(path) ? files.ReadSii(path) : null;

            if (f != null)
            {
                foreach (EtsSiiUnit u in f.Units)
                {
                    if (u.Has("price"))
                    {
                        return Math.Max(0, u.GetInt("price", 0));
                    }
                }
            }

            notFound.Add(what);
            return 0;
        }
    }

    /// <summary>
    /// The local player's truck, trailer and cargo condition (fractions 0..1, per part: damage that a repair fixes, and
    /// wear that only a new part would). Plain data; EtsDamage drives it from the truck's collisions and kilometres.
    /// </summary>
    public sealed class EtsDamageState
    {
        public readonly double[] Truck = new double[5];
        public readonly double[] TruckUnfixable = new double[5];
        public readonly double[] Trailer = new double[3];
        public readonly double[] TrailerUnfixable = new double[3];
        public double Cargo;

        /// <summary>The truck these numbers are for (EtsVehicleSpec.TruckId); another truck starts new.</summary>
        public string TruckId = "";

        /// <summary>
        /// A hit to the truck (base damage from EtsDamageRules.HitDamage): its parts take their shares, and with a trailer
        /// coupled truck_to_trailer_dmg of it goes to the trailer and, through it, the cargo.
        /// </summary>
        public void HitTruck(EtsDamageRules r, double damage, bool trailerCoupled, bool carriesCargo)
        {
            if (!(damage > 0.0))
            {
                return;
            }

            AddParts(Truck, TruckUnfixable, r.TruckRatio, r.TruckUnfixableRatio, damage);

            if (trailerCoupled)
            {
                double toTrailer = damage * r.TruckToTrailer;
                AddParts(Trailer, TrailerUnfixable, r.TrailerRatio, r.TrailerUnfixableRatio, toTrailer);
                if (carriesCargo) Cargo = Math.Min(1.0, Cargo + toTrailer * r.CargoRatio);
            }
        }

        /// <summary>A hit to the trailer: its parts, the cargo (cargo_damage_ratio) and, coupled, the truck (truck_to_trailer_dmg).</summary>
        public void HitTrailer(EtsDamageRules r, double damage, bool coupled, bool carriesCargo)
        {
            if (!(damage > 0.0))
            {
                return;
            }

            AddParts(Trailer, TrailerUnfixable, r.TrailerRatio, r.TrailerUnfixableRatio, damage);
            if (carriesCargo) Cargo = Math.Min(1.0, Cargo + damage * r.CargoRatio);

            if (coupled)
            {
                AddParts(Truck, TruckUnfixable, r.TruckRatio, r.TruckUnfixableRatio, damage * r.TruckToTrailer);
            }
        }

        /// <summary>Wear for kilometres driven (the game's km), the truck's and, coupled, the trailer's.</summary>
        public void Wear(EtsDamageRules r, double km, bool trailerCoupled)
        {
            if (!(km > 0.0))
            {
                return;
            }

            for (int i = 0; i < Truck.Length; i++)
            {
                Truck[i] += r.TruckWear[i] * km;
                TruckUnfixable[i] += r.TruckWearUnfixable[i] * km;
                Fit(Truck, TruckUnfixable, i);
            }

            if (trailerCoupled)
            {
                for (int i = 0; i < Trailer.Length; i++)
                {
                    Trailer[i] += r.TrailerWear[i] * km;
                    TrailerUnfixable[i] += r.TrailerWearUnfixable[i] * km;
                    Fit(Trailer, TrailerUnfixable, i);
                }
            }
        }

        private static void AddParts(double[] fix, double[] unfix, float[] ratio, float[] unfixRatio, double damage)
        {
            for (int i = 0; i < fix.Length; i++)
            {
                fix[i] += damage * ratio[i];
                unfix[i] += damage * unfixRatio[i];
                Fit(fix, unfix, i);
            }
        }

        // A part is at most wholly worn: wear first (it stays), damage fills what is left.
        private static void Fit(double[] fix, double[] unfix, int i)
        {
            if (!(unfix[i] >= 0.0)) unfix[i] = 0.0;
            if (unfix[i] > 1.0) unfix[i] = 1.0;
            if (!(fix[i] >= 0.0)) fix[i] = 0.0;
            if (fix[i] > 1.0 - unfix[i]) fix[i] = 1.0 - unfix[i];
        }

        /// <summary>A truck part's whole condition loss, damage and wear (0..1).</summary>
        public double TruckPart(int part)
        {
            return Math.Min(1.0, Truck[part] + TruckUnfixable[part]);
        }

        /// <summary>The truck's figure on the HUD: its parts' average (the diagnostics' "Average damage" with the wear).</summary>
        public double TruckShown
        {
            get
            {
                double s = 0.0;
                for (int i = 0; i < Truck.Length; i++) s += TruckPart(i);
                return s / Truck.Length;
            }
        }

        public double TrailerShown
        {
            get
            {
                double s = 0.0;
                for (int i = 0; i < Trailer.Length; i++) s += Math.Min(1.0, Trailer[i] + TrailerUnfixable[i]);
                return s / Trailer.Length;
            }
        }

        /// <summary>What a repair of every truck part costs (whole euros): part price x its damage x RepairPriceRatio.</summary>
        public long TruckRepairCost(EtsDamageRules r, int[] prices)
        {
            double sum = 0.0;

            for (int i = 0; i < Truck.Length && prices != null && i < prices.Length; i++)
            {
                sum += PartRepairCost(r, prices, i);
            }

            return (long)Math.Round(sum, MidpointRounding.AwayFromZero);
        }

        public double PartRepairCost(EtsDamageRules r, int[] prices, int part)
        {
            return prices == null || part >= prices.Length ? 0.0 : prices[part] * Truck[part] * r.RepairPriceRatio;
        }

        /// <summary>The service's "Repair all": every truck part's damage gone, its wear kept.</summary>
        public void RepairTruck()
        {
            for (int i = 0; i < Truck.Length; i++) Truck[i] = 0.0;
        }

        /// <summary>A new trailer and load (a job's): their damage starts at nothing.</summary>
        public void NewTrailer()
        {
            for (int i = 0; i < Trailer.Length; i++) { Trailer[i] = 0.0; TrailerUnfixable[i] = 0.0; }
            Cargo = 0.0;
        }

        // ------------------------------------------------------------------ saved with the career (EtsProfileRecord.Damage)

        /// <summary>
        /// "1|truckId|d0,d1,d2,d3,d4|w0,w1,w2,w3,w4": the truck's damage and wear in the parts' order (cabin, chassis, engine,
        /// transmission, wheels). Empty when the truck is like new, so an undamaged career's record stays byte for byte as it was.
        /// </summary>
        public string Encode()
        {
            bool any = false;

            for (int i = 0; i < Truck.Length; i++)
            {
                if (Truck[i] > 0.0 || TruckUnfixable[i] > 0.0) any = true;
            }

            if (!any)
            {
                return "";
            }

            var sb = new StringBuilder(96);
            sb.Append("1|");

            foreach (char ch in TruckId ?? "")
            {
                if (ch != '|' && ch != ',' && ch != '\n' && ch != '\r' && ch != '=') sb.Append(ch);
            }

            sb.Append('|');
            Join(sb, Truck);
            sb.Append('|');
            Join(sb, TruckUnfixable);
            return sb.ToString();
        }

        private static void Join(StringBuilder sb, double[] v)
        {
            for (int i = 0; i < v.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(v[i].ToString("0.#####", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Reads Encode's text into the truck numbers. False (nothing changed) for anything else.</summary>
        public bool TryDecode(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string[] f = text.Split('|');

            if (f.Length != 4 || f[0] != "1")
            {
                return false;
            }

            var fix = new double[5];
            var unfix = new double[5];

            if (!Parse(f[2], fix) || !Parse(f[3], unfix))
            {
                return false;
            }

            TruckId = f[1];

            for (int i = 0; i < 5; i++)
            {
                Truck[i] = fix[i];
                TruckUnfixable[i] = unfix[i];
                Fit(Truck, TruckUnfixable, i);
            }

            return true;
        }

        private static bool Parse(string list, double[] into)
        {
            string[] parts = list.Split(',');

            if (parts.Length != into.Length)
            {
                return false;
            }

            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out into[i]) || double.IsNaN(into[i]) || double.IsInfinity(into[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
