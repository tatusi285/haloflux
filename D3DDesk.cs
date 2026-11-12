using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HalofluxDesk;

/// <summary>
/// Drives the native renderer and pushes every frame onto the desktop through the
/// DXGI swap chain. This is the only path that survives the Windows 11 compositor.
/// It also repairs itself: the desktop window dies whenever Explorer restarts, and the
/// device is lost after a suspend, so the chain is rebuilt instead of giving up.
/// </summary>
internal sealed class D3DDesk : IDisposable
{
    private readonly NativeWall _renderer;
    private D3DWall? _wall;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _watch;
    private int _lastFrameId = -2;
    private bool _pausedForFullscreen;

    private double _renderMs, _blitMs;
    private int _frames;
    private DateTime _stamp = DateTime.UtcNow;
    private DateTime _health = DateTime.UtcNow;
    private int _failures;

    public D3DDesk(NativeWall renderer)
    {
        _renderer = renderer;
        _wall = new D3DWall(NativeWall.SurfaceW, NativeWall.SurfaceH);

        _timer = new System.Windows.Forms.Timer { Interval = 20 };
        _timer.Tick += (_, _) => Pump();
        _timer.Start();

        // a fullscreen game or video does not need a wallpaper burning the GPU behind it
        _watch = new System.Windows.Forms.Timer { Interval = 2000 };
        _watch.Tick += (_, _) => CheckFullscreen();
        _watch.Start();

        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.DisplaySettingsChanged += OnDisplay;
    }

    private void OnPower(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Recover("resume");
    }

    private void OnDisplay(object? sender, EventArgs e) => Recover("display change");

    private void CheckFullscreen()
    {
        try
        {
            bool busy = Native.ForegroundIsFullscreen() || Native.GameRunning();

            if (busy && !_pausedForFullscreen)
            {
                _pausedForFullscreen = true;
                _timer.Stop();
                Log.Write("paused: a fullscreen app is in front");
            }
            else if (!busy && _pausedForFullscreen)
            {
                _pausedForFullscreen = false;
                _lastFrameId = -2;
                _timer.Start();
                Log.Write("resumed: fullscreen app is gone");
            }
        }
        catch (Exception ex)
        {
            Log.Write("fullscreen check: " + ex.Message);
        }
    }

    private void Pump()
    {
        try
        {
            // the wallpaper only shows video: when the frame has not changed there is
            // nothing to render, upload or present
            int id = _renderer.FrameId;
            if (id >= 0 && id == _lastFrameId) return;
            _lastFrameId = id;

            if ((DateTime.UtcNow - _health).TotalSeconds > 3)
            {
                _health = DateTime.UtcNow;
                if (_wall == null || !_wall.HostAlive) Recover("host lost");
            }

            if (_wall == null) return;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            _renderer.RenderFrame();
            var t1 = clock.Elapsed.TotalMilliseconds;

            var frame = _renderer.Frame;
            if (frame != null) _wall.Blit(frame);
            var t2 = clock.Elapsed.TotalMilliseconds;

            // a frame that spans a suspend is not a performance sample
            if (t2 < 2000)
            {
                _renderMs += t1;
                _blitMs += t2 - t1;
                _frames++;
            }

            _failures = 0;

            if ((DateTime.UtcNow - _stamp).TotalSeconds >= 5)
            {
                _renderer.DiagFps = _frames / (DateTime.UtcNow - _stamp).TotalSeconds;

                if (_frames > 0)
                {
                    Log.Write(string.Format("perf: render {0:0.0} ms  blit {1:0.0} ms  fps {2:0.0}",
                        _renderMs / _frames, _blitMs / _frames, _frames / (DateTime.UtcNow - _stamp).TotalSeconds));
                }

                _renderMs = _blitMs = 0;
                _frames = 0;
                _stamp = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _failures++;
            Log.Write("d3d stage: " + (_wall?.LastStage ?? "none") + " -> " + ex.Message);

            if (_failures >= 3)
            {
                _failures = 0;
                Recover("exception");
            }
        }
    }

    private void Recover(string why)
    {
        try
        {
            Log.Write("d3d: rebuilding chain (" + why + ")");
            _wall?.Dispose();
            _wall = null;
            _wall = new D3DWall(NativeWall.SurfaceW, NativeWall.SurfaceH);
        }
        catch (Exception ex)
        {
            Log.Write("d3d: rebuild failed: " + ex.Message);
            Log.Crash("recover", ex);
        }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.DisplaySettingsChanged -= OnDisplay;

        _timer.Stop();
        _timer.Dispose();
        _watch.Stop();
        _watch.Dispose();
        _wall?.Dispose();
    }
}
