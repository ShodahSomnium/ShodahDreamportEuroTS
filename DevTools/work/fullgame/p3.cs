using System; using System.IO; using System.Text; using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
 static EtsFileSystem fs;
 static void Walk(string dir, string needle, ref int n){
  if(!fs.TryList(dir,out var f,out var d)) return;
  foreach(var x in f){ if(!(x.EndsWith(".sii")||x.EndsWith(".sui"))) continue; string p=dir.TrimEnd('/')+"/"+x; var t=fs.ReadText(p); if(t==null) continue; int i=t.IndexOf(needle,StringComparison.OrdinalIgnoreCase); if(i>=0){ n++; if(n<40) Console.WriteLine(p+": "+t.Substring(Math.Max(0,i-200),Math.Min(500,t.Length-Math.Max(0,i-200))).Replace("\n"," | ")); } }
  foreach(var x in d) Walk(dir.TrimEnd('/')+"/"+x,needle,ref n);
 }
 static void Main(string[] a){
  fs=EtsFileSystem.Mount(a[0],out string rep);
  foreach(var m in Directory.GetFiles(a[0],"dlc_*.scs")){ var nm=Path.GetFileNameWithoutExtension(m); var t=fs.ReadText("/"+nm+".manifest.sii"); int i=t.IndexOf("relative_priority"); Console.WriteLine(nm+" "+t.Substring(i,24).Replace("\n"," ")); }
  int n=0; Walk(a[1],a[2],ref n); Console.WriteLine("hits "+n);
 }
}
