using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public sealed partial class ArtistPage : Page
{
    private readonly PlayerService _ps = PlayerService.Instance;
    private ArtistInfo? _artist;
    private string _name = "";

    public ArtistPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _name = e.Parameter as string ?? "";
        _ps.LibraryChanged += Load;
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ps.LibraryChanged -= Load;
        if (_artist != null) _artist.PropertyChanged -= Artist_PropertyChanged;
    }

    private void Load()
    {
        if (_artist != null) _artist.PropertyChanged -= Artist_PropertyChanged;
        _artist = _ps.FindArtist(_name);
        ArtistName.Text = _artist?.Name ?? _name;
        if (_artist == null) { ArtistDetails.Text = "Not in your library"; return; }
        _artist.PropertyChanged += Artist_PropertyChanged;

        var songs = _artist.Albums.Sum(a => a.Tracks.Count);
        ArtistDetails.Text = $"{_artist.Subtitle} · {songs} songs";
        InitialText.Text = _artist.Initial;
        AlbumsGrid.ItemsSource = _artist.Albums;
        ShowArt();
    }

    private void Artist_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ShowArt();

    private void ShowArt()
    {
        ArtistImage.Source = _artist?.Art;
        HeaderBg.Source = _artist?.Art;
    }

    private List<Track> AllTracks() => _artist?.Albums.SelectMany(a => a.Tracks).ToList() ?? new();

    private void Play_Click(object sender, RoutedEventArgs e) => _ps.PlayList(AllTracks(), 0);
    private void Shuffle_Click(object sender, RoutedEventArgs e) => _ps.PlayList(AllTracks(), 0, shuffle: true);

    private void FixMetadata_Click(object sender, RoutedEventArgs e)
    {
        var tracks = AllTracks();
        if (tracks.Count > 0) MainWindow.Current.OpenMetadataFixer(tracks.Select(t => t.Path).ToList());
    }

    private void AlbumsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumInfo a) MainWindow.Current.NavigateTo(typeof(AlbumPage), a.Key);
    }
}
