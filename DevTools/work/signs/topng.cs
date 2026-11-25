using System; using System.IO; using System.IO.Compression; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static uint[] crcT;
  static uint Crc(byte[] b, int o, int n, uint c=0xffffffff){ if(crcT==null){crcT=new uint[256]; for(uint i=0;i<256;i++){uint x=i; for(int k=0;k<8;k++) x=(x&1)!=0?0xedb88320u^(x>>1):x>>1; crcT[i]=x;}} for(int i=0;i<n;i++) c=crcT[(c^b[o+i])&0xff]^(c>>8); return c; }
  static void Chunk(Stream s, string t, byte[] d){ var len=BitConverter.GetBytes(d.Length); Array.Reverse(len); s.Write(len,0,4); var td=new byte[4+d.Length]; for(int i=0;i<4;i++) td[i]=(byte)t[i]; Buffer.BlockCopy(d,0,td,4,d.Length); s.Write(td,0,td.Length); var c=BitConverter.GetBytes(Crc(td,0,td.Length)^0xffffffff); Array.Reverse(c); s.Write(c,0,4); }
  public static void Png(string path, int w, int h, byte[] rgba){
    var raw=new byte[(w*4+1)*h]; for(int y=0;y<h;y++){ raw[y*(w*4+1)]=0; Buffer.BlockCopy(rgba,y*w*4,raw,y*(w*4+1)+1,w*4);} 
    var ms=new MemoryStream(); ms.WriteByte(0x78); ms.WriteByte(0x9c); using(var ds=new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(raw,0,raw.Length);
    uint a=1,b=0; foreach(var x in raw){a=(a+x)%65521; b=(b+a)%65521;} uint ad=(b<<16)|a; var adb=BitConverter.GetBytes(ad); Array.Reverse(adb); ms.Write(adb,0,4);
    using(var f=File.Create(path)){ f.Write(new byte[]{137,80,78,71,13,10,26,10},0,8); var ih=new byte[13]; var wb=BitConverter.GetBytes(w); Array.Reverse(wb); var hb=BitConverter.GetBytes(h); Array.Reverse(hb); Buffer.BlockCopy(wb,0,ih,0,4); Buffer.BlockCopy(hb,0,ih,4,4); ih[8]=8; ih[9]=6; Chunk(f,"IHDR",ih); Chunk(f,"IDAT",ms.ToArray()); Chunk(f,"IEND",new byte[0]); }
  }
  static void Main(string[] a){
    var fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    string tobj=a[0]; int mip=int.Parse(a[1]); string outp=a[2]; float scale = a.Length>3? float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture) : 1f;
    if(!fs.TryGetEntry(tobj, out EtsHashFs arc, out EtsHashFs.Entry e)){ Console.WriteLine("missing"); return; }
    byte[] surf = arc.ReadPackedImage(e);
    Console.WriteLine(tobj+" "+e.ImageWidth+"x"+e.ImageHeight+" dxgi "+e.DxgiFormat+" mips "+e.MipCount);
    if(e.DxgiFormat==67){
      int w=e.ImageWidth,h=e.ImageHeight,at=0; int pa=Math.Max(1,e.PitchAlign), ia=Math.Max(1,e.ImageAlign);
      for(int m=0;m<=mip;m++){ at=(at+ia-1)/ia*ia; if(m==mip){ var rgba=new byte[w*h*4]; double sum=0, mx=0; for(int r=0;r<h;r++){ at=(at+pa-1)/pa*pa; for(int x=0;x<w;x++){ uint v=BitConverter.ToUInt32(surf,at+x*4); EtsPixels.SharedExponentToLinear(v, out float R, out float G, out float B); double l=0.2126*R+0.7152*G+0.0722*B; sum+=l; if(l>mx) mx=l; int o=(r*w+x)*4; rgba[o]=S(R*scale); rgba[o+1]=S(G*scale); rgba[o+2]=S(B*scale); rgba[o+3]=255; } at+=w*4; }
          Console.WriteLine("mean lum "+(sum/(w*h))+" max "+mx); Png(outp,w,h,rgba); return; }
        for(int r=0;r<h;r++){ at=(at+pa-1)/pa*pa; at+=w*4; } w=Math.Max(1,w/2); h=Math.Max(1,h/2); }
    } else {
      var dds = EtsDds.FromPackedSurface(surf, e.ImageWidth, e.ImageHeight, e.DxgiFormat, e.MipCount, 1, false, e.PitchAlign, e.ImageAlign);
      var bmp = EtsPixels.DecodeLevel(dds, mip); if(bmp==null){Console.WriteLine("no decode"); return;}
      Png(outp,bmp.Width,bmp.Height,bmp.Rgba); int x0=9999,y0=9999,x1=-1,y1=-1; for(int yy=0;yy<bmp.Height;yy++) for(int xx=0;xx<bmp.Width;xx++) if(bmp.Rgba[(yy*bmp.Width+xx)*4+3]>128){x0=Math.Min(x0,xx);x1=Math.Max(x1,xx);y0=Math.Min(y0,yy);y1=Math.Max(y1,yy);} Console.WriteLine("wrote "+bmp.Width+"x"+bmp.Height+" alpha bbox x "+x0+".."+x1+" y "+y0+".."+y1+" (row 0 = first decoded row)");
    }
  }
  static byte S(float v){ if(v<=0) return 0; double s = v<=0.0031308? v*12.92 : 1.055*Math.Pow(v,1/2.4)-0.055; return (byte)Math.Max(0,Math.Min(255,s*255+0.5)); }
}
