using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace GameGuy.Services;

/// <summary>
/// Bulk appid → name lookup. Primary source is the Morrenus app list (a single static JSON,
/// ~341k entries incl. delisted apps, no rate limit); falls back to Steam's GetAppList.
/// Cached to disk and refreshed periodically. Used for game names; covers come from elsewhere.
/// </summary>
public class SteamAppListCache
{
    private const string MorrenusUrl = "https://applist.morrenus.xyz/";
    private const string SteamUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";

    private static readonly string CacheFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirName, "steam-applist.json");
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(60));
    private readonly ConcurrentDictionary<long, string> _names = new();
    private Task? _loadTask;

    public string? GetName(long appid) => _names.TryGetValue(appid, out var n) ? n : null;

    /// <summary>Ensure the name list is loaded (from disk, or downloaded once). Safe to call repeatedly.</summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    private async Task LoadAsync()
    {
        // Fresh disk cache → use it.
        try
        {
            if (File.Exists(CacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile) < MaxAge)
            {
                var map = JsonSerializer.Deserialize<Dictionary<long, string>>(await File.ReadAllTextAsync(CacheFile));
                if (map is { Count: > 0 })
                {
                    foreach (var (k, v) in map) _names[k] = v;
                    return;
                }
            }
        }
        catch { /* fall through to download */ }

        // Download fresh: Morrenus first (reliable, includes delisted), then Steam as a fallback.
        if (await TryDownloadAsync(MorrenusUrl, flatArray: true) ||
            await TryDownloadAsync(SteamUrl, flatArray: false))
        {
            await SaveAsync();
            return;
        }

        // Both sources failed: fall back to a stale cache if we have one.
        try
        {
            if (_names.IsEmpty && File.Exists(CacheFile))
            {
                var map = JsonSerializer.Deserialize<Dictionary<long, string>>(await File.ReadAllTextAsync(CacheFile));
                if (map is not null) foreach (var (k, v) in map) _names[k] = v;
            }
        }
        catch { /* give up. Names fall back to lua-parsed / appid */ }
    }

    /// <summary>Download one source. Morrenus is a flat [{appid,name}]; Steam nests under applist.apps.</summary>
    private async Task<bool> TryDownloadAsync(string url, bool flatArray)
    {
        try
        {
            await using var stream = await _http.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            var apps = flatArray
                ? doc.RootElement
                : doc.RootElement.GetProperty("applist").GetProperty("apps");

            foreach (var app in apps.EnumerateArray())
            {
                long id = app.GetProperty("appid").GetInt64();
                string? name = app.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(name)) _names[id] = name;
            }
            return !_names.IsEmpty;
        }
        catch
        {
            return false;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            await File.WriteAllTextAsync(CacheFile, JsonSerializer.Serialize(_names));
        }
        catch { /* best effort */ }
    }
}
