using System; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class PROF { public static void Run(EtsFileSystem fs, string[] a){ var b=EtsFmodBank.Parse(fs.Read(a[0]),out var e);
  foreach(var s in b.Samples){ if(s.Name!=a[1]) continue; var w=b.Decode(s,out var err); int r=s.Rate/2; var sb=new System.Text.StringBuilder();
    for(int p=0;p+r<=w.Length;p+=r){ int zc=0; float pk=0; for(int i=p+1;i<p+r;i++){ if((w[i]>=0)!=(w[i-1]>=0)) zc++; pk=Math.Max(pk,Math.Abs(w[i])); } sb.Append(((double)zc/r).ToString("0.00")+"/"+pk.ToString("0.0")+" "); }
    Console.WriteLine(s+" off "+s.DataOffset+" len "+s.DataLength); Console.WriteLine(sb); } } }
