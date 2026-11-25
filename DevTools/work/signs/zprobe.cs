using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){ var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  string p=a[0]; var m = EtsModelData.Parse(fs.Read(p), fs.Read(p.Substring(0,p.Length-4)+".pmg"), p); float lx=float.Parse(a[1]), ly=float.Parse(a[2]);
  for (int i=0;i<m.Pieces.Length;i++){ var pc=m.Pieces[i]; var zs = new System.Collections.Generic.List<float>();
    for (int v=0; v<pc.VertexCount; v++){ float x=pc.Positions[v*3], y=pc.Positions[v*3+1], z=pc.Positions[v*3+2]; if (Math.Abs(x-lx)<2.5f && Math.Abs(y-ly)<1.2f) zs.Add(z); }
    // triangles covering the point (lx,ly): z at that point
    var hits = new System.Collections.Generic.List<string>();
    for (int t=0;t<pc.Indices.Length;t+=3){ int i0=pc.Indices[t],i1=pc.Indices[t+1],i2=pc.Indices[t+2]; float[] P0={pc.Positions[i0*3],pc.Positions[i0*3+1],pc.Positions[i0*3+2]}, P1={pc.Positions[i1*3],pc.Positions[i1*3+1],pc.Positions[i1*3+2]}, P2={pc.Positions[i2*3],pc.Positions[i2*3+1],pc.Positions[i2*3+2]};
      float d=(P1[1]-P2[1])*(P0[0]-P2[0])+(P2[0]-P1[0])*(P0[1]-P2[1]); if (Math.Abs(d)<1e-9) continue; float w0=((P1[1]-P2[1])*(lx-P2[0])+(P2[0]-P1[0])*(ly-P2[1]))/d, w1=((P2[1]-P0[1])*(lx-P2[0])+(P0[0]-P2[0])*(ly-P2[1]))/d, w2=1-w0-w1; if (w0<0||w1<0||w2<0) continue;
      float z=w0*P0[2]+w1*P1[2]+w2*P2[2]; float nx=(P1[1]-P0[1])*(P2[2]-P0[2])-(P1[2]-P0[2])*(P2[1]-P0[1]), ny=(P1[2]-P0[2])*(P2[0]-P0[0])-(P1[0]-P0[0])*(P2[2]-P0[2]), nz=(P1[0]-P0[0])*(P2[1]-P0[1])-(P1[1]-P0[1])*(P2[0]-P0[0]); hits.Add("z="+z.ToString("0.000")+(nz>0?"(+z face)":"(-z face)")); }
    Console.WriteLine("piece "+i+" mat "+pc.MaterialIndex+" "+m.LookMaterials[0][pc.MaterialIndex]+" covering ("+lx+","+ly+"): "+string.Join(" ",hits));
  } } }
