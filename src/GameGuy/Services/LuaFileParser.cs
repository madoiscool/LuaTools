using System.IO;
using System.Text.RegularExpressions;

namespace GameGuy.Services;

/// <summary>
/// One depot/app entry declared in a lua file via addappid / setManifestid.
/// <para>
/// <paramref name="ManifestId"/> is an ACTIVE version pin. <paramref name="CommentedManifestId"/> is a
/// pin that exists in the file but is commented out, which is the normal state after an install with
/// the "Auto Update Apps" setting on (see <see cref="LuaInstaller"/>). The two must stay distinct: a
/// commented pin means "Steam keeps this updated", not "pinned to this manifest".
/// </para>
/// </summary>
/// <param name="Key">
/// The depot decryption key from <c>addappid(id, 1, "key")</c>, or null for a bare <c>addappid(id)</c>
/// (a DLC entitlement rather than a content depot). Carried, not just counted, because the depot
/// downloader needs the actual key bytes to decrypt CDN chunks.
/// </param>
public record LuaEntry(long Id, string? Key, string? ManifestId, string? CommentedManifestId, string? Comment)
{
    /// <summary>True when this entry carries a decryption key, i.e. it's a content depot.</summary>
    public bool HasKey => Key is not null;

    /// <summary>
    /// Size on disk from <c>setManifestid(id, "gid", size)</c>'s third argument, or null when the line
    /// omits it. The only size available for a depot Steam's app info does not list.
    /// </summary>
    public long? SizeOnDisk { get; init; }
}

/// <summary>Parsed contents of a stplug-in lua file.
/// <para>
/// <paramref name="Entries"/> is what the lua ACTIVELY declares. <paramref name="DisabledEntries"/> are
/// ids whose addappid line exists but is commented out. Switched off from the Builds page. They're kept
/// out of Entries so the install-time before/after diff still means "what this lua unlocks", but the
/// Builds page needs them to keep showing a switched-off depot (otherwise disabling one would make its
/// row vanish from "In lua", taking the switch with it).
/// </para>
/// </summary>
public record LuaContents(
    long BaseAppId,
    IReadOnlyList<LuaEntry> Entries,
    IReadOnlyDictionary<long, string> ActivePins,
    IReadOnlyDictionary<long, string> CommentedPins,
    IReadOnlyList<LuaEntry> DisabledEntries)
{
    public int DepotCount => Entries.Count(e => e.HasKey);
    public int DlcCount => Entries.Count(e => !e.HasKey && e.Id != BaseAppId);

    /// <summary>True when this lua pins at least one depot to a fixed manifest, i.e. it's locked to a
    /// specific build rather than tracking whatever Steam ships.</summary>
    public bool HasActivePins => ActivePins.Count > 0;
}

/// <summary>Difference between an old and new lua: ids the new one adds and ids it removes.</summary>
public record LuaDiff(IReadOnlyList<LuaEntry> Added, IReadOnlyList<LuaEntry> Removed)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0;
}

/// <summary>
/// Reads the addappid()/setManifestid() lines from a stplug-in lua file so the detail view can
/// show what a game actually unlocks (base app, DLC ids, depot ids + whether a key is present).
/// </summary>
public static partial class LuaFileParser
{
    // Matched against ONE line at a time (see Parse), so \s can't cross newlines and collapse
    // multiple addappid lines into a single match.
    // addappid(123)                    -> id only
    // addappid(123, 1, "key")  -- Name -> id + key + optional trailing comment
    [GeneratedRegex(@"addappid\s*\(\s*(\d+)\s*(?:,\s*\d+\s*(?:,\s*""([^""]*)"")?)?\s*\)[ \t]*(?:--[ \t]*(.*?)[ \t]*)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AddAppIdRegex();

    // setManifestid(depotid, "manifestid", size) — the third argument is optional and is the depot's
    // size on disk, which is the only size available for a depot Steam's app info never mentions.
    [GeneratedRegex(@"setManifestid\s*\(\s*(\d+)\s*,\s*""(\d+)""\s*(?:,\s*(\d+))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex SetManifestRegex();

    // Strips a trailing "(123456) ..." / "デポ" tail that Hubcap appends to depot comments.
    [GeneratedRegex(@"\s*\(\d+\)\s*\S*\s*$")]
    private static partial Regex CommentTailRegex();

    /// <summary>Tidy a raw lua trailing comment into a display name (drops Hubcap's "(id) デポ" tail).</summary>
    private static string? CleanComment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        // Reject commented-out code (e.g. "setManifestid(...)", "addappid(...)"), not a human name.
        if (s.Contains("setManifestid", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("addappid", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("addtoken", StringComparison.OrdinalIgnoreCase))
            return null;
        s = CommentTailRegex().Replace(s, "").Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static LuaContents? Parse(string filePath, long appIdFromName)
    {
        try
        {
            string text = File.ReadAllText(filePath);

            // An id can appear twice: addappid(id) then addappid(id, 1, "key"). Merge so a key from
            // ANY occurrence counts (keeping first-seen order), otherwise the keyless line wins and
            // we'd wrongly report the depot/DLC as "not in lua".
            var order = new List<long>();
            var keyById = new Dictionary<long, string?>();
            var commentById = new Dictionary<long, string>();
            var manifests = new Dictionary<long, string>();          // active pins
            var sizes = new Dictionary<long, long>();                // setManifestid's 3rd arg
            var commentedManifests = new Dictionary<long, string>(); // pins disabled by Auto Update
            var disabledOrder = new List<long>();                    // addappid lines commented out
            var disabledKeyById = new Dictionary<long, string?>();
            // Match one line at a time: a single-line regex can't span lines, so each addappid stays
            // distinct (a multiline regex with \s* was collapsing the whole file into one match).
            foreach (string rawLine in text.Split('\n'))
            {
                // Trim both ends: a trailing '\r' (from CRLF files) would break the $-anchored regex.
                string line = rawLine.Trim();
                bool commented = line.StartsWith("--");

                // Manifest pins are collected from BOTH commented and uncommented lines, into separate
                // buckets. Scanning the whole file text at once (as this used to) can't tell them apart,
                // so a lua whose pins were commented out by "Auto Update Apps" still looked pinned.
                var pin = SetManifestRegex().Match(line);
                if (pin.Success && long.TryParse(pin.Groups[1].Value, out long depot))
                {
                    (commented ? commentedManifests : manifests)[depot] = pin.Groups[2].Value;

                    // Taken from a commented pin too: the line is disabled as a version LOCK, but the
                    // size it records is still this depot's size and is otherwise unobtainable offline.
                    if (pin.Groups[3].Success && long.TryParse(pin.Groups[3].Value, out long sz) && sz > 0)
                        sizes.TryAdd(depot, sz);
                }

                // A commented-out addappid is a DISABLED declaration, not an active one. Matched against
                // the line with its "--" stripped so it can still be recognised and reported separately.
                var m = AddAppIdRegex().Match(commented ? line.TrimStart('-', ' ') : line);
                if (!m.Success || !long.TryParse(m.Groups[1].Value, out long id)) continue;

                string? key = m.Groups[2].Success && !string.IsNullOrEmpty(m.Groups[2].Value)
                    ? m.Groups[2].Value
                    : null;

                // Keep the best (longest) trailing comment seen for this id. It's the human name
                // ('addappid(2784471, …) -- Depot 2784471'). Captured for commented-out lines too, and
                // BEFORE the disabled branch below returns: switching a depot off must not also strip its
                // name off the row, leaving a bare "Depot".
                string? comment = CleanComment(m.Groups[3].Success ? m.Groups[3].Value : null);
                if (comment is not null &&
                    (!commentById.TryGetValue(id, out var prev) || comment.Length > prev.Length))
                    commentById[id] = comment;

                // Merge keeps the first key seen for an id: a keyed line must never be overwritten by a
                // later bare addappid(id) for the same depot (same reasoning as the old bool OR-merge).
                if (commented)
                {
                    if (disabledKeyById.TryGetValue(id, out var hadKey)) disabledKeyById[id] = hadKey ?? key;
                    else { disabledKeyById[id] = key; disabledOrder.Add(id); }
                    continue;
                }
                if (keyById.TryGetValue(id, out var existing))
                    keyById[id] = existing ?? key;
                else { keyById[id] = key; order.Add(id); }
            }

            var entries = order
                .Select(id => new LuaEntry(id, keyById[id],
                    manifests.TryGetValue(id, out var mid) ? mid : null,
                    commentedManifests.TryGetValue(id, out var cmid) ? cmid : null,
                    commentById.TryGetValue(id, out var c) ? c : null)
                { SizeOnDisk = sizes.TryGetValue(id, out long sz) ? sz : null })
                .ToList();

            // Ids that are ONLY commented out. An id with both an active and a commented line is active
            // (the merge above already counted it), so it must not also be reported as disabled.
            var disabled = disabledOrder
                .Where(id => !keyById.ContainsKey(id))
                .Select(id => new LuaEntry(id, disabledKeyById[id],
                    manifests.TryGetValue(id, out var dmid) ? dmid : null,
                    commentedManifests.TryGetValue(id, out var dcmid) ? dcmid : null,
                    commentById.TryGetValue(id, out var dc) ? dc : null)
                { SizeOnDisk = sizes.TryGetValue(id, out long dsz) ? dsz : null })
                .ToList();

            // The base app is the first addappid id (matches how the files are generated).
            long baseId = entries.Count > 0 ? entries[0].Id : appIdFromName;
            return new LuaContents(baseId, entries, manifests, commentedManifests, disabled);
        }
        catch
        {
            return null; // unreadable / malformed. Caller shows "couldn't read"
        }
    }

    /// <summary>Diff old vs new lua by entry id: what the new lua adds and what it removes.</summary>
    public static LuaDiff Diff(LuaContents? oldLua, LuaContents newLua)
    {
        var oldIds = oldLua?.Entries.Select(e => e.Id).ToHashSet() ?? [];
        var newIds = newLua.Entries.Select(e => e.Id).ToHashSet();

        var added = newLua.Entries.Where(e => !oldIds.Contains(e.Id)).ToList();
        var removed = oldLua?.Entries.Where(e => !newIds.Contains(e.Id)).ToList() ?? [];
        return new LuaDiff(added, removed);
    }
}
