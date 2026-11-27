using System.IO;
using System.Text.Json;

namespace GameGuy.Services.AppInfo;

/// <summary>One game's launch-option edit, plus the snapshot needed to undo it.</summary>
public sealed class LaunchMod
{
    public int AppId { get; set; }

    /// <summary>appinfo's changeNumber when the snapshot was taken. If Steam later ships a different
    /// one, the app has been updated and the snapshot is re-based rather than trusted.</summary>
    public uint ChangeNumber { get; set; }

    /// <summary>The app's launch entries BEFORE we touched them. A whole snapshot, not a diff. A diff
    /// against a file Steam rewrites at will is worthless.</summary>
    public List<LaunchOption> Original { get; set; } = [];

    /// <summary>What the entries should look like.</summary>
    public List<LaunchOption> Desired { get; set; } = [];

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persists launch-option edits so they survive Steam rewriting appinfo.vdf.
///
/// <para>
/// Steam regenerates appinfo from PICS on login, app updates and store browsing. It rewrote the file
/// twice during development. A one-shot edit therefore silently disappears. This mirrors what SteamEdit
/// does with its mods.dat: keep the desired state plus a pre-edit snapshot in our own file, and
/// re-apply when the live appinfo no longer matches.
/// </para>
/// </summary>
public class LaunchModStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirName);
    private static readonly string FilePath = Path.Combine(Dir, "launchmods.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _gate = new();
    private Dictionary<int, LaunchMod> _mods = [];

    private readonly string _path;

    public LaunchModStore() : this(FilePath) { }

    /// <summary>Test seam: point the store at a throwaway file.</summary>
    internal LaunchModStore(string path)
    {
        _path = path;
        Load();
    }

    public bool IsEmpty { get { lock (_gate) return _mods.Count == 0; } }

    public IReadOnlyList<LaunchMod> All { get { lock (_gate) return _mods.Values.ToList(); } }

    public LaunchMod? Get(int appId)
    {
        lock (_gate) return _mods.GetValueOrDefault(appId);
    }

    /// <summary>Record an edit. The snapshot is only captured the FIRST time a game is modified.
    /// Re-saving must not overwrite the original with our own previous edit.</summary>
    public void Save(int appId, uint changeNumber, IReadOnlyList<LaunchOption> current,
                     IReadOnlyList<LaunchOption> desired)
    {
        lock (_gate)
        {
            if (!_mods.TryGetValue(appId, out var mod))
            {
                mod = new LaunchMod
                {
                    AppId = appId,
                    ChangeNumber = changeNumber,
                    Original = current.Select(o => o.Clone()).ToList(),
                };
                _mods[appId] = mod;
            }

            mod.Desired = desired.Select(o => o.Clone()).ToList();
            mod.SavedAt = DateTime.UtcNow;
            Persist();
        }
    }

    /// <summary>Forget a game's edit (used after restoring it).</summary>
    public void Remove(int appId)
    {
        lock (_gate)
        {
            if (_mods.Remove(appId)) Persist();
        }
    }

    /// <summary>
    /// Re-base a stored snapshot when Steam has updated the app since it was taken. SteamEdit's trick.
    /// Without this, "restore original" would write back launch entries from a version of the game that
    /// no longer exists.
    /// </summary>
    public bool Rebase(int appId, uint currentChangeNumber, IReadOnlyList<LaunchOption> currentOriginal)
    {
        lock (_gate)
        {
            if (!_mods.TryGetValue(appId, out var mod) || mod.ChangeNumber == currentChangeNumber)
                return false;

            mod.ChangeNumber = currentChangeNumber;
            mod.Original = currentOriginal.Select(o => o.Clone()).ToList();
            Persist();
            return true;
        }
    }

    // ── persistence ─────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<LaunchMod>>(File.ReadAllText(_path), JsonOpts);
            if (loaded is not null) _mods = loaded.ToDictionary(m => m.AppId);
        }
        catch
        {
            // Present but unreadable → keep the file for manual recovery rather than starting empty and
            // overwriting it on the next save.
            try { File.Move(_path, _path + ".corrupt", overwrite: true); } catch { /* best effort */ }
            _mods = [];
        }
    }

    /// <summary>Atomic write (tmp → move) with a .bak, same as SettingsService. A torn file here loses
    /// every recorded edit AND the snapshots needed to undo them.</summary>
    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (_mods.Count == 0)
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }

            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_mods.Values.ToList(), JsonOpts));
            try { if (File.Exists(_path)) File.Copy(_path, _path + ".bak", overwrite: true); }
            catch { /* best effort */ }
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Losing the record is bad but must never take the app down mid-edit.
        }
    }

    /// <summary>True when two entry lists differ in any field we manage. Used to detect that Steam has
    /// overwritten our edits.</summary>
    public static bool Differs(IReadOnlyList<LaunchOption> a, IReadOnlyList<LaunchOption> b)
    {
        if (a.Count != b.Count) return true;
        for (int i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Index != y.Index || x.Executable != y.Executable || x.Arguments != y.Arguments
                || x.WorkingDir != y.WorkingDir || x.Description != y.Description || x.Type != y.Type
                || x.OsList != y.OsList || x.OsArch != y.OsArch
                || x.BetaKey != y.BetaKey || x.OwnsDlc != y.OwnsDlc)
                return true;
        }
        return false;
    }
}
