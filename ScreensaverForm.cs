using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HalofluxDesk;

/// <summary>
/// Full screen presentation of the wallpaper, used as a Windows screensaver.
/// It carries the music and the whole HUD that the desktop wallpaper no longer shows.
/// </summary>
internal sealed class ScreensaverForm : Form
{
    private readonly string _url;
    private readonly MusicPlayer _music;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private Point _origin;
    private bool _ready;
    private System.Windows.Forms.Timer? _watch;
    private bool _armed;
    private Label? _osd;
    private System.Windows.Forms.Timer? _osdTimer;

    // ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
    private const uint StayAwake = 0x80000000 | 0x00000001 | 0x00000002;
    private const uint Release = 0x80000000;

    public ScreensaverForm(string url, MusicPlayer music)
    {
        _url = url;
        _music = music;

        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        Cursor.Hide();
        ImeMode = ImeMode.Disable;
        _web.ImeMode = ImeMode.Disable;

        Controls.Add(_web);

        _osd = new Label
        {
            AutoSize = true,
            BackColor = Color.FromArgb(120, 0, 0, 0),
            ForeColor = Color.FromArgb(230, 240, 250, 255),
            Font = new Font("Consolas", 13f),
            Padding = new Padding(14, 8, 14, 8),
            Visible = false,
        };
        Controls.Add(_osd);
        _osd.BringToFront();

        Load += async (_, _) =>
        {
            try
            {
                var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
                var env = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(AppContext.BaseDirectory, "webview-data"), options);

                await _web.EnsureCoreWebView2Async(env);

                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                _web.CoreWebView2.Navigate(_url + (url.Contains('?') ? "&" : "?") + "autoplay=1");

                _music.Start();
                Native.SetThreadExecutionState(StayAwake);

                // ignore the first moment: the mouse usually twitches while the window appears
                _origin = Cursor.Position;
                _watch = new System.Windows.Forms.Timer { Interval = 120 };
                _watch.Tick += (_, _) =>
                {
                    if (!_armed) { _armed = true; _origin = Cursor.Position; return; }
                    if (ShouldExit()) Quit();
                };
                _watch.Start();
                _ready = true;
            }
            catch (Exception ex)
            {
                Log.Write("screensaver: " + ex.Message);
                Close();
            }
        };

        MouseMove += (_, e) =>
        {
            if (!_ready) return;
            var p = Cursor.Position;
            if (Math.Abs(p.X - _origin.X) > 12 || Math.Abs(p.Y - _origin.Y) > 12) Quit();
        };

        MouseDown += (_, _) => Quit();
        KeyDown += (_, _) => Quit();
    }

    private static bool Pressed(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void ShowOsd()
    {
        if (_osd == null) return;

        var state = _music.Playing ? "\u25B6" : "\u23F8";
        _osd.Text = state + "  " + _music.Current + "   (" + (_music.Index + 1) + "/" + _music.Count + ")"
                    + "   \u00B7 " + _music.ModeName;
        _osd.Location = new Point((ClientSize.Width - _osd.PreferredWidth) / 2, ClientSize.Height - 120);
        _osd.Visible = true;
        _osd.BringToFront();

        _osdTimer?.Stop();
        _osdTimer = new System.Windows.Forms.Timer { Interval = 2600 };
        _osdTimer.Tick += (_, _) => { _osd.Visible = false; _osdTimer?.Stop(); };
        _osdTimer.Start();
    }

    private bool ShouldExit()
    {
        var p = Cursor.Position;
        if (Math.Abs(p.X - _origin.X) > 10 || Math.Abs(p.Y - _origin.Y) > 10) return true;

        // any mouse button or a spread of keys ends the saver, even if the web view has focus
        if ((Native.GetAsyncKeyState(0x01) & 0x8000) != 0) return true;   // left
        if ((Native.GetAsyncKeyState(0x02) & 0x8000) != 0) return true;   // right
        if ((Native.GetAsyncKeyState(0x04) & 0x8000) != 0) return true;   // middle

        // arrows and space drive the player, everything else dismisses the saver
        if (Pressed(0x25)) { _music.Previous(); ShowOsd(); return false; }   // left
        if (Pressed(0x27)) { _music.Next(); ShowOsd(); return false; }       // right
        if (Pressed(0x20)) { _music.Toggle(); ShowOsd(); return false; }     // space
        if (Pressed(0x4D)) { _music.CycleMode(); ShowOsd(); return false; }   // m

        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            if (vk is 0x25 or 0x27 or 0x20 or 0x4D) continue;
            if (vk is >= 0x0A and <= 0x0C) continue;
            if (Pressed(vk)) return true;
        }

        return false;
    }

    private void Quit()
    {
        _watch?.Stop();
        try { Native.SetThreadExecutionState(Release); } catch { }
        try { _music.Stop(); } catch { }
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { Cursor.Show(); } catch { }
            _web.Dispose();
        }
        base.Dispose(disposing);
    }
}
