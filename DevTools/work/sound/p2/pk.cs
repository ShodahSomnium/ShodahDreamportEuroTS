using System; using System.Linq; using System.Collections.Generic; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class PK { public static void Run(EtsFileSystem fs, string[] a){ var b=EtsFmodBank.Parse(fs.Read(a[0]),out var e); var s=b.Find(a[1]); var d=b.Bytes; int p=s.DataOffset, end=p+s.DataLength; var sizes=new List<int>();
  while(p+2<=end){ int z=d[p]|(d[p+1]<<8); if(z==0||p+2+z>end) break; sizes.Add(z); p+=2+z; }
  Console.WriteLine(s+" packets "+sizes.Count+" consumed "+(p-s.DataOffset)+"/"+s.DataLength+" first "+string.Join(",",sizes.Take(12))+" max "+sizes.Max()+" mean "+sizes.Average().ToString("0"));
  var o=b.Samples.First(x=>x.Name!=s.Name); p=o.DataOffset; Console.Write(o.Name+" first sizes: "); for(int k=0;k<8;k++){ int z=d[p]|(d[p+1]<<8); Console.Write(z+" "); p+=2+z; } Console.WriteLine();
  p=s.DataOffset; Console.WriteLine("first packet byte0 "+d[p+2].ToString("x2")+" (audio packets have bit0=0)"); } }
