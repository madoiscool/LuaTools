using CommunityToolkit.Mvvm.ComponentModel;

namespace GameGuy.Services.Downloads;

/// <summary>
/// The persisted shape of a finished download. Deliberately a flat POCO of primitives: a
/// <see cref="DownloadJob"/> holds delegates and cannot be serialized, so history records what
/// happened rather than anything that could be resumed.
/// </summary>
public sealed record DownloadHistoryRecord(
    string Id,
    string Kind,
    long AppId,
    string Title,
    string SubTitle,
    long Bytes,
    string Status,
    string? Message,
    long CompletedAtMs,

    /// <summary>
    /// What "Show in folder" opens for this entry: the installed file, or the depot output folder.
    /// </summary>
    /// <remarks>
    /// Trailing and nullable so a downloads.json written before this field existed still deserializes —
    /// the property is simply absent and lands as null, which reads as "nothing to show" and hides the
    /// menu item rather than offering a dead path.
    /// </remarks>
    string? RevealPath = null);

/// <summary>A finished download as shown in the Downloads tab's history list.</summary>
public partial class DownloadHistoryEntry : ObservableObject
{
    public DownloadHistoryEntry(DownloadHistoryRecord record)
    {
        Record = record;
        Status = Enum.TryParse<DownloadStatus>(record.Status, out var s) ? s : DownloadStatus.Completed;
    }

    public DownloadHistoryRecord Record { get; }
    public DownloadStatus Status { get; }

    public string Id => Record.Id;
    public long AppId => Record.AppId;
    public string Title => Record.Title;
    public string SubTitle => Record.SubTitle;
    public string? Message => Record.Message;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Record.Message);
    public bool Failed => Status is DownloadStatus.Failed;

    public string SizeLabel => Record.Bytes > 0 ? ByteFormat.Size(Record.Bytes) : "";

    public string StatusLabel => Status switch
    {
        DownloadStatus.Completed => Resources.Strings.Downloads_Status_Completed,
        DownloadStatus.Failed => Resources.Strings.Downloads_Status_Failed,
        _ => Resources.Strings.Downloads_Status_Cancelled,
    };

    /// <summary>Tool downloads carry appid 0; see <see cref="DownloadItem.CanCopyAppId"/>.</summary>
    public bool CanCopyAppId => Record.AppId > 0;

    public bool CanShowInFolder => !string.IsNullOrWhiteSpace(Record.RevealPath);

    public string WhenLabel =>
        DateTimeOffset.FromUnixTimeMilliseconds(Record.CompletedAtMs).LocalDateTime.ToString("g");

    public static DownloadHistoryRecord From(DownloadItem item, DownloadStatus status) => new(
        item.Id,
        item.Job.Kind.ToString(),
        item.AppId,
        item.Title,
        item.SubTitle,
        item.BytesRead,
        status.ToString(),
        item.Message,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        item.RevealPath);
}
