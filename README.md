<img src="assets/icon.svg" width="64" height="64" alt="">

# UsageTracker

A small Windows tray app that shows how much of your Claude, Codex and GitHub Copilot subscription allowance is left.

Each provider gets its own tray icon: a coloured badge with the remaining percentage.

| Badge | Meaning |
|---|---|
| green | more than 50% left |
| amber | 20–50% left |
| red | under 20% left |
| grey `?` | request failed, or the token has expired |
| outlined `–` | not signed in |

- **Claude** and **Codex** show their tightest window, meaning the one with the least left (the 5-hour session or the weekly limit). The tooltip names that window and says when it resets.
- **Copilot** shows how much of **today's budget** is left. On a weekend there is no daily budget, so it shows the monthly percentage instead. The tooltip shows today's budget and how many requests are left this month.

Left-click any icon to open the flyout. Opening it also triggers a refresh. Each provider section shows:

- the plan name;
- a progress bar for each window, with "% used" and a reset countdown;
- on weekly bars, a pace tick marking the usage you'd expect at this point if you spread it evenly, plus an "on pace" or "ahead of pace" label;
- for Copilot, today's used/budget and the month's remaining/entitlement, the reset date, and the overage count when there is one.

The flyout's buttons are Refresh, Settings (opens `settings.json` in your default editor) and Quit. Right-click any icon for Refresh, Start with Windows, Settings and Quit.

Windows notifications fire once per window period:

- when a window drops below 20% left, and again below 5%;
- for Copilot, when 80% and 100% of today's budget is used;
- optionally, when a window resets.

## Where the numbers come from

All three endpoints are undocumented. The parsers treat every field as optional, so a schema change shows up as an error message, not a crash.

| Provider | Credential (read-only, re-read on every poll) | Endpoint |
|---|---|---|
| Claude | `~/.claude/.credentials.json` → `claudeAiOauth.accessToken` | `GET https://api.anthropic.com/api/oauth/usage` |
| Codex | `~/.codex/auth.json` → `tokens.access_token`, `tokens.account_id` | `GET https://chatgpt.com/backend-api/wham/usage` |
| Copilot | Windows Credential Manager, generic credential `git:https://{login}@{host}` (written by Git Credential Manager) | `GET https://api.{host}/copilot_internal/user` |

UsageTracker never refreshes, writes or logs tokens. When a token has expired (Claude's `expiresAt` has passed, or any endpoint returns HTTP 401), the icon turns grey and the flyout says "token expired — open Claude Code / Codex once to refresh". Once the CLI has refreshed its token, the next poll picks it up.

### Copilot daily budget

At the first successful poll of each local day, the app records how many premium requests are left (`state.json`). From that snapshot:

```
todayBudget = snapshotRemaining / daysLeft
usedToday   = snapshotRemaining - currentRemaining
icon %      = max(0, 1 - usedToday / todayBudget)
```

`daysLeft` counts Monday to Friday, from today up to the day before the quota reset date. Public holidays are not excluded. With `excludeWeekends: false` it counts every day. If the monthly quota resets or `remaining` goes up, the app takes a new snapshot.

## Build

Requires the .NET 10 SDK (pinned in `global.json`).

```powershell
dotnet build
dotnet test
dotnet publish src/UsageTracker/UsageTracker.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

The publish step produces a single self-contained `publish/UsageTracker.exe`, about 50 MB, with no runtime to install. Copy it anywhere and run it. Only one instance runs at a time.

Layout:

- `src/UsageTracker/Core` holds the pure logic: models, tolerant JSON parsers, budget, pace and backoff math, alert bookkeeping, and settings. It has no WinForms dependencies.
- `src/UsageTracker/Providers` holds one `IUsageProvider` per service.
- `src/UsageTracker/Infrastructure` handles Credential Manager, file paths, the log and the Run key.
- `src/UsageTracker/UI` contains the tray icons, the owner-drawn flyout and the polling loop.
- `tests/UsageTracker.Tests` has xUnit tests for parsing (fixtures in `Fixtures/`) and the budget, pace and alert math.

## Settings

Settings live in `%APPDATA%\UsageTracker\settings.json`. The file is created with defaults on first run, and the app applies changes as soon as you save it. If the JSON is invalid, the app keeps the previous settings and shows a notification.

```json
{
  "pollIntervalMinutes": 5,
  "providers": { "claude": true, "codex": true, "copilot": true },
  "copilot": {
    "accounts": [ { "host": "github.com", "login": "your-login" } ],
    "excludeWeekends": true
  },
  "toasts": {
    "enabled": true,
    "remainingThresholds": [20, 5],
    "copilotDailyUsedThresholds": [80, 100],
    "resetToast": false
  }
}
```

| Field | Default | Meaning |
|---|---|---|
| `pollIntervalMinutes` | `5` | Minutes between polls (1 to 1440). After errors a provider backs off exponentially, up to 30 minutes. |
| `providers.claude` / `.codex` / `.copilot` | `true` | Turn a provider (and its tray icon) on or off. |
| `copilot.accounts` | one GHE.com account | `{host, login}` pairs. Each account gets its own icon. Use `github.com` as the host for public GitHub (the API host becomes `api.github.com`). |
| `copilot.excludeWeekends` | `true` | Spread the Copilot budget over working days only. On a weekend the icon shows the monthly percentage. |
| `toasts.enabled` | `true` | Turn all notifications on or off. |
| `toasts.remainingThresholds` | `[20, 5]` | Notify when a window's remaining % drops below each value. |
| `toasts.copilotDailyUsedThresholds` | `[80, 100]` | Notify when this % of today's Copilot budget has been used. |
| `toasts.resetToast` | `false` | Notify when a window resets. |

**Start with Windows** is a checkbox in the right-click menu. It writes the `UsageTracker` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. It is turned on the first time the app runs, and the path is updated if you move the exe.

Other files in `%APPDATA%\UsageTracker`:

- `state.json`: Copilot daily snapshots and which notifications have already fired.
- `log.txt`: errors only, with no tokens or response bodies, capped at 512 KB.

## Not in v1

Usage history and charts, an installer, token refresh, and a settings form.

Created in [T3 Code](https://t3.codes).
