using System.Collections.Concurrent;
using System.IO;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services;

/// <summary>
/// Headless add pipeline for the Steam store plugin. Mirrors the logic of
/// <c>DownloadViewModel.FetchAsync</c>/<c>DownloadFromSourceAsync</c>. Dynamic sources, synthesized
/// Hubcap/Sadie source, key-gating, usage badges, FastFetch auto-download, but uses only the underlying
/// SERVICES (never the UI view model), so the app window stays silent. The plugin polls state via the
/// HTTP server; when FastFetch is off it picks a source with <see cref="Pick"/>.
/// </summary>
public class PluginAddService(
    LuaToolsApiClient api,
    HubcapService hubcap,
    SettingsService settings,
    AuthService auth,
    DownloadQueue queue,
    ManifestJobFactory jobs)
{
    private const string HubcapSourceName = "Sadie (Morrenus)";

    public class SourceRow
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
        public bool NeedsKey { get; set; }
        public bool Locked { get; set; }
        public string? Stats { get; set; }
        public bool Downloading { get; set; }
        public double Progress { get; set; }
        public bool Indeterminate { get; set; }
        public bool Available => Status == "available";
        public bool CanDownload => Available && !Locked;
    }

    public class AddState
    {
        public long AppId;
        public string? GameName;
        public bool Checking;
        public bool FastFetch;
        public bool SourcesLoaded;
        public List<SourceRow> Sources = [];
        public string? InstallStatus;
        public bool InstallFailed;
        public string? Error;
        // A download+install is running. An int behind Interlocked rather than a plain bool: Pick runs
        // DownloadAsync on the thread pool, so two quick picks could both pass a read-then-write check.
        private int _busy;
        public bool Busy => Volatile.Read(ref _busy) == 1;
        public bool TryEnterBusy() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
        public void ExitBusy() => Volatile.Write(ref _busy, 0);
    }

    private readonly ConcurrentDictionary<long, AddState> _states = new();

    public AddState? GetState(long appId) => _states.TryGetValue(appId, out var s) ? s : null;

    /// <summary>Begin a headless add: check sources (+ Hubcap synth + gating + usage) and, if FastFetch
    /// is on, auto-download the best source. FastFetch off leaves the sources for the plugin to pick.</summary>
    public void Start(long appId, string? gameName = null)
    {
        var state = new AddState
        {
            AppId = appId,
            Checking = true,
            FastFetch = settings.FastFetch,
            // The store page usually passes the name it already shows, letting CheckAsync skip a
            // lua.tools /details round-trip. Null/blank → CheckAsync fetches it as a fallback.
            GameName = string.IsNullOrWhiteSpace(gameName) ? null : gameName.Trim(),
        };
        _states[appId] = state;
        PluginLog.Log($"PluginAdd.Start appid={appId} fastFetch={state.FastFetch} guest={auth.IsGuest}");
        _ = Task.Run(() => CheckAsync(appId, state));
    }

    /// <summary>Plugin picked a source (FastFetch-off path) → download+install it.</summary>
    public void Pick(long appId, string sourceName)
    {
        if (!_states.TryGetValue(appId, out var state))
        {
            PluginLog.Log($"PluginAdd.Pick appid={appId} source='{sourceName}' -> NO STATE (Start not called?)");
            return;
        }
        if (state.Busy)
        {
            PluginLog.Log($"PluginAdd.Pick appid={appId} source='{sourceName}' -> BUSY, ignored");
            return;
        }
        var row = state.Sources.FirstOrDefault(r =>
            string.Equals(r.Name, sourceName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.DisplayName, sourceName, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            PluginLog.Log($"PluginAdd.Pick appid={appId} source='{sourceName}' -> ROW NOT FOUND. have=[{string.Join(", ", state.Sources.Select(s => s.Name))}]");
            return;
        }
        PluginLog.Log($"PluginAdd.Pick appid={appId} source='{row.Name}' canDownload={row.CanDownload} needsKey={row.NeedsKey} -> downloading");
        _ = Task.Run(() => DownloadAsync(appId, state, row));
    }

    private async Task CheckAsync(long appId, AddState state)
    {
        try
        {
            // The game name is only needed later for the "Added {name}" status. If the store page already
            // supplied it (via Start), skip the lua.tools /details call entirely; otherwise fetch it in
            // parallel so it never gates the picker behind its own serial round-trip.
            var nameTask = string.IsNullOrEmpty(state.GameName) ? SafeGetGameNameAsync(appId) : null;
            string? key = settings.HubcapApiKey;

            // Hubcap/Sadie is the priority source. When a key is set, check ITS availability first, and in
            // FastFetch, if Hubcap can serve this game, download straight from it and skip the lua.tools
            // CheckSources call entirely (a lua.tools source would never be picked over Hubcap anyway).
            bool hubcapAvailable = false;
            if (!string.IsNullOrEmpty(key))
            {
                var hs = await hubcap.CheckStatusAsync(key, appId.ToString());
                hubcapAvailable = hs?.ManifestFileExists == true;
            }

            if (state.FastFetch && hubcapAvailable)
            {
                var stats = await hubcap.GetStatsAsync(key!);
                if (stats?.CanMakeRequests == true)
                {
                    var hubMeta = SourceMeta.Get(HubcapSourceName);
                    var hubRow = new SourceRow
                    {
                        Name = HubcapSourceName,
                        DisplayName = hubMeta.DisplayName ?? HubcapSourceName,
                        Status = "available",
                        NeedsKey = true,
                        Stats = $"{stats.DailyUsage}/{stats.DailyLimit}",
                    };
                    if (nameTask is not null) state.GameName = await nameTask;
                    PublishSources(state, new List<SourceRow> { hubRow }, appId);
                    await DownloadAsync(appId, state, hubRow);
                    return;
                }
                // Hubcap is out of daily quota → fall through and let a lua.tools source take over.
            }

            var statuses = await api.CheckSourcesAsync(appId.ToString());

            // Synthesize the Hubcap/Sadie row from the availability already checked above (or "unknown"
            // when no key is set, so it shows locked with the "needs a key" hint).
            statuses[HubcapSourceName] = string.IsNullOrEmpty(key)
                ? "unknown"
                : (hubcapAvailable ? "available" : "unavailable");

            // Premium (key-gated) sources first, mirroring the website/app ordering.
            var rows = statuses
                .OrderByDescending(kv => SourceMeta.Get(kv.Key).RequiresUserKey ? 1 : 0)
                .Select(kv =>
                {
                    var meta = SourceMeta.Get(kv.Key);
                    return new SourceRow
                    {
                        Name = kv.Key,
                        DisplayName = meta.DisplayName ?? kv.Key,
                        Status = kv.Value,
                        NeedsKey = meta.RequiresUserKey,
                    };
                }).ToList();

            // No-key premium rows lock immediately (they show the "needs a key" hint). Keyed rows get their
            // real lock state from the Hubcap stats call in FillBadgesAsync.
            var keyRows = rows.Where(r => r.NeedsKey).ToList();
            if (keyRows.Count > 0 && string.IsNullOrEmpty(key))
                foreach (var r in keyRows) r.Locked = true;

            if (nameTask is not null) state.GameName = await nameTask; // ready before any Pick builds its status

            if (state.FastFetch)
            {
                // Reached only when Hubcap couldn't serve it (no key / not on Hubcap / out of quota). No
                // picker, so resolve just the Hubcap lock that CanDownload needs (skip the cosmetic X/25
                // badges), then pick the best remaining source.
                if (keyRows.Count > 0 && !string.IsNullOrEmpty(key))
                    await FillHubcapBadgeAsync(keyRows, key);
                PublishSources(state, rows, appId);

                var best = rows.FirstOrDefault(r => r.CanDownload);
                if (best is null) { state.Error = Resources.Strings.Err_NoSourceAvailable; PluginLog.Log($"PluginAdd.Check appid={appId} FastFetch: no downloadable source"); return; }
                await DownloadAsync(appId, state, best);
                return;
            }

            // FastFetch off: publish the picker as soon as availability is known, then lazy-fill the usage
            // badges. The plugin polls state (~350ms), so the "12/25" / "X/Unlimited" counters and the real
            // Hubcap lock pop in on the next tick instead of blocking the whole list.
            PublishSources(state, rows, appId);
            await FillBadgesAsync(rows, keyRows, key);
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Checking = false;
            PluginLog.Log($"PluginAdd.Check appid={appId} EXCEPTION: {ex}");
        }
    }

    private void PublishSources(AddState state, List<SourceRow> rows, long appId)
    {
        state.Sources = rows;
        state.SourcesLoaded = true;
        state.Checking = false;
        PluginLog.Log($"PluginAdd.Check appid={appId} sources=[{string.Join(", ", rows.Select(r => $"{r.Name}({r.Status},lock={r.Locked})"))}] fastFetch={state.FastFetch}");
    }

    private async Task<string?> SafeGetGameNameAsync(long appId)
    {
        try { return (await api.GetDetailsAsync(appId.ToString()))?.Name; } catch { return null; }
    }

    /// <summary>Fill the usage-counter badges on the (already-published) rows, best-effort and
    /// concurrently: the Hubcap "12/25" badge + real lock state, and the standard "X/25" badge. A failure
    /// leaves the affected rows without a badge. The picker stays fully usable. Row fields are mutated in
    /// place; the status poll re-serializes them, so the badges appear on the next tick.</summary>
    private async Task FillBadgesAsync(List<SourceRow> rows, List<SourceRow> keyRows, string? key)
    {
        var hubcapTask = keyRows.Count > 0 && !string.IsNullOrEmpty(key)
            ? FillHubcapBadgeAsync(keyRows, key!)
            : Task.CompletedTask;
        var standardTask = !auth.IsGuest ? FillStandardBadgeAsync(rows) : Task.CompletedTask;
        await Task.WhenAll(hubcapTask, standardTask);
    }

    /// <summary>Hubcap "12/25" usage badge + lock state on the key-gated rows.</summary>
    private async Task FillHubcapBadgeAsync(List<SourceRow> keyRows, string key)
    {
        try
        {
            var stats = await hubcap.GetStatsAsync(key);
            foreach (var r in keyRows)
            {
                r.Locked = stats?.CanMakeRequests != true;
                if (stats is not null) r.Stats = $"{stats.DailyUsage}/{stats.DailyLimit}";
            }
        }
        catch { }
    }

    /// <summary>Standard "X/25" (or "X/Unlimited" for supporters) usage badge on the non-key rows.</summary>
    private async Task FillStandardBadgeAsync(List<SourceRow> rows)
    {
        try
        {
            var usageTask = api.GetStandardUsageAsync();
            var supporterTask = api.GetSupporterStatusAsync();
            await Task.WhenAll(usageTask, supporterTask);
            var usage = usageTask.Result;
            bool isSupporter = supporterTask.Result?.IsSupporter == true;
            foreach (var r in rows.Where(r => !r.NeedsKey))
                if (usage is not null)
                    r.Stats = isSupporter ? $"{usage.Used} / Unlimited" : $"{usage.Used}/{usage.Limit}";
        }
        catch { }
    }

    /// <summary>
    /// Queue the pick through the shared <see cref="DownloadQueue"/> and mirror its progress into this
    /// service's plain-POCO state, which the store-page popup polls over HTTP.
    /// </summary>
    /// <remarks>
    /// The download and install themselves live in <c>ManifestJobFactory</c>, the same code path the Add
    /// page uses. Mirroring (rather than data binding) is correct here: <see cref="SourceRow"/> is
    /// serialized to JSON on each poll, so it only needs to hold the latest values.
    ///
    /// Routing through the queue also makes the popup's Cancel button work for the first time: it posts
    /// to /cancel/{appid}, which now resolves the same queue item this method enqueued.
    /// </remarks>
    private async Task DownloadAsync(long appId, AddState state, SourceRow row)
    {
        if (!state.TryEnterBusy()) return;
        state.Error = null;
        state.InstallStatus = null;
        state.InstallFailed = false;
        row.Downloading = true;
        row.Indeterminate = true;
        row.Progress = 0;

        try
        {
            var job = jobs.CreateManifestJob(appId, state.GameName, row.Name, row.NeedsKey);
            var item = queue.Enqueue(job);

            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                row.Indeterminate = item.IsIndeterminate;
                row.Progress = item.Percent;
                row.Downloading = item.IsActive;
            }
            item.PropertyChanged += OnChanged;

            try
            {
                var result = await item.Completion;

                if (result is null || !result.Ok)
                {
                    state.Error = result?.Message ?? item.Message;
                    state.InstallFailed = true;
                    PluginLog.Log($"PluginAdd.Download appid={appId} source='{row.Name}' FAILED: {state.Error}");
                }
                else
                {
                    // "· via {source}" mirrors the GUI's FastFetch suffix, naming the source this add used.
                    state.InstallStatus = result.Message
                        + " " + string.Format(Resources.Strings.Add_FastFetch_Via, row.Name);
                    PluginLog.Log($"PluginAdd.Download appid={appId} source='{row.Name}' OK: {state.InstallStatus}");
                }
            }
            finally { item.PropertyChanged -= OnChanged; }
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.InstallFailed = true;
            PluginLog.Log($"PluginAdd.Download appid={appId} source='{row.Name}' EXCEPTION: {ex}");
        }
        finally
        {
            row.Downloading = false;
            row.Indeterminate = false;
            state.ExitBusy();
        }
    }
}
