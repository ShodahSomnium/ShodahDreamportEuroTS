using System; using System.IO; using System.Text; using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
 static void Main(string[] a){
  string root=a[0];
  var v=EtsHashFs.Open(Path.Combine(root,"version.scs"));
  Console.WriteLine("version.scs v"+v.Version+" entries "+v.EntryCount);
  foreach(var n in new[]{"version.sii","version.txt","version",""}) { if(v.TryReadDirectory(n,out var f,out var d)) Console.WriteLine("dir '"+n+"': "+string.Join(",",f)+" | "+string.Join(",",d)); }
  var fs=EtsFileSystem.Mount(root,out string rep); Console.WriteLine(rep);
  if(fs.TryList("/",out var ff,out var dd)) Console.WriteLine("root files: "+string.Join(",",ff)+"\nroot dirs: "+string.Join(",",dd));
  foreach(var name in a[1..]) { var b=fs.Read(name); if(b==null){Console.WriteLine(name+": absent");continue;} Console.WriteLine("== "+name+" "+b.Length+" bytes"); Console.WriteLine(b.Length<6000? Encoding.UTF8.GetString(b): BitConverter.ToString(b,0,64)); }
  foreach(var dlc in Directory.GetFiles(root,"dlc_*.scs")) { var h=EtsHashFs.Open(dlc); var m=h.TryGetEntry(EtsHashFs.Normalize("/manifest.sii"),out var e); Console.WriteLine(Path.GetFileName(dlc)+" v"+h.Version+" manifest "+m+(m? "\n"+Encoding.UTF8.GetString(h.Read(e)):"")); if(h.TryReadDirectory("",out var f2,out var d2)) Console.WriteLine("  root: "+string.Join(",",f2)+" | "+string.Join(",",d2)); }
 }
}
