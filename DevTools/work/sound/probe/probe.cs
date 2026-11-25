// Sound probe: opens the demo's archives, parses FMOD banks (EtsFmodBank), decodes samples to WAV + PNG.
//  scan                       -> every /sound bank: chunks, codec, sample count, setup CRCs (known/unknown)
//  list <bank>                -> samples of one bank
//  dump <bank> <regex|all> [max] -> decode matching samples to DevTools/work/sound/out/<bank>/<name>.wav + .png, stats
//  raw <bank>                 -> write the bank bytes to out/
using System; using System.Collections.Generic; using System.IO; using System.IO.Compression; using System.Text; using System.Text.RegularExpressions;
using System.Diagnostics; using System.Linq;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
  static EtsFileSystem fs; static string OutDir="D:/Dreamport/Euro Truck Simulator/Euro Truck Sim/DevTools/work/sound/out";
  static void Walk(string dir, Action<string> onFile, int depth=0){
    if(depth>12) return; if(!fs.TryList(dir, out var files, out var folders)) return;
    foreach(var f in files) onFile(dir.TrimEnd('/')+"/"+f); foreach(var d in folders) Walk(dir.TrimEnd('/')+"/"+d, onFile, depth+1); }
  static void Main(string[] a){
    fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
    string cmd=a[0];
    if(cmd=="scan"){
      var crcs=new Dictionary<uint,int>(); int banks=0, ok=0; var modes=new Dictionary<int,int>(); long totalSamples=0;
      Walk("/sound", p=>{ if(!p.EndsWith(".bank")) return; banks++; var bytes=fs.Read(p); var b=EtsFmodBank.Parse(bytes, out var err);
        if(b==null){ Console.WriteLine("FAIL "+p+" "+(bytes==null?0:bytes.Length)+" bytes: "+err+" head="+Hex(bytes,0,16)); return; }
        ok++; modes[b.Mode]=modes.TryGetValue(b.Mode,out var m)?m+1:1; totalSamples+=b.Samples.Count;
        var local=new HashSet<uint>(); foreach(var s in b.Samples){ local.Add(s.SetupCrc); crcs[s.SetupCrc]=crcs.TryGetValue(s.SetupCrc,out var c)?c+1:1; }
        Console.WriteLine(p+" "+bytes.Length+"B ["+b.ChunkSummary+"] fsb5 v"+b.Version+" codec "+b.Mode+" samples "+b.Samples.Count+(b.Samples.Count>0?" e.g. "+b.Samples[0]:""));
      });
      Console.WriteLine("banks "+banks+" parsed "+ok+" samples "+totalSamples); foreach(var kv in modes) Console.WriteLine("codec "+kv.Key+": "+kv.Value+" banks");
      int known=0; foreach(var kv in crcs){ bool k=EtsVorbisSetups.Find(kv.Key)!=null; if(k) known++; Console.WriteLine("crc "+kv.Key+" used by "+kv.Value+" samples, in table: "+k+(k?" ("+EtsVorbisSetups.Find(kv.Key).Length+" B)":"")); }
      Console.WriteLine("distinct setup crcs "+crcs.Count+", known "+known);
    }
    else if(cmd=="list"){ var b=EtsFmodBank.Parse(fs.Read(a[1]), out var err); if(b==null){Console.WriteLine(err);return;} Console.WriteLine("["+b.ChunkSummary+"] codec "+b.Mode);
      foreach(var s in b.Samples) Console.WriteLine(s.Index+" "+s+" bytes "+s.DataLength); }
    else if(cmd=="fev"){ F.Run(fs,a[1]); }
    else if(cmd=="dbg"){ DBG.Run(fs,a[1]); }
    else if(cmd=="truckset"){ TS.Run(fs,a.Skip(1).ToArray()); }
    else if(cmd=="events"){ EV.List(fs,a[1]); }
    else if(cmd=="sweep"){ EV.Sweep(fs,a[1],a[2],a[3],float.Parse(a[4]),float.Parse(a[5]),float.Parse(a[6]),a.Skip(8).ToArray(),a[7]); }
    else if(cmd=="tree"){ var d=fs.Read(a[1]); int max=a.Length>2?int.Parse(a[2]):64; Tree(d,12,d.Length,0,max); }
    else if(cmd=="raw"){ Directory.CreateDirectory(OutDir); File.WriteAllBytes(OutDir+"/"+Path.GetFileName(a[1]), fs.Read(a[1])); }
    else if(cmd=="dump"){
      var b=EtsFmodBank.Parse(fs.Read(a[1]), out var err); if(b==null){Console.WriteLine(err);return;}
      var re=new Regex(a[2]=="all"?".":a[2], RegexOptions.IgnoreCase); int max=a.Length>3?int.Parse(a[3]):1000; int n=0;
      string dir=OutDir+"/"+Path.GetFileNameWithoutExtension(a[1]); Directory.CreateDirectory(dir);
      foreach(var s in b.Samples){ if(!re.IsMatch(s.Name)) continue; if(n++>=max) break;
        var sw=Stopwatch.StartNew(); var pcm=b.Decode(s, out var derr); sw.Stop();
        if(pcm==null){ Console.WriteLine("DECODE FAIL "+s+": "+derr); continue; }
        double peak=0, sum=0; for(int i=0;i<pcm.Length;i++){ double v=Math.Abs(pcm[i]); if(v>peak)peak=v; sum+=pcm[i]*pcm[i]; }
        double rms=Math.Sqrt(sum/pcm.Length); int frames=pcm.Length/s.Channels;
        string safe=Regex.Replace(s.Name,"[^A-Za-z0-9_.-]","_");
        WriteWav(dir+"/"+safe+".wav", pcm, s.Channels, s.Rate); WritePng(dir+"/"+safe+".png", pcm, s.Channels, s.Rate);
        Console.WriteLine(string.Format("{0}: {1}ch {2}Hz frames {3} (header {4}) {5:0.00}s peak {6:0.000} ({7:0.0} dBFS) rms {8:0.0000} ({9:0.0} dBFS) zc/s {10:0} decode {11} ms",
          s.Name, s.Channels, s.Rate, frames, s.Frames, frames/(double)s.Rate, peak, 20*Math.Log10(peak+1e-9), rms, 20*Math.Log10(rms+1e-9), ZeroCross(pcm,s.Channels)*s.Rate, sw.ElapsedMilliseconds)+" f0~"+F0(pcm,s.Channels,s.Rate).ToString("0.0")+" Hz (x20="+(20*F0(pcm,s.Channels,s.Rate)).ToString("0")+" rpm if 6cyl)");
      }
    }
  }
  static void Tree(byte[] d,int p,int end,int depth,int max){
    while(p+8<=end){ string id=Encoding.ASCII.GetString(d,p,4); int size=BitConverter.ToInt32(d,p+4); if(size<0||p+8+size>end){ Console.WriteLine(new string(' ',depth*2)+"?? "+id+" "+size); return; }
      if(id=="LIST"){ string t=Encoding.ASCII.GetString(d,p+8,4); Console.WriteLine(new string(' ',depth*2)+"LIST "+t+" "+size); Tree(d,p+12,p+8+size,depth+1,max); }
      else if(id=="SND "){ Console.WriteLine(new string(' ',depth*2)+"SND  "+size); }
      else { Console.WriteLine(new string(' ',depth*2)+id+" "+size+"  "+HexA(d,p+8,Math.Min(size,max))); }
      p+=8+size; if((size&1)!=0) p++; } }
  static string HexA(byte[] b,int o,int n){ var sb=new StringBuilder(); var asc=new StringBuilder(); for(int i=0;i<n;i++){ byte x=b[o+i]; sb.Append(x.ToString("x2")); if(i%4==3) sb.Append(' '); asc.Append(x>=32&&x<127?(char)x:'.'); } return sb+" |"+asc+"|"; }
  static double F0(float[] p,int ch,int rate){ int n=Math.Min(p.Length/ch, rate*2); var x=new double[n]; double a=Math.Exp(-2*Math.PI*300.0/rate), y=0, y2=0; for(int i=0;i<n;i++){ y=(1-a)*p[i*ch]+a*y; y2=(1-a)*y+a*y2; x[i]=y2; }
    int lo=rate/160, hi=rate/22; double best=-1; int bl=lo; double r0=0; for(int i=0;i<n;i++) r0+=x[i]*x[i]; var r=new double[hi+2]; for(int L=lo;L<=hi+1;L++){ double s=0; for(int i=0;i+L<n;i++) s+=x[i]*x[i+L]; r[L]=s/(n-L); }
    for(int L=lo+1;L<=hi;L++) if(r[L]>r[L-1]&&r[L]>=r[L+1]&&r[L]>best){ best=r[L]; bl=L; } return rate/(double)bl; }
  static double ZeroCross(float[] p,int ch){ int z=0; int n=p.Length/ch; for(int i=1;i<n;i++) if((p[(i-1)*ch]>=0)!=(p[i*ch]>=0)) z++; return z/(double)Math.Max(1,n); }
  static string Hex(byte[] b,int o,int n){ if(b==null) return ""; var sb=new StringBuilder(); for(int i=o;i<Math.Min(b.Length,o+n);i++) sb.Append(b[i].ToString("x2")); return sb.ToString(); }
  static void WriteWav(string path, float[] pcm, int ch, int rate){
    using(var w=new BinaryWriter(File.Create(path))){ int bytes=pcm.Length*2; w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36+bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate); w.Write(rate*ch*2); w.Write((short)(ch*2)); w.Write((short)16); w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
      foreach(var f in pcm) w.Write((short)Math.Max(-32768,Math.Min(32767,(int)Math.Round(f*32767)))); } }
  // 1024x400 PNG: top = waveform envelope (channel 0), bottom = spectrogram (0..rate/2 Hz, log magnitude)
  internal static void WritePng(string path, float[] pcm, int ch, int rate){
    int W=1024, H=400, WH=140; var img=new byte[W*H*3]; int n=pcm.Length/ch;
    for(int x=0;x<W;x++){ int a0=(int)((long)x*n/W), a1=Math.Max(a0+1,(int)((long)(x+1)*n/W)); float lo=0,hi=0; for(int i=a0;i<a1&&i<n;i++){ float v=pcm[i*ch]; if(v<lo)lo=v; if(v>hi)hi=v; }
      int y0=(int)(WH/2-hi*WH/2), y1=(int)(WH/2-lo*WH/2); for(int y=Math.Max(0,y0);y<=Math.Min(WH-1,y1);y++){ int o=(y*W+x)*3; img[o]=80; img[o+1]=200; img[o+2]=255; } }
    int N=1024; var re=new double[N]; var im=new double[N]; int SH=H-WH-4;
    for(int x=0;x<W;x++){ int c=(int)((long)x*n/W); for(int i=0;i<N;i++){ int k=c-N/2+i; double v=(k>=0&&k<n)?pcm[k*ch]:0; re[i]=v*(0.5-0.5*Math.Cos(2*Math.PI*i/(N-1))); im[i]=0; } FFT(re,im);
      for(int y=0;y<SH;y++){ int bin=(int)((SH-1-y)*(N/2)/(double)SH); double mag=Math.Sqrt(re[bin]*re[bin]+im[bin]*im[bin]); double db=20*Math.Log10(mag+1e-9); int v=(int)Math.Max(0,Math.Min(255,(db+60)*255/80));
        int o=((H-SH+y)*W+x)*3; img[o]=(byte)v; img[o+1]=(byte)(v*v/255); img[o+2]=(byte)(255-v>0?v/3:0); } }
    SavePng(path,img,W,H); }
  static void FFT(double[] re,double[] im){ int n=re.Length; for(int i=1,j=0;i<n;i++){ int bit=n>>1; for(;(j&bit)!=0;bit>>=1) j^=bit; j^=bit; if(i<j){ var t=re[i];re[i]=re[j];re[j]=t; t=im[i];im[i]=im[j];im[j]=t; } }
    for(int len=2;len<=n;len<<=1){ double ang=-2*Math.PI/len; double wr=Math.Cos(ang), wi=Math.Sin(ang); for(int i=0;i<n;i+=len){ double cr=1,ci=0; for(int j=0;j<len/2;j++){ int u=i+j,v=i+j+len/2; double xr=re[v]*cr-im[v]*ci, xi=re[v]*ci+im[v]*cr; re[v]=re[u]-xr; im[v]=im[u]-xi; re[u]+=xr; im[u]+=xi; double t=cr*wr-ci*wi; ci=cr*wi+ci*wr; cr=t; } } } }
  static void SavePng(string path, byte[] rgb, int w, int h){
    var raw=new MemoryStream(); for(int y=0;y<h;y++){ raw.WriteByte(0); raw.Write(rgb,y*w*3,w*3); }
    var z=new MemoryStream(); z.WriteByte(0x78); z.WriteByte(0x01); using(var ds=new DeflateStream(z,CompressionMode.Compress,true)) { raw.Position=0; raw.CopyTo(ds); }
    uint ad=Adler(raw.ToArray()); z.WriteByte((byte)(ad>>24)); z.WriteByte((byte)(ad>>16)); z.WriteByte((byte)(ad>>8)); z.WriteByte((byte)ad);
    using(var f=File.Create(path)){ f.Write(new byte[]{137,80,78,71,13,10,26,10},0,8); var ih=new MemoryStream(); BE(ih,(uint)w); BE(ih,(uint)h); ih.WriteByte(8); ih.WriteByte(2); ih.WriteByte(0); ih.WriteByte(0); ih.WriteByte(0);
      Chunk(f,"IHDR",ih.ToArray()); Chunk(f,"IDAT",z.ToArray()); Chunk(f,"IEND",new byte[0]); } }
  static void BE(Stream s,uint v){ s.WriteByte((byte)(v>>24)); s.WriteByte((byte)(v>>16)); s.WriteByte((byte)(v>>8)); s.WriteByte((byte)v); }
  static void Chunk(Stream s,string t,byte[] d){ BE(s,(uint)d.Length); var td=new byte[4+d.Length]; Encoding.ASCII.GetBytes(t,0,4,td,0); Array.Copy(d,0,td,4,d.Length); s.Write(td,0,td.Length); BE(s,Crc(td)); }
  static uint Crc(byte[] b){ uint c=0xFFFFFFFF; foreach(var x in b){ c^=x; for(int k=0;k<8;k++) c=(c>>1)^(0xEDB88320u&(uint)-(int)(c&1)); } return ~c; }
  static uint Adler(byte[] b){ uint a=1,s=0; foreach(var x in b){ a=(a+x)%65521; s=(s+a)%65521; } return (s<<16)|a; }
}
