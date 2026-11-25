using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// s2: what PreviousLength is: compare with the chain (backward item's road, its PreviousLength + Length).
static class P { static void Main(string[] a){
 var w=new Win(9); int n=0, zero=0, chain=0, chainRev=0, other=0; var ex=new List<string>();
 foreach(var (x,z) in w.All()){ var s=w.Get(x,z); if(s==null) continue; var byUid=s.Roads.ToDictionary(r=>r.Uid);
  foreach(var r in s.Roads){ n++; if(r.PreviousLength==0) zero++;
    var st=s.NodesByUid.TryGetValue(r.StartNode,out var sn)?sn:null; if(st==null) continue;
    EtsMapRoad prev=null; if(st.BackwardItem!=0) byUid.TryGetValue(st.BackwardItem,out prev);
    if(prev!=null){ float exp=prev.PreviousLength+prev.Length; if(Math.Abs(exp-r.PreviousLength)<0.05f) chain++; else { other++; if(ex.Count<15) ex.Add($"road {r.Uid:x} prevLen {r.PreviousLength:0.00} len {r.Length:0.00}; backward road {prev.Uid:x} prevLen {prev.PreviousLength:0.00} len {prev.Length:0.00} sum {exp:0.00} sideModels {(r.SideModels!=null)}"); } }
  } if(n>200000) break; }
 Console.WriteLine($"roads {n} prevLen==0 {zero} chain-matches {chain} other {other}"); foreach(var e in ex) Console.WriteLine(e);
}}
