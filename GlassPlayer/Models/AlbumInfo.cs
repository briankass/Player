using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GlassPlayer.Models;

public class AlbumInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Artist { get; set; } = "";
    public uint Year { get; set; }
    public List<Track> Tracks { get; set; } = new();
    public DateTime LatestAdded { get; set; }

    public string Subtitle => Year > 0 ? $"{Artist} · {Year}" : Artist;

    public string Details
    {
        get
        {
            var total = Tracks.Sum(t => t.DurationSeconds);
            var span = TimeSpan.FromSeconds(total);
            var len = span.TotalHours >= 1 ? $"{(int)span.TotalHours} hr {span.Minutes} min" : $"{span.Minutes} min";
            return $"{Tracks.Count} songs · {len}";
        }
    }

    private string? _artPath;
    private ImageSource? _art;
    private ImageSource? _largeArt;

    public string? ArtPath
    {
        get => _artPath;
        set
        {
            _artPath = value;
            _art = null;
            _largeArt = null;
            Changed();
            Changed(nameof(Art));
            Changed(nameof(LargeArt));
        }
    }

    /// <summary>Thumbnail-sized art for grids.</summary>
    public ImageSource? Art =>
        _art ??= ArtPath == null ? null : new BitmapImage(new Uri(ArtPath)) { DecodePixelWidth = 320 };

    /// <summary>Full-size art for headers and Now Playing.</summary>
    public ImageSource? LargeArt =>
        _largeArt ??= ArtPath == null ? null : new BitmapImage(new Uri(ArtPath));
}

public class ArtistInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; set; } = "";
    public List<AlbumInfo> Albums { get; set; } = new();

    public string Subtitle => Albums.Count == 1 ? "1 album" : $"{Albums.Count} albums";
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public ImageSource? Art => Albums.Select(a => a.Art).FirstOrDefault(a => a != null);

    public void NotifyArt() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Art)));
}
