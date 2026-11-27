using GameGuy.Services;
using GameGuy.Services.Downloads;
using Xunit;

namespace GameGuy.Tests;

/// <summary>
/// The speed/ETA readout on a download row.
/// </summary>
/// <remarks>
/// These guard a beta report of "download speed is unstable". Depot progress is reported once per
/// COMPLETED FILE — DepotDownloaderMod only prints its percentage line when a file's last chunk lands,
/// and its finer per-chunk ANSI progress is disabled whenever stdout is redirected, which is how we
/// launch it. Real depots are lumpy enough for that to matter: American Truck Simulator's content depot
/// is 73 files, one of which is 8.3 GB — about 166 seconds of silence at 50 MB/s.
///
/// So the window has to survive minute-long gaps AND bursts of files finishing together, which is what
/// each test below pins down.
/// </remarks>
public class DownloadRateTests
{
    private const long MB = 1024 * 1024;

    /// <summary>An item whose clock the test drives, so a 166-second gap costs no real time.</summary>
    private static (DownloadItem Item, Action<double> Advance) Build()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var job = new DownloadJob(
            DownloadKind.Depot, "test:1", 1, "Test", "Depot files", null,
            (_, _, _) => Task.FromResult(new DownloadedFile("x", "x")),
            (_, _, _) => Task.FromResult(new JobResult(true, null)));

        var item = new DownloadItem(job) { UtcNow = () => now };
        return (item, seconds => now = now.AddSeconds(seconds));
    }

    /// <summary>
    /// A single file so large it reports nothing for minutes must still yield its true average rate.
    /// This is the original bug: the age trim stripped the window down to the newest sample, the
    /// "fewer than two samples" guard returned early, and the speed stayed frozen at a stale value.
    /// </summary>
    [Fact]
    public void LongGapBetweenFiles_ReportsTheRealAverage()
    {
        var (item, advance) = Build();
        long total = 22_580 * MB;

        item.ApplySample(0, total);
        advance(166);
        item.ApplySample(8_315 * MB, total); // ATS's 8.3 GB file, start to finish

        Assert.InRange(item.BytesPerSecond, 45 * MB, 55 * MB); // 8.3 GB / 166 s ~= 50 MB/s
    }

    /// <summary>
    /// The other half of the same bug: the rate must not merely be non-zero, it must MOVE. A stale
    /// reading looks plausible on screen, which is exactly why the report said "unstable" rather than
    /// "stuck" — the number was a real speed, just one measured minutes earlier.
    /// </summary>
    [Fact]
    public void AfterAFastStart_ALongSlowFileUpdatesTheReading()
    {
        var (item, advance) = Build();
        long total = 22_580 * MB;
        long read = 0;

        // Dense stream of small files at ~100 MB/s.
        for (int i = 0; i < 30; i++)
        {
            advance(0.1);
            read += 10 * MB;
            item.ApplySample(read, total);
        }
        double fast = item.BytesPerSecond;
        Assert.InRange(fast, 90 * MB, 110 * MB);

        // Then one huge file that averages half that.
        advance(166);
        read += 8_315 * MB;
        item.ApplySample(read, total);

        Assert.NotEqual(fast, item.BytesPerSecond);
        Assert.InRange(item.BytesPerSecond, 40 * MB, 60 * MB);
    }

    /// <summary>
    /// Several concurrent files completing milliseconds apart must not be divided by that interval.
    /// Unguarded this produced multi-GB/s readings — the visible "spike" half of the report.
    /// </summary>
    [Fact]
    public void BurstOfCompletions_DoesNotSpike()
    {
        var (item, advance) = Build();
        long total = 22_580 * MB;
        long read = 0;

        item.ApplySample(read, total);
        advance(166);
        read += 8_315 * MB;
        item.ApplySample(read, total);

        // Eight parallel downloads all landing at once, 10 ms apart.
        for (int i = 0; i < 8; i++)
        {
            advance(0.01);
            read += 1 * MB;
            item.ApplySample(read, total);
        }

        Assert.InRange(item.BytesPerSecond, 1 * MB, 200 * MB); // sane; pre-fix this read in GB/s
    }

    /// <summary>A well-shaped depot (many small files) must still measure accurately.</summary>
    [Fact]
    public void DenseSteadyStream_MatchesTheActualRate()
    {
        var (item, advance) = Build();
        long total = 3_000 * MB;
        long read = 0;

        for (int i = 0; i < 100; i++) // 10 s at 20 MB/s
        {
            advance(0.1);
            read += 2 * MB;
            item.ApplySample(read, total);
        }

        Assert.InRange(item.BytesPerSecond, 18 * MB, 22 * MB);
    }

    /// <summary>ETA follows the rate, so it must be sane once the rate is.</summary>
    [Fact]
    public void Eta_FollowsTheMeasuredRate()
    {
        var (item, advance) = Build();
        long total = 1_000 * MB;

        item.ApplySample(0, total);
        advance(10);
        item.ApplySample(500 * MB, total); // 50 MB/s, 500 MB left => ~10 s

        Assert.NotNull(item.Eta);
        Assert.InRange(item.Eta!.Value.TotalSeconds, 8, 12);
    }

    /// <summary>A retry must not inherit the previous attempt's window.</summary>
    [Fact]
    public void ResetMetrics_ClearsTheWindow()
    {
        var (item, advance) = Build();
        long total = 1_000 * MB;

        item.ApplySample(0, total);
        advance(10);
        item.ApplySample(500 * MB, total);
        Assert.True(item.BytesPerSecond > 0);

        item.ResetMetrics();
        Assert.Equal(0, item.BytesPerSecond);
        Assert.Equal(0, item.BytesRead);
        Assert.Null(item.Eta);

        // And the cleared window must not resurrect the old samples as a bogus first reading.
        advance(1);
        item.ApplySample(0, total);
        Assert.Equal(0, item.BytesPerSecond);
    }
}
