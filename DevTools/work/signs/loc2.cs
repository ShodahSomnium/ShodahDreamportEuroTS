using System; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){ var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _); Console.WriteLine(EtsLocale.Load(fs)+" "+EtsLocale.Count); foreach (var k in a) Console.WriteLine(k+" -> '"+EtsLocale.Resolve(k)+"'"); } }
