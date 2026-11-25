using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P3 { static void Main(string[] a){ Lib.Mount(); foreach(var path in a){ var m=Lib.Model(path); if(m==null){Console.WriteLine(path+": none");continue;}
  Console.WriteLine(path+" looks "+string.Join(",",m.Looks));
  foreach(var p in m.Parts){ for(int q=p.PieceStart;q<p.PieceStart+p.PieceCount;q++){ var pc=m.Pieces[q]; string[] lm=m.LookMaterials[0]; string mp=pc.MaterialIndex<lm.Length?lm[pc.MaterialIndex]:null; var d=Lib.Mat(mp);
    Console.WriteLine(string.Format("  {0,-12} piece {1,3} tris {2,5} y {3:F2}..{4:F2} z {5:F2}..{6:F2} x {7:F2}..{8:F2} {9} {10} {11}", p.Name,q,pc.Indices.Length/3,pc.BoundsMin[1],pc.BoundsMax[1],pc.BoundsMin[2],pc.BoundsMax[2],pc.BoundsMin[0],pc.BoundsMax[0],mp,d==null?"?":d.Effect, d==null?"":string.Join(" ",d.Attributes.Select(kv=>kv.Key+"="+kv.Value))));}}}}}
