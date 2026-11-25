using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// s3: road looks used on the map with a template: their template models' parts, variants, bounds; and edge models.
static class P { static void Main(string[] a){
 var w=new Win(4); var used=new Dictionary<ulong,(int n,HashSet<string> vars)>();
 foreach(var (x,z) in w.All()){ var s=w.Get(x,z); if(s==null) continue; foreach(var r in s.Roads){ used.TryGetValue(r.LookToken,out var v); if(v.vars==null) v.vars=new HashSet<string>(); v.n++; v.vars.Add(EtsToken.Decode(r.VariantToken)+"/"+EtsToken.Decode(r.MaterialToken)); used[r.LookToken]=v; } }
 var seen=new HashSet<string>();
 foreach(var kv in used.OrderByDescending(k=>k.Value.n)){ var look=w.Map.RoadLook(kv.Key); if(look==null||!look.Has("template_right")) continue;
   foreach(var key in new[]{"template_right","template_left"}){ if(!look.Has(key)) continue; var path=look.GetString(key); 
     string k="/"+EtsHashFs.Normalize(path); var pa=w.Fs.Read(k); var pg=w.Fs.Read(k.Substring(0,k.Length-4)+".pmg"); if(pa==null||pg==null){ Console.WriteLine("missing "+k); continue; }
     var m=EtsModelData.Parse(pa,pg,k); 
     Console.WriteLine($"{kv.Value.n,6} road.{EtsToken.Decode(kv.Key)} \"{look.GetString("name","")}\" {key} {k} off {look.GetFloat("road_offset",0)} bounds x {m.BoundsMin[0]:0.0}..{m.BoundsMax[0]:0.0} y {m.BoundsMin[1]:0.0}..{m.BoundsMax[1]:0.0} z {m.BoundsMin[2]:0.0}..{m.BoundsMax[2]:0.0} variants [{string.Join(",",m.Variants)}] used [{string.Join(" ",kv.Value.vars.Take(6))}]");
     if(seen.Add(k)) foreach(var p in m.Parts){ float y0=1e9f,y1=-1e9f,x0=1e9f,x1=-1e9f; for(int i=p.PieceStart;i<p.PieceStart+p.PieceCount&&i<m.Pieces.Length;i++){ y0=Math.Min(y0,m.Pieces[i].BoundsMin[1]); y1=Math.Max(y1,m.Pieces[i].BoundsMax[1]); x0=Math.Min(x0,m.Pieces[i].BoundsMin[0]); x1=Math.Max(x1,m.Pieces[i].BoundsMax[0]); }
       Console.WriteLine($"        part {p.Name} pieces {p.PieceCount} x {x0:0.0}..{x1:0.0} y {y0:0.0}..{y1:0.0} vis [{string.Join("",Enumerable.Range(0,m.Variants.Length).Select(v=>p.IsVisible(v)?"1":"0"))}]"); } } }
}}
