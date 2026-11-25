using System; using System.Collections.Generic; using System.Linq; using System.Globalization; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// args: centreX centreY centreZ then name:gx:gz:dist:side ...  -> RL_SHOTS string (Unity coords of the lab site)
static class P {
  static float[] Rot(float[] q, float[] v){ float x=q[0],y=q[1],z=q[2],w=q[3]; float[] u={x,y,z}; float[] t={2*(y*v[2]-z*v[1]),2*(z*v[0]-x*v[2]),2*(x*v[1]-y*v[0])}; return new[]{ v[0]+w*t[0]+(y*t[2]-z*t[1]), v[1]+w*t[1]+(z*t[0]-x*t[2]), v[2]+w*t[2]+(x*t[1]-y*t[0]) }; }
  static void Main(string[] a){
    var I = CultureInfo.InvariantCulture;
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs);
    float cx=float.Parse(a[0],I), cy=float.Parse(a[1],I), cz=float.Parse(a[2],I);
    var parts = new List<string>();
    for (int k=3;k<a.Length;k++){
      var f=a[k].Split(':'); string name=f[0]; float gx=float.Parse(f[1],I), gz=float.Parse(f[2],I), D=float.Parse(f[3],I); float side = f.Length>4? float.Parse(f[4],I):0f;
      EtsMapSign best=null; EtsMapNode bn=null; float bd=1e9f;
      foreach (var s in map.SectorsAround(gx,gz,50)) foreach (var sg in s.Signs){ var n=map.FindNode(sg.NodeUid); if(n==null) continue; if (sg.Template.Length==0 && !sg.Boards.Any(t=>t!=0)) continue; float d=(n.Position[0]-gx)*(n.Position[0]-gx)+(n.Position[2]-gz)*(n.Position[2]-gz); if(d<bd){bd=d;best=sg;bn=n;} }
      if (best==null){ Console.Error.WriteLine(name+": none"); continue; }
      var u = map.SignModel(best.ModelToken); string md=u.GetString("model_desc",null);
      var model = EtsModelData.Parse(fs.Read(md), fs.Read(md.Substring(0,md.Length-4)+".pmg"), md);
      // board centre: mean of the locators that are not flares/anchors
      float[] c={0,0,0}; int nl=0; foreach (var l in model.Locators){ if (l.Name.StartsWith("znl")||l.Name=="anchor") continue; c[0]+=l.Position[0]; c[1]+=l.Position[1]; c[2]+=l.Position[2]; nl++; }
      if (nl>0){ c[0]/=nl; c[1]/=nl; c[2]/=nl; } else { c[1]=2.5f; }
      var q = bn.Rotation; var tc = Rot(q, c); float[] target = { bn.Position[0]+tc[0], bn.Position[1]+tc[1], bn.Position[2]+tc[2] };
      var face = Rot(q, new float[]{0,0,1}); var right = Rot(q, new float[]{1,0,0});
      float[] cam = { target[0]+face[0]*D+right[0]*side, target[1]-0.6f, target[2]+face[2]*D+right[2]*side };
      // unity
      float ux=cam[0]-cx, uy=cam[1]-cy, uz=-(cam[2]-cz); float tx=target[0]-cx, ty=target[1]-cy, tz=-(target[2]-cz);
      float dx=tx-ux, dy=ty-uy, dz=tz-uz; float h=(float)Math.Sqrt(dx*dx+dz*dz);
      float yaw=(float)(Math.Atan2(dx,dz)*180/Math.PI), pitch=(float)(-Math.Atan2(dy,h)*180/Math.PI);
      Console.Error.WriteLine(name+": sign."+EtsToken.Decode(best.ModelToken)+" "+best.Template+" at game ("+bn.Position[0].ToString("0.0",I)+", "+bn.Position[1].ToString("0.0",I)+", "+bn.Position[2].ToString("0.0",I)+") board centre "+string.Join(",",c.Select(v=>v.ToString("0.00",I)))+" faces ("+face[0].ToString("0.00",I)+","+face[2].ToString("0.00",I)+")");
      parts.Add(name+","+ux.ToString("0.00",I)+","+uy.ToString("0.00",I)+","+uz.ToString("0.00",I)+","+yaw.ToString("0.0",I)+","+pitch.ToString("0.0",I));
    }
    Console.WriteLine(string.Join(";",parts));
  }
}
