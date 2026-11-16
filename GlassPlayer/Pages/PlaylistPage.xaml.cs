using System.Collections.ObjectModel;
using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public sealed partial class PlaylistPage : Page
{
    private readonly PlayerService _ps = PlayerService.Instance;
    private string _key = "";
    private Playlist? _playlist;          // null for automatic playlists
    private ObservableCollection<Track> _tracks = new();

    public PlaylistPage()
    {
        InitializeComponent();
    }

    private bool IsSmart => _key.StartsWith("smart:");

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _key = e.Parameter as string ?? "";
        _ps.Playlists.Changed += Load;
        _ps.LibraryChanged += Load;
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ps.Playlists.Changed -= Load;
        _ps.LibraryChanged -= Load;
    }

    private void Load()
    {
        List<Track> tracks;
        if (IsSmart)
        {
            var key = _key[6..];
            var info = PlayerService.SmartPlaylists.FirstOrDefault(s => s.Key == key);
            _playlist = null;
            NameText.Text = info.Name ?? "Playlist";
            KindText.Text = "AUTOMATIC PLAYLIST";
            CoverGlyph.Glyph = info.Glyph ?? "";
            tracks = _ps.SmartPlaylist(key);
            EmptyText.Text = key == "favorites"
                ? "Nothing here yet. Click the ♥ in the player bar while a song plays to add it."
                : "Nothing here yet. Play some music and come back.";
        }
        else
        {
            _playlist = _ps.Playlists.Find(_key);
            if (_playlist == null) { NameText.Text = "Playlist not found"; TrackList.ItemsSource = null; return; }
            NameText.Text = _playlist.Name;
            KindText.Text = "PLAYLIST";
            CoverGlyph.Glyph = "";
            tracks = _ps.Resolve(_playlist.Paths);
            EmptyText.Text = "This playlist is empty. Right-click songs, albums or folders anywhere and choose Add to playlist.";
        }

        _tracks = new ObservableCollection<Track>(tracks);
        TrackList.ItemsSource = _tracks;
        // drag to reorder: only your own playlists
        TrackList.CanDragItems = TrackList.CanReorderItems = TrackList.AllowDrop = !IsSmart;
        EmptyText.Visibility = tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var total = TimeSpan.FromSeconds(tracks.Sum(t => t.DurationSeconds));
        var missing = _playlist == null ? 0 : _playlist.Paths.Count - tracks.Count;
        DetailsText.Text = $"{tracks.Count} songs · " +
                           (total.TotalHours >= 1 ? $"{(int)total.TotalHours} hr {total.Minutes} min" : $"{total.Minutes} min") +
                           (missing > 0 ? $" · {missing} not in library" : "") +
                           (IsSmart ? "" : " · drag songs to reorder");

        RenameItem.Visibility = DeleteItem.Visibility = UserSeparator.Visibility = IsSmart ? Visibility.Collapsed : Visibility.Visible;

        // header art from the first song that has some
        var art = tracks.Take(20).Select(t => _ps.AlbumOf(t)).FirstOrDefault(a => a?.ArtPath != null);
        CoverImage.Source = art?.LargeArt;
        HeaderBg.Source = art?.Art;
        CoverGlyph.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TrackList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_playlist == null) return;
        // keep songs that aren't in the library (NAS offline...) at the end instead of dropping them
        var shown = new HashSet<string>(_tracks.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var hidden = _playlist.Paths.Where(p => !shown.Contains(p));
        _ps.Playlists.Changed -= Load;
        _ps.Playlists.SetPaths(_playlist, _tracks.Select(t => t.Path).Concat(hidden).ToList());
        _ps.Playlists.Changed += Load;
    }

    private void TrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track t) PlayFrom(_tracks.IndexOf(t));
    }

    private void PlayFrom(int index)
    {
        var list = _tracks.ToList();
        if (list.Count == 0) return;
        _ps.PlayList(list, Math.Max(0, index));
        if (list[Math.Max(0, index)].IsVideo) MainWindow.Current.NavigateTo(typeof(VideoPlayerPage));
    }

    private void TrackList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Track t) return;
        var index = _tracks.IndexOf(t);
        var menu = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = "Play", Icon = new FontIcon { Glyph = "" } };
        play.Click += (_, _) => PlayFrom(index);
        var next = new MenuFlyoutItem { Text = "Play next", Icon = new FontIcon { Glyph = "" } };
        next.Click += (_, _) => _ps.PlayNext(t);
        var queue = new MenuFlyoutItem { Text = "Add to queue", Icon = new FontIcon { Glyph = "" } };
        queue.Click += (_, _) => _ps.AddToQueue(t);
        menu.Items.Add(play);
        menu.Items.Add(next);
        menu.Items.Add(queue);
        menu.Items.Add(PlaylistMenu.SubItem(new[] { t }));
        if (_playlist != null)
        {
            var remove = new MenuFlyoutItem { Text = "Remove from this playlist", Icon = new FontIcon { Glyph = "" } };
            remove.Click += (_, _) =>
            {
                // remove this occurrence (the n-th time this path appears)
                int nth = _tracks.Take(index + 1).Count(x => x.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase));
                var paths = _playlist.Paths.ToList();
                for (int i = 0, seen = 0; i < paths.Count; i++)
                    if (paths[i].Equals(t.Path, StringComparison.OrdinalIgnoreCase) && ++seen == nth) { paths.RemoveAt(i); break; }
                _ps.Playlists.SetPaths(_playlist, paths);
            };
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(remove);
        }
        if (!t.IsVideo)
        {
            var fix = new MenuFlyoutItem { Text = "Fix metadata & art…", Icon = new FontIcon { Glyph = "" } };
            fix.Click += (_, _) => MainWindow.Current.OpenMetadataFixer(new[] { t.Path });
            menu.Items.Add(fix);
        }
        menu.ShowAt(TrackList, e.GetPosition(TrackList));
        e.Handled = true;
    }

    private void Play_Click(object sender, RoutedEventArgs e) => PlayFrom(0);

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count > 0) _ps.PlayList(_tracks.ToList(), 0, shuffle: true);
    }

    private void AddQueue_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _tracks) _ps.AddToQueue(t);
    }

    private async void SaveCopy_Click(object sender, RoutedEventArgs e)
    {
        var name = await MainWindow.Current.PromptAsync("Save as a new playlist", "Playlist name", NameText.Text);
        if (name == null) return;
        var p = _ps.Playlists.Create(name, _tracks.Select(t => t.Path));
        MainWindow.Current.NavigateTo(typeof(PlaylistPage), p.Id);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var file = await MainWindow.Current.PickSaveFileAsync(NameText.Text, "M3U playlist", ".m3u8");
        if (file == null) return;
        try { PlaylistStore.ExportM3u(file, NameText.Text, _tracks); }
        catch (Exception ex) { await MainWindow.Current.ShowMessage("Couldn't export playlist", ex.Message); }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist == null) return;
        var name = await MainWindow.Current.PromptAsync("Rename playlist", "Playlist name", _playlist.Name);
        if (name != null) _ps.Playlists.Rename(_playlist, name);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist != null && await ConfirmDeleteAsync(_playlist) && Frame.CanGoBack) Frame.GoBack();
    }

    public static async Task<bool> ConfirmDeleteAsync(Playlist p)
    {
        var dlg = new ContentDialog
        {
            Title = $"Delete “{p.Name}”?",
            Content = "The playlist is removed. Your music files are not touched.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = MainWindow.Current.Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return false;
        PlayerService.Instance.Playlists.Delete(p);
        return true;
    }
}
