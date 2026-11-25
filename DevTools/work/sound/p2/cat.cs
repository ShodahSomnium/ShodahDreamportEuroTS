using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class CT { public static void Run(EtsFileSystem fs){ var c=new EtsTrafficCatalog(fs); int withMove=0, withHorn=0, gears=0; foreach(var v in c.Vehicles){ if(v.SoundMove.Length>0) withMove++; if(v.SoundHorn.Length>0) withHorn++; if(v.SoundGears!=null) gears++; }
  Console.WriteLine("vehicles "+c.Vehicles.Count+" (wire "+c.VehicleCount+"): sound_move "+withMove+", sound_horn "+withHorn+", sound_gears "+gears);
  foreach(var v in c.Vehicles.Where(x=>x.Kind>=3).Take(6)) Console.WriteLine(v.Unit+" kind "+v.Kind+" move ["+string.Join(", ",v.SoundMove)+"] horn ["+string.Join(", ",v.SoundHorn)+"] gears "+v.SoundGears);
  foreach(var v in c.Vehicles.Where(x=>x.Kind==1).Take(2)) Console.WriteLine(v.Unit+" police move ["+string.Join(", ",v.SoundMove)+"]"); } }
