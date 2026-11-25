using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The game's data as one tree, the way the engine itself sees it.
    ///
    /// The engine mounts its archives in a fixed order and later mounts win: the log on this
    /// machine reads effect, base, core, def, locale, base_cfg, then the DLC archives. A path is
    /// looked up from the last mount backwards, so a DLC that replaces a file in base.scs is
    /// honoured the same way the game honours it. Folder listings are the union across mounts.
    ///
    /// Everything downstream - definitions, models, textures, the map - asks this for bytes by
    /// absolute path, with or without the leading slash the definition files use.
    /// </summary>
    public sealed class EtsFileSystem : IDisposable
    {
        private readonly List<EtsHashFs> _mounts = new List<EtsHashFs>();
        /// <summary>
        /// ReadSii's cache. Locked (Phase 2 integration): worker threads read definitions too, and a plain Dictionary
        /// written from two threads loses entries - D's checker reproduced it on the real install (probe 2055 (2): 1-2 of
        /// 30 rounds of concurrent ReadSii). Only the lookup and the store are locked, never the read and parse, so two
        /// threads asking for the same file at once may each parse it; both results are whole and either is kept.
        /// </summary>
        private readonly Dictionary<string, EtsSiiFile> _siiCache = new Dictionary<string, EtsSiiFile>(StringComparer.Ordinal);
        private readonly object _siiLock = new object();

        /// <summary>The install the archives came from.</summary>
        public string InstallRoot { get; private set; }

        /// <summary>Archive files, in mount order.</summary>
        public IEnumerable<string> Mounted
        {
            get
            {
                foreach (EtsHashFs mount in _mounts)
                {
                    yield return mount.FilePath;
                }
            }
        }

        public int MountCount => _mounts.Count;

        /// <summary>
        /// Opens every archive in an install, in the engine's own order.
        /// </summary>
        /// <param name="report">What was mounted, one line each, for the status label.</param>
        public static EtsFileSystem Mount(string installRoot, out string report)
        {
            var fs = new EtsFileSystem { InstallRoot = installRoot };
            var log = new StringBuilder();

            var order = new List<string> { "effect.scs", "base.scs", "core.scs", "def.scs", "locale.scs", "base_cfg.scs" };

            string[] extra;

            try
            {
                extra = Directory.GetFiles(installRoot, "*.scs");
            }
            catch (Exception)
            {
                extra = new string[0];
            }

            var dlcs = new List<string>();

            foreach (string file in extra)
            {
                string name = Path.GetFileName(file);

                if (!order.Contains(name))
                {
                    dlcs.Add(name);
                }
            }

            dlcs.Sort(StringComparer.OrdinalIgnoreCase);
            order.AddRange(dlcs);

            foreach (string name in order)
            {
                string path = Path.Combine(installRoot, name);

                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    EtsHashFs archive = EtsHashFs.Open(path);
                    fs._mounts.Add(archive);
                    log.AppendLine(name + ": " + archive.EntryCount + " entries");
                }
                catch (Exception e)
                {
                    log.AppendLine(name + ": NOT mounted - " + e.Message);
                }
            }

            report = log.ToString();
            return fs;
        }

        public bool Exists(string path)
        {
            string p = EtsHashFs.Normalize(path);

            for (int i = _mounts.Count - 1; i >= 0; i--)
            {
                if (_mounts[i].TryGetEntry(p, out EtsHashFs.Entry entry) && !entry.IsDirectory)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The entry for a path from the last mount that has it, and that mount. False when absent.</summary>
        public bool TryGetEntry(string path, out EtsHashFs archive, out EtsHashFs.Entry entry)
        {
            string p = EtsHashFs.Normalize(path);

            for (int i = _mounts.Count - 1; i >= 0; i--)
            {
                if (_mounts[i].TryGetEntry(p, out entry) && !entry.IsDirectory)
                {
                    archive = _mounts[i];
                    return true;
                }
            }

            archive = null;
            entry = default(EtsHashFs.Entry);
            return false;
        }

        /// <summary>The bytes of a file from the last mount that has it, or null.</summary>
        public byte[] Read(string path)
        {
            string p = EtsHashFs.Normalize(path);

            for (int i = _mounts.Count - 1; i >= 0; i--)
            {
                if (_mounts[i].TryGetEntry(p, out EtsHashFs.Entry entry) && !entry.IsDirectory)
                {
                    return _mounts[i].Read(entry);
                }
            }

            return null;
        }

        /// <summary>A file as text, byte order mark removed. Null when absent.</summary>
        public string ReadText(string path)
        {
            byte[] bytes = Read(path);

            if (bytes == null)
            {
                return null;
            }

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>The names in a folder across every mount. False when no mount has the folder.</summary>
        public bool TryList(string path, out List<string> files, out List<string> folders)
        {
            var fileSet = new HashSet<string>(StringComparer.Ordinal);
            var folderSet = new HashSet<string>(StringComparer.Ordinal);
            bool any = false;

            foreach (EtsHashFs mount in _mounts)
            {
                if (mount.TryReadDirectory(path, out List<string> f, out List<string> d))
                {
                    any = true;
                    fileSet.UnionWith(f);
                    folderSet.UnionWith(d);
                }
            }

            files = new List<string>(fileSet);
            folders = new List<string>(folderSet);
            files.Sort(StringComparer.Ordinal);
            folders.Sort(StringComparer.Ordinal);
            return any;
        }

        /// <summary>
        /// A definition file, parsed, with its includes resolved through this file system.
        /// Cached, because the same road look file is asked for by every road.
        /// </summary>
        public EtsSiiFile ReadSii(string path)
        {
            string p = "/" + EtsHashFs.Normalize(path);

            EtsSiiFile cached;

            lock (_siiLock)
            {
                if (_siiCache.TryGetValue(p, out cached))
                {
                    return cached;
                }
            }

            string text = ReadText(p);
            EtsSiiFile file = text == null ? null : EtsSii.Parse(text, p, ReadText);

            lock (_siiLock)
            {
                _siiCache[p] = file;
            }

            return file;
        }

        /// <summary>
        /// Every unit from a definition family: /def/world/road_look.sii together with every
        /// road_look.*.sii beside it, which is how the game itself lets content packs extend a
        /// table without editing the base file. Later files win on a name clash.
        /// </summary>
        public EtsSiiFile ReadDefinitionFamily(string folder, string baseName)
        {
            var merged = new EtsSiiFile();

            if (!TryList(folder, out List<string> files, out _))
            {
                return merged;
            }

            var matching = new List<string>();

            foreach (string file in files)
            {
                if (file == baseName + ".sii")
                {
                    matching.Insert(0, file);
                }
                else if (file.StartsWith(baseName + ".", StringComparison.Ordinal) && file.EndsWith(".sii", StringComparison.Ordinal))
                {
                    matching.Add(file);
                }
            }

            foreach (string file in matching)
            {
                EtsSiiFile parsed = ReadSii(EtsHashFs.Normalize(folder) + "/" + file);

                if (parsed != null)
                {
                    merged.AddAll(parsed);
                }
            }

            return merged;
        }

        public void Dispose()
        {
            foreach (EtsHashFs mount in _mounts)
            {
                mount.Dispose();
            }

            _mounts.Clear();
            lock (_siiLock)
            {
                _siiCache.Clear();
            }
        }
    }
}
