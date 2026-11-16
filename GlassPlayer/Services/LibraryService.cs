using System.Text.Json;
using GlassPlayer.Models;

namespace GlassPlayer.Services;

/// <summary>Scans a music folder (local or NAS/UNC) and caches the result as JSON.</summary>
public class LibraryService
{
    private static readonly string[] Extensions =
        { ".mp3", ".flac", ".m4a", ".aac", ".wav", ".wma", ".ogg", ".opus" };

    private static string DataDir
    {
        get
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string IndexPath => System.IO.Path.Combine(DataDir, "library.json");
    private static string SettingsPath => System.IO.Path.Combine(DataDir, "settings.json");

    public List<Track> Tracks { get; private set; } = new();
    public string? LibraryFolder { get; private set; }

    public void LoadCache()
    {
        try
        {
            if (File.Exists(SettingsPath))
                LibraryFolder = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))?.LibraryFolder;
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
        LibraryFolder = folder;
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings { LibraryFolder = folder }));

        var existing = Tracks.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        var result = await Task.Run(() =>
        {
            var list = new List<Track>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (!Extensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant())) continue;
                list.Add(existing.TryGetValue(file, out var cached) ? cached : ReadTrack(file));
                if (list.Count % 25 == 0) progress?.Report(list.Count);
            }
            return list;
        }, ct);

        Tracks = result.OrderBy(t => t.Artist).ThenBy(t => t.Album).ThenBy(t => t.TrackNumber).ToList();
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(Tracks));
        progress?.Report(Tracks.Count);
    }

    public void SaveIndex() => File.WriteAllText(IndexPath, JsonSerializer.Serialize(Tracks));

    public static Track ReadTrack(string file)
    {
        var track = new Track
        {
            Path = file,
            Title = System.IO.Path.GetFileNameWithoutExtension(file),
            Artist = "Unknown Artist",
        };
        try
        {
            using var tl = TagLib.File.Create(file);
            if (!string.IsNullOrWhiteSpace(tl.Tag.Title)) track.Title = tl.Tag.Title;
            track.Artist = tl.Tag.FirstPerformer ?? tl.Tag.FirstAlbumArtist ?? "Unknown Artist";
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

    private class Settings
    {
        public string? LibraryFolder { get; set; }
    }
}
