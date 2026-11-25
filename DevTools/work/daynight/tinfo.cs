using System; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  foreach(var p in a){
    string t = p.EndsWith(".tobj") ? p : p;
    // tobj -> image entry: the packed image sits at the tobj path in HashFS v2
    if(fs.TryGetEntry(t, out EtsHashFs arc, out EtsHashFs.Entry e)) Console.WriteLine(t+": packed="+e.IsPackedImage+" "+e.ImageWidth+"x"+e.ImageHeight+" mips "+e.MipCount+" dxgi "+e.DxgiFormat);
    else Console.WriteLine(t+": missing");
  }
}}
