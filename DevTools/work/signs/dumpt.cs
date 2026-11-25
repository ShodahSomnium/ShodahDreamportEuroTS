using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
  var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _); var lib = new EtsSignLibrary(fs);
  foreach (var n in a) {
    if (n.StartsWith("sign_board.")) { var b = lib.Board(n); if (b==null){Console.WriteLine(n+" missing"); continue;} Console.WriteLine(n+" board "+b.Width+"x"+b.Height); foreach (var it in b.Items) Pr(it); continue; }
    var t = lib.Template(n); if (t==null){Console.WriteLine(n+" missing"); continue;}
    Console.WriteLine(n+" model "+t.ModelName+" bindings "+string.Join(", ", t.Bindings.Select(x=>x.Key+"->"+x.Value)));
    foreach (var it in t.Items) Pr(it);
    foreach (var bd in t.Bindings) { var b = lib.Board(bd.Value); if (b==null) continue; Console.WriteLine("  board "+bd.Value+" "+b.Width+"x"+b.Height+" in "+bd.Key); foreach (var it in b.Items) Pr(it); }
  } }
  static void Pr(EtsSignItem it){ Console.WriteLine("   "+it.Kind+" z"+it.Z+" id "+it.Id+" area '"+it.Area+"' l "+it.L.ToString("0.###")+" r "+it.R.ToString("0.###")+" b "+it.B.ToString("0.###")+" t "+it.T.ToString("0.###")+" "+(it.Kind==EtsSignItemKind.Image? it.AtlasPath+"#"+it.ImageIdx+" op"+it.Opacity+" col"+(it.ColorNil?"nil":it.Color.ToString("x6")) : it.Kind==EtsSignItemKind.Frame? it.FramePath+" op"+it.Opacity+" col"+(it.ColorNil?"nil":it.Color.ToString("x6")) : "'"+it.Text+"' "+it.FontFace+" sz"+it.FontSize+" ha"+it.HAlign+" va"+it.VAlign+" op"+it.Opacity+" col"+(it.ColorNil?"nil":it.Color.ToString("x6"))+" ls"+it.LineSpacing+" lt"+it.LetterSpacing+(it.Kind==EtsSignItemKind.Mileage? " tgt'"+it.Target+"' "+it.Display:""))); }
}
