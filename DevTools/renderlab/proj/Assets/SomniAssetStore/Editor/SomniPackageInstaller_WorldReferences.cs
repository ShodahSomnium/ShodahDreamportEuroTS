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
    public static partial class SomniPackageInstaller
    {
        // ------------------------------------------- world asmdef reference help
        //
        // Packages without their own asmdef compile into the consumer's world assembly, which must
        // reference whatever the scripts use (Fusion, XRI, ...). The publisher records the author's
        // governing-asmdef references as suggestions; here we offer to add MISSING ones. Safety:
        //  - append-only: we only ever ADD entries to the world asmdef's references array
        //  - consent-gated: callers (Store window) show a dialog before calling AddWorldReferences
        //  - existing-only: we only offer assemblies already present in the consumer's project
        //  - untrusted input: names/guids from the manifest are strictly validated first

        private static readonly Regex RefGuidRe = new Regex(@"^[0-9a-f]{32}$", RegexOptions.Compiled);
        private static readonly Regex RefNameRe = new Regex(@"^[A-Za-z0-9._\-]{1,64}$", RegexOptions.Compiled);

        /// <summary>
        /// External references parsed off runtime asmdefs stripped by the LAST world install.
        /// The Store window merges these into the missing-reference offer (same consent gate as
        /// manifest.suggestedReferences); they are never applied silently.
        /// </summary>
        public static List<SuggestedRef> LastStrippedAsmdefRefs { get; private set; } = new List<SuggestedRef>();

        /// <summary>Path of the world's own asmdef inside its folder (null if not found).</summary>
        public static string WorldAsmdefPath(WorldInfo world)
        {
            if (world == null) return null;
            // Prefer the exact asmdef the user pinned / we resolved (by GUID). In a world folder
            // with more than one root asmdef, a plain scan could repoint references at the wrong
            // assembly; the pinned GUID is unambiguous.
            if (!string.IsNullOrEmpty(world.asmdefGuid))
            {
                string p = AssetDatabase.GUIDToAssetPath(world.asmdefGuid);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".asmdef") && File.Exists(p))
                    return p.Replace('\\', '/');
            }
            if (string.IsNullOrEmpty(world.folder) || !Directory.Exists(world.folder)) return null;
            foreach (string f in Directory.GetFiles(world.folder, "*.asmdef", SearchOption.TopDirectoryOnly))
                if (File.ReadAllText(f).Contains("SomniumSpace.Worlds.")) return f.Replace('\\', '/');
            return null;
        }

        public class MissingRefs
        {
            /// <summary>Missing from the world asmdef AND present in this project: safe to offer.</summary>
            public List<SuggestedRef> addable = new List<SuggestedRef>();
            /// <summary>Missing and NOT in this project: we only warn (never pull anything in).</summary>
            public List<string> unavailable = new List<string>();
        }

        /// <summary>Which suggested references the world asmdef lacks. Invalid entries are ignored.</summary>
        public static MissingRefs ComputeMissingWorldRefs(WorldInfo world, List<SuggestedRef> suggested)
        {
            var result = new MissingRefs();
            string asmdefPath = WorldAsmdefPath(world);
            if (asmdefPath == null || suggested == null) return result;

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var m = Regex.Match(File.ReadAllText(asmdefPath), "\"references\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (m.Success)
                foreach (Match e in Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\""))
                {
                    string v = e.Groups[1].Value;
                    existing.Add(v);
                    if (v.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                    {
                        existing.Add(v.Substring(5));
                        string p = AssetDatabase.GUIDToAssetPath(v.Substring(5));
                        if (!string.IsNullOrEmpty(p))
                        {
                            existing.Add(Path.GetFileNameWithoutExtension(p)); // file name (usual case)
                            // ...and the ASSEMBLY name declared inside the asmdef, which can differ
                            // from the file name; without this a name-suggested ref already present
                            // as a GUID could be offered (and added) a second time.
                            try { string an = ReadJsonString(File.ReadAllText(p), "name"); if (!string.IsNullOrEmpty(an)) existing.Add(an); }
                            catch { /* unreadable asmdef: file-name match still applies */ }
                        }
                    }
                }

            var seen = new HashSet<string>();
            foreach (var s in suggested)
            {
                if (s == null || string.IsNullOrEmpty(s.guid) || string.IsNullOrEmpty(s.name)) continue;
                string guid = s.guid.ToLowerInvariant();
                if (!RefGuidRe.IsMatch(guid) || !RefNameRe.IsMatch(s.name)) continue; // untrusted: drop
                if (!seen.Add(guid)) continue;
                if (existing.Contains(guid) || existing.Contains("GUID:" + guid) || existing.Contains(s.name)) continue;

                // Present in THIS project? (by guid, else by assembly name for e.g. package assemblies)
                bool present = !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid));
                if (!present)
                {
                    string p = UnityEditor.Compilation.CompilationPipeline
                        .GetAssemblyDefinitionFilePathFromAssemblyName(s.name);
                    if (!string.IsNullOrEmpty(p))
                    {
                        present = true;
                        guid = AssetDatabase.AssetPathToGUID(p); // use the LOCAL project's guid
                    }
                }
                if (present) result.addable.Add(new SuggestedRef { name = s.name, guid = guid });
                else result.unavailable.Add(s.name);
            }
            return result;
        }

        /// <summary>
        /// Append the given references to the world asmdef. STRICTLY additive: existing entries and
        /// every other field are preserved byte-for-byte; only the references array gains entries.
        /// Entry style (GUID vs name) follows whatever the file already uses.
        /// </summary>
        public static void AddWorldReferences(WorldInfo world, List<SuggestedRef> refs)
        {
            if (refs == null || refs.Count == 0) return;
            string asmdefPath = WorldAsmdefPath(world);
            if (asmdefPath == null) throw new Exception("world asmdef not found");

            string json = File.ReadAllText(asmdefPath);
            var m = Regex.Match(json, "(\"references\"\\s*:\\s*\\[)([^\\]]*)(\\])");
            if (!m.Success) throw new Exception("world asmdef has no references array; not touching it");

            var entries = new List<string>();
            foreach (Match e in Regex.Matches(m.Groups[2].Value, "\"([^\"]+)\"")) entries.Add(e.Groups[1].Value);
            bool guidStyle = entries.Count == 0 || entries.Exists(v => v.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase));

            foreach (var r in refs)
            {
                string guid = (r.guid ?? "").ToLowerInvariant();
                if (!RefGuidRe.IsMatch(guid) || !RefNameRe.IsMatch(r.name ?? "")) continue; // validated again
                string entry = guidStyle ? "GUID:" + guid : r.name;
                if (!entries.Contains(entry)) entries.Add(entry);
            }

            string rebuilt = m.Groups[1].Value + "\n        \"" +
                             string.Join("\",\n        \"", entries) + "\"\n    " + m.Groups[3].Value;
            json = json.Substring(0, m.Index) + rebuilt + json.Substring(m.Index + m.Length);
            File.WriteAllText(asmdefPath, json);
            AssetDatabase.ImportAsset(asmdefPath.Replace('\\', '/'), ImportAssetOptions.ForceUpdate);
        }
    }
}
#endif
