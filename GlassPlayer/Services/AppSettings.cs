using System.Text.Json;

namespace GlassPlayer.Services;

public class AppSettings
{
    public string? LibraryFolder { get; set; } // legacy single folder, migrated into MusicFolders
    public List<string> MusicFolders { get; set; } = new();
    public List<string> VideoFolders { get; set; } = new();
    public Dictionary<string, double> VideoPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Backdrop { get; set; } // 0 Mica, 1 Mica Alt, 2 Acrylic
    public double Volume { get; set; } = 0.8;
    public List<string> Recent { get; set; } = new(); // album keys, most recent first
    public GlassSettings Glass { get; set; } = new();
    public PlaybackSettings Playback { get; set; } = new();
    public bool StartMinimized { get; set; } = true;   // when launched by "Start with Windows"
    public bool MinimizeToTray { get; set; }
    public bool CloseToTray { get; set; }

    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                if (s.MusicFolders.Count == 0 && !string.IsNullOrEmpty(s.LibraryFolder))
                    s.MusicFolders.Add(s.LibraryFolder);
                s.LibraryFolder = null;
                s.VideoPositions = new Dictionary<string, double>(s.VideoPositions, StringComparer.OrdinalIgnoreCase);
                return s;
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); } catch { }
    }
}

public class GlassSettings
{
    public int Effect { get; set; } = 6;           // 0 Mica, 1 Mica Alt, 2 Acrylic, 3 Acrylic thin, 4 Solid, 5 Blur glass, 6 Clear glass
    public int TintMode { get; set; } = 0;         // 0 default, 1 album art, 2 custom
    public string CustomTint { get; set; } = "#2B4F81";
    public double TintOpacity { get; set; } = 0.5;
    public double Luminosity { get; set; } = 0.5;  // "frosting"
    public double SurfaceOpacity { get; set; } = 0.5;
    public bool KeepActive { get; set; } = true;
}

public class PlaybackSettings
{
    public bool Muted { get; set; }
    public bool CrossfadeEnabled { get; set; }
    public double CrossfadeSeconds { get; set; } = 5;
    public bool EqEnabled { get; set; }
    public string EqPreset { get; set; } = "Flat";
    public double[] EqGains { get; set; } = new double[10];

    // gains in dB for 31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz
    public static readonly Dictionary<string, double[]> Presets = new()
    {
        ["Flat"] = new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        ["Bass boost"] = new double[] { 7, 6, 5, 3, 1, 0, 0, 0, 0, 0 },
        ["Treble boost"] = new double[] { 0, 0, 0, 0, 0, 1, 3, 5, 6, 7 },
        ["Rock"] = new double[] { 5, 4, 3, 1, -1, -1, 1, 3, 4, 5 },
        ["Pop"] = new double[] { -1, 0, 2, 4, 5, 4, 2, 0, -1, -1 },
        ["Jazz"] = new double[] { 4, 3, 1, 2, -1, -1, 0, 1, 3, 4 },
        ["Classical"] = new double[] { 5, 4, 3, 2, -1, -1, 0, 2, 3, 4 },
        ["Electronic"] = new double[] { 6, 5, 2, 0, -2, 2, 1, 2, 5, 6 },
        ["Vocal"] = new double[] { -2, -3, -2, 1, 4, 5, 4, 2, 0, -1 },
        ["Loudness"] = new double[] { 6, 4, 0, 0, -2, 0, -1, -4, 5, 2 },
    };
}
