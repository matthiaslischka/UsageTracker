using System.Net;
using System.Net.Http.Headers;
using UsageTracker.Core;
using UsageTracker.Infrastructure;

namespace UsageTracker.Providers;

public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }
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
    protected TimeProvider Clock { get; } = clock;

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

    protected ProviderUsage Fail(string message)
    {
        Log.Write($"{Id}: {message}");
        return ProviderUsage.Failed(Id, DisplayName, message);
    }

    protected ProviderUsage Ok(ParsedUsage parsed) => parsed.Windows.Count == 0
        ? Fail("response contained no usage limits")
        : new(Id, DisplayName, UsageStatus.Ok, parsed.Plan, parsed.Windows,
            LastUpdated: Clock.GetUtcNow(), Copilot: parsed.Copilot);

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
    protected override string ExpiredHint => "token expired — open Claude Code once to refresh";

    private static string CredentialsPath => Path.Combine(AppPaths.Home, ".claude", ".credentials.json");

    public override Task<ProviderUsage> FetchAsync(CancellationToken ct)
    {
        var creds = ClaudeParser.ParseCredentials(TryReadFile(CredentialsPath));
        if (creds is null)
            return Task.FromResult(ProviderUsage.NotSignedIn(Id, DisplayName, "not signed in — run Claude Code and log in"));
        if (creds.ExpiresAt is { } exp && exp <= Clock.GetUtcNow())
            return Task.FromResult(ProviderUsage.Expired(Id, DisplayName, ExpiredHint));

        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.0 (external, cli)");

        return GetAsync(request, body =>
            ClaudeParser.ParseUsage(body, creds.SubscriptionType) is { } p ? Ok(p) : null, ct);
    }
}

internal sealed class CodexProvider(HttpClient http, TimeProvider clock) : HttpUsageProvider(http, clock)
{
    public override string Id => "codex";
    public override string DisplayName => "Codex";
    protected override string ExpiredHint => "token expired — open Codex once to refresh";

    private static string AuthPath => Path.Combine(AppPaths.Home, ".codex", "auth.json");

    public override Task<ProviderUsage> FetchAsync(CancellationToken ct)
    {
        var creds = CodexParser.ParseCredentials(TryReadFile(AuthPath));
        if (creds is null)
            return Task.FromResult(ProviderUsage.NotSignedIn(Id, DisplayName, "not signed in — run Codex and log in with ChatGPT"));

        var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        if (!string.IsNullOrEmpty(creds.AccountId))
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", creds.AccountId);
        request.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs");

        return GetAsync(request, body =>
            CodexParser.ParseUsage(body, Clock.GetUtcNow()) is { } p ? Ok(p) : null, ct);
    }
}

internal sealed class CopilotProvider(HttpClient http, TimeProvider clock, CopilotAccount account)
    : HttpUsageProvider(http, clock)
{
    public CopilotAccount Account { get; } = account;
    public string AccountKey => $"{Account.Login}@{Account.Host}";

    public override string Id => $"copilot:{AccountKey}";
    public override string DisplayName => "Copilot";
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
