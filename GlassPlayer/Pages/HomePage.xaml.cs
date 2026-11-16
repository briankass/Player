using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public sealed partial class HomePage : Page
{
    private readonly PlayerService _ps = PlayerService.Instance;

    public HomePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _ps.LibraryChanged += Refresh;
        _ps.TrackChanged += Refresh;
        _ps.ScanStatus += OnScanStatus;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ps.LibraryChanged -= Refresh;
        _ps.TrackChanged -= Refresh;
        _ps.ScanStatus -= OnScanStatus;
    }

    private void OnScanStatus(string s) => ScanText.Text = s;

    private void Refresh()
    {
        bool empty = _ps.Library.Tracks.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ShuffleAllButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        var recent = _ps.Settings.Recent.Select(_ps.FindAlbum).Where(a => a != null).Take(12).ToList();
        RecentGrid.ItemsSource = recent;
        RecentHeader.Visibility = RecentGrid.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var added = _ps.Albums.OrderByDescending(a => a.LatestAdded).Take(18).ToList();
        AddedGrid.ItemsSource = added;
        AddedHeader.Visibility = AddedGrid.Visibility = added.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Album_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumInfo a) MainWindow.Current.NavigateTo(typeof(AlbumPage), a.Key);
    }

    private void ShuffleAll_Click(object sender, RoutedEventArgs e) => _ps.PlayList(_ps.Library.Tracks, 0, shuffle: true);

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e) => await MainWindow.Current.PickLibraryFolderAsync();
}
