using System; using System.IO; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  var b = fs.Read(a[0]); if(b==null){Console.WriteLine("null"); return;} File.WriteAllBytes(a[1], b); Console.WriteLine(b.Length);
}}
