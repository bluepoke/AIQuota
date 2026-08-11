using System.Globalization;

namespace AIQuota.Localization;

/// <summary>
/// All user-visible text, in German and English. Defaults to the Windows UI language
/// (German for de-*, English otherwise) and can be overridden from the tray menu; the
/// choice is remembered in the registry via <see cref="LanguagePreference"/>.
/// </summary>
public static class Strings
{
    public static AppLanguage Current { get; private set; } = LanguagePreference.Load() ?? DetectSystemLanguage();

    public static event Action? LanguageChanged;

    public static void SetLanguage(AppLanguage language)
    {
        if (Current == language)
            return;
        Current = language;
        LanguagePreference.Save(language);
        LanguageChanged?.Invoke();
    }

    private static AppLanguage DetectSystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.German
            : AppLanguage.English;

    private static string T(string de, string en) => Current == AppLanguage.German ? de : en;

    public static string AppTitle => T("Claude Nutzung", "Claude Usage");

    public static string UserLabel(string email) => T($"Angemeldet als {email}", $"Logged in as {email}");

    public static string MenuSessionEmpty => T("Sitzung (5h): –", "Session (5h): –");
    public static string MenuWeeklyEmpty => T("Woche: –", "Week: –");
    public static string MenuNotLoggedIn => T("Nicht angemeldet", "Not logged in");
    public static string MenuLogin => T("Bei claude.ai anmelden…", "Log in with claude.ai…");
    public static string MenuLogout => T("Abmelden", "Log out");
    public static string MenuStartup => T("Bei Windows-Start ausführen", "Run at Windows startup");
    public static string MenuCheckForNewVersion => T("Automatisch nach neuer Version suchen", "Automatically check for new version");
    public static string MenuNewVersionAvailable(string version) => T($"Neue Version verfügbar: v{version}", $"New version available: v{version}");
    public static string MenuRefresh => T("Jetzt aktualisieren", "Refresh now");
    public static string MenuExit => T("Beenden", "Exit");
    public static string MenuLanguage => T("Sprache", "Language");
    public static string MenuLanguageGerman => "Deutsch";
    public static string MenuLanguageEnglish => "English";
    public static string VersionLabel(string version) => T($"Version {version}", $"Version {version}");
    public static string MenuGitHub => T("GitHub-Seite öffnen", "Open GitHub page");

    public static string TooltipNotLoggedIn => T("Claude Nutzung – nicht angemeldet", "Claude Usage – not logged in");
    public static string TooltipLoggingIn => T(
        "Claude Nutzung – Anmeldung läuft (Browser öffnet sich)…",
        "Claude Usage – signing in (browser is opening)…");
    public static string TooltipAuthExpired => T("Claude Nutzung – Anmeldung abgelaufen", "Claude Usage – sign-in expired");

    public static string StatusPromptLogin => T("Rechtsklick > Anmelden", "Right-click > Log in");
    public static string StatusPleaseReauth => T("Bitte erneut anmelden", "Please log in again");

    public static string InstanceAlreadyRunning => T(
        "Claude Nutzung läuft bereits (siehe Systemtray).",
        "Claude Usage is already running (see system tray).");

    public static string LoopbackNoPort => T(
        "Konnte keinen lokalen Port für den OAuth-Redirect belegen.",
        "Could not bind a local port for the OAuth redirect.");
    public static string LoopbackSuccessTitle => T("Anmeldung erfolgreich", "Login successful");
    public static string LoopbackSuccessBody => T(
        "Du kannst dieses Fenster jetzt schliessen und zur Claude Nutzung-App zurueckkehren.",
        "You can close this window now and return to the Claude Usage app.");
    public static string LoopbackFailureTitle => T("Anmeldung fehlgeschlagen", "Login failed");

    public static string LoginRejected(string error) => T($"Anmeldung wurde abgelehnt: {error}", $"Login was declined: {error}");
    public static string LoginInvalidResponse => T(
        "Ungueltige Antwort vom Login (Code oder State fehlt/stimmt nicht ueberein).",
        "Invalid response from login (missing or mismatched code/state).");
    public static string TokenExchangeFailed(int status, string text) =>
        T($"Token-Austausch fehlgeschlagen ({status}): {text}", $"Token exchange failed ({status}): {text}");
    public static string TokenExchangeEmptyResponse => T(
        "Leere Antwort beim Token-Austausch.", "Empty response during token exchange.");
    public static string TokenRefreshEmptyResponse => T(
        "Leere Antwort beim Token-Refresh.", "Empty response during token refresh.");

    public static string UsageEmptyResponse => T("Leere Antwort", "Empty response");
    public static string UsageTimeout => T("Zeitüberschreitung", "Timed out");
    public static string UsageNoInternet => T("Keine Internetverbindung", "No internet connection");
    public static string UsageUnexpectedFormat(string message) =>
        T($"Unerwartetes Antwortformat: {message}", $"Unexpected response format: {message}");

    public static string LoginFailed(string message) => T($"Anmeldung fehlgeschlagen:\n{message}", $"Login failed:\n{message}");
    public static string FetchError(string error) => T($"Fehler beim Abrufen ({error})", $"Error fetching data ({error})");
    public static string StatusUpdated(DateTimeOffset time) => T($"Stand: {time:HH:mm:ss}", $"Updated: {time:HH:mm:ss}");

    public static string TooltipSummary(int sessionPercent, DateTimeOffset? sessionResetsAt, int weeklyPercent, DateTimeOffset? weeklyResetsAt, DateTimeOffset fetchedAt)
    {
        var sessionRemaining = FormatCompactRemaining(sessionResetsAt);
        var weeklyRemaining = FormatCompactRemaining(weeklyResetsAt);
        var time = fetchedAt.ToLocalTime().ToString("HH:mm");
        return T(
            $"Claude Nutzung ({time})\nSitzung (5h): {sessionPercent}%{sessionRemaining}\nWoche: {weeklyPercent}%{weeklyRemaining}",
            $"Claude Usage ({time})\nSession (5h): {sessionPercent}%{sessionRemaining}\nWeek: {weeklyPercent}%{weeklyRemaining}");
    }

    /// <summary>Shortest-possible remaining time, e.g. " (1.5h)" or " (2d3h)", for the tooltip.</summary>
    private static string FormatCompactRemaining(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } r)
            return "";

        var remaining = r - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
            return "";

        return $" ({FormatDuration(remaining)})";
    }

    /// <summary>Shortest-possible duration, e.g. "1.5h", "45min", or "4d15h" - shared by the tooltip and the context menu.</summary>
    private static string FormatDuration(TimeSpan remaining)
    {
        if (remaining.TotalDays >= 1)
        {
            var days = (int)remaining.TotalDays;
            var hours = remaining.Hours;
            return hours > 0 ? $"{days}d{hours}h" : $"{days}d";
        }

        if (remaining.TotalHours >= 1)
            return $"{remaining.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)}h";

        return $"{Math.Max(remaining.Minutes, 1)}min";
    }

    private static readonly string[] WeekdaysDe = ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"];
    private static readonly string[] WeekdaysEn = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    private static string WeekdayName(DateTimeOffset local) =>
        (Current == AppLanguage.German ? WeekdaysDe : WeekdaysEn)[(int)local.DayOfWeek];

    public static string SessionLabel(int percent, string resetSuffix) =>
        T($"Sitzung (5h): {percent}%{resetSuffix}", $"Session (5h): {percent}%{resetSuffix}");
    public static string WeeklyLabel(int percent, string resetSuffix) =>
        T($"Woche: {percent}%{resetSuffix}", $"Week: {percent}%{resetSuffix}");

    public static string FormatReset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } r)
            return "";

        var remaining = r - DateTimeOffset.Now;
        var localTime = r.ToLocalTime();
        var until = T("bis", "until");
        var weekday = WeekdayName(localTime);
        var countdown = remaining <= TimeSpan.Zero
            ? T("in Kürze", "shortly")
            : $"in {FormatDuration(remaining)}";

        return $" ({until} {weekday} {localTime:HH:mm}, {countdown})";
    }

    public static string BalloonSessionWarning(int percent) => T($"Sitzungslimit (5h) bei {percent}%.", $"Session limit (5h) at {percent}%.");
    public static string BalloonWeeklyWarning(int percent) => T($"Wochenlimit bei {percent}%.", $"Weekly limit at {percent}%.");
    public static string BalloonNewVersionAvailable(string version) => T(
        $"Eine neue Version (v{version}) ist verfügbar. Klicke hier, um die Release-Seite zu öffnen.",
        $"A new version (v{version}) is available. Click here to open the release page.");
}
