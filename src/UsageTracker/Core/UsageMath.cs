namespace UsageTracker.Core;

public enum Severity
{
    Good,    // > 50% remaining
    Warning, // 20–50%
    Critical // < 20%
}

public sealed record PaceInfo(double ExpectedPercentUsed, bool AheadOfPace)
{
    public string Label => AheadOfPace ? "ahead of pace" : "on pace";
}

public static class UsageMath
{
    public static Severity SeverityFor(double percentRemaining) => percentRemaining switch
    {
        > 50 => Severity.Good,
        >= 20 => Severity.Warning,
        _ => Severity.Critical,
    };

    /// <summary>Fraction (0–1) of the window that has elapsed, or null if the window is unknown.</summary>
    public static double? ElapsedFraction(DateTimeOffset? resetsAt, TimeSpan? length, DateTimeOffset now)
    {
        if (resetsAt is not { } reset || length is not { } len || len <= TimeSpan.Zero) return null;
        var start = reset - len;
        return Math.Clamp((now - start) / len, 0, 1);
    }

    /// <summary>Expected usage at this point of the window if usage were spread evenly.</summary>
    public static PaceInfo? Pace(UsageWindow window, DateTimeOffset now)
    {
        if (ElapsedFraction(window.ResetsAt, window.Length, now) is not { } elapsed) return null;
        var expected = elapsed * 100;
        return new PaceInfo(expected, window.PercentUsed > expected);
    }

    /// <summary>"3h 12m", "2d 4h", "45m", "&lt;1m".</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return "<1m";
        if (span.TotalDays >= 1) return span.Hours > 0 ? $"{(int)span.TotalDays}d {span.Hours}h" : $"{(int)span.TotalDays}d";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{span.Minutes}m";
    }

    public static string ResetCountdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } r) return "reset time unknown";
        var left = r - now;
        return left <= TimeSpan.Zero ? "resetting now" : $"resets in {FormatDuration(left)}";
    }

    /// <summary>Exponential backoff after consecutive failures, capped.</summary>
    public static TimeSpan NextPollDelay(TimeSpan interval, int consecutiveFailures, TimeSpan cap)
    {
        if (consecutiveFailures <= 0) return interval;
        var factor = Math.Pow(2, Math.Min(consecutiveFailures, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(interval.Ticks * factor, cap.Ticks));
        return delay < interval ? interval : delay;
    }
}
