using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public sealed partial class SettingsPage : Page
{
    private static readonly string[] Swatches =
        { "#202020", "#2B4F81", "#4B2C7F", "#7A2357", "#8A2E2E", "#8A5A1F", "#2E6B3F", "#1F6A6A" };

    private readonly PlayerService _ps = PlayerService.Instance;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        foreach (var hex in Swatches)
        {
            var color = ColorUtil.ParseHex(hex)!.Value;
            var b = new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)),
                Tag = hex,
            };
            ToolTipService.SetToolTip(b, hex);
            b.Click += Swatch_Click;
            SwatchPanel.Children.Add(b);
        }
    }

    private GlassSettings Glass => _ps.Settings.Glass;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        LoadGlassControls();
        LoadPlaybackControls();
        VersionText.Text = $"Glass Player {UpdateService.CurrentVersion}";
        _ps.ScanStatus += OnScanStatus;
        ShowFolders();
        ScanText.Text = _ps.IsScanning ? "Scanning…"
            : $"{_ps.Library.Tracks.Count} songs · {_ps.Albums.Count} albums · {_ps.Artists.Count} artists · {_ps.VideoLibrary.Tracks.Count} videos";
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ps.ScanStatus -= OnScanStatus;
        _ps.SaveSettings();
    }

    // ---------------- glass ----------------

    private void LoadGlassControls()
    {
        _loading = true;
        EffectBox.SelectedIndex = Math.Clamp(Glass.Effect, 0, 6);
        TintModeBox.SelectedIndex = Math.Clamp(Glass.TintMode, 0, 2);
        TintSlider.Value = Glass.TintOpacity * 100;
        LumSlider.Value = Glass.Luminosity * 100;
        SurfaceSlider.Value = Glass.SurfaceOpacity * 100;
        KeepActiveSwitch.IsOn = Glass.KeepActive;
        if (ColorUtil.ParseHex(Glass.CustomTint) is Windows.UI.Color c) TintPicker.Color = c;
        _loading = false;
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        TintValue.Text = $"{TintSlider.Value:0}%";
        LumValue.Text = $"{LumSlider.Value:0}%";
        SurfaceValue.Text = $"{SurfaceSlider.Value:0}%";
        var effect = EffectBox.SelectedIndex;
        bool glass = effect != 4;
        TintModeBox.IsEnabled = TintSlider.IsEnabled = LumSlider.IsEnabled = glass;
        KeepActiveSwitch.IsEnabled = effect <= 3; // Blur/Clear glass never switch off
    }

    private void SaveAndApply()
    {
        if (_loading || EffectBox == null || SurfaceSlider == null || KeepActiveSwitch == null) return;
        Glass.Effect = EffectBox.SelectedIndex;
        Glass.TintMode = TintModeBox.SelectedIndex;
        Glass.TintOpacity = TintSlider.Value / 100.0;
        Glass.Luminosity = LumSlider.Value / 100.0;
        Glass.SurfaceOpacity = SurfaceSlider.Value / 100.0;
        Glass.KeepActive = KeepActiveSwitch.IsOn;
        UpdateLabels();
        MainWindow.Current.ApplyGlass();
    }

    private void Glass_Changed(object sender, SelectionChangedEventArgs e) => SaveAndApply();
    private void Glass_Toggled(object sender, RoutedEventArgs e) => SaveAndApply();
    private void Slider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => SaveAndApply();

    private void UseCustomTint(Windows.UI.Color c)
    {
        Glass.CustomTint = ColorUtil.ToHex(c);
        if (TintModeBox.SelectedIndex != 2) TintModeBox.SelectedIndex = 2; // triggers SaveAndApply
        else SaveAndApply();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string hex && ColorUtil.ParseHex(hex) is Windows.UI.Color c)
        {
            _loading = true;
            TintPicker.Color = c;
            _loading = false;
            UseCustomTint(c);
        }
    }

    private void TintPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_loading) UseCustomTint(args.NewColor);
    }

    private void ResetGlass_Click(object sender, RoutedEventArgs e)
    {
        _ps.Settings.Glass = new GlassSettings();
        LoadGlassControls();
        MainWindow.Current.ApplyGlass();
        _ps.SaveSettings();
    }

    // ---------------- playback ----------------

    private PlaybackSettings Playback => _ps.Settings.Playback;
    private readonly List<Slider> _bandSliders = new();
    private bool _playbackLoading;

    private void BuildEqBands()
    {
        if (_bandSliders.Count > 0) return;
        for (int i = 0; i < AudioEngine.BandFrequencies.Length; i++)
        {
            EqBands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var f = AudioEngine.BandFrequencies[i];
            var col = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            var value = new TextBlock { FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center };
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Height = 150,
                Minimum = -12,
                Maximum = 12,
                StepFrequency = 0.5,
                TickFrequency = 6,
                TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.Outside,
                Tag = i,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            slider.ValueChanged += (_, e) =>
            {
                value.Text = $"{e.NewValue:+0.#;-0.#;0}";
                if (_playbackLoading) return;
                Playback.EqGains[(int)slider.Tag] = e.NewValue;
                Playback.EqPreset = "Custom";
                _playbackLoading = true;
                EqPresetBox.SelectedItem = "Custom";
                _playbackLoading = false;
                _ps.ApplyEqualizer();
            };
            col.Children.Add(value);
            col.Children.Add(slider);
            col.Children.Add(new TextBlock
            {
                Text = f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}",
                FontSize = 11,
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            Grid.SetColumn(col, i);
            EqBands.Children.Add(col);
            _bandSliders.Add(slider);
        }
    }

    private void LoadPlaybackControls()
    {
        BuildEqBands();
        _playbackLoading = true;
        if (Playback.EqGains.Length != 10) Playback.EqGains = new double[10];
        CrossfadeSwitch.IsOn = Playback.CrossfadeEnabled;
        CrossfadeSlider.Value = Playback.CrossfadeSeconds;
        CrossfadeValue.Text = $"{Playback.CrossfadeSeconds:0} s";
        CrossfadeSlider.IsEnabled = Playback.CrossfadeEnabled;
        var presets = PlaybackSettings.Presets.Keys.ToList();
        presets.Add("Custom");
        EqPresetBox.ItemsSource = presets;
        EqPresetBox.SelectedItem = presets.Contains(Playback.EqPreset) ? Playback.EqPreset : "Custom";
        EqSwitch.IsOn = Playback.EqEnabled;
        for (int i = 0; i < _bandSliders.Count; i++) _bandSliders[i].Value = Playback.EqGains[i];
        SetEqEnabledUi();

        StartupSwitch.IsOn = StartupService.IsEnabled;
        StartMinSwitch.IsOn = _ps.Settings.StartMinimized;
        MinTraySwitch.IsOn = _ps.Settings.MinimizeToTray;
        CloseTraySwitch.IsOn = _ps.Settings.CloseToTray;
        _playbackLoading = false;
    }

    private void SetEqEnabledUi()
    {
        EqPresetBox.IsEnabled = EqSwitch.IsOn;
        foreach (var s in _bandSliders) s.IsEnabled = EqSwitch.IsOn;
    }

    private void Crossfade_Changed(object sender, RoutedEventArgs e)
    {
        if (_playbackLoading) return;
        Playback.CrossfadeEnabled = CrossfadeSwitch.IsOn;
        CrossfadeSlider.IsEnabled = CrossfadeSwitch.IsOn;
        _ps.SaveSettings();
    }

    private void CrossfadeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (CrossfadeValue != null) CrossfadeValue.Text = $"{e.NewValue:0} s";
        if (_playbackLoading) return;
        Playback.CrossfadeSeconds = e.NewValue;
    }

    private void Eq_Toggled(object sender, RoutedEventArgs e)
    {
        if (_playbackLoading) return;
        Playback.EqEnabled = EqSwitch.IsOn;
        SetEqEnabledUi();
        _ps.ApplyEqualizer();
        _ps.SaveSettings();
    }

    private void EqPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_playbackLoading || EqPresetBox.SelectedItem is not string name) return;
        if (!PlaybackSettings.Presets.TryGetValue(name, out var gains)) return; // "Custom": keep sliders
        Playback.EqPreset = name;
        Playback.EqGains = gains.ToArray();
        _playbackLoading = true;
        for (int i = 0; i < _bandSliders.Count; i++) _bandSliders[i].Value = gains[i];
        _playbackLoading = false;
        _ps.ApplyEqualizer();
        _ps.SaveSettings();
    }

    private async void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_playbackLoading) return;
        try
        {
            StartupService.SetEnabled(StartupSwitch.IsOn);
        }
        catch (Exception ex)
        {
            _playbackLoading = true;
            StartupSwitch.IsOn = StartupService.IsEnabled;
            _playbackLoading = false;
            await MainWindow.Current.ShowMessage("Couldn't change startup setting", ex.Message);
        }
    }

    private void Tray_Toggled(object sender, RoutedEventArgs e)
    {
        if (_playbackLoading) return;
        _ps.Settings.StartMinimized = StartMinSwitch.IsOn;
        _ps.Settings.MinimizeToTray = MinTraySwitch.IsOn;
        _ps.Settings.CloseToTray = CloseTraySwitch.IsOn;
        _ps.SaveSettings();
        MainWindow.Current.UpdateTray();
    }

    // ---------------- library / updates ----------------

    private void OnScanStatus(string s)
    {
        ScanText.Text = s;
        ShowFolders();
    }

    private void ShowFolders()
    {
        FillFolderList(MusicFolderList, _ps.Settings.MusicFolders, video: false);
        FillFolderList(VideoFolderList, _ps.Settings.VideoFolders, video: true);
        RescanButton.IsEnabled = !_ps.IsScanning && (_ps.Settings.MusicFolders.Count + _ps.Settings.VideoFolders.Count) > 0;
    }

    private void FillFolderList(StackPanel panel, List<string> folders, bool video)
    {
        panel.Children.Clear();
        if (folders.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No folders yet", Opacity = 0.6, FontSize = 13 });
            return;
        }
        foreach (var folder in folders.ToList())
        {
            var offline = _ps.OfflineFolders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
            var count = (video ? _ps.VideoLibrary.Tracks : _ps.Library.Tracks).Count(t => LibraryService.IsUnder(t.Path, folder));

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = folder, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = (offline ? "Offline right now · " : "") + $"{count} {(video ? "videos" : "songs")}",
                Opacity = 0.6,
                FontSize = 12,
            });
            row.Children.Add(text);

            var remove = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
                Padding = new Thickness(8),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(remove, "Remove from library (files are not deleted)");
            remove.Click += async (_, _) => await _ps.RemoveFolderAsync(video, folder);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            panel.Children.Add(row);
        }
    }

    private async void AddMusicFolder_Click(object sender, RoutedEventArgs e) => await MainWindow.Current.AddLibraryFolderAsync(video: false);
    private async void AddVideoFolder_Click(object sender, RoutedEventArgs e) => await MainWindow.Current.AddLibraryFolderAsync(video: true);

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await _ps.ScanAsync();

    private void Metadata_Click(object sender, RoutedEventArgs e) => MainWindow.Current.OpenMetadataFixer();

    private async void Update_Click(object sender, RoutedEventArgs e) => await MainWindow.Current.CheckForUpdatesAsync(silent: false);
}
