using System; using System.Collections.Generic; using System.Globalization; using System.IO; using System.Linq; using UnityEngine; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static Win win;
  static Vector3 Hermite(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float s){ float s2=s*s, s3=s2*s; return (2f*s3-3f*s2+1f)*p0+(s3-2f*s2+s)*t0+(-2f*s3+3f*s2)*p1+(s3-s2)*t1; }
  static Vector3 Tangent(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float s){ float s2=s*s; return (6f*s2-6f*s)*p0+(3f*s2-4f*s+1f)*t0+(-6f*s2+6f*s)*p1+(3f*s2-2f*s)*t1; }
  static Vector3 U(float[] g)=> new Vector3(g[0],g[1],-g[2]);
  static Vector3 D(float[] g)=> new Vector3(g[0],g[1],-g[2]).normalized;
  static float[] Floats(List<string> v){ var r=new float[v.Count]; for(int i=0;i<v.Count;i++){ var p=EtsSiiValue.ParseFloats(v[i]); r[i]=p.Length>0?p[0]:0f; } return r; }
  static readonly float[] DefaultSteps = { 1, 1, 1, 2, 6, 6, 10, 10, 10, 20, 40, 50, 50, 50, 100, 100 };
  // The side's ground grid as EtsTerrainBuilder.BuildSide lays it (profile rows, columns, vertex-tool records; no transition blend).
  static Vector3[] Grid(EtsTerrainSide side, bool left, float startOffset, int defaultCols, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, Vector3 origin, float lift, out List<float> lateral, out int rows, out int cols){
    float[] steps=DefaultSteps, heights=null; var prof=win.Map.TerrainProfile(side.ProfileToken);
    if(prof!=null){ var s=Floats(prof.GetArray("step")); var h=Floats(prof.GetArray("height")); if(s.Length>0) steps=s; if(h.Length>0) heights=h; }
    lateral=new List<float>{0f}; var height=new List<float>{0f}; float cum=0f;
    for(int i=0; cum<side.Size-0.001f && i<512; i++){ float step=steps[Math.Min(i,steps.Length-1)]; float next=cum+step; float h0=heights!=null&&heights.Length>0?heights[Math.Min(i,heights.Length-1)]:0f; float h1=heights!=null&&heights.Length>0?heights[Math.Min(i+1,heights.Length-1)]:h0;
      if(next>side.Size){ float f=(side.Size-cum)/step; lateral.Add(side.Size); height.Add(Mathf.Lerp(h0,h1,f)*side.Coefficient); break; } lateral.Add(next); height.Add(h1*side.Coefficient); cum=next; }
    var quads=side.Quads; cols = quads!=null && quads.Cols>0 ? quads.Cols : defaultCols; rows=lateral.Count-1; if(quads!=null && quads.Rows>0 && quads.Rows<rows) rows=quads.Rows;
    if(rows<=0||cols<=0) return null; float sign=left?-1f:1f; int stride=cols+1; var v=new Vector3[(rows+1)*stride];
    for(int j=0;j<=cols;j++){ float s=(float)j/cols; Vector3 pt=Hermite(p0,t0,p1,t1,s), fw=Tangent(p0,t0,p1,t1,s); fw.y=0; if(fw.sqrMagnitude<1e-6f){ fw=p1-p0; fw.y=0; } fw.Normalize(); Vector3 r=Vector3.Cross(Vector3.up,fw).normalized*sign;
      for(int i=0;i<=rows;i++) v[i*stride+j]=pt+r*(startOffset+lateral[i])+Vector3.up*(height[i]+lift); }
    if(quads!=null && quads.OffsetCount>0) for(int k=0;k<quads.OffsetCount;k++){ int c=(int)quads.Offsets[k*5], rr=(int)quads.Offsets[k*5+1]; if(rr<=rows && c<=cols) v[rr*stride+c]=origin+new Vector3(quads.Offsets[k*5+2],quads.Offsets[k*5+3],-quads.Offsets[k*5+4]); }
    return v; }
  static Dictionary<long,List<float>> cells=new Dictionary<long,List<float>>();
  static long CK(float x,float z)=> ((long)Mathf.FloorToInt(x/8f)<<32) ^ (uint)Mathf.FloorToInt(z/8f);
  static void AddGrid(Vector3[] g){ if(g==null) return; foreach(var v in g){ long k=CK(v.x,v.z); if(!cells.TryGetValue(k,out var l)) cells[k]=l=new List<float>(); l.Add(v.y);} }
  static bool Covered(Vector3 p){ for(int dx=-1;dx<=1;dx++) for(int dz=-1;dz<=1;dz++){ if(cells.TryGetValue(CK(p.x+dx*8,p.z+dz*8),out var l)) foreach(float y in l) if(y>p.y+6f && y<p.y+150f) return true; } return false; }
  static void Main(string[] a){
    int sx0=int.Parse(a[0]), sx1=int.Parse(a[1]), sz0=int.Parse(a[2]), sz1=int.Parse(a[3]);
    win=new Win(40); var secs=win.All().Where(p=>p.x>=sx0-1&&p.x<=sx1+1&&p.z>=sz0-1&&p.z<=sz1+1).ToList();
    foreach(var (x,z) in secs){ var sec=win.Get(x,z); if(sec==null) continue;
      foreach(var item in sec.Terrains){ if(!sec.NodesByUid.TryGetValue(item.StartNode, out var st) || !sec.NodesByUid.TryGetValue(item.EndNode, out var en)) continue;
        var sp=new[]{st.Position[0]+item.StartOffset[0],st.Position[1]+item.StartOffset[1],st.Position[2]+item.StartOffset[2]}; var ep=new[]{en.Position[0]+item.EndOffset[0],en.Position[1]+item.EndOffset[1],en.Position[2]+item.EndOffset[2]};
        float len=Math.Max(0.5f,item.Length); Vector3 p0=U(sp), p1=U(ep), t0=D(st.Forward())*len, t1=D(en.Forward())*len; int tc=Math.Max(1,(int)Math.Ceiling(len/item.ColumnStep)); Vector3 no=U(st.Position);
        AddGrid(Grid(item.Right,false,0f,tc,p0,t0,p1,t1,no,0f,out _,out _,out _)); AddGrid(Grid(item.Left,true,0f,tc,p0,t0,p1,t1,no,0f,out _,out _,out _)); } }
    long[] bAll=new long[32], bCov=new long[32], rAll=new long[32], rCov=new long[32]; long nAll=0,nCov=0; var lst=new List<string>();
    foreach(var (x,z) in secs.Where(p=>p.x>=sx0&&p.x<=sx1&&p.z>=sz0&&p.z<=sz1)){ var sec=win.Get(x,z); if(sec==null) continue;
      foreach(var r in sec.Roads){ if(!sec.NodesByUid.TryGetValue(r.StartNode, out var st) || !sec.NodesByUid.TryGetValue(r.EndNode, out var en)) continue;
        Vector3 p0=U(st.Position), p1=U(en.Position), t0=D(st.Forward())*r.Length, t1=D(en.Forward())*r.Length; int n=Math.Max(2,(int)(r.Length/10)); int c=0;
        for(int i=0;i<=n;i++) if(Covered(Hermite(p0,t0,p1,t1,(float)i/n))) c++;
        bool cov=c>=(n+1)*0.6f; nAll++; if(cov) nCov++;
        for(int b=0;b<32;b++){ if((r.Flags>>b&1)!=0){ bAll[b]++; if(cov) bCov[b]++; } if((r.RoadFlags>>b&1)!=0){ rAll[b]++; if(cov) rCov[b]++; } }
        if(cov) lst.Add($"{r.Uid:x} {st.Position[0]:0},{st.Position[1]:0},{st.Position[2]:0} len {r.Length:0} {c}/{n+1} flags {r.Flags:x8} rf {r.RoadFlags:x8} look {(win.Map.RoadLook(r.LookToken)?.GetString("name","")??"?")}"); } }
    Console.WriteLine($"roads {nAll}, covered (60%+ of centreline under terrain 6-150 m above) {nCov}");
    for(int b=0;b<32;b++) Console.WriteLine($"bit {b}: Flags all {bAll[b]} ({100.0*bAll[b]/nAll:0.0}%) covered {bCov[b]} ({100.0*bCov[b]/Math.Max(1,nCov):0.0}%)   RoadFlags all {rAll[b]} ({100.0*rAll[b]/nAll:0.0}%) covered {rCov[b]} ({100.0*rCov[b]/Math.Max(1,nCov):0.0}%)");
    foreach(var l in lst.Take(60)) Console.WriteLine(l);
  }
}
