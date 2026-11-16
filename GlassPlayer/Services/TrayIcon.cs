using System.Runtime.InteropServices;

namespace GlassPlayer.Services;

/// <summary>
/// Notification-area (system tray) icon using Shell_NotifyIcon. Messages arrive through a subclass of the
/// main window's HWND. Left click -> Activated; right click -> context menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public const int CmdShow = 1, CmdPlayPause = 2, CmdNext = 3, CmdPrevious = 4, CmdExit = 5;

    private const int WM_APP_TRAY = 0x8000 + 77; // WM_APP + 77
    private const int WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly SubclassProc _proc; // keep the delegate alive
    private bool _visible;
    private string _tip = "Glass Player";

    public event Action? Activated;
    public event Action<int>? Command;
    /// <summary>Supplies the label for the play/pause menu item.</summary>
    public Func<bool>? IsPlaying;

    public TrayIcon(IntPtr hwnd, string iconPath)
    {
        _hwnd = hwnd;
        _icon = LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, 16, 16, 0x10 /*LR_LOADFROMFILE*/);
        if (_icon == IntPtr.Zero) _icon = LoadIcon(IntPtr.Zero, (IntPtr)32512 /*IDI_APPLICATION*/);
        _proc = WndProc;
        SetWindowSubclass(_hwnd, _proc, (UIntPtr)0x4750, UIntPtr.Zero);
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (value == _visible) return;
            var data = Data();
            Shell_NotifyIcon(value ? NIM_ADD : NIM_DELETE, ref data);
            _visible = value;
        }
    }

    public void SetTooltip(string text)
    {
        _tip = text.Length > 120 ? text[..120] : text;
        if (!_visible) return;
        var data = Data();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = (int)((long)lParam & 0xFFFF);
            if (ev == WM_LBUTTONUP || ev == WM_LBUTTONDBLCLK) Activated?.Invoke();
            else if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU) ShowMenu();
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, (UIntPtr)CmdShow, "Show Glass Player");
        AppendMenu(menu, 0x800 /*MF_SEPARATOR*/, UIntPtr.Zero, null);
        AppendMenu(menu, 0, (UIntPtr)CmdPlayPause, IsPlaying?.Invoke() == true ? "Pause" : "Play");
        AppendMenu(menu, 0, (UIntPtr)CmdNext, "Next");
        AppendMenu(menu, 0, (UIntPtr)CmdPrevious, "Previous");
        AppendMenu(menu, 0x800, UIntPtr.Zero, null);
        AppendMenu(menu, 0, (UIntPtr)CmdExit, "Exit");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
        int cmd = TrackPopupMenuEx(menu, 0x0100 /*TPM_RETURNCMD*/ | 0x0020 /*TPM_BOTTOMALIGN*/, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd != 0) Command?.Invoke(cmd);
    }

    public void Dispose()
    {
        Visible = false;
        RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)0x4750);
    }

    // ---------------- interop ----------------

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInst, IntPtr name);

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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
