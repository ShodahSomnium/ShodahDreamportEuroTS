// ev <bank> [eventRegex]: events with instruments, sample names, lengths, loop, region, auto-pitch, automations (axis:prop:points)
using System; using System.Linq; using System.Text.RegularExpressions; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class EV2 { public static void Run(EtsFileSystem fs, string[] a){
  var bytes=fs.Read(a[0]); if(bytes==null){ Console.WriteLine("no bank "+a[0]); return; } var bank=EtsFmodBank.Parse(bytes,out var e1); var st=EtsFmodStudio.Parse(bytes, fs.ReadText(a[0]+".guids"), out var e2);
  if(st==null){ Console.WriteLine(e2); return; } var re=new Regex(a.Length>1?a[1]:".");
  Console.WriteLine("params: "+string.Join(", ",st.Params.Select(p=>p.Name+"["+p.Min+".."+p.Max+" d"+p.Default+"]")));
  foreach(var ev in st.Events.Values.OrderBy(x=>x.Path)){ if(!re.IsMatch(ev.Path)) continue;
    Console.WriteLine("EVENT "+ev.Path+" {"+string.Join(",",ev.Params.Select(p=>st.Params[p].Name))+"} "+ev.Instruments.Count+" instr");
    foreach(var ins in ev.Instruments){ Console.WriteLine("   "+(ins.IsScatter?"SCAT("+ins.ScatterMin+"-"+ins.ScatterMax+"s "+ins.ScatterDistance+"m p"+ins.ScatterPolyphony+")":ins.IsMulti?"multi":"wave ")+" ["+string.Join(",",ins.Samples.Select(s=>bank!=null&&s<bank.Samples.Count?bank.Samples[s].Name+"("+(bank.Samples[s].Frames/(double)bank.Samples[s].Rate).ToString("0.0")+"s)":"#"+s))+"] "+(ins.SilenceWeight>0?"silence "+(ins.SilenceWeight/(ins.SilenceWeight+ins.Weights.Sum())).ToString("0.00")+"x"+ins.SilenceSeconds.ToString("0.0")+"s ":"")+(ins.StartFrames>0||ins.LengthFrames>0?"trim "+ins.StartFrames+"+"+ins.LengthFrames+" ":"")+ins.VolumeDb+"dB "+ins.PitchSemitones+"st loop "+ins.Loop+(ins.OnTimeline?" TL":"")
      +(ins.RegionParam>=0?" region "+st.Params[ins.RegionParam].Name+" "+ins.RegionMin.ToString("0.##")+".."+ins.RegionMax.ToString("0.##"):"")
      +(ins.AutoPitchParam>=0?" ap "+st.Params[ins.AutoPitchParam].Name+" ref "+ins.AutoPitchRef+" min "+ins.AutoPitchAtMin.ToString("0.###"):"")
      +string.Concat(ins.Automations.Where(x=>x.Param>=0).Select(x=>" A["+st.Params[x.Param].Name+":"+x.Property+":"+string.Join(";",x.Curve.X.Zip(x.Curve.Y,(p,q)=>p.ToString("0.##")+"/"+q.ToString("0.##")))+"]"))
      +BusA(st,ins)); } } }
  static string BusA(EtsFmodStudio st, EtsFmodStudio.Instrument ins){ string s=""; int g=0; for(var b=ins.Out;b!=null&&g<8;b=b.Parent,g++) foreach(var x in b.Automations.Where(x=>x.Param>=0)) s+=" B"+g+"["+st.Params[x.Param].Name+":"+x.Property+":"+string.Join(";",x.Curve.X.Zip(x.Curve.Y,(p,q)=>p.ToString("0.##")+"/"+q.ToString("0.##")))+"]"; return s; }
}
static class X { public static void Run(EtsFileSystem fs, string[] a){ Console.WriteLine("?"); } }
