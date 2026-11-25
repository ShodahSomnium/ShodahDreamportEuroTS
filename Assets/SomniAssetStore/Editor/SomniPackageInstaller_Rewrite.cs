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
        /// <summary>
        /// Apply a text transform to a file's bytes while PRESERVING its original encoding + BOM,
        /// and return the ORIGINAL bytes untouched when the transform changes nothing. This keeps
        /// UTF-16 / BOM'd .cs and .asset files from being corrupted by a blind UTF-8 round-trip
        /// (which would leave the baked script uncompilable), and guarantees byte-identical output
        /// for any text asset the rebase does not actually touch.
        /// </summary>
        private static byte[] RewriteTextPreservingEncoding(byte[] data, Func<string, string> transform)
        {
            bool utf8Bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
            bool utf16le = data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE;
            bool utf16be = data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF;
            Encoding body = utf16le ? Encoding.Unicode : utf16be ? Encoding.BigEndianUnicode : Encoding.UTF8;
            int off = utf8Bom ? 3 : (utf16le || utf16be) ? 2 : 0;

            string text = body.GetString(data, off, data.Length - off);
            string rewritten = transform(text);
            if (string.Equals(rewritten, text, StringComparison.Ordinal)) return data; // untouched -> verbatim

            byte[] enc = body.GetBytes(rewritten);
            byte[] bom = utf8Bom ? new byte[] { 0xEF, 0xBB, 0xBF }
                       : utf16le ? new byte[] { 0xFF, 0xFE }
                       : utf16be ? new byte[] { 0xFE, 0xFF } : new byte[0];
            if (bom.Length == 0) return enc;
            byte[] outp = new byte[bom.Length + enc.Length];
            Buffer.BlockCopy(bom, 0, outp, 0, bom.Length);
            Buffer.BlockCopy(enc, 0, outp, bom.Length, enc.Length);
            return outp;
        }

        // ---------------------------------------------------------------- rewrite

        /// <summary>
        /// Apply a set of exact-token replacements, longest key first, on dotted-identifier
        /// boundaries so "SomniPkg.CoolGun" never partially matches "SomniPkg.CoolGunner" and a
        /// key that is a prefix of a longer key is only ever rewritten as part of the longer one.
        /// Empty keys are skipped. This is the single primitive behind every rebase below.
        /// </summary>
        private static string ReplaceTokens(
            string source, IEnumerable<KeyValuePair<string, string>> map)
        {
            foreach (var kv in map.Where(kv => !string.IsNullOrEmpty(kv.Key)).OrderByDescending(kv => kv.Key.Length))
            {
                string pattern = @"(?<![\w.])" + Regex.Escape(kv.Key) + @"(?![\w])";
                source = Regex.Replace(source, pattern, kv.Value);
            }
            return source;
        }

        /// <summary>
        /// Token-based namespace rebase. Replaces the package's own flat namespace and any
        /// installed-dependency / editor namespaces. Longest tokens first, word-boundary safe.
        /// </summary>
        public static string RebaseNamespaces(
            string source, string fromNs, string toNs, Dictionary<string, string> depMap)
        {
            var replacements = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(fromNs, toNs) };
            replacements.AddRange(depMap);
            return ReplaceTokens(source, replacements);
        }

        /// <summary>
        /// Rebase the serialized type references inside a Unity YAML asset (prefab, scene, .asset,
        /// animator/animation). Two surgical passes, each matching only exact dotted-identifier
        /// tokens that literally spell out the PACKAGE'S OWN namespace or assembly:
        ///   1. namespace tokens (source + editor + deps) -> baked. Covers "Ns.Class" in a
        ///      UnityEvent m_TargetAssemblyTypeName ("Ns.Class, Assembly") and the "ns:" field of
        ///      a [SerializeReference] managed-reference type.
        ///   2. assembly names -> baked asmdef names. Covers the ", Assembly" suffix of the same
        ///      UnityEvent string and the "asm:" field of a managed reference.
        /// Because the only strings that can match are the package's own namespace/assembly tokens,
        /// this cannot disturb any reference to the world, the SDK, or another package.
        /// asmNameRemap is empty for packages without an asmdef (their scripts compile into the
        /// world assembly, whose name does not change), so pass 2 is then a no-op.
        /// </summary>
        private static string RebaseSerializedAsset(
            string text, string fromNs, string toNs,
            Dictionary<string, string> nsMap, Dictionary<string, string> asmNameRemap)
        {
            text = RebaseNamespaces(text, fromNs, toNs, nsMap);
            text = ReplaceTokens(text, asmNameRemap);
            return text;
        }

        private static readonly Regex MetaGuidRe =
            new Regex(@"guid:\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);

        private static readonly HashSet<string> TextAssetExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".meta", ".asmdef", ".prefab", ".asset", ".mat", ".unity",
            ".anim", ".controller", ".json", ".txt", ".shader", ".shadergraph",
        };

        private static bool IsTextAsset(string path)
        {
            return TextAssetExts.Contains(Path.GetExtension(path));
        }

        // Unity YAML assets that can serialize a C# type reference as a STRING (not just a GUID):
        // MonoBehaviour UnityEvents (m_TargetAssemblyTypeName) and [SerializeReference] managed
        // references (type: {class, ns, asm}). These need the namespace/assembly rebased in text,
        // because the built world resolves those by name, not by the editor-only GUID.
        private static readonly HashSet<string> SerializedAssetExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".asset", ".controller", ".anim",
        };

        private static bool IsSerializedAsset(string path)
        {
            return SerializedAssetExts.Contains(Path.GetExtension(path));
        }

        /// <summary>
        /// Map each incoming .meta GUID that already exists in the project to a fresh GUID.
        /// Empty on a normal install (GUIDs are unique to the package). Only fires when a
        /// GUID collides with an asset already present (e.g. authoring project reinstall).
        /// </summary>
        private static Dictionary<string, string> BuildGuidRemap(
            SomniManifest manifest, Dictionary<string, byte[]> fileData)
        {
            var remap = new Dictionary<string, string>();
            foreach (var f in manifest.files)
            {
                if (!f.path.EndsWith(".meta")) continue;
                var m = MetaGuidRe.Match(Encoding.UTF8.GetString(fileData[f.path]));
                if (!m.Success) continue;
                string guid = m.Groups[1].Value;
                if (remap.ContainsKey(guid)) continue;
                if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                    remap[guid] = Guid.NewGuid().ToString("N"); // 32 lowercase hex, Unity-compatible
            }
            return remap;
        }

        /// <summary>Read a top-level "field": "value" string out of asmdef JSON (null if absent).</summary>
        private static string ReadJsonString(string json, string field)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string PatchAsmdef(
            string json, string bakedName, string bakedRootNs,
            Dictionary<string, string> asmdefNameRemap, Dictionary<string, string> depMap)
        {
            json = Regex.Replace(json, "\"name\"\\s*:\\s*\"[^\"]*\"", "\"name\": \"" + bakedName + "\"");
            // rootNamespace may be absent; add it if missing so the assembly is anchored correctly.
            if (Regex.IsMatch(json, "\"rootNamespace\"\\s*:"))
                json = Regex.Replace(json, "\"rootNamespace\"\\s*:\\s*\"[^\"]*\"", "\"rootNamespace\": \"" + bakedRootNs + "\"");
            // Rewrite intra-package asmdef references (by name) and installed-dependency namespaces.
            // Longest keys first so a name that is a prefix of another is not partially rewritten.
            foreach (var kv in asmdefNameRemap.OrderByDescending(k => k.Key.Length))
                json = json.Replace("\"" + kv.Key + "\"", "\"" + kv.Value + "\"");
            foreach (var kv in depMap)
                json = json.Replace("\"" + kv.Key + "\"", "\"" + kv.Value + "\"");
            json = DropDanglingGuidReferences(json);
            return json;
        }

        /// <summary>
        /// Remove "GUID:xxxxxxxx" references whose asset does not exist in this project. A dangling
        /// GUID reference is unambiguously broken (it points at one specific missing asset) and would
        /// fail the whole project's compilation, so dropping it is strictly safer. NAME references are
        /// left untouched on purpose: Unity built-in/precompiled assemblies (e.g. UnityEngine.UI) are
        /// not asmdef-resolvable, so treating an unresolved name as "dangling" would false-drop valid
        /// references and break otherwise-fine code.
        /// </summary>
        private static string DropDanglingGuidReferences(string json)
        {
            var m = Regex.Match(json, "(\"references\"\\s*:\\s*\\[)([^\\]]*)(\\])");
            if (!m.Success) return json;
            var kept = new List<string>();
            bool dropped = false;
            foreach (Match e in Regex.Matches(m.Groups[2].Value, "\"([^\"]+)\""))
            {
                string v = e.Groups[1].Value;
                if (v.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(v.Substring(5))))
                {
                    dropped = true; // dangling: the referenced asmdef asset is not in this project
                    continue;
                }
                kept.Add(v);
            }
            if (!dropped) return json;
            string rebuilt = kept.Count == 0
                ? m.Groups[1].Value + m.Groups[3].Value
                : m.Groups[1].Value + "\n        \"" + string.Join("\",\n        \"", kept) + "\"\n    " + m.Groups[3].Value;
            return json.Substring(0, m.Index) + rebuilt + json.Substring(m.Index + m.Length);
        }
    }
}
#endif
