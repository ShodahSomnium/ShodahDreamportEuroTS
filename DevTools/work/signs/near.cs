using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs);
    float gx=float.Parse(a[0]), gz=float.Parse(a[1]), R=float.Parse(a[2]); bool hex = a.Length>3;
    var tally = new Dictionary<string,int>();
    var rows = new List<(float,string)>();
    foreach (var s in map.SectorsAround(gx,gz,R)) foreach (var sg in s.Signs) {
      var n = map.FindNode(sg.NodeUid); if (n==null) continue;
      float dx=n.Position[0]-gx, dz=n.Position[2]-gz; float d=(float)Math.Sqrt(dx*dx+dz*dz); if (d>R) continue;
      var u = map.SignModel(sg.ModelToken);
      string name = u==null? "?" : u.GetString("sign_name",""); string md = u==null?"":u.GetString("model_desc","");
      int boards = sg.Raw[32];
      var sl=new System.Text.StringBuilder(); for(int bi=0;bi<boards;bi++) for(int fi=0;fi<3;fi++){ ulong t=BitConverter.ToUInt64(sg.Raw,33+bi*24+fi*8); if(t!=0) sl.Append(" "+(fi==0?"road_":fi==1?"city1_":"city2_")+bi+"="+EtsToken.Decode(t)); }
      string kind = sg.Template.Length>0 ? "TEMPLATE" : sl.Length>0 ? "BOARDS" : "plain";
      string key = kind+" "+EtsToken.Decode(sg.ModelToken)+" "+name+" ["+md+"]"+(sg.Template.Length>0?" tpl="+sg.Template:"");
      tally[key] = tally.TryGetValue(key, out var c)? c+1 : 1;
      int tail = 33+boards*24;
      if (hex && kind!="plain") rows.Add((d, d.ToString("0")+" m ("+n.Position[0].ToString("0.0")+","+n.Position[1].ToString("0.0")+","+n.Position[2].ToString("0.0")+") "+key+" look="+EtsToken.Decode(sg.LookToken)+" |"+sl+(sg.Raw.Length>tail+8 ? "\n    tail "+BitConverter.ToString(sg.Raw,tail).Replace("-",""):"")));
    }
    foreach (var r in rows.OrderBy(r=>r.Item1)) Console.WriteLine(r.Item2);
    foreach (var kv in tally.OrderBy(k=>k.Key)) Console.WriteLine(kv.Value.ToString().PadLeft(4)+" "+kv.Key);
  }
}
