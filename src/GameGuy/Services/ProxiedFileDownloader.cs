using System.IO;
using System.Net.Http;
using Velopack.Sources;

namespace GameGuy.Services;

/// <summary>
/// An <see cref="IFileDownloader"/> for Velopack's auto-updater that makes its GitHub requests resilient
/// in blocked/throttled regions (e.g. China). Velopack calls this for BOTH the release feed (the API, via
/// DownloadString/DownloadBytes) and the package download (the .nupkg, via DownloadFile). Each call tries
/// the DIRECT GitHub URL first, then falls through the same mirrors as <see cref="GithubProxy"/>
/// (<see cref="GithubProxy.Candidates"/>), so the in-app self-update works where github.com is blocked.
/// <para>
/// It does the transfers itself rather than delegating to Velopack's stock
/// <c>HttpClientFileDownloader</c>, because that class builds its own <see cref="HttpClient"/>
/// internally and so would bypass <see cref="AppHttp"/>'s shared handler entirely. That would leave the
/// auto-updater on the system resolver while every other request honoured the DNS setting — stranding a
/// DNS-blocked user on whatever build they already had, which is the one thing they cannot fix from
/// inside the app.
/// </para>
/// </summary>
public class ProxiedFileDownloader : IFileDownloader
{
    private static readonly HttpClient Http = AppHttp.Create(TimeSpan.FromMinutes(30));

    private static HttpRequestMessage Request(string url, IDictionary<string, string>? headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null)
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        return req;
    }

    public async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        Exception? last = null;
        foreach (var candidate in GithubProxy.Candidates(url))
        {
            try
            {
                await Download(candidate, targetFile, progress, headers, timeout, cancelToken);
                return;
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new Exception($"Failed to download {url} from GitHub or any mirror.");
    }

    /// <summary>
    /// Stream one candidate URL to disk, reporting 0-100. Streamed rather than buffered because the
    /// .nupkg is ~7 MB today and grows with the app; progress is what the update toast shows.
    /// </summary>
    /// <remarks>
    /// A server that sends no Content-Length gives no denominator, so progress stays at 0 until the
    /// file completes rather than reporting a made-up percentage.
    /// </remarks>
    private static async Task Download(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers, double timeout, CancellationToken ct)
    {
        using var req = Request(url, headers);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(targetFile);

        var buffer = new byte[81920];
        long read = 0;
        int lastPercent = -1;

        while (true)
        {
            int n = await src.ReadAsync(buffer, ct);
            if (n == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;

            if (total is > 0)
            {
                int percent = (int)(read * 100 / total.Value);
                if (percent != lastPercent) { lastPercent = percent; progress(percent); }
            }
        }

        progress(100);
    }

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        Exception? last = null;
        foreach (var candidate in GithubProxy.Candidates(url))
        {
            try
            {
                using var req = Request(candidate, headers);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new Exception($"Failed to download {url} from GitHub or any mirror.");
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        Exception? last = null;
        foreach (var candidate in GithubProxy.Candidates(url))
        {
            try
            {
                using var req = Request(candidate, headers);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new Exception($"Failed to download {url} from GitHub or any mirror.");
    }
}
