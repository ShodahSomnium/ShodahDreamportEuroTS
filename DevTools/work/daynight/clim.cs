using System; using System.Collections.Generic; using System.Globalization; using System.Linq; using System.Text;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static CultureInfo ic = CultureInfo.InvariantCulture;
  static string F(float v){ return v.ToString("0.#####", ic); }
  static string V(EtsSiiUnit u, string k, int s){ var a=u.GetArray(k); string raw = a.Count>0 ? a[Math.Min(s,a.Count-1)] : u.GetString(k,""); var f=EtsSiiValue.ParseFloats(raw); if(raw.StartsWith("/")||raw.Length==0||raw.StartsWith("\"")) return raw; return string.Join(",", f.Select(F)); }
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    string path=a[0]; var keys=a[1].Split(','); int slot0 = a.Length>2? int.Parse(a[2]) : -1;
    var sii = fs.ReadSii(path);
    foreach(var u in sii.Units){
      if(!u.Has("low_elevation")) continue;
      int n = u.GetArray("weight").Count;
      var sb=new StringBuilder();
      sb.Append(u.Name+" el "+F(EtsSiiValue.ParseFloat(u.GetString("low_elevation"),0))+" dir "+u.GetString("sun_direction")+" slots "+n+"\n");
      for(int s=0;s<n;s++){ if(slot0>=0 && s!=slot0) continue; sb.Append("  ["+s+"]"); foreach(var k in keys) sb.Append(" "+k+"="+V(u,k,s)); sb.Append("\n"); }
      Console.Write(sb);
    }
  }
}
