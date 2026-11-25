using System; using System.Collections.Generic; using System.Linq; using System.IO;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P1 {
  static EtsFileSystem fs;
  static EtsModelData Load(string p){ try{ return EtsModelData.Parse(fs.Read(p), fs.Read(p.Substring(0,p.Length-4)+".pmg"), p);}catch(Exception e){Console.WriteLine("  load fail "+p+": "+e.Message);return null;} }
  static string Range(EtsModelData m, EtsModelPart part){
    float zl=1e9f,zh=-1e9f,xl=1e9f,xh=-1e9f,yl=1e9f,yh=-1e9f; int tris=0;
    for(int p=part.PieceStart;p<part.PieceStart+part.PieceCount&&p<m.Pieces.Length;p++){var pc=m.Pieces[p]; zl=Math.Min(zl,pc.BoundsMin[2]);zh=Math.Max(zh,pc.BoundsMax[2]);xl=Math.Min(xl,pc.BoundsMin[0]);xh=Math.Max(xh,pc.BoundsMax[0]);yl=Math.Min(yl,pc.BoundsMin[1]);yh=Math.Max(yh,pc.BoundsMax[1]); tris+=pc.Indices.Length/3;}
    return string.Format("z {0:F2}..{1:F2} (L {2:F2}) x {3:F2}..{4:F2} y {5:F2}..{6:F2} pieces {7} tris {8}", zl,zh,zh-zl,xl,xh,yl,yh,part.PieceCount,tris);
  }
  static void Main(string[] a){
    fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out string rep);
    string mode=a.Length>0?a[0]:"railing";
    var file = fs.ReadDefinitionFamily("/def/world", mode);
    var seen=new HashSet<string>();
    foreach(var u in file.Units){
      string path=u.GetString("model_desc");
      Console.WriteLine(u.Type+" "+u.Name+" : "+string.Join(" ", u.Attributes.Select(kv=>kv.Key+"="+kv.Value)));
      if(path==null||!seen.Add(path)) continue;
      var m=Load(path); if(m==null) continue;
      Console.WriteLine("  looks: "+string.Join(",",m.Looks));
      foreach(var part in m.Parts) Console.WriteLine("  part "+part.Name+": "+Range(m,part));
    }
  }
}
