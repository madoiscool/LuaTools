using System.IO;

namespace GameGuy.Services.AppInfo;

/// <summary>What a game's launch config currently looks like, plus whether we've edited it.</summary>
public sealed record LaunchState(
    int AppId,
    uint ChangeNumber,
    IReadOnlyList<LaunchOption> Current,
    string? InstallDir,
    bool IsModded);

/// <summary>Outcome of writing appinfo.vdf.</summary>
public sealed record ApplyResult(bool Ok, string? Error = null, bool SteamWasRunning = false);

/// <summary>
/// Reads and writes Steam launch options, combining the appinfo codec with the durable
/// <see cref="LaunchModStore"/>.
///
/// <para>
/// appinfo.vdf is ~373 MB, so it is opened on demand and never held: each call indexes (~2.5 s), does
/// its work, and disposes.
/// </para>
/// </summary>
public class LaunchOptionsService
{
    /// <summary>
    /// Keep only this many backups. appinfo.vdf is ~373 MB, so each one is expensive; the real safety
    /// net is the write-to-temp-then-swap below plus the snapshot in <see cref="LaunchModStore"/>, and
    /// Steam simply regenerates the cache from PICS if it's ever lost.
    /// </summary>
    private const int MaxBackups = 1;

    private readonly Func<string?> _appInfoPath;
    private readonly Func<bool> _isSteamRunning;
    private readonly Action _stopSteam;
    private readonly Action _startSteam;
    private readonly LaunchModStore _store;
    private readonly string? _backupDir;

    public LaunchOptionsService(SteamService steam, LaunchModStore store)
        : this(() => steam.EffectivePath is { } p ? Path.Combine(p, "appcache", "appinfo.vdf") : null,
               SteamService.IsSteamRunning, steam.StopSteam, () => steam.StartSteam(), store,
               // Backups live NEXT TO appinfo.vdf, on Steam's own drive. Deliberately not %AppData%.
               // The file is ~373 MB and the system drive is often the tightest on space (this dev
               // machine had 874 MB free), so parking copies there would fill it in two applies. Same
               // drive also makes the copy a local one rather than a cross-volume transfer.
               backupDir: null)
    { }

    /// <summary>
    /// Test seam: point at a throwaway appinfo file and stub out process control. Steam control is
    /// injected rather than called directly so a test can never kill the developer's actual Steam.
    /// </summary>
    internal LaunchOptionsService(Func<string?> appInfoPath, Func<bool> isSteamRunning,
                                  Action stopSteam, Action startSteam,
                                  LaunchModStore store, string? backupDir)
    {
        _appInfoPath = appInfoPath;
        _isSteamRunning = isSteamRunning;
        _stopSteam = stopSteam;
        _startSteam = startSteam;
        _store = store;
        _backupDir = backupDir;
    }

    public LaunchModStore Store => _store;

    /// <summary>Full path to Steam's appinfo cache, or null if Steam isn't located.</summary>
    public string? AppInfoPath => _appInfoPath();

    public bool IsAvailable => AppInfoPath is { } p && File.Exists(p);

    /// <summary>
    /// Read a game's launch entries. Also re-bases a stored snapshot when Steam has updated the app
    /// (see <see cref="LaunchModStore.Rebase"/>). Null when the app isn't in appinfo at all.
    /// </summary>
    public LaunchState? Read(int appId)
    {
        if (AppInfoPath is not { } path || !File.Exists(path)) return null;

        using var file = new AppInfoFile(path);
        var body = file.ReadAppBody(appId);
        if (body is null) return null;

        var current = LaunchOption.ReadAll(body);
        uint changeNumber = file.Entries[appId].ChangeNumber;

        // If Steam updated the app since we snapshotted it, the snapshot describes a version that no
        // longer exists. Refresh it from what's live now.
        _store.Rebase(appId, changeNumber, current);

        return new LaunchState(appId, changeNumber, current,
            body.GetTable("config")?.GetText("installdir"),
            _store.Get(appId) is not null);
    }

    /// <summary>Record an edit without touching appinfo.vdf. Applying is a separate, confirmed step
    /// because it has to close Steam.</summary>
    public void Stage(int appId, uint changeNumber,
                      IReadOnlyList<LaunchOption> current, IReadOnlyList<LaunchOption> desired) =>
        _store.Save(appId, changeNumber, current, desired);

    /// <summary>Stage a return to the snapshot taken before we first edited this game.</summary>
    public IReadOnlyList<LaunchOption>? StageRestore(int appId) => _store.Get(appId)?.Original;

    /// <summary>
    /// Re-apply the staged edits for these apps: what the drift notice's "Re-apply" button runs, given
    /// the ids from <see cref="FindDrifted"/>. Ids no longer in the store are skipped rather than
    /// failing the batch: between the drift check and the user clicking, an app can have been restored.
    /// </summary>
    public ApplyResult Reapply(IReadOnlyList<int> appIds, bool restartSteam = true)
    {
        var edits = new Dictionary<int, IReadOnlyList<LaunchOption>>();
        foreach (int appId in appIds)
            if (_store.Get(appId) is { } mod) edits[appId] = mod.Desired;

        return Apply(edits, restartSteam);
    }

    /// <summary>
    /// Write every staged edit into appinfo.vdf.
    ///
    /// <para>
    /// Steam MUST be closed first: it holds the file and rewrites it from its own in-memory state, so
    /// writing underneath it is pointless. This mirrors SteamEdit, which kills Steam, writes, and
    /// relaunches. <paramref name="restartSteam"/> controls whether it's brought back up.
    /// </para>
    /// </summary>
    public ApplyResult Apply(IReadOnlyDictionary<int, IReadOnlyList<LaunchOption>> edits, bool restartSteam)
    {
        if (AppInfoPath is not { } path || !File.Exists(path))
            return new ApplyResult(false, "Steam's appinfo cache wasn't found.");
        if (edits.Count == 0) return new ApplyResult(true);

        bool wasRunning = _isSteamRunning();
        if (wasRunning) _stopSteam();

        string tmp = path + ".gameguy.tmp";
        try
        {
            Backup(path, _backupDir ?? DefaultBackupDir(path));

            var replacements = new Dictionary<int, VdfTable>();
            using (var file = new AppInfoFile(path))
            {
                foreach (var (appId, options) in edits)
                {
                    var root = file.ReadApp(appId);
                    var body = root?.GetTable("appinfo");
                    if (root is null || body is null) continue;   // app vanished from appinfo. Skip it
                    LaunchOption.WriteAll(body, options);
                    replacements[appId] = root;
                }

                if (replacements.Count == 0) return new ApplyResult(true, null, wasRunning);
                file.SaveAs(tmp, replacements);
            }

            // Swap only after a complete successful write, so a failure mid-way can't leave Steam with a
            // truncated cache.
            File.Move(tmp, path, overwrite: true);
            return new ApplyResult(true, null, wasRunning);
        }
        catch (Exception ex)
        {
            return new ApplyResult(false, ex.Message, wasRunning);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            if (wasRunning && restartSteam) _startSteam();
        }
    }

    /// <summary>
    /// Which staged edits no longer match what's in appinfo, i.e. Steam has overwritten them. Runs off
    /// the UI thread; returns an empty list when nothing is staged so the common case costs nothing.
    /// </summary>
    public IReadOnlyList<int> FindDrifted()
    {
        if (_store.IsEmpty || AppInfoPath is not { } path || !File.Exists(path)) return [];

        try
        {
            using var file = new AppInfoFile(path);
            var drifted = new List<int>();
            foreach (var mod in _store.All)
            {
                var body = file.ReadAppBody(mod.AppId);
                if (body is null) continue;
                if (LaunchModStore.Differs(LaunchOption.ReadAll(body), mod.Desired))
                    drifted.Add(mod.AppId);
            }
            return drifted;
        }
        catch
        {
            return [];   // unreadable/locked cache. Nothing actionable to report
        }
    }

    /// <summary>Backups sit beside appinfo.vdf, on whichever drive Steam lives on.</summary>
    private static string DefaultBackupDir(string appInfoPath) =>
        Path.Combine(Path.GetDirectoryName(appInfoPath)!, "gameguy-backup");

    /// <summary>
    /// Copy appinfo.vdf aside before a write, keeping the most recent <see cref="MaxBackups"/>.
    /// Skipped when the drive is short on space: a 373 MB copy that fills the disk would cause a worse
    /// problem than the one it insures against, and the temp-then-swap below already prevents a torn
    /// write.
    /// </summary>
    private static void Backup(string path, string backupDir)
    {
        try
        {
            long size = new FileInfo(path).Length;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(backupDir))!);
            if (drive.AvailableFreeSpace < size * 2) return;

            Directory.CreateDirectory(backupDir);
            string dest = Path.Combine(backupDir, $"appinfo-{DateTime.Now:yyyyMMdd-HHmmss}.vdf");
            File.Copy(path, dest, overwrite: true);

            foreach (var old in new DirectoryInfo(backupDir)
                         .GetFiles("appinfo-*.vdf")
                         .OrderByDescending(f => f.CreationTimeUtc)
                         .Skip(MaxBackups))
                try { old.Delete(); } catch { /* best effort */ }
        }
        catch
        {
            // A failed backup shouldn't block the edit: the .tmp swap is the real safety net.
        }
    }
}
