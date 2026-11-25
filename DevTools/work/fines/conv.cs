using System; using System.Collections.Generic; using System.Text; using UnityEngine; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static float[] Rot(float[] q, float[] v){ return EtsMapNode.Rotate(q, v[0], v[1], v[2]); }
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs); var cat = new EtsTrafficCatalog(fs);
    var pts = new[]{ (-33391.4f,-7420.0f,"Dover"), (14501f,3625f,"Prague"), (14994f,-15235f,"Szczecin") };
    var models = new Dictionary<ulong, List<(string name,float[] pos,float[] rot,string hook)>>();
    int[] camHist = new int[4]; int[] signHist = new int[3];
    foreach(var (gx,gz,name) in pts){
      var centre = new Vector3(gx,0,gz); var graph = new LaneGraphHost(map, fs, cat, centre);
      foreach(var sec in map.SectorsAround(gx,gz,3000f)){
        graph.G.BuildSector(sec.SectorX, sec.SectorZ);
      }
      foreach(var sec in map.SectorsAround(gx,gz,3000f)) foreach(var s in sec.Signs){
        var node = map.FindNode(s.NodeUid); if(node==null) continue;
        float dx=node.Position[0]-gx, dz=node.Position[2]-gz; if(dx*dx+dz*dz>3000f*3000f) continue;
        var unit = map.SignModel(s.ModelToken); if(unit==null) continue;
        string rule = unit.GetString("traffic_rule"); string desc = unit.GetString("model_desc");
        if(!models.TryGetValue(s.ModelToken, out var locs)){ locs = Locators(fs, desc); models[s.ModelToken]=locs; }
        var fwd = node.Forward(); // game frame (0,0,-1) rotated
        if(rule!=null && rule.StartsWith("traffic_rule.limit")){
          // nearest lane to the sign
          Vector3 u = graph.G.ToUnity(node.Position); if(graph.G.Match(u, Vector3.zero, 15f, -2f, out int lid, out float along)){ var ln=graph.G.Get(lid); var d=graph.G.DirectionAt(ln, along); float dot = d.x*fwd[0] + (-d.z)*fwd[2]; if(dot>0.7f) signHist[0]++; else if(dot<-0.7f) signHist[1]++; else signHist[2]++; }
        }
        foreach(var l in locs){ if(l.hook==null || !l.hook.StartsWith("speedcam")) continue;
          var wp = Rot(node.Rotation, l.pos); var p = new float[]{node.Position[0]+wp[0], node.Position[1]+wp[1], node.Position[2]+wp[2]};
          var lf = Rot(node.Rotation, Rot(l.rot, new float[]{0,0,-1}));
          Vector3 u = graph.G.ToUnity(p); string lane="no lane"; 
          if(graph.G.Match(u, Vector3.zero, 6f, -2f, out int lid, out float along)){ var ln=graph.G.Get(lid); var d=graph.G.DirectionAt(ln, along); var pp=graph.G.PointAt(ln,along); float dot = d.x*lf[0] + (-d.z)*lf[2];
             lane = "lane "+lid+" dist "+(pp-u).magnitude.ToString("0.0")+" dot(-Z,lane) "+dot.ToString("0.00")+" carlimit "+(ln.SpeedLimit*3.6f).ToString("0")+(ln.InPrefab?" prefab":"")+(ln.Urban?" urban":"");
             if(dot>0.7f) camHist[0]++; else if(dot<-0.7f) camHist[1]++; else camHist[2]++; } else camHist[3]++;
          Console.WriteLine(name+" cam sign "+s.Uid.ToString("x")+" "+unit.Name+" '"+unit.GetString("sign_name")+"' loc "+l.name+" hook "+l.hook+" at game ("+p[0].ToString("0.0")+", "+p[2].ToString("0.0")+") dist "+Math.Sqrt((p[0]-gx)*(p[0]-gx)+(p[2]-gz)*(p[2]-gz)).ToString("0")+" m; "+lane);
        }
      }
    }
    Console.WriteLine("camera -Z vs lane: along "+camHist[0]+" against "+camHist[1]+" across "+camHist[2]+" nolane "+camHist[3]);
    Console.WriteLine("limit sign node fwd vs nearest lane: along "+signHist[0]+" against "+signHist[1]+" across "+signHist[2]);
  }
  class LaneGraphHost { public EtsLaneGraph G; public LaneGraphHost(EtsMap m, EtsFileSystem f, EtsTrafficCatalog c, Vector3 centre){ G = new EtsLaneGraph(m,f,c,centre);} }
  public static List<(string,float[],float[],string)> Locators(EtsFileSystem fs, string desc){
    var res = new List<(string,float[],float[],string)>(); if(desc==null) return res;
    string stem = desc.EndsWith(".pmd")? desc.Substring(0, desc.Length-4) : desc; var b = fs.Read(stem+".pmg"); if(b==null||b.Length<112) return res;
    int lc=BitConverter.ToInt32(b,20), lo=BitConverter.ToInt32(b,80), so=BitConverter.ToInt32(b,88), ss=BitConverter.ToInt32(b,92);
    for(int i=0;i<lc;i++){ int o=lo+i*44; string n=EtsToken.Decode(BitConverter.ToUInt64(b,o)); var pos=new float[]{BitConverter.ToSingle(b,o+8),BitConverter.ToSingle(b,o+12),BitConverter.ToSingle(b,o+16)};
      var rot=new float[]{BitConverter.ToSingle(b,o+28),BitConverter.ToSingle(b,o+32),BitConverter.ToSingle(b,o+36),BitConverter.ToSingle(b,o+24)}; uint h=BitConverter.ToUInt32(b,o+40); string hook=null;
      if(h!=0xFFFFFFFF && so>0 && so+h<b.Length){ int e=(int)(so+h); int k=e; while(k<b.Length&&b[k]!=0) k++; hook=Encoding.ASCII.GetString(b,e,k-e); }
      res.Add((n,pos,rot,hook)); }
    return res; }
}
