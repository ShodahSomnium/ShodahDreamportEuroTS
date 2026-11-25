using System;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;

static class Sim
{
    static string P(double f) { return (f * 100.0).ToString("0.00") + "%"; }
    static int Main()
    {
        var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
        var r = EtsDamageRules.Load(fs);
        Console.WriteLine("damage_data read: " + r.FromGame + ", economy read: " + r.EconomyFromGame + ", malfunction limit " + P(r.MalfunctionLimit));
        Console.WriteLine("coef truck " + r.TruckCoef + " trailer " + r.TrailerCoef + " t->t " + r.TruckToTrailer + " side " + r.SideFactor + " roof " + r.RoofFactor + " cargo ratio " + r.CargoRatio);
        Console.WriteLine("truck ratio " + string.Join(",", r.TruckRatio) + " unfix " + string.Join(",", r.TruckUnfixableRatio));
        Console.WriteLine("trailer ratio " + string.Join(",", r.TrailerRatio) + " unfix " + string.Join(",", r.TrailerUnfixableRatio));
        Console.WriteLine("wear " + string.Join(",", r.TruckWear) + " unfix " + string.Join(",", r.TruckWearUnfixable) + " trailer " + string.Join(",", r.TrailerWear) + " / " + string.Join(",", r.TrailerWearUnfixable));
        Console.WriteLine("cargo cost " + r.CargoDamageCost + " factor " + r.CargoDamageCostFactor + " xp " + r.ExpDamagedCargo + " / " + r.ExpDamagedCargoFactor + " levels " + string.Join(",", r.RewardDamageLevel) + " titles " + string.Join(",", r.RewardTitle));
        int[] prices = EtsDamageRules.TruckPartPrices(fs, "/def/vehicle/truck/daf.xf_euro6", "/def/vehicle/truck/daf.xf_euro6/chassis/4x2.sii", "/def/vehicle/truck/daf.xf_euro6/engine/mx11_270.sii", "/def/vehicle/truck/daf.xf_euro6/transmission/zf_12tx2420td.sii", out string missing);
        Console.WriteLine("DAF XF part prices cabin/chassis/engine/transmission/wheels: " + string.Join(", ", prices) + (missing.Length > 0 ? " (missing " + missing + ")" : ""));
        Console.WriteLine();
        double truckMass = 7500, carMass = 1300;
        Console.WriteLine("scenario                                   | hit base | truck parts cab/chs/eng/trn/whl          | HUD truck | trailer | cargo | repair EUR");
        string[] names = { "front into a wall 10 km/h", "front into a wall 30 km/h", "front into a wall 60 km/h", "front into a wall 90 km/h",
                           "rear-ends a 1.3 t car at 30 km/h (closing)", "rear-ends a 1.3 t car at 60 km/h (closing)", "side scrape, 30 km/h at 10 deg", "side into a pole 20 km/h", "rollover, roof lands at 5 m/s", "trailer side into a wall 30 km/h" };
        double[] kmh = { 10, 30, 60, 90, 30, 60, 30 * Math.Sin(10 * Math.PI / 180), 20, 5 * 3.6, 30 };
        double[] other = { 0, 0, 0, 0, carMass, carMass, 0, 0, 0, 0 };
        int[] side = { 0, 0, 0, 0, 0, 0, 1, 1, 2, 1 };
        for (int k = 0; k < names.Length; k++)
        {
            var s = new EtsDamageState();
            bool trailerHit = k == 9;
            double w = EtsDamageRules.Weight(truckMass, other[k]);
            double d = r.HitDamage(trailerHit, kmh[k] / 3.6, w, side[k]);
            if (trailerHit) s.HitTrailer(r, d, true, true); else s.HitTruck(r, d, true, true);
            Console.WriteLine(names[k].PadRight(42) + " | " + P(d).PadLeft(7) + "  | " +
                (P(s.TruckPart(0)) + " " + P(s.TruckPart(1)) + " " + P(s.TruckPart(2)) + " " + P(s.TruckPart(3)) + " " + P(s.TruckPart(4))).PadRight(40) + " | " +
                P(s.TruckShown).PadLeft(8) + "  | " + P(s.TrailerShown).PadLeft(6) + " | " + P(s.Cargo).PadLeft(5) + " | " + s.TruckRepairCost(r, prices));
        }
        Console.WriteLine();
        var wear = new EtsDamageState();
        wear.Wear(r, 1000, true);
        Console.WriteLine("1000 game km of driving: engine " + P(wear.TruckPart(2)) + " transmission " + P(wear.TruckPart(3)) + " wheels " + P(wear.TruckPart(4)) + " (of it wear " + P(wear.TruckUnfixable[4]) + "), HUD truck " + P(wear.TruckShown) + ", trailer wheels " + P(wear.Trailer[2] + wear.TrailerUnfixable[2]) + ", repair " + wear.TruckRepairCost(r, prices) + " EUR");
        Console.WriteLine();
        Console.WriteLine("delivery pay with cargo damage (reward 10000 EUR, 400 XP):");
        foreach (double c in new[] { 0.0, 0.004, 0.01, 0.03, 0.039, 0.07, 0.15, 0.3 })
        {
            int rate = r.Rating(c);
            Console.WriteLine("  cargo " + P(c).PadLeft(7) + " -> pay " + r.CargoPay(10000, c).ToString().PadLeft(6) + " EUR, XP " + r.CargoXp(400, c).ToString().PadLeft(4) + ", grade " + r.RewardTitle[rate] + " / " + r.RewardName[rate]);
        }
        // round trip of the saved form
        var st = new EtsDamageState { TruckId = "daf.xf_euro6" };
        st.HitTruck(r, r.HitDamage(false, 30 / 3.6, 1, 0), false, false);
        string enc = st.Encode();
        var back = new EtsDamageState();
        Console.WriteLine();
        Console.WriteLine("saved form: " + enc + " -> decodes " + back.TryDecode(enc) + ", truck " + P(back.TruckShown) + " (was " + P(st.TruckShown) + "), new truck encodes \"" + new EtsDamageState().Encode() + "\"");
        return 0;
    }
}
