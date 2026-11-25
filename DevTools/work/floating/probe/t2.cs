using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P { static void Main(string[] a){
 var w = new Win(2); var m=w.Map;
 foreach(var (x,z) in w.All()){ var s=w.Get(x,z); if(s==null) continue;
  foreach(var sg in s.Signs){ var texts=new List<string>(); foreach(var o in sg.Overrides) if(o.Attributes!=null) foreach(var at in o.Attributes) if(at.Text!=null) texts.Add(at.Text);
    string all=string.Join(" | ",texts).ToLower(); 
    if(all.Contains("salzburg") && all.Contains("graz")){ s.NodesByUid.TryGetValue(sg.NodeUid, out var nd); Console.WriteLine("sign "+sg.Uid.ToString("x")+" sec "+x+","+z+" at "+(nd==null?"?":nd.Position[0].ToString("0")+","+nd.Position[1].ToString("0")+","+nd.Position[2].ToString("0"))+" tpl "+sg.Template+" : "+string.Join(" | ",texts).Replace("\n"," ")); } }
 }
}}
