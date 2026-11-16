using Microsoft.Win32;

namespace GlassPlayer.Services;

/// <summary>"Start with Windows" via the per-user Run key (no admin needed).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GlassPlayer";
    public const string MinimizedArg = "--minimized";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GlassPlayer.exe");
            key.SetValue(ValueName, $"\"{exe}\" {MinimizedArg}");
        }
        else if (key.GetValue(ValueName) != null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Keep the Run entry pointing at the current exe (the folder may have moved).</summary>
    public static void Refresh()
    {
        if (IsEnabled) SetEnabled(true);
    }

    public static bool LaunchedMinimized() =>
        Environment.GetCommandLineArgs().Any(a => a.Equals(MinimizedArg, StringComparison.OrdinalIgnoreCase));
}
