using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// s5 <pmd>...: per part: pieces, x/y/z ranges, and the x-y extent of upright faces lower than -1.5 m.
static class P { static void Main(string[] a){ var fs=EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
 foreach(var path in a){ string k="/"+EtsHashFs.Normalize(path); var m=EtsModelData.Parse(fs.Read(k),fs.Read(k.Substring(0,k.Length-4)+".pmg"),k); Console.WriteLine(k+" looks ["+string.Join(",",m.Looks)+"] variants ["+string.Join(",",m.Variants)+"]");
  foreach(var p in m.Parts){ float[] mn={1e9f,1e9f,1e9f}, mx={-1e9f,-1e9f,-1e9f}; float dx0=1e9f,dx1=-1e9f,dz0=1e9f,dz1=-1e9f; int deep=0;
   for(int i=p.PieceStart;i<p.PieceStart+p.PieceCount;i++){ var pc=m.Pieces[i]; for(int c=0;c<3;c++){ mn[c]=Math.Min(mn[c],pc.BoundsMin[c]); mx[c]=Math.Max(mx[c],pc.BoundsMax[c]); }
     for(int v=0;v<pc.VertexCount;v++){ float x=pc.Positions[v*3],y=pc.Positions[v*3+1],z=pc.Positions[v*3+2]; if(y<-1.5f){ deep++; dx0=Math.Min(dx0,x);dx1=Math.Max(dx1,x);dz0=Math.Min(dz0,z);dz1=Math.Max(dz1,z);} } }
   Console.WriteLine($"  {p.Name,-14} pieces {p.PieceCount,2} x {mn[0]:0.0}..{mx[0]:0.0} y {mn[1]:0.0}..{mx[1]:0.0} z {mn[2]:0.0}..{mx[2]:0.0}  verts below -1.5 m: {deep} x {dx0:0.0}..{dx1:0.0} z {dz0:0.0}..{dz1:0.0}"); } }
}}
