using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){ var fs=EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
 foreach(var path in a){ var t=fs.ReadText(path); if(t==null){ Console.WriteLine("missing "+path); continue; }
  int i=t.IndexOf("profile114"); Console.WriteLine(path+" len "+t.Length+" idx "+i); if(i>=0) Console.WriteLine(t.Substring(Math.Max(0,i-100), Math.Min(1500, t.Length-Math.Max(0,i-100)))); } }}
