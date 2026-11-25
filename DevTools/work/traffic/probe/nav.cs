using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var defs = new EtsSiiFile(); fs.TryList("/def/world", out var files, out _);
    foreach(var f in files) if(f.StartsWith("prefab")&&f.EndsWith(".sii")){ var t=fs.ReadText("/def/world/"+f); if(t!=null) defs.AddAll(EtsSii.Parse(t,f,fs.ReadText)); }
    var paths=new HashSet<string>(); foreach(var u in defs.Units){ var d=u.GetString("prefab_desc"); if(d!=null) paths.Add(d); }
    Console.WriteLine(paths.Count+" descriptors"); if(a.Length>1) foreach(var p in paths.Where(x=>x.Contains(a[1])).Take(40)) Console.WriteLine(p);
    var bit=new int[32]; var nib=new Dictionary<string,int>(); int curves=0, nd=0;
    var examples=new List<string>();
    foreach(var p in paths){ var nav=EtsPrefabNav.Parse(fs.Read(p), out _); if(nav==null) continue; nd++;
      foreach(var c in nav.Curves){ curves++; for(int k=0;k<32;k++) if((c.Flags>>k&1)!=0) bit[k]++;
        string key="b7-12:"+((c.Flags>>7)&63)+" b14-15:"+((c.Flags>>14)&3)+" n16:"+((c.Flags>>16)&15)+" n20:"+((c.Flags>>20)&15)+" b24+:"+(c.Flags>>24);
        nib[key]=nib.TryGetValue(key,out var n)?n+1:1; }
      if(a.Length>0 && p.Contains(a[0]) && examples.Count<3 && !p.Contains("_uk")){ examples.Add(p); }
    }
    Console.WriteLine(nd+" parsed, "+curves+" curves"); for(int k=0;k<32;k++) Console.Write(k+":"+bit[k]+" "); Console.WriteLine();
    foreach(var kv in nib.OrderByDescending(k=>k.Value).Take(40)) Console.WriteLine(kv.Value+"  "+kv.Key);
    foreach(var p in examples){ var nav=EtsPrefabNav.Parse(fs.Read(p), out _); Console.WriteLine("== "+p+" nodes "+nav.NodePosition.Length);
      foreach(var c in nav.Curves){ int inNode=-1; for(int n=0;n<nav.NodeInputs.Length;n++) if(Array.IndexOf(nav.NodeInputs[n],c.Index)>=0) inNode=n; Console.WriteLine("  c"+c.Index+" in"+inNode+" out"+nav.OutputNodeOf[c.Index]+" flags 0x"+c.Flags.ToString("x8")+" next "+string.Join(",",c.Next)+" sem "+c.Semaphore+" rule "+EtsToken.Decode(c.TrafficRule)+" start "+string.Join(",",c.Start.Select(x=>x.ToString("0.0")))+" end "+string.Join(",",c.End.Select(x=>x.ToString("0.0")))); } }
  }
}
