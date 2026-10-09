using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Outcome of a Steamless run over a game's executables.</summary>
public record SteamlessResult(int Patched, int Unchanged, int Total, string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// Runs <c>Steamless.CLI.exe</c> (atom0s/Steamless) to strip SteamStub DRM from a game's executable(s).
/// The tool is downloaded once from GitHub (via <see cref="GithubProxy"/>, so it works in blocked regions)
/// and cached under %AppData%\LuaToolsGui\steamless. The target exe(s) are resolved precisely from Steam's
/// own launch config (<see cref="SteamDepotInfo"/> → config.launch), supplemented by a filtered recursive
/// scan so bootstrap launchers don't hide the game executable. Each patched exe is backed up to <c>&lt;exe&gt;.bak</c>
/// first. Steamless silently no-ops on exes without DRM, so over-selection is harmless.
/// </summary>
public class SteamlessService(GithubProxy gh, SteamLibraryService library, SteamDepotInfo depots, CacheService cache)
{
    private static readonly string ToolDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "steamless");
    private static string CliPath => Path.Combine(ToolDir, "Steamless.CLI.exe");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Redists/launchers/anti-cheat/etc. to ignore in the fallback folder scan (mirrors the reference script).
    private static readonly string[] SkipPatterns =
    [
        "redist", "vcredist", "directx", "dxsetup", "vc_redist", "dotnet", "setup", "install", "uninstall",
        "crashreport", "crashhandler", "bugsplat", "easyanticheat", "battleye", "be_service", "launch",
        "_launcher", "bootstrap", "cefprocess", "cef_process", "steamwebhelper", "uplay",
    ];

    private readonly SemaphoreSlim _toolGate = new(1, 1);

    /// <summary>How long an up-to-date check is trusted before we ask GitHub again.</summary>
    private static readonly TimeSpan ToolCheckInterval = TimeSpan.FromHours(6);

    private static bool CheckedRecently(long lastMs) =>
        lastMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastMs < (long)ToolCheckInterval.TotalMilliseconds;

    /// <summary>
    /// Ensure Steamless.CLI.exe is on disk and reasonably current. Null only if no usable tool exists.
    /// </summary>
    /// <remarks>
    /// Was a bare <c>File.Exists</c>, which pinned the first download forever. Now the installed release
    /// tag is recorded and re-checked at most every <see cref="ToolCheckInterval"/>.
    ///
    /// <para><b>A failed check never disables a working tool</b> — every failure path falls back to an
    /// existing <c>CliPath</c>, so being offline can't break DRM removal for someone who already has it.</para>
    /// </remarks>
    public async Task<string?> EnsureToolAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (File.Exists(CliPath) && CheckedRecently(cache.SteamlessCheckedAtMs)) return CliPath;

        await _toolGate.WaitAsync(ct);
        bool have = false;
        try
        {
            have = File.Exists(CliPath);
            if (have && CheckedRecently(cache.SteamlessCheckedAtMs)) return CliPath; // won the race

            // A failed lookup still counts as "we looked", so an offline run backs off instead of
            // retrying the whole GithubProxy mirror chain on the next call.
            void RecordAttempt() =>
                cache.SteamlessCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Latest release → the single distributable .zip asset.
            string url = $"https://api.github.com/repos/{AppConfig.SteamlessRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) { if (have) RecordAttempt(); return have ? CliPath : null; }

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset is null) { if (have) RecordAttempt(); return have ? CliPath : null; }

            // Already on the published build: record that we looked and skip the download.
            if (have && !string.IsNullOrEmpty(release!.TagName)
                     && string.Equals(release.TagName, cache.SteamlessVersion, StringComparison.Ordinal))
            {
                cache.SteamlessCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return CliPath;
            }

            Directory.CreateDirectory(ToolDir);
            string zipPath = Path.Combine(ToolDir, "steamless.zip");
            await gh.DownloadAsync(asset.DownloadUrl, zipPath, progress, ct);

            // Verify before overwriting a working install: this is an executable we then run.
            if (!AssetHash.Matches(zipPath, asset.Digest))
            {
                try { File.Delete(zipPath); } catch { }
                if (have) RecordAttempt();
                return have ? CliPath : null;
            }

            // Extract the WHOLE zip: the CLI needs its plugin DLLs alongside it.
            ZipFile.ExtractToDirectory(zipPath, ToolDir, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { /* leftover zip is harmless */ }

            if (!File.Exists(CliPath)) { if (have) RecordAttempt(); return have ? CliPath : null; }

            cache.SteamlessVersion = release!.TagName;
            cache.SteamlessCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return CliPath;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            if (have) cache.SteamlessCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return have ? CliPath : null;
        }
        finally { _toolGate.Release(); }
    }

    /// <summary>Strip SteamStub DRM from a game's executable(s). Best-effort; never throws out of the loop.</summary>
    public async Task<SteamlessResult> PatchGameAsync(long appId, IProgress<double?>? progress, CancellationToken ct = default)
    {
        string? installDir = library.GetInstallDir(appId);
        if (installDir is null)
            return new SteamlessResult(0, 0, 0, "no-install");

        var exes = await ResolveExesAsync(appId, installDir, ct);
        if (exes.Count == 0)
            return new SteamlessResult(0, 0, 0, "no-exe");

        // DRM-free launchers (and already unpacked games) need no work and aren't failed patches.
        exes = exes.Where(MayHaveSteamStub).ToList();
        if (exes.Count == 0)
            return new SteamlessResult(0, 0, 0, null);

        string? cli = await EnsureToolAsync(progress, ct);
        if (cli is null)
            return new SteamlessResult(0, 0, 0, "tool");

        int patched = 0, unchanged = 0;
        foreach (string exe in exes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RunCliAsync(cli, exe, ct);

                string unpacked = exe + ".unpacked.exe";
                if (File.Exists(unpacked))
                {
                    // Back the original up (only once, never clobber a known-good .bak), then swap in.
                    string bak = exe + ".bak";
                    if (!File.Exists(bak)) File.Copy(exe, bak);
                    File.Delete(exe);
                    File.Move(unpacked, exe);
                    patched++;
                }
                else
                {
                    unchanged++; // no SteamStub DRM, or Steamless declined. Leave it as-is
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                unchanged++;
                try { File.Delete(exe + ".unpacked.exe"); } catch { /* stray partial */ }
            }
        }
        return new SteamlessResult(patched, unchanged, exes.Count, null);
    }

    /// <summary>The exe(s) to patch: Steam's launch config plus a filtered folder scan.</summary>
    private async Task<IReadOnlyList<string>> ResolveExesAsync(long appId, string installDir, CancellationToken ct)
    {
        var info = await depots.GetAsync(appId, ct);
        return ResolveExes(installDir, info?.LaunchExes ?? []);
    }

    /// <summary>Test seam: resolve local candidates without fetching Steam's launch config.</summary>
    internal static IReadOnlyList<string> ResolveExes(string installDir, IReadOnlyList<string> launchExes)
    {
        // Keep configured launch targets first, even if their names match a scan exclusion.
        var resolved = launchExes
            .Select(rel => Path.GetFullPath(Path.Combine(installDir, rel)))
            .Where(File.Exists)
            .ToList();

        // A configured target can be a DRM-free bootstrap; also inspect the nested game executables.
        try
        {
            resolved.AddRange(Directory.EnumerateFiles(installDir, "*.exe", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".unpacked.exe", StringComparison.OrdinalIgnoreCase))
                .Where(p => !SkipPatterns.Any(s => Path.GetFileName(p).Contains(s, StringComparison.OrdinalIgnoreCase)))
                .Select(Path.GetFullPath));
        }
        catch { /* keep known launch targets if the folder scan fails */ }

        return resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>All supported SteamStub variants have a .bind section; inspect only the PE headers.</summary>
    internal static bool MayHaveSteamStub(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.PEHeader is not null && pe.PEHeaders.SectionHeaders.Any(section =>
                string.Equals(section.Name, ".bind", StringComparison.OrdinalIgnoreCase));
        }
        catch (BadImageFormatException) { return false; } // not a valid executable
        // Keep unreadable candidates so processing reports failures instead of silently skipping them.
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static async Task RunCliAsync(string cliPath, string exePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cliPath, $"\"{exePath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(cliPath) ?? Environment.CurrentDirectory,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Steamless.");
        await proc.WaitForExitAsync(ct);
    }
}
