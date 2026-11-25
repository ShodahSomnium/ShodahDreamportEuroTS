using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(2); var hb=new Dictionary<string,int>(); var ex=new Dictionary<string,string>();
 void H(string k,string e){ hb.TryGetValue(k,out int n); hb[k]=n+1; if(!ex.ContainsKey(k)) ex[k]=e; }
 foreach(var (x,z) in w.All()){ var s=w.Get(x,z); if(s==null) continue;
  foreach(var t in s.Terrains){ bool nw=(t.Uid & 0xFFFF) < 0x40; string prof=EtsToken.Decode(t.Right.ProfileToken)+"*"+(t.Right.Coefficient<0?"neg":"pos");
    H((nw?"NEWUID ":"old ")+"flags "+t.Flags.ToString("x8"), t.Uid.ToString("x")+" sec "+x+","+z+" "+prof+" size "+t.Right.Size);
    H((nw?"NEWUID ":"old ")+"coef "+(t.Right.Coefficient<0?"neg":"pos")+" prof "+EtsToken.Decode(t.Right.ProfileToken), t.Uid.ToString("x")+" sec "+x+","+z);
  } }
 foreach(var kv in hb.OrderByDescending(k=>k.Value).Take(70)) Console.WriteLine(kv.Value+"\t"+kv.Key+"\te.g. "+ex[kv.Key]);
}}
