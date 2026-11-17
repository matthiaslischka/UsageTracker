namespace UsageTracker.Core;

public enum UsageStatus
{
    Ok,
    NotSignedIn,
    TokenExpired,
    Error,
}

/// <summary>One rate-limit window, e.g. Claude's 5-hour session or Codex's weekly limit.</summary>
public sealed record UsageWindow(
    string Name,
    double PercentUsed,
    DateTimeOffset? ResetsAt,
    TimeSpan? Length,
    bool ShowPace = false)
{
    public double PercentRemaining => Math.Clamp(100 - PercentUsed, 0, 100);
}

/// <summary>Copilot's monthly premium-request quota.</summary>
public sealed record CopilotQuota(
    int Entitlement,
    int Remaining,
    double PercentRemaining,
    int OverageCount,
    DateOnly? ResetDate);

/// <summary>Normalized result of one poll of one provider.</summary>
public sealed record ProviderUsage(
    string ProviderId,
    string DisplayName,
    UsageStatus Status,
    string? PlanName = null,
    IReadOnlyList<UsageWindow>? Windows = null,
    string? Message = null,
    DateTimeOffset? LastUpdated = null,
    CopilotQuota? Copilot = null)
{
    public IReadOnlyList<UsageWindow> Windows { get; init; } = Windows ?? [];

    /// <summary>The window with the least remaining, or null when there are none.</summary>
    public UsageWindow? TightestWindow =>
        Windows.Count == 0 ? null : Windows.MinBy(w => w.PercentRemaining);

    public static ProviderUsage NotSignedIn(string id, string name, string message) =>
        new(id, name, UsageStatus.NotSignedIn, Message: message);

    public static ProviderUsage Expired(string id, string name, string message) =>
        new(id, name, UsageStatus.TokenExpired, Message: message);

    public static ProviderUsage Failed(string id, string name, string message) =>
        new(id, name, UsageStatus.Error, Message: message);
}
