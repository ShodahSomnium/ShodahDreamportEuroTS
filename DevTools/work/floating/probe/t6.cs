using System; using System.Collections.Generic; using System.Linq; using System.Globalization; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(2); var m=w.Map;
 var hb=new Dictionary<string,int>();
 void H(string k){ hb.TryGetValue(k,out int n); hb[k]=n+1; }
 int sx0=int.Parse(a[0]), sx1=int.Parse(a[1]), sz0=int.Parse(a[2]), sz1=int.Parse(a[3]);
 // Before DropGuarded: parse ourselves without dropping? Sector Parse drops roads/prefabs/buildings; GuardDropped keeps uids.
 foreach(var (x,z) in w.All()){ if(x<sx0||x>sx1||z<sz0||z>sz1) continue; var s=w.Get(x,z); if(s==null) continue;
   H("dropped(road/prefab/bld) "+s.GuardDropped.Count);
   foreach(var t in s.Terrains){ H("terrain b1="+((t.Flags>>8)&0xFF)+" b2="+((t.Flags>>16)&0xFF)+" b3="+((t.Flags>>24)&0xFF)); }
   foreach(var t in s.Models){ H("model b1="+((t.Flags>>8)&0xFF)+" b2="+((t.Flags>>16)&0xFF)); }
   foreach(var t in s.Curves){ H("curve b1="+((t.Flags>>8)&0xFF)+" b2="+((t.Flags>>16)&0xFF)); }
   foreach(var t in s.BezierPatches){ H("bezier b1="+((t.Flags>>8)&0xFF)+" b2="+((t.Flags>>16)&0xFF)); }
   foreach(var t in s.Prefabs){ H("prefab guard="+t.DlcGuard); }
   foreach(var t in s.Roads){ H("road guard="+t.DlcGuard); }
 }
 foreach(var kv in hb.OrderBy(k=>k.Key)) if(kv.Value>0) Console.WriteLine(kv.Value+"\t"+kv.Key);
}}
