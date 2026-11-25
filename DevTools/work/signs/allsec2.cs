using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs);
    int signs=0, templ=0, withOv=0, ovs=0, areas=0, boardsOv=0, legacy=0;
    var tally = new Dictionary<string,int>(); var sample = new Dictionary<string,string>();
    foreach (var name in map.AvailableSectors) {
      string t10 = name.Substring(name.Length-10); int sx = int.Parse(t10.Substring(0,5)), sz = int.Parse(t10.Substring(5,5));
      var bb = fs.Read(EtsMapSector.FileName(sx, sz, "base")); var ab = fs.Read(EtsMapSector.FileName(sx, sz, "aux")); var dbb = fs.Read(EtsMapSector.FileName(sx, sz, "data")); if (bb==null && ab==null) continue;
      var s = EtsMapSector.Parse(sx, sz, bb, ab, dbb);
      foreach (var sg in s.Signs) { signs++; if (sg.Template.Length>0) templ++; if (sg.Overrides.Length>0) withOv++; ovs+=sg.Overrides.Length; areas+=sg.Areas.Length; foreach(var ar in sg.Areas) if ((ar.Flags&2)!=0) boardsOv++; if (sg.Boards.Any(x=>x!=0)) legacy++;
        foreach (var ov in sg.Overrides) foreach (var at in ov.Attributes) { string k = at.Type+":"+at.Index; tally[k] = tally.TryGetValue(k, out var c)? c+1:1; if (!sample.ContainsKey(k)) sample[k] = at.Type==5? at.Text : at.Type==4? at.Float.ToString() : at.Type==6? EtsToken.Decode(at.Token) : at.Number.ToString(); } }
    }
    Console.WriteLine("signs "+signs+" templated "+templ+" with overrides "+withOv+" overrides "+ovs+" area records "+areas+" board swaps "+boardsOv+" legacy-board signs "+legacy);
    Console.WriteLine("endOK "+EtsMapSector.StatEndOk+" endBAD "+EtsMapSector.StatEndBad);
    foreach (var l in EtsMapSector.BadLog) Console.WriteLine("BAD "+l);
    foreach (var kv in tally.OrderBy(k=>k.Key)) Console.WriteLine(kv.Key+" "+kv.Value+"  e.g. "+sample[kv.Key]);
  }
}
