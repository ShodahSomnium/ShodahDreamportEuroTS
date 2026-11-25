// SomniAssetStore - dependency resolution.
// Walks a package's dependency graph, picks satisfying versions, and returns an
// install order (dependencies before dependents), skipping already-installed ones.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using SomniAssetStore.Shared;

namespace SomniAssetStore.Importer
{
    public class ResolvedPackage
    {
        public string id;
        public string version;
        public string archiveSha;
        public string signature;
    }

    public static class SomniDependencyResolver
    {
        /// <summary>
        /// Resolve the transitive dependencies of a package into install order (deps first).
        /// Already-installed packages that satisfy their range are skipped. Throws on cycles,
        /// missing packages, or unsatisfiable version ranges.
        /// </summary>
        public static List<ResolvedPackage> ResolveDependencies(
            List<ManifestDependency> deps, InstalledLock installed)
        {
            var order = new List<ResolvedPackage>();
            var done = new HashSet<string>();
            var visiting = new HashSet<string>();
            foreach (var d in deps ?? new List<ManifestDependency>())
                Visit(d.id, d.version, installed, order, done, visiting);
            return order;
        }

        private static void Visit(
            string id, string range, InstalledLock installed,
            List<ResolvedPackage> order, HashSet<string> done, HashSet<string> visiting)
        {
            if (done.Contains(id)) return;
            if (visiting.Contains(id)) throw new Exception("Dependency cycle involving " + id);

            // Already installed and satisfies the range? Treat as satisfied, nothing to add.
            var inst = installed.packages.Find(p => p.id == id);
            if (inst != null && Satisfies(inst.version, range)) { done.Add(id); return; }

            visiting.Add(id);
            var detail = SomniStoreClient.GetPackage(id);
            var pick = PickVersion(detail.versions, range);
            if (pick == null)
                throw new Exception("No published version of " + id + " satisfies '" + range + "'.");

            // resolve this package's own dependencies first (post-order = deps before dependents)
            foreach (var sub in pick.manifest?.dependencies ?? new List<ManifestDependency>())
                Visit(sub.id, sub.version, installed, order, done, visiting);

            visiting.Remove(id);
            order.Add(new ResolvedPackage
            {
                id = pick.id, version = pick.version, archiveSha = pick.archiveSha, signature = pick.signature,
            });
            done.Add(id);
        }

        /// <summary>Latest published version satisfying the range (approved versions come back from the API).</summary>
        private static PackageVersionDetail PickVersion(List<PackageVersionDetail> versions, string range)
        {
            if (versions == null || versions.Count == 0) return null;
            versions.Sort((a, b) => SomniPackageInstaller.CompareVersions(b.version, a.version)); // newest first
            foreach (var v in versions)
                if (Satisfies(v.version, range)) return v;
            return null;
        }

        /// <summary>Minimal range support: empty / "*" = any; "&gt;=x.y.z" = at least; else exact match.</summary>
        public static bool Satisfies(string version, string range)
        {
            if (string.IsNullOrEmpty(range) || range == "*") return true;
            range = range.Trim();
            if (range.StartsWith(">="))
                return SomniPackageInstaller.CompareVersions(version, range.Substring(2).Trim()) >= 0;
            return version == range;
        }
    }
}
#endif
