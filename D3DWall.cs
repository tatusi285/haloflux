using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HalofluxDesk;

/// <summary>
/// Owns a DXGI swap chain bound to a child window of the desktop. Frames arrive as
/// GDI+ bitmaps and are uploaded into a dynamic texture, then copied to the back
/// buffer. This is the only rendering path that survives the Windows 11 compositor.
/// </summary>
internal sealed class D3DWall : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly RawHost _host;

    private ID3D11Texture2D? _upload;
    private int _bufW, _bufH;
    private int _frames;
    public string LastStage { get; private set; } = "new";

    /// <summary>False once Explorer restarts and takes the desktop window with it.</summary>
    public bool HostAlive => _host != null && Native.IsWindow(_host.Handle);

    public D3DWall(int width, int height)
    {
        var parent = FindDesktopHost();
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        _host = new RawHost(parent, screen.Width, screen.Height);

        Log.Write("d3d: host window " + _host.Handle);
        var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            levels, out _device!, out _context!).CheckError();

        Log.Write("d3d: device ok");
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width = width,
            Height = height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };

        Log.Write("d3d: factory ok, creating swap chain");
        _swapChain = factory.CreateSwapChainForHwnd(_device, _host.Handle, desc);
        Log.Write("d3d: swap chain ready on hwnd " + _host.Handle + " " + width + "x" + height);
    }

    private static IntPtr FindDesktopHost()
    {
        var progman = Native.FindWindow("Progman", null);
        Native.SendMessageTimeout(progman, 0x052C, (IntPtr)0xD, (IntPtr)1, 0, 1000, out _);
        var worker = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        return worker != IntPtr.Zero ? worker : progman;
    }

    public void Blit(Bitmap frame)
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);

        if (_upload == null)
        {
            _bufW = frame.Width;
            _bufH = frame.Height;

            _upload = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = _bufW,
                Height = _bufH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.None,
            });

            Log.Write("d3d: upload texture " + _bufW + "x" + _bufH);
        }

        var data = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        try
        {
            // the texture lives for the whole session, so this is one upload per frame
            _context.UpdateSubresource(_upload, 0, null, data.Scan0, data.Stride, 0);
        }
        finally
        {
            frame.UnlockBits(data);
        }

        _context.CopyResource(backBuffer, _upload);
        _swapChain.Present(0, PresentFlags.None);

        if (++_frames == 30) Log.Write("d3d: 30 frames presented");
    }

    public void Dispose()
    {
        _upload?.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
        _host.Dispose();
    }

    private sealed class RawHost : NativeWindow, IDisposable
    {
        public void Dispose() => DestroyHandle();

        public RawHost(IntPtr parent, int w, int h)
        {
            CreateHandle(new CreateParams
            {
                Caption = "HalofluxD3D",
                X = 0,
                Y = 0,
                Width = w,
                Height = h,
                Style = unchecked((int)0x40000000) | 0x10000000, // WS_CHILD | WS_VISIBLE
                ExStyle = 0x00000080,                              // WS_EX_TOOLWINDOW
                Parent = parent,
            });
        }
    }
}
