using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services.Downloads;

/// <summary>
/// The app's single download scheduler: manifests and Denuvo fixes from every entry point (the Add
/// page, the Fixes page, the Steam store plugin and the protocol handler) run through this one queue.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> All queue state — <see cref="Items"/>, <see cref="History"/>, every
/// <see cref="DownloadItem"/> property and every scheduling decision — lives on the WPF dispatcher.
/// Only the HTTP stream and the install call run on background threads. That removes the need for any
/// locking around the collections and makes bindings safe by construction. The dispatcher is touched
/// once per state transition plus a throttled progress tick, never once per network chunk.</para>
///
/// <para><b>No download cap.</b> Every queued item starts as soon as the pump sees it. Downloads are
/// the only phase that runs in parallel, so an item's index in <see cref="Items"/> stops mattering once
/// it is in flight — reordering is only meaningful in the moment between Enqueue and the pump.</para>
///
/// <para><b>Installs are always serialized</b> behind <c>_installGate</c>, and are now the only limiter
/// in the pipeline: <c>LuaInstaller.InstallZip</c> writes into Steam's shared depotcache with a
/// File.Exists guard that two concurrent installs would race.</para>
/// </remarks>
public class DownloadQueue : IHostedService
{
    private readonly CacheService _cache;
    private readonly ILogger<DownloadQueue> _log;

    /// <summary>Signals the pump that the schedule may have changed.</summary>
    private readonly SemaphoreSlim _kick = new(0);

    /// <summary>Serializes the install phase. See the remarks above.</summary>
    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <summary>How long a successful item lingers in the queue before clearing itself.</summary>
    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pump;

    public DownloadQueue(CacheService cache, ILogger<DownloadQueue> log)
    {
        _cache = cache;
        _log = log;
    }

    private Dispatcher Dispatcher =>
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>Active and recently finished items, in scheduling order. Index = priority.</summary>
    public ObservableCollection<DownloadItem> Items { get; } = [];

    /// <summary>Finished downloads from this and previous sessions, newest first.</summary>
    public ObservableCollection<DownloadHistoryEntry> History { get; } = [];

    /// <summary>Raised when <see cref="ActiveCount"/> changes.</summary>
    public event Action? StateChanged;

    public int ActiveCount => Items.Count(i => i.IsActive);

    // ── Lifecycle ────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct)
    {
        foreach (var r in _cache.GetDownloadHistory().OrderByDescending(r => r.CompletedAtMs))
            History.Add(new DownloadHistoryEntry(r));

        _pump = Task.Run(() => PumpAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancel everything in flight and record it. Anything still active when the app closes is written
    /// to history as cancelled rather than silently vanishing.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _shutdown.Cancel();

        List<DownloadItem> active = [];
        await Dispatcher.InvokeAsync(() => active = Items.Where(i => i.IsActive).ToList());

        foreach (var item in active)
        {
            try { item.Cts.Cancel(); } catch { /* already disposed */ }
        }

        await Dispatcher.InvokeAsync(() =>
        {
            foreach (var item in active)
            {
                if (!item.IsActive) continue;
                item.Message ??= Resources.Strings.Downloads_Err_Interrupted;
                item.Status = DownloadStatus.Cancelled;
                History.Insert(0, new DownloadHistoryEntry(
                    DownloadHistoryEntry.From(item, DownloadStatus.Cancelled)));
                item.SettleCompletion(null);
            }
            PersistHistory();
        });

        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(3), ct); }
            catch { /* pump is parked on _kick; shutdown proceeds regardless */ }
        }
    }

    // ── Public API ───────────────────────────────────────────────────

    /// <summary>
    /// Add a job, or return the existing active item with the same
    /// <see cref="DownloadJob.DedupeKey"/>. This is the app-wide replacement for the per-page
    /// <c>if (IsBusy) return;</c> gates and for the HTTP server's duplicate-appid 409 check.
    /// </summary>
    public DownloadItem Enqueue(DownloadJob job)
    {
        return Dispatcher.Invoke(() =>
        {
            if (FindActiveCore(job.DedupeKey) is { } existing)
            {
                _log.LogDebug("Download already queued, reusing item: {Key}", job.DedupeKey);
                return existing;
            }

            var item = new DownloadItem(job);
            Items.Add(item);
            item.PropertyChanged += OnItemPropertyChanged;
            StateChanged?.Invoke();
            Kick();
            return item;
        });
    }

    /// <summary>The in-flight item for a dedupe key, or null.</summary>
    public DownloadItem? FindActive(string dedupeKey) =>
        Dispatcher.Invoke(() => FindActiveCore(dedupeKey));

    private DownloadItem? FindActiveCore(string dedupeKey) =>
        Items.FirstOrDefault(i => i.IsActive &&
            string.Equals(i.Job.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase));

    public void Cancel(DownloadItem item) => Dispatcher.Invoke(() =>
    {
        if (!item.IsActive) return;

        // Nothing is running for a Queued item (it never entered RunItemAsync) OR for a Paused one
        // (Pause cancelled the token and RunItemAsync already returned at its PauseRequested check), so
        // in both cases no one is left to observe the token and settle the item — this has to do it.
        //
        // Paused used to be missed here, which left the row stuck: Cancel appeared to do nothing, and
        // because Paused counts as active it offered no Remove either, so Resume was the only way out.
        bool nothingRunning = item.Status is DownloadStatus.Queued or DownloadStatus.Paused;

        try { item.Cts.Cancel(); } catch { }
        if (nothingRunning)
        {
            item.PauseRequested = false; // settled, not parked: don't let a later path read it as a pause
            Finish(item, DownloadStatus.Cancelled, Resources.Strings.Err_CancelledByUser, null);
        }
        Kick();
    });

    /// <summary>
    /// Pause a running depot download. Kills the child process but leaves its bytes on disk; Resume
    /// picks up from the first unfinished depot. Only depot jobs can pause (see DownloadItem.CanPause) —
    /// they're the only kind whose partial work survives the process dying.
    /// </summary>
    public void Pause(DownloadItem item) => Dispatcher.Invoke(() =>
    {
        if (!item.CanPause) return;
        item.PauseRequested = true;
        item.Status = DownloadStatus.Paused;
        item.BytesPerSecond = 0;
        item.Eta = null;
        try { item.Cts.Cancel(); } catch { /* already disposed */ }
        StateChanged?.Invoke();
    });

    /// <summary>Resume a paused depot download from the first depot it hadn't finished.</summary>
    public void Resume(DownloadItem item) => Dispatcher.Invoke(() =>
    {
        if (!item.CanResume) return;
        item.PauseRequested = false;
        item.NeedsValidate = true;   // the interrupted depot must be re-hashed, not trusted
        item.ResetCts();
        item.Status = DownloadStatus.Queued;
        StateChanged?.Invoke();
        Kick();
    });

    /// <summary>Re-enqueue a failed or cancelled item's job as a fresh item at the tail.</summary>
    public DownloadItem Retry(DownloadItem item)
    {
        Dispatcher.Invoke(() => Remove(item));
        var fresh = Enqueue(item.Job);

        // A depot job's progress lives on disk, not in the item, so a retry must inherit what the failed
        // attempt finished. Without this the new item restarts at depot 1 with NeedsValidate false, and
        // the half-written depot that caused the failure would be skipped as "already complete" —
        // silently leaving a corrupt install.
        if (item.Job.Kind is DownloadKind.Depot && !ReferenceEquals(fresh, item))
        {
            foreach (long id in item.CompletedDepots) fresh.CompletedDepots.Add(id);

            // Same reasoning for the files themselves: they are on disk under the SAME output folder, so
            // without this a cancel of the retry would offer to delete only what the retry re-created and
            // orphan the rest — and with nothing recorded yet it suppresses the prompt entirely.
            foreach (string f in item.CreatedFiles) fresh.CreatedFiles.Add(f);

            fresh.NeedsValidate = true;
        }
        return fresh;
    }

    /// <summary>Move a pending item up (-1) or down (+1). No-op once it has started.</summary>
    public void Move(DownloadItem item, int delta) => Dispatcher.Invoke(() =>
    {
        if (item.Status != DownloadStatus.Queued) return;
        int from = Items.IndexOf(item);
        if (from < 0) return;
        int to = Math.Clamp(from + delta, 0, Items.Count - 1);
        if (to == from) return;
        Items.Move(from, to);
        Kick();
    });

    /// <summary>Drop a finished item from the active list. History keeps the record.</summary>
    public void Remove(DownloadItem item) => Dispatcher.Invoke(() =>
    {
        if (item.IsActive) return;
        item.PropertyChanged -= OnItemPropertyChanged;
        Items.Remove(item);
        StateChanged?.Invoke();
    });

    public void ClearHistory() => Dispatcher.Invoke(() =>
    {
        History.Clear();
        PersistHistory();
    });

    /// <summary>Drop one finished download from the history. Nothing on disk is touched.</summary>
    public void RemoveHistory(DownloadHistoryEntry entry) => Dispatcher.Invoke(() =>
    {
        if (History.Remove(entry)) PersistHistory();
    });

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItem.Status)) StateChanged?.Invoke();
    }

    // ── Scheduler ────────────────────────────────────────────────────

    private void Kick()
    {
        try { _kick.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _kick.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                await Dispatcher.InvokeAsync(StartEligible);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Download pump cycle failed");
            }
        }
    }

    /// <summary>Start every queued item. Dispatcher thread only.</summary>
    private void StartEligible()
    {
        // Snapshot first: RunItemAsync can settle an item synchronously and mutate Items re-entrantly.
        var ready = Items.Where(i => i.Status == DownloadStatus.Queued
                                     && !i.Cts.IsCancellationRequested).ToList();

        foreach (var item in ready)
        {
            item.Status = DownloadStatus.Downloading;
            StateChanged?.Invoke();
            _ = RunItemAsync(item);
        }
    }

    private async Task RunItemAsync(DownloadItem item)
    {
        DownloadedFile? file = null;
        var ct = item.Cts.Token;

        try
        {
            // ── 1. Download ──────────────────────────────────────────
            // Progress is time-throttled here rather than in the services: a report arrives per 80 KB
            // chunk (~25,000 for a 2 GB zip) and posting each one would flood the UI thread.
            long lastPostTicks = 0;
            var sink = new ProgressRelay<DownloadProgress>(p =>
            {
                long now = DateTime.UtcNow.Ticks;
                bool done = p.TotalBytes is > 0 && p.BytesRead >= p.TotalBytes.Value;
                if (!done && now - lastPostTicks < TimeSpan.TicksPerMillisecond * 100) return;
                lastPostTicks = now;
                _ = Dispatcher.InvokeAsync(() => item.ApplySample(p.BytesRead, p.TotalBytes),
                    DispatcherPriority.Background);
            });

            file = await Task.Run(() => item.Job.DownloadAsync(item, sink, ct), ct);

            // ── 2. Optional confirmation gate ────────────────────────
            if (item.Job.ConfirmAsync is { } confirm)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    item.Status = DownloadStatus.AwaitingConfirmation;
                    StateChanged?.Invoke();
                });

                bool proceed;
                try { proceed = await confirm(file, item, ct); }
                catch (OperationCanceledException) { proceed = false; }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Download confirm gate threw; treating as declined");
                    proceed = false;
                }

                if (!proceed || ct.IsCancellationRequested)
                {
                    DeleteStaged(file.FilePath);
                    await Dispatcher.InvokeAsync(() =>
                        Finish(item, DownloadStatus.Cancelled, Resources.Strings.Add_Status_Cancelled, null));
                    return;
                }
            }

            // ── 3. Install (always serialized) ───────────────────────
            await Dispatcher.InvokeAsync(() => item.Status = DownloadStatus.Installing);

            await _installGate.WaitAsync(CancellationToken.None);
            JobResult result;
            try
            {
                result = await Task.Run(() => item.Job.InstallAsync(file!, item, ct), CancellationToken.None);
            }
            finally { _installGate.Release(); }

            await Dispatcher.InvokeAsync(() => Finish(
                item,
                result.Ok ? DownloadStatus.Completed : DownloadStatus.Failed,
                result.Message,
                result));
        }
        catch (OperationCanceledException)
        {
            // A pause cancels the same token a real cancel does. Leave the item parked in Paused and
            // keep its bytes: Resume re-enters this method and skips the depots already finished.
            if (item.PauseRequested) return;
            if (file is not null) DeleteStaged(file.FilePath);
            await Dispatcher.InvokeAsync(() =>
                Finish(item, DownloadStatus.Cancelled, Resources.Strings.Err_CancelledByUser, null));
        }
        catch (Exception ex)
        {
            if (file is not null) DeleteStaged(file.FilePath);
            _log.LogDebug(ex, "Download job failed: {Key}", item.Job.DedupeKey);
            // Both of these carry a message meant for the user; anything else is unexpected, so it gets
            // the generic text and the detail goes to the log above.
            string message = ex is ApiException or DownloadAbortedException
                ? ex.Message
                : string.Format(Resources.Strings.Add_Err_Download, Resources.Strings.Nav_SignInDiscord);

            // Some aborts are not failures — a declined elevation prompt, or a runtime that installed but
            // wants a reboot. Those settle as Cancelled so they don't read as something having broken.
            var status = ex is DownloadAbortedException { IsCancellation: true }
                ? DownloadStatus.Cancelled
                : DownloadStatus.Failed;

            await Dispatcher.InvokeAsync(() => Finish(item, status, message, null));
        }
        finally
        {
            Kick();
        }
    }

    /// <summary>Settle an item into a terminal state and record it. Dispatcher thread only.</summary>
    private void Finish(DownloadItem item, DownloadStatus status, string? message, JobResult? result)
    {
        if (!item.IsActive) return; // already settled (e.g. cancelled while queued)

        item.Message = message;
        item.Status = status;

        // Before the history row is built: From() copies the reveal path off the item, and settling the
        // result (which carries it) happens further down.
        item.RecordInstalledPath(result?.InstalledPath);

        History.Insert(0, new DownloadHistoryEntry(DownloadHistoryEntry.From(item, status)));
        while (History.Count > 100) History.RemoveAt(History.Count - 1);
        PersistHistory();

        item.SettleCompletion(result);

        // Per-job continuation only. There is deliberately no queue-wide "finished" event: each entry
        // point already reports its own outcome, and a global subscriber double-notified all of them.
        try { item.Job.OnFinished?.Invoke(item, result); }
        catch (Exception ex) { _log.LogDebug(ex, "Download OnFinished continuation threw"); }

        StateChanged?.Invoke();

        // A successful item has nothing left to act on — History keeps the record, so clear it from the
        // queue. Failed/Cancelled stay put: their message and Retry button are the only copy the user gets.
        if (status == DownloadStatus.Completed) _ = AutoDismissAsync(item);
    }

    /// <summary>Drop a completed item from the queue after a beat, so the user can read the outcome first.</summary>
    private async Task AutoDismissAsync(DownloadItem item)
    {
        try { await Task.Delay(AutoDismissDelay, _shutdown.Token); }
        catch (OperationCanceledException) { return; } // shutting down; leave the list alone

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                // Re-check: the user may have dismissed it, or Retry may have swapped it out.
                if (item.Status == DownloadStatus.Completed && Items.Contains(item)) Remove(item);
            });
        }
        catch (Exception ex) { _log.LogDebug(ex, "Auto-dismiss failed"); }
    }

    private void PersistHistory()
    {
        try { _cache.SaveDownloadHistory(History.Select(h => h.Record)); }
        catch (Exception ex) { _log.LogDebug(ex, "Persisting download history failed"); }
    }

    /// <summary>Best-effort delete of a staged file the install never consumed.</summary>
    private static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
