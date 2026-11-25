using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  fs.TryList("/map/europe", out var files, out _); Console.WriteLine("files "+files.Count+" e.g. "+string.Join(" ",files.Take(5)));
  var secs=files.Where(f=>f.EndsWith(".base")).Select(f=>f.Substring(0,f.Length-5)).ToList(); Console.WriteLine("sectors "+secs.Count);
  var types=new Dictionary<int,int>(); int done=0;
  foreach(var name in secs){ string t10=name.Substring(name.Length-10); int sx=int.Parse(t10.Substring(0,5)), sz=int.Parse(t10.Substring(5,5));
    foreach(var ext in new[]{"base","aux"}){ var bb=fs.Read(EtsMapSector.FileName(sx,sz,ext)); if(bb==null) continue; var s=EtsMapSector.Parse(sx,sz,bb,null,null); foreach(var it in s.Items) types[it.Type]=types.TryGetValue(it.Type,out var c)?c+1:1; }
    if(++done>=int.Parse(a[0])) break; }
  foreach(var kv in types.OrderBy(x=>x.Key)) Console.WriteLine("type "+kv.Key+": "+kv.Value);
}}
