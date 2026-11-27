using System.Globalization;

namespace GameGuy.Services.Downloads;

/// <summary>
/// Display formatting for download metrics: transferred size, transfer rate and remaining time.
/// </summary>
/// <remarks>
/// Intentionally separate from the <c>FormatSize</c> helpers in <c>DownloadViewModel</c>,
/// <c>BuildsViewModel</c> and <c>DropInstallViewModel</c>. Those format *depot* sizes for diff rows:
/// they floor at MB and return an empty string for zero, which is right for a depot list but wrong for
/// live progress, where a small lua file is a few KB and "0" must render as "0 B" rather than vanish.
///
/// Unit suffixes are deliberately not localized, matching the existing depot-size helpers, which
/// hardcode "MB"/"GB" in every language.
/// </remarks>
public static class ByteFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"1.4 GB" / "812 MB" / "43 KB" / "512 B". Never returns an empty string.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return gb.ToString("0.##", Inv) + " GB";
        double mb = bytes / 1024d / 1024d;
        if (mb >= 1) return mb.ToString("0.#", Inv) + " MB";
        double kb = bytes / 1024d;
        if (kb >= 1) return kb.ToString("0", Inv) + " KB";
        return bytes.ToString("0", Inv) + " B";
    }

    /// <summary>"4.2 MB/s". Empty when the rate is not yet measurable, so callers can hide the label.</summary>
    /// <remarks>"/s" is a unit symbol, not prose: it stays unlocalized alongside the KB/MB/GB above.</remarks>
    public static string Rate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0) return "";
        return Size((long)bytesPerSecond) + "/s";
    }

    /// <summary>"1m 12s" / "8s" / "2h 5m". Empty for a non-positive or absurd duration.</summary>
    public static string Duration(TimeSpan t)
    {
        if (t <= TimeSpan.Zero || t.TotalDays >= 1) return "";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))}s";
    }
}
