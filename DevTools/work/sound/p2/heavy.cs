using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class HV { public static void Run(EtsFileSystem fs, string[] a){ var defs=EtsMapSounds.ReadDefs(fs); var paths=new SortedSet<string>(); foreach(var d in defs.Values){ paths.Add(d.Path); if(d.Sfx.Length>0) paths.Add(d.Sfx); }
  // how often each def is used on the map
  var use=new Dictionary<string,int>(); fs.TryList("/map/europe", out var files, out _); foreach(var f in files.Where(x=>x.EndsWith(".snd"))){ string t10=f.Substring(f.Length-14,10); int sx=int.Parse(t10.Substring(0,5)), sz=int.Parse(t10.Substring(5,5)); foreach(var it in EtsMapSounds.ReadSector(fs,sx,sz,defs)){ use[it.Def.Path]=use.TryGetValue(it.Def.Path,out var c)?c+1:1; if(it.Def.Sfx.Length>0) use[it.Def.Sfx]=use.TryGetValue(it.Def.Sfx,out var c2)?c2+1:1; } }
  var banks=new Dictionary<string,(EtsFmodBank,EtsFmodStudio)>(); var rows=new List<(double,string)>(); var seen=new HashSet<string>(); double tot=0, totStream=0;
  foreach(var p in paths){ if(!use.ContainsKey(p)) continue; int h=p.IndexOf('#'); string bank=p.Substring(0,h), ev=p.Substring(h+1);
    if(!banks.TryGetValue(bank,out var bs)){ var bytes=fs.Read(bank); bs=(EtsFmodBank.Parse(bytes,out _), EtsFmodStudio.Parse(bytes,fs.ReadText(bank+".guids"),out _)); banks[bank]=bs; }
    var e=bs.Item2.FindEvent(ev); if(e==null) continue; double s=0, st=0; foreach(var ins in e.Instruments) foreach(var si in ins.Samples){ var smp=bs.Item1.Samples[si]; double sec=smp.Frames/(double)smp.Rate; string k=bank+si; if(ins.Loop&&!ins.IsMulti&&sec>12){ st+=smp.DataLength/1048576.0; continue; } if(!seen.Add(k)) continue; s+=ins.Loop?Math.Min(sec,20):sec; }
    tot+=s; totStream+=st; rows.Add((s,p+" used "+use[p]+" decoded "+s.ToString("0")+" s ("+(s*48000*4/1048576).ToString("0.0")+" MB) streamed "+st.ToString("0.0")+" MB")); }
  foreach(var r in rows.OrderByDescending(x=>x.Item1).Take(25)) Console.WriteLine(r.Item2);
  Console.WriteLine("used events "+rows.Count+" decoded total "+tot.ToString("0")+" s = "+(tot*48000*4/1048576).ToString("0")+" MB float; stream packets total "+totStream.ToString("0")+" MB"); } }
