using System.IO;
using System.IO.Compression;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services.Downloads;

/// <summary>
/// Builds the <see cref="DownloadJob"/>s for every in-scope download: game manifests, DLC generation
/// and Denuvo fixes.
/// </summary>
/// <remarks>
/// This is the convergence point for what used to be three separate download+install implementations:
/// <c>DownloadViewModel.DownloadFromSourceAsync</c>, <c>PluginAddService.DownloadAsync</c> and
/// <c>HttpServerService.DownloadAndInstallAsync</c>. The Hubcap-vs-lua.tools branch, the ZIP byte
/// sniff (previously copy-pasted into three files) and the staged-file cleanup now exist exactly once.
/// </remarks>
public class ManifestJobFactory(
    LuaToolsApiClient api,
    HubcapService hubcap,
    SettingsService settings,
    LuaInstaller installer,
    SteamLibraryService library,
    CoverCache covers,
    ToastService toast,
    DepotDownloaderService depotTool,
    SteamDepotInfo depotInfo,
    SteamAutoCrackService sac,
    AppliedFixIndexService fixIndex,
    Sources.PackSourceService packSources)
{
    // ── Job builders ─────────────────────────────────────────────────

    /// <summary>A base-game manifest from a named source (Hubcap uses the user's own key).</summary>
    public DownloadJob CreateManifestJob(
        long appId, string? gameName, string sourceName, bool needsKey,
        Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? confirm = null,
        Action<DownloadItem, JobResult?>? onFinished = null,
        Action? onReveal = null)
    {
        string title = gameName ?? appId.ToString();
        return new DownloadJob(
            DownloadKind.Manifest,
            $"manifest:{appId}",
            appId,
            title,
            SourceMeta.Get(sourceName).DisplayName ?? sourceName,
            covers.GetLocalPath(appId),
            (_, progress, ct) => needsKey
                ? hubcap.DownloadManifestAsync(appId.ToString(), settings.HubcapApiKey ?? "", progress, ct)
                : api.DownloadManifestAsync(appId.ToString(), sourceName, gameName, progress, ct),
            (file, _, _) => Task.FromResult(InstallManifest(file, appId, title)),
            confirm,
            onFinished,
            onReveal);
    }

    /// <summary>
    /// A manifest from a source a pack declared. Fetched from the pack's url, then installed by exactly
    /// the same code as any other manifest — a pack changes where a file comes from, never what is done
    /// with it.
    /// </summary>
    public DownloadJob CreatePackSourceJob(
        Sources.PackSource source, long appId, string? gameName,
        Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? confirm = null,
        Action<DownloadItem, JobResult?>? onFinished = null,
        Action? onReveal = null)
    {
        string title = gameName ?? appId.ToString();
        return new DownloadJob(
            DownloadKind.Manifest,
            $"manifest:{appId}",
            appId,
            title,
            source.DisplayName,
            covers.GetLocalPath(appId),
            (_, progress, ct) => packSources.FetchAsync(source, appId, progress, ct),
            (file, _, _) => Task.FromResult(InstallManifest(file, appId, title)),
            confirm,
            onFinished,
            onReveal);
    }

    /// <summary>DLC unlock lua. Installed silently: it's an unlock, so there's nothing to confirm.</summary>
    public DownloadJob CreateDlcJob(
        long appId, string baseAppId, string? gameName,
        Action<DownloadItem, JobResult?>? onFinished = null,
        Action? onReveal = null)
    {
        string title = gameName ?? appId.ToString();
        return new DownloadJob(
            DownloadKind.Dlc,
            $"dlc:{appId}",
            appId,
            title,
            Resources.Strings.Downloads_Kind_Dlc,
            covers.GetLocalPath(appId),
            (_, progress, ct) => api.GenerateDlcAsync(appId.ToString(), baseAppId, gameName, progress, ct),
            (file, _, _) => Task.FromResult(InstallManifest(file, appId, title)),
            ConfirmAsync: null,
            OnFinished: onFinished,
            OnReveal: onReveal);
    }

    /// <summary>
    /// A Denuvo fix slot. "manifest" installs force-locked into Steam (fixes must stay version-pinned);
    /// "fix" extracts the zip into the game's install folder. Neither restarts Steam.
    /// </summary>
    public DownloadJob CreateDenuvoJob(
        string fixId, string slot, string fallbackName,
        long appId, string gameName, string fixTitle,
        Action<DownloadItem, JobResult?>? onFinished = null)
    {
        bool isManifestSlot = slot == "manifest";
        return new DownloadJob(
            isManifestSlot ? DownloadKind.DenuvoManifest : DownloadKind.DenuvoFix,
            $"denuvo:{fixId}:{slot}",
            appId,
            gameName,
            fixTitle,
            covers.GetLocalPath(appId),
            (_, progress, ct) =>
            {
                // Verify the game is on disk BEFORE the request. /api/denuvo/download spends a slot of
                // the server-side daily limit and the fix zip is game binaries, so discovering "not
                // installed" in the install phase (where ApplyDenuvoFix still checks, as a backstop)
                // costs a slot and a full download for nothing. The Fixes page disables the button for
                // uninstalled games, but a queued fix can outlive that check if the user uninstalls
                // while it waits its turn.
                if (!isManifestSlot && library.GetInstallDir(appId) is null)
                    throw new DownloadAbortedException(
                        string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, gameName));
                return api.DownloadDenuvoAsync(fixId, slot, fallbackName, progress, ct);
            },
            (file, _, _) => Task.FromResult(isManifestSlot
                ? InstallDenuvoManifest(file, appId, gameName)
                : ApplyDenuvoFix(file, appId, fixId, gameName)),
            ConfirmAsync: null,
            OnFinished: onFinished);
    }

    /// <summary>
    /// Raw depot content for a game. ONE queue item covers the whole selection; internally it runs the
    /// downloader once per depot, in list order.
    /// </summary>
    /// <remarks>
    /// Sequential by necessity, not preference: the tool's <c>-manifestfile</c> is a single value applied
    /// to every depot in its own loop, so a batched call would feed them all the same manifest.
    /// </remarks>
    public DownloadJob CreateDepotJob(
        long appId, string gameName, IReadOnlyList<DepotSelection> selections, string outDir,
        Action<DownloadItem, JobResult?>? onFinished = null)
    {
        return new DownloadJob(
            DownloadKind.Depot,
            $"depot:{appId}",
            appId,
            gameName,
            Resources.Strings.Downloads_Kind_Depot,
            covers.GetLocalPath(appId),
            (item, progress, ct) => RunDepotsAsync(item, appId, gameName, selections, outDir, progress, ct),
            // Nothing to install: the depots were written straight to outDir.
            (_, _, _) => Task.FromResult(new JobResult(true,
                string.Format(Resources.Strings.Depot_Status_Done, selections.Count, outDir), outDir)),
            ConfirmAsync: null,
            OnFinished: onFinished,
            OutputPath: outDir);
    }

    /// <summary>
    /// Fetch SteamAutoCrack (installing the .NET runtime it needs first) and open it.
    /// </summary>
    /// <remarks>
    /// Modelled as a queue job so the ~100 MB first run shows real progress and can be cancelled, rather
    /// than freezing a button. It only OPENS their GUI: the shipped release has no CLI and the GUI takes
    /// no arguments, so nothing about the actual crack can be driven from here.
    /// </remarks>
    /// <param name="launchWhenDone">
    /// False for the background-update path. Finishing an update must NOT open a second SteamAutoCrack
    /// window while the user already has one open.
    /// </param>
    public DownloadJob CreateSteamAutoCrackJob(
        bool launchWhenDone = true, Action<DownloadItem, JobResult?>? onFinished = null)
    {
        return new DownloadJob(
            DownloadKind.Tool,
            "tool:steamautocrack",
            0,
            "SteamAutoCrack", // a product name; deliberately not localized
            Resources.Strings.Downloads_Kind_Tool,
            null,
            async (item, progress, ct) =>
            {
                // Runtime BEFORE the 41 MB tool: no point paying for the download if the user declines
                // the elevation prompt.
                OnUi(() => item.Detail = Resources.Strings.Downloads_SAC_GettingRuntime);
                var runtimeProgress = new ProgressRelay<double?>(f =>
                {
                    if (f is { } v) progress.Report(new DownloadProgress((long)(v * 1000), 1000));
                });
                var prepared = await sac.EnsureRuntimeAsync(runtimeProgress, ct);
                if (prepared != SacPrepareResult.Ready)
                {
                    // Declining the prompt, and "installed but needs a reboot", are both outcomes where
                    // nothing went wrong — they settle as Cancelled so the row isn't dressed as an error.
                    bool notAFailure = prepared is SacPrepareResult.RuntimeDeclined
                                              or SacPrepareResult.RuntimeNeedsRestart;
                    throw new DownloadAbortedException(prepared switch
                    {
                        SacPrepareResult.RuntimeDeclined => Resources.Strings.Err_CancelledByUser,
                        SacPrepareResult.RuntimeNeedsRestart => Resources.Strings.Downloads_SAC_Err_Restart,
                        _ => Resources.Strings.Downloads_SAC_Err_Runtime,
                    }, isCancellation: notAFailure);
                }

                OnUi(() => item.Detail = Resources.Strings.Downloads_SAC_GettingTool);
                progress.Report(new DownloadProgress(0, null)); // hand the bar back before the real download
                // force when this job was queued by the background update probe: that probe already
                // recorded the check timestamp, so the throttle would otherwise skip this download.
                string? exe = await sac.EnsureToolAsync(progress, force: !launchWhenDone, ct)
                    ?? throw new DownloadAbortedException(Resources.Strings.Downloads_SAC_Err_Tool);

                OnUi(() => item.Detail = null);
                // Directory sentinel, same as CreateDepotJob: the queue's staged-file cleanup no-ops on it.
                return new DownloadedFile(Path.GetDirectoryName(exe)!, "SteamAutoCrack");
            },
            (_, _, _) => Task.FromResult(
                !launchWhenDone ? new JobResult(true, Resources.Strings.Downloads_SAC_Updated)
                : sac.Launch() ? new JobResult(true, Resources.Strings.Downloads_SAC_Launched)
                : new JobResult(false, Resources.Strings.Downloads_SAC_Err_Launch)),
            ConfirmAsync: null,
            OnFinished: onFinished);
    }

    private async Task<DownloadedFile> RunDepotsAsync(
        DownloadItem item, long appId, string gameName, IReadOnlyList<DepotSelection> selections,
        string outDir, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var keys = depotTool.ResolveKeys(appId);
        if (keys.Count == 0) throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoKeys);

        // Sampled ONCE, before anything runs. Checking it inside the loop would be self-fulfilling:
        // the first depot creates outDir, so every later depot would see it and think a previous session
        // had written there. (Harmless in cost — a depot whose files don't exist yet validates nothing —
        // but the intent is "did an earlier run leave partial files here", which is only true up front.)
        bool outDirExisted = Directory.Exists(outDir);

        // ── Phase 0: the downloader itself ───────────────────────────────────────────────────────────
        // Hoisted out of the per-depot loop so the ~37 MB first fetch (and any update) happens once, with
        // visible progress. RunAsync still calls EnsureToolAsync per depot, but those hit its fast path.
        OnUi(() => item.Detail = Resources.Strings.Downloads_Depots_GettingTool);
        if (await depotTool.EnsureToolAsync(progress, ct) is null)
            throw new DownloadAbortedException(Resources.Strings.Depot_Err_Tool);

        // Hand the bar back. On a fresh install the step above just drove it to 100% against the tool's
        // own size; leaving it there would show a full bar through Phase 1 and then snap to 0% when the
        // depots start. A null total reads as indeterminate until Phase 2 knows the real one.
        progress.Report(new DownloadProgress(0, null));

        // ── Phase 1: resolve EVERYTHING before a single byte is written ──────────────────────────────
        // Sizes for every selection (including finished ones, so a resumed job's baseline is right), and
        // manifests only for what's left to do. Doing this inside the download loop meant a manifest that
        // couldn't be fetched aborted the job after earlier depots had already pulled tens of GB, and it
        // left the free-space check below summing 0 for every unresolved shared depot.
        var resolved = new List<DepotSelection>(selections.Count);
        for (int i = 0; i < selections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Formatted into a local BEFORE the closure: `i` is a for-loop variable, so it is shared
            // across iterations and would have moved on by the time the dispatcher ran the lambda.
            string prep = string.Format(Resources.Strings.Downloads_Depots_Preparing, i + 1, selections.Count);
            OnUi(() => item.Detail = prep);

            // A shared redistributable carries no gid or size in the game's own app-info (it's a
            // three-field stub pointing at the owning app), so both are resolved here rather than at
            // pick time. Cached per app by SteamDepotInfo, and the owner is app 228980 for nearly
            // every game, so this costs one lookup per session across all downloads.
            var sized = await ResolveSharedAsync(selections[i], ct);

            // Resolve the manifest, fetching it into depotcache if Steam doesn't already have it. This is
            // what lets a depot be downloaded at all when the game was added with "Auto Update Apps" on,
            // which comments out the pins and skips the manifest files. Skipped for a depot already
            // finished — its bytes are on disk and nothing will re-read the manifest.
            // `prep` (not the download-phase caption) is the step text here: EnsureManifestAsync appends
            // "· fetching manifest" to whatever it's given, so passing the other string would relabel the
            // row mid-pre-flight as though depots were already downloading.
            if (!item.CompletedDepots.Contains(sized.DepotId))
            {
                sized = sized with { ManifestPath = await EnsureManifestAsync(item, sized, prep, ct) };

                // Without a key the tool cannot decrypt a single chunk, and a depot that fails aborts the
                // whole job below — so refuse here, before anything is written, naming the depot instead
                // of surfacing the downloader's own "No valid depot key" much later.
                if (!keys.TryGetValue(sized.DepotId, out string? hex) || !TryParseKey(hex, out byte[] key))
                    throw new DownloadAbortedException(
                        string.Format(Resources.Strings.Depot_Err_NoKeyFor, sized.DepotId));

                // A key that exists but is WRONG can only be caught when the manifest still has its
                // filenames encrypted, which is the small minority — see ManifestFile.KeyLooksValid.
                if (!ManifestFile.KeyLooksValid(sized.ManifestPath, key))
                    throw new DownloadAbortedException(
                        string.Format(Resources.Strings.Depot_Err_BadKey, sized.DepotId));
            }

            // The manifest's own cb_disk_original beats app info's size: it is exact, and app info may
            // not have carried a size at all (a token-gated app returns no depot list, so those depots
            // arrive here as 0 and would otherwise be budgeted as free).
            if (ManifestFile.TryRead(sized.ManifestPath) is { SizeOnDisk: > 0 } info)
                sized = sized with { Size = info.SizeOnDisk };

            resolved.Add(sized);
        }

        // ── Phase 2: budget, now that the sizes are real ─────────────────────────────────────────────
        // Refuse up front rather than part-way through. The downloader pre-allocates every file at its
        // full size BEFORE fetching a byte, so a short disk fails almost immediately — but only after it
        // has already created multi-GB of zero-filled files. Checking here also gives a message that says
        // what's actually wrong instead of a raw allocation error.
        long totalSize = resolved.Sum(s => s.Size);
        long needed = resolved.Where(s => !item.CompletedDepots.Contains(s.DepotId)).Sum(s => s.Size);
        if (needed > 0 && DepotDownloaderService.FreeSpaceFor(outDir) is { } free && free < needed)
            throw new DownloadAbortedException(string.Format(
                Resources.Strings.Depot_Err_NoSpace, ByteFormat.Size(needed), ByteFormat.Size(free)));

        // ── Phase 3: download ────────────────────────────────────────────────────────────────────────
        string keysFile = DepotDownloaderService.WriteKeysFile(keys);
        try
        {
            long done = 0;
            for (int i = 0; i < resolved.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ready = resolved[i];

                // Resume skips what's already finished rather than re-hashing tens of GB. Its size is the
                // resolved one, so a finished shared depot no longer contributes 0 to the baseline.
                if (item.CompletedDepots.Contains(ready.DepotId)) { done += ready.Size; continue; }

                // Re-checked per depot, not just once up front: the volume is shared with everything else
                // on the machine, so a budget that cleared at the start can be gone by depot 12. Running
                // out mid-download is not reported as a disk error — the tool simply stops printing, and
                // the silence watchdog kills it ten minutes later as a "timeout", which explains nothing.
                if (ready.Size > 0 && DepotDownloaderService.FreeSpaceFor(outDir) is { } left
                    && left < ready.Size)
                    throw new DownloadAbortedException(string.Format(
                        Resources.Strings.Depot_Err_NoSpace,
                        ByteFormat.Size(ready.Size), ByteFormat.Size(left)));

                string step = string.Format(Resources.Strings.Downloads_Depots_Progress, i + 1, resolved.Count);
                OnUi(() => item.Detail = step);

                // Only the FIRST depot after a resume is the partially-written one, so only it needs the
                // (expensive) re-hash. Consume the flag so later depots download at full speed.
                //
                // An existing output folder forces the same treatment even on a fresh item: it means a
                // previous session already wrote here, and CompletedDepots does not survive an app
                // restart. Skipping validation there would hand back a half-written file reported as
                // complete, which is this tool's worst failure mode.
                bool validate = item.NeedsValidate || outDirExisted;
                item.NeedsValidate = false;

                long baseBytes = done;
                var relay = new ProgressRelay<double>(f =>
                    progress.Report(new DownloadProgress(baseBytes + (long)(f * ready.Size), totalSize)));

                // The phase is PARSED from the downloader's own output rather than guessed. A big depot
                // pre-allocates every new file at full size before fetching a byte, so the row used to sit
                // at "Downloading - 0 B of 4.49 GB" looking hung for minutes. Reported only on change.
                var phases = new ProgressRelay<DepotPhase>(ph => OnUi(() =>
                {
                    item.Detail = ph switch
                    {
                        DepotPhase.PreAllocating => $"{step} · {Resources.Strings.Downloads_Depot_PreAllocating}",
                        DepotPhase.Validating => $"{step} · {Resources.Strings.Downloads_Depot_Validating}",
                        DepotPhase.Manifest => $"{step} · {Resources.Strings.Downloads_Depot_FetchingManifest}",
                        _ => step,
                    };

                    // Verifying is a real status (it gates Pause and the label), so keep driving it -
                    // but from what the tool actually reports, not from "validate was requested and no
                    // bytes have arrived yet", which also covered pre-allocation and plain slow starts.
                    item.Status = ph == DepotPhase.Validating
                        ? DownloadStatus.Verifying
                        : DownloadStatus.Downloading;
                }));

                // Recorded so a cancel can delete exactly what this download created. Collected off the
                // UI thread on purpose: a big depot reports thousands of files and none of it is visible.
                var created = new ProgressRelay<string>(path => item.CreatedFiles.Add(path));

                var res = await depotTool.RunAsync(
                    appId, ready, keysFile, outDir, validate, relay, ct, phases, created);
                if (!res.Ok)
                    throw new DownloadAbortedException(res.Error == "tool"
                        ? Resources.Strings.Depot_Err_Tool
                        : string.Format(Resources.Strings.Depot_Err_Failed, ready.DepotId, res.Error ?? ""));

                item.CompletedDepots.Add(ready.DepotId);
                done += ready.Size;
                progress.Report(new DownloadProgress(done, totalSize));
            }

            OnUi(() => item.Detail = null);
            // Sentinel for the queue's file plumbing: a directory, so the staged-file cleanup no-ops on it.
            return new DownloadedFile(outDir, gameName);
        }
        finally
        {
            DeleteStaged(keysFile); // holds decryption keys; never leave it lying around
        }
    }

    /// <summary>
    /// A depot key as bytes. Keys come from a lua file and from Steam's config.vdf, so a malformed one is
    /// a real possibility and reads the same as having no key at all: the download cannot proceed.
    /// </summary>
    private static bool TryParseKey(string? hex, out byte[] key)
    {
        key = [];
        if (hex is not { Length: 64 }) return false; // AES-256, hex-encoded
        try { key = Convert.FromHexString(hex); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// Fill in a shared depot's manifest id and size from the app that actually owns its content.
    /// Returns the selection unchanged for an ordinary depot (one that already declares its own gid).
    /// </summary>
    private async Task<DepotSelection> ResolveSharedAsync(DepotSelection sel, CancellationToken ct)
    {
        if (sel.ManifestId is not null || sel.FromAppId is not { } owner) return sel;

        var info = await depotInfo.GetAsync(owner, ct);
        if (info?.Depots.FirstOrDefault(d => d.Id == sel.DepotId) is not { PublicManifestId: not null } owned)
            throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoManifest);

        return sel with { ManifestId = owned.PublicManifestId, Size = owned.Size };
    }

    /// <summary>
    /// The depotcache path for a depot's manifest, fetching it from the API and installing it there if
    /// it's missing. Never returns null — it throws with a user-facing reason instead.
    /// </summary>
    private async Task<string> EnsureManifestAsync(
        DownloadItem item, DepotSelection sel, string step, CancellationToken ct)
    {
        // Already on disk (a previous run, a pinned install, or Steam's own copy): no request at all.
        // ResolveManifestPath only accepts a file that actually parses as this depot's manifest.
        if (depotTool.ResolveManifestPath(sel.DepotId, sel.ManifestId!) is { } have) return have;

        // Nothing usable — but something may still be sitting there under the right name. It has to go
        // before the fetch, or InstallManifestFile will skip the copy and hand the bad file straight back.
        depotTool.DiscardCachedManifest(sel.DepotId, sel.ManifestId!);

        if (!depotTool.CanFetchManifests)
            throw new DownloadAbortedException(Resources.Strings.Depot_Err_SignIn);

        OnUi(() => item.Detail = $"{step} · {Resources.Strings.Downloads_Depot_FetchingManifest}");

        DownloadedFile staged;
        try
        {
            staged = await api.DownloadDepotManifestAsync(sel.DepotId, sel.ManifestId!, null, ct);
        }
        catch (AuthException)
        {
            // Signed out between opening the picker and the download starting.
            throw new DownloadAbortedException(Resources.Strings.Depot_Err_SignIn);
        }

        // The API is expected to serve raw manifest bytes, but has been observed returning them inside
        // a ZIP (a single entry named "z"). Sniff rather than assume, exactly as InstallManifest does for
        // lua/zip: writing the wrapper into depotcache yields a file SteamKit rejects with
        // "Unrecognized magic value 4034B50" (0x04034B50 being the PK header), and it is sticky
        // once written because InstallManifestFile skips an existing destination.
        string unzipDir = Path.Combine(Path.GetDirectoryName(staged.FilePath)!, "mf_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manifestFile = staged.FilePath;
            if (IsZip(staged.FilePath))
            {
                Directory.CreateDirectory(unzipDir);
                using var archive = ZipFile.OpenRead(staged.FilePath);
                var entry = archive.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name))
                    ?? throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoManifest);

                // InstallManifestFile names the destination after this file, so it must already carry
                // the <depot>_<manifest>.manifest name; the entry inside the zip is just called "z".
                manifestFile = Path.Combine(unzipDir, $"{sel.DepotId}_{sel.ManifestId}.manifest");
                entry.ExtractToFile(manifestFile, overwrite: true);
            }

            // Check BEFORE writing. A bad file that reaches depotcache is sticky, so every later run
            // resolves it locally and fails identically with no way back short of deleting it by hand.
            if (!IsSteamManifest(manifestFile))
                throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoManifest);

            // Reuse the same depotcache write that manifest installs already use: it keeps the
            // <depot>_<manifest>.manifest name, skips an identical existing file and stamps the mtime.
            var result = installer.InstallManifestFile(manifestFile);
            if (result.AnyFailed)
                throw new DownloadAbortedException(result.Error ?? Resources.Strings.Depot_Err_NoManifest);
        }
        finally
        {
            DeleteStaged(staged.FilePath);
            try { if (Directory.Exists(unzipDir)) Directory.Delete(unzipDir, recursive: true); } catch { }
        }

        OnUi(() => item.Detail = step);

        return depotTool.ResolveManifestPath(sel.DepotId, sel.ManifestId!)
               ?? throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoManifest);
    }

    /// <summary>Marshal an observable-property write onto the dispatcher (this runs on a worker).</summary>
    private static void OnUi(Action a) =>
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(a);

    // ── Install phases ───────────────────────────────────────────────

    /// <summary>
    /// Install a downloaded manifest/lua into Steam and turn the result into a user-facing message.
    /// </summary>
    /// <remarks>
    /// The file is always staged as "&lt;appid&gt;.zip", but some sources return a BARE .lua with no zip
    /// wrapper. Unzipping that throws "End of Central Directory record could not be found", so the
    /// bytes are sniffed rather than the extension trusted.
    /// </remarks>
    private JobResult InstallManifest(DownloadedFile file, long appId, string gameName)
    {
        try
        {
            var result = IsZip(file.FilePath)
                ? installer.InstallZip(file.FilePath, appId)
                : installer.InstallLua(file.FilePath, appId);

            if (result.Error is not null) return new JobResult(false, result.Error);
            if (result.AnyFailed)
                return new JobResult(false,
                    string.Format(Resources.Strings.Add_Status_InstallFailed, result.Failed.Count));

            string message = result.ManifestCount > 0
                ? string.Format(Resources.Strings.Add_Status_AddedManifests, gameName, result.ManifestCount)
                : string.Format(Resources.Strings.Add_Status_AddedFetch, gameName);

            // Where it LANDED, not where it was staged: file.FilePath is deleted by the finally below,
            // so returning it handed callers a path guaranteed not to exist.
            return new JobResult(true, message, installer.ReadInstalledLua(appId));
        }
        finally
        {
            DeleteStaged(file.FilePath); // consumed by the install
        }
    }

    /// <summary>
    /// Denuvo manifest slot: force-locked install (version-pinned so an auto-update can't break the fix).
    /// </summary>
    /// <remarks>
    /// This used to call <c>SteamService.RestartSteam()</c> unconditionally and without asking, which
    /// killed Steam and every running game on each fix install. OpenSteamTools/BetterSteamTools watch
    /// the lua directories listed in <c>opensteamtool.toml</c>'s <c>[lua] paths</c> — which includes the
    /// <c>config/stplug-in</c> we just wrote to — so the write itself applies the change live.
    /// </remarks>
    private JobResult InstallDenuvoManifest(DownloadedFile file, long appId, string gameName)
    {
        try
        {
            bool isZip = file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var result = isZip
                ? installer.InstallZip(file.FilePath, appId, forceLocked: true)
                : installer.InstallLuaFile(file.FilePath, appId, forceLocked: true);

            if (result.AnyFailed)
            {
                string err = result.Error ?? Resources.Strings.Fixes_Toast_InstallFailed_Body;
                toast.Show(Resources.Strings.Fixes_Toast_InstallFailed, err, error: true);
                return new JobResult(false, err);
            }

            string message = string.Format(Resources.Strings.Fixes_Toast_FixInstalled_Body, gameName);
            toast.Show(Resources.Strings.Fixes_Toast_FixInstalled, message);
            return new JobResult(true, message, installer.ReadInstalledLua(appId)); // see InstallManifest
        }
        finally
        {
            DeleteStaged(file.FilePath);
        }
    }

    /// <summary>Denuvo fix slot: extract into the game folder. Only possible if the game is installed.
    /// Existing files are backed up as .bak inside .luatools-fix/ so the fix can be reverted.</summary>
    /// <remarks>
    /// Runs in four phases, because the revert record is the ONLY thing that makes a fix undoable and
    /// <c>FixesViewModel</c> keys the Revert button off that file existing:
    ///
    /// <list type="number">
    /// <item><b>Plan</b> — decide modified-vs-added from <c>File.Exists</c> alone. Nothing is written.</item>
    /// <item><b>Commit</b> — write a provisional record listing every planned entry. If this fails the
    /// apply stops here, untouched. Previously the record was written LAST inside an empty
    /// <c>catch {}</c>, so a locked or unwritable path silently "succeeded" and left .bak files with
    /// nothing indexing them and no Revert button that would ever appear.</item>
    /// <item><b>Apply</b> — back up, extract, and build the real entry list with hashes.</item>
    /// <item><b>Settle</b> — rewrite the record with what actually happened.</item>
    /// </list>
    ///
    /// Phase 4 is not tidiness. The provisional record describes INTENT, and an entry whose backup threw
    /// would claim a <c>.bak</c> that was never written — which the revert now (correctly) treats as a
    /// hard error, making a partial apply permanently un-revertable. It is also the only place
    /// <c>HashAfter</c> can come from, since that is unknowable until the file has been extracted.
    /// So the record starts as a promise and ends as a fact, and a crash in between still leaves
    /// something the user can revert.
    /// </remarks>
    private JobResult ApplyDenuvoFix(DownloadedFile file, long appId, string fixId, string gameName)
    {
        try
        {
            string? installDir = library.GetInstallDir(appId);
            if (installDir is null)
            {
                string err = string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, gameName);
                toast.Show(Resources.Strings.Fixes_Toast_GameNotFound, err, error: true);
                return new JobResult(false, err);
            }

            string fixKey = SafeFixKey(fixId);
            string fixDir = Path.Combine(installDir, FixRecordDir);
            string recordPath = Path.Combine(fixDir, $"{fixKey}.json");

            var record = new DenuvoFixRecord
            {
                AppId = appId,
                FixId = fixId,
                AppliedAt = DateTimeOffset.UtcNow.ToString("o"),
            };

            // ── Phase 1: plan. File.Exists only; nothing on disk changes yet. ──────────────────────
            using var archive = ZipFile.OpenRead(file.FilePath);
            var plan = new List<(string RelPath, string Dest, string? BakRel, ZipArchiveEntry Entry)>();
            int failed = 0;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string relPath = entry.FullName.Replace('\\', '/');

                // A zip entry naming its way out of the game folder is never legitimate; skip it and
                // count it, rather than writing wherever it points.
                if (ResolveInside(installDir, entry.FullName) is not { } dest) { failed++; continue; }

                // Backups are keyed by the FULL relative path, not just the file name: fix archives
                // routinely ship the same name in several folders (steam_api64.dll, config.ini), and
                // keying by name alone collapsed them onto one .bak. The !File.Exists guard in phase 3
                // then skipped backing the second one up while still recording it as "modified" pointing
                // at the first one's backup — so a revert restored one file with the other's contents,
                // or silently restored nothing once the .bak was consumed.
                plan.Add((relPath, dest, File.Exists(dest) ? $"{fixKey}/{relPath}.bak" : null, entry));
            }

            // ── Phase 2: commit. Nothing has been touched yet, so failing here costs nothing. ──────
            foreach (var (relPath, _, bakRel, _) in plan)
                record.Files.Add(new DenuvoFixRecordEntry
                {
                    RelativePath = relPath,
                    Action = bakRel is null ? "added" : "modified",
                    BackupPath = bakRel,
                });

            try
            {
                Directory.CreateDirectory(fixDir);
                File.WriteAllText(recordPath, SerializeRecord(record));
            }
            catch (Exception ex)
            {
                toast.Show(Resources.Strings.Fixes_Toast_CouldntApply, ex.Message, error: true);
                return new JobResult(false, ex.Message);
            }

            // ── Phase 3: apply, recording what really happened rather than what was planned. ───────
            var applied = new List<DenuvoFixRecordEntry>(plan.Count);

            foreach (var (relPath, dest, bakRel, entry) in plan)
            {
                try
                {
                    DenuvoFixRecordEntry recordEntry;
                    if (bakRel is not null)
                    {
                        string bakAbs = Path.Combine(fixDir, bakRel);
                        if (!File.Exists(bakAbs)) // never clobber a known-good .bak
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(bakAbs)!);
                            File.Copy(dest, bakAbs, overwrite: false);
                        }
                        // Hash the ORIGINAL before it is overwritten; there is no second chance.
                        recordEntry = new DenuvoFixRecordEntry
                        {
                            RelativePath = relPath,
                            Action = "modified",
                            BackupPath = bakRel,
                            HashBefore = FileHash.Sha256(dest),
                        };
                    }
                    else
                    {
                        recordEntry = new DenuvoFixRecordEntry { RelativePath = relPath, Action = "added" };
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);

                    // Hash what we just wrote. This is what a later revert checks the file against to
                    // prove nothing has replaced it since — another fix layered on top, a game update, a
                    // hand-edit. Set after the extract so it describes the file as it really landed.
                    recordEntry.HashAfter = FileHash.Sha256(dest);
                    applied.Add(recordEntry);
                }
                catch { failed++; } // entry stays OUT of `applied`, so the settled record won't claim it
            }

            // ── Phase 4: settle. Replace the promise with the facts. ───────────────────────────────
            record.Files = applied;
            try { File.WriteAllText(recordPath, SerializeRecord(record)); }
            catch { /* the provisional record from phase 2 stands: over-broad, but still revertable */ }

            // Index it even on partial failure, for the same reason the record is written: files were
            // backed up, so the fix IS applied and must be listable and revertable. The index is only a
            // hint, so a failure to update it is not a failure to apply.
            fixIndex.Add(appId, fixId, gameName, installDir, record.AppliedAt, record.Files.Count);

            if (failed > 0)
            {
                string err = string.Format(Resources.Strings.Fixes_Toast_PartiallyApplied_Body, failed);
                toast.Show(Resources.Strings.Fixes_Toast_PartiallyApplied, err, error: true);
                return new JobResult(false, err);
            }

            string message = string.Format(Resources.Strings.Fixes_Toast_FixApplied_Body, gameName);
            toast.Show(Resources.Strings.Fixes_Toast_FixApplied, message);
            return new JobResult(true, message, installDir);
        }
        catch (Exception ex)
        {
            toast.Show(Resources.Strings.Fixes_Toast_CouldntApply, ex.Message, error: true);
            return new JobResult(false, ex.Message);
        }
        finally
        {
            DeleteStaged(file.FilePath);
        }
    }

    // ── Fix revert ──────────────────────────────────────────────────

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    internal const string FixRecordDir = ".luatools-fix";

    /// <summary>The revert record's on-disk form. Indented because users do open these by hand.</summary>
    private static string SerializeRecord(DenuvoFixRecord record) =>
        System.Text.Json.JsonSerializer.Serialize(
            record, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// A fix id can carry `/`, `:`, and other characters that are illegal in a Windows path segment
    /// (e.g. "online-fix:5e01e852-..."). Sanitise it for use as a folder/file name while keeping the
    /// raw id in the record itself.
    /// </summary>
    internal static string SafeFixKey(string fixId) =>
        string.Concat(fixId.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    /// <summary>Resolve the revert-record path for a given fix inside a game's install folder.</summary>
    internal static string GetFixRecordPath(string installDir, string fixId) =>
        Path.Combine(installDir, FixRecordDir, $"{SafeFixKey(fixId)}.json");

    /// <summary>
    /// Resolve a relative path against the game folder, or null when it would escape it.
    /// </summary>
    /// <remarks>
    /// Both callers take the relative part from data we do not control: zip entry names on apply, and the
    /// revert record on revert. `Path.Combine(installDir, "../../../Windows/System32/x.dll")` happily
    /// resolves outside the game — the classic zip-slip shape — and the revert path DELETES what it
    /// resolves. The record also lives in a user-writable folder, and a fix archive can plant one for a
    /// different fix key that we would not overwrite. So neither side is trusted; both go through here.
    /// Same containment check as <c>DepotDownloaderService.TryDeleteCreatedFiles</c>.
    /// </remarks>
    private static string? ResolveInside(string root, string relative)
    {
        try
        {
            string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string full = Path.GetFullPath(Path.Combine(rootFull, relative));
            return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch { return null; } // malformed path (illegal chars, too long): treat as out of bounds
    }

    /// <summary>Read a fix's revert record from disk, or null if it doesn't exist.</summary>
    internal static DenuvoFixRecord? ReadFixRecord(string recordPath)
    {
        try
        {
            if (!File.Exists(recordPath)) return null;
            string json = File.ReadAllText(recordPath);
            return System.Text.Json.JsonSerializer.Deserialize<DenuvoFixRecord>(json, JsonOpts);
        }
        catch { return null; }
    }

    /// <summary>
    /// Revert a previously applied Denuvo fix: restore .bak files for modified entries, delete added
    /// files, then clean up the backup directory and the revert record.
    /// </summary>
    /// <remarks>
    /// This method is the SOLE emitter of revert feedback — done, partial, conflict, game-not-found,
    /// no-record and the catch-all — matching the apply path. Callers must not add a toast of their own:
    /// the view model used to show one on any failure, so every partial revert (the common case, from a
    /// locked file while the game is running) fired two toasts for a single action.
    /// </remarks>
    public JobResult RevertDenuvoFix(long appId, string fixId, string gameName)
    {
        try
        {
            string? installDir = library.GetInstallDir(appId);
            if (installDir is null)
            {
                string err = string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, gameName);
                toast.Show(Resources.Strings.Fixes_Toast_GameNotFound, err, error: true);
                return new JobResult(false, err);
            }

            string recordPath = GetFixRecordPath(installDir, fixId);
            var record = ReadFixRecord(recordPath);
            if (record is null)
            {
                string err = Resources.Strings.Fixes_Revert_NoRecord;
                toast.Show(Resources.Strings.Fixes_Revert_Failed, err, error: true);
                return new JobResult(false, err);
            }

            int restored = 0, deleted = 0, errors = 0, conflicts = 0;
            string? conflictFile = null;

            foreach (var entry in record.Files)
            {
                // The record sits in a user-writable folder and a fix archive can plant one, so its
                // paths get the same containment check the extract does. This branch DELETES.
                if (ResolveInside(installDir, entry.RelativePath) is not { } dest) { errors++; continue; }

                try
                {
                    if (entry.Action == "modified" && entry.BackupPath is { } bakRel)
                    {
                        if (ResolveInside(Path.Combine(installDir, FixRecordDir), bakRel)
                            is not { } bakAbs) { errors++; continue; }
                        if (!File.Exists(bakAbs))
                        {
                            // The record says this file was modified, so a backup MUST exist. Missing
                            // means it was never written or something removed it, and the original is
                            // gone for good — the one case where the fixed file silently stays in place.
                            // Counting it as an error is the only honest outcome: reporting success here
                            // would tell the user their game is clean when it is still patched.
                            errors++;
                            continue;
                        }

                        // Only put the original back if the file on disk is still the one THIS fix wrote.
                        // Two hashes count as ours: HashAfter (untouched since we applied) and HashBefore
                        // (an earlier attempt already restored it, so a retry must not treat it as
                        // foreign). Anything else means something replaced the file after us — most
                        // likely a second fix layered on top, whose own backup holds our version — and
                        // restoring would throw that away. Both hashes null is an old record, and
                        // FileHash.Matches reads null as "nothing to check", so those revert as before.
                        if (File.Exists(dest)
                            && !FileHash.Matches(dest, entry.HashAfter)
                            && !FileHash.Matches(dest, entry.HashBefore))
                        {
                            conflicts++;
                            conflictFile ??= entry.RelativePath;
                            continue;
                        }

                        // The .bak is NOT deleted here, deliberately. Backups are removed as a group once
                        // the whole revert succeeds (see below). Deleting per-file would make a retry
                        // after a partial failure impossible to tell apart from a lost backup: every
                        // already-restored entry would come back as a missing .bak and fail forever.
                        // Leaving them means a retry just re-copies, which is idempotent.
                        File.Copy(bakAbs, dest, overwrite: true);
                        restored++;
                    }
                    else if (entry.Action == "added")
                    {
                        if (File.Exists(dest))
                        {
                            // This fix created the file, so deleting it is normally right — but not if
                            // someone has since replaced it. Then the file is theirs, not ours.
                            if (!FileHash.Matches(dest, entry.HashAfter))
                            {
                                conflicts++;
                                conflictFile ??= entry.RelativePath;
                                continue;
                            }
                            File.Delete(dest);
                            deleted++;
                        }
                    }
                }
                catch { errors++; }
            }

            // Reported before plain errors because it is the only outcome the user can act on, and the
            // action is specific: revert the fix that was applied later, then come back to this one.
            if (conflicts > 0)
            {
                string conflictErr = string.Format(
                    Resources.Strings.Fixes_Revert_Conflict_Body, conflictFile, conflicts);
                toast.Show(Resources.Strings.Fixes_Revert_Conflict, conflictErr, error: true);
                return new JobResult(false, conflictErr);
            }

            if (errors > 0)
            {
                // Backups and record stay put ON PURPOSE. The usual reason a revert fails is a locked
                // file because the game is running, and deleting the .bak files here would leave the user
                // half-reverted with no way to ever finish. Keeping them means the revert is simply
                // retryable once the game is closed. Same principle the apply path already follows: it
                // writes the record even on partial failure so the backups stay recoverable.
                string err = string.Format(Resources.Strings.Fixes_Revert_Partial_Body, errors);
                toast.Show(Resources.Strings.Fixes_Revert_Partial, err, error: true);
                return new JobResult(false, err);
            }

            // Fully reverted, so the backups have served their purpose and the record is what makes the
            // Revert button appear — both go, and the fix reads as un-applied again.
            string backupDir = Path.Combine(installDir, FixRecordDir, SafeFixKey(fixId));
            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true); } catch { }
            try { if (File.Exists(recordPath)) File.Delete(recordPath); } catch { }

            // Drop it from the index last, so the index is never emptier than the truth. If this fails the
            // stale entry is pruned on the next read anyway, since its record file is now gone.
            fixIndex.Remove(appId, fixId);

            string message = string.Format(Resources.Strings.Fixes_Revert_Done_Body, restored, deleted);
            toast.Show(Resources.Strings.Fixes_Revert_Done, message);
            return new JobResult(true, message, installDir);
        }
        catch (Exception ex)
        {
            // "Couldn't revert", not "Couldn't apply" — this is the revert path, and the old title said
            // the opposite of what had just happened.
            toast.Show(Resources.Strings.Fixes_Revert_Failed, ex.Message, error: true);
            return new JobResult(false, ex.Message);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// True if the file begins with the ZIP local-file-header magic (PK\x03\x04). A bare .lua (or any
    /// non-zip a source returned under a .zip name) returns false, so it installs as a loose lua.
    /// </summary>
    public static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[4];
            return fs.Read(sig) == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
        }
        catch { return false; }
    }

    /// <summary>
    /// True if the file starts with Steam's depot-manifest magic (0x71F617D0, little-endian on disk).
    /// A cheap guard against storing something that merely arrived under a .manifest name.
    /// </summary>
    private static bool IsSteamManifest(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[4];
            return fs.Read(sig) == 4 &&
                   sig[0] == 0xD0 && sig[1] == 0x17 && sig[2] == 0xF6 && sig[3] == 0x71;
        }
        catch { return false; }
    }

    /// <summary>Best-effort delete of a staged download once it has been consumed.</summary>
    public static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
