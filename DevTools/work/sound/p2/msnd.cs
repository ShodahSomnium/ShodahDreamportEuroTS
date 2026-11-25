// msnd <gx> <gz>: the map sound items within 2 km of a game-frame point, with their defs and events
using System; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
static class MS { public static void Run(EtsFileSystem fs, string[] a){ var defs=EtsMapSounds.ReadDefs(fs); Console.WriteLine("defs "+defs.Count);
  float gx=float.Parse(a[0]), gz=float.Parse(a[1]); EtsMapSector.SectorOf(gx,gz,out int sx,out int sz); int n=0;
  for(int x=sx-1;x<=sx+1;x++) for(int z=sz-1;z<=sz+1;z++){ var items=EtsMapSounds.ReadSector(fs,x,z,defs); n+=items.Count;
    foreach(var it in items){ float d=it.DistanceTo(gx,gz); if(d>2000) continue; Console.WriteLine((it.Area?"AREA ":"POINT")+" "+it.Token.PadRight(12)+" d "+d.ToString("0").PadLeft(5)+" max "+it.Def.MaxDist+" "+it.Def.Path+(it.Def.Sfx.Length>0?" sfx "+it.Def.Sfx+" x"+it.Def.SfxCount:"")+(it.Area?" "+it.Width.ToString("0")+"x"+it.Height.ToString("0"):"")+" at "+it.X.ToString("0")+","+it.Y.ToString("0")+","+it.Z.ToString("0")); } }
  Console.WriteLine("items in 9 sectors "+n); } }
