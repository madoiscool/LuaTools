using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Downloads + launches CloudRedirect.exe. The user-facing CloudRedirect GUI manager. The
/// exe is fetched once from the latest GitHub release (via <see cref="GithubProxy"/>, so it works in blocked
/// regions) and cached under %AppData%\LuaToolsGui\cloudredirect. It self-updates, so we always grab the
/// latest asset and don't track versions. The Mode page's "Manage" button (shown when CloudRedirect is the
/// active mode) calls <see cref="LaunchAsync"/>, which opens its window visibly and returns immediately.
/// (Separate from the silent CloudRedirectCLI.exe the mode-install flow runs.)
/// </summary>
public class CloudRedirectService(GithubProxy gh)
{
    private static readonly string ToolDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "cloudredirect");
    private static string ExePath => Path.Combine(ToolDir, "CloudRedirect.exe");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Ensure CloudRedirect.exe is on disk (downloads the latest release asset once). Returns its
    /// path, or null if it couldn't be obtained.</summary>
    public async Task<string?> EnsureToolAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (File.Exists(ExePath)) return ExePath;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(ExePath)) return ExePath; // won the race

            string url = $"https://api.github.com/repos/{AppConfig.CloudRedirectRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a => a.Name.Equals("CloudRedirect.exe", StringComparison.OrdinalIgnoreCase));
            if (asset is null) return null;

            Directory.CreateDirectory(ToolDir);
            await gh.DownloadAsync(asset.DownloadUrl, ExePath, progress, ct); // single exe, no zip to extract

            return File.Exists(ExePath) ? ExePath : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { _gate.Release(); }
    }

    /// <summary>Download (if needed), and launch the CloudRedirect GUI. Fire-and-forget. Its window opens and
    /// we don't wait for it. Returns false if the tool couldn't be downloaded or started.</summary>
    public async Task<bool> LaunchAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        string? exe = await EnsureToolAsync(progress, ct);
        if (exe is null) return false;

        try
        {
            // GUI app: show its window (UseShellExecute) and don't block our app on it.
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = ToolDir,
            });
            return true;
        }
        catch { return false; }
    }
}
