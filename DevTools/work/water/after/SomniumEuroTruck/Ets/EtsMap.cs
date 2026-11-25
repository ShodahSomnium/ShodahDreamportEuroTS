using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The map as a whole: sectors on demand, and the definition tables the sectors refer to by
    /// token - road looks, prefabs, models, building schemes. Knows nothing about Unity; the
    /// builder turns what this returns into geometry.
    /// </summary>
    public sealed class EtsMap
    {
        private readonly EtsFileSystem _files;
        private readonly Dictionary<long, EtsMapSector> _sectors = new Dictionary<long, EtsMapSector>();
        private readonly Dictionary<ulong, EtsMapNode> _nodes = new Dictionary<ulong, EtsMapNode>();

        /// <summary>Roads by uid over every adopted sector (FindRoad; the railing builder asks it for a road's neighbours).</summary>
        private readonly Dictionary<ulong, EtsMapRoad> _roads = new Dictionary<ulong, EtsMapRoad>();
        private readonly Dictionary<string, EtsPrefabDescriptor> _descriptors = new Dictionary<string, EtsPrefabDescriptor>(StringComparer.Ordinal);

        private EtsSiiFile _roadLooks;
        private EtsSiiFile _buildingModels;
        private EtsSiiFile _curveModels;
        private EtsSiiFile _prefabs;
        private EtsSiiFile _models;
        private EtsSiiFile _schemes;
        private EtsSiiFile _roadEdges;
        private EtsSiiFile _signs;
        private EtsSiiFile _terrainProfiles;
        private EtsSiiFile _terrainMaterials;
        private EtsSiiFile _vegetation;
        private EtsSiiFile _vegetationModels;
        private EtsSiiFile _companies;
        private EtsSiiFile _cities;

        /// <summary>Sector names present in the archives, from the map folder listing.</summary>
        public readonly List<string> AvailableSectors = new List<string>();

        public EtsMap(EtsFileSystem files)
        {
            _files = files;

            if (_files.TryList("/map/europe", out List<string> sectorFiles, out _))
            {
                foreach (string file in sectorFiles)
                {
                    if (file.EndsWith(".base", StringComparison.Ordinal))
                    {
                        AvailableSectors.Add(file.Substring(0, file.Length - 5));
                    }
                }
            }
        }

        public int SectorCount => AvailableSectors.Count;

        private static long Key(int sectorX, int sectorZ)
        {
            return ((long)sectorX << 32) ^ (uint)sectorZ;
        }

        /// <summary>The sector at grid coordinates, read once. Null when the map has none there.</summary>
        public EtsMapSector GetSector(int sectorX, int sectorZ)
        {
            long key = Key(sectorX, sectorZ);

            if (_sectors.TryGetValue(key, out EtsMapSector cached))
            {
                return cached;
            }

            // Read here, on the caller's thread, even when a background read of it is on its way (ReadAhead): the
            // caller needs it now, and the background copy is dropped when it lands (Adopt).
            Adopt(key, ReadSector(_files, sectorX, sectorZ));
            return _sectors[key];
        }

        /// <summary>The three files of one sector, parsed. Pure (the archive read is thread safe, EtsWorker's notes), so it runs on the reader thread too.</summary>
        private static EtsMapSector ReadSector(EtsFileSystem files, int sectorX, int sectorZ)
        {
            byte[] baseBytes = files.Read(EtsMapSector.FileName(sectorX, sectorZ, "base"));
            byte[] auxBytes = files.Read(EtsMapSector.FileName(sectorX, sectorZ, "aux"));
            byte[] dataBytes = files.Read(EtsMapSector.FileName(sectorX, sectorZ, "data"));
            return baseBytes == null && auxBytes == null ? null : EtsMapSector.Parse(sectorX, sectorZ, baseBytes, auxBytes, dataBytes);
        }

        /// <summary>Main thread: a read sector joins the map - cached, its nodes indexed - unless it already has.</summary>
        private void Adopt(long key, EtsMapSector sector)
        {
            if (_sectors.ContainsKey(key))
            {
                return;
            }

            _sectors[key] = sector;

            if (sector != null)
            {
                foreach (EtsMapNode node in sector.Nodes) _nodes[node.Uid] = node;
                foreach (EtsMapRoad road in sector.Roads) _roads[road.Uid] = road;
            }
        }

        // ------------------------------------------------------------------ reading ahead, off the main thread

        // Why: a sector costs 43 ms to read and parse on average and up to 308 ms (harness Program1400; the node
        // table walk alone was 16.5 ms of it after the Phase 2 fix, probe 2095), and every reader of the map did it
        // on the main thread - the map viewer's first build (all sectors within its radius + 500 m in one frame, so
        // a joiner arriving before the whole-map index has passed their city froze for up to about a second), its
        // stream (a sector crossed into while driving) and the whole-map index. In a headset that is a frozen
        // frame each time. EtsMapSector.Parse is pure C# (no UnityEngine call, no static it writes - GuardShown is
        // only read) and the archive read is thread safe, so a sector can be read on EtsWorker's thread and only
        // join the map here, on the main thread: a dictionary insert per node (up to 53,288 nodes, sec+0002-0003, the
        // largest of the demo, probe 2096), one sector at least per Pump and more while under 2 ms. Probe 2098 at five
        // HQ cities: the worst poll that adopted took 1.8 ms (5.8 ms with a garbage collection in it), where the same
        // sectors read in one call took 23-294 ms.

        private EtsWorker _reader;
        private bool _closed;
        private readonly HashSet<long> _inFlight = new HashSet<long>();
        private readonly Stopwatch _pumpClock = new Stopwatch();
        private int _pumped;
        private Func<bool> _pumpMore;

        /// <summary>What went wrong on the reader thread, one line per sector ("sec+0001-0002: IndexOutOfRangeException ..."); such a sector counts as absent.</summary>
        public readonly List<string> ReadFailures = new List<string>();

        /// <summary>Sectors read on the reader thread and adopted so far, for the log.</summary>
        public int ReadInBackground { get; private set; }

        /// <summary>True when the reader runs on its own thread; false when EtsWorker could not start one and reads one sector per Pump on the main thread instead.</summary>
        public bool ReaderThreaded => _reader == null || _reader.Threaded;

        /// <summary>Whether this sector has been read (GetSector returns without reading). Adopts finished background reads first.</summary>
        public bool IsRead(int sectorX, int sectorZ)
        {
            Pump();
            return _sectors.ContainsKey(Key(sectorX, sectorZ));
        }

        /// <summary>
        /// True when the sector is read; otherwise queues it for the reader thread (once) and returns false, and a
        /// later Pump adopts it. After Close it reads on the spot, as GetSector does.
        /// </summary>
        public bool ReadAhead(int sectorX, int sectorZ)
        {
            Pump();
            return Queue(sectorX, sectorZ);
        }

        /// <summary>ReadAhead without the Pump: TryReadAround pumps once for all its squares, so one poll adopts within one budget.</summary>
        private bool Queue(int sectorX, int sectorZ)
        {
            long key = Key(sectorX, sectorZ);

            if (_sectors.ContainsKey(key))
            {
                return true;
            }

            if (_closed)
            {
                GetSector(sectorX, sectorZ);
                return true;
            }

            if (_inFlight.Contains(key))
            {
                return false;
            }

            if (_reader == null)
            {
                _reader = new EtsWorker("Ets map reader");
            }

            _inFlight.Add(key);
            EtsFileSystem files = _files;
            string name = EtsMapSector.FileName(sectorX, sectorZ, "base");
            _reader.Post(() => ReadSector(files, sectorX, sectorZ), (result, failure) => Landed(key, name, result as EtsMapSector, failure));
            return false;
        }

        private void Landed(long key, string name, EtsMapSector sector, Exception failure)
        {
            _inFlight.Remove(key);

            if (failure != null)
            {
                // The same fault would have been thrown into the caller on the main thread; here the sector counts as
                // absent and the reason is kept for the log. Probe 2096 parsed all 282 demo sectors with none.
                ReadFailures.Add(name + ": " + failure.GetType().Name + ": " + failure.Message);
                sector = null;
            }

            if (!_sectors.ContainsKey(key))
            {
                ReadInBackground++;
            }

            Adopt(key, sector);
        }

        /// <summary>
        /// Main thread: adopts background reads that have finished - at least one, then more while under 2 ms.
        /// Called by IsRead, ReadAhead and TryReadAround, so a caller polling those needs nothing else. Without the
        /// reader thread it reads one queued sector here instead (EtsWorker.Drain).
        /// </summary>
        public void Pump()
        {
            if (_reader == null)
            {
                return;
            }

            if (_pumpMore == null)
            {
                _pumpMore = () => _pumped++ == 0 || _pumpClock.ElapsedTicks < Stopwatch.Frequency / 500;
            }

            _pumped = 0;
            _pumpClock.Restart();
            _reader.Drain(_pumpMore);
        }

        /// <summary>
        /// Every sector whose square touches the circle - the same sectors in the same order as SectorsAround - once
        /// all of them are read. Until then it queues the missing ones for the reader thread, leaves 'into' empty and
        /// returns false; poll it once a frame. 'ready' and 'total' count the squares (for a progress bar).
        /// </summary>
        public bool TryReadAround(float x, float z, float radius, List<EtsMapSector> into, out int ready, out int total)
        {
            into.Clear();
            ready = 0;
            total = 0;
            Pump();
            EtsMapSector.SectorOf(x - radius, z - radius, out int x0, out int z0);
            EtsMapSector.SectorOf(x + radius, z + radius, out int x1, out int z1);

            for (int sx = x0; sx <= x1; sx++)
            {
                for (int sz = z0; sz <= z1; sz++)
                {
                    total++;

                    if (Queue(sx, sz))
                    {
                        ready++;
                    }
                }
            }

            if (ready < total)
            {
                return false;
            }

            for (int sx = x0; sx <= x1; sx++)
            {
                for (int sz = z0; sz <= z1; sz++)
                {
                    EtsMapSector sector = _sectors[Key(sx, sz)];

                    if (sector != null)
                    {
                        into.Add(sector);
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Stops the reader thread (EtsMapViewer.OnDestroy): a background thread outlives the scene otherwise. Reads
        /// still asked for afterwards happen on the caller's thread.
        /// </summary>
        public void Close()
        {
            _closed = true;

            if (_reader != null)
            {
                _reader.Dispose();
                _reader = null;
            }

            _inFlight.Clear();
        }

        /// <summary>Every sector whose square touches a circle around a point.</summary>
        public List<EtsMapSector> SectorsAround(float x, float z, float radius)
        {
            var result = new List<EtsMapSector>();
            EtsMapSector.SectorOf(x - radius, z - radius, out int x0, out int z0);
            EtsMapSector.SectorOf(x + radius, z + radius, out int x1, out int z1);

            for (int sx = x0; sx <= x1; sx++)
            {
                for (int sz = z0; sz <= z1; sz++)
                {
                    EtsMapSector sector = GetSector(sx, sz);

                    if (sector != null)
                    {
                        result.Add(sector);
                    }
                }
            }

            return result;
        }

        /// <summary>Every sector read so far.</summary>
        public IEnumerable<EtsMapSector> LoadedSectors
        {
            get
            {
                foreach (EtsMapSector sector in _sectors.Values)
                {
                    if (sector != null) yield return sector;
                }
            }
        }

        /// <summary>A road by id, searched across every loaded sector.</summary>
        /// <summary>The prefab item with this uid among the sectors read so far, or null.</summary>
        public EtsMapPrefab FindPrefab(ulong uid)
        {
            foreach (EtsMapSector sector in _sectors.Values)
            {
                if (sector == null) continue;

                foreach (EtsMapPrefab prefab in sector.Prefabs)
                {
                    if (prefab.Uid == uid) return prefab;
                }
            }

            return null;
        }

        public EtsMapRoad FindRoad(ulong uid)
        {
            if (_roads.TryGetValue(uid, out EtsMapRoad indexed))
            {
                return indexed;
            }

            foreach (EtsMapSector sector in _sectors.Values)
            {
                if (sector == null) continue;

                foreach (EtsMapRoad road in sector.Roads)
                {
                    if (road.Uid == uid) return road;
                }
            }

            return null;
        }

        /// <summary>A node by id, searched across every loaded sector - roads cross sector edges.</summary>
        public EtsMapNode FindNode(ulong uid)
        {
            if (_nodes.TryGetValue(uid, out EtsMapNode indexed))
            {
                return indexed;
            }

            foreach (EtsMapSector sector in _sectors.Values)
            {
                if (sector != null && sector.NodesByUid.TryGetValue(uid, out EtsMapNode node))
                {
                    return node;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ definitions

        /// <summary>
        /// The road look unit for a road's token. Looks are named "road.&lt;token&gt;" in
        /// road_look.sii and road_look.template.sii; the map stores only the part after the dot.
        /// </summary>
        public EtsSiiUnit RoadLook(ulong token)
        {
            if (_roadLooks == null)
            {
                _roadLooks = _files.ReadDefinitionFamily("/def/world", "road_look");
            }

            return _roadLooks.Find("road." + EtsToken.Decode(token));
        }

        public EtsSiiUnit PrefabDefinition(ulong token)
        {
            if (_prefabs == null)
            {
                _prefabs = _files.ReadDefinitionFamily("/def/world", "prefab");
            }

            return _prefabs.Find("prefab." + EtsToken.Decode(token));
        }

        public EtsSiiUnit ModelDefinition(ulong token)
        {
            if (_models == null)
            {
                _models = _files.ReadDefinitionFamily("/def/world", "model");
            }

            return _models.Find("model." + EtsToken.Decode(token));
        }

        public EtsSiiUnit BuildingScheme(ulong token)
        {
            if (_schemes == null)
            {
                _schemes = _files.ReadDefinitionFamily("/def/world", "building_scheme");
            }

            return _schemes.Find("bld_scheme." + EtsToken.Decode(token));
        }

        /// <summary>
        /// One model of a building scheme, named by the scheme's "models" array as
        /// "bld_model.&lt;name&gt;". The scheme says what a run of fence or railing is made of; the
        /// model says which pmd to repeat and how long one element is.
        /// </summary>
        public EtsSiiUnit BuildingModel(string unitName)
        {
            if (_buildingModels == null)
            {
                _buildingModels = _files.ReadDefinitionFamily("/def/world", "building_model");
            }

            return _buildingModels.Find(unitName);
        }

        /// <summary>The model a curve item repeats: unit "curve.&lt;token&gt;" in curve_model.sii.</summary>
        public EtsSiiUnit CurveModel(ulong token)
        {
            if (_curveModels == null)
            {
                _curveModels = _files.ReadDefinitionFamily("/def/world", "curve_model");
            }

            return _curveModels.Find("curve." + EtsToken.Decode(token));
        }

        public EtsSiiUnit RoadEdge(ulong token)
        {
            if (_roadEdges == null)
            {
                _roadEdges = _files.ReadDefinitionFamily("/def/world", "road_edge");
            }

            return _roadEdges.Find("road_edge." + EtsToken.Decode(token));
        }

        /// <summary>
        /// A legacy city road's sidewalk: unit "sidewalk.&lt;token&gt;" in /def/world/road_sidewalk.sii,
        /// whose path is the pavement's material (sidewalk.2 = /material/road/small_block_pavement.mat).
        /// </summary>
        public EtsSiiUnit Sidewalk(ulong token)
        {
            if (_sidewalks == null)
            {
                _sidewalks = _files.ReadDefinitionFamily("/def/world", "road_sidewalk");
            }

            return _sidewalks.Find("sidewalk." + EtsToken.Decode(token));
        }

        private EtsSiiFile _sidewalks;
        private EtsSiiFile _roadMaterials;
        private EtsSiiFile _railings;
        private EtsSiiFile _roadDefinition;

        /// <summary>
        /// A legacy road's material: unit "road_mat.&lt;token&gt;" in /def/world/road_material.sii, whose path is the
        /// asphalt atlas the look's tex0/tex1 bands address (road_mat.0 = /material/road/asphalt.mat) and whose kerb
        /// is the kerb material. Null for an empty token or an unknown one.
        /// </summary>
        public EtsSiiUnit RoadMaterial(ulong token)
        {
            if (_roadMaterials == null)
            {
                _roadMaterials = _files.ReadDefinitionFamily("/def/world", "road_material");
            }

            return _roadMaterials.Find("road_mat." + EtsToken.Decode(token));
        }

        /// <summary>A road railing: unit "railing.&lt;token&gt;" in /def/world/railing.sii (model_desc, look_name, bridge).</summary>
        public EtsSiiUnit Railing(ulong token)
        {
            if (token == 0)
            {
                return null;
            }

            if (_railings == null)
            {
                _railings = _files.ReadDefinitionFamily("/def/world", "railing");
            }

            return _railings.Find("railing." + EtsToken.Decode(token));
        }

        /// <summary>
        /// The legacy road renderer's own settings, unit road.definition in /def/world/road.sii: full_line_width 0.5,
        /// broken_line_width 0.2 and road_lines "/material/road/lines.mat" on 1.61. Null when absent.
        /// </summary>
        public EtsSiiUnit RoadDefinition()
        {
            if (_roadDefinition == null)
            {
                _roadDefinition = _files.ReadDefinitionFamily("/def/world", "road");
            }

            return _roadDefinition.Find("road.definition");
        }

        public EtsSiiUnit SignModel(ulong token)
        {
            if (_signs == null)
            {
                _signs = _files.ReadDefinitionFamily("/def/world", "sign");
            }

            return _signs.Find("sign." + EtsToken.Decode(token));
        }

        /// <summary>A terrain profile: unit "terrain.&lt;token&gt;" with a step array and a height array.</summary>
        public EtsSiiUnit TerrainProfile(ulong token)
        {
            if (_terrainProfiles == null)
            {
                _terrainProfiles = _files.ReadDefinitionFamily("/def/world", "terrain_profile");
            }

            return _terrainProfiles.Find("terrain." + EtsToken.Decode(token));
        }

        /// <summary>A terrain brush material: unit "terrain_mat.&lt;token&gt;" with a path and a colour.</summary>
        public EtsSiiUnit TerrainMaterial(ulong token)
        {
            if (_terrainMaterials == null)
            {
                _terrainMaterials = _files.ReadDefinitionFamily("/def/world", "terrain_material");
            }

            return _terrainMaterials.Find("terrain_mat." + EtsToken.Decode(token));
        }

        /// <summary>A vegetation set: unit "veg.&lt;token&gt;" in vegetation.sii, a list of vegetation models.</summary>
        public EtsSiiUnit Vegetation(ulong token)
        {
            if (_vegetation == null)
            {
                _vegetation = _files.ReadDefinitionFamily("/def/world", "vegetation");
            }

            return _vegetation.Find("veg." + EtsToken.Decode(token));
        }

        /// <summary>A vegetation model: unit "vegetmodel.&lt;name&gt;" with sprite, medium and high poly model paths.</summary>
        public EtsSiiUnit VegetationModel(string unitName)
        {
            if (_vegetationModels == null)
            {
                _vegetationModels = _files.ReadDefinitionFamily("/def/world", "vegetation_model");
            }

            return _vegetationModels.Find(unitName);
        }

        /// <summary>A company definition by token: the unit in company.sii whose name ends in the token.</summary>
        public EtsSiiUnit Company(ulong token)
        {
            if (_companies == null)
            {
                _companies = _files.ReadDefinitionFamily("/def", "company");
            }

            return FindBySuffix(_companies, "." + EtsToken.Decode(token));
        }

        /// <summary>A city definition by token: unit "city.&lt;token&gt;" in city.sii.</summary>
        public EtsSiiUnit City(ulong token)
        {
            if (_cities == null)
            {
                _cities = _files.ReadDefinitionFamily("/def", "city");
            }

            return _cities.Find("city." + EtsToken.Decode(token)) ?? FindBySuffix(_cities, "." + EtsToken.Decode(token));
        }

        private static EtsSiiUnit FindBySuffix(EtsSiiFile file, string suffix)
        {
            if (file == null) return null;

            foreach (EtsSiiUnit unit in file.Units)
            {
                if (unit.Name != null && unit.Name.EndsWith(suffix, StringComparison.Ordinal)) return unit;
            }

            return null;
        }

        /// <summary>A descriptor read off the main thread (ReadDescriptor), for AdoptDescriptor: the descriptor, or why not.</summary>
        public sealed class DescriptorRead
        {
            public EtsPrefabDescriptor Descriptor;
            public string Failure;
        }

        /// <summary>Whether Descriptor would answer from its cache (a failure is cached too).</summary>
        public bool HasDescriptor(string path)
        {
            return string.IsNullOrEmpty(path) || _descriptors.ContainsKey(path);
        }

        /// <summary>
        /// Pure (the archive read is thread safe, EtsPrefabDescriptor.Parse writes no shared state): the read and parse
        /// Descriptor does, for a worker thread - the map viewer's stream prefetches a prefab's descriptor before the
        /// prefab is built (EtsMapViewer). Never throws.
        /// </summary>
        public static DescriptorRead ReadDescriptor(EtsFileSystem files, string path)
        {
            var read = new DescriptorRead();

            try
            {
                byte[] bytes = files.Read(path);

                if (bytes == null)
                {
                    read.Failure = "descriptor not found: " + path;
                }
                else
                {
                    read.Descriptor = EtsPrefabDescriptor.Parse(bytes);
                }
            }
            catch (Exception e)
            {
                read.Descriptor = null;
                read.Failure = path + ": " + e.Message;
            }

            return read;
        }

        /// <summary>Main thread: a ReadDescriptor result into the cache, unless Descriptor got there first. A failure is cached as Descriptor caches it.</summary>
        public void AdoptDescriptor(string path, DescriptorRead read)
        {
            if (string.IsNullOrEmpty(path) || read == null || _descriptors.ContainsKey(path))
            {
                return;
            }

            _descriptors[path] = read.Descriptor;
        }

        /// <summary>A prefab descriptor by archive path, read once.</summary>
        public EtsPrefabDescriptor Descriptor(string path, out string failure)
        {
            failure = null;

            if (string.IsNullOrEmpty(path))
            {
                failure = "no descriptor path";
                return null;
            }

            if (_descriptors.TryGetValue(path, out EtsPrefabDescriptor cached))
            {
                return cached;
            }

            byte[] bytes = _files.Read(path);

            if (bytes == null)
            {
                failure = "descriptor not found: " + path;
                _descriptors[path] = null;
                return null;
            }

            try
            {
                EtsPrefabDescriptor ppd = EtsPrefabDescriptor.Parse(bytes);
                _descriptors[path] = ppd;
                return ppd;
            }
            catch (Exception e)
            {
                failure = path + ": " + e.Message;
                _descriptors[path] = null;
                return null;
            }
        }
    }
}
