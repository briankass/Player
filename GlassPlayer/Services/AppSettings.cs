using System.Text.Json;

namespace GlassPlayer.Services;

public class AppSettings
{
    public string? LibraryFolder { get; set; }
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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
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
    public int Effect { get; set; } = 2;           // 0 Mica, 1 Mica Alt, 2 Acrylic, 3 Acrylic thin, 4 Solid
    public int TintMode { get; set; } = 0;         // 0 default, 1 album art, 2 custom
    public string CustomTint { get; set; } = "#2B4F81";
    public double TintOpacity { get; set; } = 0.2;
    public double Luminosity { get; set; } = 0.55;
    public double SurfaceOpacity { get; set; } = 0.3;
    public bool KeepActive { get; set; } = true;
}
