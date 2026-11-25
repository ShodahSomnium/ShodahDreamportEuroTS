using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(2); var s=w.Get(int.Parse(a[0]),int.Parse(a[1]));
 foreach(var t in s.Terrains){ string u=t.Uid.ToString("x"); if(a.Length>2 && !a.Skip(2).Contains(u)) continue;
  foreach(var (nm,sd) in new[]{("R",t.Right),("L",t.Left)}){ var q=sd.Quads; if(q==null||q.Quads.Length==0) continue;
   var hist=new Dictionary<string,int>(); for(int i=0;i<q.Quads.Length;i+=4){ string k=q.Quads[i].ToString("x2")+" "+q.Quads[i+1].ToString("x2")+" "+q.Quads[i+2].ToString("x2")+" "+q.Quads[i+3].ToString("x2"); hist.TryGetValue(k,out int n); hist[k]=n+1; }
   Console.WriteLine(u+" "+nm+" colors "+(q.Colors.Length/4)+" ["+string.Join(" ",Enumerable.Range(0,Math.Min(8,q.Colors.Length/4)).Select(i=>q.Colors[i*4].ToString("x2")+q.Colors[i*4+1].ToString("x2")+q.Colors[i*4+2].ToString("x2")+q.Colors[i*4+3].ToString("x2")))+"] mats "+string.Join(",",q.MaterialTokens.Select(EtsToken.Decode))+" rot "+string.Join(",",q.MaterialRotations)+" quads: "+string.Join("; ",hist.OrderByDescending(k=>k.Value).Take(6).Select(k=>k.Key+" x"+k.Value)));
  } }
}}
