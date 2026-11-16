using GlassPlayer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GlassPlayer;

/// <summary>Small always-on-top player. Closing it brings the main window back.</summary>
public sealed partial class MiniPlayerWindow : Window
{
    private readonly PlayerService _ps = PlayerService.Instance;
    private readonly DispatcherQueueTimer _timer;
    private bool _settingSlider;

    public MiniPlayerWindow()
    {
        InitializeComponent();
        Title = "Glass Player";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragBar);
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "GlassPlayer.ico")); } catch { }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 190));
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            op.IsAlwaysOnTop = true;
            op.IsResizable = false;
            op.IsMaximizable = false;
        }

        _ps.TrackChanged += OnTrackChanged;
        _ps.PlaybackStateChanged += Refresh;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(300);
        _timer.Tick += (_, _) => UpdatePosition();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _ps.TrackChanged -= OnTrackChanged;
            _ps.PlaybackStateChanged -= Refresh;
        };

        OnTrackChanged();
    }

    public void Refresh() => PlayIcon.Glyph = _ps.IsPlaying ? "" : "";

    private async void OnTrackChanged()
    {
        Refresh();
        var t = _ps.Current;
        if (t == null) return;
        TitleText.Text = t.Title;
        ArtistText.Text = t.Artist;
        ArtImage.Source = null;
        BgArt.Source = null;
        var art = await _ps.GetArtPathAsync(t);
        if (art != null && ReferenceEquals(t, _ps.Current))
        {
            ArtImage.Source = new BitmapImage(new Uri(art)) { DecodePixelWidth = 220 };
            BgArt.Source = new BitmapImage(new Uri(art)) { DecodePixelWidth = 64 };
        }
    }

    private void UpdatePosition()
    {
        if (!_ps.HasMedia) return;
        var dur = _ps.Duration.TotalSeconds;
        if (dur <= 0) return;
        _settingSlider = true;
        SeekSlider.Maximum = dur;
        SeekSlider.Value = Math.Min(_ps.Position.TotalSeconds, dur);
        _settingSlider = false;
    }

    private void SeekSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_settingSlider) _ps.Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    private void Play_Click(object sender, RoutedEventArgs e) => _ps.TogglePlay();
    private void Prev_Click(object sender, RoutedEventArgs e) => _ps.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => _ps.Next();
    private void Expand_Click(object sender, RoutedEventArgs e) => Close();

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter op) op.IsAlwaysOnTop = PinButton.IsChecked == true;
    }
}
