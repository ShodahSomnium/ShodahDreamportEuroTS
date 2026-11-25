using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs);
    int signs=0, templ=0;
    foreach (var name in map.AvailableSectors) {
      // name like sec-0008-0002
      string t10 = name.Substring(name.Length-10); int sx = int.Parse(t10.Substring(0,5)), sz = int.Parse(t10.Substring(5,5));
      var bb = fs.Read(EtsMapSector.FileName(sx, sz, "base")); var ab = fs.Read(EtsMapSector.FileName(sx, sz, "aux")); var dbb = fs.Read(EtsMapSector.FileName(sx, sz, "data")); if (bb==null && ab==null) continue; var s = EtsMapSector.Parse(sx, sz, bb, ab, dbb);
      foreach (var sg in s.Signs) { signs++; if (sg.Template.Length>0) templ++; }
    }
    Console.WriteLine("sectors "+map.AvailableSectors.Count+" signs "+signs+" templated "+templ);
    Console.WriteLine("first!=0 "+EtsMapSector.StatFirstNonZero+" badcount "+EtsMapSector.StatBadCount+" badattr "+EtsMapSector.StatBadAttr+" badkind "+EtsMapSector.StatBadKind+" endOK "+EtsMapSector.StatEndOk+" endBAD "+EtsMapSector.StatEndBad);
    System.IO.File.WriteAllLines("overrides_all.txt", EtsMapSector.OvLog);
  }
}
