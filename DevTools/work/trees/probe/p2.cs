using System; using System.Collections.Generic; using System.Globalization; using System.IO; using System.Linq; using UnityEngine; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// Trees probe p2: runs the world's tree planting rule (EtsVegetationBuilder: bands, counts, the five draws per plant) over
// EVERY road side and terrain item of the map and tests each plant against EtsRoadClearance's footprints.
// args: [out.txt] [filter: x0,z0,x1,z1 game coords to list every hit in]
static class P {
  static CultureInfo ic = CultureInfo.InvariantCulture;
  static EtsRoadClearance.Defs defs; static Win win;
  static Dictionary<long, EtsRoadClearance.SectorShapes> shapes = new Dictionary<long, EtsRoadClearance.SectorShapes>();
  static LinkedList<long> lru = new LinkedList<long>(); static Dictionary<ulong,string> looks = new Dictionary<ulong,string>(); static Dictionary<string,int> allowHist = new Dictionary<string,int>(); static Dictionary<long,(int n,string what)> hwCells = new Dictionary<long,(int,string)>();
  static EtsRoadClearance.SectorShapes Shapes(int x,int z){ long k=Win.K(x,z); if(shapes.TryGetValue(k, out var s)) return s; var sec=win.Get(x,z); s = sec==null? null : EtsRoadClearance.Compute(defs, sec); if(sec!=null) foreach(var r in sec.Roads){ var lk=win.Map.RoadLook(r.LookToken); looks[r.Uid]=r.Look+" \""+(lk!=null?lk.GetString("name",""):"?")+"\""; } shapes[k]=s; lru.AddFirst(k); while(lru.Count>40){ shapes.Remove(lru.Last.Value); lru.RemoveLast(); } return s; }
  static Dictionary<ulong,(List<string> models,int total)> sets = new Dictionary<ulong,(List<string>,int)>();
  static (List<string>,int) Set(ulong tok){ if(sets.TryGetValue(tok, out var v)) return v; var u=win.Map.Vegetation(tok); List<string> m=null; int tot=0; if(u!=null){ m=u.GetArray("models"); if(m.Count==0) m=u.GetArray("model"); var w=u.GetArray("occurrence"); for(int i=0;i<m.Count;i++){ int wt=1; if(i<w.Count && int.TryParse(w[i], out int p) && p>0) wt=p; tot+=wt; } } sets[tok]=(m,tot); return (m,tot); }

  static long plants, in2d, own2d, other2d, pre2d, spot2d, oldCatch, oldMiss, newBlocked, windowAllowed, sides, allowBelow, allowAbove;
  static Dictionary<long,(int n,int miss,string what)> cells = new Dictionary<long,(int,int,string)>();
  static List<string> allowedList = new List<string>();
  static float fx0=float.NaN, fz0, fx1, fz1; static List<string> filtered = new List<string>();

  static bool TestAll(float x,float y,float z, float below, float above, out EtsRoadClearance.Hit hit){
    float b0=EtsRoadClearance.ClearBelow, a0=EtsRoadClearance.ClearAbove; EtsRoadClearance.ClearBelow=below; EtsRoadClearance.ClearAbove=above;
    try{ float gx=x, gz=-z; EtsMapSector.SectorOf(gx-500,gz-500,out int x0,out int z0); EtsMapSector.SectorOf(gx+500,gz+500,out int x1,out int z1);
      for(int sx=x0;sx<=x1;sx++) for(int sz=z0;sz<=z1;sz++){ var s=Shapes(sx,sz); if(s!=null && EtsRoadClearance.Blocked(s,x,y,z,EtsRoadClearance.TrunkMargin,0,out hit)) return true; }
      hit=default; return false; } finally { EtsRoadClearance.ClearBelow=b0; EtsRoadClearance.ClearAbove=a0; } }

  static string KindName(byte k)=> k==1?"road":k==2?"prefab lane":"prefab spot";

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
  // Foot height at (s, lateral) by index-space bilinear interpolation of the grid (the ray hits that quad).
  static float Foot(Vector3[] g, List<float> lat, int rows, int cols, float s, float lateral, float fallback){
    if(g==null) return fallback; float fj=s*cols; int j=Math.Min(cols-1,(int)fj); float tj=fj-j; int i=0; while(i<rows-1 && lateral>=lat[i+1]) i++; float ti=Mathf.Clamp01((lateral-lat[i])/Math.Max(1e-3f,lat[i+1]-lat[i])); int st=cols+1;
    float a=Mathf.Lerp(g[i*st+j].y,g[i*st+j+1].y,tj), b=Mathf.Lerp(g[(i+1)*st+j].y,g[(i+1)*st+j+1].y,tj); return Mathf.Lerp(a,b,ti); }

  static void Side(EtsTerrainSide side, bool left, float startOffset, float length, uint seed, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, ulong ownUid, string what, int defaultCols, Vector3 origin, float lift){
    if(side==null || side.Size<=0.5f || side.Vegetation==null || length<5f) return;
    bool any=false; foreach(var band in side.Vegetation) any |= band!=null && band.Token!=0 && band.Density>0f; if(!any) return;
    sides++;
    var grid = Grid(side,left,startOffset,defaultCols,p0,t0,p1,t1,origin,lift,out var gl,out int grows,out int gcols);
    var rnd = new System.Random((int)(seed ^ (left ? 0x9E3779B9u : 0x7F4A7C15u))); float sign = left?-1f:1f;
    foreach(var band in side.Vegetation){
      if(band==null||band.Token==0||band.Density<=0f) continue;
      float from=Math.Max(0f,band.From), to=Math.Min(side.Size, band.To); if(to-from<1f) continue;
      var (models,total)=Set(band.Token); if(models==null||models.Count==0) continue;
      int count=Math.Min(5000, Mathf.RoundToInt(length*(to-from)/10000f*band.Density));
      for(int k=0;k<count;k++){
        float s=(float)rnd.NextDouble(); float lateral=from+(float)rnd.NextDouble()*(to-from); rnd.Next(total); rnd.NextDouble(); rnd.NextDouble();
        Vector3 pt=Hermite(p0,t0,p1,t1,s), fw=Tangent(p0,t0,p1,t1,s); fw.y=0; if(fw.sqrMagnitude<1e-6f) continue;
        Vector3 right=Vector3.Cross(Vector3.up, fw.normalized)*sign; Vector3 pos=pt+right*(startOffset+lateral);
        pos.y = Foot(grid, gl, grows, gcols, s, lateral, pos.y);
        plants++;
        if(!TestAll(pos.x,pos.y,pos.z,1e9f,1e9f,out var h2)) continue;
        in2d++; bool own = h2.Uid==ownUid; if(own) own2d++; else if(h2.Kind==1) other2d++; else if(h2.Kind==2) pre2d++; else spot2d++;
        bool catchable = TestAll(pos.x,pos.y,pos.z,0.25f,3f,out _);
        if(catchable) oldCatch++; else oldMiss++;
        bool blocked = TestAll(pos.x,pos.y,pos.z,EtsRoadClearance.ClearBelow,EtsRoadClearance.ClearAbove,out var h3);
        if(blocked && h3.Kind==1 && h3.Uid!=ownUid && looks.TryGetValue(h3.Uid, out var lk3) && lk3.Contains(" hw")){ long hc=Win.K((int)Math.Floor(pos.x/100),(int)Math.Floor(-pos.z/100)); hwCells.TryGetValue(hc,out var q); q.n++; if(q.what==null) q.what=$"{what} -> {lk3} {h3.Uid:x} rise {h3.SurfaceY-pos.y:0.0} at {pos.x:0},{-pos.z:0} y {pos.y:0.0}"; hwCells[hc]=q; }
        if(blocked) newBlocked++; else { windowAllowed++; if(h2.SurfaceY-pos.y<0) allowBelow++; else allowAbove++; { string lk0; looks.TryGetValue(h2.Uid, out lk0); string key0=(h2.SurfaceY-pos.y<0?"below ":"above ")+(h2.Kind==1?lk0:KindName(h2.Kind)); allowHist.TryGetValue(key0, out int hc0); allowHist[key0]=hc0+1; } if(allowedList.Count<4000) allowedList.Add($"{pos.x:0.0},{-pos.z:0.0} y {pos.y:0.0} surface {h2.SurfaceY:0.0} rise {h2.SurfaceY-pos.y:0.0} by {KindName(h2.Kind)} {h2.Uid:x} from {what}"); }
        long cell = Win.K((int)Math.Floor(pos.x/100), (int)Math.Floor(-pos.z/100));
        cells.TryGetValue(cell, out var c); c.n++; if(!catchable) c.miss++; if(c.what==null) c.what=$"{what} -> {(own?"OWN ":"")}{KindName(h2.Kind)} {h2.Uid:x} rise {h2.SurfaceY-pos.y:0.0}"; cells[cell]=c;
        if(!float.IsNaN(fx0) && pos.x>=fx0 && pos.x<=fx1 && -pos.z>=fz0 && -pos.z<=fz1 && filtered.Count<3000) filtered.Add($"{pos.x:0.0} {-pos.z:0.0} y {pos.y:0.0} rise {h2.SurfaceY-pos.y:0.0} {(catchable?"old-catchable":"OLD-MISSES")} {what} -> {(own?"OWN ":"")}{KindName(h2.Kind)} {h2.Uid:x}");
      }
    }
  }
  static Vector3 Hermite(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float s){ float s2=s*s, s3=s2*s; return (2f*s3-3f*s2+1f)*p0+(s3-2f*s2+s)*t0+(-2f*s3+3f*s2)*p1+(s3-s2)*t1; }
  static Vector3 Tangent(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float s){ float s2=s*s; return (6f*s2-6f*s)*p0+(3f*s2-4f*s+1f)*t0+(-6f*s2+6f*s)*p1+(3f*s2-2f*s)*t1; }
  static Vector3 U(float[] g)=> new Vector3(g[0],g[1],-g[2]);
  static Vector3 D(float[] g)=> new Vector3(g[0],g[1],-g[2]).normalized;

  static void Main(string[] a){
    string outPath = a.Length>0? a[0] : "p2.txt";
    if(a.Length>1){ var f=a[1].Split(',').Select(v=>float.Parse(v,ic)).ToArray(); fx0=f[0]; fz0=f[1]; fx1=f[2]; fz1=f[3]; }
    bool only = a.Length>2 && a[2]=="only";
    win = new Win(16); defs = new EtsRoadClearance.Defs(win.Fs);
    var all = win.All().OrderBy(p=>p.x).ThenBy(p=>p.z).ToList();
    if(only){ EtsMapSector.SectorOf(fx0-600,fz0-600,out int ax,out int az); EtsMapSector.SectorOf(fx1+600,fz1+600,out int bx,out int bz); all=all.Where(p=>p.x>=ax&&p.x<=bx&&p.z>=az&&p.z<=bz).ToList(); }
    var clock=System.Diagnostics.Stopwatch.StartNew(); int done=0; long segs=0, roadsC=0, prefC=0, skipC=0;
    foreach(var (x,z) in all){
      var sec = win.Get(x,z); if(sec==null) continue;
      var sh = Shapes(x,z); if(sh!=null){ segs+=sh.Count; roadsC+=sh.Roads; prefC+=sh.Prefabs; skipC+=sh.Skipped; }
      foreach(var road in sec.Roads){
        if(!sec.NodesByUid.TryGetValue(road.StartNode, out var st) || !sec.NodesByUid.TryGetValue(road.EndNode, out var en)) continue;
        float[] w = defs.LookWidths(road.LookToken); float hr=w[0], hl=w[1];
        if(w[2]<0.5f && road.IsCityRoad){ hr+=road.Right.SidewalkSize; hl+=road.Left.SidewalkSize; }
        Vector3 p0=U(st.Position), p1=U(en.Position), t0=D(st.Forward())*road.Length, t1=D(en.Forward())*road.Length;
        int rc = Math.Max(1,(int)Math.Ceiling(road.Length/15f))*(road.Superfine?15:road.HighPoly?3:1); float lift = (w[2]<0.5f && road.IsCityRoad) ? 0.2f : 0f;
        Side(road.Right,false,hr,road.Length,road.Seed,p0,t0,p1,t1,road.Uid,"road "+road.Uid.ToString("x")+" R "+road.Look,rc,p0,lift);
        Side(road.Left,true,hl,road.Length,road.Seed,p0,t0,p1,t1,road.Uid,"road "+road.Uid.ToString("x")+" L "+road.Look,rc,p0,lift);
      }
      foreach(var item in sec.Terrains){
        if(!sec.NodesByUid.TryGetValue(item.StartNode, out var st) || !sec.NodesByUid.TryGetValue(item.EndNode, out var en)) continue;
        var sp=new[]{st.Position[0]+item.StartOffset[0],st.Position[1]+item.StartOffset[1],st.Position[2]+item.StartOffset[2]};
        var ep=new[]{en.Position[0]+item.EndOffset[0],en.Position[1]+item.EndOffset[1],en.Position[2]+item.EndOffset[2]};
        float len=Math.Max(0.5f,item.Length); Vector3 p0=U(sp), p1=U(ep), t0=D(st.Forward())*len, t1=D(en.Forward())*len;
        int tc = Math.Max(1,(int)Math.Ceiling(len/item.ColumnStep)); Vector3 no=U(st.Position);
        Side(item.Right,false,0f,len,item.Seed,p0,t0,p1,t1,item.Uid,"terrain "+item.Uid.ToString("x")+" R",tc,no,0f);
        Side(item.Left,true,0f,len,item.Seed,p0,t0,p1,t1,item.Uid,"terrain "+item.Uid.ToString("x")+" L",tc,no,0f);
      }
      done++; if(done%20==0) Console.Error.WriteLine($"{done}/{all.Count} sectors, {plants} plants, {in2d} in a footprint, {clock.Elapsed.TotalSeconds:0}s");
    }
    using(var o=new StreamWriter(outPath)){
      o.WriteLine($"sectors {done}, road+terrain sides with trees {sides}, plants {plants}");
      o.WriteLine($"footprints: {segs} segments from {roadsC} roads + {prefC} prefabs ({skipC} skipped)");
      o.WriteLine($"BEFORE (no map rule): plants standing inside a road/prefab footprint (2D, any height): {in2d}  = own road {own2d}, another road {other2d}, prefab lane {pre2d}, prefab spot {spot2d}");
      o.WriteLine($"   of which the old hard-surface ray could catch (surface -0.25..+3 m of the foot, IF that road was built before the side planted): {oldCatch}; never caught even fully streamed: {oldMiss}");
      o.WriteLine($"AFTER (EtsRoadClearance, window -{EtsRoadClearance.ClearBelow}..+{EtsRoadClearance.ClearAbove} m): kept out {newBlocked}; left standing inside a footprint: {windowAllowed} = {allowBelow} with the road >= {EtsRoadClearance.ClearBelow} m BELOW the foot (tunnel / ground high over a cutting) + {allowAbove} with the deck >= {EtsRoadClearance.ClearAbove} m ABOVE it (tall viaduct)");
      o.WriteLine("\nWORST 100 m cells (plants in a footprint; 'miss' = not catchable by the old ray):");
      foreach(var kv in cells.OrderByDescending(k=>k.Value.miss).ThenByDescending(k=>k.Value.n).Take(40)){ int cx=(int)(kv.Key>>32), cz=(int)(uint)kv.Key; o.WriteLine($"  game ({cx*100+50}, {cz*100+50})  {kv.Value.n} plants, miss {kv.Value.miss}: {kv.Value.what}"); }
      o.WriteLine("\nMOTORWAY cells (plants on a motorway carriageway of ANOTHER item, kept out by the new rule):"); foreach(var kv in hwCells.OrderByDescending(k=>k.Value.n).Take(40)){ int cx=(int)(kv.Key>>32), cz=(int)(uint)kv.Key; o.WriteLine($"  game ({cx*100+50}, {cz*100+50})  {kv.Value.n}: {kv.Value.what}"); }
      o.WriteLine("\nLeft standing by the height window, by what is below/above:"); foreach(var kv in allowHist.OrderByDescending(k=>k.Value).Take(30)) o.WriteLine($"  {kv.Value,7}  {kv.Key}");
      o.WriteLine("\nLeft standing inside a footprint by the height window (first 200):"); foreach(var l in allowedList.Take(200)) o.WriteLine("  "+l);
      if(filtered.Count>0){ o.WriteLine("\nFILTER area hits:"); foreach(var l in filtered) o.WriteLine("  "+l); }
    }
    Console.Error.WriteLine("done "+clock.Elapsed.TotalSeconds.ToString("0")+" s");
  }
}
