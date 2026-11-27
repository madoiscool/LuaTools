using System.IO;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameGuy.Services;

namespace GameGuy.ViewModels;

/// <summary>One row in the overwrite-confirm diff (a depot/DLC the dropped lua adds or removes).</summary>
public record DropDiffRow(string Title, string Meta, bool IsDlc, bool IsShared, string SteamDbUrl);

/// <summary>
/// Reusable drag-and-drop installer used on Home and Add. Accepts dropped .lua / .manifest / .zip
/// files and installs them into Steam via <see cref="LuaInstaller"/>. Shows a before/after diff
/// confirm when a dropped lua would overwrite an existing one, then a result banner. Raises
/// <see cref="Installed"/> so the host page can refresh.
/// </summary>
public partial class DropInstallViewModel : ObservableObject
{
    private readonly LuaInstaller _installer;
    private readonly SteamAppListCache _appList;
    private readonly SteamAppInfoCache _appInfo;
    private readonly SteamDepotInfo _depotInfo;

    /// <summary>Raised after a successful (non-cancelled) install so the page can refresh its library.</summary>
    public event Action? Installed;

    public DropInstallViewModel(LuaInstaller installer, SteamAppListCache appList,
        SteamAppInfoCache appInfo, SteamDepotInfo depotInfo)
    {
        _installer = installer;
        _appList = appList;
        _appInfo = appInfo;
        _depotInfo = depotInfo;
    }

    // Per-confirm steamcmd lookup: depot/DLC id → its real depot info (name/size/os/lang).
    private IReadOnlyDictionary<long, ContentDepot> _depotsById = new Dictionary<long, ContentDepot>();

    // ── Drag-over highlight ─────────────────────────────────────────
    [ObservableProperty] private bool _isDragOver;

    // ── Result banner ───────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultText;

    [ObservableProperty] private bool _resultFailed;
    public bool HasResult => ResultText is not null;

    // ── Overwrite confirm overlay ───────────────────────────────────
    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string? _confirmNoChanges;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAdded))]
    private List<DropDiffRow> _added = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoved))]
    private List<DropDiffRow> _removed = [];

    public bool HasAdded => Added.Count > 0;
    public bool HasRemoved => Removed.Count > 0;

    // A queue of remaining dropped files to process after the current confirm resolves.
    private Queue<string>? _queue;
    private (string luaPath, long appId)? _pendingLua;
    private InstallTally _tally;

    /// <summary>
    /// Install a game by appid. Set by App to the SAME path <c>gameguy://install/&lt;appid&gt;</c> takes,
    /// so a dropped link and the protocol trigger can't drift apart.
    /// </summary>
    public Func<long, Task>? InstallByAppId { get; set; }

    /// <summary>
    /// Entry point for a dropped LINK (a SteamDB / Steam store URL): pull the appid out and install it.
    /// Parsing lives here rather than in the view's code-behind so it's reachable from a test.
    /// </summary>
    /// <returns>True when the text named a game and the install was started.</returns>
    public async Task<bool> TryHandleLinkAsync(string? text)
    {
        if (SteamLinkParser.AppIdFrom(text) is not { } appId || InstallByAppId is null) return false;

        await InstallByAppId(appId);
        return true;
    }

    /// <summary>Entry point: hand the dropped file paths to install (called by the view's Drop handler).</summary>
    public async Task HandleDropAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(LuaInstaller.IsInstallable).ToList();
        if (files.Count == 0)
        {
            ResultFailed = true;
            ResultText = Resources.Strings.Drop_Nothing;
            return;
        }

        ResultText = null;
        _tally = new InstallTally();
        _queue = new Queue<string>(files);
        await ProcessQueueAsync();
    }

    /// <summary>Install queued files until one needs an overwrite confirm (then pause for the user).</summary>
    private async Task ProcessQueueAsync()
    {
        while (_queue is { Count: > 0 })
        {
            string path = _queue.Dequeue();
            string ext = Path.GetExtension(path);

            if (ext.Equals(".manifest", StringComparison.OrdinalIgnoreCase))
            {
                Apply(_installer.InstallManifestFile(path));
            }
            else if (ext.Equals(".lua", StringComparison.OrdinalIgnoreCase))
            {
                long? appId = LuaInstaller.AppIdFromFileName(path);
                if (appId is null) { _tally.Skipped.Add(Path.GetFileName(path)); continue; }
                if (await ConfirmIfOverwriteAsync(path, appId.Value, isZip: false)) return; // paused
                Apply(_installer.InstallLuaFile(path, appId.Value));
            }
            else // .zip
            {
                long? appId = LuaInstaller.AppIdForZip(path);
                if (appId is null) { _tally.Skipped.Add(Path.GetFileName(path)); continue; }
                if (await ConfirmIfOverwriteAsync(path, appId.Value, isZip: true)) return; // paused
                Apply(_installer.InstallZip(path, appId.Value));
            }
        }

        FinishBatch();
    }

    /// <summary>If a lua for this appid already exists, build the diff and open the confirm. Returns true if paused.</summary>
    private async Task<bool> ConfirmIfOverwriteAsync(string path, long appId, bool isZip)
    {
        string? existing = _installer.ReadInstalledLua(appId);
        if (existing is null) return false; // fresh install, no confirm

        var oldLua = LuaFileParser.Parse(existing, appId);
        var newLua = isZip ? ExtractLuaFromZip(path, appId) : LuaFileParser.Parse(path, appId);
        if (newLua is null) return false; // can't diff. Just install (caller proceeds)

        var diff = LuaFileParser.Diff(oldLua, newLua);
        await _appList.EnsureLoadedAsync();

        // One steamcmd call (cached per app) → real depot/DLC names, sizes, OS, language. Index by both
        // the depot id and the dlcappid so a lua entry keyed under either resolves.
        _depotsById = await BuildDepotLookupAsync(appId);

        Added = diff.Added.Select(ToDiffRow).ToList();
        Removed = diff.Removed.Select(ToDiffRow).ToList();
        ConfirmTitle = string.Format(Resources.Strings.Drop_Confirm_Replace, NameFor(appId));
        ConfirmNoChanges = diff.HasChanges ? null : Resources.Strings.Drop_Confirm_NoChanges;
        _pendingLua = (path, appId);
        IsConfirming = true;

        // Lazily fill any DLC rows still showing a bare id from appdetails, then rebuild.
        var unnamed = diff.Added.Concat(diff.Removed)
            .Where(e => DisplayName(e) is null)
            .Select(e => e.Id).Distinct().ToList();
        if (unnamed.Count > 0)
        {
            await Parallel.ForEachAsync(unnamed, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                async (id, _) => await _appInfo.ResolveAsync(id));
            if (IsConfirming && _pendingLua is { } p && p.appId == appId)
            {
                Added = diff.Added.Select(ToDiffRow).ToList();
                Removed = diff.Removed.Select(ToDiffRow).ToList();
            }
        }
        return true;
    }

    /// <summary>Fetch the app's depots from steamcmd (cached) and index by depot id + dlcappid.</summary>
    private async Task<IReadOnlyDictionary<long, ContentDepot>> BuildDepotLookupAsync(long appId)
    {
        var map = new Dictionary<long, ContentDepot>();
        var info = await _depotInfo.GetAsync(appId);
        if (info is null) return map;
        foreach (var d in info.Depots)
        {
            map[d.Id] = d;
            if (d.DlcAppId is { } dlc) map[dlc] = d;
        }
        return map;
    }

    [RelayCommand]
    private async Task ConfirmOverwrite()
    {
        IsConfirming = false;
        if (_pendingLua is { } p)
        {
            string ext = Path.GetExtension(p.luaPath);
            Apply(ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? _installer.InstallZip(p.luaPath, p.appId)
                : _installer.InstallLuaFile(p.luaPath, p.appId));
        }
        _pendingLua = null;
        await ProcessQueueAsync(); // continue any remaining dropped files
    }

    [RelayCommand]
    private async Task CancelOverwrite()
    {
        IsConfirming = false;
        if (_pendingLua is { } p) _tally.Skipped.Add(Path.GetFileName(p.luaPath));
        _pendingLua = null;
        await ProcessQueueAsync();
    }

    private void Apply(InstallResult r)
    {
        if (r.Error is not null) _tally.Errors.Add(r.Error);
        _tally.Failed += r.Failed.Count;
        if (r.LuaInstalled) _tally.Luas++;
        _tally.Manifests += r.ManifestCount;
    }

    private void FinishBatch()
    {
        _queue = null;
        var t = _tally;
        bool anyProblem = t.Failed > 0 || t.Errors.Count > 0;
        ResultFailed = anyProblem;

        var parts = new List<string>();
        if (t.Luas > 0) parts.Add(string.Format(Resources.Strings.Drop_Count_Luas, t.Luas));
        if (t.Manifests > 0) parts.Add(string.Format(Resources.Strings.Drop_Count_Manifests, t.Manifests));

        if (parts.Count == 0 && !anyProblem)
            ResultText = t.Skipped.Count > 0 ? Resources.Strings.Drop_NothingInstalled : null;
        else
        {
            string ok = parts.Count > 0 ? string.Format(Resources.Strings.Drop_Result_Installed, string.Join(" + ", parts)) : "";
            string bad = t.Failed > 0 ? string.Format(Resources.Strings.Drop_Result_Failed, t.Failed) : "";
            string err = t.Errors.Count > 0 ? $" {t.Errors[0]}" : "";
            // No "restart to apply" suffix: OST/BST hot-reload luas written into config/stplug-in.
            ResultText = $"{ok}{bad}{err}".Trim();
        }

        if (t.Luas > 0 || t.Manifests > 0) Installed?.Invoke();
    }

    /// <summary>Real Steam name for a lua entry (DLC name from caches, or lua comment); null if unknown.</summary>
    private string? DisplayName(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        if (d?.DlcAppId is { } dlc)
        {
            string? steamName = _appList.GetName(dlc) ?? _appInfo.GetCached(dlc)?.Name;
            if (steamName is not null) return steamName;
        }
        return _appList.GetName(e.Id) ?? _appInfo.GetCached(e.Id)?.Name ?? e.Comment;
    }

    /// <summary>Build an enriched diff row: real name + "id · size · OS · lang", matching the Manage tab.</summary>
    private DropDiffRow ToDiffRow(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        bool isDlc = d?.IsDlc ?? false;
        bool isShared = d?.IsShared ?? false;

        string title = DisplayName(e)
            ?? (isDlc ? string.Format(Resources.Strings.Manage_DlcName, d!.DlcAppId)
                : isShared ? Resources.Strings.Manage_SharedDepot
                : e.HasKey ? Resources.Strings.Manage_Depot
                : string.Format(Resources.Strings.Manage_DlcName, e.Id));

        var meta = new List<string> { e.Id.ToString() };
        if (d is { Size: > 0 }) meta.Add(FormatSize(d.Size));
        if (!string.IsNullOrWhiteSpace(d?.Os)) meta.Add(PrettyOs(d!.Os!));
        if (!string.IsNullOrWhiteSpace(d?.Language)) meta.Add(d!.Language!);

        string url = d?.DlcAppId is { } dlcId
            ? $"https://steamdb.info/app/{dlcId}/"
            : $"https://steamdb.info/depot/{e.Id}/";

        return new DropDiffRow(title, string.Join("  ·  ", meta), isDlc, isShared, url);
    }

    [RelayCommand]
    private static void OpenSteamDb(DropDiffRow row) => SteamService.OpenUrl(row.SteamDbUrl);

    private string NameFor(long appId) =>
        _appList.GetName(appId) ?? _appInfo.GetCached(appId)?.Name ?? appId.ToString();

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.##} GB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static string PrettyOs(string os) => os switch
    {
        "windows" => "Windows",
        "macos" or "macosx" => "macOS",
        "linux" => "Linux",
        _ => os
    };

    private static LuaContents? ExtractLuaFromZip(string zipPath, long appId)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var luaEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            if (luaEntry is null) return null;

            string tmp = Path.Combine(Path.GetTempPath(), $"gameguy_drop_{appId}_{Guid.NewGuid():N}.lua");
            luaEntry.ExtractToFile(tmp, overwrite: true);
            try { return LuaFileParser.Parse(tmp, appId); }
            finally { try { File.Delete(tmp); } catch { /* best-effort */ } }
        }
        catch { return null; }
    }

    private struct InstallTally
    {
        public int Luas;
        public int Manifests;
        public int Failed;
        public List<string> Errors;
        public List<string> Skipped;
        public InstallTally() { Errors = []; Skipped = []; }
    }
}
