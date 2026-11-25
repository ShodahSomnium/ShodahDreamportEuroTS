using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static int RC(EtsMapNode s, EtsMapNode e){ int a=s!=null?(int)((s.Flags>>8)&0xFF):0; if(a!=0) return a; int b=e!=null?(int)((e.Flags>>16)&0xFF):0; if(b!=0) return b; int c=s!=null?(int)((s.Flags>>16)&0xFF):0; if(c!=0) return c; return e!=null?(int)((e.Flags>>8)&0xFF):0; }
 static void Main(string[] a){
 var w = new Win(2); var m=w.Map;
 var cnt=new Dictionary<int,int>(); var bb=new Dictionary<int,float[]>(); var looks=new Dictionary<string,double>();
 var cities=new List<string>();
 var outp=new System.IO.StreamWriter("t3_roads.txt");
 foreach(var (x,z) in w.All()){ var s=w.Get(x,z); if(s==null) continue;
  foreach(var c in s.Cities){ s.NodesByUid.TryGetValue(c.NodeUid,out var nd); var d=m.City(c.CityToken); cities.Add((d!=null?d.GetString("country","?"):"?")+" "+EtsToken.Decode(c.CityToken)+" "+(nd==null?"?":nd.Position[0].ToString("0")+","+nd.Position[2].ToString("0"))); }
  foreach(var r in s.Roads){ s.NodesByUid.TryGetValue(r.StartNode,out var n0); s.NodesByUid.TryGetValue(r.EndNode,out var n1); int cc=RC(n0,n1); cnt.TryGetValue(cc,out int k); cnt[cc]=k+1;
    if(n0==null) continue; if(!bb.ContainsKey(cc)) bb[cc]=new float[]{1e9f,1e9f,-1e9f,-1e9f}; var B=bb[cc]; B[0]=Math.Min(B[0],n0.Position[0]); B[1]=Math.Min(B[1],n0.Position[2]); B[2]=Math.Max(B[2],n0.Position[0]); B[3]=Math.Max(B[3],n0.Position[2]);
    var lk=m.RoadLook(r.LookToken); string ln=lk!=null?lk.GetString("name",""):"?"; looks.TryGetValue(cc+" "+ln,out double L); looks[cc+" "+ln]=L+r.Length;
    if(n1!=null) outp.WriteLine(cc+"\t"+r.Uid.ToString("x")+"\t"+n0.Position[0].ToString("0.0")+"\t"+n0.Position[1].ToString("0.0")+"\t"+n0.Position[2].ToString("0.0")+"\t"+n1.Position[0].ToString("0.0")+"\t"+n1.Position[1].ToString("0.0")+"\t"+n1.Position[2].ToString("0.0")+"\t"+r.Length.ToString("0")+"\t"+ln); }
 }
 outp.Close();
 foreach(var kv in cnt.OrderBy(k=>k.Key)) { var B=bb.ContainsKey(kv.Key)?bb[kv.Key]:null; Console.WriteLine("country "+kv.Key+": roads "+kv.Value+(B!=null?" bbox "+B[0].ToString("0")+","+B[1].ToString("0")+" .. "+B[2].ToString("0")+","+B[3].ToString("0"):"")); }
 foreach(var c in cities.OrderBy(q=>q)) Console.WriteLine("city "+c);
 foreach(var kv in looks.OrderByDescending(k=>k.Value).Take(80)) Console.WriteLine("look "+kv.Key+" "+(kv.Value/1000).ToString("0.0")+" km");
}}
