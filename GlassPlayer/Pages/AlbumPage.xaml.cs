using System.ComponentModel;
using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GlassPlayer.Pages;

public sealed partial class AlbumPage : Page
{
    private readonly PlayerService _ps = PlayerService.Instance;
    private AlbumInfo? _album;
    private string _key = "";

    public AlbumPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _key = e.Parameter as string ?? "";
        _ps.LibraryChanged += Load;
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _ps.LibraryChanged -= Load;
        if (_album != null) _album.PropertyChanged -= Album_PropertyChanged;
    }

    private void Load()
    {
        if (_album != null) _album.PropertyChanged -= Album_PropertyChanged;
        _album = _ps.FindAlbum(_key);
        if (_album == null) { AlbumTitle.Text = "Album not found"; return; }
        _album.PropertyChanged += Album_PropertyChanged;

        AlbumTitle.Text = _album.Name;
        ArtistLink.Content = _album.Artist;
        AlbumDetails.Text = (_album.Year > 0 ? $"{_album.Year} · " : "") + _album.Details;
        TrackList.ItemsSource = _album.Tracks;
        ShowArt();
    }

    private void Album_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlbumInfo.ArtPath)) ShowArt();
    }

    private void ShowArt()
    {
        CoverImage.Source = _album?.LargeArt;
        HeaderBg.Source = _album?.Art;
    }

    private void TrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_album != null && e.ClickedItem is Track t) _ps.PlayList(_album.Tracks, _album.Tracks.IndexOf(t));
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_album != null) _ps.PlayList(_album.Tracks, 0);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        if (_album != null) _ps.PlayList(_album.Tracks, 0, shuffle: true);
    }

    private void AddQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_album == null) return;
        foreach (var t in _album.Tracks) _ps.AddToQueue(t);
    }

    private void ArtistLink_Click(object sender, RoutedEventArgs e)
    {
        if (_album != null) MainWindow.Current.NavigateTo(typeof(ArtistPage), _album.Artist);
    }
}
