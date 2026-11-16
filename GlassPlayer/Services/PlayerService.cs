using GlassPlayer.Models;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace GlassPlayer.Services;

public enum RepeatMode { Off, All, One }

/// <summary>App-wide state: library views, the play queue and the media player. UI thread only.</summary>
public sealed class PlayerService
{
    public static PlayerService Instance { get; } = new();

    public LibraryService Library { get; } = new(video: false);
    public LibraryService VideoLibrary { get; } = new(video: true);
    public List<VideoInfo> Videos { get; private set; } = new();
    public List<string> OfflineFolders { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();
    /// <summary>Windows media player: used for video, and for the system media controls.</summary>
    public MediaPlayer Player { get; } = new();
    /// <summary>NAudio engine: music, with equalizer and crossfade.</summary>
    public AudioEngine Audio { get; } = new();
    private SystemMediaTransportControls? _smtc;
    private DispatcherQueueTimer? _tick;
    private bool _crossfading;

    public List<Track> Queue { get; private set; } = new();
    private List<Track> _unshuffled = new();
    public int Index { get; private set; } = -1;
    public Track? Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;
    public bool Shuffle { get; private set; }
    public RepeatMode Repeat { get; private set; }

    public List<AlbumInfo> Albums { get; private set; } = new();
    public List<ArtistInfo> Artists { get; private set; } = new();
    private Dictionary<string, AlbumInfo> _albumByKey = new();

    public bool IsScanning { get; private set; }
    public string LastError { get; private set; } = "";

    private bool CurrentIsVideo => Current?.IsVideo == true;
    public bool IsPlaying => CurrentIsVideo ? Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing : Audio.IsPlaying;
    public bool HasMedia => CurrentIsVideo ? Player.Source != null : Audio.HasTrack;
    public TimeSpan Position => CurrentIsVideo ? Player.PlaybackSession.Position : Audio.Position;
    public TimeSpan Duration => CurrentIsVideo ? Player.PlaybackSession.NaturalDuration : Audio.Duration;
    public bool IsMuted => Settings.Playback.Muted;

    public event Action? TrackChanged;
    public event Action? PlaybackStateChanged;
    public event Action? LibraryChanged;
    public event Action? QueueChanged;
    public event Action? VideosChanged;
    public event Action<string>? ScanStatus;

    private DispatcherQueue _ui = null!;
    private int _artGeneration;
    private int _thumbGeneration;
    private TimeSpan? _pendingSeek;
    private readonly Random _rng = new();

    public void Init(DispatcherQueue ui)
    {
        _ui = ui;
        Settings = AppSettings.Load();
        Library.LoadCache();
        VideoLibrary.LoadCache();
        RebuildViews();
        RebuildVideos();

        Player.AutoPlay = false;
        Player.Volume = Settings.Volume;
        Player.IsMuted = Settings.Playback.Muted;
        Audio.Volume = Settings.Volume;
        Audio.Muted = Settings.Playback.Muted;
        ApplyEqualizer();

        Audio.Ended += () => _ui.TryEnqueue(() => { if (!_crossfading && !CurrentIsVideo) OnEnded(); });
        Player.MediaEnded += (_, _) => _ui.TryEnqueue(() => { if (CurrentIsVideo) OnEnded(); });
        Player.MediaOpened += (_, _) =>
        {
            // resume videos where they were left
            if (_pendingSeek is TimeSpan seek) Player.PlaybackSession.Position = seek;
            _pendingSeek = null;
        };
        Player.MediaFailed += (_, e) => _ui.TryEnqueue(() =>
        {
            LastError = e.ErrorMessage;
            NotifyState();
        });
        Player.PlaybackSession.PlaybackStateChanged += (_, _) => _ui.TryEnqueue(NotifyState);

        // media keys + the Windows media flyout, driven by us for both music and video
        try
        {
            Player.CommandManager.IsEnabled = false;
            _smtc = Player.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;
            _smtc.ButtonPressed += (_, e) => _ui.TryEnqueue(() =>
            {
                switch (e.Button)
                {
                    case SystemMediaTransportControlsButton.Play: if (!IsPlaying) TogglePlay(); break;
                    case SystemMediaTransportControlsButton.Pause: if (IsPlaying) TogglePlay(); break;
                    case SystemMediaTransportControlsButton.Next: Next(); break;
                    case SystemMediaTransportControlsButton.Previous: Previous(); break;
                }
            });
        }
        catch
        {
            _smtc = null;
        }

        _tick = ui.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(250);
        _tick.Tick += (_, _) => CheckCrossfade();
        _tick.Start();
    }

    public void Shutdown()
    {
        _tick?.Stop();
        Audio.Dispose();
        Player.Dispose();
    }

    private void NotifyState()
    {
        PlaybackStateChanged?.Invoke();
        if (_smtc != null)
            _smtc.PlaybackStatus = IsPlaying ? MediaPlaybackStatus.Playing
                : HasMedia ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Stopped;
    }

    public void ApplyEqualizer() => Audio.SetEqualizer(Settings.Playback.EqEnabled, Settings.Playback.EqGains);

    public void ToggleMute() => SetMuted(!Settings.Playback.Muted);

    public void SetMuted(bool muted)
    {
        Settings.Playback.Muted = muted;
        Audio.Muted = muted;
        Player.IsMuted = muted;
        Settings.Save();
        PlaybackStateChanged?.Invoke();
    }

    private void CheckCrossfade()
    {
        var p = Settings.Playback;
        if (!p.CrossfadeEnabled || _crossfading || Current == null || CurrentIsVideo || !Audio.IsPlaying) return;
        if (Repeat == RepeatMode.One) return;
        int next = Index + 1 < Queue.Count ? Index + 1 : (Repeat == RepeatMode.All ? 0 : -1);
        if (next < 0 || Queue[next].IsVideo) return;
        var dur = Audio.Duration.TotalSeconds;
        if (dur < p.CrossfadeSeconds * 2 + 2) return; // very short tracks: just play through
        if (Audio.Remaining.TotalSeconds <= p.CrossfadeSeconds)
        {
            _crossfading = true;
            Index = next;
            _ = LoadCurrentAsync(p.CrossfadeSeconds);
        }
    }

    public void SaveSettings() => Settings.Save();

    // ---------------- library ----------------

    public AlbumInfo? FindAlbum(string key) => _albumByKey.TryGetValue(key, out var a) ? a : null;

    public AlbumInfo? AlbumOf(Track t) => FindAlbum(t.AlbumKey);

    public ArtistInfo? FindArtist(string name) =>
        Artists.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void RebuildViews()
    {
        var albums = Library.Tracks
            .GroupBy(t => t.AlbumKey)
            .Select(g =>
            {
                var tracks = g.OrderBy(t => t.TrackNumber == 0 ? uint.MaxValue : t.TrackNumber).ThenBy(t => t.Title).ToList();
                var first = tracks[0];
                return new AlbumInfo
                {
                    Key = g.Key,
                    Name = first.DisplayAlbum,
                    Artist = first.GroupArtist,
                    Year = tracks.Max(t => t.Year),
                    Tracks = tracks,
                    LatestAdded = tracks.Max(t => t.AddedUtc),
                    ArtPath = ArtCache.Existing(g.Key),
                };
            })
            .OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Year)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Albums = albums;
        _albumByKey = albums.ToDictionary(a => a.Key);
        Artists = albums
            .GroupBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ArtistInfo { Name = g.First().Artist, Albums = g.ToList() })
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LibraryChanged?.Invoke();
        _ = FillArtAsync();
    }

    /// <summary>Extract missing album art in the background, one album at a time.</summary>
    private async Task FillArtAsync()
    {
        var generation = ++_artGeneration;
        var todo = Albums.Where(a => a.ArtPath == null && !ArtCache.KnownMissing(a.Key)).ToList();
        int n = 0;
        foreach (var album in todo)
        {
            if (generation != _artGeneration) return; // library rebuilt meanwhile
            var key = album.Key;
            var tracks = album.Tracks;
            var path = await Task.Run(() => ArtCache.Extract(key, tracks));
            if (path != null) album.ArtPath = path;
            if (++n % 20 == 0) foreach (var ar in Artists) ar.NotifyArt();
        }
        foreach (var ar in Artists) ar.NotifyArt();
    }

    public async Task<string?> GetArtPathAsync(Track track)
    {
        if (track.IsVideo) return ThumbCache.Existing(track.Path) ?? await ThumbCache.CreateAsync(track.Path);
        var album = AlbumOf(track);
        if (album == null) return null;
        if (album.ArtPath != null) return album.ArtPath;
        var key = album.Key;
        var tracks = album.Tracks;
        var path = await Task.Run(() => ArtCache.Extract(key, tracks));
        if (path != null) album.ArtPath = path;
        return path;
    }

    public async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            ScanStatus?.Invoke("Scanning music…");
            var p1 = new Progress<int>(n => ScanStatus?.Invoke($"Scanning music… {n} songs found"));
            var off1 = await Library.ScanAsync(Settings.MusicFolders, p1, CancellationToken.None);
            RebuildViews();

            ScanStatus?.Invoke("Scanning videos…");
            var p2 = new Progress<int>(n => ScanStatus?.Invoke($"Scanning videos… {n} found"));
            var off2 = await VideoLibrary.ScanAsync(Settings.VideoFolders, p2, CancellationToken.None);
            RebuildVideos();

            OfflineFolders = off1.Concat(off2).ToList();
            var msg = $"{Library.Tracks.Count} songs · {VideoLibrary.Tracks.Count} videos";
            if (OfflineFolders.Count > 0) msg += $" · {OfflineFolders.Count} folder(s) offline, kept their files";
            ScanStatus?.Invoke(msg);
        }
        catch (Exception ex)
        {
            ScanStatus?.Invoke("Scan failed: " + ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    public async Task AddFolderAsync(bool video, string path)
    {
        var list = video ? Settings.VideoFolders : Settings.MusicFolders;
        if (list.Any(f => f.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(path);
        Settings.Save();
        await ScanAsync();
    }

    public async Task RemoveFolderAsync(bool video, string path)
    {
        var list = video ? Settings.VideoFolders : Settings.MusicFolders;
        list.RemoveAll(f => f.Equals(path, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        await ScanAsync();
    }

    // ---------------- videos ----------------

    public void RebuildVideos()
    {
        Videos = VideoLibrary.Tracks.Select(t => new VideoInfo
        {
            Track = t,
            ThumbPath = ThumbCache.Existing(t.Path),
            Progress = ProgressOf(t),
        }).ToList();
        VideosChanged?.Invoke();
        _ = FillThumbsAsync();
    }

    private double ProgressOf(Track t) =>
        t.DurationSeconds > 0 && Settings.VideoPositions.TryGetValue(t.Path, out var pos)
            ? Math.Clamp(pos / t.DurationSeconds * 100, 0, 100)
            : 0;

    private async Task FillThumbsAsync()
    {
        var generation = ++_thumbGeneration;
        foreach (var v in Videos.Where(v => v.ThumbPath == null && !ThumbCache.KnownMissing(v.Track.Path)).ToList())
        {
            if (generation != _thumbGeneration) return;
            var p = await ThumbCache.CreateAsync(v.Track.Path);
            if (p != null) v.ThumbPath = p;
        }
    }

    /// <summary>Called every few seconds while a video plays.</summary>
    public void SaveVideoPosition()
    {
        var t = Current;
        if (t == null || !t.IsVideo || Player.Source == null) return;
        var pos = Player.PlaybackSession.Position.TotalSeconds;
        var dur = Player.PlaybackSession.NaturalDuration.TotalSeconds;
        if (dur > 0 && t.DurationSeconds <= 0) t.DurationSeconds = dur;
        if (pos < 30 || (dur > 0 && pos > dur - 60)) Settings.VideoPositions.Remove(t.Path); // barely started or finished
        else Settings.VideoPositions[t.Path] = pos;
        Settings.Save();
        var info = Videos.FirstOrDefault(v => ReferenceEquals(v.Track, t));
        if (info != null) info.Progress = ProgressOf(t);
    }

    public List<VideoInfo> ContinueWatching() =>
        Videos.Where(v => v.Progress > 0).OrderByDescending(v => v.Progress).Take(12).ToList();

    /// <summary>Called by the metadata fixer after it rewrote tags on these files.</summary>
    public void OnTagsChanged(IReadOnlyCollection<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var a in Albums.Where(a => a.Tracks.Any(t => set.Contains(t.Path)))) ArtCache.Invalidate(a.Key);
        foreach (var t in Library.Tracks.Where(t => set.Contains(t.Path))) ArtCache.Invalidate(t.AlbumKey);
        RebuildViews();
    }

    public bool IsCurrentPath(string path) =>
        HasMedia && Current != null && Current.Path.Equals(path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Run a tag write. If that file is the song playing, close it for a moment (Windows won't let us write
    /// a file that's open), write, then reopen it at the same position and play state.
    /// </summary>
    public async Task<string?> WriteWhileReleasedAsync(string path, Func<string?> write)
    {
        bool playingThis = !CurrentIsVideo && Current != null && Audio.IsCurrentPath(path);
        if (!playingThis) return await Task.Run(write);

        var pos = Audio.Position;
        var wasPlaying = Audio.IsPlaying;
        Audio.Stop();
        var error = await Task.Run(write);
        try
        {
            if (Current != null && Current.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && !Audio.HasTrack)
            {
                var deck = await Task.Run(() => AudioEngine.Open(path));
                deck.Seek(pos);
                Audio.Start(deck);
                if (!wasPlaying) Audio.Pause();
            }
        }
        catch (Exception ex)
        {
            LastError = "Couldn't resume after saving tags: " + ex.Message;
        }
        NotifyState();
        return error;
    }

    private async Task UpdateSmtcAsync(Track track)
    {
        if (_smtc == null) return;
        try
        {
            var du = _smtc.DisplayUpdater;
            du.ClearAll();
            if (track.IsVideo)
            {
                du.Type = MediaPlaybackType.Video;
                du.VideoProperties.Title = track.Title;
                du.VideoProperties.Subtitle = track.Artist;
            }
            else
            {
                du.Type = MediaPlaybackType.Music;
                du.MusicProperties.Title = track.Title;
                du.MusicProperties.Artist = track.Artist;
                du.MusicProperties.AlbumTitle = track.Album;
            }
            var art = await GetArtPathAsync(track);
            if (art != null)
                du.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(art));
            du.Update();
        }
        catch { }
    }

    // ---------------- queue ----------------

    public void PlayList(IList<Track> tracks, int start = 0, bool shuffle = false)
    {
        if (tracks.Count == 0) return;
        var list = tracks.ToList();
        _unshuffled = list;
        if (shuffle) Shuffle = true;

        if (Shuffle)
        {
            var first = shuffle && start == 0 ? list[_rng.Next(list.Count)] : list[Math.Clamp(start, 0, list.Count - 1)];
            Queue = new List<Track> { first };
            Queue.AddRange(Shuffled(list.Where(t => !ReferenceEquals(t, first))));
            Index = 0;
        }
        else
        {
            Queue = list;
            Index = Math.Clamp(start, 0, list.Count - 1);
        }
        QueueChanged?.Invoke();
        _ = LoadCurrentAsync();
    }

    public void PlayAt(int index)
    {
        if (index < 0 || index >= Queue.Count) return;
        Index = index;
        _ = LoadCurrentAsync();
    }

    public void PlayNext(Track t)
    {
        if (Current == null) { PlayList(new[] { t }); return; }
        Queue.Insert(Index + 1, t);
        _unshuffled.Add(t);
        QueueChanged?.Invoke();
    }

    public void AddToQueue(Track t)
    {
        if (Current == null) { PlayList(new[] { t }); return; }
        Queue.Add(t);
        _unshuffled.Add(t);
        QueueChanged?.Invoke();
    }

    public void RemoveFromQueue(int index)
    {
        if (index < 0 || index >= Queue.Count || index == Index) return;
        var t = Queue[index];
        Queue.RemoveAt(index);
        _unshuffled.Remove(t);
        if (index < Index) Index--;
        QueueChanged?.Invoke();
    }

    public void ClearQueue()
    {
        var cur = Current;
        Queue = cur == null ? new() : new List<Track> { cur };
        _unshuffled = Queue.ToList();
        Index = cur == null ? -1 : 0;
        QueueChanged?.Invoke();
    }

    private IEnumerable<Track> Shuffled(IEnumerable<Track> src)
    {
        var a = src.ToList();
        for (int i = a.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a;
    }

    public void ToggleShuffle()
    {
        var cur = Current;
        Shuffle = !Shuffle;
        if (cur != null)
        {
            if (Shuffle)
            {
                _unshuffled = Queue.ToList();
                Queue = new List<Track> { cur };
                Queue.AddRange(Shuffled(_unshuffled.Where(t => !ReferenceEquals(t, cur))));
                Index = 0;
            }
            else
            {
                Queue = _unshuffled.ToList();
                Index = Math.Max(0, Queue.FindIndex(t => ReferenceEquals(t, cur)));
            }
            QueueChanged?.Invoke();
        }
        PlaybackStateChanged?.Invoke();
    }

    public void CycleRepeat()
    {
        Repeat = Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
        PlaybackStateChanged?.Invoke();
    }

    // ---------------- transport ----------------

    public void TogglePlay()
    {
        if (!HasMedia)
        {
            if (Current != null) _ = LoadCurrentAsync();
            else if (Library.Tracks.Count > 0) PlayList(Library.Tracks, 0, shuffle: true);
            return;
        }
        if (CurrentIsVideo)
        {
            if (IsPlaying) Player.Pause(); else Player.Play();
        }
        else
        {
            if (Audio.IsPlaying) Audio.Pause(); else Audio.Play();
            NotifyState();
        }
    }

    public void Next()
    {
        if (Queue.Count == 0) return;
        if (Index + 1 < Queue.Count) PlayAt(Index + 1);
        else if (Repeat == RepeatMode.All) PlayAt(0);
    }

    public void Previous()
    {
        if (Queue.Count == 0) return;
        if (Position.TotalSeconds > 3 || Index == 0) Seek(TimeSpan.Zero);
        else PlayAt(Index - 1);
    }

    public void Seek(TimeSpan position)
    {
        if (CurrentIsVideo) { if (Player.Source != null) Player.PlaybackSession.Position = position; }
        else Audio.Seek(position);
    }

    public void SetVolume(double v)
    {
        v = Math.Clamp(v, 0, 1);
        Player.Volume = v;
        Audio.Volume = v;
        Settings.Volume = v;
    }

    private void OnEnded()
    {
        if (Current is Track ended && ended.IsVideo)
        {
            Settings.VideoPositions.Remove(ended.Path);
            var info = Videos.FirstOrDefault(v => ReferenceEquals(v.Track, ended));
            if (info != null) info.Progress = 0;
        }
        if (Repeat == RepeatMode.One) { PlayAt(Index); return; }
        if (Index + 1 < Queue.Count) PlayAt(Index + 1);
        else if (Repeat == RepeatMode.All && Queue.Count > 0) PlayAt(0);
        else
        {
            if (!CurrentIsVideo) Audio.Stop(); // end of the queue: Play starts this song again
            NotifyState();
        }
    }

    private async Task LoadCurrentAsync(double fadeSeconds = 0)
    {
        var track = Current;
        if (track == null) { _crossfading = false; return; }
        LastError = "";
        TrackChanged?.Invoke();

        try
        {
            if (track.IsVideo)
            {
                Audio.Stop();
                var file = await StorageFile.GetFileFromPathAsync(track.Path);
                if (!ReferenceEquals(track, Current)) return; // user skipped meanwhile
                var source = MediaSource.CreateFromStorageFile(file);
                await AddExternalSubtitlesAsync(source, track.Path);
                var item = new MediaPlaybackItem(source);
                if (!ReferenceEquals(track, Current)) return;
                _pendingSeek = Settings.VideoPositions.TryGetValue(track.Path, out var resume)
                    ? TimeSpan.FromSeconds(resume)
                    : null;
                Player.Source = item;
                Player.Play();
            }
            else
            {
                if (Player.Source != null) { Player.Pause(); Player.Source = null; }
                var path = track.Path;
                var deck = await Task.Run(() => AudioEngine.Open(path)); // may be slow on a NAS
                if (!ReferenceEquals(track, Current)) { deck.Dispose(); return; }
                Audio.Start(deck, fadeSeconds);
                NotifyState();
            }
            await UpdateSmtcAsync(track);
        }
        catch (Exception ex)
        {
            LastError = "Can't play file: " + ex.Message;
            NotifyState();
        }
        finally
        {
            _crossfading = false;
        }

        var album = track.IsVideo ? null : AlbumOf(track);
        if (album != null)
        {
            Settings.Recent.Remove(album.Key);
            Settings.Recent.Insert(0, album.Key);
            if (Settings.Recent.Count > 30) Settings.Recent.RemoveRange(30, Settings.Recent.Count - 30);
            Settings.Save();
        }
    }

    private static async Task AddExternalSubtitlesAsync(MediaSource source, string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (dir == null) return;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        try
        {
            // movie.srt, movie.en.srt, movie.eng.vtt, ...
            foreach (var sub in Directory.EnumerateFiles(dir, stem + "*")
                         .Where(f => f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
                         .Take(8))
            {
                var file = await StorageFile.GetFileFromPathAsync(sub);
                var stream = await file.OpenReadAsync();
                var tts = TimedTextSource.CreateFromStream(stream);
                source.ExternalTimedTextSources.Add(tts);
            }
        }
        catch { }
    }

    /// <summary>Subtitle/caption tracks of the current video: (index, label).</summary>
    public List<(uint Index, string Label)> SubtitleTracks()
    {
        var list = new List<(uint, string)>();
        if (Player.Source is not MediaPlaybackItem item) return list;
        for (uint i = 0; i < item.TimedMetadataTracks.Count; i++)
        {
            var t = item.TimedMetadataTracks[(int)i];
            if (t.TimedMetadataKind != TimedMetadataKind.Subtitle && t.TimedMetadataKind != TimedMetadataKind.Caption) continue;
            var label = !string.IsNullOrWhiteSpace(t.Label) ? t.Label
                : !string.IsNullOrWhiteSpace(t.Language) ? t.Language
                : $"Track {i + 1}";
            list.Add((i, label));
        }
        return list;
    }

    public void SetSubtitle(uint? index)
    {
        if (Player.Source is not MediaPlaybackItem item) return;
        for (uint i = 0; i < item.TimedMetadataTracks.Count; i++)
            item.TimedMetadataTracks.SetPresentationMode(i,
                i == index ? TimedMetadataTrackPresentationMode.PlatformPresented : TimedMetadataTrackPresentationMode.Disabled);
    }
}
