using System.Text.Json.Serialization;

namespace GameGuy.Models;

/// <summary>
/// The Steam fixes. Mutually exclusive: only one is active at a time. Each fetches its own files
/// from its own release; switching overwrites as needed but doesn't delete old files.
///
/// The member names are what land in the settings file, and they are deliberately new: no
/// value written by an older build ("SteamTools", "OpenSteamTools", "OpenSteamToolsNightly",
/// "CloudRedirect") parses into this enum. That is what makes <see cref="Services.ModeMigration"/>
/// inherently one-shot. An unparseable value is by definition pre-migration, so no marker flag is
/// needed. Reusing "OpenSteamTools" here would have silently reinterpreted every old stable-OST
/// user as being on the nightly channel.
/// </summary>
public enum UnlockerMode
{
    Ost,     // OpenSteamTools: the nightly channel (madoiscool/OST-Nightly)
    Bst,     // BetterSteamTools: the fork (madoiscool/BetterSteamTools)
    Custom,  // the user manages their own unlocker; the app places nothing
}

/// <summary>How a mode's files are delivered/installed.</summary>
public enum ModeKind
{
    Zip,     // a single release zip to download, verify, and extract
    Manual,  // nothing is downloaded or placed. The user handles their own files
}

/// <summary>State of a mode's files vs. the latest published build.</summary>
public enum ModeStatus
{
    Unknown,        // offline / GitHub unreachable / Steam not located
    NotInstalled,
    UpToDate,
    UpdateAvailable,
    UserManaged,    // Custom mode: nothing to check. Distinct from Unknown, which means "couldn't tell".
}

/// <summary>
/// Static description of one unlocker backend: where its files come from and what to place/clean.
/// </summary>
public sealed record ModeDefinition(
    UnlockerMode Mode,
    string DisplayName,
    string Description,
    ModeKind Kind,
    string Owner,
    string Repo,
    string? FixedTag,        // e.g. "ST"; null → use the repo's latest release
    string[] PlaceFiles,     // files that end up in the Steam root (for status/verify)
    string? ZipAssetPattern, // e.g. "OpenSteamTool-{version}-Release.zip"; null unless Kind == Zip
    // When set, version + hash come from this raw-hosted TOML instead of the GitHub releases API, and
    // the zip URL is built from the version it reports. Costs no api.github.com call, so the mode is
    // immune to the 60 req/hr unauthenticated limit. See UnlockerService.FetchUpdateManifestAsync.
    string? UpdateManifestUrl = null,
    string? HiddenUnlessFile = null); // if set, the card is hidden unless this file exists in the Steam root (or the mode is active)

/// <summary>One published build, as described by a mode's <c>latest.toml</c> update manifest.</summary>
/// <param name="Version">Release tag, e.g. "v1.0.0". Also builds the zip download URL.</param>
/// <param name="File">Bare filename the hash belongs to, e.g. "OpenSteamTool.dll".</param>
/// <param name="Sha256">Lowercase hex digest of that file.</param>
public sealed record UpdateManifest(string Version, string File, string Sha256);

// ── GitHub release API DTOs ─────────────────────────────────────────
public sealed class GithubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    // published_at is reliable for ordering; the /releases list array itself sorts by created_at
    // (which can be identical across tags created at once), so sort by this to find the latest.
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GithubAsset> Assets { get; set; } = [];
}

public sealed class GithubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("digest")] public string? Digest { get; set; } // "sha256:<hex>"

    /// <summary>Asset size in bytes. Lets a tool download report real byte counts instead of a bare
    /// fraction, since GithubProxy.DownloadAsync only reports 0..1.</summary>
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>Queried state for one mode. What a Mode-page card binds to.</summary>
public sealed record ModeState(
    UnlockerMode Mode,
    ModeStatus Status,
    bool IsActive,           // is this the currently-selected (active) mode
    string? LatestVersion);  // resolved release tag (for display)

/// <summary>State of the CloudRedirect add-on (a feature of both the OST nightly and BST builds), derived
/// from disk (cloud_redirect.dll presence + [cloud] enabled in opensteamtool.toml) and the latest
/// CloudRedirect release.</summary>
public sealed record CloudRedirectAddonState(
    bool Installed,          // cloud_redirect.dll is present in the Steam root
    bool Enabled,            // opensteamtool.toml has [cloud] enabled = true
    bool UpdateAvailable,    // on-disk dll differs from the latest release asset
    string? LatestVersion);  // latest CloudRedirect release tag (for display), null if unknown

/// <summary>Outcome of installing/switching to a mode. Mirrors LuaInstaller.InstallResult.</summary>
public sealed record ModeInstallResult(bool Success, string? Error, IReadOnlyList<string> Failed)
{
    public static ModeInstallResult Ok() => new(true, null, []);
    public static ModeInstallResult Fail(string error) => new(false, error, []);
}
