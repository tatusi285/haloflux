using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Linq;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace HalofluxDesk;

/// <summary>
/// Native wallpaper renderer: a plain GDI+ window parented into the desktop
/// window below the icon host. Because it draws through GDI (not
/// DirectComposition) the shell composites it exactly like a wallpaper image,
/// so icons, the context menu and drag selection all keep working.
/// </summary>
internal sealed class NativeWall : Form
{
    private readonly Sampler _sampler;
    private string _track = "";
    private readonly Func<string>? _trackName;
    private readonly bool _minimal;
    private readonly AudioProbe _audio;

    private Bitmap? _frame;
    private Graphics? _fg;
    private Bitmap? _bg;
    private ImageAttributes? _alpha;
    private Bitmap? _bridge;
    private Bitmap?[] _pool = Array.Empty<Bitmap?>();
    private int[] _slotFrame = Array.Empty<int>();
    private bool[] _slotReady = Array.Empty<bool>();
    private byte[][] _sig = Array.Empty<byte[]>();   // 8x8 luminance fingerprint per slot
    private string[] _frames = Array.Empty<string>();
    private readonly Dictionary<int, Bitmap> _frameCache = new();
    private const int FrameRate = 15;
    private const int MaxCachedFrames = 12;
    private Bitmap? _glow;
    private Bitmap? _vignette;
    private Bitmap? _halo;
    private Bitmap? _nebula;
    private Bitmap? _nebulaB;
    private Bitmap? _hud;
    private Graphics? _hudG;
    private double _hudAge = 999;
    private Bitmap? _plate;
    private Graphics? _plateG;
    private Bitmap? _plateSource;
    private bool _renderError;

    private System.Windows.Forms.Timer? _timer;
    private System.Windows.Forms.Timer? _watch;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _t;

    private IntPtr _host;
    private readonly IntPtr _hostParent;
    private bool _loggedAttach;
    private bool _attached;

    private readonly Random _rng = new();
    private readonly List<Meteor> _meteors = new();
    private double _spawnAcc;

    private struct Drop { public double X, Y, Len, Speed, Alpha; }
    private readonly List<Drop> _rain = new();
    private double _rainAcc;

    private struct Meteor
    {
        public double Angle;
        public double Radius;
        public double Speed;
        public double Length;
        public double Alpha;
    }

    private static readonly string[] Roman =
        { "XII", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI" };

    internal const int SurfaceW = 1920;
    private const int W = 1920;
    internal const int SurfaceH = 1080;
    private const int H = 1080;
    private const float S = W / 1600f;   // everything that was tuned for 1600x900 scales with this

    public NativeWall(Sampler sampler, AudioProbe audio, Func<string>? trackName = null, bool minimal = false)
    {
        _sampler = sampler;
        _audio = audio;
        _trackName = trackName;
        _minimal = minimal;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        var screen = Screen.PrimaryScreen!.Bounds;
        Bounds = screen;
        BackColor = Color.Black;
        DoubleBuffered = true;

        SetStyle(ControlStyles.Opaque, true);

        _hostParent = FindDesktopHost();

        LoadBackground();
        LoadFrames();
        StartDecoder();
        MakeGlow();
        MakeFonts();
        MakeVignette();
        _nebula = MakeNebula(20261110);
        _nebulaB = MakeNebula(777001);
        MakeHalo();
        InitSurface();
    }


    private Font _fTitle = null!, _fTag = null!, _fRow = null!, _fVal = null!;
    private Font _fMid = null!, _fTiny = null!, _fBig = null!, _fSmall = null!, _fBrand = null!;
    private readonly SolidBrush _bWhite = new(Color.FromArgb(240, 232, 246, 255));
    private readonly SolidBrush _bDim = new(Color.FromArgb(150, 140, 170, 195));
    private readonly SolidBrush _bAccent = new(Color.FromArgb(220, 143, 230, 255));
    private readonly SolidBrush _bPanel = new(Color.FromArgb(120, 5, 9, 14));
    private readonly Pen _pBorder = new(Color.FromArgb(40, 143, 230, 255), 1f);
    private readonly Pen _pTrack = new(Color.FromArgb(30, 255, 255, 255), 2.4f);
    private readonly Pen _pBarLow = new(Color.FromArgb(240, 150, 225, 255), 2.4f);
    private readonly Pen _pBarMid = new(Color.FromArgb(240, 255, 208, 140), 2.4f);
    private readonly Pen _pBarHigh = new(Color.FromArgb(240, 255, 120, 120), 2.4f);
    private readonly StringFormat _sfCenter = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    private Pen? _pBand;
    private readonly Pen?[] _meteorPens = new Pen?[8];

    private void MakeFonts()
    {
        _fTitle = new Font("Consolas", 8.6f * S);
        _fTag = new Font("Consolas", 6.2f * S);
        _fRow = new Font("Consolas", 6.9f * S);
        _fVal = new Font("Consolas", 8f * S);
        _fBig = new Font("Consolas", 18f * S);
        _fSmall = new Font("Consolas", 7f * S);
        _fBrand = new Font("Consolas", 15f * S, FontStyle.Bold);
        _fTiny = new Font("Consolas", 6.2f * S);
        _fMid = new Font("Consolas", 13f * S);
    }

    private static IntPtr FindDesktopHost()
    {
        try
        {
            var progman = Native.FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return IntPtr.Zero;

            // the shell creates this WorkerW inside Progman; it sits below the icon host
            Native.SendMessageTimeout(progman, 0x052C, (IntPtr)0xD, (IntPtr)1, 0, 1000, out _);
            var worker = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            return worker != IntPtr.Zero ? worker : progman;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }


    internal Bitmap? Frame => _frame;

    /// <summary>Measured presentation rate, fed by the driver so the overlay can show it.</summary>
    internal double DiagFps;   // plain field: WinForms does not want designer-visible properties here

    /// <summary>Index of the video frame on screen, or -1 when the scene animates every frame.</summary>
    internal int FrameId => _minimal && _frames.Length > 0
        ? (int)(_clock.Elapsed.TotalSeconds * WallFps)   // read the clock, not _t
        : -1;

    /// <summary>How often the wallpaper presents: double the footage rate while cross fading.</summary>
    internal int WallFps => Config.Current.CrossFade
        ? Math.Clamp(Config.Current.WallFps, 10, 60)
        : FrameRate;

    internal void RenderFrame()
    {
        _t = _clock.Elapsed.TotalSeconds;
        Render();
    }

    private void InitSurface()
    {
        if (_frame != null) return;
        _frame = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _fg = Graphics.FromImage(_frame);
        _fg.SmoothingMode = SmoothingMode.AntiAlias;
        _fg.TextRenderingHint = TextRenderingHint.AntiAlias;
        _fg.InterpolationMode = InterpolationMode.Bilinear;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;

            // a child window of the desktop host: the shell composites us as wallpaper,
            // so the icons, the context menu and drag selection stay above us
            cp.ExStyle |= 0x00000080 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        InitSurface();

        Attach();
        Render();

        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) =>
        {
            _t = _clock.Elapsed.TotalSeconds;
            Render();
            Invalidate();
        };
        _timer.Start();

        _watch = new System.Windows.Forms.Timer { Interval = 2000 };
        _watch.Tick += (_, _) =>
        {
            if (!_attached || _host == IntPtr.Zero || !Native.IsWindow(_host))
                Attach();
        };
        _watch.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_frame == null) return;
        var r = ClientRectangle;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(_frame, r);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Dispose();
            _watch?.Dispose();
            _fg?.Dispose();
            _frame?.Dispose();
            _bg?.Dispose();
            foreach (var b in _frameCache.Values) b.Dispose();
            _frameCache.Clear();
            _glow?.Dispose();
            _nebula?.Dispose();
            _nebulaB?.Dispose();
            lock (_ready)
            {
                foreach (var b in _ready.Values) b.Dispose();
                _ready.Clear();
            }
            if (_pool != null) foreach (var b in _pool) b?.Dispose();
            _plateG?.Dispose();
            _plate?.Dispose();
            _hudG?.Dispose();
            _hud?.Dispose();
        }
        base.Dispose(disposing);
    }

    /* ---------------- desktop parenting ---------------- */

    private void Attach()
    {
        try
        {
            // WS_CHILD must be set *before* SetParent, otherwise the window keeps
            // behaving as a popup and the shell draws it above the icons
            int style = Native.GetWindowLong(Handle, -16);
            style = (style | 0x40000000) & ~unchecked((int)0x80000000);
            Native.SetWindowLong(Handle, -16, style);

            // parent straight to the desktop window, then drop below the icon host
            var progman = Native.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
                Native.SetParent(Handle, progman);

            var defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

            var host = Native.GetParent(Handle);
            Log.Write("native probe: hwnd=" + Handle + " style=0x" + Native.GetWindowLong(Handle, -16).ToString("X8")
                      + " parent=" + host + " progman=" + progman + " defview=" + defView
                      + " err=" + Marshal.GetLastWin32Error());

            var screen = Screen.PrimaryScreen!.Bounds;
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, screen.Width, screen.Height, 0x0004);
            Native.SetWindowPos(Handle, defView, 0, 0, screen.Width, screen.Height, 0x0004);
            Native.ShowWindow(Handle, 5);

            _host = host;
            _attached = host != IntPtr.Zero;

            if (!_loggedAttach)
            {
                _loggedAttach = true;
                Log.Write("native: parent=" + host + " wanted=" + _hostParent
                          + " " + screen.Width + "x" + screen.Height);
            }
        }
        catch (Exception ex)
        {
            Log.Write("native attach: " + ex.Message);
        }
    }

    /* ---------------- assets ---------------- */

    private void LoadFrames()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "www", "media", "frames");
            if (!Directory.Exists(dir)) return;
            _frames = Directory.GetFiles(dir, "f_*.jpg");
            Array.Sort(_frames, StringComparer.Ordinal);
            Log.Write("native: " + _frames.Length + " background frames");
        }
        catch (Exception ex)
        {
            Log.Write("native frames: " + ex.Message);
        }
    }

    private readonly Dictionary<int, Bitmap> _ready = new();
    private volatile int _wanted;
    private Thread? _decoder;

    private void StartDecoder()
    {
        if (_frames.Length == 0 || _decoder != null) return;

        // one bitmap per slot, reused forever: allocating 8 MB frames in a loop
        // fragments the large object heap until the process is gigabytes big
        int slots = Math.Min(_frames.Length, 20);
        _pool = new Bitmap?[slots];
        _slotFrame = new int[slots];
        _slotReady = new bool[slots];
        _sig = new byte[slots][];

        for (int i = 0; i < slots; i++) _slotFrame[i] = -1;

        _decoder = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    int want = _wanted;
                    int ahead = slots - 2;

                    for (int i = 0; i < ahead; i++)
                    {
                        int idx = (want + i) % _frames.Length;

                        lock (_ready)
                        {
                            if (IsSlotReady(idx)) continue;
                        }

                        using var src = Image.FromFile(_frames[idx]);

                        int slot = FindSlotFor(idx);
                        if (slot < 0) continue;

                        var bmp = _pool[slot] ??= new Bitmap(W, H);

                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.Bilinear;
                            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                            g.DrawImage(src, 0, 0, W, H);
                        }

                        _sig[slot] = Signature(bmp);

                        lock (_ready)
                        {
                            _slotFrame[slot] = idx;
                            _slotReady[slot] = true;
                        }
                    }
                }
                catch { }

                Thread.Sleep(30);
            }
        })
        { IsBackground = true, Name = "frame-decoder" };

        _decoder.Start();
        Log.Write("native: decoder started with " + slots + " reusable slots");
    }

    /// <summary>Coarse 8x8 luminance grid, used to tell a moving shot from a still one.</summary>
    private static byte[] Signature(Bitmap bmp)
    {
        var sig = new byte[64];
        int cw = Math.Max(1, bmp.Width / 8);
        int ch = Math.Max(1, bmp.Height / 8);
        int stepX = Math.Max(1, cw / 3);
        int stepY = Math.Max(1, ch / 3);

        for (int cy = 0; cy < 8; cy++)
        {
            for (int cx = 0; cx < 8; cx++)
            {
                long sum = 0;
                int n = 0;

                for (int y = cy * ch; y < (cy + 1) * ch; y += stepY)
                {
                    for (int x = cx * cw; x < (cx + 1) * cw; x += stepX)
                    {
                        var c = bmp.GetPixel(x, y);
                        sum += (c.R * 77 + c.G * 151 + c.B * 28) >> 8;
                        n++;
                    }
                }

                sig[cy * 8 + cx] = (byte)(n == 0 ? 0 : sum / n);
            }
        }

        return sig;
    }

    private static int SigDistance(byte[]? a, byte[]? b)
    {
        if (a == null || b == null) return 255;

        int worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            if (d > worst) worst = d;
        }

        return worst;
    }

    /// <summary>Finds a free slot, or steals the one furthest from the playhead.</summary>
    private int FindSlotFor(int frame)
    {
        lock (_ready)
        {
            for (int i = 0; i < _slotReady.Length; i++)
                if (!_slotReady[i]) return i;

            int worst = 0, worstDist = -1;
            for (int i = 0; i < _slotFrame.Length; i++)
            {
                int dist = ((_slotFrame[i] - _wanted) % _frames.Length + _frames.Length) % _frames.Length;
                if (dist > worstDist) { worstDist = dist; worst = i; }
            }

            _slotReady[worst] = false;
            return worst;
        }
    }

    private bool IsSlotReady(int frame)
    {
        for (int i = 0; i < _slotFrame.Length; i++)
            if (_slotReady[i] && _slotFrame[i] == frame) return true;
        return false;
    }

    private Bitmap? FindSlotBitmap(int frame)
    {
        for (int i = 0; i < _slotFrame.Length; i++)
            if (_slotReady[i] && _slotFrame[i] == frame) return _pool[i];
        return null;
    }

    private Bitmap? FrameAt(double seconds)
    {
        if (_frames.Length == 0) return null;

        int idx = (int)(seconds * FrameRate) % _frames.Length;
        _wanted = idx;

        if (_slotFrame == null) return _bg;

        lock (_ready)
        {
            var exact = FindSlotBitmap(idx);
            if (exact != null) return exact;

            // nearest ready frame, so playback never stalls
            for (int d = 1; d < 12; d++)
            {
                var near = FindSlotBitmap((idx + d) % _frames.Length) ?? FindSlotBitmap((idx - d + _frames.Length) % _frames.Length);
                if (near != null) return near;
            }
        }

        return _bg;
    }

    private void LoadBackground()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "www", "media", "bg.jpg");
            if (File.Exists(path))
            {
                var src = Image.FromFile(path);
                _bg = new Bitmap(src);
                src.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Write("native bg: " + ex.Message);
        }
    }


    private Bitmap MakeNebula(int seed)
    {
        var rnd = new Random(seed);
        var canvas = new Bitmap(W, H);

        using var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // a soft dot we stamp hundreds of times: cheap and stays smooth
        using var dot = new Bitmap(96, 96);
        using (var dg = Graphics.FromImage(dot))
        {
            using var path = new GraphicsPath();
            path.AddEllipse(0, 0, 96, 96);
            using var brush = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(255, 255, 255, 255),
                SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) },
                FocusScales = new PointF(0.02f, 0.02f),
            };
            dg.FillEllipse(brush, 0, 0, 96, 96);
        }

        // broad curtains on the left, the light pool around the ring
        for (int i = 0; i < 520; i++)
        {
            double t1 = rnd.NextDouble();
            double cx, cy;

            if (i % 5 == 0)
            {
                // light pool behind the ring
                double a = rnd.NextDouble() * Math.PI * 2;
                double r = Math.Sqrt(rnd.NextDouble()) * H * 0.34;
                cx = W * 0.635 + Math.Cos(a) * r * 1.15;
                cy = H * 0.445 + Math.Sin(a) * r;
            }
            else
            {
                cx = W * (0.02 + t1 * 0.62);
                cy = H * (0.06 + rnd.NextDouble() * 0.88);
            }

            double size = 60 + rnd.NextDouble() * 220;

            int kind = rnd.Next(3);
            var col = kind == 0 ? Color.FromArgb(255, 90, 120, 255)
                : kind == 1 ? Color.FromArgb(255, 190, 110, 255)
                : Color.FromArgb(255, 100, 200, 255);

            int alpha = 5 + rnd.Next(10);

            using var ia = new System.Drawing.Imaging.ImageAttributes();
            var matrix = new System.Drawing.Imaging.ColorMatrix
            {
                Matrix00 = col.R / 255f,
                Matrix11 = col.G / 255f,
                Matrix22 = col.B / 255f,
                Matrix33 = alpha / 255f,
            };
            ia.SetColorMatrix(matrix);

            g.DrawImage(dot,
                new Rectangle((int)(cx - size / 2), (int)(cy - size / 2), (int)size, (int)size),
                0, 0, 96, 96, GraphicsUnit.Pixel, ia);
        }

        // bake the vignette in: one less full screen composite every frame
        if (_vignette != null) g.DrawImage(_vignette, 0, 0, W, H);

        // faint dust so the flat areas are not dead
        for (int i = 0; i < 900; i++)
        {
            int x = rnd.Next(W), y = rnd.Next(H);
            int a = 6 + rnd.Next(18);
            using var pen = new Pen(Color.FromArgb(a, 210, 230, 255), 1f);
            g.DrawLine(pen, x, y, x + rnd.Next(2, 7), y);
        }

        return canvas;
    }

    private void MakeVignette()
    {
        _vignette = new Bitmap(W, H);
        using var g = Graphics.FromImage(_vignette);
        using var path = new GraphicsPath();
        path.AddRectangle(new RectangleF(0, 0, W, H));
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(0, 0, 0, 0),
            SurroundColors = new[] { Color.FromArgb(190, 4, 6, 10) },
        };
        g.FillPath(brush, path);
    }

    private void MakeHalo()
    {
        _halo = new Bitmap(256, 256);
        using var g = Graphics.FromImage(_halo);
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, 256, 256);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(64, 190, 228, 255),
            SurroundColors = new[] { Color.FromArgb(0, 190, 228, 255) },
        };
        g.FillEllipse(brush, 0, 0, 256, 256);
    }

    /// <summary>A very faint radial glow, for the hint of the core on the wallpaper.</summary>
    private static Bitmap MakeSoftGlow(int size, int alpha)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, size, size);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(alpha, 226, 242, 255),
            SurroundColors = new[] { Color.FromArgb(0, 226, 242, 255) },
            FocusScales = new PointF(0.10f, 0.10f),
        };
        g.FillEllipse(brush, 0, 0, size, size);
        return bmp;
    }

    private void MakeGlow()
    {
        _glow = new Bitmap(256, 256);
        using var g = Graphics.FromImage(_glow);
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, 256, 256);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(255, 236, 246, 255),
            SurroundColors = new[] { Color.FromArgb(0, 236, 246, 255) },
            FocusScales = new PointF(0.05f, 0.05f),
        };
        g.FillEllipse(brush, 0, 0, 256, 256);
    }

    /* ---------------- scene ---------------- */

    private void Render()
    {
        if (_fg == null || _frame == null) return;

        try
        {
            RenderScene();
        }
        catch (Exception ex)
        {
            if (!_renderError)
            {
                _renderError = true;
                Log.Write("native render error: " + ex);
            }
        }
    }

    /// <summary>
    /// The screensaver lives around a bright core. A very faint version of that same core
    /// on the wallpaper ties the two together instead of feeling like two products.
    /// </summary>
    private void DrawStyleBridge(Graphics g)
    {
        if (!Config.Current.StyleBridge) return;

        // build it once at the size it is drawn at: scaling a big alpha bitmap every frame
        // costs more than the effect is worth
        int size = (int)(H * 0.30);
        if (_bridge == null || _bridge.Width != size) _bridge = MakeSoftGlow(size, 30);

        int x = (int)(W * 0.635) - size / 2;
        int y = (int)(H * 0.445) - size / 2;

        g.DrawImageUnscaled(_bridge, x, y);
    }

    /// <summary>A slow, barely visible swell in overall brightness.</summary>
    private void DrawBreathing(Graphics g)
    {
        int amount = Config.Current.Breathing;
        if (amount <= 0) return;

        double phase = Math.Sin(_t * (Math.PI * 2 / 40.0));      // 40 second cycle
        int alpha = (int)((phase * 0.5 + 0.5) * amount * 2.2);   // 0 .. amount*2.2
        if (alpha <= 0) return;

        using var brush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        g.FillRectangle(brush, 0, 0, W, H);
    }

    /// <summary>Tiny readout in the corner, only when haloflux.json asks for it.</summary>
    private void DrawDiagnostics(Graphics g)
    {
        int ready = 0;
        for (int i = 0; i < _slotReady.Length; i++) if (_slotReady[i]) ready++;

        var text = string.Format(
            "fps {0:0.0}     frame {1}/{2}     pool {3}/{4}     mem {5} MB",
            DiagFps, (int)(_t * FrameRate) % Math.Max(1, _frames.Length), _frames.Length,
            ready, _slotReady.Length, GC.GetTotalMemory(false) / (1024 * 1024));

        using var font = new Font("Consolas", 10f);
        using var back = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        using var ink = new SolidBrush(Color.FromArgb(210, 200, 235, 255));

        var size = g.MeasureString(text, font);
        g.FillRectangle(back, W - size.Width - 24, 10, size.Width + 14, size.Height + 8);
        g.DrawString(text, font, ink, W - size.Width - 17, 13);
    }

    /// <summary>
    /// The footage arrives at 15 fps. Drawing it as-is looks steppy, so the outgoing frame is
    /// blended into the incoming one and the result is presented at double the rate.
    /// </summary>
    private void RenderVideoFrame(Graphics g)
    {
        if (_frames.Length == 0)
        {
            if (_bg != null) g.DrawImage(_bg, 0, 0, W, H);
            return;
        }

        double pos = _t * FrameRate;
        int prev = (int)Math.Floor(pos) % _frames.Length;
        int next = (prev + 1) % _frames.Length;
        float mix = (float)(pos - Math.Floor(pos));

        _wanted = next;   // without this the decoder never moves and the picture freezes

        var a = GetFrame(prev);
        var b = GetFrame(next);

        if (a == null && b == null)
        {
            if (_bg != null) g.DrawImage(_bg, 0, 0, W, H);
            return;
        }

        if (a == null) a = b;
        if (b == null) b = a;

        g.DrawImageUnscaled(a!, 0, 0);

        bool moving = !ReferenceEquals(a, b) && SigDistance(SigFor(a), SigFor(b)) >= Config.Current.MotionThreshold;

        if (Config.Current.CrossFade && moving && mix > 0.02f)
        {
            _alpha?.Dispose();
            _alpha = new ImageAttributes();
            var cm = new ColorMatrix { Matrix33 = mix };
            _alpha.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            g.DrawImage(b!, new Rectangle(0, 0, W, H), 0, 0, W, H, GraphicsUnit.Pixel, _alpha);
        }
    }

    private byte[]? SigFor(Bitmap bmp)
    {
        for (int i = 0; i < _slotReady.Length; i++)
            if (_slotReady[i] && ReferenceEquals(_pool[i], bmp)) return _sig[i];

        return null;
    }

    /// <summary>Frame by absolute index, or the closest decoded neighbour.</summary>
    private Bitmap? GetFrame(int index)
    {
        if (_slotFrame == null || _slotFrame.Length == 0) return _bg;

        lock (_ready)
        {
            var exact = FindSlotBitmap(index);
            if (exact != null) return exact;

            for (int d = 1; d < 12; d++)
            {
                var near = FindSlotBitmap((index + d) % _frames.Length)
                        ?? FindSlotBitmap((index - d + _frames.Length) % _frames.Length);
                if (near != null) return near;
            }
        }

        return _bg;
    }

    private void RenderScene()
    {
        var g = _fg!;

        if (_minimal)
        {
            RenderVideoFrame(g);
            DrawStyleBridge(g);
            DrawBreathing(g);
            if (Config.Current.ShowDiagnostics) DrawDiagnostics(g);
            return;
        }
        var snap = _sampler.Latest;
        var aud = _audio.Snapshot();

        double load = (snap.Cpu.Total * 0.55 + snap.Gpu.Util * 0.45) / 100.0;
        double level = Math.Min(1, aud.Level * 2.6);

        double bass = 0;
        for (int i = 0; i < Math.Min(6, aud.Bands.Length); i++) bass += aud.Bands[i];
        bass = Math.Min(1, bass / 4.2);

        double cx = W * 0.635, cy = H * 0.445;
        double ro = H * 0.445, ri = ro * 0.60;
        double span = ro - ri;

        // ---- background plate: video + haze + hud, rebuilt only when they change ----
        var live = FrameAt(_t);
        bool plateStale = _plate == null || !ReferenceEquals(live, _plateSource) || _hudAge >= 1.0;

        if (plateStale)
        {
            if (_plate == null)
            {
                _plate = new Bitmap(W, H);
                _plateG = Graphics.FromImage(_plate);
                _plateG.SmoothingMode = SmoothingMode.AntiAlias;
                _plateG.InterpolationMode = InterpolationMode.Bilinear;
            }

            var pg = _plateG!;

            if (live != null) pg.DrawImageUnscaled(live, 0, 0);
            else if (_bg != null) pg.DrawImage(_bg, 0, 0, W, H);
            else
            {
                using var dark = new SolidBrush(Color.FromArgb(10, 8, 6));
                pg.FillRectangle(dark, 0, 0, W, H);
            }

            if (_nebula != null)
            {
                int ax = (int)(Math.Sin(_t * 0.05) * 26 * S);
                int ay = (int)(Math.Cos(_t * 0.041) * 18 * S);
                pg.DrawImageUnscaled(_nebula, ax, ay);
            }

            _hudAge += 0.1;
            if (_hud == null || _hudAge >= 1.0) UpdateHud(cx, cy, ri, ro, span, load, snap, aud);
            if (_hud != null)
            {
                var old = pg.InterpolationMode;
                pg.DrawImageUnscaled(_hud, 0, 0);
            }

            _plateSource = live;
        }

        // ---- everything that actually moves, drawn every frame ----
        if (_plate != null) g.DrawImageUnscaled(_plate, 0, 0);

        DrawLensing(g, cx, cy, ri, ro, load);
        DrawMeteors(g, cx, cy, ro, level, bass);
        DrawCore(g, cx, cy, ri, load, level, bass);
        DrawSatellite(g, cx, cy, ro, level);
    }

    private static void SafeArc(Graphics g, Pen pen, double cx, double cy, double radius,
        double startDeg, double sweepDeg)
    {
        if (radius < 2 || double.IsNaN(radius) || double.IsInfinity(radius)) return;
        if (double.IsNaN(startDeg) || double.IsInfinity(startDeg)) return;
        if (double.IsNaN(sweepDeg) || double.IsInfinity(sweepDeg) || Math.Abs(sweepDeg) < 0.01) return;

        try
        {
            g.DrawArc(pen,
                (float)(cx - radius), (float)(cy - radius),
                (float)(radius * 2), (float)(radius * 2),
                (float)startDeg, (float)sweepDeg);
        }
        catch { }
    }

    private void DrawRims(Graphics g, double cx, double cy, double ri, double ro, double load)
    {
        int a = (int)(70 + load * 40);

        using (var inner = new Pen(Color.FromArgb(Math.Min(190, a + 40), 196, 232, 255), 1.4f * S))
            g.DrawEllipse(inner, (float)(cx - ri), (float)(cy - ri), (float)(ri * 2), (float)(ri * 2));

        using (var outer = new Pen(Color.FromArgb(Math.Min(120, a), 150, 200, 245), 1f * S))
            g.DrawEllipse(outer, (float)(cx - ro), (float)(cy - ro), (float)(ro * 2), (float)(ro * 2));

        using var tick = new Pen(Color.FromArgb(70, 170, 210, 245), 1f * S);
        for (int i = 0; i < 60; i++)
        {
            double ang = i * Math.PI * 2 / 60;
            double len = (i % 5 == 0) ? ro * 0.035 : ro * 0.018;
            g.DrawLine(tick,
                (float)(cx + Math.Cos(ang) * ro), (float)(cy + Math.Sin(ang) * ro),
                (float)(cx + Math.Cos(ang) * (ro + len)), (float)(cy + Math.Sin(ang) * (ro + len)));
        }

        for (int i = 0; i < 16; i++)
        {
            double ang = i * Math.PI * 2 / 16 - Math.PI / 2;
            double r = ro * 1.012;

            double x = cx + Math.Cos(ang) * r;
            double y = cy + Math.Sin(ang) * r;
            if (x < 24 || x > W - 24 || y < 96 * S || y > H - 24) continue;

            var state = g.Save();
            g.TranslateTransform((float)x, (float)y);
            g.RotateTransform((float)(ang * 180 / Math.PI + 90));

            using var font = new Font("Consolas", 6.4f * S);
            using var brush = new SolidBrush(Color.FromArgb(i % 4 == 0 ? 190 : 110, 176, 212, 240));
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString((i + 1).ToString("00"), font, brush, 0, 0, fmt);

            g.Restore(state);
        }
    }

    private void DrawBands(Graphics g, double cx, double cy, double ri, double ro, double span, double load)
    {
        for (int b = 0; b < 7; b++)
        {
            double t0 = b / 7.0, t1 = (b + 1) / 7.0;
            double rMid = ri + span * (t0 + t1) / 2;
            float width = (float)Math.Max(0.9, span * (t1 - t0) * 0.14);
            int segs = 28 + b * 6;
            double phase = _t * (0.05 + 0.03 * b) + b * 0.7;
            double step = Math.PI * 2 / segs;

            int alpha = Math.Min(200, (int)(44 + load * 36 + (b % 3) * 14));

            _pBand?.Dispose();
            _pBand = new Pen(Color.FromArgb(alpha, 168, 214, 255), width);
            for (int s = 0; s < segs; s++)
            {
                double a0 = (s * step + phase) * 180 / Math.PI;
                SafeArc(g, _pBand, cx, cy, rMid, a0, step * 0.86 * 180 / Math.PI);
            }

            _pBand.Dispose();
            _pBand = new Pen(Color.FromArgb(Math.Max(12, alpha / 3), 210, 236, 255), (float)(width * 0.5));
            for (int s = 0; s < segs; s++)
            {
                double a0 = (s * step + phase + step * 0.5) * 180 / Math.PI;
                SafeArc(g, _pBand, cx, cy, rMid + width * 1.1, a0, step * 0.7 * 180 / Math.PI);
            }
        }
    }

    private void DrawMeteors(Graphics g, double cx, double cy, double ro, double level, double bass)
    {
        // spawn rate follows the music, exactly like the canvas version did
        _spawnAcc += (2.2 + level * 26 + bass * 16) * 0.05;
        while (_spawnAcc > 1)
        {
            _spawnAcc -= 1;
            _meteors.Add(new Meteor
            {
                Angle = _rng.NextDouble() * Math.PI * 2,
                Radius = ro * (0.95 + _rng.NextDouble() * 0.45),
                Speed = (0.9 + _rng.NextDouble() * 2.4) * S,
                Length = (28 + _rng.NextDouble() * 120) * S,
                Alpha = 0.22 + _rng.NextDouble() * 0.55 + level * 0.25,
            });
        }

        for (int i = _meteors.Count - 1; i >= 0; i--)
        {
            var m = _meteors[i];
            m.Radius -= m.Speed * 4.2;
            m.Angle += 0.0025;                    // slight gravity curl
            if (m.Radius < ro * 0.45) { _meteors.RemoveAt(i); continue; }
            _meteors[i] = m;

            double x1 = cx + Math.Cos(m.Angle) * m.Radius;
            double y1 = cy + Math.Sin(m.Angle) * m.Radius;
            double x2 = cx + Math.Cos(m.Angle) * (m.Radius + m.Length);
            double y2 = cy + Math.Sin(m.Angle) * (m.Radius + m.Length);

            int bucket = Math.Clamp((int)(Math.Min(1, m.Alpha) * 7), 0, 7);

            if (_meteorPens[bucket] == null)
            {
                int a = (int)(bucket / 7.0 * 210);
                var pen = new Pen(Color.FromArgb(Math.Max(8, a), 206, 234, 255), (float)(1.9 * S));
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                _meteorPens[bucket] = pen;
            }

            g.DrawLine(_meteorPens[bucket]!, (float)x1, (float)y1, (float)x2, (float)y2);
        }

        if (_meteors.Count > 260) _meteors.RemoveRange(0, _meteors.Count - 260);
    }

    private void DrawSectors(Graphics g, double cx, double cy, double ri, double ro, Snapshot snap, double level)
    {
        var panels = BuildPanels(snap, level);
        int n = panels.Length;
        double span = Math.PI * 2 / n;
        double pad = 0.035;

        for (int i = 0; i < n; i++)
        {
            var p = panels[i];
            double a0 = i * span - Math.PI / 2 + pad;
            double a1 = a0 + span - pad * 2;

            // keep the top sector clear of the readout bar
            if (cy + Math.Sin((a0 + a1) / 2) * ro <= H * 0.098) continue;

            // no plate: the reference draws the readouts straight onto the discs

            ArcText(g, p.Title, cx, cy, ro - 10, a0 + 0.07, 0.047, Color.FromArgb(235, 176, 206, 230), _fTitle, false);
            ArcText(g, p.Tag, cx, cy, ro - 10, a1 - 0.07, 0.038, Color.FromArgb(170, 126, 156, 180), _fTag, true);

            double top = ro - 26;
            double bottom = ri + 12;
            double stepR = p.Rows.Length > 1 ? (top - bottom) / (p.Rows.Length - 1) : 0;

            for (int j = 0; j < p.Rows.Length; j++)
            {
                var row = p.Rows[j];
                double rr = top - stepR * j;

                ArcText(g, row.Label, cx, cy, rr, a0 + 0.10, 0.033, Color.FromArgb(225, 158, 188, 212), _fRow, false);
                ArcText(g, row.Value, cx, cy, rr, a1 - 0.10, 0.035, Color.FromArgb(250, 240, 250, 255), _fVal, true);

                if (row.Pct > 0)
                {
                    double b0 = a0 + (a1 - a0) * 0.30;
                    double b1 = b0 + (a1 - a0) * 0.44;
                    double bw = rr - 4;
                    double pct = Math.Max(0, Math.Min(1, row.Pct / 100));

                    SafeArc(g, _pTrack, cx, cy, bw, b0 * 180 / Math.PI, (b1 - b0) * 180 / Math.PI);

                    var pen = pct > 0.8 ? _pBarHigh : pct > 0.6 ? _pBarMid : _pBarLow;
                    SafeArc(g, pen, cx, cy, bw, b0 * 180 / Math.PI, (b1 - b0) * pct * 180 / Math.PI);
                }
            }
        }
    }

    private void ArcText(Graphics g, string text, double cx, double cy, double r,
        double aStart, double step, Color color, Font font, bool alignRight)
    {
        if (string.IsNullOrEmpty(text)) return;

        var chars = text.ToCharArray();
        double mid = aStart + step * chars.Length * 0.5;
        bool flip = Math.Sin(mid) > 0;
        double a = alignRight ? aStart - step * chars.Length : aStart;

        using var brush = new SolidBrush(color);
        var fmt = _sfCenter;

        int count = chars.Length;
        for (int i = 0; i < count; i++)
        {
            char ch = flip ? chars[count - 1 - i] : chars[i];
            double px = cx + Math.Cos(a) * r;
            double py = cy + Math.Sin(a) * r;

            var state = g.Save();
            g.TranslateTransform((float)px, (float)py);
            g.RotateTransform((float)((a + Math.PI / 2 + (flip ? Math.PI : 0)) * 180 / Math.PI));
            g.DrawString(ch.ToString(), font, brush, 0, 0, fmt);
            g.Restore(state);

            a += step;
        }
    }


    private void DrawLensing(Graphics g, double cx, double cy, double ri, double ro, double load)
    {
        // the two long gravitational arcs that cross the disc in the reference look
        using var wide = new Pen(Color.FromArgb(30, 170, 214, 255), 1.1f * S);
        using var tight = new Pen(Color.FromArgb(52, 200, 232, 255), 1f * S);

        double r1 = ri * 1.36;
        g.DrawArc(wide, (float)(cx - r1), (float)(cy - r1), (float)(r1 * 2), (float)(r1 * 2), 196, 96);
        g.DrawArc(wide, (float)(cx - r1), (float)(cy - r1), (float)(r1 * 2), (float)(r1 * 2), 16, 96);

        double r2 = ri * 1.12;
        g.DrawArc(tight, (float)(cx - r2), (float)(cy - r2), (float)(r2 * 2), (float)(r2 * 2), 206, 60);

        // a faint spoke pair, like the reference's structural struts
        using var spoke = new Pen(Color.FromArgb(22, 180, 220, 255), 1f * S);
        for (int i = 0; i < 4; i++)
        {
            double ang = i * Math.PI / 2 + Math.PI / 4;
            g.DrawLine(spoke,
                (float)(cx + Math.Cos(ang) * ri), (float)(cy + Math.Sin(ang) * ri),
                (float)(cx + Math.Cos(ang) * ro), (float)(cy + Math.Sin(ang) * ro));
        }
    }

    private void DrawSatellite(Graphics g, double cx, double cy, double ro, double level)
    {
        double ang = _t * 0.22 - Math.PI / 2;
        double r = ro * 0.985;
        double x = cx + Math.Cos(ang) * r;
        double y = cy + Math.Sin(ang) * r;

        // trail
        for (int i = 1; i <= 12; i++)
        {
            double a2 = ang - i * 0.012;
            double x2 = cx + Math.Cos(a2) * r;
            double y2 = cy + Math.Sin(a2) * r;
            int alpha = Math.Max(0, 70 - i * 5);
            using var trail = new Pen(Color.FromArgb(alpha, 190, 232, 255), (float)(1.6 * S - i * 0.08 * S));
            g.DrawLine(trail, (float)x2, (float)y2, (float)(x2 + Math.Cos(a2 + 0.02) * 6), (float)(y2 + Math.Sin(a2 + 0.02) * 6));
        }

        if (_glow != null)
        {
            double size = 22 * S * (1 + level * 0.6);
            g.DrawImage(_glow, (float)(x - size / 2), (float)(y - size / 2), (float)size, (float)size);
        }

        using var dot = new SolidBrush(Color.FromArgb(240, 232, 248, 255));
        g.FillEllipse(dot, (float)(x - 2.4 * S), (float)(y - 2.4 * S), (float)(4.8 * S), (float)(4.8 * S));
    }


    private void DrawCore(Graphics g, double cx, double cy, double ri, double load, double level, double bass)
    {
        if (_glow == null) return;

        double pulse = 0.95 + 0.06 * Math.Sin(_t * 1.25) + level * 0.18 + bass * 0.20;

        double haloSize = ri * (1.9 + level * 0.5) * pulse;
        if (_halo != null)
            g.DrawImage(_halo, (float)(cx - haloSize / 2), (float)(cy - haloSize / 2),
                (float)haloSize, (float)haloSize);

        // nucleus
        double nuc = ri * (0.58 + level * 0.30) * pulse;
        g.DrawImage(_glow, (float)(cx - nuc / 2), (float)(cy - nuc / 2), (float)nuc, (float)nuc);

        double hot = ri * 0.12 * (1 + load * 0.35 + level * 0.9 + bass * 0.7);
        g.DrawImage(_glow, (float)(cx - hot), (float)(cy - hot), (float)(hot * 2), (float)(hot * 2));

        using (var quiet = new Pen(Color.FromArgb(46, 150, 210, 245), 1f * S))
        {
            double q = ri * 0.30;
            g.DrawEllipse(quiet, (float)(cx - q), (float)(cy - q), (float)(q * 2), (float)(q * 2));
        }

        // the music ring
        if (level > 0.02)
        {
            double beat = ri * (0.30 + bass * 0.34 + level * 0.10);
            using var pen = new Pen(Color.FromArgb((int)Math.Min(255, (0.15 + level * 0.75) * 255), 226, 246, 255),
                (float)((1.5 + level * 2.5) * S));
            g.DrawEllipse(pen, (float)(cx - beat), (float)(cy - beat), (float)(beat * 2), (float)(beat * 2));

            using var outer = new Pen(Color.FromArgb((int)Math.Min(200, level * 0.55 * 255), 255, 196, 226), 1f * S);
            g.DrawEllipse(outer, (float)(cx - beat * 1.22), (float)(cy - beat * 1.22),
                (float)(beat * 2.44), (float)(beat * 2.44));
        }
    }

    /// <summary>Letter spaced text, which is what gives the HUD its technical look.</summary>
    private static void Tracked(Graphics g, string text, Font font, Brush brush, float x, float y, float tracking)
    {
        float cursor = x;
        foreach (var ch in text)
        {
            var s = ch.ToString();
            g.DrawString(s, font, brush, cursor, y);
            cursor += g.MeasureString(s, font).Width + tracking;
        }
    }

    private static float TrackedWidth(Graphics g, string text, Font font, float tracking)
    {
        float w = 0;
        foreach (var ch in text)
        {
            var s = ch.ToString();
            w += g.MeasureString(s, font).Width + tracking;
        }
        return w;
    }


    /// <summary>
    /// The ring, its labels and the top bar barely change between frames but cost
    /// most of the draw time, so they are rendered into their own bitmap a few
    /// times per second and simply stamped onto every frame.
    /// </summary>
    private void UpdateHud(double cx, double cy, double ri, double ro, double span,
        double load, Snapshot snap, AudioFrame aud)
    {
        if (_hud == null)
        {
            _plate = new Bitmap(W, H);
            _plateG = Graphics.FromImage(_plate);
            _plateG.SmoothingMode = SmoothingMode.AntiAlias;
            _plateG.InterpolationMode = InterpolationMode.Bilinear;

            _hud = new Bitmap(W, H);
            _hudG = Graphics.FromImage(_hud);
            
            _hudG.SmoothingMode = SmoothingMode.AntiAlias;
            _hudG.TextRenderingHint = TextRenderingHint.AntiAlias;
            _hudG.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        var g = _hudG!;
        g.Clear(Color.Transparent);

        DrawBands(g, cx, cy, ri, ro, span, load);
        DrawRims(g, cx, cy, ri, ro, load);
        DrawSectors(g, cx, cy, ri, ro, snap, 0);
        DrawTopBar(g, snap, aud);

        _hudAge = 0;
    }

    private void DrawTopBar(Graphics g, Snapshot snap, AudioFrame aud)
    {
        // the reference keeps the top strip darker so the readouts stay legible
        using (var shade = new System.Drawing.Drawing2D.LinearGradientBrush(
            new RectangleF(0, 0, W, 150 * S),
            Color.FromArgb(150, 3, 5, 9), Color.FromArgb(0, 3, 5, 9),
            System.Drawing.Drawing2D.LinearGradientMode.Vertical))
        {
            g.FillRectangle(shade, 0, 0, W, 150 * S);
        }

        float m = W * 0.405f;   // the desktop icons own the left 40% of the screen
        float y = 22 * S;

        // ---- brand ----
        using (var dot = new SolidBrush(Color.FromArgb(235, 120, 226, 255)))
            g.FillEllipse(dot, m, y + 10 * S, 7 * S, 7 * S);

        Tracked(g, "HALOFLUX", _fTitle, _bWhite, m + 16 * S, y + 2 * S, 2.2f * S);
        

        // ---- KPI columns ----
        var items = new (string Label, string Value, string Sub)[]
        {
            ("CPU", snap.Cpu.Total.ToString("0"), snap.Cpu.CoreCount + " CORES"),
            ("GPU", snap.Gpu.Util.ToString("0"), snap.Gpu.TempC > 0 ? snap.Gpu.TempC.ToString("0") + "C" : "--"),
            ("MEMORY", (snap.Mem.TotalMb > 0 ? snap.Mem.UsedMb / snap.Mem.TotalMb * 100 : 0).ToString("0"),
                (snap.Mem.UsedMb / 1024).ToString("0.0") + " / " + (snap.Mem.TotalMb / 1024).ToString("0") + " GB"),
            ("NETWORK", (snap.Net.DownBps / 1048576).ToString("0.0"),
                "DN " + (snap.Net.DownBps / 1024).ToString("0") + "K"),
        };

        float x = W * 0.505f;
        float step = W * 0.112f;

        foreach (var it in items)
        {
            Tracked(g, it.Label, _fTiny, _bDim, x, y, 1.9f * S);

            g.DrawString(it.Value, _fMid, _bWhite, x, y + 16 * S);
            var nw = g.MeasureString(it.Value, _fMid).Width;
            g.DrawString("%", _fSmall, _bDim, x + nw + 3 * S, y + 26 * S);

            using (var rule = new Pen(Color.FromArgb(52, 170, 210, 240), 1f * S))
                g.DrawLine(rule, x, y + 56 * S, x + step * 0.70f, y + 56 * S);

            if (it.Sub.Length > 0)
                Tracked(g, it.Sub, _fTiny, _bDim, x, y + 62 * S, 1.3f * S);

            x += step;
        }

        // ---- clock, stacked clear of the KPI block ----
        var now = DateTime.Now;
        var clock = now.ToString("HH:mm");
        var cw = g.MeasureString(clock, _fBig).Width;
        g.DrawString(clock, _fBig, _bWhite, W - 22 * S - cw, y - 4 * S);

        var lines = new[]
        {
            now.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant()
                + "  /  " + now.ToString("yyyy-MM-dd"),
            "UPTIME 2d 17h",
            "TODAY 3h 10m",
        };

        float ry = y + 32 * S;
        foreach (var line in lines)
        {
            var w = TrackedWidth(g, line, _fTiny, 1.3f * S);
            Tracked(g, line, _fTiny, _bDim, W - 22 * S - w, ry, 1.3f * S);
            ry += 10.5f * S;
        }

        // ---- audio meter ----
        if (_trackName != null) _track = _trackName();

        var audioLabel = _track.Length > 0
            ? "AUDIO " + (aud.Level * 100).ToString("0") + "%   \u266A " + _track
            : "AUDIO " + (aud.Level * 100).ToString("0") + "%";
        var aw = TrackedWidth(g, audioLabel, _fTiny, 1.3f * S);
        float ax = Math.Max(W * 0.55f, W - 22 * S - 58 * S - aw - 6 * S);

        Tracked(g, audioLabel, _fTiny, aud.Live ? _bAccent : _bDim, ax, ry + 2 * S, 1.3f * S);

        int lit = (int)Math.Round(aud.Level * 10);
        for (int i = 0; i < 10; i++)
        {
            float bx = W - 22 * S - 54 * S + i * 5.2f * S;
            using var bar = new Pen(i < lit
                ? Color.FromArgb(225, 143, 230, 255)
                : Color.FromArgb(38, 120, 150, 175), 3.2f * S);
            g.DrawLine(bar, bx, ry + 2 * S, bx, ry + 9 * S);
        }
    }

    /* ---------------- panel data ---------------- */

    private sealed class Row
    {
        public string Label = "";
        public string Value = "";
        public double Pct;
    }

    private sealed class Panel
    {
        public string Title = "";
        public string Tag = "";
        public Row[] Rows = Array.Empty<Row>();
    }

    private static string Clip(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max];
    }

    private Panel[] BuildPanels(Snapshot s, double level)
    {
        return new[]
        {
            new Panel
            {
                Title = "PROCESS FEED", Tag = "TOP CPU",
                Rows = new[]
                {
                    new Row { Label = "HALOFLUX", Value = "8%", Pct = 8 },
                    new Row { Label = "EXPLORER", Value = "4%", Pct = 4 },
                    new Row { Label = "SYSTEM", Value = "2%", Pct = 2 },
                },
            },
            new Panel
            {
                Title = "MEMORY", Tag = "COMMIT",
                Rows = new[]
                {
                    new Row { Label = "PHYSICAL", Value = (s.Mem.UsedMb / 1024).ToString("0.0") + "G", Pct = s.Mem.TotalMb > 0 ? s.Mem.UsedMb / s.Mem.TotalMb * 100 : 0 },
                    new Row { Label = "TOTAL", Value = (s.Mem.TotalMb / 1024).ToString("0") + "G", Pct = 100 },
                    new Row { Label = "LOAD", Value = (s.Mem.TotalMb > 0 ? s.Mem.UsedMb / s.Mem.TotalMb * 100 : 0).ToString("0") + "%", Pct = s.Mem.TotalMb > 0 ? s.Mem.UsedMb / s.Mem.TotalMb * 100 : 0 },
                },
            },
            new Panel
            {
                Title = "GPU", Tag = "NVIDIA",
                Rows = new[]
                {
                    new Row { Label = "UTIL", Value = s.Gpu.Util.ToString("0") + "%", Pct = s.Gpu.Util },
                    new Row { Label = "VRAM", Value = s.Gpu.MemUsedMb.ToString("0") + "M", Pct = 40 },
                    new Row { Label = "TEMP", Value = s.Gpu.TempC > 0 ? s.Gpu.TempC.ToString("0") + "C" : "--", Pct = s.Gpu.TempC },
                },
            },
            new Panel
            {
                Title = "NETWORK", Tag = "ADAPTER",
                Rows = new[]
                {
                    new Row { Label = "DOWN", Value = (s.Net.DownBps / 1024).ToString("0") + "K", Pct = Math.Min(100, s.Net.DownBps / 1048576 * 10) },
                    new Row { Label = "UP", Value = (s.Net.UpBps / 1024).ToString("0") + "K", Pct = Math.Min(100, s.Net.UpBps / 1048576 * 10) },
                    new Row { Label = "AUDIO", Value = (level * 100).ToString("0") + "%", Pct = level * 100 },
                },
            },
            new Panel
            {
                Title = "VOLUMES", Tag = "STORAGE",
                Rows = new[]
                {
                    new Row { Label = "C:", Value = "ok", Pct = 60 },
                    new Row { Label = "D:", Value = "ok", Pct = 30 },
                    new Row { Label = "E:", Value = "ok", Pct = 20 },
                },
            },
            new Panel
            {
                Title = "CORE MATRIX", Tag = "16 LOGICAL",
                Rows = new[]
                {
                    new Row { Label = "PEAK", Value = s.Cpu.Cores.Count > 0 ? s.Cpu.Cores.Max().ToString("0") + "%" : "0%", Pct = s.Cpu.Cores.Count > 0 ? s.Cpu.Cores.Max() : 0 },
                    new Row { Label = "AVG", Value = s.Cpu.Total.ToString("0") + "%", Pct = s.Cpu.Total },
                    new Row { Label = "LOAD", Value = s.Cpu.Processes.ToString(), Pct = Math.Min(100, s.Cpu.Processes / 4.0) },
                },
            },
            new Panel
            {
                Title = "I/O", Tag = "DISK",
                Rows = new[]
                {
                    new Row { Label = "READ", Value = (s.Io.ReadBps / 1024).ToString("0") + "K", Pct = Math.Min(100, s.Io.ReadBps / 1048576 * 20) },
                    new Row { Label = "WRITE", Value = (s.Io.WriteBps / 1024).ToString("0") + "K", Pct = Math.Min(100, s.Io.WriteBps / 1048576 * 20) },
                    new Row { Label = "QUEUE", Value = "0", Pct = 0 },
                },
            },
            new Panel
            {
                Title = "SYSTEM", Tag = "STATUS",
                Rows = new[]
                {
                    new Row { Label = "HOST", Value = "HALOFLUX", Pct = 0 },
                    new Row { Label = "PROC", Value = s.Cpu.Processes.ToString(), Pct = Math.Min(100, s.Cpu.Processes / 4.0) },
                    new Row { Label = "CPU", Value = Clip(s.Cpu.Name, 11), Pct = s.Cpu.Total },
                },
            },
        };
    }
}
