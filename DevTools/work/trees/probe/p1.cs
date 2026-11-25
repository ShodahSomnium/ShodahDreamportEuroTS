using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(1);
 int roads=0, ownBoth=0, terr=0, tOwn=0, pref=0, pOwn=0; double maxOver=0; string worst="";
 foreach(var (x,z) in w.All()){ var s=w.Get(x,z); if(s==null) continue;
   float x0=x*4000f, z0=z*4000f, x1=x0+4000, z1=z0+4000;
   foreach(var it in s.Roads.Cast<EtsMapItem>().Concat(s.Prefabs)){
     double over=Math.Max(Math.Max(x0-it.KdopMin[0], it.KdopMax[0]-x1), Math.Max(z0-it.KdopMin[2], it.KdopMax[2]-z1));
     if(over>maxOver){ maxOver=over; worst=it.Type+" "+it.Uid.ToString("x")+" in "+x+","+z; } }
   foreach(var r in s.Roads){ roads++; if(s.NodesByUid.ContainsKey(r.StartNode)&&s.NodesByUid.ContainsKey(r.EndNode)) ownBoth++; }
   foreach(var r in s.Terrains){ terr++; if(s.NodesByUid.ContainsKey(r.StartNode)&&s.NodesByUid.ContainsKey(r.EndNode)) tOwn++; }
   foreach(var r in s.Prefabs){ pref++; if(r.NodeUids.All(u=>s.NodesByUid.ContainsKey(u))) pOwn++; }
 }
 Console.WriteLine($"roads {roads} own-nodes {ownBoth}; terrain {terr} own {tOwn}; prefabs {pref} own {pOwn}; max kdop overhang {maxOver:0.0} m ({worst})");
}}
