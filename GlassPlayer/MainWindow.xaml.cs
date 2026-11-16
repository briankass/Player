using GlassPlayer.Pages;
using GlassPlayer.Services;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace GlassPlayer;

public sealed partial class MainWindow : Window
{
    public static MainWindow Current { get; private set; } = null!;

    private readonly PlayerService _ps = PlayerService.Instance;
    private readonly UpdateService _updater = new();
    private readonly DispatcherQueueTimer _timer;
    private bool _settingSlider;
    private MetadataWindow? _metaWindow;

    public MainWindow()
    {
        Current = this;
        InitializeComponent();
        Title = "Glass Player";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1300, 860));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        _ps.Init(DispatcherQueue);
        ApplyBackdrop(_ps.Settings.Backdrop);
        VolumeSlider.Value = _ps.Settings.Volume * 100;

        _ps.TrackChanged += OnTrackChanged;
        _ps.PlaybackStateChanged += OnPlaybackStateChanged;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => UpdatePosition();
        _timer.Start();

        ContentFrame.Navigate(typeof(HomePage));

        Root.Loaded += async (_, _) =>
        {
            await CheckForUpdatesAsync(silent: true);
            // pick up new files on the NAS in the background
            if (!string.IsNullOrEmpty(_ps.Settings.LibraryFolder) && Directory.Exists(_ps.Settings.LibraryFolder))
                await _ps.ScanAsync(_ps.Settings.LibraryFolder);
        };
        Closed += (_, _) =>
        {
            _ps.SaveSettings();
            _ps.Player.Dispose();
            _metaWindow?.Close();
        };
    }

    // ---------------- navigation ----------------

    public void NavigateTo(Type page, object? parameter = null)
    {
        if (ContentFrame.CurrentSourcePageType == page && parameter == null) return;
        ContentFrame.Navigate(page, parameter);
    }

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked) { NavigateTo(typeof(SettingsPage)); return; }
        switch ((args.InvokedItemContainer as NavigationViewItem)?.Tag as string)
        {
            case "home": NavigateTo(typeof(HomePage)); break;
            case "library": NavigateTo(typeof(LibraryPage)); break;
            case "nowplaying": NavigateTo(typeof(NowPlayingPage)); break;
            case "queue": NavigateTo(typeof(QueuePage)); break;
            case "metadata": OpenMetadataFixer(); break;
        }
    }

    private void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        Nav.IsBackEnabled = ContentFrame.CanGoBack;
        var page = e.SourcePageType;
        if (page == typeof(SettingsPage)) { Nav.SelectedItem = Nav.SettingsItem; return; }
        var tag = page == typeof(HomePage) ? "home"
            : page == typeof(NowPlayingPage) ? "nowplaying"
            : page == typeof(QueuePage) ? "queue"
            : "library"; // library, album, artist pages
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
            if ((item.Tag as string) == tag) { Nav.SelectedItem = item; break; }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var q = args.QueryText.Trim();
        ContentFrame.Navigate(typeof(LibraryPage), q.Length == 0 ? null : q);
    }

    public void OpenMetadataFixer()
    {
        if (_metaWindow != null) { _metaWindow.Activate(); return; }
        _metaWindow = new MetadataWindow(_ps.Library, paths => _ps.OnTagsChanged(paths), _ps.IsCurrentPath);
        _metaWindow.Closed += (_, _) => _metaWindow = null;
        _metaWindow.Activate();
    }

    // ---------------- player bar ----------------

    private async void OnTrackChanged()
    {
        var t = _ps.Current;
        if (t == null) return;
        BarTitle.Text = t.Title;
        BarArtist.Text = t.Artist;
        DurText.Text = t.DurationText;
        BarArt.Source = null;
        var path = await _ps.GetArtPathAsync(t);
        if (path != null && ReferenceEquals(t, _ps.Current))
            BarArt.Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 128 };
    }

    private void OnPlaybackStateChanged()
    {
        PlayIcon.Glyph = _ps.IsPlaying ? "" : "";
        ShuffleButton.IsChecked = _ps.Shuffle;
        RepeatButton.IsChecked = _ps.Repeat != RepeatMode.Off;
        RepeatIcon.Glyph = _ps.Repeat == RepeatMode.One ? "" : "";
        if (!string.IsNullOrEmpty(_ps.LastError)) BarArtist.Text = _ps.LastError;
    }

    private void UpdatePosition()
    {
        if (_ps.Player.Source == null) return;
        var session = _ps.Player.PlaybackSession;
        var dur = session.NaturalDuration.TotalSeconds;
        var pos = session.Position.TotalSeconds;

        _settingSlider = true;
        if (dur > 0) { SeekSlider.Maximum = dur; SeekSlider.Value = Math.Min(pos, dur); }
        _settingSlider = false;

        PosText.Text = Models.Track.FormatTime(pos);
        if (dur > 0) DurText.Text = Models.Track.FormatTime(dur);
    }

    private void SeekSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_settingSlider) return;
        _ps.Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _ps.SetVolume(e.NewValue / 100.0);
        VolumeIcon.Glyph = e.NewValue == 0 ? "" : e.NewValue < 33 ? "" : e.NewValue < 66 ? "" : "";
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => _ps.TogglePlay();
    private void Prev_Click(object sender, RoutedEventArgs e) => _ps.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => _ps.Next();
    private void Shuffle_Click(object sender, RoutedEventArgs e) => _ps.ToggleShuffle();
    private void Repeat_Click(object sender, RoutedEventArgs e) => _ps.CycleRepeat();
    private void NowPlayingButton_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(NowPlayingPage));
    private void Lyrics_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(NowPlayingPage));
    private void Queue_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(QueuePage));

    // ---------------- appearance ----------------

    public void ApplyBackdrop(int kind)
    {
        SystemBackdrop = kind switch
        {
            1 => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            2 => new DesktopAcrylicBackdrop(),
            _ => new MicaBackdrop(),
        };
        _ps.Settings.Backdrop = kind;
        _ps.Settings.Save();
    }

    // ---------------- library folder ----------------

    public async Task PickLibraryFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) await _ps.ScanAsync(folder.Path);
    }

    // ---------------- updates ----------------

    /// <summary>silent: used at startup. Only speaks up if an update exists; ignores errors.</summary>
    public async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var info = await _updater.CheckAsync();
            if (info == null || !_updater.IsNewer(info))
            {
                if (!silent) await ShowMessage("You're up to date", $"Glass Player {UpdateService.CurrentVersion} is the latest version.");
                return;
            }

            var dlg = new ContentDialog
            {
                Title = $"Version {info.Tag} is available",
                Content = $"You have {UpdateService.CurrentVersion}. Download and install now? The app will restart.",
                PrimaryButtonText = "Update now",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            UpdateBar.Title = "Downloading update";
            UpdateBar.Message = "";
            UpdateBar.IsClosable = false;
            UpdateBar.IsOpen = true;
            var progress = new Progress<double>(p => UpdateBar.Message = p.ToString("P0"));
            if (await _updater.DownloadAndApplyAsync(info, progress))
            {
                _ps.SaveSettings();
                Application.Current.Exit();
            }
        }
        catch (Exception ex)
        {
            UpdateBar.IsOpen = false;
            if (!silent) await ShowMessage("Update failed", ex.Message);
        }
    }

    public async Task ShowMessage(string title, string text)
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
