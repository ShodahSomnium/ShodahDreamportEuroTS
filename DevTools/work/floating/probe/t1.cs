using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(2); var m=w.Map;
 var hist=new Dictionary<string,int>(); var ex=new Dictionary<string,string>();
 void H(string k,string e){ hist.TryGetValue(k,out int n); hist[k]=n+1; if(!ex.ContainsKey(k)) ex[k]=e; }
 long[] fb=new long[32], rb=new long[32], tfb=new long[32], trb=new long[32]; long nRoads=0,nT=0;
 foreach(var (x,z) in w.All()){ var s=w.Get(x,z); if(s==null) continue;
  foreach(var r in s.Roads){ nRoads++; var lk=m.RoadLook(r.LookToken); string ln=lk!=null?lk.Name+" \""+lk.GetString("name","")+"\"":"?"+EtsToken.Decode(r.LookToken);
    bool tun = ln.ToLower().Contains("tunnel");
    foreach(var et in new[]{r.RightEdgeToken,r.LeftEdgeToken,r.RightInnerEdgeToken,r.LeftInnerEdgeToken,r.LeftOuterEdgeToken}){ if(et==0) continue; string en=EtsToken.Decode(et); if(en.Contains("tun")) { tun=true; H("edge "+en, r.Uid.ToString("x")+" sec "+x+","+z);} }
    if(r.Railings!=null) foreach(var rl in r.Railings){ string rn=rl.Name; if(rn.Contains("tun")){ tun=true; H("railing "+rn, r.Uid.ToString("x"));} }
    for(int b=0;b<32;b++){ if((r.Flags>>b&1)!=0){ fb[b]++; if(tun) tfb[b]++; } if((r.RoadFlags>>b&1)!=0){ rb[b]++; if(tun) trb[b]++; } }
    if(tun){ nT++; H("look "+ln, r.Uid.ToString("x")+" sec "+x+","+z); } }
  foreach(var md in s.Models){ string n=md.Name; var d=m.ModelDefinition(md.ModelToken); string p=d!=null?d.GetString("model_desc",""):""; if((n+p).Contains("tun")) H("model "+n+" "+p, md.Uid.ToString("x")+" sec "+x+","+z); }
  foreach(var pf in s.Prefabs){ var d=m.PrefabDefinition(pf.PrefabToken); string p=d!=null?d.GetString("prefab_desc",""):""; if((pf.Name+p).Contains("tun")) H("prefab "+pf.Name+" "+p, pf.Uid.ToString("x")+" sec "+x+","+z); }
  foreach(var it in s.Items) if(it.Type==EtsMapItem.CutPlane) H("cutplane", it.Uid.ToString("x")+" sec "+x+","+z+" kdop "+string.Join(",",it.KdopMin.Select(f=>f.ToString("0")))+" .. "+string.Join(",",it.KdopMax.Select(f=>f.ToString("0"))));
 }
 Console.WriteLine("roads "+nRoads+" tunnel-ish "+nT);
 for(int b=0;b<32;b++) Console.WriteLine($"bit {b}: Flags all {fb[b]} tunnel {tfb[b]}   RoadFlags all {rb[b]} tunnel {trb[b]}");
 foreach(var kv in hist.OrderByDescending(k=>k.Value)) Console.WriteLine(kv.Value+"  "+kv.Key+"   e.g. "+ex[kv.Key]);
}}
