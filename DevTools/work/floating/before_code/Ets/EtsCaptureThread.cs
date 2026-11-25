using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Captures the game window on its own thread, so that what capture costs is paid there and
    /// not out of Somnium's frame.
    ///
    /// This exists because of one number. The capability probe, run against the real game on
    /// this machine, found that the DX11 window presents through a flip-model swap chain: a plain
    /// BitBlt returns black, and the only method that returns the picture is PrintWindow with
    /// PW_RENDERFULLCONTENT, at 20.5 ms per 1920x1057 frame. That is more than a whole 60 fps
    /// frame, and it would have been paid on the main thread, every frame, by a client that is
    /// also rendering a shared world and everyone's avatars. Age of Mythology got away with a
    /// 2.45 ms BitBlt on the main thread; this game does not.
    ///
    /// So the loop lives here. GDI objects are created and used on this thread only, the main
    /// thread never touches them, and the two sides hand buffers back and forth under a lock:
    /// three buffers rotate between "being written", "ready" and "being uploaded", so neither
    /// side ever waits for the other and no frame is ever copied. The main thread's whole cost
    /// is the texture upload, which is a millisecond or two.
    ///
    /// The frame rate this yields is whatever one capture costs allows - roughly 45 a second at
    /// 20 ms - and it is decoupled from Somnium's. A Somnium frame that finds no new picture
    /// simply keeps showing the last one, which is invisible at these rates.
    /// </summary>
    public sealed class EtsCaptureThread : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        private readonly IntPtr _window;
        private readonly EtsWindowCapture.Method _method;
        private readonly int _targetFps;

        private readonly object _gate = new object();

        private Thread _thread;
        private volatile bool _stop;

        // The three buffers. _ready is the newest complete frame waiting for the main thread;
        // _spare is a buffer the main thread has finished with and the capture thread may reuse.
        // The third is whichever one the capture thread is writing into at the moment.
        private byte[] _ready;
        private byte[] _spare;
        private int _readyWidth;
        private int _readyHeight;
        private bool _readyFresh;

        // Throughput, written by the capture thread and read by anyone. Reported rather than
        // assumed, because a number measured on one GPU is not a law on another.
        private long _frames;
        private double _lastMilliseconds;
        private double _averageMilliseconds;

        /// <summary>Set when the window stopped existing. The loop has ended by then.</summary>
        public bool WindowGone { get; private set; }

        /// <summary>What went wrong on the thread, if anything did. Null while all is well.</summary>
        public string Error { get; private set; }

        /// <summary>Frames captured so far.</summary>
        public long Frames => Interlocked.Read(ref _frames);

        /// <summary>Cost of the most recent capture, in milliseconds.</summary>
        public double LastMilliseconds => _lastMilliseconds;

        /// <summary>Running average cost of a capture, in milliseconds.</summary>
        public double AverageMilliseconds => _averageMilliseconds;

        /// <summary>The method the thread is using.</summary>
        public EtsWindowCapture.Method Method => _method;

        /// <param name="window">The game's render window.</param>
        /// <param name="method">The capture method, chosen by a probe on the main thread.</param>
        /// <param name="targetFps">An upper bound on captures a second. The real rate is the
        /// lower of this and what one capture costs allows.</param>
        public EtsCaptureThread(IntPtr window, EtsWindowCapture.Method method, int targetFps)
        {
            _window = window;
            _method = method;
            _targetFps = Math.Max(1, targetFps);
        }

        public void Start()
        {
            if (_thread != null)
            {
                return;
            }

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "EtsCapture"
            };

            _thread.Start();
        }

        /// <summary>
        /// Hands the newest frame to the caller, taking the caller's previous buffer back for
        /// reuse. Returns false when nothing new has arrived since the last call, in which case
        /// the caller's buffer is untouched.
        /// </summary>
        /// <param name="buffer">The caller's buffer. Replaced with the new frame on success; the
        /// old one goes back into rotation.</param>
        public bool TryTake(ref byte[] buffer, out int width, out int height)
        {
            lock (_gate)
            {
                if (!_readyFresh || _ready == null)
                {
                    width = 0;
                    height = 0;
                    return false;
                }

                byte[] taken = _ready;
                width = _readyWidth;
                height = _readyHeight;

                _ready = null;
                _readyFresh = false;

                // The buffer the caller is done with becomes the spare, unless a spare is
                // already waiting - then it is simply dropped and collected, which only happens
                // after a resize when sizes no longer match anyway.
                if (buffer != null && _spare == null)
                {
                    _spare = buffer;
                }

                buffer = taken;
                return true;
            }
        }

        private void Run()
        {
            EtsWindowCapture capture = null;

            try
            {
                capture = new EtsWindowCapture(_window) { Mode = _method };

                double intervalMs = 1000.0 / _targetFps;
                byte[] writing = null;
                var clock = new Stopwatch();

                while (!_stop)
                {
                    clock.Restart();

                    if (!IsWindow(_window))
                    {
                        WindowGone = true;
                        break;
                    }

                    if (capture.NeedsResize() && !capture.Resize())
                    {
                        WindowGone = true;
                        break;
                    }

                    int needed = capture.Width * capture.Height * 4;

                    if (writing == null || writing.Length != needed)
                    {
                        lock (_gate)
                        {
                            writing = _spare != null && _spare.Length == needed ? _spare : null;
                            _spare = null;
                        }

                        if (writing == null)
                        {
                            writing = new byte[needed];
                        }
                    }

                    bool drawn = capture.Capture(writing);
                    double cost = clock.Elapsed.TotalMilliseconds;

                    if (drawn)
                    {
                        _lastMilliseconds = cost;
                        long n = Interlocked.Increment(ref _frames);
                        _averageMilliseconds += (cost - _averageMilliseconds) / Math.Min(n, 120);

                        lock (_gate)
                        {
                            // A frame the main thread never collected is simply replaced. Its
                            // buffer goes back into rotation rather than being dropped.
                            if (_ready != null && _spare == null)
                            {
                                _spare = _ready;
                            }

                            _ready = writing;
                            _readyWidth = capture.Width;
                            _readyHeight = capture.Height;
                            _readyFresh = true;

                            writing = _spare != null && _spare.Length == needed ? _spare : null;
                            _spare = null;
                        }
                    }

                    // Pace to the target, but never spin: a minimised window fails every capture
                    // instantly, and without the floor this loop would eat a core doing nothing.
                    double remaining = intervalMs - clock.Elapsed.TotalMilliseconds;
                    Thread.Sleep(remaining > 1.0 ? (int)remaining : 1);
                }
            }
            catch (Exception e)
            {
                Error = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                capture?.Dispose();
            }
        }

        public void Dispose()
        {
            _stop = true;

            if (_thread != null)
            {
                // Bounded, because this runs from OnDestroy and a capture that is mid-DWM call
                // finishes on its own time; a background thread that is still finishing is
                // harmless, a main thread stuck waiting for it is not.
                _thread.Join(500);
                _thread = null;
            }

            lock (_gate)
            {
                _ready = null;
                _spare = null;
                _readyFresh = false;
            }
        }
    }
}
