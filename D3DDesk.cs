using System.Drawing;
using System.Windows.Forms;

namespace HalofluxDesk;

/// <summary>
/// Drives the native renderer and pushes every frame onto the desktop through the
/// DXGI swap chain. This is the only path that survives the Windows 11 compositor.
/// </summary>
internal sealed class D3DDesk : IDisposable
{
    private readonly NativeWall _renderer;
    private readonly D3DWall _wall;
    private readonly System.Windows.Forms.Timer _timer;

    public D3DDesk(NativeWall renderer)
    {
        _renderer = renderer;

        _wall = new D3DWall(NativeWall.SurfaceW, NativeWall.SurfaceH);

        _timer = new System.Windows.Forms.Timer { Interval = 33 }; // ~30 fps
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
    }

    private double _renderMs, _blitMs;
    private int _frames;
    private DateTime _stamp = DateTime.UtcNow;

    private void Pump()
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _renderer.RenderFrame();
            var t1 = clock.Elapsed.TotalMilliseconds;

            var frame = _renderer.Frame;
            if (frame != null) _wall.Blit(frame);
            var t2 = clock.Elapsed.TotalMilliseconds;

            _renderMs += t1;
            _blitMs += t2 - t1;
            _frames++;

            if ((DateTime.UtcNow - _stamp).TotalSeconds >= 5)
            {
                Log.Write(string.Format("perf: render {0:0.0} ms  blit {1:0.0} ms  fps {2:0.0}",
                    _renderMs / _frames, _blitMs / _frames, _frames / (DateTime.UtcNow - _stamp).TotalSeconds));
                _frames = 0;
                _stamp = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Log.Write("d3d stage: " + _wall.LastStage + " -> " + ex.Message);
            Log.Crash("d3ddesk", ex);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _wall.Dispose();
    }
}
