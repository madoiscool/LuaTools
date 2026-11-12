using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LuaToolsGui.Services.AppInfo;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>What a repair pass did.</summary>
/// <param name="Repaired">An appmanifest was written; the game is now installed as far as Steam knows.</param>
/// <param name="Reason">Why not, when <paramref name="Repaired"/> is false. Logged, not shown.</param>
/// <param name="InstallDir">The folder Steam is now pointed at, when repaired.</param>
public readonly record struct InstallStateRepairResult(bool Repaired, string Reason, string? InstallDir)
{
    public static InstallStateRepairResult Fail(string reason) => new(false, reason, null);
}

/// <summary>
/// Writes the appmanifest Steam needs for a game whose files are already on disk.
/// </summary>
/// <remarks>
/// <para>
/// Steam derives "installed" from <c>steamapps\appmanifest_&lt;appid&gt;.acf</c> alone. With the file
/// missing, or present but blanked to <c>StateFlags 1538</c> (Update Required | Update Paused | Update
/// Started) with an empty <c>InstalledDepots</c>, there is no install row: the library entry is dead,
/// and "Install" downloads everything again over files that are already complete. Nothing in Steam
/// repairs that, and <see cref="SteamLibraryService.GetInstallDir"/> can only report it.
/// </para>
/// <para>
/// The folder is located through appinfo (never guessed), and it is written into the manifest as
/// <c>installdir</c> with its real name, so a ragged folder name is not something the user has to
/// rename away first. Depot ids, gids and sizes come from the pinned lua, which is the only honest
/// source for them; a depot with no gid is left out rather than invented.
/// </para>
/// <para>
/// Steam holds appmanifests in memory and rewrites them on exit, so the write only sticks with Steam
/// shut — the same reason <see cref="LaunchOptionsService"/> stops it for appinfo edits. It is restarted
/// afterwards if the caller had it open.
/// </para>
/// </remarks>
public class InstallStateRepairService(
    SteamService steam, SteamLibraryService library, ILogger<InstallStateRepairService> log)
{
    /// <summary>Depot ids Steam never asks to install, mapped to the app that really owns them.</summary>
    private static readonly Regex SharedOwner = new(@"[Ss]hared from [Aa]pp (\d+)");

    /// <summary>Build number in the lua header (<c>-- Build: 24816905</c>), when the source writes one.</summary>
    private static readonly Regex HeaderBuild = new(@"^\s*--\s*Build:\s*(\d+)", RegexOptions.Multiline);

    /// <summary>Steam's state bitfield in an appmanifest. 4 is FullyInstalled.</summary>

    /// <summary>
    /// Locate the game on disk, then write the appmanifest for it. Never throws; every failure is a
    /// <see cref="InstallStateRepairResult"/> reason.
    /// </summary>
    public async Task<InstallStateRepairResult> RepairAsync(long appId, CancellationToken ct = default)
    {
        bool wasRunning = SteamService.IsSteamRunning();
        if (wasRunning) steam.StopSteam();
        try { return await Task.Run(() => Repair(appId, ct), ct); }
        finally { if (wasRunning) steam.StartSteam(); }
    }

    /// <summary>The pass with Steam assumed closed.</summary>
    public InstallStateRepairResult Repair(long appId, CancellationToken ct = default)
    {
        try
        {
            if (steam.EffectivePath is not { } steamRoot) return InstallStateRepairResult.Fail("steam-not-found");
            if (steam.AppInfoPath is not { } appInfoPath || !File.Exists(appInfoPath))
                return InstallStateRepairResult.Fail("no-appinfo");

            string? installDir;
            string name;
            string launchExe;
            using (var appInfo = new AppInfoFile(appInfoPath))
            {
                var body = appInfo.ReadAppBody((int)appId);
                if (body is null) return InstallStateRepairResult.Fail("no-appinfo");
                installDir = body.GetTable("config")?.GetText("installdir");
                name = body.GetText("common") is var _ && body.GetTable("common")?.GetText("name") is { } n
                    ? n : appId.ToString();
                launchExe = LaunchOption.ReadAll(body)
                    .FirstOrDefault(l => l.OsList is "" or "windows")?.Executable ?? "";
            }

            if (string.IsNullOrWhiteSpace(installDir)) return InstallStateRepairResult.Fail("no-installdir");

            return RepairCore(appId, installDir, launchExe, name);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Install state repair for {AppId} failed", appId);
            return InstallStateRepairResult.Fail("write-failed");
        }
    }

    /// <summary>
    /// The pass with the appinfo values already in hand, so it can run without an appinfo.vdf to read.
    /// </summary>
    internal InstallStateRepairResult RepairCore(long appId, string? installDir, string? launchExe, string name)
    {
        try
        {
            if (steam.EffectivePath is not { } steamRoot) return InstallStateRepairResult.Fail("steam-not-found");

            // Already installed? Then this is not our problem to solve. An appmanifest existing is not
            // enough: the shape being repaired is one that IS there but blanked to StateFlags 1538
            // (update required, empty InstalledDepots), so only a FullyInstalled bit counts.
            foreach (string root in SteamLibraryService.GetLibraryRoots(steamRoot))
            {
                string existing = Path.Combine(root, "steamapps", $"appmanifest_{appId}.acf");
                if (File.Exists(existing) && IsFullyInstalled(existing))
                    return InstallStateRepairResult.Fail("already-installed");
            }

            if (library.FindAdoptable(installDir, launchExe) is not { } candidate)
                return InstallStateRepairResult.Fail("no-candidate");

            string acf = Path.Combine(candidate.SteamAppsDir, $"appmanifest_{appId}.acf");

            var (depots, shared, buildId) = ReadLua(appId);
            long size = DirectorySize(candidate.FullPath);
            if (File.Exists(acf))
                File.Copy(acf, $"{acf}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);

            string folderName = Path.GetFileName(candidate.FullPath);
            File.WriteAllText(
                acf,
                BuildAcf(appId, name, folderName, depots, shared, size, buildId),
                new UTF8Encoding(false));

            EnsureLibraryIndexEntry(candidate.SteamAppsDir, appId, size);
            EnsureSharedDepotsInstalled(candidate.SteamAppsDir, shared, depots);
            WriteSteamAppId(candidate.FullPath, appId);
            ClearStaging(candidate.SteamAppsDir, appId);

            log.LogInformation(
                "Repaired install state for {AppId}: {Dir} ({Size} bytes, {Depots} depots, {Shared} shared)",
                appId, candidate.FullPath, size, depots.Count, shared.Count);


            return new InstallStateRepairResult(true, "ok", candidate.FullPath);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Install state repair for {AppId} failed", appId);
            return InstallStateRepairResult.Fail("write-failed");
        }
    }

    /// <summary>True when the manifest carries Steam's FullyInstalled bit (4).</summary>
    private static bool IsFullyInstalled(string acfPath)
    {
        try
        {
            // Split on quotes: the token after "StateFlags" is its value.
            string[] parts = File.ReadAllText(acfPath).Split('"');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!string.Equals(parts[i], "StateFlags", StringComparison.OrdinalIgnoreCase)) continue;
                for (int j = i + 1; j < parts.Length; j++)
                {
                    if (string.IsNullOrWhiteSpace(parts[j])) continue;
                    return int.TryParse(parts[j].Trim(), out int flags) && (flags & 4) != 0;
                }
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Depot ids, gids and sizes out of the pinned lua, plus the app that owns any shared depot. Gid and
    /// size are needed per depot for the manifest; a depot with neither a live nor a commented pin
    /// resolves to whatever Steam ships, so there is no honest gid to write and it is skipped.
    /// </summary>
    private (List<(long Id, string Gid, long Size)> Depots,
             Dictionary<long, long> Shared,
             long BuildId) ReadLua(long appId)
    {
        var depots = new List<(long, string, long)>();
        var shared = new Dictionary<long, long>();
        long buildId = 0;

        if (steam.StPlugInDir is not { } dir) return (depots, shared, buildId);
        string path = Path.Combine(dir, $"{appId}.lua");
        if (!File.Exists(path)) return (depots, shared, buildId);

        string text;
        try { text = File.ReadAllText(path); } catch { return (depots, shared, buildId); }

        var build = HeaderBuild.Match(text);
        if (build.Success) long.TryParse(build.Groups[1].Value, out buildId);

        if (LuaFileParser.Parse(path, appId) is not { } lua) return (depots, shared, buildId);

        foreach (LuaEntry entry in lua.Entries)
        {
            if (!entry.HasKey || entry.Id == appId) continue;

            string? gid = lua.ActivePins.GetValueOrDefault(entry.Id)
                          ?? lua.CommentedPins.GetValueOrDefault(entry.Id);
            if (gid is null) continue;

            depots.Add((entry.Id, gid, entry.SizeOnDisk ?? 0));

            if (entry.Comment is { } comment && SharedOwner.Match(comment) is { Success: true } m
                && long.TryParse(m.Groups[1].Value, out long owner) && owner != appId)
            {
                shared[entry.Id] = owner;
            }
        }

        return (depots, shared, buildId);
    }

    /// <summary>
    /// The appmanifest itself. Field order follows what Steam writes, so a diff against a real one only
    /// ever shows the values. <c>AutoUpdateBehavior 1</c> (only update on launch) is deliberate: the lua
    /// describes one build, and letting the scheduler chase another the moment the entry appears would
    /// undo the adoption.
    /// </summary>
    internal static string BuildAcf(
        long appId, string name, string installDir,
        IEnumerable<(long Id, string Gid, long Size)> depots,
        IReadOnlyDictionary<long, long> shared,
        long sizeOnDisk, long buildId)
    {
        var sb = new StringBuilder();
        sb.Append("\"AppState\"\n{\n");
        Line(sb, "appid", appId.ToString());
        Line(sb, "Universe", "1");
        Line(sb, "name", name);
        Line(sb, "StateFlags", "4"); // FullyInstalled
        Line(sb, "installdir", installDir);
        Line(sb, "SizeOnDisk", sizeOnDisk.ToString());
        Line(sb, "StagingSize", "0");
        Line(sb, "buildid", buildId.ToString());
        Line(sb, "UpdateResult", "0");
        foreach (string field in (string[])["BytesToDownload", "BytesDownloaded", "BytesToStage", "BytesStaged"])
            Line(sb, field, "0");
        Line(sb, "TargetBuildID", buildId.ToString());
        Line(sb, "AutoUpdateBehavior", "1");
        Line(sb, "AllowOtherDownloadsWhileRunning", "0");
        Line(sb, "ScheduledAutoUpdate", "0");

        sb.Append("\t\"InstalledDepots\"\n\t{\n");
        foreach (var (id, gid, size) in depots)
        {
            sb.Append($"\t\t\"{id}\"\n\t\t{{\n");
            Line(sb, "manifest", gid, 3);
            Line(sb, "size", size.ToString(), 3);
            sb.Append("\t\t}\n");
        }
        sb.Append("\t}\n");

        sb.Append("\t\"SharedDepots\"\n\t{\n");
        foreach (var (id, owner) in shared)
            Line(sb, id.ToString(), owner.ToString(), 2);
        sb.Append("\t}\n");

        sb.Append("\t\"UserConfig\"\n\t{\n");
        Line(sb, "language", "english", 2);
        sb.Append("\t}\n\t\"MountedConfig\"\n\t{\n\t}\n}\n");
        return sb.ToString();

        static void Line(StringBuilder sb, string key, string value, int depth = 2) =>
            sb.Append(new string('\t', depth)).Append('"').Append(key).Append("\"\t\t\"")
              .Append(Escape(value)).Append("\"\n");
    }

    /// <summary>VDF escapes backslashes and quotes inside a value.</summary>
    private static string Escape(string value) => value.Replace(@"\", @"\\").Replace("\"", "\\\"");

    private static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    /// <summary>
    /// Add the app to its library's <c>libraryfolders.vdf</c> <c>apps</c> block. Steam rebuilds this on
    /// its own, but only for apps it already knows about; an app it has never seen needs the entry to be
    /// offered as installed at all.
    /// </summary>
    private void EnsureLibraryIndexEntry(string steamappsDir, long appId, long size)
    {
        string vdf = Path.Combine(steamappsDir, "libraryfolders.vdf");
        if (!File.Exists(vdf)) return;

        string text;
        try { text = File.ReadAllText(vdf); } catch { return; }
        if (text.Contains($"\"{appId}\"")) return;

        // Insert into the apps block of the folder entry that contains this steamapps dir.
        string library = Path.GetDirectoryName(steamappsDir)!;
        int at = text.IndexOf(Escape(library), StringComparison.OrdinalIgnoreCase);
        if (at < 0) return;

        int apps = text.IndexOf("\"apps\"", at, StringComparison.OrdinalIgnoreCase);
        if (apps < 0) return;

        int brace = text.IndexOf('{', apps);
        if (brace < 0) return;

        string insert = $"\n\t\t\t\"{appId}\"\t\t\"{size}\"";

        try { File.WriteAllText(vdf, text.Insert(brace + 1, insert), new UTF8Encoding(false)); }
        catch { }
    }

    /// <summary>
    /// A shared depot (VC runtime, DirectX) only resolves if the app that OWNS it lists it as installed.
    /// When it does not, the depot dependency build aborts with "Invalid content configuration" and the
    /// whole app update dies before it starts, which is exactly the state this repair exists to leave.
    /// </summary>
    private void EnsureSharedDepotsInstalled(
        string steamappsDir, Dictionary<long, long> shared, List<(long Id, string Gid, long Size)> depots)
    {
        foreach (var (depotId, owner) in shared)
        {
            string ownerAcf = Path.Combine(steamappsDir, $"appmanifest_{owner}.acf");
            if (!File.Exists(ownerAcf)) continue;

            string text;
            try { text = File.ReadAllText(ownerAcf); } catch { continue; }
            if (text.Contains($"\"{depotId}\"")) continue;

            int at = text.IndexOf("\"InstalledDepots\"", StringComparison.OrdinalIgnoreCase);
            int brace = at < 0 ? -1 : text.IndexOf('{', at);
            if (brace < 0) continue;

            var entry = depots.FirstOrDefault(d => d.Id == depotId);
            string gid = entry.Gid ?? "0";
            string insert =
                $"\n\t\t\"{depotId}\"\n\t\t{{\n\t\t\t\"manifest\"\t\t\"{gid}\"\n\t\t\t\"size\"\t\t\"{entry.Size}\"\n\t\t}}";

            try { File.WriteAllText(ownerAcf, text.Insert(brace + 1, insert), new UTF8Encoding(false)); }
            catch { }
        }
    }

    /// <summary>Games launched from the exe (not the library) resolve their appid from this file.</summary>
    private void WriteSteamAppId(string installDir, long appId)
    {
        try { File.WriteAllText(Path.Combine(installDir, "steam_appid.txt"), appId.ToString()); }
        catch { }
    }

    /// <summary>
    /// An orphaned staging folder makes Steam resume a half-finished download instead of reconfiguring
    /// against the files that are there.
    /// </summary>
    private void ClearStaging(string steamappsDir, long appId)
    {
        string staging = Path.Combine(steamappsDir, "downloading", appId.ToString());
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        catch { }
    }
}
