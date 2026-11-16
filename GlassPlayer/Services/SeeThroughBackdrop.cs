using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinComp = Windows.UI.Composition;

namespace GlassPlayer.Services;

/// <summary>
/// Real see-through backdrops that bypass Windows' Mica/Acrylic materials (which always add an opaque layer):
///  - Blur:  a host-backdrop brush, i.e. a live blur of whatever is behind the window
///  - Clear: a fully transparent window background (desktop shows through sharp)
/// Tint and frosting are drawn by the app on top, so 0% really means clear.
/// </summary>
public sealed class SeeThroughBackdrop : SystemBackdrop
{
    public bool Blur { get; }
    private readonly IntPtr _hwnd;
    private WinComp.CompositionBrush? _brush;
    private static WinComp.Compositor? s_compositor;

    public SeeThroughBackdrop(bool blur, IntPtr hwnd)
    {
        Blur = blur;
        _hwnd = hwnd;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        EnsureCompositor();
        if (!Blur) SetBlurBehind(_hwnd, true); // lets the window's transparent pixels show the desktop
        _brush = Blur
            ? s_compositor!.CreateHostBackdropBrush()
            : s_compositor!.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        connectedTarget.SystemBackdrop = _brush;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
        if (!Blur) SetBlurBehind(_hwnd, false);
    }

    // ---------- Windows.UI.Composition needs a Windows.System.DispatcherQueue on this thread ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;    // DQTYPE_THREAD_CURRENT = 2
        public int apartmentType; // DQTAT_COM_STA = 2
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    private static void EnsureCompositor()
    {
        if (s_compositor != null) return;
        if (Windows.System.DispatcherQueue.GetForCurrentThread() == null)
        {
            var o = new DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                threadType = 2,
                apartmentType = 2,
            };
            CreateDispatcherQueueController(o, out _); // intentionally kept alive for the app's lifetime
        }
        s_compositor = new WinComp.Compositor();
    }

    // ---------- DWM: make the window honour per-pixel transparency ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
        public IntPtr hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;
    }

    private const uint DWM_BB_ENABLE = 0x1;
    private const uint DWM_BB_BLURREGION = 0x2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blurBehind);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private static void SetBlurBehind(IntPtr hwnd, bool enable)
    {
        // A tiny off-window blur region: DWM composes the window with alpha but doesn't actually blur anything.
        var rgn = enable ? CreateRectRgn(-2, -2, -1, -1) : IntPtr.Zero;
        var bb = new DWM_BLURBEHIND
        {
            dwFlags = enable ? DWM_BB_ENABLE | DWM_BB_BLURREGION : DWM_BB_ENABLE,
            fEnable = enable,
            hRgnBlur = rgn,
        };
        DwmEnableBlurBehindWindow(hwnd, ref bb);
        if (rgn != IntPtr.Zero) DeleteObject(rgn);
    }
}
