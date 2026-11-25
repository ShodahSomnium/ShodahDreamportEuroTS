using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(1); var want=new HashSet<ulong>(a.Select(x=>Convert.ToUInt64(x,16)));
 var hist=new Dictionary<string,int>(); void H(string k){ hist.TryGetValue(k,out int n); hist[k]=n+1; }
 int areas=0; var childType=new Dictionary<ulong,int>();
 foreach(var (sx,sz) in w.All()){ var s=w.Get(sx,sz); if(s==null) continue;
  var bb=w.Fs.Read(EtsMapSector.FileName(sx,sz,"base")); var ba=w.Fs.Read(EtsMapSector.FileName(sx,sz,"aux"));
  var types=new Dictionary<ulong,int>(); foreach(var it in s.Items) types[it.Uid]=it.Type;
  foreach(var it in s.Items.Where(i=>i.Type==48)){
   byte[] b=null; foreach(var cand in new[]{bb,ba}) if(cand!=null && it.BodyOffset-53>=0 && it.BodyOffset-53+8<=cand.Length && BitConverter.ToUInt64(cand,it.BodyOffset-53)==it.Uid) b=cand; if(b==null) continue;
   int o=it.BodyOffset; ulong node=BitConverter.ToUInt64(b,o); float wdt=BitConverter.ToSingle(b,o+8), hgt=BitConverter.ToSingle(b,o+12); uint n=BitConverter.ToUInt32(b,o+16); areas++;
   if(20+n*8!=it.BodyLength){ H("layout mismatch body "+it.BodyLength+" n "+n); continue; }
   H("area flags "+it.Flags.ToString("x")+" ");
   for(int i=0;i<n;i++){ ulong c=BitConverter.ToUInt64(b,o+20+i*8); types.TryGetValue(c,out int t); H("flags "+it.Flags.ToString("x")+" child type "+(t==0?"elsewhere":t.ToString()));
     if(want.Contains(c)){ s.NodesByUid.TryGetValue(node,out var nd); Console.WriteLine($"{c:x} is child of area {it.Uid:x} flags {it.Flags:x} sec {sx},{sz} node {(nd==null?"?":nd.Position[0].ToString("0")+","+nd.Position[2].ToString("0"))} w {wdt:0} h {hgt:0} n {n}"); } }
  } }
 Console.WriteLine("areas "+areas); foreach(var kv in hist.OrderByDescending(k=>k.Value)) Console.WriteLine(kv.Value+"\t"+kv.Key);
}}
