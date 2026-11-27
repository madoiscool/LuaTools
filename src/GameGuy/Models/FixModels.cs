using System.Text.Json.Serialization;

namespace GameGuy.Models;

// ── /api/denuvo/listings (public). The game grid ───────────────────

public class DenuvoListingsResponse
{
    [JsonPropertyName("games")] public List<DenuvoGameListing> Games { get; set; } = [];
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; set; } = [];
}

public class DenuvoGameListing
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("header_image")] public string? HeaderImage { get; set; }
    [JsonPropertyName("fixCount")] public int FixCount { get; set; }
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; set; } = [];
}

public class DenuvoTag
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("color")] public string? Color { get; set; }
}

// ── /api/denuvo/fixes?appid= (public). Per-game fix detail ──────────

public class DenuvoFixesResponse
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("header_image")] public string? HeaderImage { get; set; }
    [JsonPropertyName("fixes")] public List<DenuvoFix> Fixes { get; set; } = [];
}

public class DenuvoFix
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; set; } = [];
    [JsonPropertyName("hasManifest")] public bool HasManifest { get; set; }
    [JsonPropertyName("hasFix")] public bool HasFix { get; set; }
    [JsonPropertyName("manifestFilename")] public string? ManifestFilename { get; set; }
    [JsonPropertyName("fixFilename")] public string? FixFilename { get; set; }
    [JsonPropertyName("createdAt")] public string? CreatedAt { get; set; }
}

// ── /api/denuvo/download?fix=&slot= (auth). Returns a signed URL ────

public class DenuvoDownloadResponse
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

// ── Fix revert record (written to .gameguy-fix/ inside the game folder) ──
//
// Deliberately NOT called a manifest: in this app that word means a Steam depot manifest (depotcache,
// ResolveManifestPath, the Fixes page's own Manifest button), and having a second meaning made people
// read "no record of this fix" as "your depot manifests are gone". The JsonPropertyName values below
// are the on-disk contract and must not change with the type names.

/// <summary>
/// What one applied fix changed, written into the game's own folder so it travels with the install.
/// </summary>
/// <remarks>
/// The per-file hashes are what make several fixes on one game safe. Fix B applied over fix A backs up
/// A's file, so the backups CHAIN and must be unwound newest-first. Reverting A first would restore the
/// pre-A original over B's file and leave the game with a mix neither fix expects. Rather than police
/// the order, each entry records the hash of what the fix wrote: if the file on disk no longer matches,
/// something replaced it after us and the revert stops instead of clobbering it. That catches the
/// out-of-order case, and also game updates and hand-edits, which no ordering rule would.
///
/// Older records predate the hashes and leave them null. Those revert exactly as they always did — an
/// absent hash means "cannot verify", never "verification failed".
/// </remarks>
public class DenuvoFixRecord
{
    [JsonPropertyName("appId")] public long AppId { get; set; }
    [JsonPropertyName("fixId")] public string FixId { get; set; } = "";
    [JsonPropertyName("appliedAt")] public string AppliedAt { get; set; } = "";
    [JsonPropertyName("files")] public List<DenuvoFixRecordEntry> Files { get; set; } = [];
}

public class DenuvoFixRecordEntry
{
    [JsonPropertyName("relativePath")] public string RelativePath { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = ""; // "modified" or "added"
    [JsonPropertyName("backupPath")] public string? BackupPath { get; set; } // relative to .gameguy-fix/

    /// <summary>
    /// SHA-256 of the file as the fix left it. Lets a revert prove the file on disk is still the one this
    /// fix wrote before touching it — see <c>DenuvoFixRecord</c> for why that matters.
    /// </summary>
    [JsonPropertyName("hashAfter")] public string? HashAfter { get; set; }

    /// <summary>
    /// SHA-256 of the original, pre-fix file. Null for <c>"added"</c> entries (there was no original).
    /// Used to verify a restored backup really is the original and not a corrupt copy.
    /// </summary>
    [JsonPropertyName("hashBefore")] public string? HashBefore { get; set; }
}

// ── Applied-fix index (%AppData%\GameGuy\applied-fixes.json) ──
//
// A HINT, never the truth. The per-game .gameguy-fix/<key>.json inside the install folder is
// authoritative; this is one flat list so "what fixes are applied?" doesn't have to walk every game
// folder on every library drive (measured at ~10s cold across three drives, versus reading one small
// file). Safe to delete: a missing or stale index rebuilds from a scan, and every entry is confirmed
// against its real record before being shown. It must never be the only place a fix is recorded, or
// deleting it would strand backups with nothing pointing at them.

public class AppliedFixIndex
{
    [JsonPropertyName("entries")] public List<AppliedFixIndexEntry> Entries { get; set; } = [];
}

public class AppliedFixIndexEntry
{
    [JsonPropertyName("appId")] public long AppId { get; set; }
    [JsonPropertyName("fixId")] public string FixId { get; set; } = "";
    [JsonPropertyName("gameName")] public string GameName { get; set; } = "";
    [JsonPropertyName("installDir")] public string InstallDir { get; set; } = "";
    [JsonPropertyName("appliedAt")] public string AppliedAt { get; set; } = "";
    [JsonPropertyName("fileCount")] public int FileCount { get; set; }
}
