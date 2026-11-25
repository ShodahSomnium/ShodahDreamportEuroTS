using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P4 { static void Main(){ Lib.Mount(); int roads=0, railed=0, ds=0, dup=0, sectors=0; var runKeys=new Dictionary<string,int>(); int runs=0, runDups=0, revDups=0; var samples=new List<string>();
  foreach(var name in Lib.Map.AvailableSectors.ToList()){ // "sec+0003-0004"
    int sx=int.Parse(name.Substring(3,5)), sz=int.Parse(name.Substring(8,5)); EtsMapSector s; try{ s=Lib.Map.GetSector(sx,sz);}catch{continue;} if(s==null) continue; sectors++;
    foreach(var r in s.Roads){ roads++; if(r.Railings==null) continue; railed++; foreach(var side in new[]{false,true}){ var l=r.Railings.Where(x=>x.Left==side).ToList(); if(l.Count==0) continue; if(side? r.LeftRailingDoubleSided : r.RightRailingDoubleSided) ds++; for(int i=0;i<l.Count;i++) for(int j=i+1;j<l.Count;j++) if(l[i].Model==l[j].Model && Math.Abs(l[i].Offset-l[j].Offset)<0.05f) dup++; } }
    foreach(var b in s.Buildings){ runs++; string k="b"+b.SchemeToken+"_"+b.StartNode+"_"+b.EndNode, kr="b"+b.SchemeToken+"_"+b.EndNode+"_"+b.StartNode; if(runKeys.ContainsKey(k)) runDups++; else if(runKeys.ContainsKey(kr)) revDups++; runKeys[k]=1; }
    foreach(var c in s.Curves){ runs++; string k="c"+c.ModelToken+"_"+c.NodeUid+"_"+c.ForwardNodeUid, kr="c"+c.ModelToken+"_"+c.ForwardNodeUid+"_"+c.NodeUid; if(runKeys.ContainsKey(k)) runDups++; else if(runKeys.ContainsKey(kr)) revDups++; runKeys[k]=1; }
  }
  Console.WriteLine("sectors "+sectors+", roads "+roads+", with railings "+railed+", double-sided railing sides "+ds+", duplicate railing slots (same model, side, offset) "+dup);
  Console.WriteLine("runs "+runs+": same model on the same two nodes twice "+runDups+", reversed "+revDups);
}}
