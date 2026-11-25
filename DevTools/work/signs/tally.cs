using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs); var lib = new EtsSignLibrary(fs);
    var tally = new Dictionary<string,int>(); var samples = new Dictionary<string,List<string>>();
    int found=0, missing=0, noTpl=0;
    foreach (var name in map.AvailableSectors) {
      string t10 = name.Substring(name.Length-10); int sx = int.Parse(t10.Substring(0,5)), sz = int.Parse(t10.Substring(5,5));
      var bb = fs.Read(EtsMapSector.FileName(sx, sz, "base")); var ab = fs.Read(EtsMapSector.FileName(sx, sz, "aux")); var dbb = fs.Read(EtsMapSector.FileName(sx, sz, "data")); if (bb==null && ab==null) continue;
      var s = EtsMapSector.Parse(sx, sz, bb, ab, dbb);
      foreach (var sg in s.Signs) { if (sg.Overrides.Length==0) continue; var tpl = lib.Template(sg.Template); if (tpl==null){noTpl++; continue;}
        var items = new Dictionary<uint,EtsSignItem>(); foreach (var it in tpl.Items) items[it.Id]=it;
        foreach (var bd in tpl.Bindings) { var board = lib.Board(bd.Value); if (board!=null) foreach (var it in board.Items) items[it.Id]=it; }
        foreach (var ar in sg.Areas) if ((ar.Flags&2)!=0) { var board = lib.Board("sign_board."+EtsToken.Decode(ar.Board)); if (board!=null) foreach (var it in board.Items) items[it.Id]=it; }
        foreach (var ov in sg.Overrides) { if (!items.TryGetValue(ov.Id, out var item)) { missing++; continue; } found++;
          foreach (var at in ov.Attributes) { string k = item.Kind+" "+at.Type+":"+at.Index; tally[k] = tally.TryGetValue(k, out var c)? c+1:1;
            string val = at.Type==5? at.Text : at.Type==4? at.Float.ToString("0.###") : at.Type==6? EtsToken.Decode(at.Token) : at.Number.ToString();
            string tv = item.Kind==EtsSignItemKind.Image? "img idx"+item.ImageIdx+" col"+(item.ColorNil?"nil":item.Color.ToString())+" op"+item.Opacity+" hf"+item.HFlip : item.Kind==EtsSignItemKind.Frame? "frm col"+(item.ColorNil?"nil":item.Color.ToString())+" op"+item.Opacity : "txt '"+item.Text+"' sz"+item.FontSize+" col"+(item.ColorNil?"nil":item.Color.ToString())+" op"+item.Opacity+" ha"+item.HAlign+" va"+item.VAlign+" ls"+item.LineSpacing+" lt"+item.LetterSpacing+(item.Kind==EtsSignItemKind.Mileage?" tg'"+item.Target+"' d"+item.Display+" v"+item.VariantIdx+" su"+item.ShowUnits+" up"+item.Uppercase:"");
            if (!samples.TryGetValue(k, out var l)) samples[k] = l = new List<string>(); if (l.Count<6) l.Add(val+"  <- "+tv); } } }
    }
    Console.WriteLine("overrides matched "+found+" unmatched "+missing+" signs without template "+noTpl+"; templates indexed "+lib.TemplateCount+" boards "+lib.BoardCount);
    foreach (var kv in tally.OrderBy(k=>k.Key)) { Console.WriteLine(kv.Key+"  x"+kv.Value); foreach (var smp in samples[kv.Key]) Console.WriteLine("      "+smp); }
  }
}
