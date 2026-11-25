using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class PK2 { public static void Run(EtsFileSystem fs, string[] a){ var b=EtsFmodBank.Parse(fs.Read(a[0]),out var e); var d=b.Bytes; Console.WriteLine("fsb at "+b.FsbOffset+" bank len "+d.Length);
  foreach(var s in b.Samples){ Console.WriteLine(s.Index+" "+s.Name+" off "+s.DataOffset+" len "+s.DataLength+" frames "+s.Frames+" head "+BitConverter.ToString(d,s.DataOffset,8)); }
  // scan for plausible packet chains near default_country start
  var t=b.Samples[int.Parse(a[1])]; for(int delta=-64; delta<=64; delta++){ int p=t.DataOffset+delta; int ok=0; int q=p; for(int k=0;k<20;k++){ int z=d[q]|(d[q+1]<<8); if(z<4||z>2000) break; if((d[q+2]&1)!=0) break; q+=2+z; ok++; } if(ok>=10) Console.WriteLine("chain of "+ok+" good packets at delta "+delta); } } }
