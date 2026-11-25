using System; using System.Collections.Generic; using System.Linq;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static EtsFileSystem fs;
  static EtsSiiFile Sii(string p){ var t=fs.ReadText(p); return t==null?null:EtsSii.Parse(t,p,fs.ReadText); }
  static void Main(string[] a){
    fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var types=new[]{"car","truck","truck_light","truck_medium","truck_heavy","bus","police","fire","medical"};
    var countries=new[]{"uk","czech","poland","france","germany"};
    var cov=new Dictionary<string,Dictionary<string,float>>();
    foreach(var c in countries){ var d=new Dictionary<string,float>(); var f=Sii("/def/country/"+c+"/traffic.sii"); foreach(var u in f.Units){ var o=u.GetString("object"); if(o!=null) d[o]=u.GetFloat("spawn_ratio",-1);} cov[c]=d; }
    foreach(var t in types){
      var f=Sii("/def/vehicle/traffic_storage_"+t+".sii"); if(f==null){Console.WriteLine("no "+t); continue;}
      var variants=new HashSet<string>(); foreach(var u in f.Units) if(u.Type=="traffic_vehicle") foreach(var v in u.GetArray("variant")) variants.Add(v);
      int n=0;
      foreach(var u in f.Units){ if(u.Type!="traffic_vehicle") continue; n++;
        string own=u.Has("spawn_ratio")?u.GetFloat("spawn_ratio").ToString():"-";
        string cs=string.Join(" ", countries.Select(c=>cov[c].TryGetValue(u.Name,out var r)?c.Substring(0,2)+"="+r:""));
        string tags=string.Join(",",u.GetArray("tags"));
        string cond=u.GetArray("spawn_condition").Count>0?"COND":"";
        Console.WriteLine(t+"\t"+u.Name+"\town="+own+(variants.Contains(u.Name)?" VARIANT":"")+" "+cond+"\t"+cs+"\ttags="+tags+"\tmax="+u.GetFloat("max_speed",-1)+" mass="+u.GetFloat("vehicle_mass",-1)+" trl="+string.Join("|",u.GetArray("trailer_chains")));
      }
    }
  }
}
