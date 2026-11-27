namespace GameGuy.Services.Downloads;

/// <summary>What a queued download is for. Drives the row icon and the history label.</summary>
public enum DownloadKind { Manifest, Dlc, DenuvoManifest, DenuvoFix, Depot, Tool }

/// <summary>
/// A job refused to start for a reason the user can act on — e.g. the game a Denuvo fix targets isn't
/// installed. Carries a ready-to-display, already-localized message.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ApiException"/> on purpose: this is thrown *before* any request is made, so
/// nothing was spent (no bandwidth, and no slot of the server-side daily limit). Reusing ApiException
/// would surface the message correctly but would name the failure after a call that never happened.
/// </remarks>
public sealed class DownloadAbortedException(string message, bool isCancellation = false) : Exception(message)
{
    /// <summary>
    /// Settle the item as Cancelled rather than Failed, keeping this exception's message.
    /// </summary>
    /// <remarks>
    /// For outcomes that stop the job without anything having gone wrong — the user declining an
    /// elevation prompt, or a runtime that installed fine but wants a reboot. Those are not errors and
    /// should not be dressed as red failures. Throwing <see cref="OperationCanceledException"/> would
    /// give the right status but discard the specific message.
    /// </remarks>
    public bool IsCancellation { get; } = isCancellation;
}

/// <summary>Outcome of a job's install phase.</summary>
/// <param name="Message">User-facing result text, already localized by the factory.</param>
public sealed record JobResult(bool Ok, string? Message, string? InstalledPath = null);

/// <summary>
/// One unit of work handed to <see cref="DownloadQueue"/>.
/// </summary>
/// <remarks>
/// The queue is a pure scheduler. It knows nothing about Hubcap vs lua.tools vs signed R2 URLs, and
/// nothing about <c>LuaInstaller</c> or <c>SteamService</c>; all of that lives in the delegates, which
/// in practice are always built by <see cref="ManifestJobFactory"/>. That keeps the three formerly
/// duplicated download+install implementations (the Add page, the plugin add service and the HTTP
/// server) sharing exactly one code path.
///
/// Deliberate consequence of holding delegates: a job is NOT serializable, so nothing resumes across an
/// app restart. Only the completed-history <see cref="DownloadHistoryRecord"/> persists.
/// </remarks>
public sealed record DownloadJob(
    DownloadKind Kind,

    /// <summary>
    /// Identity for duplicate suppression: "manifest:730", "dlc:12345", "denuvo:{fixId}:{slot}".
    /// Enqueuing a key that is already active returns the existing item instead of a second download.
    /// This is what replaces the old per-page <c>if (IsBusy) return;</c> gates.
    /// </summary>
    string DedupeKey,

    long AppId,
    string Title,
    string SubTitle,
    string? CoverPath,

    /// <summary>
    /// Fetch the bytes to a staged file. Runs on a background thread.
    /// </summary>
    /// <remarks>
    /// Receives the live <see cref="DownloadItem"/> so a multi-step job (the depot downloader, which runs
    /// one child process per depot) can report which step it's on. Single-step jobs ignore it. Any
    /// observable property it touches must be set on the dispatcher.
    /// </remarks>
    Func<DownloadItem, IProgress<DownloadProgress>, CancellationToken, Task<DownloadedFile>> DownloadAsync,

    /// <summary>Consume the staged file (install into Steam). Runs serialized against other installs.</summary>
    Func<DownloadedFile, DownloadItem, CancellationToken, Task<JobResult>> InstallAsync,

    /// <summary>
    /// Optional gate between download and install; true proceeds, false discards the staged file.
    /// While this is awaited the item is <see cref="DownloadStatus.AwaitingConfirmation"/>. Nothing else
    /// waits on it: the queue has no concurrency cap, so an unanswered dialog cannot wedge anything.
    /// </summary>
    Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? ConfirmAsync = null,

    /// <summary>
    /// Fired on the dispatcher once the item reaches a terminal state: usage-badge refresh, install
    /// banner, plugin AddState mutation. Exceptions are swallowed so a bad continuation cannot kill
    /// the pump.
    /// </summary>
    Action<DownloadItem, JobResult?>? OnFinished = null,

    /// <summary>Target of the Downloads tab's "Review" / "Reveal" button (e.g. navigate to Add).</summary>
    Action? OnReveal = null,

    /// <summary>
    /// Where this job writes its output, for jobs that produce a folder rather than a staged file
    /// (depot downloads). Null for everything else.
    /// </summary>
    /// <remarks>
    /// Exists so a cancel can offer to delete what was written. The path is otherwise captured only
    /// inside the job's own closure, leaving nothing outside able to name it.
    /// </remarks>
    string? OutputPath = null);
