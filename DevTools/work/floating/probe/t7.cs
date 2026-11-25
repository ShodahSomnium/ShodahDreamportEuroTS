using System; using System.Collections.Generic; using System.Linq; using System.Globalization; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(2); var s=w.Get(int.Parse(a[0]),int.Parse(a[1]));
 var hb=new Dictionary<string,int>(); void H(string k){ hb.TryGetValue(k,out int n); hb[k]=n+1; }
 string Pre(ulong u)=> (u>>32).ToString("x8");
 foreach(var u in s.GuardDropped) H(Pre(u)+" DROPPED");
 foreach(var it in s.Items) H(Pre(it.Uid)+" type "+it.Type);
 foreach(var kv in hb.OrderBy(k=>k.Key)) Console.WriteLine(kv.Value+"\t"+kv.Key);
}}
