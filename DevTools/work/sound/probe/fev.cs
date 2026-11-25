// FMOD Studio bank metadata explorer (reverse engineering aid). fev <bank>
using System; using System.Collections.Generic; using System.IO; using System.Text; using System.Linq;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class F {
  public class Ch { public string Id, Path; public int Off, Size; public byte[] D; }
  static List<Ch> chunks=new List<Ch>();
  static void Walk(byte[] d,int p,int end,string path){
    while(p+8<=end){ string id=Encoding.ASCII.GetString(d,p,4); int size=BitConverter.ToInt32(d,p+4); if(size<0||p+8+size>end) return;
      if(id=="LIST"){ string t=Encoding.ASCII.GetString(d,p+8,4); Walk(d,p+12,p+8+size,path+"/"+t); }
      else if(id!="SND "){ var c=new Ch{Id=id,Path=path,Off=p+8,Size=size,D=new byte[size]}; Array.Copy(d,p+8,c.D,0,size); chunks.Add(c); }
      p+=8+size; if((size&1)!=0) p++; } }
  static string G(byte[] d,int o){ return new Guid(d.Skip(o).Take(16).ToArray()).ToString(); }
  static float Fl(byte[] d,int o)=>BitConverter.ToSingle(d,o);
  public static void Run(EtsFileSystem fs, string bankPath){
    var bytes=fs.Read(bankPath); var bank=EtsFmodBank.Parse(bytes,out var err);
    Walk(bytes,12,bytes.Length,"");
    var names=new Dictionary<string,string>();
    var gtxt=fs.ReadText(bankPath+".guids"); if(gtxt==null){ var dir=bankPath.Substring(0,bankPath.LastIndexOf('/')); /* any guids file in folder */ }
    if(gtxt!=null) foreach(var l in gtxt.Split('\n')){ var t=l.Trim(); if(t.Length>40&&t[0]=='{') names[t.Substring(1,36)]=t.Substring(39); }
    // wavs
    foreach(var c in chunks.Where(c=>c.Id=="WAV ")){ int idx=BitConverter.ToUInt16(c.D,22); names[G(c.D,0)]="wav#"+idx+":"+(bank!=null&&idx<bank.Samples.Count?bank.Samples[idx].Name:"?"); }
    foreach(var c in chunks.Where(c=>c.Id=="PRMB")){ int nl=BitConverter.ToUInt16(c.D,24); string n=Encoding.ASCII.GetString(c.D,26,nl).TrimEnd('\0'); string g=G(c.D,0); if(!names.ContainsKey(g)) names[g]="param:"+n;
      int q=26+nl; Console.WriteLine("PARAM "+g+" "+n+" floats: "+string.Join(" ",Enumerable.Range(0,(c.Size-q)/4).Select(i=>Fl(c.D,q+i*4).ToString("0.###")))); }
    for(int i=0;i<chunks.Count;i++){ var c=chunks[i]; if(c.Id=="WAIB"){ names[G(c.D,0)]="inst("+N(names,G(c.D,16))+")"; } }
    foreach(var c in chunks.Where(c=>c.Id=="EVTB")) if(!names.ContainsKey(G(c.D,0))) names[G(c.D,0)]="evt?";
    // event -> timeline guid at +32
    foreach(var c in chunks.Where(c=>c.Id=="EVTB")) Console.WriteLine("EVENT "+N(names,G(c.D,0))+" timeline "+G(c.D,32)+" bus "+N(names,G(c.D,48))+" "+N(names,G(c.D,64)));
    foreach(var c in chunks.Where(c=>c.Id=="TLNB")) { names[G(c.D,0)]=names.TryGetValue(G(c.D,0),out var tn)?tn:"timeline"; }
    // curves
    var curves=new Dictionary<string,List<float[]>>();
    foreach(var c in chunks.Where(c=>c.Id=="CURV")){ int hdr=BitConverter.ToUInt16(c.D,32); int es=BitConverter.ToUInt16(c.D,34); int n=(hdr-1)/2; var pts=new List<float[]>();
      for(int k=0;k<n;k++){ int o=36+k*es; pts.Add(new float[]{Fl(c.D,o),Fl(c.D,o+4),Fl(c.D,o+8),BitConverter.ToInt32(c.D,o+12)}); } curves[G(c.D,0)]=pts; }
    var ctrls=new List<(string g,string owner,string curve,int prop)>();
    foreach(var c in chunks.Where(c=>c.Id=="CTRL")) ctrls.Add((G(c.D,0),G(c.D,16),G(c.D,32),BitConverter.ToInt32(c.D,48)));
    // layouts
    var ctrlAxis=new Dictionary<string,string>();
    for(int i=0;i<chunks.Count;i++){ var c=chunks[i]; if(c.Id!="PMLB") continue; string par=G(c.D,16); int hdr=BitConverter.ToUInt16(c.D,32); int n=(hdr-1)/2; var insts=new List<string>(); for(int k=0;k<n;k++) insts.Add(N(names,G(c.D,36+k*16)));
      var co=chunks[i+1]; var cl=new List<string>(); if(co.Id=="CTRO"&&co.Size>2){ int h2=BitConverter.ToUInt16(co.D,0); for(int k=0;k<(h2-1)/2;k++){ string cg=G(co.D,4+k*16); cl.Add(cg); ctrlAxis[cg]=N(names,par);} }
      Console.WriteLine("LAYOUT "+G(c.D,0)+" on "+N(names,par)+": "+insts.Count+" instruments ["+string.Join(", ",insts)+"] "+cl.Count+" controllers"); }
    for(int i=0;i<chunks.Count;i++){ var c=chunks[i]; if(c.Id!="TLNB") continue; var co=i+1<chunks.Count?chunks[i+1]:null; }
    // timeline controllers (CTRO after TRNS inside TMLN)
    foreach(var c in chunks.Where(c=>c.Id=="CTRO"&&c.Path.EndsWith("TMLN"))){ if(c.Size<=2) continue; int h2=BitConverter.ToUInt16(c.D,0); for(int k=0;k<(h2-1)/2;k++){ string cg=G(c.D,4+k*16); if(!ctrlAxis.ContainsKey(cg)) ctrlAxis[cg]="timeline"; } }
    // instruments
    for(int i=0;i<chunks.Count;i++){ var c=chunks[i]; if(c.Id!="WAIB"&&c.Id!="MUIB"&&c.Id!="SPIB"&&c.Id!="EVIB") continue; var ins=chunks[i+1]; if(ins.Id=="PLST") ins=chunks[i+2];
      string g=G(c.D,0); var d=ins.D;
      Console.WriteLine("INST "+N(names,g)+" "+ins.Size+"B tl="+N(names,G(d,0))+" f16="+Fl(d,16)+" f20="+Fl(d,20)+" i24="+BitConverter.ToInt32(d,24)+" i28="+BitConverter.ToInt32(d,28)+" f36="+Fl(d,36)+" f40.."+Fl(d,40)+","+Fl(d,44)+","+Fl(d,48)+","+Fl(d,52)+" p56="+N(names,G(d,56))+" f72="+Fl(d,72)+" f76="+Fl(d,76)+" out="+N(names,G(d,86)));
      Console.WriteLine("     tail: "+string.Join(" ",Enumerable.Range(102,ins.Size-102).Select(k=>d[k].ToString("x2"))));
      if(ins.Size>=162) Console.WriteLine("     cond "+N(names,G(d,ins.Size-28))+" "+BitConverter.ToUInt16(d,ins.Size-12).ToString("x4")+" ["+Fl(d,ins.Size-8)+" .. "+Fl(d,ins.Size-4)+"]");
      foreach(var ct in ctrls.Where(x=>x.owner==g)) Console.WriteLine("     ctrl prop "+ct.prop+" axis "+(ctrlAxis.TryGetValue(ct.g,out var ax)?ax:"?")+" pts "+Pts(curves,ct.curve));
    }
    // other controllers (buses etc.)
    var instGuids=new HashSet<string>(chunks.Where(c=>c.Id=="WAIB").Select(c=>G(c.D,0)));
    foreach(var ct in ctrls.Where(x=>!instGuids.Contains(x.owner))) Console.WriteLine("CTRL owner "+N(names,ct.owner)+" prop "+ct.prop+" axis "+(ctrlAxis.TryGetValue(ct.g,out var ax)?ax:"?")+" pts "+Pts(curves,ct.curve));
    foreach(var c in chunks.Where(c=>c.Id=="TRNB"||c.Id=="TLNB")) Console.WriteLine(c.Id+" "+c.Path+" "+string.Join(" ",Enumerable.Range(0,c.Size/4).Select(k=>BitConverter.ToUInt32(c.D,k*4).ToString("x8"))));
  }
  static string Pts(Dictionary<string,List<float[]>> cv,string g){ if(!cv.TryGetValue(g,out var p)) return "?"; return string.Join(" ",p.Select(x=>"("+x[0].ToString("0.##")+","+x[1].ToString("0.###")+(x[2]!=0?" s"+x[2].ToString("0.##"):"")+(x[3]!=0?" t"+x[3]:"")+")")); }
  static string N(Dictionary<string,string> n,string g)=> n.TryGetValue(g,out var s)?s:g.Substring(0,8);
}
