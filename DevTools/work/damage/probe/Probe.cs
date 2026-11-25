using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;

static class Probe
{
    static EtsFileSystem fs;
    static void Walk(string dir, List<string> outFiles, int depth)
    {
        if (depth > 12) return;
        if (!fs.TryList(dir, out var files, out var folders)) return;
        foreach (var f in files) outFiles.Add(dir.TrimEnd('/') + "/" + f);
        foreach (var d in folders) Walk(dir.TrimEnd('/') + "/" + d, outFiles, depth + 1);
    }
    static int Main(string[] a)
    {
        fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out string rep);
        for (int i = 0; i < a.Length; i++)
        {
            string cmd = a[i];
            if (cmd == "cat") { Console.WriteLine("=== " + a[i+1]); Console.WriteLine(fs.ReadText(a[++i]) ?? "<missing>"); }
            else if (cmd == "ls") { string p = a[++i]; if (fs.TryList(p, out var f, out var d)) { foreach (var x in d) Console.WriteLine(x + "/"); foreach (var x in f) Console.WriteLine(x); } else Console.WriteLine("<no dir>"); }
            else if (cmd == "find") { var l = new List<string>(); Walk(a[++i], l, 0); var rx = new Regex(a[++i], RegexOptions.IgnoreCase); foreach (var x in l) if (rx.IsMatch(x)) Console.WriteLine(x); }
            else if (cmd == "grep")
            {
                var rx = new Regex(a[++i], RegexOptions.IgnoreCase); string root = a[++i];
                var l = new List<string>(); Walk(root, l, 0);
                foreach (var x in l)
                {
                    if (!(x.EndsWith(".sii") || x.EndsWith(".sui") || x.EndsWith(".txt") || x.EndsWith(".cfg"))) continue;
                    string t = fs.ReadText(x); if (t == null) continue;
                    var lines = t.Split('\n');
                    for (int k = 0; k < lines.Length; k++) if (rx.IsMatch(lines[k])) Console.WriteLine(x + ":" + (k+1) + ": " + lines[k].Trim());
                }
            }
            else if (cmd == "loc")
            {
                EtsLocale.Load(fs);
                var rx = new Regex(a[++i], RegexOptions.IgnoreCase);
                string txt = fs.ReadText("/locale/en_gb/local.sii");
                // scan included files too
                var l = new List<string>(); Walk("/locale", l, 0);
                foreach (var x in l)
                {
                    if (!(x.EndsWith(".sii") || x.EndsWith(".sui"))) continue;
                    if (x.Contains("/locale/") && !x.StartsWith("/locale/en_gb") && x.Split('/').Length > 3) continue;
                    string t = fs.ReadText(x); if (t == null) continue;
                    var lines = t.Split('\n');
                    for (int k = 0; k < lines.Length; k++) if (rx.IsMatch(lines[k])) { string prev = k>0?lines[k-1].Trim():""; Console.WriteLine(x + ":" + (k+1) + ": " + (lines[k].TrimStart().StartsWith("val")? prev + " | " : "") + lines[k].Trim()); }
                }
            }
        }
        return 0;
    }
}
