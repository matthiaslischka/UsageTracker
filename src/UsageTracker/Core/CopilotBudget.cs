namespace UsageTracker.Core;

/// <summary>The premium-request count recorded at the first successful poll of a local day.</summary>
public sealed record CopilotSnapshot(DateOnly Day, int Remaining, DateOnly? ResetDate);

public sealed record CopilotBudgetResult(
    CopilotSnapshot Snapshot,
    // Requests available today; null on a weekend (with excludeWeekends) or after the reset date.
    double? TodayBudget,
    int UsedToday,
    // The percentage the tray icon shows: of today's budget, or of the month when there is no budget.
    double IconPercentRemaining)
{
    public bool HasDailyBudget => TodayBudget is not null;

    /// <summary>Share of today's budget already used, in percent (can exceed 100).</summary>
    public double? PercentOfBudgetUsed => TodayBudget switch
    {
        null => null,
        <= 0 => UsedToday > 0 ? 100 : 0,
        { } b => UsedToday / b * 100,
    };
}

public static class CopilotBudget
{
    /// <summary>Days from <paramref name="today"/> (inclusive) to <paramref name="resetDate"/> (exclusive).</summary>
    public static int DaysLeft(DateOnly today, DateOnly resetDate, bool excludeWeekends)
    {
        var count = 0;
        for (var d = today; d < resetDate; d = d.AddDays(1))
        {
            if (!excludeWeekends || !IsWeekend(d)) count++;
        }
        return count;
    }

    public static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>
    /// Keeps the existing snapshot when it is from today and the same reset period, and
    /// remaining has not gone up; otherwise takes a fresh one.
    /// </summary>
    public static CopilotSnapshot UpdateSnapshot(CopilotSnapshot? existing, CopilotQuota quota, DateOnly today)
    {
        var keep = existing is not null
            && existing.Day == today
            && existing.ResetDate == quota.ResetDate
            && quota.Remaining <= existing.Remaining;
        return keep ? existing! : new CopilotSnapshot(today, quota.Remaining, quota.ResetDate);
    }

    public static CopilotBudgetResult Compute(
        CopilotSnapshot? existing, CopilotQuota quota, DateOnly today, bool excludeWeekends)
    {
        var snapshot = UpdateSnapshot(existing, quota, today);
        var usedToday = Math.Max(0, snapshot.Remaining - quota.Remaining);
        var monthly = Math.Clamp(quota.PercentRemaining, 0, 100);

        var noBudgetToday = excludeWeekends && IsWeekend(today);
        var daysLeft = quota.ResetDate is { } reset ? DaysLeft(today, reset, excludeWeekends) : 0;
        if (noBudgetToday || daysLeft <= 0)
            return new CopilotBudgetResult(snapshot, null, usedToday, monthly);

        var budget = (double)snapshot.Remaining / daysLeft;
        var iconPercent = budget <= 0 ? 0 : Math.Max(0, 1 - usedToday / budget) * 100;
        return new CopilotBudgetResult(snapshot, budget, usedToday, iconPercent);
    }
}
