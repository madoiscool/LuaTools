using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameGuy.Services;

public class CefInjectorService : IHostedService
{
    private readonly SteamService _steam;
    private readonly ILogger<CefInjectorService> _log;
    private CancellationTokenSource? _cts;
    private string _gameguyJs = "";
    private string _polyfillJs = "";
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(30));

    // Persistent CDP WebSocket per tab (keyed by tab id), reused across calls so the fast RPC-drain
    // loop doesn't pay a connect handshake every tick. Opened lazily, evicted (and reopened next call)
    // on any socket error. Touched only from the single InjectionLoop task (+ StopAsync on shutdown),
    // so no locking is needed. _cdpId hands out unique CDP command ids for response correlation.
    private readonly Dictionary<string, ClientWebSocket> _sockets = new();
    private int _cdpId;

    // CDP is opened by the `.cef-enable-remote-debugging` junction PluginInstallerService manages, not by
    // the loader DLL anymore, and Steam's own internal logic hardcodes this port. Confirmed this can't be
    // changed (file content has no effect on it), so unlike the old hook-based design (which deliberately
    // picked the uncommon 42067 to dodge collisions), this is stuck with 8080. A commonly-occupied port
    // (dev servers, Docker, etc.). PluginInstallerService.IsPort8080BusyAsync() surfaces a best-effort
    // warning for that case (probing /json to rule out Steam's own CDP server before warning. A bare bind
    // test can't tell "someone else has it" from "Steam has it and it's working correctly"); there is no
    // fallback port to switch to.
    private const string CefDebugUrl = "http://127.0.0.1:8080/json";
    private const string BaseUrl = "http://127.0.0.1:6767";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public CefInjectorService(SteamService steam, ILogger<CefInjectorService> logger)
    {
        _steam = steam;
        _log = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await ReloadPluginFilesAsync();

        _ = Task.Run(() => InjectionLoop(_cts.Token), _cts.Token);
        _log.LogInformation("CEF injector started");
    }

    /// <summary>(Re)reads luatools.js + the polyfill from disk into memory. Called once at startup, and
    /// again by PluginInstallerService right after install/uninstall. The file-finding paths get checked
    /// exactly once otherwise, so a plugin installed/updated/removed after launch would silently never take
    /// effect until the app was restarted (the app is commonly launched by the loader BEFORE anything is
    /// installed, so this isn't just a theoretical edge case). _gameguyJs/_polyfillJs are plain strings.
    /// Reassignment is atomic, so this is safe to call while InjectionLoop is concurrently reading them; the
    /// next poll cycle (~1s) just picks up the new content, no page reload needed.</summary>
    public async Task ReloadPluginFilesAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        var jsPath = FindGameGuyJs();
        if (jsPath is not null && File.Exists(jsPath))
        {
            _gameguyJs = await File.ReadAllTextAsync(jsPath, ct);
            _log.LogInformation("Loaded luatools.js ({Length} bytes)", _gameguyJs.Length);
        }
        else
        {
            _gameguyJs = "";
            _log.LogWarning("luatools.js not found");
        }

        var polyfillPath = FindPolyfillJs();
        if (polyfillPath is not null && File.Exists(polyfillPath))
        {
            _polyfillJs = await File.ReadAllTextAsync(polyfillPath, ct);
        }
        else
        {
            _polyfillJs = BuildInlinePolyfill();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        foreach (var ws in _sockets.Values)
            try { ws.Dispose(); } catch { /* best effort on shutdown */ }
        _sockets.Clear();
        return Task.CompletedTask;
    }

    // Loop cadence: tick fast (drain queued RPCs every tick so the page's calls resolve in ~150ms rather
    // than the old ~1s), but only refresh the tab list + re-inject on every Nth tick (~1s). Injection
    // doesn't need to be fast, and the liveness/re-inject logic is unchanged, just decoupled from the drain.
    private const int TickMs = 150;
    private const int InjectEveryTicks = 7; // 7 * 150ms ≈ 1s

    private async Task InjectionLoop(CancellationToken ct)
    {
        // The store tabs to drain each fast tick (id + ws url), refreshed on the ~1s injection cadence.
        List<(string Id, string Ws)> storeTabs = new();
        int tick = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // ── Slow cadence (~1s): discover store tabs, ensure luatools.js is injected ──
                if (tick % InjectEveryTicks == 0)
                {
                    // Millennium may also be present and running its own plugin loader on this same page.
                    // That's fine now. The polyfill (BuildInlinePolyfill) only takes over "gameguy" calls and
                    // passes everything else through to Millennium's real window.Millennium if one exists, so
                    // the two no longer need to be mutually exclusive.
                    var tabsJson = await _http.GetStringAsync(CefDebugUrl, ct);
                    if (!string.IsNullOrWhiteSpace(tabsJson))
                    {
                        var tabs = JsonSerializer.Deserialize<List<CefTabInfo>>(tabsJson, JsonOpts) ?? new();

                        // Inject luatools.js into every store-page tab whose JS context isn't already alive.
                        // Steam reuses the same CDP tab ID across SPA-style navigation (Home <-> store pages),
                        // but navigating to a different page wipes the JS context entirely, so "already
                        // injected by tab ID" is the wrong signal. Check liveness in the CURRENT context every
                        // cycle instead (window.__LuaToolsReady, set as the last statement of luatools.js's
                        // main IIFE) and re-inject whenever it's gone.
                        var script = _polyfillJs + "\n" + _gameguyJs;
                        var live = new List<(string, string)>();
                        var seen = new HashSet<string>();
                        foreach (var tab in tabs)
                        {
                            // Inject into ALL store pages, not just /app/ game pages: the store-wide header
                            // icon (and the "games added since last restart" popup, which only fires on the
                            // store home) need luatools.js present on the home page too. Steam boots straight
                            // to the home page, which has no "/app/" in its URL, so the old "/app/"-only filter
                            // meant the icon/popup never appeared there. luatools.js itself gates the per-game
                            // "Add via LuaTools" (the plugin's own label) button to /app/ pages internally, so injecting store-wide is safe.
                            if (tab.Url?.Contains("store.steampowered.com", StringComparison.OrdinalIgnoreCase) == true
                                && !string.IsNullOrEmpty(tab.WebSocketDebuggerUrl)
                                && !string.IsNullOrEmpty(tab.Id))
                            {
                                seen.Add(tab.Id!);
                                // EvaluateReturnAsync's value.GetString() throws (silently, falling through to
                                // the raw response text) for a JSON boolean, so stringify explicitly rather
                                // than returning a bare boolean expression.
                                var alive = await EvaluateReturnAsync(tab.Id!, tab.WebSocketDebuggerUrl!, "String(window.__LuaToolsReady === true)", ct);
                                if (alive != "true")
                                    await EvaluateAsync(tab.Id!, tab.WebSocketDebuggerUrl!, script, ct);

                                live.Add((tab.Id!, tab.WebSocketDebuggerUrl!));
                            }
                        }

                        storeTabs = live;
                        // Drop persistent sockets for tabs that have gone away.
                        foreach (var stale in _sockets.Keys.Where(k => !seen.Contains(k)).ToList())
                            EvictSocket(stale);
                    }
                }

                // ── Fast cadence (every tick): drain pending CDP bridge requests ──
                foreach (var (id, ws) in storeTabs)
                    await ProcessSingleTab(id, ws, ct);

                tick++;
                await Task.Delay(TickMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogDebug("CEF cycle: {Message}", ex.Message);
                try { await Task.Delay(1000, ct); } catch { break; }
            }
        }
    }

    private async Task ProcessSingleTab(string tabId, string wsUrl, CancellationToken ct)
    {
        try
        {
            var pendingJson = await EvaluateReturnAsync(tabId, wsUrl,
                "JSON.stringify(Object.values(window.Millennium._pending||{}).map(function(r){return{m:r.method,a:JSON.stringify(r.args)}}))", ct);

            if (string.IsNullOrWhiteSpace(pendingJson) || pendingJson == "[]" || pendingJson == "null")
                return;

            var pending = JsonSerializer.Deserialize<List<PendingRequest>>(pendingJson, JsonOpts);
            if (pending is null or { Count: 0 }) return;

            var responses = new List<object>();
            foreach (var req in pending)
            {
                try
                {
                    var result = await CallBackendMethod(req.m ?? "", req.a ?? "{}");
                    responses.Add(new { v = JsonSerializer.Deserialize<object>(result) ?? result });
                }
                catch (Exception ex)
                {
                    responses.Add(new { e = ex.Message });
                }
            }

            var respArray = JsonSerializer.Serialize(responses);
            await EvaluateReturnAsync(tabId, wsUrl,
                "(function(r){var ks=Object.keys(window.Millennium._pending);for(var i=0;i<ks.length&&i<r.length;i++){var id=ks[i];window.Millennium._readyResponses[id]=r[i];delete window.Millennium._pending[id];}})(" + respArray + ");", ct);
        }
        catch { }
    }

    private async Task<string> CallBackendMethod(string method, string argsRaw)
    {
        var methodMap = new Dictionary<string, (string HttpMethod, string Path)>
        {
            ["HasGameGuyForApp"] = ("GET", "/has/{appid}"),
            ["CheckApisForApp"] = ("POST", "/check-sources/{appid}"),
            ["StartAddViaGameGuyFromUrl"] = ("POST", "/download/{appid}"),
            ["GetAddViaGameGuyStatus"] = ("GET", "/download-status/{appid}"),
            ["CancelAddViaGameGuy"] = ("POST", "/cancel/{appid}"),
            ["DeleteGameGuyForApp"] = ("POST", "/remove/{appid}"),
            // Store-page popup's self-contained add pipeline (PluginAddService-backed).
            // Raw fetch() to these from the page context is blocked as mixed content
            // (HTTPS store page -> HTTP localhost); route through this CDP bridge instead.
            ["StartGameGuyAdd"] = ("POST", "/add/{appid}"),
            ["GetGameGuyAddStatus"] = ("GET", "/add-status/{appid}"),
            ["PickGameGuyAddSource"] = ("POST", "/add-source/{appid}"),
            // Menu actions (Settings, Fixes, Restart Steam). Same mixed-content problem, same fix.
            ["OpenSettings"] = ("POST", "/open/settings"),
            ["OpenFix"] = ("POST", "/open/fix/{appid}"),
            ["RestartSteam"] = ("POST", "/restart-steam"),
            // Open an external URL (Discord link, etc.), and check-for-updates. Also route
            // through the bridge (previously used a dead direct-fetch to a proxy port).
            ["OpenExternalUrl"] = ("POST", "/open-url"),
            ["CheckForUpdatesNow"] = ("POST", "/check-updates"),
            // "Games added since last Steam restart" popup: read the list, then dismiss it.
            ["ReadLoadedApps"] = ("GET", "/loaded-apps"),
            ["DismissLoadedApps"] = ("POST", "/loaded-apps"),
            ["GetApiList"] = ("GET", "/api-list"),
            ["GetIconDataUrl"] = ("GET", "/icon"),
            ["GetGamesDatabase"] = ("GET", "/games-database"),
            ["Logger"] = ("POST", "/log"),
        };

        if (!methodMap.TryGetValue(method, out var mapping))
            return """{"success":true}""";

        var path = mapping.Path;
        if (argsRaw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsRaw);
                var root = doc.RootElement;
                foreach (var prop in root.EnumerateObject())
                {
                    path = path.Replace($"{{{prop.Name}}}", Uri.EscapeDataString(prop.Value.ToString()));
                }
            }
            catch { }
        }

        var url = BaseUrl + path;
        if (mapping.HttpMethod == "GET")
        {
            return await _http.GetStringAsync(url);
        }
        else
        {
            var content = new StringContent(argsRaw ?? "{}", Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content);
            return await resp.Content.ReadAsStringAsync();
        }
    }

    /// <summary>Return the reused (or freshly opened) CDP socket for a tab. Throws if the connect fails.
    /// The caller evicts on any exception.</summary>
    private async Task<ClientWebSocket> GetSocketAsync(string tabId, string wsUrl, CancellationToken ct)
    {
        if (_sockets.TryGetValue(tabId, out var existing))
        {
            if (existing.State == WebSocketState.Open) return existing;
            EvictSocket(tabId); // stale (closed/aborted). Drop and reopen below
        }

        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), ct);
        _sockets[tabId] = ws;
        return ws;
    }

    private void EvictSocket(string tabId)
    {
        if (_sockets.Remove(tabId, out var ws))
            try { ws.Dispose(); } catch { /* best effort */ }
    }

    /// <summary>Send a Runtime.evaluate over the tab's persistent socket and return the evaluated string
    /// value (or the raw response text if there's no plain value). Uses a unique CDP id per call and reads
    /// frames until that id comes back, skipping any interleaved event frames. On any socket error the
    /// connection is evicted so the next call transparently reopens a fresh one (same net behavior as the
    /// old open-per-call code, minus the handshake cost on the happy path).</summary>
    private async Task<string> EvaluateReturnAsync(string tabId, string wsUrl, string expression, CancellationToken ct)
    {
        try
        {
            var ws = await GetSocketAsync(tabId, wsUrl, ct);
            int id = ++_cdpId;
            var cmd = new { id, method = "Runtime.evaluate", @params = new { expression, returnByValue = true } };
            var json = JsonSerializer.Serialize(cmd);
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, ct);

            var buffer = new byte[65536];
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // Each CDP message is one WS message (possibly multi-frame). Read whole messages until we get
            // the one carrying our id. We never enable any CDP domain, so in practice the only traffic is
            // our own responses. The id check is just defensive against any stray event frames.
            while (!linked.Token.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
                    if (result.MessageType == WebSocketMessageType.Close) { EvictSocket(tabId); return ""; }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var responseText = sb.ToString();
                JsonElement root;
                try { using var doc = JsonDocument.Parse(responseText); root = doc.RootElement.Clone(); }
                catch { continue; } // unparseable frame. Keep reading

                if (!(root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var rid) && rid == id))
                    continue; // an event or a different id, not our response yet

                if (root.TryGetProperty("result", out var cdpResult) &&
                    cdpResult.TryGetProperty("result", out var evalResult) &&
                    evalResult.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    // Only a string value maps cleanly; GetString() throws on e.g. a JSON boolean, so
                    // callers that want a bool stringify it in JS (String(...)). Anything non-string
                    // falls through to the raw response text (same as the old code) rather than throwing.
                    return value.GetString() ?? responseText;
                }
                return responseText; // our response, but no plain string value (e.g. an injection eval)
            }
            return "";
        }
        catch (Exception ex)
        {
            _log.LogDebug("EvaluateReturnAsync: CDP call failed: {Message}", ex.Message);
            EvictSocket(tabId);
            return "";
        }
    }

    /// <summary>Fire-and-forget evaluate (used for injecting the polyfill + luatools.js). Shares the same
    /// persistent socket + id-correlated read path; the return value is ignored.</summary>
    private async Task EvaluateAsync(string tabId, string wsUrl, string script, CancellationToken ct)
    {
        await EvaluateReturnAsync(tabId, wsUrl, script, ct);
    }

    private string? FindGameGuyJs()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // The frontend is no longer bundled with the app. PluginInstallerService downloads it from
        // GitHub releases into %AppData%\GameGuy\plugin. If it isn't installed yet, nothing injects.
        string[] candidates =
        {
            Path.Combine(appData, AppConfig.DataDirName, "plugin", "public", "luatools.js"),
            Path.Combine(appData, AppConfig.DataDirName, "plugin", "luatools.js"),
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        return null;
    }

    private string? FindPolyfillJs()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "public", "millennium-polyfill.js"),
            Path.Combine(AppContext.BaseDirectory, "millennium-polyfill.js"),
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        return null;
    }

    /// <summary>Real Millennium (if present on this page) already defines window.Millennium.callServerMethod
    /// as one shared object used by every Millennium plugin, routed server-side by the pluginName argument.
    /// It is NOT per-plugin namespaced. Blindly overwriting it (the old behavior) broke every other
    /// Millennium plugin on the page whenever both injectors were active, not just GameGuy. This only takes
    /// over "gameguy" calls and passes every other pluginName through to the real callServerMethod
    /// unchanged, so this app's CDP injection can coexist with Millennium instead of requiring it be absent.
    /// _pending/_readyResponses are attached directly onto whichever object ends up as window.Millennium
    /// (fake or real) under their original names. ProcessSingleTab's polling reads/writes those regardless
    /// of which case this is.</summary>
    private string BuildInlinePolyfill()
    {
        return @"
(function(){
var real=window.Millennium;
var pending={},ready={},reqId=0;
function ltCall(p,m,a){
  var i='_ltr_'+(++reqId);
  pending[i]={method:m,args:a,ts:Date.now()};
  return new Promise(function(rv,rj){
    var mx=100;
    function ck(){
      var r=ready[i];
      if(r!==undefined){delete ready[i];if(r.e){rj(new Error(r.e))}else{rv(r.v)}return}
      if(--mx>0){setTimeout(ck,50)}else{delete pending[i];rv({success:true})}
    }
    ck();
  });
}
if(real&&typeof real.callServerMethod==='function'){
  var realCall=real.callServerMethod.bind(real);
  real.callServerMethod=function(p,m,a){return p==='gameguy'?ltCall(p,m,a):realCall(p,m,a)};
  real._pending=pending;
  real._readyResponses=ready;
  window.Millennium=real;
}else{
  window.Millennium={_pending:pending,_readyResponses:ready,callServerMethod:function(p,m,a){return ltCall(p,m,a)}};
}
})();
";
    }
}

internal class CefTabInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string? Title { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string? Url { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("webSocketDebuggerUrl")]
    public string? WebSocketDebuggerUrl { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal class PendingRequest
{
    public string? m { get; set; }
    public string? a { get; set; }
}
