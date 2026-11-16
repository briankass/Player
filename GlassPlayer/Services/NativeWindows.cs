using System.Runtime.InteropServices;

namespace GlassPlayer.Services;

/// <summary>
/// Win32 bits for libmpv video: a plain child window mpv renders into (placed over the XAML video area),
/// and native popup menus (XAML flyouts would be hidden behind that child window).
/// </summary>
public static class NativeWindows
{
    private const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    private const int SS_BLACKRECT = 0x4;
    private const int SW_HIDE = 0, SW_SHOWNA = 8;
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    public static IntPtr CreateVideoChild(IntPtr parent) =>
        CreateWindowEx(0, "Static", "", WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | SS_BLACKRECT,
            0, 0, 1, 1, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    public static void Place(IntPtr hwnd, int x, int y, int w, int h) =>
        SetWindowPos(hwnd, IntPtr.Zero /*HWND_TOP*/, x, y, Math.Max(1, w), Math.Max(1, h), SWP_NOACTIVATE | SWP_SHOWWINDOW);

    public static void Hide(IntPtr hwnd) => ShowWindow(hwnd, SW_HIDE);

    /// <summary>Shows a menu at the mouse position. Returns the chosen index, or -1.</summary>
    public static int ShowMenu(IntPtr owner, IReadOnlyList<(string Text, bool Checked, bool Enabled)> items)
    {
        var menu = CreatePopupMenu();
        for (int i = 0; i < items.Count; i++)
        {
            var (text, chk, enabled) = items[i];
            if (text == "-") { AppendMenu(menu, 0x800, UIntPtr.Zero, null); continue; }
            uint flags = (chk ? 0x8u : 0u) | (enabled ? 0u : 0x1u); // MF_CHECKED, MF_GRAYED
            AppendMenu(menu, flags, (UIntPtr)(i + 1), text);
        }
        GetCursorPos(out var pt);
        int cmd = TrackPopupMenuEx(menu, 0x0100 /*TPM_RETURNCMD*/, pt.X, pt.Y, owner, IntPtr.Zero);
        DestroyMenu(menu);
        return cmd - 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);
}
