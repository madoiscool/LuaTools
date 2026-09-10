using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LuaToolsGui.Services;

/// <summary>One backed-up file entry in the backup manifest.</summary>
public sealed record BackupEntry(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>manifest.json describing a backup set. Written into the backup folder itself.</summary>
public sealed record BackupManifest(
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("entries")] List<BackupEntry> Entries);

/// <summary>Summary of the current backup state for UI display.</summary>
public sealed record BackupInfo(bool HasBackup, string? CreatedText, int FileCount);

/// <summary>
/// One-time safety backup of the Steam root before a managed mode ever writes to it, plus the
/// restore half of "revert to vanilla Steam".
///
/// <para>
/// Design notes:
/// • The backup captures the PRE-modification state of the loader DLLs + opensteamtool.toml, the
///   first time a mode install touches the Steam root. If those files were already a mode install
///   (not vanilla), restoring just returns the machine to that earlier state — which is exactly
///   what a backup means. If they were vanilla (the common case), restore is a true revert.
/// • dwmapi.dll/xinput1_4.dll in the Steam root are NOT the Windows system DLLs — they are proxy
///   DLLs a mode places there (or files that were already present before us). Nothing outside the
///   Steam folder is ever touched; "restore originals from System32" is deliberately NOT done
///   because System32 files are irrelevant to the Steam root.
/// • Hashes (sha256) are recorded at backup time so a restore can verify the copies are intact.
/// • The backup is created once and then left alone by later installs, so it always represents
///   the oldest state this app observed.
/// </para>
/// </summary>
public class BackupService(SteamService steam)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Files worth backing up from the Steam root. Only ones WE (a mode install) would
    /// overwrite or that describe our configuration. Never touches Steam's own files.</summary>
    private static readonly string[] RootFilesToBackup =
    [
        "dwmapi.dll",
        "xinput1_4.dll",
        "opensteamtool.toml",
    ];

    /// <summary>Mode payload file (removed on revert; recreated by a future install).</summary>
    public const string OpenSteamToolDll = "OpenSteamTool.dll";

    /// <summary>CloudRedirect add-on payload (removed on revert when present).</summary>
    public const string CloudRedirectDll = "cloud_redirect.dll";

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LuaToolsGui", "backups");

    private static string ManifestPath => Path.Combine(Dir, "manifest.json");

    /// <summary>True when a backup set exists on disk (manifest present with entries).</summary>
    public bool HasBackup
    {
        get
        {
            try
            {
                if (!File.Exists(ManifestPath)) return false;
                var manifest = ReadManifest();
                return manifest is { Entries.Count: > 0 };
            }
            catch { return false; }
        }
    }

    /// <summary>Backup summary for the Mode page UI (exists + when + how many files).</summary>
    public BackupInfo GetInfo()
    {
        try
        {
            var manifest = ReadManifest();
            if (manifest is null || manifest.Entries.Count == 0)
                return new BackupInfo(false, null, 0);

            // Local time, short format — e.g. "9/6/2026 6:46 PM".
            string when = manifest.CreatedAtUtc.LocalDateTime.ToString("g");
            return new BackupInfo(true, when, manifest.Entries.Count);
        }
        catch
        {
            return new BackupInfo(false, null, 0);
        }
    }

    /// <summary>
    /// Back up the Steam-root files we would touch — but ONLY once, and only what actually exists.
    /// Called by <see cref="UnlockerService.InstallAsync"/> before the first managed install. Later
    /// installs keep the original backup so it always preserves the oldest observed state.
    /// Returns true if a backup exists after this call (new or previous).
    /// </summary>
    public bool BackupIfNeeded()
    {
        if (HasBackup) return true; // one-time: never overwrite the original backup

        string? root = steam.EffectivePath;
        if (root is null) return false;

        Directory.CreateDirectory(Dir);

        var entries = new List<BackupEntry>();
        try
        {
            foreach (string file in RootFilesToBackup)
            {
                string src = Path.Combine(root, file);
                if (!File.Exists(src)) continue;

                string dst = Path.Combine(Dir, file);
                File.Copy(src, dst, overwrite: true);
                entries.Add(new BackupEntry(file, AssetHash.OfFile(dst)));
            }

            if (entries.Count == 0)
            {
                // Nothing to back up (fresh Steam, no loader DLLs yet). Don't leave an empty backup
                // dir masquerading as one: remove it so BackupIfNeeded can try again later if needed.
                try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
                return false;
            }

            WriteManifest(new BackupManifest(DateTimeOffset.UtcNow, entries));
            return true;
        }
        catch
        {
            // Failed backup must NOT block install silently-as-success: report no backup. The caller
            // decides whether to proceed (it does — a backup is a nicety, not a hard requirement).
            return false;
        }
    }

    /// <summary>
    /// Restore the backed-up files into the Steam root (overwriting whatever a mode left there),
    /// verify each restored copy's hash, and remove any restored file that fails verification.
    /// Returns per-file failures; empty = full success.
    /// </summary>
    public List<string> RestoreBackedUpFiles()
    {
        var failures = new List<string>();

        var manifest = TryReadManifestOrNull();
        if (manifest is null || manifest.Entries.Count == 0)
        {
            failures.Add("no backup manifest");
            return failures;
        }

        string? root = steam.EffectivePath;
        if (root is null)
        {
            failures.Add("steam not found");
            return failures;
        }

        foreach (var entry in manifest.Entries)
        {
            try
            {
                string src = Path.Combine(Dir, entry.File);
                if (!File.Exists(src))
                {
                    failures.Add(entry.File);
                    continue;
                }

                // Verify the backup copy is intact BEFORE it lands in the Steam root.
                if (!AssetHash.OfFile(src).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(entry.File); // corrupted backup copy → don't propagate it
                    continue;
                }

                string dst = Path.Combine(root, entry.File);
                File.Copy(src, dst, overwrite: true);

                // Double-check what actually landed on disk.
                if (!AssetHash.OfFile(dst).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(dst); } catch { /* best effort */ }
                    failures.Add(entry.File);
                }
            }
            catch
            {
                failures.Add(entry.File); // locked (Steam running?) or IO error
            }
        }

        return failures;
    }

    /// <summary>The backed-up file names, or empty if no backup. For UI text ("restores 3 files").</summary>
    public IReadOnlyList<string> BackedUpFiles()
    {
        try
        {
            return TryReadManifestOrNull()?.Entries.Select(e => e.File).ToList() ?? [];
        }
        catch { return []; }
    }

    /// <summary>Delete the backup set entirely (opt-in "forget the backup" action).</summary>
    public void DeleteBackup()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
        catch { /* best effort */ }
    }

    // ── plumbing ──

    private BackupManifest? TryReadManifestOrNull()
    {
        try { return ReadManifest(); }
        catch { return null; }
    }

    private BackupManifest? ReadManifest()
    {
        if (!File.Exists(ManifestPath)) return null;
        string json = File.ReadAllText(ManifestPath);
        return JsonSerializer.Deserialize<BackupManifest>(json, JsonOpts);
    }

    private static void WriteManifest(BackupManifest manifest)
    {
        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        });
        File.WriteAllText(ManifestPath, json);
    }
}
