// tmap [truck ids...]: the truck's full sound map as the world resolves it (EtsGameTruckSounds.Read by reflection); no ids = list trucks
using System; using System.Collections.Generic; using System.Linq; using System.Reflection;
using SomniumSpace.Worlds.SomniumEuroTruck; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class TM { public static void Run(EtsFileSystem fs, string[] ids){
  if(ids.Length==0){ fs.TryList("/def/vehicle/truck", out var f, out var d); Console.WriteLine(string.Join(" ",d)); return; }
  var read=typeof(EtsGameTruckSounds).GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Static);
  foreach(var id in ids){ var spec=EtsVehicleSpec.Load(fs,id,out var fail); if(spec==null){ Console.WriteLine(id+": "+fail); continue; }
    var map=(Dictionary<string,string>)read.Invoke(null,new object[]{fs,spec.Folder,spec.EnginePath,spec.TransmissionPath,spec.ChassisPath});
    Console.WriteLine("== "+id+" folder "+spec.Folder); foreach(var kv in map.OrderBy(k=>k.Key)) Console.WriteLine("  "+kv.Key.PadRight(26)+kv.Value);
    Console.WriteLine("  cabin sui: "+(fs.ReadText(spec.Folder+"/cabin/sound.sui")??"-").Replace("\n","\n     "));
    Console.WriteLine("  interior sui: "+(fs.ReadText(spec.Folder+"/interior/sound.sui")??"-").Replace("\n","\n     "));
  } } }
