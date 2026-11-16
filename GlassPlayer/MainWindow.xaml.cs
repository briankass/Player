using System.Runtime.InteropServices.WindowsRuntime;
using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace GlassPlayer;

public sealed partial class MainWindow : Window
{
    private readonly LibraryService _library = new();
    private readonly LyricsService _lyrics = new();
    private readonly UpdateService _updater = new();
    private readonly MediaPlayer _player = new();
    private readonly DispatcherQueueTimer _timer;

    private List<Track> _visible = new();
    private int _index = -1;
    private List<LyricLine> _lyricLines = new();
    private bool _lyricsSynced;
    private bool _settingSlider;
    private CancellationTokenSource? _lyricsCts;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Glass Player";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 780));

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        _player.AutoPlay = false;
        _player.Volume = 0.8;
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() => PlayRelative(1));
        _player.MediaFailed += (_, e) => DispatcherQueue.TryEnqueue(() => NowArtist.Text = "Playback failed: " + e.ErrorMessage);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => UpdatePosition();
        _timer.Start();

        _library.LoadCache();
        SetVisible(_library.Tracks);
        Closed += (_, _) => _player.Dispose();
        Root.Loaded += async (_, _) => await CheckForUpdatesAsync(silent: true);
    }

    // ---------- library ----------

    private void SetVisible(List<Track> tracks)
    {
        _visible = tracks;
        TrackList.ItemsSource = _visible;
    }

    private async void FolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        FolderButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(n => FolderButton.Content = $"Scanning… {n}");
            await _library.ScanAsync(folder.Path, progress, CancellationToken.None);
            SetVisible(_library.Tracks);
        }
        catch (Exception ex)
        {
            await ShowMessage("Scan failed", ex.Message);
        }
        finally
        {
            FolderButton.Content = "Choose music folder…";
            FolderButton.IsEnabled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) { SetVisible(_library.Tracks); return; }
        SetVisible(_library.Tracks.Where(t =>
            t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            t.Artist.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            t.Album.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    // ---------- playback ----------

    private void TrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track t) _ = PlayAsync(_visible.IndexOf(t));
    }

    private async Task PlayAsync(int index)
    {
        if (index < 0 || index >= _visible.Count) return;
        _index = index;
        var track = _visible[index];
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(track.Path);
            _player.Source = MediaSource.CreateFromStorageFile(file);
            _player.Play();
        }
        catch (Exception ex)
        {
            NowArtist.Text = "Can't open file: " + ex.Message;
            return;
        }

        NowTitle.Text = track.Title;
        NowArtist.Text = track.Subtitle;
        PlayButton.Content = "Pause";
        DurText.Text = track.DurationText;
        TrackList.SelectedIndex = index;
        TrackList.ScrollIntoView(track);
        _ = LoadArtAsync(track);
        _ = LoadLyricsAsync(track);
    }

    private void PlayRelative(int delta)
    {
        if (_visible.Count == 0) return;
        var next = _index + delta;
        if (next < 0) next = 0;
        if (next >= _visible.Count) { _player.Pause(); PlayButton.Content = "Play"; return; }
        _ = PlayAsync(next);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player.Source == null) { if (_visible.Count > 0) _ = PlayAsync(Math.Max(_index, 0)); return; }
        var state = _player.PlaybackSession.PlaybackState;
        if (state == MediaPlaybackState.Playing) { _player.Pause(); PlayButton.Content = "Play"; }
        else { _player.Play(); PlayButton.Content = "Pause"; }
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => PlayRelative(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => PlayRelative(1);

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => _player.Volume = e.NewValue / 100.0;

    private void SeekSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_settingSlider || _player.Source == null) return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void UpdatePosition()
    {
        if (_player.Source == null) return;
        var session = _player.PlaybackSession;
        var dur = session.NaturalDuration.TotalSeconds;
        var pos = session.Position;

        _settingSlider = true;
        if (dur > 0) { SeekSlider.Maximum = dur; SeekSlider.Value = pos.TotalSeconds; }
        _settingSlider = false;

        PosText.Text = pos.ToString(pos.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
        if (dur > 0) DurText.Text = TimeSpan.FromSeconds(dur).ToString(dur >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
        HighlightLyric(pos);
    }

    // ---------- art + lyrics ----------

    private async Task LoadArtAsync(Track track)
    {
        ArtImage.Source = null;
        if (!track.HasArt) return;
        try
        {
            var bytes = await Task.Run(() =>
            {
                using var tl = TagLib.File.Create(track.Path);
                return tl.Tag.Pictures.Length > 0 ? tl.Tag.Pictures[0].Data.Data : null;
            });
            if (bytes == null) return;
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            ArtImage.Source = bmp;
        }
        catch { }
    }

    private async Task LoadLyricsAsync(Track track)
    {
        _lyricsCts?.Cancel();
        _lyricsCts = new CancellationTokenSource();
        var ct = _lyricsCts.Token;

        _lyricLines = new();
        LyricsList.ItemsSource = null;
        LyricsSource.Text = "Looking for lyrics…";
        try
        {
            var result = await _lyrics.GetLyricsAsync(track, ct);
            if (ct.IsCancellationRequested) return;
            if (result == null || result.Lines.Count == 0)
            {
                LyricsSource.Text = "No lyrics found";
                return;
            }
            _lyricLines = result.Lines;
            _lyricsSynced = result.Synced;
            LyricsList.ItemsSource = _lyricLines;
            LyricsSource.Text = $"Lyrics: {result.Source}" + (result.Synced ? " (synced)" : "");
        }
        catch (OperationCanceledException) { }
    }

    private void HighlightLyric(TimeSpan pos)
    {
        if (!_lyricsSynced || _lyricLines.Count == 0) return;
        int idx = -1;
        for (int i = 0; i < _lyricLines.Count; i++)
        {
            if (_lyricLines[i].Time <= pos) idx = i; else break;
        }
        if (idx >= 0 && LyricsList.SelectedIndex != idx)
        {
            LyricsList.SelectedIndex = idx;
            LyricsList.ScrollIntoView(_lyricLines[idx], ScrollIntoViewAlignment.Leading);
        }
    }

    private void LyricsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_lyricsSynced && e.ClickedItem is LyricLine l && _player.Source != null)
            _player.PlaybackSession.Position = l.Time;
    }

    // ---------- glass ----------

    private void BackdropBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SystemBackdrop = BackdropBox.SelectedIndex switch
        {
            1 => new DesktopAcrylicBackdrop(),
            2 => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            _ => new MicaBackdrop(),
        };
    }

    // ---------- metadata fixer ----------

    private MetadataWindow? _metaWindow;

    private void MetadataButton_Click(object sender, RoutedEventArgs e)
    {
        if (_metaWindow != null) { _metaWindow.Activate(); return; }
        _metaWindow = new MetadataWindow(_library, OnLibraryChanged, IsInUse);
        _metaWindow.Closed += (_, _) => _metaWindow = null;
        _metaWindow.Activate();
    }

    private bool IsInUse(string path) =>
        _player.Source != null && _index >= 0 && _index < _visible.Count
        && _visible[_index].Path.Equals(path, StringComparison.OrdinalIgnoreCase);

    private void OnLibraryChanged()
    {
        var current = _index >= 0 && _index < _visible.Count ? _visible[_index].Path : null;
        SearchBox_TextChanged(SearchBox, null!);
        if (current != null)
            _index = _visible.FindIndex(t => t.Path.Equals(current, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- updates ----------

    private async void UpdateButton_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(silent: false);

    /// <summary>silent: used at startup — only speaks up if an update exists, ignores errors.</summary>
    private async Task CheckForUpdatesAsync(bool silent)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            var info = await _updater.CheckAsync();
            if (info == null || !_updater.IsNewer(info))
            {
                if (!silent)
                    await ShowMessage("Up to date", $"You're on the latest version ({UpdateService.CurrentVersion}).");
                return;
            }

            var dlg = new ContentDialog
            {
                Title = $"Version {info.Tag} is available",
                Content = $"You have {UpdateService.CurrentVersion}. Download and install now? The app will restart.",
                PrimaryButtonText = "Update now",
                CloseButtonText = "Later",
                XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            var progress = new Progress<double>(p => UpdateButton.Content = $"Downloading {p:P0}");
            if (await _updater.DownloadAndApplyAsync(info, progress))
                Application.Current.Exit();
        }
        catch (Exception ex)
        {
            if (!silent) await ShowMessage("Update failed", ex.Message);
        }
        finally
        {
            UpdateButton.Content = "Check for updates";
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task ShowMessage(string title, string text)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = text,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot,
        };
        await dlg.ShowAsync();
    }
}
