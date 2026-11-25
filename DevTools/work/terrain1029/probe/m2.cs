using System; using System.Linq; using System.Collections.Generic; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// m2: every road look with a template: template x bounds, road_offset, divided; how lopsided one-way templates are
static class P { static void Main(string[] a){ var fs=EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _); var m=new EtsMap(fs);
 var seen=new Dictionary<string,string>(); int lop=0, tot=0;
 m.RoadLook(0); var fam=fs.ReadDefinitionFamily("/def/world","road_look"); foreach(var lk in fam.Units){ string t=lk.GetString("template_right",null); if(string.IsNullOrEmpty(t)) continue; tot++;
  var md=Load(fs,t); if(md==null){ Console.WriteLine("NOLOAD "+lk.Name+" "+t); continue; }
  float off=lk.GetFloat("road_offset",0f); bool div=off>0.01f||lk.Has("template_left");
  float mn=md.BoundsMin[0], mx=md.BoundsMax[0]; float used= div? off+mx : Math.Max(-mn,mx);
  float gapL = div? 0 : used-(-mn), gapR = div?0: used-mx;
  if(!div && Math.Abs(mn+mx)>0.05f) lop++;
  Console.WriteLine($"{lk.Name,-14} '{lk.GetString("name","")}' {t} x {mn:0.00}..{mx:0.00} off {off} div {div} -> half {used:0.00}  gapL {gapL:0.00} gapR {gapR:0.00}");
 }
 Console.WriteLine("templates "+tot+", one-way lopsided "+lop);
 }
 static EtsModelData Load(EtsFileSystem fs,string p){ try{ var pmd=fs.Read(p); var pmg=fs.Read(p.Substring(0,p.Length-4)+".pmg"); if(pmd==null||pmg==null) return null; return EtsModelData.Parse(pmd,pmg,p);}catch{return null;} }
}
