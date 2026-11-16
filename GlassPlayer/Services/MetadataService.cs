using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace GlassPlayer.Services;

public class MetadataMatch
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public uint Year { get; set; }
    public uint TrackNumber { get; set; }
    public string ReleaseId { get; set; } = "";
    public string ReleaseGroupId { get; set; } = "";
    public int Score { get; set; }
}

/// <summary>
/// Looks up tags on MusicBrainz and front covers on the Cover Art Archive.
/// MusicBrainz allows ~1 request/second and requires a descriptive User-Agent.
/// </summary>
public class MetadataService
{
    private const string UserAgent = "GlassPlayer/0.1 ( https://github.com/briankass/Player )";
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;
    private readonly Dictionary<string, byte[]?> _coverCache = new();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    private static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if ("+-&|!(){}[]^\"~*?:\\/".IndexOf(ch) >= 0) sb.Append('\\');
            sb.Append(ch);
        }
        return "\"" + sb + "\"";
    }

    private static async Task<string?> GetMusicBrainzAsync(string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastCall);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastCall = DateTime.UtcNow;
            using var resp = await Http.GetAsync(url, ct);
            if ((int)resp.StatusCode == 503) // rate limited: back off once
            {
                await Task.Delay(3000, ct);
                _lastCall = DateTime.UtcNow;
                using var retry = await Http.GetAsync(url, ct);
                return retry.IsSuccessStatusCode ? await retry.Content.ReadAsStringAsync(ct) : null;
            }
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Find the best recording match. durationSeconds &lt;= 0 means unknown.</summary>
    public async Task<MetadataMatch?> LookupAsync(string artist, string title, string album,
        double durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var q = "recording:" + Esc(title);
        if (!string.IsNullOrWhiteSpace(artist)) q += " AND artist:" + Esc(artist);
        if (!string.IsNullOrWhiteSpace(album)) q += " AND release:" + Esc(album);

        var json = await GetMusicBrainzAsync(
            "https://musicbrainz.org/ws/2/recording?fmt=json&limit=10&query=" + Uri.EscapeDataString(q), ct);
        // album name may be wrong/missing on MusicBrainz: retry without it
        if (!string.IsNullOrWhiteSpace(album) && (json == null || !json.Contains("\"releases\"")))
            return await LookupAsync(artist, title, "", durationSeconds, ct);
        if (json == null) return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("recordings", out var recs)) return null;

        foreach (var rec in recs.EnumerateArray())
        {
            var score = rec.TryGetProperty("score", out var s) && s.TryGetInt32(out var si) ? si : 0;
            if (score < 85) continue;

            if (durationSeconds > 0 && rec.TryGetProperty("length", out var len) && len.TryGetInt32(out var ms)
                && Math.Abs(ms / 1000.0 - durationSeconds) > 10)
                continue;

            if (!rec.TryGetProperty("releases", out var releases) || releases.GetArrayLength() == 0) continue;

            var artistName = new StringBuilder();
            if (rec.TryGetProperty("artist-credit", out var credits))
                foreach (var c in credits.EnumerateArray())
                    artistName.Append(Str(c, "name")).Append(Str(c, "joinphrase"));

            JsonElement best = default;
            int bestRank = int.MinValue;
            string bestDate = "9999";
            foreach (var rel in releases.EnumerateArray())
            {
                int rank = 0;
                var relTitle = Str(rel, "title");
                if (!string.IsNullOrWhiteSpace(album) && relTitle.Equals(album, StringComparison.OrdinalIgnoreCase)) rank += 4;
                if (Str(rel, "status") == "Official") rank += 1;
                if (rel.TryGetProperty("release-group", out var rg))
                {
                    if (Str(rg, "primary-type") == "Album") rank += 2;
                    if (rg.TryGetProperty("secondary-types", out var st) && st.GetArrayLength() > 0) rank -= 2; // compilations, live
                }
                var date = Str(rel, "date");
                if (date.Length == 0) date = "9999";
                if (rank > bestRank || (rank == bestRank && string.CompareOrdinal(date, bestDate) < 0))
                {
                    best = rel; bestRank = rank; bestDate = date;
                }
            }

            var match = new MetadataMatch
            {
                Title = Str(rec, "title"),
                Artist = artistName.ToString(),
                Album = Str(best, "title"),
                ReleaseId = Str(best, "id"),
                Score = score,
            };
            if (best.TryGetProperty("release-group", out var brg)) match.ReleaseGroupId = Str(brg, "id");
            if (bestDate.Length >= 4 && uint.TryParse(bestDate[..4], out var y) && y < 9999) match.Year = y;
            if (best.TryGetProperty("media", out var media) && media.GetArrayLength() > 0
                && media[0].TryGetProperty("track", out var tracks) && tracks.GetArrayLength() > 0
                && uint.TryParse(Str(tracks[0], "number"), out var tn))
                match.TrackNumber = tn;
            return match;
        }
        return null;
    }

    /// <summary>Front cover (500px) for a release, falling back to its release group. Cached per release.</summary>
    public async Task<byte[]?> GetCoverAsync(string releaseId, string releaseGroupId, CancellationToken ct)
    {
        var key = releaseId + "|" + releaseGroupId;
        if (_coverCache.TryGetValue(key, out var cached)) return cached;

        byte[]? data = null;
        foreach (var url in new[]
                 {
                     string.IsNullOrEmpty(releaseId) ? null : $"https://coverartarchive.org/release/{releaseId}/front-500",
                     string.IsNullOrEmpty(releaseGroupId) ? null : $"https://coverartarchive.org/release-group/{releaseGroupId}/front-500",
                 })
        {
            if (url == null) continue;
            try
            {
                using var resp = await Http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode) { data = await resp.Content.ReadAsByteArrayAsync(ct); break; }
            }
            catch (HttpRequestException) { }
        }
        _coverCache[key] = data;
        return data;
    }
}
