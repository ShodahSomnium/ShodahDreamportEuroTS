using System; using System.Collections.Generic; using UnityEngine; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// Lists the speed cameras round three cities with the truck limit in force at each (found by driving the lane graph's
// lanes into the camera through EtsFineJudge), then simulates passes at several speeds and levels.
static class P {
  static EtsMap map; static EtsFineMap fm; static EtsPoliceData police;
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    EtsLocale.Load(fs);
    map = new EtsMap(fs); fm = new EtsFineMap(fs, map); police = EtsPoliceData.Load(fs);
    Console.WriteLine("police_data from file: "+police.FromFile+"; hookup units "+fm.HookupUnits+"; camera fine "+police.Amount(3)+", level factor 0/5/10/20: "+police.LevelFactor(0)+"/"+police.LevelFactor(5)+"/"+police.LevelFactor(10)+"/"+police.LevelFactor(20));
    if(Array.IndexOf(a,"scan")>=0){ var sw=System.Diagnostics.Stopwatch.StartNew(); var t=EtsFineMap.ScanCameraModels(fs); fm.SetCameraModels(t); Console.WriteLine("camera model scan: "+t.Count+" sign models with a speedcam locator, "+sw.ElapsedMilliseconds+" ms"); }
    var cat = new EtsTrafficCatalog(fs);
    var pts = new[]{ (-33391.4f,-7420.0f,"Dover"), (14501f,3625f,"Prague"), (14994f,-15235f,"Szczecin") };
    float R = float.Parse(a.Length>0?a[0]:"8000");
    EtsSpeedCamera simCam=null; List<(Vector3,Vector3)> simPath=null; EtsLaneGraph simGraph=null;
    foreach(var (gx,gz,name) in pts){
      var secs = map.SectorsAround(gx,gz,R+4000f); foreach(var s in secs) if(!fm.HasSector(s.SectorX,s.SectorZ)) fm.Gather(s);
      var graph = new EtsLaneGraph(map, fs, cat, new Vector3(gx,0,gz));
      var cams = new List<EtsSpeedCamera>(); foreach(var s in secs){ var f=fm.Sector(s.SectorX,s.SectorZ); foreach(var c in f.Cameras){ float dx=c.X-gx, dz=c.Z-gz; if(dx*dx+dz*dz<=R*R) cams.Add(c);} }
      int signs=0; foreach(var s in secs) signs+=fm.Sector(s.SectorX,s.SectorZ).Signs.Count;
      Console.WriteLine("\n== "+name+" (game "+gx+", "+gz+"): "+cams.Count+" speed camera locators within "+R/1000+" km; "+signs+" speed-rule signs in the "+secs.Count+" sectors read");
      cams.Sort((p,q)=>Dist(p,gx,gz).CompareTo(Dist(q,gx,gz)));
      foreach(var c in cams){
        foreach(var s in map.SectorsAround(c.X,c.Z,600f)) graph.BuildSector(s.SectorX, s.SectorZ);
        var path = PathInto(graph, c, 1200f);
        var judge = new EtsFineJudge(fm); string res="no lane into it";
        if(path!=null){ foreach(var p in path){ var g=Game(graph,p.Item1); judge.Step(g[0],g[1],g[2],p.Item2.x,-p.Item2.z, 10f); }
          res = "limit "+judge.SignFigure+(judge.Imperial?" mph":" km/h")+" ("+judge.LimitKmh.ToString("0.#")+" km/h) - "+judge.Country+", "+judge.SpeedClass+(judge.Urban?", urban":"")+(judge.SignRule!=null?", sign "+judge.SignRule.Replace("traffic_rule.",""):", no limit sign passed")+"; fires at >= "+((1+c.Tolerance)*judge.LimitKmh).ToString("0.0")+" km/h";
          if(simCam==null && name=="Prague") { simCam=c; simPath=path; simGraph=graph; } }
        Console.WriteLine(string.Format("  {0,6:0} m  ({1,9:0.0}, {2,9:0.0})  {3} [{4}] {5} {6} dir ({7:0.00},{8:0.00})  {9}", Dist(c,gx,gz), c.X, c.Z, c.SignModel, Short(c.ModelPath), c.Locator, c.Hookup, c.DirX, c.DirZ, res));
      }
    }
    if(simCam!=null){
      Console.WriteLine("\n== Simulation: driving the lanes into "+simCam.SignModel+" "+simCam.Locator+" at ("+simCam.X.ToString("0.0")+", "+simCam.Z.ToString("0.0")+")");
      var j0=new EtsFineJudge(fm); foreach(var p in simPath){ var g=Game(simGraph,p.Item1); j0.Step(g[0],g[1],g[2],p.Item2.x,-p.Item2.z,10f);} float lim=j0.LimitKmh;
      float[] speeds={ lim-10, lim, lim*1.09f, lim*1.10f+0.1f, lim+9.9f, lim+10, lim+20, lim+30, lim+60, lim+100 };
      foreach(var kmh in speeds){
        var j=new EtsFineJudge(fm); EtsFineJudge.Caught caught=null; int hits=0;
        foreach(var p in simPath){ var g=Game(simGraph,p.Item1); var c=j.Step(g[0],g[1],g[2],p.Item2.x,-p.Item2.z,kmh/3.6f); if(c!=null){ caught=c; hits++; if(true) Console.WriteLine("      offence at ("+g[0].ToString("0.0")+", "+g[2].ToString("0.0")+") cam "+c.Camera.SignUid.ToString("x")+" "+c.Camera.Locator); } }
        // drive the same path backwards (the wrong way past the camera): must never fire
        var jb=new EtsFineJudge(fm); int back=0; for(int i=simPath.Count-1;i>=0;i--){ var g=Game(simGraph,simPath[i].Item1); if(jb.Step(g[0],g[1],g[2],-simPath[i].Item2.x,simPath[i].Item2.z,kmh/3.6f)!=null) back++; }
        string fines = caught==null? "no fine" : "FINED, overspeed "+(caught.OverspeedMs*3.6f).ToString("0.0")+" km/h, level 0/5/10/20: "+police.Fine(3,0,caught.OverspeedMs,false)+"/"+police.Fine(3,5,caught.OverspeedMs,false)+"/"+police.Fine(3,10,caught.OverspeedMs,false)+"/"+police.Fine(3,20,caught.OverspeedMs,false)+" EUR; text: "+EtsLocale.Resolve(police.Markup(3,caught.OverspeedMs,police.Fine(3,10,caught.OverspeedMs,false),"km/h"));
        Console.WriteLine(string.Format("  {0,6:0.0} km/h (limit {1:0}): {2} offence(s); wrong way {3}; {4}", kmh, lim, hits, back, fines));
      }
    }
  }
  static string Short(string p){ if(p==null) return ""; int i=p.LastIndexOf('/'); return p.Substring(i+1); }
  static float Dist(EtsSpeedCamera c,float x,float z){ return (float)Math.Sqrt((c.X-x)*(c.X-x)+(c.Z-z)*(c.Z-z)); }
  static float[] Game(EtsLaneGraph g, Vector3 u){ return new float[]{ u.x+g.CenterGame.x, u.y+g.CenterGame.y, g.CenterGame.z-u.z }; }
  // A path of (point, unity direction) every 2 m: back along Previous lanes from the camera's lane for 'back' metres, then 60 m beyond.
  static List<(Vector3,Vector3)> PathInto(EtsLaneGraph g, EtsSpeedCamera c, float back){
    Vector3 u = new Vector3(c.X-g.CenterGame.x, c.Y-g.CenterGame.y, -(c.Z-g.CenterGame.z));
    Vector3 dir = new Vector3(c.DirX, 0, -c.DirZ);
    if(!g.Match(u, dir, 8f, 0.5f, out int lid, out float along)) return null;
    var lanes = new List<(EtsLaneGraph.Lane, float, float)>(); // lane, from, to
    var lane = g.Get(lid); lanes.Add((lane, 0f, Math.Min(lane.Length, along+60f)));
    float got = along; var cur=lane;
    while(got < back && cur.Previous.Count>0){ var prev=g.Get(cur.Previous[0]); if(prev==null) break; lanes.Insert(0,(prev,0f,prev.Length)); got+=prev.Length; cur=prev; }
    var path = new List<(Vector3,Vector3)>();
    foreach(var (l,f,t) in lanes) for(float s=f; s<=t; s+=2f) path.Add((g.PointAt(l,s), g.DirectionAt(l,s)));
    return path;
  }
}
