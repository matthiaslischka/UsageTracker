using System.Text;
using UsageTracker.Core;
using UsageTracker.Infrastructure;

namespace UsageTracker.Tests;

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

public class ClaudeParserTests
{
    [Fact]
    public void Parses_limits_array()
    {
        var parsed = ClaudeParser.ParseUsage(Fixture.Read("claude_usage.json"), "team")!;

        Assert.Equal("Team", parsed.Plan);
        Assert.Collection(parsed.Windows,
            session =>
            {
                Assert.Equal("Session (5h)", session.Name);
                Assert.Equal(57, session.PercentUsed);
                Assert.Equal(43, session.PercentRemaining);
                Assert.Equal(TimeSpan.FromHours(5), session.Length);
                Assert.False(session.ShowPace);
                Assert.Equal(DateTimeOffset.Parse("2026-11-17T11:59:59.539857+00:00"), session.ResetsAt);
            },
            weekly =>
            {
                Assert.Equal("Weekly", weekly.Name);
                Assert.Equal(58, weekly.PercentUsed);
                Assert.Equal(TimeSpan.FromDays(7), weekly.Length);
                Assert.True(weekly.ShowPace);
            });
    }

    [Fact]
    public void Falls_back_to_five_hour_and_seven_day()
    {
        var parsed = ClaudeParser.ParseUsage(Fixture.Read("claude_usage_legacy.json"))!;

        Assert.Null(parsed.Plan);
        Assert.Equal(["Session (5h)", "Weekly"], parsed.Windows.Select(w => w.Name));
        Assert.Equal([12.5, 80.0], parsed.Windows.Select(w => w.PercentUsed));
    }

    [Fact]
    public void Tightest_window_is_lowest_remaining()
    {
        var parsed = ClaudeParser.ParseUsage(Fixture.Read("claude_usage_legacy.json"))!;
        var usage = new ProviderUsage("claude", "Claude", UsageStatus.Ok, Windows: parsed.Windows);

        Assert.Equal("Weekly", usage.TightestWindow!.Name);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"limits":null,"five_hour":null}""")]
    [InlineData("""{"limits":[{"kind":"session"},{"percent":"oops"},42,null]}""")]
    [InlineData("""{"limits":"not an array","five_hour":{"utilization":"n/a"}}""")]
    public void Tolerates_schema_drift(string json)
    {
        var parsed = ClaudeParser.ParseUsage(json);

        Assert.NotNull(parsed);
        Assert.Empty(parsed.Windows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void Rejects_non_object_bodies(string json) => Assert.Null(ClaudeParser.ParseUsage(json));

    [Fact]
    public void Accepts_numbers_as_strings_and_unknown_kinds()
    {
        var parsed = ClaudeParser.ParseUsage(
            """{"limits":[{"kind":"weekly_opus","group":"weekly","percent":"12.5","resets_at":"2026-11-20T00:00:00Z"},{"kind":"monthly_thing","percent":3}]}""")!;

        Assert.Equal("Weekly (Opus)", parsed.Windows[0].Name);
        Assert.Equal(12.5, parsed.Windows[0].PercentUsed);
        Assert.True(parsed.Windows[0].ShowPace);
        Assert.Equal("Monthly Thing", parsed.Windows[1].Name);
        Assert.Null(parsed.Windows[1].Length);
    }

    [Fact]
    public void Reads_credentials()
    {
        var creds = ClaudeParser.ParseCredentials(Fixture.Read("claude_credentials.json"))!;

        Assert.Equal("sk-ant-oat01-FAKE", creds.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794924403620), creds.ExpiresAt);
        Assert.Equal("team", creds.SubscriptionType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"claudeAiOauth":{"accessToken":""}}""")]
    [InlineData("""{"mcpOAuth":{}}""")]
    public void Missing_credentials_mean_not_signed_in(string? json) => Assert.Null(ClaudeParser.ParseCredentials(json));
}

public class CodexParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_primary_and_secondary_windows()
    {
        var parsed = CodexParser.ParseUsage(Fixture.Read("codex_usage.json"), Now)!;

        Assert.Equal("Plus", parsed.Plan);
        Assert.Collection(parsed.Windows,
            primary =>
            {
                Assert.Equal("Session (5h)", primary.Name);
                Assert.Equal(6, primary.PercentUsed);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794916851), primary.ResetsAt);
                Assert.Equal(TimeSpan.FromSeconds(18000), primary.Length);
                Assert.False(primary.ShowPace);
            },
            secondary =>
            {
                Assert.Equal("Weekly", secondary.Name);
                Assert.Equal(1, secondary.PercentUsed);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795387689), secondary.ResetsAt);
                Assert.True(secondary.ShowPace);
            });
    }

    [Fact]
    public void Uses_reset_after_seconds_when_reset_at_is_missing()
    {
        var parsed = CodexParser.ParseUsage(
            """{"rate_limit":{"primary_window":{"used_percent":40,"limit_window_seconds":18000,"reset_after_seconds":600}}}""", Now)!;

        Assert.Equal(Now.AddMinutes(10), parsed.Windows.Single().ResetsAt);
    }

    [Fact]
    public void Tolerates_missing_rate_limit() =>
        Assert.Empty(CodexParser.ParseUsage("""{"plan_type":"free","rate_limit":null}""", Now)!.Windows);

    [Fact]
    public void Reads_additional_rate_limits_when_present()
    {
        var parsed = CodexParser.ParseUsage(
            """{"rate_limit":null,"additional_rate_limits":[{"limit_name":"code_review","rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":604800}}}]}""", Now)!;

        Assert.Equal("Code Review · Weekly", parsed.Windows.Single().Name);
    }

    [Fact]
    public void Reads_credentials()
    {
        var creds = CodexParser.ParseCredentials(Fixture.Read("codex_auth.json"))!;

        Assert.Equal("FAKE-ACCESS", creds.AccessToken);
        Assert.Equal("00000000-0000-0000-0000-000000000000", creds.AccountId);
    }

    [Fact]
    public void Api_key_only_auth_is_not_signed_in() =>
        Assert.Null(CodexParser.ParseCredentials("""{"OPENAI_API_KEY":"sk-x","tokens":null}"""));
}

public class CopilotParserTests
{
    [Fact]
    public void Parses_premium_interactions_and_skips_unlimited()
    {
        var parsed = CopilotParser.ParseUsage(Fixture.Read("copilot_user.json"))!;

        Assert.Equal("Business", parsed.Plan);
        var window = Assert.Single(parsed.Windows);
        Assert.Equal("Premium requests", window.Name);
        Assert.Equal(15.8, window.PercentUsed, 3);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), window.ResetsAt);
        Assert.Equal(TimeSpan.FromDays(30), window.Length);

        var quota = parsed.Copilot!;
        Assert.Equal(2500, quota.Entitlement);
        Assert.Equal(2106, quota.Remaining);
        Assert.Equal(84.2, quota.PercentRemaining, 3);
        Assert.Equal(0, quota.OverageCount);
        Assert.Equal(new DateOnly(2026, 12, 1), quota.ResetDate);
    }

    [Fact]
    public void Reads_overage_and_computes_percent_when_missing()
    {
        var parsed = CopilotParser.ParseUsage(
            """{"quota_reset_date":"2026-12-01","quota_snapshots":{"premium_interactions":{"entitlement":300,"remaining":0,"unlimited":false,"overage_count":12}}}""")!;

        Assert.Equal(0, parsed.Copilot!.PercentRemaining);
        Assert.Equal(12, parsed.Copilot.OverageCount);
        Assert.Equal(100, parsed.Windows.Single().PercentUsed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"quota_snapshots":null}""")]
    [InlineData("""{"quota_snapshots":{"premium_interactions":{"unlimited":true}}}""")]
    public void Tolerates_schema_drift(string json)
    {
        var parsed = CopilotParser.ParseUsage(json)!;

        Assert.Empty(parsed.Windows);
        Assert.Null(parsed.Copilot);
    }
}

public class CredentialBlobTests
{
    [Fact]
    public void Decodes_utf8() =>
        Assert.Equal("gho_abc123", CredentialManager.DecodeBlob(Encoding.UTF8.GetBytes("gho_abc123")));

    [Fact]
    public void Decodes_utf16() =>
        Assert.Equal("gho_abc123", CredentialManager.DecodeBlob(Encoding.Unicode.GetBytes("gho_abc123")));

    [Fact]
    public void Decodes_even_length_utf8() =>
        Assert.Equal("gho_abcd", CredentialManager.DecodeBlob(Encoding.UTF8.GetBytes("gho_abcd")));
}
