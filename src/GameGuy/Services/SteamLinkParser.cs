using System.Text.RegularExpressions;

namespace GameGuy.Services;

/// <summary>
/// Pulls a Steam appid out of a link: what makes dragging a SteamDB/store URL onto the drop box install
/// that game.
/// </summary>
public static partial class SteamLinkParser
{
    /// <summary>
    /// Appids are well under this. <c>steam://rungameid/</c> encodes a COMPOSITE 64-bit id for non-Steam
    /// shortcuts and mods, which is not an appid. Without this guard one of those would be handed to the
    /// installer as a game id.
    /// </summary>
    private const long MaxAppId = 2_000_000_000;

    // Every pattern requires the id to sit in an "/app/" (or steam:// verb) slot. That's load-bearing:
    // steamdb.info/depot/<id> and /sub/<id> are a DEPOT and a PACKAGE id, and installing either as an
    // appid would quietly fetch the wrong game.
    [GeneratedRegex(@"steamdb\.info/app/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SteamDbRegex();

    [GeneratedRegex(@"store\.steampowered\.com/(?:agecheck/)?app/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StoreRegex();

    [GeneratedRegex(@"steamcommunity\.com/app/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CommunityRegex();

    [GeneratedRegex(@"steam://(?:store|install|run|rungameid)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SteamProtocolRegex();

    /// <summary>
    /// The appid a dragged or pasted link refers to, or null when there isn't one.
    ///
    /// <para>
    /// A BARE number is deliberately not accepted: dragged text is often incidental, and turning any
    /// stray digits into an install is too easy to do by accident. The link has to name an app.
    /// </para>
    /// </summary>
    public static long? AppIdFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var regex in new[] { SteamDbRegex(), StoreRegex(), CommunityRegex(), SteamProtocolRegex() })
        {
            // Dragged text can carry more than one URL (a selection, a multi-line paste). First wins.
            var match = regex.Match(text);
            if (match.Success && long.TryParse(match.Groups[1].Value, out long appId)
                              && appId > 0 && appId < MaxAppId)
                return appId;
        }
        return null;
    }
}
