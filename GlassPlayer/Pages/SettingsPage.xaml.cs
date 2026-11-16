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
        VersionText.Text = $"Glass Player {UpdateService.CurrentVersion}";
        _ps.ScanStatus += OnScanStatus;
        ShowFolder();
        ScanText.Text = _ps.IsScanning ? "Scanning…" : $"{_ps.Library.Tracks.Count} songs · {_ps.Albums.Count} albums · {_ps.Artists.Count} artists";
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

    // ---------------- library / updates ----------------

    private void OnScanStatus(string s)
    {
        ScanText.Text = s;
        ShowFolder();
    }

    private void ShowFolder()
    {
        FolderText.Text = string.IsNullOrEmpty(_ps.Settings.LibraryFolder) ? "No folder chosen yet" : _ps.Settings.LibraryFolder;
        RescanButton.IsEnabled = !string.IsNullOrEmpty(_ps.Settings.LibraryFolder);
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e) => await MainWindow.Current.PickLibraryFolderAsync();

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_ps.Settings.LibraryFolder)) await _ps.ScanAsync(_ps.Settings.LibraryFolder);
    }

    private void Metadata_Click(object sender, RoutedEventArgs e) => MainWindow.Current.OpenMetadataFixer();

    private async void Update_Click(object sender, RoutedEventArgs e) => await MainWindow.Current.CheckForUpdatesAsync(silent: false);
}
