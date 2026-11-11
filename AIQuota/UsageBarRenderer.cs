using System.Drawing;
using System.Drawing.Drawing2D;

namespace AIQuota;

/// <summary>Colours of the tray flyout and the mini dashboard for the light and dark system
/// theme, close to the Windows 11 flyouts. Usage colours themselves come from
/// <see cref="UsagePalette"/>.</summary>
internal sealed record FlyoutPalette(Color Background, Color Text, Color Secondary, Color Track, Color Border, Color Grid, Color Pace, Color Hover)
{
    public static FlyoutPalette For(bool light) => light
        ? new FlyoutPalette(
            Background: Color.FromArgb(249, 249, 249),
            Text: Color.FromArgb(26, 26, 26),
            Secondary: Color.FromArgb(96, 96, 96),
            Track: Color.FromArgb(214, 214, 214),
            Border: Color.FromArgb(214, 214, 214),
            Grid: Color.FromArgb(229, 229, 229),
            Pace: Color.FromArgb(70, 70, 70),
            Hover: Color.FromArgb(234, 234, 234))
        : new FlyoutPalette(
            Background: Color.FromArgb(43, 43, 43),
            Text: Color.FromArgb(255, 255, 255),
            Secondary: Color.FromArgb(178, 178, 178),
            Track: Color.FromArgb(80, 80, 80),
            Border: Color.FromArgb(70, 70, 70),
            Grid: Color.FromArgb(62, 62, 62),
            Pace: Color.FromArgb(205, 205, 205),
            Hover: Color.FromArgb(60, 60, 60));
}

/// <summary>
/// Draws <see cref="UsageSection"/>s - shared by the tray flyout and the mini dashboard, so
/// both look the same. Works in 96-DPI logical units multiplied by <paramref name="scale"/>.
/// Every draw method takes a nullable <see cref="Graphics"/>: with null it draws nothing but
/// still returns the height it would need, so callers can size their window with the same
/// code that paints it.
/// </summary>
internal sealed class UsageBarRenderer(FlyoutPalette palette, UsagePalette usage, float scale)
{
    public const float BarHeight = 8f;

    private float S(float logical) => logical * scale;

    /// <summary>A full section, as in the flyout: label and percentage, the bar with its pace
    /// marker and projection, and the detail lines. Returns the y below it (no gap added).</summary>
    public float DrawSection(Graphics? g, UsageSection section, Font labelFont, Font valueFont, Font detailFont, float left, float y, float width)
    {
        y = DrawTitleRow(g, section.Label, $"{section.Percent}%", labelFont, valueFont, left, y, width);
        DrawBar(g, new RectangleF(left, y, width, S(BarHeight)), section.Percent, section.OutlineOnly,
            section.PaceFraction, section.ProjectedPercent, section.Sections);
        y += S(BarHeight) + S(6f);

        foreach (var line in section.Details)
        {
            DrawText(g, line.Text, detailFont, line.Emphasized ? palette.Text : palette.Secondary, new RectangleF(left, y, width, S(18f)));
            y += S(18f);
        }
        return y;
    }

    /// <summary>A section in one row, as in the mini dashboard's compact layout: label on the
    /// left (cut off with "…" beyond <paramref name="labelWidth"/>), percentage on the right,
    /// and the bar in between - with its scale ticks, pace marker and projection as in the
    /// flyout, but without the detail lines.</summary>
    public void DrawCompactRow(Graphics? g, UsageSection section, Font labelFont, Font valueFont, float left, float y, float width, float labelWidth, float valueWidth, float rowHeight)
    {
        if (g is null)
            return;

        DrawText(g, section.Label, labelFont, palette.Text, new RectangleF(left, y, labelWidth, rowHeight));
        DrawText(g, $"{section.Percent}%", valueFont, palette.Text, new RectangleF(left + width - valueWidth, y, valueWidth, rowHeight), TextFormatFlags.Right);

        var gap = S(8f);
        var barLeft = left + labelWidth + gap;
        var barWidth = width - labelWidth - valueWidth - 2 * gap;
        if (barWidth > S(BarHeight))
        {
            var bar = new RectangleF(barLeft, y + (rowHeight - S(BarHeight)) / 2f, barWidth, S(BarHeight));
            DrawBar(g, bar, section.Percent, section.OutlineOnly, section.PaceFraction, section.ProjectedPercent, section.Sections);
        }
    }

    public float DrawTitleRow(Graphics? g, string title, string value, Font labelFont, Font valueFont, float left, float y, float width)
    {
        DrawText(g, title, labelFont, palette.Text, new RectangleF(left, y, width - S(60f), S(20f)));
        DrawText(g, value, valueFont, palette.Text, new RectangleF(left + width - S(60f), y, S(60f), S(20f)), TextFormatFlags.Right);
        return y + S(20f) + S(6f);
    }

    /// <summary>A usage bar: the same track/fill/colour rules as the tray icon's (an
    /// outline-only track before a session starts; in the greyscale palette an outlined track
    /// and a notch above 80%), plus - for the session and the week - the pace marker as a
    /// tick that sticks out a little above and below the bar, and the forecast's <paramref
    /// name="projectedPercent"/> as a dimmed, hatched extension of the fill (see <see
    /// cref="DrawProjection"/>).</summary>
    /// <param name="sections">How many equal parts the scale ticks divide the bar into (see
    /// <see cref="UsageSections.SectionsFor"/>).</param>
    public void DrawBar(Graphics? g, RectangleF rect, int percent, bool outlineOnly, double? paceFraction, double? projectedPercent = null, int sections = 4)
    {
        if (g is null)
            return;

        var clamped = Math.Clamp(percent, 0, 100);
        var radius = rect.Height / 2f;
        using (var trackPath = RoundedRect(rect, radius))
        {
            // The greyscale palette brings its own track (with an outline for contrast);
            // the standard one uses the flyout's neutral grey.
            var greyscaleTrack = usage.TrackOutline;
            if (outlineOnly)
            {
                using var outline = new Pen(greyscaleTrack is null ? palette.Track : usage.NotStarted, S(1.5f));
                g.DrawPath(outline, trackPath);
            }
            else
            {
                using var track = new SolidBrush(greyscaleTrack is null ? palette.Track : usage.Track);
                g.FillPath(track, trackPath);
                if (greyscaleTrack is { } outlineColor)
                {
                    using var outline = new Pen(outlineColor, S(1f));
                    using var insetPath = RoundedRect(RectangleF.Inflate(rect, -S(0.5f), -S(0.5f)), radius - S(0.5f));
                    g.DrawPath(outline, insetPath);
                }
            }
        }

        if (projectedPercent is { } projected && projected > clamped)
            DrawProjection(g, rect, radius, projected);

        if (clamped > 0)
        {
            var fillWidth = Math.Max(rect.Width * clamped / 100f, rect.Height);
            var fillRect = new RectangleF(rect.X, rect.Y, Math.Min(fillWidth, rect.Width), rect.Height);
            using var fillPath = RoundedRect(fillRect, radius);
            using var fill = new SolidBrush(usage.ForPercent(clamped));
            g.FillPath(fill, fillPath);

            if (usage.MarksHighLevel && UsagePalette.IsHigh(clamped))
            {
                // Same notch as the tray icon: a contrasting gap just inside the fill's end.
                var previousClip = g.Clip;
                g.SetClip(fillPath, CombineMode.Intersect);
                using var notch = new SolidBrush(usage.HighNotch);
                g.FillRectangle(notch, fillRect.Right - S(4f) - S(2f), fillRect.Y, S(2f), fillRect.Height);
                g.Clip = previousClip;
            }
        }

        DrawScaleTicks(g, rect, outlineOnly, sections);

        if (paceFraction is { } pace)
        {
            var x = rect.X + rect.Width * (float)Math.Clamp(pace, 0.0, 1.0);
            var core = S(2f);
            var outline = S(1.5f);
            var top = rect.Y - S(3f);
            var height = rect.Height + S(6f);
            using var outlineBrush = new SolidBrush(palette.Background);
            using var coreBrush = new SolidBrush(palette.Text);
            g.FillRectangle(outlineBrush, x - core / 2f - outline, top - outline, core + 2 * outline, height + 2 * outline);
            g.FillRectangle(coreBrush, x - core / 2f, top, core, height);
        }
    }

    /// <summary>Scale ticks dividing the bar into <paramref name="sections"/> equal parts:
    /// thin, pixel-aligned gaps across the bar in the background colour, so they read the same
    /// over the fill, the projection and the empty track. Thinner and fainter than the pace
    /// marker and the greyscale palette's notch, which they mustn't be confused with. On an
    /// outline-only bar (session not started) there's nothing to cut a gap into, so they're
    /// drawn in the outline's colour instead.</summary>
    private void DrawScaleTicks(Graphics g, RectangleF rect, bool outlineOnly, int sections)
    {
        if (sections < 2)
            return;

        var color = outlineOnly
            ? (usage.TrackOutline is null ? palette.Track : usage.NotStarted)
            : Color.FromArgb(190, palette.Background);
        var width = Math.Max(1f, MathF.Round(S(1f)));

        using var brush = new SolidBrush(color);
        for (var i = 1; i < sections; i++)
        {
            var x = MathF.Round(rect.X + rect.Width * i / sections - width / 2f);
            g.FillRectangle(brush, x, rect.Y, width, rect.Height);
        }
    }

    /// <summary>The usage the window would reach by its reset at the current rate: drawn from
    /// the bar's start (the real fill covers the first part) to <paramref name="projected"/>,
    /// in the colour of the level it would reach - dimmed, with diagonal stripes, so it reads
    /// as "not yet used" and stays apart from the fill in the greyscale palette too.</summary>
    private void DrawProjection(Graphics g, RectangleF rect, float radius, double projected)
    {
        var level = Math.Clamp(projected, 0, 100);
        var width = Math.Min(Math.Max(rect.Width * (float)(level / 100), rect.Height), rect.Width);
        var projectionRect = new RectangleF(rect.X, rect.Y, width, rect.Height);
        var color = usage.ForPercent((int)Math.Ceiling(level));

        using var path = RoundedRect(projectionRect, radius);
        var previousClip = g.Clip;
        g.SetClip(path, CombineMode.Intersect);

        using (var dim = new SolidBrush(Color.FromArgb(70, color)))
            g.FillPath(dim, path);

        // Wide spacing, so the stripes stand out clearly from the solid fill next to them.
        var spacing = S(9f);
        using var stripe = new Pen(Color.FromArgb(170, color), S(1.5f));
        for (var x = projectionRect.X - rect.Height; x < projectionRect.Right; x += spacing)
            g.DrawLine(stripe, x, rect.Bottom, x + rect.Height, rect.Y);

        g.Clip = previousClip;
    }

    public static void DrawText(Graphics? g, string text, Font font, Color color, RectangleF rect, TextFormatFlags extra = 0)
    {
        if (g is null)
            return;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | extra;
        if ((extra & TextFormatFlags.WordBreak) == 0)
            flags |= TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, text, font, Rectangle.Round(rect), color, flags);
    }

    public static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
