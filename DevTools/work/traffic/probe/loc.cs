using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static EtsFileSystem fs;
  static EtsSiiFile Sii(string p){ var t=fs.ReadText(p); return t==null?null:EtsSii.Parse(t,p,fs.ReadText); }
  static void Show(string model){
    var pmd=fs.Read(model); var pmg=fs.Read(model.Substring(0,model.Length-4)+".pmg"); if(pmd==null||pmg==null){Console.WriteLine("  missing "+model);return;}
    var m=EtsModelData.Parse(pmd,pmg,model);
    Console.WriteLine("  "+model+" bounds "+string.Join(",",m.BoundsMin.Select(x=>x.ToString("0.00")))+" .. "+string.Join(",",m.BoundsMax.Select(x=>x.ToString("0.00")))+" variants "+string.Join("|",m.Variants)+" looks "+m.LookMaterials.Length);
    foreach(var l in m.Locators) Console.WriteLine("    loc "+l.Name+" @ "+string.Join(",",l.Position.Select(x=>x.ToString("0.00"))));
  }
  static void Main(string[] a){
    fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    foreach(var p in a){ if(p.EndsWith(".pmd")) Show(p); else { var f=Sii(p); foreach(var u in f.Units){ var mdl=u.GetString("model"); Console.WriteLine(u.Name+" model "+mdl+" lods "+u.GetArray("lods").Count); if(mdl!=null) Show(mdl); var l=u.GetArray("lods"); if(l.Count>0) Show(l[l.Count-1]); } } }
  }
}
