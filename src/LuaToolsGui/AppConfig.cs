namespace LuaToolsGui;

/// <summary>
/// Compiled-in client configuration. The Supabase URL and anon key are public
/// client values (they also ship in the lua.tools web bundle).
/// </summary>
public static class AppConfig
{
    public const string SupabaseUrl = "https://db.lua.tools";

    public const string SupabaseAnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3NzYwMzkzNzYsImV4cCI6MTg5MzQ1NjAwMCwicm9sZSI6ImFub24iLCJpc3MiOiJzdXBhYmFzZSJ9.f_-K38u3odjltP-g_67FVmG32Vg-_-k-lNBvIaVUVBM";

    public const string ApiBaseUrl = "https://lua.tools";

    // Bot-provisioned (Discord /login placeholder) accounts use this email domain. Detecting it on
    // startup lets the app prompt the user to re-link their full lua.tools account.
    public const string BotAccountEmailDomain = "@bot.lua.tools";

    // Hubcap (hubcapmanifest.com): the app talks to this directly with the user's own API key
    // (no lua.tools proxy). Key + stats are managed in Settings; key-gated source downloads hit it.
    public const string HubcapBaseUrl = "https://hubcapmanifest.com";

    /// <summary>Must be registered in Supabase Auth → Redirect URLs.</summary>
    public const int OAuthCallbackPort = 53789;
    public const string OAuthCallbackUrl = "http://localhost:53789/callback";

    // The standard lua.tools daily download cap (Hubcap-keyed downloads are exempt). Hardcoded here
    // because the web app enforces it inline with no API field exposing it; change in one place if it moves.
    public const int DailyDownloadLimit = 25;

    // Public upstream APIs the app calls directly (no lua.tools proxy needed for guest browsing).
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
    /// Public GitHub repos hosting Velopack release assets, in priority order. The auto-updater reads its
    /// feed from the FIRST repo; if that whole repo is unreachable or gone (e.g. banned / DMCA'd / account
    /// removed. A failure the GitHub proxy mirrors can't fix, since they'd still point at a dead repo), it
    /// falls through to the next. Mirror repos are populated MANUALLY (re-upload the same Velopack assets to
    /// the backup only if the primary goes down). Each is still individually proxied for blocked regions.
    /// TODO: set the real primary repo before the first `vpk upload`; add a backup repo URL when one exists.
    /// </summary>
    public static readonly string[] GithubReleasesRepos =
    [
        "https://github.com/not1cyyy/LuaTools",
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
