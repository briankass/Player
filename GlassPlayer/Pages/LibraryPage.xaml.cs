using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly PlayerService _ps = PlayerService.Instance;
    private string? _query;
    private List<Track> _songs = new();
    private string _tab = "songs";

    public LibraryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _query = e.Parameter as string;
        HeaderText.Text = string.IsNullOrEmpty(_query) ? "Music library" : $"Results for \u201C{_query}\u201D";
        _ps.LibraryChanged += Refresh;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _ps.LibraryChanged -= Refresh;

    private bool Matches(string s) => string.IsNullOrEmpty(_query) || s.Contains(_query, StringComparison.OrdinalIgnoreCase);

    private void Refresh()
    {
        IEnumerable<Track> songs = _ps.Library.Tracks.Where(t => Matches(t.Title) || Matches(t.Artist) || Matches(t.Album));
        songs = SortBox.SelectedIndex switch
        {
            1 => songs.OrderBy(t => t.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Album).ThenBy(t => t.TrackNumber),
            2 => songs.OrderBy(t => t.Album, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TrackNumber),
            3 => songs.OrderByDescending(t => t.AddedUtc),
            _ => songs.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
        };
        _songs = songs.ToList();
        SongsList.ItemsSource = _songs;

        IEnumerable<AlbumInfo> albums = _ps.Albums.Where(a => Matches(a.Name) || Matches(a.Artist));
        albums = SortBox.SelectedIndex switch
        {
            0 or 2 => albums.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            3 => albums.OrderByDescending(a => a.LatestAdded),
            _ => albums,
        };
        var albumList = albums.ToList();
        AlbumsGrid.ItemsSource = albumList;

        var artists = _ps.Artists.Where(a => Matches(a.Name)).ToList();
        ArtistsGrid.ItemsSource = artists;

        // search: jump to the first tab that has results
        if (!string.IsNullOrEmpty(_query) && _tab == "songs" && _songs.Count == 0)
            SelectTab(albumList.Count > 0 ? "albums" : "artists");

        CountText.Text = _tab switch
        {
            "albums" => $"{albumList.Count} albums",
            "artists" => $"{artists.Count} artists",
            _ => $"{_songs.Count} songs",
        };
    }

    private void SelectTab(string tag)
    {
        foreach (var item in Tabs.Items)
            if ((item.Tag as string) == tag) { Tabs.SelectedItem = item; break; }
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _tab = sender.SelectedItem?.Tag as string ?? "songs";
        if (SongsList == null || AlbumsGrid == null || ArtistsGrid == null) return; // still in InitializeComponent
        SongsList.Visibility = _tab == "songs" ? Visibility.Visible : Visibility.Collapsed;
        AlbumsGrid.Visibility = _tab == "albums" ? Visibility.Visible : Visibility.Collapsed;
        ArtistsGrid.Visibility = _tab == "artists" ? Visibility.Visible : Visibility.Collapsed;
        if (CountText != null) Refresh();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CountText != null && SongsList != null) Refresh();
    }

    private void SongsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track t) _ps.PlayList(_songs, _songs.IndexOf(t));
    }

    private void SongsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Track t) return;
        var menu = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = "Play", Icon = new FontIcon { Glyph = "\uE768" } };
        play.Click += (_, _) => _ps.PlayList(_songs, _songs.IndexOf(t));
        var next = new MenuFlyoutItem { Text = "Play next", Icon = new FontIcon { Glyph = "\uE893" } };
        next.Click += (_, _) => _ps.PlayNext(t);
        var add = new MenuFlyoutItem { Text = "Add to queue", Icon = new FontIcon { Glyph = "\uE710" } };
        add.Click += (_, _) => _ps.AddToQueue(t);
        var album = new MenuFlyoutItem { Text = "Go to album", Icon = new FontIcon { Glyph = "\uE93C" } };
        album.Click += (_, _) => MainWindow.Current.NavigateTo(typeof(AlbumPage), t.AlbumKey);
        var artist = new MenuFlyoutItem { Text = "Go to artist", Icon = new FontIcon { Glyph = "\uE77B" } };
        artist.Click += (_, _) => MainWindow.Current.NavigateTo(typeof(ArtistPage), t.GroupArtist);
        menu.Items.Add(play);
        menu.Items.Add(next);
        menu.Items.Add(add);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(album);
        menu.Items.Add(artist);
        menu.Items.Add(new MenuFlyoutSeparator());
        var fix = new MenuFlyoutItem { Text = "Fix metadata & art…", Icon = new FontIcon { Glyph = "\uE8EC" } };
        fix.Click += (_, _) => MainWindow.Current.OpenMetadataFixer(new[] { t.Path });
        var fixAlbum = new MenuFlyoutItem { Text = "Fix album metadata…", Icon = new FontIcon { Glyph = "\uE93C" } };
        fixAlbum.Click += (_, _) => MainWindow.Current.OpenMetadataFixer(
            _ps.AlbumOf(t)?.Tracks.Select(x => x.Path).ToList() ?? new List<string> { t.Path });
        menu.Items.Add(fix);
        menu.Items.Add(fixAlbum);
        menu.ShowAt(SongsList, e.GetPosition(SongsList));
        e.Handled = true;
    }

    private void AlbumsGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not AlbumInfo a) return;
        ShowCollectionMenu(AlbumsGrid, e, a.Tracks,
            () => MainWindow.Current.NavigateTo(typeof(AlbumPage), a.Key), "Open album");
        e.Handled = true;
    }

    private void ArtistsGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ArtistInfo a) return;
        ShowCollectionMenu(ArtistsGrid, e, a.Albums.SelectMany(x => x.Tracks).ToList(),
            () => MainWindow.Current.NavigateTo(typeof(ArtistPage), a.Name), "Open artist");
        e.Handled = true;
    }

    private void ShowCollectionMenu(UIElement target, RightTappedRoutedEventArgs e, List<Track> tracks, Action open, string openText)
    {
        var menu = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = "Play", Icon = new FontIcon { Glyph = "\uE768" } };
        play.Click += (_, _) => _ps.PlayList(tracks, 0);
        var shuffle = new MenuFlyoutItem { Text = "Shuffle", Icon = new FontIcon { Glyph = "\uE8B1" } };
        shuffle.Click += (_, _) => _ps.PlayList(tracks, 0, shuffle: true);
        var queue = new MenuFlyoutItem { Text = "Add to queue", Icon = new FontIcon { Glyph = "\uE710" } };
        queue.Click += (_, _) => { foreach (var t in tracks) _ps.AddToQueue(t); };
        var openItem = new MenuFlyoutItem { Text = openText, Icon = new FontIcon { Glyph = "\uE8A7" } };
        openItem.Click += (_, _) => open();
        var fix = new MenuFlyoutItem { Text = "Fix metadata & art…", Icon = new FontIcon { Glyph = "\uE8EC" } };
        fix.Click += (_, _) => MainWindow.Current.OpenMetadataFixer(tracks.Select(t => t.Path).ToList());
        menu.Items.Add(play);
        menu.Items.Add(shuffle);
        menu.Items.Add(queue);
        menu.Items.Add(openItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(fix);
        menu.ShowAt(target, e.GetPosition(target));
    }

    private void AlbumsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumInfo a) MainWindow.Current.NavigateTo(typeof(AlbumPage), a.Key);
    }

    private void ArtistsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistInfo a) MainWindow.Current.NavigateTo(typeof(ArtistPage), a.Name);
    }

    private void ShuffleAll_Click(object sender, RoutedEventArgs e)
    {
        if (_songs.Count > 0) _ps.PlayList(_songs, 0, shuffle: true);
    }
}
