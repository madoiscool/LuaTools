using System.IO;
using System.Text;
using System.Text.Json;
using LuaToolsGui.Models;
using LuaToolsGui.Services.SAM.Native.Types;
using LuaToolsGui.Services.SAM.Schema;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

public class SamTests
{
    [Fact]
    public void KeyValue_BinaryRead_ParsesCorrectly()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);

        // Write KeyValue node (type None = 0)
        writer.Write((byte)KeyValueType.None);
        writer.WriteNullTerminatedUtf8("UserGameStatsSchema");

        // Write String child (type String = 1)
        writer.Write((byte)KeyValueType.String);
        writer.WriteNullTerminatedUtf8("gamename");
        writer.WriteNullTerminatedUtf8("Spacewar");

        // Write Int32 child (type Int32 = 2)
        writer.Write((byte)KeyValueType.Int32);
        writer.WriteNullTerminatedUtf8("gameid");
        writer.Write((int)480);

        // Write End node
        writer.Write((byte)KeyValueType.End);

        // Write End of root
        writer.Write((byte)KeyValueType.End);

        ms.Position = 0;

        var root = new KeyValue();
        bool success = root.ReadAsBinary(ms);

        Assert.True(success);
        Assert.NotNull(root.Children);
        Assert.Single(root.Children);

        var schemaNode = root["UserGameStatsSchema"];
        Assert.True(schemaNode.Valid);
        Assert.Equal("Spacewar", schemaNode["gamename"].AsString());
        Assert.Equal(480, schemaNode["gameid"].AsInteger());
    }

    [Fact]
    public void SamAchievement_ModificationTracking()
    {
        var ach = new SamAchievement
        {
            Id = "ACH_01",
            Name = "First Blood",
            Description = "Defeat 1 enemy",
            OriginalIsAchieved = false,
            IsAchieved = false,
            IconUrl = "https://example.com/icon.png",
            LockedIconUrl = "https://example.com/icon_gray.png",
        };

        Assert.False(ach.IsModified);
        Assert.Equal("https://example.com/icon_gray.png", ach.EffectiveIconUrl);

        // Unlock
        ach.IsAchieved = true;
        Assert.True(ach.IsModified);
        Assert.Equal("https://example.com/icon.png", ach.EffectiveIconUrl);

        // Revert
        ach.IsAchieved = false;
        Assert.False(ach.IsModified);
    }

    [Fact]
    public void SamStat_IntegerModificationTracking()
    {
        var stat = new SamStat
        {
            Id = "STAT_KILLS",
            DisplayName = "Total Kills",
            StatType = UserStatType.Integer,
            OriginalIntValue = 50,
            IntValue = 50,
        };

        Assert.False(stat.IsModified);
        Assert.Equal("50", stat.ValueString);

        // Edit via string input
        stat.ValueString = "75";
        Assert.True(stat.IsModified);
        Assert.Equal(75, stat.IntValue);

        // Revert
        stat.ValueString = "50";
        Assert.False(stat.IsModified);
    }

    [Fact]
    public void SamStat_FloatModificationTracking()
    {
        var stat = new SamStat
        {
            Id = "STAT_ACCURACY",
            DisplayName = "Accuracy",
            StatType = UserStatType.Float,
            OriginalFloatValue = 0.85f,
            FloatValue = 0.85f,
        };

        Assert.False(stat.IsModified);

        stat.ValueString = "0.95";
        Assert.True(stat.IsModified);
        Assert.Equal(0.95f, stat.FloatValue, precision: 2);
    }

    [Fact]
    public void SamStoreRequest_JsonSerialization()
    {
        var req = new SamStoreRequest
        {
            AppId = 480,
        };
        req.Achievements["ACH_01"] = true;
        req.Achievements["ACH_02"] = false;
        req.IntStats["STAT_SCORE"] = 1200;
        req.FloatStats["STAT_RATIO"] = 2.5f;

        string json = JsonSerializer.Serialize(req);
        var deserialized = JsonSerializer.Deserialize<SamStoreRequest>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(480u, deserialized.AppId);
        Assert.True(deserialized.Achievements["ACH_01"]);
        Assert.False(deserialized.Achievements["ACH_02"]);
        Assert.Equal(1200, deserialized.IntStats["STAT_SCORE"]);
        Assert.Equal(2.5f, deserialized.FloatStats["STAT_RATIO"]);
    }

    [Fact]
    public void SamStoreResult_JsonSerialization()
    {
        var res = new SamStoreResult
        {
            Success = true,
            AchievementsStored = 2,
            StatsStored = 1,
        };

        string json = JsonSerializer.Serialize(res);
        var deserialized = JsonSerializer.Deserialize<SamStoreResult>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.Equal(2, deserialized.AchievementsStored);
        Assert.Equal(1, deserialized.StatsStored);
    }

    [Fact]
    public void AchievementsViewModel_BulkOperations_And_Progress()
    {
        // Setup mock service / VM
        var settings = new LuaToolsGui.Services.SettingsService();
        var steam = new LuaToolsGui.Services.SteamService(settings);
        var cache = new LuaToolsGui.Services.CacheService();
        var appList = new LuaToolsGui.Services.SteamAppListCache();
        var appInfo = new LuaToolsGui.Services.SteamAppInfoCache(cache);
        var covers = new LuaToolsGui.Services.CoverCache();
        var toast = new LuaToolsGui.Services.ToastService();
        var samService = new LuaToolsGui.Services.SAM.SamService(steam, appInfo, appList, covers);

        var vm = new AchievementsViewModel(samService, toast, covers);

        // Add 3 achievements
        var a1 = new SamAchievement { Id = "ACH_1", Name = "A1", OriginalIsAchieved = false, IsAchieved = false };
        var a2 = new SamAchievement { Id = "ACH_2", Name = "A2", OriginalIsAchieved = true, IsAchieved = true };
        var a3 = new SamAchievement { Id = "ACH_3", Name = "A3", OriginalIsAchieved = false, IsAchieved = false };

        vm.Achievements.Add(a1);
        vm.Achievements.Add(a2);
        vm.Achievements.Add(a3);

        // Unlock All
        vm.UnlockAllCommand.Execute(null);
        Assert.All(vm.Achievements, a => Assert.True(a.IsAchieved));
        Assert.Equal(3, vm.UnlockedCount);
        Assert.Equal(3, vm.TotalAchievementsCount);
        Assert.Equal(100.0, vm.ProgressPercentage);
        Assert.True(vm.HasPendingChanges); // a1 and a3 modified

        // Lock All
        vm.LockAllCommand.Execute(null);
        Assert.All(vm.Achievements, a => Assert.False(a.IsAchieved));
        Assert.Equal(0, vm.UnlockedCount);
        Assert.Equal(0.0, vm.ProgressPercentage);
        Assert.True(vm.HasPendingChanges); // a2 modified

        // Invert
        vm.InvertSelectionCommand.Execute(null);
        Assert.All(vm.Achievements, a => Assert.True(a.IsAchieved));
        Assert.Equal(3, vm.UnlockedCount);
        Assert.Equal(100.0, vm.ProgressPercentage);
    }

    [Fact]
    public async Task SamWorker_InvalidCommand_ReturnsErrorCode()
    {
        int exitCode = await LuaToolsGui.Services.SAM.SamWorker.RunAsync(["--sam-worker", "unknown-cmd"]);
        Assert.Equal(1, exitCode);
    }
}

internal static class BinaryWriterExtensions
{
    public static void WriteNullTerminatedUtf8(this BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write(bytes);
        writer.Write((byte)0);
    }
}
