using System.Text.Json.Serialization;

namespace LuaToolsGui.Services.Sources;

/// <summary>
/// One <c>.json</c> file in the source-pack folder. Pure data: a pack declares where manifests can be
/// fetched from and nothing else. There is no code path here by design — a pack cannot execute anything,
/// which is what makes it safe to install one from someone you don't know.
/// </summary>
public sealed class SourcePack
{
    /// <summary>
    /// Format version of the file. Bumped only when a change would make an older build misread a newer
    /// pack; a build refuses a schema from the future rather than guess at what it means.
    /// </summary>
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    /// <summary>Shown in the pack list. Falls back to the file name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("sources")]
    public List<SourceEntry> Sources { get; set; } = [];
}

/// <summary>The shapes of source LuaTools knows how to fetch. A pack picks one; it cannot supply its own.</summary>
public enum SourceKind
{
    /// <summary>One <c>&lt;appid&gt;.zip</c> per game, holding the lua and its <c>.manifest</c> files.</summary>
    ManifestZip,

    /// <summary>One <c>&lt;appid&gt;.lua</c> per game: entitlements and depot keys, no manifests.</summary>
    LuaFile,
}

/// <summary>A single source declared by a pack.</summary>
public sealed class SourceEntry
{
    /// <summary>
    /// Key used for the row, ordering and the download route. Must not be one the app already uses —
    /// a pack that claims an existing name is refused rather than silently shadowing it.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>What the Add page's row shows. Falls back to <see cref="Name"/>.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>"manifestZip" or "luaFile", case-insensitive.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>Where to fetch, containing the literal token <c>{appid}</c>.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>
    /// Tried in order when the primary url is unreachable. The reason packs are worth having at all: a
    /// repo that moves or goes stale becomes a line to edit rather than a release to cut.
    /// </summary>
    [JsonPropertyName("mirrors")]
    public List<string> Mirrors { get; set; } = [];

    /// <summary>Short label on the row. Cosmetic; the app never reads it back as a capability.</summary>
    [JsonPropertyName("badge")]
    public string? Badge { get; set; }
}
