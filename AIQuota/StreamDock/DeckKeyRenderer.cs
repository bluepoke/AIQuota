using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AIQuota.StreamDock;

/// <summary>
/// Renders the same session/weekly/credit usage picture as <see cref="TrayIconFactory"/>,
/// but as an opaque 200x200 PNG sized for a physical Stream Dock key face instead of a
/// tiny transparent tray icon - large enough to also print the percentages as text. Always
/// dark, since a deck key isn't affected by the Windows taskbar theme.
/// </summary>
internal static class DeckKeyRenderer
{
    private const int Size = 200;
    private static readonly Color Background = Color.FromArgb(255, 28, 28, 30);
    private static readonly Color Foreground = Color.FromArgb(255, 235, 235, 235);

    public static byte[] Render(DeckStatusSnapshot snapshot)
    {
        using var bitmap = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Background);

            if (!snapshot.LoggedIn)
            {
                DrawGlyphState(g, "?", Color.FromArgb(255, 150, 150, 150));
            }
            else if (snapshot.IsWarning)
            {
                DrawGlyphState(g, "!", Color.FromArgb(255, 240, 170, 60));
            }
            else
            {
                if (snapshot.CreditPercent is { } credit)
                {
                    DrawBar(g, "SESSION", snapshot.SessionPercent, new Rectangle(14, 44, Size - 28, 16));
                    DrawBar(g, "WEEK", snapshot.WeeklyPercent, new Rectangle(14, 90, Size - 28, 16));
                    DrawBar(g, "CREDIT", credit, new Rectangle(14, 136, Size - 28, 16));
                }
                else
                {
                    DrawBar(g, "SESSION", snapshot.SessionPercent, new Rectangle(14, 66, Size - 28, 18));
                    DrawBar(g, "WEEK", snapshot.WeeklyPercent, new Rectangle(14, 118, Size - 28, 18));
                }

                if (snapshot.SessionRemainingFraction is { } fraction)
                    DrawSessionRing(g, fraction);
            }

            if (snapshot.IsRefreshing)
            {
                using var dim = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                g.FillRectangle(dim, 0, 0, Size, Size);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>One labelled, coloured progress bar with its percentage printed to the right.</summary>
    private static void DrawBar(Graphics g, string label, int percent, Rectangle barRect)
    {
        var clamped = Math.Clamp(percent, 0, 100);

        using (var labelFont = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var labelBrush = new SolidBrush(Color.FromArgb(200, 190, 190, 190)))
            g.DrawString(label, labelFont, labelBrush, barRect.X, barRect.Y - 17);

        var radius = barRect.Height / 2;
        using (var trackBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
        using (var trackPath = RoundedRect(barRect, radius))
            g.FillPath(trackBrush, trackPath);

        var fillWidth = Math.Max((int)Math.Round(barRect.Width * clamped / 100.0), clamped > 0 ? radius * 2 : 0);
        if (fillWidth > 0)
        {
            var fillRect = new Rectangle(barRect.X, barRect.Y, Math.Min(fillWidth, barRect.Width), barRect.Height);
            using var fillPath = RoundedRect(fillRect, radius);
            using var fillBrush = new SolidBrush(ColorForPercent(clamped));
            g.FillPath(fillBrush, fillPath);
        }

        using var percentFont = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var percentBrush = new SolidBrush(Foreground);
        var text = $"{clamped}%";
        var textSize = g.MeasureString(text, percentFont);
        g.DrawString(text, percentFont, percentBrush,
            barRect.X + (barRect.Width - textSize.Width) / 2f,
            barRect.Y + (barRect.Height - textSize.Height) / 2f);
    }

    /// <summary>Thin ring hugging the key's edge, showing how much of the 5-hour session
    /// window is left - simpler than the tray icon's segmented rounded-square version since
    /// there's no tiny-pixel legibility constraint here.</summary>
    private static void DrawSessionRing(Graphics g, double remainingFraction)
    {
        const float penWidth = 5f;
        var rect = new RectangleF(penWidth / 2f, penWidth / 2f, Size - penWidth, Size - penWidth);
        var sweep = 360f * (float)Math.Clamp(remainingFraction, 0.0, 1.0);
        if (sweep <= 0f)
            return;

        using var pen = new Pen(Color.FromArgb(220, 255, 255, 255), penWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, rect, -90f, sweep);
    }

    private static void DrawGlyphState(Graphics g, string glyph, Color color)
    {
        using var font = new Font("Segoe UI", 90f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        var textSize = g.MeasureString(glyph, font);
        g.DrawString(glyph, font, brush, (Size - textSize.Width) / 2f, (Size - textSize.Height) / 2f);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color ColorForPercent(int percent) => percent switch
    {
        > 80 => Color.FromArgb(235, 70, 70),
        > 50 => Color.FromArgb(240, 170, 60),
        _ => Color.FromArgb(90, 190, 110),
    };
}
