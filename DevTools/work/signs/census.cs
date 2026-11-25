using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs); var lib = new EtsSignLibrary(fs); var art = new EtsSignArt(fs);
    EtsLocale.Load(fs);
    float gx=float.Parse(a[0]), gz=float.Parse(a[1]), R=float.Parse(a[2]);
    var hooksBy = new Dictionary<string,(EtsModelData,string[])>();
    int legacySigns=0, legacyWith=0, quads=0, missingMat=0, unhookedSlots=0, tplSigns=0, tplFaces=0, tplEmpty=0; double ms=0; var missingTok = new Dictionary<string,int>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    foreach (var s in map.SectorsAround(gx,gz,R)) foreach (var sg in s.Signs) {
      var n = map.FindNode(sg.NodeUid); if (n==null) continue; float dx=n.Position[0]-gx, dz=n.Position[2]-gz; if (dx*dx+dz*dz>R*R) continue;
      bool legacy = sg.Boards.Any(t=>t!=0); if (!legacy && sg.Template.Length==0) continue;
      var u = map.SignModel(sg.ModelToken); string md = u?.GetString("model_desc", null); if (md==null) continue;
      if (!hooksBy.TryGetValue(md, out var mh)) { var pmd=fs.Read(md); var pmg=fs.Read(md.Substring(0,md.Length-4)+".pmg"); if (pmd==null||pmg==null) continue; var m=EtsModelData.Parse(pmd,pmg,md); mh=(m, EtsSignPlanner.Hookups(pmg,m)); hooksBy[md]=mh; }
      var (model, hooks) = mh;
      if (legacy) { legacySigns++; int q0=quads; var used = new HashSet<int>(); if (Environment.GetEnvironmentVariable("V")!=null) Console.WriteLine("SIGN sign."+EtsToken.Decode(sg.ModelToken)+" at "+n.Position[0]+","+n.Position[2]+" hooks ["+string.Join(",",hooks.Select(h=>h??"-"))+"] boards "+string.Join(" ",sg.Boards.Select(t=>t==0?"-":EtsToken.Decode(t))));
        for (int i=0;i<hooks.Length;i++){ if (!lib.Overlay(hooks[i], out var ov)) continue; int slot=ov.Board*3+ov.Type; used.Add(slot); if (slot>=sg.Boards.Length||sg.Boards[slot]==0) continue; string tok=EtsToken.Decode(sg.Boards[slot]); if (!fs.Exists("/material/overlay/"+tok+".mat")) { missingMat++; missingTok[tok]=missingTok.TryGetValue(tok,out var c)?c+1:1; continue; } quads++; }
        for (int k=0;k<sg.Boards.Length;k++) if (sg.Boards[k]!=0 && !used.Contains(k)) unhookedSlots++;
        if (quads>q0) legacyWith++; else if (md.Contains("navigation")) Console.WriteLine("  nav sign with nothing drawn: sign."+EtsToken.Decode(sg.ModelToken)+" "+md+" hooks ["+string.Join(",",hooks.Where(h=>h!=null))+"] boards "+string.Join(" ",sg.Boards.Select(t=>t==0?"-":EtsToken.Decode(t)))); }
      if (sg.Template.Length>0) { tplSigns++; var t0=sw.Elapsed.TotalMilliseconds; var faces = EtsSignPlanner.Plan(lib, art, model, hooks, sg.Template, sg.Areas, sg.Overrides, out string note); ms += sw.Elapsed.TotalMilliseconds-t0; tplFaces+=faces.Count; if (faces.Count==0) { tplEmpty++; Console.WriteLine("  empty: "+sg.Template+" sign."+EtsToken.Decode(sg.ModelToken)+" "+note); } }
    }
    Console.WriteLine("legacy signs "+legacySigns+" with a drawn overlay "+legacyWith+", overlay quads "+quads+", picture missing "+missingMat+" ("+string.Join(", ", missingTok.OrderByDescending(k=>k.Value).Take(10).Select(k=>k.Key+" x"+k.Value))+"), board slots with no hookup on the model "+unhookedSlots);
    Console.WriteLine("template signs "+tplSigns+" faces "+tplFaces+" empty signs "+tplEmpty+" plan ms "+ms.ToString("0")+"; glyphs "+EtsSignRaster.GlyphsDrawn+" missing "+EtsSignRaster.GlyphsMissing+" ["+EtsSignRaster.MissingSample+"] skipped items "+EtsSignRaster.ItemsSkipped+"; no locator "+EtsSignPlanner.NoLocator+" no template "+EtsSignPlanner.NoTemplate);
  }
}
