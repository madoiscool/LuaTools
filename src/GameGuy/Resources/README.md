# Translations

GameGuy is translated into 29 languages (full parity with Steam's supported languages). Help improve or
complete a translation, or request a new one!

## How to contribute a translation

Edit the RESX file for your language directly and open a pull request. Find
`Strings.<tag>.resx` in this folder, translate the `<value>` text of each `<data>` entry, and leave
everything else alone.

- Don't add or remove keys. Every language file must carry the exact same key set as the English
  source.
- Want a language that isn't listed? Open an issue and ask; extras beyond Steam's official set are
  welcome.

CI validates every PR that touches these files: `scripts/check-i18n.py` rejects any file that drops
a key, breaks the XML, or changes a placeholder.

## How it works

- **`Strings.resx`** is the English source of truth, the canonical list of keys.
- **`Strings.<tag>.resx`** is one file per language, where `<tag>` is the .NET culture (`de`, `fr`,
  `pt-BR`, `zh-Hans`, `nb`, …).
- The app picks the file matching the user's chosen / OS language, falling back to English per-key.

### Pending translation (English-only on purpose)

Keys for a feature whose UI is still being iterated on are added to `Strings.resx` **only**, and listed in
`PENDING_TRANSLATION` at the top of [`scripts/check-i18n.py`](../../../scripts/check-i18n.py). They're
exempt from the key-parity check (the app falls back to English per key at runtime), and the check prints
them as a reminder on every run. That list is the handoff to the next translation pass. Once a feature's
UI is final, translate its keys across all the language files, clear them from `PENDING_TRANSLATION`, and
parity is enforced again.

**Currently pending:** the Builds page (multi-build switching), added 2026-07-31.

Never leave a hardcoded user-facing literal in a view as a "translate later" shortcut. Always add the key
and reference it. That way the later pass only touches the `Strings.<tag>.resx` files, never the views.

## Rules to follow

1. **Keep placeholders exactly.** `{0}`, `{1}` are filled in at runtime (a name, a count, a path).
   Keep the same `{0}`/`{1}` in your translation; you may reorder them. Dropping one crashes that screen.
   - `"Page {0} of {1}"` → German `"Seite {0} von {1}"` ✓
2. **Don't translate these.** Leave them as-is:
   - Product names: **SteamTools**, **OpenSteamTools**, **CloudRedirect**, **Steam**, **Discord**, **Hubcap**.
   - Technical tokens: **App ID**, **DLC**, file paths like `steam\config\stplug-in`, extensions `.lua` / `.manifest` / `.zip`.
   - The picker filter string `Lua / manifest / zip|*.lua;...`: only translate the human labels
     ("All files"), never the `|` parts or `*.ext` patterns.
   - Stored filter/sort dropdown values. They're English keys the code compares against in
     switch/equality, and what the user sees is localized separately by
     `FilterOptionDisplayConverter`. Translating the stored value breaks the filter.
3. **Keep the brand puns playful.** `"Let's get Luing™!"` is a pun on **GameGuy** ("Lua" as a verb).
   Don't translate it literally; render the same upbeat "let's go add some Luas" spirit in your language,
   and keep the ™. ("Lua" stays as-is; it's the brand.)
4. **Match the tone.** Short labels stay short. The Mode descriptions are casual/jokey, so keep that voice.
5. **Escape XML:** `&` → `&amp;`, `<` → `&lt;`, `>` → `&gt;`. The parity check will fail on malformed
   XML, so run it before opening a PR (see "Checking your work" below).

## Adding a brand-new language (incl. extras beyond Steam's set)

The community can request any language, not just Steam's official ones: Hindi (`hi`), Hebrew (`he`),
and so on. Beyond the translated RESX file itself, a new language needs exactly two registration
lines and no other code changes:

1. **Add `Strings.<tag>.resx`.** Copy `Strings.resx`, name it for the .NET culture tag, and translate
   every `<value>`. The key set must match the English source exactly.
2. **Validate.** Run `python scripts/check-i18n.py` to confirm the full key set, valid XML, and
   placeholder parity.
3. **Register** the tag in `SupportedLanguages` (`Program.cs`) **and** `LanguageOptions`
   (`SettingsViewModel.cs`) with the native endonym. The strongly-typed accessors (`Get(nameof(Key))`
   reflection) and the `.csproj` `Strings*.resx` glob pick the new file up automatically, so the
   satellite assembly builds with no further changes.

> **RTL note:** right-to-left languages (Arabic, Hebrew, Urdu, Farsi, Pashto) currently ship
> **LTR-only**: the strings are translated but the UI isn't mirrored yet. Wiring app-wide `FlowDirection` is a known
> follow-up; don't assume an RTL language looks fully native just because it's translated.

## Checking your work

From the repo root, `python scripts/check-i18n.py` confirms all files have the full key set, valid XML,
and consistent placeholders. CI runs this automatically on every PR.
