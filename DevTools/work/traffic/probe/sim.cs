using System; using System.Collections.Generic; using System.Linq; using System.Diagnostics; using UnityEngine; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs);
    var cat = new EtsTrafficCatalog(fs);
    string site=a[0]; float gx=float.Parse(a[1]), gz=float.Parse(a[2]); float seconds=a.Length>3?float.Parse(a[3]):300f; int seed=a.Length>4?int.Parse(a[4]):1;
    var centre=new Vector3(gx,0,gz);
    foreach(var s in map.SectorsAround(gx,gz,2600f)) {}
    var graph = new EtsLaneGraph(map, fs, cat, centre);
    var sw=Stopwatch.StartNew(); graph.EnsureAround(Vector3.zero, 1300f, 100000); Console.WriteLine(site+": lanes "+graph.LaneCount+" in "+graph.Built+" sectors, "+sw.ElapsedMilliseconds+" ms");
#if ORIG
    var settings=new EtsTrafficSim.Settings();
#else
    var start=cat.NearestCountry(gx,gz); var settings=new EtsTrafficSim.Settings{ Country=start.Name, Priorities=Environment.GetEnvironmentVariable("NOPRIO")==null, PriorityLead=float.Parse(Environment.GetEnvironmentVariable("LEAD")??"1.5") };
    // lanes by country, limits
    var byC=new Dictionary<int,int>(); int roadLanes=0; var lim=new Dictionary<string,int>();
    foreach(var l in graph.Lanes){ if(l==null) continue; byC[l.Country]=byC.TryGetValue(l.Country,out var n)?n+1:1; if(!l.InPrefab && (l.Start).magnitude<1500){ roadLanes++; string k=(cat.CountryById(l.Country)?.Name??"?")+" "+(l.Type?.SpeedClass)+(l.Urban?" urban":"")+" car "+(l.SpeedLimit*3.6f).ToString("0")+" truck "+(l.SpeedTruck*3.6f).ToString("0")+" bus "+(l.SpeedBus*3.6f).ToString("0"); lim[k]=lim.TryGetValue(k,out var m)?m+1:1; } }
    Console.WriteLine("  lanes by country: "+string.Join(", ", byC.OrderByDescending(k=>k.Value).Select(k=>(cat.CountryById(k.Key)?.Name??k.Key.ToString())+" "+k.Value)));
    foreach(var kv in lim.OrderByDescending(k=>k.Value).Take(12)) Console.WriteLine("    "+kv.Value+" road lanes within 1.5 km: "+kv.Key+" km/h");
    Console.WriteLine("  start mix "+cat.Describe(start.Name));
#endif
    var sim = new EtsTrafficSim(graph, cat, settings, seed);
    sw.Restart(); sim.Prepare(); Console.WriteLine("  prepare "+sw.ElapsedMilliseconds+" ms");
    var foci=new List<EtsTrafficSim.Focus>{ new EtsTrafficSim.Focus{ Position=Vector3.zero, Fresh=true } };
    var obstacles=new List<EtsTrafficSim.Obstacle>();
    float dt=1f/30f; double t=0; var times=new List<double>(); int overlapPairs=0, overlapSamples=0; var seenPairs=new HashSet<long>();
    var steps=(int)(seconds/dt); var clock=new Stopwatch(); int maxCars=0; double carSeconds=0, stopped=0;
    for(int i=0;i<steps;i++){
      if(t>60) foci[0]=new EtsTrafficSim.Focus{Position=Vector3.zero, Fresh=false};
      clock.Restart(); sim.Step(dt, foci, obstacles, t); times.Add(clock.Elapsed.TotalMilliseconds); t+=dt;
      if(i%30==0){
        var cars=sim.Slots.Where(c=>c!=null && !c.Wrecked).ToList(); maxCars=Math.Max(maxCars,cars.Count);
        foreach(var c in cars){ carSeconds+=1; if(c.V<0.5f) stopped+=1; }
        for(int x=0;x<cars.Count;x++) for(int y=x+1;y<cars.Count;y++){
          var A=cars[x]; var B=cars[y]; if(Math.Abs(A.Position.y-B.Position.y)>3f) continue;
#if ORIG
          float ab=A.HalfLength, bb=B.HalfLength;
#else
          float ab=A.Back, bb=B.Back;
#endif
          Vector3 fa=EtsTrafficSim.Flat(A.Direction), fb=EtsTrafficSim.Flat(B.Direction);
          Vector3 ca=A.Position+fa*((A.HalfLength-ab)*0.5f), cb=B.Position+fb*((B.HalfLength-bb)*0.5f);
          if((ca-cb).sqrMagnitude>40*40) continue;
          // tractor boxes only, 0.3 m in from each side (curves bend the combo, so only tractors are tested exactly)
          if(EtsTrafficSim.Overlap2D(A.Position,fa,A.HalfLength-0.3f,A.HalfWidth-0.3f,B.Position,fb,B.HalfLength-0.3f,B.HalfWidth-0.3f,0f,out float depth,out _)){ overlapSamples++; long key=((long)A.Slot*64+B.Slot)*65536+(A.Serial*16+B.Serial); if(seenPairs.Add(key)) overlapPairs++; }
        }
      }
    }
    times.Sort(); double mean=times.Average();
    Console.WriteLine("  "+seconds+" s: steps mean "+mean.ToString("0.000")+" ms, p99.9 "+times[(int)(times.Count*0.999)].ToString("0.000")+", max after 2 s "+times.Skip(60).Max().ToString("0.00")+" ms; cars max "+maxCars+", stopped share "+(stopped/Math.Max(1,carSeconds)*100).ToString("0.0")+"%");
    Console.WriteLine("  spawned "+sim.Spawned+", removed far "+sim.RemovedFar+" stuck "+sim.RemovedStuck+" deadend "+sim.RemovedDeadEnd+" lost "+sim.RemovedLost+"; junction waits "+sim.JunctionWaits+", entered unheld "+sim.EnteredUnheld
#if !ORIG
      +", priority give-ways "+sim.PriorityWaits+"; by kind "+string.Join(", ", Enumerable.Range(0,7).Select(k=>sim.SpawnedByKind[k]+" "+EtsTrafficCatalog.TypeNames[k]))+", trailers "+sim.SpawnedTrailers
#endif
    );
    Console.WriteLine("  tractor boxes overlapping (0.3 m in): "+overlapPairs+" pairs, "+overlapSamples+" pair-seconds");
#if !ORIG
    var now=sim.Slots.Where(c=>c!=null).GroupBy(c=>cat.Vehicles[c.Vehicle].Kind).Select(g=>g.Count()+" "+EtsTrafficCatalog.TypeNames[g.Key]);
    Console.WriteLine("  on the road at the end: "+string.Join(", ",now)+"; examples: "+string.Join("; ", sim.Slots.Where(c=>c!=null && cat.Vehicles[c.Vehicle].Kind!=0).Take(8).Select(c=>cat.Vehicles[c.Vehicle].Unit.Replace("traffic.","")+(c.Trailer>=0?"+"+cat.Vehicles[c.Trailer].Unit.Replace("traffic.trailer.",""):"")+" "+(c.V*3.6f).ToString("0")+" km/h back "+c.Back.ToString("0.0")+" m")));
#endif
  }
}
