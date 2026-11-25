using System; using System.Collections.Generic; using System.Linq; using System.Globalization; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static string F(float[] v)=>string.Join(",",v.Select(f=>f.ToString("0.##",CultureInfo.InvariantCulture)));
 static void Main(string[] a){
 float gx=float.Parse(a[0]), gz=float.Parse(a[1]), rad=a.Length>2?float.Parse(a[2]):60f; string only=a.Length>3?a[3]:"";
 var w = new Win(4); var m=w.Map; EtsMapSector.SectorOf(gx,gz,out int sx,out int sz);
 for(int dx=-1;dx<=1;dx++) for(int dz=-1;dz<=1;dz++){ var s=w.Get(sx+dx,sz+dz); if(s==null) continue;
  foreach(var t in s.Terrains){ if(gx<t.KdopMin[0]-rad||gx>t.KdopMax[0]+rad||gz<t.KdopMin[2]-rad||gz>t.KdopMax[2]+rad) continue; if(only!=""&&!t.Uid.ToString("x").Contains(only)) continue;
   var n0=w.Node(t.StartNode); var n1=w.Node(t.EndNode);
   Console.WriteLine($"TERRAIN {t.Uid:x} sec {sx+dx},{sz+dz} flags {t.Flags:x8} len {t.Length:0.0} prev {t.PreviousLength:0.0} step {t.StepSize} kdop {F(t.KdopMin)} .. {F(t.KdopMax)} body {t.BodyLength}");
   Console.WriteLine($"   start {(n0==null?"?":F(n0.Position)+" rot "+F(n0.Rotation)+" fl "+n0.Flags.ToString("x"))} off {F(t.StartOffset)}");
   Console.WriteLine($"   end   {(n1==null?"?":F(n1.Position)+" rot "+F(n1.Rotation)+" fl "+n1.Flags.ToString("x"))} off {F(t.EndOffset)}");
   foreach(var (nm,sd) in new[]{("R",t.Right),("L",t.Left)}){ var pr=m.TerrainProfile(sd.ProfileToken);
     Console.WriteLine($"   {nm}: size {sd.Size} profile {EtsToken.Decode(sd.ProfileToken)} coef {sd.Coefficient} quads {(sd.Quads==null?"-":sd.Quads.Rows+"x"+sd.Quads.Cols+" offs "+sd.Quads.OffsetCount)} prof.step [{(pr==null?"":string.Join(" ",pr.GetArray("step")))}] height [{(pr==null?"":string.Join(" ",pr.GetArray("height")))}]");
     if(sd.Quads!=null && sd.Quads.OffsetCount>0){ var o=sd.Quads.Offsets; for(int k=0;k<Math.Min(sd.Quads.OffsetCount,8);k++) Console.WriteLine($"      off c{o[k*5]} r{o[k*5+1]} ({o[k*5+2]:0.0},{o[k*5+3]:0.0},{o[k*5+4]:0.0})"); float mn=1e9f,mx=-1e9f; for(int k=0;k<sd.Quads.OffsetCount;k++){ mn=Math.Min(mn,o[k*5+3]); mx=Math.Max(mx,o[k*5+3]); } Console.WriteLine($"      offset y range {mn:0.0}..{mx:0.0} over {sd.Quads.OffsetCount}"); }
   }
  }
 }
}}
