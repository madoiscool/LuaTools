using System.Globalization;
using GameGuy;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// Tests for <see cref="Program.MatchOsLanguage"/>. The OS-display-language → supported-UI-tag mapping.
/// Regression guard for the bug where a region-qualified OS culture (e.g. Spanish "es-ES") fell through
/// to English instead of mapping to its neutral resource ("es").
/// </summary>
public class LanguageDetectionTests
{
    private static string Match(string osCultureName) =>
        Program.MatchOsLanguage(new CultureInfo(osCultureName));

    // ── The reported bug: region-qualified locales must strip to their neutral resource ──────────────
    [Theory]
    [InlineData("es-ES", "es")]   // the user's case: Spanish (Spain)
    [InlineData("fr-FR", "fr")]
    [InlineData("de-DE", "de")]
    [InlineData("it-IT", "it")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ko-KR", "ko")]
    [InlineData("ru-RU", "ru")]
    [InlineData("pl-PL", "pl")]
    [InlineData("nl-NL", "nl")]
    [InlineData("tr-TR", "tr")]
    [InlineData("uk-UA", "uk")]
    [InlineData("cs-CZ", "cs")]
    [InlineData("hu-HU", "hu")]
    [InlineData("ro-RO", "ro")]
    [InlineData("el-GR", "el")]
    [InlineData("bg-BG", "bg")]
    [InlineData("th-TH", "th")]
    [InlineData("vi-VN", "vi")]
    [InlineData("id-ID", "id")]
    [InlineData("da-DK", "da")]
    [InlineData("fi-FI", "fi")]
    [InlineData("sv-SE", "sv")]
    public void RegionQualified_StripsToNeutral(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    // ── Tags we ship verbatim must match exactly (not get stripped to a parent) ──────────────────────
    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("es-419", "es-419")]
    public void ExactRegionalTags_MatchVerbatim(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    // ── Neutral tags pass through unchanged ──────────────────────────────────────────────────────────
    [Theory]
    [InlineData("en", "en")]
    [InlineData("es", "es")]
    [InlineData("fr", "fr")]
    [InlineData("de", "de")]
    public void NeutralTags_PassThrough(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    // ── Chinese resolves by script, not just region ──────────────────────────────────────────────────
    [Theory]
    [InlineData("zh-CN", "zh-Hans")]   // mainland → Simplified
    [InlineData("zh-SG", "zh-Hans")]   // Singapore → Simplified
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]   // Taiwan → Traditional
    [InlineData("zh-HK", "zh-Hant")]   // Hong Kong → Traditional
    [InlineData("zh-MO", "zh-Hant")]   // Macau → Traditional
    [InlineData("zh-Hant", "zh-Hant")]
    public void Chinese_ResolvesByScript(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    // ── Spanish: Spain → es, Latin America → es-419 ──────────────────────────────────────────────────
    [Theory]
    [InlineData("es-ES", "es")]
    [InlineData("es-MX", "es-419")]
    [InlineData("es-AR", "es-419")]
    [InlineData("es-CO", "es-419")]
    [InlineData("es-CL", "es-419")]
    public void Spanish_SplitsSpainVsLatinAmerica(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    // ── Portuguese: no neutral resource, default to European ─────────────────────────────────────────
    [Theory]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt", "pt-PT")]      // bare neutral → European fallback
    public void Portuguese_DefaultsToEuropean(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    // ── Norwegian: Bokmål / Nynorsk / macrolanguage all fold to our single "nb" resource ─────────────
    [Theory]
    [InlineData("nb-NO", "nb")]
    [InlineData("nn-NO", "nb")]
    [InlineData("nb", "nb")]
    [InlineData("nn", "nb")]
    public void Norwegian_FoldsToBokmaal(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    // ── Arabic: region variants strip to the shipped neutral "ar" ────────────────────────────────────
    [Theory]
    [InlineData("ar-SA", "ar")]   // Saudi Arabia
    [InlineData("ar-EG", "ar")]   // Egypt
    [InlineData("ar", "ar")]      // bare neutral
    public void Arabic_StripsToNeutral(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    // ── Unsupported languages fall back to English ───────────────────────────────────────────────────
    [Theory]
    [InlineData("he-IL")]   // Hebrew, not shipped
    [InlineData("hi-IN")]   // Hindi, not shipped
    [InlineData("af-ZA")]   // Afrikaans, not shipped
    [InlineData("")]        // invariant culture
    public void Unsupported_FallsBackToEnglish(string os) =>
        Assert.Equal("en", Match(os));
}
