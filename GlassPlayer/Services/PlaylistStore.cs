using System.Text;
using System.Text.Json;
using GlassPlayer.Models;

namespace GlassPlayer.Services;

public class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> Paths { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Modified { get; set; } = DateTime.UtcNow;
}

/// <summary>User playlists, saved in %LOCALAPPDATA%\GlassPlayer\playlists.json. Paths, so they survive rescans.</summary>
public class PlaylistStore
{
    private static string FilePath => Path.Combine(AppSettings.DataDir, "playlists.json");
    public List<Playlist> All { get; private set; } = new();
    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                All = JsonSerializer.Deserialize<List<Playlist>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { All = new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(All)); } catch { }
        Changed?.Invoke();
    }

    public Playlist? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public Playlist Create(string name, IEnumerable<string>? paths = null)
    {
        var p = new Playlist { Name = UniqueName(name), Paths = paths?.ToList() ?? new() };
        All.Add(p);
        Save();
        return p;
    }

    private string UniqueName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "New playlist" : name.Trim();
        var candidate = name;
        for (int i = 2; All.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++) candidate = $"{name} ({i})";
        return candidate;
    }

    public void Rename(Playlist p, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        p.Name = name.Trim();
        p.Modified = DateTime.UtcNow;
        Save();
    }

    public void Delete(Playlist p)
    {
        All.Remove(p);
        Save();
    }

    public void Add(Playlist p, IEnumerable<string> paths)
    {
        p.Paths.AddRange(paths);
        p.Modified = DateTime.UtcNow;
        Save();
    }

    public void SetPaths(Playlist p, List<string> paths)
    {
        p.Paths = paths;
        p.Modified = DateTime.UtcNow;
        Save();
    }

    // ---------------- m3u ----------------

    public Playlist ImportM3u(string file)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        var paths = new List<string>();
        foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith("#")) continue;
            try
            {
                string path;
                if (line.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) path = new Uri(line).LocalPath;
                else if (line.Contains("://")) continue; // web streams aren't supported
                else path = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(dir, line));
                paths.Add(path);
            }
            catch { }
        }
        return Create(Path.GetFileNameWithoutExtension(file), paths);
    }

    public static void ExportM3u(string file, string name, IEnumerable<Track> tracks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#PLAYLIST:" + name);
        foreach (var t in tracks)
        {
            sb.AppendLine($"#EXTINF:{(int)Math.Round(t.DurationSeconds)},{t.Artist} - {t.Title}");
            sb.AppendLine(t.Path);
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
    }
}

public class TrackStats
{
    public int PlayCount { get; set; }
    public DateTime LastPlayed { get; set; }
    public bool Favorite { get; set; }
}

/// <summary>Play counts, last played and favourites per file path.</summary>
public class StatsStore
{
    private static string FilePath => Path.Combine(AppSettings.DataDir, "stats.json");
    private Dictionary<string, TrackStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    public event Action? FavoritesChanged;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, TrackStats>>(File.ReadAllText(FilePath));
                if (d != null) _stats = new Dictionary<string, TrackStats>(d, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
    }

    private void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_stats)); } catch { }
    }

    public TrackStats? Get(string path) => _stats.TryGetValue(path, out var s) ? s : null;
    public int PlayCount(string path) => Get(path)?.PlayCount ?? 0;
    public bool IsFavorite(string path) => Get(path)?.Favorite == true;

    private TrackStats Ensure(string path)
    {
        if (!_stats.TryGetValue(path, out var s)) _stats[path] = s = new TrackStats();
        return s;
    }

    public void RecordPlay(string path)
    {
        var s = Ensure(path);
        s.PlayCount++;
        s.LastPlayed = DateTime.UtcNow;
        Save();
    }

    public void SetFavorite(string path, bool fav)
    {
        Ensure(path).Favorite = fav;
        Save();
        FavoritesChanged?.Invoke();
    }

    public IEnumerable<KeyValuePair<string, TrackStats>> All => _stats;
}
