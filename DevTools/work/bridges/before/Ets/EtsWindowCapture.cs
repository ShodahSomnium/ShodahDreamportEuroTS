using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Copies Euro Truck Simulator 2's window into a pixel buffer using only Windows' own GDI and
    /// DWM entry points. Nothing native is shipped - user32 and gdi32 are on every Windows
    /// machine, which is the only reason this is usable from an uploaded Somnium world at all.
    ///
    /// This is the capture idea carried over from Somnium Quake and Somnium Mythology. The
    /// numbers below are theirs, not this game's: against Age of Mythology - a D3D11 game, like
    /// this one - every method returned the same picture and the cheapest was also the fastest:
    ///
    ///     BitBlt                  99.2% non-black    2.45 ms
    ///     PrintWindow             99.2% non-black    6.93 ms
    ///     PrintWindowFullContent  99.2% non-black   20.69 ms
    ///
    /// at 1920x1080, with the window parked at -6000,-6000 where no monitor covers it. ETS2 is
    /// DX11 through DXGI as well, so the expectation is the same ranking - but that is exactly
    /// the kind of thing that is measured rather than assumed. <see cref="Probe"/> runs all three
    /// against the live window and picks a winner on this machine, with this driver, against
    /// this build of the game, and <see cref="EtsLaunchSpike"/> reports the comparison.
    /// </summary>
    public sealed class EtsWindowCapture : IDisposable
    {
        /// <summary>How a frame is pulled off the game's window.</summary>
        public enum Method
        {
            /// <summary>Straight GDI blit of the window DC. Cheapest when it works.</summary>
            BitBlt,

            /// <summary>Ask the window to paint itself. Honours WM_PRINT, may miss D3D content.</summary>
            PrintWindow,

            /// <summary>Ask DWM for the composed surface. The fallback that reaches everything.</summary>
            PrintWindowFullContent
        }

        private const uint SRCCOPY = 0x00CC0020;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        private const uint DIB_RGB_COLORS = 0;
        private const uint BI_RGB = 0;

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hDC, uint flags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h,
            IntPtr src, int srcX, int srcY, uint rop);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hDC, IntPtr bitmap, uint start, uint lines,
            byte[] bits, ref BitmapInfo info, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;

            // A 32bpp BI_RGB bitmap carries no palette, but GetDIBits still reads the field.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] Colors;
        }

        private readonly IntPtr _window;

        private IntPtr _windowDc;
        private IntPtr _memoryDc;
        private IntPtr _bitmap;
        private IntPtr _previous;

        private BitmapInfo _info;
        private byte[] _pixels;
        private bool _disposed;

        /// <summary>Client width of the captured window, in pixels.</summary>
        public int Width { get; private set; }

        /// <summary>Client height of the captured window, in pixels.</summary>
        public int Height { get; private set; }

        /// <summary>
        /// The method currently used to pull frames. Defaults to the one measured fastest
        /// against a comparable game; <see cref="Probe"/> overwrites it with whatever wins here.
        /// </summary>
        public Method Mode { get; set; } = Method.BitBlt;

        /// <summary>
        /// Bottom-up BGRA32, which is what GetDIBits produces and what Unity's TextureFormat.BGRA32
        /// expects with the standard bottom-left origin. No row flip is needed.
        /// </summary>
        public byte[] Pixels => _pixels;

        public EtsWindowCapture(IntPtr window)
        {
            _window = window;
            Allocate();
        }

        /// <summary>
        /// Reads the window's client size and rebuilds every GDI object to match. Called on
        /// construction and again whenever the game changes resolution.
        /// </summary>
        private void Allocate()
        {
            Release();

            if (!GetClientRect(_window, out NativeRect client))
            {
                Width = 0;
                Height = 0;
                return;
            }

            Width = Math.Max(1, client.Right - client.Left);
            Height = Math.Max(1, client.Bottom - client.Top);

            _windowDc = GetWindowDC(_window);
            _memoryDc = CreateCompatibleDC(_windowDc);
            _bitmap = CreateCompatibleBitmap(_windowDc, Width, Height);
            _previous = SelectObject(_memoryDc, _bitmap);

            _info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader)),
                    Width = Width,

                    // Positive height asks GDI for a bottom-up DIB. Unity uploads texture data
                    // bottom-up too, so agreeing here avoids flipping several megabytes a frame.
                    Height = Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BI_RGB
                },
                Colors = new uint[4]
            };

            _pixels = new byte[Width * Height * 4];
        }

        /// <summary>
        /// True when the game's window no longer matches the buffers - a resolution change from
        /// the game's own options screen, or the window being restored at a different size.
        /// </summary>
        public bool NeedsResize()
        {
            if (!IsWindow(_window) || !GetClientRect(_window, out NativeRect client))
            {
                return false;
            }

            int width = Math.Max(1, client.Right - client.Left);
            int height = Math.Max(1, client.Bottom - client.Top);

            return width != Width || height != Height;
        }

        /// <summary>Rebuilds for the window's current size. False if the window has gone away.</summary>
        public bool Resize()
        {
            if (!IsWindow(_window))
            {
                return false;
            }

            Allocate();
            return Width > 0 && Height > 0;
        }

        /// <summary>
        /// Pulls one frame into <see cref="Pixels"/> using the current <see cref="Mode"/>.
        /// </summary>
        public bool Capture()
        {
            return Capture(_pixels);
        }

        /// <summary>
        /// Pulls one frame into a caller-owned buffer, which must be exactly
        /// Width * Height * 4 bytes.
        ///
        /// This is what the capture thread uses: it rotates a few buffers between itself and the
        /// main thread, and having GetDIBits write straight into the one being handed over saves
        /// an eight-megabyte copy per frame that the design would otherwise need.
        /// </summary>
        public bool Capture(byte[] destination)
        {
            if (_disposed || _memoryDc == IntPtr.Zero || !IsWindow(_window))
            {
                return false;
            }

            if (destination == null || destination.Length != Width * Height * 4)
            {
                return false;
            }

            // A minimised window has no composition surface for DWM to hand back, and BitBlt gets
            // the icon rather than the game. Report failure so the caller keeps the last frame
            // instead of flashing black.
            if (IsIconic(_window))
            {
                return false;
            }

            bool drawn;

            switch (Mode)
            {
                case Method.BitBlt:
                    drawn = BitBlt(_memoryDc, 0, 0, Width, Height, _windowDc, 0, 0, SRCCOPY);
                    break;

                case Method.PrintWindow:
                    drawn = PrintWindow(_window, _memoryDc, 0);
                    break;

                default:
                    drawn = PrintWindow(_window, _memoryDc, PW_RENDERFULLCONTENT);
                    break;
            }

            if (!drawn)
            {
                return false;
            }

            int lines = GetDIBits(_memoryDc, _bitmap, 0, (uint)Height, destination, ref _info, DIB_RGB_COLORS);
            return lines != 0;
        }

        /// <summary>
        /// Fraction of sampled pixels that are not black, in 0..1.
        ///
        /// This is the honest measure of whether a capture method actually works against this
        /// window. A method that "succeeds" and returns a black rectangle is the failure mode
        /// worth catching, and it is invisible to the boolean return of BitBlt or PrintWindow.
        ///
        /// Sampled on a stride rather than exhaustively, because it runs inside the probe loop
        /// and a full pass over a 4K frame is millions of comparisons for an answer that a few
        /// thousand samples already give.
        /// </summary>
        public float NonBlackFraction()
        {
            if (_pixels == null || _pixels.Length < 4)
            {
                return 0f;
            }

            int pixelCount = _pixels.Length / 4;
            int stride = Math.Max(1, pixelCount / 4096);
            int sampled = 0;
            int lit = 0;

            for (int i = 0; i < pixelCount; i += stride)
            {
                int p = i * 4;

                // Threshold rather than != 0: DWM hands back near-black noise on some drivers,
                // and a handful of value-3 pixels is not a picture.
                if (_pixels[p] > 8 || _pixels[p + 1] > 8 || _pixels[p + 2] > 8)
                {
                    lit++;
                }

                sampled++;
            }

            return sampled == 0 ? 0f : (float)lit / sampled;
        }

        /// <summary>What one method scored when probed.</summary>
        public readonly struct Score
        {
            public readonly Method Method;
            public readonly bool Succeeded;
            public readonly float NonBlack;
            public readonly double Milliseconds;

            public Score(Method method, bool succeeded, float nonBlack, double milliseconds)
            {
                Method = method;
                Succeeded = succeeded;
                NonBlack = nonBlack;
                Milliseconds = milliseconds;
            }

            public override string ToString()
            {
                return Method + ": " + (Succeeded ? "ok" : "FAILED") +
                       ", " + (NonBlack * 100f).ToString("0.0") + "% non-black" +
                       ", " + Milliseconds.ToString("0.00") + " ms";
            }
        }

        /// <summary>
        /// Times every method against the live window and selects the best one, leaving
        /// <see cref="Mode"/> set to the winner.
        ///
        /// Selection is by picture first and speed second. A method returning 40% non-black at
        /// 9 ms beats one returning 2% at 0.3 ms, because the fast one is returning nothing.
        /// Speed only decides between methods that both genuinely work.
        /// </summary>
        /// <param name="samplesPerMethod">Frames to average per method. More than one, because
        /// the first PrintWindow against a freshly created swap chain is routinely slower and
        /// emptier than the steady state.</param>
        public Score[] Probe(int samplesPerMethod = 8)
        {
            var methods = new[] { Method.BitBlt, Method.PrintWindow, Method.PrintWindowFullContent };
            var scores = new Score[methods.Length];
            Method original = Mode;

            for (int m = 0; m < methods.Length; m++)
            {
                Mode = methods[m];

                // Discarded warm-up. GDI object selection and DWM's first hand-off both cost
                // more once than they ever do again, and averaging that in slanders the method.
                Capture();

                var clock = Stopwatch.StartNew();
                bool succeeded = true;
                float nonBlack = 0f;

                for (int i = 0; i < samplesPerMethod; i++)
                {
                    succeeded &= Capture();
                    nonBlack = Math.Max(nonBlack, NonBlackFraction());
                }

                clock.Stop();

                scores[m] = new Score(methods[m], succeeded, nonBlack,
                    clock.Elapsed.TotalMilliseconds / samplesPerMethod);
            }

            Mode = Pick(scores, original);
            return scores;
        }

        /// <summary>
        /// Chooses from probe results: anything showing a real picture wins over anything that
        /// does not, and among those, the fastest wins.
        /// </summary>
        private static Method Pick(Score[] scores, Method fallback)
        {
            const float lookLikeAPicture = 0.05f;

            bool found = false;
            Method best = fallback;
            double bestMs = double.MaxValue;

            foreach (Score score in scores)
            {
                if (!score.Succeeded || score.NonBlack < lookLikeAPicture)
                {
                    continue;
                }

                if (!found || score.Milliseconds < bestMs)
                {
                    found = true;
                    best = score.Method;
                    bestMs = score.Milliseconds;
                }
            }

            return best;
        }

        private void Release()
        {
            if (_memoryDc != IntPtr.Zero)
            {
                if (_previous != IntPtr.Zero)
                {
                    SelectObject(_memoryDc, _previous);
                    _previous = IntPtr.Zero;
                }

                DeleteDC(_memoryDc);
                _memoryDc = IntPtr.Zero;
            }

            if (_bitmap != IntPtr.Zero)
            {
                DeleteObject(_bitmap);
                _bitmap = IntPtr.Zero;
            }

            if (_windowDc != IntPtr.Zero)
            {
                ReleaseDC(_window, _windowDc);
                _windowDc = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release();
            _pixels = null;
        }
    }
}
