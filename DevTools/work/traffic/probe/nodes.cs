using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var ids=new Dictionary<int,string>();
    fs.TryList("/def/country", out var files, out _);
    foreach(var f in files){ if(!f.EndsWith(".sui")) continue; var t=fs.ReadText("/def/country/"+f); var p=EtsSii.Parse("SiiNunit{"+t+"}", f, fs.ReadText); foreach(var u in p.Units) ids[u.GetInt("country_id",-1)]=u.Name; }
    Console.WriteLine(string.Join(", ", ids.OrderBy(k=>k.Key).Select(k=>k.Key+"="+k.Value)));
    var map = new EtsMap(fs);
    foreach(var site in new[]{ ("dover",-33391.4f,-7420f), ("prague",14501.1f,3624.6f), ("calais",-31300f,-4600f), ("szczecin",14994f,-15235f) }){
      var hist=new Dictionary<string,int>();
      foreach(var s in map.SectorsAround(site.Item2, site.Item3, 3000f)) foreach(var n in s.Nodes){ string k=((n.Flags>>8)&0xFF)+"/"+((n.Flags>>16)&0xFF)+"/"+((n.Flags>>24)&0xFF)+"/"+(n.Flags&0xFF); hist[k]=hist.TryGetValue(k,out var c)?c+1:1; }
      Console.WriteLine(site.Item1+": "+string.Join("  ", hist.OrderByDescending(k=>k.Value).Take(12).Select(k=>k.Key+" x"+k.Value)));
    }
  }
}
