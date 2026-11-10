using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HalofluxDesk;

/// <summary>Renders the wallpaper page in a real WebView2 and writes a PNG 鈥?used to review the look.</summary>
public sealed class CaptureForm : Form
{
    private readonly WebView2 _web = new();
    private readonly string _url;
    private readonly string _out;
    private readonly Sampler _sampler;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 300 };
    private int _ticks;
    private bool _capturing;

    public CaptureForm(string url, string outPath, int width, int height, Sampler sampler)
    {
        _url = url;
        _out = outPath;
        _sampler = sampler;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(0, 0, width, height);
        TopMost = true;
        BackColor = Color.Black;

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Color.Black;
        Controls.Add(_web);

        Load += async (_, _) => await StartAsync();
        _timer.Tick += async (_, _) => await TickAsync();
    }

    private async Task StartAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(
            null,
            Path.Combine(AppContext.BaseDirectory, "webview-capture"));

        await _web.EnsureCoreWebView2Async(env);
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.CoreWebView2.Navigate(_url + "?t=" + DateTime.Now.Ticks);
        _timer.Start();
    }

    private async Task TickAsync()
    {
        _ticks++;
        if (_capturing) return;

        // wait for a real published sample, not just a fixed delay
        bool ready = _sampler.Latest.Time > 0 && _ticks >= 6;
        bool giveUp = _ticks > 60;
        if (!ready && !giveUp) return;
        _capturing = true;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_out)!);
            await using var stream = File.Create(_out);
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            Log.Write("captured " + _out);

            const string probe =
                "JSON.stringify({frames:window.__hfFrames||0,err:window.__hfErr||null,hitsSent:window.__hitsSent||0})";
            var dump = await _web.CoreWebView2.ExecuteScriptAsync(probe);
            Log.Write("page probe: " + dump);
        }
        catch (Exception ex)
        {
            Log.Write("capture failed: " + ex);
        }
        finally
        {
            _timer.Stop();
            Close();
            Application.Exit();
        }
    }
}

