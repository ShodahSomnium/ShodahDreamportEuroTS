// strm <bank> <sample|all> [outdir] : decode whole (Decode) and streamed (LoopReader, 0.5 s blocks, 2.5 passes); compare, noise stats, WAVs
using System; using System.IO; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class STRM { public static void Run(EtsFileSystem fs, string[] a){ var b=EtsFmodBank.Parse(fs.Read(a[0]),out var e); if(b==null){Console.WriteLine(e);return;}
  string od=a.Length>2?a[2]:null; Console.WriteLine("shifted "+b.Shifted);
  foreach(var s in b.Samples){ if(a[1]!="all"&&s.Name!=a[1]) continue; int ch=s.Channels;
    var w=b.Decode(s,out var err); if(w==null){Console.WriteLine("FAIL "+s+": "+err);continue;}
    var r=new EtsFmodBank.LoopReader(b.CopyPackets(s),ch,s.Rate,s.SetupCrc,s.Coded); int blk=Math.Max(1024,s.Rate/2)*ch;
    int total=(int)(w.Length*2.5)/blk*blk; var st=new float[total]; var buf=new float[blk];
    for(int p=0;p<total;p+=blk){ r.Read(buf); Array.Copy(buf,0,st,p,blk); }
    double diff=0; int cmp=Math.Min(w.Length,st.Length)-512*ch; for(int i=0;i<cmp;i++) diff=Math.Max(diff,Math.Abs(w[i]-st[i]));
    Console.WriteLine(s+" decodedLen "+w.Length/ch+" | whole "+Stats(w,ch)+" | stream "+Stats(st,ch)+" | maxdiff first pass "+diff.ToString("0.0000")+" passes "+r.Passes);
    if(od!=null){ Wav(Path.Combine(od,s.Name.Replace('/','_')+"_whole.wav"),w,ch,s.Rate); Wav(Path.Combine(od,s.Name.Replace('/','_')+"_stream.wav"),st,ch,s.Rate);} } }
  static string Stats(float[] x,int ch){ double ss=0; int zc=0; float pk=0, jmp=0; int jat=0; for(int i=0;i<x.Length;i++){ss+=x[i]*x[i]; pk=Math.Max(pk,Math.Abs(x[i]));
      if(i>=ch){ if((x[i]>=0)!=(x[i-ch]>=0)) zc++; float j=Math.Abs(x[i]-x[i-ch]); if(j>jmp){jmp=j;jat=i/ch;} } }
    return "rms "+Math.Sqrt(ss/x.Length).ToString("0.0000")+" peak "+pk.ToString("0.000")+" zcr "+((double)zc/x.Length).ToString("0.000")+" maxjump "+jmp.ToString("0.000")+"@"+jat; }
  static void Wav(string f,float[] x,int ch,int rate){ using var bw=new BinaryWriter(File.Create(f)); int n=x.Length*2;
    bw.Write(0x46464952); bw.Write(36+n); bw.Write(0x45564157); bw.Write(0x20746d66); bw.Write(16); bw.Write((short)1); bw.Write((short)ch); bw.Write(rate); bw.Write(rate*ch*2); bw.Write((short)(ch*2)); bw.Write((short)16);
    bw.Write(0x61746164); bw.Write(n); foreach(var v in x) bw.Write((short)Math.Max(-32767,Math.Min(32767,v*32767))); } }
