// SomniAssetStore - publisher window.
// Point it at your package FOLDER, fill in the details, and publish. No ScriptableObject:
// metadata is saved as a .somnipackage.json dotfile inside the folder (Unity ignores it).
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using SomniAssetStore.Shared;
using UnityEditor;
using UnityEngine;

namespace SomniAssetStore.Publisher
{
    public class SomniPublisherWindow : EditorWindow
    {
        private DefaultAsset _folder;
        private string _folderPath;         // loaded config belongs to this folder
        private SomniPackageConfig _cfg = new SomniPackageConfig();
        private Texture2D _screenshot;
        private const string StoreUrl = SomniAssetStore.Importer.SomniStoreClient.DefaultServerUrl;
        private string _status = "";
        private Vector2 _scroll;
        private bool _showDeps;
        private bool _dirty;                   // config edited but not yet written to disk
        private static GUIStyle _descStyle;   // word-wrapping multiline area (built lazily in OnGUI)

        [MenuItem("SomniAssetStore/Somni Publisher")]
        public static void Open()
        {
            var w = GetWindow<SomniPublisherWindow>("Somni Publisher");
            w.minSize = new Vector2(480, 560);
        }

        /// <summary>Open the publisher already pointed at a folder (used by the pull-to-update flow).</summary>
        public static void OpenForFolder(string folderPath)
        {
            var w = GetWindow<SomniPublisherWindow>("Somni Publisher");
            w.minSize = new Vector2(480, 560);
            w.Focus();
            w.SyncFolder(AssetDatabase.LoadAssetAtPath<DefaultAsset>(folderPath));
            w.Repaint();
        }

        private void OnEnable()
        {
            SomniCredentials.EnsureResolved();
            SomniVersion.EnsureChecked();
        }

        private void OnFocus() => SomniVersion.MaybeCheck();

        private void OnGUI()
        {
            SomniStyle.Init();
            SomniStyle.FillBackground(position.width, position.height);
            DrawHeader();
            SomniVersion.DrawUpdateBanner();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space(2);

            // ---- folder ----------------------------------------------------
            EditorGUILayout.BeginVertical(SomniStyle.Card);
            GUILayout.Label("PACKAGE FOLDER", SomniStyle.SectionLabel);
            EditorGUILayout.Space(4);
            var newFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                "Folder", _folder, typeof(DefaultAsset), false);
            GUILayout.Label("Drag the folder that holds your package's scripts and prefabs.", SomniStyle.Subtitle);
            SyncFolder(newFolder);

            if (_folder != null && string.IsNullOrEmpty(_folderPath))
                EditorGUILayout.HelpBox("That isn't a folder. Drag a project folder here.", MessageType.Warning);
            EditorGUILayout.EndVertical();

            if (!string.IsNullOrEmpty(_folderPath))
            {
                DrawDetailsCard();
                DrawConnectionCard();
                DrawActions();
            }

            EditorGUILayout.Space(8);
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, _status.StartsWith("ERROR") ? MessageType.Error : MessageType.Info);

            EditorGUILayout.EndScrollView();
            SomniVersion.DrawFooter();
        }

        // ---------------------------------------------------------------- folder

        private void SyncFolder(DefaultAsset newFolder)
        {
            _folder = newFolder;
            string path = _folder != null ? AssetDatabase.GetAssetPath(_folder) : null;
            if (path != null && !AssetDatabase.IsValidFolder(path)) path = null;
            if (path == _folderPath) return;

            FlushConfig(); // persist edits to the previous folder before switching away
            _folderPath = path;
            if (path == null) return;

            _cfg = SomniPackageConfig.Load(path);
            _screenshot = string.IsNullOrEmpty(_cfg.screenshotPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<Texture2D>(_cfg.screenshotPath);

            // sensible auto-fill for a fresh package
            string folderName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(_cfg.sourceNamespace))
                _cfg.sourceNamespace = DetectNamespace(path) ?? ("SomniPkg." + SafeIdent(folderName));
            if (string.IsNullOrEmpty(_cfg.name)) _cfg.name = folderName;
            // if we already know who we publish as, suggest an id
            if (string.IsNullOrEmpty(_cfg.id) && !string.IsNullOrEmpty(SomniCredentials.Username))
            {
                // Folders are often named "Author.Package" (e.g. Bentlybro.SomniumTabletFramework).
                // Drop a leading segment that IS the author so the id isn't "bentlybro.bentlybro...".
                string namePart = folderName;
                int dot = namePart.IndexOf('.');
                if (dot > 0 && string.Equals(namePart.Substring(0, dot), SomniCredentials.Username,
                        StringComparison.OrdinalIgnoreCase))
                    namePart = namePart.Substring(dot + 1);
                _cfg.id = SomniCredentials.Username.ToLowerInvariant() + "." + SafeIdent(namePart).ToLowerInvariant();
            }
        }

        /// <summary>
        /// The common ROOT namespace of every .cs in the folder, so a Runtime + Editor split
        /// (e.g. Foo and Foo.Editor) resolves to Foo rather than whichever file was scanned first.
        /// </summary>
        private static string DetectNamespace(string folder)
        {
            var namespaces = new List<string>();
            foreach (string cs in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                var m = Regex.Match(File.ReadAllText(cs), @"^\s*namespace\s+([A-Za-z_][\w\.]*)", RegexOptions.Multiline);
                if (m.Success) namespaces.Add(m.Groups[1].Value);
            }
            if (namespaces.Count == 0) return null;
            string root = SomniPackageBuilder.CommonNamespaceRoot(namespaces);
            return string.IsNullOrEmpty(root) ? namespaces[0] : root;
        }

        private static string SafeIdent(string s)
        {
            string v = Regex.Replace(s ?? "", @"[^A-Za-z0-9]", "");
            return string.IsNullOrEmpty(v) ? "Package" : v;
        }

        // ---------------------------------------------------------------- details

        private void DrawDetailsCard()
        {
            EditorGUILayout.BeginVertical(SomniStyle.Card);
            GUILayout.Label("DETAILS", SomniStyle.SectionLabel);
            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();

            _cfg.name = Clamp(EditorGUILayout.TextField("Title", _cfg.name), SomniPackageConfig.TitleMax);
            Counter(_cfg.name.Length, SomniPackageConfig.TitleMax);

            GUILayout.Label("Description");
            // Explicit word-wrapping style so long lines wrap instead of running off the right edge.
            if (_descStyle == null)
                _descStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            // Auto-grow the field to fit its text (bounded by the char cap) so nothing is clipped in
            // an un-scrollable box; the window's own scroll view handles overall length.
            float wrapW = Mathf.Max(200f, EditorGUIUtility.currentViewWidth - 44f);
            float descH = Mathf.Max(72f, _descStyle.CalcHeight(new GUIContent(_cfg.description), wrapW));
            _cfg.description = Clamp(
                EditorGUILayout.TextArea(_cfg.description, _descStyle, GUILayout.Height(descH)),
                SomniPackageConfig.DescriptionMax);
            Counter(_cfg.description.Length, SomniPackageConfig.DescriptionMax);

            EditorGUILayout.Space(4);
            int catIdx = Mathf.Max(0, System.Array.IndexOf(SomniPackageConfig.CategoryIds, _cfg.category));
            catIdx = EditorGUILayout.Popup(
                new GUIContent("Category", "How this asset is listed on the store."),
                catIdx, SomniPackageConfig.CategoryLabels);
            _cfg.category = SomniPackageConfig.CategoryIds[catIdx];

            _cfg.id = EditorGUILayout.TextField(
                new GUIContent("Package ID", "Globally unique, lowercase: author.name"), _cfg.id);
            _cfg.version = EditorGUILayout.TextField("Version", _cfg.version);
            _cfg.sourceNamespace = EditorGUILayout.TextField(
                new GUIContent("Script namespace", "The single namespace all your scripts use (auto-detected)."),
                _cfg.sourceNamespace);

            EditorGUILayout.Space(6);
            GUILayout.Label("Screenshot", SomniStyle.SectionLabel);
            EditorGUILayout.BeginHorizontal();
            Rect pr = GUILayoutUtility.GetRect(140, 100, GUILayout.Width(140), GUILayout.Height(100));
            if (_screenshot != null) GUI.DrawTexture(pr, _screenshot, ScaleMode.ScaleAndCrop);
            else { EditorGUI.DrawRect(pr, SomniStyle.Divider); GUI.Label(pr, "no screenshot", EditorStyles.centeredGreyMiniLabel); }
            GUILayout.Space(10);
            EditorGUILayout.BeginVertical();
            _screenshot = (Texture2D)EditorGUILayout.ObjectField(_screenshot, typeof(Texture2D), false);
            if (GUILayout.Button("Capture Scene view", SomniStyle.SecondaryBtn, GUILayout.Width(160)))
                CaptureFromScene();
            GUILayout.Label("Drag an image, or capture the current Scene view. Auto-resized to fit " +
                            SomniPackageConfig.ScreenshotMaxDim + "px, under 4 MB.", SomniStyle.Subtitle);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            if (_screenshot != null) _cfg.screenshotPath = AssetDatabase.GetAssetPath(_screenshot);

            DrawDependencies();

            // Mark dirty only; writing the config file on every keystroke touches the Assets tree
            // and wakes Unity's asset watcher, which lags the whole editor while typing. We flush
            // to disk when the field/window loses focus, the folder changes, or on build/upload.
            if (EditorGUI.EndChangeCheck())
                _dirty = true;
            EditorGUILayout.EndVertical();
        }

        /// <summary>Persist pending config edits to disk (debounced away from per-keystroke writes).</summary>
        private void FlushConfig()
        {
            if (_dirty && !string.IsNullOrEmpty(_folderPath))
            {
                _cfg.Save(_folderPath);
                _dirty = false;
            }
        }

        private void OnLostFocus() => FlushConfig();
        private void OnDisable() => FlushConfig();

        private void DrawDependencies()
        {
            EditorGUILayout.Space(4);
            _showDeps = EditorGUILayout.Foldout(_showDeps,
                "Dependencies (" + _cfg.dependencies.Count + ")", true);
            if (!_showDeps) return;
            int remove = -1;
            for (int i = 0; i < _cfg.dependencies.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _cfg.dependencies[i].id = EditorGUILayout.TextField(_cfg.dependencies[i].id);
                _cfg.dependencies[i].versionRange = EditorGUILayout.TextField(_cfg.dependencies[i].versionRange, GUILayout.Width(90));
                if (GUILayout.Button("×", GUILayout.Width(22))) remove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (remove >= 0) _cfg.dependencies.RemoveAt(remove);
            if (GUILayout.Button("Add dependency", SomniStyle.SecondaryBtn, GUILayout.Width(130)))
                _cfg.dependencies.Add(new SomniPackageConfig.Dep());
        }

        private void DrawConnectionCard()
        {
            EditorGUILayout.BeginVertical(SomniStyle.Card);
            GUILayout.Label("ACCOUNT", SomniStyle.SectionLabel);
            EditorGUILayout.Space(4);
            if (SomniCredentials.Verified)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Publishing as", SomniStyle.SectionLabel, GUILayout.Width(90));
                GUILayout.Label(SomniCredentials.DisplayName + "  (@" + SomniCredentials.Username + ")", EditorStyles.boldLabel);
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox(
                    string.IsNullOrEmpty(SomniCredentials.Error)
                        ? "Set your upload token once in Somni Tools to publish."
                        : SomniCredentials.Error, MessageType.Info);
                if (GUILayout.Button("Open Somni Tools", SomniStyle.SecondaryBtn, GUILayout.Width(150)))
                    SomniAssetStore.Tools.SomniToolsWindow.Open();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawActions()
        {
            EditorGUILayout.Space(2);
            if (GUILayout.Button("Build .somnipkg (save to disk)", SomniStyle.SecondaryBtn, GUILayout.Height(26)))
                BuildToDisk();
            EditorGUILayout.Space(4);
            if (!SomniCredentials.Verified)
                EditorGUILayout.HelpBox("Set your upload token in the Account window to publish.", MessageType.None);
            using (new EditorGUI.DisabledScope(!SomniCredentials.Verified))
            {
                if (GUILayout.Button("Build + Upload to store", SomniStyle.PrimaryBtn, GUILayout.Height(32)))
                    BuildAndUpload();
            }
        }

        // ---------------------------------------------------------------- header

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(SomniStyle.HeaderBar);
            EditorGUILayout.BeginHorizontal();
            Rect r = GUILayoutUtility.GetRect(26, 26, GUILayout.Width(26), GUILayout.Height(26));
            GUI.DrawTexture(r, SomniStyle.Logo, ScaleMode.ScaleToFit, true);
            var s = new GUIStyle(EditorStyles.boldLabel)
            { alignment = TextAnchor.MiddleCenter, fontSize = 15 };
            s.normal.textColor = Color.white;
            GUI.Label(r, "S", s);
            GUILayout.Space(8);
            EditorGUILayout.BeginVertical();
            GUILayout.Label("Somni Publisher", SomniStyle.Title);
            GUILayout.Label("Point at your package folder, fill in the details, publish", SomniStyle.Subtitle);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private static string Clamp(string s, int max) => s != null && s.Length > max ? s.Substring(0, max) : s;

        private void Counter(int len, int max)
        {
            var style = new GUIStyle(SomniStyle.Meta) { alignment = TextAnchor.MiddleRight };
            if (len >= max) style.normal.textColor = SomniStyle.Amber;
            GUILayout.Label(len + " / " + max, style);
        }

        // ---------------------------------------------------------------- build

        private void SavePrefs()
        {
        }

        /// <summary>Author always comes from the verified token, never from user input.</summary>
        private void ApplyIdentityToConfig()
        {
            if (!SomniCredentials.Verified) return;
            _cfg.authorUsername = SomniCredentials.Username;
            _cfg.authorDisplayName = SomniCredentials.DisplayName ?? SomniCredentials.Username;
        }

        private bool TokenTransportOk()
        {
            if (!Uri.TryCreate(StoreUrl, UriKind.Absolute, out var uri))
            { _status = "ERROR: invalid server URL."; return false; }
            bool loopback = uri.IsLoopback || uri.Host == "localhost";
            if (uri.Scheme != Uri.UriSchemeHttps && !loopback)
            { _status = "ERROR: refusing to send your token over plain http to a remote host. Use https://"; return false; }
            return true;
        }

        /// <summary>
        /// Before building, verify everything the package's prefabs/materials/scenes reference by
        /// GUID actually lives in the folder. Anything outside would NOT ship and arrive broken in
        /// the consumer's world (the "missing particle material / sounds" bug). Detection is
        /// read-only; the offered fix moves the strays in, which is GUID-safe (Unity repoints every
        /// reference), and only ever runs when the author explicitly chooses it.
        /// Returns true if it is safe to proceed with the build, false to abort.
        /// </summary>
        private bool PreflightExternalDeps()
        {
            var missing = SomniPackageBuilder.FindExternalAssetDependencies(_folderPath);
            if (missing.Count == 0) return true;

            int DistinctCount(IEnumerable<SomniPackageBuilder.ExternalDep> d) => d.Select(m => m.asset).Distinct().Count();
            var byAsset = missing.GroupBy(m => m.asset).ToList();
            string list = string.Join("\n", byAsset.Take(20).Select(g =>
                "• " + Path.GetFileName(g.Key) + "  (used by " +
                string.Join(", ", g.Select(x => Path.GetFileName(x.referencedBy)).Distinct()) + ")"));
            if (byAsset.Count > 20) list += "\n… and " + (byAsset.Count - 20) + " more";

            int choice = EditorUtility.DisplayDialogComplex(
                "Assets outside the package folder",
                DistinctCount(missing) + " asset(s) your package references live OUTSIDE the package folder, so they " +
                "will NOT be included and will arrive broken in other people's worlds (missing materials, " +
                "textures, sounds, etc):\n\n" + list + "\n\nBring them into the package folder? Unity keeps " +
                "their IDs when moving, so your own prefabs and worlds keep working.",
                "Bring them in",   // 0
                "Cancel",          // 1
                "Publish anyway"); // 2

            if (choice == 1) { _status = "Cancelled - fix the missing assets and try again."; return false; }
            if (choice == 2) return true; // explicit author override

            // Bring them in, in waves: a moved asset can itself pull in further strays.
            string depFolder = _folderPath.TrimEnd('/') + "/Dependencies";
            var failed = new HashSet<string>();
            int totalMoved = 0;
            for (int pass = 0; pass < 12; pass++)
            {
                var wave = SomniPackageBuilder.FindExternalAssetDependencies(_folderPath)
                    .Select(m => m.asset).Distinct().Where(a => !failed.Contains(a)).ToList();
                if (wave.Count == 0) break;
                if (!AssetDatabase.IsValidFolder(depFolder))
                    AssetDatabase.CreateFolder(_folderPath.TrimEnd('/'), "Dependencies");

                int movedThisPass = 0;
                foreach (string asset in wave)
                {
                    if (asset.StartsWith("Packages/", StringComparison.Ordinal)) { failed.Add(asset); continue; }
                    string target = AssetDatabase.GenerateUniqueAssetPath(depFolder + "/" + Path.GetFileName(asset));
                    string err = AssetDatabase.MoveAsset(asset, target);
                    if (string.IsNullOrEmpty(err)) { movedThisPass++; totalMoved++; }
                    else failed.Add(asset);
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                if (movedThisPass == 0) break; // no progress - avoid looping forever
            }

            var remaining = SomniPackageBuilder.FindExternalAssetDependencies(_folderPath);
            if (remaining.Count > 0)
            {
                _status = "ERROR: brought in " + totalMoved + " asset(s), but " + DistinctCount(remaining) +
                          " could not be moved (read-only or in a Unity package). Move them into the folder " +
                          "manually, then build again.";
                return false;
            }
            _status = "Brought " + totalMoved + " referenced asset(s) into the package folder.";
            return true;
        }

        private void BuildToDisk()
        {
            SavePrefs();
            ApplyIdentityToConfig();
            _cfg.Save(_folderPath);
            AssetDatabase.SaveAssets();
            if (!PreflightExternalDeps()) return;
            try
            {
                var res = SomniPackageBuilder.Build(_folderPath, _cfg);
                string file = EditorUtility.SaveFilePanel(
                    "Save package", "", res.Manifest.id + "-" + res.Manifest.version + ".somnipkg", "somnipkg");
                if (string.IsNullOrEmpty(file)) { _status = "Cancelled."; return; }
                File.WriteAllBytes(file, res.ArchiveBytes);
                _status = "Built " + res.Manifest.id + "@" + res.Manifest.version +
                          " (" + res.Manifest.files.Count + " files)\n" + file + WarningsText(res);
            }
            catch (Exception e) { _status = "ERROR: " + e.Message; }
        }

        private void BuildAndUpload()
        {
            SavePrefs();
            if (!SomniCredentials.Verified) { _status = "ERROR: set your upload token in the Account window."; return; }
            if (!TokenTransportOk()) return;
            ApplyIdentityToConfig();
            _cfg.Save(_folderPath);
            AssetDatabase.SaveAssets();
            if (!PreflightExternalDeps()) return;
            try
            {
                var res = SomniPackageBuilder.Build(_folderPath, _cfg);
                _status = "Uploading " + res.Manifest.id + "@" + res.Manifest.version + "…";
                Repaint();

                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(60);
                    http.DefaultRequestHeaders.Add("Authorization", "Bearer " + SomniCredentials.Token);

                    var form = new MultipartFormDataContent();
                    var content = new ByteArrayContent(res.ArchiveBytes);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
                    form.Add(content, "file", res.Manifest.id + ".somnipkg");

                    if (_screenshot != null)
                    {
                        byte[] img = PrepareScreenshot(_screenshot, out string mime);
                        if (img == null) { _status = "ERROR: couldn't read the screenshot image."; return; }
                        var imgContent = new ByteArrayContent(img);
                        imgContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
                        form.Add(imgContent, "image", "screenshot" + (mime == "image/jpeg" ? ".jpg" : ".png"));
                    }

                    var resp = http.PostAsync(StoreUrl.TrimEnd('/') + "/api/packages", form).Result;
                    string body = resp.Content.ReadAsStringAsync().Result;
                    if ((int)resp.StatusCode == 201)
                    {
                        var r = JsonUtility.FromJson<UploadResult>(body);
                        string flags = (r.scanFlags != null && r.scanFlags.Length > 0)
                            ? "\nFlagged APIs: " + string.Join(", ", System.Array.ConvertAll(r.scanFlags, f => f.category))
                            : "";
                        _status = res.Manifest.id + "@" + res.Manifest.version + " uploaded.\nStatus: " +
                                  r.status + " — " + r.note + flags + WarningsText(res);
                    }
                    else _status = "ERROR: server returned " + (int)resp.StatusCode + "\n" + body;
                }
            }
            catch (Exception e) { _status = "ERROR: " + e.Message; }
        }

        /// <summary>Render the current Scene view camera to a PNG, save it, and use it as the screenshot.</summary>
        private void CaptureFromScene()
        {
            if (string.IsNullOrEmpty(_folderPath))
            {
                _status = "ERROR: pick your package folder first.";
                return;
            }
            var sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
            {
                _status = "ERROR: open a Scene view (Window > General > Scene), frame your asset, then capture.";
                return;
            }
            var cam = sv.camera;
            int w = Mathf.Clamp((int)cam.pixelWidth, 16, 2560);
            int h = Mathf.Clamp((int)cam.pixelHeight, 16, 1440);

            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render(); // scene only — editor grid/gizmos are not included
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            rt.Release();
            DestroyImmediate(rt);

            byte[] png = tex.EncodeToPNG();
            DestroyImmediate(tex);

            // saved INSIDE the package folder (its own preview); the builder excludes it from the .somnipkg
            string file = _folderPath.TrimEnd('/') + "/" + SomniPackageConfig.ScreenshotName;
            File.WriteAllBytes(file, png);
            AssetDatabase.ImportAsset(file, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            _screenshot = AssetDatabase.LoadAssetAtPath<Texture2D>(file);
            _cfg.screenshotPath = file;
            _cfg.Save(_folderPath);
            if (_screenshot != null) EditorGUIUtility.PingObject(_screenshot); // highlight it in the Project window
            _status = "Captured Scene view (" + w + "x" + h + ")  ->  " + file;
        }

        /// <summary>Decode a screenshot, resize to fit the max dimension, encode png (jpg if too big).</summary>
        private static byte[] PrepareScreenshot(Texture2D srcAsset, out string mime)
        {
            mime = "image/png";
            string path = AssetDatabase.GetAssetPath(srcAsset);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(path))) return null;

            int max = SomniPackageConfig.ScreenshotMaxDim;
            Texture2D outTex = tex;
            if (tex.width > max || tex.height > max)
            {
                float scale = Mathf.Min((float)max / tex.width, (float)max / tex.height);
                int tw = Mathf.Max(1, Mathf.RoundToInt(tex.width * scale));
                int th = Mathf.Max(1, Mathf.RoundToInt(tex.height * scale));
                var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32);
                var prevActive = RenderTexture.active;
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                outTex = new Texture2D(tw, th, TextureFormat.RGBA32, false);
                outTex.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
                outTex.Apply();
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }

            byte[] png = outTex.EncodeToPNG();
            byte[] result = png.Length <= SomniPackageConfig.ScreenshotMaxBytes ? png : outTex.EncodeToJPG(85);
            mime = png.Length <= SomniPackageConfig.ScreenshotMaxBytes ? "image/png" : "image/jpeg";
            // free the decoded/resized textures so repeated uploads do not leak native memory
            // until the next domain reload (CaptureFromScene already does this for its texture).
            if (outTex != tex) UnityEngine.Object.DestroyImmediate(outTex);
            UnityEngine.Object.DestroyImmediate(tex);
            return result;
        }

        private static string WarningsText(SomniPackageBuilder.BuildResult res)
        {
            return res.Warnings.Count > 0 ? "\nWarnings:\n- " + string.Join("\n- ", res.Warnings) : "";
        }

        [Serializable] private class UploadResult { public string status; public string note; public ScanFlag[] scanFlags; }
        [Serializable] private class ScanFlag { public string file; public string category; public string match; public string severity; }
    }
}
#endif
