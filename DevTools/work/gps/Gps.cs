using System; using System.Collections.Generic; using System.IO; using System.IO.Compression;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets; using UnityEngine;
static class Gps {
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs);
    var cg = new Vector3(-33391.4f, 24f, -7420f);
    var secs = new List<EtsMapSector>();
    EtsMapSector.SectorOf(cg.x, cg.z, out int sx, out int sz);
    for(int x=sx-1;x<=sx+1;x++)for(int z=sz-1;z<=sz+1;z++){var s=map.GetSector(x,z); if(s!=null)secs.Add(s);}
    var g = new EtsRoadGraph(map); g.IndexLoadedSectors();
    List<EtsRoadGraph.Step> path=null; float len=0; var rnd=new System.Random(5);
    for(int t=0;t<400 && (path==null||len<2500);t++){ var from=g.NearestNode(cg.x+rnd.Next(-800,800), cg.z+rnd.Next(-800,800), out _); var to=g.NearestNode(cg.x+rnd.Next(-3000,3000), cg.z+rnd.Next(-3000,3000), out _); if(from==null||to==null)continue; path=g.FindPath(from.Uid,to.Uid,out len);}
    Console.WriteLine("sectors "+secs.Count+" path "+(path==null?"none":path.Count+" steps "+len+" m"));
    var pts = new List<Vector3>(); EtsMapNode prev=null;
    foreach(var st in path){ var n=map.FindNode(st.NodeUid); if(n==null)continue; Vector3 h=U(n.Position,cg);
      if(prev!=null && st.Road!=null){ float L=Mathf.Max(1,st.Road.Length), sg=st.RoadReversed?-1:1; Vector3 p0=U(prev.Position,cg);
        Vector3 t0=D(prev.Forward())*L*sg, t1=D(n.Forward())*L*sg; int k=Mathf.Max(2,Mathf.CeilToInt(L/8)); for(int i=1;i<=k;i++){float u=(float)i/k,u2=u*u,u3=u2*u; pts.Add((2*u3-3*u2+1)*p0+(u3-2*u2+u)*t0+(-2*u3+3*u2)*h+(u3-u2)*t1);} }
      else pts.Add(h); prev=n; }
    int at = pts.Count/4; Vector3 truck=pts[at], fwd=pts[at+3]-pts[at];
    var gps = new EtsGpsMap(342, 179); Console.WriteLine(gps.LoadDefinitions(fs));
    var sw=System.Diagnostics.Stopwatch.StartNew(); gps.Collect(map, secs, cg, truck, gps.NeededRadius()); Console.WriteLine("collect "+gps.Segments+" segs "+sw.ElapsedMilliseconds+" ms");
    for(int zoom=0;zoom<2;zoom++){ sw.Restart(); for(int r=0;r<100;r++) gps.Render(truck,fwd,zoom,pts,at); Console.WriteLine("render zoom "+zoom+": "+(sw.ElapsedMilliseconds/100.0)+" ms, arrow "+gps.PlayerPixels(zoom)+" px");
      Arrow(gps, gps.PlayerPixels(zoom)); Png("gps_zoom"+zoom+".png", gps); }
  }
  static Vector3 U(float[] p, Vector3 c)=>new Vector3(p[0]-c.x,p[1]-c.y,-(p[2]-c.z));
  static Vector3 D(float[] d)=>new Vector3(d[0],d[1],-d[2]).normalized;
  // stand-in for /material/ui/map/player.mat (the advisor shows that texture over the centre)
  static void Arrow(EtsGpsMap g, float size){ float cx=g.Width/2f, cy=g.Height/2f; for(int y=0;y<g.Height;y++)for(int x=0;x<g.Width;x++){ float v=(y+0.5f-cy)/size+0.5f; if(v<0||v>1)continue; float hw=(1-v)*0.35f*size; if(Mathf.Abs(x+0.5f-cx)<=hw) g.Pixels[y*g.Width+x]=new Color32(255,255,255,255);} }
  static void Png(string f, EtsGpsMap g){ int w=g.Width,h=g.Height; var raw=new MemoryStream(); for(int y=h-1;y>=0;y--){raw.WriteByte(0); for(int x=0;x<w;x++){var c=g.Pixels[y*w+x]; raw.WriteByte(c.r);raw.WriteByte(c.g);raw.WriteByte(c.b);}}
    var z=new MemoryStream(); z.WriteByte(0x78); z.WriteByte(1); using(var d=new DeflateStream(z,CompressionMode.Compress,true)){var b=raw.ToArray(); d.Write(b,0,b.Length);} uint ad=Adler(raw.ToArray()); z.WriteByte((byte)(ad>>24));z.WriteByte((byte)(ad>>16));z.WriteByte((byte)(ad>>8));z.WriteByte((byte)ad);
    using(var o=File.Create(f)){ o.Write(new byte[]{137,80,78,71,13,10,26,10},0,8); var ih=new MemoryStream(); BE(ih,(uint)w);BE(ih,(uint)h); ih.Write(new byte[]{8,2,0,0,0},0,5); Chunk(o,"IHDR",ih.ToArray()); Chunk(o,"IDAT",z.ToArray()); Chunk(o,"IEND",new byte[0]); } }
  static void BE(Stream s,uint v){s.WriteByte((byte)(v>>24));s.WriteByte((byte)(v>>16));s.WriteByte((byte)(v>>8));s.WriteByte((byte)v);}
  static void Chunk(Stream o,string t,byte[] d){ BE(o,(uint)d.Length); var td=new byte[4+d.Length]; for(int i=0;i<4;i++)td[i]=(byte)t[i]; Array.Copy(d,0,td,4,d.Length); o.Write(td,0,td.Length); BE(o,Crc(td)); }
  static uint Crc(byte[] b){uint c=0xFFFFFFFF; foreach(var x in b){c^=x; for(int k=0;k<8;k++)c=(c&1)!=0?0xEDB88320^(c>>1):c>>1;} return ~c;}
  static uint Adler(byte[] b){uint s1=1,s2=0; foreach(var x in b){s1=(s1+x)%65521;s2=(s2+s1)%65521;} return (s2<<16)|s1;}
}
