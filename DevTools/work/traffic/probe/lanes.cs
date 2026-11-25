using System; using System.Collections.Generic; using System.Linq; using UnityEngine; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){ var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
 var map=new EtsMap(fs); var cat=new EtsTrafficCatalog(fs); float gx=float.Parse(a[0]), gz=float.Parse(a[1]);
 var g=new EtsLaneGraph(map,fs,cat,new Vector3(gx,0,gz)); g.EnsureAround(Vector3.zero,1500f,100000);
 var seen=new HashSet<ulong>();
 foreach(var l in g.Lanes.Where(l=>l!=null && !l.InPrefab && l.Type!=null && l.Length>120 && (l.Type.SpeedClass=="motorway"||l.Type.SpeedClass=="expressway")).OrderBy(l=>((l.Start+l.End)/2).magnitude)){
   if(!seen.Add(l.Item)) continue; var mid=g.PointAt(l,l.Length/2); var d=g.DirectionAt(l,l.Length/2);
   Console.WriteLine("item "+l.Item.ToString("X")+" "+l.Type.Name+" len "+l.Length.ToString("0")+" mid "+mid.x.ToString("0.0")+","+mid.y.ToString("0.0")+","+mid.z.ToString("0.0")+" yaw "+(Mathf.Atan2(d.x,d.z)*Mathf.Rad2Deg).ToString("0")+" curve "+(l.CurveLimit*3.6f).ToString("0")+" urban "+l.Urban+" dist "+mid.magnitude.ToString("0"));
   if(seen.Count>14) break; } } }
