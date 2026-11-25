using System; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  foreach(var path in a){ var m = EtsModelData.Parse(fs.Read(path), fs.Read(path.Replace(".pmd",".pmg")), path);
   Console.WriteLine(path+" pieces "+m.Pieces.Length);
   for(int i=0;i<m.Pieces.Length;i++){ var p=m.Pieces[i]; float[] mn={1e9f,1e9f,1e9f}, mx={-1e9f,-1e9f,-1e9f}; for(int v=0;v<p.Positions.Length/3;v++) for(int k=0;k<3;k++){ mn[k]=Math.Min(mn[k],p.Positions[v*3+k]); mx[k]=Math.Max(mx[k],p.Positions[v*3+k]); }
     Console.WriteLine("  piece "+i+" verts "+p.Positions.Length/3+" min "+string.Join(",",mn)+" max "+string.Join(",",mx)); }
  }
}}
