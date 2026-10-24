using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

public class HttpServerService : IHostedService
{
    private readonly LuaInstaller _installer;
    private readonly SteamService _steam;
    private readonly CacheService _cache;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpServerService> _log;
    private HttpListener? _listener;
    private CancellationTokenSource? _appCts;

    // appid -> the queue item for its manifest download. Retained after completion so a late poll from
    // the store-page popup still sees "done" (the previous DownloadState dictionary behaved the same way).
    private readonly ConcurrentDictionary<long, Services.Downloads.DownloadItem> _downloads = new();
    private List<ApiSource> _apiSources = new();
    private bool _apiSourcesLoaded = false;

    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "LuaTools", "downloads");
    private const string ManifestBackendUrl = "http://167.235.229.108/check_apis";

    public HttpServerService(LuaInstaller installer, SteamService steam, CacheService cache,
        IServiceProvider services, ILogger<HttpServerService> logger)
    {
        _installer = installer;
        _steam = steam;
        _cache = cache;
        _services = services;
        _log = logger;
        Directory.CreateDirectory(TempDir);
    }

    private void LoadApiSources()
    {
        if (_apiSourcesLoaded) return;
        _apiSourcesLoaded = true;

        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "public", "api.json"),
            Path.Combine(AppContext.BaseDirectory, "api.json"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("api_list", out var list))
                    {
                        _apiSources = new();
                        foreach (var entry in list.EnumerateArray())
                        {
                            var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var url = entry.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                            var successCode = entry.TryGetProperty("success_code", out var sc) ? sc.GetInt32() : 200;
                            var enabled = !entry.TryGetProperty("enabled", out var en) || en.GetBoolean();
                            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url) && enabled)
                                _apiSources.Add(new ApiSource(name, url, successCode));
                        }
                    }
                    _log.LogInformation("Loaded {Count} API sources from api.json", _apiSources.Count);
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Failed to parse api.json: {Message}", ex.Message);
                }
            }
        }
        _log.LogWarning("api.json not found, using fallback sources");
        _apiSources = new()
        {
            new("Ryuu", "http://167.235.229.108/<appid>", 200),
            new("Sushi", "https://raw.githubusercontent.com/sushi-dev55-alt/sushitools-games-repo-alt/refs/heads/main/<appid>.zip", 200),
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _appCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:6767/");
        try { _listener.Start(); }
        catch (HttpListenerException)
        {
            _log.LogWarning("HttpListener could not start on 127.0.0.1:6767, attempting netsh reservation");
            try
            {
                // Reserve the URL for the current user only. "Everyone" let any other account on the
                // machine bind this port if the app was not running.
                string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", $"http add urlacl url=http://127.0.0.1:6767/ user=\"{user}\"")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);
                _listener.Start();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to start HTTP server on :6767");
                return Task.CompletedTask;
            }
        }

        _log.LogInformation("HTTP server listening on http://127.0.0.1:6767");
        _ = Task.Run(() => ListenLoop(_appCts.Token), _appCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _appCts?.Cancel();
        try { _listener?.Stop(); } catch { }
        return Task.CompletedTask;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(ctx), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch { }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;

        resp.ContentType = "application/json; charset=utf-8";

        try
        {
            string? origin = req.Headers["Origin"];
            if (!IsTrustedRequest(req.Headers["Host"], origin))
            {
                PluginLog.Log($"HTTP rejected {req.HttpMethod} {req.Url?.AbsolutePath} host='{req.Headers["Host"]}' origin='{origin}'");
                resp.StatusCode = 403;
                await resp.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(JsonErr("Forbidden")));
                return;
            }
            SetCors(resp, origin);

            string? path = req.Url?.AbsolutePath.TrimEnd('/');
            string method = req.HttpMethod;
            // Log everything except the noisy status poll.
            if (path is not null && !path.StartsWith("/add-status/") && !path.StartsWith("/has/"))
                PluginLog.Log($"HTTP {method} {path}");
            (int status, string body) = path switch
            {
                // Answer CORS preflight FIRST so it never falls through to a route.
                _ when method == "OPTIONS" => (204, ""),
                var p when Match(method, "GET", p, "/has/{appid}", out var id) => await HandleHas(id),
                // Steam-plugin headless add: reflects the app's real DownloadViewModel.
                var p when Match(method, "POST", p, "/add/{appid}", out var id) => await HandleAdd(id, req),
                var p when Match(method, "GET", p, "/add-status/{appid}", out var id) => HandleAddStatus(id),
                var p when Match(method, "POST", p, "/add-source/{appid}", out var id) => await HandleAddSource(id, req),
                var p when Match(method, "POST", p, "/check-sources/{appid}", out var id) => await HandleCheckSources(id),
                var p when Match(method, "POST", p, "/download/{appid}", out var id) => await HandleDownload(id, req),
                var p when Match(method, "GET", p, "/download-status/{appid}", out var id) => HandleStatus(id),
                var p when Match(method, "POST", p, "/cancel/{appid}", out var id) => HandleCancel(id),
                var p when Match(method, "POST", p, "/remove/{appid}", out var id) => HandleRemove(id),
                var p when Match(method, "POST", p, "/open/fix/{appid}", out var id) => HandleOpenFix(id),
                "/open/settings" when req.HttpMethod == "POST" => HandleOpenSettings(),
                "/open-url" when req.HttpMethod == "POST" => await HandleOpenUrl(req),
                "/restart-steam" when req.HttpMethod == "POST" => HandleRestartSteam(),
                "/check-updates" when req.HttpMethod == "POST" => await HandleCheckUpdates(),
                "/loaded-apps" when req.HttpMethod == "GET" => await HandleReadLoadedApps(),
                "/loaded-apps" when req.HttpMethod == "POST" => HandleDismissLoadedApps(),
                "/api-list" when req.HttpMethod == "GET" => HandleApiList(),
                "/icon" when req.HttpMethod == "GET" => HandleIcon(),
                _ => (404, JsonErr("Not found")),
            };

            resp.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(body);
            await resp.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            resp.StatusCode = 500;
            var body = Encoding.UTF8.GetBytes(JsonErr(ex.Message));
            await resp.OutputStream.WriteAsync(body);
        }
        finally
        {
            resp.Close();
        }
    }

    /// <summary>
    /// Match a route by HTTP method AND path, and parse its numeric {appid}. The method check matters:
    /// without it a plain GET (an &lt;img src&gt; on any web page needs no CORS) reached state-changing
    /// POST routes such as /remove/{appid}. A non-numeric id is simply not a match, so it ends in a
    /// 404 instead of a 500 from long.Parse.
    /// </summary>
    internal static bool Match(string method, string expectedMethod, string? path, string pattern, out long id)
    {
        id = 0;
        if (path is null || !string.Equals(method, expectedMethod, StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = pattern.TrimEnd('/').Split('/');
        var pathParts = path.Split('/');
        if (parts.Length != pathParts.Length) return false;
        bool hasId = false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("{"))
            {
                if (!long.TryParse(pathParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                    return false;
                hasId = true;
                continue;
            }
            if (!string.Equals(parts[i], pathParts[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return hasId;
    }

    // Steam's own surfaces that legitimately host the store-page plugin. Requests from the app itself
    // (CefInjectorService's HttpClient) carry no Origin header at all.
    private static readonly string[] TrustedOriginHosts =
        ["store.steampowered.com", "steamcommunity.com", "steamloopback.host"];

    /// <summary>
    /// Reject what a hostile web page in the user's browser could send. The Host check defeats DNS
    /// rebinding (a page whose own name resolves to 127.0.0.1 still sends its own name as Host); the
    /// Origin check stops any other site's fetch/form from driving the backend.
    /// </summary>
    internal static bool IsTrustedRequest(string? host, string? origin)
    {
        if (host is not ("127.0.0.1:6767" or "localhost:6767")) return false;
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return TrustedOriginHosts.Any(h => string.Equals(uri.Host, h, StringComparison.OrdinalIgnoreCase));
    }

    // ── Endpoint handlers ─────────────────────────────────────────────

    private Task<(int, string)> HandleHas(long appId)
    {
        var exists = _installer.ReadInstalledLua(appId) != null;
        return Task.FromResult((200, Json(new { success = true, exists })));
    }

    // ── Steam-plugin add: drive + reflect the real DownloadViewModel ──

    /// <summary>Trigger the fully headless add (PluginAddService. Dynamic sources, Hubcap, key-gating,
    /// usage, FastFetch auto-download). Uses services only; the app window is never touched.</summary>
    private async Task<(int, string)> HandleAdd(long appId, HttpListenerRequest req)
    {
        // The store page passes the game name it already displays, so PluginAddService can skip a
        // lua.tools /details lookup. Best-effort: a missing/blank name just falls back to a fetch.
        string? name = null;
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var json = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("name", out var n))
                name = n.GetString();
        }
        catch { }
        _services.GetRequiredService<PluginAddService>().Start(appId, name);
        return (200, Json(new { success = true }));
    }

    /// <summary>Serialize the headless add state so the plugin popup mirrors what the app would show.</summary>
    private (int, string) HandleAddStatus(long appId)
    {
        var svc = _services.GetRequiredService<PluginAddService>();
        var st = svc.GetState(appId);
        bool installed = _installer.ReadInstalledLua(appId) != null;
        if (st is null)
            return (200, Json(new { success = true, checking = false, sourcesLoaded = false, sources = Array.Empty<object>(), installed }));

        var sources = st.Sources.Select(s => (object)new
        {
            name = s.Name,
            displayName = s.DisplayName,
            status = s.Status,
            available = s.Available,
            canDownload = s.CanDownload,
            locked = s.Locked,
            needsKey = s.NeedsKey,
            stats = s.Stats,
            downloading = s.Downloading,
            progress = s.Progress,
            indeterminate = s.Indeterminate,
        }).ToList();

        return (200, Json(new
        {
            success = true,
            appid = st.AppId,
            checking = st.Checking,
            fastFetch = st.FastFetch,
            sourcesLoaded = st.SourcesLoaded,
            sources,
            installStatus = st.InstallStatus,
            installFailed = st.InstallFailed,
            error = st.Error,
            installed,
        }));
    }

    /// <summary>Plugin picked a source by name (FastFetch-off path) → download+install it headlessly.</summary>
    private async Task<(int, string)> HandleAddSource(long appId, HttpListenerRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = await reader.ReadToEndAsync();
        string source = "";
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("source", out var s))
                source = s.GetString() ?? "";
        }
        catch { }
        PluginLog.Log($"/add-source/{appId} body='{body}' parsed source='{source}'");
        if (string.IsNullOrWhiteSpace(source)) return (400, JsonErr("source is required"));

        _services.GetRequiredService<PluginAddService>().Pick(appId, source);
        return (200, Json(new { success = true }));
    }

    private async Task<(int, string)> HandleCheckSources(long appId)
    {
        // Dynamic source list from the app's real manifest backend (same call the app's
        // DownloadViewModel uses). Sources have no per-source URL. Downloads go through
        // the app's authenticated proxy by source NAME (see HandleDownload).
        try
        {
            var api = _services.GetRequiredService<LuaToolsApiClient>();
            var statuses = await api.CheckSourcesAsync(appId.ToString());
            var results = statuses
                .Select(kv => (object)new { name = kv.Key, available = kv.Value == "available", url = (string?)null })
                .ToList();
            return (200, Json(new { success = true, results }));
        }
        catch (Exception ex)
        {
            return (200, Json(new { success = false, error = ex.Message, results = Array.Empty<object>() }));
        }
    }

    private async Task<(int, string)> HandleDownload(long appId, HttpListenerRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = await reader.ReadToEndAsync();

        var json = JsonSerializer.Deserialize<JsonElement>(body);
        // Download is by source NAME (the app's authenticated proxy resolves it). Accept
        // "source" or legacy "apiName".
        string source = json.TryGetProperty("source", out var s) ? s.GetString() ?? ""
            : json.TryGetProperty("apiName", out var a) ? a.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(source))
            return (400, JsonErr("source is required"));

        // The queue's DedupeKey is the real duplicate guard; this keeps the documented 409 contract.
        var queue = _services.GetRequiredService<Services.Downloads.DownloadQueue>();
        if (queue.FindActive($"manifest:{appId}") is not null)
            return (409, JsonErr("Download already in progress for this app"));

        var jobs = _services.GetRequiredService<Services.Downloads.ManifestJobFactory>();
        _downloads[appId] = queue.Enqueue(jobs.CreateManifestJob(appId, null, source, needsKey: false));

        return (200, Json(new { success = true }));
    }

    /// <summary>
    /// Project a queue item onto the EXACT JSON the store-page plugin already expects. The field names
    /// and the status vocabulary are a published contract consumed by luatools.js (via main.lua's
    /// GetAddViaLuaToolsStatus), so nothing here may drift.
    /// </summary>
    private (int, string) HandleStatus(long appId)
    {
        if (!_downloads.TryGetValue(appId, out var item))
            return (200, Json(new { success = true, state = (object?)null }));

        bool done = item.Status == Services.Downloads.DownloadStatus.Completed;
        var (bytesRead, totalBytes) = LegacyBytes(item);

        var payload = new
        {
            status = StatusWireName(item.Status),
            bytesRead,
            totalBytes,
            currentApi = item.SubTitle,
            apiErrors = (object?)null, // nothing ever populated this
            error = item.Status is Services.Downloads.DownloadStatus.Failed
                                or Services.Downloads.DownloadStatus.Cancelled ? item.Message : null,
            installedPath = (string?)null,
            success = done,
            api = done ? item.SubTitle : null,
        };
        return (200, Json(new { success = true, state = payload }));
    }

    /// <summary>
    /// The wire vocabulary. Note "failed", NOT "error": the plugin's startPolling shows its failure UI
    /// on "failed", and the old DownloadState comment claiming "error" was simply wrong.
    /// </summary>
    private static string StatusWireName(Services.Downloads.DownloadStatus s) => s switch
    {
        Services.Downloads.DownloadStatus.Queued => "queued",
        Services.Downloads.DownloadStatus.Downloading => "downloading",
        Services.Downloads.DownloadStatus.AwaitingConfirmation => "processing",
        Services.Downloads.DownloadStatus.Installing => "processing",
        Services.Downloads.DownloadStatus.Completed => "done",
        Services.Downloads.DownloadStatus.Cancelled => "cancelled",
        _ => "failed",
    };

    /// <summary>
    /// Real byte counts when the response had a Content-Length. When it did not, fall back to exactly
    /// what this endpoint used to synthesize (0 of 100) rather than 0/0, so an unknown-length download
    /// renders no worse in the popup than it did before.
    /// </summary>
    private static (long BytesRead, long TotalBytes) LegacyBytes(Services.Downloads.DownloadItem item) =>
        item.TotalBytes is > 0 ? (item.BytesRead, item.TotalBytes.Value) : (0L, 100L);

    /// <summary>
    /// Cancel this app's in-flight manifest download.
    /// </summary>
    /// <remarks>
    /// Resolved through the queue rather than this class's own dictionary, which means it now also
    /// cancels adds started by the store-page popup's own pipeline (PluginAddService, /add/{appid}).
    /// Those share the DedupeKey "manifest:{appid}" but never touched _downloads, so before the queue
    /// existed this endpoint silently did nothing for them and the download ran on after the popup closed.
    /// </remarks>
    private (int, string) HandleCancel(long appId)
    {
        var queue = _services.GetRequiredService<Services.Downloads.DownloadQueue>();
        var item = queue.FindActive($"manifest:{appId}");
        if (item is null) return (200, Json(new { success = true, message = "Nothing to cancel" }));

        queue.Cancel(item);
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleRemove(long appId)
    {
        try
        {
            _cache.RemoveLoadedAppId(appId); // also drop it from the "recently added" popup list
            var path = _installer.ReadInstalledLua(appId);
            if (path is not null)
            {
                File.Delete(path);
                var disabled = Path.Combine(Path.GetDirectoryName(path)!, $"{appId}.lua.disabled");
                if (File.Exists(disabled)) File.Delete(disabled);
                return (200, Json(new { success = true, deleted = new[] { path }, count = 1 }));
            }
            return (200, Json(new { success = true, deleted = Array.Empty<string>(), count = 0 }));
        }
        catch (Exception ex)
        {
            return (500, JsonErr(ex.Message));
        }
    }

    // ── App-owned actions (surface the LuaTools GUI window; it does the real work) ──

    /// <summary>Open the Fixes page for a game (same as the fix:// protocol).</summary>
    private (int, string) HandleOpenFix(long appId)
    {
        return OnUiThread(() =>
        {
            var window = _services.GetRequiredService<MainWindow>();
            var fixes = _services.GetRequiredService<FixesViewModel>();
            window.RestoreFromTray();
            window.NavigateToFixes();
            _ = fixes.OpenForAppIdAsync(appId);
        });
    }

    /// <summary>Surface the app's own Settings page (replaces the plugin's settings panel).</summary>
    private (int, string) HandleOpenSettings()
    {
        return OnUiThread(() =>
        {
            var window = _services.GetRequiredService<MainWindow>();
            window.RestoreFromTray();
            window.NavigateToSettings();
        });
    }

    private (int, string) HandleRestartSteam()
    {
        var ok = _steam.RestartSteam();
        return (200, Json(ok
            ? new { success = true, error = (string?)null }
            : new { success = false, error = (string?)"Failed to restart Steam" }));
    }

    private async Task<(int, string)> HandleOpenUrl(HttpListenerRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = await reader.ReadToEndAsync();

        string url = "";
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("url", out var u))
                url = u.GetString() ?? "";
        }
        catch { /* fall through to validation */ }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return (400, JsonErr("Invalid URL"));

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return (200, Json(new { success = true }));
        }
        catch (Exception ex)
        {
            return (500, JsonErr(ex.Message));
        }
    }

    private Task<(int, string)> HandleCheckUpdates()
    {
        try
        {
            // Frontend "Check for updates" → run the exact same update flow as Steam-open (app + plugin,
            // with the sync app-restart), so the button can't leave the backend out of sync with a freshly
            // updated plugin. Fire-and-forget: the flow may restart Steam and/or the app, so don't block the
            // HTTP response on it. Fall back to the plain checks if the app flow isn't wired yet.
            if (App.RunUpdateFlow is { } flow)
                _ = flow();
            else
            {
                _ = _services.GetRequiredService<UpdateService>().CheckAndStageAsync();
                _ = _services.GetRequiredService<PluginInstallerService>().AutoUpdateAsync();
            }
            return Task.FromResult((200, Json(new { success = true })));
        }
        catch (Exception ex)
        {
            return Task.FromResult((200, Json(new { success = false, error = ex.Message })));
        }
    }

    private async Task<(int, string)> HandleReadLoadedApps()
    {
        var ids = _cache.GetLoadedAppIds();
        // Resolve appid → game name so the plugin's "Added Games" popup shows names, not just numbers
        // (it renders item.name || item.appid). Names are best-effort. A missing one falls back to the id.
        var names = _services.GetRequiredService<SteamAppListCache>();
        try { await names.EnsureLoadedAsync(); } catch { /* offline / not cached yet → ids only */ }
        var apps = ids.Select(id => new { appid = id, name = names.GetName(id) }).ToList();
        return (200, Json(new { success = true, apps }));
    }

    private (int, string) HandleDismissLoadedApps()
    {
        _cache.ClearLoadedAppIds();
        return (200, Json(new { success = true }));
    }

    /// <summary>Marshal a fire-and-forget UI action onto the WPF dispatcher and ack immediately.</summary>
    private (int, string) OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return (500, JsonErr("App not ready"));
        dispatcher.InvokeAsync(() =>
        {
            try { action(); }
            catch (Exception ex) { _log.LogWarning("UI action failed: {Message}", ex.Message); }
        });
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleApiList()
    {
        LoadApiSources();
        var apis = _apiSources.Select((s, i) => new { name = s.Name, index = i }).ToList();
        return (200, Json(new { success = true, apis }));
    }

    private (int, string) HandleIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "luatools-icon.png");
            if (!File.Exists(iconPath))
            {
                var alt = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (File.Exists(alt))
                    iconPath = alt;
                else
                    return (200, Json(new { success = false, dataUrl = "" }));
            }
            var bytes = File.ReadAllBytes(iconPath);
            var b64 = Convert.ToBase64String(bytes);
            var mime = iconPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/x-icon";
            return (200, Json(new { success = true, dataUrl = $"data:{mime};base64,{b64}" }));
        }
        catch
        {
            return (200, Json(new { success = false, dataUrl = "" }));
        }
    }

    // ── Download worker ───────────────────────────────────────────────

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>Echo back only an origin that already passed <see cref="IsTrustedRequest"/>, never "*".</summary>
    private static void SetCors(HttpListenerResponse resp, string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return;
        resp.AddHeader("Access-Control-Allow-Origin", origin);
        resp.AddHeader("Vary", "Origin");
        resp.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        resp.AddHeader("Access-Control-Allow-Headers", "Content-Type");
    }

    private static string Json(object obj) => JsonSerializer.Serialize(obj);
    private static string JsonErr(string msg) => JsonSerializer.Serialize(new { success = false, error = msg });
}

internal record ApiSource(string Name, string Url, int SuccessCode);
