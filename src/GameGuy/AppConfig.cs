namespace GameGuy;

/// <summary>Compiled-in client configuration.</summary>
public static class AppConfig
{
    /// <summary>Display name of this app (window title, tray, toasts, dialogs).</summary>
    public const string AppName = "GameGuy";

    /// <summary>Folder under %APPDATA% that holds settings, caches and installed tools.</summary>
    public const string DataDirName = "GameGuy";

    /// <summary>URL scheme for browser/protocol links (gameguy://install/&lt;appid&gt;).</summary>
    public const string ProtocolScheme = "gameguy";

    /// <summary>Stable identity used for the cross-process lock + wake-up signals. Never rename this:
    /// a second copy of the app has to find the same name to hand off to the running instance.</summary>
    public const string InstanceId = "GameGuy";

    /// <summary>The lua.tools API this build talks to (backend, not branding).</summary>
    public const string ApiBaseUrl = "https://lua.tools";

    // Hubcap (hubcapmanifest.com): the app talks to this directly with the user's own API key
    // (no lua.tools proxy). Key + stats are managed in Settings; key-gated source downloads hit it.
    public const string HubcapBaseUrl = "https://hubcapmanifest.com";

    // The standard lua.tools daily download cap (Hubcap-keyed downloads are exempt). Hardcoded here
    // because the web app enforces it inline with no API field exposing it; change in one place if it moves.
    public const int DailyDownloadLimit = 25;

    // Public upstream APIs the app calls directly (browsing needs no authentication key).
    public const string SteamStoreSearchUrl = "https://store.steampowered.com/api/storesearch/";
    // Steam's storefront "featured categories" (top sellers, new releases, etc.). Drives the Add page's
    // featured strips. Public, no auth.
    public const string SteamFeaturedUrl = "https://store.steampowered.com/api/featuredcategories";

    // Community list of Steam "hardware" appids (Steam Deck, Index, controllers, VR headsets). Fetched
    // via GithubProxy (raw.githubusercontent.com → mirror fallback) and cached ~14 days, to filter
    // hardware out of featured/search. Array of { "appid": <long>, "name": ... } objects.
    public const string HardwareAppIdListUrl =
        "https://raw.githubusercontent.com/jsnli/steamappidlist/master/data/hardware_appid.json";

    // Steamless (atom0s): strips SteamStub DRM from a game's .exe. Downloaded via GithubProxy and
    // cached locally; the "Remove Steam DRM" Manage action runs Steamless.CLI.exe against the game's exe.
    public const string SteamlessRepo = "atom0s/Steamless";

    // CloudRedirect (Selectively11): the Mode page "Manage" button downloads the latest CloudRedirect.exe
    // GUI manager from here and launches it. (Separate from the CLI fixer used by the mode install flow.)
    public const string CloudRedirectRepo = "Selectively11/CloudRedirect";

    // SteamAutoCrack: the Downloads page button fetches this release and launches its GUI.
    //
    // Three facts about the shipped asset drive how SteamAutoCrackService handles it:
    //   * GUI ONLY. The release contains a single exe and no SteamAutoCrack.CLI.exe, and that GUI parses
    //     no command-line arguments, so we can open it and nothing more. There is no way to drive a
    //     crack from here; the user does everything in their own window.
    //   * FRAMEWORK-DEPENDENT net10.0-windows. It is a single-file bundle but the runtime is NOT inside
    //     it (no hostpolicy/coreclr/System.Private.CoreLib, no includedFrameworks), so the .NET 10
    //     DESKTOP runtime must be present. We install it on demand via Velopack's Runtimes API.
    //   * Its zip has a real directory TREE (Goldberg/, TEMP/ beside the exe), unlike the flat zip
    //     DepotDownloaderService expects. Their exe resolves those paths from its own base directory,
    //     so extract without flattening.
    public const string SteamAutoCrackRepo = "SteamAutoCracks/Steam-auto-crack";

    // DepotDownloaderMod: downloads raw depot content from Steam's CDN using depot keys + a local manifest.
    // Powers the Depots page "Download" action.
    //
    // This is a RE-PACK we host ourselves, NOT the upstream SteamAutoCracks/DepotDownloaderMod repo, for two
    // reasons: upstream ships its assets as `Release.rar` (System.IO.Compression cannot read RAR), and its
    // build is framework-dependent net9.0 while this app is net8.0-windows, so users without the .NET 9
    // runtime couldn't run it. Note that adding a RAR reader would fix only the FIRST of those.
    //
    // The re-pack is produced automatically by the `repack.yml` workflow in that repo: on each upstream
    // release it rebuilds their source at that tag as a SELF-CONTAINED win-x64 publish and uploads a FLAT
    // zip. Both properties are load-bearing for DepotDownloaderService.EnsureToolAsync, which takes the
    // first `.zip` asset, extracts it straight into ToolDir, and then expects DepotDownloaderMod.exe at the
    // archive ROOT (unlike PluginInstallerService, it has no single-top-level-folder hoisting).
    public const string DepotDownloaderRepo = "mendy-tools/DepotDownloaderMod";
    public const string ManifestBackendUrl = "http://167.235.229.108";
    public const string ManifestBackendUserAgent = "secretgoonpoon";

    // The donate-keys endpoint gates on a DIFFERENT User-Agent than the manifest backend (it 403s
    // otherwise). This matches the LuaTools plugin's config.USER_AGENT.
    public const string DonateKeysUserAgent = "discord(dot)gg/luatools";

    // ── Umami analytics (anonymous app-launch counting) ──────────────
    public const string UmamiHost = "https://analytics.lua.tools";
    public const string UmamiWebsiteId = "820d782c-a434-424f-9f90-dee83dc6032e";
    public const string UmamiHostname = "desktop.lua.tools";

    /// <summary>
    /// Public GitHub repos hosting THIS fork's Velopack release assets, in priority order. The auto-updater
    /// reads its feed from the FIRST repo; if that whole repo is unreachable or gone (banned / DMCA'd /
    /// account removed — something the GitHub proxy mirrors can't fix, since they'd still point at a dead
    /// repo), it falls through to the next. Each is still individually proxied for blocked regions.
    ///
    /// Upstream's repos (madoiscool/LuaTools, mendy-tools/LuaTools) are deliberately NOT in this list: this
    /// is an independent build, so auto-updating would replace it with upstream's. Every release of the
    /// chosen repo must bump the csproj Version (Velopack only updates to a STRICTLY higher version).
    /// </summary>
    public static readonly string[] GithubReleasesRepos =
    [
        "https://github.com/khalyld9/GameGuy",      // primary
        // "https://github.com/<backup-owner>/GameGuy",  // backup. Add + upload the same Velopack assets
                                                        // ONLY if the primary goes down (404s harmlessly until
                                                        // then; UpdateService just falls through past it).
    ];

    /// <summary>The primary releases repo (first in <see cref="GithubReleasesRepos"/>).</summary>
    public static string GithubReleasesRepo => GithubReleasesRepos[0];

    // ── Plugin releases (the store-page plugin manager fetches these) ──────────────
    // Separate from the app's own Velopack self-update repo above. Each release of this repo carries
    // `plugin.zip` (the frontend) + `winmm.dll` (the loader); the tag is the version (e.g. "v1.2").
    // Fetched + verified (by asset sha256 digest) through GithubProxy like everything else.
    public const string PluginReleasesOwner = "madoiscool";
    public const string PluginReleasesRepo = "LTSP";

    // ── GitHub proxy mirrors (for blocked/throttled regions, e.g. China) ──────────────
    // github.com / api.github.com are often unreachable in some countries. Any GitHub request is tried
    // DIRECT first, then prefixed onto the MATCHING mirrors ("<mirror>https://<github-url>") until one works.
    // Two capability classes: GithubProxy.Candidates picks by URL so we never make a guaranteed-wasted hop
    // (an API mirror 400s a download; a download mirror 403s the API):
    //   • API metadata (api.github.com): ONLY our self-hosted lua.tools/gh proxy can serve it. Server-side
    //     PAT (60→5000/hr) + cache. No PUBLIC proxy serves the REST API (they all 403 it), so there's no
    //     public backup here. Fixes the plugin release-metadata lookup in China / under rate-limit. 404s
    //     harmlessly until the /api/gh route is deployed, then lights up automatically.
    //   • Downloads (github.com releases / raw / objects): the public download proxies. lua.tools/gh is
    //     API-only (its route 400s downloads) so it is deliberately NOT in this list.
    public static readonly string[] GithubApiMirrors =
    [
        "https://lua.tools/api/gh/",   // self-hosted route (src/app/api/gh/[...rest]): proxies api.github.com with our PAT
    ];
    public static readonly string[] GithubDownloadMirrors =
    [
        "https://ghproxy.net/",    // download-only (verified live 2026-07)
        "https://ghfast.top/",     // download-only
        "https://gh.ddlc.top/",    // download-only
    ];
}
