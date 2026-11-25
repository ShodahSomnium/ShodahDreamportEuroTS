// events <bank> : parsed FMOD Studio events (EtsFmodStudio) ; sweep <bank> <event> <param> <from> <to> <seconds> [k=v...] : offline render
using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Text;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class EV {
  public static void List(EtsFileSystem fs, string bankPath){
    var bytes=fs.Read(bankPath); var bank=EtsFmodBank.Parse(bytes,out var e1); var st=EtsFmodStudio.Parse(bytes, fs.ReadText(bankPath+".guids"), out var e2);
    if(st==null){ Console.WriteLine(e2); return; }
    Console.WriteLine("params: "+string.Join(", ",st.Params.Select(p=>p.Name+"["+p.Min+".."+p.Max+" d"+p.Default+"]")));
    foreach(var ev in st.Events.Values.OrderBy(x=>x.Path)){
      Console.WriteLine("EVENT "+ev.Path+" params {"+string.Join(",",ev.Params.Select(p=>st.Params[p].Name))+"} instruments "+ev.Instruments.Count);
      foreach(var ins in ev.Instruments){
        Console.WriteLine("   "+(ins.IsMulti?"multi":"wave ")+" ["+string.Join(",",ins.Samples.Select(s=>bank!=null&&s<bank.Samples.Count?bank.Samples[s].Name:"#"+s))+"] vol "+ins.VolumeDb+"dB pitch "+ins.PitchSemitones+"st loop "+ins.Loop+(ins.OnTimeline?" timeline":"")
          +(ins.RegionParam>=0?" region "+st.Params[ins.RegionParam].Name+" "+ins.RegionMin.ToString("0.##")+".."+ins.RegionMax.ToString("0.##"):"")
          +(ins.AutoPitchParam>=0?" autopitch "+st.Params[ins.AutoPitchParam].Name+" ref "+ins.AutoPitchRef+" atMin "+ins.AutoPitchAtMin.ToString("0.###"):"")
          +" auto "+ins.Automations.Count(a=>a.Param>=0)+" bus "+(ins.Out==null?"-":ins.Out.Id.ToString().Substring(0,8)+"("+Chain(ins.Out)+")"));
      }
    }
  }
  static string Chain(EtsFmodStudio.Bus b){ int n=0; var sb=new StringBuilder(); for(;b!=null&&n<6;b=b.Parent,n++) sb.Append(b.Automations.Count(a=>a.Param>=0)).Append(n<5&&b.Parent!=null?">":""); return sb.ToString(); }

  // Offline mix of an event: params swept linearly; every instrument's loop resampled at its pitch, scaled by its gain.
  public static void Sweep(EtsFileSystem fs, string bankPath, string evName, string param, float from, float to, float seconds, string[] fixedKv, string outPath){
    var bytes=fs.Read(bankPath); var bank=EtsFmodBank.Parse(bytes,out var e1); var st=EtsFmodStudio.Parse(bytes, fs.ReadText(bankPath+".guids"), out var e2);
    var ev=st.FindEvent(evName); if(ev==null){ Console.WriteLine("no event "+evName); return; }
    var values=new float[st.Params.Count]; for(int i=0;i<values.Length;i++) values[i]=float.NaN;
    foreach(var kv in fixedKv){ var p=kv.Split('='); foreach(int pi in ev.Params) if(st.Params[pi].Name==p[0]) values[pi]=float.Parse(p[1],System.Globalization.CultureInfo.InvariantCulture); }
    var sweepIdx=ev.Params.Where(pi=>st.Params[pi].Name==param).ToList();
    var pcm=new Dictionary<int,float[]>(); foreach(var ins in ev.Instruments) foreach(var s in ins.Samples) if(!pcm.ContainsKey(s)){ pcm[s]=bank.Decode(bank.Samples[s],out var de); }
    int rate=48000; int n=(int)(seconds*rate); var mix=new float[n]; var pos=new double[ev.Instruments.Count]; var rnd=new Random(1);
    for(int k=0;k<pos.Length;k++) pos[k]=0;
    var log=new StringBuilder(); float peak=0;
    for(int i=0;i<n;i+=480){ float v=from+(to-from)*i/(float)n; foreach(int pi in sweepIdx) values[pi]=v;
      for(int k=0;k<ev.Instruments.Count;k++){ var ins=ev.Instruments[k]; if(ins.OnTimeline||ins.Samples.Count==0) continue; st.Evaluate(ins,values,out float g,out float pitch); var src=pcm[ins.Samples[0]]; if(src==null) continue;
        if(i%(48000)==0 && g>0.001f) log.Append(string.Format("t={0:0.0}s {1}={2:0} {3} gain {4:0.000} pitch {5:0.000}\n", i/(float)rate, param, v, bank.Samples[ins.Samples[0]].Name, g, pitch));
        for(int j=i;j<Math.Min(n,i+480);j++){ int a=(int)pos[k]; double f=pos[k]-a; int len=src.Length; float x=src[a%len]*(float)(1-f)+src[(a+1)%len]*(float)f; mix[j]+=g*x; pos[k]+=pitch; if(pos[k]>=len) pos[k]-=len; } } }
    foreach(var x in mix) peak=Math.Max(peak,Math.Abs(x)); P.WritePng(Path.ChangeExtension(outPath,".png"), mix, 1, rate); Console.Write(log); double ss=0; for(int q=0;q<mix.Length;q++) ss+=mix[q]*mix[q]; Console.WriteLine("peak "+peak.ToString("0.000")+" rms "+Math.Sqrt(ss/mix.Length).ToString("0.000"));
    using(var w=new BinaryWriter(File.Create(outPath))){ int bytesN=n*2; w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36+bytesN); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate*2); w.Write((short)2); w.Write((short)16); w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytesN);
      float norm=peak>0.98f?0.98f/peak:1f; foreach(var f in mix) w.Write((short)Math.Max(-32768,Math.Min(32767,(int)(f*norm*32767)))); }
  }
}
