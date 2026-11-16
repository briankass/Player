namespace GlassPlayer.Models;

public class Track
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public uint Year { get; set; }
    public uint TrackNumber { get; set; }
    public double DurationSeconds { get; set; }
    public bool HasArt { get; set; }

    public string DurationText =>
        TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    public string Subtitle => string.IsNullOrWhiteSpace(Album) ? Artist : $"{Artist} — {Album}";
}
