using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs);
    foreach(var site in new[]{ ("dover",-33391.4f,-7420f), ("prague",14501.1f,3624.6f), ("calais",-31300f,-4600f), ("szczecin",14994f,-15235f) }){
      int roads=0, set=0, diff=0, prefN=0, prefSet=0, other=0; var hist=new Dictionary<string,int>(); var nodeOwner=new HashSet<ulong>();
      var secs=map.SectorsAround(site.Item2, site.Item3, 2500f);
      foreach(var s in secs){ foreach(var r in s.Roads){ nodeOwner.Add(r.StartNode); nodeOwner.Add(r.EndNode); var n0=map.FindNode(r.StartNode); var n1=map.FindNode(r.EndNode); if(n0==null||n1==null) continue; roads++;
          int f0=(int)((n0.Flags>>8)&0xFF), b0=(int)((n0.Flags>>16)&0xFF), f1=(int)((n1.Flags>>8)&0xFF), b1=(int)((n1.Flags>>16)&0xFF);
          if(f0!=0||b0!=0||f1!=0||b1!=0) set++; if(f0!=b0||f1!=b1) diff++; string k=f0+","+b0+"->"+f1+","+b1; hist[k]=hist.TryGetValue(k,out var c)?c+1:1; }
        foreach(var p in s.Prefabs){ foreach(var u in p.NodeUids){ nodeOwner.Add(u); var n=map.FindNode(u); if(n==null) continue; prefN++; if(((n.Flags>>8)&0xFFFF)!=0) prefSet++; } } }
      foreach(var s in secs) foreach(var n in s.Nodes) if(!nodeOwner.Contains(n.Uid) && ((n.Flags>>8)&0xFFFF)!=0) other++;
      Console.WriteLine(site.Item1+": roads "+roads+" with a country on a node "+set+" (fwd!=bwd "+diff+"); prefab nodes "+prefN+" set "+prefSet+"; non-road nodes with country "+other);
      Console.WriteLine("   "+string.Join("  ", hist.OrderByDescending(k=>k.Value).Take(14).Select(k=>k.Key+" x"+k.Value)));
    }
  }
}
