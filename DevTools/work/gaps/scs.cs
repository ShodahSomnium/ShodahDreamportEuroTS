using System; using System.Collections.Generic; using System.IO; using System.Text; using System.Text.RegularExpressions;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static EtsFileSystem fs;
  static void Walk(string dir, Action<string> onFile, int depth=0){
    if(depth>12) return;
    if(!fs.TryList(dir, out var files, out var folders)) return;
    foreach(var f in files) onFile(dir.TrimEnd('/')+"/"+f);
    foreach(var d in folders) Walk(dir.TrimEnd('/')+"/"+d, onFile, depth+1);
  }
  static void Main(string[] a){
    fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    string cmd=a[0];
    if(cmd=="ls"){ fs.TryList(a[1], out var f, out var d); foreach(var x in d) Console.WriteLine(x+"/"); foreach(var x in f) Console.WriteLine(x); }
    else if(cmd=="cat"){ Console.Write(fs.ReadText(a[1])); }
    else if(cmd=="find"){ var re=new Regex(a[2], RegexOptions.IgnoreCase); Walk(a[1], p=>{ if(re.IsMatch(p)) Console.WriteLine(p);}); }
    else if(cmd=="grep"){ var re=new Regex(a[2], RegexOptions.IgnoreCase); string ext=a.Length>3?a[3]:@".(sii|sui|txt|cfg|mat|sui|soundref)$"; var er=new Regex(ext);
      Walk(a[1], p=>{ if(!er.IsMatch(p)) return; var b=fs.Read(p); if(b==null||b.Length>3000000) return; var t=Encoding.UTF8.GetString(b); int ln=0; foreach(var l in t.Split('\n')){ ln++; if(re.IsMatch(l)) Console.WriteLine(p+":"+ln+": "+l.Trim()); } }); }
    else if(cmd=="hex"){ var b=fs.Read(a[1]); Console.WriteLine(b==null?"null":b.Length+" bytes"); }
  }
}
