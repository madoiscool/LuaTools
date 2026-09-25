using System.IO;
using System.Text.Json;

namespace LuaToolsGui.Services.Sources;

/// <summary>A source a pack declared, after the registry has vetted it.</summary>
public sealed record PackSource(
    string Name,
    string DisplayName,
    SourceKind Kind,
    string Url,
    IReadOnlyList<string> Mirrors,
    string? Badge);

/// <summary>One pack file, and what became of it.</summary>
public sealed class LoadedPack
{
    public required string FileName { get; init; }
    public required string DisplayName { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }

    /// <summary>Null when the pack loaded; otherwise why it was refused, in words for the user.</summary>
    public string? Error { get; set; }

    public List<PackSource> Sources { get; } = [];
}

/// <summary>
/// Reads manifest sources from <c>%AppData%\LuaToolsGui\sources\*.json</c>.
/// </summary>
/// <remarks>
/// <para>The sources this app can fetch from are otherwise fixed at build time, so following one to a
/// fresher mirror — or adding a community repo at all — means cutting a release. A pack file moves that
/// to editing a line of JSON.</para>
///
/// <para>A pack is <b>data only</b>. There is deliberately no way for one to supply code, a binary or a
/// fetch routine of its own: it names one of the shapes the app already knows how to consume, and the
/// app does the fetching. Installing a pack from a stranger cannot execute anything.</para>
///
/// <para>Nothing here may stop the app from starting. Every stage is wrapped, a bad file is recorded
/// against its own name and the loop moves on.</para>
/// </remarks>
public sealed class SourcePackRegistry
{
    /// <summary>Highest pack schema this build understands.</summary>
    private const int SupportedSchema = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "sources");

    private readonly List<LoadedPack> _packs = [];

    /// <summary>Every pack file found, loaded or refused.</summary>
    public IReadOnlyList<LoadedPack> Packs => _packs;

    /// <summary>Sources contributed by the packs that loaded, in file then declaration order.</summary>
    public IReadOnlyList<PackSource> Sources => _packs.SelectMany(p => p.Sources).ToList();

    /// <summary>
    /// Re-read the folder. Cheap (a handful of small files) and safe to call whenever the list might
    /// have changed, which is what lets a pack be added without restarting the app.
    /// </summary>
    /// <param name="reservedNames">
    /// Source names the app already uses. A pack claiming one is refused rather than shadowing it: a row
    /// whose behaviour depended on which registration won would be impossible to reason about.
    /// </param>
    public void Reload(IReadOnlyCollection<string> reservedNames)
    {
        _packs.Clear();

        List<string> files;
        try
        {
            if (!Directory.Exists(Root)) return;
            files = Directory.EnumerateFiles(Root, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return; }

        var taken = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            string shown = Path.GetFileName(file);
            SourcePack? pack;
            try { pack = JsonSerializer.Deserialize<SourcePack>(File.ReadAllText(file), JsonOpts); }
            catch (Exception ex)
            {
                _packs.Add(new LoadedPack { FileName = shown, DisplayName = shown, Error = ex.Message });
                continue;
            }

            if (pack is null)
            {
                _packs.Add(new LoadedPack { FileName = shown, DisplayName = shown, Error = "the file is empty" });
                continue;
            }

            var loaded = new LoadedPack
            {
                FileName = shown,
                DisplayName = string.IsNullOrWhiteSpace(pack.Name) ? shown : pack.Name!,
                Author = pack.Author,
                Description = pack.Description,
            };
            _packs.Add(loaded);

            if (pack.Schema > SupportedSchema)
            {
                loaded.Error = $"needs a newer LuaTools (schema {pack.Schema}, this build reads {SupportedSchema})";
                continue;
            }

            foreach (var e in pack.Sources) Vet(loaded, e, taken);
        }
    }

    /// <summary>Adds one declared source, or records why it was skipped. Never throws.</summary>
    private static void Vet(LoadedPack pack, SourceEntry e, HashSet<string> taken)
    {
        void Skip(string why) => pack.Error = pack.Error is null ? why : $"{pack.Error}; {why}";

        if (string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Url))
        { Skip("a source is missing its name or url"); return; }

        if (!Enum.TryParse<SourceKind>(e.Kind, ignoreCase: true, out var kind))
        { Skip($"\"{e.Name}\" has an unknown kind \"{e.Kind}\""); return; }

        // Everything a pack fetches goes over TLS. A source anyone can add is not the place to accept
        // plaintext: the file it returns is installed into Steam.
        if (!IsHttps(e.Url) || e.Mirrors.Any(u => !IsHttps(u)))
        { Skip($"\"{e.Name}\" must use https"); return; }

        if (!e.Url.Contains("{appid}", StringComparison.OrdinalIgnoreCase))
        { Skip($"\"{e.Name}\" has no {{appid}} in its url"); return; }

        if (!taken.Add(e.Name))
        { Skip($"\"{e.Name}\" is a name the app already uses"); return; }

        pack.Sources.Add(new PackSource(
            e.Name,
            string.IsNullOrWhiteSpace(e.DisplayName) ? e.Name : e.DisplayName!,
            kind,
            e.Url,
            e.Mirrors.Where(IsHttps).ToArray(),
            e.Badge));
    }

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
}
