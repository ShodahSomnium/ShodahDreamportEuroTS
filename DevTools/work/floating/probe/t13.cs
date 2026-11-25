using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(2); int sx=int.Parse(a[0]), sz=int.Parse(a[1]); var s=w.Get(sx,sz); var bb=w.Fs.Read(EtsMapSector.FileName(sx,sz,"base")); var ba=w.Fs.Read(EtsMapSector.FileName(sx,sz,"aux"));
 var hb=new Dictionary<string,int>(); void H(string k){ hb.TryGetValue(k,out int n); hb[k]=n+1; }
 foreach(var t in s.Terrains){ int o=t.BodyOffset; int sideAt=82; byte[] b = (o-53>=0 && o-53+8<=bb.Length && BitConverter.ToUInt64(bb,o-53)==t.Uid) ? bb : ba; if(o-53+8>b.Length || BitConverter.ToUInt64(b,o-53)!=t.Uid) continue; string line=t.Uid.ToString("x")+" ";
   for(int si=0;si<2;si++){ int p=o+sideAt+si*78; ulong tok=BitConverter.ToUInt64(b,p+14); float f=BitConverter.ToSingle(b,p+22); line+=(si==0?"R":"L")+" size "+(BitConverter.ToUInt16(b,p)/10f)+" prof "+EtsToken.Decode(BitConverter.ToUInt64(b,p+2))+" coef "+BitConverter.ToSingle(b,p+10)+" tok2 "+(tok==0?"0":EtsToken.Decode(tok))+" f2 "+f+" | ";
     H("tok2 "+(tok==0?"0":EtsToken.Decode(tok))+" f2 "+f); }
   if(a.Length>2 && line.Contains(a[2])) Console.WriteLine(line);
 }
 foreach(var kv in hb.OrderByDescending(k=>k.Value).Take(30)) Console.WriteLine(kv.Value+"\t"+kv.Key);
}}
