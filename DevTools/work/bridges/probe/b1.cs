using System; using System.Collections.Generic; using System.Globalization; using System.IO; using System.Linq; using UnityEngine; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// Bridges probe b1 (2026-10-27, "bridges in the road"): every solid the world builds from the map - road side models
// (under each placement rule), map model items, prefab models - sampled on its upright faces and tested against the
// DRIVABLE footprint of every road (lane bands only: carriageways, no median, no shoulder edge models) and prefab lane
// (nav curve +- 2 m). A sample counts when it stands >= 0.3 m inside a lane band and 0.5..4.5 m above that lane's surface.
// args: out.txt [only=side|model|prefab|all] [box=gx0,gz0,gx1,gz1]
static class B1 {
  static CultureInfo ic = CultureInfo.InvariantCulture;
  static Win win; static EtsFileSystem fs;
  const float Inset = 0.3f, Low = 0.5f, High = 4.5f, LaneHalf = 2.0f, Spacing = 0.7f;

  // ---------------------------------------------------------------- drivable footprints (U-frame: x, y, -gz)
  class Fp { public List<float> ax=new List<float>(),ay=new List<float>(),az=new List<float>(),bx=new List<float>(),by=new List<float>(),bz=new List<float>(),i0=new List<float>(),o0=new List<float>(),i1=new List<float>(),o1=new List<float>(); public List<ulong> uid=new List<ulong>(); public List<byte> kind=new List<byte>();
    public float minX=float.MaxValue,minZ=float.MaxValue,maxX=float.MinValue,maxZ=float.MinValue; public int W,H; public List<int>[] cells; }
  static Dictionary<long,Fp> fps=new Dictionary<long,Fp>(); static LinkedList<long> fpl=new LinkedList<long>();
  static Dictionary<ulong,float[]> lookBands=new Dictionary<ulong,float[]>(); static Dictionary<ulong,string> LookNames=new Dictionary<ulong,string>(); static Dictionary<ulong,bool> skipLook=new Dictionary<ulong,bool>(); public static bool NoSkip;
  // Not a lane a truck drives: a railway, an invisible AI road (the look name says so).
  static bool Skip(ulong tok){ if(NoSkip) return false; if(skipLook.TryGetValue(tok,out var b)) return b; var lk=win.Map.RoadLook(tok); string n=(lk!=null?lk.GetString("name",""):"").ToLowerInvariant(); b=n.Contains("invis")||n.Contains("rail")||n.Contains("train")||n.Contains("metro"); skipLook[tok]=b; return b; }
  static Dictionary<string,float[]> boundsCache=new Dictionary<string,float[]>();
  static float[] Bounds(string pmd){ if(string.IsNullOrEmpty(pmd)) return null; string k="/"+EtsHashFs.Normalize(pmd); if(boundsCache.TryGetValue(k,out var c)) return c; string pmg=k.EndsWith(".pmd")?k.Substring(0,k.Length-4)+".pmg":k+".pmg"; var b=fs.Read(pmg); float[] r=null; if(b!=null&&b.Length>=72){ r=new float[6]; for(int i=0;i<6;i++) r[i]=BitConverter.ToSingle(b,48+i*4);} boundsCache[k]=r; return r; }
  // lateral lane bands [r_in, r_out, l_in, l_out] as signed laterals: right band [a,b], left band [c,d] (c<d); a single band when undivided.
  static float[] Bands(ulong token){ if(lookBands.TryGetValue(token,out var c)) return c; var look=win.Map.RoadLook(token); float[] r=null;
    if(look!=null && look.Has("template_right")){ var rt=Bounds(look.GetString("template_right")); if(rt!=null){ float off=look.GetFloat("road_offset",0f); bool div=off>0.01f||look.Has("template_left");
        if(!div) r=new[]{ rt[0], rt[3], 0f, 0f }; else { var lt=look.Has("template_left")?Bounds(look.GetString("template_left")):rt; if(lt==null) lt=rt; r=new[]{ off+rt[0], off+rt[3], -(off+lt[3]), -(off+lt[0]) }; } } }
    if(r==null){ float off=look!=null?look.GetFloat("road_offset",0f):0f; float rs=look!=null?look.GetFloat("road_size_right",4.5f):4.5f, ls=look!=null?look.GetFloat("road_size_left",4.5f):4.5f;
      r = off<0.01f ? new[]{ -ls, rs, 0f, 0f } : new[]{ off, off+rs, -(off+ls), -off }; }
    lookBands[token]=r; return r; }
  static void AddSeg(Fp f, Vector3 a, Vector3 b, float i0,float o0,float i1,float o1, ulong uid, byte kind){ f.ax.Add(a.x);f.ay.Add(a.y);f.az.Add(a.z);f.bx.Add(b.x);f.by.Add(b.y);f.bz.Add(b.z);f.i0.Add(i0);f.o0.Add(o0);f.i1.Add(i1);f.o1.Add(o1);f.uid.Add(uid);f.kind.Add(kind); }
  static Vector3 H(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float s){ float s2=s*s, s3=s2*s; return (2f*s3-3f*s2+1f)*p0+(s3-2f*s2+s)*t0+(-2f*s3+3f*s2)*p1+(s3-s2)*t1; }
  static Vector3 HT(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float s){ float s2=s*s; return (6f*s2-6f*s)*p0+(3f*s2-4f*s+1f)*t0+(-6f*s2+6f*s)*p1+(3f*s2-2f*s)*t1; }
  static Vector3 U(float[] g)=>new Vector3(g[0],g[1],-g[2]);
  static Vector3 UD(float[] g)=>new Vector3(g[0],g[1],-g[2]).normalized;
  static void RoadCurve(EtsMapSector s, EtsMapRoad r, out Vector3 p0, out Vector3 t0, out Vector3 p1, out Vector3 t1){ var st=s.NodesByUid[r.StartNode]; var en=s.NodesByUid[r.EndNode]; p0=U(st.Position); p1=U(en.Position); t0=UD(st.Forward())*r.Length; t1=UD(en.Forward())*r.Length; }
  static Fp Foot(int sx,int sz){ long k=Win.K(sx,sz); if(fps.TryGetValue(k,out var f)) return f; var s=win.Get(sx,sz); f=null;
    if(s!=null){ f=new Fp();
      foreach(var r in s.Roads){ if(!s.NodesByUid.ContainsKey(r.StartNode)||!s.NodesByUid.ContainsKey(r.EndNode)) continue; RoadCurve(s,r,out var p0,out var t0,out var p1,out var t1); var bd=Bands(r.LookToken); if(!LookNames.ContainsKey(r.Uid)){ var lkx=win.Map.RoadLook(r.LookToken); LookNames[r.Uid]=r.Look+" \""+(lkx!=null?lkx.GetString("name",""):"?")+"\""; } if(Skip(r.LookToken)) continue;
        int n=Math.Max(1,Math.Min(128,(int)Math.Ceiling(Math.Max(1f,r.Length)/6f))); Vector3 l=p0; for(int i=1;i<=n;i++){ var p=H(p0,t0,p1,t1,i/(float)n); AddSeg(f,l,p,bd[0],bd[1],bd[2],bd[3],r.Uid,1); l=p; } }
      foreach(var pf in s.Prefabs){ if(!Frame(s,pf,out var pos,out var rot,out var ppd)) continue; var nav=Nav(pf.PrefabToken); if(nav==null) continue;
        foreach(var pts in nav){ Vector3 l=default; for(int i=0;i*3+2<pts.Length;i++){ var p=pos+rot*new Vector3(pts[i*3],pts[i*3+1],-pts[i*3+2]); if(i>0) AddSeg(f,l,p,-LaneHalf,LaneHalf,0f,0f,pf.Uid,2); l=p; } } }
      int cnt=f.ax.Count; for(int i=0;i<cnt;i++){ float rr=Math.Max(Math.Abs(f.i0[i]),Math.Max(Math.Abs(f.o0[i]),Math.Max(Math.Abs(f.i1[i]),Math.Abs(f.o1[i]))))+1f; f.minX=Math.Min(f.minX,Math.Min(f.ax[i],f.bx[i])-rr); f.maxX=Math.Max(f.maxX,Math.Max(f.ax[i],f.bx[i])+rr); f.minZ=Math.Min(f.minZ,Math.Min(f.az[i],f.bz[i])-rr); f.maxZ=Math.Max(f.maxZ,Math.Max(f.az[i],f.bz[i])+rr); }
      if(cnt>0){ f.W=Math.Max(1,(int)Math.Ceiling((f.maxX-f.minX)/32f)); f.H=Math.Max(1,(int)Math.Ceiling((f.maxZ-f.minZ)/32f)); f.cells=new List<int>[f.W*f.H];
        for(int i=0;i<cnt;i++){ float rr=Math.Max(Math.Abs(f.i0[i]),Math.Max(Math.Abs(f.o0[i]),Math.Max(Math.Abs(f.i1[i]),Math.Abs(f.o1[i]))))+1f; int x0=Cl((int)((Math.Min(f.ax[i],f.bx[i])-rr-f.minX)/32f),f.W),x1=Cl((int)((Math.Max(f.ax[i],f.bx[i])+rr-f.minX)/32f),f.W),z0=Cl((int)((Math.Min(f.az[i],f.bz[i])-rr-f.minZ)/32f),f.H),z1=Cl((int)((Math.Max(f.az[i],f.bz[i])+rr-f.minZ)/32f),f.H);
          for(int gz=z0;gz<=z1;gz++) for(int gx=x0;gx<=x1;gx++){ int c=gz*f.W+gx; if(f.cells[c]==null) f.cells[c]=new List<int>(); f.cells[c].Add(i);} } } }
    fps[k]=f; fpl.AddFirst(k); while(fpl.Count>36){ fps.Remove(fpl.Last.Value); fpl.RemoveLast(); } return f; }
  static int Cl(int v,int n)=> v<0?0:v>=n?n-1:v;
  struct Hit { public ulong uid; public byte kind; public float rise, depth; }
  // Whether a U-frame point stands inside a lane band (>= Inset in) at 0.5..4.5 m above the lane; skip = a footprint uid not to test.
  static bool Test(List<Fp> near, float x, float y, float z, ulong skip, out Hit hit){ hit=default;
    foreach(var f in near){ if(f==null||f.cells==null||x<f.minX||x>f.maxX||z<f.minZ||z>f.maxZ) continue; var cell=f.cells[Cl((int)((z-f.minZ)/32f),f.H)*f.W+Cl((int)((x-f.minX)/32f),f.W)]; if(cell==null) continue;
      foreach(int i in cell){ if(skip!=0&&f.uid[i]==skip) continue; float ax=f.ax[i],az=f.az[i],dx=f.bx[i]-ax,dz=f.bz[i]-az,len=(float)Math.Sqrt(dx*dx+dz*dz); if(len<1e-3f) continue; dx/=len;dz/=len; float px=x-ax,pz=z-az,along=px*dx+pz*dz; if(along<0f||along>len) continue;
        float lat=px*dz-pz*dx; float depth=-1f; if(lat>=f.i0[i]&&lat<=f.o0[i]) depth=Math.Min(lat-f.i0[i],f.o0[i]-lat); if(f.o1[i]>f.i1[i]&&lat>=f.i1[i]&&lat<=f.o1[i]) depth=Math.Max(depth,Math.Min(lat-f.i1[i],f.o1[i]-lat));
        if(depth<Inset) continue; float t=along/len, sy=f.ay[i]+(f.by[i]-f.ay[i])*t, rise=y-sy; if(rise<Low||rise>High) continue; hit.uid=f.uid[i]; hit.kind=f.kind[i]; hit.rise=rise; hit.depth=depth; return true; } }
    return false; }

  // ---------------------------------------------------------------- prefabs: frame (EtsMapBuilder.TryPrefabFrame, re-done) and nav
  static Dictionary<ulong,EtsPrefabDescriptor> ppds=new Dictionary<ulong,EtsPrefabDescriptor>(); static Dictionary<ulong,List<float[]>> navs=new Dictionary<ulong,List<float[]>>();
  static EtsPrefabDescriptor Ppd(ulong tok){ if(ppds.TryGetValue(tok,out var p)) return p; var def=win.Map.PrefabDefinition(tok); var path=def?.GetString("prefab_desc",null); var b=string.IsNullOrEmpty(path)?null:fs.Read(path); try{ p=b==null?null:EtsPrefabDescriptor.Parse(b);}catch{p=null;} ppds[tok]=p;
    List<float[]> nl=null; if(b!=null){ try{ var nav=EtsPrefabNav.Parse(b,out _); if(nav!=null){ nl=new List<float[]>(); foreach(var c in nav.Curves) nl.Add(Curve(c)); } }catch{} } navs[tok]=nl; return p; }
  static List<float[]> Nav(ulong tok){ Ppd(tok); return navs[tok]; }
  static float[] Curve(EtsPrefabNav.Curve c){ float dx=c.End[0]-c.Start[0],dy=c.End[1]-c.Start[1],dz=c.End[2]-c.Start[2]; float ch=(float)Math.Sqrt(dx*dx+dy*dy+dz*dz); int n=Math.Max(1,Math.Min(24,(int)Math.Ceiling(ch/6f))); var pts=new float[(n+1)*3];
    for(int i=0;i<=n;i++){ float u=i/(float)n,u2=u*u,u3=u2*u,h00=2*u3-3*u2+1,h10=u3-2*u2+u,h01=-2*u3+3*u2,h11=u3-u2; for(int k=0;k<3;k++) pts[i*3+k]=h00*c.Start[k]+h10*c.StartForward[k]*ch+h01*c.End[k]+h11*c.EndForward[k]*ch; } return pts; }
  static Quaternion QU(float[] q)=> new Quaternion(-q[0],-q[1],q[2],q[3]).normalized;
  static Quaternion Yaw(float r)=> new Quaternion(0f,Mathf.Sin(r*0.5f),0f,Mathf.Cos(r*0.5f));
  static Quaternion FrameOf(Vector3 d){ d.Normalize(); float yaw=Mathf.Atan2(d.x,d.z), pitch=-Mathf.Asin(Mathf.Clamp(d.y,-1f,1f)); return Yaw(yaw)*new Quaternion(Mathf.Sin(pitch*0.5f),0f,0f,Mathf.Cos(pitch*0.5f)); }
  static Quaternion Conj(Quaternion q)=> new Quaternion(-q.x,-q.y,-q.z,q.w);
  static EtsMapNode FindNode(EtsMapSector s, ulong uid){ if(s.NodesByUid.TryGetValue(uid,out var n)) return n; return win.Node(uid); }
  static float Miss(EtsMapSector s, EtsMapPrefab pf, EtsPrefabDescriptor ppd, int origin, Vector3 ap, Vector3 mp, Quaternion rot){ int n=ppd.Nodes.Length; float worst=0f;
    for(int k=1;k<n&&k<pf.NodeUids.Length;k++){ var node=FindNode(s,pf.NodeUids[k]); if(node==null) continue; var placed=mp+rot*(U(ppd.Nodes[(origin+k)%n].Position)-ap); var act=U(node.Position); float dx=placed.x-act.x,dz=placed.z-act.z; if(dx*dx+dz*dz>1f) return float.MaxValue; worst=Math.Max(worst,Math.Abs(placed.y-act.y)); } return worst; }
  static bool Frame(EtsMapSector s, EtsMapPrefab pf, out Vector3 pos, out Quaternion rot, out EtsPrefabDescriptor ppd){ pos=default; rot=Quaternion.identity; ppd=Ppd(pf.PrefabToken); if(ppd==null||ppd.Nodes.Length==0||pf.NodeUids.Length==0) return false; var mn=FindNode(s,pf.NodeUids[0]); if(mn==null) return false;
    int origin=Mathf.Clamp(pf.Origin,0,ppd.Nodes.Length-1); var an=ppd.Nodes[origin]; var ap=U(an.Position); var ad=new Vector3(an.Direction[0],an.Direction[1],-an.Direction[2]); var mp=U(mn.Position); var mr=QU(mn.Rotation); var mf=mr*Vector3.forward;
    var fa=new Vector3(ad.x,0,ad.z); var ff=new Vector3(mf.x,0,mf.z); if(fa.sqrMagnitude<1e-8f||ff.sqrMagnitude<1e-8f) return false; float yaw=Vector3.SignedAngle(fa.normalized,ff.normalized,Vector3.up); var yo=Yaw(yaw*Mathf.Deg2Rad);
    var full=mr*Conj(FrameOf(ad)); float fm=Miss(s,pf,ppd,origin,ap,mp,full); rot = (fm<=0.25f||fm<=Miss(s,pf,ppd,origin,ap,mp,yo)) ? full : yo; pos=mp-rot*ap; return true; }

  // ---------------------------------------------------------------- model samples (model frame -> Unity axes, upright faces only)
  static Dictionary<string,Vector3[]> samples=new Dictionary<string,Vector3[]>(); static Dictionary<string,bool> renderOnly=new Dictionary<string,bool>();
  static bool RenderOnly(string mat){ if(string.IsNullOrEmpty(mat)) return false; if(renderOnly.TryGetValue(mat,out var r)) return r; var t=fs.ReadText(mat); var d=t==null?null:EtsMaterialDef.Parse(t,mat);
    r = d!=null && d.Effect!=null && (d.EffectHas("shadowonly")||d.EffectHas("shadowmap")||d.EffectHas("fakeshadow")||d.EffectHas(".night")||d.EffectHas("mlaaweight")||d.EffectHas("decal")); renderOnly[mat]=r; return r; }
  static Vector3[] Samples(string pmd, string look, string[] variants, string[] parts){ string key=pmd+"|"+look+"|"+string.Join(",",variants)+"|"+(parts==null?"":string.Join(",",parts)); if(samples.TryGetValue(key,out var c)) return c;
    var list=new List<Vector3>(); try{ string k="/"+EtsHashFs.Normalize(pmd); string pmg=k.EndsWith(".pmd")?k.Substring(0,k.Length-4)+".pmg":k+".pmg"; var a=fs.Read(k); var b=fs.Read(pmg);
      if(a!=null&&b!=null){ var m=EtsModelData.Parse(a,b,k); int li=Math.Max(0,m.LookIndex(look)); var mats=li<m.LookMaterials.Length?m.LookMaterials[li]:new string[0]; var vs=new List<int>(); foreach(var v in variants){ int i=m.VariantIndex(v.Trim()); if(i>=0) vs.Add(i);} if(vs.Count==0&&m.Variants.Length>0) vs.Add(0);
        foreach(var part in m.Parts){ bool vis=vs.Count==0; foreach(int v in vs) vis|=part.IsVisible(v); if(parts!=null) foreach(var f in parts) vis|=f==part.Name; if(!vis||part.Name=="coll") continue;
          for(int p=part.PieceStart;p<part.PieceStart+part.PieceCount&&p<m.Pieces.Length;p++){ var pc=m.Pieces[p]; string mat=pc.MaterialIndex<mats.Length?mats[pc.MaterialIndex]:null; if(RenderOnly(mat)) continue; var P=pc.Positions; var I=pc.Indices;
            for(int t=0;t+2<I.Length;t+=3){ var v0=new Vector3(P[I[t]*3],P[I[t]*3+1],-P[I[t]*3+2]); var v1=new Vector3(P[I[t+1]*3],P[I[t+1]*3+1],-P[I[t+1]*3+2]); var v2=new Vector3(P[I[t+2]*3],P[I[t+2]*3+1],-P[I[t+2]*3+2]);
              var nrm=Vector3.Cross(v1-v0,v2-v0); float area=nrm.magnitude*0.5f; if(area<1e-4f) continue; if(Math.Abs(nrm.y/(2*area))>0.8f) continue; // upright faces only
              int n=Math.Max(1,Math.Min(30,(int)Math.Ceiling(Math.Sqrt(area)/Spacing)));
              for(int i=0;i<=n;i++) for(int j=0;j<=n-i;j++){ float u=i/(float)n, v=j/(float)n; list.Add(v0+(v1-v0)*u+(v2-v0)*v); } } } } } }catch(Exception){ }
    // thin out duplicates on a 0.35 m grid
    var seen=new HashSet<long>(); var outl=new List<Vector3>(); foreach(var p in list){ long h=((long)Mathf.RoundToInt(p.x/0.35f)*73856093L)^((long)Mathf.RoundToInt(p.y/0.35f)*19349663L)^((long)Mathf.RoundToInt(p.z/0.35f)*83492791L); if(seen.Add(h)) outl.Add(p); }
    c=outl.ToArray(); cached+=c.Length; if(cached>12000000){ samples.Clear(); cached=c.Length; } samples[key]=c; return c; }
  static long cached;

  // ---------------------------------------------------------------- railings (EtsMapBuilder.BuildRailing / BuildRailingCopy / BentRun, re-done)
  static int RailRules=6;
  class RS { public int center,start,end; public float sliceStart,len; }
  static RS RSet(EtsModelData m, string[] sizes){ foreach(var size in sizes){ int c=PartIdx(m,"center"+size); if(c<0) continue; var rs=new RS{center=c,start=PartIdx(m,"start"+size),end=PartIdx(m,"end"+size)}; float zMin=1e9f,zMax=-1e9f; var cp=m.Parts[c]; for(int p=cp.PieceStart;p<cp.PieceStart+cp.PieceCount&&p<m.Pieces.Length;p++){ zMin=Math.Min(zMin,m.Pieces[p].BoundsMin[2]); zMax=Math.Max(zMax,m.Pieces[p].BoundsMax[2]); } float nom=size=="15m"?15f:5f; if(zMax-zMin<0.5f){ zMin=-nom*0.5f; zMax=nom*0.5f; } rs.sliceStart=-zMax; rs.len=zMax-zMin; return rs; } return null; }
  static Dictionary<string,EtsModelData> models=new Dictionary<string,EtsModelData>();
  static EtsModelData Model(string pmd){ string k="/"+EtsHashFs.Normalize(pmd); if(models.TryGetValue(k,out var m)) return m; try{ var a=fs.Read(k); var b=fs.Read(k.Substring(0,k.Length-4)+".pmg"); m=a==null||b==null?null:EtsModelData.Parse(a,b,k);}catch{m=null;} models[k]=m; return m; }
  static int PartIdx(EtsModelData m,string n){ for(int i=0;i<m.Parts.Length;i++) if(m.Parts[i].Name==n) return i; return -1; }
  static Dictionary<string,Vector3[]> partSamples=new Dictionary<string,Vector3[]>();
  // Model-frame (game axes, z NOT flipped) samples of a part's drawn upright faces.
  static Vector3[] PartSamples(string path, EtsModelData m, int part, string look){ string key=path+"#"+part+"#"+look; if(partSamples.TryGetValue(key,out var c)) return c; var list=new List<Vector3>(); int li=Math.Max(0,m.LookIndex(look)); var mats=li<m.LookMaterials.Length?m.LookMaterials[li]:new string[0]; var pt=m.Parts[part];
    for(int p=pt.PieceStart;p<pt.PieceStart+pt.PieceCount&&p<m.Pieces.Length;p++){ var pc=m.Pieces[p]; string mat=pc.MaterialIndex<mats.Length?mats[pc.MaterialIndex]:null; if(mat==null||RenderOnly(mat)) continue; var P=pc.Positions; var I=pc.Indices;
      for(int t=0;t+2<I.Length;t+=3){ var v0=new Vector3(P[I[t]*3],P[I[t]*3+1],P[I[t]*3+2]); var v1=new Vector3(P[I[t+1]*3],P[I[t+1]*3+1],P[I[t+1]*3+2]); var v2=new Vector3(P[I[t+2]*3],P[I[t+2]*3+1],P[I[t+2]*3+2]); var nrm=Vector3.Cross(v1-v0,v2-v0); float area=nrm.magnitude*0.5f; if(area<1e-4f||Math.Abs(nrm.y/(2*area))>0.8f) continue;
        int n=Math.Max(1,Math.Min(30,(int)Math.Ceiling(Math.Sqrt(area)/Spacing))); for(int i=0;i<=n;i++) for(int j=0;j<=n-i;j++) list.Add(v0+(v1-v0)*(i/(float)n)+(v2-v0)*(j/(float)n)); } }
    c=list.ToArray(); partSamples[key]=c; return c; }
  static EtsMapRoad FindRoad(EtsMapSector s, ulong uid){ foreach(var r in s.Roads) if(r.Uid==uid) return r; foreach(var h in win.Held.Values){ if(h==null) continue; foreach(var r in h.Roads) if(r.Uid==uid) return r; } return null; }
  static bool Continues(EtsMapSector s, EtsMapRoad road, ulong nodeUid, ulong model, out string why){ var node=FindNode(s,nodeUid); if(node==null){ why="node not found"; return false; }
    ulong other=node.ForwardItem==road.Uid?node.BackwardItem:node.ForwardItem; if(other==0||other==road.Uid){ why=$"no item beyond (fwd {node.ForwardItem:x} bwd {node.BackwardItem:x})"; return false; }
    var next=FindRoad(s,other); if(next==null){ bool pf=s.Prefabs.Any(p=>p.Uid==other)||win.Held.Values.Any(h=>h!=null&&h.Prefabs.Any(p=>p.Uid==other)); why=pf?$"prefab {other:x} beyond":$"item {other:x} beyond not a held road"; return false; }
    if(next.Railings==null){ why=$"road {other:x} beyond has no railings"; return false; } foreach(var rr in next.Railings) if(rr.Model==model){ why=$"continues on road {other:x} ({(rr.Left?"L":"R")})"; return true; }
    why=$"road {other:x} beyond carries [{string.Join(",",next.Railings.Select(x=>x.Name+(x.Left?"L":"R")))}]"; return false; }
  // Cap rules: 0 now (beyond road lacks the same model), 1 beyond road has no railing on the same side (any model, sides
  // mapped through the beyond road's direction), 2 the same side and slot has no railing, 3 only where no road goes on (dead end or prefab),
  // 4 the same model anywhere on the beyond road OR a railing on the same side.
  static bool CapRule(int rule, EtsMapSector s, EtsMapRoad road, ulong nodeUid, EtsRoadRailing rl){ var node=FindNode(s,nodeUid); if(node==null) return true;
    ulong other=node.ForwardItem==road.Uid?node.BackwardItem:node.ForwardItem; if(other==0||other==road.Uid) return true; var next=FindRoad(s,other); if(next==null) return true;
    if(rule==3) return false; if(next.Railings==null) return true;
    bool sameDir = (nodeUid==road.StartNode) ? next.EndNode==nodeUid : next.StartNode==nodeUid; // the beyond road runs on in the same direction
    bool same=false, side=false, slot=false; foreach(var rr in next.Railings){ bool rrLeft = sameDir ? rr.Left : !rr.Left; if(rr.Model==rl.Model) same=true; if(rrLeft==rl.Left){ side=true; if(rr.Slot==rl.Slot) slot=true; } }
    switch(rule){ case 0: return !same; case 1: return !side; case 2: return !slot; default: return !(same||side); } }

  // ---------------------------------------------------------------- results
  class Res { public string cat, what; public ulong uid; public int hits; public float worstRise=99f, depth; public Vector3 at; public ulong into; public byte intoKind; public string extra; }
  static List<Res> results=new List<Res>(); static Dictionary<string,int> totals=new Dictionary<string,int>(); static Dictionary<string,int> instances=new Dictionary<string,int>();
  static void Count(string cat){ instances.TryGetValue(cat,out int n); instances[cat]=n+1; }
  static Res TestSolid(List<Fp> near, Vector3[] pts, Func<Vector3,Vector3> xf, ulong skip){ Res r=null; foreach(var lp in pts){ var p=xf(lp); if(Test(near,p.x,p.y,p.z,skip,out var h)){ if(r==null) r=new Res{at=p,into=h.uid,intoKind=h.kind}; r.hits++; if(h.depth>r.depth){ r.depth=h.depth; r.at=p; r.into=h.uid; r.intoKind=h.kind; } r.worstRise=Math.Min(r.worstRise,h.rise);} } return r; }
  static void Report(Res r, string cat, string what, ulong uid, string extra){ if(r==null||r.hits<2) return; r.cat=cat; r.what=what; r.uid=uid; r.extra=extra; results.Add(r); totals.TryGetValue(cat,out int n); totals[cat]=n+1; }

  static void Main(string[] a){
    string outPath=a.Length>0?a[0]:"b1.txt"; string only=a.Length>1?a[1]:"all"; float[] box=null; if(a.Length>2) box=a[2].Split(',').Select(v=>float.Parse(v,ic)).ToArray();
    win=new Win(30); fs=win.Fs; var clock=System.Diagnostics.Stopwatch.StartNew();
    var all=win.All().OrderBy(p=>p.x).ThenBy(p=>p.z).ToList(); int done=0;
    foreach(var (sx,sz) in all){ done++; var s=win.Get(sx,sz); if(s==null) continue;
      if(box!=null){ float x0=sx*EtsMapSector.SectorSize, z0=sz*EtsMapSector.SectorSize; if(x0>box[2]||x0+EtsMapSector.SectorSize<box[0]||z0>box[3]||z0+EtsMapSector.SectorSize<box[1]) continue; }
      var near=new List<Fp>(); for(int x=sx-1;x<=sx+1;x++) for(int z=sz-1;z<=sz+1;z++) near.Add(Foot(x,z)); s=win.Get(sx,sz);
      bool inBox(Vector3 p)=> box==null || (p.x>=box[0]&&p.x<=box[2]&&-p.z>=box[1]&&-p.z<=box[3]);
      if(only=="all"||only=="side"){
        foreach(var r in s.Roads){ if(r.SideModels==null||r.Length<1f||!s.NodesByUid.ContainsKey(r.StartNode)||!s.NodesByUid.ContainsKey(r.EndNode)) continue; RoadCurve(s,r,out var p0,out var t0,out var p1,out var t1);
          foreach(var slot in r.SideModels){ if(slot.Model==0||slot.Distance<2f) continue; var u=win.Map.SignModel(slot.Model); var path=u?.GetString("model_desc",null); if(string.IsNullOrEmpty(path)) continue; if(u.GetBool("dynamic",false)) continue;
            var bb=Bounds(path); if(bb==null||bb[4]-bb[1]<0.5f) continue; var pts=Samples(path,u.GetString("look_name","default").Trim(),new[]{"default"},null);
            bool flipped=r.SideModelFlipped(slot.Left,slot.Slot), shifted=r.SideModelShifted(slot.Left,slot.Slot); float lateral=slot.Left?-slot.Offset:slot.Offset; float start=shifted?slot.Distance*0.5f:0f;
            for(int rule=0;rule<4;rule++){ string cat=rule==0?"side(now: left from end)":rule==1?"side(old: all from start)":rule==2?"side(chain: previous_length phase, both sides)":"side(chain, left from end)"; if((rule==1||rule==3)&&!slot.Left) continue; int placed=0;
              float d0=start; if(rule>=2){ d0=(start-r.PreviousLength)%slot.Distance; if(d0<0) d0+=slot.Distance; }
              for(float d=d0; d<=r.Length-0.5f && placed<256; d+=slot.Distance, placed++){ float sp = rule==0 ? (slot.Left?1f-d/r.Length:d/r.Length) : rule==3 ? 1f-d/r.Length : d/r.Length;
                var fw=HT(p0,t0,p1,t1,sp).normalized; var rt=Vector3.Cross(Vector3.up,fw).normalized; var pos=H(p0,t0,p1,t1,sp)+rt*lateral; if(!inBox(pos)) continue; var look=(slot.Left!=flipped)?-fw:fw; var q=FrameOf(look);
                Count(cat); var res=TestSolid(near,pts,lp=>pos+q*lp,0); Report(res,cat,$"sign.{slot.Name} {path} {(slot.Left?"L":"R")}{slot.Slot} off {slot.Offset} every {slot.Distance} {(shifted?"shifted ":"")}{(flipped?"flipped ":"")}d {d:0.0} of {r.Length:0.0} look {r.Look}",r.Uid,null); } } } } }
      if(only=="all"||only=="model"){
        foreach(var m in s.Models){ var def=win.Map.ModelDefinition(m.ModelToken); if(def==null||def.Has("vegetation_model")) continue; var path=def.GetString("model_desc",null); if(string.IsNullOrEmpty(path)) continue; var node=FindNode(s,m.NodeUid); if(node==null) continue;
          var pos=U(node.Position); if(!inBox(pos)) continue; var q=QU(node.Rotation); var sc=new Vector3(m.Scale[0],m.Scale[1],m.Scale[2]);
          var parts=m.AdditionalParts.Select(t=>EtsToken.Decode(t)).ToArray(); var pts=Samples(path,EtsToken.Decode(m.LookToken),new[]{EtsToken.Decode(m.VariantToken),"default"},parts.Length>0?parts:null);
          Count("model"); var res=TestSolid(near,pts,lp=>pos+q*Vector3.Scale(lp,sc),0); Report(res,"model",$"model.{m.Name} {path} scale {sc.x:0.##},{sc.y:0.##},{sc.z:0.##}",m.Uid,null); } }
      if(only=="all"||only=="prefab"){
        foreach(var pf in s.Prefabs){ if(!Frame(s,pf,out var pos,out var rot,out var ppd)) continue; if(!inBox(pos)) continue; var def=win.Map.PrefabDefinition(pf.PrefabToken); var path=def?.GetString("model_desc",null); if(string.IsNullOrEmpty(path)) continue;
          var parts=pf.AdditionalParts.Select(t=>EtsToken.Decode(t)).ToArray(); var pts=Samples(path,"default",new[]{EtsToken.Decode(pf.VariantToken),"default"},parts);
          Count("prefab"); var res=TestSolid(near,pts,lp=>pos+rot*lp,pf.Uid); Report(res,"prefab",$"prefab.{EtsToken.Decode(pf.PrefabToken)} {path} variant {EtsToken.Decode(pf.VariantToken)} origin {pf.Origin}",pf.Uid,null); } }
      if(only=="all"||only=="railing"){
        foreach(var r in s.Roads){ if(r.Railings==null||r.Length<1f||!s.NodesByUid.ContainsKey(r.StartNode)||!s.NodesByUid.ContainsKey(r.EndNode)) continue; RoadCurve(s,r,out var p0,out var t0,out var p1,out var t1); var lk=win.Map.RoadLook(r.LookToken); float roadOffset=lk!=null?lk.GetFloat("road_offset",0f):0f;
          if(!inBox(H(p0,t0,p1,t1,0.5f))) continue;
          foreach(var rl in r.Railings){ var u=win.Map.Railing(rl.Model); var path=u?.GetString("model_desc",null); if(string.IsNullOrEmpty(path)) continue; var m=Model(path); if(m==null) continue;
            var setM=RSet(m, r.Length>=10f?new[]{"15m","5m"}:new[]{"5m","15m"}); if(setM==null) continue;
            bool inverted=rl.Left?r.LeftRailingInverted:r.RightRailingInverted, centreOnly=rl.Left?r.LeftRailingCenterOnly:r.RightRailingCenterOnly, dbl=rl.Left?r.LeftRailingDoubleSided:r.RightRailingDoubleSided;
            float lateral=rl.Left?-(roadOffset+rl.Offset):(roadOffset+rl.Offset); bool turned=rl.Left!=inverted;
            string whyS, whyE; Continues(s,r,r.StartNode,rl.Model,out whyS); Continues(s,r,r.EndNode,rl.Model,out whyE);
            string railLook=u.GetString("look_name","default");
            for(int rule=0;rule<RailRules;rule++){ int capRule=rule<5?rule:0; bool small=rule==5; var setC=small?(RSet(m,new[]{"5m"})??setM):setM;
              bool capStart=!centreOnly&&CapRule(capRule,s,r,r.StartNode,rl), capEnd=!centreOnly&&CapRule(capRule,s,r,r.EndNode,rl); string rn=" [cap rule "+rule+"]";
              for(int copy=0;copy<(dbl?2:1);copy++){ bool tr=copy==0?turned:!turned;
                int firstCap=tr?setC.end:setC.start, lastCap=tr?setC.start:setC.end; bool cs=capStart&&firstCap>=0, ce=capEnd&&lastCap>=0;
                // nominal layout: [start cap] centre x k [end cap], stretched to the road's length
                float capLen=setC.len; float rest=r.Length-(cs?capLen:0)-(ce?capLen:0); int k=Math.Max(0,(int)Math.Round(rest/setM.len, MidpointRounding.AwayFromZero));
                if(!small){ int repeats=Math.Max(1,(int)Math.Round(r.Length/setM.len, MidpointRounding.AwayFromZero)); k=repeats-(cs?1:0); if(ce&&k>=1) k--; else ce=false; }
                float total=(cs?capLen:0)+(ce?capLen:0)+k*setM.len; if(total<0.5f) continue; float stretch=r.Length/total;
                var runs=new List<(int part,float sStart,float sLen,float at,string tag)>(); float pos0=0f;
                if(cs){ runs.Add((firstCap,setC.sliceStart,setC.len,pos0,"cap at start node")); pos0+=capLen; } for(int i=0;i<k;i++){ runs.Add((setM.center,setM.sliceStart,setM.len,pos0,"centre")); pos0+=setM.len; } if(ce){ runs.Add((lastCap,setC.sliceStart,setC.len,pos0,"cap at end node")); }
                int idx=0; foreach(var run in runs){ idx++; if(run.tag=="centre"&&rule>0) continue; var pts=PartSamples(path,m,run.part,railLook); if(pts.Length==0) continue; float side=tr?-1f:1f, zEnd=2f*run.sStart+run.sLen;
                  string cat=run.tag=="centre"?"railing centre":"railing cap (start/end part)"+rn; Count(cat);
                  var res=TestSolid(near,pts,lp=>{ float x=lp.x*side+lateral, y=lp.y, zu=-lp.z; if(tr) zu=zEnd-zu; float along=(run.at+(zu-run.sStart))*stretch; float sp=Mathf.Clamp01(along/r.Length); var pt=H(p0,t0,p1,t1,sp); var fw=HT(p0,t0,p1,t1,sp).normalized; var rt=Vector3.Cross(Vector3.up,fw).normalized; var up=Vector3.Cross(fw,rt).normalized; return pt+rt*x+up*y; }, r.Uid);
                  Report(res,cat,$"railing.{rl.Name} {path} part {m.Parts[run.part].Name} {run.tag} piece {idx}/{runs.Count} {(rl.Left?"L":"R")}{rl.Slot} off {rl.Offset} {(inverted?"inverted ":"")}{(centreOnly?"centreOnly ":"")}{(dbl?"double ":"")}{(copy==1?"twin ":"")}len {r.Length:0.0} look {r.Look}{(r.HighPoly?" highpoly":"")}; start node: {whyS}; end node: {whyE}",r.Uid,null); } } } } } }
      if(done%20==0) Console.Error.WriteLine($"{done}/{all.Count} sectors {clock.Elapsed.TotalSeconds:0}s hits {results.Count}");
    }
    using(var o=new StreamWriter(outPath)){
      o.WriteLine($"bridges probe b1: lane band inset {Inset} m, rise {Low}..{High} m, prefab lane +-{LaneHalf} m; {clock.Elapsed.TotalSeconds:0} s");
      foreach(var kv in instances) { totals.TryGetValue(kv.Key,out int n); o.WriteLine($"TOTAL {kv.Key}: {n} of {kv.Value} instances stand in a lane"); }
      foreach(var r in results.OrderByDescending(r=>r.cat).ThenByDescending(r=>r.hits*Math.Min(2f,r.depth))) o.WriteLine($"{r.cat}\thits {r.hits}\tdepth {r.depth:0.0}\trise {r.worstRise:0.0}\tat game ({r.at.x:0.0}, {r.at.y:0.0}, {-r.at.z:0.0})\titem {r.uid:x16}\tinto {(r.intoKind==1?"road":"prefab")} {r.into:x16}\t{r.what}");
    }
  }
}
