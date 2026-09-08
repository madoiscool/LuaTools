using System.Text.Json.Serialization;

namespace LuaToolsGui.Models;

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

// ── Fix revert manifest (written to .luatools-fix/ inside the game folder) ──

public class DenuvoFixManifest
{
    [JsonPropertyName("appId")] public long AppId { get; set; }
    [JsonPropertyName("fixId")] public string FixId { get; set; } = "";
    [JsonPropertyName("appliedAt")] public string AppliedAt { get; set; } = "";
    [JsonPropertyName("files")] public List<DenuvoFixManifestEntry> Files { get; set; } = [];
}

public class DenuvoFixManifestEntry
{
    [JsonPropertyName("relativePath")] public string RelativePath { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = ""; // "modified" or "added"
    [JsonPropertyName("backupPath")] public string? BackupPath { get; set; } // relative to .luatools-fix/
}
