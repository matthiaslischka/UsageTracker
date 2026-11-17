# UsageTracker spec

A lightweight Windows system-tray app that polls the remaining subscription allowances for Claude, Codex and GitHub Copilot. This design is final.

## Stack and structure
- C# / **.NET 10 WinForms**. `NotifyIcon` for the tray. Publish as a self-contained single-file exe for win-x64.
- `src/UsageTracker` (app) and `tests/UsageTracker.Tests` (xUnit), plus a `.sln`.
- One `IUsageProvider` per provider, each returning a normalized `ProviderUsage` model: plan name, a list of windows (name, % used, resets at, window length), status (Ok / NotSignedIn / TokenExpired / Error with a message), and last-updated time.
- Keep the parsing and math pure and testable, separate from WinForms.
- Tests cover response parsing (fixtures are the real shapes below; keep them free of personal data) and the budget/pace math. No UI tests.

## Data sources (all undocumented: treat every field as optional and never crash on schema drift)
**Claude**
- Read `~/.claude/.credentials.json` → `claudeAiOauth.accessToken`, `expiresAt` (epoch ms), `subscriptionType`.
- `GET https://api.anthropic.com/api/oauth/usage` with headers `Authorization: Bearer <token>`, `anthropic-beta: oauth-2025-04-20`, `User-Agent: claude-cli/2.0 (external, cli)`.
- Prefer the `limits[]` array. Each item has `kind` (e.g. `session`, `weekly_all`), `group`, `percent` (0–100), `severity`, `resets_at` (ISO), `is_active`.
- If `limits[]` is missing, fall back to `five_hour` and `seven_day` (each has `utilization` 0–100 and `resets_at`). Many other keys are null and should be ignored.
- Sample: `{"five_hour":{"utilization":57.0,"resets_at":"2026-11-17T11:59:59.539857+00:00"},"seven_day":{"utilization":58.0,"resets_at":"2026-11-17T21:59:59.539875+00:00"},"seven_day_opus":null,"extra_usage":{"is_enabled":true,"monthly_limit":0,"used_credits":0.0,"currency":"EUR"},"limits":[{"kind":"session","group":"session","percent":57,"severity":"normal","resets_at":"2026-11-17T11:59:59.539857+00:00","scope":null,"is_active":false},{"kind":"weekly_all","group":"weekly","percent":58,"severity":"normal","resets_at":"2026-11-17T21:59:59.539875+00:00","scope":null,"is_active":true}]}`

**Codex**
- Read `~/.codex/auth.json` → `tokens.access_token`, `tokens.account_id`.
- `GET https://chatgpt.com/backend-api/wham/usage` with headers `Authorization: Bearer <token>`, `ChatGPT-Account-Id: <account_id>`, `User-Agent: codex_cli_rs`.
- Sample: `{"plan_type":"plus","rate_limit":{"allowed":true,"limit_reached":false,"primary_window":{"used_percent":6,"limit_window_seconds":18000,"reset_after_seconds":4781,"reset_at":1794916851},"secondary_window":{"used_percent":1,"limit_window_seconds":604800,"reset_after_seconds":475620,"reset_at":1795387689}},"additional_rate_limits":null,"credits":{"has_credits":false,"balance":"0"}}`

**Copilot** (GitHub Enterprise Cloud account)
- Settings hold a list of accounts `{host, login}`. The default is a single entry `{ "host": "github.com", "login": "your-login" }`.
- The token comes from Windows Credential Manager, generic credential target `git:https://{login}@{host}`. It's a non-expiring `gho_` token stored by Git Credential Manager. Read it with `CredRead` P/Invoke. The blob may be UTF-8 or UTF-16, so handle both.
- `GET https://api.{host}/copilot_internal/user` with headers `Authorization: token <tok>`, `Editor-Version: vscode/1.96.2`, `User-Agent: GitHubCopilotChat/0.26.7`, `X-Github-Api-Version: 2025-04-01`.
- The main metric is `quota_snapshots.premium_interactions`; skip snapshots where `unlimited` is true.
- Sample: `{"login":"<login>","access_type_sku":"copilot_for_business_seat_quota","copilot_plan":"business","quota_reset_date":"2026-12-01","quota_reset_date_utc":"2026-12-01T00:00:00.000Z","quota_snapshots":{"chat":{"entitlement":0,"remaining":0,"percent_remaining":100.0,"unlimited":true},"completions":{"entitlement":0,"remaining":0,"percent_remaining":100.0,"unlimited":true},"premium_interactions":{"entitlement":2500,"remaining":2106,"percent_remaining":84.2,"unlimited":false,"overage_count":0,"overage_permitted":false,"has_quota":true,"timestamp_utc":"2026-11-17T10:41:10.913Z"}}}`
- Show `overage_count` when it's above 0.

**Auth rule: never refresh or write tokens.** On a 401, or when `expiresAt` has passed, set status TokenExpired and grey out the icon, with the hint "token expired — open Claude Code / Codex once to refresh". Re-read the credential files on every poll so a refreshed token is picked up automatically. Never log tokens.

## Tray icons
- One `NotifyIcon` per provider. Each is a 16×16 icon drawn at runtime with GDI+ and must stay crisp at 125–150% DPI (render at the system small-icon size).
- The icon shows the remaining % as a number in a colored badge: green >50, amber 20–50, red <20. Grey with "?" on error or expired token, and a muted "–" when not signed in.
- Claude and Codex show the **tightest window** (lowest remaining across their windows). The tooltip shows that window and the time until it resets.
- Copilot shows **% of today's budget remaining** (see the Copilot daily budget section). Its tooltip shows today's budget and the monthly remaining.

## Flyout
- Left-clicking any icon opens a borderless flyout anchored above the tray (use the taskbar position and work area), styled like the Windows 11 flyouts, light and dark theme following the system. It closes on deactivate.
- It shows all providers. Per provider:
  - plan name;
  - each window as a progress bar with % used and a reset countdown ("resets in 3h 12m");
  - last-updated time and any status message.
- Weekly bars get a **pace marker**: a tick for the expected usage at this point of the window (elapsed fraction), plus a label "on pace" or "ahead of pace".
- Copilot shows both today's budget (used / budget) and the month (remaining / entitlement, reset date).
- Buttons: Refresh; Settings, which opens the settings JSON in the default editor; Quit.
- Opening the flyout triggers an immediate refresh.
- The right-click context menu has Refresh, Start with Windows (checkbox), Settings and Quit.

## Polling
- Every 5 minutes by default (configurable), plus immediately when the flyout opens.
- Exponential backoff per provider after failures, capped at 30 min. Use one shared `HttpClient`.

## Copilot daily budget (rolling, working days)
- Once per local day, record a snapshot of `remaining` at the first successful poll of the day. Store it in `%APPDATA%\UsageTracker\state.json`.
- `todayBudget = snapshotRemaining / workingDaysLeft`, where `workingDaysLeft` counts Mon–Fri from today (inclusive) up to the reset date (exclusive). Public holidays are ignored.
- Setting `copilot.excludeWeekends` defaults to true. When it's false, count all days.
- On a weekend with excludeWeekends on, there is no daily budget: show only the monthly figure and use the monthly % on the icon.
- `usedToday = snapshotRemaining - currentRemaining`. The icon % = `max(0, 1 - usedToday/todayBudget)`.
- If the snapshot is from a previous reset period, or `remaining` went up (a reset happened), take a fresh snapshot.

## Toasts (Windows notifications)
- When any window falls below 20% remaining, and again below 5%.
- For Copilot, also at 80% and 100% of today's budget used.
- Each threshold fires once per window period, so track this in state.json.
- An optional "window reset" toast, off by default.
- Use `NotifyIcon.ShowBalloonTip` or the Windows App SDK / `Microsoft.Toolkit.Uwp.Notifications`, whichever stays lightweight with a single-file publish.

## Settings
- `%APPDATA%\UsageTracker\settings.json`, created with defaults on first run. Watch the file with a `FileSystemWatcher` and apply changes live.
- Fields: poll interval; enabled providers; copilot accounts; excludeWeekends; toast thresholds; resetToast.
- "Start with Windows" uses the HKCU `...\CurrentVersion\Run` value and is toggled from the menu. Default on.

## Out of scope for v1
Usage history and charts, an installer, token refresh, and a settings form.

## Done means
- `dotnet build` and `dotnet test` pass, and `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` produces the exe.
- Run the exe once to confirm that all three icons appear with real numbers.
- README documents what the app does, build/publish steps and the settings reference.
