using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// s4: railing census: unit, model, count by side, offsets, flags; model bounds and part names.
static class P { static void Main(string[] a){
 var w=new Win(4); var st=new Dictionary<ulong,(int n,int l,int inv,int dbl,int ctr,List<float> offs,string ex)>();
 foreach(var (x,z) in w.All()){ var s=w.Get(x,z); if(s==null) continue; foreach(var r in s.Roads){ if(r.Railings==null) continue; foreach(var rl in r.Railings){ st.TryGetValue(rl.Model,out var v); if(v.offs==null) v.offs=new List<float>(); v.n++; if(rl.Left) v.l++; if(rl.Left?r.LeftRailingInverted:r.RightRailingInverted) v.inv++; if(rl.Left?r.LeftRailingDoubleSided:r.RightRailingDoubleSided) v.dbl++; if(rl.Left?r.LeftRailingCenterOnly:r.RightRailingCenterOnly) v.ctr++; if(v.offs.Count<2000) v.offs.Add(rl.Offset); if(v.ex==null&&s.NodesByUid.TryGetValue(r.StartNode,out var sn)) v.ex=$"{sn.Position[0]:0},{sn.Position[2]:0} look {r.Look}"; st[rl.Model]=v; } } }
 foreach(var kv in st.OrderByDescending(k=>k.Value.n)){ var u=w.Map.Railing(kv.Key); var path=u?.GetString("model_desc",null); string info="";
   if(path!=null){ string k="/"+EtsHashFs.Normalize(path); var pa=w.Fs.Read(k); var pg=w.Fs.Read(k.Substring(0,k.Length-4)+".pmg"); if(pa!=null&&pg!=null){ var m=EtsModelData.Parse(pa,pg,k); info=$"x {m.BoundsMin[0]:0.0}..{m.BoundsMax[0]:0.0} y {m.BoundsMin[1]:0.0}..{m.BoundsMax[1]:0.0} parts [{string.Join(",",m.Parts.Select(p=>p.Name))}]"; } }
   var o=kv.Value.offs; o.Sort(); Console.WriteLine($"{kv.Value.n,6} L{kv.Value.l} inv{kv.Value.inv} dbl{kv.Value.dbl} ctr{kv.Value.ctr} railing.{EtsToken.Decode(kv.Key)} {path} off {o[0]:0.0}/{o[o.Count/2]:0.0}/{o[o.Count-1]:0.0} {info} ex {kv.Value.ex}"); }
}}
