using System; using System.Diagnostics; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){ var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
 foreach(var rep in new[]{0,1}) foreach(var m in a){ var sw=Stopwatch.StartNew(); var pmd=fs.Read(m); var pmg=fs.Read(m.Substring(0,m.Length-4)+".pmg"); long r=sw.ElapsedMilliseconds; var d=EtsModelData.Parse(pmd,pmg,m); Console.WriteLine(m+" read "+r+" ms parse "+(sw.ElapsedMilliseconds-r)+" ms, pmg "+pmg.Length/1024+" kB"); } } }
