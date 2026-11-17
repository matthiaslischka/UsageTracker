using System.Diagnostics;
using System.Drawing.Drawing2D;
using UsageTracker.Core;
using UsageTracker.Infrastructure;

namespace UsageTracker.UI;

/// <summary>What the flyout and tooltips show for one provider (or Copilot account).</summary>
internal sealed record ProviderView(ProviderUsage Usage, CopilotBudgetResult? Budget, string? Account, string? DashboardUrl = null);

/// <summary>Borderless Windows 11–style flyout anchored above the tray. Everything is owner-drawn.</summary>
internal sealed class FlyoutForm : Form
{
    private const int LogicalWidth = 360;
    private const int Padding_ = 16;

    private const string LinkPrefix = "link:";
    private const string LinkLabel = "Open ↗";

    private readonly List<(RectangleF Rect, string Id)> _buttons = [];
    private IReadOnlyList<ProviderView> _views = [];
    private bool _refreshing;
    private Theme _theme = Theme.Light;
    private float _scale = 1;
    private Point _anchor;
    private string? _hover;
    private string? _pressed;
    private Fonts? _fonts;

    public event EventHandler? RefreshClicked;
    public event EventHandler? SettingsClicked;
    public event EventHandler? QuitClicked;

    /// <summary>When the flyout last closed; lets a click on the tray icon toggle it shut.</summary>
    public DateTime LastHiddenUtc { get; private set; }

    public FlyoutForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "UsageTracker";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: keep out of Alt+Tab
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => false;

    public void ShowAt(Point anchor)
    {
        _anchor = anchor;
        _scale = Native.DpiAt(anchor) / 96f;
        ApplyTheme();
        Relayout();
        if (!Visible) Show();
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    public void SetData(IReadOnlyList<ProviderView> views, bool refreshing)
    {
        _views = views;
        _refreshing = refreshing;
        if (!Visible) return;
        Relayout();
        Invalidate();
    }

    public void HideFlyout()
    {
        if (!Visible) return;
        Hide();
        LastHiddenUtc = DateTime.UtcNow;
    }

    private void ApplyTheme()
    {
        _theme = Theme.Current();
        BackColor = _theme.Background;
        if (!IsHandleCreated) CreateHandle();
        var dark = _theme.Dark ? 1 : 0;
        var corner = Native.DwmwcpRound;
        var border = ColorTranslator.ToWin32(_theme.Border);
        Native.DwmSetWindowAttribute(Handle, Native.DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        Native.DwmSetWindowAttribute(Handle, Native.DwmwaWindowCornerPreference, ref corner, sizeof(int));
        Native.DwmSetWindowAttribute(Handle, Native.DwmwaBorderColor, ref border, sizeof(int));
    }

    private void Relayout()
    {
        _fonts?.Dispose();
        _fonts = new Fonts(_scale);

        int height;
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        {
            height = (int)Math.Ceiling(Draw(g, measureOnly: true));
        }

        var width = (int)Math.Round(LogicalWidth * _scale);
        var screen = Screen.FromPoint(_anchor);
        var wa = screen.WorkingArea;
        var bounds = screen.Bounds;
        var margin = (int)Math.Round(12 * _scale);
        height = Math.Min(height, wa.Height - 2 * margin);

        int x, y;
        if (wa.Top > bounds.Top) // taskbar at the top
        {
            x = wa.Right - width - margin;
            y = wa.Top + margin;
        }
        else if (wa.Left > bounds.Left) // taskbar on the left
        {
            x = wa.Left + margin;
            y = wa.Bottom - height - margin;
        }
        else // bottom, right, or auto-hide
        {
            x = wa.Right - width - margin;
            y = wa.Bottom - height - margin;
        }

        Bounds = new Rectangle(x, y, width, height);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        _scale = e.DeviceDpiNew / 96f;
        e.Cancel = true; // we lay ourselves out
        Relayout();
        Invalidate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        HideFlyout();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) HideFlyout();
        else if (e.KeyCode == Keys.F5) RefreshClicked?.Invoke(this, EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_theme.Background);
        Draw(e.Graphics, measureOnly: false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit is null ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = _pressed = null;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = HitTest(e.Location);
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var hit = HitTest(e.Location);
        var pressed = _pressed;
        _pressed = null;
        Invalidate();
        if (e.Button == MouseButtons.Left && hit is not null && hit == pressed)
        {
            switch (hit)
            {
                case "refresh": RefreshClicked?.Invoke(this, EventArgs.Empty); break;
                case "settings": SettingsClicked?.Invoke(this, EventArgs.Empty); break;
                case "quit": QuitClicked?.Invoke(this, EventArgs.Empty); break;
                case { } id when id.StartsWith(LinkPrefix, StringComparison.Ordinal): OpenUrl(id[LinkPrefix.Length..]); break;
            }
        }
        base.OnMouseUp(e);
    }

    private string? HitTest(Point p) => _buttons.FirstOrDefault(b => b.Rect.Contains(p)).Id;

    private void OpenUrl(string url)
    {
        HideFlyout();
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"could not open {url}: {ex.Message}");
        }
    }

    // ---- drawing -------------------------------------------------------------------------

    private float S(float v) => v * _scale;

    /// <summary>Lays out and (unless measuring) paints the flyout. Returns the content height.</summary>
    private float Draw(Graphics g, bool measureOnly)
    {
        var f = _fonts ??= new Fonts(_scale);
        var t = _theme;
        var now = DateTimeOffset.Now;
        var pad = S(Padding_);
        var width = LogicalWidth * _scale;
        var inner = width - 2 * pad;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var y = pad;
        _buttons.Clear();

        // Header
        var titleH = Text_(g, "Usage", f.Title, t.Text, pad, y, measureOnly);
        if (_refreshing)
        {
            var w = Measure(g, "Refreshing…", f.Small).Width;
            Text_(g, "Refreshing…", f.Small, t.SubText, width - pad - w, y + (titleH - f.Small.Height) / 2, measureOnly);
        }
        y += titleH + S(8);

        if (_views.Count == 0)
        {
            y += Text_(g, "No providers enabled. Turn them on in Settings.", f.Body, t.SubText, pad, y, measureOnly) + S(8);
        }

        for (var i = 0; i < _views.Count; i++)
        {
            if (i > 0)
            {
                y += S(6);
                if (!measureOnly)
                {
                    using var pen = new Pen(t.Separator, Math.Max(1, S(1)));
                    g.DrawLine(pen, pad, y, width - pad, y);
                }
                y += S(12);
            }
            y = DrawProvider(g, _views[i], pad, y, inner, now, measureOnly);
        }

        // Footer band with buttons
        y += S(12);
        var footerTop = y;
        var footerH = S(56);
        if (!measureOnly)
        {
            using var band = new SolidBrush(t.Dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xEE, 0xEE, 0xEE));
            g.FillRectangle(band, 0, footerTop, width, footerH + S(4));
            using var pen = new Pen(t.Separator, Math.Max(1, S(1)));
            g.DrawLine(pen, 0, footerTop, width, footerTop);
        }

        var bx = width - pad;
        var bh = S(32);
        var by = footerTop + (footerH - bh) / 2;
        foreach (var (id, label) in new[] { ("quit", "Quit"), ("settings", "Settings"), ("refresh", "Refresh") })
        {
            var bw = Math.Max(S(80), Measure(g, label, f.Body).Width + S(24));
            var rect = new RectangleF(bx - bw, by, bw, bh);
            _buttons.Add((rect, id));
            if (!measureOnly) DrawButton(g, rect, label, id);
            bx -= bw + S(8);
        }

        return footerTop + footerH;
    }

    private float DrawProvider(Graphics g, ProviderView view, float x, float y, float width, DateTimeOffset now, bool m)
    {
        var f = _fonts!;
        var t = _theme;
        var u = view.Usage;

        // Name, plan (with owner) and a link to the provider's usage page on the right
        var nameH = Text_(g, u.DisplayName, f.Header, t.Text, x, y, m);
        var bodyY = y + (nameH - f.Body.Height) / 2 + S(1);
        var right = x + width;
        if (!string.IsNullOrEmpty(view.DashboardUrl))
        {
            var id = LinkPrefix + view.DashboardUrl;
            var size = Measure(g, LinkLabel, f.Body);
            var rect = new RectangleF(right - size.Width, bodyY, size.Width, size.Height);
            // Generous hit target; the text itself stays small.
            _buttons.Add((RectangleF.Inflate(rect, S(6), S(4)), id));
            if (!m)
            {
                using var font = _hover == id ? new Font(f.Body, FontStyle.Underline) : null;
                Text_(g, LinkLabel, font ?? f.Body, t.Link, rect.X, rect.Y, m);
            }
            right = rect.X - S(12);
        }

        var plan = string.Join(" · ", new[] { u.PlanLabel, view.Account }.Where(s => !string.IsNullOrEmpty(s)));
        if (plan.Length > 0)
        {
            var planX = x + Measure(g, u.DisplayName, f.Header).Width + S(8);
            TextEllipsis(g, plan, f.Body, t.SubText, planX, bodyY, right - planX, m);
        }
        y += nameH + S(6);

        if (u.Status != UsageStatus.Ok && !string.IsNullOrEmpty(u.Message))
        {
            var color = u.Status == UsageStatus.NotSignedIn ? t.SubText : t.Bar(Severity.Critical);
            y += TextWrapped(g, u.Message!, f.Body, color, x, y, width, m) + S(8);
        }

        if (u.Copilot is { } quota)
        {
            y = DrawCopilot(g, view, quota, x, y, width, now, m);
            foreach (var w in u.Windows.Skip(1)) y = DrawWindow(g, w, x, y, width, now, m);
        }
        else
        {
            foreach (var w in u.Windows) y = DrawWindow(g, w, x, y, width, now, m);
        }

        var updated = u.LastUpdated is { } lu ? $"Updated {lu.ToLocalTime():HH:mm}" : "Not updated yet";
        y += Text_(g, updated, f.Small, t.SubText, x, y, m);
        return y;
    }

    private float DrawCopilot(Graphics g, ProviderView view, CopilotQuota q, float x, float y, float width, DateTimeOffset now, bool m)
    {
        var f = _fonts!;
        var t = _theme;
        var b = view.Budget;

        if (b?.TodayBudget is { } budget)
        {
            var usedPct = b.PercentOfBudgetUsed ?? 0;
            var left = Math.Max(0, budget - b.UsedToday);
            y = DrawBar(g, "Today", $"{b.UsedToday} / {budget:0.#} used", usedPct, 100 - usedPct, null,
                $"{left:0.#} left today · budget set from {b.Snapshot.Remaining} remaining", x, y, width, m);
        }
        else if (b is not null)
        {
            y += Text_(g, "No daily budget on weekends", f.Body, t.SubText, x, y, m) + S(8);
        }

        var reset = q.ResetDate is { } rd ? $"resets {rd.ToString("M")}" : "reset date unknown";
        var window = view.Usage.Windows.FirstOrDefault();
        var countdown = window?.ResetsAt is { } ra ? $" · in {UsageMath.FormatDuration(ra - now)}" : "";
        y = DrawBar(g, "Month", $"{q.Remaining} / {q.Entitlement} left", 100 - q.PercentRemaining, q.PercentRemaining, null,
            reset + countdown, x, y, width, m);

        if (q.OverageCount > 0)
            y += Text_(g, $"Overage: {q.OverageCount} requests", f.Body, t.Bar(Severity.Warning), x, y, m) + S(8);

        return y;
    }

    private float DrawWindow(Graphics g, UsageWindow w, float x, float y, float width, DateTimeOffset now, bool m)
    {
        var pace = w.ShowPace ? UsageMath.Pace(w, now) : null;
        var sub = UsageMath.ResetCountdown(w.ResetsAt, now);
        if (pace is not null) sub += " · " + pace.Label;
        return DrawBar(g, w.Name, $"{w.PercentUsed:0}% used", w.PercentUsed, w.PercentRemaining,
            pace?.ExpectedPercentUsed, sub, x, y, width, m);
    }

    private float DrawBar(Graphics g, string name, string right, double usedPct, double remainingPct,
        double? paceMarker, string subline, float x, float y, float width, bool m)
    {
        var f = _fonts!;
        var t = _theme;

        var h = Text_(g, name, f.Body, t.Text, x, y, m);
        var rw = Measure(g, right, f.Body).Width;
        Text_(g, right, f.Body, t.SubText, x + width - rw, y, m);
        y += h + S(5);

        var barH = S(6);
        if (!m)
        {
            var track = new RectangleF(x, y, width, barH);
            using (var path = Theme.RoundedRect(track, barH / 2))
            using (var brush = new SolidBrush(t.Track))
                g.FillPath(brush, path);

            var fillW = (float)(width * Math.Clamp(usedPct, 0, 100) / 100);
            if (fillW > 0.5f)
            {
                using var path = Theme.RoundedRect(new RectangleF(x, y, Math.Max(fillW, barH), barH), barH / 2);
                using var brush = new SolidBrush(t.Bar(UsageMath.SeverityFor(remainingPct)));
                g.FillPath(brush, path);
            }

            if (paceMarker is { } p)
            {
                var px = x + (float)(width * Math.Clamp(p, 0, 100) / 100);
                using var pen = new Pen(t.PaceTick, Math.Max(1.5f, S(2)));
                g.DrawLine(pen, px, y - S(3), px, y + barH + S(3));
            }
        }
        y += barH + S(5);

        y += Text_(g, subline, f.Small, t.SubText, x, y, m);
        return y + S(10);
    }

    private void DrawButton(Graphics g, RectangleF rect, string label, string id)
    {
        var t = _theme;
        var fill = _pressed == id ? t.ButtonPressed : _hover == id ? t.ButtonHover : t.ButtonFill;
        using (var path = Theme.RoundedRect(rect, S(4)))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var pen = new Pen(t.Border, Math.Max(1, S(1)));
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, label, _fonts!.Body, Rectangle.Round(rect), t.Text, fill,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static Size Measure(Graphics g, string text, Font font) =>
        TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

    private float Text_(Graphics g, string text, Font font, Color color, float x, float y, bool measureOnly)
    {
        var size = Measure(g, text, font);
        if (!measureOnly)
            TextRenderer.DrawText(g, text, font, new Point((int)Math.Round(x), (int)Math.Round(y)), color, _theme.Background,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return Math.Max(size.Height, font.Height);
    }

    /// <summary>Single line clipped with "…" to fit <paramref name="maxWidth"/>.</summary>
    private void TextEllipsis(Graphics g, string text, Font font, Color color, float x, float y, float maxWidth, bool measureOnly)
    {
        if (measureOnly || maxWidth <= 0) return;
        var rect = new Rectangle((int)Math.Round(x), (int)Math.Round(y), (int)maxWidth, font.Height + (int)S(2));
        TextRenderer.DrawText(g, text, font, rect, color, _theme.Background,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private float TextWrapped(Graphics g, string text, Font font, Color color, float x, float y, float width, bool measureOnly)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
        var size = TextRenderer.MeasureText(g, text, font, new Size((int)width, int.MaxValue), flags);
        if (!measureOnly)
            TextRenderer.DrawText(g, text, font, new Rectangle((int)x, (int)y, (int)width, size.Height), color, _theme.Background, flags);
        return size.Height;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fonts?.Dispose();
        base.Dispose(disposing);
    }

    private sealed class Fonts : IDisposable
    {
        public Font Title { get; }
        public Font Header { get; }
        public Font Body { get; }
        public Font Small { get; }

        public Fonts(float scale)
        {
            var family = PickFamily();
            float Px(float pt) => pt * 96f / 72f * scale;
            Title = new Font(family, Px(13), FontStyle.Bold, GraphicsUnit.Pixel);
            Header = new Font(family, Px(10.5f), FontStyle.Bold, GraphicsUnit.Pixel);
            Body = new Font(family, Px(9), FontStyle.Regular, GraphicsUnit.Pixel);
            Small = new Font(family, Px(8), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static string PickFamily()
        {
            foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI" })
            {
                try
                {
                    using var ff = new FontFamily(name);
                    return name;
                }
                catch (ArgumentException) { }
            }
            return SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI";
        }

        public void Dispose()
        {
            Title.Dispose();
            Header.Dispose();
            Body.Dispose();
            Small.Dispose();
        }
    }
}
