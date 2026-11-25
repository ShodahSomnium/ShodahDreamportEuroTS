// truckset <truckId...> : runs the world's own EtsGameTruckSounds.Read (by reflection) and EtsGameSound.Wanted on each
// truck, then checks every event resolves in its bank and counts what the world would decode (frames, MB, ms).
using System; using System.Collections.Generic; using System.Linq; using System.Reflection; using System.Diagnostics;
using SomniumSpace.Worlds.SomniumEuroTruck; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class TS {
  public static void Run(EtsFileSystem fs, string[] trucks){
    var read=typeof(EtsGameTruckSounds).GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Static);
    var ctor=typeof(EtsGameEvent).GetConstructors(BindingFlags.NonPublic|BindingFlags.Instance)[0];
    var wanted=typeof(EtsGameSound).GetMethod("Wanted",BindingFlags.NonPublic|BindingFlags.Static);
    var fixedFor=new Dictionary<string,string>{{"engine","Event Cone Angle=0,play=1"},{"exhaust","Event Cone Angle=0,play=1"},{"turbo","Event Cone Angle=0,play=1"},{"horn","Event Cone Angle=0,play=1"},{"aero_noise","aero_wnd=0"},{"air_brake","air_pressure=1,Event Cone Angle=0"},{"air_gear","Event Cone Angle=0"},{"tires_asphalt","skid_speed=0"},{"tires_gravel","skid_speed=0"},{"tires_grass","skid_speed=0"}};
    var used=new[]{"engine","exhaust","turbo","horn","reverse","aero_noise","air_brake","air_gear","blinker_on","blinker_off","stick_park_brake","stick_park_brake_off","hook_attach","hook_detach","trailer_cables","tires_asphalt","tires_gravel","tires_grass"};
    var bankCache=new Dictionary<string,(EtsFmodBank,EtsFmodStudio)>();
    foreach(var id in trucks){
      var spec=EtsVehicleSpec.Load(fs,id,out var fail); if(spec==null){ Console.WriteLine(id+": "+fail); continue; }
      var map=(Dictionary<string,string>)read.Invoke(null,new object[]{fs,spec.Folder,spec.EnginePath,spec.TransmissionPath,spec.ChassisPath});
      Console.WriteLine("== "+id+" engine "+spec.EnginePath+" gearbox "+spec.TransmissionPath+" chassis "+spec.ChassisPath+": "+map.Count+" sounds");
      long frames=0; double ms=0; var decoded=new HashSet<string>();
      foreach(var name in used){
        if(!map.TryGetValue(name,out var src)){ Console.WriteLine("   "+name+": (not listed) -> synthesised"); continue; }
        int h=src.IndexOf('#'); string bank=src.Substring(0,h), evp=src.Substring(h+1);
        if(!bankCache.TryGetValue(bank,out var bs)){ var bytes=fs.Read(bank); bs=(bytes==null?null:EtsFmodBank.Parse(bytes,out _), bytes==null?null:EtsFmodStudio.Parse(bytes,fs.ReadText(bank+".guids"),out _)); bankCache[bank]=bs; }
        var st=bs.Item2; var ev=st?.FindEvent(evp); if(ev==null){ Console.WriteLine("   "+name+": "+src+" -> EVENT NOT FOUND"); continue; }
        fixedFor.TryGetValue(name,out var fx); string[] fn=null; float[] fv=null; if(fx!=null){ var parts=fx.Split(','); fn=parts.Select(p=>p.Split('=')[0]).ToArray(); fv=parts.Select(p=>float.Parse(p.Split('=')[1])).ToArray(); }
        var gev=(EtsGameEvent)ctor.Invoke(new object[]{src,bank,evp,0f,false,fn,fv});
        long f0=frames; int nIns=0, nS=0; foreach(var ins in ev.Instruments){ if(!(bool)wanted.Invoke(null,new object[]{st,gev,ins})) continue; nIns++; foreach(var s in ins.Samples){ string k=bank+"|"+s; nS++; if(decoded.Add(k)){ var sw=Stopwatch.StartNew(); var pcm=bs.Item1.Decode(bs.Item1.Samples[s],out var de); ms+=sw.Elapsed.TotalMilliseconds; if(pcm==null) Console.WriteLine("      DECODE FAIL "+bs.Item1.Samples[s].Name+": "+de); else frames+=pcm.Length; } } }
        Console.WriteLine("   "+name.PadRight(22)+((frames-f0)/48000.0).ToString("0.0").PadLeft(6)+" s new  "+src+"  ("+nIns+"/"+ev.Instruments.Count+" instruments, "+nS+" samples, loops "+ev.Instruments.Count(i=>i.Loop)+")");
      }
      Console.WriteLine("   => "+decoded.Count+" distinct samples, "+frames+" floats = "+(frames*4/1048576.0).ToString("0.0")+" MB as float, decode "+ms.ToString("0")+" ms here");
    }
  }
}
