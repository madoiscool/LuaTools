using System.IO;
using System.Net.Http;

namespace GameGuy.Services.Downloads;

/// <summary>
/// The shared response→file copy loop for manifest and Denuvo-fix downloads, plus the interim staging
/// folder they land in.
/// </summary>
/// <remarks>
/// This body previously existed twice, byte for byte: <c>ApiClient.SaveResponseAsync</c> and
/// <c>HubcapService.SaveResponseAsync</c>, each with its own private copy of the staging path constant.
/// Both now delegate here, so the progress contract and the filename sanitising live in one place.
///
/// Staging is under %TEMP% so downloads never pollute the user's Downloads folder. Files are deleted by
/// the caller once installed; <see cref="SweepStale"/> catches whatever a crash or a declined overwrite
/// confirmation left behind.
/// </remarks>
internal static class HttpFileDownloader
{
    /// <summary>Interim staging destination. Downloads land here, then are deleted once installed.</summary>
    public static readonly string StagingFolder =
        Path.Combine(Path.GetTempPath(), "GameGuy", "downloads");

    /// <summary>Stream the response body to a staged file, reporting byte counts as it goes.</summary>
    public static async Task<DownloadedFile> SaveResponseAsync(
        HttpResponseMessage res, string fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        string fileName = res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? fallbackName;
        foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');

        Directory.CreateDirectory(StagingFolder);
        string filePath = Path.Combine(StagingFolder, fileName);

        long? total = res.Content.Headers.ContentLength;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(filePath);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            progress?.Report(new DownloadProgress(written, total));
        }

        // One final report so a zero-length or single-chunk body still settles the bar at 100%.
        progress?.Report(new DownloadProgress(written, total ?? written));

        return new DownloadedFile(filePath, fileName);
    }

    /// <summary>
    /// Best-effort delete of staged files older than a day. Called once at startup: a download that was
    /// interrupted by a crash, or whose overwrite confirmation was never answered, leaves its zip behind.
    /// </summary>
    public static void SweepStale()
    {
        try
        {
            if (!Directory.Exists(StagingFolder)) return;
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (string path in Directory.EnumerateFiles(StagingFolder))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch { /* in use or gone. Next startup gets it */ }
            }
        }
        catch { /* best effort */ }
    }
}
