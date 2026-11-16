using System.Text.Json.Serialization;

namespace GlassPlayer.Models;

public class Track
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "";
    public uint Year { get; set; }
    public uint TrackNumber { get; set; }
    public double DurationSeconds { get; set; }
    public bool HasArt { get; set; }
    public DateTime AddedUtc { get; set; }

    [JsonIgnore]
    public string DurationText => FormatTime(DurationSeconds);

    [JsonIgnore]
    public string Subtitle => string.IsNullOrWhiteSpace(Album) ? Artist : $"{Artist} — {Album}";

    [JsonIgnore]
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "Unknown album" : Album;

    [JsonIgnore]
    public string GroupArtist => string.IsNullOrWhiteSpace(AlbumArtist) ? Artist : AlbumArtist;

    [JsonIgnore]
    public string AlbumKey => (GroupArtist + "|" + Album).ToLowerInvariant();

    [JsonIgnore]
    public string TrackNumberText => TrackNumber > 0 ? TrackNumber.ToString() : "";

    public static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
