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
