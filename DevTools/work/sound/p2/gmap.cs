// gmap <bank>: every chunk's leading guid -> chunk id; resolves each PLST entry (MUIB/SPIB) to what it names; SPIB fields
using System; using System.Collections.Generic; using System.Linq; using System.Text; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class GM { 
  class Ch{ public string Id,Path; public int Off,Size; }
  static List<Ch> L=new List<Ch>(); static byte[] d;
  static void Walk(int p,int end,string path){ while(p+8<=end){ string id=Encoding.ASCII.GetString(d,p,4); int size=BitConverter.ToInt32(d,p+4); if(size<0||p+8+size>end) return;
      if(id=="LIST") Walk(p+12,p+8+size,path+"/"+Encoding.ASCII.GetString(d,p+8,4)); else if(id!="SND ") L.Add(new Ch{Id=id,Path=path,Off=p+8,Size=size}); p+=8+size; if((size&1)!=0)p++; } }
  static Guid G(int o)=>new Guid(d.Skip(o).Take(16).ToArray());
  public static void Run(EtsFileSystem fs,string[] a){ d=fs.Read(a[0]); Walk(12,d.Length,""); var bank=EtsFmodBank.Parse(d,out _);
    var kind=new Dictionary<Guid,string>(); foreach(var c in L) if(c.Size>=16 && c.Id!="INST"&&c.Id!="PLST"&&c.Id!="CTRO"&&c.Id!="LCNT"&&c.Id!="PROP"&&c.Id!="CURV"&&c.Id!="CTRL"){ var g=G(c.Off); if(!kind.ContainsKey(g)) kind[g]=c.Id+(c.Id=="WAV "?"#"+BitConverter.ToUInt16(d,c.Off+22)+":"+bank.Samples[BitConverter.ToUInt16(d,c.Off+22)].Name:""); }
    var names=new Dictionary<Guid,string>(); var gt=fs.ReadText(a[0]+".guids"); if(gt!=null) foreach(var l in gt.Split('\n')){ var t=l.Trim(); if(t.Length>40&&t[0]=='{') names[Guid.Parse(t.Substring(1,36))]=t.Substring(39); }
    for(int i=0;i<L.Count;i++){ var c=L[i]; if(c.Id!="MUIB"&&c.Id!="SPIB"&&c.Id!="EVIB"&&c.Id!="EVTB"&&c.Id!="TLNB"&&c.Id!="PMLB"&&c.Id!="SLNB"&&c.Id!="TRTL") continue; var g=G(c.Off);
      var sb=new StringBuilder(c.Id+" "+g.ToString().Substring(0,8)+(names.TryGetValue(g,out var nm)?" "+nm:"")+" size "+c.Size+" path "+c.Path);
      if(c.Id=="SPIB") sb.Append(" fields: "+string.Join(" ",Enumerable.Range(4,8).Select(k=>{ int o=c.Off+k*4; uint u=BitConverter.ToUInt32(d,o); return u<1000?u.ToString():BitConverter.ToSingle(d,o).ToString("0.###"); })));
      if(c.Id=="EVIB"||c.Id=="TLNB"||c.Id=="EVTB"||c.Id=="SLNB"||c.Id=="TRTL") for(int p=c.Off+16;p+16<=c.Off+c.Size;p++){ var x=G(p); if(kind.TryGetValue(x,out var kk)||names.ContainsKey(x)){ sb.Append(" ->"+(kk??"")+" "+x.ToString().Substring(0,8)+(names.TryGetValue(x,out var n2)?"("+n2+")":"")); p+=15; } }
      Console.WriteLine(sb);
      int j=i+1; if(j<L.Count&&L[j].Id=="PLST"){ var pl=L[j]; int n=(BitConverter.ToUInt16(d,pl.Off+8)-1)/2, es=BitConverter.ToUInt16(d,pl.Off+10); Console.Write("    PLST mode "+BitConverter.ToUInt32(d,pl.Off)+","+BitConverter.ToUInt32(d,pl.Off+4)+": ");
        for(int k=0;k<n;k++){ var x=G(pl.Off+12+k*es); float w=BitConverter.ToSingle(d,pl.Off+12+k*es+16); Console.Write((kind.TryGetValue(x,out var kk)?kk:"?")+" "+x.ToString().Substring(0,8)+" w"+w+"; "); } Console.WriteLine(); j++; }
      if(j<L.Count&&L[j].Id=="INST"){ var ins=L[j]; Console.WriteLine("    INST "+ins.Size+" vol "+BitConverter.ToSingle(d,ins.Off+16)+" pitch "+BitConverter.ToSingle(d,ins.Off+20)+" loop "+BitConverter.ToInt32(d,ins.Off+24)+" tl "+G(ins.Off).ToString().Substring(0,8)+" f32.."+string.Join(",",Enumerable.Range(0,6).Select(k=>BitConverter.ToSingle(d,ins.Off+32+k*4).ToString("0.###")))); }
    }
    foreach(var c in L.Where(c=>c.Id=="WAIB")){ var g=G(c.Off); var w=G(c.Off+16); }
  } }
