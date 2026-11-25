// SomniAssetStore - builds a .somnipkg from a SomniPackageDescriptor.
// Collects every asset under the descriptor's folder (with .meta files, GUIDs preserved),
// validates the flat-namespace rule, writes manifest.json, zips.
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

namespace SomniAssetStore.Publisher
{
    public static class SomniPackageBuilder
    {
        // Line-anchored so the word "namespace" inside comments/strings never matches.
        private static readonly Regex NamespaceRe =
            new Regex(@"^\s*namespace\s+([A-Za-z_][\w\.]*)", RegexOptions.Compiled | RegexOptions.Multiline);

        public class BuildResult
        {
            public byte[] ArchiveBytes;
            public SomniManifest Manifest;
            public List<string> Warnings = new List<string>();
        }

        /// <summary>An asset the package's content references that lives OUTSIDE the package folder,
        /// so it would not ship and would arrive broken in the consumer's world.</summary>
        public class ExternalDep
        {
            public string asset;        // project-relative path of the missing external asset
            public string referencedBy; // project-relative path of the in-package asset that needs it
        }

        /// <summary>Validate + build a package from a project folder + its config. Throws on hard errors.</summary>
        public static BuildResult Build(string rootFolder, SomniPackageConfig cfg)
        {
            if (string.IsNullOrEmpty(rootFolder) || !Directory.Exists(rootFolder))
                throw new Exception("Pick a package folder first.");
            rootFolder = rootFolder.Replace('\\', '/').TrimEnd('/');

            // --- validate fields ------------------------------------------
            var errors = new List<string>();
            if (!Regex.IsMatch(cfg.id ?? "", @"^[a-z0-9]+(\.[a-z0-9_]+)+$"))
                errors.Add("Package ID must be lowercase dot-namespaced, e.g. bently.coolgun (underscores allowed after the first dot)");
            if (string.IsNullOrWhiteSpace(cfg.name)) errors.Add("Title is required");
            if (!Regex.IsMatch(cfg.version ?? "", @"^\d+\.\d+\.\d+$"))
                errors.Add("Version must be semver x.y.z");
            // author is set from the authenticated upload token, not entered here
            if (string.IsNullOrWhiteSpace(cfg.sourceNamespace) || cfg.sourceNamespace.EndsWith("."))
                errors.Add("Script namespace must be a complete namespace, e.g. SomniPkg.CoolGun");
            if (errors.Count > 0) throw new Exception("Please fix:\n- " + string.Join("\n- ", errors));

            // the package's own preview image (if it lives in the folder) is store metadata, not shipped
            string shotRel = null;
            if (!string.IsNullOrEmpty(cfg.screenshotPath))
            {
                string sp = cfg.screenshotPath.Replace('\\', '/');
                if (sp.StartsWith(rootFolder + "/")) shotRel = sp.Substring(rootFolder.Length + 1);
            }

            // --- collect files (everything in the folder except metadata) --------
            var result = new BuildResult();
            var files = new List<(string rel, string abs)>();

            foreach (string abs in Directory.GetFiles(rootFolder, "*", SearchOption.AllDirectories))
            {
                string norm = abs.Replace('\\', '/');
                string rel = norm.Substring(rootFolder.Length + 1);

                if (rel == SomniPackageConfig.FileName) continue;            // our metadata dotfile
                if (shotRel != null && (rel == shotRel || rel == shotRel + ".meta")) continue; // preview image
                if (rel.Contains("#Scripts Backup/")) continue;

                files.Add((rel, norm));
            }
            if (files.Count == 0) throw new Exception("No files found under " + rootFolder);

            // every non-meta file must have a .meta next to it (GUID preservation)
            var relSet = new HashSet<string>(files.Select(f => f.rel));
            foreach (var (rel, _) in files.ToList())
            {
                if (!rel.EndsWith(".meta") && !relSet.Contains(rel + ".meta"))
                    throw new Exception("Missing .meta for " + rel + " (GUIDs must ship with the package).");
            }

            // --- namespace check: every script must share ONE root namespace -------
            // Sub-namespaces under that root are allowed (e.g. a Runtime namespace plus a
            // <root>.Editor one), so Runtime + Editor assembly splits work. The importer
            // rebases the whole root prefix on install, carrying sub-namespaces along.
            var scriptNamespaces = new List<string>();
            foreach (var (rel, abs) in files)
            {
                if (!rel.EndsWith(".cs")) continue;
                var m = NamespaceRe.Match(File.ReadAllText(abs));
                if (!m.Success)
                {
                    result.Warnings.Add(rel + ": no namespace declaration found (global-namespace scripts are not rebased).");
                    continue;
                }
                scriptNamespaces.Add(m.Groups[1].Value);
            }
            if (scriptNamespaces.Count > 0)
            {
                string root = CommonNamespaceRoot(scriptNamespaces);
                if (string.IsNullOrEmpty(root))
                    throw new Exception(
                        "Your scripts do not share a common root namespace. Found: " +
                        string.Join(", ", scriptNamespaces.Distinct()) +
                        ". Put them all under one root (nested sub-namespaces like <root>.Editor are fine).");
                if (root != cfg.sourceNamespace)
                {
                    result.Warnings.Add("Package namespace set to the detected root '" + root + "'.");
                    cfg.sourceNamespace = root;
                }
            }

            // --- the FINAL namespace must be one the whole pipeline accepts -------
            // The importer installs only ASCII dotted namespaces (NamespaceRe2) and the server
            // additionally bans reserved roots. Validate HERE, at build time, so an author gets a
            // clear error instead of publishing a package nobody can install. Checked after the
            // auto-detect above because that is what actually lands in the manifest (a legal C#
            // 'namespace Wörld' would otherwise slip through as the detected root).
            if (!Regex.IsMatch(cfg.sourceNamespace ?? "", @"^[A-Za-z_][A-Za-z0-9_.]*$"))
                throw new Exception("Script namespace '" + cfg.sourceNamespace + "' contains characters the " +
                    "store cannot install (only A-Z, a-z, 0-9, '_' and '.' are allowed). Rename the namespace " +
                    "in your scripts, then build again.");
            // SomniumSpace.Worlds.<Name> is the REQUIRED root for a world scripting assembly (the
            // SDK's WorldConfig forces it), so it is explicitly allowed - it is the world's own
            // namespace and rebases to the consumer's world root as a whole token, safely. Only bare
            // engine/SDK roots are dangerous: rebasing e.g. "System" or "SomniumSpace.Bridge" would
            // rewrite real engine/SDK type references in the consumer's world.
            string ns = cfg.sourceNamespace ?? "";
            if (!ns.StartsWith("SomniumSpace.Worlds.") && ns != "SomniumSpace.Worlds")
            {
                string nsRoot = ns.Split('.')[0];
                string[] reservedRoots = { "SomniumSpace", "UnityEngine", "UnityEditor", "Unity", "System", "Mono", "Microsoft", "Photon", "Fusion" };
                if (Array.IndexOf(reservedRoots, nsRoot) >= 0)
                    throw new Exception("Script namespace '" + ns + "' starts with the reserved root '" +
                        nsRoot + "', which the store rejects (rebasing it would corrupt engine/SDK type references " +
                        "in consumers' worlds). Use your own root, e.g. '" + (cfg.id ?? "author.package").Split('.')[0] +
                        ".MyThing', or a world namespace under SomniumSpace.Worlds.");
            }

            // --- build manifest ---------------------------------------------
            var manifest = new SomniManifest
            {
                id = cfg.id,
                name = cfg.name,
                version = cfg.version,
                author = new ManifestAuthor { username = cfg.authorUsername, displayName = cfg.authorDisplayName },
                description = cfg.description ?? "",
                category = string.IsNullOrEmpty(cfg.category) ? "other" : cfg.category,
                sourceNamespace = cfg.sourceNamespace,
                createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };
            foreach (var dep in cfg.dependencies ?? new List<SomniPackageConfig.Dep>())
                if (!string.IsNullOrWhiteSpace(dep.id))
                    manifest.dependencies.Add(new ManifestDependency { id = dep.id, version = dep.versionRange });

            // asmdef info if the package ships one
            var asmdefFile = files.FirstOrDefault(f => f.rel.EndsWith(".asmdef"));
            if (asmdefFile.abs != null)
            {
                manifest.asmdef.name = Path.GetFileNameWithoutExtension(asmdefFile.rel);
                // references are patched by the importer; recorded raw here
            }
            else
            {
                // No asmdef ships with the package, so on install its scripts compile into the
                // consumer's WORLD assembly — which must reference whatever these scripts use
                // (Fusion, XRI, ...). Record the references of the asmdef that compiles them HERE
                // (the author's world asmdef) so the installer can offer to add missing ones.
                manifest.suggestedReferences = CollectGoverningAsmdefRefs(files);
            }

            using (var sha = SHA256.Create())
            {
                foreach (var (rel, abs) in files.OrderBy(f => f.rel, StringComparer.Ordinal))
                {
                    byte[] data = File.ReadAllBytes(abs);
                    manifest.files.Add(new ManifestFile
                    {
                        path = rel,
                        sha256 = Hex(sha.ComputeHash(data)),
                        kind = ManifestKinds.ForExtension(rel),
                    });
                }
            }

            // --- zip ----------------------------------------------------------
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifestEntry = zip.CreateEntry("manifest.json");
                    using (var w = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
                        w.Write(JsonUtility.ToJson(manifest, true));

                    foreach (var (rel, abs) in files)
                    {
                        var entry = zip.CreateEntry("files/" + rel);
                        using (var es = entry.Open())
                        {
                            byte[] data = File.ReadAllBytes(abs);
                            es.Write(data, 0, data.Length);
                        }
                    }
                }
                result.ArchiveBytes = ms.ToArray();
            }

            result.Manifest = manifest;
            return result;
        }

        /// <summary>
        /// Find assets the package's content references by GUID that live OUTSIDE the package
        /// folder. Those would NOT ship in the archive, so the consumer gets a broken reference
        /// (the "missing particle material / sounds" class of bug). Engine, Unity Package Manager,
        /// and Somnium SDK assets are excluded: they already exist in every consumer project, so
        /// referencing them is fine and they must never be shipped.
        ///
        /// Best effort: uses the AssetDatabase dependency graph, so it only means anything when the
        /// folder is inside the current Unity project's Assets/. Returns an empty list otherwise.
        /// Read-only: this never moves, writes, or modifies anything.
        /// </summary>
        public static List<ExternalDep> FindExternalAssetDependencies(string rootFolder)
        {
            var result = new List<ExternalDep>();
            if (string.IsNullOrEmpty(rootFolder) || !Directory.Exists(rootFolder)) return result;
            rootFolder = rootFolder.Replace('\\', '/').TrimEnd('/');

            string folderRel = ToProjectRelative(rootFolder);
            if (folderRel == null) return result; // folder is outside Assets/ - can't reason about deps
            string folderPrefix = folderRel + "/";

            // Project-relative paths of every real asset in the package (skip .meta and our dotfile).
            var inPackage = new List<string>();
            foreach (string abs in Directory.GetFiles(rootFolder, "*", SearchOption.AllDirectories))
            {
                string pr = ToProjectRelative(abs);
                if (pr == null || pr.EndsWith(".meta")) continue;
                if (pr.EndsWith("/" + SomniPackageConfig.FileName)) continue;
                inPackage.Add(pr);
            }
            if (inPackage.Count == 0) return result;

            var seen = new HashSet<string>();
            foreach (string src in inPackage)
            {
                // recursive: a prefab -> material -> texture chain must all ship
                foreach (string dep in AssetDatabase.GetDependencies(src, true))
                {
                    string d = dep.Replace('\\', '/');
                    if (d == src) continue;
                    if (d.StartsWith(folderPrefix, StringComparison.Ordinal)) continue; // ships with the package
                    if (IsPresentInEveryProject(d)) continue;                            // engine / UPM / SDK
                    if (!seen.Add(d + "\n" + src)) continue;                             // dedupe identical pairs
                    result.Add(new ExternalDep { asset = d, referencedBy = src });
                }
            }
            return result;
        }

        /// <summary>True for assets that exist in every consumer project already (engine built-ins,
        /// UPM packages, and the Somnium SDK/plugins), so referencing them is safe and they must
        /// not be shipped.</summary>
        private static bool IsPresentInEveryProject(string projectRelPath)
        {
            if (string.IsNullOrEmpty(projectRelPath)) return true; // unknown -> don't false-alarm
            if (projectRelPath.StartsWith("Packages/", StringComparison.Ordinal)) return true;
            if (projectRelPath.StartsWith("Resources/unity_builtin", StringComparison.OrdinalIgnoreCase)) return true;
            if (projectRelPath.IndexOf("/SomniumSpace", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (projectRelPath.StartsWith("Assets/SomniumSpace", StringComparison.OrdinalIgnoreCase)) return true;
            if (projectRelPath.IndexOf("/Plugins/SomniumSpace", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Convert an absolute or already-relative path to a project-relative
        /// (Assets/... or Packages/...) path, or null if it is outside the project.</summary>
        private static string ToProjectRelative(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            p = p.Replace('\\', '/');
            if (p.StartsWith("Assets/", StringComparison.Ordinal) || p.StartsWith("Packages/", StringComparison.Ordinal))
                return p;
            string projRoot = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
            if (p.StartsWith(projRoot + "/", StringComparison.Ordinal)) return p.Substring(projRoot.Length + 1);
            return null;
        }

        /// <summary>
        /// The references of the asmdef that compiles this package's scripts in the author's
        /// project, as {name, guid} pairs. Best effort: returns an empty list if there are no
        /// scripts or no governing asmdef (scripts in Assembly-CSharp have implicit references).
        /// </summary>
        private static List<SuggestedRef> CollectGoverningAsmdefRefs(List<(string rel, string abs)> files)
        {
            var result = new List<SuggestedRef>();
            var firstCs = files.FirstOrDefault(f => f.rel.EndsWith(".cs"));
            if (firstCs.abs == null) return result;

            // project-relative path (Assets/...) for the CompilationPipeline lookup
            string projRoot = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
            string scriptPath = firstCs.abs.Replace('\\', '/');
            if (scriptPath.StartsWith(projRoot + "/")) scriptPath = scriptPath.Substring(projRoot.Length + 1);

            string asmdefPath = UnityEditor.Compilation.CompilationPipeline
                .GetAssemblyDefinitionFilePathFromScriptPath(scriptPath);
            if (string.IsNullOrEmpty(asmdefPath)) return result;

            string json;
            try { json = File.ReadAllText(asmdefPath); } catch { return result; }
            var m = Regex.Match(json, "\"references\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (!m.Success) return result;

            foreach (Match entry in Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\""))
            {
                string val = entry.Groups[1].Value;
                string name = null, guid = null;
                if (val.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                {
                    guid = val.Substring(5).ToLowerInvariant();
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(p)) name = Path.GetFileNameWithoutExtension(p);
                }
                else
                {
                    name = val;
                    string p = UnityEditor.Compilation.CompilationPipeline
                        .GetAssemblyDefinitionFilePathFromAssemblyName(val);
                    if (!string.IsNullOrEmpty(p)) guid = AssetDatabase.AssetPathToGUID(p);
                }
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(guid))
                    result.Add(new SuggestedRef { name = name, guid = guid });
            }
            return result;
        }

        /// <summary>
        /// The longest dot-segment prefix shared by every namespace. For
        /// {A.B.Weather, A.B.Weather.Editor} it is A.B.Weather; empty if they share nothing.
        /// </summary>
        public static string CommonNamespaceRoot(IEnumerable<string> namespaces)
        {
            string[] common = null;
            foreach (var ns in namespaces.Distinct())
            {
                var seg = ns.Split('.');
                if (common == null) { common = seg; continue; }
                int i = 0;
                while (i < common.Length && i < seg.Length && common[i] == seg[i]) i++;
                common = common.Take(i).ToArray();
                if (common.Length == 0) return "";
            }
            return common == null ? "" : string.Join(".", common);
        }

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
#endif
