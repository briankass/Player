using System.Text.Json;
using System.Text.RegularExpressions;
using GlassPlayer.Models;

namespace GlassPlayer.Services;

/// <summary>Scans music or video folders (local or NAS/UNC) and caches the result as JSON.</summary>
public class LibraryService
{
    private static readonly string[] MusicExtensions =
        { ".mp3", ".flac", ".m4a", ".aac", ".wav", ".wma", ".ogg", ".opus" };
    private static readonly string[] VideoExtensions =
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg" };

    private readonly bool _video;
    private readonly string[] _extensions;
    private string IndexPath => Path.Combine(AppSettings.DataDir, _video ? "videos.json" : "library.json");

    public LibraryService(bool video)
    {
        _video = video;
        _extensions = video ? VideoExtensions : MusicExtensions;
    }

    public List<Track> Tracks { get; set; } = new();

    public void LoadCache()
    {
        try
        {
            if (File.Exists(IndexPath))
                Tracks = JsonSerializer.Deserialize<List<Track>>(File.ReadAllText(IndexPath)) ?? new();
        }
        catch
        {
            Tracks = new();
        }
    }

    public static bool IsUnder(string path, string folder)
    {
        var f = folder.TrimEnd('\\', '/') + "\\";
        return path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rescan all folders. Folders that can't be reached right now (NAS off, drive unplugged) keep their
    /// previously indexed files; files under folders no longer in the list are dropped.
    /// Returns the folders that were offline.
    /// </summary>
    public async Task<List<string>> ScanAsync(IReadOnlyList<string> folders, IProgress<int>? progress, CancellationToken ct)
    {
        var snapshot = Tracks.ToList();
        var existing = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in snapshot) existing[t.Path] = t;
        var offline = new List<string>();

        var result = await Task.Run(() =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<Track>();
            foreach (var folder in folders)
            {
                bool reachable;
                try { reachable = Directory.Exists(folder); } catch { reachable = false; }
                if (!reachable)
                {
                    offline.Add(folder);
                    foreach (var t in snapshot.Where(t => IsUnder(t.Path, folder)))
                        if (seen.Add(t.Path)) list.Add(t);
                    continue;
                }

                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var file in Directory.EnumerateFiles(folder, "*.*", opts))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                    if (!seen.Add(file)) continue; // overlapping folders
                    // entries cached by older versions lack AddedUtc/AlbumArtist: re-read them once
                    list.Add(existing.TryGetValue(file, out var cached) && cached.AddedUtc != default
                        ? cached
                        : (_video ? ReadVideo(file) : ReadTrack(file)));
                    if (list.Count % 25 == 0) progress?.Report(list.Count);
                }
            }
            return list;
        }, ct);

        Tracks = _video
            ? result.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList()
            : result.OrderBy(t => t.GroupArtist).ThenBy(t => t.Album).ThenBy(t => t.TrackNumber).ToList();
        SaveIndex();
        progress?.Report(Tracks.Count);
        return offline;
    }

    public void SaveIndex()
    {
        try { File.WriteAllText(IndexPath, JsonSerializer.Serialize(Tracks)); } catch { }
    }

    public static Track ReadTrack(string file)
    {
        var track = new Track
        {
            Path = file,
            Title = Path.GetFileNameWithoutExtension(file),
            Artist = "Unknown Artist",
        };
        try { track.AddedUtc = File.GetCreationTimeUtc(file); } catch { track.AddedUtc = DateTime.UtcNow; }
        try
        {
            using var tl = TagLib.File.Create(file);
            if (!string.IsNullOrWhiteSpace(tl.Tag.Title)) track.Title = tl.Tag.Title;
            track.Artist = tl.Tag.FirstPerformer ?? tl.Tag.FirstAlbumArtist ?? "Unknown Artist";
            track.AlbumArtist = tl.Tag.FirstAlbumArtist ?? "";
            track.Album = tl.Tag.Album ?? "";
            track.Year = tl.Tag.Year;
            track.TrackNumber = tl.Tag.Track;
            track.DurationSeconds = tl.Properties.Duration.TotalSeconds;
            track.HasArt = tl.Tag.Pictures.Length > 0;
        }
        catch
        {
            // unreadable tags: keep filename-based defaults
        }
        return track;
    }

    private static readonly Regex Separators = new(@"[._]+", RegexOptions.Compiled);

    public static Track ReadVideo(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var track = new Track
        {
            Path = file,
            IsVideo = true,
            Title = Separators.Replace(name, " ").Trim(),
            Artist = Path.GetFileName(Path.GetDirectoryName(file) ?? "") ?? "",
        };
        try { track.AddedUtc = File.GetCreationTimeUtc(file); } catch { track.AddedUtc = DateTime.UtcNow; }
        try
        {
            using var tl = TagLib.File.Create(file);
            if (!string.IsNullOrWhiteSpace(tl.Tag.Title)) track.Title = tl.Tag.Title;
            track.DurationSeconds = tl.Properties.Duration.TotalSeconds;
        }
        catch
        {
            // duration unknown until played
        }
        return track;
    }
}
