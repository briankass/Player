using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GlassPlayer;

/// <summary>Search MusicBrainz by hand: pick the right song, or the right album for a whole folder.</summary>
public sealed partial class MatchDialog : ContentDialog
{
    private readonly MetadataService _meta;
    private readonly int _albumFileCount;
    private CancellationTokenSource? _cts;

    public bool AlbumMode { get; private set; }
    /// <summary>True when the user chose a result (button or double-click).</summary>
    public bool Accepted { get; private set; }
    public MetadataMatch? SelectedSong { get; private set; }
    public ReleaseCandidate? SelectedRelease { get; private set; }

    public MatchDialog(MetadataService meta, FixItem item, int albumFileCount, bool startInAlbumMode = false)
    {
        InitializeComponent();
        _meta = meta;
        _albumFileCount = albumFileCount;
        FileText.Text = item.Path;
        ArtistBox.Text = item.HintArtist;
        TitleBox.Text = item.HintTitle;
        AlbumBox.Text = item.HintAlbum;
        Closed += (_, _) => _cts?.Cancel();
        PrimaryButtonClick += (_, _) => Accepted = true;
        Opened += (_, _) =>
        {
            if (startInAlbumMode) SelectMode("album");
            else UpdateModeUi();
            _ = SearchAsync();
        };
    }

    private void SelectMode(string tag)
    {
        foreach (var i in ModeBar.Items)
            if ((i.Tag as string) == tag) { ModeBar.SelectedItem = i; break; }
    }

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Results == null) return; // during InitializeComponent
        UpdateModeUi();
        _ = SearchAsync();
    }

    private void UpdateModeUi()
    {
        AlbumMode = (ModeBar.SelectedItem?.Tag as string) == "album";
        TitleBox.Visibility = AlbumMode ? Visibility.Collapsed : Visibility.Visible;
        AlbumBox.Header = AlbumMode ? "Album" : "Album (optional)";
        ApplyNote.Text = AlbumMode
            ? $"The album's track list is matched to the {_albumFileCount} file(s) in this folder by track number or title, and title, artist, album, year, track number and cover are filled in for each. You'll see the changes before anything is written."
            : "The chosen song's title, artist, album, year, track number and cover are proposed for this file. You'll see the changes before anything is written.";
        Results.ItemsSource = null;
        IsPrimaryButtonEnabled = false;
    }

    private void Box_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { _ = SearchAsync(); e.Handled = true; }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private async Task SearchAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Busy.IsActive = true;
        StatusText.Text = "Searching MusicBrainz…";
        Results.ItemsSource = null;
        IsPrimaryButtonEnabled = false;
        try
        {
            var items = new List<Candidate>();
            if (AlbumMode)
            {
                var releases = await _meta.SearchReleasesAsync(ArtistBox.Text.Trim(), AlbumBox.Text.Trim(), ct);
                foreach (var r in releases)
                    items.Add(new Candidate
                    {
                        Line1 = r.Title,
                        Line2 = r.Artist,
                        Line3 = string.Join(" · ", new[]
                        {
                            r.Year > 0 ? r.Year.ToString() : "",
                            r.Type,
                            r.Format,
                            r.TrackCount > 0 ? $"{r.TrackCount} tracks" : "",
                            r.Country,
                            $"match {r.Score}%",
                        }.Where(s => s.Length > 0)),
                        CoverUrl = MetadataService.CoverThumbUrl(r.Id),
                        Payload = r,
                    });
            }
            else
            {
                var songs = await _meta.SearchRecordingsAsync(ArtistBox.Text.Trim(), TitleBox.Text.Trim(), AlbumBox.Text.Trim(), ct);
                foreach (var m in songs)
                    items.Add(new Candidate
                    {
                        Line1 = m.Title,
                        Line2 = m.Artist,
                        Line3 = string.Join(" · ", new[]
                        {
                            m.Album,
                            m.Year > 0 ? m.Year.ToString() : "",
                            m.TrackNumber > 0 ? $"track {m.TrackNumber}" : "",
                            m.LengthSeconds > 0 ? Track.FormatTime(m.LengthSeconds) : "",
                            $"match {m.Score}%",
                        }.Where(s => s.Length > 0)),
                        CoverUrl = string.IsNullOrEmpty(m.ReleaseId) ? null : MetadataService.CoverThumbUrl(m.ReleaseId),
                        Payload = m,
                    });
            }
            if (ct.IsCancellationRequested) return;
            Results.ItemsSource = items;
            StatusText.Text = items.Count == 0
                ? "Nothing found. Try fewer words, or check the spelling."
                : $"{items.Count} result(s). Pick one and click Use this.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "Search failed: " + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) Busy.IsActive = false;
        }
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var c = Results.SelectedItem as Candidate;
        SelectedSong = c?.Payload as MetadataMatch;
        SelectedRelease = c?.Payload as ReleaseCandidate;
        IsPrimaryButtonEnabled = c != null;
    }

    private void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Results.SelectedItem == null) return;
        Accepted = true;
        Hide();
    }
}
