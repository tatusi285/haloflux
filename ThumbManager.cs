using System.Drawing;
using System.Runtime.InteropServices;

namespace HalofluxDesk;

/// <summary>
/// Live window previews around the ring. DWM composites the source window into a
/// small host window, so we never copy pixels ourselves (PrintWindow is blocked for
/// these windows; the compositor is not).
/// </summary>
public sealed class ThumbManager : IDisposable
{
    private const int MaxSlots = 6;

    private sealed class Slot
    {
        public Form Host = null!;
        public IntPtr Thumb;
        public long Src;
    }

    private readonly List<Slot> _slots = new();
    private readonly object _gate = new();
    private int _w, _h;
    private float _scale = 1f;

    public void Start()
    {
        Task.Run(Loop);
    }

    public void SetScreen(int w, int h, float scale)
    {
        lock (_gate) { _w = w; _h = h; _scale = scale <= 0 ? 1f : scale; }
    }

    private void Loop()
    {
        while (true)
        {
            try
            {
                Refresh();
            }
            catch (Exception ex)
            {
                Log.Write("thumbs: " + ex.Message);
            }
            Thread.Sleep(2500);
        }
    }

    private void Refresh()
    {
        var targets = WindowWatch.CurrentHandles();
        if (targets.Count == 0) return;

        while (_slots.Count < Math.Min(MaxSlots, targets.Count))
        {
            var host = new Form
            {
                AutoScaleMode = AutoScaleMode.None,
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                BackColor = Color.FromArgb(6, 10, 15),
                Size = new Size(200, 112),
                TopMost = false,
            };
            host.Show();
            var ex = Native.GetWindowLong(host.Handle, -20);
            Native.SetWindowLong(host.Handle, -20, ex | 0x08000000 | 0x00000080);
            Native.SetWindowPos(host.Handle, (IntPtr)1 /*HWND_BOTTOM*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            _slots.Add(new Slot { Host = host });
        }

        lock (_gate)
        {
            if (_w <= 0) return;
            int n = Math.Min(_slots.Count, targets.Count);

            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (i >= n)
                {
                    if (slot.Thumb != IntPtr.Zero)
                    {
                        DwmUnregisterThumbnail(slot.Thumb);
                        slot.Thumb = IntPtr.Zero;
                        slot.Host.Hide();
                    }
                    continue;
                }

                var src = targets[i];
                if (slot.Thumb != IntPtr.Zero && slot.Src == src) continue;

                if (slot.Thumb != IntPtr.Zero)
                {
                    DwmUnregisterThumbnail(slot.Thumb);
                    slot.Thumb = IntPtr.Zero;
                }

                // place around the ring, in physical pixels
                double cx = _w * 0.635, cy = _h * 0.445;
                double radius = _h * 0.42;
                double ang = -Math.PI / 2 + i * (Math.PI * 2 / Math.Max(1, n)) + 0.55;
                int bw = (int)(_h * 0.155);
                int bh = (int)(bw * 0.60);
                int x = (int)(cx + Math.Cos(ang) * radius - bw / 2.0);
                int y = (int)(cy + Math.Sin(ang) * radius - bh / 2.0);

                if (y + bh > _h - (int)(_h * 0.29)) continue; // keep clear of the HUD

                // WinForms reports and accepts dimensions in logical units; convert from physical
                float s = 1f;
                slot.Host.Bounds = new Rectangle((int)(x * s), (int)(y * s), (int)(bw * s), (int)(bh * s));
                slot.Host.Show();

                IntPtr thumb;
                int hr = DwmRegisterThumbnail(slot.Host.Handle, src, out thumb);
                if (hr != 0)
                {
                    Log.Write("dwm register failed hr=" + hr);
                    continue;
                }
                Log.Write("thumb ok " + x + "," + y + " " + bw + "x" + bh);

                Native.GetWindowRect(slot.Host.Handle, out var actual);
                int aw = Math.Max(8, actual.Right - actual.Left);
                int ah = Math.Max(8, actual.Bottom - actual.Top);

                var props = new ThumbnailProps
                {
                    flags = 0x1 | 0x4 | 0x8 | 0x10,
                    destination = new RECT { Left = 2, Top = 2, Right = aw - 2, Bottom = ah - 2 },
                    opacity = 235,
                    visible = true,
                    sourceClientAreaOnly = true,
                };
                DwmUpdateThumbnailProperties(thumb, ref props);

                slot.Thumb = thumb;
                slot.Src = src;
            }
        }
    }

    public void Dispose()
    {
        foreach (var s in _slots)
        {
            try
            {
                if (s.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(s.Thumb);
                s.Host.Close();
                s.Host.Dispose();
            }
            catch { }
        }
        _slots.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProps
    {
        public int flags;
        public RECT destination;
        public RECT source;
        public byte opacity;
        public bool visible;
        public bool sourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbnailProps props);
}
