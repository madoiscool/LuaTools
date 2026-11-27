using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameGuy.Services;

/// <summary>How a stored lua came to be. Stored VALUES are English keys used in switch/equality.
/// Never localize them (the display label is localized separately).</summary>
public static class LuaVariantKind
{
    /// <summary>A plain &lt;appid&gt;.lua with no build identity (what everyone had before builds existed).</summary>
    public const string Default = "default";
    /// <summary>Came from a &lt;appid&gt;_&lt;buildid&gt;.lua. Pinned to one Steam build.</summary>
    public const string Build = "build";
    /// <summary>Hand-edited / saved by the user from the Builds page editor.</summary>
    public const string Preset = "preset";
}

/// <summary>
/// One stored lua for a game. <see cref="Hash"/> is the SHA-256 of the file's bytes AND its filename in
/// the vault, so "which variant is live right now" is answered by hashing Steam's copy, never by a
/// stored flag that could drift out of sync.
/// </summary>
public record LuaVariant(
    string Hash,
    string Kind,
    string? BuildId,
    string? Label,
    DateTime CapturedAt,
    string? Source,
    IReadOnlyDictionary<string, string> ManifestIds,
    int DepotCount,
    int DlcCount)
{
    /// <summary>True when this lua actually pins depots to fixed manifests (vs. tracking latest).</summary>
    [JsonIgnore]
    public bool IsPinned => ManifestIds.Count > 0;

    /// <summary>
    /// What the UI shows: the user's own name if they renamed it, else a generated one. Renaming only
    /// sets <see cref="Label"/>, so a renamed build still knows which build it is.
    /// </summary>
    [JsonIgnore]
    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(Label) ? Label!
        : BuildId is not null ? string.Format(Resources.Strings.Builds_Variant_Build, BuildId)
        : Kind == LuaVariantKind.Default ? Resources.Strings.Builds_Variant_Default
        : string.Format(Resources.Strings.Builds_Variant_Preset,
            CapturedAt.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture));
}

/// <summary>On-disk shape of a game's index.json.</summary>
internal class VaultIndex
{
    public long AppId { get; set; }
    public List<LuaVariant> Variants { get; set; } = [];

    /// <summary>
    /// The variant the live lua was last based on, recorded the moment an edit made it diverge. This is
    /// what an in-place Save overwrites. Persisted (not just held in memory), so closing the page, or the
    /// app. Doesn't turn "Save" into "Save as new" behind the user's back. Null when the live lua
    /// matches a stored variant, i.e. there's nothing pending.
    /// </summary>
    public string? EditBaseHash { get; set; }
}

/// <summary>
/// Keeps every lua the app has ever installed for a game, so the user can switch between builds even
/// though Steam only ever reads ONE file per game (config\stplug-in\&lt;appid&gt;.lua).
///
/// <para>
/// Layout: files are content-addressed (the name IS the sha256 of the bytes):
/// <code>
/// %AppData%\GameGuy\luavault\&lt;appid&gt;\index.json
/// %AppData%\GameGuy\luavault\&lt;appid&gt;\&lt;sha256&gt;.lua
/// </code>
/// That gives dedupe for free, and makes <see cref="GetActiveHash"/> a pure function of what's on disk,
/// so it stays correct even when Steam, another tool, or the user overwrites the live file behind
/// our back. index.json holds only metadata; the bytes are always the source of truth.
/// </para>
/// </summary>
public class LuaVault
{
    private static readonly string DefaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirName, "luavault");

    private readonly string _root;
    private readonly Func<string?> _stPlugInDir;

    public LuaVault(SteamService steam) : this(() => steam.StPlugInDir, DefaultRoot) { }

    /// <summary>
    /// Test seam: point the vault at throwaway directories instead of %AppData% and the real Steam
    /// install. Worth having. This class rewrites the files Steam loads, so "it destroys your luas" is
    /// the failure mode, and that's not something to only find out by hand.
    /// </summary>
    internal LuaVault(Func<string?> stPlugInDir, string root)
    {
        _stPlugInDir = stPlugInDir;
        _root = root;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes index read-modify-write cycles. Installs can land on background threads while
    /// the Builds page reads on the UI thread.</summary>
    private readonly object _gate = new();

    /// <summary>Raised (with the appid) after any change to a game's variants, so the Builds page can
    /// refresh. May fire on a background thread. Handlers must marshal to the UI thread.</summary>
    public event Action<long>? VaultChanged;

    private string AppDir(long appId) => Path.Combine(_root, appId.ToString());
    private string IndexPath(long appId) => Path.Combine(AppDir(appId), "index.json");
    private string VariantPath(long appId, string hash) => Path.Combine(AppDir(appId), hash + ".lua");

    /// <summary>Steam's live copy for this game (config\stplug-in\&lt;appid&gt;.lua), or null if Steam
    /// isn't located.</summary>
    public string? LivePath(long appId) =>
        _stPlugInDir() is { } dir ? Path.Combine(dir, $"{appId}.lua") : null;

    // ── Reads ───────────────────────────────────────────────────────

    /// <summary>Every stored variant for a game, newest first. Empty when the game has no vault yet.</summary>
    public IReadOnlyList<LuaVariant> GetVariants(long appId)
    {
        lock (_gate) return LoadIndex(appId).Variants.OrderByDescending(v => v.CapturedAt).ToList();
    }

    /// <summary>
    /// The hash of the lua Steam is actually using right now, or null if there's no live file. Compare
    /// against <see cref="LuaVariant.Hash"/> to find the active variant. A hash matching NOTHING means
    /// the live file has changed since the last <see cref="SyncDefaultFromLive"/> (a fresh install, a
    /// hand edit, another tool), and that sync is what adopts those bytes as the Default.
    /// </summary>
    public string? GetActiveHash(long appId)
    {
        string? path = LivePath(appId);
        return path is not null && File.Exists(path) ? HashFile(path) : null;
    }

    /// <summary>
    /// "Has this game been captured at all". Reads the index rather than probing for the directory:
    /// a folder can outlive its contents (or be half-written), and answering yes for an empty one kept
    /// deleted games alive on the Depots page.
    /// </summary>
    /// <remarks>
    /// This used to be a bare <c>Directory.Exists</c> because the game list called it per game on every
    /// filter pass. It no longer does — badges are computed once per load in <c>RefreshBadgesAsync</c>,
    /// off the UI thread, and its caller reads <see cref="GetVariants"/> (which parses the index) two
    /// lines later regardless.
    /// </remarks>
    public bool HasVariants(long appId) => HasStoredVariants(appId);

    /// <summary>At least one variant recorded in this app's index.</summary>
    private bool HasStoredVariants(long appId)
    {
        lock (_gate) return LoadIndex(appId).Variants.Count > 0;
    }

    /// <summary>The variant Steam is currently using, or null if there is none / it was edited externally.</summary>
    public LuaVariant? GetActiveVariant(long appId)
    {
        string? hash = GetActiveHash(appId);
        return hash is null ? null : GetVariants(appId).FirstOrDefault(v => v.Hash == hash);
    }

    /// <summary>
    /// Loose &lt;appid&gt;_&lt;buildid&gt;.lua files sitting directly in stplug-in. Steam only ever reads
    /// &lt;appid&gt;.lua, so these are INERT where they are. Dropping one in the folder does nothing until
    /// it's applied. Surfacing them lets the Builds page offer builds the user already has on disk.
    /// </summary>
    public IEnumerable<(long AppId, string BuildId, string Path)> EnumerateLooseBuildLuas()
    {
        string? dir = _stPlugInDir();
        if (dir is null || !Directory.Exists(dir)) yield break;

        foreach (string path in Directory.EnumerateFiles(dir, "*_*.lua"))
        {
            long? appId = LuaInstaller.AppIdFromFileName(path);
            string? buildId = LuaInstaller.BuildIdFromFileName(path);
            if (appId is { } id && buildId is not null) yield return (id, buildId, path);
        }
    }

    /// <summary>Store any loose build-named luas for this app as build variants (idempotent. The
    /// content-addressed capture no-ops once they're already in).</summary>
    public void AdoptLooseBuildLuas(long appId)
    {
        foreach (var (id, buildId, path) in EnumerateLooseBuildLuas())
            if (id == appId)
                Capture(appId, path, LuaVariantKind.Build, buildId);
    }

    /// <summary>
    /// Make a stored build live when the game has NO &lt;appid&gt;.lua at all, and return it.
    /// <para>
    /// Dropping &lt;appid&gt;_&lt;buildid&gt;.lua into stplug-in does nothing on its own. Steam only reads
    /// &lt;appid&gt;.lua, so a game in that state is loading nothing, and applying the build is plainly what
    /// putting the file there meant. Deliberately does NOTHING when a live lua already exists: silently
    /// swapping which version of a game the user runs is not a decision to make on their behalf.
    /// </para>
    /// </summary>
    public LuaVariant? ApplyBuildIfNothingLive(long appId)
    {
        if (GetActiveHash(appId) is not null) return null; // something is already live. Leave it alone

        var build = GetVariants(appId).FirstOrDefault(v => v.BuildId is not null); // newest first
        if (build is null) return null;

        return Apply(appId, build) ? build : null;
    }

    /// <summary>
    /// Appids that have at least one stored variant. A directory alone is not enough — an empty or
    /// half-written one reports nothing, rather than a game with no versions to offer.
    /// </summary>
    public IReadOnlyList<long> AppsWithVariants()
    {
        try
        {
            if (!Directory.Exists(_root)) return [];
            return Directory.EnumerateDirectories(_root)
                .Select(d => long.TryParse(Path.GetFileName(d), out long id) ? id : 0)
                .Where(id => id > 0 && HasStoredVariants(id))
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>The stored lua's text (for the editor). Empty string if it's gone.</summary>
    public string ReadText(long appId, string hash)
    {
        try
        {
            string path = VariantPath(appId, hash);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch { return ""; }
    }

    /// <summary>The live lua's text (for the editor). Empty string if there isn't one.</summary>
    public string ReadLiveText(long appId)
    {
        try
        {
            string? path = LivePath(appId);
            return path is not null && File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch { return ""; }
    }

    // ── Writes ──────────────────────────────────────────────────────

    /// <summary>
    /// Store a lua file as a variant. <paramref name="luaPath"/> must be the ORIGINAL file (the staged
    /// download or extracted zip entry), NOT what ends up in stplug-in: an install with "Auto Update
    /// Apps" on comments every setManifestid out, and a build variant without its pins is worthless.
    /// Re-capturing identical bytes is a no-op that returns the existing variant (content-addressed).
    /// </summary>
    public LuaVariant? Capture(long appId, string luaPath, string kind, string? buildId = null, string? source = null)
    {
        try
        {
            if (!File.Exists(luaPath)) return null;
            string hash = HashFile(luaPath);

            LuaVariant result;
            bool changed;

            lock (_gate)
            {
                var index = LoadIndex(appId);
                var existing = index.Variants.FirstOrDefault(v => v.Hash == hash);

                if (existing is not null)
                {
                    // Same bytes already stored. Backfill a build id if this capture knows one and the
                    // stored copy doesn't (e.g. it was first seen as the plain live file, then downloaded
                    // again under its <appid>_<buildid>.lua name).
                    changed = existing.BuildId is null && buildId is not null;
                    if (!changed) return existing;

                    result = existing with
                    {
                        BuildId = buildId,
                        Kind = LuaVariantKind.Build,
                        Source = existing.Source ?? source,
                    };
                    index.Variants[index.Variants.IndexOf(existing)] = result;
                }
                else
                {
                    Directory.CreateDirectory(AppDir(appId));
                    File.Copy(luaPath, VariantPath(appId, hash), overwrite: true);
                    result = Describe(appId, luaPath, hash, kind, buildId, source);
                    index.Variants.Add(result);
                    changed = true;
                }

                SaveIndex(appId, index);
            }

            // Fired outside the lock: a handler that reads back (the Builds page does) must not be able
            // to re-enter the index mid-write.
            if (changed) VaultChanged?.Invoke(appId);
            return result;
        }
        catch
        {
            return null; // the vault is a convenience. A failure here must never break an install
        }
    }

    /// <summary>
    /// Point the Default SLOT at whatever Steam is running. Unlike builds and presets, of which there can
    /// be many, a game has exactly one Default: it's the working copy. The live lua when that lua isn't
    /// one of the saved ones.
    ///
    /// <para>Four branches, in order:</para>
    /// <list type="bullet">
    /// <item>no live file. Nothing to do.</item>
    /// <item>live matches a stored build/preset. THAT one is active; the Default keeps its own bytes so
    /// switching back to it still works.</item>
    /// <item>an in-place edit is pending (<see cref="GetEditBase"/>). The diverged bytes belong to the
    /// build being edited, not to the Default. Without this, editing a build would fold the edit into the
    /// Default and "Save to &lt;build&gt;" would have nothing left to write back to.</item>
    /// <item>otherwise the live bytes ARE the Default: the previous default's entry AND file are dropped
    /// and these take its place.</item>
    /// </list>
    ///
    /// <para>
    /// That last branch is destructive by design (the user's call): the Default follows stplug-in, so
    /// anything overwriting the live lua (this app, SteamTools, a hand edit) replaces it with no undo.
    /// Builds and presets are never touched, so saving a preset is how a lua is kept.
    /// </para>
    /// </summary>
    public void SyncDefaultFromLive(long appId)
    {
        if (SyncDefaultFromLiveCore(appId)) VaultChanged?.Invoke(appId);
    }

    /// <summary>
    /// <see cref="SyncDefaultFromLive"/> without the event, so <see cref="Apply"/> can capture the
    /// outgoing lua and swap in the new one as one operation that raises a single change.
    /// </summary>
    /// <returns>True when the index actually changed.</returns>
    private bool SyncDefaultFromLiveCore(long appId)
    {
        try
        {
            string? live = LivePath(appId);
            if (live is null || !File.Exists(live)) return false;
            string liveHash = HashFile(live);

            lock (_gate)
            {
                var index = LoadIndex(appId);
                bool changed = CollapseDefaults(appId, index, liveHash);

                bool liveIsStored = index.Variants.Any(v => v.Hash == liveHash);
                bool editPending = index.EditBaseHash is not null;

                if (!liveIsStored && !editPending)
                {
                    foreach (var old in index.Variants
                                 .Where(v => v.Kind == LuaVariantKind.Default && v.Hash != liveHash)
                                 .ToList())
                    {
                        index.Variants.Remove(old);
                        DeleteVariantFile(appId, old.Hash);
                    }

                    Directory.CreateDirectory(AppDir(appId));
                    File.Copy(live, VariantPath(appId, liveHash), overwrite: true);
                    // Steam's copy is always <appid>.lua, so its name can't carry a build id. This is
                    // only ever "default", even when the bytes happen to be pinned.
                    index.Variants.Add(Describe(appId, live, liveHash, LuaVariantKind.Default, null, null));
                    changed = true;
                }

                if (changed) SaveIndex(appId, index);
                return changed;
            }
        }
        catch
        {
            return false; // the vault is a convenience, never break an install or a page load over it
        }
    }

    /// <summary>
    /// Reduce a game to a single Default, keeping the one Steam is running (else the newest). Migration
    /// for indexes written before the Default was a slot: every install of a plain lua used to APPEND
    /// another "default" row, so re-adding a game from a different generator left two identical-looking
    /// entries. Runs as part of the sync, so it heals on the next visit with no separate migration step.
    /// </summary>
    private bool CollapseDefaults(long appId, VaultIndex index, string? liveHash)
    {
        var defaults = index.Variants.Where(v => v.Kind == LuaVariantKind.Default).ToList();
        if (defaults.Count <= 1) return false;

        var keep = defaults.FirstOrDefault(v => v.Hash == liveHash)
                   ?? defaults.OrderByDescending(v => v.CapturedAt).First();

        foreach (var v in defaults.Where(v => v != keep && v.Hash != liveHash))
        {
            index.Variants.Remove(v);
            DeleteVariantFile(appId, v.Hash);
        }
        return true;
    }

    private void DeleteVariantFile(long appId, string hash)
    {
        try { File.Delete(VariantPath(appId, hash)); } catch { /* the index is what matters */ }
    }

    /// <summary>
    /// Make a stored variant the one Steam uses. Copies the bytes VERBATIM. Deliberately NOT through
    /// <see cref="LuaInstaller"/>, whose "Auto Update Apps" transform comments out setManifestid lines.
    /// Running that here would un-pin the very build the user just chose.
    /// </summary>
    public bool Apply(long appId, LuaVariant variant)
    {
        string? live = LivePath(appId);
        string source = VariantPath(appId, variant.Hash);
        if (live is null || !File.Exists(source)) return false;

        try
        {
            // Capture the outgoing lua FIRST. Switching builds overwrites the live file, and if what was
            // sitting there was an unsaved working copy this is its only chance to be kept. "the Builds
            // page probably refreshed and synced already" is not a guarantee to hang a destructive
            // overwrite on. Core variant so the whole swap raises one VaultChanged, not two.
            SyncDefaultFromLiveCore(appId);

            Directory.CreateDirectory(Path.GetDirectoryName(live)!);
            File.Copy(source, live, overwrite: true);
            StampNow(live);
            SetEditBase(appId, null); // live matches a stored variant again, no pending edit
            VaultChanged?.Invoke(appId);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Write text to Steam's live lua (the editor's Save). Does not touch the vault.</summary>
    public bool WriteLive(long appId, string text)
    {
        string? live = LivePath(appId);
        if (live is null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(live)!);
            File.WriteAllText(live, text);
            StampNow(live);
            VaultChanged?.Invoke(appId);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Store edited text as a new named preset (the editor's "Save as preset"). Goes through a temp file
    /// so it shares <see cref="Capture"/>'s hashing/dedupe/describe path instead of duplicating it.
    /// </summary>
    public LuaVariant? SaveText(long appId, string text, string? label)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"luavault_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(tmp, text);
            var variant = Capture(appId, tmp, LuaVariantKind.Preset);
            if (variant is null) return null;

            SetEditBase(appId, null); // the edit now has a home of its own
            if (!string.IsNullOrWhiteSpace(label))
            {
                Rename(appId, variant.Hash, label);
                return variant with { Label = label };
            }
            return variant;
        }
        catch { return null; }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    // ── In-place editing ────────────────────────────────────────────

    /// <summary>The variant an in-place <see cref="UpdateVariant"/> would overwrite, or null if the live
    /// lua isn't a pending edit of anything.</summary>
    public string? GetEditBase(long appId)
    {
        lock (_gate) return LoadIndex(appId).EditBaseHash;
    }

    /// <summary>
    /// Remember which variant the live lua is being edited from. Call this BEFORE the first write that
    /// makes it diverge. Once it has, the base is no longer derivable from the bytes.
    /// </summary>
    public void SetEditBase(long appId, string? hash)
    {
        lock (_gate)
        {
            var index = LoadIndex(appId);
            if (index.EditBaseHash == hash) return;
            index.EditBaseHash = hash;
            SaveIndex(appId, index);
        }
    }

    /// <summary>
    /// Overwrite an existing variant with new text, KEEPING its identity. Label, kind and build id all
    /// travel across. The stored file is content-addressed, so its name changes with its bytes: this
    /// writes the new file, repoints the index entry, and drops the old one. That's why "save to the
    /// build I'm on" isn't a plain file write.
    /// </summary>
    public LuaVariant? UpdateVariant(long appId, string oldHash, string text)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"luavault_{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(tmp, text);
            string newHash = HashFile(tmp);

            LuaVariant result;
            lock (_gate)
            {
                var index = LoadIndex(appId);
                int i = index.Variants.FindIndex(v => v.Hash == oldHash);
                if (i < 0) return null; // the base is gone (deleted?). Caller falls back to a new preset

                var old = index.Variants[i];

                // The edited bytes already exist under another entry. Keep that one rather than creating
                // a duplicate, and retire the entry we were editing.
                int existing = index.Variants.FindIndex(v => v.Hash == newHash);
                if (existing >= 0 && existing != i)
                {
                    index.Variants.RemoveAt(i);
                    result = index.Variants.First(v => v.Hash == newHash);
                }
                else
                {
                    Directory.CreateDirectory(AppDir(appId));
                    File.Copy(tmp, VariantPath(appId, newHash), overwrite: true);

                    // Re-describe from the new bytes (depot/DLC counts and pins change with an edit), then
                    // put the original identity back on top.
                    var described = Describe(appId, tmp, newHash, old.Kind, old.BuildId, old.Source);
                    result = described with { Label = old.Label, CapturedAt = old.CapturedAt };
                    index.Variants[i] = result;
                }

                index.EditBaseHash = null; // live now matches a stored variant again
                SaveIndex(appId, index);

                if (newHash != oldHash) DeleteVariantFile(appId, oldHash);
            }

            VaultChanged?.Invoke(appId);
            return result;
        }
        catch { return null; }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    /// <summary>Set (or clear, with null) a variant's user-chosen name. Never touches the file or build id.</summary>
    public void Rename(long appId, string hash, string? label)
    {
        lock (_gate)
        {
            var index = LoadIndex(appId);
            int i = index.Variants.FindIndex(v => v.Hash == hash);
            if (i < 0) return;
            index.Variants[i] = index.Variants[i] with
            {
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim()
            };
            SaveIndex(appId, index);
        }
        VaultChanged?.Invoke(appId);
    }

    /// <summary>Forget a variant (index entry + file). Refuses to delete the one Steam is currently
    /// using. That would leave the live lua orphaned with no way back.</summary>
    public bool Delete(long appId, string hash)
    {
        if (GetActiveHash(appId) == hash) return false;

        lock (_gate)
        {
            var index = LoadIndex(appId);
            if (index.Variants.RemoveAll(v => v.Hash == hash) == 0) return false;
            SaveIndex(appId, index);
        }
        DeleteVariantFile(appId, hash);
        VaultChanged?.Invoke(appId);
        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>Read the lua to fill in the metadata the Builds page shows without re-parsing every file.</summary>
    private static LuaVariant Describe(
        long appId, string luaPath, string hash, string kind, string? buildId, string? source)
    {
        var lua = LuaFileParser.Parse(luaPath, appId);
        var pins = lua?.ActivePins.ToDictionary(p => p.Key.ToString(), p => p.Value)
                   ?? new Dictionary<string, string>();

        return new LuaVariant(
            Hash: hash,
            Kind: buildId is not null ? LuaVariantKind.Build : kind,
            BuildId: buildId,
            Label: null,
            CapturedAt: DateTime.UtcNow,
            Source: source,
            ManifestIds: pins,
            DepotCount: lua?.DepotCount ?? 0,
            DlcCount: lua?.DlcCount ?? 0);
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Stamp a file's timestamps to now, so Manage's "Added" date reflects the switch. Mirrors
    /// <see cref="LuaInstaller"/>'s own StampNow (File.Copy carries the source's timestamps).</summary>
    private static void StampNow(string path)
    {
        try
        {
            var now = DateTime.Now;
            File.SetCreationTime(path, now);
            File.SetLastWriteTime(path, now);
        }
        catch { /* cosmetic */ }
    }

    private VaultIndex LoadIndex(long appId)
    {
        try
        {
            string path = IndexPath(appId);
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<VaultIndex>(File.ReadAllText(path), JsonOpts) is { } loaded)
                return loaded;

            // Present but unreadable → fall back to the .bak rather than starting empty, which a later
            // save would then make permanent.
            string bak = path + ".bak";
            if (File.Exists(bak) &&
                JsonSerializer.Deserialize<VaultIndex>(File.ReadAllText(bak), JsonOpts) is { } fromBak)
                return fromBak;
        }
        catch { /* fall through to a fresh index */ }
        return new VaultIndex { AppId = appId };
    }

    /// <summary>
    /// Atomic write (tmp → move) with a .bak of the last good file, same as
    /// <see cref="SettingsService"/>. A torn index.json would orphan every stored build for that game,
    /// so this is worth more than a plain WriteAllText.
    /// </summary>
    private void SaveIndex(long appId, VaultIndex index)
    {
        index.AppId = appId;
        string path = IndexPath(appId);
        string tmp = path + ".tmp";

        Directory.CreateDirectory(AppDir(appId));
        File.WriteAllText(tmp, JsonSerializer.Serialize(index, JsonOpts));
        try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); } catch { /* best effort */ }
        File.Move(tmp, path, overwrite: true);
    }
}
