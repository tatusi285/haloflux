using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HalofluxDesk;

public sealed class CoreSample
{
    public int Index { get; set; }
    public double Load { get; set; }
}

public sealed class CpuSample
{
    public double Total { get; set; }
    public List<double> Cores { get; set; } = new();
    public int CoreCount { get; set; }
    public string Name { get; set; } = "";
    public int Mhz { get; set; }
    public int Processes { get; set; }
    public int Threads { get; set; }
    public int Handles { get; set; }
}

public sealed class MemSample
{
    public double TotalMb { get; set; }
    public double UsedMb { get; set; }
    public double AvailMb { get; set; }
    public double Pct { get; set; }
    public double CommitMb { get; set; }
    public double CommitLimitMb { get; set; }
    public double CachedMb { get; set; }
}

public sealed class GpuSample
{
    public bool Present { get; set; }
    public string Name { get; set; } = "";
    public double Util { get; set; }
    public double MemUsedMb { get; set; }
    public double MemTotalMb { get; set; }
    public double TempC { get; set; }
    public double PowerW { get; set; }
    public double ClockMhz { get; set; }
}

public sealed class NetSample
{
    public double DownBps { get; set; }
    public double UpBps { get; set; }
    public double DownTotalMb { get; set; }
    public double UpTotalMb { get; set; }
    public string Iface { get; set; } = "";
}

public sealed class DiskSample
{
    public string Id { get; set; } = "";
    public double FreeGb { get; set; }
    public double TotalGb { get; set; }
    public double UsedPct { get; set; }
}

public sealed class ProcSample
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public double Cpu { get; set; }
    public double MemMb { get; set; }
}

public sealed class BatterySample
{
    public bool Present { get; set; }
    public double Pct { get; set; }
    public bool Charging { get; set; }
    public int MinutesLeft { get; set; }
}

public sealed class DiskIoSample
{
    public double ReadBps { get; set; }
    public double WriteBps { get; set; }
}

public sealed class Snapshot
{
    public long Time { get; set; }
    public string Host { get; set; } = "";
    public string User { get; set; } = "";
    public long UptimeSec { get; set; }
    public CpuSample Cpu { get; set; } = new();
    public MemSample Mem { get; set; } = new();
    public GpuSample Gpu { get; set; } = new();
    public NetSample Net { get; set; } = new();
    public List<DiskSample> Disks { get; set; } = new();
    public List<ProcSample> Top { get; set; } = new();
    public BatterySample Battery { get; set; } = new();
    public DiskIoSample Io { get; set; } = new();
    public int TodayMinutes { get; set; }
    public List<DayStat> History { get; set; } = new();
}

/// <summary>Polls real machine telemetry on a background thread and publishes an immutable snapshot.</summary>
public sealed class Sampler
{
    private readonly object _gate = new();
    private Snapshot _latest = new();
    private Thread? _thread;
    private volatile bool _stopping;

    private ProcessorPerf[]? _prevCores;
    private long _prevNetRecv = -1;
    private long _prevNetSent = -1;
    private Dictionary<int, TimeSpan> _prevProcTime = new();
    private DateTime _prevProcStamp = DateTime.UtcNow;
    private PerformanceCounter? _diskRead;
    private PerformanceCounter? _diskWrite;
    private GpuSample _gpu = new();
    private Task? _gpuTask;
    private string _cpuName = "";
    private int _cpuMhz;
    private List<ProcSample> _lastTop = new();
    private List<DiskSample> _lastDisks = new();
    private readonly History _history = new();

    public Snapshot Latest
    {
        get { lock (_gate) return _latest; }
    }

    public void Start()
    {
        if (_thread != null) return;
        Task.Run(InitPerfCounters);
        _thread = new Thread(Loop) { IsBackground = true, Name = "haloflux-sampler" };
        _thread.Start();
    }

    public void Stop() => _stopping = true;

    private struct ProcessorPerf
    {
        public long Idle, Kernel, User;
    }

    private void Loop()
    {
        _cpuName = ReadCpuName();
        _cpuMhz = ReadCpuMhz();

        // Warm up: a first publish with zeros looks broken, so prime every
        // counter baseline (and the GPU probe) before the wallpaper reads it.
        try
        {
            var warm = QueryGpu();
            if (warm != null) lock (_gate) _gpu = warm;
            SampleCpu(null!);
            var primed = SampleProcesses(Environment.ProcessorCount);
            _lastTop = primed;
            _lastDisks = SampleDisks();
            Thread.Sleep(600);
        }
        catch { }

        int tick = 0;
        while (!_stopping)
        {
            try
            {
                var snap = new Snapshot
                {
                    Time = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                    Host = "HALOFLUX",   // never publish the real machine name
                    User = Environment.UserName,
                    UptimeSec = Environment.TickCount64 / 1000,
                };

                snap.Cpu = SampleCpu(snap);
                snap.Mem = SampleMem();
                snap.Net = SampleNet();
                snap.Battery = SampleBattery();

                if (tick % 2 == 0)
                {
                    var procs = SampleProcesses(snap.Cpu.CoreCount);
                    if (procs.Count > 0) _lastTop = procs;
                }
                snap.Top = _lastTop;

                if (tick % 5 == 0)
                {
                    var disks = SampleDisks();
                    if (disks.Count > 0) _lastDisks = disks;
                }
                snap.Disks = _lastDisks;

                snap.Io = SampleIo();

                if (tick % 2 == 0) KickGpu();
                snap.Gpu = _gpu;

                _history.Add(snap.Cpu.Total, snap.Gpu?.Util ?? 0, 1.0);
                _history.NotePeak(snap.Cpu.Total);
                if (tick % 60 == 0) _history.Save();

                snap.TodayMinutes = _history.TodayMinutes;
                snap.History = _history.Last(7);

                lock (_gate) _latest = snap;
            }
            catch
            {
                // never let a sampling fault kill the wallpaper
            }

            tick++;
            Thread.Sleep(1000);
        }

        _history.Save(force: true);
    }

    private CpuSample SampleCpu(Snapshot snap)
    {
        var result = new CpuSample { Name = _cpuName, Mhz = _cpuMhz, CoreCount = Environment.ProcessorCount };
        var cores = Environment.ProcessorCount;
        int size = Marshal.SizeOf<Native.ProcessorPerfInfo>();
        IntPtr buf = Marshal.AllocHGlobal(size * cores);
        try
        {
            int status = Native.NtQuerySystemInformation(
                Native.SystemProcessorPerformanceInformation, buf, size * cores, out _);
            if (status != 0)
            {
                result.Total = 0;
                return result;
            }

            var cur = new ProcessorPerf[cores];
            for (int i = 0; i < cores; i++)
            {
                var info = Marshal.PtrToStructure<Native.ProcessorPerfInfo>(buf + i * size);
                cur[i] = new ProcessorPerf { Idle = info.IdleTime, Kernel = info.KernelTime, User = info.UserTime };
            }

            if (_prevCores == null || _prevCores.Length != cores)
            {
                _prevCores = cur;
                for (int i = 0; i < cores; i++) result.Cores.Add(0);
                return result;
            }

            double sumBusy = 0, sumTotal = 0;
            for (int i = 0; i < cores; i++)
            {
                long dIdle = cur[i].Idle - _prevCores[i].Idle;
                long dKernel = cur[i].Kernel - _prevCores[i].Kernel;
                long dUser = cur[i].User - _prevCores[i].User;
                long dTotal = dKernel + dUser; // kernel already includes idle
                double load = dTotal > 0 ? Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100) : 0;
                result.Cores.Add(Math.Round(load, 1));
                sumBusy += dTotal - dIdle;
                sumTotal += dTotal;
            }

            result.Total = sumTotal > 0 ? Math.Round(sumBusy * 100.0 / sumTotal, 1) : 0;
            _prevCores = cur;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        try
        {
            var info = new Native.PerformanceInformation { cb = (uint)Marshal.SizeOf<Native.PerformanceInformation>() };
            if (Native.GetPerformanceInfo(out info, info.cb))
            {
                result.Processes = (int)info.ProcessCount;
                result.Threads = (int)info.ThreadCount;
                result.Handles = (int)info.HandleCount;
            }
            else
            {
                Log.Write("GetPerformanceInfo failed: " + Marshal.GetLastWin32Error());
            }
        }
        catch { }

        return result;
    }

    private static MemSample SampleMem()
    {
        var ms = new Native.MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
        var sample = new MemSample();
        if (Native.GlobalMemoryStatusEx(ref ms))
        {
            sample.TotalMb = Math.Round(ms.ullTotalPhys / 1048576.0, 0);
            sample.AvailMb = Math.Round(ms.ullAvailPhys / 1048576.0, 0);
            sample.UsedMb = Math.Round((ms.ullTotalPhys - ms.ullAvailPhys) / 1048576.0, 0);
            sample.Pct = Math.Round((double)ms.dwMemoryLoad, 1);
        }

        try
        {
            var pi = new Native.PerformanceInformation { cb = (uint)Marshal.SizeOf<Native.PerformanceInformation>() };
            if (Native.GetPerformanceInfo(out pi, pi.cb))
            {
                double page = pi.PageSize == 0 ? 4096 : (double)pi.PageSize;
                sample.CommitMb = Math.Round((double)pi.CommitTotal * page / 1048576.0, 0);
                sample.CommitLimitMb = Math.Round((double)pi.CommitLimit * page / 1048576.0, 0);
                sample.CachedMb = Math.Round((double)pi.SystemCache * page / 1048576.0, 0);
            }
        }
        catch { }

        return sample;
    }

    private NetSample SampleNet()
    {
        var sample = new NetSample();
        try
        {
            long recv = 0, sent = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    var st = ni.GetIPStatistics();
                    recv += st.BytesReceived;
                    sent += st.BytesSent;
                }
                catch { }
            }
            if (_prevNetRecv >= 0)
            {
                sample.DownBps = Math.Max(0, recv - _prevNetRecv);
                sample.UpBps = Math.Max(0, sent - _prevNetSent);
            }
            _prevNetRecv = recv;
            _prevNetSent = sent;
            sample.DownTotalMb = Math.Round(recv / 1048576.0, 1);
            sample.UpTotalMb = Math.Round(sent / 1048576.0, 1);
            sample.Iface = ActiveInterfaceName();
        }
        catch { }
        return sample;
    }

    private static string ActiveInterfaceName()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => !n.Name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                            && !n.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.Speed)
                .FirstOrDefault();
            return up?.Name ?? "OFFLINE";
        }
        catch { return "OFFLINE"; }
    }

    private static BatterySample SampleBattery()
    {
        var sample = new BatterySample();
        try
        {
            if (Native.GetSystemPowerStatus(out var ps))
            {
                bool noBattery = (ps.BatteryFlag & 128) != 0;
                sample.Present = !noBattery;
                sample.Pct = ps.BatteryLifePercent == 255 ? 100 : ps.BatteryLifePercent;
                sample.Charging = ps.BatteryFlag == 8 || ps.ACLineStatus == 1;
                sample.MinutesLeft = ps.BatteryLifeTime >= 0 ? ps.BatteryLifeTime / 60 : -1;
            }
        }
        catch { }
        return sample;
    }

    private static List<DiskSample> SampleDisks()
    {
        var list = new List<DiskSample>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    double total = d.TotalSize / 1073741824.0;
                    if (total < 1) continue;
                    double free = d.AvailableFreeSpace / 1073741824.0;
                    list.Add(new DiskSample
                    {
                        Id = d.Name.TrimEnd('\\'),
                        FreeGb = Math.Round(free, 1),
                        TotalGb = Math.Round(total, 1),
                        UsedPct = Math.Round((1 - free / total) * 100, 1),
                    });
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    private DiskIoSample SampleIo()
    {
        var sample = new DiskIoSample();
        try
        {
            if (_diskRead != null) sample.ReadBps = Math.Max(0, _diskRead.NextValue());
            if (_diskWrite != null) sample.WriteBps = Math.Max(0, _diskWrite.NextValue());
        }
        catch
        {
            _diskRead = null;
            _diskWrite = null;
        }
        return sample;
    }

    /// <summary>Creating performance counters can take seconds on a cold start, so never do it on the sampling thread.</summary>
    private void InitPerfCounters()
    {
        try
        {
            var r = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
            var w = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
            r.NextValue();
            w.NextValue();
            _diskRead = r;
            _diskWrite = w;
            Log.Write("perf counters ready");
        }
        catch (Exception ex)
        {
            Log.Write("perf counters unavailable: " + ex.Message);
        }
    }

    private List<ProcSample> SampleProcesses(int cores)
    {
        var list = new List<ProcSample>();
        var now = DateTime.UtcNow;
        double elapsed = Math.Max(0.05, (now - _prevProcStamp).TotalSeconds);
        var next = new Dictionary<int, TimeSpan>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    TimeSpan cpuTime = p.TotalProcessorTime;
                    next[p.Id] = cpuTime;
                    double cpu = 0;
                    if (_prevProcTime.TryGetValue(p.Id, out var prev))
                    {
                        cpu = (cpuTime - prev).TotalSeconds / (elapsed * Math.Max(1, cores)) * 100.0;
                    }
                    list.Add(new ProcSample
                    {
                        Name = SafeName(p),
                        Pid = p.Id,
                        Cpu = Math.Round(Math.Clamp(cpu, 0, 100 * Math.Max(1, cores)), 1),
                        MemMb = Math.Round(p.WorkingSet64 / 1048576.0, 0),
                    });
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        _prevProcTime = next;
        _prevProcStamp = now;

        return list.Where(x => x.Cpu > 0.05 || x.MemMb > 0)
            .OrderByDescending(x => x.Cpu)
            .ThenByDescending(x => x.MemMb)
            .Take(14)
            .ToList();
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return "?"; }
    }

    private void KickGpu()
    {
        if (_gpuTask is { IsCompleted: false }) return;
        _gpuTask = Task.Run(() =>
        {
            var gpu = QueryGpu();
            if (gpu != null) lock (_gate) _gpu = gpu;
        });
    }

    private static GpuSample? QueryGpu()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi")
            {
                Arguments = "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,clocks.sm --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            string text = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(2500))
            {
                try { proc.Kill(true); } catch { }
                return null;
            }
            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (line == null) return null;
            var parts = line.Split(',').Select(x => x.Trim()).ToArray();
            if (parts.Length < 4) return null;

            static double Num(string s)
            {
                s = s.Replace("[N/A]", "").Replace("N/A", "");
                return double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            }

            return new GpuSample
            {
                Present = true,
                Name = parts[0],
                Util = Num(parts.Length > 1 ? parts[1] : ""),
                MemUsedMb = Num(parts.Length > 2 ? parts[2] : ""),
                MemTotalMb = Num(parts.Length > 3 ? parts[3] : ""),
                TempC = Num(parts.Length > 4 ? parts[4] : ""),
                PowerW = Num(parts.Length > 5 ? parts[5] : ""),
                ClockMhz = Num(parts.Length > 6 ? parts[6] : ""),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "";
        }
        catch { return ""; }
    }

    private static int ReadCpuMhz()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("~MHz") is int v ? v : 0;
        }
        catch { return 0; }
    }
}

