// tl <bank>: timeline entries (TLNB) -> instrument -> sample, with the two u32 after the instrument guid, and the INST start offset
using System; using System.Collections.Generic; using System.Linq; using System.Text; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class TL { class Ch{ public string Id; public int Off,Size; } static List<Ch> L=new List<Ch>(); static byte[] d;
  static void Walk(int p,int end){ while(p+8<=end){ string id=Encoding.ASCII.GetString(d,p,4); int size=BitConverter.ToInt32(d,p+4); if(size<0||p+8+size>end) return; if(id=="LIST") Walk(p+12,p+8+size); else if(id!="SND ") L.Add(new Ch{Id=id,Off=p+8,Size=size}); p+=8+size; if((size&1)!=0)p++; } }
  static Guid G(int o)=>new Guid(d.Skip(o).Take(16).ToArray());
  public static void Run(EtsFileSystem fs,string[] a){ d=fs.Read(a[0]); Walk(12,d.Length); var bank=EtsFmodBank.Parse(d,out _);
    var wav=new Dictionary<Guid,int>(); foreach(var c in L.Where(c=>c.Id=="WAV ")) wav[G(c.Off)]=BitConverter.ToUInt16(d,c.Off+22);
    var ins=new Dictionary<Guid,string>(); var instOff=new Dictionary<Guid,int>();
    for(int i=0;i<L.Count;i++){ var c=L[i]; if(c.Id=="WAIB"){ ins[G(c.Off)]=wav.TryGetValue(G(c.Off+16),out var s)?bank.Samples[s].Name:"?"; if(L[i+1].Id=="INST"){ var x=L[i+1]; instOff[G(c.Off)]=BitConverter.ToInt32(d,x.Off+x.Size-16); } } else if(c.Id!="INST"&&c.Size>=16&&!ins.ContainsKey(G(c.Off))) ins[G(c.Off)]=c.Id; }
    foreach(var c in L.Where(c=>c.Id=="TLNB")){ var sb=new StringBuilder("TLNB "+G(c.Off).ToString().Substring(0,8)+" size "+c.Size+":"); int p=c.Off+16; 
      // entries: u32 ?, u16 es, then guid + 2 u32 ...
      for(int q=c.Off+16;q+16<=c.Off+c.Size;q++){ var g=G(q); if(ins.TryGetValue(g,out var nm)){ sb.Append(" ["+nm+" start@"+(instOff.TryGetValue(g,out var so)?so:-1)+" u32s "+string.Join(",",Enumerable.Range(0,4).Where(k=>q+16+k*4+4<=c.Off+c.Size).Select(k=>BitConverter.ToUInt32(d,q+16+k*4)))+"]"); q+=15; } }
      Console.WriteLine(sb); } } }
