using System;
using System.Runtime.InteropServices;

namespace LuaToolsGui.Services.SAM.Native.Interfaces;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ISteamUserStats013
{
    public IntPtr GetStatFloat;
    public IntPtr GetStatInteger;
    public IntPtr SetStatFloat;
    public IntPtr SetStatInteger;
    public IntPtr UpdateAvgRateStat;
    public IntPtr GetAchievement;
    public IntPtr SetAchievement;
    public IntPtr ClearAchievement;
    public IntPtr GetAchievementAndUnlockTime;
    public IntPtr StoreStats;
    public IntPtr GetAchievementIcon;
    public IntPtr GetAchievementDisplayAttribute;
    public IntPtr IndicateAchievementProgress;
    public IntPtr GetNumAchievements;
    public IntPtr GetAchievementName;
    public IntPtr RequestUserStats;
    public IntPtr GetUserStatFloat;
    public IntPtr GetUserStatInt;
    public IntPtr GetUserAchievement;
    public IntPtr GetUserAchievementAndUnlockTime;
    public IntPtr ResetAllStats;
    public IntPtr FindOrCreateLeaderboard;
    public IntPtr FindLeaderboard;
    public IntPtr GetLeaderboardName;
    public IntPtr GetLeaderboardEntryCount;
    public IntPtr GetLeaderboardSortMethod;
    public IntPtr GetLeaderboardDisplayType;
    public IntPtr DownloadLeaderboardEntries;
    public IntPtr DownloadLeaderboardEntriesForUsers;
    public IntPtr GetDownloadedLeaderboardEntry;
    public IntPtr UploadLeaderboardScore;
    public IntPtr AttachLeaderboardUGC;
    public IntPtr GetNumberOfCurrentPlayers;
    public IntPtr RequestGlobalAchievementPercentages;
    public IntPtr GetMostAchievedAchievementInfo;
    public IntPtr GetNextMostAchievedAchievementInfo;
    public IntPtr GetAchievementAchievedPercent;
    public IntPtr RequestGlobalStats;
    public IntPtr GetGlobalStatFloat;
    public IntPtr GetGlobalStatInteger;
    public IntPtr GetGlobalStatHistoryFloat;
    public IntPtr GetGlobalStatHistoryInteger;
    public IntPtr GetAchievementProgressLimitsFloat;
    public IntPtr GetAchievementProgressLimitsInteger;
}
