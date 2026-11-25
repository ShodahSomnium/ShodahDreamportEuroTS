// sitems: every type-21 (sound) map item in the demo: body length histogram, decoded token/node/width/height, flags
using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    fs.TryList("/map/europe", out var files, out _); var secs=files.Where(f=>f.EndsWith(".base")).Select(f=>f.Substring(0,f.Length-5)).ToList();
    var lens=new Dictionary<int,int>(); var toks=new Dictionary<string,int>(); var flags=new Dictionary<uint,int>(); int n=0, nodeOk=0; int shown=0;
    foreach (var name in secs) {
      string t10 = name.Substring(name.Length-10); int sx = int.Parse(t10.Substring(0,5)), sz = int.Parse(t10.Substring(5,5));
      foreach(var ext in new[]{"snd"}){
        var bb = fs.Read(EtsMapSector.FileName(sx, sz, ext)); if(bb==null) continue;
        var s = EtsMapSector.Parse(sx, sz, bb, null, null);
        foreach(var it in s.Items){ if(it.Type!=21) continue; n++; lens[it.BodyLength]=lens.TryGetValue(it.BodyLength,out var c)?c+1:1;
          int o=it.BodyOffset; ulong tok=BitConverter.ToUInt64(bb,o); ulong node=BitConverter.ToUInt64(bb,o+8); float w=BitConverter.ToSingle(bb,o+16), h=BitConverter.ToSingle(bb,o+20);
          string tn=EtsToken.Decode(tok); toks[tn]=toks.TryGetValue(tn,out var k)?k+1:1; flags[it.Flags]=flags.TryGetValue(it.Flags,out var f)?f+1:1;
          bool has=s.NodesByUid.TryGetValue(node,out var nd); if(has) nodeOk++;
          if(shown++<25) Console.WriteLine(name+" "+ext+" len "+it.BodyLength+" tok "+tn+" node "+(has?string.Join(",",nd.Position.Select(x=>x.ToString("0"))):"?")+" w "+w+" h "+h+" flags "+it.Flags.ToString("x8")+" vd "+it.ViewDistance+" kdop "+string.Join(",",it.KdopMin.Take(3).Select(x=>x.ToString("0")))+" .. "+string.Join(",",it.KdopMax.Take(3).Select(x=>x.ToString("0"))));
        }
      }
    }
    Console.WriteLine("sound items "+n+" node in same file "+nodeOk);
    foreach(var kv in lens) Console.WriteLine("len "+kv.Key+": "+kv.Value);
    foreach(var kv in flags.OrderByDescending(x=>x.Value).Take(12)) Console.WriteLine("flags "+kv.Key.ToString("x8")+": "+kv.Value);
    foreach(var kv in toks.OrderByDescending(x=>x.Value)) Console.Write(kv.Key+"="+kv.Value+" "); Console.WriteLine();
  }
}
