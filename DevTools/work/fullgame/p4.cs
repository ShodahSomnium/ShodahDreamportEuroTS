using System; using System.IO; using System.Diagnostics; using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
 static void Main(string[] a){
  var fs=EtsFileSystem.Mount(a[0],out string rep);
  var map=new EtsMap(fs); Console.WriteLine("sectors "+map.SectorCount);
  long m0=GC.GetTotalMemory(true); var sw=Stopwatch.StartNew(); long bytes=0; int guarded=0; int k=0; var guards=new Dictionary<int,int>();
  foreach(var n in map.AvailableSectors){ int x=int.Parse(n.Substring(3,5)), z=int.Parse(n.Substring(8,5)); var s=map.GetSector(x,z); if(s!=null){ guarded+=s.GuardDropped.Count; } if((++k)%20==0) Console.WriteLine(k+" "+GC.GetTotalMemory(true)/1048576+" MB "+sw.ElapsedMilliseconds+" ms");  }
  long m1=GC.GetTotalMemory(true);
  Console.WriteLine("parsed all in "+sw.ElapsedMilliseconds+" ms, managed "+(m1-m0)/1048576+" MB, file bytes "+bytes/1048576+" MB, guard-dropped "+guarded);
 }
}
