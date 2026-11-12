using System.Diagnostics;
using System.Text;

namespace HalofluxDesk;

/// <summary>
/// Headless measurement of the wallpaper render path: fps, per-frame cost distribution,
/// CPU and memory. Used to compare a change against the previous baseline.
/// </summary>
internal static class Bench
{
    public static void Run(int seconds)
    {
        if (seconds <= 0) seconds = 30;

        Log.Write($"bench: starting {seconds}s run");

        var sampler = new Sampler();
        sampler.Start();

        var audio = new AudioProbe();
        var renderer = new NativeWall(sampler, audio, null, minimal: true);

        // let the decoder fill its pool and the first frame settle
        Thread.Sleep(2500);

        var frame = new List<double>(2048);
        var clock = Stopwatch.StartNew();
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();

        int lastId = int.MinValue;
        int rendered = 0;
        double peakMb = 0;

        while (clock.Elapsed.TotalSeconds < seconds)
        {
            int id = renderer.FrameId;
            if (id != lastId)
            {
                lastId = id;

                var t0 = Stopwatch.GetTimestamp();
                try { renderer.RenderFrame(); } catch { }
                var t1 = Stopwatch.GetTimestamp();

                frame.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                rendered++;

                if ((rendered & 0x1F) == 0)
                {
                    proc.Refresh();
                    peakMb = Math.Max(peakMb, proc.WorkingSet64 / 1048576.0);
                }
            }

            Thread.Sleep(4);
        }

        var wall = clock.Elapsed.TotalSeconds;
        proc.Refresh();
        var cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds;

        frame.Sort();
        double mean = frame.Count > 0 ? frame.Average() : 0;
        double P(double q) => frame.Count == 0 ? 0 : frame[Math.Clamp((int)(frame.Count * q), 0, frame.Count - 1)];

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("================ Haloflux benchmark ================");
        sb.AppendLine($"duration      {wall,8:0.0} s");
        sb.AppendLine($"frames        {rendered,8}    ({rendered / wall:0.0} fps)");
        sb.AppendLine($"render mean   {mean,8:0.00} ms");
        sb.AppendLine($"render p50    {P(0.50),8:0.00} ms");
        sb.AppendLine($"render p95    {P(0.95),8:0.00} ms");
        sb.AppendLine($"render max    {P(1.00),8:0.00} ms");
        sb.AppendLine($"cpu           {cpu / wall * 100,8:0.0} % of one core");
        sb.AppendLine($"memory peak   {peakMb,8:0} MB");
        sb.AppendLine($"config        crossFade={Config.Current.CrossFade} wallFps={Config.Current.WallFps} motion={Config.Current.MotionThreshold}");
        sb.AppendLine("====================================================");

        Console.WriteLine(sb.ToString());
        Log.Write("bench: " + sb.ToString().Replace("\r", " ").Replace("\n", " | "));

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory,
                $"bench-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, sb.ToString());
            Console.WriteLine("written: " + path);
        }
        catch { }

        audio.Dispose();
        sampler.Stop();
    }
}
