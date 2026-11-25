using System;
using System.IO;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class T
{
    static EtsProfileRecord Sample()
    {
        var r = new EtsProfileRecord { UserId = "abc-123", NickName = "Tester", Generation = 7, CreatedUnix = 1700000000, SavedUnix = 1700000100, Money = 52000, Experience = 700, DistanceMetres = 123456, JobsDelivered = 3, LifetimeEarnings = 50000, HqCity = "szczecin", TruckUnit = "daf_xf_euro6", TruckPaint = "FF0000" };
        r.RecentJobs.Add("0123456789abcdef0123456789abcdef");
        r.Deliveries.Add(EtsProfileCodec.DeliveryLine(EtsProfileCodec.DeliverySummary("Cargo", "A, B", "C, D", 215f), 9000, 215, 1700000050));
        return r;
    }
    static int Main(string[] a)
    {
        string mode = a[0];
        if (mode == "write")
        {
            File.WriteAllText("plain_" + a[1] + ".txt", EtsProfileCodec.Encode(Sample()));
#if NEWCODEC
            var d = Sample(); d.Damage = "1|daf.xf_euro6|0.03889,0.04861,0.02431,0.01458,0.00729|0.00194,0.00243,0.00097,0.00049,0.00049";
            File.WriteAllText("damaged_new.txt", EtsProfileCodec.Encode(d));
#endif
        }
        else if (mode == "read")
        {
            foreach (string f in new[] { "plain_old.txt", "plain_new.txt", "damaged_new.txt" })
            {
                if (!File.Exists(f)) continue;
                bool ok = EtsProfileCodec.TryDecode(File.ReadAllText(f), "abc-123", out EtsProfileRecord r, out string why, out int fmt);
                string extra = "";
#if NEWCODEC
                if (ok) extra = ", damage \"" + r.Damage + "\"";
#endif
                Console.WriteLine(a[1] + " codec reads " + f + ": " + ok + " (" + why + ", format " + fmt + (ok ? ", money " + r.Money + ", xp " + r.Experience + ", deliveries " + r.Deliveries.Count : "") + extra + ")");
                if (ok && f == "damaged_new.txt")
                {
                    // re-save by this codec and read again with the new codec semantics
                    File.WriteAllText("resaved_by_" + a[1] + ".txt", EtsProfileCodec.Encode(r));
                }
            }
        }
        return 0;
    }
}
