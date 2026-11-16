using System.Security.Cryptography;
using System.Text;
using GlassPlayer.Models;

namespace GlassPlayer.Services;

/// <summary>
/// Caches one cover image per album in %LOCALAPPDATA%\GlassPlayer\art, taken from embedded tags
/// or a cover.jpg/folder.jpg next to the files. A ".none" marker remembers albums with no art.
/// </summary>
public static class ArtCache
{
    private static readonly string[] FolderImages =
        { "cover.jpg", "folder.jpg", "front.jpg", "album.jpg", "cover.png", "folder.png", "front.png" };

    private static string Dir
    {
        get
        {
            var d = Path.Combine(AppSettings.DataDir, "art");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string Base(string key) =>
        Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))));

    public static string? Existing(string key)
    {
        var p = Base(key) + ".img";
        return File.Exists(p) ? p : null;
    }

    public static bool KnownMissing(string key) => File.Exists(Base(key) + ".none");

    public static void Invalidate(string key)
    {
        try { File.Delete(Base(key) + ".img"); } catch { }
        try { File.Delete(Base(key) + ".none"); } catch { }
    }

    /// <summary>Find art for the album and cache it. Slow (reads files, maybe over the network): call off the UI thread.</summary>
    public static string? Extract(string key, IReadOnlyList<Track> tracks)
    {
        var existing = Existing(key);
        if (existing != null) return existing;
        var target = Base(key) + ".img";

        foreach (var t in tracks.Where(t => t.HasArt).Take(3))
        {
            try
            {
                using var tl = TagLib.File.Create(t.Path);
                if (tl.Tag.Pictures.Length == 0) continue;
                var pic = tl.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? tl.Tag.Pictures[0];
                File.WriteAllBytes(target, pic.Data.Data);
                return target;
            }
            catch { }
        }

        var dir = tracks.Count > 0 ? Path.GetDirectoryName(tracks[0].Path) : null;
        if (dir != null)
        {
            foreach (var name in FolderImages)
            {
                try
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) { File.Copy(p, target, true); return target; }
                }
                catch { }
            }
        }

        try { File.WriteAllText(Base(key) + ".none", ""); } catch { }
        return null;
    }
}
