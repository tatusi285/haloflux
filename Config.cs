using System.Drawing;
using System.Text.Json;

namespace HalofluxDesk;

/// <summary>
/// Small JSON file next to the exe. Everything visual that is worth tuning without a
/// rebuild lives here; missing keys fall back to the defaults below.
/// </summary>
internal sealed class Config
{
    public bool CrossFade { get; set; } = true;

    /// <summary>Presentation rate while cross fading. 30 is smooth, 20 halves the cost.</summary>
    public int WallFps { get; set; } = 30;
    public float Volume { get; set; } = 0.45f;
    public bool ShowDiagnostics { get; set; }
    public int Vignette { get; set; } = 60;

    private static readonly string Path_ =
        System.IO.Path.Combine(AppContext.BaseDirectory, "haloflux.json");

    private static Config? _current;

    public static Config Current
    {
        get
        {
            if (_current != null) return _current;

            try
            {
                if (File.Exists(Path_))
                {
                    var json = File.ReadAllText(Path_);
                    _current = JsonSerializer.Deserialize<Config>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Config();
                }
            }
            catch (Exception ex)
            {
                Log.Write("config: " + ex.Message);
            }

            _current ??= new Config();

            try
            {
                if (!File.Exists(Path_))
                {
                    File.WriteAllText(Path_, JsonSerializer.Serialize(_current,
                        new JsonSerializerOptions { WriteIndented = true }));
                    Log.Write("config: defaults written to " + Path_);
                }
            }
            catch { }

            return _current;
        }
    }

    public static void Reload()
    {
        _current = null;
        var c = Current;
        Log.Write("config: crossFade=" + c.CrossFade + " volume=" + c.Volume + " diagnostics=" + c.ShowDiagnostics);
    }
}
