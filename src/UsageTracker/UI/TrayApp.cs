using System.Diagnostics;
using System.Net;
using Microsoft.Win32;
using UsageTracker.Core;
using UsageTracker.Infrastructure;
using UsageTracker.Providers;

namespace UsageTracker.UI;

/// <summary>Owns the tray icons, polling loop, settings watcher and flyout.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan BackoffCap = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ManualRefreshThrottle = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly List<Runtime> _runtimes = [];
    private readonly FlyoutForm _flyout = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 10_000 };
    private readonly System.Windows.Forms.Timer _settingsDebounce = new() { Interval = 400 };
    private readonly FileSystemWatcher _watcher;
    private readonly AppState _state;
    private AppSettings _settings;
    private int _iconSize;

    private sealed class Runtime(IUsageProvider provider, NotifyIcon icon)
    {
        public IUsageProvider Provider { get; } = provider;
        public NotifyIcon Icon { get; } = icon;
        public ProviderUsage? Latest { get; set; }
        public CopilotBudgetResult? Budget { get; set; }
        public int Failures { get; set; }
        public DateTimeOffset NextDue { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastAttempt { get; set; } = DateTimeOffset.MinValue;
        public bool InFlight { get; set; }
        public Icon? CurrentImage { get; set; }
    }

    public TrayApp()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        _settings = SettingsStore.Load(out var firstRun) ?? new AppSettings();
        if (firstRun) TrySetStartup(true);
        else TryRun(StartupRegistration.RefreshPathIfEnabled);
        _state = StateStore.Load();
        _iconSize = Native.SmallIconSize();

        _menu.Items.Add("Refresh", null, (_, _) => RefreshAll(manual: true));
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => TrySetStartup(!_startupItem!.Checked));
        _menu.Items.Add(_startupItem);
        _menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => Quit());
        _menu.Opening += (_, _) => _startupItem.Checked = TryGet(StartupRegistration.IsEnabled);

        _flyout.RefreshClicked += (_, _) => RefreshAll(manual: true);
        _flyout.SettingsClicked += (_, _) => { _flyout.HideFlyout(); OpenSettings(); };
        _flyout.QuitClicked += (_, _) => Quit();

        _watcher = new FileSystemWatcher(AppPaths.Directory, "settings.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        // Watcher events arrive on a thread-pool thread; hop to the UI thread and debounce.
        FileSystemEventHandler onChange = (_, _) => _flyout.BeginInvoke(() => { _settingsDebounce.Stop(); _settingsDebounce.Start(); });
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Renamed += (s, e) => onChange(s, e);
        _settingsDebounce.Tick += (_, _) => { _settingsDebounce.Stop(); ReloadSettings(); };

        // The flyout's handle is our UI-thread marshalling target.
        _ = _flyout.Handle;
        _watcher.EnableRaisingEvents = true;

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        ReconcileProviders();
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        OnTick();
    }

    // ---- providers & polling --------------------------------------------------------------

    private IEnumerable<IUsageProvider> DesiredProviders()
    {
        if (_settings.Providers.Claude) yield return new ClaudeProvider(_http, _clock);
        if (_settings.Providers.Codex) yield return new CodexProvider(_http, _clock);
        if (_settings.Providers.Copilot)
        {
            foreach (var account in _settings.Copilot.Accounts
                         .Where(a => !string.IsNullOrWhiteSpace(a.Host) && !string.IsNullOrWhiteSpace(a.Login))
                         .DistinctBy(a => $"{a.Login}@{a.Host}".ToLowerInvariant()))
                yield return new CopilotProvider(_http, _clock, account);
        }
    }

    /// <summary>Adds and removes tray icons so they match the enabled providers.</summary>
    private void ReconcileProviders()
    {
        var desired = DesiredProviders().ToList();
        foreach (var rt in _runtimes.Where(r => desired.All(d => d.Id != r.Provider.Id)).ToList())
        {
            rt.Icon.Visible = false;
            rt.Icon.Dispose();
            rt.CurrentImage?.Dispose();
            _runtimes.Remove(rt);
        }

        // Re-create in the desired order so icons keep a stable left-to-right order.
        var ordered = new List<Runtime>();
        foreach (var provider in desired)
        {
            var rt = _runtimes.FirstOrDefault(r => r.Provider.Id == provider.Id);
            if (rt is null)
            {
                var icon = new NotifyIcon { ContextMenuStrip = _menu, Text = provider.DisplayName };
                icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
                rt = new Runtime(provider, icon);
                UpdateIcon(rt);
                icon.Visible = true;
            }
            ordered.Add(rt);
        }
        _runtimes.Clear();
        _runtimes.AddRange(ordered);
    }

    private void OnTick()
    {
        var now = _clock.GetUtcNow();
        foreach (var rt in _runtimes)
        {
            if (!rt.InFlight && now >= rt.NextDue) _ = PollAsync(rt);
            else UpdateIcon(rt); // keep tooltip countdowns fresh
        }
        if (_flyout.Visible) PushFlyout();
    }

    private void RefreshAll(bool manual)
    {
        var now = _clock.GetUtcNow();
        foreach (var rt in _runtimes)
        {
            if (rt.InFlight) continue;
            if (manual && now - rt.LastAttempt < ManualRefreshThrottle) continue;
            _ = PollAsync(rt);
        }
        PushFlyout();
    }

    private async Task PollAsync(Runtime rt)
    {
        rt.InFlight = true;
        rt.LastAttempt = _clock.GetUtcNow();
        PushFlyout();

        ProviderUsage result;
        try
        {
            result = await rt.Provider.FetchAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write($"{rt.Provider.Id}: unexpected {ex.GetType().Name}: {ex.Message}");
            result = ProviderUsage.Failed(rt.Provider.Id, rt.Provider.DisplayName, "unexpected error (see log.txt)");
        }

        rt.InFlight = false;
        if (!_runtimes.Contains(rt)) return; // provider was disabled while polling

        rt.Failures = result.Status == UsageStatus.Error ? rt.Failures + 1 : 0;
        rt.NextDue = _clock.GetUtcNow() + UsageMath.NextPollDelay(_settings.PollInterval, rt.Failures, BackoffCap);

        // Keep the last good numbers visible in the flyout when a poll fails.
        if (result.Status == UsageStatus.Error && rt.Latest is { Windows.Count: > 0 } prev)
        {
            result = result with
            {
                PlanName = prev.PlanName,
                Windows = prev.Windows,
                LastUpdated = prev.LastUpdated,
                Copilot = prev.Copilot,
            };
        }
        rt.Latest = result;

        if (result.Status == UsageStatus.Ok)
        {
            ComputeBudget(rt, persistSnapshot: true);
            EvaluateAlerts(rt);
            StateStore.Save(_state);
        }

        UpdateIcon(rt);
        PushFlyout();
    }

    private void ComputeBudget(Runtime rt, bool persistSnapshot)
    {
        if (rt.Provider is not CopilotProvider copilot || rt.Latest?.Copilot is not { } quota)
        {
            rt.Budget = null;
            return;
        }
        var today = DateOnly.FromDateTime(DateTime.Now);
        _state.CopilotSnapshots.TryGetValue(copilot.AccountKey, out var snapshot);
        rt.Budget = CopilotBudget.Compute(snapshot, quota, today, _settings.Copilot.ExcludeWeekends);
        if (persistSnapshot) _state.CopilotSnapshots[copilot.AccountKey] = rt.Budget.Snapshot;
    }

    private void EvaluateAlerts(Runtime rt)
    {
        var usage = rt.Latest!;
        var toasts = _settings.Toasts;

        foreach (var w in usage.Windows)
        {
            var decision = AlertTracker.Evaluate(_state.Alerts, $"{usage.ProviderId}|{w.Name}",
                AlertTracker.PeriodId(w.ResetsAt), w.PercentRemaining, toasts.RemainingThresholds,
                AlertTracker.BelowRemaining);

            if (decision.NewlyFired.Count > 0)
            {
                var threshold = decision.NewlyFired.Min();
                Toast(rt, $"{usage.DisplayName}: {w.Name} below {threshold:0}%",
                    $"{w.PercentRemaining:0}% left, {UsageMath.ResetCountdown(w.ResetsAt, _clock.GetUtcNow())}.");
            }
            else if (decision.PeriodReset && toasts.ResetToast)
            {
                Toast(rt, $"{usage.DisplayName}: {w.Name} reset", $"{w.PercentRemaining:0}% available again.", ToolTipIcon.Info);
            }
        }

        if (rt.Budget is { PercentOfBudgetUsed: { } usedPct, TodayBudget: { } budget } b)
        {
            var decision = AlertTracker.Evaluate(_state.Alerts, $"{usage.ProviderId}|daily",
                b.Snapshot.Day.ToString("yyyy-MM-dd"), usedPct, toasts.CopilotDailyUsedThresholds,
                AlertTracker.ReachedUsed);
            if (decision.NewlyFired.Count > 0)
            {
                var threshold = decision.NewlyFired.Max();
                Toast(rt, $"Copilot: {threshold:0}% of today's budget used",
                    $"{b.UsedToday} of {budget:0.#} premium requests used today.");
            }
        }
    }

    private void Toast(Runtime rt, string title, string text, ToolTipIcon icon = ToolTipIcon.Warning)
    {
        if (!_settings.Toasts.Enabled) return;
        rt.Icon.ShowBalloonTip(10_000, title, text, icon);
    }

    // ---- icon & tooltip ---------------------------------------------------------------------

    private void UpdateIcon(Runtime rt)
    {
        var u = rt.Latest;
        var (kind, pct) = u?.Status switch
        {
            UsageStatus.Ok when rt.Budget is { } b => (BadgeKind.Percent, b.IconPercentRemaining),
            UsageStatus.Ok when u.TightestWindow is { } w => (BadgeKind.Percent, w.PercentRemaining),
            UsageStatus.NotSignedIn => (BadgeKind.NotSignedIn, 0d),
            _ => (BadgeKind.Unknown, 0d),
        };

        var image = TrayIconRenderer.Render(kind, pct, _iconSize);
        rt.Icon.Icon = image;
        rt.CurrentImage?.Dispose();
        rt.CurrentImage = image;
        rt.Icon.Text = Truncate(Tooltip(rt), 127);
    }

    private string Tooltip(Runtime rt)
    {
        var name = rt.Provider.DisplayName;
        if (rt.Provider is CopilotProvider c && _settings.Copilot.Accounts.Count > 1) name += $" ({c.Account.Login})";
        var u = rt.Latest;
        var now = _clock.GetUtcNow();

        if (u is null) return $"{name}: loading…";
        switch (u.Status)
        {
            case UsageStatus.NotSignedIn: return $"{name}: not signed in";
            case UsageStatus.TokenExpired: return $"{name}: {u.Message}";
            case UsageStatus.Error: return $"{name}: {u.Message}";
        }

        if (u.Copilot is { } q)
        {
            var month = $"Month: {q.Remaining}/{q.Entitlement} left";
            if (q.OverageCount > 0) month += $", overage {q.OverageCount}";
            if (rt.Budget is { TodayBudget: { } budget } b)
                return $"{name}: {b.IconPercentRemaining:0}% of today's budget left\nToday: {b.UsedToday}/{budget:0.#} used\n{month}";
            return $"{name}: {q.PercentRemaining:0}% of month left\nNo daily budget today\n{month}";
        }

        var w = u.TightestWindow!;
        return $"{name}: {w.PercentRemaining:0}% left\n{w.Name} · {UsageMath.ResetCountdown(w.ResetsAt, now)}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---- flyout -----------------------------------------------------------------------------

    private void ToggleFlyout()
    {
        if (_flyout.Visible)
        {
            _flyout.HideFlyout();
            return;
        }
        // Clicking the icon while the flyout is open first deactivates (hides) it; don't reopen.
        if ((DateTime.UtcNow - _flyout.LastHiddenUtc).TotalMilliseconds < 300) return;

        PushFlyout(force: true);
        _flyout.ShowAt(Cursor.Position);
        RefreshAll(manual: true);
    }

    private void PushFlyout(bool force = false)
    {
        if (!force && !_flyout.Visible) return;
        var multiple = _settings.Copilot.Accounts.Count > 1;
        var views = _runtimes.Select(rt => new ProviderView(
            rt.Latest ?? new ProviderUsage(rt.Provider.Id, rt.Provider.DisplayName, UsageStatus.Ok, Message: "loading…"),
            rt.Budget,
            rt.Provider is CopilotProvider c && multiple ? c.Account.Login : null,
            rt.Provider.DashboardUrl)).ToList();
        _flyout.SetData(views, _runtimes.Any(r => r.InFlight));
    }

    // ---- settings ---------------------------------------------------------------------------

    private void ReloadSettings()
    {
        var loaded = SettingsStore.Load(out _);
        if (loaded is null)
        {
            _runtimes.FirstOrDefault()?.Icon.ShowBalloonTip(5000, "UsageTracker",
                "settings.json is not valid JSON; keeping the previous settings.", ToolTipIcon.Error);
            return;
        }

        var intervalChanged = loaded.PollInterval != _settings.PollInterval;
        _settings = loaded;
        ReconcileProviders();

        foreach (var rt in _runtimes)
        {
            if (intervalChanged && rt.LastAttempt != DateTimeOffset.MinValue)
                rt.NextDue = rt.LastAttempt + UsageMath.NextPollDelay(_settings.PollInterval, rt.Failures, BackoffCap);
            ComputeBudget(rt, persistSnapshot: false);
            UpdateIcon(rt);
        }
        OnTick(); // poll newly added providers right away
    }

    private void OpenSettings()
    {
        if (!File.Exists(AppPaths.Settings)) SettingsStore.Load(out _);
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.Settings) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No editor associated with .json.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.Settings}\"") { UseShellExecute = true });
        }
    }

    private void TrySetStartup(bool enabled) => TryRun(() => StartupRegistration.SetEnabled(enabled));

    private static void TryRun(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Write($"registry: {ex.Message}");
        }
    }

    private static bool TryGet(Func<bool> f)
    {
        try { return f(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    // ---- system events & shutdown -----------------------------------------------------------

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _flyout.BeginInvoke(() =>
        {
            _iconSize = Native.SmallIconSize();
            foreach (var rt in _runtimes) UpdateIcon(rt);
        });
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
            _flyout.BeginInvoke(() => { if (_flyout.Visible) _flyout.ShowAt(Cursor.Position); });
    }

    private void Quit()
    {
        _flyout.HideFlyout();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _tick.Stop();
        foreach (var rt in _runtimes)
        {
            rt.Icon.Visible = false;
            rt.Icon.Dispose();
            rt.CurrentImage?.Dispose();
        }
        _runtimes.Clear();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _watcher.Dispose();
            _tick.Dispose();
            _settingsDebounce.Dispose();
            _flyout.Dispose();
            _menu.Dispose();
            _http.Dispose();
        }
        base.Dispose(disposing);
    }
}
