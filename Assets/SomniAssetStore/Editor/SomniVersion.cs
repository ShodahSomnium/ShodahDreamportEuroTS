// SomniAssetStore - editor tools version + update check.
// The tools embed their own version and ask the store for the latest, so an installed
// copy can tell the user when a newer package is available on the website.
//
// The check runs on its own: once when the editor loads/recompiles and again whenever a
// Somni window gains focus, throttled and off the UI thread so it never blocks the editor.
// Results are cached in SessionState so the "update available" banner survives domain reloads
// without re-fetching.
#if UNITY_EDITOR
using System;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SomniAssetStore.Shared
{
    public static class SomniVersion
    {
        // Bump this on every SomniAssetStore.unitypackage export; keep it in step with the
        // server's config.toolsVersion so "up to date" is accurate.
        public const string Current = "1.2.18";

        public static string Latest { get; private set; }
        public static string Notes { get; private set; }
        public static string Signature { get; private set; } // base64 RSA-SHA256 of the tools package
        public static bool Checked { get; private set; }
        public static bool UpdateAvailable { get; private set; }

        public static string DownloadUrl => SomniConfig.StoreUrl.TrimEnd('/') + "/SomniAssetStore.unitypackage";
        private static string VersionUrl => SomniConfig.StoreUrl.TrimEnd('/') + "/api/tools/version";

        // Don't hit the server more than this often for the automatic (load / focus) checks.
        private static readonly TimeSpan AutoThrottle = TimeSpan.FromMinutes(10);

        // SessionState keys: survive domain reloads (recompiles) but reset when the editor restarts,
        // so we re-check once per editor session and then throttle within it.
        private const string KeyLast = "Somni.Ver.LastTicks";
        private const string KeyLatest = "Somni.Ver.Latest";
        private const string KeyNotes = "Somni.Ver.Notes";
        private const string KeySig = "Somni.Ver.Sig";

        [Serializable]
        private class VersionResp { public string version; public string notes; public string signature; }

        // Background-fetch handoff: the worker thread only writes _pending; the main-thread Pump reads it.
        private static volatile bool _fetching;
        private static volatile string _pending;

        [InitializeOnLoadMethod]
        private static void OnEditorLoad()
        {
            LoadCached();                       // restore last-known result so the banner is correct immediately
            EditorApplication.update += Pump;   // main-thread pump that applies background results
            MaybeCheck();                       // refresh if the cached result is stale
        }

        /// <summary>
        /// Kick an automatic check if enough time has passed. Non-blocking: the HTTP call runs on a
        /// thread-pool thread and the result is applied on the main thread by Pump.
        /// </summary>
        public static void MaybeCheck(bool force = false)
        {
            if (_fetching) return;
            if (!force && DateTime.UtcNow - LastCheck < AutoThrottle) return;
            _fetching = true;
            _pending = null;
            string url = VersionUrl;
            Task.Run(() =>
            {
                try
                {
                    using (var http = new HttpClient())
                    {
                        http.Timeout = TimeSpan.FromSeconds(10);
                        _pending = http.GetStringAsync(url).Result ?? "";
                    }
                }
                catch { _pending = ""; } // empty = failed; Pump treats it as "no change"
            });
        }

        /// <summary>Retained for existing callers (windows call this in OnEnable). Now a throttled auto-check.</summary>
        public static void EnsureChecked() => MaybeCheck();

        /// <summary>
        /// Synchronous check for explicit user actions (the Tools "Check" button and the self-update,
        /// which needs a fresh signature right before downloading). Blocks briefly by design.
        /// </summary>
        public static void Check()
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(10);
                    Apply(http.GetStringAsync(VersionUrl).Result);
                }
            }
            catch { /* offline or unreachable: just don't show a banner */ }
            Checked = true;
            LastCheck = DateTime.UtcNow;
        }

        /// <summary>Main-thread pump: apply a completed background fetch (all Unity API calls stay here).</summary>
        private static void Pump()
        {
            if (!_fetching || _pending == null) return;
            string json = _pending;
            _pending = null;
            _fetching = false;
            LastCheck = DateTime.UtcNow;
            if (string.IsNullOrEmpty(json)) return; // failed fetch: keep the cached state
            if (Apply(json)) RepaintSomniWindows();
        }

        /// <summary>Parse a version response, update state + cache. Returns true on a successful parse.</summary>
        private static bool Apply(string json)
        {
            try
            {
                var r = JsonUtility.FromJson<VersionResp>(json);
                if (r == null || string.IsNullOrEmpty(r.version)) return false;
                Latest = r.version;
                Notes = r.notes;
                Signature = r.signature;
                Checked = true;
                UpdateAvailable = CompareVersions(Latest, Current) > 0;
                SessionState.SetString(KeyLatest, Latest ?? "");
                SessionState.SetString(KeyNotes, Notes ?? "");
                SessionState.SetString(KeySig, Signature ?? "");
                return true;
            }
            catch { return false; }
        }

        private static void LoadCached()
        {
            string latest = SessionState.GetString(KeyLatest, "");
            if (string.IsNullOrEmpty(latest)) return;
            Latest = latest;
            Notes = SessionState.GetString(KeyNotes, "");
            Signature = NullIfEmpty(SessionState.GetString(KeySig, ""));
            Checked = true;
            UpdateAvailable = CompareVersions(Latest, Current) > 0;
        }

        private static DateTime LastCheck
        {
            get => long.TryParse(SessionState.GetString(KeyLast, "0"), out long t)
                ? new DateTime(t, DateTimeKind.Utc) : DateTime.MinValue;
            set => SessionState.SetString(KeyLast, value.Ticks.ToString());
        }

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        private static void RepaintSomniWindows()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                string n = w.GetType().FullName;
                if (n != null && n.StartsWith("SomniAssetStore.", StringComparison.Ordinal)) w.Repaint();
            }
        }

        /// <summary>&gt;0 if a is newer than b. Numeric per dotted segment, missing = 0.</summary>
        public static int CompareVersions(string a, string b)
        {
            string[] pa = (a ?? "0").Split('.');
            string[] pb = (b ?? "0").Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int va = i < pa.Length && int.TryParse(pa[i], out int x) ? x : 0;
                int vb = i < pb.Length && int.TryParse(pb[i], out int y) ? y : 0;
                if (va != vb) return va - vb;
            }
            return 0;
        }

        /// <summary>Subtle right-aligned version line. Windows call this at the end of OnGUI.</summary>
        public static void DrawFooter()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var s = new GUIStyle(EditorStyles.miniLabel);
            s.normal.textColor = SomniStyle.TextMute;
            GUILayout.Label("SomniAssetStore  v" + Current, s);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Draw an "update available" bar. Windows call this at the top of OnGUI.</summary>
        public static void DrawUpdateBanner()
        {
            if (!UpdateAvailable) return;
            var box = new GUIStyle(EditorStyles.helpBox)
            {
                normal = { background = SomniStyle.BannerBg },
                border = new RectOffset(6, 6, 6, 6),
            };
            EditorGUILayout.BeginHorizontal(box);
            var msg = new GUIStyle(EditorStyles.label) { wordWrap = true };
            msg.normal.textColor = SomniStyle.Amber;
            GUILayout.Label("Update available: v" + Latest + "  (you have v" + Current + ")", msg);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Update", SomniStyle.SecondaryBtn, GUILayout.Width(96)))
                SomniAssetStore.Tools.SomniToolsWindow.OpenAndUpdate();
            EditorGUILayout.EndHorizontal();
        }
    }
}
#endif
