using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Capability probe for running Euro Truck Simulator 2 inside a Somnium world.
    ///
    /// Somnium Quake and Somnium Mythology both started the same way and for the same reason:
    /// the design of the finished thing depends on answers that cannot be read out of a file,
    /// and guessing them costs far more than measuring them. What is different here is which
    /// answers are unknown, and the list is short but decisive.
    ///
    ///   1. Can this world read the player's install, and where does the engine keep config.cfg?
    ///   2. Is P/Invoke available, or does the sandbox refuse it?
    ///   3. eurotrucks2.exe links steam_api64. Does launching it directly with SteamAppId set
    ///      keep it as the process that renders, or does it hand off to Steam and exit?
    ///   4. What window does the game end up in - class, title, size - and did r_fullscreen 0 in
    ///      config.cfg actually put it in a window?
    ///   5. Which capture method returns real pixels from its DX11 swap chain, and at what cost?
    ///   6. Does it keep rendering while it does not have focus?
    ///   7. How does it treat the mouse while it has focus - is the Windows cursor hidden and
    ///      clipped (a DirectInput game reading deltas, which works off-screen), or shown and
    ///      free (a game following the Windows cursor, which does not)?
    ///
    /// Each stage is caught on its own, because where it breaks is the diagnosis. A single
    /// try/catch around the whole thing collapses seven different answers into "it didn't work".
    /// </summary>
    public class EtsLaunchSpike : MonoBehaviour
    {
        [Header("Euro Truck Simulator 2")]
        [Tooltip("Install folder. Leave empty to search every Steam library on this machine.")]
        [SerializeField]
        private string _installRoot = "";

        [Tooltip("Settings folder holding config.cfg. Leave empty for Documents\\Euro Truck Simulator 2.")]
        [SerializeField]
        private string _settingsFolder = "";

        [Tooltip("Actually start the game. Turn this off to run stages 1 and 2 on their own, " +
                 "which is the read-only half and starts nothing.")]
        [SerializeField]
        private bool _launchGame = true;

        [Tooltip("Start through Steam rather than by running eurotrucks2.exe. Quieter when Steam " +
                 "is not already running, but Steam ignores the command line.")]
        [SerializeField]
        private bool _launchThroughSteam;

        [SerializeField]
        private EtsStartupOptions _options = new EtsStartupOptions();

        [Tooltip("Write the display settings into config.cfg before launching. Without this the " +
                 "engine starts in exclusive fullscreen and stage 5 has nothing it can capture. " +
                 "The original is backed up and the changed keys are put back afterwards.")]
        [SerializeField]
        private bool _mayWriteConfig = true;

        [Tooltip("Seconds to wait for the game window. Generous, because a cold Steam start has " +
                 "to bring Steam up first and the engine mounts its archives before opening one.")]
        [Range(10f, 180f)]
        [SerializeField]
        private float _windowTimeout = 120f;

        [Header("Output")]
        [SerializeField]
        private TMP_Text _output;

        [SerializeField]
        private bool _runOnStart = true;

        [Tooltip("Close the game when the probe finishes. Off by default so the window can be " +
                 "looked at, and because a game the player already had open must not be closed.")]
        [SerializeField]
        private bool _closeGameAfterwards;

        private readonly StringBuilder _log = new StringBuilder();
        private int _stagesPassed;
        private EtsProcess _process;
        private string _resolvedRoot;
        private string _configPath;
        private bool _wroteConfig;

        // All of these ship with Windows. Nothing native is distributed with the world.
        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CursorInfo info);

        [DllImport("user32.dll")]
        private static extern bool GetClipCursor(out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Cursor;
            public NativePoint ScreenPosition;
        }

        private const int CURSOR_SHOWING = 0x00000001;

        private void Start()
        {
            if (_runOnStart)
            {
                RunProbe();
            }
        }

        /// <summary>Runs the probe. Safe to call again from a world trigger.</summary>
        public void RunProbe()
        {
            StopAllCoroutines();
            StartCoroutine(ProbeRoutine());
        }

        private IEnumerator ProbeRoutine()
        {
            _log.Length = 0;
            _stagesPassed = 0;

            _log.AppendLine("SOMNIUM EURO TRUCK - capability probe");
            _log.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            _log.AppendLine();

            // Somnium must keep simulating once the game takes focus, or this component stops
            // running the moment it becomes interesting. Set before anything is launched.
            Application.runInBackground = true;

            string root = Stage1_FindInstall();
            _resolvedRoot = root;
            Publish();

            if (root == null)
            {
                Finish();
                yield break;
            }

            if (!Stage2_NativeCalls())
            {
                Publish();
                Finish();
                yield break;
            }

            Publish();

            if (!_launchGame)
            {
                _log.AppendLine("Stages 3-7 skipped: launching is turned off.");
                Finish();
                yield break;
            }

            yield return StartCoroutine(Stage3_Launch(root));
            Publish();

            EtsProcess.WindowInfo window = default;
            yield return StartCoroutine(Stage4_FindWindow(result => window = result));
            Publish();

            if (window.Handle == IntPtr.Zero)
            {
                Finish();
                yield break;
            }

            // The first frames after the swap chain appears are the loading screen, or nothing
            // at all. Probing capture against those understates every method equally, but it
            // also makes the whole comparison meaningless, so wait for a real picture.
            yield return new WaitForSeconds(3f);

            var capture = new EtsWindowCapture(window.Handle);

            bool captureWorks = Stage5_CompareCaptureMethods(capture);
            Publish();

            if (!captureWorks)
            {
                capture.Dispose();
                Finish();
                yield break;
            }

            yield return StartCoroutine(Stage5b_ThreadedThroughput(window.Handle, capture.Mode));
            Publish();

            yield return StartCoroutine(Stage6_UnfocusedRendering(capture));
            Publish();

            yield return StartCoroutine(Stage7_MouseBehaviour(window.Handle));
            Publish();

            yield return StartCoroutine(Stage8_OffscreenCapture(window.Handle, capture));

            capture.Dispose();
            Finish();
        }

        // ---------------------------------------------------------------------- stage 1

        /// <summary>
        /// Can this world see the player's installation, is it complete, and is there a
        /// config.cfg to write windowed mode into?
        /// </summary>
        private string Stage1_FindInstall()
        {
            _log.AppendLine("1. Install");

            try
            {
                string root = EtsInstall.Locate(string.IsNullOrEmpty(_installRoot) ? null : _installRoot);

                if (root == null)
                {
                    _log.AppendLine("   FAIL - no Euro Truck Simulator 2 found.");
                    _log.AppendLine("   " + EtsInstall.Describe(null).Replace("\n", "\n   "));
                    return null;
                }

                _log.AppendLine("   found: " + root + (EtsInstall.IsDemo(root) ? " (demo, app " +
                                EtsInstall.DemoAppId + ")" : " (app " + EtsInstall.FullGameAppId + ")"));

                // Reading bytes, not just testing existence. Directory.Exists succeeds on a
                // folder the sandbox will not let us open, and that difference is the whole
                // question at this stage.
                string executable = EtsInstall.Executable(root);

                using (var stream = File.OpenRead(executable))
                {
                    var header = new byte[2];
                    int read = stream.Read(header, 0, 2);

                    if (read != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                    {
                        _log.AppendLine("   FAIL - " + EtsInstall.ExecutableName +
                                        " is readable but is not an executable.");
                        return null;
                    }
                }

                _log.AppendLine("   read " + EtsInstall.ExecutableName + " - file access works.");

                string home = EtsInstall.HomeFolder(string.IsNullOrEmpty(_settingsFolder) ? null : _settingsFolder);
                _configPath = EtsInstall.ConfigPath(home);

                if (_configPath == null || !File.Exists(_configPath))
                {
                    _log.AppendLine("   settings: " + (home ?? "(no Documents folder)") + " - no config.cfg yet.");
                    _log.AppendLine("   Run the game once by hand so it writes one; windowed mode");
                    _log.AppendLine("   cannot be set until it exists.");
                }
                else
                {
                    _log.AppendLine("   settings: " + _configPath);

                    foreach (string line in File.ReadAllLines(_configPath))
                    {
                        string key = EtsStartupOptions.KeyOf(line);

                        if (key != null && Array.IndexOf(EtsStartupOptions.TouchedKeys, key) >= 0)
                        {
                            _log.AppendLine("     " + line.Trim());
                        }
                    }
                }

                _stagesPassed++;
                return root;
            }
            catch (UnauthorizedAccessException e)
            {
                _log.AppendLine("   FAIL - access denied: " + e.Message);
                _log.AppendLine("   The sandbox or the filesystem is refusing reads. Nothing else can work.");
                return null;
            }
            catch (Exception e)
            {
                _log.AppendLine("   FAIL - " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        // ---------------------------------------------------------------------- stage 2

        /// <summary>
        /// Is P/Invoke into the Windows system libraries permitted? Everything after this depends
        /// on it, and it is the one thing an IL2CPP build or a strict validator might refuse.
        /// </summary>
        private bool Stage2_NativeCalls()
        {
            _log.AppendLine();
            _log.AppendLine("2. Native calls");

            try
            {
                ulong ticks = GetTickCount64();
                IntPtr foreground = GetForegroundWindow();

                _log.AppendLine("   kernel32 GetTickCount64 -> " + ticks);
                _log.AppendLine("   user32 GetForegroundWindow -> 0x" + foreground.ToInt64().ToString("X"));
                _stagesPassed++;
                return true;
            }
            catch (Exception e)
            {
                // TypeLoad and EntryPointNotFound mean stripping or a blocked import; a security
                // exception means the sandbox. Both end the compositing approach, so name which.
                _log.AppendLine("   FAIL - " + e.GetType().Name + ": " + e.Message);
                _log.AppendLine("   P/Invoke is unavailable. The game cannot be launched or captured.");
                return false;
            }
        }

        // ---------------------------------------------------------------------- stage 3

        /// <summary>
        /// Start the game, and record what the launched process did. A handoff to Steam is a
        /// finding rather than a failure, and the report says so.
        /// </summary>
        private IEnumerator Stage3_Launch(string root)
        {
            _log.AppendLine();
            _log.AppendLine("3. Launch");

            if (EtsProcess.IsRunning())
            {
                _log.AppendLine("   already running - adopting the existing game rather than");
                _log.AppendLine("   starting a second copy. config.cfg is left alone.");
                _stagesPassed++;
                yield break;
            }

            _process = new EtsProcess();

            // Display settings go in first. The engine reads config.cfg once at startup, so
            // windowed mode has to be in the file before the process exists.
            if (_mayWriteConfig)
            {
                if (EtsStartupOptions.HasPendingRestore(_configPath))
                {
                    EtsStartupOptions.RestoreConfig(_configPath);
                    _log.AppendLine("   restored config.cfg from an earlier run's backup first.");
                }

                if (_options.ApplyToConfig(_configPath, out string configFailure))
                {
                    _wroteConfig = true;
                    _log.AppendLine("   config.cfg written (backed up alongside it).");
                    _log.AppendLine("   " + _options.Describe().TrimEnd().Replace("\n", "\n   "));
                }
                else
                {
                    _log.AppendLine("   config.cfg NOT written - " + configFailure);
                    _log.AppendLine("   the game will start fullscreen and capture will fail.");
                }
            }

            string commandLine = _launchThroughSteam ? string.Empty : _options.CommandLine();

            if (!_process.Launch(root, commandLine, _launchThroughSteam, out string failure))
            {
                _log.AppendLine("   FAIL - " + failure);
                yield break;
            }

            _log.AppendLine("   started " + _process.LaunchDescription);

            // Give the handoff time to happen before reporting on it, or this always reads
            // "still running" and says nothing.
            yield return new WaitForSeconds(3f);

            _log.AppendLine("   " + _process.HandoffReport());
            _stagesPassed++;
        }

        // ---------------------------------------------------------------------- stage 4

        /// <summary>
        /// Wait for the render window and describe every window the game owns.
        ///
        /// The full list is reported, not just the winner, because the class and title strings
        /// are only guessed from the executable and this is where they are confirmed - and
        /// because when no usable window appears, the unusable ones say why.
        /// </summary>
        private IEnumerator Stage4_FindWindow(Action<EtsProcess.WindowInfo> found)
        {
            _log.AppendLine();
            _log.AppendLine("4. Window");

            float deadline = Time.realtimeSinceStartup + _windowTimeout;
            EtsProcess.WindowInfo window = default;

            while (Time.realtimeSinceStartup < deadline)
            {
                window = EtsProcess.FindGameWindow();

                if (window.Handle != IntPtr.Zero)
                {
                    break;
                }

                yield return new WaitForSeconds(0.5f);
            }

            List<EtsProcess.WindowInfo> all = EtsProcess.FindWindows();

            if (all.Count == 0)
            {
                _log.AppendLine("   FAIL - no eurotrucks2 process owns any window.");
                _log.AppendLine("   The game never started, or Steam is asking for something first.");
                found(default);
                yield break;
            }

            _log.AppendLine("   windows owned by eurotrucks2:");

            foreach (EtsProcess.WindowInfo candidate in all)
            {
                _log.AppendLine("     " + candidate);
            }

            if (window.Handle == IntPtr.Zero)
            {
                _log.AppendLine("   FAIL - none of them is a usable render window yet.");
                found(default);
                yield break;
            }

            _log.AppendLine("   using: " + window);

            if (!string.Equals(window.ClassName, EtsProcess.RenderWindowClass, StringComparison.OrdinalIgnoreCase))
            {
                _log.AppendLine("   note: class is not \"" + EtsProcess.RenderWindowClass +
                                "\" - chosen by size instead. Worth updating EtsProcess.");
            }

            // The single most useful line in this whole report. config.cfg is the only thing
            // that can put the engine in a window, and a window the exact size of the desktop is
            // the signature of it having stayed in exclusive fullscreen instead - which no
            // capture method can reach.
            bool fillsScreen = window.Width >= Screen.currentResolution.width &&
                               window.Height >= Screen.currentResolution.height;

            _log.AppendLine("   asked config.cfg for " +
                            (_options.Width > 0 ? _options.Width + "x" + _options.Height : "the player's resolution") +
                            " windowed -> got " + window.Width + "x" + window.Height +
                            (fillsScreen
                                ? "  FILLS THE SCREEN - may be exclusive fullscreen; watch stage 5."
                                : "  windowed."));

            _stagesPassed++;
            found(window);
        }

        // ---------------------------------------------------------------------- stage 5

        /// <summary>
        /// The decisive stage: which capture method returns a picture from a DX11 window, and
        /// how much does each one cost per frame?
        /// </summary>
        private bool Stage5_CompareCaptureMethods(EtsWindowCapture capture)
        {
            _log.AppendLine();
            _log.AppendLine("5. Capture (" + capture.Width + "x" + capture.Height + ")");

            EtsWindowCapture.Score[] scores;

            try
            {
                scores = capture.Probe();
            }
            catch (Exception e)
            {
                _log.AppendLine("   FAIL - " + e.GetType().Name + ": " + e.Message);
                return false;
            }

            bool anyWorked = false;

            foreach (EtsWindowCapture.Score score in scores)
            {
                _log.AppendLine("   " + score);
                anyWorked |= score.Succeeded && score.NonBlack >= 0.05f;
            }

            if (!anyWorked)
            {
                _log.AppendLine("   FAIL - every method returned a blank frame.");
                _log.AppendLine("   The game cannot be composited into the world. The embedded game");
                _log.AppendLine("   falls back to showing the real window in front and toggling focus.");
                return false;
            }

            double budget = 1000.0 / 60.0;
            double cost = 0;

            foreach (EtsWindowCapture.Score score in scores)
            {
                if (score.Method == capture.Mode)
                {
                    cost = score.Milliseconds;
                }
            }

            _log.AppendLine("   chosen: " + capture.Mode +
                            " - " + cost.ToString("0.00") + " ms of a " +
                            budget.ToString("0.0") + " ms frame (" +
                            (cost / budget * 100.0).ToString("0") + "%)");

            _stagesPassed++;
            return true;
        }

        // ---------------------------------------------------------------------- stage 6

        /// <summary>
        /// Does the game keep drawing while it does not have focus?
        ///
        /// Measured rather than trusted, because the whole tablet-over-a-live-drive idea rests
        /// on it. Compared by hashing frames: the main menu animates, so two identical hashes
        /// taken a second and a half apart while unfocused means it stopped.
        /// </summary>
        private IEnumerator Stage6_UnfocusedRendering(EtsWindowCapture capture)
        {
            _log.AppendLine();
            _log.AppendLine("6. Rendering while unfocused");

            IntPtr somnium = GetForegroundWindow();

            SetForegroundWindow(somnium);
            yield return new WaitForSeconds(1f);

            if (!capture.Capture())
            {
                _log.AppendLine("   INCONCLUSIVE - could not take a frame.");
                yield break;
            }

            int before = Hash(capture.Pixels);

            // A full second and a half, not a frame or two. The menu animates slowly, and a
            // short wait can produce two identical frames from a game that is running fine.
            yield return new WaitForSeconds(1.5f);

            if (!capture.Capture())
            {
                _log.AppendLine("   INCONCLUSIVE - could not take a second frame.");
                yield break;
            }

            int after = Hash(capture.Pixels);

            if (before != after)
            {
                _log.AppendLine("   the picture changed while Somnium had focus - the game keeps");
                _log.AppendLine("   rendering without focus.");
                _stagesPassed++;
                yield break;
            }

            _log.AppendLine("   the picture did not change while unfocused.");
            _log.AppendLine("   Either the engine pauses without focus, or this screen is simply");
            _log.AppendLine("   static. Re-run on an animated screen before concluding.");
        }

        // ---------------------------------------------------------------------- stage 7

        /// <summary>
        /// How the game treats the mouse while it has focus.
        ///
        /// A game reading the mouse through DirectInput hides the Windows cursor and usually
        /// clips it, and takes movement as deltas from the device - which is what lets its
        /// window sit off-screen. A game following the Windows cursor leaves it showing and
        /// free, and its menus become unreachable the moment the window is out of reach of the
        /// cursor. Nothing in the executable says which, so it is observed here, in the menu the
        /// game starts in - the place where it matters most.
        /// </summary>
        private IEnumerator Stage7_MouseBehaviour(IntPtr gameWindow)
        {
            _log.AppendLine();
            _log.AppendLine("7. Mouse while the game has focus");

            IntPtr somnium = GetForegroundWindow();

            SetForegroundWindow(gameWindow);
            yield return new WaitForSeconds(1.5f);

            try
            {
                bool focused = GetForegroundWindow() == gameWindow;
                _log.AppendLine("   foreground is the game: " + (focused ? "yes" : "NO - something took focus back"));

                var info = new CursorInfo { Size = Marshal.SizeOf(typeof(CursorInfo)) };
                bool haveCursor = GetCursorInfo(ref info);
                bool showing = haveCursor && (info.Flags & CURSOR_SHOWING) != 0;

                _log.AppendLine("   Windows cursor: " + (haveCursor
                    ? (showing ? "showing" : "hidden") + " at " + info.ScreenPosition.X + "," + info.ScreenPosition.Y
                    : "unknown"));

                if (GetClipCursor(out NativeRect clip))
                {
                    bool wholeScreen = clip.Left <= 0 && clip.Top <= 0 &&
                                       clip.Right >= Screen.currentResolution.width &&
                                       clip.Bottom >= Screen.currentResolution.height;

                    _log.AppendLine("   cursor clip: " + (wholeScreen
                        ? "none (free)"
                        : clip.Left + "," + clip.Top + " - " + clip.Right + "," + clip.Bottom));
                }

                if (!showing)
                {
                    _log.AppendLine("   reads as DirectInput deltas - an off-screen window should work.");
                }
                else
                {
                    _log.AppendLine("   the cursor is showing. If the menus follow it, they will not be");
                    _log.AppendLine("   reachable with the window off-screen; the embedded game's Foreground");
                    _log.AppendLine("   mode or a window position just past the desktop edge are the options.");
                }

                _stagesPassed++;
            }
            catch (Exception e)
            {
                _log.AppendLine("   INCONCLUSIVE - " + e.GetType().Name + ": " + e.Message);
            }

            SetForegroundWindow(somnium);
        }

        // ---------------------------------------------------------------------- stage 5b

        /// <summary>
        /// What the chosen method yields when it runs on its own thread, which is how the
        /// embedded game actually uses it.
        ///
        /// Stage 5 measures cost per frame on the main thread. This measures throughput off it:
        /// two seconds of captures on <see cref="EtsCaptureThread"/>, reported as frames a
        /// second and average milliseconds, while the main thread does nothing but count. It is
        /// the number that says whether the picture in Somnium will be smooth.
        /// </summary>
        private IEnumerator Stage5b_ThreadedThroughput(IntPtr gameWindow, EtsWindowCapture.Method method)
        {
            _log.AppendLine();
            _log.AppendLine("5b. Threaded capture (" + method + ")");

            EtsCaptureThread thread = null;

            try
            {
                thread = new EtsCaptureThread(gameWindow, method, 120);
                thread.Start();
            }
            catch (Exception e)
            {
                _log.AppendLine("   FAIL - " + e.GetType().Name + ": " + e.Message);
                yield break;
            }

            byte[] frame = null;
            int taken = 0;
            float started = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup - started < 2f)
            {
                if (thread.TryTake(ref frame, out _, out _))
                {
                    taken++;
                }

                yield return null;
            }

            float seconds = Time.realtimeSinceStartup - started;
            long captured = thread.Frames;
            string error = thread.Error;
            double average = thread.AverageMilliseconds;

            thread.Dispose();

            if (error != null)
            {
                _log.AppendLine("   FAIL - the capture thread stopped: " + error);
                yield break;
            }

            _log.AppendLine("   " + captured + " frames in " + seconds.ToString("0.0") + " s = " +
                            (captured / seconds).ToString("0.0") + " fps, " +
                            average.ToString("0.00") + " ms average; main thread collected " + taken + ".");

            if (captured < 20)
            {
                _log.AppendLine("   That is too few to look like motion. Try the OpenGL renderer option.");
            }
        }

        // ---------------------------------------------------------------------- stage 8

        /// <summary>
        /// Does the chosen method still return a picture once the window is parked off-screen?
        ///
        /// Everything before this measured the window where the player could see it. The
        /// embedded game moves it to -6000,-6000 and relies on DWM still composing it there.
        /// That was measured for BitBlt against Age of Mythology; for the DWM path it is
        /// measured here, because it is a different mechanism and an assumption is not a
        /// measurement. The window is put back afterwards.
        /// </summary>
        private IEnumerator Stage8_OffscreenCapture(IntPtr gameWindow, EtsWindowCapture capture)
        {
            _log.AppendLine();
            _log.AppendLine("8. Capture while parked off-screen");

            if (!GetWindowRect(gameWindow, out NativeRect rect))
            {
                _log.AppendLine("   INCONCLUSIVE - could not read the window position.");
                yield break;
            }

            EtsProcess.MoveWindow(gameWindow, -6000, -6000);

            // Let DWM settle. A capture taken during the move itself reports the old surface.
            yield return new WaitForSeconds(0.75f);

            float best = 0f;
            bool anyDrawn = false;

            for (int i = 0; i < 6; i++)
            {
                if (capture.Capture())
                {
                    anyDrawn = true;
                    best = Mathf.Max(best, capture.NonBlackFraction());
                }

                yield return null;
            }

            EtsProcess.MoveWindow(gameWindow, rect.Left, rect.Top);

            _log.AppendLine("   " + capture.Mode + " at -6000,-6000: " +
                            (anyDrawn ? (best * 100f).ToString("0.0") + "% non-black" : "every capture FAILED"));

            if (anyDrawn && best >= 0.05f)
            {
                _log.AppendLine("   the picture survives parking. Composited mode will work.");
                _stagesPassed++;
            }
            else
            {
                _log.AppendLine("   nothing comes back once the window is off-screen. The embedded game");
                _log.AppendLine("   would show black; keep it on-screen behind Somnium, or use Foreground.");
            }
        }

        /// <summary>
        /// Cheap order-sensitive hash over a strided sample of the frame. Only ever compared for
        /// equality against another hash of the same buffer size, so collision quality matters
        /// far less than not walking eight megabytes twice.
        /// </summary>
        private static int Hash(byte[] pixels)
        {
            if (pixels == null)
            {
                return 0;
            }

            unchecked
            {
                int hash = 17;
                int stride = Mathf.Max(1, pixels.Length / 8192);

                for (int i = 0; i < pixels.Length; i += stride)
                {
                    hash = hash * 31 + pixels[i];
                }

                return hash;
            }
        }

        // ---------------------------------------------------------------------- reporting

        private void Finish()
        {
            _log.AppendLine();
            _log.AppendLine(_stagesPassed + " of 8 stages passed.");

            if (_closeGameAfterwards && _process != null)
            {
                // Only a game this probe started. One the player already had open is theirs.
                _process.Shutdown(true);
                _log.AppendLine("Closed the game.");
            }

            Publish();
        }

        private void Publish()
        {
            string text = _log.ToString();

            if (_output != null)
            {
                _output.text = text;
            }

            Debug.Log("[SomniumEuroTruck] " + text);
        }

        /// <summary>
        /// Puts config.cfg back. A probe that leaves the player's game permanently windowed at a
        /// resolution they did not choose has overstepped, however useful its report was. Waits
        /// for a game this probe started to exit first, because the engine writes config.cfg on
        /// the way out; a game that is still running keeps its backup for the next launch.
        /// </summary>
        private void OnDestroy()
        {
            if (!_wroteConfig)
            {
                return;
            }

            if (_process != null && _process.LaunchAttempted && _closeGameAfterwards)
            {
                EtsProcess.WaitForExit(8000);
            }

            if (EtsProcess.IsRunning())
            {
                Debug.LogWarning("[SomniumEuroTruck] the game is still running; config.cfg backup kept " +
                                 "for the next launch to restore.");
                return;
            }

            EtsStartupOptions.RestoreConfig(_configPath);
        }
    }
}
