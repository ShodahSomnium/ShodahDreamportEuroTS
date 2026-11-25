using System; using System.Text; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  foreach(var path in a){ foreach(var ext in new[]{".pmg",".pmd"}){ var b=fs.Read(path+ext); if(b==null) continue; var s=Encoding.ASCII.GetString(b);
    int i=-1; while((i=s.IndexOf("speedcam",i+1))>=0){ int e=s.IndexOf('\0',i); Console.WriteLine(path+ext+" @"+i+" len="+b.Length+": "+s.Substring(i,Math.Max(0,e-i))); }
    i=-1; while((i=s.IndexOf("police",i+1))>=0){ int e=s.IndexOf('\0',i); Console.WriteLine(path+ext+" @"+i+": "+s.Substring(i,Math.Max(0,Math.Min(60,e-i)))); }
  }}}}
