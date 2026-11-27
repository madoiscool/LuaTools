using System.IO;
using GameGuy.Services;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// Tests for <see cref="LuaVault"/>, which rewrites the file Steam actually loads, so its failure mode
/// is "the user's luas are gone/wrong", not a visible error. Runs entirely against temp directories via
/// the internal test constructor.
/// </summary>
public class LuaVaultTests : IDisposable
{
    private const long AppId = 386940;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"luavaulttest_{Guid.NewGuid():N}");
    private readonly string _plugIn;
    private readonly LuaVault _vault;

    // A lua whose pins are COMMENTED OUT: what "Auto Update Apps" (on by default) produces.
    private const string AutoUpdatingLua = """
        addappid(386940)
        addappid(228983,0,"aabb")
        --setManifestid(228983,"111111111")
        """;

    // The same game PINNED to a build: pins live.
    private const string PinnedBuildLua = """
        addappid(386940)
        addappid(228983,0,"aabb")
        setManifestid(228983,"222222222")
        """;

    public LuaVaultTests()
    {
        _plugIn = Path.Combine(_tmp, "stplug-in");
        Directory.CreateDirectory(_plugIn);
        _vault = new LuaVault(() => _plugIn, Path.Combine(_tmp, "luavault"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string LivePath => Path.Combine(_plugIn, $"{AppId}.lua");
    private void WriteLive(string text) => File.WriteAllText(LivePath, text);
    private string ReadLive() => File.ReadAllText(LivePath);

    // ── Upgrade path ───────────────────────────────────────────────────────────────────────────────

    /// <summary>An existing user's plain lua must be preserved before anything else touches it.</summary>
    [Fact]
    public void SyncDefaultFromLive_AdoptsAPreExistingLua()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);

        var variant = Assert.Single(_vault.GetVariants(AppId));
        Assert.Equal(LuaVariantKind.Default, variant.Kind);
        Assert.Null(variant.BuildId);
        // Its pins are commented out, so it must NOT claim to be pinned.
        Assert.False(variant.IsPinned);
        Assert.Equal(variant.Hash, _vault.GetActiveHash(AppId));
    }

    [Fact]
    public void SyncDefaultFromLive_IsIdempotent()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        _vault.SyncDefaultFromLive(AppId);

        Assert.Single(_vault.GetVariants(AppId));
    }

    [Fact]
    public void SyncDefaultFromLive_NoOpsWithNoLiveFile()
    {
        _vault.SyncDefaultFromLive(AppId);
        Assert.Empty(_vault.GetVariants(AppId));
    }

    // ── Loose build luas ───────────────────────────────────────────────────────────────────────────

    /// <summary>&lt;appid&gt;_&lt;buildid&gt;.lua files are inert in stplug-in (Steam reads &lt;appid&gt;.lua only),
    /// so the vault has to pick them up or the user's own build files do nothing.</summary>
    [Fact]
    public void AdoptLooseBuildLuas_PicksUpBuildNamedFiles()
    {
        File.WriteAllText(Path.Combine(_plugIn, $"{AppId}_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);

        var variant = Assert.Single(_vault.GetVariants(AppId));
        Assert.Equal(LuaVariantKind.Build, variant.Kind);
        Assert.Equal("18234567", variant.BuildId);
        Assert.True(variant.IsPinned);
        Assert.Equal("222222222", variant.ManifestIds["228983"]);
    }

    [Fact]
    public void AdoptLooseBuildLuas_IgnoresOtherApps()
    {
        File.WriteAllText(Path.Combine(_plugIn, "999999_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);

        Assert.Empty(_vault.GetVariants(AppId));
    }

    /// <summary>A build lua dropped in with no &lt;appid&gt;.lua alongside it loads nothing until applied.
    /// So it gets applied, and becomes the active variant.</summary>
    [Fact]
    public void ApplyBuildIfNothingLive_ActivatesADroppedInBuild()
    {
        File.WriteAllText(Path.Combine(_plugIn, $"{AppId}_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);
        Assert.False(File.Exists(LivePath));

        var applied = _vault.ApplyBuildIfNothingLive(AppId);

        Assert.NotNull(applied);
        Assert.Equal("18234567", applied!.BuildId);
        Assert.True(File.Exists(LivePath));
        Assert.Equal(PinnedBuildLua, ReadLive());
        Assert.Equal(applied.Hash, _vault.GetActiveVariant(AppId)!.Hash);
    }

    /// <summary>The other half of that rule: never silently swap a version the user is already running.</summary>
    [Fact]
    public void ApplyBuildIfNothingLive_LeavesAnExistingLiveLuaAlone()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        File.WriteAllText(Path.Combine(_plugIn, $"{AppId}_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);

        Assert.Null(_vault.ApplyBuildIfNothingLive(AppId));
        Assert.Equal(AutoUpdatingLua, ReadLive()); // untouched
    }

    [Fact]
    public void ApplyBuildIfNothingLive_NoOpsWithNoBuilds()
    {
        Assert.Null(_vault.ApplyBuildIfNothingLive(AppId));
        Assert.False(File.Exists(LivePath));
    }

    // ── HasVariants (the cheap "is this game tracked at all" probe the badge relies on) ─────────────

    /// <summary>
    /// A game with nothing captured is UNTRACKED, not "unsaved". Conflating the two labelled every game
    /// the user hadn't opened yet as edited.
    /// </summary>
    [Fact]
    public void HasVariants_IsFalseForAnUntrackedGameEvenWithALiveLua()
    {
        WriteLive(AutoUpdatingLua);

        Assert.False(_vault.HasVariants(AppId));
        Assert.NotNull(_vault.GetActiveHash(AppId));  // a live lua exists…
        Assert.Null(_vault.GetActiveVariant(AppId));  // …but nothing is stored, so nothing matches

        _vault.SyncDefaultFromLive(AppId);
        Assert.True(_vault.HasVariants(AppId));
    }

    // ── Applying a build ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE core guarantee: applying a build copies the bytes verbatim. If Apply ever went through
    /// LuaInstaller's "Auto Update Apps" transform, it would comment the pins out and silently un-pin the
    /// exact build the user just selected.
    /// </summary>
    [Fact]
    public void Apply_WritesTheStoredBytesVerbatimAndKeepsPins()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        File.WriteAllText(Path.Combine(_plugIn, $"{AppId}_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);

        var build = _vault.GetVariants(AppId).Single(v => v.BuildId == "18234567");
        Assert.True(_vault.Apply(AppId, build));

        Assert.Equal(PinnedBuildLua, ReadLive());
        Assert.Equal(build.Hash, _vault.GetActiveHash(AppId));
        Assert.Equal(build.Hash, _vault.GetActiveVariant(AppId)!.Hash);
        // The pin survived, not commented out.
        Assert.Contains("\nsetManifestid(228983,\"222222222\")", ReadLive().Replace("\r\n", "\n"));
    }

    [Fact]
    public void Apply_CanSwitchBackToDefault()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        File.WriteAllText(Path.Combine(_plugIn, $"{AppId}_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);

        var build = _vault.GetVariants(AppId).Single(v => v.BuildId == "18234567");
        var deflt = _vault.GetVariants(AppId).Single(v => v.Kind == LuaVariantKind.Default);

        _vault.Apply(AppId, build);
        _vault.Apply(AppId, deflt);

        Assert.Equal(AutoUpdatingLua, ReadLive());
        Assert.Equal(deflt.Hash, _vault.GetActiveVariant(AppId)!.Hash);
    }

    // ── Content addressing ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capture_DedupesIdenticalBytes()
    {
        string a = Path.Combine(_tmp, "a.lua");
        string b = Path.Combine(_tmp, "b.lua");
        File.WriteAllText(a, PinnedBuildLua);
        File.WriteAllText(b, PinnedBuildLua);

        _vault.Capture(AppId, a, LuaVariantKind.Preset);
        _vault.Capture(AppId, b, LuaVariantKind.Preset);

        Assert.Single(_vault.GetVariants(AppId));
    }

    /// <summary>A lua first seen as the plain live file, then downloaded under its build name, should
    /// gain the build identity rather than becoming a second identical entry.</summary>
    [Fact]
    public void Capture_BackfillsBuildIdOntoAlreadyStoredBytes()
    {
        WriteLive(PinnedBuildLua);
        _vault.SyncDefaultFromLive(AppId);
        Assert.Null(Assert.Single(_vault.GetVariants(AppId)).BuildId);

        string download = Path.Combine(_tmp, $"{AppId}_18234567.lua");
        File.WriteAllText(download, PinnedBuildLua);
        _vault.Capture(AppId, download, LuaVariantKind.Build, "18234567", "Ryuu");

        var variant = Assert.Single(_vault.GetVariants(AppId));
        Assert.Equal("18234567", variant.BuildId);
        Assert.Equal(LuaVariantKind.Build, variant.Kind);
    }

    // ── External edits ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A lua edited outside the app is unrecognised until the next sync, and the sync adopts it as the
    /// Default rather than leaving it permanently unaccounted for.
    /// </summary>
    [Fact]
    public void ExternalEdit_IsUnmatchedUntilSynced_ThenBecomesTheDefault()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);

        WriteLive(AutoUpdatingLua + "\n-- hand edited");

        string? active = _vault.GetActiveHash(AppId);
        Assert.NotNull(active);
        Assert.Null(_vault.GetActiveVariant(AppId));   // not yet seen

        _vault.SyncDefaultFromLive(AppId);

        var now = _vault.GetActiveVariant(AppId);
        Assert.NotNull(now);
        Assert.Equal(LuaVariantKind.Default, now!.Kind);
        Assert.Equal(active, now.Hash);
        Assert.Single(_vault.GetVariants(AppId));      // it REPLACED the old default, not joined it
    }

    // ── The Default is one slot ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The bug this replaced: every install of a plain lua used to append another "default" variant, so
    /// re-adding a game from a different generator (Luie vs Hubcap. Same game, different bytes, so the
    /// content-addressed de-dupe couldn't collapse them) left two identical-looking rows in the switcher.
    /// </summary>
    [Fact]
    public void SyncDefaultFromLive_ReplacesTheDefaultInsteadOfAppendingASecondOne()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        string firstHash = _vault.GetVariants(AppId).Single().Hash;

        // Same game, re-added from a different generator.
        string second = "-- generated by something else\n" + AutoUpdatingLua;
        WriteLive(second);
        _vault.SyncDefaultFromLive(AppId);

        var variants = _vault.GetVariants(AppId);
        Assert.Single(variants);
        Assert.Equal(LuaVariantKind.Default, variants[0].Kind);
        Assert.Equal(LuaVault.HashText(second), variants[0].Hash);

        // Replace outright: the outgoing bytes are gone from disk, not merely de-indexed.
        Assert.False(File.Exists(Path.Combine(_tmp, "luavault", AppId.ToString(), firstHash + ".lua")));
    }

    /// <summary>Migration: an index written by the old append-on-install behaviour heals on the next sync,
    /// keeping the copy Steam is actually running.</summary>
    [Fact]
    public void SyncDefaultFromLive_CollapsesTwoDefaultsKeepingTheLiveOne()
    {
        // Two defaults, the SECOND of which is live, so "keep the newest" alone would drop the wrong one.
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        string olderLive = _vault.GetVariants(AppId).Single().Hash;

        WriteLive(PinnedBuildLua);
        _vault.Capture(AppId, LivePath, LuaVariantKind.Default);   // the old append behaviour
        WriteLive(AutoUpdatingLua);                                 // …and the first one is what's live
        Assert.Equal(2, _vault.GetVariants(AppId).Count);

        _vault.SyncDefaultFromLive(AppId);

        var kept = Assert.Single(_vault.GetVariants(AppId));
        Assert.Equal(olderLive, kept.Hash);
        Assert.Equal(olderLive, _vault.GetActiveHash(AppId));
    }

    /// <summary>A saved build being live means the build is active. The Default keeps its own bytes so
    /// switching back to it still works.</summary>
    [Fact]
    public void SyncDefaultFromLive_LeavesTheDefaultAloneWhileASavedBuildIsLive()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        string defaultHash = _vault.GetVariants(AppId).Single().Hash;

        string buildPath = Path.Combine(_tmp, $"{AppId}_777.lua");
        File.WriteAllText(buildPath, PinnedBuildLua);
        var build = _vault.Capture(AppId, buildPath, LuaVariantKind.Build, "777")!;
        Assert.True(_vault.Apply(AppId, build));

        _vault.SyncDefaultFromLive(AppId);

        Assert.Equal(2, _vault.GetVariants(AppId).Count);
        Assert.Equal(build.Hash, _vault.GetActiveHash(AppId));
        Assert.Contains(_vault.GetVariants(AppId), v => v.Hash == defaultHash);
    }

    /// <summary>
    /// Mid-edit, the diverged bytes belong to the build being edited, not to the Default. Without this
    /// the sync would swallow the edit and "Save to &lt;build&gt;" would have nothing to write back to.
    /// </summary>
    [Fact]
    public void SyncDefaultFromLive_LeavesThePendingEditAlone()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        string defaultHash = _vault.GetVariants(AppId).Single().Hash;

        string buildPath = Path.Combine(_tmp, $"{AppId}_777.lua");
        File.WriteAllText(buildPath, PinnedBuildLua);
        var build = _vault.Capture(AppId, buildPath, LuaVariantKind.Build, "777")!;
        _vault.Apply(AppId, build);

        _vault.SetEditBase(AppId, build.Hash);                 // an edit of the build starts…
        _vault.WriteLive(AppId, PinnedBuildLua + "\n-- tweak"); // …and diverges

        _vault.SyncDefaultFromLive(AppId);

        Assert.Equal(2, _vault.GetVariants(AppId).Count);                    // no third row
        Assert.Contains(_vault.GetVariants(AppId), v => v.Hash == defaultHash);
        Assert.Equal(build.Hash, _vault.GetEditBase(AppId));                 // save target intact
    }

    /// <summary>
    /// Switching builds overwrites the live file. If what was sitting there was an unsaved working copy,
    /// <see cref="LuaVault.Apply"/> capturing it first is the only thing standing between the user and
    /// losing it. This must not depend on the Builds page having refreshed beforehand.
    /// </summary>
    [Fact]
    public void Apply_CapturesUnsavedLiveBytesBeforeOverwritingThem()
    {
        string buildPath = Path.Combine(_tmp, $"{AppId}_777.lua");
        File.WriteAllText(buildPath, PinnedBuildLua);
        var build = _vault.Capture(AppId, buildPath, LuaVariantKind.Build, "777")!;

        // Never synced: bytes are live and unaccounted for.
        string working = AutoUpdatingLua + "\n-- my working copy";
        WriteLive(working);
        Assert.Null(_vault.GetActiveVariant(AppId));

        Assert.True(_vault.Apply(AppId, build));

        var stored = _vault.GetVariants(AppId).Single(v => v.Kind == LuaVariantKind.Default);
        Assert.Equal(LuaVault.HashText(working), stored.Hash);

        // …and switching back returns them byte-exact.
        Assert.True(_vault.Apply(AppId, stored));
        Assert.Equal(working, ReadLive());
    }

    [Fact]
    public void SaveText_AdoptsTheEditedLiveFileAsAPreset()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        string edited = AutoUpdatingLua + "\n-- hand edited";
        WriteLive(edited);

        var preset = _vault.SaveText(AppId, edited, "my mix");
        Assert.NotNull(preset);
        Assert.Equal("my mix", preset!.Label);
        Assert.Equal("my mix", preset.DisplayLabel);
        // Now the live file is recognised again.
        Assert.Equal(preset.Hash, _vault.GetActiveVariant(AppId)!.Hash);
    }

    [Fact]
    public void WriteLive_UpdatesTheFileSteamReads()
    {
        WriteLive(AutoUpdatingLua);
        Assert.True(_vault.WriteLive(AppId, PinnedBuildLua));
        Assert.Equal(PinnedBuildLua, ReadLive());
    }

    // ── In-place save (overwrite the build you're editing) ─────────────────────────────────────────

    /// <summary>
    /// Saving an edit back into the build it came from keeps that build's NAME and build id, even though
    /// the stored file is content-addressed and therefore renamed underneath.
    /// </summary>
    [Fact]
    public void UpdateVariant_KeepsIdentityAndReplacesTheBytes()
    {
        File.WriteAllText(Path.Combine(_plugIn, $"{AppId}_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);
        var build = _vault.GetVariants(AppId).Single();
        _vault.Rename(AppId, build.Hash, "pre-nerf");

        string edited = LuaEditor.SetDepotEnabled(PinnedBuildLua, 228983, enabled: false);
        var updated = _vault.UpdateVariant(AppId, build.Hash, edited);

        Assert.NotNull(updated);
        Assert.NotEqual(build.Hash, updated!.Hash);          // new bytes → new address
        Assert.Equal("18234567", updated.BuildId);           // …but same identity
        Assert.Equal("pre-nerf", updated.Label);
        Assert.Equal(LuaVariantKind.Build, updated.Kind);

        // Still exactly one variant: an update, not a fork.
        var only = Assert.Single(_vault.GetVariants(AppId));
        Assert.Equal(updated.Hash, only.Hash);
        Assert.Equal(edited, _vault.ReadText(AppId, updated.Hash));
    }

    [Fact]
    public void UpdateVariant_RemovesTheSupersededFile()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        var original = _vault.GetVariants(AppId).Single();

        var updated = _vault.UpdateVariant(AppId, original.Hash, AutoUpdatingLua + "\n-- tweak");

        Assert.NotNull(updated);
        Assert.False(File.Exists(Path.Combine(_tmp, "luavault", AppId.ToString(), original.Hash + ".lua")));
        Assert.True(File.Exists(Path.Combine(_tmp, "luavault", AppId.ToString(), updated!.Hash + ".lua")));
    }

    [Fact]
    public void UpdateVariant_ReturnsNullWhenTheBaseIsGone()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);

        Assert.Null(_vault.UpdateVariant(AppId, "0000000000000000000000000000000000000000000000000000000000000000", "x"));
    }

    /// <summary>Editing a variant into bytes another variant already has must not leave two entries
    /// claiming the same file.</summary>
    [Fact]
    public void UpdateVariant_MergesWhenTheEditMatchesAnExistingVariant()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        File.WriteAllText(Path.Combine(_plugIn, $"{AppId}_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);
        Assert.Equal(2, _vault.GetVariants(AppId).Count);

        var deflt = _vault.GetVariants(AppId).Single(v => v.Kind == LuaVariantKind.Default);
        // Edit Default until it is byte-for-byte the build.
        var updated = _vault.UpdateVariant(AppId, deflt.Hash, PinnedBuildLua);

        Assert.NotNull(updated);
        var only = Assert.Single(_vault.GetVariants(AppId));
        Assert.Equal("18234567", only.BuildId);
    }

    // ── Edit base tracking (what an in-place save overwrites) ──────────────────────────────────────

    [Fact]
    public void EditBase_PersistsAcrossVaultInstances()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        string hash = _vault.GetVariants(AppId).Single().Hash;

        _vault.SetEditBase(AppId, hash);

        // Reopened: Save must still know what it's overwriting, or it silently becomes "save as new".
        var reopened = new LuaVault(() => _plugIn, Path.Combine(_tmp, "luavault"));
        Assert.Equal(hash, reopened.GetEditBase(AppId));
    }

    [Fact]
    public void EditBase_IsClearedByApplyAndByUpdate()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        var deflt = _vault.GetVariants(AppId).Single();

        _vault.SetEditBase(AppId, deflt.Hash);
        _vault.UpdateVariant(AppId, deflt.Hash, AutoUpdatingLua + "\n-- tweak");
        Assert.Null(_vault.GetEditBase(AppId)); // live matches the updated variant again

        var current = _vault.GetVariants(AppId).Single();
        _vault.SetEditBase(AppId, current.Hash);
        _vault.Apply(AppId, current);
        Assert.Null(_vault.GetEditBase(AppId));
    }

    [Fact]
    public void EditBase_IsClearedBySavingANewPreset()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        _vault.SetEditBase(AppId, _vault.GetVariants(AppId).Single().Hash);

        _vault.SaveText(AppId, AutoUpdatingLua + "\n-- forked", "my mix");

        Assert.Null(_vault.GetEditBase(AppId));
        Assert.Equal(2, _vault.GetVariants(AppId).Count); // base kept, fork added
    }

    // ── Rename / delete ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_SetsTheLabelWithoutLosingTheBuildId()
    {
        File.WriteAllText(Path.Combine(_plugIn, $"{AppId}_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);
        var variant = Assert.Single(_vault.GetVariants(AppId));
        Assert.Equal("Build 18234567", variant.DisplayLabel);

        _vault.Rename(AppId, variant.Hash, "pre-nerf patch");

        var renamed = Assert.Single(_vault.GetVariants(AppId));
        Assert.Equal("pre-nerf patch", renamed.DisplayLabel);
        Assert.Equal("18234567", renamed.BuildId); // identity survives the rename
    }

    [Fact]
    public void Rename_ToBlankRestoresTheGeneratedLabel()
    {
        File.WriteAllText(Path.Combine(_plugIn, $"{AppId}_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);
        string hash = _vault.GetVariants(AppId).Single().Hash;

        _vault.Rename(AppId, hash, "temp");
        _vault.Rename(AppId, hash, "   ");

        Assert.Equal("Build 18234567", _vault.GetVariants(AppId).Single().DisplayLabel);
    }

    /// <summary>Deleting what Steam is currently running would orphan the live lua with no way back.</summary>
    [Fact]
    public void Delete_RefusesTheActiveVariant()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        string activeHash = _vault.GetActiveHash(AppId)!;

        Assert.False(_vault.Delete(AppId, activeHash));
        Assert.Single(_vault.GetVariants(AppId));
    }

    [Fact]
    public void Delete_RemovesAnInactiveVariant()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        File.WriteAllText(Path.Combine(_plugIn, $"{AppId}_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);

        var build = _vault.GetVariants(AppId).Single(v => v.BuildId == "18234567");
        Assert.True(_vault.Delete(AppId, build.Hash));

        var left = Assert.Single(_vault.GetVariants(AppId));
        Assert.Equal(LuaVariantKind.Default, left.Kind);
    }

    // ── Persistence ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Variants_SurviveAcrossVaultInstances()
    {
        WriteLive(AutoUpdatingLua);
        _vault.SyncDefaultFromLive(AppId);
        File.WriteAllText(Path.Combine(_plugIn, $"{AppId}_18234567.lua"), PinnedBuildLua);
        _vault.AdoptLooseBuildLuas(AppId);
        _vault.Rename(AppId, _vault.GetVariants(AppId).Single(v => v.BuildId is not null).Hash, "keeper");

        var reopened = new LuaVault(() => _plugIn, Path.Combine(_tmp, "luavault"));
        var variants = reopened.GetVariants(AppId);

        Assert.Equal(2, variants.Count);
        Assert.Contains(variants, v => v.DisplayLabel == "keeper" && v.BuildId == "18234567");
        Assert.Contains(reopened.AppsWithVariants(), id => id == AppId);
    }
}
    