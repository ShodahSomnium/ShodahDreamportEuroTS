using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class DBG { public static void Run(EtsFileSystem fs, string b){ var bytes=fs.Read(b); var st=EtsFmodStudio.Parse(bytes, fs.ReadText(b+".guids"), out var e);
 var ev=st.FindEvent("engine/engine"); foreach(var ins in ev.Instruments) Console.WriteLine(ins.Automations.Count+" all, params: "+string.Join(",",ins.Automations.Select(a=>a.Param+"/"+a.Property))); } }
