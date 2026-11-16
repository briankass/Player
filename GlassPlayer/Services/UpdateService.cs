using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace GlassPlayer.Services;

public class UpdateInfo
{
    public Version Latest { get; set; } = new(0, 0, 0);
    public string Tag { get; set; } = "";
    public string AssetApiUrl { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>
/// Checks GitHub Releases for a newer version, downloads the zip, and swaps the files in place
/// via a small helper script that runs after this process exits.
/// For a PRIVATE repo, put a read-only GitHub token (fine-grained, Contents: read) in
/// %LOCALAPPDATA%\GlassPlayer\github_token.txt. Public repos need nothing.
/// </summary>
public class UpdateService
{
    public const string Owner = "briankass";
    public const string Repo = "player";
    public const string AssetName = "GlassPlayer-win-x64.zip";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    private static string? Token()
    {
        var env = Environment.GetEnvironmentVariable("GLASSPLAYER_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlassPlayer", "github_token.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static HttpRequestMessage Request(string url, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("GlassPlayer-Updater");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = Token();
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>Returns info about the latest release, or null if none/unreachable. Check .Latest vs CurrentVersion.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.SendAsync(
            Request($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", "application/vnd.github+json"), ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub returned {(int)resp.StatusCode}. If the repo is private, add a token (see UpdateService.cs).");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var info = new UpdateInfo
        {
            Tag = tag,
            Notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
        };
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;
        info.Latest = ver;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == AssetName)
                info.AssetApiUrl = asset.GetProperty("url").GetString() ?? "";
        }
        return string.IsNullOrEmpty(info.AssetApiUrl) ? null : info;
    }

    public bool IsNewer(UpdateInfo info) => info.Latest > CurrentVersion;

    /// <summary>Downloads and stages the update, launches the swap script, and returns true if the app should exit now.</summary>
    public async Task<bool> DownloadAndApplyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "GlassPlayerUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, AssetName);
        var stage = Path.Combine(work, "new");

        using (var resp = await Http.SendAsync(Request(info.AssetApiUrl, "application/octet-stream"),
                   HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        ZipFile.ExtractToDirectory(zipPath, stage, overwriteFiles: true);

        var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exe = Path.Combine(appDir, "GlassPlayer.exe");
        var pid = Environment.ProcessId;
        var script = Path.Combine(work, "update.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            $":wait\r\ntasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL\r\n" +
            "if not errorlevel 1 (timeout /t 1 /nobreak >NUL & goto wait)\r\n" +
            $"robocopy \"{stage}\" \"{appDir}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS >NUL\r\n" +
            $"start \"\" \"{exe}\"\r\n" +
            $"rmdir /S /Q \"{work}\"\r\n");

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        return true;
    }
}
