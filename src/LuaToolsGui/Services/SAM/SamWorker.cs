using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LuaToolsGui.Models;
using LuaToolsGui.Services.SAM.Native;
using LuaToolsGui.Services.SAM.Native.Types;
using LuaToolsGui.Services.SAM.Schema;

namespace LuaToolsGui.Services.SAM;

public static class SamWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        // args: ["--sam-worker", "<command>", ...]
        if (args.Length < 2)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { error = "Missing command" }, JsonOptions));
            return 1;
        }

        string command = args[1].ToLowerInvariant();

        try
        {
            switch (command)
            {
                case "get-games":
                    return await GetGamesAsync();

                case "get-stats":
                    if (args.Length < 3 || !uint.TryParse(args[2], out uint appId))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { error = "Invalid AppID" }, JsonOptions));
                        return 1;
                    }
                    return await GetStatsAsync(appId);

                case "store-stats":
                    if (args.Length < 3 || !uint.TryParse(args[2], out uint storeAppId))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { error = "Invalid AppID" }, JsonOptions));
                        return 1;
                    }
                    string payloadJson;
                    if (args.Length >= 4 && File.Exists(args[3]))
                    {
                        payloadJson = await File.ReadAllTextAsync(args[3]);
                    }
                    else if (args.Length >= 4)
                    {
                        payloadJson = args[3];
                    }
                    else
                    {
                        using var reader = new StreamReader(Console.OpenStandardInput());
                        payloadJson = await reader.ReadToEndAsync();
                    }

                    var request = JsonSerializer.Deserialize<SamStoreRequest>(payloadJson, JsonOptions);
                    if (request == null)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new SamStoreResult
                        {
                            Success = false,
                            ErrorMessage = "Invalid store request payload",
                        }, JsonOptions));
                        return 1;
                    }
                    return await StoreStatsAsync(storeAppId, request);

                default:
                    Console.WriteLine(JsonSerializer.Serialize(new { error = $"Unknown command '{command}'" }, JsonOptions));
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = false,
                errorMessage = ex.Message,
            }, JsonOptions));
            return 1;
        }
    }

    public static Task<int> GetGamesAsync()
    {
        var games = new List<SamGameInfo>();

        using var client = new Client();
        try
        {
            client.Initialize(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions));
            return Task.FromResult(1);
        }

        // We also add standard default games (e.g. Spacewar 480)
        var knownAppIds = new HashSet<uint> { 480 };

        // Read subscribed games from steam apps
        if (client.SteamApps008 != null && client.SteamApps001 != null)
        {
            // If user has cached games.xml or library, we can check ownership
            string? installPath = Steam.GetInstallPath();
            if (!string.IsNullOrEmpty(installPath))
            {
                string statsPath = Path.Combine(installPath, "appcache", "stats");
                if (Directory.Exists(statsPath))
                {
                    foreach (var file in Directory.GetFiles(statsPath, "UserGameStatsSchema_*.bin"))
                    {
                        string fn = Path.GetFileNameWithoutExtension(file);
                        if (fn.StartsWith("UserGameStatsSchema_") &&
                            uint.TryParse(fn["UserGameStatsSchema_".Length..], out uint fileAppId))
                        {
                            knownAppIds.Add(fileAppId);
                        }
                    }
                }

                // Check steamapps manifests
                string steamAppsDir = Path.Combine(installPath, "steamapps");
                if (Directory.Exists(steamAppsDir))
                {
                    foreach (var file in Directory.GetFiles(steamAppsDir, "appmanifest_*.acf"))
                    {
                        string fn = Path.GetFileNameWithoutExtension(file);
                        if (fn.StartsWith("appmanifest_") &&
                            uint.TryParse(fn["appmanifest_".Length..], out uint manifestAppId))
                        {
                            knownAppIds.Add(manifestAppId);
                        }
                    }
                }
            }

            foreach (uint id in knownAppIds)
            {
                try
                {
                    string? name = client.SteamApps001.GetAppData(id, "name");
                    games.Add(new SamGameInfo
                    {
                        Id = id,
                        Name = string.IsNullOrWhiteSpace(name) ? $"App {id}" : name,
                        Type = "normal",
                        ImageUrl = GetGameImageUrl(client, id),
                    });
                }
                catch
                {
                    // Ignore per-game retrieval error
                }
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(games, JsonOptions));
        return Task.FromResult(0);
    }

    public static async Task<int> GetStatsAsync(uint appId)
    {
        var result = new SamGameStatsData { AppId = appId };

        using var client = new Client();
        try
        {
            client.Initialize(appId);
        }
        catch (ClientInitializeException ex)
        {
            result.ErrorMessage = $"Failed to initialize Steam client: {ex.Failure} ({ex.Message})";
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 1;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Steam error: {ex.Message}";
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 1;
        }

        if (client.SteamUser == null || client.SteamUserStats == null || client.SteamApps001 == null)
        {
            result.ErrorMessage = "Steam interfaces not available";
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 1;
        }

        string? gameName = client.SteamApps001.GetAppData(appId, "name");
        result.GameName = string.IsNullOrWhiteSpace(gameName) ? $"App {appId}" : gameName;

        var statsReceivedTcs = new TaskCompletionSource<int>();
        var userStatsCallback = client.CreateAndRegisterCallback<Native.Callbacks.UserStatsReceived>();
        userStatsCallback.OnRun += param =>
        {
            statsReceivedTcs.TrySetResult(param.Result);
        };

        ulong steamId = client.SteamUser.GetSteamId();
        var callHandle = client.SteamUserStats.RequestUserStats(steamId);
        if (callHandle == CallHandle.Invalid)
        {
            result.ErrorMessage = "Failed to request user stats from Steam";
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 1;
        }

        // Run callbacks with timeout
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested && !statsReceivedTcs.Task.IsCompleted)
            {
                client.RunCallbacks(false);
                await Task.Delay(20);
            }
        });

        int statsResult;
        try
        {
            statsResult = await statsReceivedTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            statsResult = 1; // Try loading from local schema anyway if available
        }

        if (statsResult != 1)
        {
            // Some games return result 2 if not owned or no stats
            // Try fallback to local schema anyway
        }

        // Load schema
        var (achDefinitions, statDefinitions) = LoadSchema(client, appId);

        string currentLanguage = client.SteamApps008?.GetCurrentGameLanguage() ?? "english";

        // Fetch Achievements
        foreach (var def in achDefinitions)
        {
            if (string.IsNullOrEmpty(def.Id)) continue;

            bool isAchieved = false;
            uint unlockTime = 0;

            try
            {
                client.SteamUserStats.GetAchievementAndUnlockTime(def.Id, out isAchieved, out unlockTime);
            }
            catch
            {
                // Ignored
            }

            DateTime? unlockDateTime = isAchieved && unlockTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unlockTime).LocalDateTime
                : null;

            result.Achievements.Add(new SamAchievement
            {
                Id = def.Id,
                Name = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name,
                Description = def.Description ?? string.Empty,
                IsAchieved = isAchieved,
                OriginalIsAchieved = isAchieved,
                UnlockTime = unlockDateTime,
                IconNormal = def.IconNormal,
                IconLocked = def.IconLocked,
                IconUrl = !string.IsNullOrEmpty(def.IconNormal)
                    ? $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/{def.IconNormal}"
                    : null,
                LockedIconUrl = !string.IsNullOrEmpty(def.IconLocked)
                    ? $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/{def.IconLocked}"
                    : (!string.IsNullOrEmpty(def.IconNormal) ? $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/{def.IconNormal}" : null),
                IsHidden = def.IsHidden,
                Permission = def.Permission,
            });
        }

        // Fetch Statistics
        foreach (var def in statDefinitions)
        {
            if (string.IsNullOrEmpty(def.Id)) continue;

            if (def.StatType == UserStatType.Integer)
            {
                int val = 0;
                client.SteamUserStats.GetStatValue(def.Id, out val);
                result.Stats.Add(new SamStat
                {
                    Id = def.Id,
                    DisplayName = string.IsNullOrWhiteSpace(def.DisplayName) ? def.Id : def.DisplayName,
                    StatType = UserStatType.Integer,
                    IntValue = val,
                    OriginalIntValue = val,
                    MinInt = def.MinInt,
                    MaxInt = def.MaxInt,
                    IncrementOnly = def.IncrementOnly,
                    Permission = def.Permission,
                });
            }
            else if (def.StatType is UserStatType.Float or UserStatType.AverageRate)
            {
                float val = 0;
                client.SteamUserStats.GetStatValue(def.Id, out val);
                result.Stats.Add(new SamStat
                {
                    Id = def.Id,
                    DisplayName = string.IsNullOrWhiteSpace(def.DisplayName) ? def.Id : def.DisplayName,
                    StatType = def.StatType,
                    FloatValue = val,
                    OriginalFloatValue = val,
                    MinFloat = def.MinFloat,
                    MaxFloat = def.MaxFloat,
                    IncrementOnly = def.IncrementOnly,
                    Permission = def.Permission,
                });
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    public static async Task<int> StoreStatsAsync(uint appId, SamStoreRequest request)
    {
        var result = new SamStoreResult();

        using var client = new Client();
        try
        {
            client.Initialize(appId);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Failed to initialize Steam: {ex.Message}";
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 1;
        }

        if (client.SteamUserStats == null)
        {
            result.Success = false;
            result.ErrorMessage = "SteamUserStats interface is not available";
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 1;
        }

        if (request.ResetAll)
        {
            client.SteamUserStats.ResetAllStats(request.ResetAchievementsToo);
        }

        // Apply achievement changes
        int achievementsStored = 0;
        foreach (var (achId, state) in request.Achievements)
        {
            if (client.SteamUserStats.SetAchievement(achId, state))
            {
                achievementsStored++;
            }
            else
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to set achievement '{achId}' to {state}";
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return 1;
            }
        }

        // Apply stat changes
        int statsStored = 0;
        foreach (var (statId, intVal) in request.IntStats)
        {
            if (client.SteamUserStats.SetStatValue(statId, intVal))
            {
                statsStored++;
            }
            else
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to set integer stat '{statId}' to {intVal}";
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return 1;
            }
        }

        foreach (var (statId, floatVal) in request.FloatStats)
        {
            if (client.SteamUserStats.SetStatValue(statId, floatVal))
            {
                statsStored++;
            }
            else
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to set float stat '{statId}' to {floatVal}";
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return 1;
            }
        }

        // Commit to Steam
        if (!client.SteamUserStats.StoreStats())
        {
            result.Success = false;
            result.ErrorMessage = "Steam rejected StoreStats() commit call";
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 1;
        }

        // Run callbacks to process confirmation
        for (int i = 0; i < 10; i++)
        {
            client.RunCallbacks(false);
            await Task.Delay(25);
        }

        result.Success = true;
        result.AchievementsStored = achievementsStored;
        result.StatsStored = statsStored;

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    private static string? GetGameImageUrl(Client client, uint appId)
    {
        if (client.SteamApps001 == null || client.SteamApps008 == null) return null;

        string currentLanguage = client.SteamApps008.GetCurrentGameLanguage() ?? "english";

        string? candidate = client.SteamApps001.GetAppData(appId, $"small_capsule/{currentLanguage}");
        if (!string.IsNullOrEmpty(candidate))
        {
            return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/{candidate}";
        }

        if (currentLanguage != "english")
        {
            candidate = client.SteamApps001.GetAppData(appId, "small_capsule/english");
            if (!string.IsNullOrEmpty(candidate))
            {
                return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/{candidate}";
            }
        }

        candidate = client.SteamApps001.GetAppData(appId, "logo");
        if (!string.IsNullOrEmpty(candidate))
        {
            return $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/{candidate}.jpg";
        }

        return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";
    }

    private record RawAchDef(string Id, string Name, string Description, string? IconNormal, string? IconLocked, bool IsHidden, int Permission);
    private record RawStatDef(string Id, string DisplayName, UserStatType StatType, int MinInt, int MaxInt, float MinFloat, float MaxFloat, bool IncrementOnly, int Permission);

    private static (List<RawAchDef> Achievements, List<RawStatDef> Stats) LoadSchema(Client client, uint appId)
    {
        var achievements = new List<RawAchDef>();
        var stats = new List<RawStatDef>();

        string? installPath = Steam.GetInstallPath();
        if (string.IsNullOrEmpty(installPath)) return (achievements, stats);

        string schemaPath = Path.Combine(installPath, "appcache", "stats", $"UserGameStatsSchema_{appId}.bin");
        if (!File.Exists(schemaPath)) return (achievements, stats);

        var kv = KeyValue.LoadAsBinary(schemaPath);
        if (kv == null) return (achievements, stats);

        string currentLanguage = client.SteamApps008?.GetCurrentGameLanguage() ?? "english";

        var statsNode = kv[appId.ToString(CultureInfo.InvariantCulture)]["stats"];
        if (!statsNode.Valid || statsNode.Children == null) return (achievements, stats);

        foreach (var stat in statsNode.Children)
        {
            if (!stat.Valid) continue;

            UserStatType type = UserStatType.Invalid;
            var typeNode = stat["type"];
            if (typeNode.Valid && typeNode.Type == KeyValueType.String && typeNode.Value is string typeStr)
            {
                if (!Enum.TryParse(typeStr, true, out type))
                {
                    type = UserStatType.Invalid;
                }
            }

            if (type == UserStatType.Invalid)
            {
                var typeIntNode = stat["type_int"];
                int rawType = typeIntNode.Valid ? typeIntNode.AsInteger(0) : typeNode.AsInteger(0);
                type = (UserStatType)rawType;
            }

            switch (type)
            {
                case UserStatType.Integer:
                {
                    string id = stat["name"].AsString("");
                    string displayName = GetLocalizedString(stat["display"]["name"], currentLanguage, id);
                    stats.Add(new RawStatDef(
                        id,
                        displayName,
                        UserStatType.Integer,
                        stat["min"].AsInteger(int.MinValue),
                        stat["max"].AsInteger(int.MaxValue),
                        0, 0,
                        stat["incrementonly"].AsBoolean(false),
                        stat["permission"].AsInteger(0)));
                    break;
                }
                case UserStatType.Float:
                case UserStatType.AverageRate:
                {
                    string id = stat["name"].AsString("");
                    string displayName = GetLocalizedString(stat["display"]["name"], currentLanguage, id);
                    stats.Add(new RawStatDef(
                        id,
                        displayName,
                        type,
                        0, 0,
                        stat["min"].AsFloat(float.MinValue),
                        stat["max"].AsFloat(float.MaxValue),
                        stat["incrementonly"].AsBoolean(false),
                        stat["permission"].AsInteger(0)));
                    break;
                }
                case UserStatType.Achievements:
                case UserStatType.GroupAchievements:
                {
                    if (stat.Children != null)
                    {
                        foreach (var bits in stat.Children.Where(
                            b => string.Equals(b.Name, "bits", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (!bits.Valid || bits.Children == null) continue;

                            foreach (var bit in bits.Children)
                            {
                                string id = bit["name"].AsString("");
                                string name = GetLocalizedString(bit["display"]["name"], currentLanguage, id);
                                string desc = GetLocalizedString(bit["display"]["desc"], currentLanguage, "");
                                achievements.Add(new RawAchDef(
                                    id,
                                    name,
                                    desc,
                                    bit["display"]["icon"].AsString(string.Empty),
                                    bit["display"]["icon_gray"].AsString(string.Empty),
                                    bit["display"]["hidden"].AsBoolean(false),
                                    bit["permission"].AsInteger(0)));
                            }
                        }
                    }
                    break;
                }
            }
        }

        return (achievements, stats);
    }

    private static string GetLocalizedString(KeyValue kv, string language, string defaultValue)
    {
        string name = kv[language].AsString("");
        if (!string.IsNullOrEmpty(name)) return name;

        if (language != "english")
        {
            name = kv["english"].AsString("");
            if (!string.IsNullOrEmpty(name)) return name;
        }

        name = kv.AsString("");
        if (!string.IsNullOrEmpty(name)) return name;

        return defaultValue;
    }
}
