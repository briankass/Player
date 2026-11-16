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
    public MediaPlayer Player { get; } = new();

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

    public bool IsPlaying => Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

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
        Player.MediaEnded += (_, _) => _ui.TryEnqueue(OnEnded);
        Player.MediaOpened += (_, _) =>
        {
            // resume videos where they were left
            if (_pendingSeek is TimeSpan seek) Player.PlaybackSession.Position = seek;
            _pendingSeek = null;
        };
        Player.MediaFailed += (_, e) => _ui.TryEnqueue(() =>
        {
            LastError = e.ErrorMessage;
            PlaybackStateChanged?.Invoke();
        });
        Player.PlaybackSession.PlaybackStateChanged += (_, _) => _ui.TryEnqueue(() => PlaybackStateChanged?.Invoke());

        // media keys + the Windows media flyout
        var cm = Player.CommandManager;
        cm.IsEnabled = true;
        cm.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.NextReceived += (_, e) => { e.Handled = true; _ui.TryEnqueue(Next); };
        cm.PreviousReceived += (_, e) => { e.Handled = true; _ui.TryEnqueue(Previous); };
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
        Player.Source != null && Current != null && Current.Path.Equals(path, StringComparison.OrdinalIgnoreCase);

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
        if (Player.Source == null)
        {
            if (Current != null) _ = LoadCurrentAsync();
            else if (Library.Tracks.Count > 0) PlayList(Library.Tracks, 0, shuffle: true);
            return;
        }
        if (IsPlaying) Player.Pause(); else Player.Play();
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
        if (Player.PlaybackSession.Position.TotalSeconds > 3 || Index == 0)
            Player.PlaybackSession.Position = TimeSpan.Zero;
        else
            PlayAt(Index - 1);
    }

    public void Seek(TimeSpan position)
    {
        if (Player.Source != null) Player.PlaybackSession.Position = position;
    }

    public void SetVolume(double v)
    {
        Player.Volume = Math.Clamp(v, 0, 1);
        Settings.Volume = Player.Volume;
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
        else PlaybackStateChanged?.Invoke();
    }

    private async Task LoadCurrentAsync()
    {
        var track = Current;
        if (track == null) return;
        LastError = "";
        TrackChanged?.Invoke();

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(track.Path);
            if (!ReferenceEquals(track, Current)) return; // user skipped meanwhile
            var source = MediaSource.CreateFromStorageFile(file);
            if (track.IsVideo) await AddExternalSubtitlesAsync(source, track.Path);
            var item = new MediaPlaybackItem(source);

            var props = item.GetDisplayProperties();
            if (track.IsVideo)
            {
                props.Type = MediaPlaybackType.Video;
                props.VideoProperties.Title = track.Title;
                props.VideoProperties.Subtitle = track.Artist;
            }
            else
            {
                props.Type = MediaPlaybackType.Music;
                props.MusicProperties.Title = track.Title;
                props.MusicProperties.Artist = track.Artist;
                props.MusicProperties.AlbumTitle = track.Album;
            }
            var art = await GetArtPathAsync(track);
            if (art != null)
            {
                try { props.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(art)); }
                catch { }
            }
            item.ApplyDisplayProperties(props);

            if (!ReferenceEquals(track, Current)) return;
            _pendingSeek = track.IsVideo && Settings.VideoPositions.TryGetValue(track.Path, out var resume)
                ? TimeSpan.FromSeconds(resume)
                : null;
            Player.Source = item;
            Player.Play();
        }
        catch (Exception ex)
        {
            LastError = "Can't open file: " + ex.Message;
            PlaybackStateChanged?.Invoke();
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
