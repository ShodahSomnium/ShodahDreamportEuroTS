using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){ var fs=EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
 foreach(var p in a){ var md=EtsModelData.Parse(fs.Read(p), fs.Read(p.Substring(0,p.Length-4)+".pmg"), p); Console.WriteLine(p);
  foreach(var pt in md.Parts){ for(int k=pt.PieceStart;k<pt.PieceStart+pt.PieceCount;k++){ var pc=md.Pieces[k]; string mat=md.LookMaterials.Length>0&&pc.MaterialIndex<md.LookMaterials[0].Length?md.LookMaterials[0][pc.MaterialIndex]:"?"; string txt=""; try{ txt=fs.ReadText(mat).Replace("\n"," ").Replace("\r",""); }catch{} Console.WriteLine($"  {pt.Name} piece {k}: {mat}  {(txt.Length>220?txt.Substring(0,220):txt)}"); } } } } }
