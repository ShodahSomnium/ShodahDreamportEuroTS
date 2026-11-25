using System; using System.Linq; using System.Collections.Generic; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// s6 <pmd> <part>: the part's triangles as a coarse x-z map of the lowest point per 1 m cell (how deep each spot of the part reaches).
static class P { static void Main(string[] a){ var fs=EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
 string k="/"+EtsHashFs.Normalize(a[0]); var m=EtsModelData.Parse(fs.Read(k),fs.Read(k.Substring(0,k.Length-4)+".pmg"),k); var part=m.Parts.First(p=>p.Name==a[1]);
 var low=new Dictionary<(int,int),float>(); 
 for(int i=part.PieceStart;i<part.PieceStart+part.PieceCount;i++){ var pc=m.Pieces[i]; var I=pc.Indices; var P=pc.Positions; for(int t=0;t+2<I.Length;t+=3){ for(int s=0;s<=10;s++) for(int u=0;u<=10-s;u++){ float[] q=new float[3]; for(int c=0;c<3;c++) q[c]=P[I[t]*3+c]+(P[I[t+1]*3+c]-P[I[t]*3+c])*s/10f+(P[I[t+2]*3+c]-P[I[t]*3+c])*u/10f; var key=((int)Math.Floor(q[0]),(int)Math.Floor(q[2])); if(!low.TryGetValue(key,out var y)||q[1]<y) low[key]=q[1]; } } }
 int x0=low.Keys.Min(p=>p.Item1), x1=low.Keys.Max(p=>p.Item1), z0=low.Keys.Min(p=>p.Item2), z1=low.Keys.Max(p=>p.Item2);
 Console.WriteLine($"{a[0]} {a[1]}: rows = game z {z0}..{z1} (forward is -z), cols = x {x0}..{x1}; char = lowest y: ' ' none, '.' > -1.5, 'o' > -5, '#' deeper");
 for(int z=z0;z<=z1;z++){ var sb=new System.Text.StringBuilder(); sb.Append($"{z,4} "); for(int x=x0;x<=x1;x++){ if(!low.TryGetValue((x,z),out var y)) sb.Append(' '); else sb.Append(y>-1.5f?'.':y>-5f?'o':'#'); } Console.WriteLine(sb); }
}}
