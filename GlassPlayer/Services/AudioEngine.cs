using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GlassPlayer.Services;

/// <summary>
/// Music playback through NAudio: decks (one per song) mixed together so songs can crossfade,
/// then a 10-band equalizer and master volume, out through WASAPI.
/// Methods are called from the UI thread; Read() runs on the audio thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public static readonly double[] BandFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private readonly MixingSampleProvider _mixer;
    private readonly Equalizer _eq;
    private readonly VolumeSampleProvider _master;
    private WasapiOut? _out;
    private Deck? _current;
    private readonly List<Deck> _fadingOut = new();

    /// <summary>Raised on the audio thread when the current song reaches its end.</summary>
    public event Action? Ended;

    public AudioEngine()
    {
        _mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
        _eq = new Equalizer(_mixer);
        _master = new VolumeSampleProvider(_eq);
    }

    public bool HasTrack => _current != null;
    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Position => _current?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _current?.Duration ?? TimeSpan.Zero;
    public TimeSpan Remaining => _current == null ? TimeSpan.Zero : _current.Duration - _current.Position;

    private float _volume = 0.8f;
    private bool _muted;
    public double Volume { get => _volume; set { _volume = (float)Math.Clamp(value, 0, 1); ApplyVolume(); } }
    public bool Muted { get => _muted; set { _muted = value; ApplyVolume(); } }
    private void ApplyVolume() => _master.Volume = _muted ? 0f : _volume;

    public void SetEqualizer(bool enabled, double[] gainsDb) => _eq.Update(enabled, gainsDb);

    private void EnsureOutput()
    {
        if (_out != null) return;
        _out = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 120);
        _out.Init(_master);
    }

    /// <summary>Open a song (slow on a NAS: call from a background thread is fine).</summary>
    public static Deck Open(string path) => new(path);

    /// <summary>Switch to this deck immediately (or crossfade over fadeSeconds).</summary>
    public void Start(Deck deck, double fadeSeconds = 0)
    {
        EnsureOutput();
        var old = _current;
        _current = deck;
        deck.Ended += OnDeckEnded;

        if (old != null)
        {
            old.Ended -= OnDeckEnded;
            if (fadeSeconds > 0)
            {
                old.FadeTo(0, fadeSeconds, removeWhenSilent: true);
                lock (_fadingOut) _fadingOut.Add(old);
                // runs on the audio thread inside the mixer's Read: the mixer drops the input itself
                // (it returned a short read), so only release it here - never touch the mixer's list
                old.Silenced += () => { lock (_fadingOut) _fadingOut.Remove(old); old.Dispose(); };
            }
            else
            {
                _mixer.RemoveMixerInput(old);
                old.Dispose();
            }
        }

        if (fadeSeconds > 0)
        {
            deck.SetGain(0);
            deck.FadeTo(1, fadeSeconds);
        }
        _mixer.AddMixerInput(deck);
        _out!.Play();
    }

    private void OnDeckEnded() => Ended?.Invoke();

    public void Play() { if (_current != null) { EnsureOutput(); _out!.Play(); } }
    public void Pause() => _out?.Pause();

    public void Seek(TimeSpan position) => _current?.Seek(position);

    /// <summary>Stop music completely (e.g. when a video starts).</summary>
    public void Stop()
    {
        _mixer.RemoveAllMixerInputs();
        _current?.Dispose();
        _current = null;
        lock (_fadingOut)
        {
            foreach (var d in _fadingOut) d.Dispose();
            _fadingOut.Clear();
        }
        _out?.Pause();
    }

    public bool IsCurrentPath(string path) => _current?.Path.Equals(path, StringComparison.OrdinalIgnoreCase) == true;

    public void Dispose()
    {
        Stop();
        _out?.Dispose();
        _out = null;
    }

    // ------------------------------------------------------------------

    /// <summary>One decoded song, converted to the mix format, with its own fade gain.</summary>
    public sealed class Deck : ISampleProvider, IDisposable
    {
        private readonly MediaFoundationReader _reader;
        private readonly ISampleProvider _chain;
        private readonly object _lock = new();
        private float _gain = 1f, _target = 1f, _step;
        private bool _removeWhenSilent, _ended, _disposed;

        public string Path { get; }
        public WaveFormat WaveFormat => MixFormat;
        public event Action? Ended;
        public event Action? Silenced;

        public Deck(string path)
        {
            Path = path;
            _reader = new MediaFoundationReader(path);
            ISampleProvider sp = _reader.ToSampleProvider();
            if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
            else if (sp.WaveFormat.Channels > 2) sp = new MultiplexingSampleProvider(new[] { sp }, 2) ; // keep front L/R
            if (sp.WaveFormat.SampleRate != MixFormat.SampleRate) sp = new WdlResamplingSampleProvider(sp, MixFormat.SampleRate);
            _chain = sp;
        }

        public TimeSpan Duration => _reader.TotalTime;
        public TimeSpan Position { get { lock (_lock) return _reader.CurrentTime; } }

        public void Seek(TimeSpan t)
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                if (t > _reader.TotalTime) t = _reader.TotalTime;
                _reader.CurrentTime = t;
                _ended = false;
            }
        }

        public void SetGain(float g) { _gain = g; _target = g; _step = 0; }

        public void FadeTo(float target, double seconds, bool removeWhenSilent = false)
        {
            _target = target;
            _removeWhenSilent = removeWhenSilent;
            var frames = Math.Max(1, seconds * MixFormat.SampleRate);
            _step = (float)((target - _gain) / frames);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read;
            lock (_lock)
            {
                if (_disposed) return 0;
                try { read = _chain.Read(buffer, offset, count); }
                catch { read = 0; } // network hiccup / decode error: treat as end
            }

            // per-frame gain ramp
            for (int i = 0; i < read; i += 2)
            {
                if (_step != 0)
                {
                    _gain += _step;
                    if ((_step > 0 && _gain >= _target) || (_step < 0 && _gain <= _target)) { _gain = _target; _step = 0; }
                }
                buffer[offset + i] *= _gain;
                if (i + 1 < read) buffer[offset + i + 1] *= _gain;
            }

            if (_removeWhenSilent && _step == 0 && _gain <= 0.0001f)
            {
                _removeWhenSilent = false;
                Silenced?.Invoke();
                return 0;
            }
            // the mixer drops an input as soon as it returns a short read, so that's the end of the song
            if (read < count)
            {
                if (!_ended) { _ended = true; Ended?.Invoke(); }
                if (_removeWhenSilent) { _removeWhenSilent = false; Silenced?.Invoke(); }
            }
            return read;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _reader.Dispose();
            }
        }
    }

    /// <summary>10 peaking filters per channel. Gains are swapped atomically from the UI thread.</summary>
    private sealed class Equalizer : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private BiQuadFilter[,]? _filters; // [channel, band]; null = bypass
        private float _preamp = 1f;

        public Equalizer(ISampleProvider source) => _source = source;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public void Update(bool enabled, double[] gainsDb)
        {
            if (!enabled || gainsDb.All(g => Math.Abs(g) < 0.05)) { _filters = null; _preamp = 1f; return; }
            var rate = WaveFormat.SampleRate;
            var f = new BiQuadFilter[2, BandFrequencies.Length];
            for (int ch = 0; ch < 2; ch++)
                for (int b = 0; b < BandFrequencies.Length; b++)
                {
                    var freq = Math.Min(BandFrequencies[b], rate / 2.0 - 1000);
                    var gain = b < gainsDb.Length ? gainsDb[b] : 0;
                    f[ch, b] = BiQuadFilter.PeakingEQ(rate, (float)freq, 1.0f, (float)gain);
                }
            // pull the level down by part of the biggest boost to avoid clipping
            var maxBoost = Math.Max(0, gainsDb.DefaultIfEmpty(0).Max());
            _preamp = (float)Math.Pow(10, -maxBoost * 0.6 / 20);
            _filters = f;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            var f = _filters;
            if (f == null) return read;
            var pre = _preamp;
            int bands = f.GetLength(1);
            for (int i = 0; i < read; i++)
            {
                int ch = i % 2;
                float s = buffer[offset + i] * pre;
                for (int b = 0; b < bands; b++) s = f[ch, b].Transform(s);
                buffer[offset + i] = s;
            }
            return read;
        }
    }
}
