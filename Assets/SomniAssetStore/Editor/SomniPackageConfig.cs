// SomniAssetStore - package config.
// Plain metadata for a package, saved as a dotfile (.somnipackage.json) INSIDE the package
// folder. Unity ignores dotfiles, so there is no ScriptableObject / .meta to manage — the
// folder IS the package. The publisher reads/writes this; the builder reads the folder.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SomniAssetStore.Publisher
{
    [Serializable]
    public class SomniPackageConfig
    {
        public string id = "";
        public string name = "";
        public string version = "1.0.0";
        public string authorUsername = "";
        public string authorDisplayName = "";
        public string description = "";
        public string category = "other";
        public string sourceNamespace = "";
        public string screenshotPath = "";
        public List<Dep> dependencies = new List<Dep>();

        // Kept in sync with the server (categories.ts) and web (api.ts).
        public static readonly string[] CategoryIds =
            { "world-asset", "world-script", "avatar-wearable", "avatar-script", "shader-material", "audio", "editor-tool", "other" };
        public static readonly string[] CategoryLabels =
            { "World Asset", "World Script", "Avatar Wearable", "Avatar Script", "Shader / Material", "Audio", "Editor Tool", "Other" };

        [Serializable]
        public class Dep
        {
            public string id = "";
            public string versionRange = ">=1.0.0";
        }

        public const int TitleMax = 60;
        public const int DescriptionMax = 1000;
        public const int ScreenshotMaxDim = 1920;
        public const int ScreenshotMaxBytes = 4 * 1024 * 1024;

        public const string FileName = ".somnipackage.json"; // dotfile → Unity ignores it (no .meta)
        public const string ScreenshotName = "somni-preview.png"; // captured preview lives in the folder; not shipped

        public static string PathFor(string folder) => folder.TrimEnd('/') + "/" + FileName;

        public static SomniPackageConfig Load(string folder)
        {
            string p = PathFor(folder);
            if (File.Exists(p))
            {
                try
                {
                    var c = JsonUtility.FromJson<SomniPackageConfig>(File.ReadAllText(p));
                    if (c != null) return c;
                }
                catch { /* fall through to a fresh config */ }
            }
            return new SomniPackageConfig();
        }

        public void Save(string folder)
        {
            File.WriteAllText(PathFor(folder), JsonUtility.ToJson(this, true));
        }
    }
}
#endif
