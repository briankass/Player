using System.Runtime.InteropServices.WindowsRuntime;
using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace GlassPlayer;

public sealed partial class MetadataWindow : Window
{
    private readonly LibraryService _library;
    private readonly MetadataService _meta = new();
    private readonly Action<List<string>> _onLibraryChanged;
    private readonly Func<string, bool> _isInUse;
    private List<FixItem> _items = new();
    private CancellationTokenSource? _cts;

    public MetadataWindow(LibraryService library, Action<List<string>> onLibraryChanged, Func<string, bool> isInUse)
    {
        InitializeComponent();
        _library = library;
        _onLibraryChanged = onLibraryChanged;
        _isInUse = isInUse;
        Title = "Fix metadata";
        Services.WindowHelper.ResizeLogical(this, 1000, 760);
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleArea);
        Closed += (_, _) => _cts?.Cancel();
        _ = LoadItemsAsync();
    }

    // ---------- building the list ----------

    private async Task LoadItemsAsync()
    {
        SummaryText.Text = "Reading tags…";
        SearchButton.IsEnabled = false;
        var scope = ScopeBox.SelectedIndex;
        var paths = _library.Tracks.Select(t => t.Path).ToList();

        var items = await Task.Run(() =>
        {
            var list = new List<FixItem>();
            foreach (var p in paths)
            {
                var item = ReadItem(p);
                if (item == null) continue;
                bool missingAny = item.CurTitle.Length == 0 || item.CurArtist.Length == 0
                                  || item.CurAlbum.Length == 0 || !item.CurHasArt;
                if (scope == 0 && !missingAny) continue;
                if (scope == 1 && item.CurHasArt) continue;
                list.Add(item);
            }
            return list;
        });

        _items = items;
        ItemsList.ItemsSource = _items;
        SummaryText.Text = _library.Tracks.Count == 0
            ? "Your library is empty — choose a music folder in the main window first."
            : $"{_items.Count} of {_library.Tracks.Count} tracks shown.";
        SearchButton.IsEnabled = _items.Count > 0;
    }

    private static FixItem? ReadItem(string path)
    {
        try
        {
            using var tl = TagLib.File.Create(path);
            var item = new FixItem
            {
                Path = path,
                CurTitle = tl.Tag.Title?.Trim() ?? "",
                CurArtist = (tl.Tag.FirstPerformer ?? tl.Tag.FirstAlbumArtist ?? "").Trim(),
                CurAlbum = tl.Tag.Album?.Trim() ?? "",
                CurYear = tl.Tag.Year,
                CurTrack = tl.Tag.Track,
                CurHasArt = tl.Tag.Pictures.Length > 0,
                Duration = tl.Properties.Duration.TotalSeconds,
            };
            GuessHints(item);
            return item;
        }
        catch
        {
            return null; // unreadable / unsupported file
        }
    }

    /// <summary>Fill search hints from tags, falling back to "Artist/Album/01 - Title.ext" style paths.</summary>
    private static void GuessHints(FixItem item)
    {
        item.HintTitle = item.CurTitle;
        item.HintArtist = item.CurArtist;
        item.HintAlbum = item.CurAlbum;

        var name = Path.GetFileNameWithoutExtension(item.Path);
        var albumDir = Path.GetDirectoryName(item.Path);
        var artistDir = albumDir == null ? null : Path.GetDirectoryName(albumDir);

        if (item.HintTitle.Length == 0)
        {
            // strip a leading track number: "01 - ", "01. ", "1 "
            var stripped = System.Text.RegularExpressions.Regex.Replace(name, @"^\s*\d{1,3}\s*[-._)]?\s*", "");
            var dash = stripped.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                if (item.HintArtist.Length == 0) item.HintArtist = stripped[..dash].Trim();
                item.HintTitle = stripped[(dash + 3)..].Trim();
            }
            else
            {
                item.HintTitle = stripped.Trim();
            }
        }
        if (item.HintAlbum.Length == 0 && albumDir != null) item.HintAlbum = Path.GetFileName(albumDir);
        if (item.HintArtist.Length == 0 && artistDir != null) item.HintArtist = Path.GetFileName(artistDir);
    }

    private void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_library is null || SummaryText is null || _cts != null) return; // fires during InitializeComponent
        _ = LoadItemsAsync();
    }

    // ---------- searching ----------

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        bool onlyMissing = OnlyMissingBox.IsChecked == true;
        SearchButton.IsEnabled = ScopeBox.IsEnabled = ApplyButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        int done = 0, found = 0;
        try
        {
            foreach (var item in _items)
            {
                ct.ThrowIfCancellationRequested();
                item.Status = "Searching…";
                var match = await _meta.LookupAsync(item.HintArtist, item.HintTitle, item.HintAlbum, item.Duration, ct);
                if (match == null)
                {
                    item.Status = "No confident match on MusicBrainz";
                }
                else
                {
                    Propose(item, match, onlyMissing);
                    if (item.NewArt == null && (!item.CurHasArt || !onlyMissing))
                    {
                        var art = await _meta.GetCoverAsync(match.ReleaseId, match.ReleaseGroupId, ct);
                        if (art != null)
                        {
                            item.NewArt = art;
                            item.ArtPreview = await ToImage(art);
                        }
                    }
                    item.RefreshProposed();
                    item.Status = item.HasChanges
                        ? $"Match {match.Score}% · {match.Artist} — {match.Album}"
                        : $"Match {match.Score}% · nothing to change";
                    if (item.HasChanges) { item.IsSelected = true; found++; }
                }
                done++;
                Progress.Value = (double)done / _items.Count;
                StatusText.Text = $"Searched {done}/{_items.Count} · {found} with changes";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Stopped after {done} tracks · {found} with changes";
        }
        finally
        {
            _cts = null;
            SearchButton.IsEnabled = ScopeBox.IsEnabled = ApplyButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private static void Propose(FixItem item, MetadataMatch m, bool onlyMissing)
    {
        item.NewTitle = Pick(item.CurTitle, m.Title, onlyMissing);
        item.NewArtist = Pick(item.CurArtist, m.Artist, onlyMissing);
        item.NewAlbum = Pick(item.CurAlbum, m.Album, onlyMissing);
        item.NewYear = m.Year > 0 && (item.CurYear == 0 || (!onlyMissing && item.CurYear != m.Year)) ? m.Year : null;
        item.NewTrack = m.TrackNumber > 0 && (item.CurTrack == 0 || (!onlyMissing && item.CurTrack != m.TrackNumber)) ? m.TrackNumber : null;
    }

    private static string? Pick(string current, string found, bool onlyMissing)
    {
        if (found.Length == 0) return null;
        if (current.Length == 0) return found;
        if (!onlyMissing && !current.Equals(found, StringComparison.Ordinal)) return found;
        return null;
    }

    private static async Task<ImageSource?> ToImage(byte[] bytes)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bmp = new BitmapImage { DecodePixelWidth = 128 };
            await bmp.SetSourceAsync(stream);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsSelected = i.HasChanges;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsSelected = false;
    }

    // ---------- writing ----------

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _items.Where(i => i.IsSelected && i.HasChanges).ToList();
        if (chosen.Count == 0)
        {
            StatusText.Text = "Nothing selected. Search first, then tick the rows you want to write.";
            return;
        }

        var confirm = new ContentDialog
        {
            Title = $"Write tags to {chosen.Count} file(s)?",
            Content = "This changes the files themselves (on your NAS, if that's where they live). It can't be undone from the app.",
            PrimaryButtonText = "Write tags",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        ApplyButton.IsEnabled = SearchButton.IsEnabled = false;
        int ok = 0, failed = 0;
        var written = new List<string>();
        foreach (var item in chosen)
        {
            if (_isInUse(item.Path))
            {
                item.Status = "Skipped: this track is playing. Stop it and try again.";
                failed++;
                continue;
            }
            var error = await Task.Run(() => Write(item));
            if (error == null)
            {
                ok++;
                written.Add(item.Path);
                item.Status = "Written ✓";
                item.IsSelected = false;
                var idx = _library.Tracks.FindIndex(t => t.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) _library.Tracks[idx] = LibraryService.ReadTrack(item.Path);
                item.NewTitle = item.NewArtist = item.NewAlbum = null;
                item.NewYear = item.NewTrack = null;
                item.NewArt = null;
                item.RefreshProposed();
            }
            else
            {
                failed++;
                item.Status = "Failed: " + error;
            }
            StatusText.Text = $"Wrote {ok} · failed {failed}";
        }

        _library.SaveIndex();
        _onLibraryChanged(written);
        ApplyButton.IsEnabled = SearchButton.IsEnabled = true;
    }

    private static string? Write(FixItem item)
    {
        try
        {
            using var tl = TagLib.File.Create(item.Path);
            if (item.NewTitle != null) tl.Tag.Title = item.NewTitle;
            if (item.NewArtist != null)
            {
                tl.Tag.Performers = new[] { item.NewArtist };
                if (tl.Tag.AlbumArtists.Length == 0) tl.Tag.AlbumArtists = new[] { item.NewArtist };
            }
            if (item.NewAlbum != null) tl.Tag.Album = item.NewAlbum;
            if (item.NewYear != null) tl.Tag.Year = item.NewYear.Value;
            if (item.NewTrack != null) tl.Tag.Track = item.NewTrack.Value;
            if (item.NewArt != null)
            {
                var isPng = item.NewArt.Length > 4 && item.NewArt[0] == 0x89 && item.NewArt[1] == 0x50;
                tl.Tag.Pictures = new TagLib.IPicture[]
                {
                    new TagLib.Picture(new TagLib.ByteVector(item.NewArt))
                    {
                        Type = TagLib.PictureType.FrontCover,
                        MimeType = isPng ? "image/png" : "image/jpeg",
                        Description = "Front cover",
                    },
                };
            }
            tl.Save();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
