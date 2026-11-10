using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace HalofluxDesk;

/// <summary>Captures small stills of the windows you would see in Alt+Tab.</summary>
public sealed class WindowWatch
{
    public sealed class Item
    {
        public string Title { get; set; } = "";
        public string Png { get; set; } = "";
        public string Icon { get; set; } = "";
        public string Proc { get; set; } = "";
    }

    private volatile List<Item> _snapshot = new();
    private Thread? _thread;

    public List<Item> Snapshot() => _snapshot;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "haloflux-windows" };
        _thread.Start();
    }

    private void Loop()
    {
        while (true)
        {
            try
            {
                var list = Capture();
                if (list.Count > 0) _snapshot = list;
            }
            catch (Exception ex)
            {
                Log.Write("windows: " + ex.Message);
            }
            Thread.Sleep(3000);
        }
    }


    /// <summary>Handles of the windows currently shown as previews.</summary>
    public static List<IntPtr> CurrentHandles()
    {
        var handles = new List<IntPtr>();
        try
        {
            Native.EnumWindows((h, _) =>
            {
                if (!Win.IsWindowVisible(h)) return true;
                if (Win.GetWindowTextLength(h) == 0) return true;
                if (Win.IsIconic(h)) return true;
                if ((Win.GetWindowLong(h, -20) & 0x00000080) != 0) return true;
                if (!Win.GetWindowRect(h, out var r)) return true;
                if (r.Width < 200 || r.Height < 150) return true;
                handles.Add(h);
                return handles.Count < 6;
            }, IntPtr.Zero);
        }
        catch { }
        return handles;
    }

    private static List<Item> Capture()
    {
        var handles = new List<IntPtr>();

        Native.EnumWindows((h, _) =>
        {
            if (!Win.IsWindowVisible(h)) return true;
            if (Win.GetWindowTextLength(h) == 0) return true;
            if (Win.IsIconic(h)) return true;
            if ((Win.GetWindowLong(h, -20) & 0x00000080) != 0) return true; // tool windows
            if (!Win.GetWindowRect(h, out var r)) return true;
            if (r.Width < 200 || r.Height < 150) return true;
            handles.Add(h);
            return handles.Count < 8;
        }, IntPtr.Zero);

        var items = new List<Item>();
        foreach (var h in handles)
        {
            try
            {
                var title = Win.Title(h);
                if (string.IsNullOrWhiteSpace(title)) continue;

                Win.GetWindowRect(h, out var r);
                int w = Math.Min(2560, Math.Max(1, r.Width));
                int hh = Math.Min(1440, Math.Max(1, r.Height));

                string png = "";
                using (var native = new Bitmap(w, hh))
                {
                    bool ok;
                    using (var g = Graphics.FromImage(native))
                    {
                        IntPtr dc = g.GetHdc();
                        ok = Win.PrintWindow(h, dc, 2) || Win.PrintWindow(h, dc, 0) || Win.PrintWindow(h, dc, 1);
                        g.ReleaseHdc(dc);
                    }

                    if (ok)
                    {
                        using var thumb = new Bitmap(240, 135);
                        using (var g = Graphics.FromImage(thumb))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g.Clear(Color.Black);
                            float scale = Math.Min(240f / w, 135f / hh);
                            int tw = Math.Max(1, (int)(w * scale));
                            int th = Math.Max(1, (int)(hh * scale));
                            g.DrawImage(native, (240 - tw) / 2, (135 - th) / 2, tw, th);
                        }
                        if (!WinProbe.TooDark(thumb))
                        {
                            using var ms = new MemoryStream();
                            thumb.Save(ms, ImageFormat.Png);
                            png = Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }

                items.Add(new Item
                {
                    Title = title,
                    Png = png,
                    Icon = WinProbe.ProcessIcon(h),
                    Proc = WinProbe.ProcessName(h),
                });
            }
            catch (Exception ex) { Log.Write("window capture failed: " + ex.Message); }
        }

        Log.Write($"windows: {handles.Count} candidates, {items.Count} captured");
        return items;
    }
}

/// <summary>Reads the desktop shortcuts and extracts their real icons.</summary>
public sealed class IconWatch
{
    public sealed class Item
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Png { get; set; } = "";
    }

    private volatile List<Item> _snapshot = new();

    public List<Item> Snapshot() => _snapshot;

    public void Start()
    {
        Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    var list = Capture();
                    if (list.Count > 0) _snapshot = list;
                }
                catch (Exception ex)
                {
                    Log.Write("icons: " + ex.Message);
                }
                Thread.Sleep(60000);
            }
        });
    }

    private static List<Item> Capture()
    {
        var dirs = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        // folders stay single entries: their contents are not expanded onto the ring

        var items = new List<Item>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFileSystemEntries(dir); }
            catch { continue; }

            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    var attrs = info.Attributes;
                    if (info.Name.StartsWith(".")) continue;
                    if (info.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                }
                catch { continue; }

                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileName(file);
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;

                try
                {
                    var png = Extract(file);
                    items.Add(new Item { Name = name, Path = file, Png = png });
                }
                catch (Exception ex) { Log.Write("icon failed: " + name + " -> " + ex.Message); }

                if (items.Count >= 64) return items;
            }
        }

        Log.Write($"icons: {items.Count} extracted");
        return items;
    }

    private static string Extract(string path)
    {
        var info = new ShFileInfo();
        const uint flags = 0x000000100 /*SHGFI_ICON*/ | 0x000000000 /*LARGE*/ | 0x000000020 /*SHGFI_USEFILEATTRIBUTES off*/;
        IntPtr res = ShGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), 0x000000100);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) throw new InvalidOperationException("no icon");

        using var icon = Icon.FromHandle(info.hIcon);
        using var bmp = icon.ToBitmap();
        using var dest = new Bitmap(48, 48);
        using (var g = Graphics.FromImage(dest))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, 0, 0, 48, 48);
        }

        DestroyIcon(info.hIcon);

        using var ms = new MemoryStream();
        dest.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr ShGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

internal static class WinProbe
{
    public static bool TooDark(Bitmap bmp)
    {
        long sum = 0;
        int n = 0;
        for (int y = 0; y < bmp.Height; y += 6)
        {
            for (int x = 0; x < bmp.Width; x += 6)
            {
                var c = bmp.GetPixel(x, y);
                sum += c.R + c.G + c.B;
                n++;
            }
        }
        return n == 0 || sum / (n * 3) < 6;
    }

    public static string ProcessName(IntPtr hwnd)
    {
        try
        {
            Win.GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    public static string ProcessIcon(IntPtr hwnd)
    {
        try
        {
            Win.GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            var exe = p.MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return "";
            using var ico = Icon.ExtractAssociatedIcon(exe);
            if (ico == null) return "";
            using var bmp = ico.ToBitmap();
            using var dest = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(dest))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, 32, 32);
            }
            using var ms = new MemoryStream();
            dest.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return ""; }
    }
}

internal static class Win
{
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;

    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Native.RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public static string Title(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, 512);
        return sb.ToString();
    }
}
