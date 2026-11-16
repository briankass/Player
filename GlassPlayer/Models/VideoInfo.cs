using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GlassPlayer.Models;

public class VideoInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Track Track { get; set; } = new();
    public string Title => Track.Title;
    public string Folder => Track.Artist;
    public string DurationText => Track.DurationSeconds > 0 ? Track.DurationText : "";
    public Visibility DurationVisibility => Track.DurationSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;

    private string? _thumbPath;
    private ImageSource? _thumb;
    public string? ThumbPath
    {
        get => _thumbPath;
        set { _thumbPath = value; _thumb = null; Changed(nameof(ThumbPath)); Changed(nameof(Thumb)); }
    }

    public ImageSource? Thumb =>
        _thumb ??= ThumbPath == null ? null : new BitmapImage(new Uri(ThumbPath)) { DecodePixelWidth = 400 };

    private double _progress;
    /// <summary>0..100, how much has been watched.</summary>
    public double Progress
    {
        get => _progress;
        set { _progress = value; Changed(nameof(Progress)); Changed(nameof(ProgressVisibility)); }
    }

    public Visibility ProgressVisibility => _progress > 0 ? Visibility.Visible : Visibility.Collapsed;
}
