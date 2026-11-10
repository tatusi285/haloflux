using System.Drawing;
using System.Windows.Forms;

namespace HalofluxDesk;

/// <summary>
/// Tray icon for the wallpaper: the wallpaper itself lives behind the desktop icons
/// and cannot be clicked, so its controls have to live in the notification area.
/// </summary>
internal sealed class TrayControl : IDisposable
{
    private readonly NotifyIcon? _icon;
    private readonly MusicPlayer? _music;
    private ToolStripMenuItem _playItem = null!;
    private NotifyIcon? _iconLite;

    public TrayControl(MusicPlayer? music, Action quit)
    {
        _music = music;

        var menu = new ContextMenuStrip();

        if (_music == null)
        {
            var quitItem = new ToolStripMenuItem("退出壁纸");
            quitItem.Click += (_, _) => quit();
            menu.Items.Add(quitItem);

            _iconLite = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Haloflux 动态壁纸",
                Visible = true,
                ContextMenuStrip = menu,
            };
            return;
        }

        _playItem = new ToolStripMenuItem("暂停音乐");
        _playItem.Click += (_, _) =>
        {
            _music.Toggle();
            Refresh();
        };
        menu.Items.Add(_playItem);

        var prev = new ToolStripMenuItem("上一首");
        prev.Click += (_, _) => { _music.Previous(); Refresh(); };
        menu.Items.Add(prev);

        var next = new ToolStripMenuItem("下一首");
        next.Click += (_, _) => { _music.Next(); Refresh(); };
        menu.Items.Add(next);

        var mode = new ToolStripMenuItem("播放模式");
        foreach (var (label, value) in new[]
                 {
                     ("顺序播放", PlayMode.Sequential),
                     ("单曲循环", PlayMode.LoopOne),
                     ("随机播放", PlayMode.Shuffle),
                 })
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) =>
            {
                while (_music.Mode != value) _music.CycleMode();
                Refresh();
            };
            mode.DropDownItems.Add(item);
        }
        menu.Items.Add(mode);

        menu.Items.Add(new ToolStripSeparator());

        var track = new ToolStripMenuItem("当前：—") { Enabled = false };
        menu.Items.Add(track);
        menu.Opening += (_, _) =>
        {
            Refresh();
            track.Text = "当前：" + _music.Current + (_music.Playing ? "" : "（已暂停）") + "  ·  " + _music.ModeName;
        };

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("退出壁纸");
        exit.Click += (_, _) => quit();
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Haloflux 桌面壁纸",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => { _music?.Toggle(); Refresh(); };
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            _playItem.Text = _music != null && _music.Playing ? "暂停音乐" : "播放音乐";
        }
        catch { }
    }

    public void Dispose()
    {
        if (_iconLite != null)
        {
            _iconLite.Visible = false;
            _iconLite.Dispose();
        }

        if (_icon != null)
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
