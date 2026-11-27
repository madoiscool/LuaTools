namespace GameGuy.Services.Downloads;

/// <summary>
/// Byte-level progress from a streaming download. <see cref="TotalBytes"/> is null when the response
/// carried no Content-Length (chunked), in which case the UI shows an indeterminate bar.
/// </summary>
/// <remarks>
/// This replaces the old <c>IProgress&lt;double?&gt;</c> contract for manifest/Denuvo downloads. The
/// byte counts were always available inside the copy loop; they were folded into a fraction and thrown
/// away, which made size/speed/ETA impossible to show. Out-of-scope downloads (everything routed through
/// <see cref="GithubProxy"/>: updates, unlocker, plugin, Steamless, CloudRedirect) still use the old
/// fraction shape.
/// </remarks>
public readonly record struct DownloadProgress(long BytesRead, long? TotalBytes)
{
    /// <summary>0..1 completion, or null when the total length is unknown.</summary>
    public double? Fraction => TotalBytes is > 0 ? (double)BytesRead / TotalBytes.Value : null;
}

/// <summary>
/// A minimal <see cref="IProgress{T}"/> that invokes its callback synchronously on the reporting thread.
/// </summary>
/// <remarks>
/// Deliberately NOT <see cref="Progress{T}"/>: that type captures the creating SynchronizationContext and
/// posts every single report to it. A download reports once per 80 KB chunk, so a 2 GB file would post
/// ~25,000 messages to the WPF dispatcher and flood the UI thread. The queue does its own time-throttled
/// marshalling instead, so reports must stay on the calling thread and be cheap.
/// </remarks>
public sealed class ProgressRelay<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
