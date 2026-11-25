using System; using System.IO; using System.Collections.Generic; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
  if (a[0]=="ls") { foreach (var path in a[1..]) { if (fs.TryList(path, out var f, out var d)) { foreach (var x in d) Console.WriteLine("D " + x); foreach (var x in f) Console.WriteLine("F " + x); } else Console.WriteLine("nolist " + path);} }
  else if (a[0]=="cat") { var b = fs.Read(a[1]); if (b==null){Console.WriteLine("null");return;} if (a.Length>2) File.WriteAllBytes(a[2], b); else Console.Write(System.Text.Encoding.UTF8.GetString(b)); }
  else if (a[0]=="tree") { var st = new Stack<string>(); st.Push(a[1]); while (st.Count>0){ var p=st.Pop(); if (!fs.TryList(p, out var f, out var d)) continue; foreach (var x in f) Console.WriteLine(p.TrimEnd((char)47)+"/"+x); foreach (var x in d) st.Push(p.TrimEnd((char)47)+"/"+x);} }
}}
