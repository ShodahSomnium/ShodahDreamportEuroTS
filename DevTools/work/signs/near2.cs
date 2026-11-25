using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs);
    float gx=float.Parse(a[0]), gz=float.Parse(a[1]), R=float.Parse(a[2]);
    foreach (var s in map.SectorsAround(gx,gz,R)) foreach (var sg in s.Signs) {
      if (sg.Template.Length==0) continue;
      var n = map.FindNode(sg.NodeUid); if (n==null) continue;
      float dx=n.Position[0]-gx, dz=n.Position[2]-gz; float d=(float)Math.Sqrt(dx*dx+dz*dz); if (d>R) continue;
      Console.WriteLine(d.ToString("0")+" m ("+n.Position[0].ToString("0.0")+","+n.Position[2].ToString("0.0")+") "+EtsToken.Decode(sg.ModelToken)+" "+sg.Template+" areas "+string.Join(",", sg.Areas.Select(x=>EtsToken.Decode(x.Area)+":"+x.Flags+":"+x.Value+":"+EtsToken.Decode(x.Board))));
      foreach (var ov in sg.Overrides) Console.WriteLine("   ov "+ov.Id+" @"+EtsToken.Decode(ov.Area)+" "+string.Join(" ", ov.Attributes.Select(at=>"["+at.Type+":"+at.Index+"="+(at.Type==5? at.Text : at.Type==4? at.Float.ToString("0.###") : at.Type==6? EtsToken.Decode(at.Token) : at.Number.ToString())+"]")));
    }
  }
}
