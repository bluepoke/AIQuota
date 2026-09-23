using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AIQuota.StreamDock;

/// <summary>
/// Renders a higher-resolution copy of the tray icon (see <see cref="TrayIconFactory"/>)
/// as an opaque 200x200 PNG sized for a physical Stream Dock key face. Every shape, colour
/// and size ratio is kept identical to the tray icon - every constant below is the tray
/// icon's own constant (drawn at a fixed 32px canvas) multiplied by <see cref="Scale"/> -
/// so this is deliberately a scaled copy of that drawing code rather than a fresh design.
/// The one addition the extra resolution allows is printing each bar's label and
/// percentage as one line of text inside the (now much taller) bar itself.
/// Always dark, since a deck key isn't affected by the Windows taskbar theme - i.e. always
/// the tray icon's "dark taskbar" color branch.
/// </summary>
internal static class DeckKeyRenderer
{
    private const int Size = 200;
    private const float Scale = Size / 32f; // the tray icon is drawn at a fixed 32px

    private static readonly Color Background = Color.FromArgb(255, 28, 28, 30);

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
                DrawUnavailableGlyph(g);
            }
            else if (snapshot.IsWarning)
            {
                DrawWarningGlyph(g);
            }
            else
            {
                DrawBars(g, snapshot.SessionPercent, snapshot.WeeklyPercent, snapshot.CreditPercent);

                if (snapshot.SessionRemainingFraction is { } fraction)
                    DrawSessionRing(g, fraction);

                if (snapshot.IsRefreshing)
                {
                    using var dim = new SolidBrush(Color.FromArgb(150, 20, 20, 20));
                    g.FillRectangle(dim, 0, 0, Size, Size);
                }
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>Same layout as <see cref="TrayIconFactory"/>'s DrawBars: two bars, or three
    /// when a credit percentage is available, stacked in the middle of the icon inset far
    /// enough to leave room for the session countdown ring.</summary>
    private static void DrawBars(Graphics g, int sessionPercent, int weeklyPercent, int? creditPercent)
    {
        if (creditPercent is { } credit)
        {
            DrawBar(g, "Session", sessionPercent, new RectangleF(4 * Scale, 4 * Scale, (Size - 8 * Scale), 7 * Scale));
            DrawBar(g, "Week", weeklyPercent, new RectangleF(4 * Scale, 12 * Scale, (Size - 8 * Scale), 7 * Scale));
            DrawBar(g, "Credit", credit, new RectangleF(4 * Scale, 20 * Scale, (Size - 8 * Scale), 7 * Scale));
        }
        else
        {
            DrawBar(g, "Session", sessionPercent, new RectangleF(4 * Scale, 4 * Scale, (Size - 8 * Scale), 11 * Scale));
            DrawBar(g, "Week", weeklyPercent, new RectangleF(4 * Scale, 17 * Scale, (Size - 8 * Scale), 11 * Scale));
        }
    }

    /// <summary>Same track/fill geometry and colour-for-percent as <see
    /// cref="TrayIconFactory"/>'s DrawBar, plus - since a bar is now tall enough to hold it -
    /// the label and percentage as one centered line ("Session 45%").</summary>
    private static void DrawBar(Graphics g, string label, int percent, RectangleF rect)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var radius = Math.Min(4f * Scale, rect.Height / 2f);

        using (var trackPath = RoundedRect(rect, radius))
        using (var trackBrush = new SolidBrush(Color.FromArgb(127, Color.White)))
            g.FillPath(trackBrush, trackPath);

        var fillWidth = Math.Max(rect.Width * clamped / 100f, clamped > 0 ? 6f * Scale : 0f);
        if (fillWidth > 0)
        {
            var fillRect = new RectangleF(rect.X, rect.Y, Math.Min(fillWidth, rect.Width), rect.Height);
            using var fillPath = RoundedRect(fillRect, radius);
            using var fillBrush = new SolidBrush(ColorForPercent(clamped));
            g.FillPath(fillBrush, fillPath);
        }

        using var font = new Font("Segoe UI", Math.Min(rect.Height * 0.42f, 34f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString($"{label} {clamped}%", font, textBrush, rect, format);
    }

    /// <summary>Exact copy of <see cref="TrayIconFactory"/>'s countdown ring - a gapped,
    /// 5-segment outline tracing a rounded square, clockwise from top-center - scaled up;
    /// see that method for the full rationale. Always the "dark taskbar" (white) colour
    /// branch, since a deck key has no taskbar theme to match.</summary>
    private static void DrawSessionRing(Graphics g, double remainingFraction)
    {
        const float penWidth = 2.4f * Scale;
        const float radius = 8f * Scale;
        const int segmentCount = 5;
        const float gap = 4f * Scale;

        var rect = new RectangleF(penWidth / 2f, penWidth / 2f, Size - penWidth, Size - penWidth);

        using var fullPath = ClockwiseRoundedSquarePath(rect, radius);
        fullPath.Flatten(null, 0.2f);
        var points = fullPath.PathPoints;

        var totalLength = PolylineLength(points);
        var segmentLength = totalLength / segmentCount;
        var targetLength = totalLength * (float)Math.Clamp(remainingFraction, 0.0, 1.0);

        using var fillPen = new Pen(Color.FromArgb(235, Color.White), penWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        for (var i = 0; i < segmentCount; i++)
        {
            var segmentStart = i * segmentLength + gap / 2f;
            var segmentEnd = (i + 1) * segmentLength - gap / 2f;
            if (segmentEnd <= segmentStart)
                continue;
            if (segmentStart >= targetLength)
                continue;

            var filledEnd = Math.Min(segmentEnd, targetLength);
            var fill = ExtractRange(points, segmentStart, filledEnd);
            if (fill.Count >= 2)
                g.DrawLines(fillPen, fill.ToArray());
        }
    }

    private static GraphicsPath ClockwiseRoundedSquarePath(RectangleF rect, float radius)
    {
        var d = radius * 2f;
        var topMid = new PointF(rect.X + rect.Width / 2f, rect.Y);

        var path = new GraphicsPath();
        path.AddLine(topMid, new PointF(rect.Right - radius, rect.Y));
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddLine(new PointF(rect.Right, rect.Y + radius), new PointF(rect.Right, rect.Bottom - radius));
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddLine(new PointF(rect.Right - radius, rect.Bottom), new PointF(rect.X + radius, rect.Bottom));
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.AddLine(new PointF(rect.X, rect.Bottom - radius), new PointF(rect.X, rect.Y + radius));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddLine(new PointF(rect.X + radius, rect.Y), topMid);
        return path;
    }

    private static float PolylineLength(PointF[] points)
    {
        var total = 0f;
        for (var i = 1; i < points.Length; i++)
            total += Distance(points[i - 1], points[i]);
        return total;
    }

    private static List<PointF> ExtractRange(PointF[] points, float from, float to)
    {
        var result = new List<PointF>();
        if (to <= from)
            return result;

        var accumulated = 0f;
        var started = false;
        for (var i = 1; i < points.Length; i++)
        {
            var segmentLength = Distance(points[i - 1], points[i]);
            var segmentStart = accumulated;
            var segmentEnd = accumulated + segmentLength;
            accumulated = segmentEnd;

            if (segmentEnd < from)
                continue;
            if (segmentStart > to)
                break;

            if (!started)
            {
                var startT = segmentLength <= 0f ? 0f : Math.Clamp((from - segmentStart) / segmentLength, 0f, 1f);
                result.Add(Lerp(points[i - 1], points[i], startT));
                started = true;
            }

            if (segmentEnd >= to)
            {
                var endT = segmentLength <= 0f ? 0f : Math.Clamp((to - segmentStart) / segmentLength, 0f, 1f);
                result.Add(Lerp(points[i - 1], points[i], endT));
                break;
            }

            result.Add(points[i]);
        }
        return result;
    }

    private static PointF Lerp(PointF a, PointF b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static float Distance(PointF a, PointF b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Mirrors <see cref="TrayIconFactory.CreateUnavailableIcon"/>'s "dark
    /// taskbar" branch: a grey ring with a "?" centered in it.</summary>
    private static void DrawUnavailableGlyph(Graphics g)
    {
        var greyColor = Color.FromArgb(180, 200, 200, 200);
        using var pen = new Pen(greyColor, 4f * Scale);
        var rect = new RectangleF(3 * Scale, 3 * Scale, Size - 6 * Scale, Size - 6 * Scale);
        g.DrawEllipse(pen, rect);

        var textColor = Color.FromArgb(220, 200, 200, 200);
        using var font = new Font("Segoe UI", 16f * Scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(textColor);
        var textSize = g.MeasureString("?", font);
        g.DrawString("?", font, textBrush, (Size - textSize.Width) / 2f, (Size - textSize.Height) / 2f);
    }

    /// <summary>Mirrors <see cref="TrayIconFactory.CreateWarningIcon"/>: an orange warning
    /// triangle with a "!" centered in it.</summary>
    private static void DrawWarningGlyph(Graphics g)
    {
        var color = Color.FromArgb(240, 170, 60);
        using var trianglePath = new GraphicsPath();
        trianglePath.AddPolygon([
            new PointF(Size / 2f, 3f * Scale),
            new PointF(Size - 3f * Scale, Size - 4f * Scale),
            new PointF(3f * Scale, Size - 4f * Scale),
        ]);
        trianglePath.CloseFigure();

        using (var brush = new SolidBrush(color))
            g.FillPath(brush, trianglePath);

        using var textBrush = new SolidBrush(Color.FromArgb(230, 40, 30, 0));
        using var font = new Font("Segoe UI", 15f * Scale, FontStyle.Bold, GraphicsUnit.Pixel);
        var textSize = g.MeasureString("!", font);
        g.DrawString("!", font, textBrush,
            (Size - textSize.Width) / 2f,
            (Size - textSize.Height) / 2f + 2f * Scale);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var d = radius * 2f;
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
