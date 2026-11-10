using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HalofluxDesk;

/// <summary>Minimal loopback HTTP server (no framework dependency): static files + /api/stats.</summary>
public sealed class WebHost : IDisposable
{
    public int Port { get; }

    private readonly Sampler _sampler;
    private readonly AudioProbe _audio;
    private readonly WindowWatch _windows;
    private readonly IconWatch _icons;
    private readonly string _www;
    private readonly CancellationTokenSource _cts = new();
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".woff2"] = "font/woff2",
    };

    public WebHost(Sampler sampler, AudioProbe audio, WindowWatch windows, IconWatch icons, int preferredPort)
    {
        _sampler = sampler;
        _audio = audio;
        _windows = windows;
        _icons = icons;
        _www = Path.Combine(AppContext.BaseDirectory, "www");
        Port = FindFreePort(preferredPort);
    }

    private static int FindFreePort(int start)
    {
        for (int p = start; p < start + 40; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch { }
        }
        return start;
    }

    public void Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        var thread = new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "haloflux-http" };
        thread.Start();
        Log.Write($"http listening on {Port}");
    }

    private void AcceptLoop(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) return;
                Log.Write("accept: " + ex.Message);
                Thread.Sleep(50);
            }
        }
    }

    private void Handle(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 4000;
                using var stream = client.GetStream();

                var head = ReadHead(stream);
                if (head == null) return;

                var firstLine = head.Split("\r\n")[0];
                var parts = firstLine.Split(' ');
                if (parts.Length < 2) return;
                var path = parts[1];
                var q = path.IndexOf('?');
                if (q >= 0) path = path[..q];
                path = Uri.UnescapeDataString(path);

                if (path == "/api/stats")
                {
                    var json = JsonSerializer.SerializeToUtf8Bytes(_sampler.Latest, _json);
                    WriteResponse(stream, 200, "application/json; charset=utf-8", json, "no-store");
                    return;
                }

                if (path == "/api/windows")
                {
                    var json = JsonSerializer.SerializeToUtf8Bytes(_windows.Snapshot(), _json);
                    WriteResponse(stream, 200, "application/json; charset=utf-8", json, "no-store");
                    return;
                }

                if (path == "/api/icons")
                {
                    var json = JsonSerializer.SerializeToUtf8Bytes(_icons.Snapshot(), _json);
                    WriteResponse(stream, 200, "application/json; charset=utf-8", json, "no-store");
                    return;
                }

                if (path == "/api/audio")
                {
                    var audioJson = JsonSerializer.SerializeToUtf8Bytes(_audio.Snapshot(), _json);
                    WriteResponse(stream, 200, "application/json; charset=utf-8", audioJson, "no-store");
                    return;
                }

                if (path == "/api/ping")
                {
                    WriteResponse(stream, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("haloflux-desk"), "no-store");
                    return;
                }

                if (path == "/" || path.EndsWith('/')) path += "index.html";

                var full = Path.GetFullPath(Path.Combine(_www, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(_www, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    WriteResponse(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"), "no-store");
                    return;
                }

                var body = File.ReadAllBytes(full);
                var ext = Path.GetExtension(full);
                var mime = Mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";
                WriteResponse(stream, 200, mime, body, "no-store");
            }
        }
        catch (Exception ex)
        {
            Log.Write("handle: " + ex.Message);
        }
    }

    private static string? ReadHead(NetworkStream stream)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        int headersEnd = -1;
        while (buffer.Count < 16384)
        {
            int read = stream.Read(one, 0, 1);
            if (read <= 0) break;
            buffer.Add(one[0]);
            int n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == 13 && buffer[n - 3] == 10 && buffer[n - 2] == 13 && buffer[n - 1] == 10)
            {
                headersEnd = n;
                break;
            }
        }
        if (headersEnd < 0) return null;
        return Encoding.ASCII.GetString(buffer.ToArray(), 0, headersEnd);
    }

    private static void WriteResponse(NetworkStream stream, int status, string contentType, byte[] body, string cache)
    {
        var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Error";
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: ").Append(cache).Append("\r\n")
            .Append("Access-Control-Allow-Origin: *\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        var head = Encoding.ASCII.GetBytes(header);
        stream.Write(head, 0, head.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
    }
}

public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _resolved;

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HalofluxDesk", "haloflux.log");
        yield return Path.Combine(Path.GetTempPath(), "HalofluxDesk", "haloflux.log");
        yield return Path.Combine(AppContext.BaseDirectory, "haloflux.log");
    }

    private static string? Resolve()
    {
        if (_resolved) return _path;
        _resolved = true;
        foreach (var candidate in Candidates())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
                File.AppendAllText(candidate, "");
                _path = candidate;
                return _path;
            }
            catch { }
        }
        return null;
    }

    public static string? CurrentPath => Resolve();

    public static string DataDir => Path.GetDirectoryName(Resolve() ?? Path.Combine(AppContext.BaseDirectory, "x"))!;

    public static void Crash(string where, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HalofluxDesk");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + where + "\n" + ex + "\n\n");
        }
        catch { }

        Write(where + ": " + ex.Message);
    }


    public static string IntegrityLevel()
    {
        try
        {
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x0008, out var token))
                return "unknown";

            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(128);
            try
            {
                if (!Native.GetTokenInformation(token, 25, buf, 128, out _))
                    return "unknown";

                var sid = System.Runtime.InteropServices.Marshal.ReadIntPtr(buf, 0);
                int count = System.Runtime.InteropServices.Marshal.ReadByte(sid, 1);
                int rid = System.Runtime.InteropServices.Marshal.ReadInt32(sid, 8 + 4 * (count - 1));

                return rid >= 0x4000 ? "System"
                    : rid >= 0x3000 ? "High"
                    : rid >= 0x2000 ? "Medium"
                    : "Low";
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
                Native.CloseHandle(token);
            }
        }
        catch
        {
            return "unknown";
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Resolve();
                if (path == null) return;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
