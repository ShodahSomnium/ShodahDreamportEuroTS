using System; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static EtsModelData ReadM(EtsFileSystem fs,string p,out string f){ f=null; var a=fs.Read(p); var b=fs.Read(p.Substring(0,p.Length-4)+".pmg"); if(a==null||b==null){f="missing";return null;} return EtsModelData.Parse(a,b,p);}
  static string HK(byte[] g,int po,int ps,int at){ if(at<0||at>=ps) return "-"; int e=po+at; while(e<po+ps&&g[e]!=0)e++; return System.Text.Encoding.ASCII.GetString(g,po+at,e-po-at);}
  static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  bool pieces = Environment.GetEnvironmentVariable("PIECES")!=null;
  foreach (var p in a) { var m = ReadM(fs, p, out string f); if (m==null){Console.WriteLine(p+": "+f); continue;}
    var pmg=fs.Read(p.Substring(0,p.Length-4)+".pmg"); int po=BitConverter.ToInt32(pmg,88), ps=BitConverter.ToInt32(pmg,92);
    Console.WriteLine(p+" bounds ("+string.Join(",",m.BoundsMin)+")..("+string.Join(",",m.BoundsMax)+") looks="+string.Join(",",m.Looks)+" variants="+string.Join(",",m.Variants));
    if (pieces) for (int li=0; li<m.LookMaterials.Length; li++) Console.WriteLine("  look "+li+": "+string.Join(" ", m.LookMaterials[li]));
    foreach (var l in m.Locators) Console.WriteLine("  loc "+l.Name+" ["+HK(pmg,po,ps,l.Hookup)+"] pos ("+string.Join(", ",Array.ConvertAll(l.Position,x=>x.ToString("0.###")))+") rot ("+string.Join(", ",Array.ConvertAll(l.Rotation,x=>x.ToString("0.###")))+") s "+l.Scale);
    if (pieces) for (int i=0;i<m.Pieces.Length;i++){ var pc=m.Pieces[i]; Console.WriteLine("  piece "+i+" mat "+pc.MaterialIndex+" v "+pc.VertexCount+" uvs "+pc.Uvs.Length+" bb ("+string.Join(",",Array.ConvertAll(pc.BoundsMin,x=>x.ToString("0.##")))+")..("+string.Join(",",Array.ConvertAll(pc.BoundsMax,x=>x.ToString("0.##")))+")"); }
  }
}}
