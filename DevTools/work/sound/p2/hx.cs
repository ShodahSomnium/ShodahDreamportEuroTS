using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class HX { public static void Run(EtsFileSystem fs, string[] a){ var b=EtsFmodBank.Parse(fs.Read(a[0]),out var e); var s=b.Samples[int.Parse(a[1])]; int st=s.DataOffset+int.Parse(a[2]);
  Console.WriteLine(BitConverter.ToString(b.Bytes,st,48)); int p=st; for(int k=0;k<12;k++){ int z=b.Bytes[p]|(b.Bytes[p+1]<<8); Console.Write(z+"("+b.Bytes[p+2].ToString("x2")+") "); p+=2+z; } Console.WriteLine();
  // where does previous sample's chain end?
  if(s.Index>0){ var q=b.Samples[s.Index-1]; p=q.DataOffset; int last=p; while(true){ int z=b.Bytes[p]|(b.Bytes[p+1]<<8); if(z==0||p+2+z>q.DataOffset+q.DataLength+64) break; last=p; p+=2+z; } Console.WriteLine("prev chain ends at rel "+(p-s.DataOffset)+" bytes there "+BitConverter.ToString(b.Bytes,p,8)); } } }
