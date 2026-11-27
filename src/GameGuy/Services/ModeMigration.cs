using GameGuy.Models;

namespace GameGuy.Services;

/// <summary>
/// Rewrites a pre-3-mode <c>SelectedMode</c> value into the current <see cref="UnlockerMode"/> set.
///
/// The old app had four modes: SteamTools, OpenSteamTools (upstream OST, but *displayed* as
/// "BetterSteamTools"), OpenSteamToolsNightly (displayed "BetterSteamTools Nightly") and a
/// CloudRedirect CLI fix. The two BetterSteamTools-branded ones become <see cref="UnlockerMode.Bst"/>.
/// That's the name those users chose and it's still the name of what they get. SteamTools and
/// CloudRedirect are gone with no successor, so those users are reset to "no mode" and re-onboarded.
///
/// <para><b>Why this is safe to run on every launch.</b> None of the legacy strings parse as a current
/// enum member, so "unparseable" is an exact synonym for "written by an older build". A user already
/// on Ost/Bst/Custom parses fine and is left completely alone, which is why no persisted
/// has-migrated marker is needed. This property is the entire reason the enum members are named
/// Ost/Bst/Custom: had <c>OpenSteamTools</c> been reused for the nightly channel, every legacy
/// stable-OST user would have silently parsed into the wrong mode, and the migration would have had
/// no way to tell those users apart from legitimate post-migration ones.</para>
/// </summary>
public static class ModeMigration
{
    /// <summary>
    /// Apply the migration to persisted settings. Returns true if onboarding must be re-shown.
    /// </summary>
    public static bool Apply(SettingsService settings)
    {
        var (newMode, resetOnboarding) = Migrate(settings.SelectedMode);
        if (newMode != settings.SelectedMode) settings.SelectedMode = newMode;
        return resetOnboarding;
    }

    /// <summary>
    /// The migration itself, as a pure function of the stored string so it can be tested without
    /// touching the real settings file.
    /// </summary>
    /// <param name="stored">Raw <c>SelectedMode</c> as found on disk.</param>
    /// <returns>
    /// The value to store (null = no mode selected), and whether onboarding must be re-shown. True
    /// only when the user WAS on a mode that has since been retired, so they now have none.
    /// </returns>
    public static (string? Mode, bool ResetOnboarding) Migrate(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return (null, false);   // fresh install; onboarding handles it
        if (Enum.TryParse<UnlockerMode>(stored, out var current))
            return (current.ToString(), false);                        // already current. Leave it be

        return stored switch
        {
            // Both were branded "BetterSteamTools" in the UI, so BST is what these users think they run.
            "OpenSteamTools" or "OpenSteamToolsNightly" => (UnlockerMode.Bst.ToString(), false),

            // SteamTools and the CloudRedirect fix are retired with nothing to map onto. Clear the mode
            // and send them back through onboarding to choose deliberately. Anything else unrecognised
            // (hand-edited, or from a build we don't know) lands here too. Safer than guessing.
            _ => (null, true),
        };
    }
}
