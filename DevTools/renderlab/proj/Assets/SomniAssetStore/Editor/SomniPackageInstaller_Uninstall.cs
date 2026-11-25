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
        /// <summary>Uninstall a package from whichever lock holds it (world folder or standalone).</summary>
        public static void Uninstall(string packageId, WorldInfo world)
        {
            if (world != null)
            {
                string wlp = LockPathFor(world);
                if (ReadLockAt(wlp).packages.Any(p => p.id == packageId)) { UninstallFrom(packageId, wlp); return; }
            }
            if (ReadLockAt(StandaloneLockPath).packages.Any(p => p.id == packageId))
            { UninstallFrom(packageId, StandaloneLockPath); return; }
            throw new Exception(packageId + " is not installed.");
        }

        private static void UninstallFrom(string packageId, string lockPath)
        {
            var locked = ReadLockAt(lockPath);
            var rec = locked.packages.FirstOrDefault(p => p.id == packageId);
            if (rec == null) throw new Exception(packageId + " is not installed.");

            var dependents = locked.packages
                .Where(p => p.id != packageId)
                .Where(p => FolderReferencesNamespace(p.folder, rec.bakedNamespace))
                .Select(p => p.id).ToList();
            if (dependents.Count > 0)
                throw new Exception(packageId + " is used by: " + string.Join(", ", dependents) + ". Uninstall those first.");

            if (Directory.Exists(rec.folder)) AssetDatabase.DeleteAsset(rec.folder);
            locked.packages.Remove(rec);

            if (locked.packages.Count == 0)
            {
                // Last package gone: remove the lock file AND the now-empty install root so the
                // project returns to exactly its pre-first-install state (no orphan folder/.meta).
                if (File.Exists(lockPath)) File.Delete(lockPath);
                if (File.Exists(lockPath + ".meta")) File.Delete(lockPath + ".meta");
                string root = Path.GetDirectoryName(lockPath).Replace('\\', '/');
                if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0)
                    AssetDatabase.DeleteAsset(root);
            }
            else
            {
                File.WriteAllText(lockPath, JsonUtility.ToJson(locked, true));
            }
            AssetDatabase.Refresh();
        }

        private static bool FolderReferencesNamespace(string folder, string ns)
        {
            if (!Directory.Exists(folder) || string.IsNullOrEmpty(ns)) return false;
            // A dependent can couple to a dependency through code (using / type refs in .cs) OR
            // through a serialized reference in a prefab/scene/asset (a UnityEvent target type
            // string or a [SerializeReference] managed-reference "ns:" field). Scan both so
            // uninstalling a dependency is blocked no matter how a consumer wired it up.
            foreach (string file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (ext == ".cs" || SerializedAssetExts.Contains(ext))
                    if (File.ReadAllText(file).Contains(ns)) return true;
            }
            return false;
        }
    }
}
#endif
