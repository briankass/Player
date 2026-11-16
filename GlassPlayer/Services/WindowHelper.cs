using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace GlassPlayer.Services;

public static class WindowHelper
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Display scale for the window (1.0 at 100%, 1.5 at 150%...).</summary>
    public static double Scale(Window window)
    {
        try
        {
            var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>AppWindow.Resize takes physical pixels; this takes the same units as XAML (scaled for DPI).</summary>
    public static void ResizeLogical(Window window, int width, int height, bool center = true)
    {
        var s = Scale(window);
        int w = (int)Math.Round(width * s), h = (int)Math.Round(height * s);
        try
        {
            // never bigger than 90% of the screen it's on
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                window.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            w = Math.Min(w, (int)(area.Width * 0.9));
            h = Math.Min(h, (int)(area.Height * 0.9));
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            if (center)
                window.AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));
        }
        catch
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        }
    }
}
