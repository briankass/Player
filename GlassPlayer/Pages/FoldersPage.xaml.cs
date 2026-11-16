using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public class FolderEntry
{
    public bool IsFolder { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Right { get; set; } = "";
    public string Glyph { get; set; } = "";
    public Track? Track { get; set; }
    public string? ArtPath { get; set; }

    private ImageSource? _art;
    public ImageSource? Art => _art ??= ArtPath == null ? null : new BitmapImage(new Uri(ArtPath)) { DecodePixelWidth = 96 };
}

public class Crumb
{
    public string Label { get; set; } = "";
    public string? Path { get; set; }
    public override string ToString() => Label;
}

/// <summary>Browse library folders as they are on disk / the NAS (from the library index, so it works offline).</summary>
public sealed partial class FoldersPage : Page
{
    private readonly PlayerService _ps = PlayerService.Instance;
    private string? _current; // null = the list of library folders

    public FoldersPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _current = e.Parameter as string;
        _ps.LibraryChanged += Refresh;
        _ps.VideosChanged += Refresh;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ps.LibraryChanged -= Refresh;
        _ps.VideosChanged -= Refresh;
    }

    private IEnumerable<Track> AllTracks => _ps.Library.Tracks.Concat(_ps.VideoLibrary.Tracks);

    private IEnumerable<(string Folder, bool Video)> Roots =>
        _ps.Settings.MusicFolders.Select(f => (f, false)).Concat(_ps.Settings.VideoFolders.Select(f => (f, true)));

    private static string Norm(string p) => p.TrimEnd('\\', '/');

    private void Open(string? folder)
    {
        _current = folder == null ? null : Norm(folder);
        Refresh();
    }

    private void Refresh()
    {
        var entries = new List<FolderEntry>();
        if (_current == null)
        {
            foreach (var (folder, video) in Roots)
            {
                var count = AllTracks.Count(t => LibraryService.IsUnder(t.Path, folder));
                var offline = _ps.OfflineFolders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
                entries.Add(new FolderEntry
                {
                    IsFolder = true,
                    Path = Norm(folder),
                    Name = folder,
                    Subtitle = (video ? "Video folder" : "Music folder") + (offline ? " · offline right now" : ""),
                    Right = $"{count} {(video ? "videos" : "songs")}",
                    Glyph = video ? "" : "",
                });
            }
            EmptyText.Text = "No library folders yet. Add music or video folders in Settings.";
        }
        else
        {
            var prefix = _current + "\\";
            var under = AllTracks.Where(t => t.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

            // sub-folders: first path segment after the current folder
            foreach (var g in under.Where(t => t.Path.IndexOf('\\', prefix.Length) > 0)
                                   .GroupBy(t => t.Path[prefix.Length..t.Path.IndexOf('\\', prefix.Length)], StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var songs = g.Count(t => !t.IsVideo);
                var videos = g.Count(t => t.IsVideo);
                var firstMusic = g.FirstOrDefault(t => !t.IsVideo);
                entries.Add(new FolderEntry
                {
                    IsFolder = true,
                    Path = prefix + g.Key,
                    Name = g.Key,
                    Subtitle = string.Join(" · ", new[]
                    {
                        songs > 0 ? $"{songs} songs" : "",
                        videos > 0 ? $"{videos} videos" : "",
                    }.Where(s => s.Length > 0)),
                    Glyph = songs == 0 && videos > 0 ? "" : "",
                    ArtPath = firstMusic != null ? _ps.AlbumOf(firstMusic)?.ArtPath : null,
                });
            }

            // files directly in this folder
            foreach (var t in under.Where(t => t.Path.IndexOf('\\', prefix.Length) < 0)
                                   .OrderBy(t => t.IsVideo).ThenBy(t => t.TrackNumber == 0 ? uint.MaxValue : t.TrackNumber)
                                   .ThenBy(t => System.IO.Path.GetFileName(t.Path), StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new FolderEntry
                {
                    Path = t.Path,
                    Track = t,
                    Name = System.IO.Path.GetFileName(t.Path),
                    Subtitle = t.IsVideo ? t.Title : $"{t.Title} · {t.Artist}",
                    Right = t.DurationSeconds > 0 ? t.DurationText : "",
                    Glyph = t.IsVideo ? "" : "",
                    ArtPath = t.IsVideo ? ThumbCache.Existing(t.Path) : _ps.AlbumOf(t)?.ArtPath,
                });
            }
            EmptyText.Text = "Nothing playable in this folder.";
        }

        EntryList.ItemsSource = entries;
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FolderActions.Visibility = _current == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateCrumbs();
    }

    private void UpdateCrumbs()
    {
        var crumbs = new List<Crumb> { new() { Label = "Library folders", Path = null } };
        if (_current != null)
        {
            var root = Roots.Select(r => Norm(r.Folder))
                            .Where(r => _current.Equals(r, StringComparison.OrdinalIgnoreCase)
                                        || _current.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(r => r.Length).FirstOrDefault() ?? _current;
            crumbs.Add(new Crumb { Label = root, Path = root });
            if (_current.Length > root.Length)
            {
                var acc = root;
                foreach (var part in _current[(root.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    acc += "\\" + part;
                    crumbs.Add(new Crumb { Label = part, Path = acc });
                }
            }
        }
        Crumbs.ItemsSource = crumbs;
    }

    private void Crumbs_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb c) Open(c.Path);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var isRoot = Roots.Any(r => Norm(r.Folder).Equals(_current, StringComparison.OrdinalIgnoreCase));
        Open(isRoot ? null : System.IO.Path.GetDirectoryName(_current));
    }

    // ---------------- playing ----------------

    private List<Track> TracksIn(string folder, bool recursive) =>
        AllTracks.Where(t => recursive
                ? LibraryService.IsUnder(t.Path, folder)
                : string.Equals(System.IO.Path.GetDirectoryName(t.Path), folder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();

    private void Play(List<Track> tracks, int index, bool shuffle = false)
    {
        if (tracks.Count == 0) return;
        // don't mix music and videos in one queue: use whatever kind was picked
        var kind = tracks[Math.Clamp(index, 0, tracks.Count - 1)].IsVideo;
        var picked = tracks[Math.Clamp(index, 0, tracks.Count - 1)];
        var same = tracks.Where(t => t.IsVideo == kind).ToList();
        _ps.PlayList(same, Math.Max(0, same.IndexOf(picked)), shuffle);
        if (kind) MainWindow.Current.NavigateTo(typeof(VideoPlayerPage));
    }

    private void EntryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FolderEntry entry) return;
        if (entry.IsFolder) { Open(entry.Path); return; }
        if (entry.Track == null || _current == null) return;
        var files = (EntryList.ItemsSource as List<FolderEntry>)!.Where(x => x.Track != null).Select(x => x.Track!).ToList();
        Play(files, files.IndexOf(entry.Track));
    }

    private void PlayAll_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var tracks = TracksIn(_current, recursive: true);
        var music = tracks.Where(t => !t.IsVideo).ToList();
        Play(music.Count > 0 ? music : tracks, 0);
    }

    private void ShuffleAll_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var tracks = TracksIn(_current, recursive: true);
        var music = tracks.Where(t => !t.IsVideo).ToList();
        Play(music.Count > 0 ? music : tracks, 0, shuffle: true);
    }

    private void EntryList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FolderEntry entry) return;
        var tracks = entry.IsFolder ? TracksIn(entry.Path, recursive: true) : new List<Track> { entry.Track! };
        var music = tracks.Where(t => !t.IsVideo).ToList();
        var menu = new MenuFlyout();

        if (entry.IsFolder)
        {
            var open = new MenuFlyoutItem { Text = "Open", Icon = new FontIcon { Glyph = "" } };
            open.Click += (_, _) => Open(entry.Path);
            menu.Items.Add(open);
        }
        var play = new MenuFlyoutItem { Text = "Play", Icon = new FontIcon { Glyph = "" } };
        play.Click += (_, _) => Play(music.Count > 0 ? music : tracks, 0);
        menu.Items.Add(play);
        if (entry.IsFolder && tracks.Count > 1)
        {
            var shuffle = new MenuFlyoutItem { Text = "Shuffle", Icon = new FontIcon { Glyph = "" } };
            shuffle.Click += (_, _) => Play(music.Count > 0 ? music : tracks, 0, shuffle: true);
            menu.Items.Add(shuffle);
        }
        var queue = new MenuFlyoutItem { Text = "Add to queue", Icon = new FontIcon { Glyph = "" } };
        queue.Click += (_, _) => { foreach (var t in tracks) _ps.AddToQueue(t); };
        menu.Items.Add(queue);
        if (tracks.Count > 0) menu.Items.Add(PlaylistMenu.SubItem(tracks));
        if (music.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var fix = new MenuFlyoutItem { Text = "Fix metadata & art…", Icon = new FontIcon { Glyph = "" } };
            fix.Click += (_, _) => MainWindow.Current.OpenMetadataFixer(music.Select(t => t.Path).ToList());
            menu.Items.Add(fix);
        }
        var explorer = new MenuFlyoutItem { Text = "Show in File Explorer", Icon = new FontIcon { Glyph = "" } };
        explorer.Click += (_, _) =>
        {
            try
            {
                var args = entry.IsFolder ? $"\"{entry.Path}\"" : $"/select,\"{entry.Path}\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            }
            catch { }
        };
        menu.Items.Add(explorer);
        menu.ShowAt(EntryList, e.GetPosition(EntryList));
        e.Handled = true;
    }
}
