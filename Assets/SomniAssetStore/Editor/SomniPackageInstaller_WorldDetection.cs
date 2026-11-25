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
        // -------------------------------------------------- world detection + override
        //
        // A "world" is any asmdef whose name/rootNamespace begins with SomniumSpace.Worlds.
        // Most projects have exactly one, and the SDK convention puts it at the world folder
        // root — but people reorganise, and some projects legitimately hold more than one world.
        // So: auto-detect finds every candidate, and a per-project manual override (stored by the
        // asmdef's GUID, which survives moves/renames) lets the user pin the exact one. The
        // install location is always derived from the chosen asmdef's own folder, so a moved
        // asmdef still bakes its packages into the assembly that actually governs them.

        /// <summary>EditorPrefs key for the manual world override, namespaced per project.</summary>
        private static string WorldOverrideKey => "Somni.WorldOverride." + StableHash(Application.dataPath);

        private static string StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (char c in s ?? "") h = h * 31 + c;
                return h.ToString("x8");
            }
        }

        /// <summary>GUID of the asmdef the user pinned as their world, or "" for auto-detect.</summary>
        public static string GetWorldOverrideGuid() => EditorPrefs.GetString(WorldOverrideKey, "");

        /// <summary>Pin a specific world asmdef (pass "" / null to clear and return to auto-detect).</summary>
        public static void SetWorldOverride(string asmdefGuid)
        {
            if (string.IsNullOrEmpty(asmdefGuid)) EditorPrefs.DeleteKey(WorldOverrideKey);
            else EditorPrefs.SetString(WorldOverrideKey, asmdefGuid);
        }

        /// <summary>Build a WorldInfo from one asmdef path; null if it is not a SomniumSpace.Worlds.* asmdef.</summary>
        public static WorldInfo WorldFromAsmdef(string path)
        {
            var w = WorldFromAsmdefLoose(path);
            return w != null && w.rootNamespace.StartsWith("SomniumSpace.Worlds.") ? w : null;
        }

        /// <summary>
        /// Manual-pin variant: build a WorldInfo from ANY asmdef the user explicitly chose, using
        /// rootNamespace (or name as fallback) verbatim. Auto-detect stays strict so it never
        /// guesses a random assembly, but when the user drags their world asmdef in we trust them —
        /// some projects' asmdefs are arranged in ways the strict scan doesn't recognise. Install
        /// still validates the root before baking, so a truly wrong pick fails loudly, not silently.
        /// </summary>
        public static WorldInfo WorldFromAsmdefLoose(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string norm = path.Replace('\\', '/');
            if (norm.Contains("/" + PackagesSubfolder + "/")) return null; // never a baked package
            try
            {
                string json = File.ReadAllText(norm);
                string root = ReadJsonString(json, "rootNamespace");
                if (string.IsNullOrEmpty(root)) root = ReadJsonString(json, "name");
                if (string.IsNullOrEmpty(root)) return null;
                return new WorldInfo
                {
                    rootNamespace = root,
                    folder = Path.GetDirectoryName(norm).Replace('\\', '/'),
                    asmdefGuid = AssetDatabase.AssetPathToGUID(norm),
                };
            }
            catch { return null; }
        }

        /// <summary>Every world asmdef in the project (excluding already-baked packages).</summary>
        public static List<WorldInfo> DetectWorlds()
        {
            var results = new List<WorldInfo>();
            var seen = new HashSet<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:AssemblyDefinitionAsset"))
            {
                var w = WorldFromAsmdef(AssetDatabase.GUIDToAssetPath(guid));
                if (w != null && seen.Add(w.folder)) results.Add(w);
            }
            results.Sort((a, b) => string.CompareOrdinal(a.rootNamespace, b.rootNamespace));
            return results;
        }

        /// <summary>
        /// The world to install into: the manual override if it still points at a valid world
        /// asmdef, otherwise the sole/first auto-detected candidate. A stale override (asmdef
        /// deleted or no longer a world) silently falls back to auto-detect. Null if none exist.
        /// </summary>
        public static WorldInfo DetectWorld()
        {
            string overrideGuid = GetWorldOverrideGuid();
            if (!string.IsNullOrEmpty(overrideGuid))
            {
                // Loose here on purpose: a manually pinned asmdef (drag-and-drop) may not match
                // the strict pattern — the user chose it explicitly, so honour it across sessions.
                var pinned = WorldFromAsmdefLoose(AssetDatabase.GUIDToAssetPath(overrideGuid));
                if (pinned != null) return pinned;
            }
            var all = DetectWorlds();
            return all.Count > 0 ? all[0] : null;
        }

        // -------------------------------------------------- world root guidance (#4)
        //
        // Somnium REQUIRES a world's scripting assembly rootNamespace to be SomniumSpace.Worlds.<X>
        // (SDK WorldConfig forces it). New users often set the asmdef NAME but leave rootNamespace
        // blank/wrong, then hit a confusing "no world detected" refusal at install. These helpers let
        // the store window explain the exact problem and offer a one-click fix instead of refusing.

        /// <summary>True when the world's asmdef root is the SomniumSpace.Worlds.* form Somnium requires.</summary>
        public static bool WorldRootValid(WorldInfo w) =>
            w != null && !string.IsNullOrEmpty(w.rootNamespace) &&
            w.rootNamespace.StartsWith("SomniumSpace.Worlds.", StringComparison.Ordinal);

        /// <summary>
        /// The corrected world root for an asmdef whose rootNamespace is not SomniumSpace.Worlds.*.
        /// Keeps whatever meaningful name the user already had (sanitised to identifier chars), so
        /// "Benfreeze" -> "SomniumSpace.Worlds.Benfreeze". Empty/garbage -> "...MyWorld".
        /// </summary>
        public static string SuggestWorldRoot(WorldInfo w)
        {
            string cur = (w != null ? w.rootNamespace : null) ?? "";
            if (cur.StartsWith("SomniumSpace.Worlds.", StringComparison.Ordinal)) return cur;
            var parts = cur.Split('.')
                .Select(p => Regex.Replace(p, "[^A-Za-z0-9_]", ""))
                .Where(p => p.Length > 0 && (char.IsLetter(p[0]) || p[0] == '_'))
                .ToArray();
            string tail = parts.Length > 0 ? string.Join(".", parts) : "MyWorld";
            return "SomniumSpace.Worlds." + tail;
        }

        /// <summary>
        /// Return asmdef JSON with its rootNamespace set to <paramref name="newRoot"/> - replacing the
        /// existing value, or inserting the field right after "name" when absent. Pure/text-only so it
        /// is unit-testable; the on-disk write + reimport lives in FixWorldRoot.
        /// </summary>
        public static string PatchAsmdefRootNamespace(string json, string newRoot)
        {
            if (json == null) return null;
            if (Regex.IsMatch(json, "\"rootNamespace\"\\s*:"))
                return Regex.Replace(json, "(\"rootNamespace\"\\s*:\\s*\")[^\"]*(\")",
                    m => m.Groups[1].Value + newRoot + m.Groups[2].Value);
            var nameM = Regex.Match(json, "\"name\"\\s*:\\s*\"[^\"]*\"");
            if (nameM.Success)
                return json.Substring(0, nameM.Index + nameM.Length) +
                       ",\n    \"rootNamespace\": \"" + newRoot + "\"" +
                       json.Substring(nameM.Index + nameM.Length);
            int brace = json.IndexOf('{');
            if (brace >= 0)
                return json.Substring(0, brace + 1) + "\n    \"rootNamespace\": \"" + newRoot + "\"," +
                       json.Substring(brace + 1);
            return json;
        }

        /// <summary>
        /// Set a world asmdef's rootNamespace to a valid SomniumSpace.Worlds.* value and reimport so
        /// detection + install accept it. Returns the new root. This edits the USER'S world asmdef -
        /// the one thing outside a package we ever touch - and only ever runs on an explicit click.
        /// </summary>
        public static string FixWorldRoot(string asmdefGuid)
        {
            string path = AssetDatabase.GUIDToAssetPath(asmdefGuid);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new Exception("World asmdef not found for the pinned world.");
            var w = WorldFromAsmdefLoose(path);
            string newRoot = SuggestWorldRoot(w);
            File.WriteAllText(path, PatchAsmdefRootNamespace(File.ReadAllText(path), newRoot));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return newRoot;
        }
    }
}
#endif
