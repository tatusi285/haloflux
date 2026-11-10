using System.Windows.Forms;

namespace HalofluxDesk;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Crash("unhandled", e.ExceptionObject as Exception ?? new Exception("unknown"));
        Application.ThreadException += (_, e) => Log.Crash("ui thread", e.Exception);

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Log.Crash("main", ex);
            throw;
        }
    }

    private static void Run(string[] args)
    {
        Log.Write("--- start: " + string.Join(' ', args) + "   [" + Log.IntegrityLevel() + " integrity]");

        bool serveOnly = args.Any(a => a.Equals("--serve", StringComparison.OrdinalIgnoreCase));
        bool native = args.Any(a => a.Equals("--native", StringComparison.OrdinalIgnoreCase));
        bool wallOnly = args.Any(a => a.Equals("--wall", StringComparison.OrdinalIgnoreCase));
        string? nativeSnap = args.FirstOrDefault(a => a.StartsWith("--native-snap=", StringComparison.OrdinalIgnoreCase))?["--native-snap=".Length..];
        int port = 47821;
        string? capture = null;
        int capW = 1707, capH = 960;
        foreach (var a in args)
        {
            if (a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(a["--port=".Length..], out port);
            else if (a.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase))
                capture = a["--capture=".Length..];
            else if (a.StartsWith("--size=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = a["--size=".Length..].Split('x');
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0], out capW);
                    int.TryParse(parts[1], out capH);
                }
            }
        }

        var sampler = new Sampler();
        sampler.Start();

        var audio = new AudioProbe();
        audio.Start();

        // the native wallpaper does not consume the web API, so skip the expensive
        // window thumbnailing and icon extraction entirely
        var windows = new WindowWatch();
        var icons = new IconWatch();
        if (!native && !wallOnly)
        {
            windows.Start();
            icons.Start();
        }

        var web = new WebHost(sampler, audio, windows, icons, port);
        web.Start();
        Log.Write($"listening on http://127.0.0.1:{web.Port}/");

        if (serveOnly)
        {
            Log.Write("serve-only mode");
            Thread.Sleep(Timeout.Infinite);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (capture != null)
        {
            Application.Run(new CaptureForm($"http://127.0.0.1:{web.Port}/", capture, capW, capH, sampler));
            sampler.Stop();
        audio.Dispose();
            return;
        }

        if (nativeSnap != null)
        {
            // headless render: lets the scene be inspected without touching the desktop
            var shot = new NativeWall(sampler, audio);
            System.Threading.Thread.Sleep(1500);
            shot.RenderFrame();
            var img = shot.Frame;
            if (img != null)
            {
                img.Save(nativeSnap, System.Drawing.Imaging.ImageFormat.Png);
                Log.Write("snapshot: " + nativeSnap + " " + img.Width + "x" + img.Height);
            }
            sampler.Stop();
            audio.Dispose();
            return;
        }

        // Windows starts a .scr with /s (full screen) or /p <hwnd> (preview)
        bool saver = args.Any(a =>
            a.Equals("/s", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-s", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/S", StringComparison.OrdinalIgnoreCase));

        if (saver)
        {
            using var saverMusic = new MusicPlayer();
            using var form = new ScreensaverForm($"http://127.0.0.1:{web.Port}/", saverMusic);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.Run(form);
            sampler.Stop();
            audio.Dispose();
            return;
        }

        if (args.Any(a => a.StartsWith("/p", StringComparison.OrdinalIgnoreCase)))
            return;   // preview pane: nothing to draw

        if (native || wallOnly)
        {
            // the desktop wallpaper is silent; the screensaver is what carries the music
            MusicPlayer? music = wallOnly ? null : new MusicPlayer();
            music?.Start();

            var renderer = new NativeWall(sampler, audio,
                music == null ? null : () => music.Current, wallOnly);
            using var wall = new D3DDesk(renderer);

            var appCtx = new ApplicationContext();
            using var tray = new TrayControl(music, () =>
            {
                appCtx.ExitThread();
                Application.Exit();
            });

            Application.Run(appCtx);

            sampler.Stop();
            audio.Dispose();
            return;
        }

        using var context = new WallpaperContext(web.Port);
        Application.Run(context);
        sampler.Stop();
        audio.Dispose();
    }
}
