using System; using System.IO; using System.Text; using System.Collections.Generic; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static EtsFileSystem fs;
  static void Walk(string dir, string pat, List<string> outp, int depth){ if(depth>12) return; if(!fs.TryList(dir, out var files, out var folders)) return;
    foreach(var f in files){ string p=(dir=="/"?"":dir)+"/"+f; if(p.Contains(pat)) outp.Add(p);} foreach(var d in folders) Walk((dir=="/"?"":dir)+"/"+d, pat, outp, depth+1); }
  static void Main(string[] a){
    fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    if(a[0]=="ls"){ if(fs.TryList(a[1], out var f, out var d)){ foreach(var x in d) Console.WriteLine(x+"/"); foreach(var x in f) Console.WriteLine(x);} else Console.WriteLine("no list"); }
    else if(a[0]=="find"){ var l=new List<string>(); Walk(a[1], a[2], l, 0); foreach(var x in l) Console.WriteLine(x); }
    else if(a[0]=="cat"){ var b=fs.Read(a[1]); if(b==null) Console.WriteLine("null"); else if(a.Length>2) File.WriteAllBytes(a[2], b); else Console.WriteLine(Encoding.UTF8.GetString(b)); }
    else if(a[0]=="grep"){ var l=new List<string>(); Walk(a[1], a[2], l, 0); foreach(var x in l){ var t=fs.ReadText(x); if(t!=null && t.Contains(a[3])) Console.WriteLine(x);} }
  }
}
