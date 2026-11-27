using GameGuy.Services;
using GameGuy.ViewModels;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// Which row the build switcher treats as live. This shipped broken: mid-edit the live lua matches no
/// stored variant, nothing was active, and the selection fell through to whatever sorted first. Moving
/// the user off the build they were editing and routing their next depot toggle into the Default's
/// stored copy instead of the live file.
/// </summary>
public class BuildsActiveHashTests
{
    private static LuaVariant Variant(string hash, string kind = LuaVariantKind.Build) =>
        new(hash, kind, null, null, DateTime.UtcNow, null, new Dictionary<string, string>(), 0, 0);

    private static readonly LuaVariant Build = Variant("aaa");
    private static readonly LuaVariant Default = Variant("bbb", LuaVariantKind.Default);
    private static readonly IReadOnlyList<LuaVariant> Stored = [Build, Default];

    [Fact]
    public void LiveMatchingAStoredVariant_IsThatVariant() =>
        Assert.Equal("aaa", BuildsViewModel.ResolveActiveHash("aaa", Stored, editBase: null));

    /// <summary>The regression: an edit in progress must keep its own row active.</summary>
    [Fact]
    public void LiveMatchingNothing_ResolvesToTheVariantBeingEdited() =>
        Assert.Equal("aaa", BuildsViewModel.ResolveActiveHash("edited", Stored, editBase: "aaa"));

    /// <summary>No base to fall back on, no row matches, which is the honest answer. Must not throw.</summary>
    [Fact]
    public void LiveMatchingNothing_WithNoEditBase_LeavesNothingActive()
    {
        string? resolved = BuildsViewModel.ResolveActiveHash("edited", Stored, editBase: null);

        Assert.Equal("edited", resolved);
        Assert.DoesNotContain(Stored, v => v.Hash == resolved);
    }

    /// <summary>A base pointing at a variant that's since been deleted is stale, not a selection.</summary>
    [Fact]
    public void LiveMatchingNothing_WithAStaleEditBase_LeavesNothingActive() =>
        Assert.Equal("edited", BuildsViewModel.ResolveActiveHash("edited", Stored, editBase: "gone"));

    [Fact]
    public void NoLiveFile_IsNull() =>
        Assert.Null(BuildsViewModel.ResolveActiveHash(null, Stored, editBase: "aaa"));

    /// <summary>An edit base left over from a finished edit must not override an exact match.</summary>
    [Fact]
    public void AStaleEditBase_DoesNotOverrideAnExactLiveMatch() =>
        Assert.Equal("bbb", BuildsViewModel.ResolveActiveHash("bbb", Stored, editBase: "aaa"));
}
