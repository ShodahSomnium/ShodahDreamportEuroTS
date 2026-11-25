// shall: every bank: how many samples were shifted; decode the shifted ones and check frames
using System; using System.Linq; using System.IO; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class SHALL { public static void Run(EtsFileSystem fs, string[] a){ int tot=0, bad=0, banks=0;
  foreach(var path in File.ReadAllLines(a[0])){ if(!path.EndsWith(".bank")) continue; var bytes=fs.Read(path); var b=EtsFmodBank.Parse(bytes,out var e); if(b==null) continue; banks++;
    // re-derive which were shifted: compare to plain chain test at header offset is gone; so decode samples whose first packet byte looks odd
    if(b.Shifted==0) continue; tot+=b.Shifted; Console.Write(path+": shifted "+b.Shifted);
    int checkd=0; foreach(var s in b.Samples){ if(s.Frames<500000) continue; var pcm=b.Decode(s,out var err); checkd++; if(pcm==null||pcm.Length/s.Channels<s.Frames-4096){ bad++; Console.Write(" BAD "+s.Name+"("+(pcm==null?err:(pcm.Length/s.Channels)+"/"+s.Frames)+")"); } }
    Console.WriteLine(" (long samples checked "+checkd+")"); }
  Console.WriteLine("banks "+banks+" shifted samples "+tot+" bad "+bad); } }
