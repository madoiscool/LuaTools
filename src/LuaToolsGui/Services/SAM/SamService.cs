using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using LuaToolsGui.Models;
using LuaToolsGui.Services.SAM.Native;

namespace LuaToolsGui.Services.SAM;

public class SamService
{
    private readonly SteamService _steamService;
    private readonly SteamAppInfoCache _appInfoCache;
    private readonly SteamAppListCache _appListCache;
    private readonly CoverCache _coverCache;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ConcurrentDictionary<uint, SamGameStatsData> _statsCache = new();
    private List<SamGameInfo>? _cachedGames;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SamService(
        SteamService steamService,
        SteamAppInfoCache appInfoCache,
        SteamAppListCache appListCache,
        CoverCache coverCache)
    {
        _steamService = steamService;
        _appInfoCache = appInfoCache;
        _appListCache = appListCache;
        _coverCache = coverCache;

        // Set custom Steam path if configured
        if (!string.IsNullOrEmpty(_steamService.EffectivePath))
        {
            Steam.SetCustomInstallPath(_steamService.EffectivePath);
        }
    }

    public async Task<IReadOnlyList<SamGameInfo>> GetGamesAsync(bool forceRefresh = false)
    {
        if (_cachedGames != null && !forceRefresh)
        {
            return _cachedGames;
        }

        var gamesMap = new Dictionary<uint, SamGameInfo>();

        // 1. Ensure LuaTools app list cache is loaded for instant friendly names
        try
        {
            await _appListCache.EnsureLoadedAsync();
        }
        catch { /* best effort */ }

        // 2. Add LuaTools installed games (.lua files in stplug-in)
        try
        {
            string? plugInDir = _steamService.StPlugInDir;
            if (!string.IsNullOrEmpty(plugInDir) && Directory.Exists(plugInDir))
            {
                foreach (var f in LuaInstaller.EnumerateInstalled(plugInDir))
                {
                    uint id = (uint)f.AppId;
                    string? name = _appListCache.GetName(id) ?? _appInfoCache.GetCached(id)?.Name;
                    gamesMap[id] = new SamGameInfo
                    {
                        Id = id,
                        Name = name ?? $"App {id}",
                        Type = "normal",
                        IsInstalledInLuaTools = true,
                    };
                }
            }
        }
        catch { /* best effort */ }

        // 3. Discover from Steam installation folders (appcache & steamapps)
        string? installPath = _steamService.EffectivePath ?? Steam.GetInstallPath();
        if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
        {
            // Appcache stats schemas
            string statsPath = Path.Combine(installPath, "appcache", "stats");
            if (Directory.Exists(statsPath))
            {
                foreach (var file in Directory.GetFiles(statsPath, "UserGameStatsSchema_*.bin"))
                {
                    string fn = Path.GetFileNameWithoutExtension(file);
                    if (fn.StartsWith("UserGameStatsSchema_") &&
                        uint.TryParse(fn["UserGameStatsSchema_".Length..], out uint fileAppId))
                    {
                        if (!gamesMap.ContainsKey(fileAppId))
                        {
                            gamesMap[fileAppId] = new SamGameInfo
                            {
                                Id = fileAppId,
                                Name = _appListCache.GetName(fileAppId) ?? $"App {fileAppId}",
                                Type = "normal",
                            };
                        }
                    }
                }
            }

            // Steamapps manifests
            string steamAppsDir = Path.Combine(installPath, "steamapps");
            if (Directory.Exists(steamAppsDir))
            {
                foreach (var file in Directory.GetFiles(steamAppsDir, "appmanifest_*.acf"))
                {
                    string fn = Path.GetFileNameWithoutExtension(file);
                    if (fn.StartsWith("appmanifest_") &&
                        uint.TryParse(fn["appmanifest_".Length..], out uint manifestAppId))
                    {
                        if (!gamesMap.ContainsKey(manifestAppId))
                        {
                            gamesMap[manifestAppId] = new SamGameInfo
                            {
                                Id = manifestAppId,
                                Name = _appListCache.GetName(manifestAppId) ?? $"App {manifestAppId}",
                                Type = "normal",
                            };
                        }
                    }
                }
            }
        }

        // 4. Always include Spacewar (480) for testing
        if (!gamesMap.ContainsKey(480))
        {
            gamesMap[480] = new SamGameInfo
            {
                Id = 480,
                Name = "Spacewar",
                Type = "normal",
            };
        }

        // 5. Try running worker to discover additional games via Steam Client API
        try
        {
            var workerGames = await RunWorkerGetGamesAsync();
            if (workerGames != null)
            {
                foreach (var g in workerGames)
                {
                    if (gamesMap.TryGetValue(g.Id, out var existing))
                    {
                        if (!string.IsNullOrWhiteSpace(g.Name) && !g.Name.StartsWith("App "))
                        {
                            existing.Name = g.Name;
                        }
                        if (!string.IsNullOrWhiteSpace(g.ImageUrl))
                        {
                            existing.ImageUrl = g.ImageUrl;
                        }
                    }
                    else
                    {
                        gamesMap[g.Id] = g;
                    }
                }
            }
        }
        catch { /* Fallback to already discovered games */ }

        // 6. Populate friendly names and covers from LuaTools app list & app info caches
        foreach (var game in gamesMap.Values)
        {
            if (string.IsNullOrWhiteSpace(game.Name) || game.Name.StartsWith("App "))
            {
                string? cachedName = _appListCache.GetName(game.Id) ?? _appInfoCache.GetCached(game.Id)?.Name;
                if (!string.IsNullOrWhiteSpace(cachedName))
                {
                    game.Name = cachedName;
                }
            }

            // Cover URL
            game.DisplayCoverUrl = _coverCache.GetCoverPathOrUrl(game.Id);
        }

        var resultList = gamesMap.Values
            .OrderByDescending(g => g.IsInstalledInLuaTools)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _cachedGames = resultList;
        return resultList;
    }

    public async Task<SamGameStatsData> GetGameStatsAsync(uint appId, bool forceRefresh = false)
    {
        if (!forceRefresh && _statsCache.TryGetValue(appId, out var cached))
        {
            return cached;
        }

        // Try fetching stats with a retry on timeout
        const int maxAttempts = 3; // more attempts for stats retrieval
        SamGameStatsData? data = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            data = await RunWorkerGetStatsAsync(appId);
            // success if data contains achievements
            if (data != null && string.IsNullOrEmpty(data.ErrorMessage) && data.Achievements?.Count > 0)
            {
                break; // got valid achievements
            }
            // otherwise continue retry
            // If timeout, wait a bit before retrying
            if (data != null && data.ErrorMessage?.Contains("timed out") == true && attempt < maxAttempts)
            {
                await Task.Delay(2000);
            }
        }

        if (data != null && string.IsNullOrWhiteSpace(data.ErrorMessage))
        {
            // Populate fallback game name if missing
            if (string.IsNullOrWhiteSpace(data.GameName) || data.GameName.StartsWith("App "))
            {
                string? name = _appListCache.GetName(appId);
                if (!string.IsNullOrWhiteSpace(name)) data.GameName = name;
            }

            _statsCache[appId] = data;
            return data;
        }

        return data ?? new SamGameStatsData
        {
            AppId = appId,
            ErrorMessage = "Failed to communicate with Steam worker",
        };
    }

    public async Task<SamStoreResult> StoreStatsAsync(SamStoreRequest request)
    {
        var result = await RunWorkerStoreStatsAsync(request);
        if (result.Success)
        {
            // Invalidate cached stats so next load re-fetches updated values
            _statsCache.TryRemove(request.AppId, out _);
        }
        return result;
    }

    private static string? ExtractJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        // The native steam client / pipes library may print debug messages like:
        // "src\common\pipes.cpp (537) : !m_bOutstandingCallback" to stdout.
        // We find the JSON substring by checking trimmed lines starting with '{' or '['.
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                return trimmed;
            }
        }

        // Fallback: search for first { or [ to matching end
        int jsonStart = output.IndexOfAny(['{', '[']);
        if (jsonStart >= 0)
        {
            return output[jsonStart..].Trim();
        }

        return null;
    }

    // ── Worker Execution Helper ──────────────────────────────────────

    private static async Task<List<SamGameInfo>?> RunWorkerGetGamesAsync()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;

        var psi = new ProcessStartInfo(exePath)
        {
            Arguments = "--sam-worker get-games",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Retry logic for fetching games with increased timeout
        const int maxAttempts = 3; // increased retries for robustness
        List<SamGameInfo>? games = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)); // further increased timeout for slow responses
            try
            {
                using var process = Process.Start(psi);
                if (process == null) return null;

                var readOutputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var waitForExitTask = process.WaitForExitAsync(cts.Token);

                await Task.WhenAll(readOutputTask, waitForExitTask);

                string output = await readOutputTask;
                string? json = ExtractJson(output);
                if (string.IsNullOrWhiteSpace(json))
                {
                    games = null;
                }
                else
                {
                    games = JsonSerializer.Deserialize<List<SamGameInfo>>(json, JsonOptions);
                    if (games != null) break; // success
                }
            }
                catch (OperationCanceledException)
                {
                    // timeout, log and retry if attempts remain
                    Console.WriteLine($"[SamService] GetGames timeout on attempt {attempt}");
                    // will retry if attempts remain
                }
            catch (Exception ex)
            {
                // other error, log and break
                Console.WriteLine($"[SamService] GetGames error on attempt {attempt}: {ex.Message}");
                break;
            }
            if (attempt < maxAttempts)
            {
                await Task.Delay(2000);
            }
        }
        return games;
    }

    private static async Task<SamGameStatsData?> RunWorkerGetStatsAsync(uint appId)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return new SamGameStatsData { AppId = appId, ErrorMessage = "Process path unavailable" };
        }

        var psi = new ProcessStartInfo(exePath)
        {
            Arguments = $"--sam-worker get-stats {appId}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // extended timeout for heavy games
        try
        {
            using var process = Process.Start(psi);
            if (process == null) return null;

            var readOutputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var waitForExitTask = process.WaitForExitAsync(cts.Token);

            await Task.WhenAll(readOutputTask, waitForExitTask);

            string output = await readOutputTask;
            string? json = ExtractJson(output);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<SamGameStatsData>(json, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            // timeout, log
            Console.WriteLine($"[SamService] GetStats timeout for AppId {appId}");
            return new SamGameStatsData { AppId = appId, ErrorMessage = "Request timed out connecting to Steam" };
        }
        catch (Exception ex)
        {
            // log unexpected errors
            Console.WriteLine($"[SamService] GetStats exception for AppId {appId}: {ex.Message}");
            return new SamGameStatsData { AppId = appId, ErrorMessage = $"JSON parse error: {ex.Message}" };
        }
    }

    private static async Task<SamStoreResult> RunWorkerStoreStatsAsync(SamStoreRequest request)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return new SamStoreResult { Success = false, ErrorMessage = "Process path unavailable" };
        }

        string tempPayloadFile = Path.Combine(Path.GetTempPath(), $"sam_store_{request.AppId}_{Guid.NewGuid():N}.json");
        try
        {
            string payloadJson = JsonSerializer.Serialize(request, JsonOptions);
            await File.WriteAllTextAsync(tempPayloadFile, payloadJson);

            var psi = new ProcessStartInfo(exePath)
            {
                Arguments = $"--sam-worker store-stats {request.AppId} \"{tempPayloadFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var process = Process.Start(psi);
            if (process == null)
            {
                return new SamStoreResult { Success = false, ErrorMessage = "Failed to launch Steam worker" };
            }

            var readOutputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var waitForExitTask = process.WaitForExitAsync(cts.Token);

            await Task.WhenAll(readOutputTask, waitForExitTask);

            string output = await readOutputTask;
            string? json = ExtractJson(output);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new SamStoreResult { Success = false, ErrorMessage = "Worker produced no output" };
            }

            return JsonSerializer.Deserialize<SamStoreResult>(json, JsonOptions) ?? new SamStoreResult
            {
                Success = false,
                ErrorMessage = "Failed to parse worker response",
            };
        }
        catch (Exception ex)
        {
            return new SamStoreResult { Success = false, ErrorMessage = $"Store error: {ex.Message}" };
        }
        finally
        {
            try
            {
                if (File.Exists(tempPayloadFile)) File.Delete(tempPayloadFile);
            }
            catch { /* clean up temp file */ }
        }
    }
}
