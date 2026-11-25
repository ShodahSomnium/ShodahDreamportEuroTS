// dec <bank> <sampleName|all> : decode with the world's EtsFmodBank.Decode, report frames/peak/time
using System; using System.Diagnostics; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class DEC { public static void Run(EtsFileSystem fs, string[] a){ var b=EtsFmodBank.Parse(fs.Read(a[0]),out var e); if(b==null){Console.WriteLine(e);return;}
  int fails=0, n=0; foreach(var s in b.Samples){ if(a[1]!="all"&&s.Name!=a[1]) continue; n++; var sw=Stopwatch.StartNew(); var pcm=b.Decode(s,out var err); sw.Stop();
    if(pcm==null){ fails++; Console.WriteLine("FAIL "+s+" bytes "+s.DataLength+": "+err+" first bytes "+BitConverter.ToString(b.Bytes,s.DataOffset,Math.Min(16,s.DataLength))); continue; }
    if(a[1]!="all") Console.WriteLine(s+" -> "+pcm.Length/s.Channels+" frames in "+sw.ElapsedMilliseconds+" ms"); }
  Console.WriteLine(n+" samples, "+fails+" failed"); } }
