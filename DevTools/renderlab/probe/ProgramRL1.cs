using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;

// Render lab probe RL1: pick country-road spots with vegetation close to both sides of the road.
// args: install cx cz radius [minDist]
static class ProgramRL1
{
    static void Main(string[] args)
    {
        CultureInfo ic = CultureInfo.InvariantCulture;
        EtsFileSystem fs = EtsFileSystem.Mount(args[0], out string report);
        double cx = double.Parse(args[1], ic), cz = double.Parse(args[2], ic), radius = double.Parse(args[3], ic);
        double minDist = args.Length > 4 ? double.Parse(args[4], ic) : 0;
        var map = new EtsMap(fs);
        map.SectorsAround((float)cx, (float)cz, (float)radius + 300f);
        var rows = new List<Tuple<double, string>>();

        foreach (EtsMapSector s in map.LoadedSectors)
        {
            foreach (EtsMapRoad r in s.Roads)
            {
                EtsMapNode a = map.FindNode(r.StartNode), b = map.FindNode(r.EndNode);
                if (a == null || b == null) continue;
                double mx = (a.Position[0] + b.Position[0]) / 2, my = (a.Position[1] + b.Position[1]) / 2, mz = (a.Position[2] + b.Position[2]) / 2;
                double d = Math.Sqrt((mx - cx) * (mx - cx) + (mz - cz) * (mz - cz));
                if (d > radius || d < minDist) continue;
                if (r.IsCityRoad) continue;
                double sr = Score(r.Right), sl = Score(r.Left);
                if (sr <= 0 || sl <= 0) continue;
                double score = Math.Min(sr, sl) * 2 + sr + sl;
                double dx = b.Position[0] - a.Position[0], dz = b.Position[2] - a.Position[2];
                // Unity yaw of the road direction a->b: Unity forward (x, -z game)
                double yaw = Math.Atan2(dx, -dz) * 180 / Math.PI;
                string line = score.ToString("0.0", ic) + "  road " + r.Uid.ToString("x") + " look " + r.Look + " len " + r.Length.ToString("0", ic) +
                              "  mid game (" + mx.ToString("0.0", ic) + ", " + my.ToString("0.0", ic) + ", " + mz.ToString("0.0", ic) + ") dist " + d.ToString("0", ic) +
                              " unityYaw(a->b) " + yaw.ToString("0", ic) +
                              "\n     R: " + Describe(map, r.Right) + "\n     L: " + Describe(map, r.Left);
                rows.Add(Tuple.Create(score, line));
            }
        }

        foreach (var row in rows.OrderByDescending(t => t.Item1).Take(25))
        {
            Console.WriteLine(row.Item2);
        }

        Console.WriteLine(rows.Count + " candidate roads");
    }

    static double Score(EtsTerrainSide side)
    {
        if (side == null || side.Vegetation == null) return 0;
        double s = 0;
        foreach (EtsVegetationRecord v in side.Vegetation)
        {
            if (v == null || v.Token == 0 || v.Density <= 0) continue;
            if (v.From > 12f) continue;
            s += v.Density * Math.Max(1.0, v.To - v.From) / (1.0 + v.From);
        }
        return s;
    }

    static string Describe(EtsMap map, EtsTerrainSide side)
    {
        var parts = new List<string>();
        parts.Add("size " + side.Size.ToString("0.0", CultureInfo.InvariantCulture) + " prof " + side.Profile);
        foreach (EtsVegetationRecord v in side.Vegetation)
        {
            if (v == null || v.Token == 0) continue;
            parts.Add(v.Name + " dens " + v.Density.ToString("0.0", CultureInfo.InvariantCulture) + " " + v.From.ToString("0.0", CultureInfo.InvariantCulture) + "-" + v.To.ToString("0.0", CultureInfo.InvariantCulture) + " sc" + v.Scale);
        }
        return string.Join(" | ", parts);
    }
}
