using AIQuota.Localization;

namespace AIQuota;

static class Program
{
    /// <summary>Passed by <see cref="SelfUpdater"/>'s helper script when relaunching after a
    /// self-update, so the new instance waits for the old one's single-instance mutex to
    /// free up instead of immediately reporting "already running".</summary>
    public const string PostUpdateRelaunchArgument = "--post-update";

    /// <summary>
    /// The main entry point for the application. Tray-only, no visible window.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        var isPostUpdateRelaunch = args.Contains(PostUpdateRelaunchArgument);

        using var singleInstanceGuard = new Mutex(initiallyOwned: false, "AIQuota_SingleInstance");
        bool acquired;
        try
        {
            acquired = singleInstanceGuard.WaitOne(isPostUpdateRelaunch ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            MessageBox.Show(
                Strings.InstanceAlreadyRunning,
                Strings.AppTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Application.ThreadException += (_, e) => ShowUnexpectedError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowUnexpectedError(e.ExceptionObject as Exception);

            ApplicationConfiguration.Initialize();
            Application.Run(new UsageTrayContext());
        }
        finally
        {
            singleInstanceGuard.ReleaseMutex();
        }
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
