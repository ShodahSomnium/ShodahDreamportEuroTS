using System; using System.IO; using System.IO.Compression; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static uint[] crcT;
  static uint Crc(byte[] b, int o, int n, uint c=0xffffffff){ if(crcT==null){crcT=new uint[256]; for(uint i=0;i<256;i++){uint x=i; for(int k=0;k<8;k++) x=(x&1)!=0?0xedb88320u^(x>>1):x>>1; crcT[i]=x;}} for(int i=0;i<n;i++) c=crcT[(c^b[o+i])&0xff]^(c>>8); return c; }
  static void Chunk(Stream s, string t, byte[] d){ var len=BitConverter.GetBytes(d.Length); Array.Reverse(len); s.Write(len,0,4); var td=new byte[4+d.Length]; for(int i=0;i<4;i++) td[i]=(byte)t[i]; Buffer.BlockCopy(d,0,td,4,d.Length); s.Write(td,0,td.Length); var c=BitConverter.GetBytes(Crc(td,0,td.Length)^0xffffffff); Array.Reverse(c); s.Write(c,0,4); }
  public static void Png(string path, int w, int h, byte[] rgba){
    // composite over a mid grey so transparent areas are visible
    var img = new byte[w*h*4]; for (int i=0;i<w*h;i++){ float a=rgba[i*4+3]/255f; for(int c=0;c<3;c++) img[i*4+c]=(byte)(rgba[i*4+c]*a + 90*(1-a)); img[i*4+3]=255; }
    var raw=new byte[(w*4+1)*h]; for(int y=0;y<h;y++){ raw[y*(w*4+1)]=0; Buffer.BlockCopy(img,y*w*4,raw,y*(w*4+1)+1,w*4);} 
    var ms=new MemoryStream(); ms.WriteByte(0x78); ms.WriteByte(0x9c); using(var ds=new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(raw,0,raw.Length);
    uint a1=1,b1=0; foreach(var x in raw){a1=(a1+x)%65521; b1=(b1+a1)%65521;} uint ad=(b1<<16)|a1; var adb=BitConverter.GetBytes(ad); Array.Reverse(adb); ms.Write(adb,0,4);
    using(var f=File.Create(path)){ f.Write(new byte[]{137,80,78,71,13,10,26,10},0,8); var ih=new byte[13]; var wb=BitConverter.GetBytes(w); Array.Reverse(wb); var hb=BitConverter.GetBytes(h); Array.Reverse(hb); Buffer.BlockCopy(wb,0,ih,0,4); Buffer.BlockCopy(hb,0,ih,4,4); ih[8]=8; ih[9]=6; Chunk(f,"IHDR",ih); Chunk(f,"IDAT",ms.ToArray()); Chunk(f,"IEND",new byte[0]); }
  }
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    var map = new EtsMap(fs); var lib = new EtsSignLibrary(fs); var art = new EtsSignArt(fs);
    float gx=float.Parse(a[0]), gz=float.Parse(a[1]), R=float.Parse(a[2]); string prefix=a[3]; int max = a.Length>4? int.Parse(a[4]) : 20;
    int n=0; var sw = System.Diagnostics.Stopwatch.StartNew();
    var rows = new List<(float, EtsMapSign, EtsMapNode)>();
    foreach (var s in map.SectorsAround(gx,gz,R)) foreach (var sg in s.Signs) { if (sg.Template.Length==0) continue; var nd = map.FindNode(sg.NodeUid); if (nd==null) continue; float dx=nd.Position[0]-gx, dz=nd.Position[2]-gz; float d=(float)Math.Sqrt(dx*dx+dz*dz); if (d<=R) rows.Add((d,sg,nd)); }
    foreach (var (d, sg, nd) in rows.OrderBy(r=>r.Item1)) {
      if (n>=max) break;
      var u = map.SignModel(sg.ModelToken); string md = u?.GetString("model_desc", null); if (md==null) continue;
      var pmd = fs.Read(md); var pmg = fs.Read(md.Substring(0, md.Length-4)+".pmg"); if (pmd==null||pmg==null) continue;
      var model = EtsModelData.Parse(pmd, pmg, md); var hooks = EtsSignPlanner.Hookups(pmg, model);
      var t0 = sw.ElapsedMilliseconds;
      var faces = EtsSignPlanner.Plan(lib, art, model, hooks, sg.Template, sg.Areas, sg.Overrides, out string note);
      Console.WriteLine(d.ToString("0")+" m "+sg.Template+" "+EtsToken.Decode(sg.ModelToken)+" faces "+faces.Count+" ("+(sw.ElapsedMilliseconds-t0)+" ms)"+(note!=null?" note "+note:""));
      foreach (var f in faces) { string p = prefix+"_"+n+"_"+f.Area+".png"; Png(p, f.PixelsWide, f.PixelsHigh, f.Rgba); Console.WriteLine("   "+f.Area+" "+f.Width+"x"+f.Height+" m -> "+f.PixelsWide+"x"+f.PixelsHigh+" items "+f.Items+" "+p); }
      n++;
    }
    Console.WriteLine("glyphs "+EtsSignRaster.GlyphsDrawn+" missing "+EtsSignRaster.GlyphsMissing+" images "+EtsSignRaster.ImagesDrawn+" frames "+EtsSignRaster.FramesDrawn+" skipped "+EtsSignRaster.ItemsSkipped+" fonts "+art.FacesRead+" pictures "+art.ImagesRead+" missing: "+EtsSignRaster.MissingSample);
  }
}
