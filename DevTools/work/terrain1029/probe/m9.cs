using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// m9 gx gz r [name] : curve items near a point: model, nodes, heights, flags, length
static class P { static void Main(string[] a){ var w=new Win(9); var m=w.Map; float gx=float.Parse(a[0]), gz=float.Parse(a[1]), r=float.Parse(a[2]); string want=a.Length>3?a[3]:null;
 int sx=(int)Math.Floor(gx/4000), sz=(int)Math.Floor(gz/4000); for(int dx=-1;dx<=1;dx++) for(int dz=-1;dz<=1;dz++) w.Get(sx+dx,sz+dz);
 foreach(var s in w.Held.Values){ if(s==null) continue; foreach(var c in s.Items.OfType<EtsMapCurve>()){ var n0=w.Node(c.NodeUid); var n1=w.Node(c.ForwardNodeUid); if(n0==null) continue;
  float d=(float)Math.Sqrt((n0.Position[0]-gx)*(n0.Position[0]-gx)+(n0.Position[2]-gz)*(n0.Position[2]-gz)); if(d>r) continue; if(want!=null && !c.Name.Contains(want)) continue;
  var def=m.ModelDefinition(c.ModelToken);
  Console.WriteLine($"CURVE {c.Uid:x} model {c.Name} ({(def!=null?def.GetString("model_desc",""):"?")}) len {c.Length:0.0} n0 ({n0.Position[0]:0.0},{n0.Position[1]:0.0},{n0.Position[2]:0.0}) n1 {(n1==null?"-":$"({n1.Position[0]:0.0},{n1.Position[1]:0.0},{n1.Position[2]:0.0})")} flags {c.Flags:x} sub {c.SubcurveFlags} stretch {c.Stretch} scale {c.Scale} step {c.FixedStep} heights [{string.Join(",",c.Heights.Select(h=>h.ToString("0.0")))}] first {EtsToken.Decode(c.FirstPart)} last {EtsToken.Decode(c.LastPart)}"); } } } }
