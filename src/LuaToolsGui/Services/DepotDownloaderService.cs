using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Net.Http;

namespace LuaToolsGui.Services;

/// <summary>One depot to fetch: which depot, which version, where its manifest is, and how big it is.</summary>
/// <param name="ManifestPath">
/// Absolute path to the <c>.manifest</c> in Steam's depotcache, or null when it isn't there yet. Null is
/// normal at pick time: the run loop resolves it, fetching from the API if needed, and rewrites the
/// record with <c>sel with { ManifestPath = … }</c> before handing it to
/// <see cref="DepotDownloaderService.RunAsync"/>.
/// </param>
/// <param name="ManifestId">
/// Null for a shared redistributable: its gid lives under <paramref name="FromAppId"/>, not the game's
/// app-info, and is resolved at download time along with the real size.
/// </param>
public record DepotSelection(long DepotId, string? ManifestId, string? ManifestPath, long Size)
{
    /// <summary>Owning app for a shared depot (see <see cref="ContentDepot.FromAppId"/>), else null.</summary>
    public long? FromAppId { get; init; }
}

/// <summary>Outcome of one depot's download.</summary>
public record DepotRunResult(bool Ok, string? Error);

/// <summary>
/// Runs DepotDownloaderMod to pull raw depot content from Steam's CDN. The tool is downloaded once
/// (via <see cref="GithubProxy"/>, so blocked regions work) and cached under
/// %AppData%\LuaToolsGui\depotdownloader, mirroring <see cref="SteamlessService"/>.
/// </summary>
/// <remarks>
/// <para><b>No account is ever used.</b> We never pass <c>-username</c> or <c>-qr</c>, so the tool takes
/// its anonymous branch (<c>steamUser.LogOnAnonymous()</c>). Those flags are the only paths that reach a
/// <c>Console.ReadLine()</c>, and with stdout redirected a prompt would block forever — hence the silence
/// watchdog below. An anonymous account owns nothing, which is exactly why both inputs must be supplied:
/// the depot key (<c>-depotkeys</c>) and the manifest (<c>-manifestfile</c>).</para>
///
/// <para><b>One process per depot.</b> The tool accepts multiple <c>-depot</c>/<c>-manifest</c> pairs, but
/// <c>-manifestfile</c> is a single value applied to every depot in its loop, so batching would feed them
/// all the same manifest.</para>
///
/// <para><b>Resume needs <c>-validate</c>.</b> Files are pre-allocated at full size, and because
/// <c>-manifestfile</c> makes the tool's "previous" and "new" manifests identical, its hash check always
/// matches — so a re-run WITHOUT <c>-validate</c> downloads nothing and reports success over a
/// half-written file. Always pass validate when resuming a partial depot.</para>
///
/// <para><b>Serialized app-wide</b> behind <c>_runGate</c>. Concurrent anonymous sessions share a
/// SteamKit-derived LoginID and disconnect each other (<c>-loginid</c> is ignored on the anonymous path),
/// and parallel multi-GB transfers only split the same bandwidth.</para>
/// </remarks>
/// <summary>
/// What the downloader is doing right now, parsed from its stdout.
/// </summary>
/// <remarks>
/// A big depot spends a long stretch before a single byte arrives: the tool pre-allocates every new file
/// at full size first, and a resumed one re-hashes what is already on disk. Without this the row just
/// reads "Downloading, 0 B of 4.49 GB" and looks hung.
/// </remarks>
public enum DepotPhase
{
    /// <summary>Fetching the depot manifest.</summary>
    Manifest,
    /// <summary>Creating zero-filled files at their final size. No bytes are being fetched yet.</summary>
    PreAllocating,
    /// <summary>Re-hashing files that already exist (a resume with -validate).</summary>
    Validating,
    /// <summary>Actually pulling chunks.</summary>
    Downloading,
}

public partial class DepotDownloaderService(
    GithubProxy gh,
    SteamService steam,
    AuthService auth,
    CacheService cache,
    ILogger<DepotDownloaderService> log)
{
    /// <summary>
    /// Whether missing manifests can be fetched from the API. Guests can still download depots whose
    /// manifest Steam already has — they just can't pull new ones. Checked locally so the picker never
    /// needs a request to decide what to grey out.
    /// </summary>
    public bool CanFetchManifests => !auth.IsGuest;

    private static readonly string ToolDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "depotdownloader");

    private static string ExePath => Path.Combine(ToolDir, "DepotDownloaderMod.exe");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Kill the child if it produces no output for this long — it's prompting or wedged.</summary>
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Chunks fetched concurrently per depot (the tool's <c>-max-downloads</c>). This is the main
    /// throughput knob and the tool's own README points at it: "Try increasing -max-downloads to
    /// saturate the network more."
    /// </summary>
    /// <remarks>
    /// Raised from the tool's default of 8, which does not saturate a fast connection. The value feeds
    /// <c>MaxDegreeOfParallelism</c> over both the file loop and the chunk queue, and requests are
    /// round-robined across Steam's real CDN server list, so this is spread over several hosts rather
    /// than hammering one. Memory cost is bounded — each in-flight chunk rents roughly its uncompressed
    /// size (about 1 MB) from an ArrayPool.
    ///
    /// <para>Beyond some point the limit stops being the network: SteamKit2 decompresses chunks in
    /// managed code, so on a very fast link CPU becomes the ceiling and raising this further buys
    /// nothing. If downloads ever look CPU-bound or the CDN starts refusing connections, this is the
    /// first number to turn back down.</para>
    /// </remarks>
    private const int MaxChunkDownloads = 32;

    private readonly SemaphoreSlim _toolGate = new(1, 1);
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>
    /// Map one stdout line to a phase, or null when it says nothing about phase.
    /// </summary>
    /// <remarks>
    /// These are printed PER FILE, so a large depot emits thousands. The caller reports only on change.
    /// Order matters: "Downloading depot N manifest" has to be tested before the bare
    /// "Downloading depot N", or every manifest fetch would read as the download starting.
    /// </remarks>
    private const string PreAllocatingPrefix = "Pre-allocating ";

    private static DepotPhase? PhaseOf(string line) => line switch
    {
        _ when line.StartsWith(PreAllocatingPrefix, StringComparison.Ordinal) => DepotPhase.PreAllocating,
        _ when line.StartsWith("Validating ", StringComparison.Ordinal) => DepotPhase.Validating,
        _ when line.StartsWith("Downloading depot ", StringComparison.Ordinal)
               && line.EndsWith(" manifest", StringComparison.Ordinal) => DepotPhase.Manifest,
        _ when line.StartsWith("Downloading depot ", StringComparison.Ordinal) => DepotPhase.Downloading,
        _ => null,
    };

    /// <summary>Matches the tool's per-file progress line, e.g. " 42.17% game/data.pak".</summary>
    [GeneratedRegex(@"^\s*([0-9]+(?:\.[0-9]+)?)%\s+(.+)$")]
    private static partial Regex ProgressRegex();

    // ── Tool acquisition ─────────────────────────────────────────────

    /// <summary>How long an up-to-date check is trusted before we ask GitHub again.</summary>
    /// <remarks>Matches the re-pack workflow's own poll interval; checking more often can't find
    /// anything newer. Also keeps a 10-depot game from making 10 API calls, since RunAsync calls this
    /// once per depot.</remarks>
    private static readonly TimeSpan ToolCheckInterval = TimeSpan.FromHours(6);

    private static bool CheckedRecently(long lastMs) =>
        lastMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastMs < (long)ToolCheckInterval.TotalMilliseconds;

    /// <summary>
    /// Ensure the tool is on disk and reasonably current. Null only if it couldn't be obtained at all.
    /// </summary>
    /// <remarks>
    /// This used to be a bare <c>File.Exists</c>, which pinned whoever downloaded once to that build
    /// forever. The re-pack repo now republishes on every upstream release, so the installed release tag
    /// is recorded and re-checked at most every <see cref="ToolCheckInterval"/>.
    ///
    /// <para><b>A failed check never disables a working tool.</b> Every failure path below falls back to
    /// an existing <c>ExePath</c>, so an offline user who already has the tool keeps downloading depots
    /// exactly as before. Returning null is reserved for "there is no usable tool at all".</para>
    /// </remarks>
    /// <summary>
    /// Baixa o pacote do DepotDownloader com attempts automáticas.
    /// Uma falha temporária de rede não derruba imediatamente o processo inteiro.
    /// </summary>
    private async Task DownloadReleaseWithRetryAsync(
        string url,
        string destination,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        int maxRetries = 4)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("A URL do download não pode ser vazia.", nameof(url));

        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("O destino do download não pode ser vazio.", nameof(destination));

        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Remove um arquivo parcial antes de uma new attempt.
                // Isso evita que um ZIP incompleto seja tratado como um download válido.
                if (attempt > 1)
                {
                    try
                    {
                        if (File.Exists(destination))
                            File.Delete(destination);
                    }
                    catch (Exception cleanupEx)
                    {
                        log.LogDebug(
                            cleanupEx,
                            "[Network] Não foi possível remover o arquivo parcial antes da attempt {Attempt}.",
                            attempt);
                    }
                }

                log.LogInformation(
                    "[Network] Download do DepotDownloader: attempt {Attempt}/{MaxRetries}.",
                    attempt,
                    maxRetries);

                await gh.DownloadAsync(url, destination, progress, ct);

                if (!File.Exists(destination))
                {
                    throw new IOException(
                        "O download terminou sem criar o arquivo esperado.");
                }

                var length = new FileInfo(destination).Length;

                if (length <= 0)
                {
                    throw new IOException(
                        "O servidor retornou um arquivo vazio.");
                }

                log.LogInformation(
                    "[Network] Download concluído na attempt {Attempt}. Tamanho: {Size} bytes.",
                    attempt,
                    length);

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;

                log.LogWarning(
                    ex,
                    "[Network] Falha no download na attempt {Attempt}/{MaxRetries}: {Message}",
                    attempt,
                    maxRetries,
                    ex.Message);

                if (attempt >= maxRetries)
                    break;

                // Backoff: 2s, 4s, 8s...
                int delayMilliseconds = 2000 * (1 << (attempt - 1));

                log.LogInformation(
                    "[Network] Nova attempt em {Delay} ms.",
                    delayMilliseconds);

                await Task.Delay(delayMilliseconds, ct);
            }
        }

        throw new HttpRequestException(
            "Não foi possível baixar o DepotDownloader após várias attempts.",
            lastException);
    }

    public async Task<string?> EnsureToolAsync(IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (File.Exists(ExePath) && CheckedRecently(cache.DepotDownloaderCheckedAtMs)) return ExePath;

        await _toolGate.WaitAsync(ct);
        bool have = false;
        try
        {
            have = File.Exists(ExePath);
            if (have && CheckedRecently(cache.DepotDownloaderCheckedAtMs)) return ExePath; // won the race

            // A failed lookup still counts as "we looked". Without this the throttle only advances on
            // success, and since RunAsync calls this once PER DEPOT, an offline 10-depot game would make
            // ten lookups — each walking every GithubProxy mirror — where the old File.Exists check made
            // none. Backing off costs at most one interval of update latency.
            void RecordAttempt() =>
                cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string url = $"https://api.github.com/repos/{AppConfig.DepotDownloaderRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode)
            {
                log.LogDebug("DepotDownloader release lookup failed: {Status}", res?.StatusCode);
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                log.LogDebug("DepotDownloader release has no .zip asset");
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            // Already on the published build: record that we looked and skip the ~37 MB download.
            if (have && !string.IsNullOrEmpty(release!.TagName)
                     && string.Equals(release.TagName, cache.DepotDownloaderVersion, StringComparison.Ordinal))
            {
                cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return ExePath;
            }

            Directory.CreateDirectory(ToolDir);
            string zipPath = Path.Combine(ToolDir, "depotdownloader.zip");
            // GithubProxy only reports a 0..1 fraction; the release tells us the real size, so convert
            // here rather than making the caller show a meaningless "45 of 100".
            var sink = progress is null ? null : new ProgressRelay<double?>(f =>
                progress.Report(new DownloadProgress(
                    (long)((f ?? 0) * asset.Size), asset.Size > 0 ? asset.Size : null)));
            await DownloadReleaseWithRetryAsync(asset.DownloadUrl, zipPath, sink, ct);

            // Verify before extracting over a working install: this is an executable we then run, and
            // both UnlockerService and PluginInstallerService check the same way.
            if (!AssetHash.Matches(zipPath, asset.Digest))
            {
                log.LogDebug("DepotDownloader asset digest mismatch; keeping the existing tool");
                try { File.Delete(zipPath); } catch { }
                if (have) RecordAttempt(); // don't re-download a bad asset on the very next depot
                return have ? ExePath : null;
            }

            // Extract the WHOLE zip: the exe needs SteamKit2.dll and friends beside it.
            ZipFile.ExtractToDirectory(zipPath, ToolDir, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { /* leftover zip is harmless */ }

            if (!File.Exists(ExePath))
            {
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            cache.DepotDownloaderVersion = release!.TagName;
            cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return ExePath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Obtaining DepotDownloader failed");
            // Same backoff as the explicit failure paths: a throwing check must not be retried per depot.
            if (have) cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return have ? ExePath : null;
        }
        finally { _toolGate.Release(); }
    }

    // ── Input sourcing ───────────────────────────────────────────────

    /// <summary>
    /// Depot-id to decryption key for an app: from its installed lua first (that's where LuaTools puts
    /// them), falling back to Steam's own config.vdf for depots the lua doesn't carry.
    /// </summary>
    public IReadOnlyDictionary<long, string> ResolveKeys(long appId)
    {
        var keys = new Dictionary<long, string>();

        try
        {
            if (steam.StPlugInDir is { } dir)
            {
                string lua = Path.Combine(dir, $"{appId}.lua");
                if (File.Exists(lua) && LuaFileParser.Parse(lua, appId) is { } parsed)
                {
                    // DisabledEntries too: a depot switched off on the Depots page still has a valid key,
                    // and the user explicitly picked it for download.
                    foreach (var e in parsed.Entries.Concat(parsed.DisabledEntries))
                        if (e.Key is { Length: > 0 }) keys[e.Id] = e.Key;
                }
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "Reading depot keys from lua failed for {AppId}", appId); }

        try
        {
            if (steam.EffectivePath is { } root)
            {
                string vdf = Path.Combine(root, "config", "config.vdf");
                if (File.Exists(vdf))
                {
                    foreach (var (depot, key) in DonateKeysService.ExtractKeys(File.ReadAllText(vdf)))
                        if (long.TryParse(depot, out long id) && !keys.ContainsKey(id)) keys[id] = key;
                }
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "Reading depot keys from config.vdf failed"); }

        return keys;
    }

    /// <summary>
    /// The depotcache path for a depot at a given manifest version, or null when Steam doesn't have it.
    /// A game added with "Auto Update Apps" on has its pins commented out and its manifests skipped, so
    /// this is genuinely absent for many installs — callers must check BEFORE queueing, not mid-run.
    /// </summary>
    public string? ResolveManifestPath(long depotId, string manifestId)
    {
        if (CachedManifestPath(depotId, manifestId) is not { } path) return null;

        // Existence is not enough. A half-written or truncated file (a killed download, a full disk)
        // stays on disk under the right name and would be handed to the downloader, which fails on it
        // with a raw parse error. Requiring the file to parse AND to declare the depot and gid its name
        // claims turns that into a clean cache miss, which the fetch path then repairs.
        return ManifestFile.Matches(path, depotId, manifestId) ? path : null;
    }

    /// <summary>
    /// The name a depot's manifest has in depotcache, whether or not it is there.
    /// </summary>
    /// <remarks>
    /// Prefers the real depotcache, then falls back to a file left in the old, wrong
    /// <c>config\depotcache</c> (see <c>SteamService.DepotCacheDir</c>). The fallback is a read
    /// convenience only — it keeps existing installs from re-fetching thousands of manifests at once —
    /// so a hit there is still a real file the caller can hand to the downloader. Writes always go to
    /// the real folder, which is the only one Steam itself reads.
    /// </remarks>
    private string? CachedManifestPath(long depotId, string manifestId)
    {
        string name = $"{depotId}_{manifestId}.manifest";

        if (steam.DepotCacheDir is not { } dir) return null;
        string path = Path.Combine(dir, name);
        if (File.Exists(path)) return path;

        if (steam.LegacyDepotCacheDir is { } legacyDir)
        {
            string legacy = Path.Combine(legacyDir, name);
            if (File.Exists(legacy)) return legacy;
        }

        return path; // nothing on disk: name the real location, so callers report/fetch the right path
    }

    /// <summary>
    /// Delete a cached manifest that failed validation, so a re-fetch can actually replace it.
    /// </summary>
    /// <remarks>
    /// Mandatory before re-fetching, not a tidy-up: <c>LuaInstaller.InstallManifestFile</c> SKIPS a
    /// destination that already exists (deliberately, since the name is content-addressed and Steam may
    /// hold the file open). So a corrupt entry would survive the fetch, be handed back, and fail again
    /// on every attempt — the download could never recover on its own.
    /// </remarks>
    public bool DiscardCachedManifest(long depotId, string manifestId)
    {
        if (CachedManifestPath(depotId, manifestId) is not { } path) return false;
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            log.LogDebug("Discarded unreadable cached manifest {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not discard cached manifest {Path}", path);
            return false; // Steam holding it open; the fetch below will fail loudly rather than silently
        }
    }

    /// <summary>Free bytes on the volume that will hold <paramref name="path"/>, or null if unknown.</summary>
    /// <remarks>
    /// Shared by the depot picker (which warns before you commit) and the job's own pre-check (which
    /// refuses before a byte is allocated). One implementation so the two can never disagree.
    /// </remarks>
    public static long? FreeSpaceFor(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; } // unmapped/UNC path: let the download try and fail on its own terms
    }

    /// <summary>The volume label a path lives on ("C:\"), for display. Empty when it can't be resolved.</summary>
    public static string DriveOf(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Write the <c>depotID;hexKey</c> file the -depotkeys flag expects. Staged under the shared downloads
    /// staging folder so SweepStale() reclaims it if we crash before deleting it.
    /// </summary>
    /// <remarks>The caller MUST delete this when the run finishes — it contains decryption keys.</remarks>
    public static string WriteKeysFile(IReadOnlyDictionary<long, string> keys)
    {
        Directory.CreateDirectory(Downloads.HttpFileDownloader.StagingFolder);
        string path = Path.Combine(Downloads.HttpFileDownloader.StagingFolder, $"depotkeys_{Guid.NewGuid():N}.txt");

        var sb = new StringBuilder();
        foreach (var (id, key) in keys) sb.Append(id).Append(';').Append(key).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    // ── Process invocation ───────────────────────────────────────────

    /// <summary>
    /// Download one depot. <paramref name="depotFraction"/> reports 0..1 for THIS depot; the caller
    /// aggregates across depots. Serialized app-wide (see class remarks).
    /// </summary>
    public async Task<DepotRunResult> RunAsync(
        long appId, DepotSelection sel, string keysFile, string outDir, bool validate,
        IProgress<double>? depotFraction, CancellationToken ct, IProgress<DepotPhase>? phase = null,
        IProgress<string>? createdFile = null)
    {
        string? exe = await EnsureToolAsync(null, ct);
        if (exe is null) return new DepotRunResult(false, "tool");

        await _runGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(outDir);

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ToolDir,
            };
            // ArgumentList quotes each value itself, so paths with spaces need no manual escaping.
            // Deliberately NO -username/-qr (would prompt) and NO -loginid (ignored when anonymous).
            // Both resolved by the run loop before we get here: the id from the owning app for a shared
            // depot, the path from depotcache (or fetched into it).
            ArgumentNullException.ThrowIfNull(sel.ManifestId);
            ArgumentNullException.ThrowIfNull(sel.ManifestPath);

            foreach (string a in new[]
            {
                "-app", appId.ToString(),
                "-depot", sel.DepotId.ToString(),
                "-manifest", sel.ManifestId!,
                "-depotkeys", keysFile,
                "-manifestfile", sel.ManifestPath,
                "-dir", outDir,
                "-max-downloads", MaxChunkDownloads.ToString(),
            }) psi.ArgumentList.Add(a);

            // Mandatory on a resume: without it the tool short-circuits and reports success over a
            // partially-written file. See class remarks.
            if (validate) psi.ArgumentList.Add("-validate");

            using var proc = Process.Start(psi);
            if (proc is null) return new DepotRunResult(false, "spawn");

            long lastOutput = DateTime.UtcNow.Ticks;
            string? lastError = null;
            string? lastLine = null;

            DepotPhase? lastPhase = null;
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Interlocked.Exchange(ref lastOutput, DateTime.UtcNow.Ticks);

                var m = ProgressRegex().Match(e.Data);
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double pct))
                {
                    // A progress line IS the download phase; chunks are moving by definition.
                    if (lastPhase != DepotPhase.Downloading)
                    {
                        lastPhase = DepotPhase.Downloading;
                        phase?.Report(DepotPhase.Downloading);
                    }
                    depotFraction?.Report(Math.Clamp(pct / 100d, 0d, 1d));
                    return;
                }

                string trimmed = e.Data.Trim();

                // "Pre-allocating X" is printed ONLY when X did not already exist, so it is exactly the
                // set of files this run created — which is what a cancel is allowed to delete. Anything
                // the user already had is reported as "Validating" instead and is never recorded.
                if (createdFile is not null
                    && trimmed.StartsWith(PreAllocatingPrefix, StringComparison.Ordinal))
                {
                    string path = trimmed[PreAllocatingPrefix.Length..].Trim();
                    if (path.Length > 0) createdFile.Report(path);
                }

                // Only on CHANGE: these are per-file, so a big depot would otherwise emit thousands.
                if (PhaseOf(trimmed) is { } p && p != lastPhase)
                {
                    lastPhase = p;
                    phase?.Report(p);
                }

                // The tool writes fatal errors to STDOUT via Console.WriteLine and leaves stderr empty
                // ("There is not enough space on the disk", "No valid depot key for N", ...). Keeping the
                // last non-progress line is what turns a useless "exit 1" into the actual reason.
                //
                // Skip stack frames: an unhandled exception prints its message and THEN a dozen "at ..."
                // lines, so keeping the literal last line surfaced "at DepotDownloader.Program.<Main>"
                // rather than the message that actually explains the failure.
                string line = e.Data.Trim();
                if (line.Length > 0 && !line.StartsWith("at ", StringComparison.Ordinal)) lastLine = line;
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Interlocked.Exchange(ref lastOutput, DateTime.UtcNow.Ticks);
                lastError = e.Data;
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Cancellation (user cancelled, or Pause) and the watchdog both resolve to "kill the child".
            using var reg = ct.Register(() => TryKill(proc));
            bool timedOut = false;

            while (!proc.WaitForExit(2000))
            {
                if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastOutput) <= SilenceTimeout.Ticks) continue;
                log.LogDebug("DepotDownloader silent for {Timeout}, killing", SilenceTimeout);
                timedOut = true;
                TryKill(proc);
                break;
            }

            // Blocking overload with no timeout also waits for the async readers to drain.
            proc.WaitForExit();
            ct.ThrowIfCancellationRequested();

            if (timedOut) return new DepotRunResult(false, "timeout");
            if (proc.ExitCode != 0)
            {
                string? why = lastError ?? lastLine;
                if (why is { Length: > 300 }) why = why[..300];
                log.LogDebug("DepotDownloader exited {Code} for depot {Depot}: {Err}",
                    proc.ExitCode, sel.DepotId, why);
                return new DepotRunResult(false, why ?? $"exit {proc.ExitCode}");
            }

            depotFraction?.Report(1d);
            return new DepotRunResult(true, null);
        }
        finally { _runGate.Release(); }
    }

    /// <summary>
    /// The folder DepotDownloader creates inside its output directory. Its presence is proof the
    /// downloader actually ran there, which is what makes deleting the folder safe to offer.
    /// </summary>
    private const string DownloaderMarkerDir = ".DepotDownloader";

    /// <summary>True when the downloader has actually written to this folder.</summary>
    public static bool HasDownloadedContent(string? dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && Directory.Exists(Path.Combine(dir, DownloaderMarkerDir));

    /// <summary>
    /// Delete exactly the files this download created, then tidy up after them. Returns false only if
    /// something was left behind.
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberately not a recursive delete of the output folder.</b> That folder is chosen by
    /// the user through the Change picker, so it can legitimately be an existing game directory being
    /// repaired — wiping it would destroy an install we never created. Only paths the downloader
    /// reported <c>Pre-allocating</c> are removed, and it prints that line only for files that did not
    /// already exist.</para>
    ///
    /// <para>Every path is checked to be inside <paramref name="dir"/> before deletion, so a malformed
    /// or hostile line in the child's stdout cannot reach outside the download folder.</para>
    ///
    /// <para>Call only once the child process has exited. The kill is asynchronous and the downloader
    /// holds handles on what it pre-allocated, so deleting too early just fails.</para>
    /// </remarks>
    public static bool TryDeleteCreatedFiles(string? dir, IReadOnlyCollection<string> createdFiles)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;

        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
        catch { return false; }

        bool allGone = true;
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string f in createdFiles)
        {
            try
            {
                // Resolved against the output folder, not our own working directory (which is ToolDir):
                // absolute paths are unaffected, and a relative one lands where the downloader meant it.
                string full = Path.GetFullPath(f, root);
                // Containment check: never delete outside the folder we were given.
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(full)) File.Delete(full);
                if (Path.GetDirectoryName(full) is { Length: > 0 } parent) touched.Add(parent);
            }
            catch { allGone = false; } // locked or vanished; the folder just keeps it
        }

        // The downloader's own bookkeeping is ours by definition, so it goes too.
        try
        {
            string marker = Path.Combine(root, DownloaderMarkerDir);
            if (Directory.Exists(marker)) Directory.Delete(marker, recursive: true);
        }
        catch { allGone = false; }

        PruneEmptyDirectories(root, touched);
        return allGone;
    }

    /// <summary>
    /// Remove directories left empty by the deletion, deepest first, stopping at (and keeping) the root.
    /// </summary>
    /// <remarks>
    /// Walks up from the folders we actually emptied rather than scanning the whole tree. The output
    /// directory is user-chosen and can be an existing game folder, so sweeping every empty directory
    /// under it would delete ones that were there before this download and are none of our business.
    /// </remarks>
    private static void PruneEmptyDirectories(string root, HashSet<string> touched)
    {
        foreach (string start in touched.OrderByDescending(d => d.Length))
        {
            string? dir = start;
            // Up the chain: emptying a leaf can leave its parent empty too, but never remove the root.
            while (dir is not null
                   && dir.Length > root.Length
                   && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(dir)) { dir = Path.GetDirectoryName(dir); continue; }
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) break; // still holds something
                    Directory.Delete(dir);
                }
                catch { break; } // in use or denied: stop climbing this branch
                dir = Path.GetDirectoryName(dir);
            }
        }
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
}
