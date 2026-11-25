using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The engine settings this world needs Euro Truck Simulator 2 to start with, and the right
    /// place to put each of them.
    ///
    /// The split is decided by what the executable actually accepts. Its command-line table,
    /// read out of eurotrucks2.exe, is short: -nointro, -noworkshop, -homedir, -rdevice,
    /// -force_mods, -unlimitedlog, -mm_pool_size, -oculus, -32bit and -64bit. There is no
    /// windowed switch and no way to pass a cvar. So:
    ///
    ///   config.cfg      anything the renderer needs - windowed mode, resolution - and the sound
    ///                   suspend flag. These are "uset" lines the engine reads at startup and
    ///                   writes back on exit.
    ///   command line    the intro skip, and nothing that matters.
    ///
    /// Windowed is not a preference, it is the whole thing. config.cfg on this machine says
    /// r_fullscreen "1", and the engine's own log shows it entering exclusive fullscreen through
    /// DXGI. Nothing can composite an exclusive-fullscreen swap chain, so with r_fullscreen left
    /// at 1 there is no picture to capture and the world cannot work.
    ///
    /// config.cfg lives in the player's Documents folder, not in the install, and editing it is
    /// not something to do quietly. What makes it defensible: the original is copied aside first,
    /// only the keys named here are touched, and <see cref="RestoreConfig"/> puts back exactly
    /// the keys this world changed - by value, from the backup - and leaves anything the player
    /// changed in the game's own options during the session alone.
    /// </summary>
    [Serializable]
    public class EtsStartupOptions
    {
        /// <summary>Which rendering device the engine is asked for, via -rdevice.</summary>
        public enum RendererChoice
        {
            /// <summary>Whatever config.cfg says. dx11 on this machine.</summary>
            PlayerChoice,

            /// <summary>-rdevice dx11. The default device, and the one the probe measured.</summary>
            DirectX11,

            /// <summary>-rdevice gl. Worth a try for capture - see the tooltip.</summary>
            OpenGL
        }

        [Header("Display (written to config.cfg)")]
        [Tooltip("Start windowed rather than fullscreen. Required - an exclusive-fullscreen swap " +
                 "chain cannot be captured or composited by anything.")]
        public bool Windowed = true;

        [Tooltip("Window width in pixels, written as r_mode_width. Zero leaves the player's own " +
                 "choice alone, which is the default - the resolution is a setting they made in " +
                 "the game's own options, and there is no reason for this world to overrule it.")]
        public int Width;

        [Tooltip("Window height in pixels, written as r_mode_height. Zero leaves it alone.")]
        public int Height;

        [Tooltip("Keep the game's audio running while it does not have focus, by writing " +
                 "s_suspend_sound 0. Off by default: the engine's own default is to go quiet " +
                 "when it loses focus, which is what most players want when they open the tablet.")]
        public bool KeepSoundWhenUnfocused;

        [Header("Command line")]
        [Tooltip("Skip the startup movies. -nointro")]
        public bool SkipIntro = true;

        [Tooltip("Do not ask Steam for Workshop mods at startup. -noworkshop. Off by default: " +
                 "the player's mods are theirs.")]
        public bool NoWorkshop;

        [Tooltip("Rendering device, passed as -rdevice. Player Choice leaves config.cfg's r_device " +
                 "alone.\n\nWhy it is here: measured on this machine, the DX11 window presents " +
                 "through a flip-model swap chain, so a plain GDI BitBlt sees black and only DWM's " +
                 "composed surface (PrintWindow with RENDERFULLCONTENT, ~20 ms a frame) returns " +
                 "the picture. The engine also knows an OpenGL device, which presents through the " +
                 "GDI redirection surface - where a 2 ms BitBlt worked against Quake III. Try " +
                 "OpenGL with the capability probe; if BitBlt scores a real picture there, it is " +
                 "the cheaper way to run.")]
        public RendererChoice Renderer = RendererChoice.PlayerChoice;

        /// <summary>The config.cfg keys this class may write. Nothing else is ever touched.</summary>
        public static readonly string[] TouchedKeys =
        {
            "r_fullscreen",
            "r_mode_width",
            "r_mode_height",
            "s_suspend_sound"
        };

        /// <summary>Settings that go on the command line.</summary>
        public List<string> CommandLineTokens()
        {
            var tokens = new List<string>();

            if (SkipIntro)
            {
                tokens.Add("-nointro");
            }

            if (NoWorkshop)
            {
                tokens.Add("-noworkshop");
            }

            if (Renderer == RendererChoice.DirectX11)
            {
                tokens.Add("-rdevice dx11");
            }
            else if (Renderer == RendererChoice.OpenGL)
            {
                tokens.Add("-rdevice gl");
            }

            return tokens;
        }

        /// <summary>The command-line tokens joined into one string.</summary>
        public string CommandLine()
        {
            return string.Join(" ", CommandLineTokens().ToArray());
        }

        // ------------------------------------------------------------------ config.cfg

        private const string BackupSuffix = ".somnium-backup";
        private const string TouchedSuffix = ".somnium-touched";

        /// <summary>The values this world wants, by key. Only keys that are actually asked for.</summary>
        private Dictionary<string, string> Wanted()
        {
            var wanted = new Dictionary<string, string>();

            if (Windowed)
            {
                wanted["r_fullscreen"] = "0";
            }

            if (Width > 0)
            {
                wanted["r_mode_width"] = Mathf.Max(640, Width).ToString();
            }

            if (Height > 0)
            {
                wanted["r_mode_height"] = Mathf.Max(480, Height).ToString();
            }

            if (KeepSoundWhenUnfocused)
            {
                wanted["s_suspend_sound"] = "0";
            }

            return wanted;
        }

        /// <summary>
        /// Writes the display settings into config.cfg.
        ///
        /// Worth knowing: the engine rewrites this file itself on a clean exit, from the values
        /// it has in memory. So r_fullscreen 0 written here is written back by the game when it
        /// closes, and would outlive the world if nothing put it back. That is exactly why the
        /// backup exists and why <see cref="RestoreConfig"/> runs only after the game has gone.
        /// </summary>
        /// <param name="configPath">Full path to config.cfg.</param>
        /// <param name="failure">Why nothing was written, when this returns false.</param>
        public bool ApplyToConfig(string configPath, out string failure)
        {
            failure = null;

            if (string.IsNullOrEmpty(configPath))
            {
                failure = "No config.cfg path to write to.";
                return false;
            }

            if (!File.Exists(configPath))
            {
                // Written on first run. Before that there is nothing to edit, and creating one
                // by hand risks a file the engine will not parse.
                failure = "config.cfg does not exist yet - run the game once first.";
                return false;
            }

            try
            {
                string backup = configPath + BackupSuffix;

                // Backed up once and never again. Overwriting it on a second run would replace
                // the player's original with our own edited copy, which is the one way this
                // could actually lose their settings.
                if (!File.Exists(backup))
                {
                    File.Copy(configPath, backup);
                }

                Dictionary<string, string> wanted = Wanted();
                var lines = new List<string>(File.ReadAllLines(configPath));
                var seen = new HashSet<string>();
                var touched = new List<string>();

                for (int i = 0; i < lines.Count; i++)
                {
                    string key = KeyOf(lines[i]);

                    if (key == null || !wanted.ContainsKey(key))
                    {
                        continue;
                    }

                    seen.Add(key);

                    if (!string.Equals(ValueOf(lines[i]), wanted[key], StringComparison.Ordinal))
                    {
                        lines[i] = Line(key, wanted[key]);
                        touched.Add(key);
                    }
                }

                foreach (KeyValuePair<string, string> entry in wanted)
                {
                    if (!seen.Contains(entry.Key))
                    {
                        lines.Add(Line(entry.Key, entry.Value));
                        touched.Add(entry.Key);
                    }
                }

                // Recorded so that the restore puts back only what was changed. A key that was
                // already at the wanted value is not ours to restore - the player may change it
                // in the game's options during the session and expect that to stick.
                //
                // Merged with an earlier list rather than replacing it. When the backup already
                // exists - a previous session wrote the file and could not restore it because
                // the game was still running - the keys are already at the wanted values, so
                // this pass changes nothing and would otherwise record nothing; the restore
                // would then put nothing back and delete the backup, and the player's original
                // setting would be gone for good.
                string touchedFile = configPath + TouchedSuffix;

                if (File.Exists(touchedFile))
                {
                    foreach (string key in File.ReadAllLines(touchedFile))
                    {
                        if (!string.IsNullOrEmpty(key) && !touched.Contains(key))
                        {
                            touched.Add(key);
                        }
                    }
                }

                File.WriteAllLines(touchedFile, touched.ToArray(), new UTF8Encoding(false));

                // Encoding stated rather than left to the default. A UTF-8 byte-order mark at
                // the top of the file is three invisible bytes in front of the first "uset",
                // and an engine that does not expect them reads a setting it does not recognise.
                File.WriteAllLines(configPath, lines.ToArray(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception e)
            {
                failure = "Could not write " + configPath + ": " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// Puts back the keys this world changed, from the backup, and leaves everything else as
        /// the game last wrote it. Safe to call when nothing was ever written.
        ///
        /// A wholesale copy of the backup would be simpler and would also throw away every
        /// setting the player changed in the game's own options while the world was open. The
        /// backup is only used whole when config.cfg itself has gone missing.
        /// </summary>
        public static void RestoreConfig(string configPath)
        {
            if (string.IsNullOrEmpty(configPath))
            {
                return;
            }

            string backup = configPath + BackupSuffix;
            string touchedFile = configPath + TouchedSuffix;

            try
            {
                if (!File.Exists(backup))
                {
                    return;
                }

                if (!File.Exists(configPath))
                {
                    File.Copy(backup, configPath, true);
                }
                else
                {
                    string[] keys = File.Exists(touchedFile) ? File.ReadAllLines(touchedFile) : TouchedKeys;
                    var originals = new Dictionary<string, string>();

                    foreach (string line in File.ReadAllLines(backup))
                    {
                        string key = KeyOf(line);

                        if (key != null && Array.IndexOf(keys, key) >= 0)
                        {
                            originals[key] = ValueOf(line);
                        }
                    }

                    var lines = new List<string>(File.ReadAllLines(configPath));
                    var seen = new HashSet<string>();

                    for (int i = 0; i < lines.Count; i++)
                    {
                        string key = KeyOf(lines[i]);

                        if (key != null && originals.ContainsKey(key))
                        {
                            lines[i] = Line(key, originals[key]);
                            seen.Add(key);
                        }
                    }

                    foreach (KeyValuePair<string, string> original in originals)
                    {
                        if (!seen.Contains(original.Key))
                        {
                            lines.Add(Line(original.Key, original.Value));
                        }
                    }

                    File.WriteAllLines(configPath, lines.ToArray(), new UTF8Encoding(false));
                }

                File.Delete(backup);

                if (File.Exists(touchedFile))
                {
                    File.Delete(touchedFile);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Could not restore " + configPath + ": " + e.Message);
            }
        }

        /// <summary>True when a backup from an earlier run is still sitting next to config.cfg.</summary>
        public static bool HasPendingRestore(string configPath)
        {
            return !string.IsNullOrEmpty(configPath) && File.Exists(configPath + BackupSuffix);
        }

        /// <summary>
        /// The setting name a config.cfg line sets, or null for blanks, comments and anything
        /// that is not a "uset" line. The file is one shape throughout:
        ///
        ///     uset r_fullscreen "1"
        /// </summary>
        public static string KeyOf(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }

            string trimmed = line.Trim();

            if (!trimmed.StartsWith("uset ", StringComparison.Ordinal))
            {
                return null;
            }

            string rest = trimmed.Substring(5).TrimStart();
            int space = rest.IndexOfAny(new[] { ' ', '\t' });

            return space < 0 ? rest : rest.Substring(0, space);
        }

        /// <summary>The value of a "uset" line, without its quotes. Null when there is none.</summary>
        public static string ValueOf(string line)
        {
            if (KeyOf(line) == null)
            {
                return null;
            }

            int open = line.IndexOf('"');

            if (open < 0)
            {
                // Unquoted, which the engine does not write but does read.
                string[] parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 3 ? parts[2] : string.Empty;
            }

            int close = line.IndexOf('"', open + 1);
            return close < 0 ? line.Substring(open + 1) : line.Substring(open + 1, close - open - 1);
        }

        /// <summary>A config.cfg line in the engine's own format.</summary>
        public static string Line(string key, string value)
        {
            return "uset " + key + " \"" + value + "\"";
        }

        /// <summary>
        /// A short account of what will be set and where, for the status label. Names both
        /// places because "it ignored my resolution" is otherwise a very confusing afternoon.
        /// </summary>
        public string Describe()
        {
            var report = new StringBuilder();

            string resolution = Width > 0 && Height > 0
                ? "r_mode_width " + Width + ", r_mode_height " + Height
                : "resolution left as the player set it";

            report.AppendLine("config.cfg: " + resolution +
                              (Windowed ? ", r_fullscreen 0" : " (FULLSCREEN - capture will fail)") +
                              (KeepSoundWhenUnfocused ? ", s_suspend_sound 0" : ""));

            string commandLine = CommandLine();
            report.AppendLine("command line: " + (commandLine.Length == 0 ? "(none)" : commandLine));
            report.AppendLine("renderer: " + (Renderer == RendererChoice.PlayerChoice
                ? "as config.cfg's r_device says"
                : Renderer.ToString()));

            return report.ToString();
        }
    }
}
