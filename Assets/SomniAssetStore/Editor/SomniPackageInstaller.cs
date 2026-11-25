// SomniAssetStore - installs a downloaded .somnipkg into the current world.
// The heart of the system. Full algorithm: docs/importer-and-namespaces.md.
//   1. extract manifest + files from the archive, verify per-file shas
//   2. copy into Assets/SomniPackages/<id>/ preserving .meta files (GUIDs!)
//   3. rebase the package's flat source namespace -> SomniumSpace.Worlds.<root>.Pkg_<name>
//   4. patch asmdef (name/rootNamespace) and dependency namespace references
//   5. record the install in Assets/SomniPackages/installed.json
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SomniAssetStore.Shared;
using UnityEditor;
using UnityEngine;

namespace SomniAssetStore.Importer
{
    [Serializable]
    public class InstalledPackage
    {
        public string id;
        public string version;
        public string bakedNamespace;
        public string folder;             // Assets/SomniPackages/<id>
        public string sourceNamespace;
        public string installedAt;
    }

    [Serializable]
    public class InstalledLock
    {
        public List<InstalledPackage> packages = new List<InstalledPackage>();
    }

    /// <summary>The user's world: its root namespace and its folder under Assets/#User/.</summary>
    [Serializable]
    public class WorldInfo
    {
        public string rootNamespace;   // e.g. SomniumSpace.Worlds.Bentlybro.AssetTesting
        public string folder;          // e.g. Assets/#User/Bentlybro.AssetTesting
        public string asmdefGuid;      // GUID of the world asmdef (survives moves/renames)
    }

    public static partial class SomniPackageInstaller
    {
        /// <summary>World packages install INSIDE the world folder so the Somnium SDK uploads them with it.</summary>
        public const string PackagesSubfolder = "SomniPackages";

        /// <summary>
        /// Hard cap on total ACTUAL decompressed bytes for one install (zip-bomb defence). Not a
        /// const so the test harness can lower it to exercise the streaming guard without a
        /// multi-hundred-MB fixture; production always uses the 256 MB default.
        /// </summary>
        public static long MaxUncompressedBytes = 256L * 1024 * 1024;

        private static Exception TooLarge() =>
            new Exception("package decompresses too large (over " + (MaxUncompressedBytes / (1024 * 1024)) +
                          " MB); refusing to install.");

        /// <summary>
        /// Standalone (non-world) packages — avatar tools, editor tools, shaders, audio — install
        /// here. They are not part of a world scripting assembly, so they are copied verbatim (no
        /// namespace baking) and never need a world to be detected.
        /// </summary>
        public const string StandaloneRoot = "Assets/SomniAssetStore/Imported";

        public static string InstallRootFor(WorldInfo world) => world.folder + "/" + PackagesSubfolder;
        private static string LockPathFor(WorldInfo world) => InstallRootFor(world) + "/installed.json";
        private static string StandaloneLockPath => StandaloneRoot + "/installed.json";

        /// <summary>Only world assets/scripts go into (and are baked for) the world scripting assembly.
        /// Everything else installs standalone and keeps its original namespace.</summary>
        public static bool CategoryNeedsWorld(string category) =>
            category == "world-asset" || category == "world-script";

        // ---------------------------------------------------------------- public

        public static InstalledLock ReadLock(WorldInfo world) => ReadLockAt(LockPathFor(world));

        /// <summary>Read the standalone (non-world) install lock.</summary>
        public static InstalledLock ReadStandaloneLock() => ReadLockAt(StandaloneLockPath);

        private static InstalledLock ReadLockAt(string lockPath)
        {
            if (!File.Exists(lockPath)) return new InstalledLock();
            try
            {
                var parsed = JsonUtility.FromJson<InstalledLock>(File.ReadAllText(lockPath));
                if (parsed != null) return parsed;
            }
            catch { /* fall through to the warning below */ }
            // Surface corruption instead of silently pretending nothing is installed, which would
            // otherwise make installed packages invisible and produce a misleading "already exists".
            Debug.LogWarning("[SomniAssetStore] Could not read install manifest at " + lockPath +
                             "; treating as empty. If packages are installed there, fix or delete this file.");
            return new InstalledLock();
        }

        public static string BakedNamespaceFor(string worldRoot, string packageId)
        {
            // Use the FULL package id (globally unique on the store), not just the short name, so two
            // packages that happen to share a short name (bently.gun and shodah.gun) never bake to the
            // same namespace and collide in the one world scripting assembly. Namespaces have no length
            // limit in Somnium (only the world assembly root does), so the longer name is fine.
            // bently.coolgun -> SomniumSpace.Worlds.<world>.Pkg_bently_coolgun
            return worldRoot + ".Pkg_" + packageId.Replace('.', '_');
        }

        /// <summary>
        /// Install (or, when replaceExisting, update) an archive. World packages bake into the
        /// detected world's scripting assembly; everything else installs standalone (verbatim, no
        /// world required). The package's category decides which, so pass the world when you have
        /// one and it is only used for world packages.
        /// </summary>
        public static InstalledPackage Install(byte[] archiveBytes, WorldInfo world, bool replaceExisting = false,
            string categoryOverride = null)
        {
            // ---- read archive ------------------------------------------------
            SomniManifest manifest;
            var fileData = new Dictionary<string, byte[]>(); // rel path -> bytes
            using (var ms = new MemoryStream(archiveBytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var mEntry = zip.GetEntry("manifest.json");
                if (mEntry == null) throw new Exception("manifest.json missing from archive");
                using (var r = new StreamReader(mEntry.Open(), Encoding.UTF8))
                    manifest = JsonUtility.FromJson<SomniManifest>(r.ReadToEnd());

                // ---- validate untrusted manifest BEFORE using any of it ----------
                if (manifest == null || !IsSafePackageId(manifest.id))
                    throw new Exception("package has an invalid id");
                if (string.IsNullOrEmpty(manifest.sourceNamespace) || !NamespaceRe2.IsMatch(manifest.sourceNamespace))
                    throw new Exception("package has an invalid source namespace");
                if (manifest.files == null || manifest.files.Count == 0 || manifest.files.Count > 5000)
                    throw new Exception("package has an invalid file list");
                foreach (var f in manifest.files)
                    if (!IsSafeRelPath(f.path))
                        throw new Exception("package contains an unsafe file path: " + f.path);

                // Reject paths that differ only in case: on a case-insensitive filesystem they would
                // silently overwrite each other during install (data loss).
                var caseSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in manifest.files)
                    if (!caseSet.Add(f.path))
                        throw new Exception("package has files whose paths differ only in case (" + f.path +
                            "); this is unsafe on case-insensitive filesystems. Nothing was installed.");

                // Every non-.meta file must ship its sibling .meta so its GUID is stable; otherwise
                // Unity would mint a fresh random GUID and the package's own prefab/scene references
                // would break. Mirrors the server's publish-time check for tampered/hand-built archives.
                var declaredPaths = new HashSet<string>(manifest.files.Select(ff => ff.path), StringComparer.Ordinal);
                foreach (var f in manifest.files)
                    if (!f.path.EndsWith(".meta") && !declaredPaths.Contains(f.path + ".meta"))
                        throw new Exception("package is missing the .meta (GUID) for " + f.path +
                            "; refusing to install so its references cannot silently break.");

                // Cap total decompressed size (zip-bomb defence: a small archive can inflate hugely).
                // entry.Length is the CENTRAL-DIRECTORY declared size and can lie, so it is only a
                // fast pre-reject; the real guard is counting ACTUAL bytes as we decompress and
                // aborting mid-stream the instant the running total crosses the cap.
                long remaining = MaxUncompressedBytes;
                foreach (var f in manifest.files)
                {
                    var entry = zip.GetEntry("files/" + f.path.Replace('\\', '/'));
                    if (entry == null) throw new Exception("declared file missing in archive: " + f.path);
                    if (entry.Length > remaining) throw TooLarge();
                    using (var es = entry.Open())
                    using (var buf = new MemoryStream())
                    {
                        var chunk = new byte[64 * 1024];
                        int n;
                        while ((n = es.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            remaining -= n;
                            if (remaining < 0) throw TooLarge(); // actual bytes exceeded the cap
                            buf.Write(chunk, 0, n);
                        }
                        fileData[f.path] = buf.ToArray();
                    }
                }
            }

            // ---- verify shas ---------------------------------------------------
            using (var sha = SHA256.Create())
            {
                foreach (var f in manifest.files)
                {
                    string actual = Publisher.SomniPackageBuilder.Hex(sha.ComputeHash(fileData[f.path]));
                    if (!string.Equals(actual, f.sha256, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("sha mismatch for " + f.path);
                }
            }

            // ---- decide install mode from the package's category -----------------
            // The server listing category (categoryOverride) is authoritative: it can be edited on
            // the website after publish, whereas manifest.category is a frozen snapshot from build
            // time (older packages have a stale "other"). Fall back to the manifest only if no
            // override was passed (e.g. a raw archive with no listing context).
            string effectiveCategory = !string.IsNullOrEmpty(categoryOverride) ? categoryOverride : manifest.category;
            bool needsWorld = CategoryNeedsWorld(effectiveCategory);

            // An update must land where the package ALREADY lives, whatever the listing's category
            // says today: a world install never silently migrates to standalone (or back) just
            // because the category was edited on the website after the original install. Otherwise
            // an update would leave the old copy in place and add a duplicate elsewhere.
            if (replaceExisting)
            {
                bool inWorld = world != null &&
                    ReadLockAt(LockPathFor(world)).packages.Any(p => p.id == manifest.id);
                bool inStandalone = ReadLockAt(StandaloneLockPath).packages.Any(p => p.id == manifest.id);
                if (inWorld) needsWorld = true;
                else if (inStandalone) needsWorld = false;
            }

            if (needsWorld)
            {
                if (world == null)
                    throw new Exception("This is a world package, but no world was detected in this project. " +
                                        "Open your world project, or drag your world's .asmdef into the World slot " +
                                        "in the store window.");
                if (!WorldRootValid(world))
                    throw new Exception("Your world asmdef's Root Namespace is '" +
                                        (string.IsNullOrEmpty(world.rootNamespace) ? "(empty)" : world.rootNamespace) +
                                        "', but Somnium requires it to start with 'SomniumSpace.Worlds.'. Set the " +
                                        "Root Namespace on that asmdef to '" + SuggestWorldRoot(world) + "' " +
                                        "(select the .asmdef, Inspector > Root Namespace), then reinstall - or use the " +
                                        "'Fix world namespace' button in the store window to do it for you.");
                if (string.IsNullOrEmpty(world.folder) || !Directory.Exists(world.folder))
                    throw new Exception("World folder not found: '" + world.folder + "'.");
            }

            string installRoot = needsWorld ? InstallRootFor(world) : StandaloneRoot;
            string lockPath = needsWorld ? LockPathFor(world) : StandaloneLockPath;

            // ---- collision checks ---------------------------------------------
            var locked = ReadLockAt(lockPath);
            var existing = locked.packages.FirstOrDefault(p => p.id == manifest.id);
            string destFolder = installRoot + "/" + manifest.id;

            // Pre-flight (BEFORE any destructive step, incl. the update delete below): if we won't be
            // able to record the install because installed.json is read-only (locked by version
            // control), refuse now so an update never deletes the old copy and then fails.
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReadOnly) != 0)
                throw new Exception("Cannot record the install: " + lockPath + " is read-only (locked by " +
                    "version control?). Unlock it and retry. Nothing was installed.");

            // On UPDATE, the consumer's scenes/prefabs reference the installed scripts by the GUIDs
            // they had at v1. Capture those (per relative path) BEFORE deleting the old copy so we
            // can pin them onto the new files, otherwise a v2 archive shipping different .meta GUIDs
            // would silently break every reference in the user's world.
            var priorGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // rel(no .meta) -> guid
            if (replaceExisting)
            {
                if (Directory.Exists(destFolder))
                {
                    foreach (string meta in Directory.GetFiles(destFolder, "*.meta", SearchOption.AllDirectories))
                    {
                        var gm = MetaGuidRe.Match(File.ReadAllText(meta));
                        if (!gm.Success) continue;
                        string rel = meta.Replace('\\', '/').Substring(destFolder.Length + 1);
                        rel = rel.Substring(0, rel.Length - ".meta".Length); // strip .meta
                        priorGuids[rel] = gm.Groups[1].Value;
                    }
                    AssetDatabase.DeleteAsset(destFolder);
                }
                if (existing != null) locked.packages.Remove(existing);
            }
            else
            {
                if (existing != null)
                    throw new Exception(manifest.id + " is already installed (use Update to change versions).");
                if (Directory.Exists(destFolder))
                    throw new Exception(destFolder + " already exists on disk. Remove it first.");
            }

            // Standalone installs keep asmdefs verbatim, so an identical assembly name already in
            // the project (e.g. the author's own source copy) would hard-break compilation with
            // "Assembly with name X already exists". Refuse cleanly instead of importing the clash.
            if (!needsWorld)
            {
                foreach (var f in manifest.files)
                {
                    if (!f.path.EndsWith(".asmdef")) continue;
                    string asmName = ReadJsonString(Encoding.UTF8.GetString(fileData[f.path]), "name");
                    if (string.IsNullOrEmpty(asmName)) continue;
                    string existingPath = UnityEditor.Compilation.CompilationPipeline
                        .GetAssemblyDefinitionFilePathFromAssemblyName(asmName);
                    if (!string.IsNullOrEmpty(existingPath) &&
                        !existingPath.Replace('\\', '/').StartsWith(destFolder + "/", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("This package contains assembly '" + asmName + "', which already exists " +
                            "in your project at " + existingPath + " (you likely have this package's source, or it " +
                            "is already installed elsewhere). Remove one copy first; nothing was installed.");
                }
            }

            // Standalone packages keep their original namespace (no world assembly to bake into);
            // baked == source makes every rebase below a safe no-op.
            string baked = needsWorld ? BakedNamespaceFor(world.rootNamespace, manifest.id) : manifest.sourceNamespace;

            // If any incoming .meta GUID already exists elsewhere in this project (e.g. the author
            // reinstalling their own package), remap it to a fresh GUID and rewrite every reference
            // inside the package, so we never hijack an existing asset's identity. Applies to both modes.
            var guidRemap = BuildGuidRemap(manifest, fileData);
            if (guidRemap.Count > 0)
                Debug.LogWarning("[SomniAssetStore] Remapped " + guidRemap.Count +
                    " colliding GUID(s) while installing " + manifest.id + ".");

            // GUID continuity across update: for any incoming file whose relative path already
            // existed in the prior install, force its GUID back to the one that was on disk, so the
            // consumer's existing scene/prefab references keep resolving. Overrides any collision
            // remap for those files (the old GUID is the one references point at).
            if (priorGuids.Count > 0)
            {
                foreach (var f in manifest.files)
                {
                    if (!f.path.EndsWith(".meta")) continue;
                    string assetRel = f.path.Substring(0, f.path.Length - ".meta".Length);
                    if (!priorGuids.TryGetValue(assetRel, out string oldGuid)) continue;
                    var gm = MetaGuidRe.Match(Encoding.UTF8.GetString(fileData[f.path]));
                    if (!gm.Success) continue;
                    string incoming = gm.Groups[1].Value;
                    if (!string.Equals(incoming, oldGuid, StringComparison.OrdinalIgnoreCase))
                        guidRemap[incoming] = oldGuid; // pin to the reference-stable GUID
                }
            }

            // Namespace baking only happens for world packages. Standalone packages leave these empty.
            var depMap = new Dictionary<string, string>();
            var asmdefTargets = new Dictionary<string, string[]>();   // path -> [bakedName, bakedRootNs]
            var asmdefNameRemap = new Dictionary<string, string>();
            var csNsMap = new Dictionary<string, string>();
            var skipFiles = new HashSet<string>();                    // stripped on world installs
            var generatedFiles = new Dictionary<string, byte[]>();    // auto-generated editor asmdefs (+metas)
            LastStrippedAsmdefRefs = new List<SuggestedRef>();

            if (needsWorld)
            {
                // dependency namespace map: source namespace -> baked namespace (for already-installed deps)
                foreach (var dep in manifest.dependencies ?? new List<ManifestDependency>())
                {
                    var installedDep = locked.packages.FirstOrDefault(p => p.id == dep.id);
                    if (installedDep == null)
                        throw new Exception("Dependency not installed: " + dep.id + " " + dep.version +
                                            " (install it first; auto-resolution comes later).");
                    depMap[installedDep.sourceNamespace] = installedDep.bakedNamespace;
                }

                // ---- plan asmdef handling (supports multi-assembly packages) ---------
                // Somnium ships EXACTLY ONE scripting assembly with a world: the world's own. A
                // runtime asmdef inside a world install would put the package's scripts into a
                // separate assembly that is silently never loaded in-game ("referenced script
                // missing" on every prefab, invisible behaviour). So:
                //   - runtime asmdefs are STRIPPED: their scripts compile into the world assembly,
                //     and everything that referenced them by name or GUID (editor asmdefs,
                //     serialized "asm:" fields in prefabs/scenes) is repointed at the world assembly.
                //     Their own references are collected for the missing-world-reference offer.
                //   - editor-only asmdefs (includePlatforms: [Editor]) are KEPT, renamed under a
                //     short neutral root: they never ship with the world and must not leak
                //     UnityEditor references into the world assembly.
                // Full id in the neutral base, so two same-short-name packages don't collide.
                string worldAsmdefPath = WorldAsmdefPath(world);
                string worldAsmName = worldAsmdefPath != null
                    ? ReadJsonString(File.ReadAllText(worldAsmdefPath), "name") : null;

                // identities of the asmdefs shipped in THIS package, so intra-package references
                // are never mistaken for external dependencies worth offering to the world asmdef
                var pkgAsmIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in manifest.files)
                {
                    if (!f.path.EndsWith(".asmdef")) continue;
                    string n = ReadJsonString(Encoding.UTF8.GetString(fileData[f.path]), "name");
                    if (!string.IsNullOrEmpty(n)) pkgAsmIds.Add(n);
                    if (fileData.TryGetValue(f.path + ".meta", out var metaBytes))
                    {
                        var gm = MetaGuidRe.Match(Encoding.UTF8.GetString(metaBytes));
                        if (gm.Success) pkgAsmIds.Add(gm.Groups[1].Value.ToLowerInvariant());
                    }
                }

                string editorNeutralBase = "SomniPkgEditor." + manifest.id.Replace('.', '_');
                var editorNsRemap = new Dictionary<string, string>();  // editor rootNamespace -> neutral
                var usedAsmNames = new HashSet<string>();
                foreach (var f in manifest.files)
                {
                    if (!f.path.EndsWith(".asmdef")) continue;
                    string json = Encoding.UTF8.GetString(fileData[f.path]);
                    string origName = ReadJsonString(json, "name");
                    string origRootNs = ReadJsonString(json, "rootNamespace");
                    // Broadened detection: includePlatforms==[Editor], defineConstraints UNITY_EDITOR,
                    // or excludePlatforms covering every shipping platform all mean editor-only. An
                    // asmdef that includes Editor AND a runtime platform is treated as runtime (stripped).
                    bool editorOnly = IsEditorOnlyAsmdef(json);

                    if (!editorOnly)
                    {
                        // ---- runtime asmdef: strip it (see note above) -------------------
                        if (string.IsNullOrEmpty(worldAsmName))
                            throw new Exception("This package ships its own assembly ('" +
                                (origName ?? f.path) + "') but your world's .asmdef could not be " +
                                "found, so its scripts cannot be merged into the world assembly. " +
                                "Nothing was installed.");
                        // Some asmdef settings cannot survive being merged into the world assembly
                        // (DLL refs, unsafe code, define constraints). Refuse rather than silently
                        // drop them and produce a world that fails to compile.
                        string blocker = AsmdefStripBlocker(json);
                        if (blocker != null)
                            throw new Exception("This package's assembly '" + (origName ?? f.path) +
                                "' cannot be installed into a world because " + blocker +
                                ". Ask the author to ship it as an editor-only assembly or without that requirement. Nothing was installed.");
                        skipFiles.Add(f.path);
                        skipFiles.Add(f.path + ".meta");
                        // Name references (editor asmdef "references", prefab/scene "asm:" fields,
                        // UnityEvent ", Assembly" suffixes) now belong to the world assembly.
                        if (!string.IsNullOrEmpty(origName)) asmdefNameRemap[origName] = worldAsmName;
                        // GUID-style references to the stripped asmdef repoint too (both the
                        // original GUID and, if it collided, the remapped one the text will carry).
                        if (fileData.TryGetValue(f.path + ".meta", out var mb))
                        {
                            var gm = MetaGuidRe.Match(Encoding.UTF8.GetString(mb));
                            if (gm.Success)
                            {
                                string g = gm.Groups[1].Value;
                                asmdefNameRemap["GUID:" + g] = worldAsmName;
                                if (guidRemap.TryGetValue(g, out var g2))
                                    asmdefNameRemap["GUID:" + g2] = worldAsmName;
                            }
                        }
                        // Its references feed the consent-gated "add missing references" offer
                        // (external ones only; intra-package and world refs are meaningless there).
                        var rm = Regex.Match(json, "\"references\"\\s*:\\s*\\[([^\\]]*)\\]");
                        if (rm.Success)
                            foreach (Match e in Regex.Matches(rm.Groups[1].Value, "\"([^\"]+)\""))
                            {
                                string v = e.Groups[1].Value, rGuid = null, rName = null;
                                if (v.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                                {
                                    rGuid = v.Substring(5).ToLowerInvariant();
                                    string p = AssetDatabase.GUIDToAssetPath(rGuid);
                                    if (!string.IsNullOrEmpty(p)) rName = Path.GetFileNameWithoutExtension(p);
                                }
                                else
                                {
                                    rName = v;
                                    string p = UnityEditor.Compilation.CompilationPipeline
                                        .GetAssemblyDefinitionFilePathFromAssemblyName(v);
                                    if (!string.IsNullOrEmpty(p)) rGuid = AssetDatabase.AssetPathToGUID(p);
                                }
                                if (string.IsNullOrEmpty(rName) || string.IsNullOrEmpty(rGuid)) continue;
                                if (pkgAsmIds.Contains(rName) || pkgAsmIds.Contains(rGuid)) continue;
                                if (string.Equals(rName, worldAsmName, StringComparison.OrdinalIgnoreCase)) continue;
                                LastStrippedAsmdefRefs.Add(new SuggestedRef { name = rName, guid = rGuid });
                            }
                        continue;
                    }

                    // ---- editor-only asmdef: keep, renamed under the neutral root --------
                    string suffix = !string.IsNullOrEmpty(origRootNs) &&
                                    origRootNs.StartsWith(manifest.sourceNamespace + ".")
                        ? origRootNs.Substring(manifest.sourceNamespace.Length + 1)
                        : null;
                    string bakedRootNs = suffix != null ? editorNeutralBase + "." + suffix : editorNeutralBase;
                    string bakedName = bakedRootNs;
                    if (!string.IsNullOrEmpty(origRootNs)) editorNsRemap[origRootNs] = bakedRootNs;
                    if (usedAsmNames.Contains(bakedName)) // guard against collisions (asmdefs without rootNamespace)
                    {
                        string sfx = !string.IsNullOrEmpty(origName) && origName.Contains(".")
                            ? origName.Substring(origName.LastIndexOf('.') + 1) : "Asm" + usedAsmNames.Count;
                        bakedName = editorNeutralBase + "." + sfx;
                    }
                    usedAsmNames.Add(bakedName);
                    asmdefTargets[f.path] = new[] { bakedName, bakedRootNs };
                    if (!string.IsNullOrEmpty(origName)) asmdefNameRemap[origName] = bakedName;
                }

                // .cs namespace map: editor namespaces -> neutral, plus installed-dep namespaces.
                foreach (var kv in depMap) csNsMap[kv.Key] = kv.Value;
                foreach (var kv in editorNsRemap) csNsMap[kv.Key] = kv.Value;

                // ---- keep editor .cs out of the world assembly (CRI1 + backstop) --------
                // Directories governed by a KEPT editor-only asmdef are already safe. Any editor
                // .cs NOT under one would compile into the world runtime assembly. Auto-wrap those
                // that live in an "Editor" folder in a generated editor-only asmdef; refuse the
                // rest (we cannot quarantine editor code sitting among runtime code).
                var keptEditorDirs = asmdefTargets.Keys.Select(ParentDir).ToList();
                string worldRefGuid = worldAsmdefPath != null
                    ? AssetDatabase.AssetPathToGUID(worldAsmdefPath.Replace('\\', '/')) : null;
                var wrapDirs = new HashSet<string>();     // Editor folders that need a generated asmdef
                var unquarantinable = new List<string>(); // editor .cs we cannot safely wrap -> refuse

                foreach (var f in manifest.files)
                {
                    if (!f.path.EndsWith(".cs") || skipFiles.Contains(f.path)) continue;
                    if (keptEditorDirs.Any(d => PathUnder(f.path, d))) continue; // already in an editor asmdef
                    if (!CsUsesEditorApi(Encoding.UTF8.GetString(fileData[f.path]))) continue; // runtime code: fine
                    string editorRoot = EditorFolderRoot(f.path);
                    if (editorRoot != null) wrapDirs.Add(editorRoot);
                    else unquarantinable.Add(f.path);
                }

                if (unquarantinable.Count > 0)
                    throw new Exception("This package has editor-only scripts that are not inside an " +
                        "'Editor' folder or an editor asmdef, so they would compile into your world's " +
                        "runtime assembly and break its upload:\n  " + string.Join("\n  ", unquarantinable.Take(10)) +
                        "\nAsk the author to move them into an Editor folder or ship an editor asmdef. Nothing was installed.");

                foreach (string dir in wrapDirs)
                {
                    // a generated editor-only asmdef, uniquely named per world + package + folder
                    string token = (dir.Length == 0 ? "root" : dir.Replace('/', '_')).Replace(" ", "_");
                    string genName = "SomniPkgEditor." + manifest.id.Replace('.', '_') + "_" + token;
                    string refLine = string.IsNullOrEmpty(worldRefGuid) ? "" : "\n        \"GUID:" + worldRefGuid + "\"\n    ";
                    string genJson =
                        "{\n    \"name\": \"" + genName + "\",\n" +
                        "    \"references\": [" + refLine + "],\n" +
                        "    \"includePlatforms\": [\n        \"Editor\"\n    ],\n" +
                        "    \"autoReferenced\": true\n}\n";
                    string genPath = (dir.Length == 0 ? "" : dir + "/") + genName + ".asmdef";
                    generatedFiles[genPath] = Encoding.UTF8.GetBytes(genJson);
                    generatedFiles[genPath + ".meta"] =
                        Encoding.UTF8.GetBytes("fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") +
                                               "\nAssemblyDefinitionImporter:\n  externalObjects: {}\n  userData: \n" +
                                               "  assetBundleName: \n  assetBundleVariant: \n");
                }
            }

            // Everything below writes into the world folder. If ANY step fails, roll the partial
            // folder back so we never leave half-baked code compiling into the world assembly.
            try
            {
                // ---- write files, rewriting namespaces + GUIDs in text assets (encoding-safe) ----
                foreach (var f in manifest.files)
                {
                    if (skipFiles.Contains(f.path)) continue; // stripped runtime asmdefs (world installs)
                    string dest = ContainedPath(destFolder, f.path); // throws if it escapes destFolder
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));

                    byte[] data = fileData[f.path];
                    if (IsTextAsset(f.path))
                    {
                        string fp = f.path;
                        data = RewriteTextPreservingEncoding(data, text =>
                        {
                            foreach (var kv in guidRemap) text = text.Replace(kv.Key, kv.Value);
                            // Namespace/assembly rebasing is world-only; standalone packages stay verbatim.
                            if (needsWorld)
                            {
                                if (fp.EndsWith(".cs"))
                                    text = RebaseNamespaces(text, manifest.sourceNamespace, baked, csNsMap);
                                else if (fp.EndsWith(".asmdef"))
                                {
                                    var t = asmdefTargets[fp];
                                    text = PatchAsmdef(text, t[0], t[1], asmdefNameRemap, depMap);
                                }
                                else if (IsSerializedAsset(fp))
                                    text = RebaseSerializedAsset(text, manifest.sourceNamespace, baked, csNsMap, asmdefNameRemap);
                            }
                            return text;
                        });
                    }
                    // binary assets (textures, models, etc.): byte-for-byte verbatim.

                    File.WriteAllBytes(dest, data);
                }

                // ---- write auto-generated editor asmdefs (CRI1 quarantine) -------------
                foreach (var g in generatedFiles)
                {
                    string dest = ContainedPath(destFolder, g.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.WriteAllBytes(dest, g.Value);
                }

                // ---- record install -----------------------------------------------
                var record = new InstalledPackage
                {
                    id = manifest.id,
                    version = manifest.version,
                    bakedNamespace = baked,
                    folder = destFolder,
                    sourceNamespace = manifest.sourceNamespace,
                    installedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                };
                locked.packages.Add(record);
                Directory.CreateDirectory(installRoot);
                File.WriteAllText(lockPath, JsonUtility.ToJson(locked, true));

                AssetDatabase.Refresh();
                return record;
            }
            catch
            {
                // Roll back the partial folder so a failed install never leaves untracked code
                // baked into the world assembly (files written but no lock entry).
                try
                {
                    if (Directory.Exists(destFolder))
                    {
                        AssetDatabase.DeleteAsset(destFolder);
                        if (Directory.Exists(destFolder)) Directory.Delete(destFolder, true);
                    }
                }
                catch { /* best-effort rollback */ }
                AssetDatabase.Refresh();
                throw;
            }
        }

        /// <summary>Replace an installed package with a (usually newer) archive, preserving its folder + GUIDs.</summary>
        public static InstalledPackage Update(byte[] archiveBytes, WorldInfo world, string categoryOverride = null)
        {
            return Install(archiveBytes, world, replaceExisting: true, categoryOverride: categoryOverride);
        }

        /// <summary>
        /// Semver-ish compare. Returns &gt;0 if a is newer than b, 0 if equal, &lt;0 if older.
        /// Numeric per dotted segment, missing segments treated as 0.
        /// </summary>
        public static int CompareVersions(string a, string b)
        {
            string[] pa = (a ?? "0").Split('.');
            string[] pb = (b ?? "0").Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int va = i < pa.Length && int.TryParse(pa[i], out int x) ? x : 0;
                int vb = i < pb.Length && int.TryParse(pb[i], out int y) ? y : 0;
                if (va != vb) return va - vb;
            }
            return 0;
        }
    }
}
#endif
