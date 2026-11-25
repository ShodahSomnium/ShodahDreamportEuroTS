using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){ var fs=EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _); var f=fs.ReadDefinitionFamily("/def/world","railing");
 foreach(var name in a){ var u=f.Find("railing."+name); if(u==null){Console.WriteLine(name+" none"); continue;} Console.WriteLine("railing."+name+": "+string.Join("; ", u.Attributes.Keys.Select(k=>k+"="+u.GetString(k,"")))); } }}
