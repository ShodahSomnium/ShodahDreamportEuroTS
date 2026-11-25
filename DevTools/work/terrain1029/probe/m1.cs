using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// m1 gx gz r : roads, terrain items with a node within r of (gx,gz): sides, sizes, profiles, quads
static class P { static void Main(string[] a){ var w=new Win(9); var m=w.Map; float gx=float.Parse(a[0]), gz=float.Parse(a[1]), r=float.Parse(a[2]);
 int sx=(int)Math.Floor(gx/4000), sz=(int)Math.Floor(gz/4000);
 for(int dx=-1;dx<=1;dx++) for(int dz=-1;dz<=1;dz++) w.Get(sx+dx,sz+dz); foreach(var s0 in w.Held.Values){ if(s0==null) continue; var r0=s0.Roads.FirstOrDefault(); if(r0!=null){ var q=w.Node(r0.StartNode); Console.WriteLine("road "+r0.Uid.ToString("x")+" node "+(q==null?"null":q.Position[0]+","+q.Position[2])+" roads "+s0.Roads.Count);} }
 Console.WriteLine("sectors "+sx+","+sz+" held "+w.Held.Count+" avail "+w.Map.AvailableSectors.Count()+" e.g. "+w.Map.AvailableSectors.First());
 foreach(var s in w.Held.Values){ if(s==null) continue;
  foreach(var rd in s.Roads){ var n0=w.Node(rd.StartNode); var n1=w.Node(rd.EndNode); if(n0==null||n1==null) continue;
   float d0=D(n0,gx,gz), d1=D(n1,gx,gz), dm=Seg(n0,n1,gx,gz); if(Math.Min(dm,Math.Min(d0,d1))>r) continue;
   var lk=m.RoadLook(rd.LookToken);
   Console.WriteLine($"ROAD {rd.Uid:x} look {(lk!=null?lk.Name:"?")} '{(lk!=null?lk.GetString("name",""):"")}' tmplR {(lk!=null?lk.GetString("template_right",""):"")} len {rd.Length:0.0} n0 ({n0.Position[0]:0.0},{n0.Position[1]:0.0},{n0.Position[2]:0.0}) n1 ({n1.Position[0]:0.0},{n1.Position[1]:0.0},{n1.Position[2]:0.0}) dist {dm:0.0} city {rd.IsCityRoad}");
   Console.WriteLine($"  edges R {EtsToken.Decode(rd.RightEdgeToken)} L {EtsToken.Decode(rd.LeftEdgeToken)} RI {EtsToken.Decode(rd.RightInnerEdgeToken)} LI {EtsToken.Decode(rd.LeftInnerEdgeToken)} LO {EtsToken.Decode(rd.LeftOuterEdgeToken)}");
   Side("  R",rd.Right); Side("  L",rd.Left); }
  foreach(var t in s.Items.OfType<EtsMapTerrainItem>()){ var n0=w.Node(t.StartNode); var n1=w.Node(t.EndNode); if(n0==null||n1==null) continue; float dm=Seg(n0,n1,gx,gz); if(dm>r) continue;
   Console.WriteLine($"TERR {t.Uid:x} len {t.Length:0.0} n0 ({n0.Position[0]:0.0},{n0.Position[1]:0.0},{n0.Position[2]:0.0}) n1 ({n1.Position[0]:0.0},{n1.Position[1]:0.0},{n1.Position[2]:0.0}) dist {dm:0.0}"); Side("  R",t.Right); Side("  L",t.Left);} }
 }
 static void Side(string n, EtsTerrainSide s){ Console.WriteLine($"{n} size {s.Size:0.0} prof {s.Profile} coef {s.Coefficient} sidewalk {s.SidewalkSize} quads {(s.Quads!=null? s.Quads.Rows+"x"+s.Quads.Cols:"-")}"); }
 static float D(EtsMapNode n,float x,float z)=>(float)Math.Sqrt((n.Position[0]-x)*(n.Position[0]-x)+(n.Position[2]-z)*(n.Position[2]-z));
 static float Seg(EtsMapNode a,EtsMapNode b,float x,float z){ float vx=b.Position[0]-a.Position[0], vz=b.Position[2]-a.Position[2]; float t=((x-a.Position[0])*vx+(z-a.Position[2])*vz)/Math.Max(1e-6f,vx*vx+vz*vz); t=Math.Max(0,Math.Min(1,t)); float px=a.Position[0]+vx*t-x, pz=a.Position[2]+vz*t-z; return (float)Math.Sqrt(px*px+pz*pz); }
}
