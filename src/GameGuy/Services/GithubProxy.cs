using System.IO;
using System.Net.Http;

namespace GameGuy.Services;

/// <summary>
/// Makes GitHub requests resilient in regions where github.com / api.github.com are blocked or throttled
/// (e.g. China). Every GitHub request (API JSON or a release-asset binary) is tried DIRECT first, and
/// on failure (network error or non-success) re-tried through each capability-matched mirror (see
/// <see cref="AppConfig.GithubApiMirrors"/> / <see cref="AppConfig.GithubDownloadMirrors"/>) by prefixing
/// the full GitHub URL: "<mirror>https://github.com/...". The first usable response wins.
///
/// USE THIS FOR ALL GITHUB REQUESTS. Don't call api.github.com / github.com directly. Route the URL
/// through <see cref="SendAsync"/> (API/metadata) or <see cref="DownloadAsync"/> (asset binaries) so the
/// mirror fallback applies everywhere automatically.
/// </summary>
public class GithubProxy
{
    // 5-min timeout matches the old UnlockerService client (large asset downloads on slow mirrors).
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromMinutes(5));

    /// <summary>Only github.com / api.github.com URLs get the mirror treatment. Anything else is left as-is.</summary>
    private static bool IsGithub(string url) =>
        url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Candidate URLs to try in order: the direct URL, then each CAPABILITY-MATCHED mirror-prefixed
    /// variant. api.github.com URLs get the API mirror(s); everything else (github.com downloads, raw,
    /// objects) gets the download mirror(s), never the wrong class, which would be a guaranteed-wasted hop
    /// (an API mirror 400s a download; a download mirror 403s the API). Public so the Velopack auto-update
    /// downloader can reuse the same direct→mirror fallback.</summary>
    public static IEnumerable<string> Candidates(string url)
    {
        yield return url; // direct first. Fastest when GitHub is reachable
        if (!IsGithub(url)) yield break;
        var mirrors = url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase)
            ? AppConfig.GithubApiMirrors
            : AppConfig.GithubDownloadMirrors;
        foreach (var mirror in mirrors)
            yield return mirror + url;
    }

    /// <summary>
    /// Send a GET (with the standard GitHub headers) trying direct then mirrors. Returns the first
    /// SUCCESSFUL response (caller owns/disposes it), or null if every candidate failed. Use for the
    /// GitHub API (releases JSON, etc.).
    /// </summary>
    public async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct = default)
    {
        HttpResponseMessage? lastResponse = null;
        foreach (var candidate in Candidates(url))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                req.Headers.TryAddWithoutValidation("User-Agent", "GameGuy");
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) return res;
                lastResponse?.Dispose();
                lastResponse = res; // remember the last non-success in case all fail (for status info)
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // user cancellation, not a mirror failure. Don't keep trying
            }
            catch
            {
                // network error for this candidate. Fall through to the next mirror
            }
        }
        return lastResponse; // null if every attempt threw; else the last failed response
    }

    /// <summary>
    /// Download a (GitHub) URL to a file, trying direct then mirrors, streaming with progress. Throws if
    /// every candidate fails. Use for release-asset binaries (.dll/.exe/.zip).
    /// </summary>
    public async Task DownloadAsync(string url, string destPath, IProgress<double?>? progress, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var candidate in Candidates(url))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                req.Headers.TryAddWithoutValidation("User-Agent", "GameGuy");
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();

                long? total = res.Content.Headers.ContentLength;
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(destPath);

                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    progress?.Report(total is > 0 ? (double)written / total.Value : null);
                }
                return; // success
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                try { File.Delete(destPath); } catch { /* partial file from a failed mirror */ }
            }
        }
        throw last ?? new HttpRequestException($"Could not download {url} from GitHub or any mirror.");
    }
}
