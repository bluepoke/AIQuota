using System.Drawing.Drawing2D;

namespace AIQuota;

/// <summary>
/// Renders the small tray icon on the fly (two stacked progress bars - session on top,
/// weekly below) so we don't need to ship .ico assets or update them on every value
/// change. At real tray size (commonly 16x16px) two legible 2-digit numbers don't fit,
/// so the bars themselves - fill length plus colour - carry the at-a-glance signal;
/// exact percentages are in the tooltip and context menu instead.
/// </summary>
public static class TrayIconFactory
{
    public static Icon CreateUsageIcon(int sessionPercent, int weeklyPercent, bool updateAvailable = false)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            DrawBar(g, sessionPercent, new Rectangle(1, 2, size - 2, 13));
            DrawBar(g, weeklyPercent, new Rectangle(1, 17, size - 2, 13));

            if (updateAvailable)
                DrawUpdateBadge(g, size);
        }

        return ToIcon(bitmap);
    }

    /// <summary>Same usage bars as <see cref="CreateUsageIcon"/>, dimmed and overlaid with a
    /// spinning-refresh glyph, shown while a fetch is in flight.</summary>
    public static Icon CreateRefreshingIcon(int sessionPercent, int weeklyPercent, bool updateAvailable = false)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            DrawBar(g, sessionPercent, new Rectangle(1, 2, size - 2, 13));
            DrawBar(g, weeklyPercent, new Rectangle(1, 17, size - 2, 13));

            using var dimBrush = new SolidBrush(Color.FromArgb(150, 20, 20, 20));
            g.FillRectangle(dimBrush, 0, 0, size, size);

            DrawRefreshGlyph(g, size);

            if (updateAvailable)
                DrawUpdateBadge(g, size);
        }

        return ToIcon(bitmap);
    }

    public static Icon CreateUnavailableIcon(bool updateAvailable = false)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var pen = new Pen(Color.FromArgb(180, 200, 200, 200), 4f);
            var rect = new RectangleF(3, 3, size - 6, size - 6);
            g.DrawEllipse(pen, rect);

            using var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.FromArgb(220, 200, 200, 200));
            var textSize = g.MeasureString("?", font);
            g.DrawString("?", font, textBrush,
                (size - textSize.Width) / 2f,
                (size - textSize.Height) / 2f);

            if (updateAvailable)
                DrawUpdateBadge(g, size);
        }

        return ToIcon(bitmap);
    }

    /// <summary>Warning-triangle glyph, shown when a fetch fails with a network error
    /// (e.g. no internet connection) so it's visually distinct from the "?" unavailable
    /// icon (not logged in) and the spinning refresh icon (fetch in flight).</summary>
    public static Icon CreateWarningIcon(bool updateAvailable = false)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var color = Color.FromArgb(240, 170, 60);
            using var trianglePath = new GraphicsPath();
            trianglePath.AddPolygon([
                new PointF(size / 2f, 3f),
                new PointF(size - 3f, size - 4f),
                new PointF(3f, size - 4f),
            ]);
            trianglePath.CloseFigure();

            using (var brush = new SolidBrush(color))
                g.FillPath(brush, trianglePath);

            using var textBrush = new SolidBrush(Color.FromArgb(230, 40, 30, 0));
            using var font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            var textSize = g.MeasureString("!", font);
            g.DrawString("!", font, textBrush,
                (size - textSize.Width) / 2f,
                (size - textSize.Height) / 2f + 2f);

            if (updateAvailable)
                DrawUpdateBadge(g, size);
        }

        return ToIcon(bitmap);
    }

    /// <summary>Small yellow dot in the top-right corner flagging that a newer version is
    /// available - drawn last so it sits on top of whatever the base icon already shows.</summary>
    private static void DrawUpdateBadge(Graphics g, int size)
    {
        const float diameter = 11f;
        var rect = new RectangleF(size - diameter - 0.5f, 0.5f, diameter, diameter);

        using var border = new Pen(Color.FromArgb(220, 40, 40, 40), 1.5f);
        using var fill = new SolidBrush(Color.FromArgb(255, 255, 205, 0));
        g.FillEllipse(fill, rect);
        g.DrawEllipse(border, rect);
    }

    private static void DrawBar(Graphics g, int percent, Rectangle rect)
    {
        var clamped = Math.Clamp(percent, 0, 100);

        using (var trackPath = RoundedRect(rect, 4))
        using (var trackBrush = new SolidBrush(Color.FromArgb(127, Color.White)))
            g.FillPath(trackBrush, trackPath);

        var fillWidth = Math.Max((int)Math.Round(rect.Width * clamped / 100.0), clamped > 0 ? 6 : 0);
        if (fillWidth > 0)
        {
            var fillRect = new Rectangle(rect.X, rect.Y, Math.Min(fillWidth, rect.Width), rect.Height);
            using var fillPath = RoundedRect(fillRect, 4);
            using var fillBrush = new SolidBrush(ColorForPercent(clamped));
            g.FillPath(fillBrush, fillPath);
        }
    }

    /// <summary>Draws a clockwise circular-arrow ("refresh") glyph, centered in a square of the given size.</summary>
    private static void DrawRefreshGlyph(Graphics g, int size)
    {
        var center = new PointF(size / 2f, size / 2f);
        var radius = size * 0.30f;
        var rect = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);

        const float startAngle = -50f;
        const float sweepAngle = 280f;

        using var pen = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, rect, startAngle, sweepAngle);

        // Arrowhead at the end of the arc, tangent to the circle in the direction of travel.
        var thetaRad = (startAngle + sweepAngle) * Math.PI / 180.0;
        var cos = (float)Math.Cos(thetaRad);
        var sin = (float)Math.Sin(thetaRad);

        var tip = new PointF(center.X + radius * cos, center.Y + radius * sin);
        var tangent = new PointF(-sin, cos);
        var radial = new PointF(cos, sin);

        const float arrowLength = 7f;
        const float arrowWidth = 6f;

        var back = new PointF(tip.X - arrowLength * tangent.X, tip.Y - arrowLength * tangent.Y);
        var wing1 = new PointF(back.X + arrowWidth / 2f * radial.X, back.Y + arrowWidth / 2f * radial.Y);
        var wing2 = new PointF(back.X - arrowWidth / 2f * radial.X, back.Y - arrowWidth / 2f * radial.Y);

        using var brush = new SolidBrush(Color.White);
        g.FillPolygon(brush, [tip, wing1, wing2]);
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

    private static Icon ToIcon(Bitmap bitmap)
    {
        var hIcon = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            return (Icon)icon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);
}
