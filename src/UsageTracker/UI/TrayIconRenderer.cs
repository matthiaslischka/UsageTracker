using System.Drawing.Drawing2D;
using System.Drawing.Text;
using UsageTracker.Core;

namespace UsageTracker.UI;

internal enum BadgeKind
{
    Percent,
    Unknown,     // error or expired token: grey "?"
    NotSignedIn, // muted "–"
}

internal static class TrayIconRenderer
{
    /// <summary>Draws the badge at <paramref name="size"/> px so it stays crisp at 125–150% DPI.</summary>
    public static Icon Render(BadgeKind kind, double percentRemaining, int size)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var rect = new RectangleF(0, 0, size, size);
            var radius = size * 0.22f;
            string text;
            Color textColor = Color.White;

            switch (kind)
            {
                case BadgeKind.Percent:
                {
                    var pct = Math.Clamp((int)Math.Round(percentRemaining), 0, 100);
                    text = pct.ToString();
                    using var path = Theme.RoundedRect(rect, radius);
                    using var brush = new SolidBrush(Theme.ForSeverity(UsageMath.SeverityFor(pct)));
                    g.FillPath(brush, path);
                    break;
                }
                case BadgeKind.Unknown:
                {
                    text = "?";
                    using var path = Theme.RoundedRect(rect, radius);
                    using var brush = new SolidBrush(Theme.Unknown);
                    g.FillPath(brush, path);
                    break;
                }
                default:
                {
                    text = "–";
                    var inset = Math.Max(1f, size / 16f);
                    var outline = RectangleF.Inflate(rect, -inset / 2, -inset / 2);
                    using var path = Theme.RoundedRect(outline, radius);
                    using var pen = new Pen(Color.FromArgb(160, 128, 128, 128), inset);
                    g.DrawPath(pen, path);
                    textColor = Color.FromArgb(200, 128, 128, 128);
                    break;
                }
            }

            DrawCentered(g, text, size, textColor);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(hIcon);
        }
    }

    private static void DrawCentered(Graphics g, string text, int size, Color color)
    {
        // Largest bold font whose glyphs fit inside the badge with a 1px margin.
        var maxWidth = size - Math.Max(1, size / 12f) * 2;
        var maxHeight = size * 0.92f;
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;

        var em = text.Length >= 3 ? size * 0.62f : size * 0.84f;
        Font? font = null;
        SizeF measured = default;
        for (; em > 4; em -= 0.5f)
        {
            font?.Dispose();
            font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
            measured = g.MeasureString(text, font, PointF.Empty, format);
            if (measured.Width <= maxWidth && measured.Height <= maxHeight) break;
        }

        using (font)
        using (var brush = new SolidBrush(color))
        {
            // GenericTypographic measures the advance; nudge up a little since Segoe UI digits
            // sit low in their line box.
            var x = (size - measured.Width) / 2f;
            var y = (size - measured.Height) / 2f - size * 0.02f;
            g.DrawString(text, font!, brush, new PointF(x, y), format);
        }
    }
}
