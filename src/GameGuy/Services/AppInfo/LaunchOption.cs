using System.Text.Json.Serialization;

namespace GameGuy.Services.AppInfo;

/// <summary>
/// One entry behind Steam's Play button: <c>config.launch.&lt;n&gt;</c> in appinfo.
///
/// <para>
/// Field frequency across 64,117 real entries: <c>executable</c> 100%, <c>config</c> 91%
/// (<c>oslist</c> 90%), <c>type</c> 82%, <c>description</c> 31%, <c>arguments</c> 10%,
/// <c>workingdir</c> 4%. Unused optionals are OMITTED by Steam rather than written empty, and
/// <see cref="ToTable"/> matches that.
/// </para>
/// </summary>
public sealed class LaunchOption
{
    /// <summary>The <c>config.launch</c> key this will be WRITTEN as. Reordering renumbers these
    /// 0,1,2… in list order, because Steam takes the running order from the key, not file position
    /// (SteamEdit reads entries OrderBy(key) and rewrites them as Count.ToString()).</summary>
    public string Index { get; set; } = "0";

    /// <summary>
    /// The key this entry was READ from, which stays put when <see cref="Index"/> is renumbered.
    /// Needed because <see cref="WriteAll"/> looks the old table up to carry across keys we don't model.
    /// After a reorder, looking it up by the NEW index would graft a different entry's extra keys
    /// (e.g. Half-Life's per-entry vacmodulefilename) onto this one. Empty for entries we created.
    /// </summary>
    public string SourceIndex { get; set; } = "";

    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "default";

    // config.* sub-table
    public string OsList { get; set; } = "";
    public string OsArch { get; set; } = "";
    public string BetaKey { get; set; } = "";
    public string OwnsDlc { get; set; } = "";

    [JsonIgnore]
    public string Summary =>
        string.Join("  ·  ", new[]
        {
            Executable,
            string.IsNullOrWhiteSpace(Arguments) ? null : Arguments,
            string.IsNullOrWhiteSpace(OsList) ? null : OsList,
        }.Where(s => !string.IsNullOrWhiteSpace(s))!);

    public LaunchOption Clone() => (LaunchOption)MemberwiseClone();

    /// <summary>Read one entry out of its VDF table.</summary>
    public static LaunchOption FromTable(string index, VdfTable table)
    {
        var config = table.GetTable("config");
        return new LaunchOption
        {
            Index = index,
            SourceIndex = index,
            Executable = table.GetText("executable") ?? "",
            Arguments = table.GetText("arguments") ?? "",
            WorkingDir = table.GetText("workingdir") ?? "",
            Description = table.GetText("description") ?? "",
            Type = table.GetText("type") ?? "",
            OsList = config?.GetText("oslist") ?? "",
            OsArch = config?.GetText("osarch") ?? "",
            BetaKey = config?.GetText("BetaKey") ?? "",
            OwnsDlc = config?.GetText("ownsdlc") ?? "",
        };
    }

    /// <summary>
    /// Apply this entry onto a VDF table, preserving any keys we don't model (some entries carry
    /// <c>vacmodulefilename</c>, <c>description_loc</c>, …). Rewriting from scratch would drop them.
    /// </summary>
    public VdfTable ToTable(VdfTable? existing = null)
    {
        var table = existing ?? new VdfTable();
        table.SetText("executable", Executable);
        table.SetText("arguments", Arguments);
        table.SetText("workingdir", WorkingDir);
        table.SetText("description", Description);
        table.SetText("type", Type);

        bool anyConfig = !string.IsNullOrEmpty(OsList) || !string.IsNullOrEmpty(OsArch)
                      || !string.IsNullOrEmpty(BetaKey) || !string.IsNullOrEmpty(OwnsDlc);
        if (anyConfig)
        {
            var config = table.EnsureTable("config");
            config.SetText("oslist", OsList);
            config.SetText("osarch", OsArch);
            config.SetText("BetaKey", BetaKey);      // Steam's exact casing
            config.SetText("ownsdlc", OwnsDlc);
        }
        else
        {
            // Only drop `config` if we're the ones who emptied it. An entry that never had one stays
            // without one, and one whose only config keys we don't model keeps them.
            if (table.GetTable("config") is { } cfg && cfg.Items.Count == 0) table.Remove("config");
        }
        return table;
    }

    // ── whole-app helpers ───────────────────────────────────────────

    /// <summary>Read every launch entry for an app, in file order.</summary>
    public static List<LaunchOption> ReadAll(VdfTable appBody)
    {
        var launch = appBody.GetTable("config")?.GetTable("launch");
        if (launch is null) return [];

        return launch.Items
            .Where(p => p.Type == VdfType.Table)
            .Select(p => FromTable(p.Name, (VdfTable)p.Value))
            .ToList();
    }

    /// <summary>
    /// Replace an app's <c>config.launch</c> with <paramref name="options"/>, reusing each entry's
    /// existing table where the index still exists so unmodelled keys survive.
    /// </summary>
    public static void WriteAll(VdfTable appBody, IReadOnlyList<LaunchOption> options)
    {
        var config = appBody.EnsureTable("config");
        var previous = config.GetTable("launch");
        var launch = new VdfTable();

        foreach (var option in options)
        {
            // Look up by SOURCE index: after a reorder the write key differs, and matching on it would
            // pull the wrong entry's unmodelled keys across.
            string lookup = string.IsNullOrEmpty(option.SourceIndex) ? option.Index : option.SourceIndex;
            var existing = previous?.GetTable(lookup)?.Clone();
            launch.Items.Add(VdfProperty.Table(option.Index, option.ToTable(existing)));
        }

        config.Remove("launch");
        config.Items.Add(VdfProperty.Table("launch", launch));
    }

    /// <summary>Renumber to 0,1,2… in list order. What makes a reorder actually take effect.</summary>
    public static void Renumber(IReadOnlyList<LaunchOption> options)
    {
        for (int i = 0; i < options.Count; i++) options[i].Index = i.ToString();
    }

    /// <summary>Next free index, appended after the highest in use (gaps are left alone).</summary>
    public static string NextIndex(IEnumerable<LaunchOption> options)
    {
        int max = -1;
        foreach (var o in options)
            if (int.TryParse(o.Index, out int n) && n > max) max = n;
        return (max + 1).ToString();
    }
}
