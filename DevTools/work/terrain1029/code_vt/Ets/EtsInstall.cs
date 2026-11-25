using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Finds the player's own Euro Truck Simulator 2 - the full game or the free demo - and the
    /// folder the game keeps its settings in, and describes what it found in terms a player can
    /// act on.
    ///
    /// This world ships no part of the game. It runs a copy the player already owns, and when
    /// there is not one it has to say so clearly rather than failing somewhere deep in a launch
    /// coroutine. Everything here is read-only.
    ///
    /// Two installs are recognised because they are two different Steam apps in two different
    /// folders: the full game (app 227300, "Euro Truck Simulator 2") and the demo (app 231120,
    /// "Euro Truck Simulator 2 Demo"). Both run the same eurotrucks2.exe from bin\win_x64, and
    /// the demo is what this world was built against. The full game wins when both are present.
    ///
    /// The settings folder is separate from the install, and it matters more than the install
    /// does: the engine writes config.cfg, profiles and saves under Documents\Euro Truck
    /// Simulator 2, and it is config.cfg - not anything on the command line - that decides
    /// whether the game starts in a window. <see cref="EtsStartupOptions"/> carries the detail.
    /// </summary>
    public static class EtsInstall
    {
        /// <summary>The Steam application id of the full game.</summary>
        public const string FullGameAppId = "227300";

        /// <summary>The Steam application id of the free demo, which is a separate app.</summary>
        public const string DemoAppId = "231120";

        /// <summary>The executable, the same in both editions.</summary>
        public const string ExecutableName = "eurotrucks2.exe";

        /// <summary>Process name without extension, as Windows reports it.</summary>
        public const string ProcessName = "eurotrucks2";

        /// <summary>The folder under Documents where the engine keeps its settings and saves.</summary>
        public const string HomeFolderName = "Euro Truck Simulator 2";

        /// <summary>The engine's settings file, in the home folder.</summary>
        public const string ConfigFileName = "config.cfg";

        /// <summary>Install folder names under steamapps\common, full game first.</summary>
        private static readonly string[] SteamFolders =
        {
            "Euro Truck Simulator 2",
            "Euro Truck Simulator 2 Demo"
        };

        /// <summary>
        /// Files present in every real installation and in no partial one. The executable alone
        /// is not enough - a Steam install that is still downloading has the binaries long before
        /// it has the thirteen gigabytes of base.scs, and that difference is worth reporting
        /// before the game is launched into a black screen.
        /// </summary>
        private static readonly string[] RequiredEntries =
        {
            "bin\\win_x64\\" + ExecutableName,
            "base.scs",
            "def.scs"
        };

        /// <summary>
        /// Verifies that a folder is a usable installation.
        /// </summary>
        /// <param name="root">Folder to test - the one holding base.scs and bin.</param>
        /// <param name="missing">What was absent, when this returns false.</param>
        public static bool IsInstall(string root, out string missing)
        {
            missing = null;

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                missing = "the folder does not exist";
                return false;
            }

            var absent = new List<string>();

            foreach (string entry in RequiredEntries)
            {
                string path = Path.Combine(root, entry);

                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    absent.Add(entry);
                }
            }

            if (absent.Count == 0)
            {
                return true;
            }

            missing = string.Join(", ", absent.ToArray());
            return false;
        }

        /// <summary>
        /// Whether an install is the demo rather than the full game, which decides the Steam app
        /// id handed to the executable. Read off the folder name, because that is the only thing
        /// that differs between the two on disk.
        /// </summary>
        public static bool IsDemo(string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            string name = Path.GetFileName(root.TrimEnd('\\', '/'));
            return name.IndexOf("demo", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The Steam app id for a given install.</summary>
        public static string SteamAppId(string root)
        {
            return IsDemo(root) ? DemoAppId : FullGameAppId;
        }

        /// <summary>
        /// Looks for the game across every Steam library on the machine.
        ///
        /// The Steam path is walked by parsing libraryfolders.vdf rather than by reading the
        /// registry key for the app, because the registry only records the default library and
        /// this game is very often on a second drive - which is exactly where it is on the
        /// machine this was built against.
        /// </summary>
        /// <param name="hint">A path to try before searching. Pass the value from the inspector
        /// so an unusual install can be pointed at directly.</param>
        public static string Locate(string hint = null)
        {
            if (!string.IsNullOrEmpty(hint) && IsInstall(hint, out _))
            {
                return hint;
            }

            List<string> libraries = SteamLibraries();

            // Full game across every library before the demo in any of them.
            foreach (string folder in SteamFolders)
            {
                foreach (string library in libraries)
                {
                    string candidate = Path.Combine(
                        Path.Combine(Path.Combine(library, "steamapps"), "common"), folder);

                    if (IsInstall(candidate, out _))
                    {
                        if (!_logged)
                        {
                            _logged = true;
                            UnityEngine.Debug.Log("[SomniumEuroTruck] Install: chose " + candidate +
                                                  (IsDemo(candidate) ? " (the demo, app " + DemoAppId + ")" : " (the full game, app " + FullGameAppId + ")") +
                                                  "; searched " + libraries.Count + " Steam libraries.");
                        }

                        return candidate;
                    }
                }
            }

            return null;
        }

        private static bool _logged;

        /// <summary>
        /// Every Steam library root on this machine, starting with Steam itself.
        ///
        /// Failures are swallowed per-candidate rather than per-call: an unreadable drive in
        /// libraryfolders.vdf should not stop the search finding the game on a readable one.
        /// </summary>
        private static List<string> SteamLibraries()
        {
            var libraries = new List<string>();
            string steam = SteamRoot();

            if (!string.IsNullOrEmpty(steam))
            {
                libraries.Add(steam);
            }

            // Steam outside Program Files, or a library its manifest misses: the usual library folders on every
            // fixed drive (Directory.Exists checks only).
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    foreach (string folder in new[] { "SteamLibrary", "Steam", "Program Files (x86)\\Steam", "Games\\Steam", "Games\\SteamLibrary" })
                    {
                        try
                        {
                            string candidate = Path.Combine(drive.Name, folder);

                            if (!libraries.Contains(candidate) && Directory.Exists(Path.Combine(candidate, "steamapps")))
                            {
                                libraries.Add(candidate);

                                if (string.IsNullOrEmpty(steam) &&
                                    File.Exists(Path.Combine(Path.Combine(candidate, "steamapps"), "libraryfolders.vdf")))
                                {
                                    steam = candidate;
                                }
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            if (string.IsNullOrEmpty(steam))
            {
                return libraries;
            }

            string manifest = Path.Combine(Path.Combine(steam, "steamapps"), "libraryfolders.vdf");

            foreach (string line in ReadLines(manifest))
            {
                string path = VdfPath(line);

                if (!string.IsNullOrEmpty(path) && !libraries.Contains(path))
                {
                    libraries.Add(path);
                }
            }

            return libraries;
        }

        /// <summary>
        /// Lines of a file, or none at all if it cannot be read. An unreadable Steam manifest
        /// should cost the search one library, not throw out of the whole thing.
        /// </summary>
        private static string[] ReadLines(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllLines(path) : new string[0];
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        /// <summary>
        /// Pulls the value out of a libraryfolders.vdf "path" entry.
        ///
        /// Written by hand rather than with a VDF parser because the file is only ever read for
        /// this one key, and because the escaped backslashes it stores need undoing either way.
        /// </summary>
        private static string VdfPath(string line)
        {
            if (line == null || line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            int open = line.IndexOf('"', line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase) + 6);

            if (open < 0)
            {
                return null;
            }

            int close = line.IndexOf('"', open + 1);

            if (close < 0)
            {
                return null;
            }

            return line.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
        }

        /// <summary>
        /// Steam's own install folder, from the two default locations.
        ///
        /// Microsoft.Win32.Registry is not available in every Unity player profile, so this reads
        /// the well-known locations directly. Being wrong here costs a search of two folders, not
        /// a failure - libraryfolders.vdf is what finds the game on other drives anyway.
        /// </summary>
        private static string SteamRoot()
        {
            var candidates = new List<string>();

            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            if (!string.IsNullOrEmpty(programFilesX86))
            {
                candidates.Add(Path.Combine(programFilesX86, "Steam"));
            }

            if (!string.IsNullOrEmpty(programFiles))
            {
                candidates.Add(Path.Combine(programFiles, "Steam"));
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (Directory.Exists(Path.Combine(candidate, "steamapps")))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // An unreadable Program Files is not worth reporting; keep looking.
                }
            }

            return null;
        }

        /// <summary>Full path to eurotrucks2.exe inside an install root.</summary>
        public static string Executable(string root)
        {
            return string.IsNullOrEmpty(root)
                ? null
                : Path.Combine(Path.Combine(Path.Combine(root, "bin"), "win_x64"), ExecutableName);
        }

        /// <summary>
        /// The folder the engine keeps config.cfg, profiles and saves in.
        ///
        /// Documents\Euro Truck Simulator 2 by default. The engine can be pointed elsewhere with
        /// -homedir, which is why this takes a hint, but it is never used here: moving the home
        /// folder would also move the player's profiles and saves out of reach, and a world that
        /// hides someone's saves to get a setting written has chosen the wrong lever.
        /// </summary>
        public static string HomeFolder(string hint = null)
        {
            if (!string.IsNullOrEmpty(hint) && Directory.Exists(hint))
            {
                return hint;
            }

            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            return string.IsNullOrEmpty(documents) ? null : Path.Combine(documents, HomeFolderName);
        }

        /// <summary>Full path to config.cfg for a home folder.</summary>
        public static string ConfigPath(string home)
        {
            return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ConfigFileName);
        }

        /// <summary>
        /// A short human-readable account of what is and is not present, for the in-world status
        /// label. Reports sizes because a Steam install that is still downloading looks complete
        /// by name and is not, and that is otherwise a confusing failure.
        /// </summary>
        public static string Describe(string root, string home = null)
        {
            var report = new StringBuilder();

            if (string.IsNullOrEmpty(root))
            {
                report.AppendLine("Euro Truck Simulator 2 (or its free demo) was not found on this machine.");
                report.AppendLine();
                report.AppendLine("This world runs your own copy of the game - it does not include it.");
                report.AppendLine("Install Euro Truck Simulator 2 (or its free demo) from Steam, or set the install path by hand.");
                return report.ToString();
            }

            report.AppendLine("Install: " + root + (IsDemo(root) ? " (demo)" : ""));

            if (!IsInstall(root, out string missing))
            {
                report.AppendLine("Incomplete - missing " + missing + ".");
                return report.ToString();
            }

            try
            {
                var info = new FileInfo(Executable(root));
                report.AppendLine(ExecutableName + ": " + (info.Length / 1024L) + " KB");

                var data = new FileInfo(Path.Combine(root, "base.scs"));
                report.AppendLine("base.scs: " + (data.Length / (1024L * 1024L)) + " MB");
            }
            catch (Exception e)
            {
                report.AppendLine("install unreadable (" + e.GetType().Name + ")");
            }

            if (!string.IsNullOrEmpty(home))
            {
                string config = ConfigPath(home);
                report.AppendLine("Settings: " + home +
                                  (File.Exists(config) ? "" : " (no config.cfg yet - run the game once first)"));
            }

            return report.ToString();
        }
    }
}
