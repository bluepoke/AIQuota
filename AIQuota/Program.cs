using AIQuota.Localization;

namespace AIQuota;

static class Program
{
    /// <summary>
    /// The main entry point for the application. Tray-only, no visible window.
    /// </summary>
    [STAThread]
    static void Main()
    {
        using var singleInstanceGuard = new Mutex(initiallyOwned: true, "AIQuota_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show(
                Strings.InstanceAlreadyRunning,
                Strings.AppTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.ThreadException += (_, e) => ShowUnexpectedError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowUnexpectedError(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new UsageTrayContext());
    }

    /// <summary>
    /// Last-resort safety net for any exception that escapes a call site's own handling
    /// (e.g. a network failure - DNS lookup, no internet - not already caught closer to
    /// the call). Shows a localized message instead of the default .NET crash dialog.
    /// </summary>
    private static void ShowUnexpectedError(Exception? exception)
    {
        var message = IsNetworkError(exception) ? Strings.UsageNoInternet : exception?.Message ?? Strings.UsageEmptyResponse;
        MessageBox.Show(
            Strings.FetchError(message),
            Strings.AppTitle,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static bool IsNetworkError(Exception? exception) =>
        exception is System.Net.Sockets.SocketException ||
        exception is HttpRequestException { InnerException: System.Net.Sockets.SocketException } ||
        exception?.InnerException is System.Net.Sockets.SocketException;
}
