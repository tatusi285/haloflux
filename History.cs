using System.Text.Json;

namespace HalofluxDesk;

public sealed class DayStat
{
    public string D { get; set; } = "";
    public double CpuAvg { get; set; }
    public double CpuMax { get; set; }
    public double GpuAvg { get; set; }
    public int Minutes { get; set; }

    // raw accumulators, persisted so a restart never re-derives them from a rounded average
    public double SumCpu { get; set; }
    public double SumGpu { get; set; }
    public int N { get; set; }
    public double Sec { get; set; }
}

/// <summary>Rolling 7-day load history, persisted next to the log file.</summary>
public sealed class History
{
    private readonly List<DayStat> _days = new();
    private readonly string _path;
    private bool _dirty;

    public History()
    {
        _path = Path.Combine(Log.DataDir, "history.json");
        Load();
    }

    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    private DayStat ForToday()
    {
        var key = Today();
        var day = _days.FirstOrDefault(d => d.D == key);
        if (day == null)
        {
            day = new DayStat { D = key };
            _days.Add(day);
        }
        return day;
    }

    public int TodayMinutes => _days.FirstOrDefault(d => d.D == Today())?.Minutes ?? 0;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<DayStat>>(File.ReadAllText(_path));
            if (list != null) _days.AddRange(list);
        }
        catch (Exception ex)
        {
            Log.Write("history load: " + ex.Message);
        }
    }

    public void Add(double cpu, double gpu, double seconds)
    {
        try
        {
            var day = ForToday();
            day.SumCpu += Math.Clamp(cpu, 0, 100);
            day.SumGpu += Math.Clamp(gpu, 0, 100);
            day.N++;
            day.Sec += seconds;
            day.CpuAvg = Math.Round(day.SumCpu / Math.Max(1, day.N), 1);
            day.GpuAvg = Math.Round(day.SumGpu / Math.Max(1, day.N), 1);
            day.Minutes = (int)Math.Round(day.Sec / 60.0);
            _dirty = true;
        }
        catch { }
    }

    public void NotePeak(double cpu)
    {
        try
        {
            var day = ForToday();
            var v = Math.Clamp(cpu, 0, 100);
            if (v > day.CpuMax) { day.CpuMax = Math.Round(v, 1); _dirty = true; }
        }
        catch { }
    }

    public void Save(bool force = false)
    {
        try
        {
            if (!_dirty && !force) return;
            while (_days.Count > 14) _days.RemoveAt(0);
            Directory.CreateDirectory(Log.DataDir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_days));
            _dirty = false;
        }
        catch (Exception ex)
        {
            Log.Write("history save: " + ex.Message);
        }
    }

    public List<DayStat> Last(int days)
    {
        var result = new List<DayStat>();
        try
        {
            var today = DateTime.Today;
            for (int i = days - 1; i >= 0; i--)
            {
                var key = today.AddDays(-i).ToString("yyyy-MM-dd");
                var hit = _days.FirstOrDefault(d => d.D == key);
                result.Add(hit ?? new DayStat { D = key });
            }
        }
        catch { }
        return result;
    }
}
