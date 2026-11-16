using GlassPlayer.Models;
using GlassPlayer.Services;
using Microsoft.UI.Xaml.Controls;

namespace GlassPlayer.Pages;

/// <summary>"Add to playlist" menu items, used by every right-click menu and the player bar.</summary>
public static class PlaylistMenu
{
    private static PlayerService Ps => PlayerService.Instance;

    public static MenuFlyoutSubItem SubItem(IReadOnlyList<Track> tracks)
    {
        var sub = new MenuFlyoutSubItem { Text = "Add to playlist", Icon = new FontIcon { Glyph = "" } };
        foreach (var item in Items(tracks)) sub.Items.Add(item);
        return sub;
    }

    public static MenuFlyout Flyout(IReadOnlyList<Track> tracks)
    {
        var menu = new MenuFlyout();
        foreach (var item in Items(tracks)) menu.Items.Add(item);
        return menu;
    }

    private static IEnumerable<MenuFlyoutItemBase> Items(IReadOnlyList<Track> tracks)
    {
        var create = new MenuFlyoutItem { Text = "New playlist…", Icon = new FontIcon { Glyph = "" } };
        create.Click += async (_, _) =>
        {
            var name = await MainWindow.Current.PromptAsync("New playlist", "Playlist name",
                tracks.Count == 1 ? "" : tracks.Select(t => t.Album).FirstOrDefault(a => a.Length > 0) ?? "");
            if (name != null) Ps.Playlists.Create(name, tracks.Select(t => t.Path));
        };
        yield return create;

        var lists = Ps.Playlists.All.OrderByDescending(p => p.Modified).ToList();
        if (lists.Count > 0) yield return new MenuFlyoutSeparator();
        foreach (var p in lists.Take(20))
        {
            var item = new MenuFlyoutItem { Text = p.Name };
            item.Click += (_, _) => Ps.Playlists.Add(p, tracks.Select(t => t.Path));
            yield return item;
        }
    }
}
