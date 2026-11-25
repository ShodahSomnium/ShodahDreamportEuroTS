using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(2); var hb=new Dictionary<string,int>(); void H(string k){ hb.TryGetValue(k,out int n); hb[k]=n+1; }
 var s=w.Get(4,4);
 foreach(var t in s.Terrains){ string prof=EtsToken.Decode(t.Right.ProfileToken); H("terrain vd "+t.ViewDistance+(prof=="profile114"||prof=="profile112"?" "+prof:"")); }
 foreach(var t in s.Roads) H("road vd "+t.ViewDistance);
 foreach(var t in s.Models) H("model vd "+t.ViewDistance);
 foreach(var t in s.Terrains) if(t.Uid==0x42f33d6841c50001UL) Console.WriteLine("hill vd "+t.ViewDistance+" body "+t.BodyLength);
 foreach(var kv in hb.OrderBy(k=>k.Key)) Console.WriteLine(kv.Value+"\t"+kv.Key);
}}
