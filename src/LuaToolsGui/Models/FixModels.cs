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

// ── Supabase REST (direct, anon) — the recently-added fixes feed ─────
// There's no /api/denuvo "recent" endpoint, so the Fixes page's "Recent" tab reads the public
// `denuvo_fixes` table straight from PostgREST (same pattern as GetStandardUsageAsync reading
// `user_downloads`). Each row comes with its game and tags embedded.

public class DenuvoRecentFix
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("denuvo_games")] public DenuvoRecentGame? Game { get; set; }
    [JsonPropertyName("denuvo_fix_tags")] public List<DenuvoRecentFixTag> FixTags { get; set; } = [];

    /// <summary>The fix's tags, flattened from the denuvo_fix_tags → denuvo_tags embed.</summary>
    [JsonIgnore]
    public IReadOnlyList<DenuvoTag> Tags =>
        FixTags.Where(t => t.Tag is not null).Select(t => t.Tag!).ToList();
}

public class DenuvoRecentGame
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("header_image")] public string? HeaderImage { get; set; }
}

public class DenuvoRecentFixTag
{
    [JsonPropertyName("tag_id")] public string TagId { get; set; } = "";
    [JsonPropertyName("denuvo_tags")] public DenuvoTag? Tag { get; set; }
}
