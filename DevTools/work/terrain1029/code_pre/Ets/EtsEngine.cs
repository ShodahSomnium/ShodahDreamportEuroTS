using System;
using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The one place the game's data is opened from inside Unity.
    ///
    /// A world has one install, one set of archives and one set of caches, and every component
    /// in the scene - the model viewer, the map, the truck - wants the same ones. Holding them
    /// here means base.scs is opened once, a texture decoded once and a material built once, no
    /// matter how many things ask.
    ///
    /// Static rather than a scene object because it has no per-frame behaviour and nothing to
    /// serialise; the editor's domain reload clears it between play sessions, which is exactly
    /// the lifetime wanted.
    /// </summary>
    public static class EtsEngine
    {
        private static readonly Dictionary<string, EtsModelData> ModelCache = new Dictionary<string, EtsModelData>(StringComparer.Ordinal);

        public static EtsFileSystem Files { get; private set; }

        public static EtsTextureCache Textures { get; private set; }

        public static EtsMaterialCache Materials { get; private set; }

        public static string InstallRoot { get; private set; }

        /// <summary>What was mounted, one archive per line.</summary>
        public static string MountReport { get; private set; }

        public static bool IsMounted => Files != null;

        /// <summary>
        /// Finds the install and opens its archives. Cheap to call again once mounted.
        /// </summary>
        /// <param name="installHint">A folder to try first, or null to search Steam.</param>
        /// <param name="failure">Why nothing was mounted, when this returns false.</param>
        public static bool Mount(string installHint, out string failure)
        {
            failure = null;

            if (Files != null)
            {
                return true;
            }

            string root = EtsInstall.Locate(string.IsNullOrEmpty(installHint) ? null : installHint);

            if (root == null)
            {
                failure = EtsInstall.Describe(null);
                return false;
            }

            try
            {
                Files = EtsFileSystem.Mount(root, out string report);
                MountReport = report;
                InstallRoot = root;
            }
            catch (Exception e)
            {
                failure = "Could not open the game's archives: " + e.GetType().Name + ": " + e.Message;
                Files = null;
                return false;
            }

            if (Files.MountCount == 0)
            {
                failure = "No .scs archives could be opened in " + root + ".";
                Files.Dispose();
                Files = null;
                return false;
            }

            Textures = new EtsTextureCache(Files);
            Materials = new EtsMaterialCache(Files, Textures);
            return true;
        }

        public static void Unmount()
        {
            Materials?.Clear();
            Textures?.Clear();
            Files?.Dispose();
            Files = null;
            Textures = null;
            Materials = null;
            ModelCache.Clear();
        }

        /// <summary>
        /// A model by its descriptor path. The geometry file sits beside it with the other
        /// extension. Null when either half is missing.
        /// </summary>
        public static EtsModelData LoadModel(string pmdPath, out string failure)
        {
            failure = null;

            if (Files == null)
            {
                failure = "The game's archives are not mounted.";
                return null;
            }

            string key = ModelKey(pmdPath);

            if (ModelCache.TryGetValue(key, out EtsModelData cached))
            {
                return cached;
            }

            EtsModelData model = ReadModel(Files, pmdPath, out failure);

            if (model != null)
            {
                ModelCache[key] = model;
            }

            return model;
        }

        /// <summary>The key a model is cached under: its normalised path.</summary>
        public static string ModelKey(string pmdPath)
        {
            return "/" + EtsHashFs.Normalize(pmdPath);
        }

        /// <summary>Whether a model is in the cache already (LoadModel would not read it).</summary>
        public static bool IsModelLoaded(string pmdPath)
        {
            return !string.IsNullOrEmpty(pmdPath) && ModelCache.ContainsKey(ModelKey(pmdPath));
        }

        /// <summary>
        /// Main thread: the cached model, never a read - false when it is not in the cache. The map's stream
        /// (EtsMapViewer's prefetch) asks this after the worker's read has been adopted, to find the look's materials.
        /// </summary>
        public static bool TryGetModel(string pmdPath, out EtsModelData model)
        {
            model = null;
            return !string.IsNullOrEmpty(pmdPath) && ModelCache.TryGetValue(ModelKey(pmdPath), out model);
        }

        /// <summary>
        /// Reads and parses a model without touching the cache: pure, so a worker thread may run
        /// it (the traffic reads its cars' models that way, EtsTrafficModels). Null on failure.
        /// </summary>
        public static EtsModelData ReadModel(EtsFileSystem files, string pmdPath, out string failure)
        {
            failure = null;
            string key = ModelKey(pmdPath);

            string pmgPath = key.EndsWith(".pmd", StringComparison.OrdinalIgnoreCase)
                ? key.Substring(0, key.Length - 4) + ".pmg"
                : key + ".pmg";

            byte[] pmd = files.Read(key);
            byte[] pmg = files.Read(pmgPath);

            if (pmd == null)
            {
                failure = "Model descriptor not found: " + key;
                return null;
            }

            if (pmg == null)
            {
                failure = "Model geometry not found: " + pmgPath;
                return null;
            }

            try
            {
                return EtsModelData.Parse(pmd, pmg, key);
            }
            catch (Exception e)
            {
                failure = e.Message;
                return null;
            }
        }

        /// <summary>
        /// Main thread: a model read elsewhere (ReadModel) into the cache, unless one got there
        /// first - then that one is kept, so every user shares one parse and one set of meshes.
        /// </summary>
        public static EtsModelData AdoptModel(string pmdPath, EtsModelData model)
        {
            string key = ModelKey(pmdPath);

            if (ModelCache.TryGetValue(key, out EtsModelData cached))
            {
                return cached;
            }

            if (model != null)
            {
                ModelCache[key] = model;
            }

            return model;
        }
    }
}
