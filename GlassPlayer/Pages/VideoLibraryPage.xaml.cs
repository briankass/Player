using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public sealed partial class VideoLibraryPage : Page
{
    private readonly PlayerService _ps = PlayerService.Instance;
    private List<VideoInfo> _visible = new();
    private bool _loading;

    public VideoLibraryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _ps.VideosChanged += Refresh;
        _ps.ScanStatus += OnScanStatus;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ps.VideosChanged -= Refresh;
        _ps.ScanStatus -= OnScanStatus;
    }

    private void OnScanStatus(string s) => ScanText.Text = s;

    private void Refresh()
    {
        // folder filter: the top-level library folders
        _loading = true;
        var selected = FolderBox.SelectedItem as string;
        var folders = new List<string> { "All folders" };
        folders.AddRange(_ps.Settings.VideoFolders);
        FolderBox.ItemsSource = folders;
        FolderBox.SelectedItem = selected != null && folders.Contains(selected) ? selected : folders[0];
        _loading = false;

        bool empty = _ps.Videos.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        var cont = _ps.ContinueWatching();
        ContinueGrid.ItemsSource = cont;
        ContinueHeader.Visibility = ContinueGrid.Visibility = cont.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<VideoInfo> v = _ps.Videos;
        var q = FilterBox.Text.Trim();
        if (q.Length > 0)
            v = v.Where(x => x.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Folder.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (FolderBox.SelectedIndex > 0 && FolderBox.SelectedItem is string folder)
            v = v.Where(x => LibraryService.IsUnder(x.Track.Path, folder));
        v = SortBox.SelectedIndex switch
        {
            1 => v.OrderByDescending(x => x.Track.AddedUtc),
            2 => v.OrderBy(x => x.Track.Path, StringComparer.OrdinalIgnoreCase),
            _ => v.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase),
        };
        _visible = v.ToList();
        AllGrid.ItemsSource = _visible;
        AllHeader.Text = $"All videos ({_visible.Count})";
        AllHeader.Visibility = _ps.Videos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && AllGrid != null && FilterBox != null) ApplyFilter();
    }

    private void Play(List<VideoInfo> list, VideoInfo v)
    {
        _ps.PlayList(list.Select(x => x.Track).ToList(), list.IndexOf(v));
        MainWindow.Current.NavigateTo(typeof(VideoPlayerPage));
    }

    private void All_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VideoInfo v) Play(_visible, v);
    }

    private void Continue_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VideoInfo v) Play(new List<VideoInfo> { v }, v);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e) => await MainWindow.Current.AddLibraryFolderAsync(video: true);
}
