// SomniAssetStore - guards that keep EDITOR code out of the world's runtime scripting
// assembly. Somnium ships exactly one assembly per world; if editor-only code (UnityEditor
// API) compiles into it, the uploaded world is rejected by Somnium's build scanner. This is
// the exact failure that has bitten users, so it is defended three ways for world installs:
//   1. broadened editor-only asmdef detection (includePlatforms / excludePlatforms /
//      defineConstraints), so a real editor asmdef is KEPT, never stripped into the world
//   2. auto-wrap: editor .cs shipped WITHOUT an asmdef (the "Editor/" magic folder, which a
//      governing world asmdef silently defeats) get a generated editor-only asmdef
//   3. a hard backstop: if any .cs that would land in the world assembly still uses editor
//      API, the install is refused rather than allowed to break the world.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SomniAssetStore.Importer
{
    public static partial class SomniPackageInstaller
    {
        // Editor-only API usage in a C# source body. Deliberately broad: a false positive only
        // means a file is quarantined into an editor assembly (safe); a false negative would let
        // editor code into the world (unsafe), so we err toward catching more.
        private static readonly Regex EditorApiRe = new Regex(
            @"(^|\n)\s*using\s+UnityEditor\s*(;|\.)" +
            @"|(?<![\w.])UnityEditor\s*\." +
            @"|\[\s*(MenuItem|CustomEditor|CustomPropertyDrawer|InitializeOnLoad|InitializeOnLoadMethod|DidReloadScripts)\b" +
            @"|:\s*(EditorWindow|ScriptableWizard|AssetPostprocessor|AssetModificationProcessor|PropertyDrawer)\b",
            RegexOptions.Compiled);

        /// <summary>True if a .cs body uses editor-only API (so it must not enter the world assembly).</summary>
        public static bool CsUsesEditorApi(string csText) => !string.IsNullOrEmpty(csText) && EditorApiRe.IsMatch(csText);

        // Every non-Editor platform Unity currently knows about. Used to recognise an editor-only
        // asmdef expressed via excludePlatforms (exclude everything that ships).
        private static readonly string[] RuntimePlatforms =
        {
            "WindowsStandalone32", "WindowsStandalone64", "macOSStandalone", "LinuxStandalone64",
            "LinuxStandalone32", "iOS", "Android", "WebGL", "PS4", "PS5", "XboxOne",
            "GameCoreXboxOne", "GameCoreXboxSeries", "GameCoreScarlett", "Switch", "tvOS",
            "VisionOS", "EmbeddedLinux", "QNX", "WindowsStoreApps", "Lumin", "Stadia", "CloudRendering",
        };

        private static List<string> JsonStringArray(string json, string field)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\\[([^\\]]*)\\]");
            var list = new List<string>();
            if (m.Success)
                foreach (Match e in Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"")) list.Add(e.Groups[1].Value);
            return list;
        }

        /// <summary>
        /// Whether an asmdef is editor-only (excluded from player/world builds), recognising all
        /// three ways Unity expresses it: includePlatforms == [Editor]; defineConstraints contains
        /// UNITY_EDITOR; or includePlatforms empty with excludePlatforms covering every shipping
        /// platform. An asmdef that includes Editor AND a runtime platform is NOT editor-only.
        /// </summary>
        public static bool IsEditorOnlyAsmdef(string json)
        {
            if (JsonStringArray(json, "defineConstraints").Any(d => d.Trim() == "UNITY_EDITOR")) return true;

            var inc = JsonStringArray(json, "includePlatforms");
            if (inc.Count > 0)
                return inc.Count == 1 && inc[0] == "Editor"; // ONLY Editor -> editor-only; Editor+runtime -> not

            var exc = JsonStringArray(json, "excludePlatforms");
            if (exc.Count > 0 && !exc.Contains("Editor"))
                return RuntimePlatforms.All(p => exc.Contains(p)); // every shipping platform excluded
            return false;
        }

        /// <summary>
        /// Reason an asmdef cannot be safely stripped and merged into the world assembly (its
        /// settings would have to be dropped, silently changing compilation), or null if it can.
        /// </summary>
        private static string AsmdefStripBlocker(string json)
        {
            if (JsonStringArray(json, "precompiledReferences").Count > 0)
                return "it references precompiled DLLs (precompiledReferences), which cannot be merged into the world assembly";
            if (Regex.IsMatch(json, "\"allowUnsafeCode\"\\s*:\\s*true"))
                return "it requires unsafe code (allowUnsafeCode), which the world assembly does not enable";
            var dc = JsonStringArray(json, "defineConstraints");
            if (dc.Count > 0 && !dc.Any(d => d.Trim() == "UNITY_EDITOR"))
                return "it has define constraints (" + string.Join(", ", dc) + ") that the world assembly does not satisfy";
            return null;
        }

        /// <summary>Path segments of <paramref name="rel"/> that are (case-insensitively) "Editor".</summary>
        private static bool IsUnderEditorFolder(string rel)
        {
            foreach (var seg in rel.Replace('\\', '/').Split('/'))
                if (string.Equals(seg, "Editor", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Directory portion of a package-relative path ("" for a root-level file).</summary>
        private static string ParentDir(string rel)
        {
            rel = rel.Replace('\\', '/');
            int i = rel.LastIndexOf('/');
            return i < 0 ? "" : rel.Substring(0, i);
        }

        /// <summary>
        /// The shallowest ancestor directory of <paramref name="rel"/> whose own name is "Editor"
        /// (so a generated asmdef placed there quarantines the whole Editor subtree), or null.
        /// </summary>
        private static string EditorFolderRoot(string rel)
        {
            var parts = rel.Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length - 1; i++) // exclude the file name itself
                if (string.Equals(parts[i], "Editor", StringComparison.OrdinalIgnoreCase))
                    return string.Join("/", parts.Take(i + 1));
            return null;
        }

        /// <summary>True if <paramref name="rel"/> sits inside directory <paramref name="dir"/> (or dir is root "").</summary>
        private static bool PathUnder(string rel, string dir)
        {
            rel = rel.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir)) return true;
            return rel.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
#endif
