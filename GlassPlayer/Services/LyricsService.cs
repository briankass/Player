using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlassPlayer.Models;

namespace GlassPlayer.Services;

public class LyricLine : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public TimeSpan Time { get; set; }
    public string Text { get; set; } = "";

    private double _opacity = 1.0;
    /// <summary>Dimmed for lines that aren't being sung, full for the current one.</summary>
    public double Opacity
    {
        get => _opacity;
        set
        {
            if (Math.Abs(_opacity - value) < 0.001) return;
            _opacity = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Opacity)));
        }
    }
}

public class LyricsResult
{
    public List<LyricLine> Lines { get; set; } = new();
    public bool Synced { get; set; }
    public string Source { get; set; } = "";
}

/// <summary>Loads lyrics from a sibling .lrc file, a local cache, or LRCLIB (https://lrclib.net).</summary>
public class LyricsService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex TimeTag = new(@"\[(\d+):(\d+(?:\.\d+)?)\]", RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GlassPlayer/0.1 (https://github.com/briankass/player)");
        return c;
    }

    private static string CacheDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GlassPlayer", "lyrics");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public async Task<LyricsResult?> GetLyricsAsync(Track track, CancellationToken ct)
    {
        // 1. .lrc next to the audio file
        var lrcPath = Path.ChangeExtension(track.Path, ".lrc");
        try
        {
            if (File.Exists(lrcPath))
                return Parse(await File.ReadAllTextAsync(lrcPath, ct), "local .lrc file");
        }
        catch { }

        // 2. cache
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(track.Path.ToLowerInvariant())));
        var cachePath = Path.Combine(CacheDir, key + ".lrc");
        try
        {
            if (File.Exists(cachePath))
                return Parse(await File.ReadAllTextAsync(cachePath, ct), "cache");
        }
        catch { }

        // 3. LRCLIB
        try
        {
            var url = "https://lrclib.net/api/get"
                + "?artist_name=" + Uri.EscapeDataString(track.Artist)
                + "&track_name=" + Uri.EscapeDataString(track.Title)
                + "&album_name=" + Uri.EscapeDataString(track.Album)
                + "&duration=" + (int)Math.Round(track.DurationSeconds);
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string text = "";
            if (root.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
                text = s.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text) &&
                root.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String)
                text = p.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text)) return null;
            await File.WriteAllTextAsync(cachePath, text, ct);
            return Parse(text, "LRCLIB");
        }
        catch
        {
            return null;
        }
    }

    public static LyricsResult Parse(string text, string source)
    {
        var result = new LyricsResult { Source = source };
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = TimeTag.Matches(line);
            if (matches.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("[")) // plain lyrics
                    result.Lines.Add(new LyricLine { Time = TimeSpan.Zero, Text = line.Trim() });
                continue;
            }
            var content = TimeTag.Replace(line, "").Trim();
            foreach (Match m in matches)
            {
                var mins = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var secs = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                result.Lines.Add(new LyricLine { Time = TimeSpan.FromSeconds(mins * 60 + secs), Text = content });
                result.Synced = true;
            }
        }
        if (result.Synced) result.Lines = result.Lines.OrderBy(l => l.Time).ToList();
        return result;
    }
}
