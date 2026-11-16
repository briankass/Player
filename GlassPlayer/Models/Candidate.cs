using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GlassPlayer.Models;

/// <summary>One search result row in the match picker.</summary>
public class Candidate
{
    public string Line1 { get; set; } = "";
    public string Line2 { get; set; } = "";
    public string Line3 { get; set; } = "";
    public string? CoverUrl { get; set; }
    public object? Payload { get; set; }

    private ImageSource? _cover;
    public ImageSource? Cover =>
        _cover ??= CoverUrl == null ? null : new BitmapImage(new Uri(CoverUrl)) { DecodePixelWidth = 112 };
}
