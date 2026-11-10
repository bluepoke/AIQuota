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
    public static string MenuShowSessionRing => T("Sitzungs-Countdown-Ring anzeigen", "Show session countdown ring");
    public static string MenuShowWeeklyPace => T("Pace-Markierungen anzeigen", "Show pace markers");
    public static string MenuAccessibilityMode => T("Barrierefreiheitsmodus (kontrastreiche Graustufen)", "Accessibility mode (high-contrast greyscale)");
    public static string MenuAccessibilityModeForced => T(
        "Barrierefreiheitsmodus (aktiv, solange ein Windows-Kontrastdesign an ist)",
        "Accessibility mode (on while a Windows contrast theme is active)");
    public static string MenuEnableStreamDock => T("Auf Stream-Dock-Controller anzeigen", "Show on Stream Dock controller");
    public static string MenuNewVersionAvailable(string version) => T($"Neue Version verfügbar: v{version}", $"New version available: v{version}");
    public static string MenuRefresh => T("Jetzt aktualisieren", "Refresh now");
    public static string MenuExit => T("Beenden", "Exit");
    public static string MenuLanguage => T("Sprache", "Language");
    public static string MenuLanguageGerman => "Deutsch";
    public static string MenuLanguageEnglish => "English";
    public static string VersionLabel(string version) => T($"AIQuota Version {version}", $"AIQuota Version {version}");
    public static string MenuGitHub => T("GitHub-Seite öffnen", "Open GitHub page");
    public static string MenuSettings => T("Einstellungen", "Settings");
    public static string MenuHistory => T("Nutzungsverlauf", "Usage history");
    public static string MenuHistoryExport => T("Als CSV exportieren…", "Export as CSV…");
    public static string MenuHistoryClear => T("Verlauf löschen…", "Clear history…");

    public static string HistoryExportDialogTitle => T("Nutzungsverlauf exportieren", "Export usage history");
    public static string HistoryCsvFilter => T("CSV-Dateien (*.csv)|*.csv", "CSV files (*.csv)|*.csv");
    public static string HistoryEmpty => T(
        "Für dieses Konto wurde noch kein Nutzungsverlauf aufgezeichnet.",
        "No usage history has been recorded for this account yet.");
    public static string HistoryExportFailed(string message) => T($"Export fehlgeschlagen:\n{message}", $"Export failed:\n{message}");
    public static string ConfirmClearHistory => T(
        "Den gespeicherten Nutzungsverlauf aller Konten auf diesem PC löschen?\n\nDie Prognose startet danach ohne Daten neu.",
        "Delete the stored usage history of all accounts on this PC?\n\nThe forecast starts over without data afterwards.");

    /// <summary>The burn-rate forecast, as shown in the tray flyout.</summary>
    public static string ForecastLimitInText(TimeSpan timeToLimit) => T(
        $"Limit in ~{FormatDuration(timeToLimit)} beim aktuellen Verbrauch",
        $"Limit in ~{FormatDuration(timeToLimit)} at current rate");
    public static string ForecastResetFirstText => T(
        "Reset kommt beim aktuellen Verbrauch vor dem Limit",
        "On track to reset first");

    /// <summary>Context menu line under the session/week entry: the burn-rate forecast.</summary>
    public static string ForecastLimitIn(TimeSpan timeToLimit) => "    ↳ " + ForecastLimitInText(timeToLimit);
    public static string ForecastResetFirst => "    ↳ " + ForecastResetFirstText;

    // Tray flyout (left-click on the tray icon).
    public static string FlyoutSessionTitle => T("Sitzung (5h)", "Session (5h)");
    public static string FlyoutWeekTitle => T("Woche", "Week");
    public static string FlyoutCreditsTitle => T("Nutzungsguthaben", "Usage credits");
    public static string FlyoutHistoryTitle => T("Letzte 7 Tage", "Last 7 days");
    public static string FlyoutLegendUsage => T("Woche", "Week");
    public static string FlyoutLegendPace => T("Pace", "Even pace");
    /// <summary>Shown instead of the flyout's 7-day chart until there's enough history: at
    /// least <paramref name="minPoints"/> data points, the first and last at least <paramref
    /// name="minSpan"/> apart - with an estimate of how long that takes while the app keeps
    /// running (<paramref name="remaining"/>; zero or less means the next refresh will do).</summary>
    public static string FlyoutCollectingData(int minPoints, TimeSpan minSpan, TimeSpan remaining)
    {
        var eta = remaining > TimeSpan.Zero
            ? T($"Noch ca. {FormatDuration(remaining)}, solange die App läuft.", $"About {FormatDuration(remaining)} to go while the app keeps running.")
            : T("Mit dem nächsten Update.", "With the next refresh.");
        return T(
            $"Sammle Daten… Das Diagramm erscheint ab {minPoints} Datenpunkten mit insgesamt mindestens {FormatDuration(minSpan)} Abstand. {eta}",
            $"Collecting data… The chart appears once there are at least {minPoints} data points spanning at least {FormatDuration(minSpan)} in total. {eta}");
    }
    public static string FlyoutRefreshing => T("Wird aktualisiert…", "Refreshing…");
    public static string FlyoutSessionNotStarted => T(
        "Nicht gestartet – beginnt mit deiner nächsten Nachricht",
        "Not started – begins with your next message");

    /// <summary>"Resets 14:30 · in 1.5h" - with the weekday ("Resets Monday 09:00 · …") when
    /// the reset isn't today.</summary>
    public static string FlyoutResetLine(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var local = resetsAt.ToLocalTime();
        var when = local.Date == now.ToLocalTime().Date
            ? $"{local:HH:mm}"
            : $"{WeekdayName(local)} {local:HH:mm}";
        var remaining = resetsAt - now;
        var countdown = remaining <= TimeSpan.Zero ? T("in Kürze", "shortly") : $"in {FormatDuration(remaining)}";
        return T($"Reset {when} · {countdown}", $"Resets {when} · {countdown}");
    }

    public static string CreditsAmount(decimal used, decimal limit, string currency) =>
        T($"{FormatMoney(used)} {currency} von {FormatMoney(limit)} {currency}",
          $"{FormatMoney(used)} {currency} of {FormatMoney(limit)} {currency}");

    private static readonly string[] WeekdaysShortDe = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];
    private static readonly string[] WeekdaysShortEn = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public static string WeekdayShort(DateTimeOffset local) =>
        (Current == AppLanguage.German ? WeekdaysShortDe : WeekdaysShortEn)[(int)local.DayOfWeek];

    /// <summary>Hover label in the flyout's 7-day chart, e.g. "Mon 14:30 · 62%".</summary>
    public static string FlyoutChartPoint(DateTimeOffset time, double percent)
    {
        var local = time.ToLocalTime();
        return $"{WeekdayShort(local)} {local:HH:mm} · {Math.Round(percent):0}%";
    }

    /// <summary>Appended to the session/week line in the tooltip when the limit is forecast to
    /// be reached before the reset (e.g. " · limit ~40min"); empty otherwise.</summary>
    public static string ForecastTooltipSuffix(TimeSpan? timeToLimit) =>
        timeToLimit is { } t ? T($" · Limit ~{FormatDuration(t)}", $" · limit ~{FormatDuration(t)}") : "";
    public static string MenuAbout => T("Über", "About");

    public static string TooltipNotLoggedIn => T("Claude Nutzung – nicht angemeldet", "Claude Usage – not logged in");
    public static string TooltipLoggingIn => T(
        "Claude Nutzung – Anmeldung läuft (Browser öffnet sich)…",
        "Claude Usage – signing in (browser is opening)…");
    public static string TooltipAuthExpired => T("Claude Nutzung – Anmeldung abgelaufen", "Claude Usage – sign-in expired");
    public static string TooltipUpdating => T("Claude Nutzung – Update wird installiert…", "Claude Usage – installing update…");

    public static string ConfirmUpdatePrompt(string version) => T(
        $"Version {version} ist verfügbar. Jetzt herunterladen, installieren und die App neu starten?",
        $"Version {version} is available. Download, install it, and restart the app now?");
    public static string ConfirmUpdateYes => T("Installieren", "Install");
    public static string ConfirmUpdateNo => T("Abbrechen", "Cancel");
    public static string ChangelogHeader(string previousVersion) => T(
        $"Änderungen seit Version {previousVersion}:", $"Changes since version {previousVersion}:");
    public static string NoChangelogAvailable => T("Keine Änderungsliste verfügbar.", "No changelog available.");
    public static string UpdateFailed(string message) => T($"Update fehlgeschlagen:\n{message}", $"Update failed:\n{message}");

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

    /// <param name="weeklyPacePercent">Where even usage over the week would be by now - appended
    /// to the week line as <see cref="PaceSuffix"/> when set.</param>
    /// <param name="sessionLimitIn">Forecast time until the session limit - appended to the
    /// session line as <see cref="ForecastTooltipSuffix"/> when set.</param>
    /// <param name="weeklyLimitIn">Same for the week line.</param>
    public static string TooltipSummary(int sessionPercent, DateTimeOffset? sessionResetsAt, int weeklyPercent, DateTimeOffset? weeklyResetsAt, DateTimeOffset fetchedAt, int? creditPercent = null, int? weeklyPacePercent = null, TimeSpan? sessionLimitIn = null, TimeSpan? weeklyLimitIn = null)
    {
        var sessionRemaining = (sessionResetsAt is null ? SessionNotStartedSuffix : FormatCompactRemaining(sessionResetsAt))
            + ForecastTooltipSuffix(sessionLimitIn);
        var weeklyRemaining = FormatCompactRemaining(weeklyResetsAt) + PaceSuffix(weeklyPacePercent)
            + ForecastTooltipSuffix(weeklyLimitIn);
        var time = fetchedAt.ToLocalTime().ToString("HH:mm");
        var creditsLine = creditPercent is { } cp ? T($"\nGuthaben: {cp}%", $"\nCredits: {cp}%") : "";
        return T(
            $"Claude Nutzung ({time})\nSitzung (5h): {sessionPercent}%{sessionRemaining}\nWoche: {weeklyPercent}%{weeklyRemaining}{creditsLine}",
            $"Claude Usage ({time})\nSession (5h): {sessionPercent}%{sessionRemaining}\nWeek: {weeklyPercent}%{weeklyRemaining}{creditsLine}");
    }

    public static string TooltipUpdateAvailable(string version) => T($"Update verfügbar: Version {version}", $"Update available: Version {version}");

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

    /// <summary>Appended to the session percentage, in the tooltip and the context menu, while
    /// no 5-hour session is running.</summary>
    public static string SessionNotStartedSuffix => T(" (nicht gestartet)", " (not started)");

    public static string SessionLabel(int percent, string resetSuffix) =>
        T($"Sitzung (5h): {percent}%{resetSuffix}", $"Session (5h): {percent}%{resetSuffix}");
    public static string WeeklyLabel(int percent, string resetSuffix) =>
        T($"Woche: {percent}%{resetSuffix}", $"Week: {percent}%{resetSuffix}");

    /// <summary>Appended to a usage line with its pace (e.g. " · pace 43%"): the week in the
    /// tooltip, context menu and flyout, the session in the flyout. Empty when there's no
    /// pace to show.</summary>
    public static string PaceSuffix(int? pacePercent) =>
        pacePercent is { } p ? T($" · Pace {p}%", $" · pace {p}%") : "";

    // Built by hand rather than via CultureInfo.GetCultureInfo("de-DE") because the app runs
    // with <InvariantGlobalization>true</InvariantGlobalization>, which only supports the
    // invariant culture - loading any named culture throws CultureNotFoundException at runtime.
    private static readonly NumberFormatInfo GermanNumberFormat = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
    };

    /// <summary>Formats a monetary amount with a comma decimal separator in German and a
    /// dot in English, independent of the running system's culture (see <see cref="FormatDuration"/>
    /// for the same rationale).</summary>
    private static string FormatMoney(decimal amount) =>
        amount.ToString("N2", Current == AppLanguage.German ? GermanNumberFormat : CultureInfo.InvariantCulture);

    public static string CreditsLabel(decimal used, decimal limit, string currency) =>
        T($"Nutzungsguthaben: {FormatMoney(used)} {currency} von {FormatMoney(limit)} {currency}",
          $"Usage credits: {FormatMoney(used)} {currency} of {FormatMoney(limit)} {currency}");

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

    // Notifications (toasts, or balloon tips as the fallback).

    /// <summary>"Session usage (5h) at 92% · resets 14:30".</summary>
    public static string NotificationSessionWarning(int percent, DateTimeOffset? resetsAt) =>
        T($"Sitzungsnutzung (5h) bei {percent}%", $"Session usage (5h) at {percent}%") + NotificationResetSuffix(resetsAt);

    /// <summary>"Weekly usage at 90% · resets Mon 09:00".</summary>
    public static string NotificationWeeklyWarning(int percent, DateTimeOffset? resetsAt) =>
        T($"Wochennutzung bei {percent}%", $"Weekly usage at {percent}%") + NotificationResetSuffix(resetsAt);

    public static string NotificationSessionForecast(TimeSpan timeToLimit) => T(
        $"Sitzungslimit (5h) in ~{FormatDuration(timeToLimit)} beim aktuellen Verbrauch",
        $"Session limit (5h) in ~{FormatDuration(timeToLimit)} at current rate");

    public static string NotificationNewVersion(string version) => T(
        $"AI Quota v{version} ist verfügbar",
        $"AI Quota v{version} is available");

    /// <summary>Balloon tips have no buttons, so the text says what a click does.</summary>
    public static string NotificationNewVersionBalloon(string version) => T(
        $"AI Quota v{version} ist verfügbar. Klicke hier, um es zu installieren.",
        $"AI Quota v{version} is available. Click here to install it.");

    /// <summary>" · resets 14:30" for today, " · resets Mon 09:00" otherwise.</summary>
    private static string NotificationResetSuffix(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } reset)
            return "";
        var local = reset.ToLocalTime();
        var when = local.Date == DateTime.Now.Date ? $"{local:HH:mm}" : $"{WeekdayShort(local)} {local:HH:mm}";
        return T($" · Reset {when}", $" · resets {when}");
    }

    public static string ToastNewVersionTitle => T("Update verfügbar", "Update available");
    public static string ToastOpenUsagePage => T("Nutzungsseite öffnen", "Open usage page");
    public static string ToastDismiss => T("Schließen", "Dismiss");
    public static string ToastInstall => T("Installieren…", "Install…");
    public static string ToastLater => T("Später", "Later");

    public static string MenuRemoveNotificationRegistration => T(
        "Benachrichtigungs-Registrierung entfernen und beenden…",
        "Remove notification registration and exit…");
    public static string ConfirmRemoveNotificationRegistration => T(
        "Entfernt die Registrierung, die AI Quota für Windows-Benachrichtigungen angelegt hat (Registry-Einträge unter HKCU\\Software\\Classes und eine Kopie des Symbols), und löscht seine Benachrichtigungen aus dem Benachrichtigungscenter.\n\nAI Quota wird danach beendet. Gedacht für vor dem Löschen der App - beim nächsten Start wird die Registrierung wieder angelegt.\n\nFortfahren?",
        "Removes the registration AI Quota created for Windows notifications (registry entries under HKCU\\Software\\Classes and a copy of its icon) and clears its notifications from the notification center.\n\nAI Quota then exits. Meant for before deleting the app - the next start creates the registration again.\n\nContinue?");
}
