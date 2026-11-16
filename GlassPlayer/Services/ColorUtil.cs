using System.Globalization;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace GlassPlayer.Services;

public static class ColorUtil
{
    public static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>A representative colour for an image: average weighted towards saturated pixels.</summary>
    public static async Task<Color?> FromImageAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform { ScaledWidth = 32, ScaledHeight = 32, InterpolationMode = BitmapInterpolationMode.Fant };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var px = data.DetachPixelData();

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                double pb = px[i], pg = px[i + 1], pr = px[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double sat = max == 0 ? 0 : (max - min) / max;
                double w = 0.05 + sat * sat; // vivid pixels count much more than greys
                r += pr * w; g += pg * w; b += pb * w; total += w;
            }
            if (total == 0) return null;
            return Color.FromArgb(255, (byte)(r / total), (byte)(g / total), (byte)(b / total));
        }
        catch
        {
            return null;
        }
    }
}
