using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(1); int sx=int.Parse(a[0]), sz=int.Parse(a[1]); int type=int.Parse(a[2]); var s=w.Get(sx,sz);
 var bb=w.Fs.Read(EtsMapSector.FileName(sx,sz,"base")); var ba=w.Fs.Read(EtsMapSector.FileName(sx,sz,"aux")); var bd=w.Fs.Read(EtsMapSector.FileName(sx,sz,"data"));
 var uids=new HashSet<ulong>(s.Items.Select(i=>i.Uid)); var tuids=new HashSet<ulong>(s.Terrains.Select(i=>i.Uid));
 foreach(var it in s.Items.Where(i=>i.Type==type)){
  byte[] b=null; foreach(var cand in new[]{bb,ba}) if(cand!=null && it.BodyOffset-53>=0 && it.BodyOffset-53+8<=cand.Length && BitConverter.ToUInt64(cand,it.BodyOffset-53)==it.Uid) b=cand;
  Console.WriteLine($"type {it.Type} uid {it.Uid:x} flags {it.Flags:x8} body {it.BodyLength} scanned {it.BodyScanned} kdop {string.Join(",",it.KdopMin.Select(f=>f.ToString("0")))} .. {string.Join(",",it.KdopMax.Select(f=>f.ToString("0")))} file {(b==bb?"base":b==ba?"aux":"?")}");
  if(b==null) continue; int o=it.BodyOffset; int n=Math.Min(it.BodyLength,400);
  Console.WriteLine("  hex "+BitConverter.ToString(b,o,Math.Min(n,96)));
  // scan u64 aligned at any offset for known uids
  for(int k=0;k+8<=it.BodyLength;k++){ ulong u=BitConverter.ToUInt64(b,o+k); if(uids.Contains(u)) { var x=s.Items.First(i=>i.Uid==u); Console.WriteLine($"   +{k}: refs item {u:x} type {x.Type}{(tuids.Contains(u)?" TERRAIN":"")}"); } else if(s.NodesByUid.ContainsKey(u)) { var nd=s.NodesByUid[u]; Console.WriteLine($"   +{k}: node {u:x} at {nd.Position[0]:0},{nd.Position[1]:0},{nd.Position[2]:0}"); } }
 }
}}
