namespace UsageTracker.Core;

/// <summary>Persisted per-window record of which thresholds already fired in the current period.</summary>
public sealed class AlertRecord
{
    public string Period { get; set; } = "";
    public List<double> Fired { get; set; } = [];
}

/// <summary>Everything kept in state.json.</summary>
public sealed class AppState
{
    /// <summary>Copilot snapshots keyed by account ("login@host").</summary>
    public Dictionary<string, CopilotSnapshot> CopilotSnapshots { get; set; } = [];

    /// <summary>Alert bookkeeping keyed by "providerId|metric".</summary>
    public Dictionary<string, AlertRecord> Alerts { get; set; } = [];
}

public sealed record AlertDecision(
    // The thresholds that fire now (the most severe one is the one worth showing).
    IReadOnlyList<double> NewlyFired,
    bool PeriodReset);

public static class AlertTracker
{
    /// <summary>
    /// Identifies a window period. Reset timestamps jitter by fractions of a second between
    /// polls, so round to the nearest 10 minutes.
    /// </summary>
    public static string PeriodId(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } r) return "unknown";
        var ticks = TimeSpan.FromMinutes(10).Ticks;
        var rounded = (r.UtcTicks + ticks / 2) / ticks * ticks;
        return new DateTime(rounded, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm'Z'");
    }

    /// <summary>
    /// Records a new observation and returns which thresholds fire. A threshold fires when
    /// <paramref name="crossed"/> says the value passed it, at most once per period.
    /// </summary>
    public static AlertDecision Evaluate(
        Dictionary<string, AlertRecord> alerts,
        string key,
        string period,
        double value,
        IEnumerable<double> thresholds,
        Func<double, double, bool> crossed)
    {
        var periodReset = false;
        if (!alerts.TryGetValue(key, out var record))
        {
            record = new AlertRecord { Period = period };
            alerts[key] = record;
        }
        else if (record.Period != period)
        {
            periodReset = true;
            record.Period = period;
            record.Fired.Clear();
        }

        var fired = new List<double>();
        foreach (var t in thresholds.Distinct())
        {
            if (record.Fired.Contains(t) || !crossed(value, t)) continue;
            record.Fired.Add(t);
            fired.Add(t);
        }
        return new AlertDecision(fired, periodReset);
    }

    /// <summary>Remaining % thresholds: fires when remaining drops below the threshold.</summary>
    public static bool BelowRemaining(double remaining, double threshold) => remaining < threshold;

    /// <summary>Used-of-budget thresholds: fires when used reaches the threshold.</summary>
    public static bool ReachedUsed(double used, double threshold) => used >= threshold;
}
