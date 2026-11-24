package eu.schicht8.aiquota.core

import kotlinx.coroutines.TimeoutCancellationException
import java.io.IOException
import java.math.BigDecimal
import java.math.RoundingMode
import java.text.DecimalFormat
import java.text.DecimalFormatSymbols
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.util.Locale

enum class AppLanguage { English, German }

/**
 * All user-visible text, in German and English - the texts and formats of the Windows app's
 * AIQuota/Localization/Strings.cs, so both apps read the same. Times are shown in [zone].
 * Numbers and durations are formatted by hand, independent of the device's locale, as on
 * Windows.
 */
class Strings(val language: AppLanguage, val zone: ZoneId = ZoneId.systemDefault()) {
    private fun t(de: String, en: String) = if (language == AppLanguage.German) de else en

    // ------------------------------------------------------------------ app and accounts

    val appTitle get() = t("Claude Nutzung", "Claude Usage")
    val accountUnnamed get() = t("Konto (Name noch unbekannt)", "Account (name not known yet)")
    val addAccount get() = t("Konto hinzufügen", "Add account")
    val noAccounts get() = t(
        "Noch kein Konto. Melde dich mit deinem Claude-Konto an, um seine Nutzung zu sehen.",
        "No account yet. Log in with your Claude account to see its usage.",
    )
    val loginRequired get() = t("Anmeldung erforderlich", "Login required")
    val logIn get() = t("Anmelden", "Log in")
    val logInAgain get() = t("Erneut anmelden", "Log in again")
    val rename get() = t("Umbenennen", "Rename")
    val remove get() = t("Entfernen", "Remove")
    val moreOptions get() = t("Weitere Optionen", "More options")
    val refresh get() = t("Aktualisieren", "Refresh")
    val dialogOk get() = "OK"
    val dialogCancel get() = t("Abbrechen", "Cancel")
    val renameAccountTitle get() = t("Konto umbenennen", "Rename account")
    fun renameAccountPrompt(label: String) = t(
        "Kurzname für $label (z. B. \"Privat\" oder \"Arbeit\"). Leer lassen, um den Namen aus dem Konto zu verwenden.",
        "Short name for $label (e.g. \"Private\" or \"Work\"). Leave empty to use the account's own name.",
    )
    fun confirmRemoveAccount(name: String) = t(
        "Das Konto \"$name\" aus AI Quota entfernen?\n\nSeine Anmeldung wird von diesem Gerät gelöscht. Sein Nutzungsverlauf bleibt erhalten.",
        "Remove the account \"$name\" from AI Quota?\n\nIts sign-in is deleted from this device. Its usage history is kept.",
    )

    // Login
    val addAccountHeading get() = t("Weiteres Claude-Konto hinzufügen", "Add another Claude account")
    fun loginAgainHeading(name: String) = t("Erneut anmelden als $name", "Log in again as $name")
    val addAccountText get() = t(
        "Die Anmeldung verwendet das Konto, mit dem du gerade im Browser bei claude.ai angemeldet bist.\n\nUm ein anderes Konto anzumelden, wechsle vorher auf claude.ai das Konto - oder kopiere den Anmeldelink und öffne ihn in einem privaten Browser-Tab.",
        "The sign-in uses the account you're currently signed in with on claude.ai in your browser.\n\nTo sign in with a different account, switch accounts on claude.ai first - or copy the sign-in link and open it in a private browser tab.",
    )
    val addAccountOpenBrowser get() = t("Im Browser öffnen", "Open in browser")
    val addAccountCopyLink get() = t("Link kopieren", "Copy link")
    val loginInProgress get() = t("Warte auf die Anmeldung im Browser…", "Waiting for the sign-in in the browser…")
    val loginLinkCopied get() = t(
        "Anmeldelink kopiert. Öffne ihn im Browser - danach geht es hier weiter.",
        "Sign-in link copied. Open it in the browser - this continues afterwards.",
    )
    fun loginFailed(message: String) = t("Anmeldung fehlgeschlagen: $message", "Login failed: $message")
    fun loginRejected(error: String) = t("Anmeldung wurde abgelehnt: $error", "Login was declined: $error")
    val loginInvalidResponse get() = t(
        "Ungültige Antwort vom Login (Code oder State fehlt/stimmt nicht überein).",
        "Invalid response from login (missing or mismatched code/state).",
    )
    val loginTimedOut get() = t("Keine Anmeldung innerhalb von 5 Minuten.", "No sign-in within 5 minutes.")
    fun tokenExchangeFailed(status: Int, text: String) =
        t("Token-Austausch fehlgeschlagen ($status): $text", "Token exchange failed ($status): $text")
    val loopbackNoPort get() = t(
        "Konnte keinen lokalen Port für den OAuth-Redirect belegen.",
        "Could not bind a local port for the OAuth redirect.",
    )
    val loopbackSuccessTitle get() = t("Anmeldung erfolgreich", "Login successful")
    val loopbackSuccessBody get() = t("Du kannst jetzt zur App zurückkehren.", "You can return to the app now.")
    val loopbackReturnToApp get() = t("Zurück zu AI Quota", "Back to AI Quota")
    val loopbackFailureTitle get() = t("Anmeldung fehlgeschlagen", "Login failed")
    fun loginAccountAlreadyAdded(name: String) = t(
        "\"$name\" ist bereits in der Kontoliste. Seine Anmeldung wurde erneuert.",
        "\"$name\" is already in the account list. Its sign-in has been renewed.",
    )
    fun loginDifferentAccount(signedIn: String, expected: String) = t(
        "Du hast dich als \"$signedIn\" angemeldet, nicht als \"$expected\". \"$signedIn\" wurde als eigenes Konto hinzugefügt.",
        "You signed in as \"$signedIn\", not as \"$expected\". \"$signedIn\" has been added as a separate account.",
    )
    fun loginDifferentExistingAccount(signedIn: String, expected: String) = t(
        "Du hast dich als \"$signedIn\" angemeldet, nicht als \"$expected\". Die Anmeldung von \"$signedIn\" wurde erneuert; \"$expected\" braucht weiterhin eine neue Anmeldung.",
        "You signed in as \"$signedIn\", not as \"$expected\". The sign-in of \"$signedIn\" has been renewed; \"$expected\" still needs a new login.",
    )

    fun loginError(error: Throwable): String = loginFailed(
        when (error) {
            is LoginError.Rejected -> loginRejected(error.error)
            LoginError.InvalidResponse -> loginInvalidResponse
            is LoginError.TokenExchangeFailed -> tokenExchangeFailed(error.status, error.text)
            LoginError.NoPort -> loopbackNoPort
            is TimeoutCancellationException -> loginTimedOut
            is IOException -> fetchError(networkError(error))
            else -> error.message ?: error.javaClass.simpleName
        },
    )

    /** What [AccountMonitor.addLogin] has to say about where a login went. */
    fun loginMessage(message: LoginMessage): String = when (message) {
        is LoginMessage.AlreadyAdded -> loginAccountAlreadyAdded(message.name ?: accountUnnamed)
        is LoginMessage.DifferentAccount -> loginDifferentAccount(message.signedIn ?: accountUnnamed, message.expected ?: accountUnnamed)
        is LoginMessage.DifferentExistingAccount ->
            loginDifferentExistingAccount(message.signedIn ?: accountUnnamed, message.expected ?: accountUnnamed)
    }

    // ------------------------------------------------------------------ status

    val refreshing get() = t("Wird aktualisiert…", "Refreshing…")
    val pleaseReauth get() = t("Bitte erneut anmelden", "Please log in again")
    fun statusUpdated(time: Instant) = t("Stand: ${time.local().format(HMS)}", "Updated: ${time.local().format(HMS)}")
    fun fetchFailed(error: FetchError) = t("Fehler beim Abrufen (${fetchError(error)})", "Error fetching data (${fetchError(error)})")

    fun fetchError(error: FetchError): String = when (error) {
        FetchError.NoInternet -> t("Keine Internetverbindung", "No internet connection")
        FetchError.Timeout -> t("Zeitüberschreitung", "Timed out")
        FetchError.EmptyResponse -> t("Leere Antwort", "Empty response")
        is FetchError.Http -> "HTTP ${error.status}"
        is FetchError.UnexpectedFormat -> t("Unerwartetes Antwortformat: ${error.message}", "Unexpected response format: ${error.message}")
        is FetchError.Other -> error.message
    }

    // ------------------------------------------------------------------ usage sections

    val sessionTitle get() = t("Sitzung (5h)", "Session (5h)")
    val weekTitle get() = t("Woche", "Week")
    val creditsTitle get() = t("Nutzungsguthaben", "Usage credits")
    val sessionNotStarted get() = t("Nicht gestartet – beginnt mit deiner nächsten Nachricht", "Not started – begins with your next message")

    /** "Resets 14:30 · in 1.5h" - with the weekday ("Resets Monday 09:00 · …") when the reset
     * isn't today. */
    fun resetLine(resetsAt: Instant, now: Instant): String {
        val local = resetsAt.local()
        val time = local.format(HM)
        val day = if (local.toLocalDate() == now.local().toLocalDate()) time else "${weekdayName(local)} $time"
        return t("Reset $day · ${countdown(resetsAt, now)}", "Resets $day · ${countdown(resetsAt, now)}")
    }

    /** " · pace 43%", or empty without a pace. */
    fun paceSuffix(pacePercent: Int?) = pacePercent?.let { t(" · Pace $it%", " · pace $it%") } ?: ""

    fun forecastLimitIn(timeToLimit: Duration) = t(
        "Limit in ~${formatDuration(timeToLimit)} beim aktuellen Verbrauch",
        "Limit in ~${formatDuration(timeToLimit)} at current rate",
    )
    val forecastResetFirst get() = t("Reset kommt beim aktuellen Verbrauch vor dem Limit", "On track to reset first")

    fun creditsAmount(used: BigDecimal, limit: BigDecimal, currency: String) = t(
        "${formatMoney(used)} $currency von ${formatMoney(limit)} $currency",
        "${formatMoney(used)} $currency of ${formatMoney(limit)} $currency",
    )

    /** Name of an additional limit: its window plus what it's scoped to, e.g. "Week · Opus". */
    fun limitLabel(limit: UsageLimitInfo): String {
        val window = when (limit.group.lowercase()) {
            "session" -> sessionTitle
            "weekly" -> weekTitle
            else -> UsageLimitParser.titleCase(limit.group)
        }
        return limit.name?.let { "$window · $it" } ?: window
    }

    fun limitAllowanceLine(sharePercent: Double) = t(
        "Darf bis zu ${sharePercent.roundHalfEven()}% des Wochenlimits nutzen",
        "Can use up to ${sharePercent.roundHalfEven()}% of the weekly limit",
    )

    /** "↺ 1 weekly reset available · use by Sun 23:59 (in 3d4h)". */
    fun limitResetLine(offer: LimitResetOffer, now: Instant): String {
        val name = limitResetName(offer.kind, offer.count)
        var text = t("↺ ${offer.count} $name verfügbar", "↺ ${offer.count} $name available")
        offer.firstEndsAt?.let { end ->
            text += t(
                " · einlösbar bis ${shortDayTime(end, now)} (${countdown(end, now)})",
                " · use by ${shortDayTime(end, now)} (${countdown(end, now)})",
            )
        }
        return text
    }

    private fun limitResetName(kind: LimitResetKind, count: Int): String = when (kind) {
        LimitResetKind.Session -> if (count == 1) t("5-Stunden-Reset", "5-hour reset") else t("5-Stunden-Resets", "5-hour resets")
        LimitResetKind.Weekly -> if (count == 1) t("Wochen-Reset", "weekly reset") else t("Wochen-Resets", "weekly resets")
        LimitResetKind.Model -> if (count == 1) t("Fable-Reset", "Fable reset") else t("Fable-Resets", "Fable resets")
        LimitResetKind.Full -> if (count == 1) t("Komplett-Reset", "full reset") else t("Komplett-Resets", "full resets")
    }

    // ------------------------------------------------------------------ formatting

    private fun Instant.local(): ZonedDateTime = atZone(zone)

    /** "in 1.5h", or "shortly" once the time has come. */
    private fun countdown(at: Instant, now: Instant): String {
        val remaining = Duration.between(now, at)
        return if (remaining.isNegative || remaining.isZero) t("in Kürze", "shortly") else "in ${formatDuration(remaining)}"
    }

    /** "Sun 23:59" for another day, "23:59" for today. */
    private fun shortDayTime(at: Instant, now: Instant): String {
        val local = at.local()
        return if (local.toLocalDate() == now.local().toLocalDate()) local.format(HM) else "${weekdayShort(local)} ${local.format(HM)}"
    }

    private fun weekdayName(local: ZonedDateTime): String =
        (if (language == AppLanguage.German) WEEKDAYS_DE else WEEKDAYS_EN)[local.dayOfWeek.value % 7]

    fun weekdayShort(local: ZonedDateTime): String =
        (if (language == AppLanguage.German) WEEKDAYS_SHORT_DE else WEEKDAYS_SHORT_EN)[local.dayOfWeek.value % 7]

    /** Comma decimal separator in German, dot in English; two decimals, grouped. Midpoints
     * round away from zero, as .NET's format strings do. */
    private fun formatMoney(amount: BigDecimal): String {
        val symbols = DecimalFormatSymbols(Locale.ROOT).apply {
            decimalSeparator = if (language == AppLanguage.German) ',' else '.'
            groupingSeparator = if (language == AppLanguage.German) '.' else ','
        }
        return DecimalFormat("#,##0.00", symbols).apply { roundingMode = RoundingMode.HALF_UP }.format(amount)
    }

    companion object {
        private val HM = DateTimeFormatter.ofPattern("HH:mm")
        private val HMS = DateTimeFormatter.ofPattern("HH:mm:ss")

        // Sunday first, indexed by DayOfWeek.value % 7.
        private val WEEKDAYS_DE = listOf("Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag")
        private val WEEKDAYS_EN = listOf("Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday")
        private val WEEKDAYS_SHORT_DE = listOf("So", "Mo", "Di", "Mi", "Do", "Fr", "Sa")
        private val WEEKDAYS_SHORT_EN = listOf("Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat")

        /** Shortest-possible duration, e.g. "1.5h", "45min" or "4d15h" - as on Windows. */
        fun formatDuration(remaining: Duration): String {
            val totalMinutes = remaining.toMillis() / 60_000.0
            if (remaining.toDays() >= 1) {
                val days = remaining.toDays()
                // Not toHoursPart()/toMinutesPart(): Android has them only from API 31.
                val hours = remaining.toHours() % 24
                return if (hours > 0) "${days}d${hours}h" else "${days}d"
            }
            if (totalMinutes >= 60) {
                val hours = DecimalFormat("0.#", DecimalFormatSymbols(Locale.ROOT)).apply { roundingMode = RoundingMode.HALF_UP }
                return "${hours.format(totalMinutes / 60)}h"
            }
            return "${maxOf(remaining.toMinutes() % 60, 1)}min"
        }
    }
}
