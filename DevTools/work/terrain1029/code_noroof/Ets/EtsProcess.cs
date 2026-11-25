using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Starts Euro Truck Simulator 2 and finds the window it ends up drawing into.
    ///
    /// Those are two jobs rather than one, and the reason is Steam. eurotrucks2.exe links
    /// steam_api64.dll, and a Steam game started from outside Steam will normally ask Steam to
    /// start it instead and quit - a different process, which this world never gets a handle to.
    /// Setting SteamAppId in the environment is what keeps the process we started as the one
    /// that renders; it is the same move Somnium Mythology established by measurement against
    /// AoMX.exe, and it is applied here on the same reasoning. Steam still has to be running.
    ///
    /// The window is still found by looking for it rather than by being told about it: enumerate
    /// top-level windows, keep the ones owned by a process named eurotrucks2, prefer the one
    /// whose class is the engine's own. That keeps working when the player launched the game
    /// themselves, when Steam did start it after all, and when the engine puts up more than one
    /// window.
    /// </summary>
    public sealed class EtsProcess
    {
        /// <summary>
        /// Smallest client area treated as the real game window. The engine creates its window
        /// before the swap chain is sized, so for a moment there is a genuine window that is a
        /// few pixels across; capturing that produces a valid, useless one-pixel texture.
        /// </summary>
        private const int MinimumWidth = 320;

        private const int MinimumHeight = 240;

        /// <summary>
        /// The window class of the engine's render window. "prism3d" is the name of SCS's
        /// engine and is the class string carried in eurotrucks2.exe; the probe reports what a
        /// running game actually says, so a rename by a patch is a diagnosis and not a mystery.
        /// </summary>
        public const string RenderWindowClass = "prism3d";

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr window, StringBuilder text, int count);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>What was found for one candidate window, for reporting.</summary>
        public readonly struct WindowInfo
        {
            public readonly IntPtr Handle;
            public readonly int ProcessId;
            public readonly string Title;
            public readonly string ClassName;
            public readonly int Width;
            public readonly int Height;
            public readonly bool Visible;

            public WindowInfo(IntPtr handle, int processId, string title, string className,
                int width, int height, bool visible)
            {
                Handle = handle;
                ProcessId = processId;
                Title = title;
                ClassName = className;
                Width = width;
                Height = height;
                Visible = visible;
            }

            public bool IsUsable => Handle != IntPtr.Zero && Visible &&
                                    Width >= MinimumWidth && Height >= MinimumHeight;

            public override string ToString()
            {
                return "hwnd 0x" + Handle.ToInt64().ToString("X") +
                       "  pid " + ProcessId +
                       "  class \"" + ClassName + "\"" +
                       "  title \"" + Title + "\"" +
                       "  " + Width + "x" + Height +
                       (Visible ? "" : "  (hidden)");
            }
        }

        /// <summary>The process this class started, which may already have exited via Steam.</summary>
        public Process Started { get; private set; }

        /// <summary>True once <see cref="Launch"/> has started something without throwing.</summary>
        public bool LaunchAttempted { get; private set; }

        /// <summary>How the game was started, for the status report.</summary>
        public string LaunchDescription { get; private set; }

        /// <summary>
        /// True when a eurotrucks2 process is running right now, whoever started it. Checked
        /// before launching so that a game the player already had open is adopted rather than
        /// duplicated - the engine refuses to run twice against the same profile anyway.
        /// </summary>
        public static bool IsRunning()
        {
            return FindProcesses().Count > 0;
        }

        private static List<Process> FindProcesses()
        {
            var found = new List<Process>();

            try
            {
                foreach (Process process in Process.GetProcessesByName(EtsInstall.ProcessName))
                {
                    found.Add(process);
                }
            }
            catch (Exception)
            {
                // GetProcessesByName can throw when a process dies mid-enumeration. An empty
                // list is the right answer in that case; the caller polls again.
            }

            return found;
        }

        /// <summary>
        /// Starts the game.
        /// </summary>
        /// <param name="installRoot">Folder holding base.scs and bin.</param>
        /// <param name="arguments">Switches appended to the command line.</param>
        /// <param name="throughSteam">Launch via the steam:// protocol rather than by running
        /// the executable. Quieter when Steam is not already running, because it lets Steam start
        /// the game once instead of starting it, being handed back, and starting it again.</param>
        /// <param name="failure">Why the launch did not happen, when this returns false.</param>
        public bool Launch(string installRoot, string arguments, bool throughSteam, out string failure)
        {
            failure = null;
            LaunchAttempted = false;

            string executable = EtsInstall.Executable(installRoot);
            string appId = EtsInstall.SteamAppId(installRoot);

            if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
            {
                failure = EtsInstall.ExecutableName + " not found at: " + (executable ?? "(no install path)");
                return false;
            }

            try
            {
                if (throughSteam)
                {
                    // Steam takes launch options from the app's own settings, not from this URL,
                    // so anything in "arguments" is silently dropped on this path. Said plainly
                    // in the description rather than left as a surprise when a setting is ignored.
                    Started = Process.Start(new ProcessStartInfo
                    {
                        FileName = "steam://rungameid/" + appId,
                        UseShellExecute = true
                    });

                    LaunchDescription = "via Steam (steam://rungameid/" + appId +
                                        ") - command-line options do not apply on this path";
                }
                else
                {
                    var start = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = arguments ?? string.Empty,

                        // Steam starts the game with the install root as its working directory,
                        // and matching that is the cheapest way to never find out whether the
                        // engine resolves anything relative to it.
                        WorkingDirectory = installRoot,

                        // Required in order to set environment variables below, and harmless
                        // otherwise - there is no shell verb needed to run an executable.
                        UseShellExecute = false
                    };

                    // What keeps the process we started as the one that renders. With the app
                    // id already in the environment, SteamAPI_RestartAppIfNecessary returns
                    // false and the executable carries on as itself instead of bouncing the
                    // launch through Steam - which would be both slow and a different process.
                    // The demo and the full game are different apps, so the id is per install.
                    start.EnvironmentVariables["SteamAppId"] = appId;
                    start.EnvironmentVariables["SteamGameId"] = appId;

                    Started = Process.Start(start);

                    LaunchDescription = "directly, with SteamAppId " + appId + ": " +
                                        EtsInstall.ExecutableName +
                                        (string.IsNullOrEmpty(arguments) ? "" : " " + arguments);
                }
            }
            catch (Exception e)
            {
                failure = "Could not start the game: " + e.GetType().Name + ": " + e.Message;
                return false;
            }

            LaunchAttempted = true;
            return true;
        }

        /// <summary>
        /// Describes what the process we started did next.
        ///
        /// Worth reporting on its own, because "the process exited almost immediately" reads as a
        /// crash and here is usually the opposite: it is Steam being handed the launch, and the
        /// game arriving a few seconds later under a new process id.
        /// </summary>
        public string HandoffReport()
        {
            if (Started == null)
            {
                return "Nothing was started by this world.";
            }

            try
            {
                Started.Refresh();

                if (!Started.HasExited)
                {
                    return "Launched process " + Started.Id + " is still running.";
                }

                return "Launched process " + Started.Id + " exited with code " + Started.ExitCode +
                       " - expected when Steam takes over the launch.";
            }
            catch (Exception e)
            {
                return "Could not inspect the launched process (" + e.GetType().Name + ").";
            }
        }

        /// <summary>
        /// Every top-level window belonging to a eurotrucks2 process, usable or not.
        ///
        /// The unusable ones are returned rather than filtered because they are the diagnosis
        /// when nothing is found: a window with no client area, or one that exists but is not
        /// yet visible, tells you the game is starting rather than absent.
        /// </summary>
        public static List<WindowInfo> FindWindows()
        {
            var pids = new HashSet<uint>();

            foreach (Process process in FindProcesses())
            {
                try
                {
                    pids.Add((uint)process.Id);
                }
                catch (Exception)
                {
                    // Exited between listing and reading. Skip it.
                }
            }

            var windows = new List<WindowInfo>();

            if (pids.Count == 0)
            {
                return windows;
            }

            // Held in a local rather than passed inline: the delegate is the only managed
            // reference to the thunk user32 is calling back through, and a collection during the
            // enumeration would tear down the callback mid-call.
            EnumWindowsProc collect = (window, parameter) =>
            {
                GetWindowThreadProcessId(window, out uint owner);

                if (!pids.Contains(owner))
                {
                    return true;
                }

                GetClientRect(window, out NativeRect client);

                var title = new StringBuilder(256);
                GetWindowTextW(window, title, title.Capacity);

                var className = new StringBuilder(256);
                GetClassNameW(window, className, className.Capacity);

                windows.Add(new WindowInfo(
                    window,
                    (int)owner,
                    title.ToString(),
                    className.ToString(),
                    client.Right - client.Left,
                    client.Bottom - client.Top,
                    IsWindowVisible(window)));

                return true;
            };

            EnumWindows(collect, IntPtr.Zero);
            GC.KeepAlive(collect);

            return windows;
        }

        /// <summary>
        /// The window the game is rendering into, or an empty handle if it is not up yet.
        ///
        /// Class name first, area second. The class is the precise signal, but it is also the
        /// kind of thing a patch can rename, so it decides between candidates rather than
        /// filtering them - if no window matches, the largest usable one is still returned.
        /// </summary>
        public static WindowInfo FindGameWindow()
        {
            var best = default(WindowInfo);
            long bestArea = 0;
            bool bestMatchedClass = false;

            foreach (WindowInfo window in FindWindows())
            {
                if (!window.IsUsable)
                {
                    continue;
                }

                bool matchesClass = string.Equals(window.ClassName, RenderWindowClass,
                    StringComparison.OrdinalIgnoreCase);
                long area = (long)window.Width * window.Height;

                // A class match beats any amount of area; between two of the same kind, area wins.
                if (matchesClass && !bestMatchedClass)
                {
                    bestMatchedClass = true;
                    bestArea = area;
                    best = window;
                    continue;
                }

                if (matchesClass == bestMatchedClass && area > bestArea)
                {
                    bestArea = area;
                    best = window;
                }
            }

            return best;
        }

        /// <summary>Closes the game politely, leaving a copy the player started alone.</summary>
        /// <param name="onlyIfWeStartedIt">When true, a game that was already running when the
        /// world loaded is left open. Closing someone's delivery because they walked into a
        /// Somnium world would be indefensible.</param>
        public void Shutdown(bool onlyIfWeStartedIt)
        {
            if (onlyIfWeStartedIt && !LaunchAttempted)
            {
                return;
            }

            foreach (Process process in FindProcesses())
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    // CloseMainWindow, never Kill: the engine writes config.cfg and the profile
                    // on WM_CLOSE, and killing it loses the player's own progress since the last
                    // autosave.
                    process.CloseMainWindow();
                }
                catch (Exception)
                {
                    // Already gone, or not ours to close. Either way there is nothing to do.
                }
            }
        }

        /// <summary>
        /// Waits for every eurotrucks2 process to go away, up to a limit.
        ///
        /// Blocking, and deliberately so: it is called from OnDestroy, after a polite close,
        /// because config.cfg can only be restored once the engine has finished writing it. A
        /// restore that races the game's own exit is overwritten a second later.
        /// </summary>
        /// <returns>True if the game has exited.</returns>
        public static bool WaitForExit(int milliseconds)
        {
            var clock = Stopwatch.StartNew();

            while (clock.ElapsedMilliseconds < milliseconds)
            {
                if (!IsRunning())
                {
                    return true;
                }

                System.Threading.Thread.Sleep(100);
            }

            return !IsRunning();
        }

        /// <summary>True if the handle still refers to a live window.</summary>
        public static bool StillAlive(IntPtr window)
        {
            return window != IntPtr.Zero && IsWindow(window);
        }

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
            int x, int y, int w, int h, uint flags);

        /// <summary>
        /// Moves the game window without resizing it, raising it, or stealing focus.
        ///
        /// All three of those omissions matter. Resizing would recreate the swap chain and
        /// invalidate the capture buffers; raising it would put the game in front of Somnium at
        /// the exact moment it is meant to disappear; activating it would move input on a call
        /// that is only meant to move a rectangle.
        /// </summary>
        public static bool MoveWindow(IntPtr window, int x, int y)
        {
            return StillAlive(window) &&
                   SetWindowPos(window, IntPtr.Zero, x, y, 0, 0,
                       SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }
}
