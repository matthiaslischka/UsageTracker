using UsageTracker.Core;

namespace UsageTracker.Tests;

public class CopilotBudgetTests
{
    private static readonly DateOnly Tuesday = new(2026, 11, 17);
    private static readonly DateOnly Saturday = new(2026, 11, 21);
    private static readonly DateOnly Reset = new(2026, 12, 1);

    private static CopilotQuota Quota(int remaining, int entitlement = 2500) =>
        new(entitlement, remaining, remaining * 100.0 / entitlement, 0, Reset);

    [Fact]
    public void Counts_working_days_inclusive_of_today_exclusive_of_reset()
    {
        // Nov 17–30: Tue–Fri (4) + 23–27 (5) + Mon 30 (1)
        Assert.Equal(10, CopilotBudget.DaysLeft(Tuesday, Reset, excludeWeekends: true));
        Assert.Equal(14, CopilotBudget.DaysLeft(Tuesday, Reset, excludeWeekends: false));
        Assert.Equal(0, CopilotBudget.DaysLeft(Reset, Reset, excludeWeekends: true));
        Assert.Equal(1, CopilotBudget.DaysLeft(new DateOnly(2026, 11, 30), Reset, excludeWeekends: true));
    }

    [Fact]
    public void First_poll_of_the_day_sets_the_budget()
    {
        var r = CopilotBudget.Compute(null, Quota(2106), Tuesday, excludeWeekends: true);

        Assert.Equal(new CopilotSnapshot(Tuesday, 2106, Reset), r.Snapshot);
        Assert.Equal(210.6, r.TodayBudget!.Value, 6);
        Assert.Equal(0, r.UsedToday);
        Assert.Equal(100, r.IconPercentRemaining, 6);
    }

    [Fact]
    public void Usage_during_the_day_reduces_icon_percent()
    {
        var morning = new CopilotSnapshot(Tuesday, 2106, Reset);
        var r = CopilotBudget.Compute(morning, Quota(2080), Tuesday, excludeWeekends: true);

        Assert.Same(morning, r.Snapshot);
        Assert.Equal(26, r.UsedToday);
        Assert.Equal((1 - 26 / 210.6) * 100, r.IconPercentRemaining, 6);
        Assert.Equal(26 / 210.6 * 100, r.PercentOfBudgetUsed!.Value, 6);
    }

    [Fact]
    public void Over_budget_clamps_icon_to_zero()
    {
        var morning = new CopilotSnapshot(Tuesday, 2106, Reset);
        var r = CopilotBudget.Compute(morning, Quota(1800), Tuesday, excludeWeekends: true);

        Assert.Equal(0, r.IconPercentRemaining);
        Assert.True(r.PercentOfBudgetUsed > 100);
    }

    [Fact]
    public void New_day_takes_a_fresh_snapshot()
    {
        var yesterday = new CopilotSnapshot(Tuesday.AddDays(-1), 2300, Reset);
        var r = CopilotBudget.Compute(yesterday, Quota(2106), Tuesday, excludeWeekends: true);

        Assert.Equal(new CopilotSnapshot(Tuesday, 2106, Reset), r.Snapshot);
    }

    [Fact]
    public void Remaining_going_up_takes_a_fresh_snapshot()
    {
        var morning = new CopilotSnapshot(Tuesday, 100, Reset);
        var r = CopilotBudget.Compute(morning, Quota(2500), Tuesday, excludeWeekends: true);

        Assert.Equal(2500, r.Snapshot.Remaining);
        Assert.Equal(0, r.UsedToday);
    }

    [Fact]
    public void Snapshot_from_previous_reset_period_is_replaced()
    {
        var old = new CopilotSnapshot(Tuesday, 2106, new DateOnly(2026, 11, 1));
        var r = CopilotBudget.Compute(old, Quota(2000), Tuesday, excludeWeekends: true);

        Assert.Equal(new CopilotSnapshot(Tuesday, 2000, Reset), r.Snapshot);
    }

    [Fact]
    public void Weekend_has_no_daily_budget_and_uses_monthly_percent()
    {
        var r = CopilotBudget.Compute(null, Quota(2106), Saturday, excludeWeekends: true);

        Assert.False(r.HasDailyBudget);
        Assert.Null(r.PercentOfBudgetUsed);
        Assert.Equal(84.24, r.IconPercentRemaining, 6);
    }

    [Fact]
    public void Weekend_counts_when_weekends_are_included()
    {
        // Sat Nov 21 → Nov 30 inclusive = 10 calendar days.
        var r = CopilotBudget.Compute(null, Quota(2000), Saturday, excludeWeekends: false);

        Assert.Equal(200, r.TodayBudget!.Value, 6);
    }

    [Fact]
    public void No_budget_on_or_after_reset_date()
    {
        var r = CopilotBudget.Compute(null, Quota(2000), Reset, excludeWeekends: true);

        Assert.Null(r.TodayBudget);
    }

    [Fact]
    public void Zero_remaining_gives_zero_budget_and_zero_icon()
    {
        var r = CopilotBudget.Compute(null, Quota(0), Tuesday, excludeWeekends: true);

        Assert.Equal(0, r.TodayBudget);
        Assert.Equal(0, r.IconPercentRemaining);
    }
}

public class PaceAndFormattingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(58, true)]
    [InlineData(50, false)]
    [InlineData(40, false)]
    public void Weekly_pace_compares_usage_to_elapsed_fraction(double used, bool ahead)
    {
        var window = new UsageWindow("Weekly", used, Now.AddDays(3.5), TimeSpan.FromDays(7), ShowPace: true);

        var pace = UsageMath.Pace(window, Now)!;

        Assert.Equal(50, pace.ExpectedPercentUsed, 6);
        Assert.Equal(ahead, pace.AheadOfPace);
        Assert.Equal(ahead ? "ahead of pace" : "on pace", pace.Label);
    }

    [Fact]
    public void Elapsed_fraction_is_clamped_and_needs_window_info()
    {
        Assert.Equal(1, UsageMath.ElapsedFraction(Now.AddHours(-1), TimeSpan.FromDays(7), Now));
        Assert.Equal(0, UsageMath.ElapsedFraction(Now.AddDays(8), TimeSpan.FromDays(7), Now));
        Assert.Null(UsageMath.ElapsedFraction(null, TimeSpan.FromDays(7), Now));
        Assert.Null(UsageMath.ElapsedFraction(Now, null, Now));
        Assert.Null(UsageMath.Pace(new UsageWindow("x", 10, null, null), Now));
    }

    [Theory]
    [InlineData(3 * 60 + 12, "3h 12m")]
    [InlineData(2 * 1440 + 4 * 60 + 30, "2d 4h")]
    [InlineData(3 * 1440, "3d")]
    [InlineData(45, "45m")]
    [InlineData(0.5, "<1m")]
    public void Formats_durations(double minutes, string expected) =>
        Assert.Equal(expected, UsageMath.FormatDuration(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Formats_reset_countdown()
    {
        Assert.Equal("resets in 3h 12m", UsageMath.ResetCountdown(Now.AddMinutes(192), Now));
        Assert.Equal("resetting now", UsageMath.ResetCountdown(Now.AddMinutes(-1), Now));
        Assert.Equal("reset time unknown", UsageMath.ResetCountdown(null, Now));
    }

    [Theory]
    [InlineData(100, Severity.Good)]
    [InlineData(50.5, Severity.Good)]
    [InlineData(50, Severity.Warning)]
    [InlineData(20, Severity.Warning)]
    [InlineData(19.9, Severity.Critical)]
    [InlineData(0, Severity.Critical)]
    public void Severity_bands(double remaining, Severity expected) =>
        Assert.Equal(expected, UsageMath.SeverityFor(remaining));

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 30)]
    [InlineData(50, 30)]
    public void Backoff_doubles_and_caps_at_30_minutes(int failures, double expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
            UsageMath.NextPollDelay(TimeSpan.FromMinutes(5), failures, TimeSpan.FromMinutes(30)));
}

public class AlertTrackerTests
{
    private static readonly double[] Remaining = [20, 5];

    [Fact]
    public void Each_threshold_fires_once_per_period()
    {
        var alerts = new Dictionary<string, AlertRecord>();
        AlertDecision Eval(double remaining, string period = "p1") =>
            AlertTracker.Evaluate(alerts, "claude|Weekly", period, remaining, Remaining, AlertTracker.BelowRemaining);

        Assert.Empty(Eval(40).NewlyFired);
        Assert.Equal([20.0], Eval(15).NewlyFired);
        Assert.Empty(Eval(12).NewlyFired);
        Assert.Equal([5.0], Eval(4).NewlyFired);
        Assert.Empty(Eval(1).NewlyFired);

        var next = Eval(100, "p2");
        Assert.True(next.PeriodReset);
        Assert.Empty(next.NewlyFired);
        Assert.Equal([20.0], Eval(19, "p2").NewlyFired);
    }

    [Fact]
    public void A_big_drop_fires_all_crossed_thresholds_at_once()
    {
        var alerts = new Dictionary<string, AlertRecord>();
        var d = AlertTracker.Evaluate(alerts, "k", "p", 3, Remaining, AlertTracker.BelowRemaining);

        Assert.Equal([20.0, 5.0], d.NewlyFired);
        Assert.False(d.PeriodReset);
    }

    [Fact]
    public void Daily_budget_thresholds_fire_on_reaching_used_percent()
    {
        var alerts = new Dictionary<string, AlertRecord>();
        double[] used = [80, 100];

        Assert.Empty(AlertTracker.Evaluate(alerts, "copilot|daily", "2026-11-17", 79.9, used, AlertTracker.ReachedUsed).NewlyFired);
        Assert.Equal([80.0], AlertTracker.Evaluate(alerts, "copilot|daily", "2026-11-17", 80, used, AlertTracker.ReachedUsed).NewlyFired);
        Assert.Equal([100.0], AlertTracker.Evaluate(alerts, "copilot|daily", "2026-11-17", 130, used, AlertTracker.ReachedUsed).NewlyFired);
    }

    [Fact]
    public void Period_id_ignores_sub_second_jitter()
    {
        var a = DateTimeOffset.Parse("2026-11-17T11:59:59.539857+00:00");
        var b = DateTimeOffset.Parse("2026-11-17T12:00:00.912345+00:00");

        Assert.Equal(AlertTracker.PeriodId(a), AlertTracker.PeriodId(b));
        Assert.NotEqual(AlertTracker.PeriodId(a), AlertTracker.PeriodId(a.AddHours(5)));
    }
}

public class SettingsTests
{
    [Fact]
    public void Partial_settings_keep_defaults()
    {
        var s = JsonFiles.Deserialize<AppSettings>("""{ "pollIntervalMinutes": 2, "providers": { "codex": false } }""")!;

        Assert.Equal(TimeSpan.FromMinutes(2), s.PollInterval);
        Assert.True(s.Providers.Claude);
        Assert.False(s.Providers.Codex);
        Assert.True(s.Copilot.ExcludeWeekends);
        Assert.Single(s.Copilot.Accounts);
        Assert.Equal([20.0, 5.0], s.Toasts.RemainingThresholds);
        Assert.False(s.Toasts.ResetToast);
    }

    [Fact]
    public void Invalid_settings_return_null() => Assert.Null(JsonFiles.Deserialize<AppSettings>("{ nope"));

    [Fact]
    public void State_round_trips()
    {
        var state = new AppState();
        state.CopilotSnapshots["me@host"] = new CopilotSnapshot(new DateOnly(2026, 11, 17), 2106, new DateOnly(2026, 12, 1));
        state.Alerts["claude|Weekly"] = new AlertRecord { Period = "p", Fired = [20] };

        var back = JsonFiles.Deserialize<AppState>(JsonFiles.Serialize(state))!;

        Assert.Equal(state.CopilotSnapshots["me@host"], back.CopilotSnapshots["me@host"]);
        Assert.Equal([20.0], back.Alerts["claude|Weekly"].Fired);
    }
}
