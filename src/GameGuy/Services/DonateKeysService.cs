using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace GameGuy.Services;

/// <summary>
/// Scans Steam's config/config.vdf for per-depot DecryptionKeys and donates any not yet sent to the
/// community key pool. Ported from the LuaTools plugin's donate_keys.lua. Runs silently in the
/// background on startup when the "Donate Keys" setting is on. Dedup is permanent (the backend accepts
/// each depot only once per IP). Best-effort: never throws (fire-and-forget). Records sent appids only
/// on HTTP 200, so failures retry next launch.
/// </summary>
public partial class DonateKeysService(SettingsService settings, SteamService steam, CacheService cache)
{
    private const string DonationUrl = AppConfig.ManifestBackendUrl + "/donatekeys/send";

    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(15));

    [GeneratedRegex(@"^\d{1,10}$")]
    private static partial Regex AppIdRegex();
    [GeneratedRegex(@"^[a-zA-Z0-9]{64}$")]
    private static partial Regex KeyRegex();

    /// <summary>Donate new decryption keys when enabled. Silent no-op on any failure / when off.</summary>
    public async Task SendPendingKeysIfEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            if (!settings.DonateKeys) return;

            string? root = steam.EffectivePath;
            if (root is null) return;
            string configPath = Path.Combine(root, "config", "config.vdf");
            if (!File.Exists(configPath)) return;

            var pairs = ExtractKeys(File.ReadAllText(configPath));
            if (pairs.Count == 0) return;

            // Dedup against everything we've ever donated: the backend accepts a depot only once per
            // IP, so re-sending is wasted. Only genuinely-new keys (newly installed games) get sent.
            var donated = new HashSet<string>(cache.GetDonatedAppIds());
            var fresh = pairs.Where(p => !donated.Contains(p.appid)).ToList();
            if (fresh.Count == 0) return;

            string payload = string.Join(",", fresh.Select(p => $"{p.appid}:{p.key}"));
            using var req = new HttpRequestMessage(HttpMethod.Post, DonationUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "text/plain"),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", AppConfig.DonateKeysUserAgent);

            var res = await _http.SendAsync(req, ct);
            if (res.StatusCode != System.Net.HttpStatusCode.OK) return; // don't record on failure → retry next launch

            // Record the newly donated appids so we don't re-send them this window.
            donated.UnionWith(fresh.Select(p => p.appid));
            cache.SaveDonatedAppIds(donated);
        }
        catch
        {
            // Background task, never surface errors. Unsent keys retry on the next launch.
        }
    }

    // ── VDF parsing (ported from the plugin's _parse_vdf_simple) ──────

    /// <summary>
    /// Walk a parsed config.vdf tree and collect (appid, key) for every object carrying a string
    /// "DecryptionKey" (the object's parent key is the appid). Only validated pairs are returned.
    /// </summary>
    /// <summary>Depot-id → decryption key pairs from a config.vdf. Internal so the depot downloader can
    /// reuse it as a key source for depots whose lua carries no key.</summary>
    internal static List<(string appid, string key)> ExtractKeys(string content)
    {
        var root = ParseVdf(content);
        var result = new List<(string, string)>();
        Walk(root);
        return result;

        void Walk(Dictionary<string, object> node)
        {
            foreach (var (k, v) in node)
            {
                if (v is not Dictionary<string, object> child) continue;
                if (child.TryGetValue("DecryptionKey", out var dk) && dk is string key)
                {
                    if (IsValidPair(k, key)) result.Add((k, key));
                }
                else
                {
                    Walk(child); // keep descending. DecryptionKey nests deep under depots/<appid>
                }
            }
        }
    }

    private static bool IsValidPair(string appid, string key) =>
        AppIdRegex().IsMatch(appid) && KeyRegex().IsMatch(key);

    /// <summary>
    /// Minimal recursive VDF (Valve KeyValues) reader: "quoted" tokens, { } nesting, // line comments.
    /// Nested objects → Dictionary&lt;string,object&gt;; leaf values → string. Mirrors the plugin parser.
    /// </summary>
    private static Dictionary<string, object> ParseVdf(string content)
    {
        var root = new Dictionary<string, object>();
        var stack = new Stack<Dictionary<string, object>>();
        stack.Push(root);
        string? pendingKey = null;

        int pos = 0, len = content.Length;
        while (pos < len)
        {
            char c = content[pos];

            if (c == '/' && pos + 1 < len && content[pos + 1] == '/')
            {
                int nl = content.IndexOf('\n', pos);
                pos = nl < 0 ? len : nl + 1;
            }
            else if (c == '"')
            {
                int end = content.IndexOf('"', pos + 1);
                if (end < 0) break;
                string token = content[(pos + 1)..end];
                pos = end + 1;

                if (pendingKey is null)
                {
                    pendingKey = token;
                }
                else
                {
                    stack.Peek()[pendingKey] = token; // key → leaf string value
                    pendingKey = null;
                }
            }
            else if (c == '{')
            {
                if (pendingKey is not null)
                {
                    var child = new Dictionary<string, object>();
                    stack.Peek()[pendingKey] = child;
                    stack.Push(child);
                    pendingKey = null;
                }
                pos++;
            }
            else if (c == '}')
            {
                if (stack.Count > 1) stack.Pop();
                pos++;
            }
            else
            {
                pos++;
            }
        }

        return root;
    }
}
