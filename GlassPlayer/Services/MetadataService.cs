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
    public double LengthSeconds { get; set; }
}

public class ReleaseCandidate
{
    public string Id { get; set; } = "";
    public string ReleaseGroupId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public uint Year { get; set; }
    public int TrackCount { get; set; }
    public string Country { get; set; } = "";
    public string Format { get; set; } = "";
    public string Type { get; set; } = "";
    public int Score { get; set; }
}

public class ReleaseTrack
{
    public int Disc { get; set; }
    public uint Number { get; set; }
    public int OverallIndex { get; set; } // 1-based across all discs
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public double LengthSeconds { get; set; }
}

/// <summary>
/// Looks up tags on MusicBrainz and front covers on the Cover Art Archive.
/// MusicBrainz allows ~1 request/second and requires a descriptive User-Agent.
/// </summary>
public class MetadataService
{
    private const string UserAgent = "GlassPlayer/0.2 ( https://github.com/briankass/Player )";
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

    public static string CoverThumbUrl(string releaseId) => $"https://coverartarchive.org/release/{releaseId}/front-250";

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

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static string Credit(JsonElement e)
    {
        var sb = new StringBuilder();
        if (e.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array)
            foreach (var c in credits.EnumerateArray())
                sb.Append(Str(c, "name")).Append(Str(c, "joinphrase"));
        return sb.ToString();
    }

    private static uint YearOf(string date) =>
        date.Length >= 4 && uint.TryParse(date[..4], out var y) ? y : 0;

    // ---------------- recordings (songs) ----------------

    private static string RecordingQuery(string artist, string title, string album)
    {
        var q = "recording:" + Esc(title);
        if (!string.IsNullOrWhiteSpace(artist)) q += " AND artist:" + Esc(artist);
        if (!string.IsNullOrWhiteSpace(album)) q += " AND release:" + Esc(album);
        return "https://musicbrainz.org/ws/2/recording?fmt=json&limit=25&query=" + Uri.EscapeDataString(q);
    }

    /// <summary>Build a match from a recording, choosing its most sensible release (official album, matching name, earliest).</summary>
    private static MetadataMatch? BuildMatch(JsonElement rec, string albumHint)
    {
        if (!rec.TryGetProperty("releases", out var releases) || releases.GetArrayLength() == 0) return null;

        JsonElement best = default;
        int bestRank = int.MinValue;
        string bestDate = "9999";
        foreach (var rel in releases.EnumerateArray())
        {
            int rank = 0;
            if (!string.IsNullOrWhiteSpace(albumHint) && Str(rel, "title").Equals(albumHint, StringComparison.OrdinalIgnoreCase)) rank += 4;
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
            Artist = Credit(rec),
            Album = Str(best, "title"),
            ReleaseId = Str(best, "id"),
            Score = Int(rec, "score"),
            LengthSeconds = Int(rec, "length") / 1000.0,
            Year = YearOf(bestDate),
        };
        if (best.TryGetProperty("release-group", out var brg)) match.ReleaseGroupId = Str(brg, "id");
        if (best.TryGetProperty("media", out var media) && media.GetArrayLength() > 0
            && media[0].TryGetProperty("track", out var tracks) && tracks.GetArrayLength() > 0
            && uint.TryParse(Str(tracks[0], "number"), out var tn))
            match.TrackNumber = tn;
        return match;
    }

    /// <summary>Best confident match (score ≥ 85, length within 10 s) for automatic fixing.</summary>
    public async Task<MetadataMatch?> LookupAsync(string artist, string title, string album, double durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var json = await GetMusicBrainzAsync(RecordingQuery(artist, title, album), ct);
        // album name may be wrong/missing on MusicBrainz: retry without it
        if (!string.IsNullOrWhiteSpace(album) && (json == null || !json.Contains("\"releases\"")))
            return await LookupAsync(artist, title, "", durationSeconds, ct);
        if (json == null) return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("recordings", out var recs)) return null;
        foreach (var rec in recs.EnumerateArray())
        {
            if (Int(rec, "score") < 85) continue;
            var len = Int(rec, "length") / 1000.0;
            if (durationSeconds > 0 && len > 0 && Math.Abs(len - durationSeconds) > 10) continue;
            var m = BuildMatch(rec, album);
            if (m != null) return m;
        }
        return null;
    }

    /// <summary>Several candidates for the user to choose from.</summary>
    public async Task<List<MetadataMatch>> SearchRecordingsAsync(string artist, string title, string album, CancellationToken ct)
    {
        var list = new List<MetadataMatch>();
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist)) return list;
        var json = await GetMusicBrainzAsync(RecordingQuery(artist, string.IsNullOrWhiteSpace(title) ? "*" : title, album), ct);
        if (json == null) return list;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("recordings", out var recs)) return list;
        var seen = new HashSet<string>();
        foreach (var rec in recs.EnumerateArray())
        {
            var m = BuildMatch(rec, album);
            if (m == null) continue;
            if (!seen.Add($"{m.Title}|{m.Artist}|{m.Album}".ToLowerInvariant())) continue; // same song on same album
            list.Add(m);
            if (list.Count >= 15) break;
        }
        return list;
    }

    // ---------------- releases (albums) ----------------

    public async Task<List<ReleaseCandidate>> SearchReleasesAsync(string artist, string album, CancellationToken ct)
    {
        var list = new List<ReleaseCandidate>();
        if (string.IsNullOrWhiteSpace(album) && string.IsNullOrWhiteSpace(artist)) return list;
        var q = string.IsNullOrWhiteSpace(album) ? "" : "release:" + Esc(album);
        if (!string.IsNullOrWhiteSpace(artist)) q += (q.Length > 0 ? " AND " : "") + "artist:" + Esc(artist);
        var json = await GetMusicBrainzAsync(
            "https://musicbrainz.org/ws/2/release?fmt=json&limit=20&query=" + Uri.EscapeDataString(q), ct);
        if (json == null) return list;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("releases", out var rels)) return list;
        foreach (var rel in rels.EnumerateArray())
        {
            var c = new ReleaseCandidate
            {
                Id = Str(rel, "id"),
                Title = Str(rel, "title"),
                Artist = Credit(rel),
                Year = YearOf(Str(rel, "date")),
                TrackCount = Int(rel, "track-count"),
                Country = Str(rel, "country"),
                Score = Int(rel, "score"),
            };
            if (rel.TryGetProperty("release-group", out var rg))
            {
                c.ReleaseGroupId = Str(rg, "id");
                c.Type = Str(rg, "primary-type");
            }
            if (rel.TryGetProperty("media", out var media) && media.GetArrayLength() > 0)
            {
                var formats = media.EnumerateArray().Select(m => Str(m, "format")).Where(f => f.Length > 0).Distinct().ToList();
                c.Format = (media.GetArrayLength() > 1 ? $"{media.GetArrayLength()}× " : "") + string.Join(" + ", formats);
            }
            list.Add(c);
        }
        return list;
    }

    public async Task<List<ReleaseTrack>> GetReleaseTracksAsync(string releaseId, CancellationToken ct)
    {
        var list = new List<ReleaseTrack>();
        var json = await GetMusicBrainzAsync(
            $"https://musicbrainz.org/ws/2/release/{releaseId}?fmt=json&inc=recordings+artist-credits", ct);
        if (json == null) return list;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var releaseArtist = Credit(root);
        if (!root.TryGetProperty("media", out var media)) return list;
        int overall = 0;
        foreach (var m in media.EnumerateArray())
        {
            var disc = Int(m, "position");
            if (!m.TryGetProperty("tracks", out var tracks)) continue;
            foreach (var t in tracks.EnumerateArray())
            {
                overall++;
                var artist = Credit(t);
                list.Add(new ReleaseTrack
                {
                    Disc = disc == 0 ? 1 : disc,
                    Number = uint.TryParse(Str(t, "number"), out var n) ? n : (uint)Int(t, "position"),
                    OverallIndex = overall,
                    Title = Str(t, "title"),
                    Artist = artist.Length > 0 ? artist : releaseArtist,
                    LengthSeconds = Int(t, "length") / 1000.0,
                });
            }
        }
        return list;
    }

    // ---------------- covers ----------------

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
