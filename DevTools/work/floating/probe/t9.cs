using System; using System.Collections.Generic; using System.Globalization; using System.IO; using System.Linq; using UnityEngine; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static CultureInfo ic = CultureInfo.InvariantCulture;
  static EtsRoadClearance.Defs defs; static Win win;
  static Dictionary<long, EtsRoadClearance.SectorShapes> shapes = new Dictionary<long, EtsRoadClearance.SectorShapes>();
  static EtsRoadClearance.SectorShapes Shapes(int x,int z){ long k=Win.K(x,z); if(shapes.TryGetValue(k, out var s)) return s; var sec=win.Get(x,z); s = sec==null? null : EtsRoadClearance.Compute(defs, sec); shapes[k]=s; return s; }
  static bool Under(Vector3 p, float minDrop, out EtsRoadClearance.Hit hit){
    float b0=EtsRoadClearance.ClearBelow, a0=EtsRoadClearance.ClearAbove; EtsRoadClearance.ClearBelow=5000; EtsRoadClearance.ClearAbove=-minDrop;
    try{ float gx=p.x, gz=-p.z; EtsMapSector.SectorOf(gx-500,gz-500,out int x0,out int z0); EtsMapSector.SectorOf(gx+500,gz+500,out int x1,out int z1);
      for(int sx=x0;sx<=x1;sx++) for(int sz=z0;sz<=z1;sz++){ var s=Shapes(sx,sz); if(s!=null && EtsRoadClearance.Blocked(s,p.x,p.y,p.z,0f,0,out hit)) return true; } hit=default; return false; }
    finally{ EtsRoadClearance.ClearBelow=b0; EtsRoadClearance.ClearAbove=a0; } }
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
  static long[] bitAll=new long[32], bitOver=new long[32]; static long nAll, nOver;
  static List<(int n,int tot,string s)> worst=new List<(int,int,string)>();
  static void Item(string what, uint flags, EtsTerrainSide side, bool left, float startOffset, int defCols, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, Vector3 origin, float lift, bool isTerrainItem){
    if(side==null||side.Size<=0.5f) return;
    var g=Grid(side,left,startOffset,defCols,p0,t0,p1,t1,origin,lift,out var lat,out int rows,out int cols); if(g==null) return;
    int over=0; string ex=null; float worstDrop=0;
    for(int i=0;i<g.Length;i++){ if(Under(g[i],15f,out var h)){ over++; float drop=g[i].y-h.SurfaceY; if(drop>worstDrop){ worstDrop=drop; ex=$"at game ({g[i].x:0},{g[i].y:0},{-g[i].z:0}) {drop:0} m over {(h.Kind==1?"road":"prefab")} {h.Uid:x}"; } } }
    if(isTerrainItem){ nAll++; bool o=over>=3; if(o) nOver++; for(int b=0;b<32;b++) if((flags>>b&1)!=0){ bitAll[b]++; if(o) bitOver[b]++; } }
    if(over>0) worst.Add((over,g.Length,$"{what} flags {flags:x8} size {side.Size:0} {EtsToken.Decode(side.ProfileToken)}*{side.Coefficient} offs {(side.Quads!=null?side.Quads.OffsetCount:0)}: {over}/{g.Length} vertices >=15 m over a road/junction; worst {ex}"));
  }
  static void Main(string[] a){
    int sx0=int.Parse(a[0]), sx1=int.Parse(a[1]), sz0=int.Parse(a[2]), sz1=int.Parse(a[3]);
    win=new Win(25); defs=new EtsRoadClearance.Defs(win.Fs);
    foreach(var (x,z) in win.All().Where(p=>p.x>=sx0&&p.x<=sx1&&p.z>=sz0&&p.z<=sz1).OrderBy(p=>p.x).ThenBy(p=>p.z)){
      var sec=win.Get(x,z); if(sec==null) continue;
      foreach(var item in sec.Terrains){
        if(!sec.NodesByUid.TryGetValue(item.StartNode, out var st) || !sec.NodesByUid.TryGetValue(item.EndNode, out var en)) continue;
        var sp=new[]{st.Position[0]+item.StartOffset[0],st.Position[1]+item.StartOffset[1],st.Position[2]+item.StartOffset[2]};
        var ep=new[]{en.Position[0]+item.EndOffset[0],en.Position[1]+item.EndOffset[1],en.Position[2]+item.EndOffset[2]};
        float len=Math.Max(0.5f,item.Length); Vector3 p0=U(sp), p1=U(ep), t0=D(st.Forward())*len, t1=D(en.Forward())*len;
        int tc = Math.Max(1,(int)Math.Ceiling(len/item.ColumnStep)); Vector3 no=U(st.Position);
        Item("terrain "+item.Uid.ToString("x")+" R sec "+x+","+z, item.Flags, item.Right,false,0f,tc,p0,t0,p1,t1,no,0f,true);
        Item("terrain "+item.Uid.ToString("x")+" L sec "+x+","+z, item.Flags, item.Left,true,0f,tc,p0,t0,p1,t1,no,0f,true);
      }
      foreach(var road in sec.Roads){
        if(!sec.NodesByUid.TryGetValue(road.StartNode, out var st) || !sec.NodesByUid.TryGetValue(road.EndNode, out var en)) continue;
        float[] w = defs.LookWidths(road.LookToken); float hr=w[0], hl=w[1]; if(w[2]<0.5f && road.IsCityRoad){ hr+=road.Right.SidewalkSize; hl+=road.Left.SidewalkSize; }
        Vector3 p0=U(st.Position), p1=U(en.Position), t0=D(st.Forward())*road.Length, t1=D(en.Forward())*road.Length;
        int rc = Math.Max(1,(int)Math.Ceiling(road.Length/15f))*(road.Superfine?15:road.HighPoly?3:1); float lift=(w[2]<0.5f&&road.IsCityRoad)?0.2f:0f;
        Item("road "+road.Uid.ToString("x")+" R sec "+x+","+z, road.Flags, road.Right,false,hr,rc,p0,t0,p1,t1,p0,lift,false);
        Item("road "+road.Uid.ToString("x")+" L sec "+x+","+z, road.Flags, road.Left,true,hl,rc,p0,t0,p1,t1,p0,lift,false);
      }
      Console.Error.WriteLine("sector "+x+","+z+" done");
    }
    Console.WriteLine($"terrain item sides {nAll}, with >=3 vertices 15 m+ over a road/junction: {nOver}");
    for(int b=0;b<32;b++) Console.WriteLine($"bit {b}: all {bitAll[b]} ({100.0*bitAll[b]/Math.Max(1,nAll):0.0}%)  over {bitOver[b]} ({100.0*bitOver[b]/Math.Max(1,nOver):0.0}%)");
    foreach(var w in worst.OrderByDescending(q=>q.n).Take(80)) Console.WriteLine(w.s);
  }
}
