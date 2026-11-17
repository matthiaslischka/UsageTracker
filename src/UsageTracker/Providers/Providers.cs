using System.Net;
using System.Net.Http.Headers;
using UsageTracker.Core;
using UsageTracker.Infrastructure;

namespace UsageTracker.Providers;

public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>The provider's own web page that shows this quota.</summary>
    string DashboardUrl { get; }

    Task<ProviderUsage> FetchAsync(CancellationToken ct);
}

/// <summary>
/// Shared request plumbing. Credentials are re-read on every poll and never written,
/// refreshed or logged.
/// </summary>
internal abstract class HttpUsageProvider(HttpClient http, TimeProvider clock) : IUsageProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string DashboardUrl { get; }
    protected TimeProvider Clock { get; } = clock;

    private static readonly TimeSpan OwnerCacheTime = TimeSpan.FromHours(6);
    private string? _owner;
    private DateTimeOffset _ownerFetchedAt;

    protected abstract string ExpiredHint { get; }

    public abstract Task<ProviderUsage> FetchAsync(CancellationToken ct);

    protected async Task<ProviderUsage> GetAsync(
        HttpRequestMessage request, Func<string, ProviderUsage?> parse, CancellationToken ct)
    {
        try
        {
            using (request)
            using (var response = await http.SendAsync(request, ct))
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return ProviderUsage.Expired(Id, DisplayName, ExpiredHint);
                if (!response.IsSuccessStatusCode)
                    return Fail(response.StatusCode == HttpStatusCode.TooManyRequests
                        ? "rate limited (HTTP 429)"
                        : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

                var body = await response.Content.ReadAsStringAsync(ct);
                return parse(body) ?? Fail("unexpected response format");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("request timed out");
        }
        catch (HttpRequestException ex)
        {
            return Fail($"network error: {ex.HttpRequestError}");
        }
    }

    /// <summary>
    /// Adds the seat owner to a successful result. The owner rarely changes, so a lookup is
    /// cached for a few hours; a failed lookup keeps the last known owner and retries next poll.
    /// </summary>
    protected async Task<ProviderUsage> WithOwnerAsync(
        ProviderUsage usage, Func<HttpRequestMessage> request, Func<string, string?> parse, CancellationToken ct)
    {
        if (usage.Status != UsageStatus.Ok) return usage;
        if (_owner is null || Clock.GetUtcNow() - _ownerFetchedAt > OwnerCacheTime)
        {
            var body = await TryGetBodyAsync(request(), ct);
            if (body is not null && parse(body) is { } owner)
            {
                _owner = owner;
                _ownerFetchedAt = Clock.GetUtcNow();
            }
        }
        return _owner is null ? usage : usage with { Owner = _owner };
    }

    /// <summary>Best-effort GET for optional details: any failure just yields null.</summary>
    private async Task<string?> TryGetBodyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using (request)
            using (var response = await http.SendAsync(request, ct))
            {
                if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync(ct);
                Log.Write($"{Id}: owner lookup HTTP {(int)response.StatusCode}");
                return null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Log.Write($"{Id}: owner lookup failed: {ex.GetType().Name}");
            return null;
        }
    }

    protected ProviderUsage Fail(string message)
    {
        Log.Write($"{Id}: {message}");
        return ProviderUsage.Failed(Id, DisplayName, message);
    }

    protected ProviderUsage Ok(ParsedUsage parsed) => parsed.Windows.Count == 0
        ? Fail("response contained no usage limits")
        : new(Id, DisplayName, UsageStatus.Ok, parsed.Plan, parsed.Windows,
            LastUpdated: Clock.GetUtcNow(), Copilot: parsed.Copilot, Owner: parsed.Owner);

    protected static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed class ClaudeProvider(HttpClient http, TimeProvider clock) : HttpUsageProvider(http, clock)
{
    public override string Id => "claude";
    public override string DisplayName => "Claude";
    public override string DashboardUrl => "https://claude.ai/settings/usage";
    protected override string ExpiredHint => "token expired — open Claude Code once to refresh";

    private static string CredentialsPath => Path.Combine(AppPaths.Home, ".claude", ".credentials.json");

    public override async Task<ProviderUsage> FetchAsync(CancellationToken ct)
    {
        var creds = ClaudeParser.ParseCredentials(TryReadFile(CredentialsPath));
        if (creds is null)
            return ProviderUsage.NotSignedIn(Id, DisplayName, "not signed in — run Claude Code and log in");
        if (creds.ExpiresAt is { } exp && exp <= Clock.GetUtcNow())
            return ProviderUsage.Expired(Id, DisplayName, ExpiredHint);

        var usage = await GetAsync(Request("usage", creds.AccessToken), body =>
            ClaudeParser.ParseUsage(body, creds.SubscriptionType) is { } p ? Ok(p) : null, ct);
        return await WithOwnerAsync(usage, () => Request("profile", creds.AccessToken), ClaudeParser.ParseOwner, ct);
    }

    private static HttpRequestMessage Request(string endpoint, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.anthropic.com/api/oauth/{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.0 (external, cli)");
        return request;
    }
}

internal sealed class CodexProvider(HttpClient http, TimeProvider clock) : HttpUsageProvider(http, clock)
{
    public override string Id => "codex";
    public override string DisplayName => "Codex";
    public override string DashboardUrl => "https://chatgpt.com/codex/settings/usage";
    protected override string ExpiredHint => "token expired — open Codex once to refresh";

    private static string AuthPath => Path.Combine(AppPaths.Home, ".codex", "auth.json");

    public override async Task<ProviderUsage> FetchAsync(CancellationToken ct)
    {
        var creds = CodexParser.ParseCredentials(TryReadFile(AuthPath));
        if (creds is null)
            return ProviderUsage.NotSignedIn(Id, DisplayName, "not signed in — run Codex and log in with ChatGPT");

        var usage = await GetAsync(Request("wham/usage", creds), body =>
            CodexParser.ParseUsage(body, Clock.GetUtcNow()) is { } p ? Ok(p) : null, ct);
        return await WithOwnerAsync(usage, () => Request("accounts/check/v4-2023-04-27", creds),
            body => CodexParser.ParseOwner(body, creds.AccountId), ct);
    }

    private static HttpRequestMessage Request(string path, CodexCredentials creds)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://chatgpt.com/backend-api/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        if (!string.IsNullOrEmpty(creds.AccountId))
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", creds.AccountId);
        request.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs");
        return request;
    }
}

internal sealed class CopilotProvider(HttpClient http, TimeProvider clock, CopilotAccount account)
    : HttpUsageProvider(http, clock)
{
    public CopilotAccount Account { get; } = account;
    public string AccountKey => $"{Account.Login}@{Account.Host}";

    public override string Id => $"copilot:{AccountKey}";
    public override string DisplayName => "Copilot";
    public override string DashboardUrl => $"https://{Account.Host}/settings/copilot";
    protected override string ExpiredHint => $"token rejected — sign in to {Account.Host} again with Git (Credential Manager)";

    public override Task<ProviderUsage> FetchAsync(CancellationToken ct)
    {
        var token = CredentialManager.ReadGeneric($"git:https://{Account.Login}@{Account.Host}");
        if (string.IsNullOrEmpty(token))
            return Task.FromResult(ProviderUsage.NotSignedIn(Id, DisplayName,
                $"no Git credential for {Account.Login}@{Account.Host}"));

        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.{Account.Host}/copilot_internal/user");
        request.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.2");
        request.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.26.7");
        request.Headers.TryAddWithoutValidation("X-Github-Api-Version", "2025-04-01");
        request.Headers.Accept.ParseAdd("application/json");

        return GetAsync(request, body =>
            CopilotParser.ParseUsage(body) is { } p ? Ok(p) : null, ct);
    }
}
