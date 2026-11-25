using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Runs the player's own Euro Truck Simulator 2 inside a Somnium world, flatscreen first.
    ///
    /// The game is never redistributed. This starts a copy the player already owns - the full
    /// game or the free demo - and says so plainly when there is not one.
    ///
    /// How it works, and why this shape:
    ///
    /// The game is started windowed and moved off every monitor, where DWM keeps composing it.
    /// Its window is copied each frame and drawn as a full-screen texture in Somnium, so what the
    /// player sees is Somnium's picture of the game rather than the game's own window. That is
    /// what lets the Somnium tablet sit over it with real transparency, and it is the same idea
    /// Somnium Quake and Somnium Mythology used. Against Age of Mythology - D3D11, like this
    /// game - a plain GDI blit returned the full picture in 2.45 ms at 1920x1080; see
    /// <see cref="EtsWindowCapture"/>, which re-measures on every launch rather than trusting
    /// that number.
    ///
    /// Windowed is not a preference, it is the whole thing. The engine's log on this machine
    /// shows it entering exclusive fullscreen through DXGI, and nothing can composite one of
    /// those. There is no windowed switch on the command line, so the setting has to arrive
    /// through config.cfg in the player's Documents folder or not at all.
    /// <see cref="EtsStartupOptions"/> carries the detail, and the backup.
    ///
    /// Input is not synthesised. The hidden window is simply given focus, and Windows delivers
    /// keyboard and mouse to the focused window whether or not it is visible. What makes that
    /// plausible for this game is that it reads its devices through DirectInput - the log names
    /// di8 for both the keyboard and the mouse - so it takes mouse movement as deltas from the
    /// device rather than asking where the Windows cursor is. The Windows cursor is over Somnium,
    /// thousands of pixels from the game's client area, and the engine draws its own cursor into
    /// its own frame, which the capture picks up. Whether the menus are driven the same way is
    /// what the capability probe reports; the one thing that could go wrong there is a click
    /// landing on Somnium and moving focus, and <see cref="Guard"/> exists for exactly that.
    ///
    /// If capture turns out not to work on a machine, the world does not pretend otherwise: it
    /// switches to Foreground mode, where the real game window is put in front at its real size
    /// and the hotkey brings Somnium back. Less elegant, still playable, and honest about it.
    /// </summary>
    public class EtsEmbeddedGame : MonoBehaviour
    {
        /// <summary>How the game is shown.</summary>
        public enum Display
        {
            /// <summary>Hidden window, frames drawn by Somnium. The one worth having.</summary>
            Composited,

            /// <summary>Real window in front. The fallback when capture returns nothing.</summary>
            Foreground
        }

        [Header("Euro Truck Simulator 2")]
        [Tooltip("Install folder - the one holding base.scs and bin. Leave empty to search every " +
                 "Steam library on this machine for the full game, then for the demo.")]
        [SerializeField]
        private string _installRoot = "";

        [Tooltip("The game's settings folder, where config.cfg lives. Leave empty for the " +
                 "default, Documents\\Euro Truck Simulator 2.")]
        [SerializeField]
        private string _settingsFolder = "";

        [Tooltip("Start through Steam rather than by running eurotrucks2.exe directly. Steam " +
                 "ignores the command line, so -nointro only applies when this is off.")]
        [SerializeField]
        private bool _launchThroughSteam;

        [SerializeField]
        private EtsStartupOptions _options = new EtsStartupOptions();

        [Tooltip("Write the display settings into config.cfg before launching. This is the only " +
                 "way to get windowed mode - there is no command-line switch for it - so with " +
                 "this off the game starts in exclusive fullscreen and cannot be captured at all." +
                 "\n\nIt edits a file in the player's Documents folder. The original is backed up " +
                 "first, and only the keys this world changed are put back when the game closes.")]
        [SerializeField]
        private bool _mayWriteConfig = true;

        [Header("Controls")]
        [Tooltip("Virtual-key code that moves input between the game and Somnium. 0x70 = F1, " +
                 "the same key as the other Somnium game worlds.\n\nThe game binds F1 to pause, " +
                 "which turns out to suit: pressing it to reach the tablet also parks the truck. " +
                 "Press it once more in the game to drive on. Avoid F2-F9, which the game uses.")]
        [SerializeField]
        private int _toggleKey = 0x70;

        [Tooltip("Start with input in the game rather than in Somnium.")]
        [SerializeField]
        private bool _startInGame = true;

        [Tooltip("While the game has input, stop Somnium's own window from taking focus when a " +
                 "click lands on it. The game reads the mouse through DirectInput and never asks " +
                 "where the Windows cursor is - but the Windows cursor is still over Somnium, and " +
                 "without this a click would hand focus back to Somnium mid-drive.\n\nSomnium " +
                 "may drop off the taskbar while this is in effect. It comes back on toggle.")]
        [SerializeField]
        private bool _guardSomniumWindow = true;

        [Tooltip("While the game has input, pin the Windows cursor to the top-left pixel of the " +
                 "screen so it cannot wander over Somnium's UI. Off by default: if the game's " +
                 "menus turn out to follow the Windows cursor, this would make them unusable. " +
                 "Try it if hovering Somnium's UI during a drive is a nuisance.")]
        [SerializeField]
        private bool _parkCursor;

        [Header("Display")]
        [Tooltip("Canvas sorting order for the game screen. Negative keeps it behind Somnium's " +
                 "own UI, which is what lets the tablet composite over it.")]
        [SerializeField]
        private int _sortingOrder = -100;

        [Tooltip("Dim the game while the tablet is open, so the UI stays readable over it.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float _tabletDimming = 0.45f;

        [Tooltip("Letterbox rather than stretch when the game's aspect does not match the " +
                 "Somnium window. Mirrors and the route advisor are read by shape, and a " +
                 "stretched picture quietly lies about both.")]
        [SerializeField]
        private bool _preserveAspect = true;

        [Header("Advanced")]
        [Tooltip("Where the game window is parked, off every monitor. DWM keeps composing a " +
                 "window that is off-screen, so capture continues to work there.")]
        [SerializeField]
        private Vector2Int _offscreenPosition = new Vector2Int(-6000, -6000);

        [Tooltip("Seconds to wait for the game window before giving up. Generous, because a cold " +
                 "Steam start has to bring Steam up first, and the engine mounts thirteen " +
                 "gigabytes of archives before it opens anything.")]
        [Range(10f, 180f)]
        [SerializeField]
        private float _windowTimeout = 120f;

        [Tooltip("Seconds to wait after the window appears before probing capture. The first " +
                 "frames are a blank swap chain; probing against those scores every method at " +
                 "nearly zero and picks arbitrarily.")]
        [Range(0f, 10f)]
        [SerializeField]
        private float _settleSeconds = 2.5f;

        [Tooltip("Upper bound on captures a second, on the capture thread. The real rate is the " +
                 "lower of this and what one capture costs: measured here, the DWM path costs " +
                 "about 20 ms, so expect roughly 45. Somnium's own frame rate is unaffected " +
                 "either way - that is what the thread is for.")]
        [Range(15, 120)]
        [SerializeField]
        private int _captureFps = 60;

        [Header("Output")]
        [SerializeField]
        private TMP_Text _status;

        [SerializeField]
        private bool _launchOnStart = true;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(ref NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr rect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attaching);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr window);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>
        /// Moves the foreground to a window, and insists when Windows demurs.
        ///
        /// SetForegroundWindow is only honoured for the process that received the last input
        /// event, and once the game has focus that process is the game. Somnium, polling a
        /// hotkey through GetAsyncKeyState, has received nothing - so the plain call can be
        /// refused and answered with a flashing taskbar button instead of a focus change. The
        /// standard remedy is to attach this thread's input queue to the current foreground
        /// window's thread for the duration of the call, which makes the request come from the
        /// thread that is allowed to make it.
        /// </summary>
        private static void BringToFront(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return;
            }

            if (SetForegroundWindow(window) && GetForegroundWindow() == window)
            {
                return;
            }

            IntPtr current = GetForegroundWindow();
            uint foregroundThread = current == IntPtr.Zero ? 0 : GetWindowThreadProcessId(current, out _);
            uint ourThread = GetCurrentThreadId();
            bool attached = false;

            try
            {
                if (foregroundThread != 0 && foregroundThread != ourThread)
                {
                    attached = AttachThreadInput(ourThread, foregroundThread, true);
                }

                BringWindowToTop(window);
                SetForegroundWindow(window);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(ourThread, foregroundThread, false);
                }
            }
        }

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000L;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private EtsProcess _process;
        private EtsWindowCapture _capture;
        private EtsCaptureThread _thread;
        private byte[] _frame;

        private IntPtr _gameWindow;
        private IntPtr _somniumWindow;
        private Vector2Int _originalWindowPosition;

        private Texture2D _screen;
        private RawImage _screenImage;
        private Canvas _canvas;

        private string _resolvedRoot;
        private string _configPath;
        private bool _wroteConfig;
        private bool _guarded;
        private bool _parked;
        private bool _inGame;
        private bool _togglePressed;
        private bool _running;

        /// <summary>True while input is going to the game rather than to Somnium.</summary>
        public bool IsInGame => _inGame;

        /// <summary>How the game ended up being shown, once it is running.</summary>
        public Display Mode { get; private set; } = Display.Composited;

        private void Start()
        {
            // Must be set before the game ever takes focus. Without it Unity stops updating the
            // instant it does, which freezes the avatar, drops voice for everyone else in the
            // room, and stops this component along with all of it.
            Application.runInBackground = true;
            _somniumWindow = OwnWindow();

            if (_launchOnStart)
            {
                Launch();
            }
        }

        /// <summary>
        /// The window this process draws into - Somnium's, or the editor's when testing.
        ///
        /// Asked of the process rather than read as "whatever is in front", because a world can
        /// finish loading while the player is looking at something else, and remembering that
        /// something else as Somnium would hand focus to the wrong application forever after.
        /// </summary>
        private static IntPtr OwnWindow()
        {
            try
            {
                IntPtr main = Process.GetCurrentProcess().MainWindowHandle;

                if (main != IntPtr.Zero)
                {
                    return main;
                }
            }
            catch (Exception)
            {
                // Fall through to the foreground window.
            }

            return GetForegroundWindow();
        }

        public void Launch()
        {
            if (_running)
            {
                Report("Already running.");
                return;
            }

            StartCoroutine(LaunchRoutine());
        }

        private IEnumerator LaunchRoutine()
        {
            _running = true;

            _resolvedRoot = EtsInstall.Locate(string.IsNullOrEmpty(_installRoot) ? null : _installRoot);

            if (_resolvedRoot == null)
            {
                Report(EtsInstall.Describe(null));
                _running = false;
                yield break;
            }

            string home = EtsInstall.HomeFolder(string.IsNullOrEmpty(_settingsFolder) ? null : _settingsFolder);
            _configPath = EtsInstall.ConfigPath(home);

            // Written before launching, because the engine reads config.cfg once at startup and
            // then builds its swap chain. Anything written after this point arrives too late.
            if (_mayWriteConfig)
            {
                // A backup left behind by an earlier session that never got to put things back -
                // a crash, a hard quit - is restored first, so today's edit starts from the
                // player's real settings rather than from last time's.
                if (EtsStartupOptions.HasPendingRestore(_configPath) && !EtsProcess.IsRunning())
                {
                    EtsStartupOptions.RestoreConfig(_configPath);
                    Debug.Log("[SomniumEuroTruck] restored config.cfg from an earlier session's backup.");
                }

                if (_options.ApplyToConfig(_configPath, out string failure))
                {
                    _wroteConfig = true;
                    Debug.Log("[SomniumEuroTruck] " + _options.Describe());
                }
                else
                {
                    Debug.LogWarning("[SomniumEuroTruck] " + failure);
                }
            }

            Report("Starting Euro Truck Simulator 2 ...");

            if (!EtsProcess.IsRunning())
            {
                _process = new EtsProcess();

                string commandLine = _launchThroughSteam ? string.Empty : _options.CommandLine();

                if (!_process.Launch(_resolvedRoot, commandLine, _launchThroughSteam, out string failure))
                {
                    Report(failure + "\n\nThis world runs your own copy of the game - it does not include it.");
                    _running = false;
                    yield break;
                }
            }
            else
            {
                Debug.Log("[SomniumEuroTruck] the game is already running - adopting it rather than starting a second copy.");
            }

            // The window is found by searching rather than taken off the launched process,
            // because a Steam launch can hand off to a different process. See EtsProcess.
            //
            // Polled ten times a second, not twice: the window is parked the moment it is seen,
            // and every poll interval before that is time it sits in front of the player.
            float deadline = Time.realtimeSinceStartup + _windowTimeout;
            float lastSeenRunning = Time.realtimeSinceStartup;
            EtsProcess.WindowInfo window = default;

            while (Time.realtimeSinceStartup < deadline)
            {
                if (!_running)
                {
                    yield break;
                }

                window = EtsProcess.FindGameWindow();

                if (window.Handle != IntPtr.Zero)
                {
                    break;
                }

                // A game that dies during startup would otherwise be waited on for the full
                // timeout. Five seconds of no eurotrucks2 process at all is the signal - not one
                // poll, because a Steam hand-off has a gap between the first process exiting
                // and the real one appearing.
                if (EtsProcess.IsRunning())
                {
                    lastSeenRunning = Time.realtimeSinceStartup;
                }
                else if (_process != null && Time.realtimeSinceStartup - lastSeenRunning > 5f)
                {
                    Report("The game exited during startup.\n" + (_process.HandoffReport()) +
                           "\nCheck the game's own log: Documents\\Euro Truck Simulator 2\\game.log.txt");
                    _running = false;
                    yield break;
                }

                Report("Waiting for the game to open its window ...");
                yield return new WaitForSeconds(0.1f);
            }

            if (window.Handle == IntPtr.Zero)
            {
                Report("The game did not open a window within " + (int)_windowTimeout + " seconds.\n" +
                       "If Steam is asking for something, deal with it and try again.");
                _running = false;
                yield break;
            }

            _gameWindow = window.Handle;

            if (GetWindowRect(_gameWindow, out NativeRect rect))
            {
                _originalWindowPosition = new Vector2Int(rect.Left, rect.Top);
            }

            // Parked straight away, before capture is proven, and moved back if it is not. The
            // earlier order - prove first, hide after - meant several seconds of the real window
            // in front of Somnium on every launch, which is the one thing this world exists to
            // avoid. Moving it back on failure keeps the guarantee that a player is never left
            // with an invisible game.
            EtsProcess.MoveWindow(_gameWindow, _offscreenPosition.x, _offscreenPosition.y);
            Report("Game found - loading ...");

            yield return new WaitForSeconds(_settleSeconds);

            if (!_running)
            {
                yield break;
            }

            _capture = new EtsWindowCapture(_gameWindow);
            bool captureWorks = ProbeCapture("parked off-screen");

            if (!captureWorks)
            {
                // Measured against Age of Mythology, an off-screen window captures exactly like
                // an on-screen one. Not proven for this game and this method until it is, so a
                // failure gets one more try where the player can see the window, and the report
                // says which of the two it was.
                EtsProcess.MoveWindow(_gameWindow, _originalWindowPosition.x, _originalWindowPosition.y);
                yield return new WaitForSeconds(0.75f);

                if (!_running)
                {
                    yield break;
                }

                captureWorks = ProbeCapture("on-screen");

                if (captureWorks)
                {
                    Debug.LogWarning("[SomniumEuroTruck] capture works on-screen but not parked " +
                                     "off-screen - DWM is not composing the parked window on this " +
                                     "machine. Try an offscreen position just past the desktop edge.");
                    captureWorks = false;
                }
            }

            if (captureWorks)
            {
                Mode = Display.Composited;
                CreateScreen(_capture.Width, _capture.Height);

                // The probe's capture object has done its job. From here on every capture
                // happens on the thread, which owns its own GDI objects; the main thread only
                // ever uploads the frames it is handed. See EtsCaptureThread for the number
                // that makes this necessary.
                EtsWindowCapture.Method method = _capture.Mode;
                int width = _capture.Width;
                int height = _capture.Height;

                _capture.Dispose();
                _capture = null;

                _thread = new EtsCaptureThread(_gameWindow, method, _captureFps);
                _thread.Start();

                Report("Euro Truck Simulator 2 running at " + width + "x" + height +
                       " (" + method + ", threaded)\n" +
                       "Press " + KeyName(_toggleKey) + " for Somnium.");
            }
            else
            {
                // No method returned a picture. Rather than showing a black rectangle and calling
                // it success, put the real window in front and keep the same hotkey.
                Mode = Display.Foreground;
                _capture.Dispose();
                _capture = null;

                EtsProcess.MoveWindow(_gameWindow, _originalWindowPosition.x, _originalWindowPosition.y);

                Report("This machine will not let Somnium copy the game's picture, so the game " +
                       "window is being shown in front instead.\n" +
                       "Press " + KeyName(_toggleKey) + " to come back to Somnium.");
            }

            SetMode(_startInGame);
        }

        /// <summary>
        /// Runs the capture probe against the current window position and says whether a real
        /// picture came back. Logs every method's score, because the numbers are the diagnosis
        /// when the answer is no.
        /// </summary>
        private bool ProbeCapture(string where)
        {
            EtsWindowCapture.Score[] scores = _capture.Probe();

            foreach (EtsWindowCapture.Score score in scores)
            {
                Debug.Log("[SomniumEuroTruck] capture " + where + " - " + score);
            }

            return _capture.Capture() && _capture.NonBlackFraction() >= 0.05f;
        }

        /// <summary>
        /// Builds the surface the game is drawn on. A screen-space overlay canvas with a negative
        /// sorting order sits behind Somnium's own UI, so the tablet composites over the game
        /// instead of being hidden by it.
        /// </summary>
        private void CreateScreen(int width, int height)
        {
            CreateScreenTexture(width, height);

            var canvasObject = new GameObject("EtsScreenCanvas");
            canvasObject.transform.SetParent(transform, false);

            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = _sortingOrder;

            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            var imageObject = new GameObject("EtsScreenImage");
            imageObject.transform.SetParent(canvasObject.transform, false);

            _screenImage = imageObject.AddComponent<RawImage>();
            _screenImage.texture = _screen;
            _screenImage.raycastTarget = false;

            RectTransform rect = _screenImage.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            if (_preserveAspect)
            {
                var fitter = imageObject.AddComponent<AspectRatioFitter>();
                fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fitter.aspectRatio = (float)width / Mathf.Max(1, height);
            }
        }

        private void Update()
        {
            if (!_running)
            {
                return;
            }

            PollToggleKey();

            // Only once there is a window to lose. Before the launch has found one the handle is
            // empty, and an empty handle is not a closed game - testing it as one switched this
            // component off while the game was still loading, and left a texture nothing ever
            // drew into.
            if (_gameWindow != IntPtr.Zero && !EtsProcess.StillAlive(_gameWindow))
            {
                Report("Euro Truck Simulator 2 has closed.");
                Shutdown();
                return;
            }

            UpdateScreen();
        }

        /// <summary>
        /// Reads the toggle key from global key state rather than through Unity's input, because
        /// while the game holds focus Unity receives no input events at all.
        /// </summary>
        private void PollToggleKey()
        {
            bool down = (GetAsyncKeyState(_toggleKey) & 0x8000) != 0;

            // Edge-triggered. GetAsyncKeyState reports a held key every frame, which would
            // otherwise flip the mode dozens of times per press.
            if (down && !_togglePressed)
            {
                SetMode(!_inGame);
            }

            _togglePressed = down;
        }

        /// <summary>
        /// Uploads the newest frame the capture thread has produced, if there is one.
        ///
        /// A Somnium frame that finds nothing new keeps the previous picture. Blanking would turn
        /// the ordinary gap between two captures into a visible flash, and at the rates involved
        /// a repeated frame is invisible.
        /// </summary>
        private void UpdateScreen()
        {
            if (_thread == null || _screen == null)
            {
                return;
            }

            if (_thread.Error != null)
            {
                Report("Capture stopped: " + _thread.Error);
                _thread.Dispose();
                _thread = null;
                return;
            }

            if (!_thread.TryTake(ref _frame, out int width, out int height))
            {
                return;
            }

            // The game changed resolution from its own options screen, or the window was
            // restored at a different size. The thread has already rebuilt its side; the texture
            // follows the first frame that arrives at the new size.
            if (width != _screen.width || height != _screen.height)
            {
                Destroy(_screen);
                CreateScreenTexture(width, height);

                if (_preserveAspect && _screenImage != null)
                {
                    var fitter = _screenImage.GetComponent<AspectRatioFitter>();

                    if (fitter != null)
                    {
                        fitter.aspectRatio = (float)width / Mathf.Max(1, height);
                    }
                }
            }

            _screen.LoadRawTextureData(_frame);
            _screen.Apply(false);
        }

        private void CreateScreenTexture(int width, int height)
        {
            _screen = new Texture2D(width, height, TextureFormat.BGRA32, false)
            {
                name = "EtsScreen",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            if (_screenImage != null)
            {
                _screenImage.texture = _screen;
            }
        }

        /// <summary>
        /// Switches which application owns input. This is the whole mode system: focus follows
        /// the mode, and the game keeps rendering either way, so the tablet appears over a live
        /// drive rather than a frozen picture.
        /// </summary>
        public void SetMode(bool inGame)
        {
            _inGame = inGame;

            if (inGame)
            {
                if (EtsProcess.StillAlive(_gameWindow))
                {
                    Guard(true);
                    BringToFront(_gameWindow);
                    ParkCursor(_parkCursor);
                }
            }
            else
            {
                ParkCursor(false);
                Guard(false);

                // Released unconditionally, not only when this component parked it. The game
                // clips the cursor to its own client area while it has focus, and that area is
                // now six thousand pixels off the desktop; a game that does not lift the clip on
                // losing focus would leave the player's cursor pinned to a screen corner.
                ClipCursor(IntPtr.Zero);

                BringToFront(_somniumWindow);
            }

            if (_screenImage != null)
            {
                // Dimmed rather than hidden, so the drive stays visible behind the tablet.
                float shade = inGame ? 1f : 1f - _tabletDimming;
                _screenImage.color = new Color(shade, shade, shade, 1f);
            }
        }

        /// <summary>
        /// Keeps Somnium's window from taking focus on a click while the game has input.
        ///
        /// WS_EX_NOACTIVATE is the documented way to have a window that does not become the
        /// foreground window when the user clicks it, and SetForegroundWindow still works on it -
        /// which is what the hotkey needs. Set on our own process's window only, and taken off
        /// again the moment input goes back to Somnium.
        /// </summary>
        private void Guard(bool on)
        {
            if (!_guardSomniumWindow || _somniumWindow == IntPtr.Zero || on == _guarded)
            {
                return;
            }

            try
            {
                long style = GetWindowLongPtr(_somniumWindow, GWL_EXSTYLE).ToInt64();
                style = on ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
                SetWindowLongPtr(_somniumWindow, GWL_EXSTYLE, new IntPtr(style));
                _guarded = on;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Could not change the Somnium window style: " + e.Message);
            }
        }

        /// <summary>Pins the Windows cursor to one pixel, or lets it go. See the inspector note.</summary>
        private void ParkCursor(bool on)
        {
            if (on == _parked)
            {
                return;
            }

            try
            {
                if (on)
                {
                    var corner = new NativeRect { Left = 0, Top = 0, Right = 1, Bottom = 1 };
                    ClipCursor(ref corner);
                }
                else
                {
                    ClipCursor(IntPtr.Zero);
                }

                _parked = on;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Could not clip the cursor: " + e.Message);
            }
        }

        private static string KeyName(int virtualKey)
        {
            if (virtualKey >= 0x70 && virtualKey <= 0x7B)
            {
                return "F" + (virtualKey - 0x6F);
            }

            return virtualKey == 0x09 ? "TAB" : "key 0x" + virtualKey.ToString("X2");
        }

        private void Report(string message)
        {
            if (_status != null)
            {
                _status.text = message;
            }

            Debug.Log("[SomniumEuroTruck] " + message);
        }

        private void Shutdown()
        {
            _running = false;

            _thread?.Dispose();
            _thread = null;
            _frame = null;

            _capture?.Dispose();
            _capture = null;

            if (_canvas != null)
            {
                Destroy(_canvas.gameObject);
                _canvas = null;
            }

            // Put the window back where it was, so a game that outlives the world is not left
            // parked off-screen where the player cannot reach it.
            EtsProcess.MoveWindow(_gameWindow, _originalWindowPosition.x, _originalWindowPosition.y);

            ParkCursor(false);
            Guard(false);
            ClipCursor(IntPtr.Zero);

            // Hand focus back, or the player is left looking at a Somnium window that does not
            // respond because focus is somewhere they cannot see.
            BringToFront(_somniumWindow);
        }

        /// <summary>
        /// Closes down with the world: the game, if this world started it, and config.cfg, if
        /// this world wrote it. A game the player already had open is left alone.
        ///
        /// The restore waits for the game to exit, because the engine writes config.cfg from
        /// memory on the way out and a restore that runs first is overwritten a second later.
        /// When the game is still running after the wait - or was not ours to close - the backup
        /// is left in place and picked up by the next launch instead.
        /// </summary>
        private void OnDestroy()
        {
            Shutdown();

            if (_process != null && _process.LaunchAttempted)
            {
                _process.Shutdown(true);
                EtsProcess.WaitForExit(8000);
            }

            if (!_wroteConfig)
            {
                return;
            }

            if (EtsProcess.IsRunning())
            {
                Debug.LogWarning("[SomniumEuroTruck] the game is still running, so config.cfg is left " +
                                 "as written and the backup kept; it is restored the next time " +
                                 "this world launches the game.");
                return;
            }

            EtsStartupOptions.RestoreConfig(_configPath);
        }
    }
}
