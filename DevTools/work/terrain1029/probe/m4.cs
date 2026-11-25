using System; using System.Linq; using System.Collections.Generic; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// m4 gx gz dirx dirz : along a ray, every 200 m from 400 to 2200: terrain items (with ground y), bezier patches, models within 150 m
static class P { static void Main(string[] a){ var w=new Win(16); var m=w.Map; float gx=float.Parse(a[0]), gz=float.Parse(a[1]), dx=float.Parse(a[2]), dz=float.Parse(a[3]);
 float l=(float)Math.Sqrt(dx*dx+dz*dz); dx/=l; dz/=l;
 var secs=new HashSet<(int,int)>(); for(int d=0; d<=2400; d+=200){ secs.Add(((int)Math.Floor((gx+dx*d)/4000),(int)Math.Floor((gz+dz*d)/4000))); }
 foreach(var (x,z) in secs) for(int ox=-1;ox<=1;ox++) for(int oz=-1;oz<=1;oz++) w.Get(x+ox,z+oz);
 for(int d=400; d<=2200; d+=200){ float px=gx+dx*d, pz=gz+dz*d; int ti=0, bp=0, md=0; float tmaxy=-999, tmin=999; var mods=new Dictionary<string,int>();
  foreach(var s in w.Held.Values){ if(s==null) continue;
   foreach(var it in s.Items){ var n=w.Node(Nd(it)); if(n==null) continue; float ex=n.Position[0]-px, ez=n.Position[2]-pz; if(ex*ex+ez*ez>150*150) continue;
     if(it is EtsMapTerrainItem t){ ti++; tmaxy=Math.Max(tmaxy,n.Position[1]); tmin=Math.Min(tmin,n.Position[1]); }
     else if(it is EtsMapBezierPatch) bp++; }
   foreach(var mo in s.Models){ var n=w.Node(mo.NodeUid); if(n==null) continue; float ex=n.Position[0]-px, ez=n.Position[2]-pz; if(ex*ex+ez*ez>150*150) continue; md++; string k=mo.Name; mods.TryGetValue(k,out int c); mods[k]=c+1; } }
  Console.WriteLine($"d {d} game ({px:0},{pz:0}): terrain items {ti} (node y {tmin:0}..{tmaxy:0}), bezier {bp}, models {md}: {string.Join(", ",mods.OrderByDescending(k=>k.Value).Take(6).Select(k=>k.Key+" x"+k.Value))}"); }
 }
 static ulong Nd(EtsMapItem it)=> it is EtsMapTerrainItem t? t.StartNode : it is EtsMapBezierPatch b? b.NodeUid : 0;
}
