// Regenerates the README screenshots from AIQuota's real rendering code with mocked data:
//
//     dotnet run --project tools/Screenshots [output directory]
//
// Writes tray_icon_states.png, context_menu.png, flyout_dark.png, flyout_light.png,
// mini_dashboard_dark/light.png and heatmap_dark/light.png to the
// repository's screenshots/ folder (or the given directory), and the same tray icons,
// flyouts and heatmap in accessibility mode (accessibility_*.png), plus its Stream Dock keys.
//
// - Tray icon and flyout are drawn by the app's own TrayIconFactory and UsageFlyout. Their
//   drawing methods and fields are private, so they're reached by reflection - if a rename
//   breaks this tool, update the names here.
// - The context menu is a ContextMenuStrip with the same structure and Strings texts as the
//   app's menu. Building the real UsageTrayContext would start network calls and add a
//   second tray icon.
// - Never shows a window (everything is drawn offscreen) and never touches the signed-in
//   account, the usage history or saved settings: the language is switched to English in
//   memory only.
// - Times are relative to now, so the images differ slightly from run to run.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using AIQuota;
using AIQuota.History;
using AIQuota.Localization;
using AIQuota.StreamDock;

Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
var outDir = args.Length > 0 ? args[0] : Path.Combine(FindRepositoryRoot(), "screenshots");
Directory.CreateDirectory(outDir);
Console.WriteLine($"Writing screenshots to {Path.GetFullPath(outDir)}");

const BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;
const BindingFlags PrivInst = BindingFlags.NonPublic | BindingFlags.Instance;
var asm = typeof(UsageSnapshot).Assembly;

// English, in memory only (Strings.SetLanguage would persist the preference).
typeof(Strings).GetProperty("Current")!.SetValue(null, Enum.Parse(asm.GetType("AIQuota.Localization.AppLanguage")!, "English"));

var now = DateTimeOffset.Now;
const string Account = "Jane Doe (jane@example.com)";

// UsagePalette is internal: the standard or greyscale (accessibility mode) palette for a
// light or dark background.
object Palette(bool greyscale, bool isLight) =>
    asm.GetType("AIQuota.UsagePalette")!.GetMethod("For", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [greyscale, isLight])!;

// ------------------------------------------------------------------ tray icon states
void TraySheet(bool greyscale, string fileName)
{
    var drawBars = typeof(TrayIconFactory).GetMethod("DrawBars", PrivStatic)!;
    var drawRing = typeof(TrayIconFactory).GetMethod("DrawSessionRing", PrivStatic)!;
    var drawBadge = typeof(TrayIconFactory).GetMethod("DrawUpdateBadge", PrivStatic)!;
    var drawWarning = typeof(TrayIconFactory).GetMethod("DrawWarning", PrivStatic)!;

    Bitmap Icon(int session, int week, int? credit, double? ring, bool notStarted, double? pace, bool badge, bool warning, bool isLight, int px)
    {
        var palette = Palette(greyscale, isLight);
        var bmp = new Bitmap(px, px);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        g.ScaleTransform(px / 32f, px / 32f);   // the factory draws on a fixed 32px canvas
        if (warning)
        {
            drawWarning.Invoke(null, [g, 32, palette]);
        }
        else
        {
            drawBars.Invoke(null, [g, session, week, credit, 32, palette, notStarted, pace]);
            if (ring is { } r) drawRing.Invoke(null, [g, r, 32, palette, notStarted]);
        }
        if (badge) drawBadge.Invoke(null, [g, 32, palette]);
        return bmp;
    }

    var states = new List<(string Label, int S, int W, int? C, double? Ring, bool NotStarted, double? Pace, bool Badge, bool Warning)>
    {
        ("3.2h left, ahead of pace", 37, 62, null, 3.2 / 5, false, 0.43, false, false),
        ("With usage credits", 72, 41, 25, 1.6 / 5, false, 0.33, false, false),
        ("Session not started", 0, 20, null, 1.0, true, 0.30, false, false),
        ("Nearly used up", 93, 85, null, 0.6 / 5, false, 0.71, false, false),
        ("Update available", 55, 48, null, 4.1 / 5, false, 0.60, true, false),
    };
    if (greyscale)
        states.Add(("Network error", 0, 0, null, null, false, null, false, true));

    const int cellW = 150, big = 96, rowH = 170;
    using var sheet = new Bitmap(cellW * states.Count + 24, rowH * 2);
    using (var g = Graphics.FromImage(sheet))
    {
        // Greyscale smoothing for the accessibility sheet: ClearType fringes would add hue.
        g.TextRenderingHint = greyscale ? System.Drawing.Text.TextRenderingHint.AntiAliasGridFit : System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        foreach (var (row, isLight) in new[] { (0, false), (1, true) })
        {
            var bg = isLight ? Color.FromArgb(238, 238, 238) : Color.FromArgb(32, 32, 32);
            var fg = isLight ? Color.FromArgb(40, 40, 40) : Color.FromArgb(225, 225, 225);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, row * rowH, sheet.Width, rowH);
            using var font = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", 11f, GraphicsUnit.Pixel);
            using var fgBrush = new SolidBrush(fg);
            using var dimBrush = new SolidBrush(Color.FromArgb(150, fg));
            using var center = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(isLight ? "Light taskbar" : "Dark taskbar", small, dimBrush, 12, row * rowH + 6);

            for (var i = 0; i < states.Count; i++)
            {
                var s = states[i];
                var x = 12 + i * cellW;
                var y = row * rowH + 26;
                using (var icon = Icon(s.S, s.W, s.C, s.Ring, s.NotStarted, s.Pace, s.Badge, s.Warning, isLight, big))
                    g.DrawImage(icon, x + (cellW - big) / 2 - 14, y);
                // Actual tray size next to it (24px = 16px icon at 150% scaling).
                using (var icon = Icon(s.S, s.W, s.C, s.Ring, s.NotStarted, s.Pace, s.Badge, s.Warning, isLight, 24))
                    g.DrawImage(icon, x + (cellW - big) / 2 - 14 + big + 8, y + big - 24);
                g.DrawString(s.Label, font, fgBrush, new RectangleF(x, y + big + 10, cellW - 8, 36), center);
            }
        }
    }
    sheet.Save(Path.Combine(outDir, fileName), ImageFormat.Png);
}
TraySheet(greyscale: false, "tray_icon_states.png");
TraySheet(greyscale: true, "accessibility_tray_icon_states.png");

// ------------------------------------------------------------------ Stream Dock keys (accessibility mode)
{
    var render = asm.GetType("AIQuota.StreamDock.DeckKeyRenderer")!.GetMethod("Render", BindingFlags.Public | BindingFlags.Static)!;
    var keys = new (string Label, DeckKeyStyle Style, DeckStatusSnapshot Snapshot)[]
    {
        ("Combined", DeckKeyStyle.Combined, Deck(37, 62, null, 3.2 / 5, 0.43)),
        ("Combined, high", DeckKeyStyle.Combined, Deck(93, 85, 25, 0.6 / 5, 0.71)),
        ("Session", DeckKeyStyle.Session, Deck(72, 41, null, 1.6 / 5, null)),
        ("Week", DeckKeyStyle.Week, Deck(37, 85, null, null, 0.71)),
        ("Credit", DeckKeyStyle.Credit, Deck(37, 62, 25, null, null)),
        ("Network error", DeckKeyStyle.Combined, Deck(0, 0, null, null, null) with { IsWarning = true }),
    };

    const int key = 128, cellW = 150, rowH = 180;
    using var sheet = new Bitmap(cellW * keys.Length + 24, rowH);
    using (var g = Graphics.FromImage(sheet))
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.FromArgb(50, 50, 52));
        using var font = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        using var fgBrush = new SolidBrush(Color.FromArgb(225, 225, 225));
        using var center = new StringFormat { Alignment = StringAlignment.Center };
        for (var i = 0; i < keys.Length; i++)
        {
            var (label, style, deck) = keys[i];
            var png = (byte[])render.Invoke(null, [deck, style])!;
            using var image = Image.FromStream(new MemoryStream(png));
            var x = 12 + i * cellW;
            using (var path = Rounded(new RectangleF(x + (cellW - key) / 2, 16, key, key), 14))
            using (var texture = new TextureBrush(image) { WrapMode = WrapMode.Clamp })
            {
                texture.TranslateTransform(x + (cellW - key) / 2, 16);
                texture.ScaleTransform(key / (float)image.Width, key / (float)image.Height);
                g.FillPath(texture, path);
            }
            g.DrawString(label, font, fgBrush, new RectangleF(x, 16 + key + 10, cellW - 8, 36), center);
        }
    }
    sheet.Save(Path.Combine(outDir, "accessibility_deck_keys.png"), ImageFormat.Png);

    static DeckStatusSnapshot Deck(int session, int week, int? credit, double? remaining, double? pace) => new(
        LoggedIn: true, IsWarning: false, IsRefreshing: false,
        SessionPercent: session, WeeklyPercent: week, CreditPercent: credit,
        SessionRemainingFraction: remaining, SessionNotStarted: false, ShowSessionRing: remaining is not null,
        WeeklyPaceFraction: pace, Greyscale: true);
}

// ------------------------------------------------------------------ mock usage history
var weeklyReset = now + TimeSpan.FromDays(3.6);          // current window started 3.4 days ago
var sessionReset = now + TimeSpan.FromHours(1.6);
var history = new List<UsageHistoryRecord>();
{
    var rnd = new Random(42);
    var windowStart = weeklyReset - TimeSpan.FromDays(7);
    var cumulative = 0.0;
    var t0 = now - TimeSpan.FromDays(7);
    // Start the previous window's running total where it would be by then.
    cumulative = 24;
    for (var t = t0; t <= now; t += TimeSpan.FromMinutes(5))
    {
        if (t >= windowStart && t - TimeSpan.FromMinutes(5) < windowStart)
            cumulative = 0;                                      // weekly reset
        var local = t.ToLocalTime();
        var weekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var running = !weekend && local.Hour >= 8 && local.Hour < 19;
        if (!running && t < now - TimeSpan.FromMinutes(30))
            continue;                                            // PC off: gap
        var busy = local.Hour is >= 9 and < 12 or >= 13 and < 17;
        cumulative = Math.Min(100, cumulative + (busy ? 0.10 + rnd.NextDouble() * 0.16 : 0.02 + rnd.NextDouble() * 0.05));
        var reset = t >= windowStart ? weeklyReset : windowStart;
        history.Add(new UsageHistoryRecord(t, 40, sessionReset, cumulative, reset));
    }
}
var weekNow = history[^1].Week;
var snapshot = new UsageSnapshot(72, (int)Math.Round(weekNow), sessionReset, weeklyReset, now - TimeSpan.FromMinutes(2),
    12.34m, 50m, "EUR", 72.3, weekNow,
    // A model-specific weekly limit, as the API reports it in its "limits" list.
    [new UsageLimitInfo("weekly_scoped:opus", "weekly", "Opus", 41, weeklyReset, 50)]);
double WeeklyPacePercent() => (1 - (weeklyReset - now) / TimeSpan.FromDays(7)) * 100;

// ------------------------------------------------------------------ context menu
{
    var menu = new ContextMenuStrip { ShowImageMargin = true };
    ToolStripMenuItem Item(string text, bool enabled = true) => new(text) { Enabled = enabled };
    var history_ = Item(Strings.MenuHistory);
    history_.DropDownItems.Add(Strings.MenuHistoryExport);
    var settings = Item(Strings.MenuSettings);
    settings.DropDownItems.Add(Strings.MenuStartup);
    var about = Item(Strings.MenuAbout);
    about.DropDownItems.Add(Strings.MenuGitHub);

    menu.Items.Add(Item(Strings.UserLabel(Account), false));
    menu.Items.Add(Item(Strings.SessionLabel(72, Strings.FormatReset(sessionReset)), false));
    menu.Items.Add(Item(Strings.ForecastLimitIn(TimeSpan.FromMinutes(38)), false));
    menu.Items.Add(Item(Strings.WeeklyLabel(snapshot.WeeklyPercent, Strings.FormatReset(weeklyReset)) + Strings.PaceSuffix((int)Math.Round(WeeklyPacePercent())), false));
    menu.Items.Add(Item(Strings.ForecastResetFirst, false));
    foreach (var limit in snapshot.AdditionalLimits!)
        menu.Items.Add(Item(Strings.LimitMenuLabel(limit), false));
    menu.Items.Add(Item(Strings.CreditsLabel(12.34m, 50m, "EUR"), false));
    menu.Items.Add(Item(Strings.StatusUpdated(snapshot.FetchedAt), false));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item(Strings.MenuRefresh));
    menu.Items.Add(Item(Strings.MenuMiniDashboard));
    menu.Items.Add(Item(Strings.MenuLogout));
    menu.Items.Add(history_);
    menu.Items.Add(settings);
    menu.Items.Add(about);
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item(Strings.MenuExit));

    _ = menu.Handle;                       // create the window without showing it
    menu.PerformLayout();
    var size = menu.GetPreferredSize(Size.Empty);
    menu.Size = size;
    using var bmp = new Bitmap(size.Width, size.Height);
    menu.DrawToBitmap(bmp, new Rectangle(Point.Empty, size));
    using var framed = Frame(bmp, radius: 0, light: true);
    framed.Save(Path.Combine(outDir, "context_menu.png"), ImageFormat.Png);
    menu.Dispose();
}

// ------------------------------------------------------------------ flyout and mini dashboard
{
    var flyoutType = asm.GetType("AIQuota.UsageFlyout")!;
    var dashboardType = asm.GetType("AIQuota.MiniDashboard")!;
    var settingsType = asm.GetType("AIQuota.MiniDashboardSettings")!;
    var stateType = asm.GetType("AIQuota.FlyoutState")!;
    var paletteType = asm.GetType("AIQuota.FlyoutPalette")!;

    object State(bool greyscale) => Activator.CreateInstance(stateType,
        true, Account, Strings.StatusUpdated(snapshot.FetchedAt), false, snapshot, 25, false, true,
        new UsageForecast(ForecastKind.Limit, TimeSpan.FromMinutes(38), 100), new UsageForecast(ForecastKind.ResetFirst, ProjectedPercent: 78),
        (IReadOnlyList<UsageHistoryRecord>)history, greyscale, false)!;
    Delegate Getter(object state) => Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(stateType), new Holder(state),
        typeof(Holder).GetMethod(nameof(Holder.Get))!.MakeGenericMethod(stateType));

    // Renders a custom-painted form offscreen at 200%, with the named buttons drawn on top
    // (the form is never shown, so their Visible is always false and can't be used).
    Bitmap Render(Form form, Type type, bool light, bool greyscale, params string[] buttonFields)
    {
        type.GetField("_palette", PrivInst)!.SetValue(form, paletteType.GetMethod("For")!.Invoke(null, [light]));
        type.GetField("_usage", PrivInst)!.SetValue(form, Palette(greyscale, light));
        type.GetField("_scale", PrivInst)!.SetValue(form, 2f);
        type.GetMethod("ApplyLayout", PrivInst)!.Invoke(form, null);

        var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.ClientSize));
        using var g = Graphics.FromImage(bmp);
        foreach (var button in buttonFields.Select(f => (Button)type.GetField(f, PrivInst)!.GetValue(form)!))
        {
            using var buttonBmp = new Bitmap(button.Width, button.Height);
            button.DrawToBitmap(buttonBmp, new Rectangle(Point.Empty, button.Size));
            g.DrawImage(buttonBmp, button.Location);
        }
        return bmp;
    }

    foreach (var (light, greyscale) in new[] { (false, false), (true, false), (false, true), (true, true) })
    {
        var flyout = (Form)Activator.CreateInstance(flyoutType, Getter(State(greyscale)), (Func<Task>)(() => Task.CompletedTask), (Action)(() => { }), (Action)(() => { }))!;
        using var bmp = Render(flyout, flyoutType, light, greyscale, "_refreshButton", "_miniDashboardButton", "_heatmapButton");
        flyout.Dispose();

        using var framed = Frame(bmp, radius: 16, light: light);
        var name = (greyscale ? "accessibility_" : "") + (light ? "flyout_light.png" : "flyout_dark.png");
        framed.Save(Path.Combine(outDir, name), ImageFormat.Png);
    }

    // Mini dashboard: compact above expanded, per theme. In-memory settings only - nothing is
    // saved (the save callback is a no-op).
    var saveNothing = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(settingsType),
        typeof(Holder).GetMethod(nameof(Holder.Ignore))!.MakeGenericMethod(settingsType));
    foreach (var light in new[] { false, true })
    {
        var frames = new List<Bitmap>();
        foreach (var expanded in new[] { false, true })
        {
            var settings = Activator.CreateInstance(settingsType)!;
            settingsType.GetProperty("Expanded")!.SetValue(settings, expanded);
            settingsType.GetProperty("LogicalWidth")!.SetValue(settings, 300);
            settingsType.GetProperty("Bars")!.SetValue(settings, new List<string> { "session", "week", "limit:weekly_scoped:opus" });
            var dashboard = (Form)Activator.CreateInstance(dashboardType, Getter(State(false)), (Func<Task>)(() => Task.CompletedTask),
                (Action)(() => { }), (Action)(() => { }), settings, saveNothing)!;
            using var bmp = expanded
                ? Render(dashboard, dashboardType, light, greyscale: false, "_refreshButton", "_closeButton")
                : Render(dashboard, dashboardType, light, greyscale: false, "_closeButton");
            dashboard.Dispose();
            frames.Add(Frame(bmp, radius: 16, light: light));
        }

        using var sheet = new Bitmap(frames.Max(f => f.Width), frames.Sum(f => f.Height));
        using (var g = Graphics.FromImage(sheet))
        {
            var y = 0;
            foreach (var frame in frames)
            {
                g.DrawImage(frame, 0, y);
                y += frame.Height;
                frame.Dispose();
            }
        }
        sheet.Save(Path.Combine(outDir, light ? "mini_dashboard_light.png" : "mini_dashboard_dark.png"), ImageFormat.Png);
    }
}

// ------------------------------------------------------------------ yearly heatmap
{
    // A year of mock daily values: busy weekdays, quiet weekends, two holidays without the
    // app running, a few crunch weeks, and no data before the app was installed.
    var today = DateOnly.FromDateTime(DateTime.Now);
    var installed = today.AddDays(-300);
    var rnd = new Random(17);
    var days = new List<DailyUsageRecord>();
    var crunchWeeks = new HashSet<int> { 6, 7, 19, 31, 40 };
    for (var date = installed; date <= today; date = date.AddDays(1))
    {
        var age = today.DayNumber - date.DayNumber;
        if (age is >= 120 and < 134 || age is >= 230 and < 236)
            continue;                                                // holidays: PC off
        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        if (weekend && rnd.NextDouble() < 0.5)
            continue;                                                // PC off at the weekend
        var week = age / 7;
        double points = weekend
            ? (rnd.NextDouble() < 0.3 ? rnd.NextDouble() * 6 : 0)
            : rnd.NextDouble() < 0.12 ? rnd.NextDouble() * 0.4
            : crunchWeeks.Contains(week) ? 16 + rnd.NextDouble() * 12
            : 2 + rnd.NextDouble() * rnd.NextDouble() * 22;
        var hours = weekend ? rnd.Next(1, 4) : rnd.Next(5, 11);
        var mask = 0;
        for (var h = 0; h < hours; h++)
            mask |= 1 << (8 + h);
        days.Add(new DailyUsageRecord(date, Math.Round(points, 2), Math.Min(100, Math.Round(points * 4 + rnd.Next(5, 30))), mask, null));
    }

    var heatmapType = asm.GetType("AIQuota.UsageHeatmap")!;
    var heatmapStateType = asm.GetType("AIQuota.HeatmapState")!;
    var paletteType = asm.GetType("AIQuota.FlyoutPalette")!;

    Bitmap RenderHeatmap(bool light, bool greyscale)
    {
        var state = Activator.CreateInstance(heatmapStateType, (IReadOnlyList<DailyUsageRecord>)days, true, Account, greyscale)!;
        var getter = Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(heatmapStateType), new Holder(state),
            typeof(Holder).GetMethod(nameof(Holder.Get))!.MakeGenericMethod(heatmapStateType));
        using var form = (Form)Activator.CreateInstance(heatmapType, getter)!;
        heatmapType.GetField("_palette", PrivInst)!.SetValue(form, paletteType.GetMethod("For")!.Invoke(null, [light]));
        heatmapType.GetField("_usage", PrivInst)!.SetValue(form, Palette(greyscale, light));
        heatmapType.GetField("_scale", PrivInst)!.SetValue(form, 2f);
        heatmapType.GetMethod("ApplyLayout", PrivInst)!.Invoke(form, null);
        // Hover a busy day two weeks ago to show the day's values.
        var hovered = days.Where(d => today.DayNumber - d.Date.DayNumber is >= 14 and < 21 && d.WeekPoints > 10).Select(d => (DateOnly?)d.Date).FirstOrDefault() ?? today;
        heatmapType.GetField("_selected", PrivInst)!.SetValue(form, hovered);

        var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        using (var g = Graphics.FromImage(bmp))
            heatmapType.GetMethod("DrawAll", PrivInst)!.Invoke(form, [g]);
        return bmp;
    }

    foreach (var light in new[] { false, true })
    {
        using var bmp = RenderHeatmap(light, greyscale: false);
        using var framed = Frame(bmp, radius: 16, light: light);
        framed.Save(Path.Combine(outDir, light ? "heatmap_light.png" : "heatmap_dark.png"), ImageFormat.Png);
    }

    // Accessibility mode: dark above light.
    using var dark = Frame(RenderHeatmap(light: false, greyscale: true), radius: 16, light: false);
    using var lightFrame = Frame(RenderHeatmap(light: true, greyscale: true), radius: 16, light: true);
    using var sheet = new Bitmap(Math.Max(dark.Width, lightFrame.Width), dark.Height + lightFrame.Height);
    using (var g = Graphics.FromImage(sheet))
    {
        g.DrawImage(dark, 0, 0);
        g.DrawImage(lightFrame, 0, dark.Height);
    }
    sheet.Save(Path.Combine(outDir, "accessibility_heatmap.png"), ImageFormat.Png);
}

Console.WriteLine("done");

// The folder containing AIQuota.sln, searched upwards from the tool's build output.
static string FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "AIQuota.sln")))
            return dir.FullName;
    }
    throw new InvalidOperationException("Could not find the repository root (AIQuota.sln); pass an output directory.");
}

// Puts an image on a neutral backdrop with rounded corners, a hairline border and a soft
// shadow - roughly how Windows 11 presents menus and flyouts.
static Bitmap Frame(Bitmap content, int radius, bool light)
{
    const int margin = 28;
    var result = new Bitmap(content.Width + 2 * margin, content.Height + 2 * margin);
    using var g = Graphics.FromImage(result);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);

    var rect = new Rectangle(margin, margin, content.Width, content.Height);
    for (var i = 14; i >= 1; i--)
    {
        using var shadowPath = Rounded(new RectangleF(rect.X - i + 2, rect.Y - i + 6, rect.Width + 2 * i - 4, rect.Height + 2 * i - 4), radius + i);
        using var shadow = new SolidBrush(Color.FromArgb(5, 0, 0, 0));
        g.FillPath(shadow, shadowPath);
    }

    using var path = Rounded(rect, radius);
    using var texture = new TextureBrush(content) { WrapMode = WrapMode.Clamp };
    texture.TranslateTransform(rect.X, rect.Y);
    g.FillPath(texture, path);
    using var border = new Pen(light ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(90, 255, 255, 255), 1.5f);
    g.DrawPath(border, path);
    return result;
}

static GraphicsPath Rounded(RectangleF r, float radius)
{
    var path = new GraphicsPath();
    if (radius <= 0) { path.AddRectangle(r); return path; }
    var d = radius * 2;
    path.AddArc(r.X, r.Y, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

sealed class Holder(object state)
{
    public T Get<T>() => (T)state;

    public static void Ignore<T>(T _) { }
}
