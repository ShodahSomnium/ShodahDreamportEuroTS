// SomniAssetStore - Somni Tools.
// One hub for everything that is not browsing/publishing: sign-in (upload token),
// managing your own published packages, and updating the SomniAssetStore tools
// themselves. The Store and Publisher stay as their own windows.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using SomniAssetStore.Shared;
using UnityEditor;
using UnityEngine;

namespace SomniAssetStore.Tools
{
    public class SomniToolsWindow : EditorWindow
    {
        [Serializable] private class MyPkg
        {
            public string id, version, name, description, status, createdAt, archiveSha, signature;
            public int downloads;
        }
        [Serializable] private class MyPkgResponse { public List<MyPkg> packages = new List<MyPkg>(); }

        private class Group
        {
            public string id, name, description, status, latestVersion;
            public int versions, downloads;
        }

        private const string StoreUrl = SomniAssetStore.Importer.SomniStoreClient.DefaultServerUrl;
        private string _token = "";
        private List<MyPkg> _packages;
        private string _status = "";
        private bool _statusError;
        private Vector2 _scroll;

        [MenuItem("SomniAssetStore/Somni Tools")]
        public static void Open()
        {
            var w = GetWindow<SomniToolsWindow>("Somni Tools");
            w.minSize = new Vector2(520, 460);
            w._token = SomniCredentials.Token;
            SomniCredentials.EnsureResolved();
            SomniVersion.EnsureChecked();
            if (SomniCredentials.Verified) w.Load();
        }

        /// <summary>Open the Tools window and immediately run the signed self-update (from the update banner).</summary>
        public static void OpenAndUpdate()
        {
            var w = GetWindow<SomniToolsWindow>("Somni Tools");
            w.minSize = new Vector2(520, 460);
            w._token = SomniCredentials.Token;
            SomniCredentials.EnsureResolved();
            w.Focus();
            w.UpdateTools();
        }

        private void OnEnable()
        {
            _token = SomniCredentials.Token;
            SomniCredentials.EnsureResolved();
            SomniVersion.EnsureChecked();
            if (SomniCredentials.Verified) Load();
        }

        private void OnFocus() => SomniVersion.MaybeCheck();

        private void OnGUI()
        {
            SomniStyle.Init();
            SomniStyle.FillBackground(position.width, position.height);
            DrawHeader();
            SomniVersion.DrawUpdateBanner();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawToolsCard();
            DrawAccountCard();

            if (SomniCredentials.Verified)
            {
                GUILayout.Label("MY PACKAGES", SomniStyle.SectionLabel);
                if (_packages != null && _packages.Count == 0)
                    EditorGUILayout.HelpBox("You haven't published anything yet.", MessageType.Info);
                if (_packages != null)
                    foreach (var g in GroupPackages()) DrawPackage(g);
            }

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, _statusError ? MessageType.Error : MessageType.Info);

            EditorGUILayout.EndScrollView();
            SomniVersion.DrawFooter();
        }

        // ---------------------------------------------------------------- update

        private void DrawToolsCard()
        {
            EditorGUILayout.BeginVertical(SomniStyle.Card);
            GUILayout.Label("SOMNI TOOLS", SomniStyle.SectionLabel);
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            string state = SomniVersion.UpdateAvailable
                ? "Installed v" + SomniVersion.Current + "   ·   v" + SomniVersion.Latest + " available"
                : "Installed v" + SomniVersion.Current + (SomniVersion.Checked ? "   ·   up to date" : "");
            GUILayout.Label(state, SomniStyle.Meta);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Check", SomniStyle.SecondaryBtn, GUILayout.Width(64)))
            {
                SomniVersion.Check();
                SetStatus(SomniVersion.UpdateAvailable
                    ? "Update available: v" + SomniVersion.Latest + "."
                    : "You're on the latest version (v" + SomniVersion.Current + ").", false);
            }
            if (SomniVersion.UpdateAvailable)
            {
                if (GUILayout.Button("Update Somni Tools", SomniStyle.PrimaryBtn, GUILayout.Width(160)))
                    UpdateTools();
            }
            EditorGUILayout.EndHorizontal();
            if (SomniVersion.UpdateAvailable && !string.IsNullOrEmpty(SomniVersion.Notes))
                GUILayout.Label(SomniVersion.Notes, SomniStyle.Subtitle);
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Download the latest tools package, VERIFY its RSA signature against the embedded public
        /// key (fail-closed, same trust as package installs), then hand it to Unity's importer.
        /// </summary>
        private void UpdateTools()
        {
            try
            {
                SomniVersion.Check(); // refresh version + signature just before downloading
                if (string.IsNullOrEmpty(SomniVersion.Signature))
                {
                    SetStatus("Update aborted: the store did not provide a signature for the tools package.", true);
                    return;
                }
                byte[] data;
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(120);
                    data = http.GetByteArrayAsync(SomniVersion.DownloadUrl).Result;
                }
                if (!Importer.SomniSigning.VerifyArchive(data, SomniVersion.Signature))
                {
                    SetStatus("Update aborted: the download's signature is INVALID (tampered or wrong key). Not imported.", true);
                    return;
                }
                string tmp = Path.Combine(Path.GetTempPath(), "SomniAssetStore-latest.unitypackage");
                File.WriteAllBytes(tmp, data);
                // interactive: Unity shows its import dialog so the user confirms overwriting the tools.
                AssetDatabase.ImportPackage(tmp, true);
                SetStatus("Verified v" + SomniVersion.Latest + ". Confirm the import dialog to finish updating.", false);
            }
            catch (Exception e) { SetStatus("Update failed: " + e.Message, true); }
        }

        // ---------------------------------------------------------------- account

        private void DrawAccountCard()
        {
            EditorGUILayout.BeginVertical(SomniStyle.Card);
            GUILayout.Label("ACCOUNT", SomniStyle.SectionLabel);
            EditorGUILayout.Space(2);
            GUILayout.Label(
                "Sign in on somni.world, generate an upload token, and paste it here once. The " +
                "Publisher and My Packages both use it. Browsing and installing never need a token.",
                SomniStyle.Subtitle);
            EditorGUILayout.Space(4);

            _token = EditorGUILayout.PasswordField("Token", _token);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_token)))
                if (GUILayout.Button("Save & verify", SomniStyle.PrimaryBtn, GUILayout.Width(120)))
                {
                    SomniCredentials.SetToken(_token);
                    if (SomniCredentials.Resolve())
                    {
                        SetStatus("Signed in as " + SomniCredentials.DisplayName + ".", false);
                        Load();
                    }
                    else SetStatus(SomniCredentials.Error, true);
                }
            if (SomniCredentials.HasToken)
                if (GUILayout.Button("Sign out", SomniStyle.SecondaryBtn, GUILayout.Width(80)))
                {
                    SomniCredentials.SignOut();
                    _token = "";
                    _packages = null;
                    SetStatus("Signed out.", false);
                }
            EditorGUILayout.EndHorizontal();

            if (SomniCredentials.Verified)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Signed in as", SomniStyle.SectionLabel, GUILayout.Width(84));
                GUILayout.Label(SomniCredentials.DisplayName + "  (@" + SomniCredentials.Username + ")", EditorStyles.boldLabel);
                if (!SomniCredentials.IsDev) SomniStyle.Chip("verified", SomniStyle.Green);
                else SomniStyle.Chip("dev token", SomniStyle.Amber);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Refresh", SomniStyle.SecondaryBtn, GUILayout.Width(72))) Load();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
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
            GUILayout.Label("Somni Tools", SomniStyle.Title);
            GUILayout.Label("Account, your packages, and tool updates", SomniStyle.Subtitle);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // ---------------------------------------------------------------- packages

        private void DrawPackage(Group g)
        {
            EditorGUILayout.BeginVertical(SomniStyle.Card);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(g.name, SomniStyle.CardTitle);
            GUILayout.Space(6);
            SomniStyle.Chip("v" + g.latestVersion, SomniStyle.Accent);
            SomniStyle.StatusChip(g.status);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Edit & Update", SomniStyle.PrimaryBtn, GUILayout.Width(120)))
                EditAndUpdate(g);
            if (GUILayout.Button("Delete", SomniStyle.DangerBtn, GUILayout.Width(70)))
                Delete(g.id);
            EditorGUILayout.EndHorizontal();

            GUILayout.Label(g.id + "   ·   " + g.downloads + " installs   ·   " + g.versions +
                            " version" + (g.versions == 1 ? "" : "s"), SomniStyle.Meta);
            if (!string.IsNullOrEmpty(g.description))
                GUILayout.Label(g.description, SomniStyle.Desc);
            EditorGUILayout.EndVertical();
        }

        private List<Group> GroupPackages()
        {
            var map = new Dictionary<string, Group>();
            var order = new List<string>();
            foreach (var p in _packages)
            {
                if (!map.TryGetValue(p.id, out var g))
                {
                    g = new Group { id = p.id, name = p.name, description = p.description,
                                    status = p.status, latestVersion = p.version };
                    map[p.id] = g; order.Add(p.id);
                }
                g.versions++;
                g.downloads += p.downloads;
            }
            var list = new List<Group>();
            foreach (var id in order) list.Add(map[id]);
            return list;
        }

        private void Load()
        {
            try
            {
                string body = Send(HttpMethod.Get, "/api/my/packages", null);
                _packages = JsonUtility.FromJson<MyPkgResponse>(body).packages;
                SetStatus(_packages.Count + " package(s).", false);
            }
            catch (Exception e) { _packages = null; SetStatus("ERROR: " + e.Message, true); }
        }

        /// <summary>
        /// Edit a published package. If its editable source is already in this project (an author's
        /// working copy, identified by its .somnipackage.json), open the Publisher on THAT and never
        /// touch the files — the local copy is the source of truth. Only when no local source exists
        /// do we offer to pull the published version down to edit.
        /// </summary>
        private void EditAndUpdate(Group g)
        {
            string local = FindLocalPackageFolder(g.id);
            if (local != null)
            {
                Publisher.SomniPublisherWindow.OpenForFolder(local);
                SetStatus("Opened your local copy of " + g.id + " (" + local +
                          "). Your files are untouched — edit, bump the version, and upload.", false);
                return;
            }
            PullForEdit(g);
        }

        /// <summary>The editable source folder for a package id in this project, or null. Identified by
        /// its .somnipackage.json (which only an author's source has; installed copies never ship it).</summary>
        private static string FindLocalPackageFolder(string id)
        {
            if (!Directory.Exists("Assets")) return null;
            foreach (string cfgPath in Directory.GetFiles("Assets", Publisher.SomniPackageConfig.FileName,
                         SearchOption.AllDirectories))
            {
                if (Path.GetFileName(cfgPath) != Publisher.SomniPackageConfig.FileName) continue;
                try
                {
                    string dir = Path.GetDirectoryName(cfgPath).Replace('\\', '/');
                    var cfg = Publisher.SomniPackageConfig.Load(dir);
                    if (cfg != null && cfg.id == id) return dir;
                }
                catch { /* unreadable config, skip */ }
            }
            return null;
        }

        /// <summary>
        /// No local source exists, so pull the published version into a project folder and open the
        /// Publisher on it. Guarded by a clear overwrite warning if the target folder is occupied.
        /// </summary>
        private void PullForEdit(Group g)
        {
            var latest = _packages.Find(p => p.id == g.id && p.version == g.latestVersion);
            if (latest == null) { SetStatus("ERROR: version data missing; reload.", true); return; }

            string shortId = g.id.Substring(g.id.LastIndexOf('.') + 1);
            string folder = "Assets/#User/" + shortId;
            if (Directory.Exists(folder))
            {
                if (!EditorUtility.DisplayDialog("Pull " + g.id + "?",
                    "This replaces the contents of " + folder + " with the store version of v" +
                    g.latestVersion + " so you can edit it and publish an update.\n\nContinue?",
                    "Pull & overwrite", "Cancel")) return;
                AssetDatabase.DeleteAsset(folder);
            }

            try
            {
                byte[] archive = Importer.SomniStoreClient.DownloadPackage(
                    g.id, g.latestVersion, latest.archiveSha, latest.signature);
                Directory.CreateDirectory(folder);
                ExtractForEdit(archive, folder);
                AssetDatabase.Refresh();
                Publisher.SomniPublisherWindow.OpenForFolder(folder);
                SetStatus("Pulled " + g.id + " v" + g.latestVersion + " into " + folder +
                          ". Edit the assets, bump the version in the Publisher, and upload your update.", false);
            }
            catch (Exception e) { SetStatus("ERROR: " + e.Message, true); }
        }

        /// <summary>Extract files/ verbatim and rebuild the .somnipackage.json config from the manifest.</summary>
        private static void ExtractForEdit(byte[] archive, string folder)
        {
            SomniManifest manifest = null;
            using (var ms = new MemoryStream(archive))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName == "manifest.json")
                    {
                        using (var reader = new StreamReader(entry.Open()))
                            manifest = JsonUtility.FromJson<SomniManifest>(reader.ReadToEnd());
                        continue;
                    }
                    if (!entry.FullName.StartsWith("files/") || entry.FullName.EndsWith("/")) continue;
                    string rel = entry.FullName.Substring("files/".Length);
                    // Zip-slip guard: reject traversing/absolute names and confirm the write stays in the folder.
                    if (!Importer.SomniPackageInstaller.IsSafeRelPath(rel)) continue;
                    string dest = folder + "/" + rel;
                    string rootFull = Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/') + "/";
                    string destFull = Path.GetFullPath(dest).Replace('\\', '/');
                    if (!destFull.StartsWith(rootFull, StringComparison.Ordinal)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (var es = entry.Open())
                    using (var fs = File.Create(dest))
                        es.CopyTo(fs);
                }
            }
            if (manifest == null) return;
            var cfg = new Publisher.SomniPackageConfig
            {
                id = manifest.id,
                name = manifest.name,
                version = manifest.version,
                authorUsername = manifest.author != null ? manifest.author.username : "",
                authorDisplayName = manifest.author != null ? manifest.author.displayName : "",
                description = manifest.description,
                category = string.IsNullOrEmpty(manifest.category) ? "other" : manifest.category,
                sourceNamespace = manifest.sourceNamespace,
            };
            if (manifest.dependencies != null)
                foreach (var d in manifest.dependencies)
                    cfg.dependencies.Add(new Publisher.SomniPackageConfig.Dep { id = d.id, versionRange = d.version });
            cfg.Save(folder);
        }

        private void Delete(string id)
        {
            if (!EditorUtility.DisplayDialog("Delete " + id + "?",
                "Delete this package and ALL its versions from the store? This cannot be undone.",
                "Delete", "Cancel")) return;
            try
            {
                Send(new HttpMethod("DELETE"), "/api/packages/" + Uri.EscapeDataString(id), null);
                Load();
            }
            catch (Exception e) { SetStatus("ERROR: " + e.Message, true); }
        }

        private string Send(HttpMethod method, string path, string jsonBody)
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(30);
                var req = new HttpRequestMessage(method, StoreUrl.TrimEnd('/') + path);
                req.Headers.Add("Authorization", "Bearer " + SomniCredentials.Token);
                if (jsonBody != null)
                    req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
                var resp = http.SendAsync(req).Result;
                string body = resp.Content.ReadAsStringAsync().Result;
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("HTTP " + (int)resp.StatusCode + " " + body);
                return body;
            }
        }

        private void SetStatus(string m, bool err) { _status = m; _statusError = err; }
    }
}
#endif
