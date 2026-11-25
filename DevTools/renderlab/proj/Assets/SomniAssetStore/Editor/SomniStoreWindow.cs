// SomniAssetStore - in-editor store browser.
// Browse approved packages, install into the current world, update and uninstall.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using SomniAssetStore.Shared;
using UnityEditor;
using UnityEngine;

namespace SomniAssetStore.Importer
{
    public class SomniStoreWindow : EditorWindow
    {
        private WorldInfo _world;
        private List<StoreListing> _listings = new List<StoreListing>();
        private InstalledLock _installed = new InstalledLock();   // packages baked into the world
        private InstalledLock _standalone = new InstalledLock();  // avatar/editor/etc. installed neutrally
        private string _status = "";
        private bool _statusError;
        private Vector2 _scroll;
        private string _search = "";

        // package "id@version" -> screenshot texture (lazy-loaded, cached)
        private readonly Dictionary<string, Texture2D> _thumbs = new Dictionary<string, Texture2D>();

        // package ids whose full description is expanded (default collapsed to a short preview)
        private readonly HashSet<string> _expanded = new HashSet<string>();
        private GUIStyle _linkStyle;

        [MenuItem("SomniAssetStore/Somni Asset Store")]
        public static void Open()
        {
            var w = GetWindow<SomniStoreWindow>("Somni Store");
            w.minSize = new Vector2(520, 460);
        }

        private void OnEnable()
        {
            _world = SomniPackageInstaller.DetectWorld();
            ReloadInstalled();
            SomniVersion.EnsureChecked();
            wantsMouseMove = true; // so the star hover updates live
        }

        /// <summary>Refresh both install locks (world + standalone) from disk.</summary>
        private void ReloadInstalled()
        {
            _installed = _world != null ? SomniPackageInstaller.ReadLock(_world) : new InstalledLock();
            _standalone = SomniPackageInstaller.ReadStandaloneLock();
        }

        /// <summary>The install record for a package, from whichever lock holds it (or null).</summary>
        private InstalledPackage FindInstalled(string id) =>
            _installed.packages.FirstOrDefault(p => p.id == id) ??
            _standalone.packages.FirstOrDefault(p => p.id == id);

        /// <summary>The authoritative (server listing) category for a package id, or null if unknown.</summary>
        private string CategoryForId(string id)
        {
            var l = _listings.FirstOrDefault(p => p.id == id);
            return l != null ? l.category : null;
        }

        /// <summary>All installed records across both locks (for dependency resolution).</summary>
        private InstalledLock CombinedInstalled()
        {
            var all = new InstalledLock();
            all.packages.AddRange(_installed.packages);
            all.packages.AddRange(_standalone.packages);
            return all;
        }

        private void OnFocus() => SomniVersion.MaybeCheck();

        private void OnGUI()
        {
            if (Event.current.type == EventType.MouseMove) Repaint();
            SomniStyle.Init();
            SomniStyle.FillBackground(position.width, position.height);
            DrawHeader();
            SomniVersion.DrawUpdateBanner();
            DrawToolbar();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space(2);

            var visible = _listings.Where(Matches).ToList();
            if (_listings.Count == 0)
                DrawEmpty("No packages loaded.", "Press Refresh to load the catalogue (is the server running?).");
            else if (visible.Count == 0)
                DrawEmpty("No matches.", "Nothing matches your search.");
            else
                foreach (var pkg in visible) DrawPackageCard(pkg);

            EditorGUILayout.EndScrollView();
            DrawStatusBar();
            SomniVersion.DrawFooter();
        }

        // ---------------------------------------------------------------- header

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(SomniStyle.HeaderBar);
            EditorGUILayout.BeginHorizontal();
            DrawLogo();
            GUILayout.Space(8);
            EditorGUILayout.BeginVertical();
            GUILayout.Label("Somni Asset Store", SomniStyle.Title);
            GUILayout.Label("Community assets for Somnium Space worlds", SomniStyle.Subtitle);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawLogo()
        {
            Rect r = GUILayoutUtility.GetRect(26, 26, GUILayout.Width(26), GUILayout.Height(26));
            GUI.DrawTexture(r, SomniStyle.Logo, ScaleMode.ScaleToFit, true);
            var s = new GUIStyle(EditorStyles.boldLabel)
            { alignment = TextAnchor.MiddleCenter, fontSize = 15 };
            s.normal.textColor = Color.white;
            GUI.Label(r, "S", s);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();
            // world state — a dropdown so people who moved their world asmdef (or have more than
            // one world) can pin exactly which one packages install into. Shows the SHORT world
            // name (everyone's starts with SomniumSpace.Worlds., no information in repeating it).
            GUILayout.Label("World: ", SomniStyle.Meta, GUILayout.Width(42));
            bool pinned = !string.IsNullOrEmpty(SomniPackageInstaller.GetWorldOverrideGuid());
            string shortName = _world != null
                ? (_world.rootNamespace.StartsWith("SomniumSpace.Worlds.")
                    ? _world.rootNamespace.Substring("SomniumSpace.Worlds.".Length)
                    : _world.rootNamespace)
                : null;
            string label = _world != null ? (pinned ? "● " : "") + shortName : "None — choose…";
            string tip = _world != null
                ? (pinned ? "Pinned manually. " : "Auto-detected. ") + _world.rootNamespace +
                  "\nInstalls to " + SomniPackageInstaller.InstallRootFor(_world) + ".\nClick to change or re-detect."
                : "No SomniumSpace.Worlds.* asmdef found. Click to choose one, or drag your world's .asmdef into the field.";
            if (GUILayout.Button(new GUIContent(label + "  ▾", tip), SomniStyle.SecondaryBtn))
                ShowWorldMenu();

            // Always-visible drag-and-drop slot: shows the resolved world's .asmdef, and dropping
            // a different one pins it directly (covers projects the auto-scan doesn't recognise).
            var currentAsm = _world != null && !string.IsNullOrEmpty(_world.asmdefGuid)
                ? AssetDatabase.LoadAssetAtPath<UnityEditorInternal.AssemblyDefinitionAsset>(
                      AssetDatabase.GUIDToAssetPath(_world.asmdefGuid))
                : null;
            var picked = (UnityEditorInternal.AssemblyDefinitionAsset)EditorGUILayout.ObjectField(
                currentAsm, typeof(UnityEditorInternal.AssemblyDefinitionAsset), false,
                GUILayout.Width(_world == null ? 220 : 160));
            if (picked != null && picked != currentAsm)
                PinAsmdef(AssetDatabase.GetAssetPath(picked));
            if (_world == null)
                GUILayout.Label("← drop your world's .asmdef", SomniStyle.Meta);

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", SomniStyle.PrimaryBtn, GUILayout.Width(80))) RefreshList();
            EditorGUILayout.EndHorizontal();

            // A world is present but its asmdef root isn't the SomniumSpace.Worlds.* form Somnium
            // requires (Benjamin's case). Rather than let the install fail with a cryptic error,
            // explain it and offer a one-click fix that sets the Root Namespace for them.
            if (_world != null && !SomniPackageInstaller.WorldRootValid(_world))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal(SomniStyle.Card);
                string suggested = SomniPackageInstaller.SuggestWorldRoot(_world);
                GUILayout.Label("This world's Root Namespace is '" +
                    (string.IsNullOrEmpty(_world.rootNamespace) ? "(empty)" : _world.rootNamespace) +
                    "'. Somnium needs 'SomniumSpace.Worlds.*' before packages can install.",
                    SomniStyle.Meta);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Fix world namespace",
                        "Sets the Root Namespace on your world asmdef to '" + suggested + "'."),
                        SomniStyle.PrimaryBtn, GUILayout.Width(150)))
                {
                    try
                    {
                        string applied = SomniPackageInstaller.FixWorldRoot(_world.asmdefGuid);
                        _world = SomniPackageInstaller.DetectWorld();
                        SetStatus("World Root Namespace set to '" + applied + "'. You can install now.", false);
                    }
                    catch (Exception e) { SetStatus("Couldn't fix the world namespace: " + e.Message, true); }
                }
                EditorGUILayout.EndHorizontal();
            }

            // search (with a ghost placeholder — IMGUI has none built in)
            EditorGUILayout.Space(4);
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
            if (string.IsNullOrEmpty(_search) && Event.current.type == EventType.Repaint)
            {
                Rect sr = GUILayoutUtility.GetLastRect();
                sr.x += 16; // clear the search icon
                GUI.Label(sr, "Search packages…", SomniStyle.Meta);
            }

            SomniStyle.Divider1();
            EditorGUILayout.Space(4);
        }

        /// <summary>
        /// World picker: "Auto-detect" plus every SomniumSpace.Worlds.* asmdef in the project.
        /// Picking one pins it (survives moves/renames, stored per project); Auto-detect clears
        /// the pin. Re-detect is just choosing Auto-detect again.
        /// </summary>
        private void ShowWorldMenu()
        {
            var menu = new GenericMenu();
            string overrideGuid = SomniPackageInstaller.GetWorldOverrideGuid();
            bool auto = string.IsNullOrEmpty(overrideGuid);

            menu.AddItem(new GUIContent("Auto-detect"), auto, () =>
            {
                SomniPackageInstaller.SetWorldOverride(null);
                _world = SomniPackageInstaller.DetectWorld();
                ReloadInstalled();
                SetStatus(_world != null
                    ? "Auto-detect  ->  " + _world.rootNamespace
                    : "No SomniumSpace.Worlds.* asmdef found under Assets.", _world == null);
            });

            var worlds = SomniPackageInstaller.DetectWorlds();
            if (worlds.Count > 0) menu.AddSeparator("");
            foreach (var w in worlds)
            {
                var wl = w; // capture per iteration
                bool isPinned = !auto && overrideGuid == wl.asmdefGuid;
                menu.AddItem(new GUIContent(wl.rootNamespace + "   (" + wl.folder + ")"), isPinned, () =>
                {
                    SomniPackageInstaller.SetWorldOverride(wl.asmdefGuid);
                    _world = wl;
                    ReloadInstalled();
                    SetStatus("World pinned: " + wl.rootNamespace + "  ->  installs to " +
                              SomniPackageInstaller.InstallRootFor(wl), false);
                });
            }
            if (worlds.Count == 0)
                menu.AddDisabledItem(new GUIContent("No world asmdefs found — drop yours in the field to the right"));
            menu.ShowAsContext();
        }

        /// <summary>
        /// Pin an explicitly chosen asmdef as the world (drag-and-drop path). Accepts any asmdef —
        /// the user knows their project better than the scan — but warns when its root doesn't
        /// look like a Somnium world, since the SDK will reject non-conforming roots at upload.
        /// </summary>
        private void PinAsmdef(string path)
        {
            var w = SomniPackageInstaller.WorldFromAsmdefLoose(path);
            if (w == null)
            {
                SetStatus("That asmdef has no usable name/rootNamespace (or is an installed package's).", true);
                return;
            }
            SomniPackageInstaller.SetWorldOverride(w.asmdefGuid);
            _world = w;
            ReloadInstalled();
            if (!w.rootNamespace.StartsWith("SomniumSpace.Worlds."))
                SetStatus("Pinned " + w.rootNamespace + " — note: its root doesn't start with " +
                          "SomniumSpace.Worlds., which Somnium requires for world uploads.", true);
            else
                SetStatus("World pinned: " + w.rootNamespace + "  ->  installs to " +
                          SomniPackageInstaller.InstallRootFor(w), false);
        }

        // ---------------------------------------------------------------- cards

        private void DrawPackageCard(StoreListing pkg)
        {
            var installedRec = FindInstalled(pkg.id);
            bool updateAvailable = installedRec != null &&
                SomniPackageInstaller.CompareVersions(pkg.version, installedRec.version) > 0;

            EditorGUILayout.BeginVertical(SomniStyle.Card);
            EditorGUILayout.BeginHorizontal();

            DrawThumb(pkg);
            GUILayout.Space(12);

            EditorGUILayout.BeginVertical();
            // title row
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(pkg.name, SomniStyle.CardTitle);
            GUILayout.Space(6);
            SomniStyle.Chip("v" + pkg.version, SomniStyle.Accent);
            if (installedRec != null && !updateAvailable) SomniStyle.Chip("installed", SomniStyle.Green);
            if (updateAvailable) SomniStyle.Chip("update", SomniStyle.Amber);
            GUILayout.Space(2);
            // category tells the user WHERE it installs (world vs standalone) at a glance
            SomniStyle.Chip(SomniStyle.CategoryLabel(pkg.category), SomniStyle.TextMute);
            GUILayout.FlexibleSpace();
            GUILayout.Label("by " + pkg.author, SomniStyle.Meta);
            EditorGUILayout.EndHorizontal();

            GUILayout.Label(pkg.id, SomniStyle.IdText);
            if (!string.IsNullOrEmpty(pkg.description))
            {
                EditorGUILayout.Space(2);
                const int previewLen = 160;
                string flat = Flatten(pkg.description);
                bool isLong = flat.Length > previewLen;
                bool expanded = _expanded.Contains(pkg.id);
                // collapsed: a one-blurb preview; expanded: the full text with its paragraphs
                GUILayout.Label(isLong && !expanded ? Truncate(flat, previewLen) : pkg.description, SomniStyle.Desc);
                if (isLong)
                {
                    EnsureLinkStyle();
                    if (GUILayout.Button(expanded ? "Show less" : "Show more", _linkStyle, GUILayout.ExpandWidth(false)))
                    {
                        if (expanded) _expanded.Remove(pkg.id); else _expanded.Add(pkg.id);
                    }
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(pkg.downloads + " installs   ·   " + SizeStr(pkg.archiveSize), SomniStyle.Meta);
            GUILayout.Space(10);
            DrawStarsInline(pkg.ratingAvg, pkg.ratingCount);
            GUILayout.FlexibleSpace();
            DrawActions(pkg, installedRec, updateAvailable);
            EditorGUILayout.EndHorizontal();

            // Only an installed package, signed in, can be rated (the server also enforces this).
            if (installedRec != null && SomniCredentials.HasToken)
            {
                EditorGUILayout.Space(2);
                DrawRateRow(pkg);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            SomniStyle.OutlineLastRect(); // 1px card border for definition against the window bg
        }

        private static readonly Color StarGold = new Color(0.98f, 0.74f, 0.12f);
        private GUIStyle _starOn, _starOff, _starOnBig, _starOffBig;

        private void EnsureStarStyles()
        {
            if (_starOn != null) return;
            _starOn = new GUIStyle(EditorStyles.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            _starOn.normal.textColor = StarGold;
            _starOff = new GUIStyle(_starOn); _starOff.normal.textColor = SomniStyle.TextMute;
            _starOnBig = new GUIStyle(_starOn) { fontSize = 17 };
            _starOffBig = new GUIStyle(_starOff) { fontSize = 17 };
        }

        /// <summary>Read-only average: filled gold stars up to the rounded average, rest muted.</summary>
        private void DrawStarsInline(float avg, int count)
        {
            if (count <= 0) { GUILayout.Label("no ratings", SomniStyle.Meta); return; }
            EnsureStarStyles();
            int rounded = Mathf.Clamp(Mathf.RoundToInt(avg), 0, 5);
            for (int i = 1; i <= 5; i++)
            {
                Rect r = GUILayoutUtility.GetRect(13, 16, GUILayout.Width(13), GUILayout.Height(16));
                GUI.Label(r, i <= rounded ? "★" : "☆", i <= rounded ? _starOn : _starOff);
            }
            GUILayout.Space(5);
            GUILayout.Label(avg.ToString("0.0") + "  (" + count + ")", SomniStyle.Meta);
        }

        /// <summary>Interactive: empty until you rate, fills to your rating (or to the star you hover).</summary>
        private void DrawRateRow(StoreListing pkg)
        {
            EnsureStarStyles();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Your rating", SomniStyle.Meta, GUILayout.Width(68));

            var rects = new Rect[5];
            for (int i = 0; i < 5; i++)
                rects[i] = GUILayoutUtility.GetRect(21, 19, GUILayout.Width(21), GUILayout.Height(19));

            int hovered = 0;
            for (int i = 0; i < 5; i++)
            {
                if (rects[i].Contains(Event.current.mousePosition)) hovered = i + 1;
                EditorGUIUtility.AddCursorRect(rects[i], MouseCursor.Link);
            }
            int show = hovered > 0 ? hovered : pkg.myRating;
            for (int i = 0; i < 5; i++)
            {
                bool on = (i + 1) <= show;
                GUI.Label(rects[i], on ? "★" : "☆", on ? _starOnBig : _starOffBig);
            }
            if (hovered > 0 && Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                RateNow(pkg, hovered);
                Event.current.Use();
            }
            GUILayout.Space(6);
            GUILayout.Label(pkg.myRating > 0 ? "(" + pkg.myRating + "/5)" : "not rated yet", SomniStyle.Meta);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void RateNow(StoreListing pkg, int stars)
        {
            try
            {
                var res = SomniStoreClient.RatePackage(pkg.id, stars);
                pkg.ratingAvg = res.avg;
                pkg.ratingCount = res.count;
                pkg.myRating = res.mine;
                SetStatus("Rated " + pkg.name + " " + stars + "/5.", false);
            }
            catch (Exception e) { SetStatus("Rating failed: " + e.Message, true); }
        }

        private void DrawActions(StoreListing pkg, InstalledPackage installedRec, bool updateAvailable)
        {
            if (installedRec == null)
            {
                // Only world packages need a detected world; standalone (avatar/editor/etc.) don't.
                bool needsWorld = SomniPackageInstaller.CategoryNeedsWorld(pkg.category);
                using (new EditorGUI.DisabledScope(needsWorld && _world == null))
                    if (GUILayout.Button("Install", SomniStyle.PrimaryBtn, GUILayout.Width(84)))
                        Install(pkg);
                if (needsWorld && _world == null)
                    GUILayout.Label("needs a world", SomniStyle.Meta);
                return;
            }
            if (updateAvailable)
            {
                if (GUILayout.Button("Update to v" + pkg.version, SomniStyle.PrimaryBtn, GUILayout.Width(120)))
                    UpdatePackage(pkg);
                GUILayout.Space(4);
            }
            if (GUILayout.Button("Uninstall", SomniStyle.DangerBtn, GUILayout.Width(84)))
                Uninstall(pkg.id);
        }

        private void DrawThumb(StoreListing pkg)
        {
            Rect r = GUILayoutUtility.GetRect(104, 78, GUILayout.Width(104), GUILayout.Height(78));
            Texture2D tex = GetThumb(pkg);
            if (tex != null)
                GUI.DrawTexture(r, tex, ScaleMode.ScaleAndCrop);
            else
            {
                EditorGUI.DrawRect(r, SomniStyle.Divider);
                GUI.Label(r, "no image", EditorStyles.centeredGreyMiniLabel);
            }
            // uniform 1px frame either way, so image and placeholder read as the same element
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), SomniStyle.CardBorder);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), SomniStyle.CardBorder);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1, r.height), SomniStyle.CardBorder);
            EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), SomniStyle.CardBorder);
        }

        private Texture2D GetThumb(StoreListing pkg)
        {
            if (!pkg.hasImage) return null;
            string key = pkg.id + "@" + pkg.version;
            if (_thumbs.TryGetValue(key, out var cached)) return cached; // may be null (already tried)
            _thumbs[key] = null; // mark attempted so we don't refetch every repaint
            try
            {
                byte[] bytes = SomniStoreClient.DownloadImage(pkg.id, pkg.version);
                if (bytes != null)
                {
                    var t = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                    if (t.LoadImage(bytes)) _thumbs[key] = t;
                }
            }
            catch { /* thumbnail is best-effort */ }
            return _thumbs[key];
        }

        // ---------------------------------------------------------------- misc UI

        private void EnsureLinkStyle()
        {
            if (_linkStyle != null) return;
            _linkStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 2, 2),
                margin = new RectOffset(0, 0, 1, 2),
            };
            _linkStyle.normal.textColor = SomniStyle.Accent;
            _linkStyle.hover.textColor = SomniStyle.AccentHover;
        }

        /// <summary>Collapse newlines + runs of whitespace into a single-line blurb for the preview.</summary>
        private static string Flatten(string s)
        {
            string f = s.Replace("\r", " ").Replace("\n", " ").Trim();
            while (f.Contains("  ")) f = f.Replace("  ", " ");
            return f;
        }

        /// <summary>Cut to max chars on a word boundary and append an ellipsis.</summary>
        private static string Truncate(string flat, int max)
        {
            if (flat.Length <= max) return flat;
            int cut = flat.LastIndexOf(' ', Mathf.Min(max, flat.Length - 1));
            if (cut < max / 2) cut = max; // no nearby space: hard cut
            return flat.Substring(0, cut).TrimEnd() + "…";
        }

        private void DrawEmpty(string big, string small)
        {
            EditorGUILayout.Space(40);
            var b = new GUIStyle(SomniStyle.CardTitle) { alignment = TextAnchor.MiddleCenter };
            var s = new GUIStyle(SomniStyle.Subtitle) { alignment = TextAnchor.MiddleCenter };
            GUILayout.Label(big, b);
            GUILayout.Label(small, s);
        }

        private void DrawStatusBar()
        {
            if (string.IsNullOrEmpty(_status)) return;
            EditorGUILayout.HelpBox(_status, _statusError ? MessageType.Error : MessageType.Info);
        }

        private bool Matches(StoreListing p)
        {
            if (string.IsNullOrEmpty(_search)) return true;
            string q = _search.ToLowerInvariant();
            return (p.name ?? "").ToLowerInvariant().Contains(q)
                || (p.id ?? "").ToLowerInvariant().Contains(q)
                || (p.author ?? "").ToLowerInvariant().Contains(q)
                || (p.description ?? "").ToLowerInvariant().Contains(q);
        }

        private static string SizeStr(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024f).ToString("0.0") + " KB";
            return (n / (1024f * 1024f)).ToString("0.00") + " MB";
        }

        private void SetStatus(string msg, bool error) { _status = msg; _statusError = error; }

        // ---------------------------------------------------------------- actions

        private void RefreshList()
        {
            try
            {
                _listings = SomniStoreClient.ListPackages().packages;
                _thumbs.Clear();
                ReloadInstalled();
                SetStatus(_listings.Count + " package(s).", false);
            }
            catch (Exception e) { SetStatus("ERROR: " + Root(e).Message, true); }
        }

        /// <summary>Unmistakable consent: installed packages contain code that runs in your editor.</summary>
        private static bool ConfirmCodeExecution(StoreListing pkg, string verb)
        {
            return EditorUtility.DisplayDialog(
                verb + " " + pkg.name + "?",
                pkg.name + " (" + pkg.id + " v" + pkg.version + ") by " + pkg.author + " contains C# scripts " +
                "that will be compiled and RUN inside your Unity editor, with full access to your project and " +
                "computer.\n\nOnly install packages from authors you trust.",
                verb, "Cancel");
        }

        /// <summary>
        /// Re-fetch the live listing for a package right before acting on it. The window's cached
        /// catalogue can be stale (category edited on the website, version bumped) and installing
        /// off stale data routes packages to the wrong place. Falls back to the cached listing if
        /// the server is unreachable (the download's sha/signature checks still protect us).
        /// </summary>
        private StoreListing FreshListing(StoreListing cached)
        {
            try
            {
                var live = SomniStoreClient.ListPackages().packages.FirstOrDefault(p => p.id == cached.id);
                return live ?? cached;
            }
            catch { return cached; }
        }

        private void Install(StoreListing pkg)
        {
            try
            {
                pkg = FreshListing(pkg);
                // learn the target's dependencies and resolve the full install plan
                var detail = SomniStoreClient.GetPackage(pkg.id);
                var targetVer = detail.versions.Find(v => v.version == pkg.version) ??
                                (detail.versions.Count > 0 ? detail.versions[0] : null);
                var deps = targetVer?.manifest?.dependencies;
                var plan = SomniDependencyResolver.ResolveDependencies(deps, CombinedInstalled());

                if (!ConfirmInstall(pkg, plan)) { SetStatus("Cancelled.", false); return; }

                // install dependencies first (deps-of-deps already ordered first)
                foreach (var dep in plan)
                {
                    SetStatus("Installing dependency " + dep.id + " v" + dep.version + "…", false); Repaint();
                    byte[] depArchive = SomniStoreClient.DownloadPackage(dep.id, dep.version, dep.archiveSha, dep.signature);
                    SomniPackageInstaller.Install(depArchive, _world, false, CategoryForId(dep.id));
                    ReloadInstalled(); // so the next package sees it
                }

                SetStatus("Downloading " + pkg.id + "…", false); Repaint();
                byte[] archive = SomniStoreClient.DownloadPackage(pkg.id, pkg.version, pkg.archiveSha, pkg.signature);
                var rec = SomniPackageInstaller.Install(archive, _world, false, pkg.category);
                ReloadInstalled();
                // Judge world-vs-standalone by where it actually LANDED, not the listing category.
                bool worldMode = !rec.folder.StartsWith(SomniPackageInstaller.StandaloneRoot);
                string refNote = worldMode ? OfferWorldReferences(targetVer?.manifest) : "";
                string where = worldMode
                    ? ". Check the console compiles clean before uploading your world."
                    : ". Installed to " + SomniPackageInstaller.StandaloneRoot + " (standalone, not tied to a world).";
                // Surface the exact reference line for anyone wiring the package into their OWN
                // scripts. It bakes per-world, so this is the correct 'using' for THIS world (it
                // changes if you install into a different world) - we never edit the user's code.
                string useNote = worldMode && !string.IsNullOrEmpty(rec.bakedNamespace)
                    ? "\nTo use it from your own scripts:  using " + rec.bakedNamespace + ";  (copied to clipboard)"
                    : "";
                if (useNote.Length > 0) EditorGUIUtility.systemCopyBuffer = "using " + rec.bakedNamespace + ";";
                SetStatus("Installed " + rec.id + "@" + rec.version +
                          (plan.Count > 0 ? " + " + plan.Count + " dependency(ies)" : "") + where + refNote + useNote, false);
            }
            catch (Exception e) { SetStatus("ERROR: " + Root(e).Message, true); }
        }

        /// <summary>
        /// After installing a world package with no asmdef of its own, offer to add the assembly
        /// references its scripts likely need to the world asmdef. Strictly additive + consent-gated
        /// (see SomniPackageInstaller.AddWorldReferences); assemblies not present in this project are
        /// only reported, never pulled in. Returns a note for the status bar.
        /// </summary>
        private string OfferWorldReferences(SomniManifest manifest)
        {
            try
            {
                // Two sources, same consent gate: the publisher's recorded suggestions, plus the
                // references of any runtime asmdef the installer just stripped (its scripts now
                // compile into the world assembly, which must reference what they use).
                var suggested = new System.Collections.Generic.List<SuggestedRef>();
                if (manifest != null && manifest.suggestedReferences != null)
                    suggested.AddRange(manifest.suggestedReferences);
                suggested.AddRange(SomniPackageInstaller.LastStrippedAsmdefRefs);
                if (suggested.Count == 0) return "";
                var missing = SomniPackageInstaller.ComputeMissingWorldRefs(_world, suggested);

                if (missing.addable.Count > 0)
                {
                    string names = string.Join("\n  • ", missing.addable.ConvertAll(r => r.name));
                    bool ok = EditorUtility.DisplayDialog(
                        "Add assembly references?",
                        "This package's scripts likely need assemblies your world doesn't reference yet:\n\n  • " +
                        names + "\n\nAdd them to your world's asmdef? This only ADDS references (nothing is " +
                        "removed or replaced), and only to assemblies already in your project.",
                        "Add references", "Skip");
                    if (ok) SomniPackageInstaller.AddWorldReferences(_world, missing.addable);
                }
                if (missing.unavailable.Count > 0)
                    return "\nNote: this package may also need " + string.Join(", ", missing.unavailable) +
                           ", which your project doesn't have — it may not compile until you install those.";
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniAssetStore] reference suggestion failed (install unaffected): " + e.Message);
            }
            return "";
        }

        /// <summary>Consent dialog that lists the target and every dependency (all run code in the editor).</summary>
        private static bool ConfirmInstall(StoreListing target, System.Collections.Generic.List<ResolvedPackage> deps)
        {
            string list = "  • " + target.name + " (" + target.id + " v" + target.version + ")";
            if (deps.Count > 0)
            {
                list += "\n\nRequired dependencies:";
                foreach (var d in deps) list += "\n  • " + d.id + " v" + d.version;
            }
            return EditorUtility.DisplayDialog(
                "Install " + target.name + "?",
                "These package(s) will be installed, and their C# compiled and RUN inside your Unity " +
                "editor with full access to your project and computer:\n\n" + list +
                "\n\nOnly install packages from authors you trust.",
                "Install", "Cancel");
        }

        private void UpdatePackage(StoreListing pkg)
        {
            if (!ConfirmCodeExecution(pkg, "Update")) { SetStatus("Cancelled.", false); return; }
            try
            {
                pkg = FreshListing(pkg);
                SetStatus("Updating " + pkg.id + " to v" + pkg.version + "…", false); Repaint();
                byte[] archive = SomniStoreClient.DownloadPackage(pkg.id, pkg.version, pkg.archiveSha, pkg.signature);
                var rec = SomniPackageInstaller.Update(archive, _world, pkg.category);
                ReloadInstalled();
                // Same reference offer as a fresh install: updates can land in the world too.
                string refNote = "";
                if (!rec.folder.StartsWith(SomniPackageInstaller.StandaloneRoot))
                {
                    SomniManifest manifest = null;
                    try
                    {
                        var detail = SomniStoreClient.GetPackage(pkg.id);
                        var tv = detail.versions.Find(v => v.version == pkg.version);
                        manifest = tv != null ? tv.manifest : null;
                    }
                    catch { /* offer still runs on stripped-asmdef refs alone */ }
                    refNote = OfferWorldReferences(manifest);
                }
                SetStatus("Updated " + rec.id + " to v" + rec.version + "." + refNote, false);
            }
            catch (Exception e) { SetStatus("ERROR: " + Root(e).Message, true); }
        }

        private void Uninstall(string id)
        {
            try
            {
                SomniPackageInstaller.Uninstall(id, _world);
                ReloadInstalled();
                SetStatus("Uninstalled " + id + ".", false);
            }
            catch (Exception e) { SetStatus("ERROR: " + Root(e).Message, true); }
        }

        private static Exception Root(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e;
        }
    }
}
#endif
