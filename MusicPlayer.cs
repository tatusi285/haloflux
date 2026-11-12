using NAudio.Wave;

namespace HalofluxDesk;

/// <summary>
/// Plays every track in www/media/music in order and loops the folder.
/// Drop mp3 / m4a / wav / aac files in there and they are picked up on the next start.
/// </summary>
internal enum PlayMode { Sequential, LoopOne, Shuffle }

internal sealed class MusicPlayer : IDisposable
{
    private static readonly string[] Extensions = { "*.mp3", "*.m4a", "*.m4a", "*.aac", "*.wav" };

    private readonly string[] _tracks;
    private int _index = -1;

    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private bool _stopping;

    public MusicPlayer(float volume = 0.45f)
    {
        Volume = LoadVolume(volume);

        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "www", "media", "music");
            Directory.CreateDirectory(dir);

            var found = new List<string>();
            foreach (var pattern in new[] { "*.mp3", "*.m4a", "*.aac", "*.wav" })
                found.AddRange(Directory.GetFiles(dir, pattern));

            _tracks = found.Distinct(StringComparer.OrdinalIgnoreCase)
                           .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                           .ToArray();

            Log.Write("music: " + _tracks.Length + " track(s) in " + dir);
        }
        catch (Exception ex)
        {
            Log.Write("music: " + ex.Message);
            _tracks = Array.Empty<string>();
        }
    }

    public float Volume { get; set; }

    public PlayMode Mode { get; private set; } = PlayMode.Sequential;

    public string ModeName => Mode switch
    {
        PlayMode.LoopOne => "single loop",
        PlayMode.Shuffle => "shuffle",
        _ => "in order",
    };

    public void CycleMode()
    {
        Mode = Mode switch
        {
            PlayMode.Sequential => PlayMode.LoopOne,
            PlayMode.LoopOne => PlayMode.Shuffle,
            _ => PlayMode.Sequential,
        };
        Log.Write("music: mode " + ModeName);
    }

    public int Count => _tracks.Length;

    public int Index => _index;

    public bool Playing => _output != null && _output.PlaybackState == PlaybackState.Playing;

    public string Current
    {
        get
        {
            if (_tracks.Length == 0 || _index < 0) return "NO TRACKS";
            return Path.GetFileNameWithoutExtension(_tracks[_index]);
        }
    }

    /// <summary>Steps the volume and remembers it for the next run.</summary>
    public void NudgeVolume(float delta)
    {
        Volume = Math.Clamp(Volume + delta, 0f, 1f);

        try { if (_output != null) _output.Volume = Volume; } catch { }

        try { File.WriteAllText(VolumeFile, ((int)Math.Round(Volume * 100)).ToString()); } catch { }

        Log.Write("music: volume " + (int)Math.Round(Volume * 100) + "%");
    }

    private static string VolumeFile => Path.Combine(AppContext.BaseDirectory, "volume.txt");

    private static float LoadVolume(float fallback)
    {
        try
        {
            if (!File.Exists(VolumeFile)) return fallback;
            if (int.TryParse(File.ReadAllText(VolumeFile).Trim(), out var pct))
                return Math.Clamp(pct / 100f, 0f, 1f);
        }
        catch { }

        return fallback;
    }

    public void Start()
    {
        if (_tracks.Length == 0) return;
        Play(0);
    }

    /// <summary>Pause or resume without losing the position.</summary>
    public void Toggle()
    {
        try
        {
            if (_output == null) { if (_tracks.Length > 0) Play(Math.Max(0, _index)); return; }
            if (Playing) _output.Pause(); else _output.Play();
        }
        catch (Exception ex) { Log.Write("music toggle: " + ex.Message); }
    }

    public void Next()
    {
        if (_tracks.Length == 0) return;
        Play(Mode == PlayMode.Shuffle ? RandomIndex() : Wrap(_index + 1));
    }

    public void Previous()
    {
        if (_tracks.Length == 0) return;
        Play(Mode == PlayMode.Shuffle ? RandomIndex() : Wrap(_index - 1));
    }

    private int Wrap(int i)
    {
        i %= _tracks.Length;
        return i < 0 ? i + _tracks.Length : i;
    }

    private int RandomIndex()
    {
        if (_tracks.Length == 1) return 0;

        var rnd = Random.Shared;
        int i = _index;
        while (i == _index) i = rnd.Next(_tracks.Length);
        return i;
    }

    private void Play(int index)
    {
        try
        {
            Stop();

            _index = ((index % _tracks.Length) + _tracks.Length) % _tracks.Length;

            // MediaFoundation reads mp3, m4a/aac and wav, which covers everything users drop in
            _reader = new MediaFoundationReader(_tracks[_index]);
            _output = new WaveOutEvent { DesiredLatency = 200, Volume = Volume };
            _output.Init(_reader);
            _output.PlaybackStopped += OnStopped;
            _output.Play();

            Log.Write("music: playing " + Current + " (" + (_index + 1) + "/" + _tracks.Length + ")");
        }
        catch (Exception ex)
        {
            Log.Write("music play: " + ex.Message);
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping) return;

        try
        {
            int next = Mode switch
            {
                PlayMode.LoopOne => _index,
                PlayMode.Shuffle => RandomIndex(),
                _ => Wrap(_index + 1),
            };

            Play(next);
        }
        catch { }
    }

    public void Stop()
    {
        try
        {
            _stopping = true;

            if (_output != null)
            {
                _output.PlaybackStopped -= OnStopped;
                _output.Stop();
                _output.Dispose();
                _output = null;
            }

            _reader?.Dispose();
            _reader = null;
        }
        catch { }
        finally
        {
            _stopping = false;
        }
    }

    public void Dispose() => Stop();
}
