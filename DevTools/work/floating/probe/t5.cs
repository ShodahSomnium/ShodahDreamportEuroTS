using System; using System.Collections.Generic; using System.Linq; using System.Globalization; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 float gx=float.Parse(a[0]), gz=float.Parse(a[1]), rad=float.Parse(a[2]);
 var w = new Win(9); var m=w.Map; EtsMapSector.SectorOf(gx,gz,out int sx,out int sz);
 for(int dx=-1;dx<=1;dx++) for(int dz=-1;dz<=1;dz++) w.Get(sx+dx,sz+dz);
 foreach(var s in w.Held.Values.ToList()){ if(s==null) continue;
  foreach(var md in s.Models){ var n=w.Node(md.NodeUid); if(n==null) continue; float d=(float)Math.Sqrt((n.Position[0]-gx)*(n.Position[0]-gx)+(n.Position[2]-gz)*(n.Position[2]-gz)); if(d>rad) continue; var def=m.ModelDefinition(md.ModelToken); string p=def!=null?def.GetString("model_desc",""):"?";
    Console.WriteLine($"MODEL {md.Uid:x} at {n.Position[0]:0},{n.Position[1]:0},{n.Position[2]:0} d {d:0} {md.Name} {p} scale {string.Join(",",md.Scale)}"); }
  foreach(var r in s.Roads){ var n0=w.Node(r.StartNode); var n1=w.Node(r.EndNode); if(n0==null||n1==null) continue; float mx=(n0.Position[0]+n1.Position[0])/2, mz=(n0.Position[2]+n1.Position[2])/2; float d=(float)Math.Sqrt((mx-gx)*(mx-gx)+(mz-gz)*(mz-gz)); if(d>rad) continue; var lk=m.RoadLook(r.LookToken);
    Console.WriteLine($"ROAD {r.Uid:x} {n0.Position[0]:0},{n0.Position[1]:0},{n0.Position[2]:0} -> {n1.Position[0]:0},{n1.Position[1]:0},{n1.Position[2]:0} len {r.Length:0} look {(lk!=null?lk.GetString("name",""):"?")} R {r.Right.Size:0}/{EtsToken.Decode(r.Right.ProfileToken)} L {r.Left.Size:0}/{EtsToken.Decode(r.Left.ProfileToken)} flags {r.Flags:x8} rf {r.RoadFlags:x8}"); }
  foreach(var t in s.Terrains){ var n0=w.Node(t.StartNode); var n1=w.Node(t.EndNode); if(n0==null||n1==null) continue; float mx=(n0.Position[0]+n1.Position[0])/2, mz=(n0.Position[2]+n1.Position[2])/2; float d=(float)Math.Sqrt((mx-gx)*(mx-gx)+(mz-gz)*(mz-gz)); if(d>rad) continue;
    Console.WriteLine($"TERR {t.Uid:x} {n0.Position[0]:0},{n0.Position[1]:0},{n0.Position[2]:0} -> {n1.Position[0]:0},{n1.Position[1]:0},{n1.Position[2]:0} len {t.Length:0} R {t.Right.Size:0}/{EtsToken.Decode(t.Right.ProfileToken)}*{t.Right.Coefficient} L {t.Left.Size:0}/{EtsToken.Decode(t.Left.ProfileToken)}*{t.Left.Coefficient} kdopY {t.KdopMin[1]:0}..{t.KdopMax[1]:0}"); }
 }
}}
