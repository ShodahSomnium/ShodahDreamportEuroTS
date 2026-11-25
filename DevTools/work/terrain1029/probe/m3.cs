using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// m3 uidhex : one terrain item: nodes, sides, profile, quad grid brushes, vertex records
static class P { static void Main(string[] a){ var w=new Win(9); var m=w.Map; ulong uid=Convert.ToUInt64(a[0],16); int sx=int.Parse(a[1]), sz=int.Parse(a[2]);
 for(int dx=-1;dx<=1;dx++) for(int dz=-1;dz<=1;dz++) w.Get(sx+dx,sz+dz);
 foreach(var s in w.Held.Values){ if(s==null) continue; foreach(var t in s.Items.OfType<EtsMapTerrainItem>()){ if(t.Uid!=uid) continue;
  var n0=w.Node(t.StartNode); var n1=w.Node(t.EndNode);
  Console.WriteLine($"kdop {string.Join(",",t.KdopMin.Select(f=>f.ToString("0")))} .. {string.Join(",",t.KdopMax.Select(f=>f.ToString("0")))}");
  Console.WriteLine($"n0 {P3(n0.Position)} off {P3(t.StartOffset)} rot {string.Join(",",n0.Rotation.Select(f=>f.ToString("0.000")))}  n1 {P3(n1.Position)} off {P3(t.EndOffset)} len {t.Length} step {t.StepSize}");
  foreach(var (nm,sd) in new[]{("R",t.Right),("L",t.Left)}){ var pr=m.TerrainProfile(sd.ProfileToken);
   Console.WriteLine($"{nm} size {sd.Size} {sd.Profile} coef {sd.Coefficient} steps [{(pr!=null?string.Join(",",pr.GetArray("step")):"")}] heights [{(pr!=null?string.Join(",",pr.GetArray("height")):"")}]");
   var q=sd.Quads; if(q==null) continue; Console.WriteLine($"  grid {q.Rows}x{q.Cols} mats {string.Join(" ",q.MaterialTokens.Select(x=>EtsToken.Decode(x)))} offsets {q.OffsetCount}");
   for(int r=0;r<q.Rows;r++){ var line=""; for(int c=0;c<q.Cols;c++) line+=q.MainMaterial(r,c)+" "; Console.WriteLine("   row "+r+": "+line); }
   for(int i=0;i<q.OffsetCount;i++) Console.WriteLine($"   vtx col {q.Offsets[i*5]} row {q.Offsets[i*5+1]} d ({q.Offsets[i*5+2]:0.00},{q.Offsets[i*5+3]:0.00},{q.Offsets[i*5+4]:0.00})"); } } } }
 static string P3(float[] v)=>"("+string.Join(",",v.Select(f=>f.ToString("0.0")))+")";
}
