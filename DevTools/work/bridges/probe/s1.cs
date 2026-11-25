using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// s1: census of road side models map-wide: per sign unit, count of slots by side, offsets, distances, flags, model and bounds.
static class P { static void Main(string[] a){
 var w=new Win(4); var stat=new Dictionary<string,(int n,int left,int right,int shifted,int flipped,string ex)>();
 foreach(var (x,z) in w.All()){ var s=w.Get(x,z); if(s==null) continue;
  foreach(var r in s.Roads){ if(r.SideModels==null) continue; foreach(var m in r.SideModels){
    var u=w.Map.SignModel(m.Model); string key=m.Name+" | "+(u?.GetString("model_desc","?")??"no unit");
    stat.TryGetValue(key,out var v); v.n++; if(m.Left) v.left++; else v.right++; if(r.SideModelShifted(m.Left,m.Slot)) v.shifted++; if(r.SideModelFlipped(m.Left,m.Slot)) v.flipped++;
    if(v.ex==null){ var st=s.NodesByUid[r.StartNode]; v.ex=$"off {m.Offset} dist {m.Distance} look {r.Look} at {st.Position[0]:0},{st.Position[2]:0}"; } stat[key]=v; } } }
 foreach(var kv in stat.OrderByDescending(k=>k.Value.n)) Console.WriteLine($"{kv.Value.n,6} L{kv.Value.left} R{kv.Value.right} sh{kv.Value.shifted} fl{kv.Value.flipped}  {kv.Key}  ex {kv.Value.ex}");
}}
