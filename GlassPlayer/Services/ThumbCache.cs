using System.Security.Cryptography;
using System.Text;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace GlassPlayer.Services;

/// <summary>Video thumbnails from the Windows shell, cached in %LOCALAPPDATA%\GlassPlayer\thumbs.</summary>
public static class ThumbCache
{
    private static string Dir
    {
        get
        {
            var d = Path.Combine(AppSettings.DataDir, "thumbs");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string Base(string videoPath) =>
        Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(videoPath.ToLowerInvariant()))));

    public static string? Existing(string videoPath)
    {
        var p = Base(videoPath) + ".jpg";
        return File.Exists(p) ? p : null;
    }

    public static bool KnownMissing(string videoPath) => File.Exists(Base(videoPath) + ".none");

    public static async Task<string?> CreateAsync(string videoPath)
    {
        var existing = Existing(videoPath);
        if (existing != null) return existing;
        var target = Base(videoPath) + ".jpg";
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(videoPath);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 480, ThumbnailOptions.ResizeThumbnail);
            if (thumb != null && thumb.Type == ThumbnailType.Image)
            {
                using var input = thumb.AsStreamForRead();
                using var output = File.Create(target);
                await input.CopyToAsync(output);
                return target;
            }
        }
        catch { }
        try { File.WriteAllText(Base(videoPath) + ".none", ""); } catch { }
        return null;
    }
}
