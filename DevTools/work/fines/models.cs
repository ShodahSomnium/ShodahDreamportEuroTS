using System; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  foreach(var path in a){ var m = EtsModelData.Parse(fs.Read(path+".pmd"), fs.Read(path+".pmg"), path);
    Console.WriteLine(path+" looks="+string.Join(",",m.Looks)+" variants="+string.Join(",",m.Variants)+" bounds=("+string.Join(",",m.BoundsMin)+")-("+string.Join(",",m.BoundsMax)+")");
    foreach(var p in m.Parts) Console.WriteLine("  part "+p.Name+" pieces="+p.PieceCount+" loc="+p.LocatorCount);
    foreach(var l in m.Locators) Console.WriteLine("  loc "+l.Name+" pos=("+string.Join(",",l.Position)+") rot=("+string.Join(",",l.Rotation)+") hookup="+l.Hookup);
    foreach(var mats in m.LookMaterials) Console.WriteLine("  mats "+string.Join(" ",mats));
  }}}
