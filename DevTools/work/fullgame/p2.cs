using System; using System.IO; using System.Text; using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class P {
 static void Main(string[] a){
  var fs=EtsFileSystem.Mount(a[0],out string rep);
  string t=fs.ReadText("/version.sii"); Console.WriteLine(t.Substring(0,Math.Min(1500,t.Length)));
  var b=fs.Read("/map/europe.mbd"); Console.WriteLine(BitConverter.ToString(b));
  Console.WriteLine(fs.ReadText("/dlc_iveco_sway.manifest.sii")); Console.WriteLine(fs.ReadText("/dlc_daf_xd.manifest.sii"));
  if(fs.TryList("/dlc",out var f,out var d)) Console.WriteLine("/dlc: "+string.Join(",",f)+" | "+string.Join(",",d));
  if(fs.TryList("/map",out f,out d)) Console.WriteLine("/map: "+string.Join(",",f)+" | "+string.Join(",",d));
  if(fs.TryList("/def",out f,out d)) Console.WriteLine("/def: "+string.Join(",",f)+" | "+string.Join(",",d));
 }
}
