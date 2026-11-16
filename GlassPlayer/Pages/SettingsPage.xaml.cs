using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly PlayerService _ps = PlayerService.Instance;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        BackdropBox.SelectedIndex = Math.Clamp(_ps.Settings.Backdrop, 0, 2);
        _loading = false;
        VersionText.Text = $"Glass Player {UpdateService.CurrentVersion}";
        _ps.ScanStatus += OnScanStatus;
        ShowFolder();
        ScanText.Text = _ps.IsScanning ? "Scanning…" : $"{_ps.Library.Tracks.Count} songs · {_ps.Albums.Count} albums · {_ps.Artists.Count} artists";
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _ps.ScanStatus -= OnScanStatus;

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

    private void BackdropBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && BackdropBox.SelectedIndex >= 0) MainWindow.Current.ApplyBackdrop(BackdropBox.SelectedIndex);
    }

    private async void Update_Click(object sender, RoutedEventArgs e) => await MainWindow.Current.CheckForUpdatesAsync(silent: false);
}
