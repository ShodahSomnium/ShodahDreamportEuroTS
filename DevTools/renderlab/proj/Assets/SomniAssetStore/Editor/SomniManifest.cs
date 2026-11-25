// SomniAssetStore - shared manifest models.
// Mirrors schema/manifest.schema.json (hand-synced for now, codegen later).
// Used by both the publisher and the importer. JsonUtility-compatible.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace SomniAssetStore.Shared
{
    [Serializable]
    public class SomniManifest
    {
        public int schemaVersion = 1;
        public string id;                 // lowercase dot-namespaced: author.name
        public string name;               // display name
        public string version;            // semver x.y.z
        public ManifestAuthor author = new ManifestAuthor();
        public string description = "";
        public string category = "other"; // world-asset|world-script|avatar-wearable|...
        public string sourceNamespace;    // the flat placeholder namespace, e.g. SomniPkg.CoolGun
        public ManifestAsmdef asmdef = new ManifestAsmdef();
        public List<SuggestedRef> suggestedReferences = new List<SuggestedRef>();
        public List<ManifestDependency> dependencies = new List<ManifestDependency>();
        public List<ManifestFile> files = new List<ManifestFile>();
        public string createdAt;
    }

    [Serializable]
    public class ManifestAuthor
    {
        public string username;
        public string displayName;
    }

    [Serializable]
    public class ManifestAsmdef
    {
        public string name;
        public List<string> references = new List<string>();
    }

    /// <summary>
    /// An assembly the package's scripts likely need (recorded from the asmdef that compiled them
    /// in the author's project). The installer OFFERS to add missing ones to the consumer's world
    /// asmdef — append-only, consent-gated, and only for assemblies already present in the project.
    /// </summary>
    [Serializable]
    public class SuggestedRef
    {
        public string name;   // asmdef name, e.g. Fusion.Unity
        public string guid;   // asmdef asset guid (32 hex)
    }

    [Serializable]
    public class ManifestDependency
    {
        public string id;
        public string version;            // semver range, e.g. >=1.0.0
    }

    [Serializable]
    public class ManifestFile
    {
        public string path;               // relative under files/
        public string sha256;
        public string kind;               // script|meta|prefab|material|model|texture|asset|asmdef|other
    }

    /// <summary>Server listing shapes (GET /api/packages).</summary>
    [Serializable]
    public class StoreListing
    {
        public string id;
        public string version;
        public string name;
        public string author;
        public string description;
        public string category;
        public string sourceNamespace;
        public string archiveSha;
        public long archiveSize;
        public string status;
        public int downloads;
        public string createdAt;
        public bool hasImage;
        public string signature; // base64 RSA-SHA256 over the archive (null if unsigned)
        public float ratingAvg;  // 0 if unrated
        public int ratingCount;
        public int myRating;     // the signed-in caller's own rating (0 if none / anonymous)
    }

    [Serializable]
    public class StoreListResponse
    {
        public List<StoreListing> packages = new List<StoreListing>();
    }

    /// <summary>One version from GET /api/packages/:id (includes the full manifest for dep resolution).</summary>
    [Serializable]
    public class PackageVersionDetail
    {
        public string id;
        public string version;
        public string archiveSha;
        public string status;
        public string signature;
        public SomniManifest manifest;
    }

    [Serializable]
    public class PackageDetailResponse
    {
        public List<PackageVersionDetail> versions = new List<PackageVersionDetail>();
    }

    public static class ManifestKinds
    {
        public static string ForExtension(string path)
        {
            string p = path.ToLowerInvariant();
            if (p.EndsWith(".cs.meta") || p.EndsWith(".meta")) return "meta";
            if (p.EndsWith(".cs")) return "script";
            if (p.EndsWith(".prefab")) return "prefab";
            if (p.EndsWith(".mat")) return "material";
            if (p.EndsWith(".fbx") || p.EndsWith(".obj")) return "model";
            if (p.EndsWith(".png") || p.EndsWith(".jpg") || p.EndsWith(".jpeg") || p.EndsWith(".tga")) return "texture";
            if (p.EndsWith(".asmdef")) return "asmdef";
            if (p.EndsWith(".asset")) return "asset";
            return "other";
        }
    }
}
#endif
