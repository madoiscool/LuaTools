using System.IO;
using System.Text.Json;
using GameGuy.Models;
using Microsoft.Extensions.Logging;

namespace GameGuy.Services;

/// <summary>
/// One flat list of every applied fix, so "what is applied right now?" is a single small file read
/// instead of a walk over every game folder on every library drive.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a hint, never the truth.</b> The authoritative record of a fix is
/// <c>&lt;install&gt;\.gameguy-fix\&lt;key&gt;.json</c>, which lives with the game and travels with it.
/// Every read here is confirmed against that file before it is believed, and anything that no longer
/// checks out is dropped. Delete this index and nothing is lost: <see cref="RebuildAsync"/> reconstructs
/// it by scanning, which is exactly what the index exists to avoid doing routinely (~10s cold across
/// three drives, versus reading one small file).
/// </para>
/// <para>
/// The index can be wrong in both directions and both are handled. Stale entries — the game was
/// uninstalled, the folder deleted, the fix reverted by another copy of the app — fail the confirm and
/// are pruned. Missing entries — a fix applied by an older build, or a game folder moved in — are found
/// by a rebuild. Nothing here is ever the only place a fix is recorded, or deleting this file would
/// strand backups with nothing pointing at them.
/// </para>
/// </remarks>
public class AppliedFixIndexService(SteamLibraryService library, ILogger<AppliedFixIndexService> log)
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirName);

    private static readonly string FilePath = Path.Combine(Dir, "applied-fixes.json");

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    // ── Reading ──────────────────────────────────────────────────────

    /// <summary>
    /// Every fix that is really applied, newest first. Entries whose record no longer exists on disk are
    /// dropped from the returned list AND from the file, so the index self-heals as games come and go.
    /// </summary>
    public async Task<IReadOnlyList<AppliedFixIndexEntry>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var index = Load();
            var live = new List<AppliedFixIndexEntry>(index.Entries.Count);
            bool pruned = false;

            foreach (var e in index.Entries)
            {
                if (ct.IsCancellationRequested) break;
                if (RecordExists(e)) live.Add(e);
                else pruned = true;
            }

            if (pruned) Save(new AppliedFixIndex { Entries = live });

            return live
                .OrderByDescending(e => e.AppliedAt, StringComparer.Ordinal) // ISO-8601, so ordinal is chronological
                .ToList();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Rebuild from scratch by scanning every installed game for a <c>.gameguy-fix</c> folder.
    /// </summary>
    /// <remarks>
    /// The slow path the index exists to avoid, kept because it is the only way to recover from a deleted
    /// index or to pick up fixes applied by a build that predates it. Off the UI thread; the caller
    /// decides when it is worth paying for.
    /// </remarks>
    public async Task<IReadOnlyList<AppliedFixIndexEntry>> RebuildAsync(CancellationToken ct = default)
    {
        var found = await Task.Run(() => Scan(ct), ct);

        await _gate.WaitAsync(ct);
        try { Save(new AppliedFixIndex { Entries = found }); }
        finally { _gate.Release(); }

        log.LogDebug("Rebuilt the applied-fix index by scanning: {Count} fix(es)", found.Count);
        return found;
    }

    // ── Writing ──────────────────────────────────────────────────────

    /// <summary>Record a fix as applied, replacing any existing entry for the same game + fix.</summary>
    public void Add(long appId, string fixId, string gameName, string installDir, string appliedAt, int fileCount)
    {
        Mutate(entries =>
        {
            entries.RemoveAll(e => e.AppId == appId && e.FixId == fixId);
            entries.Add(new AppliedFixIndexEntry
            {
                AppId = appId,
                FixId = fixId,
                GameName = gameName,
                InstallDir = installDir,
                AppliedAt = appliedAt,
                FileCount = fileCount,
            });
        });
    }

    /// <summary>Drop a fix from the index. Safe to call when it was never in there.</summary>
    public void Remove(long appId, string fixId) =>
        Mutate(entries => entries.RemoveAll(e => e.AppId == appId && e.FixId == fixId));

    // ── Internals ────────────────────────────────────────────────────

    /// <summary>
    /// Read-modify-write under the lock. Best-effort throughout: the index is a cache, so a failed write
    /// must never take down the apply or revert that triggered it — the real record is already on disk.
    /// </summary>
    private void Mutate(Action<List<AppliedFixIndexEntry>> change)
    {
        _gate.Wait();
        try
        {
            var index = Load();
            change(index.Entries);
            Save(index);
        }
        catch (Exception ex) { log.LogDebug(ex, "Could not update the applied-fix index"); }
        finally { _gate.Release(); }
    }

    private static AppliedFixIndex Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppliedFixIndex();
            return JsonSerializer.Deserialize<AppliedFixIndex>(File.ReadAllText(FilePath), JsonOpts)
                   ?? new AppliedFixIndex();
        }
        catch { return new AppliedFixIndex(); } // corrupt or half-written: start clean, a rebuild restores it
    }

    private static void Save(AppliedFixIndex index)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(index, JsonOpts));
        }
        catch { /* best effort: this is a cache */ }
    }

    /// <summary>Confirm an index entry against the record that actually governs the fix.</summary>
    private static bool RecordExists(AppliedFixIndexEntry e)
    {
        try
        {
            return !string.IsNullOrEmpty(e.InstallDir)
                && File.Exists(Downloads.ManifestJobFactory.GetFixRecordPath(e.InstallDir, e.FixId));
        }
        catch { return false; }
    }

    /// <summary>Walk installed games looking for fix records. The expensive path, hence the index.</summary>
    private List<AppliedFixIndexEntry> Scan(CancellationToken ct)
    {
        var found = new List<AppliedFixIndexEntry>();

        foreach (var game in library.EnumerateInstalled())
        {
            if (ct.IsCancellationRequested) break;

            string installDir = game.InstallDir;
            string fixDir = Path.Combine(installDir, Downloads.ManifestJobFactory.FixRecordDir);
            string[] records;
            try
            {
                if (!Directory.Exists(fixDir)) continue;
                records = Directory.GetFiles(fixDir, "*.json");
            }
            catch { continue; }

            foreach (string path in records)
            {
                var record = Downloads.ManifestJobFactory.ReadFixRecord(path);
                if (record is null || string.IsNullOrEmpty(record.FixId)) continue;

                found.Add(new AppliedFixIndexEntry
                {
                    AppId = record.AppId != 0 ? record.AppId : game.AppId,
                    FixId = record.FixId,
                    GameName = game.Name,
                    InstallDir = installDir,
                    AppliedAt = record.AppliedAt,
                    FileCount = record.Files.Count,
                });
            }
        }

        return found;
    }
}
