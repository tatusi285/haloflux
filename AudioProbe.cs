using System.Text.Json;
using NAudio.Wave;

namespace HalofluxDesk;

public sealed class AudioFrame
{
    public double Level { get; set; }
    public double[] Bands { get; set; } = Array.Empty<double>();
    public bool Live { get; set; }
}

/// <summary>Captures the system output (WASAPI loopback) and reduces it to 32 log-spaced bands.</summary>
public sealed class AudioProbe : IDisposable
{
    private const int FftSize = 2048;
    private const int BandCount = 32;

    private WasapiLoopbackCapture? _capture;
    private readonly object _gate = new();
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly float[] _hann = new float[FftSize];
    private readonly int[] _bandStart = new int[BandCount];
    private readonly int[] _bandEnd = new int[BandCount];
    private readonly double[] _bandMax = new double[BandCount];
    private readonly double[] _bands = new double[BandCount];
    private readonly double[] _bandsSmooth = new double[BandCount];
    private double _level;
    private double _levelSmooth;
    private bool _live;
    private long _lastFrameTicks;

    public AudioProbe()
    {
        for (int i = 0; i < FftSize; i++)
            _hann[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1))));
    }

    public void Start()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Write("audio stopped: " + e.Exception.Message);
            };
            _capture.StartRecording();
            SetupBands(_capture.WaveFormat.SampleRate);
            _live = true;
            Log.Write($"audio capture started: {_capture.WaveFormat.SampleRate} Hz, {_capture.WaveFormat.Channels} ch");
        }
        catch (Exception ex)
        {
            Log.Write("audio unavailable: " + ex.Message);
            _live = false;
        }
    }

    private void SetupBands(int sampleRate)
    {
        double binHz = (double)sampleRate / FftSize;
        double fMin = 40, fMax = Math.Min(16000, sampleRate / 2.0);
        for (int i = 0; i < BandCount; i++)
        {
            double f0 = fMin * Math.Pow(fMax / fMin, (double)i / BandCount);
            double f1 = fMin * Math.Pow(fMax / fMin, (double)(i + 1) / BandCount);
            _bandStart[i] = Math.Max(1, (int)(f0 / binHz));
            _bandEnd[i] = Math.Max(_bandStart[i] + 1, (int)(f1 / binHz));
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var format = _capture?.WaveFormat;
            if (format == null) return;

            int channels = format.Channels;
            int frames = e.BytesRecorded / (4 * channels);
            if (frames < FftSize) return;

            int offset = Math.Max(0, frames - FftSize);
            double sum = 0;

            for (int i = 0; i < FftSize; i++)
            {
                float v = 0;
                int baseIndex = (offset + i) * channels * 4;
                for (int c = 0; c < channels && c < 2; c++)
                {
                    int idx = baseIndex + c * 4;
                    if (idx + 4 <= e.BytesRecorded)
                        v += BitConverter.ToSingle(e.Buffer, idx);
                }
                v /= Math.Min(channels, 2);
                sum += v * v;
                _re[i] = v * _hann[i];
                _im[i] = 0;
            }

            double rms = Math.Sqrt(sum / FftSize);
            lock (_gate) _level = Math.Min(1, rms * 6.0);

            Fft(_re, _im);

            for (int b = 0; b < BandCount; b++)
            {
                double mag = 0;
                int s = _bandStart[b], en = Math.Min(_bandEnd[b], FftSize / 2);
                for (int k = s; k < en; k++)
                    mag += Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
                mag /= Math.Max(1, en - s);

                // adaptive ceiling so quiet passages still scale to the full range
                _bandMax[b] = Math.Max(mag, _bandMax[b] * 0.9995);
                double norm = _bandMax[b] > 1e-6 ? mag / _bandMax[b] : 0;
                lock (_gate) _bands[b] = Math.Clamp(norm, 0, 1);
            }

            _lastFrameTicks = Environment.TickCount64;
        }
        catch { }
    }

    public AudioFrame Snapshot()
    {
        var frame = new AudioFrame
        {
            Live = _live && Environment.TickCount64 - _lastFrameTicks < 3000,
            Bands = new double[BandCount],
        };
        lock (_gate)
        {
            _levelSmooth += (_level - _levelSmooth) * 0.35;
            for (int i = 0; i < BandCount; i++)
            {
                // fast attack, slow release
                double target = _bands[i];
                _bandsSmooth[i] += (target - _bandsSmooth[i]) * (target > _bandsSmooth[i] ? 0.55 : 0.12);
                frame.Bands[i] = Math.Round(_bandsSmooth[i], 3);
            }
            frame.Level = Math.Round(_levelSmooth, 3);
        }
        return frame;
    }

    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang);
            float wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cwr = 1, cwi = 0;
                int half = len >> 1;
                for (int j = 0; j < half; j++)
                {
                    int a = i + j;
                    int b = i + j + half;
                    float ur = re[a], ui = im[a];
                    float vr = re[b] * cwr - im[b] * cwi;
                    float vi = re[b] * cwi + im[b] * cwr;
                    re[a] = ur + vr;
                    im[a] = ui + vi;
                    re[b] = ur - vr;
                    im[b] = ui - vi;
                    float nwr = cwr * wr - cwi * wi;
                    cwi = cwr * wi + cwi * wr;
                    cwr = nwr;
                }
            }
        }
    }

    public void Dispose()
    {
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
    }
}
