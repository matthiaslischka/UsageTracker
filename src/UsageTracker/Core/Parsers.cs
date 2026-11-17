using System.Globalization;
using System.Text.Json.Nodes;

namespace UsageTracker.Core;

/// <summary>What a provider's usage endpoint told us, before status handling.</summary>
public sealed record ParsedUsage(string? Plan, IReadOnlyList<UsageWindow> Windows, CopilotQuota? Copilot = null);

public sealed record ClaudeCredentials(string AccessToken, DateTimeOffset? ExpiresAt, string? SubscriptionType);

public sealed record CodexCredentials(string AccessToken, string? AccountId);

internal static class Naming
{
    public static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    public static readonly TimeSpan OneWeek = TimeSpan.FromDays(7);

    /// <summary>"plus" → "Plus", "copilot_for_business" → "Copilot For Business".</summary>
    public static string? Title(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var words = raw.Replace('_', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    public static string WindowName(TimeSpan? length, string fallback)
    {
        if (length is not { } l || l <= TimeSpan.Zero) return fallback;
        if (l == OneWeek) return "Weekly";
        if (l == FiveHours) return "Session (5h)";
        if (l.TotalDays >= 1 && l.TotalDays % 1 == 0) return $"{l.TotalDays:0}-day";
        return $"{l.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)}h window";
    }

    /// <summary>Weekly (and other multi-day) windows get a pace marker in the flyout.</summary>
    public static bool ShowsPace(TimeSpan? length) => length is { } l && l >= TimeSpan.FromDays(1);
}

public static class ClaudeParser
{
    public static ClaudeCredentials? ParseCredentials(string? json)
    {
        var oauth = Json.TryParse(json).GetObject("claudeAiOauth");
        var token = oauth.GetString("accessToken");
        if (string.IsNullOrEmpty(token)) return null;
        var expiresMs = oauth.GetLong("expiresAt");
        DateTimeOffset? expires = null;
        if (expiresMs is > 0)
        {
            try { expires = DateTimeOffset.FromUnixTimeMilliseconds(expiresMs.Value); }
            catch (ArgumentOutOfRangeException) { }
        }
        return new ClaudeCredentials(token, expires, oauth.GetString("subscriptionType"));
    }

    public static ParsedUsage? ParseUsage(string? json, string? subscriptionType = null)
    {
        var root = Json.TryParse(json);
        if (root is not JsonObject) return null;

        var windows = new List<UsageWindow>();
        if (root.GetArray("limits") is { } limits)
        {
            foreach (var item in limits)
            {
                var percent = item.GetDouble("percent");
                if (percent is null) continue;
                var kind = item.GetString("kind") ?? item.GetString("group") ?? "limit";
                var group = item.GetString("group");
                var length = LengthFor(kind, group);
                windows.Add(new UsageWindow(NameFor(kind, group), percent.Value,
                    item.GetTimestamp("resets_at"), length, Naming.ShowsPace(length)));
            }
        }

        // Older response shape: only the fixed five_hour / seven_day objects.
        if (windows.Count == 0)
        {
            AddLegacy(root, "five_hour", "Session (5h)", Naming.FiveHours, windows);
            AddLegacy(root, "seven_day", "Weekly", Naming.OneWeek, windows);
        }

        return new ParsedUsage(Naming.Title(subscriptionType), windows);
    }

    private static void AddLegacy(JsonNode root, string key, string name, TimeSpan length, List<UsageWindow> into)
    {
        var obj = root.GetObject(key);
        if (obj.GetDouble("utilization") is not { } used) return;
        into.Add(new UsageWindow(name, used, obj.GetTimestamp("resets_at"), length, Naming.ShowsPace(length)));
    }

    private static TimeSpan? LengthFor(string kind, string? group)
    {
        if (kind == "session" || group == "session") return Naming.FiveHours;
        if (kind.StartsWith("weekly", StringComparison.Ordinal) || group == "weekly") return Naming.OneWeek;
        return null;
    }

    private static string NameFor(string kind, string? group)
    {
        if (kind == "session") return "Session (5h)";
        if (kind == "weekly_all" || kind == "weekly") return "Weekly";
        if (kind.StartsWith("weekly_", StringComparison.Ordinal))
            return $"Weekly ({Naming.Title(kind["weekly_".Length..])})";
        return Naming.Title(kind) ?? Naming.Title(group) ?? "Limit";
    }
}

public static class CodexParser
{
    public static CodexCredentials? ParseCredentials(string? json)
    {
        var tokens = Json.TryParse(json).GetObject("tokens");
        var token = tokens.GetString("access_token");
        return string.IsNullOrEmpty(token) ? null : new CodexCredentials(token, tokens.GetString("account_id"));
    }

    public static ParsedUsage? ParseUsage(string? json, DateTimeOffset now)
    {
        var root = Json.TryParse(json);
        if (root is not JsonObject) return null;

        var windows = new List<UsageWindow>();
        AddRateLimit(root.GetObject("rate_limit"), null, now, windows);

        // Shape unknown so far (null in every sample); accept an array of named rate limits.
        if (root.GetArray("additional_rate_limits") is { } extra)
        {
            foreach (var item in extra)
            {
                var label = Naming.Title(item.GetString("limit_name") ?? item.GetString("name"));
                AddRateLimit(item.GetObject("rate_limit") ?? item as JsonObject, label, now, windows);
            }
        }

        return new ParsedUsage(Naming.Title(root.GetString("plan_type")), windows);
    }

    private static void AddRateLimit(JsonObject? rateLimit, string? label, DateTimeOffset now, List<UsageWindow> into)
    {
        if (rateLimit is null) return;
        foreach (var (key, fallback) in new[] { ("primary_window", "Primary"), ("secondary_window", "Secondary") })
        {
            var w = rateLimit.GetObject(key);
            if (w.GetDouble("used_percent") is not { } used) continue;

            TimeSpan? length = w.GetDouble("limit_window_seconds") is > 0 and var secs
                ? TimeSpan.FromSeconds(secs)
                : null;
            var resetsAt = w.GetTimestamp("reset_at");
            if (resetsAt is null && w.GetDouble("reset_after_seconds") is >= 0 and var after)
                resetsAt = now.AddSeconds(after);

            var name = Naming.WindowName(length, fallback);
            if (label is not null) name = $"{label} · {name}";
            into.Add(new UsageWindow(name, used, resetsAt, length, Naming.ShowsPace(length)));
        }
    }
}

public static class CopilotParser
{
    private static readonly Dictionary<string, string> SnapshotNames = new()
    {
        ["premium_interactions"] = "Premium requests",
        ["chat"] = "Chat",
        ["completions"] = "Completions",
    };

    public static ParsedUsage? ParseUsage(string? json)
    {
        var root = Json.TryParse(json);
        if (root is not JsonObject) return null;

        var resetDate = root.GetDate("quota_reset_date") ?? root.GetDate("quota_reset_date_utc");
        var resetsAt = root.GetTimestamp("quota_reset_date_utc")
            ?? (resetDate is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null);
        TimeSpan? length = resetsAt is { } r ? r - r.AddMonths(-1) : null;

        var windows = new List<UsageWindow>();
        CopilotQuota? quota = null;

        if (root.GetObject("quota_snapshots") is { } snapshots)
        {
            // Premium requests first: it is the metric the icon is about.
            var ordered = snapshots.OrderBy(kv => kv.Key == "premium_interactions" ? 0 : 1);
            foreach (var (key, snap) in ordered)
            {
                if (snap is not JsonObject || snap.GetBool("unlimited") == true) continue;
                var entitlement = snap.GetDouble("entitlement");
                var remaining = snap.GetDouble("remaining");
                var pctRemaining = snap.GetDouble("percent_remaining")
                    ?? (entitlement > 0 && remaining is not null ? remaining / entitlement * 100 : null);
                if (pctRemaining is null) continue;

                var name = SnapshotNames.GetValueOrDefault(key) ?? Naming.Title(key) ?? key;
                windows.Add(new UsageWindow(name, 100 - pctRemaining.Value, resetsAt, length));

                if (key == "premium_interactions")
                {
                    quota = new CopilotQuota(
                        Entitlement: (int)(entitlement ?? 0),
                        Remaining: (int)(remaining ?? 0),
                        PercentRemaining: pctRemaining.Value,
                        OverageCount: (int)(snap.GetDouble("overage_count") ?? 0),
                        ResetDate: resetDate);
                }
            }
        }

        return new ParsedUsage(Naming.Title(root.GetString("copilot_plan")), windows, quota);
    }
}
