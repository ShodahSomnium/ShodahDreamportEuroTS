using System; using System.Collections.Generic; using System.Linq; using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
// A window of parsed sectors (LRU) with a node lookup over the ones held.
class Win {
  public EtsFileSystem Fs; public EtsMap Map; public int Cap;
  public Dictionary<long, EtsMapSector> Held = new Dictionary<long, EtsMapSector>();
  LinkedList<long> lru = new LinkedList<long>();
  public HashSet<long> Present = new HashSet<long>();
  public Win(int cap){ Fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _); Map = new EtsMap(Fs); Cap=cap;
    foreach(var n in Map.AvailableSectors){ int x=int.Parse(n.Substring(3,5)), z=int.Parse(n.Substring(8,5)); Present.Add(K(x,z)); } }
  public static long K(int x,int z)=> ((long)x<<32) ^ (uint)z;
  public static int KX(long k)=> (int)(k>>32); 
  public IEnumerable<(int x,int z)> All(){ foreach(var n in Map.AvailableSectors){ yield return (int.Parse(n.Substring(3,5)), int.Parse(n.Substring(8,5))); } }
  public EtsMapSector Get(int x,int z){ long k=K(x,z); if(!Present.Contains(k)) return null;
    if(Held.TryGetValue(k, out var s)){ lru.Remove(k); lru.AddFirst(k); return s; }
    s = EtsMapSector.Parse(x,z, Fs.Read(EtsMapSector.FileName(x,z,"base")), Fs.Read(EtsMapSector.FileName(x,z,"aux")), Fs.Read(EtsMapSector.FileName(x,z,"data")));
    Held[k]=s; lru.AddFirst(k);
    while(lru.Count>Cap){ var o=lru.Last.Value; lru.RemoveLast(); Held.Remove(o); }
    return s; }
  public EtsMapNode Node(ulong uid){ foreach(var s in Held.Values){ if(s!=null && s.NodesByUid.TryGetValue(uid, out var n)) return n; } return null; }
}
