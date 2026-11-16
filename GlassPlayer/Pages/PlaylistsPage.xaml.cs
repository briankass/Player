using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public class PlaylistTile
{
    public string Key { get; set; } = "";       // "smart:<key>" or a playlist id
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Glyph { get; set; } = "";
    public Brush Background { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 60, 60));
    public ImageSource? Cover { get; set; }
    public Playlist? Playlist { get; set; }
}

public sealed partial class PlaylistsPage : Page
{
    private static readonly Windows.UI.Color[] SmartColors =
    {
        Windows.UI.Color.FromArgb(255, 196, 48, 84),
        Windows.UI.Color.FromArgb(255, 214, 120, 30),
        Windows.UI.Color.FromArgb(255, 46, 110, 190),
        Windows.UI.Color.FromArgb(255, 40, 140, 100),
        Windows.UI.Color.FromArgb(255, 110, 70, 180),
    };

    private readonly PlayerService _ps = PlayerService.Instance;

    public PlaylistsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _ps.Playlists.Changed += Refresh;
        _ps.LibraryChanged += Refresh;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ps.Playlists.Changed -= Refresh;
        _ps.LibraryChanged -= Refresh;
    }

    private ImageSource? CoverFor(IEnumerable<string> paths)
    {
        foreach (var t in _ps.Resolve(paths.Take(20)))
        {
            var album = _ps.AlbumOf(t);
            if (album?.Art != null) return album.Art;
        }
        return null;
    }

    private void Refresh()
    {
        var user = _ps.Playlists.All.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            var tracks = _ps.Resolve(p.Paths);
            var missing = p.Paths.Count - tracks.Count;
            return new PlaylistTile
            {
                Key = p.Id,
                Name = p.Name,
                Subtitle = $"{tracks.Count} songs" + (missing > 0 ? $" · {missing} missing" : ""),
                Cover = CoverFor(p.Paths),
                Playlist = p,
            };
        }).ToList();
        UserGrid.ItemsSource = user;
        NoPlaylistsText.Visibility = user.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int i = 0;
        SmartGrid.ItemsSource = PlayerService.SmartPlaylists.Select(sp => new PlaylistTile
        {
            Key = "smart:" + sp.Key,
            Name = sp.Name,
            Subtitle = $"{_ps.SmartPlaylist(sp.Key).Count} songs · {sp.Description}",
            Glyph = sp.Glyph,
            Background = new SolidColorBrush(SmartColors[i++ % SmartColors.Length]),
        }).ToList();
    }

    private void Tile_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistTile t) MainWindow.Current.NavigateTo(typeof(PlaylistPage), t.Key);
    }

    private void UserGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not PlaylistTile { Playlist: Playlist p }) return;
        var tracks = _ps.Resolve(p.Paths);
        var menu = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = "Play", Icon = new FontIcon { Glyph = "" } };
        play.Click += (_, _) => _ps.PlayList(tracks, 0);
        var shuffle = new MenuFlyoutItem { Text = "Shuffle", Icon = new FontIcon { Glyph = "" } };
        shuffle.Click += (_, _) => _ps.PlayList(tracks, 0, shuffle: true);
        var rename = new MenuFlyoutItem { Text = "Rename…", Icon = new FontIcon { Glyph = "" } };
        rename.Click += async (_, _) =>
        {
            var name = await MainWindow.Current.PromptAsync("Rename playlist", "Playlist name", p.Name);
            if (name != null) _ps.Playlists.Rename(p, name);
        };
        var delete = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "" } };
        delete.Click += async (_, _) => await PlaylistPage.ConfirmDeleteAsync(p);
        menu.Items.Add(play);
        menu.Items.Add(shuffle);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.ShowAt(UserGrid, e.GetPosition(UserGrid));
        e.Handled = true;
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var name = await MainWindow.Current.PromptAsync("New playlist", "Playlist name");
        if (name == null) return;
        var p = _ps.Playlists.Create(name);
        MainWindow.Current.NavigateTo(typeof(PlaylistPage), p.Id);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var file = await MainWindow.Current.PickOpenFileAsync(".m3u", ".m3u8");
        if (file == null) return;
        try
        {
            var p = _ps.Playlists.ImportM3u(file);
            var found = _ps.Resolve(p.Paths).Count;
            if (found < p.Paths.Count)
                await MainWindow.Current.ShowMessage("Playlist imported",
                    $"{found} of {p.Paths.Count} songs are in your library. The others are kept in the playlist and will show up if you add their folder.");
            MainWindow.Current.NavigateTo(typeof(PlaylistPage), p.Id);
        }
        catch (Exception ex)
        {
            await MainWindow.Current.ShowMessage("Couldn't import playlist", ex.Message);
        }
    }
}
