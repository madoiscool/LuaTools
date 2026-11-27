using GameGuy.Models;
using GameGuy.Services;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// The 4-mode → 3-mode migration. Worth testing because every failure mode here is silent: a wrong
/// mapping doesn't throw, it just puts the user on an unlocker they didn't choose.
/// </summary>
public class ModeMigrationTests
{
    // ── Legacy values ────────────────────────────────────────────────

    [Theory]
    // Upstream OST, but the UI called it "BetterSteamTools", so BST is what these users believe
    // they picked, and BST is what they should keep having.
    [InlineData("OpenSteamTools")]
    [InlineData("OpenSteamToolsNightly")]
    public void BetterSteamToolsBrandedModes_BecomeBst_WithoutReonboarding(string stored)
    {
        var (mode, reset) = ModeMigration.Migrate(stored);

        Assert.Equal(nameof(UnlockerMode.Bst), mode);
        Assert.False(reset); // they still have a mode, so don't nag them
    }

    [Theory]
    [InlineData("SteamTools")]     // retired outright
    [InlineData("CloudRedirect")]  // the SteamTools fix; retired with it
    public void RetiredModes_AreClearedAndTriggerOnboarding(string stored)
    {
        var (mode, reset) = ModeMigration.Migrate(stored);

        Assert.Null(mode);
        Assert.True(reset);
    }

    // ── Current values are left alone ────────────────────────────────

    [Theory]
    [InlineData(UnlockerMode.Ost)]
    [InlineData(UnlockerMode.Bst)]
    [InlineData(UnlockerMode.Custom)]
    public void CurrentModes_AreUntouched(UnlockerMode mode)
    {
        var (result, reset) = ModeMigration.Migrate(mode.ToString());

        Assert.Equal(mode.ToString(), result);
        Assert.False(reset);
    }

    /// <summary>
    /// The property the enum naming exists to guarantee. If a legacy string ever parsed as a current
    /// member, this migration could not tell "written by an old build" from "written by this build",
    /// and a user on Ost would be silently dragged to Bst on every launch.
    /// </summary>
    [Theory]
    [InlineData("SteamTools")]
    [InlineData("OpenSteamTools")]
    [InlineData("OpenSteamToolsNightly")]
    [InlineData("CloudRedirect")]
    public void NoLegacyValueParsesAsACurrentMode(string legacy) =>
        Assert.False(Enum.TryParse<UnlockerMode>(legacy, out _));

    [Theory]
    [InlineData("SteamTools")]
    [InlineData("OpenSteamTools")]
    [InlineData("OpenSteamToolsNightly")]
    [InlineData("CloudRedirect")]
    [InlineData("Ost")]
    [InlineData("Bst")]
    [InlineData("Custom")]
    [InlineData(null)]
    public void MigrationIsIdempotent(string? stored)
    {
        var (once, _) = ModeMigration.Migrate(stored);
        var (twice, resetSecondTime) = ModeMigration.Migrate(once);

        Assert.Equal(once, twice);
        // A second pass must never re-trigger onboarding. That would nag on every single launch.
        Assert.False(resetSecondTime);
    }

    // ── Edge cases ───────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoStoredMode_IsNotTreatedAsARetiredMode(string? stored)
    {
        var (mode, reset) = ModeMigration.Migrate(stored);

        Assert.Null(mode);
        // A fresh install already gets onboarding via OnboardingComplete; this must not claim a
        // migration happened, or it would clear that flag for users who never had a mode at all.
        Assert.False(reset);
    }

    [Fact]
    public void UnrecognisedGarbage_IsClearedRatherThanGuessed()
    {
        var (mode, reset) = ModeMigration.Migrate("SomethingHandEditedByAUser");

        Assert.Null(mode);
        Assert.True(reset);
    }

    /// <summary>Parsing is case-sensitive on purpose. "ost" is not a value we ever write.</summary>
    [Fact]
    public void LowercaseVariants_AreTreatedAsUnknown()
    {
        var (mode, reset) = ModeMigration.Migrate("ost");

        Assert.Null(mode);
        Assert.True(reset);
    }
}
