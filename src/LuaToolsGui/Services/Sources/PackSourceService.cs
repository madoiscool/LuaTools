using System.IO;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services.Sources;

/// <summary>
/// Fetches from the manifest sources a pack declared. One service for all of them, because a pack names
/// a SHAPE the app already knows how to consume rather than supplying a routine of its own.
/// </summary>
/// <remarks>
/// Everything goes through <see cref="GithubProxy"/>, the existence probe included: the proxy tries the
/// url directly and only then its mirrors, so a probe that skipped it would answer "this source doesn't
/// have the game" whenever GitHub was blocked, while the download that followed would have succeeded
/// through a mirror.
/// </remarks>
public class PackSourceService(GithubProxy gh, ILogger<PackSourceService> log)
{
    // Its own client so a probe never inherits a long download timeout.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Does this source have the game? Any failure answers "no", never an error.</summary>
    public async Task<bool> HasGameAsync(PackSource source, long appId, CancellationToken ct = default)
    {
        try
        {
            foreach (string url in Urls(source, appId))
                foreach (string candidate in GithubProxy.Candidates(url))
                {
                    try { if (await ExistsAsync(candidate, ct)) return true; }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { /* this candidate is out; the next one may answer */ }
                }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Pack source {Source} probe for {AppId} failed", source.Name, appId);
            return false;
        }
    }

    /// <summary>Fetch the game to a temp file, for the existing install pipeline to take.</summary>
    public async Task<DownloadedFile> FetchAsync(
        PackSource source, long appId, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string ext = source.Kind is SourceKind.ManifestZip ? "zip" : "lua";
        string path = Path.Combine(Path.GetTempPath(), $"pack-{Sanitize(source.Name)}-{appId}.{ext}");

        // GithubProxy reports 0..1 fractions; scaled to the byte-shaped report the queue's UI speaks, so
        // the bar moves rather than sitting still.
        var sink = progress is null ? null
            : new ProgressRelay<double?>(f => progress.Report(new DownloadProgress((long)((f ?? 0) * 1000), 1000)));

        Exception? last = null;
        foreach (string url in Urls(source, appId))
        {
            try
            {
                await gh.DownloadAsync(url, path, sink, ct);
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    return new DownloadedFile(path, $"{appId}.{ext}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }

        log.LogDebug(last, "Pack source {Source} could not fetch {AppId}", source.Name, appId);
        throw new DownloadAbortedException(Resources.Strings.Sources_Err_Fetch);
    }

    /// <summary>
    /// Does this exact url serve something? HEAD first, and on a host that refuses HEAD, a one-byte
    /// ranged GET — a pack's url is any host its author chose, and plenty answer 405 or 501 to a HEAD
    /// while serving the file perfectly well over GET.
    /// </summary>
    private async Task<bool> ExistsAsync(string url, CancellationToken ct)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        head.Headers.TryAddWithoutValidation("User-Agent", "LuaTools");
        using var headRes = await _http.SendAsync(head, ct);
        if (headRes.StatusCode == HttpStatusCode.OK) return true;
        if (headRes.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
            return false;

        // Range is a request, not a promise: a server may ignore it and start sending the whole file, so
        // the response is disposed without reading the body and only the status is used.
        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        get.Headers.TryAddWithoutValidation("User-Agent", "LuaTools");
        get.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
        using var getRes = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
        return getRes.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent;
    }

    private static IEnumerable<string> Urls(PackSource source, long appId)
    {
        yield return Fill(source.Url, appId);
        foreach (string m in source.Mirrors) yield return Fill(m, appId);
    }

    private static string Fill(string template, long appId) =>
        template.Replace("{appid}", appId.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps a pack-supplied name fit for a temp file name.</summary>
    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
