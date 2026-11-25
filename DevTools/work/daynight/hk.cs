using System; using System.Text; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  foreach(var path in a){ var m = EtsModelData.Parse(fs.Read(path), fs.Read(path.Replace(".pmd",".pmg")), path); byte[] pmg=fs.Read(path.Replace(".pmd",".pmg"));
    int po=BitConverter.ToInt32(pmg,88), ps=BitConverter.ToInt32(pmg,92);
    Console.WriteLine(path+" locators "+m.Locators.Length);
    foreach(var l in m.Locators){ string h=null; if(l.Hookup>=0&&l.Hookup<ps){int s=po+l.Hookup,e=s; while(e<po+ps&&pmg[e]!=0)e++; h=Encoding.ASCII.GetString(pmg,s,e-s);} Console.WriteLine("  "+l.Name+" @ "+string.Join(",",l.Position)+" hookup "+h); }
  }
}}
