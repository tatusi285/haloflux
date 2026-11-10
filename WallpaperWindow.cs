using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HalofluxDesk;

public sealed class WallpaperContext : ApplicationContext
{
    private readonly WallpaperWindow _window;

    public WallpaperContext(int port)
    {
        _window = new WallpaperWindow(port);
        _window.FormClosed += (_, _) => ExitThread();
        _window.Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _window.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Desktop layer that also accepts clicks.
/// The window sits at the bottom of the z-order (never above real apps) and is
/// click-through except while the pointer is over an interactive item, so the
/// desktop keeps behaving like a desktop.
/// </summary>
public sealed class WallpaperWindow : Form
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const int HTCLIENT = 1;

    private readonly int _port;
    private readonly WebView2 _web = new();
    private readonly System.Windows.Forms.Timer _keepBottom = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _hover = new() { Interval = 25 };
    private readonly NotifyIcon _tray;
    private readonly ThumbManager _thumbs = new();

    private readonly object _gate = new();
    private List<HitRect> _hits = new();
    private bool _transparent = true;
    private bool _iconsHidden;
    private bool _webReady;
    private bool _loggedHits;
    private int _hitsMsgCount;
    private IntPtr _mouseHook = IntPtr.Zero;
    private Native.LowLevelMouseProc? _mouseProc;
    private IntPtr _desktopView;

    private sealed class HitRect
    {
        public int X, Y, W, H;
        public string Action = "";
    }

    public WallpaperWindow(int port)
    {
        _port = port;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Text = "HalofluxDesk";
        Bounds = SystemInformation.VirtualScreen;
        TopMost = false;

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Color.Black;
        Controls.Add(_web);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open preview page", null, (_, _) => OpenPreview());
        menu.Items.Add("Reload wallpaper", null, (_, _) => Reload());
        menu.Items.Add("Restore desktop icons", null, (_, _) => RestoreDesktopIcons());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => CloseForReal());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "HALOFLUX desktop",
            Visible = true,
            ContextMenuStrip = menu,
        };

        Load += async (_, _) => await InitAsync();
        _keepBottom.Tick += (_, _) =>
        {
            try
            {
                if (_desktopHost == IntPtr.Zero || !Native.IsWindow(_desktopHost) || !Native.IsWindowVisible(Handle))
                    AttachToDesktop();
            }
            catch { }
        };
        _hover.Tick += (_, _) => TrackHover();
    }

    private void OpenPreview()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"http://127.0.0.1:{_port}/") { UseShellExecute = true });
        }
        catch { }
    }

    private async Task InitAsync()
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions(
                "--autoplay-policy=no-user-gesture-required");

            var env = await CoreWebView2Environment.CreateAsync(
                null,
                Path.Combine(AppContext.BaseDirectory, "webview-data"),
                options);

            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            core.WebMessageReceived += OnWebMessage;

            try
            {
                await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");
            }
            catch { }

            core.NavigationCompleted += async (_, _) =>
            {
                _webReady = true;
                try
                {
                    var info = await core.ExecuteScriptAsync(
                        "JSON.stringify({w:innerWidth,h:innerHeight,dpr:devicePixelRatio})");
                    Log.Write($"page metrics: {info}");
                }
                catch { }
                Log.Write("nav complete");
            };

            core.Navigate($"http://127.0.0.1:{_port}/?t={DateTime.Now.Ticks}"
                + (Environment.GetEnvironmentVariable("HALOFLUX_DEMO") == "1" ? "&autoplay=1" : ""));

            float scale = DeviceDpi / 96f;
            // live window thumbnails disabled: they surface raw DWM surfaces (e.g. the
            // blue TextInputHost window) that read as glitches on the wallpaper
            _thumbs.SetScreen((int)(Bounds.Width * scale), (int)(Bounds.Height * scale), scale);

            // plain wallpaper behind the icons: the desktop stays exactly as it was
            RestoreDesktopIcons();
            SetDesktopBackground();
            AttachToDesktop();
            _keepBottom.Interval = 2000;
            _keepBottom.Tick -= (_, _) => { };
            _keepBottom.Start();
            Log.Write("interactive desktop layer ready");
        }
        catch (Exception ex)
        {
            Log.Write("init failed: " + ex);
        }
    }

    /* ---------------- window behaviour ---------------- */

    private void BecomeDesktopLayer()
    {
        int ex = Native.GetWindowLong(Handle, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        ex &= ~WS_EX_TRANSPARENT;
        Native.SetWindowLong(Handle, GWL_EXSTYLE, ex);
        KeepAtBottom();
        _transparent = true;
    }

    private void KeepAtBottom()
    {
        try
        {
            Native.SetWindowPos(Handle, (IntPtr)1 /*HWND_BOTTOM*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        catch { }
    }

    private void SetTransparent(bool transparent)
    {
        if (_transparent == transparent) return;
        _transparent = transparent;
        int ex = Native.GetWindowLong(Handle, GWL_EXSTYLE);
        ex = transparent ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        Native.SetWindowLong(Handle, GWL_EXSTYLE, ex);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            long lp = m.LParam.ToInt64();
            int x = (short)(lp & 0xFFFF);
            int y = (short)((lp >> 16) & 0xFFFF);
            m.Result = (IntPtr)(HitTest(x, y) ? HTCLIENT : HTTRANSPARENT);
            return;
        }
        base.WndProc(ref m);
    }

    private bool HitTest(int screenX, int screenY)
    {
        try
        {
            lock (_gate)
            {
                foreach (var r in _hits)
                {
                    if (screenX >= r.X - 2 && screenX <= r.X + r.W + 2 &&
                        screenY >= r.Y - 2 && screenY <= r.Y + r.H + 2)
                        return true;
                }
            }
        }
        catch { }
        return false;
    }


    private IntPtr _desktopHost = IntPtr.Zero;

    private void AttachToDesktop()
    {
        try
        {
            var progman = Native.FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
            {
                Log.Write("attach: no progman");
                return;
            }

            // ask the shell to create the wallpaper host inside Progman
            Native.SendMessageTimeout(progman, 0x052C, (IntPtr)0xD, (IntPtr)1, 0, 1000, out _);

            // on current Windows 11 the icon host (SHELLDLL_DefView) is a child of
            // Progman and the wallpaper layer is a hidden WorkerW child next to it
            var target = progman;
            Native.SetParent(Handle, target);

            int style = Native.GetWindowLong(Handle, -16);
            style = (style | 0x40000000) & ~unchecked((int)0x80000000);
            Native.SetWindowLong(Handle, -16, style);

            var screen = Screen.PrimaryScreen!.Bounds;
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, screen.Width, screen.Height,
                0x0002 | 0x0001 | 0x0004);

            // put ourselves directly *below* the icon host: everything the shell draws
            // (icons, right click menu) then stays above us
            var defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            Native.SetWindowPos(Handle, defView, 0, 0, screen.Width, screen.Height,
                0x0002 | 0x0001 | 0x0004);
            Native.ShowWindow(Handle, 5 /*SW_SHOW*/);

            _desktopHost = target;
            Log.Write("attached below the icon host " + screen.Width + "x" + screen.Height);
        }
        catch (Exception ex)
        {
            Log.Write("attach failed: " + ex.Message);
        }
    }

    private void EnsureAttached()
    {
        try
        {
            if (_desktopHost == IntPtr.Zero || !Native.IsWindow(_desktopHost))
                AttachToDesktop();
        }
        catch { }
    }


    private static void SetDesktopBackground()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HalofluxDesk");
            Directory.CreateDirectory(dir);
            var bmp = Path.Combine(dir, "bg.bmp");

            if (!File.Exists(bmp))
            {
                using var fs = new FileStream(bmp, FileMode.Create);
                using var bw = new BinaryWriter(fs);
                int w = 16, h = 16, bytes = w * h * 4;
                bw.Write((byte)'B'); bw.Write((byte)'M');
                bw.Write(54 + bytes); bw.Write(0); bw.Write(54);
                bw.Write(40); bw.Write(w); bw.Write(h);
                bw.Write((short)1); bw.Write((short)32);
                bw.Write(0); bw.Write(bytes); bw.Write(2835); bw.Write(2835);
                bw.Write(0); bw.Write(0);
                for (int i = 0; i < w * h; i++) { bw.Write((byte)10); bw.Write((byte)8); bw.Write((byte)6); bw.Write((byte)0); }
            }

            Native.SystemParametersInfo(20 /*SPI_SETDESKWALLPAPER*/, 0, bmp, 0x01 | 0x02);
            Log.Write("desktop background set to solid");
        }
        catch (Exception ex)
        {
            Log.Write("background: " + ex.Message);
        }
    }

    /* ---------------- desktop icons ---------------- */

    private void HideDesktopIcons()
    {
        try
        {
            var progman = Native.FindWindow("Progman", null);
            var defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
            {
                Native.EnumWindows((h, _) =>
                {
                    var candidate = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (candidate != IntPtr.Zero) { defView = candidate; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            if (defView == IntPtr.Zero) return;

            _desktopView = Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
            if (_desktopView != IntPtr.Zero)
            {
                Native.ShowWindow(_desktopView, 0 /*SW_HIDE*/);
                _iconsHidden = true;
                Log.Write("desktop icons hidden");
            }
        }
        catch (Exception ex)
        {
            Log.Write("hide icons failed: " + ex.Message);
        }
    }

    public void RestoreDesktopIcons()
    {
        try
        {
            if (_desktopView != IntPtr.Zero && Native.IsWindow(_desktopView))
                Native.ShowWindow(_desktopView, 5 /*SW_SHOW*/);
            _iconsHidden = false;
            Log.Write("desktop icons restored");
        }
        catch (Exception ex)
        {
            Log.Write("restore icons failed: " + ex.Message);
        }
    }

    /* ---------------- hover / hit testing ---------------- */

    private void TrackHover()
    {
        try
        {
            if (!Native.GetCursorPos(out var pt)) return;
            bool over = false;
            lock (_gate)
            {
                foreach (var r in _hits)
                {
                    if (pt.X >= r.X && pt.X <= r.X + r.W && pt.Y >= r.Y && pt.Y <= r.Y + r.H)
                    {
                        over = true;
                        break;
                    }
                }
            }

            if (_webReady && _web.CoreWebView2 != null)
            {
                float scale = DeviceDpi / 96f;
                int cx = (int)(pt.X / scale);
                int cy = (int)(pt.Y / scale);
                _web.CoreWebView2.PostWebMessageAsJson(
                    $"{{\"type\":\"cursor\",\"x\":{cx},\"y\":{cy},\"over\":{(over ? "true" : "false")}}}");
            }
        }
        catch { }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            if (json == null) return;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "hits")
            {
                float scale = DeviceDpi / 96f;
                var list = new List<HitRect>();
                foreach (var el in root.GetProperty("rects").EnumerateArray())
                {
                    list.Add(new HitRect
                    {
                        X = (int)(el.GetProperty("x").GetDouble() * scale),
                        Y = (int)(el.GetProperty("y").GetDouble() * scale),
                        W = (int)(el.GetProperty("w").GetDouble() * scale),
                        H = (int)(el.GetProperty("h").GetDouble() * scale),
                        Action = el.TryGetProperty("action", out var a) ? (a.GetString() ?? "") : "",
                    });
                }
                lock (_gate) _hits = list;
                _hitsMsgCount++;
                if (_hitsMsgCount % 30 == 1)
                    Log.Write("hit regions: " + list.Count + " (msg " + _hitsMsgCount + ")");
                return;
            }

            if (type == "launch")
            {
                var path = root.GetProperty("path").GetString();
                if (!string.IsNullOrEmpty(path)) Launch(path);
            }
        }
        catch (Exception ex)
        {
            Log.Write("web message: " + ex.Message);
        }
    }

    private static void Launch(string path)
    {
        try
        {
            Log.Write("launch: " + path);

            // .lnk files: resolve the target first. Store / UWP apps have no real
            // target path, and those are the only ones that need the shell.
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string target = "";
                try
                {
                    var shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellType)!;
                        dynamic shortcut = shell.CreateShortcut(path);
                        target = (string)shortcut.TargetPath;
                        if (string.IsNullOrEmpty(target))
                            target = (string)shortcut.Arguments;
                    }
                }
                catch { }

                bool storeApp = string.IsNullOrEmpty(target)
                                || target.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0
                                || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

                if (!storeApp && File.Exists(target))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
                    {
                        UseShellExecute = true,
                    });
                    return;
                }

                // activation alias: hand it to the shell exactly like a double click
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            });
        }
        catch (Exception ex)
        {
            Log.Write("launch failed: " + ex.Message);
        }
    }


    private void InstallMouseHook()
    {
        try
        {
            if (_mouseHook != IntPtr.Zero) return;
            _mouseProc = MouseHookCallback;
            _mouseHook = Native.SetWindowsHookEx(14 /*WH_MOUSE_LL*/, _mouseProc, Native.GetModuleHandle(null), 0);
            Log.Write(_mouseHook != IntPtr.Zero ? "mouse hook installed" : "mouse hook failed: " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            Log.Write("mouse hook: " + ex.Message);
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam == (IntPtr)0x0201) // WM_LBUTTONDOWN
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                HitRect? hit = null;
                lock (_gate)
                {
                    foreach (var r in _hits)
                    {
                        if (!string.IsNullOrEmpty(r.Action) &&
                            info.pt.X >= r.X && info.pt.X <= r.X + r.W &&
                            info.pt.Y >= r.Y && info.pt.Y <= r.Y + r.H)
                        {
                            hit = r;
                            break;
                        }
                    }
                }

                if (hit != null)
                {
                    Launch(hit.Action);
                    return (IntPtr)1; // swallow: the click was ours
                }
            }
        }
        catch { }

        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /* ---------------- lifecycle ---------------- */

    private void Reload()
    {
        try
        {
            if (_web.CoreWebView2 != null)
                _web.CoreWebView2.Navigate($"http://127.0.0.1:{_port}/?t={DateTime.Now.Ticks}");
        }
        catch (Exception ex)
        {
            Log.Write("reload: " + ex.Message);
        }
    }

    private void CloseForReal()
    {
        _keepBottom.Stop();
        _hover.Stop();
        try { if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook); } catch { }
        RestoreDesktopIcons();
        _thumbs.Dispose();
        _tray.Visible = false;
        _web.Dispose();
        Close();
        Dispose();
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        e.Cancel = true;
        base.OnFormClosing(e);
    }
}
