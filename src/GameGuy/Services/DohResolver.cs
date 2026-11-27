using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace GameGuy.Services;

/// <summary>
/// Resolves host names through Cloudflare's DNS-over-HTTPS endpoint, for users whose ISP DNS-blocks
/// lua.tools.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is addressed by IP LITERAL (<c>https://1.1.1.1/dns-query</c>), never by name. Resolving
/// "one.one.one.one" would need working DNS, which is exactly what is missing. Cloudflare's certificate
/// carries 1.1.1.1 in its SANs, so TLS still validates normally against the literal.
/// </para>
/// <para>
/// This holds its OWN <see cref="HttpClient"/> on a stock handler, deliberately not the shared one in
/// <see cref="AppHttp"/>. Routing the resolver through the handler that calls the resolver would
/// recurse on the first lookup.
/// </para>
/// <para>
/// Every failure returns an empty array rather than throwing. A resolver that cannot answer must let
/// the caller fall back to the system resolver, not take down the request.
/// </para>
/// </remarks>
public class DohResolver
{
    /// <summary>IP literal on purpose. See the class remarks.</summary>
    public const string Endpoint = "https://1.1.1.1/dns-query";

    // A short floor stops a hostile-but-valid TTL of 0 turning every connection into a DoH round-trip;
    // the cap keeps a very long TTL from pinning us to an address that has since moved.
    private static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (IPAddress[] Addresses, DateTimeOffset Expires)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Injectable clock so cache expiry is testable without sleeping.</summary>
    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    public DohResolver(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(5) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Accept.Add(new("application/dns-json"));
    }

    /// <summary>
    /// Names that must never go to DoH: it either cannot answer them or does not need to.
    /// </summary>
    /// <remarks>
    /// IP literals need no resolution at all. Loopback and single-label names are the local HTTP server,
    /// the plugin port probe and the OAuth callback — a public resolver has no answer for those, and
    /// asking would leak the name and add a round-trip to every local call.
    /// </remarks>
    public static bool ShouldBypass(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        if (IPAddress.TryParse(host, out _)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return !host.Contains('.'); // single-label: intranet, not something a public resolver knows
    }

    /// <summary>
    /// Addresses for <paramref name="host"/>, or an EMPTY array if DoH could not answer. Never throws
    /// (except on caller cancellation).
    /// </summary>
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (ShouldBypass(host)) return [];

        if (_cache.TryGetValue(host, out var hit) && hit.Expires > UtcNow())
            return hit.Addresses;

        try
        {
            string url = $"{Endpoint}?name={Uri.EscapeDataString(host)}&type=A";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return [];

            string body = await resp.Content.ReadAsStringAsync(ct);
            var (addresses, ttl) = Parse(body);
            if (addresses.Length == 0) return [];

            var lifetime = ttl < MinTtl ? MinTtl : ttl > MaxTtl ? MaxTtl : ttl;
            _cache[host] = (addresses, UtcNow() + lifetime);
            return addresses;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return []; } // unreachable, TLS refused, malformed: caller falls back to system DNS
    }

    /// <summary>
    /// Pull the A records out of a Cloudflare dns-json body. Returns empty on anything unexpected.
    /// </summary>
    /// <remarks>
    /// Public for tests: the parse is the part worth pinning down, and it needs no network to exercise.
    /// A non-zero <c>Status</c> is a real DNS error (NXDOMAIN is 3) and must yield nothing, so the
    /// caller falls back instead of treating "no such host" as an answer.
    /// </remarks>
    public static (IPAddress[] Addresses, TimeSpan Ttl) Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return ([], default);
            if (!root.TryGetProperty("Status", out var status) || status.GetInt32() != 0) return ([], default);
            if (!root.TryGetProperty("Answer", out var answer) || answer.ValueKind != JsonValueKind.Array)
                return ([], default);

            var addresses = new List<IPAddress>();
            int ttl = int.MaxValue;

            foreach (var entry in answer.EnumerateArray())
            {
                // type 1 = A, 28 = AAAA. Anything else in the chain (5 = CNAME) is a step, not an answer.
                if (!entry.TryGetProperty("type", out var type)) continue;
                int t = type.GetInt32();
                if (t != 1 && t != 28) continue;

                if (!entry.TryGetProperty("data", out var data)) continue;
                if (!IPAddress.TryParse(data.GetString(), out var ip)) continue;

                addresses.Add(ip);
                if (entry.TryGetProperty("TTL", out var recordTtl) && recordTtl.TryGetInt32(out int v) && v < ttl)
                    ttl = v;
            }

            if (addresses.Count == 0) return ([], default);
            return ([.. addresses], TimeSpan.FromSeconds(ttl == int.MaxValue ? 0 : Math.Max(0, ttl)));
        }
        catch { return ([], default); }
    }
}
