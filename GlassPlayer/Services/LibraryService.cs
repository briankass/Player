using System.Text.Json;
using GlassPlayer.Models;

namespace GlassPlayer.Services;

/// <summary>Scans a music folder (local or NAS/UNC) and caches the result as JSON.</summary>
public class LibraryService
{
    private static readonly string[] Extensions =
        { ".mp3", ".flac", ".m4a", ".aac", ".wav", ".wma", ".ogg", ".opus" };

    private static string IndexPath => Path.Combine(AppSettings.DataDir, "library.json");

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

    public async Task ScanAsync(string folder, IProgress<int>? progress, CancellationToken ct)
    {
        var existing = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tracks) existing[t.Path] = t;

        var result = await Task.Run(() =>
        {
            var list = new List<Track>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                list.Add(existing.TryGetValue(file, out var cached) ? cached : ReadTrack(file));
                if (list.Count % 25 == 0) progress?.Report(list.Count);
            }
            return list;
        }, ct);

        Tracks = result.OrderBy(t => t.GroupArtist).ThenBy(t => t.Album).ThenBy(t => t.TrackNumber).ToList();
        SaveIndex();
        progress?.Report(Tracks.Count);
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
        try { track.AddedUtc = File.GetCreationTimeUtc(file); } catch { }
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
}
