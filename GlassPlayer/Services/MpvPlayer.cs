using System.Runtime.InteropServices;
using System.Text;

namespace GlassPlayer.Services;

/// <summary>
/// Minimal libmpv wrapper (libmpv-2.dll next to the exe). Video renders into a child HWND we give it ("wid").
/// mpv's own on-screen controller handles controls in full screen. Events arrive on a background thread.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    private const string Lib = "libmpv-2.dll";
    private IntPtr _ctx;
    private Thread? _events;
    private volatile bool _disposed;

    /// <summary>File played to the end (not stopped/replaced).</summary>
    public event Action? Ended;
    public event Action? FileLoaded;
    public event Action<string>? Error;
    /// <summary>"script-message glassplayer &lt;command&gt;" from our input.conf bindings.</summary>
    public event Action<string>? Command;

    public static bool IsAvailable()
    {
        try { return NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, Lib), out _); }
        catch { return false; }
    }

    public MpvPlayer(IntPtr videoHwnd, string inputConfPath)
    {
        _ctx = mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");

        SetOption("wid", videoHwnd.ToInt64().ToString());
        SetOption("vo", "gpu");
        SetOption("hwdec", "auto-safe");
        SetOption("keep-open", "no");
        SetOption("idle", "yes");
        SetOption("force-window", "yes");
        SetOption("osc", "yes");
        SetOption("input-default-bindings", "yes");
        SetOption("input-vo-keyboard", "yes");
        SetOption("input-conf", inputConfPath);
        SetOption("sub-auto", "fuzzy");          // movie.srt, movie.en.srt, ...
        SetOption("audio-file-auto", "no");
        SetOption("volume-max", "100");
        SetOption("background", "#000000");
        SetOption("terminal", "no");

        Check(mpv_initialize(_ctx), "mpv_initialize");

        _events = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _events.Start();
    }

    // ---------------- control ----------------

    public void Load(string path, double startSeconds)
    {
        SetProperty("start", startSeconds > 0 ? startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "0");
        Command_("loadfile", path, "replace");
        SetProperty("pause", "no");
    }

    public void Stop() => Command_("stop");
    public void Play() => SetProperty("pause", "no");
    public void Pause() => SetProperty("pause", "yes");
    public bool HasFile => GetString("path") is { Length: > 0 };
    public bool IsPaused => GetFlag("pause");
    public bool IsPlaying => HasFile && !IsPaused && !GetFlag("idle-active");
    public double Position => GetDouble("time-pos");
    public double Duration => GetDouble("duration");
    public void Seek(double seconds) => Command_("seek", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "absolute");
    public void SetVolume(double v01) => SetProperty("volume", ((int)Math.Round(Math.Clamp(v01, 0, 1) * 100)).ToString());
    public void SetMute(bool muted) => SetProperty("mute", muted ? "yes" : "no");

    public record TrackInfo(string Type, int Id, string Label, bool Selected);

    public List<TrackInfo> Tracks()
    {
        var list = new List<TrackInfo>();
        if (!int.TryParse(GetString("track-list/count"), out var n)) return list;
        for (int i = 0; i < n; i++)
        {
            var type = GetString($"track-list/{i}/type") ?? "";
            if (!int.TryParse(GetString($"track-list/{i}/id"), out var id)) continue;
            var title = GetString($"track-list/{i}/title");
            var lang = GetString($"track-list/{i}/lang");
            var codec = GetString($"track-list/{i}/codec");
            var external = GetString($"track-list/{i}/external") == "yes";
            var label = string.Join(" · ", new[] { title, lang?.ToUpperInvariant(), codec, external ? "file" : null }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            if (label.Length == 0) label = $"Track {id}";
            list.Add(new TrackInfo(type, id, label, GetString($"track-list/{i}/selected") == "yes"));
        }
        return list;
    }

    public void SelectSubtitle(int? id) => SetProperty("sid", id?.ToString() ?? "no");
    public void SelectAudio(int id) => SetProperty("aid", id.ToString());

    // ---------------- events ----------------

    private void EventLoop()
    {
        var ctx = _ctx;
        while (!_disposed)
        {
            var evPtr = mpv_wait_event(ctx, -1);
            if (_disposed) return;
            if (evPtr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<mpv_event>(evPtr);
            switch (ev.event_id)
            {
                case 1: // MPV_EVENT_SHUTDOWN
                    return;
                case 7: // MPV_EVENT_END_FILE
                    if (ev.data != IntPtr.Zero)
                    {
                        var end = Marshal.PtrToStructure<mpv_event_end_file>(ev.data);
                        if (end.reason == 0) Ended?.Invoke();                       // EOF
                        else if (end.reason == 4) Error?.Invoke(ErrorString(end.error)); // ERROR
                    }
                    break;
                case 8: // MPV_EVENT_FILE_LOADED
                    FileLoaded?.Invoke();
                    break;
                case 16: // MPV_EVENT_CLIENT_MESSAGE
                    if (ev.data != IntPtr.Zero)
                    {
                        var msg = Marshal.PtrToStructure<mpv_event_client_message>(ev.data);
                        if (msg.num_args >= 2)
                        {
                            var a0 = Utf8(Marshal.ReadIntPtr(msg.args, 0));
                            var a1 = Utf8(Marshal.ReadIntPtr(msg.args, IntPtr.Size));
                            if (a0 == "glassplayer" && a1 != null) Command?.Invoke(a1);
                        }
                    }
                    break;
            }
        }
    }

    // ---------------- helpers ----------------

    private void SetOption(string name, string value) => mpv_set_option_string(_ctx, U8(name), U8(value));

    private void SetProperty(string name, string value)
    {
        if (_ctx != IntPtr.Zero && !_disposed) mpv_set_property_string(_ctx, U8(name), U8(value));
    }

    private string? GetString(string name)
    {
        if (_ctx == IntPtr.Zero || _disposed) return null;
        var p = mpv_get_property_string(_ctx, U8(name));
        if (p == IntPtr.Zero) return null;
        var s = Utf8(p);
        mpv_free(p);
        return s;
    }

    private double GetDouble(string name)
    {
        if (_ctx == IntPtr.Zero || _disposed) return 0;
        double v = 0;
        return mpv_get_property(_ctx, U8(name), 5 /*MPV_FORMAT_DOUBLE*/, ref v) >= 0 ? v : 0;
    }

    private bool GetFlag(string name)
    {
        if (_ctx == IntPtr.Zero || _disposed) return false;
        int v = 0;
        return mpv_get_property_flag(_ctx, U8(name), 3 /*MPV_FORMAT_FLAG*/, ref v) >= 0 && v != 0;
    }

    private void Command_(params string[] args)
    {
        if (_ctx == IntPtr.Zero || _disposed) return;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                var bytes = U8(args[i]);
                ptrs[i] = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, ptrs[i], bytes.Length);
            }
            ptrs[args.Length] = IntPtr.Zero;
            mpv_command(_ctx, ptrs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++) if (ptrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(ptrs[i]);
        }
    }

    private static byte[] U8(string s) => Encoding.UTF8.GetBytes(s + "\0");
    private static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
    private static string ErrorString(int code) => Utf8(mpv_error_string(code)) ?? $"error {code}";

    private static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"{what} failed: {ErrorString(result)}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var ctx = _ctx;
        if (ctx == IntPtr.Zero) return;
        mpv_wakeup(ctx);          // let the event thread notice _disposed and leave
        _events?.Join(1000);
        _ctx = IntPtr.Zero;
        mpv_terminate_destroy(ctx);
    }

    // ---------------- interop ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct mpv_event
    {
        public int event_id;
        public int error;
        public ulong reply_userdata;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct mpv_event_end_file
    {
        public int reason;
        public int error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct mpv_event_client_message
    {
        public int num_args;
        public IntPtr args;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")] private static extern int mpv_get_property(IntPtr ctx, byte[] name, int format, ref double data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")] private static extern int mpv_get_property_flag(IntPtr ctx, byte[] name, int format, ref int data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, IntPtr[] args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_wakeup(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_free(IntPtr data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_error_string(int error);
}
