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
        // ------------------------------------------------- untrusted-input guards

        private static readonly Regex PackageIdRe =
            new Regex(@"^[a-z0-9]+(\.[a-z0-9_]+)+$", RegexOptions.Compiled);
        private static readonly Regex NamespaceRe2 =
            new Regex(@"^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.Compiled);

        /// <summary>A package id must be safe to use as a single folder name (no path chars).</summary>
        public static bool IsSafePackageId(string id)
        {
            return !string.IsNullOrEmpty(id) && id.Length <= 128 && PackageIdRe.IsMatch(id) &&
                   !id.Contains("..") && id.IndexOf('/') < 0 && id.IndexOf('\\') < 0;
        }

        // Windows reserved device names: a file named CON / NUL / COM1 etc. (any extension) does
        // not create a real file, so a manifest that ships one would silently write nothing (or to
        // the device). Rejected as any path segment, case-insensitively, matching the server.
        private static readonly Regex ReservedNameRe =
            new Regex(@"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>A file path from an untrusted manifest: relative, no traversal, no drive/UNC/control chars.</summary>
        public static bool IsSafeRelPath(string p)
        {
            if (string.IsNullOrEmpty(p) || p.Length > 255) return false;
            foreach (char c in p) if (c < 0x20 || c == 0x7f) return false; // control chars incl NUL
            if (p.IndexOf(':') >= 0) return false;                        // drive letter / ADS
            string norm = p.Replace('\\', '/');
            if (norm.StartsWith("/")) return false;                       // absolute
            foreach (string seg in norm.Split('/'))
            {
                if (seg.Length == 0 || seg == "." || seg == "..") return false;
                if (ReservedNameRe.IsMatch(seg)) return false;            // CON, NUL, COM1, ...
            }
            return true;
        }

        /// <summary>
        /// Resolve <paramref name="rel"/> under <paramref name="destFolder"/> and confirm the
        /// canonical result stays inside it. Defence-in-depth on top of IsSafeRelPath: a hostile
        /// or MITM'd server cannot make us write outside the package folder.
        /// </summary>
        private static string ContainedPath(string destFolder, string rel)
        {
            string rootFull = Path.GetFullPath(destFolder).Replace('\\', '/').TrimEnd('/') + "/";
            string full = Path.GetFullPath(Path.Combine(destFolder, rel)).Replace('\\', '/');
            if (!full.StartsWith(rootFull, StringComparison.Ordinal))
                throw new Exception("package tried to write outside its folder: " + rel);
            return full;
        }
    }
}
#endif
