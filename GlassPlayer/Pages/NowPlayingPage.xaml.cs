using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public sealed partial class NowPlayingPage : Page
{
    private readonly PlayerService _ps = PlayerService.Instance;
    private readonly LyricsService _lyrics = new();
    private DispatcherQueueTimer? _timer;
    private List<LyricLine> _lines = new();
    private bool _synced;
    private int _currentLine = -1;
    private CancellationTokenSource? _cts;

    public NowPlayingPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _ps.TrackChanged += Load;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) => Highlight();
        _timer.Start();
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ps.TrackChanged -= Load;
        _timer?.Stop();
        _cts?.Cancel();
    }

    private async void Load()
    {
        var t = _ps.Current;
        if (t == null)
        {
            ShowNoLyrics("Play something to see it here.");
            return;
        }

        TitleText.Text = t.Title;
        ArtistLink.Content = t.Artist;
        AlbumLink.Content = t.DisplayAlbum;
        CoverImage.Source = null;
        BgImage.Source = null;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var art = await _ps.GetArtPathAsync(t);
        if (ct.IsCancellationRequested) return;
        if (art != null)
        {
            CoverImage.Source = new BitmapImage(new Uri(art)) { DecodePixelWidth = 720 };
            BgImage.Source = new BitmapImage(new Uri(art)) { DecodePixelWidth = 200 };
        }

        _lines = new();
        _currentLine = -1;
        LyricsList.ItemsSource = null;
        LyricsSource.Text = "Looking for lyrics…";
        NoLyricsText.Visibility = Visibility.Collapsed;
        try
        {
            var result = await _lyrics.GetLyricsAsync(t, ct);
            if (ct.IsCancellationRequested) return;
            if (result == null || result.Lines.Count == 0)
            {
                ShowNoLyrics("No lyrics found for this song.");
                return;
            }
            _synced = result.Synced;
            _lines = result.Lines;
            foreach (var l in _lines) l.Opacity = _synced ? 0.4 : 0.9;
            LyricsList.ItemsSource = _lines;
            LyricsSource.Text = $"Lyrics from {result.Source}" + (_synced ? " · synced" : "");
        }
        catch (OperationCanceledException) { }
    }

    private void ShowNoLyrics(string text)
    {
        LyricsSource.Text = "Lyrics";
        LyricsList.ItemsSource = null;
        NoLyricsText.Text = text;
        NoLyricsText.Visibility = Visibility.Visible;
    }

    private void Highlight()
    {
        if (!_synced || _lines.Count == 0 || _ps.Player.Source == null) return;
        var pos = _ps.Player.PlaybackSession.Position;
        int idx = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Time <= pos) idx = i; else break;
        }
        if (idx == _currentLine) return;
        if (_currentLine >= 0 && _currentLine < _lines.Count) _lines[_currentLine].Opacity = 0.4;
        _currentLine = idx;
        if (idx < 0) return;
        _lines[idx].Opacity = 1.0;
        // keep a couple of lines of context above the current one
        LyricsList.ScrollIntoView(_lines[Math.Max(0, idx - 2)], ScrollIntoViewAlignment.Leading);
    }

    private void LyricsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_synced && e.ClickedItem is LyricLine l) _ps.Seek(l.Time);
    }

    private void ArtistLink_Click(object sender, RoutedEventArgs e)
    {
        if (_ps.Current is Track t) MainWindow.Current.NavigateTo(typeof(ArtistPage), t.GroupArtist);
    }

    private void AlbumLink_Click(object sender, RoutedEventArgs e)
    {
        if (_ps.Current is Track t) MainWindow.Current.NavigateTo(typeof(AlbumPage), t.AlbumKey);
    }
}
