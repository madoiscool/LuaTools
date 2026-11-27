using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using GameGuy.Models;
using GameGuy.Services.Downloads;
using Microsoft.Extensions.Logging;
using Velopack.Windows;

namespace GameGuy.Services;

/// <summary>The outcome of making SteamAutoCrack runnable, so callers can say what actually happened.</summary>
public enum SacPrepareResult
{
    Ready,
    /// <summary>The user declined the .NET runtime installer's elevation prompt. Not an error.</summary>
    RuntimeDeclined,
    /// <summary>The runtime installed but Windows wants a reboot before it can be used.</summary>
    RuntimeNeedsRestart,
    RuntimeFailed,
}

/// <summary>
/// Downloads and launches SteamAutoCrack (SteamAutoCracks/Steam-auto-crack).
/// </summary>
/// <remarks>
/// <para>We can only <b>open</b> it. The published release contains a single GUI exe with no
/// SteamAutoCrack.CLI.exe, and that GUI parses no command-line arguments, so there is no way to hand it
/// an appid or a path. The user does everything inside their own window.</para>
///
/// <para>Their exe is a FRAMEWORK-DEPENDENT net10.0-windows single-file build: the bundle carries no
/// hostpolicy/coreclr/System.Private.CoreLib and declares Microsoft.WindowsDesktop.App, so it cannot
/// start without the .NET 10 <i>Desktop</i> runtime. <see cref="EnsureRuntimeAsync"/> installs that on
/// demand through Velopack's Runtimes API, which we already depend on for updates.</para>
///
/// <para>Update handling mirrors <see cref="DepotDownloaderService"/> and <see cref="SteamlessService"/>:
/// the installed release tag is recorded, re-checked at most every <see cref="ToolCheckInterval"/>, the
/// asset digest is verified before extracting, and <b>every failure path falls back to an existing exe
/// while still recording the attempt</b> so a failing check is not retried on the next click.</para>
/// </remarks>
public class SteamAutoCrackService(
    GithubProxy gh,
    CacheService cache,
    ILogger<SteamAutoCrackService> log)
{
    private static readonly string ToolDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirName, "steamautocrack");

    private static string ExePath => Path.Combine(ToolDir, "SteamAutoCrack.exe");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>How long an up-to-date check is trusted before we ask GitHub again.</summary>
    private static readonly TimeSpan ToolCheckInterval = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private static bool CheckedRecently(long lastMs) =>
        lastMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastMs < (long)ToolCheckInterval.TotalMilliseconds;

    // ── Runtime ──────────────────────────────────────────────────────

    /// <summary>
    /// The .NET 10 Desktop runtime their exe needs, named for this machine's architecture.
    /// </summary>
    /// <remarks>
    /// Velopack's static fields stop at DOTNET8, but GetRuntimeByName parses the id generically, so
    /// "net10-x64-desktop" resolves fine on 1.2.0 without an upgrade. Their asset carries no RID in its
    /// name, so the OS architecture is the honest guess at which runtime it wants.
    /// </remarks>
    private static string RuntimeId => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X86 => "net10-x86-desktop",
        Architecture.Arm64 => "net10-arm64-desktop",
        _ => "net10-x64-desktop",
    };

    // Velopack marks Runtimes [Obsolete] ("no longer used by Velopack, and does not represent the
    // current supported runtimes" - docs.velopack.io/packaging/bootstrapping). It is deprecated because
    // Velopack now bootstraps runtimes through its own installer rather than at app runtime, which is
    // not what we need: this is an on-demand install for a THIRD-PARTY exe, long after our own setup ran.
    //
    // The API still works on 1.2.0 - verified live against .NET 10, which has no static field:
    //   GetRuntimeByName("net10-x64-desktop") -> ".NET 10 WindowsDesktop (x64)"
    //   CheckIsInstalled() -> true on a machine with WindowsDesktop.App 10.0.9
    //   GetDownloadUrl()   -> builds.dotnet.microsoft.com/.../windowsdesktop-runtime-10.0.11-win-x64.exe
    //
    // If a future Velopack drops these types, the replacement is doing it by hand: query
    // dotnetcli.blob.core.windows.net for the latest 10.0 release, download the desktop runtime exe and
    // run it with /install /quiet-style switches. Detection can fall back to parsing `dotnet
    // --list-runtimes` for a Microsoft.WindowsDesktop.App 10.x entry.
#pragma warning disable CS0618 // deliberate: see above

    /// <summary>
    /// Is the runtime their exe needs already present? Local and cheap - no network, no install.
    /// </summary>
    /// <remarks>
    /// Presence of the major version is all their exe needs; patch-level servicing is Windows Update's
    /// job, so this deliberately does not chase 10.0.x updates.
    /// </remarks>
    public async Task<bool> RuntimeInstalledAsync()
    {
        try
        {
            var runtime = Runtimes.GetRuntimeByName(RuntimeId);
            return runtime is not null && await runtime.CheckIsInstalled();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Checking for the .NET runtime failed");
            return false;
        }
    }

    /// <summary>
    /// Make sure the .NET 10 Desktop runtime is present, installing it if not. Returns Ready when their
    /// exe can actually start.
    /// </summary>
    public async Task<SacPrepareResult> EnsureRuntimeAsync(
        IProgress<double?>? progress, CancellationToken ct = default)
    {
        try
        {
            var runtime = Runtimes.GetRuntimeByName(RuntimeId);
            if (runtime is null)
            {
                log.LogDebug("Unknown .NET runtime id {Id}", RuntimeId);
                return SacPrepareResult.RuntimeFailed;
            }

            if (await runtime.CheckIsInstalled()) return SacPrepareResult.Ready;

            if (!await runtime.CheckIsSupported())
            {
                log.LogDebug("{Runtime} is not supported on this machine", runtime.DisplayName);
                return SacPrepareResult.RuntimeFailed;
            }

            Directory.CreateDirectory(ToolDir);
            string installer = Path.Combine(ToolDir, $"{runtime.Id}.exe");
            await runtime.DownloadToFile(installer, p => progress?.Report(p / 100d), null, null);
            ct.ThrowIfCancellationRequested();

            // Not quiet: this elevates, and the user should see what they are approving.
            var result = await runtime.InvokeInstaller(installer, false, null);
            try { File.Delete(installer); } catch { /* leftover installer is harmless */ }

            return result switch
            {
                Runtimes.RuntimeInstallResult.InstallSuccess => SacPrepareResult.Ready,
                Runtimes.RuntimeInstallResult.UserCancelled => SacPrepareResult.RuntimeDeclined,
                Runtimes.RuntimeInstallResult.RestartRequired => SacPrepareResult.RuntimeNeedsRestart,
                _ => SacPrepareResult.RuntimeFailed,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Installing the .NET runtime for SteamAutoCrack failed");
            return SacPrepareResult.RuntimeFailed;
        }
    }
#pragma warning restore CS0618

    // ── Tool ─────────────────────────────────────────────────────────

    /// <summary>
    /// Ensure SteamAutoCrack is on disk and reasonably current. Null only if no usable copy exists.
    /// </summary>
    /// <param name="force">
    /// Skip the throttle. Load-bearing for the background-update path: <see cref="IsUpdateAvailableAsync"/>
    /// records the check timestamp, so without this the job it queues would see "checked recently" and
    /// skip the very download it was queued to perform.
    /// </param>
    public async Task<string?> EnsureToolAsync(
        IProgress<DownloadProgress>? progress, bool force = false, CancellationToken ct = default)
    {
        if (!force && File.Exists(ExePath) && CheckedRecently(cache.SteamAutoCrackCheckedAtMs)) return ExePath;

        await _gate.WaitAsync(ct);
        bool have = false;
        try
        {
            have = File.Exists(ExePath);
            if (!force && have && CheckedRecently(cache.SteamAutoCrackCheckedAtMs)) return ExePath; // won the race

            // A failed lookup still counts as "we looked", so an offline click backs off instead of
            // re-walking the whole GithubProxy mirror chain every time the button is pressed.
            void RecordAttempt() =>
                cache.SteamAutoCrackCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string url = $"https://api.github.com/repos/{AppConfig.SteamAutoCrackRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode)
            {
                log.LogDebug("SteamAutoCrack release lookup failed: {Status}", res?.StatusCode);
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                log.LogDebug("SteamAutoCrack release has no .zip asset");
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            if (!force && have && !string.IsNullOrEmpty(release!.TagName)
                             && string.Equals(release.TagName, cache.SteamAutoCrackVersion, StringComparison.Ordinal))
            {
                RecordAttempt();
                return ExePath;
            }

            Directory.CreateDirectory(ToolDir);
            string zipPath = Path.Combine(ToolDir, "steamautocrack.zip");
            var sink = progress is null ? null : new ProgressRelay<double?>(f =>
                progress.Report(new DownloadProgress(
                    (long)((f ?? 0) * asset.Size), asset.Size > 0 ? asset.Size : null)));
            await gh.DownloadAsync(asset.DownloadUrl, zipPath, sink, ct);

            // Verify before extracting over a working copy: we launch this binary afterwards.
            if (!AssetHash.Matches(zipPath, asset.Digest))
            {
                log.LogDebug("SteamAutoCrack asset digest mismatch; keeping the existing copy");
                try { File.Delete(zipPath); } catch { }
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            // Extract preserving the TREE. Unlike DepotDownloader's flat zip, Goldberg/ and TEMP/ sit
            // beside the exe and their code resolves those from its own base directory, so flattening
            // would break the bundled emulator and the seeded app list.
            //
            // This throws if the user has SteamAutoCrack OPEN (the exe is locked) - which a background
            // update can easily hit. That is caught below, falls back to the existing copy and records
            // the attempt, so it simply retries after the next interval. Not a case worth blocking on.
            ZipFile.ExtractToDirectory(zipPath, ToolDir, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { /* leftover zip is harmless */ }

            if (!File.Exists(ExePath))
            {
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            cache.SteamAutoCrackVersion = release!.TagName;
            RecordAttempt();
            return ExePath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Obtaining SteamAutoCrack failed");
            if (have) cache.SteamAutoCrackCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return have ? ExePath : null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Open it immediately when nothing needs downloading. False means the caller should run the full
    /// job (which installs the runtime and/or the tool).
    /// </summary>
    /// <remarks>
    /// This exists so a launch that transfers zero bytes never touches the queue: a queued job would
    /// flash a progress row and, worse, leave a permanent entry in the user's download history next to
    /// their real game downloads.
    ///
    /// The runtime check is not redundant with <c>File.Exists</c>. A framework-dependent exe with no
    /// runtime still STARTS, and then shows Windows' own "you must install .NET" dialog - so skipping it
    /// would hand the user that dialog instead of our install flow.
    /// </remarks>
    public async Task<bool> TryLaunchIfReadyAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ExePath)) return false;
        if (!await RuntimeInstalledAsync()) return false;
        return Launch();
    }

    /// <summary>
    /// Throttled "is there a newer build?" probe. No download, and never blocks a launch.
    /// </summary>
    /// <remarks>
    /// Returns false when already current, when the 6h window has not elapsed, and on any failure -
    /// recording the attempt each time so an offline machine does not re-walk the mirror chain on every
    /// click. A true result is expected to be followed by <c>EnsureToolAsync(force: true)</c>.
    /// </remarks>
    public async Task<bool> IsUpdateAvailableAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ExePath)) return false;
        if (CheckedRecently(cache.SteamAutoCrackCheckedAtMs)) return false;

        await _gate.WaitAsync(ct);
        try
        {
            if (CheckedRecently(cache.SteamAutoCrackCheckedAtMs)) return false; // won the race

            void RecordAttempt() =>
                cache.SteamAutoCrackCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string url = $"https://api.github.com/repos/{AppConfig.SteamAutoCrackRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) { RecordAttempt(); return false; }

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            RecordAttempt();

            return !string.IsNullOrEmpty(release?.TagName)
                && !string.Equals(release!.TagName, cache.SteamAutoCrackVersion, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Checking for a SteamAutoCrack update failed");
            cache.SteamAutoCrackCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Open SteamAutoCrack's window. Fire-and-forget; we don't wait for it to exit.</summary>
    public bool Launch()
    {
        if (!File.Exists(ExePath)) return false;
        try
        {
            // WorkingDirectory matters: their exe looks for Goldberg/ and TEMP/ next to itself.
            Process.Start(new ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = ToolDir,
            });
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Launching SteamAutoCrack failed");
            return false;
        }
    }
}
