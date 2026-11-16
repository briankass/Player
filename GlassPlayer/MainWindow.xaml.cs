using GlassPlayer.Pages;
using GlassPlayer.Services;
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
    private int _ticks;
    private bool _fullscreen;
    private MetadataWindow? _metaWindow;

    public MainWindow()
    {
        Current = this;
        InitializeComponent();
        Title = "Glass Player";
        WindowHelper.ResizeLogical(this, 1300, 860);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        _ps.Init(DispatcherQueue);
        SetupWindowChrome();
        PlayerBar.Background = _barBrush;
        ApplyGlass();
        Root.ActualThemeChanged += (_, _) => ApplyGlass();
        VolumeSlider.Value = _ps.Settings.Volume * 100;
        _volumeReady = true;

        _ps.TrackChanged += OnTrackChanged;
        _ps.PlaybackStateChanged += OnPlaybackStateChanged;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) =>
        {
            UpdatePosition();
            if (++_ticks % 20 == 0 && _ps.IsPlaying) _ps.SaveVideoPosition(); // every ~5 s
        };
        VideoElement.SetMediaPlayer(_ps.Player);
        _timer.Start();

        ContentFrame.Navigate(typeof(HomePage));

        Root.Loaded += async (_, _) =>
        {
            if (UpdateService.TakeFailedUpdate() is string failed)
                await ShowFailedUpdateAsync(failed); // don't immediately offer the same update again
            else
                await CheckForUpdatesAsync(silent: true);
            // pick up new files on the NAS in the background
            if (_ps.Settings.MusicFolders.Count > 0 || _ps.Settings.VideoFolders.Count > 0)
                await _ps.ScanAsync();
        };
        Closed += (_, _) =>
        {
            _exiting = true;
            _ps.SaveSettings();
            _tray?.Dispose();
            _mini?.Close();
            _metaWindow?.Close();
            _ps.Shutdown();
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
            case "videos": NavigateTo(typeof(VideoLibraryPage)); break;
            case "nowplaying": OpenNowPlaying(); break;
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
        VideoHost.Visibility = page == typeof(VideoPlayerPage) ? Visibility.Visible : Visibility.Collapsed;
        if (page != typeof(VideoPlayerPage) && _fullscreen) SetFullscreen(false);
        if (page == typeof(SettingsPage)) { Nav.SelectedItem = Nav.SettingsItem; return; }
        var tag = page == typeof(HomePage) ? "home"
            : page == typeof(NowPlayingPage) || page == typeof(VideoPlayerPage) ? "nowplaying"
            : page == typeof(VideoLibraryPage) ? "videos"
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
        UpdateVideoInfo();
        if (t == null) return;
        _tray?.SetTooltip($"{t.Title} — {t.Artist}");
        BarTitle.Text = t.Title;
        BarArtist.Text = t.Artist;
        DurText.Text = t.DurationText;
        BarArt.Source = null;
        BarArtist.Text = t.IsVideo ? (t.Artist.Length > 0 ? t.Artist : "Video") : t.Artist;
        var path = await _ps.GetArtPathAsync(t);
        if (!ReferenceEquals(t, _ps.Current)) return;
        if (path != null) BarArt.Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 128 };
        await UpdateAlbumTintAsync(path);
    }

    private void OnPlaybackStateChanged()
    {
        UpdateVolumeIcon();
        _mini?.Refresh();
        PlayIcon.Glyph = _ps.IsPlaying ? "" : "";
        ShuffleButton.IsChecked = _ps.Shuffle;
        RepeatButton.IsChecked = _ps.Repeat != RepeatMode.Off;
        RepeatIcon.Glyph = _ps.Repeat == RepeatMode.One ? "" : "";
        if (!string.IsNullOrEmpty(_ps.LastError)) BarArtist.Text = _ps.LastError;
    }

    private void UpdatePosition()
    {
        if (!_ps.HasMedia) return;
        var dur = _ps.Duration.TotalSeconds;
        var pos = _ps.Position.TotalSeconds;

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
        if (_volumeReady && _ps.IsMuted && e.NewValue > 0) _ps.SetMuted(false); // moving the slider unmutes
        UpdateVolumeIcon();
    }

    private void UpdateVolumeIcon()
    {
        var v = VolumeSlider.Value;
        VolumeIcon.Glyph = _ps.IsMuted || v == 0 ? "\uE74F" : v < 33 ? "\uE993" : v < 66 ? "\uE994" : "\uE767";
        ToolTipService.SetToolTip(VolumeButton, _ps.IsMuted ? "Muted (right-click to unmute)" : "Volume (right-click to mute)");
    }

    private void VolumeButton_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        _ps.ToggleMute();
        e.Handled = true;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => _ps.TogglePlay();
    private void Prev_Click(object sender, RoutedEventArgs e) => _ps.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => _ps.Next();
    private void Shuffle_Click(object sender, RoutedEventArgs e) => _ps.ToggleShuffle();
    private void Repeat_Click(object sender, RoutedEventArgs e) => _ps.CycleRepeat();
    private void NowPlayingButton_Click(object sender, RoutedEventArgs e) => OpenNowPlaying();

    public void OpenNowPlaying() =>
        NavigateTo(_ps.Current?.IsVideo == true ? typeof(VideoPlayerPage) : typeof(NowPlayingPage));

    // ---------------- video ----------------

    private void UpdateVideoInfo()
    {
        var t = _ps.Current;
        bool video = t?.IsVideo == true;
        NoVideoText.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        VideoElement.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        VideoTitle.Text = video ? t!.Title : "";
    }

    private void VideoHost_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) =>
        SetFullscreen(!_fullscreen);

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void Fullscreen_Accelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (VideoHost.Visibility == Visibility.Visible) { SetFullscreen(!_fullscreen); args.Handled = true; }
    }

    private void ExitFullscreen_Accelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_fullscreen) { SetFullscreen(false); args.Handled = true; }
    }

    private void SetFullscreen(bool on)
    {
        _fullscreen = on;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        Root.RowDefinitions[0].Height = on ? new GridLength(0) : new GridLength(48);
        AppTitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        PlayerBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Nav.IsPaneVisible = !on;
        Nav.IsBackButtonVisible = on ? NavigationViewBackButtonVisible.Collapsed : NavigationViewBackButtonVisible.Visible;
        UpdateBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        // in full screen, use the built-in auto-hiding controls
        VideoElement.AreTransportControlsEnabled = on;
        if (on)
        {
            VideoElement.TransportControls.IsZoomButtonVisible = false;
            VideoElement.TransportControls.IsCompact = false;
        }
        FullscreenIcon.Glyph = on ? "\uE73F" : "\uE740";
    }

    private void Subtitles_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        var tracks = _ps.SubtitleTracks();
        var off = new ToggleMenuFlyoutItem { Text = "Off" };
        off.Click += (_, _) => _ps.SetSubtitle(null);
        menu.Items.Add(off);
        if (tracks.Count == 0)
            menu.Items.Add(new MenuFlyoutItem { Text = "No subtitles found", IsEnabled = false });
        foreach (var (index, label) in tracks)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => _ps.SetSubtitle(index);
            menu.Items.Add(item);
        }
        menu.ShowAt(SubtitleButton);
    }
    private void Lyrics_Click(object sender, RoutedEventArgs e) => OpenNowPlaying();
    private void Queue_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(QueuePage));

    // ---------------- tray, startup, mini player ----------------

    private TrayIcon? _tray;
    private MiniPlayerWindow? _mini;
    private bool _exiting;
    private bool _volumeReady;
    private static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "GlassPlayer.ico");

    private void SetupWindowChrome()
    {
        try { AppWindow.SetIcon(IconPath); } catch { }
        StartupService.Refresh();

        _tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), IconPath);
        _tray.IsPlaying = () => _ps.IsPlaying;
        _tray.Activated += ShowFromTray;
        _tray.Command += cmd =>
        {
            switch (cmd)
            {
                case TrayIcon.CmdShow: ShowFromTray(); break;
                case TrayIcon.CmdPlayPause: _ps.TogglePlay(); break;
                case TrayIcon.CmdNext: _ps.Next(); break;
                case TrayIcon.CmdPrevious: _ps.Previous(); break;
                case TrayIcon.CmdExit: ExitApp(); break;
            }
        };
        UpdateTray();

        AppWindow.Changed += (_, _) =>
        {
            if (_ps.Settings.MinimizeToTray && !_exiting && AppWindow.IsVisible
                && AppWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Minimized)
                HideToTray();
        };
        AppWindow.Closing += (_, e) =>
        {
            if (_ps.Settings.CloseToTray && !_exiting)
            {
                e.Cancel = true;
                HideToTray();
            }
        };
    }

    /// <summary>Tray icon is shown while a tray option is on (and always while the window is hidden).</summary>
    public void UpdateTray()
    {
        if (_tray == null) return;
        _tray.Visible = _ps.Settings.MinimizeToTray || _ps.Settings.CloseToTray;
    }

    private void HideToTray()
    {
        if (_tray != null) _tray.Visible = true;
        AppWindow.Hide();
    }

    /// <summary>Started by "Start with Windows": go straight to the tray (or taskbar).</summary>
    public void StartHidden()
    {
        Activate();
        if (_ps.Settings.MinimizeToTray || _ps.Settings.CloseToTray) HideToTray();
        else if (AppWindow.Presenter is OverlappedPresenter op) op.Minimize();
    }

    public void ShowFromTray()
    {
        if (_mini != null) { _mini.Activate(); return; }
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Minimized) op.Restore();
        Activate();
        UpdateTray();
    }

    public void ExitApp()
    {
        _exiting = true;
        _mini?.Close();
        Close();
    }

    private void MiniPlayer_Click(object sender, RoutedEventArgs e) => OpenMiniPlayer();

    public void OpenMiniPlayer()
    {
        if (_fullscreen) SetFullscreen(false);
        if (_mini == null)
        {
            _mini = new MiniPlayerWindow();
            _mini.Closed += (_, _) =>
            {
                _mini = null;
                if (!_exiting) ShowFromTray();
            };
        }
        _mini.Activate();
        AppWindow.Hide();
    }

    // ---------------- appearance ----------------

    private GlassBackdrop? _glass;
    private SeeThroughBackdrop? _seeThrough;

    private Windows.UI.Color CurrentTint(GlassSettings g) => g.TintMode switch
    {
        1 => _albumTint ?? DefaultTint,
        2 => ColorUtil.ParseHex(g.CustomTint) ?? DefaultTint,
        _ => DefaultTint,
    };
    private readonly SolidColorBrush _barBrush = new(Windows.UI.Color.FromArgb(255, 28, 28, 28));
    private Windows.UI.Color? _albumTint;

    private bool IsDark => Root.ActualTheme != ElementTheme.Light;

    private Windows.UI.Color DefaultTint => IsDark
        ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
        : Windows.UI.Color.FromArgb(255, 243, 243, 243);

    /// <summary>Apply Settings.Glass to the window. Cheap enough to call on every slider tick.</summary>
    public void ApplyGlass()
    {
        var g = _ps.Settings.Glass;
        TintLayer.Fill = null;

        if (g.Effect is 5 or 6)
        {
            bool blur = g.Effect == 5;
            _glass = null;
            if (_seeThrough == null || _seeThrough.Blur != blur)
            {
                _seeThrough = new SeeThroughBackdrop(blur, WinRT.Interop.WindowNative.GetWindowHandle(this));
                SystemBackdrop = _seeThrough;
            }
            // frosting: neutral wash; never fully 0 so the window still catches mouse clicks
            Root.Background = new SolidColorBrush(DefaultTint) { Opacity = Math.Max(0.02, g.Luminosity * 0.9) };
            TintLayer.Fill = new SolidColorBrush(CurrentTint(g)) { Opacity = g.TintOpacity };
        }
        else if (g.Effect == 4)
        {
            SystemBackdrop = null;
            _glass = null;
            _seeThrough = null;
            Root.Background = new SolidColorBrush(IsDark
                ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        }
        else
        {
            Root.Background = null;
            _seeThrough = null;
            if (_glass == null || _glass.Effect != g.Effect || _glass.KeepActive != g.KeepActive)
            {
                _glass = new GlassBackdrop(g.Effect, g.KeepActive);
                SystemBackdrop = _glass;
            }
            _glass.Update(CurrentTint(g), g.TintOpacity, g.Luminosity);
        }

        // panels on top of the glass: content area + player bar
        var surface = IsDark ? Windows.UI.Color.FromArgb(255, 28, 28, 28) : Windows.UI.Color.FromArgb(255, 251, 251, 251);
        _barBrush.Color = surface;
        _barBrush.Opacity = Math.Clamp(g.SurfaceOpacity + 0.1, 0, 1); // bar slightly more solid for legibility
        if (Nav.Resources.TryGetValue("NavigationViewContentBackground", out var res) && res is SolidColorBrush content)
        {
            content.Color = surface;
            content.Opacity = g.SurfaceOpacity;
        }
    }

    private async Task UpdateAlbumTintAsync(string? artPath)
    {
        var c = artPath == null ? null : await ColorUtil.FromImageAsync(artPath);
        if (c is Windows.UI.Color col && IsDark)
        {
            // keep it deep enough that white text stays readable
            col = Windows.UI.Color.FromArgb(255, (byte)(col.R * 0.75), (byte)(col.G * 0.75), (byte)(col.B * 0.75));
            c = col;
        }
        _albumTint = c;
        if (_ps.Settings.Glass.TintMode == 1) ApplyGlass();
    }

    // ---------------- library folder ----------------

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task AddLibraryFolderAsync(bool video)
    {
        var path = await PickFolderAsync();
        if (path != null) await _ps.AddFolderAsync(video, path);
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

    private async Task ShowFailedUpdateAsync(string version)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');
        var hint = appDir.Contains("Program Files", StringComparison.OrdinalIgnoreCase)
            ? "Glass Player is inside Program Files, which Windows protects. Move the whole folder to somewhere like C:\\Apps\\GlassPlayer and updates will install without problems."
            : "Something blocked the files from being replaced (antivirus, or the folder is read-only or synced by OneDrive).";
        var dlg = new ContentDialog
        {
            Title = $"The update to {version} didn't install",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = $"You're still on {UpdateService.CurrentVersion}. {hint}", TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = $"Installed in: {appDir}", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 },
                        new TextBlock
                        {
                            Text = UpdateService.LogTail(),
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 11,
                            Opacity = 0.7,
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                        },
                    },
                },
            },
            PrimaryButtonText = "Try again",
            SecondaryButtonText = "Open log",
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await CheckForUpdatesAsync(silent: false);
        else if (result == ContentDialogResult.Secondary)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateService.LogPath) { UseShellExecute = true }); } catch { }
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
