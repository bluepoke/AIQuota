using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AIQuota.StreamDock;

/// <summary>
/// Renders an opaque 200x200 PNG sized for a physical Stream Dock key face, in one of the
/// <see cref="DeckKeyStyle"/>s. The single-value styles (Session/Week/Credit) are their own
/// layout, reusing the tray icon's colours and "session not started" look. The combined
/// style is a higher-resolution copy of the tray icon (see <see cref="TrayIconFactory"/>),
/// described below. Every shape, colour
/// and size ratio is kept identical to the tray icon - every constant below is the tray
/// icon's own constant (drawn at a fixed 32px canvas) multiplied by <see cref="Scale"/> -
/// so this is deliberately a scaled copy of that drawing code rather than a fresh design.
/// The one addition the extra resolution allows is printing each bar's label and
/// percentage as one line of text inside the (now much taller) bar itself.
/// Always dark, since a deck key isn't affected by the Windows taskbar theme - i.e. always
/// the dark variant of the <see cref="UsagePalette"/> (standard or, in accessibility mode,
/// greyscale - see <see cref="DeckStatusSnapshot.Greyscale"/>). In greyscale, a level above
/// 80% gets a hatched fill, and the text in a bar switches colour where it crosses the fill
/// edge so it keeps its contrast on both the fill and the track.
/// </summary>
internal static class DeckKeyRenderer
{
    private const int Size = 200;
    private const float Scale = Size / 32f; // the tray icon is drawn at a fixed 32px

    private static readonly Color Background = Color.FromArgb(255, 28, 28, 30);

    /// <summary>Width of the greyscale palette's track outline - thinner than a scaled copy
    /// of the tray icon's, which would be heavy at key size.</summary>
    private const float TrackOutlineWidth = 3f;

    public static byte[] Render(DeckStatusSnapshot snapshot, DeckKeyStyle style = DeckKeyStyle.Combined)
    {
        var palette = UsagePalette.For(snapshot.Greyscale, isLight: false);
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
                DrawWarningGlyph(g, palette);
            }
            else
            {
                switch (style)
                {
                    case DeckKeyStyle.Session:
                        DrawSingleValue(g, palette, "Session", snapshot.SessionPercent, snapshot.SessionNotStarted);
                        if (snapshot.SessionRemainingFraction is { } remaining)
                            DrawCountdownBlocks(g, palette, remaining, snapshot.SessionNotStarted);
                        break;
                    case DeckKeyStyle.Week:
                        DrawSingleValue(g, palette, "Week", snapshot.WeeklyPercent, paceFraction: snapshot.WeeklyPaceFraction);
                        break;
                    case DeckKeyStyle.Credit:
                        DrawSingleValue(g, palette, "Credit", snapshot.CreditPercent);
                        break;
                    default:
                        DrawBars(g, palette, snapshot.SessionPercent, snapshot.WeeklyPercent, snapshot.CreditPercent, snapshot.SessionNotStarted, snapshot.WeeklyPaceFraction);
                        if (snapshot.ShowSessionRing && snapshot.SessionRemainingFraction is { } fraction)
                            DrawSessionRing(g, palette, fraction, snapshot.SessionNotStarted);
                        break;
                }

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

    // Layout of the single-value keys (Session/Week/Credit): a title on top, one tall bar that
    // fills from the bottom with the percentage in its middle, and - Session only - a row of
    // 5 countdown blocks underneath. Week/Credit keep the same bar position so a row of keys
    // lines up.
    private static readonly RectangleF SingleValueBar = new(22, 46, 156, 108);
    private const float SingleValueBarRadius = 16f;
    private const float SingleValueTitleCenterY = 24f;
    private static readonly RectangleF CountdownRow = new(30, 166, 140, 12);
    private const float CountdownGap = 6f;

    /// <summary>Draws a single-value key: <paramref name="title"/> on top and one tall bar
    /// filling from the bottom, with the same colour-for-percent as the other bars and the
    /// percentage centered inside it (or "–" when there's no value, e.g. no usage credits).
    /// With <paramref name="outlineOnly"/> the bar's empty part is drawn as an outline only -
    /// the same "session not started" look as the tray icon's session bar. With <paramref
    /// name="paceFraction"/> the weekly pace marker is drawn across the bar, as a horizontal
    /// line since this bar fills upwards.</summary>
    private static void DrawSingleValue(Graphics g, UsagePalette palette, string title, int? percent, bool outlineOnly = false, double? paceFraction = null)
    {
        using (var titleFont = new Font("Segoe UI", 26f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var titleBrush = new SolidBrush(Color.White))
        using (var titleFormat = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(title, titleFont, titleBrush, new RectangleF(0, 0, Size, SingleValueTitleCenterY * 2), titleFormat);

        var rect = SingleValueBar;
        using var trackPath = RoundedRect(rect, SingleValueBarRadius);
        if (outlineOnly)
        {
            const float outlineWidth = 6f;
            var inset = RectangleF.Inflate(rect, -outlineWidth / 2f, -outlineWidth / 2f);
            using var outlinePath = RoundedRect(inset, SingleValueBarRadius - outlineWidth / 2f);
            using var outlinePen = new Pen(palette.NotStarted, outlineWidth);
            g.DrawPath(outlinePen, outlinePath);
        }
        else
        {
            FillTrack(g, palette, rect, SingleValueBarRadius);
        }

        var clamped = percent is { } p ? Math.Clamp(p, 0, 100) : 0;
        var fillHeight = rect.Height * clamped / 100f;
        using var fillArea = new Region(trackPath);
        if (fillHeight > 0)
        {
            // Clipped to the bar's own shape so a low fill still follows its rounded bottom
            // corners instead of being stretched to a minimum height.
            var fillRect = new RectangleF(rect.X, rect.Bottom - fillHeight, rect.Width, fillHeight);
            using var fillPath = RoundedRect(fillRect, Math.Min(SingleValueBarRadius, fillHeight / 2f));
            fillArea.Intersect(fillPath);
            FillLevel(g, palette, fillArea, rect, clamped);
        }
        else
        {
            fillArea.MakeEmpty();
        }

        if (paceFraction is { } pace)
        {
            const float coreWidth = 6f;
            const float outlineWidth = 3f;
            var y = rect.Bottom - rect.Height * (float)Math.Clamp(pace, 0.0, 1.0);
            y = Math.Clamp(y, rect.Y + coreWidth / 2f, rect.Bottom - coreWidth / 2f);
            DrawPaceTick(g, palette, trackPath, new RectangleF(rect.X, y - coreWidth / 2f, rect.Width, coreWidth), outlineWidth, rect.Width * 0.14f);
        }

        var text = percent is null ? "–" : $"{clamped}%";
        using var font = new Font("Segoe UI", 44f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        DrawSplitText(g, palette, text, font, rect, format, fillArea, clamped);
    }

    /// <summary>The empty part of a bar: the track colour, plus - in the greyscale palette -
    /// an outline that keeps the bar's extent visible against the key background.</summary>
    private static void FillTrack(Graphics g, UsagePalette palette, RectangleF rect, float radius)
    {
        using (var trackPath = RoundedRect(rect, radius))
        using (var trackBrush = new SolidBrush(palette.Track))
            g.FillPath(trackBrush, trackPath);

        if (palette.TrackOutline is { } outlineColor)
        {
            var inset = RectangleF.Inflate(rect, -TrackOutlineWidth / 2f, -TrackOutlineWidth / 2f);
            using var outlinePath = RoundedRect(inset, radius - TrackOutlineWidth / 2f);
            using var outlinePen = new Pen(outlineColor, TrackOutlineWidth);
            g.DrawPath(outlinePen, outlinePath);
        }
    }

    /// <summary>Fills <paramref name="area"/> (the filled part of a bar spanning <paramref
    /// name="bar"/>) in the colour for <paramref name="percent"/>. In the greyscale palette a
    /// level above 80% is hatched with diagonal stripes, so it's told apart from the 50-80%
    /// level by its pattern, not just by brightness.</summary>
    private static void FillLevel(Graphics g, UsagePalette palette, Region area, RectangleF bar, int percent)
    {
        using (var fillBrush = new SolidBrush(palette.ForPercent(percent)))
            g.FillRegion(fillBrush, area);

        if (!palette.MarksHighLevel || !UsagePalette.IsHigh(percent))
            return;

        const float stripeWidth = 7f;
        const float stripeSpacing = 20f;
        var previousClip = g.Clip;
        g.SetClip(area, CombineMode.Intersect);
        using var stripePen = new Pen(palette.HighHatch, stripeWidth);
        for (var x = bar.X - bar.Height; x < bar.Right + stripeSpacing; x += stripeSpacing)
            g.DrawLine(stripePen, x, bar.Bottom, x + bar.Height, bar.Y);
        g.Clip = previousClip;
    }

    /// <summary>Draws a bar's text in <see cref="UsagePalette.TextOnTrack"/>, and the part of
    /// it over <paramref name="fillArea"/> in <see cref="UsagePalette.TextOnFill"/> - so in
    /// the greyscale palette, where the fill is light, each part keeps its contrast. Over a
    /// hatched fill (<paramref name="percent"/> above 80%) the text also gets a halo in the
    /// fill's base colour, so the stripes don't run through the letters.</summary>
    private static void DrawSplitText(Graphics g, UsagePalette palette, string text, Font font, RectangleF rect, StringFormat format, Region fillArea, int percent)
    {
        if (palette.TextOnFill == palette.TextOnTrack)
        {
            using var brush = new SolidBrush(palette.TextOnTrack);
            g.DrawString(text, font, brush, rect, format);
            return;
        }

        // Drawn as one path, so the two colours and the halo line up exactly at the fill edge.
        using var textPath = new GraphicsPath();
        textPath.AddString(text, font.FontFamily, (int)font.Style, font.Size, rect, format);

        using (var trackBrush = new SolidBrush(palette.TextOnTrack))
            g.FillPath(trackBrush, textPath);

        var previousClip = g.Clip;
        g.SetClip(fillArea, CombineMode.Intersect);
        if (palette.MarksHighLevel && UsagePalette.IsHigh(percent))
        {
            using var halo = new Pen(palette.High, 6f) { LineJoin = LineJoin.Round };
            g.DrawPath(halo, textPath);
        }
        using (var fillBrush = new SolidBrush(palette.TextOnFill))
            g.FillPath(fillBrush, textPath);
        g.Clip = previousClip;
    }

    /// <summary>The session countdown as 5 blocks in a row under the Session key's bar, one
    /// per hour of the 5-hour session - the same rules as the countdown ring (see <see
    /// cref="TrayIconFactory"/>): a solid block per full hour left, the block of the hour
    /// currently ticking away shrinks from the right, elapsed hours aren't drawn at all, and
    /// before a session starts all 5 are drawn in the dimmed track colour.</summary>
    private static void DrawCountdownBlocks(Graphics g, UsagePalette palette, double remainingFraction, bool notStarted)
    {
        const int blockCount = 5;
        const float radius = 3f;
        var blockWidth = (CountdownRow.Width - CountdownGap * (blockCount - 1)) / blockCount;
        var hoursLeft = Math.Clamp(remainingFraction, 0.0, 1.0) * blockCount;

        using var brush = new SolidBrush(notStarted ? palette.NotStarted : palette.Ring);
        for (var i = 0; i < blockCount; i++)
        {
            var filled = (float)Math.Clamp(hoursLeft - i, 0.0, 1.0);
            if (filled <= 0f)
                continue;

            var x = CountdownRow.X + i * (blockWidth + CountdownGap);
            var block = new RectangleF(x, CountdownRow.Y, blockWidth * filled, CountdownRow.Height);
            using var path = RoundedRect(block, Math.Min(radius, block.Width / 2f));
            g.FillPath(brush, path);
        }
    }

    /// <summary>Same layout as <see cref="TrayIconFactory"/>'s DrawBars: two bars, or three
    /// when a credit percentage is available, stacked in the middle of the icon inset far
    /// enough to leave room for the session countdown ring - including the week bar's pace
    /// marker when <paramref name="weeklyPaceFraction"/> is set.</summary>
    private static void DrawBars(Graphics g, UsagePalette palette, int sessionPercent, int weeklyPercent, int? creditPercent, bool sessionNotStarted, double? weeklyPaceFraction)
    {
        if (creditPercent is { } credit)
        {
            DrawBar(g, palette, "Session", sessionPercent, new RectangleF(4 * Scale, 4 * Scale, (Size - 8 * Scale), 7 * Scale), sessionNotStarted);
            DrawBar(g, palette, "Week", weeklyPercent, new RectangleF(4 * Scale, 12 * Scale, (Size - 8 * Scale), 7 * Scale), paceFraction: weeklyPaceFraction);
            DrawBar(g, palette, "Credit", credit, new RectangleF(4 * Scale, 20 * Scale, (Size - 8 * Scale), 7 * Scale));
        }
        else
        {
            DrawBar(g, palette, "Session", sessionPercent, new RectangleF(4 * Scale, 4 * Scale, (Size - 8 * Scale), 11 * Scale), sessionNotStarted);
            DrawBar(g, palette, "Week", weeklyPercent, new RectangleF(4 * Scale, 17 * Scale, (Size - 8 * Scale), 11 * Scale), paceFraction: weeklyPaceFraction);
        }
    }

    /// <summary>Same track/fill geometry and colour-for-percent as <see
    /// cref="TrayIconFactory"/>'s DrawBar (including its outline-only track for a session
    /// that hasn't started), plus - since a bar is now tall enough to hold it - the label and
    /// percentage as one centered line ("Session 45%"). With <paramref name="paceFraction"/>
    /// the weekly pace marker is drawn too, as notches above and below that text line (see
    /// <see cref="DrawPaceTick"/>).</summary>
    private static void DrawBar(Graphics g, UsagePalette palette, string label, int percent, RectangleF rect, bool outlineOnly = false, double? paceFraction = null)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var radius = Math.Min(4f * Scale, rect.Height / 2f);

        if (outlineOnly)
        {
            const float outlineWidth = 2f * Scale;
            var inset = RectangleF.Inflate(rect, -outlineWidth / 2f, -outlineWidth / 2f);
            using var outlinePath = RoundedRect(inset, radius - outlineWidth / 2f);
            using var outlinePen = new Pen(palette.NotStarted, outlineWidth);
            g.DrawPath(outlinePen, outlinePath);
        }
        else
        {
            FillTrack(g, palette, rect, radius);
        }

        var fillWidth = Math.Max(rect.Width * clamped / 100f, clamped > 0 ? 6f * Scale : 0f);
        using var fillArea = new Region();
        fillArea.MakeEmpty();
        if (fillWidth > 0)
        {
            var fillRect = new RectangleF(rect.X, rect.Y, Math.Min(fillWidth, rect.Width), rect.Height);
            using var fillPath = RoundedRect(fillRect, radius);
            fillArea.Union(fillPath);
            FillLevel(g, palette, fillArea, rect, clamped);
        }

        if (paceFraction is { } pace)
        {
            const float coreWidth = 1.2f * Scale;
            const float outlineWidth = 0.5f * Scale;
            var x = rect.X + rect.Width * (float)Math.Clamp(pace, 0.0, 1.0);
            x = Math.Clamp(x, rect.X + coreWidth / 2f, rect.Right - coreWidth / 2f);
            using var barPath = RoundedRect(rect, radius);
            DrawPaceTick(g, palette, barPath, new RectangleF(x - coreWidth / 2f, rect.Y, coreWidth, rect.Height), outlineWidth, rect.Height * 0.28f);
        }

        using var format = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var font = new Font("Segoe UI", FitFontSize(g, rect, format), FontStyle.Bold, GraphicsUnit.Pixel);
        DrawSplitText(g, palette, $"{label} {clamped}%", font, rect, format, fillArea, clamped);
    }

    /// <summary>Mirrors <see cref="TrayIconFactory"/>'s pace marker (dark palette): a
    /// white tick with a dark outline of <paramref name="outlineWidth"/>, clipped to the bar's
    /// rounded shape (<paramref name="barPath"/>). Unlike the tray icon, which has no text in
    /// its bars, the tick isn't drawn across the whole bar - which would run straight through
    /// the white percentage text - but only as two notches of <paramref name="notchLength"/>
    /// at either end of <paramref name="core"/> (the full tick, spanning the bar), leaving the
    /// text in the middle clear. Works for both the horizontal bars (vertical tick, notches at
    /// top and bottom) and the Week key's upward-filling bar (horizontal tick, notches at the
    /// left and right edges).</summary>
    private static void DrawPaceTick(Graphics g, UsagePalette palette, GraphicsPath barPath, RectangleF core, float outlineWidth, float notchLength)
    {
        var vertical = core.Width < core.Height;
        RectangleF[] notches = vertical
            ? [new(core.X, core.Y, core.Width, notchLength), new(core.X, core.Bottom - notchLength, core.Width, notchLength)]
            : [new(core.X, core.Y, notchLength, core.Height), new(core.Right - notchLength, core.Y, notchLength, core.Height)];

        var previousClip = g.Clip;
        g.SetClip(barPath, CombineMode.Intersect);
        using var outlineBrush = new SolidBrush(palette.PaceOutline);
        using var coreBrush = new SolidBrush(palette.PaceCore);
        foreach (var notch in notches)
        {
            // Outlined on all sides; the outer end is cut off again by the bar's clip.
            g.FillRectangle(outlineBrush, RectangleF.Inflate(notch, outlineWidth, outlineWidth));
            g.FillRectangle(coreBrush, notch);
        }
        g.Clip = previousClip;
    }

    /// <summary>Font size for a bar's text: as tall as the bar allows, shrunk so the widest
    /// possible text ("Session 100%") still fits on one line - so every bar gets the same
    /// size and it doesn't jump as percentages change.</summary>
    private static float FitFontSize(Graphics g, RectangleF rect, StringFormat format)
    {
        const string widestText = "Session 100%";
        var size = Math.Min(rect.Height * 0.42f, 34f);
        var available = rect.Width - 2 * 2f * Scale;

        using var probe = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        var width = g.MeasureString(widestText, probe, PointF.Empty, format).Width;
        return width > available ? size * available / width : size;
    }

    /// <summary>Exact copy of <see cref="TrayIconFactory"/>'s countdown ring - a gapped,
    /// 5-segment outline tracing a rounded square, clockwise from top-center - scaled up;
    /// see that method for the full rationale. Always the dark palette, since a deck key has
    /// no taskbar theme to match.</summary>
    private static void DrawSessionRing(Graphics g, UsagePalette palette, double remainingFraction, bool notStarted)
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

        using var fillPen = new Pen(notStarted ? palette.NotStarted : palette.Ring, penWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };

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

    /// <summary>Mirrors <see cref="TrayIconFactory.CreateWarningIcon"/>: a warning triangle
    /// with a "!" centered in it - orange, or in the greyscale palette black with a white
    /// outline and a white "!".</summary>
    private static void DrawWarningGlyph(Graphics g, UsagePalette palette)
    {
        using var trianglePath = new GraphicsPath();
        trianglePath.AddPolygon([
            new PointF(Size / 2f, 3f * Scale),
            new PointF(Size - 3f * Scale, Size - 4f * Scale),
            new PointF(3f * Scale, Size - 4f * Scale),
        ]);
        trianglePath.CloseFigure();

        using (var brush = new SolidBrush(palette.WarningFill))
            g.FillPath(brush, trianglePath);
        if (palette.WarningOutline is { } outline)
        {
            using var outlinePen = new Pen(outline, 2f * Scale) { LineJoin = LineJoin.Round };
            g.DrawPath(outlinePen, trianglePath);
        }

        using var textBrush = new SolidBrush(palette.WarningGlyph);
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

}
